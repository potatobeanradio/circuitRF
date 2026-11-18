# PWL-3 — The wall port: file keys, resolution, standards, and Palace end to end

Series overview: `brief-pwl-0-overview.md`. Needs brief 1 (`LineCalibrator`) and brief 2's decisions.

## Decisions from brief 2

Fill these in from `src/Design/Em3d/RESOLVED.md` §PWL-2 before starting, and keep them here:

- Default gap `g`, and the floor below which the run refuses: ______
- Port-region mesh: independent / extruded block / extruded prisms: ______
- Source of C′: ______
- Default end run, in substrate heights: ______
- Tolerances for overview gates 3 and 4: ______

## What a user writes

**In a `.c3d`**, a wall port states what a wave port states — its plane, its offset and a rectangle round the line's
end on that plane — with `"Kind": "Wall"`:

```
{ "Name": "P1", "Kind": "Wall", "Number": 1, "Z0": "50", "Plane": "YZ", "Offset": 0,
  "Rect": { ... }, "OffsetUm": 0 }
```

`Positive` is inferred (the one non-ground conductor whose end meets the rectangle) and may be stated; the wall is
always the negative side. `Flip`, `Model` and `Name` work as on every port. `OffsetUm` moves the reference plane into
the structure, as on a wave port.

**In a `.cem`**, a layout-labelled port takes `"Kind": "Wall"` in `Ports3D`, beside `Lumped` and `Wave`:

```
"Ports3D": [ { "Port": 1, "Kind": "Wall" }, { "Port": 2, "Kind": "Wall", "OffsetUm": 500 } ]
```

**In a 3D view's own setup**, the same `Ports3D` key chooses the kind of a port on an air-box face **for that setup
only**, overriding what the port states:

```
"Ports3D": [ { "Port": 1, "Kind": "Wall" }, { "Port": 2, "Kind": "Wall" } ]
```

A 3D view's setup ignores `Ports3D` today (a `.c3d` port carries its own kind). From this brief it reads it, for
**Wave and Wall only, on ports on an air-box face**: these two kinds state the same plane, offset and rectangle, so a
setup can choose between them without changing the drawing. Rules:

- Omitted, or a row without `Kind`: the port runs as the document states it. Nothing shipped states one, so nothing
  shipped moves.
- A row that names a port off the air box's faces, or a kind other than Wave or Wall, is refused by `check` and the
  run, naming the port, the setup and the reason. It is never silently ignored.
- A face's ports must end up one kind in each setup (R-pwl3-5); a setup that leaves a face mixed is refused, naming
  the ports.
- A wave port with terminals that a setup runs as Wall becomes one wall port per terminal for that run, keeping their
  numbers, as the editor's switch does (brief 4 R-pwl4-3).
- `explain` lists each port's kind per setup and whether it came from the port or from the setup.

The planar `.cem` key's meaning is unchanged: for a layout there is no drawn port kind, so `Ports3D` is the only place
a kind is stated.

**One setup key**, in both: `"CalibrateWallPorts": false` publishes the raw answer at the gap, in Z0, with a note
saying it is uncalibrated. Omitted at its default (true).

## Requirements

### Resolution (`C3dPorts`, `Em3dGenerator`)

- **R-pwl3-1 — The face.** The air box's face on the port's plane becomes PEC with zero padding for every setup that
  runs the port, whatever the setup states, and the run's notes say so once per face (the rule a wave port's face
  already follows, inverted). The line must reach the face; a line that stops short, or an object reaching further on
  that side, is refused as a wave port's is, naming the port and the face.
- **R-pwl3-2 — The gap.** In the SOLVED problem the line is cut back by `g` from the face; the drawn document is not
  changed. The lumped source fills the gap from the face (−) to the line's new end (+), across the line's width at its
  end, in the plane of the strip's face toward its return (the lower face for a strip over its ground). `Em3dPort`
  carries `Kind = Wall`, the gap, the face and its calibration group.
