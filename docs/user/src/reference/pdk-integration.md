---
title: PDK Integration
slug: reference/pdk-integration.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > PDK integration
lede: Importing a process design kit and simulating the parts it supplies.
keywords: PDK, process design kit, foundry kit, vendor kit, library import, design kit
---

A **kit** is a read-only tree supplied by a foundry or a device maker. It typically holds symbol
descriptions, netlists defining its parts, palette icons, model data files, and — usually in a *separate*
package beside it — the compiled model library those netlists name but never define.

circuitRF reads a kit **structurally**. It does not carry a list of who makes what; nothing about a kit's
identity is written into the product. Everything circuitRF knows about a kit it learned from the kit at
run time.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What a kit contributes</a></li>
<li><a href="#openpdk">OpenPDK</a></li>
<li><a href="#two-kinds">Two kinds of open kit</a></li>
<li><a href="#importing">Importing one</a></li>
<li><a href="#where">Where the parts go</a></li>
<li><a href="#models">How a kit's models are evaluated</a></li>
<li><a href="#failures">When something goes wrong</a></li>
<li><a href="#managing">Managing kits</a></li>
<li><a href="#vendor">Vendor part libraries</a></li>
</ol>
</nav>

## What a kit contributes {#what}

Four things, and they arrive by four different routes:

| | What it is |
|---|---|
| **Symbols** | Translated from the kit's own symbol descriptions into circuitRF symbols, in memory |
| **Models** | The device equations — usually a *compiled* library the kit names but ships separately |
| **Technology** | Layers, stackup and DRC rules, if the kit supplies them |
| **Artwork** | Layout for its parts, often as [PCells](pcells.html) generated from parameters |

<div class="callout note">
<span class="label">A kit part is an ordinary cell reference</span>
<p>This is the load-bearing decision of the whole area. A cell reference is <em>already</em> the
component whose artwork lives in an external file and resolves at render time — so placement, rendering,
pin geometry, hit-testing, net extraction and the symbol editor all work on a kit part unchanged. There
is no "external part" species, and there deliberately is not going to be one.</p>
</div>

Three things a kit does **not** tell circuitRF, which it therefore works out and records:

- **Which library implements its device types.** A delivery is often several kits sitting beside one
  shared library package, and no kit says which. circuitRF establishes it by scanning candidate libraries
  for the entry points it calls.
- **Which build of that library to use.** The same library name arrives in a dozen toolchain builds. The
  most specifically named build for your platform wins — and **the choice is reported**, because it was
  made automatically.
- **Which internal nodes of a model are not free unknowns.** Detectable structurally; *which* node each
  one follows is not derivable from anything the model reports, and is supplied as run-time data.

## OpenPDK {#openpdk}

**circuitRF supports the OpenPDK file structure** — <https://github.com/fossi-foundation/open-pdks>. If
you are building a kit yourself, or normalising a supplied one, that is the layout to follow: it is an
open standard, it is what circuitRF's importer expects to find, and it keeps a kit portable between
tools. [PDK Authoring](pdk-authoring.html) covers producing one.

## Two kinds of open kit {#two-kinds}

An open kit can arrive in two forms, and each supplies a different half of a design.

- **The kit's own tree** is a folder from the kit's publisher, imported as this page describes, with no
  Python needed. It gives the schematic side: parts with symbols, their models and corners.
- **A gdsfactory kit** is a Python package you install with `pip` into your own Python. It gives the
  layout side: parametric layout cells with their ports, and the kit's layer stack as a technology.

Import both and a layout cell declaring the same model as a part merges onto that part's palette tile.
[Using a gdsfactory kit](gdsfactory-kits.html) covers the second kind.

## Importing one {#importing}

**File ▸ Manage Libraries… ▸ Add…** (<kbd>Ctrl/⌘ P</kbd>), then pick the kit's folder.

