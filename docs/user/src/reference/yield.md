---
title: Yield
slug: reference/yield.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Yield
lede: Give values a tolerance, mark which goals are specs, and find out how many builds of the design meet them — and which ones do not.
keywords: yield, yield analysis, monte carlo analysis, montecarlo, monte-carlo, design centering, centering, surrogate, monte carlo, tolerance, tolerances, distribution, gaussian, uniform, lognormal, discrete, truncation, spread, correlation, specs, trials, seed, sampling, latin hypercube, sobol, auto-stop, confidence, interval, corners, statistical corner, contributions, sensitivity, histogram, kit statistics, process, mismatch, cdf, normal quantile, envelope, scatter, design of experiments, doe, optimizing across corners, evaluate at
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What the Yield panel does</a></li>
<li><a href="#modes">The five modes</a></li>
<li><a href="#tolerances">Tolerances</a></li>
<li><a href="#spread">Writing a spread</a></li>
<li><a href="#correlations">Correlations</a></li>
<li><a href="#kit">Kit statistics</a></li>
<li><a href="#specs">Specs</a></li>
<li><a href="#settings">Run settings</a></li>
<li><a href="#running">Running, pausing and reading the yield</a></li>
<li><a href="#trials">The trial table</a></li>
<li><a href="#display">The yield display</a></li>
<li><a href="#plots">Which plot answers which question</a></li>
<li><a href="#corners">Corners</a></li>
<li><a href="#optimizing-corners">Optimizing and tuning across corners</a></li>
<li><a href="#centering">Centering</a></li>
<li><a href="#doe">Design of experiments</a></li>
<li><a href="#example">The example</a></li>
<li><a href="#headless">From the command line, and from an agent</a></li>
</ol>
</nav>

## What the Yield panel does {#what}

The **Yield** panel (View ▸ Panels ▸ Yield, tabbed behind the [Optimizer](optimization.html)) runs the
focused schematic many times — once per **trial** — with each toleranced value drawn afresh from its
distribution, and counts how many trials meet the specs. It follows the focused schematic the same way
[Tuning](tuning.html) and the Optimizer do.

To find which values are worth a tolerance before counting anything, start with the
[Sensitivity](sensitivity.html) panel: it ranks what moves each spec in a handful of simulations, and
its **Run Yield on these** brings you here.

Everything you set here — tolerances, correlations, which goals are specs, the run settings, the
corners — is saved with the schematic, marks it changed and can be undone, one step per change. A run
and its trial table are not: they belong to the session. A run never changes the schematic's values.

## The five modes {#modes}

The five buttons at the top choose what **▶** does:

| Mode | What a run reports |
|---|---|
| **Monte Carlo** | The spread of every measurement and goal value over the trials. No pass or fail. |
| **Yield** | The fraction of trials meeting every spec, with its confidence interval, overall and per spec. |
| **Corners** | Every enabled corner evaluated once — or, with **MC at each corner**, a Monte Carlo at each. |
| **Centering** | The designable values moved to where the most trials meet the specs, then checked on fresh trials. |
| **DOE** | A design of experiments: which values move each spec and measurement, and how they interact. |

**Monte Carlo or Yield?** A Monte Carlo answers *how much does it vary* — the spread of every
measurement, whether or not anything is a spec. A Yield run answers *how many pass*, and needs at least
one goal used as a spec. Both draw the same trials from the same seed, so a Yield run's spread is the
Monte Carlo's.

## Tolerances {#tolerances}

A tolerance lives on **the same entry** as a value's tuning range and its optimizer flag: one row per
value, in all three panels. A tolerance can be added three ways, and all three do the same thing:

- **＋** in the panel's Variables header, through the same search Tuning uses;
- the **bell-curve toggle** beside a value in the Properties Inspector;
- right-click a value on the canvas ▸ **Tolerance…**.

A new tolerance is a Gaussian at ± 5 % for 3σ (a uniform ± 5 % on a whole-number value), with tuning
and optimizing left off. Each row shows:

