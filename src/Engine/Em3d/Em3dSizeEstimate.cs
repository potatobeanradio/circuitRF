// brief-em3d-5 R-em3d5-3c — the size of a 3D run before it starts.
//
// Two kinds of number, never confused: Palace's is an ESTIMATE (its mesher and its adaptive refinement
// decide the real count, and no Gmsh is run to find out), openEMS's is EXACT (brief 8's grid generator
// IS the grid). Every per-unknown figure below is a measurement, named with the F0 run it came from
// (docs/design/em-3d-f0-findings.md §4, R-em3d1-2b), and a figure with no measurement behind it is not
// a constant here at all — so it cannot be printed.

namespace CircuitRF.Engine.Em3d;

/// <summary>One region's share of a Palace estimate.</summary>
public sealed record Em3dRegionEstimate(string Solid, double VolumeM3, double ElementEdgeM, double Tetrahedra);

/// <summary>A Palace mesh size, ESTIMATED. <see cref="MemoryBytes"/> is null where no measurement
/// gives a per-unknown figure at this element order.</summary>
public sealed record Em3dPalaceEstimate(
    long Tetrahedra, long Unknowns, int Order, long? MemoryBytes, IReadOnlyList<Em3dRegionEstimate> Regions);

public static class Em3dSizeEstimate
{
    /// <summary>Nédélec unknowns per tetrahedron at element order 1: F0 case A, the same 146,769-tet
    /// mesh run at order 1 (<c>A palace-round-order1</c>, 178,142 unknowns).</summary>
    public const double UnknownsPerTetOrder1 = 178_142.0 / 146_769.0;

    /// <summary>…and at order 2, Palace's default: F0 case A (<c>A palace-round</c>, 953,946 unknowns
    /// on 146,769 tets). Case B's 593,548 on 87,467 gives 6.79, within 5 %.</summary>
    public const double UnknownsPerTetOrder2 = 953_946.0 / 146_769.0;

    /// <summary>
    /// Palace's peak memory per unknown at order 2, all eight ranks together, as Palace reports it:
    /// F0's <c>B palace</c>, 7.0 GB for 593,548 unknowns — the HIGHEST of the order-2 runs without
    /// adaptive refinement (case A's is 7.4 GB for 953,946), so an estimate errs toward not fitting.
    /// There is no order-1 figure: F0's one order-1 run (6.6 GB for 178,142 unknowns) is dominated by
    /// what eight ranks cost before the first unknown, so it separates nothing per unknown.
    /// </summary>
    public const double PalaceBytesPerUnknownOrder2 = 7.0e9 / 593_548.0;

    /// <summary>
    /// openEMS's peak resident memory per cell: F0's wire ladder at its finest rung, 182 MB for
    /// 1,558,730 cells — the largest run F0 made, so the one whose fixed cost is the smallest share.
    /// Smaller runs read higher per cell (47 MB for 199,200) and are small in absolute terms.
    /// </summary>
    public const double OpenEmsBytesPerCell = 182e6 / 1_558_730.0;

    /// <summary>
    /// The initial Palace mesh, estimated: every MESHED region's volume divided by the volume of one
    /// regular tetrahedron at that region's initial element edge, summed. Conductors are holes in a
    /// Palace mesh (em-3d.md §6.1), so only dielectric and air solids count. Each solid is counted as
    /// drawn: where a later solid overlaps an earlier one the overlap is counted twice, so the
    /// estimate errs high. Adaptive refinement grows the real count, and the run's first line reports
    /// it.
    /// </summary>
    /// <param name="initialEdgeM">A solid's initial element edge, metres — the Palace section's
    /// size fields (brief 7) resolved for that solid.</param>
    /// <param name="order">The Nédélec element order, 1 or 2.</param>
    public static Em3dPalaceEstimate Palace(Em3dProblem problem, Func<Em3dSolid, double> initialEdgeM, int order)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(initialEdgeM);
        if (order is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(order), order, "F0 measured element orders 1 and 2 only");

        var regions = new List<Em3dRegionEstimate>();
        double tets = 0;
        foreach (var s in problem.Solids.Where(s => s.Role != Em3dRole.Conductor))
        {
            double h = initialEdgeM(s);
            double v = Volume(s.Primitive);
            double n = h > 0 ? v / (h * h * h / (6 * Math.Sqrt(2))) : 0;
            regions.Add(new Em3dRegionEstimate(s.Name, v, h, n));
            tets += n;
        }
        long unknowns = (long)Math.Round(tets * (order == 1 ? UnknownsPerTetOrder1 : UnknownsPerTetOrder2));
        long? memory = order == 2 ? (long)Math.Round(unknowns * PalaceBytesPerUnknownOrder2) : null;
        return new Em3dPalaceEstimate((long)Math.Round(tets), unknowns, order, memory, regions);
    }

    /// <summary>openEMS's memory for a grid of <paramref name="cells"/> cells — brief 8 supplies the
    /// count.</summary>
    public static long OpenEmsMemoryBytes(long cells) => (long)Math.Round(cells * OpenEmsBytesPerCell);

    /// <summary>A primitive's volume, m³. A sweep's comes from its tessellation (divergence
    /// theorem over the closed mesh), so it is the volume of exactly what a backend is handed.</summary>
    public static double Volume(Em3dPrimitive p) => p switch
    {
        Em3dExtrudedPolygon e => (Math.Abs(Area(e.Outline)) - e.Holes.Sum(h => Math.Abs(Area(h)))) * (e.ZTop - e.ZBottom),
        Em3dBox b             => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z),
        Em3dCylinder c        => Math.PI * c.Radius * c.Radius * Distance(c.AxisStart, c.AxisEnd),
        Em3dSphere s          => 4.0 / 3.0 * Math.PI * s.Radius * s.Radius * s.Radius,
        Em3dTruncatedSphere t => Cap(t),
        _                     => MeshVolume(Em3dTessellation.Of(new Em3dSolid("", "", Em3dRole.Conductor, p, 0))),
    };

    /// <summary>The volume a closed, consistently wound mesh encloses.</summary>
    public static double MeshVolume(Em3dTriangleMesh mesh)
    {
        double six = 0;
        foreach (var t in mesh.Triangles)
        {
            var a = mesh.Vertices[t.A]; var b = mesh.Vertices[t.B]; var c = mesh.Vertices[t.C];
            six += a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
        }
        return Math.Abs(six) / 6;
    }

    private static double Cap(Em3dTruncatedSphere t)
    {
        double r = t.Radius;
        double a = Math.Max(t.ZMin - t.Center.Z, -r), b = Math.Min(t.ZMax - t.Center.Z, r);
        return b > a ? Math.PI * (r * r * (b - a) - (b * b * b - a * a * a) / 3) : 0;
    }

    private static double Area(IReadOnlyList<Point2> r)
    {
        double s = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) s += r[j].X * r[i].Y - r[i].X * r[j].Y;
        return s / 2;
    }

    private static double Distance(Point3 a, Point3 b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
