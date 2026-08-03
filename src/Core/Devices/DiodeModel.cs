using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Junction diode — the standard exponential model, with depletion and diffusion charge and an
/// optional reverse-breakdown branch. One differential port (<c>PortCount = 1</c>, two nets),
/// V = V(anode) − V(cathode).
///
/// <para>The equations are the standard ones, in the form every SPICE-lineage simulator and every
/// device-modelling text states, and the parameter names are the conventional ones.</para>
///
/// <code>
///   I(V)  =  Is · (exp(V / (N·Vt)) − 1)                        forward / reverse
///         =  −Ibv · exp(−(Bv + V) / (N·Vt))                    below −Bv, when Bv > 0
///   Qj(V) =  Cj0·Vj/(1−M) · (1 − (1 − V/Vj)^(1−M))             V ≤ Fc·Vj   (depletion)
///   Qd(V) =  Tt · I(V)                                          (diffusion)
///   Vt    =  k·T/q
/// </code>
///
/// <para><b>Series resistance is INSIDE the model, on a real internal node</b> — placing a separate
/// resistor beside every diode is not required and would not scale to a part built from many of
/// them. With <c>Rs &gt; 0</c> the device is a two-port over three nets,
/// `anode — internal — cathode`, and the elaborator mints the internal node.</para>
///
/// <para><b>The internal node is a genuine unknown, not solved locally.</b> Collapsing it — solving
/// `(V − Vj)/Rs = I(Vj)` inside <see cref="Evaluate"/> — is exact at DC and WRONG in harmonic
/// balance, where the internal node carries its own harmonic content: at RF the junction
/// capacitance shunts <c>Rs</c>, and a quasi-static collapse cannot represent that. This is the
/// same reason `ExtDevice` internal nodes get ordinary matrix rows.</para>
/// </summary>
public sealed class DiodeModel : ComponentModel
{
    // Physical constants. Vt = kT/q.
    private const double Boltzmann   = 1.380649e-23;   // J/K
    private const double ElemCharge  = 1.602176634e-19;// C

    /// <summary>
    /// Where the exponential is replaced by its tangent. Above this the exponential is astronomically
    /// large and Newton cannot recover from a single overshoot, so the model continues linearly from
    /// the last sane point: the current stays continuous AND so does its derivative, which is what
    /// keeps the solve convergent. Standard practice, not a shortcut.
    /// </summary>
    private const double ExpArgLimit = 40.0;

    private readonly double _is, _n, _cj0, _vj, _m, _fc, _bv, _ibv, _tt, _gmin, _vt, _rs;

    /// <param name="temperatureK">
    /// Junction temperature in KELVIN; sets Vt = kT/q. Defaults to the SAME nominal the FET family
    /// uses (<see cref="Fet.FetModelBase.NominalTemperatureC"/>, 26.85 °C = 300.00 K exactly) rather
    /// than SPICE's own 27 °C — two devices in one palette must not disagree about what "nominal"
    /// means. The FACTORY takes this parameter in degrees Celsius and converts here; kelvin is used
    /// at the constructor because that is what kT/q wants.
    /// </param>
    /// <param name="minimumConductance">
    /// Defaults to ZERO, unlike SPICE. circuitRF's DC engine already adds gmin to every voltage node
    /// for continuity, so a device that added its own would double it at exactly the nodes where it
    /// matters. Non-zero here is for a caller that has a specific reason.
    /// </param>
    public DiodeModel(
        double saturationCurrent   = 1e-14,
        double emissionCoefficient = 1.0,
        double zeroBiasCapacitance = 0.0,
        double junctionPotential   = 1.0,
        double gradingCoefficient  = 0.5,
        double forwardBiasCapCoeff = 0.5,
        double breakdownVoltage    = 0.0,
        double breakdownCurrent    = 1e-3,
        double transitTime         = 0.0,
        double minimumConductance  = 0.0,
        double temperatureK        = Fet.FetModelBase.NominalTemperatureC + 273.15,
        double seriesResistance    = 0.0)
    {
        _is   = saturationCurrent;
        _n    = emissionCoefficient > 0 ? emissionCoefficient : 1.0;
        _cj0  = zeroBiasCapacitance;
        _vj   = junctionPotential > 0 ? junctionPotential : 1.0;
        _m    = gradingCoefficient;
        // Fc must stay below 1 or the depletion expression divides by zero at the changeover.
        _fc   = forwardBiasCapCoeff is > 0 and < 0.95 ? forwardBiasCapCoeff : 0.5;
        _bv   = breakdownVoltage;
        _ibv  = breakdownCurrent;
        _tt   = transitTime;
        _gmin = minimumConductance;
        _vt   = Boltzmann * (temperatureK > 0 ? temperatureK : 300.0) / ElemCharge;
        _rs   = seriesResistance > 0 ? seriesResistance : 0.0;
    }

    /// <summary>True when Rs is modelled, which is what puts the extra node and port in play.</summary>
    public bool HasSeriesResistance => _rs > 0;

