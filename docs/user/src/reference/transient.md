---
title: Transient
slug: reference/transient.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Transient
lede: A transient analysis solves the circuit in time, from a starting state, with every source playing its waveform. It answers what happens on the way to a steady state, or when there is none: a pulse's edge, a converter's start-up, a reflection. This page covers when to use it instead of harmonic balance or the envelope, setting it up, reading its results, running it on ngspice, and starting harmonic balance from it.
keywords: transient, time domain, tran, step response, pulse, rise time, overshoot, settling, initial conditions, IC, switching, buck converter, ngspice, SPICE, deck, transient seed, InitialGuess, fast bias, time-domain measurement
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#when">When to use it, and when not</a></li>
      <li><a href="#directive">The analysis</a></li>
      <li><a href="#sources">Sources and the switch</a></li>
      <li><a href="#initial">The starting state and initial conditions</a></li>
      <li><a href="#grid">The output grid and the step</a></li>
      <li><a href="#elements">What runs in time</a></li>
      <li><a href="#results">Results, measurements and the Transient preset</a></li>
      <li><a href="#steering">When circuitRF suggests another analysis</a></li>
      <li><a href="#ngspice">ngspice</a></li>
      <li><a href="#seed">Starting harmonic balance from a transient run</a></li>
      <li><a href="#headless">From the command line</a></li>
      <li><a href="#examples">Examples</a></li>
    </ol>
  </nav>

## When to use it, and when not {#when}

circuitRF is a harmonic-balance simulator first. Transient sits beside it for the questions harmonic balance and the
envelope do not answer:

- **Edges and pulses**: rise time, overshoot, ringing and settling of a filter or an amplifier driven by a step or a
  pulse train.
- **Reflections**: a pulse travelling a line and coming back from a mismatch.
- **Start-up and settling**: a supply, a bias network or a switching converter from power-on.
- **Circuits with no periodic steady state**, or whose steady state is the thing in question: a converter's ripple, a
  relaxation oscillator.
