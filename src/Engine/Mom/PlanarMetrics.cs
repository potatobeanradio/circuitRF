// ANT-5 — THE NUMBERS AN ANTENNA IS JUDGED BY, AND THE CONVENTIONS NAMED ONCE.
//
// Most of what is here is short arithmetic over ANT-4's pattern. What the file is really about is
// that every one of these quantities has at least two defensible definitions, and a tool that
// reports one without saying which produces a number nobody can reproduce.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE FOUR CONVENTIONS, SETTLED HERE AND STATED IN THE CUBES' OWN NOTES
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
//   Directivity          D = 4π·U_peak / P_radiated                      standard, unambiguous
//   Gain                 G = D · η_rad = 4π·U_peak / P_accepted          EFFICIENCY ONLY
//   Realized gain        G · (accepted/available)                        INCLUDES MISMATCH
//   Radiation efficiency η_rad = P_radiated / P_accepted                 ACCEPTED, not incident
//
// R-ant-4. BOTH GAINS SHIP, BOTH ARE NAMED IN FULL, AND NEITHER IS CALLED JUST "GAIN". On a board
// mismatched by 3 dB at resonance the two differ by a factor of two, and this is the single most
// common place antenna tools quietly disagree. The cost of getting it wrong is a user comparing a
// circuitRF number against a datasheet and concluding one of them is broken.
//
// R-ant-5. THE EFFICIENCY DENOMINATOR IS P_ACCEPTED — the power that actually enters the port —
// because that is what the solve knows: mismatch is already in the port admittance and multiplying
// it in twice is the classic double count. And P_accepted is ½Re(Y_jj) of the RAW self-admittance,
// the matrix the currents being transformed came out of. The de-embedded S in the `S` cube describes
// a DIFFERENT structure with the feed leads removed, and using it here would weigh a pattern of one
// structure against the power accepted by another.
//
// R-ant-6. THERE IS EXACTLY ONE MISMATCH FACTOR AND BOTH GAINS READ IT. It is written as the ratio
// of accepted to AVAILABLE power, 4·Re(Z₀)·Re(Y)/|1 + Z₀Y|², which is a function of the same Y_jj
// and the same Z₀ that every other metric here uses — so "the two gains differ by exactly the
// mismatch factor" is structural rather than a coincidence two estimates happen to share.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY A REGISTRY AND NOT A LIST OF ADDS
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Because a metric can be UNAVAILABLE, and the interesting part of this phase is that a metric which
// cannot be computed must be PRESENT AND REFUSED rather than absent. Every entry carries its name,
// its unit, its note, how to compute it, and — the field that is the whole point —
// `Availability`, an EmSuitability saying whether it can be computed at all in the current medium
// and on the current grid, with the sentence saying why not.
//
// FRONT-TO-BACK IS THE STAGED ONE. With an analytically infinite ground plane the field at θ > 90°
// is identically zero, so F/B is infinite; reporting ∞, or a large finite number, or omitting the
// metric are all worse than refusing it. So `FrontToBackDb` is in this registry from this phase, it
// refuses with its own sentence naming the phase that supplies it, and **its Evaluate is written and
// correct already** — which is what makes the activation ONE PREDICATE rather than a re-plumb of the
// picker, the exporter and the CLI. `LayeredMedium.CanHost` states the rule being followed: deleting
// a refusal instead of narrowing it is how a kernel starts silently answering questions it cannot
// answer.
//
// BEAMWIDTH IS THE OTHER ONE, AND IT REFUSES FOR A DIFFERENT REASON. A 3 dB beamwidth is meaningless
// without saying in which plane. The E- and H-planes of a patch follow from its polarization, which
// follows from the current distribution — but a bent, slotted or circularly-polarized structure has
// no obvious principal plane, and inventing one would be the same class of error as inventing a
// current direction for the mesher (which `TransmissionLineMesh` declines, for exactly this reason).
// So a cut is either NAMED by the caller or DERIVED AND REPORTED, and when neither is available it is
// refused by name. It is never silently defaulted to φ = 0.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>Every metric ANT-5 defines. The enum is the registry's key and the cube names follow it.</summary>
public enum PlanarMetric
{
    PowerAccepted,
    PowerRadiated,
    PowerSurfaceWave,
    PowerDielectric,
    PowerConductor,
    RadiationEfficiency,
    DirectivityDbi,
    DirectivityPeakThetaDeg,
    DirectivityPeakPhiDeg,
    GainDbi,
    RealizedGainDbi,
    BeamwidthDeg,
    FrontToBackDb,
}

/// <summary>Which axes a metric's cube carries, beyond the <c>[freq, port]</c> every one of them has.</summary>
public enum PlanarMetricAxis
{
    /// <summary>One value per (frequency, port) — <c>[freq, port]</c>.</summary>
    PerPoint,

