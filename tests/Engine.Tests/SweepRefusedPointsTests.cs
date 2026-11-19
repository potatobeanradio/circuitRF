using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests;

/// <summary>
/// A sweep runs the valid portion of its range. Sweeping a P.1238 office channel's distance 1 to 40 m crosses the model's 4–30 m:
/// each refused point is NaN in every cube, the others are solved, and a nested drop sweep inside takes a whole refused distance as
/// NaN. The swept VAR's own value (1 m) is never a point the run reports, so it no longer refuses the run before it starts.
/// </summary>
public sealed class SweepRefusedPointsTests
{
    private const string Bench = """
        dist = 1
        drop = 1
        Port:P1 tx 0 Num=1 Z=50 Ohm
        Port:P2 rx 0 Num=2 Z=50 Ohm
        PropagationChannel:CH1 tx rx Distance=dist TxHeight=1 RxHeight=2.5 PathLoss=P1238 Environment=office Los=nlos Drop=drop f0=868.95e6
        analysis SP1 type=sparam start=868.95 stop=868.95 npts=1 Unit=MHz
        analysis DROPS type=parametric_sweep Var=drop Start=1 Stop=3 Step=1 Inner=SP1
        analysis DISTS type=parametric_sweep Var=dist Start=1 Stop=40 Step=3 Inner=DROPS
        """;

    /// <param name="atElaboration">With f0 stated the model is evaluated, and refuses, when the netlist is elaborated; without
    /// it, at the run's frequency when the point is solved. Both are the sweep's invalid portion.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheNominalIsNotRun_AndEachRefusedDistanceIsNaN(bool atElaboration)
    {
        var (lib, tb) = new CnlReader().Read(atElaboration ? Bench : Bench.Replace(" f0=868.95e6", ""));
        var dists = tb.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.Name == "DISTS");
        using var nl = ParametricSweepEngine.ElaborateForRun(lib, tb, null, () => dists);
        if (atElaboration)
        {
            Assert.Throws<ParameterRefusalException>(() => new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb));
            Assert.Contains(nl.Warnings, w => w.Contains("set up at dist = 4", StringComparison.Ordinal));
        }

        var s = ParametricSweepEngine.Run(dists, lib, tb, diagnosticsInto: nl).Cubes["S"];
        int perDist = s.ComplexValues.Length / 14;
        for (int d = 0; d < 14; d++)
        {
            double meters = 1 + 3 * d;
            bool valid = meters is > 3 and < 30;   // a 3-D distance of 4–30 m at heights 1 and 2.5
            var slice = s.ComplexValues.Skip(d * perDist).Take(perDist);
            Assert.True(valid ? slice.All(c => double.IsFinite(c.Real)) : slice.All(c => double.IsNaN(c.Real)), $"dist = {meters}");
        }
    }

    [Fact]
    public void ARunWhoseOwnAnalysisIsNotASweep_IsStillRefused()
    {
        var (lib, tb) = new CnlReader().Read(Bench);
        var sp = tb.Analyses.Single(a => a.Name == "SP1");
        Assert.Throws<ParameterRefusalException>(() => ParametricSweepEngine.ElaborateForRun(lib, tb, null, () => sp));
    }
}
