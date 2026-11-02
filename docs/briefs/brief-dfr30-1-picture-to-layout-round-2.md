# Brief DFR30-1 — Create Layout from Image, round 2: via sizes, corrections you can edit, clean-up by kind

**Series:** designer feedback round 30 (2026-10-28). · **Tag:** `R-dfr30-1-<n>`
**Area:** `src/Ui/Recognition/ImageSourceViewModel.Corrections.cs`, `ImageSourceViewModel.cs`, `ImageOverlayCanvas.cs`,
`ImageReadingPanel.axaml(.cs)`, `ImageSourcePane.axaml`; `src/Design/Layout/Recognition/Image/ImageTrace.cs`,
`ImageTraceCorrections.cs`; `src/Cli/PictureInput.cs`; `docs/user/src/reference/create-from-image.md`.
Findings go in `src/Ui/RESOLVED.md` and `src/Design/Layout/Recognition/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Imaging/ImageTraceGeometryTests.cs`, `tests/Ui.Tests/Recognition/ImageSourceViewModelTests.cs`,
`ImageConvertCliVerbTests.cs` — extend the DFR29-1 tests, one test per claim. Render each dialog change with
`tools/UiSnapshot --image`. Builds on `brief-dfr29-1-picture-to-layout-by-hand.md`.

---

## 0. Why

The designer ran 1.5.7's Create Layout from Image on a datasheet test-board drawing, once from a coloured picture
(green copper, white holes) and once from a black-and-white line drawing of the same board, and concluded it still
needs rework. In his words, paraphrased:

1. A coloured picture reads better than a black-and-white one.
2. The copper match looked worse than the day before.
3. The drills read poorly.
4. The proposed copper shapes cannot be edited.
5. Placing vias by hand works, but every via gets one size although the board has several.
6. Drawing the outline works, but the result is hard to see afterwards.
7. When the automatic reading is messy — the vias, say — there is no way to clear one kind of thing at once.
8. A copper fill he drew cannot be deleted or edited.

What the code does today (read before writing this brief):

- **5 is one line.** `PlaceVia` adds `ImageCorrection.Via(x, y)` with no drill. `ImageTrace.Build` takes the drill
  from the hole under the point when there is one, else the technology's `DefaultViaDrillDbu`. On a picture whose holes
  are not read as holes (a line drawing), every hand-placed via is the default size. `DrillPx` exists on the record and
  `Build` honours it; nothing in the dialog sets it.
- **8, and most of 4: a correction is not an object.** Corrections are an `ObservableCollection<ImageCorrection>` with
  undo (`Correct`, `_correctionUndo`) and *Clear corrections*, but nothing lists them, selects one, deletes one or
  moves a vertex. Undo is the only way back, and only in order.
- **A traced region can be chosen**, as *Copper on ▾ layer* or *Not copper* (`RegionIsCopper`, `RegionIsNotCopper`),
  but its shape cannot be changed.
- **1 is expected from how copper is read.** The trace reads copper as an area of the copper colour. A line drawing
  draws copper as its OUTLINE, with no fill, so there is little or no copper area to read. Nothing says so.
- **2: both of the day's picture changes shipped in 1.5.7** (`15e810e9e` round holes in copper become vias with the
  copper made whole; `c8d8b500e` DFR29-1's corrections). 1.5.6 is `2bdeefe93`. Nothing has measured whether either
  changed what a coloured picture reads.

## 1. What to do

### R-dfr30-1-1 — First find out what got worse

Before anything else: trace the designer's coloured picture with 1.5.6 (`2bdeefe93`) and with the current tree, through
the CLI (`convert` with a picture input), and compare per layer the copper area, the number of copper regions, the
number of holes and the number of vias. The two pictures are **not in the repo** — ask the owner for them. Never commit
them, name them or quote numbers measured on them in code, tests or RESOLVED (state the finding, not the file).

- If 1.5.7 reads less copper, more fragments or fewer holes, find the commit (`15e810e9e` first: making the copper
  whole under a via changes the copper area) and fix it, with a synthetic test of the same shape: copper with round
  holes drawn with a dark rim, as datasheet drawings draw them.
- If the two read the same, say so in the report back and move on. The complaint may then be the overlay (item 6), not
  the reading.

### R-dfr30-1-2 — A via has a size you choose

The Via tool gets a **Drill** choice beside it in the tool strip, a picker (never a text box alone):

- **From the hole** — the default; the hole under the click, as today.
- **The sizes read in this picture** — every distinct drill the trace read, rounded as `ImageTrace.Tidy` rounds it,
  each with its count (*0.30 mm · 24*, *0.60 mm · 6*).
- **The technology's via** — `DefaultViaDrillDbu`, named as such.
- **Other…** — a typed size, with a unit, rounded the same way.