- **R-pwl3-3 — Groups.** Wall ports on one face form groups by distance (overview D6). Each port's group is in the
  resolved port and in `explain`. A group above `MaxCalibrationGroupSize`, or one whose modes are of (nearly) one
  speed (asked from the cross-section's electrostatics, as the planar solver asks it), is refused by name with the
  alternative stated.
- **R-pwl3-4 — The end run.** Over the end-run length from the face, every object crossing the group's region must be
  a prism along the line's axis with an unchanging cross-section (overview D8). Otherwise refused, naming the object
  and its distance from the face, with "extend the line" as the remedy.
- **R-pwl3-5 — Mixed faces.** One face carries wall ports or wave ports, not both; refused by name. Lumped ports
  elsewhere in the same setup are fine, and so are wave ports on other faces.
- **R-pwl3-6 — Conductors on the wall that are not ports** are grounded by it (overview D7); `check` warns, naming each.

### The standards (`src/Design/Em3d/WallStandards.cs`)

- **R-pwl3-7 — Built from the structure, never redrawn.** A group's standard of separation ℓ is the group's
  cross-section, taken from the solved problem's end run, extruded between two PEC faces ℓ apart: the same materials,
  the same lateral extent and boundaries of the air box, the same mesh settings, the group's conductors with a wall
  port at each end (mirror images), non-port conductors within the group's region kept, other groups' port conductors
  left out. The port region is meshed per brief 2's decision, and a routine test asserts on COORDINATES that the
  standard's end-run block is the structure's.
- **R-pwl3-8 — The plan.** Separations come from brief 1's separation plan with ε_eff estimated from the cross-section
  (the same C′ source, with C′₀ for the empty cross-section). Both walls of a two-port that share a cross-section share
  their standards.
- **R-pwl3-9 — The cache.** Standards' raw S are stored beside the run's results, keyed by the cross-section's geometry
  and materials, the solver and its mesh settings, and the frequency list (overview D9). A key that matches is reused
  and the notes say so; a tuning sweep that does not move the end run pays once.

### The run (`Em3dRunService` and its Palace path)

- **R-pwl3-10 — One run, many solves.** The structure and every standard not in the cache are solved by the same solver
  path, in sequence, under one `RunControl`: progress counts all of them, cancellation abandons all of them and
  publishes nothing.
- **R-pwl3-11 — Calibrate.** Per frequency, in increasing order: `LineCalibrator` with the structure's raw S, the
  standards' raw S and C′; the de-embedded S at the reference planes; `OffsetUm` peeled with the measured γ; each port
  renormalised to its Z0. Ports that are not wall ports pass through untouched.
- **R-pwl3-12 — What is published.** The `.sNp` and `.npy` as for every 3D run, with the Touchstone header naming each
  wall group and how it was calibrated. The diagnostics group gets, per wall group, γ and ε_eff per mode, Z_c per mode,
  the calibration residuals, and the raw S at the gap (`__`-prefixed). The passivity check of the planar solver
  (σ_max(S) ≤ 1 + 1e-3 against a uniform real reference) is made on the published answer and reported as a note.
- **R-pwl3-13 — Palace.** The gap sheet is a `LumpedPort` with its direction along the line, from the face to the
  line. No other Palace change.
- **R-pwl3-14 — Dry run, check, explain.** `run dryRun=true` prices the structure and every uncached standard, and the
  total. `check` runs R-pwl3-1…6 with no solve. `explain` lists each wall: its face, its group's ports, the end run, the
  standards' separations, the C′ source and whether each standard is cached.

### Solver menus

- **R-pwl3-15** — EMerge and openEMS refuse a wall port by name in this brief ("runs on Palace; EMerge and openEMS
  follow"), and briefs 5 and 6 lift those refusals. Nothing writes a wall port as a plain lumped port.

## Gates

1. Overview gate 1 (nothing shipped moves): every shipped `.c3d`/`.cem` lowers to byte-identical Palace input.
2. Overview gates 3 and 4 on Palace, on `testdata/pwl/` S and C, to brief 2's tolerances (`Category=Benchmark`).
3. One routine test per refusal in R-pwl3-1…5, one for a 3D view whose two setups run the same face as Wave and as
   Wall (each lowers to its own kind, the document untouched), one per 3D-view `Ports3D` refusal, one for the standards' coordinates (R-pwl3-7), one for the cache hit
   (R-pwl3-9, with a stub solver), one for `CalibrateWallPorts: false`.

## Docs

`docs/user/src/reference/em-setup.md`: a **Wall ports** section beside *Wave ports* — what it is, where the reference
plane is, groups, the end run, what the standards cost and that they are cached, what the diagnostics show, the setup
switch, every refusal. `docs/design/c3d-format.md`: the key. The MCP `reference` text for `em-setup` and `3d-view`.

## Completion

`src/Design/Em3d/RESOLVED.md` §PWL-3: what was built, the measured gate numbers, and anything brief 2 did not predict.
