using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Bipolar junction transistor — the standard charge-control model, in the form every
/// device-modelling text and every SPICE-lineage simulator states it, with the conventional
/// parameter names. Three terminals, <c>collector base emitter</c>; <see cref="Polarity"/> picks
/// n-p-n or p-n-p.
///
/// <code>
///   Vt   = k·T/q
///   Icc  = Is·(exp(Vb'e'/(Nf·Vt)) − 1)                    forward transport
///   Iec  = Is·(exp(Vb'c'/(Nr·Vt)) − 1)                    reverse transport
///   Ibe  = Icc/Bf + Ise·(exp(Vb'e'/(Ne·Vt)) − 1)          base current, emitter junction
///   Ibc  = Iec/Br + Isc·(exp(Vb'c'/(Nc·Vt)) − 1)          base current, collector junction
///   Ict  = (Icc − Iec)/qb                                 collector-to-emitter transport
///   q1   = 1 / (1 − Vb'c'/Vaf − Vb'e'/Var)                base-width modulation
///   q2   = Icc/Ikf + Iec/Ikr                              high-level injection
///   qb   = q1/2 · (1 + sqrt(1 + 4·q2))
///   Qbe  = Qj(Vb'e'; Cje,Vje,Mje) + Tff·Icc/qb            depletion + diffusion
///   Qbc  = Xcjc·Qj(Vb'c'; Cjc,Vjc,Mjc) + Tr·Iec
///   Tff  = Tf·(1 + Xtf·(Icc/(Icc+Itf))²·exp(Vb'c'/(1.44·Vtf)))
/// </code>
///
/// <para><b>The transport current is one port, not a controlled source hung off another.</b>
/// <c>Ict</c> flows collector→emitter and depends on BOTH junction voltages, so it occupies its own
/// port whose row of the Jacobian is full. Writing it as a current source controlled by the
/// base-emitter port would be the same equations with a Jacobian missing its <c>∂Ict/∂Vbc</c>
/// entry — which is exactly the Early effect and the whole of the output conductance.</para>
///
/// <para><b>The three parasitic resistances are INSIDE the model, on real internal nodes</b>, and
/// each one is present only when it is non-zero — the same rule and the same reason as
/// <see cref="DiodeModel"/>'s <c>Rs</c>. At RF they are not optional detail: <c>Rb</c> sets the
/// input match and the noise, <c>Re</c> degenerates the transconductance, and all three are
/// shunted by the junction capacitances, which is why the internal nodes must be genuine unknowns
/// with ordinary matrix rows. Collapsing them locally is exact at DC and wrong in harmonic balance,
/// where an internal node carries its own harmonic content.</para>
///
/// <para><b>The base resistance is current-dependent.</b> With <c>Irb</c> given it follows the
/// standard conductivity-modulation relation, falling from <c>Rb</c> at zero base current towards
/// <c>Rbm</c> at high current; with <c>Irb = 0</c> it follows <c>Rbm + (Rb − Rbm)/qb</c> instead,
/// which is the same physics stated through the base charge. Both are in the published model and
/// which one applies is decided by the parameter set, not by us.</para>
///
/// <para><b>Xcjc splits the collector junction across the base resistance.</b> The fraction
/// <c>Xcjc</c> sits on the internal base node and the remainder from the EXTERNAL base to the
/// internal collector — a real distributed effect, and the reason a device's feedback capacitance
/// does not simply see <c>Rb</c>. It is carried as a fourth intrinsic port that stores charge and
/// conducts nothing. When <c>Rb</c> is zero the two nodes are the same net and the two halves
/// simply add back to <c>Cjc</c>, so no special case is needed.</para>
///
/// <para><b>The junction temperature relations are SHARED with the diode and FET families</b>
/// (<see cref="Temperature"/>), not written a third time. With <c>Temp == Tnom</c> every one of
/// them collapses to the identity, so a device that states no temperature is bit-identical to one
/// with no temperature model at all. <c>Bf</c> and <c>Br</c> additionally follow <c>Xtb</c>, and
/// the two leakage saturation currents are divided by that same factor — the published pairing.</para>
///
/// <para><b>What is NOT modelled, deliberately:</b> the substrate junction (<c>Cjs</c>/<c>Vjs</c>/
/// <c>Mjs</c>) — a discrete RF transistor has no substrate terminal to attach it to, and inventing
/// a fourth pin would change what the symbol means; <c>Ptf</c> excess phase — it is a delay, and
/// circuitRF's weighting functions carry 1 and jω, not exp(−jωτ), so accepting the parameter would
/// be accepting a value that does nothing; <c>Kf</c>/<c>Af</c> flicker noise — there is no noise
/// analysis for it to feed; and self-heating — the device temperature is a parameter, not a solved
/// node. A parameter this model does not read is not offered.</para>
/// </summary>
public sealed class BjtModel : ComponentModel
{
    /// <summary>
    /// Where the exponential is replaced by its tangent. Above this Newton cannot recover from a
    /// single overshoot, so the model continues linearly from the last sane point: value AND slope
    /// stay continuous, which is what keeps the solve convergent. Same limit, same reason, as
    /// <see cref="DiodeModel"/>.
    /// </summary>
    private const double ExpArgLimit = 40.0;

