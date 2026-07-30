// ================================================================
//  StabilityCardTests.cs  —  brief-stability-passivity-touchstone.md §8 gates 4/4a/5
//
//  Gate 5  — plot-kind gating (R-stb-5): circles only on Smith/Polar, scalars only on
//            rectangular; the unavailable option is DISABLED WITH A REASON, not hidden and not
//            silently producing an empty trace.
//  Gate 4  — ordered port selection at the Trace level (R-stb-3a): swapping input/output swaps
//            µ and µ′, so (1,2) and (2,1) are different selections.
//  Gate 4a — any N ≥ 2 offers the metrics (R-stb-3b); N = 1 offers passivity only.
//  R-stb-4 — the termination assumption is stated when a pair is extracted from an N-port.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class StabilityCardTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────

    private static Mat<Complex> Amp2Port(double gain = 3.2)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.60, -0.30);
        m[0, 1] = new Complex(0.05,  0.02);
        m[1, 0] = new Complex(gain,  1.10);
        m[1, 1] = new Complex(0.45, -0.25);
        return m;
    }

    /// <summary>N-port SNP with a 2-port device embedded across (a,b), 0-based.</summary>
    private static SNP MakeSnp(int n, int a = 0, int b = 1, double gain = 3.2)
    {
        var dev = Amp2Port(gain);
        var m   = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = new Complex(0.1, 0);
        if (n >= 2)
        {
            m[a, a] = dev[0, 0]; m[a, b] = dev[0, 1];
            m[b, a] = dev[1, 0]; m[b, b] = dev[1, 1];
        }
        return new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    private static Trace DerivedTrace(SNP snp, DerivedParameters d, int inPort = 1, int outPort = 2)
        => new(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Derived = d, InputPort = inPort, OutputPort = outPort,
        };

    // ── Gate 5: plot-kind gating, disabled WITH A REASON (R-stb-5) ──────────────

    [Theory]
    [InlineData(DerivedParameters.Mu)]
    [InlineData(DerivedParameters.MuPrime)]
    [InlineData(DerivedParameters.K)]
    [InlineData(DerivedParameters.DeltaMag)]
    [InlineData(DerivedParameters.MaxGain)]
    [InlineData(DerivedParameters.Passivity)]
    public void Gate5_ScalarMetrics_EnabledOnRect_DisabledWithReasonOnSmith(DerivedParameters d)
    {
        Assert.True(d.IsScalarVsFrequency());

        var onRect  = new TraceDataItem(null!, d, PlotType.Rect,  omitFilePrefix: true);
        var onSmith = new TraceDataItem(null!, d, PlotType.Smith, omitFilePrefix: true);

        Assert.True(onRect.IsEnabled);
        Assert.Null(onRect.DisabledReason);

        Assert.False(onSmith.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(onSmith.DisabledReason));
        Assert.Contains("rectangular", onSmith.DisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DerivedParameters.SourceStabilityCircle)]
    [InlineData(DerivedParameters.LoadStabilityCircle)]
    public void Gate5_StabilityCircles_EnabledOnSmith_DisabledWithReasonOnRect(DerivedParameters d)
    {
        Assert.True(d.IsCircleLocus());

        var onSmith = new TraceDataItem(null!, d, PlotType.Smith, omitFilePrefix: true);
        var onRect  = new TraceDataItem(null!, d, PlotType.Rect,  omitFilePrefix: true);

        Assert.True(onSmith.IsEnabled);
        Assert.Null(onSmith.DisabledReason);

        Assert.False(onRect.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(onRect.DisabledReason));
        Assert.Contains("Smith", onRect.DisabledReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 4: the port selection is ORDERED (R-stb-3a) ───────────────────────

    [Fact]
    public void Gate4_SwappingInputAndOutput_SwapsMuAndMuPrime_AtTheTraceLevel()
    {
        var snp = MakeSnp(2);

        double Y(DerivedParameters d, int i, int o)
        {
            var t = DerivedTrace(snp, d, i, o);
            t.BuildPath(PlotType.Rect, FreqUnit.GHz);
            return t.Points.Single().Y;
        }

        double mu12  = Y(DerivedParameters.Mu,      1, 2);
        double mup12 = Y(DerivedParameters.MuPrime, 1, 2);
        double mu21  = Y(DerivedParameters.Mu,      2, 1);
        double mup21 = Y(DerivedParameters.MuPrime, 2, 1);

        Assert.Equal(mup12, mu21,  5);
        Assert.Equal(mu12,  mup21, 5);
        Assert.True(Math.Abs(mu12 - mu21) > 1e-5,
            "(1,2) and (2,1) must be different selections, not an unordered pair");
    }

    [Fact]
    public void Gate4_MuOnAFourPort_PicksTheSelectedDevice()
    {
        // Device A across ports 1-2 (gain 3.2); a different device across ports 3-4 (gain 1.4).
        var m = new Mat<Complex>(4, 4);
        var devA = Amp2Port(3.2); var devB = Amp2Port(1.4);
        m[0, 0] = devA[0, 0]; m[0, 1] = devA[0, 1]; m[1, 0] = devA[1, 0]; m[1, 1] = devA[1, 1];
        m[2, 2] = devB[0, 0]; m[2, 3] = devB[0, 1]; m[3, 2] = devB[1, 0]; m[3, 3] = devB[1, 1];
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        double MuFor(int i, int o)
        {
            var t = DerivedTrace(snp, DerivedParameters.Mu, i, o);
            t.BuildPath(PlotType.Rect, FreqUnit.GHz);
            return t.Points.Single().Y;
        }

        Assert.Equal(RFNetwork.StabilityMu(devA), MuFor(1, 2), 5);
        Assert.Equal(RFNetwork.StabilityMu(devB), MuFor(3, 4), 5);
        Assert.True(Math.Abs(MuFor(1, 2) - MuFor(3, 4)) > 1e-5);
    }

    // ── Gate 4a: any N ≥ 2, no per-N branching (R-stb-3b) ──────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    public void Gate4a_AnyN_ProducesAMuPointForTheSelectedPair(int n)
    {
        // Embed across the LAST and SECOND ports so an implementation assuming 1/2 cannot pass.
        int a = n - 1, b = 1;
        var snp = MakeSnp(n, a, b);

        var t = DerivedTrace(snp, DerivedParameters.Mu, a + 1, b + 1);
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.Single(t.Points);
        Assert.Equal(RFNetwork.StabilityMu(Amp2Port()), t.Points[0].Y, 5);
    }

    [Fact]
    public void Gate4a_OnePort_TwoPortMetricProducesNothing_PassivityStillWorks()
    {
        var m = new Mat<Complex>(1, 1);
        m[0, 0] = new Complex(0.5, 0);
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        var mu = DerivedTrace(snp, DerivedParameters.Mu);
        mu.BuildPath(PlotType.Rect, FreqUnit.GHz);
        Assert.Empty(mu.Points);                       // refused, never a crash

        var pass = DerivedTrace(snp, DerivedParameters.Passivity);
        pass.BuildPath(PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(0.5, pass.Points.Single().Y, 5);  // |S11| — valid at N = 1 (R-stb-6)
    }

    [Fact]
    public void InvalidPortPair_YieldsNoPoints_RatherThanThrowing()
    {
        var snp = MakeSnp(2);
        var t = DerivedTrace(snp, DerivedParameters.Mu, inPort: 1, outPort: 1);   // same port
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        Assert.Empty(t.Points);
    }

    // ── R-stb-6: whole-network vs extracted-pair passivity ────────────────────

    [Fact]
    public void Passivity_WholeNetworkVersusExtractedPair_DiffersWhereExpected()
    {
        double a = Math.Pow(10.0, -6.0 / 20.0);
        var m = new Mat<Complex>(4, 4);
        m[0, 1] = new Complex(a, 0); m[1, 0] = new Complex(a, 0);      // passive on 1-2
        var dev = Amp2Port();
        m[2, 2] = dev[0, 0]; m[2, 3] = dev[0, 1];
        m[3, 2] = dev[1, 0]; m[3, 3] = dev[1, 1];                      // active on 3-4
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        var whole = DerivedTrace(snp, DerivedParameters.Passivity);
        whole.PassivityWholeNetwork = true;
        whole.BuildPath(PlotType.Rect, FreqUnit.GHz);

        var pair = DerivedTrace(snp, DerivedParameters.Passivity, 1, 2);
        pair.PassivityWholeNetwork = false;
        pair.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.True(whole.Points.Single().Y > 1.0);          // full network is active
        Assert.True(pair.Points.Single().Y  <= 1.0 + 1e-6);  // extracted pair is passive
    }

    // ── Metric-kind classification (drives every gate above) ──────────────────

    [Fact]
    public void PassivityIsTheOnlyMetricThatDoesNotNeedAPortPair()
    {
        Assert.False(DerivedParameters.Passivity.NeedsPortPair());
        foreach (var d in new[]
                 {
                     DerivedParameters.Mu, DerivedParameters.MuPrime, DerivedParameters.K,
                     DerivedParameters.DeltaMag, DerivedParameters.MaxGain,
                     DerivedParameters.SourceStabilityCircle, DerivedParameters.LoadStabilityCircle,
                 })
            Assert.True(d.NeedsPortPair(), $"{d} is a 2-port formula and must need a pair");
    }

    /// <summary>
    /// The .cdd enum is persisted numerically, so appending must not renumber existing members —
    /// a saved display would otherwise silently change which metric it plots.
    /// </summary>
    [Fact]
    public void DerivedParameterOrdinals_AreStable_ForPreExistingMembers()
    {
        Assert.Equal(0, (int)DerivedParameters.None);
        Assert.Equal(1, (int)DerivedParameters.SourceStabilityCircle);
        Assert.Equal(2, (int)DerivedParameters.LoadStabilityCircle);
        Assert.Equal(3, (int)DerivedParameters.MaxGain);
        Assert.Equal(4, (int)DerivedParameters.Mu);
        Assert.Equal(5, (int)DerivedParameters.MuPrime);
    }
}
