# circuitRF — MoM Kernel W: 3D wirebond simulation (design + plan)

**Status:** Proposal — rev 1 · **Date:** 2026-08-04 · **Phase:** 10 (extension)

Companion to [`mom-engine.md`](mom-engine.md) §10 (formerly `layout-view.md` §10), which defines the 2.5D planar MoM arc
(kernels A → B → C, phases L6–L9). This document specifies a **fourth kernel, W**, for 3D
wirebond geometry, sharing §10's `IEmKernel` boundary, stackup model, port model, mesh viewer,
results plumbing, and validation harness.

Read §10 first. This document assumes its vocabulary and does not repeat it.

---

## 0. The one-paragraph version

A bond wire is a thin curved conductor arcing through a mostly-homogeneous region above a ground
reference. That is the founding problem of computational EM — Harrington/Richmond/NEC thin-wire MoM —
and it is a **better** fit for MoM than for FEM, by a wide margin, for exactly the geometry
circuitRF's users care about: 200-wire arrays with 0.5–1.25 mil radii, 5–50 mil loop heights, and
5–300 mil pitch. It is **not** an extension of kernel A (a 2D cross-section solver cannot see a
wirebond) and it is **not** unlocked by kernel C (a wirebond is 3D geometry, and a stepped ground is
a lateral variation — precisely what the 2.5D premise forbids). It is a separate kernel, staged the
same way and for the same reasons: quasi-static first, retarded second, layered-medium never
promised.

---

## 1. Correcting §10.1

`mom-engine.md` §10.1 currently states:

> This is the commercial 2.5D planar-MoM class of tool. It is *not* FEM and not general 3D — a
> wirebond arcing through air is out of scope by construction.

That sentence is true of **kernel A/B/C** and too strong as a statement about MoM. The reframe that
makes this a coherent extension rather than a different tool:

> **A wirebond is 3D geometry in a stratified medium. The *medium* is still 2.5D.**

There is direct commercial precedent in circuitRF's own tool class: mainstream 2.5D planar solvers both
extended planar 2.5D solvers to bondwires. §10.1 is amended in place (see §10.1a of `layout-view.md`)
rather than deleted, because the constraint it states remains correct *for the kernel it describes*.

---

## 2. Why MoM beats FEM for this specific geometry

Stated as advantages and disadvantages, because the owner has solved this geometry many times in
a 3D FEM tool and the comparison should be fair rather than promotional.

### 2.1 Where MoM wins

| | Why it matters here |
|---|---|
| **Unknowns scale with wire count, not with the air between them** | The 5 mil ↔ 300 mil pitch range is *a distance in a Green's function* for MoM. In FEM it is two decades of mesh grading through a 3D volume — the source of the tet count and the mesh-adaption fragility. |
| **The 1 mil radius stops being a meshing problem** | The thin-wire approximation collapses the wire cross-section into an analytic kernel. At 40 GHz, a = 1 mil = 0.0034 λ₀ — deeply valid. FEM must resolve the circumference *and* the 0.79 µm skin depth (gold, 10 GHz) inside it. |
| **The radiation condition is exact** | No airbox, no PML, no domain truncation, no "is my boundary far enough" question. An entire class of setup error disappears. |
| **Skin/proximity loss is closed-form** | Round-wire internal impedance is a Bessel expression: R(f) and internal L(f) are exact, better than an impedance boundary on a coarse mesh. |
| **Re-meshing a loop is re-sampling a polyline** | Sweeping loop height, pitch, wire count, or Monte-Carlo over real bonder variance is nearly free, and plugs into the existing `ParametricSweepEngine`. A 3D FEM solver re-meshes from scratch each time. |
| **The output is already the product's currency** | An N-port `.snp` from pad to pad, dropped into an HB testbench next to the device model (§10.8). |

### 2.2 Where FEM wins — state this plainly

| | Why it hurts |
|---|---|
| **Inhomogeneous 3D dielectrics** | Mold compound, die attach, underfill, encapsulant. FEM assigns εr per element and moves on. Surface MoM must work for it (§5). |
| **Complex 3D metal** | Leadframes with tie bars, clips, stepped cavities, lids. Surface area to mesh grows until MoM's advantage erodes (§7.4). |
| **Dense matrix** | O(N²) memory, O(N³) per frequency, vs. FEM's sparse. Wirebond N is small enough that this does not bind (§3), but it is a real asymmetry. |
| **Field visualisation** | MoM gives currents and S-parameters, not a picture of E inside the mold cap. |
| **Cavity resonance** | MoM conditions badly near a cavity mode; FEM is comfortable there (§6.5). |
| **Maturity** | The established 3D FEM tool is the validated reference. A new kernel is not. |

