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
<li><a href="#sheet-surface">Which surface a conductor's sheet sits on</a></li>
<li><a href="#slab">The slab, and what makes one solvable</a></li>
<li><a href="#vias">Vias</a></li>
<li><a href="#mim">A thin-film (MIM) capacitor</a></li>
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
<span class="label">A dielectric is never drawn, and it is laterally infinite</span>
<p>A conductor entry has drawing layers; a <strong>dielectric entry has none</strong>. It is a sheet
of material spanning the whole problem at its stated thickness, everywhere, because that is what the
solver's Green's function is built on — a stratified medium, uniform in x and y.</p>
<p>This matters most for a <em>thin</em> dielectric that a real process only leaves under and just
beyond a structure — a capacitor dielectric, say. The model carries it as a full sheet at its true
height. That is the standard trade in this class of tool, and it is a good one where the structure
is; it is <em>not</em> free everywhere else, and <a href="#mim">A thin-film (MIM) capacitor</a> gives
the measured cost.</p>
</div>

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

## Which surface a conductor's sheet sits on {#sheet-surface}

The full-wave solver models a conductor as a **zero-thickness sheet at one height**, so its band's
thickness has to go somewhere: it is absorbed into the dielectric on the other side of the sheet.
Which side is a per-conductor setting — **EM sheet at** on the conductor's row in the Stackup tab.

| Setting | The sheet sits | The band's thickness goes | Height of a line on it |
|---|---|---|---|
| **Bottom** (default) | on the band's bottom surface | into the dielectric **above** | the substrate under it |
| **Top** | on the band's top surface | into the dielectric **below** | the substrate **plus its own metal** |

**Bottom is what you want almost everywhere**, and it is what a conductor that says nothing means: a
trace deposited on a substrate and encapsulated by whatever comes next, whose height above the ground
plane comes out as the substrate thickness.

**Top is for the lower plate of a capacitor.** With the sheet at the bottom of its band, that plate's
whole metal thickness lands inside the plate gap, and the solver separates the two sheets by the
capacitor dielectric *plus that metal* — 3.2 µm rather than 0.2 on a typical MMIC metal. Setting
**Top** on the lower plate's entry puts the gap back to the dielectric alone, and gives the metal's
thickness to the substrate below instead. The shipped *MMIC GaAs + MIM* technology does exactly this
on `Metal1`.

The trade is stated rather than hidden: on that technology a `Metal1` microstrip's EM substrate is
103 µm of GaAs rather than 100 — a ~3% height shift, against a 16× error in the modelled plate
separation. The **closed-form** microstrip models are unaffected either way; they model real metal of
real thickness and measure their height to the metal's underside, so on a technology using **Top**
they and an EM run differ by up to one metal thickness. The run's notes name each level's z *and* the
surface it sits on, which is where you read back what was actually solved.

## The slab, and what makes one solvable {#slab}

The solver works on the **dielectric between the ground plane and the lowest analysis level**. Two
stackup mistakes are refused by name rather than solved around:

- **The signal conductor sits at or below the ground plane.** There is no slab between them. Almost
  always a stackup written in the wrong order — the list runs top to bottom.
- **There is no dielectric entry between the plane and the conductor.** A conductor floating directly
  on a plane with nothing in between has no substrate to be a microstrip over.

**Several dielectric entries between the two are carried as several layers**, each at its own
thickness, ε<sub>r</sub> and tanδ — the medium is stratified and the solver solves it that way. (It
used to refuse a stratified region under the feed and tell you to merge the entries into one; merging
them changes the physics, and the reason for it is gone.) The run's notes name the layers it found and
print the single ε<sub>r</sub> it uses to *size* the calibration standards and the mesh — the
series-capacitance equivalent of the stack. That number is a mesh-sizing average and never the
reference impedance the answer is published against.

Layers *above* the top conductor are not part of that slab: the top boundary is what is above, and it
is `Open` unless you say otherwise.

## Vias {#vias}

