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
- **Via layer**: connects two conductor layers; bound to a drawing layer. Drawn via artwork is
  either the point `ViaShape` (meshed as its equal-area square) or a filled REGION — a rectangle or
  polygon on the bound layer, which is the MIM plate-connection case and reaches the mesher at the
  outline it was drawn. *(> Built at MIM-1, 2026-08-30.)*

Presets ship as starter technologies rather than as a combo box on this tab — two- and four-layer
laminate boards, a low-loss microwave board and a MMIC process, each a whole `.ctech` a New Workspace
can be created against — because preset-then-tweak is what makes the 30-second target reachable. Linear isotropic
only, as specified; the model should nonetheless leave room for anisotropic εr later (real laminates
are anisotropic).

#### 10.4a The tab is a DRAWING over a list — **shipped 2026-09-13**

**This section used to say the editor was an ordered top-to-bottom plain list with no diagram. It is
not, and has not been since the `R-stk-n` series landed.** The Stackup tab is a cross-section drawing
above a card list, split by a `GridSplitter`, and the drawing is the primary surface: click a band to
select it and land on its fields, double-click a value to edit it in place, drag a band to reorder it,
drag a via's ends to change its span or its side to slide it, right-click for delete / add via /
plated / fill, `Esc` to clear, right-click ▸ Copy to put the whole picture on the clipboard. The card
list below is still there and still edits everything; the two are two VIEWS of one stackup and never
two models of it — every mutation the canvas performs calls the function the card already calls, so
undo/redo, live validation, the dirty mark and the live-technology push all follow without a second
path to keep in step.

The drawing itself is `StackupScene` + `StackupRenderer` in `src/Render`, below the UI firewall and
with no Avalonia in it, for the reason that project exists: the tab, the clipboard export and the
documentation figures are one picture rather than three that have to be kept looking alike by hand.
The scene is laid out ONCE per `(Technology, width)` and read twice — the renderer draws its rects and
the control hit-tests the same ones — so the picture and the pointer cannot disagree.

**Band heights are relative WITHIN a kind and never across kinds.** A half-thickness conductor draws
half as tall as another conductor; it is never compared with a dielectric, because the shipped MMIC
process spans 0.2 µm to 100 µm of dielectric alone and drawn honestly against metal every conductor in
the picture would be a hairline. Where a kind's own dynamic range will not fit its height budget the
mapping compresses, monotonically, so the ordering stays truthful; the real thickness is printed on
every band, which is the number a reader needs. **The drawing says nothing about any of this** — no
note, no asterisk, no "not to scale" caption. Which branch was taken is scene metadata
(`ConductorsCompressed` / `DielectricsCompressed`) that tests and figure captions read and nothing
draws. The rule is written down once for users, in
[the Stackup chapter](../user/src/reference/stackup.md).

#### 10.4b `StackupLayer.DrawLaneFraction` is COSMETIC, and must stay cosmetic

A via entry carries one field that exists only for the drawing: `DrawLaneFraction`, the fraction of
the drawn band column (0 = left edge, 1 = right edge) at which its barrel's centre sits, null meaning
"wherever the automatic lane assignment puts it". It is additive and nullable on the
`SheetAt`/`PresentWithLayer`/`Fill` precedent — no `FormatVersion` bump, ignored on a non-via entry,
not written when null — and a value outside [0, 1] in a hand-edited file is clamped on read rather
than refused, because a drawing position is never worth failing a load over.

**Nothing downstream of the drawing may read it.** A stackup is a cross-section of a laterally
infinite sandwich and a via entry is a KIND of connection between two named conductors, not one hole
at one place; a lateral x in this model is one careless read away from being mistaken for geometry.
That is held by a test rather than by this paragraph: a technology differing only in this field
extracts to an identical `EmProblem`.

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
  the adjacent coplanar conductors. Get this wrong and everything downstream is wrong. *(Built: the
  ground-plane reference for every port type; a reference to DRAWN metal — both `CoplanarGround` and
  `SecondConductor` — for an INTERNAL DELTA GAP since RP-2a and for an EDGE port since RP-2c, which
  builds the coplanar calibration standard the edge case needs. A port between the two levels of a via
  is still refused by name.)*

> **RP-2a (2026-09-10) — a conductor-referenced port is TWO CUTS AT ONE STATION, and the sentence above
> is now only half true.**
>
> A port referenced to drawn metal is **not** a second ground plane, and cannot be: a `LayerStack` has
> exactly two terminations and no interior PEC, so a second reference plane has no representation in
> the medium at all. What is reachable is the same capability by the other route — a delta gap in the
> signal conductor and a **second delta gap in the return conductor at the same station**, driven
> against each other. The incidence column carries a signed block on each row set; the mesh, the basis
> set, `Z`, the factorisation and `Y = BᵀZ⁻¹B` are all untouched.
>
> It is two cuts because it cannot be one. The surface mesher does **not** produce a basis spanning the
> slot between two conductors and structurally never will (every polygon edge is a hard gridline, a cell
> exists only where a grid row's centre is inside metal, and a basis is a pair of grid-adjacent cells —
> so a slot of nonzero width always owns a metal-free row and refining *adds* rows to it). Measured
> before any solver work; `src/Engine/Mom/RESOLVED.md` carries the table.
>
> - **Each cut carries HALF the port's voltage.** The two gaps add around the loop, and the port current
>   is read back through the same column, so the incidence weight scales `Z₁₁` by `1/w²` — ±1 on both
>   blocks reports a **quarter** of the right impedance, with no visible symptom. Gated against the
>   coplanar-strips conformal-mapping closed form `Z = η₀·K(k)/K(k′)`.
> - **`InternalDeltaGap` only, until RP-2c.** A conductor-referenced EDGE port has an error box, and its
>   calibration standard is a *coplanar* line with its own `Z_c`, `β` and static capacitance.
>   `PlanarCalibration` built uniform single conductors over the plane, so the edge case was refused by
>   name, naming the missing standard. **RP-2c (2026-09-10) built it — see the block below**; the
>   refusal is gone and the refusals about the PAIR are unchanged and now fire for edge ports too.
> - **Three things about the PAIR are refused rather than adjusted**: two cuts at different stations (a
>   port plus a length of line), two cuts *inferred* onto different levels (stating both levels permits
>   it), and two cuts in one conductor — which is either a short across the port, or an ordinary
>   internal delta gap wearing a costume.
> - **A mixed run is legal and is the point**: one solve, one matrix, one medium, port 1 returning
>   through the plane and port 2 through drawn metal. `PlanarPortResolution.Describe()` therefore says
>   what each port returns through, per port — the standing "the plane is the negative terminal of every
>   port in this run" sentence is what a mixed run makes false.

> **PEEL (2026-09-14) — THE PEEL HAS A CONDITIONING LIMIT OF ITS OWN, AND `DeembedResidual` IS NOT A
> QUALITY MEASURE BELOW IT.**
>
> The two-line calibration is exact algebra, and at the bottom of a band it still publishes a number
> that is 59 dB wrong. The reason is in `PlanarDeembed.Apply`, not in the calibration: the peel forms
>
> ```
> Y = (S_meas − a₁₁) / a₂₁²
> ```
>
> and an edge port at low frequency is a **series gap capacitance** — the cut sits one cell inside the
> drawn metal, so the source drives the structure through a fringing capacitance and nothing else.
> That makes `a₂₁ ∝ ω` and `a₁₁ → 1`: the numerator is the difference of two numbers that both
> approach 1, and the denominator is an ω². **The ω² is the amplifier**, and it is why a de-embedded
> point at the bottom of a band is wrong by a factor that grows as 1/f while nothing published moves.
>
> **The amplification is NOT the two standards' separation, and that was measured rather than
> reasoned about.** A 30× longer second standard — βΔℓ from 0.40° to 11.8° at 10 MHz — moves the
> de-embedded |S₁₁| of a uniform line by under 6 %, and at 100 MHz a 30× separation is *ten times
> worse*, because the two standards' `M₁₁` inconsistency grows with the length difference while the
> amplifier it is multiplied by does not move at all. Lengthening the standards is the obvious move
> at the bottom of a band, it is exactly the 1/f_lo standard the quasi-static path exists to remove,
> and it does not work.
>
> **What is published.** `DeembedErrorFloor`, per (frequency, port), in the diagnostics group beside
> the two residuals:
>
> ```
> DeembedErrorFloor = DeembedResidual · |a₁₁| / |a₂₁|²
> ```
>
> It is in |ΔS| — the units of the answer — so it reads against a tolerance directly. Measured
> against the uniform-line control, where a de-embedded S₁₁ must be exactly 0, it tracks the realised
> error to a ratio of **0.73–1.06** over three stacks, three mesh densities and three decades of
> frequency. It is a **floor on the instrument's own error, not a bound on the answer**: it is what
> the peel does to a structure it was calibrated for, so it says nothing about the DUT's own meshing
> or about radiation.
>
> **`DeembedResidual` is not the number to read, and down there it points the wrong way.** It measures
> the two standards' agreement with each other, and two standards whose `x = e^{−γℓ}` are both
> approaching 1 agree better and better while determining the error box less and less. On the board
> this was reported on it reads **1.95e-10 at 10 MHz and 9.38e-9 at 500 MHz** — 48× *smaller* exactly
> where the published answer is worst. A reader checking de-embedding quality by that cube is told the
> bottom of the band is the best-calibrated part of the sweep. That is not a defect in the number; it
> is honest about what it measures, and what it measures is not accuracy. The residual is ∝ ω and
> `|a₂₁|²` is ∝ ω², so their quotient — the error — is ∝ 1/ω, which is the law measured.
>
> **The guard, and it is PER POINT.** `PlanarSolve` reads the floor off the points the sweep already
> produced (nothing is re-solved, and a run nowhere near the thresholds takes exactly the arithmetic
> it took before). Points above `PeelErrorBudgetDS` = 0.05 are **named in the run's notes and still
> published**; a point at or above `PeelErrorRefusalDS` = 0.25 is **left out of the sweep** and named
> by frequency, so the gap in the `.sNp` is never a mystery. Both thresholds are anchored on what a
> perfect match would be published as (−26 dB and −12.0 dB respectively), and −12 dB is the shape of
> the report itself.
>
> **The rest of the sweep is published exactly as it was solved — the run is not thrown away for the
> points it cannot answer.** That is this file's own rule for a far field that cannot be produced
> ("present and refused"), and it applies here for a stronger reason: the unanswerable points are at
> the *bottom* of a band, so refusing over them costs the user every higher frequency they waited
> for. The one case that is still a refusal is the sweep in which *every* de-embedded point went,
> where there is no result to hand back and nothing is lost by saying so.
>
> Neither threshold is a `.cem` field or a panel control; the way to get the dropped points into the
> file anyway is `PlanarSolveSettings.DeembedOutsideCalibrationValidity`, which already exists and
> already says so in the notes and in the `.sNp`'s provenance.
>
> **What the run actually says is three sentences and none of the above is in it** — which points are
> missing, the frequency above which de-embedding on that port is reliable, and that the
> `DeembedErrorFloor` cube carries the per-point number. The mechanism is here and in the source,
> because that is where someone asking "why" looks; a run's notes are for someone asking "what do I
> do", and a note too long to read is worth nothing however true it is. The gate holds it under 400
> characters and rejects words in block capitals.
>
> **This is the FOURTH wall at the bottom of a band and it is the highest of them.** The other three
> are §10.13's: the calibration standard's own N, the DCIM fit's floor, and the raw uncalibrated
> answer being the port rather than the structure. The fit's substitution note used to be the only
> sentence down there, and it reads as "trouble ends here" when it is a ceiling on one wall; it now
> says which wall it is.

