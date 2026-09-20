using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tenet.Kernel;
using Tenet.Olean;

namespace LeanViz;

/// <summary>
/// Reads a built Lean library straight from its <c>.olean</c> files and writes the static bundle the page reads.
/// No Lean process runs and no proof is checked: extracting which constants a declaration references needs only
/// the reader, which is why all of Mathlib takes about two minutes rather than the eleven a re-check costs.
/// <see href="../docs/format.md">docs/format.md</see> specifies what comes out; <c>tests/check_bundle.py</c>
/// enforces it.
///
/// Three passes over the library, in this order because each needs the one before it:
/// <list type="number">
///   <item>Map every module and assign ids: dense, in dependency order, contiguous per module.</item>
///   <item>Decode each declaration for its kind and the constants it references. Modules run in parallel; a
///     module's decoding touches only its own mapping.</item>
///   <item>Build the reverse edges and the axiom closure over the whole graph at once (<see cref="Graph"/>),
///     then write one shard per module, which needs the statements and so decodes a second time.</item>
/// </list>
/// The second decode is deliberate: holding 792,459 decoded constants would cost more memory than re-reading
/// them costs time, and <see cref="OleanModule.TrimCaches"/> between modules keeps the peak flat.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: leanviz <project or build tree> --out <dir> [--slug NAME] [--title TEXT] [--jobs N] [--in-edges N]

          <project>   a Lake project directory (its .lake/build/lib/lean is read, its imports found from there),
                      or any directory of .olean files
          --out       where the bundles go (default: site/data); this one lands in <out>/<slug>
          --slug      url-safe name for this bundle (default: the project directory's name)
          --title     how the page names it (default: the slug)
          --jobs      parallel modules (default: processor count)
          --in-edges  how many dependents a shard keeps per declaration, the most used first (default 200; the count is always kept)
          --check     a JSON report written by `tenet check --report`; the bundle then says what was re-checked,
                      every declaration the checker rejected carries its message, and the report itself is
                      copied into the bundle as check.json with its SHA-256 in the manifest, so a signed
                      attestation of the report can be matched to the page that cites it
          --repo URL  the repository that generated this bundle, for the page's "verify this" link
        """;

    private static int Main(string[] args)
    {
        string? target = null, outDir = "site/data", checkReport = null, repo = null, slug = null, title = null;
        int jobs = System.Environment.ProcessorCount, inEdgeCap = 200;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--in-edges": inEdgeCap = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--check": checkReport = args[++i]; break;
                case "--repo": repo = args[++i]; break;
                case "--slug": slug = args[++i]; break;
                case "--title": title = args[++i]; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (args[i].StartsWith('-') || target is not null)
                    {
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    target = args[i];
                    break;
            }
        }
        if (target is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        CheckStamp? check = checkReport is null ? null : CheckStamp.Read(checkReport);
        if (check is not null)
        {
            Console.WriteLine($"check report: Tenet {check.Tenet}, Lean {check.Lean}, {check.Checked:N0} checked, {check.Failed} failed, {check.Failures.Count} named");
        }
        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine($"{target}: no such directory. Point this at a built Lake project or a directory of .olean files.");
            return 2;
        }
        // Each bundle lives in its own directory under --out, with a projects.json beside them listing what is
        // there, so one site can serve several libraries and the page can offer a switch between them.
        slug ??= Slugify(Path.GetFileName(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar)));
        title ??= slug;
        string siteDir = outDir;
        outDir = Path.Combine(outDir, slug);

        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        OleanChecker checker;
        List<Name> own;
        try
        {
            (checker, own) = Open(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OleanFormatException)
        {
            Console.Error.WriteLine($"{target}: {ex.Message}");
            return 1;
        }
        using (checker)
        {
            List<Name> order = checker.DependencyOrder(own);
            var modules = order.Select(m => checker.Modules[m]).ToList();
            string leanVersion = modules[0].LeanVersion;
            Console.WriteLine($"{modules.Count} modules mapped ({own.Count} in the target, the rest imported), Lean {leanVersion}, {sw.Elapsed.TotalSeconds:F1}s");

            // Ids: dense, in dependency order, contiguous per module. Every declaration of every module gets one,
            // including a name two modules both declare, which happens and matters: this project has an
            // `Euler.euler_breakdown_R3` that is a challenge stub containing `sorry` and another that is the real
            // proof, in modules that are never imported together. Keeping only the first attributed one's verdict
            // to the other, which is the worst kind of wrong: confident.
            sw.Restart();
            var names = new List<Name>();
            var idsOf = new Dictionary<Name, List<int>>();
            var moduleStart = new int[modules.Count + 1];
            var moduleOfId = new List<int>();
            for (int mi = 0; mi < modules.Count; mi++)
            {
                moduleStart[mi] = names.Count;
                foreach (Name cn in modules[mi].ConstantNames)
                {
                    if (!idsOf.TryGetValue(cn, out List<int>? ids))
                    {
                        idsOf[cn] = ids = new List<int>(1);
                    }
                    ids.Add(names.Count);
                    names.Add(cn);
                    moduleOfId.Add(mi);
                }
            }
            moduleStart[modules.Count] = names.Count;
            int n = names.Count;
            int duplicated = idsOf.Count(kv => kv.Value.Count > 1);
            if (duplicated > 0)
            {
                Console.WriteLine($"{duplicated:N0} names are declared by more than one module; "
                                + "each reference is resolved to the one that module can see");
            }

            // Which modules a module can see, for the rare reference whose name is ambiguous. Lean would refuse an
            // environment where two modules declaring the same name are imported together, so within one module's
            // import closure exactly one of them exists, which makes this a lookup rather than a guess.
            var closureOf = new Dictionary<int, HashSet<int>>();
            var indexOfModule = new Dictionary<Name, int>();
            for (int mi = 0; mi < modules.Count; mi++)
            {
                indexOfModule[order[mi]] = mi;
            }

            HashSet<int> ClosureOf(int mi)
            {
                lock (closureOf)
                {
                    if (closureOf.TryGetValue(mi, out HashSet<int>? had))
                    {
                        return had;
                    }
                }
                var seen = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(mi);
                while (stack.Count > 0)
                {
                    int at = stack.Pop();
                    if (!seen.Add(at))
                    {
                        continue;
                    }
                    foreach (Import imp in modules[at].Imports)
                    {
                        if (indexOfModule.TryGetValue(imp.Module, out int next))
                        {
                            stack.Push(next);
                        }
                    }
                }
                lock (closureOf)
                {
                    closureOf[mi] = seen;
                }
                return seen;
            }

            /// The id a reference from this module means: the only one, or the one this module can see.
            int Resolve(List<int> candidates, int fromModule)
            {
                if (candidates.Count == 1)
                {
                    return candidates[0];
                }
                HashSet<int> visible = ClosureOf(fromModule);
                foreach (int id in candidates)
                {
                    if (visible.Contains(moduleOfId[id]))
                    {
                        return id;
                    }
                }
                return candidates[0];
            }

            // Pass 1: kinds and references. Decoding a module touches only its own mapping, so modules go in parallel.
            var outEdges = new int[n][];
            var typeEdges = new int[n][]; // the subset referenced from the statement alone
            var kinds = new byte[n];
            var isAxiom = new bool[n];
            long unresolved = 0, decodeFailures = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = jobs };
            Parallel.For(0, modules.Count, options, mi =>
            {
                OleanModule om = modules[mi];
                for (int id = moduleStart[mi]; id < moduleStart[mi + 1]; id++)
                {
                    ConstantInfo? ci = om.FindConstant(names[id]);
                    if (ci is null)
                    {
                        Interlocked.Increment(ref decodeFailures);
                        outEdges[id] = [];
                        typeEdges[id] = [];
                        continue;
                    }
                    kinds[id] = KindCode(ci);
                    isAxiom[id] = ci is AxiomInfo;
                    var list = new List<int>();
                    foreach (Name u in Replay.UsedConstants(ci))
                    {
                        if (idsOf.TryGetValue(u, out List<int>? cands))
                        {
                            int t = Resolve(cands, mi);
                            if (t != id)
                            {
                                list.Add(t);
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref unresolved);
                        }
                    }
                    list.Sort();
                    outEdges[id] = list.ToArray();
                    var inType = new SortedSet<int>();
                    ExprOps.ForEach(ci.Type, (e, _) =>
                    {
                        if (e is ConstExpr k && idsOf.TryGetValue(k.Name, out List<int>? cands))
                        {
                            int t = Resolve(cands, mi);
                            if (t != id)
                            {
                                inType.Add(t);
                            }
                        }
                        return true;
                    });
                    typeEdges[id] = inType.ToArray();
                }
                om.TrimCaches();
            });
            Console.WriteLine($"{n:N0} declarations read, {outEdges.Sum(o => (long)o.Length):N0} references, {unresolved:N0} to names no module stores, {decodeFailures} not decodable, {sw.Elapsed.TotalSeconds:F1}s");

            sw.Restart();
            Graph g = Graph.Build(outEdges, isAxiom);
            Console.WriteLine($"reverse references and axiom closure ({g.AxiomIds.Length} axioms) in {sw.Elapsed.TotalSeconds:F1}s");

            // Pass 2: statements, docstrings, ranges; one shard per module.
            sw.Restart();
            Directory.CreateDirectory(Path.Combine(outDir, "m"));
            var pretty = new Pretty(checker.Resolve);
            long statementChars = 0, docChars = 0, withDoc = 0, withRange = 0, withDeprecation = 0;
            long bodyChars = 0, withBody = 0, bodiesCut = 0, withFields = 0, withMarks = 0, withHead = 0;
            int done = 0;
            Parallel.For(0, modules.Count, options, mi =>
            {
                OleanModule om = modules[mi];
                // `protected` lives in an extension rather than on the constant, so it is read once per module.
                // `noncomputable` has an extension too and the reader decodes no keys from it, so it is not
                // shown rather than guessed at: a page that silently omits a mark is better than one that
                // silently invents its absence.
                var isProtected = new HashSet<Name>();
                try
                {
                    foreach (Name k in om.KeysInExtension(Name.Parse("Lean.protectedExt")))
                    {
                        isProtected.Add(k);
                    }
                }
                catch (OleanFormatException)
                {
                    // a module without its .server part cannot answer; nothing is marked rather than wrongly marked
                }
                string path = Path.Combine(outDir, "m", order[mi].ToString() + ".json.gz");
                using var fs = Compressed(path);
                using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                w.WriteStartArray();
                long sc = 0, dc = 0, wd = 0, wr = 0, wdep = 0, bc = 0, wb = 0, bcut = 0, wf = 0, wm = 0, wch = 0;
                for (int id = moduleStart[mi]; id < moduleStart[mi + 1]; id++)
                {
                    ConstantInfo? ci = om.FindConstant(names[id]);
                    w.WriteStartObject();
                    w.WriteNumber("i", id);
                    w.WriteString("n", names[id].ToString());
                    w.WriteString("k", KindName(kinds[id]));
                    if (ci is not null)
                    {
                        string s = pretty.Statement(ci);
                        sc += s.Length;
                        w.WriteString("s", s);
                        // The same statement split the way a paper states a theorem, so the page can lay it out
                        // rather than making a reader parse one long line: setting, hypotheses, claim.
                        Pretty.Shape shape = pretty.ShapeOf(ci);
                        if (shape.Setting.Length > 0)
                        {
                            w.WriteStartArray("sg");
                            foreach (string x in shape.Setting)
                            {
                                w.WriteStringValue(x);
                            }
                            w.WriteEndArray();
                        }
                        if (shape.Hypotheses.Length > 0)
                        {
                            w.WriteStartArray("sh");
                            foreach (string x in shape.Hypotheses)
                            {
                                w.WriteStringValue(x);
                            }
                            w.WriteEndArray();
                        }
                        w.WriteString("sc", shape.Conclusion);
                        // How a definition is defined, not merely what type it has. Only defs and opaques carry
                        // one: a theorem's value is its proof term, which is machine output and would cost more
                        // than the whole rest of the bundle. See Pretty.Body.
                        string? body = pretty.Body(ci);
                        if (body is not null)
                        {
                            bc += body.Length;
                            wb++;
                            w.WriteString("v", body);
                            if (Pretty.WasCut(body))
                            {
                                // Say so rather than leaving the page to infer it from a trailing glyph.
                                w.WriteBoolean("vcut", true);
                                bcut++;
                            }
                        }
                        // What a structure or class is made of. Lean stores no field list: a structure is an
                        // inductive with one constructor, and the fields are that constructor's telescope past
                        // the type's own parameters.
                        // What the conclusion is about, as an id. Instances, "what else concludes this" and a
                        // conclusion filter in search all fall out of this one number.
                        if (Pretty.ConclusionHead(ci) is Name head && idsOf.TryGetValue(head, out List<int>? hids))
                        {
                            int hid = Resolve(hids, mi);
                            if (hid >= 0 && hid != id)
                            {
                                w.WriteNumber("ch", hid);
                                wch++;
                            }
                        }
                        var fields = pretty.FieldsOf(ci, checker.Resolve);
                        if (fields.Length > 0)
                        {
                            w.WriteStartArray("fd");
                            foreach ((string fname, string ftype) in fields)
                            {
                                w.WriteStartObject();
                                w.WriteString("n", fname);
                                w.WriteString("t", ftype);
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                            wf++;
                        }
                        if (ci is InductiveInfo ind2 && ind2.Ctors.Length > 0)
                        {
                            w.WriteStartArray("ct");
                            foreach (Name ctor in ind2.Ctors)
                            {
                                w.WriteStringValue(ctor.ToString());
                            }
                            w.WriteEndArray();
                        }
                        // Modifiers a reader acts on: unsafe and partial say the kernel did not check this the
                        // way it checked everything else, private and protected say how the name resolves.
                        var marks = new List<string>();
                        if (ci is DefinitionInfo di && di.Safety == DefinitionSafety.Partial)
                        {
                            marks.Add("partial");
                        }
                        else if (ci.IsUnsafe)
                        {
                            marks.Add("unsafe");
                        }
                        if (names[id].ToString().StartsWith("_private.", StringComparison.Ordinal))
                        {
                            marks.Add("private");
                        }
                        if (isProtected.Contains(names[id]))
                        {
                            marks.Add("protected");
                        }
                        if (marks.Count > 0)
                        {
                            w.WriteStartArray("md");
                            foreach (string m2 in marks)
                            {
                                w.WriteStringValue(m2);
                            }
                            w.WriteEndArray();
                            wm++;
                        }
                    }
                    string? doc = null;
                    SourceRange? range = null;
                    Deprecation? deprecated = null;
                    try
                    {
                        doc = om.DocStringOf(names[id]);
                        range = om.SourceRangeOf(names[id]);
                        deprecated = om.DeprecationOf(names[id]);
                    }
                    catch (OleanFormatException)
                    {
                        // a module without its .server part cannot answer; the page shows the declaration without
                    }
                    if (doc is not null)
                    {
                        dc += doc.Length;
                        wd++;
                        w.WriteString("d", doc);
                    }
                    if (deprecated is not null)
                    {
                        // A page that sends someone to a superseded lemma wastes their afternoon, so this is as
                        // prominent as the axiom verdict.
                        wdep++;
                        w.WriteStartObject("x");
                        if (deprecated.NewName is Name nn)
                        {
                            w.WriteString("to", nn.ToString());
                        }
                        if (deprecated.Text is string dt)
                        {
                            w.WriteString("why", dt);
                        }
                        if (deprecated.Since is string ds)
                        {
                            w.WriteString("since", ds);
                        }
                        w.WriteEndObject();
                    }
                    if (range is not null)
                    {
                        wr++;
                        w.WriteStartArray("l");
                        w.WriteNumberValue(range.Line);
                        w.WriteNumberValue(range.EndLine);
                        w.WriteEndArray();
                    }
                    // t: referenced from the statement; u: referenced from the proof or body only
                    w.WriteStartArray("t");
                    foreach (int t in typeEdges[id])
                    {
                        w.WriteNumberValue(t);
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("u");
                    int ti = 0;
                    foreach (int t in outEdges[id])
                    {
                        while (ti < typeEdges[id].Length && typeEdges[id][ti] < t)
                        {
                            ti++;
                        }
                        if (ti < typeEdges[id].Length && typeEdges[id][ti] == t)
                        {
                            continue;
                        }
                        w.WriteNumberValue(t);
                    }
                    w.WriteEndArray();
                    // the dependents kept are the most depended-upon ones: a reader wants the important users, and the
                    // full list of a foundational constant is hundreds of thousands long
                    ReadOnlySpan<int> ins = g.In(id);
                    w.WriteNumber("bc", ins.Length);
                    w.WriteStartArray("b");
                    foreach (int t in g.MostUsed(id, inEdgeCap))
                    {
                        w.WriteNumberValue(t);
                    }
                    w.WriteEndArray();
                    if (check is not null && check.Failures.TryGetValue(names[id].ToString(), out string? why))
                    {
                        w.WriteString("f", why); // the checker rejected this one; the page must say so
                    }
                    w.WriteStartArray("a");
                    foreach (int a in g.AxiomsOf(id))
                    {
                        w.WriteNumberValue(a);
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.Flush();
                om.TrimCaches();
                Interlocked.Add(ref statementChars, sc);
                Interlocked.Add(ref docChars, dc);
                Interlocked.Add(ref withDoc, wd);
                Interlocked.Add(ref withRange, wr);
                Interlocked.Add(ref withDeprecation, wdep);
                Interlocked.Add(ref bodyChars, bc);
                Interlocked.Add(ref withBody, wb);
                Interlocked.Add(ref bodiesCut, bcut);
                Interlocked.Add(ref withFields, wf);
                Interlocked.Add(ref withMarks, wm);
                Interlocked.Add(ref withHead, wch);
                int d = Interlocked.Increment(ref done);
                if (d % 1000 == 0)
                {
                    Console.WriteLine($"  {d} modules written, {sw.Elapsed.TotalSeconds:F0}s");
                }
            });
            Console.WriteLine($"shards written: {statementChars / 1048576.0:F0} MB of statements, {docChars / 1048576.0:F0} MB of docstrings, {withDoc:N0} declarations with a docstring, {withRange:N0} with a source range, {withDeprecation:N0} deprecated, {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"  definition bodies: {withBody:N0} declarations, {bodyChars / 1048576.0:F0} MB, mean {(withBody == 0 ? 0 : bodyChars / withBody):N0} chars, {bodiesCut:N0} cut at the printer's cap");
            Console.WriteLine($"  structures with fields: {withFields:N0}; declarations with a modifier: {withMarks:N0}; conclusion heads: {withHead:N0}");

            // The name list, the module table and the manifest.
            sw.Restart();
            WriteCompressed(Path.Combine(outDir, "names.txt.gz"),
                Encoding.UTF8.GetBytes(string.Join('\n', names.Select(x => x.ToString()))));
            // one character per id, and one little-endian uint32 per id: enough to label and rank a neighbor
            // without fetching its shard
            WriteCompressed(Path.Combine(outDir, "kinds.txt.gz"),
                Encoding.UTF8.GetBytes(new string(kinds.Select(k => "?adtoqicr"[k]).ToArray())));
            var used = new byte[4L * n];
            for (int id = 0; id < n; id++)
            {
                BitConverter.TryWriteBytes(used.AsSpan(4 * id, 4), g.InOffset[id + 1] - g.InOffset[id]);
            }
            WriteCompressed(Path.Combine(outDir, "used.bin.gz"), used);
            var moduleIndex = new Dictionary<Name, int>();
            for (int mi = 0; mi < modules.Count; mi++)
            {
                moduleIndex[order[mi]] = mi;
            }
            using (Stream fs = Compressed(Path.Combine(outDir, "modules.json.gz")))
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartArray();
                for (int mi = 0; mi < modules.Count; mi++)
                {
                    w.WriteStartObject();
                    w.WriteString("n", order[mi].ToString());
                    w.WriteNumber("s", moduleStart[mi]);
                    w.WriteNumber("c", moduleStart[mi + 1] - moduleStart[mi]);
                    w.WriteStartArray("i");
                    foreach (Import imp in modules[mi].Imports)
                    {
                        if (moduleIndex.TryGetValue(imp.Module, out int ii))
                        {
                            w.WriteNumberValue(ii);
                        }
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            // How much of the library rests on each axiom, and on the ones beyond Lean's three, which
            // declarations. "Cites propext" is unremarkable; "cites something else" is the question a person
            // checking a proof actually has, and until now the only way to answer it was to open every page.
            var standard = new HashSet<string> { "propext", "Classical.choice", "Quot.sound" };
            var axiomUse = new long[g.AxiomIds.Length];
            var holders = new List<int>[g.AxiomIds.Length];
            for (int ax = 0; ax < g.AxiomIds.Length; ax++)
            {
                holders[ax] = standard.Contains(names[g.AxiomIds[ax]].ToString()) ? null! : new List<int>();
            }
            // How many declarations rest on anything beyond the standard three. Counting the axioms instead
            // would say "73 axioms beyond the standard three are in use" about a library where that comes to
            // 269 declarations out of 792,459, nearly all of them compiler and build-tool internals.
            long beyondStandard = 0;
            for (int id = 0; id < n; id++)
            {
                bool beyond = false;
                foreach (int ax in g.AxiomsOf(id))
                {
                    // an axiom rests on itself, which is true and useless
                    if (id == g.AxiomIds[ax])
                    {
                        continue;
                    }
                    axiomUse[ax]++;
                    if (holders[ax] is List<int> h)
                    {
                        h.Add(id);
                        beyond = true;
                    }
                }
                if (beyond)
                {
                    beyondStandard++;
                }
            }
            // Bounded: the point is to show what rests on an unusual axiom, not to ship the whole library twice.
            var axiomHolders = new Dictionary<string, int[]>(StringComparer.Ordinal);
            for (int ax = 0; ax < g.AxiomIds.Length; ax++)
            {
                if (holders[ax] is not List<int> list)
                {
                    continue;
                }
                list.Sort((a, b) => (g.InOffset[b + 1] - g.InOffset[b]).CompareTo(g.InOffset[a + 1] - g.InOffset[a]));
                axiomHolders[names[g.AxiomIds[ax]].ToString()] = list.Take(200).ToArray();
            }

            // Which declarations rest on `sorry`. For Mathlib this is empty; for a formalization in progress it is
            // the progress map, so the page can show what is still conditional and how much stands on each hole.
            int sorryIndex = Array.FindIndex(g.AxiomIds, a => names[a].ToString() == "sorryAx");
            var holes = new List<int>();
            if (sorryIndex >= 0)
            {
                int sorryId = g.AxiomIds[sorryIndex];
                foreach (int id in Enumerable.Range(0, n))
                {
                    // the axiom rests on itself, which is true and useless
                    if (id != sorryId && (g.AxiomBits[(long)id * g.Words + sorryIndex / 64] & (1UL << (sorryIndex % 64))) != 0)
                    {
                        holes.Add(id);
                    }
                }
                Console.WriteLine($"{holes.Count:N0} declarations rest on sorry");
            }

            // Written before the manifest, because the hash in the manifest has to be over the file that
            // actually ships rather than the one on the checker's disk.
            string? shippedCheck = checkReport is null ? null : WriteCheckReport(checkReport, target, outDir);
            var manifest = new
            {
                generated = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                lean = leanVersion,
                // The leaf name, not the full path. A bundle is public, and the absolute path says which
                // machine and whose home directory it came off, which is not what this field is for.
                target = Path.GetFileName(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar)),
                modules = modules.Count,
                declarations = n,
                references = g.EdgeCount,
                inEdgeCap,
                // What this generator wrote, so a reader of the bundle can tell "no definition bodies here"
                // from "this bundle predates them". Libraries published from an older tarball have neither
                // the field nor the data, and must not be judged against a rule they were not built under.
                definitionBodies = withBody,
                definitionBodiesCut = bodiesCut,
                axiomUse,
                axiomHolders,
                beyondStandard,
                axioms = g.AxiomIds.Select(a => names[a].ToString()).ToArray(),
                standardAxioms = new[] { "propext", "Classical.choice", "Quot.sound" },
                kinds = new[] { "unknown", "axiom", "def", "theorem", "opaque", "quot", "inductive", "constructor", "recursor" },
                libraries = Libraries(target, leanVersion),
                slug,
                title,
                // Which modules are the project's own rather than something it imports. A page landing on a
                // project has to lead with the project, not with the 792,459 declarations of Mathlib underneath.
                ownModules = own.Select(m => m.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                holes = holes.ToArray(),
                repository = repo,
                check = check is null ? null : new
                {
                    tenet = check.Tenet, lean = check.Lean, all = check.All, success = check.Success,
                    @checked = check.Checked, failed = check.Failed, seconds = check.Seconds,
                    date = File.GetLastWriteTimeUtc(checkReport!).ToString("u", CultureInfo.InvariantCulture),
                    // the report travels with the bundle; the hash is what an attestation of it is over
                    report = "check.json",
                    sha256 = Sha256(shippedCheck!),
                },
            };
            File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outDir, "badge.svg"), Badge(check, n));
            RegisterProject(siteDir, slug, title, manifest);
            // A fingerprint per declaration, so two bundles can be compared without either one's shards: the name
            // and the statement, hashed. Same name and same digest means unchanged; same name and a different
            // digest means the statement moved under someone's feet, which is the interesting case.
            var digest = new byte[8L * n];
            Parallel.For(0, modules.Count, options, mi =>
            {
                OleanModule om = modules[mi];
                for (int id = moduleStart[mi]; id < moduleStart[mi + 1]; id++)
                {
                    ConstantInfo? ci = om.FindConstant(names[id]);
                    ulong h = Fnv(names[id].ToString());
                    if (ci is not null)
                    {
                        h = Fnv(pretty.Statement(ci), h);
                        // The body is part of what the page shows, so it has to be part of what the digest
                        // covers. Hashing the statement alone meant a definition could be rewritten from under
                        // a reader and every diff would call the bundle unchanged.
                        string? b = pretty.Body(ci);
                        if (b is not null)
                        {
                            h = Fnv(b, h);
                        }
                    }
                    BitConverter.TryWriteBytes(digest.AsSpan(8 * id, 8), h);
                }
                om.TrimCaches();
            });
            WriteCompressed(Path.Combine(outDir, "digest.bin.gz"), digest);

            // The whole graph as two compressed arrays, for the questions a page cannot answer from one shard:
            // how much a declaration rests on in total, the chain between any two, and which statements mention a
            // given constant. It is the biggest file in the bundle, so the page fetches it only when asked.
            sw.Restart();
            WriteGraph(Path.Combine(outDir, "graph.bin.gz"), outEdges, typeEdges, n);
            Console.WriteLine($"graph.bin written in {sw.Elapsed.TotalSeconds:F1}s " +
                              $"({new FileInfo(Path.Combine(outDir, "graph.bin.gz")).Length / 1048576.0:F0} MB)");

            long bytes = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Console.WriteLine($"bundle: {bytes / 1048576.0:F0} MB in {Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Count():N0} files under {Path.GetFullPath(outDir)}, {total.Elapsed.TotalSeconds:F1}s in all");
        }
        return 0;
    }

    /// <summary>
    /// What <c>tenet check --report</c> wrote: the run's shape and the declarations it rejected, by name. The
    /// bundle carries this so a page can distinguish "these are the axioms this proof cites" from "an independent
    /// kernel re-derived this proof", which are different claims and were being conflated before it existed.
    /// </summary>
    private sealed class CheckStamp
    {
        public string Tenet = "", Lean = "";
        public bool All, Success;
        public long Checked, Failed;
        public double Seconds;
        public Dictionary<string, string> Failures = new(StringComparer.Ordinal);

        /// <summary>Read a report, refusing one whose counts do not agree with the failures it names.</summary>
        public static CheckStamp Read(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            var c = new CheckStamp
            {
                Tenet = r.TryGetProperty("tenet", out JsonElement t) ? t.GetString() ?? "" : "",
                Lean = r.TryGetProperty("lean", out JsonElement l) ? l.GetString() ?? "" : "",
                All = r.TryGetProperty("all", out JsonElement a) && a.ValueKind == JsonValueKind.True,
                Success = r.TryGetProperty("success", out JsonElement s) && s.ValueKind == JsonValueKind.True,
                Checked = r.TryGetProperty("checked", out JsonElement ch) ? ch.GetInt64() : 0,
                Failed = r.TryGetProperty("failed", out JsonElement f) ? f.GetInt64() : 0,
                Seconds = r.TryGetProperty("seconds", out JsonElement sec) ? sec.GetDouble() : 0,
            };
            if (r.TryGetProperty("failures", out JsonElement failures))
            {
                foreach (JsonElement x in failures.EnumerateArray())
                {
                    string name = x.GetProperty("name").GetString() ?? "";
                    string msg = x.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                    c.Failures[name] = msg;
                }
            }
            if (c.Failures.Count != c.Failed)
            {
                throw new InvalidOperationException($"{path}: says {c.Failed} failed but names {c.Failures.Count}; not a report this generator understands");
            }
            return c;
        }
    }

    /// <summary>The hash a page cites and an attestation is over, so a published verdict names the bytes it judged.</summary>
    /// <summary>
    /// The reference graph as one file: forward edges for every declaration, then the reverse of the statement
    /// edges, both as CSR (an offset array and a flat array of targets). Little-endian <c>uint32</c> throughout,
    /// with a short header, so a browser reads it into typed arrays with no parsing at all.
    ///
    /// Layout: magic <c>"LVG1"</c>, n, forward edge count, mention edge count, then
    /// <c>forwardOffsets[n + 1]</c>, <c>forwardTargets[]</c>, <c>mentionOffsets[n + 1]</c>, <c>mentionTargets[]</c>.
    /// Forward is what a declaration references; mention is the reverse of the statement edges, which answers
    /// "which statements mention this constant" and is what the constant search reads.
    /// </summary>
    private static void WriteGraph(string path, int[][] forward, int[][] typeEdges, int n)
    {
        long forwardCount = forward.Sum(o => (long)o.Length);
        var mentionCount = new int[n + 1];
        foreach (int[] o in typeEdges)
        {
            foreach (int t in o)
            {
                mentionCount[t]++;
            }
        }
        var mentionOffset = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            mentionOffset[i + 1] = mentionOffset[i] + mentionCount[i];
        }
        var fill = (int[])mentionOffset.Clone();
        var mentions = new int[mentionOffset[n]];
        for (int v = 0; v < n; v++)
        {
            foreach (int t in typeEdges[v])
            {
                mentions[fill[t]++] = v;
            }
        }

        using Stream raw = Compressed(path);
        using var w = new BinaryWriter(raw);
        w.Write((byte)'L');
        w.Write((byte)'V');
        w.Write((byte)'G');
        w.Write((byte)'1');
        w.Write(n);
        w.Write((int)forwardCount);
        w.Write(mentions.Length);
        int at = 0;
        w.Write(at);
        foreach (int[] o in forward)
        {
            at += o.Length;
            w.Write(at);
        }
        foreach (int[] o in forward)
        {
            foreach (int t in o)
            {
                w.Write(t);
            }
        }
        foreach (int off in mentionOffset)
        {
            w.Write(off);
        }
        foreach (int t in mentions)
        {
            w.Write(t);
        }
    }

    /// <summary>
    /// A stream that gzips as it is written. The bundle is served by a static host, which stores what it is given:
    /// Mathlib's shards are 474 MB of JSON and about 85 MB gzipped, and GitHub Pages allows a gigabyte for the
    /// whole site, so storing them compressed is what makes room for a second library. The page decompresses.
    /// </summary>
    private static Stream Compressed(string path) =>
        new GZipStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), CompressionLevel.SmallestSize);

    /// <summary>Write bytes through <see cref="Compressed"/>.</summary>
    private static void WriteCompressed(string path, byte[] bytes)
    {
        using Stream s = Compressed(path);
        s.Write(bytes);
    }

    /// <summary>A url-safe name for a bundle's directory.</summary>
    private static string Slugify(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }
        return sb.ToString().Trim('-') is { Length: > 0 } t ? t : "library";
    }

    /// <summary>
    /// Record this bundle in the site's <c>projects.json</c>, keeping any others already there. The page reads it
    /// to offer a switch between libraries, so a bundle regenerated on its own must not remove its neighbours.
    /// </summary>
    private static void RegisterProject(string siteDir, string slug, string title, object manifest)
    {
        string path = Path.Combine(siteDir, "projects.json");
        var entries = new List<Dictionary<string, object?>>();
        if (File.Exists(path))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                var d = new Dictionary<string, object?>();
                foreach (JsonProperty prop in e.EnumerateObject())
                {
                    d[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetString(),
                    };
                }
                if (d.TryGetValue("slug", out object? had))
                {
                    // A regenerated library keeps the place it already had. Rebuilding one used to move it, and
                    // since the list was then sorted by slug, regenerating Mathlib on a site that also carried
                    // FLT made FLT the page everyone landed on.
                    entries.Add((had as string) == slug ? null! : d);
                }
            }
        }
        dynamic m = manifest;
        var fresh = new Dictionary<string, object?>
        {
            ["slug"] = slug,
            ["title"] = title,
            ["declarations"] = (long)(int)m.declarations,
            ["modules"] = (long)(int)m.modules,
            ["lean"] = (string)m.lean,
            ["generated"] = (string)m.generated,
            ["checked"] = m.check is null ? null : (object)true,
        };
        int at = entries.IndexOf(null!);
        if (at >= 0)
        {
            entries[at] = fresh;
        }
        else
        {
            entries.Add(fresh);
        }
        // Deliberately not sorted. The first entry is the library the site opens on, and that is a decision the
        // person who built the site made by the order they built things in, not something alphabetical order
        // should be allowed to overrule.
        entries.RemoveAll(e => e is null);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// FNV-1a over a string. Not a cryptographic hash and not meant to be: it decides whether two bundles say the
    /// same thing about a declaration, where a collision costs a missed line in a diff.
    /// </summary>
    private static ulong Fnv(string s, ulong h = 14695981039346656037UL)
    {
        foreach (char c in s)
        {
            h = (h ^ (byte)c) * 1099511628211UL;
            h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>The hash a page cites and an attestation is over, so a published verdict names the bytes it judged.</summary>
    /// <summary>
    /// A checker's report names every file by its absolute path on the machine that ran it. Once the bundle
    /// is public that path is nobody's business: it puts a stranger's home directory on a web page. Paths
    /// inside the project become relative to it, and anything else under a home directory becomes <c>~</c>.
    /// The bundle ships this, and the manifest's hash is over this, so the two still match.
    /// </summary>
    private static string WriteCheckReport(string checkReport, string target, string outDir)
    {
        var text = File.ReadAllText(checkReport);
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        text = text.Replace(root + Path.DirectorySeparatorChar, string.Empty).Replace(root, ".");
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            home = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
            text = text.Replace(home, "~");
        }
        var dest = Path.Combine(outDir, "check.json");
        File.WriteAllText(dest, text);
        return dest;
    }

    private static string Sha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>The kind as one byte, for the per-id table the page loads once.</summary>
    private static byte KindCode(ConstantInfo c) => c switch
    {
        AxiomInfo => 1,
        DefinitionInfo => 2,
        TheoremInfo => 3,
        OpaqueInfo => 4,
        QuotInfo => 5,
        InductiveInfo => 6,
        ConstructorInfo => 7,
        RecursorInfo => 8,
        _ => 0,
    };

    /// <summary>
    /// A README badge for the project this bundle is about: what an independent kernel said about it.
    ///
    /// Written as a file rather than served by an endpoint, because this site is static and a static site can
    /// answer with bytes it already has. The width is computed from the text rather than guessed, since a
    /// badge whose label overflows its box is worse than no badge.
    /// </summary>
    private static string Badge(CheckStamp? check, int declarations)
    {
        string right = check is null ? "not re-checked"
            : check.Failed > 0 ? $"{check.Failed:N0} rejected"
            : $"{check.Checked:N0} checked, 0 rejected";
        string color = check is null ? "#9f9f9f" : check.Failed > 0 ? "#c0392b" : "#2e7d32";
        const string left = "Tenet";
        // 6.2px per character at 11px in the stack below, plus 10px of padding each side: measured, not guessed.
        int lw = (int)Math.Round(left.Length * 6.2) + 20;
        int rw = (int)Math.Round(right.Length * 6.2) + 20;
        int w = lw + rw;
        string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="20" role="img" aria-label="Tenet: {Esc(right)}">
              <title>Tenet: {Esc(right)}</title>
              <rect width="{lw}" height="20" fill="#444"/>
              <rect x="{lw}" width="{rw}" height="20" fill="{color}"/>
              <g fill="#fff" text-anchor="middle" font-family="Verdana,DejaVu Sans,sans-serif" font-size="11">
                <text x="{lw / 2.0:F1}" y="14">{left}</text>
                <text x="{lw + rw / 2.0:F1}" y="14">{Esc(right)}</text>
              </g>
            </svg>

            """;
    }

    /// <summary>The same kind spelled out, for a shard and for the page's badges.</summary>
    private static string KindName(byte k) => k switch
    {
        1 => "axiom", 2 => "def", 3 => "theorem", 4 => "opaque", 5 => "quot", 6 => "inductive", 7 => "constructor", 8 => "recursor", _ => "unknown",
    };

    /// <summary>
    /// Map the target's own modules and everything they import. Two loads on purpose: the first reads a module to
    /// learn which Lean version built it, and only then can the matching toolchain be added to the search path so
    /// that <c>Init</c> and <c>Std</c> resolve. A Lake project is read from its build tree, minus
    /// <c>.lake/packages</c>, whose modules arrive as imports instead so they are not counted as the project's own.
    /// </summary>
    private static (OleanChecker Checker, List<Name> Own) Open(string target)
    {
        string lib = Path.Combine(target, ".lake", "build", "lib", "lean");
        string root = Directory.Exists(lib) ? lib : target;
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"{root} does not exist");
        }
        List<string> files = Directory.EnumerateFiles(root, "*.olean", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Contains(Path.Combine(".lake", "packages"), StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"no .olean files under {root}. A Lake project needs `lake build` first; "
                                              + "a toolchain's library is the `lib/lean` under `lean --print-prefix`.");
        }
        var search = new LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(files[0]);
        var checker = new OleanChecker(search);
        var targets = files.Select(f => (Module: search.ModuleNameOf(f), Path: f)).ToList();
        checker.Load(targets);
        search.AddToolchainFor(checker.Modules[targets[0].Module].LeanVersion);
        checker.Load(targets);
        return (checker, targets.Select(t => t.Module).ToList());
    }

    /// <summary>
    /// Where each module's source lives, so a page can link a declaration to its line: the toolchain for
    /// Init/Std/Lean/Lake, the Lake manifest for packages, and git for the project itself.
    /// </summary>
    private static object[] Libraries(string target, string leanVersion)
    {
        var libs = new List<object>
        {
            new { prefixes = new[] { "Init", "Std", "Lean" }, name = "Lean", url = "https://github.com/leanprover/lean4", rev = "v" + leanVersion, path = "src/" },
            new { prefixes = new[] { "Lake" }, name = "Lake", url = "https://github.com/leanprover/lean4", rev = "v" + leanVersion, path = "src/lake/" },
        };
        string manifestPath = Path.Combine(target, "lake-manifest.json");
        if (File.Exists(manifestPath))
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["importGraph"] = "ImportGraph", ["proofwidgets"] = "ProofWidgets", ["Qq"] = "Qq", ["Cli"] = "Cli",
            };
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("packages", out JsonElement packages))
            {
                foreach (JsonElement p in packages.EnumerateArray())
                {
                    string name = p.GetProperty("name").GetString() ?? "";
                    string prefix = aliases.TryGetValue(name, out string? a) ? a : char.ToUpperInvariant(name[0]) + name[1..];
                    libs.Add(new
                    {
                        prefixes = new[] { prefix },
                        name = prefix,
                        url = p.TryGetProperty("url", out JsonElement u) ? u.GetString() : null,
                        rev = p.TryGetProperty("rev", out JsonElement r) ? r.GetString() : null,
                        path = "",
                    });
                }
            }
            string? projectUrl = Git(target, "remote", "get-url", "origin")?.Trim();
            if (projectUrl is not null && projectUrl.EndsWith(".git", StringComparison.Ordinal))
            {
                projectUrl = projectUrl[..^4];
            }
            libs.Add(new
            {
                prefixes = Array.Empty<string>(), // the fallback: any module no library above claims
                name = Path.GetFileName(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar)),
                url = projectUrl,
                rev = Git(target, "rev-parse", "HEAD")?.Trim(),
                path = "",
            });
        }
        return libs.ToArray();
    }

    /// <summary>Run git in a directory, or return null: a project without a repository still gets a bundle, minus source links.</summary>
    private static string? Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using Process? p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            string s = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? s : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