    /// <summary>One value per named or derived beamwidth cut — <c>[freq, cut, port]</c>. The cut axis
    /// carries the φ of each cut in degrees, which is how §4's "report the cut alongside the number"
    /// is satisfied without a second cube to go and read.</summary>
    PerCut,
}

/// <summary>
/// What a metric evaluation may be tuned by. <b>There is deliberately nothing here that changes a
/// DEFINITION</b> — only which cuts to take, which reference angle to measure polarization against,
/// and how finely to sample the azimuth of the surface-wave integral.
/// </summary>
/// <param name="BeamwidthCutsPhiDeg">
/// The φ planes, in degrees, to report a beamwidth in. <b>Null or empty means DERIVE one</b> — the
/// plane containing the peak and the dominant current axis — and the derived value is reported on the
/// cube's own cut axis, never silently defaulted.
/// </param>
/// <param name="AzimuthSamples">
/// Azimuth samples for the surface-wave pole integral. <b>Measured: 90 already agrees with 1,440 to
/// eleven digits</b>, because the φ integrand is smooth and periodic and the rule is the periodic
/// rectangle; 360 is the default only because it costs nothing on any mesh a pattern is affordable on.
/// </param>
/// <param name="PolarizationReferencePhiDeg">
/// <b>ANT-6's φ₀ — the nominal linear polarization direction the Ludwig-3 co/cross pair is measured
/// against, in degrees.</b> It is a property of the ANTENNA rather than of a plot, so it belongs to
/// the run and not to a view. <b>Null means DERIVE it</b> from the solved current distribution and
/// REPORT it (R-ant-9), and where that derivation is ambiguous — a circularly polarized or dual-fed
/// structure — the co/cross pair is <i>refused</i> rather than taken against an invented reference;
/// <c>AxialRatioDb</c> and <c>PolarizationSense</c> need no reference and are reported either way.
/// <b>This does not change what any cube MEANS</b>: it names the reference direction, and a second
/// DEFINITION of cross-pol would be a second cube (R-ant-8).
/// </param>
public sealed record PlanarMetricSettings(
    IReadOnlyList<double>? BeamwidthCutsPhiDeg = null,
    int                    AzimuthSamples = PlanarSurfaceWaveLaunch.DefaultAzimuthSamples,
    double?                PolarizationReferencePhiDeg = null)
{
    public static readonly PlanarMetricSettings Default = new();

    /// <summary>
    /// <b>How far past 1 a radiation efficiency may read before it is an ERROR rather than a number.</b>
    ///
    /// <para>The brief's rule is "efficiency ≤ 1 always, and a violation is an error rather than a
    /// clamp" — a clamped efficiency hides exactly the kind of balance error the itemisation exists to
    /// catch. But the numerator is a QUADRATURE (∫U dΩ on a sampled grid, the one quantity in
    /// <see cref="PlanarFarFieldPattern"/> that is not closed form) and the denominator comes out of a
    /// matrix factorisation, so a structure that radiates essentially everything it accepts can read
    /// 1 + ε for an ε that is nobody's defect. This tolerance is that ε and nothing more: it is set an
    /// order of magnitude above the worst measured power-balance residual on a lossless substrate
    /// (1.6e-4, <c>RESOLVED.md</c> §ANT-5), and anything past it REFUSES with both numbers named.</para>
    /// </summary>
    public const double EfficiencyTolerance = 2e-3;

    /// <summary>
    /// <b>How dominant the dominant current axis has to be.</b> The two principal moments of the
    /// total current's polarization ellipse are compared, and a minor axis carrying more than this
    /// fraction of the major one means there is no principal plane to derive — a circularly polarized
    /// patch is the case this exists for, and it reads 1.0 exactly. Refused, not guessed.
    /// </summary>
    public const double AxisAmbiguityRatio = 0.25;
}

/// <summary>Where a pattern peaks, on the grid it was sampled on.</summary>
/// <param name="ThetaDeg">The grid θ of the peak. <b>Quantised to the grid</b> — a 1° grid reports a
/// peak direction to 1°, which is why the grid is carried with every pattern.</param>
/// <param name="PhiDeg">The grid φ of the peak; <b>degenerate when <paramref name="ThetaDeg"/> is 0</b>,
/// where every azimuth names the same direction.</param>
/// <param name="IntensityWPerSr">U at the peak.</param>
public sealed record PlanarPatternPeak(double ThetaDeg, double PhiDeg, double IntensityWPerSr)
{
    public bool AzimuthIsDegenerate => ThetaDeg == 0.0;

