using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Mos;

/// <summary>
/// Shared plumbing for the built-in MOS transistor models. A concrete model contributes only its
/// own channel-current law and that law's three derivatives; everything below — terminals, ports,
/// polarity, the two bulk junctions, gate charge, the ohmic drain and source resistances and the
/// temperature relations — is identical across the family and lives here so the models cannot drift
/// apart. Exactly the arrangement <see cref="Fet.FetModelBase"/> uses for the MESFET laws.
///
/// <para><b>FOUR terminals, <c>drain gate source bulk</c>, and the bulk is a real pin.</b> Tying it
/// to the source internally would be one line and would silently delete the body effect — the
/// <c>Gamma</c>/<c>Phi</c> pair is a defining part of the level-1/3 law, and a card that states them
/// would import, simulate, and be wrong by hundreds of millivolts of threshold with nothing
/// reporting it. A device whose bulk really is tied to its source says so by wiring the pin, which
/// is what a discrete part's symbol does; <see cref="VdmosModel"/>, where the tie is internal to the
/// silicon, is a separate component for exactly that reason.</para>
///
/// <para><b>Ports.</b> Six intrinsic, plus one per non-zero ohmic resistance:</para>
/// <list type="table">
/// <item><term>0</term><description>(drain', source') — the channel current. No charge.</description></item>
/// <item><term>1</term><description>(bulk, source') — the bulk-source junction: current and depletion charge.</description></item>
/// <item><term>2</term><description>(bulk, drain') — the bulk-drain junction.</description></item>
/// <item><term>3</term><description>(gate, source') — half the inversion charge, plus the gate-source overlap.</description></item>
/// <item><term>4</term><description>(gate, drain') — the other half, plus the gate-drain overlap.</description></item>
/// <item><term>5</term><description>(gate, bulk) — the depletion charge under the gate, plus the gate-bulk overlap.</description></item>
/// <item><term>6</term><description>(drain, drain') — <c>Rd</c>, present only when non-zero.</description></item>
/// <item><term>7</term><description>(source, source') — <c>Rs</c>, present only when non-zero.</description></item>
/// </list>
///
/// <para>So <c>v[0]</c> is Vds, <c>v[1]</c> is Vbs and <c>v[3]</c> is Vgs — the form every published
/// MOS equation is written in — and <c>v[2] = v[1] − v[0]</c>, <c>v[4] = v[3] − v[0]</c>,
/// <c>v[5] = v[3] − v[1]</c> are the same three unknowns seen from the other terminals. The ports
/// are branches, not independent variables; <see cref="BjtModel"/>'s transport port is the same
/// arrangement and for the same reason.</para>
///
/// <para><b>The ohmic resistances are INSIDE the model, on real internal nodes</b> — the same rule
/// and the same reason as <see cref="BjtModel"/>'s Rb/Re/Rc. At RF they are not optional detail:
/// they are shunted by the junction and overlap capacitances, so collapsing them locally is exact
/// at DC and wrong in harmonic balance, where an internal node carries its own harmonic
/// content.</para>
///
/// <para><b>Polarity is a sign, not a law.</b> Every internal voltage is multiplied by
/// <see cref="Channel"/>'s own n-channel convention on the way in and every current and charge by
/// the same sign on the way out, so one set of equations serves both channels and the two cannot
/// drift apart — <see cref="BjtModel"/>'s arrangement exactly. The Jacobian passes through
/// unchanged, because the sign appears once on each side of every derivative.</para>
///
/// <para><b>What is NOT modelled, deliberately:</b> subthreshold conduction (<c>Nfs</c>) — the
/// classical law goes to exactly zero at threshold, and a device biased there is being asked a
/// question this model cannot answer; flicker noise (<c>Kf</c>/<c>Af</c>) — there is no noise
/// analysis for it to feed; and self-heating — the device temperature is a parameter, not a solved
/// node. A parameter this model does not read is not offered.</para>
/// </summary>
public abstract class MosfetModelBase : ComponentModel
{
    /// <summary>Permittivity of free space, F/m.</summary>
    public const double Eps0 = 8.8541878128e-12;

    /// <summary>Relative permittivity of the gate oxide — silicon dioxide.</summary>
    public const double OxideRelativePermittivity = 3.9;

