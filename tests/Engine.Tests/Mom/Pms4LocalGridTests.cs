// PMS-4 — a locally refined shared grid: the mesher (brief-pms-4-local-grid-mesher.md).
//
// One test per gate claim, on fixtures small enough for the routine tier. The measurements on S at 1.0 and 1.4 mm and on
// L were taken with a scratch harness in Release and are in src/Engine/Mom/RESOLVED.md §PMS-4, as is the comparison of
// the setting-off mesh against the parent commit's.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms4LocalGridTests(ITestOutputHelper output)
{
    private static (PlanarProblem Problem, List<PlanarPort> Ports) Fixture(string name)
    {
        switch (name)
        {
            case "Line":  return (PlanarLineFixtures.GaAsLine(2e-3, 10e9), []);
            case "Taper": return (PlanarLineFixtures.Taper(GroundedSlab.GaAsStarter, 70e-6, 20e-6, 1.5e-3, 10e9), []);
            case "Coil":  return (MmicCoilFixture.Coil(), []);
        }
        var chip = name switch
        {
            "A1" => SyntheticMmicChip.AccuracyBlock(regionVia: true),
            "A2" => SyntheticMmicChip.AccuracyBlock(regionVia: false),
            "S0.6" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600)),
            "S0.6 square" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600) with { OctagonalBackside = false }),
            "S0.6 45°" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600) with { DiagonalTraces = true }),
            "S0.6 MIM" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600) with { MimCapacitors = true }),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var ports = chip.Ports();
        return (PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem, ports);
    }

    private static PlanarMeshReport Mesh((PlanarProblem Problem, List<PlanarPort> Ports) f, PlanarMeshSettings s)
        => SurfaceMesher.Mesh(f.Problem, s, PlanarEdgeReference.LocalConductorWidth, ports: f.Ports);

    public static TheoryData<string, PlanarBoundaryCells, bool> Degenerate => new()
    {
        { "Line", PlanarBoundaryCells.Staircase, false },
        { "Taper", PlanarBoundaryCells.Staircase, false },
        { "Taper", PlanarBoundaryCells.Conformal, false },
        { "Coil", PlanarBoundaryCells.Staircase, false },
        { "A1", PlanarBoundaryCells.Staircase, false },
        { "A1", PlanarBoundaryCells.Staircase, true },
        { "A2", PlanarBoundaryCells.Staircase, false },
        { "S0.6 45°", PlanarBoundaryCells.Conformal, false },
        { "S0.6 MIM", PlanarBoundaryCells.Staircase, true },
    };

    /// <summary>Gates 1 and 2 — with every block forced to 1 × 1 the local grid is the tensor grid: the same cells (the
    /// very records), the same bases and the same order, cut and merged conformal cells and stacked regions included.
    /// Off builds no block and no step.</summary>
    [Theory]
    [MemberData(nameof(Degenerate))]
    public void ForcedElementary_IsTheTensorGrid(string name, PlanarBoundaryCells boundary, bool stacked)
    {
        var f = Fixture(name);
        var s = PlanarMeshSettings.Default with { BoundaryCells = boundary, StackedRegions = stacked };
        var off = Mesh(f, s);
        var on = Mesh(f, s with { LocalGrid = true, LocalGridForceElementary = true });

        Assert.False(off.LocalGrid);
        Assert.False(off.Mesh.HasBlocks);
        Assert.True(on.LocalGrid);
        Assert.False(on.Mesh.HasBlocks);
        Assert.Equal(off.Mesh.GridX, on.Mesh.GridX);
        Assert.Equal(off.Mesh.GridY, on.Mesh.GridY);
        Assert.Equal(off.Mesh.Cells.Select(Key), on.Mesh.Cells.Select(Key));
        Assert.Equal(off.Mesh.Bases, on.Mesh.Bases);
    }

    /// <summary>A cell by value: a cut cell's region is a class, so its pieces are compared rather than its reference.</summary>
    private static string Key(PlanarCell c)
        => (c with { Region = null }).ToString() + "|" +
           (c.Region is null ? "" : string.Join(";", c.Region.Pieces.SelectMany(p => p).Select(v => $"{v.X:R},{v.Y:R}")));

    public static TheoryData<string> Coarsened => new() { "A1", "A2", "S0.6", "S0.6 45°", "Coil" };

    /// <summary>Gate 4 — every rooftop's two supports are rectangles inside their cells and on metal, its pulses sum to
    /// zero (−1 for a ground attachment), it never pairs a cell with itself, every face two cells of a level share is
    /// covered by exactly one rooftop segment, and a vertical basis pairs one block on two levels.</summary>
    [Theory]
    [MemberData(nameof(Coarsened))]
    public void TheLocalGrid_IsStructurallySound(string name)
    {
        var f = Fixture(name);
        var off = Mesh(f, PlanarMeshSettings.Default);
        var on = Mesh(f, PlanarMeshSettings.Default with { LocalGrid = true });
        var m = on.Mesh;
        output.WriteLine($"{name}: {off.UnknownCount:N0} → {on.UnknownCount:N0} unknowns, {on.RefinementSteps:N0} steps");
        Assert.True(m.HasBlocks);
        Assert.True(on.UnknownCount < off.UnknownCount);
        Assert.True(on.RefinementSteps > 0);

        int nx = m.GridX.Count - 1, ny = m.GridY.Count - 1, levels = m.LayerNames.Count;
        var at = new int[levels][];
        for (int li = 0; li < levels; li++) { at[li] = new int[nx * ny]; Array.Fill(at[li], -1); }
        for (int c = 0; c < m.Cells.Count; c++)
        {
            var cell = m.Cells[c];
            for (int iy = cell.IY; iy < cell.IY1; iy++)
                for (int ix = cell.IX; ix < cell.IX1; ix++)
                {
                    Assert.Equal(-1, at[cell.LayerIndex][iy * nx + ix]);   // blocks of one level never overlap
                    at[cell.LayerIndex][iy * nx + ix] = c;
                }
            Assert.Equal(m.GridX[cell.IX], cell.XMin);
            Assert.Equal(m.GridX[cell.IX1], cell.XMax);
            Assert.Equal(m.GridY[cell.IY], cell.YMin);
            Assert.Equal(m.GridY[cell.IY1], cell.YMax);
        }

        var xFace = new int[levels][]; var yFace = new int[levels][];
        for (int li = 0; li < levels; li++) { xFace[li] = new int[nx * ny]; yFace[li] = new int[nx * ny]; }
        foreach (var b in m.Bases)
        {
            var (sa, sb) = PlanarLocalGrid.Supports(m, b);
            Assert.Equal(b.AttachesToGround ? -1.0 : 0.0, sa.Sign + sb.Sign);
            var ca = m.Cells[b.CellA]; var cb = m.Cells[b.CellB];
            if (b.Direction == PlanarBasisDirection.Z)
            {
                Assert.Equal((ca.IX, ca.IY, ca.IX1, ca.IY1), (cb.IX, cb.IY, cb.IX1, cb.IY1));
                continue;
            }
            Assert.NotEqual(b.CellA, b.CellB);
            Assert.Equal(ca.LayerIndex, cb.LayerIndex);
            foreach (var (sup, cell) in new[] { (sa, ca), (sb, cb) })
            {
                Assert.True(sup.Area > 0);
                Assert.True(sup.XMin >= cell.XMin && sup.XMax <= cell.XMax && sup.YMin >= cell.YMin && sup.YMax <= cell.YMax);
                Assert.True(InMetal(f.Problem.Layers[cell.LayerIndex], 0.5 * (sup.XMin + sup.XMax), 0.5 * (sup.YMin + sup.YMax)));
            }
            bool x = b.Direction == PlanarBasisDirection.X;
            Assert.Equal(x ? ca.IX1 : ca.IY1, x ? cb.IX : cb.IY);
            int lo = b.IsSegment ? b.SegmentLo : Math.Max(x ? ca.IY : ca.IX, x ? cb.IY : cb.IX);
            int hi = b.IsSegment ? b.SegmentHi : Math.Min(x ? ca.IY1 : ca.IX1, x ? cb.IY1 : cb.IX1);
            Assert.True(lo >= Math.Max(x ? ca.IY : ca.IX, x ? cb.IY : cb.IX));
            Assert.True(hi <= Math.Min(x ? ca.IY1 : ca.IX1, x ? cb.IY1 : cb.IX1));
            for (int t = lo; t < hi; t++)
            {
                if (x) xFace[ca.LayerIndex][t * nx + ca.IX1 - 1]++;
                else   yFace[ca.LayerIndex][(ca.IY1 - 1) * nx + t]++;
            }
        }
        for (int li = 0; li < levels; li++)
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    int p = iy * nx + ix, a = at[li][p];
                    int expectX = ix + 1 < nx && a >= 0 && at[li][p + 1] >= 0 && at[li][p + 1] != a ? 1 : 0;
                    int expectY = iy + 1 < ny && a >= 0 && at[li][p + nx] >= 0 && at[li][p + nx] != a ? 1 : 0;
                    Assert.Equal(expectX, xFace[li][p]);
                    Assert.Equal(expectY, yFace[li][p]);
                }
    }

    /// <summary>Gate 5 — a block is a union of the tensor grid's own cells, so each level's tiled area is the tensor
    /// grid's, to the last bit's worth of summation: exact on Manhattan artwork, today's staircase on an oblique edge.</summary>
    [Theory]
    [InlineData("S0.6 square")]
    [InlineData("Coil")]
    [InlineData("S0.6 45°")]
    public void TheTiling_IsTheTensorGrids(string name)
    {
        var f = Fixture(name);
        var off = Mesh(f, PlanarMeshSettings.Default).Mesh;
        var on = Mesh(f, PlanarMeshSettings.Default with { LocalGrid = true }).Mesh;
        for (int li = 0; li < off.LayerNames.Count; li++)
        {
            double a = off.Cells.Where(c => c.LayerIndex == li).Sum(c => c.Area);
            double b = on.Cells.Where(c => c.LayerIndex == li).Sum(c => c.Area);
            output.WriteLine($"{name} {off.LayerNames[li]}: {a * 1e12:F3} µm² tensor, {b * 1e12:F3} µm² local");
            Assert.Equal(a, b, a * 1e-12);
        }
    }

    /// <summary>Gate 3 — on Manhattan artwork the built count IS brief 2's estimate for the same rules: the same cells and
    /// the same unknowns. (With octagonal vias the built mesh keeps their staircased rims at today's pitch where the
    /// estimator re-staircases them coarser; RESOLVED.md §PMS-4 measures that difference.) Meshed without ports: since
    /// PMS-6 a port's window carries only the feed's own lines across it, which the estimator's all-elementary port rule
    /// does not model.</summary>
    [Fact]
    public void OnManhattanArtwork_TheCountIsTheEstimate()
    {
        var f = Fixture("S0.6 square");
        var diag = new PlanarGridDiagnostics();
        SurfaceMesher.Mesh(f.Problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth,
                           gridDiagnostics: diag);
        var est = new LocalGridEstimate(f.Problem, diag, [], PlanarMeshSettings.Default.MinCellsAcrossConductor).Count();
        var on = Mesh((f.Problem, []), PlanarMeshSettings.Default with { LocalGrid = true });
        output.WriteLine($"built {on.UnknownCount:N0} / {on.CellCount:N0} cells; estimate {est.Unknowns:N0} / {est.Cells:N0}");
        Assert.Equal(est.Cells, on.CellCount);
        Assert.Equal(est.Unknowns, on.UnknownCount);
    }

    /// <summary>R-pms4-4 / PMS-6 R-pms6-6 — a mesh with blocks is meshed and reported (its count and its note), and its
    /// edge ports resolve (Pms6LocalGridConsumersTests solves one end to end).</summary>
    [Fact]
    public void ABlockMesh_IsReported_AndItsEdgePortsResolve()
    {
        var f = Fixture("A2");
        var report = Mesh(f, PlanarMeshSettings.Default with { LocalGrid = true });
        Assert.True(report.UnknownCount > 0);
        Assert.True(report.Mesh.HasBlocks);
        Assert.Contains(report.Notes, n => n.StartsWith("The grid is local", StringComparison.Ordinal));
        Assert.Equal(f.Ports.Count, PlanarPorts.ResolveAll(report.Mesh, f.Ports).Count);
    }

    /// <summary>R-pms4-1 — the setting survives Auto, as every setting that changes what is meshed does.</summary>
    [Fact]
    public void TheSetting_SurvivesAuto()
        => Assert.True((PlanarMeshSettings.Default with { Auto = true, LocalGrid = true }).Resolved.LocalGrid);

    private static bool InMetal(PlanarConductorLayer layer, double x, double y)
    {
        foreach (var p in layer.Polygons) if (p.Contains(x, y)) return true;
        return false;
    }
}
