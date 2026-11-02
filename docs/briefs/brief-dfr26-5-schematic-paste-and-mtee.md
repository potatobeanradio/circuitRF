# Brief DFR26-5 — Schematic: paste at the pointer, a tee breaks the wire it lands on, the tee and cross model their arms

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-5-<n>`
**Area:** `src/Ui/Schematic/SchematicPasteGeometry.cs`, `src/Ui/ViewModels/SchematicViewModel.cs` (`PasteFragment`
~5651), `src/Ui/Schematic/SeriesPartInsertion.cs`, `src/Ui/Commands/Schematic/CutWireSpanCommand.cs`,
`src/Core/Devices/MicrostripTeeModel.cs`, `MicrostripCrossModel` (same folder), `src/Core/Devices/ComponentModelFactory.cs`
(~2530), `src/Design/Schematic/ComponentTypeRegistry.cs` (MTee/MCross parameters ~3130),
`src/Design/Layout/PCells/MTeePCell.cs`, `MCrossPCell.cs`, the schematic-import builders in `src/Design/Schematic/`.
Findings go in `src/Ui/RESOLVED.md` (editor) and `src/Core/RESOLVED.md` (models).
**Tests:** new `tests/Ui.Tests/Schematic/SchematicEditRound26Tests.cs`, `tests/Core.Tests` microstrip model classes,
and the existing paste, series-insertion and connectivity suites (`--filter Suite=SchematicConnectivity`).

---

## 0. The gap

1. **Paste lands almost on top of the original**, not at the pointer, and shorts nets. Reported twice; still in 1.5.
   It is the designed rule: `SchematicPasteGeometry` keeps a fragment that is fully in view where it is, offset by ONE
   connection-grid step. One grid step puts pasted pins onto the original's wires and pins. The layout editor already
   pastes as a ghost that follows the pointer until a click places it (`LayoutEditorViewModel.Clipboard.cs`,
   `_pastePlacementInstances`) — the designer expects the schematic to do the same.
2. **Dropping an MTEE onto a wire does not break the wire,** so pins 1 and 2 are shorted by it.
   `SeriesPartInsertion` only qualifies a part with exactly two pins on one straight segment; the tee has three.
3. **An MTEE whose branch has no length does not simulate like the MLIN with the same shape.** The designer's
   expectation, which the owner shares: **the same geometry must give the same answer.** Cause: the tee's `L1`, `L2`,
   `L3` are drawn in the layout (`MTeePCell`) but **the circuit model ignores them** — `MicrostripTeeModel` takes only
   `W1..W3` and the parameter comment says the lengths are "ARTWORK only". A tee drawn with 2.9 mm arms simulates as
   a bare junction with no line length. `MCROSS` has the same gap with `L1..L4`.
4. **Deleting part of an imported schematic leaves its net labels** (N49 … N56) floating on the sheet.
5. **Copying an imported schematic to another sheet breaks it:** the large IC symbol and the test point become
   placeholder boxes, and wires no longer meet their pins.

## 1. What to do

### R-dfr26-5-1 — Paste follows the pointer

Ctrl+V (and Edit ▸ Paste while the pointer is over the canvas) arms a ghost of the fragment that follows the pointer,
snapped to the connection grid, and a click places it — the layout editor's pattern. Escape cancels. Edit ▸ Paste
with the pointer off the canvas places at the view centre as today. Nothing connects while the ghost floats. Keep
`SchematicPasteGeometry`'s on-grid guarantee and its "never exactly on an existing component's origin" nudge for the
placed result. Duplicate (Ctrl+D) may keep its current offset — say in RESOLVED which you chose.

### R-dfr26-5-2 — A part whose through pins land on one wire breaks it

Generalise `SeriesPartInsertion` from "exactly two pins" to "exactly two of its pins on one straight segment, and the
rest off it". An MTEE dropped with pins 1-2 along a wire cuts the span between them; pin 3 is left for the user. All
the existing safety conditions (nothing else in the span) stay. An MCROSS dropped along a wire cuts between the two
collinear pins on that wire. Add both to the series-insertion tests.

### R-dfr26-5-3 — The tee and the cross simulate the arms they draw

The model becomes **arm lines + junction**, measured to the reference planes the junction model already uses:

- Through arms: MLIN sections of length `L1 − W3/2` and `L2 − W3/2` (widths `W1`, `W2`), branch `L3 − max(W1,W2)/2`
  (width `W3`); the same for the cross's four arms. The layout's clamps (`ClampArmLength`) already keep these ≥ 0.
- The junction (the existing Gupta/Garg/Chadha star network) between them.
- **When the branch has no metal past the through line** (its length to the reference plane is zero), the tee is two
  lines `L1` and `L2` joined at one node, with pin 3 at that node and **no junction parasitics**. An MTEE with pin 3
  open then equals an MLIN of length `L1 + L2` — exactly, not approximately. State in RESOLVED why dropping the junction
  there is right (the drawn shape is a straight line) and what happens on the way to that limit (a short branch still
  carries the junction).
- **Compatibility:** a tee from a file written before the `L` parameters existed has no `L` values. Keep its simulation
  exactly as today (junction only) when no `L` is declared, so no saved result changes; report once per instance that
  the arms are not modelled.
- Use the existing MLIN model for the arm sections; do not write a second line model.

### R-dfr26-5-4 — Imported schematics: delete takes the labels, copy keeps the symbols

- Find whether the imported net labels belong to the deleted parts' wires (then deleting the part should take a label
  whose wire went with it, as it does for a drawn schematic) or are free labels the importer placed (then they should be
  anchored to their wire on import). Fix at whichever end is wrong.
- Copying an imported schematic's parts to another sheet must carry the symbols they use. The placeholders mean the
  target sheet cannot resolve the imported symbol — the imported symbols probably live beside the source sheet only.
  The clipboard payload must carry or re-point the symbol references (layout's copy already carries cell directories:
  `BuildCopyInstancesPayload`). The wires then meet the pins because the symbols are the right size.

## 2. Tests

1. Paste arms a ghost at the pointer; a click places it there on grid; Escape leaves the sheet unchanged.
2. MTEE dropped along a wire: the span between pins 1 and 2 is cut; with a junction inside the span it is not.
3. MTEE (`L3` at its minimum, pin 3 open) equals MLIN of `L1 + L2` to 1e-9 in S over 0.1-10 GHz.
4. MTEE with a real branch differs from today's junction-only result by exactly the arm lines (compare against
   MLIN + old junction + MLIN built by hand).
5. An MTEE with no `L` parameters simulates exactly as before.
6. The same three claims for MCROSS.
7. Deleting an imported part takes its net label; copying an imported part to a new sheet keeps its symbol.

Run the new classes, the microstrip model classes in `Core.Tests`, and `--filter Suite=SchematicConnectivity`.
The model change is numerical: add the `testdata/` regression the root `CLAUDE.md` asks for (the MLIN-equality case is
the reference, since it is exact).

## 3. Text

`docs/user/src/reference/schematic-editor.md` (paste), `components.md` (MTEE and MCROSS: the arms are simulated).
Regenerate those pages only.

## 4. Out of scope

A new tee model with dispersion or a wider εr range.
