---
title: The Layout Editor
slug: reference/layout-editor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Layout editor
lede: Drawing artwork: the technology, the tools, snapping, and the schematic it belongs to.
keywords: layout, artwork, PCB, copper, drawing, DRC, polygon, trace, via
---

The layout view holds a cell's **physical geometry** — the shapes that get manufactured. It serves two
consumers that pull in slightly different directions, and it is worth knowing which one you are serving
at any moment:

- **Fab handoff.** GDSII to a semiconductor fab, Gerber plus drill to a PCB house, DXF to a mechanical
  flow, `.kicad_pcb` to and from a board tool. Here the layout is a *document*: fidelity, units and
  layer mapping are everything.
- **EM simulation.** A 2.5D method-of-moments solve. Here the layout is a *model*: it needs a substrate
  stackup, a mesh and ports. That half has its own chapters —
  [The MoM engine](mom-engine.html) and [EM Setup](em-setup.html).

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#orientation">Orientation</a></li>
<li><a href="#technology">The technology</a></li>
<li><a href="#tools">Drawing and editing</a></li>
<li><a href="#selection">Selection and vertex editing</a></li>
<li><a href="#hierarchy">Hierarchy, instances and arrays</a></li>
<li><a href="#find">Finding an instance</a></li>
<li><a href="#schematic-flow">Schematic ⇄ layout</a></li>
<li><a href="#geometry-snap">Geometry snap</a></li>
<li><a href="#ruler">The Ruler</a></li>
<li><a href="#interchange">Interchange</a>
  <ol>
  <li><a href="#gdsii">GDSII</a></li>
  <li><a href="#oasis-gdstk">OASIS and GDSII through gdstk</a></li>
  <li><a href="#dxf">DXF</a></li>
  <li><a href="#gerber">Gerber and Excellon</a></li>
  <li><a href="#board">Board files (<code>.kicad_pcb</code>)</a></li>
  </ol>
</li>
<li><a href="#drc">Design-rule checking</a></li>
<li><a href="#toolbar">The toolbar</a></li>
</ol>
</nav>

## Orientation {#orientation}

{{ui: layout-editor}}

A layout is a **view of a cell**, exactly as its schematic, its symbol and its 3D view are. The cell
folder holds `schematic/`, `symbol/`, `layout/` and `3d/` side by side, and a cell need not have all four.
The project tree shows them together; opening one opens a document tab. The other three views have their
own editors and their own pages: the [Schematic Editor](schematic-editor.html) for the electrical
contents, the [Symbol Editor](symbol-editor.html) for the glyph an instance draws, and the
[3D Editor](drawing-in-3d.html) for the solid model.

Coordinates are **integers in database units**, and the display unit you read them in is a separate,
free-to-change setting. That distinction is the single most common source of early confusion and it has
its own page: [Units](units.html).

## The technology {#technology}

A layout resolves against a **technology** — a `.ctech` file that holds everything true of the *process*
rather than of one cell:

