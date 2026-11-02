# Brief: polish-cyclic-clickthrough (B10) — repeated clicks cycle through stacked objects

**Depends on B9 (glyph-only hitbox) — land B9 first.**

**Goal.** Repeatedly left-clicking the *same point* cycles the selection through every object stacked
under the cursor, top → bottom, then wraps back to the top. This makes objects hidden behind others
(a wire under a component, a component behind a component, a wire under a wire) reachable. Shift-click
behavior is unchanged (add/remove the topmost).

Authority: laundry-list B10 ("clicking again selects the thing underneath; cyclic"). Decision (the owner):
**cyclic** — top → next → … → last → top. Size: **L**. Files:
`src/Ui/Schematic/SchematicHitTest.cs`, `src/Ui/ViewModels/SchematicViewModel.cs`, tests in `tests/Ui.Tests`.

## Today

`HandleSelectPress` calls `SchematicHitTest.Test(...)`, which returns only the **topmost** object. There
is no way to reach anything beneath it by clicking. We add a stack-returning hit-test and a small
pick-with-cycle step in front of the existing select branches.

## Change 1 — `SchematicHitTest.TestStack`

Add a new method that returns **all** selectable objects under the point, ordered top → bottom, **one
entry per object**, using the same per-kind tests and Z-priority as `Test` (labels excluded by default
since selection ignores them — B9). Add it next to `Test`; leave `Test` as-is (small duplication is fine
and keeps `Test`'s single-hit callers untouched).

```csharp
/// <summary>
/// Returns every selectable object under (worldX, worldY), ordered top→bottom (same Z-priority
/// as <see cref="Test"/>), at most one entry per object. Used for cyclic click-through selection.
/// Labels are excluded unless includeLabels is true (left-click selection ignores labels — B9).
/// Each wire contributes a single entry: a WireEndpoint (whole-wire) hit when the point is near an
/// endpoint, otherwise the WireSegment under the point.
/// </summary>
public static IReadOnlyList<HitResult> TestStack(
    SchematicEditModel  editModel,
    SchematicModel      renderModel,
    SchematicSpatialIndex index,
    double worldX, double worldY,
    double hitRadius = DefaultHitRadius,
    bool includeLabels = false)
{
    double half = hitRadius;
    var candComps = new HashSet<int>();
    var candWires = new HashSet<int>();
    index.QueryViewport(worldX - half, worldY - half, worldX + half, worldY + half,
                        candComps, candWires);

    var results = new List<HitResult>();

    // 1. Labels (only if requested) — topmost.
    if (includeLabels)
        foreach (int i in candComps.OrderByDescending(x => x))
        {
            if (i >= editModel.Components.Count) continue;
            var th = TestComponentLabels(editModel.Components[i], worldX, worldY);
            if (th.Kind != HitKind.None) results.Add(th);
        }

    // 2. Component glyphs (descending index = topmost first).
    foreach (int i in candComps.OrderByDescending(x => x))
    {
        if (i >= editModel.Components.Count) continue;
        var comp = editModel.Components[i];
        var (gMinX, gMinY, gMaxX, gMaxY) = GetCompGlyphBb(comp, editModel);
        if (worldX >= gMinX && worldX <= gMaxX && worldY >= gMinY && worldY <= gMaxY)
            results.Add(new HitResult(HitKind.Component, comp.Id));
    }

    // 3. Canvas objects (topmost first).
    for (int i = editModel.CanvasObjects.Count - 1; i >= 0; i--)
    {
        var obj = editModel.CanvasObjects[i];
        if (obj.IsLocked) continue;
        var bb = obj.GetBoundingBox();
        if (worldX >= bb.MinX && worldX <= bb.MaxX && worldY >= bb.MinY && worldY <= bb.MaxY)
            results.Add(new HitResult(HitKind.CanvasObject, obj.Id));
    }

    // 4. Wires — one entry per wire (endpoint → whole-wire; else the segment under the point).
    foreach (int i in candWires.OrderByDescending(x => x))
    {
        if (i >= editModel.Wires.Count) continue;
        var wire = editModel.Wires[i];
        var pts  = wire.Points;
        if (pts.Count == 0) continue;

        if (SchematicGeometry.CoincidentPoints(worldX, worldY, pts[0].X, pts[0].Y, EndpointHitTol))
        { results.Add(new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: 0)); continue; }

        int last = pts.Count - 1;
        if (SchematicGeometry.CoincidentPoints(worldX, worldY, pts[last].X, pts[last].Y, EndpointHitTol))
        { results.Add(new HitResult(HitKind.WireEndpoint, wire.Id, SubIndex: last)); continue; }

        for (int pi = 0; pi < pts.Count - 1; pi++)
            if (SchematicGeometry.PointOnSegment(
                    worldX, worldY, pts[pi].X, pts[pi].Y, pts[pi + 1].X, pts[pi + 1].Y, WireHitTol))
            { results.Add(new HitResult(HitKind.WireSegment, wire.Id, SubIndex: pi)); break; }
    }

    // 5. Dots.
    foreach (var dot in editModel.Dots)
        if (SchematicGeometry.CoincidentPoints(worldX, worldY, dot.X, dot.Y, hitRadius))
            results.Add(new HitResult(HitKind.Dot, dot.Id));

    // 6. Net labels.
    foreach (var lbl in editModel.NetLabels)
    {
        if (worldY < lbl.Y - NetLabelAboveBaseline || worldY > lbl.Y + NetLabelBelowBaseline) continue;
        double right = lbl.X + lbl.Name.Length * NetLabelCharWidth;
        if (worldX >= lbl.X - 8 && worldX <= right + 8)
            results.Add(new HitResult(HitKind.NetLabel, lbl.Id));
    }

    return results;
}
```

This ordering mirrors `Test` exactly: glyph → canvas object → wire endpoint/segment → dot → net label,
with components and canvas objects topmost-first. So `TestStack(...)[0]` always equals what
`Test(..., includeLabels:false)` would have returned — B9 stays intact for the first click.

## Change 2 — cyclic pick in `HandleSelectPress`

In `SchematicViewModel.HandleSelectPress`, replace the single hit-test line (the B9 version,
`var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy, includeLabels: false);`)
with a stack fetch + cyclic pick. **Everything below it stays byte-for-byte the same** — the existing
`WireSegment` branch, the `None` rubber-band branch, and the `else` object-select branch all consume
`hit` unchanged.

```csharp
// Cyclic click-through: build the top→bottom stack (labels excluded — B9) and pick the next
// object under the cursor when the current single selection is already in the stack; otherwise
// the topmost. Shift acts on the topmost only (no cycling).
var stack = SchematicHitTest.TestStack(EditModel, RenderModel, SpatialIndex, wx, wy, includeLabels: false);
var hit   = PickClickThrough(stack, shift);
```

Add two private helpers (place them right after `HandleSelectPress`):

```csharp
/// <summary>
/// Chooses the click target from the top→bottom stack. Non-shift: if exactly one object (or one
/// segment) is currently selected AND it is in the stack, return the next entry (cyclic); otherwise
/// the topmost. Shift: always the topmost (toggle semantics are applied by the caller). Empty stack
/// → a None hit (caller starts a rubber-band).
/// </summary>
private SchematicHitTest.HitResult PickClickThrough(
    IReadOnlyList<SchematicHitTest.HitResult> stack, bool shift)
{
    if (stack.Count == 0) return new SchematicHitTest.HitResult(SchematicHitTest.HitKind.None, "");
    if (shift)            return stack[0];

    int cur = CurrentSelectionIndexInStack(stack);
    return cur >= 0 ? stack[(cur + 1) % stack.Count] : stack[0];
}

/// <summary>
/// Index in the stack of the single currently-selected object/segment, or -1 if the selection is
/// empty, multiple, or not present in the stack. A whole-object selection matches any non-segment
/// stack entry with the same Id (Component / CanvasObject / Dot / NetLabel / whole-wire endpoint);
/// a segment selection matches the WireSegment entry with the same wire Id + segment index.
/// </summary>
private int CurrentSelectionIndexInStack(IReadOnlyList<SchematicHitTest.HitResult> stack)
{
    int objCount  = Selection.Ids.Count;
    var segs      = Selection.GetSelectedSegments(EditModel);
    if (objCount + segs.Count != 1) return -1;

    if (objCount == 1)
    {
        string id = Selection.Ids.First();
        for (int i = 0; i < stack.Count; i++)
        {
            if (stack[i].Kind == SchematicHitTest.HitKind.WireSegment) continue; // segment ≠ whole-object
            if (stack[i].Id == id) return i;
        }
    }
    else
    {
        var s = segs[0];
        for (int i = 0; i < stack.Count; i++)
            if (stack[i].Kind == SchematicHitTest.HitKind.WireSegment
                && stack[i].Id == s.WireId && stack[i].SubIndex == s.SegmentIndex)
                return i;
    }
    return -1;
}
```

## Why this cycles correctly

- The model doesn't change between clicks at the same point, so `TestStack` is deterministic — the
  stack is identical click-to-click, which is what makes the cycle stable.
- **Click 1** (nothing relevant selected): `cur = -1` → `stack[0]` (topmost). The existing branch
  selects it.
- **Click 2** (same point, `stack[0]` now selected): `cur = 0` → `stack[1]`.
- … **Click N** → `stack[N-1]`; the **next** click wraps `(N-1+1) % N = 0` back to the top.
- Moving to a different point yields a different stack; the old selection isn't found (`cur = -1`) →
  topmost — the cycle naturally resets.
- **Empty space**: `stack.Count == 0` → `None` → the existing branch clears selection (non-shift) and
  starts a rubber-band, exactly as today.
- **Shift-click**: `PickClickThrough` returns `stack[0]`; the existing branch's shift path toggles it
  in/out — unchanged add/remove on the topmost, no cycling.

## Interaction with segment selection / drag

When the cycle lands on a `WireSegment`, control enters the existing `WireSegment` branch — segment
selection + segment-drag setup — so the segment is selected and immediately draggable. When it lands on
a `Component` / `CanvasObject` / `WireEndpoint` (whole wire) / `Dot` / `NetLabel`, the existing `else`
branch selects it and snapshots drag-start positions. No new drag code.

A wire fully under a component (clicked mid-body) appears as a `WireSegment` entry beneath the
`Component` entry — so component → (click again) → that segment, which can then be moved or deleted.

## Tests (`tests/Ui.Tests`, headless — no Avalonia)

`TestStack` ordering/content:
- Two overlapping component glyphs → stack `[topComp, bottomComp]` (descending index first).
- A wire segment passing under a component glyph, click mid-glyph over the wire → stack
  `[Component, WireSegment(wireId, seg)]`.
- Two overlapping wires → two distinct entries, one per wire.
- `TestStack(...)[0]` equals `Test(..., includeLabels:false)` for the same point (first-click parity).

Cycle logic (drive `HandleSelectPress` via the VM, or factor `PickClickThrough`/
`CurrentSelectionIndexInStack` to be unit-testable): selecting topmost, then repeated same-point picks
advance one entry each and wrap after the last; a multi-selection or a different-point click resets to
topmost; shift always yields topmost.

## Acceptance

- Click a stack of overlapping objects repeatedly without moving the mouse → selection steps top → next
  → … → last → top, indefinitely.
- A wire hidden under a component is selectable by clicking the component once, then again.
- First click still selects the topmost (B9 glyph-only intact); shift-click add/remove unchanged;
  empty-space click clears + rubber-bands as before.