    /// <summary>
    /// Ceiling on the transit-time enhancement <c>Xtf·(Icc/(Icc+Itf))²·exp(Vbc/(1.44·Vtf))</c>.
    ///
    /// <para><b>A guard, not a modelling choice.</b> That term is an empirical fit made in and just
    /// below the forward-active region, where <c>Vbc</c> is negative and it is a small correction.
    /// In hard saturation it is being evaluated far outside where it was fitted, and a parameter set
    /// whose <c>Vtf</c> is a few millivolts — which is what an extraction returns when the fit found
    /// no <c>Vbc</c> dependence worth naming — turns it into a step of e^100 or more. That is not a
    /// slow solve: the stored charge and its derivative reach values that wreck the conditioning of
    /// the whole matrix, and the device that caused it is one a user would call ordinary.</para>
    ///
    /// <para>10,000 is far above anything a real parameter set reaches — base pushout multiplies the
    /// transit time by tens, not thousands — so this never binds on a card that means something by
    /// the term, and it only ever binds where the term has already stopped meaning anything. The
    /// enhancement is held there in value AND slope; continuing it would go on growing a number that
    /// is already unphysical.</para>
    /// </summary>
    private const double MaxTransitEnhancement = 1e4;

    /// <summary>
    /// Floor on the Early denominator <c>1 − Vbc/Vaf − Vbe/Var</c>. It is positive everywhere a
    /// real device operates (it exceeds 1 whenever the collector junction is reverse-biased), and
    /// it is only ever approached by a Newton iterate passing through a bias no device could hold.
    /// Letting it reach zero would invert the sign of the whole transport current there, which
    /// turns a transient excursion into a divergence.
    /// </summary>
    private const double EarlyDenominatorFloor = 1e-2;

    /// <summary>n-p-n and p-n-p as the sign that multiplies every internal voltage and current.</summary>
    public enum Polarity { Npn = 1, Pnp = -1 }

    // Intrinsic port indices. Named because six of the Jacobian's entries are cross terms and a
    // bare integer there reads as a typo.
    private const int PBe = 0;   // (base', emitter')  — emitter junction
    private const int PBc = 1;   // (base', collector')— collector junction, Xcjc share
    private const int PCe = 2;   // (collector', emitter') — the transport current
    private const int PBx = 3;   // (base,  collector')— the (1 − Xcjc) share, charge only
    private const int IntrinsicPorts = 4;

    private readonly double _s;                                     // +1 npn, −1 pnp
    private readonly double _is, _bf, _nf, _vaf, _ikf, _ise, _ne;
    private readonly double _br, _nr, _var, _ikr, _isc, _nc;
    private readonly double _rb, _irb, _rbm, _re, _rc;
    private readonly double _cje, _vje, _mje, _cjc, _vjc, _mjc, _xcjc, _fc;
    private readonly double _tf, _xtf, _vtf, _itf, _tr;
    private readonly double _vt;

