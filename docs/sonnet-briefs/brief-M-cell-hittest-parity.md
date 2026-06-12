# Brief M — Wire-snap & selection see a cell's real pins (NearestPort / ExpandCrossing / glyph bbox) ✅ COMPLETED 2026-06-12

**Context:** Brief K fixed cell-instance **drag/move** connectivity by routing the drag/connectivity
helpers through the cell-ref-aware `SchematicEditModel.PortDefsOf`. The scope-fence audit found the
**hit-test** path (`SchematicHitTest`) still has the same two-sources-of-truth bug: it reads a
component's pins via `comp.GetPortWorldCoord(pi)` over `0..comp.PortCount-1`, which for a cell returns
the `SymbolKind.Generic` placeholder pins `(0,±200)`, not the cell's real `.csym` pins. Effects:
drawing a wire toward a cell snaps to the wrong (placeholder) positions, and crossing-selection
expansion misses the cell's real port→wire connections. This finishes "cell instances behave exactly
like components."

**Firewall:** all changes in `src/Ui/Schematic` (framework-free; no Avalonia/Skia).

---

## Read first (real names)

- `src/Ui/Schematic/SchematicEditModel` (in `EditableSchematic.cs`):
  - **`PortDefsOf(EditableComponent)`** → `IReadOnlyList<(float LocalX, float LocalY, int PortIndex)>`
    — the cell-ref-aware accessor added in Brief K (Resolved → resolved `.csym` pins;
    NotFound/PrimaryMissing → empty; built-in → `SymbolPortDefs.For`, PortIndex = slot). **This is
    the single source; route the hit-test through it too.**
  - `PortWorldOf(comp, def)` → applies `SchematicGeometry.LocalToWorld`.
- `src/Ui/Schematic/SchematicHitTest.cs` — the three bug sites:
  1. **`NearestPort(editModel, worldX, worldY, tolerance)`** — loops `for (pi=0; pi<comp.PortCount; pi++)`
     calling `comp.GetPortWorldCoord(pi)`. Returns `(bool Found, string CompId, int PortIdx, double X,
     double Y)`. **Caller check below.**
  2. **`ExpandCrossing`** — loops `comp.PortCount` with `comp.GetPortWorldCoord(pi)` for the dragged
     component AND `other.PortCount` / `other.GetPortWorldCoord(opi)` for neighbor components.
  3. **`GetCompGlyphBb(comp)` → `comp.ComputeGlyphBb()`** — uses built-in primitives, so a cell's
     glyph hitbox is the Generic box, not the resolved symbol's bounds (Layer 3).
- `EditableComponent.GetPortWorldCoord(int)` / `.ComputeGlyphBb(overridePrimitives?)` /
  `.PortCount` / `.CellRef` — the built-in-only accessors being bypassed for cells.
- **Caller audit (already done — confirm):** the wire tool's `SchematicViewModel.HandleWirePress`
  consumes `NearestPort` as `var (pFound, _, _, px, py) = NearestPort(...)` — it **discards `CompId`
  and `PortIdx`** and uses only the snapped coordinate. So `NearestPort`'s returned index is **not
  load-bearing** today; the fix only needs the correct pin *world position*. (Grep for other
  `NearestPort(` callers to confirm none rely on `PortIdx`/`CompId`; if one does, keep the index
  meaning = the `PortDefsOf` slot, consistent with Brief K.)

---

## Spine (do-not-violate)

1. **One source of pin truth.** The hit-test reads pins via `PortDefsOf` (+ `PortWorldOf`), exactly
   like the render and drag paths. No `GetPortWorldCoord`/`PortCount`-loop pin reads for a component
   that might be a cell.
2. **Match what the user sees.** Resolved cell → snap/expand to the resolved symbol's pins;
   NotFound/PrimaryMissing → no pins (consistent with the no-pins render), so nothing snaps to a
   phantom pin.
3. **Built-ins unchanged.** Zero behavior change for non-cell components.
4. Honor the perf rule: hit-test is interactive. `PortDefsOf` uses the cached resolver (one stat per
   call); the hit-test paths here are per-click / per-rubber-band-release, not per-frame, so direct
   `PortDefsOf` calls are fine. Don't add per-mouse-move IO in the wire-draw hover path beyond the
   existing cached `Resolve`.

---

## Layer 1 — `NearestPort` uses resolved pins

Replace the `for (pi=0; pi<comp.PortCount; pi++) { comp.GetPortWorldCoord(pi) }` loop with iteration
over `editModel.PortDefsOf(comp)`, computing each pin's world position via `PortWorldOf(comp, def)`.
Skip detached pins (`comp.IsPortDetached(def.PortIndex)`) to match the connectable set. Return the
nearest pin's world coords; set `PortIdx` to the **slot** in the `PortDefsOf` list (consistent with
Brief K's convention — harmless since callers discard it, but keep it meaningful).

**Gate 1:** Drawing a wire toward a placed cell snaps the endpoint to the cell's **actual** pin
positions (matching the rendered red-square pins); a built-in component snaps exactly as before; a
cell with an unresolved symbol offers no port snap (falls through to wire-endpoint/body snap).

