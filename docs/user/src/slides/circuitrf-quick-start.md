---
title: circuitRF Quick Start
kind: slides
deck: quick-start
slug: slides/circuitrf-quick-start.pdf
lede: For engineers who already use circuit simulators — from a blank sheet to a plot.
---

## What this deck assumes

- You have used **SPICE or a vendor RF suite** before, and you want the differences, not the fundamentals.
- New to circuit simulation entirely? Take the **New User** deck first; it starts from why you would simulate at all.
- Everything here is also in the Quick Start chapter of the documentation, at more length.

> **The one-line version** — circuitRF is an RF simulator, not a SPICE simulator: the analyses, the file formats and the workflow are built around frequency response and nonlinear drive.

## The analyses you get

- **DC** — operating point, linear and nonlinear. The bias prerequisite for harmonic balance.
- **S-parameters** — multiport network parameters over a frequency sweep, with renormalisation and embedded Touchstone blocks.
- **Harmonic balance** — steady-state nonlinear, single- and two-tone, with power continuation for convergence at drive. Pout, gain, efficiency, PAE, spectra, intermodulation.
- **Loadpull / sourcepull** — sweep the source or load reflection coefficient over a Smith-chart grid and contour the figures of merit. **Pursuit** searches for the optimum instead of making you hunt.
- **Parametric sweeps** wrap any of the above in one or more swept variables.

## How a project is organised

- **Library** — a collection of reusable cells. Several can be referenced at once.
- **Cell** — a building block with up to three views: **Symbol**, **Schematic**, **Layout**. It declares parameters an instance can override.
- **Hierarchy** — a schematic instances other cells. Push in to edit, pop out; an edit reaches every instance.
- **TestBench** — what you actually simulate: a top schematic plus its analyses and measurements.

> **No project? No problem** — File ▸ New Schematic opens a standalone scratch sheet you can wire and simulate immediately, and save into a workspace later if it turns out to be worth keeping.

## The workspace window

- A workspace is a **folder** holding a `.cws` file and a folder per cell. Membership is the filesystem, so the Project panel shows what is on disk.
- Documents in tabs down the middle; tool panels docked around them.
- Drag a tab to reorder, against an edge to split, or clear of the window to float it.
- **View ▸ Hide Dockers** gives the whole window to the documents; **View ▸ Reset Layout** puts it all back.

{{ui: workspace-overview | full}}
{{caption: Documents in the middle, Project, Properties, Library and Messages around them.}}

## Build a schematic

- **Place** — click a tile in the Library Palette, or drag it onto the canvas. The tool stays armed; `R` rotates the ghost, `Esc` stops placing.
- **Wire** — press `W`, click pin to pin. `Enter` or a double-click finishes; `Esc` cancels.
- **Edit a value** — double-click the value label right on the schematic and type `50 Ω`, `1.2 nH`, `2 GHz`.
- Double-clicking the component **body** opens the full parameter editor instead.

{{ui: schematic-editor}}

## The pin grid decides connectivity

- Connections are **exact, not fuzzy**. Every pin, wire vertex and junction lands on the **connection grid**.
- Two things are connected only when they sit on the **same** grid point — there is no proximity rule and no netlist repair pass.
- A separate, finer **authoring grid** positions labels and annotations, and has nothing to do with connectivity.
- An unconnected pin is flagged with a red marker rather than being silently tied to a neighbour.

## Pin vs. Term vs. Port

- **Pin** — an interface terminal of a **cell**. Lives on the symbol; connectivity only, no electrical model. Use it to expose a reusable cell's connection points.
- **Term** — an **S-parameter port termination**: a numbered reference-impedance port, 50 Ω by default, carrying a `Num`. Use it on a test bench to define what the analysis measures between.
- **Port** — the general idea. In a cell it is realised by a Pin; on an S-parameter test bench, by a Term.

> **Why it matters** — use the wrong one and you get either no measurement port or an unintended source impedance, and neither announces itself.

## Pin and Term, the two symbols

{{ui: pin-and-term | full}}
{{caption: Port has no symbol of its own - it is the idea these two realise}}

## Set up and run an analysis

- **Simulate ▸ Setup Analyses…**, or the Analyses panel, adds an analysis — S-Parameter over 1–10 GHz, say.
- For a harmonic-balance example, add **Harmonic Balance** and drive the input with a `P1Tone` available-power source.
- Press **Run**. circuitRF extracts the netlist from the schematic, elaborates it, and runs on a background thread.
- The run writes `results/<name>/run.npy`; any Data Display already showing that result refreshes itself.

{{ui: analyses-setup}}

## The analysis editor

- The analysis type, its sweep, and the **parametric sweep that wraps it**, in one dialog.
- Harmonic balance takes its tone, its harmonic order and its convergence settings here.
- A sweep wrapping an analysis adds an axis to the result cube rather than producing a second run.

{{ui: analysis-editor-hb}}

## See the result

- Open a **Data Display**, add a plot — Rectangular, Smith, Polar or Table.
- In the **trace card**, pick the data source, then the signal — `S(2,1)`, say. On a rectangular plot choose a transform such as `dB20`; on a Smith chart the complex value plots directly.
- For loadpull, pick the loadpull run and a contour metric. Optimum markers and interactive markers read values off the contour surface.

{{ui: plot-inspector-trace-card}}

## A plot, for real

{{ui: plot-rectangular-data | full}}
{{caption: The shipped FET test bench's S-parameters, 1-10 GHz}}
{{ui: plot-table-data | full}}
{{caption: The same run as a table - a complex column beside a scalar one}}

## Smith, polar and table

- **Smith** takes a complex quantity directly — reflection and impedance, with the impedance grid.
- **Polar** is the same complex data without the grid, when magnitude and angle are what you want.
- **Table** is the numbers, with a selectable complex format per column.
- Every plot type states what data it will accept, so a mismatched trace is refused rather than drawn wrongly.

{{ui: plot-smith-data}}

## Headless and scripted

- The engine runs without the GUI, so a sweep can live in a script or in CI.
- `--set var=expr` overrides a global before elaboration; `-o` exports to `.mat`, `.npy` or Touchstone.
- The `.cnl` netlist is human-readable, and the CLI evaluates the test bench's measurements exactly as the GUI does.

```
circuitrf sparam mycircuit.cnl --freq 1GHz:3GHz:50MHz -o mycircuit.s2p
circuitrf hb     pa.cnl --set Pavs=22 -o sweep.npy
circuitrf dc     bias.cnl
```
