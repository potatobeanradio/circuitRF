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
<li><a href="#schematic-flow">Schematic ⇄ layout</a></li>
<li><a href="#geometry-snap">Geometry snap</a></li>
<li><a href="#ruler">The Ruler</a></li>
<li><a href="#interchange">Interchange</a>
  <ol>
  <li><a href="#gdsii">GDSII</a></li>
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
happens to be open. This matters because the starter technologies use the same low layer keys
starting at (1,0), so resolving against the wrong one would silently reinterpret every layer in the
design.</p>
</div>

Two starter technologies ship. They differ **only in data**, never in code path:

| | PCB starter | MMIC starter |
|---|---|---|
| Display unit / snap | mil / 1 mil | µm / 5 nm |
| Layers | Top &amp; Bottom Copper, Soldermask ×2, Silk ×2, Drill, Outline | Metal1, Metal2, Via, Resistor, Cap Dielectric, Nitride, Substrate, Backside Via |
| Stackup | 1.6 mm FR-4, ε<sub>r</sub> 4.4, tanδ 0.02, 1 oz copper, bottom ground | 100 µm GaAs, ε<sub>r</sub> 12.9, tanδ 0.0006, 3 µm plated gold, backside ground |
| Primary interchange | Gerber + Excellon | GDSII |

There is also a third: **MMIC GaAs + MIM**, which is the MMIC starter plus the three stackup entries
a [thin-film capacitor](stackup.html#mim) needs — a `MIM Metal` plate, a `MIM Dielectric` under it,
and a `MIM Via` up to Metal2 — with two matching drawing layers. Everything else is identical, and
the layer keys a design already uses mean the same thing on both.

**Pick the plain starter for airbridge work and the MIM one for capacitor work**, because a capacitor
dielectric between the two metals is not free: an airbridge post between them then crosses a
dielectric interface, which an EM run refuses outright, and a line on Metal2 shifts by about 3% in
Z₀. That is exactly why the MIM entries are a second technology instead of being added to the
starter — see [A thin-film (MIM) capacitor](stackup.html#mim), which also explains why you should
not read a capacitance off a MIM run yet.

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
diagonal gap between two Manhattan traces is exactly the measurement you most want. Geometry snap
outranks the Shift constraint when a snap feature is in tolerance, because a snapped endpoint is a
stronger statement of intent than a held modifier. A ruler whose endpoints coincide after snapping is
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

**File ▸ Import** offers GDSII, DXF and Board; **File ▸ Export** offers GDSII, DXF, Gerber and Board.

**Every export says what it could not carry at full fidelity before it writes anything**, and that
preview is produced by running the *real* write into a null stream — so it can never disagree with what
lands on disk. Imports report the same class of thing afterwards, in the Messages panel: what was
skipped, by type, with a count.

A layer is identified by its integer **(layer, datatype)** pair, not by its name — that is GDSII's model
and it is the right one, since names are for humans and change, while the numeric pair is what a fab's
process assumption is keyed to. DXF and board files are name-keyed instead, so bringing one in goes
through an explicit name↔pair mapping held in the technology; one shared mapping dialog serves every
import that needs it. Gerber has no layer concept at all: one file per layer, mapping declared at
export.

### GDSII {#gdsii}

Import and export. **Hierarchy is preserved** — one structure per cell, instances stay instances, and a
board with four hundred identical parts stays four hundred instances rather than four hundred copies of
geometry. Curved primitives are flattened to polygons and holes are keyholed, both because the format
has no other representation; the export report counts each. Vias export as barrel plus landing pad, and
a via with no landing layer exports its barrel only and is named in the report.

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

Export only — these are fabrication outputs, and nothing reads them back in. One Gerber file per layer,
plus an Excellon drill file. Arcs are native (`G02`/`G03`) and a hole is a clear region, so neither is a
lossy conversion; the pre-flight report lists only genuinely structural changes. A bare circle drawn
directly on a drill-function layer still contributes a drill hit — it is never silently dropped — but it
is *unpaired*: no matching pad, no annular-ring data, and the report suggests **Convert to Via** for it.

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

#### What comes across

Board-level geometry and footprint geometry are both handled; a footprint's items are in its own frame
and compose through its placement, at an arbitrary angle, with a back-layer part being a mirror combined
with that angle.

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
| `gr_text` | a label |
| `zone` → `filled_polygon` | polygons, with their holes |
| `footprint` / `module` | one generated cell per distinct definition, one instance per placement |
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
minimum enclosure, minimum overlap and metal density; each names the layer (or the layer pair) it
applies to and the value it requires. `.ctech` files carry them, so a kit's own rules arrive with the
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
<span class="label">DRC, not LVS</span>
<p>circuitRF checks the <strong>geometry</strong> against the process rules. It does not compare the
layout against the schematic: there is no layout-versus-schematic check, and the connectivity the DRC
computes internally exists to tell one net's shapes from another's, not to verify a netlist. Two
different nets overlapping is a short, and finding it is an LVS job circuitRF does not do.</p>
</div>

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
