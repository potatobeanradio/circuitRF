// The R-tree spatial index (docs/design/layout-view.md §5.2 R11, docs/sonnet-briefs/
// brief-L2b-spatial-index.md). Framework-free — no Avalonia/Skia types, pure C# over long-DBU Bboxes.
//
// R-L2b-1: ONE tree over every shape (not per-layer). §5.2 suggests per-layer indices so hidden layers
// cost zero, but L2a's own scenario is 200 layers — per-layer means ~200 tree descents per query
// (~2,200 node visits minimum even for an empty viewport) and multiplies incremental-maintenance work
// by 200 on every edit. Consumers filter the single tree's candidates by layer visibility/selectability
// afterwards instead — trivial on an already-small result set.

using System.Collections.Generic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// A bulk-loadable, incrementally-maintainable R-tree over <c>(bbox, shapeIndex)</c> entries — one per
/// <see cref="LayoutView.Shapes"/> entry, keyed by that shape's CURRENT list index.
///
/// <b>Two independent correctness mechanisms, deliberately layered:</b>
/// <list type="number">
/// <item><see cref="Apply"/> — called from <see cref="LayoutView.NotifyChanged"/>, the "one hook"
/// R-L2b-2 asks for. Given a <see cref="LayoutChangeInfo"/>, it updates the tree incrementally for the
/// safe cases (<see cref="LayoutChangeKind.Appended"/>/<see cref="LayoutChangeKind.RemovedTrailing"/>/
/// <see cref="LayoutChangeKind.Updated"/>) or does a full STR rebuild otherwise. This is a PERFORMANCE
/// optimization for the interactive hot paths (draw, move/nudge) — it keeps the tree in sync
/// proactively so the next query is already O(log n), not O(n log n).</item>
/// <item><see cref="EnsureFresh"/> — called at the START of every query. It is the actual correctness
/// guarantee: if <c>shapes.Count</c> does not match what the index last synced to (including "never
/// synced at all"), it rebuilds fully from <paramref name="shapes"/> before querying. This is what
/// keeps every test that builds a <see cref="LayoutView"/> via direct <c>Shapes.Add(...)</c> (the
/// overwhelming majority of this project's ~2,300 Layout tests, which never call
/// <see cref="LayoutView.NotifyChanged"/> at all) correct with ZERO test changes: the first query after
/// such a view is fully built simply rebuilds once, lazily, and is queried against the correct content
/// from then on for that view instance. <see cref="Apply"/> could be entirely absent and every query
/// would still be *correct* — just always paying a full rebuild instead of an O(log n) update.</item>
/// </list>
///
/// The count-based staleness check is a known, deliberate simplification: it does not catch a shape
/// being mutated in place at a stable index WITHOUT going through <see cref="LayoutView.NotifyChanged"/>
/// (same count, different geometry). No production code and no existing test does this — every
/// mutation path in this codebase already calls <c>NotifyChanged</c> (that is how the renderer/canvas
/// repaint at all today) — so this is a documented, not a theoretical, boundary.
/// </summary>
public sealed class LayoutSpatialIndex
{
    /// <summary>Max entries per leaf / max children per internal node.</summary>
    private const int MaxEntries = 16;

    private readonly record struct LeafEntry(Bbox Box, int Index);

    private sealed class Node
    {
        public Node? Parent;
        public bool IsLeaf;
        public Bbox Bounds = Bbox.Empty;
        public List<Node>? Children;
        public List<LeafEntry>? Entries;
    }

    private Node? _root;
    private readonly Dictionary<int, Node> _leafOf = new();
    private int _syncedCount = -1;   // -1 = never built
    private int _churnSinceRebuild;

    // ── Test/diagnostic hooks (internal, InternalsVisibleTo CircuitRF.Ui.Tests) ──────────────────
    internal int FullRebuildCount { get; private set; }
    internal int IncrementalApplyCount { get; private set; }

    private bool IsBuilt => _root is not null && _syncedCount >= 0;

