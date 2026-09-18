using System.Diagnostics;
using System.Globalization;
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
/// The second decode is deliberate: holding 791,453 decoded constants would cost more memory than re-reading
/// them costs time, and <see cref="OleanModule.TrimCaches"/> between modules keeps the peak flat.
/// </summary>
internal static class Program
{
    private const string Usage = """
        usage: leanviz <project or build tree> --out <dir> [--jobs N] [--in-edges N]

          <project>   a Lake project directory (its .lake/build/lib/lean is read, its imports found from there),
                      or any directory of .olean files
          --out       where the bundle goes (default: site/data)
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
        string? target = null, outDir = "site/data", checkReport = null, repo = null;
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
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        (OleanChecker checker, List<Name> own) = Open(target);
        using (checker)
        {
            List<Name> order = checker.DependencyOrder(own);
            var modules = order.Select(m => checker.Modules[m]).ToList();
            string leanVersion = modules[0].LeanVersion;
            Console.WriteLine($"{modules.Count} modules mapped ({own.Count} in the target, the rest imported), Lean {leanVersion}, {sw.Elapsed.TotalSeconds:F1}s");

            // Ids: dense, in dependency order, contiguous per module. A name seen twice keeps its first id.
            sw.Restart();
            var names = new List<Name>();
            var idOf = new Dictionary<Name, int>();
            var moduleStart = new int[modules.Count + 1];
            for (int mi = 0; mi < modules.Count; mi++)
            {
                moduleStart[mi] = names.Count;
                foreach (Name cn in modules[mi].ConstantNames)
                {
                    if (idOf.TryAdd(cn, names.Count))
                    {
                        names.Add(cn);
                    }
                }
            }
            moduleStart[modules.Count] = names.Count;
            int n = names.Count;

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
                        if (idOf.TryGetValue(u, out int t))
                        {
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
                        if (e is ConstExpr k && idOf.TryGetValue(k.Name, out int t) && t != id)
                        {
                            inType.Add(t);
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
            long statementChars = 0, docChars = 0, withDoc = 0, withRange = 0;
            int done = 0;
            Parallel.For(0, modules.Count, options, mi =>
            {
                OleanModule om = modules[mi];
                string path = Path.Combine(outDir, "m", order[mi].ToString() + ".json");
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                w.WriteStartArray();
                long sc = 0, dc = 0, wd = 0, wr = 0;
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
                    }
                    string? doc = null;
                    SourceRange? range = null;
                    try
                    {
                        doc = om.DocStringOf(names[id]);
                        range = om.SourceRangeOf(names[id]);
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
                int d = Interlocked.Increment(ref done);
                if (d % 1000 == 0)
                {
                    Console.WriteLine($"  {d} modules written, {sw.Elapsed.TotalSeconds:F0}s");
                }
            });
            Console.WriteLine($"shards written: {statementChars / 1048576.0:F0} MB of statements, {docChars / 1048576.0:F0} MB of docstrings, {withDoc:N0} declarations with a docstring, {withRange:N0} with a source range, {sw.Elapsed.TotalSeconds:F1}s");

            // The name list, the module table and the manifest.
            sw.Restart();
            File.WriteAllLines(Path.Combine(outDir, "names.txt"), names.Select(x => x.ToString()));
            // one character per id, and one little-endian uint32 per id: enough to label and rank a neighbor
            // without fetching its shard
            File.WriteAllText(Path.Combine(outDir, "kinds.txt"), new string(kinds.Select(k => "?adtoqicr"[k]).ToArray()));
            var used = new byte[4L * n];
            for (int id = 0; id < n; id++)
            {
                BitConverter.TryWriteBytes(used.AsSpan(4 * id, 4), g.InOffset[id + 1] - g.InOffset[id]);
            }
            File.WriteAllBytes(Path.Combine(outDir, "used.bin"), used);
            var moduleIndex = new Dictionary<Name, int>();
            for (int mi = 0; mi < modules.Count; mi++)
            {
                moduleIndex[order[mi]] = mi;
            }
            using (var fs = File.Create(Path.Combine(outDir, "modules.json")))
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
            var manifest = new
            {
                generated = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                lean = leanVersion,
                target = Path.GetFullPath(target),
                modules = modules.Count,
                declarations = n,
                references = g.EdgeCount,
                inEdgeCap,
                axioms = g.AxiomIds.Select(a => names[a].ToString()).ToArray(),
                standardAxioms = new[] { "propext", "Classical.choice", "Quot.sound" },
                kinds = new[] { "unknown", "axiom", "def", "theorem", "opaque", "quot", "inductive", "constructor", "recursor" },
                libraries = Libraries(target, leanVersion),
                repository = repo,
                check = check is null ? null : new
                {
                    tenet = check.Tenet, lean = check.Lean, all = check.All, success = check.Success,
                    @checked = check.Checked, failed = check.Failed, seconds = check.Seconds,
                    date = File.GetLastWriteTimeUtc(checkReport!).ToString("u", CultureInfo.InvariantCulture),
                    // the report travels with the bundle; the hash is what an attestation of it is over
                    report = "check.json",
                    sha256 = Sha256(checkReport!),
                },
            };
            if (checkReport is not null)
            {
                File.Copy(checkReport, Path.Combine(outDir, "check.json"), overwrite: true);
            }
            File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
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
        List<string> files = Directory.EnumerateFiles(root, "*.olean", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Contains(Path.Combine(".lake", "packages"), StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"no .olean files under {root} (is the project built?)");
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