    public static PlanarPatternPeak Of(PlanarFarFieldPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        int best = 0;
        for (int i = 1; i < pattern.U.Count; i++) if (pattern.U[i] > pattern.U[best]) best = i;
        int np = pattern.Grid.PhiDeg.Count;
        return new PlanarPatternPeak(pattern.Grid.ThetaDeg[best / np], pattern.Grid.PhiDeg[best % np],
                                     pattern.U[best]);
    }
}

/// <summary>One beamwidth cut: which plane, how it was chosen, and what it measured.</summary>
/// <param name="PhiDeg">The φ of the cut, as sampled — the nearest grid φ to the requested or derived
/// one. <b>The snap is reported rather than hidden</b>: it is this value that lands on the cube's cut
/// axis.</param>
/// <param name="RequestedPhiDeg">What was asked for or derived, before snapping.</param>
/// <param name="Derived">True when the plane was derived from the current distribution rather than
/// named by the caller.</param>
/// <param name="BeamwidthDeg">The 3 dB beamwidth in this plane.</param>
/// <param name="PeakThetaDeg">The θ of the cut's own peak, signed: negative means the φ + 180° half.</param>
public sealed record PlanarBeamCut(double PhiDeg, double RequestedPhiDeg, bool Derived,
                                  double BeamwidthDeg, double PeakThetaDeg);

/// <summary>Every beamwidth cut, or the reason there are none.</summary>
public sealed record PlanarBeamCuts(EmSuitability Verdict, IReadOnlyList<PlanarBeamCut> Cuts,
                                   string Note);

/// <summary>
/// Everything a metric's <c>Evaluate</c> is allowed to read: one driven port, one frequency, and the
/// pattern, currents and admittance that belong to each other. <b>The derived quantities are LAZY</b>
/// — the power budget runs a pole search and an O(N·N_φ) transform sum, so a caller that only wants a
/// directivity does not pay for it.
/// </summary>
public sealed class PlanarMetricContext
{
    public PlanarProblem         Problem       { get; }
    public PlanarMesh            Mesh          { get; }
    public Vec<Complex>          BasisCurrents { get; }
    public PlanarFarFieldPattern Pattern       { get; }

    /// <summary><c>Y[j, j]</c> of the RAW port admittance — R-ant-5's denominator and R-ant-6's one
    /// mismatch factor both come from this number and no other.</summary>
    public Complex RawSelfAdmittance { get; }

    /// <summary>The driven port's own reference impedance, which is what "available power" is
    /// available FROM.</summary>
    public Complex PortZ0 { get; }

    public PlanarMetricSettings Settings { get; }

    private readonly Lazy<PlanarPowerBudget> _budget;
    private readonly Lazy<PlanarBeamCuts>    _cuts;
    private readonly Lazy<PlanarPatternPeak> _peak;

    public PlanarPowerBudget Budget => _budget.Value;
    public PlanarBeamCuts    Cuts   => _cuts.Value;
    public PlanarPatternPeak Peak   => _peak.Value;

    public PlanarMetricContext(PlanarProblem problem, PlanarMesh mesh, Vec<Complex> basisCurrents,
                               PlanarFarFieldPattern pattern, Complex rawSelfAdmittance,
                               Complex portZ0, PlanarMetricSettings? settings = null,
                               int? maxDegreeOfParallelism = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pattern);

        Problem           = problem;
        Mesh              = mesh;
        BasisCurrents     = basisCurrents;
        Pattern           = pattern;
        RawSelfAdmittance = rawSelfAdmittance;
        PortZ0            = portZ0;
        Settings          = settings ?? PlanarMetricSettings.Default;

        _budget = new Lazy<PlanarPowerBudget>(() => PlanarPowerBudget.For(
            problem, mesh, basisCurrents, pattern, rawSelfAdmittance, Settings.AzimuthSamples,
            maxDegreeOfParallelism));
        _peak = new Lazy<PlanarPatternPeak>(() => PlanarPatternPeak.Of(pattern));
        _cuts = new Lazy<PlanarBeamCuts>(() => PlanarBeamwidth.Cuts(this));
    }

    /// <summary>
    /// <b>R-ant-6's ONE mismatch factor</b>: accepted power over AVAILABLE power,
    /// <c>4·Re(Z₀)·Re(Y)/|1 + Z₀Y|²</c>. Equal to <c>1 − |Γ|²</c> for a real reference and to the
    /// power-wave form for a complex one, and written this way because it reads the same Y_jj and Z₀
    /// as everything else here rather than a second estimate of the same thing.
    /// </summary>
    public double MismatchFactor
    {
        get
        {
            Complex d = 1.0 + PortZ0 * RawSelfAdmittance;
            double den = d.Real * d.Real + d.Imaginary * d.Imaginary;
            return den > 0 ? 4.0 * PortZ0.Real * RawSelfAdmittance.Real / den : double.NaN;
        }
    }

    /// <summary>4π·U_peak / P — the directivity-shaped ratio both gains are built from.</summary>
    internal double FourPiPeakOver(double powerW) =>
        4.0 * Math.PI * Peak.IntensityWPerSr / powerW;

    internal static double Db(double linear) => 10.0 * Math.Log10(linear);
}

