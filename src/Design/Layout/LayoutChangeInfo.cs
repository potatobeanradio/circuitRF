// The payload for LayoutView.Changed (docs/sonnet-briefs/brief-L2b-spatial-index.md §1, R-L2b-2).
// Framework-free — no Avalonia/Skia types.

namespace CircuitRF.Design.Layout;

public enum LayoutChangeKind
{
    /// <summary>Everything may have changed — the spatial index does a full STR rebuild. The safe
    /// default: any command that does not explicitly classify itself falls back to this via
    /// <see cref="LayoutView.NotifyChanged"/>'s null-means-Full parameter, so a missed classification
    /// is a (correct, just slower) full rebuild, never a stale index.</summary>
    Full,

    /// <summary>A contiguous run of brand-new shapes was inserted starting at
    /// <see cref="LayoutChangeInfo.StartIndex"/>, at what was then the END of <c>Shapes</c> — nothing
    /// at or after that index existed before this change, so no other shape's index shifted. Safe for
    /// the trailing-append fast path: <c>AddShapeCommand.Execute</c>, <c>ReplaceShapesCommand.Execute</c>
    /// when its removed set is empty (paste/duplicate).</summary>
    Appended,

    /// <summary>The mirror image of <see cref="Appended"/> — a contiguous run of shapes was removed
    /// from the TAIL of <c>Shapes</c> (the shapes at <see cref="LayoutChangeInfo.StartIndex"/>..
    /// <c>StartIndex+Count-1</c> no longer exist, and nothing after them existed either, so nothing
    /// shifted). <c>AddShapeCommand.Undo</c>, <c>ReplaceShapesCommand.Undo</c> when its added set is
    /// what's being removed and the removed-set was originally empty.</summary>
    RemovedTrailing,

    /// <summary>The shapes AT <see cref="LayoutChangeInfo.Indices"/> were replaced or mutated IN
    /// PLACE — <c>Shapes.Count</c> is unchanged and every OTHER index's occupant is unchanged (no
    /// insert/remove happened anywhere). <c>MoveShapesCommand</c>, <c>ReplaceShapeCommand</c>'s 1-index
    /// swap.</summary>
    Updated,

    /// <summary>L3a (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md R-L3a-4) — ONLY
    /// <see cref="LayoutView.Instances"/> changed; <see cref="LayoutView.Shapes"/> is untouched. Every
    /// instance-mutating command (add/move/delete/array-edit/retarget) uses this instead of the
    /// shape-focused kinds above, so <see cref="LayoutSpatialIndex.Apply"/> can skip shape work
    /// entirely and just mark the (cheap, rare) instance side of the tree dirty.</summary>
    InstancesChanged,
}

/// <summary>
/// Minimal description of what changed in a <see cref="LayoutView.Shapes"/> mutation, carried by
/// <see cref="LayoutView.Changed"/> so the spatial index (and any other future subscriber) can update
/// incrementally instead of rescanning everything. Immutable; construct via the static factories, which
/// each collapse to <see cref="LayoutChangeInfo.Full"/> for a degenerate (empty) input rather than
/// requiring every caller to guard that themselves.
/// </summary>
public sealed class LayoutChangeInfo : EventArgs
{
    public LayoutChangeKind Kind { get; }

    /// <summary>Appended/RemovedTrailing only — the first affected index.</summary>
    public int StartIndex { get; }

    /// <summary>Appended/RemovedTrailing only — how many contiguous indices, starting at <see cref="StartIndex"/>.</summary>
    public int Count { get; }

    /// <summary>Updated only — the indices whose occupant changed in place.</summary>
    public IReadOnlyList<int>? Indices { get; }

    private LayoutChangeInfo(LayoutChangeKind kind, int startIndex, int count, IReadOnlyList<int>? indices)
    {
        Kind = kind;
        StartIndex = startIndex;
        Count = count;
        Indices = indices;
    }

    public static readonly LayoutChangeInfo Full = new(LayoutChangeKind.Full, 0, 0, null);

    public static LayoutChangeInfo Appended(int startIndex, int count) =>
        count <= 0 ? Full : new LayoutChangeInfo(LayoutChangeKind.Appended, startIndex, count, null);

    public static LayoutChangeInfo RemovedTrailing(int startIndex, int count) =>
        count <= 0 ? Full : new LayoutChangeInfo(LayoutChangeKind.RemovedTrailing, startIndex, count, null);

    public static LayoutChangeInfo Updated(IReadOnlyList<int> indices) =>
        indices.Count == 0 ? Full : new LayoutChangeInfo(LayoutChangeKind.Updated, 0, 0, indices);

    public static readonly LayoutChangeInfo InstancesOnly = new(LayoutChangeKind.InstancesChanged, 0, 0, null);
}
