---
title: Create Schematic from Artwork
slug: reference/artwork-to-schematic.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Create Schematic from Artwork
lede: Turn a board's copper into a schematic of native components — lines, bends, tees, vias and parts — that simulates in seconds, reviewed first in a parts table you can correct.
keywords: recognise, recognize, artwork, Gerber, board, parts table, BOM, placement, MLIN, CPWG, SLIN, TLIN, VIAGND, FromArtwork, Show in Artwork, schematic from layout, ground, stitching vias, coplanar, GCPW, Swap Line Type, worked example, import part
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#example">A worked example</a></li>
<li><a href="#open">Opening it</a></li>
<li><a href="#target">Target</a></li>
<li><a href="#scope">Scope</a></li>
<li><a href="#options">Options</a></li>
<li><a href="#companions">BOM and placement files</a></li>
<li><a href="#parts">The parts table</a></li>
<li><a href="#recognised">What is recognised, and what is not</a></li>
<li><a href="#coplanar">Microstrip or coplanar, and Swap Line Type</a></li>
<li><a href="#ic">A board with an IC on it</a></li>
<li><a href="#report">The report</a></li>
<li><a href="#probe">Show in Artwork</a></li>
<li><a href="#fromartwork">Models existing artwork, and layout sync</a></li>
<li><a href="#headless">From the command line</a></li>
</ol>
</nav>

## What it does {#what}

A board of ordinary SMT parts joined by controlled-impedance lines does not need an EM run: its copper is
already a circuit. **Design ▸ Create ▸ Schematic from Artwork…** reads that circuit out of the artwork &mdash; an
imported Gerber set, a flattened GDSII, a board read back as copper &mdash; and writes it as a schematic of
native circuitRF components:

- every two-terminal part as an **R**, **L**, **C**, or an S-parameter file where the workspace has one for its
  part number;
- every trace as **MLIN** (with **MBEND**, **MTEE**, **MCROSS** and **MTAPER** where the copper has them),
  **CPWG**, **SLIN**, or **TLIN** where no circuit model fits &mdash; each bound to the layout's own technology;
- the vias that matter as **VIA** and **VIAGND**, and ground stitching dropped;
- a port wherever a line leaves the circuit, and an S-parameter analysis ready to run.

