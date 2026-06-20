# circuitRF — Pin select: land the RIGHT fix, rip out the round-2 cruft (Claude Code / Sonnet)

The instrumentation did its job — round 1 found the TRUE root cause. But the round-1 fix is a coincidence-fix,
and round 2 bolted on an unwanted rubber-band pin-selection subsystem nobody asked for. **Revert the round-2
additions and replace the round-1 snap-hack with the correct minimal fix.** Firewall green.

## The confirmed root cause (correct — keep this understanding)
Pins are placed snapped to the connection grid P=100. A click that *places* a pin can be up to 50 world units
from the resulting grid point in each axis (≈70 diagonal). A later click near the visible pin is therefore up
to ~70 world units from the pin, but the pin tolerance was ~17. The pin was unreachable from its own placement
click. **The hit-test math and coordinate space are fine; the tolerance was just too small relative to the
placement snap.** (Pins and clicks are BOTH component-local — confirmed in `SymbolModel.cs`.)

## Why the current code is wrong (both rounds)
1. **Round-1 snap-hack** (`HitTestPin(SnapToConnectionGrid(lx), SnapToConnectionGrid(ly))`): only works because
   pins happen to sit exactly on P. It collapses the click to a grid cell and compares grid-point to
   grid-point — so the zoom-scaled tolerance is now meaningless (dist is 0 or ≥100), and it breaks for any pin
   not perfectly on P. Replace it.
2. **Round-2 rubber-band subsystem** (`_pinRbPendingPlace`, the `ActiveTool == Tool.Pin && _isRubberBanding`
   branches in `OnPointerMoved`/`OnPointerReleased`, `UpdatePinRubberBandSelection`, deferred place-on-release):
   unrequested complexity. The required behavior is dead simple — **click a pin → select+drag it; click empty →
   place a pin.** Remove all of it.

## The correct behavior (what to implement)
Pin tool:
- **Press on an existing pin** → select it (`_selectedPinIndex`), begin drag (`_isPinDragging`). (As before.)
- **Press on empty space** → place a new pin at the P-snapped point, and make it the selected pin, ready to
  drag. (Place on PRESS — no deferral, no rubber-band.)
- **Move while dragging** → live P-snapped ghost. **Release** → commit `MoveSymbolPinCommand`. (As before.)
No rubber-band, no marquee, no place-on-release for the Pin tool.

## The fix
1. **`PinToolPress` — correct hit-test, no click-snapping:**
   ```
   private void PinToolPress(double lx, double ly)
   {
       int hit = HitTestPin(lx, ly);          // RAW click coords — do NOT snap the click
       if (hit >= 0)
       {
           _selectedPinIndex = hit;
           _isPinDragging    = true;
           _pinOrigX = EditableSymbol.Pins[hit].LocalX;
           _pinOrigY = EditableSymbol.Pins[hit].LocalY;
           _pinLiveDx = 0; _pinLiveDy = 0;
           SyncSelectedPinPortIndex();
       }
       else
       {
           double px = SnapToConnectionGrid(lx), py = SnapToConnectionGrid(ly);
           var pin = new SymbolPin(px, py, NextUnmappedPortIndex());
           Execute(new PlaceSymbolPinCommand(EditableSymbol, pin));
           _selectedPinIndex = EditableSymbol.Pins.IndexOf(pin);
           _isPinDragging = false;
           SyncSelectedPinPortIndex();
       }
       RebuildOverlay();
   }
   ```
2. **`HitTestPin` — tolerance that always covers the placement snap.** A click that placed a pin is up to ~70
   world units away, so the pick radius must be at least half a grid cell. Use the LARGER of the zoom-pixel
   radius and half the pin grid:
   ```
   double tol = Math.Max(12.0 / Math.Max(CanvasZoom, 1e-6), PinGrid * 0.5);  // ≥ 50 world units
   ```
   Keep the nearest-pin scan; return the nearest pin within `tol`. (With pins 100 apart, a 50-unit radius is
   unambiguous: at most one pin's half-cell contains any click. When zoomed in, the zoom term may exceed 50 —
   that's fine and still unambiguous because pins are 100 apart on screen too.) This makes a click anywhere in
   a pin's grid cell select that pin — which is exactly the gesture that placed it.
3. **Remove the round-2 cruft:**
   - Delete the `_pinRbPendingPlace` field.
   - Delete the `if (ActiveTool == Tool.Pin && _isRubberBanding)` branches in BOTH `OnPointerMoved` and
     `OnPointerReleased`.
   - Delete `UpdatePinRubberBandSelection`.
   - Remove `_pinRbPendingPlace` from `CancelOp`.
   - Revert the `// Rubber-band state (shared by Select and Pin tools)` comment back to Select-only; the Pin
     tool no longer touches `_isRubberBanding`.

## Verify (manual, the exact repro)
- New Symbol → Pin tool → click empty space: a pin appears AND is selected (highlighted), with Port/X/Y in the
  inspector. 
- Click that pin again: stays/with-becomes selected and a drag moves it (live, P-snapped, undoable). 
- Place a second pin elsewhere; click each — the correct one selects (clicks land in the right grid cell). 
- Click far from any pin (>half a cell at current zoom) → places a new pin (or, if `PortCount` exhausted,
  behaves as today). 
- Zoom out and in: clicking a pin still selects it (tol = max(zoom-px, 50)). 
- Undo: pin move and pin placement each undo cleanly.

## After it works
- Finish the L4 inspector half if not already: pin **PortIndex / X / Y editable** (PortIndex →
  `RemapSymbolPinCommand`; X/Y → `MoveSymbolPinCommand`, P-snapped), live + undoable.
- Resume the remaining round-2 layers (editable primitive fields per type, polyline coords, outline highlight,
  rotate-about-bottom-left, integer port box, no toolbar scrollbars).

## Robust under any P
Use `PinGrid * 0.5` for the half-cell floor — NEVER a literal `50`. `PinGrid` is the single named constant
read by both `SnapToConnectionGrid` and `HitTestPin`, so if P changes, the floor scales with it. The invariant
holds for any grid size: pins are always ≥ `PinGrid` apart (they snap to the grid), and a `PinGrid/2` pick
radius means half-cells never overlap — exactly one pin per click cell at any P.

## Guardrails
- **Do NOT snap the click before hit-testing** — hit-test raw click coords; fix the TOLERANCE instead
  (`max(zoom-px, half-grid)`). The snap-hack is wrong even though it appears to work.
- **No rubber-band / marquee / place-on-release for the Pin tool** — click selects+drags, empty-click places.
  Rip out the round-2 additions entirely.
- Pins stay on P; every pin op undoable; the zoom-tolerance term stays (now combined with the half-grid floor).
- Keep the round-2 zoom fix for primitives/gripper (`HitTestTopmost`, `HitTestGripper`) — that was a separate,
  real fix.
- `dotnet build`/`dotnet test` green; firewall green.
- Update `docs/design/symbol-editor.md`: pin pick tolerance = max(zoom-px radius, half connection-grid);
  Pin-tool gesture model (click=select+drag / empty=place); explicitly no pin rubber-band.

*Exit: Pin tool — click a pin to select+drag it, click empty to place one; selection works because the pick
radius covers a full grid cell (the gesture that placed the pin). The round-2 rubber-band code is gone; the
snap-hack is replaced by a correct tolerance.*
