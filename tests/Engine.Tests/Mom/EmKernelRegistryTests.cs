// L8e Tier 0 — the registry: selection, both explicit directions, the refusal when neither kernel
// fits, and the reason string.
//
// These run with NO layout document, NO extractor and NO solver anywhere near them, which is the
// point of Choose taking two verdicts rather than geometry (the UI firewall makes that mandatory and
// it makes the rule cheap to pin).

using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom;

public class EmKernelRegistryTests
{
    private static readonly EmExtractorVerdict Accepts = EmExtractorVerdict.Yes;

    private static EmExtractorVerdict RefusesA() =>
        EmExtractorVerdict.No("The conductor on layer 1/0 has a bend at (17.1 mm, 2.9 mm).");

    private static EmExtractorVerdict RefusesB() =>
        EmExtractorVerdict.No("Geometry is drawn on 2 signal conductor layers.");

    // ── The capability flag the registry finally reads ────────────────────────────────────────

    [Fact]
    public void EveryRegisteredKernel_DeclaresTheCapabilityItsKindNeeds()
    {
        foreach (var k in EmKernelRegistry.Kernels)
        {
            var need = EmKernelRegistry.RequiredCapability(k.Kind);
            Assert.NotEqual(EmCapabilities.None, need);
            Assert.True(k.Capabilities.HasFlag(need),
                $"{k.Name} is registered for {k.Kind} but does not declare {need}.");
            Assert.Same(k, EmKernelRegistry.Describe(k.Kind));
        }
    }

    /// <summary>D1 — the registry names the ACTUAL kernels, not two hand-typed strings that can
    /// drift from them.</summary>
    [Fact]
    public void TheRegisteredNames_AreTheKernelsOwn()
    {
        Assert.Equal(new QuasiStaticKernel().Name, EmKernelRegistry.CrossSection.Name);
        Assert.Equal(new PlanarKernel().Name,      EmKernelRegistry.Planar.Name);
    }

