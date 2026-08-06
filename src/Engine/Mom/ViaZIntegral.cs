// The via's z-integral — removing L9c's midpoint rule.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHAT WAS WRONG, AND WHY A PLAIN QUADRATURE IN z DOES NOT FIX IT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// L9c evaluated a via's Green's function ONCE, at the midpoint of its two feet, and multiplied by
// the length: ∫∫dz dz′ G(ρ,z,z′) → ℓ_i ℓ_j G(ρ, mid_i, mid_j). It bounded that with an ELECTRICAL
// condition (kℓ ≤ 0.05), on the grounds that the error is O((kℓ)²) — which is true of the wave
// factor e^{−jkR} and false of the whole substitution, because the same step freezes 1/R over the
// via's length. L9e measured what that costs: the via's own terminal inductance comes out high by
// ≈ 0.673·(ℓ/w), a purely GEOMETRIC error with no frequency in it at all.
//
// The obvious remedy — a Gauss rule in z, evaluating the kernel at n_z² height pairs — is NOT
// enough, and the reason is worth stating before the code rather than discovering in a curve:
//
//   • At a height pair with Δ = |z − z′| > 0 the kernel is BOUNDED at ρ = 0 and its whole structure
//     lives on the scale Δ. PlanarKernelTerms puts that in the CONSTANT and leaves the ρ-dependence
//     in the smooth remainder — which the fill integrates with a handful of Gauss nodes across a
//     cell and interpolates from a table spaced at a fraction of a cell. When Δ ≪ cell, neither
//     resolves it.
//   • The exact answer knows this: ∫∫dz dz′ 1/√(ρ²+(z−z′)²) is 2ℓ·ln(2ℓ/ρ) − 2ℓ for ρ ≪ ℓ and
//     ℓ²/ρ for ρ ≫ ℓ. A discrete z-rule reproduces neither limit — its ρ → 0 behaviour is a sum of
//     Σ_a w_a² /ρ from the diagonal nodes alone, i.e. it keeps a 1/ρ that the true integral does not
//     have.
//
// So the z-integral is split, and the split is exactly the one the kernel's own decomposition
// already provides:
//
//   ┌─ THE SINGULAR HALF ─ the two extracted ASYMPTOTES. Their coefficients are the k_ρ → ∞ limits
//   │  of the cascade — the source region's own Fresnel coefficients — so they DO NOT DEPEND ON THE
//   │  HEIGHTS AT ALL (measured at exactly 0 drift, ViaPhysicsTests.M1_1), and their depths are
//   │  exactly Δ = |z − z′| and Σ_b = z + z′ − 2z_b. Their STATIC part C/(4πR) is therefore integrated
//   │  over the two prisms in closed form, with no fit anywhere in it — see PrismCore below.
//   │
//   └─ THE BOUNDED HALF ─ everything else: the asymptotes' own wave correction C(e^{−jk_mR}−1)/(4πR),
//      which is O(k) and smooth; the surface-wave poles, whose ρ-dependence H₀⁽²⁾(k_pρ) does not
//      involve the heights at all; and the fitted images, whose depths stay at 1.3 cells or more at
//      every height pair on the fixture L9's phase gate runs on (R-fil-8's ratio, re-measured per
//      z-node). That half takes an ordinary Gauss rule in z, applied to the TERMS rather than to the
//      matrix entry — so the fill's own O(N²) work is completely unchanged and only the O(n_z²) fits
//      are added.
//
// The cost premise L9c declined on ("a fit per z-quadrature node rather than one per pairing, and
// D7's cost projection is written against one per pairing") is measured and false: on L9d's own
// two-level fixture a fit is 89.5 ms, and n_z = 4 adds 15 pairings — 1.58 s per frequency, i.e.
// 1.05% of a 149.9 s de-embedded point. The vertical block is a vanishing fraction of a matrix and
// a treatment 16× dearer there is invisible.

using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The z-integral of a vertical (via) basis pair. See the file header for the split and for why a
/// plain quadrature in z is not the answer on its own.
/// </summary>
internal static class ViaZIntegral
{
    /// <summary>A via's z extent — the segment its uniform vertical current occupies.</summary>
    internal readonly record struct Span(double Lo, double Hi)
    {
        public double Length => Hi - Lo;
        public double Mid    => 0.5 * (Lo + Hi);
    }

