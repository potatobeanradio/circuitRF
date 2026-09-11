// ANT-6 — POLARIZATION: WHICH DEFINITION OF CROSS-POL, WHICH SENSE OF CIRCULAR, AND WHAT THE
// REPORTED FLOOR IS ACTUALLY MADE OF.
//
// Short arithmetic over ANT-4's E_θ and E_φ. What the file is really about is that every number in
// it has at least two defensible definitions, that the two which differ by a SIGN are the ones
// nobody notices, and that the smallest reported cross-pol on a symmetric patch is a property of
// the MESH rather than of the antenna.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// R-ant-8. A CROSS-POL NUMBER CARRIES ITS DEFINITION, IN ITS CUBE NAME AND IN ITS NOTE.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Ludwig's first, second and third definitions of cross-polarization give DIFFERENT NUMBERS FOR THE
// SAME ANTENNA. The third is the antenna-measurement standard and is what a range reports, so it is
// what is computed here — but a cube called `CrossPolDb` with no qualifier is a number a user cannot
// reproduce, cannot compare against a measurement and cannot argue with. Hence
// `CoPolLudwig3Db` / `CrossPolLudwig3Db`, with the definition ALSO written out in the cube's own
// note, and:
//
//   **A SECOND DEFINITION WOULD BE A SECOND CUBE, NEVER A MODE.** There is deliberately no setting
//   anywhere in this file that changes what an existing cube MEANS. A switch that silently re-points
//   `CrossPolLudwig3Db` at Ludwig's second definition would make every exported file, every saved
//   plot and every number in a report ambiguous after the fact.
//
// ── The decomposition, DERIVED IN THIS REPOSITORY'S OWN CONVENTION ─────────────────────────────
//
// ANT-4 stores F_θ and F_φ on the standard spherical triad — θ̂ = (cosθcosφ, cosθsinφ, −sinθ),
// φ̂ = (−sinφ, cosφ, 0), r̂ = θ̂ × φ̂ — so the whole decomposition is a rotation inside the
// transverse plane and NOTHING about the layered medium enters it. Ludwig's third definition takes
// the co-polar unit vector to be the one that reduces to a FIXED LINEAR DIRECTION at boresight:
//
//     ê_co = θ̂ cos(φ − φ₀) − φ̂ sin(φ − φ₀)        ê_cross = θ̂ sin(φ − φ₀) + φ̂ cos(φ − φ₀)
//
// and the reason to believe that is this repository's spelling rather than a transcription is that
// it reduces correctly HERE: at θ = 0, θ̂ = (cosφ, sinφ, 0) and φ̂ = (−sinφ, cosφ, 0), so
// ê_co = (cos φ₀, sin φ₀, 0) and ê_cross = (−sin φ₀, cos φ₀, 0) — the azimuth φ₀ and the azimuth
// perpendicular to it, for EVERY φ. That is the property Ludwig-3 exists to have, and it is what a
// measurement range's two probe orientations do.
//
//     E_co = E_θ cos(φ − φ₀) − E_φ sin(φ − φ₀)      E_cross = E_θ sin(φ − φ₀) + E_φ cos(φ − φ₀)
//
// **The transform is a ROTATION, so |E_co|² + |E_cross|² = |E_θ|² + |E_φ|² exactly**, which means U
// is untouched and the decomposition can never move a level. That identity is gated to machine
// precision rather than trusted, because it is the one thing a sign error in this file could NOT
// break — and so it is not on its own sufficient. What pins the signs is §5.1's exact statement:
// for an x̂-directed current element with φ₀ = 0, ANT-4 gives
// F_θ ∝ f_TM·J̃_x cosφ and F_φ ∝ −f_TE·J̃_x sinφ, so
//
//     E_cross ∝ J̃_x sinφ cosφ (f_TM − f_TE)
//
// which is IDENTICALLY ZERO at φ = 0 and φ = 90° — the principal planes — for every θ and every
// stack, and is non-zero on the diagonals unless the two element factors coincide. That is an exact
// statement about the arithmetic, it needs no oracle, and it is the first gate.
//
// **φ₀ → φ₀ + 180° flips the SIGN of both E_co and E_cross and leaves both magnitudes unchanged**,
// so the reported dB are invariant under the choice of which end of the polarization axis is named.
// This is why the derivation below does not fold its axis toward the peak the way ANT-5's beamwidth
// cut has to: there, the fold decides which half of a θ sweep is called positive.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// R-ant-9. φ₀ IS NAMED BY THE CALLER OR DERIVED AND REPORTED, NEVER GUESSED — AND THE DERIVED ONE
//          IS THE SAME AXIS THE BEAMWIDTH CUT DERIVES, BY CONSTRUCTION.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// The reference angle is a property of the ANTENNA, not of the plot, so it has two sources in order:
// `PlanarMetricSettings.PolarizationReferencePhiDeg`, which always wins; then the solved current
// distribution, through `PlanarBeamwidth.AxisAtPatternPeak` — the major axis of the current moment's
// polarization ellipse, evaluated at the pattern's peak direction.
//
// **It is the same call ANT-5's beamwidth cut makes, and that is the point.** "The dominant current
// axis" is one physical quantity; computing it twice would be two definitions of it, and this
// directory's habit (`PlanarCurrentDensity`'s own header) is that two definitions of one quantity is
// the thing to prevent rather than the thing to tidy later. So the derived φ₀ and the derived
// beamwidth plane are the SAME NUMBER, which is also physically right: the E-plane of a linearly
// polarized patch is the plane containing its polarization.
//
// **Where the derivation is ambiguous it REFUSES.** A circularly polarized or dual-fed structure has
// no single linear reference, and a cross-pol taken against an invented one is a number about the
// invention. The test is the ellipse's own: minor/major past `AxisAmbiguityRatio`, which a
// circularly polarized structure reads as 1.0 exactly. The refusal has somewhere to point, which is
// the whole reason the axial ratio and the sense are in this same phase rather than a later one —
// for that antenna the axial ratio IS the number that describes it.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// R-ant-10. THE SENSE IS IEEE, DERIVED FROM THIS REPOSITORY'S OWN TRIAD, AND THE AXIAL RATIO HAS A
//           STATED SENTINEL RATHER THAN A CLAMP.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// **Sense conventions differ by a sign between disciplines** (optics and radio disagree, and so do
// two textbooks in the same discipline), so it is derived here rather than quoted, in the e^{jωt}
// convention the whole directory uses:
//
// A rotation about the propagation direction is RIGHT-HANDED in the IEEE sense when the real field
// vector turns the way a right hand's fingers curl with the thumb along k̂ — equivalently, when
// (E × dE/dt)·k̂ > 0. For E(t) = Re{(A θ̂ + B φ̂)e^{jωt}} with A = a e^{jα}, B = b e^{jβ},
//
//     (E × Ė)·r̂ = E_θ Ė_φ − E_φ Ė_θ = ω·a·b·sin(α − β) = ω·Im{A B*}
//
// — constant in time, as it must be. So **RHCP ⟺ Im{E_θ E_φ*} > 0**, and the check that this is the
// right hand rather than the left is that (θ̂, φ̂, r̂) is right-handed exactly as (x̂, ŷ, ẑ) is:
// substituting θ̂ → x̂, φ̂ → ŷ, r̂ → ẑ turns the rule into "x̂ − jŷ propagating along +ẑ is RHCP",
// which is the textbook statement of the IEEE convention in this time convention.
//
// The circular components follow with no extra convention: with ê_R = (θ̂ − jφ̂)/√2 and the
// conjugate projection, |E_R|² = ½(T + 2·Im{E_θE_φ*}) and |E_L|² = ½(T − 2·Im{E_θE_φ*}) where
// T = |E_θ|² + |E_φ|². Hence
//
//     PolarizationSense = s₃ = (|E_R|² − |E_L|²)/T = 2·Im{E_θ E_φ*}/T          ∈ [−1, +1]
//
// — the normalised Stokes V. **Its SIGN is the sense (+ = RH, − = LH) and its MAGNITUDE is how
// circular the direction is**, which is strictly more than a ±1 carries: a nearly linear direction
// reads ≈ 0 rather than being assigned a sense it does not have. Its relation to the axial ratio is
// exact, |s₃| = 2·AR/(AR² + 1) with AR the linear ratio ≥ 1, so the two cubes are not two estimates
// of one thing — one is the shape of the ellipse and the other is which way it is traced.
//
// The axial ratio itself is written in the ONE algebraically stable form:
//
//     AR = (T + √(T² − 4·Im{E_θE_φ*}²)) / (2·|Im{E_θE_φ*}|)
//
// The obvious spelling, (|E_R| + |E_L|)/||E_R| − |E_L||, subtracts two nearly equal numbers exactly
// where the answer matters most — a nominally linear patch — and loses half its digits there. The
// form above is the same expression with that difference replaced by its identity
// T − √(T²−4Im²) = 4Im²/(T + √(T²−4Im²)), so it is exact for a linear structure instead of noisy.
//
// **Pure linear reads a SENTINEL, not a clamp, and not ∞.** Im{E_θE_φ*} is exactly zero in the
// principal planes of a symmetric structure, so the true axial ratio there is genuinely infinite.
// Storing ∞ (or a NaN) in a Real cube would make every autoscaled plot, every min/max and every
// `.mat`/`.npy` export downstream carry a value many readers refuse; clamping to a plausible number
// like 40 dB would make "linear" indistinguishable from "quite linear", which is the thing §3
// forbids. So the reported value SATURATES at `LinearAxialRatioDb` = 100 dB, a level no antenna and
// no measurement reaches — one part in 10⁵ of cross-circular amplitude — so it cannot be misread as
// a computed number, and it is named in the cube's note as meaning exactly "linear at this
// analysis's own floor". The same reasoning gives `FloorDb` = −400 dB as the co/cross cubes' FLOOR:
// 20·log₁₀ of an exact zero is −∞, and §5.1's principal-plane cross-pol is an exact zero on purpose.
// **−400 dB is chosen so that it floors only a value that is a zero in all but name.** A structure's
// grazing co-pol is a genuine round-off zero and lands near −320 to −340 dB — those are REPORTED,
// because they are what the arithmetic produced; −400 dB (1e-20 of a volt against a pattern whose
// peak is order 1) can be reached by nothing else.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// R-ant-11. THE REPORTED CROSS-POL FLOOR IS SET BY THE MESH, AND IT IS SAID WHEREVER CROSS-POL IS
//           REPORTED.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Cross-polarization is generated by ASYMMETRY. On a nominally symmetric patch the physical cross-pol
// in the principal planes is very low, and what a solver reports there is dominated by the MESH's own
// asymmetry — a staircased boundary is not symmetric, and neither is a grid whose lines were placed
// by an attractor at one rim. So a reported principal-plane cross-pol of −45 dB may be −45 dB of
// mesh, and that must not be something a user discovers. `MeshFloorNote` says so, it is attached to
// both cross-pol cubes, and `RESOLVED.md` §ANT-6 carries the measurement across three mesh densities
// rather than the assertion.
//
// The measurement's own consequence for the series: **conformal boundary cells matter more for
// antenna work than for filter work**, and not only for cross-pol — staircase quantisation of a
// 41.3 mm patch at a 2.4 mm cell is a ~6 % length error and therefore ~6 % in resonant frequency,
// which is the headline number for an antenna. This phase does NOT flip
// `PlanarBoundaryCells.Staircase`, whose recorded reason (reproducibility of every measured number in
// this directory) stands; it records the argument, and the shipped example antenna should pick
// conformal.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Where the Ludwig-3 reference angle φ₀ came from — <b>which is reported rather than implied</b>
/// (R-ant-9). A derived one carries the ellipse it was read off.
/// </summary>
/// <param name="PhiDeg">φ₀ in degrees.</param>
/// <param name="Derived">True when it was read off the solved currents rather than named.</param>
/// <param name="MinorOverMajor">The current-moment ellipse's minor/major ratio at the peak direction,
/// which is what says there IS a dominant axis. NaN for a named reference, which needs no ellipse.</param>
public sealed record PlanarPolarizationReference(double PhiDeg, bool Derived, double MinorOverMajor)
{
    /// <summary>The sentence a run prints for the reference angle — §2's "reported, in the same words
    /// the mesher reports a direction it inferred".</summary>
    public string Note =>
        Derived
            ? $"The Ludwig-3 reference angle was DERIVED, not named: φ₀ = {PhiDeg:F2}°, the dominant " +
              $"axis of the solved current moment at the pattern's peak direction " +
              $"(minor/major = {MinorOverMajor:E2}). It is the same axis ANT-5's beamwidth cut " +
              $"derives, from the same call, so the co-polar plane and the E-plane cut cannot " +
              $"disagree. Name PolarizationReferencePhiDeg to override it."
            : $"The Ludwig-3 reference angle was NAMED by the caller: φ₀ = {PhiDeg:F2}°.";
}

