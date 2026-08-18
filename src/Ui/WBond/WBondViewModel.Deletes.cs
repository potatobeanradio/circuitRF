using System;
using System.Linq;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Deleting part of one wire — a vertex, a segment, or the whole wire (owner, 2026-08-16).
///
/// <para>The layout view's wire context menu had no delete commands at all: the only way to remove
/// anything was Del on a selection, which deletes whole wires and nothing finer. These three are what
/// a right-click on a point, on a segment, and on a wire respectively should offer.</para>
///
/// <para><b>Removing a point has no side effect beyond removing it</b> (2026-08-18). It used to also
/// detach the wire from its loop profile, because a profile defined the point set and the next
/// re-apply would have put the point back. Loop profiles are gone — a wire's points are the only
/// truth about its shape — so a delete is now just a delete, and it no longer recolours the wire in
/// the layout view as a side effect.</para>
/// </summary>
public sealed partial class WBondViewModel
{
    /// <summary>
    /// The fewest points a wire can have: two feet and the straight chord between them. Below this
    /// there is no wire — <see cref="WireMesh.Build"/> has nothing to flatten into a filament.
    /// </summary>
    public const int MinimumWirePoints = 2;

    /// <summary>Why a delete is unavailable, or null when it can go ahead.</summary>
    /// <remarks>Mirrors the layout editor's <c>DeleteVertexAvailability</c>: the menu item is SHOWN
    /// and disabled with its reason on the tooltip, never silently absent — an item that vanishes
    /// reads as the feature being broken, and one that no-ops reads as the click being missed.</remarks>
    public string? WhyCannotDeletePoint(int wireIndex, int pointIndex)
    {
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return "No wire here.";
        if (pointIndex < 0 || pointIndex >= wire.Points.Count) return "No point here.";

        return wire.Points.Count <= MinimumWirePoints
            ? "A wire needs at least two points — delete the wire instead."
            : null;
    }

    /// <inheritdoc cref="WhyCannotDeletePoint"/>
    public string? WhyCannotDeleteSegment(int wireIndex, int segmentIndex)
    {
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return "No wire here.";
        if (segmentIndex < 0 || segmentIndex >= wire.Points.Count - 1) return "No segment here.";

        return wire.Points.Count <= MinimumWirePoints
            ? "A wire's only segment is the wire — delete the wire instead."
            : null;
    }

    /// <summary>
    /// Removes one point from one wire.
    ///
    /// <para><b>Structural.</b> The point count is the filament count, so the flat layout no longer
    /// matches and the mesh is rebuilt — this is the expensive path, which is why it is a discrete
    /// command and not something a drag can trigger.</para>
    /// </summary>
    /// <returns>True when a point was removed.</returns>
    public bool DeleteWirePoint(int wireIndex, int pointIndex)
    {
        if (WhyCannotDeletePoint(wireIndex, pointIndex) is not null) return false;

        var wire = _design.AllWires().ElementAt(wireIndex);

        PushUndo();
        wire.Points.RemoveAt(pointIndex);

        // The selection indexes points within this wire, so anything it holds past the removed one
        // now names the wrong point. Dropping it is the same rule DeleteSelectedWires applies.
        Selection = new WireSelection();
        CommitStructuralChange();
        return true;
    }

    /// <summary>
    /// Removes one segment from one wire, closing the gap — the wire stays ONE wire and comes back
    /// with exactly one fewer segment.
    ///
    /// <para><b>It deletes the segment's far endpoint</b>, so the join is made between the point
    /// before the segment and the point after it. Two consequences worth stating rather than
    /// discovering: the kink at that end of the segment goes with it, and deleting the LAST segment
    /// removes the output foot, which moves where the wire lands. Splitting the wire in two was the
    /// alternative and is not offered — two disconnected halves of a bond wire are not a thing the
    /// physics can evaluate, and the reduction sums current along one continuous path (§3.4).</para>
    /// </summary>
    /// <returns>True when a segment was removed.</returns>
    public bool DeleteWireSegment(int wireIndex, int segmentIndex)
    {
        if (WhyCannotDeleteSegment(wireIndex, segmentIndex) is not null) return false;

        return DeleteWirePoint(wireIndex, segmentIndex + 1);
    }

