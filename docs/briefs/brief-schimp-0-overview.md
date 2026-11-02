# Brief series — Import Schematic (SI-1 … SI-6)

**Status:** written, not started · **Date:** 2026-10-18 · **Decisions:** locked (§2), owner-approved 2026-10-18
**Requirement tag:** `R-si<n>-<m>` (phase n, requirement m).
**Design note to be written by SI-1:** `docs/design/schematic-import.md` — this overview is its source.
**Builds on:** the component import (`src/Design/Layout/ComponentImport.cs`, `Interchange/ComponentRead.cs`,
`ComponentClassifier.cs`, `ComponentSymbolSexprReader.cs`, `ComponentPart.cs`, `Core.Pdk.KitSymbolPin/KitSymbolShape`,
`Schematic/KitTemplateSymbol.BuildFromDrawing`), the S-expression tokenizer (`Interchange/PcbSexpr.cs`), the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs` — `Build(…, exact: ExactPlacement)`), cell creation
(`src/Design/Cells/CellCreate.cs`), the history checkpoint (`src/Design/Revision/`), the `import part` verb
(`src/Cli/Authoring.cs`), and the schematic canvas's file drops (`src/Ui/Controls/SchematicCanvas.cs`).

---

## 0. What the owner asked for (paraphrased)

A **File ▸ Import ▸ Schematic…** command that reads a schematic drawn in another schematic editor and turns it into
a **new cell with its own schematic**. A file **dragged onto an open `.csch`** should build the same circuit directly
in that document, at the drop point. Start with the **binary `.DSN` design format** of one widely used commercial
schematic editor; the **`.kicad_sch`** format is wanted too, since `.kicad_pcb` boards already import — both now if
that is cheap, otherwise later. Reuse **Import Component** where it fits. The owner pointed to an MIT-licensed
open-source converter for the same source format as a model (§2 D7).

Two clarifications from the owner during the research (paraphrased):
- **A dropped IMAGE must not become a circuit.** Dropping a picture on a schematic places a picture, as it does today.
  Only a schematic FILE is built into circuitry.
- **Picture-to-circuit recognition is on hold.** Nothing in this series depends on, extends, or reuses the image
  readers (`src/Design/Schematic/Recognition/`) or the paused work on the `iml` branch.

### What the research found (2026-10-18) — read this before §1

1. **The reference converter does NOT parse the binary `.DSN` itself.** Its native-`.DSN` route shells out to the
   open-source board suite's own nightly importer (GPLv3) and post-processes that importer's output. That half is
   unusable here twice over: GPL code may not be ingested (CLAUDE.md §Licensing), and circuitRF does not ship or
   drive other applications' binaries for its imports. **Its own MIT code is the EDIF 2.0.0 route** — a ~600-line
   reader of the EDIF the commercial editor exports, plus an intermediate model and a writer. That is the part worth
   learning from.
2. **The binary `.DSN` is an undocumented format inside a Compound File Binary (CFBF/OLE2) container.** The container
   is publicly specified (MS-CFB). The streams inside it are not: the only open description is an MIT-licensed C++
   open-source parser, **archived (read-only) since 2024**, which documents many but not all record types and
   versions. **This is the series' one real risk** (SI-4 §0), and it is why SI-4 comes after two formats that are
   documented.
3. **EDIF 2.0.0 is an open standard text format** (S-expressions) the commercial editor writes from its own export
   menu. Every user of that editor can produce it. The pin, net and orientation conventions the reference converter
   learned are recorded in SI-3 as facts about the format.
4. **`.kicad_sch` is a publicly documented S-expression format whose `lib_symbols` section embeds every symbol in
   exactly the grammar `.kicad_sym` uses** — which `ComponentSymbolSexprReader` already reads, through `PcbSexpr`. A
   `.kicad_sch` file is self-contained; it needs no library lookup. It is the cheapest of the three by a wide margin.
5. **Two name collisions to keep apart.** `src/Design/Schematic/DsnSymbolReader.cs` reads an unrelated
   record-based **ASCII** `.dsn` (kit symbols), and `.dsn` is also an unrelated board-routing exchange format. The
   binary design file is told from both by its first eight bytes (the CFBF signature `D0 CF 11 E0 A1 B1 1A E1`),
   never by its extension (`ComponentClassifier`'s rule, R-PL1-28).

---

## 1. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| SI-1 | `brief-schimp-1-neutral-model-and-build.md` | The neutral `ForeignSchematic` model; part mapping (native vs symbol cell); the build into a TestBench + exact drawing; the import into a new cell; the merge into an open schematic; the connectivity check; the design note | — |
| SI-2 | `brief-schimp-2-kicad-sch-reader.md` | `.kicad_sch` → `ForeignSchematic`, hierarchy included | SI-1 |
| SI-3 | `brief-schimp-3-edif-reader.md` | EDIF 2.0.0 (`.edf`/`.edn`/`.edif`) → `ForeignSchematic` | SI-1 |
| SI-4 | `brief-schimp-4-binary-dsn-reader.md` | Read-only CFBF container + the binary `.DSN` stream reader → `ForeignSchematic`; refuses unknown versions | SI-1 (SI-3 for its cross-check) |
| SI-5 | `brief-schimp-5-entry-points.md` | File ▸ Import ▸ Schematic…, the drop onto an open `.csch`, the project-tree drop, `circuitrf import schematic`, the MCP `import` tool | SI-1, ≥ one reader |
| SI-6 | `brief-schimp-6-acceptance-and-docs.md` | Field-corpus acceptance, an example workspace, user docs, `cli.md` | all |

SI-2 and SI-3 are independent and may run in either order. **Recommended order: SI-1, SI-2, SI-3, SI-5, SI-4, SI-6**
— SI-2 is the cheapest end-to-end proof of SI-1 and the only format whose fixtures can be produced without the
commercial editor; SI-3 then delivers the commercial editor's designs through its own export, and SI-5 makes both
usable while SI-4's riskier work proceeds. If the owner prefers the commercial format first, swap SI-2 and SI-3 —
nothing else changes.

---

## 2. Locked decisions

### D1 — One implementation, below the firewall
Every reader and the build live in `src/Design/Schematic/Import/` (namespace `CircuitRF.Design.Schematic.Import`). The
GUI command, the drop, the CLI noun and the MCP tool all call **one function**, `SchematicImport.Import(...)` (new
cell) or `SchematicImport.BuildInto(...)` (into an open schematic). No view model holds import logic (the
`Authoring.cs` rule; a comment-stripped source scan gates it, as for `import part`).

### D2 — The pipeline
```
file ──classify by content──▶ reader (SI-2/3/4) ──▶ ForeignSchematic   (bytes → neutral model; reads only)
                                                         │
                                       SchematicImportMap (SI-1): each part → native component or symbol cell
                                                         │
                                       SchematicImportBuild: TestBench + ExactPlacement ──▶ NetlistSchematic.Build
                                                         │
                       ┌─────────────────────────────────┴───────────────────────────┐
              Import(new cell): CellCreate + symbol cells + .csch     BuildInto(open schematic): one undo step
