# Brief BSP-3 — What a bottom-side part does to every output

**Series:** bottom-side placement — read `brief-bsp-0-overview.md` first. · **Tag:** `R-bsp3-<k>`
**Area:** `src/Design/Layout/Interchange/` — `GerberExport.cs`, `GdsiiExport.cs` + `GdsiiWriter.cs` + `GdsiiTransformCodec.cs`,
`DxfExport.cs` + `DxfWriter.cs`, `PcbExport.cs` + `PcbWriter.cs`, `PlacementWriter.cs` (~127), `BomWriter.cs`,
`InterchangeStructure.cs`, `StreamLowering.cs`; `src/Design/Layout/Drc/`; `src/Design/Layout/Em/` (`EmGeometry`,
`PlanarExtractor`); `src/Design/ThreeD/C3dElaborator.cs`; `src/Design/RailRf/`; `src/Cli/Render.cs`, `src/Cli/Convert*.cs`;
`docs/user/src/reference/layout-editor.md`, `docs/design/layout-view.md` §8, `src/Design/RESOLVED.md`
**Tests:** new `tests/Ui.Tests/Layout/BottomSideOutputTests.cs`; the export gates named in §5
**Depends on:** BSP-1 (BSP-2 is not needed: tests author `Side = Bottom` directly).

---

## 1. Goal

A board with parts on both faces leaves circuitRF correct in every format it writes and every analysis it runs. The
flattening exporters mostly get this for free from BSP-1; the HIERARCHICAL ones do not, because a structure/block/
footprint written once is shared by every placement of that cell, and a bottom placement's layers are not its cell's.

## 2. Requirements

### Flat outputs

**R-bsp3-1 — Gerber.** `GerberExport` flattens the whole design (`LayoutDesignFlatten`), so bottom parts land in the
bottom copper / mask / paste / silk files with no change. Verify it, and that a bottom designator's strokes are written as drawn
(BSP-1 R-bsp1-9): in the Silk Bottom file the text reads left to right when the file is viewed from above. That is the
owner's choice (D4) and the board house prints what the file holds. Omitted-role notes (R-bsp1-10) go to the export report.

**R-bsp3-2 — Placement file.** `PlacementWriter` writes its `Side` column from `row.Mirror` today (~127), which is wrong
both ways: a mirrored part on the top is reported on the bottom, and after this series a bottom part may have any
`MirrorX`. Write it from `LayoutInstance.Side` (composed through the hierarchy, D5). For a bottom part, write the
rotation the convention for bottom parts uses — angle as seen from the TOP, part flipped about Y — and say in a
comment which convention it is; a reader on the other end that disagrees rotates every bottom part by the wrong sense.
`PlacementColumns`' reader (the import side) already reads a side column; round-trip one file through both (test).

**R-bsp3-3 — BOM.** If `BomWriter` has a side or layer column, fill it from `Side`; if it has none, add none.

### Hierarchical outputs

**R-bsp3-4 — GDSII and DXF: a bottom variant structure.** `CollectHierarchy` writes one structure (GDSII) / block (DXF)
per cell. A cell with ANY bottom placement in the design also gets a variant — the BSP-1 bottom view, layers swapped,
labels placed per R-bsp1-9 — named `<structure>_B` (made unique by the existing structure-naming code, `GdsiiStructureNaming`), and
each bottom placement references the variant with its own reflection/rotation exactly as today. Never write the variant
when no bottom placement uses it. Nested sides (D5) resolve per placement, so a variant may itself reference variants.
Import of our own export (GDSII → `.clay`) brings the variant back as a cell of its own: that is a format limit, said in
the export report ("bottom-side placements were written as N variant structures; reading this file back gives them as
separate cells"), not fixed.

**R-bsp3-5 — `.kicad_pcb` export.** A bottom placement is written as the format writes a back-side footprint: the
footprint on the back copper layer, its child items on the back layers and in the format's flipped local frame. Text
is written so it reads as circuitRF draws it (D4) — left to right from the top — which in that format means a back-side
text item WITHOUT its mirror flag, placed where it is drawn. This is the inverse of what `PcbReader.ReadFootprint` already undoes on import (its comments
~743-770 describe the format's back-side text and flip) — read those first and make export + import of a two-sided board
round-trip (test: positions, sides and layers of every land equal after export → import).

### Analyses

**R-bsp3-6 — DRC.** Rules evaluated per layer need nothing. A courtyard/assembly overlap rule, if one exists in
`src/Design/Layout/Drc`, must compare parts on the SAME face only — two parts directly opposite each other on the two
faces are not a clash. Find out what exists before writing any; if there is no courtyard rule, add nothing.

**R-bsp3-7 — EM and impedance.** A bottom part's lands are Bottom Copper shapes after flattening, so `PlanarExtractor`
meshes them on the bottom conductor. Verify on a two-layer microstrip with a series 0402 on the bottom face: the
extracted `EmProblem` places the land conductor on the bottom level and a port on its pin references the bottom copper.
No solve is run in the test — assert on the extracted problem (memory rule: keep EM runs short; probe the engine).

**R-bsp3-8 — 3D view and railRF.** `C3dElaborator` builds a board's 3D view from the flattened layout: check bottom parts
appear under the board, not on top. railRF reads parts and their pads (`RailArtwork`, `RailRfViewModel.PadRead/TurnedParts`);
it already reasons about a mounting side (R-rail37) — make it take a placed part's side from `Side` where it currently
infers it, and record what changed in `src/Design/RESOLVED.md`. If either needs more than a small change, stop and
report rather than widen this brief.

**R-bsp3-9 — LVS.** BSP-1 already makes `LayoutRead` see bottom pins on Bottom Copper; a two-sided board whose bottom part
is wired by a via must LVS clean (test).

### CLI

**R-bsp3-10 — `render` and `check`.** `render --layers "Bottom Copper,Silk Bottom"` of a two-sided board shows the bottom
parts; `check` reports a bottom placement whose technology lacks a bottom layer for one of its roles as a WARNING naming
the instance and the role, and a bottom placement on a technology with no bottom copper as an ERROR. `explain` needs nothing.

## 3. Out of scope

Importing (BSP-4). No view from below (owner).

## 4. Tests (one per claim)

`BottomSideOutputTests`: Gerber bottom files carry the bottom part's lands and designator strokes reading left to right (R-bsp3-1);
placement side from `Side`, not `MirrorX`, and the round trip through `PlacementColumns` (R-bsp3-2); a GDSII export writes
one variant structure only when a bottom placement exists, with swapped layers (R-bsp3-4); `.kicad_pcb` export → import
round trip of a two-sided board (R-bsp3-5); opposite-face parts are not a courtyard clash, if the rule exists (R-bsp3-6);
the extracted EM problem's bottom-land conductor level (R-bsp3-7); LVS clean (R-bsp3-9); `check` warning (R-bsp3-10).
Then the existing Gerber/GDSII/DXF/PCB export gate classes by name (`ConvertCliVerbTests` included, since it compares
bytes against the in-process exporters).
