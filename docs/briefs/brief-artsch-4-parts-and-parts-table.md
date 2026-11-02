# Brief AS-4 — Parts: evidence, connection, case, value, model; the parts table

**Series:** `brief-artsch-0-overview.md` (D9, D10, D16) · **Tag:** `R-as4-<m>`
**Depends on:** AS-3
**Area:** `src/Design/Layout/Recognition/` (`PartReading.cs`, `LandPatternMatch.cs`, `PartEvidence.cs`,
`PartsTable.cs`, `PartsTableCsv.cs`, `PartModelResolution.cs`); reads `src/Design/Layout/Footprints/`
(`SmtCaseTable`, `ChipLandPatternGenerator`, `DensityVariant`, `LayoutPartKind`),
`src/Design/Layout/Interchange/` (`PlacementFile`, `BomFile`, `RefdesCell`, `DelimitedTable`),
`src/Design/Schematic/BomTablePaste.cs` (its field recognisers), `src/Design/RailRf/` (`PartLibrary*`,
`RailPartDiscovery.SeriesTerminals`, `RailPartMarks`), `src/Design/Layout/Extraction/PlacedPins.cs`

---

## 1. Goal

Every two-terminal part on the board, read as well as the evidence allows, into **one table** that the user (or an
agent) reviews and corrects before anything is generated: refdes, kind, series or shunt, case, value, model, and
where each answer came from. The table is a document with a fixed CSV format; the GUI's grid and the CLI's
`--parts` / `--parts-out` read and write the same bytes.

## 2. Requirements

**R-as4-1 — Evidence sources (D10), strongest first, each recorded per field.**
1. **Placed footprint instances** — `LayoutInstance.RefDes`, `LayoutInstance.PartKind` (through `LayoutPartKind`,
   whose "unrecognised is absent" rule holds), and the generated `smt-<case>@<density>` cell, which states the case.
   Pads from `PlacedPins`.
2. **Placement + BOM** — `PlacementFile` and `BomFile` with every refusal intact: an unstated placement origin is
   asked (GUI) or a refusal naming the flag (CLI), never guessed. A placement row lands on pads through the same
   resolution railRF uses (`RailPartMarks`), so the two features cannot disagree about which pads are C7's.
   `RefdesCell` expands grouped BOM cells.
3. **Land-pattern match** (`LandPatternMatch`) — for copper not already claimed by 1–2: pad shapes taken from the
   **solder-mask or paste openings** on an outer layer where the technology has those layers, else from copper
   features at line ends the trace review classifies as "pad". Two pads form a candidate when their sizes and gap
   match a `SmtCaseTable` case's land pattern at **any** density variant (`ChipLandPatternGenerator` generates the
   reference geometry; do not re-tabulate it) within 20 % on each pad dimension and on the gap, in either
   orientation. Best fit wins; a runner-up within 5 % of the best is named in the row's notes (`0402 or 1005M`).
   Overlapping candidates resolve by best fit; a pad is never in two parts.
4. **Silkscreen refdes** — AS-10, plugged in here through the same `PartEvidence` interface. Not built now.
5. Nothing — the row exists (the pads were matched) and its kind and value are unknown.

**R-as4-2 — Kind.** From, in order: refdes prefix (`R`, `L`, `C`, `FB`/`FL` → L, `D`, `Q`, `U`/`IC`, `J`/`P`/`X`,
`Y`, `SW`, `TP`); placed `PartKind`; BOM description words (`BomTablePaste`'s type-word recogniser, shared, not
copied). Else **Unknown** (`?`), generated as C (D10). Kinds a row may take: `R`, `L`, `C`, `Short` (a 0 Ω link
or jumper — a wire), `Open` (not fitted — the BOM's DNP/NF/"not fitted" markers set this), `MultiPin`, `Connector`,
`Ignore` (the row is left out; a series `Ignore` is an open, a shunt one is removed). A part with more than two
pads is `MultiPin` or `Connector` and goes to AS-3's port rule (D9). Its row stays in the table (kind, refdes, pad count, part number where the BOM
gives one) so the user can see what was cut out; the part number is what a user takes to **Import Component**
(`ComponentImport`) to bring the device's symbol in afterwards. Recognition does not call the import itself.

**R-as4-3 — Connection (measured, never stated).** From the AS-3 islands of the two pads: one on ground → `shunt`;
both on signal islands → `series`; both on ground → `shorted` (reported — a part across ground does nothing; it is
left out); both pads on one signal island → `bridged` (reported — copper runs round it; it is left out, railRF's
bridged-element rule, `RailPartDiscovery.SeriesTerminals` reused for the terminal pairing).

