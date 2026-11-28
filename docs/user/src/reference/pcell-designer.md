---
title: PCell Designer
slug: reference/pcell-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > PCell Designer
lede: A parameterised cell made from circuitRF's own primitives - its layout a function of its parameters at every value, its schematic its circuit model - exported as a PCell package that any workspace places like a kit's cell.
keywords: PCell Designer, designed cell, PCell, parameterised cell, parametric cell, MSHAPE, drawn metal, offset placement, Stretch by Parameter, handle, .cpcell, PCell package, export, import, cpcell, EmCopper, pcell verb, Tools menu, layout generator
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is</a></li>
<li><a href="#open">Opening it</a></li>
<li><a href="#window">The window</a></li>
<li><a href="#markers">Markers and dimensions</a></li>
<li><a href="#operations">Operations</a></li>
<li><a href="#placement">Where each part goes</a></li>
<li><a href="#mshape">Drawn metal and Stretch by Parameter</a></li>
<li><a href="#em">Run EM and Optimise</a></li>
<li><a href="#check">The design check</a></li>
<li><a href="#package">Export and import: the PCell package</a></li>
<li><a href="#placed">A designed cell placed in a schematic</a></li>
<li><a href="#headless">Running it headless</a></li>
<li><a href="#examples">Examples</a></li>
</ol>
</nav>

## What it is {#what}

