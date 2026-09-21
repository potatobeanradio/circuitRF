# railRF — Power Integrity on Real Board Shapes, from DC Up

**Status:** **BUILT** — rev 4, amended 2026-09-18 · **Date:** 2026-09-18 · **Phase:** P0…P3 shipped; see §6
**Implementation:** [`docs/sonnet-briefs/brief-railrf-0-overview.md`](../sonnet-briefs/brief-railrf-0-overview.md) — 17 briefs across P0…P3, all built 2026-09-18
**Reads with:** `docs/design/match.md` §9 (the UI this one is modelled on), `docs/design/data-display.md`
(the plot layer this reuses), `docs/design/mom-engine.md` (the full-wave solver this deliberately does
*not* use, and why), `docs/design/layout-view.md` and `src/Design/Layout/Interchange/` (the Gerber /
Excellon / board-netlist / `.kicad_pcb` readers this is built on), `docs/design/ui-architecture.md` (the
firewall this obeys), `docs/user/reference/derived-metrics.html` (the passive readouts that turn a
vendor part file into a PDN element), `docs/design/lvs.md` (the **second reader of this one's copper
extraction** — `CopperPieces`, `LayerRegions`, `PlacedPins` and `Regions.Walk` are promoted into a
shared `Layout/Extraction` namespace that both call, because a board whose connectivity railRF and LVS
disagree about is a bug neither of them reports).

**What changed in rev 4.** The third review round answered every question rev 3 left open but one, and
one of the answers arrived against §11's window rather than against a numbered question — and it is the
one that changes the shape of the document:

1. **A rail has a SET of sources and a SET of loads, and a board has more than one rail.** Review's
   boards run a primary cell into a converter or an LDO that makes a second voltage, so the single
   source and single load rev 3 assumed are both wrong. Both are now add/remove lists, each entry
   anchored to a **component pad** rather than to a coordinate, and Q-6's "one rail at a time" is
   re-closed rather than reversed: one rail is *analysed* at a time, but the document holds the rail
   set and solves them in dependency order, because a regulator is a load on its input rail and a
   source on its output rail (§2.2).
2. **Q-13 … Q-19 are closed and Q-18 alone is carried forward** (§8.4). Two of the answers change
   behaviour rather than filling a blank: the via current limit gets a stated basis whose hidden
   variable turns out to be the plating thickness (§4.2), and the A/B comparison turns out to be a
   decap-removal tool rather than a DC one, which moves it in the phasing (§6).
3. **The board view is the layout editor's own canvas, and the copy out of it reuses the exporter that
   already exists** (owner). Every pan and zoom gesture the layout editor has — Zoom to Fit, Zoom to
   Marquee, Ctrl +/−, arrow-key panning — and a clipboard copy in every format that carries railRF's
   own overlays, written by no new clipboard code. §11.6 and §11.7 are new and they are mostly a list
   of traps already paid for.

**What changed in rev 3**, and still stands:

1. **The copper is not the small term.** Rev 2's own table said the artwork contributes a few percent of
   a DC drop. On the real geometry — 0.15–0.5 mm traces, 0.5 oz inner copper, runs far longer than
   10 mm — a single supply trace is **165 mΩ**: half the protection FET, and more than everything else on
   the board put together. §2.8's table is corrected and its conclusion is reversed.
2. **The excitation set is known and it is low.** The things that ring a PDN on these boards are the
   on-board oscillators: a 32 kHz crystal, a converter at 400 kHz–7 MHz, an RF crystal at 13–50 MHz.
   Every one of those, and the decap self-resonances they land on, sits **an order of magnitude below
   the first cavity mode of a compact board**. The cavity model moves later in the phasing and the
   sub-100 MHz model moves ahead of it (§6).
3. **Two speeds, and the fast one is the default** (owner). A closed-form model over trace geometry runs
   in milliseconds, so the live interaction rev 2 dropped comes back — in that mode only, with the mesh
   solve behind an explicit **Accuracy** button (§2.9).

Also from rev 3: the board import lands in an ordinary circuitRF cell by default (§2.3), and §11 is a
real UI spec rather than a workflow list. **Q-1 … Q-19 are now all closed or decided except Q-18**, and
Q-20 … Q-22 replace them.

**The name is decided** — railRF, following the house pattern (`harmonicaRF`, `wBond`), and the only one
of the four candidates that covers a DC voltage drop and a PDN impedance equally well.

**This note is approved** (owner, 2026-09-18) and the brief series above is the implementation. **Two
amendments have been made since rev 4 went out for review, both on 2026-09-18 and both marked in place:**
Q-18's reference package is **deferred to manual testing** and no longer blocks P0 (§8.5), and §7's
importer gate is correspondingly **two-tier**. Nothing else in rev 4 has changed.

---

## 0. Background — the problem as the reviewer states it

*Paraphrased from the rev-1 and rev-2 review rounds.*

A supplier ships a reference design: schematic, BOM and layout data. From the ordering part numbers in
that BOM, the parasitics of each part can be obtained from the manufacturers' own parametric-model web
tools, and from the circuit a target impedance for the supply network follows — whether the supply is
external or an on-board converter.

The impedance a power delivery network presents depends on the shape of the supply net, the parasitics
of the parts sitting on it, the load current and the source impedance. **The target devices are battery
powered**, and a battery's ESR is not a constant: it runs from a few ohms to several hundred ohms across
the cell's life. Converter data is often unavailable — a supplier who fears being reverse-engineered
does not publish an output-impedance curve.

The supply topology is described by the reference design's schematic, and the position of each part is
visible on the silkscreen of the Gerber set. For the customer's own design the BOM arrives as a
spreadsheet or text file from the layout tool, and part placement arrives as a pick-and-place file from
the same tool.

**The question the tool has to answer: how do you keep the reference design's performance in a customer
form factor that is smaller and has fewer components?**

---

## 1. What railRF is, in one paragraph

railRF answers a question circuitRF cannot answer today and neither can a SPICE-class simulator: **on
this board, with these decoupling capacitors in these positions, what impedance does the load actually
see, where does the supply voltage go on the way there, and where does the copper stop helping?** It
takes the artwork you already have — Gerbers, or a board file — plus a stackup, a parts list, the load
currents and a target impedance, and returns the DC drop broken down by what caused it, and Z(f) at the
load with the target mask over it, the anti-resonances named, the plane's own resonances named, and a
ranked list of which capacitors are earning their place. It then does the same thing to a second board
and shows you the difference.

The gap it fills is specific. A SPICE simulator will model your capacitors with their parasitics
perfectly well and knows nothing about the copper they are mounted on. A full-wave EM tool will model
the copper and is far too slow to sweep a whole plane pair with forty capacitors on it. The useful
model sits between them, it is well established, and nothing on the desk of a working board designer
implements it.

---

# 2. How the tool works, from the outside

*This section is the proposal. Everything after it is implementation.*

## 2.1 The four questions

railRF is built around four questions a board designer asks, in this order:

**Q0 — Is this rail actually connected, and what does it cost to get there?** At DC: is the net one
region or three islands joined by a neck; how much of the supply voltage is lost between the cell and
the load; and which element on the path is responsible. This is the question a compact battery-powered
design asks first, because on a small board with thin inner-layer copper the answer is frequently *the
trace*.

**Q1 — Does this rail meet its target?** You have a target impedance — `Z_target = ΔV_ripple / ΔI_step`,
or a frequency-dependent mask if your silicon vendor gave you one. You want Z(f) at the load, from DC to
wherever your transient content dies out, with that mask drawn on it and the violations named — and with
the frequencies your own board actually generates marked on the same axis (§2.4).

**Q2 — Which capacitors are actually doing anything?** You have forty decaps because the reference
design had forty decaps. Some of them are in parallel with a lower-ESL part two millimetres away and
contribute nothing. Today the way to find out is to build the board and start removing parts. railRF
ranks every capacitor by how much the worst mask violation would grow if you deleted it.

**Q3 — Did my form factor break it?** A supplier gave you a reference design — schematic, BOM, Gerbers.
You had to re-shape it to fit an enclosure. Same parts, same net topology, different outline and
different placement. railRF runs both and shows you the delta, before the board exists.

## 2.2 What you provide

### The artwork — mandatory

Either a **board file** (`.kicad_pcb`) or a **Gerber set** with its Excellon drill file. circuitRF
already reads all of these; railRF adds no new artwork importer.

> **The difference between the two matters and it is the single biggest practical constraint on this
> tool.** A board file carries **nets, footprints, refdes and values**. A Gerber set carries **geometry
> only** — it is a photoplot. There is no net in a Gerber, no component, no value. From Gerbers alone,
> railRF can see that a pour exists and that a drill hole passes through it; it cannot know that the pour
> is `+1V8` rather than `+3V3`, or that the two pads at (43.2, 18.7) belong to a 100 nF 0402 rather than
> a 0 Ω link.
>
> So the Gerber path is **assisted, not automatic**, and §0 makes it the *primary* path rather than the
> fallback: you name the power and reference conductors, and you supply the three companion files below.
> The tool says plainly which path it is on and what it therefore does not know. It will not guess a net
> name and it will not guess a capacitor value.

### The three companion files

All three are ordinary exports of the layout and schematic tools, and all three are new readers (§5).
None of them is geometry: each attaches facts to objects the artwork readers already built, which is the
rule `BoardNetlistFile` states and these follow.

- **The board netlist (IPC-D-356).** circuitRF **already reads one** when it is present in a Gerber
  folder: a net name per pad, a plating flag per hole, and the component reference and pin where there is
  one — and it is the *absence* of a reference that identifies a hole as a via rather than a component
  hole. Where it is present, almost all the clicking in §2.3 disappears. **Review's answer to Q-13 is
  that it is in the output set**, identified by the example file supplied rather than by the standard's
  name — so the assisted Gerber path of §2.2 is the *fallback* for a package that lacks one rather than
  the normal case, and which netlist flavour that example turns out to be is Q-21.
- **The placement (pick-and-place) file.** A plain text table, one row per refdes: x, y, rotation, mirror
  and footprint name, with a small header carrying a format version and the units. **The coordinate
  origin is a choice made at export — symbol origin, body centre, or pin 1** — and the exporting tool does
  not always record which. Three quarters of a millimetre on an 0402 is the difference between landing on
  the part's own pad and landing on its neighbour's, so **an unstated origin is a refusal naming the flag
  that answers it**, exactly as an unstated Excellon coordinate format already is. **Review's answer to
  Q-14 is that railRF should ask which was chosen** rather than learn a house convention, so in the window
  it is a three-way choice on the import dialog with nothing pre-selected — there is no default, because
  a default here is the guess the refusal exists to prevent. Headless it stays a refusal naming the
  flag. The `mirror` column is
  what puts a part on the bottom side; a row that sets it and a footprint that has no bottom-side artwork
  is reported, not assumed.
- **The BOM.** A delimited table whose useful columns are the **internal part number**, the **part
  reference** (refdes), the value, the footprint and a free-text description. The description conventionally
  carries the dielectric class and the voltage rating (`MLCC 10n0 50V 0402 X7R ±10%`), which is exactly
  what derating needs (§9), so railRF **parses it and shows what it parsed** in the parts
  table for correction. It never silently acts on a guess about a free-text field.

> **Parts are identified by PART NUMBER, and that is what the model attaches to.** The BOM carries an
> internal ordering part number per part, and behind that number sits a list of approved manufacturers
> with their own item codes. So the file splits in two: a **part library** keyed by internal part number,
> holding the model, the voltage rating, the dielectric class and the footprint; and a **placement** table
> mapping each refdes to a part number and a coordinate. A model is entered once and used twenty-two
> times. The same rule holds at DC — one on-resistance for every instance of the same FET.

> **A part is MOUNTED or it is not, and unmounting one is not deleting it.** Depopulating a board is
> the commonest what-if here, and the way to ask it is a checkbox on the part's row or **Unmount** on
> the board's own context menu — never an edit to the artwork. An unmounted part leaves the frequency
> model exactly as a deleted row would, and **stays in the parts table**, greyed, with its part
> number, its position and its computed mounting loop intact: that surviving number is the whole
> difference, because it is what makes putting the part back give the answer it gave before. The
> `.clay` is never touched — railRF shows the board, and this says what is FITTED to the geometry
> rather than changing it. It is a different state from *unresolved*: unresolved is a data problem,
> unmounted is a design question, and the table says both. The capacitor ranking below is computed on
> the mounted set, because ranking an absent part reports what removing nothing would cost.

