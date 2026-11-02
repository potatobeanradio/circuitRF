# Brief 3 — the three tables circuitRF could read and never write

**Series:** [authored board](brief-authored-board-0-overview.md) · **Tag:** `R-ab3-n` · **Phase:** P2
**Area:** `src/Design/Layout/Interchange/BoardNetlistWriter.cs`, `PlacementWriter.cs`,
`BomWriter.cs` (all new), `src/Cli/Netlist.cs`, `src/Ui/Views/Layout/` export rows,
`examples/Power Rail/Sensor board/layout/Board.gen.py`
**Depends on:** [1](brief-authored-board-1-layout-pads.md),
[2](brief-authored-board-2-net-identity.md) · **Blocks:** nothing

---

## 0. What this brief delivers

Briefs 1 and 2 made the projection. This one serialises it: `BoardNetlistWriter`,
`PlacementWriter`, `BomWriter` beside their readers, reachable from the layout editor's File ▸ Export
and from `circuitrf netlist`.

**This is interop, not the railRF path.** Overview §0's rule stands: a user analysing their own board
never needs these files. What they buy is a board that can leave — to a fab, to an assembly house, to
another tool — and the retirement of the Python script that currently writes the one example proving
railRF works.

The scope statement that keeps this brief honest: **if anything in railRF starts requiring the output
of this brief, briefs 1 and 2 have been implemented wrongly.**

---

## 1. `R-ab3-1` — three writers, each the inverse of the reader beside it

**`R-ab3-1a`** Each writer lives in the same file's folder as its reader and takes the projection
briefs 1 and 2 produce — `IReadOnlyList<PdnPad>` plus the root's vias for the netlist, the root's
instances for the placement, the schematic's components for the BOM. **No writer re-derives
anything.** A second projection is the drift this series exists to prevent.

**`R-ab3-1b`** Each carries its reader's own provenance header verbatim in spirit:
`BoardNetlistFile.cs:20` says *"WRITTEN FROM PUBLIC DOCUMENTATION ONLY"* about IPC-D-356, and the
writer is the half of that statement with teeth. Nothing is transcribed from any tool's output.

**`R-ab3-1c`** Each **declares** what its reader would otherwise have to infer:

| | declared | so the reader comes back |
|---|---|---|
| netlist | units | `BoardNetlistUnitsEvidence.Declared` |
| placement | units **and origin** | `PlacementOriginEvidence.Declared` |
| BOM | its column header | `BomFile.HeaderMatches` |

