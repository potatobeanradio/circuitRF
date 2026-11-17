using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Model.Core;

namespace CircuitRF.Ui.Docking;

/// <summary>The four document-arrangement commands at the top of the Window menu.</summary>
public enum TileArrangement
{
    /// <summary>Panes in a row, each full height.</summary>
    SideBySide,

    /// <summary>Panes one above another, each full width.</summary>
    Stacked,

    /// <summary>Columns of stacked panes — 2×2 for four documents, 3×2 for six.</summary>
    Grid,

    /// <summary>Every document — docked panes AND torn-off document windows — gathered into the main
    /// window's one tab strip: the inverse of the other three, and the way to tidy up a scattered
    /// arrangement in one step.</summary>
    MergeAll,
}

/// <summary>
/// What a document IS, for placement. The order of the members is the left-to-right (top-to-bottom)
/// order tiling puts them in: the circuit on the left, its physical views after it, results last.
/// </summary>
public enum TileRole
{
    Schematic = 0,
    Symbol,
    CellParameters,
    Layout,
    ThreeD,
    Technology,
    DataDisplay,
    Other,
}

/// <summary>One docked document as tiling sees it.</summary>
/// <param name="Document">The dockable itself.</param>
/// <param name="Role">Its kind, which decides its column.</param>
/// <param name="Cell">
/// The cell it belongs to — for a Data Display, the cell whose results it plots. Documents sharing a
/// cell sort together within a role, so in a grid a cell's schematic and its display land on the same
/// row. Null for a document with no cell (a technology, a loose file nothing links to).
/// </param>
/// <param name="WasVisible">True when it was the front tab of its pane before tiling.</param>
public readonly record struct TileCandidate(IDockable Document, TileRole Role, string? Cell, bool WasVisible);

/// <summary>
/// The result of planning: <see cref="Lines"/> are the outer split's children along
/// <see cref="Outer"/>; each line is a list of panes split the OTHER way; each pane is a tab list.
/// <see cref="Active"/> names the front tab of every pane, in pane order.
/// </summary>
public sealed record TilePlan(
    Orientation Outer,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<IDockable>>> Lines,
    IReadOnlyList<IDockable> Active)
{
    /// <summary>Every pane, line by line.</summary>
    public IEnumerable<IReadOnlyList<IDockable>> Panes => Lines.SelectMany(l => l);

    public int PaneCount => Lines.Sum(l => l.Count);
}

/// <summary>
/// Decides where each docked document goes when the user tiles the document area. Pure — it moves
/// nothing; <c>CircuitRfDockFactory.ArrangeDocuments</c> carries a plan out.
///
/// <para><b>Placement is by kind first, cell second.</b> Schematics go left (or top), then symbols,
/// layouts, 3D views and technologies, and Data Displays last — the order a design is worked in, and
/// the one that puts "edit here, Run, read there" side by side. Within a kind, documents are grouped
/// by cell in the order the cells' first tabs appear, and a Data Display counts as belonging to the
/// cell whose results it plots, so a grid puts each cell's schematic and its display on one row.</para>
///
/// <para><b>There is a pane limit, and past it documents share panes as tabs</b> — three panes side by
/// side or stacked, six (3×2) in a grid. A seventh pane on an ordinary screen is too small to edit in,
/// and a tile command that produces unusable panes is worse than one that tabs. When documents must
/// share, documents of the SAME kind share first, so the extra tabs are where a user would look for
/// them; when there are fewer kinds than panes, the largest kind is split until the panes are used.</para>
/// </summary>
public static class DocumentTiling
{
    /// <summary>Most panes in a row (Side by Side) or a column (Stacked).</summary>
    public const int MaxLinePanes = 3;

    /// <summary>Most panes in a grid: three columns of two.</summary>
    public const int MaxGridPanes = 6;

    /// <summary>The order tiling places documents in: kind, then cell, then current tab order.</summary>
    public static IReadOnlyList<TileCandidate> Order(IReadOnlyList<TileCandidate> candidates)
    {
        // A cell's rank is where its FIRST document appears in the current tabs, whatever its kind —
        // so a display linked to the first schematic sorts with it, not after every other cell.
        var cellRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            if (c.Cell is { Length: > 0 } cell && !cellRank.ContainsKey(cell))
                cellRank[cell] = cellRank.Count;

        return candidates
            .Select((c, i) => (c, i))
            .OrderBy(x => x.c.Role)
            .ThenBy(x => x.c.Cell is { Length: > 0 } cell ? cellRank[cell] : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.c)
            .ToList();
    }

