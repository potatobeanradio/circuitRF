---
title: Sensitivity
slug: reference/sensitivity.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Sensitivity
lede: Which values move each goal, and by how much — ranked at the design as it stands, before you tune, optimize or estimate a yield.
keywords: sensitivity, sensitivity analysis, sense, sensitive, ranking, contributors, share, basis, sigma, step, nonlinear, one-sided, s-parameters, snp, touchstone, entry, port, instance, reciprocal, tie, snpexclude, matrix, pareto, normalized margin, spread, estimate
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What does the design depend on?</a></li>
<li><a href="#order">Where it fits: Sensitivity, then Tuning and the Optimizer, then Yield</a></li>
<li><a href="#settings">What is stepped, and by how much</a></li>
<li><a href="#snp">S-parameter blocks</a></li>
<li><a href="#ranking">Reading the ranking</a></li>
<li><a href="#basis">The basis column</a></li>
<li><a href="#levels">Entry, Port and Instance</a></li>
<li><a href="#flags">Nonlinear, one-sided and failed</a></li>
<li><a href="#tile">The S-parameter matrix tile</a></li>
<li><a href="#actions">Acting on a row</a></li>
<li><a href="#display">The display</a></li>
<li><a href="#example">The example</a></li>
<li><a href="#headless">From the command line, and from an agent</a></li>
</ol>
</nav>

## What does the design depend on? {#what}

The **Sensitivity** panel (View ▸ Panels ▸ Sensitivity, the tab after the
[Optimizer](optimization.html), where the goals are set) answers that question for the focused schematic, at its values as they
stand. Press **▶** and every value is moved a small step up and a small step down on its own, with
everything else held where it is. Each goal is then given a ranked list of the values that move it, with
each one's share of the total.

That is 2k + 1 simulations for k values — the design once, then each value twice — and nothing is drawn
at random, so the same design always gives the same ranking. The cost is shown under the settings
before you run: `15 factor(s) (7 at ±0.1 dB, 7 at ±1°, 1 at ±σ), 31 evaluations`.

The panel is the [Yield](yield.html) panel's view with one mode. It edits the same schematic: a
tolerance added here is in the Yield panel at once, and **⌘Z** undoes it from either. Nothing runs until
you press **▶**, and a run never changes the schematic's values; its result is written beside the
schematic as `<name>.sens.npy`.

## Where it fits: Sensitivity, then Tuning and the Optimizer, then Yield {#order}

The panels sit in the order a design is usually worked:

1. **Sensitivity** — find the few values that matter.
2. **[Tuning](tuning.html)** and the **[Optimizer](optimization.html)** — move those few, by hand or by
   search, until the goals are met.
3. **[Yield](yield.html)** — give the values that matter a tolerance and count how many builds pass.

The Optimizer's **Sensitivity** button opens this panel on the same schematic when no optimization has
run. After a run, the same button ranks the variables around the optimizer's best point instead.

## What is stepped, and by how much {#settings}

| Setting | What it does |
|---|---|
| **Factors: All** | Every value the design offers. A value with a tolerance steps ± k σ through its own distribution; any other steps ± **Step** % of its value; a phase steps ± 1°. |
| **Factors: Toleranced** | Only the values with a tolerance, at ± k σ. |
| **Factors: Designable** | Only the values with **Opt** ticked, at ± **Step** % of their value — not of their range. |
| **Step** | The percent a value with no tolerance moves (default 1 %). |
| **σ** | How many standard deviations a toleranced value moves (default 1). |
| **S-parameters** | Whether S-parameter blocks are stepped at all, and by how many dB and degrees (default 0.1 dB and 1°). |

A port's reference impedance and a source's tone frequency are left out of **All**; each run says which
values it left out and why. Each setting is saved with the schematic as the `sensitivity` line, and only
a value that differs from its default is written.

## S-parameter blocks {#snp}

Every S-parameter of a Touchstone block is a value the ranking can move: its magnitude in dB and its
phase in degrees, offset the same at every frequency. Before any run the panel lists each block under
**S-parameter blocks** — `2 ports · tied · 6 factors` — so a block you are not going to change can be
left out before its simulations are spent. Untick **Include** and the cost under the settings drops by
two simulations per factor.

Untick **S-parameters** and every block is left out at once. Each block's **Include** box stays as you
set it, greyed, and ticking **S-parameters** again brings those choices back.

