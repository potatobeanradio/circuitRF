// Owner report, 2026-08-09: "I set my Edge cells to 10 and expected the mesh on the Klopf to increase
// near the edges, but the mesh appeared to be the same."
//
// It does not do that, and cannot: the FINEST edge cell is `EdgeFractionOfReference` (3%) of the edge
// reference length — a constant, not a user control. `EdgeCells` sets the derived GROWTH RATIO, i.e.
// how far the refinement reaches inward before it meets the bulk cell size. Raising it widens the fine
// band; it never sharpens the edge itself.
//
// On top of that the ratio is CLAMPED to [MinGrowthRatio, MaxGrowthRatio], so past a geometry-dependent
// point the requested count cannot be honoured and the setting is inert. That was silent. These tests
// pin both the arithmetic and the report.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class SurfaceMesherEdgeCellsTests
{
    [Fact]
    public void EffectiveEdgeCells_MatchesTheRequest_WhileTheClampIsNotBinding()
    {
        // ratio = (hMax/c0)^(1/n) exactly, for an n whose ratio lands inside the bounds.
        const double c0 = 1e-5, h = 2.44e-4;   // ratio at n=5 is ~1.89 — comfortably interior
        double ratio = Math.Pow(h / c0, 1.0 / 5);
        Assert.InRange(ratio, SurfaceMesher.MinGrowthRatio, SurfaceMesher.MaxGrowthRatio);

        Assert.Equal(5, SurfaceMesher.EffectiveEdgeCells(c0, h, ratio));
    }

    [Theory]
    [InlineData(1)]    // ideal ratio far above the ceiling
    [InlineData(2)]
    [InlineData(40)]   // ideal ratio far below the floor
    [InlineData(200)]
    public void WhenTheClampBinds_TheEffectiveCountDiffersFromTheRequest(int requested)
    {
        const double c0 = 1e-5, h = 2.44e-4;
        double ideal = Math.Pow(h / c0, 1.0 / requested);
        double ratio = Math.Clamp(ideal, SurfaceMesher.MinGrowthRatio, SurfaceMesher.MaxGrowthRatio);
        Assert.NotEqual(ideal, ratio, precision: 6);   // the fixture really is outside the bounds

        int used = SurfaceMesher.EffectiveEdgeCells(c0, h, ratio);
        Assert.NotEqual(requested, used);
        Assert.InRange(used, 1, 200);
    }

    [Fact]
    public void NothingToGrade_ReportsZero_RatherThanDividingByALogOfOne()
    {
        Assert.Equal(0, SurfaceMesher.EffectiveEdgeCells(0, 1e-4, 1.5));      // no edge cell
        Assert.Equal(0, SurfaceMesher.EffectiveEdgeCells(1e-4, 1e-5, 1.5));   // bulk finer than the edge
        Assert.Equal(0, SurfaceMesher.EffectiveEdgeCells(1e-5, 1e-4, 1.0));   // a ratio of 1 never climbs
    }

    /// <summary>
    /// The whole point of the report: a user who types a number the geometry cannot honour is TOLD,
    /// rather than left comparing two identical meshes. Driven through the real mesher.
    /// </summary>
    [Fact]
    public void AnUnhonourableEdgeCellCount_IsReportedInTheMeshNotes()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);

        var high = new PlanarKernel().Mesh(problem,
            new PlanarMeshSettings(Auto: false, EdgeMesh: true, EdgeCells: 200));

        Assert.Contains(high.Notes, n => n.Contains("not the 200 requested", StringComparison.Ordinal));

        // And it says which way, and what the control actually governs — a bare "ignored" would leave
        // the user no better off than the silence did.
        string note = high.Notes.First(n => n.Contains("not the 200 requested", StringComparison.Ordinal));
        Assert.Contains("meshes the same", note, StringComparison.Ordinal);
        Assert.Contains("never how fine", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHonourableCount_AddsNoSuchNote()
    {
        // The complement: the report must not fire on an ordinary setting, or it becomes noise and
        // stops being read at the moment it matters.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var report = new PlanarKernel().Mesh(problem,
            new PlanarMeshSettings(Auto: false, EdgeMesh: true, EdgeCells: PlanarMeshSettings.DefaultEdgeCells));

        Assert.DoesNotContain(report.Notes, n => n.Contains("requested", StringComparison.Ordinal));
    }

    /// <summary>
    /// The finding behind the report, stated as a test so it cannot quietly stop being true: raising
    /// EdgeCells does NOT sharpen the finest cell — it widens the graded band. If a future change
    /// makes this fail, the control's MEANING has changed, and its tooltip, its note and this file
    /// all need revisiting together.
    ///
    /// <para>The finest cell is not bit-identical across the two — it moves by about a percent,
    /// because <c>MinCellEdgeM</c> is the REALISED smallest cell after the per-span partition is
    /// rescaled to a whole number of cells, not <c>c₀</c> itself. What matters is that it does not
    /// SCALE with the setting: a control that genuinely refined the edge would have moved it by
    /// roughly the ratio of the two counts.</para>
    /// </summary>
    [Fact]
    public void RaisingEdgeCells_DoesNotSharpenTheFinestCell_ItWidensTheGradedBand()
    {
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);

        var few = new PlanarKernel().Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeMesh: true, EdgeCells: 3));
        var many = new PlanarKernel().Mesh(problem, new PlanarMeshSettings(Auto: false, EdgeMesh: true, EdgeCells: 10));

        double ratio = few.MinCellEdgeM / many.MinCellEdgeM;
        Assert.InRange(ratio, 0.9, 1.1);

        // The control it is NOT: 3 → 10 would have taken the finest cell to roughly a third of its
        // size if EdgeCells governed fineness. It is nowhere near that.
        Assert.True(ratio > 0.5, $"the finest cell scaled with EdgeCells (ratio {ratio:G4}) — the " +
                                 "control's meaning has changed; revisit its tooltip and its note");
    }
}
