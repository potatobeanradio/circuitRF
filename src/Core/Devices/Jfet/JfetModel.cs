using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Jfet;

/// <summary>
/// Junction field-effect transistor — the Shichman-Hodges square law, in the form every
/// device-modelling text and every SPICE-lineage simulator states it, with the conventional
/// parameter names. Three terminals, <c>drain gate source</c>; <see cref="Polarity"/> picks
/// n-channel or p-channel.
///
/// <code>
///   Vgt = Vgs − Vto
///   Id  = 0                                              Vgt ≤ 0        cutoff
///       = Beta·Vds·(2·Vgt − Vds)·(1 + Lambda·Vds)        Vds &lt; Vgt     linear
///       = Beta·Vgt²·(1 + Lambda·Vds)                     Vds ≥ Vgt      saturation
///   Ig  = Is·(exp(Vgs/(N·Vt)) − 1) + Is·(exp(Vgd/(N·Vt)) − 1)
///   Qg  = Qj(Vgs; Cgs,Pb,M) + Qj(Vgd; Cgd,Pb,M)
/// </code>
///
/// <para><b>This is NOT the MESFET family with different coefficients, which is why it is its own
/// component.</b> The two laws differ in the knee — the MESFET's is a <c>tanh</c> or a piecewise
/// cubic with its own fitted parameter, the JFET's is the square law's own boundary at
/// <c>Vds = Vgt</c> — and the JFET's gate is a real p-n junction that conducts and stores depletion
/// charge in both directions, where the MESFET's is a Schottky gate the family models as one
/// forward diode. Reading a JFET card as a Curtice quadratic with the <c>tanh</c> ignored gives a
/// device that simulates and is wrong, which is exactly what the import layer used to refuse.</para>
///
/// <para><b>The gate is TWO junctions, not one.</b> A JFET's gate sits between the drain and the
/// source and is reverse-biased against both; the depletion region that pinches the channel off is
/// the gate-source and gate-drain junctions together. Modelling only the gate-source half would
/// leave the feedback capacitance at zero, which at RF is the whole of the reverse isolation.</para>
///
/// <para><b>The device is symmetric.</b> Which terminal is the drain is decided by the bias, not by
/// the schematic, so a negative <c>Vds</c> swaps the two ends and the law is evaluated in the
/// orientation it is published in. Same rule, same reason, as <see cref="Mos.MosfetModelBase"/>.</para>
///
/// <para><b>Ports.</b> Three intrinsic, plus one per non-zero ohmic resistance:</para>
/// <list type="table">
/// <item><term>0</term><description>(drain', source') — the channel current. No charge.</description></item>
/// <item><term>1</term><description>(gate, source') — the gate-source junction.</description></item>
/// <item><term>2</term><description>(gate, drain') — the gate-drain junction.</description></item>
/// <item><term>3</term><description>(drain, drain') — <c>Rd</c>, present only when non-zero.</description></item>
/// <item><term>4</term><description>(source, source') — <c>Rs</c>, present only when non-zero.</description></item>
/// </list>
///
/// <para><b>What is NOT modelled, deliberately:</b> gate breakdown, the doping-profile knee some
/// published JFET levels add (its <c>B</c> parameter), transit-time charge, flicker noise and
/// self-heating. A parameter this model does not read is not offered.</para>
/// </summary>
public sealed class JfetModel : ComponentModel
{
    /// <summary>n-channel or p-channel, as the sign that multiplies every voltage, current and charge.</summary>
    public enum Polarity { NChannel = 1, PChannel = -1 }

    private const int PDs = 0;   // channel current, (drain', source')
    private const int PGs = 1;   // gate-source junction
    private const int PGd = 2;   // gate-drain junction
    private const int IntrinsicPorts = 3;

    private readonly double _s;
    private readonly double _vto, _beta, _lambda;
    private readonly double _is, _isr, _n, _nr, _vt;
    private readonly double _cgs, _cgd, _pb, _m, _fc;
    private readonly double _rd, _rs;

