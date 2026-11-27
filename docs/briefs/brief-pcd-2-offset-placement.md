# Brief PCD-2 — Offset placement and the plan-at-values entry point

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd2-n`
**Area:** `src/Design/Layout/SchematicSync/` (the chain step, new `SchematicLayoutPlanner.Offsets.cs`, `OffsetPlacer`,
`DesignedCellLayout`, `SchematicLayoutPlan.Root`), `testdata/schematic-to-layout/` (two designs)
**Depends on:** PCD-0, PCD-1's seams commit. PCD-5, PCD-6 and PCD-7 call `DesignedCellLayout.At`; PCD-6 calls
`OffsetPlacer.Drag`.

---

## 0. What this adds

The planner places parts joined by a net pin to pin. A designed cell also holds parts no net joins (a pad beside a
line, a second line across a gap), and their place must be a function of the parameters too (PCD-0 S1). An `Offset`
(PCD-0 §4.2) says where such a part goes, from another part's pin; the planner now honours it.

## 1. Requirements

- **`R-pcd2-1` — the planner places by offset.** A part with an `Offset` is placed by `OffsetPlacer.Pose` from its
  anchor's placed pin, after the chains are joined and before the rows. Its chain moves with it as one rigid body and
  never enters the rows on its own. An unresolvable anchor, an expression that does not evaluate, or an `Offset` on a
  part its chain already places is a report line, and nothing is moved to hide it.
- **`R-pcd2-2` — `OffsetPlacer.Pose` and `Drag`**, the geometry of PCD-0 §4.2 and its inverse for a drag.
- **`R-pcd2-3` — `DesignedCellLayout.At` is deterministic** (the same `.clay` bytes twice) and states its root: the
  chain the planner anchors first.
- **`R-pcd2-4` — two designs**, `O1-offset-gap` (two lines, the second offset by `Dx = g`) and `O2-offset-turned`
  (`Angle = 90`, the offset part leading a chain of two), tagged `pcd2` in `designs.txt`.

## 2. Decisions

- **D1 — Units, not just chains.** The offset step works on the chains and on every lone part (a part in no chain),
  so a single line can be offset or be an anchor. A unit holding a part already in the layout is never moved by an
  offset: the layout's own placement stands (SDL-4). A designed cell always plans onto an empty layout, so this never
  applies to one.
- **D2 — One offset drives a unit.** The first part with an `Offset` in schematic-id order places its unit. Another
  `Offset` in the same unit, or an anchor inside the unit itself, is the "already places" case: a Warning, ignored.
- **D3 — Order by dependency.** A unit is placed once its anchor's unit has settled, in schematic-id order, repeated
  until nothing changes. What is left anchors in a cycle and is reported.
- **D4 — One rigid body.** A placed unit joins its anchor's row group (or the fixed region, when that unit is already
  in the layout), so the row packer moves both together.
- **D5 — Exact until written.** Poses are carried as doubles from the anchor's exact pin and rounded once when the
  instance is written, as the chain does (SDL-1 D3): all-zero offset values are exactly a joint.
- **D6 — Drag folds into a trailing literal.** The drag's change, resolved in the anchor pin's frame, is added to
  `Dx` and `Dy` in `offset.Unit`, rounded to one database unit. A plain number becomes the new number; an expression
  ending in `+ n` or `- n` has `n` updated; any other expression gains `+ n` or `- n`. A zero change leaves it as is.
- **D7 — The root travels on the plan.** `SchematicLayoutPlan.Root` is the anchor of the first unit (schematic-id
  order) not placed by an offset; `DesignedCellLayout.At` adds it as one Info line. The planner's own lines are
  unchanged for a schematic with no offsets.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd2OffsetPlacementTests.cs`, one per requirement; the two designs also pass
`SdlDesignSetTests` with the rest of the set.
