// ICF-5 — planar mesh defaults on small IC cells (R-icf5-1, R-icf5-2, R-icf5-4). Mesher only, no solve; the accuracy
// behind the rule's values is src/Engine/Mom/RESOLVED.md §ICF-5.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class IcMeshDefaultsTests
{
    private const double F = 40e9;

    private static readonly PlanarMeshSettings Product =
        PlanarMeshSettings.Default with { RooftopMerging = true, StackedRegions = true, LocalGrid = true };

    private static PlanarMeshReport Mesh(PlanarProblem p, PlanarMeshSettings s, IReadOnlyList<PlanarPort>? ports = null) =>
        SurfaceMesher.Mesh(p, s, PlanarEdgeReference.LocalConductorWidth, ports: ports);

    /// <summary>Gate 1: a board's artwork is many times λ_g/10 and its copper thinner than its traces — the rule does not
    /// apply, so nothing in the mesh is touched.</summary>
    [Fact]
    public void ABoard_IsNotUnderTheIcRule()
    {
        var line = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var r = Mesh(line, Product);
        Assert.DoesNotContain(r.Notes, n => n.StartsWith("IC rule", StringComparison.Ordinal));
        Assert.False(SurfaceMesher.IcRuleApplies(line, 20e-3, PlanarLineFixtures.Fr4HeroWidthM, line.GuidedWavelengthM,
                                                 PlanarLineFixtures.Fr4HeroWidthM, out _, out _));
    }

    /// <summary>Gate 2: the small cell under Auto meshes exactly as the IC values stated by hand, 1.76× fewer unknowns
    /// than the board defaults (5,559 → 3,152 when measured), and says so; a stated cells-across wins over the rule.</summary>
    [Fact]
    public void ASmallIcCell_TakesTheIcValuesUnderAuto_AndAStatedValueWins()
    {
        var cell = SmallIcCellFixture.Cell(F);
        var ports = SmallIcCellFixture.Ports();

        var auto = Mesh(cell, Product, ports);
        var ic = Mesh(cell, Product with
        {
            Auto = false, MinCellsAcrossConductor = SurfaceMesher.IcCellsAcross, EdgeCells = SurfaceMesher.IcEdgeCells,
        }, ports);
        var board = Mesh(cell, Product with { Auto = false }, ports);

        Assert.Equal(ic.Mesh.Bases.Count, auto.Mesh.Bases.Count);
        Assert.True(board.Mesh.Bases.Count > 1.7 * auto.Mesh.Bases.Count,
                    $"{board.Mesh.Bases.Count} board-default unknowns against {auto.Mesh.Bases.Count} under the IC rule");
        Assert.Contains(auto.Notes, n => n.StartsWith("IC rule", StringComparison.Ordinal) && n.Contains("2 cells across the narrowest conductor with 1 graded edge cell"));

        var stated = Mesh(cell, Product with { MinCellsAcrossConductor = 3 }, ports);
        Assert.DoesNotContain(stated.Notes, n => n.StartsWith("IC rule", StringComparison.Ordinal));
        Assert.Equal(Mesh(cell, Product with { Auto = false, MinCellsAcrossConductor = 3 }, ports).Mesh.Bases.Count,
                     stated.Mesh.Bases.Count);
    }

    /// <summary>Gate 3: on a 40 µm-wide cell the 2 % cap would hold the floor at 0.8 µm; a stated λ_g/1000 (2.2 µm) is
    /// applied, so the 1.5 µm stubs stop setting the pitch.</summary>
    [Fact]
    public void AStatedDetailDivisor_IsAppliedPastTheTwoPercentCap()
    {
        var cell = SmallIcCellFixture.Cell(F);
        var dflt = Mesh(cell, Product with { Auto = false });
        var stated = Mesh(cell, Product with { Auto = false, DetailFloorDivisor = 1000 });

        Assert.Contains(dflt.Notes, n => n.Contains("held at 2%", StringComparison.Ordinal));
        Assert.Contains(stated.Notes, n => n.StartsWith("Detail floor λ_g/1000 = ", StringComparison.Ordinal)
                                           && n.Contains("narrower than that"));
        Assert.True(stated.Mesh.Bases.Count < dflt.Mesh.Bases.Count);
    }
}