- the **layer table**: GDSII layer/datatype pairs, display names, colours, purposes;
- the **substrate stackup**: dielectric and conductor layers with thickness, ε<sub>r</sub>, tanδ, σ, µ<sub>r</sub>;
- the mapping from a drawing layer to a stackup conductor;
- interchange mappings — GDSII layer/datatype ↔ DXF layer name ↔ Gerber file suffix;
- the **default display unit and snap grid** for layouts created against it (a preferred length unit in
  [Settings ▸ General ▸ Units](settings.html#general) is used instead, and is written into a new workspace's copy).
  Changing the unit in the technology editor also moves an open layout that is still showing the old unit; a layout
  whose **Unit** you chose in its own toolbar keeps it;
- the **DRC rules**.

Technologies live in a `tech/` folder at the workspace root, and the workspace records which is the
default. A layout stores a relative reference to one, or leaves it blank to mean "the workspace
default".

A technology's **name** — the one in the technology editor's header and on a layout's technology label —
is stored inside the `.ctech`, so renaming the file does not change it. To rename the technology,
double-click its name in the technology editor's header, type, and press Enter (Esc puts it back).

<div class="callout warn">
<span class="label">"The workspace" means the document's workspace</span>
<p>A blank technology reference resolves against the workspace containing <em>that layout file</em> —
found by walking up from the file to the nearest <code>.cws</code> — not against whichever workspace
happens to be open. This matters because the starter technologies use the same low layer keys
starting at (1,0), so resolving against the wrong one would silently reinterpret every layer in the
design.</p>
</div>

Two starter technologies ship. They differ **only in data**, never in code path:

| | PCB starter | MMIC starter |
|---|---|---|
| Display unit / snap | mil / 1 mil | µm / 5 nm |
| Layers | Top &amp; Bottom Copper, Soldermask ×2, Silk ×2, Drill, Outline | Metal1, Metal2, Via, Resistor, Cap Dielectric, Nitride, Substrate, Backside Via, MIM Metal, MIM Via |
| Stackup | 1.6 mm FR-4, ε<sub>r</sub> 4.4, tanδ 0.02, 1 oz copper, bottom ground | 100 µm GaAs, ε<sub>r</sub> 12.9, tanδ 0.0006, 3 µm plated gold, backside ground |
| Primary interchange | Gerber + Excellon | GDSII |

The MMIC starter also carries a **thin-film (MIM) capacitor module** — a `MIM Metal` plate, a
`MIM Dielectric` under it and a `MIM Via` up to Metal2, with two matching drawing layers. It costs
plain interconnect work nothing, because the capacitor dielectric is
[tied to its plate](stackup.html#mim-tied): a run with no plate artwork in it carries air in the
film's place and extracts exactly as it did before the module existed. Draw a capacitor and the film
is real. See [A thin-film (MIM) capacitor](stackup.html#mim), which also explains why you should not
read a capacitance off a raw run.

A layer's colour is **literal user data**, not a theme role: "M2 is teal" is a fact about the process
that must survive a light/dark switch and must match what you see in your other tools. The editor's own
chrome — background, grid, rulers, selection, handles — does follow the theme.

**A missing technology does not block you.** Layers render from a generated fallback palette keyed by
their (layer, datatype) pair, a warning is posted, and editing proceeds. The one thing it *does* block
is an EM run, which refuses rather than inventing a stackup.

## Layers {#layers}

The **Layers** button beside the toolbar's **Layer:** box — or **L**, or **View ▸ Layers List** — opens
a list of the layout's drawing layers at the right of the canvas. The same three close it again, as does
the **›** button at its top left, giving the canvas the whole width. The list shows the technology's
layers, then any layer this layout draws on that the technology does not define.

| Column | What it does |
|---|---|
| **Vis** | Shows or hides the layer in this view. |
| **Sel** | Whether a click, a marquee, Select All or a snap grab can pick a shape on the layer. Unavailable while the layer is hidden, because a hidden layer is never picked. |
| Swatch and name | Click the name to draw on that layer — the same as choosing it in the **Layer:** box, and the two stay in step. |

Drag the list's left edge to make it wider or narrower; double-click the edge to return to the default
width. Type in the box beside the two column buttons to show only the layers whose name contains that text; the count
beside it reads, for example, **17 of 61** while it is narrowing the list. The two buttons above the
**Vis** and **Sel** columns turn that column on or off for every layer **listed** — with a filter typed,
only the layers it shows, so "hide every pin layer" is a filter and one click. **Page Up** and **Page Down**
page through the list, whether you are in the box, in the list, or on the canvas while the list is open.

**Select on the current layer only** (the target button in the list's header) lets a click pick only
shapes on the drawing layer, whatever the **Sel** column says. It is the quick way to work on a trace
that lies on a ground plane: choose the trace's layer, turn it on, and the plane can no longer be
picked. **Show all** makes every layer visible and selectable again.

**Click again to select the next shape underneath.** A second click at the same spot moves to the next
overlapping shape — see [Selection](#selection).

**These choices are yours, not the design's.** They are not written to the `.clay` or the `.ctech`,
changing them never marks anything unsaved, and they never affect an export, a design-rule check or a
simulation. circuitRF keeps them, and the list's width, per layout in the workspace's `.cwsuser` file, so each layout reopens
as you left it. A layout opened without a workspace keeps them until circuitRF closes.

## Drawing and editing {#tools}

**Drawing tools:** Select · Rect · RoundedRect · Circle · Polygon · Curve · Path · Label · Port ·
Instance-place · Array · Ruler.

<a id="ports"></a>**Ports.** A port is drawn as a bar across the conductor edge it sits on, an arrow arriving at the
bar in the direction current enters, and its name. The bar is as wide as the port's 3D sheet will be: the
conductor end, or on an edge longer than 4 × the height to its return, twice that height centred on the port
(see [How wide a lumped port's sheet is](em-3d.html#port-width)). The port's **Width** in the Properties panel
sets it; blank is that rule, which the field's grey text shows. The name is drawn at a fixed size on screen,
beside the arrow on its outer side, at every zoom. Where two names would overlap, the later port's name moves
further out along its own direction until it is clear, with a thin line back to its port when it has moved more
than its own height. The label's stored height is unchanged and is still what a GDSII or DXF export writes.

Curves, circles and rounded rectangles are **first-class primitives** — they are stored as curves, not
as pre-flattened polygons — and are flattened on demand, always with an explicit tolerance prompt, and
automatically on GDSII export.

**Edit operations:** boolean **Union** (grouped per layer — one result per distinct layer in the
selection), **Intersect / Difference / XOR** (which need a same-layer pair to enable), **Size**
(grow or shrink by a signed offset), **Scale** (a numeric factor or a target size, plus bounding-box
handles: corner for uniform, side for one axis), **Remove All Holes** (right-click a polygon: it keeps
its outline and loses every hole; a selected polygon takes the rest of the selection with it),
**Clip** and **Cut Out** (and their **All** forms),
**Align and distribute**, **Move to layer**,
**Set net**, **Flatten to Polygon…**, **Flatten Hierarchy** and **Group into cell**.

<div class="callout note">
<span class="label">Clip and Cut Out are not Intersect</span>
<p><strong>Intersect</strong> is the region shared by <em>all</em> the selected shapes, folded across the
whole selection — so a selection holding any two shapes that do not overlap intersects to nothing. That
is the right answer to the question it asks, and it is almost never the question you meant when you drew
a rectangle over a region of interest.</p>
<p><strong>Clip</strong> and <strong>Cut Out</strong> ask the other one. Draw the region, <em>select the
shapes you want clipped</em>, then <em>right-click the region</em> and choose <strong>Clip</strong>
(keep what is inside it) or <strong>Cut Out</strong> (keep what is outside). Both name the shape they
found under your click in their tooltip, so you can check your aim before you commit to it.
Every selected shape is clipped on its own and keeps its own identity, layer and net, so the operands may
sit on any mix of layers. The right-clicked shape is the stencil, not an operand: it is never clipped
against itself and it survives the operation, so the same rectangle can clip a second layer straight
afterwards. Nothing you did not select is touched, on any layer.</p>
<p><strong>Clip All</strong> and <strong>Cut Out All</strong> sit directly below them and skip the
selecting: they use every shape in the cell, whatever is selected. That is usually what you want when
you have imported a board and the answer to "which shapes?" is "all of them" — draw the region,
right-click it, done. Their tooltip gives the shape count, so you can see the size of what you are about
to do before you do it. They obey the layer list exactly as <kbd>Ctrl</kbd>+<kbd>A</kbd> does: a layer
you have hidden, or switched selection off for, is left alone — and the Messages panel says how many
shapes that was, along with any bitmaps and placed instances, which are not geometry and are never
clipped. All four are a single undo.</p>
</div>

### Crop to Rectangle {#crop}

**Crop to Rectangle…** (right-click menu) cuts the whole cell down to one box, on **every** layer, in one step.
Choose it, then drag the box; <kbd>Esc</kbd> cancels. It is the command for keeping one corner of an imported
board.

- Every shape is cut to the box, including those on layers you have hidden or switched selection off for. A plane
  keeps its holes, and a shape cut at the edge keeps its layer and net.
- Vias, drills and labels are kept when their point is inside the box, and removed otherwise.
- A placed instance is kept when its origin is inside the box, and removed otherwise. It is never cut.
- The board outline is redrawn as the old outline's board intersected with the box. It keeps its line width, so
  the 3D view's board and air box match the crop.
- Bitmaps are left as they are.

Before anything changes, a confirmation lists each layer, marked when it is hidden or locked, with how many
shapes it keeps, cuts at the edge and removes, then the instance counts. **Crop** applies it as a single undo.

**Crop to New Cell…**, beside it on the right-click menu, makes the same cut as a copy. Drag the box, name the
cell, and the same confirmation appears with the cell's name in it. **Create** writes the cropped copy as a new
cell beside this one and opens it. This board is not changed and nothing is placed on it. Bitmaps are not copied.
Use it when the corner is to become a cell of its own, for example to set it up for EM.

Group into Cell takes only what is selected, and a selection never reaches a hidden or locked layer. To group
after a crop, show every layer and make each one selectable first. Group into Cell's confirmation names any plane
it would leave behind, and **Include them** adds what it would leave out to the group.

### Cut Clearances into Planes {#cut-clearances}

A placed [TESTPAD](components.html#testpad) declares a round cut-out, `ClearD` across, on each conductor its
`ClearLayers` names. A ground pour cuts it on its own; a plane that is drawn artwork, as on an imported board, does
not. Right-click the placed pad and choose **Cut Clearances into Planes**: the disc is cut out of every drawn shape on
each listed layer, centred on the pad, as one undo step — the same cut **Cut Out** makes. Messages lists each layer and
what was cut there, or that nothing was drawn on it.

A cut only removes metal. After making `ClearD` larger, run it again and each hole grows to the new size. To make a
hole smaller, undo the earlier cut first, then run it again.

<div class="callout note">
<span class="label">The two "flatten" commands are different operations</span>
<p><strong>Flatten to Polygon</strong> turns a curve into a polygon and always asks for the tolerance.
<strong>Flatten Hierarchy</strong> turns a placed instance into geometry in this cell. They are labelled
distinctly for that reason — "Flatten" alone gets misread.</p>
</div>

**Every command either does something or is disabled with a stated reason.** Context-menu items are
disabled rather than hidden, so their positions stay stable, and a disabled item's tooltip names the
condition — *"Select 2 or more shapes on the same layer"*. A command that is legitimately enabled but
changes nothing in a particular case (a union of shapes that do not touch) reports that through the
Messages pane rather than appearing to fail.

### Labels {#labels}

Every label is drawn in a **stroke font** — Hershey Roman Simplex, a pen font of one line width and a few
strokes per character, the kind a board shop's plotter prints silkscreen with. It covers every printable
ASCII character plus `Ω`, `µ`, `°` and `±`; any other character draws as a hollow box, and an export that
draws it says how many there were. The font draws the same on the canvas, in a rendered picture and in a
Gerber file, and Create Schematic from Artwork reads it back off a silkscreen.

The label's Properties panel holds:

| Field | What it does |
|---|---|
| **Cap height** | The height of a capital letter. A designator placed on a footprint defaults to 0.8 mm. |
| **Font** | **Stroke**, or **Sans** — the outline typeface, for a label that needs that look. The choice is per label. |
| **Pen width** | The stroke font's line width. Blank uses the default, shown greyed in the field: a sixth and a half of the cap height, raised to the layer's minimum-width rule when the technology has one. A blank pen follows the height when you change it. |
| **Style** | **Bold** draws with a pen 1.6 times as wide, **Italic** leans the text 12°, **Condensed** narrows it to 0.8 of its width. |

For a **Sans** label the same height field reads **Em size**: the outline typeface's em, of which a capital
is about seven tenths. Switching a label between the fonts keeps the number as it is, so switching back
restores the label exactly.

**Flatten to Polygon** on a stroke label gives **paths** — one per pen stroke, at the pen width — or, if
you choose **Polygons** in the dialog, the strokes' outlines as filled polygons. A Sans label always gives
filled outlines.

## Selection and vertex editing {#selection}

**Repeated clicks at the same point cycle through overlapping shapes.** The order is layer Z-order
descending, then *ascending area* — so a small shape sitting on a large one is reachable, which is the
case that actually matters. The status bar shows where you are: `Rect · M2 · 2 of 5`. Alt-click is the
explicit "next candidate" for anyone who expects it.

**A placed part is in the cycle too, and a part lying on a shape comes before that shape.** Press on a
line sitting on a ground plane and the first press picks the line; a second press at the same point picks
the plane. A part that is not inside the shape under the press comes after the shapes.

With exactly one shape selected you get handles: square handles on vertices, round handles at polygon
edge midpoints. Hit priority is strict — **vertex handle, then edge handle, then edge line, then shape
interior** — because getting that order wrong makes vertex dragging feel broken.

| Gesture | Effect |
|---|---|
| Drag a vertex | Move it, snapped and angle-mode constrained |
| Drag an edge | Translate that edge perpendicular to itself, preserving the adjacent edges' directions — this is what makes "widen this trace" one gesture |
| Ctrl/Cmd-click an edge | Insert a vertex there |
| Delete on a selected vertex | Remove it (blocked below three vertices) |
| Drag the interior | Move the whole shape |

Curved shapes get their own handles. An arc edge shows a midpoint **bulge handle** — drag perpendicular
to the chord to change the radius, past the chord to flip the sweep, which is the fastest way to radius
a corner. A cubic edge shows two control points with tangent lines drawn to their anchors; a circle
shows a radius handle; a rounded rectangle shows corner-radius and bounding-box handles. Right-click an
edge to **convert** it between Line, Arc and Cubic, so a polygon can grow a curve without being
redrawn.

A new rounded rectangle takes its corner radius from the toolbar's **Radius** field. It reads
**auto** until you type a length: each corner is rounded by a fifth of the shorter side, so the
rounding shows at any scale. Type a length to fix the radius, or **auto** to go back.

Self-intersection is allowed during a drag, flagged on release, and offered an automatic repair — it is
not rejected mid-gesture.

**Pasting between cells** carries the source layout's own resolution and layer definitions with it. Same
DBU pastes as-is; a different DBU rescales by the exact ratio, and warns through Messages naming the
offending shapes if the ratio would round. Layers absent from the destination technology are offered for
adding or mapping — never silently dropped.

## Hierarchy, instances and arrays {#hierarchy}

**Hierarchy here is the same idea as in the schematic, on the same cells.** Read
[Hierarchy in the Schematic Editor](schematic-editor.html#hierarchy) once; everything it says about
making a cell, placing it and pushing into it holds in this view, on the cell's layout instead of its
schematic. Push in and pop out are the same two toolbar buttons, the same <kbd>⌘]</kbd> /
<kbd>⌘[</kbd>, and the same clickable breadcrumb. **Pop Out** also leads the canvas context menu,
greyed out with its reason when you are already at the top level.

Three things are the layout's own:

- An **instance** references another cell's **layout** view — where the schematic instance referenced
  its symbol. A cell with a schematic but no layout has nothing to place here.
- **Arrays** (rows × columns at a pitch) are stored as one object, which is what makes a via farm or
  an MMIC unit-cell array nearly free to render.
- **Flatten** (one level or all levels) turns instances back into geometry, and **Group into new
  cell** does the reverse — hierarchy you can build after the fact, out of artwork you have already
  drawn. Cycles are detected at edit time, not discovered at render time.

## Bottom-side parts {#bottom-side}

A placed part sits on the top face of the board or the bottom one. To move it, select it and press
<kbd>B</kbd> — or **Edit ▸ Flip to Other Side**, the **Flip** button beside Mirror on the toolbar, or
**Flip to Bottom** / **Flip to Top** on the canvas context menu (**Flip** when more than one thing is
selected). It is one undo step, and flipping again brings the part back exactly.

A flip turns the selection over as one piece, about its centre, the way **Mirror** does — and then
moves it to the other face:

- A part's **lands, soldermask and paste openings, silkscreen, courtyard and assembly drawing** go to
  the bottom layers of the same kind. Its cell is not changed: the same footprint serves both faces.
- **Tracks and other shapes selected with it** on one of those layers go to the other face too, so a
  part and the copper under it flip together. Inner copper, drills and the board outline stay where
  they are, and a through via keeps its layers.
- **Text keeps its reading direction.** A bottom part's designator, and any text it carries, reads left
  to right from above and is never drawn upside down. It moves with the part and stays on the same side
  of its body.

**Side** in the Properties panel shows which face a part is on — *mixed* when the selected parts are on
different faces. Changing it turns each part over **in place**, about its own origin, rather than about
the centre of the selection.

**Mirror** is still a reflection on the same face: the part stays on its layers. Mirror also keeps
text readable — a mirrored label moves to where the reflection carries it and still reads left to
right.

**When the technology has no bottom layer for something.** On a technology with no bottom copper
there is no bottom face, and Flip is refused. If the technology has, say, a top silkscreen but no
bottom one, a part still flips: its silkscreen is simply not drawn while it is on the bottom, and the
Messages panel says so once for that part. Flipping it back restores it. A loose shape selected on such
a layer is different — it would be lost — so Flip is refused while one is selected, and the message
names it. A blind or buried via in the selection is refused the same way.

The **Layers** panel controls the bottom layers as it does the top: hiding **Silk Bottom** hides a
bottom part's designator, and hiding **Bottom Copper** hides its lands.

**In what you export.** A bottom part's lands, mask and silkscreen are written to the bottom files.

- **Gerber:** the bottom silkscreen is written as it is drawn, so its text reads left to right when you
  look at the file from above.
- **Placement file:** the Side column comes from the part's side, not from Mirror. The file states its
  rotation convention: angles are seen from the top, and a bottom part is flipped about its Y axis first.
- **Board file:** a bottom part is written as a back-side footprint.
- **GDSII and DXF:** each cell placed on the bottom is also written as a second structure or block,
  named with a `_B` suffix, holding its bottom layers. Reading such a file back gives that structure as
  a cell of its own, and the export report says so.

`check` warns about a bottom part whose technology has no bottom layer for one of its roles, and reports
a bottom part on a board with no bottom copper as an error.

## Finding an instance {#find}

On a board with hundreds of parts, **Design ▸ Find Instance…** — <kbd>⌘F</kbd> /
<kbd>Ctrl</kbd>+<kbd>F</kbd> — is the quick way to one of them. It opens the **Instances** panel for
the layout you are working in, with the cursor in its search box. Each placed instance is listed by
its **designator** (or, for one that has none, the name of the cell it places), its **type** — the
PCell generator or the part it was placed as, otherwise the cell — and the cell itself where that adds
something, including an array's rows × columns.

Type part of a designator to filter, pick a type to narrow further, and **double-click** a row — or
press <kbd>Enter</kbd> — to **zoom to that instance and select it**. An instance whose cell no longer
resolves is still listed and still zoomed to, at its placement point: that is often exactly why you
are looking for it. As in the schematic, only the level you are looking at is listed unless you tick
**Include sub-cells**: then every instance inside every placed cell is listed too, by path (`U1.U2`),
and double-clicking one pushes down to it. A parametric cell is listed but not searched inside — its
geometry is generated. The panel works identically for a schematic — see
[Finding a component](schematic-editor.html#find).

## Schematic ⇄ layout {#schematic-flow}

Two commands under the **Design** menu move work between the two views. They are deliberately symmetric,
and **neither ever runs by itself** — there is no save hook, no open hook, no activation hook. They run
when you invoke them and at no other time.

### Update Layout from Schematic

<kbd>⌘U</kbd>. Walks the schematic's instances, resolves each cell's layout view, and emits a starting
layout:

1. Each component instance resolves its layout view. A **stated footprint is consulted first** — an
   SMT case size, an imported part or your own `.clay` (see [Footprints](footprints.html)) — then a
   kit reference, then a cell reference, then a registered PCell generator, whose layout is generated
   rather than stored (see [PCells](pcells.html)).
2. Components with no layout view at all — VAR, MEAS, Ground, and any un-laid-out cell — are **reported
   to Messages and skipped**, not silently omitted.
3. Instances are placed in a packed, non-overlapping arrangement, roughly following schematic order.
4. A **ratsnest** is shown: thin straight lines between pins that share a net and are not yet joined by
   copper, each net drawn as the shortest set of lines that connects it. It follows a part as you drag,
   rotate, mirror or flip it, a deleted part's lines go with it, and a net's lines disappear once copper
   joins its pins. A part keeps its lines through **Cut then Paste**, which is the same part placed
   again, and through a Copy pasted after the original was deleted. A **Copy pasted while the original
   is still placed** is a second part the schematic does not have: it is given the next free designator,
   has no lines, and Messages says so ("C1 copied as C2 &mdash; not in the schematic, so it has no
   rats"); the original keeps its own. Undo puts the lines back with the part. The ratsnest is drawn
   over the layout, never into it &mdash; nothing is selectable, saved or exported. **View ▸ Show
   Ratsnest** turns it on and off.

<div class="callout warn">
<span class="label">There is no auto-router, and there is not going to be one</span>
<p>Step 3 places parts. It does not route them. Auto-routing is a separate multi-month product and
pretending otherwise would set the wrong expectation.</p>
</div>

**The ground is drawn too.** A pad whose schematic pin sits on a ground symbol gets a **ground via**
beside it, on the side away from the part, tied to the pad with a short trace and drilled to the
ground plane its layer returns through. On a two-layer board that is Bottom Copper. The ground plane
itself is **poured**: on every ground layer the microstrip lines and those vias return through,
inner or outer alike, five substrate heights beyond what it covers, or filling the board outline when
one is drawn. **Design ▸ Ground Pour Margin…** sets how many substrate heights the pour reaches for the
workspace; the next Update Layout or Draw Ground Pour redraws the pour with it. Every via that passes *through* a poured plane gets a clearance; a ground via is joined
to it. One Update Layout on a two-layer board with an MLIN and two shunt parts leaves the parts'
ground pads drilled to a poured Bottom Copper, and LVS reads them on the ground net.

The vias and the pour are **generated copper**, and each run redraws them from where the parts are
then: move a part and run it again, or use **Design ▸ Draw Ground Pour**, and its ground via follows
and the pour is redrawn. Copper you have taken over is left alone:

- a ground via **you moved** stays where you put it, and no longer follows its part;
- a ground via **you deleted** is not put back;
- a pour **you edited** (any reshape) is yours from then on, and so is any copper you **draw** on a
  ground layer: that layer is not poured over;
- a pasted or duplicated copy of generated copper is yours.

A ground via whose pin is no longer on ground in the schematic is removed, since it would short the
pin to the plane. If you had moved it, it is left and a warning says so.

**Re-running updates, it does not duplicate.** Each generated instance remembers the schematic
component's stable id, so a second run keeps and leaves in place what is already there, adds what is
new, and *reports* — never auto-deletes — what no longer exists in the schematic. You will hand-place
after the first generation and run it again after a schematic edit; a generator that stomped your
placement would be one you used exactly once.

**Bottom-side parts stay on the bottom.** A part you have flipped to the bottom keeps its side through
every later run, including one that replaces its footprint. Flipping a part does not touch its symbol,
and a later run does not read the flip as a change to undo. Mirroring the symbol mirrors the part on the
face it is on.

**Where the layout is written.** A schematic in a cell's `schematic/` folder writes that cell's
`layout/` folder. A schematic saved **loose** &mdash; at the workspace's root, say &mdash; writes its layout
beside it, under the same name: `PDN1.csch` updates `PDN1.clay` in the same folder. Update Schematic
from Layout follows the same rule in the other direction.

### Update Schematic from Layout

<kbd>⇧⌘U</kbd>. The mechanical inverse: it walks the **layout's** instances and writes the schematic to
match. Same file targeting (create the view if absent, open it, focus it, and leave a differently-named
primary alone with a report), same change report, one undoable action.

What moves in this direction depends on what the instance is:

| Instance in the layout | What reaches the schematic |
|---|---|
| A **PCell** — an MLIN whose `W` and `L` you dragged | The component **and its parameters**. This is how a layout-driven edit gets back into the circuit. |
| An **ordinary hierarchical cell** — a hand-drawn cell placed in this layout | A plain cell-reference component. There is no parameter for the layout to have moved. |
| A **wBond** | Its arrays and loop heights, reconciled against the live design — see [wBond](wbond.html). |

A cell placed this way that has **no symbol view at all** would render as a bare box with no pins, so
you are asked once — for the whole run, not once per cell — whether to generate symbols for them.

**A layout of artwork with nothing placed in it** — an imported Gerber board — has no instances to walk.
When the schematic it writes to is also empty, the command reads the artwork instead, exactly as
[Create ▸ Schematic from Artwork](artwork-to-schematic.html) does at its defaults, and draws the lines,
vias, ports and parts it finds into that schematic as one undoable action. The findings go to Messages.
It reads the **saved** layout. A schematic that already has anything in it is never changed this way.

On a board where parts **are** placed over imported copper, the parts are updated as usual, and Messages
says how much copper outside them the command does not model. **Create ▸ Schematic from Artwork** models
it, as lines and vias between the parts, and takes each placed part's kind and value from the schematic
it is linked to.

<div class="callout note">
<span class="label">What cannot be represented in the other view</span>
<p>Neither direction wires anything: <strong>Update Schematic from Layout places and updates components
and does not draw nets.</strong> And hand-drawn artwork beside placed instances has no schematic
counterpart — only <em>instances</em> cross the boundary, never loose shapes, unless nothing is placed
and the schematic is empty, as above. Both cases are reported, so the omission is visible rather than
silent.</p>
<p>A layout-first PCell has no existing schematic parameter to inherit a unit from, so a newly created
component's length-dimensioned parameters take the <strong>technology's own default display unit</strong>
— mil on a PCB, µm on an MMIC die.</p>
</div>

### The Design menu's schematic and layout commands

| Design ▸ | What it does |
|---|---|
| **Update Layout from Schematic** (<kbd>⌘U</kbd>) | Places and updates the layout's instances from the schematic's components. |
| **Update Schematic from Layout** (<kbd>⇧⌘U</kbd>) | Places and updates the schematic's components from the layout's linked instances. |
| **Create ▸ Schematic from Artwork…** | Reads artwork that has **no** links &mdash; an imported board's copper &mdash; as a new schematic of lines, vias and parts, reviewed first in a parts table. See [Create Schematic from Artwork](artwork-to-schematic.html). |
| **Create ▸ Schematic from Image…** | Reads a picture of a schematic or a layout &mdash; dropped, pasted or browsed to &mdash; and writes it as a schematic in a new cell. See [Create from Image](create-from-image.html#ways-in). |
| **Create ▸ Layout from Image…** | Traces a picture of a layout into the copper, vias and outline of a new layout cell. See [Create from Image](create-from-image.html#ways-in). |

## Geometry snap {#geometry-snap}

Two different things are called "snap" and it is worth separating them before anything else.

| | What it is | Control |
|---|---|---|
| **Grid snap** | The pitch new and edited vertices land on. A property of the document. | The **Snap** field in the toolbar; <kbd>F9</kbd> toggles it |
| **Geometry snap** | Snapping to a *feature* of existing artwork — a pin, a corner, a midpoint. | The magnet toggle; <kbd>F3</kbd> or <kbd>S</kbd> |

Geometry snap is **on by default**, and <kbd>Alt</kbd> held during a gesture **suppresses** it — Alt
never enables it, so the escape hatch is always in the same direction.

### What it snaps to

Six feature kinds, in **priority order, highest first**:

{{ui: snap-glyphs}}

| Priority | Feature | Glyph | What it is |
|---|---|---|---|
| 1 | **Pin** | {{snapglyph: pin}} | A declared connection point — a PCell's own pin, or one recovered from imported artwork |
| 2 | **Corner / endpoint** | {{snapglyph: corner}} | A shape's vertex, or the end of an open path |
| 3 | **Intersection** | {{snapglyph: intersection}} | Where two edges cross |
| 4 | **Midpoint** | {{snapglyph: midpoint}} | The middle of an edge |
| 5 | **Centroid** | {{snapglyph: centroid}} | A shape's area centre |
| 6 | **Nearest** | {{snapglyph: nearest}} | The closest point *on* an edge — no named feature, just "on this line" |

**The priority is what makes the behaviour predictable.** The more *intentional* a feature is, the
higher it ranks: when several candidates sit inside the capture radius at once — and near a corner they
usually do — the one that wins is the one you almost certainly meant. A pin beats a corner, a corner
beats a mere crossing, and "somewhere on this edge" is always the last resort.

The marker is drawn in **the source layer's own colour**, tinted for contrast against the canvas
background. That is not decoration: geometry snap crosses layers, and without it you cannot tell which
layer you are about to snap to.

### The capture radius

<div class="callout note">
<span class="label">The snap tolerance is not a setting</span>
<p>Geometry snap's capture radius is a fixed <strong>8 device pixels</strong>, converted to
<a href="units.html#dbu">DBU</a> from the current zoom on every query. So it stays the same size on
screen at every zoom level, and shrinks in real distance as you zoom in — which is what you want, because
zooming in is how you ask for precision. The toolbar's <strong>Snap</strong> field is the
<em>grid</em> pitch, a different quantity entirely; see <a href="units.html#snap">Layout units and the
snap grid</a>.</p>
</div>

### Intersections are different, and are off by default

**Intersections are computed live near the cursor, not indexed.** Corners, midpoints, centroids and pins
are properties of *one* shape, so they are cached per cell; an intersection is a property of a *pair*,
possibly spanning two cells, two instances or two layers, and the number of pairs is quadratic. It is
therefore computed on demand over a bounded set of candidates near the cursor.

Two consequences you will notice:

- It has its **own toolbar toggle**, and it is **off by default** — it is the one feature kind dense
  enough to be noisy, and leaving it off costs nothing when unused.
- With it on, snapping in a dense region does more work than snapping in a sparse one. That is the
  quadratic showing through, and it is why it is opt-in.

Turning either toggle recomputes the marker at the **last known cursor position immediately** — you do
not have to jiggle the mouse to see the mode change take effect.

### Dragging a part by its body

A drag started anywhere on a part or shape, not only on a snap marker, snaps what you are dragging
to what it comes to rest against:

- **A pin onto a pin.** When one of the dragged part's pins comes within the capture radius of
  another part's pin or of a port, the drag lands the two exactly on each other. This is how a line is
  dropped onto a tee with no gap.
- **An edge onto an edge.** Otherwise, when an outline edge of the selection comes within the capture
  radius of a parallel edge facing it on the same layer, the drag closes the gap (or removes the
  overlap) across the edge. Along the edge, the position stays the pointer's, on the grid.

The marker shows where the contact is. Turn geometry snap off with <kbd>S</kbd> or <kbd>F3</kbd> to
place a part freely.

## The Ruler {#ruler}

**Ruler** (`D`, or the ruler button on the toolbar) places a two-point measurement *inside* the layout:
a line between two points with the distance between them drawn at its midpoint. Click once to set the
first endpoint, move — the whole ruler previews, readout included, so you see the number before you
commit it — and click again to set the second. The tool stays armed, because measuring is something you
do several times in a row; **Escape** disarms it.

{{ui: layout-rulers}}

It serves two jobs. The first is the one you do a dozen times an hour while placing and routing: *how
far apart are these two things?* — answered on the canvas, without a dialog and without touching the
artwork. The second is annotation: a ruler stays where you put it, saves with the cell, and comes out
in a copy-paste into a slide or a report, so a design review can point at a clearance rather than
describe it.

<div class="callout note">
<span class="label">Not the ruler strip along the canvas edge</span>
<p>The strips along the top and left of the canvas are chrome: they track the viewport, show a scale,
and cannot be placed, saved or selected. Everything on this page is the <em>in-design</em> ruler, which
is a different object with its own colours in the theme editor.</p>
</div>

### Placing one accurately

**Both endpoints go through the editor's snap stack unchanged** — grid snap and [geometry
snap](#geometry-snap), with the same markers, exactly as a `Path` vertex does. That is what makes the
measurement trustworthy: an endpoint that lands 3 DBU short of a corner reports a number that is wrong
in a way nobody notices.

**Shift** locks the second endpoint to horizontal, vertical or 45°. It is *not* governed by the
document's angle mode — a Manhattan document is a statement about manufacturable artwork, and the
diagonal gap between two Manhattan traces is exactly the measurement you most want. With geometry snap
on, the two work together: your mouse movement picks the direction, and a snap feature in tolerance only
decides where along that direction the ruler ends — so a gap can be measured straight across to an edge
that is off to one side. A ruler whose endpoints coincide after snapping is
discarded rather than committed.

### What the readout says

Three parts, top to bottom, each independently omittable:

```text
   ╱
  ╱  3.59 mm             the distance — always shown, never editable
 ╱   Δx 2.54  Δy 2.54    when Show components is on
╱    bond wire span      when a Caption has been typed
```

**The distance is computed and cannot be typed over.** A ruler whose number can be overwritten is not a
measurement, and in a design review it is worse than no ruler at all — the *caption* is the free text.
It renders in the document's own display unit, with a decimal count and number format you can set, so
switching the document from mm to mil re-renders every ruler with no stored value changing. The text is
always drawn upright regardless of the ruler's angle, and is offset clear of the line.

### Fixed and Scaled text

One property, two honest behaviours, chosen per ruler:

| Mode | The text is | Use it for |
|---|---|---|
| **Fixed** (default, 11 pt) | *n* points on screen, the same size at every zoom | Temporary measurement — zoom out to the whole board and the readout is still legible |
| **Scaled** | a physical height in the layout, like a label | Annotation — the ruler keeps its proportion to the artwork, so a review figure reproduces at any scale |

The Properties panel shows **one** size field whose label and units follow the mode. Both values are
stored, so switching modes and switching back does not destroy the other one's setting. A
multi-selection whose modes differ disables the field rather than guessing — set them all to one mode
first, which is itself a single multi-edit.

### Selecting, editing, deleting

Rulers select like anything else: click the line, either endpoint, **or the readout text** — clicking
the number is the affordance most people reach for. Move, nudge, delete and copy all work, and a mixed
selection of shapes, instances and rulers still moves and deletes as one undo entry. Drag an endpoint
to re-measure. The Properties panel edits both endpoints numerically, the size mode and size, the font
style, the decimals and number format, the caption, the Δx/Δy toggle and the readout's own position and
alignment.

Right-clicking one offers **Edit Ruler…**, **Reset Ruler Label Position**, **Delete Ruler** and **Clear
All Rulers** (`Ctrl+K`). A per-document **Show Rulers** toggle hides them all without deleting anything;
it is view state and is deliberately not saved in the `.clay`.

### A ruler is not geometry

This is worth stating plainly, because it is the guarantee the feature is built on. A ruler is **not** a
shape. It lives in its own collection in the cell file, it has no layer, it obeys no layer visibility,
and nothing that walks the layout's shapes — GDSII, Gerber, Excellon, board export, booleans, offset,
flatten, DRC, the EM mesher — can see it. A dimension line etched in copper is a scrapped board; the
only way to make that impossible is for the annotation never to be in the collection those writers
read.

The two places it *does* come out are the two where a measurement is the point:

- **The clipboard.** Copy a selection and the pasted picture carries its rulers, which is what makes the
  review-slide use case work.
- **DXF**, as genuine **aligned `DIMENSION` entities** on their own `RULER` layer — see below.

## Interchange {#interchange}

**File ▸ Import** offers GDSII, DXF, Gerber and Board; **File ▸ Export** offers the same four. Both menus
also offer **GDSII (gdstk)** and **OASIS (gdstk)**, described [below](#oasis-gdstk).

**Every export says what it could not carry at full fidelity before it writes anything**, and that
preview is produced by running the *real* write into a null stream — so it can never disagree with what
lands on disk. Imports report the same class of thing afterwards, in the Messages panel: what was
skipped, by type, with a count.

A layer is identified by its integer **(layer, datatype)** pair, not by its name — that is GDSII's model
and it is the right one, since names are for humans and change, while the numeric pair is what a fab's
process assumption is keyed to. DXF and board files are name-keyed instead, so bringing one in goes
through an explicit name↔pair mapping held in the technology; one shared mapping dialog serves every
import that needs it. Gerber has no layer concept at all — one file per layer — so the mapping
is declared on the way out and reconstructed on the way in.

### GDSII {#gdsii}

Import and export. **Hierarchy is preserved** — one structure per cell, instances stay instances, and a
board with four hundred identical parts stays four hundred instances rather than four hundred copies of
geometry. Curved primitives are flattened to polygons and holes are keyholed, both because the format
has no other representation; the export report counts each. Vias export as barrel plus landing pad, and
a via with no landing layer exports its barrel only and is named in the report.

**Labels.** A label's datatype is written as GDSII's text type and read back from it, so a label on
`(31, 5)` arrives on `(31, 5)` and a process's label-purpose layer is recognised on import. A port label
carries a GDSII property on its text element (attribute 126, value `circuitrf:port`); another tool keeps or
ignores it, and on import only that property makes a label a port, never its text type. A text element's
magnification is folded into the label's height; its mirror is not held, and the import says so.

**Arrays.** An array reference whose rows and columns run along the axes, in either orientation, arrives
as one array. Any other lattice cannot be one array in circuitRF's model, so it arrives as one instance at
each lattice point, exactly, with a message naming the cell and the count. A file whose arrays would place
more than 100,000 instances this way is refused, and nothing is created.

**Other elements.** A `BOX` arrives as a polygon on its layer and box type. A `NODE` carries connectivity,
not artwork, and is skipped. The import counts each.

**Numbers the format cannot hold.** GDSII numbers layers, datatypes and text types 0–65535 and array
columns and rows 1–32767, and the export refuses, by name and before writing anything, a shape or instance
outside those ranges. A layer that came in from DXF or a board file with no GDSII number of its own is
written as the lowest layer number the export is not already using, and the report says which.

### OASIS and GDSII through gdstk {#oasis-gdstk}

OASIS is the compact successor to GDSII that mask shops and fabs increasingly exchange. circuitRF reads and
writes it through **gdstk**, an open-source layout library that runs as a separate program shipped beside
circuitRF. gdstk also reads and writes GDSII, which gives GDSII a second route. The four entries are:

| Entry | What it does |
|---|---|
| **File ▸ Import ▸ OASIS (gdstk)…** | Imports every cell of an `.oas` file, as **GDSII…** does for a `.gds`. |
| **File ▸ Export ▸ OASIS (gdstk)** | Exports the active layout and the cells it places as one `.oas` file. |
| **File ▸ Import ▸ GDSII (gdstk)…** | Imports a `.gds` file with gdstk's reader instead of circuitRF's own. |
| **File ▸ Export ▸ GDSII (gdstk)** | Exports a `.gds` file with gdstk's writer instead of circuitRF's own. |

gdstk only reads and writes the file. Everything after the read, and everything before the write, is
circuitRF's own and the same for every route: the layer-mapping dialog, cell naming, port recognition, the
export's fidelity report, and the DRC and LVS checks your settings run before an export. If an
installation has no gdstk, the four entries are disabled, and their tooltip says so.

**Why there are two GDSII routes.** Plain **GDSII…** is circuitRF's own reader and writer. It stays the
default, and it is the one the layout toolbar's export button uses. The **(gdstk)** entries are an
independent implementation of the same format. When a GDSII file from another tool imports wrongly, or a
file circuitRF wrote is read wrongly elsewhere, try it through the other route. If the two routes agree,
the problem is in the file. If they disagree, the route is the likely cause, so say which route you used
when you report it. The label names the route for that reason. Two differences are expected:

- **Label size.** gdstk reads a text element's size from its magnification only. circuitRF's own writer
  stores a label's height in the element's width field, so a label written by **GDSII** and read by
  **GDSII (gdstk)…** arrives at the default height.
- **Port labels.** gdstk does not carry properties. A port label exported through either (gdstk) route is
  written as plain text, and the export report counts how many. On import, properties are counted and
  skipped.

A gdstk export refuses an instance of a cell that the export does not include. The export dialog lists
any such instance before the save dialog opens. circuitRF's own GDSII writer writes it as a reference to
a cell that is not in the file.

**What an OASIS import does not bring in.** Properties, `XNAME` and `XELEMENT` records have no form in a
circuitRF layout. They are skipped and counted in one message. A circle arrives as the polygon gdstk reads
it as. A shape or instance **repetition** is expanded into separate shapes or instances. An import that
would create more than **1,000,000** of them is refused, says how many it would create, and creates
nothing. A GDSII file is held to 100,000 on both routes. An OASIS file can name its layers (`LAYERNAME`).
Where it does, a layer of your technology with the **same name** is matched first, whatever its number,
and the layer/datatype number is used only when no name matches. A grid finer than the workspace's
resolution is handled as for GDSII.

**What an OASIS export changes, and says so.** OASIS has no round path end, so a round end is written flush.
OASIS stores a path's half-width, so an odd width is written one database unit wider. OASIS text has no
rotation or size, so a rotated or resized label reads back upright at the default height. The export
dialog counts each of these before anything is written. Curves are flattened and holes keyholed exactly as
for GDSII. An OASIS `CIRCLE` record is never written.

The export dialog has an **OASIS options** section. Its settings are remembered between exports:

| Option | Default | What it does |
|---|---|---|
| Compression level | 6 | How hard compressed blocks are compressed, from 0 (stored) to 9. |
| Validation | CRC-32 | How a reader can check the file's bytes: CRC-32, a 32-bit checksum, or none. |
| Write rectangles and trapezoids as OASIS shapes | on | Smaller files. Off writes every polygon as a general polygon. |
| Write standard properties | off | Adds the SEMI standard properties that some readers use. |

Exporting the same layout twice with the same options writes the same bytes. From the command line,
[`convert`](cli.html#convert-oasis) does the same imports and exports (`--engine gdstk` selects the second
GDSII route), and [`check`](cli.html#check) reads an OASIS file's cells and layer names without importing
it.

### DXF {#dxf}

Import and export. The exporter writes **R2000 (AC1015), R2004 (AC1018) or R2018 (AC1032)**, selectable,
defaulting to **AC1032**. The choice is about **colour, not geometry**: every entity circuitRF emits —
`LWPOLYLINE` with bulge, `LINE`, `ARC`, `CIRCLE`, `ELLIPSE`, `SPLINE`, `HATCH`, `TEXT`, `INSERT`,
`BLOCK` — exists unchanged in R2000, but 24-bit true layer colour (group 420) arrived in R2004 and R2018
carries the identical capability. Choose R2000 only if a downstream tool refuses the newer header, and
accept nearest-index colour when you do. **R12 is deliberately not offered** — it has no `LWPOLYLINE`,
no `ELLIPSE`, no `SPLINE` and no `HATCH`, so writing it would mean flattening arcs and losing hole
fills.

**Arcs are never flattened on DXF export.** circuitRF's own bulge convention and DXF's are the same
quantity, so an arc edge exports by copying the number.

**Rulers export here and only here**, as real aligned `DIMENSION` entities on a layer named `RULER`, so
the recipient can freeze or delete every one of them at once. A `DIMENSION` rather than a line plus some
text, because loose geometry does not report a measurement, does not update if the recipient stretches
the drawing, cannot be styled, and does not appear in anything that enumerates dimensions. A
**Fixed**-size ruler has no meaning in a world-coordinate drawing with no screen, so its point size is
resolved once, at export, to the height that occupies the same fraction of the drawing that its point
size occupied of a nominal viewport — legible when the recipient zooms to extents, which is what the
mode meant on screen.

### Gerber and Excellon {#gerber}

Import **and** export. A board goes out to the fab house as artwork plus drill, and the same set comes
back in as a cell you can edit, crop and mesh — your own, or someone else's.

#### Export

One Gerber file per layer, plus an Excellon drill file. Arcs are native (`G02`/`G03`) and a hole is a
clear region, so neither is a lossy conversion; the pre-flight report lists only genuinely structural
changes. A bare circle drawn directly on a drill-function layer still contributes a drill hit — it is
never silently dropped — but it is *unpaired*: no matching pad, no annular-ring data, and the report
suggests **Convert to Via** for it.

#### Import — **File ▸ Import ▸ Gerber…**

Point at a folder and the whole board comes in as one flat cell, with its vias rebuilt from the drill data. What
each file is gets decided by its content, never its extension, and which layer it is by the job file, the
file's X2 attributes, a Gerber suffix in your technology, a name heuristic, or the layer-mapping dialog, in that
order. An older set may carry a layer list instead of a job file &mdash; a text file of `{Layer n {File …} {Type …}}`
blocks &mdash; and its `Top`, `Inner` and `Bottom` types settle which files are copper, and in what order, exactly as a
job file does. The dialog's **Technology** row chooses between a new technology written from the files &mdash; whose
substrate is a guess, named as one in the import summary &mdash; and a technology you already have, which the
layout then uses unchanged. [AN-02](../app-notes/an02-gerber-import.html) walks an import through on a real
board, every dialog included.

#### What a round trip does and does not preserve

Export → import is **not** lossless, because the format has no such types — but it is **closed after one
pass**: whatever the first cycle collapses, every later cycle preserves exactly, byte for byte.

| Leaves as | Comes back as |
|---|---|
| rectangle, rounded rectangle, curve | polygon |
| label | round-capped paths, one per pen stroke (a Sans label: polygons) — labels become geometry on export |
| path with a non-round end style | region |
| via | via — but only if the drill file came too |
| a layer using clear polarity | composited polygons; individual shape identities gone |

Circle and rectangle flashes, round-capped strokes with their width, arcs, holes, nets and multiple
drill diameters all survive intact.

**Refused rather than mangled**, each by name: negative image (`%IPNEG`), non-identity mirror, scale or
rotation, block apertures (`%AB`), a file with no coordinate format or no units, a binary drill file, a
drill *listing* mistaken for a drill file, and anything over the 500,000-entity import ceiling. Anything
read but degraded — a stroke with a non-circular aperture, a moiré macro primitive, a routed arc — is
counted by name in the summary rather than dropped quietly.

### Board files (`.kicad_pcb`) {#board}

Import **and** export, through **File ▸ Import ▸ Board…** and **File ▸ Export ▸ Board…**.

#### Which versions

**Import branches on the tokens present, never on the version stamp**, and no file is refused for its
version. That is not a stylistic preference: four epochs of one real board were measured, and the
spellings are mixed *within* a single file — at the newest epoch every footprint line carries
`(stroke (width W))` while a graphic polygon still carries a bare `(width W)`. The layer table moves
too (`B.Cu` is ordinal 31 in older files and 2 in the newest), and in the oldest a renamed layer's user
name occupies the canonical name slot, so a file may contain no string `F.Cu` at all. Everything
therefore resolves through the file's own layer table, and *"is this copper"* is that table's **type**
word — never a name and never an ordinal range. The epoch is **reported** in the import log, so you can
see what circuitRF thought it was reading.

| Epoch | Stroke width | Arcs | Fill flag | Nets |
|---|---|---|---|---|
| 20171130 | `(width W)` | centre + angle | absent | `(net 7)` + a top-level table |
| 20211014 | `(width W)` | three-point `(mid x y)` | `(fill none / yes)` | `(net 7)` + a table |
| 20221018 | `(stroke (width W) …)` | three-point | `(fill none / yes)` | `(net 7)` + a table |
| 20260206 | `(stroke (width W) …)` | three-point | `(fill no / yes)` | `(net "GND")`, no table |

**Export writes one dialect, 20221018**, and does not offer a choice. A reader has to accept files of
every epoch because they arrive unbidden; a writer has the opposite problem, and must pick one every
downstream reader accepts. 20221018 is late enough to be free of design rules and net classes — those
left the board file at the 20211014 epoch — and early enough that every later release still opens it.

#### The technology it lands in

The board comes in as a folder of cells named after the file. The layer-mapping dialog's **Technology** row
chooses where its layers go, as it does for a Gerber set:

- **New technology from the board** (the default) writes `<board>.ctech` into that folder, and every cell
  the import makes uses it. It starts as a copy of your workspace technology: its layers, rules and settings.
  Then it adds every layer the board draws on, under the name the board file gives it
  (`F.Silkscreen`, `User.Drawings`). The stackup is your technology's when its conductors match the board's
  copper. Otherwise it is the board's own stackup. Your workspace technology is not changed.
- **A technology you already have** is used as it is. The cells reference it, and nothing is added to it.
  A board layer it has no layer for stays an unnamed layer, and the import lists those layers. A board
  whose number of copper layers differs from that technology's conductors is refused, and nothing is created.

#### What comes across

Board-level geometry and footprint geometry are both handled; a footprint's items are in its own frame
and compose through its placement, at an arbitrary angle.

**Back-side footprints arrive as [bottom-side parts](#bottom-side) of one footprint.** A part on the back
of the board is placed on the bottom face, using the same cell as that footprint on the front, so a
footprint used on both faces is one cell. Every land, mask opening and silkscreen line lands exactly
where the board file puts it, on the bottom layers. The import's report says how many back-side
placements it found. Two cases keep a back part as a cell of its own, already flipped and placed on the
top: when the technology has no front layer for something the part draws on its back face (the report
names the part and the layer), and when the board goes into an existing technology that has no bottom copper.

| In the board file | Becomes, in circuitRF |
|---|---|
| `segment` (track) | a path, round-ended, carrying its net |
| `arc` (three-point) | a path with a single arc edge — the bulge is exact, not sampled |
| `via` | a via primitive: **barrel** on a drill layer, **pad** on the landing layer |
| `gr_line` | a two-point path at the stroke width |
| `gr_rect`, filled / unfilled | a rectangle / a path tracing the four edges at the stroke width |
| `gr_circle`, filled / unfilled | a circle / an annulus at ± half the stroke width |
| `gr_arc` | a path with one arc edge |
| `gr_poly`, filled / unfilled | a polygon / an outline path |
| `gr_curve`, `bezier` | a curve or path with a cubic edge |
| `gr_text` | a label in the stroke font, its size read as the cap height; on export a label writes its pen width as the text's thickness |
| `zone` → `filled_polygon` | polygons, with their holes |
| `footprint` / `module` | one generated cell per distinct definition, one instance per placement; a back-side one is a bottom-side placement |
| pads: `circle`, `rect`, `oval`, `roundrect`, `trapezoid`, `custom` | real copper geometry — see below |
| images, dimensions, groups, tables, tuning patterns, teardrops | skipped, reported by type with a count |

**Pads are the fiddly part and are handled by shape**, not approximated by a bounding box. A
`roundrect` becomes a rounded rectangle; one carrying a `chamfer` — corners cut rather than rounded, at
the chamfer ratio times the pad's short side — or one rotated off a cardinal angle builds the general
boundary instead, straight edges plus quarter-circle arc edges. A `trapezoid` honours its own offset,
which puts copper beyond the nominal size on one side and inside it on the other. A `custom` pad is its
anchor shape **unioned with every one of its primitives**, all of them — taking the anchor alone
frequently left no copper at all under the pin. An oval drill is drawn as the slot it is.

<div class="callout warn">
<span class="label">Three import rules that exist because getting them wrong is silent</span>
<ul>
<li><strong>An unfilled outline never becomes a filled region.</strong> An unfilled rectangle imported
as a solid one is an entire copper pour that does not exist on the board — and it would be meshed and
simulated as one.</li>
<li><strong>Zones import their <em>fill</em>, not their outline.</strong> The outline is the author's
request; the filled polygons are the copper that exists, and the outline includes every area the fill
would have cleared around pads and neighbouring nets. A zone that was never filled is skipped and
counted, not fallen back to — that count is your cue to fill the board in the originating tool and
re-import. Keepout zones are not copper and are skipped.</li>
<li><strong>A via's barrel and its pad are different layers.</strong> Getting them the wrong way round
produces an export that looks plausible and puts copper where the hole should be. Blind and buried vias
are reported by count, naming how they were placed, rather than being silently flattened to through
vias.</li>
</ul>
</div>

#### What does not come across, either way

- **Design rules and net classes are never written**, and this is deliberate rather than missing. The
  two models disagree in kind: circuitRF's rules are per-layer process geometry (minimum width,
  spacing, enclosure, overlap, density) over boolean-derived regions, while a board file's are
  per-net-class routing constraints (clearance, trace width, via diameter) living in a sibling project
  file. Only width and spacing have any counterpart at all, and circuitRF has no net-class concept to
  attach them to — so every rule would collapse onto one synthesised "Default" class that looks
  authoritative and is wrong for every net but the narrowest. **What the technology holds and the
  format cannot carry is reported instead.** Set the rules on the receiving side.
- **Plot/Gerber output configuration, the auxiliary origin and the tenting flags** are not written —
  all optional, all would be invented.
- **A footprint's reference and value text** is not imported. That text belongs to the *placement*, not
  to the library part, so importing it into the shared cell would bake one designator into every copy
  and mint a separate cell per placement — which is the four-hundred-copies-of-geometry outcome the
  content-addressed cell store exists to prevent. Board-level text imports normally.
- **A pad's net** does not survive into the shared footprint cell, for the same reason. The tracks
  reaching that pad still carry theirs, and the count is reported.
- **Rulers** are not written to a board file. It is fabrication data.
- On export, a **cell that declares no pins** has no pads to route to, so its artwork is flattened onto
  the board rather than written as an unroutable footprint — the geometry survives, the component does
  not, and the count says so. **Bitmaps** are skipped and **cubic curves** are flattened, both counted.

Both directions have a hard entity ceiling and refuse **before allocating** rather than dying partway
through and leaving a half-imported layout or a half-written file that opens and is wrong. Coordinates
are decimal millimetres with no exponent notation; at the default 1000 DBU/µm one DBU is one nanometre,
so six decimal places represent every DBU exactly — this is the one interchange path here that is
lossless in both directions.

## Design-rule checking {#drc}

**Design ▸ Check Design Rules**, or the **Check** button on the DRC panel. The rules come from the
**technology**, not from the layout, so a design retargeted to a different process is checked against
that process's rules without anything being re-entered.

The rule kinds a technology can declare are minimum width, minimum spacing, minimum separation,
minimum enclosure, minimum overlap, metal density and **must be empty**; each names the layer (or the
layer pair) it applies to and the value it requires. A must-be-empty rule has no value: every shape
left in its region is reported under the rule's name, which is how a rule such as "no drill inside an
SMD pad's mask opening" is written. `.ctech` files carry them, so a kit's own rules arrive with the
kit.

{{ui: drc-violations}}

Each violation says which rule it broke, between which shapes, and by how much — and clicking one
zooms the canvas to it. Markers draw over the offending geometry and can be toggled off. The header
always names the **technology that was checked against**: a clean result against the wrong process
looks exactly like a clean result against the right one, which is why the panel never leaves it to be
inferred.

A violation can be **waived, with a reason**. A waived hit stays in the list, greyed and counted
separately, rather than disappearing — a known exception you can still see is worth more than a clean
report you cannot trust.

<div class="callout note">
<span class="label">DRC is the geometry; LVS is the netlist</span>
<p>The DRC checks the <strong>geometry</strong> against the process rules, and the connectivity it
computes internally exists to tell one net's shapes from another's. Whether the artwork implements the
<strong>schematic</strong> &mdash; every device there once, every net whole and separate, every terminal
on the right net, every value in agreement &mdash; is a different check with a different engine: see
{{anchor: lvs|layout versus schematic}}. A board can pass either one and fail the other.</p>
</div>

## Trace impedance {#trace-impedance}

Right-click a trace and choose **Trace Impedance** to get its characteristic impedance in the
**Messages** panel, in well under a second. It is a review check, made for imported boards: the width is
**measured** from the copper at the point you clicked, so it works on Gerber artwork, where a trace is a
polygon and not a drawn path with a width.

It is one line, for example *Trace Z0: Z0 46.5 Ω, εeff 3.18 — GCPW, W 480, G 561/559, H 241 µm
(G to conductors, ref Inner 2) — Top Copper (-9138.1, 1)*, where G is the gap to coplanar ground on each
side and says what it ends on. **G to via pads** means the nearest copper beside the trace is a row of via
lands with no pour around them; the cross-section treats that row as a continuous edge, so check that is
what you intended. A problem found under the trace is added to the same line after ⚠. How it
gets there:

- **The reference is found from the copper, not from the technology's ground flags.** It is the nearest
  layer below the trace whose copper covers the trace's whole width at that point (and the nearest above,
  for a stripline). Copper beside the trace on its own layer is coplanar ground; every conductor near the
  trace is taken to be at ground, which is the only reading a Gerber board allows. The one exception is
  a layer with **nothing drawn on it at all** that the technology marks as the ground reference &mdash; a
  layout that draws only its top copper and lets the stackup's ground layer stand for the plane. That
  layer is taken as a solid plane, and the answer says so.
- **The answer is a quasi-static cross-section solve**, the same engine the EM setup uses for a uniform
  line, so microstrip, grounded coplanar waveguide at any gap and stripline are one method. It is
  frequency-independent and lossless: a check of the geometry, not a replacement for an EM run.
- **It refuses rather than guess.** A pad or a short stub, a bend or a mitre, a junction and a taper
  have no single width, and the message says which one it found. Copper is a line when it runs at least
  twice its width; a click less than a width from its end is measured one width in, and the answer
  says where. Click the middle of a straight, constant-width run.

It also walks the whole constant-width section and **warns where the ground under the trace breaks**
— a trace on an inner layer routed over a cutout in the plane next to it is flagged with where the gap
starts and ends, after ⚠ on the same line. A layer cleared under the *whole* section is not flagged —
that is what a wide trace referenced to a deeper plane looks like, and the *ref* on the line says which
plane was used.

The **current drawing layer** decides which trace you mean where two layers carry copper at the same
point; otherwise it is the highest one in the stackup. When that layer has no straight trace at the
point &mdash; most often because it is a **plane under the trace** &mdash; the other copper layers there
are tried in turn, and the line names the layer it measured.

Imported artwork is rarely perfectly straight. A **jog of a few microns** in a trace's edge, where a
Gerber stroke was split, is not a bend: the direction is taken from the trace's centre line, not from
the one edge beside the click. And where the coplanar ground beside the trace **is not straight**
&mdash; a pour that juts in toward the trace for part of its run &mdash; the answer is for the gap at the
point you clicked, and the line adds *G right varies 184–259 µm along the run* so you know the number
is local. The [Impedance Analysis](#impedance-analysis) shows Z0 along the whole trace.

## Impedance Analysis {#impedance-analysis}

The **Impedance** panel checks **every trace on the layers you choose** against a target impedance,
shows the result on the board, and exports the review as a PDF &mdash; the document you file with a
board you send out with an impedance-control note. It is [Trace impedance](#trace-impedance) run end
to end along every trace, from the same cross-section solve, so a trace's number in the panel is the
number you get by right-clicking it.

The **Z₀** tile on the toolbar (beside Check Design Rules) opens the panel for the layout you are in,
docked beside Properties; it is also under **View ▸ Panels ▸ Impedance**. Like the DRC panel it follows
the layout you are working in, and each layout keeps its own results.

**Settings and scope**, at the top of the panel:

- **Target Z₀** &mdash; the target, in ohms. 50 Ω by default.
- **Tolerance** &mdash; ± this many percent passes, 10 % by default.
- **Warning** &mdash; ± this many percent is a warning rather than a fail, 20 % by default. It must be
  wider than the tolerance. The panel shows both bands it gives you, for example
  *Pass 45.0–55.0 Ω · Warning 40.0–60.0 Ω*.
- **Highest frequency** &mdash; optional, blank by default. When given (*6 GHz*; a bare number is GHz),
  a stretch outside the warning band that is **shorter than λ/20** at that frequency is a warning
  rather than a fail &mdash; see [electrically short](#impedance-findings).
- **Skip near vias** &mdash; *400 µm* by default (about 16 mil). Trace within this distance of the land
  of a via **on that trace** is not checked: a plane is normally cleared round a via, so the last stretch
  of every trace into one has no reference under it by design, and would otherwise flag every layer
  change. Each skipped stretch is listed in its trace's notes, naming the via, so the transition is still
  there to verify with an EM run. A via that only passes beside a trace skips nothing. A bare number is
  µm; **0** checks every trace up to its via. The default suits an antipad of the via land plus
  0.2&ndash;0.3 mm; widen it if your antipads are larger.
- **Layers** &mdash; every copper layer of the stackup, all ticked by default. A layer with no copper
  on it is shown but cannot be ticked.
- **Traces** &mdash; the trace widths on each ticked layer, to say which traces are under review. See
  [Choosing the traces](#impedance-scope).
- **Regions, picks and nets** &mdash; the traces under review pointed at on the board itself: an area
  drawn round them, the traces clicked, or their net. See [On the board](#impedance-scope-board).
- **Scope** &mdash; the traces that will be reviewed, in one sentence, updated as you tick and untick.

**Run** analyses them. A bar shows the layer being analysed and how far through it the run is, and the
line under it counts the cross-sections solved, the time elapsed and, once a few are done, about how long
the layer has left. A whole ten-layer board of about 1,800 traces takes a few minutes, a single outer
layer a few seconds.
**Cancel** stops the run and keeps **every layer that finished**. The Messages panel gets one line with
the counts that pass, warn and fail; it is a warning line only when something **failed** or the run was
cancelled.

**The results** stay in the panel until you run again:

- **Tiles** count the traces that **pass**, **warn** and **fail**, the findings **accepted**, and the
  traces **out of scope**.
- A **filter** lists **All**, **Warnings and failures** (the default), **Failures** or **Accepted**. A
  trace that could not be solved at all was not reviewed, and is listed with the failures. The
  warning and failure filters leave [accepted findings](#impedance-accept) out; **Accepted** lists only
  them.
- **One row per trace**: its id, layer, verdict, width, Z0 min–max, the share of it inside the pass
  band, and its line type &mdash; the same text the PDF's table prints. The arrow beside a row opens its
  findings, each with its severity, and its notes.
- **Selecting a row** marks that trace, or that finding, on the canvas; **double-clicking** it zooms to
  the trace, or to the stretch the finding is about.

**On the canvas**, while the panel has results, every reviewed trace's centre line is drawn in its Z0
colour &mdash; the PDF map's scale: green inside the pass band, blue below, red above &mdash; with a
marker at each finding: **filled** for a fail, **hollow** for a warning, orange for Z0 and purple for the
return path, and **grey with a check** for an accepted finding. A trace outside the scope is not
drawn. **Show on canvas** turns this off. It is never part
of the artwork: no export, Gerber, GDSII or `render` picture carries it.

**Editing the layout keeps the results** and marks them stale &mdash; *The layout has changed since this
run* &mdash; because you are usually part-way through fixing what they found. Run again to review the
artwork as it is now.

**Export PDF…** writes the results the panel holds as a PDF, without running again. It is available once
there are results; a PDF exported from stale results says so at the top of its first page, so it cannot
pass as a review of the board as it is now. The Messages line for an export links to the file.

Above the button, choose **Full** or **Summary**, each with the number of pages it comes to. **Full**
lists every trace, junction and finding; **Summary** keeps the map and groups the findings, for a board
too large to read trace by trace (see [The report](#impedance-report)). Full is chosen unless it would
run to more than 50 pages, when Summary is chosen instead. Whenever Full would run past 50 pages, the line
under the choice says how long it is, whichever style you pick. The choice is yours either way.

**The review is saved in the layout** as you edit it: the target, both bands, the frequency, the layers
the trace widths ticked, and the regions, picks and nets are stored in the `.clay`, beside the layout's design-rule waivers, so the
panel opens on them next time and [`circuitrf impedance`](cli.html#impedance) reviews the same traces
headlessly. Like a waiver, it is a statement about this artwork: changing it marks the layout modified,
and it is not undone by **Undo**. The results themselves are not saved; the findings you
[accept](#impedance-accept) are, on the same terms.

### Choosing the traces {#impedance-scope}

A board carries far more traces than are meant to be controlled, and holding the routing to 50 Ω makes a
report that is mostly FAIL for traces nobody meant to be 50 Ω. The **Traces** section says which ones are
under review, by **width** &mdash; which is how a fabrication drawing's impedance note is written
(*457 µm on L1: 50 Ω controlled*), and which works on imported Gerber artwork with no nets.

When the panel is on screen it surveys the board in the background and lists, under each ticked layer,
every **width class** on it (the refresh button beside **Traces** surveys again after the artwork has
changed):

*457 µm &nbsp; 1 trace &nbsp; 18.3 mm · typical Z₀ 55.4 Ω*

- A trace's width is the width over **most of its length**, so a trace that tapers into a pad is listed
  at the width it mostly has. Widths within 1 % (or 1 µm) of each other are one class.
- **Typical Z₀** is one cut at the middle of the class's longest trace &mdash; a guide to which class is
  which, not the class's result. The analysis measures every trace in full.
- **Nothing is ticked for you**, not even the widths already near the target: a trace drawn at the wrong
  width is exactly what the review exists to catch. Once you have ticked some, the saved choice is what
  the panel opens on.
- **No width ticked on a layer reviews every width on it.** Unticking a layer hides its widths.
- A saved width the artwork no longer has is kept, struck through, with *no traces at this width now*;
  untick it to drop it.

Traces outside the choice are **not cut, not solved and not listed** &mdash; on a large board that is most
of the run time &mdash; but they are still **copper**: a trace under review still sees its out-of-scope
neighbour as grounded copper beside it, exactly as without a choice. The report says what was reviewed
and counts what was not, for example *Top Copper at 457 µm (1 trace). 21 traces on Top Copper and 19 on
Inner 1 are outside the scope and were not analysed.*

### On the board: regions, picks and nets {#impedance-scope-board}

Width fails when an RF trace shares its width with other traces &mdash; a 50 Ω line and a digital bus
drawn at the same width &mdash; or when the RF path changes width along its way. The **Regions, picks and
nets** section says which traces are under review by pointing at them:

- **Add region ▸ Rectangle** &mdash; drag a rectangle on the canvas. **Add region ▸ Lasso** &mdash; drag
  round the area freehand; the outline closes when you let go. **Escape** cancels.
- **Pick traces** &mdash; while it is on, **click** a trace to add it, or **Shift-click** to add it
  **and every trace joined to it** through vias, on any layer. Escape, or clicking the button again,
  stops. The canvas's right-click menu also has **Add to impedance review** beside **Trace Impedance**.
- **Nets** &mdash; when the artwork carries net names (a Gerber X2 file's net attributes, or a board
  netlist applied on import), a searchable list of them; tick the nets to review. The section is not
  there at all for artwork with no nets. A trace's net is the net of the copper under its middle; the
  panel counts the traces whose copper carries no net (or two), because no net can choose them.

**Any one of them chooses a trace, and the widths then filter what they chose.** With none set, every
trace is reviewed, as before.

**Once you point at anything, only what you pointed at is reviewed &mdash; on every layer.** A region
belongs to one layer, so if you draw regions on Top only, no other layer is reviewed. That is the
reduced review a region asks for. Each ticked layer in the **Layers** list then says what is reviewed
on it (*1 region*, *every trace*, or *nothing selected &mdash; not reviewed*). To keep a whole layer
without drawing round it, tick **Whole layer** beside it.

- **A region takes a trace any part of which lies inside it**, and reviews it **whole**: a region that
  clips the end of the RF trace does not drop it, and never cuts it short. Copper a region chooses must
  still be **two of its widths long** to be a trace; when a region on a layer finds no trace but covers
  shorter copper (a short link from a via to a test pad, say), Messages and the report's first page say
  so, with that copper's length and width.
- **A region belongs to the layer you draw it on**: the layout's current layer, when that is a copper
  layer under review and shown; otherwise the one layer under review that is shown, when only one is
  (hide the other copper layers to draw on Bottom without changing the current layer); otherwise the
  one layer under review, when only one is ticked; otherwise every layer. It selects traces on that layer only, and is drawn only while that layer is shown and ticked.
  A region on **All layers** selects on every layer, is always drawn, and has an amber dash-dot outline
  so it is not mistaken for one left over from another layer. Regions saved before regions had layers
  open as **All layers** and review exactly what they reviewed before.
- Regions are listed in the panel, where you can name one (the name appears in the report), change its
  layer (including to **All layers**) or remove it; selecting one highlights it on the canvas. To change
  a region's shape, remove it and draw it again.
- **A pick is a point, not a list of shapes**: it is looked up again at every run, so it survives edits
  and a re-import as long as copper is still there. A pick with no copper under it any more is kept and
  listed as *no copper here now* until you remove it.
- **Joined copper stops at a part.** A capacitor or resistor in series is not copper, so a path through a
  DC-block capacitor is two picks, one each side of it.

While the panel is open, regions are drawn on the canvas as a dashed outline over a light tint and picks
as a ring and a cross (two rings for a joined pick, grey when there is no copper under it). The PDF's
maps draw the regions dashed too, each on its own layer's map, and its scope sentence names each selector: *Selected by 2 regions
('RF front end', 'antenna'); 1 pick (Top Copper, connected); net RF_OUT. Top Copper, every width (3
traces).* Before a run the panel's Scope line names the selectors but cannot count what they choose;
the run counts it.

### What counts as a trace {#impedance-traces}

The analysis works on the copper as drawn, so it reads imported Gerber artwork as well as a layout
you drew:

- **A trace is a stretch with two long parallel edges facing each other.** Stretches that meet within
  about a width of each other &mdash; through a jog, round a **bend or a mitre**, across a **width
  step** &mdash; are one trace. The corner of a bend has no single width and is **not cut and not
  flagged**, so a hard 90° turn is not a false alarm.
- **A trace ends** at a junction, at a via, at a pad or where the copper ends. **A via ends it**: what
  continues on another layer is that layer's trace, and is a row of its own. The table says what each
  end is.
- **A pad at a trace's end is not part of the trace.** Copper at the end that is more than a fifth
  wider than the trace, and shorter than four of its own widths, is the land the trace runs onto: it is
  left out of the trace's length, width and Z₀, and the end reads *pad*. A pad inside a trace &mdash;
  a line that runs on past it &mdash; is a genuine width step and stays.
- **A stretch shorter than four of its widths is read as a pad** &mdash; unless a region, a pick or a
  net chooses it, and then two widths is enough. A wide line cut into sections by series parts is often
  only two or three widths long between them; point at it and each section is reviewed. A run with no
  region, pick or net leaves them out, as it leaves out the lands under a package.
- **Pours and planes are skipped.** A copper island much larger than the strips found in it, or one
  carrying a row of vias, is a pour; the report says how many it skipped on each layer. Copper wider
  than about ten times the distance to the nearest other copper layer is not read as a trace.

Each trace is cut once per width along its length, and each cut is the quasi-static solve
[Trace impedance](#trace-impedance) describes: the reference is found from the copper, and every
other conductor near the trace is held at ground.

**Junctions are reviewed too.** Where traces meet, and where a placed tee or cross sits whose arms are
too short to be traces, each arm is cut across where it leaves the junction and solved at its own width
the same way. A junction is a **tee** (three arms), a **cross** (four) or **plain**, and each arm
passes, warns or fails against the target as a trace does. The panel lists the junctions under the
traces, the canvas marks each one with a square in its worst arm's Z0 colour, and the verdict line
counts them: *PASS &mdash; 3 traces and 1 junction reviewed, …*.

### What it flags {#impedance-findings}

Every trace is **PASS**, **WARN** or **FAIL**. Each finding is a warning or a fail, and a trace takes
the worst of its findings: *look at this* is kept apart from *this is wrong*. Each is numbered on the
layer's map.

| Finding | Severity |
|---|---|
| **Z0 outside target ± tolerance** but inside ± the warning band, with the stretch it is out over and its range | Warning |
| **Z0 outside the warning band** | Fail &mdash; or a warning when it is electrically short (below) |
| **A broken return path** &mdash; the nearest layer under (or over) the trace stops covering it part of the way, while covering it elsewhere along the same trace | Fail, however short |
| **No return at all** &mdash; no copper covers the trace and nothing is beside it | Fail |
| **Copper only partly under the trace** &mdash; a plane edge running beneath it | Warning |
| **A reference step** &mdash; the reference changes from one layer to another along the trace; often a designed transition | Warning |
| **Part of the trace could not be solved** &mdash; not checked, which is not the same as wrong | Warning |
| **A test pad loads the line** &mdash; with a **Highest frequency** given, [below](#impedance-pads) | Warning or fail |

A layer cleared under the **whole** trace is not a fault; it is noted, with the deeper layer that
became the reference.

**A trace into its own via's clearance hole** is not a broken return either. Every trace that changes
layer crosses the hole each plane has round the via it ends on, past the reach of **Skip near vias**
when the hole is generous. When the nearest layer above or below is missing only from the trace's end
to the edge of that hole, the stretch is an **Info** note &mdash; *via clearance at the start,
853.8 µm* &mdash; outlined on the map in grey with no number, and left out of the trace's result and
its numbers. It is still a finding when it runs more than **0.1 mm** past the hole's radius (the trace
is over a slot, not just the hole), when it does not reach the trace's end (a gap mid-trace), or when
the via there is not on that trace. The report's *How it was measured* says the same.

<a id="impedance-pads"></a>**A round pad hanging off a trace** &mdash; a test point, joined straight to the trace's end, or
through a via at its end or on a stub too short to be a trace of its own &mdash; is read as the shunt it puts on the
line: the pad's capacitance to the plane it faces and, through a via, the via's inductance and the frequency the two
resonate at. The numbers are the [TESTPAD](components.html#testpad) model's, worked out from what is drawn: the
plane is the nearest one with copper under most of the pad, and each plane the via passes contributes through the
clearance drawn round it. With a **Highest frequency** given, the pad is judged there against the trace's own Z0, by
the |S11| it puts on the line: above **−20 dB**, or a via resonance below twice that frequency, it is a warning;
above **−10 dB**, or a resonance inside the band, a fail &mdash; *0.526 pF to 'Inner 1', the via 0.586 nH,
resonating at 8.82 GHz: |S11| −4.0 dB at 6 GHz on the 43.5 Ω line*. Its marker stands beside the pad, and selecting
the finding selects the pad. Below those, or with no frequency given, it is a note on the trace.

**Electrically short.** With a **Highest frequency** given, a stretch outside the warning band whose
electrical length is under **λ/20** there &mdash; a neck-down into a pad, a width step at a launch
&mdash; is a warning, not a fail. λ is taken from each cut's own effective permittivity, and the
finding says how long the stretch is in wavelengths, for example *0.018 λ at 6 GHz &mdash;
electrically short*. It applies to Z0 only: a broken return is never softened by being short. Left
blank, the rule is off and the report says nothing about it.

### Accepting a finding {#impedance-accept}

Some findings are known and fine: a connector trace 0.4 Ω past the band edge, a reference that steps
to an inner layer under a via fence on purpose, a ground clearance a launch was designed around.
Widening the tolerance to clear one of them clears it for **every** trace. **Accepting** it clears only
that one, with the reason on the record.

Expand a trace, select one or more of its findings (Ctrl- or Shift-click for several, across traces if
you like) and click **Accept…**. The panel asks **why** &mdash; a reason is required, because it is the
sentence a reader of a passing report relies on &mdash; and applies it at once: the counts and the
trace's verdict change without running again. The finding stays in the list, marked **ACCEPTED** with
the reason and the date; it simply no longer counts against its trace. **Un-accept** on a selected
accepted finding counts it again.

- **An acceptance belongs to the trace, not to the stretch.** It is stored against the trace's two end
  points and the kind of finding, so changing the tolerance, the warning band or the scope does not
  undo it. **Moving or re-routing the trace does**: a moved trace has not been reviewed.
- **It does not cover a worse finding.** A Z0 finding is accepted at the trace's worst Z0 at the time;
  a later run further from the target shows the finding again, un-accepted, saying both &mdash;
  *Accepted at 55.5 Ω, now 58.1 Ω*.
- **An acceptance that matches nothing** in a run &mdash; the trace moved, or the finding is gone
  &mdash; is listed under **Accepted, but matched nothing in this run** with the finding's text as it
  was, and stays on the layout until you remove it with its **✕**. One whose trace is outside this
  run's scope is neither applied nor listed.

Acceptances are saved in the `.clay` beside the design-rule waivers, mark the layout modified, and are
not undone by **Undo**. [`circuitrf impedance`](cli.html#impedance) applies them too.

### The report {#impedance-report}

- **A summary page** &mdash; always one page. It leads with the **verdict** and its counts &mdash; for
  example *PASS &mdash; 3 traces reviewed, 3 pass (1 with an accepted finding), 0 warnings, 0 failures*
  &mdash; then, when the results were stale when exported, a line saying the layout had changed since
  the run; then the **scope**, the target and pass band &mdash; which traces
  were reviewed and how many on each layer were outside it &mdash; the technology and its `.ctech`
  file, the layers, the counts of traces that pass, warn and fail, and per layer the traces outside the
  scope and the worst excursion from the target. With a highest frequency given, *How it was measured* states the λ/20 rule and the frequency. On the right is the **stackup** the traces were solved against, drawn exactly as the
  technology editor's Stackup tab draws it, with each layer's thickness, εr, tanδ and conductivity.
- **A map page per layer**: the layer's copper in grey with every trace drawn over it **coloured by its
  Z0** &mdash; green inside the pass band, blue below, red above &mdash; its id, and a numbered marker at
  every finding: **filled** for a fail, **hollow** for a warning, orange for Z0 and purple for the
  return path. The colour scale marks the pass band, and the warning band's edges as a lighter pair of
  ticks. A trace outside the scope is plain grey copper &mdash; no colour, no label, no marker. Where
  markers are too close to draw apart, one **badge** &mdash; a rounded label instead of a disc &mdash;
  stands for them, carrying their numbers (*232, 234–235, 239 +4*); the list after the table has each
  in full.
- **A table per layer**: each trace's start and end, what each end is, its length and width, its line
  **type**, Z0 min, max and average, the share of its length inside the band, its reference layers,
  PASS, WARN or FAIL; then the layer's **junctions**, one row per arm with its trace, width, Z0 and
  result; and then the numbered findings in full, each with its severity &mdash; or **ACCEPTED** and its
  reason.
- **A layer with no trace reviewed has no pages** &mdash; a plane, or a layer nothing in the scope
  reaches: its row in the summary page's **By layer** table says so.
- **Accepted findings**, when there are any: each accepted finding with its trace, its text, the reason
  and the date, and any acceptance that matched nothing in this run. An accepted finding is also still
  on its layer's map, **grey with a check** where its number would be.

#### The Summary {#impedance-report-summary}

A whole-board review of a large board finds thousands of findings, most of them the same few kinds
repeated trace after trace, and Full writes each one out. **Summary** has the same summary page, then per
layer:

- **The map**, coloured as in Full, with **one marker per trace** at its worst finding, carrying how many
  findings that trace has. Traces close together share one **badge** with their findings counted. Ids
  are shown only on traces that fail.
- **The findings, grouped**: one row per kind of finding, result (FAIL or WARN) and **width class**
  &mdash; the classes [Choosing the traces](#impedance-scope) lists &mdash; with how many are open, how
  many are accepted, the worst of them (the largest Z0 excursion, the longest broken return, the
  smallest coverage, the largest pad |S11|) and the traces they are on. Every finding on the layer is in
  one row, and the **Total** row says how many.
- **The 20 worst findings in full**, failures first and then by Z0 excursion, written exactly as Full
  writes them, so the numbers you quote are on the page.
- Passing traces and junctions are **counted** in a line, not listed.

Accepted findings are counted in their group's **Accepted** column and listed on the Accepted findings
page as in Full.

The **type** is microstrip, **GCPW** (grounded coplanar waveguide: a plane below and ground beside it on
both sides), **GCPW 1-side**, stripline or CPW. A trace that is **both** along its run &mdash; grounded CPW
where the pour runs beside it and microstrip where the pour falls away &mdash; lists both, with the share
of its length each covers, for example *GCPW 73%, Microstrip 27%*.

**Units** are the layout's own &mdash; whatever the layout's unit is set to (µm, mm, mil…) &mdash; and every
page's footer says which, beside Z0 in Ω.

The same analysis and the same PDF are available headlessly as
[`circuitrf impedance`](cli.html#impedance).

## Layout Review {#layout-review}

A **Layout Review** is help for a person reviewing a board. A [design-rule check](#drc) answers whether
the artwork meets the process's rules, pass or fail; a review marks the places an experienced reviewer
would want to look at, each with a sentence saying why, and leaves the judgement to the reviewer. A
finding is never an error: you either change the artwork or accept the finding with a reason.

The checks are built in, each with thresholds you can change. They find every layer they need by its
**role** rather than by its number &mdash; copper from the stackup, soldermask and paste from the board's
two surfaces, drills from the stackup's vias, the board edge from the board-outline layer, ground from
the conductor flagged as the ground reference &mdash; so one review runs unchanged on a shipped
technology, an imported board's and one you built. A check whose layer the technology lacks is reported
**not run**, with the one change that would let it run, and the other checks carry on.

| Check | What it marks | Threshold (default) |
|---|---|---|
| Via in pad | A via inside an SMD pad &mdash; solder wicks down the barrel. A via in a thermal pad is not marked here, and a pad centred on its one drill is a through-hole pin, whose drill is its hole. | Thermal pad area (2 mm², shared by the four pad checks) |
| Ground via distance | A ground pad with no ground via within the distance, pad edge to barrel edge, saying how far the nearest one is. A pad in a ground pour is judged the same way: the pour's stitching vias count when they are close enough. | Maximum distance (0.5 mm) |
| Shared ground via | A ground via that is the only one within the maximum distance of two or more ground pads. | The maximum distance above |
| Thermal vias | A thermal pad &mdash; a ground pad of at least the thermal pad area, such as the exposed paddle under a power stage &mdash; with fewer ground vias under it than one per maximum via pitch squared, or none &mdash; at the default, one via per 1 × 1 mm of pad. A via counts when its barrel lies at least half inside the pad. A finding gives the pad's own average pitch. | Maximum via pitch (1 mm) |
| Stacked traces | Two traces on adjacent copper layers, on different nets, running on top of each other for at least the minimum length &mdash; broadside coupling between two signals. Layers with another copper layer between them are not compared, because that layer shields them; two traces of one net (a doubled supply trace) are not marked, and only stretches where the two run the same way count, so a crossing is never a finding however wide either trace is. | Lateral margin (0: directly on top of each other), minimum length (1.0 mm) |
| Coupled lines | Two traces of different nets on one copper layer running side by side closer than the spacing, for at least the minimum length &mdash; coupling a process rule does not ask about. Both are in substrate heights (h, the dielectric between the layer and its ground reference), so one setting suits a thin board and a thick one. Only stretches where the two run the same way count. A coupler, a coupled-line filter or a differential pair is coupled on purpose: accept it. | Spacing (2 h), minimum length (5 h) |
| Copper at the board edge | Copper on any copper layer closer to the board edge than the pullback, marked where it lies inside that strip. The edge is read off the board-outline layer &mdash; a closed outline, or a line drawn round the board, whose centre line is the edge &mdash; and cut-outs in the board (slots, milled openings) are edges too. | Pullback (0.25 mm) |
| Traces at the board edge | A trace on any copper layer closer to the board edge than the distance, marked where it is. A pour pulled to the edge is not a trace and is left to the pullback check. A trace that runs to an edge-launch connector on purpose is a finding to accept. | Minimum distance (1.0 mm) |
| Edge stitching | A stretch of board edge with no ground copper along it, on the top and bottom copper; and a stretch with no ground via along it, between two vias further apart than the gap. Both are measured along the middle of a band along the edge, so a pour pulled back by the pullback still covers the edge. | Band width (1.0 mm), maximum gap (2.0 mm) |
| Impedance of the RF traces | Each trace in the [Impedance](#impedance-analysis) panel's saved choice of traces that does not pass &mdash; FAIL, WARN or unsolved &mdash; with its worst Z₀ against the target and what was found on it, marked where. Its line also says which traces were reviewed and how many passed. | None here: the target, bands, layers and traces are the Impedance panel's |

**A board or a die.** The checks above are for a printed circuit board. A MMIC die is reviewed with its own
checks, chosen with **Review as: PCB | MMIC** at the top of the Review panel and saved with the layout. Each is
something a reviewer looks at that the foundry's design rules do not check:

| Check | What it marks | Threshold (default) |
|---|---|---|
| Ground far from a via | A placed part's terminal on ground &mdash; a shunt capacitor's grounded plate, a transistor's source &mdash; with no via to the ground plane within the distance, straight from the terminal to the via's edge, saying how far the nearest one is. The line between them is inductance in series with that ground. | Maximum distance (100 µm) |
| Coupled lines | As on a board. | Spacing (2 h), minimum length (5 h) |
| Probe pads | The placed [GSG probe pads](components.html#gsgpad): one whose pitch is not the probe pitch &mdash; set it to the pitch of the on-wafer probes your lab has, so the layout matches them; a ground pad whose copper is not on ground (the probe's ground tip would land on floating metal); and metal within the clear approach in front of the pads, on their metal or any above it, where the probe comes in. | Probe pitch (150 µm), clear approach (100 µm) |
| Impedance of the RF traces | As on a board. | As on a board |

**Traces.** A trace is what [Impedance Analysis](#impedance-traces) reads as one: copper with
two long parallel edges facing each other, outside a pour, with its nominal width. The two trace checks
find traces exactly as that review does.

**Impedance.** Which traces are RF is the Impedance panel's question, so the review asks it there: it runs
the panel's saved review and lists its failing and warning traces beside the other checks. With no traces
chosen in the Impedance panel the check is not run &mdash; the review never analyses every trace on the
board, which on a large board takes minutes. A finding accepted in the Impedance panel is accepted here
with its reason, and a trace is accepted when every finding on it is.

**Pads, vias and ground.** A pad is copper exposed by a soldermask opening, on the top and bottom
copper; parts placed in the layout name a pad in a finding ("C209 pad 2") but do not decide which pads
there are, so the checks run the same on an imported board with no parts. A via is a drill on a layer
the stackup's via entries draw on, provided its holes are plated. Ground is the net of the copper on the
conductor flagged as the ground reference: where that conductor is drawn, the net of its largest copper
(the plane), and everything joined to it; where it is not drawn, the copper vias carry down to it. With
no ground reference flagged, the four pad checks and edge stitching are not run.

Findings are counted per check and grouped by layer. Which checks run, the thresholds you changed and
the findings you accepted are saved in the layout, so the next review reviews what the last one did; an
accepted finding is still reported, marked with its reason, and does not count. When the marked copper
moves, the acceptance no longer matches and is listed as stale.

### The Review panel {#review-panel}

**Design ▸ Review Layout…**, or the clipboard button beside Check Design Rules on the toolbar, opens the
Review panel on the layout you are working in. It follows the active layout, and each layout keeps its own
last result.

**Review as.** **PCB** or **MMIC** chooses which checks are listed and run: a board's or a die's. The
choice is saved with the layout. A result from the other kind of review stays on screen, marked as such,
until you run again.

**Checks.** One row per check, in the order of the tables above: tick the checks to run, and change a
threshold in its box. Lengths are in the layout's own unit, with the unit beside the box; type another
unit (`8 mil`) and it is converted. A threshold in substrate heights shows `× h`. A threshold you changed is marked, and the row's **Reset** puts every
threshold of that check back to its default. The impedance row shows the Impedance panel's target, bands
and traces, read-only, with a button that opens the [Impedance](#impedance-analysis) panel where they are
set. The checks you turned off and the thresholds you changed are saved with the layout, and changing them
is not an undoable edit.

**Run** reviews the layout with the ticked checks, saying which check it is on; **Cancel** stops it and
keeps the checks that finished, the rest marked *cancelled*. A run replaces the last result whole. After an
edit the result stays on screen and is marked out of date until the next run. One line in Messages says
how many checks found something, how many findings and how many accepted, and how many checks did not run.

**Results.** Each check's row gives its count, *No findings*, or *not run* with the change that would let it
run. Open a check to see its findings grouped by layer, each layer with its own count; a layer with more
than a hundred findings opens closed. Each finding shows its sentence and the measured value.

**On the canvas.** While the panel is open each finding is marked on the layout in the review's own
colour, distinct from the DRC markers; an accepted one is greyed. Where markers would land on one another
at the current zoom they are drawn as one badge carrying their count; zoom in and they separate. Selecting
a finding zooms the canvas to it; selecting copper on the canvas selects the findings marked on it.

**Accepting.** Select one or more findings, press **Accept…** and give the reason; a reason is required.
The finding stays listed, marked ACCEPTED with the reason, and stops counting &mdash; there is no re-run.
**Un-accept** counts it again. Accepting an impedance finding accepts its trace's findings in the
Impedance panel, so the two panels agree. **Show accepted** hides or shows accepted findings; hidden, they
are still counted on their check's row. Acceptances that matched nothing in this run are listed under
their own heading, each with a **Remove**.

**Export PDF** writes the result on screen as a PDF to send to the designer &mdash; there is no second run.
The button says how many pages it will be. The report is short whatever the board: a summary page with
one row per check (the thresholds it ran with, a changed one starred, its counts, or why it did not run),
then for each check with findings a page of maps, one for each layer it found something on, and one page
listing the first 20 findings and, after them, the accepted ones with their reasons. On a map the layer's copper is drawn
faded in its colour under the board outline, with each finding outlined and numbered as the list numbers
it; findings too close to draw apart are one badge carrying their count. A result marked out of date says
so on the PDF's first page.

Run a review with [`circuitrf review`](cli.html#review); its `-o report.pdf` writes the same report.

## The toolbar {#toolbar}

{{toolbar: layout}}

The groups, left to right: the **drawing tools**, which include the [Ruler](#ruler); the **edit and
boolean** operations; the **geometry snap** toggles — the magnet and the intersections toggle described
above; the **hierarchy navigation**
(push in, pop out); **EM setup and results**, which sit here rather than among the drawing tools because
all three are the same subject; and **save**.

<p class="small">See also: <a href="schematic-editor.html">Schematic Editor</a> ·
<a href="symbol-editor.html">Symbol Editor</a> ·
<a href="units.html">Units</a> · <a href="pcells.html">PCells</a> ·
<a href="em-setup.html">EM Setup</a> · <a href="file-formats.html">File formats</a> ·
<a href="wbond.html">wBond</a>.</p>
