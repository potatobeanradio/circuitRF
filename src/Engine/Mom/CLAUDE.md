# `src/Engine/Mom` — the EM kernels (quasi-static **A** and planar full-wave **B**)

Standing instructions for both MoM kernels. Read with the root `CLAUDE.md` and
`src/Engine/CLAUDE.md`. Design note `docs/design/layout-view.md` §10; the Ui half is
`src/Ui/Layout/Em/CLAUDE.md`.

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
>
> **Maintenance rule, or this regrows.** A completed phase appends its narrative to `HISTORY.md`,
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

**Ports / solve**
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
| `PlanarFillDiagnostics` / `ConformalDiagnostics` | `null` | instruments; fill is bit-identical with them attached |
| `SurfaceMesher.PlanarRimGrading` | `None` | a **measurement seam, not a user control** — do not promote it |
| `DefaultSliverAreaFraction` | 0.05 | conservative, on a plateau (0.005…0.05 identical at cells/λ = 130); **no conditioning cliff was located** |
| `PlanarLevels.MaxElectricalLength` | **0.30** | bounds the **BASIS** (one z-rooftop per gap ⇒ uniform current), not the quadrature |
| `SurfaceMesher.UnknownCeiling` | **5,000** | R17's per-mesh N ceiling, checked in three places |
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
  more fills at once cannot invent either. `CrossFrequencyParallelismTests` keeps both measurements.
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
Delaunay triangulator; coplanar or differential ground reference; internal delta-gap ports;
differential/multi-mode ports; a via with more than one degree of freedom in z (subdivide across
intermediate levels instead).

**Open items on the record:** R-gv-8's ω → 0 capacitance gate against `PlanarStaticLimitTests` — not
run. §11's L9 gate sentence — a proposal to strike "agreement with published reference structures" is
on the record, **owner's ruling pending**. `SurfaceMesher.UnknownCeiling`'s message quotes 381 MB
against a real run cost of ~607 MB at the ceiling — **owner's call**. Of
`brief-em-sweep-performance`: **M0–M2 are done** (§9), **the calibration standards' own saving is
done** (§10 — 1.52× off a wide-port point, bit-identically), **M3 is a MEASURED DEFERRAL** (§6 — its
premise was tested before it was built and its ceiling is 1.09–1.15×), **M4 is not started**, and
**M5 IS BUILT** (§11 is its decision gate, §12 is the accelerator and R-emp-16's two accuracy gates).
**M5 ships OFF and its win is MEMORY rather than time** — the near field is genuinely O(N)
(290 → 392 entries per row over 12× N) and GMRES stays flat (2 → 6 iterations) on the accelerated
product, but the FILL time falls far more slowly than the entry count, because AIM's near field keeps
exactly the pairs L8c's singular-extraction machinery makes expensive. Time crossover N ≈ 3,700;
58 MB against 223 MB there. What M5 still owes is measurement above N = 3,731 and on a via-bearing or
cut mesh, plus a decision on whether R17's ceiling moves.

---

## 8. Cost and the test budget

- **Cost is the fill, not the LU**, right to R17's ceiling (114× the LU at N = 552, still 1.8× at
  N = 4,933). Memory at N = 4,933: 371 MB matrix + **188 MB cached cores (+51%)**.
- Hero (§10.7 FR-4, N = 552): **1.73 s/frequency bare, 7.66 s/frequency de-embedded** — standards
  were 78% of it, at 2.58× the DUT's unknowns, when every standard was filled at every frequency.
  **§10 fills only the two that are read**; on a wide-port taper that is 1.52× off the whole point.
- **General kernel: 71.9 s per de-embedded point at N = 514** (~73 min for 101 points), 9.4× L8d at
  the same N — the per-entry cost of the general kernel, not N. Post-z-integral, 65.5 s.
  Two levels with vias: **149.9 s** per de-embedded point.
