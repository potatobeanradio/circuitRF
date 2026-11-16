# HZN-15 — geometry in tilted planes, and what an exported antenna model needs beside it

Series: `brief-hzn-0-overview.md`. Needs HZN-2, HZN-13 and HZN-14.

## Why

After HZN-14, 40 of Hertzon's 58 example designs open, and 25 of those 40 are the benchmark copies. Of the 18 still refused, the
largest group that one piece of work opens is the three runs (`run2`, `run3`, `run4`) of the second smart-glasses antenna example: one model of a glasses temple arm and its feed, exported from CAD into Hertzon. They are refused for four things, each
fixed here:

- **Twelve polylines in tilted planes**: the board, the feed traces and pads, the port sheet and the chassis pieces lie in planes up to
  3° (and, for the hinge pieces, up to 90°) from every axis plane, and most are swept along vectors off their normals.
- **Faceted cylinders** (`NumberOfSegments` 10 and 12): the feed pin and the ground pins.
- **Unite tools the file does not record as consumed**: half the antenna's branches have `consumed_by: null`, though the Unite names
  them.
- **A lumped port in a Terminal design.**

Behind those, two the census did not show:
- the port sheet is a trapezoid, not a rectangle: it joins the trace's edge to the board's side face, two edges 0.2° from parallel;
- a steel Finite Conductivity boundary on a copper chassis part was translated as nothing.

`run2` also needs a one-sided lossy sheet (Ex1's feature), so this brief opens `run3` and `run4`.

Part 0 (done first, the same session): the two `PlaneWave` designs, which TRN-5's built-in `frequency()` closed (a design variable of
that name), and behind that a non-model field-plot plane 0.6 µm past the box carrying the radiation boundary, and an open polyline.

## Established black-box (Hertzon's own runs; never its source)

- **Faceted cylinder.** Read from the nodes of Hertzon's own mesh (`mesh.npz`) of cylinders of 12, 10 and 7 sides on Z, X and Y: the
  N vertices lie ON the circle, the first along the next axis round from the cylinder's (Z → +x, X → +y, Y → +z), every 360°/N.
- **Lumped ports in a Terminal design.** The small microstrip solved as Modal and as Terminal, with one port at 25 Ω and the port
  numbers swapped, gives the same Touchstone to every printed digit: ports by `PortNumber`, each referred to its `Impedance`.

## What was built

### Part 0 — PlaneWave
- A design variable named after a circuitRF built-in is `<name>_hzn` in the document (`HznNames.C3dVariableName`); every reference
  follows, a note says so, and the save writes the original name back (`HznNames.HznVariableName`, used by the writer and patcher).
- An object that is not in the model never moves the outer extent a boundary must lie on.
- An open or uncovered polyline is a `.c3d` construction polyline (`Points3`, each component keeping its expression).

### R-hzn15-1 — a polyline in a tilted plane
A closed, covered polyline whose points lie in one plane (least squares, within 2 DBU or a part in 10⁴ of their extent, noted when
over 2 DBU) that is neither an axis plane nor upright becomes a `Sheet` on the nearest axis plane, its outline the points projected
into that plane's frame, placed by the SMALLEST rotation carrying the axis plane onto its own (`C3dTilt`, written Z, Y, X) and its
first point. No format change: an ordinary `Sheet` with an ordinary `Placement`. Its points are numbers (noted when they named
variables).

### R-hzn15-2 — sweeps off the normal
- A tilted polygon swept along any vector is a `Prism` in the same frame: `Height` the vector's part along the frame's normal,
  `Shear` the rest.
- An axis-plane rectangle or polygon swept off its normal is a `Prism` whose `Shear` is the vector's in-plane part (expressions kept).
- A circle swept off its normal (an oblique cylinder) is refused by name.

### R-hzn15-3 — a lumped port drawn as its corners
- **Format:** `C3dPort.Corners` (`c3d-format.md`): four world-DBU corners instead of `Rect` on `Plane`; a rectangle to 1 %, flat to
  10⁻⁵. `Em3dPortQuad.Problem(DrawnTolerance)` checks it; `Extent` takes the mean of opposite sides.
