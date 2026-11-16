using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Workspace;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.PhasedArray;

/// <summary>
/// brief-paa-10 — the shipped <i>Phased Array</i> example, and the two defects drawing it found: a schematic's <c>Channel=</c> did not
/// carry the channel cell into the extracted netlist, and <c>--set</c> dropped the variable's unit.
/// </summary>
public class PhasedArrayExampleTests
{
    private static string Bench(string cell)
    {
        string root = ExampleWorkspaces.ResolveRoot() ?? throw new InvalidOperationException("no examples/ tree");
        return Path.Combine(root, "Phased Array", cell, "schematic", cell + ".csch");
    }

    /// <summary>The drawn Transmit bench names its channel cell only in a parameter; extraction brings the cell along, and the
    /// bench simulates on the fast path with its measures.</summary>
    [Fact]
    public void TheTransmitBench_CarriesItsChannelCell_AndSimulates()
    {
        var circuit = PreparedCircuit.FromSchematic(Bench("1 Transmit"));
        Assert.Null(circuit.ReadError);
        var channel = circuit.Lib!.Find("TxChannel");
        Assert.NotNull(channel);
        Assert.Equal(["in", "out"], channel!.Ports);

        var result = CircuitEvaluation.Execute(CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest()));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Notes, n => n.StartsWith("Fast path:", StringComparison.Ordinal));
        var measures = result.GroupedResults!.CubesIn(DataSet.MeasurementsGroup);
        Assert.InRange(measures["SLL_dB"].RealValues[0], -26, -23);
    }

    /// <summary>The PA loadpull the brute-force displays draw their contours from reaches 3 dB compression at every load of its grid. Its
    /// tuners' nets are interface nodes the drain's current probe joins to the device, so Z_NN is singular at every harmonic: once the
    /// DC extraction held both nodes with ideal sources (a loop, every load refused), and inverting it left a zero row at three loads.</summary>
    [Fact]
    public void ThePaLoadpull_ReachesCompressionAtEveryLoad()
    {
        var circuit = PreparedCircuit.FromSchematic(Bench("4 PA Loadpull"));
        Assert.Null(circuit.ReadError);
        var result = CircuitEvaluation.Execute(CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest()));
        Assert.Empty(result.Errors);
        var stop = result.GroupedResults!["LP1.StopCode"].RealValues;
        Assert.Equal(48, stop.Length);
        Assert.All(stop, code => Assert.Equal(1.0, code));   // 1: compression
    }

    /// <summary><c>--set theta0=30</c> on <c>theta0 = 0 deg</c> is 30 degrees, as a tuning move is; a value with its own unit keeps it.</summary>
    [Fact]
    public void ASet_KeepsTheVariablesUnit_UnlessItStatesItsOwn()
    {
        var tb = new TestBench("t");
        tb.GlobalVariables.Add(new Variable("theta0", "0", "deg"));
        HbCircuitRun.ApplySet(tb, "theta0", "30");
        var v = Assert.Single(tb.GlobalVariables);
        Assert.Equal(("30", "deg"), (v.Expression, v.Unit));

        HbCircuitRun.ApplySet(tb, "theta0", "0.5 rad");
        v = Assert.Single(tb.GlobalVariables);
        Assert.Equal(("0.5", "rad"), (v.Expression, v.Unit));
    }
}