The placement one is the payoff worth naming: an unstated origin is a **refusal** in the import
dialog (`RailImportOptions.PlacementOrigin`, *"three quarters of a millimetre on an 0402 is the
difference between landing on the part's own pad and landing on its neighbour's"*). A file circuitRF
wrote must never provoke that question, and `PlacementOrigin.SymbolOrigin` is the honest answer
because that is what an instance's origin is.

**`R-ab3-1d`** The netlist writer emits `Access` per record from the stackup's own via span, and
`Plated` from the via. Where the artwork does not say, the field is **omitted** rather than defaulted
— `BoardNetlistRecord` models every one of them as nullable precisely so absent and stated are
different, and a writer that fills in a plausible `1` makes a through feature look like a surface one.

**`R-ab3-1e`** The BOM writer's rows come from the schematic's components — refdes, value, the
`Footprint` parameter (footprint brief 2), and the description where one exists. **It writes no part
number it was not given.** A BOM's part number is a purchasing decision and circuitRF does not hold
one; `BomRow.PartNumber` stays null and brief 4's coverage count is what tells the user so.

---

## 2. `R-ab3-2` — one verb, and it is the one that already means this

`circuitrf netlist` writes *the extraction Simulate performs* for a `.csch`, and refuses every other
document kind BY KIND. A board netlist is the same sentence about a different document, so it is the
same verb — not a fourth noun and certainly not a `convert` pair.

**`R-ab3-2a`** `netlist <path.clay | cell-folder>` is no longer a refusal by kind. A `.csch` behaves
exactly as it does today; nothing about the existing path moves.

**`R-ab3-2b`** **`-o` alone on a `.clay` is a refusal naming the three flags.** Two of the three
tables are `.csv` and the extension cannot say which, and "which table" is exactly what a dialog
would ask. The flags:

```
circuitrf netlist board.clay --ipc out.ipc --placement out.csv --bom out.csv
```

**`R-ab3-2c`** More than one in a run is ordinary and encouraged. Footprint 5 R-fp5-2's reason is the
governing one: *they have to agree, and hand-editing three files is how that goes wrong silently.*
One invocation, one projection, three files that cannot disagree.

**`R-ab3-2d`** A `.clay` that resolves no schematic still writes all three — the netlist and
placement off the artwork, the BOM off nothing but the refdes and footprint each instance carries.
Thin, and honest about being thin.

**`R-ab3-2e`** The refusals are briefs 1 and 2's, unchanged and not restated: over the flatten
ceiling writes nothing; a piece with two stated names writes nothing; a part whose pins cannot be
joined writes no records for that part and is named. **A partial table is never written** — the
contract `BoardNetlist` and `PlacementTable` both state on the way in (*refusal non-null means nothing
was read and nothing may be used*) applies symmetrically on the way out.

---

## 3. `R-ab3-3` — the same three rows in the layout editor

**`R-ab3-3a`** File ▸ Export gains **Board netlist (IPC-D-356)…**, **Placement…** and **Bill of
materials…**, beside the Gerber, GDSII and DXF rows that are already there.

**`R-ab3-3b`** They call the same functions the verb calls. The gate is `ConvertCliVerbTests`' own —
byte identity between the CLI as a process and the in-process call the GUI's File ▸ Export makes.
That test exists because the two drifting is invisible.

**`R-ab3-3c`** Where the export is thin — no schematic, so no net names — the dialog says so before
writing, naming what will be absent. It does not refuse: a placement file with no nets in it is a
perfectly useful placement file.

---

## 4. `R-ab3-4` — the example stops depending on Python for two of its three files

`examples/Power Rail/Sensor board/layout/Board.gen.py` writes the `.clay`, the `.ipc` and the
placement table together (footprint 5 R-fp5-2).

**`R-ab3-4a`** It keeps writing the `.clay`. That is its job and footprint 5 says so — the artwork is
authored, and a generator is a reasonable way to author it.

**`R-ab3-4b`** It stops writing the other two. They are regenerated from the `.clay` by this brief's
verb, in one command recorded in the example's README, and committed.

**`R-ab3-4c`** R-fp5-2d's invariant — *a pad in the netlist that is not under a land in the `.clay`
is a part railRF cannot locate, and a land with no anti-pad under it is a decoupling capacitor
shorting the rail to its reference* — was a check the script performed because the two files were
written independently. **Half of it becomes structurally impossible**: a netlist projected from the
lands cannot name a pad that is not under one. The anti-pad half is a real geometric check and stays,
moving into the example's own gate rather than into the writer, which draws nothing and checks no
artwork.

**`R-ab3-4d`** The example's answers must not move. Same gate briefs 1 and 2 end on.

---

## 5. Gate

`tests/Ui.Tests/RailRf/CompanionWriterTests.cs`, `tests/Ui.Tests/Cli/NetlistBoardVerbTests.cs`.

1. **Round trip, per writer.** Write, read back with the existing reader, compare the records
   field by field. This is the primary gate and it is the one that catches a coordinate-format or
   suppression mistake immediately.
2. **The reader comes back `Declared` on all three axes** — netlist units, placement units, placement
   origin (R-ab3-1c). **The origin row is the one that matters**: a file circuitRF wrote must never
   make the import dialog ask.
3. **Absent fields stay absent.** A surface pad writes no drill; a via with no stated plating writes
   no plating flag; the reader reports them as null, not as a value (R-ab3-1d).
4. **Round trip through railRF.** Author a board, write the three files, import them as if they were
   a foreign board, and compare the whole `DataSet` against the same board analysed directly through
   brief 1's projection. **They must agree.** This is the test that says the writers and the
   projection are the same statement, and it is the one that will catch a swapped pad pair that every
   field-by-field comparison passes.
5. **Byte identity, CLI process against in-process** (R-ab3-3b).
6. **`-o` alone on a `.clay` is a refusal naming all three flags** (R-ab3-2b), and a `.csch` behaves
   exactly as it does at HEAD — the existing `netlist` tests pass unchanged.
7. **Three tables in one invocation, and they agree**: every refdes in the placement appears in the
   netlist and in the BOM (R-ab3-2c).
8. **Nothing is written on a refusal.** Over the ceiling, two names on a piece, an unjoinable part —
   assert the output files do not exist (R-ab3-2e). A half-written `.ipc` is worse than none.
9. **The thin case writes.** No schematic: three files, no net names, and the dialog said so
   (R-ab3-2d, R-ab3-3c).
10. **The example regenerates.** Run the verb over `examples/Power Rail`'s `.clay` and compare
    against the committed `.ipc` and placement — and the example's `DataSet` is unchanged
    (R-ab3-4b, R-ab3-4d).

## 6. Scope

- **Not a railRF input path.** §0. Nothing in railRF may require these files.
- **Not a `convert` pair.** `convert` is artwork to artwork and every one of its readers lands on a
  cell folder plus a technology. These are derived tables and neither is that.
- **No new format.** IPC-D-356 as the reader reads it; the placement and BOM column sets the existing
  readers already recognise.
- **No part numbers invented.** R-ab3-1e.
- **No re-derivation.** R-ab3-1a — the writers take briefs 1 and 2's projection and nothing else.
- **`Board.gen.py` is not deleted.** R-ab3-4a.
