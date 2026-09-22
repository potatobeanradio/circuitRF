namespace CircuitRF.Ui.Schematic;

/// <summary>
/// <b>Sort Placement</b> — lays a selection of UNCONNECTED components out on a grid in instance-name
/// order (R1, R2, …, R10 — numbers compared as numbers), somewhere on the sheet with room for all of
/// them. The common case is a heap of parts dropped in by Update Schematic from Layout or pasted in
/// from elsewhere, which a user then wants in an order they can find things in before wiring.
///
/// <para><b>Unconnected is a precondition, not a preference.</b> Moving a part that is wired pulls its
/// pins off the wires, which silently changes the circuit; a selection with any connected pin is
/// refused and the refusal names the parts.</para>
///
/// <para><b>Where the block goes.</b> A component's pins are what connect it, so the block must not
/// land where a pin could meet a wire, a net label or another component's pin. The block is tried
/// first where the selection already is, then at increasing distance from there; the first spot whose
/// rectangle — with a grid square of clearance — touches no other component's drawn extent, no wire
/// and no net label wins. Past a bounded search it goes to the right of everything on the sheet, where
/// nothing can be in the way. Canvas text and pictures are not obstacles: they connect nothing.</para>
///
/// <para>Framework-free; <see cref="Plan"/> is pure and the command applies its result.</para>
/// </summary>
public static class SchematicSortPlacement
{
    public sealed record Move(EditableComponent Component, double X, double Y);

    /// <param name="Moves">In name order.</param>
    /// <param name="Region">World extent of the arranged block — the caller brings it on screen.</param>
    public sealed record Result(IReadOnlyList<Move> Moves, (double MinX, double MinY, double MaxX, double MaxY) Region);

    private const int MaxSearchRings = 40;

    /// <summary>The selected components in the order the command lays them out. Non-component
    /// selection (wires, labels) is ignored.</summary>
    public static IReadOnlyList<EditableComponent> SelectedComponents(SchematicEditModel model, IEnumerable<string> selectedIds)
    {
        var ids = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        return model.Components.Where(c => ids.Contains(c.Id))
                    .OrderBy(c => c.InstanceName, NaturalNameComparer.Instance)
                    .ThenBy(c => c.Id, StringComparer.Ordinal)
                    .ToList();
    }

    /// <summary>Null when the selection can be sorted; otherwise the reason, fit for a tooltip.</summary>
    public static string? Refusal(SchematicEditModel model, SchematicModel render, IEnumerable<string> selectedIds)
    {
        var comps = SelectedComponents(model, selectedIds);
        if (comps.Count < 2) return "Select two or more components to sort.";

        var byId = render.Components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var wired = comps.Where(c => byId.TryGetValue(c.Id, out var rc)
                                     && rc.Ports.Any(p => p.State == PortConnectionState.Connected))
                         .Select(c => c.InstanceName).ToList();
        if (wired.Count == 0) return null;

        string names = string.Join(", ", wired.Take(3)) + (wired.Count > 3 ? $" and {wired.Count - 3} more" : "");
        return $"Sort Placement only moves unconnected components — {names} " +
               $"{(wired.Count == 1 ? "is" : "are")} connected, and moving {(wired.Count == 1 ? "it" : "them")} " +
               "would pull pins off their wires.";
    }

