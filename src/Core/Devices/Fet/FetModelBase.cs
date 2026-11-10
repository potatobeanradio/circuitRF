using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Shared plumbing for the built-in large-signal FET models. A concrete model contributes only its
/// own drain-current law and that law's derivatives; everything below — terminals, ports, gate
/// conduction, charge storage — is identical across the family and lives here so the models cannot
/// drift apart.
///
/// <para><b>Terminals and ports.</b> Three nets, <c>gate drain source</c>, mapped onto two ports:
/// port 0 = (gate, source), port 1 = (drain, source). So <c>v[0]</c> is Vgs and <c>v[1]</c> is Vds,
/// which is the form every published FET equation is written in. The elaborator expands the three
/// declared nets into the four the port pairs need.</para>
///
/// <para><b>What is modelled.</b> The drain current and its two derivatives (gm = ∂Id/∂Vgs,
/// gds = ∂Id/∂Vds), optional forward gate conduction as a diode, and gate charge through constant
/// <c>Cgs</c>/<c>Cgd</c>.</para>
///
/// <para><b>Gate charge is selectable, because the published models differ on it.</b> Two schemes
/// are offered and the parameter <c>CapModel</c> picks:</para>
/// <list type="bullet">
/// <item><c>0</c> — no charge at all.</item>
/// <item><c>1</c> — CONSTANT <c>Cgs</c>/<c>Cgd</c> (the default).</item>
/// <item><c>2</c> — JUNCTION charge, bias-dependent, the standard depletion form applied to Vgs and
/// Vgd separately: <c>Q = Cj0·Vbi/(1−M)·[1 − (1 − V/Vbi)^(1−M)]</c> below <c>Fc·Vbi</c>, continued
/// by its tangent above. Parameters <c>Cgs</c>, <c>Cgd</c>, <c>Vbi</c>, <c>M</c>, <c>Fc</c>.</item>
/// </list>
///
/// <para><b>Polarity is a sign, not a law.</b> <see cref="Channel"/> multiplies every voltage on the
/// way in and every current and charge on the way out, so one set of equations serves both channel
/// types and the two cannot drift apart — the arrangement <see cref="BjtModel"/> uses for n-p-n and
/// p-n-p. The Jacobian passes through UNCHANGED, because the sign appears once on each side of every
/// derivative. n-channel is <c>+1</c> and every multiplication by it is exact, so an n-channel device
/// is bit-identical to one built before this parameter existed.</para>
///
/// <para><b>Each law negates its own threshold-like parameter</b> — <c>Vto</c>, <c>Vp0</c>,
/// <c>Vpk</c> — through <see cref="ChannelSign"/>, because a p-channel card states it in its own
/// convention (positive for a p-channel depletion device) and the equations below are written in the
/// n-channel one. <b>The Curtice-Ettenberg cubic law has no p-channel form and is not offered in
/// one:</b> its <c>A0</c>-<c>A3</c> polynomial is a direct fit in Vgs with no threshold to anchor a
/// sign to, so a mirror would have to negate the odd-order coefficients and leave the even ones
/// alone, and no published convention says that it does. Guessing which is a device that simulates
/// and is wrong, which is exactly what this family refuses to produce.</para>
///
/// <para><b>The intrinsic gate resistance <c>Ri</c></b> (brief NZ-4), when it is above zero, sits in series with
/// <c>Cgs</c> on an internal node <c>gi</c>: the gate diode, <c>Cgs</c> and the drain current's control voltage are all
/// on <c>gi</c>, and <c>Cgd</c> stays on the external gate. Four ports then — <c>(gate, gi)</c> the resistor,
/// <c>(gi, source)</c>, <c>(drain, source)</c> and a charge-only <c>(gate, drain)</c> for <c>Cgd</c> — the same
/// internal-node arrangement the diode's <c>Rs</c> uses, minted by the elaborator. With <c>Ri = 0</c> (the default) the
/// model is the two-port it always was, bit for bit.</para>
///
/// <para><b>Noise</b> (brief NZ-4) is the two-temperature form RF transistor noise is characterised in: <c>Ri</c>
/// thermal at the gate temperature <c>Tg</c> (default the device temperature), the output conductance <c>gds</c>
/// thermal at the drain temperature <c>Td</c> (<c>4k·Td·gds</c>; off unless stated), and flicker <c>Kf·|Id|^Af / f</c>
/// on the drain current when <c>Kf</c> is set. With none of the three set the model declares no sources and is named as
/// noiseless, as before. The family carries no extrinsic <c>Rg</c>/<c>Rd</c>/<c>Rs</c>; resistors placed beside it are
/// thermal at their own temperature.</para>
///
/// <para><b>Still NOT modelled:</b> the Statz/TOM-family charge formulation, which is a different
/// scheme again — it works on a smoothed effective voltage rather than on Vgs and Vgd separately,
/// so it is not a parameter change to the above but its own implementation. Also absent:
/// transit-time delay, breakdown, self-heating.</para>
/// </summary>
public abstract class FetModelBase : ComponentModel
{
    /// <summary>n-channel or p-channel, as the sign that multiplies every voltage, current and charge.</summary>
    public enum Channel { N = 1, P = -1 }

