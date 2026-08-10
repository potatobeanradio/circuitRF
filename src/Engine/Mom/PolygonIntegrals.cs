// Conformal boundary cells — the closed-form INNER integrals over a general simple POLYGON, for an
// observation point lying in the same plane.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHY THIS FILE EXISTS
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// L8c's whole payoff is that the inner integral of the 4-D Galerkin double integral is CLOSED FORM
// (RectangleIntegrals) rather than numerical — "the classic near-singular difficulty comes from doing
// BOTH integrals numerically, and here only one of them is". A CUT cell is not a rectangle, so a
// conformal mesh either gives that payoff back or generalises it. This file generalises it.
//
// brief-conformal-boundary-cells.md §3 named three routes and asked for the measurement that chooses:
// (a) derive the same six over a TRIANGLE and express a cut cell as rectangle ± triangle,
// (b) a numerical inner integral for cut cells only,
// (c) keep the rectangle's closed form and correct only the WEIGHT.
//
// **This is (a), taken to its natural generality: the same six over an arbitrary simple polygon, of
// which BOTH a rectangle and a triangle are special cases.** The brief's own hint is what makes that
// no harder than the triangle — "the corner primitive generalises: the classic route is to reduce the
// surface integral to a sum over the polygon's EDGES". Once the reduction is per-EDGE there is no
// reason to stop at three of them, and expressing a cut cell as "rectangle minus triangle" (with its
// sign bookkeeping, its degenerate cases where the cut clips a corner versus a side, and its second
// closed-form family) simply disappears: the cut cell IS a polygon and is integrated as one.
//
// (b) was not taken, and (c) was not taken. (c) is refused on the argument the brief itself makes —
// it puts source where there is no metal. (b) is measured against this file rather than shipped; see
// PolygonIntegralTests, which is the Tier-1 ladder RectangleIntegralTests already runs, re-run here.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// DERIVATION — from the polar reduction, not transcribed (the D4 rule L8a and L8c both followed)
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Put the observation point at the origin. For any integrand that is a function of the RADIUS alone,
// polar coordinates separate the region from the integrand:
//
//     ∫∫_S f(ρ) dA = ∫ dθ ∫₀^{ρ(θ)} f(s)·s ds = ∫ g(ρ(θ)) dθ ,   g(ρ) ≡ ∫₀^ρ f(s)·s ds
//
// and a polygon's ρ(θ) is piecewise the distance to ONE edge's line. So the area integral collapses
// to a sum of one-dimensional integrals along the edges — the "sum over the polygon's edges" route.
// It is EXACT for any simple polygon: the signed fan from the observation point covers the interior
// once with sign +1 and everything outside an even number of times with cancelling signs, so it needs
// neither convexity nor the observation point being inside.
//
// ONE EDGE, IN ITS OWN FRAME. For the edge A → B let
//
//     ŝ = (B − A)/|B − A|,   n̂ = (−ŝ_y, ŝ_x)  (the LEFT normal),
//     d = A·n̂ = B·n̂         (signed perpendicular offset of the line from the origin),
//     t = P·ŝ               (arc coordinate along the line, ZERO at the foot of the perpendicular),
//     ρ(t) = √(d² + t²).
//
// A point of the line is P(t) = d·n̂ + t·ŝ. The polar angle sweeps with
//
//     dθ/dt = cross(P, ŝ)/ρ² = −d/ρ²      ⇒      dθ = −d·dt/ρ²
//
// so the edge's whole contribution is
//
//     T = −d ∫_{t_A}^{t_B} g(ρ(t)) / ρ(t)² dt.                                            (★)
//
// SANITY, and it is the check the code asserts first: f ≡ 1 gives g = ρ²/2 and T = −(d/2)(t_B − t_A),
// which is exactly ½·cross(A, B) — the signed area of the triangle (O, A, B). The fan reproduces the
// shoelace formula, so the machinery is right before any singular integrand is put through it.
//
// THE THREE PULSE-WEIGHTED FORMS, from (★):
//
//   (1) f = 1/ρ.   g = ρ.        T = −d·[ asinh(t/|d|) ].
//   (2) f = ln ρ.  g = ρ²(2ln ρ − 1)/4.
//                  T = −d·[ ½(t·ln ρ − t + |d|·atan(t/|d|)) − ¼t ]
//                  using ∫ln ρ dt = t·ln ρ − t + |d|·atan(t/|d|)  (from ∫½ln(d²+t²)dt).
//   (3) f = ρ.     g = ρ³/3.     T = −(d/6)·[ t·ρ + d²·asinh(t/|d|) ]   (∫ρ dt = ½[tρ + d²asinh(t/|d|)]).
//
// THE THREE FIRST MOMENTS. The weight is a CARTESIAN coordinate measured from the observation point,
// u = ρ·cos θ (or v = ρ·sin θ), so the radial integral gains one power of s:
//
//     ∫∫_S u·f(ρ) dA = ∫ cos θ · h(ρ(θ)) dθ ,   h(ρ) ≡ ∫₀^ρ f(s)·s² ds
//
// and on the edge cos θ = P_x/ρ = (d·n̂_x + t·ŝ_x)/ρ, so
//
//     T_u = −d ∫ (d·n̂_x + t·ŝ_x) · h(ρ)/ρ³ dt.                                           (★★)
//
//   (4) f = 1/ρ.   h/ρ³ = 1/(2ρ).            T_u = −(d/2)·[ d·n̂_x·asinh(t/|d|) + ŝ_x·ρ ].
//   (5) f = ln ρ.  h/ρ³ = (ln ρ)/3 − 1/9.
//                  T_u = −d·[ d·n̂_x·(⅓∫ln ρ dt − t/9) + ŝ_x·(⅓∫t·ln ρ dt − t²/18) ],
//                  with ∫t·ln ρ dt = ½[ρ²·ln ρ − ρ²/2].
//   (6) f = ρ.     h/ρ³ = ρ/4.               T_u = −(d/4)·[ d·n̂_x·∫ρ dt + ŝ_x·ρ³/3 ].
//
// The v-moments are the same expressions with n̂_y, ŝ_y — which is why they share one code path and a
// flag rather than being written twice.
//
// d = 0 IS NOT A SPECIAL CASE TO GUARD, IT IS A ZERO. Every T above carries the factor −d, and every
// bracket grows at worst like ln(1/|d|), so d·[…] → 0. The code returns 0 outright for a d that
// underflows, which is the same number without the inf × 0. **This is what makes an observation point
// lying exactly on a cell edge — which on a Manhattan layout is not a rare event — one case rather
// than three**, the same property L8c's corner primitive was built for.
//
// Nothing here knows about frequency, a Green's function, or a mesh. Pure geometry, exactly as
// RectangleIntegrals is, which is why D6's frequency-independent core survives a cut cell.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// The six geometric cores over one region, in the pairing the matrix fill consumes them: the
/// pulse-weighted integral and the first moment, for each of <c>1/ρ</c>, <c>ln ρ</c> and <c>ρ</c>.
/// The moment is along whichever axis the caller asked for.
/// </summary>
public readonly record struct PolygonCores(
    double Inverse, double InverseMoment,
    double Log,     double LogMoment,
    double Radius,  double RadiusMoment);

