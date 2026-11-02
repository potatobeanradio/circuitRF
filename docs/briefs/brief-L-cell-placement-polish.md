# Brief L — Cell-placement polish: ghost renders the real symbol + Symbol Editor 1-based ports

Two small, independent fixes surfaced while testing cell placement. Neither is functional-critical
(placement and connectivity are fixed elsewhere) — these are correctness/polish.

**Status: COMPLETED 2026-06-12**

**Firewall:** UI layer only; keep `SymbolPortDefs`/registry/model framework-free.

---

## Layer 1 — Drag ghost shows the resolved cell symbol (not a Generic box)

**Symptom:** dragging a cell from the Project Tree shows a ghost with **vertical** 2-terminal pins
regardless of the cell's actual symbol. **Cause:** `SchematicCanvas.OnCellDragOver` hard-codes
`new PlacementGhost(sx, sy, SymbolKind.Generic, rotation, false, 2)` — a `Generic` placeholder, whose
`SymbolPortDefs.For(Generic, 2)` falls to the default vertical `[("1",0,−200),("2",0,200)]`. The ghost
should preview the cell's real primary symbol so what the user sees dragging matches what lands.

**Read first:**
- `src/Ui/Controls/SchematicCanvas.cs` — `OnCellDragOver` (parses `CellDragPayload`, sets the ghost),
  `OnCellDrop`. `PlacementGhost` (the overlay ghost record) and how the renderer draws it
  (`SchematicOverlay.Ghost`, the overlay render path in `SchematicRenderer`).
- `src/Ui/Schematic/CellSymbolResolver.cs` — `Resolve(cellRef, baseDir)` → `Resolved` carries the
  `Symbol` (primitives + pins). `src/Ui/Schematic/EditableSchematic.cs` — `SchematicEditModel.SchematicDirectory`.
- `src/Ui/Schematic/CellDragPayload.cs` — payload carries the absolute cell folder path.

**Do:** in `OnCellDragOver`, when `SchematicDirectory != null`, compute
`cellRef = Path.GetRelativePath(SchematicDirectory, payload.CellAbsPath)` and
`CellSymbolResolver.Resolve(cellRef, SchematicDirectory)`. On `Resolved`, render the ghost from the
**resolved symbol's primitives + pins** (a faint/dashed preview at the snapped cursor), so it matches
the placed instance. This likely needs the ghost to optionally carry resolved symbol primitives/pins
rather than only a `SymbolKind` — extend `PlacementGhost` with an optional resolved-symbol payload
(mirror how `ToRenderComponent`/the renderer already draw cell-ref primitives + pins) rather than
inventing a second draw path. On `NotFound`/`PrimaryMissing` (or `SchematicDirectory == null`), fall
back to the current neutral box ghost — don't block the drag.

Keep it cheap: the resolver is cached; resolve on drag-over is fine. Don't do filesystem work every
mouse-move beyond the cached `Resolve` call.

**Gate 1:** Dragging a cell shows a ghost matching its primary symbol (correct pin arrangement);
a symbol-less / unresolved cell shows the neutral box; the drag never breaks.

---

## Layer 2 — Symbol Editor pin field shows 1-based "Port N"

**Symptom/decision:** the user-facing port number is **1-based** everywhere (schematic Pin `Num`,
§9 generated port numbers, S-param convention). The Symbol Editor currently exposes the pin's
**0-based internal `PortIndex`** ("Port Index 0, 1"), which mismatches the schematic's "Port 1, 2".
Make the Symbol Editor display/accept **1-based** ("Port 1", "Port 2"), converting to the 0-based
stored `PortIndex` at the boundary. Storage stays 0-based (`.csym` `SymbolPin.PortIndex` unchanged —
do NOT change the file format).

**Read first:**
- `src/Ui/ViewModels/SymbolPrimitiveInspectorViewModel.cs` — `PinPortIndex` (the bound field) and
  `RemapSymbolPinCommand` path; the pin inspector binding in
  `src/Ui/Views/.../SymbolPrimitiveInspectorView.axaml` (the "Port" field).
