// ANT-4 — the far field: what kernel B's currents radiate, and the first thing in this directory
// that looks AWAY from the metal.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS IS NOT A SECOND L8a, AND WHY THERE IS NO DCIM ANYWHERE IN IT
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// DCIM exists because the matrix FILL needs the Green's function in the SPATIAL domain, which means
// inverting a Sommerfeld integral that is oscillatory, slowly convergent and full of branch points
// and poles. The far field needs the SPECTRAL function at exactly ONE point per direction,
// k_ρ = k₀ sin θ, by stationary phase — which SpectralGreens and LayeredSpectralGreens already
// return in closed form. So:
//
//   R-ant-1. THE FAR FIELD NEVER TOUCHES Dcim, SommerfeldIntegral OR ANY SPATIAL-DOMAIN KERNEL.
//   Routing it through one would import a validated-range refusal it does not need
//   (Dcim.WithinValidatedRange) and an approximation it does not have. This is asserted by a source
//   scan in PlanarFarFieldTests, so an innocent-looking refactor cannot leak it back in.
//
// The current side is closed form too — the 2-D Fourier transform of a rooftop on a rectangular
// cell pair is elementary — so the whole pattern is an EXACT sum over N basis coefficients with no
// quadrature error of its own, at O(N) per direction.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE DERIVATION. Nothing below is transcribed; it is built from THIS repository's own conventions.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Conventions, all of them already fixed elsewhere in this directory:
//
//   • e^{jωt}, and the PROPER sheet is Im k_z ≤ 0 (SpectralGreens.ProperRoot).
//   • The transform pair is SommerfeldIntegral's: G(ρ) = (1/2π)∫G̃ J₀(k_ρρ)k_ρ dk_ρ, i.e.
//     G̃(k) = ∫G(r)e^{+jk⃗·r⃗}d²r and G(r) = (1/2π)²∫G̃(k)e^{−jk⃗·r⃗}d²k. So a source displaced to
//     r₀ carries e^{+jk⃗·r⃗₀}, which is the physically right sign: |r − r₀| ≈ r − r̂·r₀.
//   • The transmission-line map is SpectralGreens' own (its L9c block comment): with ∇_t → −jk_ρû,
//     v̂ = ẑ × û, V^e = E_u, I^e = H_v, V^h = E_v, I^h = −H_u, a horizontal current enters BOTH
//     lines as a SHUNT CURRENT SOURCE of strength i^e = −Ĵ_u, i^h = −Ĵ_v, and
//     E_z = −(jk_ρI^e + Ĵ_zδ)/(jωε).
//
// ── 1. The spectral field of the surface current, in the top half-space ───────────────────────
//
// With V_i^p(z|z′) the line voltage for a 1 A shunt source (LayeredSpectralGreens.Voltage) and
// I_i^p its current (…Current):
//
//     Ẽ_u = −Ĵ_u V_i^e(z|z′)          Ẽ_v = −Ĵ_v V_i^h(z|z′)          Ẽ_z = (k_ρ/k_z0) Ĵ_u V_i^e
//
// (the last from E_z = −jk_ρI^e/(jωε₀) with I^e = −Ĵ_u I_i^e and, in the top half-space above the
// source, I_i^e = V_i^e/Z₀^e.) The consistency check that this IS L8a's kernel and not a second
// statement of it: for a v̂-directed current there is no TM excitation and no charge, so
// E_v = −jωA_v = −jωµ₀G̃_A Ĵ_v = −V_i^h Ĵ_v exactly, using G̃_A = V_i^h/(jωµ₀). It agrees.
//
// With θ̂ = (cosθcosφ, cosθsinφ, −sinθ) and φ̂ = v̂:
//
//     Ẽ_θ = cosθ Ẽ_u − sinθ Ẽ_z = −Ĵ_u V_i^e (cosθ + sin²θ/cosθ) = −Ĵ_u V_i^e / cosθ
//     Ẽ_φ = Ẽ_v                                                    = −Ĵ_v V_i^h
//
// ── 2. Stationary phase, PINNED BY THE WEYL IDENTITY rather than quoted ───────────────────────
//
// For a spectrum written as F(k)e^{−jk_z0 z} the r → ∞ limit of (1/2π)²∫F e^{−jk⃗·ρ⃗−jk_z0z}d²k is
// C(r,θ)·F(k^s) at k^s = k₀ sinθ(cosφ, sinφ). C is fixed WITHOUT quoting a saddle-point formula:
// apply it to F = 1/(2jk_z0), whose exact inverse transform is e^{−jk₀R}/4πR (the Sommerfeld/Weyl
// identity SommerfeldIntegral is built on), whose far field is e^{−jk₀r}/4πr. Hence
//
//     C(r,θ) = 2j k₀cosθ · e^{−jk₀r}/(4πr) = j k₀ cosθ e^{−jk₀r}/(2πr).
//
// ── 3. The answer, and the two element factors ────────────────────────────────────────────────
//
// Writing V_i^p(z|z′) = (Z₀^p/2)·T^p·e^{−jk_z0 z} in the top half-space (exact there: the wave is
// purely up-going, so this is a definition of T^p, not an approximation), with Z₀^e = η₀cosθ and
// Z₀^h = η₀/cosθ, and defining the r-normalised pattern F = lim r e^{+jk₀r} E:
//
//     F_θ(θ,φ) = −(j k₀ η₀/4π) · f_TM(θ) · [ J̃_x cosφ + J̃_y sinφ ]
//     F_φ(θ,φ) = −(j k₀ η₀/4π) · f_TE(θ) · [ −J̃_x sinφ + J̃_y cosφ ]
//
//     f_TM(θ) = cosθ · T^e(θ)          f_TE(θ) = T^h(θ)
//
// **BOTH ELEMENT FACTORS ARE WRITTEN SO THAT NOTHING IS EVER DIVIDED BY cosθ, AND THAT IS NOT
// TIDINESS.** The obvious spelling of f_TM carries a 1/cosθ from Ẽ_θ and a cosθ from C, and the
// obvious spelling of f_TE carries Z₀^h = ωµ₀/k_z0 — both infinite at θ = 90°, which is a point the
// θ axis (§4 below) actually contains. Written as
//
//     f_TM = 2 V_i^e(z_obs|z′) e^{+jk_z0 z_obs} / η₀
//     f_TE = 2 I_i^h(z_obs|z′) e^{+jk_z0 z_obs}          (or 2cosθ V_i^h e^{+jk_z0 z_obs}/η₀)
//
// every quantity is finite there. See FarFieldElementFactors.At for which of the two f_TE spellings
// is used where, and why the choice is forced rather than stylistic.
//
// **THE PHASE REFERENCE IS THE STACK'S OWN ORIGIN (0, 0, z = 0)** — the same origin the layout
// coordinates and LayerStack's z are written in. That is why T^p carries an e^{+jk_z0 z′} and not 1:
// referring the lateral phase to x = y = 0 and the vertical phase to the source plane would put a
// θ-dependent phase error into any pattern with metal on more than one level.
//
// ── 4. THE θ AXIS SPANS 0…90° ONLY, AND THE RUN SAYS WHY ──────────────────────────────────────
//
// With an analytically infinite ground plane there is no field at θ > 90° — not small, identically
// zero, by construction (every dielectric layer and the plane itself are laterally infinite;
// brief-antenna-0-overview.md §2). A 0…180° axis half full of structural zeros invites a polar plot
// that looks like a measurement of a very good antenna, and nothing in a plot can tell a user that
// the lower half is a property of the MEDIUM rather than of their design. A missing axis range with
// a stated reason cannot be misread; a zero can.
//
// **This is the first half of the staged front-to-back refusal.** ANT-5 creates the metric entry,
// present and refused; ANT-11 extends this axis to 180° and NARROWS the refusal. Nothing here is
// written in a way that makes that a rewrite: PlanarFarFieldGrid.ThetaDeg is DATA, and
// MaxThetaDeg is the one constant that moves.
//
// ── 5. WHAT IS REFUSED BY NAME, AND WHY GUESSING WOULD BE WORSE THAN REFUSING ─────────────────
//
// Each of these is representable in the types and is genuinely not computed here. R-mom-17's shape:
// name the thing, name what it would take.
//
//   • A CUT (conformal) CELL. PlanarCell.Region means a boundary cell's metal is not its rectangle,
//     and the transform of a cut cell is not the rectangle's. Using the rectangle's would give a
//     smooth, plausible, WRONG far field — the exact failure mode this directory's CLAUDE.md warns
//     about for a neighbouring pole. PlanarBoundaryCells.Staircase (the default) produces a mesh
//     this can transform.
//   • A VERTICAL (via or ground-attachment) BASIS. A z-directed current radiates — TM only, and
//     through a SERIES voltage source on the TM line rather than a shunt current one, so it is a
//     different element factor and a different transform, with its own oracle. Dropping it silently
//     would be a pattern that is right in shape and wrong in level on exactly the structures where
//     it matters (a probe-fed patch, a via-fenced board). Refused, not approximated.
//   • A STACK THAT IS NOT PEC BELOW, or whose top termination is not an open AIR half-space. The
//     first is what §4's θ range rests on; the second is what makes η₀ and k₀ the right constants.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The directions a pattern is evaluated at. <b>θ is bounded by <see cref="MaxThetaDeg"/> and that
/// bound is DATA, not an assumption baked into the arithmetic</b> — see the file header §4 and
/// <c>brief-antenna-0-overview.md</c> §4's staged front-to-back refusal.
/// </summary>
/// <param name="ThetaDeg">Polar angle from the +z axis (the side the metal radiates into), degrees,
/// ascending.</param>
/// <param name="PhiDeg">Azimuth from the +x axis, degrees, ascending. The default sampling spans
/// <b>[0, 360)</b> and deliberately omits 360°, which is a duplicate of 0° — a duplicated node is a
/// double-counted sample in the hemisphere integral and a doubled point in a cut.</param>
public sealed record PlanarFarFieldGrid(IReadOnlyList<double> ThetaDeg, IReadOnlyList<double> PhiDeg)
{
    /// <summary><b>90°, and this is the whole of §4's decision expressed as a number.</b> ANT-11
    /// moves it to 180 when a finite ground outline can be estimated; nothing else changes.</summary>
    public const double MaxThetaDeg = 90.0;