A block whose file is reciprocal — S21 equal to S12, as for any passive part — offers one value for each
pair: moving S21 moves S12 with it. A block that is not, such as an amplifier, offers every entry on its
own. **Tie reciprocal pairs**, on the [matrix tile](#tile), changes that.

## Reading the ranking {#ranking}

Choose a goal — under **Specs**, or in the picker above the list — and the list shows what moves it,
largest first:

| Column | What it shows |
|---|---|
| **basis** | What the value was stepped by: `σ`, `1 %`, `0.1 dB · 1°`. |
| **Δ** | Half the change from the step down to the step up. A goal's is in **normalized margin** — its margin divided by its scale — so goals in dB and goals in volts rank on one footing. A measure's is in its own unit. A row that groups several values shows √ΣΔ², with no sign. |
| **share** | Δ² as a part of the goal's total. The shares add up to 100 %. |

Each goal row under **Specs** shows **m₀**, its normalized margin at the design as it stands (positive
means met), and **σ**, the spread the ranking predicts (√ΣΔ²). When every value was stepped by its own
tolerance, a first-order pass estimate follows, labelled `estimated`. The yield itself is what the Yield
panel counts.

An S-parameter block you left out is the last row, greyed, with `excluded` for its basis and no share:
it was not found insensitive, it was not stepped. Tick its box to include it in the next run. A run does
not repeat on its own, so until you press **▶** again the status line says the ranking is out of date.

## The basis column {#basis}

A ranking can mix two kinds of step, and the basis says which each row had:

- **σ** — the value moved by its own tolerance. Its share says how much of the goal's spread in
  production comes from that part.
- **%**, **dB**, **°** — the value moved by a fixed small step. Its share says how strongly the goal
  responds near here: what a designer could change, not what production will do.

When any row is not **σ**, there is no pass estimate, because σ is then not a production spread.

## Entry, Port and Instance {#levels}

The same run, grouped three ways — the toggle regroups the list without simulating again:

| Level | One row per |
|---|---|
| **Entry** | value, with an S-parameter's dB and phase as one row (`AMP · S21`). |
| **Port** | port of each S-parameter block: every entry in that port's row or column. S31 counts toward port 3 and port 1, so the shares overlap and the column reads **share (overlapping)**. Values that are not S-parameters keep their own rows. |
| **Instance** | part, block or variable. A block's row opens to its entries, and an entry to its dB and phase. |

## Nonlinear, one-sided and failed {#flags}

A glyph at the end of a row flags a value whose Δ needs care; its tooltip gives the reason.

| Flag | Meaning |
|---|---|
| **Nonlinear** (wave) | The step up and the step down disagree — often a goal over a sweep whose worst point moves between them, or a value near a resonance. A [DOE](yield.html#doe) with **Composite** or a Monte Carlo sees what a single Δ cannot. |
| **One-sided** (half circle) | Only one of the two steps simulated; Δ is taken from that side. |
| **Failed** (alert) | Neither step simulated. The other values still rank. |

## The S-parameter matrix tile {#tile}

Select an S-parameter block, or any of its rows, and a tile opens under the list: one cell per entry,
coloured by that entry's share of the selected goal, on one scale from 0 to the tile's largest share.
The selected entry is outlined, and at the Port level so are the selected port's row and column. A tied
block colours S21 and S12 as one. Each cell's tooltip gives its share.

Two settings ride on the tile:

- **Include** — the same setting as the block's row under S-parameter blocks.
- **Tie reciprocal pairs (S21 = S12)** — whether one value moves both entries of a pair. Its tooltip
  shows what the file decides on its own (`auto: worst |S(i,j) − S(j,i)| = 0.0021`). Choosing what the
  file decides returns the block to automatic; choosing the other is saved on the block as
  `Reciprocal=tied` or `untied`. Tying ends the entries above the diagonal, so when a tolerance names one
  the panel lists those lines and asks first; the change and their removal are one undo step.

## Acting on a row {#actions}

Right-click a row:

- **Add tolerance** gives a value that was stepped by a percent, dB or degrees a tolerance, the same as
  the Yield panel's **+**. The next run steps it by σ.
- **Send to Tuning** adds the value to the Tuning panel.
- **Use in Optimizer** ticks **Opt** with the default range.

S-parameter offsets are one number at every frequency, so they are tolerances only: **Send to Tuning**
and **Use in Optimizer** are greyed on their rows.

**Run Yield on these** opens the Yield panel on the same schematic in Monte Carlo mode. When some of the
ranked values have no tolerance, its status line says how many: a Monte Carlo holds them still.

## The display {#display}

The chart button writes `<name>.sens.cdd` beside the schematic and opens it: one Pareto chart per goal at
the level the list shows, the shares as bars, largest first, with their running total as a line. The Port
level has no running total, because its shares overlap.

## The example {#example}

**File ▸ New ▸ Examples ▸ Yield, Corners and Centering** includes **SnpSensitivity**: an amplifier's
Touchstone block followed by a spiral inductor's, with a toleranced shunt capacitor and two specs. The
spiral's file is reciprocal and the amplifier's is not, so the two tiles show a tied and an untied
block. Its README says what to click.

## From the command line, and from an agent {#headless}

`circuitrf yield sens <schematic>` runs the same ranking and writes the same `.sens.npy`; `--by entry`,
`port` or `instance` prints a level. An agent runs it through MCP as `run analysis=sensitivity`. See
[the command line](cli.html) and [AI agents](ai-agents.html).
