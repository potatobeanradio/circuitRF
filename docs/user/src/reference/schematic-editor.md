---
title: The Schematic Editor
slug: reference/schematic-editor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Schematic editor
lede: Where a circuit is drawn: placing components from the Library Palette, wiring them, editing their values, and setting up the analysis that simulates the result.
keywords: schematic, draw, wire, place, net, instance, hierarchy, test bench, testbench
---

The schematic is the **electrical** view of a cell — what the circuit *is*, as opposed to what it
looks like on a symbol or what gets manufactured in a layout. It is the view you spend most of your
time in, and it is the only one an analysis runs on.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#orientation">Orientation</a></li>
<li><a href="#palette">The Library Palette</a></li>
<li><a href="#placing">Placing a component</a></li>
<li><a href="#wiring">Wiring, values and labels</a></li>
<li><a href="#context-menu">The context menu</a></li>
<li><a href="#hierarchy">Hierarchy: putting one schematic inside another</a></li>
<li><a href="#views">The other three views</a></li>
<li><a href="#analyses">Simulating: an Analysis</a></li>
<li><a href="#find">Finding a component</a></li>
<li><a href="#toolbar">The toolbar</a></li>
</ol>
</nav>

## Orientation {#orientation}

{{ui: schematic-editor}}

A schematic is a **view of a cell**, exactly as its [symbol](symbol-editor.html) and its
[layout](layout-editor.html) are; the cell folder holds `schematic/`, `symbol/` and `layout/` side
by side and a cell need not have all three. Opening one opens a document tab.

You do not need a cell library to start. With no workspace open, **File ▸ New Schematic** opens a
standalone scratch sheet you can wire up and simulate immediately, and save into a workspace later if it
turns out to be worth keeping. With a workspace open, **New Schematic** and **New Layout** create a new
**cell** holding that view instead, so nothing lands outside a cell.

Two things about the canvas are worth knowing before you draw on it:

- **Connection is exact, not proximity.** Every pin, wire vertex and junction lands on the coarse
  **connection grid**, and two things are joined only when they occupy the *same* grid point. A
  second, finer **authoring grid** positions labels and annotations. That rule, and what happens
  when you paste between designs with different grids, is [Grid &amp; Connectivity](grid.html).
