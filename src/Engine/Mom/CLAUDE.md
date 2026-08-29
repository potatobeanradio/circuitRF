# `src/Engine/Mom` — the EM kernels (quasi-static **A** and planar full-wave **B**)

Standing instructions for both MoM kernels. Read with the root `CLAUDE.md` and
`src/Engine/CLAUDE.md`. Design note **`docs/design/mom-engine.md`** (this was `layout-view.md` §10
until 2026-08-24; the section numbers are unchanged, so every "§10.x" pointer still resolves — only
the file moved). The Ui half is `src/Ui/Layout/Em/CLAUDE.md`; the user-facing page is
`docs/user/src/reference/mom-engine.md` and must not contradict either.

**This is the engine half only.** No UI, no `.clay` reading, no `.snp` on disk, no dialog.

> ### Where the history went
> This file was an append-only phase log that reached **5,047 lines**. The full text is preserved
> verbatim at **`src/Engine/Mom/HISTORY.md`** — every section heading, number and derivation, with
> line numbers unchanged. **98 pointers in `docs/sonnet-briefs/` and `docs/design/` say things like
> "read `src/Engine/Mom/CLAUDE.md` §L8a end to end" — those mean the section of `HISTORY.md`.**
> Grep it (`grep -n "§L9c" src/Engine/Mom/HISTORY.md`) rather than reading it whole.
>
> | Phase | HISTORY.md lines |
> |---|---|
> | Conventions, architecture, meshing, RLGC, oracles | 1–366 |
> | L7b, L7b-b — modal decomposition | 367–644 |
> | L8a layered Green's fn · L8b mesher · L8c fill · L8d ports · L8e registry | 645–1834 |
> | L9a general medium · L9b DCIM · L9c z-current & vias · L9d multilevel ports · L9e adaptive sweep | 1835–3508 |
> | Via z-integral · ground-via attachment basis | 3509–3918 |
> | G_A^zz accuracy ceiling · curved-edge negative result | 3919–4306 |
> | Conformal cut cells · convex decomposition · edge attractor · mesh frequency | 4307–5047 |
> | M1/M2 parallelism · calibration-standard fill-two · M5 AIM decision gate + build (archived 2026-08-13, the maintenance rule below having lapsed once already) | 5048–5515 |
>
> **Maintenance rule, or this regrows — and it already did once.** A completed phase appends its
> narrative to `HISTORY.md`, not here. This file only *changes* — a new invariant, a moved default, a
> new refusal, a trap that now has a name. If a phase adds nothing that is true of the code tomorrow,
> it adds nothing here.
> not here. This file only *changes* — a new invariant, a moved default, a new refusal, a trap that
> now has a name. If a phase adds nothing that is true of the code tomorrow, it adds nothing here.

---

## 0. Orientation — two kernels, one output contract

| | **Kernel A** — `QuasiStaticKernel` | **Kernel B** — `PlanarKernel` |
|---|---|---|
| Physics | 2D quasi-static per-unit-length RLGC → S | 2.5D full-wave MPIE surface MoM |
| Input | `EmProblem` (cross-section) | `PlanarProblem` (layout + stackup) — a **sibling type, not a subtype** |
| Diagnostics group | `"tline"` | `"planar"` |
| Cost | ~1000× cheaper (`EmKernelRegistry.CheaperByRoughly`) | dense fill dominates |

