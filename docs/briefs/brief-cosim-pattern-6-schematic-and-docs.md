# Brief CP-6 — The run makes the schematic; the editor; the docs

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp6-<n>` · **Depends on:** CP-3 (CP-4 and CP-5 for the docs' tuning and sweep sections)
**Area:** `src/Design/Layout/Em/EmCoSimNetlist.cs`, `src/Design/Layout/Em/EmSnpSchematic.cs`, `src/Cli/NetlistToSchematic.cs`, `src/Ui/ViewModels/FieldsBodyViewModel.cs`,
`src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml`, wherever `check`'s schematic findings live, `docs/user/src/reference/{cosimulation,antennas,em-3d}.md`, `docs/design/{sar,cli}.md`

## 0. Why

After CP-3 the method works, and it takes hand-work the product already knows how to do: placing the block, wiring each part to the right
port, and writing the analysis. `EmCoSimNetlist` already writes all of that as `<setup>_cosim.cnl`, with each part's model from the board's
schematic, but it is a netlist file and not a schematic someone can edit. The `_sim` cell gives every port a TermG, so a part port has no
part on it.

## 1. Requirements

**R-cp6-1 — The netlist's block.** `EmCoSimNetlist.Text` writes `EmFieldData` on the run's record when the run kept a far field or a field
set, with its port names, and `SnP` otherwise.

**R-cp6-2 — A `<setup>_cosim` cell.** When a finished driven run has component ports and the setup's `CreateSnpSchematic` is on, make a cell
from that text, drawn by the code `netlist --to-schematic` uses. That code is in `src/Cli`; move the drawing to `src/Design` so `EmRunService`
can call it, leaving `src/Cli/NetlistToSchematic.cs` as arguments and reporting. An existing cell is never overwritten, as `EmSnpSchematic`
already holds. `_cosim.cnl` keeps being written. The `_sim` cell is still made for a run with no component ports.

**R-cp6-3 — Its analyses and plots.** The netlist and cell carry the S-parameter analysis they have today. With a far field, they also
carry `analysis F1 type=fields block=BOARD drive=1`, at the stored frequency nearest the sweep's centre, and a note naming the stored
frequencies. The cell's Data Display holds S11, a polar cut of `F1.farfield.U` and `RealizedGainDbi`. A placeholder part keeps its existing
comment and `check` warning.

**R-cp6-4 — The analysis editor.**
- The radio button reads **Fields (SAR, B1, pattern)**.
- Frequency offers the block's stored frequencies from its record, plus **All** when it has a far field, and still takes typed text.
- `norm` and `average` grey when the record has no SAR field set. They keep their place: no control appears or moves.

**R-cp6-5 — `check` warns on a part between two gap ports.** Using the record's port kinds, an element directly joining two `Gap` ports'
nodes is a warning naming the part and both ports. It says that a gap port's negative end is a pad and not ground, and that the part belongs
from one node to ground. A network on a port with a real return (`Plane`, such as a feed) is not flagged. An adopted record, with no
port kinds, gives no finding.

**R-cp6-6 — Docs, stating what is true with no history.**
- `cosimulation.md`: retitle for B1, SAR and pattern; the pattern workflow; what can be placed across a gap; what a shunt branch in an `.s2p`
  does on a gap port; the frequency rule; the limits G4–G7 measured, and which solvers passed; working from saved results, including
  what survives a failed run and how to read a result's settings back.
- `antennas.md`: a short section, *Changing part values without another run*, linking there.
- `em-3d.md` (*A port across each part's own pads*): one sentence and a link.
- `sar.md` §11 and `cli.md` §26: the far-field branch.
- A worked example only if a small board meets G4–G5 quickly enough to ship.

## 2. Gates

- A headless `em` of a small board with `CreateSnpSchematic` makes a cell; `fields` on it gives the same cubes as the same circuit written
  by hand; a second `em` leaves an edited cell alone.
- The source scan that holds `Authoring.cs`-style verbs also holds `NetlistToSchematic`: no second copy of the drawing.
- `check` finding, positive and negative, one line each.
- UiSnapshot of the analysis editor with a far-field record and with a SAR record, inspected: no control shifts.
