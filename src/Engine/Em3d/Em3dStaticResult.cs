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

    /// <summary>brief-dfr34-4 R-dfr34-4-5 — each terminal's capacitance to ground alone, farads: the row sums of C, the diagonal
    /// of C_mutual. One axis, <see cref="AxisI"/>.</summary>
    public const string GroundCapacitanceCube = "C_ground";

    /// <summary>The app note page and section that explain the two matrices (a run's note links there).</summary>
    public const string MaxwellDocPage = "app-notes/an03-extracting-capacitance.html", MaxwellDocAnchor = "maxwell";

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

    /// <summary>The DataSet of a static solve: the matrix, its other form, and the notes carried as labels in
    /// <see cref="NotesGroup"/>. brief-dfr34-4 R-dfr34-4-5 — an electrostatic result always carries C_mutual (derived from C
    /// when <paramref name="mutual"/> is null) and <see cref="GroundCapacitanceCube"/>, each terminal's capacitance to ground.</summary>
    public static DataSet Build(Em3dProblemType type, IReadOnlyList<string> names, double[,] matrix, double[,]? mutual,
                                IReadOnlyList<string> notes)
    {
        bool es = type == Em3dProblemType.Electrostatic;
        if (!es && type != Em3dProblemType.Magnetostatic) throw new ArgumentOutOfRangeException(nameof(type));
        var ds = new DataSet();
        string unit = es ? "F" : "H";
        ds.Add(es ? CapacitanceCube : InductanceCube, Matrix(matrix, names, unit));
        if (es) mutual ??= MutualFromMaxwell(matrix);
        if (mutual is not null) ds.Add(es ? MutualCapacitanceCube : MutualInductanceCube, Matrix(mutual, names, unit));
        if (es) ds.Add(GroundCapacitanceCube, ToGround(matrix, names));
        if (notes.Count > 0)
            ds.AddToGroup(NotesGroup, NotesCube, new DataCube(
                [new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(k => (double)k)], "", [.. notes])],
                new double[notes.Count]));
        return ds;
    }

    /// <summary>
    /// brief-dfr31-1 R-dfr31-1-6 — the Maxwell matrix of the terminals that are not floating, from the one solved with every
    /// floating body as a terminal too. A floating body carries no net charge, so with the kept terminals at V_t its
    /// potential is whatever makes C_ft·V_t + C_ff·V_f = 0; putting that V_f back into the kept rows leaves
    /// C_red = C_tt − C_tf·C_ff⁻¹·C_ft. Exact, not an approximation (src/Design/RESOLVED.md §brief-dfr31-1).
    /// </summary>
    public static double[,] EliminateFloating(double[,] c, IReadOnlyList<bool> floating)
    {
        int n = c.GetLength(0);
        if (c.GetLength(1) != n || floating.Count != n) throw new ArgumentException("The matrix and the floating flags disagree in size.");
        int[] t = [.. Enumerable.Range(0, n).Where(k => !floating[k])];
        int[] f = [.. Enumerable.Range(0, n).Where(k => floating[k])];
        var red = new double[t.Length, t.Length];
        for (int i = 0; i < t.Length; i++)
            for (int j = 0; j < t.Length; j++) red[i, j] = c[t[i], t[j]];
        if (f.Length == 0) return red;

        // X = C_ff⁻¹ · C_ft, by Gaussian elimination with partial pivoting on [C_ff | C_ft].
        int m = f.Length, w = m + t.Length;
        var a = new double[m, w];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++) a[i, j] = c[f[i], f[j]];
            for (int j = 0; j < t.Length; j++) a[i, m + j] = c[f[i], t[j]];
        }
        for (int col = 0; col < m; col++)
        {
            int piv = col;
            for (int r = col + 1; r < m; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (!(Math.Abs(a[piv, col]) > 0)) throw new InvalidOperationException("The floating bodies' block of the matrix is singular.");
            if (piv != col)
                for (int j = 0; j < w; j++) (a[col, j], a[piv, j]) = (a[piv, j], a[col, j]);
            for (int r = 0; r < m; r++)
            {
                if (r == col || a[r, col] == 0) continue;
                double k = a[r, col] / a[col, col];
                for (int j = col; j < w; j++) a[r, j] -= k * a[col, j];
            }
        }
        for (int i = 0; i < t.Length; i++)
            for (int j = 0; j < t.Length; j++)
            {
                double s = 0;
                for (int k = 0; k < m; k++) s += c[t[i], f[k]] * a[k, m + j] / a[k, k];
                red[i, j] -= s;
            }
        return red;
    }

    /// <summary>brief-dfr34-4 R-dfr34-4-5 — each terminal's capacitance to ground alone: row i is Σⱼ C[i][j].</summary>
    public static DataCube ToGround(double[,] c, IReadOnlyList<string> names)
    {
        int n = names.Count;
        if (c.GetLength(0) != n || c.GetLength(1) != n) throw new ArgumentOutOfRangeException(nameof(names));
        var data = new double[n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) data[i] += c[i, j];
        return new DataCube([new Axis(AxisI, [.. Enumerable.Range(1, n).Select(k => (double)k)], "", [.. names])], data) { Unit = "F" };
    }

    /// <summary>brief-dfr31-1 — the mutual (lumped-circuit) form of a Maxwell matrix: Cm[i][i] = Σⱼ C[i][j] (i to ground
    /// alone) and Cm[i][j] = −C[i][j].</summary>
    public static double[,] MutualFromMaxwell(double[,] c)
    {
        int n = c.GetLength(0);
        var m = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i != j) m[i, j] = -c[i, j];
                m[i, i] += c[i, j];
            }
        return m;
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
                Em3dSphere sp => (2 * sp.Radius, 2 * sp.Radius),   // brief-em3d-102: a sphere's length is its diameter
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
