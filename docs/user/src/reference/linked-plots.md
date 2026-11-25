---
title: Linked plots and controls
slug: reference/linked-plots.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Linked plots and controls
lede: A plot that follows a marker on another plot, and the knobs, sliders, buttons and readouts that drive plots from the canvas.
keywords: link, linked plot, follow marker, overview detail, slider, knob, button, readout, front panel, control, at marker, interpolated load, @m1
---

A pinned axis of a trace can take its index from somewhere else instead of from a number you typed: a
marker on another plot, or a control on the canvas. Move the marker and the trace follows it. That turns
two plots into an **overview and a detail**: a Smith chart of load-pull contours, and beside it the drive-up
at whichever load the marker is on.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#linking">Linking a pin to a marker</a></li>
<li><a href="#matching">What a link takes from its source</a></li>
<li><a href="#off-grid">A load-pull read between the measured loads</a></li>
<li><a href="#sources">Families, versus traces and bias points</a></li>
<li><a href="#frequency">Frequencies, trials and harmonic-balance drive</a></li>
<li><a href="#controls">Controls on the canvas</a></li>
<li><a href="#kinds">The slider, the knob, the button and the readout</a></li>
<li><a href="#expressions">Control values in expressions</a></li>
<li><a href="#shown-by">Showing a trace by a control</a></li>
<li><a href="#gone">When a source is gone</a></li>
<li><a href="#headless">Without a window</a></li>
<li><a href="#example">The example</a></li>
</ol>
</nav>

{{ui: linked-plots-load}}

## Linking a pin to a marker {#linking}

A pinned axis is one that a trace holds at one sample: the load of a load-pull, the drive step, a bias
point, the curve of a family. Its row on the trace card has a **link toggle** at its end. Turn it on and
the index picker becomes a picker of sources:

- the sources that fit this axis come first, with where each one is (`m1 on Smith 1`);
- then the ones that do not fit, each saying what it supplies, so you can see why;
- then, greyed, any that would close a loop. Its tooltip names the chain.

Pick one, and the row reads `@m1 → 17 (Γ 0.48∠127°)`: the source, the index it chose and what that index
is. The spec box reads `Pout[@m1, :]`, and typing that has the same effect. A typed name is bound to that
marker when you commit it, so renaming the marker later keeps the link.

The other way round is quicker when the marker is already there. Right-click a marker and choose
**Link plots ▸**: it lists every pinned axis elsewhere in the display that the marker fits, ticked where
it is linked already. One click links that plot.

A linked trace's label carries the link as part of the quantity, `Pout @ m1 (gridPoint 17)`, in its
legend row and its axis label.

**Following is live.** Drag the marker and every plot that follows it moves with the drag. A plot that is
slower to compute, such as a set of contours, waits for you to let go, and its card says *Updates on
release*.

**A loop is refused.** If plot A follows a marker on plot B and B follows a marker on A, neither has an
answer. The link that would close the loop is greyed in the picker, and a typed one is refused with the
chain named: "Pout pinned by m1 on Gt, pinned by m2 on Pout".

Making, changing or removing a link is one undo step. A plot that follows a marker records nothing when
it follows; undoing the marker's move takes it back.

## What a link takes from its source {#matching}

A marker knows the sample it sits on: its index along each axis its trace keeps, the value there and the
unit. The pin that follows it takes an index out of that, in this order:

1. **The same sweep is followed exactly.** If the pinned axis is the same axis as the marker's (the same
   name, length and values: two plots of one run), the pin takes the marker's index.
2. **Otherwise a value takes its nearest sample.** A marker at 2.437 GHz on one file's frequency axis pins
   another file's frequency axis at its sample nearest 2.437 GHz. A value past the end takes the end
   sample, and the trace's note says so: "m1 is 3.1 GHz; this axis ends at 3 GHz".
3. **A control's value is matched the same way.** A slider in MHz on a GHz axis takes the nearest sample.
4. **Anything else is not guessed.** The pin stays where it was and the note says what each side is: "m1
   gives freq (Hz); this axis is gridPoint".

**An axis with no unit is matched by value only to an axis of the same name.** Load index 17 is not drive
step 17, and reading one as the other would draw a curve that looks right and is not.

Axes of labels, such as nodes, branches and port numbers, cannot be linked: "the nearest node" has no
answer.

## A load-pull read between the measured loads {#off-grid}

A load-pull measures a grid of loads. A marker on its contours, or on its plot of measured loads, can be
anywhere on the chart. A trace on the same load-pull whose load pin follows that marker reads one of
three things:

- **The marker is on a measured load.** The trace shows that load's own drive-up, exactly.
- **The marker is between loads, and the plot reads *Nearest*.** The trace shows the nearest measured
  load, and its card names that load and how far away it is.
- **The marker is between loads, and the plot reads *Interp*** (the default). The trace shows a drive-up
  interpolated at the marker's load from the measured loads around it, at the plot's compression and
  with the contours' own fit.

