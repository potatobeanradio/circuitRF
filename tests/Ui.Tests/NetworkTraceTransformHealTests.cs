// ================================================================
//  NetworkTraceTransformHealTests.cs — transform combo must not corrupt
//  a network/SNP trace into a bogus cube expression.
//
//  Bug: changing the transform combo (e.g. Mag → None) on a network trace
//  that had a stale Expression wrote "dB(S(1,1))" into Expression (the
//  network DESCRIPTION), falsely marking it cube-bound → "No cube refs".
//  Fix: only a CubeName trace rebuilds via BuildPickerExpression; a network
//  trace maps to YAxis and self-heals a broken Expression.
// ================================================================

using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class NetworkTraceTransformHealTests
{
    [Fact]
    public void TransformCombo_NetworkTraceWithBrokenExpression_HealsAndSetsYAxis()
    {
        var snp   = new SNP(new double[] { 1e9, 2e9 }, 2);   // a renderable network SNP
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            Transform  = CubeTransform.Mag,
            Expression = "dB(S(1,1))",   // stale network description — not a valid cube expression
        };

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var insp = new PlotInspectorViewModel(plot, () => { }, library: new DataSourceLibraryViewModel());
        insp.RebuildAndNotify();   // evaluating the bad expression sets ExpressionError

        var row = insp.Traces.FirstOrDefault();
        Assert.NotNull(row);

        // In the app, evaluating "dB(S(1,1))" against the trace's dataset fails and sets ExpressionError
        // (the "No cube references found" the user sees). Simulate that evaluated-broken state here.
        trace.ExpressionError = "No cube references found.";
        Assert.True(trace.IsCubeBound);          // falsely cube-bound via the stale Expression

        // Change the transform combo Mag → None — must HEAL, not re-write "dB(S(1,1))".
        var none = row!.TraceTransformItems.Single(t => t.Transform == CubeTransform.None);
        row.SelectedTransformItem = none;

        Assert.Null(trace.Expression);                          // stale expression cleared
        Assert.False(trace.IsCubeBound);                        // renders as a network trace again
        Assert.Equal(DependentVarFormat.Complex, trace.YAxis);  // CubeTransform.None → Complex
    }
}
