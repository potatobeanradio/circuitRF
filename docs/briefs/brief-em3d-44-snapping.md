# Brief 44 — snapping

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d44-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.2; the layout editor's geometry snap
(`brief-snap-distance-and-geometry-snap.md`, `brief-geometry-snap-followups.md`,
`src/Render/Layout/LayoutSnapQuery.cs`) — the 2D precedent whose rules carry over
**Area:** `src/Render/Scene3D/Edit/` (`SnapQuery3D`, feature tables), `src/Ui/Viewer3D/` (the pick patch),
`src/Ui/ThreeD/` (marker, toggles)
**Depends on:** 43 · **Blocks:** 45, 46, 47, 50

---

## 0. What this brief delivers

The cursor snaps to:
- **vertices**;
- **edge midpoints**;
- **the nearest point on an edge**;
- **face centres**;
- **the drawing grid** (brief 45 draws it; this brief snaps to it).

A marker shows what it snapped to, as a different glyph per kind. Snapping reaches into instances: a die's
pad corner is a target in its package. Each kind toggles on the toolbar and in *3D ▸ Snap*.

**The bar is "very fast"**, and it is met by construction, not by optimisation afterwards: a hover
examines only features **near the cursor and visible**, whatever the size of the scene. The gates count
that.

---

## 1. `R-em3d44-1` — the rules the layout editor already learned

These came from real reports on the layout snap, and each applies unchanged in 3D:

- **Geometry snap beats grid snap.** The grid applies only when no feature is in range (R-snpf-3).
- **What is being moved never attracts itself** — on *every* drag, however it started (R-snpf-4). In 3D
  that means the dragged object, the dragged face's own vertices, or the dragged vertex, and in each case
  **at its old position** too.
- **The radius is in screen pixels**, not model units, so it feels the same at every zoom. Use the layout
  editor's snap-distance setting, one setting for both.
- **A marker is always shown when a snap is in force** (the missing-hover-marker report). A snap the user
  cannot see is a snap they cannot trust.

## 2. `R-em3d44-2` — the pick patch: near and visible, from the GPU

**`R-em3d44-2a`** The ID pass (brief 43, 64-bit object and face) reads back an **N × N patch** around the
cursor instead of 1 × 1, with N = 2 × radius + 1 in physical pixels, **with its depth**. That patch
answers two questions at once, for the cost of one small read-back:
- which **(object, face)** pairs are near the cursor;
- which of them are **visible**.

**`R-em3d44-2b` Candidates.** For each distinct face in the patch, take its vertices, edges, edge midpoints
and centre from the object's **feature table** (§3). Project them, and keep those within the radius.

A feature is **visible** if its projected depth is within a small tolerance of the depth the patch holds at
its pixel, or if it lies on a face in the patch (an edge is shared by two faces, one of which may face
away). A feature hidden behind a surface is **not** a candidate. This is the rule every CAD user expects:
you snap to what you see.

**X-ray** is the exception. With the clip plane active, or with objects translucent, the hidden features
of translucent objects are candidates too.

**`R-em3d44-2c` Priority,** nearest first within each tier:
1. vertex;
2. edge midpoint, face centre;
3. nearest point on an edge;
4. grid.

Ties go to the smaller depth. This is layout's order, with face centre added.

**`R-em3d44-2d` Timing of the read-back.** The patch is read on the render thread in the frame that
answers the hover. The snap resolves when the frame arrives, never by blocking the UI thread. The UI
thread shows the last resolved snap until then. At display refresh this is at most one frame behind the
cursor, and brief 27 measured pick latency under the compositor's pacing.

**`R-em3d44-2e` CPU fallback and tests.** `SnapQuery3D` also runs entirely on the CPU:
- software ID patch (brief 28's `IdAtPixel`, extended to a patch);
- features;
- visibility by a ray cast through brief 43's hierarchy of bounds.

The gates run on this path, and the GPU path is asserted to agree with it at sampled pixels.

## 3. `R-em3d44-3` — feature tables, built once

**`R-em3d44-3a`** Per object and per scene generation: unique vertices, edges (as vertex pairs), edge
midpoints and face centres, in the object's **local** frame, in flat arrays. They are built with the
tessellation, off the drawing path. A face's features are found by face index in O(1).

**`R-em3d44-3b` Instances share their child's table.** A child (cell, view) is tabled once. A candidate
inside an instance, or inside an array element, is transformed by that element's placement **at query
time**. The patch names the instance, so only the elements near the cursor are ever transformed. An
array of 1,000 dies costs what one die costs.

**`R-em3d44-3c` Grid.** The grid point nearest the cursor's ray on the drawing plane (brief 45 owns the
plane). It is computed, not stored.

