# Brief PCD-4 — The netlist text merged back into a schematic, and layout-first pin drop

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd4-n`
**Area:** `src/Design/PCellDesigner/CellNetlistMerge.cs`, `src/Design/PCellDesigner/PinDrop.cs`,
`src/Design/Schematic/NetlistSchematic.cs` (one hook: quiet nets when auto-routed)
**Depends on:** PCD-0 and PCD-1's seams commit. The one `MSHAPE` case depends on PCD-3's kind.

---

## 0. What this adds

The designer's text view shows the cell's `.cnl`; an edit there has to come back into the schematic without losing the
drawing. And a part dropped on a pin in the layout canvas has to become a part in the schematic on that pin's net. Both
go through one function, so a pin drop is a text edit the user did not have to type.

## 1. Requirements

- **`R-pcd4-1` — `CellNetlistMerge.Merge(current, cnlText)`.** The text is merged into a copy of `current` by
  designator. Parameters come from the text; a designator added or removed adds or removes the part; a designator whose
  type changes is a refusal (rename it instead). When the text's nets equal the schematic's, the wires are kept;
  otherwise they are redrawn, and every kept component keeps its position, orientation and non-netlist fields
  (`Offset`, `Stretch`, label offsets and visibility, `CellRef`, footprint, `CellInterfaceHash`). `current` is never
  changed; a refusal returns no schematic.
- **`R-pcd4-2` — `PinDrop.Apply(current, part, partPin, targetInstance, targetPin)`.** The part gets the next free
  designator for its prefix. Its pin `partPin` joins the net on `targetInstance`'s pin `targetPin`, which is a new net
  when that pin is unjoined. A target pin already joined to another part's pin is a refusal naming that pin: no tee is
  invented (SDL S2).

## 2. Decisions

- **D1 — Reuse, no parser.** The text is read by `CnlReader` and drawn by `NetlistSchematic.Build`, the code behind
  `netlist --to-schematic`. The baseline is the schematic's own extraction (`SchematicCircuit.CnlTextOf`) read the same
  way, so both sides of every comparison come from one reader. A line the reader skips with a warning is a refusal.
- **D2 — Nets are compared by name, pin by pin.** Equal means the same designators, and each with the same net on each
  pin, as the two texts name them. A renamed net counts as a change: a net's name is part of the circuit.
- **D3 — Parameters merge by name.** For a kept part, each parameter the text states replaces the schematic's
  expression and unit (its display flags stay). A parameter the baseline stated and the text no longer does is removed,
  so it returns to its default. A parameter the extraction never writes (a symbol-only setting) is kept.
- **D4 — How the schematic is redrawn when the nets change.** `NetlistSchematic.Build` draws the text with an
  `ExactPlacement` that pins every kept designator at its current position, rotation and mirror, and `AutoRoute`, so
  `SchematicAutoRouter` wires every net. New parts are arranged by `Build` as `netlist --to-schematic` arranges them.
  Each kept part is then the current component (every non-netlist field) with D3's parameters. The wires, net labels,
  dots and ground symbols are the drawing's; everything else the schematic holds (canvas objects, tuning, technology,
  settings, components the netlist does not carry) stays as it was.
- **D5 — Quiet nets.** A redraw labels only the nets a person named: a net the baseline had that no net label in the
  schematic names is drawn without a label when its wires reach every pin (`ExactPlacement.QuietNets`, now honoured by
  the auto-routed wiring as well). A net name new in the text gets a label.
- **D6 — Variables and measurements merge by row name** into the existing VAR and MEAS blocks (a new row goes into the
  first block, a new block is the drawing's). Analyses, tuning, the technology and the cell's parameters are not in the
  merge: they are edited where they live, and a changed analysis line is a note, not a silent drop.
- **D7 — A pin drop is a merge.** The part is extracted beside the schematic, far from it, so its line is the one the
  extraction writes (substrate and all), with its own pins on nets of their own. Its `partPin` is put on the target's
  net and the text goes through `Merge`; the part's own nets are quiet. Ground (`0`) is a reference, not a joint: a drop
  on a grounded pin puts the part's pin on ground.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd4*.cs`, one per behaviour: parameters merged with the wires kept; a part added and
one removed with a redraw that keeps positions and non-netlist fields; a type change refused; a pin drop on an
unjoined pin and on a joined one; an `MSHAPE` dropped on a pin and merged (after PCD-3).

## 4. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~PCellDesigner.Pcd4"` under the build lock.
