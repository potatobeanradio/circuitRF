# Brief 134 — Expressions in a polyline's points: the editor

**Series:** [131 overview](brief-em3d-131-point-expressions-overview.md) · **Tag:** `R-em3d134-n`
**Status:** Written 2026-10-09, not started.
**Area:** `src/Ui/ThreeD/` (the Inspector: `C3dPropertiesViewModel` / `C3dPropertiesView.axaml`, the polyline's
summary row), `src/Design/ThreeD/` (the 2D↔3D forms). Findings in `src/Ui/ThreeD/RESOLVED.md`.
**Depends on:** 132. Independent of 133.

---

## 0. What this brief delivers

A polyline's vertices can be read, typed (as numbers or expressions), added and removed in the Inspector, as a wire's
can after 133.

**How a polyline differs from a wire, and why this is its own brief:**
- **The Inspector shows only a summary today** ("*n* points, closed"). There are no point rows to turn into
  expression fields, so this brief builds them.
- **Vertex editing is refused** (`C3dFaceEdit`: "A polyline is construction geometry: edit it by drawing it again").
  The Inspector rows become the way to edit one. This brief does **not** lift the refusal for vertex-mode Move;
  doing that is a separate decision.
- **Two forms.** `Points` holds 2D points in the polyline's plane, at its `Offset`. `Points3` holds 3D points once a
  vertex leaves the plane, and when present it is the polyline.
- **It has a placement.** Move, Rotate and Mirror change `Placement`, not the points, and keep brief 51's own rule
  (overview §4). D2 and D4 do not apply to a polyline's points through those commands.
- **No bonded ends.** Any vertex may be removed while enough remain.

## 1. Requirements

**R-em3d134-1 — the point rows.** In place of the summary, one row per vertex:
- the planar form shows **u, v** in the plane's own axis names (as `FieldLabel` names a sheet's on the XZ plane);
- the 3D form shows **x, y, z**.

Each component is a `C3dDimensionField` (number or expression). Rows are labelled `1`…`n`. "Closed" is shown as
today.

**R-em3d134-2 — add and remove.** Each row has the same three buttons as a wire's, with the polyline's rules:
- **+▲ / +▼** insert at the Catmull–Rom midpoint (deb6199f's `CubicMidpoint`). On a **closed** polyline the first row's
  +▲ and the last row's +▼ insert on the closing segment, and the cubic's neighbours wrap around. On an open one, the
  first row has no +▲ and the last no +▼, as a wire's.
- **−** on any row, while at least 2 vertices remain (open) or 3 (closed). The last allowed removal is refused with a
  sentence.
- In the planar form, the midpoint is computed in (u, v) and stays in the plane.
- One undo entry each. Expressions are renumbered through 132's function, for `Points` or `Points3` as the form has it.

**R-em3d134-3 — leaving the plane.** A typed value off the plane is either a 3D-form row given a z that differs, or a
planar row given a coordinate along the normal (if the planar row offers one; decide and record). Today, drawing such
a vertex converts the polyline to `Points3` (`PolygonTool`). Converting carries every bound component: each in-plane
(u, v) expression becomes the matching x/y/z component's expression. The normal component is the polyline's `Offset`,
taking its expression if it has one. If a component cannot be carried exactly, the conversion is refused, naming it.
No silent evaluation.

**R-em3d134-4 — what reads a polyline.** List every consumer of a polyline's points (grep `C3dPolyline`, `Points3Of`):
snapping, the drawing plane, any operation that takes a polyline as a path or profile. Confirm that each reads
elaborated numbers after 132. A consumer that *copies* points into another object copies numbers. Say so in that
command's result and in RESOLVED.md, as a known limit rather than a hidden one.

## 2. Gates

1. A planar polyline and a 3D one: the Inspector rows show u/v or x/y/z, and typing a number or an expression commits
   one undo entry. An expression shows bound.
2. Add on an open polyline (middle and ends) and on a closed one (the closing segment, with wrapped neighbours). The
   new point is the cubic midpoint. Expressions renumber, and undo restores.
3. Remove down to the minimum. The next removal is refused with its sentence.
4. Leaving the plane carries `u`/`v` expressions and a bound `Offset` into `Points3`, or refuses by name.
5. A vertex bound to `x_v` follows `x_v` in the elaborated scene.

Targeted classes only (the polyline and Inspector test classes, the brief-51 classes).
