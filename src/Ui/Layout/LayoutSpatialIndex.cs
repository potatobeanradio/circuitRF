// The R-tree spatial index (docs/design/layout-view.md §5.2 R11, docs/sonnet-briefs/
// brief-L2b-spatial-index.md; extended for instances by brief-L3a-instances-and-arrays.md R-L3a-4).
// Framework-free — no Avalonia/Skia types, pure C# over long-DBU Bboxes.
//
// R-L2b-1: ONE tree over every shape (not per-layer). §5.2 suggests per-layer indices so hidden layers
// cost zero, but L2a's own scenario is 200 layers — per-layer means ~200 tree descents per query
// (~2,200 node visits minimum even for an empty viewport) and multiplies incremental-maintenance work
// by 200 on every edit. Consumers filter the single tree's candidates by layer visibility/selectability
// afterwards instead — trivial on an already-small result set.
//
// R-L3a-4: instances live in the SAME tree as shapes, discriminated by a Kind tag on each entry,
// rather than a second tree. The two kinds have DELIBERATELY different freshness strategies, because
// their scale characteristics are opposite:
//   - Shapes: up to 10^5-10^6 entries. Freshness is the existing L2b design — Apply() incrementally
//     patches the tree per LayoutChangeInfo, EnsureFresh only forces a full STR rebuild when the
//     shape COUNT itself is unexpected (a cheap O(1) check on every query). Completely UNCHANGED by
//     this phase.
//   - Instances: typically a handful to a few hundred, even in a design with millions of shapes
//     reached through arrays (that compression is the whole point of R-L3a-3). Their bboxes also
//     depend on external resolution state (a referenced cell's geometry, or whether it currently
//     resolves at all) that nothing about "did LayoutView.Instances.Count change" can detect on its
//     own — R-L3a-4's "EnsureFresh must account for a resolution change" requirement. Given how rare
//     instances are, the simplest CORRECT answer is also cheap: whenever instance freshness is even
//     suspect (count changed, an explicit dirty mark, or the caller-supplied resolution-version token
//     ticked), drop every Instance-kind entry and reinsert all of them fresh via the same incremental
//     insert machinery shapes use for edits — O(instances log n), never touching a single shape entry.

using System.Collections.Generic;

using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Layout;

public sealed class LayoutSpatialIndex
{
    /// <summary>
    /// Every public entry point below is serialized on this — the index is read and WRITTEN from two
    /// threads, and nothing else in this file would make that safe.
    ///
    /// <para><b>Owner-reported crash, 2026-08-22</b> (<c>IndexOutOfRangeException</c> inside
    /// <c>Dictionary.TryInsert</c>, from <see cref="StrPackLeaves"/>, on a plain Delete): Avalonia
    /// runs <c>LayoutCanvas</c>'s <c>ICustomDrawOperation.Render</c> on the RENDER thread, and
    /// <c>LayoutRenderer.Draw</c>'s culling query goes straight through
    /// <see cref="QueryIntersecting(IReadOnlyList{LayoutShape}, Bbox)"/> — which is not a read-only
    /// operation: it self-heals a stale index by rebuilding it. A delete is precisely the case that
    /// arms both sides at once — <c>Shapes.RemoveAt</c> makes the count disagree (so the next frame
    /// rebuilds on the render thread) BEFORE <see cref="LayoutView.NotifyChanged"/> rebuilds it on
    /// the UI thread. Two threads then write <see cref="_leafOf"/> at the same time, and a
    /// half-resized Dictionary throws from an index its own bucket array no longer has.</para>
    ///
    /// <para>The lock is deliberately HERE rather than at the call sites: hit-test, marquee, snapping,
    /// DRC and the renderer all query this index, and self-healing means every one of them is a
    /// potential writer. A rule that every caller must remember to take a lock is a rule that gets one
    /// call site wrong.</para>
    ///
    /// <para>Never call out to unknown code while holding it — see
    /// <see cref="QueryIntersecting(IReadOnlyList{LayoutShape}, IReadOnlyList{LayoutInstance},
    /// Func{LayoutInstance, Bbox}, long, Bbox)"/>, which resolves every instance bbox BEFORE taking
    /// it, because that callback reaches the cell resolver and the file system.</para>
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Max entries per leaf / max children per internal node.</summary>
    private const int MaxEntries = 16;