    /// <summary>Relative permittivity of silicon, used for the depletion charge.</summary>
    public const double SiliconRelativePermittivity = 11.7;

    /// <summary>Intrinsic carrier concentration of silicon at 300 K, m^-3 (1.45e10 cm^-3).</summary>
    public const double IntrinsicCarrierDensity = 1.45e16;

    /// <summary>n-channel or p-channel, as the sign that multiplies every voltage, current and charge.</summary>
    public enum Channel4 { N = 1, P = -1 }

    // Intrinsic port indices. Named because most of the Jacobian's entries are cross terms and a
    // bare integer there reads as a typo.
    private protected const int PDs = 0;   // channel current, (drain', source')
    private protected const int PBs = 1;   // bulk-source junction
    private protected const int PBd = 2;   // bulk-drain junction
    private protected const int PGs = 3;   // gate-source charge
    private protected const int PGd = 4;   // gate-drain charge
    private protected const int PGb = 5;   // gate-bulk charge
    private protected const int IntrinsicPorts = 6;

    private readonly double _s;                       // +1 n-channel, −1 p-channel
    private readonly double _vt;                      // kT/q at the device temperature

    // Geometry and oxide.
    private readonly double _coxTotal;                // Cox·W·Leff, F — zero when Tox is not stated
    private readonly double _cgso, _cgdo, _cgbo;      // overlap capacitances, already × W or × Leff

    // Bulk junctions.
    private readonly double _isbs, _isbd, _nBulk;
    private readonly double _cjbs, _cjbd, _cjswbs, _cjswbd;
    private readonly double _pb, _mj, _mjsw, _fc;

    // Ohmic.
    private readonly double _rd, _rs;

    /// <summary>Surface potential at the device temperature, V — <c>Phi</c>, already scaled.</summary>
    protected double Phi { get; }

    /// <summary>Body-effect coefficient, V^½.</summary>
    protected double Gamma { get; }

    /// <summary>Effective channel length, m — <c>L − 2·Ld</c>.</summary>
    protected double Leff { get; }

    /// <summary>Channel width, m.</summary>
    protected double Width { get; }

    /// <summary>
    /// Lateral diffusion under each end of the gate, m — the amount by which the drawn length
    /// exceeds the effective one, per end. Exposed because level 3's short-channel charge-sharing
    /// factor is written in terms of it directly, not only through <see cref="Leff"/>.
    /// </summary>
    protected double LateralDiffusion { get; }

    /// <summary>Oxide capacitance per unit area, F/m². Zero when <c>Tox</c> is not stated.</summary>
    protected double CoxPerArea { get; }

    /// <summary>
    /// Threshold voltage at zero bulk bias and at the DEVICE temperature, V, in n-channel
    /// coordinates — so it is positive for an enhancement device of either channel type.
    /// </summary>
    protected double Vth0 { get; }

    /// <summary>
    /// Transconductance parameter at the device temperature, A/V² — <c>Kp</c> after the mobility
    /// relation. The concrete law multiplies by W/Leff itself, because level 3 needs the ratio
    /// separately from the mobility.
    /// </summary>
    protected double Kp { get; }