<div class="callout note">
<span class="label">An import writes nothing into your workspace</span>
<p>No symbols are copied in, no parameter interfaces, no icons. The workspace records a
<strong>reference</strong> to the kit plus the decisions circuitRF made about it — the kit path, the
provider name, the resolved model-library path per platform, the chosen variant defaults, and a
translation version. Everything else is rebuilt in memory each time the workspace opens.</p>
<p>The principle is <em>persist what was decided, rebuild what was translated</em>. The decisions are
tiny and carry no geometry; they are the difference between a workspace that opens the same way twice
and one that quietly re-decides.</p>
</div>

### The import report

Every import ends in a report, success included — because the interesting result is almost never
all-or-nothing. A kit typically holds parts circuitRF can place, drawings it cannot yet read, and
files it does not recognise at all, and the difference is what you need to see.

{{ui: pdk-import-report}}

The report groups what it found into **read**, **recognised but not yet supported**, and **not
recognised**, and every finding carries the action that would close the gap. It is selectable text, so
the whole thing can be pasted into a note or a bug report.

<p class="small">The kit in that figure is invented for the documentation — real kits are licensed and
none ships with circuitRF — but the report and the dialog rendering it are the application's own.</p>

A vendor delivery is often several part kits beside one shared package holding the compiled models, and
discovery finds that package by **adjacency**. Once you reference a kit from somewhere else — a
workspace folder, most obviously — that adjacency is gone and nothing on disk can recover it. So
**Add…** also accepts a folder with no parts in it, when that folder holds the model libraries: adding
the package directly is how you restore the link.

## Where the parts go {#where}

Kit parts appear in the **Library palette**, ready to place. They do **not** appear in the Project Tree,
because nothing is on disk — they are the kit's cells, not yours.

Place one and it behaves like any other cell reference: it draws its symbol, it has pins you can wire
to, and its parameters are seeded from the interface the kit published. A part that ships several
formulations arrives on the one that works, so your first Run answers rather than explains.

A welcome side effect of parts being virtual rather than on disk: **placing one does not require a saved
schematic.** An ordinary cell reference is computed relative to the schematic's own folder, so it does;
a kit part has no such need.

## How a kit's models are evaluated {#models}

A kit's device models are usually **compiled code** — a shared library the vendor built, not equations
you can read. circuitRF runs them in a **separate worker process**.

That is a deliberate design, and it buys three things: a model that crashes takes the worker with it and
not your session; a model built for a different operating system can still be reached (below); and
circuitRF learns every device type, parameter name, pin count and node role **at run time** from the
library itself, rather than carrying a table that would go stale.

### What to expect

Nothing to configure. The path is *import kit → place part → set up an analysis → Run*:

1. **Import** reads the kit and, when the kit ships no provider manifest of its own — the ordinary case
   for an unmodified vendor kit — **synthesises one**, recording which library it found. It is written
   into your workspace as plain JSON, because anything chosen automatically should be visible and
   one line to correct.
2. **Placement** seeds the instance's parameters from the published interface.
3. **Run** starts the worker on demand. Opening a workspace starts no processes, and a kit your design
   never uses is never launched.

The first evaluation of a run is slower than the rest — the worker is starting, loading the library and
interrogating it. After that it is a pipe.

### Node roles are measured, not declared

Before a model is used, circuitRF **probes** it: it perturbs each node and compares the model's own
analytic Jacobian against finite differences of its own currents. From that it learns, per node, whether
the node is a free unknown, whether it is conductively coupled to its neighbours, and therefore whether
it is electrical or **thermal**.

<div class="callout warn">
<span class="label">A thermal terminal needs a reference, and a floating one has no solution</span>
<p>An unconnected ordinary pin is fine — it simply gets its own net. A <strong>thermal</strong> pin is
not: an open thermal terminal is a floating node fed by a constant current source, and it has no
operating point at all. circuitRF supplies the reference the design did not, so an unconnected thermal
pin does not stop a run. If you are modelling self-heating deliberately, build the thermal RC network
yourself, between the thermal node and an ambient source — the models do not build it for you, and a
non-zero thermal diagonal in the model is self-heating feedback, not a reference.</p>
</div>