    // ── Public API ────────────────────────────────────────────────────────────────────────────

    /// <summary>Called from <see cref="LayoutView.NotifyChanged"/> — the proactive maintenance hook.
    /// Never required for correctness (see the type doc comment), but is what keeps interactive editing
    /// at scale from paying a full rebuild on every frame.</summary>
    public void Apply(IReadOnlyList<LayoutShape> shapes, LayoutChangeInfo info)
    {
        if (!IsBuilt || info.Kind == LayoutChangeKind.Full)
        {
            RebuildFull(shapes);
            return;
        }

        switch (info.Kind)
        {
            case LayoutChangeKind.Appended:
                for (int i = info.StartIndex; i < info.StartIndex + info.Count && i < shapes.Count; i++)
                    InsertEntry(i, ConservativeBboxOf(shapes[i]));
                break;

            case LayoutChangeKind.RemovedTrailing:
                for (int i = info.StartIndex + info.Count - 1; i >= info.StartIndex; i--)
                    RemoveEntry(i);
                break;

            case LayoutChangeKind.Updated:
                foreach (var i in info.Indices!)
                {
                    if (i < 0 || i >= shapes.Count) continue;
                    RemoveEntry(i);
                    InsertEntry(i, ConservativeBboxOf(shapes[i]));
                }
                break;
        }

        _syncedCount = shapes.Count;
        IncrementalApplyCount++;

        // R-L2b-2: repeated incremental insertion (no rebalancing on delete, a simple split on
        // overflow) degrades an R-tree's query quality over time. Rebuild once churn is large relative
        // to the tree's own size, rather than letting it degrade indefinitely.
        if (_churnSinceRebuild > System.Math.Max(2000, _syncedCount / 4))
            RebuildFull(shapes);
    }

    /// <summary>The correctness guarantee — see the type doc comment. Safe (and cheap: O(1)) to call
    /// before every single query.</summary>
    public void EnsureFresh(IReadOnlyList<LayoutShape> shapes)
    {
        if (!IsBuilt || _syncedCount != shapes.Count)
            RebuildFull(shapes);
    }

    /// <summary>All shape indices whose (conservative) bbox intersects <paramref name="rect"/>, ascending
    /// — the ONE query primitive every consumer (marquee, hit-test, render culling) uses. An empty
    /// <paramref name="rect"/> matches nothing, exactly like <see cref="Bbox.Intersects"/>'s own contract.
    /// Each consumer applies its own EXACT test to the returned candidates — the index only decides what
    /// is CONSIDERED, never the outcome (R-L2b-3).</summary>
    public IReadOnlyList<int> QueryIntersecting(IReadOnlyList<LayoutShape> shapes, Bbox rect)
    {
        EnsureFresh(shapes);
        var result = new List<int>();
        if (_root is not null) QueryNode(_root, rect, result);
        result.Sort();
        return result;
    }

    private static void QueryNode(Node node, Bbox rect, List<int> result)
    {
        if (!node.Bounds.Intersects(rect)) return;
        if (node.IsLeaf)
        {
            foreach (var e in node.Entries!)
                if (e.Box.Intersects(rect)) result.Add(e.Index);
        }
        else
        {
            foreach (var c in node.Children!) QueryNode(c, rect, result);
        }
    }

    // ── Conservative per-shape bbox (index-only — never used for the exact per-consumer test) ─────

