using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Core.Devices;

/// <summary>Which port a stated <c>IP3</c> is referred to — brief-sys-5's decision D5.</summary>
public enum Ip3Reference
{
    /// <summary>The number is an IIP3: it is the intercept at the amplifier's own input port.</summary>
    Input,

    /// <summary>
    /// The number is an OIP3, the form a power-amplifier datasheet quotes, and the model converts it
    /// with <c>IIP3 = OIP3 − Gain</c>.
    /// </summary>
    Output,
}

/// <summary>
/// The ideal power amplifier: a two-port that makes a signal bigger and distorts it, and does
/// nothing else. Gain, return loss, reverse isolation, a third-order intercept — and <b>no DC power
/// consumption</b>, which is the owner's specification for it. There is no bias pin, no supply
/// parameter, no efficiency, no PAE and no thermal node; a component with those would be competing
/// with the FET models, which is the wrong tool for a system block diagram.
///
/// <para>Two ports, four nets, <c>[in+, in−, out+, out−]</c> — the 2N-net ± pair convention every
/// block in this family uses. The single-ended schematic tile ties each port's − net to ground at
/// extraction.</para>
///
/// <para><b>Its S-matrix, in full:</b></para>
/// <code>
///   S21 = 10^(Gain/20)               a gain is what the part is FOR, so it never snaps
///   S11 = 10^(−RLin/20)              0 exactly at RLin ≥ 150 dB — the default 200 means MATCHED
///   S22 = 10^(−RLout/20)             likewise
///   S12 = 10^(−S12/20)               0 exactly at S12 ≥ 150 dB — the default 200 means UNILATERAL
/// </code>
///
/// <para><b>Unilateral by default, and that is what makes it unconditionally stable.</b> With
/// <c>S12 = 0</c> the reverse path is ABSENT rather than small: no entry is stamped, no signal
/// returns from the load to the input, and no mismatch anywhere in the circuit can start an
/// oscillation. Setting <c>S12</c> to a finite number is what makes stability a question at all —
/// it is the parameter that creates a loop, and a loop with <c>|S12·S21| ≥ (1+S11)(1+S22)</c> has
/// no admittance form, which the constructor refuses BY NAME when a nonlinearity is also asked for
/// (see below). An amplifier left unilateral cannot reach that state.</para>
///
/// <para><b>Two things fall out of the form rather than being parameters, and a user should be told
/// rather than left to discover them.</b></para>
/// <list type="bullet">
/// <item><description><b>P1dB is not adjustable independently of the intercept.</b> An ideal
/// amplifier has ONE nonlinearity, and the compression point is that nonlinearity's own value:
/// <c>IIP3 − 8.9625 dB</c>, input-referred. (brief-sys-5 says 9.6 dB; 9.6357 dB is the textbook
/// CUBIC's value, and tanh's is 0.67 dB away from it — see <see cref="ThirdOrderLimiter"/>, where
/// both are derived.) A separate <c>P1dB</c> or <c>Psat</c> knob would be a second control over one
/// curve, which is how a model stops being falsifiable.</description></item>
/// <item><description><b>There is gain at DC.</b> A memoryless block with a flat gain has it at
/// every frequency, ω = 0 included, and that is what makes the HB DC harmonic well behaved rather
/// than a special case. A design that needs the DC path broken should place a series capacitor,
/// exactly as hardware does.</description></item>
/// </list>
///
/// <para><b>The linear half is the family's wave-constraint stamp</b> (<see cref="IdealSBlockModel"/>),
/// so an amplifier with <c>IP3</c> left at its default is an ordinary <see cref="ModelKind.Linear"/>
/// block: it costs nothing in the HB nonlinear partition and it creates NOTHING — the "ideal is
/// exactly linear" gate asserts the absence of harmonics, not their smallness, and absence is only
/// available on this path.</para>
///
/// <para><b>The nonlinear half is the same S written as an admittance</b>, with the limiter on the
/// forward transfer only:</para>
/// <code>
///   i_in  = Y11·v_in + Y12·v_out
///   i_out = Y21·ψ(v_in) + Y22·v_out ,      ψ(x) = Vsat·tanh(x/Vsat)
/// </code>
/// <para>which is brief-sys-5's own "the input is a resistance, the output is a source behind Zout"
/// written for the general S: with <c>S11 = S22 = S12 = 0</c> the four entries reduce to exactly
/// <c>Y11 = 1/Zin</c>, <c>Y12 = 0</c>, <c>Y22 = 1/Zout</c> and <c>Y21 = −G/Zout</c> with
/// <c>G = 2·√(10^(Gain/10)·Zout/Zin)</c>, the brief's voltage gain. Only the forward term is
/// limited: the input really is a resistance, and it stays one at every drive.</para>
///
/// <para><b>Why the general S and not those four terms literally.</b> Because the four terms are
/// only correct when the amplifier is matched, and the return losses are parameters. A Thevenin
/// source of <c>G·ψ(v_in)</c> behind a resistance chosen to give <c>S11</c> and <c>S22</c> delivers
/// <c>|S21| = √(10^(Gain/10))·(1 + S11)·(1 − S22)</c> — so typing a 10 dB input return loss on a
/// 20 dB amplifier would silently make it a 22.4 dB amplifier, and a 10 dB OUTPUT return loss would
/// make it 17.3 dB. Neither is what the user typed, and a datasheet states gain and return loss as
/// independent measurements. Deriving Y from the S the parameters name makes all four entries
/// exactly the numbers on the netlist line, at every combination.</para>
///
/// <para><b>And the intercept is referred to AVAILABLE input power</b>, which is what a datasheet
/// means by IIP3, so <c>Vsat</c> carries the same correction: the port voltage at a given available
/// power is <c>(1 + S11)</c> times its matched value, and the factor is exactly 1 at the default
/// return loss. It is exact for the unilateral amplifier at any <c>RLin</c>; with a reverse path
/// turned on, the input voltage also depends on what the load reflects, and no memoryless
/// coefficient can carry that.</para>
/// </summary>
public sealed class AmplifierModel : IdealSBlockModel
{
    /// <summary>
    /// At or above this stated <c>IP3</c> (dBm) the forward path is left EXACTLY linear: no limiter,
    /// no nonlinear partition, <see cref="ModelKind.Linear"/>.
    ///
    /// <para>190 rather than the mixer's 90 because the sentinel differs — this component's default
    /// is 200 dBm, and the family's rule is that "off" begins 10 dB inside the default, which is
    /// nobody's real number either way. The test is applied to the number the user TYPED, before
    /// <see cref="Ip3Reference"/> is applied, so <c>IP3 = 200</c> means ideal whichever port it is
    /// referred to.</para>
    /// </summary>
    private const double InterceptOffDbm = 190.0;