    /// <param name="channel">n- or p-channel. One sign, one set of equations.</param>
    /// <param name="vto">
    /// Threshold at zero bulk bias, V, AS THE CARD STATES IT — negative for an ordinary p-channel
    /// enhancement device. It is multiplied by the channel sign here, so the equations below always
    /// see a positive enhancement threshold and nothing downstream has to branch on polarity.
    /// </param>
    /// <param name="tempC">Device temperature, °C — resolved by the factory through the one shared
    /// rule (<see cref="Temperature.ResolveDeviceC"/>) so this family, the diode, the BJT and the
    /// MESFETs cannot answer the question differently.</param>
    /// <param name="tnomC">
    /// The temperature this parameter set was EXTRACTED at, °C — a property of the model card, never
    /// of the run. Ambient must not move it: move both together and ΔT is zero at every temperature
    /// while the device still looks temperature-aware.
    /// </param>
    protected MosfetModelBase(
        Channel4 channel,
        double vto, double kp, double gamma, double phi, double nsub,
        double w, double l, double ld, double tox, double uo,
        double cgso, double cgdo, double cgbo,
        double saturationCurrent, double saturationCurrentDensity, double bulkEmission,
        double cbd, double cbs, double cj, double cjsw,
        double ad, double @as, double pd, double ps,
        double pb, double mj, double mjsw, double fc,
        double rd, double rs, double rsh, double nrd, double nrs,
        double tempC, double tnomC, double xti, double bandgapAtZeroK)
    {
        _s = (double)(int)channel;

        double tK   = Temperature.ToKelvin(tempC);
        double tnK  = Temperature.ToKelvin(tnomC);
        double dT   = Temperature.DeltaT(tempC, tnomC);
        _vt         = Temperature.ThermalVoltage(tK);

        // ── Geometry and oxide ────────────────────────────────────────────────
        // Zero or negative W/L is not a device. Both fall back to the conventional 100 µm default
        // rather than throwing: a card states the process and the INSTANCE states the geometry, so
        // a card imported on its own legitimately says nothing about either.
        Width = w > 0 ? w : 100e-6;
        double lDrawn = l > 0 ? l : 100e-6;
        // Leff cannot be driven to or past zero by the lateral diffusion — that is the parameter
        // pair leaving its range, not a device with no channel.
        LateralDiffusion = ld > 0 ? ld : 0.0;
        Leff = lDrawn - 2.0 * LateralDiffusion;
        if (Leff <= 0) { Leff = lDrawn; LateralDiffusion = 0.0; }

        // Cox is what the intrinsic gate charge is built out of, and Tox is the only thing that
        // sets it. A card that states no Tox states no oxide capacitance, so the intrinsic charge
        // is ZERO and only the overlaps remain — the published rule, and the honest one: there is
        // nothing to guess an oxide thickness from.
        CoxPerArea = tox > 0 ? OxideRelativePermittivity * Eps0 / tox : 0.0;
        _coxTotal  = CoxPerArea * Width * Leff;

        // ── Surface potential and body effect ─────────────────────────────────
        // Both may be stated directly, or derived from the substrate doping when the card gives
        // that instead. Derivation happens ONLY where the card is silent — a stated value is the
        // card's own statement and always wins.
        double nsubM3 = nsub > 0 ? nsub * 1e6 : 0.0;         // cm^-3 on the card, m^-3 here
        double phiNom = phi > 0
            ? phi
            : nsubM3 > 0
                ? 2.0 * Temperature.ThermalVoltage(tnK)
                      * System.Math.Log(nsubM3 / IntrinsicCarrierDensity)
                : 0.6;
        double gammaV = gamma > 0
            ? gamma
            : nsubM3 > 0 && CoxPerArea > 0
                ? System.Math.Sqrt(2.0 * Temperature.ElemCharge
                                   * SiliconRelativePermittivity * Eps0 * nsubM3) / CoxPerArea
                : 0.0;
        Gamma = gammaV;

        // The surface potential follows the SAME relation a junction potential does — it is the
        // same physics and the same question, so it gets the same answer (see Temperature).
        double phiT = Temperature.JunctionPotentialAt(phiNom, tempC, tnomC, bandgapAtZeroK);
        if (phiT <= 0) phiT = phiNom;
        Phi = phiT;

        // Threshold at the device temperature, DERIVED rather than fitted:
        //   Vth = −Eg/2 + Phi/2 + Gamma·√Phi   (the flat-band term carries the −Eg/2)
        // so a change of temperature moves it by half the change in Phi, minus half the change in
        // the bandgap, plus whatever the body-effect term does as √Phi moves. Writing it this way
        // means the three temperature relations already in Temperature supply all of it, and the
        // whole shift collapses to exactly zero at Temp == Tnom.
        double vtoN = _s * vto;
        if (dT != 0.0)
        {
            double egNom = Temperature.BandgapAt(tnK, bandgapAtZeroK);
            double egDev = Temperature.BandgapAt(tK,  bandgapAtZeroK);
            vtoN += 0.5 * (phiT - phiNom)
                  - 0.5 * (egDev - egNom)
                  + gammaV * (System.Math.Sqrt(phiT) - System.Math.Sqrt(phiNom));
        }
        Vth0 = vtoN;

        // ── Transconductance parameter ────────────────────────────────────────
        // Kp may be stated, or derived from the low-field mobility and Cox. Uo is in cm²/V·s on
        // every published card, which is 1e-4 m²/V·s.
        double kpNom = kp > 0
            ? kp
            : CoxPerArea > 0 ? (uo > 0 ? uo : 600.0) * 1e-4 * CoxPerArea : 2.0e-5;
        // Mobility falls as T^−1.5 — the published relation for this family, and the reason a MOS
        // drain current drops with temperature at high gate overdrive while the threshold shift
        // pushes it the other way at low overdrive.
        Kp = dT == 0.0 ? kpNom : kpNom * System.Math.Pow(tK / tnK, -1.5);

        // ── Overlap capacitances ──────────────────────────────────────────────
        // Cgso/Cgdo are per unit WIDTH and Cgbo per unit LENGTH — the published convention, and
        // getting it the wrong way round is a capacitance wrong by the aspect ratio.
        _cgso = (cgso > 0 ? cgso : 0.0) * Width;
        _cgdo = (cgdo > 0 ? cgdo : 0.0) * Width;
        _cgbo = (cgbo > 0 ? cgbo : 0.0) * Leff;

        // ── Bulk junctions ────────────────────────────────────────────────────
        // Two sources for each saturation current: a per-junction Is, or a current DENSITY times
        // the stated area. The density wins where an area is stated, which is the published rule —
        // Is is the fallback for a card that carries no geometry.
        _nBulk = bulkEmission > 0 ? bulkEmission : 1.0;
        double isScale = Temperature.SaturationCurrentScale(tempC, tnomC, _nBulk, xti, bandgapAtZeroK);
        double isDefault = saturationCurrent > 0 ? saturationCurrent : 1e-14;
        _isbs = (saturationCurrentDensity > 0 && @as > 0 ? saturationCurrentDensity * @as : isDefault) * isScale;
        _isbd = (saturationCurrentDensity > 0 && ad > 0 ? saturationCurrentDensity * ad : isDefault) * isScale;

        double pbNom = pb > 0 ? pb : 0.8;
        double pbT   = Temperature.JunctionPotentialAt(pbNom, tempC, tnomC, bandgapAtZeroK);
        if (pbT <= 0) pbT = pbNom;
        _pb   = pbT;
        _mj   = mj   > 0 ? mj   : 0.5;
        _mjsw = mjsw > 0 ? mjsw : 0.33;
        _fc   = JunctionMath.SanitiseFc(fc);

        double capScale   = Temperature.DepletionCapacitanceScale(pbNom, pbT, _mj,   dT);
        double capScaleSw = Temperature.DepletionCapacitanceScale(pbNom, pbT, _mjsw, dT);

        // A STATED absolute capacitance wins; the process constant times the geometry is the
        // fallback. That is the published order and it is the opposite of the saturation currents'
        // above, where the density wins — the two rules genuinely differ, and getting either
        // backwards makes an explicitly stated value silently inert whenever the card also carries
        // the other form, which is most cards.
        _cjbs = (cbs > 0 ? cbs : cj > 0 && @as > 0 ? cj * @as : 0.0) * capScale;
        _cjbd = (cbd > 0 ? cbd : cj > 0 && ad  > 0 ? cj * ad  : 0.0) * capScale;
        // The sidewall is a SEPARATE junction with its own grading coefficient, not a correction to
        // the bottom one — which is why it is added as its own depletion term rather than folded in.
        _cjswbs = (cjsw > 0 && ps > 0 ? cjsw * ps : 0.0) * capScaleSw;
        _cjswbd = (cjsw > 0 && pd > 0 ? cjsw * pd : 0.0) * capScaleSw;

        // ── Ohmic ─────────────────────────────────────────────────────────────
        // A stated Rd/Rs wins; otherwise a sheet resistance times a square count, which is how a
        // process card states the same quantity.
        _rd = rd > 0 ? rd : rsh > 0 && nrd > 0 ? rsh * nrd : 0.0;
        _rs = rs > 0 ? rs : rsh > 0 && nrs > 0 ? rsh * nrs : 0.0;
    }

