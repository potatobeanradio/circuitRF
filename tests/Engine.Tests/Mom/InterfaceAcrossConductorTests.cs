using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// brief-psv-1b R-psv1b-0 — a dielectric interface that runs ACROSS a conductor (a cover above the
/// metal) keeps its refinement over the metal. <c>BoundaryMesher.PartitionFractions</c> rescaled every
/// breakpoint by length/last when the walk overshot the interval's end, which moved a grading anchored
/// INSIDE the interval to one side: ~170 µm at the default mesh on a ±2 mm interface, so a 1 µm cover
/// over a 20 µm line read ε_eff anywhere from 7.9 to 9.9 as MinCellsAcrossWidth went 6 → 24.
/// </summary>
public class InterfaceAcrossConductorTests
{
    private const double Um = 1e-6;

    /// <summary>20 × 3 µm metal on 100 µm εᵣ 12.9 over ground, under a 1 µm εᵣ 7 cover that fills the
    /// metal's band (the absorbed rule): interfaces at 100 µm (cut by the metal) and 104 µm (across it).</summary>
    private static EmProblem CoveredLine() => new(
        [new EmConductor("SIG", [new(-10 * Um, 100 * Um), new(10 * Um, 100 * Um), new(10 * Um, 103 * Um), new(-10 * Um, 103 * Um)], 4.1e7)],
        [
            new EmDielectricRegion(double.NegativeInfinity, 100 * Um, new EmMaterial(12.9)),
            new EmDielectricRegion(100 * Um, 104 * Um, new EmMaterial(7.0)),
            new EmDielectricRegion(104 * Um, double.PositiveInfinity, EmMaterial.Air),
        ],
        new EmGroundPlane(0, 4.1e7),
        [new EmPort(1, "SIG", null, 50), new EmPort(2, "SIG", null, 50)],
        1e-3);

    private static double Eeff(EmProblem p, EmMeshSettings s)
    {
        var mesh = BoundaryMesher.Mesh(p, s).Mesh;
        return ChargeSolver.MaxwellCapacitance(mesh)[0, 0].Real /
               ChargeSolver.MaxwellCapacitance(ChargeSolver.AirFilled(mesh))[0, 0].Real;
    }

    /// <summary>The interface above the metal has its finest cell over the metal's edge, not hundreds of
    /// µm to one side of it.</summary>
    [Fact]
    public void TheCoverInterface_IsRefinedAtTheMetalsEdges()
    {
        var segs = BoundaryMesher.Mesh(CoveredLine(), EmMeshSettings.Default).Mesh.Segments
            .Where(s => s.Kind == EmSegmentKind.DielectricInterface && Math.Abs(s.A.Y - 104 * Um) < 1e-12)
            .ToList();
        var finest = segs.MinBy(s => s.Length)!;
        Assert.InRange(Math.Abs(finest.Mid.X), 9 * Um, 11 * Um);
    }

    /// <summary>The covered line's ε_eff is a property of the line, not of the mesh setting: within 0.5 %
    /// across MinCellsAcrossWidth 6 … 24. Measured 7.965 … 7.981 (it spread 7.92 … 9.88 before).</summary>
    [Fact]
    public void ACoveredLine_DoesNotDependOnTheMesh()
    {
        var values = new[] { 6, 8, 12, 16, 24 }
            .Select(mc => Eeff(CoveredLine(), EmMeshSettings.Default with { MinCellsAcrossWidth = mc }))
            .ToList();
        Assert.True(values.Max() / values.Min() - 1 < 0.005, string.Join(", ", values.Select(v => v.ToString("F4"))));
    }
}