    /// <param name="polarity">n-p-n or p-n-p. Every internal voltage and current is multiplied by
    /// its sign, so one set of equations serves both and the two cannot drift apart.</param>
    /// <param name="area">
    /// Emitter area, as a multiple of the area the parameters were extracted for. Dimensionless by
    /// convention — the parameters are per unit of whatever the card's own unit is, and circuitRF
    /// does not need to know which. Currents and capacitances scale up with it, resistances down.
    /// </param>
    /// <param name="tempC">Device temperature, °C — resolved by the factory through the one shared
    /// rule (<see cref="Temperature.ResolveDeviceC"/>) so this family, the diode and the FETs cannot
    /// answer the question differently.</param>
    /// <param name="tnomC">
    /// The temperature this parameter set was EXTRACTED at, °C — a property of the model card, never
    /// of the run. Ambient must not move it: move both together and ΔT is zero at every temperature
    /// while the device still looks temperature-aware.
    /// </param>
    public BjtModel(
        Polarity polarity                = Polarity.Npn,
        double saturationCurrent         = 1e-16,
        double forwardBeta               = 100.0,
        double forwardEmission           = 1.0,
        double forwardEarlyVoltage       = 0.0,
        double forwardKneeCurrent        = 0.0,
        double emitterLeakageCurrent     = 0.0,
        double emitterLeakageEmission    = 1.5,
        double reverseBeta               = 1.0,
        double reverseEmission           = 1.0,
        double reverseEarlyVoltage       = 0.0,
        double reverseKneeCurrent        = 0.0,
        double collectorLeakageCurrent   = 0.0,
        double collectorLeakageEmission  = 2.0,
        double baseResistance            = 0.0,
        double baseResistanceKneeCurrent = 0.0,
        double minimumBaseResistance     = 0.0,
        double emitterResistance         = 0.0,
        double collectorResistance       = 0.0,
        double emitterJunctionCap        = 0.0,
        double emitterJunctionPotential  = 0.75,
        double emitterGradingCoefficient = 0.33,
        double collectorJunctionCap      = 0.0,
        double collectorJunctionPotential= 0.75,
        double collectorGradingCoefficient = 0.33,
        double internalBaseCapFraction   = 1.0,
        double forwardBiasCapCoeff       = 0.5,
        double forwardTransitTime         = 0.0,
        double transitTimeBiasCoeff       = 0.0,
        double transitTimeBiasVoltage     = 0.0,
        double transitTimeHighCurrent     = 0.0,
        double reverseTransitTime         = 0.0,
        double area                       = 1.0,
        double tempC                      = Temperature.NominalC,
        double tnomC                      = Temperature.NominalC,
        double saturationTempExponent     = 3.0,
        double betaTempExponent           = 0.0,
        double bandgapAtZeroK             = Temperature.SiliconBandgapEv)
    {
        _s = (double)(int)polarity;

        double a  = area > 0 ? area : 1.0;
        double dT = Temperature.DeltaT(tempC, tnomC);

        // Emission coefficients first: two of the temperature relations are written in terms of
        // them, and a zero reaching those would divide by it.
        _nf = forwardEmission          > 0 ? forwardEmission          : 1.0;
        _nr = reverseEmission          > 0 ? reverseEmission          : 1.0;
        _ne = emitterLeakageEmission   > 0 ? emitterLeakageEmission   : 1.5;
        _nc = collectorLeakageEmission > 0 ? collectorLeakageEmission : 2.0;

        // Beta follows Xtb; the two leakage saturation currents are divided by the SAME factor.
        // That pairing is the published one and it is not decorative — it is what keeps the low-bias
        // beta roll-off moving in the direction the measurement does.
        double betaScale = dT == 0.0 ? 1.0
            : System.Math.Pow(Temperature.ToKelvin(tempC) / Temperature.ToKelvin(tnomC), betaTempExponent);

        _bf = (forwardBeta > 0 ? forwardBeta : 1.0) * betaScale;
        _br = (reverseBeta > 0 ? reverseBeta : 1.0) * betaScale;

        // Geometry, then physics. Both are plain multipliers, so the order is presentational — but
        // the temperature relations are written in terms of the CARD's parameters, and reading them
        // as though they applied to an area-scaled value is the mistake this ordering forecloses.
        // Is takes the relation at N = 1: it is the transport current, and Nf belongs to its
        // exponent rather than to its temperature scaling.
        _is  = saturationCurrent       * a * Temperature.SaturationCurrentScale(tempC, tnomC, 1.0,  saturationTempExponent, bandgapAtZeroK);
        _ise = emitterLeakageCurrent   * a * Temperature.SaturationCurrentScale(tempC, tnomC, _ne, saturationTempExponent, bandgapAtZeroK) / betaScale;
        _isc = collectorLeakageCurrent * a * Temperature.SaturationCurrentScale(tempC, tnomC, _nc, saturationTempExponent, bandgapAtZeroK) / betaScale;

        _ikf = forwardKneeCurrent * a;
        _ikr = reverseKneeCurrent * a;
        _irb = baseResistanceKneeCurrent * a;

        // Zero means "not modelled" for both Early voltages — never "the Early effect saturates at
        // zero volts". Same rule the diode's Bv follows.
        _vaf = forwardEarlyVoltage > 0 ? forwardEarlyVoltage : 0.0;
        _var = reverseEarlyVoltage > 0 ? reverseEarlyVoltage : 0.0;

        // m devices in parallel each carry their own parasitic resistance, so each pair is in
        // parallel and the resistance falls with area.
        _rb  = baseResistance      > 0 ? baseResistance      / a : 0.0;
        _re  = emitterResistance   > 0 ? emitterResistance   / a : 0.0;
        _rc  = collectorResistance > 0 ? collectorResistance / a : 0.0;
        // Rbm is a FLOOR on Rb and cannot exceed it. A card that leaves it out, or states something
        // above Rb, is stating a base resistance that does not modulate — which is Rbm == Rb.
        double rbm = minimumBaseResistance > 0 ? minimumBaseResistance / a : _rb;
        _rbm = rbm > 0 && rbm <= _rb ? rbm : _rb;

        // Junction potentials fall with temperature and the depletion capacitances follow them. A
        // potential driven to or past zero says nothing physical — it is the relation leaving its
        // range, not the device — so fall back to the card's own value BEFORE the capacitance scale
        // is computed from it.
        double vje0 = emitterJunctionPotential   > 0 ? emitterJunctionPotential   : 0.75;
        double vjc0 = collectorJunctionPotential > 0 ? collectorJunctionPotential : 0.75;
        double vjeT = Temperature.JunctionPotentialAt(vje0, tempC, tnomC, bandgapAtZeroK);
        double vjcT = Temperature.JunctionPotentialAt(vjc0, tempC, tnomC, bandgapAtZeroK);
        if (vjeT <= 0) vjeT = vje0;
        if (vjcT <= 0) vjcT = vjc0;

        _mje = emitterGradingCoefficient;
        _mjc = collectorGradingCoefficient;
        _vje = vjeT;
        _vjc = vjcT;
        _cje = emitterJunctionCap   * a * Temperature.DepletionCapacitanceScale(vje0, vjeT, _mje, dT);
        _cjc = collectorJunctionCap * a * Temperature.DepletionCapacitanceScale(vjc0, vjcT, _mjc, dT);

        // Outside [0,1] the split is not a split. 1 puts the whole collector junction on the
        // internal base, which is what a card that does not state it means.
        _xcjc = internalBaseCapFraction is >= 0.0 and <= 1.0 ? internalBaseCapFraction : 1.0;

        // Fc must stay below 1 or the depletion expression divides by zero at the changeover.
        _fc = forwardBiasCapCoeff is > 0 and < 0.95 ? forwardBiasCapCoeff : 0.5;

        _tf  = forwardTransitTime > 0 ? forwardTransitTime : 0.0;
        _xtf = transitTimeBiasCoeff;
        _vtf = transitTimeBiasVoltage > 0 ? transitTimeBiasVoltage : 0.0;
        _itf = transitTimeHighCurrent > 0 ? transitTimeHighCurrent : 0.0;
        _tr  = reverseTransitTime > 0 ? reverseTransitTime : 0.0;

        _vt = Temperature.ThermalVoltage(Temperature.ToKelvin(tempC));
    }

