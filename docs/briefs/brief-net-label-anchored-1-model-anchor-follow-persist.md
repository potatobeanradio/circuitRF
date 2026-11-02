# Brief: schematic net labels anchored to wire — Brief 1 of 2 (model + anchor + follow + persistence)

Net labels are created only by double-clicking a wire. Make a label **belong to that one wire + the
segment nearest the placement point**, and make its position **derive from the wire each render** so it
follows when the wire (or that segment) moves, and round-trips through save/load.

Cascade-delete-on-wire-delete and the orphan-revalidation sweep are **Brief 2** — not here.

## Design

A label stores an anchor (source of truth) instead of a free X,Y:
```
foot = A + AlongT*(B − A)         where (A,B) = ownerWire.Points[SegmentIndex], [SegmentIndex+1]
draw = (foot.X + OffsetX, foot.Y + OffsetY)     // OffsetX/Y = the perpendicular gap captured at creation
```
`X,Y` remain on the label as a **derived cache** (hit-test and the renderer read them); they are recomputed
from the anchor in `BuildRenderModel`. A pure translation of the wire, or a perpendicular drag of that
segment, moves `foot`, so the label follows automatically; undo restores the wire points, so it follows
back — **no per-move snapshots needed**.

Files: `EditableSchematic.cs`, `SchematicViewModel.cs`, `SchematicPersistence.cs`. Size: **S–M**.

## 1. `EditableNetLabel` (EditableSchematic.cs)

Replace the class with:
```csharp
/// <summary>A user-placed net label (§4.4), anchored to the wire it was created on. Its draw position
/// (X,Y) is DERIVED from the owner wire's geometry each build (see RecomputePosition).</summary>
public sealed class EditableNetLabel
{
    public string Id   { get; } = Guid.NewGuid().ToString("N")[..12];
    public double X    { get; set; }   // DERIVED draw origin (recomputed from the anchor each build)
    public double Y    { get; set; }
    public string Name { get; set; } = "";

    // ── Wire anchor (source of truth for position) ──
    public string OwnerWireId  { get; set; } = "";
    public int    SegmentIndex { get; set; }
    public double AlongT       { get; set; }   // foot parameter on the segment, 0..1
    public double OffsetX      { get; set; }   // world offset foot → draw origin (the perpendicular gap)
    public double OffsetY      { get; set; }

    public bool IsAnchored => OwnerWireId.Length > 0;

    /// <summary>Anchors this label to <paramref name="wire"/> by projecting (px,py) onto its nearest
    /// segment: stores SegmentIndex + AlongT (foot parameter) and the residual as a world offset.</summary>
    public void AnchorToWire(EditableWire wire, double px, double py)
    {
        OwnerWireId = wire.Id;
        var pts = wire.Points;
        int bestSeg = 0; double bestT = 0, bestDsq = double.PositiveInfinity, fx = px, fy = py;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var (ax, ay) = pts[i]; var (bx, by) = pts[i + 1];
            double dx = bx - ax, dy = by - ay, lenSq = dx * dx + dy * dy;
            double t  = lenSq < 1e-10 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0, 1);
            double cx = ax + t * dx, cy = ay + t * dy;
            double dsq = (px - cx) * (px - cx) + (py - cy) * (py - cy);
            if (dsq < bestDsq) { bestDsq = dsq; bestSeg = i; bestT = t; fx = cx; fy = cy; }
        }
        SegmentIndex = bestSeg; AlongT = bestT;
        OffsetX = px - fx; OffsetY = py - fy;
        X = px; Y = py;
    }

    /// <summary>Recomputes X,Y from the owner wire's current segment geometry. Returns false when the
    /// stored SegmentIndex no longer exists (the wire was shortened) — caller treats it as an orphan
    /// and skips it.</summary>
    public bool RecomputePosition(EditableWire wire)
    {
        var pts = wire.Points;
        if (SegmentIndex < 0 || SegmentIndex >= pts.Count - 1) return false;
        var (ax, ay) = pts[SegmentIndex]; var (bx, by) = pts[SegmentIndex + 1];
        double fx = ax + AlongT * (bx - ax), fy = ay + AlongT * (by - ay);
        X = fx + OffsetX; Y = fy + OffsetY;
        return true;
    }
}
```

## 2. Anchor at creation (SchematicViewModel.cs)

In `CommitInlineEdit`, the `InlineEditKind.WireNetLabel` case, **new-label `else` branch only**. `targetId`
is the double-clicked wire id; `worldX/worldY` is the label draw point. Replace:
```csharp
                else
                {
                    // Use the placement coordinates as-is: the perpendicular gap was computed
                    // from the wire's exact position in ClassifySegmentAt, so grid-snapping
                    // the perpendicular axis would round it back onto the wire.
                    Execute(new PlaceNetLabelCommand(EditModel,
                        new EditableNetLabel { Name = newVal, X = worldX, Y = worldY }));
                }
```
with:
```csharp
                else
                {
                    // Anchor the new label to the double-clicked wire (targetId): project the placement
                    // point onto its nearest segment so the label rides that segment. The perpendicular
                    // gap from ClassifySegmentAt is captured as the anchor's world offset.
                    var label     = new EditableNetLabel { Name = newVal, X = worldX, Y = worldY };
                    var ownerWire = EditModel.FindWire(targetId ?? "");
                    if (ownerWire is not null && ownerWire.Points.Count >= 2)
                        label.AnchorToWire(ownerWire, worldX, worldY);
                    Execute(new PlaceNetLabelCommand(EditModel, label));
                }
```
Leave the empty-clear, rename, and existing-label branches unchanged.

