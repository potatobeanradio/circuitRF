---
title: Layout versus schematic
slug: reference/lvs.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Layout versus schematic
lede: Does the artwork implement the drawing? One comparison of the copper you drew against the circuit you drew, naming every device, net and value the two disagree about.
keywords: LVS, layout versus schematic, netlist comparison, short, open, extraction, connectivity, waiver, device recognition, terminal map, mis-wiring, copper
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What LVS checks</a></li>
<li><a href="#unit">The unit of comparison, and what it reads</a></li>
<li><a href="#window">Running it from the window</a></li>
<li><a href="#headless">Running it from the command line</a></li>
<li><a href="#report">Reading a report</a></li>
<li><a href="#example">The worked example</a></li>
<li><a href="#waivers">Waivers</a></li>
<li><a href="#terminals">Terminal maps: which pad is which pin</a></li>
<li><a href="#recognize">Recognising devices in bare copper</a></li>
<li><a href="#limits">What LVS does not check</a></li>
</ol>
</nav>

## What LVS checks {#what}

A design that will be manufactured is drawn **twice** &mdash; once as a circuit and once as artwork
&mdash; and only one of the two is simulated. LVS is the check that the second drawing says the same
thing as the first.

It reads the copper on your layout, works out which pieces are electrically one net, works out which
pads belong to which placed device, and compares the netlist that falls out of that against the
netlist your schematic extracts to. Then it reports every place the two disagree.

<div class="callout note">
<span class="label">The four things it answers</span>
<ol>
<li><b>Is every device there, once?</b> A part in the schematic with no artwork, a part in the artwork
with no schematic, and a part of the wrong kind.</li>
<li><b>Is every net whole, and separate?</b> A schematic net that is two or more disconnected pieces of
copper is an <b>open</b>. Two schematic nets that are one piece of copper are a <b>short</b>, and the
report names the metal that joins them and where it is.</li>
<li><b>Is every terminal on the right net?</b> A pad landed on the neighbouring trace is not an open and
not a short &mdash; both nets still exist &mdash; and it is the fault hardest to see by eye.</li>
<li><b>Does every value agree?</b> The resistance, capacitance, inductance or length the artwork
resolves to, against the value the schematic asked for, each to a stated tolerance.</li>
</ol>
</div>

LVS never edits anything. It reads, it compares, and it reports.

## The unit of comparison, and what it reads {#unit}

**The unit is the cell.** A cell's primary layout view is compared against its primary schematic view.
That is the object a designer draws twice, so it is the object worth comparing; a test bench wraps a
cell in terminations that have no artwork at all, and comparing one of those would report the
terminations as missing parts on every run.

To do it, LVS resolves three things, all of which it finds for itself:

| | |
|---|---|
| **the layout** | the cell's own `.clay`, and every sub-cell it places, read down the hierarchy |
| **the technology** | the stackup that layout references &mdash; which conductor each drawing layer is, which vias join which conductors, and **which conductor is the ground reference** |
| **the schematic** | the cell's own `.csch`, read by the same extraction **Simulate** performs &mdash; but *not* the flattened, elaborated netlist. The hierarchy stays intact and the names stay yours; all LVS takes from elaboration is the resolved parameter **values** it compares |

The technology is not optional and is never guessed. Without it there is no way to know that a shape on
one layer and a shape on another are the same conductor, or that a barrel between them joins them.

<div class="callout note">
<span class="label">Ground can come from the stackup</span>
<p>A stackup entry may be flagged as the <b>ground reference</b> and draw no layer at all &mdash; a
die's backside metal is the usual case. Vias that terminate on it reach ground through metal that
exists in the process and nowhere in your artwork. LVS reads that connection, and <b>says so on every
run that relies on it</b>, naming the stackup entry and how many vias reached ground through it. It is
the one inference in the whole extraction that you cannot check by looking at your own screen.</p>
</div>

## Running it from the window {#window}

Open the cell's **layout** view, then **View &rsaquo; Panels &rsaquo; LVS**. The panel is not in either
shipped default layout, because it needs a cell with both views drawn and most documents being edited do
not have that yet.

