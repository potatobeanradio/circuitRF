using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>Issue 66 §1 and §2: a Stop too short to measure is refused with the shortest one that is not, and a source's
/// carriers on the lattice envelope add their powers into Pin_avg, not their phasors.</summary>
public sealed class EnvelopeShortStopAndInputPowerTests
{
    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var plan = EnvelopeRunner.Resolve(tb.Analyses.OfType<EnvelopeAnalysis>().Single(), nl.ResolvedGlobals);
        bool used = false;
        ElaboratedNetlist Elab() { if (used) return new Elaborator(lib).Elaborate(tb); used = true; return nl; }
        return EnvelopeRunner.Run(plan, Elab);
    }

    [Fact]
    public void AStopShorterThanTheMeasurementsNeed_IsRefused_NamingTheShortestThatIsNot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Run("""
            MOD_Source:S1 in 0 Fc=100 MHz Pavg=30 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;BW=1MHz;Off=0;P=0"
            R:RL in 0 R=50 Ohm
            analysis ENV1 type=env Accuracy=draft MaxHarm=3 Envelope=single Segments=1 Warmup=0 Stop=2e-7
            """));
        Assert.Contains("need at least 15", ex.Message);
        Assert.Contains("1.953 µs", ex.Message);
    }

    [Fact]
    public void TwoCwCarriersOfOneSource_OnTheLattice_ReadTheirTotalPower_AndNoLossThroughAWire()
    {
        var ds = Run("""
            MOD_Source:S1 in 0 Fc=105 MHz Pavg=33.0103 Z=50 Ohm Record=0.5e-3 Carrier[1]="CW;BW=1MHz;Off=-5MHz;P=0" Carrier[2]="CW;BW=1MHz;Off=5MHz;P=0"
            IProbe:Iout in load
            R:RL load 0 R=50 Ohm
            analysis ENV1 type=env Accuracy=draft MaxHarm=3 MaxMixOrder=3 Envelope=lattice Segments=1 Warmup=0 Stop=2e-6
            """);
        Assert.Equal(33.0103, ds["Pin_avg_dBm"].RealValues[0], 3);
        Assert.Equal(0.0, ds["Gt_dB"].RealValues[0], 3);
    }
}
