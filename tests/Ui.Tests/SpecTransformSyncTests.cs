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

// Diagnoses Bug 2: editing the spec text + committing must update the transform combo.
public sealed class SpecTransformSyncTests
{
    [Fact]
    public async Task CommitSpec_UpdatesTransformCombo()
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9, 3e9 }, "Hz");
        var nodeAxis = new Axis("node", new[] { 0.0, 1.0 });
        var data = new Complex[]
        {
            new(1, 0), new(2, 0),
            new(0.5, 0.5), new(1, 1),
            new(0.1, -0.1), new(0.9, 0.9),
        };
        var ds = new DataSet();
        ds.Add("V", new DataCube(new[] { freqAxis, nodeAxis }, data));

        string path = Path.Combine(Path.GetTempPath(), $"crf_spectfm_{System.Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        try
        {
            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                SourcePath = path,
                CubeName   = "V",
                Slice      = new[]
                {
                    new AxisSlice("freq", AxisRole.KeepAsX,   0),
                    new AxisSlice("node", AxisRole.PinToIndex, 0),
                },
                Transform  = CubeTransform.None,
            };
            trace.Expression = trace.BuildPickerExpression();   // "V[:, 0]"

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var insp = new PlotInspectorViewModel(plot, () => { }, library: lib);
            insp.RebuildAndNotify();
            var row = insp.Traces.First();

            // Edit the spec to a dB20 transform and commit.
            row.CommitSpec("dB20(V[:, 0])");

            Assert.Equal(CubeTransform.dB20, trace.Transform);
            Assert.NotNull(row.SelectedTransformItem);
            Assert.Equal(CubeTransform.dB20, row.SelectedTransformItem!.Transform);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // A transformed bare MEASUREMENT ("mag(IMD2)") must also sync the combo — the function-call form is
    // the one BuildPickerExpression emits, and it has no brackets.
    [Fact]
    public async Task CommitSpec_TransformedMeasurement_UpdatesTransformCombo()
    {
        var ds = new DataSet();
        ds.AddToGroup("measurements", "IMD2",
            new DataCube(new[] { new Axis("Pin", new[] { 0.0, 5.0 }) }, new double[] { -20, -18 }));

        string path = Path.Combine(Path.GetTempPath(), $"crf_meastfm_{System.Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        try
        {
            var trace = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                SourcePath = path,
                CubeName   = "IMD2",
                Slice      = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) },
                Transform  = CubeTransform.None,
            };
            trace.Expression = trace.BuildPickerExpression();   // "IMD2"

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var insp = new PlotInspectorViewModel(plot, () => { }, library: lib);
            insp.RebuildAndNotify();
            var row = insp.Traces.First();

            row.CommitSpec("mag(IMD2)");

            Assert.Equal("IMD2", trace.CubeName);                 // CubeName preserved (round-trips)
            Assert.Equal(CubeTransform.Mag, trace.Transform);
            Assert.NotNull(row.SelectedTransformItem);
            Assert.Equal(CubeTransform.Mag, row.SelectedTransformItem!.Transform);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
