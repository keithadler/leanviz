using System.Text;
using Tenet.Kernel;

namespace LeanNavigator;

/// <summary>
/// Prints a kernel term the way a reader expects to see a statement: universe levels hidden, implicit and
/// instance arguments dropped, Lean's notation for the operators everyone knows, binders grouped. The kernel's
/// own printer shows every argument because a checker must; a page must not. This is not Lean's delaborator and
/// does not try to be: it knows a table of notations and the binder shapes of the constants it sees, nothing more,
/// and what it does not know it prints as plain application.
/// </summary>
public sealed class Pretty
{
    private readonly Func<Name, ConstantInfo?> _find;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Name, BinderInfo[]> _binders = new();

    /// <summary>Output longer than this is cut; a page shows the statement, not the whole term.</summary>
    public int MaxLength { get; init; } = 3000;

    public Pretty(Func<Name, ConstantInfo?> find) => _find = find;

    private sealed record Op(string Symbol, int Prec, char Assoc); // 'l', 'r', 'n'

    private static readonly Dictionary<string, Op> Binary = new(StringComparer.Ordinal)
    {
        ["Eq"] = new("=", 50, 'n'), ["Ne"] = new("≠", 50, 'n'), ["HEq"] = new("≍", 50, 'n'),
        ["LE.le"] = new("≤", 50, 'n'), ["LT.lt"] = new("<", 50, 'n'), ["GE.ge"] = new("≥", 50, 'n'), ["GT.gt"] = new(">", 50, 'n'),
        ["Iff"] = new("↔", 20, 'n'), ["And"] = new("∧", 35, 'r'), ["Or"] = new("∨", 30, 'r'),
        ["HAdd.hAdd"] = new("+", 65, 'l'), ["HSub.hSub"] = new("-", 65, 'l'), ["HMul.hMul"] = new("*", 70, 'l'),
        ["HDiv.hDiv"] = new("/", 70, 'l'), ["HMod.hMod"] = new("%", 70, 'l'), ["HPow.hPow"] = new("^", 75, 'r'),
        ["HAppend.hAppend"] = new("++", 65, 'l'), ["HSMul.hSMul"] = new("•", 73, 'r'), ["HVAdd.hVAdd"] = new("+ᵥ", 65, 'r'),
        ["Function.comp"] = new("∘", 90, 'r'), ["Dvd.dvd"] = new("∣", 50, 'n'),
        ["HasSubset.Subset"] = new("⊆", 50, 'n'), ["HasSSubset.SSubset"] = new("⊂", 50, 'n'),
        ["Union.union"] = new("∪", 65, 'l'), ["Inter.inter"] = new("∩", 70, 'l'), ["SDiff.sdiff"] = new("\\", 70, 'l'),
        ["Sup.sup"] = new("⊔", 68, 'l'), ["Inf.inf"] = new("⊓", 69, 'l'), ["Max.max"] = new("⊔", 68, 'l'), ["Min.min"] = new("⊓", 69, 'l'),
        ["Prod"] = new("×", 35, 'r'), ["Sum"] = new("⊕", 30, 'r'), ["PProd"] = new("×'", 35, 'r'),
        ["Equiv"] = new("≃", 25, 'n'), ["Function.Embedding"] = new("↪", 25, 'n'),
        ["Nat.ModEq"] = new("≡ [MOD]", 50, 'n'), ["Set.image"] = new("''", 81, 'l'), ["Set.preimage"] = new("⁻¹'", 80, 'l'),
        ["Finset.image"] = new("''", 81, 'l'), ["Filter.Tendsto"] = new("→ᶠ", 50, 'n'),
    };

    private static readonly Dictionary<string, (string Symbol, int Prec)> Prefix = new(StringComparer.Ordinal)
    {
        ["Not"] = ("¬", 40), ["Neg.neg"] = ("-", 75), ["Inv.inv"] = ("⁻¹", -1), ["HasCompl.compl"] = ("ᶜ", -1),
        ["Nat.cast"] = ("↑", 1024), ["Int.cast"] = ("↑", 1024), ["Rat.cast"] = ("↑", 1024), ["NNReal.toReal"] = ("↑", 1024),
        ["Subtype.val"] = ("↑", 1024), ["Real.sqrt"] = ("√", 100), ["Nat.factorial"] = ("!", -1),
        ["Finset.card"] = ("#", 1024),
    };

    public string Statement(ConstantInfo c) => Print(c.Type);

    public string Print(Expr e)
    {
        var sb = new StringBuilder();
        var names = new List<string>();
        Go(e, sb, names, 0);
        if (sb.Length > MaxLength)
        {
            sb.Length = MaxLength;
            sb.Append(" …");
        }
        return sb.ToString();
    }