> **And Compare takes the previous run as a side.** The loop is *save the result, change one thing,
> re-run, compare*, which needs no second `.crail` at all — railRF keeps the last completed result for
> the selected rail, one deep, and **Pin this result** holds one across further runs so *"unmount,
> run, unmount another, run"* measures each cut against the fitted board rather than against the
> previous cut. The report names what differed in the INPUTS at the top — *"C10 unmounted"* — because
> otherwise the reader is left diffing two curves to work out what they did.

### The stackup — mandatory

Layer order, copper thicknesses (**0.5 oz inner copper is normal, not an edge case**), dielectric
thicknesses, ε<sub>r</sub> and tan δ per dielectric. This is circuitRF's existing technology model
(`.ctech`) — the same one the EM solver reads, edited in the same place. `.kicad_pcb` carries enough to
pre-fill it; a Gerber set does not, and the stackup is then the user's to state.

The two numbers that matter most and are most often wrong: **the power-to-reference dielectric thickness**
(it sets the plane capacitance linearly and the spreading inductance linearly) and **tan δ** (it sets how
sharp the cavity resonances are, which is the difference between a 6 dB bump and a 20 dB one).

### The rails and their references — mandatory

Which conductor is the power net and which is its reference return — **per rail, and a board has more
than one.**

**A railRF document holds the rail set.** Review's own topology is a primary cell feeding a converter or
an LDO that makes a second voltage, so a document that could describe one net could not describe one of
these boards. Each rail carries its own net, reference, sources, loads, targets and band, and **one rail
is analysed at a time** — which is what Q-6 closed and is still true. What is new is that the rails are
not independent: **a regulator is a load on its input rail and a source on its output rail**, the same
part in two rows, and railRF solves the rails in that dependency order so the regulator's input voltage
is the upstream answer rather than a nominal. A cycle in that order is a refusal naming the two rails.

**At DC that chain is the whole of the coupling, and v1 does not extend it over frequency.** Carrying
ripple from an input rail to an output one needs the regulator's PSRR and its output impedance — the
data §8.2 records as frequently impossible to obtain. So each rail's Z(f) is solved against its own
source model, and where that source is a regulator whose curves were not supplied, railRF says the
sub-megahertz answer is optimistic rather than producing a curve that looks complete. What the chain
does buy at DC is the answer that matters most on a battery design: whether the drop on the input rail
has taken the regulator below the input voltage it needs (Q-20).

**The reference plane is asked for, never inferred.** Review is unambiguous: on an RF product one layer is
assigned as the reference ground plane regardless of who supplied the board. So railRF proposes the layer
it thinks is the reference, says why, and **asks** — and where a layer is chosen, two further options are
offered explicitly because each changes the answer:

| Reference option | What it means | What it costs |
|---|---|---|
| **As imported** (default) | the actual copper on that layer | honest; a fragmented reference shows as one |
| **Filled to the board outline** | that layer taken as solid within the outline | removes return constrictions the real board may have — **optimistic**, and said so on every result |
| **Infinite plane** | the layer taken as unbounded at its own z | removes edge effects too; useful as an upper bound and for comparing two different outlines on equal terms |

The choice is stamped on every plot, every table and every export, because the second and third are
optimistic and a reader who does not know which was used cannot tell.

### The sources

**More than one per rail, and each one sits on a pad.** A rail can be fed by a cell through its
connector, by a regulator's output pin field, or by both on a design that runs from either — so the
sources are a **list with add and remove**, not a field. Each entry is anchored the way a load is
(below): **pick the refdes and the pin** that the placement file or the board netlist already named,
and railRF resolves that to the pads and to the mesh cells under them. A bare coordinate is accepted
where there is no placement file, and it is the fallback rather than the spelling — **a pad moves when the board is
re-laid out and a coordinate does not**, which is precisely what the A/B comparison of §2.5 would
otherwise get wrong on the one input it cannot check.

A battery is the default model: a series R-L whose R is swept across the cell's life (§8.2, Q-5), sitting
on the battery connector's own pad. A converter or an LDO is the same R-L unless you have its published
output-impedance curve, in which case it is a Touchstone file like any other part — and where you do not,
railRF says that the sub-MHz answer is optimistic near the loop crossover rather than pretending an R-L
is the whole story.

Two sources on one rail are two branches in the same mesh and nothing in the solve is special-cased for
them: **the geometry is what decides how they share**, which is the answer a hand calculation cannot give
and is a large part of why this is a board tool rather than a spreadsheet.

### The loads and their currents — mandatory for the DC answer

**A list with add and remove, and a load is a pad plus a current.** A load port is an IC's power/ground
pin field — a set of pads, not a point — and railRF ties them into one port, because that is what the die
sees. A regulator's input pin field is a load like any other, and it is the row that links two rails
(above). Loads are picked by refdes and pin exactly as sources are.

**The current is typed, per port, and a port with no stated current is not a load** (Q-16). Nothing in a
BOM or a placement file carries a current, so it is typed; and review's rule for the terminals that were
not given one is the useful half of the answer — **an unstated current means no load there, never a
defaulted one.** Such a port is still an *observation* port over frequency, where no current is needed;
it simply contributes nothing to the DC solve, and the DC report lists it as observed rather than
omitting it. Optionally a peak current per load, which is what the transient form of the target below
consumes.

**Amended 2026-09-21: the ADD gesture seeds a value, and the model is unchanged.** A source and a load
placed on a real board gave, correctly and uselessly, a rail with a source at no stated voltage and a
port drawing nothing — every drop zero by construction, with nothing to say the document was the
reason. So the window now hands a new load 1 mA and a new source the rail's own
nominal voltage. Nothing above stops being true: the absence is still representable, clearing the cell
still restores the observation port, and the numeric layer has learned no default. What keeps Q-16's
PURPOSE — which was never "no defaults" but "a number nobody typed must not read as one somebody did" —
is that the status strip counts the rows still holding what railRF put there. See
`src/Ui/RailRf/RailRfViewModel.Seeds.cs`.

### The parts

Each part on the rail is one of:

- **A row in the part library** — the common case, and the shape review already maintains: internal part
  number, manufacturer item, **C and self-resonant frequency**, from which `L = 1/((2πf₀)²C)` follows
  exactly. A 1 µF part resonating at 5.31 MHz is 898 pH; a 33 nF part at 39.1 MHz is 502 pH. railRF reads
  such a table directly and derives L rather than asking for it twice.
  **That table carries no ESR**, and ESR is what sets the depth of the minimum and the height of every
  anti-resonance peak. With C and f₀ alone, railRF can place a resonance but not size it, so it takes an
  ESR where one is given and **says which of the two it used** on the part row. **Q-15 closes on the
  fallback**: there is no per-part ESR figure to be had, so a dissipation-factor default per dielectric
  class *is* the basis rather than a stopgap, and every peak height computed from one is marked
  indicative wherever it appears — on the part row, on
  the plot, in the anti-resonance table and in the provenance of every export. The same per-class
  dissipation factor serves the board dielectric where the stackup does not state a tan δ of its own, and
  is flagged the same way. A part whose own Touchstone file was supplied is how a real ESR gets in, which
  is why that is the recommended path below and not merely an alternative one.
- **A Touchstone file** — the vendor's own measured `.sNp`, the manufacturers' web model tools' usual
  output. railRF reads its impedance through the shunt-through relation and uses it directly, so the ESR
  and the self-resonance are the part's real ones. Recommended, and already built: see
  [Derived Metrics](../user/reference/derived-metrics.html#fixture).
- **An R-L-C triple**, or a **SPICE/equivalent-circuit subcircuit** placed as an ordinary circuitRF
  subcircuit.

Plus, per part, the **mounting inductance**: the loop from the pad through its via to the plane pair and
back. railRF computes this from the actual via positions and the plane separation when it has the artwork,
which is the point — it is typically 0.3–1.5 nH, it dominates above roughly 50 MHz, and **it is the thing
your form factor change actually altered.** You can override it.

### The target

A flat `Z_target` in milliohms, or a piecewise mask (a table of frequency/limit points), or a transient
spec (`ΔI`, `ΔV`, rise time) from which railRF derives the flat target and the top of the band that
matters. Masks are per observation port. At DC the target is a **drop budget** in millivolts instead.

### The band, and the aggressors

Start, stop, points, log spaced and adaptively refined around resonances. **Plus the list of what your own
board generates** — review names the real set: a 32 kHz crystal, the converter's switching frequency
(400 kHz–7 MHz), an RF crystal at 13–50 MHz. Each entry is a name, a frequency and a harmonic count; they
are pre-filled from the BOM where a crystal or converter can be recognised, and they drive both the plot
markers and the coincidence check of §2.4. **A PDN peak matters if something on the board excites it**, and
this is the only input that knows whether it does.

## 2.3 What you do

railRF opens as a document with its own window, like the Match Designer and harmonicaRF. §11 is the
window; this is the path through it.

**1 — Load the board, and keep it.** The artwork imports, and **by default it lands in the open workspace
as an ordinary circuitRF cell with a layout view** — a checkbox on the import dialog, on by default. The
consequences are all the good ones: the artwork is saved with the design rather than re-imported every
session, it opens in the layout editor, DRC runs on it, `circuitrf render` draws it headlessly, revision
control keeps it, and railRF's own document holds a *reference* to that cell rather than a private copy of
the geometry. Unchecking it gives the old throwaway behaviour for a quick look. With no workspace open,
railRF offers to create one (`WorkspaceCreate`) rather than silently falling back to the throwaway path.

**2 — Identify the rails.** With a board file or a board netlist, pick the power net from a list.
Otherwise press the **crosshair button** beside the rail selector and then click the copper — the
click is *waiting to be made*, and while it is the pointer over the board is a crosshair, because a
click that makes a rail cannot be the same bare click that pans and marquees (owner, 2026-09-20).
railRF highlights everything galvanically connected to your pick — through
vias, across layers — so you immediately see whether the rail is one region or three islands joined by a
20 mil neck. *That alone has caught real problems.* Then confirm the reference layer, which railRF
proposes and never assumes (§2.2). **Repeat for each rail the board has**: where a regulator is
recognised from the BOM, railRF offers its output net as the next rail and pre-fills the row that makes
it a load on this one.

**3 — Confirm the parts.** A table: refdes, part number, value, model source, derated value, position,
computed mounting inductance. Anything railRF could not resolve is listed as unresolved rather than
defaulted.

**4 — Place the sources and the loads.** Both are lists with add and remove. An entry is a **refdes and
a pin** — the cell's connector, a regulator's output field, an IC's power pins — picked from what the
placement file or the netlist already named, or clicked on the artwork where there is neither. Type a DC
current per load; a load with no current is an observation port and nothing else (§2.2).

**5 — Set the target.** A drop budget for DC, a number or a mask for Z(f).

**6 — Work.** In **Fast** mode — the default — the result follows the edit: change a value, swap a part
number, delete a part, change the source ESR or the load current, and the numbers move as you type. When
the design is settled, press **Accuracy** for the mesh solve (§2.9).

## 2.4 What you get

### At DC

**The drop map.** The artwork itself, coloured by node voltage on a cold-to-hot scale, from the source to
the load. This is the headline of the DC mode and it is a picture, not a table: where the colour changes
fastest is where the drop is, and a designer reads that in a second. A cursor reads out the absolute
voltage anywhere on the net.

**The ranked breakdown.** One row per element on the path — the source, each series part, each trace
section, each via group — with its resistance, its current, its drop, and its share of the total. Ranked.
*"This 42 mm run of 0.3 mm inner-layer copper is 38 % of your drop"* is a finding; *"the drop is 180 mV"*
is not.

**The via check.** A via has a current limit before it is at risk, set by I²R heating in the barrel, and a
layer transition carrying real current through too few vias is a defect that no schematic shows. railRF
knows the current in **each** via, not the average — vias in parallel do not share equally, and the one
nearest the load routinely carries several times its share — so it flags a transition whose worst via
exceeds the limit, and names the count that would clear it. **The limit's basis is now stated** (§4.2,
Q-17): a temperature-rise budget over the barrel's own conducting annulus, with a per-drill-size table as
the default the setting opens on — and with the **plating thickness shown**, because that is the term the
table hides and it is worth a factor of two. **Current density as a field, and the hot-spot question
behind it, is explicitly a step 2** — review asked for it and agreed it comes after the flag.

**Everything at nominal temperature.** Review is running this as a room-temperature selection tool; the
design is measured over temperature in the lab regardless. So railRF computes at 20 °C, says so on the
report, and does not pretend to be a thermal tool. (Copper is +0.39 %/K: at 85 °C the same trace is ~25 %
worse, which is worth one line on the report and nothing more.)

### Over frequency

