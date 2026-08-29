// brief-em-p8-aim-near-radius-floor.md — the near field has to span the image depth, and a radius
// measured in basis supports does not.
//
// The pathology, in one line: `PlanarAimSettings.NearRadiusFactor` is 6 LARGEST BASIS SUPPORTS, so
// refining the mesh at a fixed footprint shrinks the near radius IN METRES. On the FR-4 hero
// cross-section it is 8.9h at the shipping cells/λ = 20 and 1.28h at cells/λ = 140 — and the scalar
// kernel over a grounded slab, 1/ρ − 1/√(ρ² + 4h²), only stops being long-ranged past ρ ≈ 2h. A
// preconditioner narrower than the image depth is missing the dominant coupling, which is what made
// brief-em-aim-ceiling.md's A1b ladder climb 21 → 143 → 372 GMRES iterations and then fail outright.
//
// P8's fix is one `Math.Max`: PlanarAimGeometry.NearRadiusM = max(6·maxSpan, 2h). Every routine test
// here is structural — a radius, a near set, a refusal — or an ITERATION COUNT, which is a counter
// and not a wall-clock measurement. The ladder that measures the effect end to end is the one
// Benchmark method at the bottom.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarP8NearRadiusFloorTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static readonly GroundedSlab Slab = GroundedSlab.Fr4Starter;

    /// <summary>Pre-P8 behaviour, which several of these tests need as the control.</summary>
    private static readonly PlanarAimSettings NoFloor = PlanarAimSettings.Default with { NearRadiusMinM = 0 };

    private static PlanarMeshSettings At(int cellsPerWavelength) =>
        new(Auto: false, CellsPerWavelength: cellsPerWavelength, EdgeMesh: true, EdgeCells: 3,
            BoundaryCells: PlanarBoundaryCells.Staircase);

    private static (PlanarMesh Mesh, PlanarFillCores Cores, IReadOnlyList<PlanarPortResolution> Ports)
        Fixture(double lengthMm, int cellsPerWavelength, double fHz = 6e9)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthMm * 1e-3, fHz);
        var report  = SurfaceMesher.Mesh(problem, At(cellsPerWavelength));
        var ports   = PlanarPorts.ResolveAll(report.Mesh, PlanarLineFixtures.EndPorts(problem));
        return (report.Mesh, PlanarFill.BuildGeometryOnlyCores(report.Mesh), ports);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P8_1 — the floor is 2h, it is derived, and the report says which of the two set the radius
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P8_1_TheFloorIsTwoImageDepths_AndTheReportSaysWhichBound()
    {
        // The shipping mesh is nowhere near it — the whole point is that the floor is inert on the
        // meshes anyone actually runs, and only binds where the support-relative radius has collapsed.
        var (_, coarse, _) = Fixture(16, 20);
        var g20 = PlanarAimGeometry.Build(coarse, Slab.HeightM);

        Assert.Equal(2.0 * Slab.HeightM, g20.NearRadiusFloorM, 15);
        Assert.Equal(PlanarAimSettings.DerivedNearRadiusImageDepths * Slab.HeightM, g20.NearRadiusFloorM, 15);
        Assert.False(g20.NearRadiusIsFloored);
        Assert.Equal(g20.NearRadiusFromSupportM, g20.NearRadiusM, 15);
        Assert.True(g20.NearRadiusM / Slab.HeightM > 8,
            $"the shipping mesh's near radius is {g20.NearRadiusM / Slab.HeightM:F2} h, which is not the " +
            "regime P8 exists for; the fixture has drifted");

        // …and the refined one is, by construction: 6 supports there is barely over one image depth.
        var (_, fine, _) = Fixture(16, 140);
        var g140 = PlanarAimGeometry.Build(fine, Slab.HeightM);

        Assert.True(g140.NearRadiusIsFloored);
        Assert.Equal(g140.NearRadiusFloorM, g140.NearRadiusM, 15);
        Assert.True(g140.NearRadiusFromSupportM < 1.5 * Slab.HeightM,
            $"unfloored the radius is {g140.NearRadiusFromSupportM / Slab.HeightM:F2} h");

        // The MMIC starter is the other technology this could have surprised, and it is not close:
        // a 72 µm conductor on a 100 µm slab has cells that are large against h by construction, so
        // its near radius is tens of image depths even at 60 GHz. Asserted rather than assumed,
        // because "P8 changes no answer a user already has" rests on it.
        var gaAs = GroundedSlab.GaAsStarter;
        var mmic = PlanarLineFixtures.Line(gaAs, 72e-6, 1e-3, 60e9);
        var mmicMesh = SurfaceMesher.Mesh(mmic, At(40)).Mesh;
        var gm = PlanarAimGeometry.Build(PlanarFill.BuildGeometryOnlyCores(mmicMesh), gaAs.HeightM);
        Assert.False(gm.NearRadiusIsFloored);
        Assert.True(gm.NearRadiusM / gaAs.HeightM > 4);

        _out.WriteLine($"cells/λ = 20:  {g20.NearRadiusM / Slab.HeightM:F2} h from the supports, floor " +
                       $"{g20.NearRadiusFloorM / Slab.HeightM:F2} h — not floored");
        _out.WriteLine($"GaAs, cells/λ = 40 at 60 GHz: {gm.NearRadiusM / gaAs.HeightM:F2} h — not floored");
        _out.WriteLine($"cells/λ = 140: {g140.NearRadiusFromSupportM / Slab.HeightM:F2} h from the supports, " +
                       $"floor {g140.NearRadiusFloorM / Slab.HeightM:F2} h — FLOORED");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P8_2 — the brief's own cost gate: on the LENGTH ladder the floor must change NOTHING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void P8_2_OnTheLengthLadderTheNearSetIsIdentical(double lengthMm)
    {
        // Growing the geometry at fixed resolution never shrinks the largest support, so the radius
        // stays at 8.9h and the floor is unreachable. "Unreachable" has to be asserted rather than
        // argued: the near set is what the whole cost of P8 would be paid in, and this says the
        // meshes users actually run pay none of it.
        var (mesh, cores, ports) = Fixture(lengthMm, 20);

        var floored = PlanarAimGeometry.Build(cores, Slab.HeightM);
        var pre     = PlanarAimGeometry.Build(cores, Slab.HeightM, NoFloor);

        Assert.False(floored.NearRadiusIsFloored);
        Assert.Equal(pre.NearRadiusM, floored.NearRadiusM, 15);
        Assert.Equal(pre.NearEntries, floored.NearEntries);

        // Pair by pair, not merely by count — two near sets of the same size can still differ.
        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (pre.IsNear(i, j) != floored.IsNear(i, j))
                    Assert.Fail($"near set differs at ({i},{j}) on the {lengthMm:F0} mm rung");

        // …and the operator built over it is bit-identical, which is the claim a user cares about.
        var k = PlanarLineFixtures.Kernel(Slab, 6e9);
        double omega = 2 * Math.PI * 6e9;
        var a = PlanarAimOperator.Build(floored, k.VectorPotential, k.Scalar, omega);
        var b = PlanarAimOperator.Build(pre,     k.VectorPotential, k.Scalar, omega);
        var rhs = PlanarExcitation.RightHandSide(n, ports[0]);
        var ya = a.Solve(rhs);
        var yb = b.Solve(rhs);
        Assert.Equal(b.LastIterations, a.LastIterations);
        for (int i = 0; i < n; i++) Assert.Equal(yb[i], ya[i]);

        _out.WriteLine($"{lengthMm:F0} mm, N = {n}: near entries {floored.NearEntries:N0} either way, " +
                       $"{a.LastIterations} GMRES iterations, solution bit-identical");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P8_3 — and on a REFINED mesh it is the whole difference between converging and not
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P8_3_OnARefinedMeshTheFloorCollapsesTheIterationCount()
    {
        // The cheapest rung of the ladder that still shows the effect — deliberately small, because
        // the number being asserted is a COUNTER (GMRES iterations) and not a time, and a counter
        // does not need a big problem to be meaningful. The Benchmark ladder below runs the rest.
        var (mesh, cores, ports) = Fixture(6, 120);
        int n = mesh.Bases.Count;
        var k = PlanarLineFixtures.Kernel(Slab, 6e9);
        double omega = 2 * Math.PI * 6e9;
        var rhs = PlanarExcitation.RightHandSide(n, ports[0]);

        var pre  = PlanarAimOperator.Build(PlanarAimGeometry.Build(cores, Slab.HeightM, NoFloor),
                                           k.VectorPotential, k.Scalar, omega);
        var post = PlanarAimOperator.Build(PlanarAimGeometry.Build(cores, Slab.HeightM),
                                           k.VectorPotential, k.Scalar, omega);
        pre.Solve(rhs);
        post.Solve(rhs);

        _out.WriteLine($"N = {n}: {pre.LastIterations} GMRES iterations at " +
                       $"{pre.Geometry.NearRadiusM / Slab.HeightM:F2} h, {post.LastIterations} at " +
                       $"{post.Geometry.NearRadiusM / Slab.HeightM:F2} h");

        Assert.True(post.LastIterations * 2 < pre.LastIterations,
            $"the floor was supposed to be the difference here, and GMRES took {pre.LastIterations} " +
            $"iterations without it against {post.LastIterations} with it");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P8_4 — the slab height is REQUIRED, because forgetting it is silent
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P8_4_TheSlabHeightIsRefusedRatherThanDefaulted()
    {
        var (mesh, cores, ports) = Fixture(8, 20);

        var g = Assert.Throws<ArgumentOutOfRangeException>(() => PlanarAimGeometry.Build(cores, 0));
        Assert.Contains("NearRadiusMinM", g.Message, StringComparison.Ordinal);

        var accel = PlanarFillSettings.Default with { Aim = PlanarAimSettings.Default };
        var c = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PlanarSolveContext(mesh, ports, accel));
        Assert.Contains("2h", c.Message, StringComparison.Ordinal);

        // …and the DENSE path is unaffected, which is why the parameter is optional rather than
        // positional: nothing on that path has a near radius to floor.
        _ = new PlanarSolveContext(mesh, ports);

        // A floor of 0 is the documented opt-out and must be accepted; a negative one is not.
        _ = PlanarAimGeometry.Build(cores, Slab.HeightM, NoFloor);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (PlanarAimSettings.Default with { NearRadiusMinM = -1 }).Validate());
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P8_5 — the ladder itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void P8_5_TheResolutionLadderIsFlatWithTheFloor()
    {
        // brief-em-aim-ceiling.md's A1b construction — fixed footprint, cells/λ swept — at 16 mm
        // rather than its own 64 mm. The pathology is set by the CELL SIZE against h and not by the
        // footprint, so the short board reproduces it faithfully (iterations 2, 4, 6, 12, 46, 144,
        // 273 without the floor) for a small fraction of the 64 mm ladder's cost, and its top rung
        // stays under the DENSE ceiling so |ΔI| has a reference to be measured against.
        _out.WriteLine("");
        _out.WriteLine("P8 — RESOLUTION ladder, FR-4 hero cross-section at 6 GHz, FIXED 16 mm length");
        _out.WriteLine("");
        _out.WriteLine("  c/λ       N   R/h off   R/h on   near/row off   on   iters off   on");

        foreach (int cpl in new[] { 20, 40, 60, 80, 100, 120, 140 })
        {
            var (mesh, cores, ports) = Fixture(16, cpl);
            int n = mesh.Bases.Count;
            var k = PlanarLineFixtures.Kernel(Slab, 6e9);
            double omega = 2 * Math.PI * 6e9;
            var rhs = PlanarExcitation.RightHandSide(n, ports[0]);

            var row = new (double RH, double PerRow, int It)[2];
            for (int m = 0; m < 2; m++)
            {
                var aim = PlanarAimOperator.Build(
                    PlanarAimGeometry.Build(cores, Slab.HeightM, m == 0 ? NoFloor : PlanarAimSettings.Default),
                    k.VectorPotential, k.Scalar, omega);
                try { aim.Solve(rhs); } catch (InvalidOperationException) { /* reported, not thrown */ }
                row[m] = (aim.Report.NearRadiusM / Slab.HeightM, aim.Report.NearEntriesPerRow,
                          aim.LastIterations);
            }

            _out.WriteLine($"  {cpl,3} {n,7}   {row[0].RH,7:F2}  {row[1].RH,7:F2}   {row[0].PerRow,12:F0} " +
                           $"{row[1].PerRow,4:F0}   {row[0].It,9} {row[1].It,5}");

            // The gate: floored, the iteration count never leaves the flat band the length ladder
            // holds. Unfloored it is 273 at the top rung, so this is not a loose bound.
            Assert.InRange(row[1].It, 1, 20);
        }
    }
}