    /// <summary>The upper hemisphere at a stated step, θ inclusive of both ends, φ over [0, 360).</summary>
    public static PlanarFarFieldGrid Hemisphere(double thetaStepDeg = 1.0, double phiStepDeg = 1.0)
    {
        if (!(thetaStepDeg > 0) || thetaStepDeg > MaxThetaDeg)
            throw new ArgumentOutOfRangeException(nameof(thetaStepDeg), thetaStepDeg,
                $"The θ step must be positive and no larger than {MaxThetaDeg}°.");
        if (!(phiStepDeg > 0) || phiStepDeg > 360.0)
            throw new ArgumentOutOfRangeException(nameof(phiStepDeg), phiStepDeg,
                "The φ step must be positive and no larger than 360°.");

        int nt = (int)Math.Round(MaxThetaDeg / thetaStepDeg);
        var theta = new double[nt + 1];
        for (int i = 0; i <= nt; i++) theta[i] = i == nt ? MaxThetaDeg : i * thetaStepDeg;

        int np = Math.Max(1, (int)Math.Round(360.0 / phiStepDeg));
        var phi = new double[np];
        for (int i = 0; i < np; i++) phi[i] = i * (360.0 / np);

        return new PlanarFarFieldGrid(theta, phi);
    }

    public int DirectionCount => ThetaDeg.Count * PhiDeg.Count;