/// <summary>
/// The same six with <b>both</b> first moments rather than one, which is the form an AFFINE weight
/// needs: <c>∫∫(αu + βv + γ)·f(ρ)</c> is <c>α·MomentU + β·MomentV + γ·Plain</c>. A cut cell's rooftop
/// ramp is affine in BOTH coordinates (its zero line is the oblique rim), which is why one moment is
/// no longer enough — see <see cref="RooftopSupport"/>.
/// </summary>
public readonly record struct PolygonCoresXY(
    double Inverse, double InverseU, double InverseV,
    double Log,     double LogU,     double LogV,
    double Radius,  double RadiusU,  double RadiusV);

/// <summary>
/// The six closed-form integrals of <c>1/ρ</c>, <c>ln ρ</c> and <c>ρ</c> — each with and without a
/// linear weight — over an arbitrary simple polygon, for an observation point <b>in the same
/// plane</b>. See the file header for the derivation.
///
/// <para>Every entry point takes the ring in ABSOLUTE coordinates plus the observation point, rather
/// than a pre-translated ring, because the fill calls these once per outer quadrature node and
/// translating a vertex list per node would allocate in the O(N²) inner statement.</para>
///
/// <para><b>The ring's winding sets the sign.</b> Counter-clockwise gives a positive area; a hole is
/// passed clockwise (or negated by the caller). <see cref="RectangleIntegrals"/> stays the path for a
/// whole rectangle — it is cheaper and it is what every pre-conformal number in this repository was
/// produced by — and <c>PolygonIntegralTests</c> gates the two against each other on rectangles.</para>
/// </summary>
public static class PolygonIntegrals
{
    /// <summary>∫∫ dS′ — the signed area (shoelace), which is (★) with f ≡ 1 and is therefore the
    /// first check that the edge reduction itself is right.</summary>
    public static double Area(IReadOnlyList<EmPoint> ring, double ox, double oy)
    {
        double s = 0;
        for (int i = 0, n = ring.Count, j = n - 1; i < n; j = i++)
        {
            double ax = ring[j].X - ox, ay = ring[j].Y - oy;
            double bx = ring[i].X - ox, by = ring[i].Y - oy;
            s += ax * by - bx * ay;
        }
        return 0.5 * s;
    }