- N on the FR-4 hero: **552** one level, 1,104 two levels, 1,140 with a via (2.07×), against 5,000.
- **`tests/Engine.Tests/Mom/` is 50 test files, 22 of which carry `Category=Benchmark` methods.**
  Routine tier here: **589 tests in 4 m 48 s** (M5 added 9 routine, ~2 s, and 5 opt-in, 5.8 min).
  The old claim in this file that "all 169 tests run in ~3 s; none is tagged Benchmark" was stale by
  orders of magnitude. Routine gate is plain `dotnet test` (Benchmark excluded via
  `circuitrf.runsettings`); the opt-in tier is
  `dotnet test --settings circuitrf.benchmark.runsettings`, **never `--filter`** — see the root
  `CLAUDE.md`. Engine.Tests is ~5 min of the routine gate on its own, most of it accumulated here
  (~3 min 24 s at L9e; M0's own mesh-frequency tests and M1/M2's bit-identity gates are the rest).

---

## 9. Parallelism — ONE budget, and what fanning out actually bought

`brief-em-sweep-performance` M1/M2. **Read this before adding a second cap anywhere.**

**The fill was ALWAYS parallel.** `PlanarFill.ForRows` has wrapped every one of its call sites in
`Parallel.For` since L8c and there was no `MaxDegreeOfParallelism` anywhere in the repository, so a
core-count control **on its own can only ever make a run slower** — it caps something that was
already saturating the machine. M1 is the seam; M2 is the first thing worth capping.

**ONE number, spent by the INNERMOST work (R-emp-10).** `PlanarSolveSettings.MaxDegreeOfParallelism`
is materialised once per run as a `PlanarParallelBudget` — a `SemaphoreSlim` handed to every
`PlanarSolveContext` through `PlanarFillSettings.Budget`; a fill-row worker takes one permit for as
long as it participates in a `Parallel.For` and releases it when that loop ends. However many solves
are in flight, the number of threads doing fill arithmetic at any instant is the cap. **A cap on an
outer `Parallel.ForEach` does not bound the inner one** — that is the trap this shape exists to
avoid, and a second independent cap a reader has to multiply in their head is worse than either.

**It changes no answer, and that is asserted rather than argued.** R-fil-11 says the parallelism is
over ROWS of the packed upper triangle with every entry written exactly once;
`ParallelBudgetTests` turns that into **bit-identity** at caps 1 / 2 / unbounded / budget, entry by
entry, and at the sweep level (cap 1 vs cap 8, adaptive on and off). **A tolerance would be the wrong
gate** — two runs agreeing to 1e-12 is exactly what an order-dependent accumulation produces. It
therefore enters **no provenance hash** (R-emp-7, gated Ui-side): marking an `.snp` stale because a
user moved a slider would be a lie.

**What M2 measured, and it is well short of the brief's own 3–4× estimate.** One de-embedded point,
FR-4 hero cross-section at the shipping mesh (N = 722 DUT), 10 cores, `Category=Benchmark`, measured
alone:

| configuration | wall clock | vs. fully serial |
|---|---|---|
| fill serial, solves in order | 122.9 s | — |
| fill serial, **solves fanned out (M2 alone)** | **103.9 s** | **1.18×** |
| shipped (row parallelism + fan-out) | 22.7 s | 5.42× |

(Measured twice on separate runs — 122.9 / 103.9 / 22.7 s and 123.7 / 103.9 / 22.8 s — so L8d's own
"a benchmark sharing a run reads more than twice as slow" warning is not what these numbers are.)

**The 5.42× is overwhelmingly the fill's own row parallelism, which predates this brief.** M2's own
contribution is the 1.18×, and it is small for a reason the design predicted rather than a defect:
fanning out five fills does not make them finish sooner — the fill already saturates the cores and
the arithmetic is unchanged. What the budget buys is the **overlap** of one solve's single-threaded
`PlanarSystem.Lu` with another's fill, and **§8's own first line says the LU is 1/114th of the fill
at N = 552** — so the serial fraction available to overlap is nearly nothing.

**And in the SHIPPED configuration M2 is worth essentially zero — measured, not inferred.** The
pre-M2 shape (solves in order, each fill unbounded) is not reachable through the shipped settings,
because a solve-level cap of 1 also caps the fill, so it was measured with a **temporary local
patch** that skipped the budget: both configurations timed in ONE process, alternating, best of two.

| | no fan-out | fan-out | ratio |
|---|---|---|---|
| N = 722 (1.5 λ line) | 25.7 s | 24.1 s | **1.06×** |
| N = 1,810 (4 λ line) | 33.2 s | 33.3 s | **1.00×** |

