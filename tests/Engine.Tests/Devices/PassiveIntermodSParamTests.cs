using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// What turning passive intermod on must NOT change (brief-sys-4's milestone-4 gate), end to end
/// through the real S-parameter engine.
///
/// <para>Turning PIM on moves a block from <c>ModelKind.Linear</c> — the wave-constraint stamp — to
/// <c>ModelKind.Nonlinear</c>, which routes it through <c>StampLinearized</c> and, because
/// <c>SParameterEngine</c> runs a nonlinear DC solve as soon as ANY component is nonlinear, changes
/// what the whole run DOES. It must not change what the run REPORTS, and these are the tests that
/// hold that shut: the same S to machine precision, out of a completely different code path.</para>
///
/// <para>That equivalence is the sharpest check there is on the <c>Y = G·T·(I − S)·G</c> derivation,
/// because it compares an admittance stamp against the wave constraint the same S was written into.
/// A dropped √Z0, a transposed inverse or an (I − S)/(I + S) the wrong way round all survive every
/// amplitude test and die here.</para>
/// </summary>
public class PassiveIntermodSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static (ElaboratedNetlist Netlist, Complex[][,] S) Sweep(string cnl, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, freqs);
        var c  = ds["S"];
        int n  = c.Axes[1].Length;

        var all = new Complex[freqs.Length][,];
        for (int f = 0; f < freqs.Length; f++)
        {
            var s = new Complex[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i, j] = (Complex)c[f, i, j];
            all[f] = s;
        }
        return (nl, all);
    }

    private static string Ports(int n)
        => string.Join("\n", Enumerable.Range(1, n).Select(k => $"Port:P{k}  n{k} 0  Num={k}  Z=50 Ohm"));

    private static string Nets(int n) => string.Join(" ", Enumerable.Range(1, n).Select(k => $"n{k} 0"));

    private static readonly double[] Band =
        [0.5e9, 1e9, 2e9, 5e9, 10e9];

    /// <summary>
    /// Each PIM-capable block, as a netlist line, with a <c>{PIM}</c> placeholder the tests fill in.
    /// The attenuator carries a small loss rather than 0 dB — a matched 0 dB attenuator is an ideal
    /// through, which has no Y at all and is refused; see <c>PassiveIntermodModelTests</c>.
    /// </summary>
    public static TheoryData<string, int, string> Blocks() => new()
    {
        { "Atten:A1       {NETS}  Loss=0.01 Z0=50 RL=200 {PIM}",                          2, "attenuator, 0.01 dB" },
        { "Atten:A1       {NETS}  Loss=6    Z0=50 RL=18  {PIM}",                          2, "attenuator, 6 dB, mismatched" },
        { "Circulator:C1  {NETS}  Direction=CW IL=0 Isolation=200 RL=200 {PIM}",          3, "ideal circulator" },
        { "Circulator:C1  {NETS}  Direction=CCW IL=0.4 Isolation=20 RL=18 {PIM}",         3, "real circulator, CCW" },
        { "Coupler:K1     {NETS}  Coupling=20 Phase=90 deg Directivity=200 IL=0 RL=200 {PIM}", 4, "20 dB quadrature coupler" },
        { "Coupler:K1     {NETS}  Coupling=3.0103 Phase=90 deg Directivity=25 IL=0.2 RL=22 {PIM}", 4, "90 hybrid, real numbers" },
        { "Coupler:K1     {NETS}  Coupling=3.0103 Phase=180 deg Directivity=200 IL=0 RL=200 {PIM}", 4, "180 hybrid" },
        { "Coupler:K1     {NETS}  Coupling=10 Phase=0 deg Directivity=30 IL=0.1 RL=20 {PIM}", 4, "in-phase coupler" },
    };

    private static string Build(string line, int ports, string pim)
        => $"{Ports(ports)}\n{line.Replace("{NETS}", Nets(ports)).Replace("{PIM}", pim)}\n";

    // ── Off is off, exactly ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Blocks))]
    public void AtTheDefault_TheBlockIsLinear_AndSaysNothingAboutIntermod(
        string line, int ports, string what)
    {
        // Two spellings of "off": the parameter absent altogether, and the -200 dBm default written
        // out. Both must give a LINEAR netlist and BIT-IDENTICAL S — "off" is not "a very small
        // intermod", it is no overlay at all.
        var (bare,   sBare)   = Sweep(Build(line, ports, ""),                    Band);
        var (stated, sStated) = Sweep(Build(line, ports, "PIM=-200 PIMPc=43"),   Band);

        Assert.Empty(bare.NonlinearComponents);
        Assert.Empty(stated.NonlinearComponents);

        for (int f = 0; f < Band.Length; f++)
        for (int p = 0; p < ports; p++)
        for (int q = 0; q < ports; q++)
            Assert.Equal(sBare[f][p, q], sStated[f][p, q]);

        output.WriteLine($"{what}: bit-identical with PIM off, {ports} ports, {Band.Length} points");
    }

    // ── On changes the code path and not the answer ───────────────────────────

    [Theory]
    [MemberData(nameof(Blocks))]
    public void WithPimOn_TheNetlistIsNonlinear_AndTheSParametersAreUnmoved(
        string line, int ports, string what)
    {
        var (off, sOff) = Sweep(Build(line, ports, ""), Band);
        var (on,  sOn)  = Sweep(Build(line, ports, "PIM=-110 PIMPc=43"), Band);

        Assert.Empty(off.NonlinearComponents);
        Assert.Single(on.NonlinearComponents);          // the block itself, and nothing else

        double worst = 0;
        for (int f = 0; f < Band.Length; f++)
        for (int p = 0; p < ports; p++)
        for (int q = 0; q < ports; q++)
            worst = Math.Max(worst, (sOff[f][p, q] - sOn[f][p, q]).Magnitude);

        output.WriteLine($"{what}: worst |ΔS| between the linear stamp and the PIM path = {worst:E3}");
        Assert.True(worst < 1e-12,
            $"{what}: PIM changed the reported S by {worst:E3} — the Y derivation is not the "
          + $"inverse of the S being stamped");
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void TheAgreementDoesNotDependOnTheStatedLevel(string line, int ports, string what)
    {
        // The brief asks for agreement with PIM set 60 dB below where it could matter. It is
        // actually exact at EVERY level, and for a reason worth stating rather than tuning around:
        // ψ(x) = Vsat·tanh(x/Vsat) − x has ψ'(0) = 0, so the linearisation at the zero-bias
        // operating point is Y with nothing added to it, whatever Vsat is.
        var (_, gentle) = Sweep(Build(line, ports, "PIM=-170 PIMPc=43"), Band);   // still ON: -150 is the floor
        var (_, harsh)  = Sweep(Build(line, ports, "PIM=-40  PIMPc=10"), Band);

        double worst = 0;
        for (int f = 0; f < Band.Length; f++)
        for (int p = 0; p < ports; p++)
        for (int q = 0; q < ports; q++)
            worst = Math.Max(worst, (gentle[f][p, q] - harsh[f][p, q]).Magnitude);

        output.WriteLine($"{what}: worst |ΔS| between a -170 dBm and a -40 dBm spec = {worst:E3}");
        Assert.True(worst < 1e-12, $"{what}: the small-signal S moved with the PIM level ({worst:E3})");
    }

    // ── The quadrature bucket ─────────────────────────────────────────────────

    [Fact]
    public void TheHybridWithPimOn_StillHoldsItsNinetyDegrees_AtEveryFrequency()
    {
        // The gate a weighting term with the WRONG SIGN passes every amplitude test and fails. With
        // PIM on the hybrid's S comes out of H[0]·Re(Y) + H[2](ω)·Im(Y) instead of the wave
        // constraint, and H[2] = −j·sign(ω) instead of +j would put the coupled arm at +90°:
        // identical magnitudes, mirrored phase.
        const string line =
            "Coupler:H1  {NETS}  Coupling=3.0103 Phase=90 deg Directivity=200 IL=0 RL=200 {PIM}";

        var (_, s) = Sweep(Build(line, 4, "PIM=-110 PIMPc=43"), Band);

        for (int f = 0; f < Band.Length; f++)
        {
            double deg = (s[f][2, 0].Phase - s[f][1, 0].Phase) * 180.0 / Math.PI;
            while (deg <= -180) deg += 360;
            while (deg >   180) deg -= 360;
            output.WriteLine($"{Band[f] / 1e9:F1} GHz: arg(S31) − arg(S21) = {deg:F9}°");
            Assert.Equal(-90.0, deg, 9);
        }
    }

    // ── DC ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Blocks))]
    public void EachBlockWithPimOn_SolvesAtDc(string line, int ports, string what)
    {
        // An S-parameter run with any nonlinear component in it BEGINS with a nonlinear DC solve of
        // the whole netlist, at ω = 0, where H[2](0) = 0 removes the quadrature bucket entirely.
        // Reaching a swept S at all is that solve having converged; this states the dependency so a
        // future singular-at-DC block fails with a sentence rather than a mystery.
        var (nl, s) = Sweep(Build(line, ports, "PIM=-110 PIMPc=43"), [1e9]);
        Assert.Single(nl.NonlinearComponents);
        for (int p = 0; p < ports; p++)
        for (int q = 0; q < ports; q++)
            Assert.True(double.IsFinite(s[0][p, q].Real) && double.IsFinite(s[0][p, q].Imaginary),
                        $"{what}: S[{p},{q}] is not finite — the DC solve did not produce a bias point");

        output.WriteLine($"{what}: DC solve converged, swept S finite");
    }

    [Fact]
    public void TheIdealQuadratureHybridIsAnOpenCircuitAtDc_AndThatIsWhyItsPortsMustBeTerminated()
    {
        // Recorded rather than papered over, as the brief asks. Z0·Y = j·(2t·Q − P) for the ideal
        // 3 dB 90° hybrid — PURELY imaginary — so with H[2](0) = 0 the block contributes NOTHING to
        // the DC Jacobian: at ω = 0 it is four open circuits. That is the honest answer for a
        // frequency-flat quadrature block (its S is a Hilbert transform, which is not a network),
        // and it is solvable because every port here sees a resistive path to ground. A hybrid port
        // wired only to reactances would float at DC — which is the ordinary floating-node case the
        // DC engine's own gmin already covers, not a special case of this block.
        const string line =
            "Coupler:H1  {NETS}  Coupling=3.0103 Phase=90 deg Directivity=200 IL=0 RL=200 {PIM}";

        var (_, s) = Sweep(Build(line, 4, "PIM=-110 PIMPc=43"), [1e9]);
        Assert.Equal(1.0 / Math.Sqrt(2.0), s[0][1, 0].Magnitude, 4);
        Assert.Equal(1.0 / Math.Sqrt(2.0), s[0][2, 0].Magnitude, 4);
    }
}