    private readonly record struct LeafEntry(Bbox Box, SpatialEntryKind Kind, int Index);

    private sealed class Node
    {
        public Node? Parent;
        public bool IsLeaf;
        public Bbox Bounds = Bbox.Empty;
        public List<Node>? Children;
        public List<LeafEntry>? Entries;
    }

    private Node? _root;
    private readonly Dictionary<(SpatialEntryKind Kind, int Index), Node> _leafOf = new();
    private int _syncedCount = -1;   // -1 = never built (shapes)
    private int _churnSinceRebuild;

    private int _syncedInstanceCount = -1;
    private bool _instancesDirty;
    private long _syncedResolutionVersion = -1;

    // ── Test/diagnostic hooks (internal, InternalsVisibleTo CircuitRF.Ui.Tests) ──────────────────
    internal int FullRebuildCount { get; private set; }
    internal int IncrementalApplyCount { get; private set; }
    internal int InstanceRefreshCount { get; private set; }

    private bool IsBuilt => _root is not null && _syncedCount >= 0;

    // ── Public API — shapes (L2b, unchanged) ─────────────────────────────────────────────────────

    /// <summary>Called from <see cref="LayoutView.NotifyChanged"/> — the proactive maintenance hook.
    /// Never required for correctness (see the type doc comment), but is what keeps interactive editing
    /// at scale from paying a full rebuild on every frame.</summary>
    public void Apply(IReadOnlyList<LayoutShape> shapes, LayoutChangeInfo info)
    {
        lock (_gate) ApplyLocked(shapes, info);
    }

    private void ApplyLocked(IReadOnlyList<LayoutShape> shapes, LayoutChangeInfo info)
    {
        if (!IsBuilt || info.Kind == LayoutChangeKind.Full)
        {
            RebuildFullShapes(shapes);
            return;
        }

        switch (info.Kind)
        {
            case LayoutChangeKind.Appended:
                for (int i = info.StartIndex; i < info.StartIndex + info.Count && i < shapes.Count; i++)
                    InsertEntry(SpatialEntryKind.Shape, i, ConservativeBboxOf(shapes[i]));
                break;

            case LayoutChangeKind.RemovedTrailing:
                for (int i = info.StartIndex + info.Count - 1; i >= info.StartIndex; i--)
                    RemoveEntry(SpatialEntryKind.Shape, i);
                break;

            case LayoutChangeKind.Updated:
                foreach (var i in info.Indices!)
                {
                    if (i < 0 || i >= shapes.Count) continue;
                    RemoveEntry(SpatialEntryKind.Shape, i);
                    InsertEntry(SpatialEntryKind.Shape, i, ConservativeBboxOf(shapes[i]));
                }
                break;

            case LayoutChangeKind.InstancesChanged:
                return; // shapes untouched — see LayoutView.NotifyChanged's routing
        }

        _syncedCount = shapes.Count;
        IncrementalApplyCount++;

        // R-L2b-2: repeated incremental insertion (no rebalancing on delete, a simple split on
        // overflow) degrades an R-tree's query quality over time. Rebuild once churn is large relative
        // to the tree's own size, rather than letting it degrade indefinitely.
        if (_churnSinceRebuild > System.Math.Max(2000, _syncedCount / 4))
            RebuildFullShapes(shapes);
    }

    /// <summary>Marks the instance portion of the index stale — call after any instance-list mutation
    /// (add/move/delete/array-edit/retarget/undo/redo). Cheap: the next query does an O(instances log n)
    /// refresh, never a full-tree rebuild.</summary>
    public void MarkInstancesDirty() { lock (_gate) _instancesDirty = true; }

    /// <summary>Shape-only query (every pre-L3a consumer, and every pre-L3a test) — behaviorally
    /// identical to the original L2b method: candidates are exactly the Shape-kind entries whose
    /// stored bbox intersects <paramref name="rect"/>, ascending index. Instance entries that may
    /// also live in the tree (a document that also happens to have instances) are simply filtered out
    /// here, never returned.</summary>
    public IReadOnlyList<int> QueryIntersecting(IReadOnlyList<LayoutShape> shapes, Bbox rect)
    {
        lock (_gate)
        {
            if (!IsBuilt || _syncedCount != shapes.Count)
                RebuildFullShapes(shapes);

            var result = new List<int>();
            if (_root is not null) QueryNode(_root, rect, SpatialEntryKind.Shape, result);
            result.Sort();
            return result;
        }
    }

