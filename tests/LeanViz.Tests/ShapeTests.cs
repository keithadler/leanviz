using LeanViz;
using Tenet.Kernel;
using Tenet.Olean;
using Xunit;

namespace LeanViz.Tests;

/// <summary>
/// A statement split the way a paper states a theorem. The division has to be right or the page misleads: a
/// hypothesis presented as part of the setting reads as something assumed about the world rather than something
/// the theorem requires of its input.
/// </summary>
public class ShapeTests
{
    private static (OleanChecker Checker, Pretty Pretty)? Open()
    {
        string? env = System.Environment.GetEnvironmentVariable("TENET_LEAN_LIB");
        string lib = env ?? "";
        if (string.IsNullOrEmpty(lib) || !Directory.Exists(lib))
        {
            string toolchains = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".elan", "toolchains");
            if (!Directory.Exists(toolchains))
            {
                return null;
            }
            lib = Directory.GetDirectories(toolchains).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "lib", "lean"))
                .FirstOrDefault(l => File.Exists(Path.Combine(l, "Init.olean"))) ?? "";
            if (lib.Length == 0)
            {
                return null;
            }
        }
        string init = Path.Combine(lib, "Init.olean");
        var search = new LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(init);
        var checker = new OleanChecker(search);
        Name m = search.ModuleNameOf(init);
        checker.Load([(m, init)]);
        search.AddToolchainFor(checker.Modules[m].LeanVersion);
        checker.Load([(m, init)]);
        return (checker, new Pretty(checker.Resolve));
    }

    [Fact]
    public void ASimpleTheoremIsAllSettingAndConclusion()
    {
        var o = Open();
        if (o is null) { return; }
        using OleanChecker checker = o.Value.Checker;
        ConstantInfo c = checker.Resolve(Name.Parse("Nat.add_comm"))!;
        Pretty.Shape shape = o.Value.Pretty.ShapeOf(c);
        Assert.Equal(["n m : ℕ"], shape.Setting);
        Assert.Empty(shape.Hypotheses);
        Assert.Equal("n + m = m + n", shape.Conclusion);
    }

    [Fact]
    public void AnImplicationPutsItsAntecedentInTheHypotheses()
    {
        var o = Open();
        if (o is null) { return; }
        using OleanChecker checker = o.Value.Checker;
        ConstantInfo c = checker.Resolve(Name.Parse("Nat.succ_le_succ"))!;
        Pretty.Shape shape = o.Value.Pretty.ShapeOf(c);
        Assert.Single(shape.Hypotheses);
        Assert.Contains("≤", shape.Hypotheses[0]);
        Assert.Contains("≤", shape.Conclusion);
        Assert.DoesNotContain(shape.Setting, s => s.Contains("≤"));
    }

    [Fact]
    public void ATypeBinderIsSettingAndNotAHypothesis()
    {
        var o = Open();
        if (o is null) { return; }
        using OleanChecker checker = o.Value.Checker;
        ConstantInfo c = checker.Resolve(Name.Parse("List.map_append"))!;
        Pretty.Shape shape = o.Value.Pretty.ShapeOf(c);
        Assert.Contains(shape.Setting, s => s.Contains("Type"));
        Assert.Empty(shape.Hypotheses);
        Assert.Contains("List.map", shape.Conclusion);
    }

    [Fact]
    public void ThePiecesReassembleIntoTheWholeStatement()
    {
        var o = Open();
        if (o is null) { return; }
        using OleanChecker checker = o.Value.Checker;
        foreach (string name in new[] { "Nat.add_comm", "List.mem_append", "Nat.succ_le_succ", "Nat.rec" })
        {
            ConstantInfo c = checker.Resolve(Name.Parse(name))!;
            Pretty.Shape shape = o.Value.Pretty.ShapeOf(c);
            string flat = o.Value.Pretty.Statement(c);
            // every hypothesis and the conclusion must appear in the one-line form: the split reorders nothing
            Assert.Contains(shape.Conclusion, flat);
            foreach (string h in shape.Hypotheses)
            {
                Assert.Contains(h.Contains(" : ") ? h[(h.IndexOf(" : ") + 3)..] : h, flat);
            }
        }
    }
}