    /// <summary>
    /// True for an n-p-n. Exposed because the two polarities are NOT interchangeable at a bias
    /// point: the same node voltages that put an n-p-n in forward active leave a p-n-p reverse-
    /// active, so anything that probes the device has to mirror its bias, not reuse one.
    /// </summary>
    public bool IsNpn => _s > 0;

    /// <summary>True when the base resistance is modelled, which is what puts an internal base node in play.</summary>
    public bool HasBaseResistance => _rb > 0;

    /// <summary>True when the emitter resistance is modelled.</summary>
    public bool HasEmitterResistance => _re > 0;

    /// <summary>True when the collector resistance is modelled.</summary>
    public bool HasCollectorResistance => _rc > 0;

    /// <summary>
    /// How many internal nets the elaborator must mint for this device — one per non-zero parasitic
    /// resistance. Zero is an ordinary answer: a card stating no parasitics is a three-net device.
    /// </summary>
    public int InternalNodeCount =>
        (HasCollectorResistance ? 1 : 0) + (HasBaseResistance ? 1 : 0) + (HasEmitterResistance ? 1 : 0);

    // The parasitic ports follow the four intrinsic ones in a FIXED order — collector, base,
    // emitter — and only the ones that exist appear. The elaborator builds its node list from the
    // same three flags, so the two orders are one rule stated twice and must be read together.
    private int PortRc => _rc > 0 ? IntrinsicPorts : -1;
    private int PortRb => _rb > 0 ? IntrinsicPorts + (_rc > 0 ? 1 : 0) : -1;
    private int PortRe => _re > 0 ? IntrinsicPorts + (_rc > 0 ? 1 : 0) + (_rb > 0 ? 1 : 0) : -1;

