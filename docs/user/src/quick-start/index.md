---
title: circuitRF Quick Start
slug: quick-start/index.html
doc-kind: Quick Start
breadcrumb: Docs > Quick Start
lede: For engineers who already know circuit simulators. In a couple of pages: what circuitRF is, how it's organized, and how to run a simulation and see a plot.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What circuitRF is</a></li>
      <li><a href="#organize">How a project is organized</a></li>
      <li><a href="#workspace">The workspace window</a></li>
      <li><a href="#build">Build a schematic: components, wires, the pin grid</a></li>
      <li><a href="#ppt">Pins vs. Ports vs. Terms</a></li>
      <li><a href="#run">Run a simulation</a></li>
      <li><a href="#plot">See the result on the Data Display</a></li>
      <li><a href="#cli">Headless / command line</a></li>
    </ol>
  </nav>

## What circuitRF is {#what}

circuitRF is a lightweight, cross-platform RF circuit simulator. If you've used a SPICE tool or a
commercial RF/microwave EDA suite, you'll be at home — but the analyses and the workflow are built
around the **RF problem**, not the time-domain transient problem. It is deliberately **not a SPICE
simulator**.

**Simulations available:**

- **DC** — operating point (linear and nonlinear), the bias prerequisite for HB.

- **S-parameters** — linear multiport network parameters over a frequency sweep, with
  renormalization and embedded Touchstone (`.sNp`) blocks.

- **Harmonic Balance (HB)** — steady-state nonlinear analysis, single- and two-tone, with
  power/source continuation for convergence at drive. Reports Pout, gain, efficiency, PAE, spectra,
  intermodulation.

- **Loadpull / sourcepull** — a first-class experiment: sweep the source/load reflection
  coefficient over a Smith-chart grid and contour the figures of merit. **Loadpull Pursuit**
  automatically searches for the terminations that optimize a metric.

- **Parametric sweeps** — wrap *any* analysis in one or more swept variables (available power, a
  bias voltage, a frequency, any user variable).

**Data Display:** results plot on rectangular, Smith, and polar charts, and in tables. Measured
data (Touchstone, `.spl`, `.lpcwave`) overlays simulated data on the same axes — a measured
loadpull contour plots exactly like a simulated one.

<div class="callout note">
    <span class="label">Results are Python-native</span>
    <p>Every run writes its results as a NumPy <code>.npy</code> dataset (a named bundle of labeled,
    unit-bearing arrays — the <code>DataSet</code>/<code>DataCube</code> model). You can plot it in
    circuitRF or load it straight into Python/MATLAB. Export to <code>.mat</code>, Touchstone,
    <code>.spl</code>, and <code>.lpcwave</code> is built in.</p>
  </div>

## How a project is organized {#organize}

circuitRF is a **hierarchical, cell-based** tool (like the commercial RF suites, not like a flat
SPICE deck). The pieces:

- **Library** — a collection of cells you can reuse and reference. circuitRF can reference several
  libraries at once.

- **Cell** — a reusable building block with up to three views: a **Symbol** (the glyph drawn when
  the cell is instanced, and where its ports sit), a **Schematic** (the electrical contents), and a
  **Layout** (the physical artwork, drawn against a technology and simulated by the planar
  method-of-moments solver). A cell declares **parameters** that an instance can override — this is
  how hierarchy passes values top-down.

- **Hierarchy** — a schematic can instance other cells as sub-cells. Push into a sub-cell to edit
  it, pop back out; an edit to a cell affects every instance.

- **TestBench** — the thing you actually simulate: a top schematic plus its analyses and
  measurements.

<div class="callout tip">
    <span class="label">No project? No problem</span>
    <p>You don't have to build a cell library to try something. Use <strong>File → New Schematic</strong> to
    open a <strong>standalone schematic</strong> — a scratch sheet you can wire up and simulate immediately,
    with no workspace or cell structure. Save it into a workspace later if it's worth keeping.</p>
  </div>

## The workspace window {#workspace}

A **workspace** is a folder holding a `.cws` file and a folder per cell — membership is the
filesystem, so the Project panel is showing you what is on disk. Open one and you get the window
below: documents in tabs down the middle, tool panels docked around them.

{{ui: workspace-overview}}

Everything in it is movable. Drag a tab to reorder it, against an edge to split the area, or clear of
the window to float it; drag a tool panel by its tab to re-dock it anywhere. **View ▸ Hide Dockers**
(<kbd>Ctrl/⌘+Shift+H</kbd>) gives the whole window to the documents and back again, and
**View ▸ Reset Layout** restores the arrangement you chose in Settings. The arrangement is saved into
the `.cws`, so a workspace reopens the way you left it.

<p class="small">Region by region, panel by panel:
<a href="../reference/workspace.html">The Workspace</a>.</p>

## Build a schematic: components, wires, the pin grid {#build}

{{ui: schematic-editor}}


<p class="small">The short version is below. The editor in full — the Library Palette, every toolbar
button, the context menu, and setting up the analysis that runs the circuit — is
<a href="../reference/schematic-editor.html">The Schematic Editor</a>.</p>

