# Brief: text-primitive — Fix 3 follow-up (absolute anchor snap on drag)

The render half of Fix 3 landed correctly: `SchematicRenderer.DrawSymbol` now draws a text's
(Align, VAlign) corner exactly at its `AnchorX/AnchorY` using real font metrics. But the reported symptom
remains because the **drag snap** is the actual cause: in `SymbolEditorViewModel`, dragging a text snaps
the *delta* (`SnapToP(lx - _dragStartLocalX)`), not the absolute anchor. So the corner only lands on a grid
line if the anchor already happened to be on one — and a prior rotate/resize leaves the anchor at
`center ± (W/2, H/2)`, which is off-grid. The text then moves in grid-sized steps **relative to its current
(off-grid) position**, never landing on grid coordinates.

**Fix:** when a single text is being dragged, snap its absolute anchor to the grid instead of the delta.

Size: **XS**. File: `src/Ui/ViewModels/SymbolEditorViewModel.cs`.

## 1. Add a helper (near the snap helpers `SnapToP` / `SnapToConnectionGrid`)

```csharp
    /// <summary>If exactly one primitive is selected and it is a TextPrimitive, returns its current
    /// anchor (the (Align,VAlign) point); otherwise null. Used to snap text drags to ABSOLUTE grid
    /// coordinates rather than to the drag delta.</summary>
    private (double X, double Y)? SingleSelectedTextAnchor()
    {
        if (_selection.Count != 1) return null;
        int idx = _selection.First();
        if (idx < 0 || idx >= EditableSymbol.Primitives.Count) return null;
        return EditableSymbol.Primitives[idx] is TextPrimitive t ? (t.AnchorX, t.AnchorY) : null;
    }
```
(During a live drag the committed primitive still holds the pre-drag anchor — the move is only applied on
release — so this returns the drag's start anchor, which is the correct reference to snap against.)

## 2. Use absolute-anchor snap in the drag branch

In `OnPointerMoved`, inside the `if (ActiveTool == Tool.Select)` → `if (_isDragging)` block, replace the
non-pin `else`:
```csharp
                else
                {
                    _liveDx = SnapToP(lx - _dragStartLocalX);
                    _liveDy = SnapToP(ly - _dragStartLocalY);
                }
```
with:
```csharp
                else if (SingleSelectedTextAnchor() is { } ta)
                {
                    // Text: snap the (Align,VAlign) ANCHOR to absolute grid coordinates so the corner lands
                    // on grid intersections — not in grid-sized steps relative to an off-grid start (which
                    // is what a rotate/resize leaves the anchor as).
                    _liveDx = SnapToP(ta.X + (lx - _dragStartLocalX)) - ta.X;
                    _liveDy = SnapToP(ta.Y + (ly - _dragStartLocalY)) - ta.Y;
                }
                else
                {
                    _liveDx = SnapToP(lx - _dragStartLocalX);
                    _liveDy = SnapToP(ly - _dragStartLocalY);
                }
```

The result is fed through the existing live-drag overlay and the `MoveSymbolPrimitivesCommand` on release,
so `newAnchor = startAnchor + _liveDx = SnapToP(startAnchor + rawDelta)` lands exactly on a grid point. In
`SnapMode.None`, `SnapToP` is identity, so free dragging is unchanged. Multi-selection and non-text drags
keep the existing delta-snap (group cohesion).

## Verification

1. Place a text, rotate it once (R), then drag it → the (Align,VAlign) corner now lands on grid
   intersections (absolute), not in steps offset from the rotated position.
2. With Align=Left, VAlign=Bottom, drag the text → the bottom-left corner snaps onto grid points.
3. Switch snap mode (G) between Fine (p=5) / Connection (P=100) / Off and drag → the corner lands on the
   active grid; Off = free.
4. Drag a shape, or a multi-selection containing a text → unchanged (delta snap).

## Acceptance

- A single text drag snaps its (Align,VAlign) anchor to absolute grid coordinates, regardless of whether
  the anchor was previously on-grid.
- No change to shapes, pins, multi-selection, or the (already-correct) text render.

## Optional follow-up (not required)

Arrow-key nudging of a text still adds a fixed ±5/±100 delta, so it preserves any existing off-grid offset.
If you want nudge to also grid-snap the anchor, the same `SingleSelectedTextAnchor()` pattern can be applied
in the arrow-key branch of `OnKeyDown` — say the word and I'll spec it.
