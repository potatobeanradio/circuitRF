// L8c — Tier 0: the rooftop basis is a basis.
//
// Everything here is an EQUALITY where the arithmetic permits one, following L8b's precedent in this
// area: charge conservation over a rooftop is not "small", it is exactly +1 − 1, and asserting it to
// a tolerance would let a genuinely non-conserving basis through. R-fil-1's failure mode is the
// expensive one — a monopole on every cell looks like a bad MESH, so the search goes to the wrong
// place.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarBasisTests
{
    // A deliberately IRREGULAR two-by-two patch: the cells have different widths and heights, so a
    // basis that silently assumes a uniform grid (dividing by "the" cell size rather than by each
    // cell's own area) fails here and would pass on a uniform one.
    private static PlanarMesh Patch()
    {
        double[] gx = [0.0, 1.0, 3.5, 4.0];
        double[] gy = [0.0, 2.0, 2.5];

        var cells = new List<PlanarCell>();
        var index = new int[(gy.Length - 1) * (gx.Length - 1)];
        for (int iy = 0; iy < gy.Length - 1; iy++)
            for (int ix = 0; ix < gx.Length - 1; ix++)
            {
                index[iy * (gx.Length - 1) + ix] = cells.Count;
                cells.Add(new PlanarCell(0, ix, iy, gx[ix], gy[iy], gx[ix + 1], gy[iy + 1]));
            }

        var bases = new List<PlanarBasis>();
        int nx = gx.Length - 1, ny = gy.Length - 1;
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                if (ix + 1 < nx) bases.Add(new PlanarBasis(0, index[iy * nx + ix], index[iy * nx + ix + 1], PlanarBasisDirection.X));
                if (iy + 1 < ny) bases.Add(new PlanarBasis(0, index[iy * nx + ix], index[(iy + 1) * nx + ix], PlanarBasisDirection.Y));
            }

        return new PlanarMesh(cells, bases, ["Metal"], gx, gy);
    }

    /// <summary>The real mesher's output, so Tier 0 is asked of production geometry too.</summary>
    private static PlanarMeshReport Hero() =>
        SurfaceMesher.Mesh(new PlanarProblem(
            [new PlanarConductorLayer("Metal",
                [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(20e-3, 0),
                                    new EmPoint(20e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
                5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, 10e9));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T0_1 — the divergence is the expected ±1/Area pulse, and it integrates to EXACTLY zero
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_1_DivergenceIsAPulseOfOnePerCellArea_WithOppositeSigns()
    {
        var mesh = Patch();
        foreach (var basis in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];

            Assert.Equal(+1.0 / ca.Area, PlanarBasisFunctions.Divergence(mesh, basis, ca.CenterX, ca.CenterY));
            Assert.Equal(-1.0 / cb.Area, PlanarBasisFunctions.Divergence(mesh, basis, cb.CenterX, cb.CenterY));
        }
    }

    [Fact]
    public void T0_2_ChargeConservation_IsExactlyZero_NotMerelySmall()
    {
        // ∫∇·f dS over the rooftop's own pair = (+1/A_a)·A_a + (−1/A_b)·A_b. R-fil-1 asks for machine
        // precision; the pulse form makes it an identity, so assert the identity.
        var mesh = Patch();
        foreach (var basis in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            double integral = PlanarBasisFunctions.Divergence(mesh, basis, Mid(mesh, ha).X, Mid(mesh, ha).Y)
                                * mesh.Cells[ha.CellIndex].Area
                            + PlanarBasisFunctions.Divergence(mesh, basis, Mid(mesh, hb).X, Mid(mesh, hb).Y)
                                * mesh.Cells[hb.CellIndex].Area;
            Assert.Equal(0.0, integral);
        }
    }

    [Fact]
    public void T0_2b_ChargeConservation_HoldsOnEveryBasisOfTheRealHeroMesh()
    {
        // 552 basis functions of the §10.7 hero, quadrature-integrated rather than argued: an outer
        // product Gauss rule on each half's own cell, so a basis whose divergence was NOT constant
        // would show up here even though T0_2 assumed it was.
        var mesh = Hero().Mesh;
        double worst = 0;
        foreach (var basis in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            double integral = IntegrateOverCell(mesh.Cells[ha.CellIndex],
                                  (x, y) => PlanarBasisFunctions.Divergence(mesh, basis, x, y))
                            + IntegrateOverCell(mesh.Cells[hb.CellIndex],
                                  (x, y) => PlanarBasisFunctions.Divergence(mesh, basis, x, y));
            worst = Math.Max(worst, Math.Abs(integral));
        }
        Assert.True(worst < 1e-14, $"worst |∫∇·f dS| = {worst:E3} over {mesh.Bases.Count} bases");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T0_3 — continuity across the shared edge, zero on the outer edges
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_3_IsContinuousAcrossTheSharedEdge_AndEqualsOneOverItsLength()
    {
        var mesh = Patch();
        foreach (var basis in mesh.Bases)
        {
            var ca = mesh.Cells[basis.CellA];
            var cb = mesh.Cells[basis.CellB];
            double expected = 1.0 / PlanarBasisFunctions.SharedEdgeLength(mesh, basis);

            // A point ON the shared edge, and points a hair either side of it.
            var (ex, ey) = basis.Direction == PlanarBasisDirection.X
                ? (ca.XMax, 0.5 * (ca.YMin + ca.YMax))
                : (0.5 * (ca.XMin + ca.XMax), ca.YMax);

            double eps = 1e-9 * Math.Min(ca.Width + ca.Height, cb.Width + cb.Height);
            var (dx, dy) = basis.Direction == PlanarBasisDirection.X ? (eps, 0.0) : (0.0, eps);

            Assert.Equal(expected, Component(mesh, basis, ex, ey), 12);
            Assert.Equal(expected, Component(mesh, basis, ex - dx, ey - dy), 8);
            Assert.Equal(expected, Component(mesh, basis, ex + dx, ey + dy), 8);
        }
    }

    [Fact]
    public void T0_4_IsZeroOnThePairsOuterEdges_AndOutsideThePair()
    {
        var mesh = Patch();
        foreach (var basis in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];

            var (ax, ay) = basis.Direction == PlanarBasisDirection.X
                ? (ha.OuterEdge, ca.CenterY) : (ca.CenterX, ha.OuterEdge);
            var (bx, by) = basis.Direction == PlanarBasisDirection.X
                ? (hb.OuterEdge, cb.CenterY) : (cb.CenterX, hb.OuterEdge);

            Assert.Equal(0.0, Component(mesh, basis, ax, ay));
            Assert.Equal(0.0, Component(mesh, basis, bx, by));

            // Well outside the pair: both components zero.
            var (fx, fy) = PlanarBasisFunctions.Evaluate(mesh, basis, 1e6, -1e6);
            Assert.Equal(0.0, fx);
            Assert.Equal(0.0, fy);
        }
    }

    [Fact]
    public void T0_5_TheWeightIsLinearOnEachHalf_RisingFromZeroToOneOverTheEdgeLength()
    {
        // The rooftop's shape, sampled: linear in the flow direction, constant across it.
        var mesh = Patch();
        var basis = mesh.Bases.First(b => b.Direction == PlanarBasisDirection.X);
        var ca = mesh.Cells[basis.CellA];
        double peak = 1.0 / PlanarBasisFunctions.SharedEdgeLength(mesh, basis);

        for (int k = 0; k <= 10; k++)
        {
            double s = k / 10.0;
            double x = ca.XMin + s * ca.Width;
            Assert.Equal(s * peak, Component(mesh, basis, x, ca.CenterY), 12);
            // constant across the flow direction
            Assert.Equal(Component(mesh, basis, x, ca.YMin + 0.1 * ca.Height),
                         Component(mesh, basis, x, ca.YMin + 0.9 * ca.Height), 12);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T0_6 — D5: an X-rooftop and a Y-rooftop are POINTWISE orthogonal
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_6_XAndYRooftopsAreOrthogonalEverywhere_IncludingWhereTheyOverlap()
    {
        var mesh = Patch();
        var xb = mesh.Bases.First(b => b.Direction == PlanarBasisDirection.X);
        var yb = mesh.Bases.First(b => b.Direction == PlanarBasisDirection.Y);

        // Deliberately sample the region where the two supports overlap — that is the only place the
        // dot product could be non-zero, so testing anywhere else would pass for the wrong reason.
        bool overlapped = false;
        var (x0, y0, x1, y1) = (mesh.GridX[0], mesh.GridY[0], mesh.GridX[^1], mesh.GridY[^1]);
        for (int i = 0; i <= 40; i++)
            for (int j = 0; j <= 40; j++)
            {
                double x = x0 + (x1 - x0) * i / 40.0, y = y0 + (y1 - y0) * j / 40.0;
                var (fx, fy) = PlanarBasisFunctions.Evaluate(mesh, xb, x, y);
                var (gx, gy) = PlanarBasisFunctions.Evaluate(mesh, yb, x, y);
                if (fx != 0 && gy != 0) overlapped = true;
                Assert.Equal(0.0, fx * gx + fy * gy);
            }

        Assert.True(overlapped, "the two bases never overlapped — the orthogonality test proved nothing");
    }

    [Fact]
    public void T0_7_HalvesAreOrderedLowerCellFirst_WithTheOuterEdgeOnTheFarSide()
    {
        // D2/R-msh-2: CellA is the left/below cell. The fill's signed assembly depends on it, and the
        // dependence is silent — a swapped pair still produces a symmetric matrix, just the wrong one.
        var mesh = Hero().Mesh;
        foreach (var basis in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, basis);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];

            Assert.Equal(+1.0, ha.Sign);
            Assert.Equal(-1.0, hb.Sign);
            if (basis.Direction == PlanarBasisDirection.X)
            {
                Assert.True(ca.XMax == cb.XMin, "X pair does not share an edge");
                Assert.Equal(ca.XMin, ha.OuterEdge);
                Assert.Equal(cb.XMax, hb.OuterEdge);
            }
            else
            {
                Assert.True(ca.YMax == cb.YMin, "Y pair does not share an edge");
                Assert.Equal(ca.YMin, ha.OuterEdge);
                Assert.Equal(cb.YMax, hb.OuterEdge);
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static double Component(PlanarMesh mesh, PlanarBasis basis, double x, double y)
    {
        var (fx, fy) = PlanarBasisFunctions.Evaluate(mesh, basis, x, y);
        return basis.Direction == PlanarBasisDirection.X ? fx : fy;
    }

    private static (double X, double Y) Mid(PlanarMesh mesh, RooftopHalf h)
    {
        var c = mesh.Cells[h.CellIndex];
        return (c.CenterX, c.CenterY);
    }

    private static double IntegrateOverCell(PlanarCell c, Func<double, double, double> f, int n = 6)
    {
        var (nodes, w) = Quadrature.Nodes(n);
        double hx = 0.5 * c.Width, mx = c.CenterX, hy = 0.5 * c.Height, my = c.CenterY;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            double inner = 0;
            for (int j = 0; j < n; j++) inner += w[j] * f(mx + hx * nodes[i], my + hy * nodes[j]);
            s += w[i] * inner * hy;
        }
        return s * hx;
    }
}
