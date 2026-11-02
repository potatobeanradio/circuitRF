# Brief 47 — face and vertex editing: the managed kernel

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d47-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.4 (naming), §8.2 point 3; overview §1b (no OCCT
here), §1e (face names), §1g (a primitive stays a primitive)
**Area:** `src/Design/ThreeD/Kernel/` (new: headless, no UI), `src/Design/ThreeD/C3dElaborator.cs`
(recognition in the lowering), `src/Ui/ThreeD/Operations/` (the face and vertex gestures)
**Depends on:** 46 (Move's gesture, constraints and typed values are reused) · **Blocks:** 49 (ports and
boundaries on faces), 51
**Owner decision D2:** the kernel is managed; no native dependency.

---

## 0. What this brief delivers

In **Face** mode, the context menu and *3D ▸ Modify* offer:

| Operation | Key (proposal) | What it does |
|---|---|---|
| **Move Along Normal** | `N` | **push/pull**: the face's plane moves along its normal, and the neighbouring faces **keep their planes** and stretch to meet it |
| **Move** | `G` | the face's vertices translate by base → target, and the neighbours **tilt** to follow |
| **Extrude to New Solid** | `E` | a new solid grows from the face, along its normal |
| **Align to Face…** | — | moves the owning object so this face becomes coplanar with a picked parallel face |
| **Copy as Sheet** | — | a new sheet object exactly on the face |
| **Plane from Face** | — | brief 45's drawing-plane command |
| **Measure** | — | area, perimeter, normal, and the distance to a picked parallel face |
| **Select Owning Object** | — | brief 43 |
| **Make Port…**, **Boundary ▸** | — | brief 49 fills these |

In **Vertex** mode: **Move** (`G`), **Set Coordinates** (Properties, typed) and **Measure From**.

**Every edit keeps the solid closed.** No command removes a face. *Delete* is not offered in Face mode,
because a closed solid has no face it can lose.

---

## 1. `R-em3d47-1` — the kernel

**`R-em3d47-1a`** `src/Design/ThreeD/Kernel/` holds a **boundary representation of planar polygonal
faces**: vertices, faces as vertex loops (outer ring plus holes), edges derived with their two faces, and
face names. It works in the object's **local** frame, in **integer DBU**. The placement (brief 41) maps it
to the world, and an edit's world vector is taken into the local frame by the inverse placement.

It is ordinary C#: no native code, no UI, headless-tested, with deterministic iteration (no hash order;
named constants — `Em3dTessellation`'s rules).

**`R-em3d47-1b` From a primitive and back.** Every primitive converts to a B-rep with its fixed face names
(brief 41 §2c). An edit first asks **whether the primitive can state the result as a change of its own
fields** (§2). If it can, only those fields change and the kind stays. If it cannot, the object becomes a
`Polyhedron` **carrying the same face names**, so nothing attached to a face is lost (overview §1e), and
the status bar says *"`lid` is now a polyhedron (undo to keep it a box)"*.

**`R-em3d47-1c` The lowering recognises what it can** (brief 42 §1d, extended). A polyhedron that is
exactly an axis-aligned box, or exactly a prism along z, lowers as `Em3dBox` or `Em3dExtrudedPolygon`.
The FDTD path then stays on primitives even after the user has edited through a polyhedron and back. The
**document** keeps what the user made. Recognition lives only in the lowering.

## 2. `R-em3d47-2` — what each primitive can state

| Kind | Move Along Normal | Move (free) | Vertex move |
|---|---|---|---|
| **Box** | `Size` on that axis; for a `min` face, `Min` too — stays a box | → polyhedron | → polyhedron |
| **Prism** — `top`/`bottom` | `Height`, or `Offset` and `Height` together — stays a prism | `top`: `Shear` changes (and `Height` for a normal component) → an **oblique** prism, still a prism; `bottom`: `Offset`, `Height` and `Shear` together | → polyhedron |
| **Prism** — `side<k>` | outline edge k offset in the plane, its neighbours keeping their lines — stays a prism | → polyhedron | → polyhedron |
| **Cylinder** — `top`/`bottom` | axis length — stays a cylinder | **refused**: *"a cylinder's end moves only along its axis. Convert to Polyhedron to move it freely"* | — (§5) |
| **Cylinder** — `side` | radius — stays a cylinder | refused, as above | — |
| **Sheet** | `Offset` — the sheet moves as a whole | in its plane: outline translate; out of its plane: refused | in its plane only |
| **Polyhedron** | §3 | §3 | §3 |

