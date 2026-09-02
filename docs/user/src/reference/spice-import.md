---
title: Importing a SPICE Model
slug: reference/spice-import.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > SPICE import
lede: What circuitRF carries across from a .model card or a .subckt, what it leaves behind, and why — including where to read the list of parameters that did not come with it.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#surfaces">Two ways a SPICE file becomes a circuit</a></li>
      <li><a href="#report">Nothing is dropped silently</a></li>
      <li><a href="#why">Why a parameter is not carried</a></li>
      <li><a href="#families">What each card type leaves behind</a></li>
      <li><a href="#refused">When the whole card is refused</a></li>
      <li><a href="#matters">Working out whether it matters</a></li>
    </ol>
  </nav>

## Two ways a SPICE file becomes a circuit {#surfaces}

Both start from the same file and use the same translation, so everything on this page applies
equally to either.

<table class="param-table"><thead><tr><th>Route</th><th>What you get</th><th>Use it when</th></tr></thead>
    <tbody>
      <tr>
        <td><b>The SpiceModel component</b><br>{{anchor: components#spicemodel|Components › SPICE Model or Subcircuit}}</td>
        <td>A component that <b>runs from the file</b>. Nothing is copied; edit the file and the schematic follows it.</td>
        <td>The file is the authority and must stay so.</td>
      </tr>
      <tr>
        <td><b>Copy to Workspace as Cell…</b><br>(the project tree's context menu)</td>
        <td>An editable cell — a schematic you can open, re-symbol and change.</td>
        <td>The model is a starting point rather than the last word.</td>
      </tr>
    </tbody>
  </table>

A `.model` card circuitRF has a device for becomes **that device**, with its terminals where that
device's terminals are. A `.subckt` becomes a **schematic** — every element inside it placed as an
ordinary component, wired as the definition wires them, with the definition's own port names on the
pins.

<div class="callout note">
<span class="label">A packaged part's parasitics are components, not parameters</span>
<p>A vendor file for a real transistor is usually a <code>.subckt</code>: a <code>.model</code> card
for the die, wrapped in the lead inductances and package capacitances that dominate its behaviour
above a gigahertz or two. Those wrappers come across as ordinary R, L and C instances, because that
is what they are. If you import the bare card instead, you get the die and no package — a far larger
difference than anything else on this page. Leave <code>Name</code> blank and the highest-level
definition is chosen, which is the wrapper.</p>
</div>

## Nothing is dropped silently {#report}

Every parameter on a card that circuitRF does not carry is **named**, every time:

```text
RF_NPN_DIE   NPN  —  34 parameter(s) — 6 not carried: AF, CJS, KF, MJS, PTF, VJS
```

Three things about that line are deliberate:

- **It is exhaustive.** The translation walks a table of the parameters circuitRF has a home for;
  everything the table did not consume goes into the list. A parameter cannot be quietly absorbed,
  because absorbing it is not one of the outcomes.
- **The spellings are your file's, not circuitRF's.** They are echoed exactly as the card writes
  them, so you can search for them in the file rather than translating a name back.
- **It survives the import.** For a cell built with *Copy to Workspace as Cell…* the same list — and
  the notes below — are written onto the cell's own schematic as a text annotation. Weeks later, the
  cell still says what it was built from and what did not come with it.

Alongside the not-carried list, the translation reports **notes**: decisions it made that you could
reasonably want to see. Which of two published MESFET laws a card was read as and what said so; that
a gate capacitance on the card switched the FET's charge model from a constant capacitance to a
bias-dependent junction; that a card's `RD`/`RS` were placed as resistors beside the transistor; that
a MOS card states no `TOX` and therefore has no intrinsic gate charge. These are not warnings — they
are the reasoning, written down.

## Why a parameter is not carried {#why}

There are five reasons, and every dropped parameter is one of them.

### 1. There is no analysis that would read it

`KF` and `AF` — the flicker-noise coefficients — appear on every semiconductor card and are carried
by none of them. circuitRF has **no noise analysis** for them to feed. Accepting them would put two
rows on the built cell that look honoured and change no result, which is worse than an empty space:
a parameter this model does not read is not offered.

### 2. The device has no node for it

A bipolar card's `CJS`/`VJS`/`MJS` describe the **collector–substrate junction**. A discrete RF
transistor has no substrate terminal to attach one to, and adding a fourth pin would change what the
symbol means.

**Self-heating** is the same shape of omission across every family. The junction temperature is a
`Temp` parameter, not a solved node, so there is no electrothermal feedback and nothing for a card's
thermal parameters to connect to. A thermal model belongs *around* the part.

### 3. circuitRF's formulation cannot express it

A bipolar card's `PTF` is **excess phase** — a delay. circuitRF's weighting functions carry 1 and
jω, not exp(−jωτ), so there is no term in the device for a delay to become. The same reasoning
retires the MESFET family's transit-time delay.

Also here: subthreshold conduction (`NFS`) on the MOS cards, because the classical law goes to
exactly zero at threshold; quasi-saturation on a VDMOS card, which needs a drift-region model and a
second internal node; the level-3 impact-ionisation substrate current; and the Statz/TOM charge
formulation, which works on a smoothed effective voltage rather than on Vgs and Vgd separately and
is a different scheme rather than a parameter change.

### 4. It belongs in the schematic, not in the device

A **MESFET** card's `RD` and `RS` are not carried, because circuitRF's MESFET family has no drain or
source parasitic resistance of its own. They are not lost either: the import places them as ordinary
series resistors beside the transistor and says so in a note.

<div class="callout warn">
<span class="label">The same two spellings mean different things on a JFET card</span>
<p>A <b>JFET</b> card's <code>RD</code> and <code>RS</code> <i>are</i> carried, as model parameters —
circuitRF's JFET puts them on internal nodes of its own, so the schematic shows one device rather
than a transistor with two resistors beside it. The two cards spell them identically and the
difference is invisible once the cell is built, which is why the translation states which happened
every time.</p>
</div>

### 5. It belongs to a law circuitRF does not implement

A JFET card stating `B`, `ALPHA` or `VK` is stating parameters of a higher published JFET level — a
doping-profile knee and that level's own channel-length modulation. There is no square-law parameter
that means the same thing, so they are named rather than folded into `LAMBDA`, which is a different
quantity. The card is still read as the square law rather than refused, with a note saying the device
will be optimistic where those terms matter.

The MOS rule runs **both ways**. On a level-1 binding the six short-channel parameters (`ETA`,
`THETA`, `KAPPA`, `VMAX`, `DELTA`, `XJ`) are not carried; on a level-3 binding `LAMBDA` is not, because
level 3 computes the output slope from a real shortening of the channel rather than fitting it. Carrying
a parameter onto a level that never reads it is the worse failure: it lands on the cell as an ordinary
row, looks honoured, and is discovered much later by wondering why changing it does nothing.

<div class="callout note">
<span class="label">A card's LEVEL number is never simply obeyed</span>
<p>On a <b>MESFET</b> card the level numbering is not portable — the same integer selects a different
law in different dialects — so which law a card states is decided from its <i>parameters</i>:
<code>B</code> appears in the Statz law and in no other, so stating it is the file's own unambiguous
answer. <code>LEVEL</code> is listed as not carried so nobody concludes it was honoured.</p>
<p>On a <b>MOS</b> card the classical numbering <i>is</i> portable — 1, 2 and 3 mean the same three
published models everywhere — so the number is read, and 4 and above are refused (see below).
<code>LEVEL</code> itself is still not carried: it selects a model rather than being one of its
parameters.</p>
</div>

## What each card type leaves behind {#families}

Named omissions only — the ones circuitRF has made a decision about. Anything else your card states
still appears in the not-carried list by name. The last column links to the device's own physics, where
each omission is stated again from the model's side.

<table class="param-table"><thead><tr><th>Card</th><th>Built as</th><th>Named omissions</th><th>The device</th></tr></thead>
    <tbody>
      <tr><td><code>D</code></td><td>Junction diode</td>
          <td><code>KF</code>/<code>AF</code>; <code>IKF</code> — the high-injection knee, which this diode has no term for</td>
          <td>{{anchor: components#diode|Diode}}</td></tr>
      <tr><td><code>NPN</code>, <code>PNP</code></td><td>Bipolar transistor</td>
          <td><code>CJS</code>/<code>VJS</code>/<code>MJS</code>, <code>PTF</code>, <code>KF</code>/<code>AF</code></td>
          <td>{{anchor: components#bjt-equations|What is modelled}}</td></tr>
      <tr><td><code>NMF</code>, <code>PMF</code></td><td>MESFET — Curtice quadratic, or Statz where the card states <code>B</code></td>
          <td><code>RD</code>/<code>RS</code> (placed as resistors instead), <code>LEVEL</code>, <code>KF</code>/<code>AF</code>; the Statz/TOM charge formulation, transit delay and breakdown</td>
          <td>{{anchor: components#fets|The FET family}}</td></tr>
      <tr><td><code>NJF</code>, <code>PJF</code></td><td>Junction FET, square law</td>
          <td><code>B</code>, <code>ALPHA</code>, <code>VK</code>, <code>KF</code>/<code>AF</code>; gate breakdown and transit-time charge</td>
          <td>{{anchor: components#jfets|The junction FET}}</td></tr>
      <tr><td><code>NMOS</code>, <code>PMOS</code></td><td>MOS level 1 or level 3, chosen by the card's own <code>LEVEL</code></td>
          <td><code>LEVEL</code>, <code>NFS</code>, <code>KF</code>/<code>AF</code>; the six short-channel parameters on level 1, <code>LAMBDA</code> on level 3; the level-3 substrate current</td>
          <td>{{anchor: components#mos|The MOS transistor}}</td></tr>
      <tr><td><code>VDMOS</code></td><td>Vertical power MOSFET, n- or p-channel from a bare keyword on the card</td>
          <td>Quasi-saturation and subthreshold shaping parameters; flicker noise; self-heating, which for a power device is a real omission</td>
          <td>{{anchor: components#vdmos|The vertical power MOSFET}}</td></tr>
      <tr><td><code>BEAD</code></td><td>Ferrite bead, four-element equivalent</td>
          <td>Saturation — a bead's inductance falls with DC bias current and this is a linear element, so it is not representable at all</td>
          <td>{{anchor: components#bead|Ferrite Bead}}</td></tr>
      <tr><td><code>RES</code>, <code>R</code></td><td>Resistor</td>
          <td>Everything beyond <code>R</code>, <code>TC1</code> and <code>TC2</code></td><td>—</td></tr>
      <tr><td><code>CAP</code>/<code>C</code>, <code>IND</code>/<code>L</code></td><td>Capacitor, inductor</td>
          <td>Every temperature coefficient. circuitRF's resistor has <code>TC1</code>/<code>TC2</code> and its capacitor and inductor do not — which is a real loss of fidelity, and is exactly why it is reported rather than absorbed</td>
          <td>—</td></tr>
    </tbody>
  </table>

<div class="callout note">
<span class="label">A p-channel VDMOS is a bare keyword, and a bare negative threshold is not one</span>
<p>A <code>VDMOS</code> card is <code>VDMOS</code> for both channels; a lone <code>pchan</code> keyword
is what makes it p-channel. A card with <b>no</b> keyword and a <b>negative</b> <code>VTO</code> looks
like a p-channel part — and equally like a (rare, real) depletion-mode n-channel one. Nothing on the
card separates them, so it is read as n-channel, which is what the absent keyword means, and the
ambiguity is reported rather than guessed at. If it is a p-channel part, build it against the
p-channel component instead.</p>
</div>

## When the whole card is refused {#refused}

A model type circuitRF has no device for is **refused by name** — in the parameter dialog the moment
the file is chosen, and again at Run. Nothing is approximated. The temptation is real: a JFET's square
law looks like the Curtice quadratic with the tanh ignored, and a ferrite bead looks like a parallel
RLC. Every one of those produces a cell that simulates and is quantitatively wrong, with nothing
anywhere reporting it. Being told costs a minute; a plausible wrong transistor costs the measurement
built around it.

Refusals fall into two groups.

**circuitRF has no model for this** — the refusal names the type and says what is missing, because
"unsupported" sends you looking for a setting that does not exist:

- An **IGBT** card. circuitRF *has* an IGBT, and still cannot take this card: its parameters belong to
  the ambipolar-transport model and describe the silicon (base width, doping, carrier lifetime), while
  circuitRF's is an equivalent-circuit model parameterised by what a data sheet gives. Neither set is
  derivable from the other by renaming.
- A **controlled-switch** card. circuitRF's Switch is an ideal RF block set by its own parameters, not
  a card-driven behavioural switch.
- A **MOS card at `LEVEL` 4 or above.** Those are the compact-model families, whose parameters name
  different quantities under different spellings — almost nothing would be carried, and what came out
  would be this transistor wearing default numbers.
- Any other type, and a card naming **no** type at all.

In each of those the suggested route is the same: run the model through
{{anchor: components#veriloga|the VerilogA component}}, which takes its parameters from the model file
itself rather than from a card.

**The card does not say enough to build anything** — refused for being incomplete, not unsupported:

- A `BEAD` stating only a DC resistance. What could be built is a resistor of a few milliohms, which
  is not a bead and would simulate perfectly.
- A resistor card stating only `RSH`. A sheet resistance is a resistance per square and needs a width
  and a length to become a value, neither of which a model card carries.
- A `CAP` or `IND` card stating no value.

The principle behind all three: **a value of zero simulates**. Refusing is the only outcome that
reaches you.

## Working out whether it matters {#matters}

Ask these in order — most of the time the first one settles it.

<ol class="steps">
  <li><b>Read the value on your own card.</b> A parameter stated as zero was doing nothing in the
  simulator the card was written for either. A bipolar card very often states <code>CJS = 0</code> and
  <code>KF = 0</code>, in which case the substrate junction and the flicker noise are absent from the
  source model too and nothing at all has been lost.</li>

  <li><b>Put a number on what is left.</b> Most omissions have an arithmetic you can do in a line.
  Excess phase is the clearest: <code>PTF</code> is the phase in <i>degrees</i> at
  f&nbsp;=&nbsp;1/(2π·<code>TF</code>), so a card with <code>TF</code>&nbsp;=&nbsp;17&nbsp;ps and
  <code>PTF</code>&nbsp;=&nbsp;0.4 is describing 0.4° at 9.2&nbsp;GHz — below the extraction
  uncertainty of the package parasitics around it, and not worth a second thought. The same card at
  <code>PTF</code>&nbsp;=&nbsp;30 would be a different conversation.</li>

  <li><b>Check you imported the wrapper, not the die.</b> See the note at the top of this page. For a
  packaged part this is nearly always the largest term.</li>

  <li><b>If the omission is load-bearing, change models rather than parameters.</b> A compiled model
  through {{anchor: components#veriloga|VerilogA}} takes its parameters from the model file itself,
  so nothing has to be mapped onto circuitRF's parameter set and nothing is left behind.</li>
</ol>
