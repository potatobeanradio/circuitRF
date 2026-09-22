---
title: railRF
slug: reference/railrf.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > railRF
lede: Power integrity on the board shape you actually drew: DC drop, Z(f) against a target, which capacitors earn their place, and what a re-layout cost.
keywords: power integrity, PDN, power distribution network, decoupling, decap, IR drop, voltage drop, rail, plane, target impedance, anti-resonance, loadswitch, ferrite, via current
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What railRF is, and the four questions</a></li>
<li><a href="#provide">What you provide</a></li>
<li><a href="#q0">Q0 &mdash; is this rail connected, and what does it cost to get there?</a></li>
<li><a href="#q1">Q1 &mdash; does this rail meet its target?</a></li>
<li><a href="#q2">Q2 &mdash; which capacitors are actually doing anything?</a></li>
<li><a href="#q3">Q3 &mdash; did my form factor break it?</a></li>
<li><a href="#speeds">Fast and Accuracy: two readings of the same copper</a></li>
<li><a href="#settings">The Settings flyout, control by control</a></li>
<li><a href="#example">Worked example, end to end</a></li>
<li><a href="#headless">Running it headless</a></li>
<li><a href="#limits">What railRF will not do</a></li>
<li><a href="#notyet">What is not wired up yet</a></li>
</ol>
</nav>

## What railRF is, and the four questions {#what}

railRF answers power-integrity questions about **the board you drew**, not about a rectangle standing
in for it. It reads your Gerbers, drill and stackup, finds the copper belonging to one net, and solves
it &mdash; at DC first, then over frequency.

It is built around four questions, in the order a board designer asks them.

<div class="callout note">
<span class="label">The four questions</span>
<ol>
<li><b>Q0 &mdash; Is this rail actually connected, and what does it cost to get there?</b> Is the net one
region or three islands joined by a neck; how much of the supply is lost between the source and the
load; and which element on the path is responsible.</li>
<li><b>Q1 &mdash; Does this rail meet its target?</b> Z(f) at the load against a flat target or a mask,
with the violations named and your own board's noise frequencies drawn on the same axis.</li>
<li><b>Q2 &mdash; Which capacitors are actually doing anything?</b> Every capacitor ranked by how much
the worst violation would grow if you deleted it.</li>
<li><b>Q3 &mdash; Did my form factor break it?</b> Two designs &mdash; same schematic, same BOM,
different outline &mdash; run and compared, before the second board exists.</li>
</ol>
</div>

{{ui: railrf-window}}

The window opens from **Tools &rsaquo; railRF**, or by opening a `.crail` in the project tree. It is one
resizable, non-modal window per document, and it can sit behind the workspace while you edit the board.

Three buttons on its title bar get a board into it, and they do different things:

| | |
|---|---|
| **Open** | Points the window at something that already exists &mdash; a `.crail` anywhere on disk, or a bare `.clay` layout from any workspace. A `.clay` brings its own technology with it: the stackup is the one that layout references, not whichever workspace happens to be open. |
| **Import a board** | *Creates.* It runs the same Gerber/drill import **File &rsaquo; Import** runs, lands the artwork in a workspace as an ordinary cell with a layout view, mints a technology from the set's own layers, and reads the placement, BOM and netlist you point it at. |
| **Help** | This chapter. |

Opening a `.crail` resolves what the document itself names &mdash; its artwork, that artwork's stackup and
its part library &mdash; so a document opens with its board already on screen. A reference that no longer
resolves is **reported and does not stop the open**: the rails, the ports and the target are the
document and are still readable, and the status strip says why the board is not there.

<div class="callout note">
<span class="label">The board panel is a view, not an editor</span>
<p>railRF <b>shows</b> the artwork; it does not let you change it. Open the <code>.clay</code> in the
layout editor and edit it there &mdash; when both windows are open on the same board they share one
document, so what you draw appears in railRF as you draw it. Every result is <b>cleared</b> when the
copper moves, because the numbers were measured on the board you have just changed; press
<b>Run</b> again.</p>
<p>Everything else about the panel is the layout editor's: the same pan, the same wheel zoom, the same
<b>F</b>, <b>Z</b>, Ctrl/&#8984; +/&minus; and arrow keys.</p>
</div>

<div class="callout note">
<span class="label">Coordinates read in the board's own units</span>
<p>Every position, length and mesh size on this window &mdash; source and load anchors, the placement
column, the drop table, the labels over the artwork, the <b>Mesh cell</b> setting &mdash; is shown in the
unit the <b>layout</b> is set to, which is the same unit the layout editor shows that board in. Change
it there and railRF follows. Positions are stored as exact database units and only <i>displayed</i> in
yours, so nothing is rounded by the choice.</p>
</div>

