using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Every System block but the amplifier DECLARES itself passive. These gate the check that the
/// declaration is true of the numbers a user actually typed.
///
/// <para><b>The failure this exists for.</b> The family writes its S-matrix entries as real,
/// in-phase amplitudes converted straight from dB, so an insertion loss, a return loss and an
/// isolation that are each individually plausible add coherently. A 0.5 dB switch with 18 dB of
/// return loss has <c>|S11 + S21| = 1.07</c> — it passes every per-port power check and is not
/// passive as a matrix. Nothing refused it and nothing reported it; it surfaced two components away
/// as an antenna-port return loss that read +1.2 dB, which no passive network can do.</para>
///
/// <para><b>Why the assertions are on the INSTANCE NAME and not on a number.</b> The measurement is
/// <c>RFNetwork.Passivity</c>, which this repository already trusts for a Touchstone file and for a
/// <c>Chain</c>'s ABCD; re-deriving σ_max here would be a second implementation to keep in step.
/// What is worth gating is that the right instance is named, that a passive block is silent, and
/// that the message survives the two routings that lose it.</para>
/// </summary>
public class SystemBlockPassivityTests(ITestOutputHelper output)
{
    private const string Key = "system.block-not-passive";

    /// <summary>Elaborates a one-line netlist and returns the warnings it raised.</summary>
    private IReadOnlyList<string> WarningsOf(string body)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "crf-passivity-" + Guid.NewGuid().ToString("N")[..12] + ".cnl");
        File.WriteAllText(path, body);
        try
        {
            var (lib, tb) = CnlReader.ReadFile(path);
            using var nl  = new Elaborator(lib).Elaborate(tb);
            foreach (string w in nl.Warnings) output.WriteLine(w);
            return [.. nl.Warnings];
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    private static bool Reports(IReadOnlyList<string> warnings, string instance)
        => warnings.Any(w => w.Contains(Key, StringComparison.Ordinal)
                          && w.Contains($"'{instance}'", StringComparison.Ordinal));

    /// <summary>
    /// The combination that started this: a switch whose through path and whose own reflection sum
    /// above one. Reported, by the name the user gave the instance.
    /// </summary>
    [Fact]
    public void ASwitchWhoseThroughAndReflectionSumAboveOne_IsReportedByInstanceName()
    {
        var w = WarningsOf("""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Switch:TR_SW a 0 b 0 c 0 State=1 Throws=2 IL=0.5 dB Isolation=28 dB OffState=Absorptive Z0=50 Ohm RL=18 dB
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:Rc c 0 R=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);

        Assert.True(Reports(w, "TR_SW"), "the switch was not reported: " + string.Join(" | ", w));
    }

    /// <summary>
    /// The same part with a return loss its insertion loss can afford is silent. This is the half
    /// that keeps the check usable: an ideal block sits at σ_max = 1 exactly, and a check that
    /// reported the boundary would fire on every freshly placed tile.
    /// </summary>
    [Theory]
    [InlineData("IL=0.5 dB Isolation=28 dB OffState=Absorptive Z0=50 Ohm RL=26 dB", "a realizable pad")]
    [InlineData("IL=0 dB Isolation=200 dB OffState=Reflective Z0=50 Ohm RL=200 dB", "the tile's own defaults")]
    public void APassiveSwitchIsSilent(string parameters, string what)
    {
        var w = WarningsOf($"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Switch:TR_SW a 0 b 0 c 0 State=1 Throws=2 {parameters}
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:Rc c 0 R=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);

        Assert.False(Reports(w, "TR_SW"), $"{what} was reported as not passive: " + string.Join(" | ", w));
    }

    /// <summary>
    /// <b>A block carrying a passive-intermod level is still checked.</b>
    ///
    /// <para>The routing hole this pins, which a stamp-time check alone does not close: <c>PIM</c>
    /// makes the block <see cref="CircuitRF.Core.ModelKind.Nonlinear"/>, and the engines then route
    /// it away from the linear stamp — the DC engine skips it outright because it declares no branch
    /// equations of its own, and the harmonic-balance linear extractor takes only the linear
    /// partition. The circulator in every transmitter of the System Design example is exactly this
    /// block, and in an HB run it was stamped by nobody and reported by nobody.</para>
    /// </summary>
    [Fact]
    public void ABlockWithPassiveIntermodOnIt_IsStillReported()
    {
        const string circulator =
            "Circulator:CR1 a 0 b 0 c 0 Direction=CW IL=0.4 dB Isolation=22 dB RL=20 dB";

        var linear = WarningsOf($"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            {circulator}
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:Rc c 0 R=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);
        Assert.True(Reports(linear, "CR1"));

        var nonlinear = WarningsOf($"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            {circulator} PIM=-67 dBm PIMPc=43 dBm
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:Rc c 0 R=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);
        Assert.True(Reports(nonlinear, "CR1"),
            "a circulator is checked until somebody gives it a PIM level, and then it is not: "
          + string.Join(" | ", nonlinear));
    }

    /// <summary>
    /// <b>An in-phase power divider cannot be built out of a four-port coupler</b>, and the check
    /// says so rather than letting it split 3 dB two ways for free.
    ///
    /// <para>This is a theorem rather than a modelling artifact: a matched, lossless, reciprocal
    /// four-port must put 90° (or 180°) between its two outputs. A real in-phase splitter is a
    /// Wilkinson — a THREE-port with a resistor in it — and the only in-phase split this block can
    /// represent is a resistive one, which pays 3.01 dB for the privilege. The quadrature hybrid at
    /// the same coupling is exactly marginal and stays silent.</para>
    /// </summary>
    [Theory]
    [InlineData("Phase=0 deg",  "IL=0.2 dB",    true,  "an in-phase 3 dB split with 0.2 dB of loss")]
    [InlineData("Phase=0 deg",  "IL=3.0103 dB", false, "the resistive in-phase splitter")]
    [InlineData("Phase=90 deg", "IL=0 dB",      false, "the ideal quadrature hybrid")]
    public void AnInPhaseThreeDbCoupler_IsReportedUnlessItPaysForTheSplit(
        string phase, string loss, bool reported, string what)
    {
        var w = WarningsOf($"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Coupler:SPL a 0 b 0 c 0 d 0 Coupling=3.0103 dB {phase} Directivity=200 dB {loss} Z0=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            R:Rc c 0 R=50 Ohm
            R:Rd d 0 R=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);

        Assert.Equal(reported, Reports(w, "SPL"));
        output.WriteLine($"{what}: {(Reports(w, "SPL") ? "reported" : "silent")}");
    }

    /// <summary>The amplifier is not in scope — it declares itself ACTIVE, and reporting a block
    /// with gain for having gain would be the check firing on the one part that is supposed to.</summary>
    [Fact]
    public void AnAmplifierIsNotReported()
    {
        var w = WarningsOf("""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Amp:A1 a 0 b 0 Gain=20 dB IP3=25 dBm IP3Ref=Output
            Port:P2 b 0 Num=2 Z=50 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
            """);

        Assert.False(Reports(w, "A1"), string.Join(" | ", w));
    }
}
