# PMS-6 — a locally refined grid: everything that reads the mesh

Series: `brief-pms-0-overview.md`. Needs briefs 4 and 5.

## Why

Brief 4 made a cell a block and gave a rooftop a segment. Brief 5 made the fill accept that. Every
other reader of the mesh still assumes elementary cells on one tensor grid. Each one is refused on a
block mesh (`PlanarMesh.HasBlocks`) until this brief converts it.

The list is what `grep` finds today. Any reader of `PlanarCell.IX`/`IY`, `PlanarMesh.GridX`/`GridY`,
or `PlanarBasis.CellA`/`CellB` geometry:

| Reader | What it assumes |
|---|---|
| `PlanarPort` / `PlanarPorts` | The reference plane is the shared edge of the two outermost cells; port cells are found by grid index |
| `PlanarCalibration` (`BuildLine`, `BuildSet`, `BuildGroupLine`) | A standard is built from the DUT's port resolution and its gridlines |
| `PlanarExcitation` | `B` from the port's rooftops |
| `PlanarSolve` (feed clearance, severed conductors, `VerticalExtent`, level-separation notes) | Cell rectangles and grid extents |
| `PlanarConductors` | The conduction graph over bases |
| `PlanarDcSolve` | One resistor per basis from its support geometry |
| `PlanarCurrentDensity` | Cell currents from covering rooftops, `J = I / transverse extent` |
| `PlanarFarField` (`RooftopSpectrum`) | A rooftop is a triangle in x times a rectangle in y over two cells |
| `PlanarSurfaceImpedance` | Per-cell area and level |
| `PlanarRooftopMerge` (PM-2) | Runs of elemental rooftops along one grid row |
| `PlanarMeshPitchField` | Pitch sampled on grid lines |
| `PlanarAim`, `PlanarAimBordered`, `PlanarStaticAim`, `PlanarNearFieldEstimate` | Basis centres and spans; the auxiliary grid pitch from the largest basis support |
| `LayoutRenderer.PlanarMesh` and the current-density overlay | Draw cells and per-cell |J| |

## What to build

Convert each reader. The rule for every one of them is the same:

- **on a 1 × 1 block mesh it is bit-identical to today;**
- on a block mesh it reads the support rectangles, never the parent cell when a strip is meant.

Points that need a decision, not just a conversion:

### R-pms6-1 — ports and standards

Port feeds are never coarsened (D-pms4-3), so the reference plane, the end run and the outermost cell
are exactly today's.

- **A calibration standard is built from the port's own local lines only.** Its transverse lines are
  the ones active across the port, not every line the chip happens to carry. This is the fix for the
  overview's standards of 20,000–52,000 unknowns. A 70 µm line should carry today's
  `MinCellsAcrossConductor`-driven count across, plus its fan, not 40.
- `SameCrossSection` still compares the DUT's port resolution to the standard's. State in
  `RESOLVED.md` why a standard with fewer transverse lines than the tensor-grid standard is still the
  DUT's own cross-section. The lines it drops are not active at the port. If that is not true for
  some port, keep them, and say which.

### R-pms6-2 — current density

`I_x(cell)` sums half of every covering x-rooftop. A coarse cell with two segment rooftops on one face
has two partial currents across different strips. Publish J per support rectangle; the overlay draws
supports. A cell's own |J| is its area-weighted mean, kept for any reader that asks per cell.

The identity in `src/Engine/Mom/CLAUDE.md` (the mean of the two adjacent edge currents, not ΣJ·w)
holds per strip.

### R-pms6-3 — far field

`RooftopSpectrum` transforms a triangle × rectangle over each half-support. A strip is a rectangle of
a different width; the closed form already takes the widths. The far field's existing refusals (cut
cells, vertical bases) are unchanged.

### R-pms6-4 — rooftop merging

PM-2 merges runs of elemental rooftops where cells are finer than the size field asks. On a block mesh
that is largely what the local grid already did. Decide by measurement on fixture A2 (single-level
version) whether PM-2 still finds anything to merge on a block mesh:

- if it does, convert it;
- if it does not, refuse it on a block mesh with a note saying the local grid already coarsened
  those runs.

### R-pms6-5 — AIM

The auxiliary-grid pitch is 0.5 of the largest basis support. On a block mesh the largest support
can be much larger than the smallest. Measure GMRES iterations and `|ΔI|` on fixture S (0.6 mm),
accelerated, against the dense local-grid solve. If the pitch from the largest support degrades the
fine region, size it from a percentile or per region, and record why. P8's floor (`NearRadiusMinM`)
is unchanged. The accelerated ceiling's single-level restriction is not touched (owner decision, P12).

### R-pms6-6 — lift the refusals

Each converted reader stops asking `HasBlocks`. When the last one is converted, `LocalGrid` meshes
solve end to end, de-embedded, through `EmRunService` and the CLI.

## Gates

1. **1 × 1 blocks are bit-identical** for every converted reader, on its own existing tests.
2. **De-embedded accuracy on A1 and A2**, self-calibrated as in brief 3, gate 2. Use edge ports,
   de-embedded, at 1, 5 and 10 GHz:
   - |S_local − S_tensor| ≤ the tensor grid's own refinement difference;
   - and < 1e-2.

   Report σ_max(S) (R-prt-15) for both.
3. **The standards shrink.** On fixture S (0.6 mm) every port's standards have at most the transverse
   cell count the port's own line asks for in isolation. Compare against a lone 70 µm line on the
   same stack, plus or minus the fan, and report the counts.
4. **DC.** `PlanarDcSolve` port-to-port resistances, local against tensor on A1: within 1 %.
5. **Far field.** A2 with one line opened into a short patch-like stub, one frequency: the pattern's
   peak directivity, local against tensor, within the 0.015 dB the mesh-density study already
   recorded (§3.8).
6. **End to end.** Fixture S at the size brief 2 found fits under the ceiling: run through
   `EmRunService` with `LocalGrid` on, one frequency, de-embedded, with a memory watchdog. Report N,
   wall time and peak resident memory. If brief 2 found no size of S that fits, run the largest that
   does.
7. **The mesh view and the current-density overlay** on fixture S at 1.0 mm. Snapshot with
   `tools/UiSnapshot --clay`, look at it, list it.

## Out of scope

The default (brief 7). Any change to a size rule, a refusal's physics, or the accelerated ceiling.
