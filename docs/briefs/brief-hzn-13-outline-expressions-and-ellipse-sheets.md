# HZN-13 — expressions in outlines, and circular and elliptical sheets

Series: `brief-hzn-0-overview.md`. Needs HZN-2 and HZN-10 (sheet booleans).

## Why

After HZN-12, 33 of Hertzon's 58 example designs open. A census of the other 25 asks, for each refusal, what stops the design and
what would stop it next once that refusal is lifted. Two refusals stand out:

- **`polyline outline built from named dimensions`.** A Hertzon polyline's points may be expressions (`-wo/2+w, -s/2-dep, h`). A
  `.c3d` `Outline` holds numbers only, so HZN-2 refuses such a polyline rather than freeze it. It is the only thing stopping
  `HairpinFilter` and the scaling benchmark's `medium_hairpin`. It is also the first refusal in both Rotman lens designs and the GPS
  patch.
- **`CreateCircle` as a sheet.** A circle swept along its axis already becomes a cylinder, but an unswept circle (a sheet) has no `.c3d`
  equivalent. This and the polyline refusal are the only two things stopping `GPS_L1`, whose ground plane subtracts a circular anti-pad.
  The circle refusal is also the first in `GerberTestBoard` and `E_CoaxMonopole_PML`.

Lifting both gives **36 of 58**, measured by relaxing the two refusals in a scratch build and re-running the census.

Both are planned in `docs/design/c3d-format.md` §10:
- §10.1 already has a row for the ellipse sheet;
- the expression rule (§3) names `Outline` as numbers-only only because no brief had needed otherwise.

Both are useful to circuitRF's own users too: a hairpin or stepped-impedance outline sized by variables, and a round pad or anti-pad
drawn in the 3D editor.

**Not in scope:**
- `Coax50`. Its wave ports sit on circular sheets, so it also needs a circular *port* shape. That is a port change, not a sheet change.
- The Rotman lenses. Their port sheets stand in slanted vertical planes (the next census item).

## What to build

### R-hzn13-1 — expressions in a prism's and a sheet's `Outline`

- `C3dPrism.Outline` and `C3dSheet.Outline` become point lists whose components may each hold `{ "Expr": …, "Unit": … }`, exactly as a
  polyline's `Points` do (brief-em3d-132). They are keyed `Outline[k]`, one slot per component (u, v).
- `Holes` stay numbers. Neither Hertzon nor any other source needs them, and keeping them out keeps the renumbering rule to one list.
- **Resolution** writes each bound component's value into the point, as for every other field, so nothing below the resolver changes.
- **The 3D editor:**
  - **Typing a point in Properties replaces that point's expressions with the typed numbers.** It is one undo step, and this is the
    polyline's rule.
  - A vertex dragged in the canvas goes through the drag rule (R-em3d51-6): it writes the name, or is refused at the grab with *Replace
    with number*.
  - A point row whose point holds an expression says so: the expression text in the row's tooltip, and a mark beside its number.
- **Spec:** `c3d-format.md` §3 lists `Outline` (prism, sheet) among the fields that may hold an expression; `Holes` and polyhedron
  `Vertices` stay numbers. The CLI reference text (`DocumentSchema`) says the same.

### R-hzn13-2 — the ellipse sheet

- `C3dSheet` gains a third shape beside `Rect` and `Outline`: `Ellipse { Centre [u,v], Radii [ru, rv], Segments? }` on the sheet's
  `Plane` at its `Offset`.
  - `Centre` and `Radii` are named dimensions and may hold expressions.
  - `Segments` is a count, omitted for the default.
  - A circle is an ellipse with equal radii.
- **Validation** (`C3dValidation`, `check`):
  - exactly one of `Rect`, `Outline`, `Ellipse`;
  - both radii positive;
  - `Segments`, when stated, a whole number from 8 to 1024.
  - A heat source's sheet is its own type (`C3dHeatSheet`) and is untouched.
- **Lowering: one polygon, for every consumer.** At elaboration the ellipse becomes the polygon of `Segments` vertices (default **72**):
  - the vertices lie on the ellipse in DBU;
  - the first vertex is at the +u end;
  - they run counter-clockwise in (u, v).

  So the scene, picking, ports' touch tests, the sheet booleans and all three solvers see an ordinary sheet and need nothing new.

  At 72 vertices the polygon is 0.13 % smaller in area than the ellipse, and its edges come at most 0.095 % of the radius inside it. The
  completion note records both.

  A true curved disc in Gmsh is deferred. It matters only where a disc must meet a curved solid edge exactly, which is the coax-port case
  that is out of scope here.
- **Sheet booleans:** an ellipse operand takes part through the same polygon, so a ground plane minus a circle is a plane with a round
  hole.
