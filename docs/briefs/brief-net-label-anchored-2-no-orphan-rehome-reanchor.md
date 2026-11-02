# Brief: schematic net labels anchored to wire — Brief 2 of 2 (no-orphan invariant + re-home + re-anchor)

Enforce: **a net label is never left hanging in space.** When its owner wire is deleted → the label is
removed; when its owner wire is merged or split (geometry preserved) → the label re-homes to the surviving
wire; when its owner wire is renumbered by an edit → the label re-anchors. All of this rides the **existing**
central post-edit pass so it is one undoable operation with the edit, and **no individual command changes**.

Depends on Brief 1 (the anchor fields + `AnchorToWire`/`RecomputePosition`).

## Why this is centralized

`SchematicViewModel.Execute` wraps every edit:
```csharp
public void Execute(IUiCommand cmd) => _undoRedo.Execute(new DotRevalidationCommand(EditModel, cmd));
```
`DotRevalidationCommand` already runs the junction-dot invariant + degenerate-wire cleanup after the inner
edit and folds them into the same undo. We add a net-label pass there. So delete / merge / segment-split /
segment-drag / paste are all covered at one point.

Size: **S**. Files: `EditableSchematic.cs`, `DotRevalidationCommand.cs`.

## 1. `SchematicEditModel` — revalidation pass + a wire-under-point helper (EditableSchematic.cs)

Add to `SchematicEditModel` (near `FindInvalidDots`):
```csharp
/// <summary>Snapshot of a net label's anchor before revalidation changed it (for undo).</summary>
public readonly record struct NetLabelAnchorSnap(
    EditableNetLabel Label,
    string OwnerWireId, int SegmentIndex, double AlongT,
    double OffsetX, double OffsetY, double X, double Y);

/// <summary>What a net-label revalidation pass changed (for undo by the wrapping command).</summary>
public readonly record struct NetLabelRevalidation(
    List<(EditableNetLabel Label, int Index)> Removed,
    List<NetLabelAnchorSnap> Reanchored);

/// <summary>First wire whose body passes through (px,py) within <paramref name="tol"/>, else null.</summary>
public EditableWire? WireUnderPoint(double px, double py, double tol)
{
    foreach (var w in Wires)
    {
        var pts = w.Points;
        for (int i = 0; i < pts.Count - 1; i++)
            if (SchematicGeometry.PointOnSegment(px, py, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                return w;
    }
    return null;
}

/// <summary>
/// Re-enforces the net-label invariant after a geometry edit (no label hangs unassigned):
///  • valid anchor (owner exists, segment in range) → untouched (BuildRenderModel keeps X,Y fresh);
///  • owner exists but segment renumbered/shortened → re-anchor on the same wire at the current draw point;
///  • owner gone (deleted / merged / split) → re-home to a wire under the label's foot if one exists
///    (merge & split preserve geometry, so the foot still lands on the surviving wire), else remove it.
/// Returns the changes so the wrapping command can undo them. Mutates in place.
/// </summary>
public NetLabelRevalidation RevalidateNetLabels()
{
    List<(EditableNetLabel, int)> removed = [];
    List<NetLabelAnchorSnap>      reanchored = [];

    for (int i = NetLabels.Count - 1; i >= 0; i--)
    {
        var l = NetLabels[i];
        if (!l.IsAnchored) continue;   // legacy free label — leave alone

        var owner = FindWire(l.OwnerWireId);
        if (owner is not null && l.SegmentIndex >= 0 && l.SegmentIndex < owner.Points.Count - 1)
            continue;                  // valid anchor — no change needed

        var snap = new NetLabelAnchorSnap(
            l, l.OwnerWireId, l.SegmentIndex, l.AlongT, l.OffsetX, l.OffsetY, l.X, l.Y);

        if (owner is not null)
        {
            // Owner exists but its segment list changed under the label — re-anchor on it,
            // keeping the label's current draw position.
            l.AnchorToWire(owner, l.X, l.Y);
            reanchored.Add(snap);
            continue;
        }

        // Owner gone. Re-home to a wire coincident with the label's foot (merge/split keep geometry);
        // if the foot lies on no wire, the node is gone → remove the label.
        double footX = l.X - l.OffsetX, footY = l.Y - l.OffsetY;
        var host = WireUnderPoint(footX, footY, ConnectTolerance);
        if (host is not null)
        {
            l.AnchorToWire(host, l.X, l.Y);
            reanchored.Add(snap);
        }
        else
        {
            removed.Add((l, i));
            NetLabels.RemoveAt(i);
        }
    }

    return new NetLabelRevalidation(removed, reanchored);
}
```

