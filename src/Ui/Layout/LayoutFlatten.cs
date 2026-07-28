// Flatten Hierarchy (brief-L3c-flatten-and-group.md §2/§3) — framework-free math for turning an
// instance into copies of its sub-cell's contents, transformed into the parent's coordinates.
// Deliberately named "Flatten HIERARCHY" everywhere, never bare "Flatten" — §1's own warning that
// "Flatten" alone collides with Flatten to Polygon (L1e/L1h, a curve becoming a polygon; an unrelated
// operation on an unrelated shape kind).

namespace CircuitRF.Ui.Layout;

public static class LayoutFlatten
{
    /// <summary>
    /// Result of flattening ONE instance by exactly one level (R-L3c-1). Never both non-empty:
    /// an ARRAYED instance (<c>Rows &gt; 1 || Cols &gt; 1</c>) yields <see cref="Instances"/> only
    /// (N = Rows×Cols plain instances, no sub-cell resolution needed at all — R-L3c-1's headline
    /// rule, "an array is a level"); a PLAIN instance yields the sub-cell's own <see cref="Shapes"/>
    /// (transformed into the parent's frame) plus its own <see cref="Instances"/> (composed into the
    /// parent's frame via <see cref="LayoutInstanceTransform.ComposeInstances"/>, CellRef rebased —
    /// "the sub-cell's own instances still become instances of the parent").
    /// </summary>
    public sealed record OneLevelResult(
        IReadOnlyList<LayoutShape> Shapes,
        IReadOnlyList<LayoutInstance> Instances,
        bool WasArray);

    /// <summary>
    /// Flattens <paramref name="inst"/> by exactly one level. <paramref name="parentBaseDir"/> is the
    /// CURRENT document's own <c>layout/</c> directory (<c>LayoutEditorViewModel.InstanceBaseDir</c>)
    /// — both the base <paramref name="inst"/>.CellRef resolves against AND the base any rebased
    /// nested CellRefs are expressed relative to. Returns <c>null</c> when the instance does not
    /// resolve (caller leaves it in place as an instance, per §3's "broken instance survives and is
    /// reported" rule — this same null contract serves both the one-level and all-levels callers).
    /// </summary>
    public static OneLevelResult? FlattenOneLevel(LayoutInstance inst, string parentBaseDir)
    {
        if (inst.Rows > 1 || inst.Cols > 1)
        {
            var expanded = new List<LayoutInstance>(inst.Rows * inst.Cols);
            for (int row = 0; row < inst.Rows; row++)
            for (int col = 0; col < inst.Cols; col++)
            {
                var (x, y) = LayoutInstanceTransform.ArrayCellOrigin(inst, row, col);
                expanded.Add(new LayoutInstance
                {
                    CellRef = inst.CellRef,
                    X = x, Y = y,
                    Rot = inst.Rot, MirrorX = inst.MirrorX, Mag = inst.Mag,
                    Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
                });
            }
            return new OneLevelResult([], expanded, WasArray: true);
        }

        var res = CellLayoutResolver.Resolve(inst.CellRef, parentBaseDir);
        if (res.State != CellLayoutState.Resolved) return null;

        var subView = res.View!;
        var subBaseDir = CellHierarchy.LayoutBaseDirOf(res.ResolvedCellDir!);

        var shapes = new List<LayoutShape>(subView.Shapes.Count);
        var pointTransform = new LayoutCoordinateTransform(
            (x, y) => LayoutInstanceTransform.TransformPoint(x, y, inst, 0, 0),
            m => (long)Math.Round(m * inst.Mag));
        foreach (var shape in subView.Shapes)
        {
            var clone = LayoutGeometry.Clone(shape);
            LayoutCoordinateWalk.Transform(clone, pointTransform);
            if (inst.MirrorX) FlipBulgeSigns(clone);
            shapes.Add(clone);
        }

        var instances = new List<LayoutInstance>(subView.Instances.Count);
        foreach (var subInst in subView.Instances)
        {
            var composed = LayoutInstanceTransform.ComposeInstances(inst, 0, 0, subInst);
            composed.CellRef = RebaseCellRef(subInst.CellRef, subBaseDir, parentBaseDir);
            instances.Add(composed);
        }

        return new OneLevelResult(shapes, instances, WasArray: false);
    }

