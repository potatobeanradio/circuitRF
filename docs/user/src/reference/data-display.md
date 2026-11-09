---
title: The Data Display
slug: reference/data-display.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Data Display
lede: Plots and tables, trace cards, axes, markers — and how a run's cubes become curves.
keywords: plot, graph, chart, curve, trace, marker, Smith chart, axes, legend, cdd, results viewer
---

The Data Display is where results are looked at. It is a **document**, like a schematic or a layout: it
opens as a tab in the workspace, tears off into its own window, and is saved to disk as a `.cdd` file
that reopens exactly as you left it.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#free-floating">It plots files, not runs</a></li>
<li><a href="#anatomy">Anatomy of a display</a></li>
<li><a href="#cubes-to-traces">From a DataSet to a curve</a></li>
<li><a href="#trace-card">The trace card</a></li>
<li><a href="#shorthand">The spec shorthand</a></li>
<li><a href="#families">Families of curves</a></li>
<li><a href="#versus">Plotting against another quantity</a></li>
<li><a href="#axes">Axes and limits</a></li>
<li><a href="#markers">Markers</a></li>
<li><a href="#contours">Load-pull contours</a></li>
<li><a href="#toolbar">The toolbar</a></li>
</ol>
</nav>

## It plots files, not runs {#free-floating}

<div class="callout note">
<span class="label">The one thing to understand first</span>
<p>A Data Display is <strong>not</strong> bound to one schematic or one run. It reads <strong>files</strong>
— <code>.npy</code> results, Touchstone <code>.sNp</code>, measured load-pull, binary simulation datasets
(<code>.ds</code>) — and it will happily hold
traces from several at once. That is what lets you overlay a measurement on a simulation, or last week's
baseline on today's, without either of them knowing about the other.</p>
</div>

Practical consequences:

- A run overwrites its results file, and every trace bound to that file refreshes. Nothing needs to be
  re-added.