/// <summary>One metric's definition: its cube, its unit, its note, its availability and its value.</summary>
/// <param name="Availability">
/// <b>The field that is the point of having a registry.</b> Whether this metric can be computed at
/// all in the current medium and on the current grid, with the sentence saying why not when it
/// cannot. A refused metric is PRESENT in the registry and absent from the cubes.
/// </param>
public sealed record PlanarMetricDefinition(
    PlanarMetric                                        Metric,
    string                                              CubeName,
    string                                              Unit,
    PlanarMetricAxis                                    Axis,
    string                                              Note,
    Func<PlanarMetricContext, EmSuitability>            Availability,
    Func<PlanarMetricContext, IReadOnlyList<double>>    Evaluate);

/// <summary>One metric's outcome at one (frequency, port): its verdict and, if available, its values.</summary>
public sealed record PlanarMetricOutcome(
    PlanarMetricDefinition Definition, EmSuitability Verdict, IReadOnlyList<double> Values)
{
    public bool Ok => Verdict.Ok;

    /// <summary>The single value of a <see cref="PlanarMetricAxis.PerPoint"/> metric.</summary>
    public double Value => Values[0];
}

/// <summary>Every metric at one (frequency, port), plus the budget and cuts they were read from.</summary>
public sealed record PlanarMetricReport(
    IReadOnlyList<PlanarMetricOutcome> Outcomes,
    IReadOnlyList<double>              CutsPhiDeg,
    PlanarPowerBudget                  Budget,
    PlanarPatternPeak                  Peak,
    int                                DrivenPort,
    double                             FrequencyHz)
{
    public PlanarMetricOutcome this[PlanarMetric metric] =>
        Outcomes.First(o => o.Definition.Metric == metric);

    /// <summary>Every metric that refused, with its reason — what a run reports as notes.</summary>
    public IEnumerable<(PlanarMetricDefinition Definition, string Reason)> Refusals =>
        Outcomes.Where(o => !o.Ok).Select(o => (o.Definition, o.Verdict.Reason!));
}

/// <summary>
/// <b>ANT-5's metric registry.</b> See the file header for the four conventions, for why every metric
/// goes through one registry rather than a list of adds, and for the two staged refusals.
/// </summary>
public static class PlanarMetrics
{
    /// <summary>The group the cubes land in — ANT-4's, because a metric is a property of a pattern.</summary>
    public const string Group = PlanarFarField.Group;

    /// <summary>
    /// <b>§2a — front-to-back is PRESENT and REFUSED, and the refusal names the phase that supplies
    /// it.</b> The activation is one predicate: when the θ axis reaches past 90° the metric becomes
    /// available and its <c>Evaluate</c>, which is already written and already correct, starts
    /// answering. Nothing downstream is re-plumbed.
    /// </summary>
    internal const string FrontToBackRefusal =
        "Front-to-back cannot be computed: the θ axis of this pattern stops at 90°, because the " +
        "ground plane and every dielectric layer are LATERALLY INFINITE in this analysis. The field " +
        "below the plane is not small — it is identically zero by construction — so the true F/B is " +
        "infinite, and ∞, a large finite number and a missing metric are all worse than this " +
        "sentence. What it would take is a FINITE GROUND OUTLINE and with it a θ axis to 180°, which " +
        "is the finite-ground phase; after that this metric is available when an outline is present " +
        "and still refused when it is not. The refusal is NARROWED there, never deleted — deleting a " +
        "refusal instead of narrowing it is how a kernel starts silently answering questions it " +
        "cannot answer.";

    private static EmSuitability PositivePower(double w, string what) =>
        w > 0 ? EmSuitability.Yes
              : EmSuitability.No(
                    $"{what} came out as {w:E6} W, which is not positive, so every ratio built on it " +
                    $"is meaningless. A driven passive structure accepts positive power and an open " +
                    $"one radiates some; a non-positive value here is the SOLVE to look at, not this " +
                    $"metric, and it is refused rather than turned into a dB that looks like an answer.");