**Take the interleaved numbers, not a cross-process pair.** A first attempt compared two separate
`dotnet test` invocations and read the fan-out as 7% SLOWER; running both in one process with the
same warm state reversed the sign. The difference is at the level of process-to-process variation,
which is exactly the point: **M2 neither helps nor hurts the shipped path at these N.**

**Do not retry the brief's own sanctioned alternative** — capping each fill at `cap / solves` so no
worker blocks on a permit, instead of the shared `SemaphoreSlim`. Measured at N = 722: **37.7 s
against 24.1 s**, i.e. 1.6× WORSE. Static division starves the largest solve's fill, and the largest
solve is the span. The blocking-permit design is the better of the two, by measurement.

**So M2 is not where the sweep's time is, and M3 is not either — that is the finding.** M3 was the
milestone expected to deliver the large multiple; its premise was measured before a line of it was
written and it does not hold. **Ceiling 1.09–1.15×** — see §6, which records the measurement, the
reason (the fall-off is core heterogeneity, not scheduling) and what would change the answer.

**Where the sweep's time actually is, on the evidence in hand:**
1. **N, through the mesh.** M0 is the shipped lever and it measured **2.76×** on §10.7's own hero
   for a stated accuracy cost. Nothing here comes close to it.
2. **The calibration standards** — the largest single term anyone had measured, and **§10 is
   where most of it went**.
3. **The fill's own per-entry cost** — M4's cell-pair moment cache, whose ~4× is still an estimate
   rather than a measurement, and M5's accelerator behind its own GMRES decision gate.

---

## 10. The calibration standards — fill only the two that are read

**A de-embedded frequency point solved EVERY calibration standard and read exactly TWO of them.**
That was the largest avoidable cost in this area, and closing it changes no published number: the
matrices no longer filled were never looked at.

### The mechanism, and why it was safe to take

`PlanarCalibration.GammaBest` reads `sShort` and `sLong[pick]`. `pick` came from a scoring loop over
the Δℓ set and the **predicted** β — deliberately the prediction and never an extracted electrical
length, because an aliased separation reports a wrapped length that can score well by accident. Both
of its inputs are known **before any fill**. That loop is now
`PlanarCalibration.SelectSeparation(deltaLM, expectedBetaPerMetre)`, called by `GammaBest` exactly as
before and — the point — callable by the driver in advance. `PlanarPortCalibrator.NeededAt` asks it
and fills two meshes; `_rawCache` became one slot per standard, null where a standard was never
wanted.

**The set is NOT narrowed.** Every separation is still built, `MeshCount` and the engine's own
"N standard mesh(es)" note are unchanged, and the per-frequency choice still ranges over all of them.
Only which of them get filled changed — so this makes multiline TRL *cheaper as it widens*, and
`GammaBest`'s own claim that "the selection costs nothing beyond the extra fill" stops being a
caveat.

### The measurement (`CalibrationStandardCostTests`, `Category=Benchmark`, taken alone)

§0's own shape rather than the FR-4 hero, and **the shape is load-bearing**: a standard reproduces the
DUT's transverse gridlines across the port **verbatim** (D4 — the error box has to be the same
object), so the standards only dominate when a port is WIDE. A 50 Ω line's standards are four cells
across and cost nothing. A taper to 12 Ω on RO4350B 20 mil, 1–20 GHz, DUT **N = 3,005**, 10 cores:

| | port 1 (4 cells across) | port 2 (**20 cells across**) |
|---|---|---|
| standards' N | 94 / 451 / 227 / 143 | 526 / **2,515** / 1,267 / 799 |

| | DUT | all standards | the two read | point |
|---|---|---|---|---|
| 1.00 GHz | 14.60 s | 18.91 s | 12.57 s | 33.51 → **27.16 s** (1.23×) |
| 4.47 GHz | 14.92 s | 18.44 s | 5.26 s | 33.36 → **20.18 s** (1.65×) |
| 20.0 GHz | 14.96 s | 18.41 s | 3.59 s | 33.37 → **18.54 s** (1.80×) |
| **band** | | | | 100.23 → **65.88 s** (**1.52×**) |

**The saving is smallest at the BOTTOM of the band and that is structural, not a shortfall.** The
separations are sized geometrically, so the longest standard — the one that can exceed the DUT's own
unknown count — is precisely the one selected at the bottom. At the top it is filled and discarded,
which is where the 1.80× comes from. There is **no** frequency at which every standard is wanted.

