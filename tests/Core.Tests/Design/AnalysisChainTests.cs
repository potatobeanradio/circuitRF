using CircuitRF.Core.Design;
using Xunit;

namespace CircuitRF.Core.Tests.Design;

/// <summary>
/// Gate tests for <see cref="AnalysisChain"/> (brief-sweep-revamp-2-dispatch).
/// Tests the pure resolver with synthetic TestBenches; no engine / no Avalonia.
/// Chain: SW_Vgs(Inner=SW_Vds) → SW_Vds(Inner=DC1) → DC1
/// </summary>
public sealed class AnalysisChainTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static (TestBench tb, DcAnalysis dc1, ParametricSweepAnalysis swVds, ParametricSweepAnalysis swVgs)
        MakeTb(bool dc1Enabled = true, bool swVdsEnabled = true, bool swVgsEnabled = true)
    {
        var dc1   = new DcAnalysis("DC1")   { Enabled = dc1Enabled };
        var swVds = new ParametricSweepAnalysis("SW_Vds", "Vds", [0.0, 5.0, 10.0], "DC1")
                    { Enabled = swVdsEnabled };
        var swVgs = new ParametricSweepAnalysis("SW_Vgs", "Vgs", [-3.0, -3.5], "SW_Vds")
                    { Enabled = swVgsEnabled };

        var tb = new TestBench("tb");
        tb.Analyses.Add(dc1);
        tb.Analyses.Add(swVds);
        tb.Analyses.Add(swVgs);
        return (tb, dc1, swVds, swVgs);
    }

    // ── All enabled ───────────────────────────────────────────────────────────

    [Fact]
    public void AllEnabled_ResolveEffectiveTop_ReturnsSelf()
    {
        var (tb, _, _, swVgs) = MakeTb();
        var top = AnalysisChain.ResolveEffectiveTop(swVgs, tb);
        Assert.Same(swVgs, top);
    }

    [Fact]
    public void AllEnabled_ResolveEffectiveInner_ReturnsSweep()
    {
        var (tb, _, swVds, _) = MakeTb();
        var inner = AnalysisChain.ResolveEffectiveInner("SW_Vds", tb);
        Assert.Same(swVds, inner);
    }

    [Fact]
    public void AllEnabled_IsChainRunnable_True()
    {
        var (tb, _, _, swVgs) = MakeTb();
        Assert.True(AnalysisChain.IsChainRunnable(swVgs, tb));
    }

    // ── Inner (SW_Vds) disabled — collapses to DC1 ───────────────────────────

    [Fact]
    public void InnerSweepDisabled_ResolveEffectiveInner_SkipsToBase()
    {
        var (tb, dc1, _, _) = MakeTb(swVdsEnabled: false);
        var inner = AnalysisChain.ResolveEffectiveInner("SW_Vds", tb);
        Assert.Same(dc1, inner);
    }

    [Fact]
    public void InnerSweepDisabled_IsChainRunnable_True()
    {
        var (tb, _, _, swVgs) = MakeTb(swVdsEnabled: false);
        Assert.True(AnalysisChain.IsChainRunnable(swVgs, tb));
    }

    // ── Outer (SW_Vgs) disabled — top collapses to SW_Vds ────────────────────

    [Fact]
    public void OuterSweepDisabled_ResolveEffectiveTop_SkipsToInner()
    {
        var (tb, _, swVds, swVgs) = MakeTb(swVgsEnabled: false);
        var top = AnalysisChain.ResolveEffectiveTop(swVgs, tb);
        Assert.Same(swVds, top);
    }

    // ── Base (DC1) disabled — whole chain inert ───────────────────────────────

    [Fact]
    public void BaseDisabled_IsChainRunnable_False()
    {
        var (tb, _, _, swVgs) = MakeTb(dc1Enabled: false);
        Assert.False(AnalysisChain.IsChainRunnable(swVgs, tb));
    }

    // ── Both sweeps disabled — top collapses all the way to DC1 ──────────────

    [Fact]
    public void BothSweepsDisabled_ResolveEffectiveTop_ReturnsBase()
    {
        var (tb, dc1, _, swVgs) = MakeTb(swVdsEnabled: false, swVgsEnabled: false);
        var top = AnalysisChain.ResolveEffectiveTop(swVgs, tb);
        Assert.Same(dc1, top);
    }

    [Fact]
    public void BothSweepsDisabled_IsChainRunnable_DependsOnBaseEnabled()
    {
        var (tbOn,  _, _, swVgsOn)  = MakeTb(swVdsEnabled: false, swVgsEnabled: false, dc1Enabled: true);
        var (tbOff, _, _, swVgsOff) = MakeTb(swVdsEnabled: false, swVgsEnabled: false, dc1Enabled: false);

        Assert.True (AnalysisChain.IsChainRunnable(swVgsOn,  tbOn));
        Assert.False(AnalysisChain.IsChainRunnable(swVgsOff, tbOff));
    }
}