    /// <summary>The binder kinds of the leading ∀s of a constant's type: what an application of it hides.</summary>
    private BinderInfo[] BindersOf(Name n) => _binders.GetOrAdd(n, k =>
    {
        ConstantInfo? c = _find(k);
        if (c is null)
        {
            return [];
        }
        var infos = new List<BinderInfo>();
        for (Expr t = c.Type; t is PiExpr p; t = p.Body)
        {
            infos.Add(p.Info);
        }
        return infos.ToArray();
    });

    /// <summary>Hygienic names (`x._@.Module._hyg.3`) show as `x✝`, private names without their mangling.</summary>
    public static string Display(Name n)
    {
        var parts = new List<string>();
        bool hygienic = false;
        for (Name at = n; !at.IsAnonymous; at = at.Prefix)
        {
            if (at.TryGetStr(out _, out string? s))
            {
                if (s == "_@")
                {
                    hygienic = true;
                    parts.Clear();
                    continue;
                }
                if (hygienic && parts.Count == 0 && (s == "_hyg" || s.StartsWith('_')))
                {
                    continue;
                }
                parts.Add(s);
            }
            else if (at.TryGetNum(out _, out ulong v))
            {
                if (!hygienic)
                {
                    parts.Add(v.ToString());
                }
            }
        }
        // the hygienic walk collected the suffix after `_@` first, then cleared; what is left is the user part
        if (hygienic)
        {
            parts.Clear();
            for (Name at = n; !at.IsAnonymous; at = at.Prefix)
            {
                if (at.TryGetStr(out _, out string? s) && s == "_@")
                {
                    for (Name u = at.Prefix; !u.IsAnonymous; u = u.Prefix)
                    {
                        if (u.TryGetStr(out _, out string? us))
                        {
                            parts.Add(us);
                        }
                        else if (u.TryGetNum(out _, out ulong uv))
                        {
                            parts.Add(uv.ToString());
                        }
                    }
                    break;
                }
            }
            parts.Reverse();
            return string.Join('.', parts) + "✝";
        }
        parts.Reverse();
        if (parts.Count > 2 && parts[0] == "_private")
        {
            int i = parts.FindIndex(1, p => p == "0");
            if (i > 0 && i + 1 < parts.Count)
            {
                parts = parts.Skip(i + 1).ToList();
            }
        }
        return string.Join('.', parts.Select(Escape));
    }

    private static string Escape(string s) =>
        s.Length == 0 || s.Contains('.') || s.Contains(' ') || char.IsDigit(s[0]) ? "«" + s + "»" : s;

    private static bool IsHygienic(Name n)
    {
        for (Name at = n; !at.IsAnonymous; at = at.Prefix)
        {
            if (at.TryGetStr(out _, out string? s) && s == "_@")
            {
                return true;
            }
        }
        return false;
    }

    private static string Wrap(string s, int own, int want) => own < want ? "(" + s + ")" : s;

    private void Go(Expr e, StringBuilder sb, List<string> names, int prec)
    {
        if (sb.Length > MaxLength)
        {
            return;
        }
        switch (e)
        {
            case BVarExpr b:
                {
                    int i = names.Count - 1 - b.Idx;
                    sb.Append(i >= 0 ? names[i] : "#" + b.Idx);
                    break;
                }
            case FVarExpr f:
                sb.Append(f.Id);
                break;
            case SortExpr s:
                sb.Append(SortText(s.Level, prec));
                break;
            case ConstExpr c:
                sb.Append(Display(c.Name));
                break;
            case LitExpr lit:
                sb.Append(lit.Value is StrLiteral str ? Quote(str.Value) : lit.Value.ToString());
                break;
            case ProjExpr p:
                Go(p.Struct, sb, names, 1024);
                sb.Append('.').Append(p.Idx + 1);
                break;
            case AppExpr:
                App(e, sb, names, prec);
                break;
            case LamExpr:
                {
                    if (prec > 0)
                    {
                        sb.Append('(');
                    }
                    sb.Append("fun");
                    Expr body = e;
                    int pushed = 0;
                    while (body is LamExpr l)
                    {
                        string nm = BinderName(l.BinderName, names);
                        sb.Append(' ').Append(nm);
                        names.Add(nm);
                        pushed++;
                        body = l.Body;
                    }
                    sb.Append(" => ");
                    Go(body, sb, names, 0);
                    names.RemoveRange(names.Count - pushed, pushed);
                    if (prec > 0)
                    {
                        sb.Append(')');
                    }
                    break;
                }
            case PiExpr pi:
                Pi(pi, sb, names, prec);
                break;
            case LetExpr l:
                {
                    if (prec > 0)
                    {
                        sb.Append('(');
                    }
                    string nm = BinderName(l.Name, names);
                    sb.Append("let ").Append(nm).Append(" := ");
                    Go(l.Value, sb, names, 0);
                    sb.Append("; ");
                    names.Add(nm);
                    Go(l.Body, sb, names, 0);
                    names.RemoveAt(names.Count - 1);
                    if (prec > 0)
                    {
                        sb.Append(')');
                    }
                    break;
                }
            default:
                sb.Append('?');
                break;
        }
    }

