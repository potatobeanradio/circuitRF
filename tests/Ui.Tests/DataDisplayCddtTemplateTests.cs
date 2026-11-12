// ================================================================
//  DataDisplayCddtTemplateTests.cs — the `.cddt` Data Display template:
//  saving one detaches every trace from its file, opening one makes an
//  UNSAVED display showing the source that display selected, and every
//  template circuitRF ships opens with data on every trace.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DataDisplayCddtTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"crf_cddt_{Guid.NewGuid():N}");

    public DataDisplayCddtTemplateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteRun(string dir, string name, double gain)
    {
        Directory.CreateDirectory(dir);
        var freq = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var ds   = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "Gain", new DataCube(new[] { freq }, new[] { gain, gain }));
        DataSetExporter.Export(ds, Path.Combine(dir, name), ExportFormat.Npy);
    }

    private static async Task<DisplayWindowViewModel> WindowOnAsync(string results, string selected)
    {
        var w = new DisplayWindowViewModel();
        w.DataSourceLibrary.ResultsRootProvider = () => results;
        await w.DataSourceLibrary.SelectDataSourceAsync(selected);
        return w;
    }

    [Fact]
    public async Task SavedAsTemplate_ThenOpened_ShowsTheOpeningDisplaysSource_Unsaved()
    {
        string results = Path.Combine(_root, "results");
        WriteRun(results, "a.npy", 10);
        WriteRun(results, "b.npy", 20);

        var from = await WindowOnAsync(results, "a.npy");
        var src  = from.DataDisplay!.Plots[0];
        src.Inspector.PlotType = PlotType.Rect;
        src.Inspector.AddTraceCommand.Execute(null);
        string cddt = Path.Combine(_root, "mine" + DataDisplayTemplate.Extension);
        await from.SaveAsTemplateAsync(cddt);

        var saved = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(cddt), DataDisplayViewModel.JsonOpts)!;
        Assert.Null(saved.SelectedDataSource);
        Assert.Empty(saved.SourceAliases);
        Assert.All(saved.Tabs.SelectMany(t => t.Plots).SelectMany(p => p.Traces),
                   t => Assert.Equal(DataSourceRef.Selected, t.SourcePath));

        var to = await WindowOnAsync(results, "b.npy");
        await to.LoadTemplateAsync(File.ReadAllText(cddt), "mine.cddt");

        var trace = to.DataDisplay!.Plots.Single().PlotVM.Plot.Traces.Single();
        Assert.Equal(20, trace.Points[0].Y, 6);
        Assert.Null(to.CurrentConfigPath);
        Assert.True(to.HasUnsavedChanges());
    }

    [Fact]
    public async Task EveryShippedTemplate_OpensOnATwoPort_WithDataOnEveryTrace()
    {
        string snp = Path.Combine(RepoRoot(), "testdata", "ndf", "potentially_unstable_amp.s2p");
        Assert.NotEmpty(ShippedDataDisplayTemplates.Stems);
        foreach (var stem in ShippedDataDisplayTemplates.Stems)
        {
            var w = new DisplayWindowViewModel();
            await w.DataSourceLibrary.SelectDataSourceAsync(snp);
            await w.LoadTemplateAsync(ShippedDataDisplayTemplates.LoadJson(stem), stem);

            var traces = w.Tabs.SelectMany(t => t.DataDisplay.Plots).SelectMany(p => p.PlotVM.Plot.Traces).ToList();
            Assert.NotEmpty(traces);
            Assert.All(traces, t => Assert.True(
                t.Points.Count > 0 || t.StabilityCircleRadii.Count > 0, $"{stem}: {t.Description} drew nothing"));
        }
    }

    /// <summary>A `.cddt` in a workspace folder is a row of its own kind, which double-click opens as a new
    /// display (WorkspaceViewModel.OpenNode) — not an OtherFile with no route.</summary>
    [Fact]
    public void ATemplateInTheWorkspace_IsListedInTheTreeAsATemplate()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "templates"));
        File.WriteAllText(Path.Combine(ws, "templates", "Mine" + DataDisplayTemplate.Extension), "{}");

        var root     = CircuitRF.Ui.Schematic.WorkspaceScanner.Scan(ws);
        var template = root.Children.Single(n => n.Name == "templates").Children.Single();

        Assert.Equal(CircuitRF.Ui.Schematic.NodeKind.DataDisplayTemplateFile, template.Kind);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
