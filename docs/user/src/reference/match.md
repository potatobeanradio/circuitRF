---
title: The Match Component
slug: reference/match.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Match
lede: Direct synthesis of a bandpass matching network that absorbs both terminations.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What Match is</a></li>
<li><a href="#absorption">Absorption, and why it is the whole point</a></li>
<li><a href="#designer">The Match Designer</a></li>
<li><a href="#spec">The specification pane</a></li>
<li><a href="#probe">Probe: reading the terminations off your own circuit</a></li>
<li><a href="#transforms">Norton transforms: moving values without moving the response</a></li>
<li><a href="#solutions">The solutions list</a></li>
<li><a href="#worked">Worked example: a two-stage FET interstage match</a></li>
<li><a href="#flatten">Flatten to Cell</a></li>
<li><a href="#refs">References</a></li>
</ol>
</nav>

## What Match is {#what}

**Match** is a two-port component that **synthesises a bandpass LC matching network matching both of its
ports simultaneously** to the network around it. Place it, tell it what is on each side and over what
band, and it produces a ladder — element by element, with values.

It is **direct synthesis**: closed-form, procedural, no optimiser. There is no seed, no convergence, no
"run it again and see if it finds something better". The same specification always produces the same
network, and when a specification has no solution at the order you asked for, you are told so with
numbers rather than left watching an optimiser fail to converge.

