using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Igbt;

/// <summary>
/// Insulated-gate bipolar transistor — the equivalent-circuit model: an insulated-gate channel
/// driving the base of a wide-base bipolar transistor. Three terminals,
/// <c>collector gate emitter</c>; <see cref="Polarity"/> picks the n-channel device (the ordinary
/// one) or its complement.
///
/// <para><b>An IGBT is that circuit, and modelling it as that circuit is what gets its two defining
/// behaviours right.</b> Both come out of the structure rather than being fitted:</para>
///
/// <list type="number">
/// <item><b>The on-state voltage has a JUNCTION DROP in it.</b> The current leaving the collector
/// crosses the bipolar's emitter-base junction on its way in, so <c>Vce(sat)</c> never falls below
/// roughly a diode drop however hard the gate is driven. That is the whole trade against a power
/// MOSFET — worse at low current, better at high, because the drop then stops growing — and a
/// model that did not put the junction in series would get the crossover wrong in both
/// directions.</item>
/// <item><b>Turn-off has a CURRENT TAIL.</b> The charge stored in the wide base cannot be removed
/// through the gate; it recombines. So the collector current falls quickly to the bipolar's share
/// and then decays, and that tail is most of the turn-off loss. It is carried here as the
/// bipolar's diffusion charge, <c>Tau·I</c>, exactly as the diode's and the BJT's is.</item>
/// </list>
///
/// <para><b>It does NOT conduct in reverse</b>, and that is structural rather than something
/// switched off: with the collector below the emitter the bipolar's junction is reverse-biased and
/// there is no path. This is the opposite of <see cref="Mos.VdmosModel"/>, whose body diode
/// freewheels — which is exactly why an IGBT half-bridge needs a discrete anti-parallel diode and a
/// MOSFET one does not. Place one if the circuit has one.</para>
///
/// <para><b>Ports.</b> Five intrinsic, plus one per non-zero ohmic resistance. <c>base</c> is the
/// internal node between the channel and the bipolar — the collector of the one and the base of the
/// other — and the elaborator mints it:</para>
/// <list type="table">
/// <item><term>0</term><description>(base, emitter') — the channel current, plus <c>Rbe</c>.</description></item>
/// <item><term>1</term><description>(collector', base) — the bipolar's base current: junction, charge, breakdown.</description></item>
/// <item><term>2</term><description>(gate', emitter') — <c>Cge</c>.</description></item>
/// <item><term>3</term><description>(gate', base) — the bias-dependent Miller capacitance.</description></item>
/// <item><term>4</term><description>(collector', emitter') — the bipolar's transport current, plus <c>Rce</c>.</description></item>
/// <item><term>5,6,7</term><description>(gate, gate'), (collector, collector'), (emitter, emitter') — <c>Rg</c>, <c>Rc</c>, <c>Re</c>.</description></item>
/// </list>
///
/// <para><b>The internal base node is a genuine unknown, not solved locally.</b> That is what makes
/// the on-state voltage come out right: the solver finds the base voltage at which the channel
/// current equals the bipolar's base current, and the collector-emitter drop is then the channel's
/// drop plus the junction's, which is the physics rather than a fitted offset. Eliminating it would
/// be exact at DC and wrong in harmonic balance, where it carries its own harmonic content.</para>
///
/// <para><b>What is NOT modelled, deliberately.</b> This is an EQUIVALENT-CIRCUIT model, not the
/// published ambipolar-transport one: the base is a lumped transit time rather than a solved carrier
/// distribution, so there is no moving depletion boundary, no conductivity modulation of the drift
/// region and no latch-up. Its parameters are therefore threshold, transconductance, current gain
/// and transit time — quantities read off a data sheet — and NOT the transport model's, which
/// describe the silicon. The two parameter sets do not map onto one another, which is why a model
/// card written for the transport model cannot be imported into this and is refused by name rather
/// than being given this device's defaults. Also absent: self-heating, and reverse blocking beyond
/// the stated breakdown.</para>
/// </summary>
public sealed class IgbtModel : ComponentModel
{
    /// <summary>The ordinary n-channel device, or its complement, as a sign.</summary>
    public enum Polarity { NChannel = 1, PChannel = -1 }