A via entry is not a layer — it is a **connection between two conductor entries, named by name** in
its `Span from` / `Span to` fields. Draw on the drawing layer the entry is bound to, and the EM
extractor turns what you drew into a real vertical current path.

**Two kinds of artwork count**, and both go through the same rules below:

- a **via primitive**, the pad-and-drill point you place with the Via tool;
- a **filled region** — a rectangle or a polygon, holes included. This is what a thin-film
  capacitor's plate connection is: a patch nearly as large as the plate itself, rather than a point.
  Several regions on one via entry are several footprints of the same connection.

A **path** on a via layer is ignored, with a note — a path is a centreline and encloses no area, so
there is no footprint to mesh. Draw the region instead.

Three rules the extractor applies, each reported when it bites:

- **The two conductors must be adjacent in the stack.** A via that skips a level is not built, because
  there is nothing to connect it to in between.
- **A via may terminate on the ground plane**, and that is the common backside/through-hole case: the
  named conductor is the ground-designated one, so the via runs from a signal level down to the plane.
  A via naming some *other* non-analysis conductor is ignored with a note — it would otherwise silently
  model a structure you did not draw.
- **A via primitive's footprint is squared.** A round barrel staircased onto the mesh grid would cost
  a gridline per facet for no physics, so it is replaced by the **equal-area square**, which preserves
  the conducting cross-section. The run reports that it did. **A drawn region is not squared** — the
  substitution exists so a circle nobody drew does not staircase, and an outline you drew already is
  the footprint, so it is meshed as it stands.

<div class="callout note">
<span class="label">An internal port does not need you to draw one</span>
<p>An <a href="mom-engine.html#ports">internal port</a> is placed on the metal and returns to the
ground plane, so it needs a path down there. If you drew a via, it drives yours. If you did not, the
solver builds one — a square of <strong>the technology's default via drill</strong> (or, if the
technology declares none, a quarter of the substrate height), reported by size in the run's notes.
That path is real metal and its inductance is in the answer, which is why you can override it simply
by drawing the via you want.</p>
</div>

## A thin-film (MIM) capacitor {#mim}

A MIM capacitor is a thin dielectric between two metal plates inside the interlayer dielectric. In a
stackup it is **three entries**:

| Entry | Kind | What it is |
|---|---|---|
| `MIM Metal` | Conductor | The top plate — a thin metal with its own drawing layer |
| `MIM Dielectric` | Dielectric | The capacitor dielectric: thin, higher ε<sub>r</sub>, **no drawing layer** |
| `MIM Via` | Via | The plate's connection up to the routing metal above it |

The bottom plate is the interconnect metal underneath — no fourth entry. You draw the two plates and
the plate connection; the dielectric is never drawn. **A `Cap Dielectric` or `Nitride` layer in a
layer table is mask documentation, not this.**

Both capacitor forms are ordinary multi-level artwork:

- **Shunt** — bottom plate on the lower metal over one or more backside vias, top plate on the plate
  metal, feed landing on the upper metal through a plate-via region.
- **Series** — feed in on the lower metal, which *is* the bottom plate, and out on the upper metal
  through the plate via.

### It is a separate technology, and that is not filing {#mim-separate}

circuitRF ships **two** MMIC technologies: the plain one, and *MMIC GaAs + MIM*. They are identical
apart from the three entries above. The reason they are two files rather than one is that a capacitor
dielectric between the two interconnect metals is **not** a free addition:

- **Airbridge posts stop solving.** A post between the two metals now crosses a dielectric interface,
  and a via that crosses one is refused by name — the closed-form integral along a via is written in
  one region's coefficients. **This refuses the whole run**, it does not drop a shape. So on the
  MIM technology, do not mix airbridge posts and capacitor plates in one EM setup.
- **A line on the upper metal moves.** The 0.2 µm ε<sub>r</sub> 6.8 sheet sits in its substrate, and
  a line on the *lower* metal sees it as superstrate: measured on the acceptance line,
  ε<sub>eff</sub> rises ~1.7% and Z₀ falls ~2.8%.