    /// <summary>Row-major index into a [theta, phi] plane.</summary>
    public int IndexOf(int thetaIndex, int phiIndex) => thetaIndex * PhiDeg.Count + phiIndex;

    /// <summary>
    /// R-mom-17 on the grid itself: a θ beyond <see cref="MaxThetaDeg"/> is refused rather than
    /// filled with the structural zero the infinite ground plane puts there.
    /// </summary>
    public EmSuitability CanUse()
    {
        if (ThetaDeg.Count == 0 || PhiDeg.Count == 0)
            return EmSuitability.No("A far-field grid needs at least one θ and one φ.");
        for (int i = 0; i < ThetaDeg.Count; i++)
        {
            if (ThetaDeg[i] < 0 || ThetaDeg[i] > MaxThetaDeg)
                return EmSuitability.No(
                    $"θ = {ThetaDeg[i]:G6}° is outside 0…{MaxThetaDeg:F0}°. The ground plane and every " +
                    $"dielectric layer are laterally INFINITE in this analysis, so the field below the " +
                    $"plane is not small — it is identically zero by construction, and an axis padded " +
                    $"with those zeros would read as a measured front-to-back ratio. A finite ground " +
                    $"outline, and with it a θ axis to 180°, is a separate phase.");
            if (i > 0 && !(ThetaDeg[i] > ThetaDeg[i - 1]))
                return EmSuitability.No("The θ values must ascend and must not repeat.");
        }
        for (int i = 1; i < PhiDeg.Count; i++)
            if (!(PhiDeg[i] > PhiDeg[i - 1]))
                return EmSuitability.No("The φ values must ascend and must not repeat.");
        return EmSuitability.Yes;
    }
}

/// <summary>
/// One driven port's radiated field at one frequency, over one <see cref="PlanarFarFieldGrid"/>.
///
/// <para><b>The excitation is the same one <see cref="PlanarCurrentDensity"/> uses and for the same
/// reason</b>: ONE driven port, whose delta gap is held at 1 V while every other port's gap is held
/// at 0 V — exactly the excitation column <i>j</i> of <c>Y = BᵀZ⁻¹B</c> means. A pattern that
/// superposed every excitation would be a pattern of nothing.</para>
///
/// <para><b>The r-normalisation, stated once because nobody notices it in a plot.</b> The stored
/// value is <c>F = lim_{r→∞} r·e^{+jk₀r}·E</c>, in VOLTS — the field times r with the propagation
/// phase removed. It is not "E at 1 m": at 1 m from a 2 GHz antenna you are not in the far field,
/// and the two differ by a factor nobody sees until they compare against a measurement.</para>
/// </summary>
/// <param name="ETheta">F_θ per direction, row-major [theta, phi]. Volts.</param>
/// <param name="EPhi">F_φ per direction, row-major [theta, phi]. Volts.</param>
/// <param name="U">Radiation intensity <c>(|F_θ|² + |F_φ|²)/2η₀</c>, W/sr. Derived, but stored,
/// because every metric downstream reads it.</param>
public sealed record PlanarFarFieldPattern(
    PlanarFarFieldGrid     Grid,
    IReadOnlyList<Complex> ETheta,
    IReadOnlyList<Complex> EPhi,
    IReadOnlyList<double>  U,
    int                    DrivenPort,
    double                 FrequencyHz)
{
    /// <summary>
    /// <b>∫U dΩ over the upper hemisphere</b> — the power this pattern actually carries away,
    /// watts. Trapezoidal in θ with the sinθ Jacobian; in φ the rule is the periodic rectangle when
    /// the grid samples the whole circle uniformly (which is spectrally exact for a periodic
    /// integrand) and the trapezoid otherwise.
    ///
    /// <para><b>It is a quadrature over a sampled grid and therefore NOT exact</b>, unlike
    /// everything else in this file. On a 1°×1° grid it is worth ~1e-4 relative on a patch-sized
    /// pattern; a metric that needs better should say so and integrate on its own grid.</para>
    /// </summary>
    public double RadiatedPowerW => IntegrateU();

    private double IntegrateU()
    {
        int nt = Grid.ThetaDeg.Count, np = Grid.PhiDeg.Count;
        var wt = ThetaWeights();
        var wp = PhiWeights();
        double sum = 0;
        for (int it = 0; it < nt; it++)
        {
            double s = Math.Sin(Grid.ThetaDeg[it] * Math.PI / 180.0) * wt[it];
            if (s == 0) continue;
            double row = 0;
            for (int ip = 0; ip < np; ip++) row += U[Grid.IndexOf(it, ip)] * wp[ip];
            sum += s * row;
        }
        return sum;
    }

    private double[] ThetaWeights()
    {
        int n = Grid.ThetaDeg.Count;
        var w = new double[n];
        if (n == 1) { w[0] = 0; return w; }
        const double D = Math.PI / 180.0;
        for (int i = 0; i < n; i++)
        {
            double lo = i == 0     ? Grid.ThetaDeg[0]     : Grid.ThetaDeg[i - 1];
            double hi = i == n - 1 ? Grid.ThetaDeg[n - 1] : Grid.ThetaDeg[i + 1];
            w[i] = 0.5 * (hi - lo) * D;
        }
        return w;
    }

    private double[] PhiWeights()
    {
        int n = Grid.PhiDeg.Count;
        var w = new double[n];
        const double D = Math.PI / 180.0;
        if (n == 1) { w[0] = 2.0 * Math.PI; return w; }

        double step = Grid.PhiDeg[1] - Grid.PhiDeg[0];
        bool uniformCircle = step > 0 && Math.Abs(Grid.PhiDeg[^1] + step - (Grid.PhiDeg[0] + 360.0)) < 1e-9 * 360.0;
        for (int i = 1; i < n && uniformCircle; i++)
            uniformCircle = Math.Abs((Grid.PhiDeg[i] - Grid.PhiDeg[i - 1]) - step) < 1e-9 * 360.0;

        if (uniformCircle)
        {
            for (int i = 0; i < n; i++) w[i] = step * D;
            return w;
        }
        for (int i = 0; i < n; i++)
        {
            double lo = i == 0     ? Grid.PhiDeg[0]     : Grid.PhiDeg[i - 1];
            double hi = i == n - 1 ? Grid.PhiDeg[n - 1] : Grid.PhiDeg[i + 1];
            w[i] = 0.5 * (hi - lo) * D;
        }
        return w;
    }

    /// <summary>The largest |F| on the grid, volts — a pattern's own normalisation, which R-res-8
    /// requires be SHOWN rather than implied.</summary>
    public double PeakFieldV
    {
        get
        {
            double m = 0;
            for (int i = 0; i < U.Count; i++)
            {
                double e = ETheta[i].Magnitude * ETheta[i].Magnitude + EPhi[i].Magnitude * EPhi[i].Magnitude;
                if (e > m) m = e;
            }
            return Math.Sqrt(m);
        }
    }

    /// <summary>R-res-8 — the scale, its units, its normalisation and its θ range, as one line a
    /// panel or a CLI run prints verbatim.</summary>
    public string ScaleCaption =>
        $"Far field, port {DrivenPort} driven at 1 V, {SurfaceMesher.Eng(FrequencyHz)}Hz. " +
        $"E_θ and E_φ are r-normalised patterns (r·E with e^{{−jk₀r}} removed), peak " +
        $"{SurfaceMesher.Eng(PeakFieldV)}V; radiation intensity U peaks at " +
        $"{SurfaceMesher.Eng(PeakIntensityWPerSr)}W/sr and integrates to " +
        $"{SurfaceMesher.Eng(RadiatedPowerW)}W over the hemisphere. " + ThetaRangeNote;

    public double PeakIntensityWPerSr
    {
        get { double m = 0; foreach (double u in U) if (u > m) m = u; return m; }
    }

    /// <summary>§4's decision, worded once so the engine, the CLI and the Data Display cannot drift.</summary>
    public const string ThetaRangeNote =
        "θ spans 0…90° only: the ground plane and every dielectric layer are laterally infinite in " +
        "this analysis, so the field below the plane is not small but identically zero by " +
        "construction. An axis padded with those zeros would read as a measured front-to-back ratio.";
}

