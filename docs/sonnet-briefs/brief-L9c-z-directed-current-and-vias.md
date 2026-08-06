# Sonnet Brief — Phase L9c: z-directed current, vias, and the multi-level structure

**Design:** `docs/design/layout-view.md` **§10.2** (item 3's research risk, now measured twice — L8a for
the grounded slab, L9b for the general stack; this slice walks into the part neither of them covered),
§10.5 (the mesher, whose D8 grid decision was taken *for* this slice), §10.7 (the N budget this doubles),
§11's phase table row **L9**.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` **§L9b** end to end — its D5
and D6 sections are the specification of half this slice and its measured numbers are what you are
scheduled against — then **§L9a** (the conventions and the cascade), then **§L8c** (the fill, the basis,
and the three singular pieces), then **§L8b**'s D8 and R-msh-2. Then read `PlanarBasisFunctions.cs`'s
header, `PlanarFill.cs`'s header and `SommerfeldIntegral.cs`'s L9a section; they are the derivations you
are extending, not summaries of them.

**Third of L9's five slices.** L9a built the spectral kernel for an arbitrary stratified medium; L9b made
DCIM work for it and established that a height pair in the top half-space needs no refit at all. **This
brief specifies L9c: the Green's-function components a z-directed current needs, the inverse transform for
sources that are not in the top half-space, the via basis and its junction condition, and the multi-level
problem type.** No ports, no references, no extractors, nothing in `src/Ui` (L9d); no adaptive frequency
sampling, no ACA, no refusal audit, no phase gate (L9e).

---

## Gate command — and it is NOT the full solution

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

L8a's, L9a's and L9b's precedent stands: a slice that touches only `src/Engine/Mom/` has a blast radius of
`src/Engine/Mom/`, and the full-solution run belongs **once, at the end of L9, as part of the last slice's
gate**.

**The routine tier's headroom is now small and the constraint has MOVED.** L9b left `Engine.Tests` at
**942 routine tests in 45 s** against the ~60 s ceiling. It is no longer the DCIM fit that costs — L9b
measured that at 11–102 ms, the same as the shipped one-layer fit — it is the **ORACLE**, at ~0.13 s per
(ρ, height pair) point for the top half-space, and an interior source will not be cheaper. **Budget the
routine tier at roughly a hundred oracle points in total and put every sweep behind
`Category=Benchmark`.** L8's opt-in tier was ~8.5 min, L9a added 8 s, L9b added ~11 min; say what you add,
and say how much of it is oracle rather than product.

---

## 0. Read this before planning anything

### 0.1 What L9b established, in numbers

These are measured, they are in `src/Engine/Mom/CLAUDE.md` §L9b with the tests that produce them, and this
slice is scheduled against them.

- **A DCIM fit costs 11–102 ms per frequency per kernel component, on every stack** — the same as the
  shipped one-layer closed-form fit (96 ms), *not* the 1.4–3.4 s L9a projected. L9a's projection scaled by
  the per-sample kernel ratio; the fit samples the reflection ladder, and most of its wall clock is Prony.
  **Do not re-derive a cost projection from a per-sample number. It was wrong by 15–35× last time.**
- **A height pair in the TOP half-space is an EXACT SHIFT and needs no refit** — same amplitudes, depth
  `b_i + Σ`, poles scaled by the real decay `e^{−αΣ}`, and the far-field sum rule survives untouched.
  Measured over a grid of height pairs; the algebra is pinned to 8e-15 on the εᵣ = 1 control.
- **The INTERIOR same-region case is ALSO an exact shift — of FOUR exponential families in the source
  region's own `k_zm`, not one in k_z0.** With both points in region m the voltage is
  `(Z_m/2·denom)·[e^{−jk_zmΔ} + Γ_t e^{−jk_zm(2d−Σ_b)} + Γ_b e^{−jk_zmΣ_b} + Γ_tΓ_b e^{−jk_zm(2d−Δ)}]`,
  `Σ_b = z + z′ − 2z_b`, and the four coefficients do not depend on the heights. **Measured, not asserted:**
  four coefficients solved from four height pairs predict a fifth to **9.8e-15**, on every stack, both
  polarisations, k_ρ/k₀ ∈ {0.3, 2, 15} — `LayeredDcimTests.R5_3`.