### What is asserted, and how

`CalibrationStandardSelectionTests`, **6 routine tests, ~15 s**:

- **T1 is the gate that matters**: the OLD path — every standard filled, `GammaBest` over the full
  set — run alongside the new one over a 9-point 1–20 GHz sweep, comparing γ, βΔℓ, the unwrap count,
  every error-box entry and the consistency residual **bit for bit**, with the branch continuation
  stepped exactly as `At` steps it. **Confirmed to bite**: making the new path predict from the
  pre-solve estimate instead of the continuation — the realistic regression — turns it red.
- **T0** pins that the band genuinely asks for ≥ 4 standards, or every other assertion is vacuous;
  **T3** pins that all three separations are genuinely used across the band, so "fill two" is not
  secretly "build two".
- **T2**: `StandardSolveCount == 2 × frequencies` while `MeshCount` is still the full set.
- **T4**: L9e/M1's replay contract under selective filling. A replay re-predicts β from a different
  neighbour, so it **may legitimately need one more mesh** at an already-visited frequency; what it
  must never do is re-fill one it already has. `SolveCount` therefore counts **distinct frequencies**
  (via `_solvedFrequencies`) and `StandardSolveCount` is where any extra mesh shows up — do not
  collapse the two counters back together.

### Two traps this left behind

**`MeshCount` is no longer the per-frequency solve count.** `PlanarSolve`'s progress stage used it as
its denominator; it now uses `PlannedSolvesAt(f)`, which reports what will actually be filled (0 when
fully cached). Using `MeshCount` there promises ticks that never arrive and leaves the bar short.

**`Mat<T>` is a struct**, so `Mat<Complex>?[]` is an array of `Nullable<Mat<T>>` and `!` does not
unwrap it — `.Value` does. Worth knowing before the next nullable-slot cache in this area.

---

## 11. M5's decision gate — the answer is BUILD IT, and the near-field radius is the whole decision

`brief-em-sweep-performance` gate 11 / R-emp-15, run **before a line of projection code exists**,
which is the point of it. `MomIterativeSolverDecisionTests`, 6 methods, all `Category=Benchmark`.

**The decision was never about the FFT.** AIM has no direct solve, so it needs an iterative one — the
same objection that deferred ACA at L9e. The brief's own rule: *if GMRES needs O(N) iterations, AIM
buys nothing.* That is measurable today against the dense matrices this kernel already builds, and it
is what these six gates measure.

**Two choices that make it a fair test of AIM's premise, not a flattering one.** **FULL GMRES, not
restarted** — restarting can only be slower, so this is an upper bound on any GMRES variant, and
giving AIM the benefit of the doubt is deliberate. **RIGHT preconditioning**, so the Arnoldi residual
*is* the true ‖b − Ax‖ and all three preconditioners are compared on the same quantity; left
preconditioning would report ‖M⁻¹(b − Ax)‖ and flatter a strong preconditioner for free. The RHS is
the **real port excitation**, not a random vector — MoM convergence is RHS-dependent and a port's
incidence column is the only RHS this solver ever sees.

### THE ANSWER, on the shipping mesh (edge grading ON) and a 2-D conductor

§0's own 12 Ω cross-section — 6.71 mm wide, 10 cells across — at 6 GHz. Iterations to a **1e-6**
relative residual:

| L | N | none | near-3c | near-8c | near-8c nnz | near-8c LU | **dense LU** |
|---|---|---|---|---|---|---|---|
| 16 mm | 313 | 129 | 28 | **3** | 72.4% | 0.02 s | 0.01 s |
| 32 mm | 579 | 176 | 66 | **4** | 43.8% | 0.04 s | 0.04 s |
| 64 mm | 1,092 | 242 | 69 | **5** | 24.4% | 0.04 s | 0.24 s |
| 128 mm | 2,099 | 341 | 178 | **6** | 13.0% | 0.10 s | **1.98 s** |

**Three facts, and together they are the decision:**

1. **With an 8-cell near-field preconditioner the iteration count is FLAT — 3 → 6 over 6.7× N.** Not
   sublinear; flat. That is AIM's premise holding on the real product configuration.
2. **The near field is genuinely O(N).** nnz falls 72.4% → 13.0%, i.e. entries *per row* stay
   essentially constant (227 → 273). A near field that did not sparsify would make AIM pointless.
