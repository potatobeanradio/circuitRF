// L8d — D4: THE CALIBRATION STANDARD IS CONSTRUCTED FROM THE DUT'S OWN MESH, NOT RE-MESHED.
//
// The two-line calibration is exact only insofar as the error box is the SAME OBJECT in the DUT and
// in the standard. That is not a tolerance, it is a construction — and a standard that is "the same
// line, re-meshed" is not the same error box, because L8b's grid spacing is derived from the WHOLE
// problem's narrowness per axis, so a bare rectangle and the feed of a bend do not get the same
// cells. The difference then shows up as a de-embedding residual that reads exactly like a
// convergence problem.
//
// So the standard is built here, cell by cell, from three things the port resolution already
// carries: the DUT's transverse gridlines across the port (verbatim), the DUT's own longitudinal
// cell run for the first K cells inward (verbatim, mirrored at the far end), and the DUT's bulk cell
// size to fill the middle. R-prt-5 asserts the result on COORDINATES, as an equality.
//
// Three consequences, all of them the point:
//   • the port's cell neighbourhood is identical, so the error box is the same object;
//   • SurfaceMesher is not touched — L8c's out-of-scope list keeps it closed and nothing here needs
//     it opened;
//   • R-msh-2's (LayerIndex, IY, IX) ordering contract is honoured by construction, because the
//     builder emits cells in exactly that order.
//
// The limitation this leaves, stated rather than discovered: the DUT's feed may have other metal
// near it that the standard does not. That is inherent to any two-line calibration — it is true of
// real TRL as well — and it is why PlanarPorts.CheckFeedClearance exists (R-prt-3) and why R-prt-4's
// feed-length study is the measurement that says how much clearance is enough.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D5 — γ COMES FROM A 2×2 EIGENVALUE, IN CLOSED FORM
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// With T₁, T₂ the wave-cascade matrices of the two standards' raw S,
//
//     M = T₂ T₁⁻¹ = T_A · diag(e^{−γΔℓ}, e^{+γΔℓ}) · T_A⁻¹
//
// so e^{∓γΔℓ} are M's eigenvalues. And it collapses further than "the quadratic formula": a
// RECIPROCAL 2-port has det T = S₁₂/S₂₁ = 1, so det M = 1, so the two eigenvalues multiply to 1 and
//
//     cosh(γΔℓ) = ½·tr(M)
//
// EXACTLY — no discriminant, no eigensolver, no library. Same shape as L7b-b's own closed-form 2×2
// and for the same recorded reason.
//
// β is then known only modulo 2π/Δℓ, and that is CHECKED rather than assumed (R-prt-6): the branch
// is anchored at the lowest frequency, where βΔℓ ≪ π, and continued upward by predicting the next
// point's βΔℓ from the last one scaled by frequency.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// How the calibration standards are dimensioned. Every one of these is a length expressed in
/// SUBSTRATE HEIGHTS, because that is the scale the port's evanescent field actually decays on —
/// R-prt-4 measures the number and this is where it lands.
/// </summary>
/// <param name="EndRunHeights">How far inward from the port the standard must reproduce the DUT's
/// own cells. The error box has to fit inside this.</param>
/// <param name="ShortLineHeights">The shorter standard's length between reference planes, so the
/// two error boxes do not see each other.</param>
/// <param name="TargetElectricalDegrees">Where βΔℓ is aimed at each sub-band's geometric mean.
/// <b>60°, not the interval's own 90° centre, and that is measured margin rather than timidity:</b>
/// Δℓ has to be chosen before any solve, from ε_eff ≈ (εᵣ+1)/2, which underestimates a real
/// microstrip's ε_eff by ~15% on FR-4 and by more once dispersion lifts it — so the realised βΔℓ
/// comes out systematically ABOVE the target. Aiming at 90° put the 20 GHz point at 202°, past π,
/// where the branch wraps. Aiming low costs nothing at the other end: 60/√3.16 = 34° is still well
/// clear of 20°.</param>
/// <param name="DrivenNeighbourClearanceHeights">
/// <b>PCAL2/R-pcal2-4 — how far a neighbouring conductor THAT CARRIES A PORT has to be from a
/// calibrated feed, laterally, before the two-line calibration is valid. A breach is a refusal.</b>
///
/// <para><b>It is separate from <see cref="EndRunHeights"/> and the two must never be collapsed
/// again.</b> They were one constant until PCAL2, and the engine tested clearance at 3 h because
/// that is how far inward the standard reproduces the DUT's own cells. Those are different
/// quantities: <see cref="EndRunHeights"/> is a LENGTH ALONG the feed and sizes every calibration
/// standard (already 4.57× the DUT's unknowns on the fixture and 6.51× on the board), so raising it
/// to buy clearance would pay for the clearance in solve time. This one is a distance ACROSS and
/// costs nothing.</para>
///
/// <para><b>5, and the number is measured.</b> PCAL1 swept a coupled pair against the exact
/// cross-section oracle and found the error follows the neighbour's distance in SUBSTRATE HEIGHTS —
/// a 4× change in line width moves the threshold by 5 %, and the neighbour's own width is inert to
/// within 2 % (a 5 mm pour behaves like a 254 µm trace). The requirement came out at ≈ 4 h on a
/// 0.9 mm substrate and ≈ 5.5 h on a 0.225 mm one; s/h is a good variable rather than an exact
/// invariant, and 5 covers both families. At the 3 h the engine used to test, the coupled pair is
/// still 0.169 out in |ΔS| — 3.3× the A-vs-B floor — and non-passive.</para>
/// </param>
/// <param name="PassiveNeighbourClearanceHeights">
/// <b>PCAL2/R-pcal2-4 — the same distance for a neighbour that carries NO port.</b>
///
/// <para><b>Two numbers rather than one, because the two cases are 2-3× apart and one number would
/// either refuse designs that are fine or pass designs that are 18 dB wrong.</b> A passive
/// neighbour leaves one driven mode at the reference plane and PCAL1 measured its threshold at
/// ≈ 2 h on both substrate heights — against 4-5.5 h for a driven one, which carries the second
/// port's own error box and the mutual terms as well. <b>It is not benign</b>: at 246 µm
/// (s/h = 0.27) a passive neighbour is 18.0 dB out in S₁₁ at 1 GHz.</para>
///
/// <para>Whether a neighbour carries a port is decided on the MESH, by
/// <see cref="PlanarConductors"/> — the port list is in hand at the call site and the answer is a
/// fact about the structure, not a setting.</para>
/// </param>
/// <param name="IncludePassiveNeighbours">
/// <b>PCAL3/R-pcal3-1 — whether a passive neighbour inside
/// <paramref name="PassiveNeighbourClearanceHeights"/> is put INTO the port's calibration standard
/// rather than refusing the run.</b> On, because a refusal is what the user gets otherwise and the
/// metal is reproducible; off is how the pre-PCAL3 answer is reproduced for comparison, which is
/// what every measurement in the findings was taken against.
///
/// <para>It changes nothing on a feed that is already clear. The widening is attempted only where a
/// PASSIVE breach was measured, which since PCAL2 is a refusal — so a run that passes today builds
/// the profile it builds today (R-pcal3-4).</para>
/// </param>
/// <param name="NeighbourExtensionCells">
/// <b>R-pcal3-3 — how far past each reference plane the standard's neighbour runs, in bulk cells of
/// the port's own line.</b> The DUT's neighbour carries on past the port; the standard's has to stop
/// somewhere, and leaving it open at the plane, shorting it, and running it past are three different
/// structures with three different error boxes.
///
/// <para><b>0 — flush with the driven line, open at both ends — and it is MEASURED rather than
/// reasoned about</b> (<c>src/Engine/Mom/RESOLVED.md</c>, PCAL3 §3). What is not negotiable is that
/// both standards of a pair treat the neighbour identically, which they do by construction here:
/// the extension is the same number of cells on the short line and on the long one, so D5's γ is
/// measuring the length between the planes and not the difference between two end treatments.</para>
/// </param>
/// <param name="IncludeDrivenGroups">
/// <b>PCAL4/R-pcal4-1 — whether ports whose feeds are mutually within
/// <paramref name="DrivenNeighbourClearanceHeights"/> are CALIBRATED TOGETHER, as one group with one
/// modal error box, rather than refusing the run.</b> On, for PCAL3's reason one conductor over: a
/// refusal is what the user gets otherwise and the metal is reproducible. Off is how the
/// pre-PCAL4 answer is reproduced for comparison, which is what every measurement in the findings
/// was taken against.
///
/// <para>It changes nothing on a feed that is already clear: the grouping is attempted only where a
/// DRIVEN breach was measured, which since PCAL2 is a refusal — so a run that passes today keeps the
/// per-port scalar box it has always had, bit for bit.</para>
/// </param>
/// <param name="MaxCalibrationGroupSize">
/// <b>PCAL4/R-pcal4-7 — how many conductors one calibration group may hold, and it is a COST gate
/// rather than an algebraic one.</b> The algebra is written for any N. What is not free is the
/// standard: N conductors make it a 2N-port whose mesh carries all of them, at every separation and
/// at every frequency, and its N modes have to stay separable on top of that. <b>3</b> covers the
/// coupled pair the series opened on and the three-conductor case the brief's own gate 3 asks for,
/// and refuses a wider group by name rather than discovering the cost at run time.
/// </param>
/// <param name="ModeSeparationFloorDegrees">
/// <b>PCAL4/R-pcal4-2 and R-pcal4-6 — how far apart two modes' electrical lengths must be, over the
/// separation a frequency actually read, before the cascade eigenproblem can tell them apart.</b>
/// At zero the two eigenvalues of M coincide, the null space of (M − μI) is a plane rather than a
/// line, and which line in it is which mode is not a question the arithmetic can answer — so a
/// nearly-degenerate group is DECLINED by name rather than de-embedded against a modal basis decided
/// by round-off. The measured distance is reported on every point whether or not it trips.
/// </param>
/// <param name="QuasiStaticBelowCrossover">
/// <b>QSC — whether a frequency below <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> takes
/// the QUASI-STATIC γ and its two short standards, rather than D5's two-line extraction and the
/// λ-scaled ladder.</b> On, because the wall is what a user gets otherwise and — below the crossover
/// — the measured value is the less accurate of the two as well as the more expensive.
///
/// <para><b>Off is how the pre-QSC answer is reproduced for comparison</b>, which is
/// <see cref="IncludePassiveNeighbours"/>'s and <see cref="IncludeDrivenGroups"/>'s own sentence and
/// exists for the same two reasons: every measurement in §QSC's findings was taken against it, and
/// the engine's own ceiling refusals are gated on a run that still reaches the ceiling. <b>It is NOT
/// a crossover knob</b> — the crossover itself is measured, fixed per stack and deliberately not
/// settable, because a knob THERE invites someone to put it in the wrong place and both failure
/// modes publish a smooth, plausible, wrong phase. This one is on or off, its off-state restores a
/// refusal rather than a wrong number, and it has no <c>.cem</c> field and no panel control.</para>
///
/// <para>It changes nothing on a band that sits entirely above the crossover, which is where the
/// measured calibration is right and where the whole L8/L9 acceptance set lives.</para>
/// </param>
/// <param name="QuasiStaticSeparationHeights">
/// <b>QSC — the line separation used BELOW the crossover, in substrate heights, and it is
/// frequency-independent on purpose.</b> That is the whole saving: with γ supplied quasi-statically
/// (<see cref="PlanarQuasiStaticLine"/>) Δℓ no longer has to be ELECTRICALLY long, so it is sized
/// from the substrate and the mesh instead of from λ, and the sub-band ladder collapses to ONE
/// separation because there is no βΔℓ window left to cover.
///
/// <para><b>6, and it is measured rather than chosen for roundness</b> — see
/// <c>src/Engine/Mom/RESOLVED.md</c> §QSC. What degrades as Δℓ shrinks is the error box's own
/// conditioning, smoothly, as 1/Δℓ; what does NOT degrade smoothly is the a₂₂ SIGN selection, whose
/// margin (<see cref="PlanarErrorBox.RejectedResidual"/>) is the quantity M3 swept and the reason
/// the floor is not lower still.</para>
/// </param>
/// <param name="QuasiStaticSeparationMinBulkCells">
/// <b>…and never fewer than this many of the port's OWN bulk cells.</b> A separation is realised by
/// <see cref="PlanarCalibration.BuildLine"/> as a whole number of bulk cells, so on a coarse mesh a
/// substrate-sized target can round to one or two of them — and two standards differing by one cell
/// differ by one cell's worth of discretisation error as well as by a length. This floor is what
/// stops the mesh deciding the separation by accident.
/// </param>
public sealed record PlanarCalibrationSettings(
    double EndRunHeights                     = 3.0,
    double ShortLineHeights                  = 3.0,
    double TargetElectricalDegrees           = 60.0,
    double DrivenNeighbourClearanceHeights   = 5.0,
    double PassiveNeighbourClearanceHeights  = 2.0,
    bool   IncludePassiveNeighbours          = true,
    int    NeighbourExtensionCells           = 0,
    bool   IncludeDrivenGroups               = true,
    int    MaxCalibrationGroupSize           = 3,
    double ModeSeparationFloorDegrees        = 0.5,
    bool   QuasiStaticBelowCrossover         = true,
    double QuasiStaticSeparationHeights      = 6.0,
    int    QuasiStaticSeparationMinBulkCells = 4)
{
    public static readonly PlanarCalibrationSettings Default = new();

    /// <summary>TRL's own usable interval, and the interval where D6's denominator is well away
    /// from its zero at βΔℓ = nπ. R-prt-6 flags every frequency outside it.</summary>
    public const double UsableLoDegrees = 20.0;
    public const double UsableHiDegrees = 160.0;

    /// <summary>
    /// The band ratio ONE line separation can cover — 160/20 = 8, straight off the usable interval.
    ///
    /// <para><b>R-prt-6, MEASURED, and it settles the brief's own open question: two standards do NOT
    /// suffice for a 2–20 GHz sweep.</b> That is a 10:1 band against an 8:1 interval, so no single Δℓ
    /// exists — aiming 90° at the geometric mean puts the edges at 28° and 285°, and the measured
    /// run reads 59.7° / 122.5° / 345.4° at 2 / 6 / 20 GHz with the 20 GHz point 6.75e-2 wrong in β
    /// while the other two are 2.5e-4 and 7.8e-4. The number of separations is therefore DERIVED from
    /// the band rather than fixed, and the answer for 2–20 GHz is two of them (three standards).</para>
    /// </summary>
    public const double BandRatioPerSeparation = UsableHiDegrees / UsableLoDegrees;

    /// <summary>
    /// What the separation COUNT is actually derived from — half the theoretical 8:1, and the halving
    /// is measured rather than nervous. Δℓ is fixed before any solve from an ε_eff estimate that runs
    /// 15–20% low, so a separation lands ~1.17× higher in βΔℓ than it was aimed; designing to the
    /// full 8:1 leaves no room for that and puts the top of a 5:1 band at 157°, one point from the
    /// edge. Designing to 4:1 costs one extra standard mesh and puts the same band at 105°.
    /// </summary>
    public const double DesignBandRatioPerSeparation = 4.0;

    /// <summary>
    /// <b>QSC — where the MEASURED calibration stops being worth its cost, as a normalised
    /// frequency: h·√(εᵣ−1)/λ₀.</b> That grouping is the classical one for microstrip dispersion —
    /// it is what makes one number cover a 1.6 mm FR-4 board and a 0.1 mm MMIC — and
    /// <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> is the only place it is read.
    ///
    /// <para><b>0.03, fitted to M1's own measured tables and not to a rule of thumb.</b> Quasi-static
    /// γ was compared against the two-line γ per frequency on three stacks at two mesh densities, and
    /// the ~1 % crossover came out at ≈ 3 GHz on 1.6 mm FR-4 (ratio 0.0295) and ≈ 10 GHz on the
    /// 0.6 mm board the series was reported on (0.0347). 0.03 sits at the conservative end of that
    /// pair, which is the right end: below the crossover the quasi-static path is not merely cheaper
    /// but MORE accurate (the two-line value at 100 MHz is 7 % out on a refined mesh and 17.8 % out
    /// on the default one, moving toward the quasi-static answer as the mesh tightens), while above
    /// it real dispersion is what the measured calibration exists to capture. Tables in
    /// <c>src/Engine/Mom/RESOLVED.md</c> §QSC.</para>
    ///
    /// <para><b>It is deliberately NOT a user setting.</b> A knob here invites someone to put it in
    /// the wrong place and there is no way for them to know they have: both failure modes publish a
    /// smooth, plausible, wrong phase.</para>
    /// </summary>
    public const double DispersionCrossoverRatio = 0.03;

    /// <summary>
    /// <b>QSC — the SECOND crossover limit, and it exists because the first one is infinite on a
    /// homogeneous substrate.</b> <c>h/λ₀</c>, an absolute electrical thickness, and the crossover is
    /// the LOWER of the two.
    ///
    /// <para><see cref="DispersionCrossoverRatio"/> measures DIELECTRIC dispersion — ε_eff climbing
    /// from (εᵣ+1)/2 toward εᵣ — and at εᵣ = 1 there is none, so that term alone says "quasi-static is
    /// exact at every frequency". <b>For the MODE that is true and for the STRUCTURE it is not:</b> a
    /// coplanar pair 5 mm above its plane in air at 10 GHz is a sixth of a free-space wavelength
    /// thick, radiates, and its measured β sits 3.6 % off k₀ — which the two-line calibration
    /// captures and a quasi-TEM γ cannot. That is not a hypothetical; it is what
    /// <c>CoplanarDeembedTests</c> measured the moment the first term was allowed to answer alone.</para>
    ///
    /// <para><b>0.02, and it is chosen to be INERT on every stack M1 measured</b> — the dispersion
    /// term binds first on FR-4 (3.05 against 3.75 GHz), on the reported board (8.65 against 9.99 GHz)
    /// and on GaAs (26.1 against 60 GHz) — so it moves no number this brief recorded. It binds only as
    /// εᵣ → 1, where the other term runs away. Being a MINIMUM it can only LOWER a crossover, i.e.
    /// only ever move a point from the quasi-static path back onto the measured one, which is the
    /// conservative direction.</para>
    /// </summary>
    public const double ElectricalThicknessCrossoverRatio = 0.02;
}

