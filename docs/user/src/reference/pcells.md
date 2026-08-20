---
title: PCells
slug: reference/pcells.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > PCells
lede: Parameterised cells: what they are, placing one, driving it, and editing it by its handles.
---

## What a PCell is {#what}

A **PCell** — a *parameterised cell* — is a cell whose layout artwork is **generated from its
parameters** rather than stored as a file.

The gap it closes is easy to state. A microstrip line's artwork depends on its width and its length, and
no stored file can express *"the width is the W parameter"*. Draw a 200 µm × 40 µm rectangle and you
have one microstrip line; declare a PCell and you have every microstrip line.

<div class="callout note">
<span class="label">A PCell is an ordinary cell in every other respect</span>
<p>It has a cell folder, a parameter list, a symbol and a project-tree node. Hierarchy, instances,
arrays, push-in navigation and the geometry cache all work on it unchanged. <strong>Exactly one thing
differs:</strong> when the editor asks the cell for its layout view, the answer is "this cell's layout is
generated" rather than the name of a file. That is the whole of the difference, and it is what keeps
PCells cheap — a PCell that were a <em>new kind of thing</em> would need parallel handling everywhere.</p>
</div>

## Why a parameterised cell beats a fixed one {#why}

Three things follow from generating rather than storing, and they are the reasons to prefer a PCell
whenever the geometry is a function of numbers:

- **One cell covers the whole family.** Fifty microstrip lines of fifty different lengths are fifty
  placements of one cell, not fifty cells.
- **The schematic and the layout cannot drift.** The generator reads *the same parameter list the symbol
  displays* — one list, not two. A symbol showing `W` and a generator reading a different `W` is a defect
  that only ever surfaces as wrong artwork.
- **A retarget is a re-run.** Change the technology and every placement regenerates against the new
  stackup and layer table. Stored artwork would have to be redrawn.

There is a cost, and it is the honest one: **generated artwork is read-only.** You cannot nudge a vertex
of a PCell's output, because the next regeneration would discard the nudge with nothing to say about it.
What you can do instead is drag its **parameter handles** — see below — or, when you genuinely want to
hand-edit, **Flatten Hierarchy** to turn the instance into ordinary geometry and give up being
parametric.

## Placing one, and driving it {#placing}

