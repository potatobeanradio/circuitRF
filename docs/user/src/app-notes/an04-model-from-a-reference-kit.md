---
title: AN-04 — A nonlinear FET model from a reference kit
slug: app-notes/an04-model-from-a-reference-kit.html
doc-kind: Application Note
breadcrumb: Docs > App Notes > AN-04 A model from a reference kit
lede: A transistor that already has a model in another simulator's design kit can become a ComposedFET by sampling that model and fitting what it gives. Which runs to make in the source simulator, which plane to sample at and how to get there, what each Model Composer step does with the result, and how to check the model against the kit it came from.
keywords: Model Composer, reference kit, design kit, PDK, nonlinear model, FET model, GaN HEMT, ComposedFET, .cmodel, simulated tables, .ds, binary simulation dataset, intrinsic plane, negative network, de-embedding, extrinsic network, six-pin network, DC I-V, Y-parameters, capacitance table, self-heating, isothermal, validation, loadpull, Model Validation Report, application note
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#when">When this note applies</a></li>
<li><a href="#flow">The flow</a></li>
<li><a href="#sample">What to run in the source simulator</a></li>
<li><a href="#plane">Choosing the plane, and the negative network</a></li>
<li><a href="#tables">Writing the tables</a></li>
<li><a href="#composer">In Model Composer, step by step</a></li>
<li><a href="#defaults">When the defaults are not enough</a></li>
<li><a href="#validate">Checking the model against the kit</a></li>
<li><a href="#direct">Tables on the kit's own grid</a></li>
<li><a href="#cli">The same flow headless</a></li>
<li><a href="#checklist">Checklist</a></li>
</ol>
</nav>

## When this note applies {#when}

