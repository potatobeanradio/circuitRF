---
title: Model Composer
slug: reference/model-composer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Model Composer
lede: A nonlinear FET model, or a passive one, built from measured or simulated data — in a document you can close and pick up again.
keywords: model extraction, device modeling, FET model, GaN HEMT, extrinsic network, de-embedding, pad de-embedding, cold-FET, multi-bias, .ds, binary simulation dataset, bias plane, intrinsic, passive model, topology, .cmc, .cmodel, Verilog-A export, .va, traps, trapping, gate lag, drain lag, current collapse, knee walkout, capture, emission, peak detector
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#steps">The steps</a></li>
<li><a href="#kinds">Data kinds and their conditions</a></li>
<li><a href="#preparing">Preparing the data</a></li>
<li><a href="#planes">Reference planes</a></li>
<li><a href="#six-pin">The six-pin network</a></li>
<li><a href="#defaults">Methods and their defaults</a></li>
<li><a href="#ann-training">How an ANN is trained</a></li>
<li><a href="#conservation">Charge conservation</a></li>
<li><a href="#temperature">Temperature</a></li>
<li><a href="#trap-model">How traps work in the model</a></li>
<li><a href="#trap-data">Traps, and what pulsed data allows</a></li>
<li><a href="#scaling">Periphery scaling</a></li>
<li><a href="#passive-models">Passive models</a></li>
<li><a href="#example">The example</a></li>
<li><a href="#open">Opening a document</a></li>
<li><a href="#window">The window</a></li>
<li><a href="#tasks">Running, progress and Cancel</a></li>
<li><a href="#data">The Data step</a></li>
<li><a href="#pads">The Pads step</a></li>
<li><a href="#network">The Network step</a></li>
<li><a href="#method">The method picker</a></li>
<li><a href="#current">The Current step</a></li>
<li><a href="#charge">The Charge step</a></li>
<li><a href="#thermal">The Thermal step</a></li>
<li><a href="#traps">The Traps step</a></li>
<li><a href="#check">The Check step</a></li>
<li><a href="#validate">The Validate step</a></li>
<li><a href="#report">The Model Validation Report</a></li>
<li><a href="#export">The Export step</a></li>
<li><a href="#veriloga">Verilog-A export</a></li>
<li><a href="#plots">Plots</a></li>
<li><a href="#passive">Passive documents: Data and Topology</a></li>
<li><a href="#undo">Undo</a></li>
<li><a href="#headless">Headless</a></li>
<li><a href="#references">References</a></li>
</ol>
</nav>

## What it does {#what}