## 3. Recompute in `BuildRenderModel` (EditableSchematic.cs)

Replace:
```csharp
        var netLabels = NetLabels.Select(l => new SchematicNetLabel { Id = l.Id, X = l.X, Y = l.Y, Name = l.Name }).ToList();
```
with:
```csharp
        var netLabels = new List<SchematicNetLabel>(NetLabels.Count);
        foreach (var l in NetLabels)
        {
            if (l.IsAnchored)
            {
                var ow = FindWire(l.OwnerWireId);
                if (ow is null || !l.RecomputePosition(ow))
                    continue;   // orphan (owner gone / segment shortened) — not rendered; Brief 2 removes it
            }
            netLabels.Add(new SchematicNetLabel { Id = l.Id, X = l.X, Y = l.Y, Name = l.Name });
        }
```
`RecomputePosition` writes X,Y back onto the `EditableNetLabel`, so `SchematicHitTest` (which reads
`lbl.X/lbl.Y`) stays correct. Unanchored labels (legacy / loaded without an anchor) render at their stored
X,Y.

## 4. Persistence (SchematicPersistence.cs) — additive, NO format-version bump

The anchor persists by **wire list-index** because ids are minted fresh on import. New fields are
defaulted/nullable, so old files load fine (unanchored) — adding defaulted fields needs no version change.

`CschNetLabel`:
```csharp
public sealed class CschNetLabel
{
    // Id is NOT persisted — assigned fresh on import.
    public double X    { get; set; }
    public double Y    { get; set; }
    public string Name { get; set; } = "";

    // Wire anchor. OwnerWireIndex null ⇒ legacy/unanchored (omitted from file when null).
    public int?   OwnerWireIndex { get; set; }
    public int    SegmentIndex   { get; set; }
    public double AlongT         { get; set; }
    public double OffsetX        { get; set; }
    public double OffsetY        { get; set; }
}
```
Write — `ToFileModel`, replace:
```csharp
        foreach (var n in m.NetLabels)
            file.NetLabels.Add(new CschNetLabel { X = n.X, Y = n.Y, Name = n.Name });
```
with:
```csharp
        foreach (var n in m.NetLabels)
        {
            int? ownerIdx = null;
            if (n.IsAnchored)
            {
                int idx = m.Wires.FindIndex(w => w.Id == n.OwnerWireId);
                if (idx >= 0) ownerIdx = idx;
            }
            file.NetLabels.Add(new CschNetLabel
            {
                X = n.X, Y = n.Y, Name = n.Name,
                OwnerWireIndex = ownerIdx,
                SegmentIndex   = n.SegmentIndex, AlongT = n.AlongT,
                OffsetX        = n.OffsetX, OffsetY = n.OffsetY,
            });
        }
```
Read — `FromFileModel`, replace:
```csharp
        foreach (var n in file.NetLabels)
            m.NetLabels.Add(new EditableNetLabel { X = n.X, Y = n.Y, Name = n.Name });
```
with:
```csharp
        foreach (var n in file.NetLabels)
        {
            var lbl = new EditableNetLabel { X = n.X, Y = n.Y, Name = n.Name };
            if (n.OwnerWireIndex is int wi && wi >= 0 && wi < m.Wires.Count)
            {
                lbl.OwnerWireId  = m.Wires[wi].Id;
                lbl.SegmentIndex = n.SegmentIndex;
                lbl.AlongT       = n.AlongT;
                lbl.OffsetX      = n.OffsetX;
                lbl.OffsetY      = n.OffsetY;
            }
            m.NetLabels.Add(lbl);
        }
```
(`m.Wires` is fully populated before the net-label loop in `FromFileModel` — keep that order so the index
resolves. It already is.)

## Verification

1. Double-click a wire, type a name → label appears where it did before.
2. Select the wire, drag it, release → label sits on the wire at the new position. Undo → both return.
3. Perpendicular-drag the segment the label sits on, release → label rides the segment. Undo → returns.
4. Save, reload → label is in the same place and still follows wire moves (anchor round-tripped).
5. Re-double-click that wire near the label → edits the existing label (unchanged behaviour).
6. Open a pre-existing .csch (no anchor data) → its net labels load and render at their saved X,Y.

## Acceptance

- New net labels anchor to the double-clicked wire + nearest segment; X,Y derive from the wire each build.
- Whole-wire moves and non-renumbering segment drags carry the label, with working undo, **no new snapshots**.
- Anchor round-trips through save/load with **no format-version change**; pre-existing files still load.
- No change to component labels, dots, or net extraction.

## Known v1 limitations (handled in Brief 2 / optional Brief 3)

- **Deleting the owner wire** hides the label (BuildRenderModel skips the orphan) but does not yet remove it
  from the model or cascade through undo — **Brief 2**.
- A segment drag that **renumbers** the wire (a pinned-end jog, or a drag that flattens a jog so collinear
  segments merge) can leave `SegmentIndex` pointing at a different segment — **Brief 2** adds a re-anchor on
  structural wire edits. Ordinary interior perpendicular drags do **not** renumber and work here.
- The label snaps onto the wire **on drag release** (the live-drag overlay path renders labels from the
  committed snapshot); live-follow during the drag is an optional **Brief 3**.
