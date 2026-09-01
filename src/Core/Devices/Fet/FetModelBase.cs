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
        _vt     = Boltzmann * (tempC + 273.15 > 0 ? tempC + 273.15 : 300.15) / ElemCharge;
    }

    public sealed override int       PortCount => 2;
    public sealed override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Four entries because two ports share the source net; the elaborator builds them.</summary>
    public sealed override string[] TerminalNames => ["gate", "source", "drain", "source"];

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

    public sealed override NonlinearResult Evaluate(in PortVoltages v)
    {
        var i = new double[2];
        var q = new double[2];
        var dg = new double[2, 2];
        var dc = new double[2, 2];
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
}
