using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// Gate tests for brief-sweep-axis-marker-units Part A:
/// ParametricSweepEngine tags the swept axis with Units.BaseUnit(origVar.Unit).
/// </summary>
public class ParametricSweepAxisUnitTests(ITestOutputHelper output)
{
    // T1 — GHz variable → axis unit "Hz"
    private const string GhzSweepCnl = @"
RFfreq = 1e9 GHz

Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=50 Ohm

analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
analysis SW1  type=parametric_sweep  Var=RFfreq  Values=1e9,2e9  Inner=SP1
";

    [Fact]
    public void Sweep_GhzVar_AxisUnitIsHz()
    {
        var (lib, tb) = new CnlReader().Read(GhzSweepCnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);
        var sCube = ds["S"];
        var sweepAxis = sCube.Axes[0];

        output.WriteLine($"axis name={sweepAxis.Name}  unit='{sweepAxis.Unit}'");
        Assert.Equal("RFfreq", sweepAxis.Name);
        Assert.Equal("Hz", sweepAxis.Unit);
    }

    // T2 — unit-less variable → axis unit ""
    private const string NoUnitSweepCnl = @"
Rval = 50

Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=Rval Ohm

analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
analysis SW1  type=parametric_sweep  Var=Rval  Values=25,50  Inner=SP1
";

    [Fact]
    public void Sweep_NoUnitVar_AxisUnitIsEmpty()
    {
        var (lib, tb) = new CnlReader().Read(NoUnitSweepCnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);
        var sCube = ds["S"];
        var sweepAxis = sCube.Axes[0];

        output.WriteLine($"axis name={sweepAxis.Name}  unit='{sweepAxis.Unit}'");
        Assert.Equal("Rval", sweepAxis.Name);
        Assert.Equal("", sweepAxis.Unit);
    }

    // T3 — pF variable → axis unit "F" (pF is in Units._scales, so CnlReader stores Unit="pF")
    private const string PfSweepCnl = @"
Cload = 100 pF

Port:P1  n1 0  Num=1  Z=50 Ohm
Port:P2  n2 0  Num=2  Z=50 Ohm
R:Rs     n1 n2  R=50 Ohm

analysis SP1  type=sparam  start=1e9  stop=1e9  npts=1
analysis SW1  type=parametric_sweep  Var=Cload  Values=1e-10,2e-10  Inner=SP1
";

    [Fact]
    public void Sweep_PfVar_AxisUnitIsF()
    {
        var (lib, tb) = new CnlReader().Read(PfSweepCnl);
        var sw1 = tb.Analyses.OfType<ParametricSweepAnalysis>().First(a => a.Name == "SW1");

        var ds = ParametricSweepEngine.Run(sw1, lib, tb);
        var sCube = ds["S"];
        var sweepAxis = sCube.Axes[0];

        output.WriteLine($"axis name={sweepAxis.Name}  unit='{sweepAxis.Unit}'");
        Assert.Equal("Cload", sweepAxis.Name);
        Assert.Equal("F", sweepAxis.Unit);
    }
}
