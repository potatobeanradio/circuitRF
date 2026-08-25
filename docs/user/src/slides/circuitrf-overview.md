---
title: circuitRF
kind: slides
deck: overview
slug: slides/circuitrf-overview.pdf
lede: A lightweight, cross-platform RF circuit simulator — and what it would mean to adopt one.
---

# What it is

circuitRF in one sentence, and the shape of the tool.

## What circuitRF is

{{stats: 4::core analyses | 3::desktop platforms | MIT::core licence}}

- A **cross-platform RF circuit simulator**: DC, S-parameters, harmonic balance, loadpull and sourcepull.
- Built around the **RF design loop** — gain, match, power, efficiency, intermodulation, terminations.
- **Not a SPICE simulator.** It does not chase time-domain transients, and it does not pretend to.
- One design database with three views per cell — **symbol, schematic and layout** — plus a planar EM solver, a 3D bondwire kernel and an interactive load-pull bench.

> **The short version** — if your day is spent asking **what does this network do across frequency, and what happens when I drive it hard**, this is the tool shaped for that question.

## Why it exists

- RF designers work in tools that are **large, expensive and tied to one operating system**. circuitRF is none of those.
- It **starts in seconds**, runs the same on Windows, macOS and Linux, and installs from a single `.msi`, `.dmg` or `.deb`.
- Every file it writes is **human-readable or a standard**: a netlist you can diff, Touchstone in and out, NumPy on the way to Python.
- The core is **MIT-licensed**, so it can be read, forked, embedded and audited — nothing about your design flow is locked inside it.

## The workspace

- A workspace is an **ordinary folder** with a `.cws` file and one sub-folder per cell — the project tree and your file browser always agree.
- Documents in tabs; Project, Library, Properties and Messages docked around them, and every one of them movable.
- **File ▸ New Schematic** gives a scratch sheet you can wire and simulate with no project at all.
- Every panel and every tab can be dragged, docked, floated or hidden, and the arrangement is remembered per workspace.

## The workspace window

{{ui: workspace-overview | full}}
{{caption: A schematic open in the document area, the Project, Properties, Library and Messages panels around it, and a layout waiting in the second tab.}}

# What you can do with it

The four analyses, the two solvers, and the two instruments.

## Draw a circuit

- Place from a **searchable palette**, wire on a connection grid, push into a sub-cell and pop back out.
- Parameters are **expressions, not numbers** — one expression engine serves variables, cell parameters, device equations and measurements.
- Analyses and measurements attach to a **test bench**, never to a cell, so a cell stays reusable.
- Nonlinear models included: five large-signal FETs, diodes, an equation-defined device, and any compiled vendor model through the worker interface.

{{ui: schematic-editor}}

## The analyses

- **DC** — operating point, linear and nonlinear. What harmonic balance biases from.
- **S-parameters** — multiport network parameters over a sweep, with renormalisation.
- **Harmonic balance** — steady-state nonlinear, single- and two-tone. Pout, gain, efficiency, PAE, spectra, IMn.
- **Loadpull and sourcepull** — sweep the termination over a grid and contour the figures of merit.
- **Parametric sweeps** wrap **any** of them in one or more swept variables.

{{ui: analyses-all-types}}

## Loadpull is first-class

- Sweep the load reflection coefficient over a grid, run harmonic balance at every point, and **contour** the result.
- **Loadpull Pursuit** searches for the terminations that optimise a metric instead of making you hunt by eye.
- **Measured data plots exactly like simulated data** — a `.lpcwave` or `.spl` file from the bench overlays your simulation on the same axes.

{{ui: plot-loadpull-contours}}

## harmonicaRF: load-pull at the speed of a drag

- An **interactive harmonic load-pull bench** for a single device: drag the load marker and the contours, the loadline and the power sweep follow.
- Fundamental and harmonic terminations, source and load, with an inverse solve that answers **what termination gives me this?**
- Ships as part of circuitRF **and as a standalone application**.

## harmonicaRF

{{ui: harmonica-instrument | full}}
{{caption: Power and efficiency contours on the load plane, the loadline, the power sweep, and the readout strip.}}

## Layout and electromagnetics

- A **layout view per cell**, drawn to real dimensions against a technology — a PCB or MMIC stackup you define or import.
- A **planar method-of-moments solver** with a layered-medium Green's function: microstrip, stripline, multi-level metal, ground vias.
- **Adaptive frequency sampling** solves few points and models the rest; the kernel picks itself from the geometry and says which it picked and why.
- Layout and schematic are reconciled in both directions, so the artwork and the circuit cannot quietly disagree.

{{ui: layout-editor}}

## Set the EM problem up, then leave it

- Stackup, ports and sweep in one panel, with the resolved ports and the mesh report shown back to you.
- The solver **refuses a problem it cannot do well** and names the reason, rather than running for twenty minutes and producing a wrong number.
- Results land as an ordinary S-parameter dataset, so they plot and cascade like anything else.

{{ui: em-setup-compact}}

## Design rules are checked, not assumed

