# Sonnet Brief — Phase L1c: flattener, hit-testing, selection, move, delete, properties

**Design:** `docs/design/layout-view.md` §6.2 R13 (selection with overlap cycling), §6.3 (hit priority and
body drag), §3.2 (flattening, bbox and hit-testing), §3.4 R10a (the net attribute), §1.5 R5 (snap governs
future edits only). **Consumes L1a** (canvas/renderer) and **L1b** (undo stack, `AddShapeCommand`, current
layer, snap and angle mode).

**Scope is L1c ONLY: select whole shapes, move them, delete them, and edit their fields.** Vertex, edge,
bulge and control-point handles are **L1d**; Clipper2 booleans, Flatten-to-Polygon and the clipboard are
**L1e**.

## Goal

Click a shape and it selects — clicking again cycles through whatever is stacked under the pointer. Drag it
and it moves. Delete removes it. A properties panel edits its layer, its net and its type-specific fields.
All of it undoable.

## Why the flattener lands here

§6.1 calls for **one** `ToClipperPaths(shape, tolerance)` helper that booleans, offsets, DRC, the mesher,
hit-test and export all share. Hit-testing a `Curve`, `Circle` or `RoundedRect` is the first consumer, so the
flattener is built in this phase rather than in L1e with the boolean ops. L1e then only wraps it for
Clipper2. (L1a needed no flattener because Skia tessellates curves natively for *display* — that remains
true and unchanged.)

## Code changes

### 1. `src/Ui/Layout/LayoutFlattener.cs` — framework-free

```csharp
public static IReadOnlyList<long[]> Flatten(LayoutShape shape, long tolDbu);
```

Returns one or more closed rings in flat `[x0,y0,x1,y1,…]` form.

- **Arc edges** — sagitta-bounded: for radius `r` and tolerance `s`, the maximum segment sweep is
  `2·acos(1 − s/r)`, clamped to something sane for very large `r`; segment count is `ceil(sweep / that)`.
  Reuse L0a's `LayoutArc` for the bulge → centre/radius/sweep conversion; do not re-derive it.
- **Cubic edges** — recursive subdivision until the control polygon is within `tolDbu` of its chord.
- **`Circle`** — a full 360° arc. **`RoundedRect`** — four lines and four quarter arcs.
- **`Rect` / `Polygon`** — returned as-is; no work, no allocation churn.
- **`Path` is NOT flattened to an outline here.** Turning a centerline plus width into a closed outline is an
  *offset* operation and belongs to Clipper2 in L1e. Hit-testing a path uses distance-to-centerline (§2), so
  nothing in this phase needs the outline.

**R-L1c-1. Flattening is deterministic.** The same `(shape, tolerance)` must produce a byte-identical vertex
list every time, on every machine. L1e's booleans, L5b's DRC and L4's export all have to agree about where a
curve's vertices are; a flattener that varies by floating-point path or iteration order produces geometry
that changes when nothing changed. Pin this with tests.

Tolerance resolution: the shape's `FlattenTolDbu` if set, otherwise the technology's `DefaultFlattenTolDbu`,
otherwise a documented constant. **One** resolver function, called by everything.

### 2. `src/Ui/Layout/LayoutHitTest.cs` — framework-free

```csharp
public static IReadOnlyList<int> HitStack(LayoutView view, Technology? tech,
                                          long x, long y, long tolDbu);
```

Returns **shape indices**, ordered per §6.2:

1. **`ZOrder` descending** (topmost layer first),
2. then **ascending area** — so a small shape sitting on a large one is reachable, which is the case that
   actually matters,
3. then ascending list index as a **deterministic** tie-break, so cycling is stable across clicks.

Per-shape tests, all against flattened geometry where curves are involved:
- Filled shapes: point-in-polygon (ray cast) **or** within `tolDbu` of an edge, so a click just outside a
  small shape still lands.
- `Path`: distance from the point to the centerline polyline ≤ `Width/2 + tolDbu`.
- `Label`: its bounding box.
- Skip layers whose `LayerDef` is `Visible == false` or `Selectable == false`. Unknown layers resolve
  through `FallbackPalette` and are selectable.

Callers convert a ~4 px tolerance into DBU using the current zoom.

### 3. Selection

VM state: an ordered `IReadOnlyList<int>` (or a set plus stable order) of selected shape indices.

- **Click** — replace the selection with the top hit; **Shift+click** adds; **Ctrl/Cmd+click** toggles.
- **Click on empty space** clears.
- **Marquee**, dragging on empty canvas with the `Select` tool, using the CAD convention users expect:
  **left-to-right = enclose** (a shape's bbox must be fully inside), **right-to-left = crossing** (bbox
  intersects). Draw the marquee with the same filled-plus-opaque-edge look §2.3 gives geometry — it is the
  rendering the layer contract was modelled on.
- **Select All / Deselect All** on the Edit menu.
- **Rendering**: selected shapes get an accent outline drawn above every layer. Do **not** change their fill —
  the layer color is the information the user is reading.

### 4. Overlap cycling (§6.2 R13)

**R-L1c-2. Repeated clicks at the same point advance through the hit stack.**

Cache `(clickPoint, orderedStack, index)`. On the next press: if it is within a few pixels of the cached
point **and** nothing has changed in between, advance `index` modulo the stack length; otherwise rebuild.
Invalidate the cache on pointer movement beyond the threshold, on any model mutation, on undo/redo, and on a
selection change originating anywhere else.

**Alt+click** is an equivalent explicit "next candidate".

**The status readout is not optional.** Show `Rect · M2 · 2 of 5` in the metadata bar whenever the selection
came from a stack of more than one. Without it, cycling reads as a glitch rather than a feature — this is
called out in the design for that reason.