    private readonly Complex _s11, _s12, _s21, _s22;
    private readonly double  _vsat;              // forward soft-limit scale, volts (0 = exactly linear)
    private readonly double[,] _y = new double[2, 2];
    private bool _degenerate;   // set once, by the BuildAdmittance the constructor calls

    /// <param name="gainDb">Small-signal power gain, input port to output port, dB.</param>
    /// <param name="ip3Dbm">
    /// Third-order intercept, dBm, referred to <paramref name="ip3Ref"/>. At or above 190 dBm the
    /// amplifier is exactly linear and never compresses.
    /// </param>
    /// <param name="ip3Ref">Whether <paramref name="ip3Dbm"/> is an IIP3 or an OIP3.</param>
    /// <param name="zIn">The impedance the input port PRESENTS, ohms. May be complex only while the
    /// amplifier is LINEAR (<c>IP3</c> at 200) — see <see cref="IdealSBlockModel.RefuseComplexZ0"/>,
    /// and note that the tile's own default IP3 is 40 dBm, so a freshly placed Amp is not.</param>
    /// <param name="zOut">The impedance the output port PRESENTS, ohms; the same restriction.</param>
    /// <param name="rlInDb">Input return loss; ≥ 150 dB means exactly matched.</param>
    /// <param name="rlOutDb">Output return loss; ≥ 150 dB means exactly matched.</param>
    /// <param name="s12Db">Reverse isolation; ≥ 150 dB means unilateral, and no entry is stamped.</param>
    /// <param name="noise">The noise specification (<c>NF</c>, <c>NFopt</c>, <c>Rn</c>, <c>Gopt</c>), or null: a noiseless
    /// amplifier, as one with no <c>NF=</c> has always been. See <see cref="AmplifierNoiseSpec"/>.</param>
    public AmplifierModel(double gainDb, double ip3Dbm, Ip3Reference ip3Ref,
                          Complex zIn, Complex zOut,
                          double rlInDb, double rlOutDb, double s12Db,
                          AmplifierNoiseSpec? noise = null)
        // CONJUGATED, exactly as FilterModel's are and for the same reason: Zin/Zout name what the
        // port PRESENTS, and the stamp works in the reference impedance. A real pair is unchanged.
        : base([Complex.Conjugate(zIn), Complex.Conjugate(zOut)])
    {
        // A gain is not a suppression and never snaps to zero: an amplifier set to −200 dB is a
        // 200 dB pad, and stamping it as one is the honest answer. The other three ARE suppressions.
        _s21 = new Complex(Math.Pow(10.0, gainDb / 20.0), 0);
        _s11 = new Complex(SuppressedAmplitude(rlInDb),   0);
        _s22 = new Complex(SuppressedAmplitude(rlOutDb),  0);
        _s12 = new Complex(SuppressedAmplitude(s12Db),    0);

        if (ip3Dbm < InterceptOffDbm)
        {
            // The compression below is a real i = f(v) built from a real admittance matrix, which a
            // complex reference impedance would make complex. Refused by name, here, where the
            // instance can be named — and NOT on the linear path, whose wave stamp takes any Z0.
            RefuseComplexZ0("compression (a finite IP3)");

            // D5: ONE intercept field plus a reference, because OIP3 = IIP3 + Gain is an identity
            // and two independent fields can be made to contradict each other. The limiter acts on
            // the INPUT-referred signal, so the conversion happens here, once.
            double iip3Dbm = ip3Ref == Ip3Reference.Output ? ip3Dbm - gainDb : ip3Dbm;

            // (1 + S11) refers the intercept to AVAILABLE input power rather than to the port
            // voltage — exactly 1 at the default return loss. See the class remarks.
            _vsat = ThirdOrderLimiter.SaturationVolts(iip3Dbm, Z0Of(0).Real) * (1.0 + _s11.Real);
        }

        BuildAdmittance();

        if (noise is { IsStated: true })
        {
            NoiseSpec  = noise;
            NoiseParameters = noise.Resolve(zIn);
        }
    }

