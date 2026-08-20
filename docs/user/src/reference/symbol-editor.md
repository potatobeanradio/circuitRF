---
title: Symbol Editor
slug: reference/symbol-editor.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Symbol Editor
lede: The Symbol Editor draws the glyph a cell shows on a schematic — its body art plus the pins that connect it. It is a focused drawing tool, not a cell editor: it never changes a cell's port count or parameters, only how the cell looks.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What it edits</a></li>
      <li><a href="#primitives">Drawing primitives</a></li>
      <li><a href="#pins">Pins &amp; ports</a></li>
      <li><a href="#grids">Two grids: pins vs. art</a></li>
      <li><a href="#orientation">Orientation convention</a></li>
      <li><a href="#tools">Tools &amp; editing</a></li>
      <li><a href="#locked">Locked symbols</a></li>
      <li><a href="#autogen">Auto-generated symbols</a></li>
    </ol>
  </nav>

## What it edits {#what}

A symbol (`.csym`) is a **dumb glyph**: a list of drawing primitives plus a set of pins, each
mapped to one of the cell's ports. It owns geometry only — the *cell* owns the port count,
parameters, and identity, and decides which of its (possibly several) symbols is primary. A
schematic instance draws the cell's primary symbol; editing the symbol updates every instance of
that cell, live.

Open it from *View → Open Symbol Editor* (docked or as a tear-off window), or by opening a `.csym`
file. New scratch symbols can be created without a workspace.

A cell has up to three views and each has its own editor: this one, the
[Schematic Editor](schematic-editor.html) for the electrical contents, and the
[Layout Editor](layout-editor.html) for the physical artwork.

## Drawing primitives {#primitives}

A symbol is built from typed primitives in component-local coordinates (100 units = one
connection-grid square):

<table>
    <thead><tr><th>Primitive</th><th>Use</th></tr></thead>
    <tbody>
      <tr><td>Line, Polyline / Path</td><td>leads, zigzags, general open shapes (free-angle, not Manhattan-routed)</td></tr>
      <tr><td>Rect, Rounded Rect</td><td>device boxes, terminating-impedance frames</td></tr>
      <tr><td>Circle / Ellipse</td><td>source bodies, dots</td></tr>
      <tr><td>Arc</td><td>inductor coils</td></tr>
      <tr><td>Triangle / Polygon</td><td>ground symbol, arrowheads (stroked or filled)</td></tr>
      <tr><td>Quad / Cubic curve</td><td>the capacitor's curved plate</td></tr>
      <tr><td>Sine, Half-wave</td><td>parameterized smart-paths (amplitude, cycles) for AC/tone-source art</td></tr>
      <tr><td>Text</td><td>labels — string, anchor, font size, style (regular / bold / italic / condensed)</td></tr>
      <tr><td>Bitmap</td><td>a reference image to trace over (lowest Z, editable opacity, lockable; not grid-snapped)</td></tr>
    </tbody>
  </table>

Each drawable primitive carries a **stroke width**, a **fill flag**, and a **color role** — never
a literal RGB. Symbols draw in theme roles (*symbol-line*, *symbol-text*), so they stay correct in
light and dark mode automatically. There is no color picker; stroke width and font size/style are
editable, color is not.

## Pins & ports {#pins}

A **pin** is a placed marker mapped to one of the cell's ports. The editor shows the cell's ports
as the set you can map; you place a pin and assign it a port.

<div class="callout note">
    <span class="label">An unmapped port is legal — it's an open circuit</span>
    <p>If a port has no pin, the symbol still works: that port is treated as an open circuit. A half-authored
    symbol is always safe, and a soft panel flags any unmapped port without blocking editing.</p>
  </div>

A pin's *position* is body geometry; its *identity* is the port mapping. The lead you draw from
the body to the pin is ordinary art, but the pin tip itself always lands on the connection grid.

<div class="callout warn">
    <span class="label">Moving a pin can disconnect wires (for now)</span>
    <p>In this release, connectivity is positional, so moving a pin relocates it on every instance and wires at
    the old location become unconnected — the editor warns before applying such a move. circuitRF is committed
    to migrating to logical pin binding (where wires follow a moved pin, as in other schematic editors); the
    position-vs-identity split above is designed to make that a clean addition.</p>
  </div>

