# Sonnet Brief — GND (and suppressed) component labels should have no hit zone

**Bug:** The Ground symbol exposes a clickable **type-label** hitbox in the schematic editor (right-click /
double-click near where its type label would be registers a `ComponentType` hit). GND is special and must never
expose its Type. More broadly, the hit-test creates label hit zones even for labels that aren't rendered.

**Root cause (confirmed):** `src/Ui/Schematic/SchematicHitTest.cs` → `TestComponentLabels`. Row 1 (instance name)
is special-cased for Ground (`comp.Symbol == SymbolKind.Ground ? "" : comp.InstanceName`), but **row 0 (type)**
is computed unconditionally:
```csharp
0 => ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount),
```
and the method never consults `comp.ShowTypeLabel` / `comp.ShowInstanceName`. So GND (and any component with a
suppressed type/name label) still gets a hit zone for a label the renderer doesn't draw. (`ToRenderComponent`
correctly emits `""` for suppressed labels and the renderer skips empties — the hit-test just doesn't match.)

## Fix (minimal, aligns hit-test with render)
In `TestComponentLabels`, skip a label row when that label is not rendered. Add, at the top of the
`for (int row = 0; row < totalRows; row++)` body (before the zone math):
```csharp
bool suppressed = row switch
{
    0 => comp.Symbol == SymbolKind.Ground || !comp.ShowTypeLabel,      // GND never exposes Type; honor ShowTypeLabel
    1 => comp.Symbol == SymbolKind.Ground || !comp.ShowInstanceName,   // honor ShowInstanceName
    _ => false,                                                        // param rows exist only when shown
};
if (suppressed) continue;
```
`EditableComponent` already exposes `ShowTypeLabel` and `ShowInstanceName` (bool, public). The existing
`labelText` switch can stay as-is — the suppressed rows are now skipped before any zone is computed, so the GND
type hitbox disappears and other rows keep their slot positions (consistent with the renderer, which keeps label
slots and skips empty text).

This removes the GND type hitbox (the report) and, as a correctness bonus, stops any component from having a hit
zone for a label it isn't showing. Row indexing for params is unaffected (they're only in `shownParams`).

**Do not** change the render path or `ComponentTypeRegistry`; the fix is hit-test-only.

## Tests (`tests/Ui.Tests`, headless — `SchematicHitTest.Test`/`TestComponentLabels` are framework-free)
1. **`Ground_NoTypeHit`**: place a Ground component; hit-test at the type-label row position (row 0 zone:
   `comp.Y + LabelStartOffY + 0.5*LabelRowHeight`, `x ≈ comp.X - 130`) → `HitKind.None` (or the glyph, never
   `ComponentType`).
2. **`SuppressedTypeLabel_NoTypeHit`**: a normal component (e.g. Resistor) with `ShowTypeLabel=false` → no
   `ComponentType` hit at row 0; with `ShowTypeLabel=true` → `ComponentType` hit IS returned (regression guard).
3. **`SuppressedInstanceName_NoNameHit`**: component with `ShowInstanceName=false` → no `ComponentName` hit at
   row 1; param-row hits still resolve to the correct `SubIndex` (alignment preserved).

## Gate
Build 0W/0E; tests green. Manually: right-clicking/double-clicking where GND's type label would be no longer
opens a type-label edit or selects a type label; GND body/glyph still selects normally; normal components with
visible labels are unchanged.
