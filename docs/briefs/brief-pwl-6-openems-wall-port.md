# PWL-6 — The wall port on openEMS

Series overview: `brief-pwl-0-overview.md`. Needs brief 3. Independent of briefs 4 and 5.

## Why

openEMS is the one solver where the port region can be made identical in the structure and in its standards with no
mesh construction at all: its grid is rectilinear, so copying the grid lines of the end run into each standard makes
the two the same cells. Two things differ from the FEM solvers: a lumped port needs at least one grid cell, and a PEC
face is a boundary of the grid rather than a meshed surface.

## Requirements

- **R-pwl6-1 — The face.** The wall port's face is written as a PEC boundary of the grid (`CsxcadWriter`); it is not
  turned absorbing, as a wave port's face is.
- **R-pwl6-2 — The gap is whole cells.** `FdtdGrid` places a line at the face and one at `g`, and `g` is at least one
  cell. A gap smaller than one cell at the run's grid is rounded up to one cell, with a note giving both numbers,
  because the gap belongs to the error box and its size does not reach the answer. The lumped source spans the gap
  along the line, from the boundary to the line's end.
- **R-pwl6-3 — Identical grids.** The standards' grid lines within the end run, across the cross-section and along the
  line, are the structure's own, copied; a routine test asserts them equal as numbers. Beyond the end run each
  standard grids itself.
- **R-pwl6-4 — Box modes.** Two PEC end walls and PEC side faces make a box. Keep the note openEMS's wave port already
  makes when the empty cross-section's first mode falls below 1.5 times the sweep's top, and make it for wall ports
  too, naming PMC or absorbing side faces as the remedy.
- **R-pwl6-5 — The standards** are openEMS runs built by `WallStandards`, cached under the openEMS key. They are
  time-domain runs and can be long; `dryRun` prices them as it prices the structure.
- **R-pwl6-6 — Lift the refusal** from brief 3's R-pwl3-15 for openEMS.

## Gates

1. Overview gates 3 and 4 on openEMS, on `testdata/pwl/` S and C, to brief 2's tolerances (`Category=Benchmark`).
2. Routine: the written CSX XML for a wall port (no solver), the grid identity of R-pwl6-3, and byte-identical XML for
   every shipped `.c3d`.

## Docs

`docs/user/src/reference/em-setup.md` §Wall ports: the openEMS paragraph; `em-solvers.md`'s capability table.

## Completion

`src/Design/Em3d/RESOLVED.md` §PWL-6, with the gate numbers beside Palace's and EMerge's.
