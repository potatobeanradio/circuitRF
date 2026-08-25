---
title: Welcome to circuitRF
kind: slides
deck: new-user
slug: slides/circuitrf-new-user.pdf
lede: Never used a circuit simulator before? This starts at the very beginning.
---

## Why simulate at all?

- Building RF hardware is **expensive and slow**. A simulator lets you predict how a design behaves before you build it.
- It lets you ask "what if?" a hundred times in an afternoon instead of twice in a week.
- circuitRF answers questions like: how much does this amplifier gain at 2 GHz? How much power before it saturates? How efficient is it? What load makes it happiest?

> **No question is too basic** — every RF word in this deck is explained where it first appears, and nothing assumes you have used an EDA tool before.

## What circuitRF is built for

- It is built specifically for **RF and microwave** work.
- You will hear it called "not a SPICE simulator". That means it focuses on the questions RF designers ask — frequency response, gain, efficiency, intermodulation, loadpull — rather than the time-domain waveforms a SPICE tool chases.
- It runs on Windows, macOS and Linux, and it starts in seconds.

> **The fastest way in** — open circuitRF, choose File ▸ New Schematic, and you have a blank sheet to drop parts onto and simulate. No setup, no project to create first.

## The window

- The **middle** of the window holds your documents — schematics, layouts, plots — one per tab.
- **Project**, on the left, is the folder you are working in.
- **Library**, on the right, is every part you can place.
- **Properties** shows whatever you have selected, and **Messages** along the bottom is the application telling you what it did.

{{ui: workspace-overview | full}}
{{caption: Documents in the middle, tool panels around them. Every one of them can be moved.}}

## The folder behind it

- The folder is called a **workspace**: an ordinary folder on disk with a `.cws` file in it and one sub-folder per cell.
- Nothing hidden, nothing in a database — which is why the Project panel and your file browser always agree.
- Every panel and every tab can be dragged somewhere else, and where you left them is remembered per workspace.
- If it ever gets away from you, **View ▸ Reset Layout** puts everything back.

## The Library: your box of parts

- Every part you can place lives in the **Library**, shown as a palette of tiles.
- Resistors, capacitors, inductors, sources, transistors, transmission lines, and more.
- Filter by category, or type in the search box — try "cap" or "tline".
- To place one, click its tile and then click the canvas, or drag the tile straight on.

{{ui: library-palette}}

## Cells: symbol, schematic, layout

- A **cell** is circuitRF's reusable building block — think of it as a chip you can drop into a bigger design. It has up to three **views**.
- **Symbol** — the glyph you see when you place the cell, and where its connection points sit. How the cell looks from the outside.
- **Schematic** — the actual circuitry inside: the components and wires that make it work.
- **Layout** — the physical artwork: copper, substrate stackup, vias, drawn to real dimensions against a technology.

## Why a symbol AND a schematic?

- The symbol is the convenient **outside** view; the schematic is the detailed **inside** view.
- Use an amplifier cell ten times and you want to see ten tidy amplifier symbols — not ten copies of the transistor-level schematic filling the screen.
- The symbol hides the detail. The schematic holds it.

{{ui: symbol-editor}}

## And why a layout as well?

- Above a couple of gigahertz, **the wiring stops being wiring**. A 90° bend, a length of line and the gap between two traces all have electrical behaviour of their own — and none of it is in the schematic.
- The layout is where that behaviour comes from: you draw the artwork, and the planar solver turns it into S-parameters.
- A cell does not need all three views. A test bench is usually schematic-only; imported artwork may be layout-only.

{{ui: layout-editor}}

## Hierarchy: circuits inside circuits

- Because a cell can contain other cells, designs are **hierarchical** — circuits nested inside circuits, as deep as you like.
- A two-stage amplifier might be one cell containing two single-stage amplifier cells, each containing a transistor and a matching network.
- **Push into** a cell to look inside; **pop back out** when you are done.
- Editing a cell changes **every** place it is used. Fix the amplifier once, and all ten instances update — that reuse is the whole point.

## Parameters and values

- A **parameter** is a value you can change: a resistor's resistance, an inductor's inductance, a source's power.
- Cells declare their own parameters too, and when you place a cell you can **override** one for that instance.
- Values flow **top-down**: the parent sets a value, the cell uses it. Circular definitions are caught and reported.

> **Just double-click it** — the quickest way to change a value is to double-click the label right on the schematic and type the new one: 50 Ω, 1.2 nH, 2 GHz. The inline editor accepts units directly.

## Editing a value in place