- `src/Ui/Schematic/SymbolModel.cs` — `SymbolPin.PortIndex` (0-based; **keep**).
- `docs/design/symbol-editor.md` §3 (pins map to ports) — note the display is 1-based, storage 0-based.

**Do:** convert at the inspector boundary only — the bound property exposes `PortIndex + 1` for
display and writes back `value − 1` to the 0-based `PortIndex` (clamp ≥ 0; reject < 1 input). Update
the field label to "Port" (not "Port Index"). Any other Symbol-Editor surface that shows the raw
0-based index to the user (e.g. the pin marker label / unmapped-port panel text) should show the
1-based number too — but the underlying mapping, `.csym`, and connectivity stay 0-based. Don't touch
the schematic side (already 1-based via Pin `Num`).

**Gate 2:** A symbol pin mapped to the first port shows "Port 1" in the editor; saving/reloading the
`.csym` round-trips the same pin (stored `PortIndex` still 0); the placed cell instance's pin
connectivity is unchanged.

---

## Acceptance
- Cell drag ghost previews the resolved primary symbol (neutral box only when unresolved). ✅
- Symbol Editor shows/accepts 1-based "Port N"; `.csym` storage and connectivity remain 0-based. ✅

## Guardrails
- Don't change the `.csym` format or `SymbolPin.PortIndex` semantics — 1-based is display-only.
- Ghost must reuse the existing cell-ref draw path, not a parallel one; fall back gracefully.
- Keep model/registry framework-free. Minimal diff; list files changed.

## Scope fence (NOT here)
- Drag/move connectivity for cell instances — **Brief K**.
- No schematic-side port-numbering changes (already 1-based).

## Exit / report
State: how the ghost carries/draws the resolved symbol and the fallback; the 1-based↔0-based
conversion point and every surface updated; and confirmation the `.csym` round-trips unchanged.

## Implementation notes

### Files changed
1. `src/Ui/Schematic/SchematicOverlay.cs` — `PlacementGhost` extended with `ResolvedPrimitives: IReadOnlyList<SymbolPrimitive>?` and `ResolvedPins: IReadOnlyList<SymbolPin>?` (both nullable, default null; all existing call sites unchanged).
2. `src/Ui/Controls/SchematicCanvas.cs` — `OnCellDragOver`: when `SchematicDirectory != null`, calls `CellSymbolResolver.Resolve` and on `Resolved` passes `sym.Primitives`/`sym.Pins` into the ghost; falls back to neutral 2-port `Generic` box on `NotFound`/`PrimaryMissing`/null directory/exception.
3. `src/Ui/Renderers/SchematicRenderer.cs` — ghost draw section: uses `ghost.ResolvedPrimitives ?? BuiltInSymbols.Primitives(ghost.Symbol).Primitives` for the body; iterates `ghost.ResolvedPins` (if non-null) for port markers, otherwise falls back to `SymbolPortDefs.For`.
4. `src/Ui/ViewModels/SymbolPrimitiveInspectorViewModel.cs` — `SetPinView`: `PinPortIndex = pin.PortIndex + 1`; `OnPinPortIndexChanged`: converts `newValue - 1` to 0-based before passing to `RemapSymbolPinCommand`; clamps `< 0` and no-change guards updated.
5. `src/Ui/Views/Properties/SymbolPrimitiveInspectorView.axaml` — PinPortIndex `NumericUpDown` `Minimum` changed from `0` to `1`.

### Renderer/editor surfaces already 1-based (no change needed)
- `SymbolEditorRenderer` line 246: pin marker label `$"P{pin.PortIndex + 1}"` — already 1-based.
- `DrawUnmappedPortPanel` line 312: `$"  Port {pi + 1} → open circuit"` — already 1-based.

### Round-trip verification
`.csym` storage is unchanged — `PortIndex` is 0-based throughout persistence. The conversion happens only in `SetPinView` (read: +1) and `OnPinPortIndexChanged` (write: −1), both at the inspector boundary.
