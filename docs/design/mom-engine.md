# circuitRF — The MoM Engine (design + plan)

**Status:** Proposal + built — rev 5 (kernel W, 3D wirebonds — §10.11, phases LW1/LW2) ·
**Date:** 2026-08-04, split out of `layout-view.md` 2026-08-24 · **Phase:** 8

> **This was §10 of [`layout-view.md`](layout-view.md) and is now its own note.** It had grown to
> roughly a third of that document while being a research-grade numerics project the editor build has
> no dependency on, in either direction — the editor ships without it and it consumes nothing from
> §§1-9A but the geometry model.
>
> **The section numbers are kept exactly as they were, deliberately.** Around a hundred pointers in
> `docs/sonnet-briefs/`, in the per-area `CLAUDE.md` files and in the engine's own source comments say
> things like "§10.6" and "§10.7's own hero", and renumbering would silently break every one of them.
> So §10.1 is still §10.1; only the file it lives in changed.

**What is built and what is not.** §§10.1-10.9 describe kernels **A** (2-D quasi-static
per-unit-length) and **B** (2.5-D full-wave planar), both of which ship. Kernel **W** (§10.11, 3-D
wirebonds) ships as **wBond**. The authoritative record of what the code actually does — every measured
number, every refusal, every negative result — is `src/Engine/Mom/CLAUDE.md` and its `HISTORY.md`; the
Ui half is `src/Ui/Layout/Em/CLAUDE.md`. **Where this note and those disagree, they are right and this
is a plan** — the inline `> Built at Lx` notes below are where the two have been reconciled.

Companions: [`layout-view.md`](layout-view.md) (the editor this analyses),
[`mom-wirebond-kernel.md`](mom-wirebond-kernel.md) (kernel W in full),
[`microstrip-models.md`](microstrip-models.md) (the closed forms this is the alternative to),
[`ui-architecture.md`](ui-architecture.md) (the UI firewall both halves respect).

The user-facing description of the same engine — what it can and cannot simulate, how to set a port
up, how to read a de-embedded result — is `docs/user/src/reference/mom-engine.md`. **Those two must
not contradict each other**; when a decision here changes, that page changes with it.

---

## 10. The 2.5D MoM simulator

### 10.1 What is being proposed, precisely

2.5D MoM solves the mixed-potential integral equation over conductors embedded in a **laterally infinite,
vertically stratified** medium. Metal is horizontal and thin; current flows in-plane, plus z-directed
current through vias. This is the planar-EM class of tool. It is *not* FEM and not
general 3D — a wirebond arcing through air is out of scope **for kernels A, B and C**.

> **Amended 2026-08-04.** That last clause originally read "out of scope by construction," which was
> too strong as a statement about MoM. It is true of the *kernels described here* and false of the
> method. A wirebond is **3D geometry in a stratified medium — the medium is still 2.5D**, which is
> why commercial 2.5D planar solvers were extended to bondwires. **Kernel W** (§10.11) covers it
> and does not disturb anything below.

The pieces, in dependency order:

1. Substrate stackup model + editor (§10.4)
2. Mesher with edge refinement + mesh viewer (§10.5)
3. **Layered-medium Green's function** (§10.3) ← the hard part
4. Matrix fill with singular-integral handling
5. Dense complex solve per frequency
6. Port excitation and de-embedding (§10.6)
7. Results → `DataSet` → Data Display / Touchstone (§10.8)

### 10.2 The honest cost

Items 1, 2, 5, 6, 7 are ordinary engineering — weeks each, well-bounded, independently testable.

**Item 3 is a research-grade numerics problem.** The spatial-domain Green's function for a layered medium
requires inverting the spectral-domain form through a Sommerfeld integral, which is oscillatory,
slowly-converging, and has branch points and surface-wave poles. The standard production answer is
**DCIM** (Discrete Complex Image Method): approximate the spectral Green's function as a sum of complex
exponentials via GPOF/matrix-pencil, each of which inverts in closed form by the Sommerfeld identity.
DCIM is implementable and well-documented in the literature, but it is fiddly, numerically delicate,
and it is where a schedule goes to die. Item 4 (singular self- and near-term integrals) is the second
such place.

Plan for this honestly rather than discovering it in month four.

> **Measured at L8a (2026-08-05).** Item 3 now has a number rather than a warning. The layered
> Green's function for a grounded slab is built and validated against direct Sommerfeld integration
> — a second, independent formulation — over ρ/λ ∈ [1e-4, 10] on both starter substrates at 2, 10
> and 20 GHz. **Error as a fraction of the free-space kernel at the same ρ, which is what a matrix
> fill experiences, is ≤ 6e-3 across that entire span; strict relative error is ≤ 1e-2 out to
> ρ/λ ≈ 1** and degrades beyond, which is where `Dcim.WithinValidatedRange` refuses. Details, the
> full curve and the two occasions an *oracle* rather than the method turned out to be wrong are in
> `src/Engine/Mom/CLAUDE.md` §L8a. Item 4 — the singular self- and near-terms — is untouched and is
> still the second place a schedule goes to die; it is L8c.
>
> **Measured at L8c (2026-08-05). Item 4 now has a number as well, and it is not the one that was
> feared.** The singular self- and near-term integrals turned out NOT to be where the difficulty
> lives, because a rectangular mesh with source and observer in one plane makes the INNER integral
> closed form — six of them, derived and checked against adaptive quadrature to 1e-12. The classic
> "nearly touching cells" problem comes from doing both integrals numerically; here only the outer one
> is, and it sees a continuous function with a kink, which is a quadrature-ORDER question. Against the
> εᵣ = 1 reduction, where the kernel is exact and only the quadrature can be wrong, the assembled
> matrix is right to **5.0e-6**; against direct Sommerfeld integration with the real DCIM kernel it is
> **5.4e-3**, i.e. item 3's own error and not item 4's.
>
> **Where the schedule actually went was a different place, and it is worth naming.** DCIM's fitted
> complex images are only "smooth" while none of them sits closer to the metal plane than a cell is
> wide — and on the FR-4 starter above ~5 GHz several do (min|b|/cell = 0.165 at 10 GHz, 0.079 at
> 20 GHz). The extraction's smooth remainder therefore is not smooth on the mesh's own scale, and a
> quadrature rule that is ample for free space was 5% wrong while converging gently enough to look
> converged at every step. Details in `src/Engine/Mom/CLAUDE.md` §L8c.
>
> Two structural notes for whoever picks up L8b–L8e. **L8 is split into five slices** (L8a the
> Green's function, L8b the mesher and viewer, L8c basis functions and the fill, L8d ports and
> de-embedding, L8e results and the kernel registry), on the same staging principle every phase in
> this area has used. And **the v1 kernel supports exactly one conductor layer, on the top surface
> of the slab** — so source and observer are always at the same height and the Green's function is a
> function of ρ alone. That is enough for all three of L8's own gates; multiple metal levels,
> z-directed current and vias are L9's, and are refused by name until then.

### 10.3 The v1 kernel — 2D quasi-static per-unit-length (decided)

**Decided: A → B → C.** v1 solves the *cross-section* of a uniform transmission-line structure and
produces per-unit-length RLGC, from which everything else follows. Full-wave single-dielectric (B) and
the general layered stack (C) replace the kernel later behind a fixed interface.

#### 10.3.1 The formulation

**Unknowns.** Charge density on boundary segments: **free** charge on conductor perimeters, **bound**
polarization charge on dielectric interfaces.

**Kernel.** With bound charge carried explicitly, the Green's function stays the *free-space* 2D
logarithmic potential `−ln(r)/2πε₀`. This is the single most valuable property of the choice: **no
Sommerfeld integrals, no DCIM, no special functions — and it handles an arbitrary number of dielectrics
immediately**, which satisfies the "multiple dielectrics with different properties" requirement in v1
rather than deferring it. A ground plane is handled exactly by a single image; a conductor-backed stack
needs nothing more.

