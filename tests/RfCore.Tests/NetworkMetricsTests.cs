// ================================================================
//  NetworkMetricsTests.cs  —  brief-stability-passivity-touchstone.md §8 gates 2,3,4,4a,6
//
//  Gate 2  — cube path and SNP path produce IDENTICAL μ/μ′/K/|Δ| for the same 2-port data.
//            This is the headline: it proves ONE implementation is in use (R-stb-1).
//  Gate 3  — per-port COMPLEX references renormalize correctly (R-stb-2), asserted against a
//            hand-renormalized reference. A uniform-50 Ω test would not exercise this at all.
//  Gate 4  — an N-port holding two devices gives different, correct μ for (1,2) vs (3,4), and
//            swapping to (2,1) swaps μ/μ′ — proving the selection is ORDERED (R-stb-3a).
//  Gate 4a — 3-, 5- and 12-port travel the same path with no per-N branching (R-stb-3b);
//            N = 1 is refused for the 2-port metrics.
//  Gate 6  — passivity (R-stb-6), including whole-network ≠ extracted sub-matrix.
// ================================================================

using System;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public class NetworkMetricsTests
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

    /// <summary>DataSet with an [freq,i,j] S cube (one frequency) plus a per-port Z0 cube.</summary>
    private static DataSet MakeSDataSet(Mat<Complex> m, Complex[] z0PerPort,
                                        double freq = 1e9, bool noZ0 = false)
    {
        int n = m.RowCount;
        var portVals = new double[n];
        for (int p = 0; p < n; p++) portVals[p] = p + 1;

        var data = new Complex[n * n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            data[i * n + j] = m[i, j];

        var ds = new DataSet();
        ds.Add("S", new DataCube(
            new[] { new Axis("freq", new[] { freq }, "Hz"),
                    new Axis("i", portVals, "port"),
                    new Axis("j", portVals, "port") },
            data));
        if (!noZ0) ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort));
        return ds;
    }

    private static Complex[] Uniform(int n, Complex z0)
    {
        var a = new Complex[n];
        for (int i = 0; i < n; i++) a[i] = z0;
        return a;
    }

    /// <summary>Block-diagonal N-port holding two independent 2-port devices.</summary>
    private static Mat<Complex> TwoDevices4Port(Mat<Complex> devA, Mat<Complex> devB)
    {
        var m = new Mat<Complex>(4, 4);
        // device A on ports 1,2 ; device B on ports 3,4 — no cross coupling
        m[0, 0] = devA[0, 0]; m[0, 1] = devA[0, 1]; m[1, 0] = devA[1, 0]; m[1, 1] = devA[1, 1];
        m[2, 2] = devB[0, 0]; m[2, 3] = devB[0, 1]; m[3, 2] = devB[1, 0]; m[3, 3] = devB[1, 1];
        return m;
    }

    // ── Gate 2: cube path ≡ SNP path (R-stb-1) ──────────────────────────────────

    [Theory]
    [InlineData(50.0)]
    [InlineData(75.0)]   // a 75 Ω part — catches any hardcoded 50 in the cube path
    public void Gate2_SameTwoPortData_CubePathMatchesSnpPath_ForEveryMetric(double z0Real)
    {
        var m   = Amp2Port();
        var z0  = new Complex(z0Real, 0);
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, z0);
        var ds  = MakeSDataSet(m, Uniform(2, z0));

        Assert.Equal(RFNetwork.StabilityMu(snp)[0],
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu,       1, 2, out _)[0], 12);
        Assert.Equal(RFNetwork.StabilityMuPrime(snp)[0],
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MuPrime,  1, 2, out _)[0], 12);
        Assert.Equal(RFNetwork.MaxGain(snp)[0],
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MaxGain,  1, 2, out _)[0], 12);

        var (k, _, _, delta, _) = RFNetwork.StabilityK(snp);
        Assert.Equal(k[0],     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.K,        1, 2, out _)[0], 12);
        Assert.Equal(delta[0], NetworkMetrics.TwoPortMetric(ds, NetworkMetric.DeltaMag, 1, 2, out _)[0], 12);
    }

    [Fact]
    public void Gate2_PassivityAlsoMatchesTheSnpPath()
    {
        var m  = Amp2Port();
        var ds = MakeSDataSet(m, Uniform(2, new Complex(50, 0)));
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        Assert.Equal(RFNetwork.Passivity(snp)[0], NetworkMetrics.PassivityFull(ds, out _)[0], 12);
    }

    // ── Gate 3: per-port COMPLEX references (R-stb-2) ───────────────────────────

    [Fact]
    public void Gate3_PerPortComplexZ0_MatchesHandRenormalizedReference()
    {
        var m  = Amp2Port();
        var z0 = new[] { new Complex(40.0, 8.0), new Complex(60.0, -15.0) };  // per-port AND complex
        var ds = MakeSDataSet(m, z0);

        // Hand oracle: renormalize the 2-port from its own per-port references to a uniform real
        // reference (the input port's real part), then use the per-matrix overloads directly.
        var target = new Complex(z0[0].Real, 0.0);
        var expected = RFNetwork.SToS(m, z0, new[] { target, target });

        Assert.Equal(RFNetwork.StabilityMu(expected),
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu,      1, 2, out _)[0], 10);
        Assert.Equal(RFNetwork.StabilityMuPrime(expected),
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MuPrime, 1, 2, out _)[0], 10);
        Assert.Equal(RFNetwork.MaxGain(expected),
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MaxGain, 1, 2, out _)[0], 10);
    }

    /// <summary>The renormalization must not be skippable — pin that it changes the answer.</summary>
    [Fact]
    public void Gate3_RenormalizationIsNotANoOp_ForPerPortComplexReferences()
    {
        var m  = Amp2Port();
        var z0 = new[] { new Complex(40.0, 8.0), new Complex(60.0, -15.0) };
        var ds = MakeSDataSet(m, z0);

        double naive = RFNetwork.StabilityMu(m);   // what skipping the renorm would give
        double real_ = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 1, 2, out _)[0];

        Assert.True(Math.Abs(naive - real_) > 1e-6,
            $"per-port complex renorm must change μ (naive={naive}, renormalized={real_})");
    }

    [Fact]
    public void Gate3_UniformRealZ0_IsAnExactIdentity_NoRenormNoise()
    {
        var m  = Amp2Port();
        var ds = MakeSDataSet(m, Uniform(2, new Complex(50, 0)));
        Assert.Equal(RFNetwork.StabilityMu(m),
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 1, 2, out _)[0]);
    }

    [Fact]
    public void MissingZ0Cube_FallsBackTo50Ohm_LikeToSnp()
    {
        var m  = Amp2Port();
        var ds = MakeSDataSet(m, Uniform(2, new Complex(50, 0)), noZ0: true);
        Assert.False(ds.Contains("Z0"));
        Assert.Equal(new Complex(50, 0), NetworkMetrics.ReadZ0(ds, 2)[0]);
        Assert.Equal(RFNetwork.StabilityMu(m),
                     NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 1, 2, out _)[0]);
    }

    // ── Gate 4: ordered port selection on a multi-device N-port (R-stb-3/3a) ────

    [Fact]
    public void Gate4_TwoDevicesInOneRun_EachPortPairGivesItsOwnDevicesMu()
    {
        var devA = Amp2Port(gain: 3.2);
        var devB = Amp2Port(gain: 1.4);          // a genuinely different device
        var ds   = MakeSDataSet(TwoDevices4Port(devA, devB), Uniform(4, new Complex(50, 0)));

        double muA = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 1, 2, out _)[0];
        double muB = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 3, 4, out _)[0];

        // Each pair must equal that device computed standalone …
        Assert.Equal(RFNetwork.StabilityMu(devA), muA, 12);
        Assert.Equal(RFNetwork.StabilityMu(devB), muB, 12);
        // … and the two devices must be distinguishable, or the test proves nothing.
        Assert.True(Math.Abs(muA - muB) > 1e-6);
    }

    [Fact]
    public void Gate4_SwappingInputAndOutput_SwapsMuAndMuPrime_TheSelectionIsOrdered()
    {
        var m  = Amp2Port();
        var ds = MakeSDataSet(m, Uniform(2, new Complex(50, 0)));

        double mu12  = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu,      1, 2, out _)[0];
        double mup12 = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MuPrime, 1, 2, out _)[0];
        double mu21  = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu,      2, 1, out _)[0];
        double mup21 = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.MuPrime, 2, 1, out _)[0];

        // Reversing the port roles exchanges the load- and source-stability factors.
        Assert.Equal(mup12, mu21,  12);
        Assert.Equal(mu12,  mup21, 12);
        // And (1,2) is genuinely NOT the same selection as (2,1).
        Assert.True(Math.Abs(mu12 - mu21) > 1e-6,
            "μ(1,2) and μ(2,1) must differ — otherwise the pair is being treated as unordered");
    }

    // ── Gate 4a: any N ≥ 2, no per-N branching (R-stb-3b) ──────────────────────

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]     // > 8, so a hardcoded 2/4 cannot pass
    public void Gate4a_AnyN_TakesTheSamePath_AndMatchesTheEmbeddedTwoPort(int n)
    {
        var dev = Amp2Port();
        var m   = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = new Complex(0.1, 0);   // benign self-reflection
        // Embed the device across the LAST port and the second port — neither is index 0, and they
        // stay distinct for every n ≥ 3, so an implementation assuming ports 1/2 cannot pass.
        int a = n - 1, b = 1;
        m[a, a] = dev[0, 0]; m[a, b] = dev[0, 1]; m[b, a] = dev[1, 0]; m[b, b] = dev[1, 1];

        var ds = MakeSDataSet(m, Uniform(n, new Complex(50, 0)));
        double mu = NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, a + 1, b + 1, out _)[0];

        Assert.Equal(NetworkMetrics.PortCount(ds), n);
        Assert.Equal(RFNetwork.StabilityMu(dev), mu, 12);
    }

    [Fact]
    public void Gate4a_OnePort_RefusesTwoPortMetrics_ButPassivityStillWorks()
    {
        var m = new Mat<Complex>(1, 1);
        m[0, 0] = new Complex(0.5, 0.0);
        var ds = MakeSDataSet(m, Uniform(1, new Complex(50, 0)));

        Assert.Throws<ArgumentException>(
            () => NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, 1, 2, out _));
        // Passivity is defined at N = 1 (|S11|) — the 2-port restriction belongs to the stability
        // metrics, not to passivity (R-stb-6).
        Assert.Equal(0.5, NetworkMetrics.PassivityFull(ds, out _)[0], 12);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    public void InvalidPortSelections_AreRejected(int inPort, int outPort)
    {
        var ds = MakeSDataSet(Amp2Port(), Uniform(2, new Complex(50, 0)));
        Assert.ThrowsAny<ArgumentException>(
            () => NetworkMetrics.TwoPortMetric(ds, NetworkMetric.Mu, inPort, outPort, out _));
    }

    // ── Gate 6: passivity, whole-network vs extracted pair (R-stb-6) ───────────

    [Fact]
    public void Gate6_PassiveNetwork_NeverExceedsOne_ActiveDoes()
    {
        double a = Math.Pow(10.0, -3.0 / 20.0);
        var passive = new Mat<Complex>(2, 2);
        passive[0, 1] = new Complex(a, 0); passive[1, 0] = new Complex(a, 0);

        var dsPassive = MakeSDataSet(passive,     Uniform(2, new Complex(50, 0)));
        var dsActive  = MakeSDataSet(Amp2Port(),  Uniform(2, new Complex(50, 0)));

        Assert.True(NetworkMetrics.PassivityFull(dsPassive, out _)[0] <= 1.0 + 1e-12);
        Assert.True(NetworkMetrics.PassivityFull(dsActive,  out _)[0] >  1.0);
    }

    [Fact]
    public void Gate6_WholeNetworkPassivity_DiffersFromExtractedPair()
    {
        // Ports 1,2 hold a passive attenuator; ports 3,4 hold an active device. The extracted
        // (1,2) sub-matrix therefore tests PASSIVE while the full 4-port does not — the reason the
        // card has to say when a pair was extracted.
        double a = Math.Pow(10.0, -6.0 / 20.0);
        var passive = new Mat<Complex>(2, 2);
        passive[0, 1] = new Complex(a, 0); passive[1, 0] = new Complex(a, 0);

        var ds = MakeSDataSet(TwoDevices4Port(passive, Amp2Port()), Uniform(4, new Complex(50, 0)));

        double pair = NetworkMetrics.PassivityPair(ds, 1, 2, out _)[0];
        double full = NetworkMetrics.PassivityFull(ds, out _)[0];

        Assert.True(pair <= 1.0 + 1e-12, $"extracted (1,2) should be passive, got {pair}");
        Assert.True(full >  1.0,         $"full 4-port should be active, got {full}");
        Assert.True(full > pair);
    }

    [Fact]
    public void Gate6_PassivityIsNotClassifiedAsTwoPortOnly()
    {
        Assert.False(NetworkMetrics.IsTwoPortOnly(NetworkMetric.Passivity));
        foreach (var m in new[] { NetworkMetric.Mu, NetworkMetric.MuPrime, NetworkMetric.K,
                                  NetworkMetric.DeltaMag, NetworkMetric.MaxGain })
            Assert.True(NetworkMetrics.IsTwoPortOnly(m));
    }
}