/// <summary>
/// <b>The 2-D Fourier transform of one rooftop, in closed form.</b> Treated exactly as L8c's six
/// inner integrals were: derived once, in one place, with its normalisation stated, and gated
/// against adaptive quadrature to 1e-12.
///
/// <para><b>The normalisation is L8c's own and must not be re-derived here.</b>
/// <c>PlanarBasisFunctions</c>' header records that a rooftop is normalised to UNIT TOTAL CURRENT
/// ACROSS ITS SHARED EDGE, so a basis coefficient <c>I_b</c> is in AMPERES and the basis function
/// itself is a sheet density in 1/m. The transform of <c>I_b f_b</c> is therefore in A·m — a current
/// MOMENT, the same dimension as the <c>Iℓ</c> of a Hertzian dipole. Getting this wrong gives a
/// pattern that is right in shape and wrong in level, which no plot reveals.</para>
/// </summary>
public static class RooftopSpectrum
{
    /// <summary>Below this |k·w| the elementary antiderivatives cancel to nothing and the series is
    /// used instead. At |k·w| = 1 the truncated series is good to ~1e-20 relative.</summary>
    internal const double SeriesThreshold = 1.0;
    private  const int    SeriesTerms     = 24;

    /// <summary><c>e^{jt}</c> without going through <see cref="Complex.Exp"/> for a purely imaginary
    /// argument.</summary>
    internal static Complex Cis(double t)
    {
        var (s, c) = Math.SinCos(t);
        return new Complex(c, s);
    }

    /// <summary>
    /// <c>∫₀^w s·e^{jks} ds</c> — the ramp factor, and the only place in this file where a formula
    /// has to be written twice.
    ///
    /// <para>The elementary form <c>w e^{jkw}/(jk) + (e^{jkw} − 1)/k²</c> is two terms of size
    /// <c>w/|k|</c> and <c>1/k²</c> whose difference is <c>w²/2</c>; at <c>|kw| = 1e-6</c> that is
    /// twelve digits gone, and a rooftop is ALWAYS electrically small, so the small-argument branch
    /// is the one production takes. The series is the definition integrated term by term and needs
    /// no special case at k = 0.</para>
    /// </summary>
    public static Complex Ramp(double k, double w)
    {
        double t = k * w;
        if (Math.Abs(t) <= SeriesThreshold)
        {
            // Σ_{n≥0} (jk)^n w^{n+2} / (n! (n+2))
            Complex term = w * w;                 // n = 0 numerator, before the 1/(n+2)
            Complex sum  = term / 2.0;
            for (int n = 1; n < SeriesTerms; n++)
            {
                term *= new Complex(0, t) / n;    // ×(jk w)/n, keeping the w^{n+2} growth in `term`
                sum  += term / (n + 2);
            }
            return sum;
        }
        Complex e = Cis(t);
        return -Complex.ImaginaryOne * w * e / k + (e - 1.0) / (k * k);
    }

    /// <summary>
    /// <c>∫_a^b e^{jkx} dx</c>, written as <c>(b−a)·e^{jk(a+b)/2}·sinc(k(b−a)/2)</c> so that the
    /// k → 0 limit is reached by a well-conditioned expression rather than by a guard.
    /// </summary>
    public static Complex Pulse(double k, double a, double b)
    {
        double len = b - a, half = 0.5 * k * len;
        double sinc = Math.Abs(half) < 1e-8 ? 1.0 - half * half / 6.0 : Math.Sin(half) / half;
        return len * sinc * Cis(k * 0.5 * (a + b));
    }

