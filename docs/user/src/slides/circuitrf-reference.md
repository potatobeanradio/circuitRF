---
title: circuitRF Reference Guide
kind: slides
deck: reference
slug: slides/circuitrf-reference.pdf
lede: The whole guide in outline — every chapter, what is in it, and where it sits.
---

## How this deck maps to the guide

- The Reference Guide is **one reading order** in five sections, so it reads straight through as well as being dipped into.
- This deck is that order in outline: one slide per chapter or small group of chapters.
- **In-app Help buttons deep-link into these pages** — a component's Help lands on its own section, an analysis's Help on that analysis.

> **If you are new to circuitRF** — read Units and Grid & Connectivity first. Between them they cover the two conventions that are circuitRF's own rather than the industry's, and almost every early surprise is one of those two.

# Core concepts

The workspace, units, expressions, connectivity and the file formats.

## The workspace

- The window: documents, tool panels, docking, floating, hiding and resetting.
- The **folder behind it all** — a `.cws` file and one sub-folder per cell, so membership is the filesystem.
- Every region of the window numbered and named, panel by panel.
- Window layout is saved into the workspace, so it reopens the way you left it.

{{ui: workspace-regions}}

## Units, and the grid you type into

- **DBU** — the integer database unit everything is stored in — versus the **display unit** you read and type.
- The snap grid, the connection grid, and how a value with an SI prefix is parsed.
- Why a unit is a **field** of a row rather than part of the number you type.
- Geometry snapping in the layout view, query by query.