    public override int       PortCount => IntrinsicPorts + InternalNodeCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// ONE name per port, which is what <see cref="ComponentModel.TerminalNames"/>'s own default
    /// establishes and what both consumers read — the branch-current cube keys
    /// (<c>I:Q1:collector</c>) and harmonicaRF's port-axis labels index it by PORT, not by net.
    /// (Two models in this directory return two entries per port instead; those two are older than
    /// either consumer and their second entries are simply never read. Do not copy them.)
    ///
    /// <para>The ports are not terminals, so the names say what each one CARRIES. The three ohmic
    /// ports are the exception and are named for the terminal, because their current IS the external
    /// terminal current exactly — which is what makes <c>I:Q1:collector</c> mean what a user expects.
    /// A device with no parasitics has no such port and therefore no such key: the intrinsic
    /// currents are all there is, and naming one of them "collector" would be claiming a terminal
    /// current the model never separately computes.</para>
    /// </summary>
    public override string[] TerminalNames
    {
        get
        {
            var names = new List<string>(PortCount)
            {
                "ibe",   // base current through the emitter junction
                "ibc",   // base current through the collector junction
                "ic",    // the transport current, collector to emitter
                "icx",   // the extrinsic collector-junction displacement branch (charge only)
            };
            if (_rc > 0) names.Add("collector");
            if (_rb > 0) names.Add("base");
            if (_re > 0) names.Add("emitter");
            return names.ToArray();
        }
    }

    // A transistor conducts at DC, so it has no linear stamp — the nonlinear engines call Evaluate,
    // and the linear engines go through ComponentModel.StampLinearized, which linearises about the
    // bias point.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>
    /// One saturation-current exponential and its derivative, continued by its TANGENT above the
    /// argument limit — value and slope both stay continuous, which is what keeps Newton
    /// convergent. A clamp keeps the value finite and puts a kink in the Jacobian, which stalls the
    /// solve in a way that looks like a bad circuit.
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