    /// <summary>
    /// <b><c>∫ f_b(r) e^{+j(k_x x + k_y y)} dS</c> — the transform of ONE rooftop, per ampere of its
    /// coefficient.</b> An x-directed rooftop is a triangle in x times a rectangle in y, so the
    /// transform is a product of two elementary factors; a y-directed one is the same with the axes
    /// exchanged.
    ///
    /// <para>Refuses a cut cell and a vertical basis BY NAME — see the file header §5. Both are
    /// checked again in <see cref="PlanarFarField.CanCompute"/> so a caller gets a verdict rather
    /// than an exception, but the refusal lives here too because this is the function that would
    /// otherwise return a plausible wrong number.</para>
    /// </summary>
    public static Complex Of(PlanarMesh mesh, PlanarBasis basis, double kx, double ky)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(basis);

        if (basis.Direction == PlanarBasisDirection.Z || basis.AttachesToGround)
            throw new ArgumentException(PlanarFarField.VerticalBasisRefusal, nameof(basis));

        var a = mesh.Cells[basis.CellA];
        var b = mesh.Cells[basis.CellB];
        if (a.IsCut || b.IsCut)
            throw new ArgumentException(PlanarFarField.CutCellRefusal, nameof(basis));

        if (basis.Direction == PlanarBasisDirection.X)
        {
            // The pair shares the edge x = a.XMax = b.XMin and spans one row of the tensor grid, so
            // the two cells have the SAME y extent. Asserted rather than assumed: a merged cell
            // (R-cut-3) breaks it, and a silently mismatched row would be a smooth wrong answer.
            RequireSharedSpan(a.YMin, a.YMax, b.YMin, b.YMax, basis);
            Complex cross = Pulse(ky, a.YMin, a.YMax);
            return cross * (Cis(kx * a.XMin) * Ramp(kx, a.Width) / a.Area
                          + Cis(kx * b.XMax) * Ramp(-kx, b.Width) / b.Area);
        }

        RequireSharedSpan(a.XMin, a.XMax, b.XMin, b.XMax, basis);
        Complex crossX = Pulse(kx, a.XMin, a.XMax);
        return crossX * (Cis(ky * a.YMin) * Ramp(ky, a.Height) / a.Area
                       + Cis(ky * b.YMax) * Ramp(-ky, b.Height) / b.Area);
    }

    private static void RequireSharedSpan(double aLo, double aHi, double bLo, double bHi,
                                          PlanarBasis basis)
    {
        double scale = Math.Max(aHi - aLo, bHi - bLo);
        if (Math.Abs(aLo - bLo) <= 1e-9 * scale && Math.Abs(aHi - bHi) <= 1e-9 * scale) return;
        throw new ArgumentException(
            $"The two cells of this {basis.Direction} rooftop do not span the same transverse extent " +
            $"([{aLo:G6}, {aHi:G6}] against [{bLo:G6}, {bHi:G6}]), so it is not the product of a " +
            $"triangle and a rectangle and this closed form does not describe it. That happens when " +
            $"cells have been MERGED, which only the conformal boundary-cell path does — and that " +
            $"path is already refused by name.", nameof(basis));
    }
}

/// <summary>
/// <b>The layered-medium element factors — the whole of what the far field needs from the Green's
/// function, at one k_ρ per direction.</b> See the file header for the derivation and for why
/// neither expression may be written with a 1/cosθ in it.
///
/// <para><b>Which spectral kernel is used is <see cref="PlanarProblem.RequiresGeneralKernel"/>'s
/// decision and not this class's</b> (R-ant-2). That is the single place the FILL makes the same
/// choice, and a far field that re-derived it could disagree with the currents it is transforming —
/// silently, and only on a stratified stack.</para>
/// </summary>
public sealed class FarFieldElementFactors
{
    private readonly SpectralGreens?        _slab;
    private readonly LayeredSpectralGreens? _general;
    private readonly double[]               _levelZ;
    private readonly bool[]                 _levelIsAtTop;
    private readonly double                 _observeZ;

    /// <summary>η₀ = µ₀c₀, formed from the two constants that are exact rather than from 376.73.</summary>
    public const double Eta0 = EmConstants.Mu0 * EmConstants.C0;

    public double K0 { get; }
    public double FrequencyHz { get; }

    /// <summary>Whether the GENERAL stratified kernel is in use — and it must equal
    /// <c>problem.RequiresGeneralKernel</c>, which is asserted.</summary>
    public bool IsGeneral => _general is not null;

    /// <summary>The conductor levels' heights, in the stack's own z.</summary>
    public IReadOnlyList<double> LevelZ => _levelZ;

    private FarFieldElementFactors(SpectralGreens? slab, LayeredSpectralGreens? general,
                                   double[] levelZ, bool[] atTop, double observeZ, double k0, double fHz)
    {
        _slab = slab; _general = general; _levelZ = levelZ; _levelIsAtTop = atTop;
        _observeZ = observeZ; K0 = k0; FrequencyHz = fHz;
    }