- **The 3D editor:**
  - Properties shows `Centre` and `Radii` as dimension rows, labelled *Centre u/v* and *Radius u/v*, with expressions as for any field.
  - Vertex and edge moves on an ellipse are refused, naming the fields to edit instead.
  - Moving the whole sheet in its plane moves `Centre`, through the drag rule.
- An image on an ellipse sheet fills the ellipse's bounding rectangle and is clipped to it, as on an edited outline.
- **Spec:** `c3d-format.md` §4.3's `Sheet` row gains `Ellipse`; the §10.1 row is marked built.

### R-hzn13-3 — HZN: translate, save and export

- **HZN-2, polylines:**
  - The in-plane components of each point keep their expressions in `Outline[k]`.
  - The normal component becomes the sheet's (or prism's) `Offset`, taken from the first point. The plane test still runs on the
    evaluated points.
  - When the points spell their normal component differently but evaluate equal, the translation notes it.
- **HZN-2, circles and ellipses:**
  - An unswept `CreateCircle` becomes an `Ellipse` sheet with both radii the `Radius` expression.
  - An unswept `CreateEllipse` on `Axis: Z` becomes `Radii [MajorRadius, (MajorRadius)*(Ratio)]`. The major radius lies along x;
    this was established black-box from Hertzon's own geometry report, a 2 × 1 bounding box for `MajorRadius` 2 and `Ratio` 0.5.
  - `CreateEllipse` on `X` or `Y` is refused by name: its major axis direction there is not established.
  - A swept ellipse (an elliptic cylinder) is refused: it has no `.c3d` type.
  - A non-zero `NumberOfSegments` stays refused, as for cylinders.
- **HZN-4/HZN-5:**
  - An `Outline` point's expressions are written as the polyline point's expressions.
  - `Offset` is written into every point. It was frozen to a number before; the reader now keeps it.
  - An `Ellipse` with equal radii is written as `CreateCircle` (`Axis` the plane's normal).
  - An `Ellipse` on `XY` with `ru ≥ rv` is written as `CreateEllipse` with `MajorRadius` ru and `Ratio` (rv)/(ru).
  - Any other ellipse is refused by name.
  - A stated `Segments` is refused: Hertzon's faceted circle is not known to put its vertices where circuitRF does.
- **Gate 1 (unchanged):** every design that opens saves with no edit byte for byte.

## Gates

- **`C3dOutlineExpressionTests`**, one per claim:
  - an outline with expressions round-trips byte for byte;
  - resolution moves the vertex when the variable changes;
  - a point typed in Properties replaces its expressions;
  - `check` names an unresolvable outline expression by point.
- **`C3dEllipseSheetTests`**, one per claim:
  - an ellipse sheet round-trips;
  - its lowered polygon has `Segments` vertices on the ellipse, the first at +u, counter-clockwise;
  - validation refuses two shapes, a zero radius, and a bad `Segments`;
  - a plane minus a circle is a plane with one hole;
  - a radius bound to a variable follows it.
- **HZN:**
  - `HairpinFilter` translates with expressions in its outlines.
  - `GPS_L1` translates with an `Ellipse` operand under its ground plane.
  - Export writes `CreateCircle` and `CreateEllipse` and refuses the rest.
  - `HznCoverageTests` under `CIRCUITRF_HERTZON_HOME` reports **36 of 58**; record the next items in `src/Design/RESOLVED.md` §HZN.
- **Dry runs:** `HairpinFilter` and `GPS_L1` on EMerge, with `dryRun` (meshing plan, no solve), recorded in the completion note.
- **UI:** UiSnapshot `--c3d` of `GPS_L1` (the round anti-pad drawn) and of Properties for an ellipse sheet and for an outline point
  holding an expression. Look at each and list it.

## Risk

Low to medium.
- **Low:** the format. The list-binding machinery already exists for polylines and wires, and the ellipse lowers to a polygon.
- **Medium, and the reason for the audit:** every reader that branches on a sheet's `Rect`/`Outline` must learn the third shape. List
  each in the completion note. They include:
  - validation, lowering and the sheet booleans;
  - the hierarchy's scaling;
  - face edits, images and thermal places;
  - Properties;
  - HZN translate and export.

## Rules

- **Tests:** one per claim, named classes only. No new `Category=Benchmark` test, and no EM solve in a routine test.
- **UI:** every UI piece is rendered with UiSnapshot and looked at.
- **Names:** no commercial tool, PDK or laminate names.
- **Clean room (brief-hzn-0 D1):** the meaning of a Hertzon step comes from its files, its documentation and black-box runs, never its
  source. Note that Hertzon's MCP server opened a window on `open_project` even with `open_in_gui: false`; probe it only when needed.
- **Findings** go in `src/Design/RESOLVED.md` §HZN.