    /// <summary>
    /// Depletion (junction) charge and its derivative, the small-signal junction capacitance.
    /// Identical in form to the diode's, and for the same reason: it is the standard formula, and
    /// above Fc·Vj it is continued by its TANGENT so value and slope both stay continuous.
    /// </summary>
    private (double Q, double C) Depletion(double v, double cj0, double vj, double m)
    {
        if (cj0 <= 0) return (0.0, 0.0);

        double fcvj = _fc * vj;
        if (v <= fcvj)
        {
            double u = 1.0 - v / vj;
            double c = cj0 * System.Math.Pow(u, -m);
            double q = System.Math.Abs(1.0 - m) < 1e-12
                ? -cj0 * vj * System.Math.Log(u)
                : cj0 * vj / (1.0 - m) * (1.0 - System.Math.Pow(u, 1.0 - m));
            return (q, c);
        }

        double u0  = 1.0 - _fc;
        double c0  = cj0 * System.Math.Pow(u0, -m);
        double dc0 = cj0 * m * System.Math.Pow(u0, -m - 1.0) / vj;   // dC/dV at the changeover
        double q0  = System.Math.Abs(1.0 - m) < 1e-12
            ? -cj0 * vj * System.Math.Log(u0)
            : cj0 * vj / (1.0 - m) * (1.0 - System.Math.Pow(u0, 1.0 - m));
        double dv = v - fcvj;
        return (q0 + c0 * dv + 0.5 * dc0 * dv * dv, c0 + dc0 * dv);
    }

    /// <summary>
    /// Base resistance and its derivative with respect to the base current.
    ///
    /// <para>Two relations, and the parameter set picks: with <c>Irb</c> given, the standard
    /// conductivity-modulation form, whose argument <c>z</c> runs from 0 (where the value is
    /// <c>Rb</c>) to π/2 (where it is <c>Rbm</c>); otherwise the base-charge form
    /// <c>Rbm + (Rb − Rbm)/qb</c>. Near zero current the first is 0/0 as written, so it is replaced
    /// there by the leading terms of its own expansion — the value and the slope, not a clamp.</para>
    /// </summary>
    /// <returns>(Rb, dRb/dIb, dRb/dqb) — only one of the two derivatives is ever non-zero.</returns>
    private (double R, double DdIb, double Ddqb) BaseResistance(double ib, double qb)
    {
        if (_rb <= 0) return (0.0, 0.0, 0.0);
        if (_rbm >= _rb) return (_rb, 0.0, 0.0);          // stated as not modulating

        double dr = _rb - _rbm;

        if (_irb <= 0)
        {
            // The base-charge form. qb > 0 always, so no guard is needed here that the caller has
            // not already applied.
            return (_rbm + dr / qb, 0.0, -dr / (qb * qb));
        }

        double x = ib / _irb;
        if (x <= 0) return (_rb, 0.0, 0.0);               // no base current: the unmodulated value

        // f(z) = (tan z − z)/(z·tan²z) → 1/3 as z → 0, so Rb → Rbm + 3·dr/3 = Rb. Expanded,
        // f ≈ 1/3 − 4z²/45 with z² ≈ 9x, giving Rb ≈ Rb − 2.4·dr·x. Below the crossover that
        // expansion IS the relation to well past double precision, and it has no 0/0 in it.
        const double SmallX = 1e-8;
        if (x < SmallX) return (_rb - 2.4 * dr * x, -2.4 * dr / _irb, 0.0);

        const double A = 144.0 / (System.Math.PI * System.Math.PI);   // 14.5924…
        const double B =  24.0 / (System.Math.PI * System.Math.PI);   //  2.4321…

        double sx = System.Math.Sqrt(x);
        double u  = System.Math.Sqrt(1.0 + A * x);
        double z  = (u - 1.0) / (B * sx);

        double t  = System.Math.Tan(z);
        double t2 = t * t;
        double dt = 1.0 + t2;                                          // d(tan z)/dz

        double num  = t - z;
        double den  = z * t2;
        double f    = num / den;
        double dnum = dt - 1.0;                                        // = t²
        double dden = t2 + z * 2.0 * t * dt;
        double dfdz = (dnum * den - num * dden) / (den * den);

        // dz/dx, written so the two terms share a denominator rather than cancelling by luck.
        double dzdx = (A * x / (2.0 * u) - (u - 1.0) / 2.0) / (B * x * sx);

        return (_rbm + 3.0 * dr * f, 3.0 * dr * dfdz * dzdx / _irb, 0.0);
    }

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
        int P = PortCount;