**Convert to Polyhedron** is an explicit Object-mode command, with a faceting count for a cylinder
(default: the tessellation's own count). A cylinder is never faceted silently.

## 3. `R-em3d47-3` — the general operations, on a polyhedron

**`R-em3d47-3a` Move Along Normal (push/pull).** The face's plane moves by d along its outward normal.
Every vertex of the face is recomputed as the **intersection of the moved plane with the planes of the
other faces that meet at it**:
- at a vertex where exactly three faces meet, that is one well-defined point;
- at a vertex where more than three meet (a pyramid's apex, say), the neighbours' planes may not share a
  point. That vertex is **translated** by d·n instead, and its neighbours fold (§3c).

The preview **clamps** at the largest d before a neighbouring edge would reach zero length, and the status
bar names the neighbour: *"side2 would vanish at 1.2 mil"*. Integer arithmetic is used where the normals
are axis-aligned. Elsewhere the intersection is solved in doubles and rounded to DBU once.

**`R-em3d47-3b` Move (free).** Every vertex of the face translates by the same vector (base → target,
brief 46's gesture, constraints and typed values).

**`R-em3d47-3c` Folding keeps faces planar.** After either operation, any face whose vertices are no
longer coplanar within **1 DBU** is split into planar triangles, by ear clipping in its best-fit plane. The
pieces are named `<face>.<i>`, and everything attached to the face (brief 49) attaches to every piece.
This is how the neighbours "adjust to keep the object closed": no gap opens, because every face still
uses the same vertices.

A face that merely **tilts** stays one face. Only a face that is no longer flat folds.

**`R-em3d47-3d` Self-intersection is refused.** After the operation, the kernel checks:
1. every face has positive area;
2. no two faces that share no vertex intersect — a bounding-volume hierarchy of face bounds, then
   triangle–triangle tests on the candidate pairs only;
3. the signed volume is positive.

A preview that fails draws **red**, and a commit that fails is refused with the faces named: *"`top`
would pass through `bottom`"*. **The document is unchanged.**

**`R-em3d47-3e` Vertex Move** is the free move of one vertex, with §3c's folding and §3d's check. **Set
Coordinates** in the Properties panel is the same edit, typed, one undo entry. In world coordinates it is
shown in the display unit and taken into the local frame by the inverse placement.

## 4. `R-em3d47-4` — the other face commands

- **Extrude to New Solid.** A new object from the face's polygon, extruded along the outward normal by a
  dragged or typed distance. An axis-aligned face gives a `Prism`; a tilted face gives a `Polyhedron`. Its
  material is the current material, and a toggle in the gesture takes the source's instead. The source is
  unchanged. Use case: grow a bump from a pad, or a wall from a floor.
- **Align to Face…** takes a face on the selected object, then a picked target face. The object moves
  along the target's normal until the two faces are coplanar, **facing each other** (touching) or the
  **same way** (flush), chosen by a toggle in the pick step and shown before the click. If the faces are
  not parallel, it is refused, pointing to *Rotate*. Exact in DBU when both are exact.
- **Copy as Sheet.** An axis-aligned face gives a new `Sheet` on that plane with the face's outline and
  holes. A tilted face is refused, because sheets lie on XY, YZ or XZ in this version (brief 41).
- **Measure** fills the Properties panel: area and perimeter (display unit), outward normal, and the
  distance to a second picked face when parallel.

## 5. `R-em3d47-5` — what Vertex mode shows on a curved primitive

A cylinder has no vertices to edit, and its tessellation's vertices are not design. Vertex mode shows its
two **cap centres**, which snap and measure but do not move. *Convert to Polyhedron* makes real vertices.

## 6. `R-em3d47-6` — the drag, under the revised gate (overview §1b)

A face or vertex drag cannot be previewed by a rigid transform, because the neighbours change shape. So
each mouse move:
- runs the kernel operation on **that one object**;
- re-tessellates **that one object**;
- uploads **its** vertex and index bytes.

The kernel's cost on a solid of a few hundred faces is microseconds. The counter gate is: **per move, one
object tessellated, zero others, zero elaborations of children, zero document writes**. The commit writes
the document once, with one undo entry.

A polyhedron large enough for this to show (a prism from a 10,000-vertex outline, converted) is exactly
where §3d's hierarchy of face bounds and §3a's local recomputation matter. Gate 6 measures the **count**
of faces touched, not the time.

## 7. Gate

`tests/Ui.Tests/ThreeD/Kernel*` (headless; the kernel is below the firewall).

1. **Closure invariants, property-tested.** Random sequences of 200 operations (push/pull, free face move,
   vertex move) on boxes, prisms, L-shaped prisms with holes and irregular polyhedra. After every accepted
   operation:
   - every edge is used by exactly two faces, in opposite directions;
   - V − E + F = 2 − 2g, where g is the solid's genus, which is 1 per prism hole;
   - every face is planar within 1 DBU;
   - the signed volume is positive.

   Refused operations leave the solid byte-identical.
2. **Primitives stay primitives.** Push/pull on every box face and on every prism face in §2's table
   leaves the kind unchanged, and the written file changes only the fields named in the table.
3. **Push/pull keeps the neighbours' planes.** On a trapezoidal prism, pushing a slanted side face out
   leaves both neighbouring side faces on their original planes (1e-12 relative).
4. **Free move tilts, it does not fold, when it can.** Moving a box's `xmax` face in y gives a polyhedron
   with the same six face names and no folded face.
5. **Fold naming.** A vertex move that bends a quad gives `<name>.0` and `<name>.1`, and an attached
   boundary (a fixture entry in `FaceBoundaries`) now names both.
6. **Locality (counter).** A push/pull on a 10,000-face polyhedron touches only the moved face's
   neighbours for recomputation. The self-intersection check tests a number of face pairs bounded by the
   neighbourhood, not by F².
7. **Drag counters** (§6): one object tessellated per move, zero others.
8. **Cylinder refusals** say what to do, and *Convert to Polyhedron* gives the stated facet count.
9. **Align to Face.** Touching and flush results are exact, and non-parallel faces are refused.

## 8. Owner check (pixels not seen)

In the **Debug** build:
- push/pull each face of a box, and the slanted side of a trapezoid prism;
- drag a box face sideways;
- drag a vertex until a face folds, and see the fold;
- try to push a face through the opposite face and see red, then the refusal;
- extrude a bump from a pad;
- align a die onto a substrate top;
- confirm or change `N`, `G` and `E`.

"Does a face drag on the package lid keep up with the cursor" is the question.

## 9. Scope

- **No booleans, fillets, chamfers or face deletion** (overview §1b).
- No edge mode (D11).
- No editing inside an instance from the parent. Push in (brief 48).
- No sheet on a tilted plane (§4).
