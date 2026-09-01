---
title: System Components
slug: reference/system-components.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > System components
lede: The ideal blocks a system diagram is drawn out of — circulator, coupler, hybrid, balun, switch, amplifier, attenuator, filter, duplexer and mixer. What each one is for, what "ideal" costs you, and where passive intermodulation comes from.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What these blocks are, and what they are not</a></li>
<li><a href="#instead">What to reach for instead, and when</a></li>
<li><a href="#running">Which analysis answers which question</a></li>
<li><a href="#pim">Passive intermodulation</a></li>
<li><a href="#catalogue">The blocks, one by one</a></li>
</ol>
</nav>

## What these blocks are, and what they are not {#what}

A **system block diagram** is a level above a circuit. The signal path is a chain of named boxes —
a filter, a coupler, an amplifier, a mixer — and each box is described the way a datasheet describes
it: a coupling in dB, an isolation in dB, an intercept in dBm, a passband and a rejection. Nothing
in that description says what the part is made of, and for the questions the diagram is drawn to
answer, nothing needs to.

These components are those boxes. Every one of them is placed from the **System** filter in the
{{anchor: schematic-editor#palette|Library Palette}}, wired like any other component, and simulated
by the same engines. Together they answer:

- **Level plans** — what power is at each point in the chain.
- **Cascaded gain, and cascaded intercept** — the amplifier and the mixer each carry one, and a
  harmonic-balance run cascades them for you rather than through a spreadsheet formula.
- **Image and spurious paths** — what a mixer's other sideband lands on, and what the filter after
  it does about that.
- **Band plans and isolation budgets** — a duplexer's two arms, a circulator's reverse leakage, a
  coupler's directivity.
- **Switch-state coverage** — every position of every switch, in one run, because `State` is a
  swept parameter rather than a wire you move.

All of it **before any of the parts exist**. That is what the class is for.

### What they are not

They are **ideal by construction**, and every one of those idealisations is a place a real part will
disagree with the simulation:

- **Frequency-flat where a real part is not.** The circulator, coupler, hybrid, balun, switch,
  attenuator and amplifier hold every number you typed at *every* frequency, from DC upwards. An
  ideal 90° hybrid is in exact quadrature at 100 MHz and at 100 GHz alike, which no physical coupler
  is — a branchline holds its quadrature over perhaps 10–20% of bandwidth and its coupling over less.
- **Exactly matched, and exactly isolated, unless a number says otherwise.** `RL`, `Isolation` and
  `Directivity` default to 200 dB, which does not mean "200 dB" — it means the term is **absent**.
  Nothing is stamped into the matrix at all, so the reverse path of a default circulator is not
  small, it does not exist. Type a real number to turn a non-ideality on.
- **Lossless except where a loss is typed in.** `IL` and `Loss` are the only dissipation in the
  family. There is no skin effect, no dielectric loss, no radiation.
- **No spread, no temperature, no power, no noise.** No manufacturing tolerance, no drift, no DC
  supply current, no bias pins, and no noise figure. An ideal amplifier is an ideal amplifier at any
  temperature and consumes nothing.

<div class="callout note">
<span class="label">"Ideal" means the term is absent, not small</span>
<p>This is a convention shared by every block here and by the
<a href="components.html#mixer">Mixer</a>. A non-ideality is switched off by an honestly large
number — 200 dB of isolation, 200 dBm of intercept, &minus;200 dBm of intermod — and the model snaps
that to <em>exactly</em> ideal rather than stamping a 10<sup>&minus;10</sup> entry. A freshly placed
block is therefore exactly linear, exactly matched and exactly isolated, and a measurement of it
comes back at the solver's own floor rather than at a number you did not choose.</p>
</div>

## What to reach for instead, and when {#instead}

The moment the question is about **bandwidth, dispersion, or a real part's own behaviour**, an ideal
block is the wrong instrument. It will answer, and the answer will be the idealisation you asked
for. What to place instead:

| The question | What to place |
|---|---|
| How does this coupler behave across the band? | Four quarter-wave arms — <a href="components.html#tline">TLIN</a> for an ideal line, <a href="components.html#mlin">MLIN</a> and the microstrip junctions for a real one |
| What does the *actual* part I bought do? | <a href="components.html#snp">SnP</a> — its measured or simulated Touchstone file |
| What does this piece of layout do? | An <a href="em-setup.html">EM extraction</a> of the artwork, through the <a href="mom-engine.html">planar method-of-moments solver</a> |
| What does this transistor do? | The <a href="components.html#fets">FET family</a>, the <a href="components.html#sdd">SDD</a>, a <a href="components.html#veriloga">compiled Verilog-A model</a>, or a <a href="pdk-integration.html">kit</a> |
| What matching network gets me there? | The <a href="match.html">Match component</a>, which synthesises one |
| What does a real filter's loss and shape cost me? | A synthesised <a href="match.html">Match</a> ladder, a Touchstone file, or an EM run of the physical filter |

None of that makes the ideal block a waste of time — it is the other way round. A level plan built
out of ideal blocks tells you what each part has to *achieve*, and that is the specification you
then go and meet with a real one.

## Which analysis answers which question {#running}

Two analyses, and the split is not arbitrary:

- **{{anchor: simulations#s-parameters|S-parameters}}** answer everything that is a property of one
  frequency at a time: port match, isolation, coupling, directivity, a filter's passband and
  rejection, a duplexer's arm-to-arm leakage, an amplifier's small-signal gain and stability. This
  is the analysis nine of the eleven blocks are entirely described by.
- **{{anchor: simulations#harmonic-balance|Harmonic balance}}** answers everything that moves energy
  between frequencies or depends on level: conversion gain, mixing products, compression, third-order
  intermodulation, and passive intermod. Drive it with two tones
  ({{anchor: simulations#two-tone|multi-tone harmonic balance}}) when the question is an intercept.

<div class="callout note">
<span class="label">An S-parameter run cannot see a device convert</span>
<p>This is worked through once, for the mixer, under
<a href="components.html#mixer">Components &rsaquo; Ideal Mixer</a> — and the argument is the same
for every nonlinear block here. S-parameters are a single-frequency small-signal measurement about a
DC operating point; conversion, compression and intermodulation are none of those things. What an
S-parameter sweep of a mixer reports — the port matches and the leakages — is the right answer to
the question it was asked, not a missing one.</p>
</div>

## Passive intermodulation {#pim}

A passive part is supposed to be linear. Real ones are not quite: a metal-to-metal contact, a
ferrite, a corroded joint or a badly torqued connector all have a slightly nonlinear
current–voltage relation, and two strong carriers passing through one come out with **odd-order
products** around them. In a transmit chain those products land in the receive band, where nothing
downstream can filter them out, and the specification that keeps them there is called **PIM**.

Five blocks here can carry it: the {{anchor: components#atten|Attenuator}}, the
{{anchor: components#circulator|Circulator}}, the
{{anchor: components#coupler|Directional Coupler}} and both hybrids. Each gains two parameters,
`PIM` and `PIMPc`, which are **one specification in two fields**.

### What PIM is here

A **deterministic, memoryless nonlinearity** — not a random or noise-like process. Two carriers in,
a third-order product out at 2f₁ &minus; f₂ and 2f₂ &minus; f₁, at exactly the level you specified,
every run. It is generated on the wave **incident** at each port and then routed by the block's own
S-matrix, which is what a datasheet's number describes: a product born where the signal arrives, and
then carried out of whichever ports the block carries things out of. On a circulator that means the
product appears at the port the carriers go to, suppressed at the isolated one by the block's own
isolation — routing you get for free rather than by tuning.

### How it is specified

`PIM` is the **absolute level of the third-order product**, in dBm, and `PIMPc` is the **power per
carrier it was measured at**, in dBm. Both, always: a product level means nothing without the
carriers it was measured against.

Suppliers quote it both ways, and the two are the same number differently dressed:

```
   product (dBm)  =  carrier (dBm)  −  product (dBc)
```

So a part specified at **&minus;153 dBc with two +43 dBm carriers** — 20 W each, the usual test —
is a part whose product sits at

```
   43 dBm  −  153 dB  =  −110 dBm
```

and you type `PIM = -110 dBm`, `PIMPc = 43 dBm`. Turn it round to check a datasheet the other way:
a part quoted at &minus;110 dBm against 2 × 43 dBm is a &minus;153 dBc part.

**Away from `PIMPc` the product rides the third power of drive.** Third order means 3 dB of product
per 1 dB of carrier, so 10 dB less carrier is **30 dB** less product, and the dBc figure improves by
20 dB. That is the whole reason the carrier power has to travel with the specification.

### Which blocks carry it, and why the others do not

A nonlinearity in circuitRF is a **memoryless** function of the port voltages: `i = f(v)`, evaluated
instant by instant, with no memory of what came before. That is a real constraint, and it decides
the list:

| Block | PIM | Why |
|---|---|---|
| Attenuator, Circulator, Coupler, Hybrid90, Hybrid180 | **yes** | Their ideal S-matrix is frequency-flat, so a memoryless law describes them exactly |
| Balun, Switch | no | Excluded by design — neither is a part PIM is specified on |
| **Filter, Duplexer** | **no, and it cannot be added** | Their whole purpose is frequency dependence. A rational transfer function *has* memory, and a memoryless nonlinearity cannot be bolted onto one inside a single component |

<div class="callout tip">
<span class="label">The workaround: a PIM generator you can put in front of anything</span>
<p>An <a href="components.html#atten">Attenuator</a> with a small <code>Loss</code> and a PIM
specification is a <b>standalone PIM generator</b>. Place one in front of a filter, a duplexer, a
length of line, or anything else that cannot host PIM itself, and the products appear exactly where
a real bad connector would put them. This is a better answer than attaching a memoryless
nonlinearity to a rational response, and it is how a real chain is analysed anyway — the PIM comes
from the connector, not from the filter body.</p>
</div>

<div class="callout warn">
<span class="label">Not <code>Loss = 0</code> — give the pad a little loss</span>
<p>A perfectly matched 0 dB attenuator is a <b>wire</b>, and a wire has no admittance matrix at all:
<code>det(I + S)</code> is zero <em>exactly</em>, so there is no <code>i = f(v)</code> to write. The
block refuses by name rather than producing a NaN somewhere inside a Newton iteration. Give it a
small loss instead — the message says so — or a finite return loss.</p>
<p><b>How small is small enough?</b> The residual error in the product level, measured against the
level you asked for:</p>
<table>
<tr><th>Pad loss</th><th>&minus;153 dBc (a datasheet part)</th><th>&minus;100 dBc</th><th>&minus;90 dBc</th></tr>
<tr><td>0.01 dB</td><td>+0.0014 dB</td><td>+0.66 dB</td><td>+2.40 dB</td></tr>
<tr><td>0.1 dB</td><td>+0.0001 dB</td><td>+0.063 dB</td><td>+0.20 dB</td></tr>
<tr><td>1 dB</td><td>0.0000 dB</td><td>+0.0056 dB</td><td>+0.018 dB</td></tr>
<tr><td>3 dB</td><td>0.0000 dB</td><td>+0.0012 dB</td><td>+0.0039 dB</td></tr>
</table>
<p>At any level a passive part is actually specified at, 0.01 dB is already invisible; <b>1 dB of
loss — still an electrically negligible pad — removes the effect everywhere</b>. Use 1 dB unless the
loss itself matters to your level plan.</p>
</div>

### What turning it on costs

PIM is **off by default**, at `PIM = -200 dBm`, and anything at or below &minus;190 dBm counts as
off. Below that threshold the block is stamped as its exact linear S-matrix, costs nothing, and
contributes no nonlinear unknowns to a harmonic-balance solve.

Type a real number and **that block becomes a nonlinear component**. Two consequences:

- **A harmonic-balance run now carries it.** That is the point, and it is what an intermod
  measurement needs.
- **An S-parameter run solves a DC operating point first**, because that is what circuitRF does with
  any nonlinear device — it linearises about the operating point before sweeping. **What it reports
  does not change.** The distortion law has zero slope at zero signal, so the linearisation is the
  same matrix the linear block stamps: measured across eight block variants and five frequencies,
  the worst difference between a &minus;170 dBm and a &minus;40 dBm specification is below
  10<sup>&minus;12</sup> in S. Your match, isolation and coupling are exactly what they were.

### What the higher orders do

Fifth- and seventh-order products come out too, because the distortion law is a soft limiter
(`tanh`) with a series of its own — and **their levels are that limiter's fixed ratios, not a fit to
any measurement**. There is one scale parameter, set by the third-order figure you typed, and every
higher order follows from it. `PIM` specifies IM3 and nothing else.

Say it plainly: if you compare a simulated IM5 against a measured one and they disagree, that
agreement was never claimed. The same single-parameter idealisation shows in IM3 itself once the
drive gets close to the limiter's own scale — the product falls slightly **below** the ideal 3:1
extrapolation, by 0.06 dB at 30 dB below the equivalent intercept and 0.6 dB at 20 dB below it —
which is exactly how an intercept has to be read off a bench measurement too.

<div class="callout warn">
<span class="label">Known gap: a 90&deg; hybrid with PIM on, in a <em>multi-tone</em> run</span>
<p>The quadrature hybrid is the one PIM-capable block whose S-matrix is genuinely complex, and it
needs a frequency-domain factor that only the <b>single-tone</b> harmonic-balance solver honours
today. In a two-tone run — which is the analysis PIM exists for — that factor is dropped and the
block degenerates to four open circuits. The failure is loud rather than plausible: essentially
nothing reaches the through port and the source node reads about 6 dB high.</p>
<p><b>Every real-S block is unaffected and fully correct in multi-tone runs</b> — the attenuator, the
circulator, the in-phase coupler and the 180&deg; hybrid. For a quadrature hybrid, put the PIM on an
attenuator beside it instead, exactly as you would for a filter.</p>
</div>

## The blocks, one by one {#catalogue}

One entry each, in the order the palette lists them under **System**. The parameter tables live in
{{anchor: components|Components}}, one section per block, and each entry below links to its own.

### Mixer, and MixerD {#mixer}

{{symbol: mixer}}

An ideal three-port mixer: the IF port carries the **product** of the RF and LO ports, so both
sidebands come out and the conversion gain tracks the LO drive.

{{symbol: mixer-d}}

`MixerD` is the same component with all six nets brought out as pins, in ± pairs, for when a port's
return is not ground. Both are full members of the System family — they carry an intercept, three
isolations and per-port impedances.

They are documented in full, with the conversion-gain arithmetic and the non-ideality table, under
{{anchor: components#mixer|Components › Ideal Mixer}} and
{{anchor: components#mixerd|Differential Mixer}}. This entry is a pointer, not a second copy.

### Amplifier (Amp) {#amp}

{{symbol: amp}}

An ideal gain block: `IN` on the left, `OUT` on the right, `Gain` in dB and one third-order
intercept. **It has no DC power consumption and no bias pins**, by design — there is no supply, no
efficiency, no PAE and no thermal node. If those are the question, the answer is a real device model
and a harmonic-balance load-pull, not this block.

`IP3Ref` says whether the intercept you typed is **input-** or **output-**referred; the default is
Output, because that is the form a power amplifier's datasheet quotes, and `OIP3 = IIP3 + Gain` is
an identity so there is deliberately one field rather than two that could contradict each other.

**P1dB is not a separate knob.** One nonlinearity sets compression and intermodulation together, so
the 1 dB compression point follows from the intercept and lands at **IIP3 &minus; 8.96 dB**
input-referred. That is the soft limiter's own value, and it is not the textbook cubic's
&minus;9.64 dB — the two differ by two-thirds of a decibel, which matters if you are checking
against a hand calculation.

The amplifier is **unilateral** unless you turn `S12` on. With no reverse path there is no feedback
loop, which is what makes an ideal amplifier unconditionally stable at every frequency and every
termination; setting `S12` is what makes stability a question at all.

<div class="callout note">
<span class="label">Return loss does not change the gain</span>
<p><code>Gain</code>, <code>RLin</code>, <code>RLout</code> and <code>S12</code> are the four
entries of the block's S-matrix and each is exactly the number you typed. Mismatching a port does
not quietly re-scale S21, the way a Th&eacute;venin-source formulation would — a datasheet states
gain and return loss as independent measurements, and so does this block.</p>
</div>

Parameters: {{anchor: components#amp|Components › Amplifier}}.

### Attenuator (Atten) {#atten}

{{symbol: atten}}

A fixed pad. Two interchangeable pins, a `Loss` in dB, a return loss, and — because a pad is where
a connector usually is — an optional PIM specification. `Loss = 0` is a legitimate thing to place:
it is an ideal through, and it stamps and solves as one.

**With a small loss and a PIM figure it becomes a standalone PIM generator**, which is the
supported way to give a filter, a duplexer or anything else a passive-intermod contribution. See
[Passive intermodulation](#pim) above for the arithmetic and for why the loss must not be exactly
zero in that role.

Parameters: {{anchor: components#atten|Components › Attenuator}}.

### Balun {#balun}

{{symbol: balun}}

A transformer between one **unbalanced** port (`UNB`, on the left) and a **balanced** pair (`BAL+`
and `BAL−`, on the right), with `AmpImb` and `PhaseImb` for the two ways a real balun departs from a
perfect split.

**`Zbal` is per port.** It is the reference impedance of *each* balanced port to ground, so the
**differential** impedance across the pair is twice it — the 50/50 default is the ordinary 1:2
balun, 100 Ω differential presenting 50 Ω single-ended. As an impedance transformer the ratio is
`n = √(2·Zbal / Zunb)`, and a differential load `R` is seen at `UNB` as `R · Zunb / (2·Zbal)`.

<div class="callout note">
<span class="label">Ports 2 and 3 are not matched, and neither are a real balun's</span>
<p>A lossless reciprocal three-port <b>cannot</b> have all three of its ports matched — that is a
theorem, not an implementation limit — and a real balun does not isolate its balanced ports from
each other either. Read one at a time, <code>BAL+</code> and <code>BAL−</code> each show a
reflection; in the modal basis that is a clean <b>through</b> from <code>UNB</code> to the
differential mode and a total <b>reflection</b> for the common mode, which is exactly what an ideal
balun is. The unbalanced port does not couple to the common mode at all.</p>
<p><b>A consequence worth knowing before you wire one.</b> A resistor floating between
<code>BAL+</code> and <code>BAL&minus;</code>, with nothing else pinning them, says the same thing
the ideal common-mode open says, and the two together leave the common-mode potential undetermined
— a genuine floating node. Use <b>two half-value resistors with the tap grounded</b> instead: it is
the identical differential load, it pins the common mode, and it changes the answer not at all.</p>
</div>

If what you want is an **exact ideal transformer** with no common-mode behaviour to think about,
that is a two-port with unequal port impedances rather than a balun — a
{{anchor: components#filter|Filter}} with `Zin ≠ Zout` is exactly that inside its passband.

Parameters: {{anchor: components#balun|Components › Balun}}.

### Circulator {#circulator}

{{symbol: circulator}}

Three ports, and power goes round them one way: 1 → 2 → 3 → 1. **It is the only non-reciprocal
component in circuitRF** — `S21 ≠ S12` on purpose, and the whole point of the part. Terminate one
port and it is an isolator.

`Direction` is a parameter, and **it is drawn on the symbol**: `CW` circulates 1 → 2 → 3 → 1 and
`CCW` reverses it, and the arrow inside the circle follows. A schematic therefore never hides which
way a circulator turns — see {{anchor: dynamic-symbols#circulator|Dynamic symbols}}.

At the default `Isolation` the reverse entry is **not stamped at all**, so the forward/reverse ratio
is infinite rather than 200 dB, which is what makes a terminated circulator behave as a real
isolator rather than as a very good one. The ideal circulator's S-matrix has no impedance matrix
whatever — `det(I − S) = 0` exactly — which is why this family is stamped from the definition of S
rather than converted to Z. Its admittance does exist, and is
`(1/Z0)·[[0, 1, −1], [−1, 0, 1], [1, −1, 0]]`: antisymmetric, with a zero diagonal.

**Detuning the port match, in magnitude AND phase.** A real circulator is notoriously badly
matched, and what a power amplifier connected to port 1 actually feels is not a return loss but a
complex reflection — the same |Γ| at a different angle is a completely different load. `VSWR1` with
`Ang1` set port 1's own reflection directly, and `VSWR2`/`Ang2`, `VSWR3`/`Ang3` do the same for the
other two:

```
S_pp = ((VSWRp − 1) / (VSWRp + 1)) ∠ Angp
```

so an amplifier on port 1, with the other two ports matched, sees exactly `Z0·(1 + Γ)/(1 − Γ)`.
**`VSWR = 1` means "not stated"** and that port falls back to `RL`, so the datasheet form — one
return loss for the whole part — still works and nothing changes for a design that never touches
these. The detune is flat with frequency, deliberately: it is the mismatch you want to test a PA
against, not a rotating one.

<div class="callout note">
<span class="label">Do not reach for Z0 to do this</span>
<p>A complex <code>Z0</code> is accepted, but it
is the reference every port shares and the reference <code>IL</code> and <code>Isolation</code> are
stated against — and with an ideal circulator the reflection it produces at port 1 is the PRODUCT of
what ports 2 and 3 are terminated in, because the wave leaves port 2, reflects, circulates to port 3,
reflects again, and only then comes back. It is not the number you typed and it is not monotone in
anything you would think to turn. <code>VSWR1</code>/<code>Ang1</code> is the port's own reflection,
which is what a VSWR figure on a datasheet means.</p>
</div>

It can carry a PIM specification. Parameters: {{anchor: components#circulator|Components › Circulator}}.

### Directional Coupler {#coupler}

{{symbol: coupler}}

Four ports in the order a coupler is always specified: **1** in, **2** through, **3** coupled, **4**
isolated. `Coupling` alone sets the split, and the arrow on the body is what separates the coupled
port from the isolated one.

**The through arm is not free.** `Coupling` is a lossless split — `t = √(1 − c²)` — so an ideal
**20 dB coupler already loses 0.044 dB** through its main arm, and a 10 dB coupler loses 0.46 dB.
`IL` is loss added **on top** of that, and it scales all three transmission paths together so that
`Directivity` keeps meaning what it says.

Parameters: {{anchor: components#coupler|Components › Directional Coupler}}.

### Duplexer {#duplexer}

{{symbol: duplexer}}

An antenna port that splits into a transmit branch and a receive branch, each through its own
passband: `ANT` on the left, `TX` and `RX` on the right. It is two complete filter specifications
(every `Filter` parameter, prefixed `Tx` and `Rx`) plus one shared antenna impedance.

**There is no `Isolation` parameter, and that is the point of the component.** A duplexer's TX→RX
isolation is a *consequence* of its two responses meeting at one node; a number you typed would be
overriding the physics with an assertion. What it achieves is what the far arm's own rejection
allows — with the default band plan at order 5, the measured TX→RX leakage runs from about
&minus;89 dB at the TX band's lower edge to &minus;57 dB at the worst point across both bands.

<div class="callout note">
<span class="label">The arms load each other — that is why real duplexers have phasing lines</span>
<p>An out-of-band ideal bandpass arm reflects essentially <b>everything</b> (|S11| = 0.999999967 —
nothing is dissipated, which is what ideal buys), but not at zero phase: at a neighbouring band's
centre its reflection sits at about &minus;23&deg;, and a unit-magnitude reflection at a non-zero
angle is a <b>reactance</b>. It loads the junction, so each arm's transmission is not quite what the
same filter would do standing alone — up to 0.144 in amplitude for adjacent bands, falling to 0.040
when the two bands are widely separated. The antenna match runs about &minus;12 dB across each
band.</p>
<p>That is the same statement as "a real duplexer needs a phasing line", and the fix is the same
one: put a <a href="components.html#tline">TLIN</a> in an arm and tune its length. There is
deliberately no hidden length inside the component.</p>
</div>

Parameters: {{anchor: components#duplexer|Components › Duplexer}}.

### Filter {#filter}

{{symbol: filter}}

A two-port filter synthesised from a prototype: `Response` picks the family — **Butterworth**,
**Chebyshev**, **InvChebyshev**, **Bessel** or **Elliptic** — and `Form` picks Lowpass, Bandpass or
Highpass. A parameter the chosen family does not read is ignored rather than refused, so changing
family never means clearing a field first.

<div class="callout note">
<span class="label">It wears the Match component's symbol, and that is deliberate</span>
<p>The Filter and the <a href="match.html">Match</a> network share <b>one glyph</b> — the same
picture, not a related one. <b>Impedance matching is a form of filtering</b>, the two are built out
of the same idea, and the library says so rather than pretending otherwise. Tell them apart the way
you tell the five FET laws apart: by the <b>type label</b> and the <b>instance name</b>,
<code>FLT1</code> against <code>MN1</code>. The stack of waves with a slash through each blocked
band follows <code>Form</code> — see <a href="dynamic-symbols.html#form">Dynamic symbols</a>.</p>
</div>

**`Order` is the PROTOTYPE order.** The bandpass transformation doubles the degree, so `Order = 3`
as a bandpass is a **6th-degree network**. Both conventions exist in the wild; this one is the
prototype's. For the three all-pole families the far stopband falls at 20 × `Order` dB per decade;
InvChebyshev and Elliptic put transmission zeros on the jω axis instead, which is what buys their
sharp transition and why their stopbands **level off** at `Astop` rather than continuing to fall.

How much that selectivity is worth, at order 5 with 0.1 dB of passband ripple and a 60 dB floor: the
elliptic reaches &minus;60 dB at 2.04 × the band edge, against Chebyshev's 3.41, Butterworth's 3.98
and Bessel's 15.57. Bessel is not competing — it is chosen for **group delay**, which it holds
within 1% out to a band that grows with every order.

**`Zin` and `Zout` are independent.** The block is stamped as its scattering matrix rather than
synthesised as a ladder, so an unequal pair is free: the filter is then a **lossless impedance
transformer as well as a filter**, matched at both ports in its passband. Measured in a uniform
50 Ω system an unequal pair shows the transformer's mismatch, which is the answer and not a fault.
`IL` lays a flat loss on top, and it **dissipates** — it multiplies S21 and leaves S11 alone, the
way a real filter's loss does.

**Either may be COMPLEX** — `Zin = 5+j100 Ohm` — which is a filter designed to work between
reactive terminations rather than resistive ones, the ordinary case at the ports of a real device.
Every ideal system block takes a complex port impedance the same way: the [Attenuator](#atten),
[Switch](#switch), [Circulator](#circulator), [Coupler](#coupler), both hybrids, the
[Balun](#balun), the [Duplexer](#duplexer), and an [Amplifier](#amp) left linear.

<div class="callout note">
<span class="label">Zin is what the port presents — so terminate it in the conjugate</span>
<p><code>Zin</code> and <code>Zout</code> name the impedance that port <b>presents</b>, so a filter
at <code>Zin = 5+j100</code> is conjugate-matched — maximum power transfer — by a
{{anchor: pins-ports-terms#term|Term}} at <code>Z=5-j100 Ohm</code>, which is where it measures the
prototype's own response. A Term at <code>5+j100</code>, the same value, is a near-total mismatch.
The duplexer's <code>Zant</code>, <code>TxZ</code> and <code>RxZ</code> are the same quantity under
shorter names.</p>
<p>A parameter spelled <code>Z0</code> is different, and deliberately: it is the <b>reference</b>
impedance S is defined against — the attenuator's, the switch's, the circulator's, the coupler's —
and the two differ by a conjugate. The parameter name is what tells you which you are looking at,
and every block with a real port impedance is unaffected either way.</p>
</div>

A block that is NONLINEAR refuses a complex impedance rather than reading its real part: an
[Amplifier](#amp) with a finite `IP3` (which is the tile's own default of 40 dBm — set `IP3=200`
for the linear stamp), any block with `PIM` turned on, and the [Mixer](#mixer). The refusal names
the component and the port. A parameter that is only ever read as a real number — a dB, a
frequency, an order — refuses a complex value the same way, for the same reason: the alternative is
the model quietly using its default and reporting nothing.

Parameters: {{anchor: components#filter|Components › Filter}}.

### 180° Hybrid (Hybrid180) {#hybrid180}

{{symbol: hybrid180}}

The same component as the directional coupler, seeded at 3.0103 dB with the coupled port in
anti-phase — a sum port and a difference port. Everything in the
[Directional Coupler](#coupler) entry applies, and 3.0103 dB is simply the equal split written to
the precision that makes `c = t = 1/√2`.

Parameters: {{anchor: components#hybrid180|Components › 180° Hybrid}}.

### 90° Hybrid (Hybrid90) {#hybrid90}

{{symbol: hybrid90}}

The same component again, at 3.0103 dB with the coupled port **in quadrature** — the standard
building block of a balanced amplifier, an image-reject mixer or a reflection-type phase shifter.

**The quadrature is exact at every frequency**, which is the sharpest idealisation in this chapter.
A branchline coupler holds 90° over a useful band and its split over a narrower one; this block
holds both from DC upwards. When the bandwidth *is* the question, build one out of four quarter-wave
{{anchor: components#tline|TLIN}} arms — or four {{anchor: components#mlin|MLIN}} arms and the
microstrip junctions — and sweep it.

It is also the only one of the three coupler tiles that is **unitary**: a lossless, matched,
reciprocal four-port with directivity *must* have its coupled arm in quadrature. At 0° or 180° the
matrix is energy-consistent under any single-port excitation but not simultaneously realisable —
which circuitRF stamps anyway, because you are allowed to type numbers a physical part could not
have.

Parameters: {{anchor: components#hybrid90|Components › 90° Hybrid}}.

### Switch {#switch}

{{symbol: switch}}

An SPST switch. Two interchangeable pins, an insertion loss for the path it is making, and an
isolation for the path it is not.

### Transfer Switch (SwitchD) {#switchd}

{{symbol: switch-d}}

The SPDT: one common port on the left, two throws on the right. The same engine component as the
SPST — they share the `SW` instance prefix, so swapping one for the other does not renumber a
schematic.

<div class="callout tip">
<span class="label"><code>State</code> is a parameter, not a pin — so sweep it</span>
<p>Which throw is closed is a <b>number</b>, not a wire you move: <code>1</code> is the SPST's only
throw, <code>1</code> or <code>2</code> selects an SPDT's, and <code>0</code> opens everything. So a
<a href="simulations.html#parametric-sweep">parametric sweep</a> over <code>State</code>
<b>simulates every switch position in one run</b>, and a <code>State</code> naming a throw that does
not exist simply closes nothing — by the same rule, not by a special case, which is what makes the
sweep safe at every value.</p>
<p>And the symbol is drawn in the position it is set to: the blade lifts for <code>0</code> and
points at the selected throw otherwise, so a swept state is readable on the page. See
<a href="dynamic-symbols.html#state">Dynamic symbols</a>.</p>
</div>

**`OffState` is the choice that changes the neighbouring circuit.** `Reflective` makes an open throw
an open circuit — what a series switch does, and what sends the signal straight back at whatever is
attached to it. `Absorptive` makes it a matched termination instead. If there is a filter, an
amplifier input or a length of line on that throw, the two answers are not close to each other.

Parameters: {{anchor: components#switch|Components › Switch}} and
{{anchor: components#switchd|Transfer Switch}}.

---

<p class="small">See also: <a href="components.html">Components</a> ·
<a href="dynamic-symbols.html">Dynamic symbols</a> ·
<a href="simulations.html">Simulations</a> ·
<a href="match.html">The Match Component</a>.</p>
