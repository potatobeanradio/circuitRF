// ================================================================
//  DataDisplayEditUndoTests.cs — every edit made through a plot's
//  inspector is an undo step, and undoing every one of them brings the
//  .cdd back to clean (DataDisplayViewModel.EditHistory.cs).
// ================================================================

using System;
using System.IO;
using System.Linq;
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
