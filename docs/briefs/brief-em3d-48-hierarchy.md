# Brief 48 — hierarchy in the 3D editor

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d48-n` ·
**Design note:** [`layout-view.md`](../design/layout-view.md) §7 (layout hierarchy — the precedent),
[`workspace-and-project-tree.md`](../design/workspace-and-project-tree.md) §4 (the cell reference model),
§5C (external references); briefs `L3a` (instances and arrays), `L3b` (navigation), `L3c` (flatten and
group), `hier2`/`hier3` (the document navigation stack, push-in / pop-out); overview §1i, §1k
**Area:** `src/Design/ThreeD/C3dHierarchy.cs`, `src/Render/Scene3D/` (instanced drawing),
`src/Ui/Viewer3D/` (per-instance transforms), `src/Ui/ThreeD/Hierarchy/`, the Project Tree and
`CellUsageScanner`
**Depends on:** 43 (44 for snapping into instances, 46 for moving them) · **Blocks:** 50, 52

---

## 0. What this brief delivers

Brief 42 made instances **elaborate**. This brief makes them something the user **works with**:

- **Place Cell Instance** into a `.c3d` — the existing *Design ▸ Place Cell Instance…* command — choosing
  the **3D** or **Layout** view of the cell. Placement is on the drawing plane, with snapping.
- **Swap View**: an instance's 3D and Layout views exchange, in place.
- **3D arrays** (counts and pitch on X, Y and Z), mirror and rotate: brief 46's operations, acting on
  placements.
- **Instanced drawing**: a child is tessellated once, and every placement or array element is a
  transform.
- **Push In / Pop Out**: edit a child `.c3d` in context. A child `.clay` opens in the layout editor.
- **Flatten** (one level or all), and **Group into Cell**.
- **Cycle detection at edit time.**
- **New 3D View from Layout**: a cell's first `.c3d`, holding one instance of its own layout.

The overview's §1k rule applies everywhere: **a child's ports, setups, face boundaries and `.clay` port
shapes are not drawn and not elaborated.** The parent defines its own.

---

## 1. `R-em3d48-1` — placing an instance

**`R-em3d48-1a`** *Design ▸ Place Cell Instance…* opens the existing cell picker. When the active
document is a `.c3d`, the picker:
- adds a **View** choice: *3D* or *Layout*. A cell with only one of the two offers only that one. A cell
  with neither is listed disabled, with the reason;
- keeps its **cross-workspace** listing (`ws://` aliases, §5C). The technology gate is **not** applied
  (overview §1i), and the picker says why in its footer, so the user knows a GaAs die in a ceramic package
  is intended, not a mistake.

**`R-em3d48-1b` Placement.** The child's elaborated bounds follow the cursor on the drawing plane, drawn as
an outline, not the full child, until the click. The placement point is the child's **origin**: (0,0,0)
of its `.c3d`, or for a layout the layout origin **at its stackup's bottom** (overview §1i). The origin
snaps (brief 44). **Ctrl/Cmd** while placing uses the child's bounding-box **bottom-centre** as the
handle instead, which is how a die is dropped onto an attach. The status bar says which handle is in use.

**`R-em3d48-1c` Names:** `U1`, `U2`, … unique, renamable, validated (`airbox` reserved).

## 2. `R-em3d48-2` — swap view (the owner's request)

*Swap View* on an instance changes `View` between `ThreeD` and `Layout`. It keeps the placement and the
array. Afterwards:
- the child re-elaborates from the other view, through the child cache;
- **the parent's ports and boundaries that name objects inside the instance** (`U1/…`, brief 42 §3c) are
  re-checked. A name that no longer exists in the new view is **reported in one message listing all of
  them**, and the swap still happens. A layout's generated conductor names and a `.c3d`'s drawn names are
  not the same, and pretending otherwise would be the silent kind of failure;
- **one undo entry** restores both the view and nothing else, since the ports were not changed.

A cell whose 3D view is missing cannot swap to it, and the menu item is disabled with the reason.

## 3. `R-em3d48-3` — drawing instances fast

**`R-em3d48-3a`** A child (cell, view, file stamp, technology stamp) is **tessellated once** into its own
batches. Every placement and every array element draws those batches with its own transform, as GPU
instanced draws or as repeated draws with a per-draw transform, whichever the backend does well. This is
brief 43's per-batch transform, generalised to a per-draw transform list.

**An orbit uploads nothing** (brief 28 gate 3), and adding an array element uploads **one transform**.

**`R-em3d48-3b` Picking an array element.** The pick target's 64 bits must identify **which element** as
well as which object and face. Extend the pick encoding. The reference implementation packs:
- the instance draw index in the top bits of the object word;
- the child's object and face in the rest.

Document the limits: elements per array, objects per child. **A layout child with more objects than the
encoding holds is refused at placement**, naming the numbers, not truncated.

**`R-em3d48-3c` Level of detail for big children.** A `.clay` child can be a full MMIC with tens of
thousands of shapes. Its elaboration is cached and its tessellation is shared, so an array of them is one
child's cost. The frame cost is still the child's triangle count × elements. When that exceeds a
documented triangle budget, elements beyond the budget draw as their **bounding boxes** until the camera
nears them, and the status bar says so.

This is the one place a picture shows less than the solver gets, so it is **stated on screen**, never
silent. The layout editor's LOD tiers are the precedent. The owner can set the budget.

## 4. `R-em3d48-4` — Push In, Pop Out, and editing in context

