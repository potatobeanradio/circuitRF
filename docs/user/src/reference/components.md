---
title: Components
slug: reference/components.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Components
lede: Every component in the standard library — its symbol, what it does, and its parameters.
keywords: resistor, capacitor, inductor, transmission line, microstrip, MLIN, CPWG, GCPW, coplanar waveguide, SLIN, stripline, via, VIA, VIAGND, part, symbol, library, bead, ferrite, MMIC, MIMCAP, MIM capacitor, SPIRAL, spiral inductor, OSPIRAL, octagonal spiral, TFR, thin-film resistor, AIRBRIDGE, air bridge, CLIN, MCLIN, BCLIN, MBCLIN, MMITER, broadside, coupler, MOD_Source, modulated, LTE, NR, OFDM, pulsed CW, pCW, radar pulse, FSK, GFSK, GMSK, PN9, wireless M-Bus, DBPSK, UNB, LPWAN, CSS, chirp spread spectrum, Bluetooth LE, LoRa, LoRaWAN, Sigfox
---

Symbols are rendered from the live drawing engine, with their connection leads and their pins shown
unconnected — the component as you meet it in the palette, before anything is wired to it. Parameter
tables are read from the component registry, so the defaults, units and on-schematic visibility below
are the as-placed values by construction rather than by transcription.

<div class="callout note">
<span class="label">Reading the tables</span>
<p>The first table is the component's <strong>terminals</strong>: the nets its netlist line takes, in
the order it takes them. That order is what the models themselves read — a diode's anode before its
cathode, a MESFET's gate before its drain — so it is worth checking before hand-writing a
<code>.cnl</code> line; the figure above it shows where those pins sit, not which is which.</p>
<p>Where a terminal has a name it carries it, and where it does not the sentence under the table says
whether the order <em>matters</em> — most two-terminal parts may be wired either way round, and a few
that look symmetric may not. Read that sentence rather than the numbers: a resistor's ends are
interchangeable, an inductor's stop being interchangeable the moment a
<a href="#mutual">Mutual</a> couples it to another, and a taper's two ends are its two widths.</p>
<p>In the parameter table below it, <strong>Name</strong> is the parameter key, as it appears in the editor and in the netlist.
<strong>Default</strong> and <strong>Unit</strong> are the as-placed value. A parameter marked
<em>shown</em> appears as a label on the schematic by default; the rest are available in the parameter
editor. Units accept SI prefixes (<code>pF</code>, <code>nH</code>, <code>GHz</code>,
<code>kΩ</code>).</p>
</div>

## Lumped elements

### Resistor (R) {#resistor}

{{symbol: resistor}}

An ideal, frequency-independent resistor. `R` is the resistance.

{{table: components/Resistor}}

### Inductor (L) {#inductor}

{{symbol: inductor}}

