---
title: EM Cells
slug: reference/em-cells.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM cells
lede: Simulating a schematic by EM of its own layout, at whatever values its variables have — and reusing it as a part.
keywords: EM, model=em, parametric EM, EM cell, EM cache, solve count, Simulate by, layout from schematic, sweep, optimize, 3D EM, .c3d, Palace, EMerge, openEMS, mesh
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#own-layout">Simulating a schematic by EM of its own layout</a></li>
<li><a href="#capable">What a schematic needs</a></li>
<li><a href="#what-runs">What the analysis runs on</a></li>
<li><a href="#as-a-part">Reusing it as a part</a></li>
<li><a href="#variables">Setting a cell's variables from the instance</a></li>
<li><a href="#cache">The EM cache</a></li>
<li><a href="#count">The solve count</a></li>
<li><a href="#unreached">A variable that does not reach the copper</a></li>
<li><a href="#optimise">Optimising geometry</a></li>
<li><a href="#in-3d">By EM in 3D</a></li>
<li><a href="#headless">From the command line</a></li>
</ol>
</nav>

A schematic of microstrip lines, tees and bends describes its own copper: **Update Layout from Schematic**
places it, joined, and its Ports become the EM setup's ports. This page is the next step: the same
schematic, with its analyses, goals and VAR block, simulated by EM of that layout instead of by its circuit
models — at whatever values its variables have when it runs.

## Simulating a schematic by EM of its own layout {#own-layout}

The choice lives on the analysis. Open it (double-click its card in the Analyses panel) and set **Simulate
by** to **EM of this schematic's layout**. Nothing else in the schematic changes: the analysis keeps its
name, so its measures, goals and Data Display traces work as before, and a trace from an EM run says
**(EM)** after its name.

At each run, and at each point of a sweep, circuitRF:

1. reads the **saved** schematic and puts the variables' current values into it — your VAR rows, a `--set`,
   a swept value or a tuned one;
2. re-plans the **saved** layout from it in memory, by the same rules **Update Layout from Schematic**
   follows, so every edit you made in the layout is kept and copper that grows pushes what is joined beyond
   it along;