**RW1. Both tables above appear in the user documentation, not only here.** A tool that states its
own edge is trusted; one that discovers it in a support ticket is not.

---

## 3. Sizing — it is comfortable

Thin-wire MoM carries **one unknown per segment** (axial current). Segmentation is driven by
*geometric* fidelity of the arc, not by wavelength: at 50 GHz, λ/20 ≈ 12 mil, while a faithful loop
needs ~25–30 segments over a ~100 mil arc regardless.

| Case | Wire unknowns | Dense complex matrix | LU per frequency |
|---|---|---|---|
| 8-wire GSGSG array | ~250 | 1 MB | instant |
| 40-wire | ~1,200 | 23 MB | milliseconds |
| **200-wire (worst stated case)** | **~6,000** | **576 MB** | ~3 s (1.4×10¹¹ flops) |

Only the 200-wire extreme brushes §10.7's R17 ceiling of ~5,000. The fill is *cheap* because stage W1
and W2 use the free-space kernel — **no Sommerfeld integral anywhere**, which is the same property
that made kernel A attractive.

**RW2. R17's N-ceiling applies to kernel W unchanged, and the predicted N is reported before the
solve.** Adding meshed surfaces (§6) is what actually pushes the large-array case over the ceiling;
that is called out where it happens rather than discovered at allocation time.

---

## 4. Kernel staging — W1 → W2 → W3

Structurally identical to §10.3's A → B → C decision, for the same reason: the quasi-static form has
**frequency-independent matrices**, so a 1001-point sweep is one fill plus 1001 closed-form
evaluations.

### 4.1 W1 — quasi-static PEEC (the v1 wirebond kernel)

**Unknowns.** Axial current on wire segments; free charge on segment ends (the standard PEEC
current/charge pairing).

**Matrices.**
- **[Lp]** — partial inductance, Neumann double line integrals over segment pairs (Ruehli/Grover).
  Closed-form for straight filaments; the arc is a polyline of straight segments.
- **[P]** — coefficients of potential, free-space Coulomb kernel.
- **[Z_int]** — per-segment internal impedance from the exact round-wire Bessel solution, giving
  R(f) ∝ √f and internal L(f) with no fitting.
- Assemble into an RLC ladder per wire plus full mutual coupling, solve for the N-port.

**The property that makes it feel fast.** [Lp] and [P] are frequency-independent. Only [Z_int] and
the ω-weighting move with frequency, and both are closed-form per segment. Sweeping is effectively
free — the same story §10.3.2 tells about kernel A, and worth telling the user in the UI.

**Validity.** A ~100 mil arc is λ/10 at ~11.8 GHz, so a *lumped* single-L model dies around 10 GHz;
segmented into ~10 sections it is a distributed ladder good well past 100 GHz. The quasi-static
*coupling* assumption is weaker at 300 mil pitch (λ/10 at 3.9 GHz) — but the error term is largest
exactly where the coupling is smallest, so the practical damage is limited. Radiation is absent.

### 4.2 W2 — retarded thin-wire MoM

Add `e^{-jkR}` to the mutual terms and it is a genuine full-wave thin-wire solver — correct
radiation, correct far coupling, correct at any pitch. The formulation delta from W1 is small; the
*cost* delta is not: matrices refill per frequency, so a 1001-point sweep costs 1001 fills and
factorisations instead of one.

**RW3. W1 and W2 are the same kernel object with a `Retarded` flag, not two kernels.** They share
geometry, ports, meshing, junction handling and results. The flag is surfaced in the EM panel with
its cost stated ("frequency-independent — sweeps free" vs. "per-frequency solve").

### 4.3 W3 — wires in the layered stack

Needs DCIM, plus the bookkeeping for segments crossing layer boundaries (the Green's function
depends on which layer the source and observer are in). Downstream of kernel C.

**RW4. W3 is named, not promised.** §10.2 already says the schedule dies at DCIM. Nothing in the W1/W2
plan may depend on it.

