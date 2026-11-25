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
using SkiaSharp;

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
    public void MarkerTableReadsEachMarkersOwnTraceAndSample()
    {
        var (plot, first) = Fixture();
        var second = new Trace(new SNP([3e9, 4e9], 2), MatrixType.S, 1, 0, DependentVarFormat.Db);
        second.BuildPath(PlotType.Rect, plot.FreqUnits);
        plot.Traces.Add(second);
        var firstMarker = new Marker(first, first.Data.Frequencies[1], false, false, 1, plot.FreqUnits)
        {
            ShowInfoBox = false,
        };
        var secondMarker = new Marker(second, 4e9, false, false, 2, plot.FreqUnits);
        first.Markers.Add(firstMarker);
        second.Markers.Add(secondMarker);

        var rows = MarkerTable.Rows(plot, showFilePrefix: true);
        Assert.Equal(2, rows.Count);
        Assert.Same(firstMarker, rows[0].Marker);
        Assert.Same(first, rows[0].Trace);
        Assert.Same(secondMarker, rows[1].Marker);
        Assert.Same(second, rows[1].Trace);
        Assert.Equal(new[] { first, second }, MarkerTable.Columns(rows).Select(column => column.Trace));
        Assert.DoesNotContain(rows[0].TraceReadings, reading => reading.Trace == second);
        Assert.DoesNotContain(rows[1].TraceReadings, reading => reading.Trace == first);
        Assert.Equal(first.BuildMarkerBoxLines(firstMarker, plot.FreqUnits, false, plot.Traces).Skip(1).Select(line => line.Text), rows[0].Readout);
        Assert.Equal(second.BuildMarkerBoxLines(secondMarker, plot.FreqUnits, false, plot.Traces).Skip(1).Select(line => line.Text), rows[1].Readout);
        Assert.DoesNotContain(rows[0].Readout, line => line.Contains("dB dB", StringComparison.Ordinal));

        Assert.Same(firstMarker, MarkerTableRenderer.RowAt(rows, MarkerTableRenderer.HeaderHeight + 1, 0)!.Marker);
        Assert.Same(secondMarker, MarkerTableRenderer.RowAt(rows,
            MarkerTableRenderer.HeaderHeight + MarkerTableRenderer.RowHeight(rows[0]) + 1, 0)!.Marker);
        Assert.Null(MarkerTableRenderer.RowAt(rows, 1, 0));
    }

    [Fact]
    public void MarkerTableSortsNumericallyAcrossTraces()
    {
        var (plot, first) = Fixture();
        var second = new Trace(first.Data, MatrixType.S, 1, 0, DependentVarFormat.Db);
        second.BuildPath(PlotType.Rect, plot.FreqUnits);
        plot.Traces.Add(second);
        var marker1 = new Marker(second, first.Data.Frequencies[0], false, false, 1, plot.FreqUnits);
        var marker2 = new Marker(first, first.Data.Frequencies[0], false, false, 2, plot.FreqUnits) { Name = "custom" };
        var marker3 = new Marker(second, first.Data.Frequencies[0], false, false, 3, plot.FreqUnits);
        var marker10 = new Marker(first, first.Data.Frequencies[0], false, false, 10, plot.FreqUnits);
        first.Markers.Add(marker10);
        first.Markers.Add(marker2);
        second.Markers.Add(marker3);
        second.Markers.Add(marker1);

        var rows = MarkerTable.Rows(plot);
        Assert.Equal(new[] { marker1, marker2, marker3, marker10 }, rows.Select(row => row.Marker));
        Assert.Equal(new[] { second, first, second, first }, rows.Select(row => row.Trace));
        Assert.Equal(new[] { first, second }, MarkerTable.Columns(rows).Select(column => column.Trace));
        float y = MarkerTableRenderer.HeaderHeightFor(rows);
        foreach (var row in rows)
        {
            Assert.Same(row.Marker, MarkerTableRenderer.RowAt(rows, y + 1, 0)!.Marker);
            y += MarkerTableRenderer.RowHeight(row);
        }
    }

    [Fact]
    public void MarkerTableWrapsLongReadoutsWithoutDroppingText()
    {
        var (plot, trace) = Fixture();
        trace.Label = "A long trace name with units and additional detail";
        var marker = new Marker(trace, trace.Data.Frequencies[0], false, false, 1, plot.FreqUnits);
        trace.Markers.Add(marker);
        var row = Assert.Single(MarkerTable.Rows(plot));
        var traceLines = MarkerTableRenderer.WrapText(row.TraceLabel, MarkerTableRenderer.ValueColumn - 10);
        Assert.True(traceLines.Count > 1);
        Assert.Equal(row.TraceLabel, string.Concat(traceLines));
        Assert.Equal(string.Concat(row.Coordinates), string.Concat(row.CoordinateLines));
        Assert.Equal(string.Concat(row.TraceReadings[0].Values), string.Concat(row.TraceReadings[0].ValueLines));
        Assert.True(MarkerTableRenderer.HeaderHeightFor([row]) >= traceLines.Count * MarkerTableRenderer.LineHeight);
        var wrapped = MarkerTableRenderer.WrapText("S(1,1) dB20=-0.03245 dB", 110);
        Assert.Equal("S(1,1) dB20=-0.03245 dB", string.Concat(wrapped));
        Assert.Contains(wrapped, line => line.Contains("-0.03245 dB", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkerTableDisambiguatesSourcesAndMultiMarkerReadings()
    {
        var (plot, first) = Fixture();
        first.SourcePath = "/first/run.s2p";
        var second = new Trace(new SNP([1e6, 2e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = "/second/run.s2p",
        };
        second.BuildPath(PlotType.Rect, plot.FreqUnits);
        plot.Traces.Add(second);
        var marker = new Marker(first, first.Data.Frequencies[1], true, false, 1, plot.FreqUnits)
        {
            Function = MarkerFunction.Vertical,
        };
        first.Markers.Add(marker);
        var row = Assert.Single(MarkerTable.Rows(plot));
        Assert.Contains("/first/run.s2p", row.TraceLabel);
        Assert.Contains(row.Readout, line => line.Contains("/second/run.s2p", StringComparison.Ordinal));
        Assert.Equal(2, row.TraceReadings.Count);
        Assert.Equal(new[] { first, second }, MarkerTable.Columns([row]).Select(column => column.Trace));
        Assert.Same(first, row.TraceReadings[0].Trace);
        Assert.Same(second, row.TraceReadings[1].Trace);
        Assert.Contains("/first/run.s2p", row.TraceReadings[0].Label);
        Assert.Contains("/second/run.s2p", row.TraceReadings[1].Label);
        Assert.Equal(first.GetMultiMarkerLine(marker, second), Assert.Single(row.TraceReadings[1].Values));
        Assert.Equal(marker.FreqString, Assert.Single(row.Coordinates));
        Assert.DoesNotContain(row.TraceReadings.SelectMany(reading => reading.Values), value => value == marker.FreqString);
    }

    [Fact]
    public void MarkerTableSeparatesCubeSweepCoordinatesFromValues()
    {
        var (plot, trace) = Fixture();
        trace.CubeName = "current";
        trace.SetCubeData([1, 2, 3], null, [4.0, 5.0, 6.0], "Vds", "V", PlotType.Rect, plot.FreqUnits);
        var marker = new Marker(trace, 0, false, false, 1, plot.FreqUnits)
        {
            PositionStatic = trace.Points[1],
        };
        trace.Markers.Add(marker);
        var row = Assert.Single(MarkerTable.Rows(plot));
        Assert.Contains(row.Coordinates, coordinate => coordinate.StartsWith("Vds=2", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(row.TraceReadings).Values, value => value.Contains("5", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.35f, 0.1f)]
    [InlineData(0.63f, 0.7f)]
    [InlineData(1.0f, 0.4f)]
    [InlineData(1.37f, 0.8f)]
    [InlineData(2.0f, 0.2f)]
    public void MarkerTableGridRemainsOnePixelAtEveryZoom(float scale, float offset)
    {
        using var bitmap = new SKBitmap(1100, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        float origin = 10 + offset;
        canvas.Translate(origin, origin);
        canvas.Scale(scale);
        MarkerTableRenderer.Draw(canvas, [], RenderTheme.Light);
        var border = RenderTheme.Light.BorderColor;
        var (width, height) = MarkerTableRenderer.Measure([]);
        int centerY = (int)(origin + (MarkerTableRenderer.HeaderHeight + 7) * scale);
        int centerX = (int)(origin + 100 * scale);

        foreach (float x in new[] { 0f, MarkerTableRenderer.MarkerColumn,
                 MarkerTableRenderer.FixedWidth, width })
        {
            int position = (int)(origin + x * scale);
            Assert.Equal(1, Enumerable.Range(position - 2, 5).Count(pixel => bitmap.GetPixel(pixel, centerY) == border));
        }
        foreach (float y in new[] { 0f, MarkerTableRenderer.HeaderHeight, height })
        {
            int position = (int)(origin + y * scale);
            Assert.Equal(1, Enumerable.Range(position - 2, 5).Count(pixel => bitmap.GetPixel(centerX, pixel) == border));
        }
    }

    [Fact]
    public void MarkerTableScrollReachesLaterRows()
    {
        var (plot, trace) = Fixture();
        for (int index = 1; index <= 20; index++)
            trace.Markers.Add(new Marker(trace, trace.Data.Frequencies[0], false, false, index, plot.FreqUnits));
        var rows = MarkerTable.Rows(plot);
        float scroll = MarkerTableRenderer.MaxScroll(rows);
        Assert.True(scroll > 0);
        Assert.True(MarkerTableRenderer.MeasureAll(rows).H > MarkerTableRenderer.Measure(rows).H);
        Assert.Same(rows[^1].Marker, MarkerTableRenderer.RowAt(rows, MarkerTableRenderer.Measure(rows).H - 1, scroll)!.Marker);
    }

    [Fact]
    public void MarkerTableColumnsRemainStableAndScrollHorizontally()
    {
        var (plot, first) = Fixture();
        for (int index = 1; index < 5; index++)
        {
            var trace = new Trace(first.Data, MatrixType.S, index % 2, 0, DependentVarFormat.Db)
            {
                Label = $"Trace {index} with a long identifying name",
            };
            trace.BuildPath(PlotType.Rect, plot.FreqUnits);
            plot.Traces.Add(trace);
        }
        var marker = new Marker(plot.Traces[3], first.Data.Frequencies[1], true, false, 1, plot.FreqUnits)
        {
            Function = MarkerFunction.Vertical,
        };
        plot.Traces[3].Markers.Add(marker);
        var ordinary = new Marker(first, first.Data.Frequencies[0], false, false, 2, plot.FreqUnits);
        first.Markers.Add(ordinary);
        var rows = MarkerTable.Rows(plot);
        var columns = MarkerTable.Columns(rows);
        Assert.Equal(plot.Traces, columns.Select(column => column.Trace));
        Assert.Equal(5, rows[0].TraceReadings.Count);
        Assert.Single(rows[1].TraceReadings);
        Assert.Equal(MarkerTableRenderer.Width, MarkerTableRenderer.Measure(rows).W);
        Assert.True(MarkerTableRenderer.MeasureAll(rows).W > MarkerTableRenderer.Measure(rows).W);
        Assert.Equal(MarkerTableRenderer.MeasureAll(rows).W - MarkerTableRenderer.Measure(rows).W,
            MarkerTableRenderer.MaxHorizontalScroll(rows));
        Assert.Same(marker, MarkerTableRenderer.RowAt(rows,
            MarkerTableRenderer.HeaderHeightFor(rows) + 1, 0)!.Marker);
        Assert.Null(MarkerTableRenderer.RowAt(rows, MarkerTableRenderer.Measure(rows).H - 1, 0));
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
