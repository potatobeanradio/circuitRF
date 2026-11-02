# Brief SI-1 — The neutral model, the part map, the build, and the two write paths

**Series:** `brief-schimp-0-overview.md` (D1–D6, D8, D10) · **Tag:** `R-si1-<m>`
**Depends on:** —
**Area:** new `src/Design/Schematic/Import/` (`ForeignSchematic.cs`, `SchematicImportMap.cs`,
`SchematicImportBuild.cs`, `SchematicImport.cs`, `ImportConnectivity.cs`, `SchematicFileClassifier.cs`);
`src/Design/Layout/ComponentImport.cs` (factor out the symbol half — no behaviour change);
`src/Design/Schematic/NetlistSchematic.cs` (read-mostly; R-si1-6 may touch the exact-placement snap);
`src/Design/Schematic/Recognition/SchematicImageRecognition.cs` (lift `Merge` out — no behaviour change);
`src/Design/Cells/CellCreate.cs` (read-only use); `docs/design/schematic-import.md` (new).
**Out of bounds:** `src/Core`, `src/Engine`, `src/RfCore`; the image readers' logic; the `iml` branch.

---

## 1. Goal

Everything the three readers share, finished and tested **before any reader exists**, against a `ForeignSchematic`
built by hand in the tests: the model a reader fills, the decision of what each part becomes, the drawing, the
connectivity check, the new-cell import and the merge into an open schematic.

## 2. Requirements

**R-si1-1 — `ForeignSchematic`, the neutral model.** Plain immutable records, no format types:
- `ForeignSymbol` — key (format-unique), display name, library name, unit count; per unit: pins (`Number`, `Name`,
  `ElectricalType`, connection point, direction the body extends, length, `Hidden`, `ImplicitNet` — a hidden power
  pin's implicit net name, or null) and graphics as **`KitSymbolShape`** (Core's existing neutral vocabulary, so
  `KitTemplateSymbol.BuildFromDrawing` takes it unchanged). Coordinates in **mils, Y up** — the convention
  `ComponentPart.cs` states for symbols, so the existing flip in `ComponentImport` applies once and only there.
- `ForeignSheet` — name, page size (mils), and its contents in **schematic orientation (mils, Y down)**:
  `ForeignInstance` (reference, unit, symbol key, origin, rotation 0/90/180/270, mirror, value, properties map),
  `ForeignWire` (polyline), `ForeignJunction`, `ForeignLabel` (kind `Local | Global | Hierarchical | OffPage | Power`,
  text, anchor), `ForeignNoConnect`, `ForeignSheetInstance` (child sheet name, rectangle, its port pins), free
  `ForeignText` and `ForeignGraphic`.
- `ForeignSchematic` — format name (by extension — overview D7), source path, sheets, symbols, root sheet, flat/
  hierarchical flag, `DeclaredNets` (sets of `(reference, pinNumber)`, or null when the format declares none), and
  `Notes` (what the reader could not carry, one sentence each).

**R-si1-2 — Pin numbers are the identity.** Connectivity is keyed on `(reference, pinNumber)`, never on pin names
(names repeat — `GND` ×4) nor on a format's internal port ids. A multi-unit part (`U1A`, `U1B`) is one reference;
each unit is placed as its own instance named `<ref><unitLetter>`, and its pins map back to the reference's numbers.

**R-si1-3 — `SchematicImportMap` (overview D3).** A table of rules, data not code: each rule matches on symbol
name pattern, reference prefix, pin count, and optionally value syntax, and names the native component and how its
parameters are read from the value (e.g. `10k` → `R=10 kOhm`; `100nF` → `C=100 nF`; `4u7`/`4R7` notation). Rules for
two-pin R, L, C and for ground. **Anything unmatched → a symbol cell** (R-si1-4). A matched part whose value does not
parse → the native part with its value a global variable `<Ref>_<Param>` and the source text kept as a note (the AS
rule). Expose the table so the dialog can show "U3 → symbol cell `LM358`" per row and the user can override a row to
symbol cell (never the other way: we do not invent behaviour for an unknown part).