| Column | |
|---|---|
| **Check** | Draw this value in every trial. Unticked, the tolerance is kept but the value stays at its nominal. |
| **Distribution** | Gaussian, Uniform, Lognormal or Discrete. A whole-number value takes Uniform or Discrete only. |
| **Spread** | How wide — see [Writing a spread](#spread). |
| **Nominal** | The schematic's value, in its unit. |
| **Share** | After **Contributions**: this value's share of the spread of the selected spec. |

**⋮ ▸ Remove tolerance** deletes the distribution and any correlation naming the value.

An edit the design cannot run is refused on the spot, with the same sentence
`circuitrf check` prints for it — a lognormal on a value that is not positive, a Gaussian on a whole
number, tolerances on both a rectangular and a polar part of one complex value. A complex value takes
tolerances on its real and imaginary parts, or on its magnitude and phase.

## Writing a spread {#spread}

Type into the Spread box and press Enter:

| You type | Means |
|---|---|
| `± 2 %` | Within 2 % of the nominal. On a Gaussian, at 3σ unless you say otherwise. |
| `± 2 % at 2σ` | A Gaussian whose ± 2 % is two standard deviations. |
| `σ 1 Ω` | A Gaussian or lognormal with a standard deviation of 1 Ω. |
| `45 … 55 Ω` | Uniform between 45 Ω and 55 Ω (`...` works too). |
| `4 … 8 by 2` | Discrete: 4, 6 or 8, equally likely. |
| `σ 2 %, trunc 3σ` | A Gaussian cut off at ± 3σ — drawn from the truncated distribution, never clipped. |

A percentage follows the nominal when the nominal moves; an absolute value does not. An absolute value
must be in the value's own kind of unit — a capacitance in pF, nF or F, say; `± 3 pF` on a resistor is
refused. A range needs **Uniform** or **Discrete**, a σ needs **Gaussian** or **Lognormal**, and a step
needs **Discrete**: choose the distribution first. `sigma` may be written for σ.

## Correlations {#correlations}

**Correlations…** (the link button) opens a grid of every toleranced value. Type a correlation
coefficient ρ, between −1 and 1, above the diagonal; the cell below mirrors it. A set of correlations
that cannot all hold at once is shown with the nearest set that can — which is what a run uses.

## Kit statistics {#kit}

A kit can carry its own spread: model parameters written as distributions, which every ordinary
simulation reads at their nominal and a trial draws. Most kits keep them in a **statistical section** of
their corner library — chosen in the Analyses panel's corner picker like any other corner (a `stat`
section beside `tt` and `ss`, say). With it chosen, a Monte Carlo draws the kit's statistics as well
as your own tolerances.