    /// <summary>
    /// R-L3c-2: rotation alone leaves an Arc edge's bulge unchanged (it is a dimensionless
    /// sweep-angle descriptor, not a coordinate — <see cref="LayoutCoordinateWalk"/> never touches
    /// it), but a MIRROR reverses which side of the chord the arc bulges toward, so the sign must
    /// flip. Applies to every Arc-kind edge on a <see cref="CurveShape"/> or <see cref="PathShape"/>;
    /// a no-op for every other shape kind (nothing else has edges).
    /// </summary>
    public static void FlipBulgeSigns(LayoutShape shape)
    {
        List<LayoutEdge>? edges = shape switch
        {
            CurveShape c => c.Edges,
            PathShape p  => p.Edges,
            _            => null,
        };
        if (edges is null) return;
        foreach (var e in edges)
            if (e.Kind == EdgeKind.Arc)
                e.Bulge = -e.Bulge;
    }

    /// <summary>Rebases a CellRef known relative to <paramref name="fromDir"/> so it is relative to
    /// <paramref name="toDir"/> instead — mirrors <c>LayoutFragment.RebaseInstances</c>'s exact
    /// algorithm and fallback (keep the original string on any path-resolution failure, e.g. a
    /// different drive on Windows, rather than throwing or silently producing a wrong path).</summary>
    internal static string RebaseCellRef(string cellRef, string fromDir, string toDir)
    {
        if (string.IsNullOrEmpty(cellRef) || string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toDir))
            return cellRef;
        try
        {
            string abs = Path.GetFullPath(Path.Combine(fromDir, cellRef));
            return Path.GetRelativePath(Path.GetFullPath(toDir), abs);
        }
        catch
        {
            return cellRef;
        }
    }

    // ── Flatten Hierarchy — all levels (§3) ─────────────────────────────────────────────────────

    /// <summary>Refuse a Flatten-All-Levels operation outright above this many resulting shapes,
    /// per R-L3c-4 — "rather than producing a layout the editor cannot open." 500,000 matches the
    /// "pathological/full chip" scale this codebase already treats as the outer edge of what the
    /// editor is expected to open at all (see the L2a/L2b/L2c 500k benchmark rows in src/Ui/CLAUDE.md);
    /// there is nothing sacred about the exact number beyond "the same order of magnitude as the
    /// largest layout this editor has ever been asked to hold."</summary>
    public const long FlattenAllLevelsHardCeiling = 500_000;

    /// <summary>Result of recursively flattening every level of an instance's hierarchy (§3). A
    /// broken or unresolvable instance ANYWHERE in the tree — including <c>inst</c> itself — is left
    /// in place as one entry of <see cref="SurvivingInstances"/> rather than dropped; partial success
    /// with a clear report beats an all-or-nothing failure on a large design (§3's own wording).</summary>
    public sealed record AllLevelsResult(
        IReadOnlyList<LayoutShape> Shapes,
        IReadOnlyList<LayoutInstance> SurvivingInstances);

    /// <summary>
    /// R-L3c-4: computes the FULL resulting shape count before anything is mutated, honouring the
    /// same array-multiplies-a-level and depth-cap (<see cref="CellHierarchy.MaxDepth"/>) rules
    /// <see cref="FlattenAllLevels"/> itself uses, WITHOUT ever materializing more than one array
    /// cell's worth of shapes — an array's per-cell count is computed exactly once and multiplied by
    /// <c>Rows×Cols</c>, so this stays cheap even for a design whose true resulting count would be in
    /// the millions. Returns <c>-1</c> the moment the running total would exceed <paramref
    /// name="ceiling"/> (a sentinel, not an exact over-ceiling count — the caller only needs to know
    /// "too big," never by how much) rather than continuing to accumulate a number that could overflow
    /// <see cref="long"/> for a sufficiently large nested-array design.
    /// </summary>
    public static long CountResultingShapes(LayoutInstance inst, string parentBaseDir, long ceiling = FlattenAllLevelsHardCeiling)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CountRecursive(inst, parentBaseDir, visiting, 0, ceiling);
    }

    private static long CountRecursive(LayoutInstance inst, string baseDir, HashSet<string> visiting, int depth, long ceiling)
    {
        if (depth >= CellHierarchy.MaxDepth) return 0;   // depth-capped instance contributes no shapes, survives as-is

        if (inst.Rows > 1 || inst.Cols > 1)
        {
            long mult = (long)Math.Max(1, inst.Rows) * Math.Max(1, inst.Cols);
            var oneCell = new LayoutInstance
            {
                CellRef = inst.CellRef, Rot = inst.Rot, MirrorX = inst.MirrorX, Mag = inst.Mag,
                Rows = 1, Cols = 1,
            };
            long perCell = CountRecursive(oneCell, baseDir, visiting, depth + 1, ceiling);
            if (perCell < 0) return -1;
            if (perCell == 0) return 0;
            try
            {
                long total = checked(perCell * mult);
                return total > ceiling ? -1 : total;
            }
            catch (OverflowException) { return -1; }
        }

        var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
        if (res.State != CellLayoutState.Resolved) return 0;   // unresolvable → survives, contributes no shapes

        if (!visiting.Add(res.ResolvedCellDir!)) return 0;      // cycle → treated the same as unresolvable

        // res.View.Instances' own CellRefs are stored relative to the SUB-CELL's own layout/ folder
        // (CellHierarchy.LayoutBaseDirOf), never the caller's baseDir — the exact one-level-too-shallow
        // pitfall R-L3a-2's own history already names once. FlattenAllRecursive avoids this because it
        // reads FlattenOneLevel's ALREADY-REBASED oneLevel.Instances instead of these raw ones; this
        // count-only walk resolves the raw list directly, so it must rebase the base dir itself.
        string subBaseDir = CellHierarchy.LayoutBaseDirOf(res.ResolvedCellDir!);
        long count = res.View!.Shapes.Count;
        foreach (var subInst in res.View.Instances)
        {
            long sub = CountRecursive(subInst, subBaseDir, visiting, depth + 1, ceiling);
            if (sub < 0) { visiting.Remove(res.ResolvedCellDir!); return -1; }
            count += sub;
            if (count > ceiling) { visiting.Remove(res.ResolvedCellDir!); return -1; }
        }
        visiting.Remove(res.ResolvedCellDir!);
        return count;
    }

    /// <summary>
    /// Recursively flattens EVERY level of <paramref name="inst"/>'s hierarchy — the one-shot path for
    /// users who genuinely want geometry (§2's R-L3c-1 note: "Flatten All Levels still goes all the
    /// way in one action"). Reuses <see cref="FlattenOneLevel"/> at every step (never a second
    /// traversal), recursing into both an array's exploded cells and a plain instance's own nested
    /// instances — both already come back rebased to <paramref name="parentBaseDir"/> by
    /// <see cref="FlattenOneLevel"/>, so every recursive call shares that same base dir. A cycle-guard
    /// stack (<c>visiting</c>, scoped to the current recursion branch — NOT global, so two SIBLING
    /// instances referencing the same cell are not mistaken for a cycle) mirrors
    /// <see cref="CellHierarchy.ResolveForWalk"/>'s own pattern; <see cref="CellHierarchy.MaxDepth"/>
    /// is the depth backstop, same as everywhere else in this codebase a hierarchy is walked.
    /// <b>Always succeeds</b> — including when <paramref name="inst"/> itself cannot be resolved, in
    /// which case the result is simply "no shapes, one surviving instance: <paramref name="inst"/>
    /// itself" — because per §3, a broken instance ANYWHERE in the tree, not excluding the root, is
    /// left in place and reported rather than failing the whole operation.
    /// </summary>
    public static AllLevelsResult FlattenAllLevels(LayoutInstance inst, string parentBaseDir)
    {
        var shapes = new List<LayoutShape>();
        var surviving = new List<LayoutInstance>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FlattenAllRecursive(inst, parentBaseDir, visiting, 0, shapes, surviving);
        return new AllLevelsResult(shapes, surviving);
    }

    private static void FlattenAllRecursive(LayoutInstance inst, string baseDir, HashSet<string> visiting, int depth,
        List<LayoutShape> shapes, List<LayoutInstance> surviving)
    {
        if (depth >= CellHierarchy.MaxDepth) { surviving.Add(inst); return; }

        bool isArray = inst.Rows > 1 || inst.Cols > 1;
        string? resolvedDir = null;
        if (!isArray)
        {
            var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
            if (res.State != CellLayoutState.Resolved) { surviving.Add(inst); return; }
            resolvedDir = res.ResolvedCellDir;
            if (!visiting.Add(resolvedDir!)) { surviving.Add(inst); return; }   // cycle — leave in place
        }

        var oneLevel = FlattenOneLevel(inst, baseDir);
        if (oneLevel is null)
        {
            surviving.Add(inst);
            if (resolvedDir is not null) visiting.Remove(resolvedDir);
            return;
        }

        shapes.AddRange(oneLevel.Shapes);
        foreach (var subInst in oneLevel.Instances)
            FlattenAllRecursive(subInst, baseDir, visiting, depth + 1, shapes, surviving);

        if (resolvedDir is not null) visiting.Remove(resolvedDir);
    }
}
