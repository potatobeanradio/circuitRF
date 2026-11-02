# Brief AS-6 — Emit: the circuit, its technology, the drawing, the target cell, and the L5 commands

**Series:** `brief-artsch-0-overview.md` (D1, D4, D5, D10, D12, D15, D20) · **Tag:** `R-as6-<m>`
**Depends on:** AS-4, AS-5
**Area:** `src/Design/Layout/Recognition/` (`RecognitionEmit.cs`, `RecognitionTarget.cs`,
`RecognitionProvenance.cs`); `src/Design/Schematic/` (`SchematicModel.cs` / `EditableSchematic.cs` /
`SchematicPersistence.cs` for the new component fields and the per-schematic `TechRef`;
`MicrostripSubstrateInjection.cs`, `ViaSubstrateInjection.cs`, `MmicPassiveInjection.cs`, AS-1's injection,
`CnlTechnologyBinding.cs`, `NetExtractor.cs`, `NetlistSchematic.cs`); `src/Core/Netlist/CnlReader.cs` /
`CnlWriter.cs` (the `technology` statement); `src/Design/Cells/CellCreate.cs` (read-only use);
`src/Design/Revision/` (the checkpoint function `history checkpoint` calls); `src/Ui/Layout/SchematicToLayoutGenerator.cs`,
`src/Ui/Layout/LayoutToSchematicGenerator.cs`, `src/Ui/Layout/TechnologyDivergenceReport.cs`

---

## 1. Goal

Turn AS-3/AS-4/AS-5's reading into a circuit that simulates on **the artwork's own technology**, draw it through the
existing netlist drawer, write it into the chosen cell, and make sure the two L5 sync commands leave its components
alone.

## 2. Requirements