### On macOS

Vendor model libraries usually ship Linux and Windows builds only. A Linux library cannot be loaded on
macOS at all — that is a binary-format and OS-ABI mismatch, not an instruction-set one, so nothing at the
library level bridges it. **circuitRF ships a small virtual machine** for exactly this, and starts it for
you; there is nothing to install. Expect a few seconds of startup on the first run of a session.

## When something goes wrong {#failures}

**A broken kit reference is a first-class, repairable state.** A workspace whose kits are missing still
opens; its kit parts draw as the "not found" placeholder. Your design is your data — a missing dependency
degrades, it does not deny.

Reporting is **one summary per kit, not one message per part** — forty parts must not produce forty
warnings. The details go to a log file in the workspace, and the Messages pane carries the summary with a
clickable path to it.

What to check first, in order:

1. **Is the kit still where the workspace thinks it is?** Manage Libraries… shows the stored path and whether
   it resolved. A kit is normally *outside* the workspace, so an absolute path is the common case and a
   moved kit is the common failure.
2. **Was the model library found?** The resolved library path per platform is recorded per kit. If the
   shared model package was never referenced — see [Importing one](#importing) — the parts will draw and
   place but will not simulate.
3. **Run `Validate PDK`** (below). It tells you whether the kit still holds the parts your design placed.
4. **Read the Messages pane for the worker's own words.** A refused evaluation usually explains itself
   only in the worker's own error output, and that output is attached to the failure.

A model that stamps cleanly but will not converge is worth a specific mention: every number is finite,
nothing errors, and the solve simply does not settle. That is the signature of a **misread node role** —
worth reporting with the kit's details rather than tuning tolerances against.

## Managing kits {#managing}

{{ui: manage-pdks}}


**File ▸ Manage Libraries…** (<kbd>Ctrl/⌘ P</kbd>) lists every library the workspace references — each
kit, and each [vendor part library](#vendor) — with its kind, its name, stored path, whether it resolved
and its part count.

| Action | What it does |
|---|---|
| **Add…** | Folder picker. The folder is classified by what it holds — readable SPICE subcircuits and no kit structure is a vendor library, anything else is imported as a kit — so you are never asked which it is |
| **Remove** | Drops the reference, after warning how many placed parts it will leave unresolved |
| **Reveal** | Opens the kit folder in your file manager |
| **Validate** | Runs the check below on the selected kit |

Every action reports in the dialog **and** posts to the Messages pane — the dialog gets dismissed, and
the record of what you added, removed or validated should outlive it.

### Validate PDK

Re-reads the kit and reports **drift**, not just breakage: a part your design placed that the kit no
longer offers, or a recorded translation version that no longer matches the reader.

It reports what it **checked** as well as what was wrong — parts offered, placed parts checked, problems,
notes. "No problems found" on its own cannot be told apart from a check that did nothing, and that is the
one thing a validation must never be ambiguous about. A kit that could not be read at all reports
*−1 parts offered* rather than 0, because "offers nothing" and "could not be read" are different answers
to different questions.

It checks your placed parts against a **fresh read of the kit**, never against whatever happens to be
loaded in this session. The question is whether the kit still holds the part — conflating that with
whether this session managed to load it turns *"your kit changed"* into *"something went wrong at
startup"*.

<div class="callout note">
<span class="label">Why a translation version exists</span>
<p>Symbol pins are snapped to the connection grid when a kit's symbols are translated. If that reader
ever changes — a scale fix, a snap fix, anything touching pin placement — re-deriving symbols would
<strong>move pins, and wires attached to them would silently disconnect</strong>. The recorded
translation version is what catches that: a mismatch is reported rather than re-derived underneath your
wiring.</p>
</div>

## Vendor part libraries {#vendor}

A component manufacturer's simulation library — a folder of plain-text SPICE files, one small
subcircuit per capacitor or inductor — can be referenced from a workspace and searched by value, size
and description.

**File ▸ Add Vendor Library…** asks for the library's folder, reads it, and shows what it found before
anything is added: the number of parts, the folders that group them, and any files it could not read.
The name it offers is the folder's own; placed parts reach the library by that name. The Messages pane
gets one line saying what was added.

<div class="callout note">
<span class="label">Referenced where it is, never copied</span>
<p>A vendor library stays in the folder you downloaded it to. The workspace records its name and path,
nothing else. To move it, add the folder again under the same name: every placed part resolves again
and no schematic changes.</p>
</div>

**Only the static, plain-text edition can be read.** Manufacturers publish their libraries in several
editions. The ones with bias- and temperature-dependent models are encrypted for one simulator each, and
circuitRF does not read them. A file like that is counted, not read, and the library says once how many
it found. A folder holding nothing readable is refused with the same sentence. If you need one bias or
temperature condition, export that condition from the manufacturer as a plain static model and place it
as an ordinary SpiceModel.

Each part is listed from the comment header above its subcircuit (description, value, frequency range,
temperature and bias). The circuit itself is read only when a run needs it. Indexing happens in the
background each time the workspace opens, so opening never waits for it.

**In the palette**, each library is a sub-menu of the [Type menu](schematic-editor.html#palette)
following its own folders, and choosing it lists its parts with value-aware search; see
[A vendor library's parts](schematic-editor.html#palette-vendor). **Add Vendor Library…** is at the
bottom of that menu too.

**Use in new workspaces.** Every library you add is remembered for you, and **File ▸ New Workspace**
references the ones ticked *New workspaces* in Manage Libraries. They are ticked by default. If a
workspace opens and one of its libraries is missing, the Messages pane says so once per library. When you
have that library somewhere else under the same name, the message offers **Repair**.

**Validate** on a vendor library re-reads it and reports parts indexed, files it could not read, placed
parts checked, and any placed part the library no longer holds.

### A placed part {#vendor-placed}

Selecting a placed vendor part shows its library and file, a **Part** field and the part's value,
frequency range and description in the Parameter Editor. The editor's title is the part number.

- **Choose another part** by typing in the Part field: it searches that file's parts with the
  palette's search, and <kbd>↓</kbd>/<kbd>↑</kbd> move through the matches while you type.
  <kbd>Enter</kbd> takes the highlighted one and <kbd>Esc</kbd> puts the placed part back. The
  <span class="ui">▾</span> button lists the whole file with the placed part highlighted.
- **Value ▼ / ▲** step to the next lower or higher value of the same kind: the same file and the same
  description except the value, so the size, dielectric and voltage stay as they are.

- **On the schematic**, double-click the value line (`C = 10 pF`). The value opens for editing with
  the parts of the same kind listed under it, in value order, and the placed part highlighted:
  <kbd>↓</kbd>/<kbd>↑</kbd> and <kbd>Enter</kbd> step the value, or type to search the whole file — a
  value, a part number or words of the description (`22p 100V`) — and take a match with
  <kbd>Enter</kbd> or a click. A plain number is in the value's own unit: `22` typed over `10 pF`
  finds 22 pF. <kbd>Esc</kbd> changes nothing.
  Double-clicking the part number does the same over the whole file.

Either way the instance keeps its place, wires and labels. The footprint follows the part only when it
came from the part: a footprint you chose yourself stays. One Undo puts both back. Selecting a part reads
nothing from the library file.

**The <span class="ui">?</span> button** opens the documents the library came with, if any, each in
your system's own viewer. Some editions ship nothing but model files, and publish their notes beside
the download instead. The menu then says the library came with no documents.

**A run reads only the placed parts.** Each part's circuit is read from its own few lines of its file,
so a run placing three parts from a 15 MB file reads three small blocks. A part whose library is
missing, or that the library no longer holds, is reported by name and left out of the run.

**Frequency range.** A part's model is valid over the range its library states. When a run's analyses
go outside it, the run says so once per part number: *"… is characterised from 100 MHz to 30 GHz; this
run reaches 1 MHz."* It is a warning in the Messages pane (on stderr from the command line); the run
still completes. A DC operating point never warns.

**Sharing.** **Archive Workspace** lists each referenced vendor library unticked, because a library is
usually licensed for your own use. The recipient adds their own copy. Ticked, the library is copied into
the archive and the archived workspace points at the copy.

### Letting the optimizer choose the part {#vendor-optimize}

A placed vendor part can be an optimizer variable. What varies is the **part** itself, so each candidate
is simulated with its own model, parasitics and self-resonance included. A real 10 pF part is not an
ideal 10 pF capacitor, and the difference between the two is often why you placed a real part.

Beside Value ▼ / ▲ in the Parameter Editor are the toggles every parameter row has.
<span class="ui">Tune</span> adds the part to the Tuning panel and <span class="ui">Optimize</span> adds it
to the Optimizer, each with the parts of the same kind as its candidates. Click a toggle again to remove
the part. It appears in those panels as `C1.Part`, showing its part number and value:

- **Candidates.** By default these are the parts of the same kind: the same file and the same
  description except the value. Size, dielectric, voltage and series stay as they are and only the value
  moves. **⋮ ▸ Search the library…** chooses them with the palette's search instead (`0603 C0G 50V`),
  which can cross series but keeps whatever the search names. The **min** and **max** under the slider
  bound the candidates' stated value (`1 pF` to `47 pF`), and the line below says how many candidates
  there are, their value span and the series they come from.
- **The Tuning slider** steps through the candidates in value order. The box takes a part number, or a
  value, which picks the nearest candidate.

When every optimized value is a vendor part, **Auto** searches the candidates directly with
**Discrete**, and the methods that move values continuously are greyed in the algorithm menu. With other
variables too, the optimizer first puts an **ideal** capacitor or inductor in the part's place and moves
its value continuously. It then tries the real candidates either side of that value, keeps the better
one, and polishes the other variables with it. The status line shows both costs, the cost with ideal
components and the cost with the real parts; the gap is what the parts' own parasitics do. A part with
no C or L glyph has no ideal stand-in and is always searched on its candidates.

**Push** writes the chosen part to the instance as choosing it in the Part field would. The footprint
follows only when it came from the part, and the whole Push is one Undo. A part is chosen, never given a
tolerance: a static model states none, so a Monte Carlo or yield run leaves it as placed. The
frequency-range warning is given once, for each chosen part whose range the analyses leave.

Headless, the same line is written `tune C1.Part kind=same min=1 pF max=47 pF opt=1`, or
`tune C1.Part parts="0603 C0G 50V" opt=1`. `circuitrf explain --tunables` lists each part with its
candidates, `circuitrf check` reports an entry with no candidate or too many, and `circuitrf opt`
reports the chosen part for each entry.

Headless, `circuitrf import library <folder> --into <workspace>` adds one,
`circuitrf find <workspace> --parts "<query>"` searches them, `circuitrf check <workspace>` reports a
missing library or a vanished part as an error, and `circuitrf explain <file> --ref vendorlib://…`
shows which workspace, library entry and folder a reference resolves through; see [CLI](cli.html).

## Authoring a kit {#authoring}

PCells can be authored in **Python** — the generator, its parameters, its pins and its
[parameter handles](pcells.html#handles) all in one function in one file. That, the OpenPDK layout, and a
complete worked example are in [PDK Authoring](pdk-authoring.html).

<div class="callout note">
<span class="label">Kits headless</span>
<p>A <a href="cli.html">command-line</a> run has no workspace to read the kit list from, so it takes the
folders directly: <code>--kits &lt;dir&gt;</code>, repeatable, valid on every verb. That is what makes a
design placing externally-supplied device models runnable in a script or a CI job.</p>
</div>

<p class="small">See also: <a href="pdk-authoring.html">PDK Authoring</a> ·
<a href="pcells.html">PCells</a> · <a href="components.html#veriloga">Compiled Verilog-A models</a> ·
<a href="layout-editor.html#technology">Technology</a> · <a href="cli.html">The command line</a>.</p>
