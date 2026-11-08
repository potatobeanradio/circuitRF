# Brief ISC-1 — Create SnP schematic: include the schematic's own components

**Tag:** `R-isc-<n>`
**Area:** `src/Design/Layout/Em/EmSetupModel.cs` + its persistence (one new setup flag), `EmRunService.Run` /
`RunThreeDView` (`WithSnpSchematic`, `WithCoSim`), `EmCoSimNetlist.cs` (the per-part decision, extracted so it is shared),
a new `src/Design/Layout/Em/EmSchematicWithParts.cs`, `src/Design/Cells/CellCreate.cs` (called, not copied),
`src/Ui/Views/Layout/EmSetupEditorView.axaml` + `EmSetupEditorViewModel.cs`, `src/Cli/DocumentSchema.cs`.
Findings go in `src/Design/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/Em/`.

---

## 0. Why

After an EM run, the next step is usually to try the result in the schematic the board was drawn from. Today the
designer does that by hand. They copy the schematic, delete the lines and tees the EM result now stands for, place an
SnP block, and wire each Port and each part to the right SnP pin. That wiring is easy to get wrong: a part on a component
port goes from its SnP pin to ground, not between two nets, and a swapped pin gives an answer that looks normal.

The run already holds what that circuit needs:

- **Create SnP schematic** (brief DFR31-4) makes `<stem>_sim`: an SnP block, a TermG per port and an S-parameter analysis.
- **Component ports** (briefs CP-6, LCP-7) write `<stem>_cosim.cnl`. They also make a `<stem>_cosim` cell, with each part's
  instance read from the cell's Primary schematic (`EmCoSimNetlist.ModelsFor`) and stood from its port's node to ground.
  That covers `.c3d` setups, Planar `.cem` setups and 3D `.cem` setups.
- **Schematic Ports become EM ports** labelled with their `Num` (brief SDL-5).
- **`EmModelSwitch.KindOf`** sorts each schematic part into copper (the EM result stands for it), a part with a footprint
  (it stays, across its component port) or a part with no layout.

What is missing is a cell built **from the user's own schematic**. The `_cosim` cell is drawn from scratch by
`NetlistSchematic.Build`. It loses the sheet's arrangement and the schematic's analyses, VARs, measures and goals. Its
Port lines are renumbered 1…k in result order, not matched to the schematic's `Num`. Parts with no layout are dropped. A
run without component ports gets only `_sim`.

## 1. Decisions

- **D1 — Setup flag `IncludeSchematicComponents`**, default off and omitted when off. It is read only when
  `CreateSnpSchematic` is on. It is stored in the `.cem` and in an embedded `.c3d` setup alike, and stripped from a 3D
  run's record the way `CreateSnpSchematic` is (`C3dPersistence.SerializeForRun`). The panel shows **Include pre-existing
  schematic components**, indented under **Create SnP schematic when the run finishes**. It is always enabled, because it also
  decides what **Create now** (D11) makes, which needs no tick on the run's box.
- **D2 — Which schematic.** The Primary schematic (`CellFolder.ResolvePrimary`) of the cell the EM document belongs to:
  for a `.cem`, the cell of the layout it names; for a `.c3d`, the cell the `.c3d` sits in. Generalise `ModelsFor` to take
  that cell folder, so the `_cosim` netlist and this cell read the same schematic.