- Every technology carries its own **design rules** — minimum width, spacing, enclosure, overlap, density — and a layout is checked against them.
- Violations land in a panel: what broke, which rule, by how much, click to zoom. Markers draw over the artwork.
- The panel always names the **technology it checked against**: a clean result against the wrong process looks exactly like a clean result against the right one.
- A violation can be **waived with a reason**, so a known exception stays visible instead of being deleted.

## The check, on a two-rule process

{{ui: drc-violations | full}}
{{caption: A 2 um neck breaking minimum width and a 2 um gap breaking minimum spacing, marked on the artwork and listed with the rule each broke}}

## Vendor kits import

- Point circuitRF at a kit and it reads what it can: the **layer technology and its rules**, the symbols, the Python PCells, the subcircuits and the compiled model library.
- The report is never all-or-nothing. It lists what was read, what is recognised but unsupported, and what is unrecognised — each with the action that would close the gap.
- Kit parts then place, simulate and draw like anything else, and a kit's **corners** become a per-test-bench choice.

## What an import tells you

{{ui: pdk-import-report | full}}
{{caption: The import report. This kit is invented for the documentation - real kits are licensed - but the report and the dialog are the application's own}}

## Two tools you would otherwise buy separately

### Match — closed-form matching-network synthesis
- Synthesises a bandpass network that **absorbs both terminations**, with no optimiser to babysit.
- Every valid transform set is listed, simplest first, with its achieved response.

### wBond — bondwire arrays as geometry
- Models an array from its **actual 3D geometry**, not a number you looked up in a table.
- Exports S-parameters, drops into a schematic as a symbol, and draws itself in the layout view.

## The Match Designer

{{ui: match-designer | full}}
{{caption: Specification, the synthesised ladder, the response it achieves, and the transform rack.}}

## Reading the results

- **Rectangular, Smith, polar and table** plots, with markers that read values off the surface.
- A **trace card** says where every number came from: the run, the signal, the transform, the reference impedance.
- Measured and simulated traces share the axes — model against measurement on one plot.

## A plot, and the card that draws it

{{ui: plot-smith-data | full}}
{{caption: S(1,1) on a Smith chart, swept 1-10 GHz}}
{{ui: plot-inspector-smith | full}}
{{caption: The same run, the same signal, on the trace card that put it there}}

# Fitting it into your flow

Formats, automation, platforms and licence.

## Results are Python-native

- Every run writes a **NumPy dataset** — named, labelled, unit-bearing arrays — so results load straight into Python or MATLAB.
- Export to `.npy`, `.mat`, Touchstone, `.spl` and `.lpcwave` is built in.
- Nothing is trapped in a proprietary result database, and no add-on is needed to get a number out.

```
import numpy as np
run = np.load("results/AmpSweep/run.npy", allow_pickle=True).item()
pout = run["Pout_dBm"]        # labelled, swept, ready to plot
```

## It runs headless

- The same engine runs from the command line, so a sweep can live in a script, a Makefile or CI.
- A netlist that works headless works when opened in the GUI — the measurements are evaluated by the identical code.

```
circuitrf sparam amp.cnl --freq 1GHz:3GHz:50MHz -o amp.s2p
circuitrf hb    pa.cnl   --set Pavs=22 -o sweep.npy
```

> **Why this matters for adoption** — a tool you can only drive by hand cannot be regression-tested. This one can, and the project regression-tests itself the same way.

## Open formats, no lock-in

- The netlist, the schematic, the symbol, the technology and the workspace are **text you can read and diff**.
- Touchstone in and out; measured loadpull files read directly; GDSII artwork imports.
- Vendor kits are integrated through an **OpenPDK layout with Python PCells** and a documented model-worker protocol — a kit is data, not a plug-in binary we compile.

## What circuitRF is not

- **Not a transient simulator.** No time-domain SPICE analysis, and none is planned for v1.
- **Not a full-wave 3D EM solver.** The MoM kernel is planar and says so; a genuinely 3D structure needs a 3D tool.
- **No LVS, and no sign-off.** Layout-versus-schematic is not implemented and there is no tape-out guarantee. **Design-rule checking is** — see the next slide.
- **Not finished.** It is in active development, and the documentation tracks the current build.

> **Deliberate** — the boundary is stated so it can be checked. Every one of these is a documented non-goal rather than a gap nobody has admitted to.

## Platforms, install and licence

- **Windows, macOS and Linux**, from one codebase — `.msi`, `.dmg` and `.deb` installers, each built on its own platform.
- **Core is MIT.** Read it, fork it, embed it, audit it.
- No licence server, no seat count, no dongle, and no per-machine activation to schedule around.

{{stats: .msi::Windows | .dmg::macOS | .deb::Linux}}

## Evaluating it in an afternoon

- **File ▸ New Schematic**, drop two `Term`s around a network, add an S-parameter analysis, press Run. Minutes, not a project setup.
- Then run the same netlist headless and diff the Touchstone against whatever you use today.
- Then try the part that is hard elsewhere: a loadpull grid on a power device, and harmonicaRF on the same device.