    /// <summary>True for an n-channel device. The two are NOT interchangeable at a bias point.</summary>
    public bool IsNChannel => _s > 0;

    /// <summary>True when the drain resistance is modelled, which is what puts an internal node in play.</summary>
    public bool HasDrainResistance => _rd > 0;

    /// <summary>True when the source resistance is modelled.</summary>
    public bool HasSourceResistance => _rs > 0;

    /// <summary>
    /// How many internal nets the elaborator must mint — one per non-zero ohmic resistance. Zero is
    /// an ordinary answer: a card stating no parasitics is a four-net device.
    /// </summary>
    public int InternalNodeCount => (HasDrainResistance ? 1 : 0) + (HasSourceResistance ? 1 : 0);

    // The ohmic ports follow the six intrinsic ones in a FIXED order — drain, source — and only the
    // ones that exist appear. The elaborator builds its node list from the same two flags, so the
    // two orders are one rule stated twice and must be read together.
    private int PortRd => _rd > 0 ? IntrinsicPorts : -1;
    private int PortRs => _rs > 0 ? IntrinsicPorts + (_rd > 0 ? 1 : 0) : -1;

    public sealed override int       PortCount => IntrinsicPorts + InternalNodeCount;
    public sealed override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// ONE name per port — what each one CARRIES, not which terminal it touches, because five of
    /// the six intrinsic ports are branches inside the device. The two ohmic ports are the
    /// exception and are named for the terminal, because their current IS the external terminal
    /// current exactly, which is what makes <c>I:M1:drain</c> mean what a user expects.
    /// </summary>
    public sealed override string[] TerminalNames
    {
        get
        {
            var names = new List<string>(PortCount) { "ids", "ibs", "ibd", "qgs", "qgd", "qgb" };
            if (_rd > 0) names.Add("drain");
            if (_rs > 0) names.Add("source");
            return names.ToArray();
        }
    }

