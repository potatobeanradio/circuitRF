# PMS-3 — a via-joined stacked-metal region is one conductor

Series: `brief-pms-0-overview.md`. **Built only if brief 2 says GO for it.** Independent of briefs 4–6
in code. Brief 2 decides whether it still pays alongside a local grid.

## Why

An MMIC routinely joins Metal2 to Metal1 over a large area: a wide pad, a ground bus, a capacitor
plate's landing. The technology draws it as a via-layer polygon the size of the overlap. Today the
mesher treats that footprint like any via:

- every cell of it is meshed on both levels;
- every cell that carries metal on both gets its own vertical unknown.

A 390,000 µm² region is two thirds of fixture S's 941,867 unknowns. Physically the two sheets are
shorted together everywhere inside the footprint. Current enters and leaves the stack through its rim,
and the interior behaves as one conductor whose sheet conductance is the sum of the two. Three
unknowns per cell are describing what one unknown per cell carries.

## What to build

### R-pms3-1 — recognise a stacked region

In `SurfaceMesher.Mesh`, after the cells are classified and before the vertical bases are emitted
(the L9c block), find **stacked regions**. A stacked region is a connected set of via-footprint cells,
from one `PlanarVia` group, where:

- both the group's levels carry metal on every cell of the set;
- the set is at least `StackedRegionMinCells` cells across in both axes. Measure the default. The
  intent is that an ordinary post (a few cells) never qualifies;
- it is a ground attachment never: a backside via has one meshed foot and nothing to merge.

A chain built by `PlanarExtractor.AddSpan` puts the footprint on an intervening level (Metal1 → MIM
Metal → Metal2). Treat the chain's consecutive groups with the same footprint as one stack. The top
level is the one kept.

### R-pms3-2 — mesh it as one level with a rim

For each stacked region:

- **Interior** (more than `StackedRegionRimCells` cells, default 1, from the set's boundary): keep the
  top level's cells. Do not create the lower levels' cells there. Emit no vertical bases there.
- **Rim:** keep every level's cells and emit vertical bases exactly as today. Current entering the
  lower level from outside the region transfers here.
- **Lower-level metal that is wholly inside the interior** (a trace under the region that is covered
  by the footprint everywhere) is shorted to the top already. It is dropped with the rest of the lower
  interior, and the note counts it.

Removing cells is done in the mesher, on the cell lists, **before** horizontal bases are built. The
ordering contract (R-msh-2, R-via-5) holds unchanged on what remains. The `PlanarProblem` and its
geometry hash are not touched.

### R-pms3-3 — the interior carries both sheets' conductance

The interior's top-level cells carry a surface impedance for the parallel combination of the stacked
levels, not the top level's own. CL1's surface impedance is per level today, and a film (EM-TFR) is
how a per-cell value already reaches the Gram. Choose one of these and justify it in `RESOLVED.md`:

- a per-cell surface-impedance override on the same path;
- a film-like region with a frequency-dependent value.

Measure the loss error of NOT doing this (top level's own Z_s) on fixture A1 and report it. If it is
inside gate 2's tolerance, the simpler path may ship, with the error stated in the note.

### R-pms3-4 — setting, note, provenance

- `PlanarMeshSettings.StackedRegions` (bool). **Default false in this brief**; brief 7 decides the
  default.
- `.cem` key `StackedRegions`, omitted at its default (the omit-at-default rule).
- `EmSnpProvenance.MeshHash` includes it when true.
- One note per run that merged anything. It gives the number of regions, the total area, and the
  unknowns saved, as {lower-level cells, vertical bases} not created. State it as fact; do not explain
  stacking to the user.
- When the setting is off and a region qualifies, the budget note says how many unknowns turning it
  on would save. That is a remedy that binds, so it may be named (`src/Engine/Mom/CLAUDE.md`'s
  "naming a remedy without asking whether it binds" trap).

## Gates

1. **Off is bit-identical.** Every existing mesh test, and fixtures S, L, A1 and A2, mesh to identical
   cells and bases with the setting off.
2. **Accuracy on A1, self-calibrated.** Solve A1 de-embedded at 1, 5 and 10 GHz three ways: full
   (setting off), stacked (on), and full at one refinement step finer (`MinCellsAcrossConductor` + 1,
   or cells/λ × 1.5; whichever moves the answer more). The tolerance is the full solve's own
   refinement difference, max |ΔS| over entries and frequencies:
   - |S_stacked − S_full| must be no larger than that difference;
   - and below 1e-2 in any case.

   Judge small entries by |ΔS|, not dB. Report all three numbers per frequency.
3. **DC agrees.** `PlanarDcSolve` on A1: the port-to-port resistances, stacked against full, agree to
   1 % (the conduction network sees the parallel sheets directly).
4. **The saving, on S.** Fixture S's unknown count with the setting on, against brief 2's estimate for
   it. Report both. The test asserts the count fell by at least the region's interior cell count on the
   lower level. That is structural, not a timing.
5. **No merge on a post.** The existing via fixtures, with the setting on, produce no stacked region.

## Out of scope

- Merging two levels that overlap without a via. That is two conductors coupled through a dielectric,
  and the mesher must not short them.
- Partial overlaps where the footprint is not covered on both levels. Those cells keep today's
  treatment; the rule is per cell set, not per drawn polygon.
- The 3D path (`.c3d`, Gmsh). It meshes solids and has no such duplication.
