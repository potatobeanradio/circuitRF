---
title: Model Composer
slug: reference/model-composer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Model Composer
lede: A nonlinear FET model, or a passive one, built from measured or simulated data — in a document you can close and pick up again.
keywords: model extraction, device modeling, FET model, GaN HEMT, extrinsic network, de-embedding, pad de-embedding, cold-FET, multi-bias, bias plane, intrinsic, passive model, topology, .cmc, .cmodel, Verilog-A export, .va
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
sit inside an extrinsic network of pads, inductances and resistances. A model can add self-heating, trap states, or a
simpler DC/RF dispersion. A `.cmodel` can also be written without the composer, from equations, a table or a network
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
| Check | Checks that the charges conserve charge. |
| Validate | Compares the model with every data set at the plane it was measured at. |
| Export | Writes the `.cmodel`. |

**Fit all** (`model fit --step all`) runs every step the document needs, in this order: the network if any value is
free, then thermal, current, traps when a trap model is chosen, and charge. With no S data, the network is held at its
values (`network: held at its values — no S data to fit to`) and the charge step writes the typed capacitances; a fit
of the network step alone still refuses.

## Data kinds and their conditions {#kinds}

| Kind | Holds | Read from |
|---|---|---|
| `DcIv` | Terminal voltages and currents at DC | CSV, `.mat`, `.npz`, `.npy` |
| `PulsedIv` | The same, sampled during a pulse from a stated quiescent point | CSV, `.mat`, `.npz`, `.npy` |
| `SParams` | Two-port S at each bias, and the bias currents when the files state them | A folder of Touchstone files |
| `PulsedSParams` | S sampled during a pulse | A folder of Touchstone files |
| `SimulatedTables` | Currents, and an admittance or S table, a capacitance table, or both; optionally intrinsic voltages | `.mat`, `.npz`, `.npy` |
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

## Traps, and what pulsed data allows {#trap-data}

Traps are a setting. **None** is the default. **CaptureEmission** has a gate-lag state and a drain-lag state, each
switched on separately. A state is captured quickly when its voltage moves one way and emits slowly when it moves back.
The defaults are 10 ns capture at a 1 V gap and 1 ms emission, and emission can be made thermally activated.
**Custom** lets you write each state's dynamics and DC value yourself.

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
| Double pulse | The pre-pulse sets the state, and the measuring pulse reads it before emission moves it. This is the clearest data. |
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
| Toolbar | Open, Save, Save As · Undo, Redo · the four panel lamps · Check Charge Conservation, Model Validation Report · Run ▾, Cancel · Help |
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
| Map | CSV, `.mat`, `.npz`, `.npy`: what each column or array holds, `name=quantity[:unit]`. For an array file it is filled in from the array names |
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
the window runs the same way from a script or an agent. An agent producing the data in another simulator reads
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
