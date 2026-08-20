---
title: The Layout Editor
slug: reference/layout-editor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Layout editor
lede: Drawing artwork: the technology, the tools, snapping, and the schematic it belongs to.
---

The layout view holds a cell's **physical geometry** — the shapes that get manufactured. It serves two
consumers that pull in slightly different directions, and it is worth knowing which one you are serving
at any moment:

- **Fab handoff.** GDSII to a semiconductor fab, Gerber plus drill to a PCB house, DXF to a mechanical
  flow. Here the layout is a *document*: fidelity, units and layer mapping are everything.
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
<li><a href="#schematic-flow">Schematic ⇄ layout</a></li>
<li><a href="#geometry-snap">Geometry snap</a></li>
<li><a href="#interchange">Interchange</a></li>
<li><a href="#drc">Design-rule checking</a></li>
<li><a href="#toolbar">The toolbar</a></li>
</ol>
</nav>

## Orientation {#orientation}

{{ui: layout-editor}}

A layout is a **view of a cell**, exactly as its schematic and its symbol are. The cell folder holds
`schematic/`, `symbol/` and `layout/` side by side, and a cell need not have all three. The project tree
shows them together; opening one opens a document tab. The other two views have their own editors and
their own pages: the [Schematic Editor](schematic-editor.html) for the electrical contents, and the
[Symbol Editor](symbol-editor.html) for the glyph an instance draws.

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
- the **default display unit and snap grid** for layouts created against it;
- the **DRC rules**.

Technologies live in a `tech/` folder at the workspace root, and the workspace records which is the
default. A layout stores a relative reference to one, or leaves it blank to mean "the workspace
default".

<div class="callout warn">
<span class="label">"The workspace" means the document's workspace</span>
<p>A blank technology reference resolves against the workspace containing <em>that layout file</em> —
found by walking up from the file to the nearest <code>.cws</code> — not against whichever workspace
happens to be open. This matters because both starter technologies use the same layer keys
(1,0)–(8,0), so resolving against the wrong one would silently reinterpret every layer in the
design.</p>
</div>

Two starter technologies ship. They differ **only in data**, never in code path:

| | PCB starter | MMIC starter |
|---|---|---|
| Display unit / snap | mil / 1 mil | µm / 5 nm |
| Layers | Top &amp; Bottom Copper, Soldermask ×2, Silk ×2, Drill, Outline | Metal1, Metal2, Via, Resistor, Cap Dielectric, Nitride, Substrate, Backside Via |
| Stackup | 1.6 mm FR-4, ε<sub>r</sub> 4.4, tanδ 0.02, 1 oz copper, bottom ground | 100 µm GaAs, ε<sub>r</sub> 12.9, tanδ 0.0006, 3 µm plated gold, backside ground |
| Primary interchange | Gerber + Excellon | GDSII |

A layer's colour is **literal user data**, not a theme role: "M2 is teal" is a fact about the process
that must survive a light/dark switch and must match what you see in your other tools. The editor's own
chrome — background, grid, rulers, selection, handles — does follow the theme.

**A missing technology does not block you.** Layers render from a generated fallback palette keyed by
their (layer, datatype) pair, a warning is posted, and editing proceeds. The one thing it *does* block
is an EM run, which refuses rather than inventing a stackup.

## Drawing and editing {#tools}

**Drawing tools:** Select · Rect · RoundedRect · Circle · Polygon · Curve · Path · Label · Port ·
Instance-place · Array · Ruler.

Curves, circles and rounded rectangles are **first-class primitives** — they are stored as curves, not
as pre-flattened polygons — and are flattened on demand, always with an explicit tolerance prompt, and
automatically on GDSII export.

**Edit operations:** boolean **Union** (grouped per layer — one result per distinct layer in the
selection), **Intersect / Difference / XOR** (which need a same-layer pair to enable), **Size**
(grow or shrink by a signed offset), **Scale** (a numeric factor or a target size, plus bounding-box
handles: corner for uniform, side for one axis), **Slice**, **Align and distribute**, **Move to layer**,
**Set net**, **Flatten to Polygon…**, **Flatten Hierarchy** and **Group into cell**.

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

## Selection and vertex editing {#selection}

**Repeated clicks at the same point cycle through overlapping shapes.** The order is layer Z-order
descending, then *ascending area* — so a small shape sitting on a large one is reachable, which is the
case that actually matters. The status bar shows where you are: `Rect · M2 · 2 of 5`. Alt-click is the
explicit "next candidate" for anyone who expects it.

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