    /// <summary>What the instance line stated about noise, or null when it stated nothing (a noiseless amplifier).</summary>
    public AmplifierNoiseSpec? NoiseSpec { get; }

    /// <summary>
    /// The amplifier's four noise parameters, or null when it has none: <see cref="AmplifierNoiseSpec.Resolve"/> of what the
    /// line stated, Γopt referenced to <c>Re Zin</c>. The block's noise is exactly a two-port with these parameters, all of
    /// it referred to the input; the S-parameter noise analysis turns them into port currents through the block's own Y.
    /// </summary>
    public TwoPortNoise? NoiseParameters { get; }

    /// <summary>
    /// Whether the block has an admittance matrix, which its noise currents are written through. False only when the
    /// reverse loop gain is unity (<c>(1+S11)(1+S22) = S12·S21</c>) — an oscillator, which the linear stamp still takes.
    /// </summary>
    public bool HasAdmittance => !_degenerate;

    /// <summary>
    /// The forward soft-limit scale in volts, or 0 when the forward path is exactly linear.
    /// Diagnostic only: no gate reads a level back out of it.
    /// </summary>
    public double SaturationVolts => _vsat;

    /// <inheritdoc/>
    protected override bool PortParameterIsPresentedImpedance => true;

    /// <inheritdoc/>
    protected override bool HasOwnNonlinearity => _vsat > 0.0;