    /// <param name="polarity">n- or p-channel. Every internal voltage, current and charge is
    /// multiplied by its sign, so one set of equations serves both and the two cannot drift apart.</param>
    /// <param name="vto">
    /// Pinch-off voltage, V, AS THE CARD STATES IT — negative for an ordinary n-channel depletion
    /// JFET and positive for a p-channel one. The channel sign is applied here, so the equations
    /// below always see the n-channel convention.
    /// </param>
    /// <param name="area">
    /// Gate area, as a multiple of the area the parameters were extracted for. Dimensionless by
    /// convention. Currents and capacitances scale up with it, the ohmic resistances down.
    /// </param>
    /// <param name="tempC">Device temperature, °C — resolved by the factory through the one shared
    /// rule (<see cref="Temperature.ResolveDeviceC"/>).</param>
    /// <param name="tnomC">The temperature this parameter set was EXTRACTED at, °C — a property of
    /// the model card, never of the run.</param>
    public JfetModel(
        Polarity polarity      = Polarity.NChannel,
        double vto             = -2.0,
        double beta            = 1e-4,
        double lambda          = 0.0,
        double saturationCurrent      = 1e-14,
        double emissionCoefficient    = 1.0,
        double recombinationCurrent   = 0.0,
        double recombinationEmission  = 2.0,
        double gateSourceCapacitance  = 0.0,
        double gateDrainCapacitance   = 0.0,
        double junctionPotential      = 1.0,
        double gradingCoefficient     = 0.5,
        double forwardBiasCapCoeff    = 0.5,
        double drainResistance        = 0.0,
        double sourceResistance       = 0.0,
        double area                   = 1.0,
        double tempC                  = Temperature.NominalC,
        double tnomC                  = Temperature.NominalC,
        double saturationTempExponent = 3.0,
        double bandgapAtZeroK         = Temperature.SiliconBandgapEv,
        double vtoTempCoefficient     = 0.0,
        double betaTempCoefficient    = 0.0)
    {
        _s = (double)(int)polarity;

        double a  = area > 0 ? area : 1.0;
        double dT = Temperature.DeltaT(tempC, tnomC);

        _n  = emissionCoefficient   > 0 ? emissionCoefficient   : 1.0;
        _nr = recombinationEmission > 0 ? recombinationEmission : 2.0;

        // Geometry, then physics. Both are plain multipliers, so the order is presentational — but
        // the temperature relations are written in terms of the CARD's parameters, and reading them
        // as though they applied to an area-scaled value is the mistake this ordering forecloses.
        _is  = saturationCurrent    * a * Temperature.SaturationCurrentScale(tempC, tnomC, _n,  saturationTempExponent, bandgapAtZeroK);
        _isr = recombinationCurrent * a * Temperature.SaturationCurrentScale(tempC, tnomC, _nr, saturationTempExponent, bandgapAtZeroK);

        // The pinch-off voltage shifts additively in volts per degree and Beta scales in PERCENT per
        // degree — the two published forms, and they are not the same shape. The FET family states
        // the same pair the same way (see FetModelBase); confusing them costs several percent over a
        // realistic junction rise.
        //
        // The shift is applied in the CARD's coordinates and the channel sign taken afterwards, not
        // the other way round: Vtotc describes how the card's own Vto moves, and a p-channel card
        // states both in the p-channel convention. Applying the sign first would drift a p-channel
        // device's threshold the WRONG WAY while leaving every n-channel one untouched — invisible
        // in an n-channel test, and several hundred millivolts over a realistic junction rise. Same
        // order as FetModelBase's ShiftPerDegree, which is what this claims to match.
        _vto    = _s * (vto + vtoTempCoefficient * dT);
        _beta   = (beta > 0 ? beta : 0.0) * a
                * (betaTempCoefficient == 0.0 ? 1.0 : System.Math.Pow(1.01, betaTempCoefficient * dT));
        _lambda = lambda;

        double pb0 = junctionPotential > 0 ? junctionPotential : 1.0;
        double pbT = Temperature.JunctionPotentialAt(pb0, tempC, tnomC, bandgapAtZeroK);
        // A junction potential driven to or past zero says nothing physical — it is the relation
        // leaving its range, not the device. Fall back to the card's own value BEFORE the
        // capacitance scale is computed from it.
        if (pbT <= 0) pbT = pb0;

        _pb  = pbT;
        _m   = gradingCoefficient;
        _fc  = JunctionMath.SanitiseFc(forwardBiasCapCoeff);
        double capScale = Temperature.DepletionCapacitanceScale(pb0, pbT, _m, dT);
        _cgs = gateSourceCapacitance * a * capScale;
        _cgd = gateDrainCapacitance  * a * capScale;

        // m devices in parallel each carry their own ohmic resistance, so each pair is in parallel.
        _rd = drainResistance  > 0 ? drainResistance  / a : 0.0;
        _rs = sourceResistance > 0 ? sourceResistance / a : 0.0;

        _vt = Temperature.ThermalVoltage(Temperature.ToKelvin(tempC));
    }

    /// <summary>True for an n-channel device. The two are NOT interchangeable at a bias point.</summary>
    public bool IsNChannel => _s > 0;

    /// <summary>True when the drain resistance is modelled, which is what puts an internal node in play.</summary>
    public bool HasDrainResistance => _rd > 0;

    /// <summary>True when the source resistance is modelled.</summary>
    public bool HasSourceResistance => _rs > 0;

    /// <summary>How many internal nets the elaborator must mint — one per non-zero ohmic resistance.</summary>
    public int InternalNodeCount => (HasDrainResistance ? 1 : 0) + (HasSourceResistance ? 1 : 0);

    // The ohmic ports follow the three intrinsic ones in a FIXED order — drain, source — and only
    // the ones that exist appear. The elaborator builds its node list from the same two flags, so
    // the two orders are one rule stated twice and must be read together.
    private int PortRd => _rd > 0 ? IntrinsicPorts : -1;
    private int PortRs => _rs > 0 ? IntrinsicPorts + (_rd > 0 ? 1 : 0) : -1;

