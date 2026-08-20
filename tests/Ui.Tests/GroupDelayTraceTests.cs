// ================================================================
//  GroupDelayTraceTests.cs  —  group delay as a Data Display derived parameter (2026-08-19)
//
//  The owner's ask: "add group delay as a derived parameter for an s-parameter analysis... it
//  should appear in a trace card alongside mu and mu' in the Data Display Plot Inspector. It is a
//  scalar value so it's only available for Rect and Table plot types."
//
//  So the gates here are the same shape as the stability set's own (R-stb-5): the metric is a
//  scalar versus frequency, it is OFFERED where a scalar belongs and DISABLED WITH A REASON where
//  it does not, it takes the ordered port pair, and the numbers it plots are RfCore's.
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

public sealed class GroupDelayTraceTests
{
    /// <summary>An ideal 2 ns delay line, swept far enough that its phase wraps many times.</summary>
    private static SNP DelayLine(double tau0 = 2e-9, int points = 401)
    {
        var f = Enumerable.Range(0, points).Select(i => 1e9 + 4e9 * i / (points - 1.0)).ToArray();
        var mats = f.Select(x =>
        {
            var s21 = Complex.Exp(new Complex(0, -2.0 * Math.PI * x * tau0));
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = Complex.Zero; m[0, 1] = s21;
            m[1, 0] = s21;          m[1, 1] = Complex.Zero;
            return m;
        }).ToArray();
        return new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    private static Trace GroupDelayTrace(SNP snp, int inPort = 1, int outPort = 2)
        => new(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Derived = DerivedParameters.GroupDelay, InputPort = inPort, OutputPort = outPort,
        };

    // ── It is a scalar versus frequency, and it says so ────────────────────────

    [Fact]
    public void GroupDelay_IsAScalarVersusFrequency_NotACircleLocus()
    {
        Assert.True(DerivedParameters.GroupDelay.IsScalarVsFrequency());
        Assert.False(DerivedParameters.GroupDelay.IsCircleLocus());
        Assert.True(DerivedParameters.GroupDelay.NeedsPortPair());
        Assert.True(DerivedParameters.GroupDelay.IsSweepDerivative());
    }

    /// <summary>
    /// <b>It has no <c>NetworkMetric</c> member, deliberately.</b> Every member of that enum is a
    /// function of ONE matrix; group delay is a derivative along the sweep. Mapping it there would
    /// mean a per-matrix evaluator that silently needed the frequency axis.
    /// </summary>
    [Fact]
    public void GroupDelay_HasNoPerMatrixNetworkMetric()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DerivedParameters.GroupDelay.ToNetworkMetric());
        Assert.DoesNotContain(Enum.GetNames<RfCore.Data.NetworkMetric>(),
                              n => n.Contains("Delay", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Offered on Rect and Table, disabled WITH A REASON on Smith and Polar — the same gating every
    /// other scalar metric gets, expressed from the metric's own kind rather than re-listed.
    /// </summary>
    [Theory]
    [InlineData(PlotType.Rect, true)]
    [InlineData(PlotType.Table, true)]
    [InlineData(PlotType.Smith, false)]
    [InlineData(PlotType.Polar, false)]
    public void GroupDelay_IsOfferedOnRectAndTableOnly(PlotType plot, bool expected)
    {
        var item = new TraceDataItem(null!, DerivedParameters.GroupDelay, plot, omitFilePrefix: true);
        Assert.Equal(expected, item.IsEnabled);
        if (!expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.DisabledReason));
            Assert.Contains("rectangular", item.DisabledReason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The label names the unit, because a bare number in nanoseconds is unreadable.</summary>
    [Fact]
    public void GroupDelay_NamesItsUnit()
    {
        Assert.Contains("ns", DerivedParameters.GroupDelay.Description(), StringComparison.Ordinal);
        Assert.Contains("Group Delay",
                        new TraceDataItem(null!, DerivedParameters.GroupDelay, PlotType.Rect,
                                          omitFilePrefix: true).Label,
                        StringComparison.Ordinal);
    }

    // ── The numbers ────────────────────────────────────────────────────────────

    /// <summary>
    /// A 2 ns line reads 2 ns, at every point of a sweep whose phase wraps eight times — the same
    /// oracle <c>RfCore.Tests.GroupDelayTests</c> uses, here proving the TRACE reaches it and
    /// converts to nanoseconds on the way.
    /// </summary>
    [Fact]
    public void ARectTrace_PlotsTheDelayInNanoseconds()
    {
        var trace = GroupDelayTrace(DelayLine(2e-9));
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.NotEmpty(trace.Points);
        Assert.All(trace.Points, p => Assert.Equal(2.0, p.Y, 6));
    }

    /// <summary>The Table/marker read goes through the same array as the plotted path.</summary>
    [Fact]
    public void TheScalarReadout_AgreesWithThePlottedPath()
    {
        var snp = DelayLine(1.25e-9);
        var trace = GroupDelayTrace(snp);
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        foreach (double f in new[] { snp.Frequencies[0], snp.Frequencies[100], snp.Frequencies[^1] })
            Assert.Equal(1.25, trace.DataPointScalar(f), 6);
    }

    /// <summary>
    /// The port pair is ORDERED, exactly as it is for μ and μ′: on an asymmetric part, (1,2) and
    /// (2,1) read the forward and the reverse path and those are different delays.
    /// </summary>
    [Fact]
    public void ThePortPairIsOrdered()
    {
        int points = 201;
        var f = Enumerable.Range(0, points).Select(i => 1e9 + 2e9 * i / (points - 1.0)).ToArray();
        var mats = f.Select(x =>
        {
            Complex Ln(double t) => 0.8 * Complex.Exp(new Complex(0, -2.0 * Math.PI * x * t));
            var m = new Mat<Complex>(2, 2);
            m[0, 0] = new Complex(0.05, 0); m[0, 1] = Ln(3e-9);
            m[1, 0] = Ln(1e-9);             m[1, 1] = new Complex(0.05, 0);
            return m;
        }).ToArray();
        var snp = new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0));

        var forward = GroupDelayTrace(snp, 1, 2);
        var reverse = GroupDelayTrace(snp, 2, 1);
        forward.BuildPath(PlotType.Rect, FreqUnit.GHz);
        reverse.BuildPath(PlotType.Rect, FreqUnit.GHz);

        Assert.All(forward.Points, p => Assert.Equal(1.0, p.Y, 5));
        Assert.All(reverse.Points, p => Assert.Equal(3.0, p.Y, 5));
    }

    /// <summary>
    /// A wrapping phase is the case that separates a correct implementation from a plausible one: a
    /// raw <c>Complex.Phase</c> difference spikes at every 2π crossing. The plotted curve must be
    /// flat — no point may be off by more than a hair.
    /// </summary>
    [Fact]
    public void TheUnwrappingIsReal_NoSpikeAtAnyPhaseCrossing()
    {
        var trace = GroupDelayTrace(DelayLine(5e-9, 1001));   // ~20 wraps across the sweep
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);

        double worst = trace.Points.Max(p => Math.Abs(p.Y - 5.0));
        Assert.True(worst < 1e-4, $"worst deviation from 5 ns was {worst:E3} ns");
    }
}