    public static FarFieldElementFactors For(PlanarProblem problem, double fHz)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var ok = PlanarFarField.CanComputeMedium(problem, fHz);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);

        var levels = PlanarLevels.From(problem);
        var z = new double[problem.Layers.Count];
        for (int i = 0; i < z.Length; i++) z[i] = levels.Of(i);

        if (!problem.RequiresGeneralKernel)
        {
            var g = new SpectralGreens(problem.Slab, fHz);
            // L8's D2: the one-slab kernel's Γ is referred to the metal plane z = h, and D2 puts the
            // single conductor level there. PlanarKernel.CanSolve has already refused anything else.
            return new FarFieldElementFactors(g, null, z, [true], problem.Slab.HeightM, g.K0, fHz);
        }

        var stack   = problem.EffectiveStack;
        var layered = new LayeredSpectralGreens(stack, fHz);
        var atTop   = new bool[z.Length];
        for (int i = 0; i < z.Length; i++)
            atTop[i] = Math.Abs(z[i] - stack.TopZ) <= 1e-12 * Math.Max(1.0, stack.TopZ);

        // The observation height. ANY height in the top half-space above every source gives the same
        // element factor — the wave there is purely up-going and the propagator is removed EXACTLY —
        // so this is a choice of conditioning, not of physics. One free-space radian above the stack
        // keeps every factor unimodular for the propagating k_ρ ≤ k₀ the far field is made of, and it
        // is strictly ABOVE the top interface, which matters: the line CURRENT is discontinuous at a
        // shunt source and I_i(z′|z′) is its two-sided average, not the up-going wave.
        double observeZ = stack.TopZ + 1.0 / layered.K0;
        return new FarFieldElementFactors(null, layered, z, atTop, observeZ, layered.K0, fHz);
    }

    /// <summary>
    /// <c>(f_TM, f_TE)</c> for one direction and one conductor level. Both are dimensionless and
    /// both are finite at θ = 90°.
    ///
    /// <para><b>The two f_TE spellings, and why the choice between them is forced.</b> The general
    /// kernel returns V and I from one cascade traversal, but it forms the region's characteristic
    /// impedance explicitly on exactly one of the two paths: <c>Z^h = ωµ/k_z</c> multiplies the
    /// SAME-region voltage and divides the CROSS-region current, and it is infinite at grazing. So
    /// the same-region case (metal on the stack's top surface) reads f_TE off the CURRENT — which
    /// LineResponse computes with no impedance at all — and the cross-region case (a buried or
    /// covered level) reads it off the VOLTAGE, whose cross-region path never divides by the top
    /// region's Z either. The two spellings are algebraically identical and are gated against each
    /// other away from grazing.</para>
    /// </summary>
    public (Complex Tm, Complex Te) At(double thetaRad, int layerIndex)
    {
        double sin = Math.Sin(thetaRad), cos = Math.Cos(thetaRad);
        double kRho = K0 * sin, kz0 = K0 * cos;
        double zp = _levelZ[layerIndex];

        if (_slab is { } s)
        {
            // f_TM = cosθ(1 + Γ^e)e^{+jk_z0 h},  f_TE = (1 + Γ^h)e^{+jk_z0 h}.
            // Γ is SpectralGreens' own, referred to the metal plane (KernelAtHeights' image term is
            // e^{−jk_z0(z + z′ − 2h)}), and the e^{+jk_z0 h} moves the phase reference to z = 0.
            Complex phase = RooftopSpectrum.Cis(kz0 * zp);
            return (cos * (1.0 + s.ReflectionTm(kRho)) * phase,
                          (1.0 + s.ReflectionTe(kRho)) * phase);
        }

        var g = _general!;
        Complex w  = kRho * kRho;
        Complex up = RooftopSpectrum.Cis(kz0 * _observeZ);

        Complex ve = g.Voltage(SurfaceWavePolarization.Tm, w, _observeZ, zp);
        Complex tm = 2.0 * ve * up / Eta0;

        Complex te = _levelIsAtTop[layerIndex]
            ? 2.0 * g.Current(SurfaceWavePolarization.Te, w, _observeZ, zp) * up
            : 2.0 * cos * g.Voltage(SurfaceWavePolarization.Te, w, _observeZ, zp) * up / Eta0;

        return (tm, te);
    }
}

/// <summary>Which directions to evaluate, and at which swept points.</summary>
/// <param name="Grid">Null takes <see cref="PlanarFarFieldGrid.Hemisphere()"/>, a 1°×1° hemisphere.</param>
/// <param name="FrequenciesHz">
/// Which swept points to produce a pattern at; the nearest actual point is used for each. Null or
/// empty takes the single point <see cref="PlanarSolveSettings.CurrentDensityFrequencyHz"/> already
/// selects, so a run that asks for a pattern and says nothing else gets one where the user is
/// already looking. <b>The freq axis is data</b> — a metric-versus-frequency phase widens this list,
/// not the cube's shape.
/// </param>
public sealed record PlanarFarFieldSettings(
    PlanarFarFieldGrid?    Grid          = null,
    IReadOnlyList<double>? FrequenciesHz = null)
{
    public static readonly PlanarFarFieldSettings Default = new();
    public PlanarFarFieldGrid EffectiveGrid => Grid ?? PlanarFarFieldGrid.Hemisphere();
}

/// <summary>Every pattern one sweep produced, on one grid, with the axes the DataSet cubes carry.</summary>
/// <param name="Patterns">Row-major <c>[freq, port]</c>.</param>
public sealed record PlanarFarFieldSet(
    PlanarFarFieldGrid                   Grid,
    IReadOnlyList<double>                FrequenciesHz,
    IReadOnlyList<int>                   PortNumbers,
    IReadOnlyList<PlanarFarFieldPattern> Patterns)
{
    public PlanarFarFieldPattern At(int freqIndex, int portIndex) =>
        Patterns[freqIndex * PortNumbers.Count + portIndex];
}

/// <summary>
/// ANT-4's entry point: basis currents in, an r-normalised pattern out. See the file header for the
/// derivation, the phase reference, the θ-range decision and the four things refused by name.
/// </summary>
public static class PlanarFarField
{
    /// <summary>The group name the diagnostics cubes land under — <b>not "planar"</b>, which carries
    /// kernel B's per-port de-embedding scalars and would make a Data Display trace mean two things.</summary>
    public const string Group = "farfield";

