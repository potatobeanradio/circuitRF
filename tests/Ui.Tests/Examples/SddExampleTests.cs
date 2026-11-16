// The "Symbolically Defined Device (SDD)" example: every bench passes `check` and is still the netlist it was drawn from
// (SddExampleAuthoring), and the bias-loop bench does what its README says. The five loadpulls (about a second each in a
// Release build) were run when the README was written; their numbers are its table.

using CircuitRF.Cli;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class SddExampleTests
{
    public static TheoryData<string> Benches()
    {
        var d = new TheoryData<string>();
        foreach (var b in SddExampleAuthoring.Benches) d.Add(b.Cell);
        return d;
    }

    private static string Schematic(string cell) => Path.Combine(SddExampleAuthoring.Workspace, cell, "schematic", cell + ".csch");

    [Theory]
    [MemberData(nameof(Benches))]
    public void EachBench_PassesCheck_AndIsItsNetlist(string cell)
    {
        string sch = Schematic(cell);
        Assert.Equal(0, CliEntry.Run(["check", sch]));
        var (_, tb) = CircuitSource.FromSchematic(sch);
        var (_, want) = new CnlReader().Read(SddExampleAuthoring.Benches.Single(b => b.Cell == cell).Netlist, cell, SddExampleAuthoring.Workspace);
        Assert.Equal(want.Analyses.Select(a => (a.Name, a.GetType())), tb.Analyses.Select(a => (a.Name, a.GetType())));
        Assert.Equal(want.Measurements.Select(m => m.Name), tb.Measurements.Select(m => m.Name));
        Assert.Equal(want.Instances.Select(i => i.InstanceName).Order(), tb.Instances.Select(i => i.InstanceName).Order());
    }

    /// <summary>The README's table: open loop the quiescent current follows a ±0.3 V threshold shift by tens of percent; closed,
    /// the SDD reading the supply's current holds it within 3 % of its 61.4 mA target.</summary>
    [Fact]
    public void TheBiasLoop_HoldsTheQuiescentCurrent()
    {
        var (lib, tb) = CircuitSource.FromSchematic(Schematic("6 Bias Loop (control current)"));
        var sweep = tb.Analyses.OfType<CircuitRF.Core.Design.ParametricSweepAnalysis>().Single(a => a.Name == "LOOP");
        var ds = CircuitRF.Engine.ParametricSweepEngine.Run(sweep, lib, tb, new CircuitRF.Engine.AnalysisSettings(), SddExampleAuthoring.Workspace);
        var meas = CircuitRF.Design.Circuit.HbCircuitRun.Measure(tb, new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb), "DC1", ds, null, out _)!;
        double[] idq = meas["Idq_mA"].RealValues;   // [Kloop 0, 200] × [dVt −0.3, 0, +0.3]
        Assert.InRange(idq[0], 85, 95);
        Assert.InRange(idq[2], 38, 44);
        foreach (double i in idq[3..]) Assert.InRange(i, 61.4 * 0.97, 61.4 * 1.03);
    }
}