        // Internal (polarity-normalised) junction voltages. Every equation below is written for an
        // n-p-n; the p-n-p is the same device with every voltage and every current negated, so one
        // sign carries it and the two cannot drift apart.
        double vbe = _s * v[PBe];
        double vbc = _s * v[PBc];
        double vbx = _s * v[PBx];

        // ── Junction currents ────────────────────────────────────────────────
        var (icc, gcc) = Exponential(vbe, _is,  _nf * _vt);   // forward transport
        var (iec, gec) = Exponential(vbc, _is,  _nr * _vt);   // reverse transport
        var (ile, gle) = Exponential(vbe, _ise, _ne * _vt);   // emitter-junction leakage
        var (ilc, glc) = Exponential(vbc, _isc, _nc * _vt);   // collector-junction leakage

        double ibe = icc / _bf + ile,  gibe = gcc / _bf + gle;
        double ibc = iec / _br + ilc,  gibc = gec / _br + glc;

        // ── Base charge factor qb ────────────────────────────────────────────
        double d = 1.0;
        if (_vaf > 0) d -= vbc / _vaf;
        if (_var > 0) d -= vbe / _var;

        double q1, dq1dve = 0.0, dq1dvc = 0.0;
        if (d > EarlyDenominatorFloor)
        {
            q1 = 1.0 / d;
            if (_var > 0) dq1dve = q1 * q1 / _var;
            if (_vaf > 0) dq1dvc = q1 * q1 / _vaf;
        }
        else
        {
            // Past the floor the relation has left its range; hold it there rather than let the
            // transport current change sign. The derivative goes with it — a Newton step that
            // reaches here is being pushed back, not guided.
            q1 = 1.0 / EarlyDenominatorFloor;
        }

        double q2 = 0.0, dq2dve = 0.0, dq2dvc = 0.0;
        if (_ikf > 0) { q2 += icc / _ikf; dq2dve += gcc / _ikf; }
        if (_ikr > 0) { q2 += iec / _ikr; dq2dvc += gec / _ikr; }

        double rad = 1.0 + 4.0 * q2;
        if (rad < 1e-12) rad = 1e-12;                    // only reachable at a non-physical iterate
        double sq = System.Math.Sqrt(rad);

        double qb     = 0.5 * q1 * (1.0 + sq);
        double dqbdve = 0.5 * (1.0 + sq) * dq1dve + q1 * dq2dve / sq;
        double dqbdvc = 0.5 * (1.0 + sq) * dq1dvc + q1 * dq2dvc / sq;

        // ── Transport current ────────────────────────────────────────────────
        double idiff = icc - iec;
        double ict   = idiff / qb;
        double gictE =  gcc / qb - idiff * dqbdve / (qb * qb);
        double gictC = -gec / qb - idiff * dqbdvc / (qb * qb);

        // ── Charges ──────────────────────────────────────────────────────────
        var (qje, cje) = Depletion(vbe, _cje, _vje, _mje);
        var (qjc, cjc) = Depletion(vbc, _cjc, _vjc, _mjc);
        var (qjx, cjx) = Depletion(vbx, _cjc, _vjc, _mjc);