    internal const string CutCellRefusal =
        "This mesh has CONFORMAL (cut) boundary cells, and the far field does not transform one. A " +
        "cut cell's metal is not its rectangle, so the rectangle's Fourier transform is not its own " +
        "— using it would give a smooth, plausible, WRONG pattern rather than a visible failure, " +
        "which is exactly the class of error conformal cells were added to remove elsewhere. Mesh " +
        "with PlanarBoundaryCells.Staircase (the default) to get a pattern; the cut-cell transform " +
        "is a separate piece of work and is named as not built.";

    internal const string VerticalBasisRefusal =
        "This mesh carries VERTICAL (via or ground-attachment) current, and the far field does not " +
        "radiate it. A z-directed current excites the TM line as a SERIES VOLTAGE source rather than " +
        "a shunt current one, so it has its own element factor and its own transform, and it needs " +
        "its own independent oracle before it can be trusted. Dropping it silently would give a " +
        "pattern that is right in shape and wrong in level on exactly the structures where a via " +
        "matters — a probe-fed patch, a via-fenced board. Compute the pattern of a layout whose " +
        "current is entirely in-plane (edge- or inset-fed), or wait for the vertical-current phase.";

    /// <summary>
    /// The medium half of the verdict — everything that can be asked of the problem alone, which is
    /// what <see cref="FarFieldElementFactors.For"/> needs and what a caller can check before it has
    /// a mesh.
    /// </summary>
    public static EmSuitability CanComputeMedium(PlanarProblem problem, double fHz)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (!(fHz > 0))
            return EmSuitability.No($"Frequency {fHz} Hz; a far field needs f > 0.");

        var stack = problem.EffectiveStack;

        if (stack.Top.Kind != TerminationKind.HalfSpace)
            return EmSuitability.No(
                $"The stack is closed above by a {stack.Top} and there is no upper half-space for a " +
                $"far field to exist in. A radiating structure needs an open top; that is what makes " +
                $"the radiation condition exact in this kernel and it is not an approximation that " +
                $"can be relaxed here.");

        var top = stack.MaterialOfRegion(stack.RegionCount - 1);
        if (Math.Abs(top.EpsR - 1.0) > 1e-12 || Math.Abs(top.MuR - 1.0) > 1e-12 || top.TanD != 0)
            return EmSuitability.No(
                $"The top half-space is not free space (εᵣ = {top.EpsR:G6}, µᵣ = {top.MuR:G6}, " +
                $"tanδ = {top.TanD:G6}). The far field is written in k₀ and η₀ — the wavenumber and " +
                $"wave impedance of the medium the observer stands in — and a pattern computed with " +
                $"free-space constants in a dielectric half-space would be wrong in level and in " +
                $"beamwidth without being wrong in shape. A structure radiating into a dielectric " +
                $"half-space is not built.");

        if (stack.Bottom.Kind != TerminationKind.Pec)
            return EmSuitability.No(
                $"The stack is terminated below by a {stack.Bottom} rather than by a ground plane. " +
                $"The θ axis spans 0…90° BECAUSE the plane is a laterally infinite perfect conductor " +
                $"and there is therefore no field below it; a stack that is open or magnetic below " +
                $"radiates into the lower half-space and this analysis does not compute that half. " +
                $"It is refused rather than reported over half a sphere, because a pattern missing " +
                $"the half it should have is a front-to-back ratio nobody can see is a fiction.");

