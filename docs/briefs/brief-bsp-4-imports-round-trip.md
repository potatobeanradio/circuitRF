# Brief BSP-4 — Imports and the schematic round trip keep the side

**Series:** bottom-side placement — read `brief-bsp-0-overview.md` first. · **Tag:** `R-bsp4-<k>`
**Area:** `src/Design/Layout/Interchange/PcbImport.cs` (~225-250), `PcbReader.cs` (`ReadFootprint`, back-side text ~743-770),
`PcbBoard.cs` (`ContentKey`); `src/Design/Layout/SchematicLayoutOrientation.cs`, `src/Ui/Layout/SchematicToLayoutGenerator.cs`
(~283-293 and the update path), `src/Ui/Layout/LayoutToSchematicGenerator.cs`; the board-recognition placement path
(`src/Design/Layout/Recognition/`, artwork → schematic) where it reads a part's face; `docs/user/src/reference/layout-editor.md`,
`src/Design/RESOLVED.md`
**Tests:** new `tests/Ui.Tests/Layout/BottomSideRoundTripTests.cs`; the PCB import and Update Layout classes named in §5
**Depends on:** BSP-1 and BSP-3 (R-bsp3-5's export is what the import round-trips against).

---

## 1. Goal

A board brought in from a file, or laid out from a schematic, keeps every part on the face it was on, through every
later update, with ONE footprint cell per footprint whichever face it is used on.

## 2. Requirements

### The `.kicad_pcb` import

**R-bsp4-1 — One cell per footprint, `Side` on the placement.** Today a back-side footprint is read as its own cell with
its child geometry already flipped and on the back layers, placed with `MirrorX = false` (`PcbImport.cs` ~237; the
comment there says this was measured, not assumed). With BSP-1 the importer instead un-flips a back footprint to its
FRONT form — each back surface role mapped to its front layer (`SurfaceRoles`, BSP-1), local geometry reflected back, back text
made to read left to right from above (D4; the importer already does this today, `PcbReader` ~763) — and places it with `Side = Bottom` and the `MirrorX`/rotation that reproduce the original world geometry
exactly. Two placements of one footprint on opposite faces then share one cell (`ContentKey` of the front form).

**R-bsp4-2 — Exactness is the gate.** For every land of every footprint on a two-sided board, the world-space polygon and
layer after import equal what the OLD importer produced, to the DBU (test against a committed, hand-written two-sided
fixture in `testdata/` — no vendor board, no local corpus). The cell count drops by the number of footprints used on both
faces; the report says how many back-side placements were found.

**R-bsp4-3 — What cannot be un-flipped stays as it was.** A back footprint drawing on a back surface role the destination
technology has no front layer for is imported exactly as today — its own baked cell, `Side = Top` — and
the report names it and the layer. Never a half-mapped cell.

### Gerber and placement files

**R-bsp4-4 — Placement side.** Where a Gerber import pairs a placement file with the artwork (`PlacementFile`,
`PlacementColumns` — the side column is already read), a part listed on the bottom is recorded with that side wherever
the import creates a placement for it. If the Gerber path creates no placements (artwork only), there is nothing to do —
check, and say so in `RESOLVED.md`.

### The schematic round trip

**R-bsp4-5 — Flip is not a mirror to the orientation link.** `SchematicLayoutOrientation` links a symbol's rotation and
mirror to its placement's (`OrientationLink`; Update Layout carries the CHANGE either side made). A Flip toggles
`MirrorX`, so without this requirement the next Update Layout reads it as a mirror made on the layout side and mirrors the
schematic symbol — or, linked the other way, un-mirrors the part. The facing compared by the link is the placement's
`MirrorX` XOR (`Side == Bottom`): a flip changes neither side's linked facing, so nothing is carried. Test both
directions: flip in layout → Update Layout leaves the symbol alone; mirror the symbol → Update Layout mirrors the bottom
part on its own face, side unchanged.

**R-bsp4-6 — Update Layout keeps `Side`.** Every path in `SchematicToLayoutGenerator` that replaces an instance's cell
(a value change that regenerates a land pattern, a footprint change, `ChangeComponentType`) carries `Side` across, as it
carries position and the orientation link. A NEW part is placed on the top face, as today.

**R-bsp4-7 — Layout → schematic and recognition.** `LayoutToSchematicGenerator` and the artwork → schematic recognition
treat a bottom part as a part (it is one); check that a bottom-side part's pins are found on Bottom Copper and joined to
the bottom traces, and that nothing there reads `MirrorX` as "bottom" (grep for it).

## 3. Out of scope

A GDSII/DXF import has no notion of a face and gets none.

## 4. Tests (one per claim)

`BottomSideRoundTripTests`: a two-sided fixture imports with one shared cell and `Side = Bottom`, every land equal to
the old importer's to the DBU (R-bsp4-1/2); a back role with no front layer falls back to the baked cell, reported (R-bsp4-3);
export (BSP-3) → import round trip of a two-sided board (R-bsp4-2); flip in layout then Update Layout leaves the symbol
unmirrored, and a symbol mirror moves the bottom part on its own face (R-bsp4-5); a value change that regenerates a bottom
part's land pattern keeps it on the bottom (R-bsp4-6). Then the existing PCB import and Update Layout classes by name.

## 5. Docs

`layout-editor.md`'s Import section: back-side footprints arrive as bottom-side placements of one footprint. The
schematic-to-layout page: flipping a part does not touch its symbol.
