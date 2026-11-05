# Brief IPX-0 — IPC-2581 import: series overview and the import seam

**Series:** IPC-2581 import, IPX-0 to IPX-4 · **Tag:** `R-ipx0-n`
**Area:** `src/Design/Layout/Interchange/PcbImport.cs`, `PcbBoard.cs`, `PcbStackupMapping.cs`
**Depends on:** nothing. IPX-2, 3 and 4 depend on this brief. IPX-1 stands alone.

---

## 0. What the series adds

A board reaches circuitRF as Gerber plus drill files or as a `.kicad_pcb`. A Gerber set carries no
dielectric data and needs a separate netlist file for its net names. IPC-2581 carries the artwork,
drills, stackup and nets in one XML file, and circuitRF cannot read it. The layout editor also has no
general list of a board's nets: net lists exist only inside the Impedance and railRF tools.

| Brief | Adds |
|---|---|
| **IPX-0** | This groundwork: `PcbImport` accepts a board from any reader, with no change in behaviour |
| **IPX-1** | A Nets panel in the layout editor and `explain --nets`, useful on every board with named nets |
| **IPX-2** | The IPC-2581 reader: layers, units, dictionaries and copper |
| **IPX-3** | The same reader: nets, pads, holes, vias and the stackup |
| **IPX-4** | File ▸ Import, `convert`, `check`, the agent tools and the user documentation, plus the real-file acceptance run |

## 1. Series rules

- **S1 — Import only.** No IPC-2581 writer. `convert` refuses it as a target.
- **S2 — The artwork is the geometry.** `LayerFeature` is the complete copper of the board. Packages,
  land patterns, `PadStackDef` and `PhyNet` points never create copper. They attach facts to it or are
  counted and reported.
- **S3 — Read by the elements present, not by the revision.** The revision is reported and never
  branched on (the rule R-L4d-1 set for the board format). Files in the field differ in more than the
  revision says; see IPX-2 §2.
- **S4 — Every loss is reported.** Skipped and degraded content goes through
  `PcbBoard.SkippedCounts` and `DegradedCounts`, once per kind with a count.
- **S5 — No physics from a material name.** `materialType` and `PRODUCT_NAME` strings are never looked
  up. They are not copied into code, messages or tests as a table of names.
- **S6 — One function, two callers.** The GUI and the CLI call the same `src/Design` function.
- **S7 — Own words.** Code and comments describe the format in our own words. No text of the standard
  and no copy of its schema enters the repo.
- **S8 — No sample boards in the repo.** Fixtures in `testdata/ipc2581/` are small files written by hand
  for these tests. Real boards are used only by the uncommitted acceptance run in IPX-4 §5; their names,
  paths and measurements stay out of the repo.

## 2. Decisions taken for the series

- **D1 — Flat board cell.** A board imports as one flat cell, as a Gerber set does. `Set` features become
  shapes in board coordinates and each pad carries `Component` and `Pin`. No footprint cells and no
  instances are built, because `LayerFeature` already holds every pad and a footprint cell would draw it
  twice. The number of components not placed as instances is reported. Placing them as instances is left
  for a later brief, and that deferral stays visible in the import message.
- **D2 — One frequency per dielectric.** `StackupLayer.Epsr` and `TanD` are single values. When a file
  tabulates them against frequency, the lowest stated frequency is used and the message lists every
  stated pair.
- **D3 — Recognised by content.** The standard's extension is `.xml` and files also circulate as `.cvg`.
  A file is IPC-2581 when its root element is `IPC-2581`, whatever the extension.
- **D4 — Rigid-flex is reported, not modelled.** One technology holds one stackup. When a file declares
  stackup zones, the stackup of the zone with the largest profile area is used and the message names
  every zone and the one chosen. All artwork still imports.

## 3. `R-ipx0-1` — the seam

`PcbImport.Import(Stream, …)` decodes the stream to a string, calls `PcbReader.Read`, then does the
layer mapping, technology, via span and cell-folder work.

- **`R-ipx0-1a`** Split out `PcbImport.ImportBoard(PcbBoard board, …)`, taking every parameter `Import`
  takes after the stream. `Import` becomes the text read, the `PcbReader.Read` call and its refusals,
  then a call to `ImportBoard`. A 200 MB XML file must never be decoded to one string on its way here.
- **`R-ipx0-1b`** Move the format-specific sentence ("Board format epoch …") into `Import`, with the
  version read. `ImportBoard` reports only what the reader put in `board.Diagnostics`.
- **`R-ipx0-1c`** A board with no footprint cells and no placements runs through `PlanSides` and the cell
  writing without error and creates only the board cell. Add one test for this (D1 depends on it).

## 4. `R-ipx0-2` — a stackup entry can state more

`PcbStackupEntry` has no conductivity. `PcbStackupMapping.Build` writes the copper default and states as
a fact that the format carries none, which is true of the board format and false of IPC-2581.

- **`R-ipx0-2a`** Add `double? ConductivitySm` to `PcbStackupEntry`. A stated value is used; otherwise the
  existing default applies.
- **`R-ipx0-2b`** Replace the fixed "Not carried by this format" note with a sentence built from what was
  actually defaulted. `.kicad_pcb` output keeps its meaning. Existing message assertions change only where
  they quote the sentence, and each such change is listed in the completion note.
- **`R-ipx0-2c`** `Build` keeps `Epsr` at its C# default of 1.0 when a dielectric entry states none, which
  is air. Do not change this for `.kicad_pcb` in this brief; IPX-3 §5 completes the IPC-2581 path through
  `SubstrateDefaults`. Record the `.kicad_pcb` behaviour as an open question in the completion note so the
  owner can decide on it separately.

## 5. Gates

- `PcbImportTests`, `PcbImportTechnologyTests`, `PcbViaSpanImportTests` and `BottomSideRoundTripTests`
  pass. The only permitted edits are the message quotes in `R-ipx0-2b`.
- `ConvertCliVerbTests` passes: a `.kicad_pcb` source produces the same files as before.
- Run those classes only.