**Equations.** Constant potential on each conductor (1 V on the excited conductor, 0 V on the rest and
on ground); normal-D continuity across each dielectric interface. Assemble, solve the real dense system
once per excited conductor.

**Known approximation.** Dielectric interfaces are laterally infinite and must be truncated. Truncate
several substrate heights beyond the outermost conductor with a graded tail, and make the truncation
distance a visible (auto-defaulted) setting. This is the one place A can be quietly wrong, so it gets an
explicit convergence test: extending the truncation must not move Z₀ by more than the oracle tolerance.

#### 10.3.2 From charge to s-parameters

1. Solve with the real stackup → **[C]**. Solve again with every dielectric replaced by air → **[C₀]**.
2. **εeff = C/C₀** (per-mode for multiconductor).
3. **[L] = µ₀ε₀[C₀]⁻¹** — the standard TEM identity. No second solve type needed.
4. **[G]** — *superseded at L7, 2026-08-04.* This item originally said "ω·tanδ-weighted partial
   capacitances, accumulated during the [C] fill". There is a cheaper and exactly-correct route: carry
   `ε* = ε_r(1 − j·tanδ)` through the whole system, which makes the interface coefficient K complex and
   [C] come out complex, `C = C′ − jC″`. Then `Y = jω·C_complex = ωC″ + jωC′` exactly, i.e.
   `G = −ω·Im(C)` and `C = Re(C)`. It costs **one** complex solve on a matrix of a few hundred, handles
   any number of independently lossy dielectrics, and `G ∝ ω` for constant tanδ falls out rather than
   being asserted. **Do not implement a separate partial-capacitance accumulation.**
5. **[R]** from **Wheeler's incremental inductance rule**: recede each conductor surface, recompute L,
   and `R(ω) = (R_s(ω)/µ₀)·∂L/∂n` with `R_s = √(ωµ₀/2σ) = 1/(σδ)`. It reuses the same solver with a
   perturbed geometry, so conductor loss costs one extra fill rather than a new formulation, and
   R ∝ √f falls out of R_s.
   *Two corrections made at L7:* (a) this item previously read `R = (ω/2)·∂L/∂n·(2/δ)`, which is short
   by a factor δ² — `R_s/µ₀` is `ωδ/2`, not `ω/δ`; (b) the recession must **not** be half a skin depth,
   because that makes it frequency-dependent and forces a matrix refill per frequency, destroying the
   frequency-independence below for no accuracy gain. ∂L/∂n is a purely *geometric* derivative,
   evaluated once. The recession must be summed over **every** lossy surface — the signal conductors
   *and* the ground plane; omitting the ground-plane term is the common error and it under-reports
   microstrip loss noticeably.
6. **γ = √((R+jωL)(G+jωC))**, **Z_c = √((R+jωL)/(G+jωC))** → ABCD of a length-ℓ uniform line → S,
   renormalized to the port reference impedances. Multiconductor goes through modal decomposition to
   generalized coupled-line s-parameters.

**The property that makes this feel fast.** [C], [C₀] and ∂L/∂n are **frequency-independent**. A 1001-
point sweep therefore costs *one* matrix solve plus 1001 closed-form evaluations — effectively
instantaneous. Full-wave (B/C) must refill and refactor per frequency. For a tool whose stated goal is
"lightweight and snappy", v1 will be dramatically snappier than the thing that eventually replaces it,
and that is worth saying to the user in the UI rather than hiding.

Optional cheap upgrade once the oracle passes: a **Kirschning–Jansen dispersion correction** so εeff(f)
and Z₀(f) track the known dispersion of microstrip. It is a closed-form formula, not a solver, and it
meaningfully extends the useful frequency range.

#### 10.3.3 Getting a cross-section out of a 2D layout

v1 solves a cross-section, but the user draws a layout. The bridge:

**R16a. Cross-section extraction is automatic and its result is shown back to the user.** When an EM
setup is run, analyse the selected geometry: if it reduces to straight, mutually parallel, constant-
width conductors on mapped stackup layers, extract the cross-section and the propagation length, and
display what was found — *"uniform 2-conductor cross-section · W = 2.9 mm · gap — · ℓ = 20 mm"*.

**If it does not reduce**, refuse **clearly and specifically**: *"This geometry has a bend at (x, y);
the quasi-static solver handles uniform cross-sections only. Full-wave analysis of discontinuities
arrives in L8."* A vague failure here is what would make v1 feel broken rather than bounded. A manual
**cut-line tool** is the escape hatch for a structure the auto-detector does not recognise.

This keeps the entire user-facing story identical to what B and C will offer — draw geometry, place
ports, sweep, plot — so nothing about the workflow has to be relearned when the kernel is swapped.

#### 10.3.4 The kernel interface, so A is not throwaway

Everything except the kernel is shared across A, B and C: the `.ctech` stackup model and editor, the
port model and placement UX, the frequency-sweep UI, the mesh viewer, the results plumbing, the
validation harness, and the edge-grading logic (§10.5). Fix the boundary now:

```csharp
IEmKernel {
    string         Name         { get; }
    EmCapabilities Capabilities { get; }          // uniform-cross-section | planar | layered+vias
    EmSuitability  CanSolve(EmProblem problem);   // the ONLY place a refusal is worded
    EmMeshReport   Mesh(EmProblem, EmMeshSettings);               // for the viewer, pre-solve
    DataSet        Solve(EmProblem, EmMeshSettings, double[] freqsHz, CancellationToken);
}
```

**Corrected at L7 (2026-08-04): the kernel consumes a neutral `EmProblem`, not `LayoutFragment` +
`Stackup`.** The original signature above named Ui types, and that is not simultaneously satisfiable
with §10.7's "all of it lives in `src/Engine/Mom/`": `LayoutFragment`, `Stackup` and `Technology`
live in `src/Ui/Layout/`, the reference graph is `Ui → Engine → Core → RfCore`, and inverting the
arrow would break the UI firewall that `tests/Firewall.Tests` enforces.

`EmProblem` (`src/Engine/Mom/EmProblem.cs`) is the neutral cross-section model — conductors as
finite-thickness polygons, dielectric regions as horizontal slabs, an optional ground plane, ports,
and the propagation length — **in SI units throughout, knowing nothing about DBU, `.clay` shapes,
layer tables or `LayerKey`.** The §10.3.3 cross-section extractor produces it, which is what
extraction already had to do. This is the better boundary anyway: it is the standing invariant *"the
numeric layer sees only fully-resolved values"* applied to geometry, and it is what lets the entire
kernel be validated against closed-form oracles without constructing a layout document.

`Capabilities` is what drives the §10.3.3 refusal message, so adding kernel B is a registration plus a
capability widening — not a rewrite of the calling code. **Kernel W (§10.11) is the first real test of
that claim**: it widens `EmCapabilities` with `Wires` (and later `Surfaces`) and registers, touching
nothing else. If W cannot be added this way, the interface is wrong and it is cheaper to learn that at
L7 than at L8.

`CanSolve` splits the refusal duty cleanly: the *geometric* refusals — bends, tapers, non-parallel
conductors — are detected by the Ui-side extractor before an `EmProblem` is ever built, and the
kernel words the ones it can see from the problem itself (a non-tiling region stack, a
zero-thickness conductor, a self-intersecting outline, a port naming an absent conductor, a port
with no resolvable reference, and — since L7b-b — more than `QuasiStaticKernel.MaxSignalConductors`
signal conductors). Both follow the same shape: name the specific feature, name where the capability
arrives. **Each phase has NARROWED those multiconductor refusals rather than deleting them**:
L7b accepted a symmetric coupled pair with its 2N ports; L7b-b's general modal decomposition
accepts an asymmetric pair and any N up to a conductor ceiling bounded by the dense
boundary-element solve, which is stated with its measured cost. Deleting a refusal instead of
narrowing it is how a kernel starts silently answering questions it cannot answer.

**No kernel registry exists yet, deliberately.** One kernel, constructed directly; a registry earns
its place when W or B exists.

#### 10.3.5 What v1 explicitly cannot do