- **The CROSS-REGION case mixes `k_zm` and `k_zn` and has no single reference wavenumber to be an exact
  shift in.** It is a genuinely different question and L9b reported it as one. **It is also exactly the case
  two metal levels at two different interfaces produce**, so it is not avoidable here.
- **There is no interior oracle, and three things refuse by name pointing at L9c**:
  `DcimModel.Evaluate(ρ, z, z′)`, `SommerfeldIntegral.EvaluateLayered` and `LayeredStaticGreens`. L9b
  enumerated what building one costs — see D3.
- **The accuracy envelope you are extending**: inside everything `Dcim.WithinValidatedRangeLayered` admits,
  the error as a fraction of the free-space kernel is **≤ 1.6e-2** (L8a's one-layer number was ≤ 6e-3), with
  a derived **near-field floor at ρ/λ = 1/(2π·PathExtent) = 5.3e-4** and two named structural refusals
  (`Dcim.CanFit` for a denser open bottom, and an electrically thin ungrounded stack).

### 0.2 Six things that are true before you start

**1. The multi-level MESHER is mostly already built, and this is the biggest scoping surprise in the
slice.** L8b's D8 chose a single tensor-product grid *shared across layers* explicitly so that "L9's
multi-level stack needs vertical current to cross between them, and a per-layer grid would make that a
re-mesh rather than an addition" — read that comment in `PlanarMesh.cs` before planning any mesher work.
`SurfaceMesher` already loops `problem.Layers`, emits `PlanarCell.LayerIndex`, and pairs rooftops per
layer. `PlanarBasis` carries a layer index. `PlanarPort` carries a layer index. **What is genuinely missing
is not the mesh — it is the z COORDINATE of each level.** `PlanarProblem` carries a `GroundedSlab`, and
`PlanarConductorLayer` has `Name`, `Polygons`, `SigmaSm`, `ThicknessM` and **no height at all**. That is a
type change with consequences that reach the Ui-side extractor, which is L9d's. **D6 is where you decide
how far it goes.**

**2. Nothing in this repository has a z component of anything.** `GreensKernel` is a two-valued enum
(`VectorPotential`, `ScalarPotential`). `PlanarBasisFunctions` is `x̂` or `ŷ` and nothing else — and D5 of
L8c leans on that: an X-rooftop and a Y-rooftop are pointwise orthogonal, so the vector block is
block-diagonal by direction and half the vector fill disappears. **A z-directed basis is not orthogonal to
either of them in general, and whether it is depends on the answer to D1.** That is a real change to the
fill's structure, not an added case.

**3. Four refusals name this phase and must be NARROWED, never deleted.** `GroundedSlab.CanHost` refuses
more than one conductor layer and any metal not on the slab's top surface. `PlanarKernel.CanSolve` refuses
more than one conductor level. `DcimModel.Evaluate(ρ,z,z′)` and `SommerfeldIntegral.EvaluateLayered` refuse
a source inside the stack. **A refusal is narrowed when the capability arrives and never in advance of
it** — and L9b's own experience is the warning here: its first ungrounded-stack refusal was written on a
plausible mechanism and was refusing two stacks it had no business refusing, which only the "a refusal must
be EARNED" assertion caught. Write that assertion into every refusal you add.

**4. `EmCapabilities.LayeredWithVias` has been declared since L6 and read by nothing.** It exists for this
slice. Wiring it is L9d/L9e's (the registry and the extractors are theirs); **declaring what it means, in
one place, is yours.**

**5. §11's L9 gate sentence is still unresolved and is still not yours to settle.** L9a found that
*"agreement with published reference structures"* cannot be a gate under this project's own rules and
proposed a replacement; the owner has not ruled. Do not build a gate on published multilayer data, and do
not pre-empt the decision. L9e owns it.

**6. This slice has L8's own shape, and the fault line is stated here rather than discovered in week
three.** L8 was split five ways because §10.2 flagged one schedule-uncertain piece and the four downstream
slices were tractable *only once the Green's function existed*. L9c's phase-table content has the same
structure: **M1–M3 below are the Green's-function work (new dyadic components, a new inverse transform, a
new fit), and M4–M5 are the ordinary engineering that stands on them.** L9b measured that the interior
oracle does not exist *at all*, which makes M2 a research item and not an increment. **If M1–M3 consume the
slice, stop and report** — that is the natural fault line, the milestone order below is unchanged either
way, and whether L9 becomes six slices is the owner's call and not this brief's.