<ol class="steps">
    <li><strong>Place a component.</strong> Click a tile in the <strong>Library Palette</strong> (or drag it
      onto the canvas). A ghost follows the cursor; click to drop. The tool stays armed so you can place
      several. Press <kbd>R</kbd> to rotate the ghost, <kbd>Esc</kbd> to stop placing.</li>
    <li><strong>Wire it up.</strong> Press <kbd>W</kbd> (or the Wire button) and click from one pin to
      another. <kbd>Enter</kbd> or double-click finishes a wire; <kbd>Esc</kbd> cancels it.</li>
    <li><strong>Edit a value.</strong> <strong>Double-click a component's value label</strong> right on the
      schematic to edit it inline — type <code>50 Ω</code>, <code>1.2 nH</code>, <code>2 GHz</code> and press
      <kbd>Enter</kbd>. (Double-clicking the body opens the full parameter editor.)</li>
  </ol>

<div class="callout note">
    <span class="label">The pin grid</span>
    <p>Connections are exact, not fuzzy. Every component pin, wire vertex, and junction lands on the
    <strong>connection grid</strong> (the coarse grid), and two things are connected only when they sit on the
    <em>same</em> grid point. A separate, finer <strong>authoring grid</strong> positions labels and
    annotations. Keep pins on the connection grid and wiring "just works"; a red marker flags an unconnected
    pin.</p>
  </div>

## Pins vs. Ports vs. Terms {#ppt}

Three things sound similar but do different jobs. The distinction matters because it controls what
becomes an external interface versus an excitation/measurement point:

<table>
    <thead><tr><th>Concept</th><th>What it is</th><th>When you use it</th></tr></thead>
    <tbody>
      <tr>
        <td class="nowrap"><strong>Pin</strong></td>
        <td>An <em>interface terminal of a cell</em>. Pins on a cell's symbol are how the cell connects to the
          parent schematic that instances it. Connectivity only — no electrical model.</td>
        <td>Inside a cell you intend to reuse hierarchically, to expose its connection points.</td>
      </tr>
      <tr>
        <td class="nowrap"><strong>Term</strong></td>
        <td>An <em>S-parameter port termination</em> — a numbered reference-impedance port (default 50 Ω). Each
          <code>Term</code> carries a <code>Num</code> (port index).</td>
        <td>On a test bench, to define the ports an S-parameter analysis measures between.</td>
      </tr>
      <tr>
        <td class="nowrap"><strong>Port</strong></td>
        <td>The general term for an external connection point. In a <em>cell</em> a port is realized by a
          <strong>Pin</strong>; in an <em>S-parameter test bench</em> a port is realized by a
          <strong>Term</strong>.</td>
        <td>Conceptually — "this circuit is a 2-port." How you realize it depends on the context above.</td>
      </tr>
    </tbody>
  </table>

{{ui: pin-and-term}}

<p class="small">The Reference Guide has a fuller treatment with diagrams; for Quick Start: use
  <strong>Term</strong> to define S-parameter ports on a test bench, and <strong>Pin</strong> to expose a
  reusable cell's connections.</p>

## Run a simulation {#run}

<ol class="steps">
    <li>Open <strong>Simulate → Setup Analyses…</strong> (or the <strong>Analyses</strong> panel) and add an
      analysis (e.g. <em>S-Parameter</em>, 1–10 GHz).
      For an HB example, add <em>Harmonic Balance</em> and drive the input with a <code>P1Tone</code>
      (available-power) source.</li>
    <li>Press <strong>Run ▶</strong>. circuitRF extracts a netlist from the schematic, elaborates it, and runs
      the analysis on a background thread.</li>
    <li>The run writes <code>results/&lt;name&gt;/run.npy</code>. Open <strong>Data Displays</strong> that are
      already showing this result refresh automatically.</li>
  </ol>

## See the result on the Data Display {#plot}

<ol class="steps">
    <li>Open a <strong>Data Display</strong> (<kbd>Ctrl/⌘+Shift+D</kbd>) and add a plot — Rectangular, Smith,
      Polar, or Table.</li>
    <li>In the trace card, pick the data source (your run), then the signal — e.g. <code>S(2,1)</code>. On a
      rectangular plot choose a transform such as <code>dB20</code>; on a Smith chart the complex value plots
      directly.</li>
    <li>For loadpull, choose the loadpull run and a contour metric (Pout, PAE). The optimum (max-power /
      max-efficiency) markers and interactive markers read values off the contour surface.</li>
  </ol>

{{ui: plot-rectangular-data}}

## Headless / command line {#cli}

The engine runs without the GUI — useful for scripting and batch sweeps. From a `.cnl` netlist (a
human-readable circuit description):

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf sparam mycircuit.cnl --freq 1GHz:3GHz:50MHz -o mycircuit.s2p</code></pre>

This reads the netlist, runs the S-parameter analysis, and writes a Touchstone file. (Make sure
the circuit's port count matches the extension, or omit `-o` to let circuitRF name it `.sNp`
automatically.) Harmonic-balance and loadpull runs are driven from the GUI's Run button in this
release.

<div class="callout tip">
    <span class="label">Next</span>
    <p>Ready to draw something for real? <a href="../reference/schematic-editor.html">The Schematic
    Editor</a> covers placing, wiring and analysis setup in full, and
    <a href="../reference/simulations.html">Simulations</a> covers every analysis type. New to circuit
    simulators in general? The <a href="../new-user-guide/index.html">New User's Guide</a> starts from
    first principles. Need exact parameters, algorithms, or the netlist format? See the
    <a href="../reference/index.html">Reference Guide</a>.</p>
  </div>