    private const int PMos  = 0;   // channel current + Rbe, (base, emitter')
    private const int PBase = 1;   // the bipolar's base current, (collector', base)
    private const int PGe   = 2;   // Cge, (gate', emitter')
    private const int PGc   = 3;   // Miller capacitance, (gate', base)
    private const int PCe   = 4;   // the bipolar's transport current + Rce, (collector', emitter')
    private const int IntrinsicPorts = 5;

    private readonly double _s;
    private readonly double _vto, _beta, _lambda, _gbe, _gce;
    private readonly double _is, _n, _vt, _alpha, _beta1;    // _alpha = b/(1+b), _beta1 = 1/(1+b)
    private readonly double _bv, _ibv, _nbv, _tau;
    private readonly double _cj0, _vj, _mj, _fc;
    private readonly double _cge, _cgcMax, _cgcMin, _vgct;
    private readonly double _rg, _rc, _re;

    /// <param name="vto">
    /// Channel threshold, V, AS THE CARD OR DATA SHEET STATES IT. The polarity sign is applied here.
    /// </param>
    /// <param name="bipolarGain">
    /// The wide-base bipolar's current gain. LOW by construction — a fraction to a few, not the
    /// hundreds a small-signal transistor has — because the base is deliberately wide. It sets how
    /// the collector current divides between the channel and the bipolar, and therefore how much of
    /// the turn-off current is in the tail.
    /// </param>
    /// <param name="baseTransitTime">
    /// The bipolar's transit time. This is what produces the current TAIL: it is the stored base
    /// charge, and turn-off cannot remove it through the gate.
    /// </param>
    /// <param name="baseEmitterResistance">
    /// The body-region shunt across the bipolar's base-emitter, which in real silicon is what stops
    /// the parasitic thyristor latching. Zero means NOT MODELLED, and is the ordinary case.
    /// </param>
    /// <param name="tempC">Device temperature, °C — resolved by the factory through the one shared
    /// rule (<see cref="Temperature.ResolveDeviceC"/>).</param>
    /// <param name="tnomC">The temperature this parameter set was EXTRACTED at, °C — a property of
    /// the parameter set, never of the run.</param>
    public IgbtModel(
        Polarity polarity = Polarity.NChannel,
        double vto = 5.0, double kp = 8.0, double lambda = 0.0,
        double bipolarGain = 0.5,
        double baseSaturationCurrent = 1e-12, double baseEmission = 1.0,
        double baseTransitTime = 1e-6,
        double baseEmitterResistance = 0.0, double collectorEmitterResistance = 0.0,
        double breakdownVoltage = 0.0, double breakdownCurrent = 1e-3, double breakdownEmission = 1.0,
        double junctionCapacitance = 0.0, double junctionPotential = 0.8,
        double gradingCoefficient = 0.5, double forwardBiasCapCoeff = 0.5,
        double gateEmitterCapacitance = 0.0,
        double millerCapacitanceMax = 0.0, double millerCapacitanceMin = 0.0,
        double millerTransitionVoltage = 1.0,
        double gateResistance = 0.0, double collectorResistance = 0.0, double emitterResistance = 0.0,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double saturationTempExponent = 3.0, double bandgapAtZeroK = Temperature.SiliconBandgapEv,
        double vtoTempCoefficient = 0.0, double kpTempCoefficient = 0.0)
    {
        _s = (double)(int)polarity;

        double tK  = Temperature.ToKelvin(tempC);
        double tnK = Temperature.ToKelvin(tnomC);
        double dT  = Temperature.DeltaT(tempC, tnomC);
        _vt = Temperature.ThermalVoltage(tK);

        // Threshold shifts additively in volts per degree; Kp scales in PERCENT per degree, or —
        // with no coefficient stated — follows the T^-1.5 mobility relation. The same pair, stated
        // the same way, as every other family here — including the ORDER: the shift is applied in
        // the card's own coordinates and the polarity sign taken afterwards, because a complementary
        // card states Vto and Vtotc together in its own convention.
        _vto = _s * (vto + vtoTempCoefficient * dT);
        _beta = (kp > 0 ? kp : 1.0)
              * (kpTempCoefficient == 0.0
                    ? (dT == 0.0 ? 1.0 : System.Math.Pow(tK / tnK, -1.5))
                    : System.Math.Pow(1.01, kpTempCoefficient * dT));
        _lambda = lambda;

        // ── The bipolar ───────────────────────────────────────────────────────
        // Its gain divides the emitter current between the base (which the channel must supply) and
        // the transport path. Written as the two fractions once here rather than as a gain in the
        // inner loop, so the two cannot disagree about which is which.
        double b = bipolarGain >= 0 ? bipolarGain : 0.0;
        _alpha = b / (1.0 + b);          // the transport share
        _beta1 = 1.0 / (1.0 + b);        // the base share

        _n  = baseEmission > 0 ? baseEmission : 1.0;
        _is = baseSaturationCurrent
            * Temperature.SaturationCurrentScale(tempC, tnomC, _n, saturationTempExponent, bandgapAtZeroK);
        _tau = baseTransitTime > 0 ? baseTransitTime : 0.0;

        // Bv = 0 means breakdown is NOT MODELLED, never "breaks down at 0 V".
        _nbv = breakdownEmission > 0 ? breakdownEmission : 1.0;
        _bv  = breakdownVoltage > 0 ? breakdownVoltage : 0.0;
        _ibv = breakdownCurrent > 0 ? breakdownCurrent : 1e-3;

        double vj0 = junctionPotential > 0 ? junctionPotential : 0.8;
        double vjT = Temperature.JunctionPotentialAt(vj0, tempC, tnomC, bandgapAtZeroK);
        if (vjT <= 0) vjT = vj0;
        _vj  = vjT;
        _mj  = gradingCoefficient > 0 ? gradingCoefficient : 0.5;
        _fc  = JunctionMath.SanitiseFc(forwardBiasCapCoeff);
        _cj0 = junctionCapacitance * Temperature.DepletionCapacitanceScale(vj0, vjT, _mj, dT);

        // ── Gate capacitances ─────────────────────────────────────────────────
        _cge    = gateEmitterCapacitance > 0 ? gateEmitterCapacitance : 0.0;
        _cgcMax = millerCapacitanceMax > 0 ? millerCapacitanceMax : 0.0;
        // The minimum cannot exceed the maximum: a parameter set stating only one of them is
        // stating a CONSTANT Miller capacitance, which is what the two being equal means. Taking it
        // literally would make the capacitance RISE with collector bias — the wrong direction, and
        // it would still simulate.
        double cgcMin = millerCapacitanceMin > 0 ? millerCapacitanceMin : _cgcMax;
        _cgcMin = cgcMin <= _cgcMax ? cgcMin : _cgcMax;
        _vgct   = millerTransitionVoltage > 0 ? millerTransitionVoltage : 1.0;

        // Zero means NOT MODELLED for both shunts, never a short.
        _gbe = baseEmitterResistance      > 0 ? 1.0 / baseEmitterResistance      : 0.0;
        _gce = collectorEmitterResistance > 0 ? 1.0 / collectorEmitterResistance : 0.0;

        _rg = gateResistance      > 0 ? gateResistance      : 0.0;
        _rc = collectorResistance > 0 ? collectorResistance : 0.0;
        _re = emitterResistance   > 0 ? emitterResistance   : 0.0;
    }

