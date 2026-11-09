---
title: Derived Metrics
slug: reference/derived-metrics.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Derived Metrics
lede: Stability, passivity, group delay, noise figure and the two-terminal passive readouts — every equation, written out.
keywords: noise figure, NF, NFmin, noise temperature, Te, Sopt, rn, noise circle, ns_circle, ga_circle, gp_circle, stab_circle, stab_side, available gain circle, operating gain circle, ESR, ESL, Q factor, self-resonance, SRF, Ceff, Leff, capacitance, inductance, stability, mu, Rollett, K factor, MAG, MSG, passivity, group delay, sigma max, decoupling, MLCC, fixture, shunt-through
---

A **derived metric** is a quantity the Data Display computes from an S-matrix rather than reading out
of it. Stability factors, gain limits, passivity, group delay, and the impedance readouts a capacitor or
an inductor is actually specified in — none of these is a matrix element, and all of them are offered on
the [trace card](data-display.html#trace-card) beside the elements themselves.

This page exists so that you never have to guess which convention was used. **Every derived metric
below is given as the equation the code evaluates**, in the same symbols, so that a number you disagree
with can be traced to an assumption rather than to a mystery.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#where">Where they live, and what they will plot on</a></li>
<li><a href="#common">What every derived metric assumes</a></li>
<li><a href="#stability">Stability: μ, μ′, K, |Δ|</a></li>
<li><a href="#gain">Maximum gain: MAG and MSG</a></li>
<li><a href="#circles">Stability circles</a></li>
<li><a href="#passivity">Passivity, σ<sub>max</sub></a></li>
<li><a href="#groupdelay">Group delay</a></li>
<li><a href="#noise">Noise figure from any source, and noise circles</a></li>
<li><a href="#passive">The two-terminal passive readouts</a></li>
<li><a href="#fixture">The fixture: the one thing you must tell it</a></li>
<li><a href="#srf">Self-resonance</a></li>
<li><a href="#datavsmodel">Checking data against a model</a></li>
<li><a href="#inside">Stability from inside the circuit</a></li>
<li><a href="#headless">Asking the same questions headless</a></li>
</ol>
</nav>

## Where they live, and what they will plot on {#where}

Add one exactly as you would add a matrix element: select a network source in the trace card's item
selector and pick the derived metric from the list beneath the S-parameter elements.

{{ui: plot-inspector-passive}}

Picking one reveals its own controls — the ordered port pair, and for a passive readout the
**Fixture** row, which is the subject of [its own section below](#fixture).

Two kinds, and they do not mix in one plot:

| Kind | Members | Plots on |
|---|---|---|
| **Scalar versus frequency** | μ, μ′, K, \|Δ\|, MaxGain, passivity, group delay, and all six passive readouts | Rectangular, Table |
| **Γ-plane locus** | Source and load stability circles | Smith, Polar |

Picking one on the wrong plot type is not an error and is not silently ignored — the item is offered but
**disabled, with the reason on it**. See [Plot Types](plot-types.html).

## What every derived metric assumes {#common}

Three assumptions are shared by everything on this page. They are stated once here rather than repeated
under each formula.

**1 — The reference impedance is made uniform and real first.** Every formula below is derived for a
single real Z<sub>0</sub>. A simulated S-matrix may be referenced to a different impedance at each port,
and those may be complex; a Touchstone file is uniform by construction but need not be 50 Ω. So before
any of these is evaluated, the matrix is renormalised — using the full power-wave form, on the whole
matrix, never element by element. The target is `Re(Z0)` of the **input port**, which is why a 75 Ω part
and its Touchstone file give identical answers instead of both being forced to 50 Ω.

**2 — The port pair is ORDERED.** For a 2-port formula the trace card offers an **input** and an
**output** selector, and swapping them is a different selection, not a re-spelling of the same one. μ is
the *load* stability factor and μ′ the *source* one, so (1,2) and (2,1) exchange their meanings. On a
2-port the selectors are hidden — input 1, output 2 is the only sensible reading.

**3 — On an N-port, the other ports are assumed terminated in the reference impedance.** Extracting a
2×2 sub-matrix from a 4-port is valid *only* under that assumption. It is the standard one and it is
almost always what you want, but if you are comparing against a bench measurement that terminated port 3
in something else, that is where the disagreement comes from. The trace card says so in a note under the
port selectors whenever N > 2.

<div class="callout note">
<span class="label">Δ, once, for the whole stability section</span>
<p>Every stability formula below uses the determinant of the 2-port sub-matrix:</p>
<p><code>Δ = S11·S22 − S12·S21</code></p>
</div>

## Stability: μ, μ′, K, |Δ| {#stability}

### μ — load stability factor (Edwards–Sinsky)

```
μ = (1 − |S11|²) / ( |S22 − Δ·S11*| + |S12·S21| )
```

**μ > 1 ⟺ unconditionally stable.** Its great advantage over K is that it is a *single* number with a
direct geometric meaning: μ is the distance from the centre of the Smith chart to the nearest unstable
load. So a larger μ is unambiguously more stable, and two designs can be compared by it. K cannot do
that.

### μ′ — source stability factor

```
μ′ = (1 − |S22|²) / ( |S11 − Δ·S22*| + |S12·S21| )
```

The same measure taken from the source side: the distance to the nearest unstable *source* termination.
Note that μ and μ′ come from **the same trace card control** — selecting μ with the ports swapped gives
μ′, which is why the port pair being ordered matters.

### K — Rollett stability factor

```
K = (1 − |S11|² − |S22|² + |Δ|²) / (2·|S12·S21|)
```

**Unconditional stability requires K > 1 *and* |Δ| < 1** — both, always. K alone is not a stability
criterion, which is the reason |Δ| is offered as its own trace rather than folded into K's. Plot them
together.

**And both together are still a two-port test.** A two-port that passes K and |Δ| can still oscillate,
because these are computed from the *reduced* two-port and a pole the reduction hides is a pole they
cannot see — see [the WSProbe page's NDF example](wsprobe.html#ndf).

### |Δ|

The magnitude of the determinant above, plotted on its own so that the K > 1 test can be completed.

## Maximum gain: MAG and MSG {#gain}

One trace, one formula, and which of the two classical quantities you get depends on K at that
frequency:

```
                |S21|
ratio     =    -------
                |S12|

K ≥ 1  (MAG)   G = ratio · ( K − √(K² − 1) )      maximum available gain
K < 1  (MSG)   G = ratio                          maximum stable gain
```

The transition is exactly at K = 1 and it is continuous there, so a single curve runs across the
potentially-unstable region into the unconditionally-stable one without a discontinuity — which is what
makes the plot readable in the first place.

The card offers this in **dB** (`10·log10 G`) or as a **linear power ratio**. The two come from the same
implementation rather than the display taking a logarithm of the other, so they cannot disagree.

## Stability circles {#circles}

Loci in the Γ plane, one circle per frequency. The **load** circle is the set of Γ<sub>L</sub> that put
|Γ<sub>in</sub>| = 1; the **source** circle is the set of Γ<sub>S</sub> that put |Γ<sub>out</sub>| = 1.

```
Load    CL = (S22 − Δ·S11*)* / (|S22|² − |Δ|²)          rL = |S12·S21| / | |S22|² − |Δ|² |

Source  CS = (S11 − Δ·S22*)* / (|S11|² − |Δ|²)          rS = |S12·S21| / | |S11|² − |Δ|² |
```

circuitRF also decides, per frequency, whether the stable region is **inside or outside** the circle and
renders it accordingly — that is not a cosmetic choice, and reading a circle without it is how a
"stable" design gets built around the unstable half of the chart.

## Passivity, σ<sub>max</sub> {#passivity}

```
σmax = the largest singular value of S
```

The network is passive at that frequency **iff σ<sub>max</sub> ≤ 1**. The value says *how far* from
passive, not merely whether: 1 is the boundary, 1.05 is 5 % of excess gain, and a well-behaved passive
file sits just below 1 everywhere.

Two things make this different from everything else on this page:

- **It is not a 2-port formula.** It is defined for any N ≥ 1 (at N = 1 it is just |S<sub>11</sub>|), so
  it needs no port pair.
- **Scope is a choice above 2 ports.** The card offers **whole network** (the default) or the extracted
  input/output pair. They answer different questions, and a sub-matrix can test passive while the full
  network is not — so if you are checking a de-embedding or an EM result for physicality, you want the
  whole network.

## Group delay {#groupdelay}

```
τ(ω) = −dφ/dω        φ = the UNWRAPPED phase of S21 (input → output)
```

Plotted in **nanoseconds**. Three details that decide whether the number is meaningful:

- **The phase is unwrapped first.** A raw `atan2` phase is a principal value in (−π, π], so any network
  whose transmission runs through more than half a cycle wraps somewhere inside its own passband. A
  difference taken across that wrap is a spike one grid-step wide that *moves when you change the grid*.
- **The derivative is a central difference on the sweep's own grid** (one-sided at the two ends), so the
  result has exactly as many points as the sweep. The grid need not be uniform.
- **The sweep must resolve the phase to better than half a cycle per step.** That is the one real limit:
  a sweep too coarse for that cannot have its group delay measured by any method, and unwrapping will
  quietly guess wrong rather than fail.

## Noise figure from any source, and noise circles {#noise}

An S-parameter run with [`Noise=yes`](simulations.html#sp-noise) reports the noise waves `Cs`, and for a
two-port its noise figure and noise parameters. Four measurement functions read them for any source and,
on more than two ports, for any output port:

| Function | Returns |
|---|---|
| `nf(SP1.S[, Γs][, port])` | The noise figure in dB from port 1 to `port` (2 by default), per frequency. With no Γs the source is port 1's own Z0, so `nf(SP1.S)` is the run's `NF`. Γs is a number, such as `0` or `polar(0.3, 45)`, or one value per frequency, such as `SP1.Sopt`. A port comes after a Γs: `nf(SP1.S, 0, 3)`. |
| `te(SP1.S[, Γs][, port])` | The same as an equivalent input noise temperature, T0·(F − 1), in K. `te(SP1.S)` is the run's `Te`. |
| `rn(SP1.S)` | Rn normalised to R, Rn/R, per frequency: a Touchstone noise row's rn. A two-port's. |
| `ns_circle(SP1.S, NFdB[, npts])` | The Γs that give NFdB on a two-port run, as `npts` points (101 by default) round a closed circle, per frequency: a complex `{freq, point}` cube. NFdB is a number, or one value per frequency, such as `SP1.NFmin + 0.5`. |
| `ns_circle(NFdB, NFmin, Sopt, rn[, npts])` | The same circle from noise parameters alone: NFmin in dB, Sopt and rn = Rn/R. Each is a number, or a cube with one value per frequency, so a circle can be drawn from a datasheet's numbers or from cubes that are not a noise run. The cubes' axes lead the result. |

```
F(Γs) = Fmin + 4·rn·|Γs − Sopt|² / ((1 − |Γs|²)·|1 + Sopt|²)          NF = 10·log10 F,   Te = T0·(F − 1)
F     = 1 + Cs[k,k] / (k·T0·|S[k,1]|²)                                with no Γs, k the output port

N      = (F − Fmin)·|1 + Sopt|² / (4·rn)                              rn = Rn / R
centre = Sopt / (1 + N)                      radius = √(N² + N·(1 − |Sopt|²)) / (1 + N)
```

R is Re Z0 of port 1, which is what Sopt and Γs are referenced to, and T0 is 290 K. These follow from
the formulas: `nf(SP1.S)` and `te(SP1.S)` are the run's `NF` and `Te`, and so is `nf(SP1.S, 0)` when
port 1's Z0 is real; `nf(SP1.S, SP1.Sopt)` is its `NFmin`; every point of `ns_circle(SP1.S, x)` gives `x`
back through `nf`; and `ns_circle(x, SP1.NFmin, SP1.Sopt, rn(SP1.S))` is `ns_circle(SP1.S, x)`.

**More than two ports.** With port 1 as the source and port k as the output, every other port stays
terminated in its own noiseless Z0. The two-port that leaves has the {1, k} rows and columns of the
run's S and Cs, so `nf(SP1.S, Γs, k)` and `te(SP1.S, Γs, k)` are its noise figure and temperature. A
splitter's noise figure to each output is `nf(SP1.S, 0, 2)` and `nf(SP1.S, 0, 3)`.

Where no answer exists, the value is NaN rather than a plausible number:

- `nf` and `te` at |Γs| ≥ 1, because no passive source presents that reflection;
- `ns_circle` below a frequency's NFmin, where there is no such circle.

The run forms need the analysis's own S cube, as the stability metrics do. They are refused, with the
reason, on a run made without `Noise=yes` (`measure.noise.not-run`), for `rn` and `ns_circle` on a
network that is not a two-port (`measure.noise.not-two-port`), and for an output port outside 2 to N
(`measure.noise.port`). The two forms of `ns_circle` are told apart by their number of arguments, and
the first argument has to fit: a call that is neither is refused (`measure.noise.circle-form`), and so
is a noise parameter of the wrong kind or length (`measure.noise.parameter`).

**To draw the circles on a Smith chart,** make the circle a measurement and plot it with `point` as X:

```
measure NF2 = ns_circle(SP1.S, 2)
```

The trace spec `NF2[~, :]` draws one circle per frequency, and `NF2[0, :]` draws the first one only.

A circle can also be a trace's expression itself, with no measurement: `ns_circle(SP1.S, SP1.NFmin + 0.5)` on a
Smith chart. The trace card then shows a **Freq** picker, and the trace draws the circle at the frequency it names.
`SP1.Sopt` with a Freq picked is the one point at that frequency. The
[Noise and gain circles](#lna-circles) chart is built this way.

## Gain and stability circles {#gain-circles}

An LNA's input match trades noise figure against available gain and stability, all on the source plane. Four
functions draw the other two beside `ns_circle`. Each takes a two-port run's S cube. A source-plane circle is on
port 1's Re Z0, the reference `Sopt` is on, and a load-plane circle on port 2's.

| Function | Returns |
|---|---|
| `ga_circle(SP1.S, GdB[, npts])` | The Γs that give an available gain of GdB, the load conjugately matched: a `{freq, point}` cube, as `ns_circle`'s. GdB is a number, or one value per frequency, such as `max_gain(SP1.S) - 1`. |
| `gp_circle(SP1.S, GdB[, npts])` | The Γ<sub>L</sub> that give an operating (power) gain of GdB, the source conjugately matched. |
| `stab_circle(SP1.S, plane[, npts])` | The [stability circle](#circles) on `plane`, `"source"` or `"load"`, as points. |
| `stab_side(SP1.S, plane)` | Per frequency, `1` when the inside of that plane's circle is the stable region and `-1` when the outside is. |

```
g = G / |S21|²        C1 = S11 − Δ·S22*        D = 1 + g·(|S11|² − |Δ|²)
centre = g·C1* / D                           radius = √(1 − 2K·|S12·S21|·g + |S12·S21|²·g²) / |D|
```

That is the available-gain circle; the operating-gain circle is the same with ports 1 and 2 exchanged. Every point of
`ga_circle(SP1.S, x)` has an available gain of x, every point of a source `stab_circle` has |Γout| = 1, and every
point of a load one |Γin| = 1.

A gain above `max_gain` has no circle at that frequency. Its points are NaN, and the run says once which frequencies
had none (`measure.circle.none`). A network that is not a two-port is refused naming its shape
(`measure.circle.not-two-port`). On a Smith chart, a `stab_circle` trace shades its unstable side inside the chart.

### The Noise and gain circles chart {#lna-circles}

One Smith chart of the source plane shows the whole trade: `Sopt` as a marker, three noise circles at NFmin + 0.25,
0.5 and 1 dB, three available-gain circles at max_gain − 1, 2 and 3 dB, and the source stability circle with its
unstable side shaded. It is drawn at one frequency, the middle of the sweep, which each trace's Freq picker changes.
Every circle is an ordinary expression trace, so any of them can be edited afterwards.

It opens in one action on a two-port S-parameter result run with `Noise=yes`:

- in a Data Display, the **Add Noise and Gain Circles** button on the toolbar, with that result as the source;
- in the Workspace, **Open Noise and Gain Circles** on the result's `.npy`, which opens a new Data Display holding it.

## The two-terminal passive readouts {#passive}

Six quantities, and they exist for a specific reason: **a capacitor, an inductor or a ferrite bead is
specified, bought and compared in ESR, C, L, Q and self-resonance — never in S-parameters.** When a
vendor ships you both an S-parameter file and a model, the question you actually want to ask is whether
the two agree, and answering it from S<sub>11</sub> in dB is possible but nobody does it.

All six are point functions of one impedance `Z(f) = R + jX`, extracted from the S-matrix by the
[fixture](#fixture) you select. With `ω = 2πf`:

| Trace | Equation | Unit | Defined where |
|---|---|---|---|
| **\|Z\|** | `\|Z\|` | Ω | everywhere |
| **ESR** | `R = Re Z` | Ω | everywhere |
| **Reactance X** | `X = Im Z` | Ω | everywhere |
| **C effective** | `C = −1 / (ω·X)` | F | `X < 0` only — the part is capacitive |
| **L effective** | `L = X / ω` | H | `X > 0` only — the part is inductive |
| **Q** | `Q = \|X\| / R` | — | `R > 0` only |

<div class="callout note">
<span class="label">Where a quantity has no value, the trace ends</span>
<p>C<sub>eff</sub> above the self-resonance and L<sub>eff</sub> below it are the routine cases: the part
is not a capacitor up there and asking for its capacitance has no answer. Those points are
<strong>dropped</strong> — not clamped to zero, not folded to a positive value, not plotted negative. A
folded-back capacitance on a log axis reads as data, which is the failure worth avoiding. The same rule
gives Q no value where R ≤ 0, which is what a de-embedding that has gone wrong looks like.</p>
</div>

The traces are left in **base SI** — ohms, farads, henries. Unlike group delay, where nanoseconds are
universal, the capacitors in one plot can run from picofarads to millifarads and there is no single
scale that would not be wrong for half of them. The unit is on the trace's own label.

## The fixture: the one thing you must tell it {#fixture}

**Nothing in a Touchstone file records how the part was measured**, and the readings differ by orders of
magnitude. So the trace card carries a **Fixture** selector, and it is an explicit choice rather than
something circuitRF tries to detect.

| Fixture | The DUT is | Equation |
|---|---|---|
| **Shunt-through** *(default)* | a shunt element bridging two through lines | `Z = (Z0/2)·S21/(1 − S21)` |
| **Series-through** | in series with the through line | `Z = 2·Z0·(1 − S21)/S21` |
| **1-port** | the only thing at the port | `Z = Z0·(1 + S11)/(1 − S11)` |

**Shunt-through is the default because it is how vendor decoupling-capacitor and inductor files are
measured.** It is the fixture with usable dynamic range on a part that is milliohms at resonance: there,
S<sub>11</sub> sits within a part in 10⁴ of −1, where a network analyser's directivity and calibration
residuals dominate, while the same impedance appears in S<sub>21</sub> as a well-conditioned
transmission measurement. A file with only one port has no S<sub>21</sub> to read and is always taken as
a 1-port, whatever the selector remembers.

<div class="callout warning">
<span class="label">The wrong fixture is smooth, finite and plausible</span>
<p>Reading a shunt-through file as a 1-port returns <strong>exactly <code>Z ∥ Z0</code></strong>. Work
through what that means: the misread is close to right wherever |Z| ≪ Z<sub>0</sub> — which for a bulk
decoupling capacitor is most of its band — and <strong>saturates at Z<sub>0</sub></strong> everywhere
else. A 10 pF capacitor, an inductor or a bead comes back as ≈ 50 Ω, and its capacitance with it. There
is no NaN, no discontinuity and nothing on the plot to notice. That is why the fixture is a control you
set rather than a guess circuitRF makes on your behalf.</p>
</div>

## Self-resonance {#srf}

The self-resonant frequency is reported on the trace card whenever a passive readout is selected, and it
is defined as:

> the **lowest** frequency at which the reactance crosses zero going **capacitive → inductive**,
> interpolated in log *f*.

The direction is the whole definition and an unsigned zero-crossing search is wrong. A real part has
several crossings: the *series* resonance where |Z| is a **minimum** — the number everyone means by
"SRF" — and above it the mounting or package *parallel* resonance where |Z| is a **maximum** and the
reactance crosses back the other way. The sign of the crossing is what separates them.

If the sweep contains no such crossing, the card reports **nothing** rather than a band edge. "This
sweep does not contain the resonance" is the honest answer; a clamped band edge would be read as a
measurement.

Interpolation is in log *f* because these sweeps are logarithmic, and interpolating linearly across a
decade-wide gap places the crossing visibly wrong.

## Checking data against a model {#datavsmodel}

The workflow these readouts were built for:

1. Add the vendor's `.sNp` as one trace source, and a schematic built from the vendor's model as
   another. A Data Display [reads files, not runs](data-display.html#free-floating), so both live in one
   plot without either knowing about the other.
2. Add the same readout — **ESR**, or **C effective** — from each, on one rectangular plot with a log
   frequency axis.
3. Set the same **Fixture** on both.

Where the two curves separate is where the model stops being the part. In practice that is almost always
*above* the band the model was fitted over, and almost always in ESR before it shows in |Z| — the
reactance dominates |Z| everywhere except within a whisker of resonance, so a model with the loss wrong
by a factor of three still overlays convincingly on a |Z| plot. **Compare ESR, not |Z|.**

## Stability from inside the circuit {#inside}

Everything on this page is computed from an S-matrix — a *terminal* view of a network. That is the
right view for a part you are buying and the wrong one for a node inside an amplifier you are
drawing, where the question is what the two halves of that node present to each other.

**[The WSProbe](wsprobe.html)** answers that one. It is a component you drop into a node; it perturbs
nothing and costs no extra sweep, and what it produces is the driving-point impedance and admittance
at that node, the impedance looking each way out of it, every classical loop gain, and a bounded
[stability margin](wsprobe.html#stability-margin) that says how close the node is rather than only
whether it is stable. It also brings the [normalized determinant function](wsprobe.html#ndf), which
is the rigorous N-port answer that K and |Δ| are an approximation to.

The two pages meet in both directions: a probe's reduced two-port is an ordinary network source, so
every metric above — μ, K, |Δ|, MAG/MSG and the stability circles — applies to a stage measured
*inside* the circuit, loaded by what actually loads it.

## Asking the same questions headless {#headless}

Two CLI verbs answer without opening a plot. Both are read-only.

**Is this file sound?**

```
circuitrf check part.s2p
```

Reports the port count, the sweep, the reference impedance, and then: a frequency axis that is not
strictly increasing (an **error** — every interpolator here assumes a sorted axis and returns a
plausible wrong number on one that is not), a reference impedance no renormalisation can use, a
reference that is **not 50 Ω** (worth knowing, because it is carried silently into every comparison),
σ<sub>max</sub> > 1, a reciprocity error, and a causality measurement.

Passivity and reciprocity are reported as **warnings, never errors**, and they carry the number and the
frequency. Nothing in a Touchstone file says what the part is: an amplifier is supposed to have gain and
a circulator is supposed to be non-reciprocal. For a capacitor, either one is a defect. Only you know
which you have.

<div class="callout note">
<span class="label">What the causality measurement can and cannot tell you</span>
<p>circuitRF transforms the file to an impulse response and measures what fraction of its energy lands
before <em>t</em> = 0. It runs <strong>only on a uniform frequency grid that reaches DC in one step</strong>
— a log sweep, which is what most vendor passive files are, is reported as <em>not evaluated</em> with
the reason, because resampling it would need an interpolation that is itself a low-pass and would
manufacture the smoothness being tested for.</p>
<p>When it does run and the number is high, <strong>two different things produce that and the
measurement cannot separate them</strong>: the file may genuinely not be causal (a model fitted without a
causality constraint is the usual cause), or the sweep may be too coarse to resolve its own response. A
ferrite bead sampled on a grid too coarse for its impedance corner measures much like a delay line
running backwards. Both matter if you intend to use the file in the time domain, so the finding is worth
making — which is why it is worded as a measurement with two readings and never as a verdict.</p>
</div>

**What is this part?**

```
circuitrf explain part.s2p
```

Reports the ports, the sweep, the reference impedance, and then — under **every applicable fixture, side
by side** — the self-resonance and the impedance floor with the ESR there. Reporting all of them is not
indecision: it is the fastest way to identify an unlabelled file, because only the physical reading
gives a sensible resonance and a milliohm-scale floor, and the others are obviously nonsense next to it.

```
part.s2p  (touchstone)
  ports        2
  sweep        401 points, 1 MHz to 1 GHz, non-uniform
  reference impedance 50 Ω
  SRF (shunt-through)     22.507906 MHz
  |Z| min (shunt-through) |Z| = 5.057 mΩ at 22.387211 MHz, ESR 5 mΩ
  SRF (series-through)    (nothing)
  |Z| min (series-through) |Z| = 796.177 Ω at 1 GHz, ESR 1.268 Ω
```

Both verbs take `--json`, and every finding carries a stable diagnostic id. See
[the CLI reference](cli.html).