---

## 5. Geometry scope — wires only, or wires plus everything else?

This is the sharpest scoping question, and it has a clean three-tier answer.

### 5.1 Tier T1 — wires only, over an image ground

Free-space Green's function plus one exact image for a flat ground plane. Ports at the wire ends. No
wire-to-surface junction problem. Produces its own `.snp`.

**RW5. T1 ships standalone and is useful standalone.** A 200-wire array N-port capturing all mutual
coupling, cascaded in the schematic with the planar structure's `.snp`, is a real instrument. It is
more than a typical commercial bondwire library does (per-wire closed-form partial inductance with limited coupling).

**RW6. The seam is documented, not hidden.** Cascading a wire `.snp` with a planar `.snp` at the
landing pad assumes a clean single-mode port there. It is not exactly — the wire's fringing field
wraps the pad. Typically a few percent on inductance, worse on pad capacitance. Most tools
effectively do this; circuitRF says so.

### 5.2 Tier T2 — wires plus meshed surfaces

Surface panels on real metal and real dielectric boundaries, keeping the free-space kernel, plus
**wire-to-surface junction basis functions**.

**This is the observation that should drive the schedule: T2 is one piece of machinery that unlocks
three separately-requested capabilities.**

| Capability | What it needs |
|---|---|
| Wire lands on a pad, coupled correctly to the planar metal | Surface panels + junction |
| Finite mold body, not a laterally-infinite slab (§6) | Surface panels (bound charge) |
| Discontinuous / stepped ground (§7) | Surface panels (free charge + surface current) + junction |

You cannot get any one of them without the junction machinery, and once it exists you get all three.

**RW7. T2 is budgeted as a single deliverable that unlocks three capabilities — not as three small
additions to T1.** It is the larger half of the total wirebond effort.

### 5.3 Tier T3 — wires plus the full layered stack

T2's surfaces plus W3's layered Green's function. Downstream of kernel C. Named, not promised.

### 5.4 How the scope is expressed in the UI

§10.3.4's `IEmKernel` already carries `Capabilities` and §10.3.3 already defines the specific-refusal
pattern. Kernel W widens `EmCapabilities` with a `Wires` flag and (at T2) a `Surfaces` flag, and the
refusal writes itself:

> *"This selection contains planar metal on layer M1. Kernel W (wirebond, T1) solves wires only —
> exclude the metal, or use the planar kernel and cascade the results."*

---

## 6. Overmold

### 6.1 The fact that shrinks the problem

**Mold compound is non-magnetic.** μr = 1 for epoxy mold compound, for air, for everything in the
package. In a PEEC formulation the mold therefore touches **[P]** and **[G]** and **never [Lp]**. The
partial inductance matrix — the dominant bondwire parasitic and the thing a PA output match actually
cares about — is completely unaffected by whether the wire is molded or bare.

Quantified for a 1 mil-radius wire 30 mil over ground:

| | Air | εr = 4 mold |
|---|---|---|
| L (external) | 0.82 nH/mm ≈ 21 pH/mil | **identical** |
| Z₀ | ~245 Ω | ~123 Ω |
| C over a 100 mil wire | ~35 fF | ~140 fF |

So mold appears as **added shunt C, added delay, and a little dielectric loss** — nothing else. The
~70 fF end-capacitance is 1.1 kΩ at 2 GHz (ignorable against 50 Ω) and 227 Ω at 10 GHz (not).
Dielectric loss is minor: G = ωC·tanδ ≈ 1.8×10⁻⁴ S at 10 GHz with tanδ = 0.02, i.e. ~5.7 kΩ shunt,
well under the conductor loss.

**RW8. The documentation states this asymmetry explicitly: inductance is exact regardless of the mold
model; capacitance and delay carry the mold model's error; loss is dominated by the conductor, not
the dielectric.** That tells a user how much to trust the result and where.

This asymmetry is also the reason the quasi-static staging is right: the part the mold affects is
exactly the part W1 computes with a static kernel, where dielectrics are cheap. In a full-wave
formulation, dielectrics are expensive.

### 6.2 The ladder of mold models

