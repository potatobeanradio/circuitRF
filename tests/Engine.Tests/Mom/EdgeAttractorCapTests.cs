// "LONG ENOUGH TO CROWD" — the second clause, and the property the first one has to keep.
//
// Reported 2026-08-11 on the MKlopf taper: the 12 Ω end cap got a graded fan and the 50 Ω end cap —
// where the crowding is strongest and where the port sits — got none, because the test was
// "at least a fifth of the POLYGON's extent" and the two ends of that part differ by ~7×. The extent
// of the other end decided how the near one was meshed.
//
// The second clause is the geometric statement the first was reaching for: an edge that TERMINATES
// the conductor has both its corners convex; an edge that is part of a longer boundary chain does
// not. This file gates both halves — that a real cap now qualifies, and that a drawn staircase still
// does not, which is the whole reason the first clause exists.
//
// Everything here is meshing. No Green's function, no fill, no solve; the file runs in milliseconds.

using CircuitRF.Engine.Mom;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class EdgeAttractorCapTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static PlanarProblem Of(IEnumerable<EmPoint> ring)
        => new([new PlanarConductorLayer("Metal", [new PlanarPolygon([.. ring])], 5.8e7, 35e-6)],
               GroundedSlab.Fr4Starter, 10e9);

    /// <summary>
    /// A narrow line running into a wide block — MKlopf's two-scale problem, Manhattan and minimal.
    /// The narrow end cap is <paramref name="narrowM"/> tall against a 10 mm total y-extent, so it
    /// fails the fifth-of-the-extent clause outright and can only qualify as a cap.
    /// </summary>
    private static PlanarProblem SteppedLine(double narrowM) => Of(
    [
        new(0, 0), new(10e-3, 0), new(10e-3, -4.5e-3), new(20e-3, -4.5e-3),
        new(20e-3, 5.5e-3), new(10e-3, 5.5e-3), new(10e-3, narrowM), new(0, narrowM),
    ]);

    /// <summary>A drawn STAIRCASE: metal below a rising flight of <paramref name="steps"/> treads,
    /// every edge of it axis-parallel. This is the artwork the first clause exists to refuse.</summary>
    private static PlanarProblem Staircase(int steps)
    {
        var ring = new List<EmPoint> { new(0, 0), new(10e-3, 0), new(10e-3, 5e-3) };
        for (int k = 1; k <= steps; k++)
        {
            double x = 10e-3 * (steps - k) / steps;
            double y = 0.5e-3 + 4.5e-3 * (steps - k) / (double)steps;
            ring.Add(new EmPoint(x, ring[^1].Y));    // tread, leftward
            ring.Add(new EmPoint(x, y));             // riser, downward
        }
        return Of(ring);
    }

    private static PlanarMeshSettings Graded =>
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: true, EdgeCells: 3);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The fix: a real end cap qualifies however small the rest of the part makes it look
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ANarrowEndCap_OnAPartWhoseOtherEndIsWIDE_StillEarnsItsFan()
    {
        var problem = SteppedLine(1e-3);
        var (attX, attY) = SurfaceMesher.EdgeAttractors(problem);

        _out.WriteLine($"attractors X = [{string.Join(", ", attX.Select(v => $"{v * 1e3:F3}"))}] mm, " +
                       $"Y = [{string.Join(", ", attY.Select(v => $"{v * 1e3:F3}"))}] mm");

        // The narrow cap is 1 mm against a 10 mm extent — a tenth, so the fifth-of-the-extent clause
        // refuses it and only the cap clause can put it here.
        Assert.Contains(attX, v => Math.Abs(v) < 1e-12);
        Assert.Contains(attX, v => Math.Abs(v - 20e-3) < 1e-12);

        // …and the grid actually responds: the first cell in from x = 0 is graded, not bulk.
        var g = SurfaceMesher.Mesh(problem, Graded).Mesh.GridX;
        double first = g[1] - g[0], bulk = g[^1] - g[^2];
        _out.WriteLine($"first x-cell {first * 1e6:F1} µm; a cell at the far end {bulk * 1e6:F1} µm");
        Assert.True(first < 0.5 * (g[4] - g[3]),
            $"the cap earned an attractor but the grid did not grade: first cell {first * 1e6:F1} µm");
    }

    /// <summary>
    /// <b>The floor, and it is derived rather than picked.</b> R17 caps this kernel at ~5,000
    /// unknowns — about a 50 × 50 grid — so one cell is ~2% of the extent per axis at the finest mesh
    /// it can afford. An edge below that is sub-cell however the mesh is refined, and grading it would
    /// spend gridlines across the whole tensor grid on something the mesh cannot represent.
    /// </summary>
    [Fact]
    public void ACapFinerThanOneCellAtTheCEILING_DoesNotEarnAFan()
    {
        // 0.1 mm against a 10 mm extent is 1%, half the floor.
        var (attX, _) = SurfaceMesher.EdgeAttractors(SteppedLine(0.1e-3));
        Assert.DoesNotContain(attX, v => Math.Abs(v) < 1e-12);

        // …and the neighbouring rung, 0.3 mm = 3%, does — so the assertion above is about the floor
        // and not about the cap clause having quietly stopped working.
        var (attX2, _) = SurfaceMesher.EdgeAttractors(SteppedLine(0.3e-3));
        Assert.Contains(attX2, v => Math.Abs(v) < 1e-12);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The property the first clause exists for, and the new one must not break it
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A staircase still gets no fan per tread</b>, and it is asserted the way D9's own guarantee
    /// is — on the COUNT, held invariant as the artwork is refined. A count that grew with the step
    /// count would be the failure the first clause was written against, arriving through the second.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(40)]
    public void ADrawnStaircase_ContributesNoFanPerTread(int steps)
    {
        var problem = Staircase(steps);
        var (attX, attY) = SurfaceMesher.EdgeAttractors(problem);

        int axisParallel = problem.Layers[0].Polygons[0].Outer.Count;   // every edge of it is one
        _out.WriteLine($"{steps} steps ({axisParallel} axis-parallel edges): " +
                       $"{attX.Count} x-attractor(s), {attY.Count} y-attractor(s)");

        // Two genuine terminations exist (the block's own far edges), and nothing else may qualify.
        Assert.True(attX.Count <= 2, $"{attX.Count} x-attractors from a {steps}-step staircase");
        Assert.True(attY.Count <= 2, $"{attY.Count} y-attractors from a {steps}-step staircase");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Nothing that already graded may stop, and §10.7's own hero must not move
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheHeroIsStillExactlyN552_AndAllFourOfItsEdgesStillGrade()
    {
        var hero = Of([new(0, 0), new(20e-3, 0), new(20e-3, 2.9e-3), new(0, 2.9e-3)]);

        // All four already passed the fifth-of-the-extent clause (2.9 ≥ 0.58 and 20 ≥ 4), so the cap
        // clause can only re-qualify them — never add a fifth attractor, and never a duplicate.
        var (attX, attY) = SurfaceMesher.EdgeAttractors(hero);
        Assert.Equal(2, attX.Count);
        Assert.Equal(2, attY.Count);

        Assert.Equal(552, SurfaceMesher.Mesh(hero, PlanarMeshSettings.Default).UnknownCount);
    }
}
