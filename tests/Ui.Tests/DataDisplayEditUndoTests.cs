// ================================================================
//  DataDisplayEditUndoTests.cs — every edit made through a plot's
//  inspector is an undo step, and undoing every one of them brings the
//  .cdd back to clean (DataDisplayViewModel.EditHistory.cs).
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
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayEditUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crf_ddundo_{Guid.NewGuid():N}");

    public DataDisplayEditUndoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A saved display of one Rect plot with one trace on a real cube, reopened.</summary>
    private async Task<DisplayWindowViewModel> OpenSavedDisplayAsync()
    {
        var freq = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var ds   = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", new DataCube(new[] { freq }, new[] { 10.0, 12.0, 11.0 }));
        ds.AddToGroup(DataSet.MeasurementsGroup, "NF",   new DataCube(new[] { freq }, new[] { 1.0, 0.8, 0.9 }));
        DataSetExporter.Export(ds, Path.Combine(_dir, "amp.npy"), ExportFormat.Npy);

        // Written by hand rather than by a first window, so what is under test starts from a file.
        string cdd = Path.Combine(_dir, "d.cdd");
        File.WriteAllText(cdd, """
            { "FormatVersion": 2, "SelectedDataSource": "amp.npy",
              "Tabs": [ { "Name": "Page 1", "Plots": [ {
                  "Left": 0, "Top": 0, "Width": 520, "Height": 320, "PlotType": "Rect", "FreqUnit": "GHz",
                  "Traces": [ { "SourcePath": "run.npy", "Expression": "Gain" } ] } ] } ] }
            """);

        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await w.LoadAllAsync(cdd);
        Assert.False(w.HasUnsavedChanges());
        Assert.Single(w.DataDisplay!.Plots[0].PlotVM.Plot.Traces);
        return w;
    }

    private static void UndoAll(DataDisplayViewModel d)
    {
        while (d.UndoRedo.CanUndo) d.UndoRedo.Undo();
    }

    [Fact]
    public async Task MarkerFrequencyEdit_SnapsAndUndoesCubePosition()
    {
        var window = await OpenSavedDisplayAsync();
        var display = window.DataDisplay!;
        var container = display.Plots[0];
        var trace = container.PlotVM.Plot.Traces[0];
        var marker = new Marker(trace, 0, false, false, 1, FreqUnit.GHz);
        trace.Markers.Add(marker);
        Assert.True(trace.TrySetMarkerFrequency(marker, 1e9, out _));
        marker.Function = MarkerFunction.Minimum;
        var box = new MarkerInfoBoxViewModel(marker, trace, container,
            () => FreqUnit.GHz, () => RenderTheme.Light, display, 0, 0);

        Assert.True(box.TrySetFrequency(2.6e9));
        Assert.Equal(MarkerFunction.Normal, marker.Function);
        Assert.True(trace.TryGetMarkerFrequency(marker, out double frequency));
        Assert.Equal(3e9, frequency);
        display.UndoRedo.Undo();
        Assert.Equal(MarkerFunction.Minimum, marker.Function);
        Assert.True(trace.TryGetMarkerFrequency(marker, out frequency));
        Assert.Equal(1e9, frequency);
        display.UndoRedo.Redo();
        Assert.Equal(MarkerFunction.Normal, marker.Function);
        Assert.True(trace.TryGetMarkerFrequency(marker, out frequency));
        Assert.Equal(3e9, frequency);
    }

    [Fact]
    public async Task MarkerFunction_IsSavedInTraceConfiguration()
    {
        var window = await OpenSavedDisplayAsync();
        var trace = window.DataDisplay!.Plots[0].PlotVM.Plot.Traces[0];
        var marker = new Marker(trace, 0, true, false, 1, FreqUnit.GHz) { Function = MarkerFunction.Vertical };
        trace.Markers.Add(marker);
        var config = DataDisplayViewModel.BuildTraceConfig(trace, _dir);
        var json = JsonSerializer.Serialize(config.Markers[0], DataDisplayJson.Options);
        var restored = JsonSerializer.Deserialize<MarkerConfig>(json, DataDisplayJson.Options);
        Assert.Equal(MarkerFunction.Vertical, restored!.Function);
        Assert.Equal(MarkerFunction.Normal, JsonSerializer.Deserialize<MarkerConfig>("{}", DataDisplayJson.Options)!.Function);
        Assert.Equal(MarkerFunction.Vertical, new Marker(marker).Function);

        string path = Path.Combine(_dir, "marker.cdd");
        await window.SaveAllAsync(path);
        var reopened = new DisplayWindowViewModel();
        reopened.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await reopened.LoadAllAsync(path);
        var savedMarker = Assert.Single(reopened.DataDisplay!.Plots[0].PlotVM.Plot.Traces[0].Markers);
        Assert.Equal(MarkerFunction.Vertical, savedMarker.Function);
        Assert.True(savedMarker.IsMulti);
    }

    [Fact]
    public void TabAnnotations_RoundTripInOrder_AndAreOptional()
    {
        var options = DataDisplayJson.Options;
        var tab = JsonSerializer.Deserialize<TabConfig>("""{"Name":"Page 1"}""", options)!;
        Assert.Null(tab.Annotations);
        Assert.DoesNotContain("Annotations", JsonSerializer.Serialize(tab, options));

        tab.Annotations = [new CanvasText(10, -20, "Gain\nand noise"),
                           new CanvasLine(-10, 4, 90, 4),
                           new CanvasLine(10, 30, 90, 35, CanvasLineStyle.Dashed),
                           new CanvasBox(90, 80, 10, 20, CanvasLineStyle.Dashed) { Color = "#FF008000" }];
        var json = JsonSerializer.Serialize(tab, options);
        var restored = JsonSerializer.Deserialize<TabConfig>(json, options)!;
        Assert.Equal(tab.Annotations, restored.Annotations);
    }

    [Fact]
    public async Task TabAnnotationEdit_KeepsSelection_AndUndoRestoresProperties()
    {
        var window = await OpenSavedDisplayAsync();
        var display = window.DataDisplay!;
        var line = new CanvasLine(10, 20, 40, 20);
        display.AddAnnotation(line);
        display.SelectAnnotation(0);
        var styled = line with { Style = CanvasLineStyle.Dashed };
        var edited = styled with { Color = "#FF008000" };
        display.AnnotationInspector.LineStyle = CanvasLineStyle.Dashed;
        display.AnnotationInspector.SetColor(edited.Color);
        Assert.Equal(edited, display.SelectedAnnotation);
        display.UndoRedo.Undo();
        Assert.Equal(styled, display.SelectedAnnotation);
        display.UndoRedo.Undo();
        Assert.Equal(line, display.SelectedAnnotation);
        display.UndoRedo.Redo();
        display.UndoRedo.Redo();
        Assert.Equal(edited, display.SelectedAnnotation);
        display.DeleteSelected();
        Assert.Empty(display.Annotations);
        Assert.Null(display.SelectedAnnotation);
        display.UndoRedo.Undo();
        Assert.Equal(edited, display.SelectedAnnotation);
        display.SelectOnly(display.Plots[0]);
        Assert.Null(display.SelectedAnnotation);
    }

    [Fact]
    public async Task TabAnnotations_AreUndoable_AndSurviveSaveAndReopen()
    {
        var window = await OpenSavedDisplayAsync();
        var display = window.DataDisplay!;
        var text = new CanvasText(10, 20, "Gain\nvs frequency")
            { FontSize = 22, Rotation = CanvasTextRotation.R270, WrapWidth = 75 };
        var line = new CanvasLine(10, 60, 90, 60, CanvasLineStyle.Dashed) { Color = "#FF008000" };

        display.AddAnnotation(text);
        display.AddAnnotation(line);
        Assert.True(window.HasUnsavedChanges());

        display.UndoRedo.Undo();
        Assert.Equal(text, Assert.Single(display.Annotations));
        display.UndoRedo.Undo();
        Assert.Empty(display.Annotations);
        Assert.False(window.HasUnsavedChanges());

        display.UndoRedo.Redo();
        display.UndoRedo.Redo();
        Assert.True(window.HasUnsavedChanges());

        string path = Path.Combine(_dir, "annotated.cdd");
        await window.SaveAllAsync(path);
        Assert.False(window.HasUnsavedChanges());
        var reopened = new DisplayWindowViewModel();
        reopened.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await reopened.LoadAllAsync(path);
        Assert.Equal(new CanvasAnnotation[] { text, line }, reopened.DataDisplay!.Annotations);
        Assert.False(reopened.DataDisplay!.UndoRedo.CanUndo);
        Assert.False(reopened.HasUnsavedChanges());
    }

    [Fact]
    public async Task TraceCardEdits_AreEachOneStep_AndUndoingAllMakesTheDocumentClean()
    {
        var w    = await OpenSavedDisplayAsync();
        var d    = w.DataDisplay!;
        var insp = d.Plots[0].Inspector;
        string expression = insp.Traces[0].Trace.Expression!;

        insp.Traces[0].CommitSpec("NF * 2");
        Assert.True(w.HasUnsavedChanges());
        insp.Traces[0].CommitTraceLabel("Twice the noise");
        insp.AddTraceCommand.Execute(null);
        Assert.Equal(2, insp.Traces.Count);
        insp.RemoveTrace(insp.Traces[0]);
        Assert.Single(insp.Traces);

        UndoAll(d);

        Assert.False(w.HasUnsavedChanges());
        Assert.Single(insp.Traces);
        Assert.Equal(expression, insp.Traces[0].Trace.Expression);
        Assert.Null(insp.Traces[0].Trace.Label);
    }

    [Fact]
    public async Task UndoThenRedo_PutsTheEditBack()
    {
        var w    = await OpenSavedDisplayAsync();
        var d    = w.DataDisplay!;
        var insp = d.Plots[0].Inspector;

        insp.Traces[0].CommitSpec("NF * 2");
        string edited = d.SavedFormOf(d.Plots[0]);

        d.UndoRedo.Undo();
        Assert.False(w.HasUnsavedChanges());
        d.UndoRedo.Redo();

        Assert.Equal(edited, d.SavedFormOf(d.Plots[0]));
        Assert.Equal("NF * 2", insp.Traces[0].Trace.Expression);
    }

    [Fact]
    public async Task PlotTypeChange_IsUndoneWithItsTracesAndSize()
    {
        var w    = await OpenSavedDisplayAsync();
        var d    = w.DataDisplay!;
        var c    = d.Plots[0];

        c.Inspector.PlotType = PlotType.Table;
        Assert.True(w.HasUnsavedChanges());

        UndoAll(d);

        Assert.Equal(PlotType.Rect, c.PlotVM.Plot.PlotType);
        Assert.Equal(PlotType.Rect, c.Inspector.PlotType);
        Assert.False(w.HasUnsavedChanges());
    }

    [Fact]
    public async Task ATitleTypedALetterAtATime_IsOneStep()
    {
        var w = await OpenSavedDisplayAsync();
        var d = w.DataDisplay!;
        var plot = d.Plots[0].PlotVM.Plot;
        plot.CustomTitleOn = true;          // its own step: a different field
        d.Plots[0].RequestPlotRedraw();
        int before = UndoDepth(d);

        foreach (var title in new[] { "G", "Ga", "Gai", "Gain" })
        {
            plot.CustomTitle = title;
            d.Plots[0].RequestPlotRedraw();
        }

        Assert.Equal(before + 1, UndoDepth(d));
        UndoAll(d);
        Assert.False(w.HasUnsavedChanges());
    }

    private static int UndoDepth(DataDisplayViewModel d)
    {
        // The stack is private; count by undoing onto a scratch copy is not possible, so read it the
        // way the menu does — by how many undos it takes to empty it, then redo them all back.
        int n = 0;
        while (d.UndoRedo.CanUndo) { d.UndoRedo.Undo(); n++; }
        for (int i = 0; i < n; i++) d.UndoRedo.Redo();
        return n;
    }
}