Say this in the docs and in the UI, not just here: no discontinuities, bends, stubs, spirals, or
radiation; no coupling between non-parallel conductors; no full-wave dispersion beyond the optional
closed-form correction; no resonance. What it *does* do — uniform single and coupled lines, on
arbitrary multi-dielectric stacks, with real conductor and dielectric loss, swept instantly — is a
genuinely useful instrument and a complete end-to-end proof of every other part of the system.

### 10.4 Substrate stackup editor

An ordered stack from top to bottom, living in the `.ctech` file:

- **Boundary conditions** top and bottom: open (free space), or perfect/lossy ground plane.
- **Dielectric layer**: thickness, εr, tanδ, µr.
- **Conductor layer**: thickness, σ, optional surface roughness; bound to one or more drawing layers.
- **Via layer**: connects two conductor layers; bound to a drawing layer.

UI: a vertical stack diagram, click a band to edit, with **presets** — "FR-4 2-layer 1.6 mm",
"Rogers 4350B 0.508 mm", "GaAs MMIC 100 µm" — because preset-then-tweak is what makes the 30-second
target reachable. Linear isotropic only, as specified; the model should nonetheless leave room for
anisotropic εr later (Rogers laminates are anisotropic in reality).

### 10.5 Meshing, including the edge mesh

Your instinct about edge current is exactly right: on an RF conductor, current density has a
1/√d singularity at the edge, and a uniform mesh badly under-resolves it — which shows up directly as
wrong loss and wrong Z₀.

**v1 meshes one dimension, not two.** The quasi-static kernel discretizes *boundaries* in the
cross-section: segments along each conductor perimeter and along each dielectric interface. This is much
simpler than a surface mesh, and — importantly — the edge-crowding physics is identical, so the edge-
grading logic written here is the same logic B and C will use. A few hundred segments is a full mesh.

**Surface bases arrive with B.** Two families, decided then rather than now:
- **Rooftop on a rectangular grid** — simple, fast, ideal for Manhattan geometry, staircases diagonals.
- **RWG on triangles** — handles arbitrary geometry, needs a robust constrained Delaunay triangulator.

Leaning rectangular rooftop for B, with triangles added for spirals and tapers: the planar-EM class of
tool has demonstrated for decades that a rectangular mesher is a production choice, not a toy. Left open until L8.

> **Decided and measured at L8b (2026-08-05): rectangular rooftop on a TENSOR-PRODUCT grid, with
> diagonals and curves STAIRCASED.** Triangles are not built — RWG needs a robust constrained Delaunay
> triangulator, which is a real commitment and is not earned by a slice whose job is to produce a
> number. The staircasing error was measured on the shipping PCells rather than on a synthetic
> diagonal, because those are what a user actually selects and because **this phase's own gate is not
> all-Manhattan** (`MBendPCell` cuts a 45° mitre, and mitred vs. unmitred is exactly the distinction
> the bend gate asks the kernel to make).
>
> **The mitre survives staircasing** — at the auto cell size the cut is reproduced to 2.8% of its area
> and removes 18 cells, so a mitred bend and a square one give different meshes and different unknown
> counts. **The smooth tapers are the sharper result**: local WIDTH error along an `MTaper`/`MKlopf`
> outline is 17–24% at worst and 5.5–11% RMS, while the global AREA error is only ~0.5% — and a
> Klopfenstein taper's whole value is a controlled equiripple |Γ| (0.05 by default), against which the
> local number is the one that matters. Two consequences, both recorded in `src/Engine/Mom/CLAUDE.md`
> §L8b: a smooth taper is a case for the shipped analytic model rather than for full-wave (the N
> report says so, by name), and if a future phase needs a taper's own full-wave answer accurately,
> **conformal/diagonal boundary cells — one straight cut through an otherwise rectangular cell — are
> the proportionate next step, not a triangulator.** The cell type and the report were deliberately
> shaped not to forbid that.
>
> **Sizing rules, as built.** λ_g is taken in the local dielectric at the sweep's HIGHEST frequency
> (εᵣ, i.e. the shortest wavelength the structure can see — conservative, and the only value available
> before a solve), and the mesh is computed **once per sweep**, not once per frequency. Cell size is
> per AXIS, from the narrowest conductor run measured along that axis; that is what keeps a long, thin
> taper affordable on a tensor grid. The unknown count is **basis functions, not cells** — a rooftop
> spans a pair of adjacent cells — and that is the number §10.7's ceiling refuses on.

> **Closed at L8c (2026-08-05): the edge reference length, measured against a converged physical
> quantity.** L8b could only count unknowns; the convergence half needed a solver. Measured on the
> static capacitance of the FR-4 hero above, refining each candidate along its own ladder: the shipped
> **conductor-width reference lands 0.18% from the two candidates' consensus limit at N = 552**, and
> the cell-size alternative 0.11% at N = 787. The mechanism is on the record too — the conductor-width
> edge cell does not shrink as cells/λ rises, so its flat refinement sequence means "already at its own
> limit" rather than "converging", and it sits ~0.35% low. That is inside any EM tolerance and is what
> keeps an ordinary GaAs line under R17, where the alternative measures N = 7,562. The default stands.

**Meshing rules, all auto-derived from the analysis so the user need not think.** (The wavelength rule
binds only from L8 onward; the quasi-static kernel has no wavelength dependence, so its `Auto` mesh is
driven purely by geometry and edge grading — one more reason v1 sweeps for free.)
- Maximum cell size ≤ λ_g/20 at the highest swept frequency, λ_g computed in the local dielectric.
- At least 3–5 cells across any conductor width.
- **Edge mesh**: 2–4 geometrically graded cells at every conductor edge, the outermost being a small
  fraction of the width (~2–5%) and growing by a ratio ~1.5–2 inward. Expose exactly three controls —
  `Auto` (default), `Cells per wavelength`, `Edge mesh on/off + cell count` — and nothing more.
- Report the resulting unknown count *before* solving, with a warning above the §10.7 budget.

**Mesh viewer.** A system layer superimposed on the geometry drawing cell boundaries, toggled from the
toolbar. It reuses the §5 renderer directly (a mesh is just more polygons on a special layer), and after
a solve the same layer renders a **current-density heat map**. This is high-value and cheap given the
existing rendering work — it is how the user develops trust that the mesh is sane, and it should land
*before* the solver, not after.

### 10.6 Ports and de-embedding

This is where EM simulators are won or lost, and where naive implementations produce plausible-looking
but wrong numbers.

- **Port types v1:** edge ports on a conductor boundary (the microstrip case), internal delta-gap
  ports, and internal (ground-referenced) ports. *(All three are built — see the notes at the end of this section for
  what each of the two internal ones is and, more importantly, what it is not.)*
- **Placement UX:** a Port tool that snaps to a conductor edge; click an edge, get P1. Auto-number,
  default 50 Ω, editable reference impedance.
- **De-embedding is mandatory, not optional.** A raw port excitation includes the port discontinuity;
  reporting those s-parameters as the structure's response is simply wrong. v1 approach: simulate a
  short and a longer uniform reference line of the port's cross-section, extract the port's own
  reflection and the line's propagation constant, and remove them — the standard two-line calibration.
  Show the de-embedding reference plane in the layout so its location is never a mystery.
- **…but de-embedding is a no-op for kernel A specifically, and that is a finding, not a shortcut
  (added at L7, 2026-08-04).** The paragraph above is about a *meshed* port excitation, which is what
  arrives with the full-wave kernel at L8. Kernel A never meshes a port: it computes γ and Z_c
  analytically from the per-unit-length RLGC and forms the Z-matrix of a uniform line of length ℓ
  directly, so the reference planes are exactly at the line ends **by construction** and there is no
  port discontinuity to remove. Building the two-line calibration now would be building a calibration
  for an error that does not exist; it becomes real work at L8. The observable consequence is pinned
  by a test: ∠S₂₁ is exactly −βℓ with no offset.
- **Ground reference** must be explicit: for microstrip it is the stackup's ground plane; for CPW it is
  the adjacent coplanar conductors. Get this wrong and everything downstream is wrong. *(v1 builds the
  ground-plane reference only, for BOTH port types; the other two are refused by name.)*

