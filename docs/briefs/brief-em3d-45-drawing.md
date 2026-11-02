# Brief 45 — drawing: the plane, the grid and the tools

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d45-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.2 point 5 (keyboard first, no modal dialog
mid-gesture); [`layout-view.md`](../design/layout-view.md) §1.5 (the snap grid), §6.1 (tools)
**Area:** `src/Render/Scene3D/Edit/` (`DrawingPlane`, `PlaneGrid`, rubber-band geometry),
`src/Ui/Viewer3D/` (the grid pass), `src/Ui/ThreeD/Tools/` (one class per tool)
**Depends on:** 44 · **Blocks:** 47 (extrude), 50
**Owner decision D6:** a cylinder tool is included.

---

## 0. What this brief delivers

- A **drawing plane**: XY, YZ or XZ, at an **offset** along its normal. It is chosen from the toolbar, the
  `3D` menu, or by picking a face. A **subtle grid** is drawn on it, behind the objects.
- **Tools:**
  - **Box**: three clicks — corner, opposite corner, height;
  - **Sheet**: a rectangle, two clicks;
  - **Polygon**: a closed polygon sheet, click per vertex;
  - **Polyline**: open or closed, click per vertex, construction geometry;
  - **Cylinder**: centre, radius, height.
- **Typed values mid-gesture.** Every dimension can be typed while drawing: width, depth, height, radius,
  or a vertex's offset.
- **Extrude**: a closed polyline or polygon sheet becomes a prism; an open polyline becomes a ribbon
  sheet.

