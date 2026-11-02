# Brief 91 — H hides the selection, and a toolbar button shows and sets its visibility

**Tag:** `R-em3d91-n` · **Series:** 3D editor round 8 (90–95).
**Area:** `src/Ui/ThreeD/C3dEditorViewModel*.cs` (a new `C3dEditorViewModel.HideSelection.cs`),
`src/Ui/Viewer3D/Viewer3DViewModel.Selection.cs` (the key switch), `src/Ui/Views/ThreeD/C3dEditorView.axaml` (+ `.cs`, the
tree's key handler), the 3D menu, `docs/user/src/reference/drawing-in-3d.md`
**Depends on:** 90 (`SetRowsVisible`, the one visibility function for every row kind) · **Blocks:** —

## What to build

### `R-em3d91-1` — the command

`ToggleSelectionVisibility`, one command on the editor view model:

- **What "the selection" is:** the tree's selected rows (`SelectedTreeItems`) when the tree has any. Otherwise the scene's
  selected objects, mapped to their rows (`RowOf`). A group's row stands for its members, and so does a group selected
  whole in the view. Records count too: a probe, a mesh region, a thermal boundary, a symmetry plane, a field plot.
- **Its state**, computed over those rows: `AllVisible`, `AllHidden`, `Mixed`, or `None` (nothing selected).
- **A press:**
  - `AllVisible` hides them all.
  - `AllHidden` shows them all.
  - `Mixed` makes them all **visible**. The next press is then `AllVisible` and hides them. This is the owner's cycle:
    all visible first, then not visible.
- It writes through brief 90's `SetRowsVisible`, one undo entry per press, and adds no visibility code of its own.
- **The selection survives a hide.** Today `SetHidden` clears the view's selection when it hides (`if (hidden)
  Viewer.SetSelection([])`), which would leave nothing selected for the second H to show again. For this command the
  tree rows stay selected. The view draws no highlight on a hidden object (there is nothing drawn to highlight), and the
  Inspector keeps showing it. The right-click ▸ Hide path may keep today's behaviour; say in RESOLVED.md which paths
  changed.
- An instance's parts: hidden in the view only, as their ticks are today.

### `R-em3d91-2` — the key

- **H** in the 3D pane: a new case in `Viewer3DViewModel`'s key switch, `case Key.H when !gestureInProgress`, routed to
  the edit host. The read-only viewer has no edit host, so H does nothing there. **H is free today**; the letters in use
  are O F V E S M P C A and B (B / Shift+B step behind / in front, `Cycle`). Checked across every 3D key path
  (2026-09-29): no `Key.H` anywhere in `src/Ui`. The one H is **Shift+A ▸ H** (arm the Heat source tool), a letter inside
  the draw popup's own handler, exactly as Shift+A ▸ B is Box beside plain B's cycle; it is unaffected. `DrawKey` runs
  before the viewer's switch, so a tool gesture in progress keeps the keyboard. Re-check this when building, because a
  brief after this one may have taken H.
- **H in the object tree** too (the tree's `KeyDown` tunnel, beside `OnTreeGroupKey`). The tree is where a
  multi-selection is usually made.
- Never while a text field has focus. `FieldInput`, the Inspector, the rename box and the plane-offset box all take
  their own letters.
- A 3D menu item **Hide / Show Selection** with the gesture shown, and the same item in the tree's and the canvas's
  context menus, all calling the one command.

### `R-em3d91-3` — the toolbar button

- A `Button` (not a `ToggleButton`: a toggle's checked style would compete with the three-way background) placed
  **immediately left of the mesh-view button** (`ViewModel.Viewer.ShowMesh`'s `ToggleButton`, `C3dEditorView.axaml`
  ≈ line 320), inside the same separator group.
- **Glyph:** one fixed icon (`EyeOff`, or `Eye`; pick one and keep it). **The glyph never changes with state.**
- **Background shows the state:**
  - `AllVisible`: the accent brush (`SystemAccentColor`), the look a checked toggle has elsewhere on this toolbar.
  - `Mixed`: the same accent at reduced opacity (about 45 %), visibly dimmer than `AllVisible`.
  - `AllHidden`: transparent (the toolbar's plain button).
  - `None`: disabled.

  Bind the background to a state property through a converter or styles on a class (`:visible`, `:mixed`). Use a theme
  resource, not a hard-coded colour, so the dark theme works.
- **Tooltip** names the state and the gesture: "Hide the selection (H)", "Show the selection (H)", "Some of the selection
  is hidden: show all of it (H)", "Select something to hide or show it".
- It is shown in the editor **and** in a setup's view-only 3D view, as the mesh button is. In the view-only view it
  hides for the session only, as that view's ticks do.
- The state property is raised whenever the selection changes, a tick changes, or undo/redo runs (`RaiseMenuStateChanged`
  is the existing hook).

## Gate

View-model tests only, run with `--filter`. Say in the report that the pixels were not seen.

1. Three boxes selected, all visible: state `AllVisible`. Press: all hidden, one undo entry, **still selected**, state
   `AllHidden`. Press: all shown.
2. Two visible and one hidden: state `Mixed`. Press: all visible. Press: all hidden.
3. A group selected whole: the members follow, including a member that was hidden on its own before.
4. A probe and a thermal boundary selected with a box: all three follow (brief 90's function is the one called).
5. Nothing selected: the command's `CanExecute` is false.
6. The key: `Viewer3DViewModel`'s key handler with H and an edit host invokes the command. With focus in a text field
   the tunnel does not reach it.

## Docs

`drawing-in-3d.md`: H, the button, the three backgrounds and the mixed-state cycle. Edit the doc sources only.

## On completion

`src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
