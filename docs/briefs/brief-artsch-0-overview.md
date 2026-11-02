# Brief series — Create Schematic from Artwork (AS-1 … AS-9, AS-11; optional AS-10)

**Status:** written, not started · **Date:** 2026-10-08 · **Decisions:** locked (§2), owner-reviewed
**Requirement tag:** `R-as<n>-<m>` (phase n, requirement m).
**Design note to be written by AS-3:** `docs/design/artwork-to-schematic.md` — this overview is its source.
**Builds on:** the trace impedance review (`src/Design/Layout/Em/TraceImpedanceAnalysis*.cs`,
`TraceCrossSection.cs`, `LineCalculator.cs`), the board interchange readers (`src/Design/Layout/Interchange/` —
Gerber, Excellon, `PlacementFile`, `BomFile`, `RefdesCell`), railRF's part discovery
(`src/Design/RailRf/RailPartDiscovery.cs`, `RailSeriesPartition.cs`, `PartLibrary*.cs`), the footprint work
(`src/Design/Layout/Footprints/` — `SmtCaseTable`, `ChipLandPatternGenerator`, `LayoutPartKind`), the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs`, AA-6), the microstrip family and its stackup binding
(`src/Core/Devices/Microstrip*`, `src/Design/Schematic/MicrostripSubstrateInjection.cs`,
`ViaSubstrateInjection.cs`), and the L5 sync commands (`src/Ui/Layout/SchematicToLayoutGenerator.cs`,
`LayoutToSchematicGenerator.cs`).

---

## 0. What the owner asked for (paraphrased)

A user imported a production board's Gerbers, placed ports at every part, ran the planar MoM engine and was
refused at about two million unknowns. The board itself is simple: about ten SMT parts joined by 50 Ω lines,
with a top-side ground pour stitched to the bottom plane by hundreds of vias. Nothing on it needs an EM run.

The owner wants a command that turns flattened artwork (an imported Gerber, a flattened GDSII, a `.kicad_pcb`
read back as copper) into a **schematic of native circuitRF components** that simulates in seconds:

- passive parts recognised as R, L, C (from placed footprints, the placement and BOM files, the land patterns,
  and later the silkscreen);
- traces recognised as **MLIN**, with **MBEND, MTEE, MCROSS, MTAPER** where the artwork has them, all bound to the
  artwork's `.ctech`;
- **grounded coplanar waveguide and stripline recognised as such**, with new circuit models for both;
- irrelevant vias (ground stitching) dropped; vias that matter (a shunt part's ground, a layer change) kept as
  **VIAGND / VIA**, with plain **GND** as the alternative;
- not perfect, and not expected to be. A best attempt that the user then checks and cleans up.

A designer who works with eval boards added: the point is a light model, between "a single impedance number" and
brute-force EM, good enough to predict a matching network and compare a datasheet's target against measurement,
reliable to about 2 GHz on ordinary boards; segment the routed trace between parts into line sections; offer a
line form that takes a **physical length rather than an electrical angle**; and pick the part's case size from the
existing footprint list.

Owner answers on 2026-10-08 (paraphrased):
- **TLIN is the general fallback** for any cross-section no circuit model covers; **GCPW and stripline get real
  models in this series**.
- **Silkscreen OCR comes late** (AS-10, optional).
- **A parts table** the user reviews and edits before anything is generated.
- **Target:** a new cell, prompted for its name. When the artwork's own cell has **no schematic view**, the user
  may instead write straight into it, to start a basic schematic there and verify and clean it up afterwards.
- **Unknown values become global variables.**
- A trace can be misread as GCPW when a top-side ground pour comes close to it (or the other way round), so the
  user needs an **easy swap between line types in the schematic**, keeping the geometry (W, L) fixed and letting
  Z0 follow — useful on any schematic, not only a recognised one.

---

## 1. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| AS-1 | `brief-artsch-1-cpwg-and-stripline-models.md` | `CPWG` and `SLIN` components: models, symbols, stackup binding, golden references, Line Calculator support | — |
| AS-2 | `brief-artsch-2-tlin-physical-length.md` | TLIN's physical form (`L`, `Eeff`, per-length loss) — the general fallback line | — |
| AS-3 | `brief-artsch-3-board-graph.md` | The recognised board: ground, via classes, signal islands, ports, scope; the design note | — |
| AS-4 | `brief-artsch-4-parts-and-parts-table.md` | Parts from every evidence source, series/shunt, case, value, model; the parts table and its CSV | AS-3 |
| AS-5 | `brief-artsch-5-traces-to-line-elements.md` | Trace chains → MLIN / MBEND / MTEE / MCROSS / MTAPER / CPWG / SLIN / TLIN | AS-1, AS-2, AS-3 |
| AS-6 | `brief-artsch-6-emit-and-target-cell.md` | The netlist, variables, testbench, drawing, the target cell, `FromArtwork` and the L5 commands | AS-4, AS-5 |
| AS-7 | `brief-artsch-7-cli-and-mcp.md` | `circuitrf recognize` and the MCP `recognize` tool — **agent-ready here** | AS-6 |
| AS-8 | `brief-artsch-8-gui-command.md` | Design ▸ Create Schematic from Artwork…, the dialog and parts table, cross-probe, the MoM refusal's pointer | AS-6 |
| AS-9 | `brief-artsch-9-acceptance-docs-example.md` | Round-trip acceptance, the field fixtures, the user pages, an example workspace | AS-7, AS-8 |
| AS-10 | `brief-artsch-10-silkscreen-ocr.md` | **Optional, late.** Refdes read from stroked silkscreen text, as one more evidence source | AS-4 |
| AS-11 | `brief-artsch-11-swap-line-type.md` | Swap Line Type: MLIN ↔ CPWG ↔ SLIN ↔ TLIN in place, W and L held, Z0 free | AS-1, AS-2 (AS-6 for measured gaps) |

AS-1, AS-2 and AS-3 are independent and can run in parallel. AS-4 and AS-5 are independent of each other. AS-7 and
AS-8 are independent of each other. AS-11 can be built as soon as AS-1 and AS-2 land; it is useful on its own.

---

## 2. Locked decisions

### D1 — Recognition produces a netlist; the drawing already exists
The new work is **artwork → circuit**. The circuit is a `TestBench` in memory (the type `CnlReader` produces), which
`NetlistSchematic.Build` already draws as a `.csch` with built-in symbols, orthogonal wiring and every net labelled,
and whose round trip through `SchematicCircuit` is its own contract. No second schematic writer. The `.cnl` text of
the same testbench is a first-class output (CLI `-o x.cnl`), because it is the thing a test can compare and an agent
can read.

### D2 — One implementation, below the firewall
Everything that decides anything lives in **`src/Design/Layout/Recognition/`** (framework-free), called by the GUI
command, the CLI verb and the MCP tool alike. `src/Ui` holds the dialog, the parts-table view model and the
cross-probe only. A rule that lives only in the dialog is a rule the CLI does not apply; the CLI chapter's "no
second route" rule holds, and AS-7's gate is byte identity between the two.

### D3 — Pipeline
```
artwork (.clay + .ctech [+ .cem] [+ placement/BOM])
   │
   ├─ AS-3  board graph   — ground net, via classes, signal islands, ports, scope
   ├─ AS-4  parts         — evidence → parts table (editable; CSV)
   ├─ AS-5  lines         — TraceImpedanceAnalysis chains → line elements
   │
   └─ AS-6  emit          — TestBench (+ globals, tune entries, sparam analysis)
                             └─ NetlistSchematic.Build → .csch in the target cell