Model Composer builds a model from data. For a transistor it builds a **nonlinear FET model**, a `.cmodel` that a
schematic places as a [`ComposedFET`](components.html#composedfet) and that DC, S-parameters, harmonic balance and
loadpull all use. For an N-port it builds a **passive model**, an ordinary cell of R, L, C and mutuals with the fitted
values as its parameters.

The data can be measured (DC and pulsed I–V, multi-bias S-parameters, pulsed S-parameters, large-signal sweeps and
loadpull) or simulated (tables of currents, admittances and intrinsic voltages written by another simulator). The work
is kept in a `.cmc` document. It names the data where it lies, records the conditions it was measured under, and holds
every setting and every result, so a model can be rebuilt, checked and changed later.

A transistor model has four parts, called **constituents**: the drain current `Id`, the gate current `Ig` (a gate–source
and a gate–drain branch), and the gate and drain charges `Qg` and `Qd`. Each one is an ANN, a table or an equation. They
sit inside an extrinsic network of pads, inductances and resistances. A model can add self-heating, [trap states](#trap-model), or
a simpler DC/RF dispersion. A `.cmodel` can also be written without the composer, from equations, a table or a network
trained elsewhere: [Writing a .cmodel](cmodel.html).

**Starting from another simulator's design kit?** [AN-04 — A nonlinear FET model from a reference
kit](../app-notes/an04-model-from-a-reference-kit.html) walks the whole flow with block diagrams: which runs to make in
the source simulator, the plane to sample at, what each step does with the result, and how to validate against the kit.

## The steps {#steps}

An active document runs through the steps below, in order. Each step's result is stored in the `.cmc` and stamped with
the data and network it came from. When either changes, the step is marked stale.

| Step | What it does |
|---|---|
| Data | Imports each data set and states its conditions. The data stays in its files and is cached beside the document. |
| Pads | Removes the probe pads: open and short dummies, a Touchstone two-port per side, or a two-pin schematic per side. |
| Network | Chooses the extrinsic network and fits its values. Every data set is then re-referenced to the intrinsic plane. |
| Current | Fits `Id` and `Ig`. |
| Charge | Fits `Qg` and `Qd`, or, with no S data, writes them from typed capacitances. |
| Thermal | Adds self-heating: Rth (typed, or extracted from pulsed I–V) and the thermal time constants. |
| Traps | Chooses a trap model and its coupling: fits the currents again with the trap states as inputs, or fits the strengths of a threshold shift from multi-bias S. |
| Noise | Optional. Fits the drain noise temperature `Td`, and the gate temperature `Tg` when the data supports both, from measured noise parameters. A document with no noise data exports a complete model without noise. |
| Check | Checks that the charges conserve charge. |
| Validate | Compares the model with every data set at the plane it was measured at. |
| Export | Writes the `.cmodel`. |

**Fit all** (`model fit --step all`) runs every step the document needs, in this order: the network if any value is
free, then thermal, current, traps when a trap model is chosen, charge, and noise when the Noise step lists data. With no S data, the network is held at its
values (`network: held at its values — no S data to fit to`) and the charge step writes the typed capacitances; a fit
of the network step alone still refuses.

## Data kinds and their conditions {#kinds}

| Kind | Holds | Read from |
|---|---|---|
| `DcIv` | Terminal voltages and currents at DC | CSV, `.mat`, `.npz`, `.ds`, `.npy` |
| `PulsedIv` | The same, sampled during a pulse from a stated quiescent point | CSV, `.mat`, `.npz`, `.ds`, `.npy` |
| `SParams` | Two-port S at each bias, and the bias currents when the files state them | A folder of Touchstone files |
| `PulsedSParams` | S sampled during a pulse | A folder of Touchstone files |
| `SimulatedTables` | Currents, and an admittance or S table, a capacitance table, or both; optionally intrinsic voltages | `.mat`, `.npz`, `.ds`, `.npy` |
| `LargeSignal` | A power sweep, a two-tone sweep or a loadpull file; used for validation only | CSV, `.spl`, `.lpcwave` |
| `PassiveSParams` | An N-port block, or a family of blocks over a geometry value | Touchstone files |

A Touchstone set takes its bias from each file's name or comment lines through a pattern such as
`Vgs{vgs}_Vds{vds}_Id{id:mA}`. The fields are `{vgs}`, `{vds}`, `{T}`, `{id}`, `{ig}` and, for a passive family,
`{geometry}`.

Each data set states the conditions it was measured under. Every condition is optional when the set is imported, and the
step that needs one names it when it is missing.

| Condition | Meaning |
|---|---|
| `AmbientC` | The chuck temperature, °C |
| `ReferencePlane` | Where the bias voltages were measured: `instrument`, `probe`, `device` or `intrinsic` ([below](#planes)) |
| `BiasPathOhms` | For `instrument`: the bias-path resistance at the gate, drain and source |
| `Quiescent` | A pulsed set's quiescent bias (`Vgs`, `Vds`, and `Id` when it conducts) |
| `PrePulse` | A double pulse's trap-setting pulse: its `Vgs`, `Vds`, width and gap before the measuring pulse |
| `PulseWidth`, `Period`, `SampleDelay` | The pulse timing, seconds; the sample delay is from the pulse's start to the sample |
| `TrapState` | How pulsed points beyond the trap-setting bias are used: `auto`, `setting` or `extreme` ([below](#trap-data)) |
| `SoakTime`, `Compliance` | How long the bias was held, and the instrument's current limits |
| `LargeSignal` | For a large-signal set: what it measured, the frequency, the bias and the terminations |
| `Note` | Free text, kept with the set |

## Preparing the data {#preparing}

What data a model needs, how to sample it in another simulator, and the file formats each kind is read from — each with
an example to copy — are on their own page: [Model Data](model-data.html). It covers
[what a model needs](model-data.html#needs), [bias ranges and steps](model-data.html#ranges),
[running another simulator](model-data.html#from-simulator), [the file formats](model-data.html#import-formats),
[checking a file before importing it](model-data.html#dry-run) and [what the import checks](model-data.html#import-checks).

## Reference planes {#planes}

The composer always models in **intrinsic** voltages, between the core's own gate, drain and source. Each data set states
the plane its voltages were measured at:

| Plane | Means |
|---|---|
| `instrument` | At the source-measure unit; the bias-path resistances are subtracted |
| `probe` | At the probe tips; the pads are removed, then the network |
| `device` | Pads already removed; the network is removed |
| `intrinsic` | Already intrinsic, as a simulator's tables can be |

The intrinsic voltages come from a DC solve of the extrinsic network carrying the measured currents. Intrinsic points
therefore do not lie on a grid, even when the measured ones do. ANNs train on them directly, and tables re-grid them.

A simulated table that also carries intrinsic voltages gets the computed ones checked against them. `model deembed`
reports the largest difference, for example `max |ΔV| 3.1e-10 V between its intrinsic voltages and the computed ones`.

## The six-pin network {#six-pin}

The extrinsic network is an ordinary schematic with **six numbered pins**: 1, 2 and 3 are the extrinsic gate, drain and
source, and 4, 5 and 6 are the intrinsic gate, drain and source. Pin 3 is the ground the data is referenced to. Each
pin is numbered once. A network that breaks this rule is refused, and nothing is written.

The network's values are its variables. Each one can be locked, bounded, tied to another (`rd × 1.2`) and fitted. Three
stock networks are provided: **GaN two-shell** (outer pad capacitances, inductances with a gate–drain mutual, inner
capacitances, then the series resistances), **classic single shell**, and **series only**. The network is fitted
cold-FET (a pinched-off and a forward-gate point), multi-bias (the intrinsic capacitances made flat in frequency at
every chosen bias), or both. After a fit, each value has a standard error, and a value the data barely determines is
marked with the partner it is correlated with.

## Methods and their defaults {#defaults}

| Setting | Default |
|---|---|
| Method, every constituent | ANN |
| ANN | Two hidden layers of 8, `tanh`; 6000 iterations for a current, 3000 for a charge; 3 restarts; 10 % held out |
| Drain-current weighting | Relative, with a floor at 10 % of the largest current (`IdFloor` 0.1) |
| Gate current | Fitted as `asinh(Ig/I0)` with `I0` at 1e-4 of the largest gate current (`IgFloor`); an equation `Ig` is fitted directly |
| Table | Grid step 0.1 V; a charge table integrates along the mean of paths A and B |
| Charges | An ANN trained on the capacitances alone (the adjoint method), anchored to zero charge at a stated bias |
| DC/RF dispersion | Off; τ 1 µs when on |
| Self-heating | Off |
| Traps | None |
| Network fit | Multi-bias |

A model never extrapolates freely. Outside its training domain each constituent is continued linearly from the domain's
edge. A current also gains a conductance on its own branch, which grows with distance from the edge: ten times the
current's own conductance at the edge it leaves, capped at ten times its largest in the data. Where the data is flat, as
a gate current below pinch-off, the continuation stays flat; where it is steep, as the forward gate diode, it stays steep.
The Current step reports that conductance at each current's lowest and highest `Vgs`, as
`IgGs 2e-09 S at −6 V, 4.9 S at 1 V`. The data is still the better answer: keep it wider than the voltages the circuit
will reach, because beyond it the current is a continuation, not the device's.

## How an ANN is trained {#ann-training}

An ANN constituent is a small network, two hidden layers of 8 `tanh` neurons by default, trained by circuitRF's own
trainer. It trains on two kinds of target, alone or together:

| Target | Trains |
|---|---|
| Values: the function itself at each bias | `Id` and `Ig`, from the I–V data |
| Derivatives: the function's slope along `vgs` and `vds` | `Qg` and `Qd`, from the capacitances; with self-heating, `Id` as well, from the RF `Gm` and `Gds` |

### The adjoint method for the charges {#adjoint}

S-parameters do not measure a charge. They measure its derivatives: at each bias the intrinsic admittance gives the
four capacitances `C11 = ∂Qg/∂vgs`, `C12 = ∂Qg/∂vds`, `C21 = ∂Qd/∂vgs` and `C22 = ∂Qd/∂vds`, averaged over the band
where they are flat. An ANN charge is trained on those derivatives alone [P1 §6.3], [X03]:

1. At each bias the network gives its value and its derivatives with respect to its inputs. The derivatives come from
   a second network carried forward with the first, so they are exact, never differenced.
2. The loss is the weighted squared difference between those derivatives and the measured capacitances. For `Qg` it is
   `(∂Qg/∂vgs − C11)² + (∂Qg/∂vds − C12)²`, summed over the biases.
3. The loss's gradient with respect to every weight is back-propagated through both networks at once: the exact
   adjoint sensitivity of [X03]. An L-BFGS optimiser [NW06] minimises it.
4. Derivatives fix a charge only up to a constant, so an **anchor** holds each charge at zero at one bias: `(0, 0)` by
   default, or the bias you state, moved to the nearest data point when it lies outside the data.

The result is one function per terminal, so the model conserves charge whatever the data. When the measured
capacitances are not the derivatives of one function (their curl, which Check (a) reads, is not zero), the fit is the
least-squares compromise between them [P1 §6.3.6], and Check (b) reads near zero on the model. A table charge takes
the other route and integrates the capacitances along a path, so its result depends on the path [P1 §6.3]; the
adjoint-trained ANN has no path.

A bias whose capacitances are not flat across the band weighs less: when the worse of `C11`'s and `C22`'s flatness
exceeds `FlatnessLimit` (0.1), the point weighs `(limit/flatness)²`. With S at two temperatures or more, an ANN charge
takes `T` as a third input, and the anchor holds both charges at zero at every temperature, so heating moves no charge
at the anchor bias [X10]. **Drift charge in Qg** splits the gate charge into depletion charges, fitted the same way to
the capacitances near `vds = 0`, and a drift charge that follows `Id` [I11].

### The currents {#ann-currents}

`Id` and `Ig` train on the measured values, `Id` weighted relative to the current with a floor (`IdFloor`). With
self-heating, `Id` also trains on derivatives: the I–V values at each point's junction temperature, and each S point's
RF `Gm` and `Gds` as `∂Id/∂vgs` and `∂Id/∂vds` at the same point [X03], [X10]. One function then gives both the
DC and the RF output conductance [P1 §6.6]. With the current step's `Symmetry` set to `Exchange` in the `.cmc`, the
currents are defined for `vds ≥ 0` only, and at negative `vds` they are their own image with drain and source
exchanged [P1 §6.5], [X07]. It is off by default, because a field-plated power device is asymmetric by design.

### Common to every ANN {#ann-common}

- Each input is scaled onto [−1, 1] over the training points, and the output onto the same range, so one network size
  suits a 10 mA device and a 10 A one.
- **Restarts** (3 by default) start from different seeded weights, and the start with the lowest held-out error is
  kept. The same data, settings and seed give the same model, to the bit, on every machine.
- **Held out** (10 % by default) are spread across the bias plane rather than taken from one corner, and the status
  strip's held-out error is the model's error on them.
- **Iterations** cap the optimiser. It stops sooner when the loss stops falling.
- A network with more weights than a tenth of its targets says so in one line: it can fit the noise.

## Charge conservation {#conservation}

The charges are charge functions, so a model is charge-conserving by construction. Its capacitances are derivatives
of one charge each, and no DC is ever made by displacement current. **Check Charge Conservation** checks three things:

| Check | Reads | A good result |
|---|---|---|
| (a) The data | The curl of the measured capacitance matrix, row by row, and the difference between the two line-integral paths | A few percent: the data's own noise and the network's error |
| (b) The model | The same curl on the fitted model's capacitances | Near zero |
| (c) Large signal | A harmonic-balance drive at *f* and at 2*f*; the change in DC gate current is DC made by displacement current | Below 0.1 % of the fundamental gate current |

Check (a) needs S at 4 biases or more across the bias plane; with fewer it reports *not run* and the number of biases,
which is not a failure. With typed charges it reports *not run*, as there is no S data. A linear or typed charge's (b)
reads zero.

A non-reciprocal capacitance matrix (a transcapacitance, `C21 ≠ C12`) is reported for information. It is physical, and
it is not an error. Checks (b) and (c) also run on any nonlinear device in a netlist:
`model check-charge design.cnl --device X1 --bias vgs,vds`.

## Temperature {#temperature}

**Data at two or more temperatures** makes `T` an input of every constituent. The model then follows the run's ambient
`temp`, clamped to the data's range with one warning, and self-heating is available. An equation template's current
scale (Ipk, Beta or Idss) is then linear in `T` unless you say otherwise; see [the method picker](#method).

**Data at one temperature** makes a model valid at that temperature, `T0`, with no temperature dependence. Self-heating
is unavailable. A run at another `temp` prints one warning naming both temperatures and uses the model as it is.

Self-heating needs an Rth. Type it, or extract it from pulsed I–V at two or more chuck temperatures, pulsed from a
quiescent point that dissipates nothing, together with DC I–V at the same biases. Extraction trains an isothermal current
on the pulses, finds the junction temperature at which it gives each DC point's current, and fits the rise against the
dissipated power. The thermal dynamics are a typed Cth, a typed τth, a Foster ladder, or τth fitted to the low-frequency
roll-off of `Re Y22`. That fit is not offered while traps are modelled, because the roll-off holds trap time constants
too.

When drain lag is modelled, type Rth (from a thermal simulation, for example) rather than extract it. A pulse from a
0 V drain leaves the trap state short of its DC value, and extraction reads that shortfall as heating.

## How traps work in the model {#trap-model}

A GaN FET remembers the voltages it has just seen. A high drain voltage or a deep pinch-off captures electrons in traps
near the channel, and the charge they hold reduces the current until it is released ([V01], [B02]). Capture is fast and
release is slow, and an RF period lies between the two. At RF the current therefore depends on the extreme voltages of
the recent past as well as on the voltages now, which a DC I–V cannot show.

The model holds that memory in a small network per trap state. Each network is one internal node, and the node's voltage
**is** the state, in volts.

<figure class="figure">
<svg viewBox="0 0 760 510" role="img" aria-label="The ComposedFET model: a linear extrinsic network surrounds the intrinsic core. Inside the core, Ig, Qg, Id and Qd are the constituents. A gate-lag network reads vgs and holds state phi1, a drain-lag network reads vds and holds state phi2, and a thermal network takes the power and holds T. The states feed Id and Qd; T feeds all four." style="width:100%;height:auto">
<defs>
<marker id="tm-a-ink" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--text)"/></marker>
<marker id="tm-a-gate" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--link)"/></marker>
<marker id="tm-a-drain" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--accent)"/></marker>
<marker id="tm-a-mut" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--muted)"/></marker>
</defs>
<rect x="50" y="24" width="660" height="456" rx="10" fill="var(--surface-2)" stroke="var(--muted)" stroke-width="1.4"/>
<text x="380.0" y="46.0" font-size="12" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">Extrinsic network: the six-pin schematic of pads, inductances and resistances (linear)</text>
<rect x="112" y="62" width="536" height="368" rx="8" fill="var(--bg)" stroke="var(--text)" stroke-width="1.4"/>
<text x="124.0" y="82.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">Intrinsic core</text>
<text x="636.0" y="82.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">Ig, Qg, Id and Qd carry the terminal currents</text>
<path d="M14 256 H112" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M648 256 H746" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M380 430 V498" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="10.0" y="246.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">G</text>
<text x="750.0" y="246.0" font-size="13" fill="var(--text)" text-anchor="end" font-family="IBM Plex Sans" font-weight="600">D</text>
<text x="392.0" y="496.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">S</text>
<text x="81.0" y="246.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">pads,</text>
<text x="81.0" y="272.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">L, R</text>
<text x="679.0" y="246.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">pads,</text>
<text x="679.0" y="272.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">L, R</text>
<text x="344.0" y="456.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">L, R</text>
<rect x="128" y="94" width="120" height="72" rx="6" fill="var(--surface)" stroke="var(--border)" stroke-width="1.4"/>
<text x="188.0" y="114.0" font-size="14" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Ig</text>
<text x="188.0" y="134.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vgs, vds, T</text>
<rect x="252" y="94" width="120" height="72" rx="6" fill="var(--surface)" stroke="var(--border)" stroke-width="1.4"/>
<text x="312.0" y="114.0" font-size="14" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Qg</text>
<text x="312.0" y="134.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vgs, vds, T</text>
<rect x="376" y="94" width="120" height="72" rx="6" fill="var(--surface)" stroke="var(--text)" stroke-width="2"/>
<text x="436.0" y="114.0" font-size="14" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Id</text>
<text x="436.0" y="134.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vgs, vds, T</text>
<text x="436.0" y="152.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">φ1, φ2</text>
<rect x="500" y="94" width="120" height="72" rx="6" fill="var(--surface)" stroke="var(--border)" stroke-width="1.4"/>
<text x="560.0" y="114.0" font-size="14" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Qd</text>
<text x="560.0" y="134.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vgs, vds, T</text>
<text x="560.0" y="152.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">φ1, φ2</text>
<text x="310.0" y="152.0" font-size="10" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">via Id, if drift</text>
<path d="M188 208 H560" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M188 208 V168" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-a-ink)"/>
<text x="194.0" y="192.0" font-size="11" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">T</text>
<path d="M312 208 V168" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-a-ink)"/>
<text x="318.0" y="192.0" font-size="11" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">T</text>
<path d="M436 208 V168" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-a-ink)"/>
<text x="442.0" y="192.0" font-size="11" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">φ1 φ2 T</text>
<path d="M560 208 V168" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-a-ink)"/>
<text x="566.0" y="192.0" font-size="11" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">φ1 φ2 T</text>
<rect x="126" y="300" width="160" height="112" rx="6" fill="var(--surface)" stroke="var(--link)" stroke-width="2"/>
<text x="206.0" y="322.0" font-size="13" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Gate-lag network</text>
<text x="206.0" y="342.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">one node, state φ1</text>
<text x="206.0" y="360.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">holds the lowest</text>
<text x="206.0" y="376.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">recent vgs</text>
<text x="206.0" y="398.0" font-size="10" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">see Figure 2</text>
<rect x="474" y="300" width="160" height="112" rx="6" fill="var(--surface)" stroke="var(--accent)" stroke-width="2"/>
<text x="554.0" y="322.0" font-size="13" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Drain-lag network</text>
<text x="554.0" y="342.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">one node, state φ2</text>
<text x="554.0" y="360.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">holds the highest</text>
<text x="554.0" y="376.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">recent vds</text>
<text x="554.0" y="398.0" font-size="10" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">see Figure 2</text>
<rect x="316" y="300" width="128" height="112" rx="6" fill="var(--surface)" stroke="var(--border)" stroke-width="1.4"/>
<text x="380.0" y="322.0" font-size="13" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans" font-weight="600">Thermal network</text>
<text x="380.0" y="342.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">one node, state T</text>
<text x="380.0" y="360.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">Rth and Cth</text>
<text x="380.0" y="376.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">from power P</text>
<path d="M250 300 V208" fill="none" stroke="var(--link)" stroke-width="2"/>
<text x="258.0" y="268.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">φ1</text>
<path d="M510 300 V208" fill="none" stroke="var(--accent)" stroke-width="2"/>
<text x="518.0" y="268.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">φ2</text>
<path d="M352 300 V208" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="340.0" y="268.0" font-size="12" fill="var(--text)" text-anchor="end" font-family="IBM Plex Sans">T</text>
<path d="M408 166 V298" fill="none" stroke="var(--muted)" stroke-width="1.4" stroke-dasharray="4 3" marker-end="url(#tm-a-mut)"/>
<text x="416.0" y="268.0" font-size="12" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">P</text>
<path d="M112 256 H166 V298" fill="none" stroke="var(--link)" stroke-width="2" marker-end="url(#tm-a-gate)"/>
<text x="120.0" y="248.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">reads vgs</text>
<circle cx="112" cy="256" r="3.5" fill="var(--link)"/>
<path d="M648 256 H594 V298" fill="none" stroke="var(--accent)" stroke-width="2" marker-end="url(#tm-a-drain)"/>
<text x="640.0" y="248.0" font-size="12" fill="var(--text)" text-anchor="end" font-family="IBM Plex Sans">reads vds</text>
<circle cx="648" cy="256" r="3.5" fill="var(--accent)"/>
<path d="M316 356 H288" fill="none" stroke="var(--muted)" stroke-width="1.4" stroke-dasharray="4 3" marker-end="url(#tm-a-mut)"/>
<path d="M444 356 H472" fill="none" stroke="var(--muted)" stroke-width="1.4" stroke-dasharray="4 3" marker-end="url(#tm-a-mut)"/>
</svg>
<figcaption style="text-align:left"><b>Figure 1.</b> Where the trap networks sit. Each network reads an intrinsic voltage and holds a state, and the constituents read the state. A network draws nothing from the voltage it reads. <code>Ig</code> never takes a state. <code>Qg</code> sees one only through the channel current in a drift charge. <code>Qd</code> takes the states when pulsed S-parameters support it. Dashed: the power that heats the junction, and the temperature that sets emission when an activation energy is given.</figcaption>
</figure>

- **A network reads and does not load.** It reads the intrinsic `vgs` or `vds`, inside the extrinsic network, and draws
  no current from it. A state reaches the terminals only through the current it changes.
- **The two states are independent.** Each has its own node and time constants, its own toggle in the Traps step and its
  own instance switch. A model can have either or both, and with both the current responds to the two at once.
- **At DC a state equals its voltage**: `φ1 = vgs` and `φ2 = vds`. The difference between DC and RF appears only when a
  voltage moves faster than its state can follow.

### The two networks {#trap-networks}

Figure 2 is the **CaptureEmission** model ([P1] eqs. 6.73–6.74, [X10] eqs. 1.3–1.4). Each state has a slow path through a
resistor and a fast path through a diode. Gate lag and drain lag differ in the voltage they read and in the diode's
direction.

<figure class="figure">
<svg viewBox="0 0 760 258" role="img" aria-label="Two peak-detector circuits. Gate lag: a controlled source reading vgs drives a resistor in parallel with a diode, whose anode is at node phi1; a capacitor holds phi1 to ground. Drain lag: the same circuit reading vds with the diode reversed, its cathode at node phi2." style="width:100%;height:auto">
<defs>
<marker id="tm-b-ink" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--text)"/></marker>
</defs>
<g>
<text x="10.0" y="18.0" font-size="14" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">Gate lag: follows the lowest vgs</text>
<g transform="translate(0,16)">
<circle cx="40" cy="128" r="17" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="40.0" y="123.0" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">+</text>
<text x="40.0" y="141.0" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">−</text>
<text x="64.0" y="136.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">reads</text>
<text x="64.0" y="151.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">vgs</text>
<path d="M40 111 V76 H84" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M84 52 V100" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M236 52 V100" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M84 52 H126 l4 -8 l8 16 l8 -16 l8 16 l8 -16 l8 16 l4 -8 H236" fill="none" stroke="var(--link)" stroke-width="2"/>
<text x="160.0" y="34.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">R_emit = τ_emit / C</text>
<path d="M84 100 H236" fill="none" stroke="var(--link)" stroke-width="2"/>
<path d="M169 91 L151 100 L169 109 z" fill="var(--link)" stroke="var(--link)" stroke-width="1.5"/>
<path d="M151 90 V110" fill="none" stroke="var(--link)" stroke-width="2.4"/>
<text x="160.0" y="126.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">capture diode (I_s, V_c)</text>
<path d="M236 76 H282" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<circle cx="282" cy="76" r="3.5" fill="var(--text)"/>
<path d="M282 76 V132" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M268 132 H296" fill="none" stroke="var(--text)" stroke-width="2.2"/>
<path d="M268 140 H296" fill="none" stroke="var(--text)" stroke-width="2.2"/>
<path d="M282 140 V170" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="300.0" y="140.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">C</text>
<path d="M40 145 V170 H282" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M150 170 V178" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M138 178 H162" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M143 183 H157" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M148 188 H152" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M282 76 H340" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-b-ink)"/>
<text x="288.0" y="66.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">φ1</text>
<text x="300.0" y="96.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">to Id, Qd</text>
<text x="10.0" y="216.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Diode conducts when vgs falls below φ1: φ1 drops fast.</text>
<text x="10.0" y="233.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Otherwise R_emit lets φ1 relax back up to vgs slowly.</text>
</g>
</g>
<g>
<text x="395.0" y="18.0" font-size="14" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">Drain lag: follows the highest vds</text>
<g transform="translate(0,16)">
<circle cx="425" cy="128" r="17" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="425.0" y="123.0" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">+</text>
<text x="425.0" y="141.0" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">−</text>
<text x="449.0" y="136.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">reads</text>
<text x="449.0" y="151.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">vds</text>
<path d="M425 111 V76 H469" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M469 52 V100" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M621 52 V100" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M469 52 H511 l4 -8 l8 16 l8 -16 l8 16 l8 -16 l8 16 l4 -8 H621" fill="none" stroke="var(--accent)" stroke-width="2"/>
<text x="545.0" y="34.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">R_emit = τ_emit / C</text>
<path d="M469 100 H621" fill="none" stroke="var(--accent)" stroke-width="2"/>
<path d="M536 91 L554 100 L536 109 z" fill="var(--accent)" stroke="var(--accent)" stroke-width="1.5"/>
<path d="M554 90 V110" fill="none" stroke="var(--accent)" stroke-width="2.4"/>
<text x="545.0" y="126.0" font-size="11" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">capture diode (I_s, V_c)</text>
<path d="M621 76 H667" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<circle cx="667" cy="76" r="3.5" fill="var(--text)"/>
<path d="M667 76 V132" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M653 132 H681" fill="none" stroke="var(--text)" stroke-width="2.2"/>
<path d="M653 140 H681" fill="none" stroke="var(--text)" stroke-width="2.2"/>
<path d="M667 140 V170" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<text x="685.0" y="140.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">C</text>
<path d="M425 145 V170 H667" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M535 170 V178" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M523 178 H547" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M528 183 H542" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M533 188 H537" fill="none" stroke="var(--text)" stroke-width="1.6"/>
<path d="M667 76 H725" fill="none" stroke="var(--text)" stroke-width="1.6" marker-end="url(#tm-b-ink)"/>
<text x="673.0" y="66.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">φ2</text>
<text x="685.0" y="96.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">to Id, Qd</text>
<text x="395.0" y="216.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Diode conducts when vds rises above φ2: φ2 climbs fast.</text>
<text x="395.0" y="233.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Otherwise R_emit lets φ2 relax back down to vds slowly.</text>
</g>
</g>
<path d="M378 8 V254" fill="none" stroke="var(--border)" stroke-width="1"/>
</svg>
<figcaption style="text-align:left"><b>Figure 2.</b> The two trap networks, drawn as the circuits their equations are: each is a peak detector whose capacitor voltage is the state. The source is a read of the intrinsic voltage, so the network does not load the device. The only difference between gate lag and drain lag is the voltage read and the diode's direction.</figcaption>
</figure>

These are the model's equations, written as a circuit:

```
C·dφ1/dt = (vgs − φ1)/R_emit,1 − I_s,1·(e^((φ1 − vgs)/Vc) − 1)     gate lag
C·dφ2/dt = (vds − φ2)/R_emit,2 + I_s,2·(e^((vds − φ2)/Vc) − 1)     drain lag
```

Above `e^20` the exponential continues along its tangent, which only a Newton iterate far from the solution reaches.
The elements come from the Traps step's settings:

| Element | From the settings | At the defaults |
|---|---|---|
| Emission resistor `R_emit` | `TauEmit / C` | 1 Ω |
| Capture diode, saturation current `I_s` | `C·(Xref / TauCapt)·e^(−Xref/Vc)` | 0.21 mA |
| Capture diode, slope voltage | `Vc` | 50 mV |
| Node capacitance `C` | Internal scale, 1 mF | Cancels: every current scales with it |

Only `TauCapt`, `TauEmit`, `Xref` and `Vc` set the behaviour.

**Emission** is the resistor. With the diode off, the state relaxes towards its voltage with time constant `TauEmit`. When a
state's activation energy `Ea` is given, emission follows the device temperature, the junction's when the model
self-heats, and capture stays fixed ([N22] eq. 3). `T` and `Tref` are in kelvin in this equation, although `Tref` is set in °C:

```
1/τ_emit(T) = (1/TauEmit)·(T/Tref)²·e^(−(Ea/k)·(1/T − 1/Tref))
```

**Capture** is the diode, and its exponential makes capture fast at first and slow as the gap closes. `TauCapt` is the
time scale at a gap of `Xref`. It is not the time the state takes to arrive. After a large step in `vds` with the
defaults, the gap between the voltage and the state is:

| Time after the step | 1 ns | 10 ns | 100 ns | 1 µs | 10 µs | 100 µs | 1 ms |
|---|---|---|---|---|---|---|---|
| Gap, V | 0.97 | 0.85 | 0.74 | 0.62 | 0.50 | 0.37 | 0.13 |

Each decade of time closes about `Vc·ln 10` = 115 mV until emission takes over. Capture spread over many decades is what
is measured on real devices ([G19]).

| | Gate lag, `φ1` | Drain lag, `φ2` |
|---|---|---|
| Reads | `vgs` | `vds` |
| Capture moves the state | Down, when `vgs` is below it | Up, when `vds` is above it |
| So the state holds | The lowest recent `vgs` | The highest recent `vds` |
| Seen in pulses as | Current slow to recover after the gate leaves pinch-off | Current slow to recover after the drain leaves a high voltage |
| Set by | Pulses from two or more quiescent gate voltages | Pulses from two or more quiescent drain voltages |
| Commonly associated with | Surface states near the gate, which act as a virtual gate ([V01]) | Traps in the buffer under the channel, which act as a back gate ([B02], [P21]) |
| Instance switch | `GateLag` | `DrainLag` |

The states are behavioural. They separate the two voltages that set the memory and do not locate the traps in the device.

### From a state to the current {#trap-current}

A state on its own is only a voltage. The **coupling** decides what it does to the current ([P1] eq. 6.70, [X10] eqs.
1.1–1.5). Under **Trained**, `Id(vgs, vds, T, φ1, φ2)` is fitted with the states as inputs. Under **Shift**, the trap-free
current is evaluated at a gate voltage lowered by `ΔV = ShiftDrain·(φ2 − vds) + ShiftGate·(vgs − φ1)`, which reads the
trapped charge as a back gate ([P1] §6.6.4, [P21]). Both terms are positive when a state is held away from its voltage, so
gate lag and drain lag add. [Traps, and what pulsed data allows](#trap-data) gives the detail of each.

<figure class="figure">
<svg viewBox="0 0 760 336" role="img" aria-label="Two columns of plots against time from 0 to 8 milliseconds. Left, gate lag: vgs steps from minus 2 to minus 6 volts for 1 millisecond; phi1 follows it down at once and returns over several milliseconds; the current after the step is about a quarter low and recovers with the state. Right, drain lag: vds steps from 5 to 30 volts for 1 millisecond; phi2 follows it up at once and returns over several milliseconds; the current after the step is about a third low and recovers with the state." style="width:100%;height:auto">
<text x="48.0" y="16.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">Gate lag: vgs held at −2 V, 1 ms down to −6 V</text>
<text x="428.0" y="16.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">Drain lag: vds held at 5 V, 1 ms up to 30 V</text>
<path d="M48 137.3 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="141.3" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−6</text>
<path d="M48 100.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="104.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−4</text>
<path d="M48 62.7 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="66.7" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−2</text>
<text x="42.0" y="35.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">V</text>
<polyline points="48.0,62.7 48.4,62.7 48.8,62.7 49.1,62.7 49.5,62.7 49.9,62.7 50.2,62.7 50.6,62.7 51.0,62.7 51.4,62.7 51.8,62.7 52.1,62.7 52.5,62.7 52.9,62.7 53.2,62.7 53.6,62.7 54.0,62.7 54.4,62.7 54.8,62.7 55.1,62.7 55.5,62.7 55.9,62.7 56.2,62.7 56.6,62.7 57.0,62.7 57.4,62.7 57.8,62.7 58.1,62.7 58.5,62.7 58.9,62.7 59.2,62.7 59.6,62.7 60.0,62.7 60.4,62.7 60.8,62.7 61.1,62.7 61.5,62.7 61.9,62.7 62.2,62.7 62.6,62.7 63.0,62.7 63.4,62.7 63.8,62.7 64.1,62.7 64.5,62.7 64.9,62.7 65.2,62.7 65.6,62.7 66.0,62.7 66.4,62.7 66.8,62.7 67.1,62.7 67.5,62.7 67.9,62.7 68.2,62.7 68.6,62.7 69.0,62.7 69.4,62.7 69.8,62.7 70.1,62.7 70.5,62.7 70.9,62.7 71.2,62.7 71.6,62.7 72.0,62.7 72.4,62.7 72.8,62.7 73.1,62.7 73.5,62.7 73.9,62.7 74.2,62.7 74.6,62.7 75.0,62.7 75.4,62.7 75.8,62.7 76.1,62.7 76.5,62.7 76.9,62.7 77.2,62.7 77.6,62.7 78.0,62.7 78.4,62.7 78.8,62.7 79.1,62.7 79.5,62.7 79.9,62.7 80.2,62.7 80.6,62.7 81.0,62.7 81.4,62.7 81.8,62.7 82.1,62.7 82.5,62.7 82.9,62.7 83.2,62.7 83.6,62.7 84.0,62.7 84.4,62.7 84.8,62.7 85.1,62.7 85.5,62.7 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.5,137.3 85.6,137.3 85.6,137.3 85.6,137.3 85.6,137.3 85.6,137.3 85.7,137.3 85.7,137.3 85.8,137.3 85.9,137.3 85.9,137.3 86.0,137.3 86.1,137.3 86.2,137.3 86.2,137.3 86.4,137.3 86.6,137.3 86.7,137.3 87.0,137.3 87.0,137.3 87.4,137.3 87.4,137.3 87.8,137.3 87.8,137.3 88.1,137.3 88.5,137.3 88.5,137.3 88.9,137.3 89.2,137.3 89.6,137.3 90.0,137.3 90.4,137.3 90.8,137.3 91.1,137.3 91.5,137.3 91.9,137.3 92.2,137.3 92.6,137.3 93.0,137.3 93.4,137.3 93.8,137.3 94.1,137.3 94.5,137.3 94.9,137.3 95.2,137.3 95.6,137.3 96.0,137.3 96.4,137.3 96.8,137.3 97.1,137.3 97.5,137.3 97.9,137.3 98.2,137.3 98.6,137.3 99.0,137.3 99.4,137.3 99.8,137.3 100.1,137.3 100.5,137.3 100.9,137.3 101.2,137.3 101.6,137.3 102.0,137.3 102.4,137.3 102.8,137.3 103.1,137.3 103.5,137.3 103.9,137.3 104.2,137.3 104.6,137.3 105.0,137.3 105.4,137.3 105.8,137.3 106.1,137.3 106.5,137.3 106.9,137.3 107.2,137.3 107.6,137.3 108.0,137.3 108.4,137.3 108.8,137.3 109.1,137.3 109.5,137.3 109.9,137.3 110.2,137.3 110.6,137.3 111.0,137.3 111.4,137.3 111.8,137.3 112.1,137.3 112.5,137.3 112.9,137.3 113.3,137.3 113.6,137.3 114.0,137.3 114.4,137.3 114.8,137.3 115.1,137.3 115.5,137.3 115.9,137.3 116.3,137.3 116.6,137.3 117.0,137.3 117.4,137.3 117.8,137.3 118.1,137.3 118.5,137.3 118.9,137.3 119.2,137.3 119.6,137.3 120.0,137.3 120.4,137.3 120.8,137.3 121.1,137.3 121.5,137.3 121.9,137.3 122.2,137.3 122.6,137.3 123.0,137.3 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.0,62.7 123.1,62.7 123.1,62.7 123.1,62.7 123.1,62.7 123.1,62.7 123.2,62.7 123.2,62.7 123.3,62.7 123.4,62.7 123.4,62.7 123.5,62.7 123.6,62.7 123.7,62.7 123.8,62.7 123.9,62.7 124.1,62.7 124.2,62.7 124.5,62.7 124.5,62.7 124.9,62.7 124.9,62.7 125.2,62.7 125.3,62.7 125.6,62.7 126.0,62.7 126.0,62.7 126.4,62.7 126.8,62.7 126.8,62.7 127.1,62.7 127.5,62.7 127.9,62.7 128.2,62.7 128.6,62.7 129.0,62.7 129.4,62.7 129.8,62.7 130.1,62.7 130.5,62.7 130.9,62.7 131.2,62.7 131.6,62.7 132.0,62.7 132.4,62.7 132.8,62.7 133.1,62.7 133.5,62.7 133.9,62.7 134.2,62.7 134.6,62.7 135.0,62.7 135.4,62.7 135.8,62.7 136.1,62.7 136.5,62.7 136.9,62.7 137.2,62.7 137.6,62.7 138.0,62.7 138.4,62.7 138.8,62.7 139.1,62.7 139.5,62.7 139.9,62.7 140.2,62.7 140.6,62.7 141.0,62.7 141.4,62.7 141.8,62.7 142.1,62.7 142.5,62.7 142.9,62.7 143.2,62.7 143.6,62.7 144.0,62.7 144.4,62.7 144.8,62.7 145.1,62.7 145.5,62.7 145.9,62.7 146.2,62.7 146.6,62.7 147.0,62.7 147.4,62.7 147.8,62.7 148.1,62.7 148.5,62.7 148.9,62.7 149.2,62.7 149.6,62.7 150.0,62.7 150.4,62.7 150.8,62.7 151.1,62.7 151.5,62.7 151.9,62.7 152.2,62.7 152.6,62.7 153.0,62.7 153.4,62.7 153.8,62.7 154.1,62.7 154.5,62.7 154.9,62.7 155.2,62.7 155.6,62.7 156.0,62.7 156.4,62.7 156.8,62.7 157.1,62.7 157.5,62.7 157.9,62.7 158.2,62.7 158.6,62.7 159.0,62.7 159.4,62.7 159.8,62.7 160.1,62.7 160.5,62.7 160.9,62.7 161.2,62.7 161.6,62.7 162.0,62.7 162.4,62.7 162.8,62.7 163.1,62.7 163.5,62.7 163.9,62.7 164.2,62.7 164.6,62.7 165.0,62.7 165.4,62.7 165.8,62.7 166.1,62.7 166.5,62.7 166.9,62.7 167.2,62.7 167.6,62.7 168.0,62.7 168.4,62.7 168.8,62.7 169.1,62.7 169.5,62.7 169.9,62.7 170.2,62.7 170.6,62.7 171.0,62.7 171.4,62.7 171.8,62.7 172.1,62.7 172.5,62.7 172.9,62.7 173.2,62.7 173.6,62.7 174.0,62.7 174.4,62.7 174.8,62.7 175.1,62.7 175.5,62.7 175.9,62.7 176.2,62.7 176.6,62.7 177.0,62.7 177.4,62.7 177.8,62.7 178.1,62.7 178.5,62.7 178.9,62.7 179.2,62.7 179.6,62.7 180.0,62.7 180.4,62.7 180.8,62.7 181.1,62.7 181.5,62.7 181.9,62.7 182.2,62.7 182.6,62.7 183.0,62.7 183.4,62.7 183.8,62.7 184.1,62.7 184.5,62.7 184.9,62.7 185.3,62.7 185.6,62.7 186.0,62.7 186.4,62.7 186.8,62.7 187.1,62.7 187.5,62.7 187.9,62.7 188.2,62.7 188.6,62.7 189.0,62.7 189.4,62.7 189.8,62.7 190.1,62.7 190.5,62.7 190.9,62.7 191.2,62.7 191.6,62.7 192.0,62.7 192.4,62.7 192.8,62.7 193.1,62.7 193.5,62.7 193.9,62.7 194.2,62.7 194.6,62.7 195.0,62.7 195.4,62.7 195.8,62.7 196.1,62.7 196.5,62.7 196.9,62.7 197.2,62.7 197.6,62.7 198.0,62.7 198.4,62.7 198.8,62.7 199.1,62.7 199.5,62.7 199.9,62.7 200.3,62.7 200.6,62.7 201.0,62.7 201.4,62.7 201.8,62.7 202.1,62.7 202.5,62.7 202.9,62.7 203.3,62.7 203.6,62.7 204.0,62.7 204.4,62.7 204.8,62.7 205.1,62.7 205.5,62.7 205.9,62.7 206.3,62.7 206.6,62.7 207.0,62.7 207.4,62.7 207.8,62.7 208.1,62.7 208.5,62.7 208.9,62.7 209.2,62.7 209.6,62.7 210.0,62.7 210.4,62.7 210.8,62.7 211.1,62.7 211.5,62.7 211.9,62.7 212.2,62.7 212.6,62.7 213.0,62.7 213.4,62.7 213.8,62.7 214.1,62.7 214.5,62.7 214.9,62.7 215.3,62.7 215.6,62.7 216.0,62.7 216.4,62.7 216.8,62.7 217.1,62.7 217.5,62.7 217.9,62.7 218.3,62.7 218.6,62.7 219.0,62.7 219.4,62.7 219.8,62.7 220.1,62.7 220.5,62.7 220.9,62.7 221.2,62.7 221.6,62.7 222.0,62.7 222.4,62.7 222.8,62.7 223.1,62.7 223.5,62.7 223.9,62.7 224.2,62.7 224.6,62.7 225.0,62.7 225.4,62.7 225.8,62.7 226.1,62.7 226.5,62.7 226.9,62.7 227.2,62.7 227.6,62.7 228.0,62.7 228.4,62.7 228.8,62.7 229.1,62.7 229.5,62.7 229.9,62.7 230.2,62.7 230.6,62.7 231.0,62.7 231.4,62.7 231.8,62.7 232.1,62.7 232.5,62.7 232.9,62.7 233.3,62.7 233.6,62.7 234.0,62.7 234.4,62.7 234.8,62.7 235.1,62.7 235.5,62.7 235.9,62.7 236.3,62.7 236.6,62.7 237.0,62.7 237.4,62.7 237.8,62.7 238.1,62.7 238.5,62.7 238.9,62.7 239.3,62.7 239.6,62.7 240.0,62.7 240.4,62.7 240.8,62.7 241.1,62.7 241.5,62.7 241.9,62.7 242.3,62.7 242.6,62.7 243.0,62.7 243.4,62.7 243.8,62.7 244.1,62.7 244.5,62.7 244.9,62.7 245.3,62.7 245.6,62.7 246.0,62.7 246.4,62.7 246.8,62.7 247.1,62.7 247.5,62.7 247.9,62.7 248.2,62.7 248.6,62.7 249.0,62.7 249.4,62.7 249.8,62.7 250.1,62.7 250.5,62.7 250.9,62.7 251.2,62.7 251.6,62.7 252.0,62.7 252.4,62.7 252.8,62.7 253.1,62.7 253.5,62.7 253.9,62.7 254.3,62.7 254.6,62.7 255.0,62.7 255.4,62.7 255.8,62.7 256.1,62.7 256.5,62.7 256.9,62.7 257.2,62.7 257.6,62.7 258.0,62.7 258.4,62.7 258.8,62.7 259.1,62.7 259.5,62.7 259.9,62.7 260.2,62.7 260.6,62.7 261.0,62.7 261.4,62.7 261.8,62.7 262.1,62.7 262.5,62.7 262.9,62.7 263.2,62.7 263.6,62.7 264.0,62.7 264.4,62.7 264.8,62.7 265.1,62.7 265.5,62.7 265.9,62.7 266.2,62.7 266.6,62.7 267.0,62.7 267.4,62.7 267.8,62.7 268.1,62.7 268.5,62.7 268.9,62.7 269.2,62.7 269.6,62.7 270.0,62.7 270.4,62.7 270.8,62.7 271.1,62.7 271.5,62.7 271.9,62.7 272.2,62.7 272.6,62.7 273.0,62.7 273.4,62.7 273.8,62.7 274.1,62.7 274.5,62.7 274.9,62.7 275.2,62.7 275.6,62.7 276.0,62.7 276.4,62.7 276.8,62.7 277.1,62.7 277.5,62.7 277.9,62.7 278.2,62.7 278.6,62.7 279.0,62.7 279.4,62.7 279.8,62.7 280.1,62.7 280.5,62.7 280.9,62.7 281.2,62.7 281.6,62.7 282.0,62.7 282.4,62.7 282.8,62.7 283.1,62.7 283.5,62.7 283.9,62.7 284.2,62.7 284.6,62.7 285.0,62.7 285.4,62.7 285.8,62.7 286.1,62.7 286.5,62.7 286.9,62.7 287.2,62.7 287.6,62.7 288.0,62.7 288.4,62.7 288.8,62.7 289.1,62.7 289.5,62.7 289.9,62.7 290.2,62.7 290.6,62.7 291.0,62.7 291.4,62.7 291.8,62.7 292.1,62.7 292.5,62.7 292.9,62.7 293.2,62.7 293.6,62.7 294.0,62.7 294.4,62.7 294.8,62.7 295.1,62.7 295.5,62.7 295.9,62.7 296.2,62.7 296.6,62.7 297.0,62.7 297.4,62.7 297.8,62.7 298.1,62.7 298.5,62.7 298.9,62.7 299.2,62.7 299.6,62.7 300.0,62.7 300.4,62.7 300.8,62.7 301.1,62.7 301.5,62.7 301.9,62.7 302.2,62.7 302.6,62.7 303.0,62.7 303.4,62.7 303.8,62.7 304.1,62.7 304.5,62.7 304.9,62.7 305.2,62.7 305.6,62.7 306.0,62.7 306.4,62.7 306.8,62.7 307.1,62.7 307.5,62.7 307.9,62.7 308.3,62.7 308.6,62.7 309.0,62.7 309.4,62.7 309.8,62.7 310.1,62.7 310.5,62.7 310.9,62.7 311.2,62.7 311.6,62.7 312.0,62.7 312.4,62.7 312.8,62.7 313.1,62.7 313.5,62.7 313.9,62.7 314.2,62.7 314.6,62.7 315.0,62.7 315.4,62.7 315.8,62.7 316.1,62.7 316.5,62.7 316.9,62.7 317.2,62.7 317.6,62.7 318.0,62.7 318.4,62.7 318.8,62.7 319.1,62.7 319.5,62.7 319.9,62.7 320.2,62.7 320.6,62.7 321.0,62.7 321.4,62.7 321.8,62.7 322.1,62.7 322.5,62.7 322.9,62.7 323.2,62.7 323.6,62.7 324.0,62.7 324.4,62.7 324.8,62.7 325.1,62.7 325.5,62.7 325.9,62.7 326.3,62.7 326.6,62.7 327.0,62.7 327.4,62.7 327.8,62.7 328.1,62.7 328.5,62.7 328.9,62.7 329.3,62.7 329.6,62.7 330.0,62.7 330.4,62.7 330.8,62.7 331.1,62.7 331.5,62.7 331.9,62.7 332.3,62.7 332.6,62.7 333.0,62.7 333.4,62.7 333.8,62.7 334.1,62.7 334.5,62.7 334.9,62.7 335.3,62.7 335.6,62.7 336.0,62.7 336.4,62.7 336.8,62.7 337.1,62.7 337.5,62.7 337.9,62.7 338.2,62.7 338.6,62.7 339.0,62.7 339.4,62.7 339.8,62.7 340.1,62.7 340.5,62.7 340.9,62.7 341.2,62.7 341.6,62.7 342.0,62.7 342.4,62.7 342.8,62.7 343.1,62.7 343.5,62.7 343.9,62.7 344.2,62.7 344.6,62.7 345.0,62.7 345.4,62.7 345.8,62.7 346.1,62.7 346.5,62.7 346.9,62.7 347.3,62.7 347.6,62.7 348.0,62.7" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round"/>
<polyline points="48.0,62.7 48.4,62.7 48.8,62.7 49.1,62.7 49.5,62.7 49.9,62.7 50.2,62.7 50.6,62.7 51.0,62.7 51.4,62.7 51.8,62.7 52.1,62.7 52.5,62.7 52.9,62.7 53.2,62.7 53.6,62.7 54.0,62.7 54.4,62.7 54.8,62.7 55.1,62.7 55.5,62.7 55.9,62.7 56.2,62.7 56.6,62.7 57.0,62.7 57.4,62.7 57.8,62.7 58.1,62.7 58.5,62.7 58.9,62.7 59.2,62.7 59.6,62.7 60.0,62.7 60.4,62.7 60.8,62.7 61.1,62.7 61.5,62.7 61.9,62.7 62.2,62.7 62.6,62.7 63.0,62.7 63.4,62.7 63.8,62.7 64.1,62.7 64.5,62.7 64.9,62.7 65.2,62.7 65.6,62.7 66.0,62.7 66.4,62.7 66.8,62.7 67.1,62.7 67.5,62.7 67.9,62.7 68.2,62.7 68.6,62.7 69.0,62.7 69.4,62.7 69.8,62.7 70.1,62.7 70.5,62.7 70.9,62.7 71.2,62.7 71.6,62.7 72.0,62.7 72.4,62.7 72.8,62.7 73.1,62.7 73.5,62.7 73.9,62.7 74.2,62.7 74.6,62.7 75.0,62.7 75.4,62.7 75.8,62.7 76.1,62.7 76.5,62.7 76.9,62.7 77.2,62.7 77.6,62.7 78.0,62.7 78.4,62.7 78.8,62.7 79.1,62.7 79.5,62.7 79.9,62.7 80.2,62.7 80.6,62.7 81.0,62.7 81.4,62.7 81.8,62.7 82.1,62.7 82.5,62.7 82.9,62.7 83.2,62.7 83.6,62.7 84.0,62.7 84.4,62.7 84.8,62.7 85.1,62.7 85.5,62.7 85.5,109.2 85.5,115.4 85.5,116.1 85.5,116.7 85.5,117.1 85.5,117.5 85.5,117.8 85.5,118.1 85.5,118.4 85.5,118.6 85.5,118.9 85.5,119.1 85.5,119.4 85.5,119.6 85.5,119.8 85.5,120.1 85.5,120.3 85.5,120.5 85.5,120.7 85.5,121.0 85.5,121.2 85.5,121.4 85.5,121.6 85.5,121.9 85.5,122.1 85.5,122.3 85.5,122.5 85.5,122.7 85.5,123.0 85.5,123.2 85.5,123.4 85.5,123.6 85.5,123.8 85.5,124.1 85.5,124.3 85.5,124.5 85.5,124.7 85.5,124.9 85.5,125.1 85.5,125.4 85.5,125.6 85.5,125.8 85.6,126.0 85.6,126.2 85.6,126.5 85.6,126.7 85.6,126.9 85.7,127.1 85.7,127.4 85.8,127.6 85.9,127.8 85.9,127.9 86.0,128.0 86.1,128.3 86.2,128.5 86.2,128.5 86.4,128.8 86.6,129.0 86.7,129.0 87.0,129.3 87.0,129.3 87.4,129.5 87.4,129.5 87.8,129.7 87.8,129.8 88.1,129.9 88.5,130.1 88.5,130.1 88.9,130.2 89.2,130.4 89.6,130.5 90.0,130.6 90.4,130.7 90.8,130.8 91.1,130.9 91.5,131.0 91.9,131.1 92.2,131.2 92.6,131.3 93.0,131.4 93.4,131.5 93.8,131.5 94.1,131.6 94.5,131.7 94.9,131.8 95.2,131.8 95.6,131.9 96.0,132.0 96.4,132.0 96.8,132.1 97.1,132.1 97.5,132.2 97.9,132.3 98.2,132.3 98.6,132.4 99.0,132.4 99.4,132.5 99.8,132.5 100.1,132.6 100.5,132.7 100.9,132.7 101.2,132.8 101.6,132.8 102.0,132.9 102.4,132.9 102.8,133.0 103.1,133.0 103.5,133.0 103.9,133.1 104.2,133.1 104.6,133.2 105.0,133.2 105.4,133.3 105.8,133.3 106.1,133.4 106.5,133.4 106.9,133.4 107.2,133.5 107.6,133.5 108.0,133.6 108.4,133.6 108.8,133.6 109.1,133.7 109.5,133.7 109.9,133.8 110.2,133.8 110.6,133.8 111.0,133.9 111.4,133.9 111.8,133.9 112.1,134.0 112.5,134.0 112.9,134.0 113.3,134.1 113.6,134.1 114.0,134.1 114.4,134.2 114.8,134.2 115.1,134.2 115.5,134.3 115.9,134.3 116.3,134.3 116.6,134.4 117.0,134.4 117.4,134.4 117.8,134.4 118.1,134.5 118.5,134.5 118.9,134.5 119.2,134.6 119.6,134.6 120.0,134.6 120.4,134.6 120.8,134.7 121.1,134.7 121.5,134.7 121.9,134.8 122.2,134.8 122.6,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.8 123.0,134.7 123.1,134.7 123.1,134.7 123.1,134.7 123.1,134.6 123.1,134.6 123.2,134.5 123.2,134.4 123.3,134.3 123.4,134.1 123.4,134.1 123.5,134.0 123.6,133.7 123.7,133.4 123.8,133.4 123.9,133.1 124.1,132.7 124.2,132.6 124.5,132.1 124.5,132.0 124.9,131.4 124.9,131.3 125.2,130.6 125.3,130.5 125.6,130.0 126.0,129.4 126.0,129.3 126.4,128.6 126.8,128.0 126.8,128.0 127.1,127.3 127.5,126.7 127.9,126.1 128.2,125.4 128.6,124.8 129.0,124.2 129.4,123.6 129.8,123.0 130.1,122.4 130.5,121.8 130.9,121.2 131.2,120.6 131.6,120.1 132.0,119.5 132.4,118.9 132.8,118.4 133.1,117.8 133.5,117.3 133.9,116.7 134.2,116.2 134.6,115.7 135.0,115.1 135.4,114.6 135.8,114.1 136.1,113.6 136.5,113.1 136.9,112.6 137.2,112.1 137.6,111.6 138.0,111.1 138.4,110.6 138.8,110.2 139.1,109.7 139.5,109.2 139.9,108.8 140.2,108.3 140.6,107.9 141.0,107.4 141.4,107.0 141.8,106.5 142.1,106.1 142.5,105.7 142.9,105.2 143.2,104.8 143.6,104.4 144.0,104.0 144.4,103.6 144.8,103.2 145.1,102.8 145.5,102.4 145.9,102.0 146.2,101.6 146.6,101.2 147.0,100.8 147.4,100.5 147.8,100.1 148.1,99.7 148.5,99.3 148.9,99.0 149.2,98.6 149.6,98.3 150.0,97.9 150.4,97.6 150.8,97.2 151.1,96.9 151.5,96.5 151.9,96.2 152.2,95.9 152.6,95.5 153.0,95.2 153.4,94.9 153.8,94.6 154.1,94.3 154.5,93.9 154.9,93.6 155.2,93.3 155.6,93.0 156.0,92.7 156.4,92.4 156.8,92.1 157.1,91.8 157.5,91.6 157.9,91.3 158.2,91.0 158.6,90.7 159.0,90.4 159.4,90.1 159.8,89.9 160.1,89.6 160.5,89.3 160.9,89.1 161.2,88.8 161.6,88.6 162.0,88.3 162.4,88.0 162.8,87.8 163.1,87.5 163.5,87.3 163.9,87.1 164.2,86.8 164.6,86.6 165.0,86.3 165.4,86.1 165.8,85.9 166.1,85.6 166.5,85.4 166.9,85.2 167.2,85.0 167.6,84.7 168.0,84.5 168.4,84.3 168.8,84.1 169.1,83.9 169.5,83.7 169.9,83.5 170.2,83.3 170.6,83.1 171.0,82.9 171.4,82.7 171.8,82.5 172.1,82.3 172.5,82.1 172.9,81.9 173.2,81.7 173.6,81.5 174.0,81.3 174.4,81.1 174.8,80.9 175.1,80.8 175.5,80.6 175.9,80.4 176.2,80.2 176.6,80.1 177.0,79.9 177.4,79.7 177.8,79.5 178.1,79.4 178.5,79.2 178.9,79.0 179.2,78.9 179.6,78.7 180.0,78.6 180.4,78.4 180.8,78.3 181.1,78.1 181.5,77.9 181.9,77.8 182.2,77.6 182.6,77.5 183.0,77.3 183.4,77.2 183.8,77.1 184.1,76.9 184.5,76.8 184.9,76.6 185.3,76.5 185.6,76.4 186.0,76.2 186.4,76.1 186.8,76.0 187.1,75.8 187.5,75.7 187.9,75.6 188.2,75.4 188.6,75.3 189.0,75.2 189.4,75.1 189.8,74.9 190.1,74.8 190.5,74.7 190.9,74.6 191.2,74.5 191.6,74.3 192.0,74.2 192.4,74.1 192.8,74.0 193.1,73.9 193.5,73.8 193.9,73.7 194.2,73.6 194.6,73.4 195.0,73.3 195.4,73.2 195.8,73.1 196.1,73.0 196.5,72.9 196.9,72.8 197.2,72.7 197.6,72.6 198.0,72.5 198.4,72.4 198.8,72.3 199.1,72.2 199.5,72.1 199.9,72.0 200.3,72.0 200.6,71.9 201.0,71.8 201.4,71.7 201.8,71.6 202.1,71.5 202.5,71.4 202.9,71.3 203.3,71.2 203.6,71.2 204.0,71.1 204.4,71.0 204.8,70.9 205.1,70.8 205.5,70.7 205.9,70.7 206.3,70.6 206.6,70.5 207.0,70.4 207.4,70.4 207.8,70.3 208.1,70.2 208.5,70.1 208.9,70.1 209.2,70.0 209.6,69.9 210.0,69.8 210.4,69.8 210.8,69.7 211.1,69.6 211.5,69.6 211.9,69.5 212.2,69.4 212.6,69.4 213.0,69.3 213.4,69.2 213.8,69.2 214.1,69.1 214.5,69.0 214.9,69.0 215.3,68.9 215.6,68.8 216.0,68.8 216.4,68.7 216.8,68.7 217.1,68.6 217.5,68.5 217.9,68.5 218.3,68.4 218.6,68.4 219.0,68.3 219.4,68.3 219.8,68.2 220.1,68.1 220.5,68.1 220.9,68.0 221.2,68.0 221.6,67.9 222.0,67.9 222.4,67.8 222.8,67.8 223.1,67.7 223.5,67.7 223.9,67.6 224.2,67.6 224.6,67.5 225.0,67.5 225.4,67.4 225.8,67.4 226.1,67.3 226.5,67.3 226.9,67.2 227.2,67.2 227.6,67.2 228.0,67.1 228.4,67.1 228.8,67.0 229.1,67.0 229.5,66.9 229.9,66.9 230.2,66.9 230.6,66.8 231.0,66.8 231.4,66.7 231.8,66.7 232.1,66.7 232.5,66.6 232.9,66.6 233.3,66.5 233.6,66.5 234.0,66.5 234.4,66.4 234.8,66.4 235.1,66.3 235.5,66.3 235.9,66.3 236.3,66.2 236.6,66.2 237.0,66.2 237.4,66.1 237.8,66.1 238.1,66.1 238.5,66.0 238.9,66.0 239.3,66.0 239.6,65.9 240.0,65.9 240.4,65.9 240.8,65.8 241.1,65.8 241.5,65.8 241.9,65.7 242.3,65.7 242.6,65.7 243.0,65.7 243.4,65.6 243.8,65.6 244.1,65.6 244.5,65.5 244.9,65.5 245.3,65.5 245.6,65.5 246.0,65.4 246.4,65.4 246.8,65.4 247.1,65.3 247.5,65.3 247.9,65.3 248.2,65.3 248.6,65.2 249.0,65.2 249.4,65.2 249.8,65.2 250.1,65.1 250.5,65.1 250.9,65.1 251.2,65.1 251.6,65.0 252.0,65.0 252.4,65.0 252.8,65.0 253.1,64.9 253.5,64.9 253.9,64.9 254.3,64.9 254.6,64.9 255.0,64.8 255.4,64.8 255.8,64.8 256.1,64.8 256.5,64.8 256.9,64.7 257.2,64.7 257.6,64.7 258.0,64.7 258.4,64.7 258.8,64.6 259.1,64.6 259.5,64.6 259.9,64.6 260.2,64.6 260.6,64.5 261.0,64.5 261.4,64.5 261.8,64.5 262.1,64.5 262.5,64.4 262.9,64.4 263.2,64.4 263.6,64.4 264.0,64.4 264.4,64.4 264.8,64.3 265.1,64.3 265.5,64.3 265.9,64.3 266.2,64.3 266.6,64.3 267.0,64.2 267.4,64.2 267.8,64.2 268.1,64.2 268.5,64.2 268.9,64.2 269.2,64.2 269.6,64.1 270.0,64.1 270.4,64.1 270.8,64.1 271.1,64.1 271.5,64.1 271.9,64.1 272.2,64.0 272.6,64.0 273.0,64.0 273.4,64.0 273.8,64.0 274.1,64.0 274.5,64.0 274.9,63.9 275.2,63.9 275.6,63.9 276.0,63.9 276.4,63.9 276.8,63.9 277.1,63.9 277.5,63.9 277.9,63.8 278.2,63.8 278.6,63.8 279.0,63.8 279.4,63.8 279.8,63.8 280.1,63.8 280.5,63.8 280.9,63.8 281.2,63.7 281.6,63.7 282.0,63.7 282.4,63.7 282.8,63.7 283.1,63.7 283.5,63.7 283.9,63.7 284.2,63.7 284.6,63.7 285.0,63.6 285.4,63.6 285.8,63.6 286.1,63.6 286.5,63.6 286.9,63.6 287.2,63.6 287.6,63.6 288.0,63.6 288.4,63.6 288.8,63.6 289.1,63.5 289.5,63.5 289.9,63.5 290.2,63.5 290.6,63.5 291.0,63.5 291.4,63.5 291.8,63.5 292.1,63.5 292.5,63.5 292.9,63.5 293.2,63.5 293.6,63.4 294.0,63.4 294.4,63.4 294.8,63.4 295.1,63.4 295.5,63.4 295.9,63.4 296.2,63.4 296.6,63.4 297.0,63.4 297.4,63.4 297.8,63.4 298.1,63.4 298.5,63.3 298.9,63.3 299.2,63.3 299.6,63.3 300.0,63.3 300.4,63.3 300.8,63.3 301.1,63.3 301.5,63.3 301.9,63.3 302.2,63.3 302.6,63.3 303.0,63.3 303.4,63.3 303.8,63.3 304.1,63.3 304.5,63.2 304.9,63.2 305.2,63.2 305.6,63.2 306.0,63.2 306.4,63.2 306.8,63.2 307.1,63.2 307.5,63.2 307.9,63.2 308.3,63.2 308.6,63.2 309.0,63.2 309.4,63.2 309.8,63.2 310.1,63.2 310.5,63.2 310.9,63.2 311.2,63.2 311.6,63.1 312.0,63.1 312.4,63.1 312.8,63.1 313.1,63.1 313.5,63.1 313.9,63.1 314.2,63.1 314.6,63.1 315.0,63.1 315.4,63.1 315.8,63.1 316.1,63.1 316.5,63.1 316.9,63.1 317.2,63.1 317.6,63.1 318.0,63.1 318.4,63.1 318.8,63.1 319.1,63.1 319.5,63.1 319.9,63.1 320.2,63.0 320.6,63.0 321.0,63.0 321.4,63.0 321.8,63.0 322.1,63.0 322.5,63.0 322.9,63.0 323.2,63.0 323.6,63.0 324.0,63.0 324.4,63.0 324.8,63.0 325.1,63.0 325.5,63.0 325.9,63.0 326.3,63.0 326.6,63.0 327.0,63.0 327.4,63.0 327.8,63.0 328.1,63.0 328.5,63.0 328.9,63.0 329.3,63.0 329.6,63.0 330.0,63.0 330.4,63.0 330.8,63.0 331.1,63.0 331.5,63.0 331.9,62.9 332.3,62.9 332.6,62.9 333.0,62.9 333.4,62.9 333.8,62.9 334.1,62.9 334.5,62.9 334.9,62.9 335.3,62.9 335.6,62.9 336.0,62.9 336.4,62.9 336.8,62.9 337.1,62.9 337.5,62.9 337.9,62.9 338.2,62.9 338.6,62.9 339.0,62.9 339.4,62.9 339.8,62.9 340.1,62.9 340.5,62.9 340.9,62.9 341.2,62.9 341.6,62.9 342.0,62.9 342.4,62.9 342.8,62.9 343.1,62.9 343.5,62.9 343.9,62.9 344.2,62.9 344.6,62.9 345.0,62.9 345.4,62.9 345.8,62.9 346.1,62.9 346.5,62.9 346.9,62.9 347.3,62.9 347.6,62.9 348.0,62.8" fill="none" stroke="var(--link)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="164.2" y="121.5" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">state φ1</text>
<text x="258.0" y="52.4" font-size="12" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">vgs</text>
<path d="M428 156.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="160.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0</text>
<path d="M428 124.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="128.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">10</text>
<path d="M428 92.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="96.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">20</text>
<path d="M428 60.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="64.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">30</text>
<text x="422.0" y="35.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">V</text>
<polyline points="428.0,140.0 428.4,140.0 428.8,140.0 429.1,140.0 429.5,140.0 429.9,140.0 430.2,140.0 430.6,140.0 431.0,140.0 431.4,140.0 431.8,140.0 432.1,140.0 432.5,140.0 432.9,140.0 433.2,140.0 433.6,140.0 434.0,140.0 434.4,140.0 434.8,140.0 435.1,140.0 435.5,140.0 435.9,140.0 436.2,140.0 436.6,140.0 437.0,140.0 437.4,140.0 437.8,140.0 438.1,140.0 438.5,140.0 438.9,140.0 439.2,140.0 439.6,140.0 440.0,140.0 440.4,140.0 440.8,140.0 441.1,140.0 441.5,140.0 441.9,140.0 442.2,140.0 442.6,140.0 443.0,140.0 443.4,140.0 443.8,140.0 444.1,140.0 444.5,140.0 444.9,140.0 445.2,140.0 445.6,140.0 446.0,140.0 446.4,140.0 446.8,140.0 447.1,140.0 447.5,140.0 447.9,140.0 448.2,140.0 448.6,140.0 449.0,140.0 449.4,140.0 449.8,140.0 450.1,140.0 450.5,140.0 450.9,140.0 451.2,140.0 451.6,140.0 452.0,140.0 452.4,140.0 452.8,140.0 453.1,140.0 453.5,140.0 453.9,140.0 454.2,140.0 454.6,140.0 455.0,140.0 455.4,140.0 455.8,140.0 456.1,140.0 456.5,140.0 456.9,140.0 457.2,140.0 457.6,140.0 458.0,140.0 458.4,140.0 458.8,140.0 459.1,140.0 459.5,140.0 459.9,140.0 460.2,140.0 460.6,140.0 461.0,140.0 461.4,140.0 461.8,140.0 462.1,140.0 462.5,140.0 462.9,140.0 463.2,140.0 463.6,140.0 464.0,140.0 464.4,140.0 464.8,140.0 465.1,140.0 465.5,140.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.5,60.0 465.6,60.0 465.6,60.0 465.6,60.0 465.6,60.0 465.6,60.0 465.7,60.0 465.7,60.0 465.8,60.0 465.9,60.0 465.9,60.0 466.0,60.0 466.1,60.0 466.2,60.0 466.2,60.0 466.4,60.0 466.6,60.0 466.7,60.0 467.0,60.0 467.0,60.0 467.4,60.0 467.4,60.0 467.8,60.0 467.8,60.0 468.1,60.0 468.5,60.0 468.5,60.0 468.9,60.0 469.2,60.0 469.6,60.0 470.0,60.0 470.4,60.0 470.8,60.0 471.1,60.0 471.5,60.0 471.9,60.0 472.2,60.0 472.6,60.0 473.0,60.0 473.4,60.0 473.8,60.0 474.1,60.0 474.5,60.0 474.9,60.0 475.2,60.0 475.6,60.0 476.0,60.0 476.4,60.0 476.8,60.0 477.1,60.0 477.5,60.0 477.9,60.0 478.2,60.0 478.6,60.0 479.0,60.0 479.4,60.0 479.8,60.0 480.1,60.0 480.5,60.0 480.9,60.0 481.2,60.0 481.6,60.0 482.0,60.0 482.4,60.0 482.8,60.0 483.1,60.0 483.5,60.0 483.9,60.0 484.2,60.0 484.6,60.0 485.0,60.0 485.4,60.0 485.8,60.0 486.1,60.0 486.5,60.0 486.9,60.0 487.2,60.0 487.6,60.0 488.0,60.0 488.4,60.0 488.8,60.0 489.1,60.0 489.5,60.0 489.9,60.0 490.2,60.0 490.6,60.0 491.0,60.0 491.4,60.0 491.8,60.0 492.1,60.0 492.5,60.0 492.9,60.0 493.2,60.0 493.6,60.0 494.0,60.0 494.4,60.0 494.8,60.0 495.1,60.0 495.5,60.0 495.9,60.0 496.2,60.0 496.6,60.0 497.0,60.0 497.4,60.0 497.8,60.0 498.1,60.0 498.5,60.0 498.9,60.0 499.2,60.0 499.6,60.0 500.0,60.0 500.4,60.0 500.8,60.0 501.1,60.0 501.5,60.0 501.9,60.0 502.2,60.0 502.6,60.0 503.0,60.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.0,140.0 503.1,140.0 503.1,140.0 503.1,140.0 503.1,140.0 503.1,140.0 503.2,140.0 503.2,140.0 503.3,140.0 503.4,140.0 503.4,140.0 503.5,140.0 503.6,140.0 503.7,140.0 503.8,140.0 503.9,140.0 504.1,140.0 504.2,140.0 504.5,140.0 504.5,140.0 504.9,140.0 504.9,140.0 505.2,140.0 505.3,140.0 505.6,140.0 506.0,140.0 506.0,140.0 506.4,140.0 506.8,140.0 506.8,140.0 507.1,140.0 507.5,140.0 507.9,140.0 508.2,140.0 508.6,140.0 509.0,140.0 509.4,140.0 509.8,140.0 510.1,140.0 510.5,140.0 510.9,140.0 511.2,140.0 511.6,140.0 512.0,140.0 512.4,140.0 512.8,140.0 513.1,140.0 513.5,140.0 513.9,140.0 514.2,140.0 514.6,140.0 515.0,140.0 515.4,140.0 515.8,140.0 516.1,140.0 516.5,140.0 516.9,140.0 517.2,140.0 517.6,140.0 518.0,140.0 518.4,140.0 518.8,140.0 519.1,140.0 519.5,140.0 519.9,140.0 520.2,140.0 520.6,140.0 521.0,140.0 521.4,140.0 521.8,140.0 522.1,140.0 522.5,140.0 522.9,140.0 523.2,140.0 523.6,140.0 524.0,140.0 524.4,140.0 524.8,140.0 525.1,140.0 525.5,140.0 525.9,140.0 526.2,140.0 526.6,140.0 527.0,140.0 527.4,140.0 527.8,140.0 528.1,140.0 528.5,140.0 528.9,140.0 529.2,140.0 529.6,140.0 530.0,140.0 530.4,140.0 530.8,140.0 531.1,140.0 531.5,140.0 531.9,140.0 532.2,140.0 532.6,140.0 533.0,140.0 533.4,140.0 533.8,140.0 534.1,140.0 534.5,140.0 534.9,140.0 535.2,140.0 535.6,140.0 536.0,140.0 536.4,140.0 536.8,140.0 537.1,140.0 537.5,140.0 537.9,140.0 538.2,140.0 538.6,140.0 539.0,140.0 539.4,140.0 539.8,140.0 540.1,140.0 540.5,140.0 540.9,140.0 541.2,140.0 541.6,140.0 542.0,140.0 542.4,140.0 542.8,140.0 543.1,140.0 543.5,140.0 543.9,140.0 544.2,140.0 544.6,140.0 545.0,140.0 545.4,140.0 545.8,140.0 546.1,140.0 546.5,140.0 546.9,140.0 547.2,140.0 547.6,140.0 548.0,140.0 548.4,140.0 548.8,140.0 549.1,140.0 549.5,140.0 549.9,140.0 550.2,140.0 550.6,140.0 551.0,140.0 551.4,140.0 551.8,140.0 552.1,140.0 552.5,140.0 552.9,140.0 553.2,140.0 553.6,140.0 554.0,140.0 554.4,140.0 554.8,140.0 555.1,140.0 555.5,140.0 555.9,140.0 556.2,140.0 556.6,140.0 557.0,140.0 557.4,140.0 557.8,140.0 558.1,140.0 558.5,140.0 558.9,140.0 559.2,140.0 559.6,140.0 560.0,140.0 560.4,140.0 560.8,140.0 561.1,140.0 561.5,140.0 561.9,140.0 562.2,140.0 562.6,140.0 563.0,140.0 563.4,140.0 563.8,140.0 564.1,140.0 564.5,140.0 564.9,140.0 565.2,140.0 565.6,140.0 566.0,140.0 566.4,140.0 566.8,140.0 567.1,140.0 567.5,140.0 567.9,140.0 568.2,140.0 568.6,140.0 569.0,140.0 569.4,140.0 569.8,140.0 570.1,140.0 570.5,140.0 570.9,140.0 571.2,140.0 571.6,140.0 572.0,140.0 572.4,140.0 572.8,140.0 573.1,140.0 573.5,140.0 573.9,140.0 574.2,140.0 574.6,140.0 575.0,140.0 575.4,140.0 575.8,140.0 576.1,140.0 576.5,140.0 576.9,140.0 577.2,140.0 577.6,140.0 578.0,140.0 578.4,140.0 578.8,140.0 579.1,140.0 579.5,140.0 579.9,140.0 580.2,140.0 580.6,140.0 581.0,140.0 581.4,140.0 581.8,140.0 582.1,140.0 582.5,140.0 582.9,140.0 583.2,140.0 583.6,140.0 584.0,140.0 584.4,140.0 584.8,140.0 585.1,140.0 585.5,140.0 585.9,140.0 586.2,140.0 586.6,140.0 587.0,140.0 587.4,140.0 587.8,140.0 588.1,140.0 588.5,140.0 588.9,140.0 589.2,140.0 589.6,140.0 590.0,140.0 590.4,140.0 590.8,140.0 591.1,140.0 591.5,140.0 591.9,140.0 592.2,140.0 592.6,140.0 593.0,140.0 593.4,140.0 593.8,140.0 594.1,140.0 594.5,140.0 594.9,140.0 595.2,140.0 595.6,140.0 596.0,140.0 596.4,140.0 596.8,140.0 597.1,140.0 597.5,140.0 597.9,140.0 598.2,140.0 598.6,140.0 599.0,140.0 599.4,140.0 599.8,140.0 600.1,140.0 600.5,140.0 600.9,140.0 601.2,140.0 601.6,140.0 602.0,140.0 602.4,140.0 602.8,140.0 603.1,140.0 603.5,140.0 603.9,140.0 604.2,140.0 604.6,140.0 605.0,140.0 605.4,140.0 605.8,140.0 606.1,140.0 606.5,140.0 606.9,140.0 607.2,140.0 607.6,140.0 608.0,140.0 608.4,140.0 608.8,140.0 609.1,140.0 609.5,140.0 609.9,140.0 610.2,140.0 610.6,140.0 611.0,140.0 611.4,140.0 611.8,140.0 612.1,140.0 612.5,140.0 612.9,140.0 613.2,140.0 613.6,140.0 614.0,140.0 614.4,140.0 614.8,140.0 615.1,140.0 615.5,140.0 615.9,140.0 616.2,140.0 616.6,140.0 617.0,140.0 617.4,140.0 617.8,140.0 618.1,140.0 618.5,140.0 618.9,140.0 619.2,140.0 619.6,140.0 620.0,140.0 620.4,140.0 620.8,140.0 621.1,140.0 621.5,140.0 621.9,140.0 622.2,140.0 622.6,140.0 623.0,140.0 623.4,140.0 623.8,140.0 624.1,140.0 624.5,140.0 624.9,140.0 625.2,140.0 625.6,140.0 626.0,140.0 626.4,140.0 626.8,140.0 627.1,140.0 627.5,140.0 627.9,140.0 628.2,140.0 628.6,140.0 629.0,140.0 629.4,140.0 629.8,140.0 630.1,140.0 630.5,140.0 630.9,140.0 631.2,140.0 631.6,140.0 632.0,140.0 632.4,140.0 632.8,140.0 633.1,140.0 633.5,140.0 633.9,140.0 634.2,140.0 634.6,140.0 635.0,140.0 635.4,140.0 635.8,140.0 636.1,140.0 636.5,140.0 636.9,140.0 637.2,140.0 637.6,140.0 638.0,140.0 638.4,140.0 638.8,140.0 639.1,140.0 639.5,140.0 639.9,140.0 640.2,140.0 640.6,140.0 641.0,140.0 641.4,140.0 641.8,140.0 642.1,140.0 642.5,140.0 642.9,140.0 643.2,140.0 643.6,140.0 644.0,140.0 644.4,140.0 644.8,140.0 645.1,140.0 645.5,140.0 645.9,140.0 646.2,140.0 646.6,140.0 647.0,140.0 647.4,140.0 647.8,140.0 648.1,140.0 648.5,140.0 648.9,140.0 649.2,140.0 649.6,140.0 650.0,140.0 650.4,140.0 650.8,140.0 651.1,140.0 651.5,140.0 651.9,140.0 652.2,140.0 652.6,140.0 653.0,140.0 653.4,140.0 653.8,140.0 654.1,140.0 654.5,140.0 654.9,140.0 655.2,140.0 655.6,140.0 656.0,140.0 656.4,140.0 656.8,140.0 657.1,140.0 657.5,140.0 657.9,140.0 658.2,140.0 658.6,140.0 659.0,140.0 659.4,140.0 659.8,140.0 660.1,140.0 660.5,140.0 660.9,140.0 661.2,140.0 661.6,140.0 662.0,140.0 662.4,140.0 662.8,140.0 663.1,140.0 663.5,140.0 663.9,140.0 664.2,140.0 664.6,140.0 665.0,140.0 665.4,140.0 665.8,140.0 666.1,140.0 666.5,140.0 666.9,140.0 667.2,140.0 667.6,140.0 668.0,140.0 668.4,140.0 668.8,140.0 669.1,140.0 669.5,140.0 669.9,140.0 670.2,140.0 670.6,140.0 671.0,140.0 671.4,140.0 671.8,140.0 672.1,140.0 672.5,140.0 672.9,140.0 673.2,140.0 673.6,140.0 674.0,140.0 674.4,140.0 674.8,140.0 675.1,140.0 675.5,140.0 675.9,140.0 676.2,140.0 676.6,140.0 677.0,140.0 677.4,140.0 677.8,140.0 678.1,140.0 678.5,140.0 678.9,140.0 679.2,140.0 679.6,140.0 680.0,140.0 680.4,140.0 680.8,140.0 681.1,140.0 681.5,140.0 681.9,140.0 682.2,140.0 682.6,140.0 683.0,140.0 683.4,140.0 683.8,140.0 684.1,140.0 684.5,140.0 684.9,140.0 685.2,140.0 685.6,140.0 686.0,140.0 686.4,140.0 686.8,140.0 687.1,140.0 687.5,140.0 687.9,140.0 688.2,140.0 688.6,140.0 689.0,140.0 689.4,140.0 689.8,140.0 690.1,140.0 690.5,140.0 690.9,140.0 691.2,140.0 691.6,140.0 692.0,140.0 692.4,140.0 692.8,140.0 693.1,140.0 693.5,140.0 693.9,140.0 694.2,140.0 694.6,140.0 695.0,140.0 695.4,140.0 695.8,140.0 696.1,140.0 696.5,140.0 696.9,140.0 697.2,140.0 697.6,140.0 698.0,140.0 698.4,140.0 698.8,140.0 699.1,140.0 699.5,140.0 699.9,140.0 700.2,140.0 700.6,140.0 701.0,140.0 701.4,140.0 701.8,140.0 702.1,140.0 702.5,140.0 702.9,140.0 703.2,140.0 703.6,140.0 704.0,140.0 704.4,140.0 704.8,140.0 705.1,140.0 705.5,140.0 705.9,140.0 706.2,140.0 706.6,140.0 707.0,140.0 707.4,140.0 707.8,140.0 708.1,140.0 708.5,140.0 708.9,140.0 709.2,140.0 709.6,140.0 710.0,140.0 710.4,140.0 710.8,140.0 711.1,140.0 711.5,140.0 711.9,140.0 712.2,140.0 712.6,140.0 713.0,140.0 713.4,140.0 713.8,140.0 714.1,140.0 714.5,140.0 714.9,140.0 715.2,140.0 715.6,140.0 716.0,140.0 716.4,140.0 716.8,140.0 717.1,140.0 717.5,140.0 717.9,140.0 718.2,140.0 718.6,140.0 719.0,140.0 719.4,140.0 719.8,140.0 720.1,140.0 720.5,140.0 720.9,140.0 721.2,140.0 721.6,140.0 722.0,140.0 722.4,140.0 722.8,140.0 723.1,140.0 723.5,140.0 723.9,140.0 724.2,140.0 724.6,140.0 725.0,140.0 725.4,140.0 725.8,140.0 726.1,140.0 726.5,140.0 726.9,140.0 727.2,140.0 727.6,140.0 728.0,140.0" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round"/>
<polyline points="428.0,140.0 428.4,140.0 428.8,140.0 429.1,140.0 429.5,140.0 429.9,140.0 430.2,140.0 430.6,140.0 431.0,140.0 431.4,140.0 431.8,140.0 432.1,140.0 432.5,140.0 432.9,140.0 433.2,140.0 433.6,140.0 434.0,140.0 434.4,140.0 434.8,140.0 435.1,140.0 435.5,140.0 435.9,140.0 436.2,140.0 436.6,140.0 437.0,140.0 437.4,140.0 437.8,140.0 438.1,140.0 438.5,140.0 438.9,140.0 439.2,140.0 439.6,140.0 440.0,140.0 440.4,140.0 440.8,140.0 441.1,140.0 441.5,140.0 441.9,140.0 442.2,140.0 442.6,140.0 443.0,140.0 443.4,140.0 443.8,140.0 444.1,140.0 444.5,140.0 444.9,140.0 445.2,140.0 445.6,140.0 446.0,140.0 446.4,140.0 446.8,140.0 447.1,140.0 447.5,140.0 447.9,140.0 448.2,140.0 448.6,140.0 449.0,140.0 449.4,140.0 449.8,140.0 450.1,140.0 450.5,140.0 450.9,140.0 451.2,140.0 451.6,140.0 452.0,140.0 452.4,140.0 452.8,140.0 453.1,140.0 453.5,140.0 453.9,140.0 454.2,140.0 454.6,140.0 455.0,140.0 455.4,140.0 455.8,140.0 456.1,140.0 456.5,140.0 456.9,140.0 457.2,140.0 457.6,140.0 458.0,140.0 458.4,140.0 458.8,140.0 459.1,140.0 459.5,140.0 459.9,140.0 460.2,140.0 460.6,140.0 461.0,140.0 461.4,140.0 461.8,140.0 462.1,140.0 462.5,140.0 462.9,140.0 463.2,140.0 463.6,140.0 464.0,140.0 464.4,140.0 464.8,140.0 465.1,140.0 465.5,140.0 465.5,65.2 465.5,63.8 465.5,63.7 465.5,63.6 465.5,63.5 465.5,63.4 465.5,63.4 465.5,63.3 465.5,63.3 465.5,63.2 465.5,63.2 465.5,63.1 465.5,63.1 465.5,63.0 465.5,63.0 465.5,63.0 465.5,62.9 465.5,62.9 465.5,62.8 465.5,62.8 465.5,62.8 465.5,62.7 465.5,62.7 465.5,62.7 465.5,62.6 465.5,62.6 465.5,62.5 465.5,62.5 465.5,62.5 465.5,62.4 465.5,62.4 465.5,62.4 465.5,62.3 465.5,62.3 465.5,62.2 465.5,62.2 465.5,62.2 465.5,62.1 465.5,62.1 465.5,62.1 465.5,62.0 465.5,62.0 465.6,61.9 465.6,61.9 465.6,61.9 465.6,61.8 465.6,61.8 465.7,61.7 465.7,61.7 465.8,61.7 465.9,61.6 465.9,61.6 466.0,61.6 466.1,61.6 466.2,61.5 466.2,61.5 466.4,61.5 466.6,61.4 466.7,61.4 467.0,61.4 467.0,61.4 467.4,61.3 467.4,61.3 467.8,61.3 467.8,61.3 468.1,61.3 468.5,61.2 468.5,61.2 468.9,61.2 469.2,61.2 469.6,61.2 470.0,61.2 470.4,61.1 470.8,61.1 471.1,61.1 471.5,61.1 471.9,61.1 472.2,61.1 472.6,61.0 473.0,61.0 473.4,61.0 473.8,61.0 474.1,61.0 474.5,61.0 474.9,61.0 475.2,60.9 475.6,60.9 476.0,60.9 476.4,60.9 476.8,60.9 477.1,60.9 477.5,60.9 477.9,60.9 478.2,60.9 478.6,60.8 479.0,60.8 479.4,60.8 479.8,60.8 480.1,60.8 480.5,60.8 480.9,60.8 481.2,60.8 481.6,60.8 482.0,60.8 482.4,60.8 482.8,60.8 483.1,60.7 483.5,60.7 483.9,60.7 484.2,60.7 484.6,60.7 485.0,60.7 485.4,60.7 485.8,60.7 486.1,60.7 486.5,60.7 486.9,60.7 487.2,60.7 487.6,60.7 488.0,60.6 488.4,60.6 488.8,60.6 489.1,60.6 489.5,60.6 489.9,60.6 490.2,60.6 490.6,60.6 491.0,60.6 491.4,60.6 491.8,60.6 492.1,60.6 492.5,60.6 492.9,60.6 493.2,60.6 493.6,60.6 494.0,60.5 494.4,60.5 494.8,60.5 495.1,60.5 495.5,60.5 495.9,60.5 496.2,60.5 496.6,60.5 497.0,60.5 497.4,60.5 497.8,60.5 498.1,60.5 498.5,60.5 498.9,60.5 499.2,60.5 499.6,60.5 500.0,60.5 500.4,60.5 500.8,60.5 501.1,60.5 501.5,60.4 501.9,60.4 502.2,60.4 502.6,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.4 503.0,60.5 503.0,60.5 503.0,60.5 503.0,60.5 503.0,60.5 503.0,60.5 503.0,60.5 503.1,60.5 503.1,60.6 503.1,60.6 503.1,60.7 503.1,60.7 503.2,60.8 503.2,60.9 503.3,61.0 503.4,61.2 503.4,61.2 503.5,61.4 503.6,61.6 503.7,62.0 503.8,62.0 503.9,62.4 504.1,62.8 504.2,62.9 504.5,63.5 504.5,63.5 504.9,64.3 504.9,64.3 505.2,65.0 505.3,65.2 505.6,65.8 506.0,66.5 506.0,66.5 506.4,67.3 506.8,68.0 506.8,68.0 507.1,68.7 507.5,69.4 507.9,70.1 508.2,70.8 508.6,71.5 509.0,72.2 509.4,72.8 509.8,73.5 510.1,74.1 510.5,74.8 510.9,75.4 511.2,76.1 511.6,76.7 512.0,77.3 512.4,78.0 512.8,78.6 513.1,79.2 513.5,79.8 513.9,80.4 514.2,81.0 514.6,81.6 515.0,82.1 515.4,82.7 515.8,83.3 516.1,83.8 516.5,84.4 516.9,84.9 517.2,85.5 517.6,86.0 518.0,86.6 518.4,87.1 518.8,87.6 519.1,88.1 519.5,88.6 519.9,89.2 520.2,89.7 520.6,90.2 521.0,90.7 521.4,91.1 521.8,91.6 522.1,92.1 522.5,92.6 522.9,93.0 523.2,93.5 523.6,94.0 524.0,94.4 524.4,94.9 524.8,95.3 525.1,95.8 525.5,96.2 525.9,96.6 526.2,97.1 526.6,97.5 527.0,97.9 527.4,98.3 527.8,98.7 528.1,99.2 528.5,99.6 528.9,100.0 529.2,100.4 529.6,100.7 530.0,101.1 530.4,101.5 530.8,101.9 531.1,102.3 531.5,102.7 531.9,103.0 532.2,103.4 532.6,103.8 533.0,104.1 533.4,104.5 533.8,104.8 534.1,105.2 534.5,105.5 534.9,105.9 535.2,106.2 535.6,106.5 536.0,106.9 536.4,107.2 536.8,107.5 537.1,107.8 537.5,108.1 537.9,108.5 538.2,108.8 538.6,109.1 539.0,109.4 539.4,109.7 539.8,110.0 540.1,110.3 540.5,110.6 540.9,110.9 541.2,111.2 541.6,111.5 542.0,111.7 542.4,112.0 542.8,112.3 543.1,112.6 543.5,112.8 543.9,113.1 544.2,113.4 544.6,113.6 545.0,113.9 545.4,114.2 545.8,114.4 546.1,114.7 546.5,114.9 546.9,115.2 547.2,115.4 547.6,115.7 548.0,115.9 548.4,116.1 548.8,116.4 549.1,116.6 549.5,116.8 549.9,117.1 550.2,117.3 550.6,117.5 551.0,117.7 551.4,118.0 551.8,118.2 552.1,118.4 552.5,118.6 552.9,118.8 553.2,119.0 553.6,119.2 554.0,119.4 554.4,119.6 554.8,119.8 555.1,120.0 555.5,120.2 555.9,120.4 556.2,120.6 556.6,120.8 557.0,121.0 557.4,121.2 557.8,121.4 558.1,121.6 558.5,121.8 558.9,121.9 559.2,122.1 559.6,122.3 560.0,122.5 560.4,122.6 560.8,122.8 561.1,123.0 561.5,123.2 561.9,123.3 562.2,123.5 562.6,123.6 563.0,123.8 563.4,124.0 563.8,124.1 564.1,124.3 564.5,124.4 564.9,124.6 565.2,124.7 565.6,124.9 566.0,125.0 566.4,125.2 566.8,125.3 567.1,125.5 567.5,125.6 567.9,125.8 568.2,125.9 568.6,126.1 569.0,126.2 569.4,126.3 569.8,126.5 570.1,126.6 570.5,126.7 570.9,126.9 571.2,127.0 571.6,127.1 572.0,127.2 572.4,127.4 572.8,127.5 573.1,127.6 573.5,127.7 573.9,127.9 574.2,128.0 574.6,128.1 575.0,128.2 575.4,128.3 575.8,128.5 576.1,128.6 576.5,128.7 576.9,128.8 577.2,128.9 577.6,129.0 578.0,129.1 578.4,129.2 578.8,129.3 579.1,129.4 579.5,129.6 579.9,129.7 580.2,129.8 580.6,129.9 581.0,130.0 581.4,130.1 581.8,130.2 582.1,130.3 582.5,130.3 582.9,130.4 583.2,130.5 583.6,130.6 584.0,130.7 584.4,130.8 584.8,130.9 585.1,131.0 585.5,131.1 585.9,131.2 586.2,131.3 586.6,131.4 587.0,131.4 587.4,131.5 587.8,131.6 588.1,131.7 588.5,131.8 588.9,131.9 589.2,131.9 589.6,132.0 590.0,132.1 590.4,132.2 590.8,132.2 591.1,132.3 591.5,132.4 591.9,132.5 592.2,132.5 592.6,132.6 593.0,132.7 593.4,132.8 593.8,132.8 594.1,132.9 594.5,133.0 594.9,133.1 595.2,133.1 595.6,133.2 596.0,133.3 596.4,133.3 596.8,133.4 597.1,133.5 597.5,133.5 597.9,133.6 598.2,133.6 598.6,133.7 599.0,133.8 599.4,133.8 599.8,133.9 600.1,134.0 600.5,134.0 600.9,134.1 601.2,134.1 601.6,134.2 602.0,134.2 602.4,134.3 602.8,134.4 603.1,134.4 603.5,134.5 603.9,134.5 604.2,134.6 604.6,134.6 605.0,134.7 605.4,134.7 605.8,134.8 606.1,134.8 606.5,134.9 606.9,134.9 607.2,135.0 607.6,135.0 608.0,135.1 608.4,135.1 608.8,135.2 609.1,135.2 609.5,135.3 609.9,135.3 610.2,135.4 610.6,135.4 611.0,135.5 611.4,135.5 611.8,135.6 612.1,135.6 612.5,135.6 612.9,135.7 613.2,135.7 613.6,135.8 614.0,135.8 614.4,135.9 614.8,135.9 615.1,135.9 615.5,136.0 615.9,136.0 616.2,136.1 616.6,136.1 617.0,136.1 617.4,136.2 617.8,136.2 618.1,136.3 618.5,136.3 618.9,136.3 619.2,136.4 619.6,136.4 620.0,136.4 620.4,136.5 620.8,136.5 621.1,136.5 621.5,136.6 621.9,136.6 622.2,136.6 622.6,136.7 623.0,136.7 623.4,136.7 623.8,136.8 624.1,136.8 624.5,136.8 624.9,136.9 625.2,136.9 625.6,136.9 626.0,137.0 626.4,137.0 626.8,137.0 627.1,137.0 627.5,137.1 627.9,137.1 628.2,137.1 628.6,137.2 629.0,137.2 629.4,137.2 629.8,137.2 630.1,137.3 630.5,137.3 630.9,137.3 631.2,137.4 631.6,137.4 632.0,137.4 632.4,137.4 632.8,137.5 633.1,137.5 633.5,137.5 633.9,137.5 634.2,137.6 634.6,137.6 635.0,137.6 635.4,137.6 635.8,137.7 636.1,137.7 636.5,137.7 636.9,137.7 637.2,137.7 637.6,137.8 638.0,137.8 638.4,137.8 638.8,137.8 639.1,137.9 639.5,137.9 639.9,137.9 640.2,137.9 640.6,137.9 641.0,138.0 641.4,138.0 641.8,138.0 642.1,138.0 642.5,138.0 642.9,138.1 643.2,138.1 643.6,138.1 644.0,138.1 644.4,138.1 644.8,138.2 645.1,138.2 645.5,138.2 645.9,138.2 646.2,138.2 646.6,138.2 647.0,138.3 647.4,138.3 647.8,138.3 648.1,138.3 648.5,138.3 648.9,138.3 649.2,138.4 649.6,138.4 650.0,138.4 650.4,138.4 650.8,138.4 651.1,138.4 651.5,138.5 651.9,138.5 652.2,138.5 652.6,138.5 653.0,138.5 653.4,138.5 653.8,138.5 654.1,138.6 654.5,138.6 654.9,138.6 655.2,138.6 655.6,138.6 656.0,138.6 656.4,138.6 656.8,138.7 657.1,138.7 657.5,138.7 657.9,138.7 658.2,138.7 658.6,138.7 659.0,138.7 659.4,138.7 659.8,138.8 660.1,138.8 660.5,138.8 660.9,138.8 661.2,138.8 661.6,138.8 662.0,138.8 662.4,138.8 662.8,138.9 663.1,138.9 663.5,138.9 663.9,138.9 664.2,138.9 664.6,138.9 665.0,138.9 665.4,138.9 665.8,138.9 666.1,139.0 666.5,139.0 666.9,139.0 667.2,139.0 667.6,139.0 668.0,139.0 668.4,139.0 668.8,139.0 669.1,139.0 669.5,139.0 669.9,139.1 670.2,139.1 670.6,139.1 671.0,139.1 671.4,139.1 671.8,139.1 672.1,139.1 672.5,139.1 672.9,139.1 673.2,139.1 673.6,139.1 674.0,139.1 674.4,139.2 674.8,139.2 675.1,139.2 675.5,139.2 675.9,139.2 676.2,139.2 676.6,139.2 677.0,139.2 677.4,139.2 677.8,139.2 678.1,139.2 678.5,139.2 678.9,139.3 679.2,139.3 679.6,139.3 680.0,139.3 680.4,139.3 680.8,139.3 681.1,139.3 681.5,139.3 681.9,139.3 682.2,139.3 682.6,139.3 683.0,139.3 683.4,139.3 683.8,139.3 684.1,139.3 684.5,139.4 684.9,139.4 685.2,139.4 685.6,139.4 686.0,139.4 686.4,139.4 686.8,139.4 687.1,139.4 687.5,139.4 687.9,139.4 688.2,139.4 688.6,139.4 689.0,139.4 689.4,139.4 689.8,139.4 690.1,139.4 690.5,139.5 690.9,139.5 691.2,139.5 691.6,139.5 692.0,139.5 692.4,139.5 692.8,139.5 693.1,139.5 693.5,139.5 693.9,139.5 694.2,139.5 694.6,139.5 695.0,139.5 695.4,139.5 695.8,139.5 696.1,139.5 696.5,139.5 696.9,139.5 697.2,139.5 697.6,139.5 698.0,139.6 698.4,139.6 698.8,139.6 699.1,139.6 699.5,139.6 699.9,139.6 700.2,139.6 700.6,139.6 701.0,139.6 701.4,139.6 701.8,139.6 702.1,139.6 702.5,139.6 702.9,139.6 703.2,139.6 703.6,139.6 704.0,139.6 704.4,139.6 704.8,139.6 705.1,139.6 705.5,139.6 705.9,139.6 706.2,139.6 706.6,139.6 707.0,139.6 707.4,139.6 707.8,139.7 708.1,139.7 708.5,139.7 708.9,139.7 709.2,139.7 709.6,139.7 710.0,139.7 710.4,139.7 710.8,139.7 711.1,139.7 711.5,139.7 711.9,139.7 712.2,139.7 712.6,139.7 713.0,139.7 713.4,139.7 713.8,139.7 714.1,139.7 714.5,139.7 714.9,139.7 715.2,139.7 715.6,139.7 716.0,139.7 716.4,139.7 716.8,139.7 717.1,139.7 717.5,139.7 717.9,139.7 718.2,139.7 718.6,139.7 719.0,139.7 719.4,139.7 719.8,139.7 720.1,139.8 720.5,139.8 720.9,139.8 721.2,139.8 721.6,139.8 722.0,139.8 722.4,139.8 722.8,139.8 723.1,139.8 723.5,139.8 723.9,139.8 724.2,139.8 724.6,139.8 725.0,139.8 725.4,139.8 725.8,139.8 726.1,139.8 726.5,139.8 726.9,139.8 727.2,139.8 727.6,139.8 728.0,139.8" fill="none" stroke="var(--accent)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="551.8" y="76.0" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">state φ2</text>
<text x="638.0" y="125.6" font-size="12" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">vds</text>
<path d="M48 302.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="306.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0</text>
<path d="M48 251.1 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="255.1" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0.5</text>
<path d="M48 200.2 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="204.2" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">1</text>
<text x="42.0" y="181.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">Id, relative</text>
<text x="48.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">0</text>
<text x="123.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">2</text>
<text x="198.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">4</text>
<text x="273.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">6</text>
<text x="348.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">8</text>
<text x="348.0" y="332.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">time, ms</text>
<polyline points="48.0,200.2 48.4,200.2 48.8,200.2 49.1,200.2 49.5,200.2 49.9,200.2 50.2,200.2 50.6,200.2 51.0,200.2 51.4,200.2 51.8,200.2 52.1,200.2 52.5,200.2 52.9,200.2 53.2,200.2 53.6,200.2 54.0,200.2 54.4,200.2 54.8,200.2 55.1,200.2 55.5,200.2 55.9,200.2 56.2,200.2 56.6,200.2 57.0,200.2 57.4,200.2 57.8,200.2 58.1,200.2 58.5,200.2 58.9,200.2 59.2,200.2 59.6,200.2 60.0,200.2 60.4,200.2 60.8,200.2 61.1,200.2 61.5,200.2 61.9,200.2 62.2,200.2 62.6,200.2 63.0,200.2 63.4,200.2 63.8,200.2 64.1,200.2 64.5,200.2 64.9,200.2 65.2,200.2 65.6,200.2 66.0,200.2 66.4,200.2 66.8,200.2 67.1,200.2 67.5,200.2 67.9,200.2 68.2,200.2 68.6,200.2 69.0,200.2 69.4,200.2 69.8,200.2 70.1,200.2 70.5,200.2 70.9,200.2 71.2,200.2 71.6,200.2 72.0,200.2 72.4,200.2 72.8,200.2 73.1,200.2 73.5,200.2 73.9,200.2 74.2,200.2 74.6,200.2 75.0,200.2 75.4,200.2 75.8,200.2 76.1,200.2 76.5,200.2 76.9,200.2 77.2,200.2 77.6,200.2 78.0,200.2 78.4,200.2 78.8,200.2 79.1,200.2 79.5,200.2 79.9,200.2 80.2,200.2 80.6,200.2 81.0,200.2 81.4,200.2 81.8,200.2 82.1,200.2 82.5,200.2 82.9,200.2 83.2,200.2 83.6,200.2 84.0,200.2 84.4,200.2 84.8,200.2 85.1,200.2 85.5,200.2 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.7,302.0 85.7,302.0 85.8,302.0 85.9,302.0 85.9,302.0 86.0,302.0 86.1,302.0 86.2,302.0 86.2,302.0 86.4,302.0 86.6,302.0 86.7,302.0 87.0,302.0 87.0,302.0 87.4,302.0 87.4,302.0 87.8,302.0 87.8,302.0 88.1,302.0 88.5,302.0 88.5,302.0 88.9,302.0 89.2,302.0 89.6,302.0 90.0,302.0 90.4,302.0 90.8,302.0 91.1,302.0 91.5,302.0 91.9,302.0 92.2,302.0 92.6,302.0 93.0,302.0 93.4,302.0 93.8,302.0 94.1,302.0 94.5,302.0 94.9,302.0 95.2,302.0 95.6,302.0 96.0,302.0 96.4,302.0 96.8,302.0 97.1,302.0 97.5,302.0 97.9,302.0 98.2,302.0 98.6,302.0 99.0,302.0 99.4,302.0 99.8,302.0 100.1,302.0 100.5,302.0 100.9,302.0 101.2,302.0 101.6,302.0 102.0,302.0 102.4,302.0 102.8,302.0 103.1,302.0 103.5,302.0 103.9,302.0 104.2,302.0 104.6,302.0 105.0,302.0 105.4,302.0 105.8,302.0 106.1,302.0 106.5,302.0 106.9,302.0 107.2,302.0 107.6,302.0 108.0,302.0 108.4,302.0 108.8,302.0 109.1,302.0 109.5,302.0 109.9,302.0 110.2,302.0 110.6,302.0 111.0,302.0 111.4,302.0 111.8,302.0 112.1,302.0 112.5,302.0 112.9,302.0 113.3,302.0 113.6,302.0 114.0,302.0 114.4,302.0 114.8,302.0 115.1,302.0 115.5,302.0 115.9,302.0 116.3,302.0 116.6,302.0 117.0,302.0 117.4,302.0 117.8,302.0 118.1,302.0 118.5,302.0 118.9,302.0 119.2,302.0 119.6,302.0 120.0,302.0 120.4,302.0 120.8,302.0 121.1,302.0 121.5,302.0 121.9,302.0 122.2,302.0 122.6,302.0 123.0,302.0 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.0,200.2 123.1,200.2 123.1,200.2 123.1,200.2 123.1,200.2 123.1,200.2 123.2,200.2 123.2,200.2 123.3,200.2 123.4,200.2 123.4,200.2 123.5,200.2 123.6,200.2 123.7,200.2 123.8,200.2 123.9,200.2 124.1,200.2 124.2,200.2 124.5,200.2 124.5,200.2 124.9,200.2 124.9,200.2 125.2,200.2 125.3,200.2 125.6,200.2 126.0,200.2 126.0,200.2 126.4,200.2 126.8,200.2 126.8,200.2 127.1,200.2 127.5,200.2 127.9,200.2 128.2,200.2 128.6,200.2 129.0,200.2 129.4,200.2 129.8,200.2 130.1,200.2 130.5,200.2 130.9,200.2 131.2,200.2 131.6,200.2 132.0,200.2 132.4,200.2 132.8,200.2 133.1,200.2 133.5,200.2 133.9,200.2 134.2,200.2 134.6,200.2 135.0,200.2 135.4,200.2 135.8,200.2 136.1,200.2 136.5,200.2 136.9,200.2 137.2,200.2 137.6,200.2 138.0,200.2 138.4,200.2 138.8,200.2 139.1,200.2 139.5,200.2 139.9,200.2 140.2,200.2 140.6,200.2 141.0,200.2 141.4,200.2 141.8,200.2 142.1,200.2 142.5,200.2 142.9,200.2 143.2,200.2 143.6,200.2 144.0,200.2 144.4,200.2 144.8,200.2 145.1,200.2 145.5,200.2 145.9,200.2 146.2,200.2 146.6,200.2 147.0,200.2 147.4,200.2 147.8,200.2 148.1,200.2 148.5,200.2 148.9,200.2 149.2,200.2 149.6,200.2 150.0,200.2 150.4,200.2 150.8,200.2 151.1,200.2 151.5,200.2 151.9,200.2 152.2,200.2 152.6,200.2 153.0,200.2 153.4,200.2 153.8,200.2 154.1,200.2 154.5,200.2 154.9,200.2 155.2,200.2 155.6,200.2 156.0,200.2 156.4,200.2 156.8,200.2 157.1,200.2 157.5,200.2 157.9,200.2 158.2,200.2 158.6,200.2 159.0,200.2 159.4,200.2 159.8,200.2 160.1,200.2 160.5,200.2 160.9,200.2 161.2,200.2 161.6,200.2 162.0,200.2 162.4,200.2 162.8,200.2 163.1,200.2 163.5,200.2 163.9,200.2 164.2,200.2 164.6,200.2 165.0,200.2 165.4,200.2 165.8,200.2 166.1,200.2 166.5,200.2 166.9,200.2 167.2,200.2 167.6,200.2 168.0,200.2 168.4,200.2 168.8,200.2 169.1,200.2 169.5,200.2 169.9,200.2 170.2,200.2 170.6,200.2 171.0,200.2 171.4,200.2 171.8,200.2 172.1,200.2 172.5,200.2 172.9,200.2 173.2,200.2 173.6,200.2 174.0,200.2 174.4,200.2 174.8,200.2 175.1,200.2 175.5,200.2 175.9,200.2 176.2,200.2 176.6,200.2 177.0,200.2 177.4,200.2 177.8,200.2 178.1,200.2 178.5,200.2 178.9,200.2 179.2,200.2 179.6,200.2 180.0,200.2 180.4,200.2 180.8,200.2 181.1,200.2 181.5,200.2 181.9,200.2 182.2,200.2 182.6,200.2 183.0,200.2 183.4,200.2 183.8,200.2 184.1,200.2 184.5,200.2 184.9,200.2 185.3,200.2 185.6,200.2 186.0,200.2 186.4,200.2 186.8,200.2 187.1,200.2 187.5,200.2 187.9,200.2 188.2,200.2 188.6,200.2 189.0,200.2 189.4,200.2 189.8,200.2 190.1,200.2 190.5,200.2 190.9,200.2 191.2,200.2 191.6,200.2 192.0,200.2 192.4,200.2 192.8,200.2 193.1,200.2 193.5,200.2 193.9,200.2 194.2,200.2 194.6,200.2 195.0,200.2 195.4,200.2 195.8,200.2 196.1,200.2 196.5,200.2 196.9,200.2 197.2,200.2 197.6,200.2 198.0,200.2 198.4,200.2 198.8,200.2 199.1,200.2 199.5,200.2 199.9,200.2 200.3,200.2 200.6,200.2 201.0,200.2 201.4,200.2 201.8,200.2 202.1,200.2 202.5,200.2 202.9,200.2 203.3,200.2 203.6,200.2 204.0,200.2 204.4,200.2 204.8,200.2 205.1,200.2 205.5,200.2 205.9,200.2 206.3,200.2 206.6,200.2 207.0,200.2 207.4,200.2 207.8,200.2 208.1,200.2 208.5,200.2 208.9,200.2 209.2,200.2 209.6,200.2 210.0,200.2 210.4,200.2 210.8,200.2 211.1,200.2 211.5,200.2 211.9,200.2 212.2,200.2 212.6,200.2 213.0,200.2 213.4,200.2 213.8,200.2 214.1,200.2 214.5,200.2 214.9,200.2 215.3,200.2 215.6,200.2 216.0,200.2 216.4,200.2 216.8,200.2 217.1,200.2 217.5,200.2 217.9,200.2 218.3,200.2 218.6,200.2 219.0,200.2 219.4,200.2 219.8,200.2 220.1,200.2 220.5,200.2 220.9,200.2 221.2,200.2 221.6,200.2 222.0,200.2 222.4,200.2 222.8,200.2 223.1,200.2 223.5,200.2 223.9,200.2 224.2,200.2 224.6,200.2 225.0,200.2 225.4,200.2 225.8,200.2 226.1,200.2 226.5,200.2 226.9,200.2 227.2,200.2 227.6,200.2 228.0,200.2 228.4,200.2 228.8,200.2 229.1,200.2 229.5,200.2 229.9,200.2 230.2,200.2 230.6,200.2 231.0,200.2 231.4,200.2 231.8,200.2 232.1,200.2 232.5,200.2 232.9,200.2 233.3,200.2 233.6,200.2 234.0,200.2 234.4,200.2 234.8,200.2 235.1,200.2 235.5,200.2 235.9,200.2 236.3,200.2 236.6,200.2 237.0,200.2 237.4,200.2 237.8,200.2 238.1,200.2 238.5,200.2 238.9,200.2 239.3,200.2 239.6,200.2 240.0,200.2 240.4,200.2 240.8,200.2 241.1,200.2 241.5,200.2 241.9,200.2 242.3,200.2 242.6,200.2 243.0,200.2 243.4,200.2 243.8,200.2 244.1,200.2 244.5,200.2 244.9,200.2 245.3,200.2 245.6,200.2 246.0,200.2 246.4,200.2 246.8,200.2 247.1,200.2 247.5,200.2 247.9,200.2 248.2,200.2 248.6,200.2 249.0,200.2 249.4,200.2 249.8,200.2 250.1,200.2 250.5,200.2 250.9,200.2 251.2,200.2 251.6,200.2 252.0,200.2 252.4,200.2 252.8,200.2 253.1,200.2 253.5,200.2 253.9,200.2 254.3,200.2 254.6,200.2 255.0,200.2 255.4,200.2 255.8,200.2 256.1,200.2 256.5,200.2 256.9,200.2 257.2,200.2 257.6,200.2 258.0,200.2 258.4,200.2 258.8,200.2 259.1,200.2 259.5,200.2 259.9,200.2 260.2,200.2 260.6,200.2 261.0,200.2 261.4,200.2 261.8,200.2 262.1,200.2 262.5,200.2 262.9,200.2 263.2,200.2 263.6,200.2 264.0,200.2 264.4,200.2 264.8,200.2 265.1,200.2 265.5,200.2 265.9,200.2 266.2,200.2 266.6,200.2 267.0,200.2 267.4,200.2 267.8,200.2 268.1,200.2 268.5,200.2 268.9,200.2 269.2,200.2 269.6,200.2 270.0,200.2 270.4,200.2 270.8,200.2 271.1,200.2 271.5,200.2 271.9,200.2 272.2,200.2 272.6,200.2 273.0,200.2 273.4,200.2 273.8,200.2 274.1,200.2 274.5,200.2 274.9,200.2 275.2,200.2 275.6,200.2 276.0,200.2 276.4,200.2 276.8,200.2 277.1,200.2 277.5,200.2 277.9,200.2 278.2,200.2 278.6,200.2 279.0,200.2 279.4,200.2 279.8,200.2 280.1,200.2 280.5,200.2 280.9,200.2 281.2,200.2 281.6,200.2 282.0,200.2 282.4,200.2 282.8,200.2 283.1,200.2 283.5,200.2 283.9,200.2 284.2,200.2 284.6,200.2 285.0,200.2 285.4,200.2 285.8,200.2 286.1,200.2 286.5,200.2 286.9,200.2 287.2,200.2 287.6,200.2 288.0,200.2 288.4,200.2 288.8,200.2 289.1,200.2 289.5,200.2 289.9,200.2 290.2,200.2 290.6,200.2 291.0,200.2 291.4,200.2 291.8,200.2 292.1,200.2 292.5,200.2 292.9,200.2 293.2,200.2 293.6,200.2 294.0,200.2 294.4,200.2 294.8,200.2 295.1,200.2 295.5,200.2 295.9,200.2 296.2,200.2 296.6,200.2 297.0,200.2 297.4,200.2 297.8,200.2 298.1,200.2 298.5,200.2 298.9,200.2 299.2,200.2 299.6,200.2 300.0,200.2 300.4,200.2 300.8,200.2 301.1,200.2 301.5,200.2 301.9,200.2 302.2,200.2 302.6,200.2 303.0,200.2 303.4,200.2 303.8,200.2 304.1,200.2 304.5,200.2 304.9,200.2 305.2,200.2 305.6,200.2 306.0,200.2 306.4,200.2 306.8,200.2 307.1,200.2 307.5,200.2 307.9,200.2 308.3,200.2 308.6,200.2 309.0,200.2 309.4,200.2 309.8,200.2 310.1,200.2 310.5,200.2 310.9,200.2 311.2,200.2 311.6,200.2 312.0,200.2 312.4,200.2 312.8,200.2 313.1,200.2 313.5,200.2 313.9,200.2 314.2,200.2 314.6,200.2 315.0,200.2 315.4,200.2 315.8,200.2 316.1,200.2 316.5,200.2 316.9,200.2 317.2,200.2 317.6,200.2 318.0,200.2 318.4,200.2 318.8,200.2 319.1,200.2 319.5,200.2 319.9,200.2 320.2,200.2 320.6,200.2 321.0,200.2 321.4,200.2 321.8,200.2 322.1,200.2 322.5,200.2 322.9,200.2 323.2,200.2 323.6,200.2 324.0,200.2 324.4,200.2 324.8,200.2 325.1,200.2 325.5,200.2 325.9,200.2 326.3,200.2 326.6,200.2 327.0,200.2 327.4,200.2 327.8,200.2 328.1,200.2 328.5,200.2 328.9,200.2 329.3,200.2 329.6,200.2 330.0,200.2 330.4,200.2 330.8,200.2 331.1,200.2 331.5,200.2 331.9,200.2 332.3,200.2 332.6,200.2 333.0,200.2 333.4,200.2 333.8,200.2 334.1,200.2 334.5,200.2 334.9,200.2 335.3,200.2 335.6,200.2 336.0,200.2 336.4,200.2 336.8,200.2 337.1,200.2 337.5,200.2 337.9,200.2 338.2,200.2 338.6,200.2 339.0,200.2 339.4,200.2 339.8,200.2 340.1,200.2 340.5,200.2 340.9,200.2 341.2,200.2 341.6,200.2 342.0,200.2 342.4,200.2 342.8,200.2 343.1,200.2 343.5,200.2 343.9,200.2 344.2,200.2 344.6,200.2 345.0,200.2 345.4,200.2 345.8,200.2 346.1,200.2 346.5,200.2 346.9,200.2 347.3,200.2 347.6,200.2 348.0,200.2" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round" stroke-dasharray="5 4"/>
<polyline points="48.0,200.2 48.4,200.2 48.8,200.2 49.1,200.2 49.5,200.2 49.9,200.2 50.2,200.2 50.6,200.2 51.0,200.2 51.4,200.2 51.8,200.2 52.1,200.2 52.5,200.2 52.9,200.2 53.2,200.2 53.6,200.2 54.0,200.2 54.4,200.2 54.8,200.2 55.1,200.2 55.5,200.2 55.9,200.2 56.2,200.2 56.6,200.2 57.0,200.2 57.4,200.2 57.8,200.2 58.1,200.2 58.5,200.2 58.9,200.2 59.2,200.2 59.6,200.2 60.0,200.2 60.4,200.2 60.8,200.2 61.1,200.2 61.5,200.2 61.9,200.2 62.2,200.2 62.6,200.2 63.0,200.2 63.4,200.2 63.8,200.2 64.1,200.2 64.5,200.2 64.9,200.2 65.2,200.2 65.6,200.2 66.0,200.2 66.4,200.2 66.8,200.2 67.1,200.2 67.5,200.2 67.9,200.2 68.2,200.2 68.6,200.2 69.0,200.2 69.4,200.2 69.8,200.2 70.1,200.2 70.5,200.2 70.9,200.2 71.2,200.2 71.6,200.2 72.0,200.2 72.4,200.2 72.8,200.2 73.1,200.2 73.5,200.2 73.9,200.2 74.2,200.2 74.6,200.2 75.0,200.2 75.4,200.2 75.8,200.2 76.1,200.2 76.5,200.2 76.9,200.2 77.2,200.2 77.6,200.2 78.0,200.2 78.4,200.2 78.8,200.2 79.1,200.2 79.5,200.2 79.9,200.2 80.2,200.2 80.6,200.2 81.0,200.2 81.4,200.2 81.8,200.2 82.1,200.2 82.5,200.2 82.9,200.2 83.2,200.2 83.6,200.2 84.0,200.2 84.4,200.2 84.8,200.2 85.1,200.2 85.5,200.2 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.5,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.6,302.0 85.7,302.0 85.7,302.0 85.8,302.0 85.9,302.0 85.9,302.0 86.0,302.0 86.1,302.0 86.2,302.0 86.2,302.0 86.4,302.0 86.6,302.0 86.7,302.0 87.0,302.0 87.0,302.0 87.4,302.0 87.4,302.0 87.8,302.0 87.8,302.0 88.1,302.0 88.5,302.0 88.5,302.0 88.9,302.0 89.2,302.0 89.6,302.0 90.0,302.0 90.4,302.0 90.8,302.0 91.1,302.0 91.5,302.0 91.9,302.0 92.2,302.0 92.6,302.0 93.0,302.0 93.4,302.0 93.8,302.0 94.1,302.0 94.5,302.0 94.9,302.0 95.2,302.0 95.6,302.0 96.0,302.0 96.4,302.0 96.8,302.0 97.1,302.0 97.5,302.0 97.9,302.0 98.2,302.0 98.6,302.0 99.0,302.0 99.4,302.0 99.8,302.0 100.1,302.0 100.5,302.0 100.9,302.0 101.2,302.0 101.6,302.0 102.0,302.0 102.4,302.0 102.8,302.0 103.1,302.0 103.5,302.0 103.9,302.0 104.2,302.0 104.6,302.0 105.0,302.0 105.4,302.0 105.8,302.0 106.1,302.0 106.5,302.0 106.9,302.0 107.2,302.0 107.6,302.0 108.0,302.0 108.4,302.0 108.8,302.0 109.1,302.0 109.5,302.0 109.9,302.0 110.2,302.0 110.6,302.0 111.0,302.0 111.4,302.0 111.8,302.0 112.1,302.0 112.5,302.0 112.9,302.0 113.3,302.0 113.6,302.0 114.0,302.0 114.4,302.0 114.8,302.0 115.1,302.0 115.5,302.0 115.9,302.0 116.3,302.0 116.6,302.0 117.0,302.0 117.4,302.0 117.8,302.0 118.1,302.0 118.5,302.0 118.9,302.0 119.2,302.0 119.6,302.0 120.0,302.0 120.4,302.0 120.8,302.0 121.1,302.0 121.5,302.0 121.9,302.0 122.2,302.0 122.6,302.0 123.0,302.0 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.0,226.8 123.1,226.8 123.1,226.7 123.1,226.7 123.1,226.7 123.1,226.7 123.2,226.7 123.2,226.6 123.3,226.6 123.4,226.5 123.4,226.5 123.5,226.4 123.6,226.3 123.7,226.2 123.8,226.2 123.9,226.1 124.1,225.9 124.2,225.9 124.5,225.6 124.5,225.6 124.9,225.3 124.9,225.3 125.2,225.1 125.3,225.0 125.6,224.8 126.0,224.5 126.0,224.5 126.4,224.2 126.8,224.0 126.8,224.0 127.1,223.7 127.5,223.4 127.9,223.2 128.2,222.9 128.6,222.7 129.0,222.4 129.4,222.2 129.8,221.9 130.1,221.7 130.5,221.4 130.9,221.2 131.2,221.0 131.6,220.7 132.0,220.5 132.4,220.3 132.8,220.0 133.1,219.8 133.5,219.6 133.9,219.4 134.2,219.2 134.6,219.0 135.0,218.7 135.4,218.5 135.8,218.3 136.1,218.1 136.5,217.9 136.9,217.7 137.2,217.5 137.6,217.4 138.0,217.2 138.4,217.0 138.8,216.8 139.1,216.6 139.5,216.4 139.9,216.2 140.2,216.1 140.6,215.9 141.0,215.7 141.4,215.5 141.8,215.4 142.1,215.2 142.5,215.0 142.9,214.9 143.2,214.7 143.6,214.6 144.0,214.4 144.4,214.2 144.8,214.1 145.1,213.9 145.5,213.8 145.9,213.6 146.2,213.5 146.6,213.3 147.0,213.2 147.4,213.1 147.8,212.9 148.1,212.8 148.5,212.6 148.9,212.5 149.2,212.4 149.6,212.2 150.0,212.1 150.4,212.0 150.8,211.9 151.1,211.7 151.5,211.6 151.9,211.5 152.2,211.4 152.6,211.2 153.0,211.1 153.4,211.0 153.8,210.9 154.1,210.8 154.5,210.6 154.9,210.5 155.2,210.4 155.6,210.3 156.0,210.2 156.4,210.1 156.8,210.0 157.1,209.9 157.5,209.8 157.9,209.7 158.2,209.6 158.6,209.5 159.0,209.4 159.4,209.3 159.8,209.2 160.1,209.1 160.5,209.0 160.9,208.9 161.2,208.8 161.6,208.7 162.0,208.6 162.4,208.5 162.8,208.4 163.1,208.4 163.5,208.3 163.9,208.2 164.2,208.1 164.6,208.0 165.0,207.9 165.4,207.8 165.8,207.8 166.1,207.7 166.5,207.6 166.9,207.5 167.2,207.4 167.6,207.4 168.0,207.3 168.4,207.2 168.8,207.1 169.1,207.1 169.5,207.0 169.9,206.9 170.2,206.9 170.6,206.8 171.0,206.7 171.4,206.6 171.8,206.6 172.1,206.5 172.5,206.4 172.9,206.4 173.2,206.3 173.6,206.3 174.0,206.2 174.4,206.1 174.8,206.1 175.1,206.0 175.5,205.9 175.9,205.9 176.2,205.8 176.6,205.8 177.0,205.7 177.4,205.6 177.8,205.6 178.1,205.5 178.5,205.5 178.9,205.4 179.2,205.4 179.6,205.3 180.0,205.3 180.4,205.2 180.8,205.2 181.1,205.1 181.5,205.1 181.9,205.0 182.2,205.0 182.6,204.9 183.0,204.9 183.4,204.8 183.8,204.8 184.1,204.7 184.5,204.7 184.9,204.6 185.3,204.6 185.6,204.5 186.0,204.5 186.4,204.4 186.8,204.4 187.1,204.3 187.5,204.3 187.9,204.3 188.2,204.2 188.6,204.2 189.0,204.1 189.4,204.1 189.8,204.1 190.1,204.0 190.5,204.0 190.9,203.9 191.2,203.9 191.6,203.9 192.0,203.8 192.4,203.8 192.8,203.7 193.1,203.7 193.5,203.7 193.9,203.6 194.2,203.6 194.6,203.6 195.0,203.5 195.4,203.5 195.8,203.5 196.1,203.4 196.5,203.4 196.9,203.4 197.2,203.3 197.6,203.3 198.0,203.3 198.4,203.2 198.8,203.2 199.1,203.2 199.5,203.1 199.9,203.1 200.3,203.1 200.6,203.1 201.0,203.0 201.4,203.0 201.8,203.0 202.1,202.9 202.5,202.9 202.9,202.9 203.3,202.9 203.6,202.8 204.0,202.8 204.4,202.8 204.8,202.7 205.1,202.7 205.5,202.7 205.9,202.7 206.3,202.6 206.6,202.6 207.0,202.6 207.4,202.6 207.8,202.5 208.1,202.5 208.5,202.5 208.9,202.5 209.2,202.5 209.6,202.4 210.0,202.4 210.4,202.4 210.8,202.4 211.1,202.3 211.5,202.3 211.9,202.3 212.2,202.3 212.6,202.3 213.0,202.2 213.4,202.2 213.8,202.2 214.1,202.2 214.5,202.2 214.9,202.1 215.3,202.1 215.6,202.1 216.0,202.1 216.4,202.1 216.8,202.0 217.1,202.0 217.5,202.0 217.9,202.0 218.3,202.0 218.6,201.9 219.0,201.9 219.4,201.9 219.8,201.9 220.1,201.9 220.5,201.9 220.9,201.8 221.2,201.8 221.6,201.8 222.0,201.8 222.4,201.8 222.8,201.8 223.1,201.7 223.5,201.7 223.9,201.7 224.2,201.7 224.6,201.7 225.0,201.7 225.4,201.6 225.8,201.6 226.1,201.6 226.5,201.6 226.9,201.6 227.2,201.6 227.6,201.6 228.0,201.5 228.4,201.5 228.8,201.5 229.1,201.5 229.5,201.5 229.9,201.5 230.2,201.5 230.6,201.5 231.0,201.4 231.4,201.4 231.8,201.4 232.1,201.4 232.5,201.4 232.9,201.4 233.3,201.4 233.6,201.4 234.0,201.3 234.4,201.3 234.8,201.3 235.1,201.3 235.5,201.3 235.9,201.3 236.3,201.3 236.6,201.3 237.0,201.3 237.4,201.2 237.8,201.2 238.1,201.2 238.5,201.2 238.9,201.2 239.3,201.2 239.6,201.2 240.0,201.2 240.4,201.2 240.8,201.2 241.1,201.1 241.5,201.1 241.9,201.1 242.3,201.1 242.6,201.1 243.0,201.1 243.4,201.1 243.8,201.1 244.1,201.1 244.5,201.1 244.9,201.1 245.3,201.0 245.6,201.0 246.0,201.0 246.4,201.0 246.8,201.0 247.1,201.0 247.5,201.0 247.9,201.0 248.2,201.0 248.6,201.0 249.0,201.0 249.4,201.0 249.8,200.9 250.1,200.9 250.5,200.9 250.9,200.9 251.2,200.9 251.6,200.9 252.0,200.9 252.4,200.9 252.8,200.9 253.1,200.9 253.5,200.9 253.9,200.9 254.3,200.9 254.6,200.9 255.0,200.8 255.4,200.8 255.8,200.8 256.1,200.8 256.5,200.8 256.9,200.8 257.2,200.8 257.6,200.8 258.0,200.8 258.4,200.8 258.8,200.8 259.1,200.8 259.5,200.8 259.9,200.8 260.2,200.8 260.6,200.8 261.0,200.7 261.4,200.7 261.8,200.7 262.1,200.7 262.5,200.7 262.9,200.7 263.2,200.7 263.6,200.7 264.0,200.7 264.4,200.7 264.8,200.7 265.1,200.7 265.5,200.7 265.9,200.7 266.2,200.7 266.6,200.7 267.0,200.7 267.4,200.7 267.8,200.7 268.1,200.6 268.5,200.6 268.9,200.6 269.2,200.6 269.6,200.6 270.0,200.6 270.4,200.6 270.8,200.6 271.1,200.6 271.5,200.6 271.9,200.6 272.2,200.6 272.6,200.6 273.0,200.6 273.4,200.6 273.8,200.6 274.1,200.6 274.5,200.6 274.9,200.6 275.2,200.6 275.6,200.6 276.0,200.6 276.4,200.6 276.8,200.6 277.1,200.5 277.5,200.5 277.9,200.5 278.2,200.5 278.6,200.5 279.0,200.5 279.4,200.5 279.8,200.5 280.1,200.5 280.5,200.5 280.9,200.5 281.2,200.5 281.6,200.5 282.0,200.5 282.4,200.5 282.8,200.5 283.1,200.5 283.5,200.5 283.9,200.5 284.2,200.5 284.6,200.5 285.0,200.5 285.4,200.5 285.8,200.5 286.1,200.5 286.5,200.5 286.9,200.5 287.2,200.5 287.6,200.5 288.0,200.5 288.4,200.5 288.8,200.5 289.1,200.4 289.5,200.4 289.9,200.4 290.2,200.4 290.6,200.4 291.0,200.4 291.4,200.4 291.8,200.4 292.1,200.4 292.5,200.4 292.9,200.4 293.2,200.4 293.6,200.4 294.0,200.4 294.4,200.4 294.8,200.4 295.1,200.4 295.5,200.4 295.9,200.4 296.2,200.4 296.6,200.4 297.0,200.4 297.4,200.4 297.8,200.4 298.1,200.4 298.5,200.4 298.9,200.4 299.2,200.4 299.6,200.4 300.0,200.4 300.4,200.4 300.8,200.4 301.1,200.4 301.5,200.4 301.9,200.4 302.2,200.4 302.6,200.4 303.0,200.4 303.4,200.4 303.8,200.4 304.1,200.4 304.5,200.4 304.9,200.4 305.2,200.4 305.6,200.4 306.0,200.4 306.4,200.3 306.8,200.3 307.1,200.3 307.5,200.3 307.9,200.3 308.3,200.3 308.6,200.3 309.0,200.3 309.4,200.3 309.8,200.3 310.1,200.3 310.5,200.3 310.9,200.3 311.2,200.3 311.6,200.3 312.0,200.3 312.4,200.3 312.8,200.3 313.1,200.3 313.5,200.3 313.9,200.3 314.2,200.3 314.6,200.3 315.0,200.3 315.4,200.3 315.8,200.3 316.1,200.3 316.5,200.3 316.9,200.3 317.2,200.3 317.6,200.3 318.0,200.3 318.4,200.3 318.8,200.3 319.1,200.3 319.5,200.3 319.9,200.3 320.2,200.3 320.6,200.3 321.0,200.3 321.4,200.3 321.8,200.3 322.1,200.3 322.5,200.3 322.9,200.3 323.2,200.3 323.6,200.3 324.0,200.3 324.4,200.3 324.8,200.3 325.1,200.3 325.5,200.3 325.9,200.3 326.3,200.3 326.6,200.3 327.0,200.3 327.4,200.3 327.8,200.3 328.1,200.3 328.5,200.3 328.9,200.3 329.3,200.3 329.6,200.3 330.0,200.3 330.4,200.3 330.8,200.3 331.1,200.3 331.5,200.3 331.9,200.3 332.3,200.3 332.6,200.3 333.0,200.3 333.4,200.3 333.8,200.3 334.1,200.3 334.5,200.3 334.9,200.3 335.3,200.3 335.6,200.3 336.0,200.3 336.4,200.3 336.8,200.3 337.1,200.3 337.5,200.3 337.9,200.3 338.2,200.3 338.6,200.3 339.0,200.3 339.4,200.3 339.8,200.3 340.1,200.3 340.5,200.2 340.9,200.2 341.2,200.2 341.6,200.2 342.0,200.2 342.4,200.2 342.8,200.2 343.1,200.2 343.5,200.2 343.9,200.2 344.2,200.2 344.6,200.2 345.0,200.2 345.4,200.2 345.8,200.2 346.1,200.2 346.5,200.2 346.9,200.2 347.3,200.2 347.6,200.2 348.0,200.2" fill="none" stroke="var(--link)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="198.0" y="218.5" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Id with gate lag</text>
<text x="258.0" y="194.1" font-size="12" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">trap-free</text>
<path d="M428 302.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="306.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0</text>
<path d="M428 251.1 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="255.1" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0.5</text>
<path d="M428 200.2 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="204.2" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">1</text>
<text x="422.0" y="181.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">Id, relative</text>
<text x="428.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">0</text>
<text x="503.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">2</text>
<text x="578.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">4</text>
<text x="653.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">6</text>
<text x="728.0" y="317.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">8</text>
<text x="728.0" y="332.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">time, ms</text>
<polyline points="428.0,200.2 428.4,200.2 428.8,200.2 429.1,200.2 429.5,200.2 429.9,200.2 430.2,200.2 430.6,200.2 431.0,200.2 431.4,200.2 431.8,200.2 432.1,200.2 432.5,200.2 432.9,200.2 433.2,200.2 433.6,200.2 434.0,200.2 434.4,200.2 434.8,200.2 435.1,200.2 435.5,200.2 435.9,200.2 436.2,200.2 436.6,200.2 437.0,200.2 437.4,200.2 437.8,200.2 438.1,200.2 438.5,200.2 438.9,200.2 439.2,200.2 439.6,200.2 440.0,200.2 440.4,200.2 440.8,200.2 441.1,200.2 441.5,200.2 441.9,200.2 442.2,200.2 442.6,200.2 443.0,200.2 443.4,200.2 443.8,200.2 444.1,200.2 444.5,200.2 444.9,200.2 445.2,200.2 445.6,200.2 446.0,200.2 446.4,200.2 446.8,200.2 447.1,200.2 447.5,200.2 447.9,200.2 448.2,200.2 448.6,200.2 449.0,200.2 449.4,200.2 449.8,200.2 450.1,200.2 450.5,200.2 450.9,200.2 451.2,200.2 451.6,200.2 452.0,200.2 452.4,200.2 452.8,200.2 453.1,200.2 453.5,200.2 453.9,200.2 454.2,200.2 454.6,200.2 455.0,200.2 455.4,200.2 455.8,200.2 456.1,200.2 456.5,200.2 456.9,200.2 457.2,200.2 457.6,200.2 458.0,200.2 458.4,200.2 458.8,200.2 459.1,200.2 459.5,200.2 459.9,200.2 460.2,200.2 460.6,200.2 461.0,200.2 461.4,200.2 461.8,200.2 462.1,200.2 462.5,200.2 462.9,200.2 463.2,200.2 463.6,200.2 464.0,200.2 464.4,200.2 464.8,200.2 465.1,200.2 465.5,200.2 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.5,200.1 465.6,200.1 465.6,200.1 465.6,200.1 465.6,200.1 465.6,200.1 465.7,200.1 465.7,200.1 465.8,200.1 465.9,200.1 465.9,200.1 466.0,200.1 466.1,200.1 466.2,200.1 466.2,200.1 466.4,200.1 466.6,200.1 466.7,200.1 467.0,200.1 467.0,200.1 467.4,200.1 467.4,200.1 467.8,200.1 467.8,200.1 468.1,200.1 468.5,200.1 468.5,200.1 468.9,200.1 469.2,200.1 469.6,200.1 470.0,200.1 470.4,200.1 470.8,200.1 471.1,200.1 471.5,200.1 471.9,200.1 472.2,200.1 472.6,200.1 473.0,200.1 473.4,200.1 473.8,200.1 474.1,200.1 474.5,200.1 474.9,200.1 475.2,200.1 475.6,200.1 476.0,200.1 476.4,200.1 476.8,200.1 477.1,200.1 477.5,200.1 477.9,200.1 478.2,200.1 478.6,200.1 479.0,200.1 479.4,200.1 479.8,200.1 480.1,200.1 480.5,200.1 480.9,200.1 481.2,200.1 481.6,200.1 482.0,200.1 482.4,200.1 482.8,200.1 483.1,200.1 483.5,200.1 483.9,200.1 484.2,200.1 484.6,200.1 485.0,200.1 485.4,200.1 485.8,200.1 486.1,200.1 486.5,200.1 486.9,200.1 487.2,200.1 487.6,200.1 488.0,200.1 488.4,200.1 488.8,200.1 489.1,200.1 489.5,200.1 489.9,200.1 490.2,200.1 490.6,200.1 491.0,200.1 491.4,200.1 491.8,200.1 492.1,200.1 492.5,200.1 492.9,200.1 493.2,200.1 493.6,200.1 494.0,200.1 494.4,200.1 494.8,200.1 495.1,200.1 495.5,200.1 495.9,200.1 496.2,200.1 496.6,200.1 497.0,200.1 497.4,200.1 497.8,200.1 498.1,200.1 498.5,200.1 498.9,200.1 499.2,200.1 499.6,200.1 500.0,200.1 500.4,200.1 500.8,200.1 501.1,200.1 501.5,200.1 501.9,200.1 502.2,200.1 502.6,200.1 503.0,200.1 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.0,200.2 503.1,200.2 503.1,200.2 503.1,200.2 503.1,200.2 503.1,200.2 503.2,200.2 503.2,200.2 503.3,200.2 503.4,200.2 503.4,200.2 503.5,200.2 503.6,200.2 503.7,200.2 503.8,200.2 503.9,200.2 504.1,200.2 504.2,200.2 504.5,200.2 504.5,200.2 504.9,200.2 504.9,200.2 505.2,200.2 505.3,200.2 505.6,200.2 506.0,200.2 506.0,200.2 506.4,200.2 506.8,200.2 506.8,200.2 507.1,200.2 507.5,200.2 507.9,200.2 508.2,200.2 508.6,200.2 509.0,200.2 509.4,200.2 509.8,200.2 510.1,200.2 510.5,200.2 510.9,200.2 511.2,200.2 511.6,200.2 512.0,200.2 512.4,200.2 512.8,200.2 513.1,200.2 513.5,200.2 513.9,200.2 514.2,200.2 514.6,200.2 515.0,200.2 515.4,200.2 515.8,200.2 516.1,200.2 516.5,200.2 516.9,200.2 517.2,200.2 517.6,200.2 518.0,200.2 518.4,200.2 518.8,200.2 519.1,200.2 519.5,200.2 519.9,200.2 520.2,200.2 520.6,200.2 521.0,200.2 521.4,200.2 521.8,200.2 522.1,200.2 522.5,200.2 522.9,200.2 523.2,200.2 523.6,200.2 524.0,200.2 524.4,200.2 524.8,200.2 525.1,200.2 525.5,200.2 525.9,200.2 526.2,200.2 526.6,200.2 527.0,200.2 527.4,200.2 527.8,200.2 528.1,200.2 528.5,200.2 528.9,200.2 529.2,200.2 529.6,200.2 530.0,200.2 530.4,200.2 530.8,200.2 531.1,200.2 531.5,200.2 531.9,200.2 532.2,200.2 532.6,200.2 533.0,200.2 533.4,200.2 533.8,200.2 534.1,200.2 534.5,200.2 534.9,200.2 535.2,200.2 535.6,200.2 536.0,200.2 536.4,200.2 536.8,200.2 537.1,200.2 537.5,200.2 537.9,200.2 538.2,200.2 538.6,200.2 539.0,200.2 539.4,200.2 539.8,200.2 540.1,200.2 540.5,200.2 540.9,200.2 541.2,200.2 541.6,200.2 542.0,200.2 542.4,200.2 542.8,200.2 543.1,200.2 543.5,200.2 543.9,200.2 544.2,200.2 544.6,200.2 545.0,200.2 545.4,200.2 545.8,200.2 546.1,200.2 546.5,200.2 546.9,200.2 547.2,200.2 547.6,200.2 548.0,200.2 548.4,200.2 548.8,200.2 549.1,200.2 549.5,200.2 549.9,200.2 550.2,200.2 550.6,200.2 551.0,200.2 551.4,200.2 551.8,200.2 552.1,200.2 552.5,200.2 552.9,200.2 553.2,200.2 553.6,200.2 554.0,200.2 554.4,200.2 554.8,200.2 555.1,200.2 555.5,200.2 555.9,200.2 556.2,200.2 556.6,200.2 557.0,200.2 557.4,200.2 557.8,200.2 558.1,200.2 558.5,200.2 558.9,200.2 559.2,200.2 559.6,200.2 560.0,200.2 560.4,200.2 560.8,200.2 561.1,200.2 561.5,200.2 561.9,200.2 562.2,200.2 562.6,200.2 563.0,200.2 563.4,200.2 563.8,200.2 564.1,200.2 564.5,200.2 564.9,200.2 565.2,200.2 565.6,200.2 566.0,200.2 566.4,200.2 566.8,200.2 567.1,200.2 567.5,200.2 567.9,200.2 568.2,200.2 568.6,200.2 569.0,200.2 569.4,200.2 569.8,200.2 570.1,200.2 570.5,200.2 570.9,200.2 571.2,200.2 571.6,200.2 572.0,200.2 572.4,200.2 572.8,200.2 573.1,200.2 573.5,200.2 573.9,200.2 574.2,200.2 574.6,200.2 575.0,200.2 575.4,200.2 575.8,200.2 576.1,200.2 576.5,200.2 576.9,200.2 577.2,200.2 577.6,200.2 578.0,200.2 578.4,200.2 578.8,200.2 579.1,200.2 579.5,200.2 579.9,200.2 580.2,200.2 580.6,200.2 581.0,200.2 581.4,200.2 581.8,200.2 582.1,200.2 582.5,200.2 582.9,200.2 583.2,200.2 583.6,200.2 584.0,200.2 584.4,200.2 584.8,200.2 585.1,200.2 585.5,200.2 585.9,200.2 586.2,200.2 586.6,200.2 587.0,200.2 587.4,200.2 587.8,200.2 588.1,200.2 588.5,200.2 588.9,200.2 589.2,200.2 589.6,200.2 590.0,200.2 590.4,200.2 590.8,200.2 591.1,200.2 591.5,200.2 591.9,200.2 592.2,200.2 592.6,200.2 593.0,200.2 593.4,200.2 593.8,200.2 594.1,200.2 594.5,200.2 594.9,200.2 595.2,200.2 595.6,200.2 596.0,200.2 596.4,200.2 596.8,200.2 597.1,200.2 597.5,200.2 597.9,200.2 598.2,200.2 598.6,200.2 599.0,200.2 599.4,200.2 599.8,200.2 600.1,200.2 600.5,200.2 600.9,200.2 601.2,200.2 601.6,200.2 602.0,200.2 602.4,200.2 602.8,200.2 603.1,200.2 603.5,200.2 603.9,200.2 604.2,200.2 604.6,200.2 605.0,200.2 605.4,200.2 605.8,200.2 606.1,200.2 606.5,200.2 606.9,200.2 607.2,200.2 607.6,200.2 608.0,200.2 608.4,200.2 608.8,200.2 609.1,200.2 609.5,200.2 609.9,200.2 610.2,200.2 610.6,200.2 611.0,200.2 611.4,200.2 611.8,200.2 612.1,200.2 612.5,200.2 612.9,200.2 613.2,200.2 613.6,200.2 614.0,200.2 614.4,200.2 614.8,200.2 615.1,200.2 615.5,200.2 615.9,200.2 616.2,200.2 616.6,200.2 617.0,200.2 617.4,200.2 617.8,200.2 618.1,200.2 618.5,200.2 618.9,200.2 619.2,200.2 619.6,200.2 620.0,200.2 620.4,200.2 620.8,200.2 621.1,200.2 621.5,200.2 621.9,200.2 622.2,200.2 622.6,200.2 623.0,200.2 623.4,200.2 623.8,200.2 624.1,200.2 624.5,200.2 624.9,200.2 625.2,200.2 625.6,200.2 626.0,200.2 626.4,200.2 626.8,200.2 627.1,200.2 627.5,200.2 627.9,200.2 628.2,200.2 628.6,200.2 629.0,200.2 629.4,200.2 629.8,200.2 630.1,200.2 630.5,200.2 630.9,200.2 631.2,200.2 631.6,200.2 632.0,200.2 632.4,200.2 632.8,200.2 633.1,200.2 633.5,200.2 633.9,200.2 634.2,200.2 634.6,200.2 635.0,200.2 635.4,200.2 635.8,200.2 636.1,200.2 636.5,200.2 636.9,200.2 637.2,200.2 637.6,200.2 638.0,200.2 638.4,200.2 638.8,200.2 639.1,200.2 639.5,200.2 639.9,200.2 640.2,200.2 640.6,200.2 641.0,200.2 641.4,200.2 641.8,200.2 642.1,200.2 642.5,200.2 642.9,200.2 643.2,200.2 643.6,200.2 644.0,200.2 644.4,200.2 644.8,200.2 645.1,200.2 645.5,200.2 645.9,200.2 646.2,200.2 646.6,200.2 647.0,200.2 647.4,200.2 647.8,200.2 648.1,200.2 648.5,200.2 648.9,200.2 649.2,200.2 649.6,200.2 650.0,200.2 650.4,200.2 650.8,200.2 651.1,200.2 651.5,200.2 651.9,200.2 652.2,200.2 652.6,200.2 653.0,200.2 653.4,200.2 653.8,200.2 654.1,200.2 654.5,200.2 654.9,200.2 655.2,200.2 655.6,200.2 656.0,200.2 656.4,200.2 656.8,200.2 657.1,200.2 657.5,200.2 657.9,200.2 658.2,200.2 658.6,200.2 659.0,200.2 659.4,200.2 659.8,200.2 660.1,200.2 660.5,200.2 660.9,200.2 661.2,200.2 661.6,200.2 662.0,200.2 662.4,200.2 662.8,200.2 663.1,200.2 663.5,200.2 663.9,200.2 664.2,200.2 664.6,200.2 665.0,200.2 665.4,200.2 665.8,200.2 666.1,200.2 666.5,200.2 666.9,200.2 667.2,200.2 667.6,200.2 668.0,200.2 668.4,200.2 668.8,200.2 669.1,200.2 669.5,200.2 669.9,200.2 670.2,200.2 670.6,200.2 671.0,200.2 671.4,200.2 671.8,200.2 672.1,200.2 672.5,200.2 672.9,200.2 673.2,200.2 673.6,200.2 674.0,200.2 674.4,200.2 674.8,200.2 675.1,200.2 675.5,200.2 675.9,200.2 676.2,200.2 676.6,200.2 677.0,200.2 677.4,200.2 677.8,200.2 678.1,200.2 678.5,200.2 678.9,200.2 679.2,200.2 679.6,200.2 680.0,200.2 680.4,200.2 680.8,200.2 681.1,200.2 681.5,200.2 681.9,200.2 682.2,200.2 682.6,200.2 683.0,200.2 683.4,200.2 683.8,200.2 684.1,200.2 684.5,200.2 684.9,200.2 685.2,200.2 685.6,200.2 686.0,200.2 686.4,200.2 686.8,200.2 687.1,200.2 687.5,200.2 687.9,200.2 688.2,200.2 688.6,200.2 689.0,200.2 689.4,200.2 689.8,200.2 690.1,200.2 690.5,200.2 690.9,200.2 691.2,200.2 691.6,200.2 692.0,200.2 692.4,200.2 692.8,200.2 693.1,200.2 693.5,200.2 693.9,200.2 694.2,200.2 694.6,200.2 695.0,200.2 695.4,200.2 695.8,200.2 696.1,200.2 696.5,200.2 696.9,200.2 697.2,200.2 697.6,200.2 698.0,200.2 698.4,200.2 698.8,200.2 699.1,200.2 699.5,200.2 699.9,200.2 700.2,200.2 700.6,200.2 701.0,200.2 701.4,200.2 701.8,200.2 702.1,200.2 702.5,200.2 702.9,200.2 703.2,200.2 703.6,200.2 704.0,200.2 704.4,200.2 704.8,200.2 705.1,200.2 705.5,200.2 705.9,200.2 706.2,200.2 706.6,200.2 707.0,200.2 707.4,200.2 707.8,200.2 708.1,200.2 708.5,200.2 708.9,200.2 709.2,200.2 709.6,200.2 710.0,200.2 710.4,200.2 710.8,200.2 711.1,200.2 711.5,200.2 711.9,200.2 712.2,200.2 712.6,200.2 713.0,200.2 713.4,200.2 713.8,200.2 714.1,200.2 714.5,200.2 714.9,200.2 715.2,200.2 715.6,200.2 716.0,200.2 716.4,200.2 716.8,200.2 717.1,200.2 717.5,200.2 717.9,200.2 718.2,200.2 718.6,200.2 719.0,200.2 719.4,200.2 719.8,200.2 720.1,200.2 720.5,200.2 720.9,200.2 721.2,200.2 721.6,200.2 722.0,200.2 722.4,200.2 722.8,200.2 723.1,200.2 723.5,200.2 723.9,200.2 724.2,200.2 724.6,200.2 725.0,200.2 725.4,200.2 725.8,200.2 726.1,200.2 726.5,200.2 726.9,200.2 727.2,200.2 727.6,200.2 728.0,200.2" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round" stroke-dasharray="5 4"/>
<polyline points="428.0,200.2 428.4,200.2 428.8,200.2 429.1,200.2 429.5,200.2 429.9,200.2 430.2,200.2 430.6,200.2 431.0,200.2 431.4,200.2 431.8,200.2 432.1,200.2 432.5,200.2 432.9,200.2 433.2,200.2 433.6,200.2 434.0,200.2 434.4,200.2 434.8,200.2 435.1,200.2 435.5,200.2 435.9,200.2 436.2,200.2 436.6,200.2 437.0,200.2 437.4,200.2 437.8,200.2 438.1,200.2 438.5,200.2 438.9,200.2 439.2,200.2 439.6,200.2 440.0,200.2 440.4,200.2 440.8,200.2 441.1,200.2 441.5,200.2 441.9,200.2 442.2,200.2 442.6,200.2 443.0,200.2 443.4,200.2 443.8,200.2 444.1,200.2 444.5,200.2 444.9,200.2 445.2,200.2 445.6,200.2 446.0,200.2 446.4,200.2 446.8,200.2 447.1,200.2 447.5,200.2 447.9,200.2 448.2,200.2 448.6,200.2 449.0,200.2 449.4,200.2 449.8,200.2 450.1,200.2 450.5,200.2 450.9,200.2 451.2,200.2 451.6,200.2 452.0,200.2 452.4,200.2 452.8,200.2 453.1,200.2 453.5,200.2 453.9,200.2 454.2,200.2 454.6,200.2 455.0,200.2 455.4,200.2 455.8,200.2 456.1,200.2 456.5,200.2 456.9,200.2 457.2,200.2 457.6,200.2 458.0,200.2 458.4,200.2 458.8,200.2 459.1,200.2 459.5,200.2 459.9,200.2 460.2,200.2 460.6,200.2 461.0,200.2 461.4,200.2 461.8,200.2 462.1,200.2 462.5,200.2 462.9,200.2 463.2,200.2 463.6,200.2 464.0,200.2 464.4,200.2 464.8,200.2 465.1,200.2 465.5,200.2 465.5,198.3 465.5,198.8 465.5,198.8 465.5,198.9 465.5,198.9 465.5,198.9 465.5,198.9 465.5,199.0 465.5,199.0 465.5,199.0 465.5,199.0 465.5,199.0 465.5,199.0 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.1 465.5,199.2 465.5,199.2 465.5,199.2 465.5,199.2 465.5,199.2 465.5,199.2 465.5,199.2 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.3 465.5,199.4 465.5,199.4 465.5,199.4 465.5,199.4 465.5,199.4 465.5,199.4 465.6,199.4 465.6,199.4 465.6,199.5 465.6,199.5 465.6,199.5 465.7,199.5 465.7,199.5 465.8,199.5 465.9,199.5 465.9,199.5 466.0,199.6 466.1,199.6 466.2,199.6 466.2,199.6 466.4,199.6 466.6,199.6 466.7,199.6 467.0,199.6 467.0,199.6 467.4,199.6 467.4,199.6 467.8,199.7 467.8,199.7 468.1,199.7 468.5,199.7 468.5,199.7 468.9,199.7 469.2,199.7 469.6,199.7 470.0,199.7 470.4,199.7 470.8,199.7 471.1,199.7 471.5,199.7 471.9,199.7 472.2,199.7 472.6,199.7 473.0,199.8 473.4,199.8 473.8,199.8 474.1,199.8 474.5,199.8 474.9,199.8 475.2,199.8 475.6,199.8 476.0,199.8 476.4,199.8 476.8,199.8 477.1,199.8 477.5,199.8 477.9,199.8 478.2,199.8 478.6,199.8 479.0,199.8 479.4,199.8 479.8,199.8 480.1,199.8 480.5,199.8 480.9,199.8 481.2,199.8 481.6,199.8 482.0,199.8 482.4,199.8 482.8,199.8 483.1,199.9 483.5,199.9 483.9,199.9 484.2,199.9 484.6,199.9 485.0,199.9 485.4,199.9 485.8,199.9 486.1,199.9 486.5,199.9 486.9,199.9 487.2,199.9 487.6,199.9 488.0,199.9 488.4,199.9 488.8,199.9 489.1,199.9 489.5,199.9 489.9,199.9 490.2,199.9 490.6,199.9 491.0,199.9 491.4,199.9 491.8,199.9 492.1,199.9 492.5,199.9 492.9,199.9 493.2,199.9 493.6,199.9 494.0,199.9 494.4,199.9 494.8,199.9 495.1,199.9 495.5,199.9 495.9,199.9 496.2,199.9 496.6,199.9 497.0,199.9 497.4,199.9 497.8,199.9 498.1,199.9 498.5,199.9 498.9,199.9 499.2,199.9 499.6,199.9 500.0,199.9 500.4,200.0 500.8,200.0 501.1,200.0 501.5,200.0 501.9,200.0 502.2,200.0 502.6,200.0 503.0,200.0 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.0,235.5 503.1,235.5 503.1,235.5 503.1,235.5 503.1,235.4 503.1,235.4 503.2,235.4 503.2,235.3 503.3,235.2 503.4,235.2 503.4,235.2 503.5,235.1 503.6,234.9 503.7,234.8 503.8,234.8 503.9,234.6 504.1,234.4 504.2,234.3 504.5,234.0 504.5,234.0 504.9,233.6 504.9,233.6 505.2,233.2 505.3,233.2 505.6,232.9 506.0,232.5 506.0,232.5 506.4,232.2 506.8,231.8 506.8,231.8 507.1,231.4 507.5,231.1 507.9,230.8 508.2,230.4 508.6,230.1 509.0,229.7 509.4,229.4 509.8,229.1 510.1,228.8 510.5,228.4 510.9,228.1 511.2,227.8 511.6,227.5 512.0,227.2 512.4,226.9 512.8,226.6 513.1,226.3 513.5,226.0 513.9,225.7 514.2,225.4 514.6,225.1 515.0,224.9 515.4,224.6 515.8,224.3 516.1,224.0 516.5,223.8 516.9,223.5 517.2,223.2 517.6,223.0 518.0,222.7 518.4,222.5 518.8,222.2 519.1,222.0 519.5,221.7 519.9,221.5 520.2,221.3 520.6,221.0 521.0,220.8 521.4,220.6 521.8,220.3 522.1,220.1 522.5,219.9 522.9,219.7 523.2,219.4 523.6,219.2 524.0,219.0 524.4,218.8 524.8,218.6 525.1,218.4 525.5,218.2 525.9,218.0 526.2,217.8 526.6,217.6 527.0,217.4 527.4,217.2 527.8,217.0 528.1,216.8 528.5,216.7 528.9,216.5 529.2,216.3 529.6,216.1 530.0,215.9 530.4,215.8 530.8,215.6 531.1,215.4 531.5,215.3 531.9,215.1 532.2,214.9 532.6,214.8 533.0,214.6 533.4,214.5 533.8,214.3 534.1,214.1 534.5,214.0 534.9,213.8 535.2,213.7 535.6,213.5 536.0,213.4 536.4,213.2 536.8,213.1 537.1,213.0 537.5,212.8 537.9,212.7 538.2,212.6 538.6,212.4 539.0,212.3 539.4,212.2 539.8,212.0 540.1,211.9 540.5,211.8 540.9,211.6 541.2,211.5 541.6,211.4 542.0,211.3 542.4,211.2 542.8,211.0 543.1,210.9 543.5,210.8 543.9,210.7 544.2,210.6 544.6,210.5 545.0,210.3 545.4,210.2 545.8,210.1 546.1,210.0 546.5,209.9 546.9,209.8 547.2,209.7 547.6,209.6 548.0,209.5 548.4,209.4 548.8,209.3 549.1,209.2 549.5,209.1 549.9,209.0 550.2,208.9 550.6,208.8 551.0,208.7 551.4,208.6 551.8,208.6 552.1,208.5 552.5,208.4 552.9,208.3 553.2,208.2 553.6,208.1 554.0,208.0 554.4,208.0 554.8,207.9 555.1,207.8 555.5,207.7 555.9,207.6 556.2,207.6 556.6,207.5 557.0,207.4 557.4,207.3 557.8,207.2 558.1,207.2 558.5,207.1 558.9,207.0 559.2,207.0 559.6,206.9 560.0,206.8 560.4,206.7 560.8,206.7 561.1,206.6 561.5,206.5 561.9,206.5 562.2,206.4 562.6,206.3 563.0,206.3 563.4,206.2 563.8,206.1 564.1,206.1 564.5,206.0 564.9,206.0 565.2,205.9 565.6,205.8 566.0,205.8 566.4,205.7 566.8,205.7 567.1,205.6 567.5,205.6 567.9,205.5 568.2,205.4 568.6,205.4 569.0,205.3 569.4,205.3 569.8,205.2 570.1,205.2 570.5,205.1 570.9,205.1 571.2,205.0 571.6,205.0 572.0,204.9 572.4,204.9 572.8,204.8 573.1,204.8 573.5,204.7 573.9,204.7 574.2,204.6 574.6,204.6 575.0,204.5 575.4,204.5 575.8,204.5 576.1,204.4 576.5,204.4 576.9,204.3 577.2,204.3 577.6,204.2 578.0,204.2 578.4,204.2 578.8,204.1 579.1,204.1 579.5,204.0 579.9,204.0 580.2,204.0 580.6,203.9 581.0,203.9 581.4,203.8 581.8,203.8 582.1,203.8 582.5,203.7 582.9,203.7 583.2,203.7 583.6,203.6 584.0,203.6 584.4,203.5 584.8,203.5 585.1,203.5 585.5,203.4 585.9,203.4 586.2,203.4 586.6,203.3 587.0,203.3 587.4,203.3 587.8,203.2 588.1,203.2 588.5,203.2 588.9,203.2 589.2,203.1 589.6,203.1 590.0,203.1 590.4,203.0 590.8,203.0 591.1,203.0 591.5,203.0 591.9,202.9 592.2,202.9 592.6,202.9 593.0,202.8 593.4,202.8 593.8,202.8 594.1,202.8 594.5,202.7 594.9,202.7 595.2,202.7 595.6,202.7 596.0,202.6 596.4,202.6 596.8,202.6 597.1,202.6 597.5,202.5 597.9,202.5 598.2,202.5 598.6,202.5 599.0,202.4 599.4,202.4 599.8,202.4 600.1,202.4 600.5,202.3 600.9,202.3 601.2,202.3 601.6,202.3 602.0,202.3 602.4,202.2 602.8,202.2 603.1,202.2 603.5,202.2 603.9,202.2 604.2,202.1 604.6,202.1 605.0,202.1 605.4,202.1 605.8,202.1 606.1,202.0 606.5,202.0 606.9,202.0 607.2,202.0 607.6,202.0 608.0,202.0 608.4,201.9 608.8,201.9 609.1,201.9 609.5,201.9 609.9,201.9 610.2,201.8 610.6,201.8 611.0,201.8 611.4,201.8 611.8,201.8 612.1,201.8 612.5,201.7 612.9,201.7 613.2,201.7 613.6,201.7 614.0,201.7 614.4,201.7 614.8,201.7 615.1,201.6 615.5,201.6 615.9,201.6 616.2,201.6 616.6,201.6 617.0,201.6 617.4,201.6 617.8,201.5 618.1,201.5 618.5,201.5 618.9,201.5 619.2,201.5 619.6,201.5 620.0,201.5 620.4,201.4 620.8,201.4 621.1,201.4 621.5,201.4 621.9,201.4 622.2,201.4 622.6,201.4 623.0,201.4 623.4,201.4 623.8,201.3 624.1,201.3 624.5,201.3 624.9,201.3 625.2,201.3 625.6,201.3 626.0,201.3 626.4,201.3 626.8,201.2 627.1,201.2 627.5,201.2 627.9,201.2 628.2,201.2 628.6,201.2 629.0,201.2 629.4,201.2 629.8,201.2 630.1,201.2 630.5,201.1 630.9,201.1 631.2,201.1 631.6,201.1 632.0,201.1 632.4,201.1 632.8,201.1 633.1,201.1 633.5,201.1 633.9,201.1 634.2,201.1 634.6,201.0 635.0,201.0 635.4,201.0 635.8,201.0 636.1,201.0 636.5,201.0 636.9,201.0 637.2,201.0 637.6,201.0 638.0,201.0 638.4,201.0 638.8,201.0 639.1,200.9 639.5,200.9 639.9,200.9 640.2,200.9 640.6,200.9 641.0,200.9 641.4,200.9 641.8,200.9 642.1,200.9 642.5,200.9 642.9,200.9 643.2,200.9 643.6,200.9 644.0,200.9 644.4,200.8 644.8,200.8 645.1,200.8 645.5,200.8 645.9,200.8 646.2,200.8 646.6,200.8 647.0,200.8 647.4,200.8 647.8,200.8 648.1,200.8 648.5,200.8 648.9,200.8 649.2,200.8 649.6,200.8 650.0,200.8 650.4,200.7 650.8,200.7 651.1,200.7 651.5,200.7 651.9,200.7 652.2,200.7 652.6,200.7 653.0,200.7 653.4,200.7 653.8,200.7 654.1,200.7 654.5,200.7 654.9,200.7 655.2,200.7 655.6,200.7 656.0,200.7 656.4,200.7 656.8,200.7 657.1,200.7 657.5,200.7 657.9,200.6 658.2,200.6 658.6,200.6 659.0,200.6 659.4,200.6 659.8,200.6 660.1,200.6 660.5,200.6 660.9,200.6 661.2,200.6 661.6,200.6 662.0,200.6 662.4,200.6 662.8,200.6 663.1,200.6 663.5,200.6 663.9,200.6 664.2,200.6 664.6,200.6 665.0,200.6 665.4,200.6 665.8,200.6 666.1,200.6 666.5,200.6 666.9,200.5 667.2,200.5 667.6,200.5 668.0,200.5 668.4,200.5 668.8,200.5 669.1,200.5 669.5,200.5 669.9,200.5 670.2,200.5 670.6,200.5 671.0,200.5 671.4,200.5 671.8,200.5 672.1,200.5 672.5,200.5 672.9,200.5 673.2,200.5 673.6,200.5 674.0,200.5 674.4,200.5 674.8,200.5 675.1,200.5 675.5,200.5 675.9,200.5 676.2,200.5 676.6,200.5 677.0,200.5 677.4,200.5 677.8,200.5 678.1,200.5 678.5,200.4 678.9,200.4 679.2,200.4 679.6,200.4 680.0,200.4 680.4,200.4 680.8,200.4 681.1,200.4 681.5,200.4 681.9,200.4 682.2,200.4 682.6,200.4 683.0,200.4 683.4,200.4 683.8,200.4 684.1,200.4 684.5,200.4 684.9,200.4 685.2,200.4 685.6,200.4 686.0,200.4 686.4,200.4 686.8,200.4 687.1,200.4 687.5,200.4 687.9,200.4 688.2,200.4 688.6,200.4 689.0,200.4 689.4,200.4 689.8,200.4 690.1,200.4 690.5,200.4 690.9,200.4 691.2,200.4 691.6,200.4 692.0,200.4 692.4,200.4 692.8,200.4 693.1,200.4 693.5,200.4 693.9,200.4 694.2,200.4 694.6,200.4 695.0,200.4 695.4,200.4 695.8,200.4 696.1,200.3 696.5,200.3 696.9,200.3 697.2,200.3 697.6,200.3 698.0,200.3 698.4,200.3 698.8,200.3 699.1,200.3 699.5,200.3 699.9,200.3 700.2,200.3 700.6,200.3 701.0,200.3 701.4,200.3 701.8,200.3 702.1,200.3 702.5,200.3 702.9,200.3 703.2,200.3 703.6,200.3 704.0,200.3 704.4,200.3 704.8,200.3 705.1,200.3 705.5,200.3 705.9,200.3 706.2,200.3 706.6,200.3 707.0,200.3 707.4,200.3 707.8,200.3 708.1,200.3 708.5,200.3 708.9,200.3 709.2,200.3 709.6,200.3 710.0,200.3 710.4,200.3 710.8,200.3 711.1,200.3 711.5,200.3 711.9,200.3 712.2,200.3 712.6,200.3 713.0,200.3 713.4,200.3 713.8,200.3 714.1,200.3 714.5,200.3 714.9,200.3 715.2,200.3 715.6,200.3 716.0,200.3 716.4,200.3 716.8,200.3 717.1,200.3 717.5,200.3 717.9,200.3 718.2,200.3 718.6,200.3 719.0,200.3 719.4,200.3 719.8,200.3 720.1,200.3 720.5,200.3 720.9,200.3 721.2,200.3 721.6,200.3 722.0,200.3 722.4,200.3 722.8,200.3 723.1,200.3 723.5,200.3 723.9,200.3 724.2,200.3 724.6,200.3 725.0,200.3 725.4,200.3 725.8,200.3 726.1,200.3 726.5,200.3 726.9,200.3 727.2,200.3 727.6,200.3 728.0,200.3" fill="none" stroke="var(--accent)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="578.0" y="230.7" font-size="12" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans">Id with drain lag</text>
<text x="638.0" y="194.1" font-size="12" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">trap-free</text>
</svg>
<figcaption style="text-align:left"><b>Figure 3.</b> One excursion of each voltage, computed from the network equations at the default time constants. The state follows the excursion at once and takes milliseconds to come back, so the current stays low after the voltage has returned: that is the lag. The currents use the Shift coupling on an illustrative trap-free curve, with <code>ShiftGate</code> 0.1 and <code>ShiftDrain</code> 0.02.</figcaption>
</figure>

Under RF the same networks settle instead of recovering:

<figure class="figure">
<svg viewBox="0 0 760 192" role="img" aria-label="Two plots over two RF periods. Left: vgs swings between minus 6 and plus 1 volts and phi1 sits flat at -5.37 volts, just above the lowest vgs. Right: vds swings between 8 and 48 volts and phi2 sits flat at 47.23 volts, just below the peak." style="width:100%;height:auto">
<text x="48.0" y="16.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">vgs, ±3.5 V about −2.5 V</text>
<text x="428.0" y="16.0" font-size="13" fill="var(--text)" text-anchor="start" font-family="IBM Plex Sans" font-weight="600">vds, ±20 V about 28 V</text>
<path d="M48 139.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="143.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−6</text>
<path d="M48 117.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="121.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−4</text>
<path d="M48 95.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="99.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">−2</text>
<path d="M48 73.0 H348" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="42.0" y="77.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0</text>
<text x="42.0" y="31.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">V</text>
<text x="48.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">0</text>
<text x="198.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">1</text>
<text x="348.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">2</text>
<text x="348.0" y="180.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">RF periods</text>
<polyline points="48.0,100.5 49.2,98.5 50.5,96.5 51.8,94.5 53.0,92.5 54.2,90.5 55.5,88.6 56.8,86.7 58.0,84.8 59.2,83.0 60.5,81.2 61.8,79.5 63.0,77.9 64.2,76.3 65.5,74.7 66.8,73.3 68.0,71.9 69.2,70.6 70.5,69.4 71.8,68.2 73.0,67.2 74.2,66.2 75.5,65.3 76.8,64.6 78.0,63.9 79.2,63.3 80.5,62.8 81.8,62.5 83.0,62.2 84.2,62.1 85.5,62.0 86.8,62.1 88.0,62.2 89.2,62.5 90.5,62.8 91.8,63.3 93.0,63.9 94.2,64.6 95.5,65.3 96.8,66.2 98.0,67.2 99.2,68.2 100.5,69.4 101.8,70.6 103.0,71.9 104.2,73.3 105.5,74.7 106.8,76.3 108.0,77.9 109.2,79.5 110.5,81.2 111.8,83.0 113.0,84.8 114.2,86.7 115.5,88.6 116.8,90.5 118.0,92.5 119.2,94.5 120.5,96.5 121.8,98.5 123.0,100.5 124.2,102.5 125.5,104.5 126.8,106.5 128.0,108.5 129.2,110.5 130.5,112.4 131.8,114.3 133.0,116.2 134.2,118.0 135.5,119.8 136.8,121.5 138.0,123.1 139.2,124.7 140.5,126.3 141.8,127.7 143.0,129.1 144.2,130.4 145.5,131.6 146.8,132.8 148.0,133.8 149.2,134.8 150.5,135.7 151.8,136.4 153.0,137.1 154.2,137.7 155.5,138.2 156.8,138.5 158.0,138.8 159.2,138.9 160.5,139.0 161.8,138.9 163.0,138.8 164.2,138.5 165.5,138.2 166.8,137.7 168.0,137.1 169.2,136.4 170.5,135.7 171.8,134.8 173.0,133.8 174.2,132.8 175.5,131.6 176.8,130.4 178.0,129.1 179.2,127.7 180.5,126.3 181.8,124.7 183.0,123.1 184.2,121.5 185.5,119.8 186.8,118.0 188.0,116.2 189.2,114.3 190.5,112.4 191.8,110.5 193.0,108.5 194.2,106.5 195.5,104.5 196.8,102.5 198.0,100.5 199.2,98.5 200.5,96.5 201.8,94.5 203.0,92.5 204.2,90.5 205.5,88.6 206.8,86.7 208.0,84.8 209.2,83.0 210.5,81.3 211.7,79.5 213.0,77.9 214.2,76.3 215.5,74.7 216.8,73.3 218.0,71.9 219.2,70.6 220.5,69.4 221.7,68.2 223.0,67.2 224.2,66.2 225.5,65.3 226.8,64.6 228.0,63.9 229.2,63.3 230.5,62.8 231.8,62.5 233.0,62.2 234.2,62.1 235.5,62.0 236.8,62.1 238.0,62.2 239.2,62.5 240.5,62.8 241.8,63.3 243.0,63.9 244.2,64.6 245.5,65.3 246.8,66.2 248.0,67.2 249.2,68.2 250.5,69.4 251.8,70.6 253.0,71.9 254.2,73.3 255.5,74.7 256.8,76.3 258.0,77.9 259.2,79.5 260.5,81.3 261.8,83.0 263.0,84.8 264.2,86.7 265.5,88.6 266.8,90.5 268.0,92.5 269.2,94.5 270.5,96.5 271.8,98.5 273.0,100.5 274.2,102.5 275.5,104.5 276.8,106.5 278.0,108.5 279.2,110.5 280.5,112.4 281.8,114.3 283.0,116.2 284.2,118.0 285.5,119.7 286.8,121.5 288.0,123.1 289.2,124.7 290.5,126.3 291.8,127.7 293.0,129.1 294.2,130.4 295.5,131.6 296.8,132.8 298.0,133.8 299.2,134.8 300.5,135.7 301.8,136.4 303.0,137.1 304.2,137.7 305.5,138.2 306.8,138.5 308.0,138.8 309.2,138.9 310.5,139.0 311.8,138.9 313.0,138.8 314.2,138.5 315.5,138.2 316.8,137.7 318.0,137.1 319.2,136.4 320.5,135.7 321.8,134.8 323.0,133.8 324.2,132.8 325.5,131.6 326.8,130.4 328.0,129.1 329.2,127.7 330.5,126.3 331.8,124.7 333.0,123.1 334.2,121.5 335.5,119.8 336.8,118.0 338.0,116.2 339.2,114.3 340.5,112.4 341.8,110.5 343.0,108.5 344.2,106.5 345.5,104.5 346.8,102.5 348.0,100.5" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round"/>
<polyline points="48.0,132.1 348.0,132.1" fill="none" stroke="var(--link)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="235.5" y="147.1" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">φ1 = −5.37 V</text>
<text x="85.5" y="56.5" font-size="12" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vgs</text>
<path d="M428 150.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="154.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">0</text>
<path d="M428 110.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="114.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">20</text>
<path d="M428 70.0 H728" fill="none" stroke="var(--border)" stroke-width="1"/>
<text x="422.0" y="74.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">40</text>
<text x="422.0" y="31.0" font-size="11" fill="var(--muted)" text-anchor="start" font-family="IBM Plex Sans">V</text>
<text x="428.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">0</text>
<text x="578.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">1</text>
<text x="728.0" y="165.0" font-size="11" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">2</text>
<text x="728.0" y="180.0" font-size="11" fill="var(--muted)" text-anchor="end" font-family="IBM Plex Sans">RF periods</text>
<polyline points="428.0,94.0 429.2,91.9 430.5,89.8 431.8,87.7 433.0,85.7 434.2,83.6 435.5,81.6 436.8,79.7 438.0,77.7 439.2,75.8 440.5,74.0 441.8,72.2 443.0,70.5 444.2,68.8 445.5,67.2 446.8,65.7 448.0,64.3 449.2,62.9 450.5,61.6 451.8,60.5 453.0,59.4 454.2,58.4 455.5,57.5 456.8,56.7 458.0,56.0 459.2,55.4 460.5,54.9 461.8,54.5 463.0,54.2 464.2,54.1 465.5,54.0 466.8,54.1 468.0,54.2 469.2,54.5 470.5,54.9 471.8,55.4 473.0,56.0 474.2,56.7 475.5,57.5 476.8,58.4 478.0,59.4 479.2,60.5 480.5,61.6 481.8,62.9 483.0,64.3 484.2,65.7 485.5,67.2 486.8,68.8 488.0,70.5 489.2,72.2 490.5,74.0 491.8,75.8 493.0,77.7 494.2,79.7 495.5,81.6 496.8,83.6 498.0,85.7 499.2,87.7 500.5,89.8 501.8,91.9 503.0,94.0 504.2,96.1 505.5,98.2 506.8,100.3 508.0,102.3 509.2,104.4 510.5,106.4 511.8,108.3 513.0,110.3 514.2,112.2 515.5,114.0 516.8,115.8 518.0,117.5 519.2,119.2 520.5,120.8 521.8,122.3 523.0,123.7 524.2,125.1 525.5,126.4 526.8,127.5 528.0,128.6 529.2,129.6 530.5,130.5 531.8,131.3 533.0,132.0 534.2,132.6 535.5,133.1 536.8,133.5 538.0,133.8 539.2,133.9 540.5,134.0 541.8,133.9 543.0,133.8 544.2,133.5 545.5,133.1 546.8,132.6 548.0,132.0 549.2,131.3 550.5,130.5 551.8,129.6 553.0,128.6 554.2,127.5 555.5,126.4 556.8,125.1 558.0,123.7 559.2,122.3 560.5,120.8 561.8,119.2 563.0,117.5 564.2,115.8 565.5,114.0 566.8,112.2 568.0,110.3 569.2,108.3 570.5,106.4 571.8,104.4 573.0,102.3 574.2,100.3 575.5,98.2 576.8,96.1 578.0,94.0 579.2,91.9 580.5,89.8 581.8,87.7 583.0,85.7 584.2,83.6 585.5,81.6 586.8,79.7 588.0,77.7 589.2,75.8 590.5,74.0 591.8,72.2 593.0,70.5 594.2,68.8 595.5,67.2 596.8,65.7 598.0,64.3 599.2,62.9 600.5,61.6 601.8,60.5 603.0,59.4 604.2,58.4 605.5,57.5 606.8,56.7 608.0,56.0 609.2,55.4 610.5,54.9 611.8,54.5 613.0,54.2 614.2,54.1 615.5,54.0 616.8,54.1 618.0,54.2 619.2,54.5 620.5,54.9 621.8,55.4 623.0,56.0 624.2,56.7 625.5,57.5 626.8,58.4 628.0,59.4 629.2,60.5 630.5,61.6 631.8,62.9 633.0,64.3 634.2,65.7 635.5,67.2 636.8,68.8 638.0,70.5 639.2,72.2 640.5,74.0 641.8,75.8 643.0,77.7 644.2,79.7 645.5,81.6 646.8,83.6 648.0,85.7 649.2,87.7 650.5,89.8 651.8,91.9 653.0,94.0 654.2,96.1 655.5,98.2 656.8,100.3 658.0,102.3 659.2,104.4 660.5,106.4 661.8,108.3 663.0,110.3 664.2,112.2 665.5,114.0 666.8,115.8 668.0,117.5 669.2,119.2 670.5,120.8 671.8,122.3 673.0,123.7 674.2,125.1 675.5,126.4 676.8,127.5 678.0,128.6 679.2,129.6 680.5,130.5 681.8,131.3 683.0,132.0 684.2,132.6 685.5,133.1 686.8,133.5 688.0,133.8 689.2,133.9 690.5,134.0 691.8,133.9 693.0,133.8 694.2,133.5 695.5,133.1 696.8,132.6 698.0,132.0 699.2,131.3 700.5,130.5 701.8,129.6 703.0,128.6 704.2,127.5 705.5,126.4 706.8,125.1 708.0,123.7 709.2,122.3 710.5,120.8 711.8,119.2 713.0,117.5 714.2,115.8 715.5,114.0 716.8,112.2 718.0,110.3 719.2,108.3 720.5,106.4 721.8,104.4 723.0,102.3 724.2,100.3 725.5,98.2 726.8,96.1 728.0,94.0" fill="none" stroke="var(--muted)" stroke-width="1.6" stroke-linejoin="round"/>
<polyline points="428.0,55.5 728.0,55.5" fill="none" stroke="var(--accent)" stroke-width="2.4" stroke-linejoin="round"/>
<text x="578.0" y="49.5" font-size="12" fill="var(--text)" text-anchor="middle" font-family="IBM Plex Sans">φ2 = 47.23 V</text>
<text x="540.5" y="147.0" font-size="12" fill="var(--muted)" text-anchor="middle" font-family="IBM Plex Sans">vds</text>
</svg>
<figcaption style="text-align:left"><b>Figure 4.</b> Under a steady RF drive each state settles at the level where a cycle's capture balances a cycle's emission, computed here at the default time constants. It sits a fraction of a volt inside the voltage's extreme, 0.63 V above the lowest <code>vgs</code> and 0.77 V below the highest <code>vds</code>, and hardly moves within a cycle. The drive and the load set the swing, so they set the state.</figcaption>
</figure>

Gate and drain swing in antiphase, so `vgs − φ1` and `φ2 − vds` are both at their largest when the channel is on and `vds`
is at its lowest. The trapped charge lowers the current exactly there, which appears as knee walkout and as output power
below what the DC I–V predicts. The level of the state follows from a cycle's balance ([P1] eq. 6.75), so at any frequency
well above `1/τ_emit` it depends on the time constants only through their ratio. A two-tone spacing near `1/τ_emit` does
depend on the time constants themselves.

### How pulsed data reaches the states {#trap-pulses}

A pulse measured before emission has moved the state holds that state at the trap-setting bias, while the terminals sit at
the pulse's voltages. Under **Trained** that gives the fit points away from the DC diagonal, at the same terminal voltages
but different states:

| Measurement | `vgs` | `vds` | `φ1` | `φ2` |
|---|---|---|---|---|
| DC | −2 V | 10 V | −2 V | 10 V |
| Pulse from a quiescent point of −4 V, 10 V | −2 V | 10 V | −4 V | 10 V |
| Pulse from a quiescent point of −4 V, 28 V | −2 V | 10 V | −4 V | 28 V |

The three currents differ, and the differences are what `Id` learns: the first two rows separate gate lag, and the last
two separate drain lag. To separate the two states, the quiescent points must move each voltage on its own. A double pulse
sets the state with a pre-pulse and is the clearest way to do this ([S14], [G19]). A pulse that goes beyond its setting was
partly captured during the pulse and is handled by the set's `TrapState` ([X10] eqs. 1.8–1.9).

### What each analysis sees {#trap-analyses}

| Analysis | The states | What results |
|---|---|---|
| DC | Settled: `φ1 = vgs`, `φ2 = vds` | The DC I–V. Under Shift, exactly the trap-free current |
| S-parameters | At the bias point's DC values, with a pole near `1/(2π·τ_emit)`, about 160 Hz at the defaults. Above it they are frozen | The RF `Gm` and `Gds`, which differ from the DC slopes |
| Harmonic balance | Solved as nodes like any other, in periodic steady state | Each state at its cycle balance, moving with drive and load: compression and knee walkout |
| Envelope | Stepped over time as memory | Current droop and recovery across a pulse. The Pulsed Radar example shows it |

`GateLag=false` or `DrainLag=false` on an instance removes that node, and the state then takes its DC value at every
instant: [Switching parts of the model off](#switches).

### Further reading {#trap-reading}

| Topic | Where |
|---|---|
| What traps are, and current collapse | [V01], [B02] |
| The state equations | [P1] eqs. 6.73–6.74, [X10] eqs. 1.3–1.4 |
| The current as a function of the states, with self-heating | [P1] §6.6 and eq. 6.70, [X10] eqs. 1.1–1.5 |
| The state under a CW drive | [P1] eq. 6.75 |
| The trapped charge as a back gate | [P1] §6.6.4, [P21] |
| Capture over many time scales | [G19] |
| Thermally activated emission | [N22] eq. 3 |
| Mapping pulsed points to states | [X10] eqs. 1.8–1.9 |
| Double-pulse characterization | [S14], [G19] |
| A large-signal model with gate and drain lag, checked at high VSWR | [J07] |

The full citations are under [References](#references).

## Traps, and what pulsed data allows {#trap-data}

Traps are a setting. **None** is the default. **CaptureEmission** has a gate-lag state and a drain-lag state, each
switched on separately. A state is captured quickly when its voltage moves one way and emits slowly when it moves back.
The defaults are 10 ns capture at a 1 V gap and 1 ms emission, and emission can be made thermally activated.
**Custom** lets you write each state's dynamics and DC value yourself. [How traps work in the model](#trap-model) draws the
networks and shows what they do.

The **coupling** says how the states reach the drain current:

| Coupling | The current | Needs |
|---|---|---|
| **Trained** (the default with pulsed I–V) | `Id` takes the enabled states as inputs and is fitted again with them | Pulsed I–V |
| **Shift** | `Id` is the trap-free current fitted to the DC I–V, evaluated at a shifted gate voltage: `Id(vgs − ΔV, vds)`, with `ΔV = ShiftDrain·(φ2 − vds) + ShiftGate·(vgs − φ1)` | Nothing beyond DC I–V; multi-bias S to fit the strengths |

Under **Shift** a drain-lag state held above `vds`, or a gate-lag state held below `vgs`, lowers the current. At any DC
bias every state equals its DC value, so the shift is zero and the model's DC is exactly the trap-free current. At RF
the states are frozen, which gives `gm_RF = (1 − ShiftGate)·gm` and `gds_RF = gds + ShiftDrain·gm`, where `gm` and
`gds` are the trap-free current's. The step fits each strength from these relations over the active S biases. A
strength can be typed and locked, and is then not fitted. The time constants are typed in both couplings. Shift is
defined for CaptureEmission; the charges take no trap state under it. With self-heating modelled, `gm` and `gds` are
read at each point's junction temperature, so heating is not counted as traps. Without self-heating, `ShiftDrain`
includes any thermal part of the DC/RF difference.

A trained trap state is fitted from the **trap-setting levels** in the pulsed data. The level of a set is its pre-pulse
bias for a double pulse, and its quiescent bias otherwise.

| Pulsed data | What it allows |
|---|---|
| Double pulse | The pre-pulse sets the state, and the measuring pulse reads it before emission moves it ([S14], [G19]). This is the clearest data. |
| Ordinary pulses from two or more quiescent points | Two or more levels: the state is fitted fully. |
| One quiescent point | One level: the state is fitted with one warning, and the model records the level it came from. |
| No pulsed data | Threshold-shift traps, time constants typed, strengths typed or fitted from multi-bias S. |

A pulsed point **toward** its setting (pulsed no higher in `Vds` for drain lag, no lower in `Vgs` for gate lag) sits at
the setting's state exactly, because only slow emission acted. A point **beyond** it was partly captured during the
pulse. The set's `TrapState` decides what happens to it: `auto` keeps it for validation only, `setting` takes the
setting's state, and `extreme` takes the pulse's own voltage as the state.

DC/RF dispersion is the simpler alternative to traps. It fits a second drain current, `IdRF`, to the RF transconductance
and output conductance, and the model follows `Id` at DC and `IdRF` above the corner set by τ. Dispersion and traps are
not combined, in either coupling.

## Periphery scaling {#scaling}

A model records the measured device's finger count `N0` and unit gate width `W0`. Export needs both. An instance's `N`
scales the model:

| What | Scales by |
|---|---|
| The core (`Id`, `Ig`, `Qg`, `Qd`) | `N/N0` |
| A network value, by its rule | `fixed`, `perFinger` (`N/N0`) or `perFingerInverse` (`N0/N`) |
| `Rth`, `Cth` | `N0/N`, `N/N0` |
| Trap time constants | Not scaled |

For the GaN two-shell network the series resistances are `perFingerInverse`, the inner capacitances `perFinger`, and the
pad capacitances, inductances and mutual `fixed`. Every rule can be changed per value. `m` places whole devices in
parallel, so `m=2 N=8` is two 8-finger devices.

## Passive models {#passive-models}

A passive document holds one N-port block, or a **family** of blocks over a geometry value such as the turns of a
spiral. **Analyse** reports the block's reciprocity, passivity, symmetry, low-frequency asymptotes and self-resonances, and fits a rational model. **Propose** ranks a library of
twelve topologies (series and parallel RLC, an inductor with skin effect, capacitor and resistor parasitics, π, T, bond
wire, via, spiral, MIM and a line with pads) against the data, or fits a topology you draw. **Fit** gives each value
with its error.

A family is fitted as one law per value, a polynomial or a rational function of the geometry. The middle geometry is
held out and predicted, and the result reports its error. Export writes a cell. A single fit's cell takes the fitted
values as its parameters, each in the unit that keeps it between 1 and 1000 (nH, fF, Ω). A family cell takes the
geometry as its only parameter: the element values stay variables on the cell's schematic, each a law of the geometry —
`x` is the geometry normalised to the fitted range and every law a short polynomial or ratio in it — and the cell warns
once when it is placed outside the fitted range.

## The example {#example}

**File ▸ New ▸ Examples ▸ Model Composer** opens a workspace with three documents built this way:

- `synthetic-gan`: a GaN FET built from synthetic DC, pulsed and multi-bias S data, with a fitted network, ANN
  currents, adjoint-ANN charges, self-heating and drain lag. Its 8-finger model drives a class-AB power sweep and a
  loadpull.
- `passive-spiral`: a family of spiral inductors over turns, fitted and placed in a filter at a turn count between two
  measured ones.
- `simulated-tables`: a model built from a simulator's tables, showing the intrinsic-voltage cross-check.

The `synthetic-gan` model is also there as Verilog-A source, `gan_4x125.va`, and the `GanVerilogA` bench runs the two
side by side: [Verilog-A export](#veriloga).

Its README lists every setting it uses, and the value a real job would use where it differs.

## Opening a document {#open}

**Tools ▸ Model Composer…** opens a window on a new document and asks one question: **Active** (a three-terminal FET)
or **Passive** (an N-port). Nothing is written until the first save, which asks where the `.cmc` goes; choosing a
network saves first, because the document's data and network files are found relative to it. Importing data into a new
document asks for the data first, then opens the Save dialog in the data's folder with the first file's name filled in,
since the data is kept beside the `.cmc`. Cancelling that Save imports nothing.

A `.cmc` in a workspace opens on double-click in the project tree, and the window's **Open** button opens one from
anywhere. Each document has one window; opening a document that is already open brings its window forward.

Closing a window with unsaved work asks whether to save it. A document is reopened where it was left: the same panels,
the same step, and the same bias cursor.

An opened document needs nothing run to be looked at: its data opens and every step that has a result draws it. The
step rail marks each finished step ✓, and the status strip says what is there, e.g. `8 data sets open · done: Pads,
Network, Current, Charge, Thermal, Traps, Export.` **Run** re-runs the selected step; on the Data step, with data already
imported, it reloads that data, and **Import…** is on the ▾ and beside the data-set list.

## The window {#window}

{{ui: model-composer-window}}

| Part | What it holds |
|---|---|
| Toolbar | Open, Save, Save As · Undo, Redo · the four panel lamps · Check Charge Conservation, Model Validation Report · Export to Library… · Run ▾, Cancel · Help |
| **Steps** (left) | The step rail, and under it the selected step's settings |
| **Plots** (centre) | A tab strip; under it the bias cursor (Vgs, Vds, T, Q) or the model sweep, whichever the plot reads; and a Data Display plot |
| **Network** (right, top) | The extrinsic network, drawn by the schematic canvas |
| **Values** (right, bottom) | The network's values: value, lock, min, max, tie, scaling rule, ± error. Widening the panel widens the name column only; narrower than its columns, the table scrolls sideways |
| Status strip | One line — a result, a warning or a refusal — and, while something runs, its stage, a progress bar and Cancel |

**Panel lamps.** Each panel has a lamp on the toolbar; a lit lamp means the panel is on screen. Hiding a panel gives its
space to the others. The last lit lamp cannot be turned off. Which panels are hidden is saved in the document. The
splitters between panels drag in both directions.

**The step rail.** Active documents have Data · Pads · Network · Current · Charge · Thermal · Traps · Check · Validate ·
Export; passive ones have Data · Topology · Fit · Export. A step that cannot run yet is dimmed and its tooltip says what
it needs. A ✓ marks a step whose result the document holds; a dot instead marks a result computed before the data, the
network or the trap model last changed. Check and Validate keep no result in the document, so they are marked once run.

**The bias cursor.** Vgs, Vds, the ambient temperature and the quiescent point. Each list holds only measured values, so
the cursor always sits on a measured bias; with a list focused, the arrow keys step it. The lists sit under the plot
tabs, beside the plot they set. Every frequency plot is drawn
at the measured point nearest the cursor. A list shows only on the plot tabs that read it: Vgs and Vds on the frequency
plots, the bias plane and the Gm, Gds and pulsed slices; T and Q where a step's plot picks a data set by them, which is
when the data-set list's selection is not one of that step's sets.

**The model sweep.** On the tabs that evaluate a fitted model rather than show data at a measured bias — the Current
step's Model tabs and the Charge step's C–V tabs — the row under the plot tabs holds start, stop and step for `vgs`
and for `vds` instead, in volts. A value takes effect on Return or on leaving its box; Escape puts back the one it had. Both are intrinsic, the plane the model is fitted on. One voltage is the x axis and the other steps the
curves; the x-axis voltage is drawn at its step or at 101 points, whichever is finer. The sweep starts from the step's
data span and keeps what you type for the rest of the session; it is not saved in the document.

## Running, progress and Cancel {#tasks}

**Run** runs the selected step's main task, which its tooltip names; the ▾ beside it lists the step's others. Every run
happens off the user interface, which stays responsive: the status strip shows the stage — which network is training,
`Training Ig, start 2 of 3` — and one progress bar, and **Cancel** stops it.

- One task runs at a time. Starting another while one runs is refused in the status strip.
- A cancelled task changes nothing: the previous result stays exactly as it was.
- A run that changes results (an import, a fit) saves the document first and reads the result back; an edit made
  while it ran is kept on top of the result.

## The Data step {#data}

**Import** (the database button, or Run) opens the importer:

| Field | Meaning |
|---|---|
| Kind | DC I–V, pulsed I–V, S-parameters, pulsed S-parameters, simulated tables, large signal |
| Source | Files, or a folder — a Touchstone set is a folder of files |
| Pattern | Touchstone: how each file name (or its comments) states its bias, e.g. `Vgs{vgs}_Vds{vds}_Id{id:mA}` |
| Map | CSV, `.mat`, `.npz`, `.ds`, `.npy`: what each column or array holds, `name=quantity[:unit]`; a leading `-` reads an array negated (`-DC1.VD.i=id`). For an array file it is filled in from the array names |
| Conditions | Ambient °C, the reference plane, the quiescent point, and any other condition as `Key=Value` |

The right side previews the first lines of the first file — for an array file, each array it holds with its shape —
and the first points as they will be read; the line at the bottom is what the import will read, how it read any array
not in the documented layout, or why it cannot.

The **data-set list** shows each set's name, kind, ambient, reference plane, quiescent point (and pre-pulse) and point
count, and where its charges read their capacitances: `Y`, its capacitance `table` or its `circuit set`.
`circuitrf model explain` says the same. A capacitance table spelled as the matrix whose `C12` is positive at most of its
points is warned about, since the matrix convention has `C12 = −Cgd`: the circuit set was probably meant. Selecting a set
points the plots and the cursor's lists at it.

Under the list, an active document shows **what its data supports**, one row per kind of data in
[the table of what a model needs](model-data.html#needs): I–V, S or capacitances, cold-FET S, two or more temperatures, pulsed data and
large-signal runs. A lit row says what that data gives the model; a dim one says what would light it, in the words
the step rail uses for a step that cannot open yet, so the two never disagree.

| Plot tab | Shows |
|---|---|
| Output | Id against Vds, one curve per Vgs; excluded points as × |
| Transfer | Id against Vgs, one curve per Vds |
| Bias plane | Every bias point: extrinsic ○, intrinsic ●, excluded ×, hygiene flags ▲, the cursor a green dot |
| S | The four S-parameters in dB at the cursor's bias |
| Smith | S11 and S22 at the cursor's bias |

**Moving the cursor.** On the bias plane, with the lasso lamp off, click a point: it turns green, the Vgs and Vds lists
move to its bias, and the other tabs follow. A plot you have zoomed or panned keeps its window while the cursor moves;
choosing another tab frames that tab's data. A point of another data set selects that set.

**Excluding points.** On the bias plane, light the lasso lamp under the plot tabs, choose a reason, then click a point
or drag a lasso around several. Points already excluded are included again. Choose **Valid** instead to make every
point the click or lasso takes valid again, excluded or not. Each click or lasso is one undo step.

{{ui: model-composer-bias-plane-lasso}}

To take a group of points out of the fit:

1. On the Data step, choose the **Bias plane** tab (the grid of dots).
2. Light the lasso lamp, the third button under the tabs. The reason list beside it is enabled.
3. Choose why the points are going: Outlier, Oscillation, Compliance, Drift, Breakdown or Other. The reason is kept with
   each point in the `.cmc`.
4. Drag a loop round the points. Releasing the button closes the loop and excludes every point inside it; each is
   drawn with a ×. A single click excludes the nearest point.
5. Turn the lasso lamp off again to go back to moving the cursor with a click.

Every fit after that leaves the excluded points out. To put one back, lasso or click it again, or choose **Valid** and
lasso a whole region. **Undo** takes back one lasso at a time.

**Base periphery.** N0 (fingers) and W0 (unit gate width, with its unit, e.g. `125 um`) of the measured device. Export
needs them; until they are set the Data step shows the one warning that says so.

## The Pads step {#pads}

The pad mode — None, OpenShort, TwoPorts or Network — and its files. The S and Smith tabs show S at the cursor's bias
before (dashed) and after (solid) the pads come off.

## The Network step {#network}

**The network.** The book button picks a stock network (GaN two-shell, classic single shell, series only) or none; the
folder button opens a six-pin `.csch`; the pencil opens the network in the schematic editor. **Copy** puts the network
on the clipboard as a schematic selection that pastes into any schematic; **Paste** takes a selection from a schematic
as the network. A network whose pins are not numbered 1 to 6, each once, is refused in one line, and nothing is written.

{{ui: model-composer-values}}

**The values table.** Every value is editable: type a value with or without its unit (`39.5 pH`, `39.5p`), tick
the lock to hold it, set a minimum and a maximum, tie it to another value (`rd` or `rd × 1.2`), and choose its scaling
rule. After a fit, ± is each value's standard error, and ⚠ marks a poorly determined value — its tooltip names the
correlated partner or the bound it sits at.

**The fit.** Method (ColdFet, MultiBias, ColdFetThenMultiBias, or Fixed, which holds every value), seed and the three
weights. The multi-bias fit's bias points are marked ■ on the bias plane; with the lasso lamp lit on the Network step a
click or lasso adds points to that set or takes them out.

| Plot tab | Shows |
|---|---|
| C | Intrinsic Cgs, Cgd and Cds against frequency at the cursor's bias, in fF |
| G | Intrinsic Gm and Gds against frequency, in mS |
| Flatness | The flatness of Cgs, Cgd and Cds across bias, one curve per Vds, in % |
| Bias plane | As on the Data step, with the fit's points |

A right network leaves C and G flat in frequency.

## The method picker {#method}

The Current and Charge steps pick a method for each constituent with the same control:

| Method | Settings |
|---|---|
| ANN | Hidden layers (1 to 4, neurons each, default `8, 8`), activation, iterations. A charge needs S at 4 biases or more |
| Table | Grid step and smoothing; a charge table also takes its line-integral path — A, B or their mean. A charge needs S at 4 biases or more |
| Equation | A template, or **Custom** for your own expression in `vgs`, `vds` and named parameters; after a fit, each parameter's value, which can be selected and copied. A charge needs at least as many capacitances as free parameters, two per bias, so `Linear` fits from one |

Choosing Equation starts on the first template. A template shows the law it fits; the expression boxes are Custom's.
**Linear in T** names the current template's parameters made linear in temperature, `p + p_T·(T − Tnom)`. Left blank it
is the template's current scale (Ipk for Angelov, Beta for Curtice and Statz, Idss for Materka) when the data spans
temperatures, so that hot DC points and cooler pulsed points are not fitted by one law; `none` turns it off.

With fewer S biases than a method needs, its entry in the picker gives the reason when you hover over it, and a fit
with it is refused rather than run with another method.

**A table on gridded data is the data.** When a constituent's training points form a complete grid in its inputs,
every combination present once, as intrinsic-plane simulated tables do, the table is a spline through the samples
themselves on that grid: no re-grid, and the Re-grid setting is not read. Its held-out points are whole grid lines
along `vgs`, so the rest stay a grid. A charge table does the same with capacitances on a grid. A grid with a missing
site, an excluded point for one, is re-gridded, and the report names the sites missing.

Otherwise a table samples a smooth function of the points onto its grid: an ANN trained on them (the default) or a
radial-basis interpolant through them (**RBF**). The RBF solves one dense system over every point, so its memory grows
with the square of the point count and its time with the cube: about 11 s at 4,000 points and 22 s at 5,000. Above
5,000 points a fit with it is refused before it starts; re-grid through the ANN.

Restarts, the seed and the held-out percentage belong to the step. After a fit the status strip reads, for example,
`Id: ANN 2×8 · held-out 0.8 % · domain 412 pts`. An ANN `Id` whose held-out error passes 2 % of its range over more
than 2,000 training points adds the next size up: `2×8 under-fits 24,000 points: try 2×16, or a Table.`

## The Current step {#current}

{{ui: model-composer-steps}}

Pickers for `Id` and `Ig` — an equation `Ig` is two expressions, `IgGs` and `IgGd` — and **DC/RF dispersion**, the
simpler alternative to self-heating and traps. **Id floor** and **Ig floor**, under the pickers, are the document's
`IdFloor` and `IgFloor`, each a fraction of the largest current: `Id` is weighted by `1/max(|Id|, floor)²`, so a lower
floor fits subthreshold current more closely, and `Ig` is fitted as `asinh(Ig/I0)` with `I0` the gate floor.

| Plot tab | Shows |
|---|---|
| Output, Transfer | Measured Id (symbols) and the model (lines), one member per measured Vgs or Vds, at the cursor's temperature and quiescent point |
| Model (vds), Model (vgs) | The fitted Id over the model sweep — along vds with a curve per vgs, or the reverse — at the cursor's temperature, every trap state at its DC value. Beyond the training domain the model's continuation is dashed and left out of the autoscale |
| Error | Model minus data over the bias plane, in % of the current's range |
| Gm, Gds | The data's own derivative (symbols) against the model, at the cursor's Vds or Vgs; with dispersion, RF Gm and Gds from the S data as well |
| Domain | The training points and the outline of the domain beyond which the model extrapolates |

## The Charge step {#charge}

Pickers for `Qg` and `Qd` (an adjoint-trained ANN by default, a table integrated along a path, or an equation), and
**Drift charge in Qg**. The charge templates are `Depletion`, `Angelov` and `Linear`.

`Linear` is `Qg = C11·vgs + C12·vds` and `Qd = C21·vgs + C22·vds`: constant capacitances, exact at a single measured
bias. From two or three biases it is the least-squares constant matrix, and the report gives each value's spread across
the biases, with the circuit set `Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12` and `Cm = C21 − C12`. Its C–V
plots are flat lines through the measured points. The exported model's provenance and `circuitrf model explain` say
`Qg, Qd: Linear from 1 bias point(s)`. A linear charge has no training domain, so no extrapolation applies to it.

**With no S, Y or capacitance data**, the step shows four capacitances in place of the pickers — `Cgs`, `Cgd`, `Cds` and `Cm`, each in
farads and 0 unless you type a value — and Run writes them as `Linear` charges. A datasheet gives them as
`Cgd = Crss`, `Cgs = Ciss − Crss` and `Cds = Coss − Crss`; `Cm` is a transcapacitance and may be negative. Export,
the provenance and `circuitrf model explain` say `Qg, Qd: typed` with the four values. With all four at zero the step,
the export and `model explain` say `the charges are zero: S-parameters and harmonic balance see the currents alone.`
— a model meant only for DC. Validation then has DC and large-signal items and no S items. Typed values beside S data
are refused, since typing would discard the data; **Fit to the S data** clears them.

| Plot tab | Shows |
|---|---|
| C–V (vgs), C–V (vds) | The model's capacitances along one voltage over the model sweep's span, a curve per value of the other voltage that the sweep steps, measured points near each as symbols. The selector beside the tabs picks one `Cij` or the circuit set Cgs, Cgd, Cds, Cm |
| Qg, Qd | The model's charge over (vds, vgs) |
| ΔQg, ΔQd | The model's charge against the line integral of the measured capacitances, in % of the charge's range |

## The Thermal step {#thermal}

Self-heating, `Rth` (blank extracts it), `Cth` or a ladder of `fraction:τ` pairs; **Extract** (Run) runs the extraction
and the line under the settings gives its Rth and residual. A document whose data is at one temperature shows its `T0`
and the words *one temperature* instead: the model it builds has no temperature dependence.

| Plot tab | Shows |
|---|---|
| Tj | Each DC point's junction-temperature rise against its dissipated power, and the model's Rth line |
| DC I–V | Measured Id, the model with self-heating (solid) and the isothermal model (dashed) |

## The Noise step {#noise}

The step is optional: a model needs no noise data to be built, exported or simulated, and one exported without it has no
`Noise` block, its core named as noiseless by the noise analysis.

**Data.** One file per line in the box. A Touchstone two-port with a noise block takes its bias after the path,
`file.s2p Vgs Vds`. Anything else is a table that states its own biases, one row per line:
`Vgs Vds Freq_Hz NFmin_dB |Γopt| ∠Γopt_deg Rn_Ω`, with Γopt on 50 Ω and `#` or `!` starting a comment. **Measured at**
is the ambient the data was taken at, °C.

**Fit** (Run). At each bias the model the earlier steps built is simulated with the same S-parameter noise analysis a
schematic runs, between 50 Ω ports. Every part of the extrinsic network is in that simulation at the device's
temperature, self-heating included, so its noise is taken out of the measurement by the model itself: that is the
de-embedding, and it uses the model's own network. What is left is fitted by least squares on the noise correlation:
`Td`, the drain temperature (a noise current `4k·Td·gds` across the intrinsic drain–source), and `Tg`, the temperature of
the network's element named `Ri`, when the network has one and the data separates the two. Otherwise `Ri`, if present,
stays at the device temperature and `Td` is fitted alone.

The readout gives each bias's temperatures and the RMS difference between measured and modelled NFmin (dB), Γopt and Rn
(relative), and then the two side by side at each frequency. **Export** writes them as the `.cmodel`'s `Noise` block: a
constant from one bias, and from several a plane in `vgs` and `vds` fitted through them. This is the one noise fit the
composer does; it does not extract noise in any other way.

## The Traps step {#traps}

The trap model (None, CaptureEmission, Custom), its coupling, a toggle per state, the time constants, and for Custom
each state's dynamics and DC expressions with the result of the fixed-point check. A state the data cannot support shows
why in place of its toggle; a state trained from one trap-setting level shows that level. Without pulsed I–V the
coupling list holds Shift only, and one line under it says why. Under Shift, each enabled state's strength has a lock,
its value and, after a fit, its ± standard error. Typing a value locks it. The step's report gives each strength and the
RMS residual of both relations.

Choosing a model, or **None**, changes what the currents must be trained with, so the step is marked stale until it
runs and Export waits for it. **None** with currents trained with traps: Run re-trains `Id` and `Ig` without them, on the
DC data — the pulsed sets are left out, and the report says so. **None** with trap-free currents: there is nothing to
run, and the step is never stale, since nothing reads an earlier trap fit. **Shift** with currents trained with traps: Run re-trains them trap-free first, then fits the strengths.

What the time constants and states mean: [How traps work in the model](#trap-model).

| Plot tab | Shows |
|---|---|
| Pulsed I–V | Trained: per trap-setting level, measured Id against the model at the cursor's Vgs; points pulsed beyond the trap-setting bias are hollow — validation only |
| DC I–V | Trained: the DC data against the model, as validation |
| Gm | Shift: Gm against Vgs at the cursor's Vds — RF from S (symbols), the shifted model with its states frozen (lines), the trap-free current (dashed) |
| Gds | Shift: Gds against Vds at the cursor's Vgs, drawn the same way |

## The Check step {#check}

**Check Charge Conservation** — also on the toolbar — checks the data, the fitted model and a large-signal drive. The
result is one row each — `Data gate 0.40 %`, `Data drain 3.80 %`, `Model < 0.01 %`, `HB 0.2 µA` — and the
transcapacitance as information. A percentage below 0.01 % reads `< 0.01 %`: a residual that small is zero for this
purpose.

| Plot tab | Shows |
|---|---|
| Curl Qg, Curl Qd | Each charge row's curl over the bias plane, % |
| Paths Qg, Paths Qd | The two line-integral paths' difference, % of the charge's range |
| Spectrum | The gate current's spectrum with the drive at f and at 2f; a change in its DC is DC made by a non-conservative charge |

## The Validate step {#validate}

One row per data set with its tolerance and, after a run, its result line. **Run all**, or ▶ on one row. The
tolerances are edited below the rows.

A loadpull file is compared **at compression**, not drive by drive: at each load the measurement drives to the
**Compression** setting (3 dB of Gt by default), the model is driven to the same compression at that load, and Pout,
Gt, PAE, drain efficiency and AM/PM are compared there, with Zin as the VSWR between measured and model. Each side's own
MXP and MXE are compared too, by their Pout or efficiency and by their load, as a VSWR. Only what the file carries is
compared, and only at loads the measurement drives that far; a file that reaches the compression at no load says so and
is not graded. `circuitrf model validate --compression 2` changes it for one run. **Show testbench** writes each item's generated `.cnl` beside the `.cmc` and shows
it in the file manager.

| Plot tab | Shows |
|---|---|
| S, Smith | S at the measurement plane at the cursor's bias, measured (symbols) against model (lines) |
| I–V | The I–V family |
| Power, IM | Pout, gain, PAE and Id, or IM3 and IM5, against input power |
| Loadpull | Measured and modelled Pout contours at the compression the comparison is made at |

## The Model Validation Report {#report}

**Model Validation Report…** (toolbar, or the Validate step) opens a dialog with every option of the report. Each
opens on the value stored in the document or its default; **Apply** stores them.

{{ui: model-report-dialog}}

| Option | What it sets |
|---|---|
| Sections | Which sections the report writes: DC I–V, Thermal, Charge, C–V, S and the PA classes. Leaving one out skips its runs too, and the PA classes are the slow one |
| S compared at these biases | The biases whose S-parameters are compared, model against data. None ticked: the lowest, middle and highest current |
| C–V curves at Vds, at Vgs | The fixed voltage of each C–V curve. None chosen: five spread over the data |
| PA classes | Which classes (B, J, J\*, F, F⁻¹) are loadpulled with the model, and at what frequency and bias |
| R_opt, Source Z, Source 2f0 | The optimum load resistance and the source's fundamental and second-harmonic terminations. Empty: from the knee, a conjugate match, and a short |
| Radius, rings, spokes | The disc of loads round each class's optimum: its reflection-coefficient radius, and how finely it is sampled |
| Drive start, step, max | The input-power ladder each load is driven up, in dBm |
| Compression | How far into compression each load is driven before its performance is read, in dB |

**Generate** stores the options as **Apply** does, then runs and writes the report; **Export PDF…** writes it as one
PDF. Closing the dialog without either leaves the document's options as they were.

The choices are the data's own values. The S biases are boxes laid out as the bias plane, Vds across and Vgs down;
hover over one for its bias, tick the ones to compare. A very irregular plan, which would need more than 2,500 boxes,
is typed instead. The C–V voltages are buttons, one per measured voltage, typed past 24. Beside each heading,
**Select all** and **Clear all** tick or clear every value at once. Under each is how many are
chosen, or what none chosen means: the lowest, middle and highest current for S, five voltages spread over the data
for C–V. The frequency and the PA bias list the measured values and accept any other; left empty, each shows the value
the report will use, such as the S data's median frequency.

**Generate** writes the report into `<name>-report/` beside the `.cmc`, and its Data Display document,
`<name>-report.cdd`, beside that folder: one tab per section — the bias points of every data set, I–V families, the ΔQ
maps, the C–V families with the fixed voltage as the legend, S at the chosen biases, the Rth plot, and per PA class its
loadpull contours, its performance table and the region where the drive-up converged. **Export PDF…** writes the
report: its summary first, then every tab as a page headed by the conditions that reproduce it. `model report --pdf`
writes the same file.

The summary is written for whoever uses the model in a design, who may not be whoever built it:

- **Scorecard.** One row per test, each **Green** (very good to excellent), **Yellow** (usable, with something specific
  the designer should know) or **Red** (does not reasonably match the measurement, or does not converge), and what that
  means for a design. A comparison with data is Green within the validation's tolerance, Yellow within twice it, and Red
  beyond; points where the solve failed make it Yellow (up to 5 % of them) or Red. Rth is Green within 10 % of the
  data's and Yellow within 25 %; a charge within 2 % and 5 % of its range; a PA class when HB converges at its load and
  over 95 % (Yellow: 75 %) of the disc about it. The overall grade is the worst row.
- **Where the model is supported by data**: the bias range the currents and charges were trained over, the hottest
  point in the I–V data, the temperatures, the frequencies checked, whether large-signal data was compared, and what
  the model does outside that range.
- **How the model was fitted**: each current and charge as an equation (and which), a table (and its grid) or a neural
  network (and its layers), with its error on held-out points; traps, self-heating and the extrinsic network.
- **Data**: every data set, what it was used for, its points and bias values, its temperatures and frequencies.

Each test's page states its bias, source and load terminations at every harmonic, frequency, temperature, Rth, and
the harmonic count, tolerance, FFT oversample and iteration cap of the simulation, and names its testbench in
`testbenches/`, which `circuitrf dc`, `sparam`, `hb` or `lp` runs as written. The same text is in `summary.txt`, and
in `report.json` for a program to read.

## The Export step {#export}

`N0` and `W0`, which can be changed here as on the Data step, the scaling rule of each network value (the same rule as
the Values table's scaling column, so either can set it), the symbol, and the `.cmodel`'s path. The symbol shows as one
picture; click it for the menu of every symbol the model can take, which are those with gate, drain and source pins (and
a fourth for the thermal node of a self-heating model). The step's buttons are on its header: **Export** writes the
model; **Copy Instance** puts an instance of it on the clipboard, ready to paste into a schematic; **VA** is Export
Verilog-A. The plots show
the model's Output and Transfer curves against its data. A placed instance's thermal pin T, and how to get it, is in
[The thermal pin, T](components.html#composedfet-thermal-pin).

A step whose result predates the data or the network blocks Export with one line naming it; **Run stale** re-runs the
stale steps in order.

**VA** (Export Verilog-A…) writes the same model as Verilog-A source, for another simulator: [Verilog-A export](#veriloga).

A placed model can have parts of itself switched off per instance, to see what each costs the circuit:
[Switching parts of the model off](#switches).

A passive document exports as a cell: its name, the library or workspace folder it goes in, and the fitted values as
its parameters.

### Publishing to a library {#publish}

**Export to Library…** on the toolbar publishes the model as a part in a [model library](model-library-format.html#making),
for others to place from the palette. It opens the schematic editor's Export to Library dialog on a cell circuitRF writes
for the model, so there is no schematic to draw first, and the part is the same one a cell you drew yourself would give.

For an active model the cell places the exported `.cmodel` as one `ComposedFET`. The part's pins are **g**, **d** and
**s**, in that order, and **t** as pin 4 when the model self-heats. It has two parameters: **N**, the finger count,
which starts at the model's N0, and **Temp**, the device's temperature in °C, which starts at `temp` and so follows the
design's. The part number starts as the `.cmodel`'s name, the symbol as the FET glyph, and the frequency range as the
measured data's; the dialog can change each of them. A passive document publishes the cell its Export step wrote, as
it is.

The button is greyed, and its tooltip says why, until the document is saved and its model is exported: an active
model needs its `.cmodel` exported since the document last changed, and a passive one needs its cell. The cell circuitRF
writes is deleted when the dialog closes, whether the export succeeds, is refused or is cancelled.

**Compile netlist** in the dialog publishes an active model as its own compiled module, without the `.cmodel`:
[Protected models](protected-models.html#compile-netlist).

## Switching parts of the model off {#switches}

Ten instance switches turn one effect off on one `ComposedFET`, so you can see what it costs the circuit in gain,
efficiency or linearity:

| Switch | Default | What changes |
|---|---|---|
| `Charge` | `true` | `false`: the intrinsic device has no capacitance at all. The network's capacitors stay. |
| `LinearC` | `false` | `true`: the intrinsic capacitances are held at their values at the bias point, so they do not change with drive. |
| `LinearCgs` | `false` | `true`: Cgs alone is held at its bias-point value; the other capacitances follow the model. |
| `LinearCdg` | `false` | `true`: Cdg alone is held at its bias-point value. |
| `LinearCds` | `false` | `true`: Cds alone is held at its bias-point value. |
| `LinearCm` | `false` | `true`: the transcapacitance Cm alone is held at its bias-point value. |
| `GateLag` | `true` | `false`: gate-lag traps follow the voltage instantly. |
| `DrainLag` | `true` | `false`: drain-lag traps follow the voltage instantly. |
| `SelfHeating` | `true` | `false`: the device stays at the run's `temp`; pin 4, if wired, sees no rise. |
| `Dispersion` | `true` | `false`: the drain carries its DC current at every frequency. |

`Charge=false` wins over every `Linear` switch, and `LinearC=true` is all four branch switches at once. **Nothing refits**: the `.cmodel` is untouched, and a switch changes only how that
one instance evaluates it. A switch naming a part the model does not have — `GateLag=false` on a model with no gate-lag
trap — changes nothing and says so, so one schematic can hold different models.

**The bias point** is the instance's quiescent DC operating point: the intrinsic `vgs` and `vds` of the circuit's own DC
solution with every RF source at zero, with the junction temperature and trap states that solution has. Every analysis
solves it first, and solves it again when a sweep moves the bias. With `LinearC=true` the charge is the tangent there,
its temperature and trap inputs held too, so the capacitance is constant; the currents stay nonlinear. S-parameters at
the bias point are therefore identical either way, and a power sweep differs only once the drive is large.

**One capacitance at a time.** The intrinsic device's charges are two terminal charges, `Qg` and `Qd`, not separate
capacitors. Its capacitances are read from their derivatives, `C11 = ∂Qg/∂vgs`, `C12 = ∂Qg/∂vds`, `C21 = ∂Qd/∂vgs` and
`C22 = ∂Qd/∂vds`, as the circuit set

```
Cgs = C11 + C12     Cdg = −C12     Cds = C22 + C12     Cm = C21 − C12
```

so `Cgs` sits between gate and source, `Cdg` between gate and drain, `Cds` between drain and source, and `Cm` is the drain
charge's extra dependence on `vgs`. "Linear Cgs" means `Cgs` keeps its bias-point value at every voltage while `Cdg`,
`Cds` and `Cm` follow the model. The charges stay charges, so nothing is lost or created over an RF cycle. At the bias
point the device is exactly the model, so S-parameters there do not change. Once the drive is large, the difference shows
which capacitance causes how much AM–PM and harmonic content. Any combination works: `LinearCgs=true LinearCds=true` holds
both. With any of them set, the whole charge is read at the bias point's temperature and trap state, as `LinearC` does.

**No gate lag** means the trap state takes, at every instant, the value it would have at DC at those voltages. That trap
then no longer tells RF from DC: the drain current under drive is the DC I–V surface in that respect, and a threshold
shift from that trap is zero. Its node is not created.

Each switch is an ordinary parameter, so it can be bound to a variable and swept. To compare two runs on one plot:

```
gl = 1
ComposedFET:X1  g d s  File="gan_8x125.cmodel"  GateLag=gl
analysis HB1 type=hb Tone=2 GHz MaxHarm=5
analysis SW1 type=parametric_sweep Var=gl Values=1,0 Inner=HB1
```

In the schematic they are the **Model switches** group of the instance's parameters. An instance with any switch off
carries one line under it naming what is off — `linear C · no gate lag · isothermal`, or `linear Cgs, Cds` — and every run that uses it adds
one note to Messages. `circuitrf explain` reports each instance's switches.

### In harmonicaRF {#switches-harmonicarf}

A `.cmodel` can be [harmonicaRF](harmonicarf.html#dut)'s device. There the switches are checkboxes on the readout
strip, and each toggle re-solves the loadline, the contours and the power sweep while you watch. The strip's `DUT` line
names whatever is switched off. The loadline and its DCIV family are read at the intrinsic device, inside the network,
with each DC curve at its own trap state and self-heated temperature.

## Verilog-A export {#veriloga}

**VA** (Export Verilog-A…) on the Export step's header writes the model as one Verilog-A source file, so the same model runs in another
simulator. It asks where the file goes, offering `<name>.va` beside the `.cmodel`'s path, and the line under the step's header
names the file and its module. The file is self-contained: it includes only the standard disciplines and constants
headers, and its header comment lists every parameter with its unit, default and meaning.

circuitRF runs Verilog-A too, so it can check the file itself. In the example, the `GanVerilogA` bench places the
`.cmodel` as a `ComposedFET` and `gan_4x125.va` as a [VerilogA](veriloga.html) on identical bias networks — ports 1 and 2
for one, 3 and 4 for the other — with a DC sweep, S-parameters and a drive sweep; its measures show each pair side by
side. Running it needs a Verilog-A compiler ([The compiler, and why it is yours](veriloga.html#compiler)).

Headless, the same function is `circuitrf model export gan.cmc -o gan.va`, and a `.cmodel` someone sent you exports with
no document: `circuitrf model export gan.cmodel -o gan.va` ([`model`](cli.html#model)).

### What is exported

Everything a placed `ComposedFET` has: the network, the equation and ANN constituents, the continuation outside the
training domain, drain–source symmetry, the charges, dispersion, the trap states, self-heating, the temperature range
and periphery scaling. Equations and ANNs are written as code, so the file computes the model's own numbers.

**Tables are not exported.** A model with a table constituent shows one line on the Export step — `Id and Qg are tables;
Verilog-A export needs ANN or equation constituents.` — and **VA** stays off; **Export** is unaffected.
To export, refit that constituent as an ANN or an equation on its step ([The method picker](#method)), then export again.

### Terminals

`g`, `d` and `s`, and for a self-heating model a fourth, `t`, whose temperature is the channel's rise above the ambient,
in K. Leave `t` off the symbol (one pin fewer) and the device heats on the model's own thermal network. Drawn and left
unwired, it does the same: unlike the general rule in [The thermal terminal](veriloga.html#terminals), this module keeps
its thermal network on a node of its own and joins `t` to it, so an unwired `t` is never a floating node. A thermal
network wired to `t` sits in parallel with the model's, as on a `ComposedFET`'s [T pin](components.html#composedfet-thermal-pin).

### Parameters

| Parameter | What it is |
|---|---|
| `N` | The finger count. Currents, charges and network values scale from the measured device's `N0`, as on a `ComposedFET`. |
| `Temp` | The device temperature, °C. Unset follows the simulator's. |
| `Rth`, `Cth` | This device's own thermal resistance and capacitance at `N` fingers. Unset is the model's, scaled. |
| The ten switches | Below. |
| `VgsQ`, `VdsQ`, `TjQ` | The bias point the linear-charge switches hold the charge at. Below. |
| Network values | One per value of the six-pin network, at its fitted value, with its scaling rule in the header. |
| `Id_beta`, … | An equation constituent's parameters, named after the constituent. |
| `Gx_Id`, … | The conductances the continuation outside the training domain adds. |
| Trap parameters | The trap model's own, such as `TauEmit2`. |

`m` is the host simulator's own device multiplier: it multiplies the whole device, so the module declares none.

The switches are those of [Switching parts of the model off](#switches), as integers, `1` on and `0` off:

| Switch | Default | `0` or `1` does |
|---|---|---|
| `Charge` | `1` | `0`: no intrinsic capacitance. |
| `LinearC` | `0` | `1`: the intrinsic capacitances held at the bias point. |
| `LinearCgs`, `LinearCdg`, `LinearCds`, `LinearCm` | `0` | `1`: that one capacitance held at the bias point. |
| `GateLag`, `DrainLag` | `1` | `0`: those trap states follow the voltage instantly. |
| `SelfHeating` | `1` | `0`: the device stays at `Temp`. |
| `Dispersion` | `1` | `0`: the drain carries its DC current at every frequency. |

A switch naming a part the model does not have changes nothing, and the header names those switches.

### The bias point {#veriloga-bias}

`VgsQ`, `VdsQ` and `TjQ` are needed only when `LinearC` or one of the four single-capacitance switches is on. Unset with
such a switch on, the run stops at its first step with a message naming the switch and the parameters to set.

- `VgsQ` and `VdsQ` are the **intrinsic** gate–source and drain–source voltages, in V: across the core, inside the
  network, not at the terminals. They differ from the terminal bias by the drops on the network's resistances.
- `TjQ` is the junction temperature at that point, in °C. Unset, it is `Temp`, and a self-heating device says once that
  its charge is held at the ambient.

To get them from circuitRF, place the `.cmodel` as a `ComposedFET` on the same bias network and run DC. Its intrinsic
nodes and its thermal node are in the DC result as `X1.gi`, `X1.di`, `X1.si` and `X1.t`:

```
VgsQ = V(X1.gi) − V(X1.si)      VdsQ = V(X1.di) − V(X1.si)      TjQ = temp + V(X1.t)
```

On the example's bench that is `VgsQ = −0.8453`, `VdsQ = 7.891` and `TjQ = 26.36`. Or take them from the other
simulator's own operating point of the placed module: the same differences across its internal nodes `gi`, `di` and
`si`, and the rise on its thermal node `ti`.

### What differs from a `ComposedFET`

A `ComposedFET` finds the bias point itself, from the circuit's DC solution; the `.va` takes it as given. Nothing else
differs.

## Plots {#plots}

Every plot is a Data Display plot. Measured traces are symbols and model traces lines. The plots are drawn from the
document, so the menu's Plot Properties… and Axes Labels… are greyed; Copy and Export… are always there.

**Markers.** Double-click a curve, a point or a Smith locus to add a marker. Its info box opens in a band between the
plot's title and its axes, beside the boxes already there. Drag a marker to move it along its trace — on the bias plane
to the point nearest the pointer — and the arrow keys step it. A bias-plane marker reads its data set and bias. Markers
stay through every redraw and reappear when you return to their tab. Select a box and press Delete, or use its menu, to
remove one. The Data step's tabs are glyphs: Output and
Transfer a small plot marked `Vds` and `Vgs`, S one marked `S`, the bias plane a grid of points, and Smith the Smith
glyph. The button at the right of the tab strip, **Open in Data Display**, sends the plot's data to a new display: the
step's plot data, or on the Data, Pads and Network steps the curves the tab shows.

## Passive documents: Data and Topology {#passive}

The Data step lists the block, or the family's blocks with their geometry values, and the analysis lines. The S and
Smith tabs show the block's S-parameters.

On the Topology step, **Propose** ranks the library's topologies against the block and lists the top three, each drawn
small with its |ΔS|; double-click one to choose it. A topology of your own is pasted into the network panel.
The built-in topologies are one- and two-ports. For a block with more ports, such as a board's six-port EM result,
Propose offers nothing unless you draw a topology whose pins 1…N are the ports; the file can also be placed as it is
as an S-parameter component.

## Undo {#undo}

Every edit — a value, a lock, a bound, a tie, an exclusion, a step setting, a network choice — is one undo step
(⌘Z / ⇧⌘Z). A run is not: it is repeated by running again, and its result stays until the next run replaces it.

## From a script or an agent {#headless}

Every run of this window is a [`model`](cli.html#model) verb noun — `model import`, `model deembed`, `model fit`,
`model check-charge`, `model validate`, `model export`, `model report` — calling the same function, so a document built in
the window runs the same way from a script or an agent. Export to Library is
[`export part <file.cmc>`](cli.html#export-part). An agent producing the data in another simulator reads
[Model Data](model-data.html) as `circuitrf reference model-data`, and checks each file with
`model import --dry-run` before anything is written.

## References {#references}

The primary reference, followed wherever a method choice is open:

- **[P1]** J. C. Pedro, D. E. Root, J. Xu, L. C. Nunes, *Nonlinear Circuit Simulation and Modeling: Fundamentals for
  Microwave Design*, Cambridge University Press, 2018. Ch. 5: parasitic stripping and the intrinsic linear model.
  Ch. 6: constitutive relations, tables, ANNs, extrapolation and linearisation (§6.2); charge modeling (§6.3);
  dispersion (§6.4); symmetry (§6.5); self-heating and trapping (§6.6); waveform-based identification (§6.7).

The papers behind its methods:

| | Reference | Used for |
|---|---|---|
| **[X03]** | J. Xu, M. C. E. Yagoub, R. Ding, Q. J. Zhang, "Exact adjoint sensitivity analysis for neural-based microwave modeling and design," *IEEE Trans. Microwave Theory Tech.*, Jan. 2003 | The adjoint training of the charges, and value-and-derivative training of `Id` |
| **[X06]** | J. Xu, D. Gunyan, M. Iwamoto, A. Cognata, D. E. Root, "Measurement-based non-quasi-static large-signal FET model using artificial neural networks," *IEEE MTT-S Int. Microwave Symp.*, 2006 | ANNs over tables where low-level distortion matters; the DC/RF dispersion time constant |
| **[X07]** | J. Xu, D. Gunyan, M. Iwamoto, J. Horn, A. Cognata, D. E. Root, "Drain-source symmetric artificial neural network-based FET model with robust extrapolation beyond training data," *IEEE MTT-S Int. Microwave Symp.*, 2007 | The drain–source symmetry option; guided extrapolation |
| **[X10]** | J. Xu, J. Horn, M. Iwamoto, D. E. Root, "Large-signal FET model with multiple time scale dynamics from nonlinear vector network analyzer data," *IEEE MTT-S Int. Microwave Symp.*, 2010 | Self-heating and trap states as model inputs; temperature in the charges |
| **[X14]** | J. Xu, R. Jones, S. A. Harris, T. Nielsen, D. E. Root, the GaN dynamic FET model from active source-injection measurements, *IEEE MTT-S Int. Microwave Symp.*, 2014, with its ARFTG (June 2014) validation companion | Training on DC, S-parameters and waveforms together; DC I–V as a training target |
| **[I11]** | M. Iwamoto, J. Xu, J. Horn, D. E. Root, "III-V FET high frequency model with drift and depletion charges," *IEEE MTT-S Int. Microwave Symp.*, 2011 | **Drift charge in Qg** |
| **[G19]** | J. Gomes, L. Nunes, C. Gonçalves, J. Pedro, "An accurate characterization of capture time constants in GaN HEMTs," *IEEE Trans. Microwave Theory Tech.*, Jul. 2019 | Trap capture times; the double-pulse pre-pulse |
| **[N22]** | L. C. Nunes, J. L. Gomes, F. M. Barradas, J. C. Pedro, "A simple thermally activated trapping model for AlGaN/GaN HEMTs," *European Microwave Integrated Circuits Conf.*, 2022 | Thermally activated emission in `CaptureEmission` |
| **[P21]** | J. C. Pedro, "Characterization and modeling of AlGaN/GaN HEMT trapping transients," MOS-AK workshop, 2021 | The trapped charge as a back gate: the threshold-shift coupling |
| **[GSF19]** | G. P. Gibiino, A. Santarelli, F. Filicori, "Charge-conservative GaN HEMT nonlinear modeling from non-isodynamic multi-bias S-parameter measurements," *Int. J. Microwave Wireless Technol.* 11, 2019 | Why a capacitance model fails in harmonic balance; charges from multi-bias S |
| **[L26]** | H. Luo, Z. Wang, Y. Guo, "A physics-informed neural network-based scalable model for GaN HEMTs," *IEEE Trans. Microwave Theory Tech.* 74(5), May 2026 | Affine periphery scaling of the extrinsic elements |

Also used:

- **[R11]** M. Rudolph, C. Fager, D. E. Root (eds.), *Nonlinear Transistor Model Parameter Extraction Techniques*,
  Cambridge University Press, 2011: the measurement-based thermal model (§2.8), HEMT extrinsic extraction (§3.4),
  uncertainty of small-signal extraction (ch. 4) and dispersive effects (ch. 7).
- **[A07]** P. H. Aaen, J. A. Plá, J. Wood, *Modeling and Characterization of RF and Microwave Power FETs*, Cambridge
  University Press, 2007.
- **[T20]** J. Tarazi, *Nonlinear Modelling of GaAs and GaN High Electron Mobility Transistors*, PhD thesis, Macquarie
  University, 2020: unit-width scaling, the thermal network, and separating thermal and trap state in small-signal data.
- **[NW06]** J. Nocedal, S. J. Wright, *Numerical Optimization*, 2nd ed., Springer, 2006: the L-BFGS optimiser and its
  line search (Algs. 7.4 and 3.5).

Background on traps and their characterization:

- **[V01]** R. Vetury, N. Q. Zhang, S. Keller, U. K. Mishra, "The impact of surface states on the DC and RF characteristics
  of AlGaN/GaN HFETs," *IEEE Trans. Electron Devices* 48(3), pp. 560–566, Mar. 2001: drain current collapse and the virtual
  gate.
- **[B02]** S. C. Binari, P. B. Klein, T. E. Kazior, "Trapping effects in GaN and SiC microwave FETs," *Proc. IEEE* 90(6),
  pp. 1048–1058, Jun. 2002: a review of trapping and its effect on power performance.
- **[J07]** O. Jardel, F. De Groote, T. Reveyrand, J.-C. Jacquet, C. Charbonniaud, J.-P. Teyssier, D. Floriot, R. Quéré, "An
  electrothermal model for AlGaN/GaN power HEMTs including trapping effects to improve large-signal simulation results on
  high VSWR," *IEEE Trans. Microwave Theory Tech.* 55(12), pp. 2660–2669, Dec. 2007: gate and drain lag in a large-signal
  model, checked against loadpull and waveform measurements.
- **[S14]** A. Santarelli, R. Cignani, G. P. Gibiino, D. Niessen, P. A. Traverso, C. Florian, D. M. M.-P. Schreurs, F.
  Filicori, "A double-pulse technique for the dynamic I/V characterization of GaN FETs," *IEEE Microwave Wireless Compon.
  Lett.* 24(2), pp. 132–134, Feb. 2014: I–V curves at a fixed trapping state.