    /// <summary>True for the ordinary n-channel device.</summary>
    public bool IsNChannel => _s > 0;

    /// <summary>True when the gate resistance is modelled, which is what puts an internal node in play.</summary>
    public bool HasGateResistance => _rg > 0;

    /// <summary>True when the collector resistance is modelled.</summary>
    public bool HasCollectorResistance => _rc > 0;

    /// <summary>True when the emitter resistance is modelled.</summary>
    public bool HasEmitterResistance => _re > 0;

    /// <summary>
    /// How many internal nets the elaborator must mint. <b>Always at least one</b> — the base node
    /// between the channel and the bipolar is what this model IS, so it exists whatever the
    /// parasitics do — plus one per non-zero ohmic resistance.
    /// </summary>
    public int InternalNodeCount => 1
        + (HasGateResistance ? 1 : 0) + (HasCollectorResistance ? 1 : 0) + (HasEmitterResistance ? 1 : 0);

    // The ohmic ports follow the five intrinsic ones in a FIXED order — gate, collector, emitter —
    // and only the ones that exist appear. The elaborator builds its node list from the same three
    // flags, so the two orders are one rule stated twice and must be read together.
    private int PortRg => _rg > 0 ? IntrinsicPorts : -1;
    private int PortRc => _rc > 0 ? IntrinsicPorts + (_rg > 0 ? 1 : 0) : -1;
    private int PortRe => _re > 0 ? IntrinsicPorts + (_rg > 0 ? 1 : 0) + (_rc > 0 ? 1 : 0) : -1;

