---
title: Create from Image
slug: reference/create-from-image.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Create from Image
lede: Drop a picture of a layout and get its copper as a layout, or its circuit as a schematic — what was read drawn over the picture, and every guess one click from being corrected.
keywords: image, picture, screenshot, PNG, JPEG, paste, trace, bitmap, scale, two points, line impedance, layers, colours, preset, underlay, schematic from image, layout from image, recognise, recognize
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#ways-in">Ways in</a></li>
<li><a href="#dialog">The dialog</a></li>
<li><a href="#kind">What kind of picture it is</a></li>
<li><a href="#make">What to make of it</a></li>
<li><a href="#canvas">The picture, and what was read</a></li>
<li><a href="#scale">Scale</a></li>
<li><a href="#layers">Layers and presets</a></li>
<li><a href="#technology">Technology</a></li>
<li><a href="#advanced">Advanced</a></li>
<li><a href="#underlay">The picture under the result</a></li>
<li><a href="#reads">What reads well, and what does not</a></li>
<li><a href="#failure">When nothing can be read</a></li>
</ol>
</nav>

## What it does {#what}

A picture of a layout &mdash; a board viewer's screenshot, a die plot, a figure in an application note &mdash; holds
the circuit, but not in a form a simulator can use. **Create from Image** reads it:

- **Create Layout from Image** traces the picture's copper into a layout: filled shapes on your technology's layers,
  drill holes as vias, sitting exactly on the picture.
- **Create Schematic from Image** traces it the same way and then reads the circuit out of the traced copper with
  [Create Schematic from Artwork](artwork-to-schematic.html) &mdash; the same recognition, the same parts table, the same
  report. The new cell holds both: the traced layout and the schematic recognised from it.

Like reading artwork, it is a best attempt that you then check. Everything it guessed is shown on the picture and
said in the report.

## Ways in {#ways-in}

Wherever there is a picture, the command is one gesture away &mdash; and it is only offered where it can work.

| Where | Rows | What opens |
|---|---|---|
| **Design** menu | **Create Schematic from Image…**, **Create Layout from Image…** | The dialog, empty, ready for a drop, a paste or **Browse…**. Enabled whenever a workspace is open; no document needs to be in front. |
| **Edit** menu | **Paste Image as Schematic…**, **Paste Image as Layout…** | The dialog, already reading the clipboard's picture. Enabled only when the clipboard holds a picture &mdash; a copied image, or one picture file copied in a file manager (two files, or a file that is not a picture, leave them greyed). The clipboard is looked at when the Edit menu opens and when you come back to the window. |
| Right-click a picture placed in a **layout** | **Trace Image into This Layout…**, **Create Schematic from Image…** | Tracing into this layout puts the copper in this layout, over the picture, at the size the picture is placed (scale **As placed**), in this layout's technology &mdash; one undo step. **Create Schematic from Image…** makes a new cell and offers **As placed** as its scale. |
| Right-click a picture placed in a **schematic** | **Create Schematic from Image…**, **Create Layout from Image…** | A new cell each. A schematic has no physical size, so **As placed** is not offered; set the scale in the dialog. |
| Right-click a picture file in the **Project** panel | **Create Schematic from Image…**, **Create Layout from Image…** | The dialog, already reading that file. Offered on a Known File and on a file inside the workspace folder whose extension is one circuitRF reads. |

A placed picture whose file cannot be found offers none of these rows &mdash; point it at its file first (on a layout,
**Resolve Path…** on the same menu). A locked picture is offered like any other: locking stops it moving, not being
read.

Dropping a picture onto a canvas still places it as a bitmap, and dropping one onto the Project panel still adds it
as a Known File; the right-click rows above are the next step. Dropping one onto the dialog reads it.

## The dialog {#dialog}

The dialog opens empty, as one drop zone. Drop a picture on it, paste one (**Cmd/Ctrl+V**), or **Browse…**. It reads
PNG, JPEG, BMP, GIF (the first frame) and WebP; a file that is not a picture is refused in the status line, and
nothing else changes.

{{ui: image-dialog-empty}}

With a picture, the top of the dialog is the picture and what to do with it; below it is the body the
[artwork dialog](artwork-to-schematic.html) has &mdash; target, options, parts table, report and **Create** &mdash;
with the picture itself taking the left two-thirds. **Replace…** and **Paste** read another picture in its place.

{{ui: image-dialog-layout-picture}}

## What kind of picture it is {#kind}

**It is** says what the picture was read as. **Auto** decides from the picture &mdash; how much of it is ink, whether
the ink is filled or stroked, how many colours it has, whether its lines run straight &mdash; and its label says what
it decided, with a dot for how sure it is: green sure, amber fairly sure, red a guess. Hover over it to see what it
measured. **Schematic** and **Layout** override it; a picture Auto reads wrongly is read your way after one click.

A sparse board of one trace width can look to Auto like a schematic drawing. If it does, choose **Layout**.

Reading schematic pictures &mdash; wires, symbols and their values &mdash; is not available in this version. A picture
read as a schematic says so, and **Create** waits.

## What to make of it {#make}

**Make** is **Schematic** or **Layout**. A schematic picture makes a schematic only: draw the schematic first, then use
**Update Layout from Schematic**. **Layout** is disabled for it, and its tooltip says so.

The **Target** is a new cell, named after the picture (a pasted picture is `pasted_image_1`, `_2`, …). A name that is a
cell this command made relabels **Create** as **Replace**, and a history checkpoint is taken before anything is
replaced. A cell this command did not make is never replaced.

Making a schematic also shows the recognition's options &mdash; ground, ground vias, coplanar lines, the frequency
sweep and the digits values are written with. They are the artwork dialog's, and mean exactly what they mean there.