The choice is kept for the following clicks and written as `DrillPx` (converted at the picture's scale). A click on a
hole under *From the hole* still takes that hole's size. A via already placed by hand can be selected (R-dfr30-1-3) and
given another size; *every one like it* works on hand vias as it does on holes. The pad stays as `Build` makes it
(the ring around the hole, else the technology's pad, else twice the drill). CLI: `--via x,y[,drill]`, the drill with a
unit, refused without one.

### R-dfr30-1-3 — A correction is an object: listed, selected, deleted, edited

- **A Corrections list** in the reading panel, one row per correction: its kind, its layer for a copper fill, its drill
  for a via, and a delete button. A click on a row selects the correction on the canvas, and a click on the canvas
  selects the row.
- **Delete** (the key, and the row's button) removes the selected correction. One undo step.
- **A drawn polygon is editable**: a selected copper fill, clear or outline shows its corners as handles. Dragging one
  moves it; a double-click on an edge adds a corner; Delete on a selected corner removes it (refused below three). Each
  drag is one undo step. A copper fill's layer can be changed from its row.
- **A hand via can be dragged** to a new place.

The record stays what DFR29-1 made it: editing replaces a correction in the list, so the CLI and the dialog still
build the same records and write the same bytes.

### R-dfr30-1-4 — Clear one kind at once

A **Remove ▾** menu in the tool strip, each item one undo step and each saying how many it removes:

- **Vias read from the picture** — a `NotVia` for every automatic via, so the holes stay openings.
- **Vias placed by hand**.
- **Copper fills drawn** · **Clears drawn** · **The outline drawn**.
- **Copper read on ▾ layer** — a `Clear` covering the board on that layer, so only drawn fills remain there.

A kind with nothing to remove is shown disabled, never hidden.

### R-dfr30-1-5 — Changing a traced shape (D1)

**Recommended (a):** a traced region cannot be reshaped in the dialog. It is marked *Not copper* and replaced with a
drawn copper fill, which R-dfr30-1-3 makes editable, or it is reshaped after Create in the layout editor. That editor
already edits polygons, and *Keep the source picture under the result* puts the picture under it. Make that route obvious: when
a traced region is selected, the picker says so in one line next to *Not copper*.

(b) Reshape traced regions in the dialog (vertex handles on the trace's own polygons, kept as a correction replacing
the region). This is a second polygon editor inside the dialog, and the region it edits is re-read whenever a colour or
the scale changes, so the edit has to be matched to the new reading. Take it only if the owner asks.

### R-dfr30-1-6 — The outline can be seen

The board outline, whether drawn, from *Board corners… ▸ Use as the board outline*, or read from the picture, is drawn
above copper as a 2 px line in the dialog's accent colour with a halo (as copper outlines have one, `ImageOverlayCanvas`
line ~301). It stays after the tool is put down. The picture outside the outline is dimmed. The outline's row in the
Corrections list selects it.

### R-dfr30-1-7 — A line drawing is said to be one (D2)

**Recommended (a):** when a layout picture reads as line art — most of the copper colour is in thin closed outlines,
and the copper area read is a small share of the area those outlines enclose — the reading panel says so once:
*"This picture draws copper as outlines, so little copper is read from it. A coloured or filled picture of the same
board reads better."* No change to the reading.

(b) Fill closed outlines as copper. This is recognizer work: a drawing's outlines also enclose holes, part bodies,
silkscreen and text, so it needs a rule for which closed outlines are copper. **This repo's rule is that no
recognizer work starts unasked.** List it as a follow-up; do not build it in this brief.

### R-dfr30-1-8 — Drills on a coloured picture

On a synthetic coloured board with three drill sizes (0.3, 0.6 and 1.0 mm, each hole white with a dark rim, ten of
each), measure how many holes `ImageTrace` finds and at what diameter. If any size is missed or read more than one
rounding step off, fix the hole finder for a dark-rimmed hole. **Stop rule:** if the fix needs more than the hole
finder (new classification, a learned model, a change to how copper is read), stop and report what was measured —
do not start it.

## 2. Gates

1. R-dfr30-1-1: the comparison done and reported back (numbers to the owner in chat, not in the repo), and a synthetic
   regression test if a regression was found.
2. Three hand vias placed with *0.30 mm*, *0.60 mm* and *Other… 1.0 mm* write three vias of those drills.
   `--via x,y,0.6mm` writes the same bytes as the dialog.
3. A copper fill drawn, then one corner dragged, then deleted from its row: the trace written after each step has that
   fill, the moved fill, no fill. Undo restores each.
4. *Remove ▾ Vias read from the picture* on a picture with 12 automatic vias writes none and leaves 12 openings.
   One undo restores them.
5. *Copper read on ▾ Top* plus one drawn fill: Top copper is exactly the fill.
6. The line-art note appears on a synthetic outline-only board and not on a filled one.
7. R-dfr30-1-8: the drill count and sizes, before and after (or the stop-rule report).
8. Dialog renders (`tools/UiSnapshot --image`): the Drill picker, the Corrections list with a selected fill's handles,
   the outline after the tool is put down. At most three pictures.

## 3. Not in this brief

- Filling a line drawing's outlines as copper (R-dfr30-1-7 b).
- Reshaping traced copper in the dialog (R-dfr30-1-5 b), unless the owner picks it.
- Placing footprints from a picture (DFR29-1 §3 still stands).
- Stackup editing in the dialog — withdrawn by the owner in DFR29-1 and not reopened.
