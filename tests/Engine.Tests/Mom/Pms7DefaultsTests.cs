// PMS-7 — the defaults (brief-pms-7-defaults-and-docs.md). Stacked regions and the local grid are the product's
// defaults. A stacked region must clear a physical floor as well as a cell count; the mesher names the local grid as a
// remedy where it brings a mesh past this machine's memory back down, and only there; and a run whose port calibration
// needs the shared grid (a passive neighbour, a coupled group) is solved on it rather than refused. RESOLVED.md §PMS-7.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class Pms7DefaultsTests
{
    private static readonly PlanarMeshSettings Limited = PlanarMeshSettings.Default with
    {
        MemoryLimitBytes = 16_000_000_000L, MemoryLimitSource = PlanarMemoryLimitSource.PhysicalMemory,
    };

    private static PlanarMeshReport Mesh(SyntheticMmicChip chip, PlanarMeshSettings s)
    {
        var ports = chip.Ports();
        var problem = PlanarGroundPath.Extend(chip.ToProblem(), ports).Problem;
        return SurfaceMesher.Mesh(problem, s, PlanarEdgeReference.LocalConductorWidth, ports: ports);
    }

    /// <summary>A region narrower than <see cref="PlanarMeshSettings.StackedRegionMinSideM"/> stays whole however many
    /// cells it spans: A1's 120 µm region merges at the default floor and not under a 200 µm one.</summary>
    [Fact]
    public void ARegionUnderThePhysicalFloor_IsNotMerged()
    {
        var a1 = SyntheticMmicChip.AccuracyBlock(regionVia: true);
        var on = Mesh(a1, Limited with { StackedRegions = true });
        var floored = Mesh(a1, Limited with { StackedRegions = true, StackedRegionMinSideM = 200e-6 });
        var off = Mesh(a1, Limited);

        Assert.Equal(1, on.StackedRegionCount);
        Assert.Equal(0, floored.StackedRegionCount);
        Assert.Equal(off.UnknownCount, floored.UnknownCount);
    }

    /// <summary>Past the machine's memory the local grid is named with the count it meshes at; with it on, or with a
    /// mesh that fits, it is not.</summary>
    [Fact]
    public void TheLocalGrid_IsNamed_OnlyWhereItBinds()
    {
        var s06 = SyntheticMmicChip.Build(SyntheticMmicChipSpec.Family('S', 600));
        var tensor = Mesh(s06, Limited);
        var local = Mesh(s06, Limited with { LocalGrid = true });
        Assert.True(tensor.PastMachineLimit);

        string Text(PlanarMeshReport r) => string.Join(" ", r.Notes);
        Assert.Contains("turn on Local grid", Text(tensor), StringComparison.Ordinal);
        Assert.Contains($"The local grid meshes this artwork at {local.UnknownCount:N0} unknowns", Text(tensor),
                        StringComparison.Ordinal);
        Assert.DoesNotContain("turn on Local grid", Text(local), StringComparison.Ordinal);

        var a2 = Mesh(SyntheticMmicChip.AccuracyBlock(regionVia: false), Limited);
        Assert.False(a2.PastMachineLimit);
        Assert.DoesNotContain("Local grid", Text(a2), StringComparison.Ordinal);
    }

    /// <summary>A feed with a portless pour 0.27 substrate heights away calibrates by taking the neighbour into its
    /// standard (PCAL3), which a block mesh declines. With the local grid on, the run is solved on the shared grid instead
    /// of refused, says so, and is the shared grid's answer to the bit.</summary>
    [Fact]
    public void ACalibrationThatNeedsTheSharedGrid_IsSolvedOnIt_NotRefused()
    {
        var slab = new GroundedSlab(0.9e-3, new EmMaterial(4.4, 0.02));
        const double W = 254e-6, Len = 3.83e-3, F = 5e9, Gap = 0.27 * 0.9e-3;
        var problem = PlanarLineFixtures.Problem(slab, F,
            PlanarLineFixtures.Rect(0, 0, Len, W),
            PlanarLineFixtures.Rect(0, W + Gap, Len, W + Gap + 1e-3));   // a pour, whose interior the local grid coarsens
        PlanarPort[] ports =
        [
            new(1, new EmPoint(0, 0.5 * W), PlanarPortSide.MinX, 50.0),
            new(2, new EmPoint(Len, 0.5 * W), PlanarPortSide.MaxX, 50.0),
        ];
        var shared = PlanarLineFixtures.Coarse;
        var local = shared with { LocalGrid = true };
        Assert.True(SurfaceMesher.Mesh(problem, local, PlanarEdgeReference.LocalConductorWidth, ports: ports).Mesh.HasBlocks);

        var onLocal = new PlanarKernel().Solve(problem, local, ports, [F]);
        var onShared = new PlanarKernel().Solve(problem, shared, ports, [F]);

        Assert.Equal(PlanarKernel.LocalGridSteppedAsideNote, onLocal.Notes[0]);
        Assert.DoesNotContain(PlanarKernel.LocalGridSteppedAsideNote, onShared.Notes);
        Assert.Equal(onShared.Data["S"].ComplexValues.ToArray(), onLocal.Data["S"].ComplexValues.ToArray());
    }
}