An ideal inductor. `L` is the inductance. Couple two inductors with a [Mutual](#mutual) element.

{{table: components/Inductor}}

### Capacitor (C) {#capacitor}

{{symbol: capacitor}}

An ideal, linear capacitor. `C` is the capacitance. For a voltage-dependent capacitance, see
[NonlinearC](#nonlinearc).

{{table: components/Capacitor}}

### Series RLC (SRLC) {#srlc}

{{symbol: srlc}}

A resistance, an inductance and a capacitance **in series**, as one part. `R`, `L` and `C` are the
three values; the branch impedance is `R + jωL + 1/(jωC)`.

This is the shape a real capacitor takes above a few hundred megahertz, which is the usual reason to
place one: a ceramic capacitor's datasheet gives an ESR and an ESL, and those go straight into `R`
and `L`. The part is series-resonant at `1/(2π√(LC))`, where its impedance falls to `R` — below that
frequency it behaves as a capacitor, above it as an inductor. At DC the series capacitance makes the
branch an open circuit.

Its pins sit exactly where a plain [R](#resistor), [L](#inductor) or [C](#capacitor)'s do, so you can
swap one in for another without moving any wires. Its inductance can be coupled with a
[Mutual](#mutual), which names the SRLC instance in place of an inductor.

{{table: components/Srlc}}

### Parallel RLC (PRLC) {#prlc}

{{symbol: prlc}}

The same three values **in parallel** — a tank. `R`, `L` and `C` all sit across the same two nodes,
giving an admittance of `1/R + jωC + 1/(jωL)`.

At the parallel resonance `1/(2π√(LC))` the reactive parts cancel and the part is purely resistive
at `R`, which is what makes it the natural way to enter a measured resonance: `R` sets the peak
impedance, `L` and `C` set where it sits and how sharp it is. At DC the ideal inductor shorts it out.

Pin positions and [Mutual](#mutual) coupling work exactly as they do for the SRLC above.

{{table: components/Prlc}}

### Series RL (SRL) {#srl}

{{symbol: srl}}

A resistance and an inductance **in series**, as one part: `R + jωL`. A real inductor, whose winding
resistance is the `R` — and, unlike the [SRLC](#srlc) you would otherwise place for the same job,
with no third value on the schematic that nobody meant.

There is no series capacitance, so this branch does **not** open at DC: at ω = 0 it is simply `R`,
which is what the physical part measures. Its inductance can be coupled with a [Mutual](#mutual).

{{table: components/Srl}}

### Series RC (SRC) {#src}

{{symbol: src}}

A resistance and a capacitance **in series**: `R + 1/(jωC)`. A real capacitor entered with its ESR,
or a series RC damper across a resonance. At DC the capacitance makes the branch an open circuit.

It carries no inductor, so a [Mutual](#mutual) naming one is refused rather than coupled — the
refusal says so and names the part.

{{table: components/Src}}

### Series LC (SLC) {#slc}

{{symbol: slc}}

An inductance and a capacitance **in series**: `jωL + 1/(jωC)`. The lossless series trap — its
impedance falls to zero at `1/(2π√(LC))`, which in a shunt arm is a notch. Below the resonance it
behaves as a capacitor and above it as an inductor; at DC it is an open circuit.

Its inductance can be coupled with a [Mutual](#mutual).

{{table: components/Slc}}

### Parallel RL (PRL) {#prl}

{{symbol: prl}}

A resistance and an inductance **in parallel**: an admittance of `1/R + 1/(jωL)`. The shunt form of
a lossy coil, and the usual way a damped choke is entered. At DC the ideal inductor shorts it out.

Its inductance can be coupled with a [Mutual](#mutual).

{{table: components/Prl}}

### Parallel RC (PRC) {#prc}

{{symbol: prc}}

A resistance and a capacitance **in parallel**: an admittance of `1/R + jωC`. A leaky capacitor, or
the shunt RC an equivalent circuit puts across a port. At DC the capacitor is an open and the part
is `R`.

This is the one member of the RLC family that adds no branch current to the matrix — and therefore
the one a [Mutual](#mutual) cannot name.

{{table: components/Prc}}

### Parallel LC (PLC) {#plc}

{{symbol: plc}}

An inductance and a capacitance **in parallel**: an admittance of `jωC + 1/(jωL)`. The lossless
tank — its impedance goes to infinity at `1/(2π√(LC))`. For a tank with a finite peak impedance, use
a [PRLC](#prlc) and let `R` set the peak. At DC the ideal inductor shorts it out.

Its inductance can be coupled with a [Mutual](#mutual).

{{table: components/Plc}}

### Ferrite Bead (Bead) {#bead}

{{symbol: bead}}

A two-terminal **linear** element: `Rdc` in series with a parallel `L` / `Rp` / `Cp` tank.

**A bead is not an inductor and not a series RLC.** The number a data sheet gives — "600 Ω at
100 MHz" — is an *impedance*, and the whole point of the part is that most of it is **resistive** at
the frequency it is quoted at. That is what makes a bead absorb rather than reflect, and why it damps
a supply rail where an inductor of the same reactance would ring against the decoupling capacitance.
An inductor's loss is zero and a series RLC's is a constant `R`; a bead's rises from nothing at DC to
a maximum at its ferromagnetic resonance and falls again above it.

Each element stands for a real mechanism:

| Parameter | What it is |
|---|---|
| `Rdc` | The winding's own resistance. It is what the part looks like at DC, and in a power rail it is what sets the drop. |
| `L` | The low-frequency inductance — what the impedance rises along. |
| `Rp` | The **core loss**. It *caps* the impedance: at the parallel resonance the reactive branches cancel and \|Z\| is `Rdc + Rp`, which is the peak a data sheet plots. Nothing else sets that peak, so a bead entered without it has no maximum at all. |
| `Cp` | The parallel (inter-turn) capacitance — what takes the impedance back **down** above resonance. A bead is not a filter above its own resonance, and this is why. |

**Zero means *not modelled* for each of the three parallel elements**, never "a short" or "a zero-ohm
resistor". `Rp = 0` removes the loss branch and leaves an ideal `L`; `Cp = 0` removes the capacitive
branch and the impedance goes on rising; `L = 0` leaves a plain `Rdc`.

**At DC the bead is `Rdc` and nothing else**, because the inductive branch shorts the tank out. That
is both the physics and what a DC operating point needs from this part — a bead in a supply rail must
not open it.

<div class="callout warn">
<span class="label">Saturation is not modelled and cannot be</span>
<p>A bead's inductance falls with DC bias current, sometimes by most of it, and this is a
<b>linear</b> element. The four numbers describe the part at whatever current they were measured at,
which is why a bead chosen from a small-signal impedance curve can behave quite differently in the
rail it was chosen for.</p>
</div>

{{table: components/Bead}}

### Nonlinear Capacitor (NonlinearC) {#nonlinearc}

{{symbol: nonlinear-c}}

A capacitor whose capacitance varies with voltage, C(V), modelled as a polynomial (Taylor) series.
Enter the coefficients directly, or generate them from a C–V curve with the C–V Editor. `C0` is the
constant term; `C1`, `C2`, … are the higher-order coefficients, added in the editor or generated by
the C–V Editor. Full treatment:
[The Nonlinear Capacitor & the C–V Editor](nonlinear-capacitor.html).

{{table: components/NonlinearC}}

### Mutual Inductance (M) {#mutual}

{{symbol: mutual}}

Couples two existing inductors — named by instance — with a mutual inductance, the basis for
transformers and coupled resonators. It has no pins of its own; `Inductor1` and `Inductor2` are the
instance names of the two coupled inductors and `M` is the mutual inductance between them.

Either name may be any part that carries an inductor: an [L](#inductor), an [SRLC](#srlc), a
[PRLC](#prlc), an [SRL](#srl), an [SLC](#slc), a [PRL](#prl) or a [PLC](#plc) — every one of them
holds its inductor current on a branch the coupling can act on, and every one spells its value `L`.
Naming anything else — an [SRC](#src) or a [PRC](#prc) included, since neither has an inductor — is
reported as an error when the design is elaborated.

{{table: components/Mutual}}

### Ideal Transformer (Transformer) {#transformer}

{{symbol: transformer}}

A lossless transformer with turns ratio `N`:1, primary to secondary:

`V(p+) − V(p−) = N · (V(s+) − V(s−))`, and the secondary current is `N` times the primary's, flowing
the other way.

Four pins on the corners — the primary `p+`, `p−` on the left, the secondary `s+`, `s−` on the right —
with the dots on `p+` and `s+`: a voltage rising at one dotted end rises at the other. Seen from the
primary, a load on the secondary looks `N²` times larger, so `N=2` between 50 Ω ports matches a
200 Ω source to a 50 Ω load.

Being ideal, it has no winding inductance and no leakage: it passes DC as readily as RF, at every
frequency, harmonic balance included. For a transformer whose windings have inductance — one that
blocks DC and rolls off at low frequency — use two [inductors](#inductor) and a [Mutual](#mutual).

{{table: components/Transformer}}

### Matching Network (Match) {#match}

{{symbol: match}}

A synthesised bandpass matching network placed as a single two-pin component, ground being the common
return. Its whole design rides in a hidden `Design` parameter, so what it stamps is a property of the
design rather than of the component type: the ladder it contains is the synthesised one **minus**
whatever the two external terminations already supply, since absorbing those reactances is the point.
Edit it in the Match Designer.

<div class="callout note">
<span class="label">Match has its own chapter</span>
<p>The synthesis, the Designer's four panes, the Norton-transform rack, the Probe button and a worked
two-stage interstage example are all in <b><a href="match.html">The Match Component</a></b>. Read that
before placing one — this entry is only the component's parameter table.</p>
</div>

{{table: components/Match}}

## Sources

### DC Voltage Source (Vdc) {#vdc}

{{symbol: vdc}}

A fixed DC voltage — gate and drain bias supplies. `Vdc` is the voltage.

{{table: components/Vdc}}

### Tone Source (VTone) {#tonesource}

{{symbol: tone-source}}

A single-tone (sinusoidal) voltage source for harmonic balance, with an optional DC offset. `V` is the
tone amplitude, `Freq` the tone frequency, and `Vdc` a DC offset.

{{table: components/ToneSource}}

### Current Tone Source (ITone) {#currenttonesource}

{{symbol: current-tone-source}}

The current-source dual of [VTone](#tonesource) — a single-tone sinusoidal **current** source with an
optional DC offset. `I` is the tone amplitude, `Freq` the tone frequency, and `Idc` a DC offset. Use
it to drive a node with a known current rather than a known voltage: an ideal current source has
infinite output impedance, so it sets the current through a branch and lets the network decide the
voltage.

**The arrow says which way the current goes.** A positive `I` **delivers** current into pin 1 (the
arrowhead end, the top pin as drawn) and draws it out of pin 2. Note this is the opposite of the
SPICE `I` element, which sinks current from its first node; circuitRF uses one direction convention
for every source it has, and the glyph states it so you never have to remember which.

Like VTone, the **+** button in the parameter editor turns it into a multi-tone source: the scalar
`I`/`Freq` migrate to `I[1]`/`Freq[1]` and each further tone gets its own `Freq[n]`, `I[n]` and
`Phase[n]`.

<div class="callout note">
<span class="label">An ideal current source is an open circuit</span>
<p>It contributes no conductance at all, and none whatsoever at frequencies it is not exciting. A node
driven <b>only</b> by a current source therefore has no DC path to ground and the matrix is singular —
that is the physics of the element, not a defect. Give the node a resistor, a termination or a bias
path and it solves.</p>
</div>

{{table: components/CurrentToneSource}}

### RF Power Source (P1Tone) {#p1tone}

{{symbol: p1tone}}

An RF source specified by **available power** with an internal source impedance — the natural drive
for power-amplifier and loadpull work. It can also serve as an S-parameter port (via `Num`), and
accepts per-harmonic source terminations `Z[k]` for harmonic source-pull.

- `Num` — S-parameter port index, sharing one pool with Term.
- `Pavl` — available power.
- `Z` — fundamental source impedance; `Freq` — fundamental frequency; `Phase` — phase.
- `Z[k]` — per-harmonic source termination: `Z[0]` is baseband/DC, `Z[2]` the second harmonic, and so
  on. Added in the parameter editor.

{{table: components/P1Tone}}

### Multi-Tone RF Power Source (PnTone) {#pntone}

{{symbol: pntone}}

The multi-tone sibling of [P1Tone](#p1tone) — an available-power RF source that injects **several
carriers at once from a single component**, the natural drive for two-tone intermodulation work. A
freshly placed PnTone is already a **two-tone source** (tones 1 and 2, at 1.99 and 2.01 GHz); the
**+** and **−** buttons in the editor add and remove tones. Each tone has its own `Freq[i]`,
`Pavl[i]` and `Phase[i]`; all tones share one source impedance `Z` and the same per-band terminations
`Z[k]`, where each mixing product is terminated by the band its frequency falls in.

Unlike P1Tone, PnTone is *not* an S-parameter port — it has no `Num`. The tones a PnTone drives must
match the tones declared on the [Harmonic Balance analysis](simulations.html#two-tone): the analysis
owns the mixing grid, and the source just supplies power at those frequencies.

{{table: components/PnTone}}

### Modulated RF Source (MOD_Source) {#modsource}

{{symbol: modsource}}

A **modulated** RF source — LTE, NR, CW, a pulsed-CW train, FSK (wireless M-Bus, Bluetooth LE), DBPSK (Sigfox), chirp spread spectrum (LoRa) or an imported I/Q waveform — for the
[Envelope analysis](simulations.html#envelope): ACLR/ACPR, EVM-style error, CCDF, AM-AM/AM-PM and
two-carrier intermodulation with real signals rather than tones. One MOD_Source holds any number of
**carriers** around its centre frequency `Fc`; `Pavg` is the total average available power behind the
source impedance `Z`.

- `Carrier[i]` — one carrier, written as a description string, e.g.
  `"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-200MHz;P=0;Seed=1"`: the standard (`LTE`, `NR`, `CW`, `FILE`),
  channel bandwidth, NR subcarrier spacing, modulation, offset from `Fc`, relative power (dB) and the
  random-data seed. `BW`, `SCS` and `Off` take `Hz`, `kHz`, `MHz` or `GHz`; a bare number is in Hz. A
  `FILE` carrier plays a two-column I/Q text file, `File=` its path and `Fs=` its sample rate (taken from
  the file's `# sample_rate_hz` header when absent). A `PCW` carrier is a pulse train,
  `"PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"`: the pulse repetition interval,
  the width (50 % to 50 %), the 10–90 % edge times and the off-time before the rising edge (centred in
  the PRI when absent). Its time fields take `s`, `ms`, `us` (or `µs`) or `ns`; a bare number is in
  seconds. A `PCW` carrier must be the source's only carrier, and **`Pavg` is then the pulse-top power**,
  not the record average: see [Pulsed CW](signal-designer.html#pulse). An `FSK` carrier is binary
  continuous-phase FSK, `"FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"`: the bit rate (`bps`,
  `kbps` or `Mbps`), the peak deviation, the Gaussian filter's BT (absent: no filter) and the data — `PN9`,
  `PN15` or a fixed pattern such as `10110100`, one period of which is the record. It too must be the
  source's only carrier: see [FSK](signal-designer.html#fsk). A `DBPSK` carrier, `"DBPSK;Rb=100bps;Data=PN9;Off=0;P=0"`,
  takes the same `Rb` and `Data` (the Sigfox uplink: [DBPSK](signal-designer.html#dbpsk)), and a `CSS` carrier,
  `"CSS;SF=7;BW=125kHz;Off=0;P=0"`, a spreading factor and a chirp bandwidth (LoRa: [CSS](signal-designer.html#css));
  each must be the source's only carrier.
- `Record` — periodic record length (s), whole 0.5 ms slots. `Clip` — target PAPR (dB) for
  clip-and-filter; absent means **unclipped** (the natural OFDM PAPR, about 11–12 dB).
  `Shaping` — out-of-channel spectral floor (dBc), or `off`.

Select the source and press **Design signal…** in the inspector to open the
[Signal Designer](signal-designer.html): the carriers and presets, or a custom I/Q file of your own, with
the spectrum, the CCDF and the EVM of the signal before you simulate it.

In a harmonic-balance or loadpull run a MOD_Source is a CW tone at `Fc` carrying `Pavg` — the signal's
average power, or for a pulsed-CW source the pulse-top power, the steady state of a pulse that never ends;
in an S-parameter run it is the passive impedance `Z`.

{{table: components/ModSource}}

### Voltage-Controlled Current Source (VCCS) {#vccs}

{{symbol: vccs}}

An ideal transconductance: the current it delivers is `G` times the voltage across a separate,
purely-sensing control pair.

`I = G · (V(ctrl+) − V(ctrl−))`

Four terminals in two pairs — the **output** pair (`out+` top, `out−` bottom) carries the current, and
the **control** pair (`ctrl+`, `ctrl−`, on the left) senses the controlling voltage. The control pair
draws **no current at all**, which is what makes the source ideal and what makes the device
unilateral: nothing travels backwards through it. `G` is the transconductance, in siemens.

**The arrow says which way the current goes**, and it points **down**: a positive `G` and a positive
control voltage draw current in at `out+` and out at `out−`. That is the SPICE `G` element's own
direction, and the way a small-signal transconductance is drawn in a device model — the controlled
source sinks drain current from the drain node. So a VCCS across a grounded load resistor is
**inverting**, and a 50 Ω, `G` = 10 mS stage measures S21 = −0.25.

Note the VCCS's arrow points the **opposite** way to [ITone](#currenttonesource)'s. That is not an
inconsistency to squint past: ITone is an independent source and *delivers* current to its arrow pin,
while the VCCS is a controlled source drawn the way controlled sources are drawn. Read each symbol's
own arrow.

<div class="callout note">
<span class="label">It works in every analysis, harmonic balance included</span>
<p>The VCCS is a <b>linear</b> device, so it is stamped into the matrix at every frequency the
simulator solves at: DC, S-parameters, and <b>every retained harmonic of a harmonic-balance run</b>,
plus everything built on those (parametric sweeps, loadpull, loadpull-pursuit). In HB it lives in the
linear partition alongside the resistors and lines, so <code>G</code> is the same number at every
harmonic — an ideal transconductance has no frequency dependence, no compression, and no delay. If you
need any of those, the device you want is an <a href="sdd.html">SDD</a>, whose equations can state
them. The same is true of <a href="#currenttonesource">ITone</a>.</p>
</div>

{{table: components/Vccs}}

### Voltage-Controlled Voltage Source (VCVS) {#vcvs}

{{symbol: vcvs}}

An ideal voltage gain: the voltage it holds across its output pair is `E` times the voltage across a
separate, purely-sensing control pair.

`V(out+) − V(out−) = E · (V(ctrl+) − V(ctrl−))`

Four terminals in two pairs, laid out exactly as the [VCCS](#vccs)'s are — the **output** pair
(`out+` top, `out−` bottom) and the **control** pair (`ctrl+`, `ctrl−`, on the left), which draws
**no current at all**. `E` is a voltage ratio and carries no unit.

The two tiles differ in one thing: what the diamond carries. The VCCS's arrow points at the current
it delivers; the VCVS has a **± pair** down its axis instead, because there is no current direction
to point at. A VCVS states a *potential difference* and whatever current the rest of the circuit
draws through it flows — including none, if nothing is connected across the output pair. That is the
ideal source's own behaviour, not a limitation.

Being ideal, its output impedance is **zero**: it holds `E` times the control voltage no matter what
it is loaded with. Put a series resistor after it if you want a source with an impedance.

<div class="callout note">
<span class="label">It works in every analysis, harmonic balance included</span>
<p>Like the VCCS, the VCVS is a <b>linear</b> device stamped at every frequency the simulator solves
at, harmonic balance included, so <code>E</code> is the same number at every harmonic. It carries a
branch-current unknown of its own where the VCCS carries none — a relation <i>between</i> node
voltages is not something admittances can express — but nothing about that is visible from outside.
For a gain that varies with frequency, drive or bias, the device you want is an
<a href="sdd.html">SDD</a>.</p>
</div>

<div class="callout note">
<span class="label">An ideal current source is an open circuit</span>
<p>The output pair gets no conductance of its own, so — exactly as with ITone — an output node with
nothing else attached to it has no DC path to ground and the matrix is singular. Load it.</p>
</div>

{{table: components/Vcvs}}

### Operational Amplifier (OpAmp) {#opamp}

{{symbol: opamp}}

A triangle with the inverting input `in−` above the non-inverting input `in+`, and the output `out`
at its point, referenced to ground.

With `A=0`, the default, it is **ideal** — infinite open-loop gain. Its inputs draw no current and are
held at the same voltage, and the output delivers whatever current that takes, so the feedback network
alone sets the gain: an inverting stage with `Rin` and `Rf` has a gain of exactly `−Rf/Rin`.

A non-zero `A` makes it a finite-gain amplifier, `V(out) = A · (V(in+) − V(in−))`, with an output
resistance `Rout` behind it. As `A` grows, the answers converge on the ideal ones. `Rout` acts only
with a finite `A`.

It is linear and the same at every frequency: there is no bandwidth, slew rate or output swing limit,
so an op-amp left without negative feedback has an output nothing bounds. For a gain that rolls off,
put the roll-off in the circuit around it.

{{table: components/OpAmp}}

### Operational Amplifier with supply pins (OpAmp5) {#opamp5}

{{symbol: opamp5}}

The same op-amp with its supply pins drawn, `V+` above and `V−` below, for schematics that wire the
supplies. They carry no current and change nothing: the op-amp behaves exactly as the three-pin tile
does. A supply pin left connected to nothing is a node nothing sets, so connect both, or place the
three-pin [OpAmp](#opamp) instead.

The supply pins are cosmetic only.

{{table: components/OpAmp5}}

## Terminals & ports

### Ground (GND) {#ground}

{{symbol: ground}}

The global reference node (net `0`). No parameters.

### Term {#term}

{{symbol: term}}

An S-parameter port: a numbered, reference-impedance termination where the simulator excites and
measures. `Num` is the port index, auto-assigned at placement; `Z` is the reference impedance. See
[Pins, Ports & Terms](pins-ports-terms.html) for how it differs from a Pin.

**Outside S-parameters a Term is a load of `Z`** to its reference: its resistance (the real part of `Z`)
in a DC run, and `Z` at every harmonic in a harmonic-balance or loadpull run. A bias or a current returning
through it flows, as it would through the instrument it stands for. Put a DC block in front of a Term on a
biased node, exactly as on a bench. A Term inside a cell is ignored in every analysis.

{{table: components/Term}}

### Grounded Term (TermG) {#termg}

{{symbol: termg}}

A [Term](#term) whose second port is permanently grounded, presenting as a one-port. It is a packaging
convenience rather than a parallel model: it uses Term's own engine component and glyph exactly, with
the ground symbol drawn at Term's port-2 location, so a schematic that swaps Term + GND for a TermG is
electrically identical.

{{table: components/TermG}}

### Pin {#pin}

{{symbol: pin}}

A cell's interface terminal — connectivity only, no electrical model. Pins on a cell's symbol are how
the cell connects to the parent that instances it. `Num` is the interface port index, auto-assigned;
`Name` is an optional label, and extraction uses `P{Num}` when it is blank. An optional `Polarity` of
`Plus` or `Minus`, set in the editor, forms a differential pair sharing one `Num`.

{{table: components/Pin}}

### Current Probe (IProbe) {#iprobe}

{{symbol: iprobe}}

A 0 V series ammeter placed in a branch to read its current. Its instance name — `Iout`, say — is how
measurements reference that current: `I("Iout", 1)`. No parameters.

{{table: components/IProbe}}

### Voltage Probe (VProbe) {#vprobe}

{{symbol: vprobe}}

A one-terminal voltage probe. Drop it anywhere on a wire and that net is reported under the probe's
instance name — `Vout`, say. That name is what the trace picker lists and what a measurement
expression reads: `V("Vout")`.

It is a label, not a part. It has one terminal, so it cannot bridge two nets; the elaborator builds no
model for it, so it stamps nothing; and a design simulates identically with every VProbe deleted.
Placing one can never change an answer — it can only add a name to it.

**On an unnamed wire the probe's name becomes the net's name.** There is one row in the results, called
`Vout`, and no `n7` beside it — the automatic name was never a name anyone chose, and two rows for one
point in the circuit is one row too many.

**On a wire you have already labelled, both names appear.** The label is not overwritten: a wire
labelled `n_drain` with a `Vout` probe on it shows up twice — as `n_drain` and as `Vout` — carrying the
same voltage. The probe is an *alias* there, and either name plots and measures the same node.

Two rules keep the names unambiguous, and breaking either refuses the run rather than reporting:

- Two VProbes may not share a name.
- A VProbe may not share a name with a net.

A VProbe that touches nothing is reported in the Messages panel and drops out of the results, rather
than reporting a plausible number for a point you never probed. So is one placed on ground, which is
0 V by definition.

**It is the one component you can drag off a wire.** Every other symbol takes its wires with it when
you move it, because moving the picture must never re-wire the circuit. A probe has nothing to
re-wire, so dragging it leaves the drawing exactly as it was and puts the probe wherever you dropped
it — pick it up, look somewhere else, run again, with no repair work in between. Rotating it behaves
the same way, and a wire dragged out from under a probe simply leaves it behind.

Select the probe **and** its wire together and they move as one, so the probe stays on the net.

{{table: components/VProbe}}

### Stability Probe (WSProbe) {#wsprobe}

{{symbol: wsprobe}}

A stability probe, placed *in* a node so that it splits the node into a generator-side terminal (`G`)
and a load-side terminal (`L`). It implements T. A. Winslow, *General Circuit Analysis Using The
WSProbe* (2023); every quantity below is that document's, under its own name.

Electrically it is an [IProbe](#iprobe) — a 0 V short — and it is placed the same way: drop it onto
the wire whose node you want to look at, and circuitRF deletes the stretch of wire between its two
pins so the probe breaks into the run instead of being shorted by it. It perturbs nothing, so a
design simulates identically with every WSProbe deleted. It takes no parameters.

**The letters are the orientation.** `G` is the generator-side terminal and `L` the load-side one:
`ZG` looks out of `G`, `ZL` out of `L`. Rotating or mirroring the symbol moves the drawing and never
the pin order, so swapping the two means swapping the wires — which swaps every `G`/`L`-labelled
output and negates nothing else.

**What it produces is not a number but a matrix.** An S-parameter run with any WSProbe in it writes a
`wsp` cube — 2N x 2N for N probes — and six named outputs per probe: `H0` and `Y0`, the driving-point
impedance and admittance; `ZG` and `ZL`, the bidirectional impedances; `LG`, the bilateral loop gain;
and `F = 1 - LG`. Two stability margins, `SM_Y0` and `SM_H0`, come with them. Everything else the
document derives — every other loop gain, the reduced two-port at the probe, the probe-pair blocks,
the global loop gains over a set of probes — is post-processing of that one matrix, and is available
in the Data Display's trace card and in a `measure` line.

**Reading a node for stability is two plots, not one.** Plot `1/H0` and `1/Y0` on a polar chart and
look for a clockwise crossing of the negative real axis; the trace card reports the frequencies it
finds, or `none`. Both must be checked, because a zero can hide the pole in one of them but never in
both — a series resonance shows in `1/Y0` alone and a parallel one in `1/H0` alone.

**The stability margin is a distance, and the search is the detector.** `SM_Y0` and `SM_H0` are
unitless numbers in `[0, 1]`, shown in dB (`20*log10`), and the trace card prints each one's minimum
beside the crossing search that pairs with it. A node with positive resistance on both sides can never
read below **-12 dB**, so anything under that line certifies negative resistance on one side; the run
reports any probe whose margin falls below the analysis' `MarginThreshold` (default -15 dB) as worth
looking at. That report is a note, not a warning: a low margin is somewhere to look, not a failure.

**[The WSProbe has its own chapter](wsprobe.html)** — every quantity with its equation, the probe
pairs, the global loop gains, the stability envelope, the probe under harmonic balance, the NDF, and
an appendix on stability analysis itself.

{{table: components/WSProbe}}

### Tuner / SourceTuner / LoadTuner {#tuner}

<div class="symbol-row">
{{symbol: tuner}}
{{symbol: source-tuner}}
{{symbol: load-tuner}}
</div>

A programmable RF termination. The three variants are the *same* engine component with different
glyphs and net ordering — match the symbol to its role. **SourceTuner** sits on the source side,
**LoadTuner** on the load side.

- `Z[1]` — the fundamental termination. Required, and it accepts complex literals such as `50+j*10`.
  Add `Z[2]`, … in the editor, or `G[k]` to give a reflection coefficient instead.
- `Zdefault` — the catch-all termination for harmonics not otherwise specified, and the impedance the
  tuner presents at DC.
- `Z0` — the reference impedance for any `G[k]` entry.
- `BiasTee` — `on` or `off`, chosen from a picker. `on` builds the internal bias-tee and DC supply,
  which the loadpull directive requires. With it `off` the tuner is RF-only: there is no DC path
  through it to the reference node.
- `Vbias` — the DC bias at the DUT-facing port when the bias-tee is on.
- `ShowBias` — `true` or `false`, chosen from a picker. Display only: it draws the bias-tee on the
  glyph and never reaches the engine, so the extracted netlist is identical either way.

{{table: components/Tuner}}

#### Which analyses a tuner works in {#tuner-analyses}

All five that terminate a circuit: {{anchor: simulations#dc|DC}}, {{anchor: simulations#s-parameters|S-parameters}},
{{anchor: simulations#harmonic-balance|harmonic balance}}, {{anchor: simulations#loadpull|loadpull}} and
{{anchor: simulations#loadpull-pursuit|loadpull pursuit}}. It is an ordinary linear component, so a
parametric sweep wrapping any of them sweeps it like anything else.

#### How its frequency response is defined {#tuner-frequency}

**A tuner is not a fitted network.** It presents the impedance you declared, exactly, with no
interpolation between entries and no roll-off. Which entry it presents depends on the analysis:

| Analysis | What the tuner presents |
|---|---|
| DC | `Zdefault` — but behind the ideal DC-block capacitor, so with `BiasTee=on` the DC seen at the pin is the supply through the choke, and with `BiasTee=off` the pin is open at DC. |
| S-parameters | **`Z[1]`, flat across the whole frequency sweep.** There is no harmonic structure outside harmonic balance, so the declared fundamental is the only value in play — `Z[2]`, `Z[3]`, … are ignored. |
| Harmonic balance | `Z[k]` at harmonic *k*, where *k* is the analysis fundamental's *k*-th multiple. Anything not declared falls back to `Zdefault`, as does any frequency that is not on the harmonic grid. |
| Loadpull / pursuit | The same per-harmonic rule, except that the harmonic being tuned takes its value from the grid point under test rather than from `Z[k]`. |

So the answer to "what does `Z[2]` do in an S-parameter run?" is *nothing*: declare the band you care
about in `Z[1]`, and use several S-parameter analyses if you want several terminations.

#### In loadpull and pursuit, the SourceTuner is also the RF generator {#tuner-drive}

A loadpull run needs a drive as well as terminations, and the **SourceTuner supplies it**: the
loadpull and pursuit engines assign it the source role, which stamps a single-tone RF generator inside
it, behind the impedance it presents. The generator is calibrated to *available* power —
|V<sub>s</sub>| = √(8·P<sub>avl</sub>·Re Z<sub>1</sub>) — so the power you ask for and the impedance the
DUT is driven from stay consistent as either one moves across the sweep.

**The drive level is not a tuner parameter.** There is no `Pavl` row on the component, because the
sweep over drive belongs to the run, not to the part. The ladder is set in the analysis: **Pin start
(dBm)**, **Pin max (dBm)** and **Pin step (dB)** in the Loadpull and Loadpull Pursuit setup, walked
upward from the start until compression is reached or the cap stops it.

<div class="callout note">
<span class="label">Outside loadpull, a SourceTuner does not drive</span>
<p>The source role is assigned by the loadpull and pursuit engines only. Place a SourceTuner on a plain
harmonic-balance testbench and it behaves as a passive termination like the other two glyphs — put a
{{anchor: components#p1tone|P1Tone}} or a tone source on the bench for the drive.</p>
</div>

## Transmission lines

### Ideal Transmission Line (TLIN) {#tline}

{{symbol: tline}}

An ideal transmission line of characteristic impedance `Z`, stated one of two ways:

- **Electrical** (the default): electrical length `E` at a reference frequency `F`. Optional loss `A`
  is the total in dB at `F`, scaling in proportion to frequency.
- **Physical**: length `L` with effective permittivity `Eeff` (default 1, an air line), so the
  electrical length is θ(f) = 2π·f·L·√Eeff / c₀ with no reference frequency needed. Loss is per unit
  length: `Ac` (conductor, scaling as √f) and `Ad` (dielectric, scaling as f), both in dB/m at `F` and
  both 0 by default.

A line states `E` or `L`, never both; `Eeff` below 1, `A` together with `L`, and `Ac` or `Ad` without
`F` are refused. In the parameters dialog, **Use physical length** / **Use electrical length** switches
form, converting at `F` (or at 1 GHz when there is none) so the line does not change; the new `L` is
written in the technology's length unit. For a physical-form line, `circuitrf explain` reports the
electrical length it resolves to at `F`.

```
TLIN:TL1 a b Z=50 Ohm L=25 mm Eeff=3.2 Ac=4 Ad=2 F=2 GHz
```

{{table: components/Tline}}

### Coupled Line (CLIN) {#cline}

{{symbol: cline}}

An ideal, lossless pair of coupled transmission lines over a common ground, specified by its even-mode
impedance `Ze`, odd-mode impedance `Zo` and electrical length `E` at a reference frequency `F`. Like
TLIN it has no substrate and no layout; it is the four-port to reach for when designing a coupler or a
coupled-line filter section before committing to a geometry.

- **Match.** All four ports are matched to `Z0` when `Ze · Zo = Z0²`; the default 69.37 Ω / 36.04 Ω
  pair is matched to 50 Ω.
- **Coupling.** At a quarter wave (`E = 90°` at `F`) the coupled-port voltage is
  `(Ze − Zo) / (Ze + Zo)` — about −10 dB for the defaults.
- **Inhomogeneous media.** `Eo` is the odd mode's electrical length. Left blank, both modes share
  `E`, which is the homogeneous (stripline) case with ideal isolation. Set it shorter than `E` to model
  a medium such as microstrip, where the odd mode travels faster and the directivity is finite.

{{table: components/CLine}}

### Broadside-Coupled Stripline (BCLIN) {#bcline}

{{symbol: bcline}}

A pair of strips on two adjacent conductor layers, one above the other, between two ground planes in
one dielectric — the offset broadside-coupled stripline used for tight (3 dB and closer) couplers. Its
pins are CLIN's: 1 and 2 are strip A (the upper one), 3 and 4 strip B, with 3 beside 1.

- **Geometry.** `W` is the strip width (both strips), `O` the lateral offset between their centres
  (0 puts one directly over the other) and `L` the length. The cross-section — `Hs` between the strips'
  facing surfaces, `Ha` from strip A up to its ground, `Hb` from strip B down to its ground, the
  thickness `T` and `Er` — starts **empty**, which means *from the stackup*: `LayerA` (default: the first
  signal conductor with another signal conductor directly beneath it and a ground plane above and below)
  and `LayerB` (the next conductor down) are resolved on the technology, every layer between two surfaces
  counting toward their spacing. Typing a value into one of those rows overrides the stackup for that row
  only; with no technology the run says which rows take their standalone defaults.
- **Physics.** The cross-section is solved by circuitRF's own 2D quasi-static field solver (finite
  differences on a graded grid, thick strips, the two grounds), once per geometry. Both modes travel at
  c/√εr; the even- and odd-mode impedances follow from the capacitance matrix. An asymmetric stack
  (`Ha` ≠ `Hb`, unequal thicknesses) is described by the symmetric average of the two strips, and the run
  says so when they differ by more than 5 %.
- **Loss.** Conductor loss is integrated over the solved surface-charge distribution (both strips and
  both grounds), dielectric loss is exact for the homogeneous medium.

{{table: components/BCLine}}

### Microstrip Line (MLIN) {#mlin}

{{symbol: mlin}}

A physical microstrip line on the current technology's substrate: `W` is the conductor width and `L`
the length. Unlike TLIN, its impedance and loss follow from the geometry and the substrate rather than
being stated. The parameters dialog shows the **Z0** (and εeff) the current `W` gives on the resolved
substrate; type a Z0 there instead and `W` is set to the width that gives it, as one undoable edit.

The length can be stated electrically instead, as `E` at `F` in place of `L`, exactly as a TLIN's is:
**Use electrical length** in the parameters dialog converts the current `L` at the line's own εeff, and
**Use physical length** converts it back, so switching never changes the line a run simulates. CPWG and
SLIN have the same button. Layout always draws the physical length.

{{table: components/Mlin}}

### Grounded Coplanar Waveguide (CPWG) {#cpwg}

{{symbol: cpwg}}

A coplanar waveguide over a ground plane: a strip of width `W` with ground copper on the same layer a
gap `G` from each edge, and a plane under the substrate. The substrate and the backing plane come from
the technology, as an MLIN's do; the gap is the line's own. When the coplanar ground is far from the
strip the line is a microstrip, and CPWG gives the same answer an MLIN would. The parameters dialog shows
the **Z0** and εeff at the top frequency of the schematic's S-parameter sweep, and a Z0 typed there sets
`W`. Against a 2-D field solve its Z0 is within about 2 % over the gaps and widths PCB and MMIC lines
use. **Update Layout from Schematic** draws the strip, and a ground strip as wide as the strip on each side
of it; the vias that tie those grounds to the plane are yours to add.

{{table: components/Cpwg}}

### Stripline (SLIN) {#slin}

{{symbol: slin}}

A strip of width `W` buried between two ground planes, centred or offset. The planes are the nearest
ground-designated conductors above and below `SignalLayer`, and the dielectric between them sets the two
heights, εr and the loss — when that dielectric is several layers of different εr, the line uses their
thickness-weighted mean and says so. A layer without a plane on each side cannot carry a stripline, and
the line is refused rather than simulated as something else. The parameters dialog shows its **Z0**; a
Z0 typed there sets `W`. **Update Layout from Schematic** draws the strip on `SignalLayer`.

{{table: components/Slin}}

### Swapping one line type for another {#swap-line-type}

Right-click a TLIN, MLIN, CPWG or SLIN and choose **Swap Line Type** to turn it into one of the others in
place, or choose the type from the type field at the top of its parameters dialog. With several lines
selected, the context menu swaps all of them as one undo step; a line that cannot be swapped is named in
**Messages** and the rest still swap.

The instance name, its wiring and its position stay the same, and so do `W` and `L` &mdash; the impedance
follows from the new line type, and the parameters dialog shows it.

- **To CPWG**, the gap is the one measured off the artwork when the line came from
  [Create Schematic from Artwork](artwork-to-schematic.html); otherwise the line's earlier `G`, if it had one;
  otherwise you are asked for it.
- **To TLIN**, `Z` and `Eeff` are what the old line has at the top frequency of the schematic's S-parameter
  sweep, with its loss as `Ac` and `Ad`; the TLIN is in its physical form.
- **From TLIN**, `W` is the line's earlier width when it had one, and otherwise the width that gives `Z` at
  that frequency (Messages says when it was worked out this way). An electrical-form TLIN's `E` becomes a
  length.
- **To SLIN**, the signal layer needs a plane above and below it; on any other layer the swap is refused and
  the line is left as it was.

Nothing is lost: a parameter the new type does not have (CPWG's `G`, a TLIN's `Z`, a layer choice) is kept
with the component, so swapping straight back gives the original line exactly. Each type takes its substrate
from the technology. An MBEND, MTEE, MCROSS or MTAPER next to a swapped line stays a microstrip
discontinuity, and Messages says so.

### Coupled Microstrip Lines (MCLIN) {#mcline}

{{symbol: mcline}}

A symmetric pair of edge-coupled microstrip lines on the technology's substrate: strip width `W`,
edge-to-edge gap `S` and length `L`. Its pins are CLIN's (1/2 line A, 3/4 line B, 3 beside 1), so a CLIN
in a schematic can be replaced by one without rewiring.

The even- and odd-mode impedances and effective permittivities come from the Kirschning–Jansen
coupled-microstrip equations, with their frequency dispersion and a correction for the strips'
thickness; the two modes keep their own εeff, so the odd mode is faster and the isolation of a coupler
is finite, as on a real board. Conductor, roughness and dielectric loss are MLIN's, per mode. The
published range is W/h and S/h from 0.1 to 10 and εr up to 18; outside it the result is still computed
and the run says it is an extrapolation. On placement `W` starts at the workspace's 50 Ω width and `S`
at the same value.

{{table: components/MCLine}}

### Broadside-Coupled Microstrip (MBCLIN) {#mbcline}

{{symbol: mbcline}}

BCLIN's broadside pair without the upper ground: strip B is buried in the dielectric `Hb` above the
ground plane, strip A sits `Hs` above it — on the top surface by default (`Hd` empty), or buried too when
`Hd` or the stackup puts dielectric over it — and there is air above. Its pins are CLIN's.

Because the medium is not uniform, the field solver runs twice — with the dielectric and with air — and
each mode gets its own impedance and its own εeff; as with MCLIN the modes travel at different speeds,
which limits the directivity. The two strips are never symmetric here (they sit at different heights),
so the pair is always described by the symmetric average of the two, and the run says how far apart
they are. The cross-section rows and `LayerA`/`LayerB` follow the stackup exactly as BCLIN's do, with the
one ground plane below strip B.

{{table: components/MBCLine}}

### Microstrip Bend (MBEND) {#mbend}

{{symbol: mbend}}

A right-angle microstrip bend of width `W`, optionally mitred, modelled with its discontinuity rather
than as an ideal corner.

{{table: components/MBend}}

### Microstrip Curve (MCURVE) {#mcurve}

{{symbol: mcurve}}

A radiused (curved) microstrip bend: width `W`, turn `Angle` (counter-clockwise positive, as MBEND's)
and `Radius`, the **centre-line** radius. Where MBEND is a corner with a discontinuity, a curve has no
corner — it is modelled as the line it nearly is, an MLIN of the same width over the effective length
(R − 0.2·W)·|Angle|. The electrical path is a little shorter than the drawn centre line because the
current favours the inner edge.

That rule is published for Radius between 3W and 7W. Outside it the result is still computed and the
run says it is an extrapolation; below 2W a bend is more than a change of length and the run tells you
to EM-simulate it. A radius below W/2 cannot be drawn and is refused. The reference planes are the two
ends of the arc, so an MCURVE butts straight onto an MLIN with no overlap to subtract. In layout it is
drawn with true arc edges, which EM, DRC and the exporters flatten to the technology's own tolerance.

{{table: components/MCurve}}

### Microstrip Mitred Bend (MMITER) {#mmiter}

{{symbol: mmiter}}

A chamfered (mitred) microstrip corner: width `W`, turn `Angle` (counter-clockwise positive, as MBEND's,
anywhere up to 120°) and miter `M`. `M` is the fraction of the corner's outer-to-inner diagonal that
is cut away, measured from the outer corner — the Douville–James definition — so at 90° `M = 0.5` runs
the cut corner to corner across the W×W corner square, and larger values cut deeper. On placement `M`
starts at the Douville–James optimum for the workspace's 50 Ω line, which is the cut that best cancels
the corner's reflection on a right-angle bend; at other angles that same fraction is a reasonable
starting point rather than an optimum.

Electrically the bend is the line it is drawn as — an MLIN of the corner's own centre-line length —
plus a small correction for what the corner does that a straight line would not. That correction comes
from a table characterised with circuitRF's own planar EM over W/h, εr, angle and miter, and is
interpolated in between, so any angle and miter inside the range work, not only 45° and 90°.

The pins sit where the strip is full width again: at 90° with `M` up to 0.5 these are the edges of the
W×W corner square (MBEND's planes); a deeper chamfer moves them out a little, and the extra line is part
of the part. Draw the MLINs either side from the pins. W/h between 0.35 and 7 and εr between 2.2 and 13
are characterised; outside that the result is still computed and the run says it is an extrapolation.
An angle past 120° or a miter deeper than the table covers (0.6 at 45° and below, 0.8 from 90°) is
refused. Conductor thickness enters through the line part only.

{{table: components/MMiter}}

### Microstrip T-Junction (MTEE) {#mtee}

{{symbol: mtee}}

A three-port microstrip T-junction. `W1`, `W2` and `W3` are the widths of the three arms, and `L1`,
`L2` and `L3` their lengths from the junction's centre to each pin — the shape the layout draws.

The arms are simulated as microstrip lines, with the junction model between them. A through arm carries
`L1 − W3/2` (or `L2 − W3/2`) of line, the branch `L3 − max(W1, W2)/2`: the junction model starts where
the crossing arm's edge is. When the branch reaches no further than the through line's edge, the tee is
a straight line with pin 3 on it, and it simulates as exactly that — two lines of `L1` and `L2` meeting
at pin 3 — so with pin 3 open it gives the same answer as an MLIN of `L1 + L2`. Any longer branch
brings the junction model in.

A tee with no `L` values (from a file saved before they existed) simulates the junction alone, and the
run says its arms are not simulated.

{{table: components/MTee}}

### Microstrip Cross-Junction (MCROSS) {#mcross}

{{symbol: mcross}}

A four-port microstrip cross-junction, with a width (`W1`–`W4`) and a length from the centre
(`L1`–`L4`) per arm.

The arms are simulated as microstrip lines with the junction model between them, as for the
[MTEE](#mtee): arms 1 and 3 carry `L − max(W2, W4)/2` of line, arms 2 and 4 `L − max(W1, W3)/2`. When
arms 2 and 4 both stop at the edge of arms 1 and 3, the cross is a straight line — the lines of `L1` and
`L3` with pins 2 and 4 on it — and with those pins open it equals an MLIN of `L1 + L3`. The same holds
the other way round. A cross with no `L` values simulates the junction alone, and says so.

{{table: components/MCross}}

### Linear Microstrip Taper (MTAPER) {#mtaper}

{{symbol: mtaper}}

A linearly tapered microstrip line running from width `W1` to width `W2` over length `L`.

{{table: components/Mtaper}}

### Klopfenstein Taper (MKLOPF) {#mklopf}

{{symbol: mklopf}}

A Klopfenstein-taper microstrip line — the taper profile that gives the shortest line for a stated
in-band ripple. Specify the two impedances (or the two widths), the maximum reflection `GammaMax`, and
either a length or a 3 dB corner frequency.

{{table: components/Mklopf}}

### Via (VIA) {#via}

{{symbol: via}}

A plated via changing layers: terminal A lands on `FromLayer`, B on `ToLayer`. Its inductance,
resistance and capacitance are computed from the technology's stackup — the barrel length, the drill it
belongs to, the ground planes it passes and any stub where the drill runs on — so none of them is typed.
Every row may be left empty to follow the technology; the parameter dialog shows the L, R and C that
result. The formulas, where they hold and how they compare with a 3D solve are in
[Vias](vias.html).

{{table: components/Via}}

### Via to Ground (VIAGND) {#viagnd}

{{symbol: via-gnd}}

A via from a pad down to a ground plane — the return of a shunt part, with the pad's capacitance across
it. One pin, on `FromLayer`; the far end is `GroundLayer`. See [Vias](vias.html#shunt) for what it
replaces.

{{table: components/ViaGnd}}

### Test Point Pad (TESTPAD) {#testpad}

{{symbol: testpad}}

A round test-point pad on an RF line: a pad of diameter `D` on `PadLayer` — by default the lowest signal
conductor, the board's far side — fed either through a via at its centre from `ViaFrom` (`Feed = Via`) or
by a short stub leaving it on one side (`N`, `E`, `S` or `W`, `FeedW` wide). Terminal 1 is the feed — the
via on `ViaFrom`, or the stub's end — and terminal 2 is the pad centre. Leave terminal 2 open for a test
point that is not in use, or continue it to a connector.

The pad loads the line with its capacitance to the nearest plane and the via's inductance, and the two
resonate. The model is an estimate: the pad's capacitance to the nearest plane on each side, as a disc over a
plane, with the dielectrics between combined in series; the via's inductance over its length, with the barrel's
capacitance to each plane it passes. Every inner conductor counts as a plane under the pad unless `ClearLayers`
names it. Messages gives the values and the resonance once per run. Rows that do not apply to the chosen `Feed`
are greyed out in the parameter dialog: `FeedW` through a via, and `ViaFrom`, `Drill`, `InnerPadD` and
`AntipadD` for a side-fed pad.

**Unused pads on the via.** Board CAD usually keeps a pad on every conductor a via passes, joined to nothing,
and each adds capacitance across its antipad. Set `InnerPadD` to their diameter and the model counts them
(and the layout draws them) on every conductor between `ViaFrom` and `PadLayer` that `ClearLayers` does not
name; `AntipadD` is the clearance round the via in each plane (empty: the technology's). Messages gives their
share separately. Removing unused pads at manufacture removes it.

**Cut-outs.** `ClearLayers` names the conductors whose plane gets a round cut-out `ClearD` across under the
pad; the parameter dialog lists the technology's conductors to tick. Where the nearest plane is cut, the pad faces
it over the ring outside the hole and the next plane through the hole, so its capacitance falls as `ClearD`
grows and settles once the hole is a little wider than the pad. `ClearD = 0`, or one no wider than the via's
`AntipadD`, cuts nothing: those planes are modelled as ordinary planes, and Messages says so. In a plane the via
passes, its barrel sees the larger of `ClearD` and `AntipadD`. Cut-outs are not drawn as shapes: a ground pour drawn by Update Layout or Draw Ground Pour cuts them, as it
cuts a via's antipad. On a board whose planes are drawn artwork, right-click the placed pad and choose
[Cut Clearances into Planes](layout-editor.html#cut-clearances).

**An EM port at the centre.** With `Centre = Port` the layout carries an EM port at the pad's centre, an
internal port to ground numbered `PortNum` (empty: the next free number). An EM setup of the board picks it
up from inside the placed part — in 3D as a sheet from the pad to the nearest plane with metal there, through
any cut-out — so the pad can be solved with and without each cut-out before a board is built. See
[Ports a component carries](em-3d.html#component-ports).

{{table: components/TestPad}}

### GSG Probe Pad (GSGPAD) {#gsgpad}

{{symbol: gsgpad}}

A ground-signal-ground probe pad and the microstrip line leaving it, for an RF probe landing on a die or a
board. The signal pad sits between two ground pads at `Pitch` — centre to centre, the probe's own pitch — each
ground pad holding `NVia` vias to the plane in a row along it, and a line `W` wide and `L` long leaves the
signal pad. Terminal 1 is where the probe's signal tip lands, the middle of the signal pad; terminal 2 is the
line's far end. Put the measurement port on terminal 1. In the layout the probe approaches from the side away
from the line.

The model is MLIN: half the signal pad, `PadW` wide and `PadL/2` long, then the line, cascaded. The ground pads
are ideal ground at the probe's tip, which is where a calibrated probe puts its reference; their vias are drawn
but are not part of the circuit. It is a microstrip, never a coplanar line: the ground pads stop at the signal
pad and do not run along the line. The vias are a [Via to Ground](#viagnd)'s, landing on the plane under the
line — on a die, backside vias — with the technology's drill and pad; a ground pad grows longer when its row
of vias needs the room. `NVia = 0` draws no vias, for ground pads grounded some other way.

`W` is set to the 50 Ω width when the part is placed on a technology. `Pitch`, `PadW` and `PadL` are in µm and
are the probe's, so no technology changes them.

{{table: components/GsgPad}}

### Wirebond (wBond) {#wbond}

{{symbol: wbond}}

A wirebond design placed as a single component. Its symbol is **generated from the design it carries**
— two pins per wire array plus a `REF` pin — so both the pin count and the pin names are properties of
that design rather than of the component type. The wires themselves are drawn in the **layout** view,
not here.

<div class="callout note">
<span class="label">wBond has its own chapter</span>
<p>The geometry, loop height and span, the inductance and capacitance models, the 3D kernel, DXF
interchange and Touchstone export are in <b><a href="wbond.html">wBond</a></b>. The two parameters
most often left wrong — <code>IncludeCapacitance</code> and <code>er</code> — are explained there.</p>
</div>

{{table: components/WBond}}

## MMIC passives {#mmic}

The parts a die is drawn from. Like a microstrip line, each one takes its process from the workspace
technology — you state only the geometry, and the stackup supplies the film's permittivity and
thickness, the metals, the sheet resistance, the air-bridge height and the substrate. Each has a
built-in layout cell of the same name, so a schematic of them generates a layout the way a board's
microstrip does, with every terminal on the metal that lies on the substrate.

On a technology that lacks what a part needs — a board has no MIM film, for instance — the run says
which piece is missing and uses the shipped GaAs process's numbers instead.

The properties panels show what the geometry comes out to, as a value you read rather than type: a
TFR's resistance, a MIMCAP's capacitance and a spiral's inductance, each computed by the same model a
run uses, on the technology's process. It follows every edit as you type. In a layout's Inspector and
Component Properties it is the greyed row after the geometry. A schematic's Parameters panel also
shows a spiral's series resistance, at DC and at 10 GHz, where the skin effect has raised it.

### MIM Capacitor (MIMCAP) {#mimcap}

{{symbol: mimcap}}

A metal-insulator-metal capacitor. `W` × `L` is the top plate; the capacitance is the film's
ε·W·L/t with the plates' edge fringing, the series resistance comes from both plates' metal, the loss
from the film's loss tangent, and the bottom plate's capacitance through the substrate to ground is
included. Terminal 1 is the top plate and terminal 2 the bottom plate: for a shunt capacitor, ground
terminal 2, which shorts that substrate capacitance out.

{{table: components/MimCap}}

### Spiral Inductor (SPIRAL) {#spiral}

{{symbol: spiral}}

A square spiral of `N` turns, `W` wide with `S` between turns and a clear opening `Din` across the
middle. Terminal 1 is the outer end; terminal 2 is the inner end, brought out over the turns on the
air-bridge metal.

<div class="callout note">
<span class="label">The inductance is an estimate</span>
<p>It is summed over the coil exactly as the layout draws it — every side, the escape over the turns
and the landing pad — together with the coil's mirror image in the ground plane under the substrate.
Each conductor is treated as a line along its centre, so current crowding toward the edges and the
skin effect are not in the inductance. On the shipped GaAs process the default coil reads 1.50 nH
against 1.72 nH from a coarse EM extraction of the drawn part. About half of that difference is the
extraction treating the metal as a sheet with no thickness. For a value to rely on, extract the drawn
coil and use the result in its place:</p>
<p><code>circuitrf em &lt;workspace&gt; --component "SPIRAL N=2.5 W=10 um S=10 um Din=100 um" --freq 1GHz:20GHz:1GHz -o L1.s2p</code></p>
<p>Port 1 of the file is terminal 1, so it drops into the schematic as an SnP in the part's place.</p>
</div>

{{table: components/Spiral}}

### Octagonal Spiral Inductor (OSPIRAL) {#octspiral}

{{symbol: ospiral}}

The square spiral with its corners cut: the same `N`, `W`, `S` and `Din`, meaning the same things,
with `Din` measured across the innermost pair of parallel flats. Every side is a constant `S` from
the turn beside it, diagonals included. For the same footprint the winding is shorter than a square
one's, which costs some inductance and saves more resistance. Its corners are 45° rather than 90°,
which spreads the current that crowds into a square spiral's corners. `N` counts from the inner end,
at the middle of the innermost flat, and the layout draws whole eighth turns.

The inductance is the same estimate as the square spiral's, summed over the octagonal coil as drawn.
Extract the drawn coil for a value to rely on:
`circuitrf em <workspace> --component "OSPIRAL N=2.5 W=10 um S=10 um Din=100 um" -o L1.s2p`.

{{table: components/OctSpiral}}

### Thin-Film Resistor (TFR) {#tfr}

{{symbol: tfr}}

The technology's resistor film, `W` wide with its contacts `L` apart: its resistance is the film's
sheet resistance times L/W squares. The contacts and their overlap of the film are not included. A
technology states its film by giving a drawing layer a sheet resistance.

{{table: components/Tfr}}

### Air Bridge (AIRBRIDGE) {#airbridge}

{{symbol: airbridge}}

A span of the air-bridge metal crossing another line: `L` is the clear span between its two posts,
`W` its width and `Wu` the width of the line it crosses. Terminals 1 and 2 are the landings; terminals
3 and 4 are the two ends of the crossed line's segment under the bridge, which is two bridge widths
long. The two are coupled through the overlap capacitance. Tie terminals 3 and 4 to ground for a
bridge over a ground strap.

{{table: components/Airbridge}}

## Data-file components

### Touchstone N-Port (SnP) {#snp}

{{symbol: snp}}

An N-port backed by a Touchstone (`.sNp`) file — measured or modelled S-parameters embedded in the
circuit and interpolated onto the analysis sweep.

- `NumPorts` — the port count. Hidden; it drives the symbol and the pin count.
- `File` — the path to the Touchstone file. Once a file is chosen and is there, **Preview** opens it in the
  [S-Parameter Viewer](sparameter-viewer.html).
- `RefNode` — when true, expose a floating common reference pin (N+1 nets) instead of grounding each
  port.
- `PinConfig` and `Pitch` — the pin arrangement and spacing on the symbol.
- `InterpMode`, `InterpDomain` and `ExtrapMode` — how the data is interpolated between points, in
  which domain, and what happens beyond its frequency range.

The symbol is dynamic; the pin arrangement, the pitch and the floating-reference option are covered in
[Dynamic symbols](dynamic-symbols.html#snp).

{{table: components/Snp}}

### SPICE Model or Subcircuit {#spicemodel}

{{symbol: spicemodel}}

A SPICE `.model` card or `.subckt` definition placed as a component and **run from its own file**.
Nothing is copied: edit the file and the schematic follows it.

- `File` — the path to the SPICE file.
- `Name` — which definition in that file to run. A vendor file usually holds a part *and* every
  piece the part is built from, each of them a definition in its own right; leaving `Name` blank
  picks the highest-level one, the one nothing else in the file calls.
- `PinConfig` and `Pitch` — the pin arrangement and spacing, for a `.subckt` only. A `.model` card
  draws as the device itself, whose terminals are where that device's terminals are.
- `Glyph` — set on a part placed from a [vendor part library](pdk-integration.html#vendor): `C` or `L`
  for a two-terminal capacitor or inductor, which then draws as the ordinary capacitor or inductor
  symbol. It is stored on the instance, so the part still draws as what it is when its library is
  missing, marked **Not Found**.

Any parameter the chosen `.subckt` declares appears as an ordinary editable row below the panel,
seeded at the definition's own default.

The symbol is dynamic and is generated from the file. A `.model` card circuitRF has a device for
draws as **that device** — a diode card draws as a diode — so the schematic says which lead is
which without anything being opened. A `.subckt` draws as a box carrying the definition's own port
names. Until a file is chosen it draws as a generic two-port, which can be placed and wired first.

A model type circuitRF has no equivalent for is **refused by name**, in the parameter dialog at the
moment the file is chosen and again at Run. Nothing is approximated: the nearest native device
would simulate and be quantitatively wrong.

<div class="note">
<p><b>This is the reference; the import is the copy.</b> The project tree's <b>Copy to Workspace as
Cell…</b> reads the same file and builds an editable cell out of it — a schematic you can open,
re-symbol and change. Use that when the model is a starting point. Use this component when the file
is the authority and must stay so. Because there is no <code>.csch</code> behind it, a SpiceModel
has no pop-in: there is nothing to push into.</p>
</div>

{{table: components/SpiceModel}}

### Impedance N-Port (Z) {#zport}

{{symbol: zport}}

An N-port defined by its impedance matrix `Z[p,q]`. `NumPorts` is the port count, hidden, and it
drives the symbol and pin count. The symbol grows with the port count — see
[Dynamic symbols](dynamic-symbols.html#zport).

{{table: components/ZPort}}

## Nonlinear devices

### Symbolically-Defined Device (SDD) {#sdd}

{{symbol: sdd}}

A user-defined nonlinear device: you write each port current — and, optionally, each port charge — as
an *equation* in the port voltages, and circuitRF differentiates it automatically for the solver. This
is how the GaN FET model in the [netlist example](netlist.html) is defined. The symbol is dynamic; see
{{anchor: dynamic-symbols#sdd|Dynamic symbols}}.

- `I[x,0]` — the port-x current equation, seeded as a 50 Ω conductance. Weight 0 is the current
  itself.
- `Q[x]`, `I[x,w]`, `H[w]` — optional charge equations, higher weightings, and weighting functions.
- `C[n]` / `Cport[n]` — bind a control current `_cn` to another device's current.

{{table: components/Sdd}}

<div class="callout note">
<span class="label">The SDD has its own chapter</span>
<p>The SDD is deep — the equation grammar (<code>I[p,w]</code>, <code>Q[p]</code>), weighting functions
<code>H[w]</code>, and <strong>how to reference another device's current</strong> (<code>_cn</code> /
<code>C[n]</code>) are all covered in <a href="sdd.html"><strong>The SDD</strong></a>.</p>
</div>

### Junction Diode (Diode) {#diode}

{{symbol: diode}}

A junction diode: anode at the top, cathode at the bottom. `Rs` is a **model parameter, not a
separate placed resistor** — when it is non-zero the elaborator mints the internal node itself, so the
schematic shows one device either way.

{{table: components/Diode}}

<div class="callout warn">
<span class="label">What is NOT modelled</span>
<p><strong>High-level injection</strong> (a card's <code>IKF</code>) — at high forward bias the
current of a real diode rolls off from the ideal exponential towards a square root of it, and this
model has no term for that knee, so it stays exponential and is optimistic there. An extracted
<code>Rs</code> often absorbs some of that roll-off, which is a fit rather than the physics and does
not follow temperature the way the real mechanism does.
<strong>Flicker noise</strong> (<code>Kf</code>/<code>Af</code>) — there is no noise analysis for it
to feed. <strong>Self-heating</strong> — the device temperature is a parameter, not a solved node.
<strong>Sidewall junction capacitance</strong> as a separate term — <code>Cj0</code>, <code>Vj</code>
and <code>M</code> describe one junction. A parameter this model does not read is not offered in the
palette; a card stating one has it {{anchor: spice-import#families|named on import}}.</p>
</div>

### The FET family {#fets}

Five native large-signal FET models, one per published drain-current law. They are five separate
components rather than one component with a model selector, because they are not variants of one
another: each has its own parameter set, and several reuse a spelling for a different quantity — the
quadratic law's `Beta` is a transconductance parameter, while the cubic law's is a gate-voltage shift
with drain bias. One kind with a selector would present the union of all five parameter sets and
silently accept the wrong ones.

All five **share one glyph and one three-pin geometry** — gate left, drain top, source bottom. The
topology genuinely is the same, and the type label below the symbol names the law. The glyph is the
JFET's: a MESFET's or HEMT's Schottky gate is a junction made directly on the channel, not an insulated
gate, so the gate arrow lands on one unbroken (depletion) channel bar — into it for n-channel, out of it
for p-channel. **The source is an
ordinary pin:** these are not hard-wired common-source.

#### What all five share {#fet-shared}

**Terminals and ports.** Three nets — `gate drain source` — mapped onto two ports: port 0 is
(gate, source) and port 1 is (drain, source). So the first port voltage is **Vgs** and the second is
**Vds**, which is the form every published FET equation is written in. Nothing has to be transposed to
use a datasheet parameter set.

**What is modelled.** The drain current and both of its derivatives — gm = ∂Id/∂Vgs and
gds = ∂Id/∂Vds — computed **analytically** rather than by finite differences, which matters inside a
Newton loop precisely where the device is most nonlinear. On top of that: optional forward gate
conduction, as an ordinary diode from gate to source (set by `Is` and `N`, off when `Is` is zero), and
gate charge (below). Parameter temperature scaling is modelled through `Temp` and `Tnom`.

<div class="callout warn">
<span class="label">What is NOT modelled — read this before choosing a model</span>
<p><strong>The Statz/TOM-family charge formulation</strong> — it works on a smoothed effective voltage
rather than on Vgs and Vgd separately, so it is a different scheme and not a parameter change to the two
below. <strong>Transit-time delay. Breakdown. Self-heating</strong> — the device temperature is a
parameter, not a solved node, so there is no electrothermal feedback. If your application depends on any
of these, use a compiled model through
<a href="#veriloga">VerilogA</a> instead.</p>
</div>

#### Cgs, Cgd and CapModel {#fet-capmodel}

Gate charge is **selectable**, because the published models disagree about it. It is a parameter rather
than a per-model decision for exactly that reason: two authors implementing "the Curtice model" from the
literature will not necessarily give it the same gate charge.

| `CapModel` | Gate charge | Are `Cgs`/`Cgd` linear? |
|---|---|---|
| `0` | None at all | n/a — no charge storage |
| `1` *(default)* | **Constant** `Cgs`/`Cgd` | **Yes — linear.** They are fixed capacitances, independent of bias |
| `2` | **Junction** (depletion) charge, applied to Vgs and Vgd separately | **No — bias-dependent.** `Cgs`/`Cgd` are then the *zero-bias* values, Cj0 |

At `CapModel = 2` the charge on each junction is the standard depletion form:

```
Q = Cj0·Vbi/(1 − M) · [ 1 − (1 − V/Vbi)^(1 − M) ]        for V < Fc·Vbi
                                                          continued by its TANGENT above Fc·Vbi

parameters:  Cgs, Cgd   zero-bias capacitances (Cj0 for the two junctions)
             Vbi        junction potential
             M          grading coefficient
             Fc         forward-bias changeover, as a fraction of Vbi
```

The tangent continuation above `Fc·Vbi` is not cosmetic: a hard clamp there would leave a kink in the
Jacobian and stall Newton.

**Which to pick.** Use `1` when your parameter set was extracted with fixed capacitances — which is the
common case for the older laws — and when you want the cheapest evaluation. Use `2` when the extraction
gives you junction parameters (Cj0, Vbi, M) and the gate swing is large enough for the bias dependence
to matter, which in a power amplifier driven into compression it usually is.

<div class="callout note">
<span class="label">Cgd sees Vgd, not Vgs</span>
<p>The gate–drain capacitance is across Vgd = Vgs − Vds, so it contributes to <em>both</em> ports and to
the Jacobian's off-diagonal terms. That is why a change in drain bias moves the gate-side loading, and it
is why <code>Cgd</code> dominates the input match of a device long before <code>Cgs</code> does.</p>
</div>

#### Curtice quadratic (FET_Curtice) {#fetcurtice}

{{symbol: fet-curtice}}

The Curtice quadratic law: `Vto`, `Beta`, `Alpha` and `Lambda`.

{{table: components/FetCurtice}}

#### Curtice–Ettenberg cubic (FET_CurticeCubic) {#fetcurticecubic}

{{symbol: fet-curtice-cubic}}

The Curtice–Ettenberg cubic law: `A0`–`A3`, `Gamma`, `Beta` and `Vds0`.

{{table: components/FetCurticeCubic}}

#### Statz (FET_Statz) {#fetstatz}

{{symbol: fet-statz}}

The Statz law: `Vto`, `Beta`, `B`, `Alpha` and `Lambda`.

{{table: components/FetStatz}}

#### Materka–Kacprzak (FET_Materka) {#fetmaterka}

{{symbol: fet-materka}}

The Materka–Kacprzak law: `Idss`, `Vp0`, `Gamma` and `Alpha`.

{{table: components/FetMaterka}}

#### Angelov / Chalmers (FET_Angelov) {#fetangelov}

{{symbol: fet-angelov}}

The Angelov (Chalmers) law: `Ipk`, `Vpk`, `P1`–`P3`, `Alpha` and `Lambda`.

{{table: components/FetAngelov}}

#### p-channel: Curtice-P, Statz-P, Materka-P {#pfets}

Three of the five laws are also offered as **p-channel** parts. Polarity is a sign, not a law: every
voltage, current and charge is mirrored, so a p-channel tile is its n-channel counterpart with the
same parameter list and the same equations. `Vto` (or `Vp0`) is stated the way a card states it —
**positive** for a p-channel depletion MESFET — and circuitRF applies the sign itself.

<div class="callout warn">
<span class="label">Why only three</span>
<p>A mirror is unambiguous only where the gate dependence is anchored to a threshold and is even in
it, which is the case for the quadratic, Statz and Materka laws. The
<b>Curtice–Ettenberg cubic</b> and <b>Angelov</b> laws are polynomials fitted <i>directly</i> against
the gate voltage — <code>A0</code>–<code>A3</code> and <code>P1</code>–<code>P3</code> — so mirroring
one would have to negate the odd-order coefficients and leave the even ones alone, and no published
convention says a p-channel parameter set is written that way. Guessing would give a device that
simulates and is wrong in its odd-order terms only: a gm curve of the wrong shape, at no bias where
anything obviously breaks. So those two are n-channel only, deliberately. Nothing is lost at import:
a p-channel model card is read as the quadratic law or Statz, never as either of these.</p>
</div>

##### Curtice-P (PFET_Curtice) {#pfetcurtice}

{{symbol: pfet-curtice}}

{{table: components/PFetCurtice}}

##### Statz-P (PFET_Statz) {#pfetstatz}

{{table: components/PFetStatz}}

##### Materka-P (PFET_Materka) {#pfetmaterka}

{{table: components/PFetMaterka}}

### The junction FET {#jfets}

Two tiles — **NJFET** and **PJFET** — over the Shichman–Hodges square law, one set of equations with
one sign changed.

<div class="callout warn">
<span class="label">This is not the MESFET family with different numbers</span>
<p>The two differ in the <b>knee</b> — a MESFET's is a fitted <code>tanh</code> or a piecewise cubic
with its own parameter, a JFET's is the square law's own boundary at <code>Vds = Vgt</code> — and in
the <b>gate</b>: a JFET's gate is a real p-n junction that conducts and stores depletion charge
against <i>both</i> ends of the channel, where the MESFET family models a Schottky gate as one
forward diode. Reading a JFET card as a Curtice quadratic with the <code>tanh</code> ignored gives a
device that simulates and is quantitatively wrong through the whole knee — matched at pinch-off and
matched deep in saturation, and out by tens of percent in between, for every choice of
<code>Alpha</code>.</p>
</div>

```
Vgt = Vgs − Vto
Id  = 0                                            Vgt ≤ 0        cutoff
    = Beta·Vds·(2·Vgt − Vds)·(1 + Lambda·Vds)      Vds < Vgt      linear
    = Beta·Vgt²·(1 + Lambda·Vds)                   Vds ≥ Vgt      saturation
```

**The gate is two junctions.** `Cgs` and `Cgd` are the zero-bias capacitances of bias-dependent
depletion charge, not fixed capacitors: `Cgd` falls as the gate-drain junction is reverse-biased,
which at RF is most of the reverse isolation. Both junctions also conduct, with a diffusion term and
an optional recombination term (`Isr`/`Nr`) that is a **second** exponential with its own ideality —
folding it into the first would fit one decade of the gate leakage and miss the rest.

**The device is symmetric**, like the MOS transistor: which end acts as the drain is decided by the
bias. **`Rd` and `Rs` are model parameters**, on internal nodes circuitRF mints, not resistors you
place beside the device.

**`Vtotc` shifts the pinch-off additively, in volts per degree; `Betatce` scales `Beta` in *percent*
per degree** — `1.01^(tc·ΔT)`, which is not the same as `1 + 0.01·tc·ΔT` once ΔT is more than a few
tens of degrees. The two forms are the published ones and they are not interchangeable.

<div class="callout warn">
<span class="label">What is NOT modelled</span>
<p><strong>Gate breakdown</strong>; the <strong>doping-profile knee</strong> that a higher published
JFET level adds (its <code>B</code> parameter) and that level's own channel-length modulation
(<code>Alpha</code>/<code>Vk</code>) — there is no square-law parameter that means the same thing, so
a card stating them is imported as the square law with those parameters <i>named</i> rather than
folded into <code>Lambda</code>; <strong>transit-time charge</strong>; <strong>flicker
noise</strong>; and <strong>self-heating</strong>.</p>
</div>

#### n-channel (JFET_N) {#jfetn}

{{symbol: jfet-n}}

The gate arrow points **into** the channel. The channel bar is unbroken — a **depletion** device,
conducting at zero gate bias, which is the opposite of the MOS glyph's three segments.

{{table: components/JfetN}}

#### p-channel (JFET_P) {#jfetp}

{{symbol: jfet-p}}

The gate arrow points **out of** the channel. Same equations, every sign reversed — so `Vto` is
positive, as a p-channel card states it.

{{table: components/JfetP}}

### The bipolar transistor {#bjt}

Two components — **NPN** and **PNP** — over **one** set of equations and one parameter list. This is
the opposite arrangement from the FET family above, and for the opposite reason: there the five names
denote five different drain-current laws, while here the parameter list is identical and only a sign
differs. It is still two components rather than one with a polarity setting, because the two **draw
differently**: the emitter arrow is the whole of what tells a reader which transistor is on the
schematic, and a setting would leave the drawing and the netlist free to disagree.

**Terminals.** Three pins — collector top, base left, emitter bottom. The base is an ordinary pin and
so is the emitter: these are not hard-wired to any configuration.

**Rb, Re and Rc are model parameters, not separately placed resistors.** When one of them is non-zero
the elaborator mints the internal node itself, so the schematic shows one device either way. They are
not optional detail at RF — `Rb` sets the input match and the noise, `Re` degenerates the
transconductance, and all three are shunted by the junction capacitances — which is why the internal
nodes are genuine unknowns rather than being folded away. Folding them would be exact at DC and wrong
in harmonic balance, where an internal node carries its own harmonic content.

#### What is modelled {#bjt-equations}

The standard charge-control model. `Vt` is kT/q at the device temperature; primed voltages are taken
at the internal nodes, inside the parasitic resistances.

```
Icc  = Is·(exp(Vb'e'/(Nf·Vt)) − 1)                    forward transport
Iec  = Is·(exp(Vb'c'/(Nr·Vt)) − 1)                    reverse transport
Ibe  = Icc/Bf + Ise·(exp(Vb'e'/(Ne·Vt)) − 1)          base current, emitter junction
Ibc  = Iec/Br + Isc·(exp(Vb'c'/(Nc·Vt)) − 1)          base current, collector junction
Ict  = (Icc − Iec)/qb                                 collector-to-emitter transport

q1   = 1 / (1 − Vb'c'/Vaf − Vb'e'/Var)                base-width modulation (the Early effect)
q2   = Icc/Ikf + Iec/Ikr                              high-level injection
qb   = q1/2 · (1 + sqrt(1 + 4·q2))

Qbe  = Qj(Vb'e'; Cje,Vje,Mje) + Tff·Icc/qb            depletion + diffusion charge
Qbc  = Xcjc·Qj(Vb'c'; Cjc,Vjc,Mjc) + Tr·Iec
Tff  = Tf·(1 + Xtf·(Icc/(Icc+Itf))²·exp(Vb'c'/(1.44·Vtf)))
```

Every derivative of every one of those is computed **analytically** rather than by finite differences,
which matters inside a Newton loop precisely where the device is most nonlinear.

<div class="callout note">
<span class="label">Zero means "not modelled"</span>
<p><code>Vaf</code> and <code>Var</code> at zero mean the Early effect is switched off — never "the Early
voltage is zero volts". The same rule applies to <code>Ikf</code>/<code>Ikr</code> (no high-level
injection), <code>Ise</code>/<code>Isc</code> (no low-bias leakage) and to each of the three parasitic
resistances. This is the same convention the diode's <code>Bv</code> follows.</p>
</div>

**The base resistance is current-dependent.** With `Irb` given it follows the standard
conductivity-modulation relation, falling from `Rb` at zero base current towards `Rbm` at high current;
with `Irb` zero it follows `Rbm + (Rb − Rbm)/qb` instead, which is the same physics stated through the
base charge. Both are in the published model, and which one applies is decided by the parameter set.

**`Xcjc` splits the collector junction across the base resistance.** That fraction of `Cjc` sits on the
internal base node and the remainder runs from the *external* base to the internal collector — a real
distributed effect, and the reason a device's feedback capacitance does not simply see `Rb`. When `Rb`
is zero the two nodes are the same net and the halves add back to `Cjc`, so nothing special happens.

<div class="callout warn">
<span class="label">What is NOT modelled — read this before choosing a model</span>
<p><strong>The substrate junction</strong> (<code>Cjs</code>/<code>Vjs</code>/<code>Mjs</code>) — a
discrete RF transistor has no substrate terminal to attach it to, and adding a fourth pin would change
what the symbol means. <strong>Excess phase</strong> (<code>Ptf</code>) — it is a delay, and circuitRF's
weighting functions carry 1 and jω, not exp(−jωτ), so accepting the parameter would be accepting a value
that does nothing. <strong>Flicker noise</strong> (<code>Kf</code>/<code>Af</code>) — there is no noise
analysis for it to feed. <strong>Self-heating</strong> — the device temperature is a parameter, not a
solved node, so there is no electrothermal feedback. <strong>Package parasitics</strong> — the lead
inductances and package capacitances of a real part are separate components you place around the
transistor, not parameters of it. A parameter this model does not read is not offered in the palette.
If your application depends on any of the above, use a compiled model through
<a href="#veriloga">VerilogA</a> instead.</p>
</div>

<div class="callout note">
<span class="label">The defaults are a working device, not a placeholder</span>
<p>A freshly dragged transistor carries a complete small-signal RF silicon parameter set, so it
simulates at gigahertz frequencies before you have edited anything. Treat it as a <strong>starting
point</strong>: it is a generic device, not a model of any particular part, and the numbers should be
replaced with the ones for the transistor you actually have.</p>
</div>

#### n-p-n (BJT_NPN) {#bjtnpn}

{{symbol: bjt-npn}}

The emitter arrow points **out of** the base — conventional current leaving the emitter.

{{table: components/BjtNpn}}

#### p-n-p (BJT_PNP) {#bjtpnp}

{{symbol: bjt-pnp}}

The emitter arrow points **into** the base. Same equations, same parameter names, every voltage and
current negated — so a parameter set written for one polarity is used unchanged for the other.

{{table: components/BjtPnp}}

### The MOS transistor {#mos}

Two tiles — **NMOS1** and **PMOS1** — over one set of equations with one sign changed, arranged like
the bipolar pair rather than like the five FET laws. The `1` is the **level**: the Shichman–Hodges
square law, the original compact MOSFET model and the one every later level is written as a
departure from.

<div class="callout warn">
<span class="label">This transistor has FOUR pins. Wire the bulk.</span>
<p>Drain, gate, source and <strong>bulk</strong>. The bulk is a real terminal and not a convenience:
tying it internally to the source would silently delete the <strong>body effect</strong>, which is
what <code>Gamma</code> and <code>Phi</code> describe and which is worth hundreds of millivolts of
threshold in a circuit where the source does not sit at the substrate potential. A part whose bulk
really is tied to its source says so by wiring the pin — one wire, and then the schematic states
the fact instead of hiding it. Leave the pin unwired and the substrate floats, which solves, and is
a different circuit.</p>
</div>

#### What is modelled {#mos-equations}

```
Vth  = Vto + Gamma·(√(Phi − Vbs) − √Phi)
Vgt  = Vgs − Vth
Id   = 0                                              Vgt ≤ 0        cutoff
     = Beta·(Vgt − Vds/2)·Vds·(1 + Lambda·Vds)        Vds < Vgt      linear
     = (Beta/2)·Vgt²·(1 + Lambda·Vds)                 Vds ≥ Vgt      saturation
Beta = Kp·W/Leff,   Leff = L − 2·Ld
```

**The device is symmetric.** Which terminal acts as the drain is decided by the bias, not by the
schematic: drive `Vds` negative and circuitRF swaps the two ends and evaluates the law in the
orientation it is published in. A model that did not would return a plausible, wrong current for
every transmission gate and every passive mixer ever drawn.

**Both bulk junctions are real diodes**, with their own saturation currents, their own depletion
charge and their own sidewall term. They are what makes the substrate connection matter, and they
are why a bulk biased above the source or the drain draws enormous current — as it does in silicon.

**`Rd` and `Rs` are model parameters, not resistors you place.** A non-zero one moves the intrinsic
transistor onto an internal node circuitRF mints for it, so the schematic shows one device either
way. That node is a genuine unknown rather than something eliminated locally: at RF the ohmic
resistances are shunted by the junction and overlap capacitances, and collapsing them is exact at DC
and wrong in harmonic balance.

<div class="callout note">
<span class="label">Where a card states a process quantity instead of a device one</span>
<p>Several parameters come in pairs — <code>Kp</code> or <code>Uo</code>, <code>Gamma</code>/<code>Phi</code>
or <code>Nsub</code>, <code>Rd</code>/<code>Rs</code> or <code>Rsh</code> with <code>Nrd</code>/<code>Nrs</code>,
<code>Cbd</code>/<code>Cbs</code> or <code>Cj</code>/<code>Cjsw</code> with the junction areas. They are
not alternative spellings of one number: circuitRF derives the device quantity from the process one
only where the device quantity is absent, and a value you state always wins.</p>
</div>

#### The gate charge {#mos-charge}

The intrinsic gate charge is the **charge-based long-channel result**, not the Meyer capacitance set
that a transient simulator conventionally uses.

That difference matters here more than it does in a time-stepping simulator. Meyer's model states
three capacitances that each depend on more than one terminal voltage, so the charge they imply
depends on the **path** taken through the bias space — go once around a harmonic cycle and it does
not return to where it started, and a periodic steady-state solve has nothing to converge to.
Integrating the channel charge directly is conservative by construction, and its derivatives reduce
to exactly Meyer's capacitances wherever Meyer's are right: `Cox` at `Vds` = 0, two thirds of `Cox`
in saturation, zero in cutoff.

The channel charge is split **evenly** between the drain and the source. The alternative 40/60 split
is the better one in a switching transient; at RF both ends see the same signal through the same
channel resistance, and the even split is what keeps the device symmetric under the drain/source
swap above.

<div class="callout warn">
<span class="label">No <code>Tox</code> means no gate capacitance</span>
<p><code>Tox</code> is the only thing that sets the oxide capacitance, so a parameter set that does
not state one has <strong>no intrinsic gate charge at all</strong> — only the <code>Cgso</code>,
<code>Cgdo</code> and <code>Cgbo</code> overlaps remain. That is the published rule and circuitRF
does not invent a thickness to fill it, because there is nothing on the card to derive one from. It
is worth knowing before wondering where the gain went.</p>
</div>

<div class="callout warn">
<span class="label">What is NOT modelled — read this before choosing a model</span>
<p><strong>Subthreshold conduction</strong> (<code>Nfs</code>) — the classical law goes to exactly
zero at threshold, so a device biased there is being asked a question this model cannot answer.
<strong>Short-channel effects</strong> — drain-induced barrier lowering, mobility degradation,
velocity saturation and channel-length modulation done properly are what the higher levels add;
here <code>Lambda</code> is a single fitted output slope and nothing else.
<strong>Flicker noise</strong> (<code>Kf</code>/<code>Af</code>) — there is no noise analysis for it
to feed. <strong>Self-heating</strong> — the device temperature is a parameter, not a solved node.
A parameter this model does not read is not offered in the palette. If your application depends on
any of the above, use a compiled model through <a href="#veriloga">VerilogA</a> instead.</p>
</div>

<div class="callout note">
<span class="label">The defaults are a bare square-law device, on purpose</span>
<p>Unlike the bipolar tiles, a freshly dragged MOS transistor carries a threshold, a transconductance
parameter and a geometry, with every process quantity at zero. A MOS parameter set is a property of
a <strong>process</strong>, and there is no such thing as a representative one — inventing plausible
numbers here would put a specific fabricated transistor in the palette and let you simulate it
without ever noticing you had not supplied a model.</p>
</div>

#### n-channel (MOS1_N) {#mos1n}

{{symbol: mos1-n}}

The bulk arrow points **into** the channel — the substrate junction read the usual way. The channel
bar is drawn in three segments, the standard mark for an **enhancement** device: there is no channel
until the gate makes one, so the part is off at zero gate bias.

{{table: components/Mos1N}}

#### p-channel (MOS1_P) {#mos1p}

{{symbol: mos1-p}}

The bulk arrow points **out of** the channel. Same equations, same parameter names, every voltage,
current and charge negated — so `Vto` is stated as the card states it, negative for an ordinary
p-channel enhancement device, and circuitRF applies the sign itself.

{{table: components/Mos1P}}

#### Level 3: the short-channel law {#mos3}

**NMOS3** and **PMOS3** are the same four terminals and the same glyph over a different set of
equations — the semi-empirical short-channel model. Two more tiles rather than a `Level` parameter on
the level-1 ones, for the reason the five FET laws are five tiles: a level is a different law, and
its six parameters mean nothing to the other one.

Each parameter turns on exactly one mechanism and each is **off at zero**, so a level-3 card stating
two or three of them is an ordinary thing to import.

| Parameter | What it turns on |
|---|---|
| `Eta` | **Drain-induced barrier lowering** — the threshold falls as the drain is pulled up. This is what makes a short device's output conductance real, and it is why level 3 has no `Lambda`. |
| `Theta` | **Mobility degradation with gate field** — carriers pressed against the oxide scatter off it, so transconductance stops rising with gate drive. |
| `Vmax` | **Velocity saturation.** Carriers stop going faster, so the device saturates *earlier* than pinch-off would put it. |
| `Kappa` | **Channel-length modulation**, done as a real shortening of the channel rather than as a multiplier on the current. |
| `Xj` with `Nsub` | **Short-channel charge sharing** — the source and drain depletion regions take a share of the bulk charge the gate would otherwise hold, so the body effect weakens as the channel gets shorter. |
| `Delta` | The **narrow-width effect**, which pushes the threshold the other way. |

<div class="callout note">
<span class="label">Level 3 is not level 1 with extra terms switched off</span>
<p>Turn all six off and the square law does come back — but only on a device with <b>no body
effect</b>. The <b>bulk-charge factor</b> is itself a level-3 term: it replaces the square law's
plain <code>Vds/2</code> with <code>(1 + fb)·Vds/2</code>, and <code>fb</code> is driven by
<code>Gamma</code>, not by any of the six. So the two levels genuinely differ by around fifteen
percent of drain current on a device that states a body effect and nothing else. That is a real
difference between the two published laws, and worth knowing before comparing them.</p>
</div>

<div class="callout warn">
<span class="label"><code>Kappa</code> and <code>Xj</code> need <code>Nsub</code></span>
<p>Both are built from the substrate depletion width, and nothing but the doping supplies one. State
either without <code>Nsub</code> and it is carried, read, and inert — the import reports this when it
sees it, but a hand-edited device will not tell you.</p>
</div>

**Subthreshold conduction (`Nfs`) and impact ionisation are still absent**, exactly as at level 1.
The current goes to zero at threshold in both.

##### n-channel (MOS3_N) {#mos3n}

{{symbol: mos3-n}}

{{table: components/Mos3N}}

##### p-channel (MOS3_P) {#mos3p}

{{table: components/Mos3P}}

### The vertical power MOSFET {#vdmos}

**NVDMOS** and **PVDMOS** — a *separate component* from the lateral MOS pair, not a setting of it.
**Three** pins, not four: the source-to-body short is inside the silicon, and that is exactly what
turns the substrate junction into a **body diode** between source and drain.

Three things a power MOSFET is chosen for are absent from the lateral model, and every one is what a
user is asking about when they reach for this part.

**The body diode is a component of the circuit, not a leakage path.** It is the freewheeling diode of
every half-bridge and the conduction path of every synchronous rectifier during dead time, and it
carries the full load current there. So it has its own saturation current, its own reverse recovery
charge (`Tt`) and its own **avalanche breakdown** (`Bv`) — a rated mode for this part, not a failure —
and its current is reported on its own branch, `I:M1:body`.

**The gate-drain capacitance collapses with drain bias, by one to two orders of magnitude.** That
collapse *is* the switching loss: the Miller plateau is the gate charge pouring into it while the
drain swings. `Cgdmax` and `Cgdmin` are the two ends of a data sheet's reverse-transfer curve and
`Vgdt` is how sharply it falls between them. A constant capacitance of either plateau value gets the
switching time wrong by the ratio of the two.

**The gate resistance `Rg` is in the drive path**, in series with a capacitance that large, so it sets
the switching speed as much as the drive current does.

<div class="callout note">
<span class="label">Third-quadrant conduction is not an edge case here</span>
<p>Pull the drain below the source with the gate <i>on</i> and the channel conducts in reverse,
shunting the body diode, so the drop is <code>I·Rds(on)</code> rather than a diode drop. That is
synchronous rectification, and it is what the part is bought for. circuitRF decides which end is
acting as the drain from the <b>bias</b> rather than from the schematic, so this comes out right
without anything being configured.</p>
</div>

<div class="callout warn">
<span class="label">What is NOT modelled</span>
<p><strong>Quasi-saturation</strong> — the drift region's own resistance modulating with current
needs a second internal node and a drift-region model this does not have; <code>Rd</code> stands in
for its low-current limit. <strong>Subthreshold conduction</strong> — the off-state leakage is
<code>Rds</code>, which you state. <strong>Self-heating</strong>, which for a power device is a real
omission: the junction temperature is a parameter, so a thermal model belongs around the part rather
than in it.</p>
</div>

#### n-channel (VDMOS_N) {#vdmosn}

{{symbol: vdmos-n}}

The bulk arm turns and joins the **source** lead instead of leaving as a fourth pin — that is the
source-to-body short — and the body diode is drawn explicitly on the right, conducting from source to
drain.

{{table: components/VdmosN}}

#### p-channel (VDMOS_P) {#vdmosp}

{{symbol: vdmos-p}}

{{table: components/VdmosP}}

### The IGBT {#igbt}

**NIGBT** and **PIGBT** — an insulated-gate channel driving the base of a wide-base bipolar
transistor. That structure *is* the model, and it is what gets the device's two defining behaviours
right without either being fitted.

**The on-state voltage has a junction drop in it.** Current leaving the collector crosses the
bipolar's emitter-base junction on its way in, so `Vce(sat)` never falls below roughly a diode drop
however hard the gate is driven. That is the whole trade against a power MOSFET — worse at low
current, better at high, because the drop then stops growing.

**Turn-off has a current tail.** The charge stored in the wide base cannot be removed through the
gate; it recombines. `Tau` is that stored charge, and the tail is most of the turn-off loss. How the
current divides between the channel and the bipolar is set by `Bf`, and it is the bipolar's share
that is still flowing after the gate is off — `I:Q1:imos` and `I:Q1:ic` report the two separately.

**`Bv` is forward *break-over*, across the drift region** — the `V_CES` rating. Note the difference
from the [power MOSFET](#vdmos)'s `Bv`, which is an **avalanche** rating and a mode the part is
designed to survive: an IGBT's is a limit, and past it the drift region conducts and turns the
bipolar on with it. `Bv = 0` means not modelled, as everywhere else.

<div class="callout warn">
<span class="label">An IGBT does not conduct in reverse</span>
<p>That is structural rather than something switched off: with the collector below the emitter the
bipolar's junction is reverse-biased and there is no path. It is the opposite of the
<a href="#vdmos">power MOSFET</a>, whose body diode freewheels — and it is exactly why an IGBT
half-bridge needs a discrete anti-parallel diode and a MOSFET one does not. <b>Place one if the
circuit has one.</b></p>
</div>

<div class="callout warn">
<span class="label">Its parameters are data-sheet quantities, and a transport-model card cannot be imported</span>
<p>This is an <b>equivalent-circuit</b> model: its base is a lumped transit time rather than a solved
carrier distribution, so there is no moving depletion boundary, no conductivity modulation of the
drift region and no latch-up. Its parameters are therefore a threshold, a transconductance, a current
gain and a transit time — things a data sheet gives.</p>
<p>A <code>.model</code> card written for the published <b>ambipolar transport</b> model states
something else entirely: base width, doping, carrier lifetime, mobility. Neither set can be derived
from the other by renaming — that is a device-modelling extraction, not a mapping — so such a card is
<b>refused by name</b> rather than being given this device's defaults under the card's own name.
Enter the data sheet's numbers here, or run the card's own model through
<a href="#veriloga">VerilogA</a>.</p>
</div>

#### n-channel (IGBT_N) {#igbtn}

{{symbol: igbt-n}}

The input side is the MOS one — a gate bar standing off a broken (enhancement) channel bar — and the
output side carries the **bipolar's** emitter arrow. The arrow is not decoration: it is what stops a
reader taking this for a power MOSFET and expecting a body diode that is not there.

{{table: components/IgbtN}}

#### p-channel (IGBT_P) {#igbtp}

{{symbol: igbt-p}}

{{table: components/IgbtP}}

### Ideal Mixer (Mixer) {#mixer}

{{symbol: mixer}}

A three-port ideal mixer: what comes out of the IF port is the **product** of what goes into the RF
and LO ports. RF on the left, LO underneath, IF on the right — the three leads are not
interchangeable, which is why each one carries its name.

`v_IF (open circuit) = K · v_RF · v_LO`

You never type `K`. You state a conversion gain and the LO drive it holds at — `ConvGain` = −7 dB at
`Plo` = +7 dBm, straight off a datasheet — and circuitRF derives the multiplier constant from them,
using each port's impedance. `ConvGain` is a **single-sideband power** gain: negative is a loss.

**Both sidebands come out.** A product of two cosines is half the sum plus half the difference, so an
RF tone at 2 GHz against a 1.8 GHz LO puts equal power at 200 MHz *and* 3.8 GHz. The mixer does not
pick one for you; a single-sideband result comes from filtering what leaves the IF port, or from an
image-reject network built around two mixers — exactly as it does in hardware.

**Conversion gain tracks LO amplitude.** That is what a multiplier does, and it is why the gain is
quoted together with `Plo`. Drive the LO 3 dB harder than `Plo` and the conversion gain rises 3 dB;
drive it 3 dB softer and it falls. If the LO in your test bench is not delivering `Plo` to the LO
port, the mixer is not running at the gain you typed.

<div class="callout note">
<span class="label">Why it is a multiplier and not a switch</span>
<p>A real diode or FET mixer <b>commutates</b>: the LO switches the RF path rather than scaling it,
which is why a real mixer's conversion loss barely moves once the LO is hard enough to switch. An
ideal switching mixer is not expressible here — a component's law is a memoryless function of its
port voltages, and hard switching has a derivative no Newton step survives. The product is the ideal
mixing law that <i>is</i> expressible, and its LO dependence is stated above rather than hidden.</p>
</div>

#### Non-idealities

A freshly-placed mixer is ideal: the three isolations default to 200 dB and `IIP3` to 100 dBm, which
mean *none* and *never compresses*. Type a real number into any of them to turn that non-ideality on.

| Parameter | What it does |
|---|---|
| `Zrf` `Zlo` `Zif` | Each port is this resistance to its reference, and the IF output sits behind `Zif`. Change one and that port is mismatched — the first non-ideality most circuits notice. |
| `IsoLO_RF` | LO leaking backwards out of the RF port. It is a real voltage at a real port, so it mixes with the LO like anything else there. |
| `IsoLO_IF` | LO feedthrough at the IF port — usually the one that sets an IF filter's job. |
| `IsoRF_IF` | RF straight through to IF, unconverted. |
| `IIP3` | Input-referred third-order intercept at the RF port. Sets compression and IM3 together, through a soft limiter whose third-order term matches the intercept exactly. |

The limiter is a `tanh`, not the textbook `a₁x − a₃x³`. A bare cubic turns over and goes **negative**
past its peak, and harmonic balance then finds that root and converges cleanly onto a wrong answer.
`tanh` is monotone everywhere and has the same third-order term.

<div class="callout note">
<span class="label">What it does in an S-parameter run</span>
<p>It reports the <b>port matches and the three leakages, and no conversion at all</b> — and that is
the right answer rather than a missing one. S-parameters are a single-frequency measurement, and
conversion is the business of moving energy <i>between</i> frequencies. The arithmetic says the same
thing: circuitRF linearises a nonlinear device at its DC operating point, and the mixer's RF-to-IF
small-signal gain is proportional to the LO voltage, which at DC is zero. So an S-parameter sweep of
a mixer is a useful measurement of the thing S-parameters can measure — how well each port is
matched, and how much leaks between them.</p>
<p><b>Conversion gain comes from harmonic balance.</b> Drive the RF and LO ports as two tones, and
read the IF power at the mixing product you want. For conversion gain <i>versus</i> frequency, wrap
that harmonic-balance analysis in a <a href="simulations.html#parametric-sweep">parametric sweep</a>
of the RF frequency — that is the swept measurement an S-parameter run cannot be.</p>
</div>

The mixer is also a **System** block, and the class it belongs to — what the ideal blocks in a
system diagram can and cannot answer — is covered in
[System Components](system-components.html).

{{table: components/Mixer}}

### Differential Mixer (MixerD) {#mixerd}

{{symbol: mixer-d}}

The **same component** as [Mixer](#mixer) — same equations, same parameters, same everything — with
all six of its nets brought out as pins instead of three. Use it when a port's return is not ground.
Otherwise the single-ended tile is the identical circuit with three fewer wires to draw.

The pins are in ± pairs: `rf+` `rf−` on the left, `lo+` `lo−` along the bottom, `if+` `if−` on the
right. Swapping a pair inverts that port's voltage, which gives a circuit that still solves and is
wrong — so read the marks.

The box body is not a second opinion about what a mixer looks like: six leads cannot land on a
circle's edge on the connection grid. The ✕ is kept, and it is the whole of the family resemblance.

{{table: components/MixerD}}

### Compiled Verilog-A model (VerilogA) {#veriloga}

{{symbol: verilog-a}}

A compact model **you** supply: point it at Verilog-A source (`.va`, `.vams`) or an already-compiled
model (`.osdi`) and circuitRF runs it. No kit, no manifest, nothing to install. It is variadic — the
model decides how many terminals it has, so `Pins` sets how many the symbol draws. The body is
deliberately generic, because circuitRF does not know what the model *is*: drawing a transistor glyph
would assert something the file has not said. The **leads are named by the model** once the file has
been read, and numbered before that.

<div class="callout note">
<span class="label">This component has its own chapter</span>
<p>Installing a compiler and pointing circuitRF at it, the source-to-simulation walkthrough, loading a
fitted parameter set, the thermal terminal and when to leave it off, and exactly when circuitRF
rebuilds, are in <b><a href="veriloga.html">Compiled Verilog-A Models</a></b>. The entry here is the
glyph, the pins and the parameter table.</p>
</div>

{{table: components/VerilogA}}

### Composed FET model (ComposedFET) {#composedfet}

{{symbol: composed-fet}}

A transistor model built from measured or simulated device data, placed from its `.cmodel` file. The
[Model Composer](model-composer.html) writes one from data, and [Writing a .cmodel](cmodel.html) describes the file
for a script of your own:

```
ComposedFET:X1  g d s  File="gan_8x125.cmodel"  N=8
```

It simulates as the circuit it is: the model's extrinsic network as ordinary resistors, inductors,
capacitors and mutuals, and one nonlinear core on the intrinsic nets `gi`, `di` and `si`. So `V(X1.gi)`
is the intrinsic gate voltage, and each network element has a path of its own (`X1.rg`). `File` is
resolved the way an SnP's is.

`N` is the finger count; blank is the model's own. The core scales by `N/N0`, and each network element
by its rule — fixed, per finger, or inversely per finger. `m` places whole devices in parallel, so
`m=2 N=8` is two 8-finger devices.

A model over a temperature range follows the design's `temp`, held to that range. `Temp` (°C) holds one device at its
own temperature instead — the baseplate or heatsink it is mounted on — while the rest of the design stays at `temp`:

```
ComposedFET:X1  g d s  File="gan_8x125.cmodel"  N=8  Temp=85
```

A model extracted at one temperature is used as it is at any other, with one warning naming both.

#### The thermal pin, T {#composedfet-thermal-pin}

**T is neither an input nor an output.** It is a thermal *node*, wired exactly like an electrical one, but it
carries temperature and heat instead of voltage and current:

| On the T node | Means |
|---|---|
| its voltage, `V(X1.t)` | the device's temperature rise above `Temp` (or `temp` when that is blank), in °C |
| the current into it | heat, in W: the device puts its own dissipated power in |
| what sits between it and ground | the path that heat takes away |

The model's own thermal network (`Rth` in parallel with `Cth`, or its ladder) is already inside the device, from
`t` to ground. Currents and charges are evaluated at `Temp` plus the rise. So you **read** T to see how hot the
channel runs, and you **load** T to change how the heat leaves.

**When the T pin is there.** Only a model that self-heats has a thermal node, so only its symbol has the pin. A
freshly dropped ComposedFET has no `File` yet and shows three pins. **To get the fourth pin, set `File` to a
self-heating `.cmodel`** (one exported with self-heating on in the Model Composer's Thermal step). The standard
symbol then gains the lead on the right marked **T**, starting at a small open circle. Choosing a model that does not
self-heat takes the pin away again. A library symbol, such as `FetCurtice`, has only three pins whatever the model is.

**Left unwired**, T is no connection, and the device keeps its own thermal network. Its end shows an open box
rather than the red unconnected marker, because leaving it open is a valid choice. That is also the usual one:
you can plot `V(X1.t)` without wiring anything.

**Wired**, whatever you attach sits in parallel with the model's own network. A heatsink or mounting that adds its
own path to ambient is a resistor (°C/W) from T to ground:

```
ComposedFET:X1  g d s t  File="gan_8x125.cmodel"
R:RSINK  t 0  R=20
```

Here 20 °C/W in parallel with the model's own `Rth` lowers the device's total thermal resistance. Pin 4 is written to
the netlist only when something is wired to it. Binding four nets to a model that does not self-heat is refused at
Run.

To change the device's own thermal network instead of adding to it, use `Rth` (°C/W) and `Cth` (J/°C). They set
this device's thermal resistance and capacitance at its finger count, replacing the model's own values scaled to
`N`; blank keeps the model's. A model with a ladder of thermal poles scales every pole by the same factor. A model
that does not self-heat refuses them. The `SelfHeating` switch set to `false` keeps the device at its `Temp` with no
rise, and T, if wired, then sees none.

```
ComposedFET:X1  g d s  File="gan_8x125.cmodel"  N=8  Rth=40  Cth=2.5e-6
```

#### The symbol

The `Symbol` row in the Inspector is a drop-down list of the symbols this model can be drawn with:

- the first entry, which is blank in the file. It means the model's own symbol, or the standard FET symbol when the
  model names none, and it reads **FET (standard, with T pin)** for a self-heating model.
- the library FET symbols, whose pins are gate, drain and source, such as `FetCurtice` and `FetAngelov`.
- every `.csym` in the workspace whose pins fit: pins 1, 2 and 3 are `g`, `d` and `s`, and a pin 4 (the thermal node
  `t`) only for a model that self-heats.

The list follows `File`, so a `.csym` path is written relative to the `.cmodel`. A symbol that cannot be found draws
as the standard FET, with one warning.

Ten switches turn one part of the model off on one instance — the intrinsic charge, its nonlinearity (all of it,
or one capacitance at a time), gate lag, drain lag, self-heating and dispersion — without refitting anything:
[Switching parts of the model off](model-composer.html#switches).

{{table: components/ComposedFET}}

## System-level blocks

The blocks a **system block diagram** is drawn out of: the level above a transistor, where a signal
path is a chain of named boxes rather than a circuit. They share the mixer's drawing grammar — a
block reads left to right, inputs on the left and outputs on the right, and a block whose leads are
not interchangeable labels them, because connecting the wrong one gives a circuit that solves and is
wrong. Every one of them is in the palette's **System** filter, along with the two mixer tiles.

<div class="callout note">
<span class="label">These blocks have their own chapter</span>
<p>What each block is for, what "ideal by construction" costs you, which analysis answers which
question, and the whole of the passive-intermodulation story — the datasheet conversion, which
blocks can carry it, and how to give one to a block that cannot — are in
<b><a href="system-components.html">System Components</a></b>. Read that before placing one; the
entries below are the glyph, the pins and the parameter table.</p>
</div>

### Balun {#balun}

{{symbol: balun}}

A transformer between a single **unbalanced** port and a **balanced** pair. `UNB` is on the left;
`BAL+` and `BAL−` are on the right, and the `+`/`−` marks say which is which — swapping them inverts
the balanced signal, which is a circuit that still solves. `Zbal` is the impedance of **each**
balanced port to ground, so the differential impedance across the pair is twice it.

In depth: [System Components › Balun](system-components.html#balun).

{{table: components/Balun}}

### Circulator {#circulator}

{{symbol: circulator}}

A three-port non-reciprocal junction: power entering port 1 leaves at port 2, power entering port 2
leaves at port 3, and power entering port 3 leaves at port 1. **The arrow inside the circle is drawn
from the `Direction` parameter** — `CW` circulates 1 → 2 → 3 → 1 and `CCW` reverses it — so which way
a circulator turns is read off the schematic rather than out of a dialog. Terminate the unused port
and it is an isolator. It can carry a passive-intermod specification.

In depth: [System Components › Circulator](system-components.html#circulator).

{{table: components/Circulator}}

### Switch {#switch}

{{symbol: switch}}

An SPST switch. Its two pins are interchangeable, so they carry no names. **The blade is drawn in the
position `State` sets** — lifted at `0`, closed at `1` — which is what makes a swept `State`
readable on the page.

In depth: [System Components › Switch](system-components.html#switch).

{{table: components/Switch}}

### Transfer Switch (SwitchD) {#switchd}

{{symbol: switch-d}}

An SPDT switch: one common port on the left and two throws on the right. `State` selects the throw —
`1` or `2`, or `0` for both open — and the blade in the symbol points at it. Both switch tiles use
the `SW` instance prefix, so swapping one for the other does not renumber a schematic.

In depth: [System Components › Transfer Switch](system-components.html#switchd).

{{table: components/SwitchD}}

### Amplifier (Amp) {#amp}

{{symbol: amp}}

An ideal amplifier: `IN` on the left, `OUT` on the right. Nothing is drawn inside the triangle
because the gain belongs where a reader looks for a number — the label beside the symbol. It has no
bias pins and consumes no DC power, and one third-order intercept sets its intermodulation and its
compression together.

In depth: [System Components › Amplifier](system-components.html#amp).

{{table: components/Amp}}

### Directional Coupler {#coupler}

{{symbol: coupler}}

Four ports in the order a coupler is always specified: **1** in, **2** through, **3** coupled, **4**
isolated. The two arms run straight through the body, because that is what a coupler is — two lines
that happen to be close to one another. The arrow does real work: it is the whole of what separates
the coupled port from the isolated one, and a coupler drawn without it is ambiguous in exactly the
way that produces a silently wrong circuit.

In depth: [System Components › Directional Coupler](system-components.html#coupler).

{{table: components/Coupler}}

### 90° Hybrid (Hybrid90) {#hybrid90}

{{symbol: hybrid90}}

The same component as the coupler above, at 3.01 dB with the coupled port in quadrature. Same body,
same pins, same arrow; the phase written inside the frame is the difference, because the phase *is*
the difference.

In depth: [System Components › 90° Hybrid](system-components.html#hybrid90).

{{table: components/Hybrid90}}

### 180° Hybrid (Hybrid180) {#hybrid180}

{{symbol: hybrid180}}

The rat race: again the same component, at 3.01 dB with the coupled port in anti-phase — a sum port
and a difference port. Both hybrids share the `HYB` instance prefix with each other, and all three
tiles share one engine component.

In depth: [System Components › 180° Hybrid](system-components.html#hybrid180).

{{table: components/Hybrid180}}

### Filter {#filter}

{{symbol: filter}}

A two-port filter synthesised from a prototype — Butterworth, Chebyshev, inverse Chebyshev, Bessel,
Gaussian or elliptic, as lowpass, highpass, bandpass or bandstop. **Its symbol is the [Match](#match)
symbol** — the same picture, not a related one. Impedance matching is a form of filtering, the two
are built out of the same idea, and the library says so. The three stacked waves read as a frequency
axis with the highest at the top, and a slash is struck through every wave the network blocks,
following the `Form` parameter:

| `Form` | Struck | Passes |
|---|---|---|
| `Lowpass` | the top two | the lowest |
| `Bandpass` | the top and the bottom | the middle |
| `Highpass` | the bottom two | the highest |
| `Bandstop` | the middle | the lowest and the highest |

The [Filter Designer](filter-designer.html) builds the LC ladder for the all-pole families and can
snap each value onto a standard series or a library part. This block stays the ideal response.

Tell a filter from a matching network by its type label and its instance name — `FLT1` against `MN1`
— the same way the five FET laws, which also share one glyph, are told apart. `Order` is the
**prototype** order, so a 3rd-order bandpass is a 6th-degree network.

In depth: [System Components › Filter](system-components.html#filter).

{{table: components/Filter}}

### Attenuator {#atten}

{{symbol: atten}}

A fixed pad. Its two pins are interchangeable, so they carry no names, and the pinched bowtie reads
as "signal made smaller". The loss shows as the label beside the symbol. With a small loss and a
passive-intermod specification it is a **PIM generator** you can place in front of anything.

In depth: [System Components › Attenuator](system-components.html#atten).

{{table: components/Atten}}

### Duplexer {#duplexer}

{{symbol: duplexer}}

An antenna port that splits into a transmit branch and a receive branch, each through its own
passband — which is what the glyph draws: one junction fanning into two filters, each wave stack
labelled with the arm it belongs to. `ANT` is on the left, `TX` and `RX` on the right. It takes a
complete filter specification per arm, and its isolation is a consequence of the two responses
meeting at one node rather than a parameter.

In depth: [System Components › Duplexer](system-components.html#duplexer).

{{table: components/Duplexer}}

## Annotation components

### Variables (VAR) {#var}

{{symbol: var}}

A port-less annotation holding `name = expression` variable definitions, scoped to where it is placed
— global at the test bench top, or local inside a cell. Variables are sweepable. Edit the rows in the
multi-line text editor by double-clicking the VAR. No fixed parameters.

### Measurements (MEAS) {#meas}

{{symbol: meas}}

A port-less annotation holding `name = expression` measurement equations, evaluated after a run — for
example `Pout_dBm = 10*log10(...)`. Measurements attach at the top test-bench level only. Edit the rows
in the same multi-line editor as VAR. No fixed parameters.

How to write one: [Measurements](measurements.html) covers reading an analysis's results (nodes,
probes, harmonics, sweeps and frequency), and [Expressions](expressions.html) covers the language
itself: [operators](expressions.html#operators), [functions](expressions.html#constants) and
[units](expressions.html#units). The editor's **Help** button opens the first of these.

---

## The full component list

{{table: components}}

---

See also: [Dynamic symbols (SDD / ZPort / SnP)](dynamic-symbols.html) ·
[Nonlinear Capacitor & C–V Editor](nonlinear-capacitor.html) ·
[Pins, Ports & Terms](pins-ports-terms.html) · [Netlist format](netlist.html).
