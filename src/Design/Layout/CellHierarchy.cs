// Recursive hierarchy walking shared by bbox computation (for the spatial index / hit-test) and by
// edit-time cycle rejection. Framework-free — no Skia. The RENDERER's own compiled-geometry cache
// (LayoutRenderer's instance path cache) does its own, parallel recursive walk for exactly the same
// reason (it needs SKPath, which this file may not reference) — the two walks share the same
// cycle/depth RULES (this file is the single place those constants and the transform math live) but
// are necessarily separate code paths, one producing a Bbox, the other producing SKPaths.
//
// R-L3a-2 — cycles are rejected at edit time, detected at load time, and bounded at render time:
//   - Edit time:  CellHierarchy.WouldCreateCycle, called before an instance is added.
//   - Load/every-resolve time: ResolveForWalk's visiting-set check, below — this editor has no
//     separate offline "validate on load" pass distinct from resolving/rendering, so the SAME guard
//     that protects rendering also protects every other consumer (bbox, hit-test) that walks the
//     hierarchy, which is effectively "detected whenever the hierarchy is walked," i.e. on load.
//   - Render time: MaxDepth below is the hard backstop, independent of whether a chain is a genuine
//     cycle or just very deep — it guarantees termination regardless of which caller forgot to check.

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout;

/// <summary>Why a nested instance did not resolve to real geometry — carried up to the renderer/bbox
/// caller so it can draw or size an appropriate placeholder instead of silently producing nothing.</summary>
public enum InstanceResolutionState
{
    Resolved,
    NotFound,
    PrimaryMissing,
    /// <summary>Resolving this instance would revisit a cell already being walked in the current
    /// chain — a genuine A -&gt; B -&gt; A reference cycle, detected (not merely bounded) rather than
    /// just running into <see cref="DepthExceeded"/> eventually.</summary>
    Cyclic,
    /// <summary>The chain is deeper than <see cref="CellHierarchy.MaxDepth"/> — a backstop for
    /// legitimate-but-very-deep hierarchy as much as for a cycle neither this walk nor the edit-time
    /// check happened to catch.</summary>
    DepthExceeded,
}

/// <summary>One resolved step of a hierarchy walk.</summary>
public readonly record struct InstanceResolutionStep(
    InstanceResolutionState State,
    LayoutView? SubView,
    string? ResolvedCellDir);

public static class CellHierarchy
{
    /// <summary>Render-time recursion depth cap (§4, R-L3a-2) — suggested value from the brief.</summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// <see cref="CellLayoutResolution.ResolvedCellDir"/> is the CELL FOLDER (matching
    /// <see cref="CellLayoutResolver"/>'s own — and <c>CellSymbolResolver</c>'s — convention: the
    /// directory a <c>CellRef</c> combines with a base dir to reach), NOT the <c>layout/</c>
    /// sub-folder a <c>.clay</c> file actually lives in. A cell's OWN nested instance <c>CellRef</c>s
    /// resolve relative to ITS <c>layout/</c> sub-folder (the same "directory containing the .clay"
    /// convention <see cref="LayoutEditorViewModel.InstanceBaseDir"/> and <c>LayoutRenderOptions.
    /// BaseDir</c> both document) — every recursive call below must convert through this helper
    /// before using a resolved cell dir as the next level's base dir, or a nested <c>CellRef</c>
    /// written as <c>"../../Sibling"</c> (correct relative to <c>Cell/layout/</c>) would silently
    /// fail to resolve one directory level too shallow.
    /// </summary>
    internal static string LayoutBaseDirOf(string resolvedCellDir) => CellFolder.SubFolderPath(resolvedCellDir, ViewType.Layout);

    /// <summary>A placeholder half-extent (DBU) used when an unresolved/broken instance has no real
    /// geometry to measure — keeps it a small but non-degenerate, clickable/selectable target rather
    /// than a zero-size point (R-L3a-1: "stays fully selectable and movable").</summary>
    public const long PlaceholderHalfExtentDbu = 50_000; // 50 microns at 1000 DBU/micron