    public static Result? Plan(SchematicEditModel model, SchematicModel render, IEnumerable<string> selectedIds)
    {
        if (Refusal(model, render, selectedIds) is not null) return null;
        var comps = SelectedComponents(model, selectedIds);
        var byId = render.Components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        double grid = render.GridSize > 0 ? render.GridSize : 100.0;

        // Each part's extent relative to its own origin: drawn box plus every pin, so a pin that pokes
        // past the artwork is still inside the rectangle the search keeps clear.
        var rel = new List<(double MinX, double MinY, double MaxX, double MaxY)>(comps.Count);
        double selMinX = double.MaxValue, selMinY = double.MaxValue;
        foreach (var c in comps)
        {
            var e = Extent(byId.GetValueOrDefault(c.Id), c);
            rel.Add((e.MinX - c.X, e.MinY - c.Y, e.MaxX - c.X, e.MaxY - c.Y));
            selMinX = Math.Min(selMinX, e.MinX);
            selMinY = Math.Min(selMinY, e.MinY);
        }

        // One cell size for all, so the columns line up and the order reads at a glance. Two grid
        // squares between neighbours: snapping each origin can take up to half a square from that.
        double gap = 2 * grid;
        double cellW = Snap(rel.Max(r => r.MaxX - r.MinX) + gap, grid, up: true);
        double cellH = Snap(rel.Max(r => r.MaxY - r.MinY) + gap, grid, up: true);
        int cols = (int)Math.Ceiling(Math.Sqrt(comps.Count));
        int rows = (comps.Count + cols - 1) / cols;
        double blockW = cols * cellW, blockH = rows * cellH;

        var obstacles = Obstacles(render, new HashSet<string>(comps.Select(c => c.Id), StringComparer.Ordinal), grid);
        var (ox, oy) = FindRoom(Snap(selMinX, grid), Snap(selMinY, grid), blockW, blockH,
                                Math.Max(cellW, cellH), obstacles, grid);

        var moves = new List<Move>(comps.Count);
        for (int i = 0; i < comps.Count; i++)
        {
            double cx = ox + (i % cols) * cellW, cy = oy + (i / cols) * cellH;
            moves.Add(new Move(comps[i], Snap(cx - rel[i].MinX + grid, grid), Snap(cy - rel[i].MinY + grid, grid)));
        }
        return new Result(moves, (ox, oy, ox + blockW, oy + blockH));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Extent(SchematicComponent? rc, EditableComponent c)
    {
        if (rc is null) return (c.X - 200, c.Y - 200, c.X + 200, c.Y + 200);
        double minX = rc.FullBbMinX, minY = rc.FullBbMinY, maxX = rc.FullBbMaxX, maxY = rc.FullBbMaxY;
        foreach (var p in rc.Ports)
        {
            var (px, py) = SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, rc.X, rc.Y, rc.Rotation, rc.MirrorX);
            minX = Math.Min(minX, px); minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px); maxY = Math.Max(maxY, py);
        }
        return (minX, minY, maxX, maxY);
    }

    private readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Overlaps(Rect o) => MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY;
    }

    /// <summary>Everything a moved pin could connect to, grown by one grid square of clearance.</summary>
    private static List<Rect> Obstacles(SchematicModel render, HashSet<string> moving, double grid)
    {
        var list = new List<Rect>();
        foreach (var c in render.Components)
        {
            if (moving.Contains(c.Id)) continue;
            var e = Extent(c, new EditableComponent { X = c.X, Y = c.Y });
            list.Add(new Rect(e.MinX - grid, e.MinY - grid, e.MaxX + grid, e.MaxY + grid));
        }
        foreach (var w in render.Wires)
            list.Add(new Rect(w.BbMinX - grid, w.BbMinY - grid, w.BbMaxX + grid, w.BbMaxY + grid));
        foreach (var l in render.NetLabels)
            list.Add(new Rect(l.X - 2 * grid, l.Y - 2 * grid, l.X + 2 * grid, l.Y + 2 * grid));
        return list;
    }

    /// <summary>The first free top-left corner, searched in rings of growing distance around
    /// (<paramref name="x0"/>, <paramref name="y0"/>); past the search, right of everything.</summary>
    private static (double X, double Y) FindRoom(double x0, double y0, double w, double h, double step,
                                                 List<Rect> obstacles, double grid)
    {
        bool Free(double x, double y)
        {
            var r = new Rect(x, y, x + w, y + h);
            foreach (var o in obstacles) if (r.Overlaps(o)) return false;
            return true;
        }

        if (Free(x0, y0)) return (x0, y0);

        step = Snap(Math.Max(step / 2, grid), grid, up: true);
        for (int ring = 1; ring <= MaxSearchRings; ring++)
        {
            // Every cell on this ring, nearest-first, so the block lands as close to where the parts
            // were as the sheet allows.
            var candidates = new List<(double X, double Y, double D)>();
            for (int i = -ring; i <= ring; i++)
            for (int j = -ring; j <= ring; j++)
            {
                if (Math.Max(Math.Abs(i), Math.Abs(j)) != ring) continue;
                candidates.Add((x0 + i * step, y0 + j * step, (double)i * i + (double)j * j));
            }
            foreach (var (x, y, _) in candidates.OrderBy(c => c.D).ThenBy(c => c.Y).ThenBy(c => c.X))
                if (Free(x, y)) return (x, y);
        }

        double right = obstacles.Count == 0 ? x0 : obstacles.Max(o => o.MaxX);
        return (Snap(right + grid, grid, up: true), y0);
    }

    private static double Snap(double v, double grid, bool up = false) =>
        (up ? Math.Ceiling(v / grid) : Math.Round(v / grid)) * grid;

    /// <summary>R2 before R10: runs of digits compare by value, everything else ordinally and
    /// case-insensitively.</summary>
    public sealed class NaturalNameComparer : IComparer<string>
    {
        public static readonly NaturalNameComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
