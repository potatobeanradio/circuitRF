# Brief: polish-grid-pin-netlabel (B11 + B12 + B13)

Three small, independent schematic fixes in one brief. Land in any order; they don't overlap.
Files: `src/Ui/ViewModels/SchematicViewModel.cs`, `src/Ui/Views/Content/SchematicView.axaml`,
`src/Ui/Views/Content/SchematicView.axaml.cs`, tests in `tests/Ui.Tests`.

NB: most of what the original list implied is already built — **B12's placement path and B11's
delta-snap already exist**. The real gaps are narrow; don't re-implement what's there.

---

## Part A (B11) — F5 label move: user-selectable snap grid (none / p / P), mirroring the Symbol Editor

**Today.** `ComputeLabelDelta` already grid-snaps the F5 label-move delta — but always to `AuthorGridSize`
(the fine grid), with no user control, and the toolbar's grid control is a binary `ToggleButton`
(`GridSnapToggle` → `OnGridSnapToggle` sets `vm.GridSnap`). The Symbol Editor already has exactly the
tri-state the user wants: a `SnapMode` enum (`ConnectionGrid` / `FineGrid` / `None`), a `CycleSnapMode`
command, a `SnapModeTooltip`, and a single toolbar button whose colour encodes the state
(opaque accent = ConnectionGrid "P", 40 % accent = FineGrid "p", default = None). We mirror that on the
schematic and wire the F5 label snap to it.

