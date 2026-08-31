using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Ideal three-port mixer: a memoryless multiplier with matched ports, plus the four
/// non-idealities that can be stated without giving the device a memory.
///
/// <code>
///   i_rf = ( v_rf − a_LR·v_lo ) / Zrf
///   i_lo =   v_lo / Zlo
///   i_if = ( v_if − K·u(v_rf)·v_lo − a_LI·v_lo − a_RI·v_rf ) / Zif
/// </code>
///
/// <para><b>Three ports, six nets, in the repository's own ± pair order</b> — the same 2N-net
/// convention <c>SDD</c>, <c>Z_Port</c> and <c>VCCS</c> use, so <c>PortCount</c> is 3 and
/// <c>Nodes = [rf+, rf−, lo+, lo−, if+, if−]</c>. The single-ended schematic tile ties each
/// port's − net to ground at extraction; the engine never sees the difference.</para>
///
/// <para><b>Why a multiplier and not a switch.</b> The <c>Evaluate</c> contract is memoryless in
/// the port voltages — there is no time argument and no internal oscillator — so the LO must
/// arrive through a port like any other signal, and the only ideal mixing law expressible that
/// way is the product. A hard-switching (commutating) mixer would give an LO-amplitude-independent
/// 2/π conversion, but <c>sgn</c> has a delta-function derivative and no Newton step can survive
/// it. The product's honest consequence is stated everywhere it matters: <b>conversion gain
/// tracks LO amplitude</b>, which is why the gain is specified together with the LO drive it
/// holds at (see <see cref="MultiplierK"/>).</para>
///
/// <para><b>Both sidebands appear.</b> cos(ω_r t)·cos(ω_l t) is half the sum plus half the
/// difference, so an RF tone produces |f_rf − f_lo| and f_rf + f_lo at equal amplitude. This
/// device does not choose one; a single-sideband result comes from filtering, or from an image
/// rejection network built around two of these, exactly as it does in hardware.</para>
///
/// <para><b>What it does in a linear engine.</b> Nothing has to be added for that: the base
/// <see cref="ComponentModel.StampLinearized"/> evaluates the Jacobian at the DC operating point,
/// where <c>v_lo = 0</c>, so the conversion term <c>∂i_if/∂v_rf = −K·v_lo/Zif</c> is exactly
/// zero and an S-parameter run reports the port matches and the three leakage paths and no
/// conversion at all. That is the correct answer rather than a missing one — S-parameters are a
/// single-frequency measurement and conversion moves energy between frequencies. Conversion gain
/// comes from a swept harmonic-balance run.</para>
/// </summary>
public sealed class MixerModel : ComponentModel
{
    /// <summary>
    /// At or above this many dB of isolation the leakage coefficient is set to EXACTLY zero rather
    /// than to 10^(−iso/20), so the default mixer's off-diagonal stamps are absent instead of being
    /// 1e-10 — <see cref="ComponentModel.StampLinearized"/> skips a zero admittance, and "ideal"
    /// should mean the entry is not there. 150 dB is far below the point where the number would
    /// matter to any result and far above any isolation a real part claims.
    /// </summary>
    private const double IsolationOff = 150.0;

    /// <summary>
    /// At or above this input-referred IP3 (dBm) the RF path is left EXACTLY linear rather than
    /// running a tanh whose argument is ~1e-5. Same reasoning as <see cref="IsolationOff"/>: the
    /// ideal device should take the identity path, not a path that merely rounds to it.
    /// </summary>
    private const double CompressionOff = 90.0;

    private readonly double _grf, _glo, _gif;   // port conductances 1/Z
    private readonly double _k;                 // multiplier constant, 1/V
    private readonly double _aLR, _aLI, _aRI;   // leakage voltage coefficients (0 = ideal)
    private readonly double _vsat;              // RF soft-limit scale, volts (0 = no compression)