        // Forward transit time, bias-dependent. Off unless the card asks for it — Xtf = 0 leaves
        // Tff = Tf exactly, and the whole block collapses to a constant.
        double tff = _tf, dtffdve = 0.0, dtffdvc = 0.0;
        if (_tf > 0 && _xtf != 0.0 && vbe > 0)
        {
            double e = 1.0, dedvc = 0.0;
            if (_vtf > 0)
            {
                double arg = vbc / (1.44 * _vtf);
                if (arg > ExpArgLimit)
                {
                    e = System.Math.Exp(ExpArgLimit);   // held; the ceiling below takes it from here
                }
                else
                {
                    e     = System.Math.Exp(arg);
                    dedvc = e / (1.44 * _vtf);
                }
            }

            double r = 1.0, drdve = 0.0;
            if (_itf > 0)
            {
                double s = icc + _itf;
                r     = icc / s;
                drdve = _itf * gcc / (s * s);
            }

            double f = _xtf * r * r * e;
            if (f > MaxTransitEnhancement)
            {
                tff = _tf * (1.0 + MaxTransitEnhancement);   // value and slope both held — see above
            }
            else
            {
                tff     = _tf * (1.0 + f);
                dtffdve = _tf * _xtf * e * 2.0 * r * drdve;
                dtffdvc = _tf * _xtf * r * r * dedvc;
            }
        }

        // Diffusion charge is the forward transit time times the forward component of the transport
        // current — Icc/qb, not Icc. High-level injection reduces the stored charge for the same
        // junction voltage, and dropping qb here is how a model gets an fT that keeps rising.
        double qdiff  = tff * icc / qb;
        double dqdiffE = (dtffdve * icc + tff * gcc) / qb - tff * icc * dqbdve / (qb * qb);
        double dqdiffC =  dtffdvc * icc / qb          - tff * icc * dqbdvc / (qb * qb);

        double qbe = qje + qdiff;
        double cbeE = cje + dqdiffE;
        double cbeC = dqdiffC;

        double qbc = _xcjc * qjc + _tr * iec;
        double cbcC = _xcjc * cjc + _tr * gec;

        double qbx = (1.0 - _xcjc) * qjx;
        double cbxX = (1.0 - _xcjc) * cjx;

        // ── Assemble ─────────────────────────────────────────────────────────
        // The buffers are the caller's and are reused across a grid's samples, so start from a clean
        // sheet: the assignments below cover only the entries this device actually contributes to,
        // and everything else must read zero rather than the previous sample's value.
        Array.Clear(i);
        Array.Clear(q);
        Array.Clear(dg);
        Array.Clear(dc);

        // Currents and charges flip with polarity; the Jacobians do NOT — each entry carries one
        // factor of the sign from the current and one from the voltage, and s² is 1. The only
        // exception is the ohmic base port below, whose current does not flip while the junction
        // voltages controlling its resistance do.
        i[PBe] = _s * ibe;
        i[PBc] = _s * ibc;
        i[PCe] = _s * ict;
        i[PBx] = 0.0;

        q[PBe] = _s * qbe;
        q[PBc] = _s * qbc;
        q[PCe] = 0.0;
        q[PBx] = _s * qbx;

        dg[PBe, PBe] = gibe;
        dg[PBc, PBc] = gibc;
        dg[PCe, PBe] = gictE;
        dg[PCe, PBc] = gictC;

        dc[PBe, PBe] = cbeE;
        dc[PBe, PBc] = cbeC;
        dc[PBc, PBc] = cbcC;
        dc[PBx, PBx] = cbxX;

        // ── Parasitic resistances ────────────────────────────────────────────
        if (_rc > 0)
        {
            int p = PortRc;
            double g = 1.0 / _rc;
            i[p] = v[p] * g;
            dg[p, p] = g;
        }

        if (_re > 0)
        {
            int p = PortRe;
            double g = 1.0 / _re;
            i[p] = v[p] * g;
            dg[p, p] = g;
        }

        if (_rb > 0)
        {
            int p = PortRb;
            var (rb, drdib, drdqb) = BaseResistance(ibe + ibc, qb);
            double g = 1.0 / rb;
            i[p] = v[p] * g;
            dg[p, p] = g;

            // The modulation makes this port's current depend on the junction voltages too. One
            // factor of the polarity sign survives: the ohmic current does not flip, the controlling
            // voltages do.
            double dIdR = -v[p] / (rb * rb);
            double drdve = drdib * gibe + drdqb * dqbdve;
            double drdvc = drdib * gibc + drdqb * dqbdvc;
            dg[p, PBe] = _s * dIdR * drdve;
            dg[p, PBc] = _s * dIdR * drdvc;
        }
    }
}
