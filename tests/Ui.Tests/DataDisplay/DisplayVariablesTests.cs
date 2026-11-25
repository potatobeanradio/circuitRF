// ================================================================
//  DisplayVariablesTests.cs — brief-ddl-7, the seam a display-bound
//  plugin would be handed: a display's variables are listed, read, set
//  and subscribed to below the firewall, and a set through them is the
//  same act as the canvas's — one undo step, a dirty mark, live follow.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class DisplayVariablesTests : IDisposable
{
    private const int Grid = 8, Steps = 6;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddl7_{Guid.NewGuid():N}");
    public DisplayVariablesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Guid M1 = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static double Pout(int g, int p) => 100 * g + p;

    /// <summary><c>Pout[gridPoint, pinStep]</c> and <c>Gain[pinStep, gridPoint]</c>.</summary>
    private string Run()
    {
        var grid = new Axis("gridPoint", [.. Enumerable.Range(0, Grid).Select(i => (double)i)]);
        var pin  = new Axis("pinStep", [.. Enumerable.Range(0, Steps).Select(i => -10.0 + 2 * i)], "dBm");
        var ds = new DataSet();
        ds.Add("Pout", new DataCube([grid, pin], [.. Enumerable.Range(0, Grid * Steps).Select(k => Pout(k / Steps, k % Steps))]));
        ds.Add("Gain", new DataCube([pin, grid], [.. Enumerable.Range(0, Steps * Grid).Select(k => 10.0 * (k / Grid) + k % Grid)]));
        string path = Path.Combine(_dir, "run.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        return path;
    }

    /// <summary>A slider bound to gridPoint driving <c>Pout[@slider1, :]</c>, which carries m1; <c>Gain[@m1, :]</c>
    /// follows m1; a button and a readout beside them.</summary>
    private async Task<(DisplayWindowViewModel W, SliderControl Slider)> OpenAsync()
    {
        string npy = Run();
        var slider = new SliderControl { Name = "slider1", Domain = SliderDomain.Bound, SourcePath = npy, CubeName = "Pout",
                                         AxisName = "gridPoint", Index = 2 };
        var button = new ButtonControl { Name = "button1", Top = 100 };
        var readout = new ReadoutControl { Name = "readout1", Top = 220, Source = M1.ToString("D"), SourceName = "m1" };
        foreach (var c in new DisplayControl[] { slider, button, readout }) c.ApplySize(ControlSize.Medium);
        var pout = new TraceConfig
        {
            SourcePath = npy, CubeName = "Pout", Expression = "Pout[@slider1, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = "gridPoint", Role = AxisRole.PinToIndex, Index = 2, Link = slider.Id, LinkName = "slider1" },
                         new AxisSliceConfig { AxisName = "pinStep", Role = AxisRole.KeepAsX }],
            Markers = [new MarkerConfig { Id = M1.ToString("D"), Name = "m1", PositionStaticX = -10, PositionStaticY = 0 }],
        };
        var gain = new TraceConfig
        {
            SourcePath = npy, CubeName = "Gain", Expression = "Gain[@m1, :]",
            CubeSlice = [new AxisSliceConfig { AxisName = "pinStep", Role = AxisRole.PinToIndex, Index = 0, Link = M1.ToString("D"), LinkName = "m1" },
                         new AxisSliceConfig { AxisName = "gridPoint", Role = AxisRole.KeepAsX }],
        };
        var config = new DataDisplayConfig
        {
            FormatVersion = DataDisplayConfig.CurrentFormatVersion,
            Tabs = [new TabConfig
            {
                Name = "Tab 1",
                Plots = [new PlotContainerConfig { PlotType = PlotType.Rect, Left = 300, Width = 400, Height = 260, Traces = [pout] },
                         new PlotContainerConfig { PlotType = PlotType.Rect, Left = 300, Top = 300, Width = 400, Height = 260, Traces = [gain] }],
                Controls = [slider, button, readout],
            }],
        };
        string cdd = Path.Combine(_dir, "vars.cdd");
        File.WriteAllText(cdd, JsonSerializer.Serialize(config, DataDisplayJson.Options));
        // Saved once by the application and opened again, so the display starts clean, as a user's file does.
        var first = new DisplayWindowViewModel();
        await first.LoadAllAsync(cdd);
        first.ResolveLinks();
        await first.SaveAllAsync(cdd);
        var w = new DisplayWindowViewModel();
        await w.LoadAllAsync(cdd);
        return (w, (SliderControl)w.Tabs[0].DataDisplay.Controls[0].Model);
    }

    private static Trace TraceOf(DisplayWindowViewModel w, int plot) => w.Tabs[0].DataDisplay.Plots[plot].PlotVM.Plot.Traces[0];

    [Fact]
    public async Task List_NamesEveryVariable_WithItsValueInBaseSI_AndNoReadout()
    {
        var (w, _) = await OpenAsync();
        var vars = w.Variables.List();
        Assert.Equal(["m1", "slider1", "button1", "selected"], vars.Select(v => v.Name));
        var m1 = w.Variables.Get("m1")!;
        Assert.Equal(DisplayVariableKind.Marker, m1.Kind);
        Assert.Equal((-10.0, "dBm", 0), (m1.Value!.Value, m1.Unit, m1.Index!.Value));
        Assert.Equal(2, w.Variables.Get("slider1")!.Index);
        Assert.Equal("Off", w.Variables.Get("button1")!.Text);
        Assert.Equal("slider1", w.Variables.Get(w.Tabs[0].DataDisplay.Controls[0].Model.Id)!.Name);   // and by id
        Assert.Null(w.Variables.Get("readout1"));
    }

    [Fact]
    public async Task SettingAControl_IsTheCanvasesAct_OneUndoStep_Dirty_AndFollowed()
    {
        var (w, slider) = await OpenAsync();
        var dd = w.Tabs[0].DataDisplay;
        var top = dd.UndoRedo.Top;
        Assert.False(w.HasUnsavedChanges());

        Assert.True(w.Variables.Set("slider1", "#5").Done);
        Assert.Equal(5, slider.Index);
        Assert.Equal(5, TraceOf(w, 0).Slice![0].Index);                    // followed, live
        Assert.IsType<EditControlCommand>(dd.UndoRedo.Top);                 // the command a drag on the thumb records
        Assert.NotSame(top, dd.UndoRedo.Top);
        Assert.True(w.HasUnsavedChanges());

        dd.UndoRedo.Undo();
        Assert.Equal(2, slider.Index);
        Assert.Equal(2, TraceOf(w, 0).Slice![0].Index);

        // A refusal changes nothing and records nothing.
        var r = w.Variables.Set("slider1", "2.4GHz");
        Assert.Equal(VariableSetStatus.Refused, r.Status);
        Assert.Equal(VariableSetStatus.Unknown, w.Variables.Set("nope", "1").Status);
        Assert.True(w.Variables.Set("button1", "1").Done);
        Assert.True(((ButtonControl)dd.Controls[1].Model).On);
    }

    [Fact]
    public async Task SettingAMarker_MovesIt_ItsDependentsFollow_AndUndoPutsItBack()
    {
        var (w, _) = await OpenAsync();
        var dd = w.Tabs[0].DataDisplay;
        var readout = (ReadoutControl)dd.Controls[2].Model;
        string before = readout.Text!;
        Assert.False(w.HasUnsavedChanges());

        Assert.True(w.Variables.Set("m1", "-4dBm").Done);
        Assert.Equal(3, w.Variables.Get("m1")!.Index);
        Assert.Equal(3, TraceOf(w, 1).Slice![0].Index);   // Gain[@m1, :] followed
        Assert.NotEqual(before, readout.Text);              // and so did the readout of m1
        Assert.True(w.HasUnsavedChanges());

        dd.UndoRedo.Undo();
        Assert.Equal(0, w.Variables.Get("m1")!.Index);
        Assert.Equal(0, TraceOf(w, 1).Slice![0].Index);

        Assert.Equal(VariableSetStatus.Refused, w.Variables.Set("m1", "2.4GHz").Status);   // pinStep is in dBm
        Assert.Equal(VariableSetStatus.Refused, w.Variables.Set("m1", "#99").Status);
    }

    [Fact]
    public async Task ASubscriberHearsOfEveryVariableThatMoved_WhoeverMovedIt()
    {
        var (w, _) = await OpenAsync();
        var heard = new List<string>();
        using (w.Variables.Subscribe(v => heard.Add($"{v.Name}={v.Index}")))
        {
            // The canvas: an arrow step on the slider, which also moves what it pins — but m1 sits on X, so stays put.
            Assert.True(w.Tabs[0].DataDisplay.Controls[0].Step(+1));
            Assert.Equal(["slider1=3"], heard);
            heard.Clear();
            // Through the variables themselves.
            w.Variables.Set("button1", "1");
            Assert.Equal(["button1=1"], heard);
        }
        heard.Clear();
        w.Variables.Set("button1", "0");
        Assert.Empty(heard);
    }
}
