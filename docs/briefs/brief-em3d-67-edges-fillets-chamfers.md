# Brief 67 — edges, fillets and chamfers

**Series:** [3D EM, fourth series](brief-em3d-60-overview.md) · **Tag:** `R-em3d67-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.4 (naming), §8.2 point 3; overview §1d (absent),
§1e (what the solvers respect), §1f (a fillet wraps its target), §1g (edge names), §1j
**Area:** `src/Render/Scene3D/Edit/` (`Scene3DSelection`, `Scene3DFeatureTable`, `SnapQuery3D`,
`Scene3DHitCycle`; new `Scene3DEdges.cs`, `Scene3DEdgeChain.cs`), `src/Ui/Viewer3D/` (Edge mode, the key,
the highlight), `src/Ui/ThreeD/` (new `C3dEditorViewModel.Fillet.cs`; `FaceEdit`, `TreeMenu`,
`TreeGrouping`), `src/Ui/ThreeD/C3dPropertiesViewModel.cs`, `src/Ui/Views/ThreeD/C3dEditorView.axaml`,
`src/Ui/Views/Viewer3D/Viewer3DView.axaml`, `src/Ui/Views/WorkspaceWindow.axaml`, `docs/user/src/reference/`
**Depends on:** 66 (the panel, the tree's feature rows, path addressing, refusal-not-rollback) ·
**Blocks:** 70
**Owner decisions:** D5 (the tree, **Enabled**), D6 (**E** is Edge mode; *Extrude to New Solid* moves to
**Shift+E**), D13

---

## 0. What this brief delivers

A fourth selection mode, **Edge**, beside Object, Face and Vertex — and, on the edges it selects, the two
operations the owner asked for:

| Operation | Key | What it does |
|---|---|---|
| **Edge mode** | `E` | a click selects one edge of a solid or a sheet; Shift-click adds or removes |
| **Select Tangent Chain** | double-click an edge | every edge that continues the clicked one smoothly |
| **Fillet…** | — | rounds the selected edges of one solid with a radius |
| **Chamfer…** | — | bevels them, at one distance or two |
| **Extrude to New Solid** | `Shift+E` *(was `E`)* | Face mode, unchanged but for its key |

Edge mode needs **no kernel**: every managed object's edges are named and selectable, so an edge can be
measured and snapped to on any machine. Fillet and Chamfer need the kernel and are **disabled with the
capability's sentence** without it (overview §1d).

A fillet or chamfer is a **row under the solid it rounds** in the object tree, with **Enabled**, its radius
(an expression) and its edge list in the Properties inspector.

---

## 1. `R-em3d67-1` — the key (D6)

**`R-em3d67-1a` E arms Edge mode in every 3D pane** — the editor and the read-only viewer a `.cem` opens —
because brief 40 §1l's rule is that two 3D panes never disagree about a key. It is one more case in
`Viewer3DViewModel.HandleKey`'s switch beside O, F and V, under the same *no modifier, no gesture* guard.

**`R-em3d67-1b` Extrude to New Solid moves to Shift+E.** Today `C3dEditorViewModel.FaceKey` claims plain
`E` in Face mode, and it runs **before** the pane's mode keys (`DrawKey` → `OperationKey` → `FaceKey`), so
left alone it would swallow Edge mode's key whenever Face mode is on. `FaceKey` opens with *"no modifier,
or return"*; it gains the one exception — `Shift+E` in Face mode starts *Extrude to New Solid* — and plain
`E` falls through to the pane. Every place the old key is written changes with it:
- the context-menu label *Extrude to New Solid (E)* in `C3dEditorViewModel.FaceEdit.cs`;
- both copies of the `3D ▸ Modify` item in `WorkspaceWindow.axaml` (the macOS `NativeMenu` and the in-window
  `Menu`, hand-mirrored);
- the comments in `C3dEditorViewModel.FaceEdit.cs` and `Operations/FaceEditTools.cs` that name `(E)`;
- the user reference: `docs/user/src/reference/drawing-in-3d.md` (the mode sentence, the Face-editing
  paragraph, and *Keys at a glance*: **O**, **E**, **F**, **V** and **N**, **Shift+E**, **T**) and
  `docs/user/src/reference/em-3d.md` (the selecting sentence). Sources only; DocGen is not run.

**`R-em3d67-1c` The toolbar** gains an **Edge** toggle in both panes (`C3dEditorView.axaml`,
`Viewer3DView.axaml`), between Face and Vertex — surfaces, then edges, then points — with a
`VectorLine` icon and the tooltip *"Edge mode: select edges; Shift-click adds  (E)"*.

## 2. `R-em3d67-2` — what an edge is, and its name

**`R-em3d67-2a` An edge is a named run.** `Scene3DFeatureTable` already finds feature segments — where two
different faces meet, or where a face has no neighbour (a sheet's rim) — face by face. This brief groups
them into **edges**: a maximal connected run of segments between the **same two faces**. A cylinder's rim is
**one** edge of many segments, not 32. A segment with the **same face on both sides** (a cylinder's seam, a
kernel face's seam) is not a feature and never an edge.

**`R-em3d67-2b` The name** (overview §1g) is the two faces' names, **relative to the object**, sorted
ordinally, joined by `|`: a box's `xmax|zmax`, a cylinder's `bottom|side`, a boolean result's
`cavity/side|zmax` (with brief 64's face spelling). A sheet's rim edge is `<face>|` — it has one side. Where
two faces share more than one run, the runs take `#1`, `#2` … ordered by their **lowest point**, compared
x, then y, then z, in the object's **own frame** — in DBU for a managed object, and for a kernel object in
the worker's coordinates **rounded to DBU first**, so floating noise cannot reorder them. This is the order
the overview leaves to this brief.