{{ui: linked-plots-interpolated}}

**An interpolated curve is drawn dashed**, and its label says so: `Pout @ m1 (interpolated, Γ 0.42∠151°)`.
It is a model of what the device would do there, not a measurement. It covers the drives the fit is built
from, near compression. Beyond the measured loads it is an extrapolation, and the card says so and by how
much. A quantity the fit cannot give is refused by name on the card, and that trace takes the nearest load.

*Interp* and the compression are set on the Plot inspector of any plot holding such a trace, as the
summary table sets them.

A marker on the measured loads is tied to them at first. Right-click it and untick **Snap to Point** to
drag it anywhere; Shift-drag puts it back on a measured load.

**The summary table can read at a marker.** Set its optimum to **At marker** and pick the marker: every
column, the load included, is read at the marker's load, interpolated or nearest as the table says.

## Families, versus traces and bias points {#sources}

**A marker on a family** supplies two things: its X and the curve it is on. A plot of the same run pinned
on the family's axis follows the curve; one pinned on X follows the X.

**A marker on a versus trace** (a quantity plotted against another, such as Id against Vds over the points
of a bias plane) supplies the point it sits on and the X value there, in the X quantity's unit. Because
the X of a bias plane repeats (every point at one Vds lies on one vertical line), such a marker sits on a
point, not just on an X value.

**From I-V to S-parameters.** Model data often lists its bias points as an axis, `point`, with small
companion cubes saying where each point is: `vgs[point]` and `vds[point]`. A marker on an I-V plot linked
to the `point` axis of an S-parameter set takes the bias point nearest the marker's Vgs and Vds, each
weighed by how far it ranges in that file. The note always says which point it took and how far off it was:
"point 10 (vds 6.1 V, vgs -380 mV; m1 at 6 V, -400 mV)". It is the nearest point by design;
interpolating between bias points would be a model.

## Frequencies, trials and harmonic-balance drive {#frequency}

**A contour's frequency** can follow a source too. Beside the frequency picker on the contour card is a link
toggle. Link it to a marker on a gain plot and the contours refit at the marker's frequency when you let go.
An expression that keeps a frequency axis has the same toggle.

**The selected trial** is a source called `selected`. A pin on a trial axis that follows it shows the trial
selected in its own data: the lowest, when several are, and the card says how many. With none selected it
stays on the last one shown.

**Harmonic-balance drive.** In a harmonic-balance sweep the voltages, currents and measurements all share
the sweep's axis, so a marker on Pout against Pin can drive `V[@m1, node, :]`: the waveform or spectrum at
that drive.

## Controls on the canvas {#controls}

A control is an input placed on the canvas: something plots follow that is not a marker on a trace. Add
one from the toolbar's **Add Control** menu: **Slider**, **Knob**, **Button** or **Readout**. It lands
in free space in view and is selected, and the Properties panel shows its inspector.

Every control has a name (`slider1`, `knob1`), unique across the display: markers and controls share one
set of names, and `@slider1` links to it exactly as `@m1` links to a marker. Its label is what it shows;
the name is shown when there is none.

**The face operates; the frame arranges.** Drag a slider's thumb, turn a knob, press a button: that is
the face. Drag the label strip or the border to move the control, with the rest of the selection.
Alt-drag moves it from anywhere. Click the frame to select it; a selected control steps with the arrow
keys. Each gesture is one undo step when it ends, and the plots follow live while you drag.

**Size.** The inspector's **S**, **M** and **L** are fixed sizes. The gripper at the bottom-right corner
resizes freely, and the size reads *Custom*; picking S, M or L again snaps back. A control's drawing grows
with its frame: a Large slider is a bigger slider, not a Small one with more room. Most controls stop at their
Small size; a button goes much smaller, to a lamp beside a plot, and leaves its label off once it would be too
small to read.

**Lock Position.** Right-click a control and choose **Lock Position**. A locked control cannot be moved or
resized, and a drag on its frame works the control instead. It can still be selected, edited, copied and
deleted. With several selected, the item locks them all, or unlocks them all when every one is locked
already. A display arranged as a panel can be saved locked, so it opens ready to use.

Controls are drawn by the same code everywhere, so Copy, Export as SVG or PDF and `circuitrf render` draw
them exactly as the canvas does, as vector graphics.

## The slider, the knob, the button and the readout {#kinds}

{{ui: linked-plots-panel}}

**The slider** has one of two domains:

- **Bound** to one axis of a data source (a source, a quantity and an axis, all picked from lists). Its
  stops are that axis's samples, so it works like a marker on that axis that you move from the canvas. If
  the source goes away, it keeps the range it last read and the inspector says so.
- **Range**: a start, a stop and a step or a number of points, in a unit, linear or log. Its value is a
  number with a unit, and an axis it pins takes the sample nearest it. Double-click either end label to
  type a new start or stop where it is drawn.

