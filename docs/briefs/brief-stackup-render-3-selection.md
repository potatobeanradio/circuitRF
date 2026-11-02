# Brief 3 — selection: click a band, land on its fields

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk3-n`
**Area:** `src/Ui/Controls/StackupCanvas.cs`, `src/Ui/Layout/TechEditorViewModel.cs`, `TechEditorView.axaml(.cs)`
**Depends on:** brief 2 · **Independent of:** briefs 4, 5, 6, 7

---

## 0. What this brief delivers

Click anywhere on a dielectric, conductor or via in the drawing and:

1. a highlight outline is drawn around it,
2. the card list below **scrolls to that element's card**,
3. that card's background is shaded in the same highlight colour, so the user can see that the thing
   they clicked and the fields they are now looking at are the same thing,
4. `Esc` clears all of it.

And the converse, because a one-way link is a link the user stops trusting: **selecting a card selects
its band.**

---

## 1. Selection lives on the view model, not on the canvas

### R-stk3-1

```csharp
// TechEditorViewModel
/// <summary>The stackup entry the drawing and the card list are both pointing at, by
/// StackupLayer.Name — null when nothing is selected. Name rather than a row VM or a StackupLayer
/// reference because ApplySnapshot REPLACES Working and rebuilds every row VM on every committed
/// edit, undo and redo; a held reference survives none of those and a selection that silently
/// evaporates on the first edit is worse than no selection.</summary>
[ObservableProperty] private string? _selectedStackupLayerName;
```

**By name.** This is the trap in this brief and it is worth spelling out: `ApplySnapshot` does
`Working = TechPersistence.Deserialize(json)` and `RebuildStackup()` clears and rebuilds every
`StackupLayerRowViewModel`. Every object identity in the stackup is destroyed on every committed edit.
A name survives; a reference does not.

A rename is then the one case a name does not survive — and it is handled where the rename happens:
`StackupLayerRowViewModel.CommitName()` re-points `SelectedStackupLayerName` when it renamed the
selected entry (`R-stk3-8`). Names are already unique-by-construction for new entries
(`NextFreeStackupName`) and duplicates are a validation problem the editor already reports; on a
duplicate, the selection resolves to the first match and that is stated rather than guarded against.

Both surfaces read this one property. Neither owns it.

---

## 2. The canvas half

### R-stk3-2 — click selects

`OnPointerPressed`, left button: `scene.HitTest(p.X, p.Y)`. A `Band` or `ViaBarrel` hit sets
`SelectedStackupLayerName` to the hit's `LayerName`. An `Empty` hit **clears** it — clicking the
background is how a drawing surface has always meant "nothing". A `Label` hit selects the label's own
layer too (brief 4 then makes a *double*-click on a value open an editor; a single click still just
selects).

### R-stk3-3 — the outline

`StackupOverlay.SelectedLayer` is already a field brief 1 defined. Set it, invalidate, and
`StackupRenderer` draws the outline around `scene.RectOf(name)` — for a via, around the barrel, not
around the bands it crosses.

Colour: `ColorRole.LayoutSelection`, so a selected band and a selected shape in the layout editor are
the same colour. The outline is drawn **outside** the band's own edge with a gap, so it never hides the
ground-reference heavy edge that brief 1 §1 makes the one distinguishing mark on the picture.

### R-stk3-4 — hover

A hovered band gets a lighter outline and, on a via, reveals brief 1's grippers. `PointerExited` clears
it. This is cheap here and it is what makes the drawing read as clickable at all; without it the first
click is a guess.

---

## 3. The card half

### R-stk3-5 — scroll to the card, and the filter problem

`TechEditorView.axaml.cs` observes `SelectedStackupLayerName` and calls
`StackupList.ScrollIntoView(row)` for the matching `StackupLayerRowViewModel` in `FilteredStackupLayers`.

**The filter can hide it.** `ApplyStackupFilter` builds `FilteredStackupLayers` from `StackupFilter`,
so a click on a band whose name does not match the filter scrolls to nothing at all — silently, which
is the outcome that makes the feature feel broken rather than limited.

**Resolution: a canvas selection clears the filter when the filter would hide it.** The entire purpose
of the click is to land on that entry's fields; a click that appears to do nothing is worse than a
filter the user has to re-type. Clear it, do not narrow it and do not warn about it.

Do this in the view model (clearing `StackupFilter` re-runs `ApplyStackupFilter` through its existing
`partial void OnStackupFilterChanged`), not in the code-behind — so it holds for brief 6's context-menu
selections too.

`ScrollIntoView` must run **after** the list has re-materialised its containers. The filter clear
rebuilds `FilteredStackupLayers` synchronously but the `ListBox` arranges later; post it on the
dispatcher at `Background`, which is what the existing `FocusForScrollingDeferred` in this same file
already does for the analogous problem. Do not add a timer.

### R-stk3-6 — the shaded card

The card `Border` in the `ListBox.ItemTemplate` binds its `Background` to a new
`StackupLayerRowViewModel.IsSelected` — set by the row VM reading the owner's
`SelectedStackupLayerName`, raised on change.

Shade, not outline: the owner asked for the **group background** to be shaded, so the connection between
the clicked band and the card is unmistakable. Use the selection colour at a low alpha so the card's own
text and the `needsvalue` warning styling stay legible on top of it.

`StackupList` has `SelectionMode="Single"`. Two selection models on one list is a fight: **drive the
`ListBox`'s own `SelectedItem` from `SelectedStackupLayerName` and vice versa** (`R-stk3-7`), so
clicking a card selects its band and clicking a band highlights its card, with no re-entrancy — guard
with a `_suppressSelectionSync` flag on the same pattern `_suppressBoundaryCommit` already uses in this
view model.

### R-stk3-8 — a rename keeps the selection

Renaming the selected entry through the card's Name box, or through brief 4's inline editor, re-points
`SelectedStackupLayerName` to the new name. Without this, renaming the thing you are looking at
deselects it.

---

## 4. `Esc`

### R-stk3-9

`Esc` with the drawing focused clears `SelectedStackupLayerName`, which clears the outline, the card
shading and the `ListBox` selection together.

**It does not clear the filter back**, and it does not scroll anywhere. Undoing a filter clear on
`Esc` would make `Esc` a second undo, which it is not.

**Precedence with brief 4's inline editor** — stated here and again there: while an inline editor is
open, `Esc` reverts the edit and the selection stands. A second `Esc`, with no editor open, clears the
selection. The canvas's key handler must therefore check "is an editor open" first and do nothing if it
is, letting the editor's own handler take it. `R-stk4-6`.

Wire `Esc` on the canvas (`Focusable = true`, take focus on pointer press) **and** as a tunnelling
handler on the tab, so it works when focus is in the card list too — the owner's ask is "pressing
`Esc` will unselect", not "pressing `Esc` while the drawing happens to have focus".

---

## 5. Tests

`tests/Ui.Tests/Stackup/StackupSelectionTests.cs`:

| Tag | Test |
|---|---|
| `R-stk3-1` | The selection survives a committed edit: select a conductor, commit a thickness change on a different entry, the selection still resolves to the same entry. (This fails outright if the selection is held as a reference — it is the test that pins the whole design decision.) |
| `R-stk3-2` | A click inside a band's rect selects that band; a click in the background clears it; a click on a via barrel selects the via, not the conductor behind it. |
| `R-stk3-3` | `StackupOverlay.SelectedLayer` reaches the renderer, and the outline rect is outside the band rect. |
| `R-stk3-5` | With a filter set that excludes the clicked band, the click clears the filter and the row is present in `FilteredStackupLayers` afterwards. |
| `R-stk3-6` | The matching row VM's `IsSelected` is true and every other row's is false. |
| `R-stk3-7` | Selecting a card sets `SelectedStackupLayerName`; setting `SelectedStackupLayerName` selects the card; neither re-enters. |
| `R-stk3-8` | Renaming the selected entry keeps it selected. |
| `R-stk3-9` | `Esc` clears the selection from the canvas and from the card list. |

`ScrollIntoView` itself is not directly assertable without a realised, arranged list; assert the
**decision** (which row was asked for, and that it is in `FilteredStackupLayers`) rather than the pixel
offset. A test that arranges a `ListBox` to check a scroll offset is a test that measures the layout
engine.

---

## 6. Scope

No editing, no drag, no menus, no clipboard. A single click and `Esc` only.

**On completion:** record findings in `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