```
Readers touch nothing but bytes (the `PcbReader`/`PcbImport` split). Only the import writes files.

### D3 — Part mapping: native where certain, the source's own symbol everywhere else
- **Native** only when the mapping is certain from the source's own evidence (library symbol name, reference prefix,
  value, pin count): two-pin **R, L, C**; **ground** symbols and any net the source names ground → node 0; the
  circuitRF **Port** only where the source draws a port/connector the user marks as one (D9). The table of rules is
  SI-1's `SchematicImportMap` and is data, not code branches per format.
- **Everything else becomes a symbol cell** — one cell per distinct source symbol, its `.csym` built from the
  source's drawing through `KitTemplateSymbol.BuildFromDrawing` (Import Component's own path; `ComponentImport`'s
  symbol half is factored so both call it). The instance is a hierarchical instance of that cell. **It has no model**,
  so `check` reports it and a run refuses it ("cell has no schematic view") until the user gives it one (an SnP, a
  sub-schematic, a model). This keeps the drawing faithful and never invents electrical behaviour.
- A value the native part cannot parse becomes a global variable with the source's text in a note — AS D10's rule.
- **Power rails are net labels, never sources.** `+5V` stays a named net; circuitRF does not invent a supply.

### D4 — Where symbol cells go
Into a folder **beside the target cell**, named `<source-file-stem>_parts/`, one cell per symbol, named after the
source symbol (sanitised by `NameValidator`). A re-import or a second drop **reuses** a cell whose `.csym` is
byte-identical and refuses (naming both) when a same-named cell differs. (Inside the target cell folder was
considered and not chosen.)

### D5 — Pages and hierarchy
- A **flat multi-page** design → ONE schematic, pages tiled left to right in page order with a gap of 20 grid
  squares; off-page connectors become **net labels** of the same name (the connectivity is the label, as in the source).
- A **hierarchical** design → one cell per distinct sheet, each with its schematic and a generated rectangle symbol
  whose pins are the sheet's hierarchical ports; the parent instantiates it. Repeated sheets share one cell.
- Title blocks and page borders are not imported; free text and graphic lines are, as schematic text/lines.

### D6 — Geometry is kept, not re-arranged
Every instance at the source's position and orientation, every wire along the source's own segments
(`ExactPlacement.Instances` / `.Routes`). Units: one schematic unit is one mil (`SchematicAutoRouter.P` = 100 = one
grid square); the readers convert exactly. **A native symbol whose pins do not land where the source symbol's did**
is re-joined by `NetlistSchematic`'s existing at-most-one-dog-leg rule — the source wire is never moved. Whether
`NetlistSchematic`'s origin snap to `P` must be relaxed for sources on a 50-mil grid is measured in SI-1 (R-si1-6),
not assumed.

### D7 — Licensing and naming
- **No commercial editor, vendor or tool is named anywhere in the repo**, the reference converter and the archived
  parser included (their names carry the vendor's). Formats are named by their extension and container: "the binary
  `.DSN` design (CFBF)", "EDIF 2.0.0", "`.kicad_sch`". The menu says **Schematic…**; its file filter lists extensions.
  The addresses of the two reference projects are held by the owner, not written here.
- **No code is translated from either reference project.** Facts about a file format are not copyrightable; each
  reader is written from the format (EDIF 2.0.0 standard; MS-CFB; the published `.kicad_sch` reference) and from
  observation of real files, with the references consulted for what the format means. If an implementer finds a
  routine worth porting, **stop and ask** — porting MIT code needs its copyright notice in `THIRD-PARTY-NOTICES.md`,
  and that notice's wording must clear the vendor-name rule.
- **No new dependency.** The CFBF container is read by a small read-only reader written here (SI-4 R-si4-1); a NuGet
  CFBF library (the common one is MPL-2.0) was considered and not chosen.

### D8 — Connectivity is verified, every import
Every reader yields the source's **own declared connectivity** where the format states it (EDIF's `net`/`joined`;
`.kicad_sch` derives it from wires, junctions and labels exactly as its editor does; the binary `.DSN` per SI-4). After
the build, `NetExtractor` extracts the produced `.csch` and SI-1 compares the two as **sets of pin sets** (net names
are not compared — circuitRF names nets its own way). Any difference is reported in the import's result naming the
pins; the import still completes (the user sees what is wrong rather than nothing). This is the gate every reader's
tests use and it is what makes "imported" trustworthy.

### D9 — Ports
A source connector or port symbol is imported as its symbol cell (D3), never silently as a circuitRF Port: what is an
RF port is the user's decision. The import dialog (SI-5) lists the source's connectors and global ports with a check
box "make a circuitRF Port"; the CLI takes `--port <ref-or-net>` repeatedly. No analysis is added unless at least one
Port exists; then the default SP1 (AS D15's sweep).

### D10 — Drop behaviour
- A schematic file (classified by content as one of the three formats) dropped on an open `.csch` is built **at the
  drop point** (the source's bounding box's top-left there, snapped to `P`), as **one undo step**, with instance,
  net-label and variable names renamed on collision (the rename helper SI-1 lifts out of the image paste, behaviour
  unchanged there). Symbol cells are written into D4's folder beside **that** schematic's cell — a drop writes files,
  and the Messages line says which.
- **Image files keep the existing behaviour** (a placed bitmap). The new handler accepts only the three schematic
  kinds; a gate drops a `.png` and asserts a bitmap and no components.
- Dropped on the **project tree** (a workspace or folder row): the same as File ▸ Import ▸ Schematic… into that
  folder, with the dialog.

### D11 — Non-goals for this series
Writing any of these formats; the source's board/footprint links (`PCB Footprint` properties are kept as instance
properties only); bus expansion beyond what is needed to keep connectivity (a bus becomes its member nets' labels;
unexpandable buses are reported); simulation models from the source (SPICE attachments are reported, not imported —
a follow-up); other schematic formats (the reader interface is the extension point).

---

## 3. UX principles
- **One command, one dialog**: source file, target (new cell name, defaulting to the file stem), the pages/sheets
  found, the ports list (D9), and a preview drawn by the same `NetlistSchematic.Build` the import calls.
- **The result is a report, not a toast**: parts mapped natively, symbol cells created/reused, connectivity check
  result (D8), anything skipped and why — in Messages, one line plus "Details…".
- Never a partial cell: a refusal writes nothing (the `NetlistSchematic` contract).

## 4. Testing rules for the whole series
- Tests are minimal and targeted (one per claim); run only the classes a phase touches, never all of `Ui.Tests`.
- **Committed fixtures are authored for the tests and tiny.** Real design files from users stay local, under
  `testdata/schematic-sources/` (git-ignored like `testdata/image-sources/`), with a `FixtureFact` skip when absent.
- The connectivity comparison (D8) is the primary assertion for every reader; geometry is asserted on a few named
  points only.
- No timing tests (counters only, if a cost matters).

## 5. Housekeeping for every phase
- Findings go in `src/Design/RESOLVED.md` (or `src/Ui/RESOLVED.md` for SI-5), never CLAUDE.md.
- No vendor or tool names in code, comments, tests, fixtures, docs or commit messages (D7); grep before handing back.
- User docs are edited as sources only; DocGen is the owner's at the end of the series.
- Do not commit unless the owner asks.
