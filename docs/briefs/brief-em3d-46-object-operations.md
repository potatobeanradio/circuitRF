# Brief 46 — object operations

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d46-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.2 point 3 (*a drag is a preview*), point 5
**Area:** `src/Design/ThreeD/C3dPlacement.cs` (compose, canonicalise), `src/Render/Scene3D/Edit/`
(gizmo geometry and its screen-space hit test), `src/Ui/Viewer3D/Viewer3DOverlay.cs` (the gizmo),
`src/Ui/ThreeD/Operations/`
**Depends on:** 44 · **Blocks:** 47, 48 (the same operations act on instances)

---

## 0. What this brief delivers

In Object mode, on the selection — which may be objects, instances, or both — the context menu and
*3D ▸ Modify* offer:

| Operation | Key | What it does |
|---|---|---|
| **Move** | `G` | base point → target point, both snapped; the selection follows the cursor |
| **Move along X / Y / Z** | `G` then `X`/`Y`/`Z` | Move, constrained to that world axis |
| **Rotate** | `R` | about X, Y or Z through a pivot; 15° steps, or free with Shift, or typed. Quick items: *Rotate 90° about X / Y / Z* and their reverses |
| **Mirror** | — | across the XY, YZ or XZ plane through the pivot |
| **Duplicate** | `Ctrl/Cmd+D` | copies in place, then starts a Move |
| **Array…** | — | copies along up to three axes at a pitch |
| **Align** | — | min, centre or max on each axis, to the **last-selected** object |
| **Order** | — | Bring to Front, Send to Back, Forward, Backward: construction order, which decides overlap |
| **Measure** | `M` | two snapped points: both points' x, y, z, the deltas and the distance, every number selectable and copyable (§6) |

Brief 43's *Rename*, *Material*, *Role*, *Hide*, *Isolate* and *Delete* stay where they are.

There is also a **move gizmo**: three axis arrows and three plane handles at the selection's pivot. It is
drawn in the 2D overlay from the camera alone. Dragging an arrow is a constrained move.

**`G` and `R` are proposals**, following the common 3D modellers, and the owner confirms them. Neither is
taken by briefs 28, 43 or 45.

---

## 1. `R-em3d46-1` — a drag is a preview, and it commits once

**`R-em3d46-1a`** During Move, Rotate or a gizmo drag, **the document is not touched**. The selection's
batches draw with a **per-batch transform** (brief 43 §5), and each mouse move uploads that transform only
(64 bytes per batch). On the commit click or release:
- the document changes once;
- the moved objects re-elaborate (brief 42 §4);
- **one** undo entry is written.

On **Esc** nothing changes, and the transform returns to identity.

This is §8.6's counter gate in its original form: *a drag makes zero kernel calls until release*. For
whole-object operations it holds exactly. Brief 47's face drags are the revised case (overview §1b).

**`R-em3d46-1b` Instances** move, rotate, mirror, duplicate and array by their placement, exactly as
objects do. A child is never re-elaborated by being moved (brief 42's child cache is keyed by the child,
not by where it is placed). Gate 2 counts it.

## 2. `R-em3d46-2` — Move

**`R-em3d46-2a` Base point, then target.** *Move* first asks for a **base point**:
- the snapped point under the cursor at the first click;
- or, if the move started from the gizmo or the key with the cursor over the selection, the snapped point
  under the cursor at that moment.

Then the selection follows the cursor. The target is the snapped point, or the drawing-plane point when
nothing is snapped. The displacement is target − base. This is the CAD way, and it is what makes "put
this pad's corner exactly on that trace's corner" a two-click operation.

**`R-em3d46-2b` Constraints:**
- **X**, **Y** or **Z** during a move locks to that world axis. The same key again unlocks it;
- with an axis locked, the displacement is the projection of (target − base) onto the axis, so a snap
  still decides **how far**;
- **Shift+X/Y/Z** locks to the plane *normal to* that axis;
- the status bar shows the constraint.

**`R-em3d46-2c` Typed:** digits open the inline field (brief 45 §4) for the displacement: `dx, dy, dz`, or
one value along a locked axis. Exact in DBU.

**`R-em3d46-2d` Exactness.** A displacement between two exact points (brief 44 §4) is an integer DBU
vector, added to the placement's integer origin. An inexact displacement is rounded once, at commit, and
the status bar shows `≈`.

## 3. `R-em3d46-3` — Rotate, Mirror and the placement's canonical form

**`R-em3d46-3a` Pivot:** the selection's bounding-box centre by default. **Ctrl/Cmd-click** sets it to a
snapped point first. The pivot is shown as a small cross while the gesture lasts.

