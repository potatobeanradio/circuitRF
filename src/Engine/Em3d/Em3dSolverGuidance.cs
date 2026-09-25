// brief-em3d-5 R-em3d5-3b — em-3d.md §4.3, "When each is the right tool", as sentences about THIS
// geometry. Guidance, never a decision: nothing here reads or changes a setup's Solver3D, and a user
// may run either solver on anything (§4.3's own first line).
//
// The table below mirrors §4.3 ROW FOR ROW, with each row's number, so a sentence can cite the row it
// came from. Three rows are about what the user INTENDS — a radiator, a resonator, an RLC extraction —
// which no geometry states; they are listed so the mirror is complete, and they never fire.

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>Which solver a §4.3 row favours.</summary>
public enum Em3dSolverFit { Fem, Fdtd }

/// <summary>One sentence of guidance: the §4.3 row it came from, the solver that row favours, and
/// what about this geometry triggered it.</summary>
public sealed record Em3dGuidance(int Row, Em3dSolverFit Favours, string Sentence);

public static class Em3dSolverGuidance
{
    /// <summary>§4.3 row 3's "very broadband", made a number: the stop frequency at least this many
    /// times the start. A decade — past it, a frequency-domain solver's adaptive sweep is doing most
    /// of the work FDTD's one pulse does for free. Provisional.</summary>
    public const double BroadbandRatio = 10.0;

    /// <summary>§4.3 row 7's "fine features in a large volume", made a number: the smallest
    /// conductor feature below this fraction of the air box's largest side. At 1/1000 one fine
    /// feature sets FDTD's cell size across a thousand cells per axis. Provisional.</summary>
    public const double FineFeatureRatio = 1e-3;

    /// <summary>The §4.3 rows, verbatim in substance: number, what the row covers, and its fit.</summary>
    public static readonly IReadOnlyList<(int Row, string Problem, Em3dSolverFit Fit, bool FromGeometry)> Rows =
    [
        (1, "board and connector transitions, Manhattan geometry on a stackup", Em3dSolverFit.Fdtd, true),
        (2, "radiating structures, antennas, far field",                      Em3dSolverFit.Fdtd, false),
        (3, "very broadband S-parameters",                                     Em3dSolverFit.Fdtd, true),
        (4, "bond wires, curved and diagonal metal, round vias",               Em3dSolverFit.Fem,  true),
        (5, "high-Q cavities, filters, package resonances",                    Em3dSolverFit.Fem,  false),
        (6, "package RLC extraction",                                          Em3dSolverFit.Fem,  false),
        (7, "fine features in a large volume",                                 Em3dSolverFit.Fem,  true),
    ];

    /// <summary>The guidance for <paramref name="problem"/>, in row order. Empty when no row that
    /// geometry can decide applies.</summary>
    public static IReadOnlyList<Em3dGuidance> For(Em3dProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var said = new List<Em3dGuidance>();

        var conductors = problem.Solids.Where(s => s.Role == Em3dRole.Conductor).ToList();
        int wires = conductors.Count(s => s.Primitive is Em3dSweep);
        int round = conductors.Count(s => s.Primitive is Em3dCylinder or Em3dSphere or Em3dTruncatedSphere);
        int diagonal = conductors.Count(s => s.Primitive is Em3dExtrudedPolygon e && HasDiagonal(e.Outline, e.Holes))
                     + problem.Sheets.Count(s => HasDiagonal(s.Outline, s.Holes));

        // Row 4, and row 1 as its complement: Manhattan means none of what row 4 names.
        if (wires + round + diagonal > 0)
        {
            var what = new List<string>();
            if (wires > 0)    what.Add($"{wires} bond wire{(wires == 1 ? "" : "s")}");
            if (round > 0)    what.Add($"{round} round conductor{(round == 1 ? "" : "s")} (vias, balls)");
            if (diagonal > 0) what.Add($"{diagonal} conductor{(diagonal == 1 ? "" : "s")} with curved or diagonal edges");
            said.Add(new Em3dGuidance(4, Em3dSolverFit.Fem,
                $"{Join(what)}: FEM (Palace) fits curved and diagonal metal better; FDTD (openEMS) will need a " +
                "fine grid near each of them, and pays in cells for every curve (em-3d.md §4.3, row 4)."));
        }
        else
            said.Add(new Em3dGuidance(1, Em3dSolverFit.Fdtd,
                "Manhattan geometry on a stackup: FDTD (openEMS) fits, since the metal is aligned with the grid " +
                "and one run covers the band (em-3d.md §4.3, row 1)."));

        var f = problem.Frequency;
        if (f.StartHz > 0 && f.StopHz / f.StartHz >= BroadbandRatio)
            said.Add(new Em3dGuidance(3, Em3dSolverFit.Fdtd,
                $"The band spans {Ratio(f.StopHz / f.StartHz)}:1 ({F(Math.Log10(f.StopHz / f.StartHz))} decades): " +
                "FDTD's one pulse covers it in one run; Palace's adaptive sweep narrows the gap " +
                "(em-3d.md §4.3, row 3)."));

        double smallest = SmallestFeature(problem);
        var b = problem.Boundary;
        double largest = Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        if (smallest > 0 && largest > 0 && smallest < FineFeatureRatio * largest)
            said.Add(new Em3dGuidance(7, Em3dSolverFit.Fem,
                $"The smallest conductor feature ({F(smallest * 1e6)} µm) is {Ratio(largest / smallest)} times smaller " +
                "than the air box's largest side: adaptive refinement puts FEM elements where they are needed, " +
                "while one fine feature sets FDTD's cell size and, through the stability limit, its time step " +
                "(em-3d.md §4.3, row 7)."));

        return said.OrderBy(g => g.Row).ToList();
    }

