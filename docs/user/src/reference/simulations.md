---
title: Simulations
slug: reference/simulations.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Simulations
lede: Every analysis circuitRF runs, what it computes, a short overview of the method, and the full set of settings. Algorithm details are deferred to the design notes / white papers; this chapter is the operational reference.
keywords: analysis, sweep, DC, S-parameters, harmonic balance, HB, loadpull, load pull, parametric sweep, run, simulate, envelope, modulated, ACLR, ACPR, pulsed CW, pulse droop, pulse-to-pulse, radar
---

<nav class="toc">
    <h2>Analyses</h2>
    <ol>
      <li><a href="#dc">DC (operating point)</a></li>
      <li><a href="#s-parameters">S-Parameters</a></li>
      <li><a href="#harmonic-balance">Harmonic Balance</a></li>
      <li><a href="#parametric-sweep">Parametric Sweep</a></li>
      <li><a href="#loadpull">Loadpull / Sourcepull</a></li>
      <li><a href="#loadpull-pursuit">Loadpull Pursuit</a></li>
      <li><a href="#two-tone">Multi-Tone Harmonic Balance</a></li>
      <li><a href="#envelope">Circuit Envelope (modulated signals)</a></li>
      <li><a href="#cli">Running an analysis from the command line</a></li>
    </ol>
  </nav>

<div class="callout note">
<span class="label">How results are stored</span>
<p>Every analysis returns a <strong>DataSet</strong> — a named bundle of labelled, unit-bearing arrays
(<strong>DataCube</strong>s), each a single kind, real or complex. A whole run's DataSets go into
<strong>one</strong> file in the workspace's shared <code>results/</code> folder, and
<strong>you name that file</strong> in the <strong>Results file</strong> field above the analyses list;
leaving it blank uses the schematic's own name. This is what the
<a href="data-display.html">Data Display</a> plots and what <code>.npy</code> / <code>.mat</code> export
produces. Full detail: <a href="npy-export.html#where">Where results live</a>.</p>
<p>A new analysis shows its frequencies in the preferred unit from
<a href="settings.html#general">Settings ▸ General ▸ Units</a>; a unit the analysis or the schematic already states wins.</p>
</div>

## DC (operating point) {#dc}

**Computes:** the steady-state node voltages and branch currents with no signal applied — the bias
point. For nonlinear circuits this is the prerequisite to harmonic balance.

**Method:** modified nodal analysis (MNA); nonlinear devices solved by Newton–Raphson with *gmin*
and source stepping for convergence robustness.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Tol</td><td>Newton convergence tolerance on the residual.</td></tr>
      <tr><td>MaxIter</td><td>Maximum Newton iterations.</td></tr>
      <tr><td>gmin / source stepping</td><td>Continuation aids that ease a hard bias point into convergence.</td></tr>
    </tbody>
  </table>

<p class="small">Read currents directly with an <a href="components.html#iprobe">IProbe</a> in series, and name a voltage with a <a href="components.html#vprobe">VProbe</a> on the wire.</p>

## S-Parameters {#s-parameters}

**Computes:** the linear, small-signal multiport network parameters over a frequency sweep — the
S-matrix at each frequency, with renormalization to the port reference impedances. Ports are
defined by [Term](components.html#term) components (or [P1Tone](components.html#p1tone) port
numbers).