**The impedance plot.** |Z(f)| at each observation port, log-log, with the target mask drawn as a shaded
ceiling and violations marked with their frequency and margin in dB. **The declared aggressors are drawn
on the same axis** — a vertical marker per fundamental, lighter ones for the harmonics — because a 9 dB
peak nothing excites is not a problem and a 3 dB peak sitting on the converter's fifth harmonic is.

**The coincidence check.** A short list: every anti-resonance within a stated fraction of an aggressor
line, worst first. This is the sentence the tool exists to produce — *your bulk-to-ceramic anti-resonance
at 7.1 MHz is the converter's own fundamental at its top setting.*

**The anti-resonance table.** Frequency, peak |Z|, margin against the mask, and the two contributors,
named: *"L(mount, C3–C9 bank) against C(bulk bank)"*. That label is the actionable output; the peak on its
own is not.

**The capacitor ranking.** One row per part: how much the worst violation grows if this part is removed, in
dB. Parts with a zero in that column are candidates for deletion. The ranking is computed by actually
removing each part and re-solving, not by a sensitivity approximation — the solve is cheap and the
approximation is not trustworthy near an anti-resonance.

**The plane resonances and the impedance map** (from P2b). The cavity modes of your actual shape with a
field map each, and |Z| across the whole plane at a chosen frequency. A mode whose maximum sits on the load
pin field is a problem; the same mode with its maximum in a corner is not, and only the map distinguishes
them.

**Everything exports.** Z(f) as Touchstone or `.npy`, the tables as CSV, the maps as vector graphics
through the same renderers the window draws with. The impedance curves land in an ordinary circuitRF Data
Display, so they overlay anything else — including a measurement. **Every export carries which model
produced it** (§2.9) and which reference option was used (§2.2). The board panel also **copies to the
clipboard** — PDF, SVG and a bitmap together, and an enhanced metafile on Windows — with whichever map
overlay is showing inside the picture rather than beside it (§11.7).

## 2.5 The A/B comparison

This is Q3 and it is the workflow that motivated the whole tool.

**What review uses it for, which rev 3 guessed wrong** (Q-19). The comparison weighs what the *layout*
did to the PDN and, through that, **which parts could come off the board** — it is not a load-current
study. The DC drop analysis is a layout-review step of its own, done once the topology is settled, and
review keeps the two apart. So the impedance half of this section is the point of it, the DC half is
supporting evidence, and **§6 phases A/B with P2a rather than shipping a DC-only version early** as rev 3
proposed.

Open the reference design and the target design side by side. railRF matches the two by **net name** where
it can and by **refdes** for the parts, falling back to **part number** for the models. Where it cannot
match something, it says so and asks — it does not pair things by proximity or by guessing. **Sources and
loads pair by refdes and pin** for the same reason §2.2 anchors them there: pairing by coordinate is
exactly what a re-layout breaks, and a comparison whose ports moved is a comparison of nothing.

Then:

- **Both impedance curves on one plot**, with the target mask and the aggressor lines. The reference
  passes; does yours?
- **A delta trace** — Δ|Z| in dB versus frequency — with the frequencies where it moved most called out.
- **A per-part comparison table**: the same capacitor's mounting inductance on both boards. A part that was
  0.4 nH on the reference and is 1.1 nH on yours because its return via moved 4 mm is a finding you can act
  on in an afternoon.
- **Both removal rankings**, which is the output Q-19's answer puts first: a part that earns its place on
  the reference and earns nothing on yours has been shadowed by the re-layout, and a part that earns
  nothing on both can come off both boards.
- **Both DC breakdowns**, element by element — supporting evidence rather than the headline, though on a
  compact redesign it is often where the first surprise is: the same schematic, 40 mm of extra 0.3 mm
  trace, and 60 mV that were not in the budget.
- **Both mode lists**, where P2b has run.

The output is a short report, not just a plot: *these three parts got worse mounting, this trace section
costs 61 mV more than the reference's, and the net effect is a 6 dB mask violation at 7.1 MHz — on the
converter's fundamental — that the reference did not have.*

## 2.6 Worked example, end to end

> A supplier's reference design for a 1.8 V rail on a battery-powered instrument: 90 × 70 mm, four layers,
> reference ground on L2, 18 decaps, a 470 µF bulk, a 350 mΩ protection FET and a ferrite in series from
> the cell, 120 mA load, drop budget 80 mV, target 50 mΩ to 100 MHz. You have re-laid it as 60 × 50 mm to
> fit an enclosure: same schematic, same BOM, less room.
>
> 1. Load the reference Gerber set with its drill, netlist, placement and BOM. Import into the workspace as
>    a cell (the default). Pick net `+1V8` and confirm L2 as the reference. Add one source — the cell at the
>    battery connector's pad — and two loads: 120 mA at `U1.VDD`, 15 mA at `U3.VCC`.
>    **DC: 60 mV at U1, 48 mV at U3, budget met.** The breakdown on the worst path is FET 42 mV, ferrite
>    6 mV, copper 11 mV, vias 1 mV — the copper is 90 mΩ of it, 25 mm of 0.5 mm outer and 20 mm of 0.3 mm
>    inner.
> 2. Press **Accuracy**, then run the sweep. **Passes, worst margin 4.2 dB at 6.8 MHz.**
> 3. Load yours, same picks. **DC: 91 mV — over budget.** The breakdown names it: 70 mm of 0.2 mm copper
>    on L3 is 347 mΩ and **42 mV on its own**, because the compact layout took the supply the long way
>    round the connector cut-out at the width the tool's own default gives a low-current net.
> 4. Widen that run to 0.4 mm in the layout tool, re-import, re-run: **DC 70 mV, budget met.** The FET is
>    now 60 % of what is left, which is a part choice rather than a layout one.
> 5. The sweep: **fails, +5.4 dB over at 7.1 MHz**, and the coincidence check puts that exactly on the
>    converter's fundamental. The anti-resonance table names it: *L(mount, C7–C11) against C(bulk)*. The
>    three parts nearest the load on the reference now sit 11 mm away with 1.3 nH of mounting inductance
>    instead of 0.45 nH.
> 6. Move three parts, re-import, re-run. **Passes, 1.9 dB margin.**
> 7. Open the capacitor ranking on the passing design. Five parts show 0.0 dB — shadowed by lower-inductance
>    neighbours. Delete them: **still passes at 1.8 dB.** Five parts and five placements saved, on a board
>    that does not exist yet.

Steps 1–6 are the form-factor question. Step 7 is the cost question, and today it happens after tooling,
with a soldering iron.

## 2.7 What railRF will not do

Stated plainly:

- **It is not a full-wave solver and does not become one.** Review's own words for why: that step is very
  much heavier in simulation terms, for an answer this question does not need.
- **It is not a thermal tool.** The via current flag is a rule with a stated basis, and current density is a
  step 2. Neither is a temperature.
- **It stops at the package.** The answer is the impedance at the board-side pads. On-die capacitance and
  package inductance sit between that and the transistor and typically dominate above a few hundred
  megahertz. If your silicon vendor supplies a package model you can cascade it; railRF will not invent one.
- **It does not model a regulator's forward transfer.** A rail chain is solved in order and the coupling it
  carries is a DC one: an input-rail drop that changes a regulator's headroom (§2.2). Ripple passing
  *through* a regulator needs its PSRR and its output impedance — the data §8.2 records as frequently
  unavailable — so railRF does not carry it, and does not approximate it either.
- **It does not do transient.** The output is Z(f) and a DC operating point. Converting Z(f) into a voltage
  waveform for a given current profile is a defensible v2 feature and is deliberately not v1.
- **It does not model a split plane as if it were solid**, and it does not silently bridge a split.
- **It does not know your capacitors are derated** unless you give it a bias curve — see §2.2 and §9.
- **It does not route, place or optimise your board.** It measures.

## 2.8 DC and the low-frequency band

The mesh of §4.1 carries a series `R + jωL` per cell and a shunt `G + jωC`. Set ω = 0 and the inductance
and the shunt branch both vanish, and what is left is a purely resistive mesh: real, symmetric,
positive-definite and fast. **DC is not a mode bolted on — it is the first point of the sweep.** One
extractor, one mesh, one solver, which is also what stops a DC answer and an AC answer drifting apart.

**Is there a path at all, and through what?** Click any copper and everything galvanically joined to it —
across layers, through vias — highlights. On imported artwork **the copper stops at every pad**, so the
board is not electrically continuous until the user has said what bridges each gap; a capacitor bridges
nothing at DC, which is correct and occasionally surprising.

**What is the drop, and where does it come from?** Here is the correction that rev 3 exists for. Rev 2
tabulated the copper as a few milliohms and concluded it was the small term. On the geometry these designs
actually use it is nothing of the kind:

| Element on the path | Typical DC contribution |
|---|---|
| Battery ESR, aged | ohms to hundreds of ohms |
| FET on-resistance | ~350 mΩ |
| **50 mm of 0.3 mm inner-layer trace, 0.5 oz** | **~165 mΩ** |
| 30 mm of 0.5 mm outer-layer trace, 1 oz | ~29 mΩ |
| Ferrite bead DCR | ~50 mΩ |
| 10 mm of 1 mm-wide 1 oz trace | ~5 mΩ |
| One 0.3 mm plated via, 1.6 mm board | ~1.2 mΩ |

The arithmetic is only the sheet resistance of copper — **0.49 mΩ/square at 1 oz, 0.99 mΩ/square at
0.5 oz** — multiplied by the number of squares, and the number of squares is what changed. Review's real
geometry is 0.15 mm minimum trace width for non-supply nets, 0.2–0.3 mm for a low-current supply and
0.4–0.5 mm for a heavier one, on runs far longer than 10 mm, and inner layers are 0.5 oz — used anyway
because they are shielded, with the outer ground fields acting as a can for them. A 50 mm run of 0.3 mm
0.5 oz copper is 167 squares.

**So the copper is not a rounding error on these boards; it is second only to the aged battery and it is
comparable with the series semiconductor.** Two things follow. The ranked breakdown of §2.4 is the right
output and a single total is not — but the copper row is now near the top of it rather than at the bottom.
And the DC mode is not a bonus feature: on a compact battery design it is the mode most likely to find the
problem.

**Above DC and below a few megahertz** the same mesh runs with the inductance in place and the shunt branch
still negligible — a distributed R-L network with the lumped parts hung on it. Copper stays at its DC
resistance until its thickness approaches two skin depths: **57 MHz for 0.5 oz, 14 MHz for 1 oz, 3.6 MHz
for 2 oz** — so on the thin inner copper these boards use, one R matrix serves an unusually wide band.

