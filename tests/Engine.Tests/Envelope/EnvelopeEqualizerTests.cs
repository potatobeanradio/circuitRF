using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Envelope;

/// <summary>
/// Brief PCH-4's envelope gate: static multipath built from today's blocks (a 2-way combiner as the splitter, a Delay, an
/// Atten, a 2-way combiner) with the genie equalizer. A second path inside the cyclic prefix is a fixed response per
/// subcarrier, which the genie takes out whole: EVM is the no-channel EVM to 1e-3 %, the run's own floor (with one gain the
/// no-channel run reads 8e-4 %, the envelope's playback of the record; the brief's 1e-6 % is below what the run resolves).
/// Beyond the prefix each symbol reaches into its neighbour, and the EVM says so. The Delay block is stepped as its delay
/// (EnvelopeLineDelay), not fitted.
/// </summary>
public sealed class EnvelopeEqualizerTests(ITestOutputHelper output)
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

    // Unshaped: the spectral shaping filters the record after the cyclic prefix is inserted, so a prefix is then not quite a
    // copy of its symbol's end, and an echo reads what the source did not play.
    private const string Source = "MOD_Source:S1 s 0 Fc=1e9 Pavg=-30 Z=50 Ohm Record=0.5e-3 Shaping=off Carrier[1]=\"LTE;BW=5MHz;Mod=64QAM;Off=0;P=0;Seed=3\"";
    private const string Analysis = "analysis ENV1 type=env Accuracy=draft MaxHarm=1 Segments=1 Warmup=20e-6 Stop=0.5e-3 Equalizer=genie";

    private static string Direct() => $"""
        {Source}
        IProbe:Iout s o
        R:RL o 0 R=50 Ohm
        {Analysis}
        """;

    /// <summary>The source split two ways, one path delayed by <paramref name="delay"/> and 6 dB down, recombined.</summary>
    private static string TwoPath(string delay) => $"""
        {Source}
        Combiner:SPL s 0 a 0 b 0 N=2 Z0=50 Ohm
        Delay:DL b 0 c 0 T={delay}
        Atten:AT c 0 d 0 Loss=6 dB
        Combiner:CMB e 0 a 0 d 0 N=2 Z0=50 Ohm
        IProbe:Iout e o
        R:RL o 0 R=50 Ohm
        {Analysis}
        """;

    [Fact]
    public void Gate2_StaticMultipath_InsideThePrefixIsEqualizedAway_BeyondItIsNot()
    {
        double clean = Run(Direct())["EVM_pct"].RealValues[0];
        var inside = Run(TwoPath("1 us"));                      // LTE's normal cyclic prefix is 4.7 us
        var beyond = Run(TwoPath("12 us"));
        double unequalized = Run(TwoPath("1 us").Replace("Equalizer=genie", "Equalizer=none"))["EVM_pct"].RealValues[0];
        double evmIn = inside["EVM_pct"].RealValues[0], evmOut = beyond["EVM_pct"].RealValues[0];
        output.WriteLine($"no channel {clean:F9} %, second path inside the prefix {evmIn:F9} %, beyond it {evmOut:F4} %; " +
                         $"BER beyond {beyond["BER"].RealValues[0]:G3}, CFO {inside["CFO_Hz"].RealValues[0]:G3} Hz");
        // the no-channel run reads 8e-4 % itself (the envelope's playback of the record): the channel is taken out to that
        // floor, from the 48 % one gain reads through it
        Assert.True(unequalized > 10, $"one gain through the channel {unequalized} %");
        Assert.True(Math.Abs(evmIn - clean) < 1e-3, $"{evmIn} % against {clean} %");
        Assert.True(evmOut > 10 * clean + 1, $"beyond the prefix {evmOut} %");
        Assert.Equal(1, inside["__Equalizer"].RealValues[0]);
        Assert.Equal(0, inside["BitErrors"].RealValues[0]);
        Assert.True(Math.Abs(inside["CFO_Hz"].RealValues[0]) < 1e-3);
    }

    /// <summary>The four keys survive the netlist writer, and a line without them writes none (so it reads as it always
    /// did).</summary>
    [Fact]
    public void TheKeys_RoundTrip_AndTheirDefaultsWriteNothing()
    {
        var (_, tb) = new CnlReader().Read(TwoPath("1 us").Replace("Equalizer=genie", "Equalizer=pilot Pilots=12,2 Taps=7 Training=100"));
        string written = CnlWriter.Write(tb);
        var env = new CnlReader().Read(written).Item2.Analyses.OfType<EnvelopeAnalysis>().Single();
        Assert.Equal(("pilot", "12,2", "7", "100"), (env.EqualizerExpr, env.PilotsExpr, env.TapsExpr, env.TrainingExpr));
        var (_, plain) = new CnlReader().Read(Direct().Replace(" Equalizer=genie", ""));
        Assert.DoesNotContain("Equalizer", CnlWriter.Write(plain));
    }
}