{{ui: inline-value-editor | full}}
{{caption: Double-click the value label and type. The unit stays; only the number is selected}}

## Pins, Ports and Terms

- A **Pin** is a cell's connection point — how a reusable cell plugs into the design around it. Pins live on the symbol.
- A **Term** is an S-parameter **port**: a numbered, reference-impedance connection, usually 50 Ω, where the simulator measures a network.
- A **Port** is the general idea of an external connection. Inside a cell it is a Pin; on an S-parameter test bench it is a Term.

> **Why it matters** — a Pin exposes a connection for reuse; a Term actively defines where the simulator excites and measures. The wrong one gives you no measurement port, or a source impedance you did not intend.

## Pin and Term, the two symbols

{{ui: pin-and-term | full}}
{{caption: Port has no symbol of its own - it is the idea these two realise}}

## The four analyses

- **DC** — what are the steady voltages and currents with no signal applied? The bias point, and the foundation harmonic balance builds on.
- **S-parameters** — how does this network respond, frequency by frequency, at low signal levels? Gain, matching, filters, isolation.
- **Harmonic balance** — how does it behave when driven hard, where it is nonlinear? Output power, compression, efficiency, intermodulation.
- **Loadpull** — which load impedance gives the best power or efficiency?

## You do not have to choose perfectly

- Start with **S-parameters** to see a network's frequency response.
- Move to **harmonic balance** when you want to drive it into nonlinear territory.
- Reach for **loadpull** when you are tuning a power amplifier's output match.
- Wrap any of them in a **parametric sweep** when you want to see a trend rather than a point.

{{ui: analyses-setup}}

## Reading results: the Data Display

- **Rectangular** — the familiar X-Y graph, e.g. gain in dB against frequency. For real, scalar numbers.
- **Smith chart** — a circular chart for impedance and reflection. It maps every possible impedance onto a disk, which makes matching networks intuitive to read.
- **Polar** — magnitude and angle on a circular plot, for complex quantities where phase matters.
- **Table** — the numbers themselves, in a grid.

{{ui: data-display}}

## Why are some results "complex"?

- An S-parameter such as `S21` is not just a size: it has a **magnitude and a phase** — how big, and how shifted in time.
- Mathematically that is a **complex number**.
- Smith and polar plots show complex values directly. On a rectangular plot you choose what to show: magnitude in dB, phase in degrees, real part.
- You can change that format per trace, and in a table you can switch how complex values are written.

{{ui: plot-smith-data}}

## Worked example A: the simplest DC simulation

- New Schematic. Place a **Vdc** source, a **Resistor** and a **Ground**.
- Wire the source across the resistor and the bottom to ground. Set the source to `10 V` and the resistor to `100 Ω` by double-clicking each value.
- Add a **DC** analysis in the Analyses panel and press **Run**.
- Open a Data Display ▸ Table and read the node voltage and the current.
- Ohm's law says **0.1 A** — a first simulation you can check in your head.

{{ui: example-dc-schematic}}

## Worked example B: a first S-parameter run

- Place two **Term**s — they auto-number 1 and 2 — and a small network between them: a series 2 nH inductor and a shunt 0.8 pF capacitor.
- Add an **S-Parameter** analysis over 1-5 GHz and press Run.
- On a **rectangular** plot, add `S(2,1)` with the `dB20` transform to see insertion loss against frequency.
- On a **Smith chart**, add `S(1,1)` to see the input match.

## Build this, and you should get this

{{ui: example-sparam-schematic | full}}
{{caption: Two 50 ohm Terms, a series 2 nH and a shunt 0.8 pF}}
{{ui: example-sparam-plot | full}}
{{caption: S(2,1) in dB, 1-5 GHz: the low-pass roll-off those two parts make}}

## Loadpull, contours and Pursuit

- For a power amplifier, the **load impedance** you present to the transistor's output dramatically changes its power and efficiency.
- **Loadpull** finds the sweet spot: circuitRF sweeps the load reflection coefficient over a grid on the Smith chart, runs a harmonic-balance simulation at each point, and draws **contours** — a topographic map whose hills are high output power or high efficiency.
- You read the impedance off the peak and design your output match toward it.

{{ui: plot-loadpull-contours}}

## Two things that make it pleasant

- **Measured data plots exactly like simulated data.** A loadpull file from the lab overlays on the same contour plot as your simulation — ideal for checking a model against measurement.
- **Loadpull Pursuit saves the hunting.** Instead of choosing a grid and finding the optimum by eye, Pursuit searches for the terminations that maximise your chosen metric and homes in on the useful region for you.