    /// <summary>∫∫ u dS′ (<paramref name="alongX"/>) or ∫∫ v dS′, measured FROM the observation
    /// point. Per triangle of the fan this is the signed area times the centroid, which needs no
    /// integral at all.</summary>
    public static double AreaMoment(IReadOnlyList<EmPoint> ring, double ox, double oy, bool alongX)
    {
        double s = 0;
        for (int i = 0, n = ring.Count, j = n - 1; i < n; j = i++)
        {
            double ax = ring[j].X - ox, ay = ring[j].Y - oy;
            double bx = ring[i].X - ox, by = ring[i].Y - oy;
            double cross = ax * by - bx * ay;
            s += cross * (alongX ? ax + bx : ay + by);
        }
        return s / 6.0;
    }

    /// <summary>∫∫ dS′/ρ.</summary>
    public static double Inverse(IReadOnlyList<EmPoint> ring, double ox, double oy)
        => Cores(ring, ox, oy, alongX: true, wantRadius: false).Inverse;

    /// <summary>∫∫ ln ρ dS′.</summary>
    public static double Log(IReadOnlyList<EmPoint> ring, double ox, double oy)
        => Cores(ring, ox, oy, alongX: true, wantRadius: false).Log;

    /// <summary>∫∫ ρ dS′.</summary>
    public static double Radius(IReadOnlyList<EmPoint> ring, double ox, double oy)
        => Cores(ring, ox, oy, alongX: true, wantRadius: true).Radius;

    /// <summary>∫∫ u dS′/ρ, or ∫∫ v dS′/ρ.</summary>
    public static double InverseMoment(IReadOnlyList<EmPoint> ring, double ox, double oy, bool alongX)
        => Cores(ring, ox, oy, alongX, wantRadius: false).InverseMoment;

    /// <summary>∫∫ u·ln ρ dS′, or the v form.</summary>
    public static double LogMoment(IReadOnlyList<EmPoint> ring, double ox, double oy, bool alongX)
        => Cores(ring, ox, oy, alongX, wantRadius: false).LogMoment;

    /// <summary>∫∫ u·ρ dS′, or the v form.</summary>
    public static double RadiusMoment(IReadOnlyList<EmPoint> ring, double ox, double oy, bool alongX)
        => Cores(ring, ox, oy, alongX, wantRadius: true).RadiusMoment;