- **Standard-library parts are drawn vertically.** R, L, C, the sources, Term and Ground all put
  port 1 at the top and port 2 at the bottom, so a horizontal signal path means rotating them as you
  place them. That is the normal workflow, not a workaround — see
  [Orientation convention](symbol-editor.html#orientation).

## The Library Palette {#palette}

{{ui: library-palette}}

The palette is where every placeable part lives. It has two controls above the tiles and they do
different jobs:

- **The Type menu.** The button shows what is selected, as a path such as
  *Library ▸ Capacitors ▸ Series*; in a narrow panel the leading levels give way to *…*. Clicking it
  opens a menu. **All** is the default and shows the whole built-in library in the
  palette's own pinned order — the parts you reach for most, first. **All - Alphabetical** is the
  same set sorted by name, **Common** is a short curated set, and **Recently Used** is what you have
  placed lately, vendor library parts included. Below those come the built-in categories — Lumped,
  Devices, **System**, Nonlinear, Sources, Terminals, Transmission Line, Microstrip, Matching, Data
  Files. Then come one sub-menu per imported kit holding the kit's own groupings, one sub-menu per
  [vendor part library](pdk-integration.html#vendor) following the library's own folders, and
  **Add Vendor Library…**. Every sub-menu starts with **All in …**, so a folder can be chosen as well
  as opened, and a folder holding only one thing is shown as that thing (*Inductors ▸ Series*). A
  library's files show how many parts each holds. See [PDK integration](pdk-integration.html) for
  where kit entries come from.

  **System** sits directly after Devices, because a system block is a signal-path part and a user who
  has found Devices is one row away from the blocks they want. It holds the ideal blocks a system
  block diagram is drawn out of — circulator, coupler, hybrid, balun, switch, amplifier, attenuator,
  filter, duplexer — plus the two mixer tiles, which keep Devices as their primary category and
  appear under both. See [System Components](system-components.html).
- **The search box.** Matches a part's display name and its search terms, and a kit part's kit name
  as well. Searching inside a built-in category narrows within it; searching from All searches
  everything, built-ins and kits together. From All, a vendor library with matching parts adds one row
  at the top — *312 parts in Caps match* — which opens that library with the search kept.

The tile grid is a **reflow grid**: the number of columns follows the panel's width, so widening the
palette gets you more tiles per row rather than bigger tiles.

### A vendor library's parts {#palette-vendor}

Choosing a vendor library, or a folder or file inside it, replaces the tiles with a list of parts. Each
row shows the part number and its value, then its description and frequency range. The list is sorted
by value. Click **Part**, **Value**, **Description** or **Range** above it to sort by that, and click
again to reverse.

The search box filters the list. Separate words with spaces; every word must match, in any order. A
word matches when it appears in the part number, the description (with or without its brackets, so
`50V` finds `50[V]`), the file or the folder name. Two kinds of word are read more exactly:

- **A value** — `10p`, `10pF`, `2.2n`, `4n7` — finds the part whose stated value is that, however the
  library wrote it, and `10 pF` with a space is the same word. A unit, when you give one, must match
  the part: `10nH` finds inductors only.
- **A case size** — `0603` — is the imperial size the library states, and `1608M` the metric one. The
  library writes both (`1608M(0603)`), and `0603` never finds a 0201 part through its metric `0603M`.

So `0603 10pF C0G 100V` narrows a library to the parts you want. Typing never waits on the disk; while
a library is still being read after the workspace opens, the list says *Indexing …* instead.

<kbd>↓</kbd> and <kbd>↑</kbd>, from the search box or the list, arm the next or previous part, so you
can type, step to the part and click on the schematic without touching the list. The
<span class="ui">?</span> button at the end of the column headers opens the documents the library
came with (each in your system's own viewer), then circuitRF's help on vendor libraries.

A row places a part exactly as a tile does: click to arm, or drag onto the schematic. The part is a
[SPICE Model](components.html#spicemodel) running from the library's own file. A capacitor or inductor
draws as the ordinary C or L symbol, is named C1, L1, …, and is labelled with its part number, its name
and, on a line of its own, its value (`C = 10 pF`). It arrives with the
[footprint](footprints.html#imported) of the case size its library states, also in a workspace that has
no technology yet.

A library can also hold [model folders](model-library-format.html), listed in the same rows. The
search, sort and **?** button work as they do for SPICE parts, and a model's **?** opens the documents
in its own `docs/` folder. In the Type menu, a folder of models is one entry that lists them all. A model places as a
cell: it draws the glyph its `part.cpart` names (R, C, L or a transistor, with the transistor's pins
matched to the model's pin names), or the model's own symbol when it names none. It is named R1, C1, L1
or Q1 for those glyphs, and arrives with the model's parameters at their defaults. If the library goes
missing, the part keeps drawing its glyph with the not-found mark.

## Placing a component {#placing}

There are two gestures, and they place the identical thing — a kit part in particular resolves
through the same path either way, so a drag and a click can never disagree about what you get.

<ol class="steps">
<li><strong>Click a tile</strong> to arm it. The tile highlights, and a ghost of the symbol follows the
cursor over the canvas. Click on the canvas to drop one. <strong>The tool stays armed</strong>, so
click again to place another — this is the fast way to lay down five capacitors. Click the tile a
second time, or press <kbd>Esc</kbd>, to disarm.</li>
<li><strong>Or drag the tile onto the canvas.</strong> The ghost follows the drag, snapped to the
connection grid, and the component lands where you release. A drag places exactly one part and
leaves nothing armed, which is what you want when you are placing one part in the middle of doing
something else.</li>
</ol>

While a placement is armed, <kbd>R</kbd> rotates the ghost 90° counter-clockwise and
<kbd>Shift</kbd>+<kbd>R</kbd> clockwise, <kbd>M</kbd> mirrors it left to right and
<kbd>Shift</kbd>+<kbd>M</kbd> top to bottom — the same keys that rotate and mirror a selection when
nothing is armed. The part lands the way the ghost faces, so an IProbe dropped onto a wire already
points its arrow the way you want the current measured.

**Dropping a part along a wire puts it in series.** When two of its pins land on one straight stretch
of wire, the wire between them is removed, so the part breaks into the run instead of being shorted by
it. That holds for a two-pin part, for an MTEE whose pins 1 and 2 lie along the wire (pin 3 is left for
you to wire) and for an MCROSS along the wire between its two pins on it. Nothing is cut when anything
else meets the wire between the pins — a junction, another wire, another part's pin, a net label — or
when the part's other pins also touch the wire.

### Copy and paste {#paste}

<kbd>Ctrl</kbd>+<kbd>V</kbd> (<kbd>⌘</kbd>+<kbd>V</kbd> on macOS) shows the copied parts as a ghost
that follows the cursor, snapped to the connection grid. Click to place them there; <kbd>Esc</kbd>
puts nothing down. Nothing connects to the ghost while it moves. **Edit ▸ Paste** does the same while
the cursor is over the canvas; from the menu with the cursor elsewhere, the parts land in the middle
of the view. A single part pasted along a wire goes in series, as a placed one does.

Parts keep their symbols when pasted into a schematic in another folder, so a part copied out of an
imported schematic arrives as its own symbol rather than a box. A net label sitting on a copied pin
comes with it; deleting a part takes the net label that sits on its pin, unless another pin or a wire
also meets it there.

<div class="callout note">
<span class="label">The ghost is the real symbol, not a stand-in</span>
<p>For a part from an imported kit the ghost is built by resolving the cell's own primary symbol, not
from its generic <code>SymbolKind</code>. Kit parts share one kind, so a ghost drawn from the kind
alone would show a plain box during the drag and then place the kit's real artwork — the drag showing
one thing and the result another.</p>
</div>

### Placing the parts of a parts table {#parts-table}

A parts list typed by hand is slow, and the one you have is usually a table in a datasheet, an
application note or a spreadsheet. Copy it, click the schematic, and press <kbd>Ctrl</kbd>+<kbd>V</kbd>
(<kbd>⌘</kbd>+<kbd>V</kbd> on macOS) — or drop a <code>.csv</code> file on the canvas. Every
resistor, capacitor and inductor in it is placed, named by its reference designator, with its value
and its case size as the footprint: one row of parts per type, below what is already on the sheet
(or at the point where you dropped the file). It is one step on the undo stack.

What it reads, row by row:

- **The reference** — <code>C4</code>, a list (<code>M11,M12,M16</code>) or a range
  (<code>C3-C5</code>), one part each.
- **The type** — a Type or Description column (<code>Capacitor</code>, <code>CAP CER …</code>,
  <code>C</code>), otherwise the reference's own letter (<code>C</code>, <code>R</code>,
  <code>L</code>, <code>FB</code>), otherwise the value's unit.
- **The value** — <code>100 pF</code>, <code>100pF</code>, <code>4k7</code>, or a number with its unit
  in the next cell.
- **The case size** — a Size, Case, Package or Footprint column: <code>0402</code>, <code>402</code>
  (a spreadsheet drops the leading zero), <code>C0805</code>, or a series code the case table knows.

Text copied out of a PDF works too: the words of a wrapped cell (<code>Capacito r</code>) and a
reference list that wrapped onto the next line are put back together. Rows marked
<code>DNP</code> or <code>--</code> are skipped. Anything it could not place — an IC, a ferrite bead
whose value is in ohms, a row with no type — is listed in the Messages panel with the reason, and so is
every reading worth checking: a bare <code>0402</code> is read as the imperial size, and says so,
because the same digits are also a smaller metric part.

<div class="callout note">
<span class="label">A best try, not an import</span>
<p>A table's columns are not a standard, so the parts are recognised by what each cell looks like
rather than by where it is. Check the Messages panel after a paste. A reference already on the sheet
is renumbered past every name the table uses, and the renames are listed there too. Case sizes become
footprints only in a workspace whose technology is a board.</p>
</div>

## Wiring, values and labels {#wiring}

**Wire** is <kbd>W</kbd> (or the toolbar button). Click from one pin to another; <kbd>Enter</kbd>
finishes the wire in progress and returns you to Select, <kbd>Esc</kbd> cancels it. **Select** is
<kbd>S</kbd>, and <kbd>Esc</kbd> from any tool comes back to it.

Clicking a wire selects one straight run of it, not the whole wire. Where a pin, another wire, a
junction dot or a net label joins that run partway along, the click selects only the stretch between
the junctions on either side of it. <kbd>Delete</kbd> then removes that stretch, and the rest of the
wire keeps every connection it had. To disconnect one branch of a supply line, click the stretch
between the two parts and delete it.

Double-click does three different things depending on what is under the cursor, and the distinction
is worth learning because it is the fastest edit in the application:

| Double-click on | What opens |
|---|---|
| A component's **value label** (`C = 1 pF`) | An **inline edit box** over the value itself. Type `1.2 nH`, `50 Ω`, `2 GHz` and press <kbd>Enter</kbd>. |
| A component's **body** | The full **parameter editor** for that instance — every parameter, not just the displayed ones. |
| A **wire** | A **net label** on that wire, so you can name the net and refer to it in a measurement. |

Hold <kbd>Alt</kbd> (<kbd>Option</kbd> on macOS) while double-clicking a wire to **select the whole run
of wiring** instead: every wire joined to the clicked one, in every direction — round corners, through
junctions, and through pins where several wires meet — until the wiring ends at the components. A part
ends the run, because the wiring on its other side is a different node; a net label does not carry it to
another label of the same name. <kbd>Delete</kbd> then removes exactly that run.

Arrow keys nudge the selection by one connection-grid step, or five with <kbd>Shift</kbd> held.
<kbd>F5</kbd> begins **Move Labels**, for pushing a crowded label block clear of the artwork without
moving the component it belongs to.

Values are expressions, not just numbers: a `VAR` block on the sheet declares variables that any
component value can refer to, and the same expression language drives sweeps and measurements. See
[Expressions](expressions.html) and [Measurements](measurements.html).

## The context menu {#context-menu}

{{ui: schematic-context-menu}}

Right-clicking a component gives you the operations that apply to *it*: **Edit Parameters**, rotate
and mirror, the two label commands, **Labels ▸** for choosing which parameters show on the sheet,
**Disconnect**, **Copy** and **Delete**. Items are **disabled rather than hidden**, so their
positions stay put and you learn the menu by muscle memory.

Two more items appear only on a **cell instance** — **Push In** and **Open Cell in New Tab**. Those
two are genuinely absent on a resistor rather than greyed, because they are not operations a
resistor has; [Hierarchy](#hierarchy) below is what they are for.

**Right-clicking empty canvas gives a different, one-item menu: Pop Out.** Nothing on the component
menu applies when the click hit nothing, so the canvas menu carries only the one thing that always
does — going back up a level. It is greyed out, with the reason on its tooltip, when you are already
at the top of the schematic.

## Keys at a glance {#keys}

| Key | What it does |
|---|---|
| **S**, **W**, **Z** | Select, Wire, zoom box |
| **F** | Zoom to fit |
| **R**, **Shift+R** | Rotate the selection — or the part being placed — 90° counter-clockwise; clockwise |
| **M**, **Shift+M** | Mirror the selection — or the part being placed — horizontally; vertically |
| **G**, **T**, **P** | Place a ground, a Term, a Pin |
| **Q** | Disable the selection as an open circuit |
| arrows; **Shift**+arrows | Nudge the selection one grid step; five |
| **F5** | Move Labels |
| **Delete** or **Backspace** | Delete the selection |
| **Enter** | Finish the wire in progress |
| **Esc** | Cancel the current tool, or clear the selection |
| **Shift+G** | Cycle the snap mode |
| **Ctrl/Cmd+A** | Select all |
| **Ctrl/Cmd+C**, **Ctrl/Cmd+X**, **Ctrl/Cmd+V** | Copy, cut, paste ([at the cursor](#paste)) |
| **Ctrl/Cmd+=**, **Ctrl/Cmd+−** | Zoom in, out |
| **Ctrl/Cmd+]**, **Ctrl/Cmd+[** | Push into the selected cell instance; pop out |

The right-click menu shows the same keys beside **Rotate 90°** and **Mirror**.

## Hierarchy: putting one schematic inside another {#hierarchy}

**Hierarchy is drawing a circuit once and using it in many places.** A bias network, a matching
section, a whole amplifier stage — draw it as its own **cell**, then drop that cell into a bigger
schematic as a single component. The big schematic stays readable, and one edit to the cell reaches
every place it is used.

Two things have to be true before any of it works, and they are the two that trip people up:

- **Hierarchy needs a cell.** A schematic on its own is not reusable — only a *cell* can be placed
  into another schematic. Making the cell is step 1 below, and it is not optional.
- **The cell goes into a *different* schematic.** A cell cannot contain itself, so the schematic you
  place it in must be another one. Pushing in is then how you get back down to the first.

### Step by step: a cell inside another schematic {#step-by-step}

<ol class="steps">
<li><p><strong>Create the cell.</strong> <strong>File ▸ New ▸ New Cell…</strong>
(<kbd>⇧⌘N</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd>), give it a name, and circuitRF makes
the cell folder and opens its schematic. The command needs an open workspace — a cell is a folder
inside one — so if it is greyed out, open or create a workspace first
(<a href="workspace.html">The Workspace</a>).</p></li>

<li><p><strong>Draw the sub-circuit, and give it pins.</strong> Wire it up as you would any
schematic, then place a <strong>Pin</strong> at every point the outside world needs to connect to.
The pins <em>are</em> the cell's ports — the pin numbered 1 becomes port 1 — and a cell with no pins
places as a component nothing can be wired to.
<a href="pins-ports-terms.html">Pin, Port and Term</a> is worth reading once if that distinction is
new. Save with <kbd>⌘S</kbd>.</p></li>

<li><p><strong>Open the schematic that will use it</strong> — a different one, and a
<strong>saved</strong> one. A cell instance records where the cell is <em>relative to the schematic
holding it</em>, so a scratch sheet from <strong>File ▸ New Schematic</strong> has nowhere to record
it from; placing into one is refused with <em>"Save the schematic before placing a cell"</em> in
Messages. Save it into the workspace first, or start it as a cell of its own.</p></li>

<li><p><strong>Drag the cell out of the Project Tree and onto the canvas.</strong> That is the
placement gesture. Cells you author are <em>not</em> in the Library Palette — the palette carries the
built-in library and any imported kits, and your own cells live in the
<a href="workspace.html">Project Tree</a> instead. It lands as one component with your cell's pins on
it.</p>
<p>If the cell has no symbol yet, you are offered an auto-generated one, built from the pin count
from step 2. Accept it to keep moving; draw a proper one later in the
<a href="symbol-editor.html">Symbol Editor</a>.</p></li>

<li><p><strong>Wire it in and set its parameters</strong> like any other component. Double-clicking
its body opens the parameter editor for that instance.</p></li>

<li><p><strong>Push in to edit the cell from here.</strong> Click the instance once to select it,
then use any of:</p>
<ul>
<li><strong>Right-click ▸ Push In</strong> — the item appears only on a cell instance.
Right-clicking <em>empty canvas</em> offers <strong>Pop Out</strong> to come back up.</li>
<li>Toolbar button <strong>18</strong>, and <strong>19</strong> to come back out
(<a href="#toolbar">the toolbar</a> below numbers them).</li>
<li><kbd>⌘]</kbd> / <kbd>Ctrl</kbd>+<kbd>]</kbd> in, <kbd>⌘[</kbd> / <kbd>Ctrl</kbd>+<kbd>[</kbd>
out — also on the <strong>View</strong> menu as <strong>Push Into Cell</strong> and
<strong>Pop Out</strong>.</li>
</ul>
<p>You are now editing the cell's own schematic, <strong>in the same tab</strong>. A breadcrumb bar
appears above the canvas showing how deep you are (<code>X1</code>, the instance you came through);
every step in it is clickable, so you can jump straight back to any level rather than popping out one
at a time. Push in again from there to go deeper.</p></li>
</ol>

Once that round trip works, everything else about hierarchy follows from it:

- **An edit to a cell reaches every instance of it, live.** That is the whole point — and it is also
  the thing to be careful about, because pushing in through `X1` and changing a value changes `X2`
  too. They are the same cell.
- **Push In is not the only way in.** **Right-click ▸ Open Cell in New Tab** opens the cell as its
  own document instead, which is what you want when you are going back and forth and would rather
  have both on screen than one behind the other.
- **Depth is not limited to one level.** A cell can instance other cells, and the breadcrumb grows a
  step each time you push in. Elaboration flattens the whole tree into one netlist before an analysis
  ever runs, so depth costs you nothing at simulation time.
- **Parameters pass downward.** A cell can publish parameters that the parent sets per instance, so
  two instances of one cell can carry different values. See
  [Expressions](expressions.html) for how a cell evaluates them in its own scope.
- **Only the top schematic is simulated.** An analysis attaches to the **test bench** — the schematic
  you press Run on — and the hierarchy below it is flattened into that run. A cell in the middle of a
  tree has no analyses of its own.

## The other three views {#views}

The schematic is one of a cell's four views, and each is edited in its own editor:

| View | Editor | What it owns |
|---|---|---|
| **Schematic** | this page | the electrical contents — instances, nets, values |
| **Symbol** | [Symbol Editor](symbol-editor.html) | the glyph an instance draws, and where its pins sit |
| **Layout** | [Layout Editor](layout-editor.html) | the physical artwork, for fab handoff and EM |
| **3D** | [3D Editor](drawing-in-3d.html) | the solid model — packages, connectors, bond wires — for a full-wave 3D solve |

The symbol is what step 4 above places; the layout is what gets manufactured. A cell need not have
all four — the cell folder holds `schematic/`, `symbol/`, `layout/` and `3d/` side by side and any of them
may be absent.

Two commands under the **Design** menu move work between the schematic and the layout —
**Update Layout from Schematic** (<kbd>⌘U</kbd>) and **Update Schematic from Layout**
(<kbd>⇧⌘U</kbd>). Neither ever runs by itself; both are described in
[Schematic ⇄ layout](layout-editor.html#schematic-flow).

A part that has no layout view of its own still reaches the board: its **Footprint** parameter — the
last row of its parameter editor — names an SMT case size, an imported part or a `.clay` you drew,
and that is what Update Layout places. See [Footprints](footprints.html).

## Starting from a template {#templates}

**New Cell and the Project Tree's New Schematic offer a *Template* under the name field.** *(Empty)*
is pre-selected; any other choice writes a ready-made test bench — its components, wiring and
analyses — into the new schematic, which you then edit like any other.

| Template | What it sets up |
|---|---|
| FET Curve Tracer | A FET's DC I–V family, swept over V<sub>GS</sub> and V<sub>DS</sub>. |
| FET Harmonic Balance Sweep | A biased FET driven into compression over input power. |
| FET Loadpull Pursuit | A loadpull-pursuit search for a FET's optimum load. |
| FET S-Parameters | A biased FET between two ports, swept 1–10 GHz. |
| Two Port Stability | An SnP between two 50 Ω ports, swept 0.5–10 GHz, with its own Data Display. |

**Include Data Display** sits under the picker. It is ticked for a template that brings its own
Data Display, and greyed out for one that does not. Ticked, the display is written beside the
workspace's `.cws` and named after the new schematic (`Amp.cdd` for a cell called *Amp*), which is
the display a finished run opens. The first **Simulate** therefore fills it and brings it forward;
until then its source combo reads *not yet run*. If a Data Display of that name already exists it is
left alone, Messages says so, and the schematic is still created.

**Two Port Stability** is what to reach for first with an amplifier's or transistor's two-port data.
Set the SnP's **File** to your `.s2p` (Simulate refuses, naming the parameter, until you do) and the
SP1 sweep to the file's band, then Simulate. The display holds six plots: the source and load
stability circles, one Smith chart each and one circle per swept frequency; μ and μ′; |S11| and
|S22|; |S21| on the left axis with |S12| on the right; and Max Gain. Each is a built-in metric of the
run, defined in {{anchor: derived-metrics|Derived Metrics}}, so there is no expression to maintain. The
*S-Parameters* example's **FetStability** cell is this template, already pointed at a file.

## Simulating: an Analysis {#analyses}

**A simulation in circuitRF is called an *Analysis*, and it is configured before it is run.** An
analysis is not a property of the circuit — it attaches to the **test bench**, the top schematic you
actually simulate — so drawing a circuit does not by itself give you anything to run.

Open **Simulate ▸ Setup Analyses…** (the same list is also available as a dock panel), add the
analyses you want, then press **Run** — <kbd>⌘R</kbd> / <kbd>Ctrl</kbd>+<kbd>R</kbd>, or the ▶
button at the top of the list.

{{ui: analyses-setup}}

The example above is an ordinary two-analysis test bench: a **DC** operating point, and a
**Harmonic Balance** run wrapped in a parametric sweep of the drive level `Pin`. Reading the panel:

- The **checkbox** enables or disables a row. A disabled analysis stays in the design and is not run
  — the way to park an analysis without deleting it.
- The **badge** is the analysis type: `DC`, `SP`, `HB`, `LP`, `LPP`, and `SW` for a parametric sweep.
- A **sweep is indented under the analysis it wraps**, which is how the panel shows that `Pin` is
  sweeping `HB1` rather than standing on its own. Analyses run in the order listed.
- **Results file** names the one file this run writes into the workspace's shared `results/` folder.
  Leave it blank to use the schematic's own name; name it to keep a baseline instead of overwriting
  the current results.

Adding or editing a row opens the analysis editor, where the analysis is actually configured:

{{ui: analysis-editor-hb}}

The **Type** picker at the top decides everything below it — the body changes to the settings that
type has. Here it is the harmonic-balance body: the fundamental tone (given as the expression
`RFfreq`, resolved and previewed underneath), the unit, how many harmonics to retain, the
single-versus-multi-tone choice, an **Advanced** block for the convergence controls, and the
**Parametric Sweeps** section that wraps this analysis in one or more swept variables. The dialog
grows to fit whatever the chosen type needs and scrolls beyond that, which is why the sweep's own
Start / Stop / Step row runs off the bottom of the figure above.

Every analysis type, its full settings, and what it computes are in
{{anchor: simulations|Simulations}} — {{anchor: simulations#dc|DC}},
{{anchor: simulations#s-parameters|S-Parameters}},
{{anchor: simulations#harmonic-balance|Harmonic Balance}},
{{anchor: simulations#parametric-sweep|Parametric Sweep}},
{{anchor: simulations#loadpull|Loadpull}} and
{{anchor: simulations#loadpull-pursuit|Loadpull Pursuit}}.

<div class="callout note">
<span class="label">What a run produces</span>
<p>Every analysis returns a <strong>DataSet</strong> — a named bundle of labelled, unit-bearing
arrays — and the whole run goes into one <code>.npy</code> file. Open a
<a href="data-display.html">Data Display</a> to plot it, or read it straight into Python or MATLAB
(<a href="npy-export.html">Getting results out</a>). Post-processing a run into named quantities is
what <a href="measurements.html">Measurements</a> are for.</p>
</div>

<div class="callout tip">
<span class="label">The same run, headless</span>
<p>An analysis set up here runs identically from the command line — <code>circuitrf sparam</code>,
<code>dc</code>, <code>hb</code>, <code>lp</code> or <code>lpp</code> against an elaborated
<a href="netlist.html">netlist</a>, with <strong>Simulate ▸ Generate Netlist</strong> producing the
<code>.cnl</code>. See {{anchor: cli|The Command Line}}.</p>
</div>

## Finding a component {#find}

**Design ▸ Find Instance…** — <kbd>⌘F</kbd> / <kbd>Ctrl</kbd>+<kbd>F</kbd> — opens the **Instances**
panel for the schematic you are working in and puts the cursor in its search box, so you can type a
name straight away. The panel lists every component on the sheet by **Name**, **Type** and, where it
adds something, the **Cell/Part** it comes from, sorted so that `R2` comes before `R10`.

- **Type to filter by name** — any part of the name, upper or lower case. The **type** picker beside
  the box narrows the list to one kind of component; the two combine.
- **Double-click a row** (or press <kbd>Enter</kbd>) to **zoom to that component and select it**. A
  single click only highlights the row, so you can arrow down the list without the view jumping about.
  <kbd>Enter</kbd> in the search box goes to the highlighted row, or the first match.
- <kbd>Esc</kbd> in the search box puts you back on the canvas.

The panel always shows the schematic that has focus, and says which one in its header. Grounds, `VAR`
and `MEAS` blocks are left out — they are rarely what you are looking for, and a sheet's grounds would
bury its parts. The command is greyed out unless a schematic or a layout has focus; the panel itself is
also under **View ▸ Panels ▸ Instances**.

**Searching inside placed cells.** By default only the level you are looking at is listed. Tick
**Include sub-cells** and the panel also lists every component inside every placed cell, all the way
down, named by its path — `X1.X3.R5` is `R5` inside `X3` inside `X1`, so typing `x1.` lists everything
under `X1`. The top level appears at once; the rest arrives a moment later (the panel shows
*Searching…* meanwhile), and the window stays responsive while it does. With the box ticked the panel
lists the whole tab from its top level, wherever you have [pushed in](#hierarchy) to, so
double-clicking `X1.X3.R5` pushes down into `X1`, then `X3`, and zooms to `R5` — and the list stays put
for the next result. Unsaved edits in a cell you have open are included. A search that finds more than
100,000 components stops there and shows a `+` after the count. The box is remembered between sessions.

## The toolbar {#toolbar}

{{toolbar: schematic}}

Grouped by what you are doing, left to right:

- **View** — Zoom to Fit, a zoom box, and Zoom Out. <kbd>F</kbd> is the one to learn; a schematic
  is usually bigger than the window.
- **Draw** — Select, the Wire tool, and the three things you place constantly: **Ground**, **Term**
  (a numbered port with a reference impedance, for an S-parameter run) and **Pin** (a connection
  point of the cell you are drawing). [Which is which](pins-ports-terms.html) is worth reading once.
- **Text** — a note on the drawing: click where it starts, type, <kbd>Enter</kbd> to place it. It
  works as the [Symbol Editor's](symbol-editor.html) Text tool does: double-click a text to change
  what it says, and the Properties panel sets its size, style, colour, alignment and rotation. Text
  copies between a schematic and a symbol both ways: paste a selection copied in the other editor and
  its texts come across, at the same size and orientation.
- **Orient** — rotate 90° either way and mirror in either axis, applied to the selection.
- **Delete** — on the selection.
- **Snap** — cycles the snap mode. Electrical points always land on the connection grid whatever this
  says; the snap mode governs the authoring grid.
- **Disable** — mark selected components **open** or **short**. This takes an element out of the
  circuit without deleting it, which is what you want when you are bracketing a problem.
- **Save**.
- **Hierarchy** — push into the selected cell, or pop back out.

---

<p class="small">See also: <a href="symbol-editor.html">Symbol Editor</a> ·
<a href="layout-editor.html">Layout Editor</a> · <a href="grid.html">Grid &amp; Connectivity</a> ·
<a href="components.html">Components</a> · <a href="pins-ports-terms.html">Pins, Ports &amp;
Terms</a> · <a href="simulations.html">Simulations</a> ·
<a href="file-formats.html">File formats</a> (<code>.csch</code>).</p>