## 2. `DotRevalidationCommand` — run the net-label pass + undo it (DotRevalidationCommand.cs)

Add fields:
```csharp
    private List<(EditableNetLabel Label, int Index)>      _removedLabels    = [];
    private List<SchematicEditModel.NetLabelAnchorSnap>    _reanchoredLabels = [];
```

In `Execute`, after the invalid-dots block and **before** the `NotifyChanged` line, add:
```csharp
        // Net-label invariant: re-home or remove labels whose owner wire changed, so none is left
        // hanging unassigned. Part of the same undoable edit.
        var nl = _model.RevalidateNetLabels();
        _removedLabels    = nl.Removed;
        _reanchoredLabels = nl.Reanchored;
```
and change the final notify guard to include them:
```csharp
        if (_removedWires.Count > 0 || _removedDots.Count > 0
            || _removedLabels.Count > 0 || _reanchoredLabels.Count > 0) _model.NotifyChanged();
```

In `Undo`, **before** `_inner.Undo();` (after the dot/wire re-inserts), add:
```csharp
        // Restore net-label anchors changed by revalidation, then re-insert removed labels — both
        // BEFORE _inner.Undo() so the restored owner wire ids match the wires it brings back.
        foreach (var s in _reanchoredLabels)
        {
            s.Label.OwnerWireId  = s.OwnerWireId;
            s.Label.SegmentIndex = s.SegmentIndex;
            s.Label.AlongT       = s.AlongT;
            s.Label.OffsetX      = s.OffsetX;
            s.Label.OffsetY      = s.OffsetY;
            s.Label.X            = s.X;
            s.Label.Y            = s.Y;
        }
        foreach (var (label, idx) in _removedLabels.OrderBy(t => t.Index))
            _model.NetLabels.Insert(Math.Min(idx, _model.NetLabels.Count), label);
```
Update the class summary to note it now also enforces the net-label invariant (keep the class name to
avoid churn, or rename to `PostEditRevalidationCommand` if preferred — optional).

## Verification

1. **Delete-cascade.** Label a wire; delete that wire → the label disappears. Undo → wire **and** label
   return. Redo → both gone again.
2. **Delete a different wire** that shares the labeled node (junction) → label survives, now attached to the
   remaining wire (re-homed); the invariant "always on a wire" holds.
3. **Merge.** Label a wire, then draw/drag another wire so they merge into one → label survives on the merged
   wire and still follows it. Undo → original two wires + original anchor.
4. **Segment delete / split.** Delete a *different* segment of the labeled wire (splitting it) → label stays
   on the surviving piece. Delete the segment the label sits on → label is removed. Undo restores both.
5. **Renumbering segment drag** (pinned-end jog, or a drag that flattens a jog so segments merge) → label
   stays on the wire (re-anchored), not hidden, not lost. Undo restores it.
6. Unrelated edits (move a component, edit a parameter) leave every label untouched (fast-path skip).

## Acceptance

- A net label whose owner wire is deleted is removed; one whose wire merges/splits re-homes to the surviving
  wire; one whose owner renumbers re-anchors. Never hidden, never orphaned.
- All of it is a single undoable step with the triggering edit (no separate command, no per-command edits).
- No change to component labels, dots, degenerate-wire cleanup, or net extraction.

## Notes / not in scope

- A loaded file with a hand-corrupted anchor (segment out of range) is hidden by Brief 1's build guard until
  the first edit triggers this pass; a one-line `EditModel.RevalidateNetLabels()` after load would fix it
  eagerly if ever wanted — left out to keep persistence free of editor invariants.
- Live-follow during a drag (label tracks the wire mid-drag rather than snapping on release) remains the
  optional Brief 3.