## 4. `R-em3d44-4` — what a snap produces

A snap result is:
- the **world point**, in DBU of the document where exact (a vertex of an unrotated object) and in
  metres otherwise;
- the **kind**;
- the **source** (instance path, object, face or edge);
- the **screen point**.

**Exactness matters here.** A box drawn to a snapped vertex must land on that vertex **exactly**, or two
solids that were meant to touch will not (overview §1d). So:
- a feature of an object whose placement is exact (translation plus 90° multiples) yields an **integer
  DBU** point;
- a feature from a child with a different `DbuPerMicron`, or from an object with a non-exact rotation,
  yields metres, rounded to the document's DBU only at the moment it is used. The status bar then shows
  `≈` beside the coordinate, so the user can see that exact coincidence was not available.

## 5. `R-em3d44-5` — the marker and the toggles

**Markers,** in the 2D overlay (`Viewer3DOverlay`, which draws from the camera alone):

| Kind | Glyph |
|---|---|
| vertex | square |
| midpoint | triangle |
| edge | an × on the edge |
| face centre | circle |
| grid | small + |

The status bar names the kind and the coordinate in the display unit, with `≈` when inexact (§4).

**Toggles:** one per kind, on the toolbar (Material `Magnet` for the master switch; verify kind names) and
in *3D ▸ Snap*. They are saved per user, not in the document.

**Holding Alt/Option suspends geometry snap** for as long as it is held, and the grid still applies. This
is the layout editor's convention if it has one; check `LayoutEditorViewModel.Snap.cs` and match it. The
held-key latch is cleared on **LostFocus** (the latched-key lesson: a key-up that goes to another window
leaves the modifier stuck, and snapping silently stops).

## 6. Gate

`tests/Ui.Tests/ThreeD/Snap*` (CPU path), `tests/Firewall.Tests`.

1. **Near, not all.** In a scene of 10,000 boxes, a hover over one box examines only the features of
   faces in the patch. Assert that the features examined are ≤ a bound set by the patch size and
   **independent of the scene's object count**: the same number at 100 and at 10,000 boxes (counter).
2. **Instances are free.** A 30 × 30 array of a 200-feature child: a hover transforms only the elements
   present in the patch (counter), and the child's table is built once.
3. **Visible only.** A vertex directly behind a face is not a candidate. With X-ray on, it is.
4. **Priority.** A vertex and a midpoint both within the radius: the vertex wins, even when it is slightly
   farther away on screen.
5. **Self-exclusion.** During a face drag, that face's vertices, at old and new positions, never snap.
6. **Exactness.** Snapping to the corner of an unrotated box returns an integer DBU point equal to the
   corner. From a 30°-rotated box it returns an inexact point, flagged.
7. **Zero allocations per hover** in steady state on the CPU path (`GC.GetAllocatedBytesForCurrentThread`
   around 1,000 queries). The pane's per-frame `Task` allocation is Avalonia's (R-em3d28-1d) and is
   excluded by name.
8. **GPU and CPU agree** on the patch's (object, face) set at sampled pixels, in the Metal offscreen
   harness (`CRF_VIEWER3D_PNG`'s path). Where Metal is not available, this is the owner's run.

## 7. Owner check (pixels not seen)

In the **Debug** build, on the Package example with its wires and on a 30 × 30 array:
- sweep the cursor fast across the model and watch the marker keep up;
- snap to a pad corner inside an instance;
- hold Option and see geometry snap stop and the grid remain;
- switch to another application while holding Option, come back, and check that snapping works (the
  latched-key check).

"Does it feel as fast as the layout editor" is the question.

## 8. Scope

- **No intersection snap** (edge × edge, edge × plane). It is useful, and it is a later addition to the
  same query.
- No snapping of angles. Rotation increments are brief 46's.
