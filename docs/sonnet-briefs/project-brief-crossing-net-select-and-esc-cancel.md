---
name: project-brief-crossing-net-select-and-esc-cancel
description: Brief crossing-net-select-and-esc-cancel — crossing rubber-band selects whole net + Esc cancels net-label edit — completed 2026-06-14
metadata:
  type: project
---

Two schematic editor fixes landed 2026-06-14.

**Change 1 — R→L crossing rubber-band selects the whole net's wires:**
- `NetExtractor.ConnectedWireIds` public method added (after `FindNodeLabel`) — builds a union-find from `AddGeometricUnions` and expands seed wire ids to every wire on the same electrical node.
- `SchematicHitTest.ExpandCrossing` — before `return result;`, seeds from all `HitKind.Wire` results and calls `ConnectedWireIds` to pull in the full net. Crossing-only path; Window mode untouched.

**Change 2 — Esc fully cancels a net-label edit (label must NOT move):**
- Root cause: Window-level `DisarmPlacementCommand` KeyBinding marked Escape Handled before `OnInlineEditKeyDown` (bubble) could fire, leaving the box open. The deferred `LostFocus → CommitInlineEdit` then moved the label.
- Fix: `OnViewKeyDownTunnel` (registered `handledEventsToo:true`) now intercepts Escape when `InlineEditBox.IsKeyboardFocusWithin` and calls `CancelInlineEdit() + DismissInlineEditBox() + SetSelectTool()`. Other keys fall through. `OnInlineEditKeyDown`'s Escape branch kept as harmless belt-and-suspenders.

**Why:** The tunnel handler is the established interception point for "Window KeyBinding ate the key" problems (see `src/Ui/CLAUDE.md`). Both changes are pure behavior — no model mutation, no undo entry, no persistence.

Gate: 1188 tests green; build clean.