A PCell is placed like any other cell — from the Library palette, or by
[Update Layout from Schematic](layout-editor.html#schematic-flow), which resolves a parametric cell just
as it resolves a stored one.

To change it, edit its parameters: select the instance and use the Properties Inspector, or
double-click it for the parameter dialog. Type a new value and the artwork regenerates.

Parameters are **kinded values** — Real, Int, Bool or String — not bare numbers. That matters because a
real cell names a model, counts fingers and picks a display mode, and forcing each of those through as a
number the generator decodes by private convention is how kits become fragile. A count that arrives as
`3.0000000000000004` either gets rounded by a rule you cannot see or produces geometry nobody asked for.

**Length parameters are stored in SI metres**, and the conversion to database units happens in exactly
one place with one documented rounding rule — so two generators can never disagree about where a 2.9 mm
edge lands. You still type them with unit suffixes as everywhere else (`2.9mm`, `115mil`, `50u`); see
[Units](units.html#typing).

<div class="callout warn">
<span class="label">A kit's parameters are often declared as text</span>
<p>Vendor PCells commonly declare every parameter as a <strong>string</strong>, and a numeric value
supplied where a string is expected can be <em>silently ignored</em> — the generator falls back to its
default. The symptom is not an error message: it is <strong>wrong artwork</strong>. If a parameter change
appears to have done nothing, look at the geometry it produced before looking anywhere else. See
<a href="pdk-authoring.html">PDK Authoring</a>.</p>
</div>

## The worked example: MLIN {#mlin}

MLIN — a straight microstrip line — is the simplest PCell that is still a real one, and it is the
example the whole contract is written around.

**Place it.** Drop MLIN into a layout. It resolves against the layout's technology, which supplies the
signal layer it draws on and the substrate underneath.

**Set W and L.** Two parameters. `W` is the trace width, `L` its length. Type `W = 40u`, `L = 200u`.

**The artwork regenerates.** One rectangle on the signal layer, running from the origin along +X,
centred on `y = 0`, with a connection pin at each end.

That last sentence is not a description of MLIN in particular — it is the **origin and orientation
rule** every PCell follows:

> **Pin 1 sits at the cell origin, and the cell's principal axis runs along +X.**

For a line, the origin is the input end and the line runs right. For a bend, it is the input arm. For a
symmetric junction, it is the centre with arm 1 along +X. The rule exists because otherwise every author
chooses differently and nothing abuts.

### Pins are part of the output, not inferred from it

A PCell returns **pins alongside shapes**, because geometry alone cannot be connected. Each pin carries:

| Field | Why it is needed |
|---|---|
| **Name** | It must match the symbol's pin, or the schematic and the layout disagree about connectivity |
| **Location** | Where it sits, in the cell's own coordinates |
| **Layer** | Which conductor it lands on |
| **Width** and **outward direction** | A microstrip connection is an **edge**, not a point — and a bend has to know which way its arm faces |

That last row is the one most easily left out and the most expensive to add later. It is also what makes
a PCell's pins snappable: they are the highest-priority
[geometry-snap](layout-editor.html#geometry-snap) feature there is.

## Parameter handles — dragging *is* editing a parameter {#handles}

A layout-driven user thinks *"this trace needs to reach that pad"*, not *"L is 3.4 mm"*. Every other
primitive in the editor answers the first sentence directly: grab an edge, drag it, watch the number
follow. **Parameter handles** give a PCell the same gesture without giving up being parametric.

Select a single PCell instance and its handles appear on the artwork. Drag one, and the parameter that
produced that piece of geometry changes; the cell regenerates around it.

MLIN declares two:

- the **far end**, travelling along +X — that grip *is* `L`;
- the **top edge** at mid-length, travelling along +Y — that grip *is* `W`.

| | |
|---|---|
| **When shown** | A single selected instance whose cell is PCell-backed and whose generator declares handles. Never on a multi-selection. |
| **Readout** | `Label = value`, live, in the document's display unit. This is the part you actually read while dragging. |
| **Snap** | The projected point snaps to the layout's grid **in world space** — you are aligning to the grid you can see. <kbd>Alt</kbd> suspends it, as everywhere. |
| **Escape** | Cancels. Nothing is committed and no undo entry is left. |
| **Commit** | One undo entry per drag, however many pointer moves it took. |

<div class="callout note">
<span class="label">Parameter grips are not geometry handles, and they do not look like them</span>
<p>A geometry handle edits a coordinate; a parameter grip edits a <em>number</em> and the artwork is
rebuilt around it. Confusing the two is surprising in a way that is hard to undo, so a grip is drawn in
its own colour, with a hollow centre and a dashed axis hint showing which way it travels. A shape shows
geometry handles; an instance shows parameter grips; the two sets are never on screen at once.</p>
</div>

**A PCell that declares no handles behaves exactly as it always did** — the Properties Inspector stays
the primary surface, and no existing cell in any kit has to be revisited to keep working.

### What handles are not

They are not an inverse function. Nothing asks a generator to read its own output back and work out what
parameters produced it. **The generator declares what is editable; the editor never guesses.** An editor
that guessed would have to answer "the user moved this edge — which parameter did they mean?" from
geometry alone, and for anything more complex than a straight line that guess is wrong often enough to
matter. The failure mode would be the worst kind: the artwork regenerates, renders perfectly, and one
parameter is now a value you never chose, with nothing on screen saying so.

## When the technology changes underneath {#retarget}

A PCell reads the resolved technology as an input, alongside its parameters. Retarget the layout to a
different technology and every PCell placement regenerates against the new layer table and the new
substrate — a 50 Ω line on FR-4 becomes whatever that width means on the new stack, and the artwork
lands on the new technology's signal layer.

Two things follow that are worth knowing:

- **Geometry is cached per unique parameter set, not per placement.** A 50 × 50 array of one PCell
  evaluates the generator once and draws 2,500 times.
- **That cache is keyed on (cell, parameter values, technology).** It is the reason a generator is
  required to be **deterministic**: the same inputs must produce byte-identical output, on any machine,
  in any process, at any time. A generator that consults the clock, a random number, or ambient state
  breaks the cache *silently* — stale or inconsistent geometry, no error anywhere, and no way to tell
  from the result that anything went wrong. Two people on two machines getting different artwork is the
  same failure seen from the other end.

## Out-of-range parameters {#validity}

A PCell may **report** a parameter outside its published validity range — once per distinct violation,
naming the parameter and the bound. It is a report, not a refusal: the formula still evaluates. That is
deliberate, and it matches how the microstrip models behave. The thing worth avoiding is *silent*
extrapolation, not extrapolation.

<p class="small">See also: <a href="layout-editor.html">The Layout Editor</a> ·
<a href="pdk-authoring.html">PDK Authoring</a> (writing your own, in Python) ·
<a href="pdk-integration.html">PDK Integration</a> · <a href="units.html">Units</a> ·
<a href="components.html#mlin">MLIN in the component reference</a>.</p>
