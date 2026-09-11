using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// <b>A REAL <c>"farfield"</c> group, from a real solve.</b> ANT-7 §1 asks for its three claims to be
/// confirmed on one, and a hand-built cube would confirm them about a hand-built cube: the axis
/// names, the units, the θ range and the DataKind of each cube are all decisions ANT-4 made in
/// <c>PlanarKernel.AddFarField</c>, and a fixture that restated them would agree with itself.
///
/// <para>The structure is the coarse FR-4 line every port test in <c>Engine.Tests</c> is built on,
/// which is what makes this cheap — one frequency, a 10 cells/λ mesh with the edge fan off, and a
/// deliberately coarse 30°×45° pattern grid. It is not a good antenna and does not need to be: what
/// is under test here is the DISPLAY.</para>
/// </summary>
internal static class PatternFixture
{
    public const double FHz = 5e9;

    private static readonly Lazy<DataSet> Cached = new(BuildCore, isThreadSafe: true);

    /// <summary>The DataSet a far-field run produces — S, the planar diagnostics, and the
    /// <c>"farfield"</c> group. Built once per process: it is a solve, and every test here reads it
    /// without writing to it.</summary>
    public static DataSet Data => Cached.Value;

    private static DataSet BuildCore()
    {
        var problem = Line(GroundedSlab.Fr4Starter, 2.9e-3, 4e-3, FHz);
        var ports   = EndPorts(problem);
        var far     = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(30, 45));
        var mesh    = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false);

        return new PlanarKernel()
            .Solve(problem, mesh, ports, [FHz], new PlanarSolveSettings(Deembed: false, FarField: far))
            .Data;
    }

    private static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
        new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

    private static PlanarProblem Line(GroundedSlab slab, double widthM, double lengthM, double fHz) =>
        new([new PlanarConductorLayer("Metal", [Rect(0, -0.5 * widthM, lengthM, 0.5 * widthM)], 5.8e7, 35e-6)],
            slab, fHz);

    private static PlanarPort[] EndPorts(PlanarProblem problem, double z0 = 50.0)
    {
        var (x0, y0, x1, y1) = problem.Bounds();
        double yc = 0.5 * (y0 + y1);
        return
        [
            new PlanarPort(1, new EmPoint(x0, yc), PlanarPortSide.MinX, z0),
            new PlanarPort(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, z0),
        ];
    }
}