## What you provide {#provide}

| | |
|---|---|
| **The artwork** | Gerbers plus the drill, a `.clay` layout, or a `.kicad_pcb`. Mandatory &mdash; it is the thing being measured. |
| **The stackup** | A technology whose conductors state a **thickness** and a **conductivity**. Mandatory: copper with neither has no sheet resistance, and a mesh built on it would report a perfect plane. |
| **The rail and its reference** | Which net, and which conductor is its return. **railRF never infers the reference** &mdash; you say which layer it is. |
| **The sources** | Where the rail is fed, and by what: an open-circuit voltage with a series R and L, or a Touchstone file. |
| **The loads and their currents** | Where the rail is drawn from, and how much. Nothing in a BOM or a placement file carries a current, so this is typed. |
| **The parts** | The decoupling, by part number, against a part library (`.crlib`) holding C, the self-resonant frequency, ESR and a bias curve. |
| **The target** | A drop budget in millivolts, a flat Z<sub>target</sub> in milliohms, a per-port mask, or ΔI/ΔV/rise-time to derive one from. |
| **The band and the aggressors** | The frequency span to sweep, and the things on your board that actually generate energy &mdash; the crystal, the converter, the radio reference. |

<div class="callout note">
<span class="label">A two-pin part placed end for end</span>
<p>A resistor, capacitor or inductor looks the same at 0° and at 180°, so a footprint dragged onto its
lands can easily sit with its pin 1 on the copper its pin 2 belongs to. railRF reads which way round each
one really is from the copper it sits on, and names every part it read as turned under the pick list.
<b>Turn them in the layout</b> turns those parts 180° about their own lands, so the layout says what
the copper shows. With the layout open in its own window it is one undoable edit there; otherwise railRF
writes the <code>.clay</code> itself. Where the copper cannot tell which way round a part is, nothing is
turned, and picking the net says which other nets' pins are standing on its copper.</p>
</div>

<div class="callout note">
<span class="label">A plane the stackup never received</span>
<p>An inner plane imported from a Gerber file named after its net comes in as a drawing layer, and a
conductor added to the stackup by hand does not know about it. Where one unattached drawing layer is the
plausible match, the technology editor's warning names it and offers an <b>Attach</b> button that joins
the two in one press.</p>
</div>

<div class="callout note">
<span class="label">A load with no current is an observation port</span>
<p>Leave a load row's current <b>empty</b> and it stops being a load: it contributes nothing to the DC
solve and is still reported &mdash; listed as <i>observed</i> rather than quietly dropped &mdash; and over
frequency it is a place the impedance is judged. An empty current and a stated zero are different
statements, so railRF never defaults one to the other.</p>
</div>

## Q0 &mdash; is this rail connected, and what does it cost to get there? {#q0}

Press **Run**. You get, for each port, the voltage there and how far below the source it is; and, under
it, **where the drop went** &mdash; every element on the path, ranked, in millivolts and in milliohms.

```
48.698 mV below the source, drawing 350 mA

    22.722 mV   47%    64.919 mOhm   26.462 mm of 0.209 mm BOT copper (126.9 squares)
        21 mV   43%        60 mOhm   the source's own series resistance
     2.023 mV    4%     5.781 mOhm    4.814 mm of 0.438 mm TOP copper (11 squares)
     1.904 mV    4%     5.441 mOhm    3.927 mm of 0.387 mm TOP copper (10.1 squares)
      0.61 mV    1%     1.743 mOhm   the reference return
     0.219 mV    0%     0.627 mOhm   2 parallel vias, 0.3 mm plated over 1.57 mm at 25 um
```

**Expect the copper to be a large term, not a rounding error.** On a compact board with thin inner
copper &mdash; 0.15 to 0.5 mm traces, half-ounce copper, runs far longer than they look &mdash; a single
supply trace is routinely tens or hundreds of milliohms, which is more than everything else on the
board put together. In the run above it is nearly half the budget.

Three other things come out of the same solve.

- **Islands.** The run says how many regions the rail is, and how many the reference is. *"The rail is
  three regions"* on a net you believe is one is the answer to a question you had not asked yet.
- **A drop map** on the board, so the gradient has a place rather than a number.
- **The via current check.** Each layer transition is grouped with the barrels that share it, the worst
  barrel's current compared against a stated limit, and a count given that would clear the flag.
  The limit's basis travels with it, because **the plating thickness is worth a factor of two** and the
  drill file does not carry it: state it in the stackup's via entry or in the document's own settings,
  and railRF says which it used. Where nobody stated one it falls back to a drill-size table and says
  *that*, rather than pricing a barrel nobody measured.