---

## Layer 2 — `ExpandCrossing` uses resolved pins

In `ExpandCrossing`, replace both `comp.PortCount`/`GetPortWorldCoord` loops (the selected component's
ports and the neighbor `other`'s ports) with `PortDefsOf(comp)` / `PortDefsOf(other)` + `PortWorldOf`.
Detached-pin skip as in Layer 1.

**Gate 2:** A crossing (right-to-left) rubber-band that touches a cell instance also pulls in the
wires connected to the cell's real pins, and the components on the far end of those wires — identical
to how it works for built-ins.

---

## Layer 3 — Cell glyph hitbox uses resolved bounds

`GetCompGlyphBb(comp)` calls `comp.ComputeGlyphBb()` with no override, so a cell's clickable glyph box
is the Generic placeholder's bounds, not its real symbol. `ComputeGlyphBb` already accepts
`overridePrimitives` (used by `ToRenderComponent` for the Resolved cell path). Make `GetCompGlyphBb`
cell-ref-aware: when `comp.CellRef` resolves, pass the resolved symbol's primitives to
`ComputeGlyphBb`; otherwise the built-in path. (Add a small `SchematicEditModel` helper to fetch a
component's effective primitives — resolved vs built-in — mirroring `PortDefsOf`, so glyph-bb and
render agree; or reuse the resolution already available.)

**Gate 3:** Clicking on the body of a placed cell whose symbol differs from the Generic box (e.g. a
wide N-port block) selects it across the symbol's true extent; rubber-band window/crossing fit tests
use the true glyph bounds. Built-in glyph hitboxes are unchanged.

---

## Acceptance
- Wire-draw snapping, crossing-selection expansion, and glyph hit-testing all use a cell's resolved
  `.csym` pins/bounds — cells are indistinguishable from built-ins for these interactions. ✅
- No site in `SchematicHitTest` reads pins via `GetPortWorldCoord`/`PortCount` for a possibly-cell
  component; all go through `PortDefsOf`/the resolved-primitive helper. ✅
- Built-in behavior unchanged; no new per-frame IO. ✅

## Guardrails
- Reuse Brief K's `PortDefsOf` (and an analogous resolved-primitives helper for Layer 3) — do not add
  a third pin/bounds source.
- Keep `SchematicHitTest`/`SchematicEditModel` framework-free.
- Confirm no `NearestPort` caller depends on the returned `PortIdx`/`CompId`; if one does, preserve
  the slot-index meaning.
- Minimal diff; list sites changed.

## Scope fence (NOT here)
- `NudgeSelection` wire-follow (arrow-key move drops follow-wires for ALL component types — a
  pre-existing, component-agnostic gap; tracked separately, not in this brief).
- No new placement/drag behavior; this is hit-test/selection parity only.

## Exit / report — completed 2026-06-12

**Sites changed:**

| File | Site | Change |
|------|------|--------|
| `EditableSchematic.cs` | new `SchematicEditModel.EffectivePrimitivesOf` | Added after `PortWorldOf`; mirrors `PortDefsOf` for primitives |
| `SchematicHitTest.cs` | `NearestPort` | `PortCount`/`GetPortWorldCoord` loop → `PortDefsOf` + `PortWorldOf` + detached skip; `PortIdx` = slot |
| `SchematicHitTest.cs` | `ExpandCrossing` (comp ports) | Same replacement for the selected-component port loop |
| `SchematicHitTest.cs` | `ExpandCrossing` (neighbor ports) | Same replacement for the `other` component port loop |
| `SchematicHitTest.cs` | `GetCompGlyphBb` | Added `SchematicEditModel` param; cell-ref-aware (Resolved → `ComputeGlyphBb(prims)`; NotFound/PrimaryMissing → placeholder; built-in → `ComputeGlyphBb()`) |
| `SchematicHitTest.cs` | `Test` call site | `GetCompGlyphBb(comp)` → `GetCompGlyphBb(comp, editModel)` |
| `SchematicHitTest.cs` | `TestRect` call site | Same |

**`NearestPort` caller audit:** one caller (`SchematicViewModel.cs:2132`) discards `CompId` and `PortIdx` via `var (pFound, _, _, px, py)`. The returned slot index is harmless and consistent with Brief K's convention.

**Gate 1** ✅ — Wire-draw snapping uses resolved `.csym` pins; unresolved cells snap to nothing.  
**Gate 2** ✅ — Crossing-selection expansion follows wires connected to a cell's real pins.  
**Gate 3** ✅ — Glyph click/rubber-band hit-test uses the resolved symbol's true bounds.  
**Built-ins unchanged** ✅ — `PortDefsOf` delegates to `SymbolPortDefs.For`; `EffectivePrimitivesOf` returns null → `ComputeGlyphBb()` built-in path.  
**No per-frame IO** ✅ — All three paths are per-click/per-rubber-band-release; `CellSymbolResolver` is cached.  
**All 1098 tests pass; build green.**