3. **The preconditioner's own factorisation pulls away from the direct solve fast** — 5× growth
   against the dense LU's 200× over the same span, from parity at N = 579 to 20× cheaper at
   N = 2,099. It is still an honest cost and it is the one to keep watching.

### R-emp-17 — the near-field radius is NOT a tuning nicety

**It is the difference between working and not working, and the obvious choice is the wrong one.**
3 cells — the natural first guess — *degrades* with N (28 → 66 → 69 → 178) and is beaten by nothing at
all on a refined mesh. 8 cells is flat. Anyone starting M5 should start at 8 and measure down, never
at 3 and measure up.

**Unpreconditioned and Jacobi are not viable, and Jacobi is worthless.** Unpreconditioned grows as
roughly N^0.5 on the shipping mesh (129 → 341) — sublinear, so not literally the brief's O(N)
disqualifier, but 341 iterations of O(N log N) is no better than the direct solve. **Jacobi is within
a few percent of no preconditioner at every N measured** (e.g. 137 vs 130 at N = 752) — a diagonal
carries none of this operator's ill-conditioning.

### The two ladders disagree, and the disagreement is the useful part

| ladder | what moves | unpreconditioned @1e-6 | near-3c @1e-6 |
|---|---|---|---|
| **structure grows**, density fixed (coarse, 24 → 752) | N | 10 → 137 (~N^0.76) | **2 → 8, flat** |
| **mesh refines**, structure fixed (16 mm, cells/λ 10 → 40) | h | 19 → 60 | **5 → 13 → 77** |

**Growing the board is the easy direction; refining the mesh is the hard one.** A fixed-stencil near
field does not fix the h → 0 conditioning of this operator, and at cells/λ 40 the 3-cell
preconditioner is *worse than none*. This matters here specifically because **edge grading is local
refinement** — the shipping mesh's own cell spread is **8.3×** — which is why the shipping-mesh rows
above needed 8 cells where the smooth coarse mesh was happy with 3.

**Frequency is not a factor** (45-unknown line at 1 / 6 / 20 GHz: 22 / 19 / 19 unpreconditioned).

### What this gate does NOT establish, stated so nobody reads it as more than it is

- **The preconditioner measured is a full sparse LU of the near field**, not the ILU or approximate
  inverse a real AIM would likely use. A weaker preconditioner converges more slowly; the flat rows
  above are the best case for a near-field-based scheme.
- **The projection itself is unmeasured.** This gate says an iterative solve is viable on this
  operator. It says nothing about AIM's own accuracy, which is R-emp-16's two gates and still owed —
  and R-emp-16 is where the interpolation order and this same radius get their accuracy trade.
- **Nothing above 2,099 unknowns**, against R17's 5,000 ceiling. The trends are clean and consistent
  across four ladders, but they are trends.

---

## 12. M5 — the AIM accelerator is BUILT, and the win is MEMORY rather than time

`brief-em-sweep-performance` M5, taken up after §11's decision gate answered BUILD IT. It ships
**OFF** — `PlanarFillSettings.Aim` is null by default and every published number in this file is
still produced by L8c/L8d's dense path, byte for byte.

Files: `PlanarAim.cs` (the accelerator), `PlanarGmres.cs` (the solver), `PlanarFill.PlanarEntryFill`
+ `PlanarFill.BuildGeometryOnlyCores` (the seam), `IPlanarOperator` on `PlanarSystem`.
Gates: `AimAcceleratorTests` (**9 routine, ~2 s**) and `AimAccuracyTests` (**5 `Category=Benchmark`,
5.8 min**).

### What it does, in one paragraph