Neither is acceptable as a silent change to the technology existing designs already use, and both
are fine in a technology whose purpose is capacitors. Pick the plain starter for airbridge work and
the MIM one for capacitor work.

There is also a **plate level makes a post non-adjacent** rule on top of the refusal above: a via
must span two conductors that are adjacent *in the analysis*, and with the plate metal in the level
list a post between the two interconnect metals skips a level and is dropped with a note.

### Adding a capacitor module to an imported technology {#mim-import}

A technology imported from a process description often arrives **without** its capacitor module. A
process stack description states the interconnect — the metals, the insulation between them, the vias
that join them — and treats an optional thin-film module as exactly that: optional, and frequently
left out, while the layer table shipped beside it still lists the module's drawing layers. The import
is faithful to what it was handed, so what you get is a valid technology whose plate layers draw
perfectly and connect to nothing.

**The import report says so, by name.** Two of its notes are about this and only this:

- *"No via in the file names these conductors…"* — a conductor the stack describes that no via entry
  reaches. It cannot be connected, and the file never said how it should be.
- *"The layer table defines N drawing layer(s) no stackup entry is bound to…"* — layers you can draw
  on that the stack does not model. A shape on one is artwork, not structure. A real layer table has
  plenty of these legitimately, so the note counts them and names the first dozen.

Neither is an error, and neither is a guess: nothing in the file states the missing piece, so
circuitRF names the gap rather than inventing an entry to fill it.

**The fix is three rows** in the Technology Editor's Stackup tab, in stack order, and it takes about
two minutes. Numbers below are silicon-nitride-class examples — take yours from the process:

| Row | Kind | What to set |
|---|---|---|
| The capacitor dielectric | Dielectric | Thickness *d* (50–300 nm; 0.2 µm here) and ε<sub>r</sub>. **No drawing layer.** |
| The top plate | Conductor | Its thickness (0.25 µm here) and its **drawing layer** — the plate layer the import already brought in |
| The plate connection | Via | **Span from** the plate **to** the metal above it, and its own **drawing layer** — the plate-via layer the import already brought in |

Insert them between the two interconnect metals, in that order, top plate above dielectric. The bottom
plate is the interconnect metal underneath — there is no fourth row, and nothing is drawn for the
dielectric.

**ε<sub>r</sub> comes from the capacitance density the process quotes**, which is the number a process
actually publishes:

<p class="center">ε<sub>r</sub> = C″·<em>d</em> / ε₀</p>

At C″ = 0.30 fF/µm² over *d* = 0.2 µm that is 6.8 — the value the shipped MIM technology carries. Work
it out from your own two numbers rather than copying a permittivity out of a materials table: the
capacitance is what you want the model to reproduce, and *d* and ε<sub>r</sub> only ever appear
together in it.