    [Fact]
    public void Auto_IsARequestNotAKernel_AndDescribeSaysSo()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => EmKernelRegistry.Describe(EmAnalysisKind.Auto));
        Assert.Contains("Auto is a request", ex.Message, StringComparison.Ordinal);
    }

    // ── D2 — auto-selection, conservative, and it always says which and why ───────────────────

    [Fact]
    public void Auto_WhenTheCrossSectionExtractorAccepts_PicksKernelA_AndSaysWhy()
    {
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Auto, Accepts, Accepts);

        Assert.True(c.Ok);
        Assert.Equal(EmAnalysisKind.CrossSection, c.Kind);
        Assert.Equal(QuasiStaticKernel.KernelName, c.KernelName);
        // Owner request (2026-08-09): the reason names the analysis the way the DROPDOWN does, so
        // these assert against EmKernelRegistry's own labels rather than a hand-typed copy — a
        // reason that says "set Analysis to X" when the dropdown offers "Y" is worse than none.
        Assert.Contains(EmKernelRegistry.AutoChoiceLabel + " chose", c.Reason, StringComparison.Ordinal);
        // R-msh-8a's shape: name the thing, name the alternative.
        Assert.Contains(EmKernelRegistry.UniformLineChoiceLabel, c.Reason, StringComparison.Ordinal);
        Assert.Contains(EmKernelRegistry.PlanarChoiceLabel, c.Reason, StringComparison.Ordinal);
    }

    /// <summary>R-res-3's second half: auto never silently picks the SLOWER kernel when the cheaper
    /// one would have been valid.</summary>
    [Fact]
    public void Auto_NeverPicksThePlanarKernel_WhenTheCrossSectionKernelWouldHaveWorked()
    {
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Auto, Accepts, Accepts);
        Assert.NotEqual(EmAnalysisKind.Planar, c.Kind);
    }

    [Fact]
    public void Auto_WhenOnlyThePlanarExtractorAccepts_PicksKernelB_AndQuotesWhyAWasRefused()
    {
        var a = RefusesA();
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Auto, a, Accepts);

        Assert.True(c.Ok);
        Assert.Equal(EmAnalysisKind.Planar, c.Kind);
        Assert.Equal(PlanarKernel.KernelName, c.KernelName);
        Assert.Contains(a.Refusal!, c.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_WhenNeitherAccepts_RefusesQuotingBOTHRefusals()
    {
        var a = RefusesA();
        var b = RefusesB();
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Auto, a, b);

        Assert.False(c.Ok);
        Assert.NotNull(c.Refusal);
        Assert.Contains(a.Refusal!, c.Refusal!, StringComparison.Ordinal);
        Assert.Contains(b.Refusal!, c.Refusal!, StringComparison.Ordinal);
    }

    // ── R-res-3 — explicit stays explicit, in BOTH directions ─────────────────────────────────

    [Fact]
    public void ExplicitPlanar_IsHonouredEvenWhenTheCrossSectionKernelWouldHaveWorked()
    {
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Planar, Accepts, Accepts);

        Assert.True(c.Ok);
        Assert.Equal(EmAnalysisKind.Planar, c.Kind);
        Assert.Contains("explicitly", c.Reason, StringComparison.Ordinal);
        // …and the panel is told what Auto WOULD have done, so the cost is a choice, not a surprise.
        Assert.Contains($"{EmKernelRegistry.AutoChoiceLabel} would have picked it", c.Reason,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCrossSection_IsHonouredEvenWhenThePlanarKernelWouldAlsoHaveWorked()
    {
        var c = EmKernelRegistry.Choose(EmAnalysisKind.CrossSection, Accepts, Accepts);

        Assert.True(c.Ok);
        Assert.Equal(EmAnalysisKind.CrossSection, c.Kind);
        Assert.Contains("explicitly", c.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitCrossSection_ThatIsRefused_NamesThePlanarKernelAsTheWayForward()
    {
        var a = RefusesA();
        var c = EmKernelRegistry.Choose(EmAnalysisKind.CrossSection, a, Accepts);

        Assert.False(c.Ok);
        Assert.Equal(EmAnalysisKind.CrossSection, c.Kind);
        Assert.Contains(a.Refusal!, c.Refusal!, StringComparison.Ordinal);
        Assert.Contains($"set Analysis to \"{EmKernelRegistry.PlanarChoiceLabel}\"", c.Refusal!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPlanar_ThatIsRefused_NamesTheCrossSectionKernelAsTheWayForward()
    {
        var b = RefusesB();
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Planar, Accepts, b);

        Assert.False(c.Ok);
        Assert.Equal(EmAnalysisKind.Planar, c.Kind);
        Assert.Contains(b.Refusal!, c.Refusal!, StringComparison.Ordinal);
        Assert.Contains($"set Analysis to \"{EmKernelRegistry.UniformLineChoiceLabel}\"", c.Refusal!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPlanar_ThatIsRefused_WithNoAlternative_DoesNotInventOne()
    {
        var b = RefusesB();
        var c = EmKernelRegistry.Choose(EmAnalysisKind.Planar, RefusesA(), b);

        Assert.False(c.Ok);
        Assert.Equal(b.Refusal, c.Refusal);
    }

    /// <summary>R-res-1 — the reason is never empty, whatever the outcome, because it goes in the
    /// notes on EVERY run.</summary>
    [Theory]
    [InlineData(EmAnalysisKind.Auto)]
    [InlineData(EmAnalysisKind.CrossSection)]
    [InlineData(EmAnalysisKind.Planar)]
    public void EveryOutcome_CarriesANonEmptyReason(EmAnalysisKind requested)
    {
        foreach (var a in new[] { Accepts, RefusesA() })
            foreach (var b in new[] { Accepts, RefusesB() })
            {
                var c = EmKernelRegistry.Choose(requested, a, b);
                Assert.False(string.IsNullOrWhiteSpace(c.Reason));
                Assert.NotEqual(EmAnalysisKind.Auto, c.Kind);
                if (!c.Ok) Assert.False(string.IsNullOrWhiteSpace(c.Refusal));
            }
    }

    // ── R-res-2 — IEmKernel did not grow a planar overload, and PlanarProblem gained no base ──

    [Fact]
    public void KernelB_DoesNotImplementIEmKernel_AndPlanarProblemHasNoSharedBase()
    {
        Assert.False(typeof(IEmKernel).IsAssignableFrom(typeof(PlanarKernel)),
            "D1/L8b-D1: kernel B has its own entry point precisely because the two problems are " +
            "different types. Making it implement IEmKernel means resurrecting the base class L8b " +
            "rejected, or pushing a nullable-fields union through every call site.");

        Assert.Equal(typeof(object), typeof(PlanarProblem).BaseType);
        Assert.Equal(typeof(object), typeof(EmProblem).BaseType);

        // IEmKernel's own surface is unchanged: three members plus Name/Capabilities.
        var names = typeof(IEmKernel).GetMembers().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Solve", names.Where(n => n.Contains("Planar", StringComparison.Ordinal)));
    }
}