**`R-em3d67-2c` Where the runs come from.** For a managed object, from its triangles and face IDs, as the
feature table does today. For a kernel object, from the worker's **edge table** (brief 63): its names, its
polylines at display deflection, and for each edge its curve kind (line, circle, arc, other), its exact
parameter midpoint, and, for a circle or arc, its centre and radius. Triangle adjacency is not used for a
kernel object: a curved face's internal triangle edges are artefacts of deflection.

**`R-em3d67-2d` Resolution through an edit.** A face split by an edit follows brief 40 §1e's fold rule
(`zmax` becomes `zmax.0`, `zmax.1`; a boolean's `#1`, `#2`). An **edge name resolves to every edge between
the pieces of its two faces** — the fold rule applied on both sides — so a fillet on `xmax|zmax` follows a
fold of `zmax` onto both new edges. Only an edge name that resolves to **nothing** is a refusal (§6c).

## 3. `R-em3d67-3` — selecting, highlighting, snapping

**`R-em3d67-3a` Picking uses the pick patch, not a new GPU channel.** The ID pass writes (object, face) per
texel; an edge is one pixel wide and would need wide lines drawn into it on three GPU backends. Instead,
Edge mode's hover is the snap query's **edge tier** (`SnapQuery3D`): the (object, face) pairs in the patch,
their edges from the feature table, the **nearest visible** edge on screen within the snap radius, with the
visibility test brief 44 R-em3d44-2b already defines. It costs what a snap costs, bounded by the patch and
never by the scene (gate 2). The overview's *"edge IDs in the pick pass"* is met this way.

**`R-em3d67-3b` The selection item.** `Scene3DItem` gains an **edge index** into its object's edge table,
−1 otherwise, and `Scene3DSelectMode` gains `Edge`. Selection is by **name** in the editor's model (an edge
index is a scene detail, rebuilt per elaboration), so a selection survives a re-elaboration that keeps the
name.

**`R-em3d67-3c` Highlight.** The hovered edge and the selected edges are drawn by the **2D overlay**
(`Viewer3DDrawOverlay`), projected from the camera each frame — a thicker line in the hover or selection
colour, as a construction polyline is drawn. No GPU buffer changes when the hover moves.

**`R-em3d67-3d` B and Shift+B** (`Scene3DHitCycle`) in Edge mode step through the edges within the snap
radius of the cursor's ray, **nearest depth first, hidden ones included** — the point of B is to reach what
is behind. The list is discarded on a mode change, as today.

**`R-em3d67-3e` Snapping to curved edges** (all modes, every tool):
- **vertices** are the B-rep's vertices — a curved edge's polyline points are **not** vertices, because
  they move with the display deflection;
- the **midpoint** of an open curved edge is its **parameter midpoint**, from the worker, not the middle of
  its polyline; a **closed** edge (a circle) has none;
- the **nearest point on an edge** runs along its polyline, and is marked inexact (`≈`, brief 44 R-em3d44-4)
  unless it lands on a vertex;
- **a circle's or an arc's centre** is a snap target, drawn with the face-centre marker with a dot in it:
  it is how a pin is put exactly on a bore's axis. *(A proposal; the owner confirms it.)*

**Every managed object snaps exactly as before.** A managed cylinder's rim keeps the snap points it has
today; the named-edge grouping is for selection only (gate 4).

**`R-em3d67-3f` What Properties shows** for one selected edge: its name, its two faces, its kind, its length,
and for a circle or arc its radius and centre — every value selectable and copyable, in the display unit.
With several edges: their count and total length. This works with no kernel for managed objects.

## 4. `R-em3d67-4` — Select Tangent Chain

