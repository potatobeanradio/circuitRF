# PWL-5 — The wall port on EMerge

Series overview: `brief-pwl-0-overview.md`. Needs brief 3. Independent of briefs 4 and 6.

## Why

Brief 3 runs the wall port on Palace and refuses it on EMerge by name. Nothing in the port needs a solver feature
beyond a lumped source on a sheet that touches a PEC face, and the calibration happens after the solves, so EMerge
should run it with no new physics.

## Requirements

- **R-pwl5-1 — The gap's source.** `circuitrf_emerge.py` writes the gap sheet as a lumped port, direction from the face
  to the line, Z0 as on every lumped port. Check, on a one-frequency fixture, that EMerge's lumped port accepts a sheet
  whose edge lies on a PEC boundary face; if it does not, refuse by name with the reason and stop and report.
- **R-pwl5-2 — The port region.** Use brief 2's port-region construction if EMerge's meshing path can state it. If it
  cannot, measure independent meshing on fixtures S and C at 1, 5 and 10 GHz against brief 2's tolerance and record
  the numbers; ship with independent meshing only if it passes.
- **R-pwl5-3 — The standards** are EMerge runs built by brief 3's `WallStandards`, cached under the EMerge key.
- **R-pwl5-4 — Lift the refusal** from brief 3's R-pwl3-15 for EMerge only.
- **R-pwl5-5 — Rebuild before `--no-build`**: `circuitrf_emerge.py` is an embedded resource.

## Gates

1. Overview gates 3 and 4 on EMerge, on `testdata/pwl/` S and C, to brief 2's tolerances (`Category=Benchmark`).
2. Routine: the written EMerge script for a wall port (no solver); every shipped `.c3d` writes a byte-identical script.

## Docs

`docs/user/src/reference/em-setup.md` §Wall ports: the EMerge paragraph; `em-solvers.md`'s capability table.

## Completion

`src/Design/Em3d/RESOLVED.md` §PWL-5, with the gate numbers beside Palace's.