    /// <summary>n-point Gauss-Legendre on the span; the weights sum to its LENGTH.</summary>
    public static (double[] Z, double[] W) Nodes(Span s, int n)
    {
        var (gx, gw) = Legendre.Nodes(n);
        double h = 0.5 * s.Length, c = s.Mid;
        var z = new double[n];
        var w = new double[n];
        for (int i = 0; i < n; i++) { z[i] = c + h * gx[i]; w[i] = gw[i] * h; }
        return (z, w);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE SINGULAR HALF — ∫∫dz dz′ ∫∫dS dS′ 1/√(ρ² + t²) over two prisms, in closed form in z
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The mean of <c>1/√(ρ² + t²)</c> over a pair of rectangular PRISMS</b>, where t is the
    /// vertical separation the family travels: <c>z − z′</c> for the DIRECT asymptote and
    /// <c>z + z′ − 2z_b</c> for the down-reflected one.
    ///
    /// <para><b>The z-double-integral is reduced EXACTLY to a one-dimensional one</b>, because the
    /// integrand depends on (z, z′) only through t: <c>∫∫dz dz′ f(t) = ∫ W(t) f(t) dt</c> with W the
    /// trapezoidal density of t, which is piecewise linear with four knots. That reduction is what
    /// makes the |t| kink at t = 0 an ordinary PANEL BOUNDARY rather than a diagonal ridge inside a
    /// tensor rule — and the kink is real: <c>M(τ)</c> has a term −2π·(overlap)·τ from the coincident
    /// point, which is exactly the physics the midpoint rule was throwing away.</para>
    ///
    /// <para>The inner in-plane integral is the CLOSED FORM
    /// <see cref="RectangleIntegrals.InverseAtOffset"/> and only the outer one is a Gauss rule, which
    /// is L8c's own structure (the whole reason a rectangular mesh is affordable) carried one
    /// dimension up. Nothing here is frequency-dependent: this is a geometric core in D6's sense, and
    /// the frequency-dependent asymptote coefficient multiplies it.</para>
    /// </summary>
    /// <returns><c>(1/(A_a A_b ℓ_i ℓ_j)) ∫∫dz dz′ ∫∫dS dS′ /√(ρ²+t²)</c> — normalised the same way
    /// <see cref="PlanarFillCores.ScalarCore"/> is, so it drops straight into the entry beside the
    /// planar cores.</returns>
    public static double PrismCore(PlanarMesh mesh, int cellA, int cellB,
                                   Span si, Span sj, bool sumFamily, double floorZ,
                                   PlanarFillSettings st, int tNodes)
    {
        // The four knots of the trapezoidal density, plus the kink at t = 0.
        double[] knots = sumFamily
            ? [si.Lo + sj.Lo - 2 * floorZ, si.Lo + sj.Hi - 2 * floorZ,
               si.Hi + sj.Lo - 2 * floorZ, si.Hi + sj.Hi - 2 * floorZ]
            : [si.Lo - sj.Hi, si.Lo - sj.Lo, si.Hi - sj.Hi, si.Hi - sj.Lo];
        Array.Sort(knots);

        var edges = new List<double>(6) { knots[0] };
        foreach (double k in knots) if (k > edges[^1]) edges.Add(k);
        if (edges[0] < 0 && 0 < edges[^1] && !edges.Contains(0.0))
        {
            edges.Add(0.0);
            edges.Sort();
        }
        if (edges.Count < 2) return 0.0;                       // a degenerate (zero-length) via

        var (gx, gw) = Legendre.Nodes(tNodes);
        double total = 0;
        for (int p = 0; p + 1 < edges.Count; p++)
        {
            double a = edges[p], b = edges[p + 1];
            double h = 0.5 * (b - a), c = 0.5 * (a + b);
            for (int i = 0; i < tNodes; i++)
            {
                double t = c + h * gx[i];
                double w = Density(t, si, sj, sumFamily, floorZ);
                if (w <= 0) continue;
                total += gw[i] * h * w * MeanAtOffset(mesh, cellA, cellB, Math.Abs(t), st);
            }
        }
        return total / (si.Length * sj.Length);
    }

    /// <summary>The trapezoidal density of t — the length of the z-set that produces it. Piecewise
    /// linear, and its integral is exactly ℓ_i·ℓ_j.</summary>
    private static double Density(double t, Span si, Span sj, bool sumFamily, double floorZ)
    {
        double lo, hi;
        if (sumFamily)
        {
            double s = t + 2 * floorZ;
            lo = Math.Max(si.Lo, s - sj.Hi);
            hi = Math.Min(si.Hi, s - sj.Lo);
        }
        else
        {
            lo = Math.Max(si.Lo, t + sj.Lo);
            hi = Math.Min(si.Hi, t + sj.Hi);
        }
        return Math.Max(0.0, hi - lo);
    }

    /// <summary><c>(1/(A_a A_b)) ∫∫dS dS′ /√(ρ² + τ²)</c> for one cell pair at a fixed vertical
    /// offset — outer Gauss, inner closed form, exactly as <c>PlanarFill.PairCores</c> does it in the
    /// plane.</summary>
    private static double MeanAtOffset(PlanarMesh mesh, int cellA, int cellB, double tau,
                                       PlanarFillSettings st)
    {
        var a = mesh.Cells[cellA];
        var b = mesh.Cells[cellB];
        var (nodes, panels) = PlanarFill.RuleForCells(a, b, st);
        var (gx, gw) = Legendre.Nodes(nodes);
        var e = PlanarFill.PanelEdgesFor(panels);

        double invAa = 1.0 / a.Area, invAb = 1.0 / b.Area;
        double sum = 0;
        for (int qx = 0; qx < panels; qx++)
        for (int qy = 0; qy < panels; qy++)
        {
            double xa = a.XMin + e[qx] * a.Width,  xb = a.XMin + e[qx + 1] * a.Width;
            double ya = a.YMin + e[qy] * a.Height, yb = a.YMin + e[qy + 1] * a.Height;
            double cx = 0.5 * (xa + xb), hx = 0.5 * (xb - xa);
            double cy = 0.5 * (ya + yb), hy = 0.5 * (yb - ya);

            for (int i = 0; i < nodes; i++)
            {
                double x = cx + hx * gx[i];
                for (int j = 0; j < nodes; j++)
                {
                    double y = cy + hy * gx[j];
                    sum += gw[i] * gw[j] * hx * hy * invAa * invAb
                         * RectangleIntegrals.InverseAtOffset(b.XMin - x, b.XMax - x,
                                                              b.YMin - y, b.YMax - y, tau);
                }
            }
        }
        return sum;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE BOUNDED HALF — the z rule, applied to the TERMS rather than to the matrix entry
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The z-averaged kernel terms of a ẑẑ pair, with the two asymptotes' STATIC parts removed.</b>
    ///
    /// <para>Averaging the TERMS rather than the ENTRY is what keeps the z-quadrature free: the entry
    /// is linear in the kernel, so <c>Σ_ab w_a w_b ⟨G_ab⟩ = ⟨Σ_ab w_a w_b G_ab⟩</c> and the fill's
    /// O(N²) cell-pair work happens exactly once, at today's cost. Only the n_z² FITS are added, and
    /// M1 measured those at ~1% of a de-embedded point.</para>
    ///
    /// <para>Each node pair's terms come from the shared fit cache, so a second via pair spanning the
    /// same two levels — the ordinary case, since every via of one drawn layer does — costs nothing
    /// further.</para>
    /// </summary>
    public static PlanarKernelTerms AveragedTerms(PlanarKernelSet set, GreensKernel kernel,
                                                  Span si, Span sj, int nz,
                                                  PlanarExtractionOrder order, double rhoFloor)
    {
        var (zi, wi) = Nodes(si, nz);
        var (zj, wj) = Nodes(sj, nz);
        double norm = 1.0 / (si.Length * sj.Length);

        var parts = new List<(double Weight, PlanarKernelTerms Terms)>(nz * nz);
        for (int a = 0; a < nz; a++)
        for (int b = 0; b < nz; b++)
            parts.Add((wi[a] * wj[b] * norm,
                       set.GetMinusStaticAsymptotes(kernel, zi[a], zj[b])));

        return PlanarKernelTerms.Combine(parts, order, rhoFloor);
    }

    /// <summary>
    /// <b>M2 — the same z-average with the fit replaced by DIRECT integration</b>
    /// (<see cref="PlanarKernelSet.GetDirectMinusStaticAsymptotes"/>).
    ///
    /// <para>Identical in every other respect: the same nodes, the same weights, the same
    /// <see cref="PlanarKernelTerms.Combine"/>, and the SINGULAR half is untouched (it is closed
    /// form in z and was never fitted). Only the bounded half's evaluator changes, which is exactly
    /// the piece M1 measured as the failure.</para>
    ///
    /// <para><b>The cost is n_z² TABLES rather than n_z² fits</b>, and a table costs
    /// <paramref name="samples"/> Sommerfeld points at 40–50 ms each. That is why the sample count
    /// is a measured argument rather than the mesh-derived spacing the fill's own remainder table
    /// uses — see M2's own convergence measurement.</para>
    /// </summary>
    public static PlanarKernelTerms AveragedTermsDirect(
        PlanarKernelSet set, GreensKernel kernel, Span si, Span sj, int nz,
        PlanarExtractionOrder order, double rhoFloor, double rhoMaxM, int samples)
    {
        var (zi, wi) = Nodes(si, nz);
        var (zj, wj) = Nodes(sj, nz);
        double norm = 1.0 / (si.Length * sj.Length);

        var parts = new List<(double Weight, PlanarKernelTerms Terms)>(nz * nz);
        for (int a = 0; a < nz; a++)
        for (int b = 0; b < nz; b++)
            parts.Add((wi[a] * wj[b] * norm,
                       set.GetDirectMinusStaticAsymptotes(kernel, zi[a], zj[b], rhoMaxM, samples)));

        return PlanarKernelTerms.Combine(parts, order, rhoFloor);
    }

    /// <summary>
    /// <b>R-viz-5 — the MIXED block integrates over ONE z only, and its asymptote's z-integral is
    /// also closed form.</b>
    ///
    /// <para>The mixed component's asymptote is a LOGARITHM rather than a 1/ρ (L9c's M5), and what the
    /// fill asks it for is <c>dG/dρ</c>. Its near-depth piece is <c>ρ/(r_p(p+r_p))</c> with
    /// <c>p = Σ_b = z + z_h − 2z_b</c> linear in z, and <c>∫dp ρ/(r_p(p+r_p)) = −ρ/(p+√(p²+ρ²))</c> —
    /// so the whole singular part of the z-average is two endpoint evaluations. Everything else (the
    /// poles, the fitted images) is bounded and takes the plain n_z rule.</para>
    ///
    /// <para>The result is one radial function, so <c>MixedEntry</c> is called exactly as many times
    /// as it is today: the z rule costs n_z fits and one table, not n_z quadratures per entry.</para>
    /// </summary>
    public static Func<double, Complex> AveragedMixedDerivative(
        PlanarKernelSet set, Span sv, double zh, int nz)
    {
        var (z, w) = Nodes(sv, nz);
        double norm = 1.0 / sv.Length;

        var models = new DcimModel[nz];
        for (int i = 0; i < nz; i++)
            models[i] = set.Model(GreensKernel.MixedVectorPotential, z[i], zh);

        // The exact z-average of the LOG asymptote's derivative, from the antiderivative above. The
        // asymptote's coefficient and regulator do not depend on the heights, so they come off one
        // node and the depth range comes off the span.
        var probe = set.Asymptote(GreensKernel.MixedVectorPotential, sv.Mid, zh);
        Complex logCoefficient = probe.IsMixedForm ? probe.ImageCoefficient : Complex.Zero;
        double regulator = logCoefficient == Complex.Zero
            ? 0.0
            : models[0].MixedLogFarDepth - models[0].MixedLogNearDepth;

        // Σ_b at the two ends of the via's own span. It is affine in z with slope 1, so one node
        // determines it; the clamp is a ROUNDING guard only — Σ_b = z + z_h − 2z_b is non-negative by
        // construction (both points are at or above their region's floor), and it is exactly zero when
        // the horizontal basis sits on the via's own lower level, which is what makes the mixed
        // component's asymptote a genuine −ln ρ. Clamping must not change the LENGTH, so pHi is
        // derived from the span rather than clamped independently.
        double pLo = Math.Max(0.0, models[0].MixedLogNearDepth - (z[0] - sv.Lo));
        double pHi = pLo + sv.Length;

        return rho =>
        {
            Complex v = Complex.Zero;
            for (int i = 0; i < nz; i++)
                v += w[i] * norm * MixedRest(models[i], rho);

            if (logCoefficient != Complex.Zero)
            {
                Complex c = logCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne);
                v += c * norm * (Antiderivative(pHi + regulator, rho) - Antiderivative(pLo + regulator, rho)
                               - Antiderivative(pHi, rho) + Antiderivative(pLo, rho));
            }
            return v;
        };

        // −ρ/(p + √(p²+ρ²)) — the p-antiderivative of ρ/(r_p(p+r_p)).
        static double Antiderivative(double p, double rho) => -rho / (p + Math.Sqrt(p * p + rho * rho));
    }

    /// <summary>The mixed component's radial derivative WITHOUT its log asymptote — the part that is
    /// bounded in z and takes the plain rule.</summary>
    private static Complex MixedRest(DcimModel m, double rho)
    {
        Complex v = m.DerivativeAtHeights(rho);
        if (m.MixedLogCoefficient == Complex.Zero) return v;

        double p = m.MixedLogNearDepth, q = m.MixedLogFarDepth;
        double rp = Math.Sqrt(p * p + rho * rho), rq = Math.Sqrt(q * q + rho * rho);
        return v - m.MixedLogCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne)
                 * (rho / (rq * (q + rq)) - rho / (rp * (p + rp)));
    }
}