    /// <summary>Unilateral, so the two ports are NOT interchangeable and are named rather than numbered.</summary>
    public override string[] TerminalNames => ["in+", "in-", "out+", "out-"];

    /// <summary>The forward gain is a dependent source: an ideal amplifier IS one
    /// (brief-wsprobe-6 §3).</summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <inheritdoc/>
    public override string? PassivationNote =>
        "forward gain → 0; matched terminations and the reverse isolation kept";

    /// <summary>
    /// Forward gain → 0. What is left is what the block's other three entries already say — matched
    /// terminations at both ports and the reverse isolation, kept — so the passivated block is an
    /// attenuator in the reverse direction and nothing in the forward one, whose <c>σ_max</c> is
    /// <c>|S12| ≤ 1</c>.
    /// </summary>
    protected override void PassivateS(Complex[,] s) => s[1, 0] = Complex.Zero;

    /// <summary>
    /// The compressing amplifier's own dependent source, in the linearised block: the forward
    /// transfer <c>∂I_out/∂V_in</c>. The reverse term <c>∂I_in/∂V_out</c> is the isolation and stays,
    /// exactly as <c>S12</c> does above.
    /// </summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances => [(1, 0)];

    protected override void FillS(double omega, Complex[,] s)
    {
        s[0, 0] = _s11;  s[0, 1] = _s12;
        s[1, 0] = _s21;  s[1, 1] = _s22;
    }

    /// <summary>
    /// <c>Y = G·(I + S)⁻¹·(I − S)·G</c> with <c>G = diag(1/√Z0)</c>, written out for the 2×2 real
    /// case — the same identity <see cref="PimOverlay"/> inverts numerically, done in closed form
    /// here because a two-port has one.
    ///
    /// <para>The ONE refusal: <c>det(I + S) = (1+S11)(1+S22) − S12·S21</c> vanishes when the reverse
    /// loop gain reaches unity, and a component with no Y cannot be written as the memoryless
    /// <c>i = f(v)</c> that every nonlinearity in this repository is. It is raised HERE, naming the
    /// parameters that caused it, rather than as a NaN inside a Newton iteration where nothing on
    /// the stack could say which instance it was. Note where it is NOT raised: the LINEAR amplifier
    /// stamps the definition of S and has no such degeneracy, so the same numbers run perfectly well
    /// with <c>IP3</c> at its default — which is the honest answer, because an oscillator has a
    /// small-signal S-matrix and does not have a memoryless large-signal one.</para>
    /// </summary>
    private void BuildAdmittance()
    {
        double s11 = _s11.Real, s12 = _s12.Real, s21 = _s21.Real, s22 = _s22.Real;

        double det   = (1.0 + s11) * (1.0 + s22) - s12 * s21;
        double scale = Math.Max(1.0, Math.Max(Math.Abs(s12 * s21), (1.0 + s11) * (1.0 + s22)));
        if (Math.Abs(det) <= 1e-12 * scale)
        {
            _degenerate = true;
            if (_vsat > 0.0) RefuseNoAdmittanceForm();
            return;                                  // linear: the wave stamp needs no Y at all
        }

        // The normalised admittance ỹ = (I + S)⁻¹(I − S), then de-normalised by 1/√(Z0p·Z0q).
        double y11 = ((1.0 - s11) * (1.0 + s22) + s12 * s21) / det;
        double y12 = -2.0 * s12 / det;
        double y21 = -2.0 * s21 / det;
        double y22 = ((1.0 + s11) * (1.0 - s22) + s12 * s21) / det;

        // .Real is exact rather than lossy here: a complex reference impedance is refused above
        // before _vsat is set, and this matrix is read only on the nonlinear path.
        double zi = Z0Of(0).Real, zo = Z0Of(1).Real, zm = Math.Sqrt(zi * zo);
        _y[0, 0] = y11 / zi;  _y[0, 1] = y12 / zm;
        _y[1, 0] = y21 / zm;  _y[1, 1] = y22 / zo;
    }

