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
<li><a href="#dcblock">DC Block: a DC-blocking capacitor on a biased end</a></li>
<li><a href="#transforms">Norton transforms: moving values without moving the response</a></li>
<li><a href="#solutions">The solutions list</a></li>
<li><a href="#feasibility">Feasibility: what is possible before you synthesise anything</a></li>
<li><a href="#worked">Worked example: a two-stage FET interstage match</a></li>
<li><a href="#dualband-worked">Worked example: the same stages, matched over two bands</a></li>
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

### The symbol tells you what it is

A Match puts **no parameter text on the schematic** — no `F1`, no `F2`, no `Order`. Everything about it
is edited in the Match Designer, and everything about it is read there or in the Properties panel, so
three numbers beside the symbol were three numbers that could not be acted on where they stood.

The **glyph** says the part you need at a glance. Its three stacked waves are a frequency axis, highest
at the top, and a slash means that band is blocked:

| The symbol shows | The network is |
|---|---|
| slashes top and bottom | **bandpass** — the middle band passes |
| slashes on the top two waves | **lowpass** |
| slashes on the bottom two waves | **highpass** |
| two smaller bandpass glyphs, side by side | **dual-band** |
| three smaller bandpass glyphs, two below one | **tri-band** |

{{ui: match-form-glyphs}}