/// <summary>
/// One driven port's polarization at one frequency, over ANT-4's own grid: the axial ratio and the
/// sense everywhere, and — when a reference angle exists — the Ludwig-3 co/cross decomposition.
///
/// <para><b>The axial ratio and the sense need no reference angle and are therefore never
/// refused</b>; they are invariant under the rotation the co/cross pair is built from, which is
/// exactly why §3 puts them in this phase. The co/cross pair needs φ₀ and refuses without one
/// (R-ant-9).</para>
/// </summary>
/// <param name="AxialRatioDb">20·log₁₀ of the linear axial ratio, saturating at
/// <see cref="PlanarPolarization.LinearAxialRatioDb"/>. Row-major [theta, phi].</param>
/// <param name="Sense">The normalised Stokes V — sign is the IEEE sense, magnitude is how circular.</param>
/// <param name="CoPolDb">20·log₁₀|E_co| in dB relative to 1 V of the r-normalised pattern, or empty
/// when the reference angle was refused.</param>
/// <param name="CrossPolDb">The same for E_cross, or empty.</param>
public sealed record PlanarPolarizationPattern(
    PlanarFarFieldGrid             Grid,
    IReadOnlyList<double>          AxialRatioDb,
    IReadOnlyList<double>          Sense,
    EmSuitability                  ReferenceVerdict,
    PlanarPolarizationReference?   Reference,
    IReadOnlyList<double>          CoPolDb,
    IReadOnlyList<double>          CrossPolDb,
    int                            DrivenPort,
    double                         FrequencyHz)
{
    /// <summary>Whether the Ludwig-3 pair was computed. The axial ratio and the sense are there
    /// either way.</summary>
    public bool HasCoCross => ReferenceVerdict.Ok && CoPolDb.Count > 0;

    /// <summary>The peak co-polar level on the grid, dB. NaN when there is no decomposition.</summary>
    public double PeakCoPolDb
    {
        get
        {
            if (!HasCoCross) return double.NaN;
            double m = double.NegativeInfinity;
            foreach (double v in CoPolDb) m = Math.Max(m, v);
            return m;
        }
    }

    /// <summary>
    /// <b>The headline cross-pol number, and it is a PRINCIPAL-PLANE one</b> — the worst
    /// <c>CrossPolDb − PeakCoPolDb</c> over the two cuts φ₀ and φ₀ + 90°, in dB. NaN when there is no
    /// decomposition or when the grid does not sample both of those azimuths.
    ///
    /// <para><b>Why not the worst over the whole hemisphere.</b> A linearly polarized patch has
    /// legitimately high cross-pol on the DIAGONAL planes — §5.1's exact statement is that it
    /// vanishes in the principal planes and nowhere else — so a hemisphere-wide worst case is a
    /// physical property of the antenna, not a floor. The principal-plane figure is the one that is
    /// ideally zero and is therefore the one R-ant-11's floor is about, and it is the number
    /// <c>RESOLVED.md</c> §ANT-6's mesh measurement reads.</para>
    ///
    /// <para><b>Floored at <see cref="PlanarPolarization.FloorDb"/></b> like the cubes themselves, so
    /// a structural zero cannot drive it past the one number the whole file's dB are bounded by.</para>
    /// </summary>
    public double PrincipalPlaneCrossPolDb
    {
        get
        {
            if (!HasCoCross || Reference is null) return double.NaN;
            double peak = PeakCoPolDb, worst = double.NegativeInfinity;
            double tol = 0.5 * FinestPhiStep();
            foreach (double want in new[] { Reference.PhiDeg, Reference.PhiDeg + 90.0 })
            {
                double target = ((want % 360.0) + 360.0) % 360.0;
                int ip = PlanarMetrics.Nearest(Grid.PhiDeg, target);
                if (Math.Abs(Delta(Grid.PhiDeg[ip], target)) > tol) return double.NaN;
                for (int it = 0; it < Grid.ThetaDeg.Count; it++)
                    worst = Math.Max(worst, CrossPolDb[Grid.IndexOf(it, ip)]);
            }
            return Math.Max(worst - peak, PlanarPolarization.FloorDb);
        }
    }

    private double FinestPhiStep()
    {
        if (Grid.PhiDeg.Count < 2) return 360.0;
        double best = double.PositiveInfinity;
        for (int i = 1; i < Grid.PhiDeg.Count; i++)
            best = Math.Min(best, Grid.PhiDeg[i] - Grid.PhiDeg[i - 1]);
        return best;
    }

    private static double Delta(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>The most circular direction on the grid — where |s₃| is largest — and its sense. What
    /// makes the caption informative on a circularly polarized patch, whose whole answer is there.</summary>
    public (double ThetaDeg, double PhiDeg, double AxialRatioDb, double Sense) MostCircular
    {
        get
        {
            int best = 0;
            for (int i = 1; i < Sense.Count; i++)
                if (Math.Abs(Sense[i]) > Math.Abs(Sense[best])) best = i;
            int np = Grid.PhiDeg.Count;
            return (Grid.ThetaDeg[best / np], Grid.PhiDeg[best % np], AxialRatioDb[best], Sense[best]);
        }
    }

    /// <summary>The axial ratio and sense at broadside (θ = 0), where a circularly polarized patch is
    /// judged. NaN when the grid has no θ = 0 row.</summary>
    public (double AxialRatioDb, double Sense) AtBroadside
    {
        get
        {
            if (Grid.ThetaDeg.Count == 0 || Grid.ThetaDeg[0] != 0.0) return (double.NaN, double.NaN);
            int k = Grid.IndexOf(0, 0);
            return (AxialRatioDb[k], Sense[k]);
        }
    }

    /// <summary>R-res-8 — the scale, its normalisation, its convention and its floor, as the lines a
    /// run or a panel prints verbatim.</summary>
    public string ScaleCaption
    {
        get
        {
            var (bAr, bSense) = AtBroadside;
            var (mt, mp, mAr, mSense) = MostCircular;
            string broadside = double.IsNaN(bAr)
                ? ""
                : $"At broadside the axial ratio is {Describe(bAr)} ({PlanarPolarization.SenseWord(bSense)}, " +
                  $"s₃ = {bSense:F4}). ";
            string best = $"The most circular direction sampled is θ = {mt:G6}°, φ = {mp:G6}°, " +
                          $"axial ratio {Describe(mAr)} ({PlanarPolarization.SenseWord(mSense)}, " +
                          $"s₃ = {mSense:F4}). ";
            string cross = HasCoCross
                ? $"Ludwig-3 co/cross about φ₀ = {Reference!.PhiDeg:F2}°: peak co-pol " +
                  $"{PeakCoPolDb:F2} dB (re 1 V, r-normalised)" +
                  (double.IsNaN(PrincipalPlaneCrossPolDb)
                      ? ", principal-plane cross-pol not sampled on this grid. "
                      : $", worst principal-plane cross-pol {PrincipalPlaneCrossPolDb:F2} dB below it. ")
                : "No Ludwig-3 co/cross decomposition: " + ReferenceVerdict.Reason + " ";
            return $"Polarization at {SurfaceMesher.Eng(FrequencyHz)}Hz, port {DrivenPort} driven at " +
                   $"1 V. " + broadside + best + cross + PlanarPolarization.SenseNote;
        }
    }

    private static string Describe(double arDb) =>
        arDb >= PlanarPolarization.LinearAxialRatioDb
            ? $"at the {PlanarPolarization.LinearAxialRatioDb:F0} dB LINEAR sentinel"
            : $"{arDb:F2} dB";
}

/// <summary>
/// <b>ANT-6 — Ludwig's third definition, the axial ratio and the IEEE sense.</b> See the file header
/// for the four rules, for the derivation in this repository's own convention, and for why the
/// linear case takes a stated sentinel rather than a clamp or an infinity.
/// </summary>
public static class PlanarPolarization
{
    /// <summary>The cubes land in ANT-4's group — polarization is a property of a pattern.</summary>
    public const string Group = PlanarFarField.Group;

    /// <summary>
    /// <b>The axial ratio of a linear direction, in dB, as a NAMED SENTINEL.</b> 100 dB is one part in
    /// 10⁵ of cross-circular amplitude — past any antenna, any measurement and any mesh — so it cannot
    /// be misread as a computed value, and it keeps the cube plottable and exportable where ∞ or NaN
    /// would not. See the file header's R-ant-10 for why this is a sentinel and not a clamp.
    /// </summary>
    public const double LinearAxialRatioDb = 100.0;

    /// <summary>The dB of an exact zero — a structural null, or a co/cross component that vanishes by
    /// symmetry. <b>−400 dB floors ONLY what is a zero in all but name</b>: a grazing co-pol is a
    /// genuine round-off zero near −320 to −340 dB and is reported as such, while 1e-20 of a volt
    /// against a pattern peaking at order 1 is reachable by nothing but an exact zero. Stated in the
    /// cubes' own notes, because a floored value must not read as a measurement.</summary>
    public const double FloorDb = -400.0;

    /// <summary>R-ant-8 — the definition, in the words the cube notes and the trace picker carry. The
    /// cube NAMES carry it too; the brief allows either and this does both.</summary>
    public const string DefinitionNote =
        "LUDWIG'S THIRD DEFINITION of co- and cross-polarization, which is the antenna-measurement " +
        "standard and is what a range reports. Ludwig's first, second and third definitions give " +
        "DIFFERENT numbers for the same antenna, so the definition is part of the quantity's name: " +
        "E_co = E_θ·cos(φ − φ₀) − E_φ·sin(φ − φ₀) and E_cross = E_θ·sin(φ − φ₀) + E_φ·cos(φ − φ₀), " +
        "whose unit vectors reduce at broadside to the azimuth φ₀ and the azimuth perpendicular to " +
        "it — the two orientations a measurement probe is rotated between. The transform is a " +
        "rotation, so |E_co|² + |E_cross|² = |E_θ|² + |E_φ|² exactly and no level moves. If a second " +
        "definition is ever added it will be a SECOND CUBE, never a mode that changes what this one " +
        "means.";

    /// <summary>R-ant-10 — the sense convention, named once, in the note both polarization cubes carry.</summary>
    public const string SenseNote =
        "Sense is IEEE: a wave is RIGHT-handed when the real field vector turns the way a right " +
        "hand's fingers curl with the thumb along the direction of PROPAGATION, which in this " +
        "directory's e^{jωt} convention and on its own (θ̂, φ̂, r̂) triad is Im{E_θ·conj(E_φ)} > 0. " +
        "PolarizationSense is the normalised Stokes V, 2·Im{E_θ·conj(E_φ)}/(|E_θ|² + |E_φ|²): its " +
        "SIGN is the sense (+1 right-hand, −1 left-hand) and its MAGNITUDE is how circular the " +
        "direction is, so a nearly linear direction reads near zero rather than being assigned a " +
        "sense it does not have. Sense conventions differ by a sign between disciplines; this one is " +
        "derived from the triad rather than quoted.";

    /// <summary>What the floor is and what a value sitting on it means — said in both dB cubes' notes
    /// rather than left for a reader to infer from a suspiciously round number.</summary>
    public static readonly string FloorSentence =
        $"The dB are FLOORED at {FloorDb:F0} dB, which is what 20·log₁₀ of an EXACT ZERO reads as: " +
        $"the Ludwig-3 cross component of a single linear current vanishes identically in the " +
        $"principal planes, so an exact zero is an ordinary outcome here rather than a failure. A " +
        $"round-off zero — a grazing co-pol, say, at −320 to −340 dB — is reported as the arithmetic " +
        $"produced it and is NOT floored; only a value no pattern can reach is.";

    /// <summary>R-ant-11 — the note attached wherever cross-pol is reported.</summary>
    public const string MeshFloorNote =
        "THE CROSS-POL FLOOR IS SET BY THE MESH, NOT BY THE ANTENNA. Cross-polarization is generated " +
        "by asymmetry, and on a nominally symmetric patch the physical principal-plane cross-pol is " +
        "very low — so what is reported there is dominated by the MESH's own asymmetry: a staircased " +
        "boundary is not symmetric, and neither is a grid whose lines were placed by an attractor at " +
        "one rim. A principal-plane cross-pol of −45 dB may be −45 dB of mesh. Read it as a ceiling " +
        "on what this analysis can resolve, refine the mesh and watch whether the number moves, and " +
        "see RESOLVED.md §ANT-6 for the measurement across three mesh densities. Cross-pol on the " +
        "DIAGONAL planes is a different thing: it is physical and is ideally non-zero, because the " +
        "Ludwig-3 cross component of a single linear current vanishes in the principal planes and " +
        "nowhere else.";

    /// <summary>The axial-ratio cube's own note. <b>Static readonly rather than const</b> so the
    /// sentinel's value is read from <see cref="LinearAxialRatioDb"/> instead of being a second
    /// literal that can drift away from it.</summary>
    public static readonly string AxialRatioNote =
        "20·log₁₀ of the linear axial ratio of the polarization ellipse, ≥ 0 dB, per direction. 0 dB " +
        "is perfectly circular. A LINEAR direction is genuinely infinite and reads the named " +
        $"sentinel {LinearAxialRatioDb:F0} dB instead — not a clamp and not ∞: a clamp at a plausible " +
        "level would make \"linear\" indistinguishable from \"quite linear\", and an ∞ or a NaN in a " +
        "real cube breaks every autoscaled plot and several export formats. It is computed as " +
        "(T + √(T² − 4·Im{E_θconj(E_φ)}²))/(2·|Im{E_θconj(E_φ)}|) with T = |E_θ|² + |E_φ|², which is " +
        "the algebraically stable spelling: the obvious one subtracts two nearly equal magnitudes " +
        "exactly where a nominally linear antenna's answer lives. Its relation to PolarizationSense " +
        "is exact — |s₃| = 2·AR/(AR² + 1) — so the two are the ellipse's shape and its direction of " +
        "travel, not two estimates of one number. " + SenseNote;

    /// <summary>The sense cube's own note.</summary>
    public const string PolarizationSenseNote =
        "The normalised Stokes V per direction, in [−1, +1]: +1 is pure right-hand circular, −1 pure " +
        "left-hand, 0 linear. A direction carrying no field reads 0, because a zero field has no " +
        "sense. " + SenseNote;

    /// <summary>
    /// <b>The four cubes, each with its unit and the note that carries its DEFINITION</b> — R-ant-8's
    /// "the cube names carry the definition, or the definition sits in the cube's own note", done both
    /// ways. This is ANT-5's registry shape minus the parts polarization does not need: every entry is
    /// per-direction and none of them can be refused for a reason the pattern itself survived, so
    /// there is no <c>Availability</c> here — the ONE refusal in this phase is the reference angle's,
    /// and it is carried by <see cref="PlanarPolarizationPattern.ReferenceVerdict"/> and by
    /// <see cref="PlanarPolarizationSet.CoCrossVerdict"/> because it takes two cubes out together.
    ///
    /// <para>It is a table rather than four string literals at the publish site so that a trace picker,
    /// a CLI listing and an exporter all read the same words a run prints, which is what §1's "the
    /// panel and the trace picker say it in words" needs to be true of more than one caller.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string CubeName, string Unit, string Note)> Cubes =
    [
        ("AxialRatioDb",      "dB", AxialRatioNote),
        ("PolarizationSense", "1",  PolarizationSenseNote),
        ("CoPolLudwig3Db",    "dB",
            "The CO-POLAR level, 20·log₁₀|E_co| in dB relative to 1 V of ANT-4's r-normalised " +
            "pattern — the same quantity the Etheta and Ephi cubes carry, as a magnitude in dB, so " +
            "the familiar \"cross-pol is X dB below peak co-pol\" is one subtraction of two " +
            "published cubes rather than a hidden self-normalisation. " + FloorSentence + " " +
            DefinitionNote),
        ("CrossPolLudwig3Db", "dB",
            "The CROSS-POLAR level, 20·log₁₀|E_cross|, in the same dB as CoPolLudwig3Db. " +
            FloorSentence + " " + DefinitionNote + " " + MeshFloorNote),
    ];

    /// <summary>The note for one cube by name — what a picker or a listing asks for.</summary>
    public static string NoteOf(string cubeName) =>
        Cubes.First(c => c.CubeName == cubeName).Note;

    /// <summary>"right-hand" / "left-hand" / "linear", for the one place a word reads better than a
    /// sign. The threshold is only about the WORD; the cube carries the number.</summary>
    public static string SenseWord(double s3) =>
        double.IsNaN(s3) ? "sense unavailable"
        : s3 >  1e-6     ? "right-hand"
        : s3 < -1e-6     ? "left-hand"
        :                  "linear";

    /// <summary>
    /// <b>The reference angle, from the two sources in §2's order.</b> Named wins; otherwise the
    /// dominant current axis at the pattern's peak, through the SAME call ANT-5's beamwidth cut makes
    /// (R-ant-9); otherwise a refusal naming what would answer it.
    /// </summary>
    public static (EmSuitability Verdict, PlanarPolarizationReference? Reference) ReferenceAngle(
        PlanarMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Settings.PolarizationReferencePhiDeg is { } named)
            return (EmSuitability.Yes,
                    new PlanarPolarizationReference(((named % 360.0) + 360.0) % 360.0, false,
                                                    double.NaN));

        var (axis, major, minor) = PlanarBeamwidth.AxisAtPatternPeak(context);

        if (!(major > 0))
            return (EmSuitability.No(
                "No Ludwig-3 reference angle could be derived: the structure's CURRENT MOMENT at the " +
                "pattern's peak direction is zero, so there is no dominant current axis and therefore " +
                "no nominal linear polarization to measure co- and cross-pol against. That happens on " +
                "a balanced or perfectly symmetric differential structure, where the moment cancels " +
                "exactly in every direction. Name PolarizationReferencePhiDeg (a φ in degrees) and " +
                "the Ludwig-3 pair is reported about it; it is refused rather than defaulted to " +
                "φ₀ = 0, because cross-pol against an invented reference is a number about the " +
                "invention. AxialRatioDb and PolarizationSense need no reference and are reported " +
                "regardless."), null);

        double ratio = minor / major;
        if (ratio > PlanarMetricSettings.AxisAmbiguityRatio)
            return (EmSuitability.No(
                $"No Ludwig-3 reference angle could be derived: the current has no dominant LINEAR " +
                $"axis. The current moment at the pattern's peak has a polarization ellipse whose " +
                $"minor axis carries {ratio:P1} of the major one, past the " +
                $"{PlanarMetricSettings.AxisAmbiguityRatio:P0} this derivation is allowed — a " +
                $"circularly polarized structure reads 1.0 here exactly, and a square patch driven " +
                $"in both modes reads close to it. Such a structure genuinely has no single linear " +
                $"reference, and co/cross against an invented one would be a number about the " +
                $"invention. This is the case AxialRatioDb and PolarizationSense exist for, and both " +
                $"are reported: for a circularly polarized antenna the axial ratio, not a cross-pol " +
                $"ratio, is the number that describes it. Name PolarizationReferencePhiDeg to get the " +
                $"Ludwig-3 pair about a reference of your own."), null);

        return (EmSuitability.Yes, new PlanarPolarizationReference(axis, true, ratio));
    }

    /// <summary>
    /// <b>The whole polarization of one pattern.</b> The axial ratio and the sense always; the
    /// Ludwig-3 pair when <see cref="ReferenceAngle"/> gives one.
    /// </summary>
    public static PlanarPolarizationPattern For(PlanarMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (verdict, reference) = ReferenceAngle(context);
        return Of(context.Pattern, verdict, reference);
    }

    /// <summary>
    /// The arithmetic alone, over one pattern and one already-decided reference — which is what makes
    /// every gate in <c>PlanarPolarizationTests</c> expressible against a HAND-BUILT pattern with no
    /// mesh, no currents and no solve anywhere near it.
    /// </summary>
    public static PlanarPolarizationPattern Of(
        PlanarFarFieldPattern pattern, EmSuitability referenceVerdict,
        PlanarPolarizationReference? reference)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(referenceVerdict);

        int n = pattern.Grid.DirectionCount;
        var ar    = new double[n];
        var sense = new double[n];
        bool pair = referenceVerdict.Ok && reference is not null;
        var co    = pair ? new double[n] : [];
        var cross = pair ? new double[n] : [];

        double phi0 = reference?.PhiDeg ?? 0.0;
        int np = pattern.Grid.PhiDeg.Count;

        for (int it = 0; it < pattern.Grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < np; ip++)
            {
                int k = pattern.Grid.IndexOf(it, ip);
                Complex eth = pattern.ETheta[k], eph = pattern.EPhi[k];

                double t  = eth.Magnitude * eth.Magnitude + eph.Magnitude * eph.Magnitude;
                double im = (eth * Complex.Conjugate(eph)).Imaginary;

                ar[k]    = AxialRatioDbOf(t, im);
                sense[k] = t > 0 ? 2.0 * im / t : 0.0;

                if (!pair) continue;
                var (sin, cos) = Math.SinCos((pattern.Grid.PhiDeg[ip] - phi0) * Math.PI / 180.0);
                co[k]    = Db(eth * cos - eph * sin);
                cross[k] = Db(eth * sin + eph * cos);
            }

        return new PlanarPolarizationPattern(
            pattern.Grid, ar, sense, referenceVerdict, pair ? reference : null, co, cross,
            pattern.DrivenPort, pattern.FrequencyHz);
    }

    /// <summary>
    /// The axial ratio in dB from the two rotation invariants — see the file header for why it is
    /// spelled this way and not as a ratio of circular magnitudes.
    /// </summary>
    internal static double AxialRatioDbOf(double totalSquared, double imEthConjEphi)
    {
        double a = Math.Abs(imEthConjEphi);
        if (!(a > 0)) return LinearAxialRatioDb;
        double root = Math.Sqrt(Math.Max(totalSquared * totalSquared - 4.0 * a * a, 0.0));
        double db = 20.0 * Math.Log10((totalSquared + root) / (2.0 * a));
        return Math.Min(Math.Max(db, 0.0), LinearAxialRatioDb);
    }

    private static double Db(Complex e)
    {
        double m = e.Magnitude;
        return m > 0 ? Math.Max(20.0 * Math.Log10(m), FloorDb) : FloorDb;
    }
}

