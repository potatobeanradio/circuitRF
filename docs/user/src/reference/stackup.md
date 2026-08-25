---
title: The Stackup
slug: reference/stackup.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Stackup
lede: The layers your board is actually made of — and, because it decides the ground plane, where the negative terminal of every EM port is.
---

Your layout is a set of flat shapes. The **stackup** is what turns those shapes into a physical
structure: what each conductor is made of, how thick it is, what dielectric separates it from the
next one, and — the part that catches people — **which conductor is ground**.

That last one is not a detail. An EM port has two terminals, and only one of them is anywhere near
your artwork: the other is the ground plane. **You do not set it per port. The stackup does, for
every port in the run at once.**

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#where">Where the stackup lives</a></li>
<li><a href="#anatomy">Anatomy of a stackup</a></li>
<li><a href="#ground">Where your port's negative terminal is</a></li>
<li><a href="#levels">Which conductors get simulated</a></li>
<li><a href="#slab">The slab, and what makes one solvable</a></li>
<li><a href="#vias">Vias</a></li>
<li><a href="#ignored">What the EM engine does not read</a></li>
<li><a href="#check">Checking what it resolved to</a></li>
</ol>
</nav>

## Where the stackup lives {#where}

The stackup is part of a **technology** — a `.ctech` file in the workspace's `tech/` folder, edited
in the technology editor's **Stackup** tab. It belongs to the technology rather than to a layout,
because it describes a *process*: every board built on that process has the same layers, whatever is
drawn on them.