    public MixerModel(
        double convGainDb, double ploDbm,
        double zRf, double zLo, double zIf,
        double isoLoRfDb, double isoLoIfDb, double isoRfIfDb,
        double iip3Dbm)
    {
        // A zero or negative port impedance is a short, not a port. Fall back to 50 Ω rather than
        // dividing by zero halfway through a Newton iteration, where the NaN would surface as a
        // non-convergence with no cause attached to it.
        zRf = zRf > 0 ? zRf : 50.0;
        zLo = zLo > 0 ? zLo : 50.0;
        zIf = zIf > 0 ? zIf : 50.0;

        _grf = 1.0 / zRf;
        _glo = 1.0 / zLo;
        _gif = 1.0 / zIf;

        // K from the stated gain, derived once here so the user never types a volt^-1.
        //
        // With every port matched, an RF tone of peak A and an LO of peak B put K·A·B/2 open-circuit
        // at each sideband, halved again by the Zif/Zload divider: K·A·B/4 across the load. Then
        //   G = P_if/P_rf = (K·A·B/4)²/(2·Zif) ÷ A²/(2·Zrf) = K²·B²·Zrf/(16·Zif)
        // which inverts to the line below at the nominal LO amplitude B = √(2·Plo·Zlo). A is absent
        // from it — the conversion gain is independent of RF drive, which is what makes it a gain.
        double gLin = Math.Pow(10.0, convGainDb / 10.0);
        double ploW = 1e-3 * Math.Pow(10.0, ploDbm / 10.0);
        double bNom = Math.Sqrt(2.0 * ploW * zLo);
        _k = bNom > 0 ? (4.0 / bNom) * Math.Sqrt(gLin * zIf / zRf) : 0.0;

        _aLR = Leak(isoLoRfDb, zRf, zLo);
        _aLI = Leak(isoLoIfDb, zIf, zLo);
        _aRI = Leak(isoRfIfDb, zIf, zRf);

        // The RF path's soft limiter. tanh is used rather than the textbook a1·x − a3·x³ because a
        // bare cubic turns over and goes NEGATIVE past its peak: Newton then finds the wrong root
        // and the run converges cleanly to nonsense. tanh is monotone and bounded everywhere, and
        // its own expansion x − x³/3 + … fixes the intercept exactly — matching the third-order
        // term to a1=1, a3=1/(3·Vsat²) in IIP3 = √(4·a1/(3·a3)) gives IIP3 = 2·Vsat in volts.
        _vsat = iip3Dbm >= CompressionOff
              ? 0.0
              : 0.5 * Math.Sqrt(2.0 * 1e-3 * Math.Pow(10.0, iip3Dbm / 10.0) * zRf);
    }

    /// <summary>
    /// A leakage coefficient in VOLTS per volt, from an isolation in dB stated as a POWER ratio
    /// between two matched ports. Delivering (a·v_src/2) across Z_dst against v_src²/(2·Z_src) at
    /// the source gives a²·Z_src/(4·Z_dst) = 10^(−iso/10), hence the 2 and the impedance ratio.
    /// </summary>
    private static double Leak(double isoDb, double zDst, double zSrc)
        => isoDb >= IsolationOff
         ? 0.0
         : 2.0 * Math.Sqrt(Math.Pow(10.0, -isoDb / 10.0) * zDst / zSrc);

    /// <summary>The derived multiplier constant, 1/V: v_if(open) = K·v_rf·v_lo.</summary>
    public double MultiplierK => _k;

    /// <summary>The RF soft-limit scale in volts, or 0 when the RF path is exactly linear.</summary>
    public double SaturationVolts => _vsat;

    public override int       PortCount => 3;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    public override string[] TerminalNames => ["rf+", "rf-", "lo+", "lo-", "if+", "if-"];

    // Nonlinear device: the linear engines call StampLinearized instead (base implementation).
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        var i  = new double[3];
        var q  = new double[3];
        var dg = new double[3, 3];
        var dc = new double[3, 3];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    /// <inheritdoc/>
    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    /// <inheritdoc/>
    protected override bool HasEvaluateInto => true;

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vRf = v[0], vLo = v[1], vIf = v[2];

        // u = the RF voltage the mixing core actually sees, and du = ∂u/∂v_rf.
        double u, du;
        if (_vsat > 0.0)
        {
            double t = Math.Tanh(vRf / _vsat);
            u  = _vsat * t;
            du = 1.0 - t * t;
        }
        else
        {
            u  = vRf;
            du = 1.0;
        }

        i[0] = (vRf - _aLR * vLo) * _grf;
        i[1] =  vLo * _glo;
        i[2] = (vIf - _k * u * vLo - _aLI * vLo - _aRI * vRf) * _gif;

        // Purely conductive — an ideal mixer stores no charge, so every Q and Dc entry is zero and
        // the device is frequency-flat at every harmonic the solver retains.
        q[0] = q[1] = q[2] = 0.0;

        dg[0, 0] = _grf;  dg[0, 1] = -_aLR * _grf;             dg[0, 2] = 0.0;
        dg[1, 0] = 0.0;   dg[1, 1] = _glo;                     dg[1, 2] = 0.0;
        dg[2, 0] = (-_k * du * vLo - _aRI) * _gif;
        dg[2, 1] = (-_k * u        - _aLI) * _gif;
        dg[2, 2] = _gif;

        dc[0, 0] = dc[0, 1] = dc[0, 2] = 0.0;
        dc[1, 0] = dc[1, 1] = dc[1, 2] = 0.0;
        dc[2, 0] = dc[2, 1] = dc[2, 2] = 0.0;
    }
}