- **Port code:** `C3dTurnedPorts.Frame` is the one map every planar port is measured through (Rect, Angle or Corners; for Corners a
  bilinear map whose parameter rectangle's edges are the drawn sides). Contact inference is unchanged; a drawn port's tolerance is
  4 DBU (its corners and a tilted shape are each rounded in their own frame). Each end must lie on its conductor's surface
  (`DrawnEnds`), and the resolved quad has its ends moved exactly onto it (`SnapEnds`: a sheet's outline vertex, else its side, else
  the surface).
- **Translation:** a tilted four-point sheet that is such a rectangle becomes `Corners`; a wave port on one is refused.

### R-hzn15-4 — faceted cylinders
`NumberOfSegments` 3…1024 is a `Prism` of that many sides by the established convention, each vertex keeping the centre's and
radius's expressions; 1, 2 and fractions are refused.

### R-hzn15-5 — tools a boolean consumes though the file does not say so
A tool of a non-keeping Subtract/Unite/Intersect whose `consumed_by` is empty is taken as consumed by the one object whose history
names it: drawn inside the boolean only, with a note.

### R-hzn15-6 — Terminal designs with lumped ports only
Such a port translates as a modal one (by the black-box above). A lumped port beside wave ports in a Terminal design stays refused.

### Also
- A Finite Conductivity boundary on a conductor solid makes the solid that metal (circuitRF solves a conductor by its faces' metal).

### The EMerge driver (found by solving `run4`)
- A sheet's, a lumped element's or a port's piece inside a conductor goes when the conductor's interior is removed; the driver now
  keeps only the pieces left, as it already did for wave-port regions (two "unknown face" stops).
- A turned port's sheet is built from its corners (the fourth put on the plane of the other three), not origin + u + v.
- **A turned port's voltage lines are the driver's own** (`drawn_port_lines`): five lines from the negative end to the positive,
  inset 1 % and scaled back. EMerge starts its own at the sheet nodes whose projection on the direction is exactly the least, which on
  a turned sheet is one corner: the fixture's open port read S11 = −0.967 at 180° at every frequency until this.
- With a turned port, a model the kernel cannot fragment exactly is built again joining within 0.1 nm, then 1 nm, with a warning.

## Gates

- `HznBuiltInNamesAndConstructionTests` (Part 0, fixture `plane-wave-box.hzn`): the rename and its references; the save writes the
  original name; a non-model plane past the box; an open polyline.
- `HznTiltedGeometryTests` (fixture `tilted-shapes.hzn`, written by a script for the tests): a tilted sheet's placement puts every
  point where it was drawn (to 3 nm); a tilted sweep's prism has the drawn bottom and top; an axis rectangle swept off its normal;
  faceted cylinders on Z and Y; the unrecorded Unite tool; the steel boundary on copper; the trapezoid port is its corners and
  resolves board → trace with no validation problem; a lumped port in a Terminal design, refused beside a wave port.
- `HznCoverageTests`: **44 of 58** (from 40: Part 0's two `PlaneWave` designs and HZN-15's two smart-glasses runs). Gate 1 (no-edit save,
  byte for byte) holds for each.
- **By hand, recorded in RESOLVED:** the fixture's tilted port solved on EMerge reads a capacitive open at 2 GHz for three port angles;
  `run4` solved on circuitRF's EMerge against Hertzon's published `A29.s1p`: it runs (6 min, 363k tetrahedra) and resonates, but does
  not yet agree — low band 2.2 GHz against Hertzon's 2.45, high band 6.15 against 6.0, and no 7.25 GHz parasitic dip. A held finer mesh
  (616k tetrahedra, 33 min) gives the same curve, so the difference is in the model, not the mesh: an open item (RESOLVED).

## Risk

- The port sheet's fragmentation against a trace it meets at 3° is numerically fragile: an exact boolean, 1 nm and 10⁻⁵ nm all fail on
  the full model and 0.1 nm passes. The retry ladder is what holds it.
- Turned (`Angle`) ports now also take the driver's voltage lines and corner-built sheets.

## Rules

As brief-hzn-10: one test per claim, named classes only; no EM solve in a routine test; no commercial names; clean room; findings in
`src/Design/RESOLVED.md` §HZN.