- **D3 — One cell per run.** With the flag on and a Primary schematic found, the run makes `<stem>_sch` **instead of**
  `<stem>_sim` or `<stem>_cosim`. The `_cosim.cnl` netlist is still written exactly as today. Without a Primary schematic,
  the run makes today's cell and adds one sentence naming the cell it looked in. An existing `<stem>_sch` is never written
  to (DFR31-4's rule).
- **D4 — The sheet is the user's sheet.** Start from a copy of the Primary `.csch` model. Kept components keep their
  position, rotation, mirroring, name and every parameter. VARs, cell parameters, analyses, measures, goals, tune and
  optimise lines and the technology reference are copied unchanged. Two exceptions: an analysis stating `model=em`
  (SDL-6) is written `model=circuit`, because in this cell the SnP block already is the EM result; and a schematic with no
  S-parameter analysis gains one over the setup's sweep.
- **D5 — What each component becomes**, by `EmModelSwitch.KindOf` and the run's ports:
  | Component | In the new cell |
  |---|---|
  | Copper (lines, tees, bends, a sub-cell with a layout) | Removed |
  | A Port whose `Num` matches an EM port's number | Kept where it is, joined to that SnP pin |
  | A cell Pin whose `Num` matches an EM port's number | The same. A cell made to be placed in a test bench marks its board ports with Pins (the shipped PCB Component Ports board does); such a cell gets no S-parameter analysis (nothing in it can drive one) and takes a copy of the original's symbol, so it can stand where the original is placed |
  | A part with a component port | Kept where it is. Its positive pad's pin joins its SnP pin and its reference pad's pin goes to ground. Each pin of a multi-pad part is wired as `_cosim.cnl` wires it. Open and Short are as `_cosim.cnl` |
  | A part with a footprint but no component port | Kept, with its pins left open, and named in a warning (the EM result has no port at its pads) |
  | A part with no layout (an ideal source, a model with no footprint) | Kept with its own wires. Pins that only touched removed copper are left open and named in a warning |
  | An EM port with no matching Port (drawn by hand, or a connector) | A new Port at the next free `Num`, Z from the Touchstone, beside its SnP pin. Named in a warning |
  | A Port with no matching EM port | Kept, open, named in a warning |

  The decisions in the component-port row (model across the port, multi-pad mapping, Open/Short, a placeholder when there is
  no model) come from **one function**, extracted from `EmCoSimNetlist.Text`, which both the netlist and this cell call.
  There must be no second copy of that decision.
- **D6 — Ports match by number, not by order.** An EM port's number is its label's `Num`: the label text for a Planar
  port, the source number (`SourceNumber ?? Number`) for a 3D one. `EmCoSimPort` gains that number. A kept Port keeps its
  own `Z`, which terminates the board and is what the user designed for. The SnP block renormalises from the Touchstone's
  reference, as any SnP does.
- **D7 — Connections are net labels.** Each stub is placed against the sheet as it stands: it may not end on or pass
  through a pin, a wire, a label, another new stub or a component's body, and turns or lengthens until it is clear. A wire's
  end on another wire is a connection, so a stub drawn straight out of a pin can short an SnP node to ground on a tight sheet
  (it did, on the shipped PCB Component Ports board). A part pin that goes to ground and already sits on the user's ground
  symbol keeps it. Each SnP pin gets a short stub and a net label `p<k>` (`EmCoSimNetlist.Node`). Each
  kept Port and component-port pin gets a stub and the label of its node, and each reference pin gets a ground symbol. If
  the schematic already uses a name `p<k>`, every label takes the prefix `em_`. The original wires on a net that touched
  copper, a component-port part or a Port are removed with that net's labels and dots. A net whose members are all kept
  components with no layout keeps its wires untouched.
- **D8 — Where the SnP block goes.** At the centre of the removed copper's bounding box, snapped to the grid. Its pin
  arrangement is chosen as `EmSnpSchematic` chooses it, by which side each port faces. If nothing was removed, it goes to
  the right of the sheet's extent.
- **D9 — The layout is checked against the schematic first.** The result is only right if the layout and schematic agree:
  a part renamed on the board or a port renumbered gets wired wrong with no error. `LvsRun.Run` runs on the cell before
  the copy is made. Every error it finds, and every `lvs.device.*` warning, is a warning on the result: LVS compares by topology
  and calls a part renamed on one side a warning, while this cell matches parts to component ports by name, so that is exactly
  the difference that matters. The cell is still made, because a layout part way through drawing is normal.
- **D11 — Create now.** A button beside the run's check box makes the same cell without a run (`EmSchematicNow`), for a run
  that finished with the box clear. It reads the setup's existing Touchstone (the file for the port count the setup now has,
  else the newest of its results stem). With none, the cell is still made: its block names the path the run will write
  (`EmRunService.ResolveSnpPath`) and a warning says the file does not exist yet. The ports are found the way a run finds them,
  with nothing meshed or solved (`EmRunService.PlannedPorts`, `C3dProblemAssembly.Assemble`). When the cell exists, a dialog
  asks: **Replace** writes over the cell's Primary schematic (other views, Data Display and results stay), **Create** makes a
  cell under a typed name that starts as the first free one, Cancel makes nothing. Replace is refused while that schematic is
  open in an editor. A run never asks; it leaves an existing cell alone (DFR31-4's rule).
- **D10 — The schematic's Data Display comes too.** If the Primary schematic has an authored Data Display, it is copied
  through `CellCreate.WriteTemplateDataDisplay`, repointed to the new cell's results. Without one, the cell gets DFR31-4's
  template.

## 2. Requirements

- **R-isc-1** — The flag, its persistence, the panel check box (D1), and `DocumentSchema.cs` plus the `em-setup` format
  reference.
- **R-isc-2** — Extract the per-part decision from `EmCoSimNetlist.Text` into a function both callers share (D5). Gate:
  `CoSimC3dBytesTests` and `PlanarCoSimTests` pass unchanged, so the netlist is byte-identical.
- **R-isc-3** — `EmSchematicWithParts.Create(workspaceDir, setup, cellDir, snpPath, ports, lvs)` builds and writes the cell
  through `CellCreate.Create` (D3–D8, D10). It returns an `EmSnpSchematicResult` with its warnings. It never throws.
- **R-isc-4** — `EmRunService.WithSnpSchematic` / `WithCoSim` call it when the flag is on, on every route: Planar `.cem`,
  3D `.cem` and `.c3d`. LVS runs first (D9). `circuitrf em` prints the sentence and warnings with no code of its own.
- **R-isc-5** — `explain` on a `.cem` or `.c3d` with the flag on reports, as part of the walk, which Primary schematic
  the run would use, or that there is none.
- **R-isc-6** — Messages: "Created cell `<name>` from `<schematic>`: S`N`P of this run, `k` Port(s), `m` part(s) on
  component ports, `j` warning(s)", with Open on the schematic.

## 3. Gates

The Planar board from LCP-1 (about 2 s) is the only solved fixture. Every other gate builds the cell from a fixed Touchstone
and a port list, with no solve. There is no Palace run, no new `Category=Benchmark` test and no timing assertion. Run the
named test classes only.

1. **Same circuit as `_cosim.cnl`.** LCP-1's board with a Primary schematic in which every part has a component port and
   the Ports are numbered in result order. The new cell's S-parameters equal `_cosim.cnl`'s to 1e-9 in every entry.
2. **By number.** Two ports, no component ports, schematic Port `Num` 1 on the right-hand line end. S11 of the new cell is
   the Touchstone's S22, to 1e-9.
3. **The sheet is reused.** Every kept component's position, rotation and parameters, and every VAR, analysis and measure,
   are equal to the Primary's. An analysis with `model=em` comes back `model=circuit`.
4. **Off-board parts.** An ideal source on a net with no layout keeps its wires as they were. An ideal R that joined a line
   is kept with that pin open, and the warning names it.
5. **Fallback and never-overwrite.** With no Primary schematic, today's cell is made and the sentence names the cell
   folder. A second run leaves `<stem>_sch` alone.
6. **3D route without a solve.** A `.c3d` with component ports: the ports from `C3dProblemAssembly.Assemble` and a fixed
   Touchstone give a cell wired as in gate 1.
7. **LVS.** A layout with one part renamed: the cell is made and the result carries the LVS warning.
8. **Off means unchanged.** A setup without the flag writes byte-identical `.cem` files, netlists and cells on every path.
9. **Looked at.** Render gate 1's cell with `tools/UiSnapshot` and look at it before reporting: labels readable, nothing
   on top of the SnP block, no stub crossing a kept component.

## 4. Docs

- `docs/user/src/reference/em-setup.md`: the check box, which schematic it reads, the table in D5 written for users, and
  the warnings.
- `docs/user/src/reference/cosimulation.md`: the new cell as the way to put a board's EM result into the schematic it
  came from; when `_cosim` is still the right choice.
- `docs/user/src/reference/antennas.md`: wherever it describes the Create SnP schematic outcome.
- `docs/design/cli.md`: `em`'s output gains the sentence; `explain`'s walk gains the schematic.
- `src/Design/RESOLVED.md`: what was found.