    /// <inheritdoc/>
    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        var i  = new double[2];
        var q  = new double[2];
        var dg = new double[2, 2];
        var dc = new double[2, 2];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    /// <inheritdoc/>
    public override bool PrefersGridEvaluate => _vsat > 0.0;

    /// <inheritdoc/>
    protected override bool HasEvaluateInto => _vsat > 0.0;

    /// <summary>
    /// The refusal, in one place because it is raised from two: at construction when a nonlinearity
    /// was asked for, and from <see cref="EvaluateInto"/> if a caller ever evaluates a LINEAR
    /// amplifier in this state — which no engine does, since a linear block goes through
    /// <see cref="IdealSBlockModel.Stamp"/> and never through <c>Evaluate</c> at all.
    /// </summary>
    [DoesNotReturn]
    private static void RefuseNoAdmittanceForm()
        => throw new InvalidOperationException(
            "Amp: this amplifier's reverse path closes a loop with unity gain, so it has no "
          + "admittance matrix and its compression cannot be written down - (1+S11)(1+S22) is "
          + "exactly S12*S21. That is an oscillator, not an amplifier. Raise S12 (200 dB, the "
          + "default, means unilateral and unconditionally stable), or lower Gain, or set IP3 "
          + "to 200 to keep the amplifier linear, where the same numbers stamp without trouble.");

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        if (_degenerate) RefuseNoAdmittanceForm();

        double vin = v[0], vout = v[1];
        ThirdOrderLimiter.Apply(_vsat, vin, out double u, out double du);

        i[0] = _y[0, 0] * vin + _y[0, 1] * vout;
        i[1] = _y[1, 0] * u   + _y[1, 1] * vout;

        // Purely conductive — an ideal amplifier stores no charge, so it is frequency-flat at every
        // harmonic the solver retains, DC included.
        q[0] = q[1] = 0.0;

        dg[0, 0] = _y[0, 0];       dg[0, 1] = _y[0, 1];
        dg[1, 0] = _y[1, 0] * du;  dg[1, 1] = _y[1, 1];

        dc[0, 0] = dc[0, 1] = dc[1, 0] = dc[1, 1] = 0.0;
    }
}

