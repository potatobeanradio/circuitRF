# HZN-7 — run a `.hzn` design on circuitRF's EMerge driver

Series: `brief-hzn-0-overview.md`. Needs HZN-2 and HZN-3.

## Why

A `.hzn` design carries its setups. Running it on circuitRF's own EMerge driver gives circuitRF's answer with
circuitRF's results, plots and field views, without Hertzon installed.

## What to build

### R-hzn7-1 — Simulate on an opened design

An opened `.hzn` design is a `C3dDocument` with setups (brief 2 R-hzn2-7). Simulate runs it through the existing `.c3d`
run path on EMerge, exactly as a native `.c3d` runs.

- **Results land in circuitRF's own results location** for a document with no cell: a scratch results folder beside
  the `.hzn`, named so it can never collide with Hertzon's `<project>.hznresults`. Never inside Hertzon's folder.
- The run note lists every Hertzon setup setting not used by circuitRF's run, with its value (R-hzn2-7). Examples:
  refinement method, error inclusion, matrix convergence.
- **Palace and openEMS are not offered for a `.hzn` design at first.** The setups are written for EMerge, and
  translating them to another solver's settings is a separate question.

### R-hzn7-2 — headless

`em <file.hzn> --design D [--setup S]` runs the same path. The `em` verb's existing walk-ups do not apply, because the
technology is the `.hzn`'s own (overview D6). `explain` on it reports which setup and sweep would run.

### R-hzn7-3 — the agreement gate (overview D7)

On two small fixture designs (a microstrip line with two lumped ports, and a waveguide section with two wave ports):

- run brief 7 and brief 8 at the same frequencies
- record |ΔS| per entry in `RESOLVED.md` §HZN
- set the tolerance from what is measured on converged meshes

A difference larger than the meshes explain is investigated before the brief closes, not widened into the tolerance.
This test needs both EMerge and Hertzon installed. It runs once and its numbers are recorded; it is not a routine test.

## Gates

- `HznEmergeRunTests`: the run path receives the translated document and setup, and the unused-settings note is exact.
  The solve itself is skipped without EMerge, and is kept to a few seconds when it runs.
- The CLI as a process matches the in-process run's outputs byte for byte, apart from timestamps.
- The agreement measurement, recorded.
