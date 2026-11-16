# HZN-17 — polylines with holes, and a port sheet drawn as a quadrilateral in an axis plane

Series: `brief-hzn-0-overview.md`. Needs HZN-13, HZN-14 and HZN-15.

## Why

Hertzon 0.3.26 ships 52 example files holding 99 3D designs. 49 translate. Of the 50 refused, 37 are one family: the runB and
runC sets of the second smart-glasses antenna example, a sweep of antenna variants on one board. Every one of the 37 is stopped
by the same two items:

- **`CreatePolyline with holes`.** The flexible circuit's substrate (`FPC_sub`) is a polyline with two holes: a 16-sided round
  hole and a small quadrilateral slot. The `Holes` property holds the rings as numbers, `x,y,z;x,y,z;…`, separated by `|`.
- **`Lumped Port on 'A_port'` / `'Port'`.** The port sheet is four points in the XY plane that are not a rectangle. It joins
  the board's slanted edge (slope 0.0055866) to the feed pad: two sides are parallel to y, and the other two follow the edge.
  Its corners are 0.32° from square. HZN-15's drawn-corner port takes exactly this shape in a tilted plane, but in an axis plane
  only an exact rectangle (or an exact turned one) is a port sheet.

With both removed by hand from a scratch copy, 35 of the 37 translate (`Ex2_Benchmark` also has a Multi-Frequencies setup, and
`T_thick` a `ThickenSheet`). The census then goes from **49 to 84 of 99**.

## What is already there

- **`.c3d` holes, end to end.** `C3dPrism.Holes` and `C3dSheet.Holes` (numbers only) are read, written, lowered
  (`C3dLowering`), solved on all three solvers, met by port seams (`C3dTurnedPorts.SeamRing`), cut by sheet booleans, and
  exported to STEP. Nothing in the format or the engine changes here.
- **Hole validity, from the 2D layout.** `.clay` polygons have had holes since §3.1a, and `LayoutClipper.HolesAreValid` (in
  `src/Design`, as the translator is) checks the three conditions: every hole vertex inside or on the outer ring, no hole
  crossing the outer ring, no two holes crossing. It is prefiltered by bounding boxes and banded for large rings, and tested
  against a brute-force copy. This brief reuses it rather than writing a second one. The `.clay` loader goes on to repair an
  invalid shape with a Clipper union (`EnsureValidHoles`); the translator does not, because Hertzon's own repair (a bridge of a
  few µm between touching rings, per its changelog) is a different shape, and a quietly different shape is worse than a refusal.
- **Drawn-corner ports.** `C3dPort.Corners`, `Em3dPortQuad.Problem(DrawnTolerance)` (a rectangle to 1 %), `C3dTurnedPorts.Frame`
  (the bilinear map), `DrawnEnds`/`SnapEnds`. The frame takes its normal from the corners, so an axis-plane quadrilateral needs
  nothing new on the port side.

## Established from Hertzon's own documentation (never its source)

- A polyline's `Holes` are inner rings of its covered face, in whatever direction they were drawn (changelog 0.3.1, "a second
  closed loop inside the outline becomes a hole, whichever way it was drawn"; the PCB import writes "one covered polyline per
  copper island, its holes in the *Holes* property").

## What to build

### R-hzn17-1 — read a polyline's holes
- A closed, covered polyline in an **axis plane** with a non-empty `Holes` becomes a sheet (unswept) or a prism (swept along its
  normal or off it) carrying each ring in `Holes`.
- Each ring needs at least three distinct points, and each point must lie in the outline's plane to the DBU. Anything else is
  refused by name.
- A hole component that names a variable is evaluated, and a note says so (`.c3d` holes hold numbers).
- `LayoutClipper.HolesAreValid` is checked on the outline and holes in DBU. If it fails, the polyline is refused with "a hole lies
  outside its outline, crosses it, or crosses another hole".
- A polyline with holes in an upright or tilted plane stays refused (`CreatePolyline with holes outside an axis plane`), and so
  does one with arcs or splines. Neither appears in any example.

