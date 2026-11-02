# Brief 8 — the FDTD grid: where the lines go

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d8-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.5 ("the grid is circuitRF's to write, and it is the real work of this backend"), §4.2 (openEMS section), §12 (FDTD grid quality; near-coincident edges)
**Area:** `src/Engine/Em3d/FdtdGrid.cs` (new), `src/Design/Layout/Em/` (the `.cem`'s `OpenEms` section
fields), `src/Cli/Explain.cs` (brief 5's openEMS size row)
**Depends on:** 3 · **Blocks:** 9

---

## 0. What this brief delivers

`FdtdGrid.Build(Em3dProblem, OpenEmsGridSettings) → FdtdGridResult`: three sorted arrays of grid
lines (x, y, z, in metres), plus the reasons behind them. The reasons are **which feature set the
smallest cell on each axis**, the cell count, the Courant time-step estimate, and every merge it had
to make.

It is managed code in the numeric layer with no process and no file. Every property is testable
headlessly (§6.5), and **it needs no openEMS**. It can be built the day brief 3 lands.

---

## 1. `R-em3d8-1` — the problem is one-dimensional, three times

Per axis (§6.5): **collect the required lines, merge the near-coincident ones, then fill and smooth
between them.** Each step is a separate, separately tested function. The three axes share the code
and differ only in which coordinates they collect.

---

## 2. `R-em3d8-2` — required lines

**`R-em3d8-2a` On every metal edge aligned with the axis.** An extruded polygon contributes the
coordinate of each polygon edge **parallel to the other lateral axis** (x-lines from edges parallel
to y), and z-lines at its bottom and top. A diagonal or curved edge contributes **no** required line
beyond its extremes. It will be staircased (§3 of the note), and the grid does not pretend
otherwise.

**`R-em3d8-2b` The thirds rule at strip edges** (§6.5). Where a conductor edge borders a
non-conductor, the lines are placed **one third of the local cell inside the metal and two thirds
outside**, instead of exactly on the edge. This is where the edge singularity lives. The local cell
is the target size at that point (§4). The rule applies to **sheet and thin-conductor edges**; a
solid conductor's faces keep lines on the face. Write the rule once, with the note's §6.5 cited,
and make it switchable per setup (`ThirdsRule`, default on), because it is the setting a user
comparing against a hand-built model will want to turn off.

**`R-em3d8-2c` Exactly on**: every port sheet's extent (a port must sit on lines, §6.5
"alignment"), every sheet's z, and each air-box face.

**`R-em3d8-2d` Every required line carries its source**: solid name and which edge or face. The
reason is carried through merges, so diagnostics can name features.

---

## 3. `R-em3d8-3` — merging near-coincident lines (§12's mirror-image risk)

**`R-em3d8-3a`** Two required lines closer than `MinCell` are merged to their midpoint, **except**
that a port or sheet line is never moved. If two unmovable lines are closer than `MinCell`, they are
kept, and the result carries a **warning** naming both features and the cell size they force.

**`R-em3d8-3b`** `MinCell` defaults to a fraction of the smallest **deliberate** feature, the
smallest metal width or thickness in the problem. It is not a fixed length, so an MMIC and a
backplane both get a sensible floor. It is a named constant with a comment.

**`R-em3d8-3c` Every merge is reported**, with both sources and the distance. A layout whose two
shapes are 10 nm apart through a drawing error would otherwise set the time step of the whole run,
invisibly (§12). **The report is the feature**, not the merge.

---

## 4. `R-em3d8-4` — fill and smooth

**`R-em3d8-4a` Maximum cell size per interval** = λ_min ÷ `CellsPerWavelength` (default 20, a named
setting). λ_min is taken **in the densest material the interval passes through** (the
highest √(εr·μr) among solids overlapping that slab of the axis), at the top frequency. This is
§6.5's "fraction of the shortest wavelength in each material".

