---
title: wBond
slug: reference/wbond.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > wBond
lede: Bondwire arrays: geometry, inductance, the 3D kernel, and S-parameters out.
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What wBond is: a bondwire model</a></li>
<li><a href="#schematic">The schematic side: one symbol, one pin pair per array</a></li>
<li><a href="#layout">Drawing wires: the layout view</a></li>
<li><a href="#loop-span">Loop height and span</a></li>
<li><a href="#alt-drag">The alt-drag</a></li>
<li><a href="#arrays">Arrays, and editing a whole group</a></li>
<li><a href="#physics">How the inductance is computed</a></li>
<li><a href="#array-basis">The array-basis reduction, derived</a></li>
<li><a href="#capacitance">Capacitance, Use Capacitance and ε<sub>r</sub></a></li>
<li><a href="#limits">What the model does not include</a></li>
<li><a href="#kernel">The 3D MoM kernel, and how it solves fast</a></li>
<li><a href="#fem">MoM and FEM, compared honestly</a></li>
<li><a href="#sparams">S-parameters out: lumped and distributed</a></li>
<li><a href="#parameters">Parameters</a></li>
<li><a href="#files">The .wBond file, and DXF</a></li>
<li><a href="#toolbar">The wBond toolbar</a></li>
</ol>
</nav>

## What wBond is: a bondwire model {#what}

A bond wire, or bondwire, is a thin curved conductor arcing through air over a ground reference. Below
a few GHz you can call it 1 nH and move on; above that its inductance depends on the loop you actually
bonded, on the wires beside it, and on how far the return path is — and by then it is often the
dominant element in your match.

**wBond models the bondwire geometry you drew.** You draw the wires over the pads they land on, group
them into arrays, and the component computes each array's inductance, the mutual inductance between
arrays, and the capacitance to the plane below — as a circuit element you simulate with, or as a
Touchstone file.

Everything in the fast path is **frequency-domain, quasi-static and closed-form**: no meshing, no
solver, no Sommerfeld integral. That is what lets it re-solve inside a drag. A **3D method-of-moments
kernel** sits behind it for when the quasi-static assumptions run out; both are described below. It is a
bondwire solver and nothing else — the geometry it knows about is wires, pads and one ground plane.

## The schematic side: one symbol, one pin pair per array {#schematic}

{{ui: wbond-symbol-arrays}}

A wBond component's symbol is **generated from the design it carries**. Each array becomes one pin pair
named after the array, so the figure above — arrays `G1`, `G2`, `D1` and `D2` — has four pin pairs with
those names. Rename an array and the pin renames with it.

<div class="callout note">
<span class="label">Wires are designed in the layout view, not in the schematic</span>
<p>The symbol is the <b>circuit-side handle</b>: it is where the arrays join your netlist and where the
component's parameters live. <b>The geometry lives in layout</b> — where the pads are, how high each
loop flies, how far apart the wires sit. There is no way to draw a wire on a schematic, and that is
deliberate: a bond wire's inductance is a property of its position in space.</p>
</div>

**The `REF` pin, and why it is not decoration.** An array's inductance is a **loop** inductance, and a
loop needs a return path. Two legitimate configurations:

- **Ground plane enabled** (the default). The return is the plane; the image terms in the physics
  *are* the return path. `REF` ties to the plane's net.
- **Ground plane disabled.** You must nominate one or more arrays as the return — downbonds. The
  reduction then runs on the remaining arrays against those, and the ground wires get their real
  inductance and coupling instead of a free perfect plane.

**The component refuses to stamp when the plane is off and no array is declared as the return.**
Quoting "the inductance of this bond wire" with no stated return path is the single most common way a
bondwire model is wrong, so it is refused rather than guessed.

