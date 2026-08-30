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
///   I(V)  =  Is · (exp(V / (N·Vt))  − 1)                       diffusion
///         +  Isr· (exp(V / (Nr·Vt)) − 1)                       recombination
///         =  −Ibv · exp(−(Bv + V) / (Nbv·Vt))                  below −Bv, when Bv > 0
///   Qj(V) =  Cj0·Vj/(1−M) · (1 − (1 − V/Vj)^(1−M))             V ≤ Fc·Vj   (depletion)
///   Qd(V) =  Tt · I(V)                                          (diffusion)
///   Vt    =  k·T/q
/// </code>
///
/// <para><b>Recombination is a SECOND exponential, not a correction to the first.</b> It has its own
/// saturation current and its own emission coefficient — conventionally near 2 where the diffusion
/// term's is near 1 — which is exactly why it dominates at low bias and is invisible at high bias.
/// Folding it into <c>Is</c> would fit one decade of the I-V curve and miss the rest.</para>
///
/// <para><b>Breakdown has its own emission coefficient too.</b> It is a different mechanism from
/// forward conduction and does not share <c>N</c>; <c>Nbv</c> defaults to 1, which is the published
/// default. Reusing <c>N</c> there — as this model did before the parameter existed — makes the
/// reverse knee follow the forward ideality, which nothing physical requires.</para>
///
/// <para><b>Area is geometry and is applied BEFORE temperature.</b> It scales the currents and the
/// capacitance up and the series resistance down, all by construction of what "m² of the same
/// junction" means. The order matters only for readability here, since the two are independent
/// multipliers — but stating it keeps the next parameter from being added in the wrong place.</para>
///
/// <para><b>The junction temperature relations are SHARED with the FET family</b>
/// (<see cref="Temperature"/>), not written twice: same physics, same question, one answer. With
/// <c>Temp == Tnom</c> every one of them collapses to the identity, so a diode that states no
/// temperature is bit-identical to one with no temperature model at all.</para>
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
    private readonly double _isr, _nr, _nbv;

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
    /// <param name="nominalTemperatureK">
    /// The temperature this parameter set was EXTRACTED at, in kelvin — a property of the model card,
    /// never of the run. Ambient must not move it: move both together and ΔT is zero at every
    /// temperature, every relation collapses to the identity, and the device still looks
    /// temperature-aware.
    /// </param>
    /// <param name="area">
    /// Junction area, as a multiple of the area the parameters were extracted for. Dimensionless by
    /// convention — the parameters are per unit of whatever the card's own unit is, and circuitRF
    /// does not need to know which.
    /// </param>
    public DiodeModel(
        double saturationCurrent      = 1e-14,
        double emissionCoefficient    = 1.0,
        double zeroBiasCapacitance    = 0.0,
        double junctionPotential      = 1.0,
        double gradingCoefficient     = 0.5,
        double forwardBiasCapCoeff    = 0.5,
        double breakdownVoltage       = 0.0,
        double breakdownCurrent       = 1e-3,
        double transitTime            = 0.0,
        double minimumConductance     = 0.0,
        double temperatureK           = Temperature.NominalK,
        double seriesResistance       = 0.0,
        double recombinationCurrent   = 0.0,
        double recombinationEmission  = 2.0,
        double breakdownEmission      = 1.0,
        double area                   = 1.0,
        double nominalTemperatureK    = Temperature.NominalK,
        double saturationTempExponent = 3.0,
        double bandgapAtZeroK         = Temperature.SiliconBandgapEv)
    {
        double a = area > 0 ? area : 1.0;

        double tK  = temperatureK        > 0 ? temperatureK        : Temperature.NominalK;
        double tnK = nominalTemperatureK > 0 ? nominalTemperatureK : Temperature.NominalK;
        double tempC = Temperature.ToCelsius(tK), tnomC = Temperature.ToCelsius(tnK);
        double dT    = tK - tnK;                       // scale-free: the same number in K or °C

        _n    = emissionCoefficient   > 0 ? emissionCoefficient   : 1.0;
        _nr   = recombinationEmission > 0 ? recombinationEmission : 2.0;
        _nbv  = breakdownEmission     > 0 ? breakdownEmission     : 1.0;

        // Geometry, then physics. Both are plain multipliers, so the order is presentational — but
        // the temperature relations are written in terms of the CARD's parameters, and reading them
        // as though they applied to an area-scaled value is the mistake this ordering forecloses.
        _is  = saturationCurrent    * a * Temperature.SaturationCurrentScale(tempC, tnomC, _n,  saturationTempExponent, bandgapAtZeroK);
        _isr = recombinationCurrent * a * Temperature.SaturationCurrentScale(tempC, tnomC, _nr, saturationTempExponent, bandgapAtZeroK);
        _ibv = breakdownCurrent     * a;

        double vj0 = junctionPotential > 0 ? junctionPotential : 1.0;
        double vjT = Temperature.JunctionPotentialAt(vj0, tempC, tnomC, bandgapAtZeroK);
        // A junction potential driven to or past zero says nothing physical — it is the relation
        // leaving its range, not the device. Fall back to the card's own value BEFORE it is used,
        // so the capacitance scale is not computed from it either.
        if (vjT <= 0) vjT = vj0;

        _vj  = vjT;
        _m   = gradingCoefficient;
        _cj0 = zeroBiasCapacitance * a * Temperature.DepletionCapacitanceScale(vj0, vjT, _m, dT);

        // Fc must stay below 1 or the depletion expression divides by zero at the changeover.
        _fc   = forwardBiasCapCoeff is > 0 and < 0.95 ? forwardBiasCapCoeff : 0.5;
        _bv   = breakdownVoltage;
        _tt   = transitTime;
        _gmin = minimumConductance;
        _vt   = Temperature.ThermalVoltage(tK);
        // m junctions in parallel each carry their own series resistance, so the pair is in parallel.
        _rs   = seriesResistance > 0 ? seriesResistance / a : 0.0;
    }

    /// <summary>True when Rs is modelled, which is what puts the extra node and port in play.</summary>
    public bool HasSeriesResistance => _rs > 0;

    // Two ports when Rs is present: port 0 is the resistor (anode-internal), port 1 the junction
    // (internal-cathode). One port otherwise, so a diode without Rs costs no extra unknown.
    public override int       PortCount => _rs > 0 ? 2 : 1;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    public override string[] TerminalNames =>
        _rs > 0 ? ["anode", "internal", "internal", "cathode"] : ["anode", "cathode"];

    /// <summary>
    /// One saturation-current exponential and its derivative, continued by its TANGENT above the
    /// argument limit — value and slope both stay continuous, which is what keeps Newton convergent.
    /// A clamp keeps the value finite and puts a kink in the Jacobian, which stalls the solve in a
    /// way that looks like a bad circuit.
    /// </summary>
    private static (double I, double G) Exponential(double v, double isat, double vte)
    {
        if (isat <= 0) return (0.0, 0.0);

        double x = v / vte;
        if (x > ExpArgLimit)
        {
            double eL = System.Math.Exp(ExpArgLimit);
            double iL = isat * (eL - 1.0);
            double gL = isat * eL / vte;
            return (iL + gL * (v - ExpArgLimit * vte), gL);
        }

        double ex = System.Math.Exp(x);
        return (isat * (ex - 1.0), isat * ex / vte);
    }

    /// <summary>Conduction current and its derivative. Both are returned because every caller wants both.</summary>
    private (double I, double G) Conduction(double v)
    {
        // Reverse breakdown. Only when Bv is given: Bv = 0 means "not modelled", not "breaks down at
        // 0 V". It carries its OWN emission coefficient — a different mechanism from forward
        // conduction, which is why it does not share N.
        if (_bv > 0 && v < -_bv)
        {
            double vteb = _nbv * _vt;
            double a  = -(_bv + v) / vteb;                   // ≥ 0 and growing as v goes more negative
            double e  = System.Math.Exp(System.Math.Min(a, ExpArgLimit));
            double i  = -_ibv * e;
            double g  = _ibv * e / vteb;
            // Beyond the limit the exponential is frozen, so its slope is too — continue linearly.
            if (a > ExpArgLimit) i -= g * (a - ExpArgLimit) * vteb;
            return (i + _gmin * v, g + _gmin);
        }

        // Diffusion plus recombination: two independent exponentials with their own ideality
        // factors, which is what lets one dominate at low bias and the other at high.
        var (id, gd) = Exponential(v, _is,  _n  * _vt);
        var (ir, gr) = Exponential(v, _isr, _nr * _vt);

        return (id + ir + _gmin * v, gd + gr + _gmin);
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
        int P = PortCount;
        var i = new double[P];
        var q = new double[P];
        var dg = new double[P, P];
        var dc = new double[P, P];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    /// <inheritdoc/>
    /// <remarks>HB-P4 M4 — see <see cref="ComponentModel.EvaluateInto"/>.</remarks>
    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    /// <inheritdoc/>
    protected override bool HasEvaluateInto => true;

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
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
            i[0] = vr * gr;  i[1] = ij;
            q[0] = 0.0;      q[1] = qj2;
            dg[0, 0] = gr;   dg[0, 1] = 0.0;  dg[1, 0] = 0.0;  dg[1, 1] = gj;
            dc[0, 0] = 0.0;  dc[0, 1] = 0.0;  dc[1, 0] = 0.0;  dc[1, 1] = cj2;
            return;
        }

        double vd = v[0];
        var (id, g) = Conduction(vd);
        var (qj, cj) = Depletion(vd);

        // Diffusion charge is Tt·I(V), so its capacitance is Tt·dI/dV — including the gmin term,
        // which is negligible but excluding it would make dc inconsistent with q.
        double qd = qj + _tt * id;
        double c = cj + _tt * g;

        i[0] = id;
        q[0] = qd;
        dg[0, 0] = g;
        dc[0, 0] = c;
    }
}
