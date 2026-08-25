---
title: The MoM Engine
slug: reference/mom-engine.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > MoM engine
lede: The planar method-of-moments solver: what it does, what it will not do, and how it works.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#plain">In plain terms</a></li>
<li><a href="#can-cannot">What can and cannot be simulated</a></li>
<li><a href="#implementation">For advanced users: how circuitRF implements MoM</a></li>
<li><a href="#ports">Ports</a></li>
<li><a href="#deembedding">De-embedding</a></li>
<li><a href="#adaptive">Adaptive frequency sampling</a></li>
<li><a href="#conformal">Conformal boundary cells</a></li>
<li><a href="#mesh-convergence">Mesh convergence, and how to check it</a></li>
<li><a href="#budget">What makes a run infeasible</a></li>
<li><a href="#refusals">What the engine refuses, and why a refusal is better</a></li>
<li><a href="#cosim">Using EM results in a circuit simulation</a></li>
<li><a href="#worked">Worked example: a microstrip line with a bend</a></li>
</ol>
</nav>

## In plain terms {#plain}

A circuit model of a microstrip bend is a formula: someone measured a family of bends, fitted a
closed-form expression, and published its validity range. It is fast, it is accurate inside that range,
and it knows nothing about *your* bend — not that there is a via 200 µm away, not that the ground plane
has a slot under it, not that the adjacent trace couples into it.

**An electromagnetic solve computes the fields for the artwork you actually drew.** It takes your layout,
your stackup and your frequency range, and returns S-parameters. No formula, no validity range, no
family of measured parts — just Maxwell's equations discretised over your geometry.

What that buys you, concretely:

- **Discontinuities you have no model for** — a bend into a taper into a pad, a coupled section with a
  via in the middle, a matching stub whose neighbour matters.
- **Coupling you did not intend.** A circuit model connects what you wired; an EM solve reports what the
  metal actually does, including the parts you would rather it did not.