    /// <summary>
    /// <b>All six in one edge walk</b> — which is how the fill wants them, because the per-edge
    /// trigonometry (<c>asinh</c>, <c>atan</c>, <c>ln</c>) is shared and is what the call costs.
    /// <paramref name="wantRadius"/> skips the two order-2 cores, exactly as
    /// <c>PlanarFill.PairCores</c> already skips <c>∫∫r</c> below extraction order 2.
    /// </summary>
    public static PolygonCores Cores(IReadOnlyList<EmPoint> ring, double ox, double oy,
                                     bool alongX, bool wantRadius)
    {
        var c = CoresXY(ring, ox, oy, wantRadius);
        return alongX
            ? new PolygonCores(c.Inverse, c.InverseU, c.Log, c.LogU, c.Radius, c.RadiusU)
            : new PolygonCores(c.Inverse, c.InverseV, c.Log, c.LogV, c.Radius, c.RadiusV);
    }

    /// <inheritdoc cref="Cores"/>
    public static PolygonCoresXY CoresXY(IReadOnlyList<EmPoint> ring, double ox, double oy,
                                         bool wantRadius)
    {
        double inv = 0, invU = 0, invV = 0;
        double lg = 0, lgU = 0, lgV = 0;
        double rad = 0, radU = 0, radV = 0;
        int n = ring.Count;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double ax = ring[j].X - ox, ay = ring[j].Y - oy;
            double bx = ring[i].X - ox, by = ring[i].Y - oy;

            double ex = bx - ax, ey = by - ay;
            double len = double.Hypot(ex, ey);
            if (!(len > 0)) continue;                       // a repeated vertex contributes nothing

            double sx = ex / len, sy = ey / len;            // ŝ
            double nx = -sy,      ny = sx;                  // n̂, the LEFT normal
            double d  = ax * nx + ay * ny;                  // the signed perpendicular offset

            // d = 0 is a ZERO, not a special case: the whole contribution carries the factor −d and
            // every bracket below diverges no faster than ln(1/|d|). Returning early is the same
            // number without the inf × 0 — see the file header.
            if (d == 0) continue;

            double tA = ax * sx + ay * sy;
            double tB = bx * sx + by * sy;
            double ad = Math.Abs(d);

            double rA = double.Hypot(d, tA), rB = double.Hypot(d, tB);
            double asA = Math.Asinh(tA / ad), asB = Math.Asinh(tB / ad);
            double lnA = Math.Log(rA),        lnB = Math.Log(rB);

            // rB − rA WITHOUT the subtraction. The two radii agree to many digits whenever the edge
            // is short against its own distance from the observation point — which is the ordinary
            // case for a far cell and the ONLY case for an edge-graded sliver. Measured on a 1 : 1e4
            // rectangle the naive difference loses eight digits and the v-moment came out 1.7e-10
            // relative from RectangleIntegrals' own (independently gated) value; this form closes it.
            // Same identity, one algebraic step: (rB−rA)(rB+rA) = tB² − tA².
            double dr = (tB - tA) * (tB + tA) / (rB + rA);

            // ── (1) ∫dt/ρ = asinh(t/|d|) ─────────────────────────────────────────────────────
            inv += -d * (asB - asA);

            // ── (2) ∫ln ρ dt = t·ln ρ + |d|·(atan(t/|d|) − t/|d|) ───────────────────────────
            //
            // Grouped that way rather than as "… − t + |d|·atan(t/|d|)", because those two terms
            // cancel to nothing whenever |t| ≪ |d| — the case of an edge-graded cell's SHORT side seen
            // from far along its long one. Measured on the 1 : 1e4 sliver the naive grouping put the
            // u-moment 2.4e-9 from RectangleIntegrals; atan(z) − z has its own series and closes it.
            double intLnB = tB * lnB + ad * AtanMinusX(tB / ad);
            double intLnA = tA * lnA + ad * AtanMinusX(tA / ad);
            lg += -d * (0.5 * (intLnB - intLnA) - 0.25 * (tB - tA));

            // ── (4) ∫t/ρ dt = ρ ─────────────────────────────────────────────────────────────
            invU += -0.5 * d * (d * nx * (asB - asA) + sx * dr);
            invV += -0.5 * d * (d * ny * (asB - asA) + sy * dr);

            // ── (5) ∫t·ln ρ dt = ¼·ρ²(ln ρ² − 1) ────────────────────────────────────────────
            //
            // Differenced ALGEBRAICALLY rather than numerically, for the same reason as `dr` above and
            // in the same measured case: on a 1 : 1e4 sliver both endpoints' values are ≈ ¼rA² and the
            // subtraction loses eight digits (measured at 1.0e-9 relative against RectangleIntegrals).
            // With S = rB² − rA² and x = S/rA²,
            //     ρB²lnρB² − ρA²lnρA² − S  =  rA²·(ln(1+x) − x)  +  S·ln ρB²
            // and the first term is evaluated by its own series, since ln(1+x) − x cancels too.
            double s2 = (tB - tA) * (tB + tA);                     // rB² − rA², without the squares
            // rA²·(ln(1+x) − x) with x = S/rA², by whichever of the two exact routes is conditioned:
            // the SERIES when the edge is short against its distance (x → 0), and 2(ln rB − ln rA)
            // when it is not — because there x → −1 and forming 1 + x cancels catastrophically.
            // (Measured: the single branch through 1 + x left the u-moment 2.4e-9 out on the sliver,
            // which is the same fixture and the same order as the two fixes above. This one is not a
            // small-argument guard; it is a LARGE-argument one, and it is the third distinct
            // cancellation this one 1 : 1e4 rectangle has caught.)
            double lp = Math.Abs(s2) <= 0.25 * rA * rA
                ? rA * rA * Log1PMinusXSmall(s2 / (rA * rA))
                : rA * rA * 2.0 * (lnB - lnA) - s2;
            double dTLn = 0.25 * (lp + s2 * 2.0 * lnB);
            double mLnN = (intLnB - intLnA) / 3.0 - (tB - tA) / 9.0;
            double mLnS = dTLn / 3.0 - s2 / 18.0;
            lgU += -d * (d * nx * mLnN + sx * mLnS);
            lgV += -d * (d * ny * mLnN + sy * mLnS);

            if (!wantRadius) continue;

            // ── (3)/(6) ∫ρ dt = ½[tρ + d²·asinh(t/|d|)],  ∫tρ dt = ρ³/3 ─────────────────────
            double intRhoB = 0.5 * (tB * rB + d * d * asB);
            double intRhoA = 0.5 * (tA * rA + d * d * asA);
            double dRho    = intRhoB - intRhoA;
            double dCube   = dr * (rB * rB + rB * rA + rA * rA) / 3.0;
            rad  += -(d / 3.0) * dRho;
            radU += -0.25 * d * (d * nx * dRho + sx * dCube);
            radV += -0.25 * d * (d * ny * dRho + sy * dCube);
        }