    // Two ports when Rs is present: port 0 is the resistor (anode-internal), port 1 the junction
    // (internal-cathode). One port otherwise, so a diode without Rs costs no extra unknown.
    public override int       PortCount => _rs > 0 ? 2 : 1;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    public override string[] TerminalNames =>
        _rs > 0 ? ["anode", "internal", "internal", "cathode"] : ["anode", "cathode"];

    /// <summary>Conduction current and its derivative. Both are returned because every caller wants both.</summary>
    private (double I, double G) Conduction(double v)
    {
        double vte = _n * _vt;

        // Reverse breakdown. Only when Bv is given: Bv = 0 means "not modelled", not "breaks down at 0 V".
        if (_bv > 0 && v < -_bv)
        {
            double a  = -(_bv + v) / vte;                    // ≥ 0 and growing as v goes more negative
            double e  = System.Math.Exp(System.Math.Min(a, ExpArgLimit));
            double i  = -_ibv * e;
            double g  = _ibv * e / vte;
            // Beyond the limit the exponential is frozen, so its slope is too — continue linearly.
            if (a > ExpArgLimit) i -= g * (a - ExpArgLimit) * vte;
            return (i + _gmin * v, g + _gmin);
        }

        double x = v / vte;
        if (x > ExpArgLimit)
        {
            // Tangent continuation: value and slope both match at the changeover.
            double eL = System.Math.Exp(ExpArgLimit);
            double iL = _is * (eL - 1.0);
            double gL = _is * eL / vte;
            return (iL + gL * (v - ExpArgLimit * vte) + _gmin * v, gL + _gmin);
        }

        double ex = System.Math.Exp(x);
        return (_is * (ex - 1.0) + _gmin * v, _is * ex / vte + _gmin);
    }

    /// <summary>Depletion (junction) charge and its derivative, the small-signal junction capacitance.</summary>
    private (double Q, double C) Depletion(double v)
    {
        if (_cj0 <= 0) return (0.0, 0.0);

        double fcvj = _fc * _vj;
        if (v <= fcvj)
        {
            double u = 1.0 - v / _vj;
            double c = _cj0 * System.Math.Pow(u, -_m);
            // Q = ∫₀ᵛ C du, integrated in closed form. M == 1 is the logarithmic special case.
            double q = System.Math.Abs(1.0 - _m) < 1e-12
                ? -_cj0 * _vj * System.Math.Log(u)
                : _cj0 * _vj / (1.0 - _m) * (1.0 - System.Math.Pow(u, 1.0 - _m));
            return (q, c);
        }

        // Above Fc·Vj the depletion expression runs away, so it is continued by its tangent —
        // the same reason and the same treatment as the exponential above.
        double u0  = 1.0 - _fc;
        double c0  = _cj0 * System.Math.Pow(u0, -_m);
        double dc0 = _cj0 * _m * System.Math.Pow(u0, -_m - 1.0) / _vj;      // dC/dV at the changeover
        double q0  = System.Math.Abs(1.0 - _m) < 1e-12
            ? -_cj0 * _vj * System.Math.Log(u0)
            : _cj0 * _vj / (1.0 - _m) * (1.0 - System.Math.Pow(u0, 1.0 - _m));
        double dv  = v - fcvj;
        return (q0 + c0 * dv + 0.5 * dc0 * dv * dv, c0 + dc0 * dv);
    }

    // A diode conducts at DC, so it has no linear stamp — the nonlinear engines call Evaluate, and
    // the linear engines go through ComponentModel.StampLinearized, which linearises about the bias.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        if (_rs > 0)
        {
            // Port 0 — the series resistor. Linear, but it lives here rather than as a separate
            // component so that a placed diode is one device with one set of parameters.
            double vr = v[0];
            double gr = 1.0 / _rs;

            // Port 1 — the junction, on the internal node.
            double vj2 = v[1];
            var (ij, gj) = Conduction(vj2);
            var (qjj, cjj) = Depletion(vj2);
            double qj2 = qjj + _tt * ij;
            double cj2 = cjj + _tt * gj;

            // No cross terms: each port's current depends only on its own voltage. The COUPLING is
            // the shared internal node, and the engine supplies that through the node map.
            return new NonlinearResult(
                i:  [vr * gr, ij],
                q:  [0.0, qj2],
                dg: new double[2, 2] { { gr, 0.0 }, { 0.0, gj } },
                dc: new double[2, 2] { { 0.0, 0.0 }, { 0.0, cj2 } });
        }

        double vd = v[0];
        var (i, g) = Conduction(vd);
        var (qj, cj) = Depletion(vd);

        // Diffusion charge is Tt·I(V), so its capacitance is Tt·dI/dV — including the gmin term,
        // which is negligible but excluding it would make dc inconsistent with q.
        double q = qj + _tt * i;
        double c = cj + _tt * g;

        return new NonlinearResult(
            i:  [i],
            q:  [q],
            dg: new double[1, 1] { { g } },
            dc: new double[1, 1] { { c } });
    }
}