When the design's kits carry statistics, a **Kit statistics** row appears: **Process** (one draw per
trial, shared by every device) and **Mismatch** (a draw per device per trial) can be switched off
independently — to see how much of the spread each one causes. When a kit offers a statistical section
that is not selected, **Choose a corner…** opens the corner picker in the Analyses panel. How a kit
writes its statistics is in [PDK authoring](pdk-authoring.html#statistics).

## Library parts {#library-parts}

A [library part](model-library-format.html#tolerances) can carry its own spread: its model's values written as
distributions. A **Library parts** group lists each placed part whose model has one, with every spread shown in
the panel's own notation (`C0  ± 5 % at 3σ`). Each part draws once per placed instance. Untick a part to hold its
own tolerances at nominal while every other spread keeps drawing; the statistics line records the parts held as
`nominalparts=TSTCAP,TSTRES`. A part's draws are mismatch draws, so the Kit statistics' **Mismatch** switch holds
them too.

A tolerance you add to a part's value in the Variables list (`tol=part`) is separate, and works as before.

## Specs {#specs}

A spec is a [goal](optimization.html#goals). Every goal is listed with its **Use**: two ticks, the same ones
the Optimizer's goal row shows.

| Ticked | Reads | |
|---|---|---|
| **Opt** only | Opt | The optimizer meets it; the yield and the Sensitivity panel ignore it. |
| **Sensitivity / Yield** only | Sens/Yield | The yield counts it and the [Sensitivity](sensitivity.html) panel ranks it; the optimizer ignores it. |
| Both | All | Every one of them — the default. |

The last tick cannot be cleared: a goal nothing uses is one with **Enabled** off. A common way to work: centre
the design with the optimizer against tight goals, then loosen copies of them, ticked **Sensitivity / Yield**
only, for the yield analysis. Goals are written in the Optimizer; the pencil
(**Edit goals…**) takes you there with the selected goal. After a run each spec shows its own yield
and interval.

## Run settings {#settings}

Collapsed to one line (`500 trials · seed 1 · random · target 95 %`); the chevron opens them.

| Setting | |
|---|---|
| **Trials** | How many. With auto-stop, the most it will run. Default 100. |
| **Seed** | The same seed draws the same trials, on any machine. The dice picks a new one. |
| **Sampling** | `random`; `lhs` (Latin hypercube — needs the trial count up front, so not with auto-stop); `sobol` (low-discrepancy). |
| **Target %** | The yield the design should reach. |
| **Confidence %** | Of the interval around the yield. Default 95. |
| **Auto-stop** | Stop once the interval lies wholly above or below the target — after 50 trials at least. |
| **Did not evaluate** | A trial that did not converge: **Fail** counts it as a fail; **Warn** leaves it out of the yield. Either way it is listed. |
| **Save** | Which trials keep their full sweeps: `auto`, `scalars`, `all`, or the first N. |
| **Parallel** | How many trials run at once. |

## Running, pausing and reading the yield {#running}

**▶** starts the run; **⏸** finishes the trials in flight and holds, and pressing it again carries on
exactly as if it had never paused; **■** stops and keeps every trial already finished. The header
shows trials done, elapsed time and an estimate of the time left. In Yield mode the readout shows the
yield, its interval as a bar with the target marked on it, and how many trials did not evaluate. Every
open Data Display holding the result follows the run live, with a **Yield** chip.

**Reading the yield.** The yield is the fraction of trials that met every spec, and the interval
around it is where the true yield lies, at the chosen confidence, given only that many trials: 86.4 %
[83.1 %, 89.3 %] from 500 trials. The interval narrows with the square root of the trial count — four
times the trials for half the width. The target is met when the yield is at or above it; when the
interval still straddles the target, the trial count cannot tell the two apart, and more trials, or
**Auto-stop**, will. With Auto-stop on, the run ends as soon as the interval lies wholly on one side of
the target, so a design far from the target stops early and one close to it runs to the trial limit.

**Trials that did not evaluate.** A trial whose simulation did not converge has no value to judge. By
default it counts as a fail, because a part that does not simulate is rarely a part that works; with
**Did not evaluate: Warn** it is left out of the count instead. Either way the count is shown beside the
yield and every such trial is listed in the table with the reason.

The result is written beside the schematic as `<name>.yield.npy` — the same file
`circuitrf yield` writes for the same schematic and seed, byte for byte.

## The trial table {#trials}

During and after a run, one row per trial: its number, pass or fail, the spec it is tightest on and
its margin there, or why it did not evaluate. Sort by trial or by margin (tightest first); the funnel
shows fails only. Selecting a row highlights that trial in every plot of the result, and picking a
trial on a plot selects its row. Right-click a row for:

- **Send trial to Tuning** — the trial's values onto the Tuning sliders, to see why it failed;
- **Re-run trial** — simulate it again in full and show it on every plot of the result;
- **Copy values**;
- **Save as corner…** — a statistical corner that replays this trial's draws around the current design.

**Contributions** (the bar-chart button) ranks, once, which toleranced values drive the selected spec,
and fills each variable's **Share** bar. With correlated tolerances a fitted share can come out above
100 % or below 0; the bar shows it between 0 and 100 %.

## The yield display {#display}

**Open yield display** (the chart button, also offered once at the end of the first run) creates a
Data Display beside the result, `<name>.yield.cdd`, or brings it forward if it exists. It holds, for
each spec, every trial's curve coloured pass and fail with the nominal and the spec's limits; a
histogram of each spec's worst value with its limits; a yield sensitivity over the value that drives
the first spec most; and the statistics table. Every plot is an ordinary one: change it as you would
any other — see [the Data Display](data-display.html).

After **Corners** with **MC at each corner**, the display is `<name>.yield.corners.cdd`, with one tab per
corner holding these plots for that corner's trials, and it opens on the corner with the lowest yield.
A corner run without Monte Carlo has no trials, so the button is disabled and its tooltip says so.

The figures below are the shipped example's bandpass filter ([The example](#example)), 500 trials at its
saved seed. Each spec's trials come first, every trial's curve drawn grey when it passes and red when it
fails, the failures on top so none is hidden, with the nominal design and the spec's limit over them:

{{ui: yield-family}}

Then each spec's worst value as a histogram, with the limit as a dashed line. Read it for how far the
bulk of the trials sits from the limit, and on which side the tail lies:

{{ui: yield-histograms}}

And the statistics table — mean, σ, the extremes, percentiles and Cpk of each spec's worst value:

{{ui: yield-statistics-table}}

## Which plot answers which question {#plots}

Every plot below is on a trace card's **Statistics** button, over any trace of the result — see
[Monte Carlo statistics](data-display.html#statistics) and [trials](data-display.html#trials) for the
menus. It is the bar-chart button under the card's remove button:

{{ui: yield-trace-card}}

| Question | Plot |
|---|---|
| How is a spec's worst value spread, and how far is it from the limit? | **Histogram** of `goal:<spec>:worst`, with the limit as a dashed line |
| What fraction of parts land below a value? | **CDF** — read the yield at any limit straight off it |
| Is the spread Gaussian, or does a tail reach further? | **Normal Quantile** — a straight line when it is Gaussian |
| Which nominal would give more yield? | **Yield Sensitivity vs** a value — the yield in each bin of that value; the nominal sits best under the tallest bars |
| Where across the band do parts fail? | The trials as a family, **Colour By ▸ Pass / Fail** |
| What does the band look like across thousands of trials? | **Envelope** (P1–P99, say) with **Show Curves** off |
| Does one value drive a spec, and in which direction? | **Scatter vs** that value, with **Fit Line** |
| Which values matter most? | **Contributions** — each value's share, largest first |

**Yield Sensitivity vs** a value bins the trials by that value and shows the yield in each bin, with the
number of trials per bin on the right axis. Bins at the edges hold few trials, so read their yield loosely:

{{ui: yield-sensitivity}}

With thousands of trials an **Envelope** reads better than the curves themselves — here the band 98 % of
built filters fall inside, around the median, with **Show Curves** off:

{{ui: yield-envelope}}

A contribution is a straight-line fit. When a spec gets worse whichever way a value moves — a filter's
passband edge as a resonator is detuned either way, say — the fit explains little (a low R² in the label)
and the shares say little; the scatter shows the shape.

## Corners {#corners}

In Corners mode the panel lists the schematic's corners — name, enabled, the kit sections, the
temperature and the values each binds; a statistical corner shows the trial it replays.
**Generate…** builds corners from every combination of the kit sections you tick, the temperatures
and the values you type, and shows how many before it writes them. **▶** evaluates every enabled corner
and shows a grid of corner × spec: the margin in each cell, failing cells red, each spec's worst corner
in bold. With **MC at each corner**, the grid shows each spec's yield at each corner instead.

**A library part's corners.** A part whose model declares [corners](model-library-format.html#corners) — its
min and max, say — offers them in two places. On the placed part, the Parameter Editor's **Corner** chooses one
for that instance; a value you set on the same instance still wins, and its row reads *overrides corner min*. In
a bench corner, `part:<part number>=<corner>` sets the corner of **every** instance of that part, as parts from
one lot, overriding each instance's own choice:

```netlist
corner slow temp=85 part:TSTCAP=max
```

**Generate…** lists each placed part with corners as an axis, a tick box per corner, crossed with everything else.
With **MC at each corner**, a part whose corner the corner sets holds its own tolerances at nominal there — the
corner is its limit — while every other spread draws.

**A statistical corner** replays one trial. **Save as corner…** on a trial's row (or on a plot) makes a
corner that applies that trial's draws — the offset of each toleranced value from its nominal, and the
kit's draws — around whatever the design's values are now, so it follows the design as you tune it.
Save the worst trial of a run as a corner, and the optimizer can design against it.

## Optimizing and tuning across corners {#optimizing-corners}

The [Optimizer](optimization.html#corners) can meet every goal at every corner at once: set **Corners**
in its settings to `all`, or to names. Each point it tries then costs one simulation per corner, and
each goal names the corner that binds it. In [Tuning](tuning.html#running), **Evaluate at** moves the
sliders' simulations to one corner, so you can tune where the design is worst.

A goal met at a corner with no margin is met there only at the corner's nominal: the parts' tolerances
still push about half of the builds below it. Run **MC at each corner** after optimizing to see that
corner's yield, and give the goal margin where it binds.

## Centering {#centering}

Centering moves the values you let the [Optimizer](optimization.html) move — those with **Opt** ticked,
within their ranges — to wherever the most trials meet the specs, while their tolerances move with them.
The list shows each such value with its range and its tolerance beside it, and the toleranced values
that stay put.

Every position the search tries is judged on the same set of trials, so two positions differ by the
design and not by luck. The chart beside the status line is the best yield found at each iteration. At
the end, the starting values and the centred ones are each run on the same set of fresh trials, and the
panel shows both: **start yield [interval] → centred yield [interval]**. That pair — never the search's
own figure — is the result. If the two intervals overlap, the gain is not resolved at that many trials;
raise **Verify** to settle it.

A value set to a standard series (E6, E24, E96 and so on) is searched continuously, and its centred
value then lands on the series: the search tries the series values either side of where it stopped and
keeps whichever scores best. The trials around it still vary continuously within its tolerance, as a
real part does. After **Stop** the value stays where the search left it.

The digits button beside **Send to Tuning** sets how many significant figures the centred values show.
**Lock in**, **Push** and **Send to Tuning** keep them to the same figures. It is the same setting as
the Tuning and Optimizer panels' digits, saved with the schematic.

The chevron opens the centering settings: the search method, the trials each position is judged on,
the trials of the final check, the iteration and simulation limits, and **Surrogate**. With
**quadratic**, each position costs a dozen simulations or so instead of one per trial: circuitRF fits
each spec to a smooth curve through a few runs and counts the yield on the curve. It is much faster
with a handful of toleranced values and specs that change gently, and it never decides the answer —
the final check is still simulated. When the curve fits a spec poorly for three iterations running,
the search goes back to simulating every trial and says so.

When it finishes, **Lock in** keeps the centred values as a preset, **Push** writes them into the
schematic (one undo step), and **Send to Tuning** loads them into the Tuning sliders.

## Design of experiments {#doe}

**DOE** runs the schematic at a planned set of combinations of a few values — its **factors** — and
works out which of them move each spec and measurement, and how they interact. The factors are the
values with **Opt** ticked, each set to the low and high ends of its range, or — with **Factors:
tolerances** — the toleranced values, each set to its nominal ± k σ: a quick look at which tolerances
matter, in a handful of runs instead of a Monte Carlo.

Before you run, the panel lists the factors with the letter each is called by, their low, centre and
high values, and how many runs the design takes. The chevron chooses the design:

| Design | Use it for |
|---|---|
| **Full factorial** | A few factors (up to 10): every combination of low and high. |
| **Fractional** | More factors in fewer runs. **IV** keeps each value's own effect apart from the interactions; **V** keeps the interactions apart from each other too. |
| **Plackett–Burman** | Screening many factors (up to 23) in 12, 20 or 24 runs. |
| **Composite (ccf)** | A curved model of a few factors — to find where the specs are best met. |

**Centre points** add runs at the middle of every range; they show whether a response curves. A
simulation gives the same answer every time, so nothing is repeated and the order does not matter.

After a run, the **Effects** list shows, for the chosen spec or measurement, each value's effect — how
much it changes from low to high — and each pair's interaction, largest first. Effects in **bold** are
larger than the noise level estimated from the small ones. A fractional or Plackett–Burman design cannot
tell some effects apart; those are listed beside the effect they are mixed with, and its tooltip has
the full list. The display button opens, for each spec, the effects as bars with the noise level as a
line, the response at each value's low, centre and high, and the strongest interaction.

A good order is **Plackett–Burman** first to find the few values that matter, then **Composite** on
just those. **Model optimum** (crosshair) then searches the fitted model for where the specs are best
met inside the ranges, and checks that point with one real simulation — the panel shows what the model
predicted beside what the simulation gave. **Send to Tuning** loads the point into the Tuning sliders;
**Send to Optimizer** makes it where the Optimizer's next run starts.

## The example {#example}

**File ▸ New ▸ Examples ▸ Yield, Corners and Centering** is a workspace with one bench per part of this
page, each run taking a second or two: a bandpass filter estimated against specs loosened from its
optimizer goals, with its one-click yield display saved; an amplifier with six generated temperature and
supply corners and a statistical corner, failing hot at low supply until it is optimized across all of
them; a bias divider centred from 73 % to 99 %; and an amplifier and spiral inductor, both Touchstone
blocks, for the [Sensitivity](sensitivity.html) panel. Its README walks through each with the numbers a run
gives.

## From the command line, and from an agent {#headless}

Everything the panel runs, `circuitrf yield` runs too — `yield mc`, `yield estimate`,
`yield corners`, `yield center` (with `--surrogate quadratic`), `yield doe` and `yield trial` — and an agent runs
the same through MCP. See
[the command line](cli.html) and [AI agents](ai-agents.html#yield).