{{ui: snap-glyphs | full}}
{{caption: The six geometry-snap glyphs, each drawn by the editor's own renderer from a real query.}}

## Expressions

- **One expression engine** serves global variables, cell parameters, device equations and measurements. It tokenises and parses — never string substitution.
- Variable references; `+ - * / ^ ( )`; the standard functions; conditionals and `if(cond, then, else)`.
- User-defined expression functions with arbitrary parameters.
- **Cycle detection is mandatory** across variables, defaults and overrides, so a circular definition is reported rather than looping.

## Grid & connectivity, and Pin / Port / Term

- **Grid & Connectivity** — the schematic grid, wires, junctions, and exactly what decides that two things are connected.
- **Pins, Ports and Terms** — three things that sound alike and do different jobs, each shown with diagrams.
- A Pin exposes a cell's connection; a Term defines an S-parameter port; a Port is the general idea realised by one of the two.

## File formats

- Every circuitRF document type and what is inside it: the workspace, the schematic, the symbol, the layout, the technology, the netlist and the data display.
- All of them are **human-readable text** you can read, diff and keep under version control.
- Touchstone in and out; measured loadpull formats; GDSII artwork import.

# Design

Drawing circuits, the component library, and synthesis.

## The schematic editor

- The Library Palette, placing, wiring, and every toolbar button with its picture.
- The context menu, the inline value editor, and pushing into and popping out of a sub-cell.
- Setting up the analysis that runs the circuit, and where the analyses live.

{{ui: schematic-editor | full}}
{{caption: The schematic editor with the shipped FET S-parameter test bench open.}}

## Components

- **Every component in the standard library**: its symbol, what it is for, and each of its parameters.
- Passive parts, sources, transmission lines, terminations, probes and the nonlinear devices.
- Five native large-signal FETs on a shared model base, with selectable gate charge; plus diodes, the equation-defined device and the nonlinear capacitor.
- Externally-supplied models arrive through the device worker rather than being compiled in.

{{ui: library-palette}}

## Importing a SPICE model

- A `.model` card or a `.subckt`, either **referenced** from its own file or **copied in** as an editable cell.
- **Every parameter not carried is named**, in the card's own spelling, and the list is written onto the imported cell so it is still there later.
- Why one is not carried: no analysis to feed it, no node to attach it to, no term in the formulation, it belongs in the schematic, or it belongs to a law circuitRF does not implement.
- A type circuitRF has no device for is **refused by name**, never approximated into the nearest one.

## Dynamic symbols and the symbol editor

- **Dynamic symbols** — components whose drawn symbol changes with their parameters, so the sheet shows what the part actually is.
- **The symbol editor** — drawing and editing a symbol, placing its pins, and the variadic bodies that grow with a parameter.

{{ui: symbol-editor}}

## The Symbolically-Defined Device

- Write a **nonlinear model as equations** rather than as code: currents and charges as expressions of the port voltages.
- The same expression engine as everywhere else, with analytic derivatives taken for you.
- It is the form the hero references depend on, so an SDD written here is an SDD anywhere.

## The nonlinear capacitor and the C-V editor

- `C(V)` as a **Taylor series**, and the C-V Editor that fits one to a measured table.
- Choose the fit order and see the polynomial it produces against the points it came from.
- Charge-based, so the capacitance and its derivative stay consistent through harmonic balance.

{{ui: cv-editor}}

## The Match component

- **Direct synthesis** of a bandpass matching network that absorbs both terminations — closed form, no optimiser.
- Specify the two terminations and the band; read the synthesised ladder and the response it achieves.
- Every valid transform set is listed, simplest first, so you choose the topology rather than accepting one.

{{ui: match-interstage}}

## Every solution, ranked

{{ui: match-solutions | full}}
{{caption: The solutions list slid out: every valid transform set, simplest first.}}

# Simulate

The analyses, the measurements, the netlist and the plots.

## Simulations

- **DC**, **S-parameters**, **harmonic balance**, **parametric sweeps**, **loadpull** and **pursuit**, each with every setting on it.
- Convergence: power and source continuation, warm starting, and what a failed search reports.
- Multi-tone harmonic balance and the intermodulation products it reports.
- Analyses attach to a **TestBench**, never to a cell.

{{ui: analysis-editor-hb}}

## Measurements

- Post-processing a run into **named quantities**, evaluated by the same expression engine.
- Measurements attach to the TestBench and reference circuit quantities by absolute downward path, e.g. `V(X1.drain)`.
- Every result is added to the run's dataset as a named cube, so a measurement plots exactly like a raw signal.

## The netlist

- The `.cnl` **elaborated-netlist** format, line by line: nodes, devices, parameters, analyses and measurements.
- Human-readable and diffable, and the exact thing the engine consumes.
- The command line runs it directly, so a netlist that works headless works when opened.

## The Data Display

- Plots, tables, **trace cards** and markers — where a number came from is always on the card.
- Contour traces: the metric, the constraint, the levels and the interpolation.
- Measured and simulated traces share the axes.

{{ui: plot-inspector-loadpull}}

## Plot types

- **Rectangular**, **Smith**, **Polar** and **Table** — and which data each will accept.
- Complex-number formats per trace, and per column in a table.
- Markers that read values off the plotted surface, including off a contour.

{{ui: plot-loadpull-contours | full}}
{{caption: Load-pull contours on the gamma plane, interpolated from a 61-point termination grid.}}

## Getting results out

- Every run is a **NumPy dataset** of named, labelled, unit-bearing arrays.
- Export to `.npy`, `.mat`, Touchstone, `.spl` and `.lpcwave`.
- Load straight into Python or MATLAB with no add-on and no proprietary reader.

```
circuitrf sparam amp.cnl --freq 1GHz:3GHz:50MHz -o amp.s2p
circuitrf hb     pa.cnl  --set Pavs=22 -o sweep.npy
```

# Layout & EM

Artwork, technologies, kits and the planar solver.

## The layout editor

- The technology, drawing, snapping, and the schematic the layout belongs to.
- **Design ▸ Update Layout from Schematic** and **Update Schematic from Layout** reconcile the two in either direction.
- Bitmaps, labels, groups, flattening, and the properties inspector for exact geometry.

{{ui: layout-editor | full}}
{{caption: A microstrip run with a mitred bend, a crossing stub and a ground via.}}

## PCells

- **Parameterised cells**: placing one, driving it, and its parameter handles on the canvas.
- Generated content is content-addressed, so a generator fix cannot leave a stale cell on disk.
- Kit PCells are Python; circuitRF's own are built in.

## PDK integration and authoring

- **Integration** — importing a kit, managing the references a workspace holds, and simulating the parts it supplies.
- **Authoring** — the OpenPDK layout, Python PCells, models and artwork.
- Compiled vendor model libraries run in a **separate worker process**, so a kit is data rather than a plug-in we compile.
- A workspace records a **reference** to a kit, never a copy of it, so nothing vendor-owned travels with a shared design.

## Manage PDKs

{{ui: manage-pdks | full}}
{{caption: Every kit the workspace references, what it resolved to, how many parts it loaded, and the actions that repair it}}

## The method-of-moments engine

- What the planar solver does, **what it will not do**, and how.
- A layered-medium Green's function; microstrip, stripline, multi-level metal and ground vias.
- Adaptive frequency sampling solves few points and models the rest.
- It **refuses a problem it cannot do well** and names the binding reason, rather than running long and returning a wrong number.

## EM Setup

- The panel control by control: stackup, ports, sweep, and where the results land.
- The kernel the registry chose, and why it chose it.
- The resolved ports and the mesh report, shown back to you before you commit to a run.

{{ui: em-setup-editor}}

# Tools

Two instruments that ship inside circuitRF and also stand alone.

## harmonicaRF

- Interactive **harmonic load-pull on one device**, at the speed of a mouse drag.
- Fundamental and harmonic terminations, source and load; contours, loadline and power sweep update as you drag.
- An inverse solve answers the question the other way round: what termination gives me this?

{{ui: harmonica-readout-strip | full}}
{{caption: The readout strip: settings, the source and load markers, and the grid's best-power and best-efficiency summaries.}}

## The instrument itself

{{ui: harmonica-instrument | full}}
{{caption: Contours on the load plane, the loadline, the power sweep and the readout strip.}}

## wBond

- **Bondwire arrays as geometry**: profile, span, loop height and pitch, not a number from a table.
- A 3D kernel computes the array network; the result exports to Touchstone and plots like anything else.
- In a workspace a wBond is the **wire layer of a layout cell**; it also runs as a standalone application.

{{ui: wbond-profile}}

## wBond in the layout, and in the schematic

- Arrays draw over their pads in the layout editor, so the wires are part of the artwork.
- The same design generates a schematic symbol with **one pin pair per array**, named after it.
- Controlling parameters carry a change across an array rather than being re-entered wire by wire.

{{ui: wbond-layout | full}}
{{caption: Four bond arrays and their ten wires, drawn over their pads in the layout editor.}}