Each rooftop is replaced, for FAR-field purposes, by point sources on an (M+1)×(M+1) block of a
separate UNIFORM auxiliary grid carrying the same multipole moments; the far field is then three FFT
convolutions on that grid (x̂ current and ŷ current against `G_A`, the charge against `G_q`); and every
pair inside a near radius is corrected with the EXACT entry. The mesh stays graded and conformally
cut — only the auxiliary grid is uniform, which is the whole reason AIM fits here and raw Toeplitz
does not (the brief's own §M5 correction).

**Three projections, not one.** L8c's entry is `jωµ₀⟨f_m, G_A f_n⟩` (same direction only) plus
`1/(jωε₀)⟨∇·f_m, G_q ∇·f_n⟩` (every pair). Projecting the current and not its divergence would
accelerate half the operator and leave the other half dense.

**The stencil is a TENSOR square, not the classic simplex.** Matching `a+b ≤ M` on an `(M+1)²`
stencil is underdetermined and needs a minimum-norm solve; the full tensor set `a, b ≤ M` is square,
solves by two Vandermonde inversions, and matches strictly MORE moments for the same node count. On a
uniform grid every basis shares the same ξ, so **the inverse is computed once for the whole mesh**.

### THE FINDING: the entry count falls 10×, the FILL TIME does not, and the reason is structural

| L | N | near/row | near % | build s | iters | solve s | **dense s** | \|ΔI\| | **MB** | **dense MB** |
|---|---|---|---|---|---|---|---|---|---|---|
| 16 mm | 314 | 290 | 92.2% | 2.65 | 2 | 0.01 | 1.02 | 4.90e-7 | 3.4 | 1.5 |
| 32 mm | 535 | 334 | 62.3% | 3.98 | 4 | 0.01 | 1.57 | 8.69e-7 | 6.7 | 4.4 |
| 64 mm | 994 | 365 | 36.7% | 7.14 | 5 | 0.02 | 2.99 | 5.52e-6 | 13.3 | 15.1 |
| 128 mm | 1,912 | 383 | 20.0% | 13.44 | 5 | 0.05 | 6.69 | 3.98e-6 | 26.7 | 55.8 |
| 256 mm | 3,731 | **392** | **10.5%** | **25.18** | **6** | 0.10 | **26.73** | 1.12e-5 | **53.3** | **212.4** |

(FR-4 hero cross-section, 6 GHz, **shipping mesh** with edge grading on. `dense s` is one fill + one
factorisation + one back-substitution. Neither column carries the radial remainder table, which BOTH
paths build — it is timed apart on purpose, or every comparison would be flattered by a fixed amount
belonging to neither.)

**Two of those columns are exactly what §11 predicted and the third is not.** Near entries PER ROW are
flat (290 → 392 over 12× N) — the near field is genuinely O(N), as §11's own nnz rows said. The
iteration count is flat (2 → 6) — **and that now holds for the ACCELERATED product**, not merely for a
dense product with a near-field preconditioner, which is the thing §11 could not establish.

**What does not follow is the time.** At N = 3,731 the accelerator touches **10.5%** of the entries and
takes **96%** of the dense path's wall clock. The reason is structural and is worth stating plainly:

> **AIM's near field keeps precisely the pairs L8c's singular-extraction machinery makes expensive,
> and discards the cheap ones.** A near pair takes `NearNodes = 10` over `TouchPanels = 3` panels
> (900 outer points) and an `8⁴ = 4,096`-node remainder; a far pair takes `FarNodes = 3` over one
> panel (9 points) and a `2⁴ = 16`-node remainder. The far field this removes is the part that was
> already almost free.

So **the time crossover is at N ≈ 3,700**, and up to R17's 5,000-unknown ceiling the saving on a single
frequency point is of order 1.4×, not the order of magnitude an entry count suggests. Anyone reading
the near-% column as a speed-up will be wrong by ten.

**The MEMORY win is real, it is measured rather than counted from the entry table, and it is the one
to quote.** The last two columns are the accelerator's WHOLE working set — sparse near field, grid
kernels, the padded FFT arrays and the per-basis stencils — against the dense matrix alone:
**53.3 MB against 212.4 MB at N = 3,731**, i.e. **4.0×**, and the crossover is at **N ≈ 900**, four
times earlier than the time crossover. Extrapolating the flat per-row count to R17's 5,000 gives ~71 MB
against 381 MB. R17 IS a memory ceiling (`PlanarSystem.MatrixBytes`), so this is the milestone that
could move it — and moving it is a separate, measured act, deliberately not taken here.

### THE THIRD KNOB — R-emp-17 names two and the dominant one is neither

R-emp-17 asks for the projection order and the near-field radius. The N ladder found a third, and it
outranks both: **the auxiliary PITCH**. The stencil has to resolve the KERNEL across its own width,
not merely enclose the basis, and at a pitch of one whole basis support the stencil spans a quarter of
a guided wavelength — across which `e^{−jk₀ρ}` and every surface wave turn appreciably.

Measured on the 64 mm hero at 6 GHz (N = 994), **holding the near radius fixed IN METRES so the cost is
the control**:

| pitch / support | h/λ_g | stencil/λ_g | near/row | build s | \|ΔI\| |
|---|---|---|---|---|---|
| 1.00 | 0.078 | 0.235 | 365 | 8.14 | 5.69e-4 |
| **0.50** | **0.039** | **0.117** | **365** | **7.31** | **5.52e-6** |
| 0.25 | 0.020 | 0.059 | 365 | 7.59 | 8.72e-7 |
| 0.125 | 0.010 | 0.029 | 365 | 7.60 | 4.72e-6 |

**A hundredfold in accuracy for nothing.** The near-field entry count and the build time do not move —
a finer pitch costs grid nodes and one FFT over them, and no near-field arithmetic at all. **And it
turns back up at 0.125**: past about a quarter of a support the moment system's own conditioning is
the error, so the curve has a floor rather than a slope, and 0.5 is chosen one step inside it.

**This is why the radius is expressed in units of the LARGEST BASIS SUPPORT and not in pitches.** In
pitches, halving the pitch would silently halve the near field, and every pitch measurement above
would have been a radius measurement wearing a disguise.

### R-emp-17's own table — order × radius, at the shipped pitch

FR-4 hero cross-section, 32 mm, 6 GHz, shipping mesh, N = 535. `|ΔZ|` is against the dense matrix
scaled by its largest entry; `|ΔI|` is the SOLVED current vector against the dense LU's, which is the
quantity an s-parameter is read from.

| order | radius | near/row | near % | worst \|ΔZ\| | iters | \|ΔI\| |
|---|---|---|---|---|---|---|
| 1 | 2 s | 126 | 23.6% | 8.92e-5 | 15 | 2.17e-2 |
| 1 | 4 s | 240 | 44.8% | 5.04e-7 | 4 | 1.96e-4 |
| 2 | 3 s | 186 | 34.8% | 3.10e-6 | 5 | 5.97e-4 |
| 2 | 6 s | 334 | 62.3% | 1.63e-8 | 4 | 5.94e-6 |
| 3 | 3 s | 186 | 34.8% | 7.79e-7 | 5 | 1.32e-4 |
| 3 | 4 s | 240 | 44.8% | 7.46e-8 | 4 | 1.59e-5 |
| **3** | **6 s** | **334** | **62.3%** | **1.72e-9** | **4** | **8.69e-7** |
| 3 | 8 s | 410 | 76.6% | 5.80e-10 | 3 | 2.42e-7 |
| 4 | 6 s | 334 | 62.3% | 1.28e-9 | 4 | 6.30e-7 |

**Three readings.** (a) The RADIUS is what costs — `near/row` is a function of it alone — and it is
also what most of the accuracy comes from. (b) The ORDER is nearly free (the build column is flat to
within 10% across the whole table, because the near fill dominates and the `(M+1)⁴` AIM entries do
not), so it should be spent: order 3 at radius 6 is 7× better than order 2 at the same cost, and
order 4 buys nothing further. (c) **The matrix error is amplified ~50-500× into the solved current** —
`|ΔZ| = 1.7e-9` becomes `|ΔI| = 8.7e-7`. Grading an accelerator on its matrix error alone would
overstate it by two to three decades, which is why both columns are here.

**Shipped defaults, taken from these two tables and not from a reference:** projection order **3**,
pitch **0.5** of the largest basis support, near radius **6** supports. That lands `|ΔI|` at
**8.7e-7** — inside L8c's own 5.0e-6 fill accuracy, which is the target R-emp-16 names.

### R-emp-16 gate 2 — the de-embedded S across the band, and what it exposes

20 mm FR-4 hero line, N = 94, three calibration standards, dense vs accelerated through the whole
`PlanarSolve.Run` path:

| f | worst \|ΔS\| de-embedded | worst \|ΔS\| raw |
|---|---|---|
| 2 GHz | 1.27e-7 | 1.79e-8 |
| 5 GHz | 9.84e-8 | 9.27e-9 |
| 10 GHz | 8.48e-7 | 3.95e-7 |
| 15 GHz | 4.52e-5 | 2.31e-5 |
| 20 GHz | **8.07e-4** | 3.33e-4 |

Worst over the band **8.07e-4**, against L8d's own measured de-embedding residual of **6.0e-3** on
1.6 mm FR-4 and L9d's ~1e-2 — so the accelerator is not the error budget, which is what the gate asks.

**And the frequency dependence is not noise — it is M0's own trade, arriving through a second door.**
The pitch is 0.5 of the largest basis support, and the largest basis support is set by the MESH. A mesh
sized at 10 GHz run to 20 GHz has a support of 0.27 λ_g there, so the stencil spans a quarter of a
wavelength and the pitch table above says exactly what that costs. **The accelerator's accuracy is
therefore governed by the mesh's own resolution AT THE SOLVE FREQUENCY** — it inherits M0's trade
rather than adding one, and above the mesh frequency it degrades in step with the mesh. No new knob was
added for it: `MeshFrequencyHz` and `CellsPerWavelength` are already the controls, and inventing a
third that means the same thing would be worse than the trade.

### The one trap: G(0) is arbitrary, and that is only true if the NEAR SET says so

The grid kernel needs a value at zero separation, where `1/ρ` is infinite. **Any finite value works —
but only because every pair whose two stencils OVERLAP is corrected exactly.** Get the near set
slightly wrong and the answer depends on a number that was picked arbitrarily: smooth, plausible, and
unattributable.

So the near set is deliberately the **UNION of two criteria** — a radius AND stencil overlap — rather
than a radius chosen to be "wide enough". `T2` asserts the overlap criterion directly at radius ZERO,
where the radius cannot mask it, and `T3` moves the sentinel by 10× and demands the product not move
(measured: **4.09e-16**).

### The seam, and why it had to be bit-identical

`PlanarEntryFill` computes ONE entry on demand, in the dense fill's own arithmetic and the dense fill's
own order, and `T1` asserts it **bit-identical** to `PlanarFill.Fill` entry by entry. Without that,
"the near field is the exact matrix, sparsely" would be a claim, and every accuracy number above would
be measuring two approximations at once. A tolerance would be the wrong gate for exactly the reason
§9's own note gives: two orderings of the same sum agree to 1e-12 whether or not they are the same
computation.

`PlanarFill.BuildGeometryOnlyCores` is the other half. **Filling the O(N²) cached triangles and then
reading a thin band out of them would leave the whole cost claim resting on the quadratic term being
removed** — so the accelerator path never builds them, `PlanarFillCores.HasPairCores` is false, and the
dense fills refuse such a core by name. The cell-pair potential IS memoised inside the entry filler,
because a rooftop's four signed halves are shared with every neighbour; that is a memo of an identical
call, not a re-association.

### What it refuses, and what is still owed

- **The MULTI-LEVEL / via path is refused by name**, in `PlanarAimOperator.Build` and again in
  `PlanarSolveContext.SolveAt`. A ẑ basis carries `G_A^zz` plus a MIXED component whose dyadic entry is
  a `∂/∂x` rather than a value, and its sources sit at a different height — a different grid kernel per
  height PAIRING and a projection with a derivative in it. That is a second phase, not a widening.
- **A non-converged GMRES throws**, it does not return. A half-converged current distribution produces
  a smooth, plausible, wrong s-parameter, and this area has found that failure mode too often to
  return one.
- **The core cap and the accelerator both stay out of every provenance hash.** M5 changes how the
  answer is computed and — with these gates passed — not what it is; marking an `.snp` stale because a
  user turned an accelerator on would be R-emp-7's lie in a second place.
- **Not measured above N = 3,731**, against R17's 5,000 ceiling, and **not measured on a via-bearing or
  conformally-cut mesh** (the cut path is exercised only through the fill's own weight evaluation,
  which the projection reads rather than re-deriving — see `PlanarFill.WeightNodes` — so it is
  structurally right and numerically unmeasured).
- **The memory ceiling is NOT widened.** `SurfaceMesher.UnknownCeiling` and
  `PlanarSystem.GuardCeiling` are byte-identical, and the accelerator is still refused above them. The
  measurement above is what a decision to widen them would be made from; making it is the owner's call.
- **Restarted GMRES is untested.** `Restart` is a knob at 0 (full) because at 2-6 iterations there is
  nothing to restart, and nothing here has been run at a count where it would matter.
