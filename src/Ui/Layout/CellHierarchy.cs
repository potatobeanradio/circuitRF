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

using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

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
    public static Bbox InstanceBbox(LayoutInstance inst, string baseDir)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = ResolveForWalk(inst, baseDir, visiting, 0);
        if (step.State != InstanceResolutionState.Resolved)
            return ArrayExpand(PlaceholderBbox(inst), inst);

        visiting.Add(step.ResolvedCellDir!);
        var localBbox = CellBboxRecursive(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), visiting, 1);
        visiting.Remove(step.ResolvedCellDir!);

        if (localBbox.IsEmpty) return ArrayExpand(PlaceholderBbox(inst), inst);

        var transformed = TransformBboxToParent(localBbox, inst);
        return ArrayExpand(transformed, inst);
    }

    /// <summary>Own shapes' bbox, unioned with every instance's (recursive) transformed bbox — the
    /// "effective bbox of a resolved cell" used both at the top level (via <see cref="InstanceBbox"/>)
    /// and recursively for nested instances.</summary>
    private static Bbox CellBboxRecursive(LayoutView view, string viewBaseDir, HashSet<string> visiting, int depth)
    {
        var bb = Bbox.Empty;
        foreach (var shape in view.Shapes)
            bb = bb.Union(LayoutGeometry.BboxOf(shape));

        foreach (var nested in view.Instances)
        {
            var step = ResolveForWalk(nested, viewBaseDir, visiting, depth);
            if (step.State != InstanceResolutionState.Resolved)
            {
                bb = bb.Union(ArrayExpand(PlaceholderBbox(nested), nested));
                continue;
            }

            visiting.Add(step.ResolvedCellDir!);
            var nestedLocal = CellBboxRecursive(step.SubView!, LayoutBaseDirOf(step.ResolvedCellDir!), visiting, depth + 1);
            visiting.Remove(step.ResolvedCellDir!);

            if (nestedLocal.IsEmpty) continue;
            var nestedTransformed = TransformBboxToParent(nestedLocal, nested);
            bb = bb.Union(ArrayExpand(nestedTransformed, nested));
        }

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
