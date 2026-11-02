# Brief 43 — the 3D editor: window, modes and selection

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d43-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.2 (what makes it feel fast), §8.4 (three loops);
[`layout-view.md`](../design/layout-view.md) §6.2 (overlap cycling — the 2D precedent for **B**)
**Area:** `src/Ui/ThreeD/` (new), `src/Ui/Viewer3D/` (pick target, per-batch transforms, highlight state),
`src/Render/Scene3D/` (`Scene3DBuilder` face IDs, `Edit/RayHits.cs`), the Dock document factory, the
Project Tree's open action, `src/Ui/Views/WorkspaceWindow.axaml` (the `3D` menu)
**Depends on:** 42 · **Blocks:** 44–50
**Owner decisions:** D3 (keys), D9 (the menu)

---

## 0. What this brief delivers

Double-clicking a `.c3d` opens it in a **3D editor** document tab. The editor is brief 28's pane, drawing
the elaboration from brief 42. In it:

- **Three selection modes:**
  - **Object (O)**: whole solids, sheets, polylines and instances;
  - **Face (F)**: one face of one solid or sheet;
  - **Vertex (V)**: one vertex.

  Each has a toolbar toggle button, exclusive, with a Material icon.
- **Hover** highlights what a click would select. **Click** selects it. **Shift-click** adds to or removes
  from the selection.
- **B** steps the selection to the next thing *behind* it along the line of sight, and **Shift+B** steps
  back toward the viewer.
- **Right-click** opens that mode's context menu. This brief builds the menu's frame and the operations
  that need no geometry: *Rename*, *Material ▸*, *Hide*, *Isolate*, *Show All*, *Delete*, *Properties*,
  *Select Owning Object*. Briefs 46, 47 and 49 fill in the rest.
- The **object tree**, the **Properties** panel, **undo and redo**, **save** and the **dirty mark**.
- The **`3D` menu**, and the read-only viewer adopting the same keys.

---

## 1. `R-em3d43-1` — the document window

**`R-em3d43-1a`** A new Dock document kind: *3D view*. It holds a `C3dDocument` and reuses
`Viewer3DPane` and its session, whose device and buffers outlive a float or re-dock (R-em3d28-1d). **One
pane implementation serves the editor and the read-only viewer.** The editor is the viewer with a
document and tools. It is not a second 3D control.