### R-hzn17-2 — a quadrilateral port sheet in an axis plane
- Four points in an axis plane that are no exact rectangle (`AxisRect`) and no exact turned rectangle (`TurnedXyRect`), but pass
  `Em3dPortQuad.Problem(DrawnTolerance)`, are the port sheet they are, as **`Corners`** in world DBU, in drawn order.
- This is the axis-plane twin of HZN-15's `TiltedRect`: the same check, the same `SheetGeom { Corners }`, so `Port` takes it
  unchanged. A wave port on one stays refused.
- When the points named variables (runC's `xf`, `gp`), a note says the corners are numbers, as HZN-15's tilted sheets do.

### R-hzn17-3 — save and export
- `C3dToHzn` writes a prism's and a polyline sheet's `Holes` in Hertzon's spelling, each point carrying the plane's coordinate
  as the outline's do, instead of refusing them.
- That is all the edit-and-save path needs: `HznPatch` diffs the writer's output for the baseline against the edited document,
  so an unchanged substrate writes nothing and a moved hole rewrites that one step.

## Gates

- **`HznPolylineHolesAndQuadPortTests`** (Ui.Tests/Hzn, synthetic fixtures in the test, no Hertzon file needed), one per claim:
  - a swept polyline with two holes is a prism whose `Holes` are those rings in DBU;
  - an unswept one is a sheet with its holes;
  - a hole crossing its outline is refused by that item, and so is a hole off the plane;
  - an XY parallelogram port sheet (0.32° skew) becomes a lumped port with `Corners`, and the port validates;
  - export writes `Holes` and the translation of the export reads the same rings back.
- **Real files, under `CIRCUITRF_HERTZON_HOME`:** runB `K1_IFA` translates with the holed substrate and a drawn-corner port, and
  `check` on its `.c3d` reports no errors. The census reports **84 of 99**.
- **Dry run:** `K1_IFA` on EMerge with `dryRun` (meshing plan, no solve), recorded in the completion note.
- **UI:** UiSnapshot `--c3d` of `K1_IFA` (the substrate's round hole and slot drawn, the port on the board edge). Look at it and
  list it.

## Risk

- Opening is not agreeing. These are the same model family as run4, whose disagreement with its measured reference (low band
  2.2 against 2.45 GHz) is still unexplained. No runB/runC result is compared here.
- A port's corners are numbers, so a runC variable that moves the feed moves the copper and not the port. The note says so.

## Rules

As brief-hzn-10. Two other agents share the repo: stage only this brief's files.

## Completion note (2026-11-16)

Built as specified, with one addition the real files forced:

- **Census 49 → 84 of 99**, as the probe predicted. `HznPolylineHolesAndQuadPortTests`: the five synthetic gates pass, and so does
  the real-file gate (runB `K1_IFA` and runC `A_IFA` assemble into problems with no validation finding).
- **Addition: a drawn port's end tolerance scales with the port.** runB's port corners are written to 0.1 µm, so its end sat 27 nm
  off the board's slanted edge against 4 nm allowed. `C3dTurnedPorts.DrawnEndTolerance` is now 4 DBU or a part in 10⁴ of the port's
  longest side, used by the end check and the snap only. `c3d-format.md` says so.
- **Dry run:** `K1_IFA` converted to a `.c3d` (`convert --add-materials`), `check` gives 0 errors and 0 warnings, and `em --dry-run`
  gives no refusal (Palace estimate about 81k unknowns at order 2). Its grid notes name the substrate's hole faces, so the holes reach
  the solved model.
- **UI:** UiSnapshot `--c3d` of `K1_IFA`: the substrate alone from above shows the round hole and the tilted slot cut through it, and a
  close-up at the feed shows the port between the board and the pad, its arrow along +y. Both were looked at.
- **Neighbouring tests:** every `Hzn` class and the `C3dPort`/turned-port classes, 227 passed. Five `Hertzon_Describes_*` tests fail
  because the freshly downloaded Hertzon has no `.venv/bin/hertzon-cli`. That is environmental and not on this change's path.

Findings: `src/Design/RESOLVED.md` §HZN-17.
