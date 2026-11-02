---
title: The MoM Engine
slug: reference/mom-engine.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > MoM engine
lede: The planar method-of-moments solver: what it does, what it will not do, and how it works.
keywords: EM, electromagnetic, method of moments, MoM, planar solver, full wave, extraction, Green's function, mesh
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#plain">In plain terms</a></li>
<li><a href="#can-cannot">What can and cannot be simulated</a></li>
<li><a href="#implementation">For advanced users: how circuitRF implements MoM</a></li>
<li><a href="#ports">Ports</a></li>
<li><a href="#deembedding">De-embedding</a>
  <ul><li><a href="#validity">The validity condition: an isolated feed</a></li></ul></li>
<li><a href="#adaptive">Adaptive frequency sampling</a></li>
<li><a href="#resonance-search">The resonance search</a></li>
<li><a href="#conformal">Conformal boundary cells</a></li>
<li><a href="#mesh-convergence">Mesh convergence, and how to check it</a></li>
<li><a href="#budget">What makes a run infeasible</a></li>
<li><a href="#aim">The accelerated solve, and when to use it</a></li>
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

This planar solver is one of three circuitRF can use. How it compares with the 3D FEM and FDTD
solvers, and when to reach for one of them instead, is in
[EM Solvers: MoM, FEM and FDTD](em-solvers.html).

## What can and cannot be simulated {#can-cannot}

This is a **planar (2.5D) solver**. It solves conductors embedded in a **laterally infinite, vertically
stratified** medium: metal is horizontal and thin, current flows in-plane, and z-directed current flows
through vias. That is the same class of tool as the commercial planar solvers, and it is *not* FEM and
not general 3D.

### Can

- **Planar conductors on a layered substrate** — any number of dielectric layers, with real
  ε<sub>r</sub> and tanδ. **Dielectric AND conductor loss, in both kernels.**