**Method:** complex MNA solved per frequency point; each port is excited in turn and the scattered
waves are extracted and renormalized. Embedded [Touchstone (SnP) blocks](components.html#snp) are
interpolated onto the sweep.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Frequency sweep</td><td>Start / Stop / Step (or point count), or <b>Single</b> for one frequency; one or more segments, which <b>+ Segment</b> adds as a single point. The run solves every segment's frequencies together, sorted, each once. Units accepted (<code>GHz</code>, <code>MHz</code>, …).</td></tr>
      <tr><td>Reference impedance</td><td>Per-port reference (from each Term's <code>Z</code>); the result renormalizes to it.</td></tr>
      <tr><td><code>NDF=yes</code></td><td>Also compute the <a href="wsprobe.html#ndf">normalized determinant function</a> — the rigorous count of right-half-plane poles of the whole network. <code>PassiveVars=</code> / <code>PassiveParams=</code> name what passivates a user-supplied model.</td></tr>
      <tr><td><code>MarginThreshold=</code></td><td>The <a href="wsprobe.html#stability-margin">stability margin</a> in dB below which a WSProbe is reported as worth looking at. Default <code>-15</code>; <code>none</code> disables.</td></tr>
      <tr><td><code>Noise=yes</code></td><td>Also compute the <a href="#sp-noise">small-signal noise</a>: the noise-wave correlation, and for a two-port the noise figure and noise parameters. The <b>Noise</b> box in the analysis editor. Default <code>no</code>.</td></tr>
      <tr><td><code>NoiseContrib=yes</code></td><td>With <code>Noise=yes</code>, also write <code>NCs</code>, <a href="#sp-noise-parts">each part's share</a> of the noise-wave correlation. The box under <b>Noise</b>. Default <code>no</code>.</td></tr>
    </tbody>
  </table>

<p class="small">A <a href="components.html#wsprobe">WSProbe</a> anywhere in the circuit adds the
<code>wsp</code> matrix and its derived quantities to this run, at the cost of a few extra
back-substitutions and no extra factorization. See <a href="wsprobe.html">The WSProbe</a>.</p>

<p class="small">For the ideal <a href="system-components.html">System Components</a> this is the
analysis that answers almost everything about them: port match, isolation, coupling and directivity,
a filter's passband and rejection, a duplexer's arm-to-arm leakage, and an amplifier's small-signal
gain and stability.</p>

<p class="small">From the CLI: <code>circuitrf sparam circuit.cnl --freq 1GHz:10GHz:50MHz -o out.s2p</code>.</p>

### Small-signal noise {#sp-noise}

`Noise=yes` adds the circuit's small-signal noise to the S-parameter run. The noise comes from the same
solve: one more back-substitution per port at each frequency, and no further factorization. S does not
change. `Noise=no`, the default, adds nothing.

```text
analysis SP1 type=sparam start=1 stop=6 npts=51 Unit=GHz Noise=yes
```

**What the run adds.** The cubes go into the analysis's own group, beside `S` and `Z0`. A
[parametric sweep](#parametric-sweep) stacks them as it stacks `S`.

| Cube | Axes | Unit | What it is |
|---|---|---|---|
| `Cs` | `[freq, i, j]` | W/Hz | The correlation ⟨b·bᴴ⟩ of the noise waves leaving the ports. Every port is terminated in its own noiseless Z0. |
| `NF` | `[freq]` | dB | The noise figure with port 1 driven from its own Z0 and port 2 loaded in its own. |
| `Fn` | `[freq]` | — | The same noise factor F, linear: 10^(NF/10). |
| `NFmin` | `[freq]` | dB | The minimum noise figure, over every source. |
| `Fmin` | `[freq]` | — | The minimum noise factor, linear. |
| `Te` | `[freq]` | K | The equivalent input noise temperature, T0·(F − 1). |
| `Tmin` | `[freq]` | K | The lowest Te any source gives, T0·(Fmin − 1). |
| `Rn` | `[freq]` | Ω | The equivalent noise resistance, not normalised. |
| `Sopt` | `[freq]` | — | The optimum source reflection, the one that reaches NFmin, on R = Re Z0 of port 1. |
| `Zopt` | `[freq]` | Ω | The optimum source as an impedance, R·(1 + Sopt)/(1 − Sopt). |
| `NFc` | `[freq, part]` | — | Each part's share of F − 1. They sum to F − 1. |
| `NFpct` | `[freq, part]` | % | The same as a percentage of F − 1, which is the number to sort by. |
| `NCs` | `[freq, part, i, j]` | W/Hz | Each part's share of `Cs`, with `NoiseContrib=yes` only. They sum to `Cs`. |

Every cube but `Cs` and `NCs` is for a two-port only, because a noise figure and noise parameters belong to a
two-port. The names are the ones commonly used for them. The linear noise factor is `Fn` rather than `F`, because
`SP1.F("probe")` already reads a [WSProbe](components.html#wsprobe)'s return difference.

A network with more than two ports gets `Cs` only. For the noise figure from port 1 to any other port, or
from any other source, and for constant-noise-figure circles, use
[`nf()`, `te()`, `rn()` and `ns_circle()`](derived-metrics.html#noise): `nf(SP1.S, 0, 3)` is the noise
figure from port 1 to port 3 of a three-port. Gain and stability circles to draw beside the noise circles are
[`ga_circle()`, `gp_circle()` and `stab_circle()`](derived-metrics.html#gain-circles), and the
[Noise and gain circles](derived-metrics.html#lna-circles) chart shows all of them in one action.

**What is noisy.**

- **Every linear passive part** is thermal at its own temperature. That is its `Temp`, or `Dtemp` above
  the ambient, or else the design's ambient. The noise is computed from the part's own small-signal
  stamp (Bosma's theorem), so a lossless part adds none, and a lossy one adds exactly the noise of its
  loss. Resistors, lossy reactances, transmission lines and coupled inductors (`Mutual`) are all covered.
- **An [SnP block](components.html#snp)** whose two-port file has a Touchstone noise block is noisy by
  that data, even if it has gain. A passive file with no noise block is thermal, like any passive part.
- **A [compiled model](veriloga.html)** is noisy by the sources it declares (`white_noise`,
  `flicker_noise`). They are evaluated at the DC operating point the run linearises the device about, and
  multiplied by the instance's multiplicity.
- **A `Z_Port`** whose impedance has a real part that is positive semidefinite at every swept frequency is
  passive, and thermal like any passive part. One with a negative real part anywhere is noiseless.
- **An SnP block's temperature** is its own `Temp` or `Dtemp`, as on any part. It matters only for a passive
  block with no noise data, whose noise is thermal. Noise data from a file is absolute and does not change
  with it.

**What is noiseless.** The following parts have no noise model here:

- the built-in nonlinear device models (diodes, BJTs, FETs, the SDD);
- active or black-box linear parts, such as a controlled source, an amplifier block or a negative
  resistor;
- an SnP block with gain and no noise data;
- every Deembed;
- a compiled model that declares no noise sources.

These are taken as noiseless, and one warning names them all (`sparam.noise.noiseless`), so a result
never leaves out a part without saying so. Ports and independent sources are noiseless by definition,
and they are not named.

**Switching a part's noise off.** `Noise=no` on a part makes it noiseless in this analysis:

```text
R:Rbias  b 0  R=10 kOhm Noise=no
```

Use it for a resistor that stands in for an instrument's load, or one that is part of a model and not a
physical resistor. The part is not named in the warning, because the statement is yours. `Noise=yes`, the
default, leaves the part as noisy as its model. The key is accepted on every part: on a `Mutual` or any
of the inductors it couples it quiets the whole coupled group, and on a port, a source or a probe it does
nothing (`check` says so, as `check.noise.inert-switch`). In the parameter editor it is the **Noise** check
box in the part's own list. Its show-on-schematic box draws it on the symbol, and is off by default.

#### Which part sets the noise {#sp-noise-parts}

A two-port run also writes `NFc` and `NFpct` on a `part`
axis. The axis has one entry per noisy part, labelled with its instance path. A `Mutual` and the inductors
it couples are one entry, labelled with their names joined by `+`, because their loss is one network.
`NFc` is the part's own output noise at port 2 divided by the source's, with port 1 driven from its Z0, so
F − 1 is their sum. `NFpct` is each one as a percentage of F − 1. The shares come from the same solve as
`Cs`, at no extra cost. To see them, plot `NFpct` against `part` at one frequency on a rectangular plot.

`NoiseContrib=yes` also writes `NCs`, each part's share of the whole `Cs` matrix, for any number of ports.
Its size is frequencies × parts × ports² complex values, which is why it is not written by default.
`circuitrf explain bench.cnl --analysis` states that size, and lists every part's noise model: thermal at
its temperature in K, the file and band of an SnP block's noise data, a compiled model's noise sources,
switched off, or noiseless with the reason. Read it before trusting an NF.

#### Conventions and noise data

- **Spectral densities are single-sided and absolute.** A matched load's outgoing noise wave is kT W/Hz,
  and a resistor's open-circuit noise voltage is 4kTR V²/Hz. k is 1.380649e-23 J/K, exact.
- **The noise waves are the power waves S is defined on.** Every port is terminated in its own noiseless
  Z0. This is why a port whose Z0 has no positive real part is refused with `Noise=yes`
  (`sparam.noise.reactive-z0`): there is no termination to define the waves against.
- **NF is referred to T0 = 290 K, but the default ambient is 26.85 °C = 300 K.** A design that states no
  temperature is 10 K warmer than the standard, and its passive parts make that much more noise. A
  matched 6 dB pad reads F = 1 + (300/290)(L − 1), which is NF = 6.111 dB, not 6.000 dB. Set
  `temp = 16.85` to put the whole design at 290 K. Noise data from a file is absolute at T0, so it does
  not change with the ambient.
- **Sopt and Zopt are on Re Z0 of port 1**, and `Rn` is in ohms. A Touchstone row's rn is Rn over that R,
  which `rn(SP1.S)` gives.

**An SnP block's noise data.** circuitRF reads Touchstone 1.x noise rows: frequency, NFmin in dB, |Γopt|,
∠Γopt in degrees, and rn normalised to the option line's R. Between rows the noise parameters are linear.
Outside the rows the nearest row holds, and `sparam.noise.data-band` says so. A row that no physical
two-port could have (0 ≤ Fmin − 1 ≤ 4·Rn·Re Yopt fails) gets `sparam.noise.data-unphysical`, and it is
used as given.

**Written out.** `circuitrf sparam bench.cnl -o out.s2p` on a two-port writes a Touchstone noise block
after the data. It is in the same 1.x format, with Γopt and rn on the option line's R. The rows are the
run's own cubes, so reading the file back gives them again.

**A worked example: Pozar's Ex. 12.5.** A transistor is described by its S-parameters and its noise
parameters in a Touchstone file. The book gives them at 4 GHz, and the file holds them over 3.5 to 4.5 GHz:
Fmin = 1.6 dB, Γopt = 0.62∠100° and rn = 0.4 on 50 Ω, which is Rn = 20 Ω.

```text
# GHz S MA R 50
3.5 0.6 -60 1.9 81 0.05 26 0.5 -60
4 0.6 -60 1.9 81 0.05 26 0.5 -60
4.5 0.6 -60 1.9 81 0.05 26 0.5 -60
! noise: f  NFmin(dB)  |Gamma_opt|  angle(Gamma_opt) deg  Rn/R
3.5 1.6 0.62 100 0.4
4 1.6 0.62 100 0.4
4.5 1.6 0.62 100 0.4
```

```text
Port:P1  p1 0  Num=1 Z=50 Ohm
Port:P2  p2 0  Num=2 Z=50 Ohm
SnP:X1   p1 p2 NumPorts=2 File="pozar_ex12_5.s2p"
analysis SP1 type=sparam start=3.5 stop=4.5 npts=3 Unit=GHz Noise=yes
measure NFopt = nf(SP1.S, polar(0.62, 100))
measure NF2   = ns_circle(SP1.S, 2)
```

The run gives back the file's NFmin = 1.6 dB, Rn = 20 Ω and Sopt = 0.62∠100°, which is
Zopt = 19.2 + j38.2 Ω. `NF` is 2.948 dB, which is the noise figure from the 50 Ω source, and its noise
temperature `Te` is 282 K against a `Tmin` of 129 K. `NFopt` is 1.6 dB again. `NF2` is the 2 dB noise circle:
its centre is at 0.563∠100° and its radius is 0.245. The book rounds these to 0.56 and 0.24.

**Worked examples.** **File ▸ New ▸ Examples ▸ S-Parameter Noise** has three benches, each with a Data Display
that opens when it is simulated. A matched 6 dB pad is swept over its temperature, and its NF lies on
1 + (T/T0)(L − 1). A low-noise stage, a pad and a second stage lie on Friis' formula as the pad's loss is swept.
A noise-matched LNA shows NF against NFmin, Te against Tmin, its Sopt and two `ns_circle` circles on a
Smith chart, and Zopt.

## Harmonic Balance {#harmonic-balance}

**Computes:** the steady-state response of a nonlinear circuit driven by one or more tones (up to
six) — the spectrum (DC + harmonics, and mixing products when there is more than one tone) at
every node. From it come Pout, gain, efficiency, PAE, compression, and intermodulation.

**Method:** the circuit is split into a *linear* sub-network (solved in the frequency domain) and
*nonlinear* devices (evaluated in the time domain and transformed by FFT). A multidimensional
Newton solve drives the harmonic-balance residual — frequency-domain KCL — to zero, using a
conversion-matrix Jacobian. Power/source **continuation** ramps the drive to reach convergence
deep into compression. [Multi-tone analysis](#two-tone) uses a diamond-truncated mixing spectrum.

For the ideal [System Components](system-components.html) this is the analysis for everything an
S-parameter run cannot reach — a mixer's conversion gain and its mixing products, an amplifier's
compression and IM3, and passive intermodulation on the blocks that carry it. Two tones
([multi-tone](#two-tone)) are what an intercept measurement needs.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th><th>Typical</th></tr></thead>
    <tbody>
      <tr><td>Tone</td><td>The fundamental frequency (a value or a variable such as <code>RFfreq</code>). Two or more tones for intermodulation.</td><td>2 GHz</td></tr>
      <tr><td>ToneUnit</td><td>Unit for the tone value when it doesn't carry its own (a unit on the referenced variable wins).</td><td>GHz</td></tr>
      <tr><td>MaxHarm</td><td>Highest harmonic order retained (5 → DC plus 5 harmonics). Higher captures sharper nonlinearity at more cost.</td><td>5–7</td></tr>
      <tr><td>FFTOverSample</td><td>Oversampling factor for the time grid used by the nonlinear FFT. Raise to reduce aliasing on stiff devices.</td><td>1–2</td></tr>
      <tr><td>Tol</td><td>Newton convergence tolerance on the HB residual.</td><td>1e-6</td></tr>
      <tr><td>DriveStepping</td><td>Power/source continuation: <code>IfNecessary</code> (ramp only when a direct solve stalls), or always/never.</td><td>IfNecessary</td></tr>
      <tr><td>GuardHarmonic</td><td>Extra guard harmonic(s) beyond <code>MaxHarm</code> for anti-alias safety.</td><td>0</td></tr>
      <tr><td>Lambda</td><td>Newton damping factor (1 = full step; lower damps an unstable solve).</td><td>1</td></tr>
      <tr><td>MaxIter</td><td>Maximum Newton iterations per solve point.</td><td>100</td></tr>
      <tr><td>SSStart / SSStop / SSNpts / SSStep / SSUnit / SSLog</td><td>The small-signal "tickle" sweep a <a href="components.html#wsprobe">WSProbe</a> is driven over, around the converged large-signal operating point. Absent, there is no small-signal solve and the run is unchanged. See <a href="wsprobe.html#hb">The WSProbe under harmonic balance</a>.</td><td>0.1-10 GHz</td></tr>
      <tr><td>SSMaxHarm</td><td>Sidebands retained in the conversion matrix for that solve.</td><td>= MaxHarm</td></tr>
      <tr><td>MarginThreshold</td><td>As for S-parameters, applied at each operating point.</td><td>-15 dB</td></tr>
      <tr><td>Noise</td><td><code>yes</code> adds noise over the small-signal frequencies. See <a href="#hb-noise">Noise in harmonic balance</a>.</td><td>no</td></tr>
      <tr><td>NoiseIn / NoiseOut</td><td>The port the noise figure is referred to, and the port the output noise is read at.</td><td>1 / 2</td></tr>
      <tr><td>NoiseSideband</td><td>The input sideband that carries the signal: the input frequency is f_out + k·f0.</td><td>0</td></tr>
      <tr><td>NoiseContrib</td><td><code>yes</code> adds each part's share of the noise.</td><td>no</td></tr>
    </tbody>
    <caption>Multi-tone analysis adds the extra tones and a mixing order (≥ 5 at two tones, to capture close-in IM products; lower it as tones are added).</caption>
  </table>

### Noise in harmonic balance {#hb-noise}

**Computes:** the noise figure of a circuit with a large signal in it — a mixer's SSB and DSB noise figure, an LNA's noise
figure while a blocker drives it, or a receiver chain's with its LO running. The S-parameter
[noise analysis](#sp-noise) cannot answer these: it linearises at the DC operating point, so it has no LO, no blocker and
no frequency conversion.

```
analysis HB1 type=hb Tone=LOfreq MaxHarm=7 SSStart=10 SSStop=200 SSNpts=20 SSUnit=MHz Noise=yes NoiseIn=1 NoiseOut=2 NoiseSideband=1
```

The small-signal frequencies are the **output** frequencies — for a mixer, the IF. `NoiseSideband` says where the signal
comes in: the input frequency is `f_out + k·f0`.

| Case | NoiseSideband |
|---|---|
| An amplifier, or an LNA with a blocker as the tone | `0` (same frequency in and out) |
| A down-converter with f_IF = f_RF − f_LO | `1` |
| A down-converter with f_IF = f_LO − f_RF | `-1` |
| Two tones (a blocker and an LO) | one integer per tone, `1,0` |

The image is the opposite sideband (`-k`). `circuitrf explain <file> --analysis` prints the signal and image input
frequencies at the first and last output point, so a wrong sign shows before a run.

**Results**, over `ssfreq`:

| Cube | Meaning |
|---|---|
| `NF`, `Fn` | The SSB noise figure, dB and linear: only the signal sideband counts as signal. |
| `NFdsb`, `Fndsb` | The DSB noise figure: the signal sideband and its image both count as signal. Written only when the input and output frequencies differ. |
| `Te` | The SSB equivalent input noise temperature, K. |
| `Nout` | The noise at the output port, W/Hz, with the input port's termination at T0 = 290 K. |
| `Gc` | The conversion (or small-signal) gain from the input sideband to the output, dB. `Fn = Nout / (k·T0·Gc)`. |
| `NFsb` [ssfreq, sideband] | The share of the output noise that arrives through each input sideband, %: noise folding made visible. |
| `NFc`, `NFpct` [ssfreq, part] | With `NoiseContrib=yes`: each part's share of F − 1, and the same as a percentage. The input termination's other sidebands are one entry. |

**What is noisy.**

- Passive parts are thermal at their own temperature, at every sideband frequency.
- A nonlinear device's noise sources are read at every time sample of its large-signal waveform, so its noise is correlated
  between sidebands: a diode switched by the LO is noisy only while it conducts. A flicker term is taken at the geometric
  mean of the two sidebands' frequencies.
- A [Mixer](system-components.html#mixer)'s `NF` is an equivalent noise source at its RF port.
- The input port's termination is at T0 at every sideband; the other ports are noiseless, as in the S-parameter noise
  analysis.

A device's noise is correlated between sidebands, so it cannot be split by sideband. `NFsb` shows the noise from the
terminations and passive parts, and it adds up to 100 % less the devices' share. Each device's share is in `NFc`.

A compiled (Verilog-A) device reports its noise for the whole waveform in one request per operating point.

Inside a [parametric sweep](#parametric-sweep), LO power or blocker power is an ordinary sweep axis, so "NF against LO
drive" and "NF against blocker level" need nothing more. With the drive turned down to nothing, `NF` equals the
S-parameter noise analysis's.

## Oscillator {#oscillator}

An oscillator analysis finds a free-running oscillation: its frequency, its amplitude, its harmonics, and how they tune.
Harmonic balance alone cannot do this. It needs the fundamental stated, and an oscillator's frequency is part of the
answer. Started on an oscillator, it finds the circuit sitting still at its DC operating point, which is always a solution.

Place one [OscProbe](components.html#oscprobe) in the oscillator's loop (series) or across its resonator (shunt), and add
an analysis of type **Oscillator**:

```
OscProbe:OP1  c 0  Mode=shunt
analysis OSC1 type=osc Fguess=140 Unit=MHz MaxHarm=9
```

**How it works.** At the OscProbe the analysis inserts a generator that works at one frequency f, with amplitude A. For
any trial (A, f) an ordinary harmonic-balance run at f gives the current the generator delivers (shunt) or the voltage
across it (series). Where that is zero, the generator is doing no work and the circuit sustains itself. That is two
equations, the real and imaginary parts, in two unknowns, solved by Newton's method around harmonic-balance runs.

**Start-up.** First, the circuit is linearised about its DC operating point, and the admittance looking into the probe is
computed from `Fstart` to `Fstop` (by default half and one and a half times `Fguess`). The circuit starts to oscillate
where that admittance has a **negative real part at a zero of its imaginary part**: the circuit gives back more power
than it takes, at a frequency where it resonates. If no such point is found, the run stops and says so, naming the span
it searched. It never returns the DC solution as an oscillation. Then the amplitude is raised from small until the
negative resistance runs out, which brackets the oscillation, and Newton's method finishes it.

`circuitrf explain --analysis` shows the start-up search without running harmonic balance.

| Key | Default | |
|---|---|---|
| `Probe` | the only OscProbe | Which OscProbe, when there are several |
| `Fguess` | (required) | Where the start-up search is centred, in `Unit` |
| `Fstart`, `Fstop` | Fguess/2, 1.5·Fguess | The start-up search's span |
| `Unit` | GHz | Unit of the three frequencies |
| `MaxHarm`, `FFTOverSample`, `Tol` | 7, 1, 1e-6 | As [harmonic balance](#harmonic-balance)'s |
| `Aguess` | ramp from small | Amplitude to start from: volts in shunt, amperes in series |

**Results.** The run writes the cubes a harmonic-balance analysis writes, at the oscillation (`V`, `I`, `ToneFreqs` and the
rest), so every HB measurement and Data Display preset works on it. It adds:

- `Fosc`: the oscillation frequency, Hz;
- `Aosc`: the generator's amplitude at the answer, which is the oscillation's own amplitude at the probe (volts in shunt,
  amperes of loop current in series);
- `Startup [freq]`: the small-signal admittance looking into the probe, the curve the start-up search read.

**Tuning.** Inside a [parametric sweep](#parametric-sweep), `Fosc` against the swept variable is a tuning curve. A
measurement gives Kvco, the slope of that curve, with `deriv()`:

```
analysis SW1 type=parametric_sweep Var=Vtune Start=0 Stop=5 Step=0.25 Inner=OSC1
measure Kvco = deriv(OSC1.Fosc)
```

**What moves the frequency.** The oscillation sits a little away from the start-up frequency. As the swing grows, the
active device spends part of each cycle cut off, and the capacitance and conductance it adds to the resonator change. A
Colpitts, for example, starts where its transistor's low emitter impedance loads one capacitor, and settles toward the
resonance of the tank with the transistor off. The harmonics pull the frequency a little too, so raise `MaxHarm` until
`Fosc` stops moving.

### Phase noise {#oscillator-phase-noise}

`PhaseNoise=yes` computes the oscillation's phase noise from the circuit's own noise sources, so the effect of the
resonator's Q, the device's flicker noise and the bias can be designed instead of assumed:

```
analysis OSC1 type=osc Fguess=140 Unit=MHz MaxHarm=9 PhaseNoise=yes PnStart=100 Hz PnStop=10 MHz PnNpts=61
```

The noise sources are the ones [harmonic-balance noise](#hb-noise) uses: passive parts at their own temperature, each
device's noise over its own large-signal waveform (shot, channel and flicker, as the model declares them), and a Mixer's
NF. Every Port and Term is the oscillator's load, a resistance at 290 K.

**Two descriptions, joined.** Far from the carrier, the noise at the carrier's two sidebands comes straight from the
conversion matrix about the oscillation, and splits into its phase part (`PN`) and its amplitude part (`AN`). Close to
the carrier, nothing holds the oscillation's phase, so the noise moves its frequency instead. There the analysis uses
how far the frequency moves for a small current at each noise source, which the oscillator's own solve already knows:
white noise gives a 1/f² slope, flicker noise a 1/f³ slope. The two are joined at one tenth of the rate at which the
oscillation's amplitude settles after a disturbance. Both hold there, and if they differ by more than 0.5 dB, the run
warns.

| Key | Default | |
|---|---|---|
| `PhaseNoise` | no | yes adds the cubes below |
| `PnStart`, `PnStop` | 100 Hz, 10 MHz | The offsets from the carrier, with an optional Hz, kHz, MHz or GHz |
| `PnNpts` | 61 | Number of offsets |
| `PnLog` | yes | Offsets evenly spaced in log-frequency, or linearly |
| `PnPort` | see below | The `Num` of the Port or Term the noise is read at |

With `PnPort` blank, the noise is read across a shunt OscProbe, or at the circuit's one Port or Term when the OscProbe
is in series. Close to the carrier the phase noise is the same everywhere. Far from it, it depends on where it is read,
so name the output port.

| Cube | Axes | |
|---|---|---|
| `PN` | offset | Phase noise L(f), dBc/Hz, single-sided |
| `AN` | offset | Amplitude noise, dBc/Hz |
| `PNc` | offset, part | Each part's phase noise, dBc/Hz; together they make `PN` |
| `PNpct` | offset, part | Each part's share of `PN`, % |
| `PNcorner` | — | The flicker corner, Hz: where the 1/f³ and 1/f² slopes cross (NaN without flicker) |

Offsets at or beyond half the carrier are NaN.

**Into a system simulation.** Right-click a `PN` trace's legend entry (or its header in a table) and choose **Copy as
Phase-Noise Mask**. The clipboard then holds `PhaseNoise="100Hz:-71.19, …"`, ready to paste onto a source an
[envelope](#envelope) run reads.

From the command line, `circuitrf osc design.csch` runs it ([`osc`](cli.html#osc)).

## Circuit Envelope (modulated signals) {#envelope}

**Computes:** the circuit's response to a **modulated** signal — LTE, NR, several carriers, a pulsed-CW
train, or an imported I/Q waveform from a [MOD_Source](components.html#modsource), designed in the
[Signal Designer](signal-designer.html) — as a time series of
harmonic-balance spectra, and from it the modulated measurements: **ACLR / ACPR1 / ACPR2** per carrier,
channel power, **PAPR and CCDF**, **AM-AM and AM-PM** (with the memory spread), gain, error (NMSE)
against the input, **drain efficiency** from the DC supply currents, and for two widely spaced
carriers the **IM2 / IM3 / IM5** products. For a pulsed-CW source it measures inside the pulses instead:
**pulse droop**, **pulse-to-pulse** spread, phase droop, rise and fall (see
[Pulsed CW: what is measured](#envelope-pulse)).

**Method.** Every envelope time step is one harmonic-balance solve with a time derivative added to
each charge (Gear-2, backward Euler on the first step). The linear network is factored once per
harmonic for the whole run, and each step costs one back-substitution per harmonic plus a small
Newton solve at the nonlinear devices, reusing its factored Jacobian while it keeps converging fast.
Each Newton update is damped by a line search, so a device driven hard at a signal peak, or one with
trap or thermal states, does not throw the solve off. A step that still does not converge is retaken
as up to 16 shorter steps, and the Messages pane says how many were.

Three things make long modulated runs practical:

- **Carriers far apart get their own fundamental.** Two 20 MHz carriers 400 MHz apart are simulated
  with each carrier's narrow envelope on a two-tone mixing lattice, so the time step follows each
  carrier's bandwidth, not the 400 MHz spacing. A single envelope around the centre would need a step
  fine enough for the IM3 products 600 MHz out; measured, that is 64× the native rate to converge.
  `Envelope = single` runs it that way anyway: the whole multi-carrier signal as one time vector
  around Fc, with the step chosen so the band holds the carriers and their IM3. Use it as a
  cross-check. Measured on two NR20 carriers 400 MHz apart, at `fine`, against the lattice:
  - channel power and ACPR1 agree within 0.1 dB, and ACPR2 within 0.5 dB at a −58 dBc floor;
  - IM3L, IM2, f1+f2 and the second harmonics agree within 0.2 dB.

  Products far from Fc are not reliable in a single envelope. IM3U moves 0.3 dB and IM5 moves 1–4 dB
  when only the envelope's centre frequency moves. The lattice result does not move (to 0.01 dB), so
  the lattice is the reference. Both modes write the same `IMD` cube. The spectra (`VSD` and `PSD`)
  are continuous either way, and show every product of the baseband and fundamental zones.
- **Parallel in time.** The record is split into segments that run on all cores at once. Each segment
  starts a warm-up early from its own harmonic-balance point and discards it. The warm-up follows the
  circuit's slowest memory: five time constants of the slowest of a [ComposedFET](components.html#composedfet)'s
  trap and thermal states and the linear network's slowest pole (a bias feed, a large decoupling
  capacitor), and never less than 5 µs. When that memory is too long to warm up in parallel, the run
  is one segment and the Messages pane says which part remembers how long; `Segments=N` splits it
  anyway. Once the warm-up is past the circuit's memory, the stitched result matches one sequential
  run. Where the segments overlap, their disagreement relative to each recorded voltage's and IProbe
  current's own level is reported as the seam error (`__EnvSeamError`), and a run warns, naming the
  net or probe, when it is above 0.01 outside a device's internal states. In a parametric sweep the points also run at the same time, up to one per core, and
  share the cores with their segments.
- **The step follows an accuracy preset.** *standard* (default) steps at 4× the signal's sample rate,
  where ACPR2 and the bias-node memory are converged on every measured case. *draft* (1×) is about
  0.4 dB pessimistic on ACPR2 and badly off on bias-network ringing. *fine* (8×) is for reference runs.

**Frequency-dependent blocks.** Resistors, capacitors and inductors enter every step exactly. Other
blocks are handled as follows.

- **Lines** ([TLIN](components.html#tline), and the microstrip, stripline and coplanar lines) are
  stepped as a delay: each end sees the wave that left the other end one line delay earlier. A
  lossless line is exact at any length, across the signal band and in the baseband envelope (the bias
  path). A lossy line's loss is taken to first order around each harmonic and left out of the baseband
  envelope, where the line stays exact at DC. A line is usually shorter than one step, so the
  delayed wave is interpolated between steps: at *standard* this is off by up to about 1.5 % at the edge
  of the signal band, at *fine* about 0.2 %. The Messages pane gives each line's delay and how far it
  is from the line across the band, and a warning names any line far enough off to move ACPR by about
  0.1 dB.
- **S-parameter blocks** ([SnP](components.html#snp)), the System [Filter](components.html#filter)
  and [Duplexer](components.html#duplexer), an [Impedance N-Port](components.html#zport) with a
  `Z(freq)`, and any other part whose response bends across a harmonic's band by more than the fit's
  error tolerance (a [Klopfenstein taper](components.html#mklopf), for one) are fitted with a rational
  model and simulated exactly across the signal band, at DC and at every harmonic. The Messages pane
  reports each fit's order and error, and, for an SnP, how far the fit is from the file's interpolated
  values at DC and at any harmonic outside the file's frequency range. An SnP, Filter or Duplexer
  whose fit misses its error tolerance, or is not passive, warns. Any other part whose fit misses or
  is not passive is not fitted: it is approximated to first order around each harmonic (its value and
  slope there) and, where its DC value is closer to it than that slope, held at its DC value across the
  baseband envelope; a warning names it.
- **Bond wires** ([wBond](wbond.html)) are a resistance and an inductance that change only slowly with
  frequency (the skin effect), plus fixed capacitances. Their value and slope at each harmonic are within
  about 10⁻⁷ of their response across the signal band, and within 0.1 % across the baseband envelope.
- **Coupled lines** (CLIN and the coupled microstrip and broadside pairs) are approximated to first
  order around each harmonic (their value and slope there). In the baseband envelope each strip is
  held at what it is at DC, a short from end to end. A warning names any whose error across the band
  is large enough to move ACPR by about 0.1 dB.

| Setting | Default | Meaning |
|---|---|---|
| Stop | 100e-6 s | Signal time simulated. ACPR settles within ~0.1 dB from 0.5 ms of LTE/NR. |
| Accuracy | standard | draft / standard / fine = 1× / 4× / 8× the signal rate. |
| MaxHarm | 5 | Harmonics of the fundamental at every step. |
| Envelope | auto | *auto* / *lattice*: carriers more than 3× their bandwidth apart get their own fundamentals. *single*: one envelope around Fc spans every carrier and their IM3 (smaller step, slower). |
| MaxMixOrder | 7 | Lattice order when carriers get their own fundamentals; 7 converges IM5. |
| Segments | auto | Parallel-in-time segments, one per core and none shorter than 4× the warm-up; one when the circuit's memory is too long to warm up in parallel. 1 runs sequentially. |
| Warmup | auto | Simulated before each segment and discarded. *auto* is 5 µs, or, with Segments auto, five of the circuit's slowest time constants when that is longer. A number is used as written. |
| Tol | 1e-6 | The Newton tolerance per step, in amperes. A run uses at most 10⁻⁴ of its own RF excitation at the record's RMS drive, so a small signal is solved as closely as a large one. |
| SaveWaveforms | yes | Keep the envelopes against time (`V`, `I`, `Pprobe_t_W`). `no` keeps every spectrum and every power, AM-AM and pulse number. |
| Noise | no | `yes` adds thermal noise to the run ([below](#envelope-noise)). |
| Seed | 1 | The seed of every random draw: thermal noise and every source's PhaseNoise mask. |
| SeedPerPoint | no | `yes` gives each point of a parametric sweep its own seed. |
| EvmSplit | no | `yes` runs the record a second time without noise, to split each carrier's error into distortion and noise and measure the SNR ([below](#envelope-evm)). Twice the run time. |

### What a run records {#envelope-records}

**Name a net to see its voltage. Put an IProbe where you want power.**

- **Every labeled net** records its voltage. A schematic labels a net by naming it, or with a
  [VProbe](components.html#vprobe) on it; a netlist labels nets on its `labelednets` line. A netlist
  that labels nothing records its MOD_Source's node.
- **Every [IProbe](components.html#iprobe)** records the power that flows through it, from its first
  node to its second. It is exact for any load, reactive ones included.

No impedance is assumed anywhere: a voltage is reported as a voltage, and a power is measured where a
current is known. Without an IProbe an envelope run reports voltages only; place one in series with a
load to measure its power. A chain of 50 Ω system blocks needs one IProbe in front of its load.

The envelope card shows what the schematic will record ("Records 4 labeled nets and 3 IProbes");
hover over it for the names. Every recorded net keeps its envelopes, so a design that labels 40 nets,
swept over five drive levels, keeps about 1 GB of waveforms. Set SaveWaveforms to `no` to keep only
the measurements.

**At every labeled net** (axis `node`, in the order harmonic balance lists nets):

| Cube | Axes | Meaning |
|---|---|---|
| `V` | time, node, harmonic | the voltage envelopes, peak phasors (SaveWaveforms) |
| `VSD` | node, freq | the voltage spectral density in dBV/Hz: RMS volts² per hertz (the same number as dBV/√Hz of the RMS amplitude density) |

With two fundamentals the `harmonic` axis is `mixIndex`. `VSD` is one continuous spectrum over every
product of the baseband and fundamental zones; into 50 Ω, a VSD in dBV/Hz plus 13.01 dB is the power
density in dBm/Hz.

**At every IProbe** (axis `branch`, named after the probes):

| Cube | Axes | Meaning |
|---|---|---|
| `Pprobe_W` | branch, harmonic | average power at each harmonic, in W. Harmonic 0 is the DC power. |
| `Pprobe_fund_W` | branch | average fundamental-band power, in W. With two fundamentals, the sum over the carrier products. |
| `Pavg_dBm` | branch | `Pprobe_fund_W` in dBm. At the output probe this is the output power. |
| `PSD` | branch, freq | the delivered power spectral density in dBm/Hz, on `VSD`'s frequency grid: ½·Re of the cross-spectrum of the probe's voltage and current. A bin where power flows backwards through the probe is blank. |
| `ACLR` | branch, carrier, metric | `Pch_dBm`, and `ACPR1L` / `ACPR1U` / `ACPR2L` / `ACPR2U` in dBc, from `PSD` |
| `IMD` | branch, product | the delivered power of each carrier and distortion product, in dBm, for two widely spaced carriers (either mode) |
| `PAPR_dB`, `CCDF` | branch / branch, prob | of the instantaneous delivered power ½·Re(V₁·I₁*) |
| `Gt_dB`, `DE_pct` | branch | [below](#envelope-power) |
| `NMSE_dB`, `Delay_s` | branch | the drive aligned to the probe node's fundamental envelope: the residual, and the delay |
| `AMAM`, `AMPM` and their `_min` / `_max` | branch, Pin_inst | [AM-AM, AM-PM and memory](#envelope-amam) |
| `EVM_pct`, `FSKerr_pct`, `CSSerr_pct` and the rest | branch, carrier | each carrier's received quality ([Signal quality](#envelope-evm)) |
| `Const` | branch, carrier, symbol | the received symbols, complex, for the constellation |
| `I` | time, branch, harmonic | the current envelopes (SaveWaveforms) |
| `Pprobe_t_W` | time, branch | fundamental-band power at every step (SaveWaveforms) |

Every IProbe gets every row, so the branch axis is the same at every sweep point. A probe that carries
no RF, such as a supply or bias probe, is blank in the output rows (`Gt_dB`, `DE_pct`, `NMSE_dB`,
`ACLR`, `PAPR_dB`, `CCDF`, AM-AM, AM-PM and EVM): its fundamental-band power is below 10⁻⁶ of the available
drive. Its `Pprobe_W` still reads its DC power.

A design with a [wBond](wbond.html) also gets `WireTemp` and `WireTempState` (axis `wire array`): each array's
hottest wire under the record's average heat ([wBond ▸ In a circuit envelope run](wbond.html#wt-envelope)).

A [Parametric Sweep](#parametric-sweep) around an envelope analysis (sweeping the source's `Pavg`, say)
gives ACPR against output power in one run. A sweep point that fails is NaN and named in the Messages
pane; the other points are kept. A Data Display trace pins a net or a probe by its name, so it keeps
reading the same one when a re-run adds nets, probes or a sweep.

### Power and efficiency {#envelope-power}

Two quantities never depend on where the output is, and are scalars:

| Scalar | How it is calculated |
|---|---|
| `Pin_avg_dBm` | The **available** power of the drive: the mean of \|V<sub>drive</sub>(t)\|² / (8 · Re Z<sub>source</sub>), where V<sub>drive</sub> is the source's open-circuit envelope. This is not the power delivered into the input. Every MOD_Source counts. |
| `Pdc_W` | The sum over **every DC source with a non-zero `Vdc`** of −Vdc · ⟨I<sub>dc</sub>(t)⟩, where I<sub>dc</sub>(t) is that source's DC branch current at each envelope step. The current follows the modulation, so this is the true average supply power. Gate and auxiliary supplies count; a resistive bias network's own dissipation lowers `DE_pct`. |

At every IProbe:

| Cube | How it is calculated |
|---|---|
| `Pavg_dBm` | The **fundamental-band** power through the probe. Everything inside the carrier's envelope counts, including the adjacent-channel and distortion power. Power at 2f₀, 3f₀ … does not. |
| `Gt_dB` | `Pavg` / `Pin_avg`: transducer gain from the available input power. |
| `DE_pct` | 100 · `Pavg` / `Pdc`: average drain efficiency in the fundamental band. Right for one amplifier; blank when no DC power is drawn. |

Each is a time average over the measured record (after the warm-up). With two fundamentals (carriers on
the mixing lattice), `Pdc_W` and `DE_pct` are not produced; the supply probe's `(0,0)` product gives the
DC power.

#### Where to place IProbes {#envelope-iprobe-power}

Put one in series with each place you want a power:

- the **load**, for output power,
- the **input**, between the source and the amplifier's input match, for delivered input power,
- each **supply**, between the DC source and the circuit, for DC power.

With the probe's first node on the source side, each of these powers is positive in a working
amplifier. The probe node's voltage is what AM-PM, NMSE and the delay are read from, so the output
probe belongs at the load itself, not ahead of an output match.

**One amplifier.** With `IProbe:Iin` at the input, `IProbe:Iout` at the load and `IProbe:IDC` at the
drain supply:

| Quantity | From |
|---|---|
| Output power | `Pavg_dBm` at `Iout` |
| Delivered input power | `Pavg_dBm` at `Iin` |
| DC power | `Pprobe_W` at `IDC`, harmonic 0 |
| PAE | (output power − delivered input power) / DC power |
| Power gain | output power / delivered input power |

**Two amplifiers**, A and B, each with its own load and supply: place `IoutA`, `IDCA`, `IoutB` and
`IDCB`. Each amplifier's drain efficiency is its own `Pprobe_fund_W` at `Iout` over its own `Pprobe_W`
at `IDC`, harmonic 0, and the total DC power is the sum of the two.

**In a measure line**, `ENV1.P("probe")` reads these cubes by name (see
[Power through an IProbe](measurements.html#power)):

```netlist
measure Pout_dBm = dBm(ENV1.P("Iout"))
measure Pin_dBm  = dBm(ENV1.P("Iin"))
measure Pdc_W    = ENV1.P("IDC", 0)
measure Gain_dB  = Pout_dBm - Pin_dBm
measure DE_pct   = 100 * ENV1.P("Iout") / ENV1.P("IDC", 0)
measure PAE_pct  = 100 * (ENV1.P("Iout") - ENV1.P("Iin")) / ENV1.P("IDC", 0)
```

The same lines work inside any sweep, and on a harmonic-balance analysis (`HB1.P("Iout")`).

### AM-AM, AM-PM and memory {#envelope-amam}

With one fundamental and a modulated drive, every IProbe gets AM-AM and AM-PM against the
instantaneous **available** drive power, |V<sub>drive</sub>(t)|² / (8 · Re Z<sub>source</sub>), on a
fixed grid of 0.5 dB bins (axis `Pin_inst`, −40 to 60 dBm). A bin needs 16 samples; one with fewer is
blank.

- The probe node's fundamental envelope is aligned to the drive (the delay `Delay_s` reports).
- **AM-AM** is the delivered power ½·Re(V₁·I₁*) over the available drive, sample by sample, averaged in
  each bin. It is exact for any load.
- **AM-PM** is the phase of the probe node's voltage relative to the drive, less the mean gain phase.
- **`AMAM_min` / `AMAM_max` and `AMPM_min` / `AMPM_max`** are the extremes of the samples in each bin.
  Memory widens them: the same drive level meets a different trap, thermal or bias state. So does the
  bin itself: in compression the gain changes across 0.5 dB of drive, by about 0.5 dB on a device whose
  gain falls 1 dB per dB.

**The memory cloud** is every one of those samples on one time axis, so the spread can be seen point
by point rather than as two edges:

| Cube | Axes | Meaning |
|---|---|---|
| `Pin_inst_t_dBm` | time | the available drive power at each sample |
| `Gt_t_dB` | time, branch | the gain at each sample: the values AM-AM bins |
| `PM_t_deg` | time, branch | the phase at each sample: the values AM-PM bins |

They keep at most 20,000 samples per probe (every m-th, m set by Stop and the step alone, so a sweep
stacks), and they are kept with SaveWaveforms `no`. Plot the cloud with a
[versus](data-display.html#versus) trace drawn with markers and no line:

```
ENV1.Gt_t_dB[:, "Iout"] vs ENV1.Pin_inst_t_dBm
```

Under a sweep of the drive, each level draws its own cloud with no edit.

### Pulsed CW: what is measured {#envelope-pulse}

A [pulsed-CW](signal-designer.html#pulse) source turns the run into a pulse measurement. Harmonic
balance cannot see what happens inside a pulse: it solves one steady state, so the device sits at one
temperature and one trap state. An envelope run steps a [ComposedFET](model-composer.html)'s thermal
and trap states, and the bias network's storage, as real memory over time, so the output falls across
the top of each pulse as the channel heats and the drain supply sags.

The run starts from harmonic balance at the first sample of the record, which for a pulse is the
off-time, so the first pulse starts from the quiescent point. A record that started on the pulse top
would start with the device already at its CW temperature, and the first pulse would show no droop at
all; that is why a pulse's `Delay` must be greater than zero.

At every IProbe, from the power delivered through it, it adds:

| Cube | Axes | Meaning |
|---|---|---|
| `Pout_t_dBm` | branch, time | delivered power at every step, floored at −100 dBm in the off-time, so it plots with no arithmetic |
| `PulseTop_dBm` | branch, pulse | the power at the droop window's start |
| `PulseDroop_dB` | branch, pulse | the power at the window's start minus at its end: positive is drooping |
| `PulsePhaseDroop_deg` | branch, pulse | the probe node's phase (relative to the input) at the window's start minus at its end |
| `PulseRise_s`, `PulseFall_s` | branch, pulse | the 10–90 % times of the amplitude (the square root of the delivered power) |
| `PulseToPulse_dB`, `PulseToPulse_deg` | branch | the peak-to-peak spread over the pulses of `PulseTop_dBm`, and of the phase at the window's start; the first pulse counts |

The **droop window** is the flat top from `MeasureStart` to `MeasureEnd` (10–90 % of the top by
default; set them on the carrier). A pulse only partly inside `Stop` is not measured, and the Messages
pane says how many were and over which window. `ACLR`, `CCDF`, `PAPR`, `AM-AM` / `AM-PM` and `NMSE`
are not reported for a pulse: they are defined for a modulated signal, and on a pulse train they print
numbers that look like results. `Pin_avg_dBm`, `Pavg_dBm`, `Gt_dB`, `Pdc_W` and `DE_pct` are
averages over the whole run, off-time included, with no settling skipped (the train starts at
quiescent and has nothing to settle), so they depend on how many pulses `Stop` holds. `Pdc_W` is
what the DC sources deliver, so a drain fed from a storage capacitor draws most of each pulse's energy
from the capacitor and repays the supply after the pulse: end `Stop` once the last pulse's recharge is
over (about five of the supply resistance × storage capacitance time constants after its fall), or
`DE_pct` reads high — above 100 % on a run that stops at the end of a pulse.

**Before the run starts**, the Messages pane states how many pulses `Stop` holds and the circuit's
slowest memory — a thermal pole, a trap's emission, the bias network — against it: a run shorter than
about five of those time constants shows **the first pulse from cold**, not a settled train. A drain-lag
trap that captures at the pulse peak in nanoseconds and emits over a millisecond lowers the quiescent
current after the first pulse, so pulse 1 differs from pulse 2: that is current collapse, the effect a
pulse-to-pulse figure exists to report. A thermal train settles only after several of its slowest time
constants, which on a package pole means milliseconds of record at a step of a few nanoseconds.

From the command line: `circuitrf env design.cnl [--stop 0.5e-3] [--accuracy standard] [-o out.npy]`.
It prints each IProbe's power, gain, efficiency, NMSE and PAPR, and the ACLR and signal quality (EVM,
FSK error or the de-chirped error) at every probe that carries RF. For a pulsed-CW source it prints each pulse's top power, droop, phase droop, rise and fall
at those probes, then the pulse-to-pulse spread.

### Noise and phase noise {#envelope-noise}

Without these settings every signal in an envelope run is noiseless: the spectrum has no floor and an
LO is a perfect tone. Two settings add what a receiver is limited by.

**Thermal noise (`Noise=yes`).** Every part the [S-parameter noise analysis](#sp-noise) makes noisy
adds noise to the run: resistors and other passive parts at their own temperature, S-parameter blocks
with noise data, and system amplifiers with an `NF`. At every step, each harmonic band gets a random
current on the part's nodes, with the part's noise at the centre of that band. The noise is taken as
flat across one band, so a part whose noise changes noticeably across the signal bandwidth is
evaluated at the band centre. The `PSD` then shows the noise floor, and `ACLR` includes it.

- Sources and ports are noiseless unless you say otherwise. To put a source's own noise in the run,
  tick **Noisy** in the source's parameters (`Noise=yes` on a P1Tone, PnTone or MOD_Source): its
  impedance then adds thermal noise at the source's temperature, `Temp` if set, else the ambient.
  Set `Temp=16.85` (290 K) for the input noise a noise figure is referred to. A P1Tone or PnTone with
  per-harmonic impedances uses each band's own. The S-parameter, harmonic-balance and loadpull noise
  analyses keep the source noiseless, because their noise figure already counts the source at 290 K.
- A transistor's or diode's own noise is not added: it changes with the device's state inside a
  step. The run names such devices in one warning, and they run noiseless.

**Phase noise (`PhaseNoise=` on a source).** P1Tone, PnTone, the tone sources and MOD_Source take a
phase-noise mask, single-sided L(f) in dBc/Hz at offsets from the carrier:

```
P1Tone:LO  lo 0  Pavl=7 dBm  Freq=2 GHz  PhaseNoise="1kHz:-80, 10kHz:-100, 100kHz:-120, 1MHz:-140"
```

- Between points the mask is a straight line in dB against log-frequency. Below the first point, the
  first segment's slope continues. Beyond the last point it is `PhaseNoiseFloor` (dBc/Hz), or the
  last point's level when that is not set.
- The run turns the source's phase by a random φ(t) whose spectrum is the mask: by φ at its
  fundamental and k·φ at its k-th harmonic. An LO with a mask mixes a blocker's energy into the
  wanted channel, which is reciprocal mixing.
- An envelope represents offsets up to half its sample rate. Mask points beyond that cannot be
  simulated, and the run says so once, naming the first such offset.
- Harmonic balance, loadpull and S-parameters run at steady state and ignore the mask. `check`
  reports it as information on those analyses.

**Seed.** The same `Seed` gives the same noise on every run and on every computer, and a run split
into parallel segments draws the same noise as one run. Every point of a parametric sweep uses the
same seed, so the differences between points come from the circuit; `SeedPerPoint=yes` gives each
point its own.

### Signal quality: EVM, FSK error and the constellation {#envelope-evm}

At every IProbe, each carrier that carries symbols is demodulated from the probe's voltage and
compared with what the source sent. NMSE measures the waveform; these measure what a receiver reads.
Each modulation is measured the way its receiver reads it:

| Carrier | Measured as | Picture |
|---|---|---|
| LTE, NR | EVM over every subcarrier of every OFDM symbol, as the [Signal Designer](signal-designer.html) measures the source's own signal | constellation |
| DBPSK | EVM at each bit centre, where a bit peaks at ±1 and its neighbours are zero | constellation |
| FSK, GFSK, GMSK | the frequency at each bit centre against the frequency sent: FSK error, deviation and offset | frequency at each bit |
| CSS | de-chirped: each chirp times the conjugate of the chirp sent, with one gain per chirp, as a receiver reads each symbol on its own | — |

**LTE, NR and DBPSK** (axes branch, carrier):

| Cube | Meaning |
|---|---|
| `EVM_pct`, `EVM_dB` | RMS EVM, in percent and in dB |
| `EVMpeak_pct` | the largest error, over the RMS of the ideal points |
| `MER_dB` | −20·log10(EVM) |
| `Const` (branch, carrier, symbol) | the received points, after the delay and gain are removed (at most 6000 per carrier) |
| `ConstRef` (carrier, symbol) | the ideal point at each of those |

**FSK:**

| Cube | Meaning |
|---|---|
| `FSKerr_pct` | the RMS frequency error at the bit centres, over the nominal deviation, after the deviation and the offset are fitted |
| `FreqDev_Hz` | the deviation measured: the fitted scale times the nominal one |
| `FreqOffset_Hz` | the carrier frequency offset measured |
| `FreqPts` (branch, carrier, bit) | the frequency at each bit centre, offset removed |
| `FreqRef` (carrier, bit) | the frequency sent there |

**CSS:** `CSSerr_pct`, the square root of the energy outside each symbol's own FFT bin over the
energy in it, after de-chirping, in percent. FSK and CSS have constant envelopes, so a constellation
would be a circle; these say more.

- **How it is measured.** The probe's envelope is brought back to the carrier's own sample rate and
  its delay is fitted against the signal the source played. OFDM is then cut into symbols, each
  transformed after its cyclic prefix, with one complex gain fitted against the ideal points. DBPSK,
  FSK and CSS are first filtered to their channel, and so is the reference they are compared with,
  so the measurement bandwidth is the channel's. There is no per-subcarrier equaliser, so the in-band
  ripple of a filter counts as error, as it does in NMSE.
- **Through a matched pad** the error is the source signal's own: the Signal Designer's EVM for LTE
  and NR (from clipping and shaping), and zero for DBPSK, FSK and CSS.
- **The record.** A run of whole records (`Stop` a multiple of the source's record, with a warm-up)
  measures every symbol. A shorter run measures the symbols, bits or chirps it holds whole, apart
  from a few samples at each end. A run shorter than one has no result, and the run says so.
- **Other carriers.** CW, a pulse and an imported I/Q file carry no symbols: their cells are blank,
  the run names them once, and NMSE remains their measure. Each set of cubes is written only when a
  carrier of its kind is in the run, and a run with none writes none of them.

**Distortion and noise (`EvmSplit=yes`).** With `Noise=yes` or a `PhaseNoise` mask, EvmSplit runs the
same record a second time with every noise source and mask off. Each error then splits into its
distortion and its noise, and the in-channel SNR is measured:

| Cube | Meaning |
|---|---|
| `EVMdist_pct`, `FSKerrDist_pct`, `CSSerrDist_pct` | the error without noise: the distortion |
| `EVMnoise_pct`, `FSKerrNoise_pct`, `CSSerrNoise_pct` | √(error² − distortion²): the noise's part |
| `SNR_dB` | the in-channel signal over the in-channel noise, the noise being the difference between the two runs |

For LTE, NR, DBPSK and CSS the noise's part is 1/√SNR. FSK reads its frequency, and noise moves the
frequency more than the amplitude, so its error is larger than that. The split is what decides
whether to fix linearity or noise. Over a short record the noisy run can read slightly less than
the noiseless one; the noise part is then 0 and the run says so. Without EvmSplit, `SNR_dB` is not
written: the spectrum's floor alone would mix distortion into it. `explain` states the second run,
and `check` says when there is nothing to split.

**In the Data Display.** **Signal quality** (on the toolbar, and **Open Signal Quality** on a result
file's menu) adds the picture, a table of every measure the result holds, and the output spectrum, at
the probe carrying the signal. The constellation plots `imag(Const[…])` against `real(Const[…])`,
points only, with **Equal X and Y scale** set in Axes Limits, so a circle of error looks like a
circle. FSK's picture is the frequency at each bit, received over sent. CSS has the table and the
spectrum.

## Parametric Sweep {#parametric-sweep}

**Computes:** nothing new on its own — it *wraps* another analysis and re-runs it across one or
more variables (drive power, a bias voltage, frequency, any user variable). Nest sweeps to cover
several variables; the innermost analysis is what each point solves.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Var</td><td>The variable to sweep (e.g. <code>Pin</code>, <code>RFfreq</code>).</td></tr>
      <tr><td>Start / Stop</td><td>Sweep endpoints (units accepted).</td></tr>
      <tr><td>Step <em>or</em> Npts</td><td>Step size, or a point count; linear or logarithmic.</td></tr>
      <tr><td>Inner</td><td>The analysis (or inner sweep) this sweep wraps — how sweeps nest.</td></tr>
    </tbody>
    <caption>A whole sweep tree writes a single results file named after its innermost analysis.</caption>
  </table>

## Loadpull / Sourcepull {#loadpull}

**Computes:** figures of merit (Pout, gain, efficiency, PAE) as the load (or source) reflection
coefficient is swept over a grid on the Smith chart — the classic loadpull experiment — ready to
draw as **contours**. Harmonic terminations can be swept too (harmonic loadpull).

**Method:** a programmable [Tuner](components.html#tuner) presents each grid termination; a
harmonic-balance solve runs per point (with previous-point continuation for convergence); the FOMs
are collected into the DataSet over the Γ-grid. The Data Display fits a surface and extracts
contour lines; measured loadpull files (`.spl`/`.lpcwave`) plot identically.

<table class="param-table">
    <thead><tr><th>Setting</th><th>Meaning</th></tr></thead>
    <tbody>
      <tr><td>Tuned port / role</td><td>Load or source side, and which DUT port the tuner terminates.</td></tr>
      <tr><td>Γ grid</td><td>The set of reflection-coefficient points (a Smith-chart grid, typically within \|Γ\| ≤ 0.9).</td></tr>
      <tr><td>Tuned harmonic</td><td>Fundamental, or a specified harmonic for harmonic loadpull.</td></tr>
      <tr><td>Power / bias</td><td>The drive level and bias at which the pull is performed (often a power sweep too).</td></tr>
    </tbody>
  </table>

### Noise figure in loadpull and sourcepull {#lp-noise}

`Noise=yes` adds the noise figure to a loadpull, beside Pout, gain and PAE. Swept on the source side (`Sweep=Source`),
its contours with gain and PAE on the same chart show the trade an input match is chosen on.

```
analysis LP1 type=loadpull Tone=2.4 ToneUnit=GHz LoadTuner=TL SourceTuner=TS Grid="source.gam" Sweep=Source Noise=yes
```

At every drive level of every grid point that converges, circuitRF reads the [harmonic-balance noise](#hb-noise) on that
drive level's own large-signal waveform. `NF` [gridPoint, pinStep] (dB, SSB) is written with the other results, the
contour plot offers it as a metric, and a `.spl` export carries it as one more column. A `.spl` reader that predates the
`NF` column skips it and reads the rest.

| Key | Meaning | Default |
|---|---|---|
| `Noise` | `yes` adds `NF`. | `no` |
| `NoiseFreq`, `NoiseFreqUnit` | The output frequency the noise is read at. | 1 % above the tone |
| `NoiseIn` | The termination NF is referred to: a Tuner's name, or the `Num` of a Port, Term or P1Tone. | the source tuner |
| `NoiseOut` | The termination the noise is read at. | the load tuner |
| `NoiseSideband` | The input sideband: the signal comes in at `NoiseFreq + k·Tone`. | `0` |

**Why not at the tone itself.** Under drive, noise near the carrier is low-frequency noise that the drive carries up,
so how much of it there is depends on how close you look. 1 kHz off the tone, flicker noise and a high-impedance bias
network can add several dB that a signal 10 MHz away never sees. By default the noise is read 1 % above the tone, the
noise a small signal beside the drive sees. For a receive band, set `NoiseFreq` to it. A `NoiseFreq` exactly at the tone,
or at any half-multiple of it, is read 1e-6 of the tone above, and the run says so.

**The tuners are noiseless**, as ports are. The input termination is at T0 = 290 K. At the low drive levels of the
ladder, `NF` equals the [S-parameter noise analysis](#sp-noise) on the same terminations. A small-signal sourcepull's NF
contours are the circles `ns_circle` draws.

It costs about one Newton iteration per drive level. On a [pursuit](#loadpull-pursuit), the same keys add `NF` to its
follow-on loadpull. The search itself does not read noise.

### The termination grid file (`.gam`) {#gam-file}

The grid of terminations a loadpull visits lives in a small, human-readable text file with a
`.gam` extension — one termination per line. It is kept *out* of the netlist on purpose: a grid is
**data** (often hundreds of points), and a real tuner system works the same way, reading a
"pattern file" of impedance states to visit. A loadpull analysis **points its grid at a `.gam`
file**; a [Loadpull Pursuit](#loadpull-pursuit) can **write one** for you (its recommended
terminations).

Each point is either an **impedance** (Ω) or a **reflection coefficient Γ**, in one of three
column layouts. An optional header line (starting `#`) declares the form, the reference impedance,
and the layout; if you omit it, circuitRF assumes **impedance** and infers the layout from the
first data line. The parser is deliberately forgiving — blank lines and `;` or `#` comment lines
are skipped.

<table class="param-table">
    <thead><tr><th>Header token</th><th>Meaning</th><th>Default</th></tr></thead>
    <tbody>
      <tr><td><code>gamma</code> / <code>impedance</code></td><td>Values are reflection coefficients Γ, or impedances in Ω.</td><td><code>impedance</code></td></tr>
      <tr><td><code>Z0=&lt;value&gt;</code></td><td>Reference impedance for the Γ↔Z conversion.</td><td>50 Ω</td></tr>
      <tr><td><code>re_im</code></td><td>Two columns: real, imaginary.</td><td rowspan="3">inferred from the first line: a <code>j</code>/<code>i</code> marker ⇒ <code>re+j*imag</code>, else <code>re_im</code></td></tr>
      <tr><td><code>mag_ang</code></td><td>Two columns: magnitude, then angle in <strong>degrees</strong>.</td></tr>
      <tr><td><code>re+j*imag</code></td><td>One column, a complex literal (e.g. <code>80+j*10</code>, <code>0.5-j*0.3</code>).</td></tr>
    </tbody>
    <caption>Header tokens are case-insensitive and may appear in any order, e.g. <code># gamma Z0=50 mag_ang</code>.</caption>
  </table>

A Γ grid in magnitude/angle form (rings on the Smith chart):

```netlist
# gamma Z0=50 mag_ang
; concentric rings of load reflection coefficient
0.00    0
0.20    0
0.20   90
0.40    0
0.40   90
0.40  180
0.60    0
0.80   30
```

<div class="callout note">
    <span class="label">Multi-frequency grids</span>
    <p>One <code>.gam</code> file can hold a <strong>different grid per frequency</strong>, for frequency-swept
    loadpull. Start each block with a bare <code>freq=&lt;value&gt;&lt;unit&gt;</code> line
    (<code>Hz</code>/<code>kHz</code>/<code>MHz</code>/<code>GHz</code>/<code>THz</code>); every point below it
    belongs to that frequency until the next <code>freq=</code> line. circuitRF reads the block whose frequency
    is <strong>nearest</strong> the one being simulated. A file with no <code>freq=</code> line is a single grid
    used at <em>every</em> frequency.</p>
    <pre class="netlist"><code># impedance Z0=50 re+j*imag
freq=1.8GHz
80+j*10
60-j*5
freq=2.2GHz
85+j*5
70+j*0</code></pre>
  </div>

**Using one.** In the Loadpull editor, set the grid field to your `.gam` file (paths are resolved
relative to the netlist). **Getting one.** A Loadpull Pursuit with an `OutputGrid` path writes its
recommended terminations as a `.gam` — always in `impedance … re+j*imag` form, with a generated-by
header and a `# WARNING:` line if any near-non-convergent points were dropped. That output reads
straight back into a standard loadpull, including the multi-frequency layout for a frequency-swept
pursuit.

## Loadpull Pursuit {#loadpull-pursuit}

**Computes:** the load (or source) terminations that optimize a figure of merit — found
*automatically* by a query-minimizing search rather than by gridding the whole Smith chart. It
reports two optima at constant compression — **MXP** (maximum output power) and **MXE** (maximum
efficiency) — recommends a conjugate-match **source impedance** for each, and can run a focused
high-fidelity loadpull around them, all from one analysis.

**Why use it:** it automates the real-world PA loadpull procedure — find the optimum, then
loadpull a focused grid around it with a sensible source match — into a single unattended run.
Point it at a DUT whose optimal terminations are unknown, run, and come back to a complete
loadpull dataset already concentrated at the right terminations. It is repeatable (great for
characterizing many devices with one netlist), avoids wasted query points far from the optima, and
avoids non-convergent terminations.

**It complements, it doesn't replace, a standard loadpull.** Pursuit finds the useful terminations
and the region of interest; a standard loadpull then shows the performance tradeoffs across that
region. Pursuit shares all [Loadpull](#loadpull) settings *except the Γ grid* (it generates
terminations rather than reading them), plus the keys below.

### How a pursuit run works

<ol class="steps">
    <li><strong>Search</strong> — a steepest-ascent search in the VSWR plane finds MXP, then MXE (seeded from
      MXP, so the second search is cheap). Each "query" is one drive-to-compression harmonic-balance run;
      queries are cached so MXE reuses MXP's data.</li>
    <li><strong>Recommend</strong> — builds a focused-around-the-optima set of recommended terminations (dense
      near MXP/MXE, sparse further out) and a conjugate-match source impedance for each optimum.</li>
    <li><strong>Loadpull</strong> — optionally runs a standard loadpull over those recommended terminations,
      using a recommended source match, producing the high-fidelity contour data.</li>
  </ol>

### Search method

- **SteepestAscent** (default) — fits the gradient and ascends along a fixed direction, shrinking
  the step on rejection; a final polynomial refinement pins the optimum. Fast and adequate for the
  near-1-D real-axis optima typical of PA loadpull.

- **IteratedQuadratic** — a trust-region search that re-fits the local curvature at every step, so
  it follows a curved 2-D ridge instead of committing to one direction. Reuses the same query cache,
  so its query count stays comparable.

### Pursuit settings

<table class="param-table">
    <thead><tr><th>Key</th><th>Meaning</th><th>Default</th></tr></thead>
    <tbody>
      <tr><td>EffType</td><td>The MXE (efficiency) criterion: drain efficiency <code>DE</code> or <code>PAE</code>.</td><td>DE</td></tr>
      <tr><td>SearchMethod</td><td><code>SteepestAscent</code> or <code>IteratedQuadratic</code> (above).</td><td>SteepestAscent</td></tr>
      <tr><td>ZsourceOBO</td><td>Output back-off (dB from compression) at which the input impedance is sampled for the auto-Zsource report. Granularity set by <code>PinStep</code>.</td><td>5</td></tr>
      <tr><td>VSWR1 (focused)</td><td>Focused box size (VSWR-circle radius) around MXP and MXE — the dense sampling region.</td><td>1.5</td></tr>
      <tr><td>VSWR1_resolution</td><td>Grid spacing (N×N samples) inside each focused box.</td><td>4</td></tr>
      <tr><td>VSWR2 (broad)</td><td>Broad box size (VSWR-circle radius) for the surrounding coarse grid.</td><td>3</td></tr>
      <tr><td>VSWR2_resolution</td><td>Grid spacing (N×N samples) for the broad box.</td><td>4</td></tr>
      <tr><td>keepNonconvergingPoints</td><td>If false, drop recommended points near terminations found non-convergent during the search (and warn).</td><td>false</td></tr>
      <tr><td>nonconvergentVSWR</td><td>Exclusion radius (VSWR) around known non-convergent terminations.</td><td>1.05</td></tr>
      <tr><td>OutputGrid</td><td>Path to write the recommended terminations as a <a href="#gam-file"><code>.gam</code> file</a>. Absent → no file written.</td><td>none</td></tr>
      <tr><td>CreateLoadpullResult</td><td>Run the follow-on standard loadpull over the recommended terminations. Independent of <code>OutputGrid</code>.</td><td>on</td></tr>
      <tr><td>LoadpullResultZsource</td><td>Source match the follow-on loadpull uses: <code>MXE</code>, <code>MXP</code>, or <code>None</code> (use the Source Tuner's own Z1).</td><td>MXE</td></tr>
    </tbody>
    <caption>The recommended terminations always exist in memory; <code>OutputGrid</code> controls only the file, <code>CreateLoadpullResult</code> controls only the follow-on simulation — the two are orthogonal.</caption>
  </table>

<div class="callout note">
    <span class="label">Auto-Zsource — a recommended source match</span>
    <p>After each optimum is found, pursuit backs the drive off by <code>ZsourceOBO</code> dB, computes the DUT
    input impedance Zin there, and reports <strong>Zsource = Zin*</strong> (the conjugate match) — a
    ready-to-use input-match target, per optimum, without a separate sourcepull.</p>
  </div>

<div class="callout warn">
    <span class="label">The DUT must compress</span>
    <p>MXP/MXE are defined <em>at compression</em>. If the DUT does not reach <code>Compression</code> within
    <code>PinMax</code>, the search aborts with a clear message (raise <code>PinMax</code> or check
    bias/load) — it never silently raises your <code>PinMax</code> cap.</p>
  </div>

<p class="small">Full design + algorithm: <code>docs/design/loadpull_pursuit.md</code> (in the repo).</p>

## Multi-Tone Harmonic Balance (2 to 6 tones) {#two-tone}

**Computes:** the steady-state spectrum of a nonlinear circuit driven by **several carriers at
once** — every harmonic *and* every intermodulation (mixing) product. Two tones is the standard
test for **linearity**: third-order intermodulation (IM3), intercept points, spectral regrowth,
and the asymmetry between the lower and upper IM products. Three or more tones is the natural
setting for **mixers** (RF, LO and their products) and for multi-carrier work. Two tones is enough
for an [ideal mixer](components.html#mixer) on its own — one for the RF, one for the LO — and the
converted power reads off the IF port at the mixing product you want.

Multi-tone is not a separate analysis — it is [Harmonic Balance](#harmonic-balance) with more than
one tone. Everything from the single-tone chapter (the linear/nonlinear split, the Newton solve,
drive continuation, all the convergence knobs) still applies. This chapter covers only what is
different.

Up to **six** tones are supported. Everything below is written for two because that is the common
case, but it holds at any tone count: read "(k₁,&nbsp;k₂)" as the full list
"(k₁,&nbsp;…,&nbsp;k<sub>T</sub>)". The one thing that changes with tone count is how far you can
push **Max mix order** — see [How many tones, at what order](#multi-tone-size).

### Setting it up {#two-tone-setup}

In the Harmonic Balance analysis editor, switch the **Single&nbsp;/&nbsp;Multi-tone** toggle to
**Multi-tone**. Three things change:

- The single **Tone&nbsp;(f₀)** field is replaced by a **tone list**, starting with
  **Tone&nbsp;1** and **Tone&nbsp;2**. **+&nbsp;Tone** adds another (up to six); the **×** on a row
  removes it, down to a minimum of two.

- **Max harmonics** is replaced by **Max mix order** — the truncation that bounds the
  two-dimensional mixing spectrum (see below).

- Every tone accepts an expression, so a common pattern is `Tone&nbsp;1 = RFfreq − Spacing/2`,
  `Tone&nbsp;2 = RFfreq + Spacing/2` with `RFfreq` and `Spacing` as variables you can sweep.

- Beside **Max mix order** the editor shows how many mixing products the current tone count and
  order retain, and warns when that is over the limit — so you can see the cost of an order before
  you run it.

<div class="callout note">
    <span class="label">The analysis owns the tones, not the source</span>
    <p>The fundamentals <strong>come from the HB analysis</strong> (the tone list, written as
    <code>NumFreqs=N&nbsp;Tone[1]=…&nbsp;Tone[N]=…</code> in the netlist), not from the source. A
    <a href="components.html#pntone">PnTone</a> (or <code>V_nTone</code>) just supplies power at those
    frequencies. Every source tone must land <strong>exactly</strong> on the analysis grid — an off-grid drive
    frequency is rejected at setup with a clear message rather than producing silent garbage.</p>
  </div>

### The mixing grid and mixing order {#two-tone-grid}

With two fundamentals, a spectral line is no longer indexed by a single harmonic number. Each line
is a pair of integers **(k₁,&nbsp;k₂)** sitting at the physical frequency

<p style="text-align:center"><code>f = k₁·f₁ + k₂·f₂</code></p>

and its **mixing order** is `m = |k₁| + |k₂|`. The carriers are (1,&nbsp;0) and (0,&nbsp;1) at
order 1; a third-order intermod such as 2f₁−f₂ is (2,&nbsp;−1) at order 3. Solving for every
possible (k₁,&nbsp;k₂) is infinite, so circuitRF keeps a finite set bounded by **Max mix order**:

<p style="text-align:center"><code>retain (k₁, k₂)&nbsp; ⟺ &nbsp;|k₁| + |k₂| ≤ MaxMixOrder</code></p>

That inequality is a **diamond** (a rotated square) in the (k₁,&nbsp;k₂) plane — hence "diamond
truncation." A diamond rather than a full rectangular box because the high–high corner products
(e.g. 5f₁+5f₂) carry no meaningful energy in a real intermod test, while every low-order product
that *does* matter is inside the diamond. `MaxMixOrder` is the two-tone analog of single-tone
`MaxHarm`: order 5 retains products down to IM5. The number of products grows *quadratically* with
the order (≈&nbsp;2·order² lines), so a two-tone run is much larger than a single-tone run at the
same order — order 5 yields dozens of mixing products versus a handful of harmonics.

With **T** tones the same rule reads `retain (k₁,&nbsp;…,&nbsp;k<sub>T</sub>) ⟺ |k₁| + … +
|k<sub>T</sub>| ≤ MaxMixOrder`, and the diamond becomes a T-dimensional one. There is still a
single order knob — no per-tone limits.

### How many tones, at what order {#multi-tone-size}

The retained product count is what actually costs you, and it grows steeply with tone count — much
faster than the quadratic growth two tones would suggest. The default `MaxMixOrder&nbsp;=&nbsp;5`
is sized for two tones; **lower it as you add tones**:

<table class="param-table">
    <caption>Retained mixing products. Configurations over the 600-product limit are refused, with a message
      naming the largest order that fits.</caption>
    <thead><tr><th>Tones</th><th>order 2</th><th>order 3</th><th>order 4</th><th>order 5</th></tr></thead>
    <tbody>
      <tr><td>2</td><td>7</td><td>13</td><td>21</td><td>31</td></tr>
      <tr><td>3</td><td>13</td><td>32</td><td>63</td><td>116</td></tr>
      <tr><td>4</td><td>21</td><td>65</td><td>161</td><td>341</td></tr>
      <tr><td>6</td><td>43</td><td>189</td><td>645 <em>(refused)</em></td><td>1827 <em>(refused)</em></td></tr>
    </tbody>
  </table>

Practical starting points: **order 5 at 2–3 tones**, **order 4 at 4 tones**, **order 3 at 5–6
tones**. On a single-FET PA, six tones at order 3 solves in a few seconds.

<div class="callout note">
    <span class="label">Too large is refused before the run starts, not after</span>
    <p>If the tone count and mixing order together ask for more products than the solver will take, circuitRF
    refuses <strong>immediately</strong> — not after a long solve — and the message tells you the largest
    <code>MaxMixOrder</code> that <em>would</em> work at that tone count. The editor shows the same number
    live beside the field, so you normally see it before you press Run.</p>
  </div>

<div class="callout note">
    <span class="label">Equally spaced tones put two products at the same frequency</span>
    <p>With evenly spaced carriers — the usual multi-carrier stimulus — different mixing products can land on
    the <em>same</em> physical frequency. At 1.99&nbsp;/&nbsp;2.00&nbsp;/&nbsp;2.01&nbsp;GHz, both
    (1,&nbsp;−1,&nbsp;0) and (0,&nbsp;1,&nbsp;−1) sit at 10&nbsp;MHz. They stay <strong>separate</strong>
    products with their own phasors, and the spectrum plot shows both stems at the same position — they are
    not added together. Use the marker readout, which names the product, to tell them apart.</p>
  </div>

<table class="param-table">
    <thead><tr><th>(k₁, k₂)</th><th>Order</th><th>Frequency</th><th>What it is</th><th>At f₁=1.99, f₂=2.01 GHz</th></tr></thead>
    <tbody>
      <tr><td>(0, 0)</td><td>0</td><td>0</td><td>DC / baseband</td><td>0</td></tr>
      <tr><td>(1, −1)</td><td>2</td><td>f₁ − f₂</td><td>IM2, the tone spacing (baseband)</td><td>0.02 GHz</td></tr>
      <tr><td>(1, 0)</td><td>1</td><td>f₁</td><td>Carrier 1</td><td>1.99 GHz</td></tr>
      <tr><td>(0, 1)</td><td>1</td><td>f₂</td><td>Carrier 2</td><td>2.01 GHz</td></tr>
      <tr><td>(2, −1)</td><td>3</td><td>2f₁ − f₂</td><td><strong>IM3 lower</strong></td><td>1.97 GHz</td></tr>
      <tr><td>(−1, 2)</td><td>3</td><td>2f₂ − f₁</td><td><strong>IM3 upper</strong></td><td>2.03 GHz</td></tr>
      <tr><td>(3, −2)</td><td>5</td><td>3f₁ − 2f₂</td><td>IM5 lower</td><td>1.95 GHz</td></tr>
      <tr><td>(−2, 3)</td><td>5</td><td>3f₂ − 2f₁</td><td>IM5 upper</td><td>2.05 GHz</td></tr>
      <tr><td>(2, 0)</td><td>2</td><td>2f₁</td><td>2nd harmonic of carrier 1</td><td>3.98 GHz</td></tr>
      <tr><td>(1, 1)</td><td>2</td><td>f₁ + f₂</td><td>2nd-harmonic sum band</td><td>4.00 GHz</td></tr>
    </tbody>
    <caption>The IM "order" is the sum of the integer coefficients: IM3 = |2|+|−1|, IM5 = |3|+|−2|. Choose
    closely-spaced tones (small Δ) so the IM3 and IM5 products fall right beside the carriers.</caption>
  </table>

<div class="callout note">
    <span class="label">Reading the spectrum in the Data Display</span>
    <p>A multi-tone result plotted as a spectrum uses a <strong>mixIndex</strong> axis: each stem is a mixing
    product, drawn at its physical frequency, labelled with its <strong>(k₁,&nbsp;k₂)</strong> tag — or
    <strong>(k₁,&nbsp;…,&nbsp;k<sub>T</sub>)</strong> at more tones. The spectrum is shown
    <strong>single-sided</strong> — the negative-frequency products (the conjugate halves, e.g. f₁−f₂
    and f₂−f₁) are folded onto their positive frequency, so each product appears once.</p>
    <p>Arrow keys step a marker in <strong>frequency</strong> order, which is what you want with tightly
    spaced IM products. Everything here works the same at any tone count.</p>
  </div>

### Measuring intermodulation (IMD2 / IMD3) {#two-tone-meas}

A [measurement](measurements.html) reads a mixing product out of the result by its **(k₁,&nbsp;k₂)
tag** — at three tones the tag simply has three entries, e.g. `HB1.V("Vout",&nbsp;"(1,1,-1)")` for
the triple-beat product f₁+f₂−f₃. The classic figures of merit are the intermodulation ratios in
**dBc** — an IM product relative to its carrier:

```netlist
IMD2 = dB( HB1.V("Vout", "(1,-1)") ) - dB( HB1.V("Vout", "(1,0)") )   dB
IMD3 = dB( HB1.V("Vout", "(2,-1)") ) - dB( HB1.V("Vout", "(1,0)") )   dB
```

Replace `HB1` with your analysis name and `"Vout"` with your output node. IMD2 uses the baseband
product (1,−1) = f₁−f₂; IMD3 uses the lower third-order product (2,−1) = 2f₁−f₂ (use (−1,2),
referenced to the (0,1) carrier, for the upper IM3). To get the curve *vs Pin*, sweep the drive
power (a parametric sweep over the variable your source `Pavl` references) — the same expression
then returns IMD vs Pin instead of a single value.

<div class="callout note">
    <span class="label">Two ways to address a product — use the accessor for IMD</span>
    <p>The <strong>accessor</strong> <code>HB1.V("Vout", "(1,-1)")</code> names the node and the product tag and
    <strong>keeps any swept axes automatically</strong>, so the same expression works whether or not you have
    added a Pin sweep — one value without it, a curve over Pin with it. The <strong>bracket</strong> form
    <code>HB1.V[:,&nbsp;"Vout",&nbsp;"(1,-1)"]</code> is positional: one token per cube axis (a leading
    <code>:</code> to keep the Pin sweep), so it must be re-edited when a sweep is added or removed. Prefer the
    accessor for IMD metrics you keep; reach for the bracket when copy-pasting from a trace card. See
    <a href="measurements.html#refs">Referencing analysis results</a>.</p>
  </div>

### Source impedance and harmonic terminations {#two-tone-z}

With dozens of products spread across the spectrum, each one needs a source termination. circuitRF
assigns it **by frequency**, not by mixing order. It defines a band-center fundamental — for two
tones the average `f_c = (f₁ + f₂) / 2` — and places each product in the harmonic band nearest its
frequency:

<p style="text-align:center"><code>band n = round( |f| / f_c )</code>&nbsp;→&nbsp;<code>Z[n]</code> if declared, else the shared <code>Z</code></p>

So band&nbsp;0 (|f|&nbsp;<&nbsp;f_c/2) is the baseband and uses `Z[0]`; band&nbsp;1 (around f_c)
holds both carriers and all the close-in IM3/IM5 products and uses `Z[1]` (or the default `Z`);
band&nbsp;2 (around 2·f_c) holds the second-harmonic products and uses `Z[2]`; and so on. The
decision is purely the product's frequency: as a high-order product such as (3,&nbsp;−1) drifts
down past the band-1/band-2 boundary it switches from `Z[2]` to `Z[1]` automatically — no order
threshold is hard-coded. This is the same harmonic-termination model as
[P1Tone](components.html#p1tone), and a [PnTone](components.html#pntone) presents its shared `Z`
and per-band `Z[k]` to the whole multi-tone spectrum this way.

<div class="callout note">
    <span class="label">Why baseband termination matters</span>
    <p>In single-tone HB the baseband is decoupled. In two-tone it is not — the IM2 product (1,&nbsp;−1) at
    f₁−f₂ lands at baseband, and its termination <code>Z[0]</code> feeds back into the carriers and the IM3
    levels. Studying source/load <strong>baseband-termination effects</strong> is a primary reason to run
    two-tone, so set <code>Z[0]</code> deliberately.</p>
  </div>

### What's different from single-tone {#two-tone-vs-single}

- **IM3 asymmetry.** The lower IM3 (2f₁−f₂) and upper IM3 (2f₂−f₁) generally have *different*
  levels in dBc, not just a phase difference — a direct consequence of the device's nonlinear
  I–V/Q–V behaviour and its loading. The full diamond resolves both, so the asymmetry is a real
  output of the run, not an artifact.

- **A fully coupled solve.** The two-tone result is *not* two single-tone solutions superimposed.
  The carriers, harmonics, IM products, and baseband are all unknowns in one Newton solve with a
  single conversion-matrix Jacobian — so the IM levels capture the true bidirectional coupling
  between the carriers and the baseband.

- **A bigger spectrum, a bigger cost.** Because the product count grows quadratically with
  `MaxMixOrder`, raise it only as far as you need: 5 captures IM5 (usually enough); higher captures
  IM7+ at noticeably more cost.

- **Commensurate tones.** f₁ and f₂ must be commensurate (a rational ratio) so every mixing
  product lands exactly on the grid and the waveform is exactly periodic — closely-spaced tones like
  1.99 / 2.01 GHz satisfy this naturally.

<p class="small">Full design + algorithm: <code>docs/design/harmonic-balance.md</code> and the
  harmonic-termination model in <code>docs/design/p1tone-harmonic-terminations.md</code> (in the repo).</p>


---

## TRL Calibration {#trl}

A TRL calibration finds the two **fixtures** between your instrument and the part you measured (the launches, probes or
connectors on each side), from three standards measured through those same fixtures. It writes them as two `.s2p` files,
which two [Deembed](components.html#deembed) blocks then take off any other measurement made through the same fixtures. It
solves no circuit.

Place three SnP blocks on a test bench, one for each measured standard, and add an analysis of type **TRL Calibration**:

- **Thru**: the two fixtures connected directly. The reference plane is the middle of the Thru.
- **Reflect**: the same high-reflection standard on both ports, port 1's in S11 and port 2's in S22. You only say whether it
  is an **open** or a **short**; its exact value is solved for, and reported.
- **Line**: a longer piece of line between the fixtures. Its impedance becomes the reference impedance of everything you
  de-embed, so make it the impedance you want to read results in.
- **Fixture A** and **Fixture B**: where the two files are written. They are resolved exactly as an SnP's File is, so the
  same text goes into the Deembed's File. Left empty, they are `<analysis name>_A.s2p` and `_B.s2p`.

Fixture A's port 1 is the instrument side and port 2 the part's. Fixture B's port 1 is the part's side and port 2 the
instrument's. So the chain reads left to right as the measurement did: `Port 1 — Deembed(A) — measured part — Deembed(B) —
Port 2`, with no flipping.

**The usable band.** TRL tells the two fixtures apart by how much longer the Line is than the Thru. Where that difference is
near 0° or 180°, the two standards are nearly the same and the answer is unreliable. A run warns about every point
outside **20° to 160°** (modulo 180°), naming the frequencies. One line covers 8:1. A line that is 90° longer at the middle
of your band, `f_mid = (f_low + f_high) / 2`, is a good choice; cover a wider band with a second line and a second
calibration.

The analysis's result holds `SA` and `SB` (the two fixtures, as S cubes), `LineDeg` (the Line's extra length over the Thru,
in degrees), `LineLossDb`, `Gamma` (the solved reflect) and `Usable` (1 inside the band, 0 outside), to plot like any other
result. A measurement reads a fixture's entry by port number, as it reads S: `abs(CAL.SA(2, 1))`.

```
SnP:THRU     t1 t2 File="measurements/thru.s2p"    NumPorts=2
SnP:REFLECT  r1 r2 File="measurements/reflect.s2p" NumPorts=2
SnP:LINE     l1 l2 File="measurements/line.s2p"    NumPorts=2
analysis CAL type=trl thru=THRU reflect=REFLECT line=LINE reflectType=short fixtureA="fixtures/A.s2p" fixtureB="fixtures/B.s2p"
```

From the command line, `circuitrf trl bench.csch` runs it ([`trl`](cli.html#trl)).

A Deembed whose fixture file a TRL analysis in the workspace writes, run before that analysis, stops naming the bench to
run first. The worked example is **TRL and De-embedding** (*File ▸ New ▸ Examples*): a calibration from synthetic
standards, a filter de-embedded with its results and laid over the filter itself, and a GaN FET de-embedded to its
intrinsic plane.

---

## Tuning {#tuning}

The **Tuning** panel puts a slider on any value you choose and re-simulates as you move it, with every
open Data Display following along. Nothing in the schematic changes until you Push. The whole chapter —
choosing values, ranges, complex values by their parts, presets, snapshots — is [Tuning](tuning.html).

---

## Optimizer {#optimizer}

The **Optimizer** panel searches the values you choose for ones that meet your goals, each goal a limit
on an expression over one analysis, with every open Data Display following the best point found so far.
Variables, goals, the algorithms and when to use each, railed variables and snap and polish are in
[Optimization](optimization.html).

---

## Running an analysis from the command line {#cli}

Everything above is driven from the GUI's Run button, and everything above also runs headless. The
command-line driver takes a `.cnl` netlist — hand-authored, or extracted from a schematic — and runs
one analysis:

```text
circuitrf sparam <file.cnl> [--freq start:stop:step] [-o out.sNp]
circuitrf dc     <file.cnl>
circuitrf hb     <file.cnl>       harmonic balance; runs the sweep if one wraps it
circuitrf lp     <file.cnl>       loadpull over the directive's Gamma grid
circuitrf lpp    <file.cnl>       loadpull pursuit: searches for MXP / MXE
circuitrf em     <file.cem>       electromagnetic extraction of the layout it names
circuitrf elab   <file.cnl>       dump the elaborated netlist
```

From a source checkout, put `dotnet run --project src/Cli --` where `circuitrf` appears.

**The full reference for every verb, its options, its output and its exit codes is
[The Command Line](cli.html)** — including a worked EM run from an empty folder. This section covers
only the part that is about *analyses* rather than about the driver.

### Harmonic balance from the command line {#cli-hb}

```netlist
dotnet run --project src/Cli -- hb hero2.cnl -o out.mat
```

That runs the netlist's harmonic-balance analysis — **single- or multi-tone; the same verb does both**
— prints the spectra as tables, and writes `out.mat`.

| Option | What it does |
|---|---|
| `-a`, `--analysis <name>` | Which analysis to run. Optional when the file declares only one HB chain. |
| `--set <var=expr>` | Override a global variable **before elaboration**, so anything derived from it follows. Repeatable. |
| `--maxharm K` | Override `MaxHarm`. |
| `--maxmix M` | Override `MaxMixOrder` (multi-tone only). |
| `--tol t`, `--max-iter N` | Override the convergence tolerance and the iteration cap. |
| `--rows N`, `--all` | How much of each printed table to show. Default is a truncated head. |
| `--diag` | Engine convergence diagnostics to stderr. |
| `-o`, `--export <path>` | Export the results. The **extension picks the format**: `.mat`, `.npy` or `.txt`. |
| `--kits <dir>` | A folder of installed kits, for externally-supplied device models. Repeatable; valid on any verb. |

```netlist
dotnet run --project src/Cli -- hb hero5.cnl --set Pavl_dbm=0 -o hero5.txt
```

<div class="callout note">
<span class="label">The sweep runs, whichever name you give</span>
<p>When a <a href="#parametric-sweep">parametric sweep</a> wraps an analysis, <b>the sweep</b> is what
runs — and naming the inner analysis with <code>-a</code> is <strong>promoted</strong> to its outermost
enabled wrapper, with a line on stderr saying so. That promotion exists because running the inner
analysis alone gives a converged, plausible, complete-looking result with the sweep axis silently
missing. It is the same rule for <code>hb</code>, <code>lp</code> and <code>lpp</code>: a
frequency-swept loadpull has exactly that shape.</p>
</div>

### Loadpull and pursuit from the command line {#cli-lp}

```netlist
dotnet run --project src/Cli -- lp  hero3.cnl  --pin -20:1:15 -o hero3.spl
dotnet run --project src/Cli -- lpp hero3B.cnl --out-grid found.gam -o hero3B.npy
```

`lp` prints **one row per Γ grid point** — where it was, how it stopped, and its figures of merit at
the last converged, non-tickle drive step — rather than the raw `[gridPoint × driveStep]` cubes.
`lpp` prints its MXP and MXE optima first, then the follow-on grid. Both take the HB options above,
plus `--pin`, `--compression`, and one grid option each: `--grid` for `lp`, `--out-grid` for `lpp`.
`lp` also exports `.spl` and `.lpcwave`, the loadpull interchange formats the Data Display reads back.
[Full detail](cli.html#lp).

### Electromagnetic extraction from the command line {#cli-em}

```netlist
dotnet run --project src/Cli -- em Amp.cem
```

`em` takes a [`.cem` EM setup](em-setup.html) and needs no other arguments — the layout and the
technology resolve by walking up to their own workspaces, exactly as they do in the GUI. It writes the
Touchstone and the diagnostics `.npy` **where the Simulate button writes them**, so a schematic's SnP
reference survives a headless re-run. [Full detail, with a worked example](cli.html#em).

### Measurements, and why headless is the regression harness {#cli-measure}

The `measure` lines on the TestBench are evaluated exactly as the GUI evaluates them, and a
measurement that fails to evaluate is reported on stderr and the run continues. **A `.cnl` that works
headless works when opened**, which is what makes the command line usable as a regression harness:
the two paths share the elaborator, the engines and the measurement evaluator.

---

<p class="small">See also: <a href="netlist.html">the netlist format</a> (how analyses are written) ·
  <a href="components.html">Components</a> · <a href="plot-types.html">Plot types</a> ·
  <a href="measurements.html">Measurements</a> · <a href="npy-export.html">Results &amp; data export</a> ·
  <a href="cli.html">The command line</a>.</p>
