// ================================================================
//  DerivedTraceRerunFromCddTests.cs
//
//  The owner's actual scenario, driven through the REAL .cdd load path rather than through the
//  trace picker: open a saved display holding a Max Gain trace and a stability circle, re-run the
//  analysis, and require both to still be there.
//
//  The picker-driven gates in SimulatedSourceNetworkMetricsTests were not enough. A trace the
//  PICKER builds is bound to `Entry.NetworkView`; a trace the LOADER builds was bound to
//  `libEntry?.Snp` — a different question with a different answer for a simulated source, which
//  has no Snp at all. So the load path could hand a derived trace an SNP that belongs to no
//  library entry, and the first LibraryChanged after a run swept it away no matter what the
//  stale-check had been taught to accept.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class DerivedTraceRerunFromCddTests : IDisposable
{
    private readonly string _dir;
    private readonly string _results;

    public DerivedTraceRerunFromCddTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-cddrerun-" + Guid.NewGuid().ToString("N")[..8]);
        _results = Path.Combine(_dir, "results");
        Directory.CreateDirectory(_results);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A run-shaped DataSet: S and Z0 in a NAMED analysis group, as a real S-parameter run writes.</summary>
    private static DataSet GroupedRun(double scale = 1.0)
    {
        double[] freqs = [1e9, 2e9, 3e9];
        var s = new Complex[freqs.Length * 4];
        for (int f = 0; f < freqs.Length; f++)
        {
            s[f * 4 + 0] = new Complex(0.5 * scale, -0.1);
            s[f * 4 + 1] = new Complex(0.05, 0.02);
            s[f * 4 + 2] = new Complex(2.0 * scale, 0.3);
            s[f * 4 + 3] = new Complex(0.4, -0.2);
        }

        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube(
            [new Axis("freq", freqs, "Hz"),
             new Axis("i", [1, 2], "port"),
             new Axis("j", [1, 2], "port")],
            s));
        ds.AddToGroup("SP1", "Z0", new DataCube(
            [new Axis("port", [1, 2], "port")],
            [new Complex(50, 0), new Complex(50, 0)]));
        return ds;
    }

    /// <summary>
    /// A .cdd shaped like a real saved display: the source is named by its logical id, while each
    /// trace's SourcePath carries <see cref="DataSourceRef.Selected"/> — the "whichever source is
    /// selected" sentinel, whose literal value happens to be the string "run.npy". A trace that
    /// takes the toolbar's own selection persists the sentinel, not the file name.
    /// </summary>
    private static string CddJson(string sourceRef) => $$"""
    {
      "FormatVersion": 2,
      "SelectedDataSource": "{{sourceRef}}",
      "SourceAliases": { "{{sourceRef}}": "run" },
      "Tabs": [{
        "Name": "t", "ZoomLevel": 1.0, "ViewOffsetX": 0, "ViewOffsetY": 0,
        "Plots": [
          {
            "Left": 0, "Top": 0, "Width": 420, "Height": 420,
            "PlotType": "Smith", "FreqUnit": "GHz",
            "Traces": [{
              "SourcePath": "{{DataSourceRef.Selected}}", "Row": 0, "Col": 0, "MatrixType": "S",
              "Derived": "SourceStabilityCircle", "InputPort": 1, "OutputPort": 2,
              "PassivityWholeNetwork": true, "YAxis": "Complex",
              "Z0": "50", "Z0Override": false, "CubeName": null, "CubeSlice": [],
              "CubeTransform": "None", "Expression": null
            }]
          },
          {
            "Left": 0, "Top": 500, "Width": 520, "Height": 320,
            "PlotType": "Rect", "FreqUnit": "GHz",
            "Traces": [{
              "SourcePath": "{{DataSourceRef.Selected}}", "Row": 0, "Col": 0, "MatrixType": "S",
              "Derived": "MaxGain", "InputPort": 1, "OutputPort": 2,
              "PassivityWholeNetwork": true, "YAxis": "Db",
              "Z0": "50", "Z0Override": false, "CubeName": null, "CubeSlice": [],
              "CubeTransform": "None", "Expression": null
            }]
          }
        ]
      }]
    }
    """;

    private const string NpyName = "S-Param.npy";

    private async Task<DisplayWindowViewModel> OpenDisplayAsync(string npyName = NpyName)
    {
        string npy = Path.Combine(_results, npyName);
        DataSetExporter.Export(GroupedRun(), npy, ExportFormat.Npy);

        string cdd = Path.Combine(_results, "display.cdd");
        await File.WriteAllTextAsync(cdd, CddJson(npyName));

        var win = new DisplayWindowViewModel();
        win.DataSourceLibrary.ResultsRootProvider = () => _results;
        win.DataSourceLibrary.RefreshAvailableDataSources();
        await win.DataSourceLibrary.SelectDataSourceAsync(npyName);
        await win.LoadAllAsync(cdd);
        return win;
    }

    private static Trace[] AllTraces(DisplayWindowViewModel win) =>
        win.Tabs.SelectMany(t => t.DataDisplay.Plots)
                .SelectMany(c => c.PlotVM.Plot.Traces)
                .ToArray();

    /// <summary>
    /// The post-run refresh a real workspace performs, in the same order:
    /// <c>WorkspaceViewModel.RefreshOpenDataDisplaysAsync</c>.
    /// </summary>
    private static async Task RefreshOpenDisplayAsync(DisplayWindowViewModel win, string changedAbs)
    {
        var lib = win.DataSourceLibrary;
        lib.RefreshAvailableDataSources();
        await lib.ReloadChangedAsync([changedAbs]);
        if (lib.SelectedDataSourceAbs is { } selAbs &&
            string.Equals(Path.GetFullPath(selAbs), Path.GetFullPath(changedAbs),
                          StringComparison.OrdinalIgnoreCase))
        {
            await lib.SelectDataSourceAsync(lib.SelectedDataSourceRef);
        }
    }

    private static bool HasGeometry(Trace t) =>
        t.IsStabilityCircle ? t.StabilityCircleCentres.Count > 0 : t.Points.Count > 0;

    /// <summary>
    /// Opening the saved display must bind both derived traces to real data. The loader read
    /// `libEntry?.Snp`, which is null for a simulated run, so the `snp is null` guard below it
    /// skipped every derived trace outright — or, when the source ref resolved to a broken entry,
    /// bound them to that entry's placeholder SNP.
    /// </summary>
    [Fact]
    public async Task OpeningTheDisplay_BindsBothDerivedTraces()
    {
        var win = await OpenDisplayAsync();
        var traces = AllTraces(win);

        Assert.Equal(2, traces.Length);
        Assert.Contains(traces, t => t.Derived == DerivedParameters.SourceStabilityCircle);
        Assert.Contains(traces, t => t.Derived == DerivedParameters.MaxGain);
        Assert.All(traces, t => Assert.True(HasGeometry(t), $"{t.Derived} drew nothing on open"));
    }

    /// <summary>The report itself: re-run the analysis, and both traces are gone from the plots.</summary>
    [Fact]
    public async Task RerunningTheAnalysis_KeepsBothDerivedTraces()
    {
        var win = await OpenDisplayAsync();
        Assert.Equal(2, AllTraces(win).Length);

        // The run overwrites its own .npy in place, then the workspace refreshes every open display.
        // This mirrors WorkspaceViewModel.RefreshOpenDataDisplaysAsync STEP FOR STEP, including the
        // re-selection at the end — a trace row rebuilds its signal list on
        // SelectedDataSourceChanged as well as on LibraryChanged, and only the full sequence
        // exercises both.
        DataSetExporter.Export(GroupedRun(scale: 1.1), Path.Combine(_results, NpyName), ExportFormat.Npy);
        await RefreshOpenDisplayAsync(win, Path.Combine(_results, NpyName));

        var traces = AllTraces(win);
        Assert.Equal(2, traces.Length);
        Assert.Contains(traces, t => t.Derived == DerivedParameters.SourceStabilityCircle);
        Assert.Contains(traces, t => t.Derived == DerivedParameters.MaxGain);
        Assert.All(traces, t => Assert.True(HasGeometry(t), $"{t.Derived} drew nothing after the re-run"));
    }
}