| # | Model | Cost | Good when | Fails when |
|---|---|---|---|---|
| 1 | **Homogeneous fill** — ε₀ → ε₀εr, k₀ → k₀√εr | A constant | Cap thick relative to loop height, wire well inside | At the mold-air surface; below the wire (substrate, not mold); thin caps |
| 2 | **Quasi-static image series** — mold slab over ground, air above | A loop inside the fill | Laterally large cap; captures the mold-air interface **exactly** | Finite cap edges; sidewall proximity; the die surface |
| 3 | **Bound charge on mold surfaces** ← recommended | ~600 panel unknowns | Everything below | Full-wave (it is a quasi-static formulation) |
| 4 | **Mold as a layer in the layered GF** | DCIM | The "proper 2.5D" answer | Downstream of kernel C |
| 5 | **Full-wave surface equivalence (PMCHWT/Müller)** | 2× dielectric unknowns, nastier singulars | The honest full-wave answer for a finite mold body | A real project, not a weekend |
| 6 | **Volume integral equation** | Meshes the mold volume | — | Throws away the reason MoM was chosen. **No.** |

### 6.3 Why model 2 is better than it sounds

A point charge near a planar dielectric interface has an **exact** image solution — q′ =
q(ε₁−ε₂)/(ε₁+ε₂) at the mirror point (Jackson §4.4). For the real package geometry — ground paddle
below, mold slab of thickness *t*, air above — this becomes a multiple-image series with products of
reflection coefficients. For εr = 4 the top-interface coefficient is 0.6, so the series converges as
0.6ⁿ: ~20 images for 1×10⁻⁵. No new formulation, no Sommerfeld integral, and the mold-air surface is
captured exactly.

**This trick is quasi-static only.** There is no exact image for the retarded single-interface
problem. W2 therefore inherits models 1 and 3, not 2.

### 6.4 Why model 3 is the recommendation

It is the direct 3D analogue of what §10.3.1 already commits to for kernel A:

> *"With bound charge carried explicitly, the Green's function stays the free-space 2D logarithmic
> potential … no Sommerfeld integrals, no DCIM, no special functions — and it handles an arbitrary
> number of dielectrics immediately."*

In 3D: surface panels carrying bound polarization charge on every dielectric–dielectric boundary,
with normal-D continuity enforced there. What it buys over model 2:

- **Finite mold cap.** A QFN mold body is perhaps 200×200 mil; a 200-wire array spans much of it and
  the edge wires sit near the sidewall. Models 1 and 2 both assume laterally infinite mold.