- To keep a baseline, name a different results file for that run — see
  [Where results live](npy-export.html#where) — and point a trace at it.
- A trace remembers the file it came from, so a saved `.cdd` reopens against the same data.
- A new Data Display starts **empty**. Nothing is auto-plotted; you author it.

A **binary simulation dataset** (`.ds`) loads and plots like a `.npy`: one group per block, named after
its analysis (`SP1`), each variable a cube on the block's sweep axes, and an `S[1,1]` … `S[n,n]` family
one `S` cube with its port axes last — a swept S cube is `[Vds, Vgs, freq, i, j]`, so the sweep axes are
pinned on the trace card exactly as for a parametric sweep of your own. A `PortZ` that is the same at
every point becomes the group's `Z0`, so `Z` and `Y` are offered. Anything the load could not do as
plainly — text variables, a `PortZ` that varies — is said on the source's row in Dataset Aliases.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf read sweep.ds
<span class="prompt">$ </span>circuitrf plot sweep.ds -o s21.svg --trace "cube=SP1.S[0,0,:,2,1],y=db20"</code></pre>

## Anatomy of a display {#anatomy}

{{ui: data-display}}

A display holds one or more **tabs**; each tab is a canvas holding **plots**; each plot holds
**traces**; a trace may carry **markers**. Plots are moved and resized on the canvas directly. The
**Plot Inspector** — one panel per selected plot — is where the plot's type, its traces and its axes are
edited.

There are four plot types, and which one will accept which data is the single most important rule in the
Data Display. It has its own page: [Plot Types](plot-types.html).

## From a DataSet to a curve {#cubes-to-traces}

A run produces one **DataSet**: an ordered map of **groups** to named **DataCubes**.

- One group per analysis — `HB1`, `DC1`, `SP1` — plus a `measurements` group, plus a default group
  (`""`) that a flat Touchstone file lands in.
- Cubes are addressed `Analysis.Cube`: `HB1.V`, `SP1.S`, `measurements.PDC`. A **bare** name resolves in
  the default group first, then in `measurements`, so you can write `PDC` and mean `measurements.PDC`.
  An analysis cube must stay qualified — a bare `V` would find the wrong group.

A **DataCube** is an N-dimensional array, real or complex throughout, with **named axes**. Each axis has
numeric values, a unit, and sometimes string labels — the node axis of `V` is labelled with your net
names (and with the name of any [VProbe](components.html#vprobe) you placed, which adds a row for the
net it taps), and the branch axis of `I` with your IProbe names.

| Cube | Axes, from single-point to fully swept | Kind |
|---|---|---|
| `V` | `[node]` → `[node, harmonic]` → `[sweep…, node, harmonic]` | Complex for HB, real for DC |
| `I` | `[branch]` → `[branch, harmonic]` → `[sweep…, branch, harmonic]` | Complex or real |
| `S` | `[freq, i, j]`, or `[sweep…, freq, i, j]` | Complex |
| a measurement | a scalar, or `[sweep…]`; a [port range](measurements.html#port-ranges) keeps `S`'s `[freq, i, j]` | Real or complex |

**A trace is a one-dimensional slice through a cube.** Authoring a trace means saying, for each axis of
the cube, what to do with it: use it as the X axis, iterate it as a family of curves, or pin it to one
value. That is the whole model, and the trace card is its interface.

## The trace card {#trace-card}

{{ui: plot-inspector-trace-card}}

Reading top to bottom:

**Identity — *what* to plot.** A **group** selector (an analysis, or Measurements) and then an **item**
selector. For an analysis group the item selector is a compact **V / I** pair, because voltage and
current are deliberately symmetric: one mental model, one set of controls. Both are always offered; if
you pick one whose cube is not in the result you get an explicit empty state rather than a blank card.
For S-parameters, an item is a matrix element.

**Axis roles — *how* to slice it.** One row per axis of the cube, each offering three roles:

| Role | What it does |
|---|---|
| **X** | Use this axis along the bottom of the plot. Exactly one axis may be X — or none, which makes the trace a scalar. |
| **Fam** | Iterate this axis as a **family** of curves. At most one, and it needs an X. |
| **Fix** | Pin this axis to a single value, chosen in the selector beside it. |

The node/branch label axis defaults to **Fix** — it is a selector, never the X axis. The default X
prefers `freq` when the cube has one, then falls back to the first non-label axis. A cube with no
non-label axis at all — a DC operating point with no sweep — has no X and resolves to a scalar, which is
what a [Table](plot-types.html#table) is for.

The **eye** on the label-axis row reveals the entries that are normally hidden: every node beyond the
ones you named, and every device-port branch beyond your IProbes. It is one control shared by the node
and branch rows. A net label and a VProbe both count as naming a node, so both are visible without it.

**Spec** — the transform, and the shorthand text box (below).

**Label** — the trace's own name. Typed here, it replaces the generated name everywhere the trace is named:
the legend, the Y-axis title, the marker box and its multi-marker rows, a table's column header and the
column of copied or exported table data. It is the whole name, with no source and no quantity added to it.
Empty, the box shows the generated name it would replace, and that name is what is drawn. Enter or
clicking away commits it, as one undo step.

**Style** — line, symbol, per-port Z0 for a network trace, and the number format on a Table.

**Derived metrics.** Beneath the matrix elements the item selector also offers quantities *computed*
from the S-matrix rather than read out of it — stability factors, maximum gain, passivity, group delay,
and the ESR / C / L / Q readouts a passive part is specified in. Selecting one reveals its own controls:
the ordered port pair, and for a passive readout the **Fixture** that decides how the two-terminal
impedance is recovered. Every equation is written out in
[Derived Metrics](derived-metrics.html).

### Against a harmonic-balance result

{{ui: plot-inspector-hb}}

The same card, pointed at an HB drive sweep. The extra axes are what change: `V` now carries a
`harmonic` axis and a swept-power axis alongside the node axis, so there is a real choice to make about
which is X, which is the family, and which is pinned.

<div class="callout note">
<span class="label">Simulated S-parameters and a Touchstone file take different paths</span>
<p>They look the same on the plot and they are not the same underneath. A <strong>Touchstone</strong> file
lands in the default group and is handled by the network path — group "S-Parameters", matrix-element
items, per-port Z0 available. An <strong>S-parameter run result</strong> lands in a named group
(<code>SP1</code>) and is a first-class cube: <code>freq</code> defaults to X, <code>i</code> and
<code>j</code> become 1-based port selectors, and <code>dB20</code> is the default transform on a
rectangular plot. The <code>Z0</code> cube beside it is a per-port reference impedance, not a signal, and
is never offered as one.</p>
</div>

## The spec shorthand {#shorthand}

The text box on the spec row is a **two-way view** of the binding: every control on the card writes it,
and editing it rewrites every control. The grammar is:

```text
[transform] CubeName[ token, token, … ]
```

- **transform** — optional, either prefixed (`dB20 V[…]`) or as a function (`mag(V[…])`). One of
  `dB20 dB10 dB mag phase unwrap real imag conj`. `unwrap` is the card's **Unwrap°**: the phase in
  degrees, continued across ±180° instead of jumping by 360°, so the slope a delay or an electrical length is read
  from stays visible. It is offered for simulation results; a Touchstone file's trace offers it greyed.
- **CubeName** — qualified (`HB1.V`) or bare (`PDC`).
- **token** — one per axis, in the cube's own axis order:

| Token | Role |
|---|---|
| `:` | the whole axis, as X |
| `a:b` | a narrowed range, as X |
| `~` | this axis is the family |
| `"Vout"` | fix to a labelled entry — a net or probe name |
| `3` | fix to index 3 |

A bare `CubeName` with no brackets means *the whole cube* — every axis `:`. A spec with no `:` and no
`~` at all is a **scalar**, valid on a Table: `DC1.V["Vout"]`.

<div class="callout warn">
<span class="label">Port numbers are 1-based; every other index is 0-based</span>
<p>On an S/Y/Z cube's <code>i</code> and <code>j</code> axes a bare integer is a <strong>port
number</strong>, so <code>SP1.S[:, 2, 1]</code> is S21 and <code>SP1.S[:, 1, 1]</code> is S11 — the way an
RF engineer names them. Every other axis (<code>freq</code>, a sweep, <code>harmonic</code>) uses a
0-based index, and labelled axes use quoted names. A port outside 1..N is reported, not clamped.</p>
</div>

A port matrix can also be read the way a measure line reads it: `SP1.S(2,1)` is `SP1.S[:, 2, 1]`, and a
measurement holding one, such as `DUT_2 = SP1.S(3:4, 3:4)`
([port ranges](measurements.html#port-ranges)), is `DUT_2(2,1)`. The card shows it with its two port rows
pinned, as if you had picked them.

Validity is checked as you type: exactly one X or none, at most one `~`, and a `~` needs an X. Anything
else is reported inline under the box rather than silently producing a different curve.

The box also accepts a free **expression** across cubes — `mag(HB1.V) - mag(HB1.Vref)`,
`dB20(SP1.S[:, 2, 1])` — evaluated element-wise. Inside one, **`freq` is the sample's own frequency in
Hz**, so `imag(SP1.Z[:, 1, 1]) / (2*pi*freq)` plots an inductance against frequency. `freq` is bound
only when the X axis is a frequency; on a Pin- or Γ-swept trace the error names the axis the trace
actually has.

## Families of curves {#families}

A **family is one trace object that renders N curves** — not N traces. Mark an axis **Fam** (or type `~`
in its position) and every value of that axis draws its own curve, with one legend entry and one set of
style controls governing all of them.

```text
dB20 HB1.V[:, "Vout", ~]        every harmonic of Vout, versus the swept axis
SP1.S[~, :, 2, 1]               S21 versus frequency, one curve per sweep point
```

That distinction matters when you change your mind about the sweep: you edit one card, not twenty.

A trace on a result with two or more sweeps opens with the **innermost** sweep as X and the next one
out as the family, every other axis pinned at its first value. A cube with a frequency axis keeps
frequency as X. When a re-run adds a sweep, the card keeps the roles you had and the new axis becomes
the family if the card has none, so a drive sweep wrapped in a frequency sweep draws one curve per
frequency without a click. A bare name typed in the spec box on three or more axes is read the same
way.

## Plotting against another quantity {#versus}

A spec may end with `vs <x-spec>`, which plots the trace against that quantity instead of against the
cube's own swept axis:

```text
Gain vs Pout
dB20(HB1.V[:, "Vout", 1]) vs Pout
Gain[~, :] vs Pout              one curve per RFfreq, each against its own Pout
```

This is how you get gain against output power rather than against drive, which is the form a PA designer
actually wants. On the card it is the **vs X** row. The X side inherits the Y side's swept axis and
family by axis name, so you rarely have to restate them, and the X side may even come from a different
loaded file.

**A scatter of samples** is a versus trace drawn with markers and no line (the trace card's line and
marker switches). An envelope run's memory cloud is the example: every sample's gain against its own
instantaneous input power, at the output IProbe,

```text
ENV1.Gt_t_dB[:, "Iout"] vs ENV1.Pin_inst_t_dBm
```

which spreads where the amplifier has memory and lies on one curve where it has none (see
[AM-AM, AM-PM and memory](simulations.html#envelope-amam)). Under a sweep of the drive, the trace becomes
a family by itself and draws one cloud per drive level. A trace of more than 5,000 circle markers draws
them as plain dots without an outline, which keeps a family of 20,000-sample clouds quick to redraw.

## Axes and limits {#axes}

Each plot carries axis labels and limits of its own, edited from the Plot Inspector. A trace may be
assigned to a **secondary (right-hand) Y axis** on a rectangular plot, which is how gain in dB and
efficiency in per cent share one frame legibly. Autoscale is on until you set a limit; setting one turns
it off for that axis, so a figure you have framed deliberately does not re-frame itself on the next run.

**Equal X and Y scale (1:1)**, in a rectangular plot's **Axes Limits**, draws one unit as long along X as
along Y. The plot box narrows to keep it and the limits stay as they are. A constellation needs it, so
that a circle of error looks like a circle. It is off by default, applies to linear axes only, and is
saved in the `.cdd`.

The three **legend** buttons to the right of **Freq** in the Plot Inspector draw a box naming each trace
beside a short sample of its line, in its colour and line style. **On** puts it in the top-right corner
of the plot; **Below** puts it in a band under the plotting region. It is **Off** to begin with, on every plot type: a rectangular plot already names
its traces on the Y axis, and a Smith or polar plot on the label strips beside the chart and the rows
under it. The setting is saved in the `.cdd`, and `circuitrf render` and `circuitrf plot` draw it too.
Right-click an entry and choose **Rename…** to open its trace card at the [Label](#trace-card) box.

**Drag a legend to put it anywhere on the display** — off the curves, or out of the plot altogether. It
lifts out as a box that moves like a marker info box: click to select it, Ctrl/Cmd-click to add it to a
selection, and it moves with the plots selected with it. **On** stays lit wherever you put it; **Below** puts it
back in the plot.
Select a legend and press **Delete** to turn it off for that plot. Each of these is one undo step, and
where you left it is saved in the `.cdd`.

## The decibel floor {#db-floor}

**A decibel trace bottoms out at −250 dB.** Below that the curve is drawn flat at −250, and a readout
says so with the inequality rather than a bare number — a marker info box reads `dB(S(1,1)) ≤ -250.00`,
a table cell `≤ -250.00`.

This matters more often than it sounds, because a quantity that is *analytically zero* does not come
back as zero. An ideal 50 Ω line terminated in 50 Ω has S(1,1) ≡ 0, and what the solver returns is
double-precision roundoff: magnitudes scattered over a few orders of magnitude with the occasional exact
zero mixed in. Converted to dB with no floor, that is a trace flickering across a 59 dB band with spikes
to −∞ — and autoscale then sizes the axis to the flicker, so anything real sharing the plot is squashed
into a line. The floor turns the noise into a flat line at the bottom of the frame and lets the rest of
the plot keep its scale.

−250 dB was chosen to sit below anything a result can physically mean and above anything roundoff can
produce: thermal noise power in a 1 Hz bandwidth is −204 dB(W), while roundoff on a quantity of order
one reaches −285 dB. `dB`, `dB10` and `dB20` all share the one floor.

**The floor is applied to the picture only.** A measurement expression, and any value you export to
`.npy`, `.mat` or Touchstone, carries the unclamped number — so `dB(S(1,1))` written as a measurement
can report a value below −250 dB even while the plot of the same quantity shows the floor. A number's
value never depends on whether it happened to be drawn.

## Markers {#markers}

Drop a marker on a trace to read its value. Markers are per-trace, they persist in the saved `.cdd`, and
on a load-pull contour they read the interpolated **surface** rather than the nearest grid point — which
is the point of fitting a surface at all. The maximum-power and maximum-efficiency locations are flagged
on a contour plot automatically.

**The arrow keys step a selected marker** from one sample to the next — Right or Up to the next higher
x, Left or Down to the next lower — on a rectangular, Smith or polar plot. On a Smith or polar plot the
marker walks along the curve in frequency order. A marker you have just added from the plot's **Add
Marker** menu is already selected, so the arrows move it straight away.

**To read every trace at one x**, turn on **Multi** in the marker's properties. The marker becomes a
vertical line across the plot, and its info box lists each trace's value at that x.

## Monte Carlo statistics {#statistics}

A Monte Carlo or yield run writes `<design>.yield.npy` beside the design, with every trial on a `trial`
axis. Open it as a data source and the pictures a yield analysis is read from are a menu away.

**The Statistics button** on a trace card (the bar-chart icon under the trash can, on a rectangular plot)
rewrites the trace:

| Entry | Draws |
|---|---|
| **Histogram** | how many trials fall in each bin, as bars; the bin count starts at the Freedman–Diaconis choice |
| **Histogram (Percent)** | the same, as a percent of the trials |
| **CDF** | the fraction of trials at or below each value, as a step |
| **Normal Quantile** | each value against the normal quantile of its rank — a straight line when the spread is Gaussian, a bend where a tail is not |
| **Yield Sensitivity vs** ▸ a variable | the yield in each bin of that variable, as bars, with the trials per bin on the right axis; a nominal sits best where the bars are tallest |
| **Normal Fit** | a normal curve with the data's mean and σ over a histogram |
| **Back to Curves** | the trace exactly as it was |

Each entry writes an ordinary expression — `histogram(trials.goal:Gain:worst, 23)` — into the trace's spec
box, where you can see it and edit it (change the bin count, add a range). The statistics are taken over
the `trial` axis; a trace without one offers a submenu per axis. The same pictures are available
headlessly: `circuitrf plot … --trace cube=…,stat=histogram`.

**Spec lines.** The run records the goals it scored, and a plot of a goal's value draws that goal's limits
as dashed lines: on a histogram, CDF or normal plot of `goal:<g>:worst`, a vertical line at each limit (a
sloped limit at its tighter end); on the curves of every trial over the goal's swept axis, the limit
across the goal's range, sloped where it slopes. **Spec lines** in the Plot Inspector turns them off.

**The statistics table.** The **Σ** toolbar button adds a table of the selected result: a row for each
goal's worst value and each scalar measure, with mean, σ, min, max, median, the 1st and 99th percentile,
skew, kurtosis, Cpk and the distance to the limit in σ, and each goal's yield with its confidence
interval.

**Noise and gain circles.** The bullseye toolbar button adds one Smith chart of the selected two-port noise result's
source plane: Sopt, NF and available-gain circles and the source stability circle, its unstable side shaded. Each circle
is an expression trace drawn at the frequency its **Freq** picker names. See
[The Noise and gain circles chart](derived-metrics.html#lna-circles).

**Signal quality.** The scatter-plot toolbar button adds the plots of the selected envelope result's signal quality:
the constellation (received symbols over the ideal ones, on one scale) for LTE, NR and DBPSK, or the frequency at each
bit for FSK; a table of every measure the result holds per carrier (EVM, MER, the FSK error, deviation and offset, the
de-chirped CSS error, with the distortion and noise split and the SNR when the run had `EvmSplit=yes`); and the output
spectrum, all at the probe carrying the signal. **Open Signal Quality** on a result file's menu opens them in a new Data Display. See
[Signal quality](simulations.html#envelope-evm).

**Draw style.** Any trace on a rectangular plot can be drawn as a line, as bars or as a step; bars stand on
zero and a family of them stands side by side within each bin.

## Monte Carlo trials {#trials}

A trace over every trial — a family with one curve per trial — shows *which* trials fail. The
**Statistics** button adds:

| Entry | Does |
|---|---|
| **Colour By** ▸ **Pass / Fail** | draws passing trials faded in grey and failing ones in red after them, so a failure is never hidden under the passes; a trial that did not evaluate is not drawn. The label counts them: `S21 — 471 pass · 29 fail`. A goal's own pass/fail is offered too |
| **Envelope** ▸ min–max, P1–P99, P5–P95, mean ± 1σ, mean ± 3σ | a shaded band across the trials at each X, with the median (or mean) as a line |
| **Show Curves** | with an envelope on, turn the individual trials off and keep the band — the readable view of thousands of trials |
| **Show Nominal** | the nominal design's curve, drawn over the trials in the trace's full colour (on by default) |
| **Scatter vs** ▸ a variable or measure | plots this per-trial value against another — `goal:S21:worst` against `R1.R`, say — as points coloured by pass/fail |
| **Fit Line** | on a scatter, the least-squares line, with its R² in the label |
| **Contributions** ▸ a goal | adds a Pareto chart of which statistical variables drive that goal: each one's share as a bar between 0 and 100 %, largest first, and the running total on the right axis. Computed once and kept in the result |

An envelope is offered on rectangular plots only: on a Smith or Polar plot every point is a complex value,
and a band between two of them at each frequency is not a region.

**Selecting a trial.** Click a trial's curve, a scatter point or a histogram bar (a bar selects every
trial in it). The same trial lights up in every plot of that result, in every Data Display, and the rest
dim; the toolbar shows `Trial 417` or `23 trials`. **Esc** clears it. With one trial selected, the plot's
right-click menu has a **Trial** submenu:

- **Send Trial to Tuning** — loads that trial's values into the Tuning sliders of the schematic, so you can
  tune from where it failed.
- **Re-run Trial** — runs that trial again in full and draws its result as a faded ghost on every plot of
  the result.
- **Copy Values** — the trial's values as text.
- **Save as Corner…** — adds a statistical corner that replays this trial, to tune and optimize against.

The same pictures are available headlessly: `circuitrf plot … --trace cube=SP1.S,i=2,j=1,y=db20,colorby=pass,envelope=p:1`.

### The yield and DOE displays {#yield-displays}

The [Yield](yield.html#display) panel's **Open yield display** writes a display beside the result,
`<design>.yield.cdd`, made of the plots above: for each spec, its trials coloured by pass and fail
against the spec's limits, and a histogram of its worst value; the yield against the value that drives
the first spec most; and the statistics table. A design of experiments' result opens as
`<design>.doe.cdd` instead: for each spec and measurement, its effects as bars largest first with the
noise level as a line, its value at each factor's low, centre and high, and its strongest interaction.
Both are ordinary displays — change them as you would any other. While a run is going, every display
holding its result follows it, with a **Yield** chip.

A Monte Carlo run at every corner keeps one set of trials per corner. Its yield display,
`<design>.yield.corners.cdd`, has one tab per corner, named after it, holding that corner's plots, and it
opens on the corner with the lowest yield. Every trace on a tab is pinned to its corner, so pick another
corner on a trace card to compare two on one plot. Headlessly, a histogram of such a result is one
histogram per corner, or one corner with `corner=<name>` on the trace.

## Load-pull contours {#contours}

{{ui: plot-loadpull-contours}}

A load-pull sweeps the termination Γ presented to the device over a grid, and at **each** grid point
drives the device up in power to a target gain compression. The raw result is therefore two-axis —
`{grid point, drive step}` — and every figure of merit is a value over that field. Contours are how you
look at it.

The form a contour takes is always the same: **a metric, at a constant value of a different metric.**
Pout at constant 3 dB compression. Efficiency at constant Pout. Gain at constant back-off.

{{ui: plot-inspector-loadpull}}

The contour card asks for exactly that:

- **Metric** — what is being contoured (Pout, PAE, gain, …).
- **Constraint** — the *other* metric held constant, and its value. Compression is the common case and
  has its own control.
- **Levels** — either a start/step/stop range or a count, plus whether to draw iso-lines, fills or
  labels.
- **Substrate** — the Γ plane (a Smith chart) or the Z plane (a rectangular plot).
- **Frequency** — pinned, for a frequency-swept load-pull.
- **Interpolation** — the radial-basis kernel, its smoothing and its shape parameter.

Reading the figure: each closed curve is a locus of terminations that deliver the same metric value, and
the labels give that value. The **P** and **E** markers are the maximum-power and maximum-efficiency
optima; the distance between them is the trade-off the design has to spend. The grid points themselves
can be shown, and it is worth doing at least once: **a contour is an interpolated surface, and it can
only be trusted where the grid actually surrounds it.** Contours that run off the edge of the
constellation are extrapolation.

<p class="small">Background on the method and the surface model:
<a href="simulations.html#loadpull">Loadpull / Sourcepull</a> and
<a href="simulations.html#loadpull-pursuit">Loadpull Pursuit</a>.</p>

## The toolbar {#toolbar}

{{toolbar: datadisplay}}

The groups, left to right: the **source picker**, which chooses the data source new traces bind to; the
four **plot types**, which add a plot of that kind; the **statistics table** of a Monte Carlo result;
**add trace**; the **zoom and fit** controls; **undo
and redo**, which cover the whole display and not just the selected plot; and **save**, **open** and
**export**.

<p class="small">See also: <a href="plot-types.html">Plot types</a> ·
<a href="npy-export.html">Results &amp; data export</a> ·
<a href="measurements.html">Measurements</a> ·
<a href="simulations.html">Simulations</a>.</p>
