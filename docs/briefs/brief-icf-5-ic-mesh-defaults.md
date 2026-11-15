# Brief ICF-5 — Planar mesh defaults on small IC cells, and MIM via arrays

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf5-<n>` · **Depends on:** ICF-4's decision on the local grid
**Area:** `src/Engine/Mom/SurfaceMesher.cs`, `PlanarMeshSettings.cs`, `PlanarStackedRegions.cs`, `PlanarLocalGrid.cs`,
`src/Design/Layout/Em/PlanarExtractor.cs`, `src/Design/Layout/Em/EmSetup.cs` (`DefaultPlanarMesh`).

## The gap

**Board defaults on a 37 × 110 µm cell.** The field test's cascode meshes to ~22k unknowns (17–20 GB a point) by default.
From `em --dry-run`:

| | 4 cells across | 2 cells across |
|---|---|---|
| edge mesh on | 22,026 | 9,462 |
| edge mesh off | 7,301 | 2,596 |

Two board-scale defaults set this: an edge fan of 3 graded cells, and 4 cells across the narrowest conductor (a 1.5 µm pin
stub sets a 0.375 µm pitch). The detail floor, min(λ_g/200, 2 % of the extent), is 0.735 µm on this cell whatever the
user's detail divisor says.

**MIM via arrays.** An HBT core with two kit MIM capacitors left in the EM meshes to ~48–51k unknowns (73–88 GB a point).
Every via cut of the capacitor's array keeps its own outline (`PlanarExtractor`, the via footprint path), every footprint
edge becomes a gridline on all levels, the local grid cannot merge a block a via covers partly, and the stacked-region
merge needs 30 µm (`StackedRegionMinSideM`).

## Requirements

**R-icf5-1 — An IC rule in Auto.** When the artwork's extent is small against λ_g at the mesh frequency and the
conductors' width-to-thickness is below a threshold (start at extent < λ_g/20 and w/t < 5; the measurement sets them),
Auto uses 2 cells across and the edge mesh off (or one graded cell, whichever the measurement shows closer to the refined
answer). The run's mesh note says the IC rule applied and which values it chose; a stated setting in the `.cem` always
wins.

**R-icf5-2 — The user's detail divisor wins over the 2 % cap.** A detail divisor stated in the setup is applied as stated;
the 2 % cap applies only to the default. Add an absolute floor as a setting only if the measurement shows the divisor
cannot reach a useful value on its own.

**R-icf5-3 — A via array becomes one region.** In the via footprint path, an array of identical cuts on one via layer
whose hull is filled above a stated fraction becomes one rectangle (the hull), with conductivity scaled by the cut fill
factor so the DC resistance through it is unchanged. Reported in the mesh note (`Vmim: 64 cuts merged into one region,
fill 0.42, σ × 0.42`). A setting turns it off.

**R-icf5-4 — MIM below the stacked-region floor.** A capacitor plate pair (a MIM layer between two metals in the stack) is
merged by the stacked-region pass at any size; other stacks keep the 30 µm floor.

## Measurement before the defaults are fixed

On synthetic fixtures (a small cascode-like cell, ~40 × 110 µm, with 1.5 µm stubs; a 20 × 20 µm MIM with a 6 × 6 cut
array) on a thick-silicon stack written for the test, in a scratch harness:
- the refined reference (4 across, edge mesh on, finer detail) against each candidate rule, |ΔS| at 3 frequencies;
- the MIM's C and S with the array as drawn against merged, and against R-icf5-4.

The rule's thresholds are the ones whose |ΔS| stays inside the refinement step the reference itself shows (the PMS-7
criterion). Recorded in `src/Engine/Mom/RESOLVED.md` §ICF-5.

## Gates

1. A board fixture's mesh and S are byte-identical to `main` (the rule does not apply).
2. The small-cell fixture under Auto takes the IC values and its unknown count drops by the measured factor; the note says
   so; a stated `MinCellsAcrossConductor` overrides it.
3. A stated detail divisor below the 2 % cap is applied.
4. The via array merges, the DC resistance through it is unchanged to 1e-9, and the setting turns it off.
5. Shipped MMIC examples that fall under the rule are re-run; their pages' figures are updated if they move, and the
   |ΔS| is recorded.

## Not here

The local grid's base-cut placement (ICF-4). 3D meshing.