Self-intersection is allowed during a drag, flagged on release, and offered an automatic repair — it is
not rejected mid-gesture.

**Pasting between cells** carries the source layout's own resolution and layer definitions with it. Same
DBU pastes as-is; a different DBU rescales by the exact ratio, and warns through Messages naming the
offending shapes if the ratio would round. Layers absent from the destination technology are offered for
adding or mapping — never silently dropped.

## Hierarchy, instances and arrays {#hierarchy}

An **instance** references another cell's layout view. **Arrays** (rows × columns at a pitch) are stored
as one object, which is what makes a via farm or an MMIC unit-cell array nearly free to render. Cycles
are detected at edit time, not discovered at render time.

**Push-in / pop-out** navigation works as it does in the schematic: descend into a sub-cell, edit it in
place, come back out. **Flatten** (one level or all levels) and **Group into new cell** are the two
operations you will reach for constantly.

## Schematic ⇄ layout {#schematic-flow}

Two commands under the **Design** menu move work between the two views. They are deliberately symmetric,
and **neither ever runs by itself** — there is no save hook, no open hook, no activation hook. They run
when you invoke them and at no other time.

### Update Layout from Schematic

<kbd>⌘U</kbd>. Walks the schematic's instances, resolves each cell's layout view, and emits a starting
layout:

1. Each component instance resolves its layout view. A **PCell resolves too** — its layout is generated
   rather than stored (see [PCells](pcells.html)).
2. Components with no layout view at all — VAR, MEAS, Ground, and any un-laid-out cell — are **reported
   to Messages and skipped**, not silently omitted.
3. Instances are placed in a packed, non-overlapping arrangement, roughly following schematic order.
4. Net names are carried onto instance pins, and a **ratsnest** is drawn: thin straight lines on a
   system layer between pins sharing a net. That is the guide that makes manual routing tractable.

<div class="callout warn">
<span class="label">There is no auto-router, and there is not going to be one</span>
<p>Step 3 places parts. It does not route them. Auto-routing is a separate multi-month product and
pretending otherwise would set the wrong expectation.</p>
</div>

**Re-running updates, it does not duplicate.** Each generated instance remembers the schematic
component's stable id, so a second run keeps and leaves in place what is already there, adds what is
new, and *reports* — never auto-deletes — what no longer exists in the schematic. You will hand-place
after the first generation and run it again after a schematic edit; a generator that stomped your
placement would be one you used exactly once.

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

<div class="callout note">
<span class="label">What cannot be represented in the other view</span>
<p>Neither direction wires anything: <strong>Update Schematic from Layout places and updates components
and does not draw nets.</strong> And hand-drawn artwork in a layout has no schematic counterpart at all —
only <em>instances</em> cross the boundary, never loose shapes. Both cases are reported, so the omission
is visible rather than silent.</p>
<p>A layout-first PCell has no existing schematic parameter to inherit a unit from, so a newly created
component's length-dimensioned parameters take the <strong>technology's own default display unit</strong>
— mil on a PCB, µm on an MMIC die.</p>
</div>

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

## Interchange {#interchange}

GDSII, DXF and Gerber plus Excellon drill. A layer is identified by its integer **(layer, datatype)**
pair, not by its name — that is GDSII's model and it is the right one, since names are for humans and
change, while the numeric pair is what a fab's process assumption is keyed to. DXF is name-keyed, so DXF
import and export go through an explicit name↔pair mapping held in the technology. Gerber has no layer
concept at all: one file per layer, mapping declared at export.

## Design-rule checking {#drc}

Minimum width and minimum spacing per layer, declared in the technology. Violations are reported to the
Messages pane and marked on the canvas. Rules come from the technology, so a design retargeted to a
different process is checked against that process's rules without anything being re-entered.

## The toolbar {#toolbar}

{{toolbar: layout}}

The groups, left to right: the **drawing tools**; the **edit and boolean** operations; the **geometry
snap** toggles — the magnet and the intersections toggle described above; the **hierarchy navigation**
(push in, pop out); **EM setup and results**, which sit here rather than among the drawing tools because
all three are the same subject; and **save**.

<p class="small">See also: <a href="schematic-editor.html">Schematic Editor</a> ·
<a href="symbol-editor.html">Symbol Editor</a> ·
<a href="units.html">Units</a> · <a href="pcells.html">PCells</a> ·
<a href="em-setup.html">EM Setup</a> · <a href="file-formats.html">File formats</a> ·
<a href="wbond.html">wBond</a>.</p>
