using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Mos;

/// <summary>
/// Vertical power MOSFET. Three terminals, <c>drain gate source</c>; <see cref="Channel"/> picks
/// n- or p-channel.
///
/// <para><b>Why this is not <see cref="MosfetLevel1Model"/> with the bulk tied to the source.</b>
/// Tying the bulk would give the right topology and the wrong device. Three things a vertical power
/// MOSFET is actually chosen for are absent from the lateral model, and every one of them is what a
/// user is asking about when they reach for this part:</para>
///
/// <list type="number">
/// <item><b>The body diode is a component of the circuit, not a leakage path.</b> It is the
/// freewheeling diode of every half-bridge and the conduction path of every synchronous rectifier
/// during dead time, and it carries the full load current there. So it has its own saturation
/// current, its own reverse recovery charge (<c>Tt</c>) and its own avalanche breakdown
/// (<c>Bv</c>), and its current is reported on its own branch.</item>
/// <item><b>The gate-drain capacitance collapses with drain bias, by one to two orders of
/// magnitude.</b> That collapse IS the switching loss — the Miller plateau is the gate charge
/// pouring into it while the drain swings — and a constant overlap capacitance of either plateau
/// value gets the switching time wrong by the same one to two orders.</item>
/// <item><b>The gate resistance is in the drive path</b>, in series with a capacitance that large,
/// so it sets the switching speed as much as the drive current does.</item>
/// </list>
///
/// <para><b>There is no bulk terminal and therefore no body effect</b>, which is the honest
/// consequence of the source-to-body short being inside the silicon. A card that states
/// <c>Gamma</c>/<c>Phi</c> is stating something this device cannot use.</para>
///
/// <code>
///   Vgt = Vgs − Vto
///   Id  = 0                                            Vgt ≤ 0
///       = Beta·(Vgt − Vds/2)·Vds·(1 + Lambda·Vds)      Vds &lt; Vgt
///       = (Beta/2)·Vgt²·(1 + Lambda·Vds)               Vds ≥ Vgt
///   Ibody = Is·(exp(Vsd/(N·Vt)) − 1),  avalanching below −Bv
///   Cgd(Vgd) = Cgdmin + (Cgdmax − Cgdmin)·½·(1 + tanh(Vgd/Vgdt))
/// </code>
///
/// <para><b>Ports.</b> Four intrinsic, plus one per non-zero ohmic resistance:</para>
/// <list type="table">
/// <item><term>0</term><description>(drain', source') — the channel current, plus <c>Rds</c>.</description></item>
/// <item><term>1</term><description>(source', drain') — the body diode: current and charge.</description></item>
/// <item><term>2</term><description>(gate', source') — <c>Cgs</c>.</description></item>
/// <item><term>3</term><description>(gate', drain') — the bias-dependent <c>Cgd</c>.</description></item>
/// <item><term>4</term><description>(gate, gate') — <c>Rg</c>, present only when non-zero.</description></item>
/// <item><term>5</term><description>(drain, drain') — <c>Rd</c>.</description></item>
/// <item><term>6</term><description>(source, source') — <c>Rs</c>.</description></item>
/// </list>
///
/// <para><b>The device is symmetric in its channel</b>, like every MOS transistor: a negative
/// <c>Vds</c> swaps the two ends and the law is evaluated in the orientation it is published in.
/// That is not a formality here — it is third-quadrant conduction, which is the whole of
/// synchronous rectification, and a model that got it wrong would be wrong in the application this
/// part exists for.</para>
///
/// <para><b>What is NOT modelled, deliberately:</b> quasi-saturation (the drift region's own
/// resistance modulating with current) — it needs a second internal node and a drift-region model
/// this does not have, and <c>Rd</c> stands in for its low-current limit; subthreshold conduction;
/// flicker noise; and self-heating, which for a power device is a real omission — the junction
/// temperature is a parameter here, so a thermal model belongs around the part rather than in it.
/// A parameter this model does not read is not offered.</para>
/// </summary>
public sealed class VdmosModel : ComponentModel
{
    /// <summary>n- or p-channel, as the sign that multiplies every voltage, current and charge.</summary>
    public enum Channel { N = 1, P = -1 }