With [capacitance on](#capacitance) the shunt charge has to leave through something: it stamps to `REF`
when that pin is exposed, and to node 0 otherwise.

## Drawing wires: the layout view {#layout}

{{ui: wbond-layout}}

In a workspace, **a wBond is the wire layer of a layout cell**. You get the ordinary layout editor —
its technology, its snap, its display unit, its DRC — with wire tools added: draw a wire, rotate one,
transform one, and the two dock panels below. The pads are ordinary layout artwork, because that is
what they are: a wire flies *over* a layout, it does not replace it.

The wires follow the layout's own **snap pitch and display unit**. There is one Snap box and one Unit
box in that editor and they govern the wires too — see [Units](units.html).

A wire is a **polyline**: its points *are* its shape. There is no separate named loop object a wire
binds to, so two wires never share a shape by reference and any wire can be reshaped on its own.

## Loop height and span {#loop-span}

{{ui: wbond-profile}}

The **Wire Profile** panel is the side view: height against distance along the wire. It is where the
two defining numbers are visible, and it carries a plane selector — `Auto`, `XZ`, `YZ` or any angle in
degrees — because an array bonded at 37° is ordinary and a foreshortened picture would lie about it.

<div class="callout note">
<span class="label">The two definitions, exactly</span>
<p><b>Loop height is the wire's maximum z minus its minimum z.</b> Not the rise above the chord. In
chip-and-wire the two feet are usually at different heights — a die pad up to a substrate lead — so the
straight line joining them is tilted, and the crest's height above <i>that</i> is smaller. A
wire-bonder is set up against the first number: loop height is what an operator measures from the lower
pad to the top of the loop.</p>
<p><b>Span is the XY distance between the two feet. There is no z in it anywhere.</b> It is a plan
distance, so raising a loop does not change its span.</p>
</div>

Two consequences worth knowing before you type a number:

- **A wire's loop height can never be below its own foot drop.** With the feet |z₁ − z₂| apart, even a
  dead-straight wire measures that much. A smaller request is clamped to the floor rather than refused
  (the wire is perfectly drawable) and rather than arched upward to fake the number.
- **Level feet hide both distinctions.** If your test case has both feet at the same z, loop height and
  rise-above-chord coincide and so do span and chord length. The ordinary chip-and-wire case is the one
  that separates them.

## The alt-drag {#alt-drag}

<div class="callout warn">
<span class="label">Hold <kbd>alt</kbd> and drag in the Wire Profile view: it scales LOOP HEIGHT AND SPAN together</span>
<p>This is the gesture the whole editor is built around. Without <kbd>alt</kbd>, dragging moves a point.
With <kbd>alt</kbd> held, the drag <b>scales the wire</b> — vertical travel scales the loop height,
horizontal travel scales the span, and a diagonal drag does both at once, live, every frame.</p>
</div>

The details that make it predictable:

- **Both axes, independently, every frame.** A diagonal alt-drag really does both; neither axis is
  guessed from the first few pixels of travel.
- **It scales the whole array group**, not the one wire under your hand. A bond group is one loop
  program on one bonder, and the profile view draws the group as one superimposed shape.
- **The anchor is the foot further from your grab**, so the end you are pulling is the end that moves.
- **The result lands on the snap pitch** — the *span* and the *loop height* are what snap, not the
  cursor and not the scale factor. That is what makes an alt-drag land on 30 mil instead of 29.87.
  Uniquely in the application, <kbd>alt</kbd> does *not* suppress the snap here: <kbd>alt</kbd> is what
  selects this gesture, so it cannot also mean "ignore the grid".

**In the layout view, alt-drag scales span only.** That view has no z axis for you to have meant
anything by, and the drag is projected onto the wire's own chord — so a drag across the wire correctly
does nothing.

## Arrays, and editing a whole group {#arrays}

An **array** is a named group of wires that share a pair of landing points — a gate bond group, a drain
bond group, a set of downbonds. Arrays are what the circuit sees: one pin pair, one terminal current,
one inductance.

Grouping matters physically, not just cosmetically. The reduction below assumes every wire in an array
starts on one pad and ends on another, so the array is what carries the current and the wires inside it
share it out among themselves.

**Editing is array-scoped.** The profile view's alt-drag scales every wire in the group; the group-level
loop-height, diameter and material commands apply to all of its members; and the properties inspector
edits one wire when you want that instead.

<div class="callout note">
<span class="label">If you have read an older design note</span>
<p>Wires used to bind to a named <b>loop profile</b> that several arrays could share, and editing the
profile edited every wire carrying it. That object was removed in favour of per-wire points: a wire's
own polyline is now the only truth about its shape. <b>The array is the unit of bulk editing</b>, and
that is what this page describes.</p>
</div>

## How the inductance is computed {#physics}

{{ui: wbond-inductance}}

The **Array Inductance** panel is the live readout: each array's own inductance, the mutual inductance
between every pair, and the frequency it is quoted at. Everything behind it is closed form.

<div class="callout note">
<span class="label">The formulas are Grover's</span>
<p>Every closed form in this section — the mutual inductance of two filaments in general position, the
parallel-filament special case, and the geometric-mean-distance treatment of a round conductor's self
inductance — comes from <b>Frederick W. Grover, <i>Inductance Calculations: Working Formulas and
Tables</i></b>. Use the <b>second edition</b> (Dover, which reprints the 1946 Van Nostrand text); it is
still in print and still the reference for this whole family of problems. Grover's companion work with
Edward B. Rosa, <i>Formulas and Tables for the Calculation of Mutual and Self-Inductance</i>
(NBS Bulletin, 1916), is where the same results are derived at length.</p>
<p>Nothing here improves on Grover. What circuitRF adds is the bookkeeping around them: the filament
decomposition of an arbitrary polyline, the image treatment of the ground plane, the frequency-dependent
internal impedance, and the array reduction below.</p>
</div>

**1. Every wire is a polyline; every polyline is a chain of straight filaments.** The mutual inductance
between two wires is the double sum of the mutual inductances of their filament pairs, from Grover's
two closed forms:

**Skew filaments in general position** — lengths *l* and *m*, angle *ε* between the axes, shortest
distance *d*, offsets *μ*, *ν* along their own lines:

```
M = (µ₀/4π) · 2·cos ε · [ T − Ω·d / (2·sin ε) ]

T = (µ+l)·atanh( m/(R₁+R₂) ) + (ν+m)·atanh( l/(R₁+R₄) )
       − µ·atanh( m/(R₃+R₄) ) −     ν·atanh( l/(R₂+R₃) )

Ω = atan2( d²cos ε + (µ+l)(ν+m)sin²ε , d·R₁·sin ε )
  − atan2( d²cos ε + (µ+l)·ν·sin²ε   , d·R₂·sin ε )
  + atan2( d²cos ε + µ·ν·sin²ε       , d·R₃·sin ε )
  − atan2( d²cos ε + µ(ν+m)·sin²ε    , d·R₄·sin ε )
```

with R₁…R₄ the four end-to-end distances. Note the whole bracket is multiplied by `cos ε`: M is exactly
`cos ε` times a positive double integral, so it vanishes identically for perpendicular filaments.

**Parallel filaments** — lateral separation *d*, axial intervals [0, *l*] and [*s*, *s*+*m*]:

```
M = (µ₀/4π) · [ f(s+m) − f(s) − f(s+m−l) + f(s−l) ],   f(z) = z·asinh(z/d) − √(z²+d²)
```

which is exact at ε ≡ 0 and about a third cheaper — and most filament pairs in a real array are
near-parallel.

**2. Self-inductance is the same formula evaluated against itself**, at the conductor's geometric mean
distance rather than at zero:

- **GMD = a·e<sup>−1/4</sup> = 0.7788 a** for uniform current (DC), which reproduces the internal
  inductance term µ/8π automatically;
- **GMD = a** for full skin effect, current on the surface, no internal inductance.

The shipped path uses GMD = *a* for the external inductance and adds L<sub>int</sub>(f) from the same
evaluation that produces R(f), so self and mutual share one code path and the whole frequency
dependence lives in one place.

**3. The ground plane enters by images.** The plane is a perfect conductor at z = 0; every wire is
mirrored through it, and

```
L_ij = M( i , j ) − M( i , image(j) )
```

The image is built by mirroring through z = 0 **and reversing traversal direction**. That single rule
gives the right sign for horizontal and vertical current alike — get it wrong and you get a
plausible-looking array inductance that is 10–30% off. As a check, a horizontal wire of length ℓ and
radius *a* at height *h* reduces to the textbook `L = (µ₀ℓ/2π)·ln(2h/a)`.

**4. Resistance and skin effect come from one dimensionless number.** The exact internal impedance of a
round wire, normalised to its own DC resistance, depends only on `q = a/δ` — the radius in skin depths,
with `δ = √(2/ωµσ)`:

```
Z_int(ω) = R_dc · (γa/2) · I₀(γa)/I₁(γa),   γ = √(jωµσ),   R_dc = 1/(σπa²)   [per unit length]
```

**The loss the model stamps is the real part of that, taken along every filament:**

```
R(f)     = ℓ · Re{ Z_int(ω) }                            ← series loss of a filament of length ℓ
L_int(f) = ℓ · Im{ Z_int(ω) } / ω                        ← the internal inductance that goes with it

R/R_dc → 1 + q⁴/48                            as q → 0   (DC: current uniform across the section)
R/R_dc → q/2 + ¼ + 3/(32q)  ≈ a/(2δ)          as q ≫ 1   (skin effect: current in one skin depth)
```

Both asymptotes are Kelvin's, and neither is what circuitRF evaluates: between roughly q = 1 and q = 4
— about 100 MHz to 1 GHz for a half-mil gold wire, which is inside the range this tool is for — the
small-q form is 5 % high and the large-q form 2.5 % high, so the shipped path uses a continued fraction
for I₁/I₀ that is exact across the whole band. They are written here because they are what tells you
*which* regime a wire is in.

Since `γa = (1+j)·q`, the ratio is a one-dimensional function — tabulated once to double precision, so
every wire at every frequency is a lookup plus two multiplies, exact to the table's tolerance rather
than to a curve fit's. The same lookup yields R(f) and L<sub>int</sub>(f).

## The array-basis reduction, derived {#array-basis}

This is what turns *N* wires into *M* circuit terminals, and it is exact under two stated assumptions.

Let **L** be the *N × N* wire-basis inductance matrix from above (symmetric, positive definite, images
folded in), and **A** the *N × M* **0/1 mapping matrix**: `A[i,k] = 1` iff wire *i* is in array *k*,
exactly one 1 per row. With **V** the per-wire voltage drops and **I** the per-wire currents,

```
V = jω L I
```

**Assumption 1 — equipotential bond pads.** Every wire in an array runs between the same two pads, so
all wires in array *k* share one voltage drop *u_k*:  **V** = **A u**.

**Assumption 2 — KCL at the pads.** The array's terminal current is the sum of its members':
**J** = **Aᵀ I**.

Substituting **I** = (jω)⁻¹ **L**⁻¹ **A u** into the second gives **J** = (jω)⁻¹ (**Aᵀ L⁻¹ A**) **u**,
so

```
u = jω L_arr J,      L_arr = ( Aᵀ L⁻¹ A )⁻¹
```

**A congruence transform on the inverse inductance matrix, inverted back.** Equivalently, with
**Γ** = **L**⁻¹, the reduction is the plain block sum **Γ**<sub>arr</sub> = **Aᵀ Γ A** — because **A**
is 0/1, that is literally "add up the sub-blocks of **Γ** belonging to each array pair".

Three properties fall out of the algebra rather than out of a tolerance:

- **Symmetric by construction.** **L** symmetric ⇒ **L**⁻¹ symmetric ⇒ **AᵀL⁻¹A** symmetric.
  Reciprocity is structural.
- **Positive definite**, provided **L** is and every array is non-empty.
- **Resistance never appears.** The reduction consumes **L** and **A** only, which is what makes
  L<sub>arr</sub> frequency-independent and cacheable.

It reduces to the classic result: *N* identical wires with self *L_s* and mutual *M* in one array give
`L_arr = (L_s + (N−1)M)/N`.

**Current sharing comes free**, and it is worth looking at: back-substituting gives
**I** = **L**⁻¹ **A** **L**<sub>arr</sub> **J**, from which **edge wires carry appreciably more current
than centre wires** — the classic array current-crowding result — and an *undriven* array tied together
at both ends carries a small circulating current, because it is a shorted turn. Both are real physics
the reduction captures with no extra machinery, and the editor renders them as a per-wire colour ramp.

## Capacitance, Use Capacitance and ε<sub>r</sub> {#capacitance}

Two parameters matter more than the rest, and they are the two most often left wrong.

### Use Capacitance

**On by default.** A bond wire has capacitance — to the plane it flies over and to its neighbours — and
above a few GHz that is what turns its terminal inductance into a function of frequency and gives it a
self-resonance at all.

The electrostatic problem is **the dual of the inductance fill**: the same filament pairs and the same
images, summed against the Coulomb kernel instead of Grover's, one charge basis function per wire:

```
V_i = Σ_j P_ij Q_j ,   P_ij = 1/(4πε ℓ_i ℓ_j) · Σ_p Σ_q [ K(p,q) − K(p, image(q)) ]
```

Turn it **off** and the component becomes a pure series impedance whose self-resonance is at infinity.
That is the right model for a low-frequency estimate and it is what every design did before this
existed — but at 20 GHz it is not a bond wire.

Cross-wire capacitance is **not** a separate switch, deliberately: dropping the cross terms biases a
multi-wire array's capacitance *high* by tens of percent, in the optimistic direction.

### ε<sub>r</sub> — the overmold

**Default 1.0, which is air.** Set it to the relative permittivity of the plastic the wires are moulded
in, and the physics change is exact and simple: a non-magnetic encapsulant leaves **L** untouched and
scales **P** by 1/ε<sub>r</sub>, so every capacitance rises by ε<sub>r</sub>, the self-resonance falls
as 1/√ε<sub>r</sub>, and the effective inductance the panel quotes rises with it.

<div class="callout warn">
<span class="label">What the single number assumes</span>
<p>It fills <b>all space above the ground plane</b> — one homogeneous medium, not a mould cap of finite
thickness with air above it. A loop that sits well inside the mould body is described well by this; one
whose apex breaks the mould surface is <i>bounded</i> by it, not modelled by it, and the number you get
is the pessimistic (high-C) end.</p>
<p>The quasi-static assumption also gets stricter as ε<sub>r</sub> rises, because the wavelength in the
medium shortens by √ε<sub>r</sub>. At ε<sub>r</sub> = 4 a 1 mm wire is electrically twice as long as it
was in air — expect the lumped and distributed models to part company sooner than they do in air.</p>
</div>

## What the model does not include {#limits}

The two assumptions behind the array reduction are good, and they are assumptions.

- **Equipotential pads.** A long lead-frame finger, or an array spanning 300 mil of a package lead, has
  real impedance between the landing points. The reduction then **over-couples** the array: it forces a
  voltage equality the structure does not enforce. **Nothing warns you**, and that is deliberate —
  there is no threshold that separates the good case from the bad one, because whether a 60 mil landing
  span matters depends on the lead's sheet resistance, the frequency, and how much of the array's total
  impedance the pad represents. The array's landing span *is* shown in the panel as an ordinary
  readout, where it informs without asserting.
- **Resistance is excluded from the inductance reduction** by construction — and that sentence is about
  ONE panel, not about the model. To be explicit, because it is easy to read the wrong way:
  - **R(f) is computed, and it is in the answer.** Every wire carries the R(f) of §4, the simulation
    stamps the exact complex reduction **Z**<sub>arr</sub>(ω) = (**AᵀZ**(ω)⁻¹**A**)⁻¹, and the exported
    Touchstone S-parameters are lossy accordingly. Nothing is thrown away.
  - **What is excluded is the ARRAY-BASIS number the panel quotes.** The reduction that turns *N* wires
    into *M* terminals — **L**<sub>arr</sub> = (**AᵀL**⁻¹**A**)⁻¹ — consumes **L** and **A** only, which
    is exactly what makes L<sub>arr</sub> frequency-independent and cacheable, and therefore what makes
    the live readout live. So the **Array Inductance** panel shows inductance per array and mutual
    inductance per array pair, and **no per-array resistance**: there is no such quantity in that
    reduction. Read R off the S-parameters, or off the per-wire properties.
  - **The panel does not report a per-array R, or an R/ωL, and that is not an omission to be fixed** —
    the number it would report does not exist in the reduction it performs. When the split matters to
    you, look at |S| or at the array's own Z(ω) from the exported Touchstone: the frequency at which
    the reactance stops dominating is visible there directly.
- **No radiation, no retardation** in the quasi-static path. A 100 mil arc is λ/10 at about 11.8 GHz;
  segmented into filaments it is a distributed ladder good well past that, but the *coupling* is
  quasi-static.
- **Proximity effect is not in the shipped resistance.** The Bessel term is the *isolated* wire. Real
  arrays at 4–8 mil pitch with 1 mil wire sit at s/a ≈ 8–16, where neighbour currents raise R above the
  isolated value. The mesher warns when any wire pair falls below **s/a = 6**.
- **The ground plane is infinite, flat and perfect.** A stepped or split ground under the wires is a
  first-order error — 30–50% on L for a plane split — which is why the reference conductor is not
  optional.

## The 3D MoM kernel, and how it solves fast {#kernel}

Behind the closed-form path is a **thin-wire method-of-moments kernel** — the Harrington/Richmond/NEC
formulation, which is the founding problem of computational EM and a very good fit for this geometry.

**What it solves.** One unknown per wire segment: the axial current. Free charge at the segment ends
pairs with it (the standard PEEC current/charge pairing). Three matrices:

- **[Lp]** — partial inductance, Neumann double line integrals over segment pairs, closed-form for
  straight filaments;
- **[P]** — coefficients of potential, free-space Coulomb kernel;
- **[Z_int]** — per-segment internal impedance from the exact round-wire Bessel solution, giving
  R(f) ∝ √f and internal L(f) with no fitting.

Assembled, that is an RLC ladder per wire plus full mutual coupling, solved for the N-port.

**How it solves fast** — four mechanisms, none of them an adjective:

1. **Unknowns scale with wire count, not with the air between the wires.** The 5 mil ↔ 300 mil pitch
   range is *a distance in a Green's function*, not two decades of volume mesh.
2. **The thin-wire approximation collapses the cross-section into an analytic kernel.** At 40 GHz a
   1 mil radius is 0.0034 λ₀ — deeply valid. Nothing has to resolve the circumference, and nothing has
   to resolve the sub-micron skin depth inside it, because the internal impedance is closed-form.
3. **The matrices are frequency-independent** in the quasi-static stage. Only [Z_int] and the
   ω-weighting move with frequency, and both are closed-form per segment — so a 1001-point sweep is one
   fill plus 1001 cheap evaluations.
4. **There is no Sommerfeld integral and no domain truncation.** The ground plane is an image; the
   radiation condition is exact. No airbox, no PML, no "is my boundary far enough".

**Sizing, so you know what you are asking for.** Segmentation is driven by geometric fidelity of the
arc rather than by wavelength — a faithful loop needs roughly 25–30 segments over a 100 mil arc:

| Case | Wire unknowns | Dense complex matrix | Factorisation per frequency |
|---|---|---|---|
| 8-wire GSGSG array | ~250 | 1 MB | instant |
| 40-wire | ~1,200 | 23 MB | milliseconds |
| 200-wire | ~6,000 | 576 MB | a few seconds |

Only the 200-wire extreme brushes the engine's unknown ceiling, and the predicted count is reported
before the solve rather than discovered at allocation time.

## MoM and FEM, compared honestly {#fem}

If you already solve bond wires in a 3D FEM tool, here is the fair comparison. **Neither table is
marketing**; both are the reason to pick one tool over the other for a given job.

### Where this kernel wins

| | Why it matters here |
|---|---|
| Unknowns scale with wire count, not with the air between them | The 5 mil ↔ 300 mil pitch range is a distance in a Green's function, not a graded 3D volume mesh |
| The 1 mil radius stops being a meshing problem | The thin-wire kernel is analytic; FEM must resolve the circumference *and* the skin depth inside it |
| The radiation condition is exact | No airbox, no PML, no domain truncation — a whole class of setup error disappears |
| Skin loss is closed-form | R(f) and internal L(f) from a Bessel expression, better than an impedance boundary on a coarse mesh |
| Re-meshing a loop is re-sampling a polyline | Sweeping loop height, pitch or wire count — or Monte-Carlo over real bonder variance — is nearly free |
| The output is already the currency | An N-port Touchstone from pad to pad, ready for a test bench |

### Where FEM wins — plainly

| | Why it hurts |
|---|---|
| **Inhomogeneous 3D dielectrics** | Mould compound, die attach, underfill. FEM assigns ε<sub>r</sub> per element and moves on; this kernel has one homogeneous medium above the plane |
| **Complex 3D metal** | Lead frames with tie bars, clips, stepped cavities, lids. Surface area to mesh grows until the advantage erodes |
| **Dense matrix** | O(N²) memory and O(N³) per frequency, against FEM's sparse. Wirebond N is small enough that it does not bind — but it is a real asymmetry |
| **Field visualisation** | This gives you currents and S-parameters, not a picture of E inside the mould cap |
| **Cavity resonance** | MoM conditions badly near a cavity mode; FEM is comfortable there |
| **Maturity** | An established 3D FEM tool is the validated reference. A newer kernel is not |

If your problem is a moulded package with a lead frame and a lid, use FEM. If it is 40 wires over a
plane and you want to sweep the loop height, this is the faster and better-conditioned tool.

## S-parameters out: lumped and distributed {#sparams}

{{ui: wbond-sparameters}}

**Export Touchstone…** writes the array network over a frequency grid you state (a bond array is
broadband and has no natural band, so the grid is yours). Two choices in that dialog change what is
being computed; the rest are formatting.

### Port basis

| Basis | What you get |
|---|---|
| **Per terminal (2 per array)** *(default)* | Every terminal is its own port, referenced to the ground plane — which is the file's own common reference node. Four arrays give an 8-port. **This is the only basis that can carry the capacitance**, because Touchstone's implicit common reference node *is* the plane the shunt capacitors return to. |
| **Per array (differential pair)** | One port per array, its two terminals as the port's + and −. Compact, and it matches the schematic symbol's port pairs — but a floating pair has no terminal for a shunt to leave by, so **a design with capacitance loses it**. The written file says so in its header. |

### Model — lumped or distributed

| Model | What it is | Cost |
|---|---|---|
| **Lumped (analytic)** *(default)* | The array-basis model above: one current and one charge basis function per wire, frequency-independent matrices. **This is what the schematic component stamps**, so an export in this mode is exactly what your circuit simulates. | Effectively instant |
| **Distributed (MoM)** | The thin-wire kernel: one current unknown per segment, so a wire is a transmission line rather than a lumped L with an end capacitance. Publishes on the terminal basis only. Segments per wire: 8 fast / 24 balanced / 48 accurate. | One dense complex factorisation per frequency point — a 201-point export of a 40-wire array takes seconds, not milliseconds |

**Which to choose.** Lumped, until the wire is electrically long: a 100 mil arc is λ/10 around 12 GHz in
air, and sooner in overmould. Above that the distributed model sees what the lumped one cannot — the
current varying along the wire — and the two parting company *is* the answer to "am I past the lumped
regime?". **Compare Distributed Model…** runs them side by side on a grid you state, which is the
cheapest way to find out where your own geometry crosses over.

## Parameters {#parameters}

On the placed component:

| Parameter | Default | What it does |
|---|---|---|
| **`IncludeCapacitance`** | `true` | [See above](#capacitance). The one parameter whose default changes the answer for a design authored before it existed. |
| **`er`** | `1` | The overmould's relative permittivity. An ordinary real expression, so it can be swept and optimised. |
| `GroundPlane` | as drawn | Enable/disable the reference plane, and its z. With it off you must nominate a return array. |
| `RefPin` | `false` | Exposes the `REF` terminal. Changes the terminal count, 2M vs 2M+1. |
| `Temp` | as drawn | Operating temperature — conductivity, and therefore R(f), depends on it. |
| `LoopHeight`, `Diameter`, `Material` | **blank** | The controlling parameters: blank means *as drawn*. Set one and it drives every wire; array-scoped spellings (`LoopHeight_G1`, `Diameter_D2`, …) drive one array. Blank is not emitted at all, so an unset parameter never reaches the engine. |
| `Source`, `File` | `Carried` | Whether the component **carries** its design or **links** to a `.wBond` on disk. |
| `Design`, `Arrays`, `SymbolPitch` | — | The carried payload, the array list, and the symbol's pin spacing. Not part of the netlist. |

<div class="callout warn">
<span class="label">Blank means "as drawn", and it matters</span>
<p><code>LoopHeight</code>, <code>Diameter</code> and <code>Material</code> ship blank on purpose. A
wBond that shipped <code>LoopHeight = 20 mil</code> among its defaults would regenerate every placed
instance's wires to 20 mil on its next run — silently rewriting geometry somebody drew.</p>
</div>

Because they are ordinary expressions, the controlling parameters are exactly what a
[parametric sweep](simulations.html#parametric-sweep) or an optimiser turns: sweep `LoopHeight_G1` and
watch the match move.

## The .wBond file, and DXF {#files}

**A design saves as [`.wBond`](file-formats.html)** — the arrays, every wire's points, the materials,
the ground plane, the temperature and the view state. A placed component either **carries** the design
(self-contained, shareable as one schematic) or **links** to a `.wBond` file (one bond drawing, several
designs). Both are supported; the `Source` parameter says which, and only *Update Layout from
Schematic* flips it.

### DXF — the bridge to the assembly house

Wires travel in and out through DXF, because assembly houses, package designers and mechanical CAD all
speak it. **The layer name is the contract:**

| | |
|---|---|
| **Layer** | **`Wires_<group>`** — one DXF layer per wire array. **The `Wires_` prefix is what identifies wire geometry on import**; the suffix is the array name, so arrays survive by name rather than by position. |
| **Wire** | one **3D polyline** (`POLYLINE` with group 70 bit 8 set, a `VERTEX` per point, `SEQEND`). Not an `LWPOLYLINE`: that is 2D by definition and would silently drop the loop height. |
| **Diameter and material** | **XDATA** under application name `CIRCUITRF_WBOND` — group 1000 is the material, group 1040 the diameter. A reader that does not know the application name ignores it, so the file stays valid everywhere. |
| **Feet** | a filled circle at each end, on the same layer, at the wire's own diameter (a `CIRCLE` plus a solid `HATCH`, because DXF has no filled-circle entity). |

**On import, wire layers are diverted from the layout entirely.** A 3D polyline on a `Wires_*` layer
becomes a wire, never a layout path; anything else on a wire layer — the foot circles — is dropped,
because it is decoration regenerated on every export. Letting it back in would grow the design on every
round trip while looking plausible each time.

**Two entry points, because they answer different questions.** A full DXF **export** writes the
reference layout *and* the wires together — that is the file you send out. **Import Wires…** reads only
the 3D polylines into the current document, leaving its layout untouched.

**Coordinates are written in the file's own `$INSUNITS`** and read back the same way. GDSII is
deliberately not offered for wires: it has no 3D polyline and no notion of a diameter, so a wire could
only be flattened to a meaningless 2D trace.

## The wBond toolbar {#toolbar}

wBond also ships as a **standalone application** — the same editor with one document in it, for people
who want the bond drawing and nothing else. Its toolbar carries the wire tools, which inside circuitRF
live on the [layout editor's toolbar](layout-editor.html#toolbar) instead:

{{toolbar: wbond}}
