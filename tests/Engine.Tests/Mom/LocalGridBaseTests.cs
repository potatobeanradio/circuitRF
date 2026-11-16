// ICF-4 R-icf4-1 — the local grid does not follow the sweep on artwork one or two base blocks across. Mesher only, no solve:
// the solve is a function of the mesh and the frequency, so an identical mesh at a 22 and a 40 GHz mesh frequency gives the
// fixture's L at 16 GHz bit for bit (src/Engine/Mom/RESOLVED.md §ICF-4 measured 578.53 against 578.51 pH before the rule).

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class LocalGridBaseTests(ITestOutputHelper output)
{
    private static PlanarMeshReport Mesh(PlanarProblem p, PlanarMeshSettings s, IReadOnlyList<PlanarPort>? ports = null) =>
        SurfaceMesher.Mesh(p, s, PlanarEdgeReference.LocalConductorWidth, ports: ports);

    /// <summary>Gate 1: the spiral meshes identically at a 22 and a 40 GHz mesh frequency — the same cells, the same bases.</summary>
    [Fact]
    public void TheSpiral_MeshesTheSame_At22And40GHz()
    {
        var coil = ThickSiliconCoilFixture.Coil(40e9);
        var a = Mesh(coil, ThickSiliconCoilFixture.Mesh(22e9), ThickSiliconCoilFixture.Ports()).Mesh;
        var b = Mesh(coil, ThickSiliconCoilFixture.Mesh(40e9), ThickSiliconCoilFixture.Ports()).Mesh;
        Assert.Equal(a.Bases.Count, b.Bases.Count);
        Assert.Equal(a.SolvedUnknownCount, b.SolvedUnknownCount);
        Assert.Equal(a.Cells.Select(c => (c.LayerIndex, c.XMin, c.YMin, c.XMax, c.YMax)),
                     b.Cells.Select(c => (c.LayerIndex, c.XMin, c.YMin, c.XMax, c.YMax)));
        Assert.True(SurfaceMesher.LocalGridStartsFromArtwork(coil, ThickSiliconCoilFixture.Mesh(40e9)));
    }

    /// <summary>The mesher's note on the spiral swept at 16 GHz alone, its mesh frequency stated at 22 GHz: it names 22 GHz as stated,
    /// not as the top of the sweep, and says the partition starts from the artwork, which is why neither frequency moves it. It used
    /// to call a stated frequency at or above the sweep's top "the top of the sweep", and say the sweep's top set the base block.</summary>
    [Fact]
    public void TheMeshNote_NamesTheFrequencyUsed_AndThatThePartitionStartsFromTheArtwork()
    {
        string Note(double fTop, double? meshHz) => Assert.Single(
            Mesh(ThickSiliconCoilFixture.Coil(fTop), ThickSiliconCoilFixture.Mesh(meshHz), ThickSiliconCoilFixture.Ports()).Notes,
            n => n.StartsWith("The local grid", StringComparison.Ordinal));
        string stated = Note(16e9, 22e9);
        output.WriteLine(stated);
        Assert.StartsWith("The local grid starts from one block across the artwork", stated, StringComparison.Ordinal);
        Assert.Contains("at 22 GHz, as Mesh frequency states", stated, StringComparison.Ordinal);
        Assert.DoesNotContain("sweep", stated, StringComparison.Ordinal);
        Assert.Contains("at 40 GHz, the top of the sweep", Note(40e9, null), StringComparison.Ordinal);
    }

    /// <summary>Gate 1's other half: a board is many base blocks across, so the rule does not reach its partition.</summary>
    [Fact]
    public void ABoard_IsNotReached()
    {
        var line = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        Assert.False(SurfaceMesher.LocalGridStartsFromArtwork(line, ThickSiliconCoilFixture.Mesh(null)));
        Assert.False(SurfaceMesher.LocalGridStartsFromArtwork(ThickSiliconCoilFixture.Coil(40e9), ThickSiliconCoilFixture.Mesh(40e9, localGrid: false)));
        Assert.False(PlanarLocalGrid.StartsFromArtwork(2.01, 1.0));
        Assert.True(PlanarLocalGrid.StartsFromArtwork(2.0, 1.0));
    }
}