    public override int       PortCount => IntrinsicPorts
        + (HasGateResistance ? 1 : 0) + (HasCollectorResistance ? 1 : 0) + (HasEmitterResistance ? 1 : 0);
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// ONE name per port — what each one CARRIES. <c>ib</c> and <c>ic</c> are named because how the
    /// current divides between the channel and the bipolar is the question this device's user asks:
    /// the bipolar's share is what will still be flowing after the gate is off.
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            var names = new List<string>(PortCount) { "imos", "ib", "qge", "qgc", "ic" };
            if (_rg > 0) names.Add("gate");
            if (_rc > 0) names.Add("collector");
            if (_re > 0) names.Add("emitter");
            return names.ToArray();
        }
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int P = PortCount;
        var i  = new double[P];
        var q  = new double[P];
        var dg = new double[P, P];
        var dc = new double[P, P];
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
        int P = PortCount;
        Array.Clear(i);
        Array.Clear(q);
        Array.Clear(dg);
        Array.Clear(dc);

        // Into n-channel coordinates; the sign comes back out at the end and the derivatives carry
        // it on both sides, so they pass through unchanged.
        double vbe = _s * v[PMos];    // internal base to emitter — the channel's own drain voltage
        double veb = _s * v[PBase];   // collector to internal base — the bipolar's junction
        double vge = _s * v[PGe];
        double vgb = _s * v[PGc];
        double vce = _s * v[PCe];

        // ── The insulated-gate channel ────────────────────────────────────────
        // Its drain is the internal base node, so it is driven by Vge and loaded by Vbe. The
        // drain/source swap is the same rule every MOS device here follows: which end acts as the
        // drain is decided by the bias.
        bool   forward = vbe >= 0.0;
        int    pG  = forward ? PGe : PGc;      // Vge in forward, Vgb in reverse
        double egs = forward ? vge : vgb;
        double eds = forward ? vbe : -vbe;
        double sd  = forward ? 1.0 : -1.0;
        double si  = forward ? 1.0 : -1.0;

        var (imos, gm, gds) = ChannelCurrent(egs, eds);
        // Forward blocking is sustained across the DRIFT REGION, which is this span — so that is
        // where the device breaks over, not at the bipolar's own junction. Once it does, the current
        // flows base to emitter and turns the bipolar on with it, which the shared internal node
        // takes care of on its own.
        var (iav, gav) = Avalanche(vbe);
        i[PMos] = si * imos + _gbe * vbe + iav;
        dg[PMos, PMos] = si * sd * gds + _gbe + gav;
        dg[PMos, pG]   = si * gm;

        // ── The bipolar ───────────────────────────────────────────────────────
        // One junction, one current, divided into the base share and the transport share by the
        // gain. The BASE share is what the channel has to supply, which is why the two are coupled
        // through the internal node rather than through an expression here.
        var (ie, ge) = JunctionCurrent(veb);
        i[PBase] = _beta1 * ie;
        dg[PBase, PBase] = _beta1 * ge;

        i[PCe] = _alpha * ie + _gce * vce;
        // The transport current depends on the JUNCTION voltage, not on the collector-emitter
        // voltage — so its Jacobian entry is an off-diagonal. Omitting it is the classic way to get
        // a plausible Jacobian that will not converge, and it is the whole of the device's gain.
        dg[PCe, PBase] = _alpha * ge;
        dg[PCe, PCe]   = _gce;

        // The junction's stored charge: depletion, plus the DIFFUSION charge Tau·I, which is the
        // stored base charge and therefore the current tail. Carried with its own capacitance so
        // the two cannot disagree.
        var (qdep, cdep) = JunctionMath.Depletion(veb, _cj0, _vj, _mj, _fc);
        q[PBase] = qdep + _tau * ie;
        dc[PBase, PBase] = cdep + _tau * ge;

        // ── Gate charge ───────────────────────────────────────────────────────
        q[PGe] = _cge * vge;
        dc[PGe, PGe] = _cge;

        var (qgc, cgc) = MillerCharge(vgb);
        q[PGc] = qgc;
        dc[PGc, PGc] = cgc;

        // ── Ohmic ports ───────────────────────────────────────────────────────
        int pRg = PortRg, pRc = PortRc, pRe = PortRe;
        if (pRg >= 0) { double g = 1.0 / _rg; i[pRg] = _s * v[pRg] * g; dg[pRg, pRg] = g; }
        if (pRc >= 0) { double g = 1.0 / _rc; i[pRc] = _s * v[pRc] * g; dg[pRc, pRc] = g; }
        if (pRe >= 0) { double g = 1.0 / _re; i[pRe] = _s * v[pRe] * g; dg[pRe, pRe] = g; }

        if (_s < 0)
            for (int p = 0; p < P; p++) { i[p] = -i[p]; q[p] = -q[p]; }
    }

    /// <summary>
    /// The channel's square law and its two derivatives, in n-channel coordinates and forward
    /// orientation. Its drain is the internal base node.
    /// </summary>
    private (double Id, double Gm, double Gds) ChannelCurrent(double vgs, double vds)
    {
        double vgt = vgs - _vto;
        if (vgt <= 0.0) return (0.0, 0.0, 0.0);

        double lam = 1.0 + _lambda * vds;
        if (vds < vgt)
        {
            double core = (vgt - 0.5 * vds) * vds;
            return (_beta * core * lam,
                    _beta * vds * lam,
                    _beta * ((vgt - vds) * lam + core * _lambda));
        }
        double sat = 0.5 * vgt * vgt;
        return (_beta * sat * lam, _beta * vgt * lam, _beta * sat * _lambda);
    }

    /// <summary>The bipolar's emitter-base junction — a plain forward exponential.</summary>
    private (double I, double G) JunctionCurrent(double v)
        => JunctionMath.Exponential(v, _is, _n * _vt);

    /// <summary>
    /// Forward break-over, across the drift region. Nothing at all below <c>Bv</c>, then an
    /// exponential rise — the device's <c>V_CES</c> rating, which is a limit rather than an
    /// operating mode.
    ///
    /// <para>It carries its OWN emission coefficient, because avalanche is a different mechanism
    /// from conduction and nothing physical ties the two. <c>Bv = 0</c> means NOT MODELLED, never
    /// "breaks over at 0 V". The exponential is continued by its tangent past the argument limit,
    /// for the reason every other one in this directory is.</para>
    /// </summary>
    private (double I, double G) Avalanche(double vbe)
    {
        if (_bv <= 0 || vbe <= _bv) return (0.0, 0.0);

        double vteb = _nbv * _vt;
        double a = (vbe - _bv) / vteb;
        double e = System.Math.Exp(System.Math.Min(a, JunctionMath.ExpArgLimit));
        double iav = _ibv * e;
        double gav = _ibv * e / vteb;
        if (a > JunctionMath.ExpArgLimit) iav += gav * (a - JunctionMath.ExpArgLimit) * vteb;
        return (iav, gav);
    }

    /// <summary>
    /// The Miller (gate-to-internal-base) charge and its capacitance, on the same smooth switch
    /// between two plateaus that <see cref="Mos.VdmosModel"/> uses, and integrated in closed form
    /// for the same reason: a bias-dependent capacitance is only conservative if it comes from a
    /// potential, and around a harmonic cycle a non-conservative one does not return to where it
    /// started.
    ///
    /// <code>
    ///   Cgc(V) = Cgcmin + (Cgcmax − Cgcmin)·½·(1 + tanh(V/Vgct))
    ///   Qgc(V) = Cgcmin·V + (Cgcmax − Cgcmin)·½·(V + Vgct·ln cosh(V/Vgct))
    /// </code>
    /// </summary>
    private (double Q, double C) MillerCharge(double vgb)
    {
        if (_cgcMax <= 0) return (0.0, 0.0);
        double delta = _cgcMax - _cgcMin;
        if (delta <= 0) return (_cgcMax * vgb, _cgcMax);       // stated as a constant capacitance

        double x  = vgb / _vgct;
        double c  = _cgcMin + delta * 0.5 * (1.0 + System.Math.Tanh(x));
        double qq = _cgcMin * vgb + delta * 0.5 * (vgb + _vgct * LogCosh(x));
        return (qq, c);
    }

    /// <summary>
    /// <c>ln(cosh x)</c>, without forming <c>cosh x</c> — which is <c>Infinity</c> past about 710,
    /// a value a Newton iterate reaches long before the circuit does.
    /// </summary>
    private static double LogCosh(double x)
    {
        double a = System.Math.Abs(x);
        return a > 20.0
            ? a - System.Math.Log(2.0) + System.Math.Log(1.0 + System.Math.Exp(-2.0 * a))
            : System.Math.Log(System.Math.Cosh(x));
    }
}
