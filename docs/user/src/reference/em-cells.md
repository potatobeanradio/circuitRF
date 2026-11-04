---
title: EM Cells
slug: reference/em-cells.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > EM cells
lede: Simulating a schematic by EM of its own layout, at whatever values its variables have — and reusing it as a part.
keywords: EM, model=em, parametric EM, EM cell, EM cache, solve count, Simulate by, layout from schematic, sweep, optimize
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

## From the command line {#headless}

The analysis's choice is the `.cnl` key `model=em` (and `emSetup=` when the layout has several setups); a
schematic run headless honours it. Every run verb also takes:

- `--em on` — run every analysis by EM, and every EM-capable part by EM;
- `--em off` — run everything by circuit models;
- `--em-max-solves N` — the most uncached solves the run may start (default 20).

`circuitrf explain <file> --analysis` lists, for each analysis run by EM, every evaluation in run order and
whether the cache holds it. The details are in [the command line reference](cli.html).