    /// <summary>
    /// The bbox stored in the tree — a safe (never-too-small) upper bound of whatever a consumer's own
    /// exact test could match, so over-inclusion in the candidate set is the only failure mode culling
    /// can ever introduce. Identical to <see cref="LayoutGeometry.BboxOf"/> for every shape kind except
    /// <see cref="LabelShape"/>, whose EXACT rendered/hit-testable extent depends on real font metrics
    /// (<c>LayoutRenderer.MeasureLabelWorldBbox</c>, Skia — unavailable to this framework-free file and
    /// to headless tests) or an approximate character-count formula
    /// (<c>LayoutHitTest.LabelHitBbox</c>, 0.62-of-height-per-character) — <see cref="LayoutGeometry.
    /// BboxOf"/>'s OWN label case is a zero-size point (correct for the marquee predicate, which is
    /// re-applied verbatim on candidates and must see the exact same point bbox marquee always has —
    /// see <see cref="LayoutEditorViewModel.ComputeMarqueeSelection"/>). A generous, rotation- and
    /// font-agnostic square pad — <c>(charCount+1) × height</c> in all four directions — safely bounds
    /// both of those narrower notions without needing to duplicate either one here.
    /// </summary>
    internal static Bbox ConservativeBboxOf(LayoutShape shape)
    {
        if (shape is LabelShape label)
        {
            if (string.IsNullOrEmpty(label.Text) || label.Height <= 0)
                return new Bbox(label.X, label.Y, label.X, label.Y);
            long pad = (label.Text.Length + 1) * label.Height;
            return new Bbox(label.X - pad, label.Y - pad, label.X + pad, label.Y + pad);
        }
        return LayoutGeometry.BboxOf(shape);
    }

    // ── Full rebuild — STR (Sort-Tile-Recursive) bulk load, O(n log n) ──────────────────────────

    private void RebuildFull(IReadOnlyList<LayoutShape> shapes)
    {
        _leafOf.Clear();
        var entries = new List<LeafEntry>(shapes.Count);
        for (int i = 0; i < shapes.Count; i++)
        {
            var bb = ConservativeBboxOf(shapes[i]);
            if (!bb.IsEmpty) entries.Add(new LeafEntry(bb, i));
        }

        _root = entries.Count == 0 ? new Node { IsLeaf = true, Bounds = Bbox.Empty } : BuildStrTree(entries);
        _syncedCount = shapes.Count;
        _churnSinceRebuild = 0;
        FullRebuildCount++;
    }

    private Node BuildStrTree(List<LeafEntry> entries)
    {
        var level = StrPackLeaves(entries);
        while (level.Count > 1)
            level = StrPackInternal(level);
        return level[0];
    }

    private List<Node> StrPackLeaves(List<LeafEntry> entries)
    {
        int numLeaves = (entries.Count + MaxEntries - 1) / MaxEntries;
        int numSlices = System.Math.Max(1, (int)System.Math.Ceiling(System.Math.Sqrt(numLeaves)));
        int sliceCapacity = numSlices * MaxEntries;

        entries.Sort(static (a, b) => CenterX(a.Box).CompareTo(CenterX(b.Box)));

        var leaves = new List<Node>(numLeaves);
        for (int sliceStart = 0; sliceStart < entries.Count; sliceStart += sliceCapacity)
        {
            int sliceCount = System.Math.Min(sliceCapacity, entries.Count - sliceStart);
            var slice = entries.GetRange(sliceStart, sliceCount);
            slice.Sort(static (a, b) => CenterY(a.Box).CompareTo(CenterY(b.Box)));

            for (int i = 0; i < slice.Count; i += MaxEntries)
            {
                int count = System.Math.Min(MaxEntries, slice.Count - i);
                var leafEntries = slice.GetRange(i, count);
                var node = new Node { IsLeaf = true, Entries = leafEntries };
                var bb = Bbox.Empty;
                foreach (var e in leafEntries) { bb = bb.Union(e.Box); _leafOf[e.Index] = node; }
                node.Bounds = bb;
                leaves.Add(node);
            }
        }
        return leaves;
    }