**Then set the lower metal's sheet surface to Top.** A conductor is solved as a zero-thickness sheet,
and by default that sheet sits at the *bottom* of its own band — so the modelled plate separation
would be your dielectric **plus the lower plate's whole metal thickness**, several times the gap you
just entered, with a plausible capacitance to show for it. See
[Which surface a conductor's sheet sits on](#sheet-surface).

<div class="callout warn">
<span class="label">Do it on a copy, not on the technology your designs already use</span>
<p>A capacitor dielectric between two interconnect metals is not a free addition — an airbridge post
between those metals then crosses a dielectric interface and <strong>refuses the whole run</strong>,
and a line on either metal moves in ε<sub>eff</sub> and Z₀. That is why circuitRF
ships the MIM stackup as a <a href="#mim-separate">separate technology</a> rather than as three extra
rows on the plain one. Copy the imported technology, add the rows to the copy, and retarget the
layouts that need capacitors.</p>
</div>

Two things to know before you read a number off the result: the dielectric is
[laterally infinite](#anatomy) — present everywhere at its stated thickness, which is a good
approximation because the fields that set the capacitance are confined under the plates — and the
mesh has to resolve the gap, which is the subject of the next section.

### Reading a capacitance off a MIM run {#mim-accuracy}

<div class="callout warn">
<span class="label">Never read a small element off a RAW solve, and mesh the gap</span>
<p>The solver models the plate separation the process states — 0.2 µm on the shipped MIM technology,
the capacitor dielectric and nothing else. (It used to model 3.2 µm: a conductor is solved as a
zero-thickness sheet, and with that sheet at the bottom of its own band the lower plate's whole metal
thickness fell inside the gap. The lower plate's entry now says its sheet sits on the
<strong>top</strong> of its band — see <a href="#sheet-surface">Which surface a conductor's sheet
sits on</a>.) The run's notes print every level's z <em>and</em> the surface it sits on, so you can
always read back the separation it used.</p>
<p><strong>A port on upper metal now de-embeds.</strong> It used to be refused: the reference
impedance a de-embedded answer is published against is Z<sub>c</sub> = γ/(jωC<sub>pul</sub>), and
C<sub>pul</sub> came from an electrostatic image series over one grounded slab — the right problem for
a trace on the substrate's own top surface and the wrong one for metal buried in the interlayer
dielectric. The solver now solves that electrostatics at the port level's own height in the real
stack, so an ordinary two-port MIM network de-embeds like anything else. The same change lets a
technology carry several dielectrics <em>under</em> the lowest analysis level, which used to be
refused with "merge the layers".</p>
<p><strong>Read the capacitance from the de-embedded answer, never from a raw one.</strong> A raw
solve's s-parameters include each port's own discontinuity, and that discontinuity is a
fraction-of-a-femtofarad series element: read a small capacitance through it and you read the port,
not the capacitor, whatever the plates do. That is not special to a capacitor — a matched 50 Ω GaAs
microstrip reads |S<sub>21</sub>| = 0.07 raw at 10 GHz. (An earlier revision of this page said the
plate capacitance was "not modelled", on a measurement taken through exactly that raw path —
retracted.)</p>
<p><strong>And mesh the gap.</strong> The one thing that genuinely limits a plate capacitance is the
mesh: the cross-level part of the fill degrades as the cell size grows against the plate separation,
and the extracted capacitance follows it — within 10% of ε₀ε<sub>r</sub>A/d while the ratio is at
most 5, 1.46× at 12.5, and the wrong sign at 25. <strong>The shipped MIM technology's default mesh
sits outside that</strong> (a 10 µm plate pair 0.2 µm apart meshes at 2.5 µm, i.e. 12.5), and the run
says so in its notes — it is a note rather than a refusal because the rest of the structure is
unaffected. Refine the mesh over the plates until the note stops firing before you trust the
value.</p>
</div>

**The shunt form's backside via sets an upper frequency.** A vertical basis carries uniform current
along its whole run, so a via is refused above k·ℓ = 0.3. Through 100 µm of GaAs that ceiling is just
under 40 GHz; the refusal says so and names the number it computed. The series form has no such via
and no such bound.

## What the EM engine does not read {#ignored}

Stated plainly, because a field that is carried but unused is worse than one that is absent:

- **Conductor σ is not used by the full-wave planar kernel.** It models metal as a perfect conductor;
  conductor loss is not in the answer. The **cross-section (uniform-line) kernel** does use it, for
  its Wheeler-incremental-inductance surface resistance.
- **Conductor thickness is not modelled by the full-wave kernel either** — it solves a zero-thickness
  sheet, placed on one surface of the conductor's own band. The thickness itself is used by the
  cross-section kernel and by interchange. Which surface the sheet sits on is normally invisible
  (3 µm of metal under a 100 µm substrate moves nothing), and matters exactly once: between the two
  plates of a [thin-film capacitor](#mim). It is a per-conductor setting — see
  [Which surface a conductor's sheet sits on](#sheet-surface).
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
