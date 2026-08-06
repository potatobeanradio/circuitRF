// L8d Tier 0 — the port operator.
//
// Everything here is EXACT: a port either resolves onto the row of rooftops D2 names or it does not,
// the reference plane either sits on the gridline one cell in from the metal or it does not, and the
// incidence sign either matches the direction current flows into the structure or it does not. The
// only tolerant assertion in the file is the LU-tolerance symmetry of Y, and the comment there says
// why it cannot be an equality.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarPortTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A 6 × 3 rectangular grid of unit-ish cells, the algebra fixture for this tier.</summary>
    private static PlanarMesh Slab6x3()
    {
        var gx = new double[7];
        var gy = new double[4];
        for (int i = 0; i < 7; i++) gx[i] = i * 1e-3;
        for (int i = 0; i < 4; i++) gy[i] = i * 0.5e-3;
        return PlanarFillTests.Grid(gx, gy);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-2 — resolution, and everything it has to report
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_1_APortResolvesToTheOutermostRooftopRow_WithTheFullConductorWidth()
    {
        var mesh = Slab6x3();
        var p    = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0, 0.75e-3),
                                                            PlanarPortSide.MinX, 50.0));

        _out.WriteLine(p.Describe());

        // Three cells across the width, so three rooftops in the row.
        Assert.Equal(3, p.BasisCount);
        Assert.Equal(1.5e-3, p.WidthM, 15);

        // D2: the plane is the SHARED edge of the two outermost cells — one cell in from the metal.
        Assert.Equal(1e-3, p.ReferencePlaneM, 15);
        Assert.Equal(0.0,  p.OuterEdgeM,      15);

        // Every basis is an X rooftop pairing column 0 with column 1.
        foreach (int b in p.BasisIndices)
        {
            var bs = mesh.Bases[b];
            Assert.Equal(PlanarBasisDirection.X, bs.Direction);
            Assert.Equal(0, mesh.Cells[bs.CellA].IX);
            Assert.Equal(1, mesh.Cells[bs.CellB].IX);
        }

        // The transverse lines are the mesh's own, verbatim — D4 copies these into the standard.
        Assert.Equal(4, p.TransverseLines.Count);
        for (int i = 0; i < 4; i++) Assert.Equal(mesh.GridY[i], p.TransverseLines[i], 15);

        // The longitudinal run marches inward and covers the whole conductor.
        Assert.Equal(6, p.LongitudinalRunM.Count);
        foreach (double d in p.LongitudinalRunM) Assert.Equal(1e-3, d, 15);
    }

    [Fact]
    public void T0_2_TheFarEndResolvesToTheMirrorRow_AndTheIncidenceSignFlips()
    {
        var mesh = Slab6x3();
        var lo   = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(0,    0.75e-3), PlanarPortSide.MinX, 50.0));
        var hi   = PlanarPorts.Resolve(mesh, new PlanarPort(2, new EmPoint(6e-3, 0.75e-3), PlanarPortSide.MaxX, 50.0));

        Assert.Equal(+1.0, lo.IncidenceSign);
        Assert.Equal(-1.0, hi.IncidenceSign);

        Assert.Equal(1e-3, lo.ReferencePlaneM, 15);
        Assert.Equal(5e-3, hi.ReferencePlaneM, 15);
        Assert.Equal(6e-3, hi.OuterEdgeM,      15);

        // The two rows are disjoint and the same size.
        Assert.Equal(lo.BasisCount, hi.BasisCount);
        Assert.Empty(lo.BasisIndices.Intersect(hi.BasisIndices));

        foreach (int b in hi.BasisIndices)
        {
            var bs = mesh.Bases[b];
            Assert.Equal(4, mesh.Cells[bs.CellA].IX);
            Assert.Equal(5, mesh.Cells[bs.CellB].IX);
        }
    }

    [Fact]
    public void T0_3_AYDirectedPortResolvesTheSameWay_OnTheOtherAxis()
    {
        var mesh = Slab6x3();
        var p    = PlanarPorts.Resolve(mesh, new PlanarPort(1, new EmPoint(2.5e-3, 0),
                                                            PlanarPortSide.MinY, 50.0));

        Assert.Equal(PlanarBasisDirection.Y, p.Direction);
        Assert.Equal(6,     p.BasisCount);            // the full 6 mm run is the "width" here
        Assert.Equal(6e-3,  p.WidthM,          15);
        Assert.Equal(0.5e-3, p.ReferencePlaneM, 15);

        foreach (int b in p.BasisIndices) Assert.Equal(PlanarBasisDirection.Y, mesh.Bases[b].Direction);
    }

    [Fact]
    public void T0_4_APortThatMissesTheMetalIsRefusedByName_NotSnappedToSomethingNearby()
    {
        // An L-shaped conductor with a hole in the row the port aims at: the point is that a MISS is
        // a refusal, never a quiet slide onto whatever metal happened to be closest.
        var gx = new double[] { 0, 1e-3, 2e-3 };
        var gy = new double[] { 0, 1e-3, 2e-3, 3e-3 };
        var full = PlanarFillTests.Grid(gx, gy);

        // Keep only the bottom row: rows 1 and 2 hold no metal at all.
        var kept = full.Cells.Where(c => c.IY == 0).ToList();
        var mesh = Rebuild(kept, full);

        bool ok = PlanarPorts.TryResolve(mesh, new PlanarPort(7, new EmPoint(0, 2.5e-3),
                                                             PlanarPortSide.MinX, 50.0),
                                         out var res, out string? refusal);
        Assert.False(ok);
        Assert.Null(res);
        Assert.Contains("Port 7", refusal);
        Assert.Contains("does not lie on any conductor", refusal);
        _out.WriteLine(refusal!);
    }

    [Fact]
    public void T0_5_AConductorOneCellLongInTheCurrentDirectionIsRefused_WithWhatToChange()
    {
        var gx = new double[] { 0, 1e-3 };
        var gy = new double[] { 0, 1e-3, 2e-3 };
        var mesh = PlanarFillTests.Grid(gx, gy);

        bool ok = PlanarPorts.TryResolve(mesh, new PlanarPort(1, new EmPoint(0, 0.5e-3),
                                                             PlanarPortSide.MinX, 50.0),
                                         out _, out string? refusal);
        Assert.False(ok);
        Assert.Contains("only one cell long", refusal);
        Assert.Contains("Lengthen the feed", refusal);      // R-mom-17: name what to change
        _out.WriteLine(refusal!);
    }

    [Theory]
    [InlineData(PlanarPortReference.CoplanarGround,  "coplanar ground reference")]
    [InlineData(PlanarPortReference.SecondConductor, "second-conductor")]
    public void T0_6_ANonGroundPlaneReferenceIsRefusedByName_PointingAtWhereItArrives(
        PlanarPortReference reference, string fragment)
    {
        var mesh = Slab6x3();
        bool ok = PlanarPorts.TryResolve(
            mesh, new PlanarPort(3, new EmPoint(0, 0.75e-3), PlanarPortSide.MinX, 50.0, 0, reference),
            out _, out string? refusal);

        Assert.False(ok);
        Assert.Contains(fragment, refusal);
        // L9d — these used to point at "L9". L9 has arrived and neither is built, so the refusal
        // now names where the capability actually arrives (§10.6's later-work port models) rather
        // than a phase number that has since gone past. Updated, not loosened: the assertion is
        // still that the destination is NAMED.
        Assert.Contains("10.6", refusal);
        _out.WriteLine(refusal!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D1 — Y = BᵀZ⁻¹B, and the reciprocity that falls out of it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T0_7_TheRightHandSideIsPlusOrMinusOneOnThePortRowAndZeroEverywhereElse()
    {
        var mesh = Slab6x3();
        var hi   = PlanarPorts.Resolve(mesh, new PlanarPort(2, new EmPoint(6e-3, 0.75e-3),
                                                            PlanarPortSide.MaxX, 50.0));
        var rhs  = PlanarExcitation.RightHandSide(mesh.Bases.Count, hi);

        int nonZero = 0;
        for (int m = 0; m < mesh.Bases.Count; m++)
        {
            if (rhs[m] == Complex.Zero) continue;
            nonZero++;
            Assert.Equal(new Complex(-1, 0), rhs[m]);       // MaxX: current enters flowing −x̂
            Assert.Contains(m, hi.BasisIndices);
        }
        Assert.Equal(hi.BasisCount, nonZero);
    }

    [Fact]
    public void T0_8_YIsSymmetric_AndASymmetricStructureGivesEqualDiagonals()
    {
        // The real kernel, on the coarse fixture — this is the first time in L8 that anything is
        // actually excited, so it is worth doing against the production Green's function.
        var problem     = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
        _out.WriteLine($"N = {mesh.Bases.Count}, cells = {mesh.Cells.Count}");
        foreach (var p in prt) _out.WriteLine(p.Describe());

        var ctx = new PlanarSolveContext(mesh, prt);
        var y   = ctx.SolveAt(PlanarLineFixtures.Kernel(problem.Slab, 10e9), 10e9).Y;

        // Z is symmetric BIT FOR BIT (L8c computes m ≤ n and mirrors), so Y is symmetric because
        // BᵀZ⁻¹B is — but it passes through an LU, so this is a tolerance and not an equality, and
        // saying so precisely is the point L7b-b makes about overclaiming.
        double asym = (y[0, 1] - y[1, 0]).Magnitude / (y[0, 1] + y[1, 0]).Magnitude;
        double diag = (y[0, 0] - y[1, 1]).Magnitude / (y[0, 0] + y[1, 1]).Magnitude;
        _out.WriteLine($"|Y12−Y21|/|Y12+Y21| = {asym:E3}   |Y11−Y22|/|Y11+Y22| = {diag:E3}");

        Assert.True(asym < 1e-12, $"Y is not symmetric to LU tolerance: {asym:E3}");

        // A mirror-symmetric line driven from either end must present the same admittance. This is
        // the assertion that catches a transposed index, which no magnitude check would.
        Assert.True(diag < 1e-9, $"a symmetric line gives Y11 ≠ Y22: {diag:E3}");
    }

    [Fact]
    public void T0_9_TheRawSIsReciprocalAndPassive_BeforeAnyCalibrationExists()
    {
        var problem     = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(problem);
        var ctx         = new PlanarSolveContext(mesh, prt);

        foreach (double f in new[] { 2e9, 10e9 })
        {
            var s = ctx.RawScatteringAt(PlanarLineFixtures.Kernel(problem.Slab, f), f);
            double rec = (s[0, 1] - s[1, 0]).Magnitude;
            double pas = RfCore.RFNetwork.Passivity(s);
            double sum = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
            _out.WriteLine($"{f / 1e9,4:F0} GHz: |S11| = {s[0, 0].Magnitude:F4}, |S21| = {s[1, 0].Magnitude:F4}, " +
                           $"|S11|²+|S21|² = {sum:F4}, reciprocity {rec:E2}, passivity {pas:E3}");

            Assert.True(rec < 1e-10, $"raw S is not reciprocal at {f / 1e9} GHz: {rec:E3}");
            Assert.True(pas >= -1e-9, $"raw S is not passive at {f / 1e9} GHz: {pas:E3}");
        }
    }

    [Fact]
    public void T0_10_TheFeedClearanceCheckWarnsWithTheMeasuredDistance_AndIsSilentWhenClear()
    {
        // An isolated line: nothing to warn about.
        var clear = PlanarLineFixtures.Fr4Line(12e-3, 10e9);
        var (m1, p1) = PlanarLineFixtures.MeshAndPorts(clear);
        Assert.Null(PlanarPorts.CheckFeedClearance(m1, p1[0], 3 * clear.Slab.HeightM));

        // A second line running alongside the feed, well inside three substrate heights.
        double w = PlanarLineFixtures.Fr4HeroWidthM;
        var crowded = PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 10e9,
            PlanarLineFixtures.Rect(0, -0.5 * w, 12e-3, 0.5 * w),
            PlanarLineFixtures.Rect(0, 0.5 * w + 0.4e-3, 12e-3, 1.5 * w + 0.4e-3));
        var m2 = SurfaceMesher.Mesh(crowded, PlanarLineFixtures.Coarse).Mesh;
        var p2 = PlanarPorts.Resolve(m2, new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50.0));

        string? warn = PlanarPorts.CheckFeedClearance(m2, p2, 3 * crowded.Slab.HeightM);
        Assert.NotNull(warn);
        Assert.Contains("Port 1", warn);
        _out.WriteLine(warn!);
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────

    private static PlanarMesh Rebuild(List<PlanarCell> kept, PlanarMesh full)
    {
        var index = new Dictionary<(int, int), int>();
        for (int i = 0; i < kept.Count; i++) index[(kept[i].IX, kept[i].IY)] = i;

        var bases = new List<PlanarBasis>();
        foreach (var c in kept)
        {
            if (index.TryGetValue((c.IX + 1, c.IY), out int bx))
                bases.Add(new PlanarBasis(0, index[(c.IX, c.IY)], bx, PlanarBasisDirection.X));
            if (index.TryGetValue((c.IX, c.IY + 1), out int by))
                bases.Add(new PlanarBasis(0, index[(c.IX, c.IY)], by, PlanarBasisDirection.Y));
        }
        return new PlanarMesh(kept, bases, full.LayerNames, full.GridX, full.GridY);
    }
}
