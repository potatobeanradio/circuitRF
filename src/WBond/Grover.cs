namespace CircuitRF.WBond;

/// <summary>
/// A straight current filament: the atom of the inductance calculation.
///
/// <para>Every field is a per-filament <b>invariant</b>, computed once when the design is meshed and
/// never recomputed per pair. There are ~3,600 filaments in the worst stated case and ~13 M ordered
/// pairs, so anything derived from a single filament that is computed inside the pair loop is
/// computed ~3,600× too often (brief-wbond-wba §3).</para>
/// </summary>
/// <param name="Ax">Start point x, metres. Current enters here.</param>
/// <param name="Uz">Unit direction z. Current flows along (Ux, Uy, Uz).</param>
/// <param name="Length">Filament length, metres.</param>
/// <param name="Radius">Conductor radius, metres — the GMD floor of <see cref="Grover"/>.</param>
public readonly record struct Filament(
    double Ax, double Ay, double Az,
    double Ux, double Uy, double Uz,
    double Length,
    double Radius)
{
    public static Filament FromEndpoints(
        double ax, double ay, double az,
        double bx, double by, double bz,
        double radius)
    {
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len <= 0.0)
            throw new ArgumentException("A filament of zero length has no direction.", nameof(bx));
        double inv = 1.0 / len;
        return new Filament(ax, ay, az, dx * inv, dy * inv, dz * inv, len, radius);
    }

    /// <summary>
    /// This filament's image in the ground plane at z = 0 (WB7 / D3).
    ///
    /// <para><b>Mirror through z = 0 <i>and</i> reverse traversal.</b> That single rule produces the
    /// correct sign for horizontal and vertical current alike, and it is the only construction that
    /// does:</para>
    /// <list type="bullet">
    /// <item>a <b>horizontal</b> filament mirrors to a horizontal one and reverses, so it runs
    ///   <b>anti-parallel</b> — the classic wire-over-ground image;</item>
    /// <item>a <b>vertical</b> filament mirrors to a downward one, and reversing it points it back
    ///   up, so it runs <b>parallel</b> — the case that is wrong in every implementation that
    ///   special-cases only the horizontal one.</item>
    /// </list>
    ///
    /// <para>Because the reversal is baked into the returned filament's direction, the image's
    /// contribution is <b>added</b>, not subtracted: <c>L_ij = M(i, j) + M(i, Image(j))</c>. The
    /// minus sign of the textbook formula is carried by the geometry instead of by a special case,
    /// which is what makes the sign structural rather than remembered.</para>
    /// </summary>
    public Filament Image()
    {
        // End point of the mirrored filament becomes the start of the reversed one.
        double ex = Ax + Ux * Length;
        double ey = Ay + Uy * Length;
        double ez = -(Az + Uz * Length);
        return new Filament(ex, ey, ez, -Ux, -Uy, Uz, Length, Radius);
    }
}

/// <summary>
/// Grover's closed-form mutual inductance between two straight filaments
/// (<i>Inductance Calculations</i>, 2nd ed.), per wbond.md §3.1.
///
/// <para><b>There are two formulae and both are needed.</b> Chapter 19's general skew form handles
/// filaments in any relative position; Chapter 17's parallel form handles the degenerate case.
/// The crossover exists for <i>speed and exactness at ε ≡ 0</i>, not as numerical rescue — see
/// <see cref="ParallelEpsilon"/>.</para>
/// </summary>
public static class Grover
{
    /// <summary>μ₀/4π, H/m. CODATA 2022 μ₀ = 1.25663706127e-6.</summary>
    public const double Mu0Over4Pi = 1.25663706127e-6 / (4.0 * Math.PI);

    /// <summary>
    /// The |sin ε| below which the parallel formula is used instead of the skew one.
    ///
    /// <para><b>Measured, not guessed (brief-wbond-wba §0.3 item 3).</b> The skew formula does not
    /// blow up as sin ε → 0 — the <c>Ω·d/sin ε</c> term is a genuine 0/0 whose limit is finite and
    /// <c>Atan2</c> handles it gracefully. It agrees with the parallel closed form to <b>9 digits at
    /// ε = 1e-6</b> and only loses ~3 digits by 1e-8. So the parallel form is here because it is
    /// 32 % cheaper and exact at ε ≡ 0, not because the skew form fails.</para>
    ///
    /// <para><b>Do not raise this to a "safe" 1e-3.</b> That silently eats a ~3e-6 relative error on
    /// every nominally-parallel pair — which, in a real bond-wire array, is most of them.</para>
    /// </summary>
    public const double ParallelEpsilon = 1e-6;

