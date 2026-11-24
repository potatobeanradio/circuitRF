// ================================================================
//  LinkedPlotsTests.cs — brief-ddl-1: a pinned slice axis that follows a
//  marker, resolved the same way in the window and in `circuitrf render`.
//  One test per gate.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Cli;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class LinkedPlotsTests : IDisposable
{
    private const int Grid = 30, Steps = 8;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl1_{Guid.NewGuid():N}");

    public LinkedPlotsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Complex Gamma(int g) => Complex.FromPolarCoordinates(0.8 * (g + 1) / Grid, 0.5 * g);
    private static double Pout(int g, int p) => 100 * g + p;
    private static double Gt(int g, int p) => g + 1000 * p;

    /// <summary>A loadpull-shaped run: <c>GammaLoad[gridPoint]</c>, <c>Pout[gridPoint, pinStep]</c>, <c>Gt</c> likewise.</summary>
    private string LoadpullRun()
    {
        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, Grid).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", [.. Enumerable.Range(0, Steps).Select(i => -10.0 + 2 * i)], "dBm");
        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([grid], [.. Enumerable.Range(0, Grid).Select(Gamma)]));
        var pout = new double[Grid * Steps];
        var gt   = new double[Grid * Steps];
        for (int g = 0; g < Grid; g++)
            for (int p = 0; p < Steps; p++) { pout[g * Steps + p] = Pout(g, p); gt[g * Steps + p] = Gt(g, p); }
        ds.Add("Pout", new DataCube([grid, pin], pout));
        ds.Add("Gt",   new DataCube([grid, pin], gt));
        string path = Path.Combine(_dir, "lp.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static AxisSliceConfig Pin(string axis, int index, Guid? link = null, string? name = null)
        => new() { AxisName = axis, Role = AxisRole.PinToIndex, Index = index, Link = link?.ToString("D"), LinkName = name };
    private static AxisSliceConfig X(string axis) => new() { AxisName = axis, Role = AxisRole.KeepAsX };

    private static TraceConfig Cube(string src, string cube, string expr, params AxisSliceConfig[] slice)
        => new() { SourcePath = src, CubeName = cube, Expression = expr, CubeSlice = [.. slice] };

    private static PlotContainerConfig PlotOf(PlotType type, double left, params TraceConfig[] traces)
        => new() { PlotType = type, Left = left, Top = 0, Width = 300, Height = 300, Traces = [.. traces] };

    private static MarkerConfig MarkerAt(Guid id, string name, Vector2 at)
        => new() { Id = id.ToString("D"), Name = name, PositionStaticX = at.X, PositionStaticY = at.Y };

    private string WriteCdd(string name, params PlotContainerConfig[] plots)
    {
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig { Name = "Tab 1", Plots = [.. plots] }],
        };
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return path;
    }

    private static readonly Guid M1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Gate 1's document: m1 on the Smith trace of GammaLoad at sample 17; a Rect plot of
    /// <c>Pout[@m1, :]</c> stored at index 0.</summary>
    private string LinkedCdd(string npy, string name = "linked.cdd", bool withMarker = true, int storedIndex = 0)
    {
        var smith = Cube(npy, "GammaLoad", "GammaLoad", X("gridPoint"));
        var g = Gamma(17);
        if (withMarker) smith.Markers.Add(MarkerAt(M1, "m1", new Vector2((float)g.Real, (float)g.Imaginary)));
        var detail = Cube(npy, "Pout", "Pout[@m1, :]", Pin("gridPoint", storedIndex, M1, "m1"), X("pinStep"));
        return WriteCdd(name, PlotOf(PlotType.Smith, 0, smith), PlotOf(PlotType.Rect, 320, detail));
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    private static Trace TraceAt(DisplayWindowViewModel w, int plot, int trace = 0)
        => w.Tabs[0].DataDisplay.Plots[plot].PlotVM.Plot.Traces[trace];

    private static double[] PoutRow(int g) => [.. Enumerable.Range(0, Steps).Select(p => Pout(g, p))];
    private static double[] Ys(Trace t) => [.. t.Points.Select(p => (double)p.Y)];

    [Fact]
    public async Task Gate1_SameRun_ByIndex_DrawsExactlyTheMarkersRow()
    {
        var w = await OpenAsync(LinkedCdd(LoadpullRun()));
        var detail = TraceAt(w, 1);

        Assert.Equal(17, detail.Slice![0].Index);
        Assert.Equal(PoutRow(17), Ys(detail));
        Assert.False(detail.LinkFrozen);
        Assert.Null(detail.LinkNote);
        Assert.Equal("Pout[@m1, :]", detail.BuildPickerExpression());
    }

    [Fact]
    public async Task Gate2_AnotherFile_ByValue_TakesTheNearestSample_AndRefusesAnotherUnit()
    {
        // A Touchstone file on 1.0 … 3.0 GHz, m1 at 2.4 GHz.
        double[] f = [.. Enumerable.Range(0, 21).Select(i => 1e9 + i * 1e8)];
        var snp = new SNP(f, [.. f.Select(_ => { var m = new Mat<Complex>(2, 2); m[1, 0] = 0.5; return m; })],
                          MatrixType.S, MatrixFormat.RI);
        string s2p = Path.Combine(_dir, "dut.s2p");
        TouchstoneIO.WriteFile(snp, s2p);

        // A run whose freq grid is offset by 30 MHz: 1.03, 1.13 … 2.43 (index 14) … 3.03 GHz.
        var freq = new Axis("freq", [.. Enumerable.Range(0, 21).Select(i => 1.03e9 + i * 1e8)], "Hz");
        var pin  = new Axis("pinStep", [.. Enumerable.Range(0, Steps).Select(i => -10.0 + 2 * i)], "dBm");
        var ds = new DataSet();
        var gain = new double[21 * Steps];
        for (int k = 0; k < gain.Length; k++) gain[k] = k;
        ds.Add("Gain", new DataCube([freq, pin], gain));
        string npy = Path.Combine(_dir, "sweep.npy");
        DataSetExporter.Export(ds, npy, ExportFormat.Npy);

        var network = new TraceConfig { SourcePath = s2p, Row = 1, Col = 0, YAxis = DependentVarFormat.Db };
        network.Markers.Add(new MarkerConfig { Id = M1.ToString("D"), Name = "m1", Freq = 2.4e9 });

        // m2 sits on a pinStep axis and is asked to drive a freq axis: refused by unit.
        var m2 = Guid.NewGuid();
        var overPin = Cube(npy, "Gain", "Gain[3, :]", Pin("freq", 3), X("pinStep"));
        overPin.Markers.Add(MarkerAt(m2, "m2", new Vector2(0, 0)));

        var byValue = Cube(npy, "Gain", "Gain[@m1, :]", Pin("freq", 0, M1, "m1"), X("pinStep"));
        var byUnit  = Cube(npy, "Gain", "Gain[@m2, :]", Pin("freq", 5, m2, "m2"), X("pinStep"));
        var w = await OpenAsync(WriteCdd("value.cdd",
            PlotOf(PlotType.Rect, 0, network), PlotOf(PlotType.Rect, 320, overPin),
            PlotOf(PlotType.Rect, 640, byValue, byUnit)));

        var linked = TraceAt(w, 2, 0);
        Assert.Equal(14, linked.Slice![0].Index);
        Assert.Equal([.. Enumerable.Range(0, Steps).Select(p => (double)(14 * Steps + p))], Ys(linked));

        var refused = TraceAt(w, 2, 1);
        Assert.Equal(5, refused.Slice![0].Index);                       // drawn where it was stored
        Assert.Equal("m2 gives pinStep (dBm); this axis is freq (Hz).", refused.LinkNote);
    }

    [Fact]
    public void Gate2_AValueOffTheEnd_TakesTheEndSample_AndSaysSo()
    {
        var axis = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var v = new LinkValue("x", "m1", [new LinkCoordinate(null, "", "freq", 0, 3.1e9 + 1e9, "Hz", [4.1e9])]);
        var r = LinkMatch.Match(v, axis, 0);
        Assert.Equal((2, LinkMatchKind.Clamped), (r.Index, r.Kind));
        Assert.Equal("m1 is 4.1 GHz; this axis ends at 3 GHz.", r.Note);
    }

    [Fact]
    public async Task Gate3_Frozen_WhenTheMarkerIsGone_DrawsTheStoredIndex_AndKeepsTheLink()
    {
        string npy = LoadpullRun();
        var w = await OpenAsync(LinkedCdd(npy, "frozen.cdd", withMarker: false, storedIndex: 17));
        var detail = TraceAt(w, 1);

        Assert.Equal(PoutRow(17), Ys(detail));
        Assert.True(detail.LinkFrozen);
        Assert.Contains("m1 is gone", detail.LinkNote);

        string saved = Path.Combine(_dir, "frozen-saved.cdd");
        await w.SaveAllAsync(saved);
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(saved), DataDisplayJson.Options)!;
        var pin = config.Tabs[0].Plots[1].Traces[0].CubeSlice[0];
        Assert.Equal((M1.ToString("D"), 17), (pin.Link, pin.Index));
    }

    [Fact]
    public async Task Gate4_ACycle_IsRefusedAtCommit_WithTheChain()
    {
        string npy = LoadpullRun();
        var m2 = Guid.NewGuid();
        // Pout follows m2 (on Gt); m1 sits on Pout. Gt is still an ordinary pin.
        var pout = Cube(npy, "Pout", "Pout[@m2, :]", Pin("gridPoint", 0, m2, "m2"), X("pinStep"));
        pout.Markers.Add(MarkerAt(M1, "m1", new Vector2(-6, 0)));
        var gt = Cube(npy, "Gt", "Gt[:, 3]", X("gridPoint"), Pin("pinStep", 3));
        gt.Markers.Add(MarkerAt(m2, "m2", new Vector2(9, 0)));
        var w = await OpenAsync(WriteCdd("cycle.cdd", PlotOf(PlotType.Rect, 0, pout), PlotOf(PlotType.Rect, 320, gt)));

        // The second link — Gt following m1 on Pout — closes the cycle.
        var gtTrace = TraceAt(w, 1);
        Assert.True(CubeTraceSpecParser.TryParse("Gt[:, @m1]", PlotSourceFile.Load(npy).Data!, out _, out var slice, out _, out _));
        gtTrace.Slice = slice;
        Assert.False(LinkCommit.TryCommit(w.BuildLinkDocument(), gtTrace, out var error));
        Assert.Equal("That would make a cycle: Gt pinned by m1 on Pout, pinned by m2 on Gt.", error);
    }

    [Fact]
    public async Task Gate4_AHandWrittenCycle_LoadsWithBothFrozen()
    {
        string npy = LoadpullRun();
        var m2 = Guid.NewGuid();
        var pout = Cube(npy, "Pout", "Pout[@m2, :]", Pin("gridPoint", 4, m2, "m2"), X("pinStep"));
        pout.Markers.Add(MarkerAt(M1, "m1", new Vector2(-6, 0)));
        var gt = Cube(npy, "Gt", "Gt[:, @m1]", X("gridPoint"), Pin("pinStep", 2, M1, "m1"));
        gt.Markers.Add(MarkerAt(m2, "m2", new Vector2(9, 0)));
        var w = await OpenAsync(WriteCdd("cyclic.cdd", PlotOf(PlotType.Rect, 0, pout), PlotOf(PlotType.Rect, 320, gt)));

        var (a, b) = (TraceAt(w, 0), TraceAt(w, 1));
        Assert.True(a.LinkFrozen && b.LinkFrozen);
        Assert.Equal((4, 2), (a.Slice![0].Index, b.Slice![1].Index));
        Assert.Contains("Pout pinned by m2 on Gt, pinned by m1 on Pout", a.LinkNote);
    }

    [Fact]
    public async Task Gate5_TheWindowAndTheCli_ResolveALinkedDisplayToTheSamePoints()
    {
        string npy = LoadpullRun();
        string cdd = LinkedCdd(npy);

        // The window, with m1 moved to sample 5.
        var w = await OpenAsync(cdd);
        var smith = TraceAt(w, 0);
        Assert.True(smith.PlaceMarkerAtSample(smith.Markers[0], 5));
        w.ResolveLinks();
        var windowPoints = TraceAt(w, 1).Points.ToList();

        // Headless, through CddSources and `--marker m1=#5`.
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var (sources, refusal) = CddSources.Bind(cdd, config, config.Tabs, [0], []);
        Assert.Null(refusal);
        var plots = config.Tabs[0].Plots.Select(pc => PlotConfigLoader.LoadPlot(pc, sources!)).ToList();
        foreach (var p in plots) p.LabelColumnsOutside = true;
        var doc = new LinkDocument([new LinkDocument.Tab("Tab 1", plots)]);
        Assert.Null(RenderDataDisplay.MarkerMove.Parse("m1=#5")!.Apply(doc));
        LinkResolve.Resolve(doc, sources!);

        Assert.Equal(PoutRow(5), Ys(plots[1].Traces[0]));
        Assert.Equal(windowPoints, plots[1].Traces[0].Points);

        // And `render --marker m1=#5` as a process draws exactly that.
        string svg = Path.Combine(_dir, "moved.svg");
        var (exit, _, err) = CircuitRF.Ui.Tests.Optimization.OptCli.Run("render", cdd, "-o", svg, "--marker", "m1=#5");
        Assert.True(exit == 0, err);
        var placed = plots.Select((p, i) => RenderDataDisplay.Place(p, config.Tabs[0].Plots[i].Left, config.Tabs[0].Plots[i].Top,
            config.Tabs[0].Plots[i].Width, config.Tabs[0].Plots[i].Height, config.Tabs[0].Plots[i].FreqUnit,
            sources!.HasMultipleSources, t => sources.AliasFor(t.EffectiveSourcePath ?? ""))).ToList();
        var current = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme                    = current.ExportTheme,
            ExportTransparentBackground    = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix  = current.AlwaysDisplayDataSourcePrefix,
        };
        string mine = PlotDocumentWriter.BuildSvgString(
            canvas => PlotComposer.Render(canvas, placed, RenderTheme.Light, settings, PagePlacement.Letter),
            PagePlacement.Letter);
        static string WithoutSkiaIds(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\bcl_[0-9a-fA-F]+\b", "cl_");
        Assert.Equal(WithoutSkiaIds(mine), WithoutSkiaIds(File.ReadAllText(svg, new System.Text.UTF8Encoding(false))));
    }

    [Fact]
    public async Task Gate5_RenderMarker_NamingNoMarker_IsARefusalListingThem()
    {
        string cdd = LinkedCdd(LoadpullRun());
        var (exit, _, err) = CircuitRF.Ui.Tests.Optimization.OptCli.Run("render", cdd, "-o", Path.Combine(_dir, "x.svg"), "--marker", "m9=#5");
        Assert.NotEqual(0, exit);
        Assert.Contains("No marker 'm9'. Markers: m1.", err);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Gate6_ARepointedMarker_KeepsItsId_AndTheConsumerFollowsIt()
    {
        string npy = LoadpullRun();
        string cdd = LinkedCdd(npy);
        var w = await OpenAsync(cdd);

        // Re-point m1 from the Smith trace to a Rect trace of Pout over gridPoint, at sample 9.
        var other = w.Tabs[0].DataDisplay.Plots[1].PlotVM.Plot;
        var overGrid = new Trace(TraceAt(w, 1), includeMarkers: false)
        {
            CubeName = "Pout", Expression = "Pout[:, 0]",
            Slice = [new AxisSlice("gridPoint", AxisRole.KeepAsX, 0), new AxisSlice("pinStep", AxisRole.PinToIndex, 0)],
        };
        other.Traces.Add(overGrid);
        CircuitRF.Render.DataDisplay.TraceResolve.ResolveCubeTrace(overGrid, Sources(w), PlotType.Rect, other.FreqUnits);

        var smith = TraceAt(w, 0);
        var m1 = smith.Markers[0];
        var moved = MarkerInfoBoxViewModel.MovedTo(m1, overGrid);
        Assert.Equal(m1.Id, moved.Id);
        smith.Markers.Remove(m1);
        overGrid.Markers.Add(moved);
        Assert.True(overGrid.PlaceMarkerAtSample(moved, 9));

        w.ResolveLinks();
        Assert.Equal(PoutRow(9), Ys(TraceAt(w, 1)));
    }

    [Fact]
    public async Task MarkerNames_AreDocumentWide()
    {
        var w = await OpenAsync(LinkedCdd(LoadpullRun()));
        w.NewTabCommand.Execute(null);
        Assert.Equal(2, w.Tabs[^1].DataDisplay.GetNextMarkerIndex());
    }

    private static IPlotDataSources Sources(DisplayWindowViewModel w) => new LibraryDataSources(w.DataSourceLibrary);
}