---

## 1. Decisions taken

**D1 — The dyadic components are DERIVED here, exactly as L8a derived its pair, and HOW MANY there are is
the first thing to establish rather than the first thing to assume.**

L8a obtained `G̃_A = V_i^h/(jωµ₀)` and `G̃_q = jωε₀(V_i^e − V_i^h)/k_ρ²` by requiring the mixed-potential
representation `E = −jωA − ∇φ` to reproduce the spectral electric-field dyadic of a **horizontal** dipole —
and the consistency check that made the split legitimate is that the **xx, yy and xy components each
independently produce the same G̃_q**. Apply the same requirement to a **vertical** dipole and to the mixed
terms. That is what fixes how many kernel components there are, whether the scalar kernel stays a single
scalar, and where the formulation's characteristic asymmetry lands. `SpectralGreens`'s own header is the
model for how to write it down.

**Two structural facts are free and are the checks that catch a plausible-but-wrong derivation:**

- **A z-directed current excites TM only.** A TE field has E purely transverse, so a z-directed source
  couples to nothing in it. `G_A^xx` in formulation C is purely TE (that is what makes it formulation C);
  whatever carries the vertical current is built from the TM line alone. **The two are therefore not the
  same function with a polarisation swapped**, which is the wrong obvious answer this milestone has.
- **The image sign flips.** Over a PEC ground plane a horizontal current has a NEGATIVE image and a
  vertical one has a POSITIVE image. The εᵣ = 1 reduction is therefore a *different* exact answer for the
  new components than for the old ones — and it is the strongest single check in the slice, the direct
  analogue of L8a's `T1_2` and kernel A's `T0_7`. Getting it backwards produces a smooth, plausible,
  completely wrong structure and nothing else catches it.

Name the components in a doc comment and say which transmission line each comes from. `GreensKernel` grows;
decide once whether it grows into an enum with more members or into something with a source and observer
direction, and write down why — `Dcim`, `SommerfeldIntegral`, `SingularExtraction` and `PlanarFill` all
switch on it, and two of them switch on it in a hot loop.

**D2 — The via basis is an ATTACHMENT MODE, and continuity is built into the basis rather than added as a
constraint row.**

This is L8c's D2 generalised, and its reasoning carries over verbatim: "there is no second basis family, no
charge-only basis and no half rooftop at the boundary — adding one would put charge on the rim, which is
physically wrong and would silently change every answer rather than failing." **A via carries current from
one horizontal level to another; if the basis does not conserve charge at the foot, charge accumulates
there and the wrongness looks like a bad mesh.**

Two constructions are available and the measurement decides, not taste:

- **(a) An attachment basis** spanning the via and the horizontal cells at each foot, so `∫∇·f dS = 0`
  holds by construction exactly as the rooftop's does — `+1/Area` on one cell, `−1/Area` on another, with
  the vertical segment carrying the current between them.
- **(b) A separate vertical basis plus an explicit continuity constraint row** in the system.

