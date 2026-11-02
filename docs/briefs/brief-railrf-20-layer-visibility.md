# Brief 20 — the board panel's own layer visibility, and a `.ctech` edit that does not need a save

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail20-n` · **Phase:** corrective
**Area:** `src/Ui/RailRf/RailRfViewModel.Board.cs`, `src/Ui/Views/RailRf/RailRfWindow.axaml`,
`src/Ui/Layout/TechEditorViewModel.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs`
**Depends on:** 1-18 · **Blocks:** nothing
**Found by:** a first-time designer's pass, 2026-09-20 — *"difficult to navigate if I can't disable
layers visibility"*, and then *"even if I use the technology file to toggle, it only works after
saving the file and then closing railRF and reopening"*

---

## 0. What this brief delivers

Two halves of one complaint.

| | |
|---|---|
| `R-rail20-1` | railRF gets its own per-layer visibility checkboxes — the owner's own proposal when the report came in |
| `R-rail20-2` | a `.ctech` visibility edit reaches every viewer **without a save**, which is the reason the round trip was unbearable in the first place |

R-rail20-1 is the feature. R-rail20-2 is the defect underneath it, and it is not railRF's — it
affects the layout editor identically and is simply less visible there.

---

## 1. `R-rail20-1` — railRF's own layer list

### Why the window needs one at all

The board panel is the only place in railRF where the artwork is read, and the four overlay tabs all
draw ON TOP of it. On a four-layer board with copper on every layer plus vias, the question *"which
layer is this run on"* is unanswerable without turning layers off — and today the only route is to
open the `.ctech`, find the layer, untick `Vis`, save, and come back.

That route is wrong on its own terms. **Layer visibility is a property of the VIEW, not of the
process.** A `.ctech` is a manufacturing document shared across a workspace; using it as a
per-window display switch means one user's navigation is another user's diff.

### `R-rail20-1a` — the list

A collapsible layer list in the board pane, one row per drawing layer of the resolved technology,
each with a visibility checkbox and the layer's own colour swatch and name. Seeded from the
technology's `Visible`, and thereafter the window's own.

### `R-rail20-1b` — session state, not document state

It is **not written to the `.ctech`** and it does not mark anything dirty. It IS written to the
`.crail`, alongside the panel toggles that already live there (commit `8995f1e0`, *"panel toggles
kept in the .crail"*), because which layers a reader wants to see on a particular board is a
property of that document's own reading.

### `R-rail20-1c` — the maps follow

`SyncHiddenLayers` (`RailRfViewModel.Board.cs:414`) already tells `BoardOverlayLayer` which drawing
layers the technology is not drawing, and the comment there states the rule this must keep:

> *"the maps are laid OVER the artwork, so a layer the renderer skips has to take its shading with
> it — otherwise turning a layer off in the `.ctech` removes the copper and leaves the drop map of
> it floating on the board."*

The window's own hidden set is unioned with the technology's and fed through the same call. There
is exactly one hidden-layer set reaching the overlay, not two.

### `R-rail20-1d` — hiding a layer never re-solves

The stackup is untouched, so the numbers stand. `StackupSignature` (`Board.cs:436`) already draws
this line and states why: *a user turning a drawing layer's visibility off is asking a question
about the PICTURE, and losing the answer they just ran for it would make the toggle cost a
re-solve.* The window's own toggles are on the same side of that line, and must not go near
`ClearResults`.

### `R-rail20-1e` — reset to the technology

One row at the top of the list: **Follow the technology**. It clears the window's overrides so the
list reads what the `.ctech` says. Without it, a user who has hidden four layers has no way to find
out what the document itself states, and the two would drift with nothing able to reconcile them.

---

## 2. `R-rail20-2` — a visibility edit must not need a save

### What is wrong, exactly

Traced through the code. `TechEditorViewModel` raises two events:

- **`StackupChanged`** (`src/Ui/Layout/TechEditorViewModel.cs:397`) — raised on every edit. It has
  **exactly one subscriber in the whole application**: `StackupCanvas`, the tech editor's own
  drawing, at `src/Ui/Controls/StackupCanvas.cs:144`.
- **`TechSaved`** (line 401) — *"fired after a successful save with the absolute path — the
  workspace's cue to call `TechnologyCache.Invalidate(path)`, which is what fires L0c's live-refresh
  seam."*

Only the second reaches anyone. `TechnologyCache.Invalidate` -> `OnTechnologyChanged`
(`WorkspaceViewModel.cs:1182`) -> `ApplyTechResolution` on every open layout document, and
`TechnologyReResolved` -> railRF's `AdoptLiveTechnology`.

So **no unsaved `.ctech` edit reaches any viewer, in any window.** The designer's report is precise:
it only works after saving the file. His second clause — *and then closing railRF and reopening* —
is the second-order effect of the first: after a save the adoption does fire, but by then he had
already learned the toggle did nothing and was closing the window to force it.

### Why this is not railRF's bug

The layout editor has it identically. It is less visible there because the tech editor usually opens
*from* the layout editor and a user saves as a matter of course. railRF made it obvious because the
board panel is the only view of the artwork and the designer was toggling layers to navigate.

### `R-rail20-2a` — a live seam for DISPLAY properties only

A new workspace-level event, raised from `TechEditorViewModel`'s existing edit funnel — the same
funnel `StackupChanged` is raised from, so there is no second place to remember — carrying the
technology's **working copy**. Subscribers re-read it and repaint.

**Display properties only**: `Visible`, `Selectable`, colour, fill pattern. **Not the stackup.** A
live stackup edit would invalidate every computed number in every open window on every keystroke,
which is the opposite of useful. The split already exists and is already argued in
`StackupSignature`'s own remarks: thicknesses, conductivities, dielectrics and the drawing layers a
stackup entry claims live under `Technology.Stackup`; visibility, colour and fill live on the
drawing-layer table beside it.

### `R-rail20-2b` — one instance, or `ReferenceEquals` eats it

`RailRfViewModel.AdoptTechnology` (`Board.cs:377`) returns immediately when
`ReferenceEquals(board.Technology, technology)`. If the live seam hands out the tech editor's
`Working` instance and that instance is MUTATED in place by subsequent edits, the second edit is
silently dropped: the reference is unchanged, so adoption short-circuits before
`SyncHiddenLayers` runs.

Either the seam hands out a fresh clone per edit, or `AdoptTechnology` stops using reference
identity as its change test. **Pick one and write down which**, because getting it wrong produces
"the first toggle works and the rest do not", which is much harder to diagnose than "none of them
work".

### `R-rail20-2c` — the save path is unchanged

`TechSaved` -> `Invalidate` -> `OnTechnologyChanged` stays exactly as it is. The live seam is
additive. A brief that replaced the save path with the live one would make an unsaved edit
indistinguishable from a saved one, and the `.ctech`'s dirty state is what tells a user their
change is not on disk yet.

---

## 3. Gate

`tests/Ui.Tests/RailRf/RailLayerVisibilityTests.cs` and
`tests/Ui.Tests/Layout/TechnologyLiveDisplayEditTests.cs`.

1. **The list exists and is seeded.** Loading the shipped example builds one row per drawing layer
   of `pcb-4layer-1p6mm`, each reading the technology's own `Visible`.
2. **Hiding a layer hides the copper AND its map shading.** Both, in one assertion — R-rail20-1c's
   own rule, which is the defect its comment records.
3. **Hiding a layer does not clear the result.** Run, hide, assert the DC answer is the same object
   (R-rail20-1d).
4. **It round-trips in the `.crail`.** Hide two layers, save, reload, assert two hidden
   (R-rail20-1b).
5. **It is NOT written to the `.ctech`.** Byte-compare the technology file before and after
   (R-rail20-1b).
6. **Follow the technology resets.** R-rail20-1e.
7. **An unsaved visibility edit reaches railRF.** Open the tech editor, untick `Vis`, do **not**
   save, and assert railRF's board stops drawing that layer. **Fails at HEAD** — this is the
   designer's report as a test.
8. **And the SECOND one does too.** Untick a second layer without saving; assert both are hidden.
   This is R-rail20-2b's trap, and a test that toggles only once would pass with the bug present.
9. **A stackup edit does NOT arrive live.** Change a copper thickness without saving and assert no
   open window's numbers moved (R-rail20-2a).
10. **The save path still works.** Save, and assert the ordinary invalidate path still fires
    (R-rail20-2c).

## 4. Scope

- **No layer reordering, no colour editing in railRF.** Visibility only. Colour is a `.ctech`
  property and the editor for it exists.
- **No change to the stackup seam.** R-rail20-2a.
- **The layout editor gets the live seam too**, because it is a workspace-level event and excluding
  it would mean writing a filter to exclude it. But the layout editor's own layer panel is out of
  scope here.
