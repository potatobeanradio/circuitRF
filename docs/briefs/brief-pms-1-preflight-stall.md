# PMS-1 — the preflight cannot stall: standards pricing, the via scan, the accelerated estimate

Series: `brief-pms-0-overview.md`. Independent of every other brief; ships on its own.

## Why

On a medium MMIC the mesher returns in about a second, and then nothing reaches the panel for half an
hour or more. The overview's measurements give the cause. `PlanarMemoryCheck.PriceStandards` calls
`PlanarRunMemory.StandardBytes`, which builds every port's calibration standards and counts each one's
cores exactly with `PlanarCoreFootprint.Count`. On a chip-scale grid those standards are 20,000–52,000
unknowns each. Counting the smallest takes 34 s and 5.9 GB, and the set never finishes on a 7.6 GB
machine.

The figure it produces cannot change the outcome: the structure alone is already far past the limit.
The step has no progress stage, so the panel shows the mesher's last label (`scanning via footprints:
N / N`). It takes no cancellation token, so the panel's Cancel ("It stops at the next grid row") does
not stop it. The run path prices the standards again (`PlanarKernel.cs`, the CAP-1 follow-up block),
so Simulate Anyway would stall the same way.

Two smaller costs sit on the same path. The via-footprint scan tests every grid cell against every
polygon of every via group. The accelerated solve's near-field estimate counts 89.6 × 10⁹ near
entries exactly (58 s) before saying the set is too large to analyse.

## What to build

### R-pms1-1 — the synthetic MMIC chip fixture

`tests/Engine.Tests/Mom/Support/SyntheticMmicChip.cs`: a deterministic generator (fixed seed) that
returns a `PlanarProblem` plus its `PlanarPort` list. Parameters:

- chip size
- traces per level (Manhattan, 8–28 µm wide)
- Metal1–Metal2 vias (6 µm squares on 14 µm pads on both levels)
- backside vias (octagons, 60 µm on 80 µm Metal1 pads; square as an option)
- region-via side length (metal on both levels, joined by one via footprint; 0 = none)
- three 70 µm edge feeds on Metal2 with edge ports

The medium is the two-level GaAs stack (100 µm GaAs, 3 µm εr 2.7 dielectric, Metal1 on the interface,
Metal2 on top), as `ResistiveFilmTests.NoFilmKeepsTheGeometryHash` builds it.

Give it two named presets with the overview's parameters: **S** (80 small vias, 20 backside vias,
625 µm region, 20 traces/level) and **L** (150 / 20 / 625 / 30). Then add a `.clay` + `.cem` writer
for the same chip on the shipped MMIC GaAs technology (Via layer 3 for the posts and the region,
Backside Via layer 8). Use `LayoutPersistence.SaveToFile` and the `.cem` writer the editor uses, into
a temp workspace, so the CLI preflight path can be driven end to end. Engine.Tests already references
`src/Cli`, which brings `src/Design`.

### R-pms1-2 — do not price standards when the verdict is already decided

The mesher already has the predicate: `pastWithNoCores` in `SurfaceMesher.Mesh` (the dense matrix
alone, with zero-byte cores, is past the limit). Expose it on `PlanarMeshReport` under a clear name.
Then:

- `PlanarMemoryCheck.PriceStandards` returns the report unpriced when it is set.
- So does the run path's call in `PlanarKernel.cs`.

Do not drop the information silently: add one note saying the calibration standards were not priced
because the structure alone is past the limit. Keep it plain; the warning above it already carries
the numbers.

### R-pms1-3 — exact core counting is bounded

The counted footprint exists so a mesh near the limit is judged on its real cores (CAP-3). Keep that,
and bound it:

- **A standard is counted through `PlanarCoreFootprint.For`**, not `Count`, so the existing
  `CountedUpTo` applies to it as it does to the structure.
- **`For` gains a second bound, on the work**, because 20,210 unknowns is under `CountedUpTo` and
  still took 34 s and 5.9 GB. Two candidates; measure both and ship whichever bounds both time and
  memory:
  - the band size (Σ over cells of `m − MinInner[a]`, available once `RampTopology` is built);
  - a cap on distinct classes, where `DistinctBandKeys` stops and returns "not counted" once any
    worker's set passes it. On a chip-scale grid classes ≈ pairs, so the class sets are the memory.
- Past either bound the estimate is used, with `Counted: false`, so every sentence that quotes the
  bytes already says "estimated".
- **Record the measurement that sets each constant** in `RESOLVED.md` (§PMS-1): wall time and peak
  memory against band size on the dev box, plus the constant chosen. Size it so a count stays well
  under a few seconds and a few hundred MB. Every existing CAP-3 test must still count; if one falls
  past the bound, the bound is wrong.

### R-pms1-4 — a stage and a cancellation token

- `StandardBytes` and `PriceStandards` take a `RunControl?`. Open a stage, `pricing the calibration
  standards`, with one unit per standard, labelled with the port number.
- `CountCores` / `DistinctBandKeys` take the token and check it once per chunk.
- `EmSetupEditorViewModel.ComputePlanarMesh` and `EmRunService.Preflight` pass the control they already
  hold. The panel's Cancel then reaches the step, and its sentence stays true. If it no longer is,
  correct it.

### R-pms1-5 — the via scan is bounded by each footprint

In `SurfaceMesher.Mesh`'s vertical-basis loop (L9c block, "scanning via footprints"):

- For each via group, mark covered cells polygon by polygon. Limit each polygon to the index range of
  its bounding box: binary-search `gx`/`gy`, as the conformal path already does for its own ranges.
- Then emit the group's covered cells in (IY, IX) order, each once, over the union of the boxes. Use a
  reused `bool[]`/`int[]` mask, not a set (R-msh-2).
- **Bases must be bit-identical**: the same list, the same order, the same `viaFootprintCells` /
  `viaUnattached` / `groundUnknowns`.
- Make the stage label say which group it is on: `scanning via footprints (k of V)`. Tick per group
  row actually scanned. The stage total is declared up front, so make it honest for the bounded scan.

### R-pms1-6 — the accelerated estimate stops when it knows

`PlanarNearFieldEstimate.For` already has `AnalysedNearEntriesCap`, past which it reports the set as
too large. Stop counting the moment the running count passes that cap. Report it as a lower bound
("more than X entries"), and keep the sentence's remedies. Below the cap the result must be
identical.

## Gates

1. **The preflight decides fast, on fixture S.** Two tests:
   - Through the engine sequence the preflight runs (`PlanarGroundPath.Extend`, `SurfaceMesher.Mesh`
     with a 7.6 GB limit, `PriceStandards`): the verdict is past the machine limit, the new note is
     present, and no standard was counted.
   - Through `EmRunService.Preflight` on the generated `.clay`/`.cem`: the same verdict, and every
     finding is present.

   Assert on counters and flags (a counter of exact counts performed, or `Counted: false` on what
   was quoted), never on time. Report the wall time in the completion note. The target is seconds.
2. **Near the limit, nothing moves.** Every existing CAP-1/CAP-3 test passes unchanged, and
   `StandardBytes` returns the same bytes as before on every existing fixture it is called with.
3. **The via scan is bit-identical** on fixture S, fixture L and the existing via fixtures
   (`ViaPhysicsTests`, the ground-via tests, `MmicCoilFixture`): the same `Bases` sequence and the same
   notes. Report the scan's wall time before and after on fixture L.
4. **Cancel reaches the pricing.** A control whose token is cancelled from the progress callback when
   the `pricing the calibration standards` stage opens makes `PriceStandards` throw
   `OperationCanceledException`. Use a fixture small enough to price normally, with the bound lowered
   in the test if needed.
5. **The bounded estimate.** `PlanarNearFieldEstimate.For` on fixture S returns `Large`, quoting the
   lower bound. On an existing fixture below the cap it returns the identical result.

## Out of scope

- Making the chip solvable (briefs 2–7). After this brief it still reports ~940,000 unknowns. It
  reports them in seconds, with the per-level and vertical breakdown the budget note already carries.
- The accelerated ceiling's single-level restriction (owner's decision, P12).
- Why the standards inherit the chip's gridlines. That is the shared tensor grid, and brief 6 revisits
  it under a local grid.
