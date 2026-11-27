# circuitRF — PCell Designer: a schematic of primitives as a parameterised cell

**Status:** built (series PCD-0 to PCD-8) · **Date:** 2026-11-27

The schematic-driven layout series (SDL) made a schematic of primitives draw a joined layout and be solved and
optimised by EM. The PCell Designer makes such a schematic a **designed cell**: a PCell whose layout is the planner's
output at the instance's values, whose circuit model is its own schematic, and which goes to another workspace as a
**PCell package** (`.cpcell`). The contract is `docs/briefs/brief-pcd-0-overview-and-contract.md`; each lane's brief is
`brief-pcd-<N>-*.md`. The user page is `docs/user/src/reference/pcell-designer.md`.

## 1. The series rules

| Rule | What it says | Where it holds |
|---|---|---|
| **S1 — Everything is a function of the parameters** | Parts joined by a net are placed pin to pin; a part no net joins carries an offset placement; a drawn shape's outline and pins are expressions; a drag writes an expression. The saved `.clay` is the planner's output at the defaults. SDL's "never distort to close" and "no auto-routing" stand | `SchematicLayoutPlanner.Offsets.cs`, `OffsetPlacer`, `MShapePCell`, the designer's Save |
| **S2 — One model** | The cell's schematic is its circuit model, extracted as a `define` like any hierarchical cell. Nothing is fitted or stored beside it. `MSHAPE` has no circuit model unless `Model="open"` (pins open) or `Model="short"` (pins one node) | `SchematicCircuit.CnlTextOf`, `EmModelSwitch.WithoutCircuitModel` |
| **S3 — The same interface as any PCell** | Placed through `IPCellGeneratorResolver` and the `.ccell` `LayoutGenerator` path, so the Library tile, parameters, handles, regeneration, LVS and EM need no designer branch. The PCell contract is unchanged | `DesignedPCellResolver` |
| **S4 — Existing files read and save unchanged** | Every new field is additive and omitted when unset; no `FormatVersion` bump | `CellPersistence`, `SchematicPersistence`, `Pcd1SeamsTests` |
| **S5 — Deterministic** | Same cell, values and technology: same layout, same generated cell, same `.cpcell` bytes | `DesignedCellLayout.At`, `PCellPackage.Export` |
| **S6 — Minimal tests** | One scoped test per R-tag; the end-to-end acceptance (`Pcd8AcceptanceTests`) is the main check | `tests/Ui.Tests/PCellDesigner/` |
| **S7 — circuitRF's own naming** | PCell Designer, designed cell, offset placement, drawn metal (`MSHAPE`), Stretch by Parameter, PCell package, generator prefix `cpcell:` | throughout |
| **S8 — Decisions for the series** | **D-cell:** a designed cell is a PCell in its own workspace from the moment it is made (`LayoutGenerator = "cpcell:<Name>"`, `EmCopper = true`); export only packages the folder. **D-drawing:** the schematic drawing follows the netlist; a kept designator keeps its place and every non-netlist field. **D-numbers:** parameters are numbers; `Choices` is a check error | `PCellDesign.Create`, `CellNetlistMerge`, `PCellDesignCheck` |

## 2. What each lane decided

### PCD-1 — the seams, EM copper, the step floor

- The seams commit changed no signature of the contract. `DesignedCellLayout.At` takes the database unit, the technology
  reference and the display unit from the cell's primary `.clay`; with none, the default unit, no reference and the
  technology's display unit. Read and argument errors become a refusal, never an exception.
- `EmPartKinds`: a generator cell whose `.ccell` has `EmCopper`, or a `pdk://` part whose in-memory `.ccell` has it, is
  copper. Copper is decided before the model-cell test, so the cell's schematic (its circuit model) is never read as its
  layout.
- The two-database-unit step floor also applies with no Path A (placed EM cells): the coarsest unit of the placed
  layouts, carried on `EmEvaluationRecord.DbuPerMicron`, over every length coordinate, a placed cell's own parameter
  (`X1.Lpad`) included.
- The no-circuit-model sentences state the fact and the remedy, without naming a 3D-parametric cell as the only cause.

### PCD-2 — offset placement

- The offset step runs after the chains and before the rows, on chains and lone parts alike. A placed unit is one rigid
  body and joins its anchor's row group.
- One offset drives a unit (the first in schematic-id order); a second one in the same unit, an anchor inside its own
  unit, an unresolved anchor, an expression that does not evaluate and an anchor cycle are each a Warning ending "the
  offset was not applied", and the part goes to the rows. Success is the Info line "placed by its offset from X pin k".
