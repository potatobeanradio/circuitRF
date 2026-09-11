// ANT-5 §4 — A BEAMWIDTH NEEDS A NAMED CUT, AND MUST NOT GUESS ONE.
//
// A 3 dB beamwidth is meaningless without saying in which plane. The E- and H-planes of a patch
// follow from its polarization, which follows from the current distribution — but a bent, slotted or
// circularly-polarized structure has no obvious principal plane, and inventing one would be the same
// class of error as inventing a current direction for the mesher. `TransmissionLineMesh` already
// DECLINES that question when the port vector and the artwork's principal axis disagree by more than
// 15°, and this is the same question one level up.
//
//   R-ant-7. A CUT IS EITHER NAMED BY THE CALLER OR DERIVED AND REPORTED. When neither is available
//   the beamwidth is refused BY NAME. It is never defaulted to φ = 0 silently, and the cut that was
//   actually used lands on the cube's own axis so the number can never be read without its plane.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// HOW THE CUT IS DERIVED, AND THE THREE WAYS THAT CAN FAIL
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// The dominant current axis is read off the CURRENT TRANSFORM EVALUATED AT THE PEAK DIRECTION —
// `RooftopSpectrum.Of` at k = k₀ sinθ_p (cosφ_p, sinφ_p), summed over the basis — which costs one
// O(N) pass and shares its normalisation with the pattern's own LEVEL rather than inventing a second
// definition:
//
//     M_x(k) = Σ_{x-rooftops} I_b · ∫f_b e^{+jk·r} dS        [A·m],   M_y likewise
//
// **IT IS EVALUATED AT THE PEAK AND NOT AT k = 0, AND THAT IS A CORRECTION, NOT A PREFERENCE.** At
// k = 0 the transform is the rooftop's plain dipole moment and the sum is the structure's TOTAL
// current moment — which is the right quantity only while the structure is electrically small. On a
// 3 λ_g line the net moment CANCELS to round-off (the integral of a standing wave over whole periods
// is zero), so the axis would be read from noise; measured, it came out as a valid plane only because
// the y moment was exactly zero by symmetry. At the peak direction the transform is by construction
// the current that produced the largest field there, so it cannot vanish, and "the plane containing
// the peak and the dominant current axis" is then literally what is computed. For a broadside peak
// k = 0 and the two agree, which is why a patch is unaffected either way.
//
// (M_x, M_y) is a COMPLEX vector, so what it describes is a polarization ellipse, and the dominant
// axis is that ellipse's major axis:
//
//     φ_c = ½·atan2( 2·Re(M_x M_y*),  |M_x|² − |M_y|² )
//
// with principal values λ± = ½[(|M_x|²+|M_y|²) ± √((|M_x|²−|M_y|²)² + 4Re(M_x M_y*)²)]. A LINEAR
// current, including a diagonal one, has λ− = 0 exactly and a perfectly well defined axis; a
// CIRCULARLY POLARIZED one has λ− = λ+ and no axis at all. Hence the three refusals:
//
//   1. λ+ = 0 — the structure has no net current moment (a balanced pair, a perfectly symmetric
//      differential feed). There is nothing to derive a plane from.
//   2. λ−/λ+ past `AxisAmbiguityRatio` — the current is not dominantly linear. This is the case the
//      whole refusal exists for, and a circularly polarized patch reads exactly 1.
//   3. The grid does not sample both halves of the plane. A cut runs from −θ_max through broadside
//      to +θ_max, and the negative half is the φ + 180° branch — so a pattern sampled on a half-
//      circle of φ has half a cut, and half a cut has no second half-power point.
//
// And one more that is about the PATTERN rather than the plane: the cut may have no −3 dB crossing
// inside 0…θ_max at all. On an infinite ground plane U → 0 at grazing so there is always one, but a
// user-named cut through a null, or a very broad pattern on a future finite-ground model, need not
// have one. Refused by name, with the lowest level the cut actually reached, because "the beam is
// wider than the sampled range" and "the beamwidth is 180°" are different statements.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>ANT-5 §4's cut derivation and the 3 dB crossing search. See the file header.</summary>
public static class PlanarBeamwidth
{
    /// <summary>
    /// The current moment (M_x, M_y) in A·m at one lateral wavenumber, summed over every level —
    /// <c>RooftopSpectrum.Of</c>, which at k = 0 is the rooftop's plain dipole moment and therefore
    /// shares its normalisation with the pattern's LEVEL rather than restating it. See the file header
    /// for why the caller evaluates this at the PEAK rather than at k = 0.
    /// </summary>
    public static (Complex X, Complex Y) CurrentMoment(PlanarMesh mesh, Vec<Complex> basisCurrents,
                                                       double kx = 0, double ky = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Complex mx = Complex.Zero, my = Complex.Zero;
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var basis = mesh.Bases[b];
            if (basis.Direction == PlanarBasisDirection.Z || basis.AttachesToGround) continue;
            Complex t = basisCurrents[b] * RooftopSpectrum.Of(mesh, basis, kx, ky);
            if (basis.Direction == PlanarBasisDirection.X) mx += t; else my += t;
        }
        return (mx, my);
    }

    /// <summary>
    /// The dominant current axis: the major-axis azimuth of the current moment's polarization ellipse,
    /// in degrees over [0, 180), together with the two principal values that say whether there IS one.
    /// </summary>
    public static (double AxisDeg, double Major, double Minor) DominantAxis(
        PlanarMesh mesh, Vec<Complex> basisCurrents, double kx = 0, double ky = 0)
    {
        var (mx, my) = CurrentMoment(mesh, basisCurrents, kx, ky);
        double ax = mx.Magnitude * mx.Magnitude, ay = my.Magnitude * my.Magnitude;
        double cross = 2.0 * (mx * Complex.Conjugate(my)).Real;
        double root = Math.Sqrt((ax - ay) * (ax - ay) + cross * cross);
        double major = 0.5 * (ax + ay + root), minor = 0.5 * (ax + ay - root);
        double axis = 0.5 * Math.Atan2(cross, ax - ay) * 180.0 / Math.PI;
        if (axis < 0) axis += 180.0;
        return (axis, major, Math.Max(minor, 0));
    }

    /// <summary>
    /// <b>The dominant current axis AT THE PATTERN'S OWN PEAK</b> — the one call that answers "which
    /// way does this structure's current point", so that ANT-5's beamwidth cut and ANT-6's Ludwig-3
    /// reference angle are the SAME NUMBER rather than two derivations of one physical quantity.
    ///
    /// <para>See the file header for why the transform is evaluated at the peak direction and not at
    /// k = 0, and for what the two principal values mean: <c>Major</c> = 0 is no net moment at all and
    /// <c>Minor/Major</c> past <see cref="PlanarMetricSettings.AxisAmbiguityRatio"/> is a current with
    /// no dominant linear axis, which a circularly polarized structure reads as 1.0 exactly. Both
    /// callers refuse on those, in their own words.</para>
    /// </summary>
    public static (double AxisDeg, double Major, double Minor) AxisAtPatternPeak(
        PlanarMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        double kRho = 2.0 * Math.PI * context.Pattern.FrequencyHz / EmConstants.C0
                      * Math.Sin(context.Peak.ThetaDeg * Math.PI / 180.0);
        var (sinP, cosP) = Math.SinCos(context.Peak.PhiDeg * Math.PI / 180.0);
        return DominantAxis(context.Mesh, context.BasisCurrents, kRho * cosP, kRho * sinP);
    }

    /// <summary>
    /// Every beamwidth cut for one pattern, or the reason there are none. See the file header for the
    /// four things this refuses and why each would otherwise be a guess.
    /// </summary>
    public static PlanarBeamCuts Cuts(PlanarMetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var grid = context.Pattern.Grid;

        var named = context.Settings.BeamwidthCutsPhiDeg;
        bool derived = named is null || named.Count == 0;

        double[] wanted;
        string note;
        if (!derived)
        {
            wanted = named!.ToArray();
            note = $"The beamwidth cut(s) were NAMED by the caller: " +
                   string.Join(", ", wanted.Select(p => $"φ = {p:G6}°")) + ".";
        }
        else
        {
            // At the PEAK direction — see the file header for why not at k = 0. ANT-6's Ludwig-3
            // reference angle reads the SAME call, so the derived co-polar plane and the derived
            // E-plane cut cannot disagree.
            var (axis, major, minor) = AxisAtPatternPeak(context);
            if (!(major > 0))
                return new PlanarBeamCuts(EmSuitability.No(
                    "No beamwidth cut could be derived: the structure's CURRENT MOMENT at the peak " +
                    "direction is zero, so there is no dominant current axis and therefore no " +
                    "principal plane to report a beamwidth in. That happens on a balanced or " +
                    "perfectly symmetric differential structure, where the moment cancels exactly " +
                    "in every direction. Name a cut explicitly (a φ in " +
                    "degrees) and the beamwidth in that plane is reported; it is refused rather than " +
                    "defaulted to φ = 0, because a 3 dB beamwidth with the wrong plane attached is " +
                    "worse than no beamwidth."), [], "");

            double ratio = minor / major;
            if (ratio > PlanarMetricSettings.AxisAmbiguityRatio)
                return new PlanarBeamCuts(EmSuitability.No(
                    $"No beamwidth cut could be derived: the current has no dominant linear axis. The " +
                    $"current moment at the peak has a polarization ellipse whose minor axis carries " +
                    $"{ratio:P1} of the major one, past the " +
                    $"{PlanarMetricSettings.AxisAmbiguityRatio:P0} this derivation is allowed — a " +
                    $"CIRCULARLY POLARIZED structure reads 1.0 here exactly, and a square patch " +
                    $"driven in both modes reads close to it. Such a structure genuinely has no " +
                    $"principal plane, and inventing one would be the same class of error as " +
                    $"inventing a current direction for the mesher. Name the cuts you want (φ in " +
                    $"degrees) — for a circularly polarized antenna the axial ratio, not a beamwidth, " +
                    $"is the number that describes it."), [], "");

            // An axis is a LINE, so φ_c and φ_c + 180° name the SAME plane — take whichever half
            // faces the peak, so the cut's own peak lands in its positive θ branch and the reported
            // peak θ is not a confusing negative. Measured: on a 3 λ_g line with its peak at θ = 31°,
            // φ = 0° the transverse rooftops across the line's width give the ellipse a cross term of
            // round-off size and a SIGN, so the unfolded axis came out as 180.00° — the right plane
            // reported back to front.
            // ── ANT-12 — THE FOLD MAY NOT BE TAKEN AGAINST A DEGENERATE PEAK AZIMUTH ─────────
            //
            // The fold below exists for a peak that is genuinely off broadside, where φ_peak names a
            // direction. At θ_peak = 0 every azimuth names the SAME direction — which is exactly why
            // DirectivityPeakPhiDeg refuses there — so φ_peak is then whichever grid value the peak
            // search happened to land on, and folding against it makes the reported plane a function
            // of that. Measured on the shipped 5.8 GHz patch: the derived cut came out as 90° at most
            // frequencies and 270° at two of them, the SAME plane reported two ways, and
            // PlanarMetricSet.From then refused the beamwidth for the whole sweep on the grounds that
            // the planes disagreed. They never did.
            //
            // With no usable azimuth to face, the canonical representative is the one in [0, 180) —
            // a plane is a line, so that names it exactly once.
            double folded = axis;
            if (context.Peak.AzimuthIsDegenerate)
                folded = ((axis % 180.0) + 180.0) % 180.0;
            else if (Math.Abs(Delta(axis, context.Peak.PhiDeg)) > 90.0)
                folded = (axis + 180.0) % 360.0;

            wanted = [folded];
            note = $"The beamwidth cut was DERIVED, not named: the current transform at the pattern's " +
                   $"peak (θ = {context.Peak.ThetaDeg:G6}°, φ = {context.Peak.PhiDeg:G6}°) has its " +
                   $"dominant axis at φ = {folded:F2}° (minor/major = {ratio:E2}), and the cut is the " +
                   $"plane containing that axis.";
        }

        var cuts = new List<PlanarBeamCut>(wanted.Length);
        foreach (double want in wanted)
        {
            double front = ((want % 360.0) + 360.0) % 360.0;
            double back  = (front + 180.0) % 360.0;

            // NearestAzimuth, not Nearest: the wanted azimuth is a POINT ON A CIRCLE and the miss is
            // measured three lines down with a WRAPPED delta, so a linear lookup here would reject a
            // snap the tolerance accepts. A patch fed along x derives its axis at 0 or 180 and
            // round-off decides the side, so `front` lands at 359.99 as often as at 0.01 — see
            // PlanarMetrics.NearestAzimuth for the board that measured it.
            int ifr = PlanarMetrics.NearestAzimuth(grid.PhiDeg, front);
            int iba = PlanarMetrics.NearestAzimuth(grid.PhiDeg, back);
            double snapF = grid.PhiDeg[ifr], snapB = grid.PhiDeg[iba];

            // Half a cut has no second half-power point. The tolerance is half the FINEST φ step,
            // which is strict on a graded grid and is the safe direction to be strict in — the
            // alternative, half the coarsest, counts a half-circle grid's own 225° wrap-around gap as
            // "a step" and then accepts a back azimuth 45° away from where the cut needs one.
            double tol = 0.5 * FinestStep(grid.PhiDeg);
            if (Math.Abs(Delta(snapF, front)) > tol || Math.Abs(Delta(snapB, back)) > tol)
                return new PlanarBeamCuts(EmSuitability.No(
                    $"The beamwidth cut at φ = {want:G6}° cannot be taken on this grid: a cut runs " +
                    $"from −θ_max through broadside to +θ_max, and the negative half is the " +
                    $"φ + 180° branch — so BOTH φ = {front:G6}° and φ = {back:G6}° have to be " +
                    $"sampled. The nearest sampled azimuths are {snapF:G6}° and {snapB:G6}°, which " +
                    $"is further than half a grid step ({tol:G6}°) from what the cut needs. A " +
                    $"pattern sampled over only part of the azimuth circle has half a cut, and half " +
                    $"a cut has only one half-power point. Sample the full circle, or name a cut the " +
                    $"grid does have."), [], note);

            var (bw, peakTheta, reached) = Width(context.Pattern, ifr, iba);
            if (!(bw > 0))
                return new PlanarBeamCuts(EmSuitability.No(
                    $"The cut at φ = {snapF:G6}° has no 3 dB crossing inside the sampled θ range. " +
                    $"Its own peak is at θ = {peakTheta:G6}° and the lowest level the cut reaches " +
                    $"anywhere in 0…{grid.ThetaDeg[^1]:G6}° is {reached:F2} dB below it, so there is " +
                    $"no half-power point to measure between. \"The beam is wider than the sampled " +
                    $"range\" and \"the beamwidth is {2 * grid.ThetaDeg[^1]:G6}°\" are different " +
                    $"statements and this refuses rather than printing the second one."),
                    [], note);

            cuts.Add(new PlanarBeamCut(snapF, want, derived, bw, peakTheta));
        }

        return new PlanarBeamCuts(EmSuitability.Yes, cuts, note);
    }

    /// <summary>The smallest gap between consecutive sampled azimuths. The wrap-around gap is
    /// deliberately NOT included: on a grid that does not close the circle it is the symptom, not a
    /// step, and counting it is what made a half-circle grid look finely sampled.</summary>
    private static double FinestStep(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 360.0;
        double best = double.PositiveInfinity;
        for (int i = 1; i < values.Count; i++) best = Math.Min(best, values[i] - values[i - 1]);
        return best;
    }

    /// <summary>Signed angular difference a − b, folded into (−180, 180].</summary>
    private static double Delta(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>
    /// The 3 dB width of one cut, built as a signed-θ profile running from −θ_max (on the φ + 180°
    /// branch) through broadside to +θ_max. Crossings are found by LINEAR INTERPOLATION OF U against
    /// θ, which is stated because interpolating in dB instead moves the answer by a fraction of a grid
    /// step and there is no reason to do both.
    /// </summary>
    private static (double WidthDeg, double PeakThetaDeg, double LowestDb) Width(
        PlanarFarFieldPattern pattern, int frontPhi, int backPhi)
    {
        var g  = pattern.Grid;
        int nt = g.ThetaDeg.Count;

        // Signed profile. θ = 0 is shared by the two halves and appears once.
        var theta = new List<double>(2 * nt);
        var u     = new List<double>(2 * nt);
        for (int i = nt - 1; i >= 0; i--)
        {
            if (g.ThetaDeg[i] == 0) continue;
            theta.Add(-g.ThetaDeg[i]);
            u.Add(pattern.U[g.IndexOf(i, backPhi)]);
        }
        for (int i = 0; i < nt; i++)
        {
            theta.Add(g.ThetaDeg[i]);
            u.Add(pattern.U[g.IndexOf(i, frontPhi)]);
        }

        int top = 0;
        for (int i = 1; i < u.Count; i++) if (u[i] > u[top]) top = i;
        double peak = u[top];
        if (!(peak > 0)) return (0, theta[top], 0);

        double half = 0.5 * peak;
        double lowest = peak;
        foreach (double value in u) lowest = Math.Min(lowest, value);

        double? hi = null, lo = null;
        for (int i = top; i + 1 < u.Count; i++)
            if (u[i] >= half && u[i + 1] < half)
            { hi = Lerp(theta[i], u[i], theta[i + 1], u[i + 1], half); break; }
        for (int i = top; i - 1 >= 0; i--)
            if (u[i] >= half && u[i - 1] < half)
            { lo = Lerp(theta[i], u[i], theta[i - 1], u[i - 1], half); break; }

        double lowDb = 10.0 * Math.Log10(Math.Max(lowest, double.Epsilon) / peak);
        return hi is { } a && lo is { } b ? (a - b, theta[top], lowDb) : (0, theta[top], lowDb);
    }

    private static double Lerp(double x0, double y0, double x1, double y1, double y) =>
        y1 == y0 ? x0 : x0 + (x1 - x0) * (y - y0) / (y1 - y0);
}