/// <summary>One synthesised uniform line: its mesh, its two ports, and the length between the two
/// reference planes (which is what γ multiplies, not the drawn length).</summary>
/// <param name="ModePotential">
/// <b>RP-2c — the electrostatic problem D7's C_pul is taken from, per CELL, or null for a
/// single-conductor standard.</b>
///
/// <para>A microstrip standard has one conductor and one answer: put the whole sheet at 1 V above
/// the plane and total its charge. A coplanar standard has two, and the port drives the voltage
/// BETWEEN them — so the electrostatic problem is the signal conductor at +½ V and the return at
/// −½ V, and the capacitance that belongs in <c>Z_c = γ/(jωC_pul)</c> is the one that mode sees.
/// Putting a coplanar standard's whole sheet at 1 V measures the COMMON mode instead: a complete,
/// plausible reference impedance for a mode the port does not drive.</para>
/// </param>
/// <param name="ModeWeight">
/// What <c>C = Σ wᵢqᵢ</c> sums, per cell — <c>+½</c> on the signal conductor and <c>−½</c> on the
/// return, so the answer is <c>(Q⁺ − Q⁻)/2</c> per volt. <b>The average rather than one plate's
/// charge, deliberately:</b> with a ground plane present the two are not exactly equal and opposite,
/// and the average is the reading that does not depend on which conductor the user happened to name
/// as the return.
/// </param>
/// <param name="FloatingPotential">
/// <b>PCAL3 — the cells of a conductor that is in the standard but connected to nothing.</b> Its net
/// charge is zero and its potential is an unknown, which is a CONSTRAINT and cannot be expressed as
/// an entry of <paramref name="ModePotential"/>; <see cref="PlanarDeembed.StaticCapacitance"/> spans
/// it with one further right-hand side. Null on every standard but a widened one.
/// </param>
/// <param name="GroupPorts">
/// <b>PCAL4 — the 2N ports of a calibration GROUP's standard</b>, conductors 1..N at the low end
/// followed by the same conductors at the high end, and null on every one-conductor standard. The
/// block split <see cref="PlanarModalCalibration.SToT"/> takes is exactly that ordering, so the
/// first N ports ARE one side of the 2N-port by construction rather than by a convention someone
/// has to remember.
/// </param>
/// <param name="ConductorOfCell">
/// <b>PCAL4 — which conductor of the group each cell belongs to, or −1.</b> The reference impedance
/// of a group is a capacitance MATRIX, and a matrix needs to know which charge belongs to which
/// conductor; a single-conductor standard totals the whole sheet and carries none of this.
/// </param>
public sealed record PlanarStandard(
    PlanarMesh           Mesh,
    PlanarPortResolution Port1,
    PlanarPortResolution Port2,
    double               LengthM,
    int                  EndRunCells,
    IReadOnlyList<double>? ModePotential = null,
    IReadOnlyList<double>? ModeWeight    = null,
    IReadOnlyList<double>? FloatingPotential = null,
    IReadOnlyList<PlanarPortResolution>? GroupPorts = null,
    IReadOnlyList<int>?  ConductorOfCell = null)
{
    public IReadOnlyList<PlanarPortResolution> Ports => GroupPorts ?? [Port1, Port2];

    /// <summary>PCAL4 — how many conductors this standard carries; 1 on everything else.</summary>
    public int ConductorCount => GroupPorts is null ? 1 : GroupPorts.Count / 2;

    /// <summary>PCAL4 — is this a calibration GROUP's standard, i.e. a 2N-port?</summary>
    public bool IsGroup => GroupPorts is not null;

    /// <summary>RP-2c — whether this standard is a coplanar pair rather than one conductor over the
    /// plane. The two differ in their MESH and in their electrostatics; nothing in the calibration
    /// ALGEBRA asks (R-rp2c-3).</summary>
    public bool IsCoplanar => ModePotential is not null;
}

