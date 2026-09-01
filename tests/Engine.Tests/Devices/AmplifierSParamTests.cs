using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The ideal amplifier end to end in an S-parameter run (brief-sys-5): a one-block netlist
/// terminated in ideal ports returns EXACTLY the four S entries its parameters state.
///
/// <para>The gain is not asserted against <c>10^(Gain/20)</c> alone — the brief's own
/// <b>voltage-gain algebra is done here</b>, from the dB on the netlist line through
/// <c>G = 2·√(10^(Gain/10)·Zout/Zin)</c> and back out as <c>S21 = (G/2)·√(Zin/Zout)</c>, so what is
/// gated is the whole derivation and not one exponent. Nothing is read out of the model.</para>
/// </summary>
public class AmplifierSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz = 1e9)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [freqHz]);
        var c  = ds["S"];
        int n  = c.Axes[1].Length;
        var s  = new Complex[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            s[i, j] = (Complex)c[0, i, j];
        return s;
    }

    private static string Cnl(double gainDb, double zIn, double zOut,
                              double ip3 = 200, double rlIn = 200, double rlOut = 200,
                              double s12 = 200, string ip3Ref = "Output") => $@"
Port:P1  a 0  Num=1  Z={N(zIn)} Ohm
Port:P2  b 0  Num=2  Z={N(zOut)} Ohm
Amp:A1   a 0 b 0  Gain={N(gainDb)} IP3={N(ip3)} IP3Ref={ip3Ref} Zin={N(zIn)} Zout={N(zOut)} RLin={N(rlIn)} RLout={N(rlOut)} S12={N(s12)}
";

    private static void Near(Complex expected, Complex actual, double tol)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    // ── The gain, through the brief's own voltage-gain algebra ────────────────

    public static TheoryData<double, double, double> GainCases()
    {
        var d = new TheoryData<double, double, double>();
        foreach (double gain in new[] { 6.0, 20.0, 40.0 })
        foreach (var (zi, zo) in new[] { (50.0, 50.0), (75.0, 50.0), (25.0, 200.0) })
            d.Add(gain, zi, zo);
        return d;
    }

    [Theory]
    [MemberData(nameof(GainCases))]
    public void S21IsTheGainThatWasTyped_ByWayOfTheVoltageGainTheModelDerives(
        double gainDb, double zIn, double zOut)
    {
        var s = SAt(Cnl(gainDb, zIn, zOut));

        // brief-sys-5's derivation, done here. With both ports matched, an input of peak A puts
        // G·A/2 across a matched load, so Gp = (G·A/2)²/(2·Zout) ÷ A²/(2·Zin) = G²·Zin/(4·Zout).
        double g = 2.0 * Math.Sqrt(Math.Pow(10.0, gainDb / 10.0) * zOut / zIn);
        double expectedS21 = (g / 2.0) * Math.Sqrt(zIn / zOut);

        output.WriteLine($"Gain {gainDb} dB, Zin {zIn}, Zout {zOut}: G = {g:G12} V/V "
                       + $"→ S21 {expectedS21:G12}, measured {s[1, 0]}");

        Near(new Complex(expectedS21, 0), s[1, 0], 1e-9);
        Assert.Equal(gainDb, 20.0 * Math.Log10(s[1, 0].Magnitude), 9);
    }

    // ── Unilateral, and matched ───────────────────────────────────────────────

    [Fact]
    public void FreshlyPlaced_ItIsUnilateralAndMatched_AndTheEntriesAreABSENT()
    {
        var s = SAt(Cnl(20.0, 50.0, 50.0));

        // An "off" suppression stamps NO ENTRY at all — that is asserted exactly, on the matrix
        // itself, in AmplifierModelTests. What survives a solve is the solve's own roundoff
        // (~1e−17 here), which is the honest floor: the wave extraction divides solved node
        // voltages, and no arithmetic on them is exact. 1e−15 is three orders below anything a
        // stamped 200 dB entry would produce (1e−10) and two above the floor.
        foreach (var (name, entry) in new[] { ("S12", s[0, 1]), ("S11", s[0, 0]), ("S22", s[1, 1]) })
        {
            output.WriteLine($"{name} = {entry}");
            Assert.True(entry.Magnitude < 1e-15, $"{name} holds {entry} — nothing should be stamped there");
        }
    }

    [Theory]
    [InlineData(20.0, 20.0, 200.0)]
    [InlineData(10.0, 25.0,  30.0)]
    [InlineData(6.0,   6.0,  15.0)]
    public void AStatedReturnLossAndIsolationComeBackAsThemselves(
        double rlIn, double rlOut, double s12Db)
    {
        var s = SAt(Cnl(20.0, 50.0, 50.0, rlIn: rlIn, rlOut: rlOut, s12: s12Db));

        double rho12 = s12Db >= 150 ? 0.0 : Math.Pow(10.0, -s12Db / 20.0);
        Near(new Complex(Math.Pow(10.0, -rlIn  / 20.0), 0), s[0, 0], 1e-9);
        Near(new Complex(Math.Pow(10.0, -rlOut / 20.0), 0), s[1, 1], 1e-9);
        Near(new Complex(rho12, 0),                         s[0, 1], 1e-9);

        // And the gain is STILL the gain that was typed. A Thevenin source behind a mismatched
        // resistance would read (1+S11)(1−S22) times it — up to 2.4 dB out on these rows.
        Assert.Equal(20.0, 20.0 * Math.Log10(s[1, 0].Magnitude), 9);
    }

    // ── The two halves of the model must report the same small signal ─────────

    /// <summary>
    /// With <c>IP3</c> at its default the amplifier is LINEAR and takes the family's wave-constraint
    /// stamp; with an intercept it is NONLINEAR and the linear engine reaches it through
    /// <c>StampLinearized</c> instead — an entirely different code path, through a nonlinear DC solve
    /// and a Jacobian. Both must report the same S, or the amplifier's gain would depend on whether
    /// its intercept had been filled in.
    /// </summary>
    [Theory]
    [InlineData(200.0, 200.0, 200.0)]
    [InlineData( 20.0,  15.0,  25.0)]
    public void TheLinearAndTheLinearizedStampsAgreeExactly(double rlIn, double rlOut, double s12Db)
    {
        var linear    = SAt(Cnl(20.0, 50.0, 50.0, ip3: 200, rlIn: rlIn, rlOut: rlOut, s12: s12Db));
        var nonlinear = SAt(Cnl(20.0, 50.0, 50.0, ip3: 40,  rlIn: rlIn, rlOut: rlOut, s12: s12Db));

        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
        {
            output.WriteLine($"S{p + 1}{q + 1}: linear {linear[p, q]}  linearized {nonlinear[p, q]}");
            Near(linear[p, q], nonlinear[p, q], 1e-12);
        }
    }

    // ── Memoryless means flat, and flat includes the bottom of the band ──────

    [Fact]
    public void ItIsFlatAcrossEveryDecadeItIsAskedAbout()
    {
        // A memoryless block with a flat gain has it at every frequency. The 1 Hz row is the one
        // that matters: it is the neighbourhood of the DC harmonic HB has to solve at, and a stamp
        // that had acquired an ω anywhere would show it here first.
        foreach (double f in new[] { 1.0, 1e3, 1e6, 1e9, 1e11 })
        {
            var s = SAt(Cnl(20.0, 50.0, 50.0), f);
            output.WriteLine($"{f:G3} Hz: S21 = {s[1, 0]}");
            Near(new Complex(10.0, 0), s[1, 0], 1e-9);
            Assert.True(s[0, 1].Magnitude < 1e-15);
        }
    }
}
