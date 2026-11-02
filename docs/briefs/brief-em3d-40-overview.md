# Brief — 3D EM, third series: the 3D view, drawn and edited

**Status:** Briefed, not built · **Date:** 2026-09-26 · **D1–D4 decided by the owner, 2026-09-26** (§3) ·
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) rev 5 (Draft) — phase **F4**, §6.2–§6.4, §8
**Previous series:** [`brief-em3d-0-overview.md`](brief-em3d-0-overview.md) (1–10) and
[`brief-em3d-20-overview.md`](brief-em3d-20-overview.md) (20–31), all built
**Area:** `src/Design/Cells/` (a fourth view type), `src/Design/ThreeD/` (new: the document, its
elaboration, the editing kernel), `src/Engine/Em3d/` (two new primitives, face boundaries),
`src/Design/Em3d/` (both backends lower them), `src/Render/Scene3D/` (face IDs, the grid, snapping,
gizmo geometry), `src/Ui/Viewer3D/` + `src/Ui/ThreeD/` (new: the editor), `src/Cli/`, `examples/`, `tests/`
**Requirement tag:** `R-em3d<n>-<m>` as before. This series is numbered **40–52**.

---

## 0. The short answer

Series 1 and 2 generate a 3D model from a layout and solve it. Nobody can **draw** one. This series adds
a **3D view** to a cell — a fourth view beside schematic, symbol and layout — in a new document, the
**`.c3d`**, which the user draws, edits and simulates from one window. When it is done:

- **Three selection modes.** Object (**O**), Face (**F**) and Vertex (**V**), from the keyboard or from
  toolbar buttons. What is under the cursor highlights. A right-click opens the operations for that
  kind of thing. **B** steps to the next thing *behind* the current one along the cursor's line of sight.
- **Drawing.** Box, sheet, polygon, polyline and cylinder, on a drawing plane (XY, YZ, XZ) with a subtle
  grid. A box takes three clicks: two corners, then its height.
- **Snapping** to vertices, edge midpoints, edges, face centres and the grid. It is as fast as the layout
  editor's snapping.
- **Editing that keeps a solid closed.** Move a face, push it along its normal, or move a vertex, and the
  neighbouring faces follow.
- **Hierarchy.** Place an instance of another cell's 3D view, **or of its layout view**, which is turned
  into 3D through that layout's own technology. Mirror it, rotate it, array it, push into it. An MMIC
  drawn in µm on one technology sits inside a package drawn in mil on another.
- **Simulate from the document.** The `.c3d` carries its own EM setups, in the same schema a `.cem`
  uses. A `.cem` may also point at a `.c3d`. Ports are drawn in the parent. The ports of sub-cells are
  ignored and not drawn.

### The one rule this series adds

> **What the editor shows is what the solver gets.** The editor draws the **elaborated** model — the same
> `Em3dProblem` both backends lower — and never a second picture of the document. An edit changes the
> document, the document is elaborated again, and the scene is built from that. A drag previews by moving
> what is already drawn, and commits once, on release.

This is series 2's rule ("everything a demonstration shows is something the solver produced") applied to
authoring. It is also CLAUDE.md's rule for the circuit side: *elaborate first*.

---

## 1. Things that are not obvious, resolved here once

### 1a. Skipping F3 is fine

F4 does not depend on F3 (thermal). F3 reads the same `Em3dProblem` this series produces. So a thermal
solve on a drawn `.c3d` works when F3 lands, with no rework. One thing here is shaped for F3: **face
selection** (brief 43) and **face boundaries** (brief 49) are what a thermal solve needs to put a heat
source or a convection coefficient on a surface.

### 1b. No OpenCASCADE worker in this series (owner decision D2 — decided 2026-09-26)

`em-3d.md` §6.2 puts F4 on **Route B**: a native C++ geometry worker linking OpenCASCADE. That is a native
dependency (CLAUDE.md: *ask before*). Nothing the owner has asked for needs it:
- boxes, prisms, sheets, polylines, cylinders and polyhedra with **planar faces**;
- moving objects, faces and vertices;
- duplicate, mirror, arrays and hierarchy.

All of these are small, exact, managed computations. A face move on a solid of a few hundred faces takes
microseconds. So **this series writes a managed polyhedral editing kernel** (brief 47) and adds **no native
dependency**. It is ordinary C# below the firewall, and headless tests cover it.

