# Brief DFR22-2 — Create Schematic from Artwork: a drawing that looks like the board

**Series:** designer feedback round 22 (2026-10-20) — DFR22-1 units, DFR22-2 artwork → schematic drawing, DFR22-3
layout round trip. · **Tag:** `R-dfr22-2-<n>`
**Area:** `src/Design/Schematic/NetlistSchematic.cs` (`Place`, `PlaceExact`, `Wire`, `WireExact`, `LabelPoint`),
`src/Design/Layout/Recognition/` (`RecognitionEmit`, `RecognitionTarget.DrawCircuit`, `LineJunctions`,
`RecognitionCircuit.Hints`), `src/Ui/Recognition/` (`RecognitionSessionViewModel` create path, `RecognitionOptionsRow`,
`CreateSchematicFromArtworkViewModel`), `src/Ui/ViewModels/SchematicViewModel.cs` + `src/Ui/Controls/SchematicCanvas.cs`
(net-label drag), `docs/design/artwork-to-schematic.md`, `docs/user/src/reference/artwork-to-schematic.md`
**Tests:** `tests/Ui.Tests/Recognition/`, `tests/Ui.Tests/Schematic/`; `Suite=SchematicConnectivity` after any wire change

---

## 1. Goal

On a four-port LNA evaluation board (two RF ports, two bias ports, eight parts, CPWG lines) the recognised circuit was
right but the drawing needed long manual clean-up before anyone could check it against the board:

1. **Placement does not follow the board.** The raw drawing is one diagonal staircase: the main path, then each branch
   as a further row. The designer asked for the parts to sit roughly where they sit on the board, so the check
   afterwards is a side-by-side comparison.
2. **Wire loops.** A bias branch (line → line → port to ground) came out with the port beside the line and a wire
   looping round it, where a straight vertical chain was wanted.
3. **Net labels far from their wire**, and no way to drag one back — a label can only be re-anchored by
   double-clicking a wire and retyping.
4. **No tee at junctions.** Microstrip junctions already become MTEE/MCROSS (`LineJunctions`), but this board is
   CPWG, so every junction was a plain node ("not every arm is microstrip").
5. **No progress during Create**, and Create recognises the board a second time after the preview already did.

Round 22's fixes to rotate/drag connectivity (`src/Ui/RESOLVED.md`, round 22 entry) made the clean-up safe; this brief
removes most of the need for it.

## 2. Requirements

### Placement — "As on the board"