3. solves that layout through its EM setup — or reads the result from the [EM cache](#cache) when the
   copper has been solved before.

**Your files are never written.** The `.csch`, the `.clay` and the `.cem` are read and left as they were;
an EM run of a schematic changes nothing you would see in the layout editor. To change the layout, run
Update Layout from Schematic. Values only reach the evaluation through the variables, so an unsaved change
to a VAR row counts; an unsaved change to the drawing itself (a part added, a wire moved) does not — save
first.

Several setups can analyse one layout. When there are several, pick one in the **EM setup** box beside
**Simulate by**; with one, the box shows it and stays greyed.

## What a schematic needs {#capable}

- **A layout made from it.** Run Update Layout from Schematic once; the layout keeps the record of which
  part is which, and that record is what lets a change of value reach the copper.
- **An EM setup analysing that layout.** File ▸ New ▸ EM Setup, the layout editor's EM button, or Update
  Layout from Schematic with **Create or update EM setup** on.
- **Ports numbered 1 to N.** The EM result's port *k* is the schematic's Port with `Num = k`.

Anything missing stops the run with a sentence naming it. EM never falls back to the circuit models
without saying so — `circuitrf check` reports the same sentence before you run.

## What the analysis runs on {#what-runs}

When every part of the schematic is copper — lines, tees, bends, crosses, coupled lines — the analysis runs
on the EM N-port between your Ports. An S-parameter analysis then reports exactly the EM S-parameters,
renormalised to the Ports' `Z` as any SnP is.

Two kinds of part stop the run, by name:

- **A part with no layout at all**, such as an ideal resistor between two line ends. Nothing in the layout
  stands for it, so it cannot be connected to the EM result. Give it a footprint, or replace it with a line
  model.
- **A part that is not copper**, such as a footprinted capacitor or a transistor. Its pads are copper, but
  the part between them is not, and joining its own model to the EM result needs ports at its pads. A
  layout's planar EM setup makes none — only a 3D view's setup does. Run that analysis by circuit models
  for now.

## Reusing it as a part {#as-a-part}

The same schematic can be placed as one part in a larger schematic, with terminations or other circuitry
around it. Place its cell as you place any cell. It needs no symbol of its own: one is made for it, with one
pin per Port, numbered by the Ports' `Num`, so the EM ports, the symbol's pins and the larger schematic's
wires agree with no step from you.

In the placed part's properties, **Model** chooses **Circuit** (the default) or **EM**. With EM, the part
is the EM result of its own layout at the values the instance gives it. An analysis set to **EM of this
schematic's layout** in the larger schematic also switches every such part to EM.

## Setting a cell's variables from the instance {#variables}

A placed cell's properties list its VARs in a **Variables** group under its parameters. A blank row is the
cell's own value, shown greyed; a value typed there sets that variable for this instance only, evaluated in
the larger schematic's scope as a parameter is. Variables that depend on it follow: with `L = L90 - w50/2`
in the cell, setting `w50` on the instance moves `L` too.

This works for every placed cell, not only EM ones. Three things follow:

- A variable set on an instance is a tunable and an optimisation variable, named `X1.w50` — the same form
  as a parameter of a placed cell. (`Cell:w50` is the cell's own variable, shared by every instance.)
- Renaming a cell's variable is a change to its interface: every instance placed against the old name says
  so, as a renamed parameter does.
- A name that is both a parameter and a variable of one cell cannot be set from an instance — it would be
  unclear which is meant — and a name the cell neither declares nor reads is refused rather than ignored.

## The EM cache {#cache}

Every EM result is kept under the workspace's `results/em-cache/<cell>/`, named by the **geometry** it was
solved at — the copper on every layer the stackup solves, the port labels, the setup's settings, the
stackup and the solver. Two values that round to the same copper share one result; anything that changes
the copper — a value, a hand edit, a generator change — makes a new one. Beside each result,
`<key>.provenance.txt` lists the values that first produced it.

The cache is never trimmed on its own. **Layout ▸ Clear EM Cache** removes it; the next run solves what it
needs again.

## The solve count {#count}

Before anything is solved, the run works out every EM evaluation it will make at every point and says so:

> 2 EM solves needed for 3 evaluations (2 distinct geometries, 0 already in the EM cache), about 24 s of
> factoring, up to 1.7 GB a frequency point.

The time and memory are the figures `em --dry-run` gives for the same setup; the fill is extra. When more
than one solve is needed, Simulate asks once before it starts. The command line and the MCP server do not
ask: they stop at a limit (20 by default) with the same sentence, before solving anything.

## A variable that does not reach the copper {#unreached}

A value you set in the layout is kept over the schematic's (see
[the layout editor](layout-editor.html)). A variable that would have driven it then has no effect on that
part, and the run says so:

> w50 does not reach ML3.W, which is set in the layout.

`circuitrf check` says it too. Sweeping or optimising such a variable moves nothing on that part.

## Optimising geometry {#optimise}

The VAR block's variables are already tunables, so an analysis run by EM can be optimised in the schematic you
drew: tick the variables in the Optimizer, add goals on that analysis, and run. What an EM solve costs decides how,
and the **EM** section of the Optimizer's ⚙ settings (the `.cnl` keys `em=` and `emBudget=` on the `optimize` line)
offers three ways:

- **Correct the circuit models with EM** (`em=correct`, the default when a goal reads an analysis run by EM). The
  same schematic, its analysis run by circuit models, does the searching: it costs nothing and is usually close. Each
  EM solve records, for every goal at every point of its trace, how far EM is from the circuit model, and the next
  search adds that difference while staying within a step of the solved point. A worse EM result halves the step.
  It stops when EM meets every goal, when the next point would be the same copper as the last, when the step is
  smaller than the layout's database unit, or at the budget. A handful of solves is typical.
- **Every evaluation by EM** (`em=direct`). The search runs on EM itself, each distinct geometry solved once and
  then read from the EM cache. Only algorithms that work in few evaluations are accepted: pattern search, simplex,
  the trust-region model and Bayesian (Automatic runs pattern search).
- **Circuit models only** (`em=off`).

**Most EM solves** (`emBudget=`, 12 by default when correcting, 30 when every evaluation is EM) caps the solves the
run may make; geometries already in the cache are free. The run stops there and keeps its best point. **The best
point is always one EM has scored**: what Push writes back is never a circuit-model answer.

Correcting lifts or lowers the circuit model's response by what EM says. When EM moves a response in
frequency instead, say a resonance a few percent lower, that correction is slow. The run says so when it ends
short of its goals; then run a short pass with every evaluation by EM, starting from where it stopped.

A length is never stepped by less than two database units of the layout, because a smaller step leaves the
copper unchanged. A variable that cannot change the copper (one that [does not reach it](#unreached), or a part's
value rather than a variable) is refused before anything is solved.

### The copper stays connected

Optimising changes lengths, widths and gaps, never which pieces of metal touch. Before the first solve the run
plans the layout at the start values and records which joints close, which parts' copper is kept apart, and, for a
part such as a coupled line, which of its own pins its copper joins. A start whose loop does not close, or whose
copper overlaps where the schematic does not join it, is refused, quoting Update Layout's report. A joint you
opened by hand is part of the design and is kept open.

Every candidate point is planned first, which takes milliseconds. One that would open a joint or make two
parts on different nets touch is not solved. The run's history says why, for example:

> L90 = 0.05 mm: ML3 overlaps ML4 (nets n3/n9, n6/n12): the schematic's lengths make these cross

A gap you designed, such as a coupled line's spacing, may vary freely as long as its two sides stay apart.

Before the first solve each variable is also scanned alone across its range, and the run says where its copper
stays connected:

> L90: connected from 0.144 mm to 6 mm (range 0.05 mm to 6 mm).

The Optimizer's **Tighten to the connected ranges** button sets each such variable's range to that interval.

### Applying the result

Push writes the best values into the VAR block, as values you typed, in one undo step. With a result from EM it
then runs **Update Layout from Schematic**, which keeps your hand edits, and checks that the layout now drawn is
exactly the geometry EM solved. The status line says so, or says that it is not.

### Sweeps, yield and sensitivity

A parametric sweep over the variables of an analysis run by EM solves (or reads from the cache) each of its points.
A point whose copper connects differently from the first point's is solved as drawn, and the run says so. A yield,
Monte Carlo or sensitivity run needs too many evaluations to make each one an EM solve, so it is refused with its
count. Run it by circuit models with `--em off`, or set the analysis to simulate by circuit models.

## By EM in 3D {#in-3d}

An enclosure, a connector launch, bond wires, a lid or a patterned dielectric need a full 3D solve. The cell's
**3D view** (`<cell>/3d/<cell>.c3d`) can be its EM view instead of a `.cem`: any **driven** setup embedded in it is
offered in the **EM setup** box beside **Simulate by**, spelled `<view>.c3d#<setup>`. Palace, EMerge or openEMS solves
it, as the setup says. With one EM view in all, it is used; with a `.cem` and a 3D setup, or several of either, pick
one. The `.cnl` key is `emSetup=C1#Driven1` on an analysis of the cell's own schematic; a netlist that is not the cell's names the view by its path, `emSetup=C1/3d/C1.c3d#Driven1`.

**Making the 3D view.** On a layout made from its schematic, **New 3D View from Layout** places the layout live, takes
its port labels as the 3D view's ports, and adds one VAR per name that drives the layout — each cell parameter and
each VAR of the schematic, at its current value. Add the enclosure, connector or lid around it, and use those VARs
in their dimensions.

**Live or flattened.** A layout placed **live** follows the schematic with no extra work: at each point it is
re-planned exactly as for a `.cem`, and the 3D view's ports are refreshed from its port labels before the solve. A
layout **flattened** into 3D bodies is a copy: its dimensions are the 3D view's numbers now, and it follows the
schematic only where they use a linked VAR. Each run says so for each flattened group, and a schematic VAR that no
dimension of the 3D view uses is reported as not reaching it.

**Linked VARs.** A 3D VAR named like a cell parameter or a VAR of the cell's schematic takes that value at every
point, so `w50` in the schematic sizes the copper and any enclosure height or connector offset written with it. A
3D VAR whose name the schematic does not define stays the 3D view's own and cannot be tuned from the schematic;
`circuitrf explain <schematic> --tunables` lists such VARs. A 3D VAR whose `Linked` is `false` keeps its own value.

**The mesh is held.** A 3D mesh changes with the geometry, and that change is noise between nearby points that an
optimiser would read as a slope. Every evaluation of the cell is meshed by the same rule: the setup's sizes, every
refinement pass the setup allows (Palace and EMerge stop early otherwise, a different number of passes at different
points), openEMS's merge distance and each mesh region's element size at the values the saved design gives. The run
says what it held. An optimisation does not step a length by less than the held mesh's smallest element.

**Connectivity in 3D.** Besides the layout's own joints, every body of the 3D view's own — a bond wire, a connector
pin, a lid — is checked against what it touches at the start. A point where one leaves its pad or touches copper it
did not touch is infeasible, says which, and costs no solve.

**Budgets.** A 3D solve takes minutes to hours, so the counts use each solver's own size estimate, a solve whose
estimate is past this machine's memory is refused before any solve starts, and an optimisation's default budgets are
smaller: `em=correct` stops at 6 solves and `em=direct` at 15 unless `emBudget=` says otherwise. From an agent, start
such a run with `async=true`.

The cache key covers the whole 3D problem: the copper, every body at its evaluated size, the materials, the setup's
solve settings and the solvers' versions. A VAR change that moves nothing still reads the cached result.

## Placing an EM component and optimising it {#placed}

A coupler, a filter or a transition simulated by EM can sit among other circuitry, and an optimisation can vary its
own values, the circuitry around it, or both.

**Three kinds of EM component.** A placed cell's properties say which it is, and so do `circuitrf check <cell>` and
`circuitrf explain <schematic> --tunables`:

| Kind | Its geometry follows | What can be varied |
|---|---|---|
| **Schematic-driven** | its VARs and parameters: its layout is made from its schematic | the instance's own values |
| **3D-parametric** | VARs of its 3D view linked to its parameters; no schematic drives it | the instance's own values |
| **Fixed** | nothing: a drawn layout with an EM setup, or the result of one EM run (`<setup>_sim`) | none of its own; the circuitry around it as usual |

A 3D-parametric cell with no schematic has no circuit model, so it can only be simulated by EM: an optimisation
with `em=` unstated uses `em=direct` and says why, and `em=correct` or `em=off` is refused naming the part. Give the
cell a schematic as its circuit model and `em=correct` works.

**Each placement at its own values.** When the larger schematic is laid out (**Update Layout from Schematic**), a
placement whose own values give it different copper is placed as a **variant**: the cell's layout made at that
placement's values, with your hand edits to the cell in it. Placements with the same values share one variant, and
the cell's own layout is not changed. Variants are generated and read-only: make an edit in the cell, and the next
Update Layout carries it to every variant. A longer coupler's ports move, and the lines joined to them move with them,
as any length change carries its neighbours.

**Several instances.** `X1.w50` and `X2.w50` are separate variables. The solve count and the budget count distinct
geometries, so two placements at the same values cost one solve. To keep two placements alike, set both to one VAR of
the larger schematic.

**Connectivity, at both levels.** During an optimisation each EM component's own copper is held as before, and so is
the larger schematic's layout when it has one: a point that keeps the coupler intact but pulls its port off the line
it joins is infeasible, says so, and costs no solve.

**Where results are written.** Apply (Push, or `--apply`) writes each value where it is defined: a VAR of the larger
schematic to its VAR block, an instance's own value (`X1.w50`) to that instance. The cell itself is never changed by
Apply, because it may be placed elsewhere with other values. Its layout is then updated and every placed EM
component's geometry is checked against what EM solved.

**Make These the Cell's Own Values.** In a placed cell's properties, under **Variables**, this writes the instance's
own values into the cell — its VAR block, or a parameter's default — and clears them from the instance. Before writing
it lists every other placement of the cell in the workspace and what each will change to: a placement that states its
own value keeps it, one that does not takes the new one.

## From the command line {#headless}

The analysis's choice is the `.cnl` key `model=em` (and `emSetup=` when the layout has several setups); a
schematic run headless honours it. Every run verb also takes:

- `--em on` — run every analysis by EM, and every EM-capable part by EM;
- `--em off` — run everything by circuit models;
- `--em-max-solves N` — the most uncached solves the run may start (default 20).

`circuitrf opt` takes `--em off|correct|direct` and `--em-budget N` for one run, and `--apply`: the best
EM-verified values are written into the schematic's VAR block, its layout is updated from the schematic, and the
saved layout's geometry is checked against the one EM solved. A mismatch is an error, exit 1. The report's `em` object
lists every EM evaluation (its values, its geometry, whether the cache held it, each goal's margin) and each
variable's connected range. `yield` and `yield sens` take `--em off`.

`circuitrf explain <file> --analysis` lists, for each analysis run by EM, every evaluation in run order and
whether the cache holds it. The details are in [the command line reference](cli.html).
