# HZN-16 — a periodic unit cell and its Floquet ports

Series: `brief-hzn-0-overview.md`. Needs HZN-11 (wave ports normalised to their modes) and HZN-15.

## Why

Two of Hertzon's examples are periodic unit cells: the crossed-dipole frequency-selective surface (`FSS_UnitCell` / `CrossDipoleFSS`,
two Floquet ports) and the showcase's reflecting cell (`F_UnitCell_Periodic`, one Floquet port over a perfect backplane). Each is
refused for its Primary/Secondary boundaries and its Floquet ports alone. A periodic cell is also a capability circuitRF did not have
at all: frequency-selective surfaces, reflectarray elements and metasurfaces are solved as one cell of an infinite array.

EMerge, the solver Hertzon itself runs, has a rectangular periodic cell (`RectCell`), periodic conditions with a scan phase and a
Floquet port that absorbs the TE and TM waves of the (0, 0) harmonic and the grating harmonics.

## What was built

### Engine (`Em3dProblem.cs`)
- `Em3dBoundaryKind.Periodic`: one face of an x or y pair; both faces of an axis or neither, never z (`ValidatePeriodic`).
- `Em3dAirBox.Scan` (`Em3dScan` θ, φ in degrees; θ in [0°, 90°)).
- `Em3dPort.Floquet` (`Em3dFloquet` Mode, Modes, Er): one neutral wave port per mode, the modes of one port sharing a face group,
  normalised to the mode. Validated: a driven problem, a cell periodic in x and y, the whole zmin or zmax face, one entry per mode.
  A Floquet group is not a terminal group (`ValidateFaceGroups` skips it; the end-name and hollow-guide checks skip its modes).

### Format
- Setup `AirBox` faces take `"Boundary": "Periodic"`; the `AirBox` takes `"Scan": {"ThetaDeg", "PhiDeg"}` (`CemScan`, carried by
  `EmAirBox.Scan` into the generator's air box).
- `C3dPort.Floquet` {`Modes`, `Er`} on a wave port whose `Rect` is the whole face, `Z0` `"mode"`. Its modes take `Number`,
  `Number + 1`, …, in the one port namespace (`C3dPorts.Numbers`). `c3d-format.md` §7 and §10.3, the CLI schema, the user
  reference (*em-setup*, "A periodic cell and Floquet ports").

### EMerge
- The problem file gains `periodic` {x, y, thetaDeg, phiDeg} and `floquetPorts` [{number, name, face, region, er, modes}], written only
  when present (every other problem file is the bytes it was).
- The driver builds the cell after the conditions are cleared (clearing forgets one) and before the mesh: EMerge's `RectCell`, its
  width and height in metres (the Floquet lattice), finding and pairing the faces itself in Gmsh's model units from the kernel's
  centres. EMerge's own pairing failed twice: its face planes were in metres against a model in micrometres, and after the mesh it
  reports a face's centre in metres while the kernel's stay in model units. Its periodic conditions take the displacement in metres.
- A Floquet port is `sim.mw.bc.floquet_port` on its face region, numbered as one EMerge port whose modes are `p.1`, `p.2`, …; the scan
  is set on the cell. Its region is kept out of the box face's own condition. Its S is modal as solved: the run service's terminal
  conversion is for terminal groups only.
- EMerge's local mesh refinement splits a periodic face's triangles without splitting its partner's alike, and the periodic condition
  then stops (an index out of bounds pairing triangles), so a cell is solved on its first mesh, with a warning naming the two mesh
  settings that make it finer.
- Palace and openEMS refuse a periodic cell and a Floquet port by name (`Em3dModePorts.SolverRefusal`).

### Translation
- `Primary` and `Secondary` boundaries on the cell's outer faces (drawn as sheets there, removed, or named as faces of the box that
  bounds a closed design) are the air box's periodic faces. Each Secondary names a Primary on the opposite face of the same axis;
  `PhaseMode: Scan Angles` gives the scan (every Secondary must agree; a number in the setup, noted when it named a variable); any
  other phase mode is refused. `UVector` is an unused setting.
- A `Floquet Port` on a sheet or a face covering the cell's zmin or zmax face is a Floquet port with `NumberOfModes` and
  `Permittivity`. Floquet ports are numbered by PortNumber with their modes expanded (port 1's modes 1 and 2, port 2's 3 and 4, as
  Hertzon's Touchstone numbers them); beside other ports they are refused (their joint numbering is not established).
- A Perfect E on an outer face of a closed periodic cell (its backplane) is the air box's PEC face there.
- Export refuses a Floquet port and a periodic face by name; a `.hzn` opened from Hertzon keeps its own when saved unedited.

## Gates

- `HznPeriodicCellTests` (fixture `periodic-cell.hzn`, written by a script for the tests): the periodic pairs and scan on the setup and
  the sheets gone; the Floquet ports over their faces numbered 1 and 3; the problem's four mode ports and the EMerge problem file's
  `periodic` and `floquetPorts`; Palace refuses, EMerge does not; a lone periodic face and a Floquet port over part of its face
  refused; a Secondary without its Primary refused; the writer refuses a Floquet port.
- `HznCoverageTests`: **46 of 58** with HZN-15 (`CrossDipoleFSS`, `F_UnitCell_Periodic`).
- **By hand, recorded in RESOLVED:** both cells solved on circuitRF's EMerge against Hertzon's own runs.

## Risk

- No mesh refinement on a cell: accuracy at a sharp resonance rests on the first mesh.
- An oblique scan was translated and written, and a broadside scan solved; no oblique solve was compared.

## Rules

As brief-hzn-10.