The panel is the [DRC panel](layout-editor.html#drc)'s pattern, control for control:

| | |
|---|---|
| **Compare** | Runs the comparison. It never runs on its own and it never blocks editing. |
| **Markers** | Draws each finding's region over the artwork. A view preference: never saved, never on the undo stack. |
| **Layout &rarr; Schematic** | Selects the schematic component matching the artwork you have selected. |
| **Schematic &rarr; Layout** | The other direction. |

Under the buttons: the technology the comparison ran against, the reduction mode, both sides' device and
net counts, and the findings, grouped by severity. Clicking a finding zooms the layout to it. The text is
selectable; **Copy** (and a finding's right-click **Copy Report**) copies the same report
`circuitrf lvs -o` writes.

<div class="callout note">
<span class="label">A result describes what is on disk</span>
<p>The comparison reads the cell folder, which is what makes the panel and the command line give the
same answer. So editing the document after a run makes that run <b>stale</b>, and the panel says so.
The result is <i>kept</i> rather than discarded &mdash; unlike a DRC result &mdash; because it also
carries the correspondence, which is what cross-probing runs on and the thing you are most likely to
still want after nudging a pad. Cross-probing refuses while the result is stale rather than
highlighting against a correspondence that no longer describes the design.</p>
</div>

## Running it from the command line {#headless}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs &lt;path&gt; [--no-reduce] [--flat] [--testbench] [--recognize] [-o report.txt]</code></pre>

Same function, same arguments, same findings in the same order &mdash; so a design that passes on a
build machine passes when somebody opens it. The path says what to compare:

| Path | Compared |
|---|---|
| a **cell folder** | its primary schematic against its primary layout |
| a **workspace** | every cell holding both views; one holding a single view is reported and skipped |
| a **`.clay`** | its sibling schematic, in the cell folder that holds it |
| a **`.csch`** | its sibling layout, likewise |

A cell with only one of the two views is **not** an error. That is the ordinary state of a design being
drawn, and the run reports which view is missing and carries on.

### Options {#lvs-options}

| Option | Meaning |
|---|---|
| `--no-reduce` | Turn series/parallel reduction off and compare object for object. |
| `--flat` | Flatten the whole hierarchy instead of comparing sub-cells as cells. |
| `--flatten-cell <name>` | Flatten one named sub-cell and keep the rest hierarchical. Repeatable, matched on the cell folder's name, and every use is reported &mdash; a design that quietly flattens everything is a design whose hierarchy was never compared. A cell that is permanently like this says so itself, with `FlattenForLvs`. |
| `--testbench` | Compare a test bench as drawn, instead of the cell it instantiates. |
| `--recognize` | Also read devices out of bare copper &mdash; see [below](#recognize). Off by default. |
| `--set var=expr` | Set a global before the schematic elaborates, exactly as a run verb does. |
| `--severity warning\|error` | What makes the exit code non-zero. Default `error`. |
| `-o report.txt` | Write the report to a file. |

**`-o` is the only thing this verb ever writes**, and with no `-o` it writes nothing at all. It runs on
a read-only tree and on a workspace another process has open.

### Exit codes {#lvs-exit}

**0** when nothing at or above `--severity` was found, **1** otherwise, **130** on a cancellation. A run
that found warnings and no errors exits 0 and still reports every one of them &mdash; hiding them to
keep the exit code clean is what makes an exit code useless.

`--json` gives the whole result as one document. Every finding travels with a stable `lvs.` id and
typed arguments &mdash; the layer, the coordinate, the two values &mdash; so a script reads what it
needs without parsing the sentence back apart. The id is the contract; the sentence is not.

## Reading a report {#report}

A report opens with one line per cell and then the findings, grouped by severity. The header line is
worth reading before the findings are:

<pre><code class="cmd"><span class="output">Attenuator broken: 6 error(s), 0 warning(s)
  technology 'PCB 2-Layer FR-4 (0.8mm, 1oz) - LVS example', reduction on
  schematic 4 device(s) and 3 net(s); layout 4 device(s) and 5 net(s)</span></code></pre>

Both sides' counts are on the face of it, and so is the technology and the reduction mode, because two
people reading the same report have to be reading the same run.

Findings name **your own objects** &mdash; designators, net names, pin names &mdash; never internal
indices, and never a coordinate where a name exists. These are the ones you will meet most:

| | |
|---|---|
| `lvs.device.unmatched-schematic` | A part in the drawing that the artwork does not have |
| `lvs.device.unmatched-layout` | A part in the artwork that the drawing does not have |
| `lvs.device.type-mismatch` | Both sides have it, and they disagree about what it is |
| `lvs.device.turned` | A resistor, capacitor or inductor placed end for end: the circuit is the same, but its pin 1 is on the other land. A **warning**, one per part |
| `lvs.device.reversed` | A diode or other one-way two-pin part placed end for end, **naming both nets**. An **error**: the board would assemble it backwards |
| `lvs.net.open` | One schematic net is two or more separate pieces of copper, **with the pins on each** |
| `lvs.net.short` | Two or more schematic nets are one piece of copper, **with the metal that joins them and its coordinate** |
| `lvs.terminal.wrong-net` | A terminal is on one net in the drawing and reaches a different one in the artwork, **naming both** |
| `lvs.property.mismatch` | A value disagrees, **with both values and the tolerance that was applied** |
| `lvs.pin.no-copper` | A pad that lands on nothing at all |

**Parts placed end for end are listed together, with one button to fix them.** Two identical lands
look the same whichever way round a resistor or capacitor sits, so a board can easily have dozens of
them the wrong way round. The circuit is still right, but railRF, the placement table and
cross-probing all read those parts' pin 1 backwards. The LVS panel lists every one of them &mdash; the
full count, however many there are &mdash; with a checkbox each and a **Turn these N in the layout**
button that turns the checked ones in one step you can undo, then compares again. railRF offers the
same button for the same parts, and turning them in either window clears both. A reversed diode gets a
button of its own and is never turned with the others, because only you know whether the part or the
copper is the wrong half.

**A short is never reported without its path.** A short reported as a bare pair of net names is a
finding nobody can act on, so the sentence carries the neck or via that joins them, its width and where
it is. The same rule drives the rest: an open names the pins on each island, a property mismatch names
the tolerance it applied, so a wrong default shows up on the line it produced rather than being argued
about in the abstract.

### Reduction, and why findings un-reduce {#reduction}

By default LVS **reduces** before it compares: parallel `R`/`C`/`L` on a net pair, parallel
multi-terminal devices with every terminal common, and series `R`/`C`/`L` through a node of degree
exactly two that no port, label or `measure` line observes. A four-finger FET drawn as four devices and
schematised as one is the case this exists for.

Every reduction is reported, and **every finding un-reduces** &mdash; a collapsed device names all four
of the objects you drew, not the merged one the comparison worked on. `--no-reduce` gives the
object-for-object reading, and the mode is on the face of every report either way.

Distributed and behavioural elements are never reduced, and neither are series cascodes.

## The worked example {#example}

**Tools &rsaquo; Examples &rsaquo; Layout versus schematic** ships a 3 dB attenuator whose artwork
matches its drawing, **the same board with six deliberate faults in its artwork**, and an MMIC bias
tee. Open it and follow along.

Its own `README.md` is the fault list: each of the six, what was mutated, and what the report should
say about it. It also carries the Python that regenerates both boards, so you can write a seventh fault
and see what LVS makes of it.

<h3 id="lvs-example">Both boards, from the folder you copied the example into</h3>

The correct board first, so you know what a clean run looks like:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs Attenuator
<span class="output">Attenuator: matches
  technology 'PCB 2-Layer FR-4 (0.8mm, 1oz) - LVS example', reduction on
  schematic 4 device(s) and 3 net(s); layout 4 device(s) and 3 net(s)

1 cell(s) compared: 0 error(s), 0 warning(s), 3 note(s).</span></code></pre>

Now the same schematic, byte for byte, against artwork with six faults in it:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs "Attenuator broken"
<span class="output">Attenuator broken: 6 error(s), 0 warning(s)
  technology 'PCB 2-Layer FR-4 (0.8mm, 1oz) - LVS example', reduction on
  schematic 4 device(s) and 3 net(s); layout 4 device(s) and 5 net(s)
  error: The layout's 'R4' has no counterpart in the schematic (0 indistinguishable device(s) in the schematic against 1 in the layout).
      R4
  error: The schematic's 'C1' has no counterpart in the layout (1 indistinguishable device(s) in the schematic against 0 in the layout).
      C1
  error: Open: schematic net '0' should connect R1.2 and R3.2, but in the layout they are on 2 pieces of copper that do not touch: R3.2 (that copper also reaches R2.1, R2.2, R1.1); R1.2.
      0, R2.1, R2.2, R1.1, R3.2, R1.2
  error: Short: schematic nets '0' and 'IN' are joined by one piece of copper in the layout. It carries R3.2 (on '0') and R2.1, R1.1 (on 'IN'). They are joined through a 200.064 µm neck of Top Copper at (1500, 4675) µm.
      0, IN
  error: R3 R: schematic 294 Ω, layout 150 Ω, tolerance 1 %.
      R3, R3
  error: R2 terminal 2 ('2') is on 'OUT' in the schematic and reaches '0' in the layout.
      R2, R2

1 cell(s) compared: 6 error(s), 0 warning(s), 3 note(s).</span></code></pre>

Six faults, six findings, in one run. Read against the README's table they line up one for one: an extra
part, a missing part, an open, a short, a wrong value and a mis-wired terminal. The short carries the
0.2 mm spur that causes it and its coordinate, and which of each net's pins that copper reaches; the
open names the net's own pins on each piece of copper, then whatever else that piece reaches; the value
mismatch prints both values and the tolerance.

Note what the **fifth** one is. `R3` is the right kind of part, in the right place, with the right
designator, wired correctly &mdash; and it is the wrong resistor. No topology check of any kind will
find it, and neither will looking at the board.

Point the verb at the whole folder and it does every cell in it:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs .
<span class="output">Attenuator: matches
Attenuator bench: 'Attenuator bench' has no primary layout view, so there was nothing to compare it against.</span></code></pre>

`Attenuator bench` is the test bench that runs the correct cell &mdash; two terminations and an
S-parameter sweep. It has no artwork, so it is reported and skipped rather than failed.

### The die {#example-mmic}

`Bias tee` is an MMIC cell on the shipped GaAs process, and it shows two things a board cannot.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf lvs "Bias tee"
<span class="output">  note: Ground came from the stackup, not from the artwork: 'Backside Metal' is the ground reference and draws no layer, so 2 via(s) were read as reaching net 0. Nothing on your drawing shows this connection.</span></code></pre>

That is the undrawn ground reference, reported unconditionally.

The second thing is its **spiral inductor**, which is one continuous piece of metal from one terminal
to the other. A placed part whose own copper joins its terminals &mdash; a spiral, a microstrip line, a
bend, a tee &mdash; is read as a part and not as wire: its copper is left out of the connectivity, and
each terminal is read at its pin, on whatever copper is under that pin. Two lines placed end to end join
where their ends meet, and an open stub's far end is a net of its own, exactly as the schematic draws
it. So the cell compares clean.

## Waivers {#waivers}

A finding you have decided is not a fault can be **waived, with a reason**, from the panel. A waived
finding is still reported and merely not counted &mdash; a known exception you can still see is worth
more than a clean report you cannot trust.

**An LVS waiver names a relationship, not a place.** "`R7`'s pin 2 is deliberately not connected"
survives moving `R7` to the other side of the board, and stops being true the moment the *schematic*
changes. That is the opposite of a DRC waiver, which names a place and correctly stops applying when
the shape moves. So re-routing never silently un-waives anything here.

Waivers are saved with the layout. A waiver the last run matched nothing for is listed separately, under
**Waivers that no longer match any finding**, carrying the text of the finding at the time it was
waived, so you can recognise it and remove it.

**The command line honours waivers and never creates one.** Waiving is a deliberate, reasoned act with a
sentence attached, and it belongs beside the thing being waived.

## Terminal maps: which pad is which pin {#terminals}

Before anything can be compared, each cell has to say which of its **layout pins** is which of its
**schematic ports**. A cell states that in its `.ccell`, and where it does not, circuitRF derives it and
tells you which rule produced the answer:

| | |
|---|---|
| **declared** | The `.ccell` states the map. Nothing is guessed. |
| **imported** | The map came in with the part, from the source it was imported from. |
| **by name** | Every symbol pin is named and every one of those names is a layout pin name. Layout pins no symbol pin names &mdash; a mounting pad, a shield &mdash; stay out of the map. |
| **by order** | **Only** when no pin on either side is named at all and the counts match. It is a guess that is usually right, so it is reported at warning on every run that does it, including an otherwise clean one. |

**A partial name match never falls through to positional.** Three of five names matching is evidence the
author meant them to match and got two wrong; reading the whole thing by position would produce a
confident wrong answer over a visible clue. That case is reported, naming the pins on both sides that
did not match.

The map is validated by [`check`](cli.html#check) too, before any comparison, because it is cheap and
static and a design whose map is wrong is a design that would be compared against the wrong pads.

## Recognising devices in bare copper {#recognize}

circuitRF's layout is **instance-bearing**: a device on it is a placed object with a designator, so the
primary reading is correspondence rather than recognition. That is why `--recognize` is **off** by
default &mdash; turning it on for a design circuitRF authored would re-derive, from geometry and less
reliably, devices that are right there in the file.

It exists for artwork that carries no instances: a hand-drawn MMIC device, a GDSII import whose
hierarchy was flattened away, a Gerber board read back as polygons.

It is also off **per technology**. The rules are a `DeviceRules` block in the `.ctech`, stating the
layer expression a device's copper must satisfy and the formula that turns its measured geometry into a
value. A process that declares none recognises nothing, whatever the flag says, and that is not an
error. The deck is checked by `circuitrf check <tech.ctech>` before any run &mdash; an unreadable region,
an unknown kind, a formula naming something neither measured nor declared, a cyclic constant.

A run that recognised anything says so, naming the technology and the count, so a clean report is never
mistaken for the stronger claim. **Recognition answers *what does this copper look like*, never *is this
the device the process actually makes*.** Every candidate it rejected is reported with its reason, and a
recognised device carries no designator, so it can only ever be matched structurally.

## What LVS does not check {#limits}

Four things, stated plainly, because a check whose boundary is unstated is one people over-trust.

1. **Performance.** LVS compares topology and values. It says nothing about whether the circuit works,
   how much the artwork's parasitics cost it, or whether a trace is wide enough for its current. Those
   are questions for [simulation](simulations.html), the [EM engine](mom-engine.html) and
   [railRF](railrf.html), and none of them is answered by a netlist comparison.
2. **Manufacturability.** Widths, spacings, annular rings, minimum areas and the rest are
   [design rules](layout-editor.html#drc), and they are a different check with a different engine. A
   board can pass LVS and be unbuildable.
3. **Parasitics.** Copper between two pads is a net, not an inductor, and LVS never extracts one. A
   clean report is not a statement that the artwork behaves like the schematic &mdash; only that it is
   wired like it. Extracting what the copper really is, is the [EM engine](em-setup.html)'s job.
4. **Whether a declared device is really there.** A placement says "this is a 294 Ω resistor" and LVS
   believes it, checking its value against the schematic's. It does not verify from the geometry that
   the process would actually make that part &mdash; and with `--recognize` on, what it reads out of
   copper is still what the copper *looks* like, not what the fab will build. A device the process
   cannot manufacture is caught by design rules and by the process's own sign-off, not here.

One more, and it is a limit of the reading rather than a non-goal: **a line joins other copper only at
its ends.** A trace that overlaps a line part-way along without covering an end, or a stub that meets a
line with no tee, reads as not connected. The schematic cannot draw that junction either, so the
comparison reports it as an open. And a part is read this way only when it is placed as a part &mdash;
a spiral drawn as loose shapes, with no placement, is still copper, which is what
[device recognition](#recognize) is for.