    /// <summary>
    /// Plans <paramref name="arrangement"/> for <paramref name="candidates"/> (in current tab order).
    /// Returns null when there is nothing to arrange.
    /// </summary>
    /// <param name="focus">The document that has focus now; it stays the front tab of its pane.</param>
    public static TilePlan? Plan(IReadOnlyList<TileCandidate> candidates, TileArrangement arrangement, IDockable? focus = null)
    {
        if (candidates.Count == 0) return null;

        if (arrangement == TileArrangement.MergeAll)
        {
            // Current tab order, untouched (docked first, then each floating window's): undoing a tile
            // should not also shuffle the tabs.
            var all = candidates.Select(c => c.Document).ToList();
            return new TilePlan(
                Orientation.Horizontal,
                [[all]],
                [FrontOf(all, candidates, focus)]);
        }

        var ordered = Order(candidates);
        int cap = arrangement == TileArrangement.Grid ? MaxGridPanes : MaxLinePanes;
        var panes = Partition(ordered, Math.Min(ordered.Count, cap));

        IReadOnlyList<IReadOnlyList<IReadOnlyList<IDockable>>> lines = arrangement switch
        {
            TileArrangement.Grid => GridColumns(panes),
            _                    => panes.Select(p => (IReadOnlyList<IReadOnlyList<IDockable>>)[p]).ToList(),
        };

        return new TilePlan(
            arrangement == TileArrangement.Stacked ? Orientation.Vertical : Orientation.Horizontal,
            lines,
            lines.SelectMany(l => l).Select(p => FrontOf(p, candidates, focus)).ToList());
    }

    /// <summary>
    /// How many panes each grid column holds, left to right — column-major, earlier columns full:
    /// 3 → [2,1], 4 → [2,2], 5 → [2,2,1], 6 → [2,2,2]. Column-major so that, with kinds ordered left to
    /// right, three documents give the schematic and layout stacked on the left and the display
    /// full-height on the right.
    /// </summary>
    public static IReadOnlyList<int> GridShape(int panes)
    {
        if (panes <= 2) return Enumerable.Repeat(1, Math.Max(panes, 0)).ToList();
        int cols = panes <= 4 ? 2 : 3;
        int rows = (panes + cols - 1) / cols;

        var shape = new List<int>();
        int left = panes;
        for (int c = 0; c < cols && left > 0; c++)
        {
            int take = Math.Min(rows, left);
            shape.Add(take);
            left -= take;
        }
        return shape;
    }

    private static List<IReadOnlyList<IReadOnlyList<IDockable>>> GridColumns(List<IReadOnlyList<IDockable>> panes)
    {
        var columns = new List<IReadOnlyList<IReadOnlyList<IDockable>>>();
        int at = 0;
        foreach (int count in GridShape(panes.Count))
        {
            columns.Add(panes.GetRange(at, count));
            at += count;
        }
        return columns;
    }

    /// <summary>
    /// Splits the ordered documents into exactly <paramref name="paneCount"/> contiguous panes, keeping
    /// each kind together where it can: one pane per kind to start, then the two smallest NEIGHBOURING
    /// groups merged while there are too many, or the largest group halved while there are too few.
    /// </summary>
    private static List<IReadOnlyList<IDockable>> Partition(IReadOnlyList<TileCandidate> ordered, int paneCount)
    {
        var groups = new List<List<IDockable>>();
        TileRole? last = null;
        foreach (var c in ordered)
        {
            if (last != c.Role) groups.Add([]);
            groups[^1].Add(c.Document);
            last = c.Role;
        }

        while (groups.Count > paneCount)
        {
            int best = 0;
            for (int i = 1; i < groups.Count - 1; i++)
                if (groups[i].Count + groups[i + 1].Count < groups[best].Count + groups[best + 1].Count)
                    best = i;
            groups[best].AddRange(groups[best + 1]);
            groups.RemoveAt(best + 1);
        }

        while (groups.Count < paneCount)
        {
            int largest = 0;
            for (int i = 1; i < groups.Count; i++)
                if (groups[i].Count > groups[largest].Count) largest = i;
            if (groups[largest].Count < 2) break;

            var g = groups[largest];
            int half = (g.Count + 1) / 2;
            groups[largest] = g.GetRange(0, half);
            groups.Insert(largest + 1, g.GetRange(half, g.Count - half));
        }

        return groups.Select(g => (IReadOnlyList<IDockable>)g).ToList();
    }

    /// <summary>The focused document if it is in this pane; else one that was already in front; else
    /// the first — so tiling hides as little of what the user was looking at as it can.</summary>
    private static IDockable FrontOf(IReadOnlyList<IDockable> pane, IReadOnlyList<TileCandidate> candidates, IDockable? focus)
    {
        if (focus is not null && pane.Contains(focus)) return focus;
        foreach (var d in pane)
            if (candidates.Any(c => ReferenceEquals(c.Document, d) && c.WasVisible)) return d;
        return pane[0];
    }
}