    private const double Boltzmann  = Temperature.Boltzmann;
    private const double ElemCharge = Temperature.ElemCharge;

    /// <summary>
    /// Nominal (parameter-extraction) temperature, °C. 26.85 °C = 300 K.
    /// Forwarding alias for <see cref="Temperature.NominalC"/>, which is now the definition — kept
    /// under this name because the factory and the FET tests refer to it and there is no reason to
    /// churn them. New code should use <see cref="Temperature"/> directly.
    /// </summary>
    public const double NominalTemperatureC = Temperature.NominalC;

    /// <summary>
    /// Temperature scaling of a parameter whose coefficient is given in **percent per degree** —
    /// the convention the published parameter tables use for Beta and Alpha. `1.01^(tc·ΔT)` is the
    /// documented form, and it is NOT the same as `1 + 0.01·tc·ΔT`: the two diverge as soon as ΔT
    /// is more than a few tens of degrees, which is exactly the range this exists for.
    /// </summary>
    protected static double ScalePercentPerDegree(double value, double tcPercentPerDeg, double dT)
        => tcPercentPerDeg == 0.0 ? value : value * System.Math.Pow(1.01, tcPercentPerDeg * dT);

    /// <summary>Temperature scaling of a parameter whose coefficient is a plain fraction per degree.</summary>
    protected static double ScaleLinear(double value, double tcPerDeg, double dT)
        => value * (1.0 + tcPerDeg * dT);

    /// <summary>An additive shift per degree — how threshold-like voltages are specified (V/°C).</summary>
    protected static double ShiftPerDegree(double value, double tcVoltsPerDeg, double dT)
        => value + tcVoltsPerDeg * dT;

    /// <summary>Device temperature minus nominal, in degrees. The argument of every relation above.</summary>
    protected static double DeltaT(double tempC, double tnomC) => tempC - tnomC;

    private readonly double _cgs, _cgd, _isGate, _nGate, _vt;
    private readonly double _vbi, _m, _fc;
    private readonly int    _capModel;
    private readonly double _s;
    private readonly double _tempK;

    // Noise and the intrinsic gate resistance (brief NZ-4), set by the factory once, before the model is used.
    private double _ri, _tg = double.NaN, _td = double.NaN, _kf, _af = 1.0;

    /// <summary>
    /// The intrinsic gate resistance and the noise parameters, from the instance line. Called by the factory straight
    /// after construction and never again: <c>Ri</c> decides the port count, which the elaborator reads to build the node
    /// list. <paramref name="tgK"/> NaN means the device temperature; <paramref name="tdK"/> NaN means no drain noise.
    /// </summary>
    internal void SetNoise(double ri, double tgK, double tdK, double kf, double af)
    {
        _ri = ri > 0 ? ri : 0.0;
        _tg = tgK;
        _td = tdK;
        _kf = kf;
        _af = af > 0 ? af : 1.0;
    }

    /// <summary>The intrinsic gate resistance, Ω, or 0 when there is none.</summary>
    public double IntrinsicGateResistance => _ri;

    /// <summary>Whether the drain has a noise source (<c>Td</c> or <c>Kf</c>). Without one, an <c>Ri</c> alone makes the FET
    /// noisy at its gate only, and the noise analysis names the noiseless drain.</summary>
    public bool HasDrainNoise => double.IsFinite(_td) || _kf != 0;