## The picture, and what was read {#canvas}

What was read is drawn over the picture, each kind toggled from the legend at the top left:

| Legend | What it shows |
|---|---|
| **Copper** | Each traced region, outlined in its layer's colour. |
| **Drills** | Each drill hole, as the circle read. |
| **Parts** | Each recognised part, boxed with its designator. |
| **Lines** | Each line, as its centre line, labelled with its component and width &mdash; `MLIN W=450 µm`. |
| **Ports** | Each port, with its number. |
| **Ignored** | Colours read as *Ignore*, hatched grey. |
| **Unknown** | Whatever was not read &mdash; a part of unknown kind, a value not found, a colour with no layer &mdash; in amber. |

Click a part on the picture to select its row in the parts table; select a row, or an anchor in the report, to
highlight its part on the picture.

| Gesture | Does |
|---|---|
| Wheel, or pinch | Zoom about the pointer. |
| Right- or middle-drag, or Space with a drag | Pan. |
| Drag | Read only the rectangle dragged. **Whole picture** reads all of it again. |
| Double-click | Fit the picture. |
| Escape | Put down a measuring tool. |

## Scale {#scale}

A picture has no millimetres in it, and a wrong scale is wrong by a factor while looking entirely right &mdash; so the
scale is never a silent guess. The **Scale** row shows the length of one pixel and what it was taken from, and the
combo beside it lists every piece of evidence found:

| Evidence | Chosen by itself? |
|---|---|
| **From parts** &mdash; chip land patterns that agree on one scale, at least three of them | Yes |
| **Two points…** &mdash; click two points on the picture, then type the distance between them | You choose it |
| **Line impedance…** &mdash; click a trace and type its Z0; the technology gives its width | You choose it |
| **File resolution** &mdash; the dots per inch the file states | Never: a screenshot's resolution describes a screen |

The distance needs its unit &mdash; `1.6 mm`, `62 mil`, `500 um`. A bare number is refused where you typed it: it could
be any of them. Beside the value, the resolution line says how finely the picture can be read: *1 px = 20 µm; a
451 µm trace is read to ± 2 %*.

With no usable evidence, the row is highlighted, the picture asks *Set the scale: Two points…*, and **Create** waits.

{{ui: image-dialog-needs-scale}}

## Layers and presets {#layers}

A picture's colours are its only layer information. **Layers** lists each colour with its share of the picture and
what it is read as: one of your technology's layers, **Drill**, **Board outline**, **Silkscreen**, **Background** or
**Ignore**. The first reading maps the colour touching most of the picture's border to the background, round holes to
**Drill**, and the rest to your copper layers by area, top first; a colour that is two translucent layers drawn over
each other is read as both. Hover over a row to see its colour on the picture.

Change a row and it is marked with a dot: every later reading keeps it.

Pictures from one source use the same colours every time, so a map can be kept. **Save Preset…** keeps the map and the
options under a name; **Preset** applies one. When a picture's colours match a saved preset, the status line offers
it &mdash; *Colours match preset "viewer dark"* &mdash; and applies it only when you click **Apply**. Presets belong to
you, not to a workspace.

## Technology {#technology}

A picture says nothing about the stackup. The traced layout and the recognised schematic use the technology chosen
here &mdash; the workspace's default to begin with &mdash; and the report says once that the stackup was not read from
the picture. Changing it reads the picture again.

## Advanced {#advanced}

Collapsed until you open it, and it stays as you left it. Each change reads the picture again.

| Option | Does |
|---|---|
| **Minimum feature** | Regions and holes smaller than this, in square pixels, are specks and are removed. |
| **Simplify** | How far, in pixels, a traced outline may move to lose a vertex. |
| **Snap edges**, **and 45°** | Edges near 0° and 90° (and 45°) are made exact. |
| **Snap within** | How near, in degrees, an edge must be to be snapped. |
| **Colours, at most** | The most colours the picture is separated into. |
| **Merge within ΔE** | Colours closer than this are one colour. |
| **Keep the source picture under the result** | See [below](#underlay). |

## The picture under the result {#underlay}

The written layout and schematic carry the source picture behind them, locked and faded to 35 %, placed so each
shape lies on what it was read from. Checking the result is then comparing in place. The picture is copied into the
cell's folder; it is an ordinary bitmap, which you can hide, fade or delete like any other, and it is never exported,
checked or meshed. Clear **Keep the source picture under the result** to leave it out.

## What reads well, and what does not {#reads}

| Picture | Expect |
|---|---|
| A layout drawn by a program &mdash; a board viewer, a layout editor, a die plot, a figure | Good: flat colours and sharp edges trace to within half a pixel. |
| A picture large enough that the narrowest trace is several pixels wide | Good. A trace one or two pixels wide is read, but its width is uncertain &mdash; the resolution line says by how much. |
| A picture with each layer in its own colour | Good. Translucent layers drawn over each other are read too. |
| A JPEG saved at low quality | Fair: its smudged edges and colours are read, less exactly. |
| A photograph of a real board | Poor: perspective, glare and solder mask hide the copper. |

## When nothing can be read {#failure}

A picture with no drawing in it is shown dimmed, with one sentence saying what was seen &mdash; *No drawing found: a
photograph-like picture with no straight line work* &mdash; and two links, **Read it as a schematic** and **Read it as a
layout**, for a picture you know better than that. A reading that finds nothing to make &mdash; no copper at the mapped
colours, no technology &mdash; is shown the same way, with the control that answers it highlighted. **Create** stays
disabled, and its tooltip is that sentence.

{{ui: image-dialog-no-drawing}}