A **double-click** on an edge, or *Select Tangent Chain* in its context menu, selects every edge of the same
object reachable from it through vertices where the two edges' tangents differ by **less than 1°** — the
loop around a filleted top, the whole rim of a rounded slot. The walk is headless (`Scene3DEdgeChain`), uses
the end tangents the edge table carries (exact from the worker for a kernel edge; the segment direction for
a managed one, where only collinear edges chain), stops at a vertex where more than one continuation
qualifies, and says so on the status line (*"stopped at a branch: 3 edges"*). Shift+double-click adds the
chain to the selection.

## 5. `R-em3d67-5` — Fillet… and Chamfer…

**`R-em3d67-5a` Where.** Edge mode's context menu and *3D ▸ Modify*, on one or more selected edges **of one
solid**. Legal targets are those of brief 66 §1a (Box, Prism, Cylinder, Polyhedron, Boolean, Step, and a
solid already filleted or chamfered). Refused, each with its sentence as the disabled item's tooltip:
- edges of **more than one object**: *"A fillet rounds one solid: select edges of one object."*;
- a **sheet's** edge: *"A sheet has no volume to round."*;
- a **wire**: its shape is its points' (brief 50); an **instance's** part: *Push into the cell*, as
  `TreeMenu` already says for a part;
- **no kernel**: the capability's sentence, read from it.

**`R-em3d67-5b` The panel** is brief 66's popover, reused:
- **Fillet:** *Radius*, in the typed field of brief 45 §4, which accepts an **expression** and offers to
  define an unknown name as a VAR (brief 51);
- **Chamfer:** *Equal distances* or *Two distances*; with two, a **Flip** button chooses which face the
  first distance is measured on, and the preview shows which;
- the **edge list**: a count (*"4 edges"*) and the names, each with a remove ×. **While the panel is open,
  Shift-click in the view adds or removes edges** — the panel is not modal — and a double-click adds a
  chain. Edges of another object are refused on the status line;
- the refusal line and **OK** / **Cancel**, as brief 66.

**`R-em3d67-5c` Preview** exactly as brief 66 R-em3d66-2e: asynchronous, superseded, the target ghosted and
the result a transient batch uploaded once per reply, OK disabled until a valid reply.

**`R-em3d67-5d` Refusals name the edge.** The worker reports which edge or corner failed; the panel says it
in the user's terms, for example:
- *"A 50 µm radius does not fit edge 'xmax|zmax': the faces beside it are 30 µm wide. Try less than 30 µm."*;
- *"The kernel cannot blend the corner where 'xmax|zmax', 'ymax|zmax' and 'xmax|ymax' meet at different
  sizes. Fillet them together, or one at a time."*;
- a radius or distance that is not positive, or does not resolve, is refused by the typed field itself.

