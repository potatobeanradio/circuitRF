# Brief: polish-glyph-only-hitbox (B9) — only a component's glyph selects it, not its labels

**Goal.** A left-click on a component's **label** (type / instance-name / parameter text) must **not**
select the component. Only a click on the component's **glyph** selects it. Land this before B10
(cyclic click-through), which assumes labels are transparent to selection.

Authority: laundry-list "Behavior Change: Only the component glyphs hitbox should select a component."
Size: **S**. Files: `src/Ui/Schematic/SchematicHitTest.cs`, `src/Ui/ViewModels/SchematicViewModel.cs`.

## Today

`SchematicHitTest.Test` returns the topmost object, and **step 1 tests component label zones at the
highest Z** (`ComponentType` / `ComponentName` / `ComponentParam`), each carrying the component's `Id`.
`SchematicViewModel.HandleSelectPress` then does `Selection.SelectOne(hit.Id)` / `Toggle(hit.Id)` — so
clicking a label selects (and sets up a drag of) the whole component.

Labels must remain interactive for **double-click-to-edit** (`OnDoubleTapped`) and the **right-click
context menu** (`OnPointerPressed` right-button) and the **Move Labels (F5)** tool — so we don't remove
label hit-testing globally; we make *left-click selection* ignore labels.

## Change 1 — `Test` gains an opt-out for labels

Add a parameter (default `true`, so all existing callers are unchanged) and guard step 1:

```csharp
public static HitResult Test(
    SchematicEditModel  editModel,
    SchematicModel      renderModel,
    SchematicSpatialIndex index,
    double worldX, double worldY,
    double hitRadius = DefaultHitRadius,
    bool includeLabels = true)          // NEW
{
    double half = hitRadius;
    var candComps = new HashSet<int>();
    var candWires = new HashSet<int>();
    index.QueryViewport(worldX - half, worldY - half, worldX + half, worldY + half,
                        candComps, candWires);

    // ── 1. Text label zones (highest Z) ──────────────────────────────────
    if (includeLabels)                  // NEW guard
    {
        foreach (int i in candComps.OrderByDescending(x => x))
        {
            if (i >= editModel.Components.Count) continue;
            var textHit = TestComponentLabels(editModel.Components[i], worldX, worldY);
            if (textHit.Kind != HitKind.None) return textHit;
        }
    }

    // ── 2. Component symbol glyphs … (unchanged) ──
    …
}
```

## Change 2 — select-tool clicks ignore labels

In `SchematicViewModel.HandleSelectPress`, the hit-test call becomes label-excluded:

```csharp
// was: var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy);
var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy, includeLabels: false);
```

Everything else in `HandleSelectPress` stays. Effect: a click on a label is now transparent to
selection — it resolves to whatever is *under* the label (the glyph only if the cursor is actually on
the glyph; otherwise a wire/dot/etc. behind it, or `None`). A label click over empty space therefore
behaves like a background click (clears selection / starts a rubber-band), which is the existing
background behavior — acceptable, and consistent with "labels don't select."

## Leave unchanged (verify)

- **Double-click to edit a label** — `OnDoubleTapped` calls `Test` with the default (`includeLabels:
  true`), so `ComponentType`/`Name`/`Param` hits still drive inline editing. (The single click that
  precedes the double-tap may clear the prior selection via the background path; that's fine.)
- **Right-click context menu** — `SchematicCanvas.OnPointerPressed` (right button) calls `Test` with
  the default and still resolves a label hit to the component's `Id` for `ContextMenuTargetId` +
  `SelectIfUnselected`. So right-clicking a component's label still targets that component's menu.
  (If you want right-click to also be glyph-only, say so — I kept it as-is since it only affects the
  context-menu target, not left-click selection.)
- **Move Labels (F5)** — its own press handler (`HandleMoveLabelPress`) is unaffected; labels stay
  pickable there.

## Tests

In the existing `SchematicHitTest` tests (`tests/Ui.Tests`), add: a component with a visible label,
click coordinates over the **label** text →
- `Test(..., includeLabels: true)` returns `ComponentType`/`ComponentName`/`ComponentParam` (unchanged).
- `Test(..., includeLabels: false)` returns `None` (or the object behind, if one is placed under the
  label) — never `Component` for a label-only click.
And a click over the **glyph** returns `Component` in both modes.

## Acceptance

- Left-clicking a component's type/name/parameter text does **not** select the component.
- Left-clicking the component's glyph selects it as before.
- Double-click-to-edit-label, right-click context menu, and F5 Move Labels all still work.
