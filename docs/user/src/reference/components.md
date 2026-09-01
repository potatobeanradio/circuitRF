---
title: Components
slug: reference/components.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Components
lede: Every component in the standard library — its symbol, what it does, and its parameters.
---

Symbols are rendered from the live drawing engine, with their connection leads and their pins shown
unconnected — the component as you meet it in the palette, before anything is wired to it. Parameter
tables are read from the component registry, so the defaults, units and on-schematic visibility below
are the as-placed values by construction rather than by transcription.

<div class="callout note">
<span class="label">Reading the parameter tables</span>
<p><strong>Name</strong> is the parameter key, as it appears in the editor and in the netlist.
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

Either name may be an [L](#inductor), an [SRLC](#srlc) or a [PRLC](#prlc) — all three carry an
inductor the coupling can act on, and all three spell its value `L`. Naming anything else is
reported as an error when the design is elaborated.

{{table: components/Mutual}}

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

<div class="callout note">
<span class="label">An ideal current source is an open circuit</span>
<p>The output pair gets no conductance of its own, so — exactly as with ITone — an output node with
nothing else attached to it has no DC path to ground and the matrix is singular. Load it.</p>
</div>

{{table: components/Vccs}}

## Terminals & ports

### Ground (GND) {#ground}

{{symbol: ground}}

The global reference node (net `0`). No parameters.

### Term {#term}

{{symbol: term}}

An S-parameter port: a numbered, reference-impedance termination where the simulator excites and
measures. `Num` is the port index, auto-assigned at placement; `Z` is the reference impedance. See
[Pins, Ports & Terms](pins-ports-terms.html) for how it differs from a Pin.

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

### Tuner / SourceTuner / LoadTuner {#tuner}

{{symbol: tuner}}

A programmable RF termination used by loadpull and sourcepull. The three variants are the *same*
engine component with different glyphs and net ordering — match the symbol to its role.
**SourceTuner** sits on the source side, **LoadTuner** on the load side.

- `Z[1]` — the fundamental termination. Required, and it accepts complex literals such as `50+j*10`.
  Add `Z[2]`, … in the editor, or `G[k]` to give a reflection coefficient instead.
- `Zdefault` — the catch-all termination for harmonics not otherwise specified.
- `Z0` — the reference impedance for any `G[k]` entry.
- `BiasTee` — turns on the internal bias-tee and DC supply, which the loadpull directive requires.
- `Vbias` — the DC bias at the DUT-facing port when the bias-tee is on.
- `ShowBias` — display only: draws the bias-tee on the glyph. It never reaches the engine.

{{table: components/Tuner}}

{{symbol: source-tuner}}

{{symbol: load-tuner}}

## Transmission lines

### Ideal Transmission Line (TLIN) {#tline}

{{symbol: tline}}

An ideal, lossless transmission line specified by characteristic impedance `Z` and electrical length
`E` at a reference frequency `F`.

{{table: components/Tline}}

### Microstrip Line (MLIN) {#mlin}

{{symbol: mlin}}

A physical microstrip line on the current technology's substrate: `W` is the conductor width and `L`
the length. Unlike TLIN, its impedance and loss follow from the geometry and the substrate rather than
being stated.

{{table: components/Mlin}}

### Microstrip Bend (MBEND) {#mbend}

{{symbol: mbend}}

A right-angle microstrip bend of width `W`, optionally mitred, modelled with its discontinuity rather
than as an ideal corner.

{{table: components/MBend}}

### Microstrip T-Junction (MTEE) {#mtee}

{{symbol: mtee}}

A three-port microstrip T-junction. `W1`, `W2` and `W3` are the widths of the three arms.

{{table: components/MTee}}

### Microstrip Cross-Junction (MCROSS) {#mcross}

{{symbol: mcross}}

A four-port microstrip cross-junction, with a width per arm.

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

## Data-file components

### Touchstone N-Port (SnP) {#snp}

{{symbol: snp}}

An N-port backed by a Touchstone (`.sNp`) file — measured or modelled S-parameters embedded in the
circuit and interpolated onto the analysis sweep.

- `NumPorts` — the port count. Hidden; it drives the symbol and the pin count.
- `File` — the path to the Touchstone file.
- `RefNode` — when true, expose a floating common reference pin (N+1 nets) instead of grounding each
  port.
- `PinConfig` and `Pitch` — the pin arrangement and spacing on the symbol.
- `InterpMode`, `InterpDomain` and `ExtrapMode` — how the data is interpolated between points, in
  which domain, and what happens beyond its frequency range.

The symbol is dynamic; the pin arrangement, the pitch and the floating-reference option are covered in
[Dynamic symbols](dynamic-symbols.html#snp).

{{table: components/Snp}}

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

### The FET family {#fets}

Five native large-signal FET models, one per published drain-current law. They are five separate
components rather than one component with a model selector, because they are not variants of one
another: each has its own parameter set, and several reuse a spelling for a different quantity — the
quadratic law's `Beta` is a transconductance parameter, while the cubic law's is a gate-voltage shift
with drain bias. One kind with a selector would present the union of all five parameter sets and
silently accept the wrong ones.

All five **share one glyph and one three-pin geometry** — gate left, drain top, source bottom. The
topology genuinely is the same, and the type label below the symbol names the law. **The source is an
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

A compiled Verilog-A model **you** supply: point it at a compiled model file and circuitRF runs it. No
kit, no manifest, nothing to install. It is variadic — the model decides how many terminals it has, so
`Pins` sets how many the symbol draws. The body is deliberately generic and the terminal numbers are
drawn on it, because circuitRF does not know what the model *is*: drawing a transistor glyph would
assert something the file has not said.

{{table: components/VerilogA}}

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

A two-port filter synthesised from a prototype — Butterworth, Chebyshev, inverse Chebyshev, Bessel
or elliptic, as lowpass, bandpass or highpass. **Its symbol is the [Match](#match) symbol** — the
same picture, not a related one. Impedance matching is a form of filtering, the two are built out of
the same idea, and the library says so. The three stacked waves read as a frequency axis with the
highest at the top, and a slash is struck through every wave the network blocks, following the
`Form` parameter:

| `Form` | Struck | Passes |
|---|---|---|
| `Lowpass` | the top two | the lowest |
| `Bandpass` | the top and the bottom | the middle |
| `Highpass` | the bottom two | the highest |

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

---

## The full component list

{{table: components}}

---

See also: [Dynamic symbols (SDD / ZPort / SnP)](dynamic-symbols.html) ·
[Nonlinear Capacitor & C–V Editor](nonlinear-capacitor.html) ·
[Pins, Ports & Terms](pins-ports-terms.html) · [Netlist format](netlist.html).
