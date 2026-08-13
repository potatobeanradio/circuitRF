// ================================================================
//  StabilityFourPortCrashTests.cs  —  brief-dd-network-params-and-stability.md §6
//
//  Regression: plotting/tabling a stability metric from an N>2-port run must never crash.
//  Trace.DataPoint's derived branch used to build an SNP straight from the raw N-port matrices
//  and call RFNetwork.StabilityMu/StabilityMuPrime/MaxGain directly — those throw
//  ArgumentException ("This calculation requires a 2-port network.") for N != 2. The Table path
//  reaches it via TableRenderer.FormatTraceCell -> Trace.DataPointScalar -> Trace.DataPoint.
//
//  Fixed by routing DataPoint's IsDerived branch through the same RfCore.Data.NetworkMetrics
//  authority BuildDerivedPath already uses (TwoPortMetric / PassivityFull / PassivityPair),
//  catching ArgumentException -> NaN. This test must fail before that fix (a raw call would throw
//  instead of returning NaN for K/DeltaMag/Passivity, which the old code never even attempted —
//  and would throw outright for Mu/MuPrime/MaxGain on N=4).
// ================================================================

using System;
using System.Numerics;
using NumFlat;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class StabilityFourPortCrashTests
{
    private static Mat<Complex> Amp2Port(double gain = 3.2)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = new Complex(0.60, -0.30);
        m[0, 1] = new Complex(0.05,  0.02);
        m[1, 0] = new Complex(gain,  1.10);
        m[1, 1] = new Complex(0.45, -0.25);
        return m;
    }

    /// <summary>
    /// A 4-port SNP with an active 2-port device embedded across ports 2-3 (0-based 1,2), and weak
    /// coupling between every other port pair so K (which divides by |S12*S21|) stays finite for
    /// ANY selected pair, not only (2,3).
    /// </summary>
    private static SNP FourPortSnp()
    {
        var dev = Amp2Port();
        var m = new Mat<Complex>(4, 4);
        for (int i = 0; i < 4; i++)
        for (int j = 0; j < 4; j++)
            m[i, j] = i == j ? new Complex(0.1, 0) : new Complex(0.02, 0.01);
        m[1, 1] = dev[0, 0]; m[1, 2] = dev[0, 1];
        m[2, 1] = dev[1, 0]; m[2, 2] = dev[1, 1];
        return new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    private static Trace DerivedTrace(SNP snp, DerivedParameters d, int inPort = 2, int outPort = 3)
        => new(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Derived = d, InputPort = inPort, OutputPort = outPort,
        };

    [Theory]
    [InlineData(DerivedParameters.Mu,      NetworkMetric.Mu)]
    [InlineData(DerivedParameters.MuPrime, NetworkMetric.MuPrime)]
    [InlineData(DerivedParameters.K,       NetworkMetric.K)]
    [InlineData(DerivedParameters.DeltaMag, NetworkMetric.DeltaMag)]
    [InlineData(DerivedParameters.MaxGain, NetworkMetric.MaxGain)]
    public void FourPort_TwoPortMetric_DataPointScalar_MatchesNetworkMetricsAndDoesNotThrow(
        DerivedParameters derived, NetworkMetric metric)
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, derived);

        double scalar = trace.DataPointScalar(1e9);   // used to throw for N=4

        Assert.True(double.IsFinite(scalar));

        var z0PerPort = RFNetwork.Z0Array(new Complex(50, 0), 4);
        double expected = NetworkMetrics.TwoPortMetric(snp.Matrices, z0PerPort, metric, 2, 3)[0];
        Assert.Equal(expected, scalar, 6);
    }

    [Fact]
    public void FourPort_PassivityPair_DataPointScalar_MatchesNetworkMetricsAndDoesNotThrow()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.Passivity);
        trace.PassivityWholeNetwork = false;

        double scalar = trace.DataPointScalar(1e9);
        Assert.True(double.IsFinite(scalar));

        var z0PerPort = RFNetwork.Z0Array(new Complex(50, 0), 4);
        double expected = NetworkMetrics.PassivityPair(snp.Matrices, z0PerPort, 2, 3)[0];
        Assert.Equal(expected, scalar, 6);
    }

    [Fact]
    public void FourPort_PassivityFull_DataPointScalar_MatchesNetworkMetricsAndDoesNotThrow()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.Passivity);
        trace.PassivityWholeNetwork = true;

        double scalar = trace.DataPointScalar(1e9);
        Assert.True(double.IsFinite(scalar));

        var z0PerPort = RFNetwork.Z0Array(new Complex(50, 0), 4);
        double expected = NetworkMetrics.PassivityFull(snp.Matrices, z0PerPort)[0];
        Assert.Equal(expected, scalar, 6);
    }

    [Fact]
    public void FourPort_OutOfRangePortPair_YieldsNaN_NeverThrows()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.MuPrime, inPort: 1, outPort: 9);

        double scalar = trace.DataPointScalar(1e9);
        Assert.True(double.IsNaN(scalar));
    }

    [Fact]
    public void FourPort_SamePortTwice_YieldsNaN_NeverThrows()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.K, inPort: 3, outPort: 3);

        double scalar = trace.DataPointScalar(1e9);
        Assert.True(double.IsNaN(scalar));
    }

    /// <summary>
    /// DataPointScalar(f) must equal the plotted Points y-value at the same frequency, for all
    /// seven metrics (Mu, MuPrime, K, DeltaMag, MaxGain, Passivity-whole, Passivity-pair) across
    /// two different port pairs. BuildDerivedPath (the plotted path) applies no YAxis transform on
    /// top of the NetworkMetrics value, so DataPoint/DataPointScalar must not either.
    /// </summary>
    [Theory]
    [InlineData(2, 3)]
    [InlineData(1, 4)]
    public void FourPort_DataPointScalar_MatchesPlottedPoint_ForAllSevenMetrics(int inPort, int outPort)
    {
        var snp = FourPortSnp();

        void Check(DerivedParameters d, bool? passivityWhole = null)
        {
            var trace = DerivedTrace(snp, d, inPort, outPort);
            if (passivityWhole is { } whole) trace.PassivityWholeNetwork = whole;

            trace.BuildPath(PlotType.Rect, FreqUnit.GHz);
            double plotted = trace.Points.Single().Y;

            double scalar = trace.DataPointScalar(1e9);

            // Points.Y is float (single precision) — 3 decimal places is well inside its ~7
            // significant-digit resolution regardless of the metric's magnitude.
            Assert.Equal(plotted, scalar, 3);
        }

        Check(DerivedParameters.Mu);
        Check(DerivedParameters.MuPrime);
        Check(DerivedParameters.K);
        Check(DerivedParameters.DeltaMag);
        Check(DerivedParameters.MaxGain);
        Check(DerivedParameters.Passivity, passivityWhole: true);
        Check(DerivedParameters.Passivity, passivityWhole: false);
    }

    [Fact]
    public void FourPort_TableRenderer_FormatTraceCell_AllSevenMetrics_NeverThrows()
    {
        var snp = FourPortSnp();
        DerivedParameters[] metrics =
        [
            DerivedParameters.Mu, DerivedParameters.MuPrime, DerivedParameters.K,
            DerivedParameters.DeltaMag, DerivedParameters.MaxGain, DerivedParameters.Passivity,
        ];

        foreach (var d in metrics)
        {
            var trace = DerivedTrace(snp, d);
            string cell = TableRenderer.FormatTraceCell(trace, 1e9);
            Assert.False(string.IsNullOrEmpty(cell));
            Assert.NotEqual("NaN", cell);
        }

        // Stability circles reach the same DataPoint path via GetMarkerDataPoint/MuString, not
        // FormatTraceCell (they render as loci, not scalars) — covered by MuStringFourPort below.
    }

    [Fact]
    public void FourPort_GetMarkerDataPoint_MuPrime_DoesNotThrow()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.MuPrime);
        var marker = new Marker(trace, 1e9, isMulti: false, isDelta: false, index: 1);

        var dp = trace.GetMarkerDataPoint(marker);
        Assert.True(double.IsFinite(dp.Real));
    }

    [Fact]
    public void FourPort_MuString_OnStabilityCircle_DoesNotThrow()
    {
        var snp = FourPortSnp();
        var trace = DerivedTrace(snp, DerivedParameters.SourceStabilityCircle);
        var marker = new Marker(trace, 1e9, isMulti: false, isDelta: false, index: 1);

        string s = trace.MuString(marker);
        Assert.Contains("Source Stability", s);
        Assert.DoesNotContain("NaN", s);
    }
}