## Q1 &mdash; does this rail meet its target? {#q1}

{{ui: railrf-impedance}}

Scroll the results column past the DC answers and it judges each observation port's |Z| against its
target, reporting the verdict as a sentence: *"Passes by 0.6 dB at its worst, 100 MHz."* A violation
names its frequency and its margin in decibels.

Three things are on the plot besides the curve and the mask.

- **The anti-resonances**, each attributed: *L(C9) against C(C7&ndash;C8)*. A peak you cannot attribute
  is a peak you cannot fix.
- **Your aggressors**, as vertical lines at each fundamental and its harmonics.
- **The coincidences** &mdash; where one of those lines lands within a stated fraction of a peak. Neither
  the peak nor the harmonic is alarming alone; the two together are the finding.

<div class="callout note">
<span class="label">The frequencies the search added are marked</span>
<p>A plane resonance is narrow and a logarithmic grid steps straight over one, drawing a curve that
looks perfectly smooth. railRF's resonance search adds points where the peaks actually are &mdash; and
<b>says which points it added</b>, because a curve whose x-axis grew is one you cannot overlay on a
measurement taken at the frequencies you asked for.</p>
</div>

## Q2 &mdash; which capacitors are actually doing anything? {#q2}

You have forty decaps because the reference design had forty decaps. Some of them sit two millimetres
from a lower-inductance part and contribute nothing.

railRF re-solves the whole sweep once per capacitor and reports what deleting each one would cost:

```
C10  100 uF bulk    12.1 dB  -> -11.5 dB.  Removing it fails the target outright.
C1   100 nF 0402     1.1 dB  ->  -0.4 dB.  Removing it fails.
...
C13  100 nF 0402     0.4 dB  ->   0.2 dB.  Removing it still passes.
```

