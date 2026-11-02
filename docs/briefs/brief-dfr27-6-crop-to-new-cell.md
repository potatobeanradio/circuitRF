# Brief DFR27-6 — Crop to New Cell, and the ground fill that does not reach the grouped cell

**Series:** designer feedback round 27 (2026-10-25), follow-up to DFR26-2. · **Tag:** `R-dfr27-6-<n>`
**Area:** `src/Design/Layout/LayoutCrop.cs`, `src/Ui/Layout/LayoutEditorViewModel.Crop.cs`,
`src/Ui/Layout/LayoutEditorViewModel.Group.cs` (`CommitGroupIntoCell`, `GroupIntoCellLeftOutText`),
`LayoutEditorViewModel.SelectableShapeIndices`, `LayoutGroup.BuildContents`, the layout context menu
(`src/Ui/Controls/LayoutCanvas.cs`). Findings go in `src/Ui/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Layout/CropBoardRound26Tests.cs` (extend it).

---

## 0. What the designer does, and what goes missing

To study one corner of an imported ten-layer board he runs **Crop to Rectangle**, then **selects everything and
runs Group into Cell**, so the corner becomes a cell he can set up for EM. Two reports:

1. With some layers hidden, the new cell lacks those layers. Expected: Crop cuts every layer, but a selection
   (Select All, marquee, click) never reaches a hidden or locked layer, and Group into Cell takes only what is
   selected. The confirmation says how many shapes it leaves out, but that is easy to miss.
2. **With every layer shown, a ground fill still does not reach the new cell.** Resolved from his before/after
   workspaces (round-27 feedback folder, `BG24_before` / `BG24_after`, an eight-copper board): **the board in the
   "after" workspace was never cropped.** Its shapes still span the whole board (−2.4…32.8 × −2.4…53.4 mm), and
   2,339 shapes before less the 175 grouped is exactly the 2,164 left. The new cell holds 122 polygons and 53 vias
   — a marquee's worth — and per layer, inside the cell's box:

   | layer | before | in the cell | left on the board |
   |---|---|---|---|
   | Top Copper | 55.9 mm² | 10.2 | 45.8 |
   | Inner 1 / 3 / 4 / 5 / 6 | 78.3 / 67.1 / 77.2 / 72.3 / 59.8 | 0 | all of it |
   | Bottom Copper | 77.9 | 0.6 | 77.3 |

   Nothing was lost: every square millimetre is either in the cell or still on the board. The planes are
   board-sized polygons, so a marquee inside the board never encloses them (`GroupIntoCellLeftOutText`'s
   "reaching past" case), and they stayed behind. Whether the crop was cancelled at its confirmation, undone, or
   not run on this attempt, the two-step workflow let it happen without anything looking wrong.

## 1. What to do

### R-dfr27-6-1 — Say it when grouping leaves a plane behind

Report 2 is the reaching-past case, not a defect in Crop or Group into Cell (§0). The confirmation already counts
what it leaves out; make the case that matters impossible to miss: when a left-out shape is a plane (its box covers
the whole selection) on a copper layer, the confirmation's FIRST line names the layers whose plane stays behind,
before the counts.

### R-dfr27-6-2 — Crop to New Cell…

A second context-menu entry beside Crop to Rectangle: drag the box, name the cell, and a cropped COPY of the
board — every layer, hidden and locked included, the outline rebuilt, vias and instances by their origin, as Crop
does — is written as a new cell beside this one. **The board itself is not changed**, and nothing is added to it
(no instance is placed), so the command is one step and cannot lose anything. It reuses `LayoutCrop.Crop` for the
geometry and Group into Cell's cell-writing path (`CellFolder.CreateCellFolder`, `TechRefFrom`, `DbuPerMicron`,
the `.ccell` primary layout). The same confirmation as Crop, with the cell name in it. The new cell opens.

Gate: on a fixture with shown, hidden and locked layers and a fill spanning the box, the new cell's per-layer areas
equal Crop's, the source document is byte-identical before and after, and its `TechRef` resolves.

### R-dfr27-6-3 — Group into Cell offers to take what it would leave behind

When `GroupIntoCellLeftOutText` is non-null, the confirmation gets an **Include them** button beside OK: it adds
the shapes on hidden or locked layers and those reaching past the selection that overlap its box, then groups. The
text names Crop to New Cell as the way to take a region whole.

### R-dfr27-6-4 — Docs

`docs/user/src/reference/layout-editor.md` §Crop to Rectangle: add Crop to New Cell, and say in one line that a
selection never reaches a hidden or locked layer, so Group into Cell after a crop needs every layer shown and
selectable. Regenerate that page with `DocGen --page`.
