---
title: AN-02 — Importing a Gerber file set, and turning it into a schematic
slug: app-notes/an02-gerber-import.html
doc-kind: Application Note
breadcrumb: Docs > App Notes > AN-02 Gerber import to schematic
lede: A fabricator's Gerber and drill files brought into circuitRF as an editable board, its stackup checked, and its copper read back as a schematic that simulates without an EM run — walked through on the shipped Artwork to Schematic example.
keywords: Gerber, Excellon, drill, job file, gbrjob, X2, import, layer mapping, technology, stackup, FR-4, via, Create Schematic from Artwork, recognise, parts table, BOM, placement, tuning, convert, recognize, application note
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#board">The board</a></li>
<li><a href="#pick">Pointing at the files</a></li>
<li><a href="#identify">Which layer is which</a></li>
<li><a href="#mapping">The layer-mapping dialog and the Technology row</a></li>
<li><a href="#drill">Drill data and vias</a></li>
<li><a href="#result">What you get</a></li>
<li><a href="#stackup">Check the stackup before you trust a number</a></li>
<li><a href="#schematic">From artwork to a schematic</a></li>
<li><a href="#cli">The same thing from the command line</a></li>
<li><a href="#checklist">Checklist</a></li>
</ol>
</nav>

## The board {#board}

A small two-layer board, as a fabricator receives it. A 450 µm microstrip line enters at the left edge, turns up
through a 90° bend, passes a tee whose branch is an open stub, then a series 2.2 pF capacitor C1, a shunt inductor
L1 to a ground via, and a series 10 Ω resistor R1, and leaves at the top edge. The bottom layer is a solid ground
plane under a 254 µm laminate.

**Tools ▸ Examples ▸ Artwork to Schematic** gives you a copy. Its `fab` folder holds what left the design office:

| File | What it is |
|---|---|
| `fab/Board/Board.GTL` | Top copper: the line, the stub and the parts' pads |
| `fab/Board/Board.GBL` | Bottom copper: the ground plane |
| `fab/Board/Board.GTS` | Top solder mask: an opening over each pad |
| `fab/Board/Board.drl` | Excellon drill file: one 0.3 mm hole, the via |
| `fab/Board/Board.gbrjob` | Job file: names the three Gerber files and what each one is. It states no stackup. |
| `fab/Board.pos` | Placement file: where C1, L1 and R1 sit and how they are turned |
| `fab/Board-bom.csv` | Bill of materials: C1 and R1. **It has no row for L1, on purpose** — a real BOM is often incomplete. |

