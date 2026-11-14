// brief-paa-2 R-paa2-9 — the lattice a result's ports stand on, so a sub-array's ports can be placed on an array's lattice without the
// user saying where they are.
//
// FROM THE RECORDED POSITIONS (R-paa2-8): the ports are grouped into rows by y (top first), each row ordered by x, and the fit is a
// rectangular lattice (every row starting at one x) or a triangular one (the odd rows half a pitch to the RIGHT of the even ones —
// the convention ArrayLattice draws), with one row pitch and one column pitch, each within a thousandth of the closest two ports'
// distance. The numbering is whatever the ports' order on that lattice is, and the fit says when it is not row-major. What does not fit
// is explained rather than forced: a row short of ports (a missing port), two pitches, rows offset by something other than 0 or half
// a pitch, a lattice rotated from the layout's axes.
//
// WITHOUT POSITIONS (an older record: "positions not recorded") the lattice is the caller's RecordRows × RecordCols at
// RecordDx × RecordDy, the numbering is ASSUMED row-major from the top left (D6), and the ports are assumed centred on the record's
// origin. The last assumption changes nothing a magnitude can show: an origin elsewhere multiplies every element's referred pattern by
// one common phase over direction.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.AntennaArray;

namespace CircuitRF.Design.Em3d;

/// <summary>R-paa2-9 — a record's lattice, or why its ports are not one.</summary>
/// <param name="Lattice">The lattice; null when the ports are not one (<see cref="Refusal"/> says why).</param>
/// <param name="PortOfElement">The 0-based port at each lattice element, row-major.</param>
/// <param name="Positions">Each port's position in the record's frame, metres, by port (assumed when not recorded).</param>
/// <param name="PositionsRecorded">Whether the record states its ports' positions.</param>
/// <param name="Note">What was fitted or assumed, as a run's notes say it.</param>
public sealed record RecordLatticeFit(ArrayLattice? Lattice, IReadOnlyList<int> PortOfElement, IReadOnlyList<(double X, double Y)> Positions,
                                      bool PositionsRecorded, string? Refusal, string Note)
{
    /// <summary>The ports are numbered row-major from the top left, as D6 numbers elements.</summary>
    public bool RowMajor => PortOfElement.Select((p, e) => p == e).All(x => x);
}

/// <summary>R-paa2-9 — fits a record's port positions to a lattice.</summary>
public static class RecordLattice
{
    /// <summary>What an older record is said to be.</summary>
    public const string NotRecorded = "positions not recorded";

    public static RecordLatticeFit Of(EmResultRecord record, int? recordRows = null, int? recordCols = null, double? recordDx = null, double? recordDy = null)
        => Of([.. record.Ports.OrderBy(p => p.Number).Select(p => p.Position is { } at ? ((double, double)?)(at.X, at.Y) : null)],
              recordRows, recordCols, recordDx, recordDy);

    public static RecordLatticeFit Of(PortFarFieldSet far, int? recordRows = null, int? recordCols = null, double? recordDx = null, double? recordDy = null)
        => Of(far.PortPositions, recordRows, recordCols, recordDx, recordDy);

    /// <summary>The lattice of ports at <paramref name="positions"/> (metres; null where not recorded), or of the caller's lattice when
    /// none is recorded.</summary>
    public static RecordLatticeFit Of(IReadOnlyList<(double X, double Y)?> positions, int? recordRows = null, int? recordCols = null,
                                      double? recordDx = null, double? recordDy = null)
    {
        int n = positions.Count;
        if (n == 0) return Refused("The record lists no ports.", false, []);
        if (positions.Any(p => p is null)) return Assumed(n, recordRows, recordCols, recordDx, recordDy, positions.All(p => p is null));
        var at = positions.Select(p => p!.Value).ToArray();
        if (n == 1)
            return new RecordLatticeFit(new ArrayLattice(1, 1, recordDx ?? 0, recordDy ?? 0), [0], at, true, null, "One port: a single element.");

        double closest = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++) closest = Math.Min(closest, Distance(at[i], at[j]));
        if (!(closest > 1e-9))
            return Refused("Two of its ports are at one point, so they are not elements of a lattice.", true, at);
        double tol = 1e-3 * closest;

        // Rows by y, top first; each row by x.
        var order = Enumerable.Range(0, n).OrderByDescending(i => at[i].Y).ThenBy(i => at[i].X).ToArray();
        var rows = new List<List<int>>();
        foreach (int i in order)
            if (rows.Count > 0 && Math.Abs(at[rows[^1][0]].Y - at[i].Y) <= tol) rows[^1].Add(i);
            else rows.Add([i]);
        foreach (var row in rows) row.Sort((a, b) => at[a].X.CompareTo(at[b].X));

        int cols = rows.Max(r => r.Count);
        if (rows.Count == n && n > 2 && Rotation(at, closest) is { } degrees)
            return Refused($"Its ports lie on a lattice turned {degrees:0.##}° from the layout's axes, and an array's lattice runs along them. " +
                           "Turn the layout so its rows run along x, or state RecordRows, RecordCols, RecordDx and RecordDy.", true, at);
        if (rows.Any(r => r.Count != cols))
        {
            int short_ = rows.FindIndex(r => r.Count != cols);
            return Refused($"Its ports are not a full lattice: row {short_ + 1} (from the top) has {rows[short_].Count} ports and the longest has {cols}, " +
                           "so a port is missing or one is out of line.", true, at);
        }