    /// <summary>
    /// <b>The registry.</b> One entry per metric, in the order a listing should show them: the power
    /// itemisation first, then the efficiency it implies, then the directional numbers.
    /// </summary>
    public static readonly IReadOnlyList<PlanarMetricDefinition> Registry =
    [
        new(PlanarMetric.PowerAccepted, "PowerAccepted", "W", PlanarMetricAxis.PerPoint,
            "½·Re(Y_jj) for the 1 V delta gap — the power that actually enters the driven port, and " +
            "R-ant-5's denominator for every efficiency and gain here. It is the RAW self-admittance " +
            "of the structure as meshed, which is the matrix the transformed currents came out of; " +
            "the de-embedded S cube describes a different structure with the feed leads removed. It " +
            "is reported rather than left implicit because an efficiency whose denominator is not " +
            "published cannot be reproduced.",
            _ => EmSuitability.Yes,
            c => [c.Budget.AcceptedW]),

        new(PlanarMetric.PowerRadiated, "PowerRadiated", "W", PlanarMetricAxis.PerPoint,
            "∫U dΩ over the upper hemisphere — the only term in the budget that leaves the model. It " +
            "is a quadrature on the pattern's own grid and therefore the one number here that is not " +
            "closed form; on a 1°×1° grid it is worth ~1e-4 relative.",
            _ => EmSuitability.Yes,
            c => [c.Budget.RadiatedW]),

        new(PlanarMetric.PowerSurfaceWave, "PowerSurfaceWave", "W", PlanarMetricAxis.PerPoint,
            "The power launched into the substrate's own guided modes, from the RESIDUES of the " +
            "spectral line voltages at the surface-wave poles. In this model it is a LOSS " +
            "permanently: the substrate is laterally infinite, so power launched into a surface wave " +
            "never comes back. On a real board it reaches the edge and radiates, usually badly — " +
            "which is why the reported radiation efficiency is a LOWER BOUND on what a finite board " +
            "does. It is NOT radiation and must not be added to it.",
            c => c.Budget.SurfaceWaveVerdict,
            c => [c.Budget.SurfaceWaveW]),

        new(PlanarMetric.PowerDielectric, "PowerDielectric", "W", PlanarMetricAxis.PerPoint,
            "The dielectric loss NOT carried away by a guided mode: accepted − radiated − surface " +
            "wave. It is a RESIDUAL rather than a third independent integral, and that is a physical " +
            "statement, not a shortcut — over a laterally infinite lossy substrate a volume integral " +
            "of ωε₀ε″|E|² already contains the whole surface-wave term (the mode decays as " +
            "e^{−2αρ}/ρ and never escapes), so the two are not disjoint channels and adding them " +
            "would double-count. What makes this a measurement rather than a definition is the " +
            "LOSSLESS case, where it must come out zero and three independent routes are forced to " +
            "close on one number.",
            c => c.Budget.SurfaceWaveVerdict.Ok
                 ? EmSuitability.Yes
                 : EmSuitability.No(
                       "The dielectric term is the residual after the surface-wave term is taken out, " +
                       "and the surface-wave term was refused: " + c.Budget.SurfaceWaveVerdict.Reason +
                       " Without it the remainder is dielectric loss AND guided-mode power together, " +
                       "which is a different quantity from this one, and reporting the sum under this " +
                       "name would be the double count the note warns about. PowerAccepted minus " +
                       "PowerRadiated is that combined remainder, and both of those are published."),
            c => [c.Budget.DielectricW]),

        new(PlanarMetric.PowerConductor, "PowerConductor", "W", PlanarMetricAxis.PerPoint,
            PlanarPowerBudget.ConductorNote,
            _ => EmSuitability.Yes,
            c => [c.Budget.ConductorW]),

        new(PlanarMetric.RadiationEfficiency, "RadiationEfficiency", "", PlanarMetricAxis.PerPoint,
            "η_rad = P_radiated / P_accepted — a FRACTION, not dB, and not clamped. The denominator " +
            "is the power ACCEPTED at the port, never the incident power (R-ant-5): mismatch is " +
            "already in the port admittance and counting it twice is the classic double count. " +
            PlanarPowerBudget.BoundNote,
            c =>
            {
                var ok = PositivePower(c.Budget.AcceptedW, "The power accepted at the port");
                if (!ok.Ok) return ok;
                double e = c.Budget.RadiationEfficiency;
                return e <= 1.0 + PlanarMetricSettings.EfficiencyTolerance
                    ? EmSuitability.Yes
                    : EmSuitability.No(
                        $"The radiation efficiency came out as {e:F6} — above 1, by more than the " +
                        $"{PlanarMetricSettings.EfficiencyTolerance:E0} quadrature tolerance. " +
                        $"{SurfaceMesher.Eng(c.Budget.RadiatedW)}W is reported as radiated out of " +
                        $"{SurfaceMesher.Eng(c.Budget.AcceptedW)}W accepted, which no passive " +
                        $"structure can do. It is REFUSED rather than clamped, because a clamped " +
                        $"efficiency reads as 100 % and hides exactly the kind of level error the " +
                        $"itemisation exists to catch — the pattern's own normalisation, the port's " +
                        $"admittance, or the hemisphere quadrature is wrong, and all three are " +
                        $"published here so the wrong one can be found.");
            },
            c => [c.Budget.RadiationEfficiency]),

        new(PlanarMetric.DirectivityDbi, "DirectivityDbi", "dBi", PlanarMetricAxis.PerPoint,
            "D = 4π·U_peak / P_radiated, peak over the sampled hemisphere. Standard and unambiguous: " +
            "it says nothing about loss or mismatch, which is what separates it from the two gains. " +
            "With a LATERALLY INFINITE ground plane it reads optimistic against a real board, whose " +
            "finite plane puts substantial power behind it and tilts and ripples the pattern.",
            c => PositivePower(c.Budget.RadiatedW, "The radiated power"),
            c => [PlanarMetricContext.Db(c.FourPiPeakOver(c.Budget.RadiatedW))]),

        new(PlanarMetric.DirectivityPeakThetaDeg, "DirectivityPeakThetaDeg", "deg",
            PlanarMetricAxis.PerPoint,
            "Where the peak is. A peak directivity with no direction is half an answer, and on a " +
            "small ground plane the peak is not always at broadside — so this is reported rather " +
            "than assumed. It is QUANTISED TO THE PATTERN'S OWN GRID: a 1° grid locates the peak to 1°.",
            _ => EmSuitability.Yes,
            c => [c.Peak.ThetaDeg]),

        new(PlanarMetric.DirectivityPeakPhiDeg, "DirectivityPeakPhiDeg", "deg",
            PlanarMetricAxis.PerPoint,
            "The azimuth of the peak, quantised to the grid. Refused when the peak is at broadside, " +
            "where it carries no information.",
            c => c.Peak.AzimuthIsDegenerate
                 ? EmSuitability.No(
                       "The pattern peaks at θ = 0 — BROADSIDE — where every azimuth names the same " +
                       "direction, so the peak's φ carries no information. Reporting the first grid " +
                       "value would look like a measurement of a direction. The θ cube says where " +
                       "the peak is; this metric has nothing to add until the peak leaves broadside, " +
                       "which on a finite ground plane it does.")
                 : EmSuitability.Yes,
            c => [c.Peak.PhiDeg]),

        new(PlanarMetric.GainDbi, "GainDbi", "dBi", PlanarMetricAxis.PerPoint,
            "G = D · η_rad = 4π·U_peak / P_accepted. **EFFICIENCY ONLY — THIS EXCLUDES MISMATCH.** " +
            "It is never called just \"gain\": RealizedGainDbi is the one that includes mismatch, " +
            "and on a board mismatched by 3 dB at resonance the two differ by a factor of two. " +
            "Compare this against a datasheet number only after checking which of the two that " +
            "number is.",
            c => PositivePower(c.Budget.AcceptedW, "The power accepted at the port"),
            c => [PlanarMetricContext.Db(c.FourPiPeakOver(c.Budget.AcceptedW))]),

        new(PlanarMetric.RealizedGainDbi, "RealizedGainDbi", "dBi", PlanarMetricAxis.PerPoint,
            "Realized gain — G times the mismatch factor, so 4π·U_peak / P_available. **THIS ONE " +
            "INCLUDES MISMATCH.** The mismatch factor is 4·Re(Z₀)·Re(Y_jj)/|1 + Z₀·Y_jj|², read " +
            "from the SAME raw self-admittance and the SAME port reference as GainDbi and " +
            "PowerAccepted — there is exactly one mismatch factor in this registry and both gains " +
            "read it, so the two differing by exactly that factor is structural rather than two " +
            "estimates happening to agree.",
            c =>
            {
                var ok = PositivePower(c.Budget.AcceptedW, "The power accepted at the port");
                if (!ok.Ok) return ok;
                double m = c.MismatchFactor;
                return m > 0 && m <= 1.0 + 1e-12
                    ? EmSuitability.Yes
                    : EmSuitability.No(
                        $"The mismatch factor came out as {m:E6}, which is not in (0, 1]. It is the " +
                        $"ratio of accepted to AVAILABLE power, 4·Re(Z₀)·Re(Y)/|1 + Z₀Y|², with " +
                        $"Y_jj = {c.RawSelfAdmittance} S and Z₀ = {c.PortZ0} Ω; a value outside that " +
                        $"range means one of those two is not a passive one-port seen from a source " +
                        $"of positive resistance. Refused rather than turned into a dB.");
            },
            c => [PlanarMetricContext.Db(c.FourPiPeakOver(c.Budget.AcceptedW) * c.MismatchFactor)]),

        new(PlanarMetric.BeamwidthDeg, "BeamwidthDeg", "deg", PlanarMetricAxis.PerCut,
            "The 3 dB beamwidth, PER NAMED CUT — and a cut is either named by the caller or DERIVED " +
            "from the plane containing the peak and the dominant current axis, never silently " +
            "defaulted to φ = 0. The cut that was used is on the cube's own `cut` axis, in degrees, " +
            "including when it was derived. The half-power crossings are found by linear " +
            "interpolation of U against θ, on each side of the cut's own peak, with the cut running " +
            "from −θ_max through broadside to +θ_max (the negative half being the φ + 180° branch). " +
            "Two things to read it by. The cut DOMINATES the answer — measured on a 29 × 36 mm patch " +
            "on 1.6 mm FR-4 the E-plane reads 157° and the H-plane 84°, so a beamwidth quoted without " +
            "its plane is not a number. And with a LATERALLY INFINITE ground plane the E-plane is " +
            "much broader than a real board measures: the pattern only reaches zero at exact grazing, " +
            "so the half-power point sits near 78° where a finite board puts it nearer 40°.",
            c => c.Cuts.Verdict,
            c => c.Cuts.Cuts.Select(x => x.BeamwidthDeg).ToArray()),

        new(PlanarMetric.FrontToBackDb, "FrontToBackDb", "dB", PlanarMetricAxis.PerPoint,
            "10·log₁₀ of the peak intensity over the intensity in the antipodal direction " +
            "(180° − θ_peak, φ_peak + 180°). **Present and REFUSED in this kernel** — see the " +
            "verdict's own sentence, which names the reason and the phase that supplies it.",
            c => c.Pattern.Grid.ThetaDeg[^1] <= PlanarFarFieldGrid.MaxThetaDeg
                 ? EmSuitability.No(FrontToBackRefusal)
                 : AntipodalIntensity(c) > 0
                   ? EmSuitability.Yes
                   : EmSuitability.No(
                       $"Front-to-back cannot be computed: the θ axis of this pattern reaches " +
                       $"{c.Pattern.Grid.ThetaDeg[^1]:G6}°, but the intensity in the antipodal " +
                       $"direction ({180.0 - c.Peak.ThetaDeg:G6}°, {(c.Peak.PhiDeg + 180.0) % 360.0:G6}°) " +
                       $"is {AntipodalIntensity(c):E3} W/sr — not positive. The ratio would be " +
                       $"infinite, and printing ∞ or a large finite number for it is the one thing " +
                       $"this metric exists to avoid. A structural zero behind the pattern means the " +
                       $"model still has no lower hemisphere however far the axis was extended, which " +
                       $"is the same fact the 0…90° refusal states and is a model question rather " +
                       $"than a numerical one."),
            c => [FrontToBack(c)]),
    ];