/// <summary>
/// An amplifier's noise as its instance line states it (brief NZ-3). <c>NF</c> alone is the datasheet number: the noise
/// figure when the block is driven from a source equal to its own <c>Zin</c>, at T0. <c>NFopt</c>, <c>Rn</c> and
/// <c>Gopt</c> add the rest of the four noise parameters, for a block whose noise match is not its power match.
///
/// <para><b>NF alone is one input noise wave.</b> A wave of temperature <c>Te = T0·(F − 1)</c> entering port 1,
/// uncorrelated with anything, and nothing at port 2. From a source Zs its noise factor is
/// <c>F(Zs) = 1 + (Te/T0)·|Zs − Zin|² / (4·Re Zs·Re Zin) + Te/T0</c>, written as noise parameters: <c>Fmin = F</c>,
/// <c>Zopt = Zin</c>, and <c>Rn = (F − 1)·|Zin|² / (4·Re Zin)</c> — the correlation of the input noise voltage and current
/// is then complete (det C_A = 0), which is what one wave is. The NF comes back out of the analysis exactly, because at
/// <c>Zs = Zin</c> the mismatch term is zero.</para>
///
/// <para><b>The four-parameter form.</b> <c>Fmin = NFopt</c> (default NF); <c>Γopt = Gopt</c>, referenced to
/// <c>Re Zin</c> (default: the Γ of <c>Zin</c> itself, 0 for a real <c>Zin</c>); and <c>Rn</c>, when it is not stated:
/// with NF and NFopt both stated and a Γopt away from <c>Zin</c>, the Rn that makes the noise factor at <c>Zs = Zin</c>
/// equal NF; otherwise the one-wave value above, at <c>Fmin</c>. A set that describes no physical two-port
/// (<c>Fmin − 1 &gt; 4·Rn·Re Yopt</c>) or that contradicts itself is refused by name.</para>
/// </summary>
/// <param name="NfDb">The noise figure at <c>Zs = Zin</c>, dB, or null.</param>
/// <param name="NfOptDb">The minimum noise figure, dB, or null.</param>
/// <param name="RnOhm">The equivalent noise resistance, Ω, or null.</param>
/// <param name="GammaOpt">The optimum source reflection referenced to <c>Re Zin</c>, or null.</param>
public sealed record AmplifierNoiseSpec(double? NfDb, double? NfOptDb, double? RnOhm, Complex? GammaOpt)
{
    /// <summary>Whether the line stated a noise figure at all: <c>NF</c> or <c>NFopt</c>. <c>Rn</c> or <c>Gopt</c> alone
    /// describe no noise and are refused rather than ignored.</summary>
    public bool IsStated => NfDb is not null || NfOptDb is not null;

    /// <summary>The four noise parameters for an amplifier whose input presents <paramref name="zIn"/>, Γopt referenced to
    /// <c>Re zIn</c>. Throws, naming the keys, on a set that is not physical.</summary>
    public TwoPortNoise Resolve(Complex zIn)
    {
        if (NfDb is < 0 || NfOptDb is < 0)
            throw new InvalidOperationException(AmplifierNoiseDiagnostics.BelowZero(NfDb, NfOptDb).Render());
        if (NfDb is { } nf0 && NfOptDb is { } opt0 && nf0 < opt0 - 1e-12)
            throw new InvalidOperationException(AmplifierNoiseDiagnostics.BelowMinimum(nf0, opt0).Render());

        double r     = zIn.Real;
        double fmin  = Math.Pow(10, (NfOptDb ?? NfDb!.Value) / 10);
        var    yIn   = 1 / zIn;
        var    yopt  = GammaOpt is { } g ? (1 - g) / (r * (1 + g)) : yIn;
        var    gamma = GammaOpt ?? (1 - r * yIn) / (1 + r * yIn);
        // The one-wave Rn at Fmin: 4·Rn·Re Yin = Fmin − 1.
        double oneWave = (fmin - 1) / (4 * yIn.Real);

        double rn;
        if (RnOhm is { } given) rn = given;
        else if (NfDb is { } nf && NfOptDb is not null && nf - NfOptDb.Value > 1e-12)
        {
            double mis = Sq(yIn - yopt);
            if (!(mis > 1e-24 * Sq(yIn)))
                throw new InvalidOperationException(AmplifierNoiseDiagnostics.NoMinimumPlace(nf, NfOptDb.Value).Render());
            rn = (Math.Pow(10, nf / 10) - fmin) * yIn.Real / mis;
        }
        else rn = oneWave;

        var p = new TwoPortNoise(fmin, gamma, rn, r);
        if (!NoiseMetrics.IsRealizable(p, 1e-9 * Math.Max(1, fmin - 1)))
            throw new InvalidOperationException(
                AmplifierNoiseDiagnostics.Unphysical(10 * Math.Log10(fmin), rn, gamma.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)).Render());
        return p;
    }

    private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;
}