A layout names a technology (or leaves the name blank to mean the workspace default). Everything
else — the layer table, the DRC rules, the default display unit — lives in the same file. See
[The Layout Editor](layout-editor.html#technology) for the rest of it.

**A missing technology does not block drawing, but it does block an EM run.** Layers fall back to a
generated palette and editing carries on; the EM run refuses rather than inventing a stackup, because
there is no honest default for "how far above the ground plane is this trace".

## Anatomy of a stackup {#anatomy}

A stackup is an ordered list, **top to bottom**, of three kinds of entry, plus two boundary
conditions for what is above and below the whole sandwich.

{{ui: stackup-mmic}}

| Entry kind | What it is | What it carries |
|---|---|---|
| **Conductor** | A metal layer | Thickness, conductivity σ, the **drawing layers** that map onto it, and whether it is the **ground reference** |
| **Dielectric** | The material between two conductors | Thickness, ε<sub>r</sub>, tanδ, µ<sub>r</sub> |
| **Via** | A connection *between* two conductors — not a layer of the sandwich | The drawing layer via shapes are drawn on, the two conductors it **spans by name**, and its fill (plated or solid) with a wall thickness |

The two boundary conditions are properties of the stack as a whole:

- **Top** — `Open` (free space above, the usual case) or `Ground`.
- **Bottom** — `Ground` (the usual case) or `Open`.

<div class="callout note">
<span class="label">A drawing layer is not a conductor</span>
<p>What you draw on is a <em>drawing layer</em> — a GDSII layer/datatype pair with a name and a
colour. What gets simulated is a <em>stackup conductor</em>. The link between them is the conductor
entry's <strong>drawing layers</strong> list, and a shape on a layer that is bound to nothing is
simply not part of the EM problem. If a run tells you it found "nothing on a layer bound to a signal
conductor", that binding is what is missing — not the artwork.</p>
</div>

## Where your port's negative terminal is {#ground}

**Every port in an EM run returns through one plane, and the stackup picks it.** Not the panel, not
the port label, not per port.

The rule, in order:

1. **The ground-designated conductor.** Mark a conductor entry as the **ground reference** in the
   technology editor. The plane is the **top surface of the highest ground-designated conductor that
   lies below the signal level being simulated**.
2. **Failing that, the stack's bottom.** If no conductor is marked and **Bottom = Ground**, the plane
   is taken at the bottom of the stack. The run says so in its notes, and asks you to mark one
   instead — this fallback places the plane by boundary condition rather than by a real conductor's
   surface, which is a different height by the thickness of whatever is down there.
3. **Failing both, the run is refused.** No ground reference and no grounded bottom means no second
   terminal for any port, and there is nothing to solve.

Three consequences worth having in mind:

- **The plane is modelled as laterally infinite.** It is the boundary condition the Green's function
  handles analytically, not a meshed pour — so a ground *pour* drawn as artwork is not it, and a
  finite plane's edges are not modelled. A conductor marked as the ground reference is not meshed and
  cannot also be an analysis level; naming one as a level is refused.
- **An intervening metal layer does not become ground just by being underneath.** The rule keys on
  the *designation*, not on stack position, precisely so that an MMIC's second metal level is not
  mistaken for a plane. Mark what you mean.
- **This is the port's negative terminal for every port type** — an edge port at a conductor end, an
  [internal delta gap](mom-engine.html#ports), or an [internal port](mom-engine.html#ports) whose
  whole purpose is to reach it. It is why an internal port needs a path down to the plane at all.

<div class="callout warn">
<span class="label">If the answer looks wrong by a constant factor, check this first</span>
<p>The height between the signal conductor and the ground plane is the single number a microstrip's
impedance depends on most. A ground reference marked one entry too low — on the far side of a
dielectric you forgot was there — changes every impedance in the run while leaving the result looking
completely plausible. The run's own notes name the conductor it used and its height; read them.</p>
</div>

## Which conductors get simulated {#levels}

A conductor entry that is **not** the ground reference is a *signal* conductor, and a signal
conductor with artwork on it is an **analysis level**.

- With one signal conductor, that is the level. Nothing to choose.
- With more than one, the EM setup can name the levels it wants (the analysis-level list); with none
  named, every signal conductor that actually carries artwork is included.
- Levels are ordered **bottom to top**, and the lowest one sits on the slab's top surface.

## The slab, and what makes one solvable {#slab}

The solver works on the **dielectric between the ground plane and the lowest analysis level**. Two
stackup mistakes are refused by name rather than solved around:

- **The signal conductor sits at or below the ground plane.** There is no slab between them. Almost
  always a stackup written in the wrong order — the list runs top to bottom.
- **There is no dielectric entry between the plane and the conductor.** A conductor floating directly
  on a plane with nothing in between has no substrate to be a microstrip over.

Dielectric entries between the two are combined into the substrate the kernel solves on, with their
own ε<sub>r</sub> and tanδ. Layers *above* the top conductor are not part of that slab: the top
boundary is what is above, and it is `Open` unless you say otherwise.

## Vias {#vias}

A via entry is not a layer — it is a **connection between two conductor entries, named by name** in
its `Span from` / `Span to` fields. Draw a via shape on the drawing layer the entry is bound to, and
the EM extractor turns it into a real vertical current path.

Three rules the extractor applies, each reported when it bites:

- **The two conductors must be adjacent in the stack.** A via that skips a level is not built, because
  there is nothing to connect it to in between.
- **A via may terminate on the ground plane**, and that is the common backside/through-hole case: the
  named conductor is the ground-designated one, so the via runs from a signal level down to the plane.
  A via naming some *other* non-analysis conductor is ignored with a note — it would otherwise silently
  model a structure you did not draw.
- **The footprint is squared.** A round barrel staircased onto the mesh grid would cost a gridline per
  facet for no physics, so it is replaced by the **equal-area square**, which preserves the conducting
  cross-section. The run reports that it did.

<div class="callout note">
<span class="label">An internal port does not need you to draw one</span>
<p>An <a href="mom-engine.html#ports">internal port</a> is placed on the metal and returns to the
ground plane, so it needs a path down there. If you drew a via, it drives yours. If you did not, the
solver builds one — a square of <strong>the technology's default via drill</strong> (or, if the
technology declares none, a quarter of the substrate height), reported by size in the run's notes.
That path is real metal and its inductance is in the answer, which is why you can override it simply
by drawing the via you want.</p>
</div>

## What the EM engine does not read {#ignored}

Stated plainly, because a field that is carried but unused is worse than one that is absent:

- **Conductor σ is not used by the full-wave planar kernel.** It models metal as a perfect conductor;
  conductor loss is not in the answer. The **cross-section (uniform-line) kernel** does use it, for
  its Wheeler-incremental-inductance surface resistance.
- **Conductor thickness is not modelled by the full-wave kernel either** — it solves a zero-thickness
  sheet. It is used by the cross-section kernel and by interchange.
- **A via's fill and wall thickness are carried for thermal work**, not read by the RF solve: a plated
  wall a few µm thick is many skin depths at RF, so plated and solid behave the same.

## Checking what it resolved to {#check}

**Every EM run says which conductor it used as ground, and at what height**, in its notes — along with
the level list, the slab it built, and any via it ignored or built for you. That report is the
authoritative answer to "where is my port's negative terminal", and it is written by the code that
actually did it rather than re-derived for display.

If you are chasing a result that looks plausible but wrong, read those notes before anything else:
almost every stackup mistake produces a complete, believable answer for a structure you did not draw.

**See also:** [The MoM engine](mom-engine.html) for the ports themselves, [EM Setup](em-setup.html)
for the panel that runs them, and [The Layout Editor](layout-editor.html#technology) for the rest of
the technology file.