**R-dfr22-2-1 — An Arrange choice for artwork.** The options row already offers **Arrange** for a schematic picture
(`RecognitionSessionViewModel.ArrangeOptions`, brief-img-10). Offer it for artwork too, with two values:
**As on the board** (new, the default) and **Tidy** (today's main-line-and-hangers drawing, unchanged byte for byte).
`recognize` gains `--arrange board|tidy` for artwork (it exists already for pictures).

**R-dfr22-2-2 — How "As on the board" places.** Build an `ExactPlacement` from the circuit's artwork anchors
(`RecognitionCircuit.Anchors`/`Hints`, DBU, y up) and draw with the existing `PlaceExact` + `WireExact` path that
schematic pictures use — no third placement algorithm.
- **Scale.** One uniform board→schematic scale per drawing, chosen so the closest pair of part anchors is at least
  one glyph plus labels apart (measure from the items' `Full` boxes), snapped to the connection grid. Flip y.
- **Orientation.** A two-pin part turns to its `PadAxisDeg` (already measured, `RecognitionEmit.PadAxisKey`) rounded to
  the nearest quarter turn; a line element turns to its trace direction at its anchor.
- **Grounds and ports** sit on their pins as today.
- **Collisions** fall back per instance (`PlacementFallbacks`), and the report says how many were arranged
  automatically — exactly the picture path's contract.
- **Scope.** A selection-scoped recognition places relative to the selection's box, not the whole board.

**R-dfr22-2-3 — No wire loops.** Reproduce the loop first: a chain of two series lines ending in a shunt port to
ground, hanging off a junction on the main path. Find whether it comes from `Place` (the hanger is placed beside its
own predecessor instead of below it) or `SchematicAutoRouter` (the route detours round the port's keep-out), fix it at
that point, and state which in the design note. Gate: no wire in the fixture's drawing has a segment that doubles back
on itself or encloses a part's box. Applies to both arrangements.

### Net labels

**R-dfr22-2-4 — Labels on their wire.** Every label `Build` writes is anchored to a wire of its own net with a
perpendicular offset no larger than the label's own height (`LabelPoint` today picks a point on the wire; find where a
label ends up far away — the reported one was a port's net name floating a grid row or more from its wire — and fix
that path). A net with no wire (an unrouted terminal) keeps its label ON the terminal.

**R-dfr22-2-5 — A net label can be dragged.** In the Select tool, pressing on a net label and dragging moves it:
- along its wire, re-anchoring (`SegmentIndex`/`AlongT`) to the nearest point of any wire **of the same net** under the
  drop; dropped away from every wire of its net it keeps its anchor and takes the drop as its offset;
- one undoable step (a new `MoveNetLabelCommand` beside `MoveNetLabelAnchorCommand`), live preview while dragging;
- never changes connectivity — a label is a name, so dropping it on another net's wire is refused (the label snaps
  back) rather than renaming either net. Gate with the netlist partition, as `DesignerRound22Tests` does.

### Junctions

**R-dfr22-2-6 — A tee at coplanar junctions.** circuitRF has no coplanar tee model. Two options; the owner chooses
(§5), and the brief implements the choice:
- **(a) MTEE as the nearest model** (recommended to start): a three-arm junction whose arms are all CPWG becomes an
  MTEE with the arms' widths, exactly as a microstrip one does, and the report says
  "N coplanar junctions are modelled with the microstrip tee (no coplanar tee model)". Four arms → MCROSS likewise.
  Holds the reference-plane arithmetic `LineJunctions` already documents.
- **(b) A coplanar tee model** — a new `ComponentModel` with its own golden reference. That needs an externally
  generated reference (CLAUDE.md "Validation expectations") and is a series, not a requirement here.
Mixed arms (CPWG + MLIN) take the same treatment as all-CPWG.

### Create

**R-dfr22-2-7 — Create shows progress and can be cancelled.** `CreateJob.Work` gets the `RunControl` the preview uses:
the same progress bar and stage text (`ProgressText`) while it runs, a Cancel that stops it (right-click on the bar as in
the rest of the app, and the dialog's own Cancel), and nothing written when cancelled.

**R-dfr22-2-8 — Create reuses the preview.** When the inputs Create would recognise from are those the last completed
preview used (scope, options, BOM, placement, parts CSV — compare the `RecognitionInput` value plus the layout file's
write time), Create emits and draws from that `RecognitionResult` instead of recognising again. Otherwise it
recognises, as today. Gate with a counter on the runner (one recognition for preview + Create), not with timing.

## 3. Not in this brief

A coplanar tee model (option (b)); placing parts for a schematic PICTURE (already exact); changing Tidy.

## 4. Gates

- `ArtworkBoardArrangementTests` on a synthetic board (`RecognitionBoards`/`EmitBoards`): every part's drawn position
  preserves the board's left/right and above/below order between neighbours; no two items overlap; the netlist
  partition equals Tidy's; `--arrange tidy` is byte-identical to today.
- The loop fixture (R-3) and the label fixture (R-4) in the same class.
- Net-label drag: re-anchors on its own net, refuses another net, undo restores anchor and offset.
- Junctions: an all-CPWG three-arm junction emits an MTEE whose widths are the arms' and whose arms are shortened by the
  documented reference planes; the report line appears; microstrip junctions unchanged.
- Create: a cancelled Create writes nothing; preview + Create runs one recognition.
- Run the `Recognition` tests (excluding `PlotRecognition`/`ImageRecognition`), `Suite=SchematicConnectivity`, and the
  new classes. No full suite.

## 5. Open for the owner

- R-dfr22-2-6: (a) MTEE as nearest model now, or wait for (b) a coplanar tee model.
- Whether "As on the board" should also become the default for **Update Schematic from Layout** on artwork (round 21's
  fill path) — recommended yes, same code.
