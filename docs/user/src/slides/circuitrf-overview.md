---
title: circuitRF
kind: slides
slug: slides/circuitrf-overview.pdf
lede: A lightweight cross-platform RF circuit simulator
---

## What circuitRF is

- A lightweight, cross-platform RF circuit simulator: DC, S-parameters, harmonic balance, loadpull and sourcepull.
- Not a SPICE simulator. It is built for the RF design loop, not for general-purpose transient analysis.
- One design database, three views per cell: symbol, schematic and layout.
- Runs headless from the command line as well as from the GUI, so a netlist that works in CI works when opened.

## The schematic editor

- Place from a searchable palette, wire on a grid, push into a cell and pop back out.
- Parameters are expressions, not numbers: one expression engine serves variables, cell parameters, device equations and measurements.
- Analyses and measurements attach to a test bench, never to a cell.

{{ui: schematic-editor}}

## Electromagnetics

- A method-of-moments solver for planar structures, with a layered-medium Green's function.
- Set the stackup, the ports and the sweep; the analysis picks itself from the geometry and says which it picked and why.
- Adaptive frequency sampling solves fewer points and models the rest.

{{ui: em-setup-editor}}

## Where the figures in this deck come from

- Every picture here is a vector capture of the running application, taken by one command.
- No screenshots, no bitmaps, and nothing hand-drawn: when the interface changes, the deck is regenerated.
- The same content tree produces the HTML documentation and this deck.
