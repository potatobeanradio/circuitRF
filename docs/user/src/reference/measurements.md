---
title: Measurements
slug: reference/measurements.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Measurements
lede: A measurement is a named figure of merit computed from a run's results — Gain, PAE, Pout, whatever you can write as an equation over the analysis output. It draws no current and stamps nothing; it is cube algebra evaluated after the analyses finish, and its result plots like any other trace.
---

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/meas.svg" alt="MEAS symbol">
    <img class="sym-dark"  src="../assets/symbols/meas-dark.svg" alt="">
  </span><figcaption>The MEAS component holds your measurement equations</figcaption></figure>

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#what">What a measurement is</a></li>
      <li><a href="#authoring">Authoring — the MEAS component</a></li>
      <li><a href="#refs">Referencing analysis results (two notations)</a></li>
      <li><a href="#sparam">S-parameters: 1-based ports</a></li>
      <li><a href="#compose">Composition &amp; scope</a></li>
      <li><a href="#swept">Swept variables</a></li>
    </ol>
  </nav>

## What a measurement is {#what}

It is the RF engineer's "equations" pane. Once your analyses have produced their result cubes, a
measurement pulls values out of them and computes a derived quantity: `Gain = dB(...)`, `PAE =
...`, `Idc = DC1.I("Iout")`. The result is a new cube added to the run's `measurements` group,
addressable by its bare name and plottable like any trace. Measurements use the same [expression
language](expressions.html) as everything else, with operands extended to cube quantities.

## Authoring — the MEAS component {#authoring}

Drop a **MEAS** component on the schematic (it is an annotation — no ports, nothing emitted to the
netlist) and edit its rows, one per line, exactly like a [VAR](components.html#var) block:

```netlist
Gain  = dB( HB1.V("Vout", 1, All) / HB1.V("Vin", 1, All) )   dB
Pout  = 0.5 * real( HB1.V[:, "Vout", 1] * conj(HB1.I[:, "Iout", 1]) )   W
PAE   = 100 * (Pout - Pin) / Pdc   %
```

Each row is `name = expression [unit]`. Row order — and the order of multiple MEAS components — is
the declaration order the evaluator uses (so later rows can reference earlier ones). A duplicate
name is a reported conflict; the first definition wins. MEAS at the top testbench level feeds
`TestBench.Measurements`; a MEAS inside a sub-cell is ignored (with a warning).

## Referencing analysis results (two notations) {#refs}

A measurement reads an analysis by the **name it appears under in the results tree** (`HB1`,
`SP1`, `DC1`, …). There are **two equivalent notations** for pulling a value out of a cube — mix
them freely.

### 1. Accessor — name-keyed (durable)

You name the node/branch and the harmonic; remaining axes default to `All` (kept whole).

```text
HB1.V("Vout")          node Vout, all harmonics, all sweep points
HB1.V("Vout", 1)       node Vout, fundamental, all sweep points
HB1.I("M1:d", 1, All)  device-port branch current, fundamental, swept
DC1.I("Iout")          no sweep → a scalar
```

**Why use it:** it is order-independent — the engine locates each axis by name, so adding or
reordering sweep axes never breaks the expression. This is the right choice for measurements you
author by hand and keep.

For a **two-tone** run the spectral axis is `mixIndex`, so the second argument is the
mixing-product tag `"(k₁,k₂)"` instead of a harmonic number:

```text
HB1.V("Vout", "(1,0)")    carrier 1 (f₁)
HB1.V("Vout", "(1,-1)")   IM2 product (f₁−f₂)
```

Because the accessor keeps swept axes automatically, the same expression works with or without a
Pin sweep (one value vs a curve) — see [measuring
intermodulation](simulations.html#two-tone-meas).

### 2. Bracket — positional (fast copy/paste)

One token per cube axis, in cube-axis order (numpy-style): `:` keeps the axis, a name or integer
*fixes* and drops it, `a:b` keeps a sub-range.

```text
Pout_W = 0.5 * real( HB1.V[:, "Vout", 1] * conj(HB1.I[:, "Iout", 1]) )
```

**Why use it:** it is exactly what the Plot Inspector's trace card writes for you. Dial in a
trace, copy the shorthand string straight into a measurement, done — no remembering argument
order. The catch: brackets are positional, so a later outer sweep can shift the axis a hand-edited
bracket addresses.

<div class="callout note">
    <span class="label">Rule of thumb</span>
    <p>Prefer the <strong>accessor</strong> for durable hand-authored measurements; reach for the
    <strong>bracket</strong> when copy-pasting from a trace you already have on screen.</p>
  </div>

## S-parameters: 1-based ports {#sparam}

On an S-parameter cube, the `i`/`j` axes carry **1-based port numbers** — the way RF engineers
name S-parameters — in both notations:

```text
s21    = SP1.S(2, 1)          S21 over frequency
s21_db = dB( SP1.S[:, 2, 1] ) S21, freq kept (:), ports fixed
s11    = SP1.S[:, 1, 1]        S11
```

`SP1.S[:, 2, 1]` is **S21**, not "row 2 column 1 by zero-based index." A port outside `1..nPorts`
is a clear error listing the available ports.

## Composition & scope {#compose}

Measurements are evaluated in declaration order, and each is in scope by name for the ones after
it — so a complex figure of merit builds from intermediates. Also in scope: every global
[VAR](components.html#var) variable (by name). The element-wise helpers `conj`, `real`, `imag`,
`mag`, `phase`, `dB`, `dB10`, `dBm`, `log10`, `ln` broadcast over cubes. Referencing an unknown
analysis raises an error naming the available ones; a failing measurement is reported as a run
note and does not fail the whole run.

## Swept variables {#swept}

A global variable that is also a parametric-sweep axis is injected as a **1-D cube** (one element
per sweep point) rather than a scalar — so `Pin_avail = Pin` over a 10-point `Pin` sweep yields a
10-element curve. Its axis matches the swept analysis's axis, so it broadcast-aligns: `Gain =
dB(HB1.V("out",1,All)) - Pin` resolves element-wise. A non-swept global stays a scalar.

---

<p class="small">See also: <a href="expressions.html">Expressions</a> (the language) ·
  <a href="components.html#meas">MEAS component</a> · <a href="plot-types.html">Plot types</a>. Full design:
  <code>docs/design/measurements.md</code>.</p>
