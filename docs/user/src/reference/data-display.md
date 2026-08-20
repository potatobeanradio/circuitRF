---
title: The Data Display
slug: reference/data-display.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Data Display
lede: Plots and tables, trace cards, axes, markers — and how a run's cubes become curves.
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
— <code>.npy</code> results, Touchstone <code>.sNp</code>, measured load-pull — and it will happily hold
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
names, and the branch axis of `I` with your IProbe names.

| Cube | Axes, from single-point to fully swept | Kind |
|---|---|---|
| `V` | `[node]` → `[node, harmonic]` → `[sweep…, node, harmonic]` | Complex for HB, real for DC |
| `I` | `[branch]` → `[branch, harmonic]` → `[sweep…, branch, harmonic]` | Complex or real |
| `S` | `[freq, i, j]`, or `[sweep…, freq, i, j]` | Complex |
| a measurement | a scalar, or `[sweep…]` | Real or complex |

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
and branch rows.

**Spec** — the transform, and the shorthand text box (below).

**Style** — line, symbol, per-port Z0 for a network trace, and the number format on a Table.

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
  `dB20 dB10 dB mag phase real imag conj`.
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

Validity is checked as you type: exactly one X or none, at most one `~`, and a `~` needs an X. Anything
else is reported inline under the box rather than silently producing a different curve.

## Families of curves {#families}

A **family is one trace object that renders N curves** — not N traces. Mark an axis **Fam** (or type `~`
in its position) and every value of that axis draws its own curve, with one legend entry and one set of
style controls governing all of them.

```text
dB20 HB1.V[:, "Vout", ~]        every harmonic of Vout, versus the swept axis
SP1.S[~, :, 2, 1]               S21 versus frequency, one curve per sweep point
```

That distinction matters when you change your mind about the sweep: you edit one card, not twenty.

## Plotting against another quantity {#versus}

A spec may end with `vs <x-spec>`, which plots the trace against that quantity instead of against the
cube's own swept axis:

```text
Gain vs Pout
dB20(HB1.V[:, "Vout", 1]) vs Pout
```

This is how you get gain against output power rather than against drive, which is the form a PA designer
actually wants. On the card it is the **vs X** row. The X side inherits the Y side's swept axis and
family by axis name, so you rarely have to restate them, and the X side may even come from a different
loaded file.

## Axes and limits {#axes}

Each plot carries axis labels and limits of its own, edited from the Plot Inspector. A trace may be
assigned to a **secondary (right-hand) Y axis** on a rectangular plot, which is how gain in dB and
efficiency in per cent share one frame legibly. Autoscale is on until you set a limit; setting one turns
it off for that axis, so a figure you have framed deliberately does not re-frame itself on the next run.

## Markers {#markers}

Drop a marker on a trace to read its value. Markers are per-trace, they persist in the saved `.cdd`, and
on a load-pull contour they read the interpolated **surface** rather than the nearest grid point — which
is the point of fitting a surface at all. The maximum-power and maximum-efficiency locations are flagged
on a contour plot automatically.

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
four **plot types**, which add a plot of that kind; **add trace**; the **zoom and fit** controls; **undo
and redo**, which cover the whole display and not just the selected plot; and **save**, **open** and
**export**.

<p class="small">See also: <a href="plot-types.html">Plot types</a> ·
<a href="npy-export.html">Results &amp; data export</a> ·
<a href="measurements.html">Measurements</a> ·
<a href="simulations.html">Simulations</a>.</p>