**R-as6-1 — The per-schematic technology (D20).** A schematic today always takes the **workspace default**
technology (`MicrostripSubstrateInjection.ResolveWorkspaceTechnology`; its own comment reserves a per-schematic
override "if a need appears"). An imported board's cell carries its **own** `.ctech`, usually not the workspace
default — so a recognised MLIN would be computed on the wrong substrate with nothing said. The need has appeared:
- a `.csch` gains an optional **`TechRef`** (relative path, the `.clay`'s spelling and resolution);
- every injection (`MicrostripSubstrateInjection`, `ViaSubstrateInjection`, `MmicPassiveInjection`, AS-1's) and
  `NetExtractor` resolve **the schematic's `TechRef` first, then the workspace default** — the order a layout
  already uses, in one shared resolver, not four copies;
- a `.cnl` gains a **`technology "<path>"`** statement (relative to the `.cnl`), which `CnlTechnologyBinding`
  honours ahead of the walk-up; `CnlReader` stops reporting it as unknown; `CnlWriter` writes it when the
  schematic has a `TechRef`; `reference` and `check` document and validate it (a path that does not resolve is an
  error naming it);
- `TechnologyDivergenceReport` compares the layout's technology with the schematic's **resolved** one (its own
  header describes the divergence this removes) — a schematic whose `TechRef` matches its layout no longer warns;
- `explain` reports which technology a schematic resolved and through which walk.
A schematic without `TechRef` behaves byte-identically to today.

**R-as6-2 — The circuit.** `RecognitionEmit` builds a `TestBench` (the type `CnlReader` produces):
- **Instance names**: parts by refdes (`C6`, `C_A1`); lines `TL1…`, bends `B1…`, tees `TEE1…`, crosses `X1…`, tapers
  `TP1…`, CPWG `CP1…`, SLIN `SL1…`, TLIN fallbacks `TF1…`, vias `V1…` / `VG1…`, ports `P1…`/`X1…` as AS-3 named them;
  numbered along the signal path from port 1, so a name reads the board in order.
- **Nets**: `n<k>`, except a port's net, which takes the port's name in lower case (`p1`), and ground, `0`.
- **Line elements** carry `SignalLayer` (and `GroundReference` where the reference is not the nearest below) — the
  layer-choice parameters `CnlTechnologyBinding` already consumes — and **no substrate values**; the substrate
  comes from the technology (R-as6-1).
- **Vias** carry drill, pad and `FromLayer`/`ToLayer`/`GroundLayer` from AS-3; the barrel and planes are injected.
- **Parts**: `R`/`L`/`C` with the value or the variable's name; `Short` → the two nets merged; `Open`/`Ignore` →
  nothing; `SnP` → the SnP instance with its file (path relative to the target schematic, `SnpPathPolicy`), port 2
  to ground for a shunt part.
- **Globals**: each unknown-value variable `C6_C = 100 pF`, and a **`tune`** entry for it (the tuning series'
  grammar, tune enabled, optimize off, the range ×0.1 … ×10 of the initial value, or 0 … 10 Ω / 0 … 1 nH for the
  transparent zero-ish initials).
- **Analysis**: `SP1`, S-parameters, the range per D15.
- The `technology` statement naming the artwork's `.ctech`.
This is also the `.cnl` the CLI writes (`-o x.cnl`). It must pass `check` with **0 errors** on every gate board.

**R-as6-3 — The drawing.** `NetlistSchematic.Build` draws it. Build gains an **optional placement-hint map**
(instance → artwork point). With hints, the signal path's left-to-right order follows the artwork path from the
lowest-numbered port, and a shunt element drops toward the side its artwork lies on. Without hints the output is
byte-identical to today (a gate). Large drawings are accepted as they come out; the gate is that they draw and
round-trip, not that they are pretty.

**R-as6-4 — Component fields (D5, D12, D19).** `EditableComponent` gains, persisted in the `.csch` and ignored by
the elaborator:
- `FromArtwork` (bool);
- `ArtworkAnchor` — a DBU point, or a polyline for a line element, in the source layout's coordinates;
- `ArtworkMeasured` — a small name → value bag of what was measured but is not a parameter of this component
  (for MLIN and CPWG: `GapLeft`, `GapRight`; for every line: `Z0`, `Eeff` from the stations). AS-11 reads it.
Applied to the drawn schematic by instance name after `Build`. A `.cnl` does not carry them; a `.cnl` → `.csch`
draw has none, which is correct (nothing in it came from artwork).

**R-as6-5 — Provenance (D12).** The `.csch` gains an `ArtworkSource` block: the source `.clay` (relative), the
scope (whole / polygons / rectangle, in DBU), the options, the parts CSV's SHA-256 (when one was read), the
circuitRF version (`AppVersion`'s source, the `VERSION` file), the time (UTC). Its presence is what marks the
schematic as this command's to replace.

**R-as6-6 — The target (D4).** `RecognitionTarget`:
- **NewCell(name)** — `CellCreate.Create` with a schematic view only, in the artwork cell's workspace (the folder
  New Cell would use); a name that exists is a refusal naming it; `NameValidator` applies.
- **ArtworkCell** — allowed **only** when the artwork cell has **no schematic view**; writes the primary schematic
  view there (`CellCreate.WriteSchematicView`).
- **Replace** — a cell whose primary schematic carries `ArtworkSource`: a **history checkpoint** first (the
  function `history checkpoint --intent` calls, intent "Create Schematic from Artwork"), then the schematic is
  rewritten. A schematic without `ArtworkSource` is a refusal: *"<cell>'s schematic was not created from artwork;
  choose a new cell"*.
- The written schematic's `TechRef` is the artwork's resolved technology, made relative to the schematic.
Writes the schematic only. **Never the layout.**

**R-as6-7 — The L5 commands honour `FromArtwork` (D5).**
- `SchematicToLayoutGenerator`: a `FromArtwork` component is skipped before resolution — no add, no update, no
  orphan deletion — and the report gains one line, *"N components model existing artwork — not generated"*.
- `LayoutToSchematicGenerator`: a layout instance whose `RefDes` or `SchematicId` equals the instance name of a
  `FromArtwork` component creates and edits nothing, and the report says *"C6 is modelled from artwork —
  unchanged"*.
- Clearing the flag returns a component to ordinary sync; nothing else differs.

**R-as6-8 — One entry point.** `ArtworkRecognition.Run(input, target)` → recognise, emit, draw, write; it returns
the result with the written paths and the full report. The GUI command and the CLI verb both call it.

## 3. Not in this phase
The verb (AS-7), the dialog (AS-8), the swap (AS-11).

## 4. Gates (minimal tests, run only these classes)
- `SchematicTechRefTests` — a schematic with `TechRef` to a 0.5 mm εr 3.5 technology in a workspace whose default is
  1.6 mm FR-4: its MLIN extracts with the `TechRef`'s H and Er; without `TechRef` it takes the default (unchanged);
  the `.cnl` `technology` statement does the same headlessly; an unresolvable path is a `check` error.
- `TechnologyDivergenceReportTests` (existing or new) — matching `TechRef` and layout technology: no sentence.
- `RecognitionEmitTests` — a synthetic board (two ports, a series C with a BOM value, a shunt L unknown, a bend):
  the `.cnl` has the expected instances, the variable `L_A1_L` with its `tune` entry, the `technology` statement and
  `SP1`; `check` reports 0 errors; it simulates.
- `NetlistSchematicHintTests` — with no hints the drawing is byte-identical to the existing output; with hints a
  shunt part whose artwork lies above the line is drawn above it.
- `RecognitionTargetTests` — new cell created; artwork cell offered only without a schematic view; replace of an
  `ArtworkSource` schematic takes a checkpoint first (a counter on the revision call); replace of a hand-drawn
  schematic is the refusal; the `.clay` is byte-identical before and after every case.
- `FromArtworkSyncTests` — Update Layout from Schematic on a cell holding recognised MLINs generates nothing for
  them and reports the count; Update Schematic from Layout with a placed instance `RefDes=C6` beside a `FromArtwork`
  `C6` creates no duplicate.
