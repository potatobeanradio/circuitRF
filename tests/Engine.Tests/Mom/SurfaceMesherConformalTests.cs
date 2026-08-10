// Conformal boundary cells — M1: the cut cell as GEOMETRY, gated on TILING alone.
//
// §2's instruction is explicit: do this first and gate it on tiling. A cut cell that does not tile
// its polygon is a solver that solves a slightly different structure and reports a smooth, plausible,
// wrong s-parameter — R-msh-1's own words, and the whole reason that rule exists. No physics is
// touched here; nothing in this file evaluates a Green's function.
//
// The deliverable §2 asks for is a TABLE rather than a pass: cut / merged / total cells and the area
// error, for the shipping parts and for the 96-point disc. The PCell half lives in
// tests/Ui.Tests/Em/PlanarMeshPCellTests.cs, because MBend/MTaper/MKlopf are in src/Ui and the
// reference graph is Ui → Engine.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class SurfaceMesherConformalTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The rim-grading brief's own disc: 96 vertices, no axis-parallel edge anywhere.</summary>
    private static PlanarProblem Disc(int points = 96, double radiusM = 1.45e-3, double fHz = 10e9)
    {
        var ring = new EmPoint[points];
        for (int i = 0; i < points; i++)
        {
            double a = 2.0 * Math.PI * i / points;
            ring[i] = new EmPoint(radiusM * Math.Cos(a), radiusM * Math.Sin(a));
        }
        return new PlanarProblem(
            [new PlanarConductorLayer("Metal", [new PlanarPolygon(ring)], 5.8e7, 35e-6)],
            GroundedSlab.Fr4Starter, fHz);
    }

    /// <summary>A symmetric linear taper — MTaper's own shape, without needing src/Ui.</summary>
    private static PlanarProblem Taper(double w0 = 2.9e-3, double w1 = 1.0e-3, double len = 10e-3,
                                       double fHz = 10e9)
        => new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, -0.5 * w0), new EmPoint(len, -0.5 * w1),
                                      new EmPoint(len, 0.5 * w1), new EmPoint(0, 0.5 * w0)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, fHz);

    /// <summary>
    /// A 45° MITRED bend — MBend's own discontinuity, the shape L8b's D2 measured at 2.8% cut-area
    /// error. The chamfer replaces the outer corner, so the outline carries exactly one oblique edge
    /// and the rest is Manhattan: the cheapest fixture that separates "the cut cells work" from "the
    /// interior is unchanged".
    /// </summary>
    private static PlanarProblem Mitre(double w = 2.9e-3, double arm = 8e-3, double fHz = 10e9)
    {
        double m = 0.65 * w * Math.Sqrt(2.0) / 2.0;    // the classic ~65% mitre, along each arm
        return new([new PlanarConductorLayer("Metal",
                  [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(arm, 0),
                                      new EmPoint(arm, arm - m), new EmPoint(arm - m, arm),
                                      new EmPoint(arm - w, arm), new EmPoint(arm - w, w),
                                      new EmPoint(0, w)])],
                  5.8e7, 35e-6)],
                GroundedSlab.Fr4Starter, fHz);
    }

    /// <summary>§10.7's own FR-4 hero — the Manhattan bit-identity fixture, N = 552.</summary>
    private static PlanarProblem Hero() => new(
        [new PlanarConductorLayer("Metal",
            [new PlanarPolygon([new EmPoint(0, 0), new EmPoint(20e-3, 0),
                                new EmPoint(20e-3, 2.9e-3), new EmPoint(0, 2.9e-3)])],
            5.8e7, 35e-6)],
        GroundedSlab.Fr4Starter, 10e9);

    private static PlanarMeshSettings Settings(PlanarBoundaryCells cells, int cpw = 20,
                                               int edgeCells = 3, bool edgeMesh = true)
        => new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: edgeMesh, EdgeCells: edgeCells,
               BoundaryCells: cells);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cut-2 — A MANHATTAN MESH IS BIT-IDENTICAL, asserted on gridlines, cells AND bases
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(20, 0, false)]
    [InlineData(20, 3, true)]
    [InlineData(45, 10, true)]
    public void C1_ManhattanIsBitIdentical(int cpw, int edgeCells, bool edgeMesh)
    {
        var problem = Hero();
        var a = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Staircase, cpw, edgeCells, edgeMesh));
        var b = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal, cpw, edgeCells, edgeMesh));

        Assert.Equal(a.Mesh.GridX.Count, b.Mesh.GridX.Count);
        for (int i = 0; i < a.Mesh.GridX.Count; i++) Assert.Equal(a.Mesh.GridX[i], b.Mesh.GridX[i]);
        Assert.Equal(a.Mesh.GridY.Count, b.Mesh.GridY.Count);
        for (int i = 0; i < a.Mesh.GridY.Count; i++) Assert.Equal(a.Mesh.GridY[i], b.Mesh.GridY[i]);

        Assert.Equal(a.Mesh.Cells.Count, b.Mesh.Cells.Count);
        for (int i = 0; i < a.Mesh.Cells.Count; i++)
        {
            Assert.Equal(a.Mesh.Cells[i], b.Mesh.Cells[i]);
            // A Manhattan polygon has no oblique edge, so no cell CAN be cut — this is the property,
            // not a consequence of the coordinates happening to match.
            Assert.Null(b.Mesh.Cells[i].Region);
        }

        Assert.Equal(a.Mesh.Bases.Count, b.Mesh.Bases.Count);
        for (int i = 0; i < a.Mesh.Bases.Count; i++) Assert.Equal(a.Mesh.Bases[i], b.Mesh.Bases[i]);

        Assert.Equal(0, b.CutCellCount);
        Assert.Equal(0, b.MergedSliverCount);
        Assert.Equal(0, b.StaircaseFallbackCells);
    }

    [Fact]
    public void C1b_TheHeroIsStillExactlyN552()
    {
        Assert.Equal(552, SurfaceMesher.Mesh(Hero(), PlanarMeshSettings.Default).UnknownCount);
        Assert.Equal(552, SurfaceMesher.Mesh(Hero(),
            PlanarMeshSettings.Default with { BoundaryCells = PlanarBoundaryCells.Conformal })
            .UnknownCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cut-1 — THE TILING GATE, and §2's own table
    // ══════════════════════════════════════════════════════════════════════════════════════════

    public static TheoryData<string> Parts() => new() { "disc", "taper", "mitre" };

    private static PlanarProblem PartNamed(string name) => name switch
    {
        "disc"  => Disc(),
        "taper" => Taper(),
        _       => Mitre(),
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void C2_ConformalCellsTileTheArtworkToRoundOff(string part)
    {
        var problem = PartNamed(part);
        double drawn = problem.Layers[0].Polygons[0].Area();

        var stair = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Staircase));
        var cut   = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal));

        double eStair = Math.Abs(stair.MeshedAreaM2 - drawn) / drawn;
        double eCut   = Math.Abs(cut.MeshedAreaM2   - drawn) / drawn;

        _out.WriteLine($"{part,-6}  drawn {drawn * 1e6:F6} mm²");
        _out.WriteLine($"        staircase: cells {stair.CellCount,5}, N {stair.UnknownCount,5}, " +
                       $"area {stair.MeshedAreaM2 * 1e6:F6} mm², error {eStair:P4}");
        _out.WriteLine($"        conformal: cells {cut.CellCount,5}, N {cut.UnknownCount,5}, " +
                       $"cut {cut.CutCellCount,4}, merged {cut.MergedSliverCount,3}, " +
                       $"fallback {cut.StaircaseFallbackCells,3}, " +
                       $"area {cut.MeshedAreaM2 * 1e6:F6} mm², error {eCut:E3}");

        // ROUND-OFF, not 0.5%. The cut region of every boundary cell is the drawn outline clipped to
        // the cell, so the union is the outline exactly — up to the arithmetic of the clip and the
        // shoelace, which is what 1e-12 is here.
        Assert.True(eCut < 1e-12,
            $"{part}: conformal cells left an area error of {eCut:E3}, which is not round-off — the " +
            "mesh is solving a slightly different structure from the one that was drawn");
        Assert.True(eCut < eStair / 100.0,
            $"{part}: conformal {eCut:E3} against staircase {eStair:P4} — no material improvement");
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void C2b_NoCellStraysOutsideTheMetal(string part)
    {
        var problem = PartNamed(part);
        var poly    = problem.Layers[0].Polygons[0];
        var report  = SurfaceMesher.Mesh(problem, Settings(PlanarBoundaryCells.Conformal));

        // Every VERTEX of every cut piece must lie in or on the drawn polygon: the tiling gate above
        // is an area identity, and an area identity alone would survive a cell that reached outside
        // by exactly as much as another fell short.
        double tol = 1e-9 * Math.Sqrt(poly.Area());
        foreach (var cell in report.Mesh.Cells)
        {
            if (cell.Region is null) continue;
            foreach (var piece in cell.Region.Pieces)
                foreach (var v in piece)
                    Assert.True(Polygon2D.ContainsOrOn(poly.Outer, v, tol),
                        $"{part}: a cut cell has a vertex at ({v.X:E3}, {v.Y:E3}), outside the metal");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-cut-3 / R-cut-4 — the sliver, and what a merged cell breaks
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [MemberData(nameof(Parts))]
    public void C3_NoCellIsANormalisationSliver(string part)
    {
        var report = SurfaceMesher.Mesh(PartNamed(part), Settings(PlanarBoundaryCells.Conformal));

        int survivors = 0;
        double worst = 1.0;
        foreach (var c in report.Mesh.Cells)
        {
            if (c.Region is null) continue;
            double f = c.Area / (c.Width * c.Height);
            worst = Math.Min(worst, f);
            if (f < SurfaceMesher.DefaultSliverAreaFraction) survivors++;
        }
        _out.WriteLine($"{part,-6}: {report.MergedSliverCount} sliver(s) merged, {survivors} left, " +
                       $"smallest surviving area fraction {worst:P3}");

        // A sliver that survives is REPORTED rather than silently solved — the note has to say so, and
        // it is the note the user acts on.
        if (survivors > 0)
            Assert.Contains(report.Notes, n => n.Contains("no ordinary neighbour"));
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void C4_TheBasisSetSurvivesMerging(string part)
    {
        var mesh = SurfaceMesher.Mesh(PartNamed(part), Settings(PlanarBoundaryCells.Conformal)).Mesh;

        var seen = new HashSet<(int, int, PlanarBasisDirection)>();
        foreach (var b in mesh.Bases)
        {
            Assert.NotEqual(b.CellA, b.CellB);                       // no rooftop from a cell to itself
            Assert.True(seen.Add((Math.Min(b.CellA, b.CellB), Math.Max(b.CellA, b.CellB), b.Direction)),
                $"{part}: two bases span the same cell pair in the same direction — one unknown " +
                "counted twice, which is a singular matrix and not a mesh defect anything would name");
        }

        // R-msh-2's ordering contract, re-asserted because merging emits a cell at a position whose
        // neighbour was skipped.
        for (int i = 1; i < mesh.Cells.Count; i++)
        {
            var p = mesh.Cells[i - 1];
            var c = mesh.Cells[i];
            Assert.True((p.LayerIndex, p.IY, p.IX).CompareTo((c.LayerIndex, c.IY, c.IX)) < 0,
                $"{part}: cell order broke at {i}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The control, and Auto
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void C5_AutoDoesNotThrowTheBoundaryModelAway()
    {
        var s = new PlanarMeshSettings(Auto: true, BoundaryCells: PlanarBoundaryCells.Conformal);
        Assert.Equal(PlanarBoundaryCells.Conformal, s.Resolved.BoundaryCells);
        // …and everything else Auto DOES throw away is still thrown away.
        var t = new PlanarMeshSettings(Auto: true, CellsPerWavelength: 99, EdgeCells: 17,
                                       BoundaryCells: PlanarBoundaryCells.Conformal);
        Assert.Equal(PlanarMeshSettings.DefaultCellsPerWavelength, t.Resolved.CellsPerWavelength);
        Assert.Equal(PlanarMeshSettings.DefaultEdgeCells, t.Resolved.EdgeCells);
        Assert.Equal(PlanarBoundaryCells.Conformal, t.Resolved.BoundaryCells);

        // A mesh built with Auto on must actually BE the conformal one, not merely report it.
        var report = SurfaceMesher.Mesh(Disc(), s);
        Assert.Equal(PlanarBoundaryCells.Conformal, report.BoundaryCells);
        Assert.True(report.CutCellCount > 0);
    }

    [Fact]
    public void C6_TheNotesSayWhichBoundaryModelProducedTheMesh()
    {
        var stair = SurfaceMesher.Mesh(Disc(), Settings(PlanarBoundaryCells.Staircase));
        var cut   = SurfaceMesher.Mesh(Disc(), Settings(PlanarBoundaryCells.Conformal));

        Assert.Contains(stair.Notes, n => n.Contains("approximated by a STAIRCASE"));
        // §4 — the staircasing note must stop CLAIMING a staircase when the cells are conformal, and
        // so must the edge-mesh note beside it.
        Assert.DoesNotContain(cut.Notes, n => n.Contains("approximated by a STAIRCASE"));
        Assert.DoesNotContain(cut.Notes, n => n.Contains("approximated by a staircase"));
        Assert.Contains(cut.Notes, n => n.Contains("CONFORMAL"));

        foreach (var n in cut.Notes) _out.WriteLine($"[conformal] {n}");
    }

    [Fact]
    public void C7_AnAllManhattanArtworkSaysTheControlChangedNothing()
    {
        var cut = SurfaceMesher.Mesh(Hero(), Settings(PlanarBoundaryCells.Conformal));
        Assert.Contains(cut.Notes, n => n.Contains("no cell needed cutting"));
    }
}