    /// <summary>
    /// The model's own channel-current law, in n-channel coordinates and in FORWARD orientation
    /// (<paramref name="vds"/> ≥ 0). Returns the current and all three derivatives — analytically,
    /// because a finite-difference Jacobian inside a Newton loop costs an extra evaluation per entry
    /// and loses accuracy exactly where the device is most nonlinear.
    ///
    /// <para>Reverse operation is NOT this method's problem: the base swaps drain and source before
    /// calling and maps the derivatives back, so every law is written once, for the orientation it
    /// is published in.</para>
    /// </summary>
    /// <returns>(Id, gm = ∂Id/∂Vgs, gds = ∂Id/∂Vds, gmbs = ∂Id/∂Vbs)</returns>
    protected abstract (double Id, double Gm, double Gds, double Gmbs) Channel(
        double vgs, double vds, double vbs);

    /// <summary>
    /// Threshold voltage and its derivative with respect to the bulk bias, in n-channel
    /// coordinates. The body-effect term is the standard one; <c>Phi − Vbs</c> is held at a small
    /// positive floor because a forward-biased bulk past the surface potential is the relation
    /// leaving its range, not a device with an imaginary threshold.
    /// </summary>
    protected (double Vth, double DVthDVbs) Threshold(double vbs)
    {
        if (Gamma <= 0) return (Vth0, 0.0);
        var (r, dr) = BodySqrt(vbs);
        return (Vth0 + Gamma * (r - System.Math.Sqrt(Phi)), Gamma * dr);
    }