    /// <summary>The smallest lateral dimension of any conductor: a polygon's 2·area/perimeter (a
    /// strip's width), a sheet's likewise, a wire's diameter, a via's diameter. Zero with none.</summary>
    public static double SmallestFeature(Em3dProblem problem)
    {
        double least = double.PositiveInfinity;
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
            least = Math.Min(least, s.Primitive switch
            {
                Em3dExtrudedPolygon e => Width(e.Outline, e.Holes),
                Em3dBox x             => Math.Min(x.Max.X - x.Min.X, x.Max.Y - x.Min.Y),
                Em3dCylinder c        => 2 * c.Radius,
                Em3dSweep w           => w.Diameter,
                Em3dSphere p          => 2 * p.Radius,
                Em3dTruncatedSphere t => 2 * t.Radius,
                _                     => double.PositiveInfinity,
            });
        foreach (var sh in problem.Sheets) least = Math.Min(least, Width(sh.Outline, sh.Holes));
        return double.IsPositiveInfinity(least) ? 0 : least;
    }

    private static bool HasDiagonal(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
        => Diagonal(outline) || holes.Any(Diagonal);

    private static bool Diagonal(IReadOnlyList<Point2> ring)
    {
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double dx = Math.Abs(ring[i].X - ring[j].X), dy = Math.Abs(ring[i].Y - ring[j].Y);
            // Axis-aligned to a part in a billion of the edge's own length: a rounding difference in a
            // DBU-to-metre conversion is not a diagonal.
            double tol = 1e-9 * Math.Max(dx, dy);
            if (dx > tol && dy > tol) return true;
        }
        return false;
    }

    private static double Width(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
    {
        double area = Math.Abs(Area(outline)) - holes.Sum(h => Math.Abs(Area(h)));
        double perimeter = Length(outline) + holes.Sum(Length);
        return perimeter > 0 ? 2 * area / perimeter : double.PositiveInfinity;
    }

    private static double Area(IReadOnlyList<Point2> r)
    {
        double a = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += r[j].X * r[i].Y - r[i].X * r[j].Y;
        return a / 2;
    }

    private static double Length(IReadOnlyList<Point2> r)
    {
        double s = 0;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            s += Math.Sqrt((r[i].X - r[j].X) * (r[i].X - r[j].X) + (r[i].Y - r[j].Y) * (r[i].Y - r[j].Y));
        return s;
    }

    private static string Join(List<string> parts)
        => parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private static string F(double v) => v.ToString("G3", CultureInfo.InvariantCulture);

    /// <summary>A large ratio as a whole number, not in exponent form.</summary>
    private static string Ratio(double v) => v >= 100 ? Math.Round(v).ToString("N0", CultureInfo.InvariantCulture) : F(v);
}
