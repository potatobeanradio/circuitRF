# Brief — an inline, interactive stackup rendering in the Technology Editor: the series

**Status:** SHIPPED, briefs 1-8 · **Date:** 2026-09-13 · **Area:** `src/Render`, `src/Ui/Layout`,
`src/Ui/Controls`, `src/Ui/Views/Layout`, `src/Design/Layout`
**Requirement tag for the series:** `R-stk-n`, scoped per brief (`R-stk1-3` is brief 1's third)

---

## 0. The short answer

The Technology Editor's Stackup tab is a **list of cards**. A stackup is a **sandwich**, and the one
thing a list cannot say is what a sandwich looks like: which metal is on top, how thick the substrate
is against the copper on it, which two conductors a via actually joins. The user docs already draw
that picture — `stackup-mmic`, `stackup-mim`, `mom-bend-stackup`, all three from
`DocStackupFixtures` — and it is the clearest artefact this application produces about a technology.
It exists only in the documentation, and it cannot be clicked.

This series puts that picture **in the tab, above the cards**, and makes it the primary way to
navigate and edit a stackup: click a band to select it and land on its fields, double-click a value
to edit it in place, drag a layer to reorder it, drag a via to change its span or slide it sideways,
right-click for delete / add-via / plated-solid, and copy the whole drawing to the clipboard.

### The one rule the whole series is built on

> **The rendering is a second VIEW of the stackup. It is never a second MODEL of it.**

Every mutation the canvas performs goes through the function the card already calls —
`StackupLayerRowViewModel.CommitName()`, `CommitThickness()`, `TechEditorViewModel.MoveStackupLayer`,
`RemoveStackupLayer`, `CommitEdit(before, description)`. Nothing in the canvas writes
`Working.Stackup.Layers` directly, and nothing in it pushes an undo entry of its own.

That is not tidiness. `TechEditorViewModel` already owns coarse-grained whole-`Technology` snapshot
undo, `TechValidation` re-runs after every committed edit, and `TechLiveChanged` pushes the
in-progress technology into `TechnologyCache` so every open layout repaints. **Route the canvas
through `CommitEdit` and undo/redo, live validation, the dirty mark, the live-tech push and the card
list all follow for free.** Write a second path and every one of those is a separate bug, each of
which fails silently.

The same rule the CLI's `Authoring.cs` states for the GUI's own create functions, and it is held the
same way: a comment-stripped source scan (`R-stk6-7`).

---

## 1. What the owner asked for, and where each piece lands

| # | Asked for | Brief |
|---|---|---|
| 1 | A stackup drawing in the Stackup tab, above the card list, looking like the user-doc figures | 1, 2 |
| 2 | Relative z-heights **within** a kind (a half-thickness conductor draws half as tall), never across kinds | 1 |
| 3 | Via wall thickness relative to other vias; plated vs solid visibly different | 1 |
| 4 | Layer colours from the `.ctech`; dielectrics stay grey | 1 |
| 5 | Text padded, never overlapping | 1 |
| 6 | Card list shortened; drawing-layer picker 3 wide × 4 tall; filter box moves below the drawing | 2 |
| 7 | The drawing scrolls when the stack is tall | 2 |
| 8 | Live update as the user edits, from either surface | 2 |
| 9 | Click an element → highlight outline, card list scrolls to it, card background shaded | 3 |
| 10 | `Esc` clears the selection and all highlighting | 3 |
| 11 | Double-click a value → inline text editor, reusing the schematic editor's | 4 (typed values; closed choices are 14b) |
| 12 | Drag a conductor/dielectric up or down to reorder | 5 |
| 13 | Drag a via to change its span; grippers at each end; slide it sideways (cosmetic, persisted) | 5 |
| 14 | Right-click: Delete Conductor / Delete Via / …, Add Via on a dielectric, plated↔solid and fill | 6 |
| 14b | *“Edit **all** conductor, dielectric and via parameters from the rendering”* — the closed-choice ones (ground reference, sheet surface, patterned-with, drawing layers) that are not typed values | 6 (`R-stk6-8`) |
| 15 | Undo/redo for all of it | 5, 6 (and by construction from brief 1's rule) |
| 16 | Right-click anywhere → Copy the rendering to the clipboard | 7 |
| 17 | Docs and figures follow | 8 |

---

## 2. The briefs

| Brief | What it delivers | Gate |
|---|---|---|
| [1 — the scene](brief-stackup-render-1-scene.md) | `StackupScene` + `StackupRenderer` + `StackupRenderTheme` in `src/Render`. One layout pass, two readers: drawing and hit-testing read the same rects. Headless. No UI at all. | `tests/Ui.Tests/Stackup/StackupSceneTests.cs` |
| [2 — the canvas, and the tab re-layout](brief-stackup-render-2-canvas-and-tab.md) | `StackupCanvas` (`ICustomDrawOperation`), the splitter, the shortened card list, the 3×4 drawing-layer picker, the moved filter row, live repaint | `StackupCanvasTests.cs`, `TechEditorStackupTabLayoutTests.cs` |
| [3 — selection](brief-stackup-render-3-selection.md) | Selection on the VM, outline on the canvas, shaded card, `ScrollIntoView`, `Esc` | `StackupSelectionTests.cs` |
| [4 — inline editing](brief-stackup-render-4-inline-edit.md) | `SchematicInlineEditBox` over the canvas; every commit through the row VM | `StackupInlineEditTests.cs` |
| [5 — drag](brief-stackup-render-5-drag.md) | Reorder drag, via span drag, end grippers, the lateral lane and its `.ctech` field | `StackupDragTests.cs`, `TechPersistenceTests.cs` (extended) |
| [6 — context menus](brief-stackup-render-6-context-menu.md) | Delete, Add Via, Plated hole / Fill, and the one-write-path source scan | `StackupContextMenuTests.cs` |
| [7 — copy](brief-stackup-render-7-copy.md) | `StackupGraphicExport` on `PlotExporter.SetClipboardDataAsync` | `StackupCopyTests.cs` |
| [8 — docs, figures, closeout](brief-stackup-render-8-docs-and-figures.md) | `layout-view.md` §10.4, `stackup.md`, the `tech-editor-stackup` figure, and the doc-figure repoint decision | DocGen, reported |

Briefs 3–7 each depend on 2, and 2 depends on 1. **3, 4, 5, 6 and 7 are otherwise independent of each
other** and can be taken in any order once 2 has landed.

---

## 3. Five things that are not obvious, resolved here once

These came out of reading the code before writing the briefs. Each is restated where it bites.

### 3a. "Half as thick draws half as tall" is not always satisfiable, and the conflict is real

The shipped MMIC technology has dielectrics spanning **0.2 µm (the MIM film) to 100 µm (the GaAs
substrate)** — a 500:1 ratio **within one kind**. Drawn proportionally, either the film is a hairline
or the substrate is four screens tall.

So the rule is: **proportional while the kind's own dynamic range fits the band budget, monotone and
compressed when it does not, and the real number is always printed on every band.** Brief 1 §3 states
the function and the tests that hold it. This is the one place the series knowingly does not do exactly
what was asked, and it is because what was asked is unsatisfiable on a technology this application
ships.

**The picture says nothing about any of this** (owner, 2026-09-13). No note, no asterisk, no "not to
scale" caption — the rendering stays as clean as possible, and the compression is scene metadata the
tests read rather than ink on the drawing (`R-stk1-4`). The height rule is explained once, in the
chapter (`R-stk8-3`), which is where a qualification belongs when the alternative is a line of chrome
on every stackup anyone ever looks at.

### 3b. "Plated" already means two different things in this model, and both are in the menu

`StackupLayer` carries **two** independent fields, and the collision is in the model already:

- `Plated` (`bool?`) — *is this hole metal at all.* `false` is a mounting hole, a routed cutout, an
  unplated castellation. Null means plated, so every technology written before the field reads
  identically. `PlanarExtractor.BuildViaBinding` turns every via entry into a conductive `PlanarVia`,
  so a millimetre-scale unplated hole modelled as a barrel **shorts every layer it passes through and
  the run completes cleanly**.
- `Fill` (`ViaFillKind` = `Plated` | `Solid`) — *the fill model.* Both values are conductive:
  `Plated` is a hollow barrel with a wall thickness, `Solid` is filled.

The owner's "change via from Plated to solid and whether its Fill or not" maps onto both. The menu
therefore carries both, spelled as the existing card spells them, and never merges them
(brief 6 `R-stk6-4`). Merging them is expressible and wrong.

### 3c. `Esc` has two jobs and they must not fight

`Esc` clears the selection (item 10) — and `Esc` also reverts an open inline editor, which is the
three-key contract every inline editor in this application honours. **The editor wins while it is
open**: one `Esc` reverts the edit and leaves the selection standing, a second clears the selection.
Brief 4 `R-stk4-6`.

### 3d. Selecting on the canvas can select a card the filter is hiding

"Filter by stackup layer name" filters the card list. Clicking a band the filter excludes would scroll
to nothing, silently. **The canvas selection clears the filter when the filter would hide it**
(brief 3 `R-stk3-5`) — the whole point of the click is to land on the fields, and a click that
appears to do nothing is the worse outcome.

### 3e. The lateral via position is cosmetic and must stay cosmetic

Item 13 persists a purely visual x. That field is one careless read away from becoming geometry.
`R-stk5-9` gates it: **a technology differing only in the lane field extracts to an identical
`EmProblem`.** Not a comment — a test.

---

## 4. What this series does NOT do

- **No zoom on the stackup canvas.** It fits the pane's width and scrolls vertically. A stackup has
  one meaningful axis and a handful of bands; a viewport with pan and zoom is the layout editor's
  problem, not this one's.
- **No editing of the Layers tab, the DRC tab or the Interchange tab.** Only the Stackup tab moves.
- **No new document, no new file format.** One additive nullable field on `StackupLayer`
  (brief 5), on the `SheetAt` / `PresentWithLayer` / `Fill` precedent: no `.ctech`
  `FormatVersion` bump, ignored on a non-via entry, `WhenWritingNull` so no existing file changes by
  one byte.
- **No second renderer.** Brief 1 puts the drawing in `src/Render` precisely so the tab, the
  clipboard export and (brief 8's decision) the user-doc figures are one picture rather than three
  that must be kept looking alike by hand.

---

## 5. Build and test

Every brief in this series is reachable from `tests/Ui.Tests` and `tests/Firewall.Tests` alone.

```
dotnet test tests/Ui.Tests      --no-build --filter "FullyQualifiedName~Stackup"
dotnet test tests/Firewall.Tests --no-build
```

Two commands, never one — this SDK's `dotnet test` rejects two project paths in one invocation.
**Do not run the full solution suite for this work**: nothing here can reach `src/Core`,
`src/Engine` or `RfCore`, and the root run is minutes of wall clock whose likely failures are known
flakes. Read `tests/Ui.Tests/TestResults/last-run.trx` for failures rather than re-running.

**On completion of each brief:** record findings in the RESOLVED.md beside the code
(`src/Render/RESOLVED.md` for brief 1, `src/Ui/RESOLVED.md` for 2–7, `src/Design/RESOLVED.md` for
brief 5's model field). **Never write findings into a CLAUDE.md.**