**`R-em3d8-4b` Grading.** Adjacent cells differ by at most `GradingRatio` (default 1.3, a named
setting). Between two required lines, insert lines so that both the maximum and the ratio hold,
**growing geometrically away from the smaller neighbouring cell**. The fill must be exact at both
ends: the interval is partitioned with no remainder cell smaller than its neighbour ÷ ratio.

**`R-em3d8-4c` Absorbing faces reserve their PML.** Each face whose boundary is `Absorbing` gets
`PmlCells` (default 8) **uniform** cells at the edge of the domain. openEMS's PML sits in the
outermost cells and wants them even. The air box is extended outward to hold them, never inward
over the structure, and `explain` says by how much.

**`R-em3d8-4d`** The fill is **deterministic** (overview rule 2) and stable: adding a solid far from
a region does not move that region's lines. A test asserts that locality, because it is what makes a
small layout edit produce a small grid change and a comparable result.

---

## 5. `R-em3d8-5` — what the result reports, and when it refuses

**`R-em3d8-5a`** `FdtdGridResult` carries: the three line arrays; per axis, the smallest cell and
**the feature that set it**; the cell count Nx·Ny·Nz; the **Courant time-step estimate**
Δt = 1 / (c₀·√(1/Δx_min² + 1/Δy_min² + 1/Δz_min²)), labelled an estimate because openEMS computes
its own and brief 9 compares them; and the step count to cover the excitation plus a nominal
ring-down.

**`R-em3d8-5b` Memory** from the cell count and a per-cell figure measured in F0 (brief 1
R-em3d1-2b), a named constant with provenance. A run whose estimate exceeds available memory is a
**refusal** that names the feature that set the cell size, plus the setting that would relax it (§6.5:
"a grid that would not fit in memory is a refusal naming the feature that set the cell size").

**`R-em3d8-5c` `explain`'s openEMS size row** (brief 5 §3c) is filled in here: cells per axis, total,
smallest cell and its feature, Δt, steps, memory, merges.

---

## 6. `R-em3d8-6` — the `.cem`'s openEMS section (grid part)

`CellsPerWavelength`, `GradingRatio`, `ThirdsRule`, `MinCell` (override), `PmlCells`. They are
nullable, omitted at default, and described by the generated reference page. Brief 9 adds the
run-control fields (end criterion, maximum steps).

---

## 7. Gate

`tests/Engine.Tests/Em3d/FdtdGridTests.cs`. Everything runs in milliseconds.

1. **Microstrip** (brief 3 gate 1): x-lines at strip edge ± (h/3, 2h/3) per the thirds rule, and
   exactly on the edges with the rule off; z-lines on the substrate top and the ground; port extents
   on lines. Expected values computed in the test from the rule's statement.
2. **Grading** holds everywhere (every adjacent ratio ≤ `GradingRatio` + 1e-12), on the microstrip
   and on a seeded random set of 50 Manhattan layouts.
3. **Maximum cell** holds everywhere, using the densest material in each slab: a substrate slab
   gets smaller cells than the air above it.
4. **Merge** of two edges 10 nm apart is reported with both sources; two port lines 10 nm apart are
   kept, with a warning.
5. **Locality**: adding a far-away pad leaves the lines over the original strip unchanged.
6. **PML**: an absorbing face gets `PmlCells` uniform cells and the box grows outward by exactly
   that span.
7. **Courant estimate** matches the formula on a hand-built three-cell grid.
8. **Refusal** when the memory estimate exceeds a limit (inject the limit): names the feature and the
   setting.
9. **Determinism**: two builds are bitwise-equal.
10. **Case B** (via transition) builds, and its smallest cell comes from the via barrel or the
    antipad, not from a numerical artefact. Assert the named feature.

## 8. Scope

- **No CSXCAD, no openEMS.** Brief 9.
- **No cylindrical or multi-grid.** openEMS has both (§3); Tier A's geometry does not need them yet.
- **No automatic convergence re-run** on a finer grid. Brief 9 may offer "run again at 1.5× density"
  as an explicit option; it is never automatic.