    private static string SortText(Level l, int prec)
    {
        if (l.Kind == LevelKind.Zero)
        {
            return "Prop";
        }
        if (l.Equals(Level.One))
        {
            return "Type";
        }
        (Level b, int off) = l.ToOffset();
        if (off >= 1)
        {
            Level inner = Level.MkSucc(b, off - 1);
            string t = inner.Kind == LevelKind.Zero ? "Type" : "Type " + LevelText(inner);
            return prec > 1023 ? "(" + t + ")" : t;
        }
        string s = "Sort " + LevelText(l);
        return prec > 1023 ? "(" + s + ")" : s;
    }

    private static string LevelText(Level l)
    {
        string s = l.ToString();
        return s.Contains(' ') || s.Contains('+') ? "(" + s + ")" : s;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string BinderName(Name n, List<string> names)
    {
        string s = IsHygienic(n) ? Display(n) : Display(n);
        return s.Length == 0 ? "x✝" : s;
    }

    private void Pi(PiExpr first, StringBuilder sb, List<string> names, int prec)
    {
        // A → B when nothing depends on the binder and it is explicit
        if (first.Info == BinderInfo.Default && !ExprOps.HasLooseBVar(first.Body, 0))
        {
            string left = Sub(first.Domain, names, 26);
            names.Add("_");
            string right = Sub(first.Body, names, IsLeading(first.Body) ? 0 : 25);
            names.RemoveAt(names.Count - 1);
            sb.Append(Wrap(left + " → " + right, 25, prec));
            return;
        }
        var groups = new List<string>();
        Expr body = first;
        int pushed = 0;
        while (body is PiExpr p && (ReferenceEquals(body, first) || p.Info != BinderInfo.Default || ExprOps.HasLooseBVar(p.Body, 0)))
        {
            // group binders with the same kind and the same closed domain: (n m : Nat)
            var run = new List<string>();
            Expr domain = p.Domain;
            BinderInfo info = p.Info;
            string domainText = Sub(domain, names, 0);
            bool closed = !ExprOps.HasLooseBVar(domain, 0) && LooseFree(domain);
            while (body is PiExpr q && q.Info == info && (run.Count == 0 || (closed && q.Domain.Equals(domain))))
            {
                if (run.Count > 0 && !(q.Info != BinderInfo.Default || ExprOps.HasLooseBVar(q.Body, 0)))
                {
                    break; // the next one is an arrow, not a binder
                }
                string nm = BinderName(q.BinderName, names);
                bool anonymousInst = info == BinderInfo.InstImplicit && (IsHygienic(q.BinderName) || !ExprOps.HasLooseBVar(q.Body, 0));
                run.Add(anonymousInst ? "" : nm);
                names.Add(nm);
                pushed++;
                body = q.Body;
                if (anonymousInst)
                {
                    break;
                }
            }
            var (open, close) = info switch
            {
                BinderInfo.Implicit => ("{", "}"),
                BinderInfo.StrictImplicit => ("⦃", "⦄"),
                BinderInfo.InstImplicit => ("[", "]"),
                _ => ("(", ")"),
            };
            groups.Add(run.Count == 1 && run[0].Length == 0
                ? open + domainText + close
                : open + string.Join(' ', run) + " : " + domainText + close);
        }
        string bodyText = Sub(body, names, 0);
        names.RemoveRange(names.Count - pushed, pushed);
        sb.Append(Wrap("∀ " + string.Join(' ', groups) + ", " + bodyText, 0, prec));
    }

    private static bool LooseFree(Expr e) => e.LooseBVarRange == 0;

    /// <summary>A form whose body runs to the end of the line, so as a last operand it needs no parentheses: `p → ∀ x, q`.</summary>
    private static bool IsLeading(Expr e) =>
        e is PiExpr or LamExpr or LetExpr
        || (e.GetAppArgs(out _) is ConstExpr c && c.Name.ToString() is "Exists" or "ite" or "dite");

    private string Sub(Expr e, List<string> names, int prec)
    {
        var sb = new StringBuilder();
        Go(e, sb, names, prec);
        return sb.ToString();
    }

    private void App(Expr e, StringBuilder sb, List<string> names, int prec)
    {
        Expr head = e.GetAppArgs(out Expr[] args);
        if (head is ConstExpr c)
        {
            string name = c.Name.ToString();
            BinderInfo[] infos = BindersOf(c.Name);
            var visible = new List<Expr>();
            for (int i = 0; i < args.Length; i++)
            {
                if (i >= infos.Length || infos[i] == BinderInfo.Default)
                {
                    visible.Add(args[i]);
                }
            }
            switch (name)
            {
                case "OfNat.ofNat" when args.Length >= 2:
                    Go(args[1], sb, names, prec);
                    return;
                case "DFunLike.coe" or "FunLike.coe" when visible.Count >= 2:
                    {
                        var parts = visible.Select(a => Sub(a, names, 1024)).ToList();
                        sb.Append(Wrap(string.Join(' ', parts), 1023, prec));
                        return;
                    }
                case "Exists" or "Subtype" or "setOf" or "Set" when visible.Count == 1 && visible[0] is LamExpr lam && name != "Set":
                    {
                        string nm = BinderName(lam.BinderName, names);
                        names.Add(nm);
                        string body = Sub(lam.Body, names, 0);
                        names.RemoveAt(names.Count - 1);
                        sb.Append(name switch
                        {
                            "Exists" => Wrap("∃ " + nm + ", " + body, 0, prec),
                            "Subtype" => "{ " + nm + " // " + body + " }",
                            _ => "{" + nm + " | " + body + "}",
                        });
                        return;
                    }
                case "ite" when visible.Count == 3:
                    sb.Append(Wrap("if " + Sub(visible[0], names, 0) + " then " + Sub(visible[1], names, 0) + " else " + Sub(visible[2], names, 0), 0, prec));
                    return;
                case "Prod.mk" when visible.Count == 2:
                    sb.Append('(').Append(Sub(visible[0], names, 0)).Append(", ").Append(Sub(visible[1], names, 0)).Append(')');
                    return;
                case "Membership.mem" when visible.Count == 2:
                    {
                        // Lean 4.12 and later: mem (collection) (element); before: mem (element) (collection)
                        bool collectionFirst = FirstExplicitDomainIsSecondImplicit(c.Name);
                        Expr elem = collectionFirst ? visible[1] : visible[0];
                        Expr coll = collectionFirst ? visible[0] : visible[1];
                        sb.Append(Wrap(Sub(elem, names, 51) + " ∈ " + Sub(coll, names, 51), 50, prec));
                        return;
                    }
            }
            if (Binary.TryGetValue(name, out Op? op) && visible.Count == 2)
            {
                int lp = op.Assoc == 'l' ? op.Prec : op.Prec + 1;
                int rp = IsLeading(visible[1]) ? 0 : op.Assoc == 'r' ? op.Prec : op.Prec + 1;
                sb.Append(Wrap(Sub(visible[0], names, lp) + " " + op.Symbol + " " + Sub(visible[1], names, rp), op.Prec, prec));
                return;
            }
            if (Prefix.TryGetValue(name, out (string Symbol, int Prec) pre) && visible.Count == 1)
            {
                string arg = Sub(visible[0], names, pre.Prec < 0 ? 1024 : pre.Prec);
                sb.Append(pre.Prec < 0 ? arg + pre.Symbol : Wrap(pre.Symbol + arg, pre.Prec, prec));
                return;
            }
            if (visible.Count == 0)
            {
                sb.Append(Display(c.Name));
                return;
            }
            var pieces = new List<string> { Display(c.Name) };
            pieces.AddRange(visible.Select(a => Sub(a, names, 1024)));
            sb.Append(Wrap(string.Join(' ', pieces), 1023, prec));
            return;
        }
        var all = new List<string> { Sub(head, names, 1023) };
        all.AddRange(args.Select(a => Sub(a, names, 1024)));
        sb.Append(Wrap(string.Join(' ', all), 1023, prec));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Name, bool> _memOrder = new();

    /// <summary>For Membership.mem: whether the first explicit binder's type is the second implicit (the collection type).</summary>
    private bool FirstExplicitDomainIsSecondImplicit(Name n) => _memOrder.GetOrAdd(n, k =>
    {
        ConstantInfo? c = _find(k);
        int depth = 0;
        for (Expr t = c?.Type ?? Expr.Sort(Level.Zero); t is PiExpr p; t = p.Body, depth++)
        {
            if (p.Info == BinderInfo.Default)
            {
                return p.Domain is BVarExpr b && depth - 1 - b.Idx == 1;
            }
        }
        return true;
    });
}
