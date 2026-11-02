# Brief DFR26-2 — Crop a board to a region in one step, and Group into Cell that keeps what it was given

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-2-<n>`
**Area:** `src/Ui/Layout/LayoutEditorViewModel.Booleans.cs` (Clip / Cut Out and their All variants),
`src/Design/Layout/LayoutBooleans.cs`, `src/Ui/Layout/LayoutEditorViewModel.Group.cs` (Group into Cell, `TechRef` ~107),
`src/Ui/Controls/LayoutCanvas.cs` (context menu ~1474). Findings go in `src/Ui/RESOLVED.md`.
**Tests:** new `tests/Ui.Tests/Layout/CropBoardRound26Tests.cs`, plus the existing clip and group classes.

---

## 0. The gap

To study one RF path, the designer needed a 12 mm × 25 mm corner of a ten-layer board with everything else removed,
on every layer, with a clean edge. It took them **50 minutes**: a rectangle, then Cut Out ("difference") layer by
layer, then hand repairs, and they did it twice. Drill, mask and paste layers went fine; the copper layers gave
trouble. Their Messages show why the existing commands did not do it in one go:

- `Cut Out All: 344 shapes on hidden or non-selectable layers left untouched.` Clip All / Cut Out All deliberately take
  only shapes on visible, selectable layers (the 2026-09-15 rule). For a crop of a whole board that rule is the
  obstacle: the designer had to work layer by layer to avoid touching a layer by mistake.
- `Cut Out All: 4 instances left untouched — Cut Out applies to shape geometry.`
- After Group into Cell, the new cell reported `Technology file not found: …\<workspace>\<board>.ctech` on every open.
  **Cause, read from the code:** `LayoutEditorViewModel.Group.cs` copies the parent's `TechRef` verbatim (~107). The
  parent's reference is relative to the parent's own folder (`../../<board>.ctech`); the new cell lives at a different
  depth, so the same string points somewhere else.
- The cell made by Group into Cell **did not contain the ground fill areas**.

## 1. Reproduction

The owner has the designer's Gerber set (ask; do not commit or name it). Import it onto the DFR26-3 ten-layer
technology, then try to keep only the region around one connector and its feed. Record, per copper layer, what
Clip All with a rectangle stencil does to the large plane polygons, and what the "trouble" on copper was (holes lost?
a plane drawn as many strokes rather than one region? a shape kind that is not a clip operand?). Write it down in
RESOLVED before fixing anything.

## 2. What to do

### R-dfr26-2-1 — Crop to Rectangle

A new layout command, **Crop to Rectangle**: drag a rectangle; every shape on **every** layer (shown, hidden, locked)
is clipped to it, in one undo entry.

- Before applying, one confirmation lists the count per layer, including hidden and non-selectable ones, so nothing is
  touched in silence. That is what replaces the visible-layer gate here: the command is explicitly "the whole board".
- Point-anchored shapes (vias, drills, labels) are kept when their anchor is inside, as Clip already does.
- Instances: kept when their origin is inside, removed otherwise; the count of each goes in the message.
- The board outline becomes the rectangle intersected with the old outline, so the 3D air box and the outline match.
- Planes with holes come out with their holes; a plane made of many strokes comes out as the clipped region (whatever
  §1 finds the copper trouble to be, it is fixed here and has a test).
- Time it on the designer's board (Release): it should be seconds, not minutes. Report the number.

Clip All and Cut Out All keep their current visible-layer rule.

### R-dfr26-2-2 — Group into Cell writes a technology reference that resolves from the new cell

Compute the new cell's `TechRef` from the parent's resolved absolute technology path, relative to the new cell's own
folder — the same way any other cross-folder reference is written. Opening the new cell resolves the same `.ctech` as
its parent, with no message.

### R-dfr26-2-3 — Group into Cell takes every shape it was given

Find why the ground fill areas were left out (a shape kind the group operand set excludes, a pour tag, a hidden layer
under the selection) and make Group into Cell take every selected shape of every kind. If some kind is genuinely
excluded on purpose, the command says how many were left and why, as Clip All does.

## 3. Tests

Synthetic layouts, one test per claim:

1. Crop to Rectangle clips shapes on a hidden layer and a locked layer, and is one undo entry.
2. A plane with a hole, half inside the rectangle, keeps the hole's inside part.
3. Vias outside the rectangle are removed, inside are kept; an instance straddling the edge follows its origin.
4. The outline after cropping is the intersection.
5. Group into Cell from a cell two folders deep writes a `TechRef` that resolves to the parent's technology.
6. Group into Cell over a selection containing a ground fill keeps the fill.

Run the new class and the existing `*Clip*` and `*Group*` layout test classes only.

## 4. Text

`docs/user/src/reference/layout-editor.md`: a short section on Crop to Rectangle — what it touches and the
confirmation. `docs/user/src/app-notes/an02-gerber-import.md`: one paragraph, "cutting a region out of an imported
board", pointing at it. The designer also asked for a walkthrough video; that is the owner's to make, not this brief's.
Regenerate the edited pages only (`--page`, no figures).

## 5. Out of scope

- Cropping to an arbitrary polygon (a rectangle covers the request; a polygon stencil can come later).
- Changing Clip All / Cut Out All.