public static class PlanarCalibration
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D4 — building the standard
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>D4's longitudinal half: end run, bulk fill, mirrored end run</b> — the cumulative
    /// gridlines along the port's own axis, with the length rounded UP to a whole number of bulk
    /// cells.
    ///
    /// <para>Shared by the microstrip and the coplanar builders because it is the same partition:
    /// both conductors of a coplanar pair are cells of ONE mesh, so they share every longitudinal
    /// gridline, and a second copy of this arithmetic is a second chance for the two standards to
    /// discretise a length differently. The operations and their order are L8d's own, so a
    /// single-conductor standard's coordinates are bit-identical to what it built before RP-2c.</para>
    /// </summary>
    private static double[] LongitudinalPartition(PlanarPortResolution port, double targetLengthM,
                                                  int endRunCells)
    {
        if (endRunCells < 1)
            throw new ArgumentOutOfRangeException(nameof(endRunCells),
                "A standard needs at least the port's own outer cell reproduced.");
        if (port.LongitudinalRunM.Count < endRunCells)
            throw new InvalidOperationException(
                $"Port {port.Number}'s feed is only {port.LongitudinalRunM.Count} cell(s) long, and the " +
                $"calibration standard has to reproduce {endRunCells} of them. Lengthen the feed line: " +
                "the de-embedding replaces exactly that much of it, so the structure has to have it.");

        var sizes = new List<double>();
        double endLen = 0;
        for (int k = 0; k < endRunCells; k++) { sizes.Add(port.LongitudinalRunM[k]); endLen += port.LongitudinalRunM[k]; }

        // The reference planes sit one cell in from each end (D2), so the length BETWEEN them is the
        // whole line minus the two outer cells. Solve for the number of bulk cells that reaches the
        // target, and report what was actually built rather than what was asked for.
        double bulk    = port.BulkCellM;
        double covered = 2 * (endLen - port.LongitudinalRunM[0]);
        int    fill    = Math.Max(0, (int)Math.Ceiling((targetLengthM - covered) / bulk));

        // Four cells is the floor: fewer and the two ports' rooftop rows are not disjoint, so the
        // "line between the planes" the algebra assumes does not exist.
        fill = Math.Max(fill, 4 - 2 * endRunCells);

        for (int k = 0; k < fill; k++) sizes.Add(bulk);
        for (int k = endRunCells - 1; k >= 0; k--) sizes.Add(port.LongitudinalRunM[k]);

        var gLong = new double[sizes.Count + 1];
        for (int i = 0; i < sizes.Count; i++) gLong[i + 1] = gLong[i] + sizes[i];
        return gLong;
    }

    /// <summary>
    /// <b>RP-2c/R-rp2c-1 — the standard for a port that returns through drawn metal: BOTH
    /// conductors, the slot between them, and the DUT's own transverse gridlines verbatim.</b>
    ///
    /// <para>D4's rule is unchanged and is the whole point — the standard must rebuild the port's
    /// neighbourhood exactly — and the neighbourhood is now more than one piece of metal. So the
    /// transverse partition is <see cref="PlanarPortCrossSection.Lines"/> (the DUT's own gridlines
    /// at the cut, trimmed to the outermost metal) and a cell exists only where that profile says
    /// metal. A basis needs both of its cells, so no basis crosses the slot — which is the mesh's
    /// own structural property, measured in <c>CoplanarSlotMeshTests</c> and not something this
    /// builder has to arrange.</para>
    ///
    /// <para><b>The standard's own two ports are two-cut ports, at the same station (R-rp2c-2).</b>
    /// They are placed by transverse coordinate and resolved through <see cref="PlanarPorts"/> — the
    /// one resolution rule, including its skew, level and same-conductor checks — rather than
    /// assembled here. A standard whose two cuts were skewed would calibrate out a length that is
    /// not in the DUT, and the check that says so already exists.</para>
    /// </summary>
    public static PlanarStandard BuildCoplanarLine(PlanarPortResolution port, double targetLengthM,
                                                   int endRunCells, string layerName = DefaultLayerName)
    {
        ArgumentNullException.ThrowIfNull(port);
        var xs = port.CrossSection
               ?? throw new InvalidOperationException(
                      $"Port {port.Number} has no cross-section, so it is not a conductor-referenced " +
                      "edge port and BuildLine is what builds its standard.");

        var gLong = LongitudinalPartition(port, targetLengthM, endRunCells);
        var gTran = xs.Lines.ToArray();

        bool alongX = port.Direction == PlanarBasisDirection.X;

        // R-msh-2's (LayerIndex, IY, IX) order, as the single-conductor builder emits it — with the
        // void intervals skipped, which is the only difference. PCAL3 shares the builder rather than
        // keeping a second copy of it; the cells and bases it emits here are the ones this method
        // emitted before, in the same order.
        var (mesh, cells) = BuildProfileMesh(port.Direction, gLong, gTran,
                                             (_, t) => xs.IsMetal[t], layerName);

        var (sideLo, sideHi) = alongX
            ? (PlanarPortSide.MinX, PlanarPortSide.MaxX)
            : (PlanarPortSide.MinY, PlanarPortSide.MaxY);

        EmPoint Pt(double along, double across) =>
            alongX ? new EmPoint(along, across) : new EmPoint(across, along);

        PlanarPort Std(int number, double along, PlanarPortSide side) =>
            new(number, Pt(along, xs.PositiveCentreM), side, port.Z0,
                Reference: port.Reference, Kind: PlanarPortKind.Edge,
                NegativeLocation: Pt(along, xs.NegativeCentreM));

        var p1 = PlanarPorts.Resolve(mesh, Std(1, gLong[0],  sideLo));
        var p2 = PlanarPorts.Resolve(mesh, Std(2, gLong[^1], sideHi));

        // ── D7's electrostatic problem, per cell: +½ V on the signal conductor, −½ on the return ──
        var v = new double[cells.Count];
        var w = new double[cells.Count];
        for (int c = 0; c < cells.Count; c++)
        {
            int t = alongX ? cells[c].IY : cells[c].IX;
            double sign = t >= xs.PositiveLo && t <= xs.PositiveHi ? +1.0
                        : t >= xs.NegativeLo && t <= xs.NegativeHi ? -1.0
                        : 0.0;
            v[c] = 0.5 * sign;
            w[c] = 0.5 * sign;
        }

        return new PlanarStandard(mesh, p1, p2, p2.ReferencePlaneM - p1.ReferencePlaneM, endRunCells,
                                  v, w);
    }

    /// <summary>
    /// <b>The placeholder level name a standard carries when no caller supplies the port's own.</b>
    /// A standard's mesh has exactly one conductor level and numbers it 0 whatever level the port
    /// sits on, so <see cref="PlanarConductorLoss.SheetTable"/> resolves its metal by NAME with an
    /// index-0 fallback — which is the right answer for a single-level problem by either route, and
    /// is why the placeholder is harmless there and why the real name has to be passed otherwise.
    /// </summary>
    public const string DefaultLayerName = "Metal";

    /// <summary>
    /// A uniform line of the port's own cross-section, at least <paramref name="targetLengthM"/>
    /// between reference planes. The actual length is rounded UP to a whole number of bulk cells and
    /// is reported on the result — the requested length is never assumed.
    /// </summary>
    public static PlanarStandard BuildLine(PlanarPortResolution port, double targetLengthM,
                                           int endRunCells, string layerName = DefaultLayerName,
                                           PlanarCalibrationSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(port);

        // ── PCAL4 — A FEED IN A CALIBRATION GROUP GETS EVERY CONDUCTOR OF THE GROUP, DRIVEN ─────
        //
        // Decided at setup by PlanarPorts.TryFormCalibrationGroup, which is where the port list and
        // the threshold are; by the time the standard is built the answer is a fact on the
        // resolution. Null on every port that is not in a multi-conductor group, so this branch is
        // not taken on any run that passes today.
        if (port.Group is not null)
            return BuildGroupLine(port, targetLengthM, endRunCells, layerName);

        // ── PCAL3 — A FEED WITH A PASSIVE NEIGHBOUR GETS THE NEIGHBOUR IN ITS STANDARD ───────────
        //
        // The profile is widened at setup by PlanarPorts.TryWidenForNeighbours, which is where the
        // port list and the threshold are; by the time the standard is built the answer is a fact on
        // the resolution. Null on every port whose feed is clear, so this branch is not taken on any
        // run that passes today.
        if (port.Neighbourhood is not null)
            return BuildNeighbourLine(port, targetLengthM, endRunCells, layerName, settings);

        // ── RP-2c — A CONDUCTOR-REFERENCED PORT'S STANDARD IS A COPLANAR LINE ────────────────────
        //
        // Its neighbourhood is two pieces of metal with a slot between them, not one rectangle, and
        // calibrating it against a rectangle publishes s-parameters that are plausible and
        // referenced to nothing (RP-2's R-rp2-4). The LONGITUDINAL half of D4 is the same either
        // way and is shared below; what differs is the cross-section and the standard's own ports.
        if (port.CrossSection is not null)
            return BuildCoplanarLine(port, targetLengthM, endRunCells, layerName);

        var gLong = LongitudinalPartition(port, targetLengthM, endRunCells);
        var gTran = port.TransverseLines.ToArray();

        bool alongX = port.Direction == PlanarBasisDirection.X;
        var  gx     = alongX ? gLong : gTran;
        var  gy     = alongX ? gTran : gLong;

        // ── The cells, in R-msh-2's order: (LayerIndex, IY, IX), integers, no ties ───────────────
        int nx = gx.Length - 1, ny = gy.Length - 1;
        var cells = new List<PlanarCell>(nx * ny);
        var at    = new int[nx * ny];
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                at[iy * nx + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (ix + 1 < nx) bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[iy * nx + ix + 1], PlanarBasisDirection.X));
                if (iy + 1 < ny) bases.Add(new PlanarBasis(0, at[iy * nx + ix], at[(iy + 1) * nx + ix], PlanarBasisDirection.Y));
            }

        var mesh = new PlanarMesh(cells, bases, [layerName], gx, gy);

        // ── Its own two ports, at the same side conventions the DUT's port uses ──────────────────
        double tMid = 0.5 * (gTran[0] + gTran[^1]);
        var (sideLo, sideHi) = alongX
            ? (PlanarPortSide.MinX, PlanarPortSide.MaxX)
            : (PlanarPortSide.MinY, PlanarPortSide.MaxY);

        EmPoint Pt(double along) => alongX ? new EmPoint(along, tMid) : new EmPoint(tMid, along);

        var p1 = PlanarPorts.Resolve(mesh, new PlanarPort(1, Pt(gLong[0]),  sideLo, port.Z0));
        var p2 = PlanarPorts.Resolve(mesh, new PlanarPort(2, Pt(gLong[^1]), sideHi, port.Z0));

        return new PlanarStandard(mesh, p1, p2, p2.ReferencePlaneM - p1.ReferencePlaneM, endRunCells);
    }


    /// <summary>
    /// <b>PCAL3/R-pcal3-1 — the standard for a port whose feed has a PASSIVE neighbour: the port's
    /// own conductor, the gap, and the neighbour, all from the DUT's own transverse gridlines.</b>
    ///
    /// <para>D4's rule is unchanged and is again the whole point — the standard must rebuild the
    /// port's neighbourhood exactly — and the neighbourhood is now more than one piece of metal. The
    /// mesh is built exactly as <see cref="BuildCoplanarLine"/>'s is, from the same shared builder,
    /// because "a cell exists only where the profile says metal" is the same sentence in both cases
    /// and a second copy of it is a second chance for the two to disagree.</para>
    ///
    /// <para><b>What is NOT the same is who is driven.</b> The standard's two ports are single-cut
    /// ports on the port's own conductor, referenced to the ground plane exactly as the DUT's port
    /// is; the neighbour has no port in the standard because it has none in the DUT. That is what
    /// keeps this the CHEAP half of the series: one driven mode at the reference plane, and D6's
    /// per-port scalar error box still describes it.</para>
    ///
    /// <para><b>R-pcal3-3 — what the neighbour does at the standard's ends is a decision.</b> The
    /// DUT's neighbour carries on past the port; the standard's has to stop somewhere, and open,
    /// shorted and run-past are three different structures with three different error boxes. It is
    /// <see cref="PlanarCalibrationSettings.NeighbourExtensionCells"/>, it is measured rather than
    /// argued (the findings carry the numbers), and the one thing that is not negotiable is that
    /// BOTH standards of a pair treat it identically — otherwise D5's γ is measuring the difference
    /// between the two treatments rather than the length between them.</para>
    /// </summary>
    public static PlanarStandard BuildNeighbourLine(PlanarPortResolution port, double targetLengthM,
                                                    int endRunCells, string layerName = DefaultLayerName,
                                                    PlanarCalibrationSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        var nb = port.Neighbourhood
              ?? throw new InvalidOperationException(
                     $"Port {port.Number} has no widened profile, so BuildLine is what builds its " +
                     "standard.");

        var s     = settings ?? PlanarCalibrationSettings.Default;
        int ext   = Math.Max(0, s.NeighbourExtensionCells);
        var gCore = LongitudinalPartition(port, targetLengthM, endRunCells);

        // The neighbour's own ends, when they are asked to run past the port plane, are bulk cells of
        // the port's own line — the same cell size D4 fills the standard's middle with, so nothing
        // here introduces a discretisation the DUT does not have.
        var gLong = new double[gCore.Length + 2 * ext];
        double bulk = port.BulkCellM;
        for (int k = 0; k < gCore.Length; k++) gLong[ext + k] = gCore[k] + ext * bulk;
        for (int k = ext - 1; k >= 0; k--)     gLong[k] = gLong[k + 1] - bulk;
        for (int k = 0; k < ext; k++)          gLong[ext + gCore.Length + k] = gLong[ext + gCore.Length + k - 1] + bulk;

        int nLongCore = gCore.Length - 1;
        bool Present(int col, int t) =>
            nb.IsMetal[t] && (!nb.IsOwn(t) || (col >= ext && col < ext + nLongCore));

        var (mesh, cells) = BuildProfileMesh(port.Direction, gLong, nb.Lines, Present, layerName);

        bool alongX = port.Direction == PlanarBasisDirection.X;
        var (sideLo, sideHi) = alongX
            ? (PlanarPortSide.MinX, PlanarPortSide.MaxX)
            : (PlanarPortSide.MinY, PlanarPortSide.MaxY);

        EmPoint Pt(double along) =>
            alongX ? new EmPoint(along, nb.OwnCentreM) : new EmPoint(nb.OwnCentreM, along);

        var p1 = PlanarPorts.Resolve(mesh, new PlanarPort(1, Pt(gLong[ext]),             sideLo, port.Z0));
        var p2 = PlanarPorts.Resolve(mesh, new PlanarPort(2, Pt(gLong[ext + nLongCore]), sideHi, port.Z0));

        // ── D7's electrostatic problem: the port drives its OWN conductor and nothing else ───────
        //
        // The whole sheet at 1 V — what a single-conductor standard takes — would put the neighbour
        // at the line's own potential and measure the capacitance of the two of them bonded
        // together: a complete, plausible reference impedance for a mode the port does not drive,
        // which is RP-2c's own failure one conductor over.
        // And the neighbour's own potential is an UNKNOWN, not a zero: it is connected to nothing, so
        // its NET charge is zero and it floats to whatever that implies. Grounding it instead is a
        // different structure — a ground pour — and the two readings are 12.3 % apart at s/h = 0.27
        // on the series' own fixture, which lands on Z_c and therefore on every published
        // s-parameter. PlanarDeembed.StaticCapacitance spans it with one extra right-hand side.
        var v = new double[cells.Count];
        var w = new double[cells.Count];
        var f = new double[cells.Count];
        for (int c = 0; c < cells.Count; c++)
        {
            int t = alongX ? cells[c].IY : cells[c].IX;
            bool own = nb.IsOwn(t);
            v[c] = w[c] = own ? 1.0 : 0.0;
            f[c] = own ? 0.0 : 1.0;
        }

        return new PlanarStandard(mesh, p1, p2, p2.ReferencePlaneM - p1.ReferencePlaneM, endRunCells,
                                  v, w, f);
    }

    /// <summary>
    /// <b>PCAL4/R-pcal4-1 — the standard for a calibration GROUP: every conductor of the group, the
    /// gaps between them, and a port on each conductor at each end.</b>
    ///
    /// <para>D4's rule is unchanged and is still the whole point — the standard rebuilds the port
    /// region exactly — and the region is now N conductors that are all DRIVEN. That is the one
    /// thing separating this from <see cref="BuildNeighbourLine"/>, whose extra conductor has no
    /// port because it has none in the DUT: here every conductor has one, so the standard is a
    /// 2N-port, the error box is <see cref="PlanarModalCalibration"/>'s N×N blocks, and the
    /// electrostatics is a capacitance MATRIX rather than one number with a floating constraint.</para>
    ///
    /// <para><b>Port order is conductors 1..N at the low end, then the same conductors at the high
    /// end</b>, which is the block split every piece of the modal algebra assumes. Both ends' cuts
    /// sit at the transverse centre of their own conductor and are resolved through
    /// <see cref="PlanarPorts"/> — the one resolution rule, including its skew, level and
    /// same-conductor checks — rather than assembled here.</para>
    ///
    /// <para><b>There is no neighbour extension setting here and there must not be one.</b> PCAL3's
    /// reproduced neighbour is open at both ends and therefore a resonator, which is what
    /// <c>NeighbourExtensionCells</c> exists to measure; a group's conductors are all driven from
    /// their own ports, exactly as the DUT's are, so the structure has no undriven stub in it at
    /// all.</para>
    /// </summary>
    public static PlanarStandard BuildGroupLine(PlanarPortResolution port, double targetLengthM,
                                                int endRunCells, string layerName = DefaultLayerName)
    {
        ArgumentNullException.ThrowIfNull(port);
        var g = port.Group
             ?? throw new InvalidOperationException(
                    $"Port {port.Number} is not in a calibration group, so BuildLine is what builds " +
                    "its standard.");

        var gLong = LongitudinalPartition(port, targetLengthM, endRunCells);
        int n = g.ConductorCount;

        var (mesh, cells) = BuildProfileMesh(port.Direction, gLong, g.Lines,
                                             (_, t) => g.ConductorOf[t] >= 0, layerName);

        bool alongX = port.Direction == PlanarBasisDirection.X;
        var (sideLo, sideHi) = alongX
            ? (PlanarPortSide.MinX, PlanarPortSide.MaxX)
            : (PlanarPortSide.MinY, PlanarPortSide.MaxY);

        EmPoint Pt(double along, double across) =>
            alongX ? new EmPoint(along, across) : new EmPoint(across, along);

        var ports = new PlanarPortResolution[2 * n];
        for (int k = 0; k < n; k++)
        {
            double c = g.CentreM(k);
            ports[k]     = PlanarPorts.Resolve(mesh, new PlanarPort(k + 1,     Pt(gLong[0],  c), sideLo, port.Z0));
            ports[n + k] = PlanarPorts.Resolve(mesh, new PlanarPort(n + k + 1, Pt(gLong[^1], c), sideHi, port.Z0));
        }

        // Which conductor each cell belongs to — D7's capacitance MATRIX is assembled against this.
        var conductorOfCell = new int[cells.Count];
        for (int c = 0; c < cells.Count; c++)
            conductorOfCell[c] = g.ConductorOf[alongX ? cells[c].IY : cells[c].IX];

        return new PlanarStandard(
            mesh, ports[0], ports[n], ports[n].ReferencePlaneM - ports[0].ReferencePlaneM,
            endRunCells, GroupPorts: ports, ConductorOfCell: conductorOfCell);
    }

    /// <summary>
    /// The mesh of a profiled standard: a tensor grid with a cell only where the profile says metal,
    /// and a rooftop only where both of a pair's cells exist. Shared by the coplanar builder and
    /// PCAL3's widened one — R-msh-2's (LayerIndex, IY, IX) ordering is honoured by construction,
    /// because the loops emit cells in exactly that order.
    /// </summary>
    private static (PlanarMesh Mesh, List<PlanarCell> Cells) BuildProfileMesh(
        PlanarBasisDirection direction, double[] gLong, IReadOnlyList<double> gTranLines,
        Func<int, int, bool> present, string layerName)
    {
        bool alongX = direction == PlanarBasisDirection.X;
        var  gTran  = gTranLines is double[] a ? a : [.. gTranLines];
        var  gx     = alongX ? gLong : gTran;
        var  gy     = alongX ? gTran : gLong;

        int nx = gx.Length - 1, ny = gy.Length - 1;
        var cells = new List<PlanarCell>(nx * ny);
        var at    = new int[nx * ny];
        Array.Fill(at, -1);

        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (!present(alongX ? ix : iy, alongX ? iy : ix)) continue;
                at[iy * nx + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                int c = at[iy * nx + ix];
                if (c < 0) continue;
                if (ix + 1 < nx && at[iy * nx + ix + 1] >= 0)
                    bases.Add(new PlanarBasis(0, c, at[iy * nx + ix + 1], PlanarBasisDirection.X));
                if (iy + 1 < ny && at[(iy + 1) * nx + ix] >= 0)
                    bases.Add(new PlanarBasis(0, c, at[(iy + 1) * nx + ix], PlanarBasisDirection.Y));
            }

        return (new PlanarMesh(cells, bases, [layerName], gx, gy), cells);
    }

    /// <summary>
    /// How many of the DUT's own cells the standard must reproduce so that the port's evanescent
    /// field is entirely inside the identical region. Derived from the substrate height, clamped to
    /// what the feed actually has.
    /// </summary>
    public static int EndRunCellsFor(PlanarPortResolution port, GroundedSlab slab,
                                     PlanarCalibrationSettings? settings = null)
    {
        var s = settings ?? PlanarCalibrationSettings.Default;
        double want = s.EndRunHeights * slab.HeightM;

        double acc = 0;
        int k = 0;
        while (k < port.LongitudinalRunM.Count && acc < want) acc += port.LongitudinalRunM[k++];
        return Math.Clamp(k, 1, Math.Max(1, port.LongitudinalRunM.Count));
    }

    /// <summary>
    /// The short standard's target length and the SEPARATION between the two, for a band. Δℓ is
    /// aimed at <see cref="PlanarCalibrationSettings.TargetElectricalDegrees"/> at the band's
    /// GEOMETRIC mean, which is what centres the usable interval on a log-frequency sweep; the
    /// realised electrical lengths are reported afterwards from the measured γ, never from this
    /// estimate (R-prt-6).
    ///
    /// <para><b>The second return value is a SEPARATION, not a second length, and that is a
    /// measured correction rather than a stylistic choice.</b> Returning two absolute lengths is the
    /// obvious API and it silently breaks the calibration: <see cref="BuildLine"/> has a floor — a
    /// standard cannot be shorter than its two end runs — so a short target gets inflated while the
    /// long one does not, and Δℓ comes out at half what was asked for. Measured on the 20 mm FR-4
    /// fixture at 2 GHz: a requested Δℓ of 7.2 mm was realised as 3.58 mm, dropping βΔℓ from 31° to
    /// 15.6° and out of the usable interval. The caller must build the short line FIRST and take Δℓ
    /// from its ACTUAL length.</para>
    /// </summary>
    public static (double Short, double Delta) SuggestLengths(
        GroundedSlab slab, double fLoHz, double fHiHz, PlanarCalibrationSettings? settings = null)
    {
        var s = settings ?? PlanarCalibrationSettings.Default;
        double fGm = Math.Sqrt(Math.Max(fLoHz, 1.0) * Math.Max(fHiHz, 1.0));
        return (s.ShortLineHeights * slab.HeightM, DeltaAt(slab, fGm, s));
    }

    /// <summary>
    /// The line SEPARATIONS a band needs — one per <see cref="PlanarCalibrationSettings
    /// .BandRatioPerSeparation"/> of band, geometrically spaced so each covers its own octave-and-a-
    /// bit with βΔℓ centred on the usable interval. A narrow band gets one; 2–20 GHz gets two.
    /// </summary>
    /// <param name="betaAt">SI-LOW — β of this port's line at a frequency, where the substrate conducts enough
    /// that εᵣ does not say it (<see cref="PlanarSubstrateReturn.EstimatedBetaAt"/>). Null reads εᵣ alone.</param>
    public static double[] SuggestDeltas(GroundedSlab slab, double fLoHz, double fHiHz,
                                         PlanarCalibrationSettings? settings = null,
                                         Func<double, double>? betaAt = null)
    {
        var s = settings ?? PlanarCalibrationSettings.Default;
        double lo = Math.Max(Math.Min(fLoHz, fHiHz), 1.0);
        double hi = Math.Max(Math.Max(fLoHz, fHiHz), 1.0);

        double ratio = hi / lo;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Log(ratio) /
                                              Math.Log(PlanarCalibrationSettings.DesignBandRatioPerSeparation)));

        var deltas = new double[n];
        for (int k = 0; k < n; k++)
        {
            // Each separation is centred on its own sub-band's geometric mean.
            double f = lo * Math.Pow(ratio, (k + 0.5) / n);
            deltas[k] = DeltaAt(slab, f, s, betaAt);
        }
        return deltas;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // QSC — the crossover, and the one short separation below it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Below this frequency a port is calibrated with a QUASI-STATIC γ and two SHORT standards;
    /// above it, with D5's measured γ and the λ-scaled ladder.</b>
    /// <b>The LOWER of two limits</b> —
    /// <c>DispersionCrossoverRatio·c / (h√(εᵣ−1))</c> and
    /// <c>ElectricalThicknessCrossoverRatio·c / h</c> — each of which has its own note; the first
    /// carries the fit and the measured tables, the second says why one term is not enough.
    ///
    /// <para><b>The two paths are not interchangeable and neither is a fallback for the other.</b>
    /// Above the crossover the disagreement is REAL DISPERSION and the measured calibration is the
    /// one that is right; below it the disagreement is the MEASURED value being wrong, which M1
    /// proved by refinement rather than asserting — on the reported board the two-line ε_eff at
    /// 100 MHz reads 2.02 on the default mesh and 2.45 on a refined one against a static 2.786 it
    /// must approach as f → 0.</para>
    ///
    /// <para><b>An AIR substrate does not get "+∞", and that is the whole reason there are two
    /// terms.</b> The dispersion term alone says so — correctly about the MODE, since every bound mode
    /// of a homogeneous medium is exactly TEM — and wrongly about the STRUCTURE, which can still be a
    /// sixth of a wavelength thick and radiating. See
    /// <see cref="PlanarCalibrationSettings.ElectricalThicknessCrossoverRatio"/>.</para>
    /// </summary>
    public static double QuasiStaticCrossoverHz(GroundedSlab slab)
    {
        ArgumentNullException.ThrowIfNull(slab);
        if (!(slab.HeightM > 0)) return double.PositiveInfinity;

        // THE LOWER OF TWO LIMITS, and the second is not a belt-and-braces addition — without it a
        // homogeneous substrate (εᵣ = 1) answers "+∞" and hands the quasi-static path a structure
        // that is a sixth of a wavelength thick. See ElectricalThicknessCrossoverRatio.
        double disp = slab.HeightM * Math.Sqrt(Math.Max(slab.Material.EpsR - 1.0, 0.0));
        double byDispersion = disp > 0
            ? PlanarCalibrationSettings.DispersionCrossoverRatio * EmConstants.C0 / disp
            : double.PositiveInfinity;
        double byThickness =
            PlanarCalibrationSettings.ElectricalThicknessCrossoverRatio * EmConstants.C0 / slab.HeightM;

        return Math.Min(byDispersion, byThickness);
    }

    /// <summary>
    /// <b>The ONE separation the quasi-static path uses, whatever the band</b> — sized from the
    /// substrate height and floored at a whole number of the port's own bulk cells. Frequency does
    /// not appear, which is the entire saving: <see cref="SuggestDeltas"/>'s λ-scaled ladder is what
    /// makes a 100 MHz lower edge cost a 161.5 mm standard, and there is no βΔℓ window left to cover
    /// once γ is supplied.
    /// </summary>
    /// <param name="port">The port whose bulk cell floors the answer, or null to take the substrate
    /// term alone — which is what <see cref="PlanarSolve"/>'s ceiling refusal wants, since it is
    /// sizing a message rather than a mesh.</param>
    public static double QuasiStaticSeparationM(GroundedSlab slab, PlanarPortResolution? port,
                                                PlanarCalibrationSettings? settings = null,
                                                Func<double, double>? betaAt = null)
    {
        ArgumentNullException.ThrowIfNull(slab);
        var s = settings ?? PlanarCalibrationSettings.Default;

        // ── AND IT IS CAPPED FROM ABOVE, WHICH IS THE OPPOSITE OF THE OBVIOUS WORRY ──────────
        //
        // M3 swept Δℓ from 24 substrate heights down to one bulk cell and the error box degrades
        // GENTLY going DOWN (max|ΔS| against the measured reference at 1 GHz: 1.2e-3 at 24 h,
        // 6.0e-4 at 6 h, 1.7e-3 at one bulk cell) and BREAKS going UP: at 24 h the separation
        // reaches βΔℓ = 170° at 2 GHz, which is D6's own denominator zero at βΔℓ = nπ, and the a₂₂
        // sign margin collapses from ~1e3 to 25. A supplied γ frees Δℓ from the interval's LOWER
        // end — that is the whole point — and leaves its upper end exactly where it was.
        //
        // So the substrate term is capped at the same electrical length `SuggestDeltas` aims a
        // measured separation at, evaluated at the CROSSOVER, which is the highest frequency this
        // separation ever serves. The h cancels: βΔℓ there is a pure function of εᵣ, ≈ 58° on FR-4
        // and ≈ 50° on GaAs, so the cap is inert on any ordinary board and binds only on a
        // substrate close enough to air that the crossover runs away.
        double want = Math.Min(s.QuasiStaticSeparationHeights * slab.HeightM,
                               DeltaAt(slab, QuasiStaticCrossoverHz(slab), s, betaAt));

        // The mesh has the last word, and it would have it anyway: BuildLine realises a separation
        // as a whole number of bulk cells, so a target under one cell simply becomes one cell. This
        // makes that floor explicit and puts it where D7's capacitance DIFFERENCING needs it —
        // C_pul is (C₂ − C₁)/Δℓ, and two standards differing by one cell difference by one cell's
        // worth of discretisation error as well.
        //
        // SI-LOW — but a slow wave can make those cells electrically long, and this floor then undoes the
        // cap above: on 1 Ω·cm at 20 GHz, a point run alone meshes coarsely and four cells read 159°, on the
        // way to D6's zero. Where β is known the floor stops at twice the angle a separation is aimed at.
        if (port is not null)
        {
            double floor = s.QuasiStaticSeparationMinBulkCells * port.BulkCellM;
            if (betaAt is not null) floor = Math.Min(floor, 2.0 * DeltaAt(slab, QuasiStaticCrossoverHz(slab), s, betaAt));
            want = Math.Max(want, floor);
        }
        return want;
    }

    /// <summary>
    /// <b>QSC — the separations a band actually builds, and which of them is the quasi-static one.</b>
    /// </summary>
    /// <param name="DeltaLM">The separations, measured ladder first and the quasi-static one (if any)
    /// LAST — so a band entirely above the crossover is <see cref="SuggestDeltas"/>'s own array,
    /// index for index, and every such run is bit-identical to the one that shipped.</param>
    /// <param name="QuasiStaticIndex">Which entry is the short, frequency-independent one, or −1 when
    /// the whole band sits above the crossover and there is none.</param>
    /// <param name="CrossoverHz">The crossover this plan was drawn at, carried so a caller reporting
    /// which path a point took does not recompute it.</param>
    public readonly record struct PlanarSeparationPlan(
        double[] DeltaLM, int QuasiStaticIndex, double CrossoverHz)
    {
        /// <summary>Whether <paramref name="fHz"/> is calibrated quasi-statically under this plan.
        /// <b>The one place the question is asked</b> — <see cref="PlanarPortCalibrator"/>'s own
        /// separation choice, its γ source and the run's diagnostics all read it, and a second
        /// spelling would let a run solve one standard and calibrate against another.</summary>
        public bool IsQuasiStaticAt(double fHz) => QuasiStaticIndex >= 0 && fHz < CrossoverHz;
    }

    /// <summary>
    /// <b>The plan for a band: the measured ladder over the part of it ABOVE the crossover, plus one
    /// short separation for the part below.</b>
    ///
    /// <para><b><see cref="SuggestDeltas"/> is called with the EFFECTIVE lower edge, not the user's
    /// one, and that is the whole of the change.</b> A separation exists to cover a sub-band, and
    /// below the crossover no measured separation is used at all — so building the ladder from the
    /// user's f_lo would mesh (and, at the bottom, solve) standards that no frequency will ever
    /// read. That is what a 100 MHz–6 GHz sweep was paying 79,055 unknowns for.</para>
    ///
    /// <para>Three shapes, and the first is the one that guarantees nothing that ships today moves:
    /// a band entirely at or above the crossover gets <see cref="SuggestDeltas"/>'s array unchanged
    /// and no quasi-static entry; a band entirely below it gets ONE separation and no measured
    /// ladder at all; a band spanning it gets both.</para>
    /// </summary>
    /// <param name="betaAt">SI-LOW — see <see cref="SuggestDeltas"/>.</param>
    public static PlanarSeparationPlan SeparationPlan(
        GroundedSlab slab, double fLoHz, double fHiHz, PlanarPortResolution? port = null,
        PlanarCalibrationSettings? settings = null, Func<double, double>? betaAt = null)
    {
        ArgumentNullException.ThrowIfNull(slab);
        var st = settings ?? PlanarCalibrationSettings.Default;
        double lo = Math.Max(Math.Min(fLoHz, fHiHz), 1.0);
        double hi = Math.Max(Math.Max(fLoHz, fHiHz), 1.0);
        double cross = st.QuasiStaticBelowCrossover ? QuasiStaticCrossoverHz(slab) : 0.0;

        // The pre-QSC answer, bit for bit — see QuasiStaticBelowCrossover. A zero crossover makes
        // `IsQuasiStaticAt` false everywhere as well, so the switch is one comparison and not a
        // second branch each caller has to remember.
        if (!st.QuasiStaticBelowCrossover)
            return new PlanarSeparationPlan(SuggestDeltas(slab, lo, hi, settings, betaAt), -1, cross);

        // ── A CALIBRATION GROUP STAYS ON THE MEASURED LADDER, AND THAT IS A DECLINE BY NAME ──
        //
        // PCAL4's modal error box extracts N propagation constants from a 2N-port cascade
        // eigenproblem, and what separates the modes there is the DIFFERENCE of their electrical
        // lengths over Δℓ — `ModeSeparationFloorDegrees`, 0.5°, asked at setup and again per point.
        // A separation sized from the substrate rather than from λ drives every one of those
        // differences toward zero at the bottom of a band, so the quasi-static path would turn
        // PCAL4's refusal from a rare event into the normal case. Supplying the modal γ's
        // quasi-statically as well would remove that objection — `PlanarModalMedium` already
        // computes them — but it is a different error box and a different measurement, and it is
        // not this brief's.
        if (port?.Group is not null)
            return new PlanarSeparationPlan(SuggestDeltas(slab, lo, hi, settings), -1, cross);

        if (lo >= cross)
            return new PlanarSeparationPlan(SuggestDeltas(slab, lo, hi, settings, betaAt), -1, cross);

        double qs = QuasiStaticSeparationM(slab, port, settings, betaAt);

        if (hi <= cross) return new PlanarSeparationPlan([qs], 0, cross);

        var measured = SuggestDeltas(slab, cross, hi, settings, betaAt);
        var all = new double[measured.Length + 1];
        Array.Copy(measured, all, measured.Length);
        all[^1] = qs;
        return new PlanarSeparationPlan(all, measured.Length, cross);
    }

    /// <summary>
    /// <b>The lower edge the MEASURED ladder is actually drawn from</b> — the user's, or the
    /// crossover, whichever is higher, clamped to the band. Every band-edge quantity
    /// (<see cref="LongestStandardLengthM"/>, <see cref="StartFrequencyThatFits"/>, and the refusal
    /// that prints them) has to be asked of THIS rather than of <c>fLoHz</c>, or it describes a
    /// regime the run no longer has and offers a remedy that cannot bind.
    /// </summary>
    public static double MeasuredBandBottomHz(GroundedSlab slab, double fLoHz, double fHiHz)
    {
        double lo = Math.Max(Math.Min(fLoHz, fHiHz), 1.0);
        double hi = Math.Max(Math.Max(fLoHz, fHiHz), 1.0);
        return Math.Min(Math.Max(lo, QuasiStaticCrossoverHz(slab)), hi);
    }

    /// <summary>
    /// <b>The longest standard this band asks for, as a LENGTH, without meshing anything.</b>
    ///
    /// <para>This is the quantity the mesh-ceiling refusal has to be able to name. A standard's
    /// unknown count is set by its length times the DUT's own transverse gridlines, and the length
    /// comes from <see cref="SuggestDeltas"/> — λ at each sub-band's geometric mean. So the binding
    /// input is the BOTTOM of the sweep, not the mesh and not the port's width, and a refusal that
    /// offers mesh remedies is offering the knob that moves this least. Measured on a 3.8 mm
    /// microstrip over 100 MHz–6 GHz: three separations, the longest standard 160 mm of line — 42×
    /// the DUT — and coarsening the DUT 4× cut the standard only 5.8×, still over the ceiling.</para>
    ///
    /// <para>Target lengths, not realised ones: <see cref="BuildLine"/>'s end-run floor inflates a
    /// short target. <see cref="StartFrequencyThatFits"/> only ever takes RATIOS of this, which is
    /// what cancels that bias rather than leaving it in an absolute estimate.</para>
    /// </summary>
    public static double LongestStandardLengthM(GroundedSlab slab, double fLoHz, double fHiHz,
                                                PlanarCalibrationSettings? settings = null,
                                                Func<double, double>? betaAt = null)
    {
        ArgumentNullException.ThrowIfNull(slab);
        var (shortTarget, _) = SuggestLengths(slab, fLoHz, fHiHz, settings);
        double max = 0;
        foreach (double d in SuggestDeltas(slab, fLoHz, fHiHz, settings, betaAt)) max = Math.Max(max, d);
        return shortTarget + max;
    }

    /// <summary>
    /// <b>The lowest band edge at which this port's standards would fit — the remedy the ceiling
    /// refusal has to name instead of "turn de-embedding off".</b>
    ///
    /// <para>Scaled from the standard that was ACTUALLY built rather than predicted from scratch:
    /// <paramref name="measuredN"/> unknowns at <paramref name="measuredLengthM"/> fixes the
    /// unknowns-per-metre for this port's own transverse mesh, and the candidate's count is that
    /// times <see cref="LongestStandardLengthM"/>'s ratio. Taking the ratio is what makes the
    /// target-vs-realised difference cancel; taking the absolute length would not.</para>
    ///
    /// <para>The scan runs UPWARD from the current lower edge and returns the first candidate that
    /// fits, because the separation COUNT is a step function of the band ratio — the length falls
    /// in jumps, not smoothly, and a bisection over a step function can settle above the first
    /// frequency that would have worked. Null when even a band starting at <paramref name="fHiHz"/>
    /// would not fit, which is the case where no band edge is the answer.</para>
    /// </summary>
    public static double? StartFrequencyThatFits(
        GroundedSlab slab, double fLoHz, double fHiHz, PlanarCalibrationSettings? settings,
        int ceiling, int measuredN, double measuredLengthM, Func<double, double>? betaAt = null)
    {
        ArgumentNullException.ThrowIfNull(slab);
        if (measuredN <= 0 || measuredLengthM <= 0 || ceiling <= 0) return null;

        double lNow = LongestStandardLengthM(slab, fLoHz, fHiHz, settings, betaAt);
        if (lNow <= 0) return null;

        const int Steps = 240;                       // ~1% resolution over a 60:1 band
        double lo = Math.Max(fLoHz, 1.0), hi = Math.Max(fHiHz, lo * 1.000001);
        for (int k = 1; k <= Steps; k++)
        {
            double f = lo * Math.Pow(hi / lo, (double)k / Steps);
            double predicted = measuredN * (LongestStandardLengthM(slab, f, fHiHz, settings, betaAt) / lNow);
            if (predicted <= ceiling) return f;
        }
        return null;
    }

    /// <summary>
    /// A frequency rounded UP onto the 1-2-5 ladder, so a refusal names a band edge someone would
    /// actually type. Up rather than to-nearest: this number is offered as one that FITS, and
    /// rounding it down past the estimate would hand back the same refusal.
    /// </summary>
    public static double RoundUpToTidyFrequency(double fHz)
    {
        if (!(fHz > 0) || double.IsInfinity(fHz)) return fHz;
        double decade = Math.Pow(10, Math.Floor(Math.Log10(fHz)));
        double m = fHz / decade;
        double tidy = m <= 1.0 ? 1.0 : m <= 2.0 ? 2.0 : m <= 5.0 ? 5.0 : 10.0;
        return tidy * decade;
    }

    private static double DeltaAt(GroundedSlab slab, double fHz, PlanarCalibrationSettings s,
                                  Func<double, double>? betaAt = null)
    {
        // SI-LOW — a conducting substrate's slow wave is not in εᵣ, and on 0.1 Ω·cm the crude estimate below
        // sat 3× low: a 60° target read past 180° and the extraction aliased onto −α.
        if (betaAt is not null) return s.TargetElectricalDegrees * Math.PI / 180.0 / betaAt(fHz);

        // ε_eff is not known before a solve; (εᵣ+1)/2 is the standard crude microstrip estimate, and
        // it only has to be right to a factor for βΔℓ to land inside an 8:1-wide usable interval.
        double epsEst = 0.5 * (slab.Material.EpsR + 1.0);
        double lambda = EmConstants.C0 / (fHz * Math.Sqrt(epsEst));
        return lambda * (s.TargetElectricalDegrees / 360.0);
    }

    /// <summary>
    /// The full standard set for a port and a band: element 0 is the short line, and each further
    /// element is the short line plus one of <see cref="SuggestDeltas"/>'s separations.
    ///
    /// <para>Δℓ is taken from the SHORT line's ACTUAL length, never from its target — see
    /// <see cref="SuggestLengths"/> for the measurement that made that necessary.</para>
    /// </summary>
    /// <param name="layerName">
    /// <b>CL1 — the name of the PROBLEM's conductor level this port sits on</b>, which is how the
    /// fill resolves the standard's metal. A standard's mesh carries one level, numbered 0 whatever
    /// level the port is actually on, so <see cref="PlanarConductorLoss.SheetTable"/> matches it by
    /// NAME and falls back to index 0 — and before this parameter existed the name was always the
    /// placeholder, so on a multi-level problem every standard was filled with level 0's σ and
    /// thickness however high the port sat. The default keeps every single-level call site
    /// bit-identical: one level resolves to index 0 by either route.
    /// </param>
    public static PlanarStandard[] BuildSet(
        PlanarPortResolution port, GroundedSlab slab, double fLoHz, double fHiHz,
        PlanarCalibrationSettings? settings = null, string layerName = DefaultLayerName,
        Func<double, double>? betaAt = null)
    {
        // QSC — the measured ladder over the band's ABOVE-crossover part, plus one short
        // frequency-independent separation for the part below it. Above the crossover throughout,
        // this IS SuggestDeltas' own array and the set built is the one that shipped.
        return BuildSet(port, slab, SeparationPlan(slab, fLoHz, fHiHz, port, settings, betaAt),
                        SuggestLengths(slab, fLoHz, fHiHz, settings).Short, settings, layerName);
    }

    /// <summary>
    /// <b>The same set, from a plan the caller already has.</b> <see cref="PlanarPortCalibrator"/>
    /// draws the plan once and hands it here, so the standards that are BUILT and the separations
    /// that are SELECTED cannot come from two evaluations of the same function — which is the defect
    /// PCAL6/R-pcal6-3 fixed one level up, and it is the same defect here.
    /// </summary>
    /// <param name="layerName">The PROBLEM's name for the conductor level this port sits on — see the
    /// band overload above for why a standard's metal is resolved by name and what the placeholder
    /// costs on a multi-level problem.</param>
    public static PlanarStandard[] BuildSet(
        PlanarPortResolution port, GroundedSlab slab, PlanarSeparationPlan plan,
        double shortTargetM, PlanarCalibrationSettings? settings = null,
        string layerName = DefaultLayerName)
    {
        int k = EndRunCellsFor(port, slab, settings);
        var deltas = plan.DeltaLM;

        var set = new PlanarStandard[deltas.Length + 1];
        set[0] = BuildLine(port, shortTargetM, k, layerName, settings);
        for (int i = 0; i < deltas.Length; i++)
            set[i + 1] = BuildLine(port, set[0].LengthM + deltas[i], k, layerName, settings);

        return set;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D5 — γ from the two standards
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the two-line step produced, plus what R-prt-6 has to report about it.</summary>
    /// <param name="ElectricalDegrees">βΔℓ in degrees — the number that decides whether this
    /// frequency is inside TRL's usable interval.</param>
    /// <param name="Unwrapped">How many 2π were added to the principal branch. Non-zero is fine;
    /// non-zero at the FIRST frequency of a sweep means Δℓ is already too long.</param>
    public sealed record GammaResult(Complex Gamma, double ElectricalDegrees, int Unwrapped, bool Usable)
    {
        public double Alpha => Gamma.Real;
        public double Beta  => Gamma.Imaginary;

        public double EffectivePermittivity(double fHz)
        {
            double b = Beta / (2.0 * Math.PI * fHz / EmConstants.C0);
            return b * b;
        }
    }

    /// <summary>
    /// γ from the two standards' raw s-parameters. <paramref name="expectedBetaDeltaL"/> is the
    /// previous frequency's βΔℓ scaled by frequency; pass NaN at the first point, where the branch is
    /// anchored on βΔℓ &lt; π instead.
    /// </summary>
    public static GammaResult Gamma(Mat<Complex> sShort, Mat<Complex> sLong, double deltaLM,
                                    double expectedBetaDeltaL = double.NaN)
    {
        if (!(deltaLM > 0)) throw new ArgumentOutOfRangeException(nameof(deltaLM));

        var m = RFNetwork.SToT2Port(sLong) * Inverse2(RFNetwork.SToT2Port(sShort));
        Complex w = 0.5 * (m[0, 0] + m[1, 1]);          // det M = 1, so this IS cosh(γΔℓ)

        // cosh is EVEN and 2πj-periodic, so the solutions are ±g₀ + 2πjk. Both families have to be
        // considered together.
        //
        // MEASURED, AND IT WAS A REAL BUG: the obvious rule — "negate g₀ if Re g₀ < 0, because a
        // passive line has α ≥ 0" — is WRONG, because negating flips β as well. On the FR-4 fixture
        // at 20 GHz the principal value came back as (−0.061, +2.2005) with a true αΔℓ of only
        // +0.016: α is two orders of magnitude smaller than β here, so its extracted SIGN is noise,
        // and flipping on it turned a correct β = 804 into 1492. β is the well-determined half, so
        // β is what selects the branch; Re is used only to break a tie when there is no prediction
        // to select against.
        Complex g0 = Acosh(w);

        Complex best = default;
        double  bestErr = double.PositiveInfinity;
        int     k = 0;

        foreach (double sign in new[] { +1.0, -1.0 })
        {
            Complex c  = sign * g0;
            int     kk = double.IsNaN(expectedBetaDeltaL)
                ? (c.Imaginary < 0 ? 1 : 0)                       // anchor βΔℓ into (0, 2π)
                : (int)Math.Round((expectedBetaDeltaL - c.Imaginary) / (2 * Math.PI));

            Complex cc = c + new Complex(0, 2 * Math.PI * kk);
            if (cc.Imaginary <= 0) continue;                       // β > 0 on a forward-travelling wave

            double err = double.IsNaN(expectedBetaDeltaL)
                ? (cc.Real >= 0 ? 0.0 : 1.0)                       // no prediction: fall back on α ≥ 0
                : Math.Abs(cc.Imaginary - expectedBetaDeltaL);

            if (err < bestErr) { bestErr = err; best = cc; k = kk; }
        }

        Complex g = best;
        double deg = g.Imaginary * 180.0 / Math.PI;
        bool usable = deg >= PlanarCalibrationSettings.UsableLoDegrees
                   && deg <= PlanarCalibrationSettings.UsableHiDegrees;

        return new GammaResult(g / deltaLM, deg, k, usable);
    }

    /// <summary>
    /// γ from a short standard and SEVERAL long ones, choosing per frequency the separation whose
    /// realised βΔℓ sits closest to the middle of the usable interval. This is multiline TRL's own
    /// idea reduced to what a simulator needs: every standard is solved at every frequency anyway,
    /// so the selection costs nothing beyond the extra fill and can be made on the MEASURED
    /// electrical length rather than on the pre-solve estimate.
    /// </summary>
    /// <param name="expectedBetaPerMetre">The running β estimate — the previous frequency's measured
    /// β scaled by frequency, or <see cref="EstimateBeta"/> at the first point. <b>The choice is made
    /// on the PREDICTION, never on the extracted value, and that is not a stylistic preference:</b> an
    /// aliased separation (βΔℓ past π) reports a wrapped electrical length that can land inside the
    /// usable interval by accident and score better than the correct one.</param>
    /// <param name="index">Which separation was chosen — reported, because "which standard produced
    /// this number" is exactly what someone debugging a bad point needs to know.</param>
    public static GammaResult GammaBest(Mat<Complex> sShort, IReadOnlyList<Mat<Complex>> sLong,
                                        IReadOnlyList<double> deltaLM, double expectedBetaPerMetre,
                                        out int index)
    {
        if (sLong.Count == 0 || sLong.Count != deltaLM.Count)
            throw new ArgumentException("Each long standard needs its own Δℓ.", nameof(sLong));

        index = SelectSeparation(deltaLM, expectedBetaPerMetre);
        return Gamma(sShort, sLong[index], deltaLM[index], expectedBetaPerMetre * deltaLM[index]);
    }

    /// <summary>
    /// Which separation <see cref="GammaBest"/> will choose — <b>a pure function of the Δℓ set and
    /// the PREDICTED β, so it can be asked BEFORE any standard has been solved.</b>
    ///
    /// <para>That is the whole point of it being extracted rather than inlined. <see cref="GammaBest"/>
    /// reads exactly two of the standards' raw matrices at any one frequency — the short line and the
    /// one long line named here — so a driver that knows the answer in advance can solve two meshes
    /// instead of all of them. <c>GammaBest</c>'s own doc comment used to say the selection "costs
    /// nothing beyond the extra fill"; the extra fill turned out to be most of the run, and this is
    /// what lets the sentence become true rather than remain a caveat.</para>
    ///
    /// <para>The scoring is unchanged and deliberately still keyed on the PREDICTION, never on an
    /// extracted electrical length — see <see cref="GammaBest"/>'s own parameter note for the aliasing
    /// reason. Moving it here changes no arithmetic; it changes only who may ask.</para>
    /// </summary>
    public static int SelectSeparation(IReadOnlyList<double> deltaLM, double expectedBetaPerMetre)
    {
        ArgumentNullException.ThrowIfNull(deltaLM);
        if (deltaLM.Count == 0) throw new ArgumentException("No separations.", nameof(deltaLM));

        double centre = Math.Sqrt(PlanarCalibrationSettings.UsableLoDegrees *
                                  PlanarCalibrationSettings.UsableHiDegrees) * Math.PI / 180.0;

        int index = 0;
        double bestScore = double.PositiveInfinity;
        for (int i = 0; i < deltaLM.Count; i++)
        {
            double predicted = Math.Max(expectedBetaPerMetre * deltaLM[i], 1e-12);
            double score = Math.Abs(Math.Log(predicted / centre));
            if (score < bestScore) { bestScore = score; index = i; }
        }
        return index;
    }

    /// <summary>
    /// β before any solve, from ε_eff ≈ (εᵣ+1)/2. Only used to seed the first frequency's branch and
    /// separation choice, and it only has to be right to ~20% for either.
    /// </summary>
    public static double EstimateBeta(GroundedSlab slab, double fHz) =>
        2.0 * Math.PI * fHz * Math.Sqrt(0.5 * (slab.Material.EpsR + 1.0)) / EmConstants.C0;

    /// <summary>acosh, principal branch — written here rather than depended on, as L8a's Bessel
    /// functions and Legendre nodes were, and for the same recorded reason.</summary>
    public static Complex Acosh(Complex w) => Complex.Log(w + Complex.Sqrt(w * w - Complex.One));

    /// <summary>The 2×2 inverse, in closed form. A general solver here would be silly.</summary>
    internal static Mat<Complex> Inverse2(Mat<Complex> a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        var r = new Mat<Complex>(2, 2);
        r[0, 0] =  a[1, 1] / det;
        r[0, 1] = -a[0, 1] / det;
        r[1, 0] = -a[1, 0] / det;
        r[1, 1] =  a[0, 0] / det;
        return r;
    }
}
