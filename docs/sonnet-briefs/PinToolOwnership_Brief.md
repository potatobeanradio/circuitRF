# circuitRF — Pin tool = place only; Select tool owns pin select/move (Option A) (Claude Code / Sonnet)

Decision (user): **Option A.** The "Pin" tool should ONLY instantiate pins. Selecting, moving, and deleting a
pin belong to the **Select** tool — same as primitives. Today the Pin tool double-duties (place + select +
drag) and the Select tool can't touch pins at all (it only hit-tests `Primitives`, never `Pins`). This brief
moves pin manipulation to Select and strips it from Pin. It also folds in the correct pin-drag delta math
(the absolute-snap bug from `PinDragFix_Brief.md` is NOT yet landed — write it correctly here). Firewall green;
every mutation undoable; pins stay on the connection grid P.

## Target behavior
- **Pin tool:** click → place a new pin at the P-snapped point (becomes selected so the inspector shows it).
  Clicking an existing pin with the Pin tool does NOT drag it — at most it re-selects it; no move. No
  rubber-band. Pin tool is purely "make pins."
- **Select tool:** click a pin → select it (show in inspector). Drag a pin → move it, P-snapped, live,
  undoable. Delete key → delete the selected pin. Pins participate in Select alongside primitives.
- A pin and primitives are both selectable with Select; for v1 keep it simple — selecting a pin is its own
  selection (clears primitive selection and vice-versa); no mixed multi-select of pin+primitive required.

## Read first (all in `SymbolEditorViewModel.cs`, current on disk)
- `SelectToolPress` — currently: gripper check → `HitTestTopmost` (primitives only) → drag/rubber-band. Add a
  pin hit-test BEFORE the primitive hit-test.
- `OnPointerMoved` Select branch — has `_isResizing` / `_isDragging` (primitive) / `_isRubberBanding`. Add a
  pin-drag sub-state.
- `OnPointerReleased` Select branch — commits primitive move. Add pin-move commit.
- `PinToolPress` — currently places OR selects+drags. Strip the select+drag; keep place-only (+select).
- Pin state fields: `_selectedPinIndex`, `_isPinDragging`, `_pinOrigX/Y`, `_pinLiveDx/Dy`. The Pin-drag
  branches in `OnPointerMoved`/`OnPointerReleased` move from the Pin path to the Select path.
- `HitTestPin` (tol = max(12/zoom, PinGrid*0.5)) — REUSE as-is for the Select tool's pin hit-test.
- `OnKeyDown` Pin-tool Delete/rotate handlers — pin Delete + R should now work under the Select tool too
  (pin selected) — move/extend accordingly.
- `RebuildOverlay` — `SelectedPinIndex`/`PinLiveDragOffset` already flow to the overlay; the renderer's
  selection ring (from the feedback brief) already shows the selected pin. Just make sure the Select tool sets
  `_selectedPinIndex` so the ring + inspector light up.

## LAYER 1 — Select tool selects + drags a pin (with correct delta math)
1. **`SelectToolPress`** — after the gripper check, BEFORE `HitTestTopmost`, hit-test pins:
   ```
   if (!IsLocked)
   {
       int pinHit = HitTestPin(lx, ly);
       if (pinHit >= 0)
       {
           _selection.Clear();                 // pin selection is its own thing (v1)
           _selectedPinIndex = pinHit;
           _isPinDragging    = true;
           _pinOrigX = EditableSymbol.Pins[pinHit].LocalX;
           _pinOrigY = EditableSymbol.Pins[pinHit].LocalY;
           _pinGrabX = lx;  _pinGrabY = ly;    // NEW grab fields (see §2)
           _pinLiveDx = 0;  _pinLiveDy = 0;
           SyncSelectedPinPortIndex();
           RebuildOverlay();
           return;
       }
   }
   ```
   When a primitive (or empty) is hit instead, CLEAR `_selectedPinIndex` (so clicking a primitive deselects
   the pin) before the existing primitive/rubber-band logic.
2. **Add grab fields** `private double _pinGrabX, _pinGrabY;` and use **delta-from-grab** math (this is the
   correct fix the prior brief specified — NOT absolute snap). In `OnPointerMoved`, in the **Select** branch,
   add a pin-drag sub-case (before/beside the primitive `_isDragging` case):
   ```
   if (_isPinDragging)
   {
       double destX = SnapToConnectionGrid(_pinOrigX + (lx - _pinGrabX));
       double destY = SnapToConnectionGrid(_pinOrigY + (ly - _pinGrabY));
       _pinLiveDx = destX - _pinOrigX;
       _pinLiveDy = destY - _pinOrigY;
       RebuildOverlay();
       return;
   }
   ```
