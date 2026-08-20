---
title: PDK Integration
slug: reference/pdk-integration.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > PDK integration
lede: Importing a process design kit and simulating the parts it supplies.
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
<li><a href="#importing">Importing one</a></li>
<li><a href="#where">Where the parts go</a></li>
<li><a href="#models">How a kit's models are evaluated</a></li>
<li><a href="#failures">When something goes wrong</a></li>
<li><a href="#managing">Managing kits</a></li>
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

## Importing one {#importing}

**File ▸ Manage PDKs… ▸ Add…** (<kbd>Ctrl/⌘ P</kbd>), then pick the kit's folder.

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

1. **Is the kit still where the workspace thinks it is?** Manage PDKs… shows the stored path and whether
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

**File ▸ Manage PDKs…** (<kbd>Ctrl/⌘ P</kbd>) lists every referenced kit with its name, stored path,
whether it resolved, its part count and its translation version.

| Action | What it does |
|---|---|
| **Add…** | Folder picker; imports and adds the reference through the ordinary import path |
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

## Authoring a kit {#authoring}

PCells can be authored in **Python** — the generator, its parameters, its pins and its
[parameter handles](pcells.html#handles) all in one function in one file. That, the OpenPDK layout, and a
complete worked example are in [PDK Authoring](pdk-authoring.html).

<p class="small">See also: <a href="pdk-authoring.html">PDK Authoring</a> ·
<a href="pcells.html">PCells</a> · <a href="components.html#veriloga">Compiled Verilog-A models</a> ·
<a href="layout-editor.html#technology">Technology</a>.</p>
