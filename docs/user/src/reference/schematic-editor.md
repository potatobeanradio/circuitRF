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
<li><a href="#hierarchy">Hierarchy and the other two views</a></li>
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

Right-clicking a component gives you the operations that apply to *it*: **Edit Parameters**, **Push
In** (greyed when the component is not a hierarchical cell), rotate and mirror, the two label
commands, **Labels ▸** for choosing which parameters show on the sheet, **Disconnect**, **Copy** and
**Delete**. Items are **disabled rather than hidden**, so their positions stay put and you learn the
menu by muscle memory.

## Hierarchy and the other two views {#hierarchy}

A schematic can instance other cells as sub-cells. **Push In** descends into the selected cell and
**Pop Out** comes back; the breadcrumb above the canvas shows where you are. An edit to a cell
affects every instance of it, live.

The three views of a cell are edited in three editors, and each one hands off to the others:

| View | Editor | What it owns |
|---|---|---|
| **Schematic** | this page | the electrical contents — instances, nets, values |
| **Symbol** | [Symbol Editor](symbol-editor.html) | the glyph an instance draws, and where its pins sit |
| **Layout** | [Layout Editor](layout-editor.html) | the physical artwork, for fab handoff and EM |

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
