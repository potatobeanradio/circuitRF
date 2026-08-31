---
title: The Schematic Editor
slug: reference/schematic-editor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Schematic editor
lede: Where a circuit is drawn: placing components from the Library Palette, wiring them, editing their values, and setting up the analysis that simulates the result.
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
<li><a href="#views">The other two views</a></li>
<li><a href="#analyses">Simulating: an Analysis</a></li>
<li><a href="#toolbar">The toolbar</a></li>
</ol>
</nav>

## Orientation {#orientation}

{{ui: schematic-editor}}

A schematic is a **view of a cell**, exactly as its [symbol](symbol-editor.html) and its
[layout](layout-editor.html) are; the cell folder holds `schematic/`, `symbol/` and `layout/` side
by side and a cell need not have all three. Opening one opens a document tab.

You do not need a cell library to start. **File ▸ New Schematic** opens a standalone scratch sheet
you can wire up and simulate immediately, with no workspace around it, and save into a workspace
later if it turns out to be worth keeping.

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

- **The category picker.** **All** is the default and shows the whole built-in library in the
  palette's own pinned order — the parts you reach for most, first. **All - Alphabetical** is the
  same set sorted by name, **Common** is a short curated set, and **Recently Used** is what you have
  placed lately. Below those come the built-in categories — Lumped, Devices, Nonlinear, Sources,
  Terminals, Transmission Line, Microstrip, Matching, Data Files — and then one entry per imported
  kit, with the kit's own groupings indented beneath it. See
  [PDK integration](pdk-integration.html) for where kit entries come from.
- **The search box.** Matches a part's display name and its search terms, and a kit part's kit name
  as well. Searching inside a built-in category narrows within it; searching from All searches
  everything, built-ins and kits together.

The tile grid is a **reflow grid**: the number of columns follows the panel's width, so widening the
palette gets you more tiles per row rather than bigger tiles.

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
<kbd>Shift</kbd>+<kbd>R</kbd> clockwise — the same two keys that rotate a selection when nothing is
armed, so there is one pair of rotate keys to remember rather than two.

<div class="callout note">
<span class="label">The ghost is the real symbol, not a stand-in</span>
<p>For a part from an imported kit the ghost is built by resolving the cell's own primary symbol, not
from its generic <code>SymbolKind</code>. Kit parts share one kind, so a ghost drawn from the kind
alone would show a plain box during the drag and then place the kit's real artwork — the drag showing
one thing and the result another.</p>
</div>

## Wiring, values and labels {#wiring}

**Wire** is <kbd>W</kbd> (or the toolbar button). Click from one pin to another; <kbd>Enter</kbd>
finishes the wire in progress and returns you to Select, <kbd>Esc</kbd> cancels it. **Select** is
<kbd>S</kbd>, and <kbd>Esc</kbd> from any tool comes back to it.

Double-click does three different things depending on what is under the cursor, and the distinction
is worth learning because it is the fastest edit in the application:

| Double-click on | What opens |
|---|---|
| A component's **value label** (`C = 1 pF`) | An **inline edit box** over the value itself. Type `1.2 nH`, `50 Ω`, `2 GHz` and press <kbd>Enter</kbd>. |
| A component's **body** | The full **parameter editor** for that instance — every parameter, not just the displayed ones. |
| A **wire** | A **net label** on that wire, so you can name the net and refer to it in a measurement. |

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
<li><strong>Right-click ▸ Push In</strong> — the item appears only on a cell instance.</li>
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

## The other two views {#views}

The schematic is one of a cell's three views, and each is edited in its own editor:

| View | Editor | What it owns |
|---|---|---|
| **Schematic** | this page | the electrical contents — instances, nets, values |
| **Symbol** | [Symbol Editor](symbol-editor.html) | the glyph an instance draws, and where its pins sit |
| **Layout** | [Layout Editor](layout-editor.html) | the physical artwork, for fab handoff and EM |

The symbol is what step 4 above places; the layout is what gets manufactured. A cell need not have
all three — the cell folder holds `schematic/`, `symbol/` and `layout/` side by side and any of them
may be absent.

Two commands under the **Design** menu move work between the schematic and the layout —
**Update Layout from Schematic** (<kbd>⌘U</kbd>) and **Update Schematic from Layout**
(<kbd>⇧⌘U</kbd>). Neither ever runs by itself; both are described in
[Schematic ⇄ layout](layout-editor.html#schematic-flow).

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

## The toolbar {#toolbar}

{{toolbar: schematic}}

Grouped by what you are doing, left to right:

- **View** — Zoom to Fit, reset to 1:1, and a zoom box. <kbd>F</kbd> is the one to learn; a schematic
  is usually bigger than the window.
- **Draw** — Select, the Wire tool, and the three things you place constantly: **Ground**, **Term**
  (a numbered port with a reference impedance, for an S-parameter run) and **Pin** (a connection
  point of the cell you are drawing). [Which is which](pins-ports-terms.html) is worth reading once.
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
