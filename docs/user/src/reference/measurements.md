---
title: Measurements
slug: reference/measurements.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Measurements
lede: A measurement is a named figure of merit computed from a run's results — Gain, PAE, Pout, whatever you can write as an equation over the analysis output. It draws no current and stamps nothing; it is cube algebra evaluated after the analyses finish, and its result plots like any other trace.
keywords: measure, measurement syntax, expression syntax, syntax, how to write a measurement, MEAS, post-processing, expression, equation, derived result, post process, gain vs pout, PAE
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
      <li><a href="#power">Power through an IProbe: <code>P</code></a></li>
      <li><a href="#sparam">S-parameters: 1-based ports</a></li>
      <li><a href="#compose">Composition &amp; scope</a></li>
      <li><a href="#swept">Swept variables</a></li>
      <li><a href="#gain-vs-pout">Gain against output power, at each frequency</a></li>
      <li><a href="#freq">Frequency: <code>freq</code></a></li>
      <li><a href="#spec">Spec checks: the worst value over a band</a></li>
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
Pout_W   = 0.5 * real( HB1.V("Vout", 1) * conj(HB1.I("Iout", 1)) )   W
Pout_dBm = dBm(Pout_W)                                              dBm
Gain     = Pout_dBm - Pin                                           dB
Pin_W    = 10^((Pin - 30)/10)                                       W
PDC_W    = real( HB1.V("VDD", 0) * HB1.I("IDC", 0) )                W
PAE      = 100 * (Pout_W - Pin_W) / PDC_W                           %
```

Here `HB1` is the analysis, `Vout` and `VDD` are net names, `Iout` and `IDC` are
[IProbe](components.html#iprobe) current probes, and `Pin` is the global variable the source's
`Pavl` is set from, in dBm. Use your own names. `dBm()` takes watts, which is why `Pout_W` is
computed first.

Each row is `name = expression [unit]`. The expression is written in the
[expression language](expressions.html) used everywhere else in circuitRF: its
[operators](expressions.html#operators) and [functions](expressions.html#constants) work on
results the same way they work on numbers. What this page adds is how a row reads an analysis's
results. Row order — and the order of multiple MEAS components — is
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
HB1.P("Iout")          power through IProbe Iout, fundamental band, in W — see Power through an IProbe
ENV1.Pt("Iout")        the same power at every envelope step (SaveWaveforms=yes)
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
*fixes* and drops it, `a:b` keeps a sub-range. A range is not allowed on an S matrix's port axes;
use a [port range](#port-ranges) instead.

```text
Pout_W = 0.5 * real( HB1.V[:, "Vout", 1] * conj(HB1.I[:, "Iout", 1]) )
```

or `HB1.P("Iout")`.

**Why use it:** it is exactly what the Plot Inspector's trace card writes for you. Dial in a
trace, copy the shorthand string straight into a measurement, done — no remembering argument
order. The catch: brackets are positional, so a later outer sweep can shift the axis a hand-edited
bracket addresses.

<div class="callout note">
    <span class="label">Rule of thumb</span>
    <p>Prefer the <strong>accessor</strong> for durable hand-authored measurements; reach for the
    <strong>bracket</strong> when copy-pasting from a trace you already have on screen.</p>
  </div>

## Power through an IProbe: `P` {#power}

`P` reads the power flowing through an [IProbe](components.html#iprobe), from its first node to its
second, in watts. Place an IProbe in series with each place you want a power — the load, the input,
each supply — and name it:

| Spelling | Result |
|---|---|
| `ENV1.P("Iout")` | fundamental-band power through `Iout` |
| `ENV1.P("Iout", k)` | power at harmonic `k` (`0` is DC: V₀·I₀), or at a mixing product by its tag, `"(1,0)"` |
| `ENV1.Pt("Iout")` | instantaneous fundamental-band power against `time` (envelope only) |

It reads an [envelope](simulations.html#envelope-power) or a harmonic-balance analysis the same way,
so one measure line can point at either. The voltage is taken at the probe's first node, so put that
node on the source side and every power in a working amplifier is positive.

```netlist
measure Pout_dBm = dBm(ENV1.P("Iout"))
measure Pin_dBm  = dBm(ENV1.P("Iin"))
measure Pdc_W    = ENV1.P("IDC", 0)
measure Gain_dB  = Pout_dBm - Pin_dBm
measure DE_pct   = 100 * ENV1.P("Iout") / ENV1.P("IDC", 0)
measure PAE_pct  = 100 * (ENV1.P("Iout") - ENV1.P("Iin")) / ENV1.P("IDC", 0)
```

`Iout` is in series with the load, `Iin` between the source and the amplifier's input, and `IDC`
between the drain supply and the circuit. `dBm()` takes watts. Two amplifiers are the same lines with
each one's own probes (`Iout1`, `IDC1`, `Iout2`, `IDC2`).

- **Sweeps pass through.** `P` finds the probe and the harmonic by name and keeps every other axis, so
  the same line gives one number with no sweep, a curve under a `Pin` sweep, and a surface when a
  `Vdd` sweep is wrapped around that, with no edit. There are no positional sweep arguments. To read
  one sweep point, pin its axis by name with `at`, which takes the point's position along that axis
  (0 is the first, −1 the last): `at(ENV1.P("Iout"), "Pin", -1)` is the power at the highest drive.
  To read it by its value instead, use [`value_at`](#value-at): `value_at(ENV1.P("Iout"), 10, "Pin")`
  is the power at 10 dBm of drive.
- **Probe names** match like `I("…")`: the whole name, any case. An unknown name is an error that
  lists the analysis's IProbes.
- **With two or more fundamentals**, `P("Iout")` is the sum over the carrier products, the same band
  `Pavg_dBm` reports; `P("Iout", "(1,0)")` is one carrier.
- **`Pt` needs the waveforms.** With `SaveWaveforms=no` it is an error naming that setting. `P` never
  needs them.

`P("Iout")` means the same as the long form

```text
Pout_W = mean_over(real(0.5*ENV1.V("Vout", 1)*conj(ENV1.I("Iout", 1))), "time")
```

with `Vout` the probe's first node. The one difference is the window: `P` averages over the run's
measured window, after the settle a cold start skips, and the long form over the whole record, so `P`
agrees with the probe's `Pavg_dBm` and the long form can differ slightly. The long form also needs
`SaveWaveforms=yes`, and the probe's first node labeled, so its voltage is recorded.

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

The same `(i, j)` works on any result cube whose axes are ports: a [TRL calibration](simulations.html#trl)'s
fixtures, `CAL.SA(2, 1)` and `CAL.SB(2, 2)`, read exactly as `SP1.S(2, 1)` does.

### One S matrix per device: port ranges {#port-ranges}

Simulate two 2-port parts on one bench and the result is a single 4-port `S`. A port range gives each
part an S matrix of its own, so you can read each one's gain as S21:

```netlist
measure DUT_1 = SP1.S(1:2, 1:2)     ports 1 and 2, as a 2-port
measure DUT_2 = SP1.S(3:4, 3:4)     ports 3 and 4, as a 2-port
measure G1_dB = dB(DUT_1(2, 1))     S21 of the first part
measure G2_dB = dB(DUT_2(2, 1))     S21 of the second part: the bench's S43
```

- **`lo:hi` is port numbers, both ends included.** `3:4` is ports 3 and 4.
- **The result's ports are numbered from 1**, so `DUT_2(2, 1)` is the bench's S43. Frequency and any
  sweep are kept.
- **A range takes the ports the result has inside it.** On a bench whose ports are 1, 2, 9 and 10,
  `SP1.S(9:10, 9:10)` is a 2-port.
- **Rows and columns can differ.** `SP1.S(3:4, 1:2)` is the coupling from the first part into the
  second. A single number on one side keeps just that port: `SP1.S(1:4, 1)` is every port's response to a
  signal into port 1.
- **It works for any number of ports and on any port matrix**, including `Y`, `Z` and a TRL fixture's
  `SA`.
- **Read the new matrix as you would `S`**, in a later measure line, in a goal, or in a
  [Data Display](data-display.html) trace: type `DUT_2(2,1)`, or pick `DUT_2` under Measurements and
  set its two port rows.

Port ranges go in the parentheses only. In the bracket form a range counts positions from 0 with the end
left out, so `SP1.S[:, 1:2, 1:2]` would mean port 2 alone. A range on a port axis inside brackets is
therefore an error that tells you to write `SP1.S(1:2, 1:2)`.

## WSProbe accessors {#wsprobe}

A run with a [WSProbe](components.html#wsprobe) in it carries one `wsp` matrix plus a named set of
outputs per probe, and both are reachable by accessor:

```text
SP1.wsp                 the whole matrix, over {freq, row, col}
SP1.wsp(3, 13)          one element traced over frequency — the document's wsp(3,13)
SP1.idx("GATE")         the probe's 1-based index, as an integer
SP1.H0("GATE")          and Y0, ZG, ZL, LG, F, SM_Y0, SM_H0 the same way
```

`row` and `col` are **1-based matrix positions**, matched by axis value rather than by position, so
they are the reference document's own indices with no arithmetic in between — and, like the
S-parameter ports above, a positional slice would pin the frequency axis instead.

`HB1.*` resolves identically for a probe under [harmonic balance](wsprobe.html#hb), over `ssfreq`
rather than `freq`.

A worked measurement — the smaller of a probe's two stability margins, and the frequencies at which
the driving-point locus actually crosses:

```text
measure  SMgate = wsp_stability_margin(SP1.wsp, SP1.idx("GATE"))
measure  fosc   = wsp_unstable_freq_kurokawa(1 / SP1.H0("GATE"))
```

An unknown probe label is an error that lists the probes present. Every derived metric of
[The WSProbe](wsprobe.html#metrics) can be written this way, and evaluates identically in the window
and under `circuitrf sparam`.

## Composition & scope {#compose}

Measurements are evaluated in declaration order, and each is in scope by name for the ones after
it — so a complex figure of merit builds from intermediates. Also in scope: every global
[VAR](components.html#var) variable (by name). The element-wise helpers `conj`, `real`, `imag`,
`mag`, `phase`, `dB`, `dB10`, `dBm`, `log10`, `ln` broadcast over cubes, and so do the
[math functions](expressions.html#constants) (`sin`, `exp`, …); `max_over` and
`min_over` [reduce one](#spec). Referencing an unknown
analysis raises an error naming the available ones; a failing measurement is reported as a run
note and does not fail the whole run.

## Swept variables {#swept}

A global variable that is also a parametric-sweep axis is injected as a **1-D cube** (one element
per sweep point) rather than a scalar — so `Pin_avail = Pin` over a 10-point `Pin` sweep yields a
10-element curve. Its axis matches the swept analysis's axis, so it broadcast-aligns: `Gain =
Pout_dBm - Pin` resolves element-wise, one gain per drive level. A non-swept global stays a scalar.

A global **derived** from a swept one is a cube too. With `x` swept and `y = 2*x` in the VAR block, `y` reads
as one value per sweep point. Each point's value is evaluated exactly as that point's circuit evaluated it,
in plain numbers, so a conditional, `max(x, 0)` or `polar(1, x)` work as they do in the circuit. This holds
through other variables and user-defined functions. Over nested sweeps `y` has an axis for each sweep it
depends on, outermost first. If `y` has no value at some point (`log(x)` at `x = 0`), a measurement that
reads it fails and names the point. It never gets the nominal value of `y`.

## Gain against output power, at each frequency {#gain-vs-pout}

A measurement computes values. It does not choose what they are plotted against. With `Pin`
swept inside a sweep over `RFfreq`, the three rows below each give one value per
(`RFfreq`, `Pin`) point. That is a table with a row for each frequency and a column for each drive
level:

```netlist
Pout_W   = 0.5 * real( HB1.V("Vout", 1) * conj(HB1.I("Iout", 1)) )   W
Pout_dBm = dBm(Pout_W)                                              dBm
Gain     = Pout_dBm - Pin                                           dB
```

Gain *against Pout* is a choice about the plot, so you make it in the
[Data Display](data-display.html#versus), not in the MEAS block. A trace card on `Gain` already
opens with the innermost sweep, `Pin`, as **X** and the next one out, `RFfreq`, as the family, so
there is one step: tick **vs X** and pick `Pout_dBm`. Each frequency's curve is drawn against its
own output power. Typed in the card's spec box, the same trace is:

```text
Gain vs Pout_dBm
```

A sweep added later keeps this working. Re-run with a `VDD` sweep around the other two and the
card stays on Pin as X with one curve per frequency, with `VDD` pinned at its first value; click
**Fam** on the `VDD` row to draw one curve per supply voltage instead.

Do not put `vs` in a MEAS row. It is part of the trace card's plotting shorthand, not the
expression language, so a measurement containing it does not evaluate. Headless, `circuitrf plot
run.npy -o gain.svg --trace "cube=Gain vs Pout_dBm"` draws the same picture.

## Frequency: `freq` {#freq}

`freq` in a measurement is the run's **frequency axis in Hz**, as a 1-D cube — one element per
analysis point — so a quantity that needs ω is written the obvious way:

```text
measure  Xm    = imag(zm)                 an intermediate reactance, in ohms
measure  Lval  = Xm / (2*pi*freq)         henries, one per frequency
```

Its axis is the analysis's own `freq` axis, so it broadcast-aligns with every cube taken from that
analysis. `freq` is reserved — a VAR may not be named `freq`.

It needs a frequency axis to be. A run whose analyses produced none (DC only), or two analyses on
different frequency grids, leaves `freq` unresolved and the measurement's error says which of the
two it was — rather than silently computing against the wrong grid. Under harmonic balance the
spectral axis is `harmonic` (or `mixIndex`) and a small-signal sweep is `ssfreq`; neither is `freq`.

## One point by its value: `value_at` {#value-at}

`value_at(x, v)` is `x` at the frequency `v`. When `v` is a grid point it is read straight off; between
two points it is interpolated linearly. Name another axis as the last argument to read along that one
instead.

```text
measure  G_2G45   = value_at(dB(SP1.S(2,1)), 2.45GHz)      the gain at 2.45 GHz
measure  NF_2G45  = value_at(SP1.NF, 2.45GHz)              the noise figure there
measure  Pout_10  = value_at(dBm(ENV1.P("Iout")), 10, "Pin")   the output power at 10 dBm of drive
```

- **By value, not position.** `at(x, "freq", 19)` names the 20th point, which is a different frequency as
  soon as the sweep changes. `value_at(x, 2.45GHz)` means 2.45 GHz on any grid that reaches it.
- **Every other axis is kept**, as with `at`: over a parametric sweep you get one value per sweep point.
- **Real or complex.** A complex value is interpolated as a complex number, so
  `value_at(SP1.S(1,1), 2.45GHz)` is a reflection coefficient you can take `mag` or `dB` of afterwards.
- **A value outside the axis is refused**, naming the axis's extent. Reading past the data would be a
  number that looks right and is not.

A measurement that comes out a single number shows as one cell of a [Table plot](data-display.html), so a
few `value_at`, `max_over` and `min_over` lines make a readout of the numbers you care about.

## Spec checks: the worst value over a band {#spec}

A specification line asks for the **worst** value of a quantity over a frequency band: "insertion
loss ≤ 1 dB from 0.1 to 3 GHz", "rejection ≥ 20 dB from 6 to 12 GHz". `max_over` and `min_over`
compute exactly that:

```text
measure  IL_worst  = max_over(-dB(SP1.S(2,1)), 0.1GHz, 3GHz)     the largest loss in the passband
measure  RL1_worst = min_over(-dB(SP1.S(1,1)), 0.1GHz, 3GHz)     the smallest return loss
measure  Rej_worst = min_over(-dB(SP1.S(2,1)), 6GHz, 12GHz)      the least rejection in the stopband
measure  IL_margin = 1 - IL_worst                                 positive means the spec is met
```

`max_over(x, lo, hi)` is the largest value of `x` over the points whose frequency lies in
`[lo, hi]`, and `min_over` the smallest. Both ends are **inclusive**, so a band edge that is a grid
point is part of the band.

- **The axis is `freq`** unless you name another as the last argument:
  `max_over(x, -10, 0, "Pin")`. A value with a single axis is reduced over that axis, and a Monte
  Carlo result's values over their `trial` axis (below). Leave the range out to take every point:
  `max_over(x, "Pin")`.
- **Every other axis is kept.** Over a parametric sweep you get one worst case per sweep point, a
  curve you can plot against the swept variable. With no other axis you get a single number.
- **The values must be real.** Complex numbers have no order, so take `dB(…)`, `mag(…)` or
  `real(…)` first. A complex value is refused, saying so.
- **A band with no grid point in it is refused**, naming the axis's extent. An empty band in a spec
  check is almost always a mistyped limit.

The worst value is only as good as the grid. A narrow notch between two frequency points is not
seen, so sweep finely enough to resolve the response near each band edge.

## Statistics over trials {#statistics}

A Monte Carlo or yield run stacks every trial on a `trial` axis. These functions reduce that axis
(or, on a value with none, `freq`, or a single axis's only one; name another as the last argument,
and give `lo, hi` before it to take part of the axis):

| Function | Gives |
|---|---|
| `mean_over(x)`, `median_over(x)` | the mean, the median |
| `std_over(x)` | the sample standard deviation |
| `pctl_over(x, p)` | the p-th percentile, p from 0 to 100 |
| `skew_over(x)`, `kurt_over(x)` | the skewness and the excess kurtosis (0 for a normal distribution) |
| `yield_over(condition)` | the fraction of trials for which the condition holds: `yield_over(Gain > 14)` |
| `cpk(x, lo, hi)` | the process capability index; write either limit as `"none"` for a one-sided spec |
| `sigma_to(x, limit)` | how many standard deviations the limit lies from the mean (positive above it) |

Four build a new axis from a value over the trials:

| Function | Gives |
|---|---|
| `histogram(x, bins)`, `histogram(x, bins, lo, hi)` | counts over a `bin` axis of bin centres; add `"percent"` as the last argument for each bin's percent of the trials |
| `cdf(x)` | the values sorted, against the fraction of trials at or below each |
| `normq(x)` | the values sorted, against the normal quantile of each one's rank — a straight line when the spread is Gaussian |
| `yield_sens(pass, x, bins)` | per bin of `x`, the fraction of trials that pass |

A `measure` line built on `histogram` or `yield_sens` also stores what does not fit in one curve
beside it: `<name>:width` (the bin width) and, for `yield_sens`, `<name>:count` (the trials in each
bin).

- **A trial with no value is skipped.** A trial that did not evaluate has no value, and the
  statistics are over the trials that do. (`max_over` and `min_over` are spec checks and do not
  skip: a missing value in a band makes the worst case missing too.)
- **A comparison between values over the trials is taken trial by trial**, giving 1 or 0 for each,
  so `yield_over(x > 3 && x < 5)` counts the trials inside a window.
- **A goal may use them.** A goal written over the spread of the trials is legal, but each
  evaluation of it is a whole Monte Carlo run.

---

<p class="small">See also: <a href="expressions.html">Expressions</a> (the language) ·
  <a href="components.html#meas">MEAS component</a> · <a href="plot-types.html">Plot types</a>. Full design:
  <code>docs/design/measurements.md</code>.</p>
