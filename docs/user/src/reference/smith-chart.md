---
title: Smith Chart
slug: reference/smith-chart.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Smith Chart
lede: Narrowband impedance matching done by hand, with one curve per component and a handle on every joint - drag it and watch the load move.
keywords: Smith chart, matching, impedance match, narrowband, L match, gripper, trajectory, constant Q, VSWR, conjugate match, stub, transmission line, csmith, reflection coefficient, tuner
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it is, and when to reach for the Match Designer instead</a></li>
<li><a href="#generator">Stating the generator</a></li>
<li><a href="#cascade">Building the cascade</a></li>
<li><a href="#trajectory">What the per-element curve means</a></li>
<li><a href="#drag">Dragging a gripper</a></li>
<li><a href="#loadpoints">The load points, and their targets</a></li>
<li><a href="#q">Constant Q and the swept band</a></li>
<li><a href="#overlays">Overlays and markers</a></li>
<li><a href="#clipboard">Out to a schematic, and back</a></li>
<li><a href="#headless">Running it headless</a></li>
<li><a href="#limits">What it will not do</a></li>
</ol>
</nav>

## What it is, and when to reach for the Match Designer instead {#what}

The Smith Chart tool is a **scratchpad for narrowband impedance matching, done by hand**. You state
one impedance &mdash; the thing being matched &mdash; and then add two-pin components outward from it,
in series and in shunt, watching where the impedance at the far end lands.

The chart draws **one curve per component**: the path the impedance takes as that component grows from
nothing to the value you set. So a network is not a list of numbers, it is a visible walk across the
chart. Every joint in that walk carries a **gripper**, and dragging one changes the component it
belongs to and moves everything downstream of it live.

That last sentence is the whole tool. The question a matching network actually poses is *"if I make
this one a bit bigger, where does the load end up?"*, and this answers it by letting you drag it and
look.

{{ui: smith-window}}

Open it from **Tools &rsaquo; Smith Chart** for a new scratch document, or by opening a `.csmith` in the
project tree. It is an ordinary circuitRF **document**, not a separate application: it gets a tab, a
`&bull;` dirty mark, Save and Save As, Undo and Redo on the keys you already use, tear-off and
floating, and it is restored when you reopen the workspace. It also needs no workspace at all &mdash; a
scratch chart opens with nothing else loaded, and **Save As** gives it a home later.

<div class="callout note">
<span class="label">This tool and the Match Designer answer different questions</span>
<p>Reaching for the wrong one wastes an afternoon, so it is worth being blunt about which is which.</p>
<p><b>The Match Designer</b> (<a href="match.html">its chapter</a>) <i>computes</i> a network for you.
It is <b>broadband</b>: you give it two terminations and a band, and it synthesises a bandpass ladder
from a Fano-optimum prototype, absorbs both terminations into it, and hands you a list of solutions to
choose between. Use it when the band is wide enough that the answer is a filter problem.</p>
<p><b>This tool</b> computes nothing. It is <b>narrowband</b>, and <b>you are the algorithm</b>: it
evaluates what you build, instantly, and draws it. Use it when the band is narrow enough that two or
three parts will do, when you want to <i>see</i> why a match is narrow, or when you have a network
already and want to know what one component is doing in it.</p>
<p>There is deliberately no <b>Solve</b> button here, and no optimiser, no goal and no error function.
Adding one would put the Designer's synthesis in two places, and the second copy would be worse.</p>
</div>

## Stating the generator {#generator}

The generator is **an impedance and nothing else** &mdash; no available power, no dBm. Everything in
this tool is a linear immittance.

It is a table: one row per frequency, each carrying a resistance and a reactance.

| | |
|---|---|
| **One row** | The ordinary case &mdash; *"50 &#8486; at 2 GHz"*. The design frequency is then free to be anything. |
| **Several rows** | What makes the per-frequency load points interesting. Rows are kept **sorted by frequency**, and two rows at one frequency is a refusal naming the frequency rather than a silent last-wins. |