**`R-em3d48-4a`** *Push Into Cell* (context menu on an instance, or the key the layout editor uses, which
the implementer should check and reuse):
- a **3D child** opens **in the same editor, in context**. The parent draws dimmed and unpickable around
  it, through the document navigation stack (`hier2`/`hier3`), with the breadcrumb (`hier4`);
- a **Layout child** opens its `.clay` in the layout editor (a layout cannot be edited in 3D), and the
  status bar says so.

*Pop Out* returns. A saved child re-elaborates in every open parent (brief 43 §1d).

**`R-em3d48-4b` In-context coordinates.** While pushed in, the drawing plane, the snap coordinates and the
Properties panel read in the **child's** frame and display unit, and the status bar names the frame.
Snapping reaches parent geometry, transformed into the child's frame, so a child's pad can be drawn to
meet a parent's lead.

**`R-em3d48-4c` Save** follows the hierarchy-save behaviour the schematic and layout have (`hier3`, the
`HierarchySaveTests` pattern): a dirty child is saved or discarded on Pop Out, never lost.

## 5. `R-em3d48-5` — Flatten and Group into Cell

L3c's rules, carried to 3D:
- **Flatten (one level)** replaces an instance with its child's objects, transformed and renamed
  `<instance>_<object>`. An array becomes N plain copies.

  Flattening a **Layout** instance writes the **elaborated solids** as objects: prisms, sheets,
  cylinders, and wires as polyhedra. It asks first, stating the object count, because the layout's
  link is then gone (L3c R-L3c-1a: *the command states its outcome before acting*).
- **Flatten (all levels)** computes the resulting object count **before** mutating anything and states it
  (R-L3c-4).
- **Group into Cell**: a new cell folder through `CellFolder.CreateCellFolder` with a `3d/` holding the
  selected objects, which are replaced by one instance. **The geometry must not move** (R-L3c-5). Undo
  removes the instance and restores the objects, and does **not** delete the created cell folder
  (R-L3c-6).

## 6. `R-em3d48-6` — cycles, stale children, references

**`R-em3d48-6a` Edit-time cycle detection.** Placing an instance that would reach the current cell is
refused **at the pick**, with the path, before anything is written (layout-view §7: *enforced at edit
time, not discovered at render time*). Brief 42's elaboration check remains as the backstop.

**`R-em3d48-6b` Three states per instance,** as layout's `CellLayoutResolver` has them:
- **resolved**;
- **not found**: a dashed box with the cell name, at the last known bounds or a unit cube;
- **stale and reloading**: the last good tessellation is drawn while the new one builds.

External instances carry the `[alias]` tag as chrome (layout-view §7.1: *marking is chrome, never the
geometry*).

**`R-em3d48-6c`** `CellUsageScanner`, rename, remove and archive already follow `.c3d` references (brief
41). This brief adds the tests that exercise them with **real placed instances** of both views.

## 7. `R-em3d48-7` — New 3D View from Layout

In the cell context menu and in the layout editor's *Design* menu: *New 3D View from Layout* writes a
`.c3d` with:
- one instance `U1` of **this cell's own layout**, at the origin;
- the layout's `DisplayUnit`;
- the layout's technology as `TechRef`, for any materials the user draws with;
- a copy of every 3D `.cem` setup that names this layout, as embedded setups (brief 42 §5a), **with their
  ports translated** to parent-level ports (brief 49 §2) where each layout port maps to one rectangle.
  Where one does not, the port is left out and listed.

Instancing one's own cell's layout inside one's own cell's 3D view is **not a cycle**, because the views
differ. §6a's check is by (cell, view), not by cell.

This is the shortest route from everything series 1 and 2 built to the editor: an existing 3D setup
becomes an editable 3D view in one command.

## 8. Gate

`tests/Ui.Tests/ThreeD/Hierarchy*`.

1. **Tessellate once.** A 20 × 20 array of a layout child: the child is elaborated once and tessellated
   once. Adding a column uploads 20 transforms and no geometry (counters).
2. **Pick an element.** A synthetic pick in element [7,3] returns that element and the right child face,
   and the CPU and software ID paths agree.
3. **Encoding limit.** A child exceeding the documented object limit is refused at placement with the
   numbers.
4. **Swap view.** A port naming `U1/trace` reports it missing after a swap to a view without it, and the
   swap still happens. Undo restores it.
5. **Cycle at edit time.** Placing A in B, where B already contains A, is refused and nothing is written.
   Placing a cell's own layout in its own 3D view is allowed.
6. **Flatten.** Flatten, then Group into Cell, round-trips the elaborated problem (equal solids up to
   names and order, asserted).
7. **New 3D View from Layout** on the Package example: its elaborated problem equals brief 42's
   equivalence oracle.
8. **Rename/Remove Cell** with placed instances of both views rewrites and counts them.
9. **LOD is said.** Above the budget, the status text names the element count drawn as boxes (view-model
   assertion).

## 9. Owner check (pixels not seen)

In the **Debug** build:
- place the example MMIC's layout in a package `.c3d` in mil, and drop it on the attach with the
  bottom-centre handle;
- swap it to a 3D view and back;
- array it 4 × 1; push in, edit, pop out;
- group two leads into a cell;
- orbit a 20 × 20 array and judge the frame rate;
- place a cell from another workspace with a different technology, and see that no gate fires.

## 10. Scope

- **Magnification is not supported.** A physical instance has none, and a layout child's own internal
  magnification is flattened by the layout's own flattener before it arrives.
- No per-instance parameter overrides (brief 51).
- A layout child is **never edited in 3D**. Push In opens the layout editor.