The inductance cannot be dropped from that band. Using §4.1's loop resistance (`R = 2·R_s`, both planes),
`ωL = R` at roughly **1.2 MHz** for a tight four-layer plane pair (`L_sq = μ₀h`, h = 100 µm) and roughly
**83 kHz** for a two-layer board on 1.5 mm FR-4 — and |Z| is already 10 % high at 46 % of those, so about
570 kHz and 38 kHz. *(Rev 2 quoted half of each of these; it had used one plane's sheet resistance rather
than the loop's.)* A resistance-only mesh is therefore honest only to a few tens of kilohertz on a
two-layer board.

## 2.9 Fast and Accurate — the two-speed model

**Owner, rev 3: the default must be fast enough for live interaction, with the full calculation behind an
explicit button.** That is a better design than rev 2's re-run-when-you-are-ready, and it reinstates the
live result rev 2 dropped — in one mode, knowingly.

**Fast — the default.** The copper is reduced to a **graph** rather than a mesh: each trace section between
junctions becomes one resistance `R = ρ·L/(W·T)` (and, above DC, one loop inductance), each via becomes its
barrel resistance, each pad a node, each part its library model. Only copper that is **not** trace-shaped —
a pour, a plane, the fan-out under a BGA — is meshed, and coarsely. The result is a netlist of a few
hundred elements that solves in single-digit milliseconds, so it re-solves on every keystroke.

**Accurate.** The full mesh of §4.1 at the meshing density §4.1 requires, the cavity model, the modes and
the maps. On a button. Never entered automatically, and never left silently.

Four rules make the two speeds safe to have:

1. **Every result says which model produced it** — on the plot, on each table, in the status strip, and in
   the provenance of every export. A pass in Fast mode is reported as *a fast-model pass*, never as a pass.
2. **The classification is visible and correctable.** railRF draws which copper it treated as a trace and
   which it meshed, and a region can be forced either way. A silent misclassification is the one failure
   mode of this design: the fast model is good where the current fills the conductor's width and wrong
   where it spreads, so a wide supply polygon mistaken for a trace is optimistic and invisible. Drawing it
   is what makes it neither.
3. **Fast is refused where it cannot be honest.** Above the frequency where the shunt branch matters — the
   cavity band — Fast does not offer a number; the button says so.
4. **The two are compared on the user's own board, once.** Running Accuracy keeps the fast curve on the
   plot beside the accurate one, so the error is measured on this design rather than promised in a
   document. §7 gates it: on a trace-dominated DC path the two must agree to 5 %.

---

# 3. Why this is not the existing EM solver

circuitRF has a planar method-of-moments engine with layered Green's functions, and the instinct is to
point it at the plane pair. **That is the wrong tool and it is worth being explicit about why**, since this
decision shapes the whole implementation.

A MoM solve builds a dense N × N system over current basis functions on the metal. Our engine refuses above
a few thousand unknowns by design and costs tens of seconds per frequency point at that size. A 90 × 70 mm
plane pair meshed finely enough to resolve its own modes is tens of thousands of cells, and a PDN sweep
wants several hundred frequency points. That is not a tuning problem; it is three orders of magnitude.

The parallel-plate model is cheap for a structural reason: between two closely-spaced planes (`h ≪ λ`), the
field is essentially TM<sub>z</sub> with no z variation, and the problem collapses to a two-dimensional one.
It is a **sparse** system whose size is the cell count, solved by exactly the sparse complex LU circuitRF
already carries for MNA. That is the difference between a minute and a day, and nothing is given up that a
PDN analysis needed.

**So the deliverable of the extraction step is a NETLIST, not an `EmProblem`.** That distinction is the
architectural heart of this proposal and §5 turns on it. It is also what makes §2.9's fast model a *second
extractor into the same currency* rather than a second simulator.

---

# 4. The physical model

## 4.1 The plane pair as a mesh of unit cells

Divide the overlap region of the two conductors into cells of side Δ. Each cell contributes:

```
Shunt capacitance to the reference plane   C = ε₀·εᵣ·Δ² / h
Dielectric loss                            G = ω·C·tan δ
Series inductance along each cell edge     L = µ₀·h                    (square cells)
Series resistance along each cell edge     R = 2·Rs                    (both planes)
    with the skin-effect sheet resistance  Rs = √(π·f·µ / σ)   →   ρ/T below two skin depths
```

`h` is the dielectric separation, `σ` the copper conductivity. The factor of two on R is the two planes in
series in the loop. For non-square cells L and R scale by the aspect ratio (along/across). This is the
standard plane-pair unit-cell model and its accuracy against measurement is well documented in the SI
literature.

**Cells are square-ish and sized by the shortest wavelength in the dielectric**, Δ ≤ λ<sub>min</sub>/20. On
FR-4 (ε<sub>r</sub> ≈ 4.3) that is 7.2 mm at 1 GHz and 1.4 mm at 5 GHz. A 90 × 70 mm board at 1.4 mm is
about 3,200 cells — trivially sparse. **Below the cavity band the mesh may be far coarser**, which is what
makes the low-frequency phases cheap, and coarser still in Fast mode where it is used at all.

The mesh follows the copper. A cell is present where **both** conductors have copper; a cutout, an antipad
field, a split or a board edge simply removes cells. That is what makes arbitrary shapes work, and it is why
*"the actual shapes used"* is not a stretch goal here — it falls out of the meshing.

## 4.2 Vias

Via count and via span are a *reading* of the artwork rather than an approximation of it. The drill data
gives every hole; the stackup's via entries give the layers each span joins and carry a plated-wall
thickness, which is what sets barrel resistance; and circuitRF's connectivity walk bridges layers **through
via geometry** rather than by assuming the metal above and below overlaps — which is what makes an offset
staircase of metal connect correctly. Parallel vias fall out as parallel resistances at DC and parallel
partial inductances above it, with no special case anywhere. A 0.3 mm plated via through a 1.6 mm board is
about **1.2 mΩ**: twenty in parallel is negligible, two is not. Shared return vias are handled naturally —
two parts sharing one return via are coupled through it because the mesh has a single node there, not two.

**Review answers the span question: one through-drill file.** So v1 assumes through vias, reads a span
declaration where one exists, and **reports** rather than assumes when it meets blind or buried spans it
cannot resolve. The one honest limit from artwork alone — a via and a plated component hole are
indistinguishable — is settled by the board netlist, whose *absence* of a component reference is what marks
a hole as a via.

**The current limit.** Each via's I²R heating sets a current at which it is at risk, and review's answer
to Q-17 is a **table by drill size on a 10 °C rise**:

| Drill | Current at roughly 10 °C rise |
|---|---|
| 0.2–0.3 mm, a signal via | 0.3–0.5 A |
| 0.4–0.5 mm | 0.7–1.0 A |
| 0.6–0.8 mm | 1.0–1.5 A |
| 1.0–1.2 mm | 1.5–2.5 A |

with a 0.3 mm drill **at 20 µm of plating** quoted separately at 0.8–1.0 A.

**That separate figure is the most useful part of the answer, because it disagrees with the table's own
0.3 mm row by about a factor of two** — and the one term that differs between them is the **plating
thickness**, which the table does not state and which is the only thing setting the barrel's conducting
cross-section. A 0.3 mm hole plated to 20 µm has roughly twice the copper annulus of the same hole plated
to 10 µm, and roughly twice the current. A table indexed on drill size alone cannot express that.

So **railRF does not ship the table as the rule.** It computes the limit from the barrel's own annulus,
its span and a rise budget that is a setting, and it uses the table as the value that setting opens on and
as a sanity band drawn beside each flagged transition. The plating thickness comes from the stackup's via
entry where one states it, and is otherwise **typed and shown on the report**, never defaulted silently,
because it is worth that factor of two (Q-22). The figures above are general engineering guidance rather
than a standard held internally, and **each flag says which basis produced it** — computed-from-geometry
or table — for the same reason every other number in this document carries its provenance.

railRF flags a layer transition whose **worst** via exceeds the limit — worst, not average, because the
mesh knows the actual split and the nearest via of a group routinely carries several times its share.

## 4.3 What attaches to the mesh

- **Each capacitor** at the cell under its pads: its own model (library row, Touchstone, R-L-C or
  subcircuit) in series with its **mounting inductance**, connecting the power node to the reference node.
- **The mounting loop** from the actual via geometry: the partial self-inductance of the power via and the
  return via, minus twice their partial mutual inductance, plus the pad-to-via trace. The dominant term is
  the via pair's separation and the plane separation `h` — precisely the quantity that changes when a part
  moves.
- **Each source** at its own pad's cells: a series R-L to the reference, R swept over battery life, or a
  supplied output-impedance curve. More than one source on a rail is more than one such branch and
  nothing else — no special case anywhere, and the mesh is what decides how they share (§2.2).
- **Series parts on the path** — the protection FET, the ferrite — as their library models. At DC these are
  the largest terms after the source, so they are elements, never annotations.
- **Each load port** across the power and reference nodes of its own pin-field cells, tied together, with
  its DC current as a source for the operating point. A port with no stated current contributes an
  observation port and no current (§2.2).
- **A regulator, twice** — a load branch on its input rail and a source branch on its output rail, in two
  solves run in dependency order. It is never two branches in one mesh, which is the modelling mistake
  this arrangement exists to make impossible (§9).

## 4.4 The solve

Assemble one sparse complex MNA system per frequency and solve for the port impedances via CSparse's LU —
the same numerical layer every other circuitRF analysis uses. At ω = 0 the system is real, symmetric and
positive-definite and the same code path solves it far faster. The result is a `DataSet` carrying a Z cube
over `[freq, port, port]`, the DC node voltages and branch currents, and — when asked — the full node
voltage field for the maps.

**Adaptive frequency sampling** is not optional once the cavity band is in scope: plane resonances are
narrow and a log grid steps straight over one. The existing adaptive sweep from the EM engine is the
mechanism.

## 4.5 Modes

The cavity modes come out of the same discretisation as a generalised eigenproblem on the loss-free system,
so the mode list and the sweep cannot disagree about the structure. For a rectangle they reduce to the
textbook `f_mn = (c / 2√εᵣ)·√((m/a)² + (n/b)²)`, which is the acceptance check.

**Where those modes land matters for the phasing.** A 60 × 50 mm board on FR-4 has its first mode at about
**1.2 GHz**. The excitation set of §2.2 tops out at an RF crystal fundamental of 50 MHz. The cavity is real
and worth modelling eventually; on these boards it is an order of magnitude above anything the board itself
drives, which is why §6 puts it last.

## 4.6 The fast model's arithmetic

Nothing new, and deliberately so: `R = ρ·L/(W·T)` per trace section, summed along a path exactly as §7's
oracle does; a barrel resistance per via; the same library models for the parts; the same coarse mesh for
pours. It produces the same kind of netlist as §4.1, so the solver, the result model, the tables, the plots
and the exports are identical and only the extractor differs. **That is the reason the fast path is safe to
have at all** — it is not a second simulator, it is a second reading of the geometry.

---

# 5. Architecture

Where each piece lives, and why. The rule is `docs/design/ui-architecture.md`: nothing below the UI may
reference a UI framework.

| Piece | Home | Why |
|---|---|---|
| Board/Gerber/drill/board-netlist import, stackup | `src/Design` (existing) | Already there. railRF adds no artwork importer. |
| Placement (pick-and-place) and BOM readers | `src/Design/Layout/Interchange/` | New, and they belong beside `BoardNetlistFile` for the same reason it is there: they attach facts to objects the artwork readers built, and create no geometry. |
| The part library | `src/Design` | A document, keyed by internal part number. Read headlessly, so `rail` works in CI. |
| Plane and trace extraction → netlist (both speeds) | `src/Design/Layout/Pdn/` | A document-to-model transform, like the EM extractors beside it. |
| Mode eigensolve, field maps | `src/Engine/Pdn/` | Numerics. No domain types. |
| Target masks, ranking, aggressor coincidence, A/B diff | `src/Engine/Pdn/` | All arithmetic over a `DataSet`. |
| The solve | `src/Engine` (existing MNA) | Nothing new. |
| Drawing the artwork and the colour maps | `src/Render` (existing) | Below the firewall since 2026-09-07, so the window and the headless report draw with the same code. The maps are an overlay on `LayoutRenderer`, not a second renderer. |
| The board view, its navigation, its overlays | `src/Ui/Controls/LayoutCanvas` (existing) + `src/Ui/RailRf/` | railRF adds an `ILayoutCanvasOverlay`, **not a canvas** — the seam wBond already draws wires through. Pan, zoom, Zoom to Fit, the magnifier, arrow-key pan, rulers and hit-testing are the canvas's own (§11.6). |
| Copy to clipboard | `src/Ui/RailRf/` calling `PlotExporter.SetClipboardDataAsync` | **No clipboard code is written.** The page framing, the writers, the Windows metafile path and the font repair all exist (§11.7). |
| The window | `src/Ui/RailRf/` | The only piece that draws chrome. |
| `rail` CLI verb | `src/Cli` | Headless, per `docs/design/cli.md`. |

**The extraction produces an elaborated netlist and that is the whole trick.** It means the sweep, the
parametric machinery, the measurement engine, the `DataSet`/`DataCube` result model, the Data Display and
every export path are reused rather than rebuilt — and it means a PDN model can be *inspected* as a netlist,
which is the difference between a tool you trust and a black box.

Consequences worth stating: railRF runs headless, so a board can be gated in CI; the same `LayoutRenderer`
draws the drop map in the window and in a report `circuitrf render` writes with no display; and because the
result is a `DataSet`, a PDN curve overlays a measurement in an ordinary Data Display with no special
support.

---

# 6. Phasing

Five phases, each shippable and independently useful. **The order changed in rev 3**, for the two reasons
§2.8 and §4.5 give: the copper's DC resistance is a first-order term on these boards, and the cavity sits an
order of magnitude above anything they excite.

**P0 — DC.** The graph extractor and the resistive mesh, the galvanic-island pick, the drop map, the ranked
breakdown, the via current flag, Fast mode and its live edit loop. No frequency sweep, no eigensolve. It
builds the import front end, the three companion readers, the part library, the placement and the
extractors — most of everything later — and it is the only phase with a closed-form external oracle (§7).
**The rail set, the source and load lists and the rail chain are all in P0**, because on a resistive mesh
several sources and several loads are only more branches, and because the chain's whole payoff is a DC
one: whether the input rail's drop has taken a regulator below the input voltage it needs.

**P1 — Lumped PDN.** The part library's C/f₀ rows and Touchstone models, derating, the source model and its
life sweep, the target mask, the aggressor lines and the coincidence check, Z(f) at one port, the
anti-resonance table and the removal ranking. **Artwork optional**: mounting inductances may be typed. This
is where the tool starts answering Q1 and Q2, and on these boards it covers the whole excitation set.

**P2a — The distributed low band.** The mesh with R and L, spreading inductance, computed mounting
inductance, per-part via geometry. No shunt branch, no modes. This is what makes the form-factor answer
quantitative between about 1 MHz and 100 MHz, which is where the decap self-resonances and the RF crystal
are.

**P2b — The cavity.** The shunt branch, the eigensolve, the mode list, the field maps, adaptive sampling
around narrow resonances. Real, and last: for a compact board it is above the excitation set.

**P3 — A/B.** Two designs, net/refdes/part-number matching, both impedance curves, the delta trace, the
per-part mounting table, both removal rankings, the DC comparison and the report. **It lands with P2a, not
before** (Q-19): review's use for it is weighing the layout's effect on the PDN and deciding which parts
can come off, and that is the frequency answer. A DC-only A/B was rev 3's guess at an early win and it is
not the one — the DC comparison ships inside P3 as supporting evidence rather than ahead of it as a
feature.

**If only one phase is ever built it should be P0**, which rev 2 could not have said: P0 answers a question
these designs have, on artwork that always exists, with an oracle that is arithmetic rather than opinion.

## 6.1 What shipped, per phase (2026-09-18)

All seventeen briefs were built, and two review rounds over the finished code found nine further defects —
*"none of which reported a failure"*, which is the shape this whole series was written against. Findings
live in the `RESOLVED.md` beside each project rather than here.

| Phase | Briefs | What landed |
|---|---|---|
| **P0 — DC** | 1–10 | the `.crail` document; the three companion readers and the `.crlib` part library; the resistive mesh and the fast graph extractor with its classification; the DC solve, the ranked breakdown and the rail chain; the via current check; the window; the board view; copy to clipboard; `circuitrf rail` |
| **P1 — Lumped PDN** | 11–12 | the part models, derating and the source's own life sweep; Z(f), the mask, the anti-resonance table, the aggressor coincidences and the removal ranking |
| **P2a — Distributed low band** | 13 | the mesh's inductance, spreading inductance, and the mounting loop computed from a part's own via geometry |
| **P2b — Cavity** | 14–15 | the shunt branch, the eigensolve, the mode list, the maps, and the resonance search that keeps a log grid from stepping over a narrow peak |
| **P3 — A/B** | 16 | the match, both curves, the delta in dB, the excursion bands, both rankings and the report |
| **Docs** | 17 | `docs/user/reference/railrf.*`, the `Power Rail` example workspace, and this record |

**Three things did not land, and each is recorded where it was found rather than left to be
rediscovered.** None is a phase that was skipped; each is a seam nobody reached.

- **A refdes anchor does not resolve, in the window or on the command line.** `PdnExtractionRequest.Pads`
  is filled by nothing in the application — the import reads the placement table and
  `RailRfViewModel.BuildRequest` does not carry it — so every port is anchored by coordinate and every
  report row reads a point in DBU. **A rail CHAIN is therefore unsolvable**, because `RailOrder` links two
  rails only through a refdes that is a load on one and a source on the other. The cycle refusal still
  works, which is what the gate can honestly gate. `src/Cli/RESOLVED.md`.
- **`circuitrf rail` produces no Z(f).** `PdnSweep` is reachable from the window and from tests only; the
  verb's `-o out.sNp` is still brief 10's P0 placeholder refusal. `src/Cli/RESOLVED.md`.
- **The same gap costs the DC solve its series and shunt parts.** `SeriesElements` and `ShuntParts` are
  constructed nowhere but in tests, so a protection FET, a ferrite or a decoupling bank enters the DC
  answer only as a source's own R and L. Over frequency the parts are the rail's own `Parts` rows and are
  fully modelled. `src/Design/RESOLVED.md`.

---

# 7. Acceptance

Each phase is gated against something that is not our own arithmetic.

- **DC resistance against the closed form.** A straight trace of known width, thickness and length has
  `R = L/(σWT)` exactly, and a stepped trace is the sum over its sections. Both the mesh and the fast graph
  must reproduce it to under 1 %. This is external arithmetic in the sense the PRD requires, and it is the
  cheapest real gate in this document.
- **Fast against Accurate, on the same board.** On a trace-dominated DC path the two must agree to 5 %; on a
  pour-dominated one the fast model must *refuse* rather than differ. This gate is what keeps §2.9's default
  honest, and it is the one that would catch a misclassification.
- **Via current split.** Against a closed-form parallel-resistance calculation for a symmetric via group, and
  against the mesh for an asymmetric one, where the point is precisely that the split is not equal.
- **More than one source, against superposition.** A resistive mesh is linear, so a two-source DC solve is
  exactly the sum of the two one-source solves. That is arithmetic rather than opinion, it needs no
  external data, and it catches the whole class of defect where a second source is stamped once, twice or
  at the wrong node — the class a single-source tool cannot have and this one now can.
- **The rail chain, against a hand-solved two-rail ladder.** Input rail with a known drop, a regulator
  drawing a known current, an output rail with its own loads: the input voltage the second solve starts
  from is a number written down in advance. The gate is that it is the *upstream answer* and not the
  nominal, and the negative half of it is that a cycle in the order is refused rather than iterated.
- **The rectangular cavity.** A uniform rectangular plane pair has closed-form modes and a closed-form input
  impedance. The mesh must reproduce the first six modes to better than 2 %, with monotone convergence in
  cell size.
- **The lumped limit.** Well below the first mode, a plane pair is a parallel-plate capacitor. The extracted
  Z must approach `1/(jωC)` with `C = ε₀εᵣA/h` to under 1 %.
- **Derived part inductance.** `L = 1/((2πf₀)²C)` against the library's own stated L, on rows that carry
  both — an arithmetic gate on the importer, not on the physics.
- **A published measured PDN.** At least one board from the open SI literature with measured Z(f),
  reproduced within the tolerance that literature states. This is the acceptance anchor, and it is external
  data in the sense the PRD requires.
- **Mounting inductance** against a closed-form partial-inductance calculation for a via pair.
- **The A/B report** against two synthetic boards differing in exactly one known way, where the correct
  answer is constructed rather than solved for.
- **The importers against real files, in two tiers** (amended 2026-09-18, per Q-18's deferral). Review will
  supply a reference-design package and the layout tool's own placement and BOM exports; each reader is
  gated on those bytes, and on a refusal where a required field (the placement origin, the Excellon
  format) is absent. Because that package now arrives at **manual testing** rather than before P0, the
  gate is built in two tiers and both ship: a **committed synthetic tier** that runs on every clone, and a
  **guarded real-bytes tier** that skips with a stated reason until the files land. The synthetic tier is
  honest about its own limit — it proves a reader parses its own output and nothing more — and it carries
  a case per §8.5 shape allowance, so those cases are already green or red on the day the package
  arrives.
- **Navigation parity with the layout editor**, and it is a comparison rather than a checklist. Every
  gesture in §11.6's table is driven on the railRF board view and on a layout editor view holding the same
  geometry, and the two resulting viewports must be identical — pan offsets, zoom, and the framing Zoom to
  Fit chose. A list of features can be satisfied by an approximation of each one; asserting the same
  viewport cannot.
- **The clipboard picture carries the overlay.** A copy taken with the drop map showing must contain the
  drop map, asserted against the real SVG text the way `LayoutClipboardVisibilityTests` and the
  `TryRenderToSvg` font tests already assert against Skia's own output. This gate exists because the
  overlay set a copy draws is an **explicit parameter list** (§11.7), so an overlay nobody added to it is
  absent from the picture and nothing reports a failure. The same test asserts the page is framed on the
  painted extent including the overlay, and that a hidden layer does not size it.

---

# 8. Questions — what is settled, and what is left

Numbered so the thread survives iteration. **Q-1 … Q-19 are all closed or decided, Q-18 by deferral**
(§8.5, 2026-09-18 — it no longer blocks P0); their answers are recorded below because the reasoning behind
a closed question is what stops it reopening by accident. **Q-20 … Q-22 are new in rev 4.**

## 8.1 Closed in the rev-1 review

- **Q-1 — Is the Gerber path worth building? — YES, DAY ONE.** §0 describes the customer design arriving as
  Gerbers with a BOM and a placement file. The board-file path becomes the *easier* case, not the primary
  one.
- **Q-2 — Where does the load port come from? — CLOSED.** Clicking the IC's pin field is acceptable; where a
  netlist or board file names the footprint it is pre-selected.
- **Q-3 — How far up? — CLOSED**, and rev 3 goes further: §4.5 shows the cavity is above the excitation set
  on a compact board, so the ceiling is not the binding concern at either end.
- **Q-6 — Multiple rails at once? — CLOSED, then re-closed differently in rev 4.** One rail is *analysed*
  at a time, which stands. What changed is that a railRF **document holds the whole rail set** and solves
  them in dependency order, because review's boards run a primary cell into a regulator that makes a
  second voltage — so the original answer was right about the solve and wrong about the document (§2.2).
- **Live results — CLOSED, then reopened and resolved differently.** Review accepted re-running; the owner
  then asked for a fast default that supports live interaction. §2.9 is the answer, and it is better than
  either: live in Fast, explicit in Accurate.
- **Part parasitics keyed by part number — ADOPTED** (§2.2).

## 8.2 Closed in the rev-2 review

**Q-5 — VRM and source impedance. — CLOSED: assume a battery.** The tool is aimed at battery-powered
designs, and converter data frequently cannot be had — a supplier who fears reverse engineering does not
publish an output-impedance curve. So: **R-L is the v1 source model**, with R swept across cell life
(ohms to hundreds of ohms), and a published curve is accepted as a Touchstone file where one exists. Where
the source is a converter modelled as R-L, railRF states that the answer near the loop crossover is
optimistic rather than quietly producing a monotonic curve that misses the peak.

**Q-4 — Is the capacitor ranking the right shape? — CLOSED: removal is priority one.** The removal ranking
ships in P1, because it is one re-solve per part and needs no cost data at all. The minimum-cost search over
a priced library is a later feature, not a v1 one.

**Q-7 — Is the DC mode wanted, and what should it report? — CLOSED, and it grew.** Wanted. The output is
three things, in this order: a **colour visualisation along the chain**, hot-to-cold, showing where the drop
occurs; the **ranked per-element breakdown**; and a **flag on any layer transition with too few vias** for
the current it carries, since a via has a limited capability before it is at risk from I²R heating. **Current
density is of interest for hot spots and is explicitly a step 2.** §2.4 and §4.2 are written to this.

**Q-8 — Are boards with no reference plane in scope? — CLOSED: there is always a reference plane.** On an RF
product one layer is assigned as the reference ground plane no matter who supplies the board. Two
consequences, both in §2.2: **where there is doubt the tool asks** which layer is the reference rather than
inferring it, and it offers to **extend the assigned reference to a filled or infinite plane** — with the
optimism that buys stamped on every result.

**Q-9 — Which band pays first? — CLOSED: the low one.** The excitation on a mounted board comes from its own
oscillators — a 32 kHz crystal, a converter at 400 kHz–7 MHz, an RF crystal at 13–50 MHz. The parts respond
in exactly that band: a 470 µF bulk with 5 nH of mounting self-resonates near **104 kHz**, a 1 µF ceramic
near **5.3 MHz**, a 100 nF 0402 with 1 nH near **16 MHz**, and the bulk-against-ceramic anti-resonance sits
near **7.1 MHz** — the converter's own fundamental at the top of its range. *(Rev 2 put the 100 nF part at
5 MHz; that was an arithmetic slip, and the corrected figure lands it inside the RF crystal band rather than
below it.)* §6 is re-ordered accordingly, and §2.2's aggressor list is what turns this answer into a feature
rather than a footnote.

**Q-10 — What is in the output set? — PARTLY CLOSED.** **One through-drill file** (so v1 assumes through
vias, §4.2). A supplier reference package typically carries a parts table with references and sourcing
names, which should correlate with the silkscreen. An internal design additionally has the schematic tool's
board-netlist folder. The placement and BOM formats are settled by the samples supplied — §2.2 describes
both and §5 places the readers. What is **not** settled is whether an IPC-D-356 netlist is normally in the
set; that is Q-13.

**Q-11 — Where do the part parasitics come from? — CLOSED: both.** A maintained table keyed by internal part
number carrying C and self-resonant frequency (from which L follows), and the manufacturers' own model web
tools for a per-item Touchstone or SPICE model. So v1 builds **a part-library reader and a per-part file
attachment**, with the file overriding the row. What the table does not carry is ESR — Q-15.

**Q-12 — Derating: correct it, or only warn? — CLOSED: correct it.** The rail's voltage is known, and
capacitance-versus-bias curves are available from the manufacturers' model tools. So the part library carries
a bias curve per part number where one has been obtained, railRF applies it at the rail voltage, and the
parts table shows marked and derated side by side. Where there is no curve it warns and uses the marked
value, and says so on the result.

**Temperature — CLOSED: room temperature only.** This is a selection tool; the design is measured over
temperature in the lab regardless. Everything is computed at 20 °C and the report says so (§2.4).

**Signal integrity — CLOSED and agreed out of scope.** Review's own reason is the right one: the step is very
much heavier in simulation terms. §2.7 stands.

**The antipad mesh risk — NO INPUT, and it stays ours.** Review had no information on it. §9 keeps it as an
engineering requirement rather than a user-facing question.

## 8.3 Decided by the owner

**In rev 3:**

- **The board import lands in a circuitRF cell, default on** (§2.3).
- **Fast is the default and Accuracy is a button** (§2.9).
- **The window borrows the Match Designer's UI** (§11), which has had roughly ten rounds of refinement and
  is the house standard for a tool window.

**In rev 4:**

- **The board view gets the layout editor's whole navigation set** — Zoom to Fit, Zoom to Marquee, Zoom
  Out, Ctrl +/−, arrow-key panning when no text field has the keyboard, and the mouse gestures — which is
  satisfied by the board view *being* `LayoutCanvas` rather than by re-implementing any of it (§11.6).
- **Copy to clipboard must work and must carry railRF's own rendering**, in every format, reusing the
  layout clipboard exporter rather than a second implementation — because that path has already cost
  substantial debugging time across three platforms (§11.7). The corollary, stated as a rule: **railRF
  writes no clipboard code.**

## 8.4 Closed in the rev-3 review

**The rail's sources and loads are both plural, and both sit on pads. — ADOPTED, and it is the largest
change in rev 4.** It arrived against §11's window rather than as an answer to a numbered question: these
boards run a primary source that feeds a regulator making a second voltage, there is more than one load on
a rail, and a source or load location is typically a **component pad** — a battery's being the connector's
pad. So both are add/remove lists keyed on refdes and pin, and the rail set and its dependency order
follow from the same fact (§2.2, §4.3, §11.3).

**Q-13 — Is a board netlist normally in the output set? — CLOSED: yes.** Review confirmed it by pointing
at the example file supplied rather than by the standard's name, so §2.2's assisted Gerber path becomes the
fallback for a package that lacks one. **Which netlist flavour that example is** — circuitRF's existing
reader is IPC-D-356 — is the residue, and it is Q-21.

**Q-14 — The placement origin. — CLOSED: ask.** It is not one house convention to be learned, so railRF
asks which origin the export used, on every import, with nothing pre-selected. A default here would be the
guess the refusal exists to prevent. Headless it stays a refusal naming the flag (§2.2).

**Q-15 — Where does ESR come from? — CLOSED: the dissipation-factor default per dielectric class.** There
is no per-part ESR figure to be had, so the fallback is the basis rather than a stopgap, and every peak
height derived from one is marked indicative wherever it appears. A part's own Touchstone file remains the
only route to a real ESR, which is why §2.2 recommends it rather than merely offering it. The same per-class
figure serves the board dielectric where the stackup states no tan δ. **§9 keeps this as a standing
accuracy condition, not a gap to be filled later.**

**Q-16 — Where do the load currents come from? — CLOSED: typed, per port.** Typing a current per port is
acceptable, and the second half of the answer is the more useful one: **a terminal with no stated current is
not a load**, never a defaulted one. Such a port is an observation port over frequency and contributes
nothing to the DC solve (§2.2).

**Q-17 — What via current limit should the flag use? — CLOSED: a temperature-rise basis, with a table by
drill size as the default.** Roughly 0.3–0.5 A for a 0.2–0.3 mm signal via, 0.7–1.0 A at 0.4–0.5 mm,
1.0–1.5 A at 0.6–0.8 mm and 1.5–2.5 A at 1.0–1.2 mm, all at about a 10 °C rise — with a 0.3 mm drill **at
20 µm of plating** quoted separately at 0.8–1.0 A. **That separate figure is what makes the answer usable**,
because it disagrees with the table's own 0.3 mm row by about two-fold and the term that differs is the
plating thickness. §4.2 therefore computes the limit from the barrel's annulus and uses the table as a
default and a sanity band. The plating thickness itself is Q-22.

**Q-19 — Does the A/B comparison need to run at DC alone? — CLOSED: no, and the question had the purpose
backwards.** A/B exists to weigh what the layout did to the PDN and to decide **which parts can come off**;
the DC load analysis is a separate layout-review step taken once the topology is settled. So the impedance
comparison and the removal rankings are the deliverable, the DC comparison rides along inside P3, and the
DC-only early version rev 3 offered is not worth building (§2.5, §6).

## 8.5 Q-18 … Q-22 — how they landed

*Every question below is answered as of 2026-09-18, and each keeps the reasoning that produced it:
§8's rule is that the reasoning behind a closed question is what stops it reopening by accident.
Q-18's deferral is the one that is still a deferral, and the paragraph that says why is exactly the
one that must not be deleted.*

### How they landed, before the reasoning that produced each

- **Q-18 — the reference package. Still deferred: it did not arrive, so the guarded tier still
  skips with a reason.** The finding is the other half of the question. **Of `R-rail2-14`'s four
  shape allowances, none was exercised, because nothing real was read** — each has a synthetic case
  and is green or red on the day the package lands. That is the whole value of the allowance: it
  cost one fixture each to build now and it would have cost a change to the BOM row type, the
  refdes join or the recognition rule to retrofit. **A fifth shape was needed and was not
  anticipated** — and it came from the artwork rather than from a companion file: a through via is
  joined only to the two conductors its stackup span NAMES, so the layers it physically passes are
  neither shorted to it nor connected by it. The `Power Rail` example is routed on TOP and BOT for
  that reason.
- **Q-20 — typing the regulator's input current and minimum input voltage: ACCEPTED**, both on
  `RailLoad`, both nullable and neither defaulted. A regulator with no stated minimum still appears
  in the headroom table with a null margin, and the run says which rails had no minimum — the drop
  is always reported, the headroom finding only where the number was stated. *Untested against a
  real chain*, because no rail chain is solvable today (§6.1).
- **Q-21 — which netlist flavour: UNANSWERED, and no fourth reader was written.** The example is
  synthetic and carries no board netlist at all, so it is not evidence either way. `BoardNetlistFile`
  reads IPC-D-356/356A and nothing else. This question travels with Q-18 and is answered by the same
  twenty lines of a real file.
- **Q-22 — the plating thickness: it STAYED A TYPED SETTING**, and the implementation gave it a
  sharper answer than the question asked for. `PdnPlatingBasis` has three values and they line up
  exactly with what the number is worth: a stackup via entry or the document's own setting both give
  a *computed* limit, and `Defaulted` — nobody stated it — falls back to the drill-size table and
  says so, because that is the case resting on a number no one checked. §4.2's own suspicion was
  confirmed and quantified: **one current density reproduces review's entire table at 10–12 µm of
  plating**, so the table is not inconsistent with its own separately-quoted 20 µm figure — it is a
  table of a thinner barrel than anyone plates today. It therefore ships as a sanity band and never
  as the rule.

### Q-18 — the reasoning, kept

**Q-18 — How much of the reference package can we have? — DEFERRED TO MANUAL TESTING, and it no longer
blocks P0** (owner, 2026-09-18). §7 gates the three new readers on real bytes rather than on invented
ones. A complete supplier reference package — Gerbers, drill, the netlist Q-13 confirmed, BOM, placement —
and one of your own designs' exports would settle the importers outright and become the acceptance
fixtures. *(They would be committed anonymised: the repo carries no company, vendor or product names, so
part numbers and library prefixes in a fixture are rewritten to the same shape.)*

**The owner's decision is that the package arrives during manual testing, once the briefs are
implemented.** Rev 4 called this question the only thing between the note and P0; that is no longer the
case, and this paragraph supersedes it. What makes the deferral safe rather than a gamble is that Q-18
bundles **two** asks and only one of them is on the critical path:

| | What it is | Deferring it |
|---|---|---|
| **(a) The fixtures** | a package to gate against, committed anonymised | **Defers cleanly.** The importer gate becomes two-tier (§7): a committed synthetic tier that runs always, and a guarded real-bytes tier that skips *with a reason* until the package lands — the arrangement `RfCore.Tests`' own unavailable fixtures already use. |
| **(b) The format shape** | what a real placement file, BOM and netlist *look like* — column names, header form, units, whether there is a header at all | **Does not defer cheaply.** A shape surprise is not a bug fix; it changes the BOM row type, the refdes join or the recognition rule, and each of those propagates into the extractor, the parts table and the A/B matcher. |

So the implementation **allows for (b) rather than assuming it away.** Four shapes a real export plausibly
has are treated as supported from the start, because each is cheap to allow now and expensive to retrofit:
a **one-to-many** BOM-to-placement join (§2.2's own list of approved manufacturers behind one part number
makes a row per manufacturer ordinary); a refdes cell carrying a **range or a list** rather than one
reference; **no header row at all**, which must fail into a refusal naming the flag and never into a
positional guess; and the ordinary text-file mechanics — encoding, line endings, quoted delimiters, a
leading byte order mark.

**The cheap way to close (b) early, if it is ever convenient:** the first twenty or so lines of one file of
each kind. Not a package, not anonymised, nothing committed — enough to read the header, the delimiter,
the units and the column names. That also answers **Q-21** outright, which is worth doing before P0's gate
rather than at it, since a netlist flavour that is not IPC-D-356 is a fourth reader and a phase of its own.

**What the synthetic tier proves, stated plainly so a green suite is not misread:** it proves the readers
parse their own output. It does not prove they parse a real export. That is the whole reason the guarded
tier exists, and the reason the first thing pointed at the package when it arrives is `circuitrf rail` run
as a process — one command exercises every reader, both extractors and the whole solve, with no window
and with every refusal legible in a terminal.

### Q-20 … Q-22 — the reasoning, kept

**Q-20 — The rail chain: what does a regulator need typed?** railRF will solve the rails in dependency
order so a regulator's input voltage is the upstream answer rather than a nominal (§2.2). Two things that
order needs and no file carries: the regulator's **input current** at the operating point being judged, and
its **minimum input voltage** — without the second, railRF can report the input rail's drop but not that
the drop broke the rail downstream, which is the finding the chain exists to produce. Is typing both per
regulator acceptable, exactly as the load currents are?

**Q-21 — Which netlist format is the example?** Q-13's answer identified a file rather than a standard.
circuitRF already reads IPC-D-356; if the supplied example is that, nothing more is needed. If it is
another flavour, that is a fourth reader and it is much cheaper to know now than at P0's gate.

**Q-22 — Is the via plating thickness stated anywhere in what you have?** §4.2 shows it is worth a factor
of two in the via current limit, and the drill file does not carry it. A fabrication note, a stackup
drawing or a board specification may. If nothing does, it stays a typed setting and every flag says which
thickness produced it — which is honest but means the one number the flag turns on is the one number nobody
checked.

---

# 9. Risks

**Capacitance derating — now correctable, still the largest accuracy risk.** An MLCC's capacitance falls with
applied DC bias, and for a small-case high-value part it falls a very long way: a 10 µF 0402 X5R can be under
2 µF at its rated voltage. A PDN answer computed with the marked value is wrong by a factor of several in
exactly the band the bulk capacitors own, and it is wrong *optimistically*. Q-12 closes the mechanism — the
rail voltage is known and bias curves are obtainable — so the remaining risk is coverage: a library that is
only partly populated with curves produces a result that is partly derated, and that is worse than either
extreme unless it is visible. **Every part row shows marked, derated and which it used, and the result
carries a count of parts with no curve.**

**ESR coverage — and Q-15 closes it as a permanent condition, not a gap.** C and f₀ place a resonance; ESR
sizes it. There is no per-part ESR figure to be had, so a defaulted one is the normal case rather than the
degraded one, and **a mask margin in dB computed from an indicative peak looks exactly as authoritative as
a real one.** Every such margin is marked wherever it appears — part row, plot, table, export provenance —
and that marking is now load-bearing rather than temporary. The one route to a real number is a part's own
Touchstone file, so the parts table's count of *how many parts are modelled from a file* is a headline
number and not a detail.

**The stackup is usually wrong.** Designers copy a stackup from the last board. The plane-to-plane dielectric
thickness sets the plane capacitance linearly, and a 2× error there is a 2× error in the answer at every
frequency below the first mode. railRF shows the extracted plane capacitance as a single number early and
prominently, because a designer recognises a wrong one instantly and would never notice it buried in a curve.
The same applies to copper weight: 0.5 oz where 1 oz was assumed doubles every trace resistance in §2.8's
table.

**The fast model's classification.** §2.9's rule 2 exists because this is the failure that would not announce
itself: a wide supply polygon treated as a trace is optimistic, and the number looks entirely ordinary.
Drawing the classification and gating Fast against Accurate (§7) are both required, not optional.

**The mesh at antipad fields.** A BGA's antipad array removes a large fraction of the copper in a small region
and it is precisely under the load port. Too coarse a mesh there and the spreading inductance is
underestimated — again optimistically. Local refinement under port regions is a correctness requirement, not
an optimisation. Review had no input here; it stays our own risk.

**The rail chain invites a simultaneous multi-rail solve, and that invitation should be refused.** Solving
the rails in dependency order is a few lines and it answers the question review has. Solving them *together*
— a regulator as a two-port with a forward transfer and a PSRR — is a different model, it needs exactly the
data §8.2 records as frequently impossible to obtain, and it would be entered by accident the first time
someone asked for a cycle in the order to be supported. **The refusal on a cycle (§2.2) is the boundary and
it is load-bearing**, not a limitation to be lifted when convenient.

**Several sources on one rail invite an average, and the answer is the one thing an average cannot give.**
Two supplies feeding one net do not share in proportion to anything a designer can see; the copper decides,
and on a compact board it decides badly. The superposition gate in §7 exists because this is cheap to get
subtly wrong — a second source stamped at the wrong node produces a plausible number, not an error.

**Scope creep toward signal integrity.** Every one of these primitives — a plane pair, a mesh, a port — is one
step from a return-path-discontinuity tool. That is a different product and a much heavier simulation. §2.7 is
the boundary and it should be defended.

---

# 10. What this reuses

Worth listing, because it is the argument that this is a large feature rather than a new product:

- Gerber, Excellon, DXF, board-netlist (IPC-D-356) and `.kicad_pcb` import — **built**
- The cell-folder and layout-view documents the import lands in — **built**
- The technology/stackup model and its editor — **built**
- Net extraction and connectivity, including the layer bridge through via geometry — **built**
- Sparse complex MNA with CSparse — **built**
- Adaptive frequency sweeps — **built**
- The `DataSet`/`DataCube` result model, `.npy`/MATLAB/Touchstone export — **built**
- The Data Display: plots, masks as traces, markers, overlays, tear-off windows — **built**
- `src/Render`'s layout renderer, below the firewall, so the window and a headless report draw alike — **built 2026-09-07**
- `LayoutCanvas` and the whole of its navigation — pan, cursor-anchored zoom, Zoom to Fit, the one-shot
  magnifier, Ctrl +/−, arrow-key panning, rulers, hit-testing, per-document viewport persistence — **built**
- The `ILayoutCanvasOverlay` seam, which is how wBond already draws on that canvas without owning it — **built**
- The multi-format clipboard write — painted-extent page framing, PDF/SVG/PNG writers, the Windows
  enhanced-metafile path and the SVG font repair — **built, and debugged over several rounds across three
  platforms**, which is the reason §11.7 writes none of it again
- Passive-part readouts, so a vendor `.sNp` becomes a PDN element — **built 2026-09-06**
- Touchstone health checking, so a vendor file is trusted for a reason — **built 2026-09-06**
- A tool-as-document shell with its own menus, window and standalone binary — **built twice**, for the Match
  Designer and harmonicaRF

What is genuinely new is the two extractors, the placement/BOM/part-library readers, the mode solve, the
mask/ranking/coincidence arithmetic and the comparison report. That is the honest measure of the work.

---

# 11. The window

**The owner's instruction is to borrow from the Match Designer**, which has had roughly ten rounds of UI
refinement, and to keep this clean and simple. So this section is deliberately short on invention: it names
what is taken, and only describes what is genuinely new.

## 11.1 Taken from the Match Designer verbatim

Not "inspired by" — the same classes, the same conventions, and where possible the same controls
(`src/Ui/Views/Match/MatchDesignerWindow.axaml`):

- **Two-tier chrome.** `Border.pane` for a region, `Border.card` for a group inside it, so a group reads as a
  thing sitting *on* the panel rather than a rectangle drawn on it. One tile border, 6 px corners.
- **Sentence-case headings**, never shouted.
- **Label left, value right**, with the numbers forming one right-aligned column whether the row is settable
  or read-only. Settable values are `InlineEditText` with the circuitRF value+unit conventions, so unit
  handling, validation and formatting come for free; a disabled one is visibly dimmed.
- **A status strip that states numbers**, and **refusals that appear there with numbers in them** — *"the
  placement file does not state its coordinate origin; pass --origin or set it here"* is a sentence this UI
  must be able to say plainly, with the affected input turning red.
- **A docked list panel, not a modal sheet**, for the results lists — so a user can click through rows and
  watch the map and the curve change.
- **The golden-ratio opening size**, non-modal, resizable, opened per document, with a minimum that can
  actually show the specification column.
- **Compact sliders** (the negative-margin trick), so a slider in a row does not make the row tall.
- **Plots are `PlotControl`s** in rectangular mode, fed from a `DataSet` — never a bespoke chart.

## 11.2 The owner's own rules for this window

- **Centred text in comboboxes and on buttons.** `HorizontalContentAlignment="Center"` on every push button
  and on the combobox's selection and its items. The one exception is already an owner decision in the Match
  Designer and it stays: a **click-to-sort column header stays left-aligned with the column under it**,
  because any centring there is a misalignment the user can see.
- **Clean and simple by default.** Advanced settings are reachable and do not clutter: the reference-plane
  options, the via limit basis, the meshing density and the temperature note live behind `Settings`, not on
  the face of the window.

## 11.3 Layout

One resizable window, opened per railRF document:

```
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ railRF — evk_1v8_compact                            [Report ▸] [Settings] [Help] [Close]   │
├────────────────────┬──────────────────────────────────────┬────────────────────────────────┤
│ SPECIFICATION      │ BOARD       [copper│drop│|Z|│class]  │ RESULTS         [DC│frequency] │
│                    │                                      │  ┌──────────────────────────┐  │
│ Rail  [ +1V8    ▾] │   ┌────────────────────────────────┐ │  │ |Z| vs f, mask shaded,   │  │
│  Net    [ +1V8  ▾] │   │                                │ │  │ aggressor lines drawn    │  │
│  Ref.   [ L2    ▾] │   │   the real artwork, drawn by   │ │  └──────────────────────────┘  │
│  Extent [as imp ▾] │   │   LayoutRenderer, with the     │ │  Drop  U1  70 mV / 80 mV   ✔   │
│                    │   │   drop map or |Z| map as an    │ │  Worst Z   93 mΩ / 50 mΩ   ✘   │
│ Sources    [+] [−] │   │   overlay                      │ │  +5.4 dB over at 7.1 MHz       │
│  BT1.1  batt 2.5 Ω │   │                                │ │  ← on the converter fundamental│
│  U2.OUT  R-L    ▸  │   └────────────────────────────────┘ │                                │
│ Loads      [+] [−] │                                      │  BREAKDOWN / ANTI-RESONANCES   │
│  U1.VDD    120 mA  │  PARTS                               │  ┌──────────────────────────┐  │
│  U3.VCC     15 mA  │   ┌────────────────────────────────┐ │  │ ranked rows, one per     │  │
│  U4.VIN   observe  │   │ refdes · P/N · value · derated │ │  │ element or per peak      │  │
│  I dc   [ 120 ] mA │   │ · model · L_mount · flags      │ │  └──────────────────────────┘  │
│                    │   └────────────────────────────────┘ │  [ Rank capacitors ]           │
│ Target             │                                      │                                │
│  Drop  [  80 ] mV  │  AGGRESSORS           [+ add] [− ]   │  VIA CHECK                     │
│  Z     [  50 ] mΩ  │   32 kHz xtal · 3.3 MHz conv ×8 ·    │   2 transitions flagged        │
│  Band [10k…200M]Hz │   38.4 MHz xtal ×3                   │                                │
├────────────────────┴──────────────────────────────────────┴────────────────────────────────┤
│ Fast model · 4.1 ms · 20 °C · reference as imported · 3 parts with no bias curve           │
│                                              [Accuracy ▸] [Compare…] [Export ▾] [Run]      │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Four things in that sketch are the whole design:

1. **The centre is the board, and it is the layout editor's own canvas.** Not a schematic, not a sketch and
   not a picture of a layout — `LayoutCanvas` itself, drawing the imported artwork with railRF's maps as an
   overlay through the seam wBond already uses. The tabs switch the overlay, never the geometry; `class` is
   §2.9's trace-versus-mesh classification, which is a requirement rather than a diagnostic. Everything
   this buys — and the traps it inherits — is §11.6, and the copy out of it is §11.7.
2. **The status strip carries the model.** *Fast model · 4.1 ms* is always on screen, so nobody reads a fast
   answer as an accurate one, and the elapsed time makes the cost of `Accuracy` obvious before it is pressed.
   The reference-plane choice and the derating coverage sit beside it for the same reason.
3. **`Accuracy` is a button on the bottom bar, next to Run** — the one control that changes what the numbers
   mean, in the place the user looks when the design is settled.
4. **Sources and loads are lists, not fields.** Review's boards have more than one of each — a cell through
   a connector, a regulator making a second voltage, several ICs on one rail — so both carry add and remove,
   each row is a refdes and a pin rather than a coordinate, and the **rail selector above them** is what
   says which rail the whole window is currently showing (§2.2). A load row with no current reads
   *observe*, because that is what it is — reached by clearing the cell, since 2026-09-21 a new row
   arrives carrying a starting current instead (the amendment under §2.2's "loads and their currents").

A fifth was added on 2026-09-19, at the owner's request:

5. **Each of the four panels has a lamp on the top bar, left of `Open` and behind a rule.** They are in
   the window's own left-to-right order — specification, board, parts, results — and all four start lit.
   The point is temporary room: hiding one gives its space to the panels still on screen, so the board
   can have the window for a moment without a splitter to drag back afterwards. **It is kept in the
   `.crail`** (`RailPanels`), at the owner's instruction, and it is the one piece of pure view state
   the document carries: the question it answers is about a BOARD rather than about a sitting, so a
   reader who gave the artwork the whole window to review one board gets it back the next time they
   open *that* board. **Absent from the file means all four are shown**, and the block is written only
   when something is hidden — so a `.crail` from before this, and every one nobody has collapsed a
   panel in, is unchanged. The specification panel is the one that never
   *expands*: a rail selector, two short lists and a target are legible at 300 px and gain nothing from
   more, so its column is fixed-or-gone. The board and the parts table share the centre column, so
   hiding one of them widens nothing and hiding **both** is what collapses the column and hands its
   width to results. **At least one panel is always showing**: four hidden panels is a window with a
   toolbar, a status strip and nothing between them, so the last lit button is *disabled* rather than
   inert, and a hand-edited `.crail` that hides everything opens with everything.

   Saving arrived with it. **The window could open a `.crail` and never write one back** — `Save` and
   `Save as…` sit beside `Open` on the top bar with ⌘S and ⇧⌘S, the title carries a leading bullet
   while the document differs from disk, and closing a window with unsaved work asks, through the same
   dialog wBond uses for the same reason (one window per document means closing the window *is*
   closing the document). Until this, every edit the window made — the rail set, the sources and
   loads, the targets, the four settings, the class overrides — lived only as long as the window did.

A sixth followed on the same day, from a pass over the window:

6. **It has a menu bar, of three menus.** **File** is the toolbar in the toolbar's order — `Open…`,
   `Save`, `Save as…`, `Import Board…`, `Report…`, `Export…`, `Compare…`, `Close` — and it is a second
   *route*, never a second implementation: each item binds a command whose hook calls the method that
   button's own `Click` already calls, so there is one Open, one Export and one Compare. `Report…`,
   `Export…` and `Compare…` dim until something has been solved, which a menu item needs told
   (`CanExecute` is re-asked where `CanExport` is announced) where a button re-reads the property for
   itself. **View** carries `Zoom to Fit` and a checkbox for each of the four panels plus the results
   readouts — every one of them the same command its toolbar lamp runs, never a second copy of the
   state. **Window** lists every open circuitRF window, selectable to bring it to the front, resolved
   through the workspace's own enumeration so there is one ordering rule rather than two; railRF
   appears in *circuitRF's* Window menu for the same reason, by implementing `ICrfMenuWindow`. That
   pairing is the point: a railRF window is shown UNOWNED so it can go behind the workspace, which is
   correct and is also how a window gets lost. Both surfaces are hand-mirrored (the macOS `NativeMenu`
   and the in-window `Menu`), as every other menu bar in this application is — and **the four
   keystrokes (⌘O, ⌘S, ⇧⌘S, ⌘W) live in `Window.KeyBindings` only**: a `NativeMenuItem`'s `Gesture` is
   *functional* on macOS, so spelling one there as well would arm the same keystroke twice, while the
   in-window `InputGesture` is display-only and cannot.

   Three smaller things came with it. **The title names a FILE** — `evk_1v8.crail`, extension and all
   — with the full path on its tooltip and a *Reveal in Finder / Explorer* on its context menu, through
   the one shared `FileReveal`; the text drawn in the window is the file name alone, while the OS title
   bar keeps `railRF — `. **The aggressor rows are settable**: name, frequency and harmonic count are
   `InlineEditText` like every other value in the specification column, where the row had been one
   read-only summary line and `+` therefore made a row nobody could name or tune. And **the results
   readouts have their own toggle** in the results strip, behind a rule: off, the text cards go and the
   plot's height ceiling lifts to take the space, which is the whole of what that control buys.

   **And the markers on the results plot are kept in the `.crail`** (`RailSpec.Markers`). A marker is
   a reading somebody took — the thing they came to the window for — and the document said nothing
   about markers at all, so every one was gone when the window closed. Each is stored by what a curve
   IS, the READING and the PORT, which is the same key the window already carries a user's edits
   across a re-solve with: every trace on that plot is rebuilt from the sweep on each committed edit,
   so a marker pinned to a trace object would be pinned to something that does not survive a
   keystroke. Restore and capture are both **per curve**, because the accurate reading's curve exists
   only after `Accuracy` has been pressed — a capture that rebuilt the list from what is drawn would
   quietly delete the markers of the reading that is not on screen. **Moving a marker, or its info
   box, marks the document**: the capture runs off `DataDisplayViewModel.ContentChanged`, the same
   channel a `.cdd`'s own dirty check runs off, so a drag, a rename and a close are all seen.

   **And the results plot follows the light/dark variant.** It is a real Data Display `PlotControl`, so
   it wears the Data Display's own palette — `RenderTheme.Light` / `RenderTheme.Dark`, chosen exactly as
   a Data Display document chooses it. Both halves are set: the control's own `PlotTheme` draws the
   axes, grid, labels and markers, and the host's `Theme` repaints the marker info boxes. Neither was
   set at all before, so in dark mode all of it was drawn in the light palette.

## 11.4 Where it opens from

From the Tools menu for a new document, and by double-clicking a railRF document in the project tree. A
selected railRF document's Properties panel shows a compact summary — the rail, its reference, its source
count and load count, worst drop and worst margin — and an **Open railRF…** button, following the same
pattern as the Match and wBond panels.

## 11.5 Headless

Everything the window does, `circuitrf rail` does with no display: `--rail` to name which rail of the set,
`--fast` (default) / `--accurate`, the mask, the aggressor list, the breakdown, the via flags, and `-o` for
Touchstone, `.npy`, CSV or an SVG/PDF report drawn by the same renderers. Sources and loads are repeatable
arguments (`--source BT1.1=batt` and `--load U1.VDD=120mA`, spelled per the CLI's own value+unit
conventions), and **a load with no current is accepted as an observation port** rather than refused,
because that is what it means in the window. Omitting `--rail` on a document holding several rails runs
them all in dependency order, which is the shape `hb`/`lp` already have for a wrapped sweep. Per
`docs/design/cli.md`, the verb holds no analysis logic of its own — a rule the automation verbs already
follow, and the reason a board can be gated in CI.

## 11.6 The board view is the layout editor's canvas

**Owner instruction: the board in the centre of that window pans and zooms exactly as the layout editor
does, by mouse and by keyboard.** Not "similarly" — the same gestures, the same step sizes, the same
anchoring. Someone who has learned one of these windows has learned the other, and a near-miss is worse
than an absence because it is discovered by being wrong.

The way to get that is not to re-implement it. **railRF's board view IS `LayoutCanvas`**
(`src/Ui/Controls/LayoutCanvas.cs`), and railRF's own drawing arrives through the `ILayoutCanvasOverlay`
seam that wBond already uses to draw wires on that same control. The overlay is offered every gesture
first and declines everything that is not its own, so pan, zoom, the marquee, the magnifier, the rulers and
the hit-test all reach the canvas's own state machine untouched — an arrangement that exists precisely
because a transparent sibling control layered on top would swallow every event it did not want. What railRF
adds is a drop-map overlay, an impedance-map overlay and the classification overlay, **and no navigation
code at all.**

| Gesture | What it does | Where it already lives |
|---|---|---|
| Wheel, or two-finger scroll | zoom, anchored on the world point under the cursor | `LayoutViewport.WithZoomAnchoredAt` |
| Middle-button drag, or Space + left drag | pan | `LayoutCanvas`'s pan latch |
| `F` | Zoom to Fit | `LayoutCanvas.ZoomToFit` over `LayoutViewport.ZoomToFit` |
| `Z`, then a left drag | Zoom to Marquee — the magnifier, **one shot**, self-disarming | `ArmZoomBox` / `ZoomBoxMarquee` |
| Ctrl/⌘ `+` and `−` | zoom one step about the centre, main row **and** numeric keypad | `ZoomIn` / `ZoomOut` |
| Arrow keys | pan 40 device pixels a step, ×5 with Shift | `CanvasArrowPan.ScreenStep` |
| `Escape` | disarms the magnifier and drops a half-drawn box | `DisarmZoomBox` |

**And the same four toolbar buttons**, in the same order and with the same tooltips the layout editor's
carry: Zoom to Fit, the Zoom Box magnifier (which lights while armed), Zoom Out, and Zoom 1:1. The
keyboard is not the only route to any of these, and the buttons are where a user who has never read a
tooltip finds them.

Five things in that table are traps already paid for once, and this window must not reopen any of them:

1. **Every keyboard gesture is gated on nothing else owning the keyboard.** `F` is an ordinary letter in a
   net name and `Z` is one in a part number; the layout canvas suppresses both while a label is being
   typed, and **railRF's window has far more text fields than the layout editor does** — its whole left
   column is editable rows. The gate is therefore wider here, not narrower: no navigation key fires while
   focus is in any text-entry control.
2. **The arrow keys are the case that instruction turns on.** They pan only when nothing is selected and
   nothing is being typed, because in every one of these editors an arrow key already nudges a selection
   and that gesture is the older one. In this window the competing claim is stronger still — an arrow key
   inside a value field belongs to the caret, always — so the pan is what an arrow key means on the board
   panel and nowhere else. It matters because **a trackpad has no middle button** and the two-finger
   scroll is spent on zoom: on a laptop the arrow keys are the only pan there is.
3. **Every held-key latch is dropped on `LostFocus`.** Hold Space to pan, then click a button on the bottom
   bar; the key-up goes to the button and the canvas never sees it, so the latch stays set and from that
   moment every left-drag is a pan with nothing on screen explaining it. That was diagnosed once already —
   and in a window whose board panel is ringed by controls it is *more* likely here than it was there, not
   less.
4. **Zoom to Fit must include the overlay's own extent.** `ContentBounds()` exists on the overlay seam for
   exactly this: the canvas fits the union of the layout's shapes and the overlay's, and an overlay whose
   content is in neither gets framed out. A drop map is co-extensive with the copper, so this looks
   harmless — until a legend, a source marker or a flagged-via callout sits outside the copper's own bbox
   and Zoom to Fit cuts it off.
5. **The viewport is per document and it persists.** A railRF document reopens where it was left, as a
   layout document does, through the mechanism already tested.

Two further rules follow from the overlay contract rather than from navigation, and both are correctness
rather than polish: an overlay **never touches the layout model** — nothing railRF draws enters the
`.clay`, and a map repaint must go through `InvalidateOverlay` rather than invalidating the path cache,
which on a real board is the difference between a repaint and a rebuild of half a million shapes — and an
overlay's coordinates are the canvas's own **database units**, converted at this boundary and nowhere
else.

## 11.7 Copy to clipboard, with railRF's own rendering in it

**Owner instruction: a copy out of railRF must land as a picture in a document or a slide, it must carry
railRF's own rendering and not just the copper, and it must reuse the exporter that already exists — this
path has cost real debugging time across three platforms and none of it should be spent again.**

So railRF **writes no clipboard code**. The existing pieces, in the order they are called, each with the
finding that put it there:

1. **Frame the page from what is PAINTED** — never from the current pan and zoom, and never from raw
   geometry bounds. Two separate defects are baked into that sentence. A label's stored bbox is a *point*,
   so a page unioned from geometry bboxes once sized itself to almost nothing and hung the content off the
   edges; and a hidden layer must not size the page, which is why `LayoutClipboard.ComputeSelectionBounds`
   and `LayoutRenderer.Draw` now read the same `LayerDef.Visible` flag — before they did, two shapes on two
   layers with one hidden produced a page spanning both and a visible shape too small to read. **railRF's
   overlays are part of the painted extent** and go into that same pass, which is the same requirement as
   §11.6's point 4 seen from the other side.
2. **Resolve colour and background through `ClipboardRenderPolicy.Resolve()`** — one app-wide setting,
   never a per-call parameter, and the contract every copy path in this application already signs.
   railRF's maps are the one place this needs thought. For every other copy the background is a *backdrop*,
   so honouring transparency is a matter of not painting it; the stackup copy is the counter-example, and
   it is instructive — its renderer uses the background colour as **paint**, to cut a drill hole, and
   simply not painting the bore did not make it transparent, it showed the dielectric behind it. A hole had
   to be cut in what was behind. **A railRF map must not acquire that shape**: the shading is opaque paint
   laid over the copper and the legend carries its own scale, so nothing in the picture depends on the
   page's background being any particular colour. That is a design constraint on the overlay, decided here
   rather than discovered in an export.
3. **Render PDF, SVG and a 2× PNG through the writers the plot exporter already uses**, and write them all
   at once with `PlotExporter.SetClipboardDataAsync`. On Windows that call bypasses Avalonia's clipboard
   entirely and performs **one** P/Invoke session covering every format, with the enhanced metafile first —
   which is what a slide or a word-processor document takes when it iterates the formats on offer. On macOS
   and Linux it is one transfer carrying the native PDF and SVG types, the bitmap and the text, with a
   text-only fallback if the platform refuses the multi-format write. It is one call and not four because
   **a second Avalonia clipboard session fails on Windows** for reasons `WindowsClipboard`'s own header
   records.
4. **Exports opt out of the level-of-detail tiers** (`DetailPixelThreshold = -1`). A PDF or SVG page has no
   device pixels to budget against, and a pasted bitmap may be rescaled away from the size it was rendered
   at — so what is stored is what is drawn, exactly as `circuitrf render --detail full` already does. On a
   real board this is a visible difference, not a theoretical one.
5. **The font repair is not optional.** Skia's SVG device writes font-family names and embeds no font data,
   so every emitted SVG passes through `SvgFontNormalizer`, and on Windows the embedded faces are
   registered for the process for the duration of the render. **A copy must never be able to fail because
   of a font** — which is the rule that made that repair a shared step rather than an export-only one.

**The one thing that is genuinely new, and the one that will break.** The overlay set a layout copy draws
is an **explicit parameter list**: the EM mesh, the current-density map and the DRC markers were each added
to `LayoutClipboard.CopyAsync` as they arrived, and each rides the *picture* while staying deliberately out
of the JSON payload — a mesh or a violation marker is a result, not geometry, and must not paste into
another layout as though it were. railRF's overlays follow that rule exactly, and the failure mode follows
from it too: **an overlay nobody added to that list is silently absent from the copy.** The picture is still
produced, it still looks correct, and the one thing the user copied it for is missing. That is why §7 gates
the overlay's presence in the real SVG text rather than trusting the wiring.

**What lands on the clipboard.** The rail's own state as marker-guarded JSON, so a copy pastes back into
another railRF document and a foreign paste is ignored rather than half-parsed — plus PDF, SVG and a bitmap
of the board *as drawn*: the active map overlay, its legend, the source and load markers, the flagged vias
and the rulers, which count as content. **An empty view is not a licence to return early.** A copy that
writes nothing to the system clipboard leaves the *previous* copy sitting there, so the next paste produces
something unrelated and nothing reports a failure — that is precisely what a ruler-only layout copy did,
and it is a further reason railRF's copy is of the **view** rather than of a selection.

Ctrl/⌘+C on the board panel and a **Copy** item in its context menu, dispatched through
`WorkspaceViewModel.InvokeClipboardAsync` on the active document's type, exactly as every other document's
copy already is. The same render path produces the picture in the `Report ▸` menu and in the headless
report, so there is one route from an overlay to a page and not two.