    private static List<Node> StrPackInternal(List<Node> children)
    {
        int numParents = (children.Count + MaxEntries - 1) / MaxEntries;
        int numSlices = System.Math.Max(1, (int)System.Math.Ceiling(System.Math.Sqrt(numParents)));
        int sliceCapacity = numSlices * MaxEntries;

        children.Sort(static (a, b) => CenterX(a.Bounds).CompareTo(CenterX(b.Bounds)));

        var parents = new List<Node>(numParents);
        for (int sliceStart = 0; sliceStart < children.Count; sliceStart += sliceCapacity)
        {
            int sliceCount = System.Math.Min(sliceCapacity, children.Count - sliceStart);
            var slice = children.GetRange(sliceStart, sliceCount);
            slice.Sort(static (a, b) => CenterY(a.Bounds).CompareTo(CenterY(b.Bounds)));

            for (int i = 0; i < slice.Count; i += MaxEntries)
            {
                int count = System.Math.Min(MaxEntries, slice.Count - i);
                var group = slice.GetRange(i, count);
                var node = new Node { IsLeaf = false, Children = group };
                var bb = Bbox.Empty;
                foreach (var c in group) { bb = bb.Union(c.Bounds); c.Parent = node; }
                node.Bounds = bb;
                parents.Add(node);
            }
        }
        return parents;
    }

    private static double CenterX(Bbox b) => (b.MinX + (double)b.MaxX) / 2.0;
    private static double CenterY(Bbox b) => (b.MinY + (double)b.MaxY) / 2.0;
    private static double Area(Bbox b) => b.IsEmpty ? 0.0 : (b.MaxX - (double)b.MinX) * (b.MaxY - (double)b.MinY);

    // ── Incremental insert (ChooseLeaf + enlarge-on-the-way-up + split-on-overflow) ──────────────

    private void InsertEntry(int index, Bbox bbox)
    {
        if (bbox.IsEmpty) return; // matches every consumer's own "skip empty bbox" convention

        _root ??= new Node { IsLeaf = true, Bounds = Bbox.Empty };

        var leaf = ChooseLeaf(_root, bbox);
        (leaf.Entries ??= []).Add(new LeafEntry(bbox, index));
        leaf.Bounds = leaf.Bounds.Union(bbox);
        _leafOf[index] = leaf;
        EnlargeAncestors(leaf);

        if (leaf.Entries.Count > MaxEntries)
            SplitLeaf(leaf);

        _churnSinceRebuild++;
    }

    /// <summary>Descends choosing, at each level, the child needing least area enlargement to cover
    /// <paramref name="bbox"/> (ties broken by smaller current area) — the standard Guttman ChooseLeaf
    /// heuristic.</summary>
    private static Node ChooseLeaf(Node node, Bbox bbox)
    {
        while (!node.IsLeaf)
        {
            Node? best = null;
            double bestEnlargement = double.MaxValue, bestArea = double.MaxValue;
            foreach (var c in node.Children!)
            {
                double enlargement = Area(c.Bounds.Union(bbox)) - Area(c.Bounds);
                double area = Area(c.Bounds);
                if (enlargement < bestEnlargement || (enlargement == bestEnlargement && area < bestArea))
                {
                    best = c; bestEnlargement = enlargement; bestArea = area;
                }
            }
            node = best!;
        }
        return node;
    }

    private static void EnlargeAncestors(Node node)
    {
        var child = node;
        var p = node.Parent;
        while (p is not null)
        {
            var enlarged = p.Bounds.Union(child.Bounds);
            if (enlarged.Equals(p.Bounds)) break; // already covers it — so does every ancestor above p
            p.Bounds = enlarged;
            child = p;
            p = p.Parent;
        }
    }

