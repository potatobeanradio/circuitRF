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

/// <summary>
/// A typed expression trace is no single item of the card's group and quantity pickers, so they show nothing selected and
/// say "Expression" — never the first item (S(1,1)) as if the trace were it. A picked quantity still shows itself, and a
/// commit that turns a picked trace into an expression clears them.
/// </summary>
public sealed class ExpressionTracePickerTests
{
    private static async Task<(PlotInspectorViewModel Insp, Trace Trace, string Path)> Open(string? expression)
    {
        var freq = new Axis("freq", [1e9, 2e9, 3e9], "Hz");
        var ds = new DataSet();
        ds.Add("V", new DataCube([freq], new Complex[] { new(1, 0), new(0.5, 0.5), new(0.1, -0.1) }));
        ds.Add("W", new DataCube([freq], new Complex[] { new(2, 0), new(0.4, 0.1), new(0.2, -0.3) }));
        string path = Path.Combine(Path.GetTempPath(), $"crf_exprpick_{System.Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);

        var trace = new Trace(new SNP([1e9], 2), MatrixType.S, 0, 0, DependentVarFormat.Db) { SourcePath = path };
        if (expression is null)
        {
            trace.CubeName = "W";
            trace.Slice = [new AxisSlice("freq", AxisRole.KeepAsX, 0)];
            trace.Expression = trace.BuildPickerExpression();
        }
        else trace.Expression = expression;
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var insp = new PlotInspectorViewModel(plot, () => { }, library: lib);
        insp.RebuildAndNotify();
        return (insp, trace, path);
    }

    [Fact]
    public async Task ATypedExpression_LeavesThePickersEmpty_SayingExpression()
    {
        var (insp, trace, path) = await Open("V[:] - W[:]");
        try
        {
            var row = insp.Traces.Single();
            Assert.True(row.IsTypedExpression);
            Assert.Null(row.SelectedGroup);
            Assert.Null(row.SelectedSignal);
            Assert.Equal("Expression", row.GroupPlaceholder);
            Assert.Null(row.PickerPlaceholder);
            Assert.Equal("V[:] - W[:]", trace.Expression);   // nothing re-aimed it
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task APickedQuantity_StillShowsItself_UntilAnExpressionIsCommitted()
    {
        var (insp, trace, path) = await Open(null);
        try
        {
            var row = insp.Traces.Single();
            Assert.False(row.IsTypedExpression);
            Assert.Equal("W", row.SelectedSignal!.Label);

            row.CommitSpec("V[:] - W[:]");
            Assert.True(row.IsTypedExpression);
            Assert.Null(row.SelectedGroup);
            Assert.Null(row.SelectedSignal);
            Assert.Equal("V[:] - W[:]", trace.Expression);
        }
        finally { File.Delete(path); }
    }
}
