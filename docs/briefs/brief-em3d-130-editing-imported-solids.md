# Brief 130 — Editing an imported piece (decision brief)

**Series:** [3D EM, ninth series](brief-em3d-126-overview.md) · **Tag:** `R-em3d130-n`
**Status:** **Decided 2026-10-09: the owner picked (b) then (a), as recommended; both built** (§4). Option (c) was not built.
**Area, depending on the option chosen:** `src/Design/ThreeD/Step/` (a new `StepConvert.cs`), `src/Design/ThreeD/Kernel/`
(`C3dFaceEdit`/`C3dBrep`), `tools/geometry-worker/geometry_worker.cpp` (option c only), `src/Ui/ThreeD/` (the tree menu and
the face-edit tool). Findings in `src/Design/RESOLVED.md`.
**Depends on:** 127; 129 for documents imported before 128.

---

## 0. The question

After 128 and 129, every piece of an imported package is its own Step object. Each can then be moved, rotated,
mirrored, arrayed, deleted, hidden and given a material. It can be a boolean operand, a fillet or chamfer target, and
a port's or a boundary's face. **What a piece cannot do is change shape.** The face and vertex editor
(`C3dFaceEditor`, brief 47) edits a box, a prism or a polyhedron only. A Step piece is a B-rep read from a file, and
its dimensions are not parameters.

The owner asked to be able to edit the pieces. Typical edits on a package model:
- lengthen or shorten a lead to meet a pad;
- thin the body;
- remove a tab;
- replace a detailed body with a plain block, so the mesh is smaller.

There are three ways to provide that. They differ in cost by an order of magnitude.

## 1. The options

### (a) Convert to Polyhedron (exact, for flat-faced pieces)

A piece whose faces are **all planar** becomes a `C3dPolyhedron` with the same faces. Face kinds come from the
kernel's face list (`GeometryKernelFace.Kind`). Vertex and face editing then apply as to any polyhedron.
- **Exact:** nothing is approximated. A piece with any curved face is refused, naming the faces: `"'<piece>' has 4
  cylindrical faces (face3, face7, ...); only a piece whose faces are all flat converts to a polyhedron"`.
- **Coverage:** straight leads and tabs, many plain bodies. **Not** gull-wing or J-leads with bend radii, and not a
  body with rounded edges.