**R-as4-4 — Value.** From the BOM's value column, read with `BomTablePaste`'s value-with-unit recogniser (SI
prefixes and the letter-as-decimal convention, `4R7`, `10n0`, `2p2`); else from the BOM description's parse where
it yields a value; else **unknown**. `BomFile`'s rule holds: a field the parse could not read is null and stays
null, never filled from a case code or a class default. A value in the wrong dimension for the kind (`10nH` on a
`C`) is a row warning, and the value is not used.

**R-as4-5 — Unknown values (D10).** A row with an unknown value carries a **variable name** `<Refdes>_<Param>`
(`C6_C`, `L2_L`, `R1_R`) and a **transparent initial value** (series C 100 pF, L 0.1 nH, R 0 Ω; shunt C 0.01 pF,
L 1 µH, R 1 MΩ). The user may type a value in the table, which removes the variable. AS-6 emits the variable and
its `tune` entry.

**R-as4-6 — Model (D10).** `PartModelResolution`: a part number (from the BOM) that resolves in the workspace's
part library (`.crlib`, `PartLibraryIo`, the library railRF reads) to a Touchstone file, or names a workspace
`.sNp` whose file name **begins with the part number** (case-insensitive; several matches → the shortest name, the
others in the notes) → `SnP` with that file; else `Ideal`. A Touchstone with other than two ports is not used, and
says so. The user can set the model column to any `.sNp` path or back to `Ideal`.

**R-as4-7 — The table.** `PartsTable` rows: `Refdes, Kind, Connection, Case, Value, Variable, Model, ModelFile,
PartNumber, X, Y, Evidence, Confidence, Notes`. `X`/`Y` are the part's centre **with an SI unit** (the layout's
display unit). `Evidence` names the source of each field that has one (`kind=bom;value=bom;case=instance`).
`Confidence` is `high` (instance or BOM agrees with pads), `medium` (one source only), `low` (land pattern only,
kind unknown). Refdes for parts with none is generated `U?` style per kind: `C_A1`, `C_A2`, … (`_A` marks
recognised-not-named, so it can never collide with a board's own refdes).

**R-as4-8 — The CSV (D10).** `PartsTableCsv` writes and reads the table: UTF-8, comma-delimited, a fixed header
in the R-as4-7 order, one row per part, sorted by refdes in natural order. Reading back:
- a row whose `Refdes` matches a recognised part **overrides** its editable columns (`Kind`, `Value`, `Model`,
  `ModelFile`, `Variable` cleared by a value); `Connection`, `Case`, `X`, `Y`, `Evidence`, `Confidence` are
  **measured** and ignored on read, with a note when they differ from the board;
- a row naming a refdes not on the board is reported and ignored, never invents a part;
- an unknown column is a refusal naming it (a typo would otherwise silently do nothing).
Write → read is the identity (a gate).

**R-as4-9 — Report classes.** Parts by evidence source, unknown kinds, unknown values (= variables), SnP models
used, land-pattern ambiguities, shorted/bridged parts left out, BOM and placement rows not on the board,
mask/paste layers absent (land-pattern match fell back to copper features).

## 3. Not in this phase
Placing anything in a schematic (AS-6); the grid UI (AS-8); OCR (AS-10); case-size parasitic models (D18).

## 4. Gates (minimal tests, run only these classes)
- `LandPatternMatchTests` — pad pairs generated by `ChipLandPatternGenerator` for 0402/0603/0805 at each density,
  placed at 0° and 90°, are each matched to their case; a pair scaled by 30 % is not matched; two overlapping
  candidates resolve to the better fit.
- `PartReadingTests` — on a synthetic board: a placed instance with `RefDes=C6` wins over a land-pattern match on
  the same pads; a BOM row gives the value `4R7` → 4.7 Ω on an `R`; a `10nH` value on a `C` is the warning; a DNP
  row is `Open`; a part across two signal islands is series, a part to the pour is shunt, a part with copper round
  it is bridged and left out.
- `PartModelResolutionTests` — a part number matching `TESTPN123_series.s2p` in a temp workspace resolves to it;
  a four-port file is refused with the note; no match is `Ideal`.
- `PartsTableCsvTests` — write → read identity; an edited `Value` clears `Variable`; an edited `Connection` is
  ignored with a note; an unknown column is the refusal; a refdes not on the board is reported.
- `PartReadingFieldTests` — `FixtureFact` on `testdata/artwork-boards/`: each board's part count and series/shunt
  split equal its `expected.json`.