It follows the design: apply a lowpass solution and the symbol on the page becomes a lowpass symbol,
and switching **Bands** to *Dual* or *Tri* splits the wave stack into two or three smaller ones. The
glyph is drawn from the component's own `Form` and `Bands` parameters, so a schematic printed for a
review states the topology of every matching network on it without anyone opening a Designer.

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
| **DC Block** | Inserts a DC-blocking capacitor in series with the first shunt inductor on this end's DC path, and enlarges the inductor to compensate — see [DC Block](#dcblock). Available whenever there is such an inductor; greyed out, with the reason, otherwise. |
| **Conjugate** | Targets Z* instead of Z — which flips the reactance sign, and so turns a measured parallel R‖C into a parallel R‖L target. |
| **Bands** | Single, Dual or Tri. Dual and Tri match two or three bands at once — see [Multiband](#dualband). |
| **Band f1, f2** | The passband. Everything is computed at ω₀ = √(ω₁ω₂) with fractional bandwidth *w*. |
| **Band f3, f4** | The second band, when Bands is Dual — or the **middle** band, when it is Tri. |
| **Band f5, f6** | The third band, when Bands is Tri. |
| **Order** | Number of in-band match points, 2–6, restricted to the parities the terminations permit in bandpass form. Multiband counts match points **per band** and offers 1–3. The element count depends on your two terminations: **2n** (or **4n** multiband) for a mixed or resistive pair, and one arm more — **2n + 1**, or **4n + 2** — when both ends are the same topology. The tooltip beside the picker states which. |
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

## DC Block: a DC-blocking capacitor on a biased end {#dcblock}

**A shunt inductor at a biased node is a short across the supply.** If the end of your ladder is a shunt
inductor and the termination behind it carries DC — a drain, a gate — the network as synthesised puts
your supply on ground. The **DC Block** toggle on that termination's card fixes it, and fixes the thing
people normally get wrong afterwards.

Click it and two things happen:

- **A capacitor goes in series with that inductor.** It appears in the network pane as an element of its
  own, named after the inductor it blocks — `L1` blocks with `L1blk` — and it is written into the
  flattened cell as a real component.
- **The inductor is enlarged so the branch is unchanged at band centre.** The branch's reactance is now
  `ωL′ − 1/ωC` instead of `ωL`, so `L′ = L + 1/(ω₀²C)` makes it exactly what the synthesis asked for at
  ω₀. That is the number the ladder shows: `L1` reads its *compensated* value, and the status line says
  what it was before.

The compensation is **exact at band centre and second-order away from it**. The branch's own series
resonance sits below the band at `f_s = 1/(2π√(L′C))`, and between the two band edges the effective
inductance runs a little either side of what the synthesis wanted. The status line states all of it:

> DC block at termination 1: 1 nF in series with L1 (105.9 pH, from 99.5); branch resonates at
> 489.1 MHz; inductance ±1.3 % across the band. Feed the bias through L1, not through a separate choke.

**Bigger is better, and the default is chosen for you.** The seed puts `f_s` at about one tenth of band
centre, which keeps the spread under a percent — and it is capped, because at a low band with a small
end inductor that rule alone asks for tens of nanofarads: fine on a board, impossible on an MMIC. The
cap is in **Settings ▸ DC block default**. Past that, the value is yours: type anything positive into
`L1blk` and it is compensated exactly at ω₀ whatever it is. Above `f_s > f₀/5` the line turns to a
warning and tells you what a ten-times-larger part would buy — measured on a 20 % band, a 500 pF block
costs about 3 dB of worst-case return loss where 10 nF costs none. Typing `0` removes the block.

<div class="callout warn">
<span class="label">Feed the bias through the compensated inductor, not through a separate choke</span>
<p>This is the half that no amount of RF design catches. Put the block in the branch and then feed the
drain through a <em>separate</em> choke, and the block resonates against that choke <em>through the
drain node</em> — a parallel pole in the middle of the baseband, tens of kilohms at a few megahertz, and
no lossless network can remove it. Feed the supply <em>through</em> the compensated inductor instead,
with the block as its far-end decoupling, and the residual poles are between your decoupling capacitors,
where a small series resistance on a capacitor carrying no RF damps them. Check it the ordinary way: an
S-parameter or AC sweep of Z at the drain node in the schematic.</p>
</div>

**The block goes on the first shunt inductor your bias current would reach.** A series inductor in
the way doesn't stop it, a real series capacitor does, and your FET's own input capacitance is not a
real capacitor on the board. So a termination whose end arm is a *series* arm — a gate modelled as R in
series with C_gs — still gets a block: the arm's capacitor is the device's own and is left out of the
flattened cell, the arm's inductor passes DC, and the ladder's next shunt inductor is what would short
the gate bias. The toggle names that inductor and the one the bias reaches it through, and the status
line's feed rule says the same: *feed the bias through L3; it reaches the termination through L4*. A
Norton T on the end pair, which turns the end arm into a series inductor, moves the block one product
in the same way — and a Norton **π** of inductors, whose series product passes DC between its two
shunt products, gets a block on *each* of them, with your one value and each inductor compensated on
its own.

**Where the toggle is greyed out, it says why.** When Match has put a **real** capacitor of its own in
that end's through path — a `CFano` or `CDetune` from a termination whose Q is far below what the
synthesis needs — that capacitor already isolates the termination, and a block on a shunt inductor
beyond it would protect nothing; feed that termination's bias on its own side of the named capacitor
instead. A **lowpass** ladder has no shunt inductor at all — it passes DC end to end, and blocking that
would need a series capacitor in the through path, which is a different network and a different
compensation; Match does not offer it. Shunt inductors beyond the next real series capacitor never
need one: that capacitor ends the DC path.

A block is a specification input, not something the synthesis chose. It is applied **after** the
transforms, attached by *node* rather than by name, so a Norton transform that replaces `L1` with a
product still leaves the block on whatever shunt inductor is first on that end's DC path — and
switching the end pair between π and T keeps your value and moves the block to the new host. Nothing
in the solutions list, the transform ranges or the feasibility numbers changes when you set one.

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
are matched between f1 and f2, and all three use 2n elements at order n (2n + 1 when both terminations
are the same topology) — the lowpass form is not the cheaper one.

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
- **Two terminations of the same topology are fine here too, at one element more.** The ladder then has
  an odd element count, both of its ends face the same way, and both are absorbed. Its two ends flip
  together, so a parallel pair has to be analysed from the *higher* resistance and a series pair from
  the lower; if you have it the other way round the refusal says which end to analyse from. Odd counts
  are Chebyshev only.

#### The math behind the three forms {#forms-math}

All three forms come out of **one two-parameter prototype family**, evaluated in the squared frequency
variable `u = Ω²` with the band mapped onto `[-1, 1]`. With `a = F1/F2`:

```text
x(u) = (2u − 1 − a²) / (1 − a²)             the band [a², 1] mapped onto [−1, 1]

Φ(u) = T_n(x(u))²        Chebyshev          T_n = the nth Chebyshev polynomial
Φ(u) = x(u)^(2n)         Butterworth

|Γ(u)|² = (K + ε²·Φ(u)) / (1 + ε²·Φ(u))
```

`Φ` has **maximum 1 in band**, so the worst in-band reflection is `|Γ|²_worst = (K + ε²)/(1 + ε²)` —
which is why the two free parameters are exactly `K` and `ε²`, and why the panel can quote a return
loss before it has drawn an element.

**`K` is not free: it is pinned at DC, and that is the whole cost of the lowpass and highpass forms.**
A ladder of single elements is transparent at `u = 0` — a lowpass ladder's inductors are shorts and its
capacitors are opens — so the network's reflection there is whatever the two resistances make it:

```text
Γ(0) = (r − 1)/(r + 1)        r = R_far / R_analysis        K = Γ(0)²
```

That number comes out of the same budget as the in-band match, which is the whole reason a large
impedance ratio costs return loss in these forms and costs nothing in bandpass form — the figures
quoted above are this expression. (`K = 0` exactly is a trap rather than the ideal case — the numerator's roots
then sit in double pairs on the jω axis and there is no well-defined spectral factor — so circuitRF
floors it at 1e-12, a −120 dB ceiling that is past anything a matching network means. The equal-
resistance case `r = 1` drops the pin entirely and makes `ε` the free parameter instead.)

With `K` and `ε²` chosen, the reflection's numerator and denominator are both of the form `c + ε²Φ`, so
their roots are written down rather than searched for — one arccosine (Chebyshev) or one root of unity
(Butterworth), mapped `x → u → s` — and the ladder falls out of a continued-fraction (Cauer) expansion.
**Order `n` gives `2n` elements** — `T_n(x)` is degree `n` in `x` and `x` is degree 1 in `u`, so `Φ` is
degree `2n` in `u` — or `2n + 1` when both terminations are the same topology, where the family gains a
third parameter: an extra pole at `u = −u_R`, outside the band.

**Denormalising is where lowpass and highpass part company**, and it is the only place they differ. The
prototype `g`-values become elements at a single reference frequency — the **top** of the band for a
lowpass network, the **bottom** for a highpass one, because that is the edge each form is matched up to:

```text
lowpass   ω_ref = 2π·F2      shunt C:  g = ω_ref·R·C        series L:  g = ω_ref·L / R
highpass  ω_ref = 2π·F1      shunt L:  g = R / (ω_ref·L)    series C:  g = 1 / (ω_ref·R·C)
```

Read the highpass row as the lowpass row with `ω → −1/ω`, which is the classical lowpass-to-highpass
transformation: every series inductor becomes a series capacitor and every shunt capacitor a shunt
inductor. Both ends normalise at the **analysis** end's resistance, not each at its own — in this
prototype the terminating resistance is a *ratio* and element values do not rescale at the far port.

Two consequences fall straight out of those four expressions:

- **Which reactance each end can absorb is fixed by the form and by the ratio.** A lowpass ladder holds
  a series L or a shunt C and nothing else; a highpass ladder a series C or a shunt L. The
  low-impedance port takes the series arm and the high-impedance port the shunt arm, so a shunt
  capacitance absorbs on the high side of a step-down and is refused on the low side of a step-up.
- **The values stay tame.** There are no resonators, so nothing is set by a fractional bandwidth: at a
  wide band a bandpass arm's two elements are pushed apart by roughly `1/w` while these stay within a
  factor of a few of each other.

A highpass design additionally requires `F1 > 0` — it is matched between F1 and F2 and pinned at
infinity, so a zero lower edge is the lowpass form's degenerate case and is refused by name.

### Multiband: two or three bands at once, and the gaps left alone {#dualband}

Set **Bands** to *Dual* and a second pair of edges appears. The network is then matched over **f1–f2
and f3–f4 together**, and the region between them is deliberately *not* matched. *Tri* adds a third
pair, f5–f6, and two such gaps.

That is the whole idea rather than a compromise. The Fano bound is a fixed budget spread over all
frequency; a single wide match from f1 to f4 spends it across the whole span, gap included, and
everything spent between f2 and f3 is wasted if your application does not use those frequencies. A
dual-band network spends the budget in the two bands and leaves the gap reflecting. For 20 Ω ‖ 2.5 pF
into 50 Ω over 2.4 GHz and 5 GHz Wi-Fi, eight elements reach **−31.8 dB in both bands**, against
−18.8 dB for a single-band eight-element network covering the whole span.

**The gap mismatch is the design working, so the status strip states it.** Beside the worst in-band
return loss you get a line like *gap 2.5–5.15 GHz: max |S11| 0.445 (−7.0 dB)*, and that number **rises
with order** — a higher-order network is bigger in the gap, and that is exactly where the extra in-band
return loss comes from.

Three things to know before you type four frequencies:

- **Type the edges in any order.** f1…f6 are one ordered list of passband boundaries, so if you put a
  number in the wrong field the Designer sorts them back into increasing order and renumbers the
  fields in front of you. What it cannot fix is two edges that are equal — a zero-width band — and
  that stays a refusal.
- **The bands must be geometric mirrors of each other** — `f1·f4 = f2·f3`, which is the same as
  equal ratio bandwidths `f2/f1 = f4/f3`. Yours will not be. circuitRF keeps the wider band exactly,
  **widens the narrower one away from the gap** until the ratios match, and says so in one line under
  the fields: *"Band 1 widened to 2.201–2.5 GHz to mirror band 2 about 3.588 GHz."* The design is
  always to the effective bands, never silently to the ones you typed. Where the two ratio bandwidths
  differ a lot the stretch is severe and this is the wrong tool — a 0.9–1.0 GHz band paired with
  3–6 GHz would be designed as 0.5–1.0 GHz, which is not what you asked for.
- **Order is match points per band**, so orders 1, 2 and 3 give 4, 8 and 12 elements for a mixed
  termination pair — the same twelve a single-band order-6 network gives, which is the fair comparison.
  Two bands stop there. **Three bands go on to orders 4, 5 and 6** — 16, 20 and 24 elements — for the
  reason the Feasibility section below explains: a tri-band spec with a narrow middle band is not
  three bands at all until order 4. Higher is not automatically better; the solutions list spans every
  order and ranks by return loss, so pick by the number on the card rather than by the order.
- **Two terminations of the same topology get one arm more**, not a refusal. A shunt-C-to-shunt-C
  interstage needs a ladder whose two ends face the same way, which is an odd number of arms, so those
  orders give 6, 10 and 14 elements instead. You do not choose this — your terminations do, and the
  order picker's tooltip says which count you are getting. The extra arm buys the absorption, not
  return loss: at the same order the odd ladder measures within 0.01 dB of the even one.

Everything else is unchanged. The terminations are read at ω₀ — which is now the gap centre, where
every arm of the ladder is transparent — the ladder is an ordinary bandpass ladder with twice as many
arms, so Norton transforms, absorption, the excess-element rule, the solutions list and Flatten all
work exactly as they do for a single band.

#### Three bands

*Tri* works the same way with one difference in the rule above: **the middle band is the one that is
kept**, because a three-band response has to be symmetric about its own centre and only the middle band
can straddle it. Bands 1 and 3 are widened onto each other's mirror image about ω₀ = √(f3·f4), and the
note names every band that moved. Switching from *Dual* to *Tri* therefore moves your existing second
band out to f5–f6 and seeds a new middle band between them, rather than hanging a third band off the
end where it would immediately be mirrored on top of the second.

The status strip shows **two gap lines**, one per gap. On a spec that already mirrors they come out
equal; they separate as soon as the middle band sits off centre.

Three bands are **Chebyshev only**. Butterworth means maximally flat at the middle of one passband, and
three bands do not have a single middle to be flat at — the equal-ripple answer is the only one there
is, so that is what is offered. For 50 Ω ‖ 4 pF over 0.5–0.6, 0.9–1.1 and 1.65–1.98 GHz, eight elements
reach **−12.0 dB in all three bands**, twelve reach −14.5 dB and sixteen reach **−18.9 dB**.

**Multiband is bandpass only**, and the solutions filter says so in place of its form group: the
lowpass and highpass forms of the previous section have no multiband version yet. Nor do asymmetric
bands — bands whose ratio bandwidths are genuinely different, matched as requested rather than
widened. Both are recorded as future work.

#### The math behind two and three bands {#dualband-math}

**There is no separate multiband synthesis.** A multiband network is the *same* single-element
prototype family the previous section describes, pushed through the *same* bandpass transformation an
ordinary Match uses — so what comes out is an ordinary alternating bandpass ladder of `2n`
two-element arms, and Norton transforms, absorption, the excess-element rule, Flatten and the stamp all
handle it with no multiband case anywhere.

**Step 1 — the bands are made mirror images.** A real network's `|Γ(jΩ)|²` is an *even* function of Ω,
so the passbands a resonated ladder produces are mirror images about ω₀ in log frequency. That is one
equation, and it is what your four (or six) numbers have to satisfy:

```text
two bands    f1·f4 = f2·f3                    ⇔  f2/f1 = f4/f3   (equal ratio bandwidths)
three bands  f1·f6 = f2·f5 = f3·f4            the MIDDLE band is the one kept
```

Yours will not satisfy it. circuitRF keeps the wider band exactly, widens the narrower one *away from
the gap* until the ratios match, and states the result in one line under the fields. Everything from
here uses the **effective** bands.

**Step 2 — the bandpass transformation.** With `ω₀` the geometric centre and `w` the **outer**
fractional bandwidth:

```text
two bands    ω₀ = 2π·√(f1·f4)      three bands  ω₀ = 2π·√(f3·f4)   (the middle band's centre)
w = (f_high − f_low) / √(f_low·f_high)          the outer pair, gap included

Ω(f) = ( f/f0 − f0/f ) / w                      the standard bandpass map
```

Each single prototype element becomes a two-element resonant arm at ω₀, exactly as it does for one
band. **The gap is where Ω is small**: the mapping folds the region between the passbands onto the
*middle* of the prototype axis, so a prototype that has a **stopband there** is a network that has an
unmatched gap there. That is the whole trick.

**Step 3 — where the passbands land in the prototype variable.** In `u = Ω²`, and with the mirror
relations above collapsing every square root into a ratio of frequency *differences* over the outer
span:

```text
one band     u ∈ [0, 1]

two bands    u ∈ [a², 1]                    a = (f3 − f2)/(f4 − f1)
             Φ(u) = T_n(x(u))²              x(u) = (2u − 1 − a²)/(1 − a²)

three bands  u ∈ [0, a²] ∪ [b², 1]          a = (f4 − f3)/(f6 − f1)
             Φ(u) = p(u)²                   b = (f5 − f2)/(f6 − f1)
```

Two bands is therefore **literally the lowpass-form family of the previous section, on the interval
`[a², 1]` instead of `[0, 1]`** — written down by arccosine, no root-finding. Three bands is the only
place a new object appears: the passband is a **union** of two intervals, and `p` is the equal-ripple
polynomial on that union, produced by a Remez exchange rather than by a formula. `max Φ = 1` in band
either way, so the worst in-band reflection is still `(K + ε²)/(1 + ε²)` and the parameter search does
not know which case it is looking at.

**This is why Butterworth exists for two bands and does not exist for three.** The Butterworth member
is `x(u)^(2n)` — maximally flat at the centre of *one* interval — and a union of intervals has no
single centre to be flat at. The equal-ripple answer is the only member of the family that lives on a
union, so **tri-band is Chebyshev only** and the others are refused by name rather than silently
substituted.

**The two free parameters are chosen exactly as §Feasibility describes.** The near end's absorbed
element is pinned by the termination — `g₁ = Q_analysis · w`, with Q read at ω₀, which for a multiband
spec is the *gap* centre where every arm is transparent — and the remaining freedom minimises the worst
in-band `|Γ|²`. `K` is **scanned** rather than solved, because the near element is not monotone in it
(it rises and then falls); `ε²` is then bracketed and bisected at each `K`, where it is monotone. The
optimum is flat — the worst return loss moves by at most 0.1 dB across a whole decade of `K` — so a
64-point log scan plus a bounded refinement is ample and there is no root-finding subtlety to tune.

**Element counts, and where the +2 comes from.** `2n` arms of two elements is `4n`, and one arm more —
`4n + 2` — when both terminations are the same topology. Orders count match points **per band**, which
is why dual-band stops at 3: order 3 is twelve elements, the same twelve a single-band order-6 network
gives, and that is the fair comparison. Tri-band goes on to 6 because a narrow middle band is not three
bands at all until order 4 — before that the middle passband has no ripple of its own to speak of.
A design may then carry two more elements than the arithmetic above: an **excess** element where the
synthesised end value exceeds what the termination supplies, and one extra arm per **Norton transform**
that was applied.

## Feasibility: what is possible before you synthesise anything {#feasibility}

A lossless network cannot match a reactive termination arbitrarily well over an arbitrary bandwidth.
There is a hard ceiling, it depends only on the terminations and the bands, and the Designer shows it
**beside** every result and **before** any of them — the status strip's line reads

> Fano ceiling 6.4 dB (termination 2, over the bands)

quoted positive like the return loss above it, and naming which of the two ends is the one setting it.
Hover it for both ends, the ceiling over the bands as you typed them, the ceiling over the whole span
from your lowest edge to your highest, and — for a multiband spec — how much of the ceiling the
mirror widening cost. **The line is there even when the synthesis refuses**, which is when it usually
matters most: a refusal and a ceiling of −3 dB are the same fact, and only one of them tells you what
to change.

When the line ends **"— at the ceiling"**, the design is within a dB of what physics allows and no
amount of searching will improve it. Anything else is headroom.

### Which end limits you, and what makes it worse

Two of the four terminations are limited by **total bandwidth**, and two by your **lowest band edge**:

| your termination | what costs you |
|---|---|
| R ‖ C (shunt capacitance) | total bandwidth — the sum of all your band widths |
| R + L (series inductance) | total bandwidth |
| R + C (series capacitance) | the **lowest** frequency you ask for |
| R ‖ L (shunt inductance) | the **lowest** frequency you ask for |

That difference matters. With a series capacitance, moving your lowest band edge up by a few hundred
megahertz can be worth more than everything else put together; with a shunt capacitance it is the
total width that counts and where the bands sit hardly matters at all.

### The hints

When the ceiling is genuinely what is stopping you — the search came back empty, or what it found is
already against the wall — a line appears under the solutions list saying so and offering up to four
one-variable changes that would reach −15 dB:

> The best any lossless network can do here is -6.4 dB, set by termination 2 (1.25 Ω + 5 pF series)
> over 2.25–3 / 4.5–5 / 7.5–10 GHz. To reach -15 dB: termination 2's capacitance at or above 11.7 pF;
> or band 1 starting at 2.86 GHz instead of 2.25; or without band 1 the ceiling over bands 2 and 3 is
> -32.1 dB; or band 1 as 2.25–2.5 GHz mirrors band 3 without widening (ceiling -13.8 dB).

Each clause holds everything else fixed and solves for the one thing it names, so they are alternatives
rather than a recipe. **They are ceilings, not designs**: reaching one still needs an order and a
family that fit, and the ladder that gets there may be a longer one than you wanted. It is a hint and
never a refusal — solutions that exist are still listed beside it.

### Does your order actually use the gaps?

A multiband network buys its in-band return loss by leaving the gaps alone. **At low order and with a
narrow middle band, the prototype may not exclude them at all** — the equal-ripple polynomial on your
bands turns out to be the same polynomial as a single wide match over the whole span, and the result is
one broad mediocre match instead of two or three good ones. This is not a fault in the synthesis; at
that order no polynomial does better.

The Frequency Band card says so when it happens:

> At order 2 the tri-band prototype does not exclude the gaps — this is a single-band match over
> 2.25–10 GHz (ceiling -3.1 dB). The gaps open at order 4 (rise ×2.9).

and each gap line in the status strip carries the same measurement as a **prototype rise** factor: ×1
means the gap is not being excluded, and the larger it grows the more of the budget is being reclaimed
from the gap and spent in your bands. If the note says no offered order opens them, the band geometry
itself is the problem — widen the middle band, or move the outer bands closer together.

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

## Worked example: the same stages, matched over two bands {#dualband-worked}

The same two stages as the previous section: **200 Ω ‖ 0.125 pF into 1.25 Ω + 10 pF.** This time the
radio only ever operates in two narrow bands with 200 MHz of nothing between them, so there is no
reason to spend the Fano budget on the gap. Everything below is reproducible — place a `Match`, open
the Designer, and type these numbers.

### What to type

| Field | Value |
|---|---|
| **Termination 1** — topology / R / X kind / value | Parallel · `200 Ω` · C · `0.125 pF` |
| **Termination 2** — topology / R / X kind / value | Series · `1.25 Ω` · C · `10 pF` |
| **Bands** | Dual |
| **Band f1, f2** | `1.75 GHz`, `1.9 GHz` |
| **Band f3, f4** | `2.1 GHz`, `2.2 GHz` |
| **Order** | 3 (three match points **per band**) |
| **Response** | Chebyshev — single-match |
| **Form** | Bandpass (multiband is bandpass only) |
| **Response pane ▸ ± band** | `20` % |

{{ui: match-dualband}}

### Reading the result

1. **A note appears under the band fields, and it is not a warning:** *"Band 2 widened to
   2.1–2.28 GHz to mirror band 1 about 1.997 GHz."* The two bands as typed have ratio bandwidths of
   1.0857 and 1.0476, which no even `|Γ(jΩ)|²` can produce; band 1 is the wider one so it is kept
   exactly, and band 2 is widened away from the gap to 2.28 GHz. **The design is to 1.75–1.9 and
   2.1–2.28 GHz** — you get the band you asked for and a little more, never less.
2. **116 solutions, and the applied one is the first card:** *Chebyshev · dual-band · order 3 ·
   1 transform · (L1, L2) · RL 17.06 dB*. The list is the whole order × family cross-product ranked
   simplest first, and the strip along the bottom names what is currently applied.
3. **Fourteen elements.** Order 3 with a mixed termination pair is `4n = 12`; one more comes from the
   Norton transform (a Π replaces two like inductors with three) and one more is the excess capacitor
   `CFano` beside termination 1, where the synthesis wanted more shunt capacitance than the stage's own
   125 fF supplies. `C1 = 125 fF` and `C6 = 10 pF` are the two terminations themselves, absorbed —
   elements you do not have to buy, and the line under the schematic says so.
4. **One Norton Π on `L1`/`L2` at N = 7.198** brings the achieved Π N² to 51.816 against a required
   51.816. Without it the far end lands nowhere near 200 Ω.

The Response pane's readout card carries the rest, and these are the numbers worth checking against
your own run:

| Readout | This design |
|---|---|
| Q1, Q2 (both at ω₀ = 2π · 1.9975 GHz, which sits in the **gap**) | 0.308, 6.49 |
| Fano ceiling, and which end sets it | 25.9 dB, termination 2 |
| Worst in-band return loss, across **both** bands | 17.06 dB |
| Insertion loss / ripple | 0.086 dB / 0.050 dB |
| Gap, peak reflection over 1.9–2.1 GHz | 0.511, i.e. −5.8 dB |

Two of those are worth a sentence each.

**The ceiling is set by termination 2, and that tells you what to change.** A series capacitance is
limited by the **lowest** frequency you ask for, not by total bandwidth — so if this design were short
of return loss, moving f1 up would buy more than anything else on the panel.

**The gap number is the design working**, not failing. The budget a single wide match from 1.75 to
2.28 GHz would have spent on 200 MHz of unused spectrum is in the two passbands instead.

### The response

{{ui: match-dualband-response}}

Two matched passbands, a reflecting gap between them, and the whole thing plotted at **±20 %** of the
band so you can see what happens on either side as well. `|S11|` is on the left axis and `|S21|` on the
right, because at 0.086 dB of insertion loss a shared scale renders `|S21|` as a flat line on the
ceiling.

### What the order buys, and what it costs

Every row below is the same specification with only **Order** changed, and every one is a row the
solutions list offers:

| Order | Elements | Worst in-band return loss | Gap, max reflection |
|---|---|---|---|
| 1 | 6 | 10.5 dB | −8.9 dB |
| 2 | 10 | 14.7 dB | −8.9 dB |
| 3 | 14 | 17.1 dB | −5.8 dB |

**The gap number rises as the in-band number improves**, and that is the mechanism rather than a side
effect: a higher-order network reclaims more of the budget from the gap and puts it in the bands. If
the gap mattered to you, this is the trade you would be making in the wrong direction — and the panel
states it so the choice is yours.

**For comparison**, set Bands back to *Single* and ask for 1.75–2.28 GHz at order 6. That is also
fourteen elements — and it reaches **14.35 dB**, in the passbands and everywhere between them alike.
The dual-band network buys **2.7 dB in both bands** for the same part count, and the only thing it
gave up is 200 MHz the application never uses.

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

**The values are the ones you were looking at.** Every element is written at the **significant digits**
set in *Settings* — an inductor the pane showed as 1.201 pH lands in the cell as 1.201 pH, not as a
fifteen-digit rendering of the double behind it. Raise that setting before flattening when you want the
network carried at full precision; the flattened cell is a rounded copy of the design, and at three
digits its response differs from the `Match` component's by about one part in a thousand.

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

Three files, from the **Export** button:

- **Touchstone (`.s2p`)** of the design response, with the per-port references R1 and R2 written as the
  file's own.
- **Component listing (`.csv`)** — instance, type, value, unit: the same rows as the grid view.
- **Prototype g-values (`.csv`)**, for anyone checking the synthesis against a published table.

**And copy and paste works, from all four views** — which is usually the faster route, because nothing
lands on disk and nothing has to be re-imported.

| Right-click | You get |
|---|---|
| the **network schematic**, or the **value grid** | **Copy** — the ladder as a real schematic selection. It pastes into a circuitRF schematic page as live components and wires, and into a document or slide as **vector** art (SVG and PDF, plus a PNG; on Windows an enhanced metafile, which is the vector form an office suite pastes). |
| the **value grid** | **Copy as CSV** as well — the same rows as the file export, straight onto the clipboard. |
| either **plot** | the Data Display's own **Copy**, which puts the chart on the clipboard as vector art with its markers and info boxes. |

Pasting the network into a schematic page is worth knowing about on its own, when what you want is the
ladder inside a bench you already have open rather than a new cell beside it. **It is not the same
circuit [Flatten to Cell](#flatten) writes, and the difference is deliberate.** Copy gives you the
picture you are looking at — the elements at the values and positions the pane drew them, plus both
terminations as ordinary live components. Flatten writes a cell that *simulates*: interface pins, the
terminations parked in annexes and disabled, and a design annotation. Copy for the drawing, Flatten
for the cell.

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
