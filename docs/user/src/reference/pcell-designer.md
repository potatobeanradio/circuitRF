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
<li><a href="#placement">Where each part goes</a></li>
<li><a href="#mshape">Drawn metal and Stretch by Parameter</a></li>
<li><a href="#em">Run EM and Optimise</a></li>
<li><a href="#check">The design check</a></li>
<li><a href="#package">Export and import: the PCell package</a></li>
<li><a href="#placed">A designed cell placed in a schematic</a></li>
<li><a href="#headless">Running it headless</a></li>
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
  makes a new designed cell in the workspace, `PCell1` (`PCell2`, … when taken), and opens it. Rename it in the project
  tree.
- **Edit in PCell Designer** on the right-click menu of a placed designed cell, in a schematic or a layout, opens the cell
  it places.
- **File ▸ Import ▸ PCell Package…** imports a `.cpcell` into the workspace (see [below](#package)).

One window is open per cell. Asking for a cell that is already open brings its window forward.

A designed cell is an ordinary cell folder with a schematic, a symbol and a `.ccell`. What marks it as designed is a
`PCellDesign` entry in its `.ccell`, which also names its layout generator `cpcell:<cell name>` and sets `EmCopper` (see
[File formats](file-formats.html#cpcell)).

## The window {#window}

| Part | What it does |
|---|---|
| **Canvas** | The cell's layout at the values in the **Value** column, drawn as `circuitrf render` draws a layout. It is redrawn after every change. You cannot draw on it: you change the schematic, and the canvas shows what the schematic places. |
| **Parameters** | One row per cell parameter: name, default with its unit, **Minimum**, **Maximum** and description. **+** adds a row and **−** removes one. A parameter is a number: a length, an angle or a count. |
| **Value** | The value the canvas and the response are drawn at, typed as a default is (`400 um`, or `400` in the parameter's unit). It is not saved. Use it to see the cell at the ends of its range. |
| **Response** | The cell's S-parameters by its circuit model, recomputed a quarter of a second after you stop typing. The sweep is the schematic's first S-parameter analysis, else 1 to 20 GHz. |
| **Text** | The cell's netlist (`.cnl`), kept up to date. Edit it and press **Apply**: the text is merged into the schematic by designator. A changed value changes the part, a new designator adds a part, and a removed one removes it. A part whose type changes is refused: give it a new designator instead. When the nets are unchanged the wires stay as drawn; otherwise they are redrawn, and every part you kept keeps its place. |
| **Save** | Writes the schematic, the `.ccell` and the cell's own layout. The saved layout is always the one at the parameters' defaults, never at the **Value** column. |
| **Undo**, **Redo** | Every gesture and every **Apply** is one step. |

Gestures on the canvas, each one undo step:

- **Drop a Library part on a pin.** The part is added to the schematic with its pin on that pin's net. A pin already
  joined to another part's pin is refused, because a tee is a part you place, not one the designer invents. A pin on
  ground puts the new pin on ground.
- **Drag an offset part by its pin** to move its offset (see [below](#placement)).
- **Drag a corner of an MSHAPE**, or select corners and use **Stretch by Parameter** (see [below](#mshape)).

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

An anchor that does not resolve, an expression that does not evaluate, or two offsets that pull one chain in different
directions is reported, and that offset is not applied. Nothing is moved to hide it.

## Drawn metal and Stretch by Parameter {#mshape}

[MSHAPE](components.html#mshape) is copper you draw: a closed outline (`Form = "polygon"`) or a centreline of width `W`
(`Form = "path"`). Its corners are the rows `X[i]`, `Y[i]` and its pins the rows `PinX[k]`, `PinY[k]`, `PinW[k]`,
`PinDir[k]`, all in the part's own frame. Each row is an expression like any other value, so a corner can be `Lpad` or
`W/2`.

`Model` says what a circuit simulation does with it:

- `Model = "open"`: its pins are left open. A pad at a line's end simulates as an open end.
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
| `pcell.mshape-pins`, `pcell.mshape-model` | A drawn shape with no pin, or a `Model` other than `none` and `open` |
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
  layout and [optimised](em-cells.html#placed) with the cell's own parameters as variables (`X1.Lpad`).

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