> **RP-2c (2026-09-10) — de-embedding a coplanar edge port. The STANDARD changed; the ALGEBRA did not.**
>
> D4's rule is unchanged and is the whole point of this phase: the calibration standard must rebuild the
> port's neighbourhood *exactly*, and a coplanar port's neighbourhood is three pieces of geometry, not
> one — signal conductor, slot, return conductor. So `PlanarPortResolution` now carries the transverse
> METAL PROFILE at its reference plane (`PlanarPortCrossSection`, taken from the DUT's own mesh at the
> cut), `PlanarCalibration.BuildCoplanarLine` extrudes that profile to the two calibration lengths, and
> **the standard's own two ports are two-cut ports at one station**, resolved through `PlanarPorts` like
> any other — the skew, level and same-conductor refusals are the same ones. The two-line trace, the γ
> branch resolution and the T-matrix cascade are untouched (R-rp2c-3): they are exact regardless of
> cross-section.
>
> - **A de-embedded coplanar uniform section is exact at the two calibration lengths** — |S₁₁| at the
>   **1e-15** level across five geometries, which is L8d's own microstrip figure and is the measurement
>   that says the standard IS the port's neighbourhood. γ two ways (the two-line trace against a
>   travelling-wave fit sharing no algebra with it) agrees to **2.0e-5 … 1.1e-4**, inside L8d's
>   2.5e-4 … 3.9e-3 microstrip band.
> - **D7's reference impedance is the MODE's, not the sheet's.** A microstrip standard's C_pul is the
>   whole sheet at 1 V over the plane; a coplanar pair's port drives the voltage BETWEEN the two
>   conductors, so the electrostatic problem is +½ V on the signal and −½ V on the return and the
>   capacitance is `(Q⁺ − Q⁻)/2` per volt. Totalling the sheet's charge instead measures the COMMON
>   mode — a complete, plausible reference impedance for a mode the port does not drive. Both the dense
>   and the accelerated (`PlanarStaticAim`) static solves take the mode; the shipped all-ones route is
>   untouched and is what a single-conductor standard still runs.
> - **A THIRD conductor at the reference plane is refused by name at calibration setup.** A CPW's far
>   ground strip has no stated potential, and the two reasonable readings — bonded to the return, or
>   floating at zero net charge — are far apart. The refusal names both and offers the remedies
>   (join the grounds before the plane, move the station, cut an interior gap, or read the raw solve).
> - **The mixed case is the ordinary case** and costs what it costs: one DCIM fit still serves the DUT
>   and every standard, because the fit depends on (slab, frequency) alone and a standard's SHAPE is not
>   part of that key. What a coplanar standard costs is unknowns — measured on one port at one length,
>   **2.86× the single-conductor standard's N and 2.27× its fill-and-solve time**.
> - **The direct port-to-port coupling residual is much SMALLER with coplanar grounds present.** Measured
>   against a microstrip control on the same slab, the same mesh and the same frequency: away from the
>   calibration lengths a coplanar section reads |S₁₁| = 8.2e-6 / 2.3e-5 / 1.6e-4 at 5 / 10 / 20 GHz
>   against the microstrip's 1.1e-3 / 3.2e-3 / 5.4e-3 — **35–140× lower**, with the same rough f²
>   scaling. The coplanar mode's return current is beside the line rather than in the plane below it,
>   so less of the field goes around the structure.
> - **A conformal cut cell at a conductor-referenced port is refused**, because the single-conductor
>   path absorbs a cut by re-centring the cross-section on the metal, and re-centring two conductors
>   independently moves the slot between them — which is most of what sets a coplanar line's impedance.
> - **An EDGE pair on two LEVELS is refused, even when both were stated.** RP-2a permits that for an
>   internal delta gap, which has no standard; an edge port's standard is a uniform line on ONE level
>   (D3), and a level change in the middle of the section the algebra assumes uniform is exactly the
>   thing a standard may not contain.
> - **The reference plane is still not user-positionable** (see the L8e block below), and it is now two
>   planes that must be one: the pair check is what makes them one, and the coordinates the layout draws
>   are still the engine's own.

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

> ### **QSC (2026-09-14) — DE-EMBEDDING NOW HAS TWO PATHS, SEPARATED BY A MEASURED CROSSOVER.**
>
> The paragraph above describes the MEASURED calibration: γ extracted from two full-wave line
> standards (D5), which captures dispersion and the port's own discontinuity with no quasi-TEM
> assumption. **That instrument is right at the top of the band and it is what the whole L8/L9
> acceptance set rests on. It is also the entire reason low frequency was unreachable.**
>
> The two standards' length difference Δℓ has to sit inside TRL's usable interval βΔℓ ∈ [20°, 160°],
> so it scales as **1/f_lo**. On a real board — a 3.8 mm microstrip on 20 mil laminate swept from
> 100 MHz — that meant a **161.5 mm** calibration standard at **79,055 unknowns** against a
> 12,000-unknown ceiling. The DUT is N = 1,144 and solves fine down there; **the artefact was 42× the
> size of the part**, and the run refused.
>
> **So below a crossover frequency, γ and Z_c are supplied QUASI-STATICALLY instead** (`PlanarQuasiStaticLine`):
> C and C₀ per metre by D7's own differencing of the two standards' electrostatics, `L = μ₀ε₀/C₀`,
> `γ = jω√(LC)`, and Z_c on D7's existing `γ/(jωC_pul)` spelling. **Only γ's SOURCE changes.**
>
> Four things about it are load-bearing and are easy to get wrong from the outside:
>
> - **A supplied γ does not by itself remove the long standards.** γ was already an input to the error
>   box, and the error box still needs TWO lines — one gives two complex equations for three complex
>   unknowns. What a known γ buys is that **Δℓ need not be electrically long**, because the usable
>   interval protects the γ EXTRACTION and nothing else. Δℓ is then sized from the substrate height and
>   the port's own bulk cell, is frequency-independent, and the sub-band ladder collapses to ONE
>   separation. On the reported file the longest standard falls from 79,055 unknowns to 3,289 while the
>   short one stays bit-identical.
> - **Kernel A is still the ORACLE and still not an input.** The supplier is the STANDARD's own
>   electrostatic solve, not `RlgcExtractor` — which keeps D7's own rule intact, reuses the solve
>   de-embedding already owes, and inherits the coplanar, floating-conductor and modal cases for free.
> - **The crossover is a property of the STACK and is not a user setting.** It is the LOWER of two
>   limits — `h·√(εᵣ−1)/λ₀ = 0.03`, fitted to measurement (≈ 3.0 GHz on 1.6 mm FR-4, ≈ 8.7 GHz on the
>   reported 0.6 mm board, ≈ 26 GHz on 0.1 mm GaAs), and an absolute electrical thickness
>   `h/λ₀ = 0.02`. **The second is not belt and braces**: the first measures DIELECTRIC dispersion, so
>   on a homogeneous substrate it is infinite — true of the MODE and false of the STRUCTURE, which can
>   still be a sixth of a wavelength thick and radiating. A knob for the crossover itself invites
>   someone to put it in the wrong place and there is no way for them to know, because both failure
>   modes publish a smooth, plausible, wrong phase; what IS settable is
>   `PlanarCalibrationSettings.QuasiStaticBelowCrossover`, on/off, which exists to reproduce the
>   pre-QSC answer for comparison exactly as `IncludePassiveNeighbours` and `IncludeDrivenGroups` do.
> - **BELOW THE CROSSOVER THE QUASI-STATIC VALUE IS THE MORE ACCURATE OF THE TWO, not merely the
>   cheaper**, and that was measured rather than argued. On the reported board at 100 MHz the two-line
>   ε_eff reads 2.02 on the default mesh and 2.45 on a refined one, against a static 2.786 it must
>   approach as f → 0 — the MEASURED value moves toward the quasi-static one as the mesh tightens. Its
>   Z_c lands 0.3 % from the closed-form microstrip value where the two-line route was 6.6 % out.
>
> **A run that crosses the crossover says which path each point took**, in its notes and in the `.npy`
> as `planar.CalQuasiStatic` (1 supplied, 0 measured, NaN uncalibrated). A run entirely above the
> crossover builds exactly the standards it always did, entry for entry; a run that STRADDLES it
> changes at its top as well, because the measured ladder is drawn from the crossover rather than from
> the user's lower edge — deliberately, since a separation exists to cover a sub-band and no measured
> separation is used below the crossover at all. Measurements, the crossover
> table, the Δℓ sweep and the a₂₂ sign margin are in `src/Engine/Mom/RESOLVED.md` §QSC.
>
> **What this does NOT fix:** the series delta gap's own `a₂₁ ∝ ω` amplification, which is the same on
> both paths and is what still limits the very bottom of a sweep — on the reported file, 0.32 dB of
> S₂₁ error at 100 MHz against 0.16 dB at 6 GHz, with the calibration's own residual at its SMALLEST
> there. That floor is a property of the port type, not of the calibration.

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
> mild inaccuracy, because D6's peel forms `(S_meas,ii − a₁₁)/a₂₁²` and `a₂₁ ∝ ω` — on the shipped
> 20 mil low-loss laminate at 1 GHz `a₂₁² = 9.8e-5`, so a **0.1% error in the error box is a 10× error in the answer**.
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

