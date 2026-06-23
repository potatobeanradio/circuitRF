// ================================================================
//  PickerExcludesToneFreqsTests.cs
//  Gate test for brief-hb-spectrum-1-tone-metadata — Part C
//
//  5. Picker_ExcludesToneFreqs
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PickerExcludesToneFreqsTests
{
    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_tone_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static TraceRowViewModel BuildInspector(
        DataSourceLibraryViewModel lib, string sourcePath, string cubeName)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = null;
        trace.Transform  = CubeTransform.None;

        var plot      = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    // ── 5. Picker_ExcludesToneFreqs ───────────────────────────────────────────
    // A HB DataSet that includes ToneFreqs and MetaMixOrder must not surface
    // either in the signal picker — they are run-metadata, not plottable traces.

    [Fact]
    public async Task Picker_ExcludesToneFreqs()
    {
        var ds = new DataSet();

        // A real plottable cube (node × harmonic — as emitted by single-tone HB after stage 2).
        var nodeVals = new double[] { 0, 1 };
        var harmVals = new double[] { 0.0, 1.0, 2.0 };
        var nodeAxis = new Axis("node",     nodeVals, "", ["out", "in"]);
        var harmAxis = new Axis("harmonic", harmVals, "");
        ds.AddToGroup("HB1", "V",
            new DataCube([nodeAxis, harmAxis], new Complex[nodeVals.Length * harmVals.Length]));

        // ToneFreqs as emitted by single-tone HB: rank-1, axis "tone".
        var toneAxis = new Axis("tone", [1.0], "");
        ds.AddToGroup("HB1", "ToneFreqs",
            new DataCube([toneAxis], new double[] { 2e9 }));

        // MetaMixOrder as emitted by two-tone HB: rank-1, axis "order".
        var orderAxis = new Axis("order", [1.0], "");
        ds.AddToGroup("HB1", "MetaMixOrder",
            new DataCube([orderAxis], new double[] { 3.0 }));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            var trvm = BuildInspector(lib, path, "HB1.V");
            trvm.SelectedGroup = "HB1";

            var labels = trvm.AvailableSignals.Select(s => s.Label).ToList();

            // The real signal must be present.
            Assert.Contains("V", labels);

            // Metadata cubes must be hidden from the picker.
            Assert.DoesNotContain("ToneFreqs",    labels);
            Assert.DoesNotContain("MetaMixOrder", labels);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
