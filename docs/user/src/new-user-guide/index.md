---
title: Welcome to circuitRF!
slug: new-user-guide/index.html
doc-kind: New User's Guide
breadcrumb: Docs > New User's Guide
lede: Never used a circuit simulator before? Perfect — this guide starts from the very beginning and gets you to your first working simulation. No prior EDA experience assumed, and no question is too basic.
---

<nav class="toc">
    <h2>Contents</h2>
    <ol>
      <li><a href="#why">Why simulate? And what circuitRF does</a></li>
      <li><a href="#window">The circuitRF window</a></li>
      <li><a href="#library">The Library: your box of parts</a></li>
      <li><a href="#cells">Cells — symbol, schematic, layout and 3D</a></li>
      <li><a href="#hierarchy">Hierarchy: circuits inside circuits</a></li>
      <li><a href="#params">Parameters &amp; editing values</a></li>
      <li><a href="#ppt">Pins, Ports, and Terms</a></li>
      <li><a href="#sims">The simulation types</a></li>
      <li><a href="#display">The Data Display &amp; plot types</a></li>
      <li><a href="#examples">Two worked examples</a></li>
      <li><a href="#loadpull">Loadpull, contours &amp; Pursuit</a></li>
      <li><a href="#match">Matching networks: the Match component</a></li>
      <li><a href="#connector-3d">A first 3D solve: a connector and its fillet</a></li>
      <li><a href="#wave-ports-3d">Wave ports: one port per conductor</a></li>
    </ol>
  </nav>

## 1 · Why simulate? And what circuitRF does {#why}

Building RF hardware is expensive and slow. A **circuit simulator** lets you predict how a design
behaves *before* you build it — and lets you ask "what if?" a hundred times in an afternoon.
circuitRF answers questions like: *How much does this amplifier gain at 2 GHz? How much power does
it put out before it saturates? How efficient is it? What load impedance makes it happiest?*

circuitRF is built specifically for **RF and microwave** work. You'll hear it's "not a SPICE
simulator" — that just means it focuses on the questions RF designers ask (frequency response,
gain, efficiency, intermodulation, loadpull) rather than the time-domain waveforms a SPICE tool
chases. If those RF words are new, don't worry — you'll meet them gently below.

<div class="callout tip">
    <span class="label">The fastest way in</span>
    <p>Open circuitRF, choose <strong>File → New Schematic</strong>, and you have a blank sheet to drop parts
    onto and simulate — no setup, no project to create first. Everything below builds on that.</p>
  </div>

## 2 · The circuitRF window {#window}