> **Built at P1 (`brief-em-p1-honest-memory-accounting.md`, 2026-08-29) — the column above is the
> MATRIX, and the matrix is under a third of a frequency point.** The resident peak of one dense
> point is the matrix PLUS both LU factors (`NumFlat` holds `L` and `U` as two separate full
> matrices, not a packed in-place factorisation) PLUS the cached frequency-independent cores. The
> ratio is **3.5×**, flat, because every term is O(N²) with a fixed coefficient — measured at
> N = 552, 1,980 and 4,836 and recorded in `src/Engine/Mom/HISTORY.md` §P1.
>
> | N | matrix (above) | + L and U | + cached cores | **resident peak** |
> |---|---|---|---|---|
> | 500 | 3.8 MB | 7.6 MB | 1.9 MB | **13.4 MB** |
> | 2,000 | 61.0 MB | 122.1 MB | 31.0 MB | **214.1 MB** |
> | 5,000 | 381.5 MB | 763.0 MB | 193.3 MB | **1.31 GB** |
> | 10,000 | 1.49 GB | 2.98 GB | 773.0 MB | **5.23 GB** |
>
> `PlanarSystem.ResidentBytes(n, cellCount)` is the one function that computes this, and all three
> ceiling refusals quote it. A machine additionally sees about **1.19×** the peak above while the
> factorisation runs — NumFlat allocates ~0.6 of a matrix of scratch that it releases again, and the
> large object heap does not hand it straight back. The transient m×m scalar-potential matrix `P`
> the fill builds is a quarter of a matrix and is released before the factorisation, so it is not
> the peak.
>
> **What this does NOT change: the ceiling itself.** `UnknownCeiling` stays at 5,000 and
> `AcceleratedUnknownCeiling` at 12,000 — moving either belongs to P7 and P8, once P2's and P7's
> reductions have landed and the memory is what it will be.