- **A start for harmonic balance** on circuits it struggles to start from DC: see [Starting harmonic balance from a
  transient run](#seed).

It is the wrong tool for a steady-state RF question. A 1 GHz amplifier's output power, gain, efficiency and harmonics
are a [harmonic-balance](simulations.html#harmonic-balance) run of a fraction of a second; a transient run has to step
through every carrier period until the bias network settles, which can be millions of them. A modulated signal (ACLR,
EVM, AM-AM) is the [envelope](simulations.html#envelope)'s question: it follows the modulation without resolving the
carrier. circuitRF says so when a bench asks transient for that kind of answer ([below](#steering)).

## The analysis {#directive}

Add a **Transient** analysis to a test bench from **Simulate ▸ Setup Analyses**. In a netlist it is one line:

```
analysis TR1 type=tran Stop=10n Step=5p
analysis TR2 type=tran Stop=200u Step=10n InitialState=zero Method=gear2 Solver=ngspice
```

{{ui: transient-analysis-card}}

The card shows `Stop`, `Step`, `Method`, `InitialState` and `Solver`; the rest are under **More**. Every value can be an
expression.

| Key | Default | |
|---|---|---|
| `Stop` | required | End time (s) |
| `Step` | `Stop`/1000 | The output grid's spacing (s), and the largest step unless `MaxStep` says otherwise |
| `Start` | 0 | The first reported time; the run still starts at 0 |
| `MaxStep` | auto | The largest step: the smaller of `Step` and 1/(20 f) for the highest frequency any source carries |
| `Method` | `trap` | `trap` (trapezoidal: second order, no numerical damping) or `gear2` (second-order backward difference: damps ringing). Either takes two backward-Euler steps at the start and after every corner of a source |
| `RelTol`, `VTol`, `ITol` | 1e-3, 1e-6 V, 1e-12 A | Tolerances on each Newton update and each step's error estimate |
| `InitialState` | `op` | `op`: start from the DC operating point. `zero`: start from zero, with no DC solve ([below](#initial)) |
| `IC` | | Net voltages held during the operating-point solve: `IC="out=5.5, bias=1.2"` |
| `Solver` | `native` | `native`, circuitRF's own engine, or `ngspice` ([below](#ngspice)) |
| `SaveSteps` | `no` | `yes` adds the engine's own accepted steps (`Steps.time`, `Steps.V`) beside the output grid |
| `MaxSteps` | 10,000,000 | The run is refused before it starts when `Stop`/`MaxStep` exceeds it |
| `SpiceOptions`, `SpiceLines`, `SpiceLineFreq`, `SpiceFit` | | ngspice only ([below](#ngspice)), enabled on the card only with that solver |

`trap` is the default because it does not damp: a lossless resonator rings for as long as it should. If a run shows a
small oscillation that flips sign every step after an abrupt edge, that is trapezoidal ringing, and `gear2` removes it.

## Sources and the switch {#sources}

The [waveform sources](components.html#waveform-sources) exist for transient, one type per waveform, each with a voltage
(`V_`) and a current (`I_`) form: [`V_Pulse`](components.html#vpulse) (a pulse train, or a step when `Width` is blank),
[`V_PWL`](components.html#vpwl) (piecewise linear, from points or a file), [`V_Exp`](components.html#vexp),
[`V_SFFM`](components.html#vsffm) (a frequency-modulated sine) and [`V_Expr`](components.html#vexpr) (any expression in
`time`). In every other analysis each is a constant, its value at t = 0. [`Idc`](components.html#idc) is the DC current
source.

Every corner of a waveform (a pulse's edges, a PWL vertex) is a time the engine lands a step on exactly, so an edge is
never smeared across a step.

The tone sources run in transient as sines: [`V_1Tone`](components.html#tonesource), `I_1Tone`, `P1Tone` and `PnTone`,
with three keys only a transient reads: `Delay` (the tone starts then), `Ramp` (it rises over this time along a raised
cosine) and `Damping` (it decays after `Delay`). A `P1Tone` keeps its available power and source impedance, and its
harmonic terminations `Z[k]` are presented at their harmonics.

The [voltage-controlled switch, `VSwitch`](components.html#vswitch), is a resistance between `Ron` and `Roff` set by a
control voltage, with a smooth transition between `Voff` and `Von`, so it works in every analysis. Transient adds
`Hyst` (hysteresis) and `TransitionPoints`: the step is held short while the control crosses, at least eight steps per
crossing by default.

## The starting state and initial conditions {#initial}

With `InitialState=op` (the default) the run starts from the DC operating point, with every source at its t = 0 value.
With `InitialState=zero` every voltage and current starts at zero and nothing is solved first: the supplies switch on at
t = 0, which is how a start-up from power-on is simulated.

Initial conditions set the stored energy the run starts with:

- **On the analysis**, `IC="out=5.518"` holds net `out` at 5.518 V while the operating point is solved, and releases it
  at t = 0.
- **On a part**, a capacitor's `IC` is its starting voltage and an inductor's `IC` its starting current.

Both apply under either `InitialState`. Their use is to skip a start-up when only the steady state is wanted: the [Buck
Converter](../examples/buck-converter/index.html) example reads the output voltage and the inductor current at the start
of a switching cycle from a run from rest, and starts a second bench there, steady from its first cycle, in 10 µs
instead of 200 µs. Setting one of the two alone does not work: the energy left out rings through the filter again.

## The output grid and the step {#grid}

The engine takes steps of its own size, short where the circuit moves fast, long where it does not, and reports every
waveform on a uniform grid from `Start` to `Stop` at `Step`, interpolated within each accepted step at the integration's
own order. The grid sets what you see; the tolerances set how accurate it is. `MaxStep` caps the step, and by default
follows the fastest source, so a 1 GHz tone gets at least 20 steps a period. `SaveSteps=yes` keeps the engine's own
steps as well, to see where it worked hardest.

The result carries the run's counters, `Tran.AcceptedSteps`, `Tran.RejectedSteps`, `Tran.NewtonIterations` and
`Tran.Factorizations`: a run with many rejected steps is a circuit that is hard to step, not a slow computer.

## What runs in time {#elements}

| Elements | In a transient run |
|---|---|
| Resistors, capacitors, inductors, mutual inductance, the transformer, controlled sources, ports and terms | Exact |
| Diodes, BJTs, the FET laws, MOS models, SDDs, Verilog-A devices, nonlinear capacitors, composed FETs | As they are: every device evaluates exactly as in harmonic balance |
| A lossless line, a lossless coupled pair | Exact, as delays. The run's message gives the delay and the error against the line's model |
| A lossy microstrip, stripline, coplanar or ideal line | Its delay and a rational fit of the rest, checked against the model up to the run's highest frequency, 1/(2 `MaxStep`). A line that cannot be fitted within 1e-3 of \|S21\| there is refused by name with its error |
| SnP blocks, filters, Z(freq) blocks, tapers, EM cells | A rational fit from DC to the run's highest frequency, made passive if it is not, its error and any correction reported. Data that stops short of DC is extrapolated and the run says so |
| `MOD_Source` | Its signal on its carrier, over a window of its record ([Modulated sources](#modulated)) |
| Lossy coupled lines, mixers and system amplifiers, the Phased Antenna Array, the Propagation Channel, tuners, the WSProbe, the EM Field Data block | Refused by name, each with the analysis that does run it |

A term or port is a load of its `Z` in transient, as in harmonic balance.

## Results, measurements and the Transient preset {#results}

Every labelled net's voltage and every IProbe's current are recorded with nothing to set: `V[time, node]`,
`I[time, branch]`, the instantaneous power `P` through each IProbe, `Vprobe` (the voltage at its first net) and `OP.V`,
the starting state.

The [time-domain functions](measurements.html#time-domain) read them in `measure` lines: `rise`, `fall`, `overshoot`,
`undershoot`, `settle`, `period`, `frequency`, `value_at`, `tmin`, `tmax`, `avg`, `rms`, `fft`, `fft_power` and `thd`.
`fft` of the last period is shaped like a harmonic-balance spectrum, so a trace written for one plots the other.

When a run ends, the Messages panel offers **Open Transient plots**, and a Data Display's **Add Transient** button (the
sine-wave icon) does the same for its source: the recorded voltages against time, the output's spectrum over its last
period (the power through the IProbe whose name says "out", or the output net's voltage), and the bench's measures as a
table. Every trace is an ordinary trace to re-point afterwards.

{{ui: transient-filter-step}}

{{ui: transient-line-reflection}}

{{ui: transient-buck-startup}}

## Modulated sources {#modulated}

A [MOD_Source](components.html#modsource) plays its signal in a native transient run, carrier-resolved: its LTE, NR,
QAM, FSK, chirp or imported I/Q record on its carrier `Fc`, behind its `Z`. Use it where the [envelope
analysis](simulations.html#envelope) assumes too much: a switching or saturating stage driven by a real modulated
signal, a modulated signal beside an unrelated tone, a start-up while the modulation runs, or a check of an envelope result
on the same bench. Otherwise the envelope analysis answers a modulated question in a fraction of the time.

A run plays a **window** of the record, since every carrier cycle is stepped: 10 µs of a 2.4 GHz carrier is about half
a million steps, a whole 1 ms record fifty times that. Two keys on the source pick the window:

| Key on the MOD_Source | Default | |
|---|---|---|
| `RecordStart` | `0` | Where in the record the run starts (s). The record repeats, so any time is a place in it |
| `Ramp` | `0` | A raised-cosine rise of the drive over this time (s), so the circuit is not hit with the signal at full power |

Every other analysis ignores both. ngspice does not play a MOD_Source.

What the run keeps is the tran card's **Store**:

| Key on the tran directive | Default | |
|---|---|---|
| `Store` | `auto` | `baseband`: the envelopes about `Fc`, at the signal's own sample rate. `rf`: every output point of the waveform. `auto` is `baseband` when the bench has a MOD_Source |
| `StoreHarm` | `3` | Baseband: the harmonics of `Fc` kept, 0 to this |
| `MeasureFrom` | `auto` | Baseband: where the measurements start (s); `auto` is after the largest `Ramp` |

**Baseband** results are the [envelope analysis's](simulations.html#envelope) own: `V[time, node, harmonic]`,
`I[time, branch, harmonic]` and, at every IProbe, the power, the spectrum (`PSD`), `ACLR`, `EVM`, the constellation,
`AM-AM` and the gain, measured by the same code. A plot or `measure` line written for an envelope run reads a
modulated transient unchanged. The run downconverts as it steps and never holds the waveform, so memory stays small however
long the window. EVM needs whole symbols: an OFDM symbol is about 70 µs, so a window shorter than that reports no EVM, and
says so.

**RF** keeps the waveform itself, at an output step of 1/20 of the carrier's period unless `Step` says otherwise, and
the card warns how many points that is. Read it with the [time-domain functions](measurements.html#time-domain), as any
transient result.

All sources in a baseband run share one `Fc` and one `RecordStart`; a bench whose sources differ is refused and names
`Store=rf`.

## When circuitRF suggests another analysis {#steering}

`circuitrf explain --analysis` on a transient bench reports, without running it, the solver, the step bound and why,
roughly how many steps the run will take, and the slowest time constant against `Stop`:

```
TR1              tran               TR1                          DISPATCHED by tran
  Solver native
  Step bound 5E-12 s (the output Step): about 1.2E+04 steps over Stop = 6E-08 s at that bound
  Slowest time constant 9.63E-10 s (the network at node 'g'): Stop is 62.3 of them
```

When `Stop` spans more than 10<sup>5</sup> periods of the highest carrier a source plays, it adds a steering line:

```
  Consider another analysis: this bench is 1·10⁶ periods of a 1 GHz carrier: harmonic balance answers a
  steady-state question in a fraction of the time.
```

It names the envelope instead when the source is modulated. It is advice, not a refusal: the run still goes, but a bench
that simulates a million carrier periods to read one steady state is asking transient a harmonic-balance question. A
slowest time constant far longer than `Stop` means the result has not settled.

## ngspice {#ngspice}

ngspice is an open-source SPICE simulator circuitRF can run as a second transient solver. circuitRF writes the bench as a
SPICE deck, runs ngspice on it as a separate program, and reads its waveforms back into an ordinary result: the same
cubes, the same measures, the same plots. It is useful as a check on the native engine, and for a circuit someone already
trusts in ngspice.

**The set of benches ngspice can run is smaller than the set the native engine runs**, on purpose. Every element is
translated exactly or refused by name, never approximated without being asked. A bench that uses a microstrip line, an
S-parameter block or a FET law runs natively but not on ngspice unless one of the two approximations below is asked for.

**Installing.** **Settings ▸ Solvers** lists ngspice under **Circuit**, below the EM solvers. **Install ngspice…** fetches
upstream's own build, or run `circuitrf solver install ngspice`. An ngspice from a package manager, on `PATH` or named by
`CIRCUITRF_NGSPICE` is found with nothing to set. On Windows the program is ngspice's console build, `ngspice_con.exe`.
Details per platform are on the [Settings](settings.html#em3d) page.

**Choosing it.** On the transient card, **Solver ▸ ngspice**. The choice is enabled only while ngspice is found, labelled
with its version; a bench that already says ngspice, opened where there is none, shows the choice greyed with a note. Its
run is refused with the way to install one. It never falls back to the native engine.

**What translates exactly.** Resistors, capacitors and inductors (with `IC`), mutual inductance, the transformer,
controlled sources, IProbes, ports and terms, every DC, waveform and tone source, `V_Expr`/`I_Expr`, lossless lines, the
`VSwitch`, SDDs, a placed SPICE subcircuit (its file included as it is), Verilog-A devices (ngspice loads the same
compiled `.osdi`), and the diode, BJT and MOS level 1 and 3 models where every parameter maps one to one.
Discontinuities whose model is a fixed lumped network (a gap, a bend, an arm-less tee or cross) translate too.

**What is refused, and why.** A device parameter with no counterpart in ngspice's model of the same name, a device away
from its nominal temperature (the two temperature models differ), a MOS with `Tox` (its gate charge model differs), a
pulse with a zero rise, fall or width (ngspice reads zero as its own default), a `P1Tone` with harmonic terminations
`Z[k]`. Lossy lines, S-parameter blocks, filters, EM
cells and frequency-dependent discontinuities have no exact counterpart. The FET laws and composed FETs, system blocks,
tuners and probes other than the IProbe have none either. Every refused element is listed at once, with its reason;
nothing is run. `circuitrf check` lists the same refusals before a run.

**`SpiceLines=rlgc`.** ngspice has lossy lines described by per-unit-length R, L, G and C (`TXL`, and `CPL` for a coupled
pair) but none described by geometry and substrate. With `SpiceLines=rlgc`, each line is written as one of those, with
R, L, G and C taken from circuitRF's model at `SpiceLineFreq` (by default the highest frequency a source carries). It
matches circuitRF's line exactly at that frequency and drifts away from it elsewhere, because constant per-unit-length
values cannot follow dispersion or a skin-effect loss. The run says how far, one message per line: the largest |ΔS|
between the ngspice line and circuitRF's model over the band the run resolves, and the frequency it is at. |ΔS| is an
absolute difference of S-parameters; 1e-3 is negligible for most purposes, 1e-2 is visible in a reflection, and above
that the ngspice result is a different line. `circuitrf check` reports the same number before a run. A coupled pair whose
two modes lose differently is still refused, because `CPL` cannot represent it.

**`SpiceFit=yes`.** Writes each frequency-defined element (an SnP, a filter, an EM cell, a frequency-dependent
discontinuity) as a subcircuit of the rational fit the native engine runs, and each lossy line as a lossless line plus
the fit of its loss. Each carries a message saying ngspice ran circuitRF's fit of it, not the element itself. Where both
apply to a line, `SpiceLines=rlgc` wins.

**The deck.** Each ngspice run leaves the deck it ran, with a map of names, in `results/ngspice/<analysis>-<time>-<id>/`,
and its message gives the path. It is an ordinary deck, every line ending with the circuitRF part it came from:
`ngspice -b deck.cir` runs it by hand. `circuitrf netlist <bench> -o deck.cir` writes it without running anything, and
`SpiceOptions` adds an `.options` line of your own. ngspice's own messages appear in the Messages panel, prefixed with
its name and version.

The [Pulse Response](../examples/pulse-response/index.html) example draws two benches twice, once per solver, and
the two agree to the fourth digit.

## Starting harmonic balance from a transient run {#seed}

Harmonic balance normally starts Newton from the DC point and steps the drive up. Some circuits are far from there: a
switching power amplifier whose devices only switch at full drive, a hard-driven mixer, a frequency divider whose
divided solution cannot be reached by turning the drive up gradually. `InitialGuess` on the harmonic-balance and
oscillator cards picks where Newton starts:

| `InitialGuess` | Newton starts from |
|---|---|
| `dc` (the default) | The DC point, with the drive stepped as `DriveStepping` says. Every existing result is unchanged |
| `transient` | The last period of a native transient run from rest at full drive, run until one period looks like the next. No drive stepping |
| `fallback` | `dc` first; only if that does not converge, the transient start |

**The answer is still harmonic balance's**, solved to its own `Tol`: the transient run only supplies the start. The run
writes `Seed.Periods`, `Seed.Settled`, `Seed.HbIterations` and `Seed.ScaledElements`, and a message such as:

```
harmonic balance started from a transient seed: 43 periods, settled; no choke or block scaled;
harmonic balance converged in 5 Newton iterations from it.
```

Reach for it when a DC start does not converge or needs a long drive ladder, and when harmonic balance converges to a
solution you do not believe (an undivided divider). `fallback` costs nothing on circuits that already converge. In a
parametric sweep only the first point, and any point that fails from its neighbour's answer, is seeded.

{{ui: transient-preset-classe}}

**Fast bias.** A bias choke or DC block settles over its own time constant, which can be thousands of carrier periods.
With `SeedFastBias=yes` (the default), the transient run alone uses smaller values for each inductor whose reactance at
the fundamental is more than 50 times the impedance it sees, and each capacitor whose reactance is under a fiftieth of
it, scaled to exactly 50 times (a fiftieth): still a choke or a block, settling in about eight periods. It does not change
what harmonic balance solves: the real values are put back before Newton starts, and the answer is the same to Newton's
tolerance. It shortens only the transient run. The message names each scaled part and its factor. `SeedTol` (default
1e-3) is how alike two periods must be, and `SeedPeriods` (default `auto`: until settled, at most 200) how long the
transient run may go.

**Oscillators without `Fguess`.** With `InitialGuess=transient` on an oscillator analysis, the circuit is kicked
(`KickV`, by default 1 % of the largest DC supply) at the OscProbe's node and simulated until its period and amplitude
stop changing. The oscillation's own frequency, amplitude and waveforms start the solve, so `Fguess` is optional: without
it the start-up search spans 1 kHz to 1 THz. If nothing oscillates, the run stops and quotes the start-up search's
result.

Detail: [Starting from a transient run](simulations.html#hb-transient-start) and the [Oscillator](simulations.html#oscillator)
analysis. The [Class-E with a Transient Seed](../examples/class-e-with-a-transient-seed/index.html) example shows a
switching stage seeded, beside a transient bench of the waveforms the seed came from.

## From the command line {#headless}

```
circuitrf tran bench.csch                       run every transient analysis, print the measures
circuitrf tran bench.csch --solver ngspice -o tr.npy
circuitrf netlist bench.csch -o deck.cir        the ngspice deck, or every refusal
circuitrf explain bench.csch --analysis TR1     step count, time constants, steering
circuitrf hb classe.csch                        a harmonic-balance bench with InitialGuess=transient
```

[`tran`](cli.html#tran) takes the same overrides as the other run verbs and runs a sweep that wraps the analysis.

## Examples {#examples}

| Example | Shows |
|---|---|
| [Pulse Response](../examples/pulse-response/index.html) | A Butterworth filter's step response and a line's reflection, native and ngspice side by side |
| [Buck Converter](../examples/buck-converter/index.html) | A switching converter's start-up, ripple and settling, and initial conditions that skip the start-up |
| [Class-E with a Transient Seed](../examples/class-e-with-a-transient-seed/index.html) | Harmonic balance seeded by a transient run, and the drain waveforms of the stage |