    /// <summary>
    /// Mutual inductance between two filaments, in henries, with the current direction convention
    /// carried by each filament's own direction vector. Reversing either filament negates the result.
    ///
    /// <para>Dispatches to <see cref="Parallel"/> or <see cref="Skew"/>; applies the GMD floor of
    /// <see cref="MinimumSeparation"/> in both.</para>
    /// </summary>
    public static double Mutual(in Filament p, in Filament q)
    {
        double cosEps = p.Ux * q.Ux + p.Uy * q.Uy + p.Uz * q.Uz;

        // Guard the dot product against drifting outside [-1, 1] through rounding, which would make
        // sin^2 negative and the square root NaN.
        if (cosEps > 1.0) cosEps = 1.0;
        else if (cosEps < -1.0) cosEps = -1.0;

        double sinSq = 1.0 - cosEps * cosEps;
        if (sinSq <= ParallelEpsilon * ParallelEpsilon)
            return Parallel(p, q, cosEps);

        return Skew(p, q, cosEps, Math.Sqrt(sinSq));
    }

    /// <summary>
    /// The floor on the axis-to-axis separation of two filaments: the geometric mean of their radii.
    ///
    /// <para><b>This is physics, not a numerical guard (WB6 / D5).</b> Consecutive filaments of the
    /// same wire share an endpoint, so their axes intersect and d = 0 — where the skew formula
    /// returns NaN. The physically correct separation is not zero: they are the same conductor of
    /// radius <i>a</i>, so it is the cross-section's GMD. Measured, the skew formula is stable down
    /// to d = 1e-14 m and NaNs only at exactly zero, so this clamp is never load-bearing
    /// numerically — implementing it as a small epsilon instead would return a finite, plausible,
    /// <b>wrong</b> answer.</para>
    ///
    /// <para>√(a_p·a_q) is the natural generalisation to two wires of different diameter, and it
    /// reduces to <i>a</i> exactly for the self case.</para>
    /// </summary>
    public static double MinimumSeparation(in Filament p, in Filament q) =>
        Math.Sqrt(p.Radius * q.Radius);

    /// <summary>
    /// Grover Ch. 17 — two parallel filaments.
    ///
    /// <para>With the filaments occupying axial intervals [0, l] and [s, s+m] at lateral separation
    /// <i>d</i>:</para>
    /// <code>
    /// M = (μ₀/4π)·[ f(s+m) − f(s) − f(s+m−l) + f(s−l) ],   f(z) = z·asinh(z/d) − √(z²+d²)
    /// </code>
    /// <para><b>This exact combination of the four end-pair terms is the one that has been verified</b>
    /// — against Grover's closed form for equal overlapping filaments, and against the skew formula's
    /// ε → 0 limit. A plausible-looking different sign pattern passes a self-consistency check while
    /// failing both oracles.</para>
    /// </summary>
    /// <param name="cosEps">±1. Negative means the filaments are antiparallel.</param>
    public static double Parallel(in Filament p, in Filament q, double cosEps)
    {
        // Work in p's frame: axial coordinate along p's direction, lateral distance perpendicular.
        double wx = q.Ax - p.Ax, wy = q.Ay - p.Ay, wz = q.Az - p.Az;
        double axial = wx * p.Ux + wy * p.Uy + wz * p.Uz;

        double px = wx - axial * p.Ux;
        double py = wy - axial * p.Uy;
        double pz = wz - axial * p.Uz;
        double d = Math.Sqrt(px * px + py * py + pz * pz);

        double dMin = MinimumSeparation(p, q);
        if (d < dMin) d = dMin;

        double l = p.Length;
        double m = q.Length;

        // q traverses +p when cosEps > 0 and −p when cosEps < 0. In the antiparallel case q occupies
        // [axial − m, axial] and the mutual changes sign with the traversal.
        double s = cosEps >= 0.0 ? axial : axial - m;
        double sign = cosEps >= 0.0 ? 1.0 : -1.0;

        double value = FourTerm(s, l, m, d);
        return sign * Mu0Over4Pi * value;
    }