        // One row pitch, one column pitch.
        double dy = rows.Count > 1 ? at[rows[0][0]].Y - at[rows[1][0]].Y : 0;
        for (int r = 1; r < rows.Count; r++)
        {
            double step = at[rows[r - 1][0]].Y - at[rows[r][0]].Y;
            if (Math.Abs(step - dy) > tol) return Refused($"Its rows are {Mm(dy)} and {Mm(step)} apart: two pitches, not one lattice.", true, at);
        }
        double dx = cols > 1 ? at[rows[0][1]].X - at[rows[0][0]].X : 0;
        foreach (var row in rows)
            for (int c = 1; c < row.Count; c++)
            {
                double step = at[row[c]].X - at[row[c - 1]].X;
                if (Math.Abs(step - dx) > tol) return Refused($"Its columns are {Mm(dx)} and {Mm(step)} apart: two pitches, not one lattice.", true, at);
            }

        // Rectangular, or the odd rows half a pitch to the right.
        var kind = ArrayLatticeKind.Rect;
        double x0 = at[rows[0][0]].X;
        if (rows.Count > 1)
        {
            double odd = at[rows[1][0]].X - x0;
            if (Math.Abs(odd) > tol)
            {
                if (cols > 1 && Math.Abs(odd - dx / 2) <= tol) kind = ArrayLatticeKind.Tri;
                else if (cols > 1 && Math.Abs(odd + dx / 2) <= tol)
                    return Refused("Its odd rows are half a pitch to the LEFT of the even ones, and a triangular lattice here offsets them to the right. " +
                                   "Mirror the layout, or state RecordRows, RecordCols, RecordDx and RecordDy.", true, at);
                else return Refused($"Its rows are offset from each other by {Mm(odd)}, which is neither zero nor half the {Mm(dx)} pitch.", true, at);
            }
            for (int r = 0; r < rows.Count; r++)
            {
                double want = x0 + (kind == ArrayLatticeKind.Tri && r % 2 == 1 ? dx / 2 : 0);
                if (Math.Abs(at[rows[r][0]].X - want) > tol)
                    return Refused($"Row {r + 1} (from the top) starts {Mm(at[rows[r][0]].X - x0)} from the first, which no one lattice explains.", true, at);
            }
        }

        var portOf = rows.SelectMany(r => r).ToArray();
        // A single column has no column pitch; a single row no row pitch. Neither enters an array factor along it.
        var lattice = new ArrayLattice(rows.Count, cols, cols > 1 ? dx : recordDx ?? dy, rows.Count > 1 ? dy : recordDy ?? dx, kind);
        bool rowMajor = portOf.Select((p, e) => p == e).All(x => x);
        string note = $"The record's {n} ports stand on a {lattice}" +
                      (rowMajor ? ", numbered row-major from the top left." : $"; they are not numbered row-major, so element n takes port {string.Join(", ", portOf.Take(8).Select(p => p + 1))}{(n > 8 ? ", …" : "")} in turn.");
        return new RecordLatticeFit(lattice, portOf, at, true, null, note);
    }

    private static RecordLatticeFit Assumed(int n, int? rows, int? cols, double? dx, double? dy, bool none)
    {
        string why = none ? $"The record's ports' {NotRecorded}" : "Not every one of the record's ports has a recorded position";
        if (rows is not { } r || cols is not { } c || r * c != n || (c > 1 && dx is not > 0) || (r > 1 && dy is not > 0))
            return Refused($"{why}, so its lattice must be stated: give RecordRows and RecordCols (their product the record's {n} ports) and RecordDx and RecordDy.", false, []);
        var lattice = new ArrayLattice(r, c, dx ?? 0, dy ?? 0);
        var at = Enumerable.Range(0, n).Select(lattice.Position).ToArray();
        return new RecordLatticeFit(lattice, [.. Enumerable.Range(0, n)], at, false, null,
                                    $"{why}: its lattice is taken as the stated {lattice}, its ports ASSUMED numbered row-major from the top left and centred on its origin.");
    }

    private static RecordLatticeFit Refused(string why, bool recorded, IReadOnlyList<(double, double)> at) => new(null, [], at, recorded, why, why);

    /// <summary>The angle of the shortest port-to-port vectors from the nearest layout axis, degrees, when it is not a multiple of 30°
    /// (an axis, or a triangular lattice's diagonal); null when it is.</summary>
    private static double? Rotation((double X, double Y)[] at, double closest)
    {
        for (int i = 0; i < at.Length; i++)
            for (int j = i + 1; j < at.Length; j++)
            {
                if (Distance(at[i], at[j]) > closest * (1 + 1e-3)) continue;
                double a = Math.Atan2(at[j].Y - at[i].Y, at[j].X - at[i].X) * 180 / Math.PI;
                double m = ((a % 90) + 90) % 90;
                double off = Math.Min(m, 90 - m);
                if (off > 0.05 && Math.Abs(off - 30) > 0.05) return off;
            }
        return null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string Mm(double m) => $"{m * 1e3:G6} mm";
}
