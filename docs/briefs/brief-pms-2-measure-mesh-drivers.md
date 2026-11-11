# PMS-2 — measure what drives N on an MMIC, and decide what to build

Series: `brief-pms-0-overview.md`. Needs brief 1's fixture. **Builds no product code**, apart from
whatever instrumentation the measurement needs, which may stay as a diagnostic.

## Why

Fixture S meshes at 941,867 unknowns against a dense ceiling near 40,000. Two causes are visible, but
how much each one is worth is not known.

- **The shared tensor grid.** Each edge runs a hard gridline across the whole chip, so the grid is the
  union of every feature's lines. It stayed at ~800 lines per axis with the region via removed and
  with the edge mesh off.
- **The region via.** 390,000 µm² meshed on two levels plus one vertical unknown per cell. Removing
  it took N from 941,867 to 297,201.

Briefs 3–6 are a large investment, and briefs 4–6 change the mesher's central data structure. This
brief measures before anything is built. It says whether a local grid, a stacked-metal region, both,
or neither gets a chip like this under the ceiling.

## What to build

### R-pms2-1 — the fixture family

Extend `SyntheticMmicChip`:

- **Sizes:** 0.3, 0.6, 1.0, 1.4 mm.
- **Two densities:** S and L.
- **Option: MIM capacitors.** A MIM Metal level with plates under Metal2 and MIM Via windows, on the
  shipped technology's three-level stack. This is how the shipped MMIC technology meshes when MIM
  Metal is ticked, and it adds the chain vias `PlanarExtractor` builds for a post that spans a level.
- **Option: one 45° trace per level.** Exercises staircasing, and the conformal path where it is on.
- **Two small accuracy fixtures** that solve dense on 16 GB in a few minutes at 3 frequencies:
  - **A1:** a 250 µm block with two lines, one backside via, four posts and a 120 µm region via.
  - **A2:** the same block without the region via.

  Record N for each. These are the accuracy references for briefs 3–6 (decision D2).

### R-pms2-2 — where the gridlines come from

Instrument `CollectBoundaryLines` and the partition so a diagnostic run can attribute every final
gridline to its source:

- a conductor hard edge
- a via-footprint hard edge
- a port or feed line
- an edge-fan line
- a bulk line (λ cap or min-cells-across)
- a detail-floor suppression

For fixtures S and L report per axis: the line count by source, and the count of lines that pass
through cells of no feature within k cells of their origin, for k = 2, 4 and 8 (lines that exist
only because the grid is shared). A diagnostic object filled when supplied, like
`ConformalDiagnostics`, is the pattern; the mesh must be bit-identical with it attached.

### R-pms2-3 — what a local grid would produce, estimated offline

Without changing the mesher, compute the cell and unknown count of a **locally refined shared grid**:

- Start from a coarse base grid at the bulk pitch.
- Refine each cell recursively (quadtree, 2:1 balanced across neighbours, one tree shared by every
  level so a via's cells still pair in z) until it meets what the current rules ask for at that
  point: hard edges, the edge fan's size field, `MinCellsAcrossConductor` on the metal there, and the
  λ cap.
- Count the cells on metal per level, the vertical bases, and the rooftops, including the extra
  rooftops a 2:1 step creates (two fine faces against one coarse face).

This is arithmetic over the size field, not a mesher. It must reproduce today's count within a few
per cent when refinement is forced uniform. That is its calibration, and it must hold before any
other number from it is reported.

### R-pms2-4 — what a stacked-metal region would save, estimated offline

On the tensor grid and on the local-grid estimate, count N with each fully overlapped, via-joined
region replaced by one level of metal plus a one-cell rim of vertical unknowns. Brief 3 builds exactly
this.

## The decision this brief makes

Report a table: fixture × {today, stacked region, local grid, both} → N, and dense resident bytes per
frequency point (`PlanarSystem.ResidentBytes` on the estimated footprint). Then apply these rules:

- **Local grid (briefs 4–6) is GO** if it brings fixture S at 1.0 mm under 40,000 unknowns, or cuts
  fixture S at 1.4 mm by at least 5×.
- **Stacked region (brief 3) is GO** if, on top of whichever grid is going ahead, it cuts fixture S by
  at least 1.5×.
- **If neither passes, stop.** Report the table and the line attribution; the series ends with brief
  1 and brief 7's docs.

**Flag the risk plainly.** If the estimate shows a full 1.4 mm chip still past the ceiling even with
both, say so in the decision, with the size that does fit. A local grid that makes 0.6 mm sub-blocks
solvable is a different deliverable from one that makes a whole chip solvable, and the owner should
know which one is being bought.

Write the table, the decision and the line attribution in `src/Engine/Mom/RESOLVED.md` §PMS-2.

## Gates

1. The local-grid estimator, forced uniform, reproduces `SurfaceMesher.Mesh`'s cell count on fixtures
   A1, A2 and S (0.6 mm) to within 3 %. Report the actual differences.
2. The line attribution sums to the grid's actual line count on every fixture it reports.
3. Fixtures A1 and A2 solve dense, de-embedded, at 3 frequencies (1, 5, 10 GHz), and their
   S-parameters are committed as `testdata/` references for briefs 3–6, with the settings recorded
   beside them.

## Out of scope

Any change to the mesher, fill or solver output. The diagnostic may be committed; nothing it measures
may change a mesh.
