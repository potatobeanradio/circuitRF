using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// Reshaping one wire without adding or removing wires — <b>Add Vertex</b> and <b>Straighten Wire</b>
/// (owner, 2026-08-17). The counterpart of <c>WBondViewModel.Deletes.cs</c>, which takes points away.
///
/// <h3>Both are context-menu commands, and both start from what the right-click LANDED on</h3>
/// <para>They sit with Delete Vertex / Delete Segment / Delete Wire and follow the same rule those
/// three established: a command named for one wire that quietly acted on forty is expensive to
/// notice. <b>Straighten is the one exception, and it is the owner's own</b> (2026-08-17): with
/// SEVERAL wires selected it straightens all of them, because that is what its plural spelling
/// promises. One selected wire never overrides the click — see the menu item for that boundary.</para>
///
/// <h3>The geometry itself is <c>WireEdits</c>'</h3>
/// <para>These are the undo, the mesh and the readout — the rules that can be wrong are next door,
/// framework-free and tested against arithmetic rather than through a canvas.</para>
/// </summary>
public sealed partial class WBondViewModel
{
    /// <summary>Why a vertex cannot be added there, or null when it can.</summary>
    /// <remarks>Same shape as <see cref="WhyCannotDeletePoint"/>: the menu item is SHOWN and disabled
    /// with its reason, never silently absent.</remarks>
    public string? WhyCannotAddPoint(int wireIndex, int segmentIndex)
    {
        if (_design.AllWires().ElementAtOrDefault(wireIndex) is not { } wire) return "No wire here.";

        return segmentIndex < 0 || segmentIndex >= wire.Points.Count - 1
            ? "Right-click a wire segment."
            : null;
    }

    /// <summary>
    /// Adds a vertex to <paramref name="wireIndex"/> on <paramref name="segmentIndex"/>, at parameter
    /// <paramref name="t"/> along it — collinear with the two points it sits between, and at their
    /// interpolated z (<see cref="WireEdits.InsertPointOnSegment"/>).
    ///
    /// <para><b>Structural</b>, like every point-count change: the point count IS the filament count,
    /// so the mesh is rebuilt rather than patched. And it DETACHES the wire from its profile for the
    /// reason the deletes already document — a <see cref="LoopProfile"/> defines the point set, so a
    /// wire with an extra point no longer follows it and the next Re-apply would silently take the
    /// vertex away again.</para>
    /// </summary>
    /// <param name="t">
    /// Where along the segment, 0..1 — the caller's own projection of the click, in whichever plane
    /// that caller works in. Clamped downstream.
    /// </param>
    /// <returns>True when a vertex was added.</returns>
    public bool AddWirePoint(int wireIndex, int segmentIndex, double t)
    {
        if (WhyCannotAddPoint(wireIndex, segmentIndex) is not null) return false;

        var wire = _design.AllWires().ElementAt(wireIndex);

        PushUndo();

        if (WireEdits.InsertPointOnSegment(wire, segmentIndex, t) < 0)
        {
            DropUndoEntry();
            return false;
        }

        ProfileEnvelope.Detach(wire);

        // Point indices past the insertion have shifted, so anything the selection holds within this
        // wire now names a different point — the same rule DeleteWirePoint applies.
        Selection = new WireSelection();
        CommitStructuralChange();
        return true;
    }

    /// <summary>Why nothing in <paramref name="wireIndices"/> can be straightened, or null.</summary>
    /// <remarks>
    /// Enabled when ANY of them has something between its feet: a batch that includes one two-point
    /// wire is not a batch to refuse, it is a batch with one wire that has nothing to do.
    /// </remarks>
    public string? WhyCannotStraighten(IReadOnlyCollection<int> wireIndices)
    {
        if (wireIndices.Count == 0) return "Right-click a wire, or select the wires to straighten.";

        var wires = _design.AllWires().ToList();
        bool any = wireIndices.Any(i => i >= 0 && i < wires.Count && wires[i].Points.Count >= 3);

        return any
            ? null
            : wireIndices.Count == 1
                ? "This wire has only its two feet — there is nothing between them to straighten."
                : "These wires have only their two feet — there is nothing between them to straighten.";
    }

    /// <inheritdoc cref="WhyCannotStraighten(IReadOnlyCollection{int})"/>
    public string? WhyCannotStraighten(int wireIndex) =>
        _design.AllWires().ElementAtOrDefault(wireIndex) is null
            ? "No wire here."
            : WhyCannotStraighten([wireIndex]);

    /// <summary>
    /// Straightens one wire — <see cref="StraightenWires"/> for one.
    /// </summary>
    /// <returns>True when anything moved.</returns>
    public bool StraightenWire(int wireIndex) => StraightenWires([wireIndex]) > 0;

    /// <summary>
    /// Straightens each of <paramref name="wireIndices"/> in the XY plane — lateral bow removed, loop
    /// height untouched (<see cref="WireEdits.StraightenXy"/>).
    ///
    /// <para><b>Each wire straightens about its OWN two feet</b> (owner, 2026-08-17: <i>"each wire must
    /// be straightened individually using its own anchors"</i>). Not a shared chord — an array fanning
    /// out from a common pad has a different direction per wire, and one chord for all of them would
    /// swing every wire onto the first one's line and destroy exactly the geometry the flexible model
    /// exists to allow. It is the same reasoning WB24c gives for scaling by FACTOR rather than to a
    /// common value.</para>
    ///
    /// <para><b>The drag path, not the structural one:</b> points move, none are added or removed, so
    /// this is a rank-2 fill update rather than a rebuild — one update for the whole batch.</para>
    /// </summary>
    /// <returns>How many wires actually moved.</returns>
    public int StraightenWires(IReadOnlyCollection<int> wireIndices)
    {
        ArgumentNullException.ThrowIfNull(wireIndices);
        if (wireIndices.Count == 0) return 0;

        var wires = _design.AllWires().ToList();
        var moved = new List<int>(wireIndices.Count);

        bool pushed = PushUndo();

        foreach (int index in wireIndices)
        {
            if (index < 0 || index >= wires.Count) continue;
            if (WireEdits.StraightenXy(wires[index]) > 0) moved.Add(index);
        }

        if (moved.Count == 0)
        {
            // Already straight. No undo entry for an edit that changed nothing — a Ctrl+Z that
            // appears to do nothing is worse than one that is not offered.
            if (pushed) DropUndoEntry();
            return 0;
        }

        CommitPointMove(moved);
        return moved.Count;
    }
}