**Scope:** SnapMode governs the **F5 label-move snap only** (the user's explicit ask). Component / wire /
canvas-object placement keeps snapping to the connection grid exactly as today — do **not** change those.

### A1 — SchematicViewModel: add the tri-state (reuse the existing enum)

`SchematicViewModel` is in `CircuitRF.Ui.ViewModels`, the same namespace as the existing top-level
`SnapMode` enum that `SymbolEditorViewModel` uses (members `ConnectionGrid`, `FineGrid`, `None`).
**Reuse it — do not declare a new enum.** (If you find it nested inside `SymbolEditorViewModel`, promote
it to a namespace-level enum; both references are unqualified so promotion is safe.)

Add near the existing `[ObservableProperty] private bool _gridSnap = true;` (mirrors the Symbol Editor):

```csharp
/// <summary>
/// Tri-state grid for the F5 label move (mirrors the Symbol Editor):
/// ConnectionGrid = snap to the connection grid (P), FineGrid = snap to the fine grid (p), None = free.
/// </summary>
[ObservableProperty, NotifyPropertyChangedFor(nameof(SnapModeTooltip))]
private SnapMode _snapMode = SnapMode.FineGrid;   // default preserves today's behaviour (fine-grid snap)

public string SnapModeTooltip => SnapMode switch
{
    SnapMode.ConnectionGrid => "Snap: Connection Grid  (G)",
    SnapMode.FineGrid       => "Snap: Fine Grid  (G)",
    _                       => "Snap: Off  (G)",
};

/// <summary>Cycles P → p → none → P (same order as the Symbol Editor).</summary>
public void CycleSnapMode()
{
    SnapMode = SnapMode switch
    {
        SnapMode.ConnectionGrid => SnapMode.FineGrid,
        SnapMode.FineGrid       => SnapMode.None,
        _                       => SnapMode.ConnectionGrid,
    };
}
```

(The toolbar calls `CycleSnapMode()` directly from code-behind — no `[RelayCommand]` needed, since the
schematic toolbar uses Click handlers, unlike the Symbol Editor.)

### A2 — wire the F5 label delta to SnapMode

Replace the `static` `ComputeLabelDelta(double, double, KeyModifiers, double gridSize)` with an **instance**
method that reads `SnapMode` and picks the grid:

```csharp
/// <summary>
/// Applies the schematic SnapMode + Shift axis-lock to a raw label drag delta.
/// SnapMode.None → free; FineGrid → snap to AuthorGridSize (p); ConnectionGrid → snap to GridSize (P).
/// Ctrl forces free movement regardless of SnapMode. Shift locks to the dominant axis first.
/// </summary>
private (double DX, double DY) ComputeLabelDelta(double rawDx, double rawDy, KeyModifiers modifiers)
{
    bool ctrl  = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
    bool shift = modifiers.HasFlag(KeyModifiers.Shift);

    double dx = rawDx, dy = rawDy;
    if (shift)
    {
        if (Math.Abs(rawDy) >= Math.Abs(rawDx)) dx = 0;   // predominantly vertical — lock X
        else                                    dy = 0;   // predominantly horizontal — lock Y
    }

    double grid = SnapMode switch
    {
        SnapMode.ConnectionGrid => EditModel.GridSize,        // P
        SnapMode.FineGrid       => EditModel.AuthorGridSize,  // p
        _                       => 0,                          // None → no snap
    };
    if (!ctrl && grid > 0)
    {
        dx = Math.Round(dx / grid) * grid;
        dy = Math.Round(dy / grid) * grid;
    }
    return (dx, dy);
}
```

Update both call sites to drop the grid-size argument:
- `HandleMoveLabelMove`: `var (dx, dy) = ComputeLabelDelta(wx - _moveLabelRefX, wy - _moveLabelRefY, modifiers);`
- `CommitMoveLabels`: same call shape.

**Verify before trusting the mapping:** read `GridSize` and `AuthorGridSize` in `SchematicEditModel`
(EditableSchematic.cs). The connection grid (P) must be the *coarser* of the two (the symbol port comment
says "100 units = 1 grid square", matching the Symbol Editor's `PinGrid=100`/`SmallGrid=5`). If
`AuthorGridSize > GridSize`, swap the mapping so **P is coarser than p**.

### A3 — the existing `GridSnap` bool

Grep `GridSnap` / `_gridSnap`. In the code I read it's set only by `OnGridSnapToggle` and never *read*
(component/wire snapping uses `EditModel.SnapToGrid` unconditionally). Confirm that, then:
- **If unused** → delete the `_gridSnap` property and the `OnGridSnapToggle` handler (alpha, no back-compat).
- **If something reads it** (grid-dot rendering, placement gating, persistence) → keep it and mirror the
  tri-state with `partial void OnSnapModeChanged(SnapMode value) => GridSnap = value != SnapMode.None;`
  so existing consumers still work.

SnapMode is view state — do **not** persist it (matches the Symbol Editor; defaults to FineGrid each open).

### A4 — toolbar button (SchematicView.axaml)

Copy the two snap style blocks **verbatim** from `SymbolEditorView.axaml`'s `<UserControl.Styles>` into
`SchematicView.axaml`'s `<UserControl.Styles>`: `Button.snap-connection` (+ `:pointerover`) and
`Button.snap-fine` (+ `:pointerover`). (No style for "none" — default look signals off.)

Replace the existing grid control:

```xml
<!-- was: <ToggleButton x:Name="GridSnapToggle" .../> -->
<Button x:Name="SnapModeBtn"
        Click="OnCycleSnapMode"
        ToolTip.Tip="Snap: Fine Grid  (G)"
        Padding="6,3">
    <mi:MaterialIcon Kind="Grid" Width="16" Height="16"/>
</Button>
```

(The tooltip + colour classes are set imperatively in code-behind — the literal Tip here is just the
initial value before first `UpdateSnapModeButton()`.)

### A5 — code-behind (SchematicView.axaml.cs)

`SchematicView.axaml.cs` already `using CircuitRF.Ui.ViewModels;`. Remove `OnGridSnapToggle`; add:

```csharp
private void OnCycleSnapMode(object? sender, RoutedEventArgs e)
{
    Vm?.CycleSnapMode();
    UpdateSnapModeButton();
    SchematicCanvasCtrl.Focus();
}

private void UpdateSnapModeButton()
{
    var mode = Vm?.SnapMode ?? SnapMode.FineGrid;
    SnapModeBtn.Classes.Set("snap-connection", mode == SnapMode.ConnectionGrid);
    SnapModeBtn.Classes.Set("snap-fine",       mode == SnapMode.FineGrid);
    ToolTip.SetTip(SnapModeBtn, Vm?.SnapModeTooltip ?? "Snap: Off  (G)");
}
```

Call `UpdateSnapModeButton()`:
- at the end of `RebindActiveViewModel()` (right after `UpdateToolButtonStates();`), and
- in `OnViewModelPropertyChanged` — extend the existing `if` to also fire on
  `nameof(SchematicViewModel.SnapMode)` → `UpdateSnapModeButton()`.
  (Keep the tool-state branch as is; just add a SnapMode check.)

### A6 — `G` shortcut (mirrors the Symbol Editor's "(G)" tooltip)

In `OnViewKeyDownTunnel`'s non-ctrl `switch (e.Key)` (next to `S`/`W`/`Z`/`F`):

```csharp
case Key.G:
    vm.CycleSnapMode();
    UpdateSnapModeButton();
    e.Handled = true;
    break;
```

(If you'd rather not add the shortcut, drop the "(G)" from the three tooltip strings in A1.)

---

## Part B (B12) — Pin / Term Num conflict auto-resolves on inline type-change

**Today.** `CommitPlacement` already assigns the next-free `Num` for both `Pin` and `Term` via
`NextFreePinNum` / `NextFreeTermNum` — placement is fine. The gap is the **inline type-label edit**
(`CommitInlineEdit` → `InlineEditKind.ComponentType`): it seeds `DefaultParameters` (Num placeholder "1")
and never resolves it, so changing a component's type to Pin/Term via its type label collides on "1".

**Fix.** In `CommitInlineEdit`, in the `ComponentType` case, right after the
`foreach (var dp in ComponentTypeRegistry.DefaultParameters(newKind, portCount)) newComp.Parameters.Add(...)`
loop and **before** `Execute(new ChangeComponentTypeCommand(...))`, add (mirrors `CommitPlacement`):

```csharp
// Auto-assign the next-free Num so a type-change to Pin/Term never duplicates an existing number.
if (newKind == SymbolKind.Term)
{
    var np = newComp.Parameters.FirstOrDefault(p => p.Name == "Num");
    if (np is not null) np.Expression = NextFreeTermNum(EditModel).ToString();
}
else if (newKind == SymbolKind.Pin)
{
    var np = newComp.Parameters.FirstOrDefault(p => p.Name == "Num");
    if (np is not null) np.Expression = NextFreePinNum(EditModel).ToString();
}
```

`NextFreeTermNum` / `NextFreePinNum` count existing components *of that kind*; the component being changed
is still its old kind at this point, so it never collides with itself. No other change.

(Out of scope unless you've seen it: paste of a Pin/Term copies its Num and could collide. Say so and
I'll add a separate brief for `SchematicPasteCommand`.)

---

## Part C (B13) — empty net-node name removes the label (no rendered label, no explicit name)

**Today.** `CommitInlineEdit`'s `WireNetLabel` case bails on empty (`if (newVal.Length == 0) break;`).
For a *new* label that's correct (nothing placed). But editing an **existing** label to empty is silently
ignored — you can't clear a net name.

**Fix.** Clearing an existing label deletes it, so the node reverts to its implicit/auto name and renders
nothing. `DeleteCommand` already handles net labels by Id (it snapshots/removes/restores `NetLabels`), so
reuse it — no new command. Replace the `WireNetLabel` case body:

```csharp
case InlineEditKind.WireNetLabel:
{
    if (newVal.Length == 0)
    {
        // Cleared: delete the existing label (node reverts to its implicit name, nothing rendered).
        // Nothing to do when there was no label (empty new label was never placed).
        if (label is not null)
            Execute(new DeleteCommand(EditModel, new[] { label.Id }));
        break;
    }
    if (label is not null)
    {
        if (newVal != label.Name)
            Execute(new RenameNetLabelCommand(EditModel, label, newVal));
    }
    else
    {
        Execute(new PlaceNetLabelCommand(EditModel,
            new EditableNetLabel { Name = newVal, X = worldX, Y = worldY }));
    }
    break;
}
```

(`label` is the captured `_inlineEditExistingNetLabel`; `EditableNetLabel` has an `Id` — `DeleteCommand`
and the hit-test both key off it.)

---

## Tests (`tests/Ui.Tests`)

- **B11**: drive `SchematicViewModel` — set `SnapMode = ConnectionGrid` and assert a label move snaps to a
  `GridSize` multiple; `FineGrid` → `AuthorGridSize` multiple; `None` → unsnapped. If `ComputeLabelDelta`
  is too private to reach, assert via `CommitMoveLabels` producing the expected `MoveLabelsCommand`
  offsets. Also assert `CycleSnapMode` order P → p → none → P.
- **B12**: place two Pins (Nums 1, 2), then change a third component's type to `Pin` via `CommitInlineEdit`
  (`ComponentType`, value "PIN"/whatever `TryParseCode` accepts) → new Pin's `Num` == 3. Repeat for Term.
- **B13**: place a net label, then `BeginWireNodeLabelEdit` + `CommitInlineEdit` with empty value → the
  label is gone from `EditModel.NetLabels`; one Undo restores it.

## Acceptance

- Schematic toolbar shows one grid button that cycles none → p → P with the same colours as the Symbol
  Editor; `G` cycles it; F5 label moves snap to the chosen grid (none = free).
- Changing a component to Pin/Term via its type label lands on the next free Num (no "1" collision).
- Clearing a net label's text removes the label (undoable); a node with no label shows no name.