    private const int PDs   = 0;   // channel current + Rds, (drain', source')
    private const int PBody = 1;   // body diode, (source', drain')
    private const int PGs   = 2;   // Cgs, (gate', source')
    private const int PGd   = 3;   // Cgd, (gate', drain')
    private const int IntrinsicPorts = 4;

    private readonly double _s;
    private readonly double _vto, _beta, _lambda, _gds0;
    private readonly double _is, _n, _vt, _bv, _ibv, _nbv, _tt;
    private readonly double _cj0, _vj, _mj, _fc;
    private readonly double _cgs, _cgdMax, _cgdMin, _vgdt;
    private readonly double _rg, _rd, _rs;

    /// <param name="vto">
    /// Threshold, V, AS THE CARD STATES IT — negative for a p-channel enhancement device. The
    /// channel sign is applied here, so the equations below always see a positive threshold.
    /// </param>
    /// <param name="drainSourceResistance">
    /// A fixed resistance in parallel with the channel — the off-state leakage a data sheet quotes
    /// as I_DSS. Zero means it is NOT modelled, never "a short circuit".
    /// </param>
    /// <param name="gateDrainTransitionVoltage">
    /// How abruptly the gate-drain capacitance collapses, V. It is the one parameter of the
    /// transition a data sheet's reverse-transfer-capacitance curve actually fits; the two plateau
    /// values <paramref name="gateDrainCapacitanceMax"/> and <paramref name="gateDrainCapacitanceMin"/>
    /// are read straight off that curve's two ends.
    /// </param>
    /// <param name="tempC">Device temperature, °C — resolved by the factory through the one shared
    /// rule (<see cref="Temperature.ResolveDeviceC"/>).</param>
    /// <param name="tnomC">The temperature this parameter set was EXTRACTED at, °C — a property of
    /// the model card, never of the run.</param>
    public VdmosModel(
        Channel channel = Channel.N,
        double vto = 3.0, double kp = 1.0, double lambda = 0.0,
        double drainSourceResistance = 0.0,
        double bodySaturationCurrent = 1e-13, double bodyEmission = 1.0,
        double breakdownVoltage = 0.0, double breakdownCurrent = 1e-3, double breakdownEmission = 1.0,
        double transitTime = 0.0,
        double bodyZeroBiasCapacitance = 0.0, double bodyJunctionPotential = 0.8,
        double bodyGradingCoefficient = 0.5, double forwardBiasCapCoeff = 0.5,
        double gateSourceCapacitance = 0.0,
        double gateDrainCapacitanceMax = 0.0, double gateDrainCapacitanceMin = 0.0,
        double gateDrainTransitionVoltage = 1.0,
        double gateResistance = 0.0, double drainResistance = 0.0, double sourceResistance = 0.0,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double saturationTempExponent = 3.0, double bandgapAtZeroK = Temperature.SiliconBandgapEv,
        double vtoTempCoefficient = 0.0, double kpTempCoefficient = 0.0)
    {
        _s = (double)(int)channel;

        double tK  = Temperature.ToKelvin(tempC);
        double tnK = Temperature.ToKelvin(tnomC);
        double dT  = Temperature.DeltaT(tempC, tnomC);
        _vt = Temperature.ThermalVoltage(tK);

        // The threshold shifts additively in volts per degree and Kp scales in PERCENT per degree.
        // Two published forms, two different shapes — the same pair, stated the same way, as the
        // MESFET and JFET families (see FetModelBase).
        //
        // The shift is applied in the CARD's coordinates and the channel sign taken afterwards: a
        // p-channel card states Vto and Vtotc together in the p-channel convention, so signing Vto
        // first would drift a p-channel threshold the WRONG WAY and leave n-channel untouched.
        _vto = _s * (vto + vtoTempCoefficient * dT);
        double kpT = (kp > 0 ? kp : 1.0)
                   * (kpTempCoefficient == 0.0
                        // With no coefficient stated, mobility falls as T^−1.5, the published
                        // relation for this family. It is why a power MOSFET's on-resistance rises
                        // with temperature, which is what makes paralleling them work.
                        ? (dT == 0.0 ? 1.0 : System.Math.Pow(tK / tnK, -1.5))
                        : System.Math.Pow(1.01, kpTempCoefficient * dT));
        // Kp is already A/V² for the whole device on a discrete part's card — there is no W/L to
        // apply, because the geometry is the die and the card is written for that die.
        _beta   = kpT;
        _lambda = lambda;
        _gds0   = drainSourceResistance > 0 ? 1.0 / drainSourceResistance : 0.0;

        // ── The body diode ────────────────────────────────────────────────────
        _n   = bodyEmission > 0 ? bodyEmission : 1.0;
        _nbv = breakdownEmission > 0 ? breakdownEmission : 1.0;
        _is  = bodySaturationCurrent
             * Temperature.SaturationCurrentScale(tempC, tnomC, _n, saturationTempExponent, bandgapAtZeroK);
        // Bv = 0 means avalanche is NOT MODELLED, never "breaks down at 0 V" — the same rule the
        // diode's own Bv follows.
        _bv  = breakdownVoltage > 0 ? breakdownVoltage : 0.0;
        _ibv = breakdownCurrent > 0 ? breakdownCurrent : 1e-3;
        _tt  = transitTime > 0 ? transitTime : 0.0;

        double vj0 = bodyJunctionPotential > 0 ? bodyJunctionPotential : 0.8;
        double vjT = Temperature.JunctionPotentialAt(vj0, tempC, tnomC, bandgapAtZeroK);
        if (vjT <= 0) vjT = vj0;
        _vj  = vjT;
        _mj  = bodyGradingCoefficient > 0 ? bodyGradingCoefficient : 0.5;
        _fc  = JunctionMath.SanitiseFc(forwardBiasCapCoeff);
        _cj0 = bodyZeroBiasCapacitance * Temperature.DepletionCapacitanceScale(vj0, vjT, _mj, dT);

        // ── Gate capacitances ─────────────────────────────────────────────────
        _cgs    = gateSourceCapacitance > 0 ? gateSourceCapacitance : 0.0;
        _cgdMax = gateDrainCapacitanceMax > 0 ? gateDrainCapacitanceMax : 0.0;
        // Cgdmin cannot exceed Cgdmax: a card stating only one of them is stating a CONSTANT
        // gate-drain capacitance, which is what the two being equal means. Reading it the other way
        // round would make the capacitance RISE with drain bias, which is the wrong direction and
        // would still simulate.
        double cgdMin = gateDrainCapacitanceMin > 0 ? gateDrainCapacitanceMin : _cgdMax;
        _cgdMin = cgdMin <= _cgdMax ? cgdMin : _cgdMax;
        _vgdt   = gateDrainTransitionVoltage > 0 ? gateDrainTransitionVoltage : 1.0;

        _rg = gateResistance   > 0 ? gateResistance   : 0.0;
        _rd = drainResistance  > 0 ? drainResistance  : 0.0;
        _rs = sourceResistance > 0 ? sourceResistance : 0.0;
    }