        return new PolygonCoresXY(inv, invU, invV, lg, lgU, lgV, rad, radU, radV);
    }

    /// <summary>
    /// <c>ln(1 + x) − x = −x²/2 + x³/3 − …</c>, <b>for |x| ≤ ¼ only</b> — the caller picks this or the
    /// logarithm-difference form, because neither is conditioned over the whole range and the choice
    /// is what the measurement above turned on.
    /// </summary>
    private static double Log1PMinusXSmall(double x)
    {
        double sum = 0, p = x * x;
        for (int k = 2; k <= 24; k++)
        {
            double t = (k % 2 == 0 ? -1.0 : 1.0) * p / k;
            sum += t;
            if (Math.Abs(t) <= 1e-20 * Math.Abs(sum)) break;
            p *= x;
        }
        return sum;
    }

    /// <summary><c>atan(z) − z</c>, which is <c>−z³/3 + z⁵/5 − …</c> and cancels to nothing for small
    /// z if the two terms are computed and subtracted.</summary>
    private static double AtanMinusX(double z)
    {
        if (Math.Abs(z) > 0.25) return Math.Atan(z) - z;

        double sum = 0, p = z * z * z, z2 = z * z;
        for (int k = 1; k <= 24; k++)
        {
            double t = (k % 2 == 1 ? -1.0 : 1.0) * p / (2 * k + 1);
            sum += t;
            if (Math.Abs(t) <= 1e-20 * Math.Abs(sum)) break;
            p *= z2;
        }
        return sum;
    }
}