A transistor often already has a good model: the one in a foundry's or a vendor's design kit, written for another
simulator. That model can be the **reference** for a circuitRF model. Sample it the way a lab would measure a device,
hand the samples to [Model Composer](../reference/model-composer.html), and the result is a `.cmodel` that a schematic
places as a [`ComposedFET`](../reference/components.html#composedfet) and that DC, S-parameters, harmonic balance and
loadpull all use.

Sampled data is better than measured data in three ways, and the flow below is built around all three:

- **It is noise-free and as dense as you like.** A grid of every 0.1 V costs seconds, not a week on a probe station.
- **It can be taken where no instrument can reach**: at the core's own terminals, inside the pads, inductances and
  resistances, and with self-heating switched off.
- **It can be repeated at will**, so the same benches can be run on the kit and on the fitted model and the two
  compared point for point.

The work that takes longest is not the fit. It is deciding what to export from the source simulator and getting it
out in a form the composer reads. Most of this note is about that.

## The flow {#flow}

<figure class="fig">
<svg viewBox="0 0 560 290" role="img" aria-label="The flow: the reference kit is sampled into tables, Model Composer fits them into a .cmodel, and the model is validated against large-signal runs of the kit, with a refit loop back to the composer" style="width:100%;height:auto">
  <defs>
    <marker id="an04-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0 0 L10 5 L0 10 z" fill="var(--text)"/>
    </marker>
  </defs>
  <g stroke="var(--border)" stroke-width="1.4" fill="var(--surface)">
    <rect x="10"  y="30"  width="120" height="76" rx="6"/>
    <rect x="150" y="30"  width="120" height="76" rx="6"/>
    <rect x="290" y="30"  width="120" height="76" rx="6" stroke="var(--accent)"/>
    <rect x="430" y="30"  width="120" height="76" rx="6"/>
    <rect x="290" y="196" width="260" height="70" rx="6"/>
  </g>
  <g fill="none" stroke="var(--text)" stroke-width="1.4">
    <path d="M130 68 H 148" marker-end="url(#an04-arrow)"/>
    <path d="M270 68 H 288" marker-end="url(#an04-arrow)"/>
    <path d="M410 68 H 428" marker-end="url(#an04-arrow)"/>
    <path d="M490 106 V 194" marker-end="url(#an04-arrow)"/>
    <path d="M70 106 V 231 H 288" marker-end="url(#an04-arrow)"/>
    <path d="M350 196 V 108" stroke-dasharray="5 4" marker-end="url(#an04-arrow)"/>
  </g>
  <g fill="var(--text)" font-family="var(--font-sans)" text-anchor="middle">
    <text x="70"  y="58" font-size="14" font-weight="600">Reference kit</text>
    <text x="70"  y="78" font-size="12" fill="var(--muted)">its own model,</text>
    <text x="70"  y="94" font-size="12" fill="var(--muted)">its own simulator</text>
    <text x="210" y="58" font-size="14" font-weight="600">Sampled tables</text>
    <text x="210" y="78" font-size="12" fill="var(--muted)">DC I–V, Y, per T</text>
    <text x="210" y="94" font-size="12" fill="var(--muted)">.mat</text>
    <text x="350" y="58" font-size="14" font-weight="600">Model Composer</text>
    <text x="350" y="78" font-size="12" fill="var(--muted)">fit and check</text>
    <text x="350" y="94" font-size="12" fill="var(--muted)">.cmc</text>
    <text x="490" y="58" font-size="14" font-weight="600">ComposedFET</text>
    <text x="490" y="78" font-size="12" fill="var(--muted)">the model</text>
    <text x="490" y="94" font-size="12" fill="var(--muted)">.cmodel</text>
    <text x="420" y="224" font-size="14" font-weight="600">Validate and report</text>
    <text x="420" y="244" font-size="12" fill="var(--muted)">the same benches in both simulators</text>
    <text x="180" y="222" font-size="12" fill="var(--muted)">power sweeps, loadpull</text>
  </g>
  <g fill="var(--accent)" font-family="var(--font-sans)" font-size="12">
    <text x="358" y="156">refit</text>
  </g>
</svg>
<figcaption>The whole flow. Small-signal and DC samples of the kit become the model; large-signal runs of the kit
judge it. The dashed path is the loop: a validation that disagrees sends you back to a step in the composer, not to
the source simulator.</figcaption>
</figure>

Two kinds of run come out of the source simulator, and they have different jobs:

| Runs | Become | Used for |
|---|---|---|
| DC I–V, small-signal Y or S at every DC bias, at one or more temperatures | One `SimulatedTables` data set | **Fitting** the currents and the charges |
| Power sweeps, two-tone sweeps, loadpull | `LargeSignal` data sets | **Validation** only — never fitted |

Keeping the large-signal runs out of the fit is what makes them a fair test: the model has never seen them.

## What to run in the source simulator {#sample}

Use one test bench per run, with the kit's transistor at the size you want as the base, and nothing else in it but
bias and ports.

| Run | Analysis | Sweep | What to save |
|---|---|---|---|
| I–V | DC | Vgs inside Vds, inside temperature when there are several | Vgs, Vds and the gate and drain currents at every point |
| Small-signal | S-parameters at every bias of the DC sweep | One low frequency for the charges (a few hundred MHz to 1 GHz); add a span only if the network is to be checked | Y (or S) of the two-port, gate port 1, drain port 2, source grounded |
| Capacitances (optional) | The core's operating-point capacitances, where the simulator reports them | The same bias grid | `cgs`, `cgd`, `cds` and optionally `cm`, or the matrix `C` |
| Large-signal (for validation) | Harmonic balance: power sweep at the operating bias; loadpull at the frequencies you care about | Drive up into compression | The power sweep as a CSV; the loadpull as a file the composer reads (`.spl`, `.lpcwave` or a CSV) |

**Ranges.** Sample wider than the circuit will ever go: from 2 V below pinch-off to well into forward gate conduction,
and from 0 V (or slightly negative) to beyond the peak drain swing. A model is continued linearly past the edge of its
data, and that continuation is not the device. Sampled points cost nothing; the
[bias table](../reference/model-data.html#ranges) gives the steps.

**Settings in the source simulator.** Three matter, and each one gives a model that looks right at the operating point
and is wrong elsewhere when it is missed:

- **Self-heating off** for every fitting run — the kit's thermal resistance set to zero, or its thermal switch off. The
  composer fits **isothermal** currents, at the temperature of each run, and adds the heating itself on the Thermal
  step. Data that already contains heating would be heated twice.
- **Traps and dispersion off**, unless you mean to model them. A DC sweep with them on and a small-signal analysis
  with them frozen describe two different devices.
- **Stay inside the kit's valid range.** Many kit models clamp their inputs outside it, and the clamped region samples
  as flat data that the fit then reproduces faithfully.

**Temperature.** Run the DC and small-signal sweeps at two or more temperatures, spanning the junction temperatures
the circuit will reach under drive (the ambient plus the heating). The model then takes `T` as an input, follows the
run's `temp`, and self-heating becomes available.

## Choosing the plane, and the negative network {#plane}

The composer always fits the **core**: the intrinsic gate, drain and source, inside the pads, inductances and series
resistances. A kit device is that core wrapped in its own extrinsic network, and the samples can be taken on either
side of it.

<figure class="fig">
<svg viewBox="0 0 560 250" role="img" aria-label="A bench port drives a negative copy of the kit's extrinsic network, read from the core outwards, which meets the kit device at its terminal; the kit's own network then leads to the core. The two networks cancel, so the port's voltage and current are the core's." style="width:100%;height:auto">
  <defs>
    <marker id="an04-arrow2" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
      <path d="M0 0 L10 5 L0 10 z" fill="var(--accent)"/>
    </marker>
  </defs>
  <g stroke="var(--border)" stroke-width="1.4" fill="var(--surface)">
    <rect x="70"  y="60" width="160" height="70" rx="6" stroke="var(--accent)"/>
    <rect x="300" y="60" width="140" height="70" rx="6"/>
    <rect x="466" y="60" width="80"  height="70" rx="6"/>
  </g>
  <rect x="290" y="40" width="264" height="110" rx="8" fill="none" stroke="var(--muted)" stroke-width="1.2" stroke-dasharray="6 4"/>
  <g fill="none" stroke="var(--text)" stroke-width="1.6">
    <circle cx="30" cy="95" r="14"/>
    <path d="M44 95 H 70 M230 95 H 300 M440 95 H 466"/>
  </g>
  <g stroke="var(--muted)" stroke-width="1.2" stroke-dasharray="3 3">
    <path d="M265 30 V 170"/>
    <path d="M453 30 V 170"/>
  </g>
  <g fill="none" stroke="var(--accent)" stroke-width="1.4">
    <path d="M30 204 H 500" marker-end="url(#an04-arrow2)"/>
  </g>
  <g fill="var(--text)" font-family="var(--font-sans)" text-anchor="middle">
    <text x="30"  y="128" font-size="12" fill="var(--muted)">port</text>
    <text x="150" y="90"  font-size="14" font-weight="600">negative copy</text>
    <text x="150" y="110" font-size="12" fill="var(--muted)">−Rg  −Ci  −Lg  −Cp</text>
    <text x="370" y="90"  font-size="14" font-weight="600">kit's network</text>
    <text x="370" y="110" font-size="12" fill="var(--muted)">Cp  Lg  Ci  Rg</text>
    <text x="506" y="100" font-size="14" font-weight="600">core</text>
    <text x="340" y="34"  font-size="12" fill="var(--muted)">kit device</text>
    <text x="265" y="186" font-size="12" fill="var(--muted)">device plane</text>
    <text x="453" y="186" font-size="12" fill="var(--muted)">intrinsic plane</text>
    <text x="265" y="234" font-size="13" fill="var(--accent)">together an identity: the port's V and I are the core's</text>
  </g>
</svg>
<figcaption>Sampling at the intrinsic plane when the kit gives no access to its internal nodes. The negative copy is
the kit's own extrinsic network read from the core outwards, every value negated; cascaded with the real one it is the
identity, at DC and at every frequency. One side of the gate is drawn; the drain and source are treated the same way.
</figcaption>
</figure>

| Plane | How to sample | In the composer |
|---|---|---|
| **Intrinsic** — preferred | Record the core's internal node voltages, if the kit exposes them; or wrap the kit device in its negative network, as drawn | `ReferencePlane=intrinsic`. The network holds the kit's values, all **locked**; nothing is fitted to it |
| **Device** | Sample at the kit device's terminals, as it is | `ReferencePlane=device`, with a six-pin network holding the kit's values, locked; the composer removes it |

**Why intrinsic is preferred.** At the intrinsic plane the points lie exactly on the grid you swept, and the network
plays no part in the fit: it is exported with the core exactly as the kit has it. At the device plane the composer
removes the network with a DC solve carrying the sampled currents, so the intrinsic points move off the grid and every
`Table` constituent must re-grid them. Either way the network values come from the kit, not from a fit: a fit can only
find values the data can tell apart, and the kit already knows them.

**Building the negative network.** Take the kit's extrinsic network from its model card or documentation and draw it
again in the source simulator's bench, between each port and the kit device's terminal:

- **Read it from the core outwards.** What sits next to the core in the kit sits next to the port in the copy. For a
  two-shell network — pad capacitance, inductance, inner capacitance, resistance, from the terminal in — the copy is
  `−R`, `−Cinner`, `−L`, `−Cpad`, from the port in.
- **Negate every value**, the mutuals included. A source resistance and inductance that both loops share go in the
  source lead, negated, in the same place.
- **Use it in DC and small-signal analyses only.** Negative elements are exact in a linear analysis and make the bench
  non-passive, so they have no place in harmonic balance or a transient. The large-signal validation runs use the kit
  device as it is.
- **At DC only the resistances matter.** The `−R` cancels the kit's own drop, so the core sees exactly the port's
  voltage, and the I–V grid you sweep is the intrinsic grid.
- **A value that depends on temperature** must be negated at each temperature you run.

If the kit's network values are not published, sample at the device plane instead and let the
[Network step](../reference/model-composer.html#network) fit them cold-FET and multi-bias. That needs S over a
frequency span, and the two cold biases (`Vds = 0 V`, pinched off and with the gate forward-biased).

## Writing the tables {#tables}

The composer reads a simulator's tables as named arrays: a MATLAB `.mat` (written by MATLAB, or by Python with
`scipy.io.savemat`) or a NumPy `.npz` (`numpy.savez`). Name the arrays as you like. Every array, its unit and the
layouts the import accepts are in [File formats](../reference/model-data.html#format-arrays).

**A simulator that saves a binary simulation dataset** (`.ds`) needs none of the writing below: import that file as
it is. Its arrays are named after the analysis that produced them, `Group.Variable` — `DC1.Vgs`, `DC1.VD.i`, `SP1.S` —
and a DC block and an S block swept over the same biases combine into one data set. The map is written by those names
([A binary simulation dataset](../reference/model-data.html#format-ds)):

```
circuitrf model import kit.cmc kit_sweep.ds --as SimulatedTables --map DC1.Vgs=vgs --map DC1.Vds=vds --map -DC1.VD.i=id --map -DC1.VG.i=ig --map SP1.freq=freq --map SP1.S=S --condition ReferencePlane=intrinsic
```

The `-` in front of the two currents reads them negated, because a bias source's current is recorded flowing into the
source, out of the device.

**What the file holds.** For the runs above, swept as Vgs from −5 to 2 V in 0.1 V steps (71 values), Vds from 0 to
40 V in 0.5 V steps (81), at 25, 85 and 150 °C, with Y at 1 GHz:

| Array | Shape | Unit | Holds |
|---|---|---|---|
| `vgs` | 71 | V | The Vgs sweep values |
| `vds` | 81 | V | The Vds sweep values |
| `T` | 3 | °C | The temperatures |
| `id`, `ig` | 71 × 81 × 3 | A | The DC currents at each Vgs, Vds and T, positive into the terminal |
| `freq` | 1 | Hz | The small-signal frequency |
| `Y` | 71 × 81 × 3 × 1 × 2 × 2 | S, complex | Y at each bias and frequency: gate port 1, drain port 2 |
| `cgs`, `cgd`, `cds` (optional) | 71 × 81 × 3 | F | The core's operating-point capacitances |

The import lists the arrays of the file you pick in that form, before anything is mapped:

```
cds    71 × 81 × 3
cgd    71 × 81 × 3
cgs    71 × 81 × 3
freq   1
id     71 × 81 × 3
ig     71 × 81 × 3
T      3
vds    81
vgs    71
Y      71 × 81 × 3 × 1 × 2 × 2   complex
```

From Python, with the results gathered into arrays of those shapes:

```python
import numpy as np
# vgs (71,), vds (81,), T (3,), freq (1,); id, ig (71, 81, 3) in A; Y (71, 81, 3, 1, 2, 2) complex, in S
np.savez("kit_tables.npz", vgs=vgs, vds=vds, T=T, id=id, ig=ig, freq=freq, Y=Y)
```

From MATLAB, the 2 × 2 matrix may come first, as MATLAB code usually has it — `Y(i, j, f, g, d, t)`, shaped
2 × 2 × 1 × 71 × 81 × 3:

```
save('kit_tables.mat', 'vgs', 'vds', 'T', 'id', 'ig', 'freq', 'Y')
```

The import works out the layout from the shapes, and says how it read any array not in the documented order. The axes
of a grid may come in any order; a table at one frequency needs no frequency axis. A flat list with one value per point
(as a simulator's own export often is) reads too: `vgs`, `vds`, `T`, `id` and `ig` each 71 × 81 × 3 = 17,253 long, and
`Y` 17,253 × 1 × 2 × 2. With the names above, or any of the usual spellings (`Vg`, `Ids`, `Temp`, `freq_GHz`), the import
maps the arrays itself.

At the **device plane**, the file is the same with Y over a frequency span: `freq` 20 long and `Y` 71 × 81 × 3 × 20 ×
2 × 2. A sweep that also wrote the two cold biases as separate runs can go in a second file, or a Touchstone folder.

**Things to check** before importing; the import warns about the first three when it sees them
([What the import checks](../reference/model-data.html#import-checks)):

- **Signs.** Currents are positive **into** the terminal. A simulator that reports the drain current flowing out gives
  a model with negative transconductance. Map such an array with a leading `-` to read it negated: `-DC1.VD.i=id`.
- **The port order.** Gate is port 1, drain port 2, source grounded.
- **Units.** Arrays are read in SI and °C. A frequency in GHz, currents in mA or temperatures in kelvin need their unit
  in the map: `freq:GHz`, `id:mA`, `T:K`.
- **The base size.** Note the kit device's finger count and unit gate width: they become `N0` and `W0`, and every
  instance scales from them ([Periphery scaling](../reference/model-composer.html#scaling)). Sample at a size from the
  middle of the range you will use.

## In Model Composer, step by step {#composer}

**Tools ▸ Model Composer…**, choose **Active**, and import the `.npz`, `.mat` or `.ds` on the **Data** step as
**Simulated tables**. The map field fills itself from the array names; check it against the arrays listed beside it.
For a `.ds` with a DC block and an S block, complete it with each quantity's `Group.Variable` name. The steps then
run in order; for kit data some of them have nothing to do.

<figure class="fig">
<svg viewBox="0 0 540 250" role="img" aria-label="The ten steps of an active Model Composer document and what each does with kit data; Pads and Traps have nothing to do" style="width:100%;height:auto">
  <defs>
    <marker id="an04-arrow3" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
      <path d="M0 0 L10 5 L0 10 z" fill="var(--text)"/>
    </marker>
  </defs>
  <g stroke="var(--border)" stroke-width="1.4" fill="var(--surface)">
    <rect x="10"  y="20" width="92" height="84" rx="6"/>
    <rect x="116" y="20" width="92" height="84" rx="6" stroke-dasharray="5 4" fill="none"/>
    <rect x="222" y="20" width="92" height="84" rx="6"/>
    <rect x="328" y="20" width="92" height="84" rx="6" stroke="var(--accent)"/>
    <rect x="434" y="20" width="92" height="84" rx="6" stroke="var(--accent)"/>
    <rect x="10"  y="146" width="92" height="84" rx="6"/>
    <rect x="116" y="146" width="92" height="84" rx="6" stroke-dasharray="5 4" fill="none"/>
    <rect x="222" y="146" width="92" height="84" rx="6"/>
    <rect x="328" y="146" width="92" height="84" rx="6"/>
    <rect x="434" y="146" width="92" height="84" rx="6"/>
  </g>
  <g fill="none" stroke="var(--text)" stroke-width="1.3">
    <path d="M102 62 H 114" marker-end="url(#an04-arrow3)"/>
    <path d="M208 62 H 220" marker-end="url(#an04-arrow3)"/>
    <path d="M314 62 H 326" marker-end="url(#an04-arrow3)"/>
    <path d="M420 62 H 432" marker-end="url(#an04-arrow3)"/>
    <path d="M480 104 V 125 H 56 V 144" marker-end="url(#an04-arrow3)"/>
    <path d="M102 188 H 114" marker-end="url(#an04-arrow3)"/>
    <path d="M208 188 H 220" marker-end="url(#an04-arrow3)"/>
    <path d="M314 188 H 326" marker-end="url(#an04-arrow3)"/>
    <path d="M420 188 H 432" marker-end="url(#an04-arrow3)"/>
  </g>
  <g fill="var(--text)" font-family="var(--font-sans)" text-anchor="middle">
    <text x="56"  y="46" font-size="13" font-weight="600">Data</text>
    <text x="56"  y="68" font-size="11" fill="var(--muted)">tables, and</text>
    <text x="56"  y="84" font-size="11" fill="var(--muted)">large-signal</text>
    <text x="162" y="46" font-size="13" font-weight="600" fill="var(--muted)">Pads</text>
    <text x="162" y="68" font-size="11" fill="var(--muted)">None</text>
    <text x="268" y="46" font-size="13" font-weight="600">Network</text>
    <text x="268" y="68" font-size="11" fill="var(--muted)">kit's values,</text>
    <text x="268" y="84" font-size="11" fill="var(--muted)">all locked</text>
    <text x="374" y="46" font-size="13" font-weight="600">Current</text>
    <text x="374" y="68" font-size="11" fill="var(--muted)">Id, Ig:</text>
    <text x="374" y="84" font-size="11" fill="var(--muted)">ANN or Table</text>
    <text x="480" y="46" font-size="13" font-weight="600">Charge</text>
    <text x="480" y="68" font-size="11" fill="var(--muted)">Qg, Qd from</text>
    <text x="480" y="84" font-size="11" fill="var(--muted)">Y or C table</text>
    <text x="56"  y="172" font-size="13" font-weight="600">Thermal</text>
    <text x="56"  y="194" font-size="11" fill="var(--muted)">kit's Rth, Cth,</text>
    <text x="56"  y="210" font-size="11" fill="var(--muted)">typed</text>
    <text x="162" y="172" font-size="13" font-weight="600" fill="var(--muted)">Traps</text>
    <text x="162" y="194" font-size="11" fill="var(--muted)">None</text>
    <text x="268" y="172" font-size="13" font-weight="600">Check</text>
    <text x="268" y="194" font-size="11" fill="var(--muted)">charge</text>
    <text x="268" y="210" font-size="11" fill="var(--muted)">conservation</text>
    <text x="374" y="172" font-size="13" font-weight="600">Validate</text>
    <text x="374" y="194" font-size="11" fill="var(--muted)">against the</text>
    <text x="374" y="210" font-size="11" fill="var(--muted)">kit's runs</text>
    <text x="480" y="172" font-size="13" font-weight="600">Export</text>
    <text x="480" y="194" font-size="11" fill="var(--muted)">.cmodel,</text>
    <text x="480" y="210" font-size="11" fill="var(--muted)">or .va</text>
  </g>
</svg>
<figcaption>The steps of an active document, with what each does for kit data. Dashed steps have nothing to do;
the two outlined in the accent colour are where the fitting happens and where the settings are worth a look.</figcaption>
</figure>

| Step | For kit data |
|---|---|
| **Data** | Import the tables as **Simulated tables**: map each array, and state `ReferencePlane` and, for a single temperature, `AmbientC`. Import each large-signal run as **Large signal**. Set `N0` and `W0` to the sampled device's. |
| **Pads** | **None.** A simulated device has no probe pads. |
| **Network** | Pick a stock network or open a six-pin `.csch` of the kit's own, type the kit's values, and **lock every one**. Set each value's scaling rule. With every value locked, the fit holds them. |
| **Current** | `Id` and `Ig`. The ANN default suits most kits; see [below](#defaults) when it does not. |
| **Charge** | `Qg` and `Qd`, from Y, or from the capacitance table if that is what you saved. The default trains an ANN on the capacitances alone. |
| **Thermal** | Turn self-heating on and **type** the kit's `Rth` and `Cth` (or its thermal ladder), since the data was sampled isothermal. Extraction needs pulsed data that a kit does not give. |
| **Traps** | **None**, unless the kit models them and you sampled them as the [reference describes](../reference/model-composer.html#trap-data). |
| **Check** | **Check Charge Conservation.** A kit's own charges can fail it; the model's must pass. |
| **Validate** | Run all. Each large-signal set is compared at its own bench ([below](#validate)). |
| **Export** | Writes the `.cmodel`. **VA** writes the same model as Verilog-A source for another simulator. |

## When the defaults are not enough {#defaults}

The defaults are chosen for a measured device over an ordinary bias range: a two-layer network of 8 `tanh` neurons
per constituent, and a relative weighting floored at 10 % of the largest current. A kit sampled over a wide range —
deep pinch-off to forward conduction, several temperatures, tens of thousands of points — asks more of a fit than
that. Look at the fit before changing anything:

- **The status line** after a fit gives each constituent's held-out error and its training domain, e.g.
  `Id: ANN 2×8 · held-out 0.8 % · domain 412 pts`. When an ANN's held-out error passes 2 % of the current's range on
  more than 2,000 training points, it adds the next size up: `2×8 under-fits 24,000 points: try 2×16, or a Table.`
- **The Error tab** on the Current step maps model minus data over the bias plane. An error concentrated in one corner
  — subthreshold, the knee, the hot end — says which lever to pull.
- **The Gm and Gds tabs** compare the data's own derivatives with the model's. Harmonic balance runs on the
  derivatives, so a current that fits in value and not in slope still drives badly.

Then, one lever at a time:

| Symptom | Try |
|---|---|
| Held-out error high everywhere | A larger network on the method picker (more neurons, or a third layer), more iterations, or more restarts on the step |
| Subthreshold current poor | Lower **Id floor** on the Current step (`IdFloor`, a fraction of the largest current), so small currents count for more |
| Small gate currents lost | Lower **Ig floor** on the Current step (`IgFloor`), the level below which `Ig` is fitted on a linear rather than a logarithmic scale |
| The knee or the hot end poor | More points there in the source simulator; a sampled grid can always be made finer |
| Every ANN size leaves a residual | A **Table** constituent: on gridded kit data it is the samples themselves ([below](#direct)) |
| An equation template does not fit | Expected over a wide range: the templates are for compact, few-parameter models. Use an ANN or a Table |

**Table re-gridding.** When the points do not lie on a grid, a `Table` constituent samples a smooth function of them
onto its grid and fits a spline to that. The function is an ANN trained on the points by default, or a radial-basis
interpolant (**RBF**) through them. The RBF solves one dense system over every point, so its memory grows with the
square of the point count and its time with the cube: 1,000 points take a tenth of a second, 4,000 about 11 s, 5,000
about 22 s. Above 5,000 points the fit refuses before it starts and names the ANN instead. A dense kit sweep is
usually a grid, and then no re-grid happens at all.

## Checking the model against the kit {#validate}

The validation is only as fair as the benches on the two sides are equal. A difference in the bench reads as a
difference in the model.

- **The same bench, both sides.** The same bias, the same source and load terminations at every harmonic, the same
  frequency and drive plan, the same compression point. **Show testbench** on the Validate step writes each item's
  `.cnl`, and the [Model Validation Report](../reference/model-composer.html#report) names its benches in
  `testbenches/`, so the circuitRF side is on record.
- **The same thermal state.** Validate with self-heating **on** in both simulators, at the kit's own thermal
  resistance. The isothermal data trained the core; the validation tests the model as a designer will use it.
- **A loadpull at compression, not drive by drive.** The Validate step compares each load at the same gain compression
  (3 dB by default), so a model that compresses a little earlier is not marked wrong at every drive level.
- **Loads near the edge of stability.** A load at which the source simulator's drive-up does not converge, or turns
  back on itself, is a statement about that load, not about either model. Leave it out of both sides or run both at a
  source impedance that keeps the input passive.
- **Bias S at the terminals.** Place the exported model and the kit device side by side in one S-parameter bench at a
  few biases. The core was fitted inside the network; this checks the two together.

When a comparison disagrees, the dashed loop in [the flow](#flow) goes back into the composer — a step's settings, a
network value, `Rth` — not back to the source simulator, unless the data itself was short.

## Tables on the kit's own grid {#direct}

Intrinsic-plane tables from a kit lie exactly on the swept Vgs × Vds (× T) grid, and for those the **Table** method
is the route to take. When a constituent's training points form a complete grid, every combination present once, the
fit uses the samples themselves as the table: a tensor-product cubic spline through them, with no network to train
and no re-grid, and no Re-grid setting consulted. The status line says so, e.g. `Id: Table on the data's own
101 × 121 × 5 grid, 10 vgs line(s) held out.` The held-out points are whole grid lines along Vgs, so the training
points stay a grid and the held-out error still measures the table between lines, comparably with the other
methods. The Charge step does the same with capacitances on a grid: each is splined through its own samples and
integrated on that grid.

A grid with a hole, from an excluded point or a sample the source simulator did not converge, is re-gridded instead,
and the status line names the missing sites: `Id: the data is a 101 × 121 grid missing 1 site(s) — (vgs -1, vds 2)
— so it is re-gridded.` Re-run that sample, or accept the re-grid.

**Writing the `.cmodel` by hand** remains the route for a law the composer cannot fit: a `.cmodel` is an open format
([Writing a .cmodel](../reference/cmodel.html)), a [Table constituent](../reference/cmodel.html#table) is a
tensor-product cubic spline on a grid of sites, and the network, the scaling rules and the thermal model go in the
same file. **circuitrf check** reads it back.

The composer remains the place to **check** such a model. `circuitrf model validate kit.cmc --model kit.cmodel`
compares it with a document's data exactly as the Validate step compares a fitted one, and `circuitrf model
check-charge` on a `.cnl` that places it checks its charges.

## The same flow headless {#cli}

Every step is a `circuitrf model` noun, the same functions the window calls ([Model Composer in the
CLI](../reference/cli.html#model)):

```
circuitrf model new kit.cmc --kind active --network gan-two-shell --fingers 4 --unit-width "125 um"
circuitrf model import kit.cmc kit_tables.npz --as SimulatedTables --condition ReferencePlane=intrinsic
circuitrf model fit kit.cmc --step all
circuitrf model check-charge kit.cmc
circuitrf model validate kit.cmc
circuitrf model export kit.cmc -o kit.cmodel
circuitrf model export kit.cmc -o kit.va
```

The network's values and locks are part of the `.cmc`, which is JSON ([File formats](../reference/file-formats.html));
type them in the window's Values table, or write them into the document, before `fit`.

## Checklist {#checklist}

- Self-heating **off** and traps **off** in every fitting run; the temperatures span the junction temperatures the
  circuit reaches.
- The I–V grid runs from below pinch-off into forward gate conduction and past the peak drain swing.
- Y or S at every DC bias, at a low frequency for the charges.
- The plane is stated: `intrinsic` through the kit's internal nodes or its negative network, or `device` with the kit's
  network in the composer.
- The kit's network values are typed and **locked**, each with its scaling rule.
- Currents are positive into the terminal; gate is port 1, drain port 2.
- `N0` and `W0` are the sampled device's.
- The kit's `Rth` and `Cth` are typed on the Thermal step.
- Large-signal runs are imported as **Large signal**, for validation only, and both simulators run the same benches.