    /// <summary>
    /// The <b>scalar</b> double integral <c>∫∫ ds ds′ / R</c> between two parallel filaments, in
    /// metres — the electrostatic kernel of <see cref="PotentialCoefficients"/> (wbond.md §3.7).
    ///
    /// <para><b>It is the same integral <see cref="Parallel"/> already evaluates, and that is why it
    /// lives here rather than as a second copy.</b> The Neumann integral is
    /// <c>M = (μ₀/4π)∮∮(dl₁·dl₂)/R</c> with <c>dl₁·dl₂ = cos ε·dt·ds</c>, so for parallel filaments
    /// Grover's four end-pair terms ARE this integral, times <c>cos ε</c> and <c>μ₀/4π</c>. Strip
    /// those two factors and what is left is exactly the coefficient-of-potential kernel.</para>
    ///
    /// <para><b>The sign does NOT come off with them.</b> <see cref="Parallel"/> negates for
    /// antiparallel filaments because a current has a direction; a <i>charge</i> has none, so this
    /// returns the strictly positive integral whichever way the two filaments are traversed. Passing
    /// a ground-plane <see cref="Filament.Image"/> — which reverses traversal by construction —
    /// therefore returns the same value it would for the un-reversed mirror segment, which is what
    /// the image term needs. The image's own minus sign belongs to the CHARGE and is applied by
    /// <see cref="PotentialCoefficients"/>, not here.</para>
    ///
    /// <para>Exact for the self case (<c>p == q</c>), where the <see cref="MinimumSeparation"/> GMD
    /// floor supplies the lateral separation.</para>
    /// </summary>
    public static double ParallelScalarKernel(in Filament p, in Filament q)
    {
        double cosEps = p.Ux * q.Ux + p.Uy * q.Uy + p.Uz * q.Uz;

        // Work in p's frame, exactly as Parallel does.
        double wx = q.Ax - p.Ax, wy = q.Ay - p.Ay, wz = q.Az - p.Az;
        double axial = wx * p.Ux + wy * p.Uy + wz * p.Uz;

        double px = wx - axial * p.Ux;
        double py = wy - axial * p.Uy;
        double pz = wz - axial * p.Uz;
        double d = Math.Sqrt(px * px + py * py + pz * pz);

        double dMin = MinimumSeparation(p, q);
        if (d < dMin) d = dMin;

        double l = p.Length;
        double m = q.Length;

        // q occupies [axial, axial+m] when it traverses +p and [axial−m, axial] when it traverses −p.
        // The interval is what matters; the traversal is not.
        double s = cosEps >= 0.0 ? axial : axial - m;

        return FourTerm(s, l, m, d);
    }

    /// <summary>
    /// Grover's four end-pair terms, shared by <see cref="Parallel"/> and
    /// <see cref="ParallelScalarKernel"/> so the combination exists once.
    ///
    /// <para><b>This exact sign pattern is the one that has been verified</b> — a plausible-looking
    /// different one passes a self-consistency check while failing both oracles. See
    /// <see cref="Parallel"/>.</para>
    /// </summary>
    private static double FourTerm(double s, double l, double m, double d) =>
        F(s + m, d) - F(s, d) - F(s + m - l, d) + F(s - l, d);

    /// <summary>
    /// f(z) = z·asinh(z/d) − √(z²+d²). Even in z, and finite at z = 0 where f(0) = −d.
    ///
    /// <para><b>Public because the electrostatic kernel is the same integral</b> (§3.7): with
    /// <c>cos ε</c> and <c>μ₀/4π</c> stripped, Grover's parallel form IS
    /// <c>∫∫ ds ds′/R</c>. Exposed rather than re-derived so there is one antiderivative in the
    /// codebase, not two that can drift.</para>
    /// </summary>
    public static double F(double z, double d) => z * Math.Asinh(z / d) - Math.Sqrt(z * z + d * d);

