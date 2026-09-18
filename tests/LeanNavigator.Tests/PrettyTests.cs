using Tenet.Kernel;
using Tenet.Olean;
using Xunit;

namespace LeanNavigator.Tests;

/// <summary>
/// The printer against real terms from the toolchain's compiled core library, when an elan toolchain is installed
/// (any version: the statements below have been stable for years). Each case is one notation rule.
/// </summary>
public class PrettyTests
{
    private static string? ToolchainLib()
    {
        string? env = System.Environment.GetEnvironmentVariable("TENET_LEAN_LIB");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }
        string toolchains = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".elan", "toolchains");
        if (!Directory.Exists(toolchains))
        {
            return null;
        }
        return Directory.GetDirectories(toolchains).OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "lib", "lean"))
            .FirstOrDefault(lib => File.Exists(Path.Combine(lib, "Init", "Prelude.olean")));
    }

    private static (OleanChecker Checker, Pretty Pretty)? Open()
    {
        string? lib = ToolchainLib();
        if (lib is null)
        {
            return null;
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

    [Theory]
    [InlineData("Nat.add_comm", "∀ (n m : ℕ), n + m = m + n")]
    [InlineData("List.map_append", "∀ {α : Type u_1} {β : Type u_2} {f : α → β} {l₁ l₂ : List α}, List.map f (l₁ ++ l₂) = List.map f l₁ ++ List.map f l₂")]
    [InlineData("List.mem_append", "∀ {α : Type u_1} {a : α} {s t : List α}, a ∈ s ++ t ↔ a ∈ s ∨ a ∈ t")]
    [InlineData("Nat.rec", "∀ {motive : ℕ → Sort u}, motive Nat.zero → (∀ (n : ℕ), motive n → motive n.succ) → ∀ (t : ℕ), motive t")]
    [InlineData("Exists.intro", "∀ {α : Sort u} {p : α → Prop} (w : α), p w → Exists p")]
    [InlineData("Eq", "∀ {α : Sort u_1}, α → α → Prop")]
    public void StatementsReadLikeLean(string name, string expected)
    {
        var opened = Open();
        if (opened is null)
        {
            return;
        }
        using OleanChecker checker = opened.Value.Checker;
        ConstantInfo? c = checker.Resolve(Name.Parse(name));
        Assert.NotNull(c);
        Assert.Equal(expected, opened.Value.Pretty.Statement(c));
    }

    [Fact]
    public void HygienicAndPrivateNamesDisplayLikeLean()
    {
        Assert.Equal("inst✝", Pretty.Display(Name.Of("inst", "_@", "Mathlib", "Foo").Num(123).Str("_hygCtx").Str("_hyg").Num(3)));
        Assert.Equal("Foo.bar", Pretty.Display(Name.Of("_private", "Mathlib", "X").Num(0).Str("Foo").Str("bar")));
        Assert.Equal("List.map", Pretty.Display(Name.Of("List", "map")));
        Assert.Equal("«term.x»", Pretty.Display(Name.Of("term.x")));
        Assert.Equal("term_+_", Pretty.Display(Name.Of("term_+_")));
    }
}