**`R-em3d46-3b` Canonical placement.** Rotations accumulate, and a list that grows with every edit is
unreadable. After each commit the placement's rotation part is **canonicalised**:
- if the composed rotation is a **signed permutation** (any composition of 90° turns and mirrors), it is
  stored exactly, as `MirrorX` plus at most three 90°-multiple entries, in the order Z, Y, X;
- otherwise it is stored as Z-Y-X Euler angles in degrees, rounded to 1e-9°.

The origin is adjusted so the pivot stays fixed. The reference page states the canonical form.

**`R-em3d46-3c` Mirror** across a plane through the pivot is `MirrorX` composed with the rotation that
takes X to that plane's normal, then canonicalised.

**The elaborator must reverse face winding when the placement's determinant is −1.** Otherwise every
mirrored polyhedron is inside out. Gmsh orients by its own rules, but the tessellation's normals, the
signed-volume check, and a face's outward direction for brief 47 all read the winding. Brief 42's
elaborator gets this line if brief 42 did not already add it, and gate 4 holds it.

## 4. `R-em3d46-4` — Duplicate, Array, Align, Order

**`R-em3d46-4a` Duplicate** copies the selection with new names and starts a Move whose base point is the
pivot. **Esc cancels the duplicate as well as the move.** Two coincident copies of a solid would overlap
exactly, and the solve would silently use one. The copies are one undo entry together with their move.

**Names:** the source's name with its trailing number incremented past any in use (`pad3` → `pad4`,
`lid` → `lid2`).

**`R-em3d46-4b` Array…** is a small panel: a popover, not a modal dialog, because no gesture is in
progress. It takes counts on X, Y and Z and a pitch per axis in the display unit, with a live preview
through per-batch transforms. Accept writes independent copies.