    /// <summary>
    /// <c>√(Phi − Vbs)</c> and its derivative — the factor the threshold, the depletion charge under
    /// the gate and (through <see cref="MosfetLevel3Model"/>) the bulk-charge factor are all built
    /// from, so no two of them can disagree about it. It is reached in its continued region only by
    /// a bulk driven forward past the surface potential.
    ///
    /// <para><b>Below a small positive floor it is continued by the RECIPROCAL form</b>
    /// <c>√Floor / (1 + (Floor − arg)/(2·Floor))</c>, which is the published continuation and the
    /// only shape that meets all three requirements at once: it matches the square root's own VALUE
    /// and its own SLOPE at the changeover, so nothing below the floor moves by a bit; and it decays
    /// toward zero without ever reaching it.</para>
    ///
    /// <para><b>Both of the obvious alternatives are wrong here, in opposite directions.</b> A
    /// TANGENT — the treatment every other runaway expression in this directory gets, and what this
    /// used to do — crosses zero about two millivolts past the changeover and goes negative, and
    /// level 3 DIVIDES by this quantity in its bulk-charge factor: that is a pole sitting at an
    /// ordinary forward bulk bias, and a negative √ feeding a charge-sharing term below it. A CLAMP
    /// freezes the body effect outright — the threshold stops moving, <c>gmbs</c> reads exactly
    /// zero, and the drain current flatlines while the device is plainly still conducting. Staying
    /// positive is a correctness requirement of the callers, not a numerical nicety.</para>
    /// </summary>
    private protected (double R, double DVbs) BodySqrt(double vbs)
    {
        const double Floor = 1e-3;
        double arg = Phi - vbs;
        if (arg < Floor)
        {
            double r0 = System.Math.Sqrt(Floor);
            double k  = 1.0 + (Floor - arg) / (2.0 * Floor);   // ≥ 1, growing as the bulk goes forward
            return (r0 / k, -0.5 / (r0 * k * k));
        }
        double sq = System.Math.Sqrt(arg);
        return (sq, -0.5 / sq);
    }

    // A transistor conducts at DC, so it has no linear stamp — the nonlinear engines call Evaluate,
    // and the linear engines go through ComponentModel.StampLinearized, which linearises about the
    // bias point.
    public sealed override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public sealed override NonlinearResult Evaluate(in PortVoltages v)
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

        // Into n-channel coordinates. Every equation below is written for an n-channel device; the
        // sign comes back out at the end, and the derivatives carry it on BOTH sides and so pass
        // through unchanged.
        double vds = _s * v[PDs];
        double vbs = _s * v[PBs];
        double vbd = _s * v[PBd];
        double vgs = _s * v[PGs];
        double vgd = _s * v[PGd];
        double vgb = _s * v[PGb];

        // ── Which end is the source ───────────────────────────────────────────
        // A MOS transistor is symmetric: which terminal acts as the drain is decided by the bias,
        // not by the schematic. Evaluating the forward law at a negative Vds would run it outside
        // where it is defined and return a current that is plausible and wrong.
        //
        // The swap is done by choosing WHICH PORTS carry the effective gate-source and bulk-source
        // voltages, not by rewriting them as differences of other ports. Both are already ports in
        // their own right — that is what ports 2 and 4 are — so the derivatives land in the columns
        // the quantities actually came from, and the node-level Jacobian comes out the same either
        // way because the port voltages are consistent by construction.
        bool   forward = vds >= 0.0;
        int    pG  = forward ? PGs : PGd;      // the port carrying the effective gate-source voltage
        int    pB  = forward ? PBs : PBd;      // ... and the effective bulk-source voltage
        double egs = forward ? vgs : vgd;
        double eds = forward ? vds : -vds;
        double ebs = forward ? vbs : vbd;
        double sd  = forward ? 1.0 : -1.0;     // d(eds)/d(v[PDs])
        double si  = forward ? 1.0 : -1.0;     // the sign the drain current comes back out with

        // ── Channel current ───────────────────────────────────────────────────
        var (id, gm, gds, gmbs) = Channel(egs, eds, ebs);
        i[PDs] = si * id;
        dg[PDs, PDs] = si * sd * gds;          // which is +gds in both orientations
        dg[PDs, pG]  = si * gm;
        dg[PDs, pB]  = si * gmbs;

        // ── The two bulk junctions ────────────────────────────────────────────
        // These need no reverse handling: each is a junction between two named terminals, and its
        // own port voltage is the bias across it whichever way the channel is running.
        double vteBulk = _nBulk * _vt;
        var (ibs, gbs) = JunctionMath.Exponential(vbs, _isbs, vteBulk);
        var (ibd, gbd) = JunctionMath.Exponential(vbd, _isbd, vteBulk);
        i[PBs] = ibs; dg[PBs, PBs] = gbs;
        i[PBd] = ibd; dg[PBd, PBd] = gbd;