    /// <summary>True when <c>Ri</c> puts the gate side on an internal node.</summary>
    public bool HasIntrinsicGateResistance => _ri > 0;

    /// <summary>
    /// <c>+1</c> for n-channel, <c>−1</c> for p-channel. A concrete law multiplies its own
    /// threshold-like parameter by this, because a card states that parameter in its own channel's
    /// convention while every equation here is written in the n-channel one.
    /// </summary>
    protected double ChannelSign => _s;

    protected FetModelBase(double cgs, double cgd, double gateSaturationCurrent,
                           double gateEmissionCoefficient,
                           int capModel = 1, double vbi = 1.0, double m = 0.5, double fc = 0.5,
                           double tempC = NominalTemperatureC, double tnomC = NominalTemperatureC,
                           double xti = 0.0, double bandgap = 1.16,
                           Channel channel = Channel.N)
    {
        _s = (double)(int)channel;

        double dT = DeltaT(tempC, tnomC);

        // Junction potential falls with temperature; the gate capacitances follow it, plus a small
        // linear expansion term. The gate diode's saturation current rises. All three are the
        // standard relations that go with these parameters — the same ones a junction diode uses.
        // The three relations are SHARED with the junction diode rather than written twice — they are
        // the same physics for the same reason, and two copies would be two answers to one question.
        // See Temperature; this family's own tests are the proof the move changed no number.
        double vbiT = vbi;
        if (dT != 0.0)
        {
            if (vbi > 0)
            {
                vbiT = Temperature.JunctionPotentialAt(vbi, tempC, tnomC, bandgap);
                double scale = Temperature.DepletionCapacitanceScale(vbi, vbiT, m, dT);
                cgs *= scale;
                cgd *= scale;
            }

            if (gateSaturationCurrent > 0)
                gateSaturationCurrent *= Temperature.SaturationCurrentScale(
                    tempC, tnomC, gateEmissionCoefficient, xti, bandgap);
        }

        _capModel = capModel;
        _vbi      = vbiT > 0 ? vbiT : 1.0;
        _m        = m;
        // Fc must stay below 1 or the depletion expression divides by zero at the changeover.
        _fc       = fc is > 0 and < 0.95 ? fc : 0.5;
        _cgs    = cgs;
        _cgd    = cgd;
        _isGate = gateSaturationCurrent;
        _nGate  = gateEmissionCoefficient > 0 ? gateEmissionCoefficient : 1.0;
        _tempK  = tempC + 273.15 > 0 ? tempC + 273.15 : 300.15;
        _vt     = Boltzmann * _tempK / ElemCharge;
    }

    // Port indices with Ri: the resistor, the intrinsic gate-source, the drain-source, and Cgd's charge-only port.
    private const int PRi = 0, PGi = 1, PDs = 2, PGd = 3;

    public sealed override int       PortCount => _ri > 0 ? 4 : 2;
    public sealed override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Four entries because two ports share the source net; the elaborator builds them. Eight with <c>Ri</c>.</summary>
    public sealed override string[] TerminalNames => _ri > 0
        ? ["gate", "gi", "gi", "source", "drain", "source", "gate", "drain"]
        : ["gate", "source", "drain", "source"];

    /// <inheritdoc/>
    public sealed override IReadOnlyList<NoiseSourceDecl> NoiseSources => _noise ??= BuildNoiseSources();
    private IReadOnlyList<NoiseSourceDecl>? _noise;

    private IReadOnlyList<NoiseSourceDecl> BuildNoiseSources()
    {
        int drain = _ri > 0 ? PDs : 1;
        var list = new List<NoiseSourceDecl>();
        if (_ri > 0) list.Add(NoiseSourceDecl.OnPort("thermal_ri", PRi));
        if (double.IsFinite(_td)) list.Add(NoiseSourceDecl.OnPort("thermal_gds", drain));
        if (_kf != 0) list.Add(NoiseSourceDecl.OnPort("flicker", drain));
        return list;
    }

