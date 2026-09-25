// brief-em3d-22 R-em3d22-3c, R-em3d22-4b/c — a static solve's matrices as a DataSet.
//
// A capacitance or inductance matrix is not S: there is no frequency, no port, no reference impedance
// and no Touchstone file (overview §1g). It lands as single-kind REAL cubes on two axes labelled with
// the terminals' names, so a matrix entry is read by name ("C" at RF_IN × VDD) and not by position.
//
// TWO FORMS OF ONE MATRIX, and both are kept and labelled, because they are mixed up constantly and
// their diagonals mean different things:
//   * the MAXWELL matrix (cube C): Cᵢᵢ is the charge on i with i at 1 V and every other conductor
//     grounded — i's capacitance to EVERYTHING — and Cᵢⱼ ≤ 0 off the diagonal;
//   * the MUTUAL (lumped-circuit) matrix (cube C_mutual): the capacitors a schematic would draw —
//     Cᵢᵢ = Σⱼ Cᵢⱼ is i's capacitance to GROUND only, and Cᵢⱼ = −Cᵢⱼ(Maxwell) ≥ 0 is the capacitor
//     between i and j.
// Palace writes both (terminal-C.csv, terminal-Cm.csv) and circuitRF only relabels them.

using RfCore.Data;

namespace CircuitRF.Engine.Em3d;

public static class Em3dStaticResult
{
    /// <summary>The row axis's name.</summary>
    public const string AxisI = "Terminal i";

    /// <summary>The column axis's name.</summary>
    public const string AxisJ = "Terminal j";

    /// <summary>The Maxwell capacitance matrix, farads.</summary>
    public const string CapacitanceCube = "C";

    /// <summary>The mutual (lumped-circuit) capacitance matrix, farads.</summary>
    public const string MutualCapacitanceCube = "C_mutual";

    /// <summary>The inductance matrix, henries.</summary>
    public const string InductanceCube = "L";

    /// <summary>Palace's current-difference form of the inductance matrix, henries.</summary>
    public const string MutualInductanceCube = "L_mutual";

    /// <summary>The group the result's notes are carried in, as labels (the comparison's convention).</summary>
    public const string NotesGroup = "static";

    /// <summary>The notes cube in <see cref="NotesGroup"/>.</summary>
    public const string NotesCube = "Notes";

    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>
    /// A matrix as a cube: <c>[i, j]</c> is row <paramref name="names"/>[i], column [j]. The axis
    /// values are the terminal indices (1-based, Palace's), the labels their names.
    /// </summary>
    public static DataCube Matrix(double[,] m, IReadOnlyList<string> names, string unit)
    {
        int n = names.Count;
        if (m.GetLength(0) != n || m.GetLength(1) != n) throw new ArgumentOutOfRangeException(nameof(names));
        double[] index = [.. Enumerable.Range(1, n).Select(k => (double)k)];
        string[] labels = [.. names];
        var data = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                data[i * n + j] = m[i, j];
        return new DataCube([new Axis(AxisI, index, "", labels), new Axis(AxisJ, index, "", labels)], data) { Unit = unit };
    }

    /// <summary>The DataSet of a static solve: the matrix, its other form when there is one, and the
    /// notes carried as labels in <see cref="NotesGroup"/>.</summary>
    public static DataSet Build(Em3dProblemType type, IReadOnlyList<string> names, double[,] matrix, double[,]? mutual,
                                IReadOnlyList<string> notes)
    {
        bool es = type == Em3dProblemType.Electrostatic;
        if (!es && type != Em3dProblemType.Magnetostatic) throw new ArgumentOutOfRangeException(nameof(type));
        var ds = new DataSet();
        string unit = es ? "F" : "H";
        ds.Add(es ? CapacitanceCube : InductanceCube, Matrix(matrix, names, unit));
        if (mutual is not null) ds.Add(es ? MutualCapacitanceCube : MutualInductanceCube, Matrix(mutual, names, unit));
        if (notes.Count > 0)
            ds.AddToGroup(NotesGroup, NotesCube, new DataCube(
                [new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(k => (double)k)], "", [.. notes])],
                new double[notes.Count]));
        return ds;
    }

    /// <summary>
    /// R-em3d22-4b — what a magnetostatic result leaves out, said with its size. Conductors are voids
    /// (F0 Q6), so all current is on their surfaces and the solve returns EXTERNAL inductance — the
    /// high-frequency value — with no internal term. For a round wire of length ℓ that term is μ₀ℓ/8π
    /// (the DC limit, uniform current), which is quoted for the THICKEST round conductor, the one it is
    /// largest relative to its external inductance for. Null when the problem has no round conductor.
    /// </summary>
    public static string InternalInductanceNote(Em3dProblem problem)
    {
        const string head = "This is EXTERNAL inductance: conductors are modelled as surfaces, so all of the " +
                            "current flows on them, as it does at RF, and the inductance inside the metal is not " +
                            "included. The DC inductance is higher by that internal term, which is the limit that differs.";
        (string Name, double Diameter, double Length)? thickest = null;
        foreach (var s in problem.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            (double d, double l)? round = s.Primitive switch
            {
                Em3dSweep { Section: Em3dSection.Circle } w => (w.Diameter, PathLength(w.Path)),
                Em3dCylinder c => (2 * c.Radius, Distance(c.AxisStart, c.AxisEnd)),
                _ => null,
            };
            if (round is { } r && (thickest is null || r.d > thickest.Value.Diameter))
                thickest = (s.Name, r.d, r.l);
        }
        if (thickest is not { } t) return head;
        double lint = Mu0 * t.Length / (8 * Math.PI);
        return head + $" For the thickest round conductor, '{t.Name}' ({Eng(t.Diameter, "m")} across, {Eng(t.Length, "m")} long), " +
               $"the internal term at DC is μ₀ℓ/8π = {Eng(lint, "H")}.";
    }

    /// <summary>A value in engineering notation: 142.31 fF, 1.2 nH, 25 µm.</summary>
    public static string Eng(double v, string unit)
    {
        if (v == 0 || !double.IsFinite(v)) return $"{v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)} {unit}";
        string[] prefixes = ["f", "p", "n", "µ", "m", "", "k", "M", "G"];
        int e = (int)Math.Floor(Math.Log10(Math.Abs(v)) / 3);
        e = Math.Clamp(e, -5, 3);
        double scaled = v / Math.Pow(1000, e);
        return $"{scaled.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {prefixes[e + 5]}{unit}";
    }

    private static double PathLength(IReadOnlyList<Point3> path)
    {
        double s = 0;
        for (int k = 1; k < path.Count; k++) s += Distance(path[k - 1], path[k]);
        return s;
    }

    private static double Distance(Point3 a, Point3 b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