    /// <summary>True for an n-channel device. The two are NOT interchangeable at a bias point.</summary>
    public bool IsNChannel => _s > 0;

    /// <summary>True when the gate resistance is modelled, which is what puts an internal node in play.</summary>
    public bool HasGateResistance => _rg > 0;

    /// <summary>True when the drain resistance is modelled.</summary>
    public bool HasDrainResistance => _rd > 0;

    /// <summary>True when the source resistance is modelled.</summary>
    public bool HasSourceResistance => _rs > 0;

    /// <summary>How many internal nets the elaborator must mint — one per non-zero ohmic resistance.</summary>
    public int InternalNodeCount =>
        (HasGateResistance ? 1 : 0) + (HasDrainResistance ? 1 : 0) + (HasSourceResistance ? 1 : 0);

    // The ohmic ports follow the four intrinsic ones in a FIXED order — gate, drain, source — and
    // only the ones that exist appear. The elaborator builds its node list from the same three
    // flags, so the two orders are one rule stated twice and must be read together.
    private int PortRg => _rg > 0 ? IntrinsicPorts : -1;
    private int PortRd => _rd > 0 ? IntrinsicPorts + (_rg > 0 ? 1 : 0) : -1;
    private int PortRs => _rs > 0 ? IntrinsicPorts + (_rg > 0 ? 1 : 0) + (_rd > 0 ? 1 : 0) : -1;