    /// <summary>
    /// Grover Ch. 19 (after Campbell) — filaments in any relative position.
    ///
    /// <para>Let <i>d</i> be the shortest distance between the two axes, ε the angle between them,
    /// and μ, ν the signed distances from the common-perpendicular feet to each filament's start:</para>
    /// <code>
    /// M = (μ₀/4π)·2·cos ε·[ T − Ω·d/(2·sin ε) ]
    /// </code>
    /// <para>with T the four inverse-hyperbolic terms and Ω the four arctangent terms in the source.
    /// R₁…R₄ are the four end-to-end distances.</para>
    ///
    /// <para><b>The Ω term is INSIDE the 2·cos ε factor, and that placement is load-bearing.</b>
    /// The Neumann integral is M = (μ₀/4π)·∮∮(dl₁·dl₂)/R and dl₁·dl₂ = cos ε·dt·ds, so <b>M is
    /// exactly cos ε times a strictly positive double integral</b> — it must vanish identically for
    /// perpendicular filaments. Writing the Ω term outside the factor, as
    /// <c>(μ₀/4π)(2cos ε·T − Ω·d/sin ε)</c>, breaks that: it is wrong by 8 % at ε = 30°, 31 % at
    /// 55°, and returns a large non-zero value at ε = 90° where the true answer is zero.</para>
    ///
    /// <para><b>Both forms agree to 9 digits as ε → 0</b>, because cos ε → 1 there — so the
    /// skew→parallel convergence check (tier 1) passes either way and cannot separate them. This
    /// was caught by the perpendicular-crossing oracle and confirmed against direct numerical
    /// integration of the Neumann double integral, which is why both of those tests exist and why
    /// neither may be deleted as redundant.</para>
    /// </summary>
    public static double Skew(in Filament p, in Filament q, double cosEps, double sinEps)
    {
        double l = p.Length;
        double m = q.Length;

        // Common perpendicular. With w = A_p − A_q, the closest-approach parameters t (on p) and
        // s (on q) solve the 2x2 normal equations; the determinant is −sin²ε, which is why this
        // construction is exactly what the parallel case cannot use.
        double wx = p.Ax - q.Ax, wy = p.Ay - q.Ay, wz = p.Az - q.Az;
        double a = wx * p.Ux + wy * p.Uy + wz * p.Uz;
        double b = wx * q.Ux + wy * q.Uy + wz * q.Uz;
        double sinSq = sinEps * sinEps;

        double sPar = (b - a * cosEps) / sinSq;
        double tPar = sPar * cosEps - a;

        // Foot-to-start offsets. Grover measures each filament from its own perpendicular foot.
        double mu = -tPar;
        double nu = -sPar;

        // Shortest axis-to-axis distance, from the closest-approach points themselves.
        double cx = (p.Ax + tPar * p.Ux) - (q.Ax + sPar * q.Ux);
        double cy = (p.Ay + tPar * p.Uy) - (q.Ay + sPar * q.Uy);
        double cz = (p.Az + tPar * p.Uz) - (q.Az + sPar * q.Uz);
        double d = Math.Sqrt(cx * cx + cy * cy + cz * cz);

        double dMin = MinimumSeparation(p, q);
        if (d < dMin) d = dMin;

        double muL = mu + l;
        double nuM = nu + m;
        double dSq = d * d;

        double r1 = Math.Sqrt(dSq + muL * muL + nuM * nuM - 2.0 * muL * nuM * cosEps);
        double r2 = Math.Sqrt(dSq + muL * muL + nu * nu - 2.0 * muL * nu * cosEps);
        double r3 = Math.Sqrt(dSq + mu * mu + nu * nu - 2.0 * mu * nu * cosEps);
        double r4 = Math.Sqrt(dSq + mu * mu + nuM * nuM - 2.0 * mu * nuM * cosEps);

        double t = muL * Math.Atanh(m / (r1 + r2))
                 + nuM * Math.Atanh(l / (r1 + r4))
                 - mu * Math.Atanh(m / (r3 + r4))
                 - nu * Math.Atanh(l / (r2 + r3));

        double dSinEps = d * sinEps;
        double omega = Math.Atan2(dSq * cosEps + muL * nuM * sinSq, dSinEps * r1)
                     - Math.Atan2(dSq * cosEps + muL * nu * sinSq, dSinEps * r2)
                     + Math.Atan2(dSq * cosEps + mu * nu * sinSq, dSinEps * r3)
                     - Math.Atan2(dSq * cosEps + mu * nuM * sinSq, dSinEps * r4);

        return Mu0Over4Pi * 2.0 * cosEps * (t - omega * d / (2.0 * sinEps));
    }

    /// <summary>
    /// Self partial inductance of a straight filament: the parallel mutual evaluated against itself
    /// at d = GMD (D4 / WB8).
    ///
    /// <para>GMD = <i>a</i> gives the <b>external</b> inductance only; the internal contribution
    /// L_int(f) comes from the same Bessel evaluation that produces R(f), so the whole frequency
    /// dependence lives in one place. Do not make the GMD frequency-dependent to fake the
    /// transition — it is right at both ends, wrong in between, and double-counts against the
    /// Bessel term.</para>
    /// </summary>
    public static double SelfExternal(in Filament p) => Parallel(p, p, 1.0);
}