3. **`OnPointerReleased`** Select branch — add the pin-move commit (move the existing logic here from the Pin
   branch):
   ```
   if (_isPinDragging)
   {
       _isPinDragging = false;
       if ((_pinLiveDx != 0 || _pinLiveDy != 0) && _selectedPinIndex.HasValue && !IsLocked)
       {
           var pin = EditableSymbol.Pins[_selectedPinIndex.Value];
           Execute(new MoveSymbolPinCommand(EditableSymbol, pin,
                                            _pinOrigX + _pinLiveDx, _pinOrigY + _pinLiveDy));
           _selectedPinIndex = EditableSymbol.Pins.IndexOf(pin);
       }
       _pinLiveDx = 0; _pinLiveDy = 0;
       RebuildOverlay();
       return;
   }
   ```
4. Reset `_pinGrabX/_pinGrabY` (and `_isPinDragging`) in `CancelOp`.

**Layer 1 gate:** With the **Select** tool: click a pin → it shows the selection ring + the inspector Pin
panel; drag it → moves in clean P-steps following the cursor, live; release → commits; Undo restores. Clicking
a primitive or empty clears the pin selection. Report.

## LAYER 2 — Pin tool: place only (strip select+drag)
1. **`PinToolPress`** — remove the select-and-drag branch. New behavior:
   ```
   private void PinToolPress(double lx, double ly)
   {
       int hit = HitTestPin(lx, ly);
       if (hit >= 0)
       {
           // Existing pin: just select it (so the inspector shows it). No drag under the Pin tool.
           _selectedPinIndex = hit;
           _isPinDragging    = false;
           SyncSelectedPinPortIndex();
       }
       else
       {
           double px = SnapToConnectionGrid(lx), py = SnapToConnectionGrid(ly);
           var pin = new SymbolPin(px, py, NextUnmappedPortIndex());
           Execute(new PlaceSymbolPinCommand(EditableSymbol, pin));
           _selectedPinIndex = EditableSymbol.Pins.IndexOf(pin);
           _isPinDragging    = false;
           SyncSelectedPinPortIndex();
       }
       RebuildOverlay();
   }
   ```
2. **`OnPointerMoved`/`OnPointerReleased`** — DELETE the `ActiveTool == Tool.Pin && _isPinDragging` branches
   (that logic now lives in the Select path). The Pin tool no longer drags.

**Layer 2 gate:** With the **Pin** tool: click empty → places a pin (selected); click an existing pin →
selects it but a drag does NOT move it (no movement, no commit). Report.

## LAYER 3 — Delete / rotate a pin under the Select tool
Pins are now manipulated under Select, so pin Delete and pin Rotate (R) must work when a pin is selected with
the Select tool:
1. **`OnKeyDown`** — make the pin Delete and pin-R handlers fire when `_selectedPinIndex` is set, regardless of
   whether the tool is Pin or Select. (Currently they're gated under `ActiveTool == Tool.Pin`.) Simplest:
   check `_selectedPinIndex.HasValue` for pin Delete/rotate before the tool-specific branches; keep primitive
   Delete/rotate for `_selection`.
2. Pin rotate stays P-snapped about its position (existing math); Delete uses `DeleteSymbolPinCommand`.

**Layer 3 gate:** Select a pin (Select tool) → Delete removes it (undoable); R rotates it (P-snapped,
undoable). Primitive Delete/rotate still work. Report.

## Acceptance
1. Pin tool ONLY places pins (click empty = place; click pin = select; no drag/move).
2. Select tool selects, drags (clean P-steps via delta-from-grab), deletes, and rotates pins — and shows them
   in the inspector with the selection ring.
3. Pins stay on P; all ops undoable; the pin-drag uses origin+delta snapped to destination (no absolute-cursor
   snap, no zero-movement-within-cell bug).
4. `dotnet build`/`dotnet test` green; firewall green; no regression to primitive select/drag/resize/rubber-band.

## Guardrails
- **Tool ownership:** Pin = create only; Select = manipulate (select/move/delete/rotate). Don't leave pin-drag
  in the Pin path.
- **Delta-from-grab, snap the destination** for pin drag (the correct fix); never snap the absolute cursor.
- Pins always on connection grid P; one undoable command per op (Move/Delete/Remap/Rotate).
- Reuse `HitTestPin` as-is; reuse the existing selection-ring + inspector (feedback brief) — just drive
  `_selectedPinIndex` from the Select path.
- v1: pin selection is separate from primitive selection (no mixed multi-select).
- Update `docs/design/symbol-editor.md`: Pin tool places only; Select tool owns pin select/move/delete/rotate;
  pin drag = delta-from-grab snapped to P.

*Exit: the Pin tool makes pins and nothing else; the Select tool selects/moves/deletes/rotates pins just like
primitives, with pin drag following the cursor in clean P-steps. The tool model finally matches the user's
mental model.*