**What would bring the worker back:**
- **booleans** (subtract a cavity from a lid);
- **fillets and chamfers** on curved edges;
- **STEP import** (connectors and packages come from their makers as STEP).

Each is a real feature and a later brief. None of them is here. The `.c3d` format leaves room for all three
(a boolean is an object whose operands are other objects, §1f).

**The owner's decision (2026-09-26): OpenCASCADE WILL be added, in a later series, as an optional
capability the user installs.** Drawing and editing basic shapes never needs it. A user who installs it
gets booleans, rounded edges and STEP import. So F4 splits into **F4a**, the managed modeller built here,
and **F4b**, the OpenCASCADE geometry worker, found and installed the way the solvers are (§7.1, §7.2).
`em-3d.md` rev 6 records this, and Open 3 (the Tier B format) is closed by brief 41.

**What F4b will need from this series, so it is kept true now:**
- **the managed kernel stays the editing path** whether OpenCASCADE is present or not. F4b adds
  operations; it does not take over the ones built here;
- **a document must stay usable on a machine without OpenCASCADE.** A `.c3d` with a boolean in it, opened
  where OpenCASCADE is absent, draws the operands, marked, with a banner naming the missing capability. It
  is not a refusal to open. This is a constraint on F4b's format, recorded here so that nothing in briefs
  41–52 makes it impossible (brief 41 §2d already reads an unknown `$type` as a named refusal of that one
  object, not of the document);
- **the Palace path may not need the worker at all for a boolean.** Gmsh embeds OpenCASCADE (§6.2 Route
  A), so a boolean can be lowered as a `.geo` boolean. openEMS needs the worker's tessellation. F4b's
  brief decides this; nothing here forecloses it.

A consequence of the managed kernel: §8.6's gate *"a drag makes zero kernel calls until release"* was
written for an OCCT rebuild that takes tens of milliseconds. Here the gate becomes **a drag re-tessellates
only the dragged object and uploads only its bytes**. It is still a counter.

### 1c. The view's name, folder and extension (owner decision D1 — decided 2026-09-26: `.c3d`)

The full-wave suites say "3D layout", "3D model", "3D component" or "3D structure". The short word they
share is **3D**. So:

| | Value |
|---|---|
| User-facing view name | **3D** (Project Tree: *Schematic · Symbol · Layout · 3D*) |
| Cell sub-folder | **`3d/`** |
| Extension | **`.c3d`** |
| `ViewType` member | `ThreeD` (an identifier cannot start with a digit) |
| Code | `src/Design/ThreeD/`, type prefix `C3d` (as `CemPalace`, `CcellFile`) |

**`.c3d` has one known collision:** an established motion-capture interchange format uses the same
extension. circuitRF classifies a file by content as well as extension (`DocumentKinds.Classify`), so
nothing inside circuitRF is ambiguous. The cost is only an OS file association on a machine that also has
motion-capture software installed. **The owner chose `.c3d` knowing this** (2026-09-26).

### 1d. Coordinates are integer DBU, and every object carries a placement (owner decision D4 — decided 2026-09-26)

A `.c3d` stores coordinates the way a `.clay` does:
- integer database units, `DbuPerMicron` (default 1000, so 1 DBU = 1 nm);
- a separate `DisplayUnit` (nm, µm, mm, mil, inch — `LayoutUnit`, reused);
- a separate snap grid.

The reason is **exact coincidence**:
- two solids drawn to touch share a face *exactly*;
- Gmsh's fragment then produces one shared surface rather than a sliver;
- the FDTD grid then gets one line where the two faces meet, not two lines a rounding error apart (§12:
  one tiny cell sets the time step for the whole run).

Doubles would make touching a matter of tolerance.

**The display unit behaves exactly as the layout editor's (owner, 2026-09-26):**
- a new `.c3d` takes its **default display unit** from the cell's primary `.clay` when the cell has one,
  otherwise from its technology's `DefaultDisplayUnit` (the `.ctech`), otherwise µm. The cell's own
  layout is more specific than its technology: it is the unit someone already chose for this cell. This
  is the rule a new layout already follows for its technology half (`WorkspaceViewModel`'s new-layout
  path: `tech?.DefaultDisplayUnit ?? LayoutUnit.Um`);
