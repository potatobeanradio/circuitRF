// brief-em3d-74 R-em3d74-6 — how big a thermal run will be, before Gmsh runs: the elements the sizing rules ask for, the
// unknowns they make, the solver that count selects, and the memory it will take. An ESTIMATE, and it says so: each solid
// is its bounding box at its own element size; each heat source adds the graded shell its Threshold field refines
// (a hemisphere); each mesh region its box at its size. The memory figures are brief 72 Q5's measurements on S5
// (CSparse's Cholesky factor ≈ 401 MB at 89,761 unknowns, growing as n^1.44; CG + AMG's peak ≈ 2.8 kB per unknown).

using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Thermal;

namespace CircuitRF.Design.Thermal;

/// <summary>A thermal run's size, estimated.</summary>
public sealed record ThermalSizeEstimate(long Elements, long Unknowns, ThermalSolverKind Solver, long MemoryBytes)
{
    /// <summary>The volume of a regular tetrahedron of edge 1: an element's volume at a size.</summary>
    private const double TetVolume = 0.11785113019775793;

    /// <summary>P2 nodes per tetrahedron on the graded meshes brief 72 made (1.5), and P1's (1/5.5).</summary>
    private const double P2NodesPerTet = 1.5, P1NodesPerTet = 1 / 5.5;

    public static ThermalSizeEstimate Of(GmshThermalInput input, ThermalSolveOptions? options = null)
    {
        options ??= new ThermalSolveOptions();
        var z = input.Sizing;
        double sizeMax = z.Scale * GmshGeoWriter.ThermalMaxElementM(input);
        double elements = 0, background = sizeMax;
        foreach (var s in input.Solids)
        {
            var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
            double h = z.Scale * GmshGeoWriter.ThermalSolidSizeM(input, s);
            elements += (x1 - x0) * (y1 - y0) * (z1 - z0) / (TetVolume * h * h * h);
            background = Math.Min(background, h);
        }
        foreach (var sh in input.Sheets)
        {
            double near = z.Scale * GmshGeoWriter.ThermalSourceSizeM(input, sh.Sheet);
            double far = near + (sizeMax - near) / (z.Grading - 1);
            const int steps = 200;
            for (int k = 0; k < steps; k++)
            {
                double r = far * (k + 0.5) / steps, dr = far / steps;
                double h = r <= near ? near : near + (sizeMax - near) * (r - near) / (far - near);
                if (h >= background) continue;
                double shell = 2 * Math.PI * r * r * dr;
                elements += shell / (TetVolume * h * h * h) - shell / (TetVolume * background * background * background);
            }
        }
        foreach (var region in input.MeshRegions)
        {
            double h = Math.Min(region.SizeM * z.Scale, background);
            double v = (region.Max.X - region.Min.X) * (region.Max.Y - region.Min.Y) * (region.Max.Z - region.Min.Z);
            elements += v / (TetVolume * h * h * h) - v / (TetVolume * background * background * background);
        }
        long tets = (long)Math.Ceiling(Math.Max(elements, input.Solids.Count));
        long unknowns = (long)Math.Ceiling(tets * (z.Order == 2 ? P2NodesPerTet : P1NodesPerTet));
        return Of(tets, unknowns, options);
    }

    /// <summary>brief-dfr36-1 R-dfr36-1-2 — the solver and the memory for a known count, which a mesh on disk gives exactly.</summary>
    public static ThermalSizeEstimate Of(long elements, long unknowns, ThermalSolveOptions options)
    {
        var solver = options.Solver != ThermalSolverKind.Auto ? options.Solver
                   : unknowns < options.DirectBelow ? ThermalSolverKind.Direct : ThermalSolverKind.Iterative;
        long bytes = solver == ThermalSolverKind.Direct
            ? (long)(401e6 * Math.Pow(unknowns / 89761.0, 1.44))
            : (long)(2.8e3 * unknowns);
        return new ThermalSizeEstimate(elements, unknowns, solver, bytes);
    }
}