> **Built at P7 (`brief-em-p7-symmetric-inplace-factorisation.md`, 2026-08-29) — the "+ L and U"
> column above is gone, because there is no second matrix any more.** Z is complex-symmetric bit for
> bit (the fill computes the lower triangle and mirrors), so the dense solve is an unpivoted
> LDLᵀ written INTO Z's own lower triangle — `SymmetricFactorization`, blocked at 64 columns, its
> trailing update parallel over destination columns under the same one cap the fill spends. The only
> new array is the diagonal D, 16·N bytes.
>
> | N | matrix | + L and U (pre-P7) | + D (P7) | + cached cores | **peak pre-P7** | **peak P7** |
> |---|---|---|---|---|---|---|
> | 500 | 3.8 MB | 7.6 MB | 7.8 kB | 1.5 MB | 12.9 MB | **5.3 MB** |
> | 2,000 | 61.0 MB | 122.1 MB | 31.3 kB | 23.3 MB | 206.4 MB | **84.4 MB** |
> | 5,000 | 381.5 MB | 763.0 MB | 78.1 kB | 145.6 MB | 1,290.0 MB | **527.2 MB** |
> | 10,000 | 1.49 GB | 2.98 GB | 156.3 kB | 582.3 MB | 5.04 GB | **2.06 GB** |
>
> **2.45×, flat**, for the same reason P1's 3.39× was flat: every term is O(N²) with a fixed
> coefficient. `PlanarSystem.ResidentBytes` is still the one function all three refusals quote and it
> now quotes **527 MB** at the ceiling rather than 1,290. `PlanarFillSettings.UseSymmetricFactorization
> = false` puts NumFlat's general LU and the old number back; it is the oracle every P7 accuracy gate
> compares against, kept reachable exactly as `UseRadialTable = false` is.
>
> **The ceiling still does not move, and the reason has changed.** R17 re-asked: 1 GB of resident peak
> now buys **N = 6,968** against 4,454 with the general LU — 1.56× the unknowns. Memory is no longer
> what binds at 5,000. What has NOT been re-measured is the FILL's time and a mesh's accuracy at that
> size, so moving `UnknownCeiling` stays a separate owner decision; `HISTORY.md` §P7 carries the
> sentence that would change, written out and not applied.
>
> **Moved, and made a default (2026-10-29).** Fill and factor time were measured at N = 11,210 on real
> artwork (6.5 s and 37 s per point in Release, ten cores; 3.8 GB peak), and the owner moved
> `UnknownCeiling` to **10,000**, which holds about what 5,000 did before P7 (2.2 GB). It is now a
> DEFAULT: a setup may state a **memory limit** for one frequency point (`PlanarMeshSettings.MemoryLimitBytes`,
> `.cem` `MemoryLimitGb`, the EM panel's Solver options) and the mesh is then allowed whenever
> `PlanarSystem.ResidentBytes` fits it — the accelerated path never below 12,000. A memory rather than a
> count because a user cannot know their unknown count before meshing. Every run is then checked against
> the machine before it fills (`PlanarSolveSettings.Admit`, `PlanarMemoryCheck`): a warning past 75 % of
> physical memory (or of the Settings ▸ Solvers limit when set), the 3D solvers' own confirmation past
> 100 %. `src/Engine/Mom/RESOLVED.md` §Ceiling records it.

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

> **Built at P8 (`brief-em-p8-aim-near-radius-floor.md`, 2026-08-29) — the accelerated ceiling's own
> failure mode had a physical cause and it is fixed; the number 12,000 is unchanged and the case for
> moving it is now the owner's to take.** The ladder that broke it was refining the RESOLUTION at a
> fixed footprint, and the reason was that `NearRadiusFactor` measures the near field in BASIS
> SUPPORTS: refining the mesh shrinks the largest support, so the exact near field shrinks in metres
> — from 8.9 slab heights at the shipping cells/λ = 20 to 1.28 at cells/λ = 140 — while the coupling
> it has to span does not, because the grounded slab's image sits at a fixed depth 2h.
> `PlanarAimGeometry` now floors the radius at `2h` (`PlanarAimSettings.NearRadiusMinM`, null =
> derived), and the same 64 mm ladder that climbed 21 → 143 → 372 GMRES iterations and then failed to
> converge at cells/λ = 140 runs 21 → 28 → 36 instead. **The floor is inert on every mesh at the
> shipping resolution** — the LENGTH ladder's near set is identical pair for pair with it and without
> — so it costs the meshes users actually run nothing at all. What it does change is what BINDS at the
> ceiling on a refined mesh: N no longer predicts the working set there. The accelerator holds 426 MB
> at N = 10,708 with the floor against 303 MB without, and one rung further — N = 13,967, near matrix
> 8.9% dense — CSparse's exact sparse LU had not returned after 8 minutes of CPU where the unfloored one
> factors in 5 s. **The recommendation is to leave 12,000 alone, and the reason has changed**: it is no
> longer "margin under a ladder that fails to converge" but "margin under the near-set density at which
> the preconditioner stops being buildable", and moving it would need a check on near entries per row
> rather than a bigger integer. `HISTORY.md` §P8 carries both ladders and the sentence that would move
> the constant, written out and not applied.

> **Built at P11 (`brief-em-p11-accelerated-static-capacitance.md`, 2026-08-29) — de-embedding's own
> reference-impedance step is NO LONGER "a separate, always-dense cell system the accelerator does not
> reach", and that sentence two notes above is the one this corrects.** `PlanarDeembed.StaticCapacitance`
> solves `P q = ε₀·1` over a standard's CELLS, and `P` is exactly the scalar block the AIM accelerator
> already projects — the same cell-pulse potential with the static kernel. `PlanarStaticAim` therefore
> solves it with the same projection, near set, grid FFT and sparse-LU-preconditioned GMRES, so an
> accelerated run's calibration standards are judged against `AcceleratedUnknownCeiling` like
> everything else and **a wide port's standard no longer refuses a run whose DUT would have
> succeeded**. Gated at **1e-6 relative** against the dense solve on `C_pul` itself — the DIFFERENCED
> quantity, which is what the published s-parameters are referenced to — and measured at 1.04e-7
> (FR-4 hero's standards) and 2.02e-9 (GaAs). The 2026-08-14 owner report's own taper, reconstructed:
> its wide port's standards mesh at N = 1,498 / 7,603 / 4,273, the dense run is still refused at setup
> and the accelerated de-embedded run completes in **107 s for 3 points**, the N = 7,603 standard's
> static solve costing 9.7 s and 222 MB against 683 MB dense. The dense path is untouched and
> bit-identical; its refusal now names turning the accelerator on as the first remedy, which it could
> not before — and which, until P11 moved the check ahead of `PlanarSolveContext`'s own eager ceiling
> guard, no user ever saw, because that refusal was unreachable. `src/Engine/Mom/RESOLVED.md` §P11 and
> `HISTORY.md` §P11 carry the knob ladders, the tolerance measurement and the taper's tables.

> **Built at P12 (`brief-em-p12-aim-bordered-vias.md`, 2026-08-29) — "a multi-level or via-bearing
> problem is refused by name regardless" is no longer true, and the note four above is the one this
> corrects. The CEILING sentence in it still is.** A via-bearing mesh runs accelerated as a BORDERED
> system: R-via-5 already orders every horizontal rooftop before every ẑ basis, so
> `Z = [Z_hh Z_hz; Z_zh Z_zz]` with `N_z` in the tens, and only `Z_hh` is projected — one grid kernel
> table per (level, level) pairing over the SAME auxiliary grid, `L(L+1)/2` of them, with the scatter
> and the gather per level. The border is filled densely by `PlanarFill`'s own via arms and the
> preconditioner is the near LU with it folded in exactly, through an `N_z × N_z` Schur complement.
> **Nothing about a ẑ basis is projected** — which is what the original refusal was about, and why
> lifting it needed no new physics.
>
> Measured against the dense multi-level solve on the fixtures L9 was built on. The claim that
> matters is exactness rather than a tolerance: widen the near radius until every pair is in the near
> set and the bordered operator has to reproduce `PlanarFill.FillMultiLevel`'s matrix to round-off —
> it does, at **1.1e-15** entry-wise and **1.4e-10** in the solved current, on the MMIC two-level
> fixture with its via and on the FR-4 hero with a backside ground via alike. At the shipped radius
> what is left is the PROJECTION's error and it is M5's, untouched: **3.97e-7** on the ground-via
> fixture at the shipping mesh, inside `AimAccuracyTests`' own 8.7e-7, and 3.3e-6 on a deliberately
> un-edge-meshed cells/λ = 80 rung whose single-level control — the same mesh character with no via
> in it at all — reads 2.7e-5. De-embedded S through the whole sweep driver agrees to **1.6e-14**.
>
> **The ceiling did NOT move, and that is a deliberate hand-back.** The length ladder is healthy well
> past 12,000 — near entries per row 488 → 517 and GMRES 4 → 6 iterations from N = 1,728 to
> **N = 15,192**, converging to 3.6e-9, a whole point costing ~2.5 s and 327 MB against 4.9 GB for a
> dense one of the same size — so the case for applying `AcceleratedUnknownCeiling` to via meshes is
> made. Applying it is two lines (`PlanarSolveContext`'s `levels is null` and
> `SurfaceMesher.GuardCeiling`'s `accelerated` argument) and is the owner's decision;
> `src/Engine/Mom/RESOLVED.md` §P12 carries them written out and not applied, together with the
> caveat that decides the answer — **the border's cost is set by `N_z`, not by `N`**, and a fixture
> whose via footprint grows with the part (`N_z` = 140 at N = 15,352) spends 28.6 s of a 34 s point
> in the mixed block, against 0.5 s when the vias stay the size real vias are.

Sanity check on the hero: 50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line at 10 GHz has
λ_g ≈ 16.5 mm, so λ_g/20 ≈ 0.8 mm → ~24 cells long × ~6 across with edge refinement → **N of a few
hundred**. Genuinely fast. A spiral inductor lands at 1–3k. The scope is realistic.

Fill is O(N²) singular integrals; solve is O(N³) LU per frequency; both must be recomputed per frequency
because the Green's function is frequency-dependent. Since RF users sweep many points, **adaptive
frequency sampling** (solve sparsely, rational-interpolate, refine where the model disagrees) becomes
essential at L9 — it typically cuts solve count by 5–10× and is the best performance investment after
the mesh. Build it only when the kernel that needs it exists; v1 does not.

> **Two corrections at P9 (`brief-em-p9-adaptive-sweep-default.md`, 2026-08-29), both measured.**
> *"rational-interpolate"* is not what shipped: L9e's `T4_2` measured the Floater-Hormann rational
> form against a complex cubic spline on the one structure here with a real notch, the **spline won**
> (the rational scheme accepted no modelled point anywhere — strictly more cost, zero saving), and
> `PlanarAdaptiveSettings.Interpolant` defaults to `CubicSpline`. And *"typically cuts solve count by
> 5–10×"* is not what the shipped default sweep gets: on the EM panel's own 1–20 GHz / 101-point
> default the saving is **1.0× to 1.73×** across five fixtures, and on two of them it is nothing,
> because adjacent points of that grid already differ by 0.13–0.63 in |ΔS| — 130–630× the 1e-3
> tolerance, so there is nothing an interpolant may skip. The 5–10× is real but it needs a **finer**
> grid: the same hero at 401 points solves 81 of them in 3.01 s, against 4.25 s for a non-adaptive
> 101-point sweep of the same band. `HISTORY.md` §P9 carries the tables.

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
> **Built at P4 (`brief-em-p4-vector-block-moment-cache.md`, 2026-08-29).** The two ramps a cell can
> carry are linearly dependent through its pulse (`w_B = Δ·p − w_A`), so ONE outer pass per ordered
> cell pair yields seven primitives per kernel from which every (half, half) combination of both
> flow directions is a linear map — `PlanarFill.cs`'s P4 header has the derivation. Measured alone
> on the three series fixtures, quadrature passes fell from ≈ 4.0 m² to ≈ 0.6 m² (**6.6×**) for the
> core build and from ≈ 3.5 m² to ≈ 0.6 m² (**5.9×**) for the per-frequency vector remainder; wall
> clock followed at **3.6–4.0× on the core build and 2.7–3.0× on the per-frequency fill** (hero 2.81
> → 0.78 s and 1.52 → 0.51 s; N = 3,731: 26.6 → 6.8 s and 11.1 → 4.1 s), and AIM's near fill at
> N = 3,731 at 2.5× (23.7 → 9.3 s). Two things the paragraph above did not anticipate: the primitives
> are computed per ORDERED pair over a band `c ≥ a − n_x`, because the ŷ block integrates the same
> cell pair in both orientations and the two are not interchangeable to 1e-12; and they are not
> stored — holding them would be 2.3× the cached cores — but assembled straight into P2's layout
> through a transient second triangle, so resident memory is unchanged. The assembled matrix agrees
> with the retained four-call path to 8e-13 relative per entry (S0/SLog and every cut-cell pair
> bit-identical); the per-frequency fill at N = 3,731 is now 4.1 s against an LU of ≈ 18 s (scaled
> by N³ from the 42.8 s in the table above), so **the LU dominates a dense frequency point from
> roughly N = 2,000 rather than 3,000**. `HISTORY.md` §P4.
>
> **Built at P5 (`brief-em-p5-translation-class-memo.md`, 2026-08-29).** Every kernel is a function of
> separation alone and the grid is tensor-product, so two cell pairs that are translates of each
> other have the same seven primitives; the fill now keys every ordered band pair on GRID INDICES
> (spacing classes at 1e-12 relative — exact equality does not hold, the mesher's bulk lines are
> `a + len·i/n` — hash-consed spacing lists, the τ band, the 180° rotation folded) and integrates one
> synthetic representative per class. Reuse is 2.5× on the hero and 17–31× on the long line and the
> tapers, and the brief's own class table reproduced exactly. Measured alone against P4's retained
> arithmetic: core build **3.7–41×** faster (256 mm line 7.5 → 0.46 s; 60 mm taper 3.7 → 0.09 s),
> per-frequency fill **2.8–5.8×** (256 mm 4.66 → 1.25 s), AIM's near fill on the 256 mm line 6.8×
> (8.3 → 1.2 s); the cached cores 83.5 → 25 MB at N = 3,731 and 21 → 4.3 MB on the taper, with a stated
> break-even at reuse ≈ 3× below which the class table is slightly larger than P4's triangles. Two
> things the brief did not anticipate: the rule must be in the class key (equal cells offset (4, 4)
> sit exactly on τ = FarRatio), and "1e-12 relative per entry" is not a property the reference has —
> P4's own closed forms carry ~1e-13·|Z_ii| of absolute-coordinate roundoff that D4's second
> differences turn into O(1) relative on cancellation-residue entries — so the gate is
> |Δ| ≤ 1e-12·√(|Z_ii||Z_jj|) per entry (measured ≤ 4.7e-13) and the solved currents. The
> per-point crossover is now nearer **N = 1,000**: the fill is 1.25 s against an LU of ≈ 18 s at
> N = 3,731. `HISTORY.md` §P5.
>
> **Built at P6 (`brief-em-p6-aim-frequency-independent-state.md`, 2026-08-29).** The AIM accelerator
> rebuilt its stencils, its near set and — through a fresh per-entry fill — every near pair's singular
> cores at every frequency, where the dense path builds its cores once per mesh (D6). It is now two
> objects: `PlanarAimGeometry`, once per mesh (grid, stencils as `double`, near-set CSR, and the
> near cores warmed over the near set at build), and `PlanarAimOperator`, per frequency over it (grid
> kernels and their FFT hats, the near entries' remainders and assembly, the AIM correction, the
> sparse LU). Two things the brief did not anticipate. First, its premise was two phases stale — the
> "25 s per frequency at N = 3,731" is §12's pre-P4 number and the build was 1.75 s on the tree P6
> started from — so the per-point saving is **1.2–1.4×** (579–623 → 392–476 ms at N = 552;
> 1,751–1,791 → 1,380–1,435 ms at 3,731; 4.7–4.9 → 3.9–4.1 s at 11,959), with the geometry paid once
> (0.24 / 0.5 / 1.1 s). Second, the mirror index it listed for the geometry costs 4 B per near entry —
> 18 MB at N = 11,959, taking the accelerated working set to 214 MB — to save a 17 ms binary-search
> rebuild, so it is not held; the resident set is 195.8 MB there (196.2 before), the halved stencils
> covering the 2.5 MB class-keyed core store (the brief's per-cell-pair estimate would have been
> ~130 MB; P5's classes are why it is not). Measured directly, one dense point against one
> accelerated point is 0.24 / 0.54 / 2.03 s vs 0.37 / 0.71 / 0.96 s at N = 552 / 994 / 1,912: **the
> time crossover is N ≈ 1,100**, and the accelerated point is ~13× faster at N = 3,731. Splitting the
> near fill's timer showed the AIM correction is now the largest per-frequency term (43–46% at
> N ≥ 3,731), ahead of the sparse LU; a 5× restructuring of it is recorded for P7/P8. `HISTORY.md` §P6.
>
> **Built at P7 (`brief-em-p7-symmetric-inplace-factorisation.md`, 2026-08-29) — and this is where
> the paragraph below stops being true.** *"Cost is the fill, not the LU"* was already false per
> frequency (P4 and P5 cut the fill 8–17× and left the LU untouched at 42.8 s at N = 4,933); what
> P7 changes is the other side. **The dense factorisation is no longer an LU and no longer runs on
> one core.** Measured in the SHIPPED configuration, on the same three meshes P1's memory table used,
> the factorisation alone:
>
> | N | cap 1 | cap 2 | cap 4 | cap 10 | NumFlat LU | **LU ÷ cap 10** | cap 1 ÷ cap 10 |
> |---|---|---|---|---|---|---|---|
> | 552 | 0.04 s | 0.02 s | 0.02 s | **0.01 s** | 0.04 s | **2.7×** | 3.1× |
> | 1,980 | 0.91 s | 0.49 s | 0.41 s | **0.26 s** | 1.92 s | **7.3×** | 3.5× |
> | 4,836 | 13.14 s | 6.88 s | 5.83 s | **3.59 s** | 41.21 s | **11.5×** | 3.7× |
>
> Half of that is arithmetic — LDLᵀ is N³/3 complex multiply-adds against an LU's 2N³/3, and the
> blocked form gets 3.1× of it single-threaded — and the rest is the ten cores, which the LU never
> used. The scaling tops out at 3.7× for P3's own reason: this box's ten cores are four performance
> and six efficiency ones. **So a dense frequency point at the ceiling is now roughly a 2 s fill and
> a 3.6 s factorisation rather than a 2 s fill and a 41 s one**, and adaptive frequency sampling —
> which the paragraph below calls "the best performance investment after the mesh" — is now worth
> proportionally less on the dense path than it was this morning, not more.
>
> **A measurement trap this cost real time, recorded so the next phase does not pay it again.**
> `dotnet test` builds **Debug**, and every other timing in this area compares managed code against
> managed code, so the configuration has always cancelled. This comparison does not: NumFlat's LU is
> native and barely moves, while `SymmetricFactorization` is ordinary C# and runs ~10× slower
> unoptimised. The same N = 4,836 rung reads **139 s / 33.7 s (cap 1 / cap 10) against an LU of
> 40 s in Debug** — i.e. "the new factorisation is 3.5× SLOWER than the one it replaces" — and
> 13.1 s / 3.6 s against 41.2 s in Release. The table above is Release; `PlanarP7FactorCostTests`
> prints its own build configuration beside its table and asserts only what is true in both.
>
> **Reviewed 2026-08-28 — two statements in this section are wrong today, and a brief series exists
> to act on them (`docs/sonnet-briefs/brief-em-perf-series.md`, P1–P12).** First, *"cost is the fill,
> not the LU"* holds for a whole sweep only because the once-per-sweep core build is folded into
> "fill"; **per frequency the LU dominates from about N = 3,000** (42.8 s against 21.8 s at N = 4,933
> in the table above), and NumFlat's LU runs on one core while the fill runs on all of them (P7).
> *(**P7 is done, 2026-08-29** — see its own note above. The sentence is now wrong in the other
> direction and in a way that will not come back: the dense factorisation is 3.59 s at N = 4,836 on
> ten cores, against a fill of the same order, so neither dominates. Nothing here is "the fill, not
> the LU" any more; the per-point cost is simply the two of them, both parallel, both O(N²)–O(N³)
> with small coefficients.)*
> Second, the memory column and the refusal's "381 MB" quote `16·N²` alone; **the resident peak of one
> frequency point is roughly four times that** — NumFlat holds `L` and `U` as two further full
> matrices beside the one `PlanarSystem` keeps, plus the cached cores and the transient m×m `P`
> (measured: a 137 MB matrix at N = 3,000, and `.Lu()` added 530 MB). P1 measures and re-words; P2
> and P7 recover it. Each brief in that series updates the paragraph it changes here with its own
> `> Built at Px` note, in this same style; nothing else in §10.7 has been rewritten in advance.
>
> **P1 is done (2026-08-29) — see the `> Built at P1` note under the N table above for the numbers.**
> Two refinements to the sentence immediately above: the honest multiplier for what is RETAINED is
> **3.52×**, not four (four is what a machine sees, once the factorisation's released scratch on the
> large object heap is counted); and the transient `P` is not part of the peak at all, because the
> fill drops it before the factorisation starts.
>
> **P2 is done (2026-08-29).** Four mechanical allocations came out, none of them changing an entry:
> the largest was one of the three O(N²) same-direction vector core triangles, which held nothing but
> the outer product of an O(N) per-basis vector and is now formed at the point of use. That is **24%
> off the cached cores at every N** (2.43 → 1.85 MB at N = 552; 184 → 140 MB at N = 4,836), so the
> multiplier above is now **3.39×** and the ceiling refusal quotes **1,290 MB**. The other three are
> off the de-embedding path rather than off a frequency point — see `HISTORY.md` §P2. The 51%-on-top-of-the-matrix core figure three
> paragraphs above is confirmed exactly — `PlanarSystem.CoreBytes` reproduces
> `PlanarFillCores.CoreBytes` to within 1% on real meshes.
>
> **P3 is done (2026-08-29).** Its premise — that the multi-level fill's per-entry locks and
> allocations would make it scale far worse than the single-level one — was measured false before
> anything changed (5.5–5.9× at ten cores, the same as `Fill`). The hoist was done anyway and is
> bit-identical; what it bought is **16–24% off every via-bearing fill**, from the ẑx̂ mixed block's
> per-node iterator, not from any O(N²) arm. The strided-write hypothesis for the fill's parallel
> fall-off is retired: the loss is the box's efficiency cores. `HISTORY.md` §P3.

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
> DeembedErrorFloor, CalibrationUsable). **They deliberately do not share a name.** A per-unit-length quantity from a 2-D
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
low-loss-laminate line.

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

> **Kernel B's metal was a PERFECT CONDUCTOR until CL3, and the paragraph this replaces said so.**
> `PlanarConductorLayer.SigmaSm` and `ThicknessM` were carried through the whole pipeline and never
> read by the fill; only kernel A's Wheeler term (§10.3) used conductivity. The omission was recorded
> here as **6.5% / 3.0% / 2.1%** of the total conducted loss at 2 / 10 / 20 GHz on 1.6 mm FR-4. **That
> figure was correct and was measured on the one substrate class where conductor loss matters LEAST**
> — on the shipped MMIC technology the same quantity is **92-99%**, and the generalisation built on it
> ("dielectric loss dominates on ordinary substrates") was false there by a factor of about thirty.
>
> **Built at CL1 (2026-09-14) and turned ON by default at CL3 (2026-09-14).**
> `PlanarFillSettings.ConductorLoss` carries σ and t into the fill as
> `Z[m,n] += Z_s(ω, layer)·⟨f_m, f_n⟩` with `Z_s = (η_c/2)·coth(γ_c t/2)`; the term is a true EFIE
> term, not a post-processing `∫R_s|J|²` over a PEC current, which would be log-divergent in the edge
> mesh. `PlanarFillSettings.PerfectConductor` is what remains reachable, permanently, as the PEC
> oracle — on the pattern of `UseSymmetricFactorization = false` and `UseRadialTable = false`, and for
> the same reason: every accuracy figure below is a comparison AGAINST it.
>
> **This section's own "A and B agree on a uniform line" gate has changed what it gates, and the
> change is the point.** It used to compare two models that diverged on loss BY CONSTRUCTION — one
> with lossy metal, one without — so it could only ever be a gate on ε_eff and β. Both models carry
> conductor loss now, so α is comparable for the first time. Measured through the WHOLE SHIPPED PATH
> (the published, de-embedded S₂₁ of a uniform 50 Ω line of known length, not the calibration's own
> γ), ground held PEC in BOTH kernels, at 2 / 10 / 20 GHz:
>
> | | α_c, kernel B ÷ kernel A | TOTAL α, kernel B ÷ kernel A | (the same, with PEC metal) |
> |---|---|---|---|
> | FR-4 starter, 1.6 mm, 35 µm Cu | 0.731 / 0.637 / 0.632 | 0.986 at 2 GHz | 0.949 at 2 GHz |
> | **MMIC starter, 100 µm GaAs, 3 µm Au** | 0.836 / 0.748 / 0.827 | **0.852 / 0.787 / 0.863** | **0.061 / 0.134 / 0.181** |
>
> **The α_c column is the honest limit and it is a MODEL limit, not a mesh one.** A zero-thickness
> sheet holds ONE current per location, so it cannot carry the sidewall current or the two-face
> crowding a 35 µm / 3 µm strip really has; CL1 measured that deficit as 0.63 (FR-4) and 0.73 (GaAs)
> by extracting γ from two bare standards, and the table above reproduces 0.637 and 0.748 at the same
> frequency through the entire de-embedding instead. **The two instruments share no algebra**, which
> is what makes the 16-40% under-read structural rather than a defect somewhere in the chain. It also
> converges: refining `EdgeCells` moves it under 1% per rung by `EdgeCells = 8`.
>
> **FR-4's total-α column stops at 2 GHz and the reason is pre-existing.** At 10 and 20 GHz a 1.6 mm
> FR-4 line is h/λ₀ = 0.05 and 0.11, it radiates and couples its ports through a surface wave, and
> the two-line calibration's own error scales as f² (§5). At 20 GHz the peel over-corrects far enough
> that |S₂₁| > 1 and α comes out NEGATIVE — identically so on the PEC oracle, at three line lengths
> and two mesh densities. The DIFFERENCE is still good there, which is §CL1's own sentence ("the
> absolute α of a PEC FR-4 line out of this route is not a usable number; the difference is").
>
> **What is still PEC is the GROUND PLANE**, which is CL4 and has not run. Kernel A's sum over
> surfaces isolates it exactly: **21.1% (FR-4), ~11% (GaAs MMIC), 25.0% (low-loss laminate)** of the
> conductor term. Every number in the table above holds the ground PEC in kernel A too, so the two
> sides are comparing the same quantity. Detail in `src/Engine/Mom/RESOLVED.md` §CL1, §CL2 and §CL3.

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

### 10.12 Thin-film (MIM) capacitors — plan

**Status: MIM-1 (region vias), MIM-2 (the shipped exemplar), MIM-6 (the level
reference surface), MIM-3 (the thin-layer ladders and their verdict), MIM-4 (interior-height
electrostatics) and MIM-5 (the import coverage note) are all built, 2026-08-30.** MIM-6 is not in the
original four-gap plan below — it is the fifth gap MIM-2's own measurement surfaced, and it is
folded into gap 2's finding (a) rather than given a numbered gap of its own, because it is that
finding's fix and nothing else.
The work is staged as the `docs/sonnet-briefs/brief-em-mim-series.md` series (MIM-1 … MIM-5); the authoritative record of
what lands is, as always, `src/Engine/Mom/CLAUDE.md` and the per-area `RESOLVED.md` files, and each
brief corrects this section in place with a `> Built at MIM-x` note as it ships.

**The structure.** A MIM capacitor is a thin dielectric (50–300 nm, silicon-nitride-class εr) between
two metal plates inside the interlayer dielectric: the SHUNT form returns its bottom plate through a
backside via to the wafer's back metal; the SERIES form feeds in on one metal level and out on
another through a plate-connection via. Both are ordinary multi-level planar-EM structures — exactly
the class of problem L9/P12's multi-level + via machinery was built for — and an investigation on
2026-08-30 found that what still blocks them is four specific, independent gaps, **none of which is
the stackup model**: `StackupLayer` already expresses the thin dielectric, the plate conductor and
the via entry, and the technology editor already binds a drawing layer and a span to a Via entry.

**The capacitor dielectric is a stackup layer, not artwork — by design.** The 2.5D premise (§10.1)
is a laterally infinite stratified medium, so the thin dielectric enters as a full layer of the
stack, present everywhere at its true thickness. This is the standard approximation in the
planar-EM class of tool, and it is a good one here: the fields that set the capacitance are
confined under the plates, where the layer genuinely exists; elsewhere a 50–300 nm sheet perturbs
an interconnect-scale stack negligibly. Drawn artwork on a dielectric-bound drawing layer stays
ignored (with a note), exactly as §10.4's editor behaviour already reflects. What the user DRAWS
are the two plates (conductor layers) and the plate-connection via — a REGION, which was gap 1
and is built (MIM-1, 2026-08-30).

**The four gaps, each one brief:**

1. **Region vias (MIM-1). > Built 2026-08-30.** `PlanarExtractor` consumed only the point
   `ViaShape`; a rectangle or polygon drawn on a via-bound layer was dropped silently, while the
   engine's `PlanarVia` already took an arbitrary polygon footprint and meshed one vertical basis
   per covered cell. It now becomes a footprint at the outline it was drawn (NOT squared — the
   equal-area substitution belongs to a point via and exists so a circle nobody drew does not
   staircase), every region on one stackup entry groups into one `PlanarVia`, and nothing on a
   via-bound layer falls into `ignoredOther` any more. The engine and every §7 via refusal were
   untouched. Write-up: `src/Design/RESOLVED.md`.
2. **A shipped exemplar (MIM-2). > Built 2026-08-30, as a SECOND technology rather than as three
   entries on the starter.** `mmic-GaAs_2LM_100um_MIM` is the GaAs starter plus a plate conductor
   (`MIM Metal`, 0.25 µm), a thin capacitor dielectric (`MIM Dielectric`, 0.2 µm, εr 6.8, never
   drawn — the starter's `Cap Dielectric`/`Nitride` drawing layers stay unbound) and a plate-via
   entry (`MIM Via`), with the air gap absorbing the 0.45 µm so Metal2 still sits 3 µm above Metal1.
   Both capacitor forms and the two-caps-on-a-line acceptance shape extract, and nothing in the
   extractor turned out to be per-capacitor.

   **The brief asked for these three entries to be ADDED to the starter, and measurement refuted its
   premise that "a 50-300 nm sheet perturbs an interconnect-scale stack negligibly".** A capacitor
   dielectric between Metal1 and Metal2 makes every Metal1-Metal2 airbridge post cross a dielectric
   interface, which `PlanarKernel.CanSolve` refuses BY NAME — a whole-run refusal, not a dropped
   shape — and moves a Metal1 microstrip's ε_eff by ~1.7% and Z₀ by ~2.8% (49.62 → 48.25 Ω on the
   acceptance line). Neither may happen silently to the technology every existing MMIC workspace
   already copied, so the plain starter is byte-identical and the MIM stack is its own file.

   > **Built at MIM-7 (2026-08-30): there is ONE MMIC technology again, and the second file is
   > retired.** Both costs above came from the film being present in runs that contain no capacitor,
   > which the 2.5D premise does not actually require: it forces "laterally infinite per RUN", not
   > "present in every run". `StackupLayer.PresentWithLayer` names the conductor a dielectric is
   > PATTERNED with, and when that conductor is not among a run's analysis levels the extractors
   > carry the band as air at its stated thickness AND treat `SheetAt = Top` on the conductor
   > directly beneath it as unset for that run — the pair is what makes an interconnect-only run
   > BIT-identical to the same run on a module-free stack rather than merely close, gated on the
   > airbridge artwork above. `mmic-GaAs_2LM_100um` now carries the module (the byte diff is pure
   > additions plus the Air thickness MIM-2's own arithmetic requires) and
   > `mmic-GaAs_2LM_100um_MIM` is gone. Both extractors read the tie — the cross-section kernel
   > builds its own medium from the same stackup, so a film left on there is exactly the ~2.8% Z₀
   > shift. The KERNEL is untouched: it still refuses a via across a dielectric interface, and a
   > hand-authored stack whose dielectric genuinely is everywhere still meets that refusal. The one
   > residual cost is closed-form-only and measured: a Metal2 line's `SubstrateResolver` substrate is
   > 102.75 µm instead of 103, because the module put 0.25 µm of plate METAL where air was and only
   > dielectric bands are summed. Write-ups: `src/Design/RESOLVED.md`, `src/Ui/RESOLVED.md` §MIM-7.

   > **Amended at MIM-11 (2026-09-16): `PresentWithLayer` resolves in TWO namespaces, and the
   > shipped MMIC technology's tie names the nitride MASK.** A CONDUCTOR stackup entry first,
   > exactly as above and with priority, so every technology authored against MIM-7 extracts bit for
   > bit as it did; otherwise a DRAWING LAYER, in which case "in this run" means "this run's layout
   > carries artwork on that layer". The plate was always a proxy — on this process the insulator is
   > streamed out as its own mask and the plate is deposited on what that mask left — and the proxy
   > could not express a mask-only structure at all. `KIT_MIMCAP` now draws the nitride, which is
   > also what makes a `.gds` off this kit manufacturable and gives a DRC deck something to check.
   > One consequence is deliberate: a layout that draws the mask carries the film whether or not the
   > plate LEVEL is in the run, because excluding a level does not etch a wafer. The capacitor's
   > absence from such a run is still reported, by the extractor's own dropped-artwork warnings.
   >
   > **And a carried film now SAYS it is laterally infinite, with a number.** The medium string is a
   > correct list of bands and nowhere said that the 0.2 µm εᵣ 6.8 one is present over the whole
   > plane; the run's notes now say it, with the fraction of the layout's extent the defining artwork
   > covers, plus the paired sheet move (Metal1 at 103 µm rather than 100 — MIM-6 working as
   > designed, the same order of effect, and invisible in every other report). Measured size of the
   > approximation on a Metal1 line: max |ΔS| 2.26e-3, S₁₁ phase +0.108°, a gap capacitance −2.68 %.
   > **Making the film laterally FINITE is not deferred — it cannot be done in this kernel**, which
   > is built on laterally infinite strata; the routes that exist elsewhere are a different solver.
   > Write-up: `src/Design/RESOLVED.md` §MIM-11.

   **Two findings on the physics, and the second is the important one.** (a) A level is a sheet at
   the BOTTOM of its conductor band, so the modelled plate separation is the dielectric PLUS the
   lower plate's own metal thickness — 3.2 µm, not 0.2 µm. **> Built at MIM-6 (2026-08-30): a
   conductor entry now names which SURFACE of its band its sheet sits on (`StackupLayer.SheetAt`),
   with the absorption direction PAIRED to that choice so every sheet still lands on an interface of
   the medium by construction. `Bottom`/unset is the old behaviour bit-identical; the shipped MIM
   technology sets `Metal1 = Top`, and the three levels extract at 103 / 103.2 / 106 µm with the
   between-plates region at its stated 0.2 µm of εᵣ 6.8. Stated cost, taken deliberately: the region
   under Metal1 is 103 µm of GaAs rather than 100 (~3%), and `SubstrateResolver` — the closed-form
   path — is NOT taught the field, so on that technology it and the extractor differ by one metal
   thickness (100 vs 103 µm; teaching it would move a 70 µm line's static Z₀ 49.42 → 50.06 Ω, and its
   h is the physical substrate while the extractor's is a sheet POSITION). Write-up:
   `src/Design/RESOLVED.md`, `src/Ui/RESOLVED.md` §MIM-6.** (b) An initial claim that **the plate
   capacitance is not in the answer at all** — 0.30 fF on a one-port shunt cap, constant across a
   32× change in the modelled gap — was RETRACTED at MIM-2 as an artefact of reading RAW,
   un-de-embedded S. **> MIM-3 (2026-08-30) confirms the retraction and measures why the rule is
   stronger than it looked:** a matched ~50 Ω GaAs microstrip reads |S₂₁| = 0.0706 raw at 10 GHz,
   because the port is a series delta gap with a₂₁ ∝ ω (§5's low-frequency floor). A raw reading of
   ANY reactive element in this kernel is the port, at any topology — MIM-3 reproduced the ~0.3 fF
   answer twice with two different raw instruments, and de-embedding the same structure moved it to
   44 fF against a 30 fF closed form. Write-up: `src/Ui/RESOLVED.md`,
   `src/Engine/Mom/RESOLVED.md` §MIM-3.
3. **Thin-layer numerics (MIM-3). > Built 2026-08-30, as a measurement plus a note.** The
   kernel / fill / physics ladders were run against direct Sommerfeld integration, forced-high
   quadrature and the parallel-plate closed form, with 2 µm and 3 µm control rungs at the
   interconnect spacing L9c had already measured. **The kernel is flat in the separation** — worst
   4.2e-3 of the free-space kernel at 0.05 µm against 6.4e-3 at 3 µm, inside L9b's own envelope, so
   no ρ/λ constant moved and none was moved. **The CROSS-LEVEL quadrature is what degrades**, by four
   decades between cell size / level separation of 1 and 20 (2.3e-7, 4.1e-3 at 5, 3.9e-2 at 10,
   1.5e-1 at 20, 4.9e-1 at 50) while the same-level block stays at 3e-6 — §3.5's recorded "different levels ⇒ smooth ⇒
   plain quadrature" trap, with the peak's width now set by the stackup rather than by the mesh. The
   extracted plate capacitance follows it rung for rung: within 10% of ε₀εᵣA/d while
   cell/separation ≤ 5, 1.46× at 12.5, wrong SIGN at 25. Reciprocity (1e-19) and passivity (1e-5)
   hold throughout, so nothing downstream shows it. The verdict is therefore a validated MESH
   condition rather than a stackup one — `PlanarLevels.ValidatedCellOverSeparation = 5.0`, reported
   by `PlanarSolve.LevelSeparationNotes` as a note and never a refusal. **The shipped MIM technology
   sits outside it** (a 10 µm plate pair 0.2 µm apart meshes at 2.5 µm, i.e. 12.5) and the note
   fires on it. Kernel A on the same cross-section reproduces the closed form to 1.007–1.16 and is
   mesh-converged to five digits, so the closed form is not in doubt. Write-ups:
   `src/Engine/Mom/RESOLVED.md` §MIM-3, tables in `src/Engine/Mom/HISTORY.md`.
4. **Interior-height electrostatics (MIM-4 — §7's "all of Part B"). > Built 2026-08-30.**
   De-embedding's Z_c = γ/(jωC_pul) rested on an electrostatic image series over ONE grounded slab,
   which is why a de-embedded edge port off the slab top threw and a stratified region under the
   lowest level refused at extraction — and a MIM's feed arrives on upper metal. **Both refusals are
   retired.** `InteriorStaticGreens` solves the ω = 0 problem as a Sturm-Liouville problem in z —
   `d/dz(P dG/dz) − k²P G = −2kδ(z−z′)`, with `P = ε*` for the scalar problem and `1/µ` for the
   vector one — so source and observer may sit anywhere in the stack, and `InteriorStaticImages`
   inverts it by fitting the spectral remainder as complex exponentials in k_ρ, which IS an image
   expansion because `∫e^(−bk)J₀(kρ)dk = 1/√(ρ²+b²)`. Two Prony levels, because a thin-film stack
   carries image depths three orders of magnitude apart; the sum rule `c_∞ + Σa = 0` imposed exactly,
   because over a ground plane the 1/ρ tail cancels and the far field IS that cancellation.

   **Measured.** The fit reproduces the shipped one-slab series to 1e-10 out to ρ = h and agrees with
   direct Hankel integration to 1e-8 at interior heights on a 0.2 µm-over-100 µm MIM stack; it costs
   0.34 µs per ρ, against the shipped series' own 2.5 µs and the reference integrator's 1–270 ms —
   which is the brief's fit-versus-quadrature decision, and quadrature is the loser and stays as the
   oracle. C_pul through the new route reproduces the shipped one to 3.5e-12 where both apply, and a
   uniform line on a buried level de-embeds to a matched section (|S₁₁| = 9e-4, ∠S₂₁ within 1.2e-3 rad
   of −βℓ, cascade identity 1.8e-4). The shipped on-slab-top path is untouched and is selected by
   comparing the MEDIUM structurally rather than the level's height — a single level over a stratified
   region sits at the slab's height too. `PlanarExtractor` now carries the sub-feed layers and reports
   a series-capacitance ε_eff used only to size the mesh, the standards and the phase seed. Write-up:
   `src/Engine/Mom/RESOLVED.md` §MIM-4, tables in `src/Engine/Mom/HISTORY.md`.

> **Learned at MIM-2 (2026-08-30).** MIM-1 and MIM-2 are built; three amendments, full record in
> `src/Ui/RESOLVED.md` §MIM-2. (1) The MIM stack ships as a SECOND technology beside the plain GaAs
> starter, because the capacitor dielectric is not the free addition the plan above assumed: an
> airbridge post between the interconnect metals then crosses a dielectric interface and is refused
> by `PlanarKernel.CanSolve` — a whole-run refusal — and the upper-metal line's substrate resolution
> shifts. *(PSV-3, 2026-11: that refusal is gone. A via's z-integral is split at every interface it
> crosses, and the direct term transmitted across an interface is taken in closed form like the
> same-region one; `src/Engine/Mom/RESOLVED.md` §PSV-3.)* (2) A FIFTH gap surfaced: the extractor places a level's sheet at the BOTTOM of its
> conductor band and absorbs the band into the dielectric above, so a plate pair solves at the
> capacitor dielectric PLUS the lower plate's metal thickness — 3.2 µm where the process states
> 0.2 µm. That was **brief MIM-6** (a per-conductor reference-surface field), sequenced before
> MIM-3's physics tier so the ladder measures the true regime; **it is BUILT (2026-08-30) and the
> shipped MIM capacitor now solves at 0.2 µm** — so MIM-3's physics tier may run, and must not be
> run against a pre-MIM-6 build. **It was run on a post-MIM-6 build and its verdict is gap 3
> above.** (3) An initial claim that the plate
> capacitance was absent from the kernel was RETRACTED as a measurement artifact — it read raw,
> un-de-embedded S, which is the port and not the structure; the L9 gate's own bridged-vs-open
> record (|S₂₁| 0.9993 vs 0.0502) stands, and **MIM-3 confirmed the retraction against a known-good
> line: a matched 50 Ω microstrip reads |S₂₁| = 0.0706 raw.** Corollary for every
> measurement in this section's briefs: never read a small element's value off raw S — compare
> with/without on the same artwork, or measure de-embedded.

A fifth brief (MIM-5) closes the import loop. **> Built 2026-08-30.** A process stack description
that omits an optional capacitor module imported faithfully and silently, so
`ProcessTechnologyBuilder` now names the non-device conductors its via list reaches at neither end
and the drawing-purpose layer-table rows no stackup entry binds — two ordinary notes beside the
existing dangling/undrawn ones, both pointing at the user docs' new three-row hand-add recipe
(`reference/stackup.html#mim-import`, with εr worked from the capacitance density the process
publishes). **The brief's directional exemption — topmost exempt from above, bottom from below —
is NOT what shipped**: a directional test names a ladder when one via entry is missing (the level
above the gap has nothing below, the one below has nothing above), which buries the conductor that
is actually unreachable, so the test is "named by no via at all" and a stack with fewer than two
non-device conductors is silent. The import itself is unchanged — only the report grew. Write-up:
`src/Ui/RESOLVED.md` §MIM-5.

**What a MIM run looks like at each stage.** After MIM-1 + MIM-2 + MIM-6: a runnable RAW solve
(internal ports, or de-embedding off) that is STRUCTURALLY right and whose capacitance must not be
read off it — **not because a term is missing, but because a raw reading in this kernel is the
series delta-gap port (MIM-3, gap 3 above); the plate term is present and is right to 10% once
de-embedded, provided the mesh resolves the gap to `ValidatedCellOverSeparation`.** **After MIM-4
(built): ordinary de-embedded S-parameters with the feed on any level, and a technology may carry
several dielectrics under the lowest one.** **The acceptance topology is a
NETWORK — several capacitors joined by transmission lines in one run — because that is what an MMIC
matching section is.** Nothing in the plan is per-capacitor: the extractor takes every shape on the
selected levels and every via region, so multi-cap structures use the same machinery (MIM-2 pins
this with a two-caps-plus-line fixture). The two real bounds on such a run are now the MESH condition
of gap 3 (`PlanarLevels.ValidatedCellOverSeparation`, reported as a note, and the shipped MIM
technology's default mesh sits outside it) and the unknown budget: on the shared tensor grid each plate's fine gridlines span the whole domain, so
caps far apart with a long line between them grow N quickly; the accelerated (AIM) path and the
§10.7 ceiling refusals are the existing answer, not anything new here. The user-facing EM reference
page's "Cannot" list tracks the same boundary at every stage — §10.9's rule that the two documents
must not contradict applies here verbatim.

### 10.13 The low-frequency end of the band, and why "do not calibrate" does not reach it

**Status: investigated 2026-09-13. > Built at LF2 the same day** — the substitution in "What this leaves" below is what shipped (`PlanarSolveSettings.SubstituteConductionBelowFitFloor`, on by default); the write-up is `src/Engine/Mom/RESOLVED.md` §LF2. Everything measured here stands unchanged.

LF1 (`src/Engine/Mom/RESOLVED.md` §LF1) left
`Dcim.CanFitAtFrequency` refusing below k₀H = 1e-4 and named two ways out: raise the sweep's lower
edge, or ask for 0 Hz, which `PlanarDcSolve` answers exactly. A third was proposed — publish an
UNCALIBRATED full-wave point down there, on the reasoning that a port discontinuity is a reactance
and therefore has nothing left to remove at low frequency, so the calibration is the part that should
be dropped rather than the frequency. It is a good argument and it is measurably wrong, for a reason
that has nothing to do with low frequency. This section records the measurements, because the
negative result is worth more than the refusal it was testing.

**What the report was about.** A four-port imported PCB, 0.9 mm of εr 4.4 between the signal level
and the ground plane, swept 100 MHz – 1 GHz with de-embedding on and the mesh pinned at 2 GHz. On
that stack the fit floor is **5.30 MHz**, not the 3.4 MHz LF1 quotes — that figure is LF1's own
1.4 mm worked example, and the floor is `1e-4·c/(2πH)`.

**Method.** A scratch harness drives `SurfaceMesher` → `PlanarSolveContext` →
`PlanarFrequencyKernel` directly, which is below `PlanarSolve.Run` and therefore below the guard, so
an arbitrarily low frequency can be asked for; `PlanarKernel.Solve` supplies the calibrated
comparison on the same geometry. Two fixtures: §10.7's 20 mm × 2.9 mm FR-4 hero on 1.6 mm, and a
20 mm × 1.7 mm line on 0.9 mm standing in for the reported board's cross-section. The mesh is built
once per sweep and held fixed, so nothing in a column can be a change of N.

#### (a) The raw answer is the PORT, at every frequency — and refining the mesh makes it worse

Raw against de-embedded |S₂₁| on the hero line, straight off `PlanarFrequencyPoint`:

| f | raw \|S₂₁\| (N = 24) | raw \|S₂₁\| (N = 94) | de-embedded \|S₂₁\| (N = 94) |
|---|---|---|---|
| 10 MHz | −59.64 dB | — | — |
| 50 MHz | — | −63.38 dB | −0.050 dB |
| 100 MHz | −39.64 dB | −57.36 dB | −0.021 dB |
| 200 MHz | — | −51.32 dB | −0.016 dB |
| 500 MHz | — | −43.18 dB | −0.030 dB |
| 1 GHz | −19.12 dB | −36.52 dB | −0.061 dB |

**The raw column rises at exactly 6 dB per octave** (50 → 100 → 200 MHz: +6.02, +6.04 dB), which is
a SERIES CAPACITANCE and nothing else. A 20 mm piece of copper reads as −60 dB of insertion loss at
10 MHz. This is not a degraded version of the right answer; it is a measurement of the excitation.
§10.6's delta gap drives the structure against the plane through the field, and `RESOLVED.md` §LF1 §4
already states the consequence in the DC context — a source in series with its own rooftop means
"every line would read as an open at both ends". The raw solve is that open, at every frequency, and
the calibration is what removes it.

**And the capacitance is a mesh artifact, so it cannot be corrected for.** The gap is one cell wide,
so a finer mesh is a smaller gap: at 100 MHz the 94-unknown mesh is **17.7 dB further from the
truth** than the 24-unknown one (0.022 pF against 0.17 pF of implied series C). Refining the mesh —
the one lever that improves every other number in this kernel — moves the uncalibrated answer away
from the structure's own response. This is the same quantity §LF1 §5(a) records as `1/|a₂₁|²`
growing 6.9 → 24.7 → 2,413 from 2 GHz to 100 MHz; it is a₂₁ ∝ ω seen from the other side.

#### (b) The fit does go jagged below the floor, at a few percent — measured, and then moot

The one quantity the gap capacitance does NOT corrupt is the line-to-ground capacitance, because the
gap is not in series with it: it is `Z₁₂` of the port pair's own T, and it is a clean probe of the
kernel. Widened DCIM throughout, mesh fixed:

| k₀H | f (1.6 mm) | C (pF), N = 24 | C (pF), N = 94 | scalar fit residual | G_A images |
|---|---|---|---|---|---|
| 1.0e-4 … 3.4e-4 | 3–10 MHz | 1.5142 | 4.0863 | 1.9e-8 … 1.9e-7 | 12–15 |
| 5.0e-5 | 1.5 MHz | 1.5142 | 4.0863 | 1.5e-5 | 10 |
| 3.4e-5 | 1.0 MHz | 1.5155 | 4.0824 | 4.3e-3 | 10 |
| 2.4e-5 | 700 kHz | 1.4717 | 4.0171 | 4.0e-2 | **1** |
| 1.0e-5 | 300 kHz | 1.5463 | 4.1733 | 9.4e-2 | **1** |
| 3.4e-6 | 100 kHz | 1.6341 | 4.3515 | 1.3e-1 | **1** |

Flat to five digits above k₀H ≈ 5e-5 and **non-monotonic below it**, +8.0 % / −2.8 % on the coarse
mesh and +6.5 % / −1.7 % on the fine one — the same shape on both, so it is the kernel and not the
mesh. The break sits within a factor of 1.2 of `Dcim`'s own documented 6e-5, measured there by a
completely different route (spectral |ΔG_q| against direct Sommerfeld integration; here, a converged
physical capacitance). **So the answer to "does it merely bias, or does it go jagged" is: it goes
jagged, by a few percent, and it starts where the residual table says it starts.**

**A detail worth keeping: the vector kernel's fit residual is not an alarm.** `FitResidual` on G_A
stays between 7e-13 and 6e-10 the whole way down — three to six decades better than G_q's — while its
image count collapses from 12–15 to **one**. A single-image fit reproduces its own samples perfectly
and carries no stack information at all, so a guard keyed on the residual alone would never fire on
the vector potential. The image count is the observable that moves.

#### (c) Below ~20 MHz the binding constraint is the calibration standard, not the kernel

`TargetElectricalDegrees` = 60 makes a standard's length go as 1/f, and its N with it. Measured on
the 94-unknown hero DUT, one `PlanarKernel.Solve` per frequency:

| f | standard N | verdict at the dense 5,000 ceiling |
|---|---|---|
| 20 MHz | 7,507 | refused |
| 10 MHz | 14,955 | refused |
| 5 MHz | 29,858 | refused |
| 3 MHz | 49,724 | refused |

**De-embedding on this fixture stops at ~20 MHz dense and ~12 MHz accelerated — both well ABOVE the
kernel's own 2.98 MHz floor.** So even a perfect Green's function at 1 MHz is unreachable through the
present calibration, and §LF1 §5(b)'s observation on the reported board (a 9,690-unknown standard at
100 MHz against a 311-unknown DUT) is the general case rather than that board's bad luck.

#### (d) Where de-embedding stops being trustworthy is higher still

The de-embedded phase slope must be flat in frequency for a quasi-TEM line. On the same fixture:

| f | 2 GHz | 1 GHz | 500 MHz | 200 MHz | 100 MHz | 50 MHz |
|---|---|---|---|---|---|---|
| S₂₁ deg/GHz | −37.90 | −37.85 | −37.52 | −33.69 | −17.79 | **+48.20** |

11 % low at 200 MHz, 53 % low at 100 MHz, sign-inverted at 50 MHz. This is a coarse fixture and
§LF1 §5(a)'s shipping-mesh measurement of the same effect reads 46 % at 100 MHz, so the two agree on
the magnitude and the mechanism. **The practical reading is that the bottom decade of an ordinary
100 MHz – 1 GHz sweep is already inside the amplified regime**, which is a live concern for existing
runs and not only for the band below the refusal.

**The GROUPED half of that has since been measured and closed, and it was worse than (d) suggests**
(`src/Engine/Mom/RESOLVED.md` §PCAL6, 2026-09-13). Where two ports share a reference plane the same
amplification lands on a quantity a refusal is drawn on: `ModeSeparationDegrees` at 200 MHz reads
between 0.05× and 17× the modes' actual distance depending on Δℓ, so a group whose modes are
comfortably separable is refused at a Δℓ where two corrupt curves happen to cross. What governs it
is the SHORT calibration standard's own electrical length — `ShortLineHeights` = 3 substrate heights
is 1.5° of line at 200 MHz. **Regrowing that standard was built and then removed**: against the
cross-section oracle it makes the published answer worse at every frequency and nonsense at the
bottom of a decade band, so such a group is still refused and the refusal now reports both numbers.

**What was measured while establishing that, and it is the reassuring half**: an ordinary grouped
run over 100 MHz - 1 GHz on `coupled-pair` comes back at 0.130 / 0.110 / 0.074 / 0.048 / 0.032 in
max |ΔS| at 100 / 200 / 300 / 500 / 1000 MHz, against a well-separated floor of 0.268 / 0.148 /
0.106 / 0.074 / 0.054 on the same geometry — **below the floor at every point.** The growth toward
the bottom of the band is (d)'s amplification acting on any de-embedded port; co-calibration adds
nothing to it. **That does not close (d)**, which is about the single-port peel: what closes it is
the port model named under "What this leaves" below.

#### (e) The DC solve converges; a microwave mesh is too coarse for it

`PlanarDcSolve`'s port Y on an isolated line is rank 1 by construction — no conduction path to ground
means Z does not exist — and it measures that way to 1e-14, so R comes off the admittance directly as
1/g. Against the sheet answer `R_s·L/W` = 3.3973 mΩ:

| mesh built at | N | R_dc | vs sheet |
|---|---|---|---|
| 1 GHz | 24 | 2.5480 mΩ | 0.750× |
| 2 GHz | 24 | 2.5480 mΩ | 0.750× |
| 5 GHz | 45 | 2.9120 mΩ | 0.857× |
| 10 GHz | 94 | 3.1547 mΩ | 0.929× |
| 20 GHz | 247 | 3.2760 mΩ | 0.964× |
| 40 GHz | 943 | 3.3366 mΩ | 0.982× |

Monotone and convergent, which is the reassuring half. The other half is that **a mesh built for
2 GHz reads the DC resistance 25 % low** — LF1 §4's "~11 % on a coarse mesh" is the same effect
measured on a less coarse one. Any answer that leans on the conduction solve therefore has to say
something about the mesh, because the mesh a user pins for a microwave sweep is not the mesh their
DC point wants.

#### What this leaves

**"Do not calibrate" is refuted, and not for a reason about frequency.** With a delta-gap edge port
the uncalibrated answer is never the structure's own response at any frequency, and it degrades under
mesh refinement. The proposal's own premise stands — there really is nothing left to remove down
there — but the present port needs removing regardless of what it is in series with.

**The enabling change is the PORT, not the kernel and not the calibration.** What makes 0 Hz work is
that `PlanarDcSolve` uses a different port model: the terminals are the conductor's end cells against
the ground node, not a cut in the metal. A port of that shape at AC would have an a₂₁ that does not
vanish with ω, which is precisely what §LF1 §5(a) names as the thing that would close the
de-embedding amplification and records as not built. It is the same change twice: it removes the
amplification at 100 MHz that (d) measures on today's runs, and it is what would let a
sub-floor point be published without a calibration at all. That makes it a better target than the
static-kernel work, which only addresses (b) — the smallest of the four walls.

**Until it exists, the reachable options below the floor are the two LF1 already named, plus
substituting the conduction solve** at the caller's request with a note saying so and a word about
the mesh. The DCIM fit is the last of the four constraints to bite, not the first.
