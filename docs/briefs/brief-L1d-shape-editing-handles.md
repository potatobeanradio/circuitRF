# Sonnet Brief — Phase L1d: vertex, edge, bulge and control-point editing

**Design:** `docs/design/layout-view.md` §6.3 R14 (hit priority and the edit gestures), §3.2 R9a (the shared
edge-list vocabulary), §3.3 R10 (angle mode), §1.5 R5 (snap governs future edits only). **Consumes L1c** —
`LayoutFlattener`, `LayoutHitTest`, selection, move, delete and the properties panel all exist.

**Scope is L1d ONLY: reshape existing geometry.** Clipper2 booleans/offsets, Flatten-to-Polygon and the
clipboard are **L1e**.

This is also the phase that makes the **`Curve` primitive reachable at all** — L1b deliberately shipped no
Curve tool, on the grounds that the bulge-drag interaction built here is the same one a drawing tool would
need, and building it twice would let the two drift.

---

## Read first: the two traps this phase inherits

**1. Handles are measured in pixels.** A handle's size and its grab radius are screen quantities converted
to DBU through the live zoom — the exact shape of the L1b default-viewport bug and of L1c's hit tolerance.
Compute per query from the current viewport; never cache, never derive from `SnapDbu`. **At least one test
per gesture must start from screen-pixel coordinates** through the canvas's conversion, at a realistic
viewport, on both starter technologies.

**2. Vertex-drag snapping is the opposite of move-drag snapping, and that is correct.** L1c's R-L1c-3 snaps
the translation *delta* because a move is a rigid-body translation and rounding each vertex would destroy
imported off-grid geometry. A vertex drag is the user **placing a point**, so it snaps the *resulting
position* — the other vertices are untouched, so nothing is silently mangled. An **edge** drag is a rigid
translation of two vertices, so it snaps the perpendicular *offset*, which keeps a 45° edge at 45°. Write
all three rules next to each other in the code so they read as a considered system rather than an
inconsistency.

---

## Code changes

### 1. One command for every geometry edit

**R-L1d-1. Geometry edits go through a single `ReplaceShapeCommand(index, before, after)`.**