## Two grids: pins vs. art {#grids}

This is the [two-grid rule](grid.html) applied to symbols: **pins snap to the connection grid `P`
(100 units)** so placed instances always connect, while **body art snaps to the fine authoring
grid `p` (5 units)** for drawing freedom. Bitmaps are exempt from both — placed and resized freely
as tracing aids. You can toggle the art-grid snap; the pin grid is always enforced.

## Orientation convention {#orientation}

The standard library follows the conventional EDA look:

- **2-terminal parts are vertical** — Resistor, Inductor, Capacitor, sources, Term, Ground: port 1
  at top `(0,-200)`, port 2 at bottom `(0,+200)`. Place them rotated 90° in a horizontal signal path
  (the normal workflow).

- **Multi-terminal boxes/devices are horizontal** — FET, ZPort, SDD, and the auto-generated box
  use left/right ports.

## Tools & editing {#tools}

{{toolbar: symbol}}

The toolbar is grouped by what you are doing, and the groups are worth learning as groups:

- **View** — Zoom to Fit is the one you will press most; a symbol is small and the canvas is not.
- **Select** — the tool everything returns to. Press `S`, or `Esc` twice.
- **Straight and curved outlines** — Line and Polyline for the strokes a symbol is mostly made of;
  Quadratic and Cubic Bézier when a hand-shaped curve is what the part looks like.
- **Closed shapes** — Rectangle, Rounded Rectangle, Circle, Ellipse, Arc, Triangle, Polygon. Reach for
  these for the body of a device rather than drawing four lines.
- **The two RF shapes** — a Sine wave and an Exponential taper, because a coupler and a taper are
  drawn far more often in this application than in a general drawing tool.
- **Text and pins** — the Text tool for labels, the **Pin** tool for the things that actually connect.
  A symbol with no pins is a picture.
- **Bitmap** — for tracing over a datasheet outline; see the note on locking one below.
- **Snap and rotate** — the same snap ladder the schematic uses, and rotation about the origin, which
  is what keeps a pin on grid.
- **Stroke weight** — thin, normal, thick, applied to the selection or to what you draw next.
- **Undo / Redo** — on the symbol's own stack.

Gestures:

- **Two-point drag** — Line, Rect, Rounded Rect, Circle, Ellipse, Arc, Sine, Half-wave.

- **Multi-point click** — Polyline, Polygon, Triangle, Quad/Cubic curve (Enter or double-click to
  commit).

- **Text** — click an anchor, type, Enter to commit.

- **Esc** — cancels the gesture in progress, or clears the selection when idle.

Everything is undoable on the symbol's own undo stack, with full select / move / delete / rotate,
an inspector for the selected primitive's coordinates and text, a resize gripper, and copy/paste
(including between symbols). Any schematic showing the cell re-renders live as you edit.

## Locked symbols {#locked}

The standard-library symbols (R, L, C, V, Tone, Term, GND, Port, FET, ZPort, SDD) and the
annotation symbols (VAR, MEAS, the analysis directive) are **defined but not user-editable**. You
can place them but not edit them; the editor opens a locked symbol read-only. This is a cell-level
flag, so the cell — not the symbol file — owns it.

## Auto-generated symbols {#autogen}

For a cell with no symbol yet, circuitRF can generate one on command (never automatically): an
outer rectangle with a thinner inset, odd-numbered ports down the left edge and even down the
right, each with a short lead and a port-number label inside the box; the box grows in height with
the port count. Pin tips land on the connection grid by construction. A **3-port** symbol is
special-cased to the conventional device look — port 1 on the left at center, ports 2 and 3 on the
right (matching the FET). The result is an ordinary editable symbol you then refine; re-running
generation is an explicit command that regenerates from scratch (discarding manual edits, with a
warning).

---

<p class="small">See also: <a href="schematic-editor.html">Schematic Editor</a> ·
  <a href="layout-editor.html">Layout Editor</a> ·
  <a href="dynamic-symbols.html">Dynamic symbols</a> (how SDD/ZPort pins grow) ·
  <a href="grid.html">Grid &amp; Connectivity</a> · <a href="file-formats.html">File formats</a>
  (<code>.csym</code>). Full design: <code>docs/design/symbol-editor.md</code>.</p>