    /// <inheritdoc/>
    /// <remarks><paramref name="kelvin"/> is the device temperature, which <c>Tg</c> defaults to.</remarks>
    public sealed override void EvaluateNoise(in PortVoltages v, double kelvin, Span<NoiseDensity> into)
    {
        bool ri = _ri > 0;
        double vgs = _s * v[ri ? PGi : 0], vds = _s * v[ri ? PDs : 1];
        var (id, _, gds) = DrainCurrent(vgs, vds);
        int k = 0;
        if (ri) into[k++] = NoiseDensity.Thermal(1.0 / _ri, double.IsFinite(_tg) ? _tg : kelvin);
        if (double.IsFinite(_td)) into[k++] = NoiseDensity.Thermal(gds, _td);
        if (_kf != 0) into[k] = NoiseDensity.Flicker(_kf, _af, id);
    }

    /// <summary>
    /// The model's own drain-current law. Returns Id and both derivatives — analytically, because a
    /// finite-difference Jacobian inside a Newton loop costs an extra evaluation per entry and loses
    /// accuracy exactly where the device is most nonlinear.
    /// </summary>
    /// <returns>(Id, gm = ∂Id/∂Vgs, gds = ∂Id/∂Vds)</returns>
    protected abstract (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds);

    /// <summary>Forward gate conduction, as an ordinary diode from gate to source.</summary>
    private (double I, double G) GateCurrent(double vgs)
    {
        if (_isGate <= 0) return (0.0, 0.0);
        double vte = _nGate * _vt;
        double x = vgs / vte;
        if (x > 40.0)
        {
            // Tangent continuation past the exponential limit, for the same reason DiodeModel does
            // it: value and slope stay continuous, which is what keeps Newton convergent.
            double eL = System.Math.Exp(40.0);
            return (_isGate * (eL - 1.0) + _isGate * eL / vte * (vgs - 40.0 * vte),
                    _isGate * eL / vte);
        }
        double e = System.Math.Exp(x);
        return (_isGate * (e - 1.0), _isGate * e / vte);
    }

    public sealed override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>
    /// Depletion charge and its derivative for one junction. Identical in form to the diode's, and
    /// for the same reason: it is the standard formula, and above Fc·Vbi it is continued by its
    /// TANGENT so value and slope both stay continuous. A clamp there would leave a kink in the
    /// Jacobian and stall Newton.
    /// </summary>
    private (double Q, double C) JunctionCharge(double cj0, double v)
    {
        if (cj0 <= 0) return (0.0, 0.0);
        double fcvbi = _fc * _vbi;
        if (v <= fcvbi)
        {
            double u = 1.0 - v / _vbi;
            double c = cj0 * System.Math.Pow(u, -_m);
            double q = System.Math.Abs(1.0 - _m) < 1e-12
                ? -cj0 * _vbi * System.Math.Log(u)
                : cj0 * _vbi / (1.0 - _m) * (1.0 - System.Math.Pow(u, 1.0 - _m));
            return (q, c);
        }
        double u0  = 1.0 - _fc;
        double c0  = cj0 * System.Math.Pow(u0, -_m);
        double dc0 = cj0 * _m * System.Math.Pow(u0, -_m - 1.0) / _vbi;
        double q0  = System.Math.Abs(1.0 - _m) < 1e-12
            ? -cj0 * _vbi * System.Math.Log(u0)
            : cj0 * _vbi / (1.0 - _m) * (1.0 - System.Math.Pow(u0, 1.0 - _m));
        double dv = v - fcvbi;
        return (q0 + c0 * dv + 0.5 * dc0 * dv * dv, c0 + dc0 * dv);
    }

    /// <summary>
    /// The transconductance is a dependent source and circuitRF wrote it, so the passivation is
    /// exact (brief-wsprobe-6 §3; overview D-11).
    /// </summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <summary>
    /// <c>gm = ∂I_d/∂V_gs → 0</c>, and nothing else. <c>gds = ∂I_d/∂V_ds</c> stays (a two-terminal
    /// conductance), the gate diode's conductance stays, and the gate capacitances stay evaluated at
    /// bias — this family's <c>dc</c> is already symmetric (<c>dc[0,1] = dc[1,0] = −Cgd</c>), so
    /// there is no transcapacitance to remove and the symmetrisation is the identity here.
    /// </summary>
    public sealed override IReadOnlyList<(int P, int Q)> ControlledConductances => _ri > 0 ? [(PDs, PGi)] : [(1, 0)];