    public static PlanarMetricDefinition Of(PlanarMetric metric) =>
        Registry.First(d => d.Metric == metric);

    /// <summary>
    /// Every metric at one (frequency, port). <b>A refusal is caught and carried, not thrown</b>: a
    /// metric that cannot be computed must not take the rest of the report down with it, which is the
    /// same shape ANT-4 gave the far field itself.
    /// </summary>
    public static PlanarMetricReport Evaluate(PlanarMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcomes = new List<PlanarMetricOutcome>(Registry.Count);
        foreach (var d in Registry)
        {
            var verdict = d.Availability(context);
            outcomes.Add(new PlanarMetricOutcome(
                d, verdict, verdict.Ok ? d.Evaluate(context) : []));
        }
        return new PlanarMetricReport(
            outcomes, context.Cuts.Cuts.Select(x => x.PhiDeg).ToArray(), context.Budget,
            context.Peak, context.Pattern.DrivenPort, context.Pattern.FrequencyHz);
    }

    /// <summary>
    /// <b>Written now although it cannot run now</b>, so that activating front-to-back is the one
    /// predicate in the registry entry above and not a second piece of work. The antipode of
    /// (θ, φ) is (180° − θ, φ + 180°), taken at the nearest grid direction.
    /// </summary>
    private static double FrontToBack(PlanarMetricContext c) =>
        PlanarMetricContext.Db(c.Peak.IntensityWPerSr / AntipodalIntensity(c));

