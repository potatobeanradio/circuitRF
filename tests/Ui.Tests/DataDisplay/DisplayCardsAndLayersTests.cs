// ================================================================
//  DisplayCardsAndLayersTests.cs — the Data Display's cards, layers and
//  locks: what a .cdd saves of them (and leaves unwritten by default),
//  the stacking a save keeps, a locked plot left behind by a move, the
//  card spans, a marker box taking its plot's card, a bound slider's
//  frequency unit and the button's switch style. The looks themselves
//  are checked in UiSnapshot.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class DisplayCardsAndLayersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_cards_{Guid.NewGuid():N}");
    public DisplayCardsAndLayersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, DataDisplayJson.Options);
    private static T Back<T>(string json) => JsonSerializer.Deserialize<T>(json, DataDisplayJson.Options)!;

    [Fact]
    public void NewFields_RoundTrip_AndDefaultsAreNotWritten()
    {
        // At their defaults nothing new is written, so a display from before saves the bytes it saved.
        var slider = new SliderControl { Name = "slider1" };
        var lamp   = new ButtonControl { Name = "button1" };
        string sj = Json<DisplayControl>(slider), bj = Json<DisplayControl>(lamp);
        foreach (var key in new[] { "\"Card\"", "\"ZIndex\"", "\"FrequencyUnit\"" }) Assert.DoesNotContain(key, sj);
        foreach (var key in new[] { "\"Style\"", "\"Orientation\"", "\"Card\"" }) Assert.DoesNotContain(key, bj);
        Assert.DoesNotContain("\"Locked\"", Json<CanvasAnnotation>(new CanvasText(0, 0, "a")));
        Assert.DoesNotContain("\"Locked\"", Json(new PlotContainerConfig()));
        Assert.DoesNotContain("\"ZIndex\"", Json(new PlotContainerConfig()));
        Assert.DoesNotContain("\"InfoBoxCard\"", Json(new MarkerConfig()));

        // Set, each comes back.
        var sw = new ButtonControl { Name = "s", Style = ButtonStyle.Switch, Orientation = ControlOrientation.Vertical,
                                     ShowLabel = false, ShowCard = false, ZIndex = 7 };
        var back = (ButtonControl)Back<DisplayControl>(Json<DisplayControl>(sw));
        Assert.Equal((ButtonStyle.Switch, ControlOrientation.Vertical, false, false, 7),
                     (back.Style, back.Orientation, back.ShowLabel, back.ShowCard, back.ZIndex));
        Assert.IsType<PanelControl>(Back<DisplayControl>(Json<DisplayControl>(new PanelControl { Name = "panel1" })));
        Assert.False(ControlLinkSource.IsSource(new PanelControl()));   // a panel has no value to follow
        Assert.Equal("MHz", ((SliderControl)Back<DisplayControl>(Json<DisplayControl>(new SliderControl { FrequencyUnit = "MHz" }))).FrequencyUnit);
        Assert.True(((CanvasText)Back<CanvasAnnotation>(Json<CanvasAnnotation>(new CanvasText(0, 0, "a") { Locked = true }))).Locked);
        Assert.True(Back<PlotContainerConfig>(Json(new PlotContainerConfig { Locked = true })).Locked);
        Assert.False(Back<MarkerConfig>(Json(new MarkerConfig { InfoBoxCard = false })).InfoBoxCard);
    }

    private string Run()
    {
        var freq = new Axis("freq", [.. Enumerable.Range(0, 11).Select(i => 1e9 + i * 1e8)], "Hz");
        var ds = new DataSet();
        ds.Add("Gain", new DataCube([freq], [.. Enumerable.Range(0, 11).Select(i => (double)i)]));
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    private string Cdd(string name, bool card = false)
    {
        string npy = Run();
        PlotContainerConfig P(double left) => new()
        {
            PlotType = PlotType.Rect, Left = left, Top = 0, Width = 400, Height = 250, Card = card,
            Traces = [new TraceConfig
            {
                SourcePath = npy, CubeName = "Gain", Expression = "Gain",
                Markers = [new MarkerConfig { Id = Guid.NewGuid().ToString("D"), Name = left == 0 ? "m1" : "m2",
                                              PositionStaticX = 1.5e9f, PositionStaticY = 5 }],
            }],
        };
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig
            {
                Name = "Tab 1", Plots = [P(0), P(500)],
                Controls = [new PanelControl { Name = "panel1", Width = 900, Height = 300 },
                            new ButtonControl { Name = "button1", Left = 950, Width = 96, Height = 112 }],
            }],
        };
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, Json(config));
        return path;
    }

    private static async Task<DisplayWindowViewModel> OpenAsync(string cdd)
    {
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return w;
    }

    [Fact]
    public async Task Stacking_IsSaved_AndPanelsTakeTheirOwnLayer()
    {
        string cdd = Cdd("z.cdd");
        var w = await OpenAsync(cdd);
        var d = w.Tabs[0].DataDisplay;
        Assert.Equal(["panel1"], d.PanelControls.Select(c => c.Model.Name));
        Assert.Equal(["button1"], d.ForegroundControls.Select(c => c.Model.Name));

        d.SelectOnly(d.Plots[1]);
        d.SelectOnly(d.Plots[0]);   // the first plot is brought over the second
        await w.SaveAllAsync(cdd);

        var again = (await OpenAsync(cdd)).Tabs[0].DataDisplay;
        Assert.True(again.Plots[0].ZIndex > again.Plots[1].ZIndex);
    }

    [Fact]
    public async Task LockedPlot_StaysWhereItIs_WhenTheSelectionMoves()
    {
        var d = (await OpenAsync(Cdd("lock.cdd"))).Tabs[0].DataDisplay;
        d.Plots[0].PlotVM.Plot.PositionLocked = true;
        d.SelectOnly(d.Plots[0]);
        d.ToggleSelect(d.Plots[1]);
        d.BeginMoveOperation();
        d.MoveSelected(40, 30);
        d.EndMoveOperation();
        Assert.Equal((0.0, 0.0), (d.Plots[0].Left, d.Plots[0].Top));
        Assert.Equal((540.0, 30.0), (d.Plots[1].Left, d.Plots[1].Top));
    }

    [Fact]
    public async Task SelectAll_LocksEverything_AsOneUndoStep()
    {
        var d = (await OpenAsync(Cdd("all.cdd"))).Tabs[0].DataDisplay;
        d.SelectAll();
        var sel = d.SelectionInspector;
        Assert.True(sel.IsActive && sel.HasLock && sel.HasCard && !sel.OnlyControls);
        Assert.Null(d.SelectedControl);   // one control among others is not edited alone

        sel.Locked = true;
        Assert.True(d.Plots.All(p => p.PlotVM.Plot.PositionLocked) && d.Controls.All(c => c.Model.Locked));
        d.UndoRedo.Undo();
        Assert.True(d.Plots.All(p => !p.PlotVM.Plot.PositionLocked) && d.Controls.All(c => !c.Model.Locked));

        d.SelectOnly(d.Controls[0]);
        d.ToggleSelect(d.Controls[1]);
        // A panel and a button: both controls, but a panel has no accent and is its own card.
        Assert.Equal((true, false, true), (sel.OnlyControls, sel.HasAccent, sel.HasCard));
    }

    [Fact]
    public async Task Marquee_PicksAPlotByItsCard()
    {
        var d = (await OpenAsync(Cdd("marquee.cdd"))).Tabs[0].DataDisplay;
        var p = d.Plots[1];
        var (l, _) = PlotCanvasGeometry.CardSpan(p.PlotVM.Plot, p.ViewWidth, p.ViewHeight);
        Assert.True(l > 2);   // the card starts inside the canvas
        double y = p.ViewTop + p.ViewHeight / 2;
        d.SelectItemsInRect(new Avalonia.Rect(p.ViewLeft + 1, y, 1, 1), false);   // beside the card
        Assert.False(p.IsSelected);
        d.SelectItemsInRect(new Avalonia.Rect(p.ViewLeft + l + 4, y, 1, 1), false);
        Assert.True(p.IsSelected);
    }

    [Fact]
    public async Task BringingForward_IsAnUndoStep_AndDirties()
    {
        var w = await OpenAsync(Cdd("restack.cdd"));
        var d = w.Tabs[0].DataDisplay;
        Assert.False(w.HasUnsavedChanges());

        d.SelectOnly(d.Plots[0]);   // the two load level, so this brings it forward
        Assert.True(d.Plots[0].ZIndex > d.Plots[1].ZIndex);
        Assert.True(w.HasUnsavedChanges());
        var top = d.UndoRedo.Top;
        d.SelectOnly(d.Plots[0]);   // already in front: nothing changes
        Assert.Same(top, d.UndoRedo.Top);

        d.UndoRedo.Undo();
        Assert.False(w.HasUnsavedChanges());
        d.UndoRedo.Redo();
        Assert.True(d.Plots[0].ZIndex > d.Plots[1].ZIndex);
        Assert.True(w.HasUnsavedChanges());

        // Controls too, each among its own layer: the button over the button layer, which it alone is in.
        d.UndoRedo.Undo();
        d.SelectOnly(d.Controls[1]);
        Assert.False(w.HasUnsavedChanges());
    }

    [Fact]
    public async Task DraggingAnUnselectedPlot_TellsTheInspectorWhenTheDragEnds()
    {
        var d = (await OpenAsync(Cdd("drag.cdd"))).Tabs[0].DataDisplay;
        d.SelectOnly(d.Plots[0]);
        d.BeginMoveFromPlot(d.Plots[1]);
        Assert.True(d.Plots[1].IsSelected && !d.Plots[0].IsSelected);   // the canvas shows it at once
        Assert.Same(d.Plots[0], d.SingleSelectedPlot);                   // the Properties panel waits
        d.MoveSelected(10, 0);
        d.EndMoveOperation();
        Assert.Same(d.Plots[1], d.SingleSelectedPlot);
    }

    [Fact]
    public async Task HidingAMarkerBox_IsAnUndoStep_AndDirties()
    {
        var w = await OpenAsync(Cdd("box.cdd"));
        var d = w.Tabs[0].DataDisplay;
        var c = d.Plots[0];
        var marker = c.PlotVM.Plot.Traces[0].Markers[0];
        Assert.True(marker.ShowInfoBox);
        Assert.False(w.HasUnsavedChanges());

        marker.ShowInfoBox = false;     // what the box's menu does
        c.RequestInfoBoxRebuild();
        c.RequestPlotRedraw();
        Assert.True(w.HasUnsavedChanges());

        d.UndoRedo.Undo();
        Assert.True(c.PlotVM.Plot.Traces[0].Markers[0].ShowInfoBox);
        Assert.False(w.HasUnsavedChanges());
    }

    [Fact]
    public async Task NewMarkerBox_TakesItsPlotsCard()
    {
        var d = (await OpenAsync(Cdd("marker.cdd", card: true))).Tabs[0].DataDisplay;
        var trace = d.Plots[0].PlotVM.Plot.Traces[0];
        var marker = new Marker(trace, 0, false, false, 1);
        d.Plots[0].OnMarkerAdded(marker, trace);
        Assert.True(marker.InfoBoxCard);

        d.Plots[1].PlotVM.Plot.Card = false;
        var other = new Marker(trace, 0, false, false, 2);
        d.Plots[1].OnMarkerAdded(other, trace);
        Assert.False(other.InfoBoxCard);
    }

    [Fact]
    public void BoundFrequency_ReadsInTheUnitTheControlWasPlacedWith()
    {
        var s = new SliderControl { Domain = SliderDomain.Bound, BoundUnit = "Hz", BoundValues = [2.4e9] };
        Assert.Equal("2.4 GHz", ControlRenderer.Format(s, 2.4e9, "Hz"));   // Automatic: engineering notation
        s.FrequencyUnit = "MHz";
        Assert.Equal("2400 MHz", ControlRenderer.Format(s, 2.4e9, "Hz"));
    }

    [Fact]
    public void CardSpan_FitsARectPlotsLabels_AndTakesInASmithPlotsStrips()
    {
        var rect = new Plot(PlotType.Rect, FreqUnit.GHz) { Card = true };
        var vp = PlotRenderer.BuildTransforms(rect, (520, 320)).Viewport;
        var (l, r) = PlotCanvasGeometry.CardSpan(rect, 520, 320);
        Assert.InRange(l, 1, vp.X * 520);                     // tighter than the canvas, still around the box
        Assert.InRange(r, (vp.X + vp.Width) * 520, 519.9);

        var smith = new Plot(PlotType.Smith, FreqUnit.GHz) { Card = true };
        var (sl, sr) = PlotCanvasGeometry.CardSpan(smith, 360, 360, leftStrips: 2, rightStrips: 1, stripWidth: 18);
        Assert.True(sl < 0 && sr > 360);
    }

    [Fact]
    public void Switch_OperatesOnItsTrack_AndControlsGoSmall()
    {
        var sw = new ButtonControl { Name = "s", Style = ButtonStyle.Switch };
        sw.ApplySize(ControlSize.Medium);
        var track = ControlRenderer.SwitchLayout(sw, (float)sw.Width, (float)sw.Height).Track;
        Assert.True(ControlRenderer.HitFace(sw, (float)sw.Width, (float)sw.Height, new SKPoint(track.MidX, track.MidY)));
        Assert.False(ControlRenderer.HitFace(sw, (float)sw.Width, (float)sw.Height, new SKPoint(1, 1)));

        sw.Orientation = ControlOrientation.Vertical;
        sw.ApplySize(ControlSize.Medium);
        Assert.True(sw.Height > sw.Width);
        Assert.Equal((12.0, 12.0), sw.MinimumSize());
        Assert.Equal((48.0, 56.0), new KnobControl().MinimumSize());   // half the Small preset each way
        Assert.Equal((100.0, 30.0), new SliderControl().MinimumSize());
        Assert.Equal((37.5, 100.0), new SliderControl { Orientation = ControlOrientation.Vertical }.MinimumSize());
    }
}