    public override int       PortCount => IntrinsicPorts + InternalNodeCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// ONE name per port — what each one CARRIES. <c>body</c> is named rather than numbered because
    /// how much current the body diode is taking during dead time is a question a user of this part
    /// asks directly, and <c>I:M1:body</c> is the answer.
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            var names = new List<string>(PortCount) { "ids", "body", "qgs", "qgd" };
            if (_rg > 0) names.Add("gate");
            if (_rd > 0) names.Add("drain");
            if (_rs > 0) names.Add("source");
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

        // Into n-channel coordinates. The sign comes back out at the end; the derivatives carry it
        // on BOTH sides and so pass through unchanged.
        double vds = _s * v[PDs];
        double vsd = _s * v[PBody];
        double vgs = _s * v[PGs];
        double vgd = _s * v[PGd];

        // ── Channel current, with the two ends swapped when Vds < 0 ───────────
        // Third-quadrant conduction is not an edge case for this part — it is synchronous
        // rectification, which is what the device is bought for.
        bool   forward = vds >= 0.0;
        int    pG  = forward ? PGs : PGd;
        double egs = forward ? vgs : vgd;
        double eds = forward ? vds : -vds;
        double sd  = forward ? 1.0 : -1.0;
        double si  = forward ? 1.0 : -1.0;

        var (id, gm, gds) = DrainCurrent(egs, eds);
        i[PDs] = si * id + _gds0 * vds;
        dg[PDs, PDs] = si * sd * gds + _gds0;          // which is gds + gds0 in both orientations
        dg[PDs, pG]  = si * gm;

        // ── The body diode ────────────────────────────────────────────────────
        // Its own port, so its current is a branch a measurement can name. Anode at the source,
        // cathode at the drain, for an n-channel device.
        var (ibody, gbody) = BodyConduction(vsd);
        var (qdep, cdep)   = JunctionMath.Depletion(vsd, _cj0, _vj, _mj, _fc);
        i[PBody] = ibody;
        dg[PBody, PBody] = gbody;
        // Diffusion charge is Tt·I, so its capacitance is Tt·dI/dV — carried together so the two
        // cannot disagree. This is the reverse-recovery charge, which for a power MOSFET's body
        // diode is usually the dominant switching loss in a hard-switched bridge.
        q[PBody] = qdep + _tt * ibody;
        dc[PBody, PBody] = cdep + _tt * gbody;

        // ── Gate charge ───────────────────────────────────────────────────────
        // Cgs is constant; Cgd is not, and its collapse with drain bias is the Miller plateau.
        q[PGs] = _cgs * vgs;
        dc[PGs, PGs] = _cgs;

        var (qgd, cgd) = GateDrainCharge(vgd);
        q[PGd] = qgd;
        dc[PGd, PGd] = cgd;

        // ── Ohmic ports ───────────────────────────────────────────────────────
        int pRg = PortRg, pRd = PortRd, pRs = PortRs;
        if (pRg >= 0) { double g = 1.0 / _rg; i[pRg] = _s * v[pRg] * g; dg[pRg, pRg] = g; }
        if (pRd >= 0) { double g = 1.0 / _rd; i[pRd] = _s * v[pRd] * g; dg[pRd, pRd] = g; }
        if (pRs >= 0) { double g = 1.0 / _rs; i[pRs] = _s * v[pRs] * g; dg[pRs, pRs] = g; }