    /// <summary>U at the nearest sampled direction to the antipode of the peak, (180° − θ, φ + 180°).</summary>
    private static double AntipodalIntensity(PlanarMetricContext c)
    {
        var g = c.Pattern.Grid;
        int it = Nearest(g.ThetaDeg, 180.0 - c.Peak.ThetaDeg);
        int ip = Nearest(g.PhiDeg, (c.Peak.PhiDeg + 180.0) % 360.0);
        return c.Pattern.U[g.IndexOf(it, ip)];
    }

    internal static int Nearest(IReadOnlyList<double> values, double want)
    {
        int at = 0;
        for (int i = 1; i < values.Count; i++)
            if (Math.Abs(values[i] - want) < Math.Abs(values[at] - want)) at = i;
        return at;
    }
}

/// <summary>
/// Every metric report one sweep produced, on one grid, with the axes the <c>DataSet</c> cubes carry.
///
/// <para><b>A metric is published as ONE cube over the whole set or not at all</b>, which is what
/// <see cref="Publishable"/> decides. A <c>DataCube</c> has no missing-value concept, so a cube with
/// a refused point in it would have to carry either a NaN — a hole in a plot that reads as data — or
/// a fabricated number. Availability here depends on the medium and the grid, both of which are
/// constant across a sweep, so in practice a metric is available at every point or at none; the one
/// exception is the surface-wave pole's own loss ceiling, which is frequency-dependent, and that case
/// is reported by name rather than half-published.</para>
/// </summary>
/// <param name="CutsPhiDeg">The beamwidth cut axis, in degrees — empty when no cut was available.</param>
/// <param name="Reports">Row-major <c>[freq, port]</c>.</param>
public sealed record PlanarMetricSet(
    IReadOnlyList<double>              FrequenciesHz,
    IReadOnlyList<int>                 PortNumbers,
    IReadOnlyList<double>              CutsPhiDeg,
    EmSuitability                      CutAxisVerdict,
    IReadOnlyList<PlanarMetricReport>  Reports)
{
    public PlanarMetricReport At(int freqIndex, int portIndex) =>
        Reports[freqIndex * PortNumbers.Count + portIndex];

    /// <summary>
    /// Whether <paramref name="metric"/> can be published as one cube over the whole set, and the
    /// reason when it cannot — which is the first point's own refusal, with that point named so a
    /// frequency-dependent refusal can be told from a medium-wide one.
    /// </summary>
    public EmSuitability Publishable(PlanarMetric metric)
    {
        var def = PlanarMetrics.Of(metric);
        if (def.Axis == PlanarMetricAxis.PerCut && !CutAxisVerdict.Ok) return CutAxisVerdict;

        for (int i = 0; i < FrequenciesHz.Count; i++)
            for (int p = 0; p < PortNumbers.Count; p++)
            {
                var outcome = At(i, p)[metric];
                if (!outcome.Ok)
                    return EmSuitability.No(
                        $"{def.CubeName} is not published. At " +
                        $"{SurfaceMesher.Eng(FrequenciesHz[i])}Hz, port {PortNumbers[p]} driven: " +
                        outcome.Verdict.Reason);
            }
        return EmSuitability.Yes;
    }

    /// <summary>Every metric that could not be published, with its sentence — said once per metric
    /// rather than once per point, which is what a run's notes want.</summary>
    public IEnumerable<string> Refusals
    {
        get
        {
            foreach (var d in PlanarMetrics.Registry)
            {
                var v = Publishable(d.Metric);
                if (!v.Ok) yield return v.Reason!;
            }
        }
    }

    /// <summary>
    /// The set, from one report per (frequency, port). <b>The cut axis has to AGREE across the set</b>:
    /// a derived cut is derived per pattern, and a structure whose dominant current axis rotates with
    /// frequency has no single plane to put on one axis. Averaging one in would be exactly the guess
    /// §4 forbids, so a disagreement refuses the beamwidth for the whole set and says to name a cut.
    /// </summary>
    public static PlanarMetricSet From(IReadOnlyList<double> frequenciesHz,
                                       IReadOnlyList<int> portNumbers,
                                       IReadOnlyList<PlanarMetricReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var first = reports.Count > 0 ? reports[0].CutsPhiDeg : [];
        var verdict = EmSuitability.Yes;

        foreach (var r in reports)
            if (!r.CutsPhiDeg.SequenceEqual(first))
            {
                verdict = EmSuitability.No(
                    $"BeamwidthDeg is not published: the cut planes do not agree across the sweep. " +
                    $"The first point takes [{string.Join(", ", first.Select(x => $"{x:G6}°"))}] and " +
                    $"{SurfaceMesher.Eng(r.FrequencyHz)}Hz port {r.DrivenPort} takes " +
                    $"[{string.Join(", ", r.CutsPhiDeg.Select(x => $"{x:G6}°"))}]. A DERIVED cut is " +
                    $"derived per pattern, and a structure whose dominant current axis moves with " +
                    $"frequency or differs between driven ports has no single plane to put on one " +
                    $"axis — averaging one in would be the guess §4 exists to prevent. Name the cuts " +
                    $"explicitly and every point reports the same planes.");
                break;
            }

        return new PlanarMetricSet(frequenciesHz, portNumbers, verdict.Ok ? first : [], verdict,
                                   reports);
    }
}