- Poses are doubles until the instance is written, so an all-zero offset is exactly a joint.
- `Drag` measures in the anchor pin's frame and writes in `offset.Unit`, rounded to one database unit: a plain number
  becomes the new number, a trailing `± n` is updated, any other expression gains `+ n` or `- n`; `g` → `g + 3` → `g`,
  never `-0`.
- The root is the anchor of the first unit not placed by an offset (`SchematicLayoutPlan.Root`), stated as one Info line.
- Known limit: a chain already in an existing layout does not follow its offset on a re-run (SDL-4's "the layout's own
  placement stands"). A designed cell always plans onto an empty layout, so it is never affected.
- `MSHAPE` reaches the planner through its own word branch (the quoted `Form` and `Model`).

### PCD-3 — `MSHAPE` and Stretch by Parameter

- The `.cnl` line is `MSHAPE:SH1 <one net per PinX row> Form="polygon" W=0 um X[1]=… Y[1]=… … PinX[1]=… PinY[1]=…
  PinW[1]=… PinDir[1]=180 deg … Model="none"`. `SignalLayer` and `GroundReference` are not written to the line.
- **The two words are written quoted.** The elaborator evaluates every key of a primitive as an expression; a string
  literal passes as text, an unquoted word is an unresolved name. This differs from the contract's §4.3 table, which
  shows them bare. Every reader strips the quotes.
- Pin k exists while the instance has a `PinX[k]` row; the symbol, the line's net count and the generator's pins all read
  this rule. The parameter editor's **+** adds corners; a pin is added as rows.
- The Core model stamps nothing, so the pins are open. `WithoutCircuitModel` names every `MSHAPE` whose `Model` is absent
  or not `open`; a run by circuit models is then refused with "SH1 has no circuit model…". `Model="short"` (added by
  PCD-9) ties every pin to pin 1 by a 0 V branch, so the pins are one node, and it counts as a circuit model.
- Stretch is additive: the old expression stays and gains `(P − P0)·cos θ`, `(P − P0)·sin θ`; two bindings on one corner
  add. Corners move, pins stay.
- LVS kind: a `TransmissionLine`, as every drawn conductor. No substrate is injected. The catalogue now has 33 microstrip
  parts.

### PCD-4 — the text merged back, and pin drop

- The text is read by `CnlReader` and drawn by `NetlistSchematic.Build` (the code behind `netlist --to-schematic`); the
  baseline is the schematic's own extraction read the same way, so values the extraction adds are never stored.
- Same nets: the wires are kept. Different nets: kept parts stay where they are and the wires are auto-routed; a net no
  label named stays unlabelled (`ExactPlacement.QuietNets`, now honoured by auto-routing).
- Variables and measurements merge by row; analyses, tuning, the technology and the cell's parameters do not, and an
  edited analysis line becomes a note. A designator whose type changes is refused.
- A pin drop is a merge: the part gets its kind's registry prefix (MLIN → `ML1`), its pin joins the target's net, a drop
  on a grounded pin puts it on ground. A drop on a joint makes the tee (PCD-11).
- A cell instance cannot be added by typing it in the text; an existing one merges normally.

### PCD-5 — the generator, the resolver, the package, the check

- `Create` refuses an existing folder. `Export` runs the check first and refuses on any error. `Import` never overwrites;
  the same content is `pcell.already-imported`, with the cell folder still returned.
- The package is the folder renamed to its cell: `manifest.json`, `<Name>/.ccell`, `<Name>/schematic/<Name>.csch`,
  `<Name>/symbol/<Name>.csym` (or `AutoSymbolGenerator`'s), sorted, deflated, every timestamp 1980-01-01. The packaged
  `.ccell` names no primary and carries `Terminals` (`PCellDesign.TerminalsOf`) and `NumPorts`.
- The content key is one function of the folder (schematic bytes, parameters, sorted generator keys); import compares the
  folder as unpacked, not the manifest's text, so two builds that version a primitive differently still recognise their
  own import.
- Resolver ids are case-insensitive. Discovery reads the root, its `LibraryRefs` and referenced workspaces four levels
  deep; a name in two folders resolves to nothing and `Describe` names both. A cell that places itself is refused by a
  per-thread stack.
- Nested planner runs (a bench placing a designed cell) do not deadlock: no gate is held while a generator runs, and
  `Register` takes its gates in a fixed order. Nested cells are keyed on the technology's content.
- Found: `NetExtractor` made a cell's Ports into its pins only for EM-capable cells, so an imported package had no
  terminals in LVS. It now does so for a cell with a `LayoutGenerator` too.

### PCD-6 — the window

- Tools ▸ PCell Designer… (both Tools menus), File ▸ Import ▸ PCell Package…, and Edit in PCell Designer on the
  schematic and layout context menus (in the layout, on the selected placement). One window per cell folder.
- A new cell is `PCell<k>` with no name prompt; import goes into the workspace root.
- The view model owns everything a test touches; the window turns pointer events into its calls. Undo is a snapshot of
  the `.csch`; a refused gesture pushes nothing.
- The Value column reaches the planner as an unsaved VAR row (`__PCDVALUES`), so unsaved values drive the canvas.
- Selected corners are listed in a text line, and Stretch by Parameter takes inline fields; there is no canvas overlay.
- Save writes the `.csch`, the `.ccell` (parameters, `PCellDesign`, `Terminals`) and the `.clay` at the defaults.
- PCD-11: a new cell starts with Port 1; a drop anywhere is an offset from the nearest pin, in literals of the cell's
  length unit; a drop on a joint makes an `MTEE` (an `MCROSS` on a tee); **Draw MSHAPE** makes drawn metal from clicked
  corners (`PCellAuthoring`).
- PCD-10: the response keeps its markers across a re-run by trace name and x, and **Dimensions** draws each parameter as
  a ruler over the canvas (`PCellDimensions.Of`: stretch bindings, offsets, and the primitives' own handles).

### PCD-7 — `circuitrf pcell` and the MCP tool

- One verb with five nouns (`new`, `check`, `render`, `export`, `import`); each calls the function the window calls, and
  a gate compares the process against the in-process call byte for byte. Exit 0 done, 1 refused or a finding at the
  threshold, 130 render cancelled.
- `render` reaches `render`'s own layout path through `Render.RunLayout(args, documentPath, view)`, so a designed cell
  at values is drawn by the one renderer with every viewport, layer and theme flag, and no temporary `.clay` is written.
- The MCP tool `pcell` takes `what=`. `ServeProtocolAdapterTests` gained `pcell` and two rows it was missing.

### PCD-8 — the example, the docs, the acceptance check

- `examples/PCell Designer/` holds `PadTee` and a bench placing it twice. It is built by the functions the window calls
  (`Pcd8ExampleAuthoring`, run only with `CRF_AUTHOR_PCD8=1`), so the files are what a user would get.
- `Pcd8AcceptanceTests` runs PCD-0 §8's six steps in one test.

### PCD-9 — the showcase cells

- `Wilkinson3`, `Spurline`, `DualModePatch` and `Slotline` are built by `Pcd9Cells.Design`, using the steps of the
  window: Create, the text applied, the table typed, offsets and flips set on the schematic, the text applied again,
  Stretch by Parameter, Save. The text view carries no placement, so an offset or a flip is set on the part.
- A bend's turn and a tee's branch side come from the drawing's chirality, not from the text: the sign of MBEND's
  `Angle` does not turn it. Flipping a part moves its pins off its wires, so the text is applied again afterwards. That
  redraws the wires and keeps every part's orientation and offset.
- An offset's `Dx` or `Dy` that uses a parameter with a unit takes that unit (var-unit-wins), so with `s` in mm,
  `s+0.55` is `s` + 0.55 mm. A part's own expressions evaluate in SI.
- The design check found joined parts by coincident pins. A footprint's pin is at its pad's centre, so a footprint
  looked unjoined. The check now also counts the pad's outer-edge joint (`PadJoint`), which is where the planner joins
  it.
- A planar 3-way Wilkinson with a star of resistors cannot keep all three outputs outside the star (K2,3). One resistor
  is a 1206 whose body bridges the middle arm.
- A slotted ground is drawn copper: two `MSHAPE`s on the ground layer, on net 0, because a polygon has no hole. A bench
  placing only a designed cell draws no pour, and the generator leaves the cell's own pour out, so the slot stays open.
  Planar cannot solve it: its ground is the unbroken plane of the Green's function. A 3D view turns the drawn ground into
  solids.

## 3. The acceptance check and the real EM run

`Pcd8AcceptanceTests` runs the six steps of PCD-0 §8 in one test, and all six pass (about 40 s, the EM solver a
stand-in). Step 6 tunes the placed parameter `X1.Lpad` itself, on the bench's own layout by EM. Two findings about the
contract:

- **`Form` and `Model` are quoted** (PCD-3 above).
- **A pad with `Model="open"` is invisible to the circuit model.** Its pins are left open, so `Lpad` changes the copper
  and not the circuit. Step 5 therefore expects `X1`'s S21 with `X1.Lpad = 200 um` to equal the one with
  `X1.Lpad = X2.Lpad` (S2), and a parameter of the lines, `Lstub`, to change it. For the same reason `em=correct`
  cannot move `Lpad`: it searches on the corrected circuit model, which gives `Lpad` no slope, so it stops at its start
  after one solve. Use `em=direct` for a variable that only moves EM-only geometry (§5).

The real Planar runs on the example are recorded in §5.

## 4. Possible enhancements

- **Offset and flip in the text view.** Every showcase cell needed its `.csch` edited for them. Carrying them in the
  `.cnl` would let a designed cell be authored as text alone.
- **A cut on `MSHAPE` for the ground pour.** A slot drawn on its own needs the ground drawn around it in two shapes. A
  shape that cuts the pour would make a slot one part, and would keep it open under a pour.

- **Repetition and conditional shapes** (finger arrays, an optional stub). A Python PCell placed inside the designed cell
  covers them today. Built in, they would make the part count a function of a parameter, so the netlist, the port count
  and LVS's terminal map would change with a value, and one schematic would no longer be one model (S2).
- **A constraint solver for placement.** S1 already makes every position an explicit expression. A solver adds
  configurations with several or no solutions, an order-dependence S5 forbids, and a second placement engine beside the
  planner.
- **Models fitted from EM.** `em=correct` already corrects the primitives' models by EM during optimisation. A fitted
  parametric model needs a sampling plan and a model format of its own, which is the Model Composer's territory.
- **Offset and Stretch in the `.cnl`, and an Offset editor in Properties.** The text view does not carry them (they are
  placement, not circuit), and today an offset is written in the `.csch` and then dragged. Carrying them in the text
  needs a Core grammar change; a Properties row needs only the UI, and is the cheaper first step.
- **Choice parameters on a designed cell.** `EmEvaluation.Apply` applies real values only (D-numbers); a choice needs
  string values through the planner's scope.
- **A declared circuit model for `MSHAPE` beyond `open`** (a pad capacitance, a Touchstone file). It would give a pad's
  length a slope in the circuit model, so `em=correct` could move it instead of stopping at its start (§3, §5).
- **Pins that ride a stretch.** A pin on a stretched edge stays where it is; moving its `PinX`/`PinY` with the corners
  would keep a launch's pin on its edge without a second binding.
- **Arcs in `MSHAPE`.** A bulge per corner, as `MCURVE` draws, would give round pads without many corners.
- **A corner editor on the canvas** (insert, delete). The parameter rows already hold the corners; a canvas editor is a
  second way to write them, worth it once shapes with many corners are common.
- **A parameter sweep overlay** (the response at Minimum, default and Maximum at once), which shows a range's effect at
  a glance at three times the cost of the live response.
- **Nets from Update Schematic from Layout outside the designer**, for pins that meet in any layout. `PinDrop` is the
  seam it would call.

## 5. The real Planar runs

Made on copies of `examples/PCell Designer`: `circuitrf new layout Bench --from-schematic --em-setup` (the bench's layout
and its Planar setup, 2,700 unknowns, both placed cells solved as copper), `SP1` set to `model=em` over 6-8 GHz at 3
points, `tune X1.Lpad min=100 um max=600 um`, one goal `dB(SP1.S(1,1)) le -15`, then `circuitrf opt`. No result was
kept.

| | `em=correct`, simplex, budget 3 | `em=direct`, pattern, budget 8 |
|---|---|---|
| EM solves | 1 | 8 (the budget) |
| Wall time | 91 s | 9 min 19 s (about 70 s a solve) |
| `X1.Lpad` | 200 µm → 200 µm | 200 µm → 600 µm (its Maximum) |
| Worst \|S11\| over 6-8 GHz | −0.22 dB | −0.22 dB → −0.13 dB |
| Cost | 0.896 | 0.896 → 0.841 |
| Why it stopped | "the next point is the same geometry as the last one solved" | the budget was spent |

`em=correct` (run with `X1.Lpad = Lp1` and `tune Lp1`, before a placed parameter could be tuned on the bench's own layout)
stops at its start, as §3 says: the corrected circuit model has no slope in `Lpad`. `em=direct` searches on EM and moves
`Lpad` the way EM says, monotonically up to its Maximum. The goal itself is out of reach for this one variable: the band
sits on the stubs' resonance, which the pads only pull a little. The two runs agree at their shared start (200 µm, cost
0.896, the same geometry key 1974bbb9…).

The geometry also had to suit a calibrated port: in the first version of the example the tee's stub ran beside the feed,
2.16 mm (4.25 substrate heights) from port 1, and the solve was refused for a feed that is not isolated. The example's
tee now turns its stub away from `TL1`.
