// ================================================================
//  MarkerFrequencyToleranceTests.cs
//
//  An adjacent defect found while building the logarithmic X axis (brief-dd-log-frequency-axis
//  §"While you are here"), fixed and gated on its own: resolving a marker to its data point used an
//  ABSOLUTE tolerance of 1e-6 Hz — a relative 5e-13 at 2 MHz and roughly two ULPs at 2 GHz.
//
//  Measured: a marker asked for 2138469.2 Hz against a stored 2138469.1999823763 Hz — the same
//  number to ten significant figures, and the number the run's own `.s2p` wrote — missed its point
//  and SILENTLY snapped to the next one, 4.573 MHz, landing on top of another marker.
//
//  In-app placement was never at risk: AddMarkerAtFreqIndex copies Data.Frequencies[fi] verbatim.
//  What this bites is a frequency that has been ROUND-TRIPPED, TYPED or UNIT-CONVERTED — which is
//  exactly what a marker editor and a re-imported Touchstone do.
// ================================================================

using CircuitRF.Render.DataDisplay;
using RfCore;

namespace CircuitRF.Ui.Tests;

public sealed class MarkerFrequencyToleranceTests
{
    private static (Plot Plot, Trace Trace) Fixture()
    {
        // The brief's own sweep: 11 log-spaced points from 1 MHz to 2 GHz. Point 1 is the measured
        // 2138469.1999823763 Hz.
        var freqs = new double[11];
        for (int i = 0; i < freqs.Length; i++)
            freqs[i] = 1e6 * Math.Pow(2000.0, i / 10.0);

        var snp = new SNP(freqs, 2);
        for (int f = 0; f < freqs.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    snp.Matrices[f][i, j] = new System.Numerics.Complex(0.1 * (f + 1), -0.05 * (i + j + 1));

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
        trace.BuildPath(plot.PlotType, plot.FreqUnits);
        plot.Traces.Add(trace);
        plot.Autoscale(force: true);
        return (plot, trace);
    }

    /// <summary>
    /// A frequency written out and read back at ten significant figures resolves to ITS OWN point,
    /// not to the next one along. The rounding here is what a `.s2p` or a marker-editor text box
    /// does to the value; the tolerance it has to survive is RELATIVE, and the absolute 1e-6 Hz was
    /// not.
    /// </summary>
    [Fact]
    public void ARoundTrippedFrequency_ResolvesToItsOwnPoint()
    {
        var (plot, trace) = Fixture();

        for (int i = 0; i < trace.Data.Frequencies.Length; i++)
        {
            double exact   = trace.Data.Frequencies[i];
            double written = double.Parse(exact.ToString("G10"),
                                          System.Globalization.CultureInfo.InvariantCulture);

            var m = new Marker(trace, written, false, false, 1, plot.FreqUnits);
            Assert.Equal(trace.Points[i], trace.GetMarkerDataLocation(m));

            trace.SetMarkerFreq(m, written);
            Assert.Equal(exact, m.Freq);
        }
    }

    /// <summary>The exact stored value still resolves exactly — the path in-app placement takes, and
    /// the one that was already correct.</summary>
    [Fact]
    public void AnExactFrequency_StillResolvesExactly()
    {
        var (plot, trace) = Fixture();
        for (int i = 0; i < trace.Data.Frequencies.Length; i++)
        {
            var m = new Marker(trace, trace.Data.Frequencies[i], false, false, 1, plot.FreqUnits);
            Assert.Equal(trace.Points[i], trace.GetMarkerDataLocation(m));
        }
    }

    /// <summary>
    /// A frequency genuinely between two samples goes to the NEARER one, in both directions. The old
    /// rule always rounded UP ("the first sample at or above freq − 1e-6"), so a marker asked for a
    /// frequency a hair below a sample landed one point past it.
    /// </summary>
    [Fact]
    public void AFrequencyBetweenSamples_GoesToTheNearerOne()
    {
        var (plot, trace) = Fixture();
        var f = trace.Data.Frequencies;

        foreach (int i in new[] { 3, 7 })
        {
            var justBelow = new Marker(trace, f[i] * (1 - 1e-9), false, false, 1, plot.FreqUnits);
            Assert.Equal(trace.Points[i], trace.GetMarkerDataLocation(justBelow));

            var nearerNext = new Marker(trace, f[i] + 0.9 * (f[i + 1] - f[i]),
                                        false, false, 2, plot.FreqUnits);
            Assert.Equal(trace.Points[i + 1], trace.GetMarkerDataLocation(nearerNext));
        }
    }

    [Fact]
    public void TypedFrequencyMovesNetworkAndCubeMarkersToActualSamples()
    {
        var (plot, network) = Fixture();
        var networkMarker = new Marker(network, 0, false, false, 1, plot.FreqUnits);
        Assert.True(network.TrySetMarkerFrequency(networkMarker, 2.2e6, out double networkFrequency));
        Assert.Equal(network.Data.Frequencies[1], networkFrequency);
        Assert.Equal(networkFrequency, networkMarker.Freq);

        var cube = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "value",
        };
        cube.SetCubeData([1e9, 2e9, 4e9], null, [1.0, 2.0, 3.0],
                         "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
        var cubeMarker = new Marker(cube, 0, false, false, 1, FreqUnit.GHz);
        Assert.True(cube.TrySetMarkerFrequency(cubeMarker, 2.6e9, out double cubeFrequency));
        Assert.Equal(2e9, cubeFrequency);
        Assert.Equal(cube.Points[1], cube.GetMarkerDataLocation(cubeMarker));
        Assert.Equal(0, cubeMarker.Freq);
        Assert.False(cube.TrySetMarkerFrequency(cubeMarker, double.NaN, out _));

        Assert.True(cube.TrySetMarkerFrequency(cubeMarker, 10e9, out cubeFrequency));
        Assert.Equal(4e9, cubeFrequency);
        Assert.True(cube.TrySetMarkerFrequency(cubeMarker, 1.5e9, out cubeFrequency));
        Assert.Equal(1e9, cubeFrequency);

        cube.SetCubeData([1.0, 2.0], null, [1.0, 2.0],
                 "voltage", "V", PlotType.Rect, FreqUnit.GHz);
        Assert.False(cube.TrySetMarkerFrequency(cubeMarker, 1.5, out _));
    }

    [Fact]
    public void VisibleExtremaUseDisplayedSamplesWithinTheWindow()
    {
        var cube = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "value",
        };
        cube.SetCubeData([1e9, 2e9, 3e9, 4e9], null, [4.0, 1.0, 3.0, 9.0],
                         "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
        var marker = new Marker(cube, 0, false, false, 1, FreqUnit.GHz);
        var window = new PlotRect(1.5, 0, 1.6, 10);
        Assert.True(cube.TrySetVisibleExtremum(marker, window, maximum: false));
        Assert.True(cube.TryGetMarkerFrequency(marker, out double frequency));
        Assert.Equal(2e9, frequency);
        Assert.True(cube.TrySetVisibleExtremum(marker, window, maximum: true));
        Assert.True(cube.TryGetMarkerFrequency(marker, out frequency));
        Assert.Equal(3e9, frequency);
        Assert.False(cube.TrySetVisibleExtremum(marker, new PlotRect(5, 0, 1, 1), maximum: true));
    }

    [Fact]
    public void ExtremaFollowWindowWithoutMutatingSavedMarker()
    {
        var (plot, trace) = Fixture();
        var marker = new Marker(trace, trace.Data.Frequencies[0], false, false, 1, plot.FreqUnits)
        {
            Function = MarkerFunction.Maximum,
        };
        var original = marker.Freq;
        plot.Axes.Window = new PlotRect(trace.Points[2].X, -100, trace.Points[6].X - trace.Points[2].X, 200);
        Assert.Equal(trace.Data.Frequencies[6], plot.ResolveMarker(trace, marker).Freq);
        Assert.Equal(original, marker.Freq);
        plot.Axes.Window = new PlotRect(50, -100, 1, 200);
        Assert.True(plot.ResolveMarker(trace, marker).Unavailable);
    }

    [Fact]
    public void VerticalMarkerInterpolatesOtherTraceAndDoesNotExtrapolate()
    {
        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { CubeName = "value" };
        trace.SetCubeData([1e9, 3e9], null, [2.0, 6.0], "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(4.0, trace.InterpolateDisplayedValue(2.0), 6);
        Assert.True(double.IsNaN(trace.InterpolateDisplayedValue(4.0)));
    }
}
