// PMS-3 — a via-joined stacked-metal region is one conductor (brief-pms-3-stacked-metal-regions.md).
//
// The measurements and the accuracy gate are in src/Engine/Mom/RESOLVED.md §PMS-3. These hold the structural claims:
// off is the mesh it always was, on removes at least what it says, an ordinary post never qualifies, a MIM window keeps
// its lower plate, the interior's sheet is its stack's sheets in parallel, and the conduction network agrees at DC.

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms3StackedRegionTests(ITestOutputHelper output)
{
    private static PlanarMeshSettings Settings(bool stacked, int across = 4) => PlanarMeshSettings.Default with
    {
        MinCellsAcrossConductor = across, StackedRegions = stacked,
        MemoryLimitBytes = 16_000_000_000L, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
    };

    private static PlanarMeshReport Mesh(SyntheticMmicChip chip, bool stacked, int across = 4)
    {
        var ports = chip.Ports();
        var problem = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        return SurfaceMesher.Mesh(problem, Settings(stacked, across), PlanarEdgeReference.LocalConductorWidth, ports: ports);
    }

    private static string Digest(PlanarMesh mesh)
    {
        var sb = new StringBuilder();
        foreach (var c in mesh.Cells)
            sb.Append($"{c.LayerIndex},{c.IX},{c.IY},{c.XMin:R},{c.YMin:R},{c.XMax:R},{c.YMax:R},{c.Region is null},{c.Film};");
        foreach (var b in mesh.Bases) sb.Append($"{b.LayerIndex},{b.CellA},{b.CellB},{b.Direction},{b.AttachesToGround};");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Gate 1 — off, the cells and bases are byte for byte the mesh before PMS-3 (digests taken from the
    /// mesher as it stood before the change; S, S with MIM and L were compared the same way, see RESOLVED.md).</summary>
    [Theory]
    [InlineData(true, "8C03545B642A51E8")]
    [InlineData(false, "5BED49B15375315E")]
    public void Off_MeshesExactlyAsBefore(bool regionVia, string digest)
        => Assert.Equal(digest, Digest(Mesh(SyntheticMmicChip.AccuracyBlock(regionVia), stacked: false).Mesh));

    /// <summary>Gate 4 — on fixture S the unknowns fall by at least the interior cells removed, and the report's saving
    /// is the real difference. Brief 2's offline estimate for it was 515,450.</summary>
    [Fact]
    public void OnS_TheCountFallsByAtLeastTheInterior()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.S);
        var off = Mesh(chip, stacked: false);
        var on = Mesh(chip, stacked: true);
        output.WriteLine($"S: off {off.UnknownCount:N0}, on {on.UnknownCount:N0} (brief 2's estimate 515,450)");

        int removedCells = off.CellCount - on.CellCount;
        Assert.True(removedCells > 0);
        Assert.True(off.UnknownCount - on.UnknownCount >= removedCells);
        Assert.Equal(off.UnknownCount - on.UnknownCount, on.StackedUnknownsSaved);
        Assert.Equal(on.StackedUnknownsSaved, off.StackedUnknownsSaved);       // what "off" said turning it on would save
        Assert.Contains(off.Notes, n => n.Contains($"Stacked regions would save {off.StackedUnknownsSaved:N0}"));
        // A backside via inside the region keeps its foot: a position another via covers is never merged.
        Assert.Equal(off.Mesh.Bases.Count(b => b.AttachesToGround), on.Mesh.Bases.Count(b => b.AttachesToGround));
    }

    /// <summary>R-msh-2 / R-via-5 hold on what remains: cells in (LayerIndex, IY, IX) order, every horizontal basis
    /// before every vertical one, and every basis naming cells that exist on its own level.</summary>
    [Fact]
    public void On_TheOrderingContractHolds()
    {
        var mesh = Mesh(SyntheticMmicChip.AccuracyBlock(regionVia: true), stacked: true).Mesh;
        Assert.NotEmpty(mesh.Stacks);
        for (int i = 1; i < mesh.Cells.Count; i++)
        {
            var (a, b) = (mesh.Cells[i - 1], mesh.Cells[i]);
            Assert.True((a.LayerIndex, a.IY, a.IX).CompareTo((b.LayerIndex, b.IY, b.IX)) < 0);
        }
        int firstZ = mesh.Bases.ToList().FindIndex(b => b.Direction == PlanarBasisDirection.Z);
        Assert.True(firstZ > 0);
        Assert.All(mesh.Bases.Skip(firstZ), b => Assert.Equal(PlanarBasisDirection.Z, b.Direction));
        Assert.All(mesh.Bases.Take(firstZ), b =>
        {
            Assert.Equal(b.LayerIndex, mesh.Cells[b.CellA].LayerIndex);
            Assert.Equal(b.LayerIndex, mesh.Cells[b.CellB].LayerIndex);
        });
    }

    /// <summary>Gate 5 — an ordinary post never qualifies: the via fixtures mesh identically with the setting on.</summary>
    [Fact]
    public void NoMergeOnAPost()
    {
        var problems = new[]
        {
            ViaBasisTests.TwoLevelWithVia(),
            MmicCoilFixture.Coil(),
            PlanarGroundPath.Extend(SyntheticMmicChip.AccuracyBlock(regionVia: false).ToProblem(),
                                    SyntheticMmicChip.AccuracyBlock(regionVia: false).Ports()).Problem,
        };
        foreach (var p in problems)
        {
            var on = SurfaceMesher.Mesh(p, PlanarMeshSettings.Default with { StackedRegions = true },
                                        PlanarEdgeReference.LocalConductorWidth);
            var off = SurfaceMesher.Mesh(p, PlanarMeshSettings.Default, PlanarEdgeReference.LocalConductorWidth);
            Assert.Equal(0, on.StackedRegionCount);
            Assert.Equal(Digest(off.Mesh), Digest(on.Mesh));
        }
    }

    /// <summary>A MIM Via window joins MIM Metal to Metal2 over the Metal1 bottom plate: the window keeps its LOWEST
    /// level, MIM Metal (the capacitor's plate stays 0.2 µm over Metal1), drops Metal2, and Metal1 is not touched.</summary>
    [Fact]
    public void AMimWindow_KeepsItsLowerPlate()
    {
        var chip = SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600) with { MimCapacitors = true });
        var mesh = Mesh(chip, stacked: true).Mesh;

        int window = mesh.Stacks.ToList().FindIndex(s => s.LowerLayerIndex == 1 && s.UpperLayerIndex == 2);
        Assert.True(window >= 0);
        Assert.Equal(1, mesh.Stacks[window].KeptLayerIndex);
        var interior = mesh.Cells.Where(c => c.Stack == window).Select(c => (c.IX, c.IY)).ToHashSet();
        Assert.NotEmpty(interior);
        Assert.All(interior, p => Assert.Contains(mesh.Cells, c => c.LayerIndex == 1 && (c.IX, c.IY) == p));
        Assert.DoesNotContain(mesh.Cells, c => c.LayerIndex == 2 && interior.Contains((c.IX, c.IY)));
        Assert.Equal(interior.Count, mesh.Cells.Count(c => c.LayerIndex == 0 && interior.Contains((c.IX, c.IY))));
    }

    /// <summary>R-pms3-3 — an interior cell's surface impedance is its stack's sheets in parallel, at every frequency.</summary>
    [Fact]
    public void TheInterior_CarriesItsStacksSheetsInParallel()
    {
        var chip = SyntheticMmicChip.AccuracyBlock(regionVia: true);
        var problem = chip.ToProblem();
        var mesh = Mesh(chip, stacked: true).Mesh;
        var loss = PlanarConductorLoss.For(problem);
        foreach (double f in new[] { 1e9, 10e9 })
        {
            var t = loss.SheetTable(mesh, 2 * Math.PI * f);
            var parallel = 1.0 / (1.0 / t[0] + 1.0 / t[1]);
            Assert.True((t[mesh.StackClassOf(0)] - parallel).Magnitude <= 1e-15 * parallel.Magnitude);
        }
        // The interior keeps the LOWEST joined level, Metal1 (RESOLVED.md §PMS-3).
        Assert.All(mesh.Cells.Where(c => c.Stack >= 0), c => Assert.Equal(0, c.LayerIndex));
        Assert.All(mesh.Stacks, st => Assert.Equal(0, st.KeptLayerIndex));
    }

    /// <summary>Gate 3 — the conduction network at DC. On A1 at the reference settings the port-to-ground resistance
    /// (no region on its path) is unchanged, and the port-to-port resistance through the region is 2.6 % HIGH: the
    /// brief's 1 % is NOT met. Current arriving on Metal2 must change level inside the one-cell rim, because the interior
    /// keeps Metal1; RESOLVED.md §PMS-3 has the measurement, the rim-width ladder that closes it and why the lowest level
    /// is still the one kept. In the units the run publishes it is |ΔS| = 1.7e-5 at 0 Hz.</summary>
    [Fact]
    public void AtDc_TheResistancesAgree_ToTheMeasuredRimError()
    {
        var chip = SyntheticMmicChip.AccuracyBlock(regionVia: true);
        var ports = chip.Ports();
        var grounded = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        var feed = PlanarFeedExtension.ExtendDetailed(grounded, ports, PlanarSolveSettings.Default.Calibration);

        PlanarDcResult Dc(bool stacked)
        {
            var report = new PlanarKernel().Mesh(feed.Problem, Settings(stacked, across: 2), ports: ports, leads: feed.Leads);
            var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
            return PlanarDcSolve.Solve(feed.Problem, report.Mesh, resolved, feed.Leads);
        }

        var full = Dc(false);
        var stacked = Dc(true);
        double r12Full = -1.0 / full.Y[0, 1].Real, r12 = -1.0 / stacked.Y[0, 1].Real;
        double r33Full = 1.0 / full.Y[2, 2].Real, r33 = 1.0 / stacked.Y[2, 2].Real;
        double dS = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) dS = Math.Max(dS, (stacked.S[i, j] - full.S[i, j]).Magnitude);
        output.WriteLine($"R12 {r12Full:G6} / {r12:G6} Ω ({r12 / r12Full - 1:P2}); R33 {r33Full:G6} / {r33:G6} Ω; max|ΔS| {dS:E2}");

        Assert.True(Math.Abs(r33 / r33Full - 1) < 1e-3);
        Assert.InRange(r12 / r12Full - 1, 0.0, 0.035);
        Assert.True(dS < 1e-4);
    }
}
