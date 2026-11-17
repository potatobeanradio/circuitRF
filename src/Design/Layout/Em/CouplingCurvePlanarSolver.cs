using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// Planar MoM behind <see cref="ICouplingCurveSolver"/> (docs/design/filter-synthesis.md §6b): the plan drawn as its PCell
/// draws it, the copper unioned into one sheet on the substrate's grounded slab, one edge port on each pin facing out, and
/// the sweep solved without de-embedding (a bench is read for its resonances, which the ports' own steps barely move).
/// </summary>
/// <param name="Mesh">The mesh; null is a coarse one, ten cells a wavelength and no edge cells.</param>
/// <param name="Threads">The solve's parallelism.</param>
public sealed record CouplingCurvePlanarSolver(PlanarMeshSettings? Mesh = null, int Threads = 8) : ICouplingCurveSolver
{
    public static readonly PlanarMeshSettings Coarse = new(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);

    public Complex[][,] Solve(PassivePlan plan, MicrostripSubstrate s, IReadOnlyList<double> fHz)
    {
        var cell = PassivePlanPCells.Draw(plan, null, PCellLayerSelection.Default);
        const double m = 1e-6 / LayoutUnits.DefaultDbuPerMicron;
        IReadOnlyList<EmPoint> Ring(long[] xy) => [.. Enumerable.Range(0, xy.Length / 2).Select(i => new EmPoint(xy[2 * i] * m, xy[2 * i + 1] * m))];
        var copper = LayoutBooleans.Union(cell.Shapes, null).Shapes
            .Select(shape => LayoutFlattener.Flatten(shape, 1))
            .Where(rings => rings.Count > 0)
            .Select(rings => new PlanarPolygon(Ring(rings[0]), rings.Count > 1 ? [.. rings.Skip(1).Select(Ring)] : null));
        var problem = new PlanarProblem([new PlanarConductorLayer("Metal", [.. copper], s.SigmaSPerM, s.TMeters)],
            new GroundedSlab(s.HMeters, new EmMaterial(s.EpsR, s.TanD)), fHz.Max());
        var ports = cell.Pins.Select((p, i) => new PlanarPort(i + 1, new EmPoint(p.X * m, p.Y * m),
            ((int)Math.Round(p.OutwardDirectionDeg / 90.0) & 3) switch
            {
                0 => PlanarPortSide.MaxX, 1 => PlanarPortSide.MaxY, 2 => PlanarPortSide.MinX, _ => PlanarPortSide.MinY,
            }, 50.0)).ToArray();
        var mesh = SurfaceMesher.Mesh(problem, Mesh ?? Coarse).Mesh;
        var run = PlanarSolve.Run(problem, mesh, PlanarPorts.ResolveAll(mesh, ports), fHz,
            new PlanarSolveSettings(Deembed: false, MaxDegreeOfParallelism: Threads));
        return [.. run.Points.Select(p =>
        {
            var sm = new Complex[p.S.RowCount, p.S.ColCount];
            for (int i = 0; i < p.S.RowCount; i++)
                for (int j = 0; j < p.S.ColCount; j++) sm[i, j] = p.S[i, j];
            return sm;
        })];
    }
}