A row reading **0.0 dB** is a part whose removal leaves the worst margin exactly where it was &mdash; a
candidate for deletion. That is not parallelism: removing one of *N* equal parts in parallel moves the
answer by about 8.7/*N* dB, so no bank of a plausible size is redundant that way. A genuine 0.0 dB row is
a part that is **out of band where the judging happens** &mdash; shadowed by a neighbour with less
mounting inductance.

Today the way to find this out is to build the board and start removing parts with a soldering iron.

## Q3 &mdash; did my form factor break it? {#q3}

A supplier gives you a reference design. You re-shape it to fit an enclosure: same schematic, same BOM,
different outline and different placement. **Compare&hellip;** runs both and reports the delta.

The pairing is the part worth knowing about, because it is the part that could quietly compare the wrong
things:

- Ports and parts are matched **by refdes**, and models by part number only where exactly one unmatched
  row on each side carries that number. Anything else is reported as unmatched, with the count.
- **Nothing is matched by proximity.** Two ports 0.2 mm apart stay unmatched however close they get;
  two ports at exactly the same coordinate are the port that did not move.
- **Two sweeps on different frequency grids are refused, not interpolated.** The resonance search adds
  points where each board's own peaks are, so two boards come back on two axes as a matter of course
  &mdash; and they differ precisely where the curve is changing fastest. Sweep both sides on one explicit
  grid instead.
- The delta is a **ratio in decibels**, not a difference in ohms: a PDN curve crosses three or four
  decades, so a difference in ohms is mostly a picture of where the curve is big.
- An excursion band ends where the **sign** changes. A resonance that moved is two findings &mdash; worse
  here, better there &mdash; and merging them would put the reported peak at the one frequency where
  nothing happened.

## Fast and Accuracy: two readings of the same copper {#speeds}

{{ui: railrf-classification}}

railRF reads your copper two ways. **Fast** &mdash; the default, and what makes the window re-solve while
you type &mdash; reduces each trace section between junctions to one resistance from its own measured
length and width, and meshes only copper that is not trace-shaped. **Accuracy** meshes all of it.

**What Accuracy buys is a bounded answer on copper the closed form cannot price.** The fast reading is
exact on a ribbon and optimistic wherever current spreads across a width instead of filling it &mdash; and
optimistic is the direction that matters, because it is the one that turns a marginal board into a
passing report. Four rules make the two safe to have together:

1. **Every result says which model produced it** &mdash; on the plot, on each table, in the status strip
   and in the provenance of every export. A pass in Fast is reported as *a fast-model pass*, never as a
   pass.
2. **The classification is visible and correctable.** The figure above is the `class` tab: it draws which
   copper was read as a trace and which was meshed, and a region can be forced either way. A wide supply
   polygon mistaken for a trace is optimistic and invisible &mdash; drawing it is what makes it neither.
   The reference plane is drawn first and the rail's own sections over it, so a board with a plane shows
   both; click a region to force it either way.
3. **Fast refuses where it cannot be honest.** Where a source reaches a load *only* through copper
   classified as spreading, the fast model produces no number rather than a smaller one, and the refusal
   names both answers: run Accuracy, or force the region to `trace` if you know the current follows a
   path across it.
4. **The two are compared on your own board.** Running Accuracy keeps the fast curve beside the accurate
   one, so the error is measured on this design rather than promised in a document.

Accuracy is **never entered automatically and never left silently**: the button is the only way in.

## The Settings flyout, control by control {#settings}

The **cog** on the title bar holds the five advanced choices, and nothing else &mdash; the window is
deliberately clean by default. **Every one of them is stated on the report**, which is why they are
stored in the `.crail` rather than being per-session preferences: a number a report cannot read is a
basis nobody can check afterwards.

| | |
|---|---|
| **Reference extent** | What the reference conductor is taken to be. See below &mdash; **two of the three are optimistic**. |
| **Via plating** | The plated barrel thickness a via's current limit is computed from, in µm. **Empty is not a default:** railRF reads the thickness from the stackup's own via entry where one states it, takes this where it does not, and falls back to a drill-size table as a sanity band &mdash; and every flag says which of the three produced it. Clearing the field puts that behaviour back rather than leaving the last number standing. |
| **Via rise** | The temperature **rise**, in °C, a via's current limit is stated at. A budget, not a temperature: there is no thermal model in railRF and nothing else reads this number. 10 °C is the usual convention. |
| **Temperature** | The one temperature every resistance in the document is computed at. Copper is **+0.39 %/K**, so this is not a detail: the same trace at 85 °C is about 25 % worse than at 20 °C. It is on the status strip on every frame and on every report for that reason. |
| **Mesh cell** | How finely **Accuracy** meshes, as a cell size **in the board's own display unit**. **Empty is the ordinary case** &mdash; it reads *automatic*, and the extractor computes a cell size from the artwork itself. A number here overrides that; a unit typed explicitly (`50 µm`) is honoured whatever the board is set to. The fast model never reads it at all; it traces instead. |

### Reference extent

railRF solves the rail's copper **and its return**, so what the return is taken to be changes the
answer. The three choices are:

| | |
|---|---|
| **As imported** | The actual copper on that layer. Honest: a reference fragmented by anti-pads, a cut-out or a routing channel shows up as one, and the return resistance it reports is the one the board has. **This is the default and it is the one to report against.** |
| **Filled to outline (optimistic)** | That layer taken as solid within the board outline. It removes return constrictions the real board may have, so the drop it reports is **lower than the truth** by however much those constrictions cost. Useful for answering "how much of this is my plane?" &mdash; run both and read the difference. |
| **Infinite (an upper bound)** | The layer taken as unbounded at its own height. It removes the edge effects too. This is the only way to compare two different outlines on equal terms, which is what makes it worth having; as an absolute answer it is a bound, not a result. |

The status strip states the one in force on **every frame**, and so does every report and every export,
because an optimistic reading that is not labelled as one is the thing that turns a marginal board into
a passing report.

## Worked example, end to end {#example}

**Tools &rsaquo; Examples &rsaquo; Power Rail Integrity** ships a four-layer board with one 3.3 V rail on
it, set up for all four questions. Open it and follow along; its own README carries the full numbers.

1. Open `Sensor board.crail`. The board, the stackup and the part library are already resolved, and the
   window opens on Fast. Every part on it is an instance of a footprint cell with its reference
   designator on silkscreen, so the parts table and the board are reading the same thing.
2. **Run.** DC: **48.6 mV** at `U1` against a **52 mV** budget &mdash; met, and only just. The breakdown
   names it: **26.5 mm of 0.209 mm copper on the bottom layer is 65 mΩ**, and 46 % of the budget on its
   own, because the supply took the long way round the connector cut-out at the width a low-current net
   gets by default. The ferrite `FB1` is the third row at **20 mΩ and 7 mV**, ranked with the copper
   rather than reported beside it. Three via transitions, none over its limit. The second load, `U3`, is
   listed as *observed*: it states no current and draws none.
3. **Widen that run to 0.4 mm** in the layout editor and re-run. **40.6 mV.** The copper term halves and
   the protection FET becomes the thing worth arguing about &mdash; which is a part choice rather than a
   layout one.
4. **The sweep**, over 100 kHz to 100 MHz against a flat 55 mΩ: **passes by 0.8 dB at its worst, at
   100 MHz** &mdash; the top of the band, where every capacitor is its own mounting inductance and nothing
   else.
5. Two anti-resonances are named: *L(C9) against C(C7&ndash;C8)* at 4.14 MHz, and *L(C7&ndash;C8) against
   C(C1&ndash;C13)* at 10.80 MHz. **The converter's fifth harmonic at 11 MHz sits 1.8 % from the second
   of them** &mdash; the coincidence check earning its keep.
6. **Open the ranking.** `C10`, the bulk, is holding the low band up on its own: remove it and the rail
   fails by 17.8 dB. `C11`&ndash;`C13` are the same part number as `C1`&ndash;`C3` and worth about half
   as much, because their 0.9 mm fan-out carries **1201 pH** against the **555 pH** of a via in the land.
7. **Unmount `C11`.** Still passes, at 0.3 dB &mdash; one part and one placement saved on a board that
   does not exist yet. **Unmount all three and the rail fails by 0.7 dB**, which is the ranking's own
   limit said out loud: it re-solves once per part and answers *what does removing THIS one cost*, and
   three answers of 0.5 dB do not add up to a margin of 1.5 dB.

Steps 2 and 3 are the form-factor question. Step 7 is the cost question, and today it happens after
tooling, with a soldering iron.

## Running it headless {#headless}

Everything above runs with no window:

```
circuitrf rail board/Panel.crail
circuitrf rail board/Panel.crail --rail +1V8 --accurate -o report.pdf
circuitrf rail board/Panel.crail --load U1.VDD=120mA --target-drop 50mV --json
```

Omitting `--rail` runs every rail in the document, in dependency order. Every number comes out of the
same solve the **Run** button calls, and every pixel of an `.svg` or `.pdf` report out of the same
renderer the window draws with. See {{anchor: cli#rail|`rail` in the CLI chapter}}.

## What railRF will not do {#limits}

Stated plainly:

1. **It is not a full-wave solver and does not become one.** That step is very much heavier for an
   answer this question does not need. circuitRF's planar method-of-moments engine is a different tool
   for a different question &mdash; see {{anchor: mom-engine|the MoM engine}}.
2. **It is not a thermal tool.** The via current flag is a rule with a stated basis, and current density
   is a step towards one. Neither is a temperature. Everything is computed at the copper temperature you
   state, and copper is +0.39 %/K: at 85 &deg;C the same trace is about 25 % worse.
3. **It stops at the package.** The answer is the impedance at the **board-side pads**. On-die
   capacitance and package inductance sit between that and the transistor and typically dominate above a
   few hundred megahertz. If your silicon vendor supplies a package model you can cascade it; railRF
   will not invent one.
4. **It does not model a regulator's forward transfer.** A rail chain is solved in order and the
   coupling it carries is a DC one: an input-rail drop that changes a regulator's headroom. Ripple
   passing *through* a regulator needs its PSRR and its output impedance, which are frequently
   unpublished &mdash; so railRF does not carry it, and does not approximate it either.
5. **It does not do transient.** The output is Z(f) and a DC operating point. Turning Z(f) into a voltage
   waveform for a given current profile is a different feature and is deliberately not this one.
6. **It does not model a split plane as if it were solid**, and it does not silently bridge a split.
7. **It does not know your capacitors are derated** unless you give it a bias curve. An MLCC at bias can
   be a fraction of its marked value; without a curve railRF uses the marked one and says so.
8. **It does not route, place or optimise your board.** It measures.

## What is not wired up yet {#notyet}

Three things are representable in a `.crail` and do not yet reach a solve. They are here rather than
left to be discovered:

- **A port anchored by refdes does not resolve.** The placement table is read on import and is not
  joined into the extraction, so every port has to be anchored by **coordinate**, and every report row
  reads a point in DBU rather than `U1.VDD`. This is the same in the window and on the command line.
- **A rail chain is therefore unsolvable.** Two rails are linked only by a refdes that is a load on one
  and a source on the other, and that is the one anchor shape that cannot resolve. A chain that would
  form a cycle is still refused, correctly, before any pad is looked up.
- **A series part and a shunt part do not enter the DC solve.** A protection FET, a ferrite or a
  decoupling bank reaches the DC answer only as a source's own series R and L. Over frequency the parts
  are fully modelled from the rail's own part rows.

A fourth used to be here and no longer is: a `.crail`'s artwork, its stackup and its part library now
resolve when the **window** opens it, by the same walks `circuitrf rail` takes.