        var kernelOk = problem.RequiresGeneralKernel
            ? LayeredSpectralGreens.CanSolveAt(stack, fHz)
            : SpectralGreens.CanSolveAt(problem.Slab, fHz);
        return kernelOk;
    }

    /// <summary>
    /// R-mom-17 in full: the medium, the grid and the MESH. Every refusal here names a configuration
    /// that is representable in the types and genuinely not computed, and names what it would take.
    /// </summary>
    public static EmSuitability CanCompute(PlanarProblem problem, PlanarMesh mesh, double fHz,
                                           PlanarFarFieldGrid? grid = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        var medium = CanComputeMedium(problem, fHz);
        if (!medium.Ok) return medium;

        var gridOk = (grid ?? PlanarFarFieldGrid.Hemisphere()).CanUse();
        if (!gridOk.Ok) return gridOk;

        foreach (var b in mesh.Bases)
        {
            if (b.Direction == PlanarBasisDirection.Z || b.AttachesToGround)
                return EmSuitability.No(VerticalBasisRefusal);
            if (mesh.Cells[b.CellA].IsCut || mesh.Cells[b.CellB].IsCut)
                return EmSuitability.No(CutCellRefusal);
        }

        int levels = problem.Layers.Count;
        foreach (var b in mesh.Bases)
            if (b.LayerIndex < 0 || b.LayerIndex >= levels)
                return EmSuitability.No(
                    $"A basis names conductor level {b.LayerIndex}, which the problem does not have " +
                    $"({levels} level(s)). The mesh and the problem are not the same structure.");

        return EmSuitability.Yes;
    }

    /// <summary>
    /// <b>The pattern of ONE driven port at ONE frequency.</b> The signature is what enforces that,
    /// exactly as <see cref="PlanarCurrentDensity.Compute"/>'s does and for the same reason.
    /// </summary>
    /// <param name="basisCurrents">One column of <see cref="PlanarPortSolution.Currents"/> — the
    /// basis currents when a single port is driven at 1 V. <b>Read directly, never through
    /// <see cref="PlanarCurrentDensityMap"/></b>: that is a DISPLAY reduction which collapses a
    /// rooftop pair to a per-cell scalar and loses the basis structure the transform needs.</param>
    /// <param name="maxDegreeOfParallelism">The run's own core cap. Each direction writes its own
    /// slot and reads nothing another direction writes, so the answer is bit-identical at any cap.</param>
    public static PlanarFarFieldPattern Compute(
        PlanarProblem problem, PlanarMesh mesh, Vec<Complex> basisCurrents,
        int drivenPortNumber, double fHz, PlanarFarFieldGrid? grid = null,
        int? maxDegreeOfParallelism = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);

        var g  = grid ?? PlanarFarFieldGrid.Hemisphere();
        var ok = CanCompute(problem, mesh, fHz, g);
        if (!ok.Ok) throw new InvalidOperationException(ok.Reason);

        if (basisCurrents.Count != mesh.Bases.Count)
            throw new ArgumentException(
                $"The solution has {basisCurrents.Count} unknowns and the mesh has {mesh.Bases.Count} " +
                $"basis functions — these are not the same solve.", nameof(basisCurrents));

        var factors = FarFieldElementFactors.For(problem, fHz);

        // R-ant-2 — the far field and the fill must agree on which kernel this problem is. They are
        // asked of the SAME predicate, so this can only fail if someone widens one of them.
        if (factors.IsGeneral != problem.RequiresGeneralKernel)
            throw new InvalidOperationException(
                "The far field and the matrix fill disagree about which spectral kernel this problem " +
                "needs. PlanarProblem.RequiresGeneralKernel is the single place that choice is made.");

        int nt = g.ThetaDeg.Count, np = g.PhiDeg.Count, nLevels = problem.Layers.Count;
        var eTheta = new Complex[nt * np];
        var ePhi   = new Complex[nt * np];
        var u      = new double[nt * np];

        double k0   = factors.K0;
        double eta0 = FarFieldElementFactors.Eta0;
        // C = −j k₀ η₀ / 4π — the r-normalised half of §2's C(r), with e^{−jk₀r}/r removed.
        Complex c = -Complex.ImaginaryOne * k0 * eta0 / (4.0 * Math.PI);

        var rows = new List<Action>(nt);
        for (int itOuter = 0; itOuter < nt; itOuter++)
        {
            int it = itOuter;
            rows.Add(() =>
            {
                ct.ThrowIfCancellationRequested();
                double theta = g.ThetaDeg[it] * Math.PI / 180.0;
                double kRho  = k0 * Math.Sin(theta);

                var fTm = new Complex[nLevels];
                var fTe = new Complex[nLevels];
                for (int L = 0; L < nLevels; L++) (fTm[L], fTe[L]) = factors.At(theta, L);

                var jx = new Complex[nLevels];
                var jy = new Complex[nLevels];

                for (int ip = 0; ip < np; ip++)
                {
                    double phi = g.PhiDeg[ip] * Math.PI / 180.0;
                    var (sinPhi, cosPhi) = Math.SinCos(phi);
                    double kx = kRho * cosPhi, ky = kRho * sinPhi;

                    Array.Clear(jx); Array.Clear(jy);
                    for (int b = 0; b < mesh.Bases.Count; b++)
                    {
                        var basis = mesh.Bases[b];
                        Complex t = basisCurrents[b] * RooftopSpectrum.Of(mesh, basis, kx, ky);
                        if (basis.Direction == PlanarBasisDirection.X) jx[basis.LayerIndex] += t;
                        else                                          jy[basis.LayerIndex] += t;
                    }

                    Complex fth = Complex.Zero, fph = Complex.Zero;
                    for (int L = 0; L < nLevels; L++)
                    {
                        fth += fTm[L] * (jx[L] * cosPhi + jy[L] * sinPhi);
                        fph += fTe[L] * (-jx[L] * sinPhi + jy[L] * cosPhi);
                    }
                    fth *= c;
                    fph *= c;

                    int idx = g.IndexOf(it, ip);
                    eTheta[idx] = fth;
                    ePhi[idx]   = fph;
                    u[idx]      = (fth.Magnitude * fth.Magnitude + fph.Magnitude * fph.Magnitude)
                                  / (2.0 * eta0);
                }
            });
        }

        PlanarFanOut.Run(maxDegreeOfParallelism, rows);

        return new PlanarFarFieldPattern(g, eTheta, ePhi, u, drivenPortNumber, fHz);
    }

    /// <summary>
    /// <b>§5.4's power balance, reported rather than gated.</b> The power that actually leaves the
    /// driven port for a 1 V delta gap is <c>½·Re(Y_jj)</c> — the RAW admittance of the structure as
    /// meshed, which is what the currents belong to; the de-embedded matrix belongs to a different
    /// structure with the feed leads removed.
    ///
    /// <para><b>The copper term is identically zero in this kernel</b> — the metal is a perfect
    /// conductor and <c>SigmaSm</c> is carried through the whole pipeline and never read by the fill
    /// — so the balance closes OPTIMISTICALLY, and the shortfall reported here is dielectric loss
    /// plus surface-wave power together. Itemising those two is the metrics phase's, and it is named
    /// rather than approximated.</para>
    /// </summary>
    public static string PowerBalanceNote(PlanarFarFieldPattern pattern, Complex rawSelfAdmittance)
    {
        double accepted = 0.5 * rawSelfAdmittance.Real;
        double radiated = pattern.RadiatedPowerW;
        double shortfall = accepted - radiated;
        string pct = accepted > 0 ? $" ({100.0 * radiated / accepted:F1} % of it)" : "";
        return
            $"Power balance at {SurfaceMesher.Eng(pattern.FrequencyHz)}Hz, port {pattern.DrivenPort} " +
            $"driven at 1 V: {SurfaceMesher.Eng(accepted)}W accepted, " +
            $"{SurfaceMesher.Eng(radiated)}W radiated into the upper hemisphere{pct}, " +
            $"{SurfaceMesher.Eng(shortfall)}W unaccounted. The unaccounted term is DIELECTRIC LOSS " +
            $"plus SURFACE-WAVE power, which this phase does not itemise. Conductor loss is " +
            $"identically zero here — the metal is a perfect conductor — so the balance closes " +
            $"optimistically and a radiation efficiency read off it would read high.";
    }
}
