# Brief BSP-0 — Bottom-side placement: overview

**Series:** BSP-0 overview · BSP-1 the side model · BSP-2 the editor's Flip · BSP-3 outputs · BSP-4 imports and the
schematic round trip. · **Tag:** `R-bsp<n>-<k>` · **Origin:** designer feedback, 2026-10-21

---

## 1. The problem

A board's parts sit on both faces, and circuitRF's layout editor can only put them on one. A placed part is a
`LayoutInstance` — a cell reference plus `X`, `Y`, `RotationDegrees`, `MirrorX`, `Mag` — and nothing in it says which
face it is on. **Mirror** (`LayoutEditorViewModel.MirrorSelection`) reflects the geometry and leaves every shape on its
own layer, so a "mirrored" chip resistor still has its lands on Top Copper, its mask openings on Soldermask Top and its
designator on Silk Top. The designer reported exactly that: pads on top copper, reference on top silk, no flip.

The one place bottom-side parts exist today is the `.kicad_pcb` import (`PcbImport`), and it gets them by baking: a
back-side footprint becomes its own cell whose stored geometry is already Y-negated and already on the back layers, and
the instance is placed with `MirrorX = false` (`PcbImport.cs` ~237, `PcbReader.ReadFootprint`). That cannot be offered
to a user placing a part — it means a second cell per footprint per side, which drifts the moment either is edited.

## 2. The decisions this series is built on

**D1 — Side is a property of the PLACEMENT, not a second cell.** `LayoutInstance.Side` (`Top` | `Bottom`, default
`Top`). One footprint cell serves both faces. A bottom-side placement is drawn with its geometry mirrored (the existing
`MirrorX` transform) and what the cell draws on the top SURFACE moved to the bottom surface. **Flip** is the one
gesture that changes both together; Mirror stays what it is (a reflection on the same face).

**D2 — A board has two surfaces, and a flip moves a part from one to the other — role by role, not layer by layer.**
A footprint draws only on surface roles: copper, soldermask, paste, silkscreen, courtyard and assembly. Each role has a
top layer and a bottom layer in the technology, found the way `LandPatternLayers` already finds the front ones (board-format
alias, then `Purpose`, then a side word in the name; copper = first/last stackup conductor). A flip swaps each role's top
layer for its bottom layer; **every other layer — inner copper, drill, outline — stays where it is.** No new technology
field and no pairing table. A role the technology has on only one surface is **omitted** from the flipped part and
reported, exactly as a land pattern omits a role the technology lacks; the one refusal is a board with no bottom copper.

**D3 — One resolution, side-aware, used everywhere.** About sixty call sites resolve a placed part's cell by its
`inst.CellRef` (`CellLayoutResolver.Resolve(inst.CellRef, baseDir)` — flatten, the renderer's instance pass, hit test,
snap, conductor lookup, placed pins, LVS's layout read, designators, the EM geometry, the 3D elaborator, WBond…). They
all move to an overload that takes the INSTANCE and returns, for a bottom-side placement, a cached layer-swapped clone
of the view. Because the renderer's compile cache is keyed on the `LayoutView` instance the resolver returns, a
distinct cached bottom view is a distinct cache entry with no renderer change. A source-scan gate forbids
`Resolve(<x>.CellRef,` from coming back.

**D4 — Text is never mirrored; it keeps its reading direction (owner's decision).** A bottom part's designator and any
other text it carries reads left to right from above, on screen and in every output, because that is the more readable
board. The text MOVES with the part — its centre goes where the mirror carries it — but its glyphs are not reflected and
its angle is not turned upside down. No `Mirrored` flag is added to `LabelShape`.

**D5 — Sides compose.** A bottom-side placement inside a cell that is itself placed on the bottom is on the top. The
resolver XORs sides down the hierarchy exactly as `LayoutInstanceTransform.ComposeInstances` composes mirrors.

**D6 — The shipped board technologies are made complete.** `Courtyard Top` (`F.CrtYd`) has no bottom twin in any
shipped `.ctech`; each gains `Courtyard Bottom` (`B.CrtYd`) so a flipped generated land pattern omits nothing. Any other
role a shipped board technology has on one surface only gets its twin in the same change.

## 3. Order and dependencies

| Brief | Builds | Depends on |
|---|---|---|
| BSP-1 | `Side`, surface roles on both sides, side-aware `Resolve`, the call-site migration, readable text, persistence | — |
| BSP-2 | Flip in the editor (key, menus, properties), designators, loose shapes, undo, user docs | BSP-1 |
| BSP-3 | Gerber, GDSII, DXF, `.kicad_pcb` export, placement file and BOM, DRC, EM and 3D checks, CLI `render` | BSP-1 |
| BSP-4 | `.kicad_pcb` import places back parts as `Side = Bottom` of one footprint; schematic → layout keeps the side | BSP-1, BSP-3 |

BSP-2 and BSP-3 are independent of each other once BSP-1 is in.

## 4. Owner's answers, and what is still open

- **O1 — Format version: answered, additive.** `Side` is written only when `Bottom`; no `FormatVersion` bump. A `.clay`
  written before this series has no `Side`, so every part in it is on the top face; a part that belongs on the bottom is
  flipped and the file resaved. Nothing migrates old files.
- **O2 — The key: still open.** `F` is Fit (`LayoutCanvas.cs` ~2048). Proposed: **B** for Flip.
- **O3 — Viewing from below: answered, not wanted.** The board is always viewed from the top, as many layout tools do;
  there is no bottom-view brief.

## 5. Standing rules for every brief in the series

- No vendor or tool names anywhere (`F.Cu`/`B.Cu` already appear in code as format aliases; that is the only use).
- Findings go in the nearest `RESOLVED.md`, never in a `CLAUDE.md`. Never quote the reporter.
- Tests: one per claim, targeted classes only — never the full suite or all of `Ui.Tests`; read the TRX, never re-run
  to find a failure. A performance claim is gated by a COUNTER, never a timing.
- DocGen is run by the owner at the end of the series; edit doc sources only.