    /// <summary>
    /// <b>Delete, dispatched on WHAT is selected</b> — the Del key's one implementation for both
    /// canvases (owner, 2026-08-17: <i>"whole wires are deleted when the user selects segments and
    /// uses the Delete keystroke; only the segments should be deleted"</i>).
    ///
    /// <para>It used to call <see cref="DeleteSelectedWires"/> unconditionally, so the finest thing
    /// the key could remove was a whole wire however carefully the user had picked a segment — while
    /// the context menu, one right-click away, could remove exactly that segment. The selection
    /// already distinguishes the three cases; nothing was reading it.</para>
    ///
    /// <list type="bullet">
    ///   <item>Wholly-selected WIRES are deleted whole.</item>
    ///   <item>SEGMENTS and POINTS on wires that are not wholly selected remove just those points —
    ///     a segment through its far endpoint, exactly as <see cref="DeleteWireSegment"/> defines
    ///     it, so the wire stays one wire with the gap closed.</item>
    /// </list>
    ///
    /// <para><b>Points come off in DESCENDING order</b>, because every index past a removed one shifts;
    /// and a wire is never taken below <see cref="MinimumWirePoints"/> — that request is refused by
    /// name rather than silently leaving the wire at two points, since "delete the wire instead" is
    /// the thing the user needs told.</para>
    ///
    /// <para>ONE undo entry for the whole batch, and one structural rebuild at the end.</para>
    /// </summary>
    /// <returns>How many wires were deleted or edited.</returns>
    public int DeleteSelection()
    {
        var selection = Selection;
        if (selection.IsEmpty) return 0;

        var wholeWires = new HashSet<int>(selection.Wires);

        // Point indices to remove, per wire — a segment names its FAR endpoint (DeleteWireSegment's
        // own rule). Wires being deleted whole are skipped: removing points from them first would be
        // work thrown away, and would count twice in the result.
        var pointsByWire = new Dictionary<int, SortedSet<int>>();

        void Want(int wire, int point)
        {
            if (wholeWires.Contains(wire)) return;
            if (!pointsByWire.TryGetValue(wire, out var set)) pointsByWire[wire] = set = [];
            set.Add(point);
        }

        foreach (var p in selection.Points) Want(p.Wire, p.Point);
        foreach (var s in selection.Segments) Want(s.Wire, s.Point + 1);

        var wires = _design.AllWires().ToList();
        int refusedFor = 0;
        int edited = 0;

        bool pushed = PushUndo();

        foreach (var (wireIndex, points) in pointsByWire)
        {
            if (wireIndex < 0 || wireIndex >= wires.Count) continue;

            var wire = wires[wireIndex];
            if (wire.Points.Count - points.Count < MinimumWirePoints) { refusedFor++; continue; }

            foreach (int point in points.Reverse())
                if (point >= 0 && point < wire.Points.Count)
                    wire.Points.RemoveAt(point);

            edited++;
        }

        int removedWires = 0;
        if (wholeWires.Count > 0)
        {
            // Walk the flat index space once, keeping what is not selected — the same rebuild-in-place
            // DeleteSelectedWires does, inlined here so the whole batch is one undo entry rather than
            // two.
            int flat = 0;
            foreach (var array in _design.Arrays)
            {
                var keep = new List<Wire>(array.Wires.Count);
                foreach (var wire in array.Wires)
                {
                    if (wholeWires.Contains(flat)) removedWires++;
                    else keep.Add(wire);
                    flat++;
                }

                array.Wires.Clear();
                array.Wires.AddRange(keep);
            }

            PruneEmptyGroups();
        }

        if (edited == 0 && removedWires == 0)
        {
            if (pushed) DropUndoEntry();
            if (refusedFor > 0)
                ReportRefusal(refusedFor == 1
                    ? "A wire needs at least two points — delete the wire instead."
                    : $"{refusedFor} wires would be left with fewer than two points — delete them instead.");
            return 0;
        }

        // Every flat index may have shifted, and every point index within an edited wire has.
        Selection = new WireSelection();
        CommitStructuralChange();

        if (refusedFor > 0)
            ReportRefusal($"{refusedFor} wire(s) were left alone — a wire needs at least two points.");

        return edited + removedWires;
    }

    /// <summary>
    /// Why the whole wire cannot be deleted, or null.
    ///
    /// <para><b>The last wire CAN be deleted</b> (owner, 2026-08-16: "make it support 0 wires"). This
    /// used to refuse it, because <c>WBondDesign.Validate</c> rejected a design with no arrays and the
    /// delete would have been rolled back and reported as a mapping-matrix failure. An empty design is
    /// now a valid one — the last group is pruned with the rest — so the only thing left to refuse is
    /// an index that names no wire.</para>
    /// </summary>
    public string? WhyCannotDeleteWire(int wireIndex) =>
        wireIndex < 0 || wireIndex >= _design.WireCount ? "No wire here." : null;

    /// <summary>
    /// Removes one whole wire, and its group with it when that leaves the group empty — see
    /// <c>PruneEmptyGroups</c> for why an empty group cannot simply be kept.
    /// </summary>
    /// <returns>True when a wire was removed.</returns>
    public bool DeleteWire(int wireIndex)
    {
        if (WhyCannotDeleteWire(wireIndex) is not null) return false;

        int flat = 0;
        foreach (var array in _design.Arrays)
        {
            for (int w = 0; w < array.Wires.Count; w++, flat++)
            {
                if (flat != wireIndex) continue;

                PushUndo();
                array.Wires.RemoveAt(w);
                PruneEmptyGroups();

                // Every flat index after this one has shifted.
                Selection = new WireSelection();
                CommitStructuralChange();
                return true;
            }
        }

        return false;
    }
}