    public sealed override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = PortCount;
        var i = new double[n];
        var q = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    /// <inheritdoc/>
    /// <remarks>HB-P4 M4 — a closed-form law has nothing to gain from the SDD's vectorised register
    /// program, but it has the same six-arrays-per-sample allocation to lose.</remarks>
    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    /// <inheritdoc/>
    protected override bool HasEvaluateInto => true;

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        if (_ri > 0) { EvaluateWithRi(v, i, q, dg, dc); return; }

        // Into n-channel coordinates. For n-channel the sign is exactly +1, so every multiplication
        // below is the identity and the result is bit-identical to one computed before polarity
        // existed — which is what lets this family's own tests stand as the proof it changed no
        // number.
        double vgs = _s * v[0], vds = _s * v[1];
        double vgd = vgs - vds;
        var (id, gm, gds) = DrainCurrent(vgs, vds);
        var (ig, gg)      = GateCurrent(vgs);

        // Charge on the two gate junctions. Cgs sees Vgs; Cgd sees Vgd = Vgs − Vds, so it
        // contributes to BOTH ports and to the off-diagonals — omitting those cross terms is the
        // classic way to get a plausible but wrong Jacobian.
        double qgs, cgsV, qgd, cgdV;
        switch (_capModel)
        {
            case 0:                                   // no charge
                qgs = cgsV = qgd = cgdV = 0.0;
                break;
            case 2:                                   // bias-dependent junction charge
                (qgs, cgsV) = JunctionCharge(_cgs, vgs);
                (qgd, cgdV) = JunctionCharge(_cgd, vgd);
                break;
            default:                                  // constant capacitance
                qgs = _cgs * vgs; cgsV = _cgs;
                qgd = _cgd * vgd; cgdV = _cgd;
                break;
        }

        double qg = qgs + qgd;      // charge into the gate
        double qd = -qgd;           // charge into the drain

        // Back out of n-channel coordinates. The Jacobian is NOT touched: the sign appears once on
        // the current and once on the voltage, and the two cancel.
        i[0] = _s * ig;  i[1] = _s * id;
        q[0] = _s * qg;  q[1] = _s * qd;
        // Port 0 current depends on Vgs only; port 1 on both.
        dg[0, 0] = gg;  dg[0, 1] = 0.0;  dg[1, 0] = gm;   dg[1, 1] = gds;
        dc[0, 0] = cgsV + cgdV;  dc[0, 1] = -cgdV;
        dc[1, 0] = -cgdV;        dc[1, 1] = cgdV;
    }

    /// <summary>
    /// The four-port form with <c>Ri</c>: the resistor on (gate, gi); the gate diode and <c>Cgs</c> on (gi, source), whose
    /// voltage also controls the drain current; the drain current on (drain, source); <c>Cgd</c> alone on (gate, drain).
    /// Each port's charge depends only on its own voltage, so no cross capacitance appears — the coupling is the shared
    /// nodes. The resistor's current is ohmic and does not flip with polarity.
    /// </summary>
    private void EvaluateWithRi(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        Array.Clear(i); Array.Clear(q); Array.Clear(dg); Array.Clear(dc);

        double vgs = _s * v[PGi], vds = _s * v[PDs], vgd = _s * v[PGd];
        var (id, gm, gds) = DrainCurrent(vgs, vds);
        var (ig, gg)      = GateCurrent(vgs);

        double qgs, cgsV, qgd, cgdV;
        switch (_capModel)
        {
            case 0:  qgs = cgsV = qgd = cgdV = 0.0; break;
            case 2:  (qgs, cgsV) = JunctionCharge(_cgs, vgs); (qgd, cgdV) = JunctionCharge(_cgd, vgd); break;
            default: qgs = _cgs * vgs; cgsV = _cgs; qgd = _cgd * vgd; cgdV = _cgd; break;
        }

        double gr = 1.0 / _ri;
        i[PRi] = v[PRi] * gr;  dg[PRi, PRi] = gr;
        i[PGi] = _s * ig;      dg[PGi, PGi] = gg;   q[PGi] = _s * qgs;  dc[PGi, PGi] = cgsV;
        i[PDs] = _s * id;      dg[PDs, PGi] = gm;   dg[PDs, PDs] = gds;
        q[PGd] = _s * qgd;     dc[PGd, PGd] = cgdV;
    }
}