Not a family of per-operation commands. The reason is the promotion rule (§4): converting a `PolygonShape`'s
edge to an arc **changes the runtime type** to `CurveShape`. A command that mutates a shape in place cannot
express that; one that swaps the instance at a fixed index expresses every edit uniformly — vertex move,
edge move, insert, remove, conversion, radius change — and undo is trivially the reverse swap at the same
index (L1b's restore-at-original-index rule, which this satisfies by construction).

Geometry edits are therefore **immutable-style**: build the new shape, swap it in. Do not mutate the shape
the renderer may be reading.

One drag gesture is **one** command, pushed on release — not one per pointer-move. During the drag, render a
preview through the existing `dragOverrides` mechanism `DrawLayer` already supports for L1c's move.

### 2. Handles

Drawn above every layer whenever the selection is **exactly one** shape (multi-selection shows no handles —
it is a move/delete selection).

| Handle | Appears on | Look |
|---|---|---|
| Vertex | `Polygon`, `Curve`, `Path` vertices; `Rect`/`RoundedRect` corners | filled square |
| Edge midpoint | `Polygon`, `Curve`, `Path` straight edges | hollow circle |
| Bulge | `Arc` edges, at the arc midpoint | hollow diamond |
| Cubic control point | `Cubic` edges, 2 per edge | small filled circle, with a thin tangent line to its anchor |
| Radius | `Circle` | single square on the +X axis |
| Corner radius | `RoundedRect` | single square inset on the top-left corner |

**R-L1d-2. Hit priority, strictly: cubic control point > bulge handle > vertex > edge midpoint > edge line >
shape interior.** §6.3 R14 with the curve handles slotted in. Getting this order wrong makes vertex dragging
feel broken, because the interior body-move grabs first.

Handles hit-test **before** L1c's shape hit stack, and a press on a handle must not disturb the selection or
the overlap-cycling cache.

### 3. Gestures

- **Drag vertex** → move it; snapped (resulting position), angle-mode constrained against both neighbours.
- **Drag edge midpoint / edge line** → translate that edge **perpendicular to itself**, moving both endpoints
  and preserving the adjacent edges' directions. This is what makes "widen this trace" or "nudge this wall"
  a one-drag gesture. Snap the perpendicular offset.
- **Ctrl/Cmd+click an edge** → insert a vertex at the clicked position (snapped). On a curved edge, split the
  arc/cubic at that parameter into two edges of the same kind, preserving the shape.
- **Delete on a selected vertex** → remove it; **blocked below 3** vertices for a closed shape, **below 2**
  for a `Path`.
- **Drag bulge handle** → set the `Arc` edge's bulge; dragging past the chord flips the sweep. This is the
  fastest way to radius a corner, and it is the gesture L1b deferred to here.
- **Drag cubic control point** → move it; the tangent line follows.
- **Drag `Circle` radius / `RoundedRect` corner-radius** → resize, clamped (corner radius ≤ half the shorter
  side).
- **Drag a `Rect` corner** → resize; normalize on release so `X1<X2`, `Y1<Y2`.
- Alt suspends snapping for the gesture. Escape mid-drag cancels and restores the original shape. Live
  dimension readout throughout (§1 R6): segment length, arc radius, or the perpendicular offset.

### 4. Edge conversion and the promotion rule

Right-click an edge → **Convert to Line / Arc / Cubic**.

- **Line → Arc**: bulge 0 (a straight arc), immediately draggable via its new bulge handle.
- **Line → Cubic**: control points at the 1/3 and 2/3 points, so the initial shape is unchanged.
- **Arc/Cubic → Line**: drop the curvature.

**R-L1d-3. The promotion rule.** `PolygonShape` carries no edge list. Converting one of its edges therefore
**replaces it with an equivalent `CurveShape`** — same layer, same net, same vertices, same list index, with
an edge list whose entries are all `Line` except the converted one. The reverse demotion is optional; if all
edges become `Line` again, leaving it a `CurveShape` is acceptable and simpler. `PathShape` already carries
an edge list and gains the curved edge **in place**, with no type change. The rule was written into
`LayoutModel.cs`'s header during L1b — implement exactly that, and update the header if reality differs.

### 5. Self-intersection

Allow it freely during a drag. On release, if the resulting shape self-intersects, **flag it** — a
non-blocking Messages note naming the shape — and keep the edit. Do not reject it and do not repair it;
auto-repair via a Clipper2 union is **L1e**. Detection can reuse `LayoutFlattener` output plus a simple
segment-intersection sweep.

---

## Scope guardrails (do NOT do in L1d)

- No Clipper2, no booleans, no offsets, no Flatten-to-Polygon, no self-intersection repair, no clipboard (L1e).
- No rotate / mirror / align / distribute / array.
- No object snapping to other shapes' vertices or edges — grid snap only.
- No new drawing tools; the `Curve` primitive arrives via §4's conversion, not via a tool.
- No spatial index, no caching, no LOD, no R8b merge tier (L2). No instances (L3).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, `SchematicRenderer`, or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Handles appear** for a single-shape selection of each type, in the right places, and **disappear** for a
   multi-selection.
3. **Hit priority (R-L1d-2)** — with a control point, a bulge handle, a vertex and an edge deliberately
   overlapping within a few pixels, each is grabbed in the specified order. This is the test that catches an
   interior-first implementation.
4. **Screen-pixel coverage** (see "Read first") — on both starter technologies at a realistic viewport, grab
   and drag a vertex using **screen** coordinates through the canvas's conversion; assert the vertex lands
   where expected. Repeat at a very low and a very high zoom and assert the grab radius stayed ~constant in
   pixels while varying by orders of magnitude in DBU.
5. **Vertex drag snaps position; move drag snaps delta** — the same off-grid fixture used in L1c's gate:
   dragging one vertex snaps *that* vertex and leaves the others exactly where they were; a whole-shape move
   still moves every vertex by an identical snapped delta.
6. **Edge drag preserves direction** — dragging a 45° edge leaves it at exactly 45°, and both endpoints moved
   by the same perpendicular offset.
7. **Insert / remove vertex** — insert lands at the snapped click point; removal is blocked at 3 vertices
   (closed) and 2 (`Path`); inserting on an arc edge yields two arc edges and a visually unchanged shape
   (assert flattened outlines match within tolerance).
8. **Bulge** — dragging the bulge handle produces the expected radius; dragging past the chord flips the
   sweep sign; a bulge of 0 renders identically to a straight edge.
9. **Promotion (R-L1d-3)** — converting a `PolygonShape` edge to an arc yields a `CurveShape` at the **same
   index**, with layer, net and vertices preserved; undo restores the original `PolygonShape` instance at
   that index; a `PathShape` gains the arc **without** a type change.
10. **One gesture, one undo entry** — a vertex drag through 50 pointer-move events undoes in a single
    Ctrl+Z, and redo reproduces the shape exactly (`LayoutPersistence.Serialize` equality).
11. **Escape mid-drag** restores the original shape and pushes no command.
12. **Self-intersection is flagged, not blocked** — dragging a vertex across the shape produces a Messages
    note and keeps the edit.

## On completion

1. Add a "Phase L1d — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **`ReplaceShapeCommand`
   as the single geometry-edit command and why the promotion rule forces it**, the **three different snapping
   rules** (move = delta, vertex = position, edge = perpendicular offset) and the reasoning that makes them
   consistent, the **hit-priority order**, that **handle radii are pixel quantities computed per query**, and
   the test file names.
2. Report back before L1e (Clipper2 booleans and offsets, Flatten-to-Polygon, self-intersection repair, and
   cross-cell cut/copy/paste with DBU rescale) is briefed.