The PCell Designer makes a **designed cell**: a [PCell](pcells.html) built from the parts in the Library (lines, bends,
tees, coupled lines, vias, footprints, kit and Python PCells) and from drawn copper ([MSHAPE](components.html#mshape)).
Three rules make it a PCell rather than a drawing:

- **Its layout is a function of its parameters.** Parts joined by a net are placed pin to pin, as
  [Update Layout from Schematic](layout-editor.html) places them. A part that no net joins is placed by an **offset** from
  another part's pin. A drawn shape's corners and pins are expressions. There is no coordinate typed by hand anywhere, so
  the layout is right at every value of every parameter.
- **Its schematic is its circuit model.** The cell simulates as any hierarchical cell does, from its own schematic. No
  second model is fitted or stored.
- **It places like any PCell.** Once designed, the cell is a Library part with its parameters in the Properties, drag
  handles in the layout, regeneration when a value changes, an identity in [LVS](lvs.html) and copper in EM. It goes to
  another workspace as one file, a **PCell package** (`.cpcell`).

<figure class="figure">
<img src="../../images/pcell-designer/designer.png" alt="The PCell Designer on the PadTee cell: the toolbar along the top, the layout at the current values on the left (a 50 ohm line with a bend, a tee, an open stub and a rectangular pad at the stub's end), the parameter table on the right with W, L1, Lstub and Lpad, the circuit response below it, and the cell's netlist text along the bottom." style="width:100%;height:auto">
<figcaption>The <b>PadTee</b> cell of the <b>PCell Designer</b> example: a 50 Ω line with a bend and a tee, the tee's
stub ending in a drawn pad whose length is the parameter <code>Lpad</code>.</figcaption>
</figure>

## Opening it {#open}

- **Tools ▸ PCell Designer…** opens the designer on the active schematic's cell when that cell is designed. Otherwise it
  makes a new designed cell in the workspace, `PCell1` (`PCell2`, … when taken), and opens it. A new cell starts with
  Port 1 and its ground, so the first part you drop has a pin to join. Rename it in the project tree.
- **Edit in PCell Designer** on the right-click menu of a placed designed cell, in a schematic or a layout, opens the cell
  it places.
- **File ▸ Import ▸ PCell Package…** imports a `.cpcell` into the workspace (see [below](#package)).

One window is open per cell. Asking for a cell that is already open brings its window forward.

A designed cell can also come from filter synthesis: the Filter Designer's **Make PCell…** (or `circuitrf filter … --realiser R --pcell <name>`) makes the selected realisation a designed cell. It holds the part `F1`, its site parts and two Ports, and each synthesised length, gap and width is a parameter with a ±20 % range. Such a cell keeps its filter specification. The **Filter metrics** panel reads its circuit response, or an EM run, back to the prototype and shows each check with pass or fail, as the Filter Designer does. See [Make PCell](filter-designer.html#make-pcell).

A designed cell is an ordinary cell folder with a schematic, a symbol and a `.ccell`. What marks it as designed is a
`PCellDesign` entry in its `.ccell`, which also names its layout generator `cpcell:<cell name>` and sets `EmCopper` (see
[File formats](file-formats.html#cpcell)).

## The window {#window}

| Part | What it does |
|---|---|
| **Canvas** | The cell's layout at the values in the **Value** column, drawn as `circuitrf render` draws a layout. It is redrawn after every change. Each gesture on it changes the schematic, and the canvas shows what the schematic places. |
| **Parameters** | One row per cell parameter: name, default with its unit, **Minimum**, **Maximum** and description. **+** adds a row and **−** removes one. A parameter is a number: a length, an angle or a count. |
| **Value** | The value the canvas and the response are drawn at, typed as a default is (`400 um`, or `400` in the parameter's unit). It is not saved. Use it to see the cell at the ends of its range. |
| **Response** | The cell's S-parameters by its circuit model, recomputed a quarter of a second after you stop typing. The sweep is the schematic's first S-parameter analysis, else 1 to 20 GHz. |
| **Text** | The cell's netlist (`.cnl`), kept up to date. Edit it and press **Apply**: the text is merged into the schematic by designator. A changed value changes the part, a new designator adds a part, and a removed one removes it. A part whose type changes is refused: give it a new designator instead. When the nets are unchanged the wires stay as drawn; otherwise they are redrawn, and every part you kept keeps its place. |
| **Save** | Writes the schematic, the `.ccell` and the cell's own layout. The saved layout is always the one at the parameters' defaults, never at the **Value** column. |
| **+ Port** | Adds the next port, grounded, on the last free pin. A Port dropped from the Library on a pin does the same there. |
| **Undo**, **Redo** | Every gesture and every **Apply** is one step. |

Gestures on the canvas, each one undo step:

- **Drop a Library part on a pin.** The part is added to the schematic with its pin on that pin's net. A pin on ground
  puts the new pin on ground. On an empty cell the first part joins Port 1.
- **Drop a part on a joint** (a pin already joined to another part). The joint becomes a tee (`MTEE`): the two parts on
  its through pins, the new part on its branch, its widths `W1`, `W2` and `W3` those of the three parts. On a pin of a
  tee the tee becomes a cross (`MCROSS`). A joint with a part that has no width, such as a port or a pad, is refused
  with a sentence saying so.
- **Drop a part anywhere else.** It is placed by an offset from the nearest pin: `Dx` and `Dy` are where you dropped it,
  measured from that pin in its own frame and written as numbers in the cell's length unit (that of its first length
  parameter), `Angle` 0. Rewrite them in parameters afterwards to make the place follow the cell.
- **Draw MSHAPE.** Turn on **Draw MSHAPE**, click the corners, and double-click or press Enter to finish (Esc
  cancels). Start on a pin and the shape's pin 1 joins it, as wide as that part's `W`; start anywhere else and the shape
  is placed by an offset from the nearest pin. The corners are written relative to pin 1 in the cell's length unit,
  ready for **Stretch by Parameter**, and `Model` is `"open"`.
- **Drag an offset part by its pin** to move its offset (see [below](#placement)).
- **Drag a corner of an MSHAPE**, or select corners and use **Stretch by Parameter** (see [below](#mshape)).

## Markers and dimensions {#markers}

**Markers.** The response is a [Data Display](data-display.html) plot, and its markers work as they do there: add one
from the plot's menu, drag it along a trace or step it with the arrow keys, and read its values in its box. Every edit
recomputes the response, and each marker moves to the new trace of the same name (`S21`, `EM S21`) at the same
frequency. A marker on the EM curve waits while an edit has cleared that curve, and comes back with the next
**Run EM**.

**Dimensions.** With **Dimensions** on (the toolbar; it is on until you turn it off, and the setting is kept), each
parameter is drawn on the canvas as a dimension: a line between the two points it sets, in the style of the layout
editor's rulers, with the measured length and a label such as `Lpad = 300 µm`. The lines are redrawn with the canvas,
so changing a **Value** moves them and their labels. They come from what the cell already holds:

| Where a parameter is used | The dimension |
|---|---|
| A Stretch by Parameter binding on an `MSHAPE` | From the corner that stays to the first corner that moves, along the stretch's axis. |
| An offset whose **Dx**, **Dy** or **Angle** names a parameter | From the anchor pin to the part's own pin. |
| A part's parameter that names one (`TL1.L = L1`, `W = W`) | The span of that part's own drag handle for the parameter, once per expression. |

When the expression combines terms, the label is the expression and its value (`Lstub/2 = 2 mm`). The unit is the
parameter's own.

<figure class="figure">
<img src="../../images/pcell-designer/dimensions.png" alt="The PCell Designer on the PadTee cell with Dimensions on: rulers over the layout labelled W = 1.1 mm, L1 = 3 mm, Lstub = 4 mm and Lpad = 300 µm." style="width:100%;height:auto">
<figcaption>The <b>PadTee</b> cell with <b>Dimensions</b> on: each parameter drawn where it acts on the copper.</figcaption>
</figure>

## Operations {#operations}

Beside placing parts, a designed cell can be shaped by **operations** on their copper: Merge, Subtract, Intersect,
Exclusive, Grow (or shrink), Repeat, Fillet and Chamfer, the icon buttons under the canvas (each names itself in its tooltip). The
**Operations** panel lists the cell's history in order. Each step changes
the copper that the parts draw, and it is applied again at every value, so the cell stays a function of its
parameters.

- **Select operands.** Click a part or a drawn shape on the canvas; Shift-click adds or removes one. The selection is
  listed under the canvas, first selected first. An operand is a part's copper (its designator), a whole layer
  (**Add layer** adds `layer:<name>`, for example `layer:Soldermask Top`), or a **tool**.
- **Make tool** turns the selected `MSHAPE` into a cutter (its `Role` is `"tool"`): its outline is an operand and is
  never copper.
- **Merge**, **Subtract**, **Intersect** and **Exclusive** take two operand sets: the first selected, and the rest
  (Subtract is the first minus the rest). The result replaces the first set on the step's layer, and the second set
  is left as it is.
- **Grow** widens the selection by its amount, or narrows it when the amount is negative. Name another layer in
  **onto** and the result goes there instead, which makes a mask opening, a keep-out or a clearance.
- **Repeat** copies the selection into rows and columns at the pitches. The copies are copper only, with no pins and
  no circuit model.
- **Fillet** rounds the selection's corners by the radius, and **Chamfer** cuts them straight, the size measured along
  each edge (a right angle gets a 45° cut). Outer corners lose copper and inner corners gain it. Where two parts meet in
  line, the joint is not a corner and stays as placed.

Amounts, counts and pitches are written as any value is: `20 um`, `20um`, or an expression such as `Lmid/30`. One
that names a parameter is drawn as a dimension.
Each press adds one step, as one undo step. In the panel, change a step's operands (the first set, then `|` and the
rest), amount or pitches and press **Apply**; move it earlier or later with the arrows; tick **Suppressed** to keep it
in the history without applying it; or delete it. Each change is one undo step, and the canvas, the response and the
dimensions follow.

A step that would remove the copper under a part's pin, or would join or separate the ports, is not applied, and its
row says why. A step that changes copper on a conductor layer makes the cell **EM-only**: it runs by EM and optimises
with `em=direct`, and the response line says so, naming the step (`no circuit model: op1 (Subtract) changes the
copper`). Steps on mask, silk or keep-out layers keep the circuit model. Tick **Keep model** on a step to accept the
circuit model as an approximation. Operations are geometry, so they are not in the netlist text;
`circuitrf pcell ops <cell>` lists them, as the MCP `pcell` tool does with `what=ops`.

LVS compares a cell with operations by which ports each piece of its copper joins. Placed in another design, the cell is
compared as one part, like any PCell.

<figure class="figure">
<img src="../../images/pcell-designer/operations.png" alt="The PCell Designer on a copy of the PadTee cell with a square tool on the line TL3 and one operation, op1 Subtract TL3 minus SH9, in the Operations panel; the line has a square notch where the tool was, and the response line says the cell has no circuit model." style="width:100%;height:auto">
<figcaption>A <b>Subtract</b> in the history: a square tool cut out of <code>TL3</code>.</figcaption>
</figure>

## Where each part goes {#placement}

Parts joined by a net are placed pin to pin, starting from the first chain in the schematic. Report lines under the
canvas say where each part went and name the **root**, the part the layout starts from.

A part that no net joins needs to be told where it goes: a pad beside a line, a second line across a gap. It carries an
**offset placement**, kept with the part in the schematic file (the component's `Offset`, see
[File formats](file-formats.html#cpcell)). The netlist text does not carry it, because it is placement and not circuit:

| Field | Meaning |
|---|---|
| Anchor | The pin it is placed from, as `<part>.<pin>`: `TL1.2` |
| Pin | Which of this part's pins goes to the offset point |
| Dx | Along the anchor pin's outward direction |
| Dy | 90° counter-clockwise from it |
| Unit | The unit of Dx and Dy |
| Angle | The turn, in degrees. 0 means this pin faces the anchor pin, as at a joint |

Dx, Dy and Angle are expressions, so `Dx = g` makes a gap that follows the parameter `g`. All three zero is exactly a
joint. Everything joined to the offset part moves with it. When you drag an offset part on the canvas, the change is
added to Dx and Dy as a number: `g` becomes `g + 30`, and the parameter stays in the expression.

Select one part and the **Part** panel shows its Dx, Dy and Angle (when it has an offset), its rotation, and
**Flipped**, the mirror that decides which way a bend turns or which side a tee's branch leaves. **Apply** writes them
into the schematic as one undo step; after a turn or a flip the wires are redrawn to the part's nets.

An anchor that does not resolve, an expression that does not evaluate, or two offsets that pull one chain in different
directions is reported, and that offset is not applied. Nothing is moved to hide it.

## Drawn metal and Stretch by Parameter {#mshape}

[MSHAPE](components.html#mshape) is copper you draw: a closed outline (`Form = "polygon"`) or a centreline of width `W`
(`Form = "path"`). Its corners are the rows `X[i]`, `Y[i]` and its pins the rows `PinX[k]`, `PinY[k]`, `PinW[k]`,
`PinDir[k]`, all in the part's own frame. Each row is an expression like any other value, so a corner can be `Lpad` or
`W/2`.

`Model` says what a circuit simulation does with it:

- `Model = "open"`: its pins are left open. A pad at a line's end simulates as an open end.
- `Model = "short"`: its pins are one node, an ideal junction. A star pad where several resistors meet simulates as
  their common node.
- `Model = "none"`: it has no circuit model. A cell that holds one can only be simulated by EM: a run by circuit models
  is refused naming the shape, and an optimisation uses `em=direct`.

Write the two words quoted, as `Form="polygon"` and `Model="open"` in the netlist text.

**Stretch by Parameter** ties corners to a parameter. Shift-click the corners that should move, type the parameter and the
direction (degrees, in the shape's own frame), and press **Stretch by Parameter**. The shape stays as it is at the
current values, and each selected corner moves by δ along that direction when the parameter grows by δ. The binding is
kept with the part, so a placed copy of the cell shows a drag handle for that parameter. Pins do not move with a stretch.

## Run EM and Optimise {#em}

**Run EM** saves the cell, makes an EM setup for its layout when it has none (a [Planar](mom-engine.html) setup, the
schematic's Ports as its ports), and solves the layout at the current values. The EM response is drawn over the circuit
response. **Cancel** stops it.

**Optimise** saves the cell and opens its schematic with the [Optimizer](optimization.html). Goals, variables, `em=correct`
and `em=direct` work there as for any [cell simulated by EM](em-cells.html#optimise).

## The design check {#check}

**Export…** runs the design check first, and `circuitrf pcell check` runs it on its own. It reports, among others:

| Id | What it says |
|---|---|
| `pcell.unjoined`, `pcell.placement` | A part that is neither joined nor offset-placed, or an offset that cannot be made, at the defaults, at a parameter's Minimum or at its Maximum |
| `pcell.choices` | A parameter that offers choices. A designed cell's parameters are numbers |
| `pcell.parameter-is-var` | A parameter that is also a VAR of the schematic |
| `pcell.range` | A Minimum above its Maximum |
| `pcell.no-ports`, `pcell.port-num` | No Port, or Ports not numbered 1, 2, … |
| `pcell.mshape-pins`, `pcell.mshape-model` | A drawn shape with no pin, or a `Model` other than `none`, `open` and `short` |
| `pcell.no-circuit-model` | A drawn shape with `Model = "none"`: the cell is EM only |

An error stops the export. The check plans the cell at each parameter's ends because a layout that holds together at the
default can still break at a limit: a stub shorter than its tee, or two parts that overlap.

## Export and import: the PCell package {#package}

**Export…** writes `<cell>.cpcell`: the cell folder (its `.ccell`, its schematic and its symbol) and a small manifest. It
holds no layout, no EM setup and no results, because the generator draws the layout wherever the cell is placed. The same
cell exported twice gives the same bytes.

**File ▸ Import ▸ PCell Package…** unpacks it into the workspace as an ordinary cell. Importing the same package again is
reported as already imported. A different package with the same cell name is refused, and nothing is overwritten. A
package that names a layer the workspace's technology does not have is refused with the layers it misses.

## A designed cell placed in a schematic {#placed}

Place the cell from the Library like any other cell, and set its parameters on the instance. Then:

- **Update Layout from Schematic** draws each placement at its own values. Two placements with different values get
  different copper.
- In the layout, each Stretch by Parameter binding is a **drag handle** on the placed cell, inside the parameter's range.
- **Update Schematic from Layout** links a designed cell placed in the layout to a schematic instance, as it links a kit
  part.
- **LVS** knows the placed cell by its generator, `cpcell:<cell name>`, and pairs its pins with the schematic instance's
  pins by port number.
- **EM** solves the placed cell's layout as copper (`EmCopper`), so a bench placing it can be simulated by EM of its own
  layout and [optimised](em-cells.html#optimise) with a placement's own value as the variable (`tune X1.Lpad`): each
  point's layout draws `X1` at that value.

A drawn shape with `Model = "open"` is an open end to the circuit model, so its size has no effect there: in the
example, `X1`'s S21 is the same at every `Lpad`, while `Lstub` moves it. `em=correct` corrects the circuit model by EM but
searches on the circuit model, so it cannot move a variable that only changes such a shape: it stops at the start, saying
the next point is the geometry it has already solved. Optimise such a variable with `em=direct`, which searches on EM
itself. On the example's bench (Planar, 2,700 unknowns, 6-8 GHz at 3 points), `em=correct` stopped after one solve with
`X1.Lpad` at 200 µm; `em=direct` with the `pattern` algorithm and a budget of 8 solves moved it to 600 µm in about nine
minutes, the worst |S11| from −0.22 dB to −0.13 dB.

<figure class="figure">
<img src="../../images/pcell-designer/bench-layout.png" alt="The Bench layout: two PadTee cells joined by a 50 ohm line between two ports, the first with a 200 micrometre pad, the second with a 400 micrometre pad." style="width:100%;height:auto">
<figcaption>The example's <b>Bench</b> after <b>Update Layout from Schematic</b>: <code>X1</code> with
<code>Lpad = 200 um</code> and <code>X2</code> with <code>Lpad = 400 um</code>, each its own copper.</figcaption>
</figure>

## Running it headless {#headless}

`circuitrf pcell` is the designer with no window. Each noun calls the function the window's command calls, so both write
the same files (see the [command line](cli.html#pcell)):

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf pcell new . PadTee
<span class="prompt">$ </span>circuitrf pcell check PadTee
<span class="prompt">$ </span>circuitrf pcell render PadTee -o padtee.svg --set Lpad=400um
<span class="prompt">$ </span>circuitrf pcell export PadTee -o PadTee.cpcell
<span class="prompt">$ </span>circuitrf pcell import PadTee.cpcell --into ../other-workspace</code></pre>

Over MCP it is the `pcell` tool, with `what=new|check|render|export|import`.

## Examples {#examples}

The **PCell Designer** example (File ▸ New ▸ Examples) holds `PadTee`, the cell this page uses, and five showcase
cells. Each comes with a bench that places it and a Data Display, and its README gives the closed-form estimates and
the numbers.

**A 3-way Wilkinson divider** (`Wilkinson3`, 2.45 GHz). A star of three resistors cannot be drawn on one layer with
every output outside it, so the cell divides in two Wilkinson stages, each with one resistor on its own ring: an
[MWPD](components.html#mwpd-unequal) with `K` = 1.4142 sends a third to port 2 and two thirds to an equal MWPD, which
halves them into ports 3 and 4. A phase line of [MDLY](components.html#mdly)s on port 2, as long at `F` as the second
stage, keeps the outputs in phase. Every line is stated at `F`, so changing `F` re-plans both rings and the lines. By
circuit models:
- S21, S31 and S41 are −5.03, −5.02 and −5.02 dB, within 0.2° of each other in phase;
- the input match is −40 dB and every output's −30 dB or better;
- the isolation between outputs is −39 dB or better.

<figure class="figure">
<img src="../../images/pcell-designer/wilkinson3.png" alt="The PCell Designer on the 3-way Wilkinson: two Wilkinson rings in a row, an 0603 across each ring's gap, a phase line rising past the second ring to port 2, and the circuit response from 1.5 to 3.5 GHz." style="width:100%;height:auto">
<figcaption><code>Wilkinson3</code> in the designer. The first ring <code>W1</code> divides one third to two thirds, the
second <code>W2</code> halves the two thirds, and <code>D1</code>-<code>D3</code> carry port 2 past it. The response
shows the three outputs near −5 dB and the input match at 2.45 GHz.</figcaption>
</figure>

**A symmetric 3-way divider** (`Wilkinson3Sym`, 2.45 GHz). A cross feeds three quarter-wave arms of 86.6 Ω: one
turns up, one down, and the middle one runs on through a serpentine. Resistors only between neighbouring outputs
cannot match every port, so a third, a 2010 between the outer outputs whose body bridges the middle output line,
makes the three a delta of 150 Ω. The copper each resistor spans is of fixed size and the rest of every arm is one
`MDLY` at `F`, so every loop closes at any `F`. By circuit models at 2.45 GHz:
- the three outputs are in phase, at −4.95, −4.79 and −4.95 dB;
- every match is −29.9 dB or better and every isolation −31 dB or better;
- placed at `Re = 86.6 Ω` with the bridge left off (`Rb = 1 GΩ`, the second bench), S33 and S42 rise to −15 dB.

<figure class="figure">
<img src="../../images/pcell-designer/wilkinson3-sym.png" alt="The PCell Designer on the symmetric 3-way divider: a cross at the input, an arm turning up and one turning down, a serpentine middle arm, two small resistors between neighbouring outputs and a long one bridging the middle output line, and the circuit response from 1.5 to 3.5 GHz." style="width:100%;height:auto">
<figcaption><code>Wilkinson3Sym</code> in the designer. <code>R23</code> and <code>R34</code> join neighbouring outputs at
the arm ends; <code>R24</code> joins ports 2 and 4 across the middle line <code>O3</code>. <code>Lm</code>, the
serpentine's leg, trims the middle arm.</figcaption>
</figure>

**A spurline bandstop** (`Spurline`). A 50 Ω line with an L-shaped slit, drawn as one polygon with `Model = "none"`,
so it is simulated by EM only. The slit's far end follows `Ls` by Stretch by Parameter, so `Ls` is a drag handle
wherever the cell is placed. A Planar sweep in 25 MHz steps puts the notch at 5.38 GHz, against a quarter-wave
estimate of 5.45 GHz. Two `em=direct` passes on `X1.Ls`, the second over a narrowed range, found `Ls = 9.21 mm`, which
the bench ships with: its notch is at 4.99 GHz.

<figure class="figure">
<img src="../../images/pcell-designer/spurline.png" alt="The PCell Designer on the spurline: a straight line with a long slit along its axis that turns out through one edge." style="width:100%;height:auto">
<figcaption><code>Spurline</code>: one drawn polygon between two feed lines. The slit's far end is tied to
<code>Ls</code>.</figcaption>
</figure>

**A dual-mode patch bandpass** (`DualModePatch`). A square patch with one corner cut, fed along two adjacent edges
across a gap. No net joins the feeds and the patch, so they are placed by offset: the patch from the first feed's end,
the second feed from the patch's pin. A Planar sweep shows the two modes the cut splits, at 5.10 and 5.35 GHz,
either side of the unperturbed patch's 5.22 GHz. The feeds couple across a gap at the board's spacing rule, which
leaves them under-coupled: S21 peaks at −7.3 dB, and `em=direct` on the gap and the cut gained only 1 dB.

<figure class="figure">
<img src="../../images/pcell-designer/dual-mode-patch.png" alt="The PCell Designer on the dual-mode patch: a square patch with its top right corner cut, a feed along its left edge and a feed along its bottom edge." style="width:100%;height:auto">
<figcaption><code>DualModePatch</code>. The patch and the second feed are placed by offset, the gap <code>s</code> from
the feeds.</figcaption>
</figure>

**A slotline divider** (`Slotline`, 5 GHz). A slot is cut in the ground, and three microstrips cross it: the input at
its middle and two outputs either side, each crossing a quarter-wave microstrip-to-slotline transition. One output
crosses it the other way round, so the outputs are 180° apart. The ground is drawn copper: one `MSHAPE` on the ground
layer, whose outline runs in to the slot along a zero-width cut, since a polygon has no hole. Planar's ground is an
unbroken plane, so the bench solves in its 3D view, which turns the drawn ground into copper with the slot in it. An
EMerge solve on its Standard mesh (11 points over 4-6 GHz, 42 s) gives −5.95 and −5.94 dB at 5 GHz, 179-180° apart
across the band. Most of the loss is the slot radiating.

<figure class="figure">
<img src="../../images/pcell-designer/slotline.png" alt="The PCell Designer on the slotline divider: three microstrips crossing a long slot in the drawn ground, the input in the middle and an output at each side." style="width:100%;height:auto">
<figcaption><code>Slotline</code>. The slot runs across the drawn ground <code>SH1</code>; each microstrip runs on past it
to an open end.</figcaption>
</figure>
