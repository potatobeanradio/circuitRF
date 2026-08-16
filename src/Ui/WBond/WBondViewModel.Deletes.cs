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
/// <h3>Removing a point DETACHES the wire from its profile, and that is deliberate</h3>
/// <para>A <see cref="LoopProfile"/> IS the point set: it says how many points a bound wire has and
/// where each sits along the chord. A wire that has had one removed no longer follows it, so leaving
/// the binding in place would mean the next Re-apply Profile silently put the point back — and the
/// next group height edit would too. Detaching states the outcome instead. It is the same rule
/// <see cref="SetWireLoopHeight"/> already applies for the same reason, and the panel's Profile row
/// flips to "(free)" the moment it happens.</para>
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
        ProfileEnvelope.Detach(wire);   // see the class remarks — the profile defines the point set

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

    /// <inheritdoc cref="WhyCannotDeletePoint"/>
    public string? WhyCannotDeleteWire(int wireIndex)
    {
        if (wireIndex < 0 || wireIndex >= _design.WireCount) return "No wire here.";

        // Said HERE, in the user's terms, rather than left to the model: WBondDesign.Validate refuses
        // a design with no arrays, so deleting the last wire would be rolled back and reported as
        // "wBond design has no arrays" — true, and no help at all to someone who just pressed Delete.
        return _design.WireCount <= 1
            ? "A wBond design needs at least one wire."
            : null;
    }

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
