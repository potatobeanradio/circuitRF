# PMS-4 — a locally refined shared grid: the mesher

Series: `brief-pms-0-overview.md`. **Built only if brief 2 says GO for the local grid.** Briefs 5 and
6 depend on it. This brief produces a mesh and a basis set. It does not have to solve; brief 5 makes
the fill accept it.

## Why

The planar mesher puts every conductor level on one tensor-product grid (L8b's D8). That is what lets
a vertical basis be a cell pair in z, and it is what makes every gridline global. A line asked for by
a 6 µm via pad's edge runs from one side of the chip to the other. It splits every cell it crosses,
on every level, far from anything that needed it. On an MMIC, with hundreds of small pads, the grid
becomes the union of all of them: about 800 lines per axis on 1.4 mm, with or without the edge mesh,
and 941,867 unknowns on fixture S.

Brief 2 measured what a grid that keeps lines local would produce. This brief builds it. The size
field is unchanged: the hard edges, the edge fan, `MinCellsAcrossConductor`, the λ cap, the
transmission-line and sheet pitch fields, and the detail floor all ask for exactly what they ask for
today. What changes is how far each request reaches.

## Design

### D-pms4-1 — a cell is a block of today's grid

Keep today's global line arrays `gx`, `gy` exactly as they are built: every candidate line, hard
lines included. A cell becomes a rectangle spanning `[gx[IX0], gx[IX1]) × [gy[IY0], gy[IY1])`, a block
of today's elementary cells. Today's mesh is the special case where every block is 1 × 1.

This keeps three things that were expensive to earn:

- **Exactness:** every feature edge is still a cell edge, so R-msh-1 is untouched.
- **Integer adjacency:** neighbours are found by comparing integer spans, so R-msh-2 is untouched.
- **The conformal cut:** it still runs on a rectangle.

`PlanarCell` gains `IX1`/`IY1`. Its existing `IX`/`IY` become the block's low corner. **Every reader
of `IX`/`IY` is visited in brief 6.** Until then, a mesh with any block larger than 1 × 1 is refused
by every consumer not yet converted, by name, never read wrongly.

### D-pms4-2 — one tree for every level

Refinement is decided once, for the union of all levels' requests. Every level's cells are blocks of
the same partition, so a via footprint's cells still pair one-to-one in z and R-via-5's vertical bases
are unchanged in kind.

The partition is a quadtree over a base grid, with a **2:1 balance**: a cell face meets at most two
neighbouring faces. Measure whether 2:1 or a looser k:1 gives the better unknown count for the same
accuracy on fixture A1 before fixing it. Record the decision.

### D-pms4-3 — when a block may merge

A block may be coarser than 1 × 1 only where no line inside it is **needed**. A line is needed in a
cell if:

- it is a hard line from a feature edge, and the cell lies within `LocalReachCells` (measure;
  brief 2's k-attribution is the starting point) of that feature along the line; or
- the size field at the cell asks for a pitch finer than the block would give, at today's realised
  pitch. The fan, the λ cap, min-cells-across and the pitch fields keep their meaning.

**Port feeds are never coarsened.** A port's end run and its de-embedding cells must be exactly
today's (brief 6 depends on it).

### D-pms4-4 — a rooftop across a refinement step

Where a coarse cell's face meets two finer faces, there is **one rooftop per fine face segment**. Its
half-support on the coarse side is the strip of the coarse cell that the segment spans: full depth
along the current, segment width across. Its divergence pulse is ±1/(strip area) on that strip.

Charge conservation stays exact per basis, and every support is still a rectangle, so
`RectangleIntegrals` applies unchanged. The conformal path's `RooftopSupport` already carries
weighted strips; reuse its vocabulary.

`PlanarBasis` gains the segment's transverse span (integer line indices on the face). A full-face
rooftop is the special case where the segment equals the face.

### D-pms4-5 — ordering

Cells sort by (LayerIndex, IY0, IX0). Horizontal bases are emitted per level in that order: X faces,
then Y faces, segments ascending. Vertical bases follow, as (via as given, IY0, IX0). Integers
throughout, no set or dictionary iteration (R-msh-2).

## What to build

- **R-pms4-1 — the partition.** Build it from the existing size inputs and the line provenance brief
  2 added. It sits behind `PlanarMeshSettings.LocalGrid` (bool, default false; `.cem` key omitted at
  default; `MeshHash` includes it when true).
- **R-pms4-2 — cells, adjacency and bases** as D1–D5, including the conformal cut on blocks, film
  assignment by block, and the via scan of brief 1 on blocks.
- **R-pms4-3 — the report.**
  - `PlanarMeshReport` and the budget note quote cells, unknowns, and the number of refinement steps
    (faces meeting more than one neighbour).
  - `MinCellEdgeM`/`MaxCellEdgeM` report block extents.
  - `MinCellsAcrossRun` measures across blocks.
  - The note says the grid is local and how many of the candidate lines were active on average.
- **R-pms4-4 — the refusal for unconverted consumers.** Add one predicate,
  `PlanarMesh.HasBlocks`, and one refusal sentence, asked by every consumer brief 6 converts. Until
  brief 5 lands, a `LocalGrid` mesh is refused by the solve with that sentence. The panel's mesh view
  and unknown count must still work, so a user can see the grid before it solves.

## Gates

1. **Degenerate case is bit-identical.** With `LocalGrid` on and every block forced to 1 × 1 (a test
   hook that marks every line needed), the cells, bases and order equal today's on every existing
   mesh test fixture and on S, L, A1 and A2. This is the gate that proves D1 and D5.
2. **Off is bit-identical**, trivially, and asserted on the same fixtures.
3. **The count matches the estimate.** Fixtures S (0.6, 1.0, 1.4 mm) and L: the built count is
   within 5 % of brief 2's estimator for the same rules. Report both.
4. **Structural soundness, on every fixture:**
   - every rooftop's two supports are rectangles inside metal;
   - the sum of divergence pulse charges on every basis is zero, except a ground attachment, which
     is −1;
   - every face shared by two metal cells on a level is covered exactly once by rooftop segments;
   - no rooftop pairs a cell with itself;
   - vertical bases pair identical blocks.
5. **Exactness.** Tiling error against the drawn artwork is identical to the tensor grid's on every
   Manhattan fixture (both are exact). The staircase error on the 45° option is reported, not gated.
6. **The mesh view.** The layout's planar mesh overlay (`LayoutRenderer.PlanarMesh`) draws blocks.
   Snapshot fixture S at 1.0 mm with `tools/UiSnapshot --clay`, look at it, list it.

## Out of scope

- The fill (brief 5) and every other consumer (brief 6).
- Changing any size rule: a local grid realises the same requests.
- Per-level independent grids. They would break the z pairing, and nothing has measured a need.