        // Bottom and sidewall are two junctions with two grading coefficients, added.
        var (qbsA, cbsA) = JunctionMath.Depletion(vbs, _cjbs,   _pb, _mj,   _fc);
        var (qbsB, cbsB) = JunctionMath.Depletion(vbs, _cjswbs, _pb, _mjsw, _fc);
        var (qbdA, cbdA) = JunctionMath.Depletion(vbd, _cjbd,   _pb, _mj,   _fc);
        var (qbdB, cbdB) = JunctionMath.Depletion(vbd, _cjswbd, _pb, _mjsw, _fc);
        q[PBs] = qbsA + qbsB; dc[PBs, PBs] = cbsA + cbsB;
        q[PBd] = qbdA + qbdB; dc[PBd, PBd] = cbdA + cbdB;

        // ── Gate charge ───────────────────────────────────────────────────────
        // Computed in the same forward orientation the current is, so a device operating with its
        // terminals reversed stores the charge its BIAS asks for rather than the charge its
        // schematic does.
        var (qinv, dQdEgs, dQdEds, dQdEbs) = InversionCharge(egs, eds, ebs);
        var (qbulk, dQbDEbs)               = DepletionChargeUnderGate(ebs);

        // Charge neutrality: Qg + Qb + Qd + Qs = 0, with the channel charge split evenly between
        // the two ends. See InversionCharge for why the split is 50/50 and what that costs.
        double half = 0.5 * qinv;
        q[PGs] = -half  + _cgso * vgs;
        q[PGd] = -half  + _cgdo * vgd;
        q[PGb] = -qbulk + _cgbo * vgb;

        // Accumulated rather than assigned: pG and pB are PGs/PBs in one orientation and PGd/PBd in
        // the other, so one of these entries shares a slot with an overlap capacitance.
        dc[PGs, pG]  += -0.5 * dQdEgs;
        dc[PGs, PDs] += -0.5 * dQdEds * sd;
        dc[PGs, pB]  += -0.5 * dQdEbs;
        dc[PGs, PGs] += _cgso;

        dc[PGd, pG]  += -0.5 * dQdEgs;
        dc[PGd, PDs] += -0.5 * dQdEds * sd;
        dc[PGd, pB]  += -0.5 * dQdEbs;
        dc[PGd, PGd] += _cgdo;

        dc[PGb, pB]  += -dQbDEbs;
        dc[PGb, PGb] += _cgbo;

        // ── Ohmic ports ───────────────────────────────────────────────────────
        // Linear, but inside the model so a placed transistor is one device with one parameter set.
        // No cross terms: each carries only its own voltage. The COUPLING is the shared internal
        // node, which the engine supplies through the node map.
        int pRd = PortRd, pRs = PortRs;
        if (pRd >= 0) { double g = 1.0 / _rd; i[pRd] = _s * v[pRd] * g; dg[pRd, pRd] = g; }
        if (pRs >= 0) { double g = 1.0 / _rs; i[pRs] = _s * v[pRs] * g; dg[pRs, pRs] = g; }