Here is what you will be looking at. The middle of the window is the
[document area](../reference/workspace.html#documents) and holds your **documents** — schematics,
layouts, plots — one per tab. Around them sit the
[tool panels](../reference/workspace.html#panels): the *Project* panel on the left is the folder you
are working in, the *Library* on the right is every part you can place, *Properties* shows whatever
you have selected, and *Messages* along the bottom is the application telling you what it did.

{{ui: workspace-overview}}

<p class="small">Every region of that window, numbered and named:
<a href="../reference/workspace.html#regions">The Workspace ▸ The regions of the window</a>.</p>

The folder behind it is called a [**workspace**](../reference/workspace.html#workspace). It is an
ordinary folder on disk with a `.cws` file in it and one sub-folder per cell — nothing hidden,
nothing in a database — which is why the Project panel and your file browser always agree with each
other.

<div class="callout tip">
<span class="label">Move anything, and put it all back</span>
<p>Every panel and every tab can be dragged somewhere else — tabbed together, docked against an edge,
or pulled out into a window of its own — and where you left them is remembered per workspace. If it
ever gets away from you, <strong>View ▸ Reset Layout</strong> puts everything back. See
<a href="../reference/workspace.html#docking">Moving, hiding and resetting the layout</a>.</p>
</div>

## 3 · The Library: your box of parts {#library}

Every part you can place lives in the **Library**, shown as the **Library Palette** — a panel of
tiles, each a component. Resistors, capacitors, inductors, sources, transistors, transmission
lines, and more. You can filter by category or type in the search box (try "cap" or "tline"). To
place one, click its tile and then click on the canvas (or drag the tile onto the canvas).

A library is also where reusable building blocks you create yourself live — which brings us to
cells.

## 4 · Cells — symbol, schematic, layout and 3D {#cells}

A **cell** is circuitRF's reusable building block. Think of it like a chip you can drop into a
bigger design. A cell has up to four **views** — four ways of looking at the same block:

- A **Symbol** — the little glyph you see when you place the cell, and where its connection points
  (pins) sit. It's how the cell looks *from the outside*.

- A **Schematic** — the actual circuitry *inside* the cell: the components and wires that make it
  work.

- A **Layout** — the physical artwork: the copper, the substrate stackup it sits on, and the vias
  through it. It is drawn to real dimensions against a **technology** (a PCB or MMIC process), and
  it is what the electromagnetic solver simulates when you want the parasitics of the real thing
  rather than the ideal components the schematic names.

- A **3D view** — the solid model: boxes, cylinders, bond wires, imported STEP parts, and other cells'
  layouts placed inside them. It is drawn in circuitRF's own [3D editor](../reference/drawing-in-3d.html)
  and simulated by a full-wave [3D solver](../reference/em-3d.html) — for the things a flat layout
  cannot describe, such as a package and its lid, a connector, or a bond wire's loop.

**Why a symbol *and* a schematic?** Because the symbol is the convenient outside view and the
schematic is the detailed inside view. When you use an amplifier cell ten times in a design, you want
to see ten tidy amplifier *symbols* — not ten copies of the full transistor-level schematic
cluttering your screen. The symbol hides the detail; the schematic holds it.

**And why a layout as well?** Because above a couple of gigahertz the wiring stops being wiring. A
90° bend, a length of line and the gap between two traces all have electrical behaviour of their
own, and none of it is in the schematic. The layout is where that behaviour comes from — you draw
the artwork, and the [planar method-of-moments solver](../reference/mom-engine.html) turns it into
S-parameters you can put back into the circuit.

**And a 3D view?** Because some structures are not flat. A planar layout is copper on layers; a
connector, a package cavity or a bond wire arching over a die has real height and shape, and only a
3D solve sees it. **New 3D View from Layout** on a cell's menu in the Project panel starts one from
the cell's own layout, so the artwork you already drew becomes the floor of the 3D model.

A cell does not need all four. A test bench is usually schematic-only; a piece of artwork imported
from GDSII may be layout-only. The two are kept in step in either direction by
**Design ▸ Update Layout from Schematic** and **Update Schematic from Layout**, which reconcile the
components in one against the artwork in the other.

## 5 · Hierarchy: circuits inside circuits {#hierarchy}

Because a cell can contain other cells, designs are **hierarchical** — circuits nested inside
circuits, as deep as you like. A two-stage amplifier might be one cell that contains two
"single-stage amplifier" cells, each of which contains a transistor cell and a matching-network
cell.

To look inside a cell, **push into** it (open its schematic); when you're done, **pop back out**.
Editing a cell changes *every* place it's used — fix the amplifier once, and all ten instances
update. That reuse is the whole point of hierarchy: build a thing once, use it everywhere,
maintain it in one place.

## 6 · Parameters & editing values {#params}

A **parameter** is a value you can change — a resistor's resistance, an inductor's inductance, a
source's power. Cells can declare their own parameters too (say, an amplifier cell with a `Gain`
parameter), and when you place that cell you can **override** the parameter for that one instance.
Values flow *top-down*: the parent sets a value, the cell uses it. (circuitRF also catches
circular definitions and tells you, so you can't tie yourself in knots.)

<div class="callout tip">
    <span class="label">Just double-click it</span>
    <p>The quickest way to change a value: <strong>double-click the value label right on the schematic</strong>
    and type the new one — <code>50 Ω</code>, <code>1.2 nH</code>, <code>2 GHz</code> — then press
    <kbd>Enter</kbd>. The inline editor accepts units directly. (Double-click the component <em>body</em> for
    the full parameter editor with every setting.)</p>
  </div>

{{ui: inline-value-editor}}

## 7 · Pins, Ports, and Terms {#ppt}

These three sound alike but do different jobs, and keeping them straight saves confusion later:

- A **Pin** is a cell's connection point — how a reusable cell plugs into the design around it.
  (Pins live on a cell's symbol.)

- A **Term** is an S-parameter *port*: a numbered, reference-impedance (usually 50 Ω) connection
  where the simulator measures a network. You place Terms to say "measure the S-parameters between
  here and here."

- A **Port** is the general idea of an external connection. Inside a cell, a port is a **Pin**; on
  an S-parameter test bench, a port is a **Term**.

**Why it matters:** a Pin just exposes a connection for reuse; a Term actively defines where and
how the simulator excites and measures. Use the wrong one and you'll either have no measurement
port or an unintended source impedance. The [Reference Guide](../reference/pins-ports-terms.html)
shows each with pictures.

{{ui: pin-and-term}}

## 8 · The simulation types {#sims}

Different questions need different analyses. circuitRF offers four big ones:

<table>
    <thead><tr><th>Analysis</th><th>Answers…</th><th>Reach for it when…</th></tr></thead>
    <tbody>
      <tr><td><strong>DC</strong></td><td>What are the steady voltages and currents with no signal applied?</td>
        <td>You want the bias point — and it's the foundation HB builds on.</td></tr>
      <tr><td><strong>S-parameters</strong></td><td>How does this network respond, frequency by frequency, at low signal levels?</td>
        <td>Gain, matching, filters, isolation — anything <em>linear</em> vs. frequency.</td></tr>
      <tr><td><strong>Harmonic Balance</strong></td><td>How does it behave when driven hard, where it's nonlinear?</td>
        <td>Power amplifiers, mixers — output power, compression, efficiency, intermodulation.</td></tr>
      <tr><td><strong>Loadpull</strong></td><td>Which load impedance gives the best power/efficiency?</td>
        <td>Tuning a power amplifier's output match. (See §11.)</td></tr>
    </tbody>
  </table>

You don't have to choose perfectly up front — start with S-parameters to see a network's frequency
response, then move to harmonic balance when you want to drive it into nonlinear territory.

## 9 · The Data Display & plot types {#display}

Results appear in the **Data Display**, where you choose how to view them. Why several plot types?
Because RF data comes in different flavors:

- **Rectangular** — the familiar X–Y graph (e.g. gain in dB vs. frequency). For real, scalar
  numbers.

- **Smith chart** — a special circular chart for *impedance and reflection*. If you've not seen
  one: it maps every possible impedance onto a disk, which makes matching networks and reflection
  coefficients intuitive to read. It's the RF engineer's favorite chart.

- **Polar** — magnitude-and-angle on a circular plot, for complex quantities where you care about
  phase.

- **Table** — the numbers themselves, in a grid.

<div class="callout note">
    <span class="label">Why are some results "complex"?</span>
    <p>An S-parameter like <code>S21</code> isn't just a size — it has a <strong>magnitude and a phase</strong>
    (how big, and how shifted in time). Mathematically that's a <em>complex number</em>. Smith and polar plots
    show complex values directly; on a rectangular plot you pick what to show — magnitude in dB, phase in
    degrees, real part, etc. You can change that <strong>complex-number format</strong> per trace, and in a
    Table you can switch how complex values are written (magnitude/angle, real/imag, dB/angle).</p>
  </div>

## 10 · Two worked examples {#examples}

Beyond these two, circuitRF ships with complete example workspaces you can open, edit and run from
**File ▸ New ▸ Examples**; [Example Workspaces](../examples/index.html) lists every one with what it shows.

### A. The simplest possible DC simulation

<ol class="steps">
    <li>New Schematic. Place a <strong>Vdc</strong> source and a <strong>Resistor</strong>; place a
      <strong>Ground</strong>.</li>
    <li>Wire the source across the resistor, and connect the bottom to ground. Set the source to
      <code>10 V</code> and the resistor to <code>100 Ω</code> (double-click each value).</li>
    <li>Add a <strong>DC</strong> analysis in the Analyses panel and press <strong>Run</strong>.</li>
    <li>Open a Data Display → Table to read the node voltage and current. (Ohm's law says 0.1 A — a good first
      sanity check that everything's wired right.)</li>
  </ol>

{{ui: example-dc-schematic}}

### B. A first S-parameter simulation

<ol class="steps">
    <li>Place two <strong>Term</strong>s (they auto-number 1 and 2) and a small network between them — for
      example a series inductor and a shunt capacitor, or a <strong>TLIN</strong> transmission line.</li>
    <li>Add an <strong>S-Parameter</strong> analysis over, say, 1–5 GHz.</li>
    <li>Run, then open a Data Display. On a <strong>Rectangular</strong> plot, add <code>S(2,1)</code> with the
      <code>dB20</code> transform to see insertion loss vs. frequency; on a <strong>Smith chart</strong>, add
      <code>S(1,1)</code> to see the input match.</li>
  </ol>

<p class="small">Build the schematic below and you should get the response beside it — a series 2 nH
and a shunt 0.8 pF between two 50 Ω Terms, swept 1–5 GHz.</p>

{{ui: example-sparam-schematic}}

{{ui: example-sparam-plot}}

## 11 · Loadpull, contours & Pursuit {#loadpull}

For a power amplifier, the *load impedance* you present to the transistor's output dramatically
changes its power and efficiency. **Loadpull** finds the sweet spot: circuitRF sweeps the load
reflection coefficient over a grid of points on the Smith chart, runs a harmonic-balance
simulation at each, and draws **contours** — like a topographic map where the "hills" are high
output power or high efficiency. You read off the impedance at the peak and design your output
match toward it.

Two things make this delightful in circuitRF:

- **Measured data plots exactly like simulated data.** A loadpull file from the lab (Touchstone,
  `.spl`, or `.lpcwave`) overlays on the same contour plot as your simulation — perfect for checking
  model against measurement.

- **Loadpull Pursuit saves you time.** Instead of you choosing the grid and hunting for the
  optimum by eye, Pursuit *automatically searches* for the terminations that maximize your chosen
  metric (peak power, peak efficiency), homing in on the useful region of the Smith chart for you.

## 12 · Matching networks: the Match component {#match}

Sooner or later you will want to get from the impedance your transistor presents to the 50 Ω the rest
of the world runs at. circuitRF has a component that designs the network for you: place a **Match**,
double-click it, and the **Match Designer** opens.

One thing to know before you open it, because it is the thing every experienced RF engineer trips over:

<div class="callout warn">
<span class="label">Match is a matching tool, but it works over a band</span>
<p>If you have used a single-frequency matching tool — the kind where you give it a target impedance
and it hands back two parts — Match will look unfamiliar. It asks for a <b>band</b>, an <b>order</b>
and a <b>ripple</b>, which is filter vocabulary. That is not a detour: a network that matches over a
band <em>is</em> a filter, so those three words are simply how you say <em>how wide</em> and
<em>how well</em>. You are still designing a match.</p>
<p>Two things follow. <b>You get more parts than a two-element L-match</b> — but some of them are your
device's own capacitance, absorbed into the network rather than bought. And <b>parts that run to
ground are normal</b>: a ladder alternates series arms and shunt arms, and a shunt arm goes to ground
by definition.</p>
</div>

There is also one button worth reading about before you press it. **Shunt DC Block** is *not* the
series blocking capacitor you may be expecting in the signal path — it stops a shunt inductor from
shorting your bias supply to ground, so the capacitors it adds appear in the arms that run to ground.
The <a href="../reference/match.html#dcblock">reference chapter</a> explains it, and the tooltip on
the button says the same thing in two sentences.

<p class="small">The full story — what absorption buys you, how to read the solutions list, and the two
worked amplifier examples — is in <a href="../reference/match.html">The Match Component</a>.</p>

## 13 · A first 3D solve: a connector and its fillet {#connector-3d}

Ten minutes, and nothing to draw. It shows what the 3D editor's geometry kernel adds — booleans, fillets and
imported parts — and what a solver does and does not see of a curved surface.

1. **File ▸ New ▸ Examples ▸ 3D Connector**, and choose a folder for your copy. The copy opens in the window you
   chose the menu from when that window has no workspace, no torn-off document window and no unsaved work,
   and in a new window otherwise, so nothing you are working on is replaced. It opens `README.md`; open the
   **Launch** cell's 3D view from the Project panel.
2. Look at the **object tree**. `housing` is a **boolean**: a box with the bore subtracted, the bore **kept** as the
   PTFE that fills it, and a flange **imported from a STEP file** united with it. `pin` is a cylinder with a
   **fillet** row beneath it, which rounds its tip. Click each row; the view shows what it is.
3. **Simulate ▸ Setup Analyses…** lists two setups, *Palace* and *openEMS*. Under *openEMS* there is already a
   warning: it will not represent the pin's fillet, because its grid cell there is wider than the fillet's radius.
   Nothing has run yet — the warning is worked out from the drawing. Solvers not installed? The first **Run**
   offers to install them.
4. **Run** *Palace* (about a minute on a recent laptop), then make *openEMS* the active setup and run it (about two).
5. In the tree, untick **Enabled** on the pin's fillet row — the tip turns square — and run both again.
6. Compare |S11|. Palace moves by a fraction of a dB; openEMS moves too, but its warning told you why that is not the
   fillet: the grid changed shape, not the geometry it resolves.

The whole story, with the numbers, is in
<a href="../reference/em-solvers.html#curved">EM Solvers ▸ What each solver sees of curved geometry</a>, and every
gesture that drew the example is in
<a href="../reference/drawing-in-3d.html#connector">The 3D Editor ▸ The second example</a>.

## 14 · Wave ports: one port per conductor {#wave-ports-3d}

Ten minutes, and nothing to draw. A **wave port** makes a region of the air box's face a matched continuation of the
line that ends there, so the port adds no gap and no parasitic of its own. This example uses one on a coax, and then
on pairs of lines that end on one face, where each line becomes a port of its own.

1. **File ▸ New ▸ Examples ▸ 3D Wave Ports**, and open the **Launch** cell's 3D view. It is §13's connector with its back
   end open: the coax runs to the air box's face, and **P1** there is a wave port. Its arrow runs from the housing to
   the pin. P1 states only its rectangle: delete it and draw it again with the Port tool (**Shift+A P**) on that face,
   2 mm square round the axis, and the same arrow is inferred, along a ray from the bore's wall to the pin, because the
   housing encloses the pin. The coax's closed-form impedance is **50.02 Ω**.
2. **Run** *Palace*, then *openEMS*. Compare |S11| at 18 GHz with §13's lumped gap port: it read **−40.75 dB** on Palace
   and **−19.03 dB** on openEMS; the wave port reads **−19.53 dB** and **−21.38 dB**. The deep null was the gap's
   capacitance, not the connector.
3. Open **Pair**: two strips in PTFE between two ground planes, with a wave port at each end whose **two numbered
   arrows** are its **terminals**, one per strip. Delete both ports and right-click each end face ▸ *Make Port ▸ Wave*
   to make them yourself: the status line names `airbox/zmin` as the reference, because the ground planes are the air
   box's PEC faces and count as one. Terminals 1 and 2 are at one end and 3 and 4 at the other, so
   **S31 is the thru, S21 the near-end coupling and S41 the far-end coupling**. Its even and odd impedances are
   **57.51 Ω** and **43.10 Ω** by Cohn's closed form.
4. **Run** *openEMS* (Pair has no Palace setup) and plot dB(S31), dB(S21) and dB(S41) on a Data Display. At 10 GHz
   openEMS reads **−0.092 dB**, **−16.94 dB** and **−60.88 dB** where Cohn's ideal line gives **−0.088 dB**,
   **−16.98 dB** and **−64.47 dB**.
5. Open **Coupled Microstrip**: two microstrip lines of different widths (1.2 mm and 2 mm) in a closed housing, with the
   same kind of port at each end, numbered as Pair's. **Run** *Palace* and *openEMS* and plot dB(S11) and dB(S22), the
   two lines' reflections. They differ, on both solvers: at 2 GHz Palace reads **−21.74 dB** and **−15.48 dB**, openEMS
   **−21.00 dB** and **−15.72 dB**. At 10 GHz Palace reads **−17.38 dB** and **−16.08 dB**, openEMS **−17.82 dB** and
   **−15.30 dB**, and across the band the two agree to 0.03. Each strip has the lid above it as well as the floor below,
   and openEMS's port reads its voltage to the floor alone, as Palace does, because a strip on the laminate with air
   above it is a microstrip.

**What each solver did.** Palace solved the coax's mode on the face and reports its impedance, **49.18 to 49.31 Ω**
at the *Draft* preset and **50.13 to 50.26 Ω** at element order 2 (8 min 30 s and 8.9 GB instead of a minute; |S11|
then reads −25.02 dB at 10 GHz and −21.44 dB at 18 GHz).
openEMS fed the line from behind the face and measured its voltage and current there; on the Launch its square cells
leave the round pin unresolved, and it says so: the line measured **54.59 Ω**, **ε_eff 3.08**, so on that cell
Palace is the answer to trust. On Pair, openEMS fed each strip on its own and built S from the four runs together;
each terminal's line measured **50.17 Ω**, **ε_eff 2.10**. Pair is openEMS's: Palace runs a port with two terminals only where
the lines' modes travel at different speeds, and a stripline's travel at one, which Palace returns in an arbitrary mixture
(the example's README has what was measured).

| Run | Time | Size | Measured |
|---|---|---|---|
| Launch, Palace | 1 min 5 s | 4.5 GB | \|S11\| −21.71 dB at 10 GHz, \|S21\| −0.562 dB at 18 GHz |
| Launch, openEMS | 2 min 17 s | 937,440 cells | \|S11\| −19.36 dB at 10 GHz, \|S21\| −0.452 dB at 18 GHz |
| Pair, openEMS | 52 s | 1,033,923 cells | four runs, one per terminal |
| Coupled Microstrip, Palace | 14 min 34 s | 5.1 GB | each face's two modes converted to one port per strip |
| Coupled Microstrip, openEMS | 7 min 28 s | 6,053,568 cells | four runs, one per terminal |

The example's README has every step and number, and the reference is
<a href="../reference/em-setup.html#wave-ports">EM Setup ▸ Wave ports</a>.

---

<p class="small">Ready for specifics? The <a href="../reference/index.html">Reference Guide</a> documents
  every component, every analysis setting, and the plot types in detail. Already comfortable with simulators?
  The <a href="../quick-start/index.html">Quick Start</a> is the fast path. Want an AI agent to do the
  drawing? <a href="../reference/ai-agents.html">Designing with an AI agent</a> covers connecting one and
  what to tell it.</p>
