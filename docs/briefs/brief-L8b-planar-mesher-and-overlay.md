# Sonnet Brief — Phase L8b: the 2D surface mesher, the plan-view overlay, and the N report

**Design:** `docs/design/layout-view.md` **§10.5 (meshing and the mesh viewer — read it first)**,
§10.7 (the N budget and **R17**), §10.3.4 (the kernel seam), §10.2 (the honest cost). Phase table row
**L8 — Full-wave, single dielectric (B)**, second of five slices.

**Read `src/Engine/Mom/CLAUDE.md` §L8a end to end before planning anything**, and
`src/Ui/Layout/Em/CLAUDE.md` before touching the Ui half. L8a is complete: the layered Green's
function, its oracle ladder, its measured accuracy range, and two occasions on which an *oracle*
rather than the method turned out to be wrong. **This brief adds no physics.** Its entire job is to
turn drawn geometry into cells, count them, and show them. If you find yourself evaluating a Green's
function, stop — that is L8c.

---

## Gate command — and it is NOT the full solution

**Run `dotnet test tests/Engine.Tests` and `dotnet test tests/Ui.Tests`, as two invocations** (this
SDK's `dotnet test` rejects two explicit project paths in one call), plus `dotnet test
tests/Firewall.Tests` whenever an assembly reference could have moved. **Do not run the
full-solution `dotnet test` at the repo root as a routine gate for this slice.**

**This applies to every L8 slice — L8a through L8d.** The full-solution run is required **once, at
the end of L8**, as part of L8e's gate. Three reasons, and the second is measured rather than
assumed:

1. **The slices touch two directories.** L8a–L8d live in `src/Engine/Mom/` and `src/Ui/Layout/Em`
   (plus this slice's renderer file). The seams that could affect anything else — `IEmKernel`,
   `EmCapabilities`, the kernel registry, the narrowed refusals — are deliberately *not opened* until
   L8e. Running 6,800 tests after each slice is cost with no signal.
2. **The full-solution run is where an unrelated test's timing budget bites.** `Hero1BTests` gates on
   a 10 s import-plus-solve wall clock. Measured at L8a under full-solution load **with L8's tests
   excluded**: 4.2 / 9.6 / 8.6 s — it already reaches its own gate on that machine, independently of
   this phase. Per-slice full runs therefore produce intermittent red that is not about the work, and
   the standing rule here is that a race is never called disproven (or proven) from a filtered run.
   **Do not "fix" `Hero1BTests` by widening its budget.** It is not this phase's test; if it becomes
   a genuine problem, report it and let the owner decide.
3. **L8e is where the full run earns itself**, because L8e is where the interface actually changes.

If a slice does something that could plausibly reach outside those directories — it should not, but
if it does — say so in the report rather than quietly running the full suite and moving on.

**Tagging:** L8a tags 5 tests `Category=Benchmark` **for another test's budget, not their own
runtime** (the heaviest is ~3 s). Follow that precedent: if this slice adds CPU-heavy sweeps whose
job is to *report* rather than to guard, tag them and keep one representative case in the routine
gate. `src/Engine/Mom/CLAUDE.md` §L8a records the reasoning.

---

## 0. Read this before planning anything

**§10.5 is explicit that the viewer lands *before* the solver, and L8b is that instruction taken
seriously.** There is no solver in this slice and there is nothing to compare against a reference.
That makes it sound like a soft phase. It is not, for two reasons:

- **The N report is the entire product.** R17 says declare a hard ceiling, surface the predicted N
  *before* solving, and refuse politely above it. Until L8b exists, nobody can answer "is this
  geometry affordable?" — and L8c and L8d are both scheduled against the answer. A mesher that
  reports 50,000 unknowns for §10.7's own worked example is a schedule problem discovered at exactly
  the right moment; discovered at L8d it is a disaster.
- **Cell ORDER is a permanent contract.** L8c's matrix fill, L8d's port excitation and L8e's heat map
  all index by cell. A mesher whose output order depends on a hash iteration, a parallel loop, or a
  floating-point tie is a solver whose s-parameters are irreproducible run to run — and that will be
  found much later, as "the answer moved slightly", which is the most expensive way to find it.

**What makes this slice tractable is that everything in it is checkable without physics.** A mesh
either tiles its input exactly or it does not; a cell either honours λ_g/20 or it does not; N either
equals the cell count or it does not. Build the ladder that says so.

---

## 1. Decisions taken

**D1. The planar problem type is a SIBLING of `EmProblem`, not a subtype, and it arrives here.**
L8a's §2 already fixed this: *"`EmProblem` is a **cross-section** model — conductor outlines in the
(x, y) plane of a *slice*, horizontal dielectric slabs, one ground plane. It cannot describe a planar
layout and must not be stretched to."* Same for its mesh: `EmMesh`/`EmSegment` are 1-D boundary
segments, and `EmMeshReport` carries `SegmentsPerInterface`, `InterfaceYs`, `TruncationHalfExtent`,
`WheelerValidAboveHz` and a `ConductorMeshTemplate` — **not one of which means anything for a surface
mesh.** Define new types. Do not add nullable fields to the old ones, do not introduce a base class
they share, and do not make one an interface implemented by both: two things that are genuinely
different, described by two types, is the cheapest arrangement to be correct in.

R-mom-1 applies unchanged: **the new type is neutral and in SI units** — metres, siemens/metre,
hertz — and knows nothing about DBU, `.clay` shapes, `LayerKey` or `Technology`. The Ui-side
extractor produces it.

**D2. Rectangular cells, and triangles are NOT built here — but the diagonal-edge question is ON
L8's critical path and must be MEASURED here.** §10.5 leans rectangular rooftop for kernel B and says
why: *"Sonnet has demonstrated for decades that a rectangular mesher is a production choice, not a
toy."* RWG-on-triangles needs a robust constrained Delaunay triangulator, which is a real commitment
and is not earned inside the slice whose job is to produce a number.

**But "L8's gates are all Manhattan" is false, and it is worth being exact about why.** L8's own
phase gate is *"a quarter-wave open stub resonates at the right frequency; **a bend's s-parameters
are physically sane**; A and B agree on a uniform line."* `MBendPCell` cuts a **45° mitre** — and
R-pc-18 records that *"mitered and unmitered are DISTINCT discontinuities"*, which is the entire
reason a bend is interesting to a full-wave kernel at all. So the shortest genuinely diagonal edge in
the library sits directly on this phase's gate. A staircased mitre is an unmitred bend with a rough
corner, which is the one thing the gate is asking the kernel to tell apart.

**Diagonals and curves are therefore staircased in L8b, and the staircasing error is MEASURED on
real library geometry rather than on a synthetic diagonal** — see §4 Tier 6 and §8.2. If the
measurement says staircasing cannot separate a mitred from an unmitred bend, that is a finding, and
conformal cells (D8) or triangles become their own brief rather than a surprise inside L8c.

**D3. Exactly three user controls, per §10.5, and no more.** `Auto` (default), `Cells per
wavelength`, `Edge mesh on/off + cell count`. **Kernel A's `EmMeshSettings` has six controls and the
temptation will be to mirror it** — do not. Its six exist because a boundary mesher over dielectric
interfaces has a truncation problem that a surface mesher does not have at all. Anything the user
does not need to think about is auto-derived from the analysis, which is §10.5's own instruction and
is what makes §10.10's 30-second target reachable.

**D4. The mesh is frequency-dependent, but computed ONCE per sweep — and the distinction is
load-bearing.** §10.5's wavelength rule *"binds only from L8 onward"*: max cell ≤ λ_g/20 at the
**highest swept frequency**, λ_g in the local dielectric. So unlike kernel A's mesh (R-mom-11:
frequency-independent, enforced by a counter) this one depends on the sweep — but on one number from
it, not on each point. **One mesh per sweep, not one per frequency.** The Green's function is the
thing that is genuinely per-frequency (L8a's R-lgf-5); do not let that leak into the mesher. State it
in code, and make the N report name the frequency it was derived at, so a user who widens the sweep
and sees N change is not confused by it.

**D5. The overlay is a GENUINE plan-view overlay now, and the existing inset panel STAYS.** This is
the one place L8b reverses an earlier decision, and it reverses it for a good reason.
`src/Ui/Renderers/LayoutRenderer.Mesh.cs` currently draws the mesh as an **inset cross-section
panel**, with this in its header:

> *The mesh lives in the CROSS-SECTION plane (x across the line, y above the ground plane); the
> layout canvas shows the PLAN view. There is no coordinate mapping between them, so painting mesh
> segments onto plan-view artwork would be a picture of nothing.*

That was correct and stays correct **for kernel A**, which still exists and still produces
cross-section meshes. Kernel B's surface mesh lives in the *same (x, y) plane the canvas already
draws*, so for the first time the coordinate mapping exists and §10.5's "system layer superimposed on
the geometry drawing cell boundaries" is the right picture. **Add the plan-view overlay; do not
delete the inset.** Which one is drawn follows from which mesh was computed, not from a mode.

**D6. Engine half gates before Ui half.** The mesher and the N report are `src/Engine/Mom/`; the
extractor, the overlay and the panel numbers are `src/Ui/`. Same staging as L6/L7. The engine half is
testable with no layout document at all, and that is the property worth protecting.

**D8. Decide the GRID MODEL here, because L8c inherits it and it is expensive to reverse.** There
are two shapes a rectangular mesher can take and they differ by an order of magnitude in cost on
exactly the geometry that motivates this question:

- a **tensor-product grid**, where every gridline spans the whole domain — trivial to fill, trivial
  to order, but a cell size demanded *anywhere* propagates a fine row or column *everywhere*;
- an **independent-cell mesh** (quadtree-ish), where refinement is local — far cheaper on mixed-scale
  geometry, but non-conforming cell edges make rooftop pairing genuinely harder, and that difficulty
  lands on L8c rather than here.

**Take the decision explicitly and record the arithmetic behind it.** A worked estimate to be
checked, not trusted — this is precisely what Tier 7 exists to replace with measurements. For a
50 Ω → 100 Ω Klopfenstein taper on 1.6 mm FR-4 swept to 10 GHz: λ_g/20 ≈ 0.83 mm sets the *axial*
cell, but R-msh-4's "3–5 cells across the narrowest conductor" applied to the ~0.7 mm narrow end
demands ~0.18 mm *transverse*. On a tensor grid the fine transverse rows run the taper's full ~80 mm
length, and it is the resulting aspect-ratio mismatch — not the curve — that drives N. A conformal or
diagonal boundary cell (one straight cut through an otherwise rectangular cell) is a much smaller
commitment than a triangulator and addresses the mitre directly; **it is explicitly not built here**,
but the cell type and the report should not be shaped so as to forbid it later.

**D9. The mesh is a THIRD discretization, and must not inherit either of the other two.** This
project already fought this once and won: R-tap-2 states *"there are two discretizations, not one"* —
`MicrostripCascadeSectioning` answers only the **electrical** question (how many uniform sections the
S-parameter cascade needs, from λ/20 and a 2% profile-resolution criterion), while the **artwork**
tessellation is *"a separate, purely geometric decision… and must never be coupled to this number"*.
`MKlopfPCell` accordingly emits a fixed 96-point tessellation — 194 polygon vertices — described in
its own source as *"fixed geometric fidelity, independent of electrical N"*.

**A mesher that snaps cell boundaries to input polygon vertices silently inherits that 96**, which is
a mesh derived from a drawing decision rather than from the analysis. On the taper above it happens
to land near λ_g/20 at 10 GHz and over-refines by ~10× at 1 GHz. Vertices are geometry to be
*covered*, not gridlines to be *adopted*. Say so in the code.

**D7. No automatic kernel selection. The `.cem` says which analysis it is.** §10.3.4 defers the
kernel registry to *"when kernel W or B exists"* and D1 of L8a puts it in **L8e**. So L8b must not
open that seam: add a field to the `.cem` naming the analysis, **defaulting to the existing
cross-section behaviour so every `.cem` written before this slice loads and re-serialises
byte-identically**, and let the user pick. Automatic selection from geometry is a registry decision
and it arrives with the registry.

---

## 2. What already exists, and what genuinely does not

**Exists and is reused unchanged — none of this should be re-implemented:**

- **`EmSuitability` / the R-mom-17 refusal vocabulary.** Name the specific feature, name where the
  capability arrives. `EmCapabilities` **already has a `Planar` flag** — it does not need widening.
- **`EmMaterial`, `EmConstants`** (`Eps0` is *derived* as `1/(µ₀c²)` — keep using it), and the
  complex-permittivity convention `ε* = ε_r(1 − j·tanδ)`.
- **`GroundedSlab`** (L8a) — the stackup as kernel B sees it, with its D2 limit and its refusals
  already worded. The planar problem type should carry one of these rather than re-describing a slab.
- **The stackup model and both starter technologies.** `Stackup`/`StackupLayer` carry
  `DrawingLayers` — *"which drawing layers map onto this stackup layer"* — so the planar extractor is
  a pure function of `(shapes, Technology)` exactly as the cross-section one is. **No `.ctech` schema
  change and no `.ctech` editor change is in scope.**
- **`LayoutRenderOptions.ShowPCellPins`' contract**, which the existing mesh overlay already copies
  and which the new one must copy too: screen-space, never layer geometry, never counted in
  `LayoutFrameCounters`, never reachable by any exporter, **defaulting to `false` so every
  export/one-shot render draws no mesh by construction**, with the toggle default living at the VM
  layer. `LayoutRenderer.Mesh.cs` does this correctly today — including *not taking* a
  `LayoutFrameCounters` parameter, so the invariant holds by construction rather than by remembering.
- **R-em-17 — an edited `.clay` CLEARS the displayed mesh.** `LayoutEditorViewModel`'s `Model.Changed`
  subscription nulls the report. A plan-view mesh drawn over *edited* artwork is worse than no mesh,
  so this matters more here than it did for an inset.
- **Kernel A's edge grading (R-mom-8)**, which was deliberately *"written against segment geometry —
  a cell-size field over attractor points — not against 'the microstrip case', so B and C reuse it"*.
  **Test that claim rather than inheriting it** — see §3 R-msh-5.

**Does NOT exist:**

- **Any planar/surface mesh anywhere.** `BoundaryMesher` meshes perimeters and horizontal interfaces
  in a cross-section; nothing in the repository subdivides a 2-D region.
- **Any N ceiling, budget check or R17 refusal.** §10.7 says the constraint *"is what arrives with
  the full-wave kernel"* and kernel A never needed it (a few hundred segments, milliseconds).
- **Any planar extractor.** `CrossSectionExtractor` is 939 lines and almost all of it is the hard
  part of §10.3.3 — detecting that geometry *reduces* to straight, mutually parallel, constant-width
  conductors, and refusing specifically when it does not. **A planar extractor needs none of that**,
  because accepting geometry that does not reduce is the entire point of a full-wave kernel. **Do not
  bolt it onto `CrossSectionExtractor`**: it is a different, much simpler function that happens to
  read the same inputs, and merging them would put the refusal logic of one on the acceptance path of
  the other.

---

## 3. Requirements

**R-msh-1. The mesh tiles its input exactly.** The union of the cells is the input geometry, to the
last DBU: no gaps, no overlaps, no cell straying outside the metal. This is the one property
everything downstream silently assumes — a fill over a mesh with a sliver gap solves a slightly
different structure and reports a smooth, plausible, wrong s-parameter. Assert it as **area
conservation plus a pairwise-overlap check**, not as a visual inspection.

**R-msh-2. Cell order is deterministic and stable, and it is part of the contract.** Same geometry
and same settings ⇒ **bit-identical** cell list, in the same order, across runs and across processes.
Sort by a stated geometric key (e.g. layer, then y, then x, with an exact integer comparison on DBU —
never a floating-point tie-break). Say in the code that L8c/L8d/L8e index by this order. A
`HashSet`/`Dictionary` iteration anywhere on this path is a defect even when it happens to be stable
today.

**R-msh-3. The wavelength rule, in the LOCAL dielectric.** Max cell ≤ λ_g/20 at the highest swept
frequency, with λ_g computed in the medium the metal actually sits on — not in free space. Getting
this wrong is a factor of √ε_r (2.1 on FR-4, 3.6 on GaAs) in cell count *and* in accuracy, in
opposite directions, and it is invisible without an explicit test. **The "20" is `Cells per
wavelength` and is one of D3's three controls.**

**R-msh-4. At least 3–5 cells across every conductor width**, §10.5's own number, and it must hold
for the *narrowest* conductor in the layout rather than on average. A 2.9 mm feed and a 100 µm stub
in one drawing is the ordinary case, and a mesh sized off the big one resolves the small one with a
single cell.

**R-msh-5. Edge refinement, with the reference length settled by MEASUREMENT rather than by
inheritance.** The 1/√d edge singularity is the same physics kernel A meshes for, and R-mom-8's
cell-size-field formulation was written to be reusable. **But R-mom-8's headline finding does not
obviously carry over and must not be assumed to:** kernel A's edge cell is a fraction of the
conductor's *smallest bounding-box dimension* — the metal **thickness** — because in a cross-section
the charge singularity lives at the 90° corner, whose scale is the thickness. A planar surface mesh
sits on a sheet with **no thickness in the model at all**, so the analogous scale is an in-plane one.
Decide it by measuring N and the mesh's own convergence behaviour against both candidates, and
**record the number**, exactly as R-mom-8 records "measured: with the width reference ε_eff converges
as N^−½ and sits 4% low at any affordable N; with the thickness reference it is within 0.1% at
N ≈ 150". If kernel A's code is genuinely reusable, reuse it and say so; if it is not, say why.

**R-msh-6. N is reported before anything is solved, and it is exact.** `UnknownCount` equals the
number of basis functions L8c will produce — **not** the cell count, if those differ (for rooftop
bases on a rectangular grid they do: a rooftop spans a *pair* of adjacent cells, so N is the number of
shared internal edges, not the number of cells). **Work out which number R17's ceiling is about and
say so in the code**, because reporting cells and budgeting basis functions is a factor of ~2 error
in the one number this slice exists to produce.

**R-msh-7. R17 — a hard ceiling, surfaced early, refused politely.** Declare it (~5000, per §10.7's
own table: 5,000 unknowns is 400 MB and *"the practical ceiling for lightweight"*). Above it, refuse
by name with a message that says the predicted N, the ceiling, and **what to change** — coarser cells
per wavelength, edge mesh off, a smaller analysed region. *"A 'lightweight' simulator that silently
tries to allocate 12 GB is not lightweight."* Warn, don't refuse, in a band below the ceiling.

**R-msh-8a. Where an ANALYTIC model already exists and is better, say so — do not silently spend
5,000 unknowns reproducing it.** A Klopfenstein taper is a slowly-varying quasi-TEM structure, which
is the regime a cascade of short uniform sections models extremely well and full-wave is not needed
for. circuitRF already ships that model: `MicrostripKlopfModel` + `KlopfensteinTaper` +
`MicrostripCascadeSectioning`, sourced to Klopfenstein 1956, Kajfez & Prewitt's endpoint correction
and Grossberg's rapid series, adaptive in frequency, and effectively free. Kernel B exists for
**discontinuities, radiation and resonance** — bends, stubs, junctions, spirals, coupled structures —
not for smooth tapers.

So when the analysed geometry is (or contains) a PCell that has a validated analytic model, the N
report should **note it**, naming the model, alongside the predicted cost. Not a refusal — a user may
legitimately want the full-wave answer, e.g. to check the taper's own radiation or its interaction
with a nearby structure — but the choice should be informed. This is the R-mom-17 shape applied to a
cost rather than to a capability: name the thing, name the alternative.

**R-msh-8. The report carries everything the panel shows, computed in the engine.** Same rule as
`EmMeshReport`: *"report it from the engine so the UI has nothing to recompute."* Cell count, N,
min/max cell size, cells across the narrowest conductor, the frequency λ_g was taken at, per-layer
counts, the staircasing note, and every refusal/warning string.

---

## 4. The oracle ladder

Same rule as every phase in this area: **each tier passes before the next is written.** There is no
solver here, so every tier is exact — which is a luxury, not a shortcut. Use it.

**Tier 0 — the mesh is a mesh.**
- Union of cells == input geometry (area to the last DBU, and a boundary comparison, not just area —
  two different wrong meshes can have the same area).
- No two cells overlap. No cell lies outside its conductor.
- Every cell is non-degenerate (positive width and height).

**Tier 1 — the rules are honoured, on geometry chosen to violate them.**
- Max cell ≤ λ_g/20 in the *local* dielectric, checked on both starter technologies, where √ε_r
  differs by 1.7× between them.
- ≥ the configured cells across the **narrowest** conductor in a layout that deliberately mixes a
  2.9 mm feed with a 100 µm stub.
- Edge cells present, graded by the configured ratio, at every metal edge — including the edges of an
  interior hole, which is the case a perimeter walk gets wrong.

**Tier 2 — N is exact.** The reported `UnknownCount` equals an independently computed count over the
returned cells (count shared internal edges directly in the test; do not call the mesher's own
counter). This is the number R17 refuses on and the number L8c is scheduled against, so it gets its
own tier.

**Tier 3 — convergence and scaling.**
- Refining any control **monotonically increases** N and never decreases it.
- Halving the target cell size **quadruples** N to within a stated tolerance on a rectangle — a 2-D
  mesh that scales like N^1 has a bug that no visual inspection finds.
- The mesh of a shape is unchanged by translating that shape by a whole number of cells, and changes
  *only near the edges* under a sub-cell translation.

**Tier 4 — determinism.** Same input ⇒ bit-identical output, including order (R-msh-2). Run it twice
in one process and compare the full cell list element by element, not a hash of it.

**Tier 5 — R17.** The refusal fires at the declared ceiling and not before, names the predicted N,
and names a remedy. The warning band fires below it. A geometry just under the ceiling is accepted.

**Tier 6 — staircasing, measured on REAL library geometry (D2).** A synthetic 45° square is not the
test; the shipping PCells are, because they are what a user will actually select.
- **`MBendPCell` with its 45° mitre**, the case on L8's own phase gate. Report the area error of the
  staircased mitre cut against the true triangle, and — the number that matters — **how much of the
  mitre survives at the cell size R-msh-3 and R-msh-4 actually choose.** A mitre cut that quantises
  to zero or to one cell means the mesher cannot represent the discontinuity the gate is about.
- **`MKlopfPCell`** (194-vertex smooth outline) and **`MTaperPCell`**, both on-axis and with the
  Offset variant, whose sloped centerline turns a nearly-grid-parallel edge into a genuinely diagonal
  one. Report the local width error along the taper, not just a global area error: a Klopfenstein
  profile's whole value is a controlled equiripple |Γ|, and a width error that is negligible as a
  fraction of total area can still be large compared to the ripple the taper was designed for.

This is the measurement §8.2 asks for, and it is what decides whether conformal cells or triangles
become a brief of their own.

**Tier 7 — §10.7's own worked example, which is a closed-form prediction.** The design note already
computes it: *"50 Ω microstrip on 1.6 mm FR-4 is W ≈ 2.9 mm; a 20 mm line at 10 GHz has λ_g ≈ 16.5
mm, so λ_g/20 ≈ 0.8 mm → ~24 cells long × ~6 across with edge refinement → N of a few hundred."*
**Reproduce that.** It is the only end-to-end sanity check available before a solver exists, it is
independent of the implementation, and it catches the whole class of errors — wrong λ_g medium, wrong
units, cells confused with unknowns — that produce a mesh which is internally consistent and
completely the wrong size. Do the same for the GaAs starter and report the number.

**Then report N for the three non-Manhattan library PCells** — mitred `MBend`, `MKlopf`, `MTaper`
(with and without Offset) — on both starter technologies. **This is the number that decides D8**, and
it costs almost nothing once Tier 7's machinery exists. R17's ceiling is 5,000; if a shipping PCell
that a user can place in one click lands above it on a tensor grid, that is the finding, and it is
worth far more at L8b than at L8d.

**Tier 8 (Ui) — the overlay contract.** Copy `EmMeshOverlayTests`' existing shape:
- draws nothing when the toggle is off, and the toggle defaults to off at the render layer;
- contributes zero to every `LayoutFrameCounters` geometry count;
- is absent from every exporter's output;
- is cleared by a `.clay` edit (R-em-17);
- **the inset cross-section panel still works** — kernel A's mesh did not stop existing.

---

## 5. What must NOT be built here

- **Basis functions, matrix fill, singular or near-singular integrals** — L8c, and §10.2's *second*
  place a schedule dies. Reporting the number of basis functions (R-msh-6) is not the same as
  defining them; if you are writing an integral, you have gone too far.
- **Any solve, any port, any de-embedding** — L8d.
- **The current-density heat map** — L8e, because it needs a solution to display. But **shape the
  overlay so the heat map is a per-cell scalar added later**, not a rewrite: one colour-per-cell path
  with a null scalar today is the whole provision needed.
- **The kernel registry, any `IEmKernel` change, any `EmCapabilities` widening** — L8e (D7).
- **RWG / triangles / a Delaunay triangulator** — D2.
- **N dielectrics, vias, z-directed current** — L9.
- **Adaptive frequency sampling** — §10.7 says build it when the kernel that needs it exists.
- Nothing in `src/Core`, `RfCore`, or `src/Engine` outside `Mom/`.

---

## 6. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | The neutral planar problem type + the mesher core: rectangular cells over Manhattan geometry, deterministic order, exact tiling. | **Tier 0, 2 and 4 green.** |
| **M2** | The meshing rules: wavelength in the local dielectric, cells-across-narrowest, edge grading with its reference length settled by measurement. | **Tier 1 and 3 green**, and R-msh-5's measured comparison reported. |
| **M3** | The N report and R17: the ceiling, the warning band, the refusal wording. | **Tier 5 and 7 green — and §10.7's worked example REPRODUCED, with the number reported.** Stop and report here if N is not "a few hundred"; that is a schedule signal, not a rounding difference. |
| **M4** | The Ui half: the planar extractor, the plan-view overlay, the panel numbers, the `.cem` field. | **Tier 8 green**, existing `.cem` files byte-identical on load/save, and the inset panel still working. |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  PlanarProblem.cs        the neutral planar problem — polygons in metres + a GroundedSlab (new)
  PlanarMesh.cs           cells, their order, and the mesh itself (new)
  PlanarMeshSettings.cs   D3's THREE controls, and no more (new)
  PlanarMeshReport.cs     N, counts, sizes, the frequency, notes, the R17 verdict (new)
  SurfaceMesher.cs        the mesher (new)

src/Ui/Layout/Em/
  PlanarExtractor.cs      .clay shapes + Technology -> PlanarProblem (new; NOT part of
                          CrossSectionExtractor)
  EmSetupModel.cs         + the D7 analysis-kind field, defaulting to cross-section

src/Ui/Renderers/
  LayoutRenderer.PlanarMesh.cs   the plan-view overlay (new; LayoutRenderer.Mesh.cs stays)

tests/Engine.Tests/Mom/
  SurfaceMesherTests.cs         Tiers 0-7
tests/Ui.Tests/Em/
  PlanarMeshOverlayTests.cs     Tier 8
```

---

## 8. Three things to report back on, whatever else happens

1. **The measured N for both starter technologies' hero geometries, against §10.7's own prediction
   of "a few hundred".** This is what L8c and L8d are scheduled against, and it is the one number
   this slice exists to produce. Report the breakdown too — cells, basis functions, cells across the
   narrowest conductor — because "N is 4,000" and "N is 4,000 because the edge mesh is being applied
   to a 100 µm stub at 3% of the wrong reference length" are different findings.
2. **The staircasing error for the three non-Manhattan library PCells, as a number and as a function
   of cell size** (Tier 6), with the mitred `MBend` first because it is on L8's own phase gate — and,
   given those numbers, a plain recommendation on which of three routes L8c should assume: staircase
   as-is, conformal/diagonal boundary cells, or a triangulator. Say which, and why, in one paragraph.
4. **Which grid model D8 landed on, with the measured N that decided it** — tensor-product or
   independent cells — and the N for each non-Manhattan PCell against R17's 5,000 ceiling. If a
   one-click library part exceeds the ceiling, that is the headline of the report, not a footnote.
3. **Whether kernel A's edge-grading code was actually reused, and what the reference length turned
   out to be** (R-msh-5). R-mom-8 records a measurement, not a preference; this slice should record
   its own rather than inheriting one taken in a different geometry.