- **Conductor loss**, from the conductivity and thickness set in the stackup. In the full-wave kernel
  it is a term in the field equation itself — the solved current is the one a lossy conductor
  actually carries, not a perfect conductor's current with a correction applied afterwards — so the
  extra loss at a rim, where the current crowds, comes out of the solve rather than being assumed.
  **Two things bound it and both are measured.** The model is a *sheet*: one current per location,
  so it cannot carry the different currents a real strip's upper and lower faces carry, nor the
  current down its sides. Against the quasi-static kernel's independent thick-strip calculation on a
  re-bisected 50 Ω line, the full-wave answer comes out at **0.63 of it on 1.6 mm FR-4 and 0.73 on
  100 µm GaAs** — under-reading, consistently, and **converging**: refining the mesh at the
  conductor rim changes it by under 1% per step, so this is the model's own limit and not a
  resolution you can mesh your way out of. **The ground plane's share IS included** — see
  [The ground plane's own loss](#ground-loss) below.
  **How much it matters depends entirely on the substrate**, and the FR-4 figure everyone quotes is
  the one where it matters least:

  | Substrate | conductor loss as a share of the total, at 2 / 10 / 20 GHz |
  |---|---|
  | 1.6 mm FR-4, 35 µm Cu, tanδ 0.02 | 6.4% / 3.0% / 2.1% |
  | **MMIC starter — 100 µm GaAs, 3 µm Au, tanδ 0.0006** | **98.5% / 96.2% / 94.6%** |
  | Low-loss laminate 0.508 mm, 35 µm Cu, tanδ 0.0015 | 72.2% / 53.7% / 45.1% |

  On FR-4 dielectric loss dominates and always did. **On the MMIC technology the conductor is
  effectively all of the loss** — which is why "the metal is nearly lossless" is the wrong intuition
  to carry from one to the other.
  **On an antenna this lands on radiation efficiency**: the power the metal absorbs is itemised in
  the power budget as its own term rather than left inside the dielectric residual, and
  [Antennas](antennas.html#numbers) says which way each correction pushes. A measured example — a
  half-wave patch — loses **0.28 points** of radiation efficiency to 35 µm copper on FR-4 at
  2.4 GHz and **4.12 points** to 3 µm gold on GaAs at 60 GHz.
- <a id="ground-loss"></a>**The ground plane's own loss**, in both kernels. The laterally infinite plane the
  stack terminates on is a real conductor: it takes the conductivity and thickness of whichever
  stackup conductor this run returns through, and the run's notes name that layer and its σ. It is
  **not meshed** and costs no unknowns — it enters as a surface impedance on the boundary the solver's
  own Green's function terminates on, which is why it is free.
  **It is worth about a fifth to a quarter of the conductor term** — measured on a re-bisected 50 Ω
  line as **21% on 1.6 mm FR-4, about 11% on the MMIC technology and 25% on a low-loss laminate** —
  so on the MMIC technology, where the conductor term is nearly all of the loss, the plane alone is
  about a tenth of a line's total attenuation.
  Two independent formulations agree on it: the full-wave kernel's surface impedance and the
  quasi-static kernel's incremental-inductance rule come out within **6%** of each other wherever the
  substrate is electrically thin (k₀·h ≤ 0.07). It scales as 1/√σ, as a skin-effect term must, and it
  is **additive** with the signal metal's — the two surfaces do not double-count each other.
  **A ground layer with no conductivity set is a perfect plane, as it always was**, and the run says
  so rather than leaving you to infer it. Set σ on that layer in the technology editor's
  [Stackup](stackup.html) tab to carry the term.
  **In the power budget it is not separable from the dielectric loss** and the line is named for both
  — see [Antennas](antennas.html#numbers). The plane has no basis function to integrate over, so what
  it absorbs can only be reported as part of the residual; on a low-tanδ substrate that is most of
  what the line reads.
- **Arbitrary planar shapes**: lines, bends, tapers, stubs, spirals, pads, coupled sections.
- **Multiple metal levels**, with **vias** carrying z-directed current between them. A via is either
  a placed via primitive or a **drawn region** — a rectangle or polygon on the via entry's drawing
  layer, which is how a plate-sized connection is expressed. See
  [Vias](stackup.html#vias).
- **A ground plane** at the bottom of the stack, or an open boundary.
- **Discontinuities and the coupling between them** — which is the entire point.
- **Frequency sweeps**, with [adaptive sampling](#adaptive) so a resonant response does not need a fine
  uniform grid.
- **Radiation.** The top of the stack is an open half-space and the radiation condition is exact — no
  airbox, no absorbing boundary — so an antenna is an ordinary run with one checkbox added. Far-field
  pattern, directivity, gain, radiation efficiency, the itemised power budget, beamwidth per plane and
  polarization all come out of the same solve. See [Antennas](antennas.html), which also carries the
  limits that bound every one of those numbers.

### Cannot

<div class="callout warn">
<span class="label">Read this list before you trust a result</span>
<p>A user who discovers a limit by getting a wrong answer has been failed by the documentation.</p>
</div>

- **Anything genuinely three-dimensional.** A wirebond arcing through air is not a planar structure. Use
  [wBond](wbond.html), which is a separate 3D kernel built for exactly that.
- **Finite substrate extent.** The dielectric layers are laterally **infinite**. A board edge, a cavity
  wall, a shielding can — none of these exist to this solver.
- **A finite ground plane.** The plane is laterally infinite too, so there is no field behind it at all:
  no back radiation, no front-to-back ratio (the metric is present and refused by name), and a
  directivity that reads **optimistic** against a real board, whose plane is finite and puts substantial
  power behind it. A run that can see your pour reports how large it is in wavelengths — the example
  antenna's is 0.78 λ₀ across, the measured board that prompted this work 0.41 λ₀ — but the analysis
  still terminates on the infinite plane. See [Antennas](antennas.html#cannot).
- **Apertures in the ground plane.** A slot in the plane is not representable, so a slot antenna, a
  CPW-fed slot and an aperture-coupled patch are out. Edge-fed, inset-fed and coupled-patch feeds are
  in — see [Antennas](antennas.html#feeds) for which feed to use instead.
- **A dielectric that stops somewhere.** This is the limit most often mis-remembered, because circuitRF
  *does* model **drawn** dielectric artwork — a thin-film (MIM) capacitor's patterned film, tied to its
  plate. That mechanism decides **whether** a layer is in the run, never **where it stops**: inside a run
  every dielectric is laterally infinite. So a radome, a conformal coating or a gain-raising superstrate
  drawn over the patch alone is modelled as covering the **whole run, to infinity** — which moves
  resonance, gain and surface-wave launch. **And a cover layer above the topmost analysis level is not
  in the solve at all**: the medium is built from the ground plane up to that level and terminated in
  air there, so a radome or coating is discarded and the run warns by name. Measured on the shipped
  patch example — adding one gives bit-identical s-parameters. See
  [Antennas](antennas.html#example).
- **Enclosures and absorbing boundaries.** There is no box. Real planar tools suppress port-to-port
  radiative and surface-wave coupling with box walls or absorbing boundaries; **this kernel has
  neither, by design**, and that is what sets the [de-embedding accuracy floor](#deembedding).
- **Vertical conductors other than vias.** A via is a z-directed current path through the stack. An
  edge-plated wall, a connector barrel, a heatsink is not.
- **Non-planar dielectrics.** A partially milled cavity, a moulded package body, a coating that follows
  the metal — the medium is stratified, so a dielectric is a slab spanning the whole plane or it is not
  representable.
- **Magnetic materials beyond a scalar µ<sub>r</sub> per layer.** No ferrites, no anisotropy.
- **Non-linearity of any kind.** This solves a linear problem and returns S-parameters.
- **A plate capacitance read off a mesh that does not resolve the plate gap.** A thin-film (MIM)
  capacitor's network, feeds and vias are solved normally, and since a port on upper metal
  de-embeds the capacitance itself is in the de-embedded answer — to within 10% of ε₀ε<sub>r</sub>A/d
  while the mesh cell is at most **five times** the plate separation, 1.46× at 12.5 and the wrong
  sign at 25. The shipped MMIC technology's default mesh sits outside that and the run says so in a
  note. Two things that are no longer limits, because they were fixed: the plate sheets used to be
  separated by the capacitor dielectric *plus the lower plate's own metal thickness*, and a port on
  upper metal used to be refused. See [A thin-film (MIM) capacitor](stackup.html#mim).

There is also a **quasi-static kernel** for the special case of a uniform transmission-line
cross-section, which is described below and which is far faster than the full-wave path where it
applies. Its own limits are narrower: no discontinuities, no bends, no stubs, no spirals, no radiation,
no resonance, and no coupling between non-parallel conductors. It models conductor loss through
Wheeler's incremental inductance rule over every lossy surface **including the ground plane**, which
is a different calculation from the full-wave kernel's sheet — so the two do not agree exactly on a
uniform line, and the quasi-static one reads the higher of the two, by the 0.63/0.73 sheet factor
given above. **The ground plane's own term is in both of them now**, and where the substrate is
electrically thin the two formulations agree on it to within **6%** — see below.

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

Which edges get a graded fan: an axis-parallel edge long enough to carry the crowding (at least a fifth
of its conductor's extent across it), an edge that ends the conductor (both of its corners convex — an
end cap or a line's open end), and both edges at every **re-entrant corner** where a horizontal edge
meets a vertical one: an inset feed's notch, a step in width, a T-junction. The current is most strongly
singular at a re-entrant corner, so that is where the fan matters most. An edge shorter than the
detail floor gets none, and a diagonal or curved rim gets none, since a fan on a staircased edge does not
help. The mesh report says how many re-entrant corners were graded. Copper drawn as several shapes
that overlap or share an edge is merged into one conductor before any of this is decided, so how the
artwork was drawn — one polygon, or abutting pieces from an import — does not change which fans it gets.

**The fill.** With a rectangular mesh and source and observer in one plane, the **inner integral is
closed form** — six of them, checked against adaptive quadrature to 10<sup>-12</sup>. That is why the
classic "nearly touching cells" problem is not where the difficulty lives here: only the outer integral
is numerical, and it sees a continuous function with a kink, which is a quadrature-order question.
Against an ε<sub>r</sub> = 1 reduction where the kernel is exact, the assembled matrix is right to
**5.0 × 10<sup>-6</sup>**.

**The solve.** A dense complex factorisation per frequency, or — optionally — an iterative solve
against a grid-accelerated matrix–vector product. The dense one exploits the fact that the matrix is
symmetric: it factors it **in place**, in half the arithmetic a general solver would use, across
every core your core-count setting allows. The accelerated path computes the same answer a different way,
to its own accuracy gates; its win is **working-set memory**, roughly 4× less past about 900 unknowns,
while the *time* crossover is much later, around 3,700 unknowns. Below that the dense path is faster.
**Its unknown ceiling is never below 12,000 on a single-metal-level structure with no vias**; past
that it is the count your memory allows, as for the dense solve.

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

**A port is a label, not a new kind of shape.** The Port tool (<kbd>P</kbd> in the layout editor) places an
ordinary layout label with its port flag set, which is why a layout carrying ports is still just a layout.

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

### The three port types

| Type | Where the cut is | Is it de-embedded? | Use it for |
|---|---|---|---|
| **Edge port** *(the default)* | One mesh cell in from a conductor's **end face**, referenced to the ground plane | **Yes** — the [two-line calibration](#deembedding) removes the port discontinuity, so what you get back is your structure's own response | Anything power flows **into or out of**: a line, a bend, a filter, a matching network, a coupler — every structure you would measure on a fixture or a probe station |
| **Internal delta-gap port** | An **interior cut** of a conductor, with metal on both sides, at the mesh gridline nearest where you put the label | **No, and it cannot be** — there is no feed outside the cut to remove. Its S-parameters are reported at the gap, in the reference impedance you set | A **series** element embedded in the metal (a series R, L or C you will attach in the schematic), or a **device terminal in the middle of a structure** |
| **Internal port** | Between the metal **and the ground plane**, at the point you put the label. It does not cut the trace | **No, and it cannot be**, for the same reason. Its S-parameters are reported there, in the reference impedance you set | Anything that returns to **ground**: a bypass capacitor, a grounded resistor, a device terminal that returns to ground |

**The decision rule is two questions.**

1. **Does the power cross the boundary of your drawn metal?** If it comes in from a connector, a probe,
   or the next block along — it is an **edge port**, on that boundary.
2. **If not: does your component go IN LINE with the trace, or DOWN TO GROUND?** In line means breaking
   the conductor and putting something across the break — an **internal delta-gap port**. Down to
   ground means an **internal port**, placed on the metal at the point that goes to ground.

<div class="callout note">
<span class="label">You do not have to draw the via</span>
<p>An internal port's second terminal is the ground plane, so its current has to <em>get</em> there —
and in a planar solver the only thing that carries current downward is a via. <strong>That via is the
solver's job, not yours.</strong> Put the port on the metal at the point you want referenced to
ground:</p>
<ul>
<li><strong>If you drew a via there, the port drives your via</strong>, at the size and shape you drew
it.</li>
<li><strong>If you did not, one is built for you</strong> before meshing, and the run's notes say so
and give its size.</li>
</ul>
<p><strong>How big is the built one?</strong> A square of <strong>your technology's default via
drill</strong> — 0.305 mm on the PCB starter, 60 µm on the MMIC one. If the technology declares no
default via size, a quarter of the substrate height is used instead and the notes say that is what
happened. <em>It is deliberately not a mesh cell</em>: that path is real metal and its inductance is
part of what the port sees, so sizing it from the mesh would make your answer move when you refine
the mesh — which is the one thing refining a mesh must never do.</p>
<p>Either way the path is <strong>in the answer</strong>. If its size matters to you, draw the via and
the solver will use yours.</p>
</div>

<div class="callout note">
<span class="label">Where the negative terminal actually is</span>
<p>The ground plane an internal port returns to is <strong>not</strong> chosen per port: it is the
ground-designated conductor in your technology's stackup, and it is the negative terminal of
<em>every</em> port in the run — edge ports included. Which conductor that is, and how to change it,
is <a href="stackup.html">The stackup</a>.</p>
</div>

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

{{ui: ports-internal}}

The same line with an **internal port** at its centre — where a bypass capacitor or a grounded
resistor would attach. (A via is drawn here as well, to show the case where you have one; the port
does not need it.) Its mark is deliberately unlike the other two: **a plain ring round the point.**

The reason is that the other two marks are statements about a plane the current crosses *in the
layout* — an edge port's bar across an end face, a gap's brackets either side of a break — and both are
oriented by the direction current flows. **An internal port has no such direction**: its current
leaves the metal downward, out of the plane the layout draws. The ring says *the port is here*,
without claiming a direction the port does not have.

The ring used to carry a schematic ground symbol hanging below it. It said *the other terminal is the
plane* — which is true of every internal port and never varies, so it was ink on every one of them
that told none of them apart, and over dense artwork it read as clutter. The ring alone carries what
actually varies.

Its **polarity is fixed and you do not set it**: the + terminal is the metal and the − terminal is the
ground plane. Rotating the label does nothing, because there is nothing for a direction to mean.

**Like the gap's break, the ring is drawn at the real thing once a mesh exists.** Before you compute
one it is a fixed fraction of the conductor's width — a legible glyph, not a dimension, so do not
measure it. After, it is drawn round the **actual footprint the mesh resolved**: the cells the port
drives, centred on them, with a dashed leader back to your label when the two are not in the same
place. That is how you see a via that meshed smaller than you drew it, or the size of the path the
solver built for you. Editing the layout drops the mesh and the ring reverts to its glyph size.

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
  resistor across a gap — gets an **internal delta-gap port** at the break, plus edge ports wherever
  power actually enters. Simulate the metal in EM, connect the component in the schematic, and the two
  meet at the gap.
- **A component that returns to ground** — a bypass capacitor, a grounded resistor — gets an **internal
  port** at the point it attaches, plus edge ports wherever power enters. Drawing the via is optional;
  the solver builds one if you have not.
- **An active device embedded in your artwork** is the same pattern: an internal port at each terminal
  you will attach the device model to — a delta gap where the terminal sits in the trace, an internal
  port where it returns to ground.
- **If power crosses your metal's boundary, use an edge port.** The two types report at different
  planes and give different answers; which one is right is set by where the power actually goes, not
  by which is cheaper to compute.

<div class="callout note">
<span class="label">What an internal port costs you, stated plainly</span>
<p>A delta gap is <strong>one mesh cell wide</strong>, and the cut lands on the nearest mesh gridline
to where you put the label — not exactly where you clicked. The run reports how far it moved, and
refining the mesh there is what closes both gaps: it puts the cut closer to where you asked, and it
makes the gap a better approximation to a point discontinuity.</p>
<p>An internal port's cost is the same in a different currency: it drives the cells of the via
footprint that the mesh resolved, so a coarse mesh drives a smaller via than the one that is there.
The run reports the area it got.</p>
<p>Neither has a calibration to fall back on, so the mesh is the only lever for both.</p>
</div>

### Setting the type

**EM Setup panel → Ports.** Each port gets a row with its reference impedance and a **type** dropdown —
Edge, Internal delta gap or Internal. The rows come from the port labels in your layout, so the port *count* is
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

**The type only appears for a full-wave planar analysis.** Both internal port types are full-wave
features, and the uniform-line (quasi-static) kernel has neither — the row shows a reference impedance
and nothing else there.

That is a property of what the uniform-line kernel *is*, not a gap in it. It never meshes the plane at
all: it solves a **cross-section** for per-unit-length RLGC and forms the network of a length-ℓ line in
closed form, so its ports are the two ends of that line by construction. There is no gridline in the
middle to cut, no pair of cells to drive across, and no mesh whose refinement could shrink a gap. (It is
the same fact that makes de-embedding a no-op there: the reference planes are the line's ends exactly,
so there is no port discontinuity to remove.)

**If you need an internal port on a uniform line, set Analysis to the full-wave planar kernel
explicitly.** Do not leave it on Auto for this. A uniform line with a gap or a via on it is still a
uniform *cross-section* as far as the geometry is concerned, so Auto picks the cheaper uniform-line
kernel — and that kernel has nowhere to put the port. **circuitRF refuses that combination by name rather than
running it**, because the alternative is a complete, plausible two-port answer for a line without the
gap you asked for. The refusal names the remedy: change the analysis, or change the port back to an
edge port.

Three rules the panel enforces:

- **An internal port must sit on the metal**, not just near it. An edge port's label may sit slightly off
  the end face it names; an internal one cuts the conductor, so it has to be on the conductor.
- **A delta-gap port must state its direction.** For an edge port the direction can be inferred from the
  nearest conductor boundary. A label in the middle of a conductor is roughly equally far from all four
  edges, so there is nothing to infer from — and the direction is what decides which way positive
  current crosses the gap. Rotate the port to point the way current should flow across the cut.
- **An internal port states no direction at all**: its terminals are the metal and the ground plane,
  so the polarity is decided by what the port is — + is the metal, − is the plane. Rotating the label
  does nothing. It does need to be **on the metal**, because that is where its + terminal is.

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
  treated as the plane. **If you have drawn the pour, the run now measures it and tells you how big it
  is in wavelengths** — its size, its area, and, the number that actually matters, how far it extends
  *beyond* your own metal. The pour is still not meshed and reading it changes none of the numbers
  beside the note; what it buys you is knowing whether "laterally infinite" was a fair description of
  your board before you trust a result that assumed it. On a plane that is a fraction of a wavelength
  across it is not, and the errors all run the same way: no back radiation at all, and a gain and
  directivity that read high.
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
- **Keep the feed's own cross-section uniform for the calibration's run.** The solver grows the uniform
  lead it needs, but **a lead lengthens a feed, it cannot move a neighbour sideways.** A neighbour that
  **carries a port of its own** inside the calibration's run is never just a warning: if its port is at
  the **same reference plane** as yours the two are [calibrated together](#group), and otherwise the run
  is **refused**. One that carries no port is **reproduced in the standard** instead, as long as it runs
  straight past the port — see [the validity condition](#validity) directly below.
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

### The validity condition: the standard has to be the feed's own neighbourhood {#validity}

The calibration standard is a **uniform line of the port's own cross-section**. That is not a tolerance,
it is what the standard *is* — so when the standard's cross-section is not the feed's, the error box is
measured on a structure that is not the one being corrected, and the peel divides that mismatch by a₂₁²,
which is of order 10⁻⁴ at 1 GHz.

**The error that produces is not mild.** Measured against the exact cross-section answer on two straight
254 µm microstrips 246 µm apart on 0.9 mm FR-4, ports at all four ends:

| f | S₁₁ (exact) | S₁₁ (de-embedded) | S₂₁ (exact) | S₂₁ (de-embedded) |
|---|---|---|---|---|
| 1.0 GHz | −19.09 dB | −0.08 dB | **−0.11 dB** | **−22.75 dB** |
| 3.4 GHz | −9.91 dB | −0.85 dB | −0.84 dB | −13.23 dB |
| 7.0 GHz | −6.52 dB | −4.01 dB | −1.88 dB | −13.07 dB |

Non-passive at 48 of 51 frequencies, and **a finer mesh does not help** — it computes the wrong error box
more accurately. Doubling the cells per wavelength on a real board moved S₁₁ by at most 0.19 dB and left
the answer non-passive at every point.

**That table is what the two-line calibration does on this geometry when it is forced to describe it
with one scalar per port.** It is the size of the error the condition below exists to prevent — not
what circuitRF publishes for this structure, which [calibrates those four ports as a
group](#group) and comes back at the accuracy floor.

**So the clearance is a condition, it is enforced, and it is two numbers:**

| The nearest other conductor | Clearance it needs | Inside it |
|---|---|---|
| carries a port of its own, at the SAME reference plane | **5 substrate heights** | **calibrated together, as one group** |
| carries a port of its own, somewhere else | **5 substrate heights** | **refused** |
| carries no port (a passive trace, a ground pour) | **2 substrate heights** | **put into the standard** |

Both are measured across the port's own profile, over the run of line the standard reproduces, and both
scale with **substrate height** — not with line width, and not with the neighbour's width. That was
measured rather than assumed: a 4× change in line width moves the threshold by 5%, and a 5 mm ground pour
behaves like a 254 µm trace to within 2%. A neighbour carrying a port needs 2–3× the clearance of one
that does not, because it brings a second port's error box and the mutual terms with it; one number for
both cases would either refuse designs that are fine or pass designs that are 18 dB wrong.

#### A neighbour that carries no port is reproduced, not refused {#passive-neighbour}

A conductor that is merely *there* — a passive trace, an adjacent net, a ground pour — leaves **one
driven mode** at the reference plane, so the error box can stay what it has always been. The standard is
therefore **widened to contain it**: the neighbour is copied out of your own mesh, at your own gridlines,
with the gap between the two conductors reproduced as a gap, and it is driven by nothing in the standard
exactly as it is driven by nothing in your structure. The run says what it took in, and the clearance
margin then reads clear because everything inside a port's profile *is* reproduced.

On the 246 µm case above, with the neighbour's ports deleted, that takes the answer from **18.0 dB out in
S₁₁ and non-passive at 3 of 7 frequencies** to **within the two kernels' own agreement floor and passive
at every one**.

Three things are declined by name rather than guessed at, and each falls back to the refusal:

- **A neighbour that bends, ends or changes width** inside the run the standard reproduces. A standard is
  a uniform extrusion; guessing what such a conductor should become in it would be inventing metal you
  did not draw.
- **A neighbour on another conductor level.** Nothing has been measured about one, so nothing is assumed.
- **A neighbour carrying a port.** Two driven conductors support two modes at one reference plane and the
  per-port error box is one scalar — reproducing the metal does not give it a second mode to describe.
  That case has its own treatment: [a calibration group](#group).

<div class="callout warn">
<span class="label">The widened standard has a resonance of its own, and the run names it</span>
<p>The neighbour in the standard is <b>open at both ends</b>, so it is a resonator: where the standard is
a half wavelength long the neighbour's own standing wave dominates it and the two-line calibration stops
measuring a single mode. <b>Your neighbour does not resonate there</b> — it is whatever length you
drew — so this is a property of the instrument, and the frequencies within 25° of it are listed in the
run's notes. Everything outside those points comes back at the accuracy floor. Narrow the sweep past
them, or give the feeds the clearance and have no neighbour reproduced at all.</p>
</div>

A wider profile is also a **wider standard on every frequency of every run**, and standards already
dominate a de-embedded solve. On the fixture this was developed against they go from 4.6× to 9.1× the
DUT's own unknowns. The run reports both numbers.

#### Two ports on coupled conductors are calibrated TOGETHER {#group}

Where the neighbour **carries a port of its own at the same reference plane**, reproducing it is not
enough: two coupled conductors support **two modes** there, with different propagation constants and
different characteristic impedances, and no pair of scalars can describe them. Those ports form a
**calibration group** instead, and the group — not the port — becomes the unit of calibration:

- **one standard carrying every conductor of the group**, built from your own mesh at your own
  gridlines exactly as a one-conductor standard is, with a port on each conductor at each end;
- **one MODAL error box** whose blocks are N×N, solved from that standard's own cascade;
- **one propagation constant and one characteristic impedance per mode**, both reported.

On the same 246 µm pair as above — the case this whole section opened on — that takes the answer from
**22.6 dB out in S₂₁ at 1 GHz and non-passive at almost every point** to **inside the two kernels'
own agreement floor at every frequency**, with S₂₁ within 0.04 dB of exact. It is measured on a
symmetric pair, an **asymmetric** pair and a **three**-conductor group, because a symmetric pair is
the one case where assuming even and odd modes would also have worked.

**The modes are extracted from the standard, never assumed.** Even and odd is the right answer for
two identical lines and the wrong answer for almost anything else — unequal widths, unequal spacings,
three conductors — so the modal basis comes out of the standards' own cascade, and **how far apart
the modes are is reported on every run**. That number is the one everything else rests on.

<div class="callout warn">
<span class="label">Modes that are too close together are refused, not approximated</span>
<p>If two of a group's modes have nearly the same electrical length, there is no arithmetic that can
tell them apart: the answer would be smooth, plausible and wrong, which is the failure this whole
section exists to remove. The run <b>refuses</b> instead, at setup, naming the separation it measured
and the floor it needed — and the remedy is the same one the clearance refusal names: separate the
feeds, or move the port plane to a station where the conductors are not coupled. Measured: three
conductors of <i>equal</i> width at 246 µm are refused; the same three at 254 / 432 / 660 µm are
calibrated.</p>
</div>

A group is declined — and the run falls back to the clearance refusal — when its conductors do not
all cross one plane, when one of them carries no port (a driven conductor and a floating one cannot
share one standard), when one is not uniform over the run the standard reproduces, when the port
needed an [automatic feed lead](#feed) (a lead is a matched length of ONE propagation constant, and a
group's port region has one per mode), or when the group would be larger than three conductors.

**The group's own cost is the surprise**: its standard carries the same metal a widened one does, so
it is the same mesh and the same **9.1× the DUT's unknowns**, and a four-port coupled pair runs about
8 % slower than the two-port passive case. What it adds is one port excitation per conductor on a
mesh that was going to be solved anyway, and a second electrostatic solve with the dielectric
removed, which is what the modal impedances need.

<div class="callout note">
<span class="label">There is an application note about exactly this</span>
<p><a href="../app-notes/an01-ports-and-coupling.html">AN-01 — Coupled lines, EM ports, terminations
and de-embedding</a> is the worked version of this section: which kernel a coupled pair wants in the
first place, where its four ports go, what the calibration group prints when it forms, what to do when
it cannot, and how to compare two port arrangements without fooling yourself. It runs on a committed
fixture, so every number in it is one command away.</p>
</div>

<div class="callout note">
<span class="label">What the group's answer is referenced to</span>
<p>Each mode's characteristic impedance is <code>γ/(jωC)</code> with a modal capacitance, which is the
same construction a single line's is — so it inherits the same quasi-static limitation, and the run
reports how far the full-wave and quasi-static propagation constants disagree <b>separately</b> from
everything else. A run where that number is large has a perfectly good de-embedding and a reference
impedance that is out by about that much; they are two different things and one figure of merit would
hide which.</p>
</div>

Three things are deliberately **not** neighbours:

- **Metal on the port's own net** — a flare, a pad, the structure the port is attached to. That is what
  [the automatic feed extension](#feed) is for, and it removes its lead exactly.
- **A coplanar port's own return conductor**, and everything else inside the port's declared
  cross-section. All of it is reproduced in the standard.
- **Metal outside the run of line the standard reproduces.** A coupled section several millimetres down
  a clean feed is part of your circuit, not part of the port's neighbourhood.

**Every de-embedded run reports its margin**, breached or not, in substrate heights — the variable the
error was measured to follow. There is deliberately **no error bound**: nothing the solve already
computes tracks the size of this error. The de-embedding residuals cannot, structurally — they are
computed from the two standards and the DUT is not an input, so they are bit-for-bit identical between a
run that is 22 dB wrong and one at the accuracy floor. σ_max detects the failure well and estimates it
not at all; it is not even monotone in the error.

What to do about a refusal, in the order worth trying: check whether you need the full-wave kernel at all
(a uniform cross-section is solved exactly and far more cheaply by the
[uniform-line kernel](#can-cannot)); move the **ports** to where the line is already isolated and
de-embed the extra length in the circuit; or give the feeds an isolated run in the artwork. The two
switches in [Solver options](em-setup.html#deembedding) are the ways to get a number out of the geometry
as drawn — the raw solve, or the de-embedded answer with the file marked.

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
So a sweep costs the number of points times the cost of a point. Sample finely enough to see every
feature and you have paid for that resolution across the whole band; sample coarsely and the sweep is
cheap but thin. Adaptive sampling breaks that link: it lets you **ask for a fine grid and pay closer to
what a coarse one costs**, because it solves only where the response is actually doing something.

### How it works

1. Solve a sparse subset of the requested frequencies.
2. Fit a **complex cubic spline** to what has been solved.
3. Solve a **midpoint** and compare it with what the model predicted.
4. Where they disagree, add samples there and refit. Where they agree, stop.

The tolerance is agreement to 10<sup>-3</sup> in |S|. Four properties are worth knowing:

- **Both ends of your sweep are always solved, and so is a DC point.** The first and last frequencies
  you asked for are the refinement's own starting samples, so neither end is ever modelled. A 0 Hz
  point is not part of the sampling at all: it is taken out of the sweep and answered as a conduction
  network — resistance only, no reactance — so the bias path an HB or s-parameter run reads off it is
  solved, not interpolated. Points low enough that the full-wave fit has no valid range carry that
  same answer, and the run says which ones did.
- **The published sweep is always exactly the grid you asked for.** Adaptive sampling changes which
  points are *solved*, never which are *reported*.
- **Every solved point carries the solver's own result, unchanged.** Nothing that was actually solved is
  replaced by the model.
- **It never adds a frequency you did not ask for.** So it cannot rescue a feature that falls *between*
  two of your requested points — a resonance narrower than your frequency step is lost with the setting
  on or off. Resolving a feature is the grid's job; adaptive sampling only makes a fine grid affordable.
  The one exception is the [resonance search](#resonance-search), which is off by default and which
  flags every point it adds.

### What the tolerance trades

Tighter tolerance means more solved points, so more time, and a curve that tracks the true response more
closely between the ones you asked for. Looser means fewer solves and a curve that may smooth a feature
the solver would have found.

Two things to know before turning either knob. The tolerance is a **local stopping test, not an error
bar**: it is checked at the midpoints the refinement probes, and the worst realised difference against a
fully-solved sweep can be up to about ten times it. And **reducing the number of requested points is
usually the wrong move** — with adaptive sampling on, how many points get solved is set by the structure
rather than by your grid, so a coarser grid mostly costs you resolution without saving much time. On a
20 mm microstrip over 1–20 GHz, asking for 401 points and letting it sample adaptively is *faster* than
asking for 101 and solving them all.

### How to tell it converged, and what to do if it did not

The run reports how many points it solved out of how many you requested. Two checks:

- **Solve fraction.** A response that is smooth *on the grid you asked for* converges after solving a
  fraction of it. A run that solved nearly every point is telling you the response is not smooth on that
  grid — which is information, not a failure, and on a wide band over an electrically long structure it
  is the normal answer, because the transmission phase alone rotates a long way between neighbouring
  points.
- **Re-run with the sampling off.** If the curve does not move, it converged. This is the direct test and
  it costs one full sweep; it is worth doing once per new class of structure, not once per run.

If it will not settle, the usual cause is a genuine sharp resonance. Narrow the band around it and sweep
that region on its own rather than fighting the whole span — or turn on the
[resonance search](#resonance-search), which is built for exactly this case.

Adaptive sampling applies to the full-wave kernel only. The quasi-static kernel evaluates in closed form
after one solve, so there is nothing to sample adaptively, and the control says so rather than sitting
there greyed out with no explanation.

## The resonance search {#resonance-search}

### The problem it solves

Adaptive sampling bisects *the grid you gave it*. That is what makes every published point yours, and
on a high-Q structure it is also what puts the answer out of reach: if the resonance is narrower than
your frequency step, there is no point refinement is allowed to look at that is near it. The symptom is
a sweep that solves nearly every point you asked for and still reports that it did not converge — all
of the cost, none of the saving, and the feature still invisible.

You cannot fix that by refining harder. Either the grid gets finer, or something is allowed to look
between its points.

### What it does

**The resonance search is the one setting in circuitRF that lets a sweep publish a frequency you did
not ask for.** It is off by default. With it on:

1. It looks for **Im(Z<sub>in</sub>) crossing zero** — which costs no solve, because it reads the same
   model adaptive sampling already built.
2. It **brackets each crossing by bisection**, solving as it goes, until f₀ is pinned to 1 part in
   10<sup>4</sup>.
3. It reports **f₀, Q, the resistance at resonance, the half-power bandwidth and the measured −10 dB
   bandwidth** as a diagnostic — so "where is it" comes back as a number rather than a curve you have
   to read off.
4. It then spends what is left of its budget **resolving the shape** of the curve around each
   resonance, on the same |ΔS| criterion adaptive sampling uses.

Three properties are worth knowing:

- **Every added point is flagged.** Your own grid is published exactly as it was — same frequencies,
  same values — with the found points spliced in between, each marked as found rather than requested.
- **It finds all of them.** A patch has higher-order modes; the search reports every resonance in the
  span, not the first or the strongest.
- **The number of added solves is capped, and the cap is reported when it binds.** A full-wave point
  costs tens of seconds, so this is a budget you are spending deliberately.

Q here is the **resonator's own Q** at that port — radiation and loss, everything inside
Z<sub>in</sub> — not the loaded Q of a matched system. It is the number that sets the bandwidth you can
achieve before any matching network is designed.

### What it cannot do

**It needs a sign to bracket.** The search finds a resonance your *solved* points straddle in sign; a
resonance whose entire reactance swing falls between two neighbouring solved points leaves nothing for
it to notice. When it finds nothing it says so in those words, rather than implying there is nothing
there.

So the search does not remove the need for a sensible grid — it removes the need for that grid to
**resolve** the resonance. Asking for a few hundred points across the band you care about, with both
adaptive sampling and the search on, is the combination this is built for: the grid notices the
feature, the search pins it, and adaptive sampling keeps you from paying for the rest of the band.

It needs adaptive sampling on, because it seeds itself from the model refinement builds. With adaptive
sampling off, the control is disabled and says why.

## Conformal boundary cells {#conformal}

### What it is

The mesh is a rectangular tensor-product grid. Where a conductor's edge is oblique or curved, the grid
has to decide what to do with the cells the edge passes through. Two options:

- **Staircase** *(the default)* — a cell is either in the metal or not, so a diagonal becomes a
  staircase.
- **Conformal** — the boundary cells are **cut** to follow the metal: one straight cut through an
  otherwise rectangular cell.

{{ui: mom-conformal-vs-staircase}}

The setting is **Boundary cells** in the EM Setup panel's Surface mesh group, and it is the only thing
that differs between those two pictures. Look at the flank rather than at the interior: the two meshes
are the same grid, and what changes is what happens to the cells the outline passes through.

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

## Mesh as merged shapes {#merged-shapes}

### What it is

The surface mesh is a tensor-product grid, so every gridline crosses the whole part. The graded fan
one edge raises — a notch corner, a junction, a bend — runs the length of its gridline, and far from
that edge the cells it produces are much finer than the field needs. On real artwork over half the
unknowns can sit in such cells.

**Mesh as Merged Shapes**, in the EM Setup panel's Surface mesh group, keeps that grid and solves it
in a coarser basis where it is spread: along each row and column, rooftops lying in cells more than
four times finer than the local field asks for are grouped into runs along their own direction, and
one merged shape replaces every rooftop between two neighbouring runs. Three things never merge:
anything near the feature that raised a line, anything within the ports' calibrated end runs, and
anything within one conductor width of a corner of the artwork — a junction, a step, a bend or a jog —
and where the mesh knows the current's direction (the transmission-line mesh) only rooftops
transverse to it merge. The merged basis is a subspace of the elemental one, so current stays
continuous at every join by construction. The mesh overlay draws what is solved: an internal edge is
drawn only where it is still the face of its own unknown, so the picture matches the unknown count
beside it.

**It is on by default.** It applies to a single conductor level solved densely; with the accelerated
solve on, or on a layout with more than one metal level or a via, the checkbox is greyed, the
elemental mesh is solved and the mesh notes say so.

### What it buys, and what it costs — measured

| | Elemental unknowns | Merged | Saving | Worst \|ΔS\| against the elemental mesh |
|---|---|---|---|---|
| Patch Antenna example, 20 cells/λ | 1,981 | 1,850 | 7 % | 3 × 10⁻⁵ |
| An internal test geometry, transmission-line mesh | 11,131 | 10,539 | 5 % | < 0.001 in every entry |
| An edge-coupled bandpass filter | 2,267 | 2,051 | 10 % | < 0.001 |
| A branch-line coupler | 2,034 | 1,899 | 7 % | < 0.001 |
| A stepped-impedance lowpass | 4,475 | 4,272 | 5 % | 0.002 |

The corner and direction exclusions are what make the last column read like that. Without them the
test geometry merged 23 % of its unknowns and paid for it: its two output reflections, near-total at
|S| ≈ 0.9, turned by 4 to 7° while every transmission moved by under 0.02 — the merged rows inside its
staircased bends and a shallow jog carried the current itself rather than its transverse part. With
them, the saving is modest — a few per cent of the unknowns on every geometry measured — and the wall
clock moves less than that, because the calibration standards and the frequency-independent geometry
cache are shared and are most of a run below a few thousand unknowns.

<div class="callout warn">
<span class="label">What to expect</span>
<p>A smaller matrix, the same answer to the mesh's own accuracy, and a mesh overlay that shows the
merged basis rather than the grid. If you want the elemental solve for a comparison, turn the setting
off; the <code>.snp</code> provenance hash carries it, so a result solved one way reads as stale under
the other.</p>
</div>

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

The full-wave matrix is dense, complex and symmetric, and only its lower half is stored: N unknowns
is about N² × 8 bytes. **The matrix is most of what a frequency point costs; the
frequency-independent geometry cache and the working space the fill uses while it builds the matrix
are the rest** — so the figure the refusal quotes, and the one below, is all three together. For a
mesh you have already meshed the geometry cache is counted from that mesh, because how much of it a
structure needs depends on how much of its geometry repeats. The factorisation itself is free of charge: it is written into the matrix
rather than beside it.

| N | Matrix | **Held while one frequency solves** | Character |
|---|---|---|---|
| 500 | 2 MB | **4 MB** | A short line or a bend lives here |
| 2,000 | 33 MB | **56 MB** | Interactive: under a second per frequency |
| 5,000 | 0.2 GB | **0.31 GB** | Seconds per frequency |
| 10,000 | 0.8 GB | **1.0 GB** | About 20 s per frequency on ten cores |
| 20,000 | 3.2 GB | **3.9 GB** | Minutes per frequency |

A de-embedded run holds more again: its calibration standards are solved at the same time as the
structure, at every frequency, and each holds a matrix and a geometry cache of its own. Every memory
figure below counts them.

**The unknown ceiling is sized on this machine's memory**: the largest mesh whose frequency point —
the structure plus, de-embedded, its standards — fits this machine's physical memory, or the
**Memory limit** in Settings ▸ Solvers when one is set. On a 16 GB machine that is about 40,000
unknowns for a structure that de-embeds little. The predicted N and the ceiling are shown before you
solve, with an estimate of the factorisation time per frequency once N is past 10,000.

**Past that memory a run is not refused; you are asked.** The mesh report carries a warning, and
Simulate asks before anything is filled: **Simulate Anyway** starts it, **Don't Simulate** does not.
`circuitrf em` (and the MCP `run` tool) starts such a run only with `--force` (`force`), and its
`--memory-limit` (`memoryLimitGB`) stands in for the Settings ▸ Solvers limit. Short of that, past 75 %
of the memory the run carries a warning. A run past its machine's physical memory is likely to swap
heavily or be stopped by the operating system.

The memory limit belongs to the computer, not to an EM setup: it is set once, in
[Settings ▸ Solvers](settings.html#em3d), and holds the planar solver and the 3D solvers alike. Time
grows faster than memory — the factorisation goes as the cube of the unknown count — so doubling the
unknowns costs about eight times as long per frequency.

Two numbers that surprise people, both measured on 1.6 mm FR-4 at 10 GHz:

- **A 4 MB matrix is not a 4 MB sweep.** At N = 552, one frequency costs about 1.7 s once the
  frequency-independent core is built — so a 101-point sweep is about **three minutes**, not "instant".
- **De-embedding multiplies that by about 4.4×.** The calibration standards are not small — on that same
  structure they measure 2.58× the DUT's own unknowns — and they are **78% of the total cost**. A
  de-embedded 101-point sweep of that structure is about 13 minutes.

That is the arithmetic behind [adaptive sampling](#adaptive) being on by default: the per-point cost went
up 4.4× and the number of points did not.

The **accelerated solve** option buys memory first and time second: roughly 4× less working set past
about 900 unknowns, and a faster frequency point past about 1,100 — its frequency-independent state
is built once per structure and the dense factorisation's cost grows as the cube of the unknown count,
so the gap widens with size (about 2× at 1,900 unknowns, an order of magnitude at 3,700). Below the
crossover it is somewhat slower than the dense solve. On a **single-metal-level structure with no vias**
its unknown ceiling is never below 12,000; a multi-level or via-bearing structure runs accelerated
against the dense ceiling.

<div class="callout note">
<span class="label">It covers de-embedding's own reference-impedance step too</span>
<p>Working out a de-embedded result's reference impedance needs a separate static calculation on each
calibration standard, and a standard reproduces your port's own cross-section — so a <em>wide</em>
port's standard can be as large as the whole structure. With the accelerated solve on, that step is
accelerated as well and judged against the same ceiling as everything else. If a run is still refused
because a calibration standard is too large, coarsen the mesh at the port.</p>
</div>

## The accelerated solve, and when to use it {#aim}

**Accelerated solve** is a checkbox in the EM Setup panel, off by default. It changes *how* the linear
system is solved, not *what* is solved: the same mixed-potential integral equation, the same mesh, the
same basis functions, the same Green's function, checked against the dense path to its own accuracy
gates. What changes is that the matrix is never formed.

### What AIM is

The method is the **Adaptive Integral Method (AIM)** — the standard grid-based acceleration for
method-of-moments problems, and the same family as the pre-corrected FFT and IE-FFT schemes you will
find in the literature.

The idea in one paragraph. A dense MoM matrix is expensive because every basis function talks to every
other one, and the direct solve costs O(N³). But at any useful distance a basis function's field is
indistinguishable from that of a small cluster of point sources carrying the same low-order multipole
moments. AIM exploits that in three steps:

1. **Project.** Lay a *uniform* auxiliary grid over the structure and replace each basis function, for
   far-field purposes only, by weights on a small block of grid nodes chosen to reproduce the same
   moments. circuitRF projects three densities per basis — the x̂ current, the ŷ current, and the charge
   ∇·f — because the operator has two blocks with two different kernels and projecting the current
   without its divergence would accelerate half of it and quietly leave the rest dense.
2. **Convolve.** On a uniform grid the interaction is a convolution, so the whole far field is three
   **FFT convolutions** per matrix–vector product — two for the vector potential, one for the scalar —
   rather than an N² sum.
3. **Correct.** Near pairs — where the multipole approximation is not good enough — are computed
   *exactly*, as they are on the dense path, and the grid's approximation of them is subtracted back
   off. That correction is sparse and O(N).

The system is then solved iteratively (GMRES), preconditioned by the near-field block's own sparse LU.
No dense matrix is ever formed and nothing dense is ever factored.

Two details worth knowing, because they are the reason this works on a real mesh:

- **Your mesh is not the grid.** circuitRF's mesher is edge-graded and conformally cut, which is what
  buys accuracy at low unknown counts — a uniform mesh needs roughly twenty times the unknowns to
  match it. AIM keeps that mesh untouched; only the *auxiliary* grid it projects onto is uniform. This
  is not the "uniform mesh makes the matrix Toeplitz" trick, which would cost you the grading.
- **A non-converged solve throws.** GMRES has an iteration cap, and reaching it raises an error rather
  than returning a current distribution that is smooth, plausible and wrong. That is the backstop
  behind the higher unknown ceiling.

### When to turn it on

**Turn it on when the structure is large.** The switch buys memory first and time second, and both
thresholds are measured rather than estimated:

| Where you are | What the accelerator does |
|---|---|
| Below ~900 unknowns | Nothing useful. Working sets are megabytes either way. |
| Above ~900 unknowns | Roughly **4× less working set** than the dense path. |
| Above ~1,100 unknowns | Also **faster per frequency point**, and the gap widens with size: about 2× at 1,900 unknowns, an order of magnitude at 3,700. |
| Above 10,000 unknowns | Holds a fraction of the dense path's memory, and its ceiling is never below 12,000 on a single-level structure. |

Concretely, the cases where the answer is yes:

- **The mesh is past the unknown ceiling your memory allows** and the structure is on one metal level.
  The refusal or warning names the switch.
- **A long or wide board at a fine mesh** — anything where N is in the thousands. Measured on a
  length-growth ladder to N ≈ 12,900, the accelerator's working set went 53 MB → 188 MB; the dense
  matrix alone at that size would be 2.3 GB, which is why it is structurally unreachable.
- **A de-embedded run whose calibration standard was refused.** A standard reproduces your port's own
  cross-section, so a wide port's standard can be larger than the DUT. Both the standards'
  frequency-domain solves and their static capacitance step are accelerated, and are judged against the
  same ceiling as the structure.
- **A long frequency sweep on a big structure**, where the frequency-independent projection state is
  built once and reused at every point.

### When to leave it off

- **Small structures.** Below about 1,100 unknowns it is 1.3–1.5× *slower* than the dense solve, and it
  saves memory you were not short of. A bend, a short line, a single stub — leave it off. This is why
  off is the default.
- **A multi-level or via-bearing layout, if you were hoping for headroom.** It runs accelerated, as a
  bordered system, and is faster and smaller, but its cost is set by how many vertical unknowns it
  carries rather than by N, so it is judged against the dense ceiling whatever the switch says.
- **A cross-section (quasi-static) setup.** There is no dense system to accelerate — that kernel's
  solve is closed-form per frequency — so the checkbox is disabled there too.
- **When you are checking a result you do not trust.** The two paths are separate implementations of
  the same operator, so running a suspect point both ways is a genuine cross-check. They agree to the
  accelerator's own gates; a disagreement is worth reporting.

### What it does not do

It does **not** change the mesh, the ports, the de-embedding or the physics, so it cannot fix an
accuracy problem — a mesh too coarse to resolve a gap is equally wrong on both paths. It does not raise
the ceiling for multi-level geometry. And it is not a licence to ask for an arbitrarily fine mesh: the
ceiling was set from a length-growth ladder that stayed healthy to N ≈ 12,900, while *refining the
resolution* at a fixed footprint was measured pushing GMRES from 5–8 iterations to 372 by cells/λ = 120
and failing to converge at 140. Growing the structure is well behaved; over-refining a small one is
where the iteration count goes.

## What the engine refuses, and why a refusal is better {#refusals}

A wrong EM answer is smooth, plausible and expensive — it goes into a design, and the board comes back
wrong. So this engine refuses rather than extrapolating, and every refusal names the specific feature
and says where the capability arrives.

The refusals you are most likely to meet:

| Refusal | What to do |
|---|---|
| **"This geometry has a bend at (x, y)"** — from the quasi-static kernel | Nothing: the full-wave kernel takes it. This message means the cross-section extractor declined, which is how the two kernels divide the work. |
| **The predicted unknown count exceeds the ceiling** | A warning, not a refusal: Simulate asks, and **Simulate Anyway** starts it. To fit, coarsen the mesh or simulate less of the structure. See [the budget](#budget). |
| **"Port n is ambiguous"** | Move the label off the corner onto a clean end face. |
| **Two ports naming the same number** | Renumber one. |
| **"Port n is an internal delta-gap port… not ON any conductor"** | An internal port cuts the metal, so it has to be placed on the metal. Move the label onto the conductor, or make it an edge port if you meant the end. |
| **"…an internal delta-gap port with no direction on it"** | Rotate the port to point the way current should flow across the cut. There is no nearby conductor end to infer a direction from in the middle of a trace, and the sign is not guessed. |
| **"Port n is an internal port… not ON any conductor"** | An internal port's + terminal is the metal, so it has to be on the metal. Move the label onto the conductor. |
| **"…there is nothing to build its path to the ground plane from"** | The rare case: no via drawn, no default via size in the technology, and no substrate height to fall back on. Draw a via, or give the technology a default via size. |
| **This run has no ground plane** | Every port's negative terminal is the stackup's ground-designated conductor, so a stackup without one cannot be solved at all. See [The stackup](stackup.html). |
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
<span class="label">It looks like it goes to ground. It does not.</span>
<p>Port 3's two terminals are the two lips of the cut — neither of them is the ground plane. The
schematic draws every port of an N-port against a shared ground because that is how an N-port is
written down, not because one lip is grounded.</p>
<p>What matters is the constraint, and it is the right one: terminating port 3 with an impedance
imposes <em>V₃ = −Z·I₃</em> on the <strong>gap</strong> voltage and the current <strong>crossing the
gap</strong>, which is exactly "put Z into the cut". Ground here is bookkeeping.</p>
<p>For a component that genuinely does return to ground — one that takes current from the trace down
to the ground plane — the port you want is an <strong>internal port</strong>, placed on the metal
where it attaches. Same schematic step, different port: there, port 3's − terminal really is the
plane.</p>
</div>

### Putting a component from the metal down to ground

Same three steps, one port type further:

1. Draw the conductor. (You may draw the via to ground where the component attaches, and the port will
   use it — but you do not have to; the solver builds that path if it is not there.)
2. Put edge ports where power enters and leaves, and an **internal port** where the component attaches.
   Three ports again, so the run writes an `.s3p`.
3. Connect the component between that port and ground in the schematic.

Port 3's + terminal is the metal and its − terminal is the ground plane, so the connection in the
schematic means what it looks like this time. The EM run models the whole path down to the plane,
**including its inductance**; your component model is only the component. Which conductor the plane is
comes from [the stackup](stackup.html).

Two practical notes, for either kind of internal port:

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

{{ui: mom-bend-layout}}

### 2. Set the stackup

Nothing to do. The stackup comes from the technology, and the EM Setup panel shows it back to you:
`FR-4 1.6 mm εr 4.4 tanδ 0.02` between `Top Copper 35 µm` and the ground plane. Check the **Ground
reference** row reads the ground plane and not something else.

{{ui: mom-bend-stackup}}

The cross-section prints every thickness in the technology's own display unit, which on this board is
**mil** — so the core reads 62.99 mil, which is the 1.6 mm above, and each copper reads 1.378 mil,
which is 1 oz. If anything there is not what you expected, that same drawing is live at the top of the
Technology Editor's **Stackup** tab: click a band to land on its fields and change them in place — see
[The stackup](stackup.html#cross-section).

That is the whole stack this run sees. The soldermask, the silkscreen and the outline are drawing
layers with no stackup band, so they are not in the solve at all — see [The stackup](stackup.html) for
which drawing layers become physics and which do not.

### 3. Place ports

Port tool; click the horizontal arm's left end face, then the vertical arm's top end face. Two labels,
`1` and `2`. Check the notes: each port should report the edge it resolved to and the direction current
flows in. If either says "ambiguous", the label is on a corner — move it.

Leave both reference impedances at 50 Ω.

{{ui: mom-bend-ports}}

The two ports are on **different faces** here, which is the arrangement de-embedding likes: they are
as far apart as this piece of artwork allows, and neither feed runs alongside the other. A structure
whose two ports leave the same edge is the one to look at twice — see
[De-embedding](#deembedding), whose rules of thumb are all consequences of one fact, that port-to-port
coupling is the error floor.

### 4. Choose a mesh

Leave the mesh on its defaults for the first run and press **Mesh** — not Simulate. Read the mesh
report:

- **Unknowns**: expect a few hundred. Well under the ceiling.
- **The largest cell, and what set it**: the report names whichever of the two caps was binding, which
  is the question every later mesh change turns on.
- **The finest cell**: a small fraction of 114 mil — that is the edge mesh doing its job. The notes say
  how many graded cells it put at each edge and what they cost.

{{ui: mom-bend-mesh-settings}}

Those are the numbers "defaults" means, and the summary under them is the report. The two you will
change first if you change anything are **Cells per wavelength** and **Cells across conductor** — and
which of the two actually binds depends on the geometry, which is what **This metal is** decides. On
this structure the two land within 2 % of each other — λ_g/20 is 28.1 mil and the 114 mil width over
4 cells is 28.5 — so the wavelength cap wins by a whisker, and the report says which.

Pressing Mesh also draws the grid over the artwork, which is worth looking at once:

{{ui: mom-bend-mesh}}

The arithmetic behind it: at 10 GHz on this stack λ_g is 563 mil, so λ_g/20 is 28 mil — about 14 cells
along each 400 mil arm and 4 across the 114 mil width, which the edge mesh then refines to 9 by adding
three graded cells at each edge. That comes out at **351 cells and 654 unknowns**, which is the few
hundred the step above says to expect. A number wildly different from that means something is wrong
with the geometry or the layer mapping, not with the mesh settings.

### 5. Set the sweep and run

1 to 10 GHz, 101 points. Leave adaptive sampling on. Press **Simulate**.

Expect roughly a minute or two — most of it in the calibration standards, not the structure. The run
reports how many points it actually solved.

### 6. Read the result

The result opens in the [Data Display](data-display.html). Two plots are worth making, and they answer
different questions.

**S₁₁ on a Smith chart** says what KIND of mismatch this is, which no magnitude plot can:

{{ui: mom-bend-smith}}

It is one clean spiral. At 1 GHz the point sits almost exactly at the centre — a 50 Ω line into 50 Ω
is matched, and the bend is electrically tiny — and it winds **clockwise** and **outward** from there.
The winding is the two arms' electrical length, which is why it is one full turn across the band; the
outward drift is the discontinuity itself, growing with frequency. An ideal corner between two
perfectly matched lines would sit at the centre for the whole sweep, so **everything you can see here
is the bend**, and how far out the locus has travelled by the top of the band is how much of it there
is.

**Magnitude and phase against frequency** is where the numbers are. Put |S₁₁| and |S₂₁| in dB on the
left axis and both phases on the right — the trace card has a left/right toggle for exactly this, and
without it the dB curves flatten into the bottom of a frame scaled for ±180°:

{{ui: mom-bend-mag-phase}}

| What to look for | What it means | What this run gives |
|---|---|---|
| \|S₂₁\| close to 0 dB at 1 GHz, falling smoothly | Ordinary conductor and dielectric loss on FR-4 | −0.05 dB at 1 GHz, −2.9 dB at 10 GHz |
| \|S₁₁\| low at 1 GHz, rising with frequency | The bend's shunt capacitance beginning to matter | −39 dB at 1 GHz, −7.1 dB at 10 GHz |
| A smooth ∠S₂₁, roughly −βℓ | The electrical length of the two arms | −36°/GHz at the bottom of the band steepening to −45°/GHz at the top, 360° in all — one wrap. The steepening is dispersion |
| Σ\|S\|² slightly below 1, smooth | Passive and lossy, as expected | 0.989 at 1 GHz falling to 0.70 at 10 GHz |

Your own numbers will not match these to the last digit — the mesh is a function of the cell sizes
your build's defaults produce — but every one of the four shapes should be there, and a result that
breaks one of them is a setup problem rather than a physics one.

### 7. Sanity-check it against the circuit model

This is the step people skip and should not.

Build the same thing in a schematic from [MLIN](components.html#mlin) and
[MBEND](components.html#mbend) with the same widths, lengths and substrate, and run an S-parameter
analysis over the same band. Overlay the two in one Data Display — the EM result is a Touchstone file,
so [add it as a second data source](data-display.html#free-floating) and put both traces on one plot.

Give each MLIN **286 mil** of length, not 400: the bend model owns the corner square, and its reference
planes are that square's two edges. 400 − 114 = 286 either side. Leave every substrate parameter
unstated — the microstrip family's own defaults are 1.6 mm of ε<sub>r</sub> 4.4 FR-4 with tanδ 0.02 and
35 µm of copper, which is this stackup exactly, and restating them is a second place for the two to
disagree.

{{ui: mom-bend-em-vs-circuit}}

They should agree closely at the bottom of the band and separate at the top, and they do: on this run
the two S₁₁ phases are 2° apart at 1 GHz and 46° apart at 10 GHz, and the two magnitudes cross at
4.4 GHz. Below the crossing the closed-form bend reads a few dB **more** reflection than the solve
(−29.9 dB against −39.4 dB at 1 GHz — a difference between two numbers that are both negligible);
above it the solve keeps climbing while the model levels off, and by 10 GHz the model is reading
−8.0 dB where the structure gives −7.1 dB. **That divergence is the whole reason to own an EM
solver**: the formula was fitted on a family of bends and has a validity range, and the top of this
band is at the edge of it.

**If they disagree at 1 GHz, something is wrong with your setup, not with the physics** — check, in
this order: the line width (is it really 50 Ω on this stack?), the substrate (does the schematic's
MLIN carry the same ε<sub>r</sub> and h?), the arm lengths (286, not 400), the ground reference, and the
port sides.

### 8. Refine once

Raise cells per wavelength by 1.4× and re-run. If |S₁₁| at the top of the band moves by less than you
care about, you are converged. If it moves a lot, refine again — and if it moves *differently* each
time on a mitred bend, turn on [conformal boundary cells](#conformal).

### 9. Run it again without the GUI

Once the setup is right, it does not need the window any more. `circuitrf em Bend.cem` runs the same
extraction, the same mesh and the same solve, and writes the **same two files** to the same place — so
a re-extraction after a technology edit is one line in a script, and the schematic that references the
Touchstone picks the new result up with no further action. See
<a href="cli.html#em">the command line, EM section</a>.

<p class="small">See also: <a href="em-setup.html">EM Setup</a> — the panel, control by control ·
<a href="layout-editor.html">The Layout Editor</a> · <a href="cli.html#em">Running an EM setup
headless</a> · <a href="wbond.html">wBond</a> (3D bondwires) ·
<a href="components.html#mlin">The microstrip component family</a> ·
<a href="data-display.html">The Data Display</a> ·
<a href="../app-notes/an01-ports-and-coupling.html">AN-01</a> — coupled lines, where their ports go,
and how two coupled feeds are de-embedded together.</p>