        // ── Back out of n-channel coordinates ─────────────────────────────────
        // The Jacobian is NOT touched: the sign appears once on the current and once on the
        // voltage, and the two cancel.
        if (_s < 0)
            for (int p = 0; p < P; p++) { i[p] = -i[p]; q[p] = -q[p]; }
    }

    /// <summary>
    /// Total inversion (channel) charge and its three derivatives, in n-channel coordinates and
    /// forward orientation. Negative, because it is electrons.
    ///
    /// <para><b>This is the CHARGE-based long-channel result, not the Meyer capacitance set</b>, and
    /// the difference matters here more than it does in a transient simulator. Meyer's model states
    /// three capacitances that each depend on more than one terminal voltage, so the charge implied
    /// by them depends on the PATH taken through the bias space — around a harmonic cycle it does
    /// not return to where it started, and a periodic steady-state solve has nothing to converge to.
    /// Integrating the channel charge directly, as below, is conservative by construction, and its
    /// derivatives reduce to exactly Meyer's capacitances wherever Meyer's are right.</para>
    ///
    /// <code>
    ///   Vgt  = Vgs − Vth(Vbs),   Vdse = min(Vds, Vgt),   u = Vdse/Vgt
    ///   Qinv = −Cox·W·Leff · Vgt · (2/3)·(3 − 3u + u²)/(2 − u)
    /// </code>
    ///
    /// <para>u = 0 (no drain bias) gives the uniform channel <c>−Cox·Vgt</c>; u = 1 (saturation)
    /// gives the classical <c>−(2/3)·Cox·Vgt</c>. There is no singularity anywhere between, which is
    /// why it is written in u rather than in the ratio of two expressions that both vanish.</para>
    ///
    /// <para><b>The split between drain and source is 50/50, deliberately.</b> The charge-partition
    /// question — how much of the channel charge belongs to each end — has no answer the classical
    /// law itself supplies; the 40/60 alternative comes from the same integral weighted by position
    /// and is the better one in a switching transient. At RF, where this family is used, both ends
    /// see the same signal through the same channel resistance, and the even split is what keeps the
    /// device symmetric under the drain/source swap above. Stated here because it is a modelling
    /// decision, not an approximation nobody made.</para>
    /// </summary>
    private (double Q, double DVgs, double DVds, double DVbs) InversionCharge(
        double vgs, double vds, double vbs)
    {
        if (_coxTotal <= 0) return (0.0, 0.0, 0.0, 0.0);

        var (vth, dVthDVbs) = Threshold(vbs);
        double vgt = vgs - vth;
        if (vgt <= 0.0) return (0.0, 0.0, 0.0, 0.0);       // cutoff: no inversion charge

        // Vdse = min(Vds, Vgt) — the channel pinches off at the saturation voltage and stores no
        // more charge past it.
        bool   linear = vds < vgt;
        double vdse   = linear ? vds : vgt;
        double u      = vdse / vgt;                        // in [0,1] by construction

        // f(u) = (2/3)·(3 − 3u + u²)/(2 − u), and f'(u) = (2/3)·(u² − 4u + 3)/(2 − u)²·(−1)… written
        // out below rather than simplified, so the quotient rule is visible against the formula.
        double den  = 2.0 - u;
        double num  = 3.0 - 3.0 * u + u * u;
        double f    = (2.0 / 3.0) * num / den;
        double dfdu = (2.0 / 3.0) * ((-3.0 + 2.0 * u) * den + num) / (den * den);

        double qinv = -_coxTotal * vgt * f;

        // ∂Qinv/∂Vgt and ∂Qinv/∂Vdse, then the chain rule onto the three port voltages.
        //   u = Vdse/Vgt  →  ∂u/∂Vgt = −u/Vgt,  ∂u/∂Vdse = 1/Vgt
        double dQdVgt  = -_coxTotal * (f + vgt * dfdu * (-u / vgt));
        double dQdVdse = -_coxTotal * vgt * dfdu / vgt;

        // In saturation Vdse IS Vgt, so the drain derivative is zero and the gate derivative picks
        // up the second path. In the linear region Vdse is Vds and the two are independent.
        double dVgs, dVds, dVbs;
        if (linear)
        {
            dVgs = dQdVgt;                       // ∂Vgt/∂Vgs = 1
            dVds = dQdVdse;
            dVbs = dQdVgt * -dVthDVbs;           // ∂Vgt/∂Vbs = −∂Vth/∂Vbs
        }
        else
        {
            dVgs = dQdVgt + dQdVdse;             // Vdse = Vgt, so both paths follow Vgs
            dVds = 0.0;
            dVbs = (dQdVgt + dQdVdse) * -dVthDVbs;
        }

        return (qinv, dVgs, dVds, dVbs);
    }

    /// <summary>
    /// Depletion charge under the gate and its derivative — the acceptor charge the body effect is
    /// the voltage cost of, so it is written from the SAME <c>Gamma</c> and <c>Phi</c> the threshold
    /// is. Negative, and it grows in magnitude as the bulk is reverse-biased.
    ///
    /// <para>Taken at the source end rather than integrated along the channel. That is the classical
    /// simplification and it is the one <see cref="Threshold"/> already makes — a threshold written
    /// as a function of Vbs alone is exactly the statement that the depletion charge does not vary
    /// down the channel. Doing the two differently would be two models in one device.</para>
    /// </summary>
    private (double Q, double DVbs) DepletionChargeUnderGate(double vbs)
    {
        if (_coxTotal <= 0 || Gamma <= 0) return (0.0, 0.0);

        var (r, dr) = BodySqrt(vbs);
        return (-_coxTotal * Gamma * r, -_coxTotal * Gamma * dr);
    }
}