    /// <summary>
    /// Resolves one instance for a recursive walk already <paramref name="depth"/> levels deep, with
    /// <paramref name="visiting"/> holding every resolved cell absolute directory currently on the
    /// path from the walk's root to here (inclusive of the caller, exclusive of this instance's own
    /// target). On a <see cref="InstanceResolutionState.Resolved"/> result the caller MUST add the
    /// returned <c>ResolvedCellDir</c> to <paramref name="visiting"/> before recursing into
    /// <c>SubView.Instances</c>, and remove it again afterward (standard DFS backtracking) — this
    /// method itself only performs the CHECK, not the add, since the caller also needs the directory
    /// to recurse with regardless of outcome.
    /// </summary>
    public static InstanceResolutionStep ResolveForWalk(LayoutInstance inst, string baseDir, IReadOnlySet<string> visiting, int depth)
    {
        if (depth >= MaxDepth)
            return new InstanceResolutionStep(InstanceResolutionState.DepthExceeded, null, null);

        var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
        var state = res.State switch
        {
            CellLayoutState.Resolved       => InstanceResolutionState.Resolved,
            CellLayoutState.NotFound       => InstanceResolutionState.NotFound,
            CellLayoutState.PrimaryMissing => InstanceResolutionState.PrimaryMissing,
            _                              => InstanceResolutionState.NotFound,
        };
        if (state != InstanceResolutionState.Resolved)
            return new InstanceResolutionStep(state, null, null);

        if (visiting.Contains(res.ResolvedCellDir!))
            return new InstanceResolutionStep(InstanceResolutionState.Cyclic, null, res.ResolvedCellDir);

        return new InstanceResolutionStep(InstanceResolutionState.Resolved, res.View, res.ResolvedCellDir);
    }

    /// <summary>
    /// The placeholder bbox for a broken/unresolved/cyclic/too-deep instance — centered at the
    /// instance's own placement, independent of what its array multiplies it into (a broken reference
    /// still occupies exactly ONE placeholder, not <c>Rows*Cols</c> of them — there is nothing to
    /// array-replicate when there is no real geometry).
    /// </summary>
    public static Bbox PlaceholderBbox(LayoutInstance inst) => new(
        inst.X - PlaceholderHalfExtentDbu, inst.Y - PlaceholderHalfExtentDbu,
        inst.X + PlaceholderHalfExtentDbu, inst.Y + PlaceholderHalfExtentDbu);

    /// <summary>
    /// Full effective bbox of one <see cref="LayoutInstance"/> placement — the resolved sub-cell's own
    /// recursive bbox (its shapes, unioned with every one of ITS OWN instances' recursive bboxes),
    /// transformed by this instance's rotation/mirror/magnification, translated to its position, and
    /// expanded across the array extent (Rows x Cols x Pitch). A broken/cyclic/too-deep reference
    /// returns <see cref="PlaceholderBbox"/> (still array-expanded, since array cells of a genuinely
    /// resolved-but-then-broken-deeper reference are each independently a placeholder — but see the
    /// no-array-replication note on <see cref="PlaceholderBbox"/> for the outer-instance-itself-broken
    /// case, which is what <see cref="InstanceBbox"/> actually hits first for a directly-broken ref).
    /// </summary>
    /// <param name="layerVisible">Optional per-layer filter. Null (the default, and every interactive
    /// caller) measures every layer, which is what the spatial index, hit-testing and the renderer's
    /// LOD decision all need — they cull against where the geometry IS, not against what is currently
    /// painted. A graphics EXPORT is the opposite case and passes one: a page sized to a layer the
    /// viewer turned off is a page mostly full of nothing (owner report, 2026-09-04). Supplying a
    /// filter bypasses the <see cref="ShapesBbox"/> memo, which is keyed on the view alone.</param>
    public static Bbox InstanceBbox(LayoutInstance inst, string baseDir, Func<LayerKey, bool>? layerVisible = null)
        => ArrayExpand(BasePlacementBbox(inst, baseDir, layerVisible), inst);