- **A number you can hand to a harmonic-balance run.** An EM result is a Touchstone file, and a
  Touchstone file drops into a test bench beside the real device model. That is the workflow that makes
  this worth having rather than a curiosity — see [Co-simulation](#cosim).

It is not free. A circuit model answers in microseconds; a full-wave solve of a small structure takes
seconds per frequency point. Use the model where the model is valid, and the solver where it is not.

## What can and cannot be simulated {#can-cannot}

This is a **planar (2.5D) solver**. It solves conductors embedded in a **laterally infinite, vertically
stratified** medium: metal is horizontal and thin, current flows in-plane, and z-directed current flows
through vias. That is the same class of tool as the commercial planar solvers, and it is *not* FEM and
not general 3D.

### Can

- **Planar conductors on a layered substrate** — any number of dielectric layers, with real
  ε<sub>r</sub> and tanδ. *(Dielectric loss, in both kernels. **Conductor** loss is the quasi-static
  kernel only — see below.)*
- **Arbitrary planar shapes**: lines, bends, tapers, stubs, spirals, pads, coupled sections.
- **Multiple metal levels**, with **vias** carrying z-directed current between them.
- **A ground plane** at the bottom of the stack, or an open boundary.
- **Discontinuities and the coupling between them** — which is the entire point.
- **Frequency sweeps**, with [adaptive sampling](#adaptive) so a resonant response does not need a fine
  uniform grid.

### Cannot

<div class="callout warn">
<span class="label">Read this list before you trust a result</span>
<p>A user who discovers a limit by getting a wrong answer has been failed by the documentation.</p>
</div>

- **Anything genuinely three-dimensional.** A wirebond arcing through air is not a planar structure. Use
  [wBond](wbond.html), which is a separate 3D kernel built for exactly that.
- **Finite substrate extent.** The dielectric layers are laterally **infinite**. A board edge, a cavity
  wall, a shielding can — none of these exist to this solver.
- **Enclosures and absorbing boundaries.** There is no box. Real planar tools suppress port-to-port
  radiative and surface-wave coupling with box walls or absorbing boundaries; **this kernel has
  neither, by design**, and that is what sets the [de-embedding accuracy floor](#deembedding).
- **Vertical conductors other than vias.** A via is a z-directed current path through the stack. An
  edge-plated wall, a connector barrel, a heatsink is not.
- **Non-planar dielectrics.** Conformal coating, a partially milled cavity, a moulded package body — the
  medium is stratified, so a dielectric is a slab spanning the whole plane or it is not representable.
- **Magnetic materials beyond a scalar µ<sub>r</sub> per layer.** No ferrites, no anisotropy.
- **Non-linearity of any kind.** This solves a linear problem and returns S-parameters.
- **Conductor loss, in the full-wave kernel.** Its metal is a **perfect conductor**: the conductivity
  and thickness you set in the stackup are carried but not used in the field solve. Dielectric loss
  (tanδ) *is* modelled, and it dominates on ordinary substrates — measured on 1.6 mm FR-4, the missing
  conductor term is **6.5% / 3.0% / 2.1%** of the total conducted loss at 2 / 10 / 20 GHz. So a
  full-wave insertion loss reads slightly optimistic, by a known and shrinking amount. The
  quasi-static kernel *does* model conductor loss, through Wheeler's incremental inductance rule over
  every lossy surface including the ground plane — which is one reason the two kernels' loss numbers
  do not agree exactly on a uniform line.

There is also a **quasi-static kernel** for the special case of a uniform transmission-line
cross-section, which is described below and which is far faster than the full-wave path where it
applies. Its own limits are narrower: no discontinuities, no bends, no stubs, no spirals, no radiation,
no resonance, and no coupling between non-parallel conductors. It **does** model conductor loss, which
the full-wave kernel does not.

## For advanced users: how circuitRF implements MoM {#implementation}

Two kernels ship. circuitRF chooses between them from the geometry and tells you which one it picked and
why.

### The quasi-static kernel — uniform cross-sections

When the selected geometry reduces to straight, mutually parallel, constant-width conductors, it is not
solved as a field problem at all. Its **cross-section** is solved for per-unit-length RLGC, and
everything else follows in closed form.

The unknowns are **charge density on boundary segments** — free charge on conductor perimeters, bound
polarisation charge on dielectric interfaces. Carrying bound charge explicitly is the decision that
makes this cheap: the Green's function stays the **free-space 2D logarithmic potential**, so there are
no Sommerfeld integrals and no special functions, and an arbitrary number of dielectrics costs nothing.
A ground plane is one image.

From there:

```text
[C]  from the real stackup, with ε* = εr(1 − j·tanδ) carried through  →  C = Re(C), G = −ω·Im(C)
[C₀] from the same geometry with every dielectric replaced by air
εeff = C/C₀                    [L] = µ₀ε₀[C₀]⁻¹        (the TEM identity)
[R]  from Wheeler's incremental inductance rule, summed over EVERY lossy surface — including the ground plane
γ = √((R+jωL)(G+jωC))          Z_c = √((R+jωL)/(G+jωC))    →  ABCD of a length-ℓ line  →  S
```

**[C], [C₀] and ∂L/∂n are frequency-independent.** A 1001-point sweep is therefore *one* matrix solve
plus 1001 closed-form evaluations — effectively instantaneous. An optional closed-form
Kirschning–Jansen dispersion correction extends the useful frequency range without needing a solver.

The one place it can be quietly wrong is that dielectric interfaces are laterally infinite and must be
truncated. The truncation distance is a visible setting with a sensible default, and extending it must
not move Z₀ — which is a convergence test you can run yourself.

### The full-wave planar kernel

The general case. It solves the mixed-potential integral equation over the metal.

**Green's function.** The spatial-domain Green's function for a layered medium requires inverting the
spectral form through a **Sommerfeld integral** — oscillatory, slowly convergent, with branch points
and surface-wave poles. circuitRF uses **DCIM** (the Discrete Complex Image Method): the spectral
Green's function is approximated as a sum of complex exponentials by matrix-pencil fitting, and each
term inverts in closed form by the Sommerfeld identity. It is validated against direct Sommerfeld
integration — a second, independent formulation — over ρ/λ from 10<sup>-4</sup> to 10 on both starter
substrates. As a fraction of the free-space kernel at the same ρ, which is what a matrix fill actually
experiences, the error is **≤ 6 × 10<sup>-3</sup> across that span**; strict relative error is
≤ 10<sup>-2</sup> out to ρ/λ ≈ 1, beyond which the fit **refuses** rather than extrapolating.

**Basis functions and mesh.** Rectangular **rooftop** basis functions on a **tensor-product grid**.
Diagonals and curves are staircased by default, or cut conformally — see
[Conformal boundary cells](#conformal). The unknown count is basis functions, not cells: a rooftop spans
a pair of adjacent cells.

**Edge mesh.** Current density has a 1/√d singularity at a conductor edge, and a uniform mesh
under-resolves it badly — which shows up directly as wrong loss and wrong Z₀. So every conductor edge
gets 2–4 geometrically graded cells, the outermost a small fraction of the conductor width and growing
inward by a ratio of about 1.5–2. The reference length for that grading is the **conductor width**, and
that choice is measured rather than assumed: on the FR-4 reference structure it lands 0.18% from the
converged limit at N = 552, where the alternative reference needs N = 7,562 for a comparable answer.

**The fill.** With a rectangular mesh and source and observer in one plane, the **inner integral is
closed form** — six of them, checked against adaptive quadrature to 10<sup>-12</sup>. That is why the
classic "nearly touching cells" problem is not where the difficulty lives here: only the outer integral
is numerical, and it sees a continuous function with a kink, which is a quadrature-order question.
Against an ε<sub>r</sub> = 1 reduction where the kernel is exact, the assembled matrix is right to
**5.0 × 10<sup>-6</sup>**.

**The solve.** Dense complex LU per frequency, or — optionally — an iterative solve against a
grid-accelerated matrix–vector product. The accelerated path computes the same answer a different way,
to its own accuracy gates; its win is **working-set memory**, roughly 4× less past about 900 unknowns,
while the *time* crossover is much later, around 3,700 unknowns. Below that the dense path is faster.
**It does raise the unknown ceiling — from 5,000 to 12,000 — but only on a single-metal-level structure
with no vias**, which is the only case it can accelerate at all.

**De-embedding.** A two-line calibration; see [De-embedding](#deembedding).

### Which kernel ran, and why

The EM Setup panel names it, with the reason. The quasi-static kernel is chosen when the geometry
reduces to a uniform cross-section — and when it does, the panel shows you the cross-section it
extracted: *"uniform 2-conductor cross-section · W = 2.9 mm · ℓ = 20 mm"*. If it does not reduce, the
refusal is specific: *"this geometry has a bend at (x, y)"*, not a vague failure.

## Ports {#ports}

### What a port is

A port is where power enters or leaves the structure. In this engine every port is the same thing: a
**voltage source impressed across a cut in the metal**, driving the current that crosses that cut. The
solve excites one port at a time to build the S-matrix.

The two port types differ in **where the cut is** — at the end of a conductor, or in the middle of one
— and everything else about them follows from that one difference.

### How you define one

**A port is a label, not a new kind of shape.** The Port tool places an ordinary layout label with its
port flag set, which is why a layout carrying ports is still just a layout.

- **Numbering comes from the label's own text.** `1`, `P1`, `p2`, `#3`, `Port 4` all parse. A label that
  names no number is auto-numbered to the lowest free one rather than refused.
- **Two labels naming the same number is a refusal by name**, not a silent win for one of them.
- **The direction is inferred from geometry where it can be, reported, and refused when it cannot.** A
  label at the exact corner of a conductor is equally close to two edges — and guessing reverses the
  direction of current into the structure, which is a hard π in S₂₁: smooth, plausible, and completely
  invisible in a magnitude plot. So it is named and refused: *"Port 1 is ambiguous… Move the label."*
  Every resolved port reports the cut it landed on and which way current flows in. **Read that report.**
- **The type and the reference impedance live in the EM setup**, per port, not on the shape. A layout is
  geometry; how you drive it is an analysis setting. The same artwork can be an edge-driven filter in
  one setup and a structure with a gap in the middle in another, without editing the drawing.

### The two port types

| Type | Where the cut is | Is it de-embedded? | Use it for |
|---|---|---|---|
| **Edge port** *(the default)* | One mesh cell in from a conductor's **end face**, referenced to the ground plane | **Yes** — the [two-line calibration](#deembedding) removes the port discontinuity, so what you get back is your structure's own response | Anything power flows **into or out of**: a line, a bend, a filter, a matching network, a coupler — every structure you would measure on a fixture or a probe station |
| **Internal delta-gap port** | An **interior cut** of a conductor, with metal on both sides, at the mesh gridline nearest where you put the label | **No, and it cannot be** — there is no feed outside the cut to remove. Its S-parameters are reported at the gap, in the reference impedance you set | A **series** element embedded in the metal (a series R, L or C you will attach in the schematic), or a **device terminal in the middle of a structure** |

<div class="callout warn">
<span class="label">There is no shunt-to-ground port, and that is a real limit</span>
<p>Both types above put a port <strong>in line with the current</strong>. Neither gives you a port from
a point on the metal <strong>down to the ground plane</strong> — the thing a <strong>shunt</strong> R, L
or C needs, or a device terminal that returns to ground.</p>
<p>Other planar tools have one, variously called a <em>via port</em> or an <em>internal port</em>.
circuitRF does not. A shunt port is not a variation on an internal gap: it does not cut the trace, and
it needs a vertical current path down to the plane, which is a different excitation.</p>
<p><strong>What to do meanwhile:</strong> draw the shunt path in the artwork. Run a stub, a pad and a via
to the ground plane as real metal, and put an <em>edge</em> port on the far end of that stub — then the
EM run models the whole shunt path, including the via, and your component attaches at the stub's end
where an edge port is well posed.</p>
</div>

**The decision rule is one question: does the power cross the boundary of your drawn metal?**

- **Yes** — it comes in from a connector, a probe, or the next block along — then it is an **edge port**,
  on that boundary.
- **No** — you want to break the conductor *here* and put something across the break — then it is an
  **internal delta-gap port**.

<p class="small">An edge port may grow a short uniform lead onto your metal before meshing, so its
calibration has uniform line to measure against; the lead is removed exactly afterwards and the answer
is still reported at your own drawn edge. It is automatic —
see <a href="#feed">Auto-ports and the feed extension</a>.</p>

### What each one looks like in the layout

{{ui: ports-edge}}

A Klopfenstein taper with an edge port on each end — the ordinary two-port setup, and what a correct
one looks like. Each port draws **a bar across its own end face** (where current crosses into the
structure) **and an arrow along the direction it flows in**. A dashed leader ties the label to the bar
when the two are not in the same place, so the mark stays readable wherever you put the text.

Three things worth reading off this picture:

- **Each bar is the width of the metal at ITS OWN end**, not the width of the part. On a taper those
  are very different numbers, and the bar is how you check the solver agrees with you about which face
  you named.
- **The labels sit at the centre of each end face**, not at a corner. A label at a corner is equally
  close to two edges, and the run refuses it rather than guessing — because guessing reverses the
  direction of current into the structure, which is a hard π in S₂₁ and invisible in a magnitude plot.
- **Both arrows point inward**, at each other. Current flows *into* the structure through every port;
  that is what a port's direction means, on both ends of a two-port.

{{ui: ports-internal-gap}}

A 50 Ω line with the same two edge ports on its ends and an **internal delta-gap port in the middle** —
where a series component would go. Holding the width constant is what makes the comparison readable:
the only thing that differs between port 3's mark and ports 1 and 2's is the mark itself.

An internal delta-gap port is drawn as a **different mark on purpose**: two bracketed bars facing each
other across a break in the metal, overhanging the conductor top and bottom, with the arrow running
through the break. An edge port's mark says *a boundary, and which way in*; a gap's says *a break, and
which way across*. At a glance they are not each other.

The gap is drawn **where you placed the label**, because that is where the cut is. An edge port's bar
is always snapped to the conductor's end face, however far from it you put the text.

### How wide is the gap, really?

**The gap is the mesh's, not the artwork's.** The cut is a mesh gridline, and the excitation drives the
pair of cells either side of it — so the length of conductor the gap occupies is those two cells, set by
*Cells per wavelength* and the rest of the mesh settings. **Nothing you can draw changes it.**

The drawing follows that, and says which of the two things it is showing:

- **Before you compute a mesh** there is nothing to measure, so the break is drawn at a fixed fraction
  of the port's width — a legible glyph, not a dimension. Do not measure it.
- **Once the mesh is computed** the break is drawn at **the real thing**: the two cells either side of
  the cut, with the brackets on the mesh's own gridlines so you can read them against the overlay.

{{ui: ports-gap-mesh-width}}

The mark also moves to the cut. A gap can only fall on a gridline, so if your label sits between two,
the brackets go to the gridline that was chosen and a dashed leader runs back to your label — **the
snap, drawn**, rather than only reported in the notes.

**If you edit the layout, the mesh is dropped and the break reverts to its glyph width.** That is
deliberate: a stale width left on screen would look exactly like a live one.

**Refine the mesh and the gap gets shorter**, which is what makes it a better approximation to a point
discontinuity. There is no other lever.

<div class="callout warn">
<span class="label">Do not draw a slot in your metal for an internal port</span>
<p>The conductor stays <strong>continuous</strong> and the port cuts it. Drawing a physical gap makes
two separate conductors with two end faces — which is a pair of edge ports, and a different structure.</p>
</div>

### How to choose, in practice

- **Two-port and multi-port passive structures** — everything from a bend to a Wilkinson — are **all
  edge ports**. This is the overwhelmingly common case.
- **A structure you will attach a component to in the middle** — a series capacitor breaking a line, a
  resistor across a gap, a shunt element — gets an **internal delta-gap port** at the break, plus edge
  ports wherever power actually enters. Simulate the metal in EM, connect the component in the
  schematic, and the two meet at the gap.
- **An active device embedded in your artwork** is the same pattern: an internal port at each terminal
  you will attach the device model to.
- **If power crosses your metal's boundary, use an edge port.** The two types report at different
  planes and give different answers; which one is right is set by where the power actually goes, not
  by which is cheaper to compute.

<div class="callout note">
<span class="label">What an internal port costs you, stated plainly</span>
<p>An internal port's gap is <strong>one mesh cell wide</strong>, and the cut lands on the nearest mesh
gridline to where you put the label — not exactly where you clicked. The run reports how far it moved,
and refining the mesh there is what closes both gaps: it puts the cut closer to where you asked, and it
makes the gap a better approximation to a point discontinuity. There is no calibration to fall back on
here, so the mesh is the only lever.</p>
</div>

### Setting the type

**EM Setup panel → Ports.** Each port gets a row with its reference impedance and a **type** dropdown —
Edge or Internal delta gap. The rows come from the port labels in your layout, so the port *count* is
the geometry's; the type and the impedance are yours.

<div class="callout note">
<span class="label">Two EM setups on one layout may disagree, and that is allowed</span>
<p>Because the type belongs to the analysis, nothing stops two <code>.cem</code> files that reference
the same layout from calling the same port different things — a gap in the middle of a trace in one, a
pair of edge ports in the other. That is a legitimate thing to want.</p>
<p>The layout can only draw one of them. It follows <strong>the setup you last worked in</strong>, and
if that takes the marks off a different setup that disagreed, the Messages panel says so and names
both. If the marks are not what you expect, that line tells you which setup they belong to.</p>
</div>

**The type only appears for a full-wave planar analysis.** Internal delta-gap ports are a full-wave
feature, and the uniform-line (quasi-static) kernel has none — the row shows a reference impedance and
nothing else there.

That is a property of what the uniform-line kernel *is*, not a gap in it. It never meshes the plane at
all: it solves a **cross-section** for per-unit-length RLGC and forms the network of a length-ℓ line in
closed form, so its ports are the two ends of that line by construction. There is no gridline in the
middle to cut, no pair of cells to drive across, and no mesh whose refinement could shrink a gap. (It is
the same fact that makes de-embedding a no-op there: the reference planes are the line's ends exactly,
so there is no port discontinuity to remove.)

**If you need a gap in the middle of a uniform line, set Analysis to the full-wave planar kernel
explicitly.** Do not leave it on Auto for this. A uniform line with a gap on it is still a uniform
*cross-section* as far as the geometry is concerned, so Auto picks the cheaper uniform-line kernel —
and that kernel has nowhere to put the port. **circuitRF refuses that combination by name rather than
running it**, because the alternative is a complete, plausible two-port answer for a line without the
gap you asked for. The refusal names the remedy: change the analysis, or change the port back to an
edge port.

Two rules the panel enforces:

- **An internal port must sit on the metal**, not just near it. An edge port's label may sit slightly off
  the end face it names; an internal one cuts the conductor, so it has to be on the conductor.
- **An internal port must state its direction.** For an edge port the direction can be inferred from the
  nearest conductor boundary. A label in the middle of a conductor is roughly equally far from all four
  edges, so there is nothing to infer from — and the direction is what decides which way positive
  current crosses the gap. Rotate the port to point the way current should flow across the cut.

A gap in the middle of a conductor is a **series** source: it drives the two halves in antiphase, so a
gap at the centre of a symmetric line gives S₁₃ = −S₂₃, not +. That sign is real physics, not a
convention you can flip, and it is why the direction is required rather than guessed.

### Auto-ports and the feed extension {#feed}

**You do not have to add a feed line to your artwork.** Place a port on the part you drew and press
Simulate.

This matters more than it sounds. The de-embedding calibration standard is an isolated **uniform line**
of the port's cross-section, and the calibration is only valid if your metal looks like that line for
the distance the standard replaces. A taper's flanks are oblique from the first cell, so it does not. So
before meshing, each port's own polygon is **extruded outward from its drawn end face** by however much
uniform line the calibration is short of; afterwards the lead is removed exactly, as a matched section
in the line's own Z_c, using the propagation constant the calibration already measured.

Three properties of that are load-bearing:

- **The reference plane is still your drawn metal edge.** The lead moves where the *error box* is
  measured, never where the *answer* is reported.
- **A feed that is already uniform grows nothing**, so a structure that never needed this is unchanged
  bit for bit. Running out of metal counts as uniform — a short line is a short structure, not a flared
  one.
- **Every case it cannot be sure of is declined, not guessed.** An end face that is not a single
  straight segment, a port whose level is ambiguous, a lead that would run into other metal: all
  declined, with the warning you would have got anyway. Moving metal you drew would be a worse failure
  than the one being fixed.

### Port Z0

Default 50 Ω, editable per port in the EM Setup panel. It is a **renormalisation** applied to the
result, not a property of the geometry — the solve does not change.

### The ground reference — every port's negative terminal

**Get this wrong and everything downstream is wrong**, and there is no per-port control for it.

**Every port in a full-wave run returns through the same plane: the stackup's ground.** That is the
negative terminal of every edge port and of every internal delta gap, and it is not something you set
on a port — it comes from the *technology*, by one rule:

> the **top surface of the highest conductor marked as a ground reference that lies below the signal
> level**. If no conductor is marked, the stackup's bottom boundary condition is used instead, and the
> run says so.

**On a stackup with several metal layers this matters and is worth checking.** Which levels take *part*
in the analysis is yours to choose (the EM Setup panel's analysis-level checkboxes). Which one is
*ground* is not a port setting — **to return through a different conductor, designate that conductor as
the ground reference in the technology editor.** The run's notes name the plane it resolved, its height,
and the signal level it sits below.

Two consequences of it being the stackup's plane:

- **It is modelled as laterally infinite.** A finite ground pour is not that, and a via to a
  ground-designated conductor that is *not* the resolved reference is dropped by name rather than
  treated as the plane.
- **Any other reference is refused, not approximated** — a coplanar ground, a second signal conductor,
  a differential pair, or a port driven between two levels at a via. Each is named in the refusal.

Coplanar waveguide is the case people expect to work and it does not: its return is the adjacent
coplanar conductors, which is a different port model rather than a different layer, and this kernel
does not build one.

## De-embedding {#deembedding}

### What it does

A raw port excitation includes the **port discontinuity** — the local field disturbance where the
excitation is applied, which is an artefact of the simulation and not a property of your structure.
Reporting those S-parameters as the structure's response is simply wrong. De-embedding removes it.

circuitRF uses a **two-line calibration**: it simulates a short and a longer uniform reference line of
the port's cross-section, extracts the port's own reflection and the line's propagation constant, and
removes them.

<div class="callout note">
<span class="label">This is about edge ports. An internal delta-gap port is not de-embedded.</span>
<p>A two-line calibration removes a <em>feed</em>. An interior cut has metal on both sides, so there is
no feed outside it — nothing to calibrate against, nothing to remove, and no line impedance to
reference the answer to. An internal port's S-parameters are reported <strong>at the gap itself, in the
reference impedance you set for it</strong>, and the run's notes say so whenever one is present.
Everything below in this section concerns edge ports.</p>
</div>

### Where the reference plane sits

<div class="callout warn">
<span class="label">The reference plane is not user-positionable, and that is a stated limitation</span>
<p>It sits <strong>one mesh cell in from the drawn metal edge</strong>, because that is where the
calibration actually removes the port discontinuity. There is deliberately no offset knob: offering one
would offer a way to get a different answer for the same structure. The planes are <strong>drawn over
your layout</strong>, from coordinates the engine reports, so their location is never a mystery.</p>
<p>The corollary: if you need the reference plane somewhere else, move the drawn metal edge — that is,
change where your structure ends — rather than looking for a setting.</p>
</div>

One more property of the method, worth stating because it is a property and not a gap: **the
de-embedded S-matrix is referenced to the line's own Z_c, and the calibration cannot determine it.**
Z_c is recovered from γ and the per-unit-length capacitance, differenced between the two standards so
the end effects cancel exactly. The assumption that C is frequency-independent is that route's real
cost, and it is measured at 0.4% / 2.3% / 6.3% at 1 / 5 / 20 GHz.

### The accuracy limit is radiation, not the algebra

The calibration algebra is **exact**: a de-embedded uniform section comes out perfectly matched at the
two lengths the calibration was solved from (|S₁₁| = 8.5 × 10<sup>-16</sup>), and two independent routes
to γ agree to between 2.5 × 10<sup>-4</sup> and 3.9 × 10<sup>-3</sup> across 2–10 GHz.

What limits the answer is **direct radiative and surface-wave coupling between the two ports**. It
decays only algebraically, and there is no term for it in a "box + matched line + box" model. Measured
on 1.6 mm FR-4, a section that *should* be perfectly matched reads:

| Frequency | \|S₁₁\| of a section that should read zero |
|---|---|
| 2 GHz | 3.9 × 10<sup>-4</sup> |
| 10 GHz | 6.0 × 10<sup>-3</sup> |

That is an f² scaling — and, importantly, it is **not monotone in the standard's length**, which is how
it was identified as coupling rather than as calibration error.

<div class="callout note">
<span class="label">What that means for you, in one line</span>
<p>A de-embedded answer here is good to <strong>a few parts in 10<sup>3</sup> at 2 GHz and a few parts
in 10<sup>2</sup> at 10 GHz</strong>, and <strong>a longer feed does not improve it</strong>. Real planar
tools suppress this with box walls or absorbing boundaries; this kernel has neither.</p>
</div>

### Rules of thumb for port setup

All of these follow from one fact: **de-embedding accuracy is limited by radiation, so a port whose feed
radiates cannot be cleanly de-embedded.**

- **Keep ports apart.** Port-to-port coupling is the error floor, and it grows as f². If you can place
  the two ports on opposite ends of the structure rather than on the same side, do. *Rule of thumb: at
  least a few substrate heights of separation, and more at the top of your band.*
- **Do not lengthen the feed to fix accuracy.** It does not work — the coupling is direct, not through
  the line — and it costs solve time. This is the counter-intuitive one, and it is measured.
- **Keep the feed's own cross-section uniform for the calibration's run**, and keep other metal out of
  that run. The solver grows the uniform lead it needs, but **a lead lengthens a feed, it cannot move a
  neighbour sideways.** Metal running alongside the port inside the calibration's own run is still a
  limitation and is still warned about.
- **Use a feed width the mesh can resolve.** The edge mesh needs several cells across the conductor; a
  feed narrower than a few cells is under-resolved exactly where the port excitation is applied. *Rule
  of thumb: at least 3–5 cells across the feed width, which is what the default mesh settings give you
  if you leave them alone.*
- **Port the *end face*, not a corner.** A label at a corner is ambiguous and will be refused; a label
  on a clean straight end face resolves without a guess.
- **None of this applies to an internal delta-gap port**, which is not de-embedded at all. Its accuracy
  is set by the mesh at the cut, not by radiation between feeds.
- **Watch the band, not just the centre.** Everything above degrades with frequency. If your structure
  is fine at 2 GHz and strange at 12 GHz, suspect the port before the geometry.

### What a good and a bad de-embedded result look like

| | Good | Bad |
|---|---|---|
| **Passivity** | σ<sub>max</sub>(S) ≤ 1 at every frequency | σ<sub>max</sub> > 1 — the analysis, not the design |
| **Σ\|S\|²** for a low-loss structure | Slightly below 1, decreasing smoothly with frequency | Above 1, or wandering |
| **A section you know is matched** | \|S₁₁\| in the 10<sup>-4</sup>–10<sup>-2</sup> range, rising smoothly with f | \|S₁₁\| near 1, or jumping between adjacent frequency points |
| **∠S₂₁** | Smooth, monotone, ≈ −βℓ | Discontinuous, or with a hard π step (a reversed port side) |
| **Refining the mesh** | Moves the answer a little, and in one direction | Moves it a lot, or in different directions each time |

<div class="callout warn">
<span class="label">A non-passive result is reported, not shipped quietly</span>
<p>A de-embedded sweep that publishes σ<sub>max</sub>(S) > 1 says so — at the frequency, and by how
much. <strong>The excess is the analysis, never the design</strong>, and you need to know that before you
read the plot. A famous example: a 2000 mil 50 → 12 Ω Klopfenstein taper once came back as
|S₁₁| = 1.0000, |S₂₁| = 0.0008, Σ|S|² = 1.06 — a non-passive open circuit — because the calibration
standard did not resemble the taper's own flanks. That is what the automatic feed extension exists to
prevent, and what the passivity check exists to catch if anything like it ever happens again.</p>
</div>

## Adaptive frequency sampling {#adaptive}

### The problem it solves

Fill is O(N²) and solve is O(N³) **per frequency**, because the Green's function is frequency-dependent.
So a sweep costs the number of points times the cost of a point, and a resonant structure needs a lot of
points to look right. Sample a resonance on a coarse uniform grid and you get a plot that is *wrong* —
the notch lands between two samples and vanishes. Sample it finely enough everywhere and you have paid
for resolution across the whole band to get it in one place.

### How it works

1. Solve a sparse subset of the requested frequencies.
2. Fit a **rational interpolant** to what has been solved.
3. Solve a **midpoint** and compare it with what the model predicted.
4. Where they disagree, add samples there and refit. Where they agree, stop.

The tolerance is agreement to 10<sup>-3</sup> in |S|. Two properties are worth knowing:

- **The published sweep is always exactly the grid you asked for.** Adaptive sampling changes which
  points are *solved*, never which are *reported*.
- **Every solved point carries the solver's own result, unchanged.** Nothing that was actually solved is
  replaced by the model.

### What the tolerance trades

Tighter tolerance means more solved points, so more time, and a curve that tracks the true response more
closely between the ones you asked for. Looser means fewer solves and a curve that may smooth a feature
the solver would have found. Because the trade is *entirely* between solve count and fidelity in the
un-solved gaps, the useful move when in doubt is not to loosen the tolerance — it is to reduce the
number of requested points.

### How to tell it converged, and what to do if it did not

The run reports how many points it solved out of how many you requested. Two checks:

- **Solve fraction.** A smooth response converges after solving a small fraction of the grid. A run that
  solved nearly every point is telling you the response is not smooth on that grid — which is
  information, not a failure.
- **Re-run with the sampling off.** If the curve does not move, it converged. This is the direct test and
  it costs one full sweep; it is worth doing once per new class of structure, not once per run.

If it will not settle, the usual cause is a genuine sharp resonance. Narrow the band around it and sweep
that region on its own rather than fighting the whole span.

Adaptive sampling applies to the full-wave kernel only. The quasi-static kernel evaluates in closed form
after one solve, so there is nothing to sample adaptively, and the control says so rather than sitting
there greyed out with no explanation.

## Conformal boundary cells {#conformal}

### What it is

The mesh is a rectangular tensor-product grid. Where a conductor's edge is oblique or curved, the grid
has to decide what to do with the cells the edge passes through. Two options:

- **Staircase** *(the default)* — a cell is either in the metal or not, so a diagonal becomes a
  staircase.
- **Conformal** — the boundary cells are **cut** to follow the metal: one straight cut through an
  otherwise rectangular cell.

### What it buys, measured

On a 96-sided disc refined from 316 to 3,964 unknowns:

| | Staircase | Conformal |
|---|---|---|
| Does the simulated shape match what you drew? | No — between 0.2% and 0.8% of the area is wrong, and **the amount changes every time you refine** | **Yes, exactly** — to round-off, at every refinement |
| Does refining converge? | **No — the value wanders up and down** | **Yes — it steps steadily toward a limit** |
| Spread over the last three refinements | 0.669% | **0.279%** |

The first row is the real result. Under a staircase, refining the mesh quietly changes *which shape* is
being simulated, which is why the second row is possible at all.

### What it costs

**Essentially nothing.** At matched settings the conformal mesh was *slightly smaller* than the
staircased one (316 unknowns vs 324 coarse; 3,964 vs 3,972 fine), and solve time is set by the unknown
count. Building the mesh does a little more geometry work, but meshing is milliseconds against a solve
measured in seconds to minutes.

### It ships OFF, and that is not an oversight

<div class="callout warn">
<span class="label">Do not read this as a free win</span>
<p>Two reasons it is opt-in. The bookkeeping one: <strong>every accuracy figure recorded for this engine
was taken with the staircase</strong>, and anyone reproducing one has to be able to. The real one:
<strong>a Klopfenstein taper comes out slightly worse under conformal cells at coarse PCB settings</strong>
— 0.593% area error becomes 0.766%. Making something worse by default is not defensible even when it is
better in most cases.</p>
</div>

**Turn it on** when you are simulating a bend, a linear taper, a disc, or any curved outline, and you
want an answer you can refine toward with confidence. On a bend or a linear taper the area error goes
from 0.10–0.47% to **exact**.

**Leave it off** for all-Manhattan artwork, where it has nothing to do, and for a Klopfenstein taper at
coarse settings.

### Convex decomposition — the reflex-vertex fallback

The cut works by clipping a rectangular cell against the metal outline, and the result has to be a
region the fill's integrals can evaluate exactly. Where the clipped region fails that test — most often
because the outline **bends back on itself** inside a single cell — the cell falls back to the staircase
rule rather than producing an answer it cannot stand behind.

The consequence you can observe: on artwork with many reflex vertices, the number of fallback cells
**saturates**. Measured on a Klopfenstein taper at cells/λ of 20 / 40 / 80 / 160 / 320, the fallback
count runs 52 / 78 / **126 / 126 / 126** — and the outline has exactly 126 reflex vertices. Once each
reflex vertex owns a cell, refining the mesh cannot reduce the count any further.

**That is why "exact" holds for a bend, a linear taper and a disc but not for a Klopfenstein taper**, and
why refining does not fix it. It is a property of the artwork, not of the settings.

## Mesh convergence, and how to check it {#mesh-convergence}

A mesh is a discretisation, so **an EM answer is only as good as its mesh, and the only honest test is
refinement**. The procedure:

1. Run at the default mesh. Note the unknown count and the answer at a frequency you care about.
2. Raise **Cells per wavelength** by roughly 1.4× and re-run.
3. Repeat until the answer stops moving by more than you care about.

What to look for:

- **A monotone, decreasing step.** Each refinement should move the answer less than the last, in the
  same direction. That is convergence.
- **A wandering value is not converging**, and on curved or oblique artwork it usually means the
  staircase is changing the shape at each refinement — see [Conformal boundary cells](#conformal).
- **Edge mesh matters more than cell size.** If loss or Z₀ is off, refine the edge cells before refining
  the whole grid; the edge singularity is where the error lives.

The **Mesh** button computes the mesh **without solving**, so the unknown count, the smallest and largest
cell and the truncation extent are all visible before you commit to a run. Use it.

## What makes a run infeasible {#budget}

The full-wave matrix is dense and complex: N unknowns is N² × 16 bytes.

| N | Matrix memory | Character |
|---|---|---|
| 500 | 4 MB | A short line or a bend lives here |
| 2,000 | 64 MB | Interactive: seconds per frequency |
| 5,000 | 400 MB | The practical ceiling for a lightweight tool |
| 10,000 | 1.6 GB | Out of scope |

**There is a hard ceiling at 5,000 unknowns for the dense solve, the predicted N is shown before you
solve, and a mesh above it is refused** with a message pointing at the remedies that actually bind. A
tool that silently tried to allocate 12 GB would not be lightweight.

**The [accelerated solve](#budget) raises that ceiling to 12,000** — on a single-metal-level structure
with no vias, which is the only kind it can accelerate. A multi-level or via-bearing mesh is refused by
name regardless, so the ceiling there is still 5,000, and the refusal names turning the accelerator on
as the first remedy whenever doing so would let your mesh through.

Two numbers that surprise people, both measured on 1.6 mm FR-4 at 10 GHz:

- **A 4 MB matrix is not a 4 MB sweep.** At N = 552, one frequency costs about 1.7 s once the
  frequency-independent core is built — so a 101-point sweep is about **three minutes**, not "instant".
- **De-embedding multiplies that by about 4.4×.** The calibration standards are not small — on that same
  structure they measure 2.58× the DUT's own unknowns — and they are **78% of the total cost**. A
  de-embedded 101-point sweep of that structure is about 13 minutes.

That is the arithmetic behind [adaptive sampling](#adaptive) being on by default: the per-point cost went
up 4.4× and the number of points did not.

The **accelerated solve** option changes the memory picture rather than the time one: roughly 4× less
working set past about 900 unknowns, with the time crossover much later, around 3,700 unknowns. It is
**single-metal-level only, with no vias** — and within that, it raises the ceiling from 5,000 unknowns
to 12,000.

<div class="callout warn">
<span class="label">One thing it does not reach, and it costs real minutes when it bites</span>
<p>De-embedding's own reference-impedance step is a <strong>separate, always-dense</strong> computation
on each calibration standard, and the accelerator does not touch it. A standard reproduces your port's
own cross-section, so a <em>wide</em> port's standard can be as large as the whole structure — and such
a run is refused up front, at setup time, even though the DUT's own accelerated solve would have
succeeded. Turn de-embedding off to read the raw solve, knowing those S-parameters include the port
discontinuity and are for diagnostics only.</p>
</div>

## What the engine refuses, and why a refusal is better {#refusals}

A wrong EM answer is smooth, plausible and expensive — it goes into a design, and the board comes back
wrong. So this engine refuses rather than extrapolating, and every refusal names the specific feature
and says where the capability arrives.

The refusals you are most likely to meet:

| Refusal | What to do |
|---|---|
| **"This geometry has a bend at (x, y)"** — from the quasi-static kernel | Nothing: the full-wave kernel takes it. This message means the cross-section extractor declined, which is how the two kernels divide the work. |
| **The predicted unknown count exceeds the ceiling** | Coarsen the mesh, or simulate less of the structure. See [the budget](#budget). |
| **"Port n is ambiguous"** | Move the label off the corner onto a clean end face. |
| **Two ports naming the same number** | Renumber one. |
| **"Port n is an internal delta-gap port… not ON any conductor"** | An internal port cuts the metal, so it has to be placed on the metal. Move the label onto the conductor, or make it an edge port if you meant the end. |
| **"…an internal delta-gap port with no direction on it"** | Rotate the port to point the way current should flow across the cut. There is no nearby conductor end to infer a direction from in the middle of a trace, and the sign is not guessed. |
| **"…the conductor under it has no interior cut to gap"** | The conductor is only one cell long where you put the gap, so there is no pair of adjacent cells to break between. Raise Cells per wavelength, move the port into the middle of a longer run, or make it an edge port. |
| **The DCIM fit is outside its validated range** | The structure is electrically larger than the fitted kernel covers at that frequency. Narrow the band. |
| **A via separation the vertical kernel cannot resolve** | Turn on the **direct vertical (via) kernel**, which replaces the fitted Green's function with direct numerical integration for that one term, at 15–45% more per frequency point per via span. |
| **No stackup** | An EM run refuses without a technology rather than inventing one — the one place a missing technology is not degraded gracefully. |

A refusal you can read is worth more than a number you cannot check.

## Using EM results in a circuit simulation {#cosim}

**An EM run produces a Touchstone file.** That is the whole co-simulation story, and it needs no new
machinery.

1. Lay out the structure — a matching network, a coupler, a bias tee.
2. Set up and run the EM analysis. It writes an `.sNp` to a predictable path derived from the layout and
   setup names.
3. Drop an [SnP component](components.html#snp) into your test bench and point it at that file.
4. Run harmonic balance with the real device model beside it.

### Putting a component in the middle of the metal

An [internal delta-gap port](#ports) is how a component gets *into* the metal rather than beside it. The
workflow is the ordinary one with one extra port:

1. Draw the conductor as **one continuous piece** — do not draw a slot where the component goes.
2. Put edge ports where power enters and leaves, and an **internal delta-gap port** where the component
   goes. Three ports, so the EM run writes an `.s3p`.
3. Drop that `.s3p` into a schematic and **connect the component to the gap's port**.

{{ui: em-series-gap-cosim}}

Ports 1 and 2 are the line's ends. The 1.2 pF capacitor sits on **port 3** — the gap — and it is in
**series** in the metal: everything that gets from port 1 to port 2 goes through it.

<div class="callout note">
<span class="label">It looks like a shunt element. It is not one.</span>
<p>Port 3's two terminals are the two lips of the cut — neither of them is the ground plane. The
schematic draws every port of an N-port against a shared ground because that is how an N-port is
written down, not because one lip is grounded.</p>
<p>What matters is the constraint, and it is the right one: terminating port 3 with an impedance
imposes <em>V₃ = −Z·I₃</em> on the <strong>gap</strong> voltage and the current <strong>crossing the
gap</strong>, which is exactly "put Z into the cut". Ground here is bookkeeping. If you want a
<em>shunt</em> element — one that takes current from the trace down to the ground plane — an internal
delta gap is the wrong port for it, and circuitRF has no port for it yet.</p>
</div>

Two practical notes:

- **Leave port 3's reference impedance at 50 Ω** unless you have a reason. It is a reference the answer
  is expressed in, not a property of the gap; the `.sNp` header records it and the schematic reads it
  back, so the two cannot disagree.
- **Use it for anything the gap can carry**: a series R, L or C, a measured 2-port of a real part, or a
  device terminal. The EM run models the artwork; the schematic models the part. Neither approximates
  the other, which is the whole point of splitting them.

### Everything else about co-simulation

Three consequences, all good ones:

- **The artefact is inspectable and portable.** It is a Touchstone file: plot it, archive it, hand it to
  a colleague, diff it against a measurement.
- **Re-running is a file update**, so the schematic picks up a new result the same way it picks up any
  changed source.
- **Staleness is detectable.** The file's header is stamped with the stackup, the mesh settings, the port
  definitions and a hash of the geometry — hashed separately, so a warning can say *which* of the three
  moved. A stale `.sNp` sitting beside an edited layout is reported rather than silently wrong.

The EM result also carries a **diagnostics group** alongside `S` and the per-port `Z0`, and the two
kernels deliberately do not share its name: the quasi-static kernel's is `tline` (Z_c, γ, ε_eff,
attenuation, R/L/G/C per unit length) and the full-wave kernel's is `planar` (γ, Z_c, ε_eff,
attenuation, C per unit length, and the calibration's own residual and usability flags). A per-unit-length
quantity from a 2D quasi-static solve and one back-solved from a de-embedded full-wave S-matrix are
different claims. They agree on a uniform line, and they diverge with frequency — which is dispersion,
and is a *result*.

## Worked example: a microstrip line with a bend {#worked}

A 50 Ω line on the PCB starter technology, with a right-angle bend in it. The point of the exercise is
that the bend is exactly what a circuit model handles badly and an EM solve handles well.

### 1. Draw it

New layout on the PCB starter technology — 1.6 mm FR-4, ε<sub>r</sub> 4.4, tanδ 0.02, 1 oz copper,
bottom ground. Display unit mil.

A 50 Ω line on that stack is **W ≈ 2.9 mm (114 mil)**. Draw two rectangles on Top Copper:

```text
horizontal arm:   from (0, 0)          to (400 mil, 114 mil)
vertical arm:     from (286 mil, 0)    to (400 mil, 400 mil)
```

They overlap in the corner square, which is what makes it one conductor. Leave the corner square — an
unmitred bend is the thing being measured.

### 2. Set the stackup

Nothing to do. The stackup comes from the technology, and the EM Setup panel shows it back to you:
`FR-4 1.6 mm εr 4.4 tanδ 0.02` between `Top Copper 35 µm` and the ground plane. Check the **Ground
reference** row reads the ground plane and not something else.

### 3. Place ports

Port tool; click the horizontal arm's left end face, then the vertical arm's top end face. Two labels,
`1` and `2`. Check the notes: each port should report the edge it resolved to and the direction current
flows in. If either says "ambiguous", the label is on a corner — move it.

Leave both reference impedances at 50 Ω.

### 4. Choose a mesh

Leave the mesh on its defaults for the first run and press **Mesh** — not Simulate. Read the mesh
report:

- **Unknowns**: expect a few hundred. Well under the ceiling.
- **Smallest / largest cell**: the smallest should be a small fraction of 114 mil — that is the edge
  mesh doing its job.

At 10 GHz on FR-4, λ_g ≈ 16.5 mm, so λ_g/20 ≈ 0.8 mm gives roughly 24 cells along a 20 mm run and about
6 across the width, before edge refinement. A few hundred unknowns is the expected answer, and a number
wildly different from that means something is wrong with the geometry or the layer mapping.

### 5. Set the sweep and run

1 to 10 GHz, 101 points. Leave adaptive sampling on. Press **Simulate**.

Expect roughly a minute or two — most of it in the calibration standards, not the structure. The run
reports how many points it actually solved.

### 6. Read the result

The result opens in the [Data Display](data-display.html). Plot |S₂₁| in dB and |S₁₁| in dB against
frequency.

| What to look for | What it means |
|---|---|
| \|S₂₁\| close to 0 dB at 1 GHz, falling smoothly | Ordinary conductor and dielectric loss on FR-4 |
| \|S₁₁\| low at 1 GHz, rising with frequency | The bend's shunt capacitance beginning to matter |
| A smooth ∠S₂₁, roughly −βℓ | The electrical length of the two arms |
| Σ\|S\|² slightly below 1, smooth | Passive and lossy, as expected |

### 7. Sanity-check it against the circuit model

This is the step people skip and should not.

Build the same thing in a schematic from [MLIN](components.html#mlin) and
[MBEND](components.html#mbend) with the same widths, lengths and substrate, and run an S-parameter
analysis over the same band. Overlay the two in one Data Display — the EM result is a Touchstone file,
so [add it as a second data source](data-display.html#free-floating) and put both traces on one plot.

They should agree closely at the bottom of the band and separate at the top. **If they disagree at
1 GHz, something is wrong with your setup, not with the physics** — check, in this order: the line width
(is it really 50 Ω on this stack?), the substrate (does the schematic's MLIN carry the same
ε<sub>r</sub> and h?), the ground reference, and the port sides.

### 8. Refine once

Raise cells per wavelength by 1.4× and re-run. If |S₁₁| at the top of the band moves by less than you
care about, you are converged. If it moves a lot, refine again — and if it moves *differently* each
time on a mitred bend, turn on [conformal boundary cells](#conformal).

<p class="small">See also: <a href="em-setup.html">EM Setup</a> — the panel, control by control ·
<a href="layout-editor.html">The Layout Editor</a> · <a href="wbond.html">wBond</a> (3D bondwires) ·
<a href="components.html#mlin">The microstrip component family</a> ·
<a href="data-display.html">The Data Display</a>.</p>
