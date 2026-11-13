# HZN-14 — ports turned in a vertical plane, and Hertzon's Terminal solution

Series: `brief-hzn-0-overview.md`. Needs HZN-2, HZN-11 and HZN-13.

## Why

After HZN-13, 36 of Hertzon's 58 example designs open. Four more are each stopped by one thing. Removing that thing in a scratch copy
leaves each of them translating cleanly.

- **The Rotman lenses** (`Rotman`, `RotmanArray`):
  - **18 lumped-port sheets stand vertically**, from the ground (z = 0) to the top of each angled feed line (z = h). They lie in planes
    turned about z, along each feed's end edge.
  - A `.c3d` port turns only in the XY plane (`Angle`), so they are refused as `CreatePolyline outside an axis plane`.
  - Two more port sheets lie in axis planes but are drawn as 4-point polylines. They are refused only because a port sheet must be a
    `CreateRectangle`.
  - Every slanted sheet in these designs is a port sheet; no ordinary slanted sheet occurs.
- **The Terminal designs** (`DifferentialPair`, `G_DiffPair_Terminal`):
  - Two traces in a closed box, with no ground plane drawn. Each wave-port sheet spans both traces.
  - Hertzon solves them as terminals: one per conductor reaching the port.
  - circuitRF already has multi-terminal wave ports, including the box-referenced reading (brief-em3d-121). The translation refuses
    `SolutionType: Terminal` only because no brief mapped it.

Together these give **40 of 58**.

## Established black-box (Hertzon's own runs, one frequency, about 4 s each)

These were found by running Hertzon, never by reading its source.

- **Reference impedance.** A Terminal design's Touchstone has one row per terminal and states `R 50`, although its ports say
  `Renormalize: False`. Terminal S-parameters are referenced to each port's `RenormImpedance`.
- **Numbering.** Terminals are numbered port by port, in port-number order. Within a port they go by the conductor's position across
  the port face: x ascending on a y-facing port. Neither object order nor name order decides it: swapping the traces' order in the
  file kept the numbering, and swapping their positions changed it.
- **A conductor that does not reach a port** gives that port no terminal (a 3 × 3 result when one trace stops short).

## What to build

### R-hzn14-1 — a lumped port turned in a vertical plane

- **`Angle` on a port in the `XZ` or `YZ` plane** turns it about the vertical axis through its `Rect`'s centre, counter-clockwise seen
  from +z.
  - `Rect` is the rectangle before it is turned.
  - The plane's horizontal axis turns, and its vertical axis stays z.
  - A wave port and a port on a cylinder are still never turned; `XY` keeps its meaning.
- **Lowering:** the four corners come from one map, which `C3dTurnedPorts.Map` now gives for every plane. They become the neutral
  problem's `Em3dPortQuad`, which Palace (Gmsh) and EMerge already take. openEMS keeps refusing a turned port, naming it.
- **Contact** is inferred exactly as for any port, through the map.
- **Ends.** A vertical turned port's ends are horizontal edges. Each must lie on a horizontal surface of its conductor at its height:
  a sheet's plane, or an outline-extruded solid's top or bottom. Then:
  - an end inside the surface needs nothing;
  - an end along one straight side of the surface's outline makes the seam that XY turned ports already make, so the port and the
    conductor share their points exactly. A sheet's outline takes a seam as a prism's does.
  - Anything else is refused, naming the end and the conductor.
- **The editor:**
  - Placing a turned port in a vertical plane is allowed.
  - The Properties Angle field applies to XZ and YZ ports.
  - The scene draws the port from its quad, as it does for an XY turned port.
- **Spec:** `c3d-format.md` §7, the `Angle` line.

### R-hzn14-2 — HZN: slanted and polyline port sheets

- **A closed, covered polyline in a vertical plane** becomes a sheet in `XZ` (or `YZ`, whichever needs the smaller turn), rotated about
  z by its placement.
  - Its outline is (distance along the plane, z) from the plane's first point.
  - The z components keep their expressions. The horizontal ones are numbers, because a turn is not linear in them; the translation
    notes it when they named variables.
  - A polyline in any other non-axis plane stays refused.
- **A polyline that is a rectangle can be a port sheet:**
  - in an axis plane, as a rectangle;
  - in a vertical plane, as a port turned by R-hzn14-1's `Angle`.

  Any other polyline still refuses the port, with the reason.
- **Export:** a turned port (XY or vertical) is written as a 4-point polyline sheet, which reads back as the same port. A rotated
  vertical sheet is written as its polyline followed by its rotation.

### R-hzn14-3 — HZN: the Terminal solution type

- **`SolutionType: Terminal` translates.** Each wave port becomes a `.c3d` wave port with `Terminals`:
  - one terminal per conductor whose extent reaches the port's region;
  - numbered port by port in port-number order, then by the conductor's position across the face (world x, then y, then z);
  - named `<port>_<conductor>`, as Hertzon names them;
  - each with `Z0` the port's `RenormImpedance`.
  - A port reached by one conductor is an ordinary wave port numbered as that terminal. A port reached by none is refused.
- **`ReferenceConductor`:**
  - empty or `Outer` leaves `Reference` to inference, which takes the closed box's PEC faces;
  - any other value names an object, which becomes `Reference`.
- **Not read, and listed as unused settings:** `DifferentialPairs`, `OutputVariables`, `Sources`. These are Hertzon's mixed-mode
  reports and drive weights; circuitRF's result is the terminal S-parameters, as Hertzon's Touchstone is.
- A lumped port in a Terminal design is refused: how Hertzon numbers it among terminals is not established.
- **Export:**
  - a wave port with `Terminals` is written as one Wave Port (port number by order), with `RenormImpedance` the terminals' shared
    `Z0`;
  - the design's `SolutionType` is `Terminal` when any port has terminals;
  - terminals whose `Z0`s differ are refused.

## Gates

- **`C3dVerticalTurnedPortTests`**, one per claim:
  - a vertical turned port lowers to the quad its `Angle` turns;
  - from ground sheet to trace sheet it infers ground negative and trace positive;
  - an end along a sheet's side seams that sheet's outline;
  - an end off its surface is refused;
  - openEMS refuses it;
  - `Angle` on a wave port in a vertical plane is still refused.
- **HZN (fixtures written for the tests):**
  - a slanted polyline port sheet translates to a turned port;
  - an axis-plane rectangle polyline is a port;
  - a slanted non-port polyline is a rotated sheet;
  - a Terminal design gives terminals numbered by position, named, with `Z0` the `RenormImpedance`;
  - export writes a turned port as a polyline and terminals as one wave port with `SolutionType: Terminal`, and each reads back.
- **`HznCoverageTests`:** **40 of 58**. Gate 1 (save with no edit, byte for byte) holds for every newly opened design.
- **By hand, recorded in RESOLVED:**
  - Palace mesh-only of `Rotman` and `DifferentialPair` (ports lowered, mesh written).
  - `DifferentialPair` solved on circuitRF's EMerge at 5 GHz against Hertzon's own 4 × 4 at 5 GHz, with the tolerance HZN-7 recorded.

## Risk

Medium.
- **The ends check** is the new geometry. A slanted end that is a nanometre off its sheet's side must seam, not leave a sliver.
- **Terminal numbering** rests on three black-box runs, all with y-facing ports. A port facing another axis uses the same
  coordinate order, which is assumed; the translation notes it.

## Rules

As brief-hzn-10:
- one test per claim, named classes only;
- no `Category=Benchmark` test and no EM solve in a routine test;
- UI rendered with UiSnapshot and looked at;
- no commercial names;
- clean room;
- findings in `src/Design/RESOLVED.md` §HZN.
