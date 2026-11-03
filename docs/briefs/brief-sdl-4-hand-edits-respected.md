# Brief SDL-4 — Re-runs that respect hand edits

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl4-n`
**Area:** `src/Design/Layout/SchematicSync/` (new `EditState.cs`, `DeltaPropagation.cs`; the planner,
`ChainPlacer`), the `GeneratedPlacements` table from SDL-0, `SchematicPCellSnapshots`,
`OrientationLink`, `src/Design/Layout/LayoutModel.cs` (`LayoutInstance.Locked`),
`src/Ui/Layout/LayoutEditorViewModel*.cs` (three commands), `SchematicToLayoutOverwriteReportTests`
**Depends on:** SDL-1 (SDL-2 and SDL-3 for their designs in the scenario set). **Gate designs:** E1-E14.

---

## 0. What this adds

After a first Update Layout from Schematic, a designer works on the layout: drags parts into a compact
arrangement, turns a stub, widens a line by hand, deletes a part and draws a meander in its place. Then
one MLIN's length changes in the schematic and the command runs again. Two things must both hold:

1. **The arrangement survives.** Every part keeps where the user put it and how it was turned, relative
   to the parts around it.
2. **The copper stays connected.** The MLIN is longer, so everything joined beyond it moves along by
   exactly the difference.

Today a re-run keeps positions only because it never moves an existing part, so a length change opens
a gap. And it **overwrites** a parameter edited in the layout with the schematic's value, with a warning
(R-L5-11, `SchematicToLayoutGenerator.cs:305-340`). This brief replaces both behaviours.

## 1. The model: the layout as left, plus the schematic's change

A re-run does **not** regenerate the layout and then merge in the user's edits. It starts from the
layout exactly as it is and applies only what the schematic changed:

- **D1 — Every change is a change of pin poses.** For each linked part whose geometry changes (a
  parameter, a cell reference, a mirror), regenerate it in its own frame and compare each pin's pose
  (position, outward direction) before and after. An MLIN whose L grows by ΔL moves its second pin by ΔL
  along its axis; an MBEND whose angle changes turns its second pin about its first; an MLIN whose W
  changes moves no pin. A part whose pins do not move changes in place and propagates nothing.
- **D2 — The far side moves as one rigid body.** Take the joints that are **made in the layout as it
  is** (the two pins coincide within 1 DBU, SDL-1 D4). Remove the changed part's joints on its far side
  (D3). The parts still reachable from the stay side do not move. Every other group moves rigidly by the
  transform that carries the old far pin onto the new one: translation, plus rotation about the joint
  when the pin's direction changed. Inside a moving group nothing changes relative to anything else, so
  the user's arrangement, angles and hand moves are all kept exactly.
- **D3 — Which side stays.** A side **stays** if it holds any of the following, checked in this order:
  1. a **Locked** part (§3): a connector at the board edge, a part fixed for mechanical reasons;
  2. a part with **no schematic link** joined to it (a hand-placed connector or fixture);
  3. **user-drawn copper** that joins it to something outside the side (moving the side would tear that
     copper);
  4. the chain's recorded anchor (`GeneratedPlacement.ChainAnchor`, normally the part on port 1's side).

  The other side moves. **If both sides must stay**, nothing moves: the length change cannot be absorbed
  without breaking a constraint, so the joint is left open and reported (S1): *"ML1 is 40.0 µm longer;
  both sides are held (J1 is locked; MT2 is joined to drawn copper on TOP at 12.40, 3.10 mm). Left a
  40.0 µm gap at ML1 pin 2."*
- **D4 — Several changes in one run** are applied one part at a time, in order of distance from the
  stay side (nearest first), each propagating through the layout as already moved. The result does not
  depend on schematic-id order for a tree; for loops, D5.
- **D5 — Loops.** In a loop, removing one changed part's far joints may not split anything, because the
  far side is still reachable round the loop. Remove the far joints of every changed part in the loop
  first, then move each separated group by its transform. When a group receives two transforms that
  disagree (only one side of a branch-line got longer), the one from the change nearest the stay side
  wins and the other joint is reported as a loop gap (SDL-1 D4). A branch-line whose two arms both
  change by the same amount moves its far side cleanly with no report.
- **D6 — Joints the user opened stay open.** Propagation follows only joints made in the current
  layout. A part the user deliberately pulled away from its neighbour is not reached by propagation and
  does not move; its open joint is reported as `Info` (not a warning, since it is the user's choice) each
  run.
- **D7 — Drawn copper inside a moving group moves with it.** A user-drawn shape that touches parts of
  one moving group and nothing outside it moves rigidly with that group, unchanged in shape, and the
  report lists it. Without this a drawn meander between two parts on the far side would be left behind.
  Drawn copper touching anything outside the group holds the side instead (D3.3).
- **D8 — Obstacles are reported, not avoided.** A moved group that now overlaps unlinked parts or other
  copper gets SDL-3's overlap warning. Steering around obstacles would change the schematic's geometry
  (S1).
- **D9 — Undo is one step** and restores parts, moved shapes, records, snapshots and orientation links
  together.

## 2. Parameters, removal, and older files

`EditState.Classify` reads the baselines the generator wrote last time:

| Situation | Re-run does | Report |
|---|---|---|
| Schematic changed a parameter, layout did not | Applies it; D1-D5 propagate the geometry change | `Info`: *"ML1 L 4.660 → 4.700 mm; moved MT2, ML6, MT3, ML7 and P2 by 40.0 µm."* |
| Layout changed a parameter, schematic did not | **Layout value kept** | `Info` each run: *"W of ML3 is 300 µm in the layout; the schematic says 280.152 µm."* |
| Both changed the same parameter (D-conflict) | **Layout value kept** | `Warning` naming both values |
| User deleted a linked part | Not re-added; its joints count as open (D6) | `Info`, naming Restore Removed Parts |
| Linked part with no `GeneratedPlacement` (placed before SDL-0) | Treated like any part: it moves only if a change propagates to it through joints that are made | One `Info` per run listing such parts the first time they move |

A hand move or rotation is **not** a state of its own any more: it is simply the layout as it is, which
D1-D5 start from. There is nothing to classify and nothing to pin.

## 3. `R-sdl4-2` — commands and the Locked flag

- **`LayoutInstance.Locked`** (additive `.clay` field, omitted when false): the part never moves during
  Update Layout from Schematic, and holds its side (D3.1). Set from the Properties panel and the context
  menu (**Lock Position** / **Unlock Position**). A locked part is drawn with a small lock mark on its
  selection outline only, so a picture of the layout is unchanged.
- **Re-snap** (context menu, on a selection of linked parts): re-applies the schematic's parameter
  values to the selection and re-joins each selected part to its neighbours by SDL-1's chain rule,
  closing joints the user had opened. One undo step.
- **Layout ▸ Restore Removed Parts**: re-adds removed parts and joins them. One undo step.

All three are thin wrappers over planner arguments. Nothing in the view model decides a position.

## 4. `R-sdl4-3` — R-L5-11 replaced

`SchematicToLayoutOverwriteReportTests.LayoutEditOverwritten_ReportsFromLayoutValueToSchematicValue_AtWarning_Gate13`
encodes the rule this brief replaces. Rewrite it to assert D-conflict (layout kept, warning naming both
values) and say why in its comment. `SchematicOnlyChange_ReportsInformational_Gate13` stays as it is.

## 5. Scenarios (E1-E14)

Over C1, C2 and F1, each a test through `SdlChecker` plus exact assertions on what moved and by how much:

| | Edits, then the schematic change, then re-run | Expected |
|---|---|---|
| E1 | C1: drag the far half into a U-shape by hand; lengthen the middle MLIN by 1 mm | The U keeps its shape; the far half moves 1 mm along the middle MLIN's axis; LVS clean |
| E2 | C1: rotate the far half by 90° by hand; lengthen the middle MLIN | Far half moves along the middle MLIN's axis, stays turned |
| E3 | C1: shorten the middle MLIN | Far half moves back by the difference |
| E4 | C2: change L90 (both arms change alike) | Right half of the loop moves by ΔL; no gap |
| E5 | C2: change only ML1's L | ML1's far side moves; one loop-gap warning at ML2's joint |
| E6 | C1: lock the far port's part, lengthen the middle MLIN | The near side moves instead |
| E7 | C1: lock both end parts, lengthen the middle MLIN | Nothing moves; one both-sides-held warning with the gap |
| E8 | F1: place an unlinked edge connector touching the far line, lengthen the near MLIN | The near side moves (D3.2) |
| E9 | C2: delete ML1, draw a meander from MT1 to MT2, change L90 | The meander holds the top; the bottom arm's change is reported as a gap (D3.3) |
| E10 | C1: draw a short stub touching only far-side parts, lengthen the middle MLIN | The stub moves with the far side; reported (D7) |
| E11 | C1: pull the far port's line 2 mm away, leaving a gap; lengthen the middle MLIN | Parts up to the gap move; the pulled-away line does not; gap reported as `Info` (D6) |
| E12 | C2: set ML1's W in the layout, then change W in the schematic | Layout W kept; `Warning` with both values |
| E13 | E1-E12's edits on one design, then Undo | Layout byte-identical to before the re-run |
| E14 | A `.clay` with no placement table, lengthen one MLIN | Parts beyond it move; the older-file `Info` line |

## 6. Docs

`layout-view.md` §9.1: §1 in prose, the §2 table, R-L5-11 marked superseded. §9 R16 (re-run must
update, not duplicate) still holds and is restated as the case where nothing changed. User page: *What
Update Layout keeps*, with a before/after figure of E1, Lock Position, Re-snap and Restore Removed Parts.

## 7. Gate

As SDL-1, plus `--filter FullyQualifiedName~SchematicToLayoutOverwriteReport`. UI: the context menu,
the Properties panel's Locked row and a locked part's selection outline rendered with
`tools/UiSnapshot`, looked at, PNGs listed. Findings in `src/Design/RESOLVED.md`.
