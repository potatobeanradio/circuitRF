// ================================================================
//  PlotsAndTuningRound26Tests.cs  —  brief-dfr26-6
//
//  1. Tuning one of two independent circuits never moves the other's curve.
//  2. The legend: one entry per trace when it is on, none when it is off,
//     and the setting survives a .cdd.
//  3. The Tuning window's Up/Down choose the row; Left/Right step it.
//  4. Markers: a menu-added marker is selected; a Smith marker steps by frequency.
// ================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Circuit;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Tests.Tuning;
using CircuitRF.Ui.Tuning;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class PlotsAndTuningRound26Tests : FirstLaunchUserState
{
    // ---- 1. Two independent circuits (R-dfr26-6-1) -------------------------------------

    // Each a 50 Ω through line with a shunt series L-C to ground: ports 1-2 on one, 3-4 on the other.
    private const string TwoNotches = """
        Port:P1 a1 0 Num=1 Z=50 Ohm
        TLIN:T1 a1 n1 Z=50 E=20 deg F=1e9
        L:L1 n1 m1 L=1.45 nH
        C:C1 m1 0 C=1.7 pF
        TLIN:T2 n1 b1 Z=50 E=20 deg F=1e9
        Port:P2 b1 0 Num=2 Z=50 Ohm
        Port:P3 a2 0 Num=3 Z=50 Ohm
        TLIN:T3 a2 n2 Z=50 E=20 deg F=1e9
        L:L2 n2 m2 L=2 nH
        C:C2 m2 0 C=0.7 pF
        TLIN:T4 n2 b2 Z=50 E=20 deg F=1e9
        Port:P4 b2 0 Num=4 Z=50 Ohm
        analysis SP1 type=sparam start=1 stop=6 npts=101 Unit=GHz
        """;

    private sealed class CapturingSink(Action<DataSet> publish) : ITuneResultSink
    {
        public void Publish(DataSet data) => publish(data);
        public void Commit(DataSet data, IReadOnlyDictionary<string, string> values) { }
        public void Drop() { }
        public void Snapshot() { }
        public void ClearSnapshot() { }
    }

    private static Trace SParam(string path, DataCube s, int i, int j)
    {
        var slice = s.Axes.Select(a => a.Name switch
        {
            "freq" => new AxisSlice(a.Name, AxisRole.KeepAsX, 0),
            _      => new AxisSlice(a.Name, AxisRole.PinToIndex, a.Name == s.Axes[1].Name ? i : j),
        }).ToArray();
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = path, CubeName = "SP1.S", Slice = slice, Transform = CubeTransform.dB20,
        };
        t.Expression = t.BuildPickerExpression();
        return t;
    }

    /// <summary>
    /// The first circuit's inductor tuned 1,000 times, requests arriving while an evaluation is in flight and
    /// publications arriving while the display is mid-frame: the second circuit's S43 — in every evaluated result,
    /// and on the plot after every frame — is bit-identical to the untuned run's.
    /// </summary>
    [Fact]
    public async Task TuningOneCircuit_NeverMovesTheOthersCurve()
    {
        var dir = TuningDisplayFixture.TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var circuit = PreparedCircuit.FromText(TwoNotches, null, null);
            var untuned = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest()).GroupedResults!;
            var path    = Path.Combine(dir, "tb.npy");
            DataSetExporter.Export(untuned, path, ExportFormat.Npy);

            var frames = new List<Action>();
            var vm  = new DataDisplayDocumentViewModel();
            var lib = vm.Window.DataSourceLibrary;
            lib.ResultsRootProvider = () => dir;
            lib.FrameScheduler      = frames.Add;           // a frame ends when the test says so
            await lib.LoadFileAsync(path);

            var plot = vm.Window.DataDisplay!.Plots[0].PlotVM.Plot;
            plot.SetPlotType(PlotType.Rect);
            var s   = lib.Entries.Single().Data!["SP1.S"];
            var s21 = SParam(path, s, 1, 0);
            var s43 = SParam(path, s, 3, 2);
            foreach (var t in new[] { s21, s43 })
            {
                plot.Traces.Add(t);
                PlotInspectorViewModel.TrySetCubeData(t, lib, PlotType.Rect, FreqUnit.GHz);
            }
            Assert.Equal(["freq", "i", "j"], s.Axes.Select(a => a.Name));
            var baseline   = s43.Points.ToArray();
            var notch      = baseline.MinBy(p => p.Y);
            Assert.InRange(notch.X, 4.0f, 4.5f);                         // 1/2π√(2 nH · 0.7 pF) ≈ 4.25 GHz
            Assert.True(notch.Y < -20f);
            var s21Initial = s21.Points.ToArray();
            Assert.Equal(101, baseline.Length);
            Complex[] S43Of(DataSet d) => Enumerable.Range(0, 101).Select(f => d["SP1.S"].ComplexValues[(f * 4 + 3) * 4 + 2]).ToArray();
            var engineBaseline = S43Of(untuned);

            var ui = new ConcurrentQueue<Action>();
            int published = 0, engineMismatches = 0;
            using var session = TuneSession.ForCircuit(circuit, new CapturingSink(d =>
            {
                published++;
                if (!S43Of(d).SequenceEqual(engineBaseline)) engineMismatches++;
                lib.Publish(path, d);
            }), ui.Enqueue);

            // The plot is redrawn the moment a frame starts (DrawFrame raises LibraryChanged), so it is checked
            // after every UI action and after every frame that ends.
            int checks = 0, displayMismatches = 0;
            bool s21Moved = false;
            void Check()
            {
                checks++;
                if (!s43.Points.SequenceEqual(baseline)) displayMismatches++;
                s21Moved |= !s21.Points.SequenceEqual(s21Initial);
            }
            void DrainUi()   { while (ui.TryDequeue(out var a)) { a(); Check(); } }
            void EndFrames() { while (frames.Count > 0) { var end = frames[0]; frames.RemoveAt(0); end(); Check(); } }

            var rng = new Random(26);
            for (int k = 0; k < 1000; k++)
            {
                session.Request(new Dictionary<string, string> { ["L1.L"] = $"{1.0 + rng.Next(0, 100) * 0.01:0.00} nH" });
                if (k % 3 == 0) DrainUi();                                // results, some while a frame is drawing
                if (k % 8 == 0) EndFrames();
                if (k % 4 == 0)                                           // let the in-flight run land
                    while (session.IsEvaluating || !ui.IsEmpty) { Thread.Yield(); DrainUi(); }
            }
            while (session.IsEvaluating || !ui.IsEmpty || frames.Count > 0) { Thread.Yield(); DrainUi(); EndFrames(); }

            Assert.True(published > 100, $"only {published} evaluations were displayed");
            Assert.True(lib.SkippedFrames > 0);                           // publications did overlap a frame
            Assert.True(s21Moved);
            Assert.Equal(0, engineMismatches);
            Assert.Equal(0, displayMismatches);
            Assert.Equal(baseline, s43.Points);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- 2. The legend (R-dfr26-6-2) ----------------------------------------------------

    private static Plot SmithWithTwoTraces()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var snp  = new SNP([1e9, 2e9, 3e9], 2);
        var red  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var blue = new Trace(snp, MatrixType.S, 1, 1, DependentVarFormat.Db);
        red.Properties.LineColorIndex  = 12;   // Red
        blue.Properties.LineColorIndex = 1;    // Blue
        red.Properties.LineWidth = blue.Properties.LineWidth = 3;
        plot.Traces.Add(red);
        plot.Traces.Add(blue);
        return plot;
    }

    /// <summary>Pixels of <paramref name="c"/> (to within anti-aliasing) at or below <paramref name="fromY"/>.</summary>
    private static int PixelsOf(SKBitmap bmp, SKColor c, int fromY)
    {
        int n = 0;
        for (int y = Math.Max(0, fromY); y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Math.Abs(p.Red - c.Red) < 40 && Math.Abs(p.Green - c.Green) < 40 && Math.Abs(p.Blue - c.Blue) < 40) n++;
            }
        return n;
    }

    [Fact]
    public void LegendBelow_OnASmithChart_DrawsOneEntryPerTrace_OffDrawsNone()
    {
        var plot = SmithWithTwoTraces();
        Assert.Equal(LegendPlacement.Off, plot.Legend);                // off unless asked for
        Assert.Equal(0, LegendRenderer.BelowBandPx(plot, (400, 400)));

        plot.Legend = LegendPlacement.Below;
        Assert.Equal(2, LegendRenderer.Entries(plot, false, null).Count);

        // The canvas a Smith container is given grows by the band, as it does for the rows under the chart.
        double h    = 400 + PlotCanvasGeometry.BottomLabelExtraLogical(plot, 400);
        double band = LegendRenderer.BelowBandPx(plot, (400, h));
        Assert.True(band > 0);

        using var bmp = new SKBitmap(400, (int)h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            PlotRenderer.Draw(canvas, (400, h), plot, PlotDetail.Full, RenderTheme.Light);
        }
        int top = (int)(h - band);
        Assert.True(PixelsOf(bmp, new SKColor(255, 0, 0), top) > 0);   // each trace has its sample in the band
        Assert.True(PixelsOf(bmp, new SKColor(0, 0, 255), top) > 0);

        plot.Legend = LegendPlacement.Off;
        Assert.False(LegendRenderer.Draws(plot));
    }

    [Fact]
    public void Legend_RoundTripsThroughTheCdd_AndOffIsNotWritten()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot(PlotType.Smith);
        var container = display.Plots[0];
        Assert.DoesNotContain("\"Legend\"", System.Text.Json.JsonSerializer.Serialize(
            DataDisplayViewModel.BuildPlotContainerConfig(container, "."), DataDisplayJson.Options));

        container.PlotVM.Plot.Legend = LegendPlacement.Below;
        var json  = System.Text.Json.JsonSerializer.Serialize(
            DataDisplayViewModel.BuildPlotContainerConfig(container, "."), DataDisplayJson.Options);
        var round = System.Text.Json.JsonSerializer.Deserialize<PlotContainerConfig>(json, DataDisplayJson.Options)!;
        Assert.Equal(LegendPlacement.Below, PlotConfigLoader.LoadPlot(round, new NoSources()).Legend);
    }

    private sealed class NoSources : IPlotDataSources
    {
        public string?  ResolveAbs(string? sourceRef)  => sourceRef;
        public bool     Contains(string absPath)       => false;
        public SNP?     NetworkFor(string absPath)     => null;
        public DataSet? DataFor(string absPath)        => null;
        public string?  AliasFor(string absPath)       => null;
        public string?  DisplayNameFor(string absPath) => absPath;
        public bool     HasMultipleSources             => false;
        public DataSet? SelectedData                   => null;
    }

    // ---- 3. Tuning rows (R-dfr26-6-3) ---------------------------------------------------

    [Fact]
    public void TuningRows_DownMovesToTheNextRow_RightStepsIt_TheEndsGoNowhere()
    {
        var f = new TuningPanelFixture();
        f.Panel.SetTuned(["R1.R", "Wline"], true);
        var first = f.Panel.Rows[0];
        var last  = f.Panel.Rows[1];

        var next = f.Panel.NeighbourRow(first, +1);
        Assert.Same(last, next);
        string before = next!.ValueText;
        next.Nudge(+1, TuningNudge.Step);                              // what Right does on that row
        Assert.NotEqual(before, next.ValueText);

        Assert.Null(f.Panel.NeighbourRow(first, -1));                  // Up on the first row: nothing
        Assert.Null(f.Panel.NeighbourRow(last, +1));                   // Down on the last row: nothing
        Assert.Same(first, f.Panel.NeighbourRow(last, -1));
    }

    // ---- 4. Markers (R-dfr26-6-4) -------------------------------------------------------

    [Fact]
    public void AMarkerAddedFromTheMenu_IsSelected_AndRightStepsItWithoutAClick()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot(PlotType.Rect);
        var container = display.Plots[0];
        var plot      = container.PlotVM.Plot;
        var trace     = new Trace(new SNP([1e9, 2e9, 3e9], 2), MatrixType.S, 1, 0, DependentVarFormat.Db);
        plot.Traces.Add(trace);
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);
        var marker = new Marker(trace, freq: 1e9, isMulti: false, isDelta: false, index: 1) { InfoBoxPos = new PlotPoint(0, 0) };
        trace.Markers.Add(marker);

        container.OnPlotChanged(null, EventArgs.Empty);               // what PlotControl raises first
        container.OnMarkerAdded(marker, trace);                       // then MarkerAdded

        Assert.Same(marker, Assert.Single(container.GetSelectedMarkers()));
        Assert.True(container.StepSelectedMarkers(+1));
        Assert.Equal(2e9, marker.Freq);
    }

    [Fact]
    public void ASmithMarker_StepsAlongTheLocus_InFrequencyOrder()
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "S", Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)],
        };
        // Samples stored out of frequency order: 3, 1, 2 GHz.
        t.SetCubeData([3e9, 1e9, 2e9], [new(0.3, 0), new(0.1, 0), new(0.2, 0)], null, "freq", "Hz",
                      PlotType.Smith, FreqUnit.GHz);
        var m = new Marker(t, 0, false, false, 1) { PositionStatic = new System.Numerics.Vector2(0.1f, 0) };

        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(0.2f, m.PositionStatic.X, 3);                     // 2 GHz
        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(0.3f, m.PositionStatic.X, 3);                     // 3 GHz
        Assert.False(t.StepMarkerAlongX(m, +1));                       // the top: no wrap
        Assert.True(t.StepMarkerAlongX(m, -1));
        Assert.Equal(0.2f, m.PositionStatic.X, 3);
    }

    /// <summary>
    /// New data under a Smith marker — every frame of a tuning drag — keeps it on its own sample. It was
    /// re-snapped to the point nearest where it USED to be, so as the curve slid it hopped between samples:
    /// a few pixels of jitter, and a readout at another frequency (owner report, 2026-10-25).
    /// </summary>
    [Fact]
    public void ASmithMarker_RidesItsOwnSample_WhenTheDataUnderItMoves()
    {
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "S", Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)],
        };
        t.SetCubeData([1e9, 2e9, 3e9], [new(0.1, 0), new(0.2, 0), new(0.3, 0)], null, "freq", "Hz",
                      PlotType.Smith, FreqUnit.GHz);
        var m = new Marker(t, 0, false, false, 1) { PositionStatic = new System.Numerics.Vector2(0.2f, 0) };   // 2 GHz
        t.Markers.Add(m);

        // The curve slides one sample's width: 1 GHz now lies where 2 GHz was.
        t.SetCubeData([1e9, 2e9, 3e9], [new(0.2, 0), new(0.3, 0), new(0.4, 0)], null, "freq", "Hz",
                      PlotType.Smith, FreqUnit.GHz);

        Assert.Equal(0.3f, t.GetMarkerDataLocation(m).X, 3);           // still 2 GHz
    }

    /// <summary>A tuning step whose result has a non-finite sample: the autoscaled window stays finite (it went
    /// infinite, and serializing it for the unsaved-changes check crashed the app), and a marker on that sample
    /// is not moved onto it.</summary>
    [Fact]
    public void ANonFiniteSample_LeavesTheWindowFinite_AndTheMarkerOffIt()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var t = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "S", Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)],
        };
        t.SetCubeData([1e9, 2e9, 3e9], [new(0.1, 0), new(0.2, 0), new(0.3, 0)], null, "freq", "Hz",
                      PlotType.Smith, FreqUnit.GHz);
        var m = new Marker(t, 0, false, false, 1) { PositionStatic = new System.Numerics.Vector2(0.2f, 0) };
        t.Markers.Add(m);
        plot.Traces.Add(t);

        t.SetCubeData([1e9, 2e9, 3e9], [new(0.1, 0), new(double.PositiveInfinity, 0), new(0.3, 0)], null, "freq", "Hz",
                      PlotType.Smith, FreqUnit.GHz);
        plot.Autoscale(force: true);

        var w = plot.Axes.Window;
        Assert.True(double.IsFinite(w.X) && double.IsFinite(w.Y) && double.IsFinite(w.Width) && double.IsFinite(w.Height));
        Assert.True(float.IsFinite(m.PositionStatic.X) && float.IsFinite(m.PositionStatic.Y));
    }
}
