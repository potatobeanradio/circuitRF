# Brief: schematic net labels — live-follow during drag (Brief 3)

Make anchored net labels track their wire **during** a drag instead of snapping into place on release.
Mirrors how wires already drag: the overlay carries live wire points; we add a parallel per-label
position override, computed from those same live points so labels and wires move together. On release the
committed `BuildRenderModel` takes over (and re-anchors per Brief 2), so nothing else changes.

Size: **S**. Files: `SchematicOverlay.cs`, `SchematicViewModel.cs`, `SchematicRenderer.cs`.

## 1. `SchematicOverlay.cs` — add a per-label position override

After the `WireDragPoints` property, add:
```csharp
    /// <summary>
    /// Per-net-label world draw position during an active drag, keyed by label Id. Computed from the
    /// drag's live wire points (WireDragPoints) so anchored labels track their wire instead of lagging
    /// at their pre-drag spot. Non-null only while a drag that moves a labeled wire is in progress.
    /// </summary>
    public IReadOnlyDictionary<string, (double X, double Y)>? NetLabelDragPositions { get; init; }
```

## 2. `SchematicViewModel.cs` — compute live label positions from the live wire points

Add a helper (near `LiveConnectionDots` / the drag-overlay builders):
```csharp
    /// <summary>
    /// Live draw positions for anchored net labels whose owner wire is among <paramref name="livePts"/>
    /// (the drag's live wire points), computed from the moving segment geometry so labels track their
    /// wire during a drag. Null when no anchored label sits on a dragged wire.
    /// </summary>
    private IReadOnlyDictionary<string, (double X, double Y)>? BuildNetLabelDragPositions(
        IReadOnlyDictionary<string, IReadOnlyList<(double X, double Y)>>? livePts)
    {
        if (livePts is null || livePts.Count == 0 || EditModel.NetLabels.Count == 0) return null;
        Dictionary<string, (double X, double Y)>? result = null;
        foreach (var lbl in EditModel.NetLabels)
        {
            if (!lbl.IsAnchored) continue;
            if (!livePts.TryGetValue(lbl.OwnerWireId, out var pts)) continue;
            if (lbl.SegmentIndex < 0 || lbl.SegmentIndex >= pts.Count - 1) continue;
            var (ax, ay) = pts[lbl.SegmentIndex];
            var (bx, by) = pts[lbl.SegmentIndex + 1];
            double fx = ax + lbl.AlongT * (bx - ax);
            double fy = ay + lbl.AlongT * (by - ay);
            (result ??= [])[lbl.Id] = (fx + lbl.OffsetX, fy + lbl.OffsetY);
        }
        return result;
    }
```
(This is `RecomputePosition`'s math against the live points, without mutating the label.)

Wire it into both drag-overlay builders.

In **`RebuildDragOverlay`** (whole-wire + component drag), the `Overlay = new SchematicOverlay { … }`
that sets `WireDragPoints = wireOverrides` — add:
```csharp
            NetLabelDragPositions = BuildNetLabelDragPositions(wireOverrides),
```

In **`HandleSegmentDragLive`**, the `Overlay = new SchematicOverlay { … }` that sets
`WireDragPoints = wireDragPoints` — add:
```csharp
            NetLabelDragPositions = BuildNetLabelDragPositions(wireDragPoints),
```
(Both feed the same live point lists the renderer uses for the wires, so labels stay locked to their wire.
A segment drag that transiently renumbers a wire pushes `SegmentIndex` out of range → that label is
skipped for the frame and snaps in on commit — acceptable.)

## 3. `SchematicRenderer.cs` — draw labels at the override position when present

Replace the net-label block:
```csharp
        // ── Net labels ────────────────────────────────────────────────────────
        if (!isLod && !isSimplified && netLabelFont.Size >= 4f)
        {
            foreach (var lbl in model.NetLabels)
            {
                if (lbl.X < vpMinX - 200 || lbl.X > vpMaxX + 200 ||
                    lbl.Y < vpMinY - 50  || lbl.Y > vpMaxY + 50) continue;
                var (lx, ly) = ToPixel(lbl.X, lbl.Y, panX, panY, zoom);
                canvas.DrawText(lbl.Name, lx, ly, SKTextAlign.Left, netLabelFont, netLabelPaint);
            }
        }
```
with:
```csharp
        // ── Net labels ────────────────────────────────────────────────────────
        if (!isLod && !isSimplified && netLabelFont.Size >= 4f)
        {
            foreach (var lbl in model.NetLabels)
            {
                // Live drag: track the wire via the overlay override; else use the committed position.
                double lwx = lbl.X, lwy = lbl.Y;
                if (overlay?.NetLabelDragPositions is { } nlp && nlp.TryGetValue(lbl.Id, out var op))
                    (lwx, lwy) = op;
                if (lwx < vpMinX - 200 || lwx > vpMaxX + 200 ||
                    lwy < vpMinY - 50  || lwy > vpMaxY + 50) continue;
                var (lx, ly) = ToPixel(lwx, lwy, panX, panY, zoom);
                canvas.DrawText(lbl.Name, lx, ly, SKTextAlign.Left, netLabelFont, netLabelPaint);
            }
        }
```

## Verification

1. Label a wire, then drag the whole wire → the label moves continuously with it during the drag, not
   just on release. Release → label sits correctly (committed rebuild). Undo → both return.
2. Perpendicular-drag the labeled segment → the label rides it live.
3. Drag a component whose connected wire carries a label → the label follows the wire as it re-routes.
4. Drag an unrelated wire → labels on other wires don't move.
5. Cancel a drag (Esc) → labels return to their pre-drag positions (overlay cleared, render from model).

## Acceptance

- Anchored net labels track their wire throughout whole-wire drags, segment drags, and component-move
  wire-follows; they no longer jump only at release.
- Positions are computed from the same live wire points the renderer draws the wires from, so labels and
  wires stay locked together.
- No change to commit/undo, persistence, the one-per-node / merge-collapse rules, or extraction; a
  non-dragging frame has `NetLabelDragPositions == null` (no overhead).