The workspace also holds **Board**, which is this same file set already imported with its stackup corrected, and
**Board design**, the schematic the board was drawn from. Keep Board design closed until
[the end](#schematic): it is the answer to compare against.

Every count, thickness and decibel on this page came out of a run on this example: the import and the recognition
the figures were captured from, and the command-line runs quoted in [the last section](#cli).

## Pointing at the files {#pick}

**File ▸ Import ▸ Gerber…** opens a file picker. Point it at a **folder** and you get the whole board. Pick one
file and circuitRF asks whether the folder was what you meant, and says what else is in it. Pick `Board.GTL`
alone and this is the question:

{{ui: an02-scope-prompt}}

One Gerber file is one layer, with no drill data, no other copper and no outline, which is occasionally what you
want and usually is not. **Whole Folder** is the default. **Another Folder…** opens the folder picker instead, and
**Cancel** creates nothing.

The counts in that question are made **by content, never by extension**: every file is opened and classified, by
the same classifier the import itself uses. A file in the folder that is neither artwork nor drill data is
skipped, and the summary names it, so a folder with other things in it is safe to point at. This one holds
nothing else.

## Which layer is which {#identify}

Each artwork file's identity comes from the strongest source the set offers, in this order:

1. the **job file** (`.gbrjob`), which settles which files belong to the board and what each one is, and can
   carry the stackup;
2. the file's own **X2 attributes** (`%TF.FileFunction`), which also give a copper layer's position in the stack;
3. a **Gerber suffix** in your workspace technology that matches the file's extension;
4. a **name heuristic** — copper top, bottom, inner or numbered from the top (`MET-1` … `MET-4`), mask, silk,
   paste, outline, drill;
5. the **layer-mapping dialog**, for anything still unnamed.

This board's job file names all three Gerber files, so the first rung settles every one of them. The files carry
X2 attributes as well, but the job file is asked first. The [import summary](#result) says so, file by file:

<pre>
Board.GBL → Bottom (declared by the job file): 1 flash(es), 0 stroke(s), 1 region(s).
Board.GTL → Top (declared by the job file): 2 flash(es), 0 stroke(s), 16 region(s).
Board.GTS → Soldermask Top (declared by the job file): 0 flash(es), 0 stroke(s), 6 region(s).
Imported 3 artwork file(s) as 3 layer(s) and 26 shape(s): 3 layer(s) identified exactly,
0 guessed from the file name, 0 mapped by hand.
</pre>

Read that last line on every import. **"guessed from the file name"** is the rung that can be confidently wrong:
a set with neither a job file nor X2 attributes lands there, and a name says what the person who chose it meant,
not what is in the file. The drill file is not in that count: Excellon is recognised
as drill data by what it contains, and is [read separately](#drill).

## The layer-mapping dialog and the Technology row {#mapping}

Every Gerber import opens the layer-mapping dialog. Its first row, **Technology**, decides which technology the
board lands in, and the table under it shows where each file goes in that technology.

{{ui: an02-layer-mapping}}

- **New technology from the files** writes a technology for this import alone, beside the cell, built from what
  the files say. Your workspace technology is read for layer names and never changed.
- **A technology you already have** — any `.ctech` in the workspace, or a built-in one — is **used**: every file is
  mapped onto that technology's own layers, nothing is added to it, nothing in it changes, and no `.ctech` is
  written. A built-in technology is copied into `tech/` when you press **Continue**.

Changing the row re-proposes the table for that technology at once, without reading the files again. A
technology is offered only when its stackup has as many conductors as the set has copper files, counting the
files you mark as copper in the **In the stackup as** column described below. The others are listed with
their copper count and cannot be chosen until the count matches. The dialog opens on the workspace's default technology when
its count matches, and on **New technology from the files** otherwise. This workspace's default,
`tech/board.ctech`, has two conductors, as the set has two copper files, so the dialog opens on it, and all three
files are matched by name.

**Use the technology you already have when it holds the fabricator's stackup.** That is the usual case for a board
from your own design flow, and for any board built on a process you have set up once. Choose **New** when nothing
in the workspace describes this board, and then [check what it guessed](#stackup) before you run anything on it.

The same row on a six-copper set, and its choices:

{{ui: gerber-import-technology}}

{{ui: gerber-import-technology-choices}}

**An import into a chosen technology is refused, and creates nothing**, when the set does not fit it:

- the set has a different number of copper files than the technology has conductors;
- a copper file lands on a conductor of a different rank — the file that says it is layer 2 on the technology's
  third conductor;
- two copper files land on one conductor;
- plated drill data lands on a layer no via in that stackup binds. A drill file that only routes — the board
  outline and its cutouts — or that says it is non-plated is not a via, and is kept as an unknown layer instead.

**A file nothing could identify is asked where it goes in the stackup.** Only a conductor enters the stackup, so
an unnamed file that is really a plane has to be told so, or it will be imported as drawing-only artwork and
nothing will treat it as copper. Its row gets an **In the stackup as** column: *artwork*, the default, or *copper*
at a position in the copper order.

{{ui: an02-layer-mapping-unidentified}}

The example board does not raise that question; the figure is a made-up set with one file that carries no
attribute and an extension no rung knows.

## Drill data and vias {#drill}

An Excellon file often does not say what its numbers mean. Units and zero suppression are inferred from the format
comment, the `INCH`/`METRIC` line, `M71`/`M72`, the tool diameters, and a cross-check of where the hits land
against the artwork's own extent. **You are asked only when that inference had to guess, or when the cross-check
disagrees**, and the question shows the evidence.

This board's drill file declares `METRIC` and writes every coordinate with a decimal point, so nothing is guessed
and nothing is asked:

<pre>
Board.drl: mm 3:3 decimal-point coordinates. Units: millimetre — declared by the file (its INCH/METRIC line).
Digit format: not needed — every coordinate carries a literal decimal point. Zero suppression: not applicable —
every coordinate carries a literal decimal point.
</pre>

A drill file that states none of that gets this, pre-filled with the inference:

{{ui: an02-drill-format}}

The made-up file behind that figure has one tool of 0.0118 and two hits, the first written `X394Y591`. The
inference reads the tool size as inches, defaults the digits to 2:4 with leading zeros suppressed, and then checks
the result against the artwork: both hits fall inside it. Correct whatever is wrong and press **Import**.
**Cancel cancels the whole import** — a board read at the wrong scale is worse than no board.

**Vias are rebuilt** wherever a drill hit and a copper flash share a coordinate: pad diameter, drill diameter,
barrel and landing layer, back to a real via. This board's one hole lands on its via pad:

<pre>
Drill: 1 tool(s), 1 hit(s) → 1 via(s) (0 declared as vias by the files themselves), 0 unpaired hole(s),
0 component hole(s), 0 slot(s).
</pre>

A hit with no flash under it stays a circle on the drill layer and is counted as *unpaired*; many of those
usually mean the artwork and the drill file are from different boards. Where the files declare a hole as a via or
a component hole (X2 `ViaDrill` / `ComponentDrill`) the declaration is used. These files do not, so the summary says
the via was rebuilt "without the file saying so": from artwork alone, a via and a plated component hole look the
same. What the via model needs from the stackup is in [Vias](../reference/vias.html#stackup).

## What you get {#result}

One **flat cell**, in a folder of its own at the top of the workspace, named after the folder the files were in.
This workspace already has a `Board`, so the import lands in **`Board_2`**: the cell `Board_2`, and with **New**,
the technology `Board_2.ctech` beside it. With a technology you chose instead, the layout references that file and
no technology is written.

The import's summary goes to the [Messages panel](../reference/workspace.html#panels), under the line that says
what was imported:

{{ui: an02-import-summary}}

Most of it is what the files said, file by file. Two paragraphs are not, and are written apart from the rest so
they can be found: the one beginning **NOT STATED BY ANY FILE IN THIS SET** lists every number the import made up,
and the plated-via paragraph names the wall thickness it defaulted. The last line is the one to take literally: a
whole board is not an EM problem. Crop the region you mean to simulate first, or read the board as a circuit,
[below](#schematic).

The cell opens in the layout editor:

{{ui: an02-imported-layout}}

Every shape that came in as a filled region is a polygon: Gerber has no rectangle, so a rectangle drawn in the
design that produced these files is a polygon here, and the summary counts them (23 on this board). The line
arrives as one polygon per stretch of copper, overlapping where the stretches meet:

{{ui: an02-imported-copper}}

The [round-trip table](../reference/layout-editor.html#gerber) lists what survives an export and import and what
does not. On a board whose pours arrive as thousands of painted strokes, the **Coalesce imported painted fill
into regions** setting on the [General page of Settings](../reference/settings.html#general) turns them back into the
regions they paint; it is on by default, and the summary names each layer it coalesced. With it off, the
summary's per-layer stroke counts show where a pour came in as strokes, and the layout editor's boolean
[**Union**](../reference/layout-editor.html#tools) turns them into copper you can edit and mesh.

## Check the stackup before you trust a number {#stackup}

**No Gerber file states the substrate.** The job file can, but this one does not, so under **New** the import
builds the stackup's structure from the files — two conductors, in the order the files declared, each bound to
its drawing layer — and fills in the rest with ordinary FR-4 so the board can be opened and looked at:

<pre>
NOT STATED BY ANY FILE IN THIS SET, so circuitRF filled it in with ordinary FR-4 so the technology can be
simulated as it stands: copper thickness on 2 conductor(s) (35 um outer, 18 um inner); thickness on 1
dielectric(s) (1778 um each, sharing out a 1778 um board); relative permittivity 4.4 on 1 dielectric(s); loss
tangent 0.02 on 1 dielectric(s).
</pre>

Open `Board_2.ctech` and its **Stackup** tab shows exactly that:

{{ui: an02-stackup-guessed}}

Every one of those numbers is wrong for this board. The fabricator's laminate is **254 µm, not 1.778 mm**, with
ε<sub>r</sub> **3.66**, not 4.4. On the guessed stackup the 450 µm line is a **115 Ω** microstrip; on the
fabricator's it is **54 Ω** (the MLIN model's static Z<sub>0</sub>, from the
[line calculator](../reference/cli.html#impedance-line) on each technology). **Nothing at all is marked as the ground
reference**, and the status line at
the foot of the tab says so: a microstrip component has no ground plane to resolve until one conductor is marked.

Type the fabricator's values in on the Stackup tab — the dielectric's thickness, ε<sub>r</sub> and tan δ, the
copper thickness if it is not 35 µm, and **Ground reference** on the bottom conductor. The example's `Board/Board.ctech` is this same import
with that done:

{{ui: an02-stackup-corrected}}

[The Stackup chapter](../reference/stackup.html#tab) describes every field on that tab, and
[its cross-section](../reference/stackup.html#cross-section) is the drawing above.

**Under a technology you chose, nothing is guessed.** The layout references `tech/board.ctech` and reads its stackup
— 254 µm, ε<sub>r</sub> 3.66, the bottom conductor the ground reference — and the summary's closing line says which
file it used:

<pre>
This import uses the technology 'round-trip' (tech/board.ctech): the layout references it, no technology was
written, and nothing in that one was changed.
</pre>

When the set's job file does state a stackup and it differs from the technology you chose, the summary warns you
and says how they differ, layer by layer; the technology is used as it is, and the warning is the prompt to find
out which of the two is right.

## From artwork to a schematic {#schematic}

The usual reason to import a board is to simulate it, and a whole board is too large for that as an EM problem:
the full-wave matrix grows as the square of the mesh, and a run whose matrix will not fit is refused before it
starts ([what makes a run infeasible](../reference/mom-engine.html#budget)). But a board of ordinary SMT parts joined by
controlled-impedance lines is already a circuit, and **Design ▸ Create Schematic from Artwork…** reads it out of
the copper as one: lines, bends, tees, vias and parts, as native components that run as an ordinary S-parameter
simulation. The steps below run on the example's **Board** cell, whose stackup is already corrected.

**1. Recognise.** Open Board's layout and choose **Design ▸ Create Schematic from Artwork…**
([opening it](../reference/artwork-to-schematic.html#open)). Recognition runs as the dialog opens. Give it the
[placement file and the BOM](../reference/artwork-to-schematic.html#companions): **Placement…** `fab/Board.pos`,
**BOM…** `fab/Board-bom.csv`.

{{ui: artwork-to-schematic-dialog}}

The report strip under the table says what was read: ground on the bottom plane, the via kept as a
[VIAGND](../reference/components.html#viagnd), two ports where the line leaves the board, seven
[MLIN](../reference/components.html#mlin)s, a [bend](../reference/components.html#mbend) and a
[tee](../reference/components.html#mtee), and one line end with nothing on it — the stub at (11550, 7450) µm.
[The report](../reference/artwork-to-schematic.html#report) says what each line of it means, and
[what is recognised](../reference/artwork-to-schematic.html#recognised) says what is not.

**2. Review the parts table.** C1 and R1 take their values from the BOM. L1's kind comes from its designator and
its case from the land pattern, but nothing states its value, so it becomes the global variable **`L1_L`**,
starting at 1 µH — a shunt inductor large enough to be invisible, so the first simulation shows the lines alone.
Select a row and its pads are marked on the layout:

{{ui: an02-parts-row-selected}}

[The parts table](../reference/artwork-to-schematic.html#parts) lists every column and what you can correct in
each.

**3. Create.** Leave the [target](../reference/artwork-to-schematic.html#target) at **New cell**, `Board_model`,
and click **Create**. The schematic is written beside the artwork cell and opens:

{{ui: an02-recognised-schematic}}

Every line length came off the copper — 7.6 mm into the bend, 4 mm to the tee, a 3.5 mm stub, and so on to the
4.5 mm that leaves the top edge — and every one equals its line in Board design.

**4. Simulate.** Press **Simulate**. At 2 GHz `Board_model` reads **S11 ≈ −7.7 dB**. Simulate **Board design**:
**−15.8 dB**. Every value in the two schematics agrees except one, and the difference is L1.

**5. Tune the unknown part.** Open the **Tuning** panel on `Board_model`. `L1_L` is listed already, its range
100 nH to 10 µH. Click the minimum and type `1 nH`, then drag the slider down
([a row of the Tuning panel](../reference/tuning.html#rows)). The match deepens as L1 falls, and at **8.2 nH** —
the value in Board design — the two curves lie on top of each other: −15.8 dB at 2 GHz, and a minimum of −15.9 dB
just below it.

{{ui: an02-tuning-l1}}

{{ui: an02-s11}}

**Then, while you are here:**

- **Show in Artwork**, on the right-click menu of any component of `Board_model`, marks the copper it was read
  from ([Show in Artwork](../reference/artwork-to-schematic.html#probe)). On the stub's line:

{{ui: an02-show-in-artwork}}

- **Swap Line Type** on one of the lines turns it into a [TLIN](../reference/components.html#tline), or a
  [CPWG](../reference/components.html#cpwg), and back, keeping its width and length
  ([swapping a line type](../reference/components.html#swap-line-type)).

A schematic created this way keeps a record of the layout it came from; what that means when the layout changes
is in [models existing artwork](../reference/artwork-to-schematic.html#fromartwork).

## The same thing from the command line {#cli}

Both halves have a [command-line](../reference/cli.html) form, and both run the same import and the same
recognition as the GUI. From the example's workspace folder:

<pre>
circuitrf convert fab/Board -o imported/ --to clay
</pre>

[`convert`](../reference/cli.html#convert) with a `clay` target is the Gerber import, writing into the folder you
name. With no workspace to open there is no default technology, so the import mints its own, as **New** does, and
says what it guessed on the same terms — here, from the run:

<pre>
note: Board.gbrjob names 3 file(s) as part of this board.
note: Copper stack order was DECLARED for all 2 copper layer(s): Top Copper, Bottom Copper, top to bottom.
note: Board.drl: 1 tool(s), 1 hit(s) → 1 via(s). No layer span was declared; the holes are treated as through-hole.
note: NOT STATED BY ANY FILE IN THIS SET, so circuitRF filled it in with ordinary FR-4 so the technology can be
simulated as it stands: copper thickness on 2 conductor(s) (35 um outer, 18 um inner); thickness on 1
dielectric(s) (1778 um each, sharing out a 1778 um board); …
note: Imported 3 artwork file(s) as 3 layer(s) and 26 shape(s): 3 layer(s) identified exactly, 0 guessed from
the file name, 0 mapped by hand.
[circuitRF] wrote 1 cell(s) and a technology to imported/
</pre>

The layers are called *Top Copper* and *Bottom Copper* here rather than *Top* and *Bottom*: in the GUI the
workspace technology lends its names to the New technology, and headless there is none to lend them.
**`--into-tech`** is the Technology row's other answer
([the technology, and why it matters here](../reference/cli.html#convert-tech)):

<pre>
circuitrf convert fab/Board -o imported-use/ --to clay --into-tech tech/board.ctech
</pre>

<pre>
note: This import uses the technology 'round-trip' (../tech/board.ctech): the layout references it, no
technology was written, and nothing in that one was changed.
</pre>

The path in that line is relative to the output folder, as it is relative to the workspace in the GUI.

[`recognize`](../reference/cli.html#recognize) reads the board as Create Schematic from Artwork does and writes
nothing unless asked:

<pre>
circuitrf recognize Board/Board --placement fab/Board.pos --bom fab/Board-bom.csv
</pre>

<pre>
Ground is the largest copper on the reference conductor 'Bottom' at (14000, 23425) µm, and everything joined to it.
1 via to ground — a ground pad's or a shorted line's — kept as VIAGND.
2 ports where a line reaches the board edge: P1, P2.
3 parts were named by the placement file on land patterns found on the board: C1, L1, R1.
1 part has no known value and becomes a variable with a transparent starting value: L1_L.
9 line elements read from the traces: 7 MLIN, 1 MBEND, 1 MTEE.
7 segments read as microstrip (MLIN) under Auto (both side gaps within 3·H).
1 line ends on nothing a part, a via or a port lands on, and is left open (there is no open-end model): (11550, 7450) µm.

Refdes,Kind,Connection,Case,Value,Variable,Model,ModelFile,PartNumber,X,Y,Evidence,Confidence,Notes
C1,C,series,0402,2.2 pF,,Ideal,,,7825 µm,11350 µm,refdes=placement;kind=refdes;case=land;value=bom,high,
L1,L,shunt,0402,,L1_L,Ideal,,,7425 µm,14025 µm,refdes=placement;kind=refdes;case=land,medium,
R1,R,series,0603,10 Ohm,,Ideal,,,7825 µm,17725 µm,refdes=placement;kind=refdes;case=land;value=bom,high,
</pre>

`--into new:Board_model` writes the schematic, as **Create** does. The parts table is the same CSV the dialog's
**Export Parts…** writes, so a table corrected in a spreadsheet goes back in with `--parts`
([from the command line](../reference/artwork-to-schematic.html#headless)).

## Checklist {#checklist}

| Check | Where it is reported |
|---|---|
| Every file you expected was imported, and none was skipped that should not have been | Import summary — the skipped files are named |
| No layer was **guessed from the file name**, or you have looked at each one that was | Import summary — the *Imported N artwork file(s)* line |
| The drill data agrees with the artwork, and the via count is what the board has | Import summary — the *Drill:* line; *unpaired* should be 0 |
| The technology is the one holding the fabricator's stackup, or you chose **New** knowing it guesses | The Technology row; the summary's closing line |
| Under **New**: every number in the *NOT STATED* paragraph replaced with the fabricator's | The Stackup tab |
| One conductor is marked as the ground reference | The Stackup tab — its status line says when none is |
| Every recognised part has a value, or a variable you mean to tune | The parts table, and the report's unknown-value line |
| One number from the recognised schematic agrees with something you trust | Your own simulation of the design it came from |