It is a best attempt that you then check and clean up. Everything it guessed and everything it left out is
said, with a count, in the [report](#report).

## A worked example {#example}

**File ▸ New ▸ Examples ▸ Artwork to Schematic** is a small two-layer board as a fabricator receives it: Gerber and
drill files, a placement file and a bill of materials in its `fab` folder, already imported as the cell **Board**.
A microstrip line enters at the left edge, turns through a 90° bend, passes a tee with an open stub, a series
capacitor C1, a shunt inductor L1 to a ground via, and a series resistor R1, and leaves at the top edge. The bill of
materials gives no value for L1, on purpose. Beside it, **Board design** is the schematic the board was drawn from.

1. Open **Board**'s layout. Its technology is the one the import wrote beside it, with the fabricator's stackup
   typed in on the **Stackup** tab &mdash; a Gerber set carries no stackup, and every line's impedance comes from it.
2. **Design ▸ Create ▸ Schematic from Artwork…**. Name **Placement…** `fab/Board.pos` and **BOM…**
   `fab/Board-bom.csv`. The report reads ground on the bottom plane, one `VIAGND`, two ports at the board edges,
   seven `MLIN`s, a bend, a tee and one open end &mdash; the stub.
3. In the [parts table](#parts), C1 (2.2 pF) and R1 (10 Ohm) have their values from the BOM. L1 is a shunt L of
   case 0402 with no value: it becomes the variable `L1_L`, starting at a transparent 1 µH.
4. **Create**. The schematic `Board_model` opens beside the artwork. **Simulate**: at 2 GHz S11 is about −7.7 dB.
5. Simulate **Board design**: −15.8 dB. In `Board_model`'s **Tuning** panel, click `L1_L`'s minimum, type 1 nH, and
   drag the slider down. At 8.2 nH the two agree &mdash; every other value and every line length came off the board.

The example's README walks the same steps with the dialog's reports in full. [AN-02](../app-notes/an02-gerber-import.html) starts
one step earlier, from the Gerber files, and has a figure for each step.

## Opening it {#open}

Focus a **saved** layout and choose **Design ▸ Create ▸ Schematic from Artwork…**. A layout that has never been
saved has no cell to write beside, and is refused with a message, as **Update Schematic from Layout** refuses
it. The dialog reads the layout and its technology as they are saved on disk. When either has unsaved changes
&mdash; a stackup you have just built in the Technology editor, say &mdash; it asks first: **Save** writes them,
**Don't Save** reads what is on disk, **Cancel** stops.

{{ui: artwork-to-schematic-dialog}}

The dialog stays open beside the layout: selecting a row of its parts table marks that part on the artwork.
Recognition runs when the dialog opens, and again whenever the scope, an option or a companion file changes;
the strip at the bottom shows its progress.

## Target {#target}

| Choice | Writes |
|---|---|
| **New cell** | A new cell beside the artwork's, holding a schematic only. The name defaults to `<cell>_model` and is checked as you type. |
| **This cell's schematic** | The artwork's own cell. Offered **only when that cell has no schematic view, or an empty one**, so you can start a schematic there and clean it up in place. An empty drawing is written over under its own name. |

A name that is a cell this command created earlier turns the button into **Replace**; its confirmation says
that a history checkpoint is taken first, so the replaced schematic can always be restored. A cell whose
schematic you drew yourself is never replaced &mdash; choose another name.

## Scope {#scope}

**Whole layout**, or **Selection** &mdash; the copper inside whatever is selected in the layout. Copper outside the
scope is not read. A line that runs on past the selection's outline becomes a **port** there, which is how you model
one section of a board without any EM. Ground is always read from the whole board.

A selection also reads only the **layers** its shapes and parts are on. Copper on another layer under the outline
&mdash; an inner-layer trace or a power plane crossing beneath a selected top-layer line &mdash; is read only where a
via joins it to the selected copper, and the report says which layers were left out. When you select a line and
the pads at both of its ends, nothing runs on past the outline, so there is no port: place port labels where the
circuit's ports belong.

## Options {#options}

| Option | Choices |
|---|---|
| **Arrange** | **As on the board** (the default) draws each part roughly where it sits on the board, so you can check the schematic side by side with the layout. **Tidy** draws the signal path as one line from port to port with everything else hanging from it. The circuit is the same either way. |
| **Ground** | **Auto** reads the largest copper on the stackup's reference conductor, and everything joined to it, as ground. **Pick on layout** asks you to click the copper that is ground. |
| **Ground vias** | **Model as VIAGND** keeps a via that grounds a part's own pad or a line end as a stackup-bound `VIAGND` (at most four per pad). **Plain GND** writes a plain ground instead. Stitching vias are dropped either way. |
| **Coplanar lines** | How a line with ground close beside it is read: **Auto**, **Microstrip** or **GCPW** &mdash; see [the coplanar choice](#coplanar). |
| **Frequency** | The S-parameter analysis written with the schematic. Default: the layout's EM setup's sweep, else 100 MHz – 6 GHz in 201 points. The stop frequency is also the frequency coupled lines and each `TLIN` are judged at. |
| **Values** | The significant digits every number in the schematic is written with &mdash; line widths and lengths, via sizes, part values and tuning ranges: **3**, **4**, **5**, **6** (the default) or **All digits**. |

## BOM and placement files {#companions}

**BOM…** names a bill of materials: values, part numbers and kinds by designator. It reads:

- a bill of materials with a header row &mdash; a column of designators, and any of value, footprint or package,
  description or type, part number and quantity; grouped designators (`M1,M23`, `C1-C9`) and DNP rows are understood,
  and a unit in the column after its number (`100`, `pF`) is read with it;
- the file **File ▸ Export ▸ Bill of materials…** writes, from a layout or [from a schematic](#bom-export);
- a circuitRF schematic (`.csch`) or netlist (`.cnl`, or a netlist saved under any other name), read as the circuit
  it is: every resistor, capacitor, inductor and ferrite bead with its value and footprint.

A bill of materials lists what is fitted. With one named, a two-pad land pattern it has no row for is read as not
fitted (Open), unless a placed part in the layout says what it is. A part that is fitted but whose value is unknown
keeps its row with the value left empty, and becomes a variable to tune.

A kind the bill of materials states beats the one a designator implies: a ferrite bead `FB1` listed as `R` with
`0.01 Ohm` is a resistor of that value. A part you placed on the board keeps its own kind.

**Placement…** names a
placement file: where each designator sits. A placement file that does not say which point of each part its
coordinates are shows a three-way **Origin** choice &mdash; the footprint's symbol origin, the part body's
centre, or pin 1 &mdash; with **nothing chosen**, and recognition waits until you choose. On an 0402 the
difference is the width of a pad, so it is never guessed.

### A bill of materials from your schematic {#bom-export}

When you have the schematic the board was drawn from, open it and choose **File ▸ Export ▸ Bill of materials…**: it
writes one row per part to fit &mdash; designator, value, footprint and type, a part disabled as Open listed as `DNP`
&mdash; in the columns **BOM…** reads. Ports, sources, lines and vias are not listed.

## The parts table {#parts}

One row per part found on the board. **Kind**, **Value** and **Model** are yours to correct, and so is
**Refdes** where it was read off the silkscreen or made up (`C_A1`); every other column is measured on the board.

| Column | |
|---|---|
| ● | Confidence: green for a placed part or a BOM that agrees with the pads, amber for one source, red for a land pattern alone. Its tooltip lists the evidence. |
| **Kind** | R, L, C, Short, Open, MultiPin, Connector, Ignore, or `?` &mdash; an unknown kind is generated as a capacitor. |
| **Connection** | Series, shunt, or why the part is left out (shorted, bridged, multi-pin). |
| **Case** | The case size the land pattern matches. The part is written with that footprint (`smt:0402@N`). |
| **Value** | With its unit (`10 pF`, `4.7 nH`, `49.9 Ohm`). Empty when unknown, showing the variable that stands for it; a value of the wrong dimension for the kind is outlined in red. |
| **Model** | Ideal, a two-port Touchstone file found in the workspace, or **Browse…**. |
| **Part number**, **X**, **Y** | From the BOM and the board. |

Click a column header to sort; type in the filter box to narrow the list. Selecting a row marks the part's
pads on the layout and brings it into view.

**A value that is not known becomes a global variable** named `<Refdes>_<Param>` &mdash; `C6_C`, `L2_L` &mdash;
with a tuning entry, so it is a slider in the **Tuning** panel and a variable in the **Optimizer** from the first
simulation. Its starting value is chosen to be transparent (a series capacitor 100 pF, a shunt one 0.01 pF), so
the first run shows the lines alone.

**A net you named in the layout keeps its name.** Give a trace a **Net** in its properties (`RFin`, `VCC`) and
the schematic's net there is called that, so the section is easy to find. Where lines divide one named trace into
several nodes, the first along the signal path takes the name and the rest are numbered: `RFin`, `RFin_2`,
`RFin_3`. A port on named copper takes the copper's name rather than `p1`.

Your edits are kept across every re-run the options cause. **Export Parts…** writes the table as CSV, with your
edits in it, and
**Import Parts…** reads an edited one back &mdash; the same file `circuitrf recognize --parts` reads, so a table
can be corrected in a spreadsheet or by an agent and brought back.

### Parts the Gerber files declare {#declared}

Gerber files written by a board tool often name, on every pad, the part and pin it belongs to (`C1`, pin `1`)
and the net it is on. When they do, each part is read from its own pads: the parts land on the board with
their designators, and a two-pad part is wired the way round the file says, pin 1 first. Kind comes from the
designator's prefix and value from a bill of materials, as for any other part. A connector pin the file names
&mdash; an SMA's `In` &mdash; becomes a port of that name; when two connectors share a pin name, each port is
named after its connector too (`J1.In`, `J3.In`). A placed footprint or a placement file still names a part
first, and where one disagrees with the pads, the report says so.

The nets the pads name are checked against the copper, never used in its place. Two named nets on one piece
of copper (a short) or one net on copper that does not join (an open) is a line in the report, with the
places; when they all agree, the report says that in one line.

### Designators on the silkscreen {#silkscreen}

When nothing else names a part &mdash; no placed footprint, no placement file &mdash; the designator printed
beside it on the silkscreen does. Text typed on the silkscreen, as a DXF drawing's `TEXT` and `MTEXT` arrive, is read
as it is. Stroked silkscreen text is read at any rotation, and mirrored on the bottom
side; each designator goes to the part it stands beside, even where it is printed a little nearer a
neighbour. Its prefix gives the part's kind (`C`, `L`, `R`, `FB`, &hellip;). Values are never read from the
silkscreen. Text drawn as filled outlines is not read.

A board drawn with no lands &mdash; a series part across a gap cut in a line, a shunt part from a line's edge to the
pour, as in many drawings of RF boards &mdash; still has each part's body on the silkscreen. A closed, part-sized
rectangle there whose two ends lie on two different pieces of copper is read as a two-pad part, its pads where its
ends meet the copper, and is named by the designator beside it. Its confidence is amber: the drawing says where it is,
nothing says what it is.

A board's font may draw a character unlike the built-in one, and then a designator is misread or not read at
all &mdash; the report lists lines that look like designators but have a glyph that fits two characters. Correct
the **Refdes** of a part named from the silkscreen, or of a made-up one beside such a line, and click **Learn
These Glyphs**: the glyphs you corrected are remembered as those characters for every later recognition, in
the dialog and from the command line alike. They are kept with your own settings, never in the workspace.

## What is recognised, and what is not {#recognised}

| On the board | In the schematic |
|---|---|
| A trace on microstrip | `MLIN` |
| &hellip; its corners, tees, crossings and linear width ramps | `MBEND` (with the nearest miter), `MTEE`, `MCROSS`, `MTAPER` |
| A trace with ground close on both sides | `CPWG` &mdash; see [the coplanar choice](#coplanar) |
| A trace between two planes | `SLIN`, its heights from the stackup |
| Any other cross-section | `TLIN` in its physical form, with Z0 and εeff from the cross-section solve; the report says why |
| A tee or crossing of `CPWG` lines, or of `CPWG` and `MLIN` | `MTEE` or `MCROSS` with the lines' widths &mdash; the nearest model, since there is no coplanar tee; the report says so |
| A corner on a `CPWG`, `SLIN` or `TLIN`, or a junction on an `SLIN` or `TLIN` | No discontinuity model: a corner is centre-line length, a junction a plain node |
| A junction of more than four lines | A plain node |
| A short piece between two lines | Merged into its neighbours; no line is ever dropped for being short |
| A two-pad part across a gap in a line | `R`, `L`, `C` or a two-port `SnP`, in series |
| A two-pad part from a line to ground | The same, shunt; where its pad stands on the line, the line is split there, and two parts either side of one point share that node |
| A pad where several lines and parts meet | One node: the pad is an ideal short |
| A via joining two signal layers | `VIA` |
| A via grounding a pad or a line end | `VIAGND` (or `GND`) |
| Stitching and plane-tie vias | Nothing &mdash; counted |
| A part with more than two pads, a connector | Cut out; its pads on RF lines become ports |
| Lines reaching no port and no part &mdash; traces to a pin of a part that is not modelled | Left out, and listed in the report |

A `TLIN` is drawn with its Z, length, εeff and reference frequency on the schematic.

Each line runs between the reference planes of the models either side of it: a part's pad edge, a via's land, half
a width from a bend's corner or a tee's centre. That is where the models stop, so nothing is counted twice.

Not modelled: coupled lines (a close parallel pair is reported as modelled uncoupled); step, gap and open-end
discontinuities (an open stub is an open line); case-size parasitics; devices with more than two pins; Klopfenstein
and other curved tapers (they become `MTAPER` or stepped lines); and the pads themselves &mdash; a line ends at the
pad's edge. Nothing is ever written to the layout.

## Microstrip or coplanar, and Swap Line Type {#coplanar}

A line with top-side ground beside it is grounded coplanar waveguide if the ground is close, and microstrip if it is
far &mdash; and a pour that merely creeps toward a line can make it read as coplanar. **Coplanar lines** decides:

| Choice | A line with ground on both sides is |
|---|---|
| **Auto** | `CPWG` when both gaps are within the factor's substrate heights (3 by default), else `MLIN` |
| **Microstrip** | Always `MLIN` |
| **GCPW** | `CPWG` whenever both gaps are measured |

Whichever you choose, every `MLIN` and `CPWG` records the two gaps measured beside it.

A line that came out as the wrong type is one [**Swap Line Type**](components.html#swap-line-type) away &mdash;
right-click it, or pick the type in its parameter editor. `MLIN`, `CPWG`, `SLIN` and `TLIN` swap in place, keeping
the width and the length; the impedance follows from the new type. A swap to `CPWG` takes the gap measured off the
board, a swap to `TLIN` computes Z0 and εeff at the analysis's top frequency, and a swap back restores what was set
aside. A bend, tee or taper beside a line swapped away from `MLIN` is noted, since their models are microstrip ones.

## A board with an IC on it {#ic}

An IC, or any part with more than two pads, is not modelled. Each of its pads that meets an RF line becomes a
**port**, so the schematic is the networks around the device as the device sees them &mdash; what matching work
needs. Pads on ground join ground; bias and control pads are left open and listed.

To bring the device in afterwards, import its symbol and footprint with
[**File ▸ Import ▸ Component…**](footprints.html#imported), place it, and wire it to those ports &mdash; or put an
S-parameter file of the device in its place.

From the command line it is two verbs: [`recognize`](cli.html#recognize) writes the networks around the device, and
[`import part`](cli.html#import) brings the device's footprint and symbol into the workspace as a cell, which you
then place in the schematic by writing it.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize Board --placement Board.pos --bom Board.csv --into new:Board_model
<span class="prompt">$ </span>circuitrf import part downloads/QFN-16.zip --into . --cell QFN-16</code></pre>

### Where the ports go {#ports}

Ports are found in this order: the ports of the layout's EM setup; then port labels and pins you placed on the
layout; then every line that reaches the board's edge, or a connector's pin where the board's files name one
(a pin or edge on copper that a via joins straight to ground and that the artwork names as ground &mdash; a
connector's shell tab, a ground strip at the edge &mdash; is never a port). To put a port where a connector lands &mdash; an SMA's centre
pin, say &mdash; place a port label on the copper there before you run the command.

## The report {#report}

After **Create**, the schematic opens focused and the report goes to **Messages**, one line per kind of finding
with its count: the ground chosen, vias dropped and kept, ports and where they came from, parts by source (including those from the pads the Gerber files declare, and
whether the nets they declare agree with the copper),
silkscreen text read and designators that named no part, unknown kinds and values, `TLIN` fallbacks by reason, coupled pairs, copper read as nothing, and lines left out because
they reach no port and no part. Last, the circuit is checked against the copper: every place where the copper joins
two part terminals, ports or vias and the circuit does not (or the other way round), and every terminal joined across
copper no line models, is a line naming both places; when they agree, the report says that in one line. A line about
places on the board has a toggle that expands it to them; **double-click** one to select and zoom the artwork
there. The same report is in the dialog's bottom strip while you work.

A recognition is never refused for being imperfect. It is refused only when the layout has no technology,
there is no copper in the scope, or no port could be found &mdash; and then the reason is in the dialog's status
line and the dialog stays open.

## Show in Artwork {#probe}

Right-click a component the command created and choose **Show in Artwork**: the layout it came from opens and
the component's copper is marked &mdash; a line's centre line, a part's pads. The schematic records which layout
it was created from, the scope and the options, so this works in later sessions too.

## Models existing artwork, and layout sync {#fromartwork}

Every component the command creates carries a **Models existing artwork** flag, shown as a check box in its
parameter editor. It means the copper already exists:

- **Update Layout from Schematic** into the layout it was recognised from leaves its artwork alone and counts it,
  so a schematic written into the artwork's own cell never has lines generated over the imported board. Into any
  other layout &mdash; the new cell's own &mdash; it is generated like any component, on the schematic's technology,
  so a round trip from the recognised schematic back to a layout is one command. Each component is placed where
  the board has it: a line from its first end toward its second, at its measured length, and a part centred on its
  pads and turned the way it lies on the board. Microstrip, grounded coplanar and stripline lines are all drawn; a
  grounded coplanar line has a ground strip as wide as the signal strip along each side, without stitching vias.
  Anything added to the schematic after it was recognised is laid out in rows below the board;
- **Update Schematic from Layout** does not place it a second time.

Clear the box to hand that component back to ordinary layout sync; it is one undo step and changes nothing else.

### Drawing the schematic {#drawing}

With **Arrange ▸ As on the board** (the default), each part, line and port is drawn roughly where it sits on the
board, turned the way it lies there, and the wires are routed between them. Parts keep their order across and down
the board, but long runs of line and empty board are drawn short, so the sheet stays compact. A part that would land on another is
drawn below the rest instead, and the report names it. A recognition of a selection is drawn relative to that
selection. **Update Schematic from Layout** on a layout of bare artwork draws the same way.

With **Tidy**, the schematic is laid out along the signal path, in the order the parts sit along the copper. With
**Settings ▸ Link symbol and footprint orientation** on (the default), a shunt part is drawn on the side of its line its
copper is on; off, every shunt part is drawn below its line. The same setting decides whether turning a symbol later
turns its footprint at the next **Update Layout from Schematic**.

Every net carries a label on one of its wires. To move a label, drag it: dropped on another wire of the same net it
moves there; dropped beside its wire it stays with that wire, at most one grid cell away; it cannot be dropped on
another net's wire.

**Create** shows its progress like the preview does. Right-click the progress bar, or press **Cancel**, to stop it;
nothing is written. When nothing has changed since the preview, Create uses what the preview already read rather than
reading the board again.

## From the command line {#headless}

[`circuitrf recognize`](cli.html#recognize) runs the same recognition with the same options, and an agent can
call it through the MCP `recognize` tool. With no output named it writes nothing and prints the report and the
parts table &mdash; review, edit the table, then write:

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf recognize Board --parts-out parts.csv
<span class="prompt">$ </span>circuitrf recognize Board --parts parts.csv --into new:Board_model</code></pre>

When an EM run of a whole board is refused for its size, the refusal names this command.
