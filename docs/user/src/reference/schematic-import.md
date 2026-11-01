---
title: Importing a schematic
slug: reference/schematic-import.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Importing a schematic
lede: A schematic drawn in another editor, read as a new cell with its own schematic, or dropped straight into one you have open — its resistors, capacitors and inductors as circuitRF's own parts, everything else as the source's own symbol, and its connectivity checked every time.
keywords: import schematic, schematic import, foreign schematic, kicad_sch, EDIF, EDIF 2.0.0, edf, edn, DSN, binary DSN, compound file, Import Schematic, drop schematic, symbol cell, parts folder, connectivity check, matched, ports, connector, net label, hierarchy, sheet, off-page connector, code page, import schematic verb
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#formats">What can be imported</a></li>
<li><a href="#ways-in">Three ways in</a></li>
<li><a href="#parts">What each part becomes</a></li>
<li><a href="#symbol-cells">Symbol cells, and why they do not simulate yet</a></li>
<li><a href="#ports">Ports</a></li>
<li><a href="#pages">Pages, sheets and the drawing</a></li>
<li><a href="#check">The connectivity check</a></li>
<li><a href="#refused">When a file is refused</a></li>
<li><a href="#cli">From the command line</a></li>
</ol>
</nav>

## What can be imported {#formats}

| Format | Files | What is read |
|---|---|---|
| `.kicad_sch` | `.kicad_sch` | the sheet and every sheet it places; the file carries its own symbols, so nothing else is looked up |
| EDIF 2.0.0 | `.edf`, `.edn`, `.edif` | the pages, the symbols, and the nets the file declares |
| binary `.DSN` design | `.dsn` | versions 2.0 and 3.2 of the compound-file design format, hierarchy included in 3.2, and the nets the file declares |

A file is recognised by what is in it, not by its name. A `.dsn` that is a kit symbol or a board-routing file is
not mistaken for a design.

## Three ways in {#ways-in}

**File ▸ Import ▸ Schematic…** makes a new cell. The dialog reads the file as soon as you pick it and shows:

| Field | |
|---|---|
| Source | the file, its format and version, and its pages or sheets |
| Into folder | where the cell goes; the workspace, or the folder you started from |
| Cell name | the file's name, until you change it |
| Code page | EDIF and binary `.DSN` only: how text that is not UTF-8 is decoded (1252 unless you say otherwise) |
| Parts | every part — **Ref**, **Symbol**, **Value**, what it **Becomes** — and a **Symbol cell** box to keep one as the source's symbol |
| Ports | each connector and global port the design draws, with a box to make it a circuitRF Port |

Below them is the schematic the import will write, and its connectivity verdict. Nothing is written until you press
**Import**.

**Drop the file onto an open schematic** and the circuit is built there, its top-left corner at the drop point, as
one undo step, with no dialog: every part goes where the table below sends it, and no Port is made. Instance names,
net labels and variables the schematic already uses are renamed. Any symbol cells are written beside that schematic's
cell. Save the schematic first: an unsaved one has nowhere to put them.

**Drop it onto a folder or the workspace in the project tree** and the dialog opens, importing into that folder.

A picture dropped on a schematic is placed as a picture. Only a schematic file becomes circuitry.

## What each part becomes {#parts}

A part becomes one of circuitRF's own components only where the source makes it certain:

| Becomes | When |
|---|---|
| **R**, **C**, **L** | a two-pin part whose symbol is named as one (`R`, `RES`, `RESISTOR`, `C`, `CAP`, `CAPACITOR`, `CP`, `CP1`, `L`, `IND`, `INDUCTOR`, alone or followed by `_` or a space and more) **and** whose reference starts with exactly that letter |
| **Bead** | a two-pin part whose reference starts with `FB` and whose symbol is named `FB`, `BEAD`, `FERRITE`, or `Ferrite_Bead` and more. Its value, such as `220R/1A`, is the impedance the bead is sold by and becomes **Rp**. L and Cp are not in the source, so until you set them the bead is just its Rdc |
| **Vdc** | a supply connector: a two-pin part with one pin named for ground and the other for a voltage (`5V`, `+12V`, `3V3`). It becomes a DC source of that voltage, its + on the voltage pin |
| R, C or L with its footprint | a symbol named after its land pattern, such as `R-0603` or `C-1210`; or an R, C or L whose footprint property names a chip land pattern, such as `RESC1005X038L` or `R_0603_1608Metric` |
| **ground** | a ground symbol, and any net named `GND`, `GROUND`, `EARTH` or `0` |

Everything else comes in as the source's own symbol: a **symbol cell**. A reference such as `RB1` is not `R`, so that
part stays a symbol cell even though its symbol is a resistor. To keep any R, L or C as its symbol, tick its
**Symbol cell** box.

**Values** are read as written — `10k`, `4k7`, `2R2`, `100nF`, `1.5 nH` — and a rating after a slash is ignored:
`100nF/10V` is 100 nF. A value that does not read as the part's quantity, a word or a bare number with no unit or
multiplier, becomes a global variable named `<Ref>_<parameter>` (`R1_R`), with the source's text in the import's
report. Its default is 1 kΩ, 1 nF or 1 nH; for a part marked not fitted it is an open — 1 GΩ, 0.01 pF or 1 H. Set the
variable to the value you meant.

**A footprint property's size is metric when it is an IPC-7351 name.** `RESC1005X038L` is a 1.0 × 0.5 mm resistor,
the **0402** case, and its last letter is the density level: the part's `Footprint` becomes `smt:0402@L`. Any text
before or after the name, such as a library prefix, is ignored.

**A supply rail is a net, never a source.** `+5V` stays a net named `+5V`; circuitRF adds no supply for it. Only a
connector whose own pins name the voltage and ground becomes a source.