    /// <summary>Overflow split — deliberately a simple, always-terminating, always-correct "sort along
    /// the wider axis and bisect" rather than Guttman's quadratic PickSeeds: node QUALITY after a split
    /// is a soft concern here (R-L2b-2's churn-triggered full rebuild is the real quality backstop),
    /// while CORRECTNESS (every entry lands in exactly one of the two resulting nodes, bounds are exact
    /// unions) is not.</summary>
    private void SplitLeaf(Node leaf)
    {
        var entries = leaf.Entries!;
        var overall = Bbox.Empty;
        foreach (var e in entries) overall = overall.Union(e.Box);
        bool splitOnX = (overall.MaxX - overall.MinX) >= (overall.MaxY - overall.MinY);

        entries.Sort(splitOnX
            ? static (a, b) => CenterX(a.Box).CompareTo(CenterX(b.Box))
            : static (a, b) => CenterY(a.Box).CompareTo(CenterY(b.Box)));

        int mid = entries.Count / 2;
        var groupB = entries.GetRange(mid, entries.Count - mid);
        entries.RemoveRange(mid, entries.Count - mid); // leaf keeps group A in place

        var sibling = new Node { IsLeaf = true, Entries = groupB, Parent = leaf.Parent };
        var boundsA = Bbox.Empty; foreach (var e in entries) boundsA = boundsA.Union(e.Box);
        var boundsB = Bbox.Empty; foreach (var e in groupB) { boundsB = boundsB.Union(e.Box); _leafOf[e.Index] = sibling; }
        leaf.Bounds = boundsA;
        sibling.Bounds = boundsB;

        AttachSplitSibling(leaf, sibling);
    }

    private void SplitInternal(Node node)
    {
        var children = node.Children!;
        var overall = Bbox.Empty;
        foreach (var c in children) overall = overall.Union(c.Bounds);
        bool splitOnX = (overall.MaxX - overall.MinX) >= (overall.MaxY - overall.MinY);

        children.Sort(splitOnX
            ? static (a, b) => CenterX(a.Bounds).CompareTo(CenterX(b.Bounds))
            : static (a, b) => CenterY(a.Bounds).CompareTo(CenterY(b.Bounds)));

        int mid = children.Count / 2;
        var groupB = children.GetRange(mid, children.Count - mid);
        children.RemoveRange(mid, children.Count - mid);

        var sibling = new Node { IsLeaf = false, Children = groupB, Parent = node.Parent };
        var boundsA = Bbox.Empty; foreach (var c in children) boundsA = boundsA.Union(c.Bounds);
        var boundsB = Bbox.Empty; foreach (var c in groupB) { boundsB = boundsB.Union(c.Bounds); c.Parent = sibling; }
        node.Bounds = boundsA;
        sibling.Bounds = boundsB;

        AttachSplitSibling(node, sibling);
    }

    /// <summary>Links a freshly-split-off sibling into the parent (or grows a new root when
    /// <paramref name="original"/> WAS the root), then propagates enlargement and recurses into the
    /// parent's own overflow — exactly Guttman's AdjustTree, minus the "tighten on the way up" half
    /// (deliberately not needed for correctness — see the type doc comment).</summary>
    private void AttachSplitSibling(Node original, Node sibling)
    {
        var parent = original.Parent;
        if (parent is null)
        {
            var newRoot = new Node { IsLeaf = false, Children = [original, sibling] };
            original.Parent = newRoot;
            sibling.Parent = newRoot;
            newRoot.Bounds = original.Bounds.Union(sibling.Bounds);
            _root = newRoot;
            return;
        }

        parent.Children!.Add(sibling);
        parent.Bounds = parent.Bounds.Union(sibling.Bounds);
        EnlargeAncestors(parent);

        if (parent.Children.Count > MaxEntries)
            SplitInternal(parent);
    }

    // ── Remove (by current index — O(1) lookup via the reverse map, no rebalancing) ──────────────

    private void RemoveEntry(int index)
    {
        if (!_leafOf.TryGetValue(index, out var leaf)) return;
        leaf.Entries!.RemoveAll(e => e.Index == index);
        _leafOf.Remove(index);
        // Deliberately no bounds-shrinking / rebalancing here — an over-large ancestor bbox after a
        // delete only ever costs query EFFICIENCY, never correctness (a query still finds every real
        // match; it may visit a few extra, now-empty subtrees). R-L2b-2's churn-triggered rebuild is
        // the quality backstop, not per-delete rebalancing.
        _churnSinceRebuild++;
    }
}
