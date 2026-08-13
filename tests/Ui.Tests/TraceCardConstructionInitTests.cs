// ================================================================
//  TraceCardConstructionInitTests.cs  —  brief-dd-network-params-and-stability.md §5
//
//  AvailablePorts (and the rest of the network-metric card state) was populated only by
//  RefreshNetworkMetricCard, reached only through RefreshDescription <- RefreshDescription is
//  called by PlotInspectorViewModel after trace paths are rebuilt, never by the
//  TraceRowViewModel constructor itself (which calls RebuildSignals() only). So a freshly
//  constructed card — every plot-type change and every RebuildTraces() — rendered the In/Out
//  row with two EMPTY combos until the user re-picked the signal on the live VM, which is what
//  ran OnSelectedSignalChanged -> RefreshDescription and filled them in.
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TraceCardConstructionInitTests
{
    private static SNP FourPortSnp()
    {
        var m = new NumFlat.Mat<System.Numerics.Complex>(4, 4);
        for (int i = 0; i < 4; i++) m[i, i] = new Complex(0.1, 0);
        return new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    [Fact]
    public void FreshCard_OnADerivedFourPortTrace_HasAvailablePortsPopulated_WithNoPriorSelection()
    {
        var snp = FourPortSnp();
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            Derived = DerivedParameters.MuPrime,
            InputPort = 1,
            OutputPort = 2,
        };

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: null);

        // Construct the row directly — no OnSelectedSignalChanged, no prior RefreshDescription call.
        var row = new TraceRowViewModel(trace, inspector);

        Assert.Equal(4, row.AvailablePorts.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, row.AvailablePorts);
        Assert.True(row.ShowPortSelectors);
    }

    [Fact]
    public void FreshCard_OnATwoPortDerivedTrace_HidesPortSelectors_WithNoPriorSelection()
    {
        var m = new NumFlat.Mat<System.Numerics.Complex>(2, 2);
        var snp = new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            Derived = DerivedParameters.Mu,
        };

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: null);
        var row = new TraceRowViewModel(trace, inspector);

        Assert.Equal(2, row.AvailablePorts.Count);
        Assert.False(row.ShowPortSelectors);   // hidden at exactly 2 ports
    }
}