The component **stamps the ladder element by element on internal nodes** — not as a lumped ABCD block —
so it behaves correctly at DC and in harmonic balance, and it is identical by construction to the cell
that [Flatten to Cell](#flatten) writes.

## Absorption, and why it is the whole point {#absorption}

Most matching networks *tune out* a termination's reactance: the transistor has 10 pF across its input,
so you resonate it away and match what is left. That works, over a narrow band, because the resonance
only cancels at one frequency.

**Match absorbs each termination's reactance into the network instead.** A transistor's C<sub>gs</sub>
or C<sub>ds</sub> becomes *an element of the matching filter* — one of the ladder's own capacitors, in
the position the filter wanted a capacitor anyway.

<div class="callout note">
<span class="label">Why this is not a trick</span>
<p>Fano's bound says how much bandwidth you can get matching a reactive load, and it depends on the
load's Q. A network that tunes the reactance out and then matches a resistance gives away most of that
bound. A network that treats the reactance as part of the filter gets the widest bandwidth the load's Q
permits — which is the best any network can do.</p>
<p>"Efficient broadband impedance-matching structures are necessarily filter structures."
— Matthaei, Young &amp; Jones</p>
</div>

Two consequences worth being clear about:

- **The absorbed elements are not inside the component.** The external network supplies them — that is
  what makes the match real. The Designer always draws them **immediately beside the termination that
  supplies them** — that position, and the one-line legend under the schematic, are how you tell which
  elements you do not have to buy. They are drawn at full brightness like everything else: the part
  you are matching against is the last thing that should be hard to read.
- **Q is the entire content of a termination**, as far as the synthesis is concerned. At band centre
  ω₀ = √(ω₁ω₂) with fractional bandwidth *w*: a parallel R‖C has `Q = ω₀RC`, a series R+C has
  `Q = 1/(ω₀RC)`. R re-appears later only as an impedance scale and as the Norton-transform target.

The **higher-Q end drives the synthesis**, because it is the binding constraint — the one Fano's bound
limits. The far end falls out of the synthesis and is then reconciled against its real termination.

**Order parity is not free.** An end absorbing a *series* reactance needs a series arm there; an end
absorbing a *shunt* reactance needs a shunt arm; and arms alternate. So one-series-one-shunt forces an
**even** order and like-kind forces an **odd** one. The order picker offers only the parities that can
work — {2, 4, 6} or {3, 5}, or all of 2…6 when an end is purely resistive — and changing a topology
adjusts the order rather than presenting an impossible one, saying so when it does.

## The Match Designer {#designer}

Double-click a placed `Match` to open the Designer. (The properties panel for a selected `Match` shows
a compact summary and an **Open Match Designer…** button; the design blob is never rendered as a text
row.)

{{ui: match-designer}}

That is a freshly placed component: 50 Ω to 10 Ω over 1.8–2.2 GHz, order 4 — a real 5:1 transformation,
arriving with a solution already applied so there is something to look at rather than an identity.

Four regions, left to right and top to bottom:

- **Specification** — the two terminations, the band, the order, the response shape and the options.
- **Network** — the synthesised ladder as a real circuitRF **schematic**, with the **value grid** in a
  scroll view underneath it. The schematic draws each element with its instance name and value,
  out-of-range values in red, a grounded termination at each end, and a brace under the elements each
  transform produced. Right-click either view for **Copy**, which puts the drawing on the clipboard as
  something a schematic page can paste and PowerPoint can paste as vector art; the grid's menu also
  offers **Copy as CSV**.
- **Response** — two rectangular plots, drawn by the Data Display's own plot control from an S-parameter
  run of the **full design** (ladder plus both terminations, port references R1 and R2). |S11| and
  |S21| on the first — S21 against the **right-hand axis**, because the two live decades apart and one
  shared scale renders the insertion loss as a flat line on the ceiling; phase and group delay on the
  second. Markers, their info boxes and the plot's own Copy work exactly as they do in a Data Display.
  The plot band defaults to the design band ±10%.
- **Transforms** — the linked Norton slider rack, and below everything the **status strip**: Q1, Q2,
  worst in-band return loss, insertion loss, ripple, and the achieved-versus-required Π N².

<div class="callout note">
<span class="label">There is no "nearest standard value" column, deliberately</span>
<p>What counts as a realizable value is your call and depends on the flow: in an MMIC flow a capacitor
is <i>designed</i> to its value and an E24 series is meaningless, and on a board the available series
depends on the vendor and the package. The grid shows the synthesised value and the sliders move it;
deciding what is buildable stays with the person who knows the process.</p>
</div>

**Refusals appear in the status strip, with numbers.** "No real root at this order", "the far end is not
absorbable", "the transforms cannot reach the target" — each names the quantity that failed, and the
affected termination turns red.

## The specification pane {#spec}

Each termination is **a resistance in series or in parallel with one reactive element** — C, L, or none.
The little RC pictogram shows which topology you have picked at a glance. Every numeric field is an
ordinary circuitRF value-and-unit pair, so [unit entry](units.html) works exactly as it does elsewhere.

| Control | What it does |
|---|---|
| **Topology** | Series or parallel. This is a physical statement about the network you are matching, and it changes which order parities are available. |
| **R**, **X kind**, **Value** | The termination. `X = –` means purely resistive. |
| **Conjugate** | Targets Z* instead of Z — which flips the reactance sign, and so turns a measured parallel R‖C into a parallel R‖L target. |
| **Band f1, f2** | The passband. Everything is computed at ω₀ = √(ω₁ω₂) with fractional bandwidth *w*. |
| **Order** | Number of in-band match points, 2–6, restricted to the parities the terminations permit. The element count is **2n in every form**. |
| **Response** | Chebyshev — single-match (optimum), Chebyshev — double-match (exact), Butterworth, or Bessel where feasible. |
| **Form** | Bandpass, lowpass or highpass. Not a control — you choose it by applying a solution, and every solution card names its form. |
| **Q-adjust** | Offers solutions synthesised at a raised Q, which trades a little bandwidth for element values that may be easier to build. |
| **Allow negative components** | Widens the Norton slider ranges past the positivity threshold. Off by default, and it is off for a reason — see below. |

<div class="callout warn">
<span class="label">Conjugate is the right target for a small-signal stage and usually the wrong one for a PA output</span>
<p>A power amplifier's load should come from <a href="simulations.html#loadpull">load-pull</a> (R<sub>opt</sub>),
not from the device's own output impedance. Conjugate-matching a PA output gives you maximum small-signal
gain and the wrong large-signal load.</p>
</div>

## Probe: reading the terminations off your own circuit {#probe}

Each termination carries a **Probe** button that looks *outward* from that pin into the circuit the
`Match` is placed in, and fills in R, topology, reactance kind and value for you.

What it does, so you know what you are getting:

1. It extracts the enclosing test bench, **deletes the `Match` instance** so the probe cannot measure
   itself, and attaches a 50 Ω `Term` to the net that pin was on.
2. It **keeps every DC source and bias network** — the interesting case is a transistor, and a
   transistor's small-signal impedance is only meaningful at its operating point. **If the DC solve
   fails, the probe refuses and reports the DC failure** rather than returning an impedance computed at
   zero bias.
3. It runs an S-parameter sweep over the design band and converts to Z.
4. It fits **all four** two-element models — series R+C, series R+L, parallel R‖C, parallel R‖L — each
   as a linear least-squares fit in its natural domain, then scores each by mean |ΔΓ| over the band.
   That single bounded metric ranks all four on equal terms.

**All four fits are shown with their residuals in Γ units**, so you can take the second-best when you
know better. The best physical fit is applied. If even the best residual is poor (mean |ΔΓ| above 0.05
by default) the result is still applied but **flagged**: the external network is not well described by a
two-element model over this band — which is the honest answer for a network with an in-band resonance,
and points you at narrowing the band.

**A probed termination is a snapshot, not a live link.** It records where it came from and shows a
badge; editing the value by hand clears the badge to manual, and your override always wins. Changing
the surrounding circuit does not silently re-synthesise the network — re-probing is always an explicit
action.

The button is greyed out, with a reason, when the pin is unconnected, when its net has nothing else on
it, when the schematic has unresolved errors, or when the `Match` is inside a cell rather than in a
test bench — there is no external network to look at from inside a definition.

## Norton transforms: moving values without moving the response {#transforms}

This is the part of the window that makes a synthesised network buildable.

A **Norton transform** replaces an L-section of two like-kind elements with a π or T of three like-kind
elements plus an ideal transformer of ratio *N*, then absorbs the transformer by scaling the rest of the
network (impedances by N², so L·N² and C/N²).

<div class="callout note">
<span class="label">The property that matters</span>
<p><b>The transfer function is unchanged.</b> Only the element values and the terminating resistance
move. That is how a 2.1 pH inductor becomes something a PCB can actually build — without giving back
any of the match you just synthesised.</p>
</div>

The rack has one row per applied transform: a π/T selector, a numeric box, a slider, and a lock. Which
two elements a transform acts on is read off the **schematic above it**, where a brace spans exactly
the elements that transform produced. `+ add` lists the pairs currently available by element name;
`− remove` removes the last.

- **The slider ends where the maths does.** Past a positivity threshold the transform produces negative
  elements, so the range is bounded at `N = 1 + Z₁/Z₂` when stepping up and `N = Z₂/(Z₁+Z₂)` when
  stepping down. Turning on *Allow negative components* widens it; the values then go negative, which
  is occasionally what a designer wants and usually not.
- **Which pairs are transformable is structural**: two like-kind elements at most three positions apart,
  of opposite orientation, with matching intervening elements — and **never an end element carrying an
  absorbed termination reactance**, because moving it would break the absorption.
- **Link (on by default) keeps the product on target.** The transforms must satisfy
  Π N² = R<sub>far,target</sub> / R<sub>far,synthesised</sub>. Drag one slider and the unlocked others
  re-solve so the product stays on target, each clamped to its own range. Locked rows are never touched.
  With a single transform and link on, N is fully determined and its slider is disabled. When the
  product cannot reach the target within the ranges, the offending termination goes red and the design
  is flagged as matched at one end only.

<div class="callout warn">
<span class="label">A transform on an inductor pair can make the network unsolvable at DC</span>
<p>A Norton transform produces three elements of the same kind as the pair it replaced. Three ideal
<i>inductors</i> in a π are, at DC, a loop of ideal shorts — which is a singular system, so the network
will sweep S-parameters happily and refuse to DC-solve at all. A capacitive transform puts a series
capacitor in the middle branch, which is a DC open, and stays solvable. Both are legitimate; if your
design has to DC-solve, prefer the capacitive solution from the list.</p>
</div>

## The solutions list {#solutions}

{{ui: match-solutions}}

**Solutions ▸** slides out a docked list: every valid transform set for this specification, so you can
click through candidates and watch the ladder and the response change live. Each row shows a badge
(current / previously applied / never applied), the transform count, the element pairs each transform
acts on, the Q-adjust value when non-zero, and the response type.

**Ordering is by transform count, then by position, then by Q-adjust** — the simplest realizable
solution first. Within one form, order and family the response is the same for every row; what differs
is the element values you would have to build.

### Bandpass, lowpass and highpass {#forms}

The list covers **three network forms**, and the filter's first group turns each on and off. A bandpass
network is two-element arms resonant at band centre. A **lowpass** network is series inductors in the
through path and shunt capacitors to ground; a **highpass** network is the other way round. All three
are matched between f1 and f2, and all three use 2n elements at order n — the lowpass form is not the
cheaper one.

What the lowpass and highpass forms buy is **tame element values at wide bandwidth** — there are no
resonators, so the L's and C's stay within a factor of a few of each other instead of spreading over
decades — plus a DC path (lowpass) or a DC block (highpass), which is what a bias network wants. What
they cost is **return loss that depends on the impedance ratio**: the ladder is transparent at DC, so
|Γ(0)| = (r−1)/(r+1) is fixed by the two resistances and comes out of the same budget as the in-band
match. At a 2:1 band, four elements, that is −22.2 dB into a 2:1 ratio and −10.5 dB into a 10:1 one,
against a bandpass order-2 network's −16.4 dB at any ratio. Which is better depends on your numbers, so
the panel lists all three and you read the return loss off the cards.

Two consequences worth knowing before you go looking for them:

- **A lowpass or highpass network has no Norton transforms.** Every like-kind element in it sits in the
  same orientation, so there is no pair to transform; the ratio is already on target and the transform
  rack says so instead of showing an empty list.
- **Each form absorbs half of the termination kinds, and the impedance ratio decides which end takes
  which.** A lowpass ladder puts its series inductor against the *lower*-impedance port and its shunt
  capacitor against the *higher*-impedance one. So a shunt capacitance on the high side of a step-down
  absorbs; the same capacitance on the low side of a step-up does not, and the refusal says so and
  points you at bandpass form.

## Worked example: a two-stage FET interstage match {#worked}

The interstage problem between two amplifier stages: **stage 1's output, 200 Ω ‖ 0.125 pF, into stage
2's input, 1.25 Ω + 10 pF, over 3.3–5.0 GHz.** A 160:1 impedance transformation with a reactance at both
ends — exactly the case where tuning-out gets you a few percent of bandwidth and absorption gets you
forty.

{{ui: match-interstage}}

Reading the figure, in the order you would work:

1. **The two terminations, as specified.** Parallel 200 Ω ‖ 125 fF on the left; series 1.25 Ω + 10 pF on
   the right. Band 3.3–5.0 GHz, order 4, Chebyshev/Fano.
2. **The Q values the status strip reports: Q1 = 0.638, Q2 = 3.134.** The series end is the higher-Q end,
   so it is the analysis end and the synthesis prescribes its absorbed element there.
3. **The ladder, from the analysis end.** `C4 = 10 pF` is the load's own 10 pF, exactly — that is the
   absorption, visible as an element you did not have to add. Then `L4 ≈ 154 pH`, `C3 ≈ 82.9 pF`,
   `L3 ≈ 18.5 pH` and onward.
4. **The far end does not land on 200 Ω by itself.** Synthesised bare, it comes out at 1.68 Ω, so the
   required Π N² is **119.03** — and the status strip says so. That is what the transforms are for.
5. **Two Norton transforms applied** — the first-ranked solution from the list — bring the product to
   119.027 / 119.027 and the strip reads **✔ matched**.
6. **The achieved response: worst in-band return loss 16.66 dB, insertion loss 0.095 dB, ripple
   0.036 dB**, over a 42% fractional bandwidth into a 160:1 transformation with a capacitive load at
   both ends. That is the number to compare against whatever you were doing before.

The element values shown are what the synthesis computes, and they are the same values the component
stamps and the same values [Flatten to Cell](#flatten) writes out.

## Flatten to Cell {#flatten}

**Flatten to Cell…** turns the design into an ordinary editable cell. What it writes:

- **Two `Pin` components**, matching the `Match` symbol's pins, so the cell is pin-compatible with the
  component it replaces.
- **The network as ordinary `L` and `C` instances**, placed on a grid — series arms along the spine,
  shunt arms to ground, with wires. A series L+C arm is written as **two components**, not one element
  with a `C=` parameter, because your next action is to edit, sweep or replace individual elements.
- **Both terminations**, as a `Term` plus the absorbed reactive element, **all disabled (open)**. They
  record what the network was designed for without affecting the netlist.
- **A text annotation** listing the design: band, order, response, both terminations, achieved return
  loss, insertion loss, ripple, Π N², and the date.
- **The design record itself**, carried onto the cell, so *Re-open in Match Designer…* can reconstruct
  it later. A flattened cell that has forgotten what it was is a dead end.

A checkbox, on by default, **replaces the instance in place** with one of the new cell. The symbol and
pin positions are identical, so the wires stay connected and the schematic is immediately runnable. The
whole operation — create cell, write files, replace instance — is one undoable command.

**When you would flatten.** When you want to hand-tune an element, sweep one, substitute a real
component model or a [PCell](pcells.html) for an ideal one, or lay the network out. **What you lose** is
the live link: the cell is a circuit, not a design. Editing an element changes the circuit and nothing
re-synthesises. That is the point of flattening, and it is also why the design record travels with it.

**Why the terminations are disabled rather than omitted.** Omitted, the design intent is lost the moment
someone opens the cell. Enabled, the cell would short its own ports when placed. Disabled, the cell
simulates correctly against the real circuit **and** anyone who wants to reproduce the Designer's plot
can enable the two `Term`s and run an S-parameter analysis on the cell alone.

## Getting the design out {#exports}

- **Touchstone (`.s2p`)** of the design response, with the per-port references R1 and R2 written as the
  file's own.
- **Component listing (`.csv`)** — instance, type, value, unit: the same rows as the grid view.
- **Prototype g-values (`.csv`)**, for anyone checking the synthesis against a published table.

**Settings** holds the display units per dimension, the significant digits, the minimum Q for
Q-adjusted solutions, and whether to offer them at all. Inductance and capacitance default to **pH**
and **pF** rather than to *Auto*: a fixed unit makes a column of values directly comparable, where
*Auto* picks per value and leaves you converting "1.53 nH" against "680 pH" in your head.

## References {#refs}

The synthesis rests on published work, and it is worth naming so a sceptical reader can check it:

1. G. Matthaei, L. Young, E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling
   Structures*, McGraw-Hill 1964 / Artech 1980 — §4.09 (matching networks with a prescribed load
   decrement), §4.12 (Norton transforms).
2. R. Levy, "Explicit formulas for Chebyshev impedance-matching networks, filters, and interstages,"
   *Proc. IEE*, vol. 111, no. 6, pp. 1099–1106, June 1964 — the closed-form recursion the prototype
   uses.
3. D. E. Dawson, "Closed-form solutions for the design of optimum matching networks," *IEEE Trans.
   MTT*, vol. 57, no. 1, pp. 121–129, January 2009.
4. T. E. Shea, *Transmission Networks and Wave Filters*, Van Nostrand 1929, p. 325 — the Norton
   transforms.
5. R. M. Fano, "Theoretical limitations on the broadband matching of arbitrary impedances," *J. Franklin
   Inst.*, 1950 — the bound that makes absorption the right idea, and perfection impossible.