**(a) is the expected answer** and it is the one that keeps `PlanarSolve`'s single factorisation and
`Y = BᵀZ⁻¹B` structure intact (L8d's D1). **(b) changes the shape of the linear system**, which reaches
ports, de-embedding and the current-density reduction. If you find yourself reaching for (b), that is a
finding worth reporting before building it.

**D3 — The interior/cross-region ORACLE is this slice's schedule risk, and L9b already scoped it.**

You cannot measure a fit you have no oracle for, and there is no interior oracle. L9b enumerated exactly
what one needs, from the structure of the integrand rather than from a guess:

- **`SommerfeldIntegral.FreeSpace(double k0, Complex r)` takes a REAL wavenumber and must widen to a
  complex one.** The Sommerfeld identity itself is unchanged for complex k — but a lossy interior layer's
  `k_m` is complex where the top half-space's `k₀` is real, and every closed-form extraction is referenced
  to the source region's own wavenumber.
- **Three closed-form extractions, not two.** L8a extracts the direct term and the quasi-static constant. A
  source sitting exactly *on* an interior interface — which is precisely where metal goes — makes the
  down-reflection term non-decaying as well, so it needs its own extraction or the tail never converges.
- **The `k₀ sinθ` / `k₀ cosh u` substitutions are NOT needed** for the interior case: a lossy interior
  region's `k_zm` never vanishes on the real k_ρ axis, so there is no 1/k_z singularity to remove. **But
  the top half-space's own branch point at k_ρ = k₀ is still in the integrand as a square-root kink** and
  needs breakpoints, in the same way the surface-wave poles already do.
- L8a's warning applies to the whole of it: **the textbook contour deformation is wrong here**, because
  J₀ grows like `e^{|Im z|}`. The path stays real.

**D4 — Two metal levels means THREE height pairings, and only one of them is already solved.**

Low–low, low–high, high–high. L9b's shift theorem covers **high–high** (both in the top half-space) with no
refit at all, and its four-family result says **low–low** (both at an interior interface, same region) is
also an exact shift once four families are fitted in `k_zm`. **Low–high is the cross-region case and is the
one with no single reference wavenumber.** Whether it is a shift in *some* variable, or genuinely needs a
two-variable treatment, is the question — and it is the one §10.2's original warning was actually about.
Answer it with a measurement, and if the answer is "two variables", say what that costs before building
it.

**D5 — Junction continuity is a GATE, not a comment, and it is the R-mom-11 pattern.**

Kernel A enforces "the frequency-independent quantities really are computed once" with
`RlgcModel.MatrixFillCount`, asserted at exactly 4 for a 3-point and a 1001-point sweep, **not with a
comment**. L8c does the same with `PlanarSweepResult.CoreFillCount`. Do the same here: the total charge on
a via basis, and the current continuity at each foot, must be **asserted as numbers** on a real
multi-level mesh — `Σ ∫∇·f dS = 0` to machine precision, and the current entering a foot equal to the
current leaving it. Both are exact statements, not tolerances, if the basis is constructed per D2(a).

**D6 — The problem type gains z coordinates, and how far that change travels is decided HERE.**

`PlanarProblem` carries a `GroundedSlab`; it must carry a `LayerStack` (L9a's type), and
`PlanarConductorLayer` must carry the z of its level. Three things follow and each is a decision:

- **`PlanarProblem.GuidedWavelengthM`** uses `Slab.Material.EpsR`. With N dielectrics there is no single
  εᵣ; R-msh-3's rule ("the shortest wavelength any part of the structure can see, the conservative
  direction, the only one available before a solve") tells you what to replace it with.
- **`GroundedSlab` is still shipped and still correct for one slab.** L9a's D5 precedent applies: do not
  delete it in favour of the general type; gate the general path against it and let collapsing them be a
  later, separate, measured decision.
- **The Ui-side extractor produces `PlanarProblem`** and it is behind the firewall. **You may not touch
  `src/Ui`.** So either the new members are optional with a one-slab default, or the extractor is left
  producing the old shape and L9d adapts it. **Decide and say which**; leaving a required member with no
  producer means L9d discovers a compile error rather than a design.

**D7 — Report the COST as a projection you have checked, not as one you have computed from a per-sample
number.**

The projection to make and then verify: **four kernel components × three height pairings ≈ 12 fits per
frequency at L9b's measured ~0.1 s each ≈ 1.2 s**, on top of L8d's measured **7.66 s per de-embedded
point** at the hero's N = 552. And **two metal levels roughly double N**, which is quadratic in the fill —
L8c measured the fill at 114× the LU at N = 552 and still 1.8× it at N = 4,933, so the fill is what grows.
**L8b measured N = 552 for §10.7's hero and up to 2,055 for a library PCell on ONE level**; two levels plus
vias may cross R17's 5,000 ceiling on ordinary geometry. Enforcement is L9e's. **The number is yours**, and
if a 101-point sweep of a two-level structure lands in hours rather than minutes, that is the finding and
L9e's adaptive frequency sampling stops being optional.

**D8 — No dependency, and no eigensolver.** L8a wrote its own Bessel functions and reached GPOF's poles
through classic Prony plus Durand-Kerner rather than committing to a general complex eigensolver; L7b-b
weighed the same commitment and declined; L9b did not revisit it. Nothing here changes that calculus. A
z-directed basis does not need new special functions — if you find yourself wanting a linear-algebra
package, the formulation has gone wrong.

---

## 2. What already exists, and what genuinely does not

**Exists and is load-bearing — read it before writing anything:**

- `LayeredSpectralGreens` — the cascade, `Voltage(pol, w, z, z′)` at **arbitrary** source and observer
  heights including interior and cross-region, `KzOfRegion`, `TopInterfaceReflectionAtKz0`, the D7 cascade
  cache. **The spectral side of the interior case is already built and gated** (L9a Tier 0–2); what is
  missing is the inverse transform and the components.
- `Dcim` — `FitCore` and its three inputs, `BranchPointTaylor`, `Residue`/`ContourAverage`, `PoleSum`,
  `FitAmplitudes`, `Prony`, `LinearAlgebra`, and `DcimModel.Evaluate(ρ, z, z′)`. **Every measurement that
  chose a default is in a doc comment.**
- `SommerfeldIntegral` — `Transform` (exposed precisely so the Sommerfeld identity is checkable standalone
  on one exponential before a sum of them is), `EvaluateLayered`, `CanIntegrateLayered`, the J₀-zero tail
  partition and the pole breakpoints.
- `PlanarBasisFunctions`, `PlanarFill`, `SingularExtraction`, `RectangleIntegrals` — the rooftop, the
  per-cell potential matrix, the three singular pieces and the six derived closed forms.
- `SurfaceMesher`, `PlanarMesh` — already multi-layer, already on one shared conforming grid (§0.2 item 1).
- `PlanarSolve`, `PlanarPort`, `PlanarDeembed` — untouched by this slice, but their contracts (`Y = BᵀZ⁻¹B`,
  one factorisation, reciprocity structural) are what D2's choice must not break.

**Does not exist:**

- **Any z component of any Green's function** — D1.
- **Any inverse transform for a source that is not in the top half-space** — D3.
- **Any vertical basis function, any via geometry in `PlanarProblem`, any z coordinate on a conductor
  level** — D2, D6.
- **Any interior-height oracle**, spectral-domain or static — D3.
- **Ports on more than one level in a way that has been exercised.** `PlanarPort.LayerIndex` exists and is
  resolved; nothing has ever passed it a non-zero value.

---

## 3. The formulation, stated as requirements

**R-via-1 — The horizontal-only path stays bit-identical.** Every number in L8a's, L8c's, L8d's and L9b's
tables must be unchanged, including `Dcim.Fit`'s one-layer output and the assembled matrix on a
single-level problem. If you refactor shared internals — and `PlanarFill` and `GreensKernel` are both going
to want it — reconstruct the pre-change result and compare at **full precision**, the way L9b pinned twelve
dumped fit configurations and L7b-b found two one-ulp re-associations. The Tier oracles carry tolerances
and structurally cannot catch a one-ulp move.

**R-via-2 — Every kernel component is derived, and the derivation is written where the code is.** Names in
the literature are attribution, not provenance (R-lgf-1). What makes it trustworthy is the εᵣ = 1
reduction, not a citation.

**R-via-3 — Charge is conserved exactly, and it is asserted as a number.** Per D5.

**R-via-4 — The accuracy claim is a MEASURED range per height pairing.** Two measures, as L8a, L9a and L9b
all report: the **scaled** error `|ΔG|·4πR` — and note `R`, not ρ, once the two points are at different
heights, because normalising by ρ overstates the error whenever the vertical separation dominates — and the
**strict relative** error. Report both, per component, per height pairing, over the same ρ/λ span, on the
same stacks. Compare against L9b's ≤ 1.6e-2 and say whether the vertical components are worse and by how
much.

**R-via-5 — Determinism, bit for bit.** L8a asserts `Dcim.Fit` is deterministic across repeated calls and
across a serialize/reload; R-msh-2 makes cell order a permanent contract; R-fil-11 makes the parallel fill
write every entry exactly once from one thread. **A via basis introduces a new ordering question** — where
vertical bases sit relative to horizontal ones in the unknown vector — and it is a contract from the moment
it is written, because ports, the current-density map and the de-embedding all index by it. State it once,
in the type, as R-msh-2 does.

**R-via-6 — Every refusal names the specific feature and where the capability arrives (R-mom-17), and every
refusal is EARNED.** A stack, a geometry or a height pairing this cannot handle is refused by name — and
the test that measures the refused case must assert the answer out there is **actually bad**, so a
mis-scoped refusal fails loudly instead of standing. L9b added that assertion and it immediately caught a
wrong refusal.

**R-via-7 — Nothing is cached across frequencies** that is not already provably frequency-independent.
L8c's `CoreFillCount` and L9a's per-instance cascade cache are the two patterns; a new component must join
one of them or neither, and the counter says which.

---

## 4. The oracle ladder

**Tier 0 — structural, free.** Reciprocity in the new components (source and observer swapped, including
across levels); the k_ρ → 0 limit; the proper branch; every refusal. L9a's cross-region reciprocity is the
model: the two orders take genuinely different computational paths, so their agreement is a real check
rather than a tautology — **do not canonicalise the order to obtain bit-identity**.

**Tier 1 — the εᵣ = 1 reduction, and it is the strongest single check in the slice.** Over a bare ground
plane with no dielectric, a vertical current's answer is free space plus **one POSITIVE image**, and a
horizontal one's is free space plus one negative image, both exactly. No external data, no quadrature, and
no plausible-but-wrong dyadic survives it. Do this before anything empirical.

**Tier 2 — the one-layer and horizontal-only reductions (R-via-1).** The shipped path reproduced exactly.

**Tier 3 — the interior oracle against itself, before it is believed.** Convergence under refinement, the
tail series converged, and the εᵣ-uniform reduction (every layer and both terminations the same material
over a PEC floor) where the interior answer is free space in that medium plus one image and is elementary.
**This area has now had five occasions where the ORACLE, not the method, was at fault — L9b's D3 conclusion
rests entirely on having checked the oracle first, at a cost of 6 m 40 s.** Budget for it.

**Tier 4 — the static limit.** As ω → 0 the fitted model converges onto the electrostatic answer
quadratically with no floor. **L9b's own trap is waiting here**: `DcimSettings.PathExtent = 300` is in
units of k₀ while the stack's structure lives at 1/H, so the product `300·k₀H` is what decides whether the
fit sees the stack — and below ~1 the error *grows* as the frequency falls. That is neither a floor nor the
oracle. Hold the sampled k_ρ range fixed in physical units and the convergence is exactly quadratic.

**Tier 5 — DCIM against direct integration, per height pairing. THIS IS THE REPORTED MEASUREMENT.**
R-via-4's curve. L9c/L9d's fill and L9e's budget are scheduled against it.

**Tier 6 — the assembled matrix.** L8c's rungs generalised: against the εᵣ = 1 reduction, where the kernel
is exact and only the quadrature can be wrong (L8c reached 5.0e-6); charge conservation per D5; and
reciprocity of `Z`, which is structural if the fill is written the way L8c's is.

**A warning that has now cost this area five milestones: check the oracle before concluding the method is
wrong.** L8a records two occasions, L7b-b a third, the L8e phase gate a fourth, L9a a fifth. When a rung
disagrees, **the first hypothesis is the rung.**

---

## 5. What must NOT be built here

- **Ports on more than one level, references, finite ground pours, extractors, `.cem`, anything in
  `src/Ui`** — **L9d**. In particular `PlanarExtractor`'s ungrounded-stack refusal is not narrowed here;
  L9b measured *whether* it can be (yes for an equal-density open bottom, never for a denser one) and
  narrowing it is L9d's.
- **Adaptive frequency sampling, ACA/MLFMM, N-budget enforcement, the refusal audit, L9's phase gate** —
  **L9e**. Report the N and the cost; do not act on them.
- **A gate on published multilayer reference data** — §0.2 item 5.
- **Any widening of `Dcim.ValidatedRhoOverLambda`, `Dcim.ValidatedRhoOverLambdaLayered`, `Dcim.CanFit`, or
  any existing refusal string** on the grounds that a new case is inconvenient. New cases get new,
  separately measured refusals — that is what L9b did rather than loosening L8a's.
- **A conformal or diagonal boundary cell.** `PlanarCell`'s doc comment reserves room for one and L8b
  explicitly did not build it; it is not this slice's either.
- **A general complex eigensolver, or any new package** — D8.
- **A losslessness check.** An open stratified structure with vias radiates more, not less.
- **A new starter technology.** Hand-built stacks in `tests/Engine.Tests/Mom/Support/LayerStacks.cs`, in SI
  units with every parameter written out — `MmicTwoLevel` was built at L9a for exactly this slice (100 µm
  GaAs plus a 3 µm spacer, so two metal levels sit at z = 100 µm and z = 103 µm).

---

## 6. Milestones, each with its own gate

| | content | gate |
|---|---|---|
| **M1** | The dyadic components, derived (D1) | **Tier 1** — the εᵣ = 1 reduction, with the image SIGN right for both orientations — and **Tier 0** |
| **M2** | The interior and cross-region inverse transform (D3) | **Tier 3**, and the oracle checked before anything is concluded from it |
| **M3** | DCIM for the interior and cross-region height pairings (D4) | **Tier 4** and **Tier 5**, per pairing, written down before anything is claimed |
| **M4** | The via basis, junction continuity, and the problem type (D2, D5, D6) | **Tier 6**, charge conservation as an exact number |
| **M5** | The fill extension, the N report, and the cost (D7) | R-via-1 bit-identical on the horizontal-only path; the cost measured, not projected |

**M1 is the one with a wrong obvious answer** — that the vertical component is the horizontal one with the
TE line swapped for the TM one, and that the image sign is the same. **M2 is the research item and the
reason §0.2 item 6 names a fault line.** M3 is where L9b's four-family result either pays off or does not.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  SpectralGreens.cs      + the vertical and mixed components on LayeredSpectralGreens (D1).
                           The one-layer members and the horizontal components are untouched.
  SommerfeldIntegral.cs  + interior and cross-region support: a complex-k FreeSpace, the third
                           extraction, the k₀ breakpoints (D3)
  LayeredMedium.cs       + the interior branch of LayeredStaticGreens, for Tier 4
  Dcim.cs                + the interior (four-family) and cross-region fits (D4); the horizontal
                           top-half-space path is bit-identical (R-via-1)
  PlanarProblem.cs       + LayerStack in place of GroundedSlab, per-level z, via geometry (D6)
  PlanarBasisFunctions.cs+ the attachment mode (D2)
  PlanarMesh.cs          + vias in the mesh; the CELL ordering contract is unchanged (R-msh-2)
  PlanarFill.cs          + the vertical and mixed blocks; L8c's per-cell scalar matrix is reused
  SurfaceMesher.cs         probably a small change — it is already multi-layer (§0.2 item 1)

tests/Engine.Tests/Mom/
  Support/LayerStacks.cs        MmicTwoLevel is the two-level fixture; add more only if measured
  GeneralLayeredMediumTests.cs  L9a's ladder — EXTEND, loosen nothing
  LayeredDcimTests.cs           L9b's ladder — EXTEND, loosen nothing
  PlanarFillTests.cs            L8c's ladder — EXTEND, loosen nothing
  VerticalCurrentTests.cs     + Tiers 0-6 for the z-directed path
```

Nothing outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/` should change. If something does, that is a
finding worth reporting, not a step to take quietly.

---

## 8. Five things to report back on, whatever else happens

1. **How many kernel components there are, which transmission line each comes from, and the εᵣ = 1
   reduction that proves them** — including the image sign for both orientations. This is the deliverable
   of M1 and everything else stands on it.

2. **The R-via-4 curve as numbers**, per component and per height pairing (low–low, low–high, high–high),
   scaled and strict relative, over the same ρ/λ span and the same stacks L9b used — **and where it stops
   being trustworthy in each variable.** Compare to L9b's ≤ 1.6e-2 and say whether the vertical components
   are worse, by how much, and whether L9b's near-field floor and its two structural refusals still bound
   the answer or need companions.

3. **Whether the cross-region case is a shift in some variable, or genuinely two-variable** — and what it
   costs either way. This is the question §10.2's original warning was actually about, and L9b narrowed it
   to exactly this one case.

4. **Whether junction continuity holds exactly or only to a tolerance**, with the numbers, and which of
   D2's two constructions produced that. If it is only a tolerance, say what the tolerance costs in the
   assembled matrix and at ω → 0, because that is where an unconserved charge shows up first.

5. **N and the cost for a real two-level structure**, against L8b's one-level N = 552 for §10.7's hero and
   up to 2,055 for a library PCell, against R17's 5,000 ceiling, and against L8d's measured 7.66 s per
   de-embedded point. If a 101-point sweep lands in hours rather than minutes, that is the finding, and it
   is what decides whether L9e's adaptive frequency sampling and its N-budget enforcement are optional.