## Symbol cells, and why they do not simulate yet {#symbol-cells}

Each distinct symbol becomes one cell, holding the source's drawing as its symbol, in a folder beside the new cell
named after the file: `amp.kicad_sch` puts them in `amp_parts/`. A part with several units, such as a dual op-amp,
becomes one cell per unit. Import the same file again, or a second design that uses the same symbol, and an identical
cell is reused; a different cell of the same name is refused, naming both.

**A symbol cell has no model.** It is the source's picture of a transistor or an IC with nothing behind its pins, so
`check` reports it and a simulation refuses to run until you give it one: a schematic of its own, an S-parameter file,
or a model. circuitRF does not invent electrical behaviour the source did not state.

**To give one a schematic**, add a schematic to its cell. A new schematic in a cell that has a symbol starts with one
Pin for each of the symbol's pins, its **Num** the pin's number and its **Name** the pin's name, so each symbol pin
already connects to its own Pin. Wire your model to those Pins, including the ground pin: an amplifier drawn with
in, GND and out needs all three. A cell whose Pins do not match its symbol's pins also refuses to run, and the
message says which Pins it needs.

**Test points are left out of a run.** A part whose reference starts with `TP`, and any other symbol cell with a single
pin, is drawn as usual with its **Disable** set to **Open**, so it neither needs a model nor takes part in a
simulation. The report lists them. Set Disable back to **None** on any you want to include.

## Ports {#ports}

Nothing becomes a circuitRF Port unless you say so, because which connector is an RF port is your decision. The
**Ports** list offers only connectors that can be one: a signal pin, and at most one other net off ground for its
reference. A header carrying several signals is not offered. Tick one in the list and:

- **a connector** becomes a Term where it was drawn — its first pin off ground the signal, the next its reference — and
  no symbol cell is made for it;
- **a net** gets a Term to ground beside the drawing.

Ports are numbered in the order the list shows them. With at least one, the import adds an S-parameter analysis, `SP1`,
from 100 MHz to 6 GHz, which you can change like any other. See [Pin, Port and Term](pins-ports-terms.html).

## Pages, sheets and the drawing {#pages}

- **Several pages** become one schematic, the pages laid side by side in order. An off-page connector becomes a net
  label of the same name, which is how the pages stay connected.
- **A hierarchical design** becomes one cell per distinct sheet, each with its schematic and a rectangle symbol whose
  pins are the sheet's ports, placed by its parent. A sheet used twice is one cell placed twice. Ground is shared by
  every cell; any other global net used on more than one sheet is not joined across cells, and the report names it.
- **The drawing is the source's.** Every part keeps its position and orientation and every wire its own segments. A
  design drawn on a finer grid than 100 mils keeps its points exactly, and the schematic's connection grid is set to
  match. A part whose pins sit at a different spacing from circuitRF's own symbol is joined to the source's wire with
  one short bend. Where circuitRF's symbol is the longer of the two, the stretch of wire its body now covers is
  removed, so the wire ends on the pin rather than inside the part.
- **Labels** go where the source shows them: a part's name where it shows the reference, and its value where it shows
  the value, each reading from the same end. A field the source hides is hidden. circuitRF's labels are always level, so
  a part whose source writes a label up the page — usually an upright resistor's value — keeps circuitRF's own layout.
- **Free text and lines** come across. Title blocks, page borders, pictures and simulation attachments do not; a
  footprint property is kept on the part.
- **Buses**: each member connects through its own label, as it does in the source. A bus that crosses into a sheet is
  not expanded, and the report lists it.

## The connectivity check {#check}

Every import compares the circuit it built with the source: which pins are joined to which. When the format declares
its nets (EDIF and the binary `.DSN`) it is compared with those as well as with the drawing.

**Matched** means every pin is joined to exactly the pins the source joins it to. Otherwise the report names the pins
— *joined in the source, split in the import*, or the other way round — and the cell is still written, so you can see
where. When a file's declared nets disagree with its own drawing, the report says that instead: the file contradicts
itself, and no import can settle which is right.

The report goes to Messages — the parts mapped, the cells written or reused, anything left out and why, and the
verdict. A drop reports in one line, with **Details…** for the rest.

## When a file is refused {#refused}

A refused import writes nothing. The reasons you may see:

- the cell name is taken, or not a valid name;
- a symbol cell of the same name already exists and differs;
- a binary `.DSN` design in a version circuitRF does not read, or one that draws nothing. **Export it as EDIF 2.0.0**
  from the editor that made it, and import that file instead;
- an EDIF file in a version other than 2.0.0.

## From the command line {#cli}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import schematic &lt;file&gt; --into &lt;workspace-or-dir&gt; [--cell N] [--port &lt;ref-or-net&gt;]...
                                  [--symbol-cell &lt;ref&gt;]... [--codepage N] [--list]</code></pre>

The same import the dialog runs, with the dialog's defaults: the cell is named after the file and no Port is made.
`--port` names each one, a connector's reference or a net's name, in port order; `--symbol-cell` keeps a part as its
symbol; `--list` prints the pages, the parts and the port candidates and writes nothing.

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import schematic matching.kicad_sch --into ~/designs/Amp --port J1 --port J2
<span class="output">/home/you/designs/Amp/matching
/home/you/designs/Amp/matching/schematic/matching.csch</span></code></pre>

The paths written are printed on stdout; the report goes to stderr. **It exits 1 when the connectivity check is not
matched**, with the cell written, so a script does not mistake a mismatch for success. See [the CLI](cli.html#import-schematic).
An agent connected over MCP imports with the `import` tool, `what=schematic`.