It can lie horizontally or vertically, and show or hide its label, ticks, end labels and value.

**The knob** has the slider's two domains and value rules, drawn as a dial over a 270° sweep. Drag on it to
turn it: up or right turns it up, down or left turns it down. Upward, one full sweep is 200 pixels of drag; sideways,
a drag across the dial's width turns it through its whole arc, so a knob turns as readily sideways as up. Both are ten
times finer with Ctrl (Cmd on a Mac). The mouse wheel steps it one stop. On a bound knob, clicking a tick sets that stop.

**The button** is **Toggle** (a click turns it on or off) or **Momentary** (on only while you hold it). Its
value is 1 when lit and 0 when dark. Choose its colour from eight presets or any colour, and put its label
under it or on its face. Lit, it glows in its colour. A momentary press is a gesture, not a setting: it is
not an undo step, and the display is not saved lit.

A pinned axis linked to a button takes index 0 or 1, so a button can flip a plot between the two cases of
a two-element axis, such as two corners or two bias points written as an axis.

**The readout** shows a value as text, with its unit and prefix, beside the controls that set it. It has no
input. Pick its source (a marker, a control or `selected`) and what to show:

- **Value**: a marker's X reading as its info box writes it, or a control's setting. A button reads On or
  Off.
- **Coordinate**: one coordinate the source supplies, such as a marker's Vgs on a family (`m1 · Vgs`).
- **Reading**: a marker's reading on its own trace, its info box's value line.

Its size preset scales the digits. It is the plain way to put `f = 2.437 GHz` on a panel.

## Control values in expressions {#expressions}

In a trace's expression, `@name` outside a slice is a number: the source's value in base SI units. A
control's value, a marker's X, the lowest trial selected:

- `Pout - @slider1` offsets a curve by the slider's value.
- `if(@button1, dB(SP1.S(2,1)), dB(SP1.S(1,1)))` draws S21 while the button is lit and S11 while it is dark.

A button enters an expression as true or false, because a condition must be one; `!@button1` and `&&` work
on it, and `if(@button1, 1, 0)` turns it into a number. A source that has no number, such as a free point on
the Smith chart or a readout, is an error on the trace that says so. These operands count as links, so a
loop through one is refused like any other.

## Showing a trace by a control {#shown-by}

The eye at the right of a trace card binds the trace's visibility to a control: **Shown by** a button, knob
or slider shows the trace while that control's value is not zero, and **Inverted** shows it while the value
is zero. One button can then switch a group of traces, and another group the other way, with no expression.

A hidden trace is still computed, so showing it again is instant. It is not drawn, nor are its markers. Its
legend row and its axis label stay, marked *(hidden)*. If its control is deleted, the trace is shown.

## When a source is gone {#gone}

Delete a marker that a plot follows and the plot does not go blank. It stays where the marker last put it,
and its card says so: "m1 is gone — showing gridPoint 17, where m1 last was". The pin row shows a chip, *m1
is gone*, the picker offers *Link to…*, and the legend strikes the name through. Messages names every plot
that stopped following, once.

The link is kept in the file, so the display reopens the same way. When a marker of that name exists again,
the chip offers **Use the new m1**: one click relinks it. It is never done for you. Undoing the delete
brings the marker itself back, and its plots follow it again at once.

A pasted pair of plots, an overview and a detail, keeps working: the pasted marker is new, and the pasted
detail follows it rather than the original.

## Without a window {#headless}

`circuitrf render` draws a linked display through the same links as the window, and can move a marker or a
control first, for that render only:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf render "Linked Plots.cdd" --tab Load --marker m1=#12 -o load.png
<span class="prompt">$ </span>circuitrf render "Linked Plots.cdd" --tab Panel --control freq=2.2GHz --control measured=0 -o panel.png</code></pre>

`--marker m1=2.4GHz` puts m1 on its trace's sample nearest that value, and `m1=#17` on sample 17.
`--control` sets a slider or knob to its nearest stop (or `#5` to stop 5) and a button to `1` or `0`. A
display has no selection outside a window, so a pin that follows `selected` is drawn where it was saved.
See {{anchor: cli#render|render}} for the rest of the verb.

## The example {#example}

The shipped **Loadpull** example has a display built this way, `Linked Plots.cdd`, with three tabs. Run its
**Loadpull** and **LoadpullPursuit** benches first, then open it:

- **Load**: drag m1 between the measured loads. The detail plot shows Gt and PAE against Pout at m1,
  interpolated and dashed, and the **At marker** table reads the same load.
- **Frequency**: step m2 along the optimum loads against frequency, and the contours refit at m2's
  frequency.
- **Panel**: a knob bound to frequency, a slider bound to the drive, a button that shows the measured
  curves beside the interpolated ones, and a readout of each, all locked in place.

Its README says what each one does. See also {{anchor: data-display#markers|Markers}} and
{{anchor: data-display#contours|Load-pull contours}} on the Data Display page.