- `DbuPerMicron` is the `.clay`'s if the cell has one, else `LayoutUnits.DefaultDbuPerMicron`. `SnapDbu`
  is the technology's `DefaultSnapDbu`;
- the user changes the display unit with the **same Unit combobox** the layout editor's toolbar has
  (`LayoutEditorViewModel.AllUnits`, lower-case items). As there, it is a **document preference, not
  geometry**: it marks the document dirty and is saved in the `.c3d`, adds **no undo entry**, and moves
  nothing (layout-view §1.3, *changing the display unit is free*).

**Rotation would break that if it were baked in.** A box rotated by 30° has irrational corners, and
rounding them to DBU makes its faces non-planar. So **every object and every instance carries a
placement**: an integer origin, an ordered list of axis rotations, and a mirror. Its geometry stays integer
in its own frame. A rotation by a multiple of 90° is exact (`LayoutAngle.CosSin`'s quadrant handling). Any
other angle is exact in the object's own frame and double in the world, which is what a rotated object
genuinely is.

### 1e. Face identity is stored, so a boundary survives an edit (§6.4)

§6.4: *never store a face index*. A boundary or a port attached to "face 17" moves to another face when the
solid is rebuilt. This series stores **face names**:
- a primitive's faces have fixed names: a box's `xmin` … `zmax`, a prism's `bottom`, `top`, `side0`…, a
  cylinder's `bottom`, `top`, `side`;
- a polyhedron stores a **name per face** in the document.

A face keeps its name through every move. When the kernel **folds** a face that has become non-planar into
triangles (brief 47), the pieces inherit the name, with a suffix. Whatever was attached to the face goes
with all of them. A boundary whose face no longer exists is a refusal naming the object and the face,
never a guess (§6.4's last sentence).

### 1f. The document is a flat list of objects, not a history (§6.3, narrowed)

§6.3 describes Tier B as a **construction history**: primitives and operations, re-run on every edit. With
no booleans (§1b) there are no operations to re-run. So the `.c3d` holds **objects**:
- each is a primitive or a polyhedron, with a placement, a material and a name;
- objects are kept in construction order, which is `Em3dSolid.Order` — the overlap rule the backends
  already honour.

A boolean, when it comes, is an object whose operands are other objects. The format reserves that shape
and does not implement it. Expressions come in brief 51 on the same objects. This is the smallest honest
reading of §6.3 that leaves both open.

### 1g. A primitive stays a primitive for as long as it can

Editing never throws away information it does not have to:
- a box whose face is pushed along its normal is still a box;
- a prism whose top is moved sideways is an **oblique** prism, still a prism;
- **a rotation never converts anything**: it lives in the placement (§1d), so a box rotated by any angle
  is still a box in the document.

Only an edit the primitive cannot express converts the object to a **polyhedron**: a vertex moved, or a
face moved in a way the primitive has no parameter for. The conversion is undoable, and the status bar
says so.

The **lowering** (brief 42) then uses the richest neutral primitive that states the geometry exactly: an
axis-aligned box is `Em3dBox`, a horizontal prism `Em3dExtrudedPolygon`, a cylinder `Em3dCylinder`, and a
box rotated by 30° about X becomes an `Em3dPolyhedron` only there. That keeps the FDTD path free of
triangle files wherever it can be (§6.5).

### 1h. The neutral problem grows two things, and every existing golden stays

`Em3dProblem` today can state:
- horizontal extrusions (`Em3dExtrudedPolygon` is z-only);
- horizontal sheets (`Em3dSheet` has one `Z`);
- boundaries on the air box only.

A drawn model needs three more things:
1. **`Em3dPolyhedron`**: vertices, and planar faces with holes. It covers prisms on other planes, oblique
   prisms, rotated boxes and edited solids.
2. **A sheet in any plane**, for a port or a sheet drawn on YZ or XZ.
3. **A boundary on a named face of a named solid** (brief 49).

Palace gets a polyhedron through `.geo` points, curve loops, plane surfaces and a surface loop. openEMS gets
CSXCAD's `Polyhedron` primitive. The FDTD grid gets its lines from the polyhedron's axis-aligned edges. An
oblique face is staircased and reported, as a curved one already is.

**Gate for all of this:** every existing Palace and openEMS golden, and every existing tessellation count,
is **byte-identical**. A problem that uses none of the new records writes exactly what it wrote before.

### 1i. Layout instances: the layout's own technology, the stackup's bottom at the instance's z

A `.clay` has no z. When a `.clay` is placed in a `.c3d`, three rules decide its 3D form (brief 42):
- **Technology: the layout's own.** It is resolved as the layout resolves it, not as the parent does.
  This is the point of the feature: GaAs inside ceramic.

  `ExternalWorkspaceGate`'s rule — *an external cell may only be instanced when both sides resolve to the
  same `.ctech`* — exists because a **layout** hierarchy matches layers by number. A `.c3d` matches
  nothing by layer. Every instance becomes metres and named materials before the parent sees it. So **the
  gate does not apply inside a `.c3d`**, and the reference page says why.
- **z: the bottom of the layout's stackup sits at the instance's z.** That is where a die's back meets
  its attach. The die-attach height that `workspace-and-project-tree.md` §1.2.1 said an assembly was
  missing is simply the instance's z.
- **Lateral extent: bounded.** A planar stackup's dielectrics are laterally infinite. Inside a parent they
  cannot be, so an instance's slabs and planes are bounded by its **board outline**. With no outline they
  are bounded by the **bounding box of its drawn geometry**, and the elaboration note says which.

**This needs one refactor.** `Em3dGenerator` today generates the whole problem: geometry, air box, ports
and sweep. Brief 42 splits out **"the solids of a layout"**, meaning geometry and materials only. The
generator then calls that part, and so does the `.c3d` elaborator. The gate is that the generator's output
is byte-identical before and after the split.

### 1j. Materials from two technologies can share a name

A GaAs technology's `Gold` and a package technology's `Gold` may have different σ. Elaboration **merges two
materials with the same name when their resolved values are equal**. Otherwise it keeps both and qualifies
the name with the technology: `Gold@gaas-mmic`. It says so in a note. A silent merge would give one of them
the wrong conductivity, and nothing would look wrong.

### 1k. Sub-cells contribute geometry only

An instance's **ports**, its **embedded setups**, its **air box** and its `.clay`'s **port shapes** are:
- ignored by elaboration;
- **not drawn** in the parent.

Ports belong to the parent, because only the parent knows where the signal enters. This is the owner's
rule, and it is also how a schematic sub-cell's pins become nets rather than ports in its parent.

### 1l. The keyboard: F, O and V are taken, and the owner's choice wins (owner decision D3 — decided 2026-09-26)

Three of the requested keys are already used:
- **F is Fit** in every canvas in circuitRF: schematic, symbol, layout, Smith, Data Display, and the 3D
  viewer;
- **O is orthographic** in the 3D viewer;
- 1–7 are the standard views.

The owner asked for O, F and V, so in **every 3D pane** — the editor, and the read-only viewer a `.cem`
opens:
- **O** is Object mode, **F** is Face mode, **V** is Vertex mode;
- **Home** is Fit (Home is Fit in most 3D tools), also on the toolbar and the menu;
- **P** toggles perspective and orthographic.

**The owner confirmed (2026-09-26): in the 3D editor, F arms Face mode**, knowingly a departure from
every other editor. The read-only viewer changes too, because two 3D panes where F means different things
would be worse than either choice. The layout and schematic canvases keep **F = Fit**.

### 1m. One top-level menu, always present (owner decision D9)

The 3D editor needs a menu of its own. On macOS, Avalonia binds **one `NativeMenu` per window for its
lifetime** (harmonicaRF R3's finding). A top-level menu that appears and disappears with the active
document is therefore the fragile path. So the application gets **one new top-level menu, `3D`**, always present. Its items are enabled only
when a 3D document is active, and each tooltip says so. This is the pattern the Design menu already uses
(*"Requires an active layout document"*). Commands that already exist are reused, not duplicated:
- *Design ▸ Place Cell Instance…* places into a `.c3d`;
- *Simulate ▸ Run* runs the active setup;
- *Edit ▸ Undo* undoes.

### 1n. Nothing in this series can be seen from an agent's session

As in series 2 (§1e there):
- every gate is a counter or a headless check, never a timing and never a pixel;
- every brief with a visible surface ends with an **owner check list** of what to try by hand in the
  **Debug** build (the one the owner runs);
- every completion note says pixels were not seen.

Metal can render offscreen here (`CRF_VIEWER3D_PNG`), which brief 28 used. It proves a picture was drawn.
It does not prove the picture feels right.

---

## 2. The briefs

| # | Brief | Delivers | Depends on |
|---|---|---|---|
| 41 | [the 3D view and the `.c3d`](brief-em3d-41-the-3d-view-document.md) | `ViewType.ThreeD`, `3d/`, the format, persistence, primacy, Project Tree, New 3D View, every reference walker, the reference page, `new cell --views 3d`, `check`/`find` | — |
| 42 | [elaborate and run, headless](brief-em3d-42-elaborate-and-run.md) | `.c3d` → `Em3dProblem`; `Em3dPolyhedron` and planar sheets in both backends; the layout-solids split; instances of both views; units; materials; embedded setups; `.cem` → `.c3d`; `em`/`explain`/`render` on a `.c3d` | 41 |
| 43 | [the editor: window, modes, selection](brief-em3d-43-editor-and-selection.md) | the document window, O/F/V modes and toolbar, face IDs in the pick pass, highlight, **B** to go behind, the context menu's frame, the object tree, properties, the `3D` menu, undo, save; the read-only viewer takes the same keys | 42 |
| 44 | [snapping](brief-em3d-44-snapping.md) | vertex, midpoint, edge, face centre and grid snaps; the pick-patch approach; a marker per kind; counters | 43 |
| 45 | [drawing](brief-em3d-45-drawing.md) | the drawing plane and its grid, box (three clicks), sheet, polygon, polyline, cylinder, typed values mid-gesture, extrude | 44 |
| 46 | [object operations](brief-em3d-46-object-operations.md) | move (base point → target, axis constraint), move along axis, rotate, mirror, duplicate, array, align, material and role, delete, the move gizmo; **Measure** (two points, deltas, distance, copyable) | 44 |
| 47 | [face and vertex editing](brief-em3d-47-face-and-vertex-editing.md) | the managed kernel: move face, move along normal, move vertex, closure by folding, self-intersection refusal, extrude face to a new solid, copy face as sheet, face measurements | 46 |
| 48 | [hierarchy](brief-em3d-48-hierarchy.md) | placing 3D and layout instances, swap view, 3D arrays, instanced drawing, push in / pop out, flatten, group into cell, cycle detection, stale-child reload, *New 3D view from layout* | 43 (44 for snapping into instances) |
| 49 | [simulate from the document](brief-em3d-49-simulate-from-the-document.md) | the Setups panel on embedded setups, the port tool (lumped, wave), contact-inferred port polarity, the air box drawn and edited, face boundaries, Simulate, results and fields over the drawn model | 42, 47 |
| 50 | [bond wires in a `.c3d`](brief-em3d-50-bond-wires.md) | a wire object between two snapped points, across hierarchy — die pad to package lead — with §6.6's profile, feet and loop height | 45, 48 |
| 51 | [dimensions as expressions](brief-em3d-51-dimensions-as-expressions.md) | a dimension typed as an expression; an unknown name defined on the spot as a **VAR stored in the `.c3d`**; VARs linked to same-name cell parameters so the parameter passes down; the Variables panel; instance overrides; the drag rule for a bound dimension | 45, 47, 48 |
| 52 | [the showcase](brief-em3d-52-showcase.md) | an example workspace (an MMIC `.clay` in µm, inside a package `.c3d` in mil, wired and simulated), the user page, the newcomer walk-through | all |

**Tracks:**
- **Headless:** 41 → 42. Everything else stands on these.
- **Editor:** 43 → 44 → 45, 46 → 47.
- **Hierarchy and simulation:** 48 and 49 once the editor exists, then 50 and 51.
- Brief 52 comes last.

**Smallest demonstrable cut:** 41, 42, 43, 44, 45, 49. That is enough to draw a box on a board, add a port
and simulate it. Hierarchy (48) is the next cut, and it is the one the owner's MMIC-in-package case needs.

---

## 2A. Traceability — the owner's request, and where each part is built

| Request | Brief |
|---|---|
| Phase F4 of em-3d.md | the series; §1b narrows it |
| Object / Face / Vertex modes on O / F / V, toolbar buttons with Material icons | 43 |
| Click selects and highlights; right-click opens that kind's operations | 43 (frame), 46, 47 (operations) |
| Face: Move, Move Normal, Apply boundary; the solid stays closed | 47, 49 |
| Other face commands | 47 §2 (extrude face, copy as sheet, align to face, measure, select owner), 49 (port on face, boundary) |
| **B** selects the next face (or object) behind | 43 |
| Objects: move along axis, duplicate | 46 |
| Draw boxes, sheets, polylines (to extrude later) | 45 |
| Drawing plane XY / YZ / XZ with a subtle grid; box in three clicks | 45 |
| Snapping to corners, fast | 44 |
| A 3D view of a cell; its name and extension | 41, §1c |
| The document stores its own display units | 41 |
| A `.cem` can point at it; it can carry its own setups; no planar solve | 42, 49 |
| Author, edit and simulate from its window, with its own menu | 43, 49, §1m |
| Hierarchy: instances, arrays, mirror | 42 (elaboration), 48 (editor) |
| A `.clay` instance made 3D from its own `.ctech`, µm inside mil | 42, §1i |
| Choose, and swap, the 3D or layout view per instance | 48 |
| Sub-cell ports ignored and not drawn | 42, 48, §1k |
| A dimension typed as an expression; an undefined name prompts a definition and becomes a VAR stored in the `.c3d` | 45 §4a (the field), 51 |
| VARs reconciled with cell parameters; a same-name parameter passes down into the VAR | 51 §3, D12 |
| Measure in 3D: two points, their x/y/z, the deltas and the distance, all selectable and copyable; no dimension drawn in 3D | 46 §6 |

---

## 3. Decisions — D1–D4 made by the owner on 2026-09-26, the rest open

| # | Decision | Brief's default | Blocks |
|---|---|---|---|
| D1 | The view's name, folder and extension (§1c) | **DECIDED: 3D, `3d/`, `.c3d`** | 41 |
| D2 | The geometry kernel (§1b) | **DECIDED: managed, no native dependency in this series.** OpenCASCADE comes later (F4b) as a capability the user installs, which unlocks booleans, rounded edges and STEP import | 47 |
| D3 | Keys (§1l) | **DECIDED: O / F / V.** In the 3D editor, F arms Face mode. Fit moves to **Home** and **P** toggles projection, in every 3D pane | 43 |
| D4 | Coordinates (§1d) | **DECIDED: the `.clay`'s DBU, plus a placement per object.** Default display unit from the cell's `.clay`, else the `.ctech`; changed with the layout editor's Unit combobox | 41, 43 |
| D5 | Setups: embedded in the `.c3d`, or only in `.cem` files | **both**. A `.c3d` carries any number of setups in the `.cem` schema, and a `.cem` may name a `.c3d` | 42, 49 |
| D6 | A cylinder tool, which was not asked for | **in**. Vias, pins and coax need it, and both backends already take cylinders | 45 |
| D7 | Bond wires that cross hierarchy (brief 50) | **in**. The MMIC-in-package case has no signal path without them | 50 |
| D8 | Dimensions as expressions (brief 51) | **in, last**. Brief 45's typed field is built for it but need not accept expressions at its own gate (owner, 2026-09-26). Brief 41 stores sizes, not second corners, so a width has a field to bind to | 51 |
| D9 | The menu (§1m) | **one top-level `3D` menu**, always present, enabled per document | 43 |
| D10 | When the `3d/` sub-folder is created | **when the first 3D view is made**, not with every cell. Every existing cell lacks it anyway, so all code must handle its absence | 41 |
| D11 | An Edge mode (not asked for) | **not in this series**. Vertex and face cover the edits asked for. It is a later brief if wanted | — |
| D12 | A VAR with the same name as a cell parameter | **linked by default**: the parameter passes down, meaning an instance's override, else the `.ccell` default, and the VAR's own expression waits unused. Unlinking makes the VAR hide the parameter, and `check` warns. This keeps **one** default per name. The circuit side's rule for the same case is pinned first (brief 51 §1), and any difference is documented | 51 |

---

## 4. What is deferred, and why

- **The OpenCASCADE geometry worker, booleans, fillets, STEP import** (§1b). The owner has decided these
  come in a later series (F4b), as a capability the user installs. Nothing in this series needs them.
- **Edge mode** (D11).
- **Sweeping a profile along a polyline.** The bond wire (brief 50) is the one sweep this series needs, and
  it reuses §6.6's resolved rings. A general sweep is a later brief.
- **A parametric sweep over a `.c3d` dimension.** Brief 51 makes dimensions expressions. Running a sweep
  over them is the `parametric_sweep` machinery's job and a later brief.
- **A headless picture of the 3D view itself.** `render` of a `.c3d` gives the sections brief 5 already
  draws (brief 42). A GPU picture needs a headless GPU. It was deferred in series 2 and stays deferred.
- **F3 (thermal), FU, the container and remote locations**, as before.

---

## 5. Where the code goes

```
src/Design/Cells/CellFolder.cs        ViewType.ThreeD, "3d", ".c3d" (41)
src/Design/ThreeD/                    NEW, no UI
    C3dDocument.cs                    the mutable working model (41)
    C3dPersistence.cs                 read/write, omit-at-default, round-trip (41)
    C3dPlacement.cs                   origin + ordered rotations + mirror; exact at 90° (41)
    C3dElaborator.cs                  document -> Em3dProblem + provenance (42)
    C3dProvenance.cs                  problem object/face -> document object/face, instance path (42)
    C3dHierarchy.cs                   instance resolution, cycle detection, child cache (42, 48)
    Kernel/                           the managed polyhedral kernel (47)
src/Design/Layout/Em3d/
    Em3dLayoutSolids.cs               NEW, split out of Em3dGenerator: a layout's solids only (42)
src/Engine/Em3d/Em3dProblem.cs        Em3dPolyhedron, planar sheets, face boundaries (42, 49)
src/Design/Em3d/{GmshGeoWriter,CsxcadWriter}.cs   lower them (42, 49)
src/Render/Scene3D/
    Scene3DBuilder.cs                 face IDs, per-object placement, instancing (43, 48)
    Edit/                             NEW: pick lists along a ray, snap query, drawing plane and grid,
                                      gizmo geometry; no GPU, no Avalonia (43-46)
src/Ui/Viewer3D/                      the pane gains a 64-bit pick target and per-batch transforms (43)
src/Ui/ThreeD/                        NEW: the editor view model, tools, context menus (43-50)
src/Cli/                              .c3d in DocumentKinds, check, explain, find, render, em (41, 42)
examples/3D Package/                  NEW (52)
```

---

## 6. The series' own gate

**`R-em3d40-1`** Every brief in §2 exists and every link resolves.

**`R-em3d40-2`** *Done 2026-09-26, when the owner decided D1–D4:* `em-3d.md` is at **rev 6**. It records
F4's split into F4a (this series) and F4b (OpenCASCADE, user-installed, later), Open 3 closed by `.c3d`,
§6.3's construction history narrowed to §1f's flat object list, and §8.6's drag gate restated for the
managed kernel (§1b). Whoever builds a brief that changes one of those keeps the note in step.

**`R-em3d40-3` The owner's walk-through (owner check).** In the Debug build, with no terminal at any step:
1. New cell ▸ 3D view;
2. draw a substrate box, a trace sheet and a ground;
3. place the example MMIC's layout as an instance;
4. draw a bond wire from a die pad to the trace;
5. add two ports;
6. Simulate;
7. read S21 in the Data Display, and see |E| on the drawn model.

The owner records the elapsed time of each step, and anything that did not feel immediate.

---

## 7. Scope for the series

- **No change to any existing answer.** No planar result, no existing 3D golden, no shipped example's
  numbers (§1h).
- **No per-primitive edit verbs.** A `.c3d` is authored headlessly by writing it, and the reference page
  describes every field (CLAUDE.md, *the format is the contract*).
- **No native dependency** (D2).
- **No timing tests.** Counters only.
- **Commercial names stay out of the repository**, including the names of other tools' 3D views (§1c says
  what they have in common, not who they are).
- **On completion of each brief, findings go in the relevant `RESOLVED.md`**, never `CLAUDE.md`. Doc
  sources are edited, and DocGen is not run per brief.
- **Keep EM runs short.** A gate that needs a solve uses the smallest case that tests the claim. Anything
  over ~5 s is `Category=Benchmark`.
