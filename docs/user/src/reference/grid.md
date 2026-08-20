---
title: Grid & Connectivity
slug: reference/grid.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Grid &amp; Connectivity
lede: Connecting components must be easy, and connections must be unambiguous: "do these two pins touch?" has to have exactly one answer. circuitRF achieves both with **two grids** — a coarse connection grid that all electrical points snap to, and a fine authoring grid for everything cosmetic.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#two">The two grids</a></li>
      <li><a href="#why">Why two grids</a></li>
      <li><a href="#exact">Connection is exact equality</a></li>
      <li><a href="#paste">Pasting across grids</a></li>
      <li><a href="#changing">Changing the grids</a></li>
    </ol>
  </nav>

## The two grids {#two}

<table>
    <thead><tr><th></th><th>Connection grid <code>P</code></th><th>Authoring grid <code>p</code></th></tr></thead>
    <tbody>
      <tr><td><strong>Default pitch</strong></td><td>100 units (the classic 100-mil EDA pitch)</td><td>5 units (<code>p = P/20</code>)</td></tr>
      <tr><td><strong>Job</strong></td><td>electrical connection</td><td>cosmetic placement</td></tr>
      <tr><td><strong>What snaps to it</strong></td><td>component pins, wire endpoints, wire bends, junction dots</td><td>symbol body art, label offsets, net-label positions, canvas objects</td></tr>
      <tr><td><strong>Can you change it?</strong></td><td>fixed for a design (design-level)</td><td>freely, anytime (cosmetic)</td></tr>
    </tbody>
  </table>

Every electrically-connectable point lands on `P` **exactly** — an integer multiple, verified by
arithmetic, not by tolerance. Everything that carries no electrical meaning lives on the finer
`p`, which is always a refinement of `P` (`p = P/k`), so a point on `P` is always also on `p`. A
connection point can never fall "between" cells.

## Why two grids {#why}

One grid can't serve both masters. "Easy to connect" wants a *coarse* grid (few, well-spaced
targets a wire snaps to cleanly); "freedom to place art and labels" wants a *fine* grid. Splitting
them resolves the conflict: pins/wires/dots on coarse `P` so connection is trivial and exact;
bodies/labels/decorations on fine `p` so authoring has room.

<div class="callout warn">
    <span class="label">Counter-intuitive but important</span>
    <p>Authoring freedom is controlled by <code>p</code>, <strong>not</strong> <code>P</code>. Making
    <code>P</code> finer does not give more freedom — it makes connection <em>harder</em> (targets get denser,
    easier to snap to the wrong one) and breaks the 100-mil convention every EDA tool and RF engineer assumes.
    Want finer placement? Shrink <code>p</code> (increase <code>k</code>). Leave <code>P = 100</code>.</p>
  </div>

## Connection is exact equality {#exact}

Two pins are connected **if and only if** their connection-grid coordinates are equal — not
"within a few pixels." Because placement, wire drawing, and dragging all snap to `P` at the moment
of input, coincident points are bit-for-bit equal, and the extracted netlist's connectivity is
decided by exact grid coincidence. There is no fuzzy radius that could disagree with what you see
on screen. This is what makes the netlist trustworthy: a connection, once made, is unambiguous.

A small junction dot is drawn only where incident wire segments form a real branch (a horizontal
*and* a vertical segment meet). Three collinear segments overlapping draw no dot — but still read
as connected.

## Pasting across grids {#paste}

If you copy from a schematic authored on a different connection grid and paste into one using `P =
100`, circuitRF **detects, warns, and snaps**:

<ol class="steps">
    <li>The clipboard payload records the grid it was authored on.</li>
    <li>On paste, if the source grid differs, you get a message — e.g. <em>"Pasted content was created on a
      50-unit grid; this schematic uses 100. Pins were snapped to this grid — verify connections."</em></li>
    <li>Pasted pins, wire endpoints, and dots are snapped to the destination grid (as one undoable action);
      decorations snap to the fine grid. Internal coincidences within the pasted group are preserved where
      possible, and any point that can't land cleanly is reported — never a silent off-grid pin.</li>
  </ol>

## Changing the grids {#changing}

- **Display grid** (which lines are drawn) and the **authoring grid `p`** — cosmetic, change
  anytime. `p` is constrained to `p = P/k` (a refinement of `P`).

- **Connection grid `P`** — fixed for a design. It is stored in the schematic file and is stable;
  changing it could strand existing connection points off the new grid, so it is treated as a
  deliberate migration, not a slider. In v1, `P` stays at the design's grid size; you change only
  `p` and the display.

## The schematic editor {#editor}

Everything above is enforced by the editor you draw in. Its canvas, its toolbar button by button,
the Library Palette and how a component gets from one to the other are on their own page:
[The Schematic Editor](schematic-editor.html).

---

<p class="small">See also: <a href="schematic-editor.html">Schematic Editor</a> ·
  <a href="file-formats.html">File formats</a> (where the grid is stored) ·
  <a href="symbol-editor.html">Symbol Editor</a> (pins on <code>P</code>, art on <code>p</code>). Full design:
  <code>docs/design/grid-and-connectivity.md</code>.</p>