**`R-em3d43-1b` Scene from the elaboration** (the overview's rule). The scene is built from
`C3dElaboration` through `Scene3DBuilder`, under brief 28's generation numbers. An edit updates the
document, re-elaborates the changed objects (brief 42 §4), and rebuilds only their batches. Gate 5
counts it.

**`R-em3d43-1c` Save, dirty, undo:**
- **Save** writes through `C3dPersistence`. A tab shows the dirty mark as every other editor does.
- **Undo and redo:** one entry per user action. A drag is **one** entry, not one per mouse move. A typed
  value is one entry. An entry stores **only the objects it changed**, before and after, not a snapshot of
  the document, so a 10,000-object document does not copy itself per edit.
- *Edit ▸ Undo/Redo* and **Ctrl/Cmd+Z**, **Ctrl/Cmd+Shift+Z** reach it, as they reach the layout editor.

**`R-em3d43-1d` External change.** A child cell's `.c3d` or `.clay` changing on disk re-elaborates that
instance only (brief 42's child cache key), with no prompt. The open document changing on disk while it
is dirty asks, as the layout editor does.

## 2. `R-em3d43-2` — modes and keys (D3)

**`R-em3d43-2a`** In every 3D pane, editor and read-only viewer alike:

| Key | Does |
|---|---|
| **O** / **F** / **V** | Object / Face / Vertex mode |
| **B** / **Shift+B** | next behind / next in front (§4) |
| **Home** | Fit (was F) |
| **P** | toggle perspective and orthographic (was P = perspective, O = orthographic) |
| **1–7** | standard views, unchanged |
| **Esc** | cancel the gesture in progress; with none, clear the selection |
| **Delete**, **Backspace** | delete the selection (editor only) |

Mode keys act **only with no modifier and no gesture in progress**. `Cmd+V` is still paste.

**`R-em3d43-2b` Toolbar.** Three exclusive `ToggleButton`s, O, F and V, with tooltips naming the key.
Icons are from `Material.Icons.Avalonia`, which is already a dependency. The kinds below were checked to
exist in 3.0.2: `CubeOutline` (Object), `VectorSquare` (Face), `VectorPoint` (Vertex). Beside them:
- **Fit** (`FitToPageOutline`, or whichever kind the implementer verifies);
- the projection toggle;
- the existing standard-view buttons;
- **the Unit combobox**: the layout editor's control, not a copy (`LayoutEditorViewModel.AllUnits`,
  lower-case items, `Unit:` label). It sets `DisplayUnit` (owner decision D4). As in the layout editor it
  is a **document preference**: it dirties the document and is saved, adds **no undo entry**, and moves no
  geometry. Every length the editor shows follows it at once: status bar, Properties, typed fields, the
  scale bar, snap readouts and measure labels.

**Verify every kind name against the package before using it.** An unknown kind renders nothing, with no
error.

**`R-em3d43-2c` The read-only viewer** (a `.cem`'s *Show 3D*) takes the same keys and the same three
modes. Selecting there is for **measuring and reading**: the Properties panel shows the face's area and
normal, and the tooltip shows the material. It gets brief 46's **Measure** tool, point to point with
copyable numbers, when that brief lands. It edits nothing. The owner check confirms F no longer fits
there.

## 3. `R-em3d43-3` — picking faces and vertices

**`R-em3d43-3a` Face IDs in the pick pass.** Today the ID pass writes a 32-bit object ID from each
vertex's `Id`. It becomes a **64-bit (object, face) pair**:
- a second `uint` in the vertex (stride 20 → 24);
- an `RG32Uint` target in place of `R32Uint`, on all three backends;
- every face's triangles carry their face index from the tessellation (brief 42 §1c).

**The object ID is unchanged**, so brief 28's and 29's hover and picking behave exactly as before. A test
says so.

**`R-em3d43-3b` Vertex mode picks through the face.** The ID pass finds the face under the cursor; the
nearest of that face's vertices **in screen space** is the candidate. It is highlighted if within the snap
radius (brief 44's constant), and otherwise there is no candidate. This costs one face's vertices, not
the scene's.

**`R-em3d43-3c` Instances in face and vertex mode.** An instance's faces and vertices **highlight and
select**, because measuring from a die's pad is the point. The context menu then offers only what does not
edit the child:
- *Measure*, *Copy face as sheet*, *Select owning instance*;
- *Push into cell to edit* (brief 48).

Editing a child's geometry from the parent is **refused by construction**: the menu does not offer it.

## 4. `R-em3d43-4` — B: the next one behind

**`R-em3d43-4a` The hit list.** On **B**, not on hover, a CPU ray through the cursor collects **every**
hit in the current mode, ordered by depth, ignoring visibility. What it collects per mode:
- **Face:** every face crossed. A face crossed twice (entering and leaving a solid) appears twice, because
  entering and leaving are both faces the user may want;
- **Object:** every object crossed, **once**, at its nearest hit;
- **Vertex:** every vertex within the snap radius of the ray, by depth.

It uses a bounding-volume hierarchy over object bounds, built once per scene generation. The CPU path
already exists as brief 28's `Scene3DPicking.Pick` (nearest only). It becomes `RayHits`, in
`src/Render/Scene3D/Edit/`.

**`R-em3d43-4b` Cycling** is layout's R13, in depth order:
- the list and the cursor point are cached;
- **B** advances and **Shift+B** goes back, wrapping at the ends;
- a cursor move of more than a few pixels, a scene change, or a mode change discards the list.

The first **B** after a click starts from the clicked item's position in the list.

**`R-em3d43-4c` Status readout** (without it, cycling feels like a glitch — layout R13 point 4):
`Face top · Box "lid" · 2 of 5`.

**`R-em3d43-4d` A face behind others must be visible when selected.** The selected face is drawn with its
highlight **on top of** everything: a second pass with depth test off, at reduced opacity. The objects in
front stay opaque, so the user can see both what they selected and what is in front of it.

**`R-em3d43-4e` The context menu acts on the current item.** A right-click with a B-cycled selection acts
on that selection, **even though the cursor is over the front face**. A right-click on something that is
not selected selects it first, as everywhere else.

## 5. `R-em3d43-5` — highlight, drawn without geometry work

A shader state change, never a retessellation (§8.2 point 2). The pane gets:
- a uniform for the hovered (object, face);
- a uniform for the selected set: a small ID list, or a per-object selection bit in a buffer when the list
  outgrows the uniform — document the limit;
- a per-batch transform (identity by default), which brief 46's drag previews and brief 48's instances use.

| Mode | Hover | Selected |
|---|---|---|
| Object | a tint | an outline, via an edge pass |
| Face | a lighter fill on the face only | a stronger fill plus its edges |
| Vertex | a dot | a larger dot |

Vertex dots are drawn for the hovered and selected objects only. The vertices of every object at once are
noise.

The colours come from the application theme (light and dark) as brief 28's do.

## 6. `R-em3d43-6` — the tree, Properties, and the `3D` menu (D9)

**`R-em3d43-6a` Tree.** Brief 28's object tree, fed from the **document**:
- objects in construction order, grouped by kind;
- instances as expandable nodes that show the child's objects, read-only;
- ports and boundaries (brief 49).

Selection syncs both ways. Visibility toggles write `Hidden` (document state, brief 41 §2a), so they
are undoable.

**`R-em3d43-6b` Properties** shows the selection's fields in the document's display unit:
- for an object: kind, material, role, placement, and each dimension;
- for a face: name, area and normal;
- for a vertex: coordinates.

Editing a value is a typed edit: one undo entry, validated. Brief 47 makes vertex coordinates and face
offsets editable here. This brief makes name, material, role and placement editable.

**`R-em3d43-6c` The `3D` menu** is a new top-level menu, always present. Every item is enabled only when a
3D document is active, and says so in its tooltip. This brief adds:
- *Select Mode ▸ Object / Face / Vertex*;
- *View ▸ Fit / Standard Views / Perspective / Clip Plane / Axis Indicator*;
- *Show All*.

Later briefs add *Draw*, *Modify*, *Hierarchy* and *Setups*. Existing commands are reused, not copied
(overview §1m).

## 7. Gate

`tests/Ui.Tests/ThreeD/`, `tests/Ui.Tests/Viewer3D/` (unchanged ones must still pass),
`tests/Firewall.Tests`.

1. **Face pick = CPU face pick.** For fixture scenes, the software ID pass (`IdAtPixel`, extended) and
   `RayHits`' nearest agree on (object, face) at sampled pixels.
2. **Object IDs unchanged.** Brief 28's gate 9 and brief 29's picking tests pass untouched.
3. **B order.** Through a stack of three boxes, B from the top face visits, in order: top of box 1, bottom
   of box 1, top of box 2, and so on, then wraps. Shift+B reverses.
4. **The list resets** on a cursor move beyond the threshold, a mode change or a scene generation
   (counters).
5. **Hover does no geometry work.** 1,000 hover moves: 0 elaborations, 0 tessellations, 0 uploads
   (brief 28 gate 5's counter, extended).
6. **Edit locality.** Renaming one object of 1,000 re-elaborates 1 object and re-uploads only its batch's
   bytes.
7. **Undo.** A sequence of edits undoes and redoes back to byte-identical saved documents at each step.
   One drag is one entry.
8. **Keys.** In both panes, F sets Face mode and Home fits. Assert it on the view model; there is no
   pixel test.
9. **Firewall.** `Edit/` in `src/Render` references no Avalonia and no GPU API.
10. **Display unit is free.** Changing the Unit combobox marks the document dirty, adds 0 undo entries,
    causes 0 elaborations and 0 tessellations, and changes every displayed length (view-model assertion).

## 8. Owner check (pixels not seen from this session)

In the **Debug** build, on the owner's machine:
- open a `.c3d`; switch modes by key and by button;
- hover faces, then click one; press **B** through a stack; right-click the cycled face and see that its
  menu names it;
- Shift-click several objects; undo and redo a rename;
- check that the read-only viewer's F no longer fits;
- "does hover feel immediate" on the Package example with its wires.

## 9. Scope

- No drawing (45), no moves (46, 47), no snapping (44). The vertex highlight in §3b uses the snap radius
  constant only.
- No marquee selection. It is a small follow-on if the owner wants it: select objects whose projected
  bounds are inside a screen rectangle.