- **Multiple materials in one solve** — mold, die attach, underfill, and the die itself (GaAs
  εr = 12.9, Si εr = 11.9, which the wire's near-field sees at the ball bond).
- **Architectural consistency.** Same formulation as the kernel already chosen. One story to
  document, one validation approach.

Cost: a 200×200 mil top face at 10 mil panels is ~400 unknowns, sidewalls ~160, call it ~600 with
refinement under the loops. Against a 200-wire array's ~6,000 that is noise; against an 8-wire
array's ~250 it dominates, but 850 total is still instant.

**RW9. Ship model 1 as a mode and model 3 as the real thing, both inside kernel W.** Models 4 and 5
are named and deferred; model 6 is rejected.

### 6.5 The accuracy floor is the material data

EMC is silica-filled epoxy: εr ranges ~3.4–4.5 between compounds, is not well characterised above a
few GHz, and tanδ less so. **Do not build a solver more accurate than the datasheet.** This is a
genuine argument for stopping at model 3 rather than chasing 4 or 5, and it belongs in the docs so a
user calibrating against measurement knows where the residual lives.

---

## 7. Discontinuous ground — z-steps under the wires

### 7.1 What breaks

A flat ground plane is *free* because it is replaced by an image, which is exact only for a
**laterally infinite plane**. A z-step is a lateral variation, so:

- Single-image ground: **breaks**.
- The §6.3 multiple-image dielectric series: **breaks**.
- **The layered-medium Green's function / DCIM: also breaks.** This is the important one. The 2.5D
  premise is *laterally infinite, vertically stratified*; a ground step is precisely what that
  premise forbids.

**RW10. Stepped-ground support is not unlocked by kernel C, and the plan must not assume it is.**
Spending L9 expecting this to fall out of DCIM would be a quarter spent on the wrong thing.

### 7.2 What works — mesh the ground as a conductor

Stop assuming the ground; discretise it. Same move as §6.4, same free-space kernel. Two halves with
very different costs:

- **The [P] half is easy.** Free-charge panels at V = 0 on the ground metal. Identical machinery to
  the mold bound-charge panels, different boundary condition. Essentially free once one exists.
- **The [Lp] half is the real work.** The ground's contribution to loop inductance requires the
  **return current distribution** on the stepped metal — surface-current cells (rooftop/RWG) with
  partial inductances between every cell pair. That is full Ruehli PEEC: well established, not
  research-grade, but a genuine step up from thin-wire-only.

### 7.3 Keeping it affordable

**Hybrid the image and the mesh.** Keep the semi-infinite image plane for the lower tier — exact and
free — and mesh only the *raised* structure (paddle, pedestal, step wall). That covers the dominant
real case (die on a paddle, wire down to a laminate trace) without discretising an entire plane.

**Grade the ground mesh aggressively.** Return current under a wire at height *h* spreads over
roughly ±2h and is smooth compared to the wire current. At h = 30 mil that is a ~120 mil strip per
wire; coarse cells are fine outside it. Realistically ~800–1,500 ground unknowns rather than a naive
several thousand.

**Size warning.** The 200-wire case is already ~6,000. Meshed ground pushes it toward 8,000 → ~1 GB
dense, past R17. Either coarsen, or this is the first place ACA compression genuinely earns its keep.
8–40 wire cases stay comfortable.

### 7.4 How wrong you are if you ignore it

Wire-over-ground inductance goes as ln(2h/a). At a = 1 mil:

| h | ln(2h/a) | vs. h = 30 mil |
|---|---|---|
| 30 mil | 4.09 | — |
| 50 mil | 4.61 | +12.5% |

A 20 mil ground drop under half the span is roughly **+6% on total L** — 120 pH on a 2 nH bond,
j7.5 Ω at 10 GHz. Noticeable in a PA output match, not catastrophic.

A **ground gap or plane split** under the wire is a different story: the return current detours
laterally and inductance jumps **30–50% or more**. That is a first-order error, and it is exactly the
case where "assume a flat plane" produces a confidently wrong number.

### 7.5 Practical traps

- **The ground tiers must be electrically connected in the model.** Paddle-to-board ground is often a
  few vias, some solder, or conductive die attach — real inductance. Modelling a stepped ground with
  the tiers implicitly shorted gives an answer wrong in the *optimistic* direction, which is the
  worst kind. **RW11: the model refuses to solve a multi-tier ground whose tiers have no declared
  connection**, rather than silently shorting them.
- **Downbonds already do much of this.** In many real designs the dominant return path near the die
  is a set of ground bond wires to the paddle, not the plane. Those are just more wires — available
  in T1, before any surface kernel exists. Worth exploiting and worth documenting: a user who models
  downbonds explicitly gets much of the step effect for free.
- **Cavity resonance.** A wire inside a metal cavity has cavity modes. MoM captures them with meshed
  walls, but conditions badly near resonance and needs enough mesh to be trustworthy. Flag it as a
  known weak spot rather than letting a user meet it as a mystery spike.
- **Where to stop.** When the ground stops being "a plane with a step" and becomes genuinely complex
  3D metal — leadframes with tie bars, clips, stepped cavities with lids — the surface area to mesh
  dwarfs the wire count and MoM's advantage erodes. **RW12: that crossover is named in the
  documentation** so the tool has a stated edge rather than a soft failure.

---

## 8. The return path is the whole ballgame

Escalated from §7.5 because it is the single most common way a bondwire model goes wrong.

**Partial inductance is not a physical quantity on its own.** A number labelled "the inductance of
this bond wire" is meaningless without a stated return path.

**RW13. A port definition in kernel W carries an explicit reference conductor**, and the UI does not
permit a port without one. The reference may be the image ground plane, a meshed ground tier, or one
or more ground bond wires.

**RW14. Ground bond wires are ordinary wires in the model**, not a boundary condition. A user
building a GSG array declares the ground wires explicitly and gets their inductance and their
coupling. Modelling only the signal wires while assuming a perfect plane return reports
optimistically low inductance.

---

## 9. Geometry — where a wirebond comes from

### 9.1 The parametric wirebond

Standard packaging profiles, both requested:

- **Ball bond** — vertical rise from the ball, a kink/neck, an arc over, a shallow descent to the
  stitch (wedge) at the far pad.
- **Wedge bond** — a more symmetric catenary/parabolic arc between two wedges.

Parameters: ball position (x, y, z), wedge position (x, y, z), loop height, profile type, wire
diameter, kink height, and the number of discretisation segments (Auto by default). The solver
consumes a polyline in 3D; the profile generator produces it.

### 9.2 The PRD tension, resolved

`docs/PRD.md` §2 previously stated *"Layout is 2D only, and EM is 2.5D … no 3D layout and no 3D
full-wave EM — no arbitrary 3D structures, no volumetric meshing."* A wirebond needs a z-profile.

**RW15. A wirebond is a parametric *component instance*, not a new layout shape.** Its layout view is
its **2D projection** — a line from pad to pad — plus an annotation (*"ball→wedge · 30 mil loop ·
1 mil Au"*). The canvas stays 2D, no 3D shape type enters `.clay`, no volumetric mesher is written,
and the PRD's real non-goals survive intact: *arbitrary* 3D geometry, volume meshing and FEM remain
out of scope. What changes is that a **specific, parameterised, non-planar conductor** is now in.

**Resolved 2026-08-04 (PRD v1.3).** The owner took the scope decision and the PRD non-goal was
narrowed to *no FEM, no volumetric meshing, no arbitrary 3D geometry* — see `PRD.md` §2, §5, §8 and
§17. Recorded in `layout-view.md` §13 as decisions 11 and 12.

### 9.3 Import

Wirebond tables are the normal interchange for packaging flows (a CSV of from-pad / to-pad / profile
per wire, or a bonder program). **RW16: a CSV wirebond table importer is in scope for the wirebond
phase**, because hand-placing 200 wires in a GUI is not a workflow anyone will use.

---

## 10. Known accuracy limits, stated up front

- **Thin-wire reduced kernel at tight pitch.** At 5 mil pitch with 1.25 mil radius, s/a ≈ 4. The
  reduced kernel assumes filamentary axial current matched at the wire surface; below s/a ≈ 5–8 the
  azimuthal proximity effect matters. Expect a few percent error at the tightest end of the stated
  range. Fixable with the exact (azimuthally-averaged) kernel or multi-filament, at ~2× fill cost.
  **RW17: the mesher warns when any wire pair falls below s/a = 6.**
- **The T1 cascade seam** at the landing pad (RW6).
- **Mold material data** (§6.5).
- **No cavity-resonance guarantee** (§7.5).
- **W1 has no radiation and degrades at wide pitch × high frequency** (§4.1) — though the error term
  is largest where the coupling is smallest.

---

## 11. Validation oracles

Following §10.9's philosophy — closed-form anchors plus oracle-free self-consistency — with the
advantage that the owner has a 3D FEM reference for this exact geometry, which kernels B and C do not.

**Closed-form anchors:**

| Oracle | Tests |
|---|---|
| Self-inductance of a straight round wire (Rosa/Grover) | [Lp] diagonal, internal L |
| Mutual inductance of two parallel filaments (Neumann, closed form) | [Lp] off-diagonal |
| Wire over an infinite ground plane, L = (μ₀/2π)·acosh(h/a) | The image path |
| **Right-angle conducting corner — exact 3-image quasi-static solution** (two single reflections, one double; signs −, −, +) | **The meshed-surface code, before any real package.** Build the corner from panels, compare to the analytic images. |
| Charge above a dielectric half-space, q′ = q(ε₁−ε₂)/(ε₁+ε₂) | The §6.3 image series |
| Round-wire internal impedance (Bessel) | R(f) ∝ √f and internal L(f) |
| ~25 pH/mil (≈1 nH/mm) rule of thumb | A smell test, not a gate |

**Self-consistency** (all of §10.9's list applies unchanged): reciprocity, passivity, losslessness at
σ = ∞ / tanδ = 0, segment-count convergence, a wire of length 2ℓ equalling two cascaded ℓ wires,
reference-plane invariance.

**3D FEM regression set**, owner-generated and owner-approved before it becomes a gate: a single wire
over ground, a 2-wire coupled pair at 10 mil and at 100 mil pitch, an 8-wire GSGSG array, a molded
vs. bare comparison, and a stepped-paddle case. The 8-wire array is the acceptance anchor.

---

## 12. Phasing

Slotted into `layout-view.md` §11 as two phases. Kernel W depends on L6's stackup and mesh viewer and
on L7's `IEmKernel`, ports, sweep and results plumbing — **not** on L8 or L9.

| Phase | Content | Gate |
|---|---|---|
| **LW1 — Wirebond T1 (kernel W1/W2)** | Parametric wirebond component (ball + wedge profiles), 2D-projection layout representation, CSV wirebond-table import, 3D polyline mesher + 3D mesh viewer, quasi-static PEEC ([Lp], [P], Bessel [Z_int]), `Retarded` flag for W2, image ground, homogeneous and image-series mold, explicit port reference conductors, N-port → `DataSet` → `.snp` | Single wire over ground within 2% of the closed-form L; 2-wire mutual within 2% of Neumann at 10 and 100 mil pitch; 8-wire GSGSG array within 5% of owner-generated 3D FEM data on all S-parameters to 20 GHz; molded vs. bare shows the §6.1 signature (L unchanged, C ~4×); reciprocity/passivity/losslessness/convergence pass; a 200-wire array solves within the R17 budget and reports N first |
| **LW2 — Wirebond T2 (surfaces)** | Surface panels (free charge + bound charge + surface current), wire-to-surface junction basis functions, meshed stepped ground with image/mesh hybrid and graded mesh, finite mold body, coupling to planar metal | Right-angle-corner 3-image oracle passes before any package geometry; stepped-paddle case within 5% of the same 3D FEM reference; a ground-gap case reproduces the §7.4 inductance jump; multi-tier ground with no declared connection is refused (RW11); mold sidewall proximity shows the expected edge-wire asymmetry |

**LW1 is a shippable increment on its own** (RW5). LW2 is the larger half (RW7) and should not start
until LW1's oracles are green — same reasoning §11 gives for L8 waiting on L7.

**LW3 (T3/W3 — wires in the layered stack) is named, not scheduled.** It is downstream of L9.

---

## 13. Decisions and open questions

### Decided in this document

| # | Question | Decision |
|---|---|---|
| W1 | Is wirebond MoM feasible? | **Yes**, and a better fit than FEM for this geometry (§2). §10.1's exclusion is amended (§1) |
| W2 | Which kernel family? | **Thin-wire, quasi-static first (W1), retarded second (W2), layered third (W3, unpromised)** — mirroring A→B→C for the same reason (§4) |
| W3 | Wires only, or wires + planar? | **T1 wires-only ships first and standalone**; T2 surfaces is one deliverable unlocking pads + stepped ground + finite mold together (§5) |
| W4 | Overmold? | **Homogeneous fill as a mode, bound charge on mold surfaces as the real thing** (§6.4, RW9). Image series available in W1. VIE rejected |
| W5 | Discontinuous ground? | **Mesh it** — image/mesh hybrid, graded. Explicitly **not** unlocked by DCIM (§7, RW10) |
| W6 | How does a 3D wire live in a 2D layout? | **A parametric component whose layout view is its 2D projection** — `.clay` gains no 3D shape type (§9.2, RW15) |
| W7 | Where does it sit in the schedule? | **LW1/LW2 after L7, independent of L8/L9.** LW1 carries no DCIM risk, so it may be higher value per unit risk than L8 (§12) |

### Resolved by the owner, 2026-08-04

| # | Question | Decision | Where it landed |
|---|---|---|---|
| W8 | Does the PRD's "no 3D EM" non-goal have to change? | **Yes, narrowed** — to *no FEM, no volumetric meshing, no arbitrary 3D geometry*. The old wording excluded a solver needing none of them | `PRD.md` §2 (non-goal), §5 (simulation scope), §8 (views), §9 (UI), §17 (resolved); `Development_Plan.md` §4, §8, §9, §10 |

### Open

1. **Priority against L8.** LW1 has no DCIM dependency and directly serves the PA/module designer who
   is already circuitRF's hero-circuit persona. Running LW1 *before* L8 is defensible on
   risk-adjusted value. A roadmap call, not a technical one; tracked as `PRD.md` §17 open item 4.
   It blocks neither design.
2. **N-ceiling for the 200-wire + meshed-ground case** (§7.3): raise R17, coarsen, or implement ACA.
   Decide when LW2 has real numbers, not now.