- **Cost:** moderate. The worker returns each planar face's boundary loop (it already tessellates and names faces).
  The C# side builds the polyhedron. The open question is whether `C3dPolyhedron` represents a face **with a hole**
  (a lead frame's slot). If it does not, those pieces are refused too. Find out first and say so in the refusal.
- Face references move by geometry (overview rule 5): `face<n>` becomes the polyhedron's face that coincides.

### (b) Replace with Box or Prism (exact where the piece is one; a stated approximation otherwise)

- **Replace with Prism**, exact: offered when the piece **is** an extrusion. That means two parallel planar caps, every
  other face planar and perpendicular to them, and one boundary loop per cap. The result is a `C3dPrism` with the
  cap's polygon and the length, whose dimensions are then ordinary editable parameters. A straight lead or an
  undrafted body usually qualifies.
- **Replace with Box**, an approximation the user chooses knowingly: offered for any piece. It is the piece's
  bounding box in its own frame. The confirmation states the volume change: `"'<body>' is replaced by its bounding
  box: 6.10 × 9.80 × 1.27 mm, +7.4 % volume; its chamfers and drafts are not kept."`. **A face reference with no
  coinciding box face is refused, never moved to the nearest face.**
- Name, material, group, appearance and transparency are kept. The Step file is untouched.
- **Cost:** small to moderate. The prism test and the box are C# over the kernel's face list. The worker gains at most
  a "loops of a planar face" query, shared with (a).
- This is the edit an RF model most often wants: a body whose fine detail costs mesh and adds no accuracy, replaced
  by a block whose height is a parameter.

### (c) Edit a STEP piece's faces directly (B-rep local operations)

Push/pull a face of the piece itself, curved faces included, with the kernel's local operations: offset a face, move
a face, remove a feature. The piece stays a Step object plus an **edit list** replayed by the worker on every build,
the way a fillet is replayed.
- **Coverage:** everything, including bent leads.
- **Cost:** large. It needs:
  - a new operation kind in the `.c3d`;
  - worker operations whose failure modes (self-intersection, a face that cannot move that far) need refusals;
  - face naming after an edit (which `face<n>` is which after a face is removed);
  - the face-edit tool learning a fourth source kind;
  - re-pointing on reload through an edit list.

  **A spike comes first**: one face offset on a hand-written fixture, measured for robustness, before any brief
  builds it.

## 2. Recommendation

Build **(b)** first, then **(a)**. Defer **(c)** to a spike brief written only if (a) and (b) leave a real model
uneditable.

- (b) covers the most common RF edit (simplify the body) and the most common lead (straight), exactly where it can,
  and as an approximation the user sees and confirms where it cannot.
- (a) covers what (b) misses among flat-faced pieces, with no approximation.
- Both turn a piece into an **existing** editable type. Every editor, solver writer, exporter and test already
  understands those types, so nothing downstream learns anything new.
- (c) is the only option that adds a new kind of document state, and its cost is mostly in edge cases that only a
  spike can size.

## 3. If the owner picks (b) then (a): the build outline

Each item becomes a section of this brief, rewritten as a build brief when picked:
1. A worker query for each planar face's boundary loops in µm, in the face's own order (shared by (a) and (b)).
2. `StepConvert.PrismOf(piece)` (exact or refused, with the reason) and `StepConvert.BoxOf(piece)` (the bounding box
   and the volume change).
3. `StepConvert.PolyhedronOf(piece)`: exact, or refused naming every curved face (and every holed face, if
   `C3dPolyhedron` has no holes).
4. One `Apply`, shared by all three: replace at the same index; keep name, material, group, appearance and
   transparency; move references by geometry; refuse what does not coincide.
5. Tree menu: **Replace with Prism**, **Replace with Box…** (with a confirmation stating the volume change), and
   **Convert to Polyhedron**, each enabled only where it applies, with the reason in the tooltip otherwise. One undo
   entry each.
6. Gates on hand-written fixtures: an extruded L-section (a prism), a block with a chamfer (box only, with the volume
   change stated), a bent strip with a cylindrical face (refused by (a) and by Replace with Prism, offered Replace
   with Box), and a port on a face moved through each.

## 4. Built (2026-10-09)

- **Holes.** `C3dPolyhedron`'s faces have hole loops, honoured end to end, so a holed face converts and is never refused.
  `C3dPrism` has holes too, so a cap with a hole is still a prism.
- **Worker:** `loops`, each face's plane flag and its boundary loops in µm (outer first), with whether every edge is straight.
- **`src/Design/ThreeD/Step/StepConvert.cs`:** `Analyse` reads the piece once and plans all three; `PrismOf`, `BoxOf`,
  `PolyhedronOf` pick one; `Apply` is shared. Prism is the exact polyhedron recognised by `C3dRecognition.ZPrism` along z, y
  or x. Box's `Summary` is the confirmation sentence.
- **UI:** the tree's and the canvas's menus and *3D ▸ STEP*: Replace with Prism, Replace with Box…, Convert to Polyhedron.
  Each is disabled with its reason as a tooltip, and each is one undo entry.
- **Gate:** `tests/Ui.Tests/ThreeD/StepPieceEditTests.cs`. A port names no face, so the gate's reference is a face boundary.
- Findings: `src/Design/RESOLVED.md`.
- **After the owner's first trial:** a gull-wing lead was refused (curved bends) and a vendor body too (1.7 µm file
  tolerance). Convert to Polyhedron and Replace with Prism now cut curved faces into ≤ 15° facets, stated and confirmed,
  and every vertex is snapped onto its flat faces. Detail in `src/Design/RESOLVED.md`.