**R-si1-4 — Symbol cells, through Import Component's own path.** Factor `ComponentImport`'s symbol half into a
public `BuildSymbol(KitSymbol drawing, …)` that both callers use (Import Component's tests must pass unchanged). Per
distinct `(symbol, unit)` a cell with a symbol view only, in overview D4's folder, reused when byte-identical, refused
(naming both) when a same-named cell differs. A symbol pin whose number is empty takes the name, then the index
(report it).

**R-si1-5 — `SchematicImportBuild`: one TestBench + one `ExactPlacement`.** The TestBench's nets come from the
source's geometry — wires joined at endpoints and junctions, labels of equal text on one sheet (local) or anywhere
(global/power), hierarchical labels to the parent sheet's pins, off-page labels by name across pages, hidden power
pins to their `ImplicitNet` — implemented ONCE here for all formats (the readers supply geometry, not nets). A net
the source grounds becomes `0`. Instance names as the source's references (sanitised; the map of renamings is in the
result). `ExactPlacement.Instances` = source origins/orientations; `.Routes` = the source's wires per net. Pages tile
per overview D5. Free text and graphic lines become schematic text and lines.

**R-si1-6 — Measure the grid question before changing anything.** Sources on a 50-mil grid (common) put pins at odd
multiples of 50. Write a test with a two-resistor sheet on a 50-mil grid and assert the drawn pins coincide with the
source's connection points. If `NetlistSchematic`'s exact path snaps origins to `P` (100) and breaks it, the fix is a
flag on `ExactPlacement` (`SnapToGrid = false`, default true so the image path is untouched) — not a change of `P`.
Record the outcome in `src/Design/RESOLVED.md`.

**R-si1-7 — The connectivity check (overview D8).** `ImportConnectivity.Compare(ForeignSchematic, built)`: extract
the produced schematic with `NetExtractor` (the path Simulate uses), translate instance pins back to
`(reference, pinNumber)`, and compare as sets of pin sets against (a) `DeclaredNets` when the format has them and
(b) R-si1-5's own geometric nets always. Report `Matched`, and each mismatch as "pins {…} joined in the source, split
in the import" or the converse. Single-pin nets and no-connect pins are excluded from both sides identically.

**R-si1-8 — `SchematicImport.Import` (new cell).** `(ForeignSchematic, target workspace/folder, cell name, ports,
options) → ImportResult(CellDir, CreatedCells, ReusedCells, Mapping, Connectivity, Messages)`. Uses
`CellCreate` with a schematic view (as New Cell does); a name that exists is a refusal naming it (`NameValidator`
applies). Writes the `.csch` (and hierarchy sub-cells per D5) only after the whole build succeeded — never a partial
cell. A `SchematicImportSource` provenance block in the `.csch` records format, source file name (not path — no
personal paths) and import date, so a later re-import can offer Replace with a history checkpoint first (the
`RecognitionTarget.Replace` pattern).

**R-si1-9 — `SchematicImport.BuildInto` (open schematic).** Same build; the drawing offset so the source's bounding
box top-left lands on the given point (snapped to `P`), analyses dropped, instance/net-label/variable names renamed on
collision. **Lift the generic part of `SchematicImageRecognition.Merge` (renaming against an existing
`SchematicEditModel`) into `SchematicMerge` in `src/Design/Schematic/`** and call it from both places; the image
paste's tests must pass unchanged (run `SchematicImage*Paste*` classes only). Returns the edit for the UI to execute
as one undo step (SI-5).

**R-si1-10 — Classifier.** `SchematicFileClassifier.Classify(path)` by content: CFBF signature → `BinaryDsn` (SI-4
confirms by its root streams; an unrelated compound file is `Unknown`); text starting `(kicad_sch` → `KicadSch`; text
starting `(edif` (case-insensitive, after whitespace) → `Edif200` when its `edifVersion` is `2 0 0`, else refused with
the version. An ASCII `.dsn` kit symbol or a routing `.dsn` is `Unknown` with "not a schematic design" — never routed
to `DsnSymbolReader` from here.

**R-si1-11 — The design note.** `docs/design/schematic-import.md`: this series' model, map, build and check — written
from the overview, kept current by each phase.

## 3. Gates (targeted tests only)
- A hand-built `ForeignSchematic` (R + C divider, a two-unit op-amp, a ground, a `+5V` power label, a hidden power pin,
  two pages joined by an off-page label) → `Import` → a cell whose `NetExtractor` nets equal the source's: one test.
- The map: one theory over values (`10k`, `4k7`, `100nF`, `4u7`, `2R2`, `1.5 nH`, `DNP`); one test that an unmatched
  part becomes a reused symbol cell on the second import.
- R-si1-6's 50-mil test.
- `BuildInto` with a colliding `R1` and a colliding variable → renamed, one edit.
- Import Component's and the image paste's existing classes pass unchanged.

## 4. Hand-back
Report the R-si1-6 outcome, the map table as written, and anything in `ForeignSchematic` a reader brief should know.
