// L8c — an INDEPENDENT evaluator for one Galerkin cell-pair integral.
//
// D3's rule in this area, three times over now (kernel A's meshed ground plate, L7b-b's closed-form
// 2x2 eigen-decomposition, L8a's direct Sommerfeld contour): a second formulation that shares no
// approximation with the first. RectangleIntegrals validated against RectangleIntegrals proves
// nothing.
//
// THE SECOND FORMULATION: CROSS-CORRELATION, NOT A CORNER RULE.
//
//   ∫_a ∫_b w_a(r)·w_b(r′)·f(|r − r′|) dS′ dS  =  ∫∫ C_x(u)·C_y(v)·f(√(u²+v²)) du dv
//
// with u = x − x′, v = y − y′ and C the cross-correlation of the two weight PROFILES along that
// axis. It is exact and elementary: substitute r′ = r − (u,v) and do the r integral first. Two things
// make it a genuinely independent path rather than a rearrangement of the same algebra:
//
//   • it collapses a 4-D integral to a 2-D one, so a plain graded quadrature reaches 1e-12 where a
//     brute-force 4-D rule would not get past 1e-4 on a self term;
//   • it never evaluates an antiderivative, a corner sum or a closed form of any kind — the only
//     thing it needs about the geometry is "how much do these two intervals overlap when shifted",
//     which is arithmetic.
//
// A rooftop's weight is separable — ξ/Area varies along the flow direction and is constant across it
// — so C_x and C_y are each a 1-D correlation of two piecewise-LINEAR profiles. Both are integrated
// exactly by a 4-point Gauss rule on the overlap, since the product of two linears is a quadratic and
// the ramp has no kink inside a cell (its zero is always AT a cell edge, by construction).
//
// HAND CHECK, and it is the one worth knowing: for the unit square against itself with pulse weights
// the correlation is the classic triangle product (1−|u|)(1−|v|), so
//     ∫∫ dS dS′/R  =  4∫₀¹∫₀¹ (1−u)(1−v)/√(u²+v²) du dv  =  2.9732096…
// which is the mean RECIPROCAL distance between two random points of a unit square — a number that
// can be obtained from a one-line quadrature with nothing in this repository involved.
// PlanarFillTests asserts it against the production self core.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public static class PlanarPairOracle
{
    /// <summary>A weight profile along one axis: <c>Scale·(Ramp ? |t − Edge| : 1)</c> on [Lo, Hi].</summary>
    public readonly record struct Profile(double Lo, double Hi, double Edge, bool Ramp, double Scale)
    {
        public double Value(double t) => Scale * (Ramp ? Math.Abs(t - Edge) : 1.0);
    }

    /// <summary>
    /// <c>C(u) = ∫ p_a(x)·p_b(x − u) dx</c>. Exact: the integrand is a quadratic on the overlap.
    /// </summary>
    public static double Correlate(Profile a, Profile b, double u)
    {
        double lo = Math.Max(a.Lo, b.Lo + u);
        double hi = Math.Min(a.Hi, b.Hi + u);
        if (!(hi > lo)) return 0.0;

        var (x, w) = Quadrature.Nodes(4);
        double half = 0.5 * (hi - lo), mid = 0.5 * (lo + hi), s = 0;
        for (int i = 0; i < 4; i++)
        {
            double t = mid + half * x[i];
            s += w[i] * a.Value(t) * b.Value(t - u);
        }
        return s * half;
    }

    /// <summary>
    /// <c>∫_a ∫_b w_a w_b f(ρ) dS′ dS</c> for two mesh cells, with each cell's weight either the
    /// divergence PULSE (1/Area) or the rooftop's RAMP (|coord − outerEdge|/Area).
    /// </summary>
    public static Complex Pair(PlanarCell a, PlanarCell b,
                               bool rampA, double edgeA, bool rampB, double edgeB,
                               bool alongX, Func<double, Complex> f,
                               int levels = 12, int nodes = 14)
    {
        // Split each cell's weight into its x and y profiles. The 1/Area factors go on the x ones.
        var ax = new Profile(a.XMin, a.XMax, edgeA, rampA && alongX, 1.0 / a.Area);
        var ay = new Profile(a.YMin, a.YMax, edgeA, rampA && !alongX, 1.0);
        var bx = new Profile(b.XMin, b.XMax, edgeB, rampB && alongX, 1.0 / b.Area);
        var by = new Profile(b.YMin, b.YMax, edgeB, rampB && !alongX, 1.0);

        // The (u, v) domain, split at every breakpoint of the piecewise-polynomial correlations AND
        // at the origin — the correlations have kinks there and the kernel is singular there.
        double[] us = Breakpoints(a.XMin, a.XMax, b.XMin, b.XMax);
        double[] vs = Breakpoints(a.YMin, a.YMax, b.YMin, b.YMax);

        Complex total = Complex.Zero;
        for (int i = 0; i + 1 < us.Length; i++)
            for (int j = 0; j + 1 < vs.Length; j++)
                total += Wedge(u => Correlate(ax, bx, u), v => Correlate(ay, by, v), f,
                               us[i], us[i + 1], vs[j], vs[j + 1], levels, nodes);

        return total;
    }

    /// <summary>The one entry of the Galerkin matrix, from the same formulation — MPIE, Galerkin,
    /// rooftops — but with every integral done by the correlation path above.</summary>
    public static Complex Entry(PlanarMesh mesh, int m, int n,
                                Func<double, Complex> gA, Func<double, Complex> gQ, double omega,
                                int levels = 12, int nodes = 14)
    {
        var bm = mesh.Bases[m];
        var bn = mesh.Bases[n];
        var (ma, mb) = PlanarBasisFunctions.Halves(mesh, bm);
        var (na, nb) = PlanarBasisFunctions.Halves(mesh, bn);

        Span<RooftopHalf> mh = [ma, mb];
        Span<RooftopHalf> nh = [na, nb];

        // scalar: the divergence pulses, signed
        Complex scalar = Complex.Zero;
        foreach (var hm in mh)
            foreach (var hn in nh)
                scalar += hm.Sign * hn.Sign
                        * Pair(mesh.Cells[hm.CellIndex], mesh.Cells[hn.CellIndex],
                               false, 0, false, 0, true, gQ, levels, nodes);

        // vector: zero unless the two rooftops point the same way (D5)
        Complex vector = Complex.Zero;
        if (bm.Direction == bn.Direction)
        {
            bool alongX = bm.Direction == PlanarBasisDirection.X;
            foreach (var hm in mh)
                foreach (var hn in nh)
                    vector += Pair(mesh.Cells[hm.CellIndex], mesh.Cells[hn.CellIndex],
                                   true, hm.OuterEdge, true, hn.OuterEdge, alongX, gA, levels, nodes);
        }

        return Complex.ImaginaryOne * omega * EmConstants.Mu0 * vector
             + scalar / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
    }

    // ── the graded 2-D quadrature, singular corner at the origin ──────────────────────────────

    private static double[] Breakpoints(double aLo, double aHi, double bLo, double bHi)
    {
        var set = new SortedSet<double> { aLo - bHi, aLo - bLo, aHi - bHi, aHi - bLo };
        double lo = aLo - bHi, hi = aHi - bLo;
        if (lo < 0 && hi > 0) set.Add(0.0);
        return [.. set];
    }

    private static Complex Wedge(Func<double, double> cx, Func<double, double> cy,
                                 Func<double, Complex> kernel,
                                 double x1, double x2, double y1, double y2, int levels, int nodes)
    {
        if (!(x2 > x1) || !(y2 > y1)) return Complex.Zero;

        double sx = x2 <= 0 ? -1 : 1, sy = y2 <= 0 ? -1 : 1;
        double a0 = Math.Min(Math.Abs(x1), Math.Abs(x2)), a1 = Math.Max(Math.Abs(x1), Math.Abs(x2));
        double b0 = Math.Min(Math.Abs(y1), Math.Abs(y2)), b1 = Math.Max(Math.Abs(y1), Math.Abs(y2));

        var us = Graded(a0, a1, levels);
        var vs = Graded(b0, b1, levels);

        Complex total = Complex.Zero;
        for (int i = 0; i + 1 < us.Length; i++)
            for (int j = 0; j + 1 < vs.Length; j++)
                total += Panel(u => cx(sx * u), v => cy(sy * v), kernel,
                               us[i], us[i + 1], vs[j], vs[j + 1], nodes);
        return total;
    }

    /// <summary>Panel edges graded by decades toward a singular endpoint at zero.</summary>
    private static double[] Graded(double lo, double hi, int levels)
    {
        if (lo > 0 || !(hi > 0)) return [lo, hi];
        var e = new double[levels + 1];
        for (int k = 0; k <= levels; k++) e[k] = hi * Math.Pow(0.1, levels - k);
        return e;
    }

    /// <summary>
    /// One plain Gauss panel. The two correlations are hoisted to the node LINES rather than
    /// evaluated per node PAIR — 2n calls instead of n², which is what makes a whole-matrix
    /// comparison against this oracle affordable enough to sit in the routine gate.
    /// </summary>
    private static Complex Panel(Func<double, double> cx, Func<double, double> cy,
                                 Func<double, Complex> kernel,
                                 double x1, double x2, double y1, double y2, int n)
    {
        if (!(x2 > x1) || !(y2 > y1)) return Complex.Zero;
        var (nodes, w) = Quadrature.Nodes(n);
        double hx = 0.5 * (x2 - x1), mx = 0.5 * (x1 + x2);
        double hy = 0.5 * (y2 - y1), my = 0.5 * (y1 + y2);

        Span<double> us = stackalloc double[n], vs = stackalloc double[n];
        Span<double> fx = stackalloc double[n], fy = stackalloc double[n];
        for (int i = 0; i < n; i++) { us[i] = mx + hx * nodes[i]; fx[i] = cx(us[i]); }
        for (int j = 0; j < n; j++) { vs[j] = my + hy * nodes[j]; fy[j] = cy(vs[j]); }

        Complex s = Complex.Zero;
        for (int i = 0; i < n; i++)
        {
            if (fx[i] == 0) continue;
            Complex inner = Complex.Zero;
            for (int j = 0; j < n; j++)
            {
                if (fy[j] == 0) continue;
                inner += w[j] * fy[j] * kernel(Math.Sqrt(us[i] * us[i] + vs[j] * vs[j]));
            }
            s += w[i] * fx[i] * inner * hy;
        }
        return s * hx;
    }
}
