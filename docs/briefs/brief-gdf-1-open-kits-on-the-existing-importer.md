# Brief 1 — the open kits on the importer circuitRF already has

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf1-n` · **Phase:** measure, fix, CLI
**Area:** `src/Core/Pdk/` (`PdkImporter`, `PdkFormatRegistry`, `KitSymbolFileReader`), `src/Design/Schematic/`
(`PdkPartInstaller`), `src/Ui/ViewModels/WorkspaceViewModel.cs` (the import sequence at the two
`PdkImporter.Import` call sites), `src/Cli/Authoring.cs`, `src/Cli/Serve/ToolCatalog.cs`, `src/Core/RESOLVED.md`
**Depends on:** — · **Blocks:** 5
**Rule:** overview §0. No kit, foundry, process name, device name or model name in the repo. Fixtures
are synthetic. Results are recorded by process ("kit C: 130 nm CMOS").

---

## 0. Why this comes first

The question "will an open kit's parts appear in the Library palette with symbols?" is answered by
the **upstream** kit trees, not by the gdsfactory packages (overview §3). Kit A's upstream tree is
already proven. Kits C and D have the same shape (text symbol files, SPICE-dialect libraries with
corners, Verilog-A sources), but **nobody has run them through File ▸ Import PDK**. This brief does
that before anything new is built. It measures first and fixes what breaks **in circuitRF**. It never
patches a kit.

## 1. `R-gdf1-1` — get the upstream trees

- Each gdsfactory kit's README links its upstream repository. Clone kits A, C and D shallow, and
  sparse where a tree is large. You need the symbol, model and technology folders, not the
  standard-cell libraries.
- **Kit C keeps its symbols and its models in separate repositories.** Measure whether the importer
  pairs them as they stand: one tree beside the other, the delivery shape `pdk-external-devices.md`
  §1 already describes. Do not invent a merge step before measuring.
- Clones live **outside the repo**. Nothing in the repo (code, tests, comments, RESOLVED) names
  where they came from.

## 2. `R-gdf1-2` — measure, per kit, in a scratch harness

Drive `PdkImporter.Import` from a throwaway harness, which is the established fastest way to see what
a kit holds. Do not use a committed test, because it would name the kit. Record a table per kit:

| Measure | Meaning |
|---|---|
| assets by kind and support | the import report's own classification |
| parts | how many reach the palette |
| parts with a symbol | the answer to the owner's question |
| parts with a model that resolves | the model card or subcircuit found, with corners listed |
| parts whose model needs a compiled `.osdi` | and whether one is present. Upstream trees ship Verilog-A **sources**, and the kit's own install step compiles them. circuitRF compiles nothing on a kit's behalf, so report it |
| unrecognised files | each one by extension, counted |
| layout artwork | which parts get artwork through `cni`, and which upstream cells are written for a different layout tool's macro system that circuitRF cannot run (expected for kit D, and for kit C's layout half) |
| import time | wall clock, cold and warm |

Then, in a scratch workspace, for **one part of each device family** (MOSFET, HBT where present,
resistor, MIM/MOM capacitor, diode, varactor):

1. Place it from the palette.
2. Extract it (`circuitrf netlist`).
3. Run a DC operating point (`circuitrf dc`) with the kit's typical corner.
4. Run `circuitrf explain --analysis` to confirm the model card used is the kit's own and not a
   fallback.

**This is not a numerical validation.** It needs an externally generated reference, which this brief
does not create, and the completion note says so in those words.

## 3. `R-gdf1-3` — fix what breaks in circuitRF

Every failure gets attributed to one of three causes:

- **circuitRF defect.** Fix it, with a synthetic fixture reproducing the file shape, never the kit's
  file.
- **The kit's own defect.** Report it in the import report's own words and change nothing.
- **Out of scope.** For example, layout PCells written for another tool. Report it and change
  nothing.

The kit A bring-up found nine traps, listed in `src/Ui/CLAUDE.md` and the PDK `RESOLVED.md` files.
Read them first. **Several of them are invisible unless a part is placed the way placement places
it** (blank-not-null `ModelLibrary`, text-kind parameters ignored silently, `TYPE` swallowed by the
selector). The harness must place parts through the real placement path, with every declared
parameter present.

Record the findings in `src/Core/RESOLVED.md` or `src/Ui/Schematic/RESOLVED.md`, wherever the fixed code lives (never `CLAUDE.md`).

## 4. `R-gdf1-4` — `circuitrf import pdk`

Brief 5's acceptance harness has to import a kit with no display, and a PDK import is currently
reachable only from a view model. That is not a capability (`CLAUDE.md`, the `new` / `import part`
rule).

- **`R-gdf1-4a`** Extract the import sequence both `WorkspaceViewModel` call sites perform into
  **one** framework-free function in `src/Design`. It covers: `PdkImporter.Import`, the `.cws` PDK
  reference, `KitPCellLibrary.EnsureDeclared`, the technology report and the palette composition
  inputs. The view model calls it. A comment-stripped source scan proves the view model kept no second
  copy (the `AuthoringCliVerbTests` pattern).
- **`R-gdf1-4b`** `circuitrf import pdk <kit-dir> --into <workspace> [--name n] [--allow-code] [--json]`.
  - Prints the report's counts and every refusal sentence.
  - Kit code (`cni` PyCells) runs only with a **recorded** consent or `--allow-code`, which grants
    consent for this one process. It is never recorded (overview D2).
  - Without either, the import still succeeds, and it says which generators were not started and why.
  - `import` stays one verb with nouns.
- **`R-gdf1-4c`** Add it to `ToolCatalog` (the `import` tool's `what=pdk`).

## 5. Tests (one per claim, synthetic)

1. One test per circuitRF defect fixed in §3, each with a synthetic file of the failing shape.
2. `import pdk` run as a process gives the same `.cws` reference and manifest **bytes** as the
   in-process function. The source scan finds no second copy in the view model.
3. Without consent, `import pdk` starts no interpreter. A counting seam on the worker resolver
   proves it.

Run only the classes you touch.

## 6. Completion note (in the brief, by process only)

- The §2 table for kits A, C and D.
- The answer for each kit: **how many parts land in the Library palette with a symbol**.
- The list of kit defects and out-of-scope items.
- The overview §3 paragraph rewritten from "expected" to measured.

---

## Completion note (2026-10-30)

Upstream trees cloned shallow and sparse outside the repo. Kit C's symbols and models were imported as
**two trees side by side under one folder**, which the importer pairs as it stands (corners and
subcircuits are found across the two) — no merge step was needed. **This is not a numerical validation.**
It needs an externally generated reference, which this brief does not create.

### §2 — measured, per kit

| Measure | Kit A: 130 nm SiGe BiCMOS | Kit C: 130 nm CMOS | Kit D: 180 nm CMOS |
|---|---|---|---|
| Assets (read / recognised-not-supported / unrecognised) | 989 (493 / 238 / 258) | 4,795 (1,879 / 289 / 2,627) | 417 (242 / 39 / 136) |
| Parts reaching the Library palette | 43 | 628 — 79 are the device library; the rest are digital standard cells and test benches | 73 |
| **…with a symbol** | **43** | **628** | **73** |
| Parts paired with a kit subcircuit | 25 | 59 | 0 — its devices are primitive elements on `.model` cards, with no subcircuit |
| Corners | 7 axes (one per device family) | 1 axis, 9 sections | 3 axes; the main one appears only after fix 1 below |
| Compiled models | Verilog-A sources only, no `.osdi` shipped. After the kit's own install step compiled three (in the scratch clone), the MOSFET and resistor model routes resolve; two capacitor sources are outside its compile list | none possible from the kit: its MOSFETs are a model level the kit's simulator builds in, with no source shipped | as kit C |
| Unrecognised, top extensions | `.py` 76, `.lvs` 50, `.drc` 42, `.tcl` 13, `.include` 11, `.yaml` 11, `.va` 10 | `.svg` 857, `.spice` 538 (parameter-only include files, which the corners still read), `.json` 346, `.lef` 289, `.tsv` 270, `.data` 261 | `.py` 56, `.csv` 42, `.json` 10, `.yaml` 8 |
| Layout artwork | 38 parametric cell modules found through `cni`; declared; not started without consent. 236 fixed GDS cells, read-not-supported | the layout half lives in a separate tree for another layout tool — not imported | 22 fixed GDS files (10 parts), read-not-supported; its parametric cells are another layout tool's macros |
| Import, read only (cold / warm) | 0.34–0.40 s / 0.20–0.25 s | 6.8–7.3 s / 5.6–5.9 s | 0.13–0.18 s / 0.06–0.10 s |
| `circuitrf import pdk`, whole process | 0.45 s | 8.0 s | 0.28 s |

Kit C's import rose from 2.5 s to 6.8 s with fix 1: its 44 per-corner MOSFET model files (1.1–1.3 MB each)
are now read, where before they were skipped by size.

**One part per family**, placed with every declared parameter (the placement path's seeding), extracted with
`circuitrf netlist`, typical corner selected:

| Family | Kit A | Kit C | Kit D |
|---|---|---|---|
| MOSFET | extracts; **DC solves** (3 iterations) through the kit's own compiled model | not paired; built-in model level → cannot simulate | not paired; as C |
| HBT / BJT | not paired; its card is a type the kit's simulator builds in | paired; its plain BJT card is refused (open item 1) | not paired |
| Resistor | not paired (open item 2) | paired; its resistor cards refused (open item 1) | not paired |
| MIM capacitor | paired; **DC solves** | paired; **DC solves**, with its geometry after fix 6 | not paired |
| Diode | not paired; diode cards refused (open item 1) | not paired | not paired |
| Varactor | **skipped**: 3-pin symbol, 4-port subcircuit — extracts after the body-terminal fix below | paired; its subcircuit uses a node voltage inside a parameter expression → out of scope | not paired |

`explain --analysis` reports the analysis chain only; the extracted `.cnl` is the evidence that the card
used is the kit's own (the card's name and the compiled file are on the device line).

### §3 — circuitRF defects fixed (each with a synthetic test)

1. A file over 512 KB was classified by name only — a kit's whole corner library was invisible.
   `PdkCornerImportTests.I5`.
2. A cloned kit's `.git` folder was enumerated as kit content. `PdkCornerImportTests.I6`.
3. A trailing `$` comment on one `+` continuation line dropped the rest of a `.param` block (64 process
   constants on kit C). `SpiceReaderHygieneTests.H19`.
4. Bound resistor/capacitor values were composed with spaces, so every netlist extracted from a kit
   holding one could not be read back — the GUI's Simulate included. `SpicePassiveModelBindingTests.B9b`.
5. A capacitor card's `CAP` was not read (every kit-D capacitor left unbound). `…B5b`.
6. A placed kit part's values were dropped when the symbol spelled a parameter in a different case from
   its subcircuit (kit C: every one). `KitPartParameterCaseTests`.
7. Headless runs never loaded a workspace's kits — no schematic with a kit part could be netlisted or run
   outside the window. `ImportPdkCliVerbTests.APlacedKitPartExtractsHeadlessly`.
8. `netlist` and the run verbs never printed extraction conflicts — a skipped instance simply vanished.

Recorded in `src/Core/RESOLVED.md`, `src/Design/RESOLVED.md` and `src/Cli/RESOLVED.md`.

### §3 — open circuitRF items, NOT fixed: each changes numerics or a pairing policy, so the owner decides

1. **Inside a kit netlist, a device on a plain card type circuitRF implements natively (diode, BJT,
   sheet resistor) is routed only to a compiled model**, and refused with advice to compile Verilog-A the
   kit does not ship. `SpiceModelCardTranslation` already binds these cards elsewhere. Routing them there
   is a numerical change that needs a reference, and must not turn a card stating a different
   formulation (another BJT level) into the one circuitRF has. This is the single largest cause of kit
   parts not simulating, in all three kits.
2. **A record-format symbol is paired to its subcircuit only by the name-token matcher**, so a single-word
   part name never pairs, although the symbol's own format line names the subcircuit. Pairing on that
   statement is a policy change the repository has reverted once.
3. **Kit D's devices are primitive elements on `.model` cards**; the kit-part path has no route for that
   shape at all (it rides on item 1).
Items 1–3 are carried by [brief GDF-1b](brief-gdf-1b-kit-devices-on-plain-model-cards.md). Two more found
here were fixed in this brief after review:

- **A symbol's body terminal** (`@pinlist @body @model`: 2 drawn pins, a 3-port subcircuit) is wired to the
  net its parameter names, instead of the part being skipped — kit A's varactor now extracts.
  `KitPartSubcircuitBindingTests`, `KitSymbolFileReaderTests.K21`.
- **An escaped quote inside a record-symbol attribute** (`\\"`) no longer ends the value — kit C's MOSFET
  symbols now yield all four geometry expressions instead of one truncated value.
  `KitSymbolFileReaderTests.K20`.

### §3 — kit defects and out-of-scope items (reported, nothing changed)

- Kit D ships four regression-test templates in its model folder that are not netlists; reported as
  unreadable.
- Kits C and D: MOSFET models that the kits' simulator builds in, with no source — circuitRF compiles
  nothing and implements none of them.
- Kits A and C: varactors whose parameters depend on a node voltage.
- Layout cells written for another layout tool (kit D; kit C's layout tree); fixed GDS cells
  (read-not-supported).

### §4 — `circuitrf import pdk`

`PdkWorkspaceImport` (src/Design) is the sequence both GUI call sites run; `import pdk` calls it, writes
the `.cws` reference and the cell declaration byte-for-byte as the in-process call does, and starts kit
code only with a recorded decision, `--trust-kit` or `--allow-code`. The MCP `import what=pdk` mode has no
`allowCode` (cli.md §34). Gate: `tests/Ui.Tests/Cli/ImportPdkCliVerbTests.cs`.

**The answer — parts that land in the Library palette with a symbol:** kit A **43 of 43**, kit C **628 of
628** (79 devices), kit D **73 of 73**. Placeable is not simulatable: see the family table and open item 1.
