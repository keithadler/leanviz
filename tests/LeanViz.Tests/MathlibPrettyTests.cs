using LeanViz;
using Tenet.Kernel;
using Tenet.Olean;
using Xunit;

namespace LeanViz.Tests;

/// <summary>
/// The printer against Mathlib itself, when LEANVIZ_MATHLIB points at a built checkout (the one the
/// bundle is generated from). Each expectation was compared by eye with the Mathlib docs, which are Lean's own
/// rendering, and matches them up to namespaces the docs abbreviate because they are open on that page.
/// </summary>
public class MathlibPrettyTests
{
    private static string? Mathlib()
    {
        string? dir = System.Environment.GetEnvironmentVariable("LEANVIZ_MATHLIB");
        return !string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, ".lake", "build", "lib", "lean")) ? dir : null;
    }

    private static string Statement(string dir, string module, string name)
    {
        string path = Path.Combine(dir, ".lake", "build", "lib", "lean", module.Replace('.', Path.DirectorySeparatorChar) + ".olean");
        var search = new LeanSearchPath();
        search.AddFromEnvironment();
        search.AddAroundOleanFile(path);
        using var checker = new OleanChecker(search);
        Name m = search.ModuleNameOf(path);
        checker.Load([(m, path)]);
        search.AddToolchainFor(checker.Modules[m].LeanVersion);
        checker.Load([(m, path)]);
        ConstantInfo? c = checker.Resolve(Name.Parse(name));
        Assert.NotNull(c);
        return new Pretty(checker.Resolve).Statement(c);
    }

    [Theory]
    [InlineData("Mathlib.Algebra.Module.Submodule.Bilinear", "Submodule.map₂_le",
        "∀ {R : Type u_1} {M : Type u_2} {N : Type u_3} {P : Type u_4} [CommSemiring R] [AddCommMonoid M] [AddCommMonoid N] [AddCommMonoid P] [Module R M] [Module R N] [Module R P] {f : M →ₗ[R] N →ₗ[R] P} {p : Submodule R M} {q : Submodule R N} {r : Submodule R P}, Submodule.map₂ f p q ≤ r ↔ ∀ m ∈ p, ∀ n ∈ q, (f m) n ∈ r")]
    [InlineData("Mathlib.LinearAlgebra.Basis.Defs", "Module.Basis.repr",
        "∀ {ι : Type u_1} {R : Type u_3} {M : Type u_4} [Semiring R] [AddCommMonoid M] [Module R M], Module.Basis ι R M → M ≃ₗ[R] (ι →₀ R)")]
    [InlineData("Mathlib.Algebra.Algebra.Hom", "AlgHom.comp",
        "∀ {R : Type u} {A : Type v} {B : Type w} {C : Type u₁} [CommSemiring R] [Semiring A] [Semiring B] [Semiring C] [Algebra R A] [Algebra R B] [Algebra R C], (B →ₐ[R] C) → (A →ₐ[R] B) → A →ₐ[R] C")]
    [InlineData("Mathlib.Analysis.Normed.Group.Basic", "norm_add_le",
        "∀ {E : Type u_4} [SeminormedAddGroup E] (a b : E), ‖a + b‖ ≤ ‖a‖ + ‖b‖")]
    [InlineData("Mathlib.CategoryTheory.Iso", "CategoryTheory.Iso.symm",
        "∀ {C : Type u} [CategoryTheory.Category C] {X Y : C}, (X ≅ Y) → Y ≅ X")]
    [InlineData("Mathlib.NumberTheory.Zsqrtd.Basic", "Zsqrtd.ext_iff",
        "∀ {d : ℤ} {x y : Zsqrtd d}, x = y ↔ x.re = y.re ∧ x.im = y.im")]
    [InlineData("Mathlib.Analysis.Calculus.Deriv.Basic", "HasDerivAt",
        "∀ {𝕜 : Type u} [NontriviallyNormedField 𝕜] {F : Type v} [AddCommGroup F] [Module 𝕜 F] [TopologicalSpace F] [ContinuousSMul 𝕜 F], (𝕜 → F) → F → 𝕜 → Prop")]
    public void StatementsMatchTheDocs(string module, string name, string expected)
    {
        string? dir = Mathlib();
        if (dir is null)
        {
            return;
        }
        Assert.Equal(expected, Statement(dir, module, name));
    }
}
