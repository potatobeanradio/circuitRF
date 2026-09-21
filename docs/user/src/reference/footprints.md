---
title: Footprints
slug: reference/footprints.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Footprints
lede: How a part on the schematic gets real artwork: SMT case sizes, IPC-7351B density levels, the land pattern circuitRF generates for each, and what reaches the board.
keywords: footprint, land pattern, SMT, surface mount, case size, package, pad, pads, land, 0402, 0603, 0805, 1206, 0201, chip resistor, chip capacitor, tantalum, reverse geometry, IPC, IPC-7351, IPC-7351B, density level, courtyard, soldermask, silkscreen, refdes, reference designator, decal, metric twin, imperial
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">A footprint is a layout view of a cell</a></li>
<li><a href="#choose">Choosing one on a component</a></li>
<li><a href="#cases">The case sizes circuitRF generates</a></li>
<li><a href="#density">The three density levels</a></li>
<li><a href="#drawn">What gets drawn, and on which layers</a></li>
<li><a href="#board">Onto the board</a></li>
<li><a href="#layout">In the layout editor</a></li>
<li><a href="#imported">Imported parts, BOMs and part libraries</a></li>
<li><a href="#headless">Asking about footprints headless</a></li>
<li><a href="#limits">What this is not</a></li>
</ol>
</nav>

## A footprint is a layout view of a cell {#what}

There is no second kind of artifact here, no footprint library file and no registry to keep in step.
**A footprint is a layout view of a cell** — the same `.clay` the [Layout Editor](layout-editor.html)
edits and the same view an instance of any other cell resolves to. That one rule is why an imported
part shows up beside a built-in case size in the same picker with no extra step, and why a footprint
you drew by hand is a first-class choice rather than a workaround.

A component's `Footprint` parameter names one of three things:

| What you point at | Written as | Where the artwork comes from |
|---|---|---|
| A **built-in case size** | `smt:0402@N` | Generated on demand from the case table — it is not a file, and nothing ships a `.clay` per case |
| A **cell in this workspace** | a relative path, e.g. `parts/Widget9` | That cell's own layout view, including every part [`import part`](cli.html#import) has ever written here |
| **Your own `.clay`** | a relative path to the file | Exactly the artwork in it |

Anything that does not begin with `smt:` is a path, resolved against the document's own folder the
same way a cell reference is. There is no third case to learn.

<div class="callout note">
<span class="label">A built-in pattern is generated, not shipped</span>
<p>A stored <code>.clay</code> carries absolute layer keys, and the shipped technologies disagree
about every one of them — the same key that is Silk Top on the 2-layer stackup is Soldermask Top on
the 4-layer one. A shipped 0402 file would therefore put its silkscreen into a mask opening on the
wrong board and <em>nothing would say so</em>. A generated pattern resolves its layers by
<strong>role</strong> against whichever technology is in force, which is the only form that survives
contact with more than one board.</p>
</div>

## Choosing one on a component {#choose}

Open a component's **parameter editor** (double-click its body on the schematic). The **Footprint**
row is the last row, laid out like any other parameter: the picker in the value column, the density
beside it where a unit would go, and the checkbox in the display column.

| Control | What it does |
|---|---|
| **Footprint** picker | **None**, then every built-in case size, then every layout view of every cell in this workspace, then **Custom…** — which opens a file picker over `.clay`. One list; there is no separate place to look for an imported part. |
| **Density** | `M` / `N` / `L` for a built-in case, greyed out for anything else — an imported cell's density is whatever its author drew. See [below](#density). |
| The checkbox | Draws the footprint's case code on the schematic as a third label, after the parameters. Off by default, and nothing is drawn when the footprint is None whatever it says. |

**The label reads `0402`, not `smt:0402@N`.** The reference is machine spelling; a drawing gets the
case code, or a cell's folder name, or a custom file's name without its extension.