        // Back out of n-channel coordinates. The Jacobian is NOT touched.
        if (_s < 0)
            for (int p = 0; p < P; p++) { i[p] = -i[p]; q[p] = -q[p]; }
    }

    /// <summary>
    /// The square law and its two derivatives, in n-channel coordinates and forward orientation.
    /// There is no body-effect term, because there is no bulk terminal to have one against.
    /// </summary>
    private (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double vgt = vgs - _vto;
        // Below threshold the channel is off AND its derivatives are zero. The leakage a data sheet
        // quotes is Rds, which is stamped separately and stated by the user; a fudge conductance
        // here would be a second, invented one.
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

    /// <summary>
    /// The body diode's conduction current and its derivative: the forward exponential, or the
    /// avalanche branch below <c>−Bv</c>.
    ///
    /// <para>Avalanche is a real, RATED mode for this part — an unclamped inductive switching event
    /// puts the device there deliberately — so it is modelled rather than left as an unbounded
    /// reverse blocking. It carries its OWN emission coefficient, because it is a different
    /// mechanism from forward conduction and nothing physical ties the two.</para>
    /// </summary>
    private (double I, double G) BodyConduction(double v)
    {
        if (_bv > 0 && v < -_bv)
        {
            double vteb = _nbv * _vt;
            double a = -(_bv + v) / vteb;                       // ≥ 0, growing as v goes more negative
            double e = System.Math.Exp(System.Math.Min(a, JunctionMath.ExpArgLimit));
            double ib = -_ibv * e;
            double gb = _ibv * e / vteb;
            // Past the limit the exponential is frozen, so its slope is too — continue linearly, so
            // value AND slope stay continuous and Newton can still come back.
            if (a > JunctionMath.ExpArgLimit) ib -= gb * (a - JunctionMath.ExpArgLimit) * vteb;
            return (ib, gb);
        }

        return JunctionMath.Exponential(v, _is, _n * _vt);
    }

    /// <summary>
    /// Gate-drain charge and its capacitance.
    ///
    /// <code>
    ///   Cgd(Vgd) = Cgdmin + (Cgdmax − Cgdmin)·½·(1 + tanh(Vgd/Vgdt))
    ///   Qgd(Vgd) = Cgdmin·Vgd + (Cgdmax − Cgdmin)·½·(Vgd + Vgdt·ln cosh(Vgd/Vgdt))
    /// </code>
    ///
    /// <para><b>Why a smooth switch between two plateaus, and why the charge rather than the
    /// capacitance.</b> The gate overlap of a vertical MOSFET sits over the drift region: with the
    /// gate above the drain that region is accumulated and the capacitance is the bare oxide's
    /// (<c>Cgdmax</c>); with the drain above the gate it depletes, putting a depletion capacitance
    /// in series and dropping the total by one to two orders of magnitude (<c>Cgdmin</c>). Both
    /// plateaus are read straight off a data sheet's reverse-transfer curve; only the width of the
    /// transition is fitted, and that is what <c>Vgdt</c> is.</para>
    ///
    /// <para>The CHARGE is integrated in closed form rather than the capacitance being handed to the
    /// engine, because a capacitance that depends on bias is only conservative if it comes from a
    /// potential — and around a harmonic cycle a non-conservative one does not return to where it
    /// started, which a periodic steady-state solve cannot represent. The same reason the MOS
    /// family's intrinsic gate charge is charge-based; see <see cref="MosfetModelBase"/>.</para>
    ///
    /// <para><c>ln cosh</c> is evaluated in the overflow-safe form: written directly it is
    /// <c>Infinity − Infinity</c> for <c>|Vgd/Vgdt|</c> past about 710, which a Newton iterate
    /// reaches long before the circuit does.</para>
    /// </summary>
    private (double Q, double C) GateDrainCharge(double vgd)
    {
        if (_cgdMax <= 0) return (0.0, 0.0);
        double delta = _cgdMax - _cgdMin;
        if (delta <= 0) return (_cgdMax * vgd, _cgdMax);        // stated as a constant capacitance

        double x  = vgd / _vgdt;
        double th = System.Math.Tanh(x);
        double c  = _cgdMin + delta * 0.5 * (1.0 + th);
        double q  = _cgdMin * vgd + delta * 0.5 * (vgd + _vgdt * LogCosh(x));
        return (q, c);
    }

    /// <summary>
    /// <c>ln(cosh x)</c>, without forming <c>cosh x</c>. For large <c>|x|</c> it is
    /// <c>|x| − ln 2 + ln(1 + e^(−2|x|))</c>, which stays finite everywhere a double does.
    /// </summary>
    private static double LogCosh(double x)
    {
        double a = System.Math.Abs(x);
        return a > 20.0
            ? a - System.Math.Log(2.0) + System.Math.Log(1.0 + System.Math.Exp(-2.0 * a))
            : System.Math.Log(System.Math.Cosh(x));
    }
}