> **Decided and built at L8e (2026-08-05) — a port is a LABEL, not a new shape type.** The "Port tool
> that snaps to a conductor edge" above is real now, and what it places is an ordinary `LabelShape`
> with `IsPort` set. That flag already existed and already round-trips, so **the `.clay` schema did not
> change** and a layout carrying ports still round-trips byte-identically. Four consequences:
>
> - **Numbering** comes from the label's own text — `1`, `P1`, `p2`, `#3`, `Port 4` all parse. A label
>   that names no number is auto-numbered to the lowest free one rather than refused, and the Port tool
>   uses the same parser, so what the tool writes and what the extractor reads cannot drift.
> - **Two labels naming the same number is a refusal by name**, not a silent win for one of them.
> - **The side is INFERRED from geometry, reported in the notes, and refused when ambiguous.** A label
>   at the exact corner of a conductor is equally close to two edges; guessing reverses the direction of
>   current into the structure, which is a hard π in S₂₁ — smooth, plausible, and invisible in a
>   magnitude plot. So it is named and refused: *"Port 1 is ambiguous… Move the label."* Every resolved
>   port reports its inferred side and which way current flows in.
> - **The reference impedance lives in the `.cem`**, per port, never on the shape. A layout is geometry.
>
> **The de-embedding reference plane is not user-positionable, and that is a stated limitation.** It sits
> one mesh cell in from the drawn metal edge, because that is where L8d's calibration actually removes
> the port discontinuity; an adjustable plane would need a re-referencing step that does not exist. The
> planes are DRAWN over the layout (the bullet list's own requirement, "so its location is never a
> mystery") from the coordinates the *engine* reports, not from a Ui re-derivation of them.

> **Built and measured at L8d (2026-08-05).** The two-line calibration above is implemented, and three
> things about it are worth having in the design note rather than only in the engine's own file.
>
> **The bullet list's own emphasis was right about the wrong thing.** The calibration ALGEBRA is exact:
> a de-embedded uniform section comes out perfectly matched at the two lengths the calibration was
> solved from — |S₁₁| = 8.5e-16, four equations fixing four unknowns — and the two independent routes
> to γ (the two-line trace and a travelling-wave fit that shares no algebra with it) agree to
> **2.5e-4 … 3.9e-3** across 2–10 GHz. What limits accuracy is not the calibration but **direct
> radiative and surface-wave coupling between the two ports**, which decays only algebraically and has
> no term in a "box + matched line + box" model. Measured on 1.6 mm FR-4: a section that should be
> matched reads |S₁₁| = 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz — an f² scaling, and NOT monotone in the
> standard's length, which is how it was identified. **A de-embedded answer here is good to a few 1e-3
> at 2 GHz and a few 1e-2 at 10 GHz, and a longer feed does not improve it.** Real planar tools suppress
> this with box walls or absorbing boundaries; this kernel has neither, by design.
>
> **"Show the de-embedding reference plane in the layout" is now a trivial UI job**, because the plane
> is not a user choice: it is one cell in from the drawn metal end, fixed by construction, and there is
> deliberately no offset knob — offering one would offer a way to get a different answer for the same
> structure.
>
> **One thing the bullet list does not say and should: the de-embedded S is referenced to the LINE'S own
> Z_c, and the calibration cannot determine it.** That is a property of the method, not a gap. Z_c comes
> from `γ/(jωC_pul)` with C_pul differenced between the two standards so the end effects cancel exactly;
> kernel A is its ORACLE and never an input (measured: C_pul agrees to −0.26%, Z_c to +0.40% at 1 GHz).
> The assumption that C is frequency-independent is the route's real cost, and it is 0.4% / 2.3% / 6.3%
> at 1 / 5 / 20 GHz against kernel A's static value. Details in `src/Engine/Mom/CLAUDE.md` §L8d.

> ### **Requirement, added 2026-08-12 (owner report): THE SOLVER BUILDS ITS OWN CALIBRATION FEED.**
>
> **A user places a port on the part they drew and presses Simulate. Nothing above that is their job.**
> In particular, *the user must not have to add a uniform feed line to their artwork so that the
> de-embedding has something to calibrate against* — circuitRF works out what calibration structure it
> needs, and how much of it, on its own.
>
> **Why this had to become an explicit requirement.** L8d's calibration standard is an ISOLATED UNIFORM
> LINE of the port's cross-section, and its `a₁₁` is only the DUT's `a₁₁` if the DUT's own metal looks
> like that line for the distance the standard replaces. Drop a shipped `MKLOPF` PCell into a layout,
> port its two ends, and it does not: a taper's flanks are oblique from the first cell. That is not a
> mild inaccuracy, because D6's peel forms `(S_meas,ii − a₁₁)/a₂₁²` and `a₂₁ ∝ ω` — on 0.508 mm
> RO4350B at 1 GHz `a₂₁² = 9.8e-5`, so a **0.1% error in the error box is a 10× error in the answer**.
> The owner's 2000 mil, 50 → 12 Ω Klopfenstein taper came back as `|S₁₁| = 1.0000`, `|S₂₁| = 0.0008`
> and `Σ|S|² = 1.06` — a **non-passive open circuit**, shipped to a `.s2p` with nothing but a note.
>
> **What v1 does about it (`PlanarFeedExtension`, R-fed-1/R-fed-2).** Before meshing, each port's own
> polygon is extruded outward from its drawn end face by however much uniform line the calibration is
> short of. The lead is real metal to the fill; afterwards it is removed EXACTLY, as a matched section
> in the line's own `Z_c` using the γ the calibration already measured for that cross-section. Three
> properties are load-bearing and are gated:
>
> - **The reference plane is still the user's drawn metal edge.** The lead moves where the error box is
>   measured, never where the answer is reported — which is what keeps the paragraph above ("one mesh
>   cell in from the drawn metal edge") true rather than quietly false.
> - **A feed that is already uniform grows nothing**, and the problem reaches the mesher by reference,
>   so every number recorded in `src/Engine/Mom/HISTORY.md` stays reproducible bit for bit. Running out
>   of metal counts as uniform: a short line is a SHORT structure, not a flared one.
> - **Every case it cannot be sure of is declined, not guessed** — an end face that is not a single
>   straight segment, a port whose level is ambiguous, a lead that would run into other metal. A
>   decline is the pre-existing behaviour plus the pre-existing warning, which is the honest fallback;
>   moving metal the user drew would be a worse failure than the one being fixed.
>
> **And the answer is now checked before it ships.** σ_max(S) ≤ 1 was a gate in the test project only.
> A de-embedded sweep that publishes σ_max > 1 now says so, at the frequency and by how much
> (R-prt-15) — the excess is the analysis, never the design, and the user has to know that before
> reading the plot.
>
> **What this does NOT fix, and it is the reason `CheckFeedClearance` still exists:** a lead lengthens a
> feed, it cannot move a neighbour sideways. Metal running alongside the port inside the calibration's
> own run is still a limitation and is still warned about — and that warning was itself wrong until
> now (it scanned to the far end of the board, so it fired on every part wider than its port and could
> never be cleared).

> ### **BUILT 2026-08-24 — the INTERNAL DELTA-GAP PORT.**
>
> `PlanarPortKind.InternalDeltaGap`, offered per port in the panel.
>
> **It is the SAME OBJECT, cut somewhere else.** D1 already makes a port "a delta gap across the shared
> edge of two adjacent cells, driving the rooftop row that spans it" — nothing about that says the cells
> have to be the two outermost ones. An edge port names an END of a conductor and D2 fixes its cut at
> the outermost pair; an internal port names a POINT ON the metal and its cut is the mesh gridline
> nearest that point, with metal on both sides. Downstream, both are one row of the same incidence
> matrix and one column of the same `Y = BᵀZ⁻¹B`. **No new basis function, no new excitation, no new
> algebra.**
>
> **What genuinely differs is that there is nothing outside the gap, and everything else follows from
> it.**
>
> - **An internal port is NOT de-embedded, and that is what it IS rather than a step postponed.** The
>   two-line calibration measures a *feed* and removes it. An interior cut has metal on both sides:
>   there is no feed, so there is no error box to solve for, no uniform line that could serve as its
>   standard, and no `Z_c` to reference the answer to. Building one would mean inventing a feed the
>   structure does not have and then removing it, which changes the answer by whatever was invented.
>   The reference plane is the gap itself and the published s-parameters are referenced directly to the
>   port's own declared Z₀.
> - **It takes an IDENTITY error box** (a₁₁ = 0, a₂₂ = 0, a₂₁ = 1) and `Z_c = Z₀`, so the two kinds
>   share one de-embedding path and one s-matrix without either pretending to be the other. That is
>   arithmetic which provably changes nothing — `PlanarDeembed.Apply` divides the mixed terms by
>   a₂₁(i)·a₂₁(j), so a unit a₂₁ also leaves a de-embedded NEIGHBOUR's terms untouched. Gated directly:
>   the same problem solved with de-embedding on and off agrees to < 1e-12 on an internal port.
> - **No automatic feed lead is grown for it** (R-fed-1 is about a calibration that does not happen
>   here), and `CheckFeedClearance` says nothing about it (there is no length of line the warning could
>   be about, and a warning a user cannot act on is one they learn to skip).
> - **The gap can only land on a mesh gridline, and how far it moved is REPORTED.** The displacement is
>   bounded by half a cell — and half a cell is a quantity the user sets, so absorbing it silently would
>   hide a positional error behind a mesh setting. `PlanarPortResolution.GapOffsetM` carries it and the
>   port's own note quotes it, naming a finer mesh as the remedy.
> - **The DIRECTION is required and is never inferred.** For an edge port `PlanarPortSide` names which
>   end of the conductor the port is on, and inferring it from the nearest boundary works because that
>   is what the label is near. A label in the middle of a conductor is roughly equidistant from all four
>   edges, so the same inference measures nothing — and for an internal port the side is not an end at
>   all, only the polarity of the port current. A port with no stated direction is refused by name.
>
> **A delta gap is a SERIES source, and the measurement is what says so.** A gap at the exact centre of
> a uniform line came back with **S₁₃ = −S₂₃ to sixteen digits** (and S₁₁ = S₂₂ likewise): the
> excitation pushes current one way along the conductor, into the line on one side of the cut and out
> of it on the other, so the two halves are driven in ANTIPHASE. An internal VIA port — current
> injected against the ground plane — is symmetric instead, and measures that way (below). The gate for this feature originally asserted the symmetric
> identity and the solve corrected it; that is worth recording, because the difference is a hard π and
> a magnitude plot would never show it.
>
> **What is still refused, unchanged:** a port driven ALONG a via between two levels (§0.2's own
> argument — a vertical basis has no end in the layout plane and no cell beyond the cut; a port from a
> level to the GROUND PLANE is a different object and is the next note), a coplanar or differential
> ground reference, and multi-mode ports.
>
> ### **THE INTERNAL PORT — the third port type, and the one whose current leaves the plane.**
>
> `PlanarPortKind.Internal`. The gap is at the foot of a via, between the metal and the ground
> plane, and the port drives that via's ground-attachment bases. It is what a component returning to
> ground, or a device terminal that returns to ground, attaches to.
>
> **The distinction from the delta gap is the RETURN PATH, not the position.** An internal delta gap
> is a **series** port: it cuts the trace, its two terminals are the two lips, and a component on it
> carries the whole current from one side to the other. An internal port does *not* cut the trace
> — its second terminal is the ground plane, and the current it drives leaves the conductor
> vertically. The planar-EM class of tool calls the same object a **via port** or an **internal
> port**; the 3-D field-solver analogue is a lumped port on a sheet between the trace and ground.
>
> **It is not a one-cell in-plane object, which is the natural first guess.** A single horizontal cell
> has no second terminal — there is nothing in the plane for a port to be *across*. The current path
> is necessarily **vertical**, from that cell to the ground plane, i.e. a via; the port is the gap at
> that via's grounded foot.
>
> **But that via is the SOLVER's to build, not the user's to draw** (owner, 2026-08-25: *"I want to
> use this type of port simply inside a metal area (no vias anywhere)… why make the distinction with
> the user?"*). The port is placed on the METAL and means "here, referenced to ground"; whether the
> artwork happens to have a via under it is a question about the drawing, not about the port. So
> there is ONE port type, and `PlanarGroundPath` — run before meshing, beside R-fed-1's feed
> extension and by the same three rules — resolves it: **a drawn via always wins**, a missing one is
> **built**, and **what was built is reported by port and by size**, because the path is real metal
> whose inductance the port's own answer carries.
>
> **How big, and why not a mesh cell.** A square of the technology's own default via drill, centred
> on the label — 0.305 mm on the shipped PCB starter, 60 µm on the MMIC one; failing both defaults,
> a quarter of the substrate height, stated in the notes as the rule of thumb it is. **A mesh cell
> was the obvious candidate and is the wrong one:** the path's inductance is part of the answer, so
> sizing it from the mesh would make the answer a function of *Cells per wavelength* — refining the
> mesh, the one thing a user does to converge a result, would move it for a reason that has nothing
> to do with convergence. A process dimension does not move. (It typically MESHES to about one cell,
> since its four edges are hard gridlines; that is a consequence, not the definition.)
>
> **The stackup's own `Via` entries are not consulted**, and cannot be: they carry a fill, a wall
> thickness and a span, never a diameter. "This technology declares no via" is therefore not a reason
> to refuse the port. What IS required is a ground plane to be the second terminal — and a stackup
> with none already refuses every port in the run, by name, before this question arises.
>
> **The excitation turned out to be D1's after all, and the reason is the normalisation.** The
> ground-attachment basis carries **unit total current across the connection it spans**, just as a
> horizontal rooftop carries unit current across its shared edge — the ramp degenerates to a uniform
> 1/Area over the footprint, but the normalisation is the same one. A delta gap of *v* volts in
> series with that path therefore has reaction `⟨f_m, E^imp⟩ = v` **exactly**, with no gap width, no
> footprint area and no quadrature in it. So the port is one more ±1 row of the same incidence matrix
> and one more column of the same `Y = BᵀZ⁻¹B`: **no new basis function, no new excitation, no new
> algebra** — the same sentence the delta gap earned. `NetCharge = −1` and the failure of
> `∫∇·f dS = 0` for an attachment (L9c's D5) are properties of the FILL, which was already built and
> gated; neither enters a reaction with an impressed field, which is an integral against the CURRENT.
>
> **What is specific to it, and every one of these is gated:**
>
> - **It drives the WHOLE footprint, not the cell under the label.** A via's footprint is one
>   conductor at one potential, exactly as a wide feed's transverse row is. Driving one cell of it
>   would leave the remaining cells shorting the trace straight to the plane beside the port — a
>   complete and plausible answer for a structure with a short across it. The footprint is walked by
>   4-connectivity from the label's own cell, and `PlanarPortResolution.FootprintAreaM2` reports the
>   area the mesh actually resolved, which is the quantity a coarse mesh silently shrinks.
> - **The POLARITY is fixed and is never asked for.** An internal delta gap's two lips are both
>   metal, so which is + has to be stated; here one terminal IS the ground reference, so + is the
>   metal and − is the plane. The label's own direction is not read.
> - **The sign of that convention is a MEASUREMENT, not a derivation.** It is `IncidenceSign = +1`,
>   the same "current flows into the structure" convention every other port uses. The first
>   implementation took −1, from a written derivation of which lip of the gap is "+", and produced an
>   s-matrix with every term through the port turned by π and nothing else changed — |S₁₃| right to
>   two figures at the wrong sign. What settles it is a structure whose answer is known
>   independently: a short line with a via to ground at its centre is three 50 Ω ports meeting at one
>   node above the plane, S_ii = −1/3 and S_ij = **+2/3**, and that is what a correctly-signed port
>   returns. **A port's sign is unobservable through any termination** — every reduction carries
>   `S_i3·S_3j` and both factors flip — so no amount of terminating the port could have caught it.
> - **A centred internal port gives S₁₃ = +S₂₃**, where a centred delta gap gives S₁₃ = −S₂₃. That pair is
>   the difference between a ground-returning port and a series one, stated as the measurement that
>   distinguishes them rather than as an intention.
> - **It is uncalibrated, like the gap**, for the same reason and by the same mechanism: nothing
>   outside the cut to remove, so `PlanarSolve.IdentityBox` and `Z_c = Z₀`, gated at < 1e-12 against
>   `Deembed = false`.
> - **Shorting it reproduces the plain board.** A port is a gap with a source in it, so terminating
>   that gap in a short is the same structure with no port on it at all: reducing the 3-port with
>   Γ₃ = −1 reproduces the ordinary 2-port solve of the same artwork to < 1e-9. That is the
>   end-to-end oracle for everything between the incidence row and the published matrix, and it needs
>   no external data.
>
> **`PlanarPorts.ViaPortRefusal` is unchanged and is a different question.** It refuses a port driven
> *along* a via, BETWEEN two levels — §0.2 item 2's option (b) — which has no cell beyond the cut to
> reference against. An internal port is driven between a level and the **ground plane**, which
> the Green's function terminates on analytically, and that is what makes it well posed.
>
> **What is still refused, unchanged:** a coplanar or differential ground reference, multi-mode
> ports, and a port between two meshed levels.
>
> **Where every port's type is set:** `EmSetup.PortKinds`, per port, in the `.cem` beside `PortZ0s` —
> never on the port label. A layout is geometry: the same artwork can be analysed with a gap in the middle of a trace in
> one setup and driven from its ends in another, and neither should edit the drawing. The list is
> **omitted from the file when every port is an edge port**, so a `.cem` written before this gains no
> byte. **`EmSnpProvenance.PortHash` includes the type** — changing it moves the excitation and turns
> de-embedding off, so an `.snp` written under one type is not current for the other — but appends it
> only when a port is non-default, so no `.snp` this application has ever written reports a one-time
> false staleness.

### 10.7 Solver and the size budget

**v1 (quasi-static).** The matrix is **real, dense, and small** — a few hundred boundary segments, so
under a megabyte — and it is factored **once for the whole frequency sweep** (§10.3.2). Runtime is
milliseconds. There is no size budget to police at this stage; the constraint below is what arrives with
the full-wave kernel, and it is stated now so nothing in the architecture assumes solves are cheap
forever.

**L8 onward (full-wave).** The matrix is **dense and complex**: N unknowns → N² × 16 bytes.

| N | Matrix memory | Character |
|---|---|---|
| 500 | 4 MB | Instant. The microstrip hero lives here. |
| 2,000 | 64 MB | Interactive: seconds per frequency. |
| 5,000 | 400 MB | The practical ceiling for "lightweight". |
| 10,000 | 1.6 GB | Out of scope without ACA/MLFMM compression. |

**R17. Declare a hard N ceiling (~5000), surface the predicted N before solving, and refuse politely
above it** with a message pointing at mesh coarsening. A "lightweight" simulator that silently tries to
allocate 12 GB is not lightweight.

> **Built, and the ceiling is now TWO numbers (2026-08-14).** `SurfaceMesher.UnknownCeiling = 5,000` for
> the dense path, exactly as above, and **`AcceleratedUnknownCeiling = 12,000` for the AIM-accelerated
> solve on a SINGLE-LEVEL mesh** — a multi-level or via-bearing problem is refused by name regardless,
> so the ceiling there is still the dense one. Two other corrections to the paragraph above, both from
> real runs: the refusal must name a remedy that **binds** (on any wide-to-narrow part the transverse
> pitch is set by the narrowest metal, so "coarsen the mesh" changes nothing and the message says so),
> and de-embedding's own reference-impedance step is a **separate, always-dense** cell system the
> accelerator does not reach — a wide port's calibration standard can refuse a run whose DUT would have
> succeeded. `src/Engine/Mom/CLAUDE.md` §4 and §8 carry the measurements.

Sanity check on the hero: 50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line at 10 GHz has
λ_g ≈ 16.5 mm, so λ_g/20 ≈ 0.8 mm → ~24 cells long × ~6 across with edge refinement → **N of a few
hundred**. Genuinely fast. A spiral inductor lands at 1–3k. The scope is realistic.

Fill is O(N²) singular integrals; solve is O(N³) LU per frequency; both must be recomputed per frequency
because the Green's function is frequency-dependent. Since RF users sweep many points, **adaptive
frequency sampling** (solve sparsely, rational-interpolate, refine where the model disagrees) becomes
essential at L9 — it typically cuts solve count by 5–10× and is the best performance investment after
the mesh. Build it only when the kernel that needs it exists; v1 does not.

> **Measured at L8c (2026-08-05), on the fill and factorisation as built.** At 10 GHz on FR-4, per
> frequency, with the frequency-independent geometric core built once:
>
> | N | cells | kernel fit | core (once) | fill | LU | matrix | cached core | per freq | 101 points |
> |---|---|---|---|---|---|---|---|---|---|
> | **552** (the hero above) | 297 | 0.21 s | 2.87 s | 1.48 s | 0.04 s | 4.6 MB | 2.4 MB | **1.73 s** | **178 s** |
> | 1,956 | 1,012 | 0.20 s | 13.9 s | 6.80 s | 2.08 s | 58 MB | 30 MB | 9.08 s | 931 s |
> | 4,933 (≈ R17) | 2,520 | 0.20 s | 53.9 s | 21.8 s | 42.8 s | 371 MB | 188 MB | 64.8 s | 6,599 s |
>
> **Three corrections to the paragraph above, all in the same direction.** First, *"solve is O(N³) LU
> per frequency"* is true and is **not yet the constraint**: the O(N²) fill is 114× the LU at N = 552
> and is still 1.8× it at the ceiling, so the crossover has not been reached inside R17's own budget.
> Second, the hero's *"Instant"* is a statement about its 4 MB matrix, not about its sweep — a
> 101-point sweep of it is about **three minutes**. (§10.10's 30-second target is an *interaction*
> budget and is unaffected.) Third, the **400 MB line is optimistic by half a matrix**: reusing the
> frequency-independent core is worth 62% of a single-frequency solve at the hero size and 45% at the
> ceiling, but its cached arrays add **51% on top of the matrix** — 559 MB resident at N = 4,933.
>
> **So adaptive frequency sampling is no longer a "build it at L9" item**; the kernel that needs it now
> exists. The cheaper first move, though, is per-cell-pair moment caching in the vector block: adjacent
> rooftops share cells, so the same cell pair is currently integrated up to four times. See
> `src/Engine/Mom/CLAUDE.md` §L8c for both.
>
> **Measured again at L8d (2026-08-05), with ports and de-embedding on, and the multiplier is 4.4×.**
> The table above is the cost of filling and factoring the DUT alone. A de-embedded answer also solves
> the calibration standards at every frequency, and they are not small — on the same hero they measure
> N = 297 / 382 / 331 / 416 against the DUT's 552, i.e. **2.58× the DUT's own unknowns**:
>
> | per frequency, N = 552, FR-4 at 10 GHz | | 101 points |
> |---|---|---|
> | kernel fit (`Dcim.Fit`, shared across all meshes) | 0.20 s | 20 s |
> | the DUT (fill + factor + excite) | 1.47 s | 149 s |
> | **the calibration standards** | **5.98 s** | **604 s** |
> | **total** | **7.66 s** | **~780 s** + 10 s of cores |
>
> (The DUT column reproduces L8c's own 1.48 s fill to 1%, which is what says the two measurements are
> comparable. Both runs above were taken in isolation and repeat to 1.5%; the same test run alongside
> nine other benchmark tests reads more than twice as slow, so **measure this one alone or not at all**.)
>
> **The standards are 78% of the cost, so the first saving to take is not in the fill at all.** Two are
> identified and neither needs new numerics: (1) the two ports of a plain microstrip *should* share one
> calibration and do not, because L8b's edge grading is not exactly mirror-symmetric end to end —
> **making it symmetric is worth 2× here**; (2) a calibration is a first-class reusable object, so a UI
> that caches one per feed cross-section pays for it once across every DUT that shares it. Adaptive
> frequency sampling is worth correspondingly more than §10.7 assumed, because the per-point cost went
> up 4.4× while the number of points did not.

All of it lives in `src/Engine/Mom/`, uses NumFlat for the dense factorisation, and touches no UI.

### 10.8 Results and EM/circuit co-simulation

The solver returns s-parameters → a `DataSet` with an `S` cube → the existing Data Display plots it and
the existing Touchstone exporter writes it. **No new result type**, per the standing invariant.

**Decided: an EM run produces an `.snp` artifact.** This resolves the standing constraint *"Analyses
attach to a `TestBench`, never to a `Cell`"* — an EM setup naturally attaches to a **layout view**,
which is a cell view, and would otherwise violate it.

**R17a. An EM setup is its own document — a `.cem` — that REFERENCES a layout**, and **running it
writes an `.snp` file** plus returning a `DataSet`. The schematic consumes that artifact through the
**existing SnP component** — no new analysis kind, no change to the testbench model, no new result type.

> **Revised at L6/L7 (brief-L6-L7-em-ui.md D1).** This rule originally read *"an EM setup is a property
> of the layout, persisted in the `.clay`"*. The standalone document serves R17a's own stated purpose
> better: the standing invariant *"analyses attach to a `TestBench`, never to a `Cell`"* is satisfied
> more cleanly by a setup that is not embedded in a cell view at all, and it buys three things
> embedding does not — several EM setups against one layout, editing a setup without dirtying the
> `.clay`, and a setup that is independently diffable and versionable. A `.cem` is workspace-scoped and
> never scratch (mirroring `.ctech`), and it names its layout by workspace-relative path, **never by
> embedding geometry** — which is exactly why re-running after a layout edit picks the edit up.

The consequences are all good ones:
- **EM/circuit co-simulation for free.** Lay out a matching network, EM-simulate it, drop the resulting
  `.snp` into the harmonic-balance testbench next to the real device model. That workflow is what makes
  this feature valuable to a PA designer rather than a curiosity, and it needs no new machinery.
- **The artifact is inspectable and portable** — a Touchstone file the user can plot, archive, hand to
  a colleague, or diff against a measurement.
- **Re-running is a file update**, so the schematic picks up the new result the same way it picks up any
  changed SnP source.

> **Extended at L8e (2026-08-05) — still no new result type, and the diagnostics group is per KERNEL.**
> Whichever kernel runs, the `DataSet` has the same shape: `S`, per-port `Z0`, and **one** diagnostics
> group. Kernel A's is `"tline"` (Zc, Gamma, Eeff, AttenDbPerM, Rpul, Lpul, Gpul, Cpul); kernel B's is
> `"planar"` (Gamma, Zc, Eeff, AttenDbPerM, Cpul, CalElectricalDeg, DeembedResidual, DeembedRejected,
> CalibrationUsable). **They deliberately do not share a name.** A per-unit-length quantity from a 2-D
> quasi-static solve and one back-solved from a de-embedded full-wave S-matrix are different claims;
> they agree on a uniform line — that agreement is L8's phase gate — and they diverge with frequency,
> which is dispersion and is a *result*. One shared group would let a Data Display trace silently mix
> the two in any project that contains both kinds of run.
>
> The staleness stamp below now covers the **planar** problem too — geometry, mesh settings, and ports
> each hashed separately, so the warning says *which* of the three moved. Without that it would have
> gone on stamping the cross-section for a run that has no cross-section, and staleness detection would
> have quietly stopped working for kernel B while still appearing to be on.

Two details worth fixing now: write the `.snp` to a **predictable path** derived from the cell and setup
name (mirroring `RunResultsWriter`'s convention) so the schematic's reference is stable across runs; and
stamp the file's comment header with the stackup, mesh settings, port definitions, and a hash of the
geometry, so a stale `.snp` sitting next to an edited layout is **detectable** rather than silently
wrong. That staleness check is the one failure mode this design introduces, and a header stamp plus a
Messages warning on mismatch is the whole mitigation.

### 10.9 Validation oracles

Same philosophy as the MINT work: prefer self-consistency checks that need no external tool, plus a few
closed-form anchors.

**Closed-form anchors:** Hammerstad-Jensen microstrip Z₀ and εeff (±2% is a reasonable gate); coupled
microstrip even/odd-mode impedances; a quarter-wave open stub's resonant frequency; a known
Rogers-substrate line.

**Learned at L7 (2026-08-04): validate the charge solver against *exact* closed forms before comparing
anything to Hammerstad-Jensen.** H-J is an empirical fit, so a ±2% agreement against it can hide a real
defect and a disagreement tells you nothing about which of five stages is wrong. The ladder that
actually worked, each tier passing before the next was written: (0) the potential and field integrals
vs quadrature and vs a finite difference of each other; (1) coax `2πε₀/ln(b/a)`, wire-over-ground
`2πε₀/acosh(h/a)` — this is what tests the image ground — and two parallel wires; (2) two-layer coax
`2πε₀/[ln(r_m/a)/ε₁ + ln(b/r_m)/ε₂]`, which is the only cheap closed form that genuinely exercises a
dielectric interface, plus a fully-filled coax and a lossy fill for the complex-ε* path; then (3) H-J.
Two of these caught real defects that the ±2% H-J gate had passed.

**And H-J is not the arbiter where it is itself extrapolated.** Its finite-thickness correction widens
W, which *raises* εeff; a boundary-element solve of the real rectangle sees the strip's side faces in
air, so a thicker strip *lowers* εeff. At t/W ≈ 0.2 — ordinary 35 µm copper on a narrow strip — the two
disagree by ~5% and H-J is the one outside its regime. Gate against it at a thin strip across the full
W/h span, and against real metal only where t/W is small. Same lesson for `MicrostripLoss`'s
`α_c = R_s/(Z₀W)`: it is a wide-strip asymptote that over-counts the ground plane, and the right check
is that a proper Wheeler computation approaches it monotonically from below as W/h → ∞ (measured 0.40
at W/h = 0.3, 0.96 at W/h = 50), not a fixed tolerance band at one geometry.

**Oracle-free self-consistency, which catches most real bugs:**
- Reciprocity: S₁₂ = S₂₁ to solver tolerance.
- Passivity: eigenvalues of I − SᴴS ≥ 0.
- Losslessness: with σ = ∞ and tanδ = 0, |S₁₁|² + |S₂₁|² = 1.
- Mesh convergence: refine the mesh, results must converge monotonically rather than wander.
- A uniform line of length 2L must equal two cascaded lines of length L.
- Reference-plane invariance: moving the de-embedding plane must only rotate phase.

**Added at L8a (2026-08-05), because the full-wave kernel needs oracles kernel A did not.** The same
ladder discipline, one tier lower: (−1) the special functions themselves, against an *integral
representation* and the Wronskian, before anything uses them; (0) the spectral-domain function alone,
before any inverse transform exists — a spectral function that is wrong produces a spatial function
that is wrong in a way no downstream oracle can localise; (1) the exact reductions, of which
**εᵣ = 1 collapsing to free space plus one image** is the strongest and is the direct analogue of the
image gate that validated kernel A's R-mom-7; (2) the production method against **direct numerical
Sommerfeld integration**, which shares no approximation with it. Two of these caught real defects,
and in one case the defect was in the *oracle* — see `src/Engine/Mom/CLAUDE.md` §L8a. Note also that
**losslessness does not survive into kernel B**: an open planar structure radiates and launches
surface waves, so |S₁₁|² + |S₂₁|² < 1 legitimately. Reciprocity and passivity carry over.

> **And kernel B's metal is a PERFECT CONDUCTOR, which the stackup editor's own σ field does not say.**
> `PlanarConductorLayer.SigmaSm` and `ThicknessM` are carried through the whole pipeline and are never
> read by the fill — only kernel A's Wheeler term (§10.3) uses conductivity. So a full-wave insertion
> loss carries dielectric loss and radiation but no conductor loss, and reads slightly optimistic by a
> known amount: **6.5% / 3.0% / 2.1%** of the total conducted loss at 2 / 10 / 20 GHz on 1.6 mm FR-4.
> It is stated here, and in the user-facing page's own "Cannot" list, because a σ field the user can
> edit and the solver ignores is exactly the shape of thing that gets trusted silently. Adding it needs
> a surface-impedance term in the fill — named in `src/Engine/Mom/CLAUDE.md` §5, not built.

**Regression golden data** reviewed and approved by the owner before it becomes a gate — the established
project pattern.

### 10.10 The 30-second target, as an acceptance test

**R18. "Draw a microstrip line and configure a MoM sim in under 30 seconds" is written as a scripted
acceptance test with a click/keystroke budget, and it gates the MoM phase.**

The path it measures:

| Step | Interaction | Budget |
|---|---|---|
| New layout from the workspace's starter template | 1 click — stackup, layers, units all preset from `.ctech` | 3 s |
| Draw the line | Path tool, click start, click end, type `W = 2.9mm` in the live dimension field | 8 s |
| Frequency | EM panel: `1` `20` `GHz`, 101 points | 8 s |
| Mesh | Untouched — `Auto` is the default and is correct | 0 s |
| Run | 1 click | 1 s |

Total ≈ 20 s.

> **The "Ports — Port tool, click each end, 5 s" row is gone (brief-L6-L7-em-ui.md D5).** For a uniform
> cross-section the two ports simply ARE the two ends of the extracted line, by construction — the same
> fact that makes de-embedding a no-op for kernel A (R-mom-15): there is nothing to place because there
> is no meshed port to place. The `.cem` carries per-port Z₀ and nothing else. A Port tool becomes real
> work at **L8**, when a meshed port exists, and `PinInference` is what it should be built on then
> rather than a new picking mode. The target got *easier*, not harder. **The chosen v1 kernel covers this case exactly** — a uniform microstrip line is precisely
what a quasi-static per-unit-length solver is for — so the headline acceptance test is satisfied by L7
rather than waiting on full-wave. The same test runs against the MMIC starter tech with a GaAs line, so
both markets are gated (§2.4).

What makes the target achievable is not speed of interaction but **defaults that are already right**: preset stackups, auto mesh, ports that need no placing at all (D5), 50 Ω, and numeric entry with unit suffixes
(R6). Design the defaults first and the target falls out; design the dialogs first and it never will.

### 10.11 Kernel W — 3D wirebond simulation

**Full design: [`mom-wirebond-kernel.md`](mom-wirebond-kernel.md).** Summarised here because it changes
§10's kernel inventory, `EmCapabilities`, and the phasing table, and because §10.1's original wording
excluded it.

**What it is.** A **thin-wire MoM kernel** for bond wires: ball- and wedge-bond loop profiles, 0.5–1.25
mil radii, 5–50 mil loop heights, 5–300 mil pitch, arrays to 200 wires, with full mutual coupling
between every wire. This is the founding problem of computational EM (Harrington/Richmond/NEC), and for
this geometry it beats FEM decisively: unknowns scale with **wire count, not with the volume of air
between the wires**, the 1 mil radius collapses into an analytic kernel rather than a meshing problem,
and the radiation condition is exact — no airbox, no PML. Where FEM still wins: inhomogeneous 3D
dielectrics, complex 3D metal (leadframes, clips, lids), cavity resonance, and field plots.

**It is a separate kernel, not an extension of A.** Kernel A is a 2D cross-section solver and cannot see
a wirebond. It is also **not unlocked by C** — a stepped ground is a *lateral* variation, precisely what
the 2.5D premise forbids, so DCIM buys nothing here. Kernel W registers against the §10.3.4 interface
and shares the `.ctech` stackup, the port model, the sweep UI, the mesh viewer, the results plumbing and
the validation harness.

**Staged the same way as A→B→C, for the same reason:**

| | Kernel | Property |
|---|---|---|
| **W1** | Quasi-static PEEC — partial inductance (Neumann), coefficients of potential, exact round-wire Bessel internal impedance | **Frequency-independent matrices — fill once, sweep free**, exactly as kernel A |
| **W2** | Retarded thin-wire MoM — add `e^{-jkR}` to the mutuals | Genuine full-wave; per-frequency refill. A flag on the same kernel, not a second kernel |
| **W3** | Wires in the layered stack | Needs DCIM. **Named, not promised** — downstream of C |

**Scope tiers.** **T1 = wires only** over an image ground plane: free-space kernel, one exact image, no
wire-to-surface junction, ports at the wire ends, its own `.snp` cascaded in the schematic with the
planar result. T1 ships and is useful standalone. **T2 = wires + meshed surfaces**, and this is the
scheduling insight worth carrying: **one piece of machinery (surface panels + wire-to-surface junction
basis functions) unlocks three separately-requested capabilities** — coupling to landing pads, a finite
overmold body, and discontinuous ground. None is obtainable without the junction; all three arrive
together. T2 is the larger half of the wirebond effort and must be budgeted as one deliverable, not as
three small additions to T1.

**Overmold.** The fact that shrinks it: **mold compound is non-magnetic**, so it touches [P] and [G] and
**never [Lp]**. Inductance — the dominant bondwire parasitic — is unaffected by the mold model; only
capacitance, delay and a small dielectric loss carry its error. Ship homogeneous fill as a mode and
**bound charge on the mold surfaces** as the real thing, which is the direct 3D analogue of §10.3.1's
already-chosen formulation and handles a finite cap, sidewalls, die attach and the die surface with the
free-space kernel. The accuracy floor is the EMC datasheet (εr 3.4–4.5, poorly characterised above a few
GHz), which is itself the argument for stopping there.

**Discontinuous ground.** A flat plane is free because it is an image, exact only for a laterally
infinite plane; a z-step kills the image, the dielectric image series **and** DCIM. The answer is to
mesh the ground as a conductor — cheap for [P] (charge panels at V = 0), the real work for [Lp] (surface
current cells, full Ruehli PEEC). Keep it affordable by hybridising: semi-infinite image plane for the
lower tier, meshed panels only for the raised structure, graded because return current spreads over
roughly ±2h. Ignoring a 20 mil step under half a span costs ~6% on L; ignoring a **ground gap** costs
30–50%+.

**Sizing.** ~25–30 segments per wire (set by arc fidelity, not wavelength): 8 wires ≈ 250 unknowns,
40 wires ≈ 1,200, 200 wires ≈ 6,000 → 576 MB dense, ~3 s LU per frequency. Only the 200-wire extreme
brushes R17's ceiling; meshed ground is what pushes it over, and that is where ACA first earns its keep.

**The PRD tension, resolved.** A wirebond is a **parametric component instance whose layout view is its
2D projection** plus an annotation — `.clay` gains no 3D shape type and no volumetric mesher is written,
so "layout is 2D" and "no volume meshing" survive untouched. The PRD's §2 non-goal was **narrowed on
2026-08-04 (PRD v1.3)** from "no 3D full-wave EM" to *no FEM, no volumetric meshing, no arbitrary 3D
geometry* — the old wording would have excluded a solver that requires none of those things.

**Two things this design insists on, because they are how bondwire models usually go wrong:**
- **Ports carry an explicit reference conductor.** Partial inductance is not a physical quantity on its
  own; "the inductance of this bond wire" is meaningless without a stated return path.
- **Ground bond wires are ordinary wires, not a boundary condition.** Modelling only the signal wires
  against an assumed perfect plane reports optimistically low inductance. Conversely, a user who
  declares downbonds explicitly gets much of the stepped-ground effect in **T1**, before any surface
  kernel exists.