    /// <summary>The combined query (R-L3a-4) — every L3a-aware consumer (render culling, hit-test,
    /// marquee) uses this instead of the shape-only overload once instances exist to see at all.
    /// <paramref name="instanceBboxOf"/> computes one instance's full effective bbox (its resolved
    /// sub-cell's geometry, transformed and array-expanded — see <c>CellHierarchy.InstanceBbox</c>);
    /// <paramref name="resolutionVersion"/> is an opaque freshness token the caller supplies (this type
    /// deliberately does not know about <c>CellLayoutResolver</c> — every caller passes
    /// <c>CellLayoutResolver.Generation</c> directly) so a resolution change anywhere invalidates every
    /// open document's instance entries on their next query, not just the one that triggered it.</summary>
    public IReadOnlyList<LayoutSpatialEntry> QueryIntersecting(
        IReadOnlyList<LayoutShape> shapes,
        IReadOnlyList<LayoutInstance> instances,
        Func<LayoutInstance, Bbox> instanceBboxOf,
        long resolutionVersion,
        Bbox rect)
    {
        // instanceBboxOf resolves the referenced cell and unions every shape in it (CellHierarchy.
        // InstanceBbox — uncached by design), so it is both too expensive to run on a query that
        // changes nothing AND far too heavy to run under _gate, where it would hold the index the
        // render thread needs every frame across a file system walk and put the resolver's own locks
        // underneath this one. Hence: check staleness, resolve OUTSIDE the lock only if that says so,
        // then re-check under the lock — and if the answer changed in between, resolve and go round
        // once more. At most two passes: the second always arrives holding resolved boxes.
        Bbox[]? instanceBoxes = null;

        while (true)
        {
            if (instanceBoxes is null && InstanceSideLooksStale(shapes, instances, resolutionVersion))
                instanceBoxes = ResolveInstanceBoxes(instances, instanceBboxOf);

            lock (_gate)
            {
                bool shapesStale = !IsBuilt || _syncedCount != shapes.Count;

                // A full shape rebuild replaces _root wholesale — any previously-tracked instance
                // entries are discarded along with it, so they are unconditionally treated as stale
                // too, regardless of whether their own freshness signal actually fired.
                bool instancesStale = shapesStale
                    || instances.Count != _syncedInstanceCount || _instancesDirty
                    || resolutionVersion != _syncedResolutionVersion;

                if (!instancesStale || instanceBoxes is not null)
                {
                    if (shapesStale)    RebuildFullShapes(shapes);

                    // The boxes were resolved outside the lock, so the instance list may have changed
                    // length since. RefreshInstances records the length it actually indexed, which
                    // makes the entries and the bookkeeping self-consistent and makes the NEXT query
                    // see the mismatch and refresh again; both index consumers already skip an entry
                    // whose index the live list no longer has.
                    if (instancesStale) RefreshInstances(instanceBoxes!, resolutionVersion);

                    var result = new List<LayoutSpatialEntry>();
                    if (_root is not null) QueryNodeAll(_root, rect, result);
                    result.Sort(static (a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Kind.CompareTo(b.Kind));
                    return result;
                }
            }

            instanceBoxes = ResolveInstanceBoxes(instances, instanceBboxOf);
        }
    }

    /// <summary>The same staleness question the locked section asks, read cheaply so the expensive
    /// bbox resolution can be skipped entirely on the overwhelmingly common query that changes
    /// nothing. Racy on purpose — it is re-asked under the lock, which is what decides.</summary>
    private bool InstanceSideLooksStale(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, long resolutionVersion)
    {
        lock (_gate)
            return !IsBuilt || _syncedCount != shapes.Count
                || instances.Count != _syncedInstanceCount || _instancesDirty
                || resolutionVersion != _syncedResolutionVersion;
    }

    private static Bbox[] ResolveInstanceBoxes(IReadOnlyList<LayoutInstance> instances, Func<LayoutInstance, Bbox> instanceBboxOf)
    {
        var boxes = new Bbox[instances.Count];
        for (int i = 0; i < boxes.Length; i++) boxes[i] = instanceBboxOf(instances[i]);
        return boxes;
    }

    private static void QueryNode(Node node, Bbox rect, SpatialEntryKind kind, List<int> result)
    {
        if (!node.Bounds.Intersects(rect)) return;
        if (node.IsLeaf)
        {
            foreach (var e in node.Entries!)
                if (e.Kind == kind && e.Box.Intersects(rect)) result.Add(e.Index);
        }
        else
        {
            foreach (var c in node.Children!) QueryNode(c, rect, kind, result);
        }
    }

    private static void QueryNodeAll(Node node, Bbox rect, List<LayoutSpatialEntry> result)
    {
        if (!node.Bounds.Intersects(rect)) return;
        if (node.IsLeaf)
        {
            foreach (var e in node.Entries!)
                if (e.Box.Intersects(rect)) result.Add(new LayoutSpatialEntry(e.Kind, e.Index));
        }
        else
        {
            foreach (var c in node.Children!) QueryNodeAll(c, rect, result);
        }
    }

    /// <summary>Removes every currently-tracked Instance-kind entry, then inserts a fresh one per
    /// <paramref name="instances"/> — never touches a Shape-kind entry or the shape side's own
    /// freshness bookkeeping. O(instances log n): correct and cheap given how rare instances are
    /// relative to shapes (see the type doc comment).</summary>
    private void RefreshInstances(Bbox[] instanceBoxes, long resolutionVersion)
    {
        var staleKeys = _leafOf.Keys.Where(k => k.Kind == SpatialEntryKind.Instance).ToList();
        foreach (var k in staleKeys) RemoveEntry(k.Kind, k.Index);

        for (int i = 0; i < instanceBoxes.Length; i++)
            InsertEntry(SpatialEntryKind.Instance, i, instanceBoxes[i]);

        _syncedInstanceCount = instanceBoxes.Length;
        _instancesDirty = false;
        _syncedResolutionVersion = resolutionVersion;
        InstanceRefreshCount++;
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
    /// <summary>A box that intersects every query — big enough to never prune, small enough that
    /// its own width arithmetic stays far from overflow (1e15 nm is 10^6 km).</summary>
    private static readonly Bbox Everywhere = new(-1_000_000_000_000_000L, -1_000_000_000_000_000L,
                                                   1_000_000_000_000_000L,  1_000_000_000_000_000L);

    internal static Bbox ConservativeBboxOf(LayoutShape shape)
    {
        if (shape is LabelShape label)
        {
            // ── A PORT IS NEVER CULLED, AND THAT IS INSIDE THIS METHOD'S OWN CONTRACT ─────────
            //
            // A port's hit region and selection highlight are the MARK it draws (2026-08-25), and an
            // EDGE port's bar and arrow are drawn at the CONDUCTOR END — an unbounded distance from
            // the label's anchor, and knowable only from the technology and the artwork, neither of
            // which this framework-free file has. Any finite pad about the anchor is therefore
            // sometimes too small, and a too-small conservative box is the one failure this method
            // must never have: the port would be pruned before its exact test ran, so it would be
            // pickable from one direction and invisible to the query from another. Measured, before
            // this: a press directly on a port's arrow selected the trace underneath it.
            //
            // Over-inclusion is the safe direction (see this method's summary), and the cost is
            // bounded by construction — a port is one EM port, so a layout has a handful, not
            // thousands. It also fixes a rarer bug in the other direction: a port whose anchor is
            // off-screen while its plane is on-screen used to have its marker culled and not drawn.
            if (label.IsPort) return Everywhere;

            if (string.IsNullOrEmpty(label.Text) || label.Height <= 0)
                return new Bbox(label.X, label.Y, label.X, label.Y);
            long pad = (label.Text.Length + 1) * label.Height;
            return new Bbox(label.X - pad, label.Y - pad, label.X + pad, label.Y + pad);
        }
        return LayoutGeometry.BboxOf(shape);
    }

    // ── Full rebuild — STR (Sort-Tile-Recursive) bulk load, O(n log n) ──────────────────────────
    // Shape-only: any instance entries that existed before this call are gone afterward (a whole new
    // _root replaces the old one) — the combined-query caller (above) always follows a shape rebuild
    // with an unconditional RefreshInstances to restore them.

    private void RebuildFullShapes(IReadOnlyList<LayoutShape> shapes)
    {
        foreach (var k in _leafOf.Keys.Where(k => k.Kind == SpatialEntryKind.Shape).ToList()) _leafOf.Remove(k);
        foreach (var k in _leafOf.Keys.Where(k => k.Kind == SpatialEntryKind.Instance).ToList()) _leafOf.Remove(k);

        // ONE read of the caller's list, held for the whole rebuild.
        //
        // This runs on the RENDER thread as often as on the UI one — a stale index self-heals here,
        // and the render thread is what notices first — while the UI thread may be deleting. Reading
        // `shapes.Count` once per iteration and indexing after it is the very crash this type's own
        // header describes from the other side: the count shrinks between the test and the read, and
        // List<T> throws from a thread with nothing to catch it. Taking the backing span once fixes
        // both the length and the storage this rebuild sees.
        //
        // The result may be a slightly stale tree, and that is the point: it is rebuilt again on the
        // very next query, because _syncedCount records what was actually BUILT FROM rather than
        // whatever the list happens to say afterwards — set it from the count read after the loop and
        // a rebuild that raced a delete would look in sync and stay stale.
        ReadOnlySpan<LayoutShape> snapshot =
            shapes is List<LayoutShape> list ? CollectionsMarshal.AsSpan(list) : [.. shapes];

        var entries = new List<LeafEntry>(snapshot.Length);
        for (int i = 0; i < snapshot.Length; i++)
        {
            // Null-checked: List<T>.RemoveAt clears the vacated slot, so a span captured before a
            // delete can hand back a null where a shape used to be.
            if (snapshot[i] is not { } shape) continue;
            var bb = ConservativeBboxOf(shape);
            if (!bb.IsEmpty) entries.Add(new LeafEntry(bb, SpatialEntryKind.Shape, i));
        }

        _root = entries.Count == 0 ? new Node { IsLeaf = true, Bounds = Bbox.Empty } : BuildStrTree(entries);
        _syncedCount = snapshot.Length;
        _churnSinceRebuild = 0;
        FullRebuildCount++;

        // The freshly-built root has no leaves registered for the OLD instance entries anymore —
        // force the next combined query to treat instances as stale too (belt-and-suspenders; the
        // combined-query call site already does this unconditionally, but a bare Apply() call, e.g.
        // from a shape-only test, should not leave a document's instance bookkeeping lying about a
        // tree that no longer contains them).
        _syncedInstanceCount = -1;
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
                foreach (var e in leafEntries) { bb = bb.Union(e.Box); _leafOf[(e.Kind, e.Index)] = node; }
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

    private void InsertEntry(SpatialEntryKind kind, int index, Bbox bbox)
    {
        if (bbox.IsEmpty) return; // matches every consumer's own "skip empty bbox" convention

        _root ??= new Node { IsLeaf = true, Bounds = Bbox.Empty };

        var leaf = ChooseLeaf(_root, bbox);
        (leaf.Entries ??= []).Add(new LeafEntry(bbox, kind, index));
        leaf.Bounds = leaf.Bounds.Union(bbox);
        _leafOf[(kind, index)] = leaf;
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
        var boundsB = Bbox.Empty; foreach (var e in groupB) { boundsB = boundsB.Union(e.Box); _leafOf[(e.Kind, e.Index)] = sibling; }
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

    // ── Remove (by current (kind,index) — O(1) lookup via the reverse map, no rebalancing) ────────

    private void RemoveEntry(SpatialEntryKind kind, int index)
    {
        var key = (kind, index);
        if (!_leafOf.TryGetValue(key, out var leaf)) return;
        leaf.Entries!.RemoveAll(e => e.Kind == kind && e.Index == index);
        _leafOf.Remove(key);
        // Deliberately no bounds-shrinking / rebalancing here — an over-large ancestor bbox after a
        // delete only ever costs query EFFICIENCY, never correctness (a query still finds every real
        // match; it may visit a few extra, now-empty subtrees). R-L2b-2's churn-triggered rebuild is
        // the quality backstop, not per-delete rebalancing.
        _churnSinceRebuild++;
    }
}

/// <summary>Which list a <see cref="LayoutSpatialEntry"/> indexes into — <see cref="LayoutView.Shapes"/>
/// or <see cref="LayoutView.Instances"/> (R-L3a-4: one tree, discriminated entries, not a second tree).</summary>
public enum SpatialEntryKind { Shape, Instance }

/// <summary>One combined-query result — <see cref="Kind"/> says which list <see cref="Index"/> indexes
/// into.</summary>
public readonly record struct LayoutSpatialEntry(SpatialEntryKind Kind, int Index);