`EmKernelRegistry` is keyed on the **analysis kind** and unifies the **output** (`EmKernelOutcome`
= kind, kernel name, `DataSet`, `EmSuitability`, notes; every outcome's `Reason` names the kernel),
never the input. `Choose(requested, crossSectionVerdict, planarVerdict)` takes extractor **verdicts,
never geometry** — both extractors live behind the UI firewall in `src/Ui/Layout/Em/`.
`RequiredCapability(kind)` / `Describe(kind)` map kind ↔ `EmCapabilities` both ways.

**Do not put planar diagnostics in `"tline"`** — a Data Display trace would silently mix
quasi-static and de-embedded full-wave per-unit-length claims.

---

## 1. Invariants that hold everywhere here

- **R-mom-2. SI doubles throughout**: metres, S/m, radians, Hz. DBU stops at the Ui-side extractor.
- **R-mom-1. The kernels consume neutral types defined here.** `LayoutFragment` / `Stackup` /
  `Technology` are `src/Ui` types; taking them inverts `Ui → Engine → Core → RfCore` and breaks
  `tests/Firewall.Tests`.
- **R-mom-14 / R-gen-2. Form `Z`, then `RFNetwork.ZToS`.** Never write a second ABCD→S.
- **Reciprocity and passivity are gates; losslessness is NOT** — and is checked nowhere in this
  directory, on purpose. An open planar structure radiates and launches surface waves, so
  |S₁₁|² + |S₂₁|² < 1 legitimately. (Kernel A's losslessness oracle does not carry to kernel B.)
- **`EmConstants.Eps0` is derived** as `1/(µ₀c²)`, so `µ₀ε₀ = 1/c²` to the last bit.

---

## 2. Kernel A — quasi-static (`EmProblem` → RLGC → S)

### 2.1 Sign and frame conventions — the part that silently goes wrong

Segment frame (`Kernel2D`), for **a**→**b**, length `L`, `û = (b−a)/L`, **left** normal
`n̂ = (−û_y, û_x)`; `x = (p−a)·û`, `y = (p−a)·n̂`, `r₁ = hypot(x,y)`, `r₂ = hypot(x−L,y)`:

```
F(u)   = u·ln(u² + y²) − 2u + 2y·atan(u/y)
Φ      = ½·[F(L−x) − F(−x)] = ∫₀ᴸ ln r ds
P      = −Φ / (2πε₀)
P_self = L·(1 − ln(L/2)) / (2πε₀)          (collocation at own midpoint)
∂Φ/∂y  = atan((L−x)/y) + atan(x/y)          ∂Φ/∂x = ln(r₁/r₂)
E      = (σ/2πε₀)·(∂Φ/∂x·û + ∂Φ/∂y·n̂)      — returned in WORLD coordinates
```

`Field` is invariant under reversing a↔b; callers never reason about winding.

- **R-mom-5. `F_ii = 0`.** The self-field term is excluded — the segment's own subtended angle π is
  the σ/(2ε₀) self-field the dielectric row already carries analytically. Violating it double-counts
  and the solve converges *smoothly* to a wrong number.
- **Dielectric row (`ChargeSolver`)**: with `n̂` from region 1 into region 2 ("up" for the mesher's
  horizontal interfaces), `σ_b = 2ε₀·K·E_n^avg`, `K = (ε₁ − ε₂)/(ε₁ + ε₂)`, ε₁ **behind** the
  normal. Total induced bound charge for a half-space is `−Kλ`.
- **ε_r charge weighting.** Solved σ is the *equivalent free-space* density, so
  `Q_k = Σ_{i∈k} ε_r,i · σ_i · Δ_i`, **not** `Σ σ_i Δ_i`. `EmSegment.EpsOutside` is the region the
  surface's **outward** normal points into; a conductor straddling an interface carries both values.
  Get it wrong and a filled coax reports the air value.
- **R-mom-6. Loss is a complex permittivity**, and it costs one solve: `ε* = ε_r(1 − j·tanδ)` ⇒
  complex `K` ⇒ `[C] = C′ − jC″`, and `Y = jωC` ⇒ **`G = −ω·Im(C)`, `C = Re(C)`**. Any number of
  independently lossy dielectrics. Do **not** add a separate partial-capacitance accumulation.
- **R-mom-7. The ground plane is an exact image, never meshed.** Every source segment contributes
  its mirror about `y = Y_g` with negated charge, for both `P` and `E`. An interface coincident with
  the ground plane is dropped (noted in the report).
- **Maxwell sign convention on `[C]`: off-diagonals are NEGATIVE.** A "mutual capacitance" (positive
  off-diagonal) matrix swaps even and odd modes and *both answers look physical*. Pinned by `Z_o < Z_e`.
- **Port map (D3): port `2k−1` is conductor *k*'s NEAR end, `2k` its FAR end.** Stated in
  `EmProblemBuilders.CoupledMicrostrip`, `CrossSectionExtractor`, `RlgcToSparams`. Never re-derive
  it; transposing it swaps through and coupled ports, invisible in a magnitude plot.
- **R-mom-11. `[C]`, `[C₀]` and `∂L/∂n` are frequency-independent — computed once per sweep.**
  Enforced by `RlgcModel.MatrixFillCount = 2 + N + (ground ? 1 : 0)` (exactly **4** for a single
  line, 5 for a pair), asserted for both 3-point and 1001-point sweeps.
- **Two arithmetic associations are load-bearing to the ulp** in `RMatrix`/`Derivative`:
  `rs / µ₀ * DLdn` must not be re-associated or hoisted, and the finite difference must be
  `(a − b) / Δ`, not `(a − b) * (1/Δ)`. Tier-3 oracles carry tolerances and cannot catch a 1-ulp move.
- **R-gen-1. `ModalDecomposition.Symmetrise` is a PRECONDITION of the GEVD**, not a tidy-up —
  NumFlat reads only the upper triangle and does not check, and point collocation does not produce a
  symmetric `[C]`.

### 2.2 Architecture

| File | Owns |
|---|---|
| `EmProblem.cs` | `EmPoint`, `EmMaterial`, `EmDielectricRegion`, `EmConductor`, `EmGroundPlane`, `EmPort`, `EmProblem`, `EmConstants` |
| `IEmKernel.cs` | `IEmKernel`, `EmCapabilities`, `EmSuitability` |
| `EmMesh.cs` | `EmSegment`, `EmMesh`, `ConductorMeshTemplate` |
| `EmMeshSettings.cs` | the six mesh controls |
| `EmMeshReport.cs` | segments, counts, min/max cell, truncation extent, Wheeler crossover, notes |
| `Polygon2D.cs` | winding, containment, `HorizontalFootprint`, `OffsetInward`, self-intersection |
| `BoundaryMesher.cs` | perimeters, interfaces, edge grading, truncation, R-mom-9 exclusion, `EdgeReference`, `GeometricSlopeFor` |
| `Kernel2D.cs` | the closed forms + the R-mom-7 image |
| `ChargeSolver.cs` | assembly, NumFlat LU, one factorisation for M excitations |
| `RlgcExtractor.cs` | `[C]`, `[C₀]`, `[L]`, `[G]`, Wheeler `[R]`, `LossSurface.DLdn`, `RdcPerM` |
| `ModalDecomposition.cs` | `DecomposeGeneral`, `EvaluateAt`, `Symmetrise`, `CheckGeometricSymmetry`, `GeneralModes.TryIdentifyEvenOdd`, `ModalR` |
| `RlgcToSparams.cs` | γ, Z_c, Z-matrix, `ModalBlock`, `BuildGeneral`, `RFNetwork.ZToS`, DataSet assembly |
| `QuasiStaticKernel.cs` | `IEmKernel` impl; `CanSolve`; `MaxSignalConductors` |

- **`ChargeSolver` takes an `EmMesh`, not an `EmProblem`** — the physics is stated over segments,
  which is what lets cylindrical-interface oracles exist at all.
- **There is deliberately no kernel registry inside kernel A** (one kernel, constructed directly).
- `RlgcToSparams.BuildCoupledPair` is **deleted**; the L7b `[1 1; 1 −1]` closed form survives only as
  `tests/Engine.Tests/Mom/Support/L7bSymmetricPairOracle.cs`, as an oracle.

### 2.3 Meshing, RLGC, and modal — live numbers

- `[L] = µ₀ε₀[C₀]⁻¹` (TEM identity, no second formulation). `ε_eff = C/C₀`.
- **R-mom-8. Edge grading is a cell-size field over attractor points**, geometric, from both ends of
  every conductor face, applied also to dielectric-interface segments near a conductor edge:
  `h(x) = min over attractors a of [ c₀ + (r−1)·|x − a| ]`, clamped. Equivalent to `c₀rᵏ` with
  `d_k = c₀(rᵏ−1)/(r−1)`.
- **`BoundaryMesher.EdgeReference` = 3% of the conductor's SMALLEST bounding-box dimension**, not
  its width — the charge singularity's scale is the metal corner, i.e. thickness for a rolled foil.
  (This is the trap that cost L6/L7 real time; kernel B's default is different — see §3.3.)
- **Interface cells have two scales**: out to `2h` the cap is `h/MinCellsAcrossWidth`; beyond `2h`
  the cap grows geometrically so the tail costs exactly `TruncationTailCells` cells.
- **R-mom-9. Interface segments are excluded wherever the interface lies inside *or on* a
  conductor.** Two unknowns on one physical surface make the matrix singular.
- **R-mom-10. Truncation distance is an explicit setting**, reported as
  `EmMeshReport.TruncationHalfExtent`; doubling it moves Z₀ < 0.5%.
- **Wheeler**: `R(ω) = Σ_k (R_s,k(ω)/µ₀)·(∂L/∂n)_k`, `R_s = √(ωµ₀/2σ) = 1/(σδ)`.
  **R-mom-12: `∂L/∂n` is purely geometric** — one finite-difference recession, frequency enters only
  through `R_s`. Two surfaces recede: signal conductors inward (`Polygon2D.OffsetInward`) and the
  ground plane downward; **both `∂L/∂n` are positive** (a negative one is a bug). `Δ = min(t, W)/50`,
  capped at half the smallest mesh cell; halving it moves R < 1%. The perturbed geometry is re-meshed
  from the *same* `ConductorMeshTemplate`.
- **R-mom-13. `R = √(R_dc² + R_wheeler²)`** — a smooth interpolation, labelled as such, not physics.
  The DC floor is applied **diagonal-only**. Wheeler-invalid frequency `f(δ = t/2) = 4/(π·t²·µ₀·σ)`
  (~14 MHz for 35 µm copper) goes in the report.
- **Line → S**: `Z = R + jωL`, `Y = jωC = G + jωC`, `γ = √(ZY)` with `Re(γ) ≥ 0`, `Zc = √(Z/Y)`,
  `Z11 = Z22 = Zc·coth(γℓ)`, `Z12 = Z21 = Zc/sinh(γℓ)`.
- **General modal (Route A)**: `Gevd(Re[C], [L]⁻¹)`, λ = 1/v_p², both sides real symmetric definite;
  loss carried perturbatively (form the full lossy modal matrices, keep only the diagonals).
- **R-gen-3a. `Ti` normalisation is the trap**: `Ti_m = Tib_m / ‖Tib_m‖²` where `Tib = (Tvᵀ)⁻¹`.
  Without it the published `Zc_m` comes out as the mode's **phase velocity** (≈1.8e8), not ohms,
  while terminal S stays correct. `Ti⁻¹ = diag(1/e)·Tvᵀ`, computed directly, never by a second
  numerical inversion.
- **R-gen-7. Mode order: λ ascending** (relative tolerance), tie-broken by the eigenvector's
  largest-magnitude conductor index, then raw LAPACK index. For a microstrip pair this puts the
  **ODD** mode at index 0. Mode identity is stable across a sweep by construction (`Tv` comes from
  the ω-free lossless problem). Nothing downstream depends on order —
  `GeneralModes.TryIdentifyEvenOdd` reads the sign pattern.
- **R-gen-8. `"tline"` group cubes are rank-2 `[freq, mode]`**: `Zc`, `Gamma`, `Eeff`,
  `AttenDbPerM`, `Rpul`, `Lpul`, `Gpul`, `Cpul`, plus `ModeCouplingResidual` over `[freq]`.
  `…Even`/`…Odd` survive as **aliases for N = 2 only**, sliced from the same arrays, identified from
  `Tv`'s sign pattern; ambiguous pattern ⇒ no alias, N ≥ 3 ⇒ none. No new result type.
- Blocks are assembled as `Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]` so `Z[i,j]` and `Z[j,i]` are
  **bit-identical**; `S` is symmetric only to `ZToS`'s own tolerance (<1e-12).
- **`QuasiStaticKernel.MaxSignalConductors = 16.`** FR-4, RLGC only: N=2 → 206 unknowns / 0.04 s;
  N=8 → 680 / 1.0 s; N=16 → 1,312 / 9.1 s. Cost is the dense BEM solve, not the modal step.
- **`DiagonalAsymmetry` warning threshold: 2%** ("refine the mesh").
- **Kirschning–Jansen dispersion** is an opt-in `QuasiStaticKernel` constructor flag, **off by
  default**, single-microstrip only (`TryMicrostripDispersion` returns null otherwise).

### 2.4 Kernel A traps

- **Asking R-cpl-8 of the solved `[C]` instead of the geometry.** `CheckGeometricSymmetry` uses
  conductor *outlines*. Tell: a mirror-symmetric pair is refused as "asymmetric" (a 1 µm × 1.4 mm
  strip reads 6.8% diagonal asymmetry at default settings, 0.99% at `Refined(4)`) when it only needs
  a finer mesh.
- **Treating `C₁₂ − C₂₁` as a bug.** Point collocation on a piecewise-constant basis is not
  symmetric; the residual is a discretisation-error indicator (0.554% default / 0.146% `Refined(2)`
  on a realistic edge-coupled pair). It comes from the **MESHER** (grading + interface segments), not
  from collocation — identical uniformly-meshed circles give zero to round-off. `Symmetrise`
  symmetrises while `RlgcModel.AsymmetryResidual` reports the raw value in `Notes`.
- **Handing complex `[Z][Y]` to `MatrixDecompositions.Evd`.** NumFlat's complex EVD is Hermitian-only,
  reads only the upper triangle, and returns **real** eigenvalues. Tell: smooth, plausible γ² with no
  imaginary part on a lossy line.
- **Trusting `ModeCouplingResidual` as an accuracy bound.** It is *anti-correlated* with terminal
  error in frequency (residual ~0.36 / error ~5e-5 at 100 kHz; residual ~2e-4 / error ~5e-4 at
  20 GHz). A diagnostic of what was discarded, not a predictor.
- **Omitting the ground-plane `∂L/∂n` term.** Tell: microstrip conductor loss noticeably under-reported.
- **"Fixing" the solver toward Hammerstad-Jensen on a thick strip.** H-J models thickness by
  *widening* W (ε_eff rises); a BEM solve sees the side faces in air (ε_eff falls). Divergence scales
  with t/W (−1.4% at 0.02, −4.7% at 0.22 on FR-4, ~1.8× that on GaAs) and collapses as t → 0.
- **Comparing conductor loss to `MicrostripLoss.ConductorLossNpPerM` as a level gate below
  W/h ≈ 20** — it is a wide-strip asymptote (Wheeler/crude measures 0.40 at W/h = 0.3 rising to 0.96
  at 50).
- **Reinstating L7b's forced `[1 1; 1 −1]` on accuracy grounds.** On a discretised mesh the general
  path is ~3 orders closer to exact (8e-6 vs 8.9e-3 at default settings).
- **Comparing kernel A against kernel B with an absurdly thin strip.** A's edge reference is a
  fraction of metal **thickness**, so t = 1 nm degenerates its mesh (+4.5%). Use t = 1 µm.

---

## 3. Kernel B — planar full-wave (`PlanarProblem` → S)

### 3.1 The layered Green's function and DCIM

- **Formulation: MPIE, Michalski-Zheng formulation C**, derived in `SpectralGreens`'s header:
  `G̃_A = V_i^h/(jωµ₀)`, `G̃_q = jωε₀(V_i^e − V_i^h)/k_ρ²`. Vector kernel purely TE; scalar kernel
  free of k_x/k_y.
- `Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)`, with
  `Γ^e − Γ^h = 2jT k_ρ²(εᵣ−1)/[(jk_z1T + εᵣk_z0)(jk_z0T + k_z1)]` so k_ρ² cancels **exactly**.
  `Γ^q(0) ≠ Γ^e(0)`. k₀ is the only branch point (one-layer); coefficients are written in
  `tan(k_z1h)/k_z1`, never `tan` alone.
- **Four kernel components** — `GreensKernel` is a **flat enum**, direction structure lives in the
  fill: `VectorPotential` G_A^xx (TE, from `V_i^h`) · `ScalarPotential` G_q (`V_i^e − V_i^h`) ·
  `VerticalVectorPotential` G_A^zz (series-source current `I_v`, TM **and** TE) ·
  `MixedVectorPotential` (shunt-source current `I_i`).
- **PEC image signs**: G_A^xx and G_q take a **negative** image, **G_A^zz a POSITIVE one** (the
  *current* reflection at a short is +1), mixed ≡ 0.
- `G_A^uz(z,z′) = −G_A^zu(z′,z)`; the ẑx̂ block's oddness in x−x′ supplies the second sign, so `Z` is
  symmetric **structurally**.
- **Surface-wave poles** by bisection on `u tan u = εᵣ√(U²−u²)` (TM_n, on (nπ, nπ+π/2)) and
  `u cot u = −√(U²−u²)` (TE_n, on ((2n−1)π/2, nπ)), `U = k₀h√(εᵣ−1)`, then a secant to the complex
  root. Residues by circular contour average in `w = k_ρ²`. **A grounded slab always carries TM₀ (no
  cutoff)**, verified to h = 1 µm at 100 MHz. FR-4 1.6 mm: TE₁ at **25.404 GHz**, TM₁ at twice that.
- **General stack** (`LayeredMedium.cs`, `LayeredSpectralGreens`, `SurfaceWavePoles`): layer order
  **bottom-to-top, z increases upward**; z = 0 bottom termination, `TopZ` top; regions 0 = bottom,
  1..N finite, N+1 = top. **A point ON an interface belongs to the region ABOVE it.** `Termination`
  = PEC / PMC / half-space. Möbius ladder per layer, every interface coefficient cross-multiplied.
  `SpectralGreens.LineResponse` yields all four TL Green's functions from **one** cascade traversal
  (dual line: Z↔Y, Γ negated, transmission ratio reciprocated).
- **`BranchPointOrders` default 1** — the only order that is a *theorem*: the far-field sum rule
  `Σ A_i = −(1 + Γ(∞))`, exact for any layer count with an open top, imposed by eliminating one
  amplitude (residual 1e-16). Orders 0/2/3 reachable, better only for G_A.
- `Dcim.FitCore` is the shared body; the one-layer and general paths differ in exactly three inputs
  (reflection function, k₀/k_top², reference height). The shipped one-layer path is **bit-identical**
  and asserted as exact equality.
- **Height pairs in the top half-space are an exact SHIFT** (`DcimModel.Evaluate(ρ, z, z′)`): same
  amplitudes, depth `b_i + Σ`, poles scaled by `e^{−αΣ}`. Do not fit per height pair. Interior
  same-region is an exact shift of four families in `k_zm`; **cross-region is not a shift at all**.
- **Height dependence at fixed k_ρ and region pair spans exactly four products**
  `e^{∓jk_zm z′}e^{∓jk_zn z}`. It is a *span*, **not** a fittable variable — LOW–HIGH has no single one.
- `Bessel.cs` — J₀/J₁/Y₀/Y₁/H₀⁽²⁾/H₁⁽²⁾ for complex argument, **written from the defining series**,
  no package added. Series below |z| = 13, Hankel asymptotic above. J 2.9e-13 / Y 5.6e-11.

### 3.2 The mesher (`SurfaceMesher`, `PlanarMesh`, `PlanarMeshReport`)

- **Tensor-product grid**, spacing derived **per axis** from the narrowest run along that axis.
  A gridline comes from an **axis-parallel boundary edge, never from a vertex** — a Manhattan
  polygon tiles exactly and a 194-vertex smooth outline contributes only its two end caps.
- Cell order is `(LayerIndex, IY, IX)` compared as integers; `cellAt` is a plain `int[]`.
  **R-via-5: all horizontal bases precede all vertical ones**; vertical order is (via as given, IY, IX).
- **N counts BASIS FUNCTIONS, not cells** (shared internal edges, ≈2× cells) — R17 is about N.
  Reporting cells where N is wanted is a factor-of-2 error in the one number the mesher produces.
- Edge grading reuses `BoundaryMesher.PartitionFractions` / `GeometricSlopeFor`. **Reference length
  is the CONDUCTOR WIDTH** (`PlanarEdgeReference` default — note this differs from kernel A's
  smallest-dimension rule). Growth ratio is *derived*, `r = (h_max/c₀)^(1/EdgeCells)`, never fixed
  at 1.7. `EdgeFractionOfReference` = 0.03, `EdgeGrowthRatio` = 1.7.
- The mesh is **exactly translation-invariant** — anchored to the artwork, not the world origin.
- **Edge attractor**: an axis-parallel boundary edge earns a graded fan if it is ≥ **0.2 × the
  polygon's own extent** across it **OR** both its corners are convex (it *terminates* the
  conductor). An **OR**, so nothing that graded before can stop. Floor **0.02 × extent**
  (`CapMinFractionOfExtent`, derived: R17's ~5,000 unknowns ≈ 2,500 cells ≈ a 50×50 grid ⇒ one cell
  is ~2% of the extent at the finest affordable mesh).
- **Cell-size cap**: `cellSize = λ_g(f_mesh)/N = c/(f_mesh·√ε·N)` — depends only on the **product**
  `MeshFrequencyHz × CellsPerWavelength`. Effective cells/λ at the sweep top is
  `CellsPerWavelength × MeshFrequencyHz / sweepTop`, a physical quantity, never hertz.
- **Conformal cut cells** (`PlanarCellRegion`, off by default — see §4): a cut cell is a **LIST of
  convex pieces**, not a half-plane; a whole rectangle is a **null** region, not a 4-vertex piece.
  `Contains` uses a half-plane test on a convex piece and a ray cast otherwise; `Area`/`Centroid` are
  signed-area formulas valid for any simple polygon. `RooftopSupport` measures the ramp from the
  metal's own boundary as weighted strips (`FlowSimple`, `IsFlowSimple`, `Tiles` builds X and falls
  back to Y). `PlanarMeshReport.MinCellEdgeM`/`MaxCellEdgeM` report the **GRID's** extents, never the
  cut region's.
- Via footprints are emitted as **hard gridlines** and get **no** conductor-rim edge grading.

### 3.3 The fill (`PlanarFill`, `RectangleIntegrals`, `SingularExtraction`, `PolygonIntegrals`)

- Rooftop basis (`PlanarBasisFunctions`), normalised to **unit total current across the shared
  edge**; divergence is ±1/Area pulses. **A via is a rooftop in z** — same divergence pulse, uniform
  current over the footprint.
- `RectangleIntegrals` — six closed-form inner integrals over a rectangle for an in-plane point
  (`∫∫dS/R`, `∫∫ln r dS`, `∫∫r dS` and their three first moments), built from a **corner primitive**
  so interior/edge/corner are one case. `PolygonIntegrals.CoresXY` generalises the same six onto a
  convex piece; its edge reduction needs neither convexity nor the point being inside.
- **Quadrature keyed on τ = centroid separation ÷ larger cell diagonal.** Self: 10-pt Gauss/axis on
  **4×4 Chebyshev-clustered** panels (`t_k = ½(1−cos(πk/p))`); τ<1.6: 10-pt on 3×3 clustered;
  τ<4: 5-pt, 1 panel; τ≥4: 3-pt, 1 panel. **The inner integral is closed form in every row.**
  Remainder quadrature **8 / 4 / 2 points per axis** (near/mid/far), on **both** inner and outer.
- Remainder by subtraction with a ρ floor of **1e-8 × smallest cell**; below it the analytic limit.
- **Three singular pieces**, and the second is the one that gets missed: `1/ρ`; a real `ln ρ`
  **per surface-wave pole** with coefficient **−Residue/2π**; the image sum (smooth only if `b_i` is
  not small — "the images are smooth" is FALSE on the FR-4 starter). Extraction order default **1**
  (0 and 2 reachable; order 2 costs the `∫∫r` and `∫∫u·r` cores).
- **Multi-level fill** (`FillMultiLevel`), three blocks: scalar (generalised, geometric cores
  reused); ẑẑ (the scalar block's cell-pair integral with G_A^zz × ℓ_mℓ_n); ẑx̂/ẑŷ — **the only new
  machinery** — integrates ∂G/∂x, odd in x−x′, direct graded quadrature. `PlanarFillCores.DirPos`
  caches direction cores.
- **The via z-integral is a closed-form split, not a quadrature.** `ViaZIntegral.cs` — prism core,
  z-averaged terms, mixed derivative; `RectangleIntegrals.Corner0AtOffset` / `InverseAtOffset` are
  the lifted rectangle closed form. `ViaZNodes = 2` (bounded half's Gauss rule in z; **1 would be
  the midpoint rule this exists to undo**), `ViaZStaticNodes = 10` (singular half's t-rule, per panel).
- Every vertical basis's current flows **+z** (no per-basis direction factor in the ẑẑ block).
- **The fill is ~3 decades more accurate than the kernel**: 5.0e-6 against the εᵣ=1 reduction,
  5.4e-3 worst against direct Sommerfeld — which is the kernel's own ≤6e-3. **Chasing the quadrature
  further is wasted work.** On a cut mesh: non-self 1.5e-12…1.1e-11, scalar self 1.35e-6, vector self
  2.34e-6, non-convex cells 1.46e-6 — gates at 5e-6, because the reference rule's own pulse-self
  residual is 3.3e-7. Shipped reference rule: **(grading levels 3, outer 8, inner 24)**.

### 3.4 Ports, excitation, solve, de-embedding

- `PlanarPort` (type, resolution, refusals) · `PlanarExcitation` (`B`, multi-RHS,
  `Y = BᵀZ⁻¹B`, raw S, line current) · `PlanarCalibration` (`BuildLine`, `SuggestDeltas`,
  `SuggestLengths`, `SameCrossSection`; γ from ½·tr(M)) · `PlanarDeembed` (error box, branch
  resolutions, P-port peel, `C_pul`, `Z_c`) · `PlanarSolve` (per-frequency kernel cache,
  `PlanarPortCalibrator`, sweep driver, `VerticalRangeVerdict`).
- **Port cut**: reference plane = the shared edge of the two outermost cells, **one cell in** from
  the drawn metal; the half-cell beyond is error box. **Nothing user-positionable.** Ground reference
  is always the slab's ground plane. `PlanarPort.LayerIndex` is `int?` — null infers **one** candidate
  or refuses by name.
- **`PlanarPortKind` — TWO port types, the same object cut in two places.** `Edge` is the above.
  `InternalDeltaGap` cuts an INTERIOR gridline of a conductor (metal both sides), nearest the placed
  point, and reports `GapOffsetM` — how far the snap moved it, bounded by half a cell. Same basis,
  same incidence matrix, same `Y = BᵀZ⁻¹B`. **An internal port is NOT de-embedded and cannot be**:
  there is no feed outside an interior cut, so no error box, no standard and no `Z_c` — it takes
  `PlanarSolve.IdentityBox` and `Z_c = Z₀`, which is arithmetic that provably changes nothing (gated
  at < 1e-12 against `Deembed = false`). It grows no feed lead and raises no clearance warning, and
  its DIRECTION is required rather than inferred (a label mid-conductor is equidistant from all four
  edges). **A delta gap is a SERIES source: a centred gap gives S₁₃ = −S₂₃, measured to 16 digits.**
  `PlanarPortResolution.IsDeembeddable` is the one place the two kinds are distinguished downstream.
- **R-fed-1. The solver grows its own calibration feed** (`PlanarFeedExtension`, 2026-08-12), before
  meshing, by extruding each port's own polygon outward from its drawn end face by whatever uniform
  line the calibration is short of. **The user never adds a feed line to their artwork** — §10.6 now
  states that as a requirement. A feed that is already uniform grows nothing and the problem reaches
  the mesher **by reference**, which is what keeps every number in `HISTORY.md` reproducible;
  `Assert.Same` is the gate, because a vertex-count assertion would drift. **Running out of metal
  counts as uniform** — a short line is a SHORT structure, not a flared one, and `EndRunCellsFor`
  already clamps for it. Uniformity tolerance is **0.1% of the port width**, tight because the
  quantity it protects is amplified by 1/a₂₁². Everything it is not sure of is DECLINED (end face not
  a single straight segment, ambiguous level, lead would hit other metal) — a decline is the
  pre-existing behaviour plus the pre-existing warning.
- **R-fed-2. The lead is peeled EXACTLY, before renormalisation**: `S_ij *= exp(γ_iℓ_i + γ_jℓ_j)`,
  with γ the value the two-line calibration measured **for that same cross-section**, and ℓ measured
  from the resolutions as `|ReferencePlaneM − DrawnEdgeM|` (the lead **minus the outermost cell**, which
  the error box already owns). "Matched" means matched in `Z_c`, so peeling after `Renormalise` puts
  back a reflection that was never there. A port that grew nothing is **bit-identical** — no `exp(0)`.
- **R-prt-15. σ_max(S) ≤ 1 is checked on the answer that ships**, per frequency, against a **uniform
  real** reference (per-port Z₀ may differ and may be complex; σ_max is reference-dependent, so asking
  it of the published matrix flags passive networks). A note carrying the worst value and its
  frequency, tolerance 1e-3 — not a refusal, which would discard a whole sweep for one point.
- **Two standards per port cross-section**; the count for a band is derived by `SuggestDeltas` (one
  separation covers a usable 8:1, designed to **4:1** for margin). `TargetElectricalDegrees = 60`.
  `SuggestLengths` returns a **separation**, never a second absolute length. `SameCrossSection`
  tolerance **1e-12**. Default feed length **3 substrate heights**.
- One `Dcim.Fit` per (slab, frequency) serves the DUT and every standard; one geometric core per
  mesh. A two-level structure with a via costs **9 fits per frequency**; a calibration standard adds
  **zero**. D7's fit-count bound is arithmetic: `6 + n_z(n_z+1)/2 + n_z·levels` (13 on the two-level
  fixture), **independent of N**.
- `PlanarAdaptiveSweep` — `PlanarSolveSettings.Adaptive` default **null**; `SolveRawAt`/`DeembedAt`
  are shared local functions so OFF is bit-identical. Default interpolant **`CubicSpline`**
  (measured winner); `Rational` (Floater-Hormann, d = 3) reachable. The refinement criterion must be
  an **ERROR** (solved midpoint vs interpolant prediction), never a residual.
- `PlanarCurrentDensity` — `I_x(cell) = ½·Σ I_b` over covering x-rooftops; `J = I / transverse
  extent` (**`Area / Width`**, not `Height`); `Iz` is a CURRENT in amperes with its own normalisation
  and caption, and `Magnitude` is Jx/Jy only. One excitation, one frequency: no superposition, no sweep.

### 3.5 Kernel B traps

**Green's function / DCIM**
- Implement `Γ^q` from the naive difference and every digit is gone by k_ρ = 1e-8·k₀ (0.82 abs
  error) — exactly where DCIM sampling starts.
- **Do not use `SpectralGreens.ProperRoot` on a contour.** It negates its own result whenever
  `Im(w) < 0`, so it flips sign on half of any circle about the origin. Tell: reflection coefficients
  perfect to 2e-16 while Γ^q's small-k_ρ limit is ~5% wrong.
- **Never write an interface coefficient as `(Z_b − Z_a)/(Z_b + Z_a)`** — 0/0 or ∞/∞ at k_z = 0. Use
  the cross-multiplied form; a zero denominator means identical media (reflection exactly 0), not an
  error. Tell: NaN at k_ρ = k₀ in the εᵣ = 1 reduction.
- **Assemble G̃_q from Γ^q, not from `V^e − V^h`, whenever both points are in the open top
  half-space** (the voltage route amplifies rounding by `|Z|/|V^e−V^h|`); the voltage route is for
  INTERIOR heights only.
- **`w = k_ρ²` is even in k_z0 and cannot serve `Dcim.BranchPointTaylor`**, which differentiates
  through k_z0 = 0. The two k_z0 signs are **different analytic functions** near |w| → 0 — extract
  `ReflectionTaylor` per branch.
- **`LayeredSpectralGreens.ScalarKernel`'s top-half-space fast path assumes an AIR top**; a non-air
  top must fall through to the voltage route (was 4.4× wrong).
- Start the pole search with Newton and it converges onto the neighbouring mode — the fit then holds
  two copies of one pole. Tell: mode count disagrees with the cutoff conditions.
- **Do not reuse the one-layer residue-contour radius** in the general case; a contour enclosing a
  neighbouring pole returns a smooth, plausible, wrong far field. General radius is
  `0.05 · min` over every region's `|k_i² − w_p|` **and** every other pole.
- **`DcimModel.FitResidual` does not predict spatial error** (GaAs's best spectral residual has an
  8e-2 far-field error). Only the oracle answers that.
- Deform the Sommerfeld contour off the real axis and J₀ grows like `e^{|Im z|}` — 0.2k₀ at ρ = 10λ
  turns an O(1) answer into a difference of 3e5-sized terms.
- `StaticGreens` with a **real** εᵣ sits a frequency-independent 1.1e-6 off, which reads exactly like
  a convergence floor. The image constant K must be complex.
- A numerical ∂_z step must be bounded by the distance to the nearest interface and by
  `1/max(k_ρ, k_max)`, **never by the thinnest layer**.

**Fill / mesh**
- `double.LogP1` is **not** a true log1p — use `RectangleIntegrals.Log1P`. Tell: ~1e-9 error in the
  log first moment on a high-aspect-ratio cell.
- A raw corner-summed antiderivative divides by zero when the observation point lands on a
  gridline — routine on Manhattan artwork.
- **Cross-level entries have no 1/ρ but still carry logarithms** (surface wave, plus the mixed
  kernel's 1/k_ρ² tail when Σ_b = 0). "Different levels ⇒ smooth ⇒ plain quadrature" pushes a log
  through Gauss-Legendre.
- **`PlanarKernelSet.For()` must share the fit cache**; a copied cache refits every pairing *per
  mesh* and no answer anywhere looks wrong.
- **`FillMultiLevel` must look cores up through `DirPos`**; re-integrating `PairCores` per entry is
  invisible on one mesh and crippling once single-level standards exist.
- **A via footprint must contribute hard gridlines** or the via vanishes with zero vertical unknowns
  and no error; and it must **not** get rim grading (2,448 unknowns vs 424 on the same fixture).
- **`ViaZNodes = 0`** yields an empty quadrature set ⇒ ẑẑ block zero ⇒ vias stop conducting, with
  complete, smooth, wrong s-parameters. Caught only by `PlanarFillSettings.Validate()`.
- **A ground attachment's lower index is −1** (`PlanarVia.GroundTerminal`): any
  `problem.LevelZ(via.LowerLayerIndex)` throws — take `InterfaceZ[0]`. Engine-side tests use
  hand-built problems that never reach `PlanarKernel.CanSolve`.
- **L9c's D5 (`∫∇·f dS = 0`) does NOT hold for a ground attachment** — `NetCharge` is exactly −1,
  balanced by the image. Adding a compensating pulse double-counts the image.
- **Tabulate the REMAINDER, not the kernel.** `_full` still diverges as 1/ρ (and ln ρ from poles)
  after static asymptotes; subtract `Extracted` first and hand back `table + Extracted`. Tell: worst
  error at self and touching cell pairs.
- **Total N and minimum cell both lie about rim response.** Every shipping PCell responds to
  `EdgeCells` in N and its min cell collapses ~8×, from axis-parallel END-CAP fans. The honest
  quantity is transverse spacing at the rim point farthest from any axis-parallel edge.
- `RooftopSupport.Tiles`' direction looks free (unit weight) but is the divergence pulse's own
  domain; strips across the wrong axis span a gap in the metal.
- `MergedSliverCount` counts **absorptions, not hosts** — 32 merges into 28 hosts; the counts should
  not match.
- **Naming a remedy in a refusal or a note without asking whether it BINDS** (owner report,
  2026-08-14). `hx`/`hy` are `Math.Min(hWave, narrow/MinCellsAcrossConductor)`, and on any part with a
  wide-to-narrow width ratio the second term wins by orders of magnitude — so "lower Cells per
  wavelength" and "lower Mesh frequency" change **nothing**, and a user who follows them halves a knob,
  sees the identical unknown count, and stops. Measured on a 6.9 → 100 Ω Klopfenstein taper: **7,749
  unknowns at 5, 10 and 20 cells/λ alike**, and at f_mesh of 500 MHz and 5 GHz alike. `BuildRefusal`
  now asks `waveBinds` and offers only remedies that act on the binding quantity, saying outright when
  the frequency knobs do not. **The same defect was live in a NOTE**: the below-sweep-top mesh-frequency
  note computes its effective cells/λ from the CAP, so where the cap does not bind it stated
  "the cells are λ_g/1" about cells that were λ_g/1120, and recommended the two inert knobs. Both are
  gated by `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs`.
- **Building a diagnostic and then throwing it away.** R17's refusal left `PlanarKernel.Solve` as a
  bare `InvalidOperationException`, so `PlanarMeshReport.Notes` — the narrowest conductor and how many
  cells it got, whether the edge mesh acted on anything, R-msh-8a's note — were assembled and dropped,
  and the caller reported the whole thing as an *engine error*. It is a **refusal** and it now carries
  its report (`PlanarMeshRefusedException`).

**Ports / solve**
- **Treating "the feed is not uniform at the plane" as an accuracy note.** It is not: D6's diagonal is
  `(S_meas,ii − a₁₁)/a₂₁²`, a cancellation of two numbers within 1e-4 of unity divided by ~1e-4, so a
  **0.1% error in the error box is a 10× error in the answer**. Measured on the owner's 2000 mil
  50 → 12 Ω MKlopf on 0.508 mm RO4350B: `|S₁₁| = 1.0000`, `|S₂₁| = 0.0008`, σ_max = 1.06 — a
  non-passive open circuit. R-fed-1 exists because of this, and R-prt-15 exists because it shipped.
- **A small taper on a coarse mesh does NOT reproduce it, so do not size a gate that way.** The
  amplification is set by a₂₁, and a₂₁ is set by the OUTERMOST CELL: with `EdgeMesh` off that cell is
  a bulk cell and 1/a₂₁² is a few hundred; with edge grading on (the default) it is 3% of the width
  and 1/a₂₁² reaches 1e4. Several 12–20 mm tapers at 1–5 GHz measured σ_max ≈ 0.99 either way.
- **`CheckFeedClearance` had no upper bound on `along`** (fixed 2026-08-12) — it computed the distance
  along the feed and then only used it to skip cells BEHIND the port, so `nearest` was the smallest
  lateral gap anywhere on the board. It fired on every part ever wider than its port, could not be
  cleared by any amount of feed, and was therefore the one warning nobody read. It is what R-fed-1
  **cannot** fix: a lead lengthens a feed, it cannot move a neighbour sideways.
- Excite and read back with the **same `B`**; a side-dependent sign gives a hard π in S₂₁, invisible
  in a magnitude plot.
- **Do not negate γ on `Re γ < 0`** — it flips β too, and α's extracted sign is noise. **β selects
  the branch**; Re is a tiebreak only when there is no prediction. `a₂₂`'s sign comes from the
  redundant M₁₁ equation; `a₂₁`'s cancels only for **identical** ports.
- A calibration standard must be built from the DUT's own port resolution (`BuildLine`), never
  re-meshed; reusing a plain-line calibration on a stepped DUT moved the answer 1.8e-1.
- **`PlanarPortCalibrator` is stateful and must be stepped in increasing frequency order** (the
  driver sorts). Under adaptive insertion, cache raw matrices per frequency and **replay** — predicting
  βΔℓ from the pre-solve ε_eff runs 15–20% low and coin-flips the 2π branch.
- On FR-4 above ~5 GHz the lever is the **remainder quadrature**, not the extraction order (orders 0
  and 2 agree to 1e-4 while both are 5% out).
- **The two ports of a plain microstrip do not share a calibration** on the shipping mesh (end
  grading is not exactly mirror-symmetric), doubling the standards.
- **Do not test the current-density map as `Σ J·w == I_port`** — an outermost cell correctly carries
  HALF its neighbour's current; the identity is against the mean of the two adjacent EDGE currents.
- Port level inference is one candidate or a refusal; picking the lowest silently drives a different
  conductor with the same footprint.
- `PlanarCurrentDensity.Compute` must switch **explicitly** on direction — an `if (X) … else …`
  counts a vertical basis as Y. An attachment names one cell twice and must not be counted twice.

---

## 4. User-facing controls and defaults

| Setting | Default | Notes |
|---|---|---|
| `PlanarMeshSettings.CellsPerWavelength` | 20 | with mesh frequency, only the **product** matters |
| `PlanarMeshSettings.MinCellsAcrossConductor` | — | sets transverse pitch; **does not respond to λ** |
| `PlanarMeshSettings.EdgeCells` | — | reference length = conductor width |
| **`PlanarMeshSettings.PlanarBoundaryCells`** | **`Staircase` — conformal SHIPS OFF** | the fourth control, added on explicit instruction. All four flip gates now pass; it stays off only so every accuracy figure recorded in `HISTORY.md` stays reproducible. **Flipping the default is a separate deliberate act.** |
| **`PlanarMeshSettings.MeshFrequencyHz`** | **`null` = the sweep's top** | bit-identical to prior behaviour. No refusal — the measurement supports a note, not a floor. |
| **`PlanarSolveSettings.MaxDegreeOfParallelism`** | **`null` = automatic** | M1. ONE number for both levels of parallelism; enters **no** provenance hash (R-emp-7) because it can change no answer (R-emp-8). `1` means strictly in order. |
| `PlanarSolveSettings.Adaptive` | `null` (off) | OFF is bit-identical |
| `PlanarSolveSettings.CurrentDensityPortNumber` / `…FrequencyHz` | null | captured during the existing sweep, no second factorisation |
| `PlanarFillSettings.DirectVerticalKernel` | `false` | ẑẑ from `SommerfeldIntegral.EvaluateInterior`; skips the ρ/λ refusal and says so |
| **`PlanarFillSettings.Aim`** | **`null` = OFF, but REACHABLE from the panel since 2026-08-14** | `EmSetup.AcceleratedSolve`, persisted in the `.cem`. **It MOVES the ceiling** (`brief-em-aim-ceiling.md`, 2026-08-14) — see `SurfaceMesher.AcceleratedUnknownCeiling` below — on a single-level mesh; a multi-level/via problem is refused by name regardless (`PlanarAimGeometry.Build`, P6 — `PlanarAimOperator.Build` before), so the effective ceiling there is still the dense one. The refusal names turning it on as the first remedy whenever doing so would let the mesh run. |
| **`PlanarFillSettings.UseSymmetricFactorization`** | **`true` = P7's in-place complex-symmetric LDLᵀ** | Z is complex-symmetric bit for bit, so the dense solve is an `SymmetricFactorization`, not an LU: half the arithmetic, parallel over the trailing update under the SAME one cap the fill spends, and written INTO the matrix — `PlanarSystem.Matrix` throws after `Factor()`. `false` restores NumFlat's general LU, kept reachable as the oracle exactly as `UseRadialTable = false` is. **Unpivoted, which is standard for MoM matrices and is not a theorem** — the gate is a residual, and `SymmetricFactorization.GrowthFactor` / `SmallestPivotRatio` are computed on every factorisation at no cost. |
| **`PlanarFillSettings.TrackFactorizationResidual`** | **`false`** | Keeps a copy of Z so every solve reports `‖Zx − b‖/‖b‖` on `PlanarSystem.LastResidual`. That copy is a whole N×N matrix — the memory P7 removed — so it is a diagnostic, never a default. |
| `PlanarFillDiagnostics` / `ConformalDiagnostics` | `null` | instruments; fill is bit-identical with them attached |
| `SurfaceMesher.PlanarRimGrading` | `None` | a **measurement seam, not a user control** — do not promote it |
| `DefaultSliverAreaFraction` | 0.05 | conservative, on a plateau (0.005…0.05 identical at cells/λ = 130); **no conditioning cliff was located** |
| `PlanarLevels.MaxElectricalLength` | **0.30** | bounds the **BASIS** (one z-rooftop per gap ⇒ uniform current), not the quadrature |
| `SurfaceMesher.UnknownCeiling` | **5,000** | R17's per-mesh N ceiling for the DENSE path, checked in three places. **A COMPILE-TIME CONSTANT, not a probe of the machine** — say so when it refuses; the megabytes it quotes read as a RAM limit and a 2026-08-14 owner report asked exactly that. **Still 5,000 after P7, and the reason it did not move has changed**: the memory argument behind it is largely gone (one point now holds 527 MB at the ceiling, not 1,290 — the same 1 GB would buy **N ≈ 6,968**), but nothing has re-measured the FILL time or the accuracy of a mesh that size, and moving it is an owner decision the brief explicitly reserves. `HISTORY.md` §P7 carries the sentence that would change. |
| **`SurfaceMesher.AcceleratedUnknownCeiling`** | **12,000** | R17's ceiling for the ACCELERATED solve, single-level meshes only (`brief-em-aim-ceiling.md`, 2026-08-14) — see §8's own AIM paragraph and `HISTORY.md` §12's closing subsection for the measurement. **A de-embedded run's calibration-standard capacitance step is NOT accelerated** (`PlanarDeembed.StaticCapacitance` is always dense, out of this brief's scope) and can still refuse on a wide port even when the DUT's own solve would succeed — see that subsection's own limitation note. |
| `QuasiStaticKernel` K-J dispersion | off | opt-in ctor flag, single microstrip only |

- **`MaxLengthOverWidth` is RETIRED** (with `PlanarKernel.NarrowestViaFootprint`). Retiring it
  **widened nothing** — `ValidatedRhoOverLambdaAtHeights` still restricts every via run.
- `.cem` follows the omit-at-default rule (a pre-phase file gains no byte); one undo entry; commits
  on selection; calls `InvalidateMesh()`. **`Resolved` carries both new fields through `Auto`** —
  Auto has no opinion about boundary model or mesh frequency.
- **`PlanarProblem.MaxFrequencyHz` still means the SWEEP's top only**, and that separation is
  load-bearing: pointing `CanSolve`'s via bound, the ρ/λ note or the geometry hash at the mesh
  frequency would let a **performance** knob silently widen a **physics** refusal. `CanSolve` sees
  only a `PlanarProblem`. `EmSnpProvenance.MeshHash` includes `BoundaryCells`.

---

## 5. Validated ranges and accuracy ceilings

| Constant | Value | Governs |
|---|---|---|
| `Dcim.ValidatedRhoOverLambda` | 1.0 | one-layer top-half-space; ≤6e-3 scaled, ≤1e-2 strict to ρ/λ ≈ 1 |
| `Dcim.ValidatedRhoOverLambdaLayered` | 1.0 | general stack; ≤**1.6e-2** (≈2.6× worse than one-layer, stated not rounded away) |
| `Dcim.ValidatedRhoOverLambdaAtHeights` | **0.1** | **G_A^zz only** — the binding limit on every via-bearing run. Asked of the **via-footprint extent**, not the mesh diagonal. **Do not widen it.** |
| `Dcim.ValidatedRhoOverLambdaInteriorHorizontal` | 1.0 | a **note, never a refusal** |
| `GroundedSlab.MinElectricalThickness` | k₀H ≥ 1e-6 | kernel limit |
| `MinUngroundedElectricalThickness` | 0.05 (k₀H) | bracketed by 0.021 failing / 0.105 passing; **mechanism NOT isolated**, and the refusal says so |
| `Dcim.CanFitAtFrequency` | k₀H < 1e-6, or `PathExtent·k₀H < 1` | near-DC refusal, asked of the sweep's lowest point |
| near-field floor | ρ/λ = `1/(2π·PathExtent)` = 5.3e-4 at `PathExtent = 300` | derived, stack-independent |

- `Dcim.WithinValidatedRange` is reported by `PlanarSolve` as a **note, never a refusal**.
- **De-embedding accuracy is limited by RADIATION and surface-wave coupling between ports, not the
  algebra**: exact (8.5e-16) at the two calibrated lengths, ~3.9e-4 at 2 GHz and ~6.0e-3 at 10 GHz
  elsewhere on 1.6 mm FR-4, scaling ≈ f². A longer feed does not fix it (feed-length sensitivity is
  ~1e-2, the same order as the residual).
- **Low-frequency floor**: the port is necessarily a **series** delta-gap, so `a₂₁ ∝ ω` and the peel
  divides by `a₂₁²` — ~22× error amplification at 2 GHz, growing as f⁻². A true edge port would
  remove it and is not built (the basis is forbidden).
- **`Z_c = γ/(jωC_pul)` holds C at its static value** (`PlanarKernel.QuasiStaticNote` states it
  once): +0.4% at 1 GHz, +2.3% at 5 GHz, +6.3% at 20 GHz vs kernel A; −9.6% vs Kirschning-Jansen at
  20 GHz, where neither is authoritative. A dispersive C needs a field integral this kernel lacks.
- **Fit accuracy is not uniform in Σ = z + z′.** `|e^{−jk_z0Σ}| = e^{−k_ρΣ}` kills the evanescent
  spectrum: G_A on a grounded stack improves with height, G_q on a thick low-εᵣ substrate degrades
  ~20× at Σ/λ = 0.30.
- **`PathExtent` is in units of k₀ while a stack's image structure lives at `k_ρ ~ 1/H`**, so
  `300·k₀H` decides whether the fit sees the stack; below ~300 MHz on a 1.4 mm stack the error grows
  as frequency falls. Recorded, not acted on.
- The conductor-width edge reference sits **~0.35% low** on static capacitance; kept because the
  cell-size alternative measures N = 7,562 on an ordinary GaAs line, over R17's ceiling.
- **Conductor loss is not modelled** — the sheet is PEC; `PlanarConductorLayer.SigmaSm`/`ThicknessM`
  are carried and unused. α_c is 6.5 / 3.0 / 2.1% of conducted loss at 2 / 10 / 20 GHz.
- **Staircase error is NOT monotone in cell size.** Local width error on MTaper/MKlopf is 17–24%
  worst, 5.5–11% RMS, against 0.47–0.59% global *area* error. Conformal tiling error vs the drawn
  artwork is 7.7e-16…6.5e-15; staircase is 0.096–0.593%.
- **Mesh frequency** (FR-4 hero 1–20 GHz, cells/λ = 20): 20 GHz N = 1,345 / 352.4 s (reference);
  **10 GHz N = 552 / 127.8 s, worst |ΔS| 2.97e-3 below and 1.50e-2 above**; 5 GHz N = 348 / 103.6 s,
  1.58e-1 above. **Halving is defensible, quartering is not.** Error concentrates ABOVE f_mesh.

---

## 6. Do not retry — measured negative results

- **Cross-frequency parallelism (M3), and the whole "just parallelise the sweep" family.** Its
  premise is that a de-embedded point leaves cores idle for another frequency to use.
  **Measured false.** One fill scales **5.3× on a 10-core box** and the whole de-embedded point
  scales **5.4×** — a point that scales as well as its own fill has no serial fraction left to
  overlap. Reduced to its essence: four independent frequency-shaped units run concurrently under
  one budget against one after another each using the whole machine is **1.09× / 1.15×** on two
  runs. **That is M3's entire ceiling**, before its cost — R-emp-12's 1.3–3.6 GB of concurrent
  working set, and making `_rawCache`, `SolveCount` and the branch state thread-safe in a shipped
  solver. **The fall-off is HARDWARE, not scheduling:** fill efficiency is 98% at 2 cores and 81% at
  4, then 74% at 6 and 53% at 10; solving for an Amdahl serial fraction gives 5.9% / 3.3% / 9.6% at
  caps 2 / 4 / 10 — not one number, so it is core heterogeneity or memory bandwidth, and running
  more fills at once cannot invent either. **P3 (2026-08-29) struck "memory bandwidth":** writing
  the contiguous triangle instead of the strided one left the 256 mm fill's serial time unchanged
  (70.9 → 71.3 s) and its fall-off unmoved; efficiency is 89–95% at cap 4 on every fixture and drops
  only when the cap admits the box's 6 efficiency cores beside its 4 performance cores — every
  cap-10 speedup solves 4 + 6f to f ≈ 0.2–0.4. There is no serial fraction; the extra cores are
  slow ones. `HISTORY.md` §P3. `CrossFrequencyParallelismTests` keeps both measurements.
  **What would change the answer:** a machine whose fill scales near-linearly to a high core count
  (then there is still nothing to overlap), or a fill that turns out to be limited by a genuine
  serial PHASE (then fix that phase — it is a smaller change and it helps every run, including
  single-frequency ones). Neither is this box.
- **Rim / edge grading on curved geometry.** A graded fan on a **staircased** rim cannot help:
  quantisation error depends on how the grid *aligns* with an oblique edge, not only on fineness, and
  that artifact (0.669% band) is larger than anything the fan changes. `PerRunSampled` is measurably
  worse. Asserted as a negative in `E3`. The real fix is conformal cells.
- **DCIM knob tuning for `G_A^zz`.** `BranchPointOrders`, `BranchSamples`, `BranchExtent`,
  `FitTolerance` are **structurally inert** — `Dcim.FitAtHeights` re-references to the source
  region's own k_m and never reads them, and the interior kernel is finite at its own branch point so
  there is no branch-point Taylor sampling. Bit-identical over 30 configs. The reachable knobs trade
  the wrong way (23× worse inside ρ/λ ≤ 0.1). Per-component `DcimSettings` plumbing was therefore
  not built.
- **Amplitude-conditioning cap on the DCIM fit / depth set** — measured worse (14 → 39 on GaAs
  low–low at 1e4; every candidate rejected at 1e2). Diagnosis kept, cap not shipped.
- **Widening `ValidatedRhoOverLambdaAtHeights` on the 4.53e-7 fitted-vs-direct result** — one layout
  on one stack; FR-4 low–low is 1.1e-1 at ρ/λ = 1 where GaAs low–low is 14.
- **A plain Gauss rule in z for the via.** Structural: at Δ ≪ cell neither the 8-node cell quadrature
  nor the 2%-of-a-cell radial table resolves the ρ-structure, and a discrete rule keeps a
  `Σ w_a²·C/ρ` the exact integral lacks.
- **The z-segment chain (M2)** — 14.2% of a de-embedded point at n = 8 to move the answer
  0.077–0.141%. `ViaPhysicsTests.ChainVias` is the construction if ever needed.
- **The four-fit spatial recombination** — refuted by derivation (basis functions are functions of
  k_ρ); measured 8.8e-4, inside the fit's own envelope, so not contradicted.
- **ACA** — far-field blocks need 53–62% of min(m,n) rank at 1e-3 even with a full pivot; blocks
  under R17's ceiling are not many wavelengths apart, and a compressed matrix needs an iterative solver.
- **GPOF / any general complex eigensolver** for a better depth search — declined (D8).
- **Route B, a general non-symmetric complex eigensolver** (kernel A) — Route A's error is 4.9e-4
  |ΔS| on a realistic asymmetric copper pair, two orders below the `[C]` solve's own ≤1.3%.
- **Route B, an actual convex decomposition** — of 1,158 refused cells, 1,152 were flow-simple in
  both directions, 6 in one, **0 in neither**: the residue is empty.
- **`PlanarPairOracle` extended to a cut support** — its cross-correlation identity needs a
  **separable** weight, and a cut cell's ramp is affine in both coordinates at once. The replacement
  is a direct 4-D quadrature with `(x − x₀) = |d|·sinh w`.
- **Refusing on `ValidatedRhoOverLambdaInteriorHorizontal`** — nothing was measured past ρ/λ = 1;
  refusing would invent a limit and reject structures accepted today.
- **Calling the mesh frequency a quadratic saving** — N falls only 2.44× for a 2× drop, because
  `MinCellsAcrossConductor` sets the transverse pitch. And **on a narrow conductor, lowering the mesh
  frequency RAISES N** (2 mm × 72 µm GaAs: 773 / 705 / **2,014** at 20 / 10 / 5 GHz) — the edge fan
  must bridge a wider gap. **Never add a UI hint that a lower mesh frequency is cheaper.**

### Oracle traps (this area has burned nine oracles — check the oracle first)

- Measuring a **polygonal** fixture against the true smooth shape measures the FIXTURE. Tell: the
  error is flat to four figures at every rung — a 96-gon's deficit is exactly `(2π/96)²/6` = 7.138e-4.
- Compare the fit against the **converged** direct block; against a fresh 128-sample table it reads
  8.67e-5, which is the table's own resolution, overstating the fit's error by two decades.
- Running the sliver sweep at a density that produces no sliver reads "the threshold barely matters"
  for the wrong reason. Use cells/λ = 130 / 250.
- Sharing one `nodes` parameter between the outer and inner quadrature makes the outer rule look like
  it is drifting; it is converged at 8 nodes once the inner is resolved.
- Disc fixture vertices must sit **ON** the axes — half-step-offset vertices make axis-straddling
  edges axis-parallel to 1e-12, splitting the ring into four runs (16 attractors instead of 4).
- The Sommerfeld oracle requires a **lossy** slab (tanδ > 0): a lossless one puts TM₀ exactly on the
  contour. `Dcim` has no such restriction.
- **`LayerStacks.OpenBelow` is DEGENERATE for second-branch-point questions** (alumina in air,
  k_b = k₀, both branch points coincide). Use `FilmOnSilicon`.
- Do not canonicalise cross-region height order to force bit-identity — the two chains are
  independent implementations agreeing to 2.7e-13, and **that agreement is the evidence**.

---

## 7. Refused by name / not built

**Structural obstructions (a remedy is named, not built):**
- **An open-below stack with a DENSER bottom cannot be fitted.** `k_zb` carries a second branch cut
  at `k_z0 = ±j·k₀√(εᵣµᵣ−1)`, in the half-plane the sampling path runs into; DCIM's exponential sum
  in k_z0 is entire and **cannot carry a cut**. `Dcim.CanFit(LayerStack)` refuses by name. Equal-
  density bottoms are fine.
- **Interior sources are refused by name** by `DcimModel.Evaluate(ρ,z,z′)`,
  `SommerfeldIntegral.EvaluateLayered` and `LayeredStaticGreens`. *(`DcimModel.Evaluate`'s refusal
  string still says buried metal arrives with L9c — stale; the interior fit is on `FitAtHeights`.)*
- **All of Part B** — the interior electrostatic Green's function and `C_pul` at a buried level.
  `PlanarSolve`'s buried-level refusal is byte-identical. **The most valuable remaining work here.**
- **A port on a buried level de-embeds nothing** — `PlanarDeembed`'s `C_pul` is a grounded-slab image
  series and the de-embedded S is *referenced* to the Z_c it yields (`LevelIsOnSlabTop`).
- **A port ON a via** — a different object: a vertical basis has no end in the layout plane and no
  cell beyond the cut. **A port on a cut cell** is likewise refused: its reference plane is a shared
  edge whose transverse extent is no longer the grid's.
- **A calibration standard containing a via** — the two-line algebra models a uniform matched
  section. A standard also carries **no cut cell** (`BuildLine` never runs the conformal pass).
- `CanSolve` refuses: a level not on any interface, levels not bottom-to-top, a via skipping a level.
- **Three per-cell conformal configurations fall back to the staircase for that cell**: >1 polygon of
  the layer touches it; a hole ring touches it; flow-simple in **neither** direction (measured at
  zero everywhere).

**The binding limit right now:** `R-cut-4`'s `Anchored` test is all-or-nothing over a support's
strips, so a shallow oblique rim declines a whole rooftop — **17.3% of the feed's width at
cells/λ = 20**, 11.4% at 40, 5.0% at 80. Accepting a nearly-swept support (0.994 A instead of
1.000 A) is deliberately **not** taken: it retires L8c's exact `∫f·û dℓ = 1 A` and needs its own
s-parameter measurement.

**Out of scope, deliberately:** stripline (needs an infinite image series, not one image);
frequency-dependent modal matrices; inhomogeneous-medium modal theory beyond quasi-TEM; surface
roughness (absent from the model rather than accepted and ignored); a sloped or vertical dielectric
boundary (outside the 2.5D premise — "layered" means N **horizontal** layers); triangles/RWG and a
Delaunay triangulator; coplanar or differential ground reference;
differential/multi-mode ports; a via with more than one degree of freedom in z (subdivide across
intermediate levels instead).

**Open items on the record:** R-gv-8's ω → 0 capacitance gate against `PlanarStaticLimitTests` — not
run. §11's L9 gate sentence — a proposal to strike "agreement with published reference structures" is
on the record, **owner's ruling pending**. ~~`SurfaceMesher.UnknownCeiling`'s message quotes 381 MB
against a real run cost of ~607 MB at the ceiling — owner's call.~~ **CLOSED at P1
(brief-em-p1-honest-memory-accounting.md, 2026-08-29): 607 was itself an underestimate. One dense
frequency point holds 3.52× the 16·N² all three refusals quoted — a flat, structural ratio measured
at N = 552, 1,980 and 4,836 alike — because NumFlat keeps `L` and `U` as two SEPARATE full matrices
beside the one `PlanarSystem` holds, and the cached cores add another half again. All three refusals
now quote `PlanarSystem.ResidentBytes` (1,338 MB at the ceiling, not 381) through one shared
function, so they cannot drift apart. The ceiling CONSTANT is untouched — that is P7's decision.
Tables in `HISTORY.md` §P1. **P2 (2026-08-29) then took ~24% off the cores term** — one of the three
O(N²) vector triangles held an outer product of an O(N) vector — so the flat ratio is now **3.39×**
and the refusals quote **1,290 MB** at the ceiling. `HISTORY.md` §P2. **P7 (2026-08-29) took both
FACTOR matrices out**: the complex-symmetric LDLᵀ overwrites Z, so the flat ratio is **1.39×** and
the refusals quote **527 MB**. R17 was re-asked and NOT acted on — 1 GB now buys N = 6,968 against
4,454, so memory no longer binds at 5,000, but nothing has re-measured the fill's time or a mesh's
accuracy at that size and moving the constant stays the owner's decision. `HISTORY.md` §P7 carries
the sentence that would change, written out and not applied.** Of
`brief-em-sweep-performance`: **M0–M2 are done** (§9), **the calibration standards' own saving is
done** (§10 — 1.52× off a wide-port point, bit-identically), **M3 is a MEASURED DEFERRAL** (§6 — its
premise was tested before it was built and its ceiling is 1.09–1.15×), **M4 is not started**, and
**M5 IS BUILT** (§11 is its decision gate, §12 is the accelerator and R-emp-16's two accuracy gates).
**M5 ships OFF and its win is MEMORY rather than time** — the near field is genuinely O(N)
(290 → 392 entries per row over 12× N) and GMRES stays flat (2 → 6 iterations) on the accelerated
product, but the FILL time falls far more slowly than the entry count, because AIM's near field keeps
exactly the pairs L8c's singular-extraction machinery makes expensive. Time crossover N ≈ 3,700;
58 MB against 223 MB there. *(Time figures superseded at P4–P6 — see §8's AIM paragraph: the
crossover is now N ≈ 1,100, measured.)* **What M5 owed — measurement above N = 3,731, on a via-bearing or cut
mesh, and a decision on whether R17's ceiling moves — is PAID, by `brief-em-aim-ceiling.md`
(2026-08-14).** The ceiling moves, to **12,000, for the accelerated solve on a single-level mesh
only**; see §8's own AIM paragraph for the number and §12's own closing subsection in `HISTORY.md`
for the two ladders, the conformal check and the calibration-standard limit that decided it.


---

## 8. Performance — current shipped state

- **~~Cost is the fill, not the LU~~ — neither, since P7. Both halves of a dense frequency point are
  now parallel and of the same order** (at N = 4,836, ten cores: a fill of a couple of seconds and a
  factorisation of 3.59 s). The original claim ("114× the LU at N = 552, still 1.8× at N = 4,933")
  was true of L8c and has been overturned twice — by P4/P5 cutting the fill, and by P7 replacing the
  LU.
  > **Corrected at P4, and again at P5 (2026-08-29):** no longer true even of a whole sweep once
  > the core build is amortised over more than a handful of points. P4 cut the per-frequency fill
  > 2.7–3× and P5's translation-class memo a further 2.8–5.8× (and the core build 3.7–41×), so per
  > frequency point the LU dominates from roughly **N = 1,000**: at N = 1,891 the fill is 0.41 s
  > against an LU of ≈ 2.4 s, at N = 3,731 1.25 s against ≈ 18 s (LU scaled by N³ from P1's 42.8 s at
  > N = 4,933, not re-timed). P7 (the factorisation) is where the time is. `HISTORY.md` §P4, §P5.
  > **Built at P7 (2026-08-29), and the whole sentence is retired rather than re-pointed.** There is
  > no LU on the dense path any more: Z is complex-symmetric bit for bit, so
  > `SymmetricFactorization` does an unpivoted blocked LDLᵀ **in place**, in half the arithmetic, with
  > the trailing update parallel over destination columns under the same one cap the fill spends.
  > Measured in Release on the three shipping-mesh rungs: **0.01 / 0.26 / 3.59 s at cap 10** for
  > N = 552 / 1,980 / 4,836, against NumFlat's **0.04 / 1.92 / 41.21 s** — 2.7× / 7.3× / **11.5×**,
  > of which 3.1–3.7× is the cores and the rest is the arithmetic. Neither half of a dense point
  > dominates now. **`dotnet test` builds DEBUG and that inverts this comparison** (139 s / 33.7 s
  > against an LU of 40 s at N = 4,836) because the LU is native and this is not — see §8's own
  > paragraph. `HISTORY.md` §P7.
- **A de-embedded point fills only the TWO calibration standards `GammaBest` actually reads**
  (`PlanarCalibration.SelectSeparation`, computable before any fill), not every standard built for the
  band — 1.23–1.80× faster across a band, smallest saving at the band's bottom (structural: the
  longest standard is always the one selected there). The standard *set* is unchanged; only what gets
  filled is. `StandardSolveCount` counts distinct frequencies; a replay may legitimately need one more
  mesh without it counting as a re-fill. `Mat<T>` is a **struct** — `Mat<Complex>?[]` is
  `Nullable<Mat<T>>[]` and needs `.Value`, not `!`, to unwrap.
- **Parallelism is ONE shared budget** (`PlanarSolveSettings.MaxDegreeOfParallelism`, default
  `null` = automatic), spent by the innermost fill work via a single semaphore — never add a second,
  independent cap. It changes no answer (gated as bit-identity at every cap) and therefore enters no
  provenance hash. The fill's own row-parallelism (present since L8c) is where nearly all the
  multi-core win already was (~5.4× on a 10-core box); fanning independent solves out on top of that
  buys ~1.06–1.18× at best, and effectively **zero in the shipped configuration**. Cross-frequency
  fan-out was measured and deliberately **not built** — see §6.
- **`PlanarFillSettings.Aim` (default `null` = OFF).** An AIM (Adaptive Integral Method) accelerator
  exists but ships disabled — every number elsewhere in this file is still the dense L8c/L8d path.
  Its win is **memory, not time**: ~4× less working set at practical N (crossover ≈ N 900), while the
  *time* crossover is much later (≈ N 3,700) and even past it the saving is only ~1.4× up to R17's
  5,000-unknown ceiling — because AIM's near field keeps exactly the pairs L8c's singular-extraction
  machinery already makes expensive, not the cheap far-field ones an entry-count reduction implies.
  > **Corrected at P6 (2026-08-29): the time half of that sentence is no longer true, and the
  > explanation is only half of why.** The expensive pairs are still the near ones, but their
  > singular quadrature is no longer paid per frequency at all: P5 keys it by translation class and
  > P6 builds the accelerator's frequency-independent state ONCE per mesh (`PlanarAimGeometry` —
  > stencils, near set, and every near pair's singular cores, warmed at build; `PlanarAimOperator`
  > is per frequency over it, holding only the grid kernels, the near entries' remainders, the
  > correction and the sparse LU). Measured, not scaled: one dense point (P5 fill + LU) against one
  > accelerated point is 0.24 / 0.54 / 2.03 s vs 0.37 / 0.71 / 0.96 s at N = 552 / 994 / 1,912, so
  > **the time crossover is N ≈ 1,100** and the accelerated point is ~13× faster at N = 3,731. What
  > is left per frequency is, largest first, the AIM correction itself (43–46% at N ≥ 3,731 —
  > `AimEntry`'s 256 lookups per near pair, restructurable to 49; recorded for P7/P8), the sparse LU
  > and the remainder quadrature. The memory sentence stands: 195.8 MB at N = 11,959, the `double`
  > stencils (−3 MB) covering the class-keyed core store (+2.5 MB); the brief's mirror index was
  > measured at 18 MB there and is NOT held. Counters: `PlanarCoreBuildCounter.AimGeometryTotal`,
  > `PlanarEntryCores.CorePasses`. `HISTORY.md` §P6.
  **These are two different measurements in two different units, and CLAUDE.md used to state them as
  if they were one — corrected 2026-08-14 (brief-em-aim-ceiling.md §3).** §11's decision gate (the
  question of whether an iterative solve converges AT ALL, run before a line of AIM existed) measured
  a near-field radius in MESH CELLS on the dense matrix: **3 cells degrades with N and is beaten by no
  preconditioner at all on a refined mesh; 8 cells is flat (3–6 GMRES iterations across a 6.7× N
  range)**. That settled viability, nothing more. R-emp-17 (§12), on the accelerator that was actually
  built, measured `PlanarAimSettings.NearRadiusFactor` in units of the LARGEST BASIS SUPPORT — a
  different, coarser unit by construction — and its own order × radius table put the shipped default
  at **radius 6 supports**, not 8: order 3 landed `|ΔI|` at 8.7e-7 there, inside L8c's own 5.0e-6 fill
  accuracy, and radius 8 supports bought a further 3.6× for cost that table itself showed was not
  worth spending. **`NearRadiusFactor = 6.0` in code is correct and was never the discrepancy; the
  prose was the bug**, conflating the pre-build viability check's "8" with R-emp-17's shipped "6" as
  though they were the same knob. Shipped defaults where it's turned on: projection order 3,
  auxiliary-grid pitch 0.5 of the largest basis support, near radius 6 supports.
  **The "working set under 200 MB at the ceiling" figure above is P1-corrected and it is now tight**:
  honestly counted at N = 11,959 it is 196.2 MB, and it fits only because P1 releases the near set's
  EXACT entries once the preconditioner has been factored from them
  (`PlanarAimSettings.KeepNearExact`, new, default false — the accelerated product reads the
  CORRECTION, never the exact). With those entries still held, which is what shipped until
  2026-08-29, the same operator holds 269 MB and the sentence was false.
  `PlanarAimReport.ApproximateBytes` is now **`ResidentBytes`** and carries the sparse LU's own
  fill-in, which `PreconditionerNonZeros` never did — that field is the NEAR MATRIX's nnz under a
  name that reads like the factor's; **`FactorNonZeros` is the factor's**. See `HISTORY.md` §P1.
  **Multi-level/via
  meshes are refused by
  name** — a ẑ basis needs a different grid kernel per height pairing, a separate phase. A
  non-converged GMRES throws rather than returning a smooth-but-wrong current distribution. Turning
  it on changes no provenance hash, same reasoning as the parallelism cap above.
- **The ceiling MOVES for the accelerated solve, to `SurfaceMesher.AcceleratedUnknownCeiling` =
  12,000, single-level meshes only** (`brief-em-aim-ceiling.md`, 2026-08-14 — the decision M5 left
  open). Grown from N = 3,731 by two ladder constructions that told two different stories: growing a
  part's LENGTH at the shipping mesh (the construction that matches how a real board gets big — a
  wide-to-narrow taper included) stayed flat to N = 12,894 (near/row 392 → 399, GMRES 6 → 7 iterations,
  accelerator working set 53 → 188 MB); refining the RESOLUTION at a FIXED footprint instead — the
  brief's own trap check for a ladder that changes the mesh's CHARACTER rather than only its size — is
  a genuinely different regime and broke: GMRES climbed 21 → 143 → 372 iterations as cells/λ went
  80 → 100 → 120 (still converging), then FAILED to converge at cells/λ = 140 (N = 13,967). 12,000 sits
  at the healthy construction's own top rung, with margin under the one that failed, and leans on
  `PlanarAimOperator.Solve`'s own non-convergence throw as the backstop for the residual risk an
  over-refined mesh still carries. A CONFORMALLY CUT mesh carries no penalty of its own (measured: 4-5
  GMRES iterations, `|Δcurrent|` 1.6e-6 to 5.5e-5 across N = 1,538 to 2,232), so the ceiling does not
  depend on `PlanarBoundaryCells`. **A de-embedded run's calibration-standard capacitance step is a
  SEPARATE, always-DENSE m×m cell system** (`PlanarDeembed.StaticCapacitance`, out of this brief's
  scope) that can still refuse on a wide port even when the DUT's own accelerated solve would succeed
  — measured on the owner's own reported taper (§0), where the wide port's calibration standard alone
  meshed at N = 6,466. `SurfaceMesher.Mesh`'s own `accelerated` parameter and `PlanarSolveContext`'s
  constructor are the two places this is enforced, alongside the dense path's unchanged three
  (`SurfaceMesher.Mesh`'s verdict, `PlanarSystem.GuardCeiling`, `PlanarFill.cs`'s own copy).

Full derivations, every measured table, and the M3/Jacobi/3-cell negative results behind these
numbers are in `HISTORY.md` §"Sections 8–12" — grep `AIM`, `MaxDegreeOfParallelism`, or
`GammaBest`. The ceiling decision's own two ladders, the conformal check and the calibration-standard
finding are in `HISTORY.md`'s closing AIM subsection — grep `AcceleratedUnknownCeiling`.