```
The trace review is **reused, not re-derived**: its pieces, chains, junctions, end kinds ("via", "pad",
"junction", "open end"), per-station Z0/εeff, gaps and `Configuration` strings are exactly what AS-5 needs. Where
it lacks something (a junction's member list, for instance), AS-5 adds it to the analysis's output records rather
than walking the copper a second time.

### D4 — Target cell
- **Default: a new cell** in the artwork's workspace, name prompted, default `<artwork cell>_model`. Created with
  `CellCreate` — the function New Cell calls.
- **Into the artwork cell** is offered **only when that cell has no schematic view**. It writes the cell's primary
  schematic view.
- **Re-running** into a cell whose schematic was written by this command (its provenance block says so, D12)
  replaces it after a confirmation, with a **history checkpoint first** (`history checkpoint --intent`, the
  revision-control floor). A schematic this command did not write is **never** replaced: that is a refusal naming
  the cell, in the GUI and the CLI alike.

### D5 — `FromArtwork`, and why the L5 commands must honour it
Every component the command creates carries **`FromArtwork=true`** (a stored component flag, visible and clearable
in the parameter editor as "Models existing artwork"). Its meaning: *this component models copper that already
exists; layout sync neither creates, updates nor deletes artwork for it.*
- **Update Layout from Schematic** skips a `FromArtwork` component and counts it in one report line. Without this,
  a recognised schematic written into the artwork cell would have its MLINs generated as PCell copper on top of the
  imported Gerber.
- **Update Schematic from Layout** does not create a component for a layout instance whose `RefDes`/`SchematicId`
  names an existing `FromArtwork` component (it would duplicate the recognised part), and says so.
- Clearing the flag on a component hands it back to ordinary sync. Nothing else changes.
The command **never writes the layout**: no links, no `SchematicId`s, no edits to the `.clay`.

### D6 — Ground
The ground net (node 0) is, in order of evidence: a net the artwork names as ground (`GND`, `AGND`, `0`, case
insensitive, when shapes carry nets); else the connected component holding the **largest-area copper on the
stackup's reference conductor**; with every island joined to it through vias included. Every pour and plane
`TraceImpedanceAnalysis` already skips as a pour is ground unless it is galvanically separate from that component
(then it is a signal island and the report names it). The dialog and the CLI can name a different ground net
(`--ground <net>` or a click on copper).

### D7 — Vias
| A via that joins … | becomes |
|---|---|
| ground to ground (stitching, fences, plane ties) | **dropped**, counted in the report |
| a signal island on one layer to a signal island on another | **VIA**, stackup-bound by `ViaSubstrateInjection` |
| a **ground pad that is its own island** (not a pour) to ground | **VIAGND**, one per via, the nearest 4 at most; the rest counted |
| a signal island directly to ground (a shorted stub, a shorted line end) | **VIAGND** |
A part's ground pad that sits **on a pour** is a plain **GND**: the pour is ground and its stitching is already
dropped. Policy switch `vias=model|ground`: `ground` turns every VIAGND into GND (the "regular GND as an
alternative" the owner asked for). Mutual inductance between neighbouring VIAGNDs is not modelled, and the report
says so once when any pad has more than one.

### D8 — Ports
Ports are found, in priority order, from: (1) the cell's `.cem` ports when one exists; (2) layout pins and port
labels; (3) **a multi-pin part's pins** that touch a recognised RF line (D9); (4) a line that ends at the board
outline or at a connector footprint (an edge launch); (5) a line that crosses the **selection boundary** (D11).
Every port is `Port … Z=50 Ohm`, numbered in the order: (1)-(2) as stated, then left-to-right, top-to-bottom.

### D9 — Multi-pin parts are cut out
An IC or any part with more than two pads is **not modelled**. Each of its pads that touches a recognised RF line
becomes a Port, so the schematic is *the networks around the device as the device sees them* — what matching work
needs. The user can drop an SnP onto those ports afterwards, or bring the device in with the existing **Import
Component** (File ▸ Import ▸ Component…, `circuitrf import part`, `ComponentImport.Import` in `src/Design`), which
imports a vendor library's symbol (and footprint) as a cell, and wire that cell to the ports by hand. Pads on ground join ground. Other pads (bias, control)
are left open and listed. A DC connector's pins are left open with a note: the decoupling parts already present
provide the AC ground.

### D10 — Parts evidence and the parts table
Evidence, strongest first: (1) **placed footprint instances** on the layout (`LayoutInstance.RefDes`, `PartKind`,
the generated `smt-<case>` cell); (2) **placement + BOM** through the existing readers, with their refusals intact
(an unstated placement origin is asked, never guessed); (3) **land-pattern match**: a two-pad copper pair (with paste
or mask openings where the artwork has them) matched against `SmtCaseTable` / `ChipLandPatternGenerator` at every
density variant → case code and orientation; (4) silkscreen refdes (AS-10); (5) nothing.
- **Kind** from refdes prefix (R, L, C, FB → L, D, …), the placed `PartKind`, the BOM description, in that order;
  else **unknown**, shown as `?` and generated as C.
- **Connection** is measured: one pad on ground → shunt; both on signal → series; more than two pads → multi-pin (D9).
- **Value** from the BOM's parsed value (the existing `BomFile` parse, its "never acts on a guess" rule intact); else
  unknown.
- **Unknown values become global variables**, `<Refdes>_<Param>` (`C6_C`, `L2_L`, `R1_R`), with a `tune` entry
  (tuning series) so they appear in the Tuning and Optimizer panels at once. The initial value is **transparent**:
  series C = 100 pF, series L = 0.1 nH, series R = 0 Ω; shunt C = 0.01 pF, shunt L = 1 µH, shunt R = 1 MΩ. The first
  simulation then shows the lines alone and every unknown part is a knob, which is the matching workflow the
  designer described.
- **Model**: a BOM part number that resolves in the workspace's `.crlib` (railRF's part library) or names an `.sNp`
  in the workspace (file name begins with the part number) → **SnP**, two-port, port 2 grounded for a shunt part;
  else the ideal R/L/C. No case-size parasitic models in v1.
- **The parts table** is a document: a CSV with a fixed header (AS-4), the same table the dialog edits and the CLI
  writes (`--parts-out`) and reads (`--parts`). The format is the contract, so a user or an agent edits it the same
  way.

### D11 — Scope
The whole layout, or the **current selection** (GUI) / `--region x0,y0,x1,y1` with SI units on every coordinate
(CLI, `render --window`'s spelling and its bare-number refusal). Copper outside the scope is not read. A line that
crosses the scope's boundary is cut there and becomes a Port (D8 (5)) — the designer's "clip a section" case without
any EM.

### D12 — Provenance and anchors
The written schematic carries a provenance block: the source `.clay` (relative), the scope, the options, the parts
CSV's hash, the circuitRF version, the time. Each created component carries its **artwork anchor** (a DBU point or
polyline, stored as a component property the elaborator ignores), which is what AS-8's cross-probe uses in both
directions. The provenance block is also what marks a schematic as this command's to replace (D4).

### D13 — Line element choice
Per trace segment, by the **majority `Configuration`** of its solved stations (the trace review's own names):
| Configuration | Component |
|---|---|
| `microstrip`, `microstrip with coplanar ground on one side` | **MLIN** (and its discontinuities, D14) |
| `grounded coplanar waveguide` | **CPWG** (AS-1); gap = the stations' mean gap; left/right gaps differing by more than 1.5× → TLIN |
| `stripline` | **SLIN** (AS-1), offset as measured |
| anything else (`stripline with coplanar ground`, `coplanar waveguide (no ground plane)`, a reference above, mixed or partial references, a stackup the injection cannot bind) | **TLIN physical form** (AS-2), Z0 and εeff from the cross-section solve, loss estimated |
A trace whose configuration changes along its length is split where it changes. Every TLIN fallback is counted in
the report by reason.

**The coplanar reading is the user's to choose** (`Auto` / `Microstrip` / `GCPW`, and the gap factor under `Auto`,
default 3·H — AS-5 R-as5-2). Whatever is chosen, every MLIN and CPWG records the **measured gaps**, so a misread
line is one swap away (D19).

### D14 — Discontinuities and simplification
- **MLIN regions:** bends → **MBEND** (angle and miter measured; a chamfered 45° pair is two 45° bends), three-way
  junctions → **MTEE**, four-way → **MCROSS**, a linear width ramp longer than 2·W → **MTAPER**. **MKLOPF is not
  recognised** (a curved taper becomes MTAPER or stepped MLINs). Width steps **abut** (no step model in v1).
- **CPWG / SLIN / TLIN regions:** a bend is centre-line length; a junction is a plain node. Noted once.
- Collinear pieces of one width class (`TraceImpedanceAnalysis.MergeToleranceMicrons`) merge. A piece shorter than
  max(W, 100 µm) is absorbed into its longer neighbour. **No line is ever dropped for being short**: a short thin
  line is an inductor.
- A line ends at the **pad edge** of a part; the pad itself is not modelled in v1.
- Two lines running parallel within 3× their gap for more than λ/20 at the analysis's top frequency are reported as
  *coupled, modelled uncoupled*. Coupled-line models are not in this series.

### D15 — The testbench
The emitted circuit carries an S-parameter analysis: the range from the cell's `.cem` when it has one, else
**100 MHz – 6 GHz, 201 points**, editable in the dialog (`--start/--stop/--npts`). Port impedance 50 Ω. A
measurement block is not written.

### D16 — The report
Every guess and every omission is stated with a count: vias dropped, VIAGNDs capped, TLIN fallbacks by reason,
unknown kinds, unknown values, multi-pin parts cut, open pins, coupled pairs, copper read as nothing. In the GUI it
goes to Messages (one line per class, expandable); in the CLI to stderr and `--json`. **A recognition is never
refused for being imperfect**: it is refused only when there is no technology, no copper in scope, or no port.

### D17 — Validation
- **Committed fixtures are synthetic**: AS-9's round trip draws a known schematic, generates its layout, exports
  Gerber, imports it, recognises it, and compares topology and S-parameters with the original.
- **Field boards** (the eval boards used to shape this series) are third-party artwork and are **never committed**.
  They live under the git-ignored `testdata/artwork-boards/` and their tests are `FixtureFact`s that skip with a
  reason on a fresh clone.
- The new line models follow the repo's validation rule: externally generated references, committed as data (AS-1).

### D18 — Non-goals for this series
Coupled lines, differential pairs, step / open-end / gap discontinuity models, case-size parasitic models,
multi-pin device models, placing an imported component in the recognised schematic automatically (Import
Component stays a separate, manual step — D9), PCells for CPWG and SLIN, recognising MKLOPF, and anything that writes the layout.

### D19 — Swap Line Type
A line component (MLIN, CPWG, SLIN, TLIN) can be swapped to another of the four **in place**: instance name,
wiring and flags kept; **W and L held**; Z0 follows from the new model. A parameter the target lacks is
remembered on the component, so a swap and its inverse are the identity. CPWG's gap comes from the measured artwork
gap when the component has one. One function in `src/Design` (AS-11), a context-menu row and the parameter
editor's type combo in the GUI.

### D20 — A schematic may name its own technology
A schematic today always takes the workspace default technology. An imported board's cell carries its own
`.ctech`, so a recognised MLIN would silently be computed on the wrong substrate. A `.csch` gains an optional
`TechRef` and a `.cnl` a `technology "<path>"` statement, resolved **before** the workspace default by every
stackup injection — the order a layout already uses. The recognised schematic names the artwork's technology.
Absent, everything behaves exactly as today (AS-6 R-as6-1).

---

## 3. UX principles

- The command is one menu row, **Design ▸ Create Schematic from Artwork…**, enabled when a layout view is focused.
  It sits beside, not inside, Update Schematic from Layout: that command syncs linked instances, and this one
  recognises artwork that has no links.
- **No explanatory prose under readouts** in the dialog: values, a short status, a note only where there is no value.
- The parts table is the centre of the dialog. Selecting a row highlights the part on the layout.
- The result opens focused, with the report in Messages and every unknown value already in the Tuning panel.

---

## 4. Testing rules for the whole series

- The **minimal** tests: one per claim, not per rung; trim `InlineData`.
- Run **only the classes the phase adds or touches** (`--filter "FullyQualifiedName~<Class>"`); never the full
  suite, never all of `Ui.Tests`. Rebuild each test project before `--no-build`.
- **No new timing benchmark tests.** A performance claim asserts a **counter** (stations solved, cuts shared).
- Synthetic layouts built in memory wherever a claim allows it; the round trip of AS-9 is the one end-to-end gate.
- No EM runs anywhere in this series. That is the point of it.

---

## 5. Housekeeping for every phase

- Findings go in the relevant `RESOLVED.md`, never in a `CLAUDE.md`. Paraphrase the owner; never quote.
- **No commercial vendor or product names** anywhere: code, docs, tests, commit text, fixture names. The field
  boards' folder names under `testdata/artwork-boards/` are chosen locally and generically (`board-a`, `board-b`, …).
- No personal paths in the repo.
- No commits unless the owner asks; commits go to `main`.
- Edit `docs/user/src` sources where a phase changes user-visible behaviour; **do not run DocGen**.
- All algorithms written in-house under MIT; no GPL or copyleft code, none copied.
