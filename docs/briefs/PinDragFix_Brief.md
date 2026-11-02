# circuitRF — Pin DRAG is broken (separate from select); the snap kills sub-cell motion (Claude Code / Sonnet)

You were right to push back: this is NOT a feedback issue. **Pin SELECT works (the transcript proved HIT →
selIdx=0). Pin DRAG is a separate, real bug** — traced below. The drag branch snaps the ABSOLUTE cursor
position to the P=100 grid, so any drag that stays within the pin's 100-unit cell produces ZERO movement, and
a short drag commits nothing on release. The pin only jumps in 100-unit steps after the cursor crosses a half
cell. That reads as "I can't drag the pin." Fix the delta math. Do NOT touch select/hit-test. Firewall green.

> **Note:** the prior feedback brief (`PinSelectFeedbackFix_Brief.md`) is already applied and the `[PIN]`
> instrumentation is removed — verified on disk. The pin-DRAG path below is untouched by that work and is still
> the absolute-snap bug. Do NOT re-add instrumentation; do NOT revisit select/feedback.

## The bug (exact mechanism, from the code + the earlier transcript)
Press selects the pin and sets `_pinOrigX/_pinOrigY` to the pin's grid position (e.g. (-200,-200)) and
`_isPinDragging=true`. Then in `OnPointerMoved` (Pin-drag branch):
```
double nx = SnapToConnectionGrid(lx);   // snaps ABSOLUTE cursor to nearest 100
double ny = SnapToConnectionGrid(ly);
_pinLiveDx = nx - _pinOrigX;            // 0 while cursor stays in the same 100-cell
_pinLiveDy = ny - _pinOrigY;
```
While the cursor is anywhere inside the pin's own 100×100 cell, `nx==_pinOrigX` → `_pinLiveDx==0`. At zoom
0.705 a 100-unit cell is ~70 screen px, so a normal drag moves the pin **not at all** until you cross ~35px
into the next cell, then it jumps a full 100. On release, `if ((_pinLiveDx != 0 || _pinLiveDy != 0) …)` is
usually false → **no `MoveSymbolPinCommand` committed**. Net effect: the pin appears undraggable.

Contrast the PRIMITIVE drag, which is correct — it tracks a DELTA from the press point, then snaps the delta:
`_liveDx = SnapToP(lx - _dragStartLocalX);`

## The fix — track delta from the press point, snap the destination (not the absolute cursor)
1. **Record the press cursor position.** In `PinToolPress`, when a pin is hit, store the press point alongside
   the pin origin:
   ```
   _selectedPinIndex = hit;
   _isPinDragging    = true;
   _pinOrigX = EditableSymbol.Pins[hit].LocalX;
   _pinOrigY = EditableSymbol.Pins[hit].LocalY;
   _pinGrabX = lx;  _pinGrabY = ly;     // NEW fields: raw cursor at grab
   _pinLiveDx = 0;  _pinLiveDy = 0;
   ```
   Add `private double _pinGrabX, _pinGrabY;` to the Pin-tool state fields.
2. **Compute the destination from the delta, then snap the DESTINATION to P.** In `OnPointerMoved` Pin-drag
   branch, replace the absolute-snap with:
   ```
   double destX = SnapToConnectionGrid(_pinOrigX + (lx - _pinGrabX));
   double destY = SnapToConnectionGrid(_pinOrigY + (ly - _pinGrabY));
   _pinLiveDx = destX - _pinOrigX;
   _pinLiveDy = destY - _pinOrigY;
   RebuildOverlay();
   ```
   Now the destination is "origin + how far the cursor moved," snapped to the nearest grid point — so the pin
   follows in clean 100-unit steps as you drag, and a drag of half a cell (≈35px at this zoom) moves it one
   cell, exactly like a grid-snapped drag should. (This matches how the primitive drag snaps the delta.)
3. **Release already works once the delta is non-zero** — `OnPointerReleased`'s Pin branch commits
   `MoveSymbolPinCommand(pin, _pinOrigX+_pinLiveDx, _pinOrigY+_pinLiveDy)`. No change needed there beyond the
   correct `_pinLiveDx/Dy`.
4. **Reset the grab fields** in `CancelOp` (set `_pinGrabX=_pinGrabY=0` for tidiness; not strictly required).

## Why this is the right fix (not the absolute snap)
- Pins must land on P → snap the DESTINATION, not the raw cursor. Snapping the absolute cursor discards the
  grab offset, so motion within a cell is lost. Snapping `origin + delta` preserves the gesture and still
  lands on P.
- It mirrors the working primitive drag (`SnapToP(lx - dragStart)`), so the editor is consistent.

## Verify (manual)
- Pin tool → place a pin → press on it and drag slowly: the pin should follow the cursor in 100-unit steps
  (one cell per ~half-cell of cursor travel), live. Release → it stays at the new grid point; Undo restores.
- A small drag that crosses a half-cell moves the pin one cell (not zero). A drag back returns it.
- Dragging stays on P at every step.

## After it works
- Keep the selection-ring + editable-pin-inspector work from the prior brief (`PinSelectFeedbackFix_Brief.md`)
  — that's still wanted (visible selection + editable Port/X/Y), just not the cause of "can't drag."
- Remove the `[PIN]` instrumentation once drag + select + feedback all confirmed.
- Resume the remaining round-2 layers.

## Guardrails
- **Do NOT touch `HitTestPin` / select / place logic** — select works; this is purely the drag delta math.
- Snap the DESTINATION (`origin + delta`), never the absolute cursor; pins stay on P; the move stays one
  undoable `MoveSymbolPinCommand`.
- Mirror the primitive-drag delta pattern for consistency.
- `dotnet build`/`dotnet test` green; firewall green.
- Update `docs/design/symbol-editor.md`: pin drag tracks delta-from-grab and snaps the destination to P (not
  the absolute cursor).

*Exit: pressing on a pin and dragging moves it in clean P-steps following the cursor, commits on release, and
undoes — because the drag now tracks origin+delta snapped to P, instead of snapping the absolute cursor and
losing all sub-cell motion.*