Every cell is edited the way every other value in circuitRF is: **double-click to open**, type,
**Return** to commit, **Escape** to revert, clicking away commits. Type a unit and it is honoured
&mdash; `2`, `2 GHz` and `1800 MHz` all mean what they look like, and opening the editor pre-selects
only the number so the unit is left alone.

**Import .s1p&hellip;** reads a one-port Touchstone file and replaces the table with **one row per file
frequency**, converting S<sub>11</sub> to an impedance against the file's own stated reference. Two
things about it are worth knowing:

- **The numbers are copied in; the path is kept only as provenance.** A `.csmith` holds the impedances,
  not a reference to the file, so the document stays portable and a moved or archived workspace cannot
  break it. The path is shown, and **Re-import** repeats the read.
- That is the **opposite** of how overlays work ([below](#overlays)), and deliberately: an overlay is
  reference material you are comparing against, while the generator is part of the design.

**Conjugate** negates every row's reactance, once. It is an ordinary undoable edit and **not a
persistent flag** &mdash; pressing it twice puts the table back. A flag would mean the number in the
table and the number the tool uses disagreed, and there is no way to display that which does not
eventually mislead somebody.

<div class="callout note">
<span class="label">The design frequency is free, but it must be inside the table</span>
<p><b>Design f</b> is what the trajectories are drawn at, what the reactances are computed at, and what
the status strip reports. It need <i>not</i> be a row of the table &mdash; Z<sub>gen</sub> is
interpolated linearly in R and X between the two rows that bracket it.</p>
<p>A design frequency <b>outside</b> the table's span is a <b>refusal</b> with the span in the
sentence, never an extrapolation. A single-row table is the stated exception: one row is one
impedance, flat, and every frequency is legal against it.</p>
</div>

## Building the cascade {#cascade}

The network is an ordered list. Element 0 is nearest the generator; each element is either **series**
(in the through path) or **shunt** (from the through path to ground). There are no branches, no
nesting and no sub-circuits &mdash; that constraint is what makes a per-element curve *mean* something,
because a walk across a chart has to be a walk.

{{ui: smith-network-strip}}

| | |
|---|---|
| **Add &#9662;** | Appends at the end, nearest the load. |
| **Insert &#9662;** | Places before the selected element. With nothing selected it appends, because *before nothing* and *at the end* are the same place in a list. |
| **Delete** | Removes the selected element; the chain closes up. |
| **&#8597;** / drag | Reorders. Dragging an element along the strip does the same thing. |
| **enabled** | Unticking an element makes it contribute nothing and draw no curve &mdash; **without deleting it**. It keeps its values and its place. That is the difference between trying something and losing it. |

These are the elements, and every one of them is a component circuitRF already has &mdash; which is
what makes the copied network ([below](#clipboard)) a circuit that really simulates.

| Element | Placement | What it is |
|---|---|---|
| **R**, **L**, **C** | series or shunt | The three single-parameter parts. |
| **SRLC** | series or shunt | R, L and C in series in one part &mdash; a real capacitor with its ESR and ESL, rather than three components wired together. |
| **PRLC** | series or shunt | R, L and C in parallel &mdash; a tank. |
| **Z1P** | series or shunt | A complex impedance, **constant over frequency**. The frequency independence is the point of it. |
| **S1P** | series or shunt | A one-port Touchstone file &mdash; a measured part. |
| **S2P** | **series only** | A two-port Touchstone file. A 2-port with its second port grounded is a different component than the one you placed, and a shunt one-port is what S1P and Z1P are for. |
| **TLIN** | series | An ideal transmission line with its own Z<sub>0</sub> and electrical length. |
| **Open stub**, **Shorted stub** | shunt | The same line as a stub, far end open or grounded. |

Selecting an element shows its **sliders** underneath &mdash; one for an L, three for an SRLC, two for
a line, and none at all for S1P or S2P, whose value is a file. Each row is a label, a slider and a
value you can type. The slider is logarithmic over a decade either side of the current value for R, L,
C and Z<sub>0</sub>, and linear for an electrical length and for the parts of a Z1P; **typing a value
outside the range re-centres the range rather than clamping the value**. Dragging is live: every step
re-evaluates and redraws, and one drag is one undo entry.

<div class="callout note">
<span class="label">A line's length is quoted at its own reference frequency</span>
<p>A TLIN carries both a characteristic impedance and an electrical length, and the length is stated
<b>in degrees at the element's own F<sub>ref</sub></b>, scaling as &theta;(f) = E&middot;f/F<sub>ref</sub>.
That is what makes a line come apart at the band edges like a real one.</p>
<p><b>F<sub>ref</sub> defaults to the design frequency when the element is placed and then stays put.</b>
It does not follow the design frequency afterwards: a line that silently re-specified itself whenever
you retuned would be a different physical line each time, and the per-frequency load points would stop
meaning anything. It is an editable field on the element's row, so you can move it deliberately.</p>
</div>

### Mirroring the drawing {#mirror}

The **&#8646;** button on the strip's toolbar (**M**) flips the drawing so the generator sits on the
right and the cascade grows leftward.

{{ui: smith-network-mirrored}}

**It mirrors the drawing and nothing else.** The element order does not change, the circuit does not
change, and the chart does not mirror &mdash; the &Gamma; plane's orientation is fixed by physics
(inductive above the real axis, capacitive below), and a mirrored Smith chart is simply a wrong one.
The symbols reflect along with their positions, which matters for a part like an S2P whose port 1
marking means something.

It is a view setting: it lives in the document, it marks it dirty, and it is one undo entry.

## What the per-element curve means {#trajectory}

This is the idea the whole tool is built on, so it gets its own section.

**Each enabled element draws one curve, from the impedance at its input to the impedance at its
output, by scaling that element's immittance from zero to its set value.** A series element sweeps
Z<sub>in</sub> + t&middot;Z<sub>e</sub> and a shunt element sweeps Y<sub>in</sub> + t&middot;Y<sub>e</sub>,
for t from 0 to 1; a line sweeps its electrical length from zero to its full value.

{{ui: smith-trajectories}}

For the everyday parts that is exactly the classical construction, and it produces exactly the
classical curves:

| | |
|---|---|
| a **series** reactance | walks a **constant-resistance** circle |
| a **shunt** susceptance | walks a **constant-conductance** circle |
| a **series** resistance | walks the real-part line |
| a **line** | rotates about **its own** Z<sub>0</sub> &mdash; the figure's third curve is a 75 &#8486; quarter wave taking 50 &#8486; to 112.5 &#8486; &mdash; which is why a tool that fixed lines at 50 &#8486; could not draw the most common move there is |

Nothing about the familiar picture changes. What the rule buys you is that the multi-parameter parts
need no special case: an SRLC's Z<sub>in</sub> + t(R + jX) is a straight segment in the Z plane and so
a circular arc on the chart &mdash; one curve, one gripper, no discontinuity.

<div class="callout note">
<span class="label">Why it scales the immittance and not the component value</span>
<p>"From nothing to its value" most naturally suggests sweeping the capacitance or the inductance
itself, and that reading does not survive contact with a capacitor: as C &rarr; 0 the reactance
&minus;1/&omega;C runs to &minus;&infin;, so an SRLC's curve would leave the chart at one end and come
back. That is a true picture of a nonsensical question.</p>
</div>

Three details you will meet:

- **A small arrowhead at the middle of each curve** says which way the walk runs. Two adjacent arcs
  sharing a gripper are otherwise ambiguous about direction.
- **A stub longer than a quarter wave draws a closed circle.** Its susceptance sweeps to +&infin; and
  returns from &minus;&infin;, which on the chart is not a discontinuity at all: it is the whole
  constant-conductance circle, traversed through the short at &Gamma; = &minus;1.
- **An impedance with a negative real part is drawn, not hidden.** An active S2P, or a Z1P with a
  negative R, puts a node outside the unit circle and the chart grows to show it. Clamping it back
  inside would be a lie about a stability result.

## Dragging a gripper {#drag}

There is a gripper at **every node of the walk** &mdash; N+1 of them for N elements, drawn as a small
hollow ring in the curve's own colour, brightening under the pointer and filling while you drag.

- **Node 0 is the generator, and it is an anchor rather than a handle.** It is drawn so the walk has a
  visible start, and it does not drag: the generator is edited in its own panel, and a drag there would
  have to guess which row of the table you meant.
- **Node k drags element k&minus;1** &mdash; the element that produced it. Everything downstream
  follows, live. Dragging a mid-cascade part and watching the load point move is what this tool is for.
- **The last node drags the last element.** It does *not* solve the network: there is no load element
  and nothing terminates the cascade, so the far end is simply where you read.

A drag changes **one** parameter &mdash; the element's **active parameter**, which is the one whose
slider you last touched, and which starts at a sensible default per type (L for an SRLC, C for a PRLC,
the electrical length for a line). The active row is marked in the slider panel, so the connection
between *the slider I just used* and *the handle on the chart* is visible rather than remembered. Click
a slider's label to make it the active one without moving anything.

Only the part of your drag the parameter can actually reach is used. That is not an approximation
&mdash; it is what *drag along the arc* means, and it is why the gripper follows the curve rather than
the cursor.

<div class="callout note">
<span class="label">A drag that asks for something unbuildable pins, and says so</span>
<p>Inductances, capacitances and resistances are non-negative; a line's Z<sub>0</sub> is positive; an
electrical length is non-negative. A drag that would demand otherwise <b>pins at the boundary and the
status strip names the parameter and the limit</b>. It does not quietly produce a negative inductance,
and it does not stop following your hand either &mdash; so you can drag back out of the pin.</p>
</div>

**One drag is one undo entry**, pushed when you release, carrying the value from when you pressed. The
same is true of a slider drag, a Q drag, a paste and a mirror toggle.

## The load points, and their targets {#loadpoints}

Every row of the generator table puts a **load point** on the chart, labelled with its frequency. The
design frequency's point is drawn emphasised and the others are secondary. Together they are the
picture of the network coming apart at the band edges &mdash; which, on a narrowband match, is the
thing you are actually deciding about.

Beside each one is a faint **&#8853; target glyph**, at &Gamma;(conj(Z<sub>gen</sub>)) for that
frequency.

<div class="callout note">
<span class="label">Everything in the strip is against the chart's own Z<sub>0</sub></span>
<p>The status strip states, for the design frequency: the load impedance, &Gamma; in polar and
rectangular, <b>VSWR</b>, and <b>mismatch</b> &mdash; and all four are taken against the chart's own
reference impedance <b>Z<sub>0</sub></b>, which is what "matched to 50 &#8486;" means. Land the load
point on the middle of the chart and &Gamma; goes to zero, VSWR to 1 and mismatch to 0 dB.</p>
<p><b>mismatch is the power that reflection costs</b>, &minus;10&middot;log<sub>10</sub>(1&nbsp;&minus;&nbsp;|&Gamma;|<sup>2</sup>)
&mdash; the same &Gamma; the VSWR beside it is made of, said in decibels. It is not a return loss:
a return loss gets more negative as a match improves, and this goes to zero.</p>
<p>The &#8853; glyphs are a <b>different</b> question and carry no number. Landing a frequency's load
point on its own glyph is the conjugate match to the generator, which on a network that takes a device
to 50 &#8486; is a different point from the middle of the chart.</p>
</div>

**Z<sub>0</sub> is a single, real, document-wide reference impedance**, 50 &#8486; by default and
settable. &Gamma; = (Z &minus; Z<sub>0</sub>)/(Z + Z<sub>0</sub>), the grid is the ordinary fixed Smith
grid, and every overlay is renormalized to it on the way in. A generator-referenced normalization that
moved under your hands whenever you edited the table was considered and rejected on exactly that
ground.

## Constant Q and the swept band {#q}

**Constant Q** draws a pair of arcs on which |x|/r = Q, for the normalized impedance. Both branches are
true circles &mdash; centre (0, &#8723;1/Q), radius &radic;(1 + 1/Q&sup2;) &mdash; and they are drawn
only inside the unit disc, because that is the only part of them that is an impedance.

{{ui: smith-constant-q}}

They are the bandwidth drawn on the same picture as the match. A joint that sits **inside** the arcs is
a lower-Q, wider-band network; a joint **outside** them is narrower. On a two-element L match the
corner between the arcs is the one that sets the bandwidth, and its Q is exactly |X|/R there.

- **Drag either branch to set Q** &mdash; they are one setting, so both move. Q is read straight off
  the drag point as |x<sub>d</sub>|/r<sub>d</sub>.
- **Hold shift while dragging to round to the nearest 0.25.** The rounding is applied to Q before it is
  stored, so a shift-drag lands on an exact quarter and an unshifted drag afterwards starts from that
  exact quarter.
- A drag outside the passive region has no finite Q; it pins at the last valid value and the strip
  says so.

**The swept band** is a thin continuous locus through the load points, over a start, stop and point
count of your choosing. It is **off by default** &mdash; bandwidth is not the question this tool is
built around, and none of it is needed to match an impedance &mdash; and it costs one closed-form
evaluation per point.

A band that reaches past the generator table's span is **clamped, with the span named in the strip**,
not refused. A band is a viewing choice, where a design frequency is a design input, and the difference
is why the two are treated differently.

## Overlays and markers {#overlays}

**Overlays** put reference data under the work, from either of the two sources circuitRF already has:

- **a Touchstone file**, referenced by a path **relative to the document** &mdash; so it survives a
  moved or archived workspace;
- **a cube in an open data set**, referenced the way a Data Display trace card references one.

Each row picks its quantity through the same machinery a trace card uses: a raw S-parameter
(`S11`, `S21`, &hellip;), a virtual Z or Y, or a derived quantity &mdash; of which
**SourceStabilityCircle** and **LoadStabilityCircle** are drawn as circles in the &Gamma; plane.

| | |
|---|---|
| **Renormalize** | On by default, and on is the right answer: a 75 &#8486; part drawn on a 50 &#8486; chart without it is a curve in the wrong place that looks entirely plausible. |
| **Autoscale** | Off by default. A stability circle can be enormous, and one unlucky overlay would squash the cascade into a corner. |
| **Visible** | A hidden row is not on the chart at all, so it is also out of the trace list and out of the Add Marker menu. |
| **An overlay that does not resolve** | Is reported and does not stop the document opening. |

**Markers** are the Data Display's own markers, which means placement, drag, the info box, the context
menu and the editor all behave exactly as they do on a plot &mdash; including the constant-VSWR circle
about a marker. A marker remembers which curve it is a reading *on*, by that curve's label, so deleting
an element cannot silently move a reading onto the next one.

<div class="callout note">
<span class="label">A VSWR circle is not centred on its marker</span>
<p>Except when the marker is at &Gamma; = 0. The constant-VSWR locus about an arbitrary point is a
circle whose centre is somewhere else entirely; circuitRF draws the true one. It is worth stating
because the picture invites the opposite assumption.</p>
</div>

## Out to a schematic, and back {#clipboard}

### Copying the network out

Right-click the network strip &rsaquo; **Copy**. What goes on the clipboard is, all at once, the
schematic JSON, vector **SVG** and **PDF**, and a **PNG** &mdash; the same clipboard the schematic
editor's own Copy writes, so it pastes as real editable components into a `.csch`, and as a vector into
a presentation.

{{ui: smith-copied-schematic}}

What lands is a **complete, runnable two-port**, not a fragment with dangling ends:

- the generator end becomes a **`TermG` with `Num=1`**, carrying the generator impedance **at the
  design frequency**;
- the load end becomes a **`TermG` with `Num=2`**, carrying the chart's Z<sub>0</sub>;
- **no analysis card is copied.** A pasted selection is a fragment of a circuit, and the test bench it
  lands in owns its analyses &mdash; so add the sweep you want after pasting.

**A `Term` carries one impedance and the generator table may carry many.** When it does, the status
strip says so on copy and names the frequency that was used. It is stated rather than prevented,
because the copy is still the right circuit at the design frequency, which is what you wanted.

The copy follows the mirror. Someone who flipped the network to make a figure would not thank us for
un-flipping it on the way out; the circuit is electrically identical either way.

**Right-click the chart &rsaquo; Copy** does the same for the picture &mdash; PDF, SVG, the plot
configuration and a 2&times; bitmap &mdash; with the trajectories, grippers, targets, Q arcs, load
labels and markers all in it.

### Pasting a schematic in

Right-click the network strip &rsaquo; **Paste** takes a selection copied out of a schematic and
replaces the whole cascade with it, or refuses with a sentence naming what stopped it. **One paste is
one undo entry**, restoring the entire previous network.

A refusal matters more here than a success would: a reader that accepted *part* of a paste would
replace your network with something that is not what you copied, and report success.

<div class="callout note">
<span class="label">Which selections are compatible &mdash; the five rules, in order</span>
<ol>
<li>Every component is one of the element types above, a ground, or a <code>Term</code>/<code>Port</code>.</li>
<li>Every non-ground net has exactly two connections, except the two ends.</li>
<li>There are exactly two end nets and the walk between them is unique. <b>Any branch is a refusal
naming the net</b> &mdash; a cascade has no tee in it.</li>
<li>Every part hanging off the through path has its other pin on ground, and nothing else does.</li>
<li>No component carries a parameter this tool cannot represent &mdash; an expression, a swept
variable, a hierarchical reference. That is a refusal <b>naming the instance and the parameter</b>,
because silently dropping an expression would change the circuit.</li>
</ol>
</div>

**Which end is the generator** is decided by a `Term`/`Port` with the lowest `Num` if there is one, and
otherwise by geometry: the leftmost end when the strip is drawn generator-left, the rightmost when it
is mirrored. **The strip says which of the two rules fired**, because they can disagree and you are the
only one who knows which you meant.

The generator table itself is **not** touched by a paste. What is replaced is the cascade; the ports
that told the reader which end was which are then discarded. Overwriting a table you may have imported
from an `.s1p` with a single number, on the strength of a paste you made to change the network, would
be the wrong answer.

## Running it headless {#headless}

`circuitrf smith` evaluates a `.csmith` with no display: it prints what the status strip states, and
then the walk **one node at a time** &mdash; which is the part worth having on a build machine, because
it is what you diff between two revisions of a network. It writes the load reflection coefficient as a
`.s1p`, and the chart as SVG, PDF or PNG, drawn by the same code the window draws with.

See {{anchor: cli#smith|`smith` in the CLI chapter}} for the options, the refusals and the exit codes.

## What it will not do {#limits}

Stated plainly, because each of these is a thing somebody reasonably expects.

| | |
|---|---|
| **It will not compute a network for you.** | No synthesis, no optimiser, no goal, no error function. A network is judged by looking at it. That is the [Match Designer](match.html)'s job. |
| **It has no power, no dBm and no gain.** | The generator has an impedance and nothing else, and every quantity here is a linear immittance. A gain readout would be inventing a quantity the model does not have. |
| **It has no branches and no hierarchy.** | One cascade, ground on the shunt side. This is not a simplification waiting to be relaxed &mdash; it is what makes a per-element curve mean something. |
| **It has no layout and no physical length.** | A TLIN is an ideal line with an impedance and an electrical length. Microstrip and its family are a schematic's business. |
| **It is not run by the simulation engine.** | A `.csmith` is evaluated in closed form, which is why it keeps up with a drag. It is *checked* against the engine: every element type in both placements is compared against the ordinary S-parameter analysis of the equivalent netlist. |
| **There is no separate application.** | It is a document type. There is no standalone binary and none is planned. |