### 5. Move — snap the delta, not the coordinates

`MoveShapesCommand` (mirroring `MoveSymbolPrimitivesCommand`): drag from inside any selected shape translates
the whole selection.

**R-L1c-3. Snap the translation delta, never the resulting vertices.** Rounding each moved vertex onto the
snap grid would silently re-snap — and therefore destroy — imported off-grid geometry, 45° diagonals and
flattened arcs, all of which legitimately sit between grid points (§1.5 R5). Snap `Δx`/`Δy` to `SnapDbu` and
add; every shape then keeps its internal relationships exactly. Alt suspends snapping, as in L1b.

Also: arrow keys nudge the selection by one snap step (Shift for ten). Live delta readout in the display unit
throughout the drag.

### 6. Delete

`DeleteShapesCommand`, restoring **at original indices** on undo — L1b's rule, and it matters more here
because deleting a multi-selection and undoing must restore z-order exactly. Delete and Backspace both bind.

### 7. Properties panel

Mirror the existing Properties view (`src/Ui/Views/Properties/`) rather than inventing a panel.

- **Common to every shape**: layer (a combo like L1b's, showing swatch + name) and **net** (free text, §3.4 —
  the field exists and this is where it becomes editable).
- **Type-specific**: `Path` width and end style; `RoundedRect` corner radius; `Circle` radius; `Label` text,
  height and rotation; `FlattenTolDbu` on curved shapes (blank = inherit).
- Dimension fields parse through `LayoutUnits.TryParse` and display through `Format`, per §1 R6.
- **Multi-selection**: show fields common to the selection, blank where values differ, and apply an edit to
  every selected shape as **one** undo entry (`CompositeCommand` already exists for this).
- All edits go through a `SetShapeFieldCommand` mirroring `SetSymbolPrimitiveFieldCommand`. Commit on
  focus-loss or Enter — one undo entry per commit, not per keystroke.

## Scope guardrails (do NOT do in L1c)

- **No vertex, edge, bulge or control-point handles; no insert/remove vertex; no edge conversion; no
  polygon→curve promotion** (L1d). Selection in this phase is whole-shape only.
- **No Clipper2, no booleans, no offsets, no Flatten-to-Polygon command, no path-outline generation, no
  self-intersection repair** (L1e). If a move produces a self-intersection, that is fine and nothing needs
  to notice yet.
- **No clipboard** — no cut, copy or paste (L1e).
- No rotate/mirror/align/distribute (later in L1 or L3). No object snapping to vertices or edges — grid only.
- No spatial index: hit-testing iterates shapes linearly, which is correct for this phase. **L2** adds the
  R-tree, and `HitStack`'s signature should not presuppose one.
- No instances (L3), no layer panel, no visibility/lock UI.
- Don't touch `src/Core`, `src/Engine`, `RfCore`, `SchematicRenderer`, or the symbol editor.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; no existing test regresses.
2. **Flattener** — a circle of radius `r` at tolerance `s` produces vertices all within `s` of the true
   circle; halving the tolerance strictly increases the vertex count; a `Rect` and a straight-edged `Polygon`
   pass through unchanged. **Determinism**: flattening the same shape a hundred times, and after a
   serialize/deserialize round-trip, yields byte-identical vertex arrays (R-L1c-1).
3. **Hit ordering** — a small rectangle on top of a large one: the small one is **first** in the stack even
   though both are on the same layer. Two shapes on different layers order by `ZOrder` descending. Ties break
   by list index, reproducibly.
4. **Hit accuracy** — points inside, outside, and within tolerance of an edge behave correctly for every
   primitive, including an arc-bearing `Curve` and a `RoundedRect`; a point on a `Path`'s centerline hits and
   one at `Width/2 + 2·tol` away does not.
5. **Non-selectable and hidden layers are never hit.**
6. **Cycling (R-L1c-2)** — five stacked shapes: five clicks at one point visit all five in order and the
   sixth wraps to the first. Moving the pointer beyond the threshold and clicking rebuilds from the top.
   A model change mid-cycle invalidates the cache. The status readout reports `n of m` correctly.
7. **Marquee** — left-to-right selects only fully-enclosed shapes; right-to-left also selects intersecting
   ones; Shift adds; Ctrl toggles.
8. **Move preserves off-grid geometry (R-L1c-3)** — take a shape whose vertices are deliberately off-grid
   (e.g. from a 45° segment), move it by a dragged delta, and assert **every vertex moved by exactly the same
   snapped delta** and no vertex was individually rounded. This is the test that catches the tempting wrong
   implementation.
9. **Nudge** — arrow keys move by exactly one snap step; Shift by ten.
10. **Delete + undo restores indices** — delete a multi-selection spanning several z-positions, undo, and
    assert `LayoutPersistence.Serialize` equality with the pre-delete state.
11. **Properties** — editing layer and net on a multi-selection is one undo entry and applies to all;
    differing values show blank and leave untouched shapes alone; a dimension field accepts `2.9mm` and
    reverts cleanly on garbage.
12. **Selection survives undo/redo sensibly** — it never references a stale index after an undo (assert no
    out-of-range access after undoing a delete).

## On completion

1. Add a "Phase L1c — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`. Call out explicitly: **why the
   flattener lives here** and that it is the single shared one §6.1 requires, the **determinism guarantee**,
   the hit-stack **ordering rule**, the **cycling cache invalidation conditions**, and above all
   **R-L1c-3 — move snaps the delta, not the vertices** — with the reason, because the wrong version looks
   correct in every test that only uses on-grid fixtures. Plus the test file names.
2. Report back before L1d (vertex / edge / bulge / control-point handles, insert and remove vertex, edge
   conversion, and the polygon→curve promotion rule) is briefed.