    /// <summary>
    /// <see cref="InstanceBbox"/> for ONE array cell — the row-0/col-0 placement, WITHOUT the
    /// <c>Rows x Cols x Pitch</c> expansion. Identical work and identical result otherwise; the two
    /// share one body precisely so a caller reasoning about a single cell can never disagree with
    /// the whole-array box about where that cell is.
    ///
    /// <para>Hit-testing is what wants it: with a uniform pitch, "which array cells can this point
    /// possibly be in" is arithmetic on this box, so a 20x20 array costs one or two descents into
    /// the sub-cell rather than four hundred (<c>LayoutHitTest.InstanceHitTest</c>).</para>
    /// </summary>
    public static Bbox BasePlacementBbox(LayoutInstance inst, string baseDir, Func<LayerKey, bool>? layerVisible = null)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = ResolveForWalk(inst, baseDir, visiting, 0);
        if (step.State != InstanceResolutionState.Resolved)
            return PlaceholderBbox(inst);

        visiting.Add(step.ResolvedCellDir!);
        var localBbox = CellBboxRecursive(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1, layerVisible);
        visiting.Remove(step.ResolvedCellDir!);

        if (localBbox.IsEmpty) return PlaceholderBbox(inst);

        return TransformBboxToParent(localBbox, inst);
    }

    /// <summary>
    /// <see cref="InstanceBbox"/> against a caller-supplied cell view instead of the one on disk —
    /// for a live PCell grip drag, whose regenerated artwork exists only in memory until the gesture
    /// commits. Everything downstream of the resolve (recursive nested-instance union, the placement
    /// transform, array expansion) is the SAME code, so a preview bbox and a committed one can never
    /// disagree about anything except the geometry they were given.
    /// </summary>
    public static Bbox InstanceBboxOfView(LayoutView view, LayoutInstance inst, string baseDir)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = ResolveForWalk(inst, baseDir, visiting, 0);
        string viewBaseDir = step.State == InstanceResolutionState.Resolved
            ? LayoutBaseDirOf(step.ResolvedCellDir!) : baseDir;

        var localBbox = CellBboxRecursive(view, viewBaseDir, visiting, 1);
        if (localBbox.IsEmpty) return ArrayExpand(PlaceholderBbox(inst), inst);
        return ArrayExpand(TransformBboxToParent(localBbox, inst), inst);
    }

    /// <summary>Own shapes' bbox, unioned with every instance's (recursive) transformed bbox — the
    /// "effective bbox of a resolved cell" used both at the top level (via <see cref="InstanceBbox"/>)
    /// and recursively for nested instances.</summary>
    private static Bbox CellBboxRecursive(LayoutView view, string viewBaseDir, HashSet<string> visiting, int depth,
                                          Func<LayerKey, bool>? layerVisible = null)
    {
        var bb = ShapesBbox(view, layerVisible);

        foreach (var nested in view.Instances)
        {
            var step = ResolveForWalk(nested, viewBaseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved)
            {
                bb = bb.Union(ArrayExpand(PlaceholderBbox(nested), nested));
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var nestedLocal = CellBboxRecursive(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), visiting, depth + 1,
                                                layerVisible);
            visiting.Remove(step.ResolvedCellDir!);

            if (nestedLocal.IsEmpty) continue;
            var nestedTransformed = TransformBboxToParent(nestedLocal, nested);
            bb = bb.Union(ArrayExpand(nestedTransformed, nested));
        }

        return bb;
    }

    /// <summary>A view's OWN shapes' bbox, memoized on the view REFERENCE — the same lifecycle the
    /// renderer's compiled-geometry cache piggybacks on, for the same reason: a
    /// <see cref="CellLayoutResolver"/> hit hands back the same <see cref="LayoutView"/> instance, and a
    /// file change produces a new one, so the two caches go stale together for free with no
    /// invalidation call to forget.
    ///
    /// <para><b>Deliberately the shapes only, never the recursive result.</b> The recursive walk's
    /// answer depends on the <c>visiting</c> set and the <c>depth</c> it was reached at (see
    /// <see cref="MaxDepth"/>), so the same sub-cell reached down two different chains can legitimately
    /// have two different effective bboxes and must not share one cache entry. A view's own shapes
    /// depend on neither, and they are where the time actually goes: a generated cell can hold a
    /// six-figure via field, and <see cref="InstanceBbox"/> is called PER PLACEMENT, PER FRAME (the
    /// renderer's LOD decision) — so a design placing that cell two dozen times was re-unioning
    /// millions of rectangles every frame of every pan.</para></summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutView, StrongBoxBbox> _shapesBboxCache = new();

    private sealed class StrongBoxBbox(Bbox value) { public readonly Bbox Value = value; }

    /// <summary>Evicts <paramref name="view"/>'s memoized shapes bbox — the bbox half of
    /// <c>LayoutRenderer.InvalidateCompiledGeometry</c>, which calls this, and which documents the
    /// in-place-edited push-in session the eviction exists for. Safe on a view never measured (no-op).
    /// </summary>
    public static void InvalidateShapesBbox(LayoutView view)
    {
        _shapesBboxCache.Remove(view);
        InvalidateOwnLayerKeys(view);
    }

    private static Bbox ShapesBbox(LayoutView view, Func<LayerKey, bool>? layerVisible = null)
    {
        // A filtered answer is not the view's bbox and must never be stored as one — the memo is keyed
        // on the view alone, so caching a filtered result here would hand a hidden-layer-less bbox to
        // the spatial index and to hit-testing.
        if (layerVisible is not null)
        {
            var filtered = Bbox.Empty;
            foreach (var shape in view.Shapes)
                if (layerVisible(shape.Layer)) filtered = filtered.Union(LayoutGeometry.BboxOf(shape));
            return filtered;
        }

        if (_shapesBboxCache.TryGetValue(view, out var hit)) return hit.Value;

        var bb = Bbox.Empty;
        foreach (var shape in view.Shapes)
            bb = bb.Union(LayoutGeometry.BboxOf(shape));

        _shapesBboxCache.AddOrUpdate(view, new StrongBoxBbox(bb));
        return bb;
    }

    /// <summary>Applies (mirror, rotation, magnification, translation) to a cell-local bbox (in that
    /// cell's own DBU, Y-up) to produce a bbox in the PARENT's DBU space, at the instance's base
    /// position (row=0,col=0 — <see cref="ArrayExpand"/> replicates across the rest of the array).
    /// Uses <see cref="LayoutInstanceTransform"/> — the SAME transform math the renderer uses, so a
    /// bbox computed here always agrees with what actually gets drawn.</summary>
    private static Bbox TransformBboxToParent(Bbox localBbox, LayoutInstance inst)
    {
        Span<(long X, long Y)> corners =
        [
            (localBbox.MinX, localBbox.MinY), (localBbox.MaxX, localBbox.MinY),
            (localBbox.MinX, localBbox.MaxY), (localBbox.MaxX, localBbox.MaxY),
        ];

        var bb = Bbox.Empty;
        foreach (var (x, y) in corners)
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(x, y, inst, 0, 0);
            bb = bb.Union(new Bbox(wx, wy, wx, wy));
        }
        return bb;
    }

    /// <summary>Unions the base-placement bbox across every array cell (Rows x Cols x Pitch, applied
    /// in the PARENT's unrotated frame — see <see cref="LayoutInstanceTransform"/>'s own doc comment
    /// for why array pitch is deliberately not rotated with the instance).</summary>
    private static Bbox ArrayExpand(Bbox baseBbox, LayoutInstance inst)
    {
        if (baseBbox.IsEmpty) return baseBbox;
        int rows = Math.Max(1, inst.Rows), cols = Math.Max(1, inst.Cols);
        if (rows == 1 && cols == 1) return baseBbox;

        long maxDx = (cols - 1) * inst.PitchX, maxDy = (rows - 1) * inst.PitchY;
        long minDx = Math.Min(0, maxDx), minDy = Math.Min(0, maxDy);
        maxDx = Math.Max(0, maxDx); maxDy = Math.Max(0, maxDy);

        return new Bbox(
            baseBbox.MinX + minDx, baseBbox.MinY + minDy,
            baseBbox.MaxX + maxDx, baseBbox.MaxY + maxDy);
    }

    // ── Edit-time cycle rejection (R-L3a-2) ──────────────────────────────────────

    /// <summary>
    /// True if adding an instance from a layout resolved at <paramref name="currentLayoutAbsDir"/>
    /// (the document currently being edited — null/not-yet-saved documents cannot participate in a
    /// cycle at all, since nothing can reference back to a path that doesn't exist yet, so this always
    /// returns false for a scratch document) to <paramref name="candidateCellRef"/> (resolved from
    /// <paramref name="candidateBaseDir"/>) would create a cycle — i.e. the candidate cell can already
    /// (transitively, through its own instances) reach <paramref name="currentLayoutAbsDir"/>. Adding
    /// edge current-&gt;candidate closes a cycle iff candidate can already reach current; this is a
    /// plain DFS from the candidate, reusing <see cref="ResolveForWalk"/>'s own depth/visiting guards
    /// so a malformed EXISTING sub-graph can't itself hang this check.
    /// </summary>
    public static bool WouldCreateCycle(string? currentLayoutAbsDir, string candidateCellRef, string candidateBaseDir)
    {
        if (string.IsNullOrEmpty(currentLayoutAbsDir)) return false;

        // Normalize once here — the caller's currentLayoutAbsDir may not be Path.GetFullPath-clean
        // (e.g. it came straight from CellFolder.CreateCellFolder's Path.Combine), while every
        // ResolvedCellDir this method compares against always IS (CellLayoutResolver.Resolve's own
        // Path.GetFullPath). Comparing a non-normalized path against a normalized one can silently
        // fail even when both refer to the identical directory.
        string target;
        try { target = Path.GetFullPath(currentLayoutAbsDir); }
        catch { return false; }

        var res = CellLayoutResolver.Resolve(candidateCellRef, candidateBaseDir);
        if (res.State != CellLayoutState.Resolved) return false; // nothing real to cycle through

        if (string.Equals(res.ResolvedCellDir, target, StringComparison.OrdinalIgnoreCase))
            return true; // directly self-referential

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { res.ResolvedCellDir! };
        return CanReach(res.View!, LayoutBaseDirOf(res.ResolvedCellDir!), target, visiting, 1);
    }

    // ── Occupied layer keys (§5C.2a/R47h) ────────────────────────────────────────────────────────

    /// <summary>
    /// Every <see cref="LayerKey"/> a cell's hierarchy actually PUTS something on — its own shapes
    /// (including a via's <see cref="ViaShape.LandingLayer"/>, which is a second, different key on the
    /// same shape) and its pins, unioned with the same answer for every cell it can reach.
    /// <b>Null when the walk could not be completed</b> (see below), which callers must treat as
    /// "unknown", never as "none".
    ///
    /// <para><b>Why this exists:</b> R47's cross-workspace technology gate compares two layer tables,
    /// and the hazard it names is a key being REINTERPRETED — which can only happen to a key something
    /// is drawn on. Comparing the whole table refuses two projects that share a metal stack and differ
    /// only in their documentation layers, which is the ordinary case and not a hazard at all. This is
    /// the set the comparison is honestly over.</para>
    ///
    /// <para><b>The answer is a UNION over reachable cells, so every cell is visited ONCE.</b> That is
    /// what separates this walk from <see cref="CellBboxRecursive"/>, which cannot dedupe: a bbox
    /// depends on the transform chain that reached it, so the same sub-cell down two paths is
    /// genuinely two different answers. A layer key is not transformed — a rotation moves a shape, it
    /// never changes its layer — so the second visit can only re-derive what the first already
    /// contributed. <b>This distinction is the whole performance story:</b> the first version of this
    /// method carried only the DFS-path set and re-walked a shared sub-cell once per PATH to it, which
    /// is exponential in depth. A 43-cell fixture (depth 5, fan-out 7) took 5.8 s; deduped it is
    /// sub-millisecond, and a real library cell dropped into a workspace hung the UI for a minute.</para>
    ///
    /// <para><b>Cycles need no special handling here</b>, for the same reason: a union over a graph is
    /// well defined however the edges run, and the visited set terminates it. Depth is different — a
    /// chain longer than <see cref="MaxDepth"/> is TRUNCATED by <see cref="ResolveForWalk"/>, which
    /// would silently under-report the union and hand the gate a permit it did not earn. That case
    /// returns <b>null</b> instead, and <c>ExternalWorkspaceGate</c> falls back to comparing the whole
    /// table — the strict direction, which is the only safe one for a gate.</para>
    ///
    /// <para>An instance that does not resolve contributes nothing rather than failing the walk — its
    /// layers are unknowable, and it is already reported as a broken reference by every other
    /// consumer.</para>
    /// </summary>
    /// <param name="view">The cell's own layout view.</param>
    /// <param name="viewBaseDir">The directory <paramref name="view"/>'s own <c>CellRef</c>s resolve
    /// against — the <c>layout/</c> sub-folder holding its <c>.clay</c>, per <see cref="LayoutBaseDirOf"/>.</param>
    public static IReadOnlySet<LayerKey>? OccupiedLayerKeys(LayoutView view, string viewBaseDir)
    {
        var keys = new HashSet<LayerKey>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;
        CollectOccupiedKeys(view, viewBaseDir, visited, 0, keys, ref truncated);
        return truncated ? null : keys;
    }

    private static void CollectOccupiedKeys(
        LayoutView view, string viewBaseDir, HashSet<string> visited, int depth,
        HashSet<LayerKey> keys, ref bool truncated)
    {
        keys.UnionWith(OwnLayerKeys(view));

        foreach (var inst in view.Instances)
        {
            var step = ResolveForWalk(inst, viewBaseDir, visited, depth);

            if (step.State == InstanceResolutionState.DepthExceeded) { truncated = true; continue; }

            // Cyclic here means "already in the visited set", which after dedup is the ORDINARY case
            // for a shared sub-cell, not an error: its keys are in the union already.
            if (step.State != InstanceResolutionState.Resolved) continue;

            if (!visited.Add(step.ResolvedCellDir!)) continue;
            CollectOccupiedKeys(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), visited, depth + 1,
                                keys, ref truncated);
        }
    }

    /// <summary>
    /// One view's OWN layer keys — shapes (both of a via's two layer fields) and pins, nothing
    /// recursive. Memoized on the view REFERENCE, on exactly the terms
    /// <see cref="_shapesBboxCache"/> documents: a <see cref="CellLayoutResolver"/> hit hands back the
    /// same <see cref="LayoutView"/> instance and a file change produces a new one, so the memo goes
    /// stale for free with no invalidation call to forget.
    ///
    /// <para>Unlike the bbox memo this one is unconditionally safe to share, because it depends on
    /// neither the depth nor the path a view was reached by. It matters because the R47h re-check runs
    /// on the process-wide live-refresh tick, and a generated cell can hold a six-figure via field
    /// that would otherwise be re-enumerated on every one.</para>
    /// </summary>
    private static IReadOnlySet<LayerKey> OwnLayerKeys(LayoutView view)
    {
        if (_ownLayerKeysCache.TryGetValue(view, out var cached)) return cached.Value;

        var own = new HashSet<LayerKey>();
        foreach (var shape in view.Shapes)
        {
            own.Add(shape.Layer);
            // A via occupies its barrel layer AND its pad layer, and the two are deliberately
            // different fields (ViaShape's own doc comment). Missing the landing layer here would
            // permit a placement whose PADS land on a key the host table means something else by.
            if (shape is ViaShape { LandingLayer: { } landing }) own.Add(landing);
        }
        foreach (var pin in view.Pins) own.Add(pin.Layer);

        _ownLayerKeysCache.AddOrUpdate(view, new StrongBoxKeys(own));
        return own;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutView, StrongBoxKeys>
        _ownLayerKeysCache = new();

    private sealed class StrongBoxKeys(IReadOnlySet<LayerKey> value) { public readonly IReadOnlySet<LayerKey> Value = value; }

    /// <summary>Evicts <paramref name="view"/>'s memoized own-layer keys — called from
    /// <see cref="InvalidateShapesBbox"/>, since the one thing that invalidates either is the same
    /// thing: this view's shapes were edited in place by an open push-in session.</summary>
    private static void InvalidateOwnLayerKeys(LayoutView view) => _ownLayerKeysCache.Remove(view);

    private static bool CanReach(LayoutView view, string viewBaseDir, string targetAbsDir, HashSet<string> visiting, int depth)
    {
        if (depth >= MaxDepth) return false;

        foreach (var inst in view.Instances)
        {
            var step = ResolveForWalk(inst, viewBaseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved) continue;

            if (string.Equals(step.ResolvedCellDir, targetAbsDir, StringComparison.OrdinalIgnoreCase))
                return true;

            visiting.Add(step.ResolvedCellDir!);
            bool found = CanReach(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), targetAbsDir, visiting, depth + 1);
            visiting.Remove(step.ResolvedCellDir!);
            if (found) return true;
        }
        return false;
    }
}