**`R-em3d67-5e` OK commits once.** One document change, **one undo entry** (*"Fillet 4 edges of lid"*), zero
further worker calls (the preview's reply is adopted, as brief 66 R-em3d66-2g). The new `Fillet` or
`Chamfer` **wraps** its target and **takes its place and its name** (overview §1f), so every port, face
boundary and wire that referred to the target's faces still does: the target's faces keep their names, and
the faces the operation creates are `fillet(<edge>)` and `chamfer(<edge>)`. A second Fillet… on the same
solid wraps again; it does not silently merge into the first — the first's edge list is edited in Properties.

## 6. `R-em3d67-6` — the tree, the inspector, and edits later

**`R-em3d67-6a` The tree (D5).** A solid with fillets and chamfers is **one node**, named with the name
references use; beneath its own children (a boolean's operands, if it is one) come its **feature rows**,
innermost first: *Fillet 50 µm — 4 edges*, *Chamfer 20 µm — 1 edge*, each with the `RoundedCorner` or
`AngleObtuse` icon. Grouping follows brief 66 R-em3d66-3b: the node sits in its material's group; feature
rows are never listed elsewhere.

**`R-em3d67-6b` The inspector** for a feature row:
- **Radius** (Fillet) or **Distance** / **Distance 2** and **Flip** (Chamfer) — expression fields;
- **Enabled** — a toggle;
- **Edges** — the list; *Show* selects them in the view (switching to Edge mode); *Edit…* reopens the panel
  on them, so edges are added and removed exactly as at creation;
- **Remove** — unwraps the feature: the target returns at its place with its name, one undo entry.

Each is one undo entry and is re-evaluated through the client; **an evaluation that fails is not rolled
back** — the row carries a refusal, as brief 66 R-em3d66-4 sets out.

**`R-em3d67-6c` When the target changes.** An edit to the target — resizing the box, moving an operand of a
boolean underneath — re-evaluates the chain once, on release (brief 66 R-em3d66-5b's counter). An edge that
still resolves (§2d) is rounded again, at the new geometry. An edge that resolves to **nothing** makes the
feature row a refusal naming it: *"Edge 'xmax|zmax' of 'lid' no longer exists: its face 'zmax' was removed.
Edit the fillet's edges."* **It is never mapped to a nearby edge by guess** (em-3d.md §6.4).

**`R-em3d67-6d` Enabled off** elaborates the target **unrounded**. A port or face boundary on a
`fillet(<edge>)` face then names a face that does not exist, and is a refusal saying so: *"'fillet(xmax|zmax)'
of 'lid' exists only while its fillet is enabled."*

**`R-em3d67-6e` Direct face edits (D13).** Face-mode edits on a filleted solid are refused with brief 66
§5c's sentence, adapted: *"'lid' is rounded by a fillet: edit the box (disable the fillet, or change it in
Properties)."* The target inside the chain stays a managed object and is edited with the chain disabled —
that is the path the sentence names.

---

## 7. Gate

View-model and headless tests. `KernelFact` tests skip with a reason without the worker; the rest run
everywhere.

1. **Names (no kernel).** A box has 12 edges with the expected `a|b` names; a prism's names follow its face
   names; a cylinder has exactly two (`bottom|side`, `side|top`) and no seam; a sheet's rim edges are
   `<face>|`; a shape with two runs between one face pair gives `#1`/`#2` in the §2b order, and the order is
   unchanged when the object is moved or rotated.
2. **Picking (no kernel).** On the CPU patch fill: hovering a box edge's projected midpoint returns that
   edge; a hidden edge is not returned; B reaches it. The hover examines a feature count bounded by the
   patch, not the scene (brief 44's gate 1 form).
3. **Grouping.** A managed cylinder's rim is one edge whose run has the tessellation's segment count.
4. **Snapping unchanged on managed objects.** For each managed primitive kind, the snap feature set is equal
   before and after this brief; every existing snap test passes unchanged.
5. **Curved snaps (`KernelFact`).** On a boolean result's circular edge: the nearest point is within the
   display deflection of the exact circle and is marked inexact; the centre snap is the circle's centre in
   DBU; an open arc's midpoint is the worker's parameter midpoint.
6. **Tangent chain.** Managed: collinear edges chain, a right-angle corner stops it. `KernelFact`: the top
   loop of a box whose four vertical edges are filleted chains to 8 edges.
7. **Keys.** `E` arms Edge mode in the editor and in the read-only viewer. In Face mode, `Shift+E` starts
   *Extrude to New Solid* and plain `E` switches to Edge mode without extruding. The menu labels read
   `(Shift+E)`.
8. **Commit (`KernelFact`).** Fillet on one box edge: one undo entry, zero worker calls at commit; a face
   boundary on the box's `zmax` still resolves; the new face is `fillet(xmax|zmax)`.
9. **Refusals (`KernelFact`).** A radius larger than the adjoining face: the panel names that edge, OK is
   disabled, the document is unchanged.
10. **Enabled off.** The problem equals the unwrapped target's; a boundary on the fillet face is a refusal
    naming it.
11. **Edges through edits.** Folding `zmax` makes a fillet on `xmax|zmax` round both new edges; deleting the
    face's neighbour so the edge resolves to nothing makes the row a refusal naming the edge.
12. **No kernel.** Edge mode, its Properties and snapping work; Fillet… and Chamfer… are disabled with the
    capability's sentence.

## 8. Owner check (pixels not seen)

In the **Debug** build:
- press **E**, hover the edges of a box and a cylinder, Shift-click several, read Properties;
- in Face mode, confirm **Shift+E** extrudes and **E** now switches to Edge mode;
- after brief 66's walk-through, pick the edge around the hole in the lid; double-click to take the chain;
  *Fillet…* 50 µm; watch the preview; OK;
- try a radius that cannot fit and read the sentence;
- untick **Enabled** on the fillet row; tick it; change the radius to an expression (`r_edge`), define it;
- resize the lid with a face push after disabling the fillet, re-enable it, see it follow;
- snap a new cylinder onto the hole's centre.

"Does picking one edge among many on a small part feel as easy as picking a face" is the question — and
whether **E** for Edge and **Shift+E** for extrude are the keys the owner wants.

## 9. Scope

- No variable-radius fillets, no setback or corner-blend options, no face-to-face fillets.
- No other edge operations: an edge is not moved, split or deleted in this series.
- Fillets and chamfers on solids only; none on sheets.
- No parametric sweep of a radius (overview §4).
- No new GPU pick channel (§3a).
- No timing test. Counters only.