    public override int       PortCount => IntrinsicPorts + InternalNodeCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// ONE name per port — what each one CARRIES. The two ohmic ports are named for the terminal,
    /// because their current IS the external terminal current exactly.
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            var names = new List<string>(PortCount) { "ids", "igs", "igd" };
            if (_rd > 0) names.Add("drain");
            if (_rs > 0) names.Add("source");
            return names.ToArray();
        }
    }

    // A transistor conducts at DC, so it has no linear stamp — the nonlinear engines call Evaluate,
    // and the linear engines go through ComponentModel.StampLinearized.
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

        // Into n-channel coordinates. The sign comes back out at the end, and the derivatives carry
        // it on BOTH sides and so pass through unchanged.
        double vds = _s * v[PDs];
        double vgs = _s * v[PGs];
        double vgd = _s * v[PGd];

        // ── Channel current, with the two ends swapped when Vds < 0 ───────────
        // The swap is done by choosing WHICH PORT carries the effective gate-source voltage rather
        // than by rewriting it as a difference of others: both are already ports in their own right,
        // so the derivative lands in the column the quantity actually came from.
        bool   forward = vds >= 0.0;
        int    pG  = forward ? PGs : PGd;
        double egs = forward ? vgs : vgd;
        double eds = forward ? vds : -vds;
        double sd  = forward ? 1.0 : -1.0;
        double si  = forward ? 1.0 : -1.0;

        var (id, gm, gds) = DrainCurrent(egs, eds);
        i[PDs] = si * id;
        dg[PDs, PDs] = si * sd * gds;        // which is +gds in both orientations
        dg[PDs, pG]  = si * gm;

        // ── The two gate junctions ────────────────────────────────────────────
        // Each is a junction between two named terminals, so neither needs reverse handling: its
        // own port voltage is the bias across it whichever way the channel is running.
        var (igs, ggs) = GateCurrent(vgs);
        var (igd, ggd) = GateCurrent(vgd);
        i[PGs] = igs; dg[PGs, PGs] = ggs;
        i[PGd] = igd; dg[PGd, PGd] = ggd;

        var (qgs, cgs) = JunctionMath.Depletion(vgs, _cgs, _pb, _m, _fc);
        var (qgd, cgd) = JunctionMath.Depletion(vgd, _cgd, _pb, _m, _fc);
        q[PGs] = qgs; dc[PGs, PGs] = cgs;
        q[PGd] = qgd; dc[PGd, PGd] = cgd;

        // ── Ohmic ports ───────────────────────────────────────────────────────
        int pRd = PortRd, pRs = PortRs;
        if (pRd >= 0) { double g = 1.0 / _rd; i[pRd] = _s * v[pRd] * g; dg[pRd, pRd] = g; }
        if (pRs >= 0) { double g = 1.0 / _rs; i[pRs] = _s * v[pRs] * g; dg[pRs, pRs] = g; }

        // Back out of n-channel coordinates. The Jacobian is NOT touched: the sign appears once on
        // the current and once on the voltage, and the two cancel.
        if (_s < 0)
            for (int p = 0; p < P; p++) { i[p] = -i[p]; q[p] = -q[p]; }
    }

    /// <summary>
    /// The square law and its two derivatives, in n-channel coordinates and forward orientation
    /// (<paramref name="vds"/> ≥ 0). Analytic, because a finite-difference Jacobian inside a Newton
    /// loop costs an extra evaluation per entry and loses accuracy exactly where the device is most
    /// nonlinear.
    /// </summary>
    private (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double vgt = vgs - _vto;
        // Below pinch-off the device is off AND its derivatives are zero. A fudge conductance would
        // put current where there is none; the engine's own gmin already keeps the node alive.
        if (vgt <= 0.0) return (0.0, 0.0, 0.0);

        double lam = 1.0 + _lambda * vds;
        if (vds < vgt)
        {
            // Linear. Written as Vds·(2·Vgt − Vds) so the published form is visible rather than
            // recovered from an expanded polynomial.
            double core = vds * (2.0 * vgt - vds);
            return (_beta * core * lam,
                    _beta * 2.0 * vds * lam,
                    _beta * (2.0 * (vgt - vds) * lam + core * _lambda));
        }

        double sat = vgt * vgt;
        return (_beta * sat * lam,
                _beta * 2.0 * vgt * lam,
                _beta * sat * _lambda);
    }

    /// <summary>
    /// One gate junction's conduction current and its derivative. Diffusion plus recombination:
    /// two independent exponentials with their own ideality factors, which is what lets one dominate
    /// at low bias and the other at high. Folding the second into the first would fit one decade of
    /// the gate leakage curve and miss the rest.
    /// </summary>
    private (double I, double G) GateCurrent(double v)
    {
        var (id, gd) = JunctionMath.Exponential(v, _is,  _n  * _vt);
        var (ir, gr) = JunctionMath.Exponential(v, _isr, _nr * _vt);
        return (id + ir, gd + gr);
    }
}