<div class="callout note">
<span class="label">The list is filtered by port count</span>
<p>A row is offered only when its pad count <em>equals</em> the component's port count, so the
built-in case sizes — which are all two-pad — do not appear at all on a four-port part, and a
nine-pad cell you imported appears only on a nine-port one. Offering the rest would be offering a
<a href="#board">refusal</a>: the pad-count contract would report it at Update Layout and place
nothing, which is a worse way to learn it.</p>
</div>

<div class="callout note">
<span class="label">A freshly placed R, C or L already has one</span>
<p>Place a resistor, capacitor, inductor or one of the two-element RL/RC/LC parts on a design whose
technology is a <strong>board</strong>, and it lands carrying <code>smt:0201@N</code> — the smallest
size a person still places by hand. Everything else lands with <strong>None</strong>: a three-element
branch, a PCell, an SnP, a source, a port and a placed cell are all things where guessing artwork
would be putting a package on a model.</p>
<p>It is applied <strong>at placement and never again</strong>. Change the workspace technology later
and your parts keep the footprints they were authored with — a default that followed the technology
around would be a design that changes when you open it somewhere else.</p>
</div>

**The simulator never sees it.** `Footprint` is artwork, not a value: `smt:0402@N` is not an
expression, and it is dropped before parameters are resolved, for every component kind alike. It
*is* written into the `.cnl` — so a headless Update Layout can still read it — and the engine
ignores it.

## The case sizes circuitRF generates {#cases}

Two-terminal chips, reverse-geometry chips, larger MLCC bodies and moulded tantalums. The body
dimensions read **along the termination axis first** — the direction the two lands are separated in
— so a reverse-geometry row's first number is its short side, which is exactly how the generator
uses it.

{{table: footprints}}

<div class="callout warn">
<span class="label">0201 imperial and 0201 metric are two different parts</span>
<p>Imperial <code>0201</code> is 0.60 &times; 0.30 mm. <em>Metric</em> <code>0201</code> is
0.25 &times; 0.125 mm, which is imperial <code>008004</code> — a factor of 2.4, and both codes are
real. Nothing about the mistake announces itself: it places, it renders, it exports, and the first
sign of trouble is a board. That is why every case row in every picker, tooltip and message reads
its metric twin and its millimetres, and why a bare four-digit token arriving from a BOM is
<a href="#imported">reported as ambiguous rather than guessed</a>.</p>
</div>

## The three density levels {#density}

IPC-7351B states the same land pattern at three land protrusions, and the density picker chooses
between them. The part is identical in all three; what changes is how far the copper reaches past
it.

{{ui: footprint-densities}}

| Level | Written | For |
|---|---|---|
| **M** — most | `smt:0805@M` | Wave solder, hand rework, high reliability. The largest fillet, the largest courtyard. |
| **N** — nominal | `smt:0805@N`, or `smt:0805` | General commercial reflow. **The default**, and what an omitted `@` means. |
| **L** — least | `smt:0805@L` | High density, fine pitch. The land barely clears the termination. |

One case at two densities is **two different cells**, not one cell with a setting — which is why a
stored reference spells the density out, and why re-pointing a part from `@N` to `@L` is the same
kind of edit as re-pointing it from 0402 to 0603.

**Chips below 1.6 mm get their own goals.** IPC splits the table at metric 1608 (imperial 0603) and
gives the smaller bodies much reduced fillet goals; circuitRF follows that split, and scales the
heel, side and courtyard figures with the toe at that size. Without it an 008004 would be handed a
0.35 mm toe on a 0.25 mm body — a land pattern almost four times the part, which places, renders,
exports and is wrong.

## What gets drawn, and on which layers {#drawn}

{{ui: footprint-case-sizes}}

A generated pattern is four things, each resolved to a layer **by role** — the technology's
`F.Cu` / `F.Mask` / `F.SilkS` / `F.CrtYd` interchange alias first, then a layer's stated purpose,
then its name:

| Role | What is drawn | If the technology declares no such layer |
|---|---|---|
| **Copper** | Two lands, each carrying its pin (`1`, `2`) | **Refused.** There is no land pattern without lands, and the reason is reported by name. |
| **Soldermask** | One opening per land, grown 50 µm per side | Omitted, with a note |
| **Silkscreen** | Two lines running the length of the body, clear of the mask openings | Omitted, with a note |
| **Courtyard / assembly** | The keepout rectangle around the lands *or* the body, whichever is larger | Omitted, with a note |

<div class="callout warn">
<span class="label">A missing layer is never replaced by a different one</span>
<p>A silkscreen outline drawn on Soldermask Top because the technology has no silk is worse than no
outline at all: the mask layer is manufacturing data, and a stray rectangle in it is a defect nobody
sees until fabrication. The same goes for the courtyard, which is <strong>never</strong> drawn on the
board outline — a courtyard rectangle on <code>Edge.Cuts</code> is a routed slot. Every shipped board
technology declares <code>Courtyard Top</code> (<code>F.CrtYd</code>); a technology that does not —
one you wrote, or one imported from a Gerber set — omits the courtyard and says so in Messages, at
<strong>Info</strong> rather than Warning, because a courtyard is placement metadata that never
reaches the fabricated board. A missing soldermask or silkscreen is a warning, because both do.</p>
</div>

**A pattern is centred on its body, not on pin 1.** That is the opposite of the convention the
microstrip PCells follow, and it is deliberate: every board format places a part by its body centre,
so an imported cell's origin is already there, and one picker listing both would otherwise make a
part jump when you changed its footprint.

**The origin of the numbers.** Spans and gaps are computed from the case's worst-case material
condition plus the level's fillet goals, in exact decimal millimetres, and converted to DBU once —
so the same case lands on the same DBU on every machine. IPC's statistical fabrication-and-placement
allowance is **not** applied: it needs the board's fabrication tolerance and the assembler's
placement accuracy, and a technology declares neither. What you get is the geometric land pattern.

<div class="callout note">
<span class="label">A chip land pattern needs a board</span>
<p>The copper role must be a surface a part can be soldered to — a conductor sitting directly on a
solid dielectric. A MMIC technology whose topmost metal is an air-bridge level is refused by name
rather than quietly given an 0402 on a GaAs die. This is also the test behind the 0201 default: a
default is offered exactly where a land pattern can be generated.</p>
</div>

## Onto the board {#board}

**Design ▸ Update Layout from Schematic** (<kbd>⌘U</kbd>) is what turns the stated footprint into
placed artwork. A stated footprint is consulted **first**, ahead of the kit / cell-reference / PCell
chain, because it is an explicit choice somebody made.

- **Re-pointing updates in place.** Change a part from 0402 to 0603, run it again, and the existing
  instance moves to the new cell and is reported. It does not add a second one.
- **Setting a footprint back to None removes the artwork**, and says so. Leaving it behind would mean
  the layout says a part is there and the schematic says it is not.
- **Pads must equal ports.** A footprint with two pads under a four-port part is reported and
  **nothing is placed** — `S4P1 states footprint 0402, which has 2 pads, and the component has 4
  ports.` Artwork on the board with the wrong pad count is artwork somebody routes to.
- **Each placement draws its reference designator** on silkscreen, taken from the schematic instance
  name. It is derived, not stored, so a rename in the schematic arrives with the next update and a
  board can never disagree with the drawing about what a part is called.

<div class="callout warn">
<span class="label">An SnP with a reference pin has one more port than its file</span>
<p>A 2-port S2P with <strong>External reference pin</strong> ticked is a <em>three</em>-port
component, and it does not fit a two-pad chip land. This is the pad-count refusal people meet first,
and the message names both numbers.</p>
</div>