/// <summary>
/// Every polarization pattern one sweep produced, with the set-wide verdict the co/cross cubes need.
///
/// <para><b>A co/cross cube is published over the whole set or not at all, and that needs ONE φ₀.</b>
/// A <c>DataCube</c> has no missing-value concept, and a cube whose reference angle changed halfway
/// along the frequency axis would be two quantities under one name — which is worse than a refusal,
/// because nothing in the axis says it happened. A DERIVED φ₀ is derived per pattern, so a structure
/// whose dominant current axis rotates with frequency or differs between driven ports has no single
/// reference to put on one cube; that refuses the pair for the whole set and says to name φ₀. This is
/// the same rule and the same reason as <see cref="PlanarMetricSet"/>'s beamwidth cut axis.</para>
/// </summary>
/// <param name="Patterns">Row-major <c>[freq, port]</c>.</param>
public sealed record PlanarPolarizationSet(
    IReadOnlyList<double>                      FrequenciesHz,
    IReadOnlyList<int>                         PortNumbers,
    EmSuitability                              CoCrossVerdict,
    double                                     ReferencePhiDeg,
    IReadOnlyList<PlanarPolarizationPattern>   Patterns)
{
    public PlanarPolarizationPattern At(int freqIndex, int portIndex) =>
        Patterns[freqIndex * PortNumbers.Count + portIndex];

    /// <summary>Every reason a cube is not published, said once for the set rather than once per
    /// point — which is what a run's notes want.</summary>
    public IEnumerable<string> Refusals
    {
        get { if (!CoCrossVerdict.Ok) yield return CoCrossVerdict.Reason!; }
    }

    /// <summary>
    /// The set, from one pattern per (frequency, port). <b>The reference angle has to AGREE across
    /// it</b> — see the type's own summary for why a disagreement is a refusal rather than an average.
    /// </summary>
    /// <summary>
    /// <b>ANT-12 — how far two DERIVED reference angles may differ and still be one reference.</b>
    /// 1e-9° was not a tolerance, it was exact equality on the output of an eigen-decomposition, and
    /// it refused the Ludwig-3 pair on an ordinary symmetric patch whose own refusal sentence then
    /// printed its two disagreeing angles as <c>89.999°</c> and <c>89.999°</c> — the whole spread was
    /// round-off in the current moment.
    ///
    /// <para><b>0.01° is sized from what the decomposition does with it, not from taste.</b> A
    /// reference off by δ leaks co-pol into cross at 20·log₁₀(sin δ), which at 0.01° is −75 dB —
    /// roughly thirty dB below the ≈ −45 dB cross-pol floor the MESH itself sets (see
    /// <see cref="MeshFloorNote"/>). Two decompositions that close are indistinguishable in the cube
    /// they would produce. A reference that genuinely rotates with frequency moves far more than this
    /// and still refuses, which is the case the refusal exists for.</para>
    /// </summary>
    public const double ReferenceAgreementDeg = 0.01;

    /// <summary>φ₀ and φ₀ + 180° give the same |E_co| and |E_cross|, so they are ONE reference.</summary>
    private static bool SameReference(double a, double b)
    {
        double d = Math.Abs(((a - b) % 180.0 + 180.0) % 180.0);
        return Math.Min(d, 180.0 - d) <= ReferenceAgreementDeg;
    }

    public static PlanarPolarizationSet From(IReadOnlyList<double> frequenciesHz,
                                             IReadOnlyList<int> portNumbers,
                                             IReadOnlyList<PlanarPolarizationPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var verdict = EmSuitability.Yes;
        double phi0 = double.NaN;

        foreach (var p in patterns)
            if (!p.ReferenceVerdict.Ok)
            {
                verdict = EmSuitability.No(
                    $"CoPolLudwig3Db and CrossPolLudwig3Db are not published. At " +
                    $"{SurfaceMesher.Eng(p.FrequencyHz)}Hz, port {p.DrivenPort} driven: " +
                    p.ReferenceVerdict.Reason);
                break;
            }
            else if (double.IsNaN(phi0))
            {
                phi0 = p.Reference!.PhiDeg;
            }
            else if (!SameReference(p.Reference!.PhiDeg, phi0))
            {
                verdict = EmSuitability.No(
                    $"CoPolLudwig3Db and CrossPolLudwig3Db are not published: the Ludwig-3 reference " +
                    $"angle does not agree across the sweep. The first point takes φ₀ = {phi0:G6}° " +
                    $"and {SurfaceMesher.Eng(p.FrequencyHz)}Hz port {p.DrivenPort} takes " +
                    $"φ₀ = {p.Reference.PhiDeg:G6}°. A DERIVED reference is derived per pattern, and a " +
                    $"structure whose dominant current axis moves with frequency or differs between " +
                    $"driven ports has no single reference to put on one cube — a cube whose φ₀ " +
                    $"changed halfway along its own frequency axis would be two quantities under one " +
                    $"name, with nothing in the axis to say so. Name PolarizationReferencePhiDeg and " +
                    $"every point reports the same decomposition. AxialRatioDb and PolarizationSense " +
                    $"need no reference and are published either way.");
                break;
            }

        return new PlanarPolarizationSet(frequenciesHz, portNumbers, verdict,
                                         verdict.Ok ? phi0 : double.NaN, patterns);
    }
}
