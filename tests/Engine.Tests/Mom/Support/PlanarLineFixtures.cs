// L8d — the shared geometry every port/de-embedding test is built on.
//
// TWO TIERS OF FIXTURE, ON PURPOSE. The brief's tagging rule says the routine gate stays under ~20 s
// of new tests, and the way to get there is to test the ALGEBRA on a deliberately coarse mesh: the
// port operator, the T-matrix cascade, the branch resolutions and the self-consistency identities
// are all exact regardless of mesh quality, and a coarse mesh tests them just as hard. Only the
// measurements that need a physically converged answer — A vs B, the feed-length study, the stub —
// use the shipping mesh, and those are Category=Benchmark.
//
//   Coarse(...)   CellsPerWavelength 10, edge mesh OFF   →  N ≈ 90 on the FR-4 hero, ~10 ms a fill
//   Shipping(...) the mesher's own defaults              →  N = 552 there, ~1.5 s a fill

using System.Collections.Concurrent;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

public static class PlanarLineFixtures
{
    // Dcim.Fit costs ~0.2 s per kernel per frequency regardless of N (L8c's Tier 8), and it depends
    // on (slab, frequency) ALONE — which is exactly why PlanarSolve shares one fit across the DUT
    // and both calibration standards. The same fact makes it cacheable across tests. The cache is a
    // pure memo of a deterministic function of an immutable key, so it is safe under xUnit's
    // cross-class parallelism in the way a mutable static would not be.
    private static readonly ConcurrentDictionary<(double H, double Eps, double Tan, double F),
                                                 PlanarKernelPair> Kernels = new();

    public static PlanarKernelPair Kernel(GroundedSlab slab, double fHz) =>
        Kernels.GetOrAdd((slab.HeightM, slab.Material.EpsR, slab.Material.TanD, fHz),
                         k => PlanarKernelPair.Fit(new GroundedSlab(k.H, new EmMaterial(k.Eps, k.Tan)), k.F));

    /// <summary>Cheap enough that a de-embedded solve (DUT + two standards) is milliseconds.</summary>
    public static readonly PlanarMeshSettings Coarse =
        new(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);

    public static readonly PlanarMeshSettings Shipping = PlanarMeshSettings.Default;

    public static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    public static PlanarProblem Problem(GroundedSlab slab, double fHz, params PlanarPolygon[] polys) =>
        new([new PlanarConductorLayer("Metal", polys, 5.8e7, 35e-6)], slab, fHz);

    /// <summary>A bare x-directed line: origin at (0,0), width centred on y = 0.</summary>
    public static PlanarProblem Line(GroundedSlab slab, double widthM, double lengthM, double fHz) =>
        Problem(slab, fHz, Rect(0, -0.5 * widthM, lengthM, 0.5 * widthM));

    /// <summary>§10.7's own hero cross-section: 50 Ω on 1.6 mm FR-4 is W ≈ 2.9 mm.</summary>
    public const double Fr4HeroWidthM = 2.9e-3;

    /// <summary>The MMIC counterpart: 72 µm on 100 µm GaAs.</summary>
    public const double GaAsHeroWidthM = 72e-6;

    /// <summary>
    /// A line of a stated number of GUIDED WAVELENGTHS, not of a stated physical length.
    ///
    /// <para><b>Measured, and it is why this exists.</b> Both γ routes are conditioned on ELECTRICAL
    /// length: the travelling-wave recurrence extracts acosh(w) with w − 1 ≈ (γΔz)²/2, and the
    /// two-line trace extracts acosh of a quantity whose distance from 1 scales the same way with
    /// γΔℓ. A fixture of fixed physical length is therefore well conditioned at the top of a band
    /// and badly conditioned at the bottom — on the 20 mm FR-4 line at 2 GHz the wave oracle read
    /// β = 36.6 against a true ≈ 78. Scaling the fixture electrically keeps βΔz constant across the
    /// band and costs nothing, because the MESH is frequency-scaled too: a 1.5 λ_g line is about the
    /// same N at 1 GHz as at 20 GHz.</para>
    /// </summary>
    public static PlanarProblem LineOfWavelengths(GroundedSlab slab, double widthM,
                                                  double lambdas, double fHz)
    {
        double epsEst = 0.5 * (slab.Material.EpsR + 1.0);
        double lambda = EmConstants.C0 / (fHz * Math.Sqrt(epsEst));
        return Line(slab, widthM, lambdas * lambda, fHz);
    }

    public static PlanarProblem Fr4Line(double lengthM, double fHz) =>
        Line(GroundedSlab.Fr4Starter, Fr4HeroWidthM, lengthM, fHz);

    public static PlanarProblem GaAsLine(double lengthM, double fHz) =>
        Line(GroundedSlab.GaAsStarter, GaAsHeroWidthM, lengthM, fHz);

    /// <summary>The two ports of an x-directed line, one at each end, at the stated impedance.</summary>
    public static PlanarPort[] EndPorts(PlanarProblem problem, double z0 = 50.0)
    {
        var (x0, y0, x1, y1) = problem.Bounds();
        double yc = 0.5 * (y0 + y1);
        return
        [
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, z0),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, z0),
        ];
    }

    /// <summary>Mesh + resolved ports in one step — the thing nearly every test starts with.</summary>
    public static (PlanarMesh Mesh, IReadOnlyList<PlanarPortResolution> Ports) MeshAndPorts(
        PlanarProblem problem, PlanarMeshSettings? settings = null, double z0 = 50.0)
    {
        var report = SurfaceMesher.Mesh(problem, settings ?? Coarse);
        var ports  = PlanarPorts.ResolveAll(report.Mesh, EndPorts(problem, z0));
        return (report.Mesh, ports);
    }
}
