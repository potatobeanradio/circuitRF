# Brief: polish-place-rotate (B14) — `R` rotates the placement ghost regardless of focus

**Goal.** While a component placement is armed, pressing `R` (CCW) / `Shift+R` (CW) rotates the ghost no
matter where keyboard focus is — in particular after arming from the Library Palette, where focus stays
in the palette panel and `R` currently does nothing.

Size: **S–M**. Files: `src/Ui/Views/WorkspaceWindow.axaml.cs`, `src/Ui/ViewModels/SchematicViewModel.cs`.

## Root cause (verified)

There are two placement paths and `R` reaches neither reliably:

1. **Palette-armed** (`PlacementService.Toggle` → `Pending` set): the ghost rotation is driven by
   `PlacementService.Rotate()`. But `R` only reaches it via `SchematicCanvas.OnKeyDown → VM.OnKeyDown`,
   which fires **only when the canvas has keyboard focus**. After clicking a palette tile, focus is in
   the palette, so `R` is dropped. There is no window-level key handler.
2. **Toolbar Ground/Term** (`BeginPlacement` → local `_placementRot`, `Pending` stays null): in
   `VM.OnKeyDown`, the `R` branch is
   `if (ActiveTool == Tool.Place && _placementService is not null) _placementService.Rotate(...)`.
   `_placementService` is **always** non-null (wired at doc creation), so this calls `Rotate()` which is
   a **no-op when `Pending` is null** — the local `_placementRot` ghost never rotates.

## Fix 1 — window-level rotate when a placement is armed (the main ask)

Add a tunnel `KeyDown` handler on `WorkspaceWindow` so `R`/`Shift+R` rotate the armed placement regardless
of focus. `WorkspaceViewModel.PlacementService` is public; `_vm` is already tracked in the window.

In the `WorkspaceWindow` constructor, after `InitializeComponent()` / the existing `HostWindowFactory`
line:

```csharp
AddHandler(InputElement.KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
```

Add the handler + a context guard:

```csharp
// While a placement is armed, R / Shift+R rotate the ghost regardless of which control has focus
// (palette tile, canvas, …). Scoped to the schematic-placement context so it never steals R from
// the Symbol Editor (rotate primitive), a text field, or other panels. Tunnel = fires before the
// SchematicView tunnel and the canvas bubble, so it wins when armed and they don't double-rotate.
private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
{
    if (_vm is null || _vm.PlacementService.Pending is null) return;  // only when armed
    if (e.Key != Key.R) return;
    if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return;  // leave ⌘/Ctrl+R alone
    if (!IsPlacementKeyContext(FocusManager?.GetFocusedElement())) return;

    _vm.PlacementService.Rotate(clockwise: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    e.Handled = true;
}

// True only when focus is inside a schematic editor or the Library Palette (and not a text field),
// i.e. the contexts where R-as-rotate-the-ghost is the intended meaning.
private static bool IsPlacementKeyContext(IInputElement? focused)
{
    if (focused is TextBox) return false;            // typing — don't steal R
    if (focused is not Visual v) return false;
    return v.FindAncestorOfType<Content.SchematicView>()  is not null
        || v.FindAncestorOfType<Palette.PaletteToolView>() is not null;
}
```

Add the usings to `WorkspaceWindow.axaml.cs`:
`using Avalonia.Input;`, `using Avalonia.VisualTree;` (for `FindAncestorOfType`),
`using CircuitRF.Ui.Views.Content;` and `using CircuitRF.Ui.Views.Palette;` (or fully-qualify
`Content.SchematicView` / `Palette.PaletteToolView` as written). `Avalonia.Controls` (TextBox, Window) is
already present.

Rotation semantics match the rest of the app: `R` = CCW (`clockwise:false`), `Shift+R` = CW
(`clockwise:true`). `PlacementService.Rotate` updates `Pending.Rotation`; the active schematic VM's
`OnSvcPropertyChanged` syncs `_placementRot` and the live ghost, so the visible ghost rotates immediately.

**No double-rotate:** the window tunnel fires before the canvas bubble and sets `Handled`, so the
`VM.OnKeyDown` path is skipped when armed. The `SchematicView` tunnel (registered `handledEventsToo:true`)
still runs but ignores `R`, so no conflict.

## Fix 2 — toolbar Ground/Term ghost also rotates (related; drop if undesired)

In `SchematicViewModel.OnKeyDown`, change both `R` cases to gate on `Pending`, so toolbar placement (local
`_placementRot`, `Pending` null) routes to `RotateSelection`, which rotates `_placementRot` and rebuilds
the ghost. (The canvas is focused after `OnPlaceGround`/`OnPlaceTerm` call `SchematicCanvasCtrl.Focus()`,
so `R` already reaches `VM.OnKeyDown` for this path — it was just routed to the no-op.)

```csharp
case Key.R when !modifiers.HasFlag(KeyModifiers.Shift):
    if (ActiveTool == Tool.Place && _placementService?.Pending is not null)   // was: _placementService is not null
        _placementService.Rotate(false);
    else
        RotateSelection(clockwise: false);
    return true;
case Key.R when modifiers.HasFlag(KeyModifiers.Shift):
    if (ActiveTool == Tool.Place && _placementService?.Pending is not null)   // was: _placementService is not null
        _placementService.Rotate(true);
    else
        RotateSelection(clockwise: true);
    return true;
```

`RotateSelection(clockwise)` with an empty selection already rotates `_placementRot` and calls
`RebuildOverlay()` (which builds the ghost from `_placementRot` when `ActiveTool == Place`), so the
toolbar-placement ghost now rotates. When not in Place mode, behaviour is unchanged (rotate selection).

## Verification (manual — window key routing isn't unit-testable)

- Arm a component from the **Library Palette**, move the mouse over the schematic without clicking
  → press `R`/`Shift+R`: the ghost rotates CCW/CW. Works even if you never click the canvas first.
- Click the **Ground** (or **Term**) toolbar button, then `R`/`Shift+R`: the ghost rotates (Fix 2).
- With **no placement armed**, select a component and press `R`: it still rotates the selection (the
  window handler bails because `Pending` is null).
- Open the **Symbol Editor** tab, arm nothing, press `R`: still rotates the symbol selection (window
  handler bails on `Pending`/context). Arm a palette item, focus a **text field** (e.g. inline edit),
  press `R`: types "r", no rotate.

## Acceptance

- `R`/`Shift+R` rotate the armed (palette) placement ghost regardless of focus.
- Toolbar Ground/Term placement ghost also rotates with `R`/`Shift+R`.
- Rotating a selection (not in placement) and `R` inside text fields / the Symbol Editor are unaffected.
