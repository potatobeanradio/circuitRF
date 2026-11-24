// ================================================================
//  DisplayControlsTests.cs — brief-ddl-5: display controls on the canvas,
//  and the slider. One test per gate; the S/M/L look (gate 3) and the
//  pointer gestures on the real view are looked at in UiSnapshot.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class DisplayControlsTests : IDisposable
{
    private const int Grid = 12, Steps = 6;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl5_{Guid.NewGuid():N}");
    public DisplayControlsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private const string SliderId = "55555555-5555-5555-5555-555555555555";

    private static double Pout(int g, int p) => 100 * g + p;

    /// <summary><c>Pout[gridPoint, pinStep]</c>, and <c>Gain[freq, pinStep]</c> on 1.03 … 3.03 GHz.</summary>
    private string Run()
    {
        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, Grid).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", [.. Enumerable.Range(0, Steps).Select(i => -10.0 + 2 * i)], "dBm");
        var freq = new Axis("freq", [.. Enumerable.Range(0, 21).Select(i => 1.03e9 + i * 1e8)], "Hz");
        var ds = new DataSet();
        ds.Add("Pout", new DataCube([grid, pin], [.. Enumerable.Range(0, Grid * Steps).Select(k => Pout(k / Steps, k % Steps))]));
        ds.Add("Gain", new DataCube([freq, pin], [.. Enumerable.Range(0, 21 * Steps).Select(k => (double)k)]));
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private static TraceConfig Pinned(string src, string cube, string axis, string x, int stored = 0)
        => new()
        {
            SourcePath = src, CubeName = cube, Expression = $"{cube}[@slider1, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = axis, Role = AxisRole.PinToIndex, Index = stored, Link = SliderId, LinkName = "slider1" },
                         new AxisSliceConfig { AxisName = x, Role = AxisRole.KeepAsX }],
        };

    private string Cdd(string name, SliderControl slider, params TraceConfig[] traces)
    {
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig
            {
                Name = "Tab 1",
                Plots = [new PlotContainerConfig { PlotType = PlotType.Rect, Left = 300, Top = 0, Width = 400, Height = 260, Traces = [.. traces] }],
                Controls = [slider],
            }],
        };
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        return path;
    }

    private static SliderControl Bound(string npy)
    {
        var s = new SliderControl { Id = SliderId, Name = "slider1", Domain = SliderDomain.Bound,
                                    SourcePath = npy, CubeName = "Pout", AxisName = "gridPoint", Left = 10, Top = 10 };
        s.ApplySize(ControlSize.Medium);
        return s;
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    private static Trace Detail(DisplayWindowViewModel w) => w.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot.Traces[0];
    private static double[] Ys(Trace t) => [.. t.Points.Select(p => (double)p.Y)];

    [Fact]
    public async Task Gate1_BoundSlider_EveryStopDrawsItsRowExactly()
    {
        string npy = Run();
        var w = await OpenAsync(Cdd("bound.cdd", Bound(npy), Pinned(npy, "Pout", "gridPoint", "pinStep")));
        var control = w.Tabs[0].DataDisplay.Controls.Single();
        var slider = (SliderControl)control.Model;
        Assert.Equal(Grid, slider.Stops.Count);   // its stops are the axis's samples, read at load

        for (int k = 0; k < Grid; k++)
        {
            slider.SetStop(k);
            w.ResolveLinks();
            Assert.Equal(k, Detail(w).Slice![0].Index);
            Assert.Equal([.. Enumerable.Range(0, Steps).Select(p => Pout(k, p))], Ys(Detail(w)));
            Assert.Null(Detail(w).LinkNote);
        }

        // The arrow key's path: one stop, and the display follows by itself.
        control.IsSelected = true;
        slider.SetStop(3);
        Assert.True(control.Step(+1));
        Assert.Equal([.. Enumerable.Range(0, Steps).Select(p => Pout(4, p))], Ys(Detail(w)));
    }

    [Fact]
    public async Task Gate2_RangeSliderInMHz_TakesTheNearestSampleOfAGHzAxis()
    {
        string npy = Run();
        var range = new SliderControl { Id = SliderId, Name = "slider1", Start = 1000, Stop = 3000, Step = 10,
                                        Unit = "MHz", Value = 2400 };
        range.ApplySize(ControlSize.Medium);
        var w = await OpenAsync(Cdd("range.cdd", range, Pinned(npy, "Gain", "freq", "pinStep", stored: 5)));

        // 2400 MHz on 1.03, 1.13 … 3.03 GHz: the nearest is 2.43 GHz, index 14.
        Assert.Equal(14, Detail(w).Slice![0].Index);
        Assert.Equal([.. Enumerable.Range(0, Steps).Select(p => (double)(14 * Steps + p))], Ys(Detail(w)));
        var value = ControlLinkSource.Resolve(range, null);
        Assert.Equal(new LinkScalar(2.4e9, "Hz"), value.Scalar);
    }

    [Fact]
    public async Task Gate4_TheFaceOperates_TheFrameArranges_EachOneUndoStep()
    {
        string npy = Run();
        var w = await OpenAsync(Cdd("operate.cdd", Bound(npy), Pinned(npy, "Pout", "gridPoint", "pinStep")));
        var display = w.Tabs[0].DataDisplay;
        var vm = display.Controls.Single();
        var slider = (SliderControl)vm.Model;
        var layout = ControlRenderer.Layout(slider, (float)vm.ViewWidth, (float)vm.ViewHeight);

        // A drag on the thumb, from the first stop to the last: the value moves, the frame does not.
        var top = display.UndoRedo.Top;
        Assert.True(ControlRenderer.HitFace(slider, (float)vm.ViewWidth, (float)vm.ViewHeight, layout.TrackStart));
        vm.BeginValueDrag(layout.TrackStart);
        vm.DragValueTo(new SKPoint((layout.TrackStart.X + layout.TrackEnd.X) / 2, layout.TrackStart.Y));
        vm.DragValueTo(layout.TrackEnd);
        vm.EndValueDrag();
        Assert.Equal((Grid - 1, 10.0, 10.0), (slider.StopIndex, vm.Left, vm.Top));
        Assert.Equal(Grid - 1, Detail(w).Slice![0].Index);
        display.UndoRedo.Undo();
        Assert.Equal(0, slider.StopIndex);
        Assert.Same(top, display.UndoRedo.Top);   // one step

        // A drag on the frame: the control moves, its value does not.
        slider.SetStop(5);
        var label = layout.Label;
        Assert.False(ControlRenderer.HitFace(slider, (float)vm.ViewWidth, (float)vm.ViewHeight, new SKPoint(label.MidX, label.MidY)));
        top = display.UndoRedo.Top;
        display.BeginMoveFromControl(vm);
        display.MoveSelected(15, 5);
        display.MoveSelected(15, 5);
        display.EndMoveOperation();
        Assert.Equal((40.0, 20.0, 5), (vm.Left, vm.Top, slider.StopIndex));
        display.UndoRedo.Undo();
        Assert.Equal((10.0, 10.0), (vm.Left, vm.Top));
        Assert.Same(top, display.UndoRedo.Top);
    }

    [Fact]
    public async Task Gate5_VectorExport_AndRenderMatchesTheExportByteForByte()
    {
        string npy = Run();
        string cdd = Cdd("export.cdd", Bound(npy), Pinned(npy, "Pout", "gridPoint", "pinStep"));
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cdd), DataDisplayJson.Options)!;
        var (sources, refusal) = CircuitRF.Cli.CddSources.Bind(cdd, config, config.Tabs, [0], []);
        Assert.Null(refusal);
        var plots = config.Tabs[0].Plots.Select(pc => PlotConfigLoader.LoadPlot(pc, sources!)).ToList();
        foreach (var p in plots) p.LabelColumnsOutside = true;
        var controls = config.Tabs[0].Controls!;
        LinkResolve.Resolve(new LinkDocument([new LinkDocument.Tab("Tab 1", plots, controls)]), sources!);
        var placed = plots.Select((p, i) => CircuitRF.Cli.RenderDataDisplay.Place(p, config.Tabs[0].Plots[i].Left, config.Tabs[0].Plots[i].Top,
            config.Tabs[0].Plots[i].Width, config.Tabs[0].Plots[i].Height, config.Tabs[0].Plots[i].FreqUnit,
            sources!.HasMultipleSources, t => sources.AliasFor(t.EffectiveSourcePath ?? ""))).ToList();
        var current = AppSettings.Current;
        var settings = new AppSettings
        {
            ExportTheme = current.ExportTheme, ExportTransparentBackground = current.ExportTransparentBackground,
            MarkerBoxTransparentBackground = current.MarkerBoxTransparentBackground,
            AlwaysDisplayDataSourcePrefix = current.AlwaysDisplayDataSourcePrefix,
        };
        void Draw(SKCanvas canvas) => PlotComposer.Render(canvas, placed, RenderTheme.Light, settings, PagePlacement.Letter,
                                                          [], 1, 0, 0, controls);

        string svg = PlotDocumentWriter.BuildSvgString(Draw, PagePlacement.Letter);
        Assert.DoesNotContain("<image", svg);
        Assert.Contains("<path", svg);
        byte[] pdf = PlotDocumentWriter.BuildPdfBytes(Draw, PagePlacement.Letter, PlotDocumentWriter.PdfTitleFor("export.pdf"));
        Assert.DoesNotContain("/Subtype /Image", Encoding.Latin1.GetString(pdf));

        // `circuitrf render` of the same document draws the same picture.
        string outSvg = Path.Combine(_dir, "render.svg");
        var (exit, _, err) = CircuitRF.Ui.Tests.Optimization.OptCli.Run("render", cdd, "-o", outSvg);
        Assert.True(exit == 0, err);
        static string WithoutSkiaIds(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\bcl_[0-9a-fA-F]+\b", "cl_");
        Assert.Equal(WithoutSkiaIds(svg), WithoutSkiaIds(File.ReadAllText(outSvg, new UTF8Encoding(false))));

        // --control sets the value for one render: the detail follows it.
        var (exit2, _, err2) = CircuitRF.Ui.Tests.Optimization.OptCli.Run("render", cdd, "-o", outSvg, "--control", "slider1=#7");
        Assert.True(exit2 == 0, err2);
        var (exit3, _, err3) = CircuitRF.Ui.Tests.Optimization.OptCli.Run("render", cdd, "-o", outSvg, "--control", "knob9=#7");
        Assert.NotEqual(0, exit3);
        Assert.Contains("no control 'knob9'. Controls: slider1.", err3);
    }

    [Fact]
    public async Task Gate6_RoundTrip_KeepsEverything_AndAnOlderFileIsUnchanged()
    {
        string npy = Run();
        var slider = Bound(npy);
        slider.SetStop(0);
        var w = await OpenAsync(Cdd("trip.cdd", slider, Pinned(npy, "Pout", "gridPoint", "pinStep")));
        var vm = w.Tabs[0].DataDisplay.Controls.Single();
        vm.Edit(m => { var s = (SliderControl)m; s.Index = 7; s.Locked = true; s.Left = 33; s.Width = 300; s.Size = ControlSize.Custom;
                       s.Accent = "#c04020"; s.Orientation = ControlOrientation.Vertical; }, "test");

        string saved = Path.Combine(_dir, "trip-saved.cdd");
        await w.SaveAllAsync(saved);
        var w2 = await OpenAsync(saved);
        var back = (SliderControl)w2.Tabs[0].DataDisplay.Controls.Single().Model;
        string Json(DisplayControl c) => JsonSerializer.Serialize(c, DataDisplayJson.Options);
        Assert.Equal(Json(vm.Model), Json(back));
        Assert.Equal(7, Detail(w2).Slice![0].Index);

        // A display from before controls: none appear, and none are written back.
        var old = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig { Name = "Tab 1", Plots = [new PlotContainerConfig { PlotType = PlotType.Rect, Width = 300, Height = 200 }] }],
        };
        string oldPath = Path.Combine(_dir, "old.cdd");
        File.WriteAllText(oldPath, JsonSerializer.Serialize(old, DataDisplayJson.Options));
        Assert.DoesNotContain("\"Controls\"", File.ReadAllText(oldPath));
        var w3 = await OpenAsync(oldPath);
        Assert.Empty(w3.Tabs[0].DataDisplay.Controls);
        string resaved = Path.Combine(_dir, "old-saved.cdd");
        await w3.SaveAllAsync(resaved);
        Assert.DoesNotContain("\"Controls\"", File.ReadAllText(resaved));
    }

    [Fact]
    public async Task Gate7_LockPosition_KeepsItInPlace_StillOperates_AndUndoes()
    {
        string npy = Run();
        var w = await OpenAsync(Cdd("lock.cdd", Bound(npy), Pinned(npy, "Pout", "gridPoint", "pinStep")));
        var display = w.Tabs[0].DataDisplay;
        var vm = display.Controls.Single();
        var plot = display.Plots[0];

        var before = display.UndoRedo.Top;
        display.ToggleLock(vm);
        Assert.True(vm.Model.Locked);

        // Selected with an unlocked plot and dragged: only the plot moves.
        display.SelectOnly(plot);
        display.ToggleSelect(vm);
        double plotLeft = plot.Left;
        plot.BeginMove();
        display.MoveSelected(25, 0, plot);
        plot.EndMove();
        Assert.Equal((plotLeft + 25, 10.0), (plot.Left, vm.Left));

        // A locked control still operates.
        var layout = ControlRenderer.Layout((SliderControl)vm.Model, (float)vm.ViewWidth, (float)vm.ViewHeight);
        vm.BeginValueDrag(layout.TrackEnd);
        vm.EndValueDrag();
        Assert.Equal(Grid - 1, ((SliderControl)vm.Model).StopIndex);

        // Undo back to the lock, then undo the lock itself.
        display.UndoRedo.Undo();   // the value
        display.UndoRedo.Undo();   // the move
        display.UndoRedo.Undo();   // the lock
        Assert.False(vm.Model.Locked);
        Assert.Same(before, display.UndoRedo.Top);

        // A mixed selection: the item is unticked, and choosing it locks both.
        display.AddSlider();
        var second = display.Controls[1];
        display.ToggleLock(vm);                       // vm alone (not selected): locked
        display.SelectOnly(vm);
        display.ToggleSelect(second);
        Assert.False(display.AllLocked(vm));
        display.ToggleLock(vm);
        Assert.True(vm.Model.Locked && second.Model.Locked);
        display.ToggleLock(vm);                       // all locked: unlocks all
        Assert.True(!vm.Model.Locked && !second.Model.Locked);
    }

    [Fact]
    public void RangeEnd_TypedInPlace_ReadsInTheSlidersUnit_AndRefusesAnother()
    {
        var s = new SliderControl { Unit = "MHz", Start = 1000, Stop = 3000 };
        Assert.Equal(800, ControlLinkSource.ParseRangeEnd(s, "800 MHz", out _));
        Assert.Equal(800, ControlLinkSource.ParseRangeEnd(s, "0.8 GHz", out _)!.Value, 9);
        Assert.Equal(800, ControlLinkSource.ParseRangeEnd(s, "800", out _));
        Assert.Null(ControlLinkSource.ParseRangeEnd(s, "5 V", out var why));
        Assert.Equal("This slider is in MHz.", why);
        Assert.Null(ControlLinkSource.ParseRangeEnd(s, "abc", out _));
    }

    [Fact]
    public async Task PlotCard_IsSaved_AndOffByDefault()
    {
        string npy = Run();
        var w = await OpenAsync(Cdd("card.cdd", Bound(npy), Pinned(npy, "Pout", "gridPoint", "pinStep")));
        var plot = w.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot;
        Assert.False(plot.Card);
        string plain = Path.Combine(_dir, "card-off.cdd");
        await w.SaveAllAsync(plain);
        Assert.DoesNotContain("\"Card\"", File.ReadAllText(plain));

        plot.Card = true;
        string saved = Path.Combine(_dir, "card-on.cdd");
        await w.SaveAllAsync(saved);
        var w2 = await OpenAsync(saved);
        Assert.True(w2.Tabs[0].DataDisplay.Plots[0].PlotVM.Plot.Card);
    }
}
