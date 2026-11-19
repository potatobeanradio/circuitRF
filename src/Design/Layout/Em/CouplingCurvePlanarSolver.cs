using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// Planar MoM behind <see cref="ICouplingCurveSolver"/> (docs/design/filter-synthesis.md §6b): the plan drawn as its PCell
/// draws it, the copper unioned into one sheet on the substrate's grounded slab, one grounded edge port on each pin facing
/// out, and the sweep solved without de-embedding (a bench is read for its resonances, which the ports' own steps barely move).
/// A realised filter's run (§6d) is the same solve on ports of its own.
/// </summary>
/// <param name="Mesh">The mesh; null is a coarse one, ten cells a wavelength and no edge cells.</param>
/// <param name="Threads">The solve's parallelism.</param>
public sealed record CouplingCurvePlanarSolver(PlanarMeshSettings? Mesh = null, int Threads = 8) : ICouplingCurveSolver
{
    public static readonly PlanarMeshSettings Coarse = new(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);

    /// <summary>Metres per layout database unit.</summary>
    internal const double MetresPerDbu = 1e-6 / LayoutUnits.DefaultDbuPerMicron;

    public Complex[][,] Solve(PassivePlan plan, MicrostripSubstrate s, IReadOnlyList<double> fHz)
    {
        var cell = PassivePlanPCells.Draw(plan, null, PCellLayerSelection.Default);
        return Solve(cell.Shapes, [.. cell.Pins.Select((p, i) => EdgePort(i + 1, p, 50.0, grounded: true))], s, fHz);
    }

    /// <summary>An edge port on <paramref name="pin"/>, on the side it faces out of. <paramref name="grounded"/> makes it
    /// an Uncalibrated Port, whose source returns through the ground plane: without de-embedding, the default series gap
    /// leaves the outermost cell's capacitance in series with the port.</summary>
    internal static PlanarPort EdgePort(int number, PCellPin pin, double z0, bool grounded = false)
        => new(number, new EmPoint(pin.X * MetresPerDbu, pin.Y * MetresPerDbu),
            ((int)Math.Round(pin.OutwardDirectionDeg / 90.0) & 3) switch
            {
                0 => PlanarPortSide.MaxX, 1 => PlanarPortSide.MaxY, 2 => PlanarPortSide.MinX, _ => PlanarPortSide.MinY,
            }, z0) { Uncalibrated = grounded };

    /// <summary><paramref name="shapes"/> unioned into one sheet on the substrate's grounded slab, a via grown under each
    /// internal port that names a width (<see cref="PlanarGroundPath"/>), and the sweep solved at
    /// <paramref name="ports"/> without de-embedding: S at each frequency, in port order.</summary>
    internal Complex[][,] Solve(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<PlanarPort> ports, MicrostripSubstrate s,
        IReadOnlyList<double> fHz, RunControl? control = null)
    {
        IReadOnlyList<EmPoint> Ring(long[] xy)
            => [.. Enumerable.Range(0, xy.Length / 2).Select(i => new EmPoint(xy[2 * i] * MetresPerDbu, xy[2 * i + 1] * MetresPerDbu))];
        var copper = LayoutBooleans.Union(shapes, null).Shapes
            .Select(shape => LayoutFlattener.Flatten(shape, 1))
            .Where(rings => rings.Count > 0)
            .Select(rings => new PlanarPolygon(Ring(rings[0]), rings.Count > 1 ? [.. rings.Skip(1).Select(Ring)] : null));
        var problem = PlanarGroundPath.Extend(new PlanarProblem([new PlanarConductorLayer("Metal", [.. copper], s.SigmaSPerM, s.TMeters)],
            new GroundedSlab(s.HMeters, new EmMaterial(s.EpsR, s.TanD)), fHz.Max()), ports).Problem;
        var mesh = SurfaceMesher.Mesh(problem, Mesh ?? Coarse).Mesh;
        var run = PlanarSolve.Run(problem, mesh, PlanarPorts.ResolveAll(mesh, ports), fHz,
            new PlanarSolveSettings(Deembed: false, MaxDegreeOfParallelism: Threads), control);
        return [.. run.Points.Select(p =>
        {
            var sm = new Complex[p.S.RowCount, p.S.ColCount];
            for (int i = 0; i < p.S.RowCount; i++)
                for (int j = 0; j < p.S.ColCount; j++) sm[i, j] = p.S[i, j];
            return sm;
        })];
    }
}
