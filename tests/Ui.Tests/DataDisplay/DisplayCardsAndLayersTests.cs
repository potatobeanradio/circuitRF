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
            Traces = [new TraceConfig { SourcePath = npy, CubeName = "Gain", Expression = "Gain" }],
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
        d.SelectOnly(d.Controls[1]);
        await w.SaveAllAsync(cdd);

        var again = (await OpenAsync(cdd)).Tabs[0].DataDisplay;
        Assert.True(again.Plots[0].ZIndex > again.Plots[1].ZIndex);
        Assert.True(again.Controls[1].ZIndex > again.Controls[0].ZIndex);
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
    public void Switch_OperatesOnItsTrack_AndAButtonGoesSmall()
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
    }
}