**More than 100 copies** asks once whether to **group into a cell and array the instance** instead (brief
48's *Group into Cell* plus an instance `Array`), because one instance array is one object to edit and one
child to elaborate. The panel says so in one line. It does not refuse.

**`R-em3d46-4c` Align:** min, centre or max on X, Y and Z, to the last-selected object's bounds. Six items
plus three centre items, in a submenu. Exact in DBU when both objects are exact.

**`R-em3d46-4d` Order.** Construction order decides which solid wins an overlap (brief 41 §2b). It is
invisible unless the user can change it, so the Order items move the selection in the object list, the
tree shows the order, and the Properties panel shows the index. Icons: `ArrangeSendBackward`,
`FlipToFront` (both verified in 3.0.2).

## 5. `R-em3d46-5` — the move gizmo

- Drawn by `Viewer3DOverlay` from the camera and the pivot: three arrows in the axis indicator's colours,
  and three small squares for the planes. Its constant screen size is a few tens of pixels.
- **Hit test in screen space**: distance to the projected arrow segment, or inside the projected square.
  This is computed in `src/Render/Scene3D/Edit/GizmoGeometry.cs` so it is tested headlessly.
- **Hover highlights the handle. Dragging it is a Move** with that constraint, and its base point is the
  point on the axis nearest the cursor at press.
- Shown only in Object mode with a selection and no other gesture in progress. It hides during a drag's
  preview except for the active handle.

A rotate gizmo (rings) is **not** in this brief. *Rotate* with its keys, snapping and typed angles covers
the need, and rings are a follow-on if the owner wants them.

## 6. `R-em3d46-6` — Measure

The layout editor has a Ruler for dimensioning (layout-view §9B). The 3D editor needs to **measure**,
not to dimension (owner, 2026-09-26). There is no dimension drawn in 3D and nothing stored in the
document. What the user needs is the two points, their differences and their distance, as numbers they
can read and copy.

**`R-em3d46-6a` The tool.** `M`, *3D ▸ Measure*, or a toolbar button (`RulerSquare`, verified in 3.0.2).
- **Two clicks**, each **snapped** by brief 44: vertex, midpoint, edge, face centre, grid, including
  inside instances. A click with nothing snapped takes the point on the drawing plane under the cursor,
  as drawing does.
- After the first click, a rubber band follows the cursor and the readout (§6b) updates live. After the
  second, the measurement **stays** until **Esc**, another tool, or a third click, which starts a new
  measurement from that point.
- It is available in the **editor and the read-only viewer** alike (brief 43 §2c: the read-only viewer
  is for measuring and reading). It works in any selection mode and changes no selection.

**`R-em3d46-6b` The readout** is a small card anchored in a corner of the 3D pane:

```
         x            y            z
P1    12.5 mil     0 mil        10 mil
P2    30 mil       4.25 mil     10 mil
Δ     17.5 mil     4.25 mil     0 mil
Distance  18.0087 mil
```

- **Coordinates are the document's world frame**, or the child's frame when pushed into an instance
  (brief 48 §4b). The card names the frame when it is not the top.
- **In the display unit**, following the Unit combobox at once (brief 43 §2b).
- **Δ is P2 − P1, signed.**
- A point that is not exact (brief 44 §4: a feature of a rotated object, or of a child at another DBU)
  shows `≈` on its row, and on Δ and Distance.

**`R-em3d46-6c` Every number can be selected and copied.**
- Each value is a `SelectableTextBlock`, so a drag or double-click selects it and **Cmd/Ctrl+C** copies
  the selection.
- Hovering a value shows a small **copy** button, which copies that one value.
- A **Copy All** button copies the whole card as tab-separated text, a header row then P1, P2, Δ and
  Distance rows, in the display unit with a unit column. It pastes as a table into a spreadsheet.

**`R-em3d46-6d` What a copied number is.** A single value copies as the **unit-bearing spelling
`LayoutUnits.Spell` writes** (`12.5mil`, `-3mm`), **at the lossless precision** `SpellDecimals` derives.
So:
- pasting it into any typed field (brief 45 §4, Properties) reads back **the same DBU**, not a rounded
  neighbour. `LayoutUnits` records the failure this avoids: a mil quantised to 2.54 nm at four decimals
  turned 35 µm into 35.001 µm;
- the text is **invariant-culture**: a decimal point in every country (expressions.md §15A);
- the **distance** is not a whole number of DBU. It is spelled at the same precision, which resolves one
  DBU, and a pasted distance lands on the nearest DBU;
- the card **displays** at that same precision, so what is copied is what is shown.

**`R-em3d46-6e` In the view:** a thin line between the points and a small marker at each, drawn in the 2D
overlay from the camera alone. There are no extension lines, no arrowheads and no text in 3D; the numbers
live on the card. Orbiting keeps the line on its points and uploads nothing.

**`R-em3d46-6f` Nothing is written.** A measurement is not document state, adds no undo entry, and does
not dirty the document. A persisted 3D ruler, like layout's, is a later brief if wanted.

## 7. Gate

`tests/Ui.Tests/ThreeD/Operations*`, `tests/Design.Tests` or the equivalent for `C3dPlacement`.

1. **Preview only.** 500 mouse moves during Move: 0 elaborations, 0 tessellations, and upload bytes of
   ≤ 64 × (selected batches) per move (counters). One commit: elaborations = objects moved.
2. **Instances are not re-elaborated** by Move, Rotate or Array (child cache hits, counter).
3. **Exact moves.** Base → target between two box corners gives an integer origin equal to their
   difference. Four 90° rotations about any axis return to the identity placement, byte-identical in the
   written file.
4. **Mirror.** Mirroring a polyhedron across each of the three planes keeps its elaborated signed volume
   positive and equal to the original (1e-12 relative). Mirroring twice restores the file byte for byte.
5. **Canonical form.** Twenty random 90°-multiple rotations and mirrors canonicalise to ≤ 3 entries plus
   `MirrorX`, and their matrix equals the product.
6. **Duplicate + Esc** leaves the document byte-identical.
7. **Order** changes `Em3dSolid.Order` in the elaboration exactly as the list moved.
8. **Gizmo hit test** at sampled screen points picks the right handle, headless.
9. **Measure.** Two snapped box corners give P1, P2, Δ and Distance equal to the corners, their signed
   difference and its length. Every copied value parses back through `LayoutUnits.TryParse` to the
   **same DBU**, in each of the five display units (mil and inch are the cases a fixed precision gets
   wrong). Copy All is tab-separated with one header row. Changing the display unit re-spells the card
   without moving the points. The document is byte-identical and not dirty afterwards.

## 8. Owner check (pixels not seen)

In the **Debug** build:
- move a pad from one corner to another in two clicks;
- move along Z with `G` `Z` and a typed `5mil`;
- rotate 90° and 30°; mirror; duplicate and cancel;
- array 3 × 3; Align centres;
- use the gizmo arrows and planes;
- measure between two instances; select a coordinate and copy it; use a value's copy button and Copy
  All, and paste into a spreadsheet and into a typed field;
- confirm or change `G`, `R` and `M`.

## 9. Scope

- No rotate gizmo (§5). No scaling: a physical model has no magnification, and a `.c3d` instance has none
  either.
- No persisted rulers.