Every click snaps (brief 44). Every new object gets the **current material** (a toolbar combo, from the
technology's materials) and an automatic name (`box1`, `sheet2`, …), unique and renamable.

---

## 1. `R-em3d45-1` — the drawing plane

**`R-em3d45-1a`** The plane is (axis, offset in DBU). XY at z = offset, YZ at x = offset, XZ at
y = offset. It is shown on the toolbar as three exclusive buttons plus an offset field in the display
unit. It is **editor state**, saved per document in the workspace's window state, not in the `.c3d`
(it is not design).

**`R-em3d45-1b` Setting the offset from the model:**
- **Plane from face**: in Face mode, the context menu, or **Ctrl/Cmd-click** a face with a drawing tool
  armed. (Alt/Option is taken: holding it suspends geometry snap, brief 44 §5.) The plane takes the face's axis and offset if the face is axis-aligned. A tilted face is
  refused, naming its normal, because the planes are XY, YZ and XZ only. A plane in any orientation is a
  later brief.
- **Offset from a snap**: **Ctrl/Cmd+Shift-click** a snapped point to move the plane's offset through
  it, keeping the axis.

**`R-em3d45-1c` The plane follows the view only if asked.** Changing the camera never changes the plane.
Drawing on a plane seen edge-on is refused with a status message *"the XY plane is edge-on; orbit or
choose another plane"*, rather than producing points at infinity.

## 2. `R-em3d45-2` — the grid

**`R-em3d45-2a` Subtle:**
- thin lines, low contrast, **minor and major** lines. Major lines are every 10 minor lines, or every 5
  in mil and inch, matching the layout grid's cadence;
- spacing adapts with zoom, in the display unit's steps, so the screen spacing of minor lines stays in a
  band of roughly 8–40 px;
- lines fade with distance from the view's focus and with obliqueness, so an edge-on plane does not
  become a grey slab.

**`R-em3d45-2b` Behind the objects.** It is drawn after the opaque objects **with depth test on and depth
write off**, so solids hide it and it hides nothing. Where it passes through translucent dielectrics it
shows faintly. Axis lines through the origin are drawn slightly stronger, in the axis indicator's colours.

**`R-em3d45-2c`** A shader over one quad sized to the view, not a vertex buffer of lines. An orbit uploads
nothing (brief 28 gate 3's counter still holds).

**`R-em3d45-2d`** The grid snap spacing is the document's `SnapDbu`, which is not necessarily the drawn
minor spacing. The status bar shows both when they differ. Changing `SnapDbu` never moves existing
geometry (layout-view §1.5).

## 3. `R-em3d45-3` — the tools

A tool is armed from the toolbar, from *3D ▸ Draw*, or from the keyboard.

**Single letters are nearly used up:**
- O, F, V, B, P, C, A and 1–7 are taken (briefs 28 and 43);
- X, Y and Z are brief 46's axis constraints during a move.

So the keyboard route is **one key and a popup**. **Shift+A** opens a *Draw* menu at the cursor, and
inside it one letter arms each tool:

| Tool | Letter in the popup | Icon (verify every kind name in 3.0.2) |
|---|---|---|
| Box | **B** | `CubeUnfolded` or another; `CubeOutline` is Object mode's |
| Sheet | **S** | `VectorRectangle` |
| Polygon | **G** | `VectorPolygon` |
| Polyline | **L** | `VectorPolyline` |
| Cylinder | **Y** | there is no `Cylinder` kind in 3.0.2; choose one and name it in the completion note |

The owner confirms this at the owner check. Arming a tool keeps the selection mode, and **Esc** disarms.

**`R-em3d45-3a` Box — three clicks:**
1. **Corner A** on the plane (snapped).
2. **Corner B** on the plane (snapped). A rubber-band rectangle is drawn on the plane.
3. **Height.** Moving the mouse sets the height along the plane's normal. The height is the parameter of
   the point on the normal line through corner B that is **closest to the cursor's ray** (closest points
   of two lines). If the cursor is near a feature, the snap point is projected onto that line, so a box
   rises to exactly the height of a neighbouring pad. Negative heights are allowed. A third click ends the
   box.

The box is stored as a `Box` (`Min` plus a positive `Size`, brief 41), whatever the plane. Each typed
or snapped dimension lands in **one** `Size` component, which is what lets brief 51 bind it to an
expression.

**`R-em3d45-3b` Sheet** is steps 1–2 of the box, stored as a `Sheet` with `Plane`, `Offset`, a
rectangular `Outline`, and `ThicknessUm` from the material's default thickness if the technology states
one. Otherwise it defaults to 0, and the elaboration warns (brief 42 §3f) when a conductor sheet has no
thickness.

**`R-em3d45-3c` Polygon** is click per vertex. It closes on a click on the first vertex, **Enter**, or a
double-click. It is stored as a `Sheet` with that outline. A self-intersecting outline is refused at
close, with the crossing highlighted.

**`R-em3d45-3d` Polyline** is click per vertex, and **Enter** or a double-click ends it. A click on the
first vertex closes it. It is stored as a `Polyline` (construction geometry, brief 41 §2e). Its vertices
may be anywhere in 3D, because they snap to features off the plane.

**The format keeps `Plane` + 2D points** (brief 41). So a polyline whose snapped points leave the plane
is stored in 3D form: brief 41's `Polyline` gets an alternative `Points3` field. It is written only when
needed, so a planar polyline is unchanged. Record the decision in the reference page.

**`R-em3d45-3e` Cylinder:**
1. the centre on the plane;
2. the radius (a click, or typed);
3. the height (as a box's).

It is stored as a `Cylinder` whose axis is the plane's normal.

## 4. `R-em3d45-4` — typed values mid-gesture (§8.2 point 5)

- While a gesture is in progress, **typing a digit** opens an inline field at the cursor, in the 2D
  overlay, never a dialog. It is prefilled with the current value of the dimension the next click would
  fix: width, then depth, then height; radius; or a polyline segment's length.
- **Tab** moves to the next dimension of the same step, for example width → depth on a box's step 2.
- **Enter** accepts the step as if clicked.
- **Esc** in the field returns to the mouse.
- **Units:** a bare number is the display unit. A suffix (`25um`, `10mil`) is honoured through the
  layout's unit parser. **An unparseable entry stays in the field, red.** It never becomes zero.
- **Values are exact.** A typed `10mil` in a document at 1000 DBU/µm is 254,000 DBU exactly (decimal
  arithmetic, as `LayoutUnits`).
- **Tab or `=` also opens the field**, empty, so an entry can start with a letter. Letters cannot open it
  by themselves, because X, Y and Z are constraint keys mid-gesture (brief 46).

**`R-em3d45-4a` Built for expressions, which arrive with brief 51.** The owner wants a dimension typed as
an expression (`w`, `2*w`), with an undefined name defined on the spot as a VAR stored in the `.c3d`.
**That does not have to work at this brief's gate**, but this brief must not make it hard:
- the field's parser is **one function** returning either an exact DBU value or "this is an expression".
  At this brief the second answer is shown inline (*"expressions arrive with the 3D expressions
  feature"*), in the field's red state, and nothing is written;
- every typed dimension lands in **exactly one stored field**: a box's `Size` component, a prism's
  `Height`, a cylinder's `Radius` or `Length`, a rectangle's `Size` (brief 41's sizes, not second
  corners). That field is what brief 51 binds the expression to;
- the field sits in the overlay with room beneath it, for brief 51's inline *Define* strip.

## 5. `R-em3d45-5` — extrude

*Extrude* is in the context menu of a closed polyline, a polygon sheet or a rectangle sheet, and in
*3D ▸ Modify*:
- the distance comes from a drag along the normal (brief 47's move-normal gesture, reused) or is typed;
- the result is a **Prism** (brief 41) on the source's plane, with the distance as its `Height`;
- **the source is kept** or consumed by a toggle in the gesture, default **consume**, as SketchUp-style
  push/pull does and because a sheet left inside its own prism would overlap it. The status bar says
  which;
- an **open** polyline extrudes to a **ribbon sheet**: a polygon sheet in the plane that contains the
  polyline and the normal. That is only possible when the polyline is straight in projection; otherwise
  it is a polyhedron of quads, which is a sheet with more than one plane. **This series refuses it**, with
  the reason. A folded ribbon is a later brief.

## 6. Gate

`tests/Ui.Tests/ThreeD/Draw*` (the tools driven through their view models with synthetic clicks).

1. **Box in three clicks.** Clicks at snapped corners give a `Box` whose `Min` and `Min + Size` equal the snapped
   DBU points exactly. The height snapped to a neighbour's top face equals that top face's z exactly.
2. **Every plane.** The same three clicks on XY, YZ and XZ give boxes that are permutations of each other.
3. **Typed values.** `Tab`/`Enter` sequences give exact DBU, and a bad entry changes nothing.
4. **Edge-on refusal** at a camera looking along the plane.
5. **Polygon self-intersection** is refused, and the document is unchanged.
6. **Extrude.** A closed polyline becomes a prism whose elaborated volume equals area × distance (1e-12
   relative), and it consumes the polyline.
7. **Grid.** 100 orbits upload 0 bytes. The minor spacing stays in its band across a zoom sweep (counter
   and computed spacing, not pixels).
8. **Undo.** Each tool's result is one undo entry. A cancelled gesture adds none.

## 7. Owner check (pixels not seen)

In the **Debug** build:
- draw a box on each plane;
- snap a box's height to a neighbour;
- type `10mil` mid-gesture;
- draw a polygon and extrude it;
- judge the grid: is it subtle, visible when needed, and not a grey slab edge-on?
- confirm or change the Shift+A popup and its letters (§3).

## 8. Scope

- No drawing plane at an arbitrary orientation.
- No arcs in outlines. Layout's curved primitives flatten before 3D anyway.
- No fillets, no booleans (overview §1b).