Everything else about Update Layout — packing, the ratsnest, what is skipped and what is reported —
is unchanged and is described in
[Schematic ⇄ layout](layout-editor.html#schematic-flow).

## In the layout editor {#layout}

Two gestures, and they are different things:

| Gesture | Where | What it does |
|---|---|---|
| **Place one by hand** | The **Footprint…** button on the toolbar, beside **Instance…** | Arms the placement ghost with a land pattern, so you can drop an 0402 onto existing copper with no schematic anywhere. |
| **Re-point what is selected** | The **Footprint** row in the Properties inspector, with its own density picker | Swaps the selected instance's artwork, as one undoable command like any other instance edit. |

A part placed by hand corresponds to no schematic component, so it carries no schematic id — and it
is given no invented one. It takes its designator from **Designator** in the same inspector, where
you can also hide it, drag its label and **Reset** the label back to its automatic position above the
body.

**Neither gesture writes back to the schematic.** Re-pointing artwork in the layout makes the two
views disagree, which is what Update Layout exists to reconcile and what its change report exists to
say. A silent write-back would make an artwork edit change a simulation.

## Imported parts, BOMs and part libraries {#imported}

- **An imported part is already in the picker.** [`import part`](cli.html#import) — and **File ▸ Import ▸
  Component…** — writes a cell folder holding a symbol plus one or more land patterns as
  sibling layout views. Because a footprint *is* a layout view of a cell, that part appears in the
  workspace section of every footprint picker with no second import and no registration. Density
  variants an import wrote (`-M`, `-L`) are listed as what they are, and a view that is not its
  cell's **primary** is listed as that — an instance draws the primary, so choosing one says so in
  its tooltip and Update Layout repeats it rather than placing the wrong artwork.
- **A BOM's footprint column is read and reported, never applied.** `SM/C_0402`, `C0402`,
  `CAP-0402-X7R` and `0402` all reduce to the same case; a token that reduces to nothing is shown
  **exactly as written** rather than snapped to the nearest code, and a bare token that names a real
  case in *both* schemes is reported as ambiguous with both readings named. A column that silently
  set artwork would be the same class of error as the ambiguity itself.
- **A railRF part library** (`.crlib`) has a footprint column that is a picker over the same case
  table, and it stores the scheme (`smt:0402`) because a bare token is ambiguous. It stores no
  density: an IPC level belongs to a land pattern, not to a package.
- **Exports carry it.** **File ▸ Export ▸ Bill of materials…** writes one row per placement with its
  reference, value and footprint; a board export writes each placement's designator as its
  reference.

## Asking about footprints headless {#headless}

Two verbs answer without opening anything —
see the [CLI chapter](cli.html#explain-footprints) for the full output:

```
circuitrf explain Board1/schematic/Board1.csch --footprints
circuitrf check Board1
```

`explain --footprints` prints, per component, what it states, what that resolved to, how it got
there, and **pads against ports on one line** with `MISMATCH` where they differ. `check` reports a
footprint that no longer resolves and a pad/port disagreement as warnings, on the design as a whole.

## What this is not {#limits}

- **Not a footprint editor.** A land pattern is a `.clay`, and the layout editor already edits those.
  **Custom…** means *point at one*, never *draw one here*.
- **Not a parts database.** The picker lists case sizes and the cells already in your workspace. It
  knows nothing about part numbers, manufacturers, stock, or a vendor's recommended pattern.
- **Not multi-pin packages.** The generator draws two-terminal chips and moulded tantalums. A QFN or
  a SOIC is a different land-pattern problem; import one, or draw it, and it is a first-class choice
  in the same picker.
- **No parameter handles.** A case size is a discrete choice from a table, not a dimension to drag —
  a grip would let you pull an 0402 into a shape no case code names, and the stored reference would
  then lie about what the artwork is.
- **Not LVS, and not a router.** Nothing here checks connectivity, and Update Layout places parts
  without routing them.
