// PMS-2 — what drives N on an MMIC (brief-pms-2-measure-mesh-drivers.md).
//
// The measurements and the decision are in src/Engine/Mom/RESOLVED.md §PMS-2; these hold the instruments they were
// taken with: the grid diagnostic leaves the mesh untouched and accounts for every line, the offline local-grid
// estimator reproduces today's mesh when forced uniform, the MIM option builds the chain a spanning post resolves to,
// and the accuracy references in testdata/pms2 still describe the fixtures that produced them.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms2MeshDriverTests(ITestOutputHelper output)
{
    /// <summary>The settings the accuracy references were solved at (testdata/pms2/README.md).</summary>
    private static PlanarMeshSettings ReferenceSettings => PlanarMeshSettings.Default with
    {
        MinCellsAcrossConductor = 2,
        MemoryLimitBytes = 16_000_000_000L, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
    };

    public static TheoryData<string> Fixtures => new() { "A1", "A2", "S0.6", "S1.4" };

    private static SyntheticMmicChip Chip(string name) => name switch
    {
        "A1" => SyntheticMmicChip.AccuracyBlock(regionVia: true),
        "A2" => SyntheticMmicChip.AccuracyBlock(regionVia: false),
        "S0.6" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600)),
        "S1.4" => SyntheticMmicChip.Build(SyntheticMmicChipSpec.S),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string ReferenceDirectory()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "testdata", "pms2");
            if (Directory.Exists(c)) return c;
        }
        throw new DirectoryNotFoundException("testdata/pms2");
    }

    private static (PlanarProblem Problem, List<PlanarPort> Ports) Meshable(SyntheticMmicChip chip)
    {
        var ports = chip.Ports();
        return (PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem, ports);
    }

    /// <summary>R-pms2-2 — the diagnostic is filled and never read: the grid, cells and bases are identical with it
    /// attached.</summary>
    [Fact]
    public void TheDiagnostic_LeavesTheMeshIdentical()
    {
        var (problem, ports) = Meshable(Chip("S0.6"));
        var plain = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth, ports: ports).Mesh;
        var diag = new PlanarGridDiagnostics();
        var with = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth,
                                      ports: ports, gridDiagnostics: diag).Mesh;

        Assert.Equal(plain.GridX, with.GridX);
        Assert.Equal(plain.GridY, with.GridY);
        Assert.Equal(plain.Cells, with.Cells);
        Assert.Equal(plain.Bases, with.Bases);
        Assert.Equal(with.GridX, diag.X.Lines);
    }

    /// <summary>Gate 2 — every final gridline is attributed to exactly one source, so the attribution sums to the grid's
    /// own line count, per axis.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TheLineAttribution_SumsToTheGrid(string name)
    {
        var (problem, ports) = Meshable(Chip(name));
        var diag = new PlanarGridDiagnostics();
        var mesh = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth,
                                      ports: ports, gridDiagnostics: diag).Mesh;
        foreach (var (axis, lines) in new[] { (diag.X, mesh.GridX.Count), (diag.Y, mesh.GridY.Count) })
        {
            var counts = axis.CountBySource();
            output.WriteLine($"{name}: {lines} lines — " + string.Join(", ", counts.Where(c => c.Value > 0).Select(c => $"{c.Key} {c.Value}")));
            Assert.Equal(lines, counts.Values.Sum());
            Assert.Equal(lines, axis.Source.Length);
        }
    }

    /// <summary>Gate 1 — the local-grid estimator, forced uniform, reproduces the mesher's cell and unknown counts within
    /// 3 % (it measures its own metal and footprints from the polygons, so this is its calibration).</summary>
    [Theory]
    [InlineData("A1")]
    [InlineData("A2")]
    [InlineData("S0.6")]
    public void TheEstimator_ForcedUniform_ReproducesTheMesh(string name)
    {
        var (problem, ports) = Meshable(Chip(name));
        var diag = new PlanarGridDiagnostics();
        var report = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth,
                                        ports: ports, gridDiagnostics: diag);
        var uniform = new LocalGridEstimate(problem, diag, ports, PlanarMeshSettings.Default.MinCellsAcrossConductor)
            .Count(forceUniform: true);

        double dCells = (uniform.Cells - report.CellCount) / (double)report.CellCount;
        double dN = (uniform.Unknowns - report.Mesh.Bases.Count) / (double)report.Mesh.Bases.Count;
        output.WriteLine($"{name}: cells {uniform.Cells:N0} vs {report.CellCount:N0} ({dCells:P3}), " +
                         $"N {uniform.Unknowns:N0} vs {report.Mesh.Bases.Count:N0} ({dN:P3})");
        Assert.InRange(Math.Abs(dCells), 0, 0.03);
        Assert.InRange(Math.Abs(dN), 0, 0.03);
    }

    /// <summary>R-pms2-1 — with MIM capacitors on, the chip is three levels and a Metal1–Metal2 post becomes a chain:
    /// its footprint on MIM Metal and one via per gap, as the extractor builds it.</summary>
    [Fact]
    public void TheMimOption_ChainsThePosts()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600) with { MimCapacitors = true });
        var problem = chip.ToProblem();
        Assert.Equal(3, problem.Layers.Count);
        Assert.NotEmpty(chip.MimVia);
        var posts = chip.Posts.Count;
        Assert.Contains(problem.ViaList, v => v.LowerLayerIndex == 0 && v.UpperLayerIndex == 1 && v.Polygons.Count == posts);
        Assert.Contains(problem.ViaList, v => v.LowerLayerIndex == 1 && v.UpperLayerIndex == 2 && v.Polygons.Count == posts);
        Assert.Contains(problem.ViaList, v => v.LowerLayerIndex == 1 && v.UpperLayerIndex == 2 && v.Polygons.Count == chip.MimVia.Count);
        Assert.All(chip.Ports(), p => Assert.Equal(2, p.LayerIndex));
    }

    /// <summary>Gate 3's references still describe the fixtures: the blocks mesh, at the recorded settings, to the
    /// unknown counts the references were solved at, and each file holds 3 ports at 1, 5 and 10 GHz.</summary>
    [Theory]
    [InlineData("A1", true, 11_655)]
    [InlineData("A2", false, 7_308)]
    public void TheAccuracyReferences_DescribeTheFixtures(string name, bool regionVia, int expectedUnknowns)
    {
        // PlanarKernel.Solve's own sequence up to the mesh: the ground path, the grown feed leads, then the mesh.
        var chip = SyntheticMmicChip.AccuracyBlock(regionVia);
        var ports = chip.Ports();
        var grounded = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var feed = PlanarFeedExtension.ExtendDetailed(grounded, ports, PlanarSolveSettings.Default.Calibration);
        var report = new PlanarKernel().Mesh(feed.Problem, ReferenceSettings, ports: ports, leads: feed.Leads);
        output.WriteLine($"{name}: N {report.UnknownCount:N0}");
        Assert.Equal(expectedUnknowns, report.UnknownCount);

        var snp = RfCore.TouchstoneIO.ReadFile(Path.Combine(ReferenceDirectory(), name + ".s3p"));
        Assert.Equal([1e9, 5e9, 10e9], snp.Frequencies);
        Assert.Equal(3, snp.Matrices[0].RowCount);
    }
}
