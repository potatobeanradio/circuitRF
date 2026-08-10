# `src/Engine/Mom` — the quasi-static MoM kernel (kernel A)

Standing instructions for the 2D quasi-static per-unit-length EM kernel. Read with the root
`CLAUDE.md` and `src/Engine/CLAUDE.md`. Design note: `docs/design/layout-view.md` §10. Brief:
`docs/sonnet-briefs/brief-L6-L7-mom-kernel-a.md`. Phase table rows **L6**, **L7**, **L7b** and
**L7b-b**. **The L7b-b section near the bottom supersedes L7b's own where they disagree** — read it
before touching anything about modal decomposition or the multiconductor refusals.

**This is the engine half only.** It builds the neutral geometry contract, the boundary mesher, the
charge solver, RLGC extraction, RLGC → s-parameters and the `IEmKernel` seam. It touches no UI,
reads no `.clay`, writes no `.snp` and adds no dialog. The Ui side — cross-section extraction from
real layout geometry (§10.3.3), the mesh viewer, the EM setup panel and the `.snp` artifact of
§10.8 — is a following brief.

Gate command is plain `dotnet test`. All 169 tests in `tests/Engine.Tests/Mom/` run in ~3 s; none is
tagged `Benchmark`.

---

## The one correction to the design note (R-mom-1)

§10.3.4 specifies `IEmKernel.Solve(LayoutFragment, Stackup, Port[], double[] freqs, …)` and §10.7
says the kernel lives in `src/Engine/Mom/`. **Those two statements are not simultaneously
satisfiable.** `LayoutFragment`, `Stackup` and `Technology` live in `src/Ui/Layout/`, the reference
graph is `Ui → Engine → Core → RfCore`, and inverting the arrow would break the UI firewall that
`tests/Firewall.Tests` enforces.

**The kernel consumes `EmProblem` — a neutral EM problem model defined here, in SI units, that knows
nothing about DBU, `.clay` shapes, layer tables or `LayerKey`.** The Ui-side cross-section extractor
produces it; producing it is what extraction already had to do. This is the standing invariant
*"the numeric layer sees only fully-resolved values"* applied to geometry, and it is what lets the
whole kernel be tested without constructing a layout document.

**R-mom-2. Everything here is in metres, siemens/metre, radians and hertz — doubles, not integers.**
DBU is a database concern and stops at the extractor.

---

## Sign and frame conventions — the part that silently goes wrong

A sign error in this kernel does not produce garbage; it produces a smooth, plausible, wrong number.
Every convention below is pinned by a Tier-0/1/2 test. Do not change one without changing its test.

### The two closed forms (`Kernel2D`)

For a segment **a**→**b** of length `L`, tangent `û = (b−a)/L`, **left** normal `n̂ = (−û_y, û_x)`,
and observation point **p**: `x = (p−a)·û`, `y = (p−a)·n̂`, `r₁ = hypot(x,y)`, `r₂ = hypot(x−L,y)`.

```
F(u)   = u·ln(u² + y²) − 2u + 2y·atan(u/y)
Φ      = ½·[F(L−x) − F(−x)]                  = ∫₀ᴸ ln r ds
P      = −Φ / (2πε₀)
P_self = L·(1 − ln(L/2)) / (2πε₀)            (collocation at the segment's own midpoint)

∂Φ/∂y  = atan((L−x)/y) + atan(x/y)           (the angle the segment subtends at p)
∂Φ/∂x  = ln(r₁ / r₂)
E      = (σ/2πε₀)·(∂Φ/∂x·û + ∂Φ/∂y·n̂)       returned in WORLD coordinates
```

- `Potential` at the midpoint reduces analytically to `SelfPotential`; the dedicated entry point
  exists only so the reduction can be pinned (`T0_4`).
- `Field` is **invariant under reversing a↔b** (`T0_5`), so callers never reason about winding.
- **R-mom-5. `F_ii = 0`.** On the segment itself the subtended angle is π, which is the σ/(2ε₀)
  self-field the dielectric row has already accounted for analytically — so it must be *excluded*.
  Getting this wrong double-counts the self-field and the solver converges smoothly to the wrong
  answer, which is why `T0_2` checks the field kernel against a finite difference of the potential
  kernel rather than trusting it.

### The dielectric-interface row (`ChargeSolver`)

With `n̂` pointing from region 1 into region 2 (**"up"** for the horizontal interfaces the mesher
produces), `σ_b = 2ε₀·K·E_n^avg` with `K = (ε₁ − ε₂)/(ε₁ + ε₂)`, ε₁ **behind** the normal.

*Concretely:* a positive line charge above a dielectric half-space gives `K > 0` and a downward
`E_n^avg`, hence **negative** bound charge — the dielectric is attracted. That matches the textbook
image `q′ = −q(ε_r−1)/(ε_r+1)`. Pinned by `T2_5`, and by the exact half-space check that the total
induced bound charge is `−Kλ`.

### The ε_r charge weighting — the one thing the brief's §3.6 does not say

`§3.6` writes `Q_k = Σ_{i∈k} σ_i·Δ_i`. **That is wrong whenever a conductor faces a dielectric**,
and it is why `EmSegment.EpsOutside` exists.

The solved unknown σ on any surface is the **equivalent free-space** charge density: the quantity
that, radiating into vacuum, produces the true field. On a conductor surface facing a medium ε_r,
the dielectric's own bound charge sits immediately against the metal and is folded into σ. With all
charge explicit, `E_n = σ/ε₀` just outside the metal, and `D_n = σ_free` at a conductor surface, so

```
σ_free = ε_r · σ            ⇒   Q_k = Σ_{i∈k} ε_r,i · σ_i · Δ_i
```

Without this, a fully-filled coax comes out at the **air** value instead of ε_r times it (`T2_1`),
and a microstrip's substrate-facing bottom face is under-counted. `EpsOutside` is looked up per
segment from the region the surface's **outward** normal points into, so a conductor straddling an
interface carries both values (`MeshingTests.AConductorEdgeCrossingAnInterfaceIsSplitAtIt`).

### Loss is a complex permittivity, and it costs one solve (R-mom-6)

`ε* = ε_r(1 − j·tanδ)` throughout ⇒ `K` complex ⇒ `[C] = C′ − jC″`, and

```
Y = jω·C_complex = ωC″ + jωC′      ⇒   G = −ω·Im(C),   C = Re(C)
```

`G ∝ ω` for constant tanδ **falls out** rather than being asserted (`T5_2`). Any number of
independently lossy dielectrics is handled. **Do not implement a separate partial-capacitance
accumulation** — that sketch in §10.3.2 item 4 is superseded. When every tanδ is zero the imaginary
part is zero to round-off (`T2_3`).

### The ground plane is an exact image (R-mom-7)

**Never meshed.** Every source segment contributes its mirror about `y = Y_g` with negated charge,
for both `P` and the field. Because *all* charge — free and bound — is explicit and radiating into
free space, the image makes φ = 0 on the plane **exactly**, dielectrics included. This is not an
approximation (`T0_7`, and `T1_2`'s wire-over-ground oracle is what actually tests it).

A dielectric interface coincident with the ground plane is not an interface; it is dropped, with a
note in the report.

**Cross-checked independently:** replacing the image with an explicitly meshed 60 h ground plate —
a formulation using no image whatsoever, ~4800 unknowns — reproduces ε_eff to 0.14%.

---

## Architecture

```
EmProblem.cs          EmPoint, EmMaterial, EmDielectricRegion, EmConductor, EmGroundPlane, EmPort,
                      EmProblem, EmConstants
IEmKernel.cs          IEmKernel, EmCapabilities, EmSuitability
EmMesh.cs             EmSegment, EmMesh, ConductorMeshTemplate
EmMeshSettings.cs     the six mesh controls
EmMeshReport.cs       segments, counts, min/max cell, truncation extent, Wheeler crossover, notes
Polygon2D.cs          winding, containment, horizontal footprint, inward offset, self-intersection
BoundaryMesher.cs     perimeters, interfaces, edge grading, truncation, R-mom-9 exclusion
Kernel2D.cs           the closed forms + the R-mom-7 image
ChargeSolver.cs       assembly, NumFlat LU, one factorisation for M excitations
RlgcExtractor.cs      [C], [C₀], [L], [G], Wheeler [R]
ModalDecomposition.cs the GENERAL decomposition (L7b-b, Route A) + the R-cpl-7/8 symmetry checks;
                      the L7b even/odd split survives here only because tests use it as an oracle
RlgcToSparams.cs      γ, Z_c, Z-matrix, RFNetwork.ZToS, DataSet assembly — the single-line 2-port
                      AND the general modal 2N-port
QuasiStaticKernel.cs  IEmKernel implementation; CanSolve; MaxSignalConductors
```

**`ChargeSolver` takes an `EmMesh`, not an `EmProblem`.** The physics of §3 is stated over segments,
not over horizontal slabs. Keeping the solver neutral about how the segments were produced is what
lets the exact *cylindrical*-interface oracles (two-layer coax) be tested at all — they are not
expressible in the horizontal-slab `EmProblem` that R-mom-3 deliberately restricts the mesher to.

**There is deliberately no kernel registry.** One kernel, constructed directly. A registry earns its
place when kernel W or B exists; adding it now is speculative plumbing with no second implementation
to constrain it.

---

## Meshing

**R-mom-8. Edge grading is geometric, from both ends of every conductor face, and it applies to
dielectric-interface segments near a conductor edge too.** It is written against segment geometry —
a cell-size field over attractor points — not against "the microstrip case", so B and C reuse it:

```
h(x) = min over attractors a of [ c₀ + (r−1)·|x − a| ],  clamped
```

That linear form **is** the geometric progression c₀, c₀r, c₀r², …: cell k starts at
`d_k = c₀(rᵏ−1)/(r−1)` and has size `c₀rᵏ = c₀ + (r−1)d_k`. Stating it as a field rather than a
per-end loop is what makes it compose over any number of attractors and both-ended intervals.

**Two deliberate deviations from §10.5's wording, both measured, both load-bearing:**

1. **The edge cell is a fraction of the conductor's SMALLEST bounding-box dimension, not its width**
   (`BoundaryMesher.EdgeReference`). §10.5 says "a small fraction of the width (~2–5%)", which is
   right when width and thickness are comparable. For a rolled foil it is not: the charge
   singularity lives at the 90° metal *corner*, whose scale is the thickness. On 1.6 mm FR-4 with
   W = 2.9 mm and t = 35 µm, 3% of the width is 87 µm — larger than the entire side face — and the
   mesh cannot see the corner. **Measured: with the width reference ε_eff converges as N^−½ and
   sits 4% low at any affordable N; with the thickness reference it is within 0.1% of its own
   converged limit at N ≈ 150.** Same rule, same 3%, correct reference length.

2. **The interface cell size has two scales.** Near a conductor edge the bound charge is singular
   and the scale is the metal feature size. Away from it the microstrip field decays on the scale of
   the **substrate height**, so the cap out to `2h` is `h/MinCellsAcrossWidth` — *not* a fraction of
   the truncation length, which for a narrow strip is an unrelated number. Only beyond `2h` does the
   cap itself grow, geometrically, so the truncated tail costs exactly `TruncationTailCells` cells
   (`BoundaryMesher.GeometricSlopeFor` solves for the ratio).

**R-mom-9. Interface segments are excluded wherever the interface lies inside *or on* a conductor.**
The strip sits *on* the substrate: its bottom face carries free charge and the interface beneath it
does not exist. Two unknowns on the same physical surface make the matrix singular — a good failure,
but the one to test for (`MeshingTests`). `Polygon2D.HorizontalFootprint` handles the degenerate
touching case that a plain crossing count gets wrong.

**R-mom-10. The truncation distance is an explicit setting with a convergence test, never a hidden
constant.** Reported in `EmMeshReport.TruncationHalfExtent` and in the notes. Doubling it moves Z₀
by < 0.5% (`T3_3`). It converges fast because a microstrip is charge-neutral with its image — the
far field is dipolar, so the bound charge falls as 1/x², not 1/x. Do not assume that stays true for
a geometry without a ground plane.

`Mesh(...)` returns everything the mesh viewer will draw and everything §10.5's "report the unknown
count *before* solving" needs. **Report it from the engine so the UI has nothing to recompute.**

---

## RLGC

1. **[C]** — the charge solve with the real stackup and complex ε*.
2. **[C₀]** — the same solve with every material air. Every `K` is zero, so the dielectric rows drop
   out entirely and only the conductor block is solved.
3. **ε_eff = C/C₀**.
4. **[L] = µ₀ε₀[C₀]⁻¹** — the TEM identity. No second formulation. `EmConstants.Eps0` is *derived*
   as `1/(µ₀c²)` so µ₀ε₀ = 1/c² to the last bit.
5. **G** — already in [C]'s imaginary part.
6. **[R]** — Wheeler, below.

**R-mom-11. [C], [C₀] and ∂L/∂n are frequency-independent and are computed exactly once per sweep.**
This is what makes v1 "dramatically snappier than the thing that replaces it", and it is easy to
lose in a refactor — so it is enforced by `RlgcModel.MatrixFillCount`, asserted at exactly **4**
(the two capacitance solves plus the two Wheeler recessions) for both a 3-point and a 1001-point
sweep (`T4_5`). **Not by a comment.**

### Wheeler, without destroying R-mom-11

```
R(ω) = Σ_surfaces (R_s,k(ω)/µ₀)·(∂L/∂n)_k ,   R_s = √(ωµ₀/2σ) = 1/(σδ)
```

**R-mom-12. ∂L/∂n is a purely geometric derivative, evaluated once by a single finite-difference
recession; the frequency dependence enters only through R_s.** The naive reading — "recede every
surface by δ/2, re-solve, difference" — makes the recession frequency-dependent and forces a refill
per frequency for no accuracy gain.

Two surfaces, each with its own R_s:
- **signal conductors** — outline shrunk inward by Δ (`Polygon2D.OffsetInward`);
- **the ground plane** — moved *down* by Δ (equivalently h → h+Δ).

Both increase L, so both ∂L/∂n are positive; a negative one is a bug and the sign is asserted
(`T5_4`). **Omitting the ground-plane term is the common error and it under-reports microstrip loss
noticeably.** The perturbed geometry is re-meshed from the *same* `ConductorMeshTemplate`, so the
finite difference is not contaminated by the discretisation moving underneath it. Δ = min(t, W)/50,
further capped at half the smallest mesh cell; halving it moves R by < 1% (`T5_5`).

**Note the design note's own formula is wrong.** §10.3.2 item 5 writes `R = (ω/2)·∂L/∂n·(2/δ)`,
which is `ωδ/2 · ∂L/∂n` short by a factor δ². The correct `R_s/µ₀ = ωδ/2` form is what is
implemented.

**R-mom-13. Report the frequency below which Wheeler is invalid, and floor R with the DC value.**
`f(δ = t/2) = 4/(π·t²·µ₀·σ)` (~14 MHz for 35 µm copper) goes in the mesh report and its notes.
`R = √(R_dc² + R_wheeler²)` is labelled in the code as the standard smooth interpolation it is, not
as physics — it takes about three decades below the crossover for R to settle onto R_dc.

Surface roughness is **out of scope**: the field is absent from the model rather than accepted and
ignored.

---

## RLGC → s-parameters

```
Z  = R + jωL              Y = jω·C_complex = G + jωC
γ  = √(ZY), Re(γ) ≥ 0     Zc = √(Z/Y)
Z11 = Z22 = Zc·coth(γℓ)   Z12 = Z21 = Zc / sinh(γℓ)
S   = RFNetwork.ZToS(Z, z0PerPort)
```

**R-mom-14. Form the Z-matrix and convert with `RFNetwork.ZToS`; do not write a second ABCD→S.**
It already handles per-port and complex reference impedances and is already the path every other
s-parameter in this project goes through, so reciprocity is *structural* rather than hoped for.

**R-mom-15. De-embedding is a no-op for kernel A, and that is a finding, not a shortcut.** §10.6
requires de-embedding because a *meshed* port excitation carries a port discontinuity. Kernel A
computes γ and Z_c analytically and forms the Z of a uniform line of length ℓ — the reference planes
are exactly at the line ends by construction and there is nothing to remove (`T4_7` pins the
observable consequence: ∠S₂₁ is exactly −βℓ). The two-line calibration becomes real work at L8;
building it now would be building a calibration for an error that does not exist.

Results follow the house convention exactly — `SNP` → `DataSetBuilder.FromSnp` → per-port `Z0` cube
— plus a `"tline"` group carrying `Zc`, `Gamma` (Complex) and `Eeff`, `AttenDbPerM`, `Rpul`, `Lpul`,
`Gpul`, `Cpul` (Real). **No new result type.** No `.snp` file is written here.

The **Kirschning–Jansen dispersion correction** is wired as an opt-in constructor flag on
`QuasiStaticKernel`, **off by default**, applicable to the single-microstrip case only
(`TryMicrostripDispersion` returns null for anything else rather than applying a formula never
derived for it). It is a correction on top of a validated static result, never a substitute for one.

---

## What the oracles actually established

The gate ladder is `tests/Engine.Tests/Mom/`, and **R-mom-16** is the rule that made it worth
building: validate the charge solver against exact closed forms *before* comparing anything to
Hammerstad-Jensen, because H-J is itself an empirical fit and a ±2% agreement against it can hide a
real defect.

| Tier | What | Result |
|---|---|---|
| 0 | the integrals vs quadrature / finite difference | 1e-10 / 1e-7 relative |
| 1 | coax, wire-over-ground, two parallel wires | ≤ 0.5%, monotone under refinement |
| 2 | two-layer coax, filled coax, air check, lossy fill | ≤ 1%; air check and Im(C) to 1e-12 |
| 3 | microstrip vs H-J, 0.1 ≤ W/h ≤ 10, FR-4 and GaAs | **≤ 1.3% on ε_eff, ≤ 0.6% on Z₀** |
| 4 | reciprocity, passivity, losslessness, cascade, fill count | exact / to solver tolerance |
| 5 | dielectric loss vs closed form; conductor loss vs `MicrostripLoss` | 5%; √f slope exact |

### Two findings worth keeping

**1. The thick-strip divergence from H-J is H-J's thickness model, not this kernel's.** At zero
thickness the two agree everywhere in the published range. H-J models a thick strip by *widening* W,
which raises ε_eff slightly; a boundary-element solve of the actual rectangle sees the strip's two
side faces in **air**, so a thicker strip pulls field out of the substrate and ε_eff **falls**. The
two therefore diverge in opposite directions, scaling with t/W — measured −1.4% at t/W = 0.02,
−2.7% at 0.07, −4.7% at 0.22 on FR-4, and roughly 1.8× that on GaAs where the larger ε_r makes the
substrate/air split matter more. At t/W ≈ 0.2 (35 µm copper on a 160 µm strip) H-J's ΔW correction
is far outside any regime it was fitted for. Tier 3 therefore has two gates: the full W/h span at a
thin strip, and real metal over the span where t/W ≤ 0.01. **Do not "fix" the solver toward H-J** —
`T3_6` pins that the whole disagreement collapses as t → 0. Z₀,air (no dielectric in the problem at
all) matches H-J to 0.3% at *every* thickness.

**2. `MicrostripLoss.ConductorLossNpPerM` is a wide-strip asymptote and this kernel approaches it
from below.** `α_c = R_s/(Z₀·W)` assumes uniform strip current across exactly W *and* an equal
R_s/W from the ground plane. The second assumption is the weak one: a real ground-plane current
spreads over roughly ±(W/2 + h), so for a narrow strip it contributes far less. Wheeler's rule on
the actual geometry weights every surface by its own |H|² and gets this right. Measured ratio
(Wheeler / crude): 0.40 at W/h = 0.3, 0.60 at 1.81, 0.79 at 10, 0.87 at 20, **0.96 at 50** — i.e. it
converges to the parallel-plate limit exactly, which is what validates the Wheeler path. The ±20%
level gate is therefore taken where the oracle is derived (W/h ≥ 20); the monotone approach across
0.3 ≤ W/h ≤ 50 is pinned instead, and it is a stronger statement than any single band.

### The asymmetry residual, measured (superseding the earlier ~3% estimate)

Point collocation on a piecewise-constant basis does **not** make the system matrix symmetric — only
a Galerkin discretisation would, so the residual `C₁₂ − C₂₁` is a **discretisation-error indicator**,
not a bug. L7b is the phase that consumes the off-diagonals: it symmetrises
(`ModalDecomposition.Symmetrise`) while REPORTING the raw residual (`RlgcModel.AsymmetryResidual`,
surfaced in `Notes`) rather than averaging it silently out of existence.

**The ~3% recorded here previously was an incidental observation; these are the measured numbers**,
on a realistic edge-coupled pair (FR-4, 35 µm copper, W = 1.4 mm, S = 0.3 mm):

| | `EmMeshSettings.Default` | `Refined(2)` |
|---|---|---|
| off-diagonal residual `max\|C_ij − C_ji\|/\|C_ij + C_ji\|` | **0.554%** | **0.146%** |
| diagonal asymmetry `\|C₁₁ − C₂₂\|/\|C₁₁ + C₂₂\|` | **0.073%** | 0.022% (0.001% at `Refined(4)`) |

Two findings behind those numbers, both load-bearing:

- **Collocation is not the source of the asymmetry — the MESHER is.** Two identical circles
  discretised by the same *uniform* template are segment-for-segment mirror images, so `P_ij = P_ji`
  exactly and the residual is zero to round-off (`ClosedFormCapacitanceTests.TC1_2`). It appears only
  once edge grading and interface segments make the two conductors' discretisations differ. That is
  why the convergence gate lives on the mesher path (`CoupledLineTests.TC1_…`), not on `TwoWires`.
- **It blows up on an extreme aspect ratio, and converges anyway.** At t/W ≈ 1/1400 (a 1 µm strip
  1.4 mm wide) the diagonal asymmetry is **6.8%** at default settings, falling to 0.99% at
  `Refined(4)`. Converging is what proves it is discretisation error rather than a geometric or
  solver fault.

**R-cpl-8 is therefore asked of the GEOMETRY, never of the solved [C]** —
`ModalDecomposition.CheckGeometricSymmetry`. Testing `C₁₁ ≈ C₂₂` directly is the obvious
implementation and it is wrong: it refuses that 6.8% pair — mirror-symmetric *by construction* — as
"asymmetric" and points the user at L7b-b, when what they need is a finer mesh. Conductor outlines
are exact and immune to discretisation, so they are what the legality question is asked of; the
matrix version survives as a WARNING (`DiagonalAsymmetry`, threshold 2%) saying "refine the mesh".

---

## L7b — the symmetric coupled pair

> **PARTLY SUPERSEDED BY L7b-b (below).** Everything here about `[R]`/`[R_dc]`/`[G]`, the D3 port
> map and the block construction stands unchanged. What no longer holds: the even/odd split is no
> longer a production path (D1 below is now a statement about a *test oracle*), and the asymmetric /
> N > 2 refusals it describes are gone. Where the two sections disagree, L7b-b wins.

`[C]`, `[C₀]` and `[L]` were full N×N matrices from the engine half; L7b opens up the five places
their answers were **collapsed to element [0,0]**, adds the even/odd decomposition, and forms the
coupled 4-port. Brief: `docs/sonnet-briefs/brief-L7b-coupled-lines-and-cosim.md`.

### D1 — the symmetric pair needs NO eigensolver, and that is why THAT phase was tractable

Two identical conductors, mirror-symmetric about a plane, decouple with a **fixed** modal matrix
`[1 1; 1 −1]/√2` — by symmetry alone, with or without loss, because that matrix diagonalises *any*
2×2 of the form `[a b; b a]` whatever `a` and `b` are.

**This is not a convenience.** `NumFlat`'s complex eigensolver is Hermitian-only — its own XML says
*"the matrix to be decomposed must be symmetric positive definite… only the upper triangular part is
used, and the rest is ignored"* — and returns `Vec<double>`, **real** eigenvalues. For a lossy
multiconductor line `[Z][Y] = (R + jωL)(G + jωC)` is a general non-Hermitian complex matrix whose
eigenvalues γ² are genuinely complex. Handing it to `MatrixDecompositions.Evd` would read the upper
triangle of a matrix that is not symmetric and return real numbers for a quantity that is not real —
a smooth, plausible, wrong answer. **Verified against NumFlat 1.3.0 directly at L7b, not assumed.**

**That is still true, and L7b-b did not work around it — it went the other way.** Rather than
decomposing the complex `[Z][Y]`, Route A decomposes the *lossless real symmetric-definite* problem,
which is the one shape NumFlat genuinely can do, and carries loss perturbatively. See the L7b-b
section for the measurement that says the error that introduces is negligible.

### The five scalar collapses, opened (R-cpl-1/2/3)

| Was | Now |
|---|---|
| `∂L/∂n` a scalar from `[0,0]`, every conductor receding TOGETHER | `LossSurface.DLdn` is the full N×N derivative, one surface receded **at a time** |
| `rdc` summing every conductor into one number | `RdcPerM` a **diagonal** matrix — a conductor's DC series resistance is its own |
| `R(ω)` scalar | `RMatrix(ω)`; the DC floor is applied **diagonal-only** (a `√(a²+b²)` blend on an off-diagonal would strip the sign of a mutual resistance) |
| `G(ω)` scalar | `GMatrix(ω)` |
| `Eeff` one number | still the single-conductor value **and documented as such**; the modal pair comes from `ModalDecomposition` |

`MatrixFillCount` is now `2 + N + (ground ? 1 : 0)` — still **4** for a single line, so R-mom-11's
counter gate is unchanged; 5 for a pair.

**C1's own gate was byte-identity of the single-line answer, and it was checked rather than reasoned
about** — the pre-C1 extractor was reconstructed and both were run over three microstrip fixtures at
full `R` precision. Two re-associations moved `R` by one ulp and were reverted: `rs / µ₀ * DLdn` must
stay in that association (not `(rs/µ₀) * DLdn` hoisted out of the loop), and the finite difference
must be `(a − b) / Δ` (not `(a − b) * (1/Δ)`). Both are commented at the point they matter. **If you
touch `RMatrix` or `Derivative`, re-run that comparison — the Tier 3 oracles carry tolerances and
structurally cannot catch a one-ulp move.**

### The modal quantities (R-cpl-9), stated once

With the symmetrised matrices and the Maxwell convention already in use (off-diagonals **negative**):

```
C_even = C₁₁ + C₁₂        C_odd = C₁₁ − C₁₂        (and the same combination for [L], [R], [C₀])
Z_e = √(L_even/C_even)    Z_o = √(L_odd/C_odd)
ε_eff,e = C_even/C₀,even  ε_eff,o = C_odd/C₀,odd
```

A mode is then an **ordinary per-unit-length line**, so γ and Z_c come from the single line's own
per-frequency code — there is no coupled-line-specific γ anywhere. Because the complex `[C]` already
carries `[G]` (R-mom-6), a mode's shunt admittance is one complex sum and there is no separate `G`
combination to get wrong. `[R]` is the only per-frequency modal quantity (`ModalDecomposition.ModalR`).

**The sign convention on `C₁₂` is what silently inverts all of this.** A Maxwell matrix has negative
off-diagonals; a "mutual capacitance" matrix has positive ones, and getting it backwards swaps even
and odd — both answers look physical and a magnitude plot of a symmetric structure barely moves. It
is pinned by `Z_o < Z_e`, which holds for every real edge-coupled line.

### D3 — the port map, and why it gets its own test

**Port `2k−1` is conductor *k*'s NEAR end, `2k` its FAR end.** So ports 1,2 are the two ends of
conductor A and 3,4 the two ends of conductor B. Stated in `EmProblemBuilders.CoupledMicrostrip`, in
`CrossSectionExtractor`, and in `RlgcToSparams.BuildCoupledPair`; **never re-derive it.** A transposed
map produces a coupler whose through and coupled ports are swapped — smooth, plausible, wrong, and
invisible in a magnitude plot. `CoupledLineTests.TC2_D3PortMap_…` and `…_HasNoCrossCouplingInTheSMatrix`
both turn red against the transposition (verified by making it, not by assuming).

### R-cpl-10 — the 4-port is a block construction, so reciprocity is structural

Each mode is a 2×2 line matrix `Z_m2` over (near, far). Superposition gives
`Zs = ½(Z_e2 + Z_o2)`, `Zm = ½(Z_e2 − Z_o2)`, and the 4-port is `[[Zs, Zm], [Zm, Zs]]` →
`RFNetwork.ZToS`. Both blocks are symmetric, so `S = Sᵀ` falls out of the construction rather than
being hoped for; and with `Z_e = Z_o` the mutual block vanishes and it degenerates *exactly* into two
independent lines, which is Tier C2's far-apart gate.

**D4 — no new result type.** The same `"tline"` group, whose scalars become per-mode PAIRS
(`ZcEven`/`ZcOdd`, `EeffEven`/`EeffOdd`, …).

### Tier C3 — the published fit was NOT obtained, and that is reported rather than faked

The brief asks for coupled microstrip against a published even/odd fit at ±2–3%. **No fit whose
inputs could be verified was obtainable.** The one reachable calculator returned *identical* output
for εr = 4.4 and εr = 1.0 while reporting *"There were errors in your input values"* — its numbers
correspond to unknown geometry. Building a gate on those would be worse than having none, and this
codebase has been here twice before (H-J's thickness model; the Garg-Bahl gap at L5a).

**Substituted: a physical limiting case against an INDEPENDENTLY COMPUTED geometry.** As the gap
closes, two strips of width W separated by S become one strip of width 2W + S; in the even mode both
conductors sit at the same potential, so `2·C_even` must approach that wide strip's own C — solved by
the same kernel on a geometry it knows nothing about. Measured: −1.10% at S = 400 µm falling
monotonically to **−0.075% at S = 20 µm** (`CoupledLineTests.TC3_MergedStripLimit_…`). It exercises
the `C_even` combination directly, so an inverted `C₁₂` sign misses it by tens of percent.

If a verifiable fit ever becomes available, add it — but Tier C1 (exact) and Tier C2 (far-apart
against kernel A's own single-line result, `Z_o < Z_e`, reciprocity/passivity/losslessness) are the
stronger half of the ladder and were never contingent on it.

## L7b-b — the GENERAL modal decomposition

L7b's even/odd split is gone from production. A cross-section of **any** N (up to a stated ceiling,
symmetric or not) goes through one path: `ModalDecomposition.DecomposeGeneral` → `EvaluateAt` →
`RlgcToSparams.BuildGeneral`. Brief: `docs/sonnet-briefs/brief-L7b-b-general-modal-decomposition.md`.

### D1 — one path, and L7b's closed form is now a TEST ORACLE

`RlgcToSparams.BuildCoupledPair` is deleted. Two code paths that must agree are two code paths that
will eventually disagree, and the one that drifts would be the rarely-exercised one. L7b's fixed
`[1 1; 1 −1]` construction lives on verbatim in `tests/…/Mom/Support/L7bSymmetricPairOracle.cs` —
exact by symmetry alone, with or without loss, and therefore the one exact answer available for a
genuinely coupled, genuinely lossy structure.

### Route A, and the measurement that decided against Route B

`Gevd(Re[C], [L]⁻¹)` — the lossless problem `[C]v = λ[L]⁻¹v`, λ = 1/v_p², both sides real symmetric
definite, which is the one shape NumFlat CAN decompose. Loss is then carried perturbatively: form
the FULL modal matrices with loss in them and keep only their diagonals.

**R-gen-1 — `Symmetrise` is a PRECONDITION, not a tidy-up.** NumFlat reads only the upper triangle
and does not check; point collocation does not produce a symmetric `[C]`. Re-verified against
NumFlat 1.3.0's own XML, and `GeneralModalTests.NumFlatGevd_…` pins both halves of the contract
(`A v = λ B v`, and `V` returned B-orthonormal) so a version bump cannot change them silently.

**The measurement (Tier G1), because D2 said the number decides:**

| fixture | worst \|ΔS\| vs exact | where |
|---|---|---|
| symmetric pair, any loss | ~0 (machine) | Route A is **exact** there — see below |
| asymmetric pair, copper, tanδ 0–0.2, 100 kHz–20 GHz | **4.9e-4** | at 20 GHz |
| 100 mm of 1 MS/m metal, 10:1 widths, 150 µm gap, 100 kHz | **1.7e-2** | four decades below that metal's own Wheeler crossover |

Two orders of magnitude below the `[C]` solve's own discretisation error (Tier 3 is ≤1.3% on ε_eff)
on anything realistic. **Route B was not built** — a hand-written Hessenberg+shifted-QR complex
eigensolver is a real numerical-methods commitment and this measurement does not earn it.

**Finding — a SYMMETRIC pair cannot measure Route A's error at all, so the brief's own G1 fixture
had to be replaced.** `[1 1; 1 −1]` diagonalises any `[a b; b a]`, and for a mirror-symmetric pair
every one of [R], [L], [G], [C] has that form — so lossless `[L][C]` and lossy `[Z][Y]` share
eigenvectors and Route A discards *nothing*. The accuracy measurement is therefore taken on an
ASYMMETRIC pair against `ExactTwoConductorOracle` — a closed-form 2×2 complex eigen-decomposition
(the quadratic formula; no eigensolver library) sharing R-gen-2's block construction with production
so that the ONLY difference is which Tv is used, and the comparison isolates exactly the
approximation being measured.

**Finding — `ModeCouplingResidual` does NOT predict the terminal error, and G1 said to report that
rather than loosen a tolerance.** The two are *anti-correlated in frequency*: at 100 kHz the residual
is ~0.36 while the error is ~5e-5; at 20 GHz the residual has fallen to ~2e-4 while the error has
risen to ~5e-4 — past the residual, so it is not even a bound. The residual measures the error in
the modal MATRICES; how much of it reaches the terminals scales with electrical length γℓ, and a
line that is electrically short is insensitive to how its modes were split. It is still the right
diagnostic — it is the honest measure of what was discarded, and it is loud where the error is worst
— but it is not a predictor of accuracy. `G1_TheResidualDoesNotPredictTheError_…` pins this so a
later change cannot quietly assume the opposite.

**Finding — on a discretised mesh the general path is ~3 orders of magnitude CLOSER to exact than
L7b's forced modal matrix** (8e-6 against 8.9e-3 at default settings; both converge under
refinement). L7b forces the modal matrix a *perfectly* symmetric pair would have, but the solved
matrices carry the mesher's own diagonal asymmetry (0.074% at default) — so forcing `[1 1; 1 −1]` is
itself an approximation, and the larger one. **Nothing should be reinstated on the grounds that
L7b's closed form was the better answer; it was not.** It is worth keeping only as an oracle.

### R-gen-3a — the reported `Zc` is in OHMS, and this is the trap

Under NumFlat's B-orthonormal normalisation `Zc_m = √(Zm_mm/Ym_mm)` comes out as the mode's own
**phase velocity** (1.8×10⁸, exactly 1/√λ), not 90.5 Ω / 47.3 Ω — and the per-mode ratios differ, so
no single constant repairs it. The terminal S-parameters are correct throughout; this affects only
what the `tline` group publishes, i.e. precisely the number a user reads off a plot and believes.

**The fix, stated once.** The physics fixes `Ti` only up to one scalar per mode. Take the strict
biorthogonal partner `Tib = (Tvᵀ)⁻¹` and scale its column m to be the least-squares closest to
`Tv`'s column m — which, because `Tvᵀ·Tib = I`, is simply `Ti_m = Tib_m / ‖Tib_m‖²`. Where the two
are parallel (a symmetric pair) it makes them **equal**, which is L7b's own "each conductor carries
the mode's own current". Consequences worth knowing:

- `Zc_m` comes out in ohms and reproduces L7b's `Z_e`/`Z_o` exactly (gate: `G2_ReportedZc_IsInOhms_…`).
- `Ti⁻¹ = diag(1/e)·Tvᵀ` — computed directly, never by a second numerical inversion.
- Every reported per-mode quantity becomes invariant to scaling `Tv`'s columns, not just the terminal
  answer. R-gen-3's vicious-scaling gate drives the production derivation
  (`FromVoltageModalMatrix` + `RlgcToSparams.ModalBlock`) rather than a restatement of it.

### R-gen-2 / reciprocity — STRUCTURAL, at exactly L7b's strength

Every 2N-port block is `Tv·diag(x_m)·Ti⁻¹ = Tv·diag(x/e)·Tvᵀ` — **symmetric for any Tv**, not just a
symmetric one, because of the `Ti` rule above. It is assembled as `Σ_m (x_m/e_m)·Tv[i,m]·Tv[j,m]` so
the `[i,j]` and `[j,i]` entries are **bit-identical**.

**Say the strength precisely, because it is easy to overclaim:** *Z* is symmetric bit for bit; *S* is
not, because `RFNetwork.ZToS` inverts a matrix — it is symmetric to that routine's own tolerance
(measured well under 1e-12). **That is exactly what L7b had** (its blocks were structurally symmetric
too and its own gate also used 1e-12 on S), so generalising the transform weakened nothing.

### R-gen-7 — mode order, and a wording inconsistency in the brief

Sorted by **λ ascending**, tie-broken by the eigenvector's own largest-magnitude conductor index
(with a relative tolerance on λ, so a genuinely degenerate pair falls through to the tie-break rather
than ordering on round-off), then by raw LAPACK index for a total order.

R-gen-7 says "ascending — slowest mode first"; those are not the same thing, since λ = 1/v_p² makes
ascending λ the *fastest* mode first. The operative instruction — ascending, deterministic, a
physical property rather than whatever LAPACK returned — is what is implemented; for a microstrip
pair that puts the ODD mode at index 0. Nothing downstream depends on which end is which:
`GeneralModes.TryIdentifyEvenOdd` names the modes from their **sign pattern**, not their position.

**Mode identity is stable across a sweep BY CONSTRUCTION, not by a sorting heuristic** — Tv comes
from the lossless problem, which has no ω in it, so there is one ordering decision for the whole
sweep. That is a real advantage of Route A over a Route B, which would produce a per-frequency Tv and
make mode TRACKING a genuine problem rather than a free one.

### R-gen-8 — the `tline` group gets a MODE AXIS

`Zc`, `Gamma`, `Eeff`, `AttenDbPerM`, `Rpul`, `Lpul`, `Gpul`, `Cpul` are rank-2 `[freq, mode]` cubes,
plus `ModeCouplingResidual` over `[freq]`. **D4 still holds: no new result type, for the fourth phase
running.**

**Decision on `…Even`/`…Odd`: kept as an ADDITIONAL alias for N = 2 only, sliced from the same
arrays.** A coupled-line designer thinks in even and odd, every saved `.cdd` trace pointing at
`tline.ZcEven` keeps working, and a second name for one number cannot drift. The two are identified
from `Tv`'s sign pattern, never from mode order; an ambiguous pattern publishes no alias rather than
a guessed one, and N ≥ 3 publishes none at all.

### R-gen-9 — the ceiling that replaced the L7b-b refusals

`QuasiStaticKernel.MaxSignalConductors = 16`. **Measured**, on the FR-4 starter stackup (1 mm strips,
0.3 mm gaps), RLGC extraction only — the s-parameter step is milliseconds at any N:

|  | `EmMeshSettings.Default` | `Refined(2)` |
|---|---|---|
| N = 2 | 206 unknowns, 0.04 s | 292 unknowns, 0.07 s |
| N = 8 | 680 unknowns, 1.0 s | 886 unknowns, 2.1 s |
| N = 16 | 1,312 unknowns, 9.1 s | 1,678 unknowns, 19.1 s |

Bounded by the **dense boundary-element solve** (cubic in mesh unknowns, repeated once per conductor
for Wheeler's derivative), not by the modal step, which is only cubic in the conductor count. The
cost is dominated by the mesh, so a refined mesh raises it steeply — these figures are the floor.

**`CheckGeometricSymmetry` SURVIVES; it stopped being a refusal.** It still answers "is this pair
mirror-symmetric?", which is what makes L7b's exact construction applicable as a test oracle and is
the honest place to decide whether an even/odd vocabulary means anything for a cross-section. Only
its callers changed. Every L8/L9/LW refusal elsewhere is untouched, with a gate saying so.

### Three tests were UPDATED, not loosened

`TC4_AnAsymmetricPair_IsRefusedByName…`, `TC4_APairOnTwoDifferentMetalLevels_IsRefusedByName` and
`KernelSeamTests.ThreeSignalConductors_IsRefusedByPointingAtL7bB` each asserted the refusal that
pointed at L7b-b. L7b-b is what they were pointing at, so they now assert acceptance — while keeping
the half that still matters (the geometric check still names both widths / the right reason).

## Out of scope here, on purpose

- **All UI.** No mesh viewer, no EM setup panel, no stackup-editor change, no cross-section
  extraction from `.clay`, no `.snp` on disk.
- **A general non-symmetric complex eigensolver (Route B).** Not built, because M1's measurement did
  not earn it — see the table above. If a case is ever found where Route A's error matters, that
  measurement is the thing to re-take first.
- **Frequency-dependent modal matrices.** Route A's Tv is fixed by the lossless problem and is the
  same at every frequency. A Route B would produce a per-frequency Tv and make mode TRACKING across
  the sweep a real problem rather than a free one — scope that with Route B, not before.
- **Inhomogeneous-medium modal theory beyond quasi-TEM.** A mode here is a quasi-TEM mode; full-wave
  modes are L8.
- **Full-wave** (L8/L9) and **wirebonds** (LW1/LW2).
- **Surface roughness**, **adaptive frequency sampling**, **the N-ceiling of R17** — none binds a
  solver whose matrix is a few hundred square.
- **Stripline** (ground above *and* below). It needs an infinite image series rather than one image;
  a small, well-defined extension, to be added with its own convergence test on the series
  truncation.

## L8a — the layered Green's function (kernel B's foundation)

**A DIFFERENT KERNEL, not an increment on A.** Kernel A's whole design was an escape from this:
carrying bound charge explicitly kept its kernel at the free-space logarithmic potential, so there
were no Sommerfeld integrals, no DCIM and no special functions anywhere in L6/L7. **That escape is
not available for a full-wave planar solver on a grounded slab**, and nothing in this section shares
code with the sections above it. Brief: `docs/sonnet-briefs/brief-L8a-layered-greens-function.md`.
Phase-table row **L8**, first of five slices (L8a…L8e); L8b is the mesher, L8c the fill and the
singular integrals, L8d ports and de-embedding, L8e results and the kernel registry.

**Scope, exactly:** the Green's function and its oracle ladder. No mesher, no basis functions, no
matrix fill, no solve, no ports, no `EmProblem` change, no `IEmKernel` change, no kernel registry.

```
LayeredMedium.cs      GroundedSlab (the D2 stackup + its refusals), StaticGreens (the ω→0 branch)
SpectralGreens.cs     M1 — Γ^h/Γ^e/Γ^q, the MPIE kernels, surface-wave poles, mode counting
SommerfeldIntegral.cs M2 — direct contour integration: the ORACLE, not the product
Dcim.cs               M3/M4 — extraction, Prony fit, complex images, the validated-range refusal
Bessel.cs             J₀/J₁/Y₀/Y₁/H₀⁽²⁾ for complex argument — written, not depended on
```

Gate: **194 routine tests in `tests/Engine.Tests/Mom/` (+25), ~4 s**, plus **5 tagged
`Category=Benchmark`** — see "the tests are tagged for someone else's budget" below.

### D2 — one conductor layer, on the slab's top surface

Source and observer are therefore always at the **same height** z = z′ = h, and the Green's function
is a function of lateral separation ρ alone at each frequency. That collapses a two-variable fit into
a one-variable one and is enough for all three of L8's own gates (a quarter-wave open stub, a bend, a
uniform line). Buried or multi-level metal is refused by name (`GroundedSlab.CanHost`), pointing at
L9. `SpectralGreens.KernelAtHeights` carries the general two-height form anyway — production never
calls it; it exists so Tier 0's reciprocity check asks a real question and so the height dependence
is written down correctly once for whoever lifts this to two metal levels.

### R-lgf-1 — the formulation is MPIE, Michalski-Zheng FORMULATION C, and it was DERIVED

**Nothing here is transcribed, and that is the D4 answer rather than a dodge.** The kernel pair is
derived from Maxwell plus the transmission-line analogy, written out in full in `SpectralGreens`'s
header:

```
  G̃_A = V_i^h/(jωµ₀)              G̃_q = jωε₀ (V_i^e − V_i^h)/k_ρ²
```

obtained by requiring `E = −jωA − ∇φ` to reproduce the spectral electric-field dyadic of a horizontal
dipole. **The xx, yy AND xy components each independently produce the same G̃_q**, which is the
consistency check that the split is legitimate — and it is what makes this formulation C: the vector
kernel is purely TE, the scalar kernel is a genuine scalar with no k_x/k_y left in it.

Names in the literature (Michalski & Zheng, Sommerfeld, Aksun, Prony) are **attribution, not
provenance** — no paper was read and none is being paraphrased. What makes it trustworthy is Tier 1,
not a citation.

**Three algebraic facts that are load-bearing and easy to lose:**

- **Γ^q is NOT Γ^e.** `Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)`. The apparent 0/0 at k_ρ → 0 is removed
  *exactly*: `Γ^e − Γ^h = 2jT k_ρ²(εᵣ−1)/[(jk_z1T + εᵣk_z0)(jk_z0T + k_z1)]`, and the k_ρ² cancels
  algebraically. The obvious implementation is fine at k_ρ ~ k₀ and has lost **every digit by
  k_ρ = 1e-8 k₀** (measured: 0.82 absolute error) — which is exactly where the DCIM sampling path
  starts. Pinned by `T0_4`, including an assertion that the naive form IS ruined there, so the test
  cannot quietly stop demonstrating why the cancellation matters.
- **`tan(k_z1h)/k_z1` is the combination that actually appears**, not `tan` alone. It is even in
  k_z1 (so k₀ is the only branch point in the whole kernel — there is none at k₁) and finite as
  k_z1 → 0 (so k_ρ = k₁ is not a numerical event either). Writing the coefficients in terms of `tan`
  divides by k_z1 somewhere and turns an ordinary point into a 0/0.
- **Γ^q(0) ≠ Γ^e(0).** Γ^e = Γ^h exactly at k_ρ = 0 — both transmission lines have impedance ratio
  1/√εᵣ there — but their difference vanishes as k_ρ² against a k₀²/k_ρ² prefactor, so a finite,
  non-zero limit survives. `T0_4` asserts the two do *not* coincide, because if they did the k₀²/k_ρ²
  term would be missing and everything downstream would still look plausible.

### R-lgf-3 — the surface wave is physics, and the TM₀ mode has no cutoff

A grounded slab **always** has at least one surface wave, however thin (`T3_2`: verified down to
h = 1 µm at 100 MHz). Poles are found by bisecting the lossless transcendental relations —
`u tan u = εᵣ√(U²−u²)` for TM_n on (nπ, nπ+π/2), `u cot u = −√(U²−u²)` for TE_n on
((2n−1)π/2, nπ), with `U = k₀h√(εᵣ−1)` — then moved to the complex root by a secant on the
dispersion residual. **Bisection, not Newton, and that is deliberate**: a carelessly started Newton
converges happily onto the *neighbouring* mode, and the fit then carries two copies of one pole and
none of the other.

Mode counts are cross-checked against the cutoff conditions at every frequency (`T3_3`): FR-4 1.6 mm
predicts TE₁ at **25.404 GHz** and the mode list changes across it, TM₁ at twice that.

**Residues are taken by a circular contour average in w = k_ρ², not by differentiating the
dispersion relation.** The trapezoidal rule on a circle is spectrally accurate for an analytic
function, needs nothing re-derived if the formulation changes, and — unlike a closed-form derivative
— cannot silently disagree with the reflection coefficient it is supposed to be the residue *of*.
`T3_6` checks the thing it exists for: closing in on the pole by 100× multiplies the raw kernel by
100× (0.101 → 9.34) and leaves the regularised one **flat at 8.2e-3**.

### The oracle ladder, and what it actually established

| Tier | What | Result |
|---|---|---|
| −1 | Bessel vs its integral representation; Y from the Wronskian | 2.9e-13 abs; 5.6e-11 rel |
| 0 | Fresnel h→∞, εᵣ=1 PEC image, height reciprocity, the k_ρ→0 cancellation | ≤ 1e-11, reciprocity bit-identical |
| 1 | the Sommerfeld identity alone; **εᵣ=1 → free space + ONE image**; the static image series | 1.9e-9; **3.8e-10**; exact |
| 2 | **DCIM vs direct integration** — the second, independent formulation | the curve below |
| 3 | pole location vs its own dispersion relation, mode counts vs cutoffs, residue regularisation | ≤ 1e-9 |
| 4 | oracle convergence under refinement, DCIM determinism, fit monotonicity | ≤ 6.5e-9; bit-identical |

**Tier 1's εᵣ = 1 reduction is the strongest single check and is the direct analogue of kernel A's
`T0_7`/`T1_2` image gate** — the test that actually validated R-mom-7. With no slab the answer is
`e^{−jk₀ρ}/4πρ − e^{−jk₀R′}/4πR′` exactly, for BOTH kernels, and it needs no external data.

**The oracle is a real-axis contour, and the textbook deformation is wrong here.** J₀(z) grows like
e^{|Im z|}/√|z|, so lifting the path by 0.2k₀ at ρ = 10λ turns an O(1) answer into a difference of
terms of size 3e5. The two genuine difficulties are removed at the source instead: the **1/k_z0
branch singularity by substitution** (k_ρ = k₀ sin θ below the light line, k₀ cosh u above — in both
cases dk_ρ cancels the 1/k_z0 outright, so nothing is ever divided by a small number), and the
**non-decaying tail by extracting the two closed-form pieces first**, leaving a 1/k_ρ² remainder
partitioned at the zeros of J₀(k_ρρ) and summed by repeated averaging.

**The oracle requires a LOSSY slab (tanδ > 0) and says so** — a lossless grounded slab puts its TM₀
pole exactly on the contour. `Dcim` has no such restriction; it extracts the pole analytically. Both
starter substrates are lossy, so the measurement is unaffected.

### R-lgf-4 — WHERE DCIM STOPS BEING ACCURATE, as a number

Measured against direct Sommerfeld integration over ρ/λ ∈ [1e-4, 10], both starter substrates, 2/10/
20 GHz. **Two measures, because they answer different questions and reporting only one misstates the
result in both directions.**

| | FR-4 2 GHz | FR-4 10 GHz | FR-4 20 GHz | GaAs 2 GHz | GaAs 10 GHz | GaAs 20 GHz |
|---|---|---|---|---|---|---|
| G_q worst **relative** | 2.5e-1 | 4.9e-3 | 8.5e-4 | 5.7e-1 | 5.1e-1 | 3.9e-1 |
| G_q worst **scaled** (|ΔG|·4πρ) | 1.9e-3 | 3.8e-3 | 2.4e-3 | 5.7e-3 | 4.4e-4 | 1.5e-3 |
| G_A worst relative | 2.6e-4 | 6.8e-5 | 4.8e-3 | 1.0e-6 | 1.4e-6 | 1.9e-4 |
| relative first > 1e-2 at ρ/λ | 1.8 | never | never | 1e-4 | 1.0 | 1.0 |

- **Scaled error — |ΔG| as a fraction of the free-space kernel at the same ρ — is ≤ 6e-3 across the
  ENTIRE span, everywhere.** This is what a matrix fill experiences (an entry perturbed by
  ε·(1/4πρ) perturbs the linear system by ε), and it is **the number L8c should be scheduled
  against.**
- **Strict relative error is ≤ 1e-2 out to ρ/λ ≈ 1** and degrades beyond, to 0.25–0.57 at ρ/λ = 10.
  `Dcim.WithinValidatedRange` is the R-mom-17 refusal that words this, and it is worded on the strict
  measure deliberately.
- **The gap between the two is real physics, not slack.** G_q has deep cancellation zones — a few
  substrate heights out, charge plus its ground image is a DIPOLE, so G_q falls like h²/ρ³ while its
  constituents fall like 1/ρ; on the 100 µm GaAs slab at 2 GHz |G_q| drops from 687 to 0.0013 over
  half a decade of ρ. A relative error against a quantity that is nearly zero says more about the
  zero than about the method.

### M4 — the far-field defect had an exact cause, and the fix is a theorem

One-level DCIM was excellent in the near field (~1e-6) and reached **187% error at ρ/λ = 10 on
GaAs.** It looks like a fitting problem and is not.

**`1 + Γ(k_ρ)` vanishes identically at the branch point k_ρ = k₀** (Γ^e → +1, Γ^h → −1, so Γ^q → −1
and both `1 + Γ` are zero). Since `e^{−jk₀R_i}/4πR_i → e^{−jk₀ρ}/4πρ` for every image as ρ → ∞, the
coefficient of the 1/ρ far field is exactly `(1 + Γ(∞)) + Σ A_i` — and the physical far field (a
surface wave in 1/√ρ, a lateral wave in 1/ρ²) **has no 1/ρ term at all.** The sampling path never
passes through k_z0 = 0, so an unconstrained fit only *extrapolates* that cancellation, and the
error survives as an uncancelled 1/ρ that eventually dwarfs an answer which has decayed to 1/ρ².

Imposing `Σ A_i = −(1 + Γ(∞))` **exactly** — by eliminating one amplitude, not by a weighted row,
since a weighted constraint still leaves a residual 1/ρ — is the whole fix. The sum-rule residual is
1e-16 in every case and is surfaced as `DcimModel.SumRuleResidual`.

**Higher Taylor orders at the branch point are also exact statements and they make it WORSE, which
is why the default is 1:**

| BranchPointOrders | FR-4 G_q | GaAs G_q | FR-4 G_A | GaAs G_A |
|---|---|---|---|---|
| 0 | 2.6e-2 | 8.2e-2 | 2.6e-3 | 5.1e-4 |
| **1 (default)** | **4.9e-3** | 5.1e-1 | **6.8e-5** | **1.4e-6** |
| 2 | 8.3e-3 | 3.4e+0 | 4.9e-6 | 1.4e-6 |
| 3 | 1.9e-2 | 6.2e+1 | 1.0e-6 | 1.4e-6 |

Orders 2 and 3 pin genuine Taylor coefficients, but as *exact equalities* they fight the sampled
data — the spectral fit residual degrades 100× on GaAs. **Order 1 is the only one that is a theorem
rather than a knob**, and it is the only one kept on by default. They stay reachable because they are
right for G_A, where they buy another 60×.

**Two levels, chosen per problem by measured residual rather than asserted.** One path cannot resolve
both regimes: with T₀ = 300 and 512 samples the step is Δt = 0.59, while the entire small-k_ρ region
that governs the far field lives at t ≲ 0.5 — one sample interval covering all of it. A short path
picks up the large-depth images, the long one the near-field ones. **But two levels are not
unconditionally better**: on the electrically-thin GaAs slab they produce near-duplicate depths, the
combined design matrix goes rank deficient, and the amplitudes come back as enormous cancelling
numbers (a 2e35 fit residual). Three candidate depth sets are therefore scored and the best kept, and
depths are de-duplicated first.

**A finding that repeats L7b-b's exactly, and is pinned for the same reason.** `DcimModel.FitResidual`
— how well the exponentials reproduce the samples they were fitted to — **does not predict the
spatial error.** GaAs's best spectral residual (5.7e-6) belongs to a configuration whose far-field
error is 8e-2, while a configuration fitting 100× worse can be spatially better. It is the honest
measure of what the fit did; it is not a measure of what the answer is worth. Only the oracle answers
that, which is why the oracle exists.

### Two findings about the ORACLES themselves, both caught before they were believed

**1. The static image series had to be COMPLEX, and getting that wrong looked exactly like a
convergence floor.** Written with a real εᵣ, `StaticGreens` sits a frequency-*independent* 1.1e-6
from the full-wave kernel's ω → 0 limit on FR-4 at tanδ = 0.001 — which reads precisely like the
kernel bottoming out, and would have been recorded as one. What ruled it out was refining the
*integrator* 100×: the answer moved by 7e-11 while the discrepancy did not move at all, so whatever
it was, it was not convergence. Nothing in the derivation ever used the realness of εᵣ; with a
complex K the convergence is exactly quadratic — **1.205e-3, 1.337e-4, 1.203e-5, 1.337e-6 at
300/100/30/10 MHz**, ratios 9.0/11.1/9.0 against the 9.0/11.1/9.0 that (f₁/f₂)² predicts.

**2. Before concluding DCIM's far field was wrong, the oracle was checked there.** GaAs at ρ/λ = 10
is the least comfortable configuration this contour has — a TM₀ pole 1.9e-4 above k₀ with an
imaginary part of 6e-8·k₀, i.e. a Lorentzian spike of relative half-width 3e-6 almost on the path,
and an answer 170× smaller than the term it cancels against. Refinement moves it by **1.1e-10**.
The 52% was genuinely DCIM's. This ordering is not optional here — this area has twice found the
closed-form "oracle" to be the wrong one.

### The tests are tagged for someone else's budget, not for their own runtime

5 of the 30 new tests carry `Category=Benchmark` — the full error-curve sweep across the band, and
the oracle refinement sweep. **None of them is slow by the ~5 s rule** (the heaviest is ~3 s). They
are tagged for the reason this file already gives for tagging a *fast* test — "the purpose the
mechanism serves, not the letter of the ~5 s rule" — with the polarity reversed: they are CPU-heavy
work competing with a test that is wall-clock-BUDGETED. `Hero1BTests` gates on a 10 s
import-plus-solve budget; measured under full-solution load it runs **4.2–9.6 s with this phase's
tests excluded** and 7.7–10.1 s with them included, and failed once in each condition. **It is
marginal on this machine independently of this phase** — that was measured, not assumed, by running
the full solution with and without these tests — so its budget has not been touched. But a phase's
own reporting sweep has no business spending another phase's headroom, so the sweeps are opt-in and
the routine gate keeps one case per starter technology.

### Bessel functions: WRITTEN, not added as a dependency (§8.3)

There are no Bessel functions anywhere else in this repository — kernel A needed none. **No package
was added**, because the root `CLAUDE.md` reserves dependency additions to the owner. Nor is anything
transcribed: *Numerical Recipes*-style tables are copyrighted and their licence does not permit it,
and A&S §9.4's polynomial fits are public-domain but real-argument only and good to ~1e-8, which is
not enough for something serving as an oracle. What is implemented is the **defining series** —
ascending power series below |z| = 13, Hankel asymptotic expansion above, with the asymptotic
coefficients `a_m(ν) = Π(4ν²−(2k−1)²)/(m!8^m)` *computed* from that product rather than written down,
truncated at the smallest term. Gauss-Legendre nodes are likewise computed by Newton on the Legendre
recurrence rather than tabulated, for the same reason.

Measured: **J₀/J₁ to 2.9e-13 / 1.1e-13 absolute** against the integral representation
`J₀(x) = (1/π)∫₀^π cos(x sin θ)dθ` over 0.05 ≤ x ≤ 200 (worst exactly at the crossover, as expected);
**Y₀/Y₁ to 5.6e-11** via the Wronskian, which holds for complex argument and which no wrong pair of
functions satisfies.

### Out of scope here, on purpose

- **Everything downstream:** no mesher, basis functions, matrix fill, solve, ports or de-embedding
  (L8b–L8d); no planar problem type, extractor, `EmSetup`, panel or `.snp` (the Ui half); no kernel
  registry and no `IEmKernel` change (L8e).
- **N dielectrics.** One grounded slab, per D2. The general stack is L9 and is where §10.2's warning
  bites hardest.
- **Adaptive frequency sampling.** §10.7 says build it when the kernel that needs it exists.
- **A general complex eigensolver.** GPOF's pole step wants one; L7b-b weighed writing one and
  declined, and the same judgement applies. Classic Prony reaches the same poles through a
  linear-prediction least squares (Householder QR, written here) plus Durand-Kerner rooting, and the
  fit residual is what validates it.

> **A forward warning, because it is a fact about this physics rather than about L8e.**
> **Kernel A's losslessness oracle does NOT carry over to kernel B.** With σ = ∞ and tanδ = 0 a
> closed 2D cross-section is exactly lossless, but an *open* planar structure radiates and launches
> surface waves — both of which carry real power away — so `|S₁₁|² + |S₂₁|² < 1` **legitimately**.
> Reciprocity and passivity carry over; losslessness does not. Whoever writes L8d/L8e must not copy
> `TC2_TheFourPortIsLosslessWhenEveryLossIsIdeal` across and then "fix" the kernel until it passes:
> that would mean suppressing radiation, which is one of the two things L8 exists to model.

## L8b — the surface mesher and the N report

**Second of L8's five slices, and it adds NO PHYSICS.** Its whole job is to turn drawn geometry into
cells, count them, and hand the count back before anything is solved. Nothing here evaluates a
Green's function — that is L8c. Brief: `docs/sonnet-briefs/brief-L8b-planar-mesher-and-overlay.md`.

```
PlanarProblem.cs      PlanarPolygon, PlanarConductorLayer, PlanarAnalyticAlternative, PlanarProblem
PlanarMeshSettings.cs D3's THREE controls, and no more
PlanarMesh.cs         PlanarCell, PlanarBasis, PlanarMesh — and the ORDER contract
PlanarMeshReport.cs   N, counts, sizes, the frequency, the R17 verdict, the notes
SurfaceMesher.cs      the mesher
```

Gate: **22 routine tests in `tests/Engine.Tests/Mom/SurfaceMesherTests.cs`, ~0.3 s**, none tagged
`Benchmark` — the sweeps in this slice are milliseconds, not the CPU-heavy kind L8a had to make
opt-in. Tier 6 and the PCell half of Tier 7 live in `tests/Ui.Tests/Em/PlanarMeshPCellTests.cs`,
because `MBendPCell`/`MKlopfPCell`/`MTaperPCell` are in `src/Ui` and the reference graph is
`Ui → Engine`.

### D1 — the planar problem is a SIBLING of `EmProblem`, not a subtype

`EmProblem` is a **cross-section** model and cannot describe a planar layout; `EmMesh`/`EmSegment`
are 1-D boundary segments, and not one field of `EmMeshReport` (`SegmentsPerInterface`, `InterfaceYs`,
`TruncationHalfExtent`, `WheelerValidAboveHz`, `ConductorMeshTemplate`) means anything for a surface
mesh. So: new types. No shared base, no interface implemented by both, no nullable fields bolted onto
the old ones. R-mom-1 is unchanged — the new type is neutral and in SI units, and the Ui-side
`PlanarExtractor` produces it.

### D8 — the grid model is TENSOR-PRODUCT, and the measurement that decided it

Every gridline spans the whole domain. The alternative — an independent-cell (quadtree-ish) mesh —
refines locally and is far cheaper on mixed-scale geometry, but its non-conforming cell edges make
rooftop pairing genuinely harder, and that difficulty would land on **L8c** rather than here.

**The cost of the choice is measured, not assumed. N for every non-Manhattan shipping PCell, both
starter technologies, 10 GHz, against R17's 5,000 ceiling:**

| Part | PCB 2-Layer | MMIC GaAs |
|---|---|---|
| MBend, mitred | 550 | 536 |
| MTaper 2.9 → 1.0 mm | 714 | 714 |
| MKlopf 50 → 100 Ω, on-axis | 743 | 1,893 |
| MKlopf with Offset | 590 | 2,055 |

**No one-click library part comes near the ceiling.** The brief's own worry — that a tensor grid's
"a fine row anywhere is a fine row everywhere" would blow the budget on a taper — does not
materialise, for a reason worth recording: the spacing is derived **per axis** from the narrowest
run measured **along that axis**, so a taper that is narrow across and long along gets a fine
transverse pitch and a coarse axial one rather than a fine square grid. Isotropic spacing on the
§10.7 taper would have been ~15,000 unknowns; per-axis spacing makes it 714.

### R-msh-1/R-msh-2 — the two things everything downstream assumes

**The mesh tiles its input exactly, for Manhattan artwork**, and the rule that makes it true is
stated once: **a gridline comes from an AXIS-PARALLEL boundary edge, never from a vertex.** A
Manhattan polygon's every edge is axis-parallel, so its grid is conformal and the tiling is exact to
the last DBU. **That is also D9's guarantee**: `MKlopfPCell`'s 194-vertex smooth outline has
axis-parallel edges only at its two end caps, so it contributes exactly two gridlines however finely
it was tessellated. The 96-point artwork tessellation cannot leak into the mesh, because vertices are
geometry to be COVERED, not gridlines to be ADOPTED.

**Cell order is `(LayerIndex, IY, IX)`, compared as integers.** `IX`/`IY` are grid indices the mesher
assigns, not coordinates — there is no floating-point tie to break, and there is no dictionary or set
iteration anywhere on the path that produces them (`cellAt` is a plain `int[]`). L8c's fill, L8d's
port excitation and L8e's heat map all index by this order.

### R-msh-6 — N is BASIS FUNCTIONS, not cells, and R17 is about N

A rooftop spans a *pair* of adjacent cells, so N is the number of shared internal edges — about 2×
the cell count on a rectangular grid. Reporting cells while budgeting basis functions is a factor-of-2
error in the one number this slice exists to produce. N is what R17 refuses on, because it is the
matrix dimension: N² × 16 bytes.

### R-msh-5 — kernel A's grading code IS reused; its FINDING is not, and here is why

`BoundaryMesher.PartitionFractions` and `GeometricSlopeFor` are called directly, and R-mom-8's own
claim — that the cell-size field was written against segment geometry rather than against "the
microstrip case", so B and C could reuse it — **holds**. What does not carry over is R-mom-8's
headline finding. Kernel A's edge cell is a fraction of the metal **thickness** because in a
cross-section the charge singularity lives at the 90° corner, whose scale is the thickness. **A
planar surface mesh sits on a zero-thickness sheet** (D2 puts the metal on the slab's top surface),
so there is no thickness to be a fraction of and §10.5's original "a small fraction of the width" is
the right rule here — kernel A's deviation from it existed only because a cross-section has a second,
much smaller dimension.

**Measured, N under both candidate references at 10 GHz:**

| | conductor-width reference (default) | cell-size reference |
|---|---|---|
| FR-4 hero (2.9 × 20 mm) | c₀ = 87 µm, **N = 552** | c₀ = 21.4 µm, N = 787 |
| GaAs hero (72 µm × 2 mm) | c₀ = 2.16 µm, **N = 705** | c₀ = 0.54 µm, **N = 7,562 — over the ceiling** |

The cell-size reference **exceeds R17 on an ordinary MMIC line**, because the wavelength cell is huge
relative to a 72 µm conductor and 3% of it is absurdly fine. The conductor-width reference is the
default and `PlanarEdgeReference` is kept only as the measurement seam.

**What is NOT available at L8b, said rather than faked:** the CONVERGENCE half of R-mom-8's
measurement. Kernel A could compare ε_eff against its own converged limit because it had a solver;
this slice has none, by design. That comparison belongs to L8c.

### The growth ratio is DERIVED, and the knife edge that forced it

The first implementation fixed r at §10.5's 1.7 and stopped grading at the accumulated distance
`c₀(rⁿ−1)/(r−1)`. That is **wrong in a way that only shows up under translation**: the marcher's own
steps ARE that geometric series, so it lands EXACTLY on the cutoff and whether the next cell is graded
or bulk is decided by the last bit of a floating-point comparison. Moving the same rectangle 3.7 mm
flipped it and changed the mesh by 33% (256 → 340 cells). Deriving `r = (h_max/c₀)^(1/EdgeCells)`
instead makes the size field continuous and the knife edge disappears rather than being tolerated.
**Tier 3's translation test is what found it**, and the mesher is now exactly translation-invariant —
the grid is anchored to the ARTWORK, not to the world origin, which is a stronger property than Tier 3
asked for and is what makes a design's mesh independent of where it was drawn.

### D2 — staircasing, MEASURED on real library geometry

Diagonals and curves are staircased; conformal cells and triangles are not built. The measurement is
on the shipping PCells because those are what a user selects, and because **L8's own phase gate is not
all-Manhattan** — "a bend's s-parameters are physically sane", and `MBendPCell` cuts a 45° mitre.

**The mitre survives, which is the load-bearing result.** At the mesher's own auto cell size (704 µm)
the staircased cut is 1.357 mm² against a true 1.396 mm² — **2.8% area error, 18 cells removed, N 550
against the unmitred 586**. A staircased mitre is therefore still distinguishable from an unmitred
bend, which is exactly what R-pc-18 says the two discontinuities are.

| cells/λ | cell size | mitre-cut area error | cells removed |
|---|---|---|---|
| 10 | 714 µm | 13.6% | 19 |
| 20 (auto) | 704 µm | **2.8%** | 18 |
| 40 | 353 µm | 0.7% | 31 |
| 80 | 177 µm | 6.7% | 65 |

**The error is not monotone in cell size, and that is the staircase's own signature** rather than a
defect — the error depends on how the grid happens to align with a 45° edge, not only on how fine it
is. Refining helps on average and does not help reliably at any single step.

**The smooth tapers are the finding that matters more.** Global area error is 0.47–0.59%; **local
width error is 17–24% worst and 5.5–11% RMS**:

| | worst local width error | RMS | global area error |
|---|---|---|---|
| MTaper 2.9 → 1.0 mm | 23.6% | 10.4% | 0.47% |
| MKlopf on-axis | 20.9% | 11.2% | 0.59% |
| MKlopf Offset 5 mm | 17.2% | 5.5% | 0.54% |

A Klopfenstein taper's whole value is a controlled equiripple |Γ| — `GammaMax` = 0.05 in the shipped
default. A 21% local width error is enormous against that, while the 0.6% area error says nothing
about it. **This is why the brief asked for the local number.**

### Out of scope here, on purpose

- **Basis functions, matrix fill, singular and near-singular integrals** — L8c, and §10.2's *second*
  place a schedule dies. Reporting the NUMBER of basis functions is not the same as defining them.
- **Any solve, any port, any de-embedding** — L8d. **The current-density heat map** — L8e, because it
  needs a solution; the provision made for it here is one per-cell scalar on
  `LayoutRenderer.DrawPlanarMeshOverlay` and nothing else.
- **The kernel registry, any `IEmKernel` change, any `EmCapabilities` widening** — L8e (D7).
  `EmCapabilities` already has a `Planar` flag and did not need widening.
- **RWG / triangles / a Delaunay triangulator** (D2); **N dielectrics, vias, z-directed current** (L9);
  **adaptive frequency sampling** (§10.7 says build it when the kernel that needs it exists).

## L8c — basis functions, the matrix fill, and the singular integrals

**Third of L8's five slices, and §10.2's SECOND place a schedule goes to die** — the design note names
item 4 (singular self- and near-term integrals) as the other one, after DCIM. Brief:
`docs/sonnet-briefs/brief-L8c-fill-and-singular-integrals.md`.

```
PlanarBasisFunctions.cs  the rooftop: evaluation, divergence, support, the two halves
RectangleIntegrals.cs    SIX closed-form inner integrals + their derivations in the header
SingularExtraction.cs    which terms are extracted, the stable remainder, the radial table
PlanarFill.cs            the geometric cores (D6), P over cells (D4), Z over bases, the rules
PlanarSystem.cs          dense storage, the NumFlat LU, R17's pre-allocation refusal, the sweep
```

Gate: **297 routine tests in `tests/Engine.Tests/Mom/` (+81), ~11 s**, plus **17 tagged
`Category=Benchmark`** (~6 min: the oracle sweep, the convergence studies, R-fil-12's measurement and
the whole of Tier 8). `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` unchanged and green;
nothing outside `src/Engine/Mom/` was touched.

### The three singular pieces, and the second one is what gets missed

`DcimModel.Evaluate` returns three terms and **only the first is the one every MoM text warns about**:

```
G(ρ) = (1 + QuasiStatic)·e^{−jk₀ρ}/(4πρ)                     ← 1/ρ
     + Σ_poles  Residue·(−j/4)·H₀⁽²⁾(k_p ρ)                  ← ln ρ  — NOT optional
     + Σ_images A_i·e^{−jk₀R_i}/(4πR_i), R_i = √(ρ²+b_i²)    ← smooth ONLY IF b_i is not small
```

`H₀⁽²⁾ = J₀ − jY₀` and `Y₀(z) → (2/π)(ln(z/2)+γ)`, so **every surface-wave term carries a real
logarithmic singularity with coefficient −Residue/2π**, and a grounded slab always has at least one
surface wave (L8a's R-lgf-3, verified to h = 1 µm). `SingularExtraction` names, in code, which term of
`DcimModel.Evaluate` each extracted coefficient comes from, so a future change to the DCIM's own
decomposition cannot silently leave one unextracted (R-fil-3).

### R-fil-8 — "the images are smooth" IS FALSE ON THE FR-4 STARTER, and that is the finding

D3's third bullet is a condition, not a fact, and the measurement says it fails where it matters:

| | min\|b_i\| | smallest cell | **ratio** | images |
|---|---|---|---|---|
| FR-4 1.6 mm, 2 GHz | 173.5 µm | 84.5 µm | 2.05 | 17 |
| FR-4 1.6 mm, **10 GHz** | 13.9 µm | 84.5 µm | **0.165** | 20 |
| FR-4 1.6 mm, **20 GHz** | 6.69 µm | 84.5 µm | **0.079** | 20 |
| GaAs 100 µm, 2/10/20 GHz | 29.1 / 22.7 / 13.2 µm | 2.16 µm | 13.5 / 10.5 / 6.1 | 19 / 14 / 16 |

**On FR-4 above ~5 GHz a fitted image sits an order of magnitude closer to the metal plane than a cell
is wide.** Its own `1/√(ρ²+b²)` is then nearly singular across the cell, so the "smooth" remainder is
not smooth on the mesh's own scale. That is not a DCIM defect — the *function* is accurate to L8a's
≤ 6e-3 everywhere, the individual images cancel — but the remainder inherits enough structure that a
3-point rule is 5% wrong. **`RemainderNodesNear` is 8 rather than 3 because of this measurement**, and
that is the whole content of "the quadrature must know".

The failure was found the expensive way and is worth the warning: on FR-4 at 20 GHz the self entry
converged like n^-2.2 (165.15 / 159.59 / 157.68 / 157.04 / 156.84 at n = 3/5/8/12/16 against a true
156.64), i.e. it looked converged at every step. **The extraction ORDER is not the lever here** —
orders 0 and 2 agree to 1e-4 while both are 5% out. Only the remainder quadrature moved it.

### Six closed forms, derived, and one .NET library bug found on the way

`RectangleIntegrals` carries `∫∫dS/R`, `∫∫ln r dS`, `∫∫r dS` and the three first moments
`∫∫u·(…)dS` that a rooftop's linear weight needs, each over a rectangle for an in-plane observation
point. All are derived in the file header from antiderivatives (the D4 rule L8a followed), and all six
are checked against an independent graded adaptive quadrature to **1e-12** at an interior point, an
edge, a corner, far outside, and a 1 : 1e4 sliver (Tier 1, 54 tests). Two hand-checkable values are
asserted: the centre of a unit square gives `4·asinh(1) = 3.5254943`, and `∫∫r` from a corner gives
the mean corner distance `(√2 + asinh 1)/3 = 0.7651957`.

**Working from a CORNER primitive rather than from a raw antiderivative is what makes interior / edge
/ corner one case rather than three** — a naive corner-summed antiderivative divides by zero exactly
where the observation point lands on a gridline, which on Manhattan artwork is not a rare event.

**`double.LogP1` in .NET is NOT a true log1p** — measured, not assumed: at x = 1e-8 it returns
9.99999989e-9 against the correct 9.99999995e-9, i.e. it is `Log(1+x)` with the addition's rounding
left in. That is invisible almost everywhere and showed up here as a 1e-9 error in the log first
moment on a high-aspect-ratio cell. `RectangleIntegrals.Log1P` is the Kahan form
(`Log(u)·x/(u−1)`), and `Math.Log(Hypot(a,b))` is likewise replaced by `ln(max) + ½·ln(1+(min/max)²)`
because the radius rounds to `b` long before the term stops mattering. A 1 : 1e4 aspect ratio is an
ordinary edge-graded cell, not a contrived fixture.

### The independent oracle is a CROSS-CORRELATION, not a second corner rule

`tests/…/Support/PlanarPairOracle.cs`. Substituting `r′ = r − (u,v)` and doing the `r` integral first
turns the 4-D Galerkin integral into

```
∫_a ∫_b w_a(r)·w_b(r′)·f(|r−r′|) dS′dS  =  ∫∫ C_x(u)·C_y(v)·f(√(u²+v²)) du dv
```

with `C` the cross-correlation of the two weight profiles — which for a rooftop is the correlation of
two linear ramps and is exact under a 4-point Gauss rule. It **evaluates no antiderivative, no corner
sum and no closed form of any kind**, and collapsing to 2-D is what lets a graded quadrature reach
1e-12 on a self term where a brute-force 4-D rule would not pass 1e-4. This is D3's standing rule in
this area for the fourth time (kernel A's meshed ground plate, L7b-b's closed-form eigen-decomposition,
L8a's Sommerfeld contour).

### The oracle ladder, and what it established

| Tier | What | Result |
|---|---|---|
| 0 | basis: ∫∇·f dS, continuity, zero on outer edges, X⊥Y pointwise | **exact**, asserted as equalities |
| 1 | six closed forms vs adaptive quadrature, five placements | ≤ 1e-12 |
| 2 | matrix entries vs **direct Sommerfeld integration** | the table below |
| 3 | the whole fill at εᵣ = 1 vs the correlation oracle | **5.0e-6**; DCIM-at-εᵣ=1 vs closed form 1e-7 |
| 4 | symmetry bit-identical, D5's block-diagonality, D4's exactness, mirror permutation | exact / 5e-6 / 1e-9 |
| 5 | plate over ground → ε₀A/h; isolated plate by Richardson | trend confirmed; 0.36715 |
| 6 | mesh, quadrature and extraction-order convergence, separately | see below |
| 7 | determinism, and D6's counter | bit-identical; count = 1 at 3 and 101 points |
| 8 | cost and memory at N = 552 / 1,956 / 4,933 | see below |

**Tier 2, the scaled measure (|ΔZ| as a fraction of the free-space entry — L8a's own R-lgf-4 metric,
because G_q's far entries sit in a cancellation zone and a strict relative error there says more about
the zero than about the method):**

| | 2 GHz | 10 GHz | 20 GHz |
|---|---|---|---|
| FR-4 1.6 mm | 5.8e-7 | 1.3e-4 | 2.9e-3 |
| GaAs 100 µm | **5.4e-3** | 9.1e-7 | 4.9e-7 |

**Every case is inside L8a's own ≤ 6e-3 kernel accuracy, and the worst one IS that kernel error** —
GaAs at 2 GHz measures 5.05e-3 pointwise at the same ρ. Against the εᵣ = 1 reduction, where the kernel
is exact and only the quadrature can be wrong, the fill is **5.0e-6**. So the fill is three decades
more accurate than the kernel it fills from; **chasing the quadrature further is wasted work, and
saying so is part of the deliverable.**

### The ORACLE was wrong first, for the third time in this area

Tier 2's first run reported 2.4e-2 on FR-4 at 20 GHz. Built to exactly the mesh's extent, the
Sommerfeld radial table's last Catmull-Rom stencil has to clamp its forward sample and degrades to
near-linear: measured at **2.1e-3 scaled error at ρ = 2 cells, against a DCIM error of 4.2e-6 at the
same ρ**. That reads exactly like a kernel failure. Building the table three times as far keeps every
query strictly interior; `T2_4` then pins it — tripling the sampling density (16 → 48 points/decade)
moves an entry by 3.7e-6. **The ordering is not optional here**: check the oracle, then conclude.

The table samples `S(ρ) = 4πρ·G(ρ)` on a LOGARITHMIC grid rather than G on a linear one. S is O(1)
and bounded including at ρ → 0, and it is exactly L8a's scaled measure, so an interpolation error in
it is term-for-term an error in the quantity R-lgf-4 reports.

### D4/D5/D6, and what each actually bought

- **D4 — the scalar block from a per-CELL matrix.** `∇·f` is a pulse of ±1/Area, so
  `Z^φ[m,n] = (1/jωε₀)·Σ s_a s_b P[a,b]` with `P` built once over ~N/2 cells. Exact, ~4× fewer scalar
  integrals, and `P` **is** the electrostatic potential-coefficient matrix — which is the only reason
  Tier 5's capacitance gate is reachable at all. A structural consequence worth not "fixing":
  `s_A + s_B = 0`, so **any ρ-independent part of G_q contributes exactly zero to the scalar block**.
- **D5 — the vector block is block-diagonal by direction.** Tested as a formulation fact (T4_2), with
  the mixed pair's SCALAR block asserted non-zero so the test cannot pass for the wrong reason.
- **D6 — the frequency-independent core is the four GEOMETRIC integrals**, not the kernel: the
  extraction factors into `C(ω) × ∫∫w_a w_b·{1/R, ln r, 1, r}`, and only the smooth remainder is
  redone per frequency. Enforced by `PlanarSweepResult.CoreFillCount`, asserted at exactly 1 for a
  3-point AND a 101-point sweep — an INSTANCE property, not a static, because a static counter makes
  the test flaky the moment two fill tests run concurrently. **What it bought is measured below and
  it is not a small fraction: 62% of a single-frequency solve at the hero size.**

### The quadrature rule, reported rather than hidden (R-fil-5)

Keyed on τ = centroid separation ÷ larger cell diagonal:

| τ | rule | inner integral |
|---|---|---|
| 0 (self) | 10-point Gauss/axis on **4×4 Chebyshev-clustered** panels | CLOSED FORM |
| < 1.6 | 10-point on 3×3 clustered panels | CLOSED FORM |
| < 4 | 5-point, 1 panel | CLOSED FORM |
| ≥ 4 | 3-point, 1 panel | CLOSED FORM |
| remainder | 8 / 4 / 2 points per axis (near/mid/far), **both** inner and outer | quadrature |

**The panels are Chebyshev-clustered (`t_k = ½(1−cos(πk/p))`), and that is worth three decades.** The
outer integrand `∫_b dS′/R` has a log-divergent GRADIENT on ∂b, which for a self or touching pair lies
on the outer domain's own boundary — where a Gauss rule is weakest. Clustering makes the end panel
O(1/p²) wide instead of O(1/p). Measured against the unit square's `2.9732096`: uniform panels at 12
nodes give 2.8e-5 (1 panel), 3.1e-6 (3), 7.8e-7 (6); clustered, 4 panels at 10 nodes already reach
1.3e-6. **The limiting case is not the self term but a face-TOUCHING neighbour** (self 4.8e-8 against
touching 9.7e-6 at 2 panels), because the self term's singular line is spread over all four sides while
a touching pair concentrates it on one.

**Extraction order is a MEASUREMENT and the answer is "order 1".** On a fixed mesh all three orders
agree with the converged answer to 5e-9; on the one case where the fill is genuinely stressed they
agree to 1e-4 while both are 5% out. Order 2 costs the `∫∫r` and `∫∫u·r` cores in time and memory and
buys nothing, so it is reachable and not default — L8a's own branch-point-order table, with the same
conclusion.

The remainder is evaluated by SUBTRACTION with a ρ floor of `1e-8 × smallest cell`, because an outer
and an inner Gauss rule of the same order on the same cell share nodes EXACTLY and ρ = 0 is otherwise
reachable. Below the floor the analytic limit is returned; the cancellation error the subtraction
costs is bounded by `(d/(n·ρ))·ε`, i.e. 1e-9 even at ρ = 1e-8·d.

### R-fil-12 — R-msh-5's DEFERRED HALF, CLOSED, and the conductor-width default SURVIVES

L8b measured N under both candidate edge reference lengths and said the convergence half "needs a
solver and belongs to L8c". Measured on the static capacitance of §10.7's own FR-4 hero over its
ground plane (εᵣ = 1, so the kernel is closed form), refining each candidate along its own ladder:

| cells/λ | ConductorWidth | | CellSize | |
|---|---|---|---|---|
| | N | C (fF) | N | C (fF) |
| 20 (default) | **552** | 777.43 | 787 | 779.65 |
| 30 | 864 | 777.43 | 1,184 | 779.93 |
| 45 | 1,506 | 777.16 | 1,957 | 780.06 |
| extrapolated | | 777.43 | | 780.18 |

**The two agree in the limit to 0.354%, and at the DEFAULT mesh the conductor-width reference is 0.18%
from the consensus against the cell-size reference's 0.11% — with 30% fewer unknowns.** The reference
is each candidate's own refinement limit and not a uniform mesh, deliberately: a uniform mesh does not
resolve the 1/√d edge current at all, converges from below at order ~0.5, and its extrapolation would
flatter whichever candidate happened to be coarser.

**One honest caveat, because it is a fact about the mechanism rather than about the number.** The
conductor-width reference's edge cell is ~85 µm at every cells/λ, so refining the sweep does not refine
the edge: its sequence is flat because it is already at its own limit, not because it is converging to
the truth. The cell-size reference's edge cell does shrink (21 → 14 → 9.5 µm) and it is still rising at
cells/λ = 45. The true value is therefore probably nearer 780 than 777, and the conductor-width default
sits ~0.35% low on this quantity. **That is inside any EM tolerance, and it is what keeps an ordinary
GaAs line under R17** (L8b: the cell-size reference measures N = 7,562 there, over the ceiling). The
default stands; the mechanism is now on the record rather than assumed.

### Tier 8 — the cost, and it is a FINDING rather than a pass

Measured on this machine at 10 GHz on FR-4, `Order = Constant`, default rule:

| N | cells | `Dcim.Fit`/freq | cores (ONCE) | fill/freq | LU/freq | matrix | cores | **per freq** | **101 pts** |
|---|---|---|---|---|---|---|---|---|---|
| **552** (§10.7's hero) | 297 | 0.21 s | **2.87 s** | 1.48 s | 0.04 s | 4.6 MB | 2.4 MB | **1.73 s** | **178 s** |
| 1,956 | 1,012 | 0.20 s | 13.9 s | 6.80 s | 2.08 s | 58 MB | 30 MB | 9.08 s | 931 s |
| 4,933 (≈ R17) | 2,520 | 0.20 s | 53.9 s | 21.8 s | 42.8 s | 371 MB | 188 MB | 64.8 s | 6,599 s |

**What dominates is the fill, not the LU, right up to the ceiling** — the O(N²) singular cores and the
O(N²) smooth remainder together are **114×** the O(N³) LU at N = 552 and are still **1.8×** it at
N = 4,933.
§10.7's "solve is O(N³) LU per frequency" is true and is not yet the constraint; the crossover has not
been reached inside R17's own budget. `Dcim.Fit` costs ~0.2 s per frequency regardless of N, which is
negligible at the ceiling and is 12% of a hero frequency point.

**D6's answer to "what fraction does the reused core represent": 62%** of a single-frequency solve at
N = 552 (2.87 s of 4.65 s) and 45% at the ceiling. It is not a small fraction and it was worth doing.

**The finding, stated plainly rather than left in L8d's lap.** A 101-point sweep of §10.7's own hero
takes **~3 minutes**, not the "instant" its 4 MB matrix suggests. That is not a §10.10 failure — R18's
30-second target is an *interaction* budget for configuring a sim (D5 removed the port-placement row
from it entirely) and is unaffected — but it does mean **an interactive full-wave sweep at L8d/L8e will
need one of two things**, and the cheaper one is identified rather than merely wished for:

1. **Per-cell-pair moment caching for the vector remainder.** Each same-direction basis pair currently
   integrates four cell pairs independently, and adjacent rooftops share cells, so the same cell pair
   is integrated up to four times. Caching the 2×2 ramp-moment form per cell pair is a ~4× saving on
   the dominant term. It costs `4 × M²/2` complex, which is affordable at the hero (2 MB) and is NOT
   at the ceiling (200 MB) — so it wants to be blocked, which is why it was not done here.
2. **Adaptive frequency sampling**, which §10.7 already defers to "when the kernel that needs it
   exists". It now exists.

**Memory: D6's cached cores add 51% on top of §10.7's own table.** The table says 400 MB at N = 5,000;
the measured matrix is 371 MB at N = 4,933 and the cores add 188 MB, for 559 MB resident and ~1.9 GB
allocated in total across the run. §10.7's ceiling is therefore optimistic by half a matrix, which is
worth knowing before someone believes 5,000 is comfortable.

### Tier 5's two rungs, neither compared against a transcribed constant

- **A plate over ground at εᵣ = 1**, 10×10 uniform, `C/(ε₀A/h)` = **1.968 / 1.517 / 1.278** at
  h/W = 0.20 / 0.10 / 0.05. The parallel-plate value is an asymptote, so the TREND is the test — the
  fringing excess must fall as h/W does, and it does.
- **An isolated square plate** (no ground, no slab): 39.496 / 40.111 / 40.447 fF at 6/12/24 cells per
  side, observed order 0.87, **Richardson limit 40.851 fF ⇒ C/(4πε₀w) = 0.36715**. Reported as its own
  refinement sequence rather than against a remembered number; it happens to agree with the classical
  square-plate constant to three figures, which is corroboration and not the test.

Tier 6 separates the two error sources that a single convergence study would conflate: on a FIXED mesh
the answer moves by **5.2e-9** across three quadrature levels, so the quadrature error and the
discretisation error are genuinely separable and each can be reported honestly. The capacitance itself
converges at order 0.45 → 1.34 on a uniform mesh — slow, because a uniform mesh does not resolve the
edge current, which is exactly what the edge mesh exists for.

One structural result worth keeping: **`P` is EXACTLY invariant under subdivision.** Because it is
area-averaged, the mean coefficient between a fixed pair of regions is identical however those regions
are cut up (verified to 1e-12 over a 4 → 32 refinement). That is a strong check on the area
normalisation, and it is also why "individual entries converge under mesh refinement" had to be asked
of a physical matrix entry — a raw `P[a,a]` grows without bound as its cell shrinks and has no limit to
converge to.

### Out of scope here, on purpose

- **Ports, excitation, de-embedding, s-parameters, any `DataSet`** — L8d. The one excitation built is
  Tier 5's static harness, and it lives in the TEST project rather than in the engine (D8): it is
  assembled from `PlanarFill.ScalarPotentialMatrix`, which IS a product surface.
- **The current-density heat map** — L8e. L8b's provision on `LayoutRenderer.DrawPlanarMeshOverlay` is
  untouched and wired to nothing.
- **The kernel registry, any `IEmKernel` change, any `EmCapabilities` widening** — L8e.
- **Any change to `SurfaceMesher`, `PlanarMesh` or the cell/basis ordering.** The fill indexes by
  L8b's order throughout and did not want a different mesh.
- **Conformal or diagonal boundary cells** (D9); **ACA, MLFMM, any matrix compression**;
  **adaptive frequency sampling** — all still §10.7's, and Tier 8 above is the first measurement that
  says which of them will be needed first.
- **A new dependency.** The Gauss-Legendre nodes are computed by Newton on the Legendre recurrence, as
  L8a's were and for the same recorded reason; `SommerfeldIntegral`'s own private copy was left alone
  because the duplication is nine lines and the two have different lifetimes.

## L8d — ports, excitation, the per-frequency solve, and de-embedding

**Fourth of L8's five slices, and the first one that produces a number a user would recognise.** L8c
filled and factored a matrix nobody excited; this is the right-hand side it was waiting for, plus the
two-line calibration §10.6 says is mandatory and R-mom-15 says is *"real work at L8"*. Brief:
`docs/sonnet-briefs/brief-L8d-ports-and-de-embedding.md`.

```
PlanarPort.cs        the port type, its resolution onto L8b's mesh, the refusals
PlanarExcitation.cs  B, the multi-RHS solve, Y = BᵀZ⁻¹B, the raw S, the line current
PlanarCalibration.cs the SYNTHESISED standards, γ from a 2×2 trace, the separation set
PlanarDeembed.cs     the error box, both branch resolutions, the P-port peel, C_pul and Z_c
PlanarSolve.cs       the kernel-per-frequency cache, the calibrator, the sweep driver
```

Gate: **+29 routine tests in `tests/Engine.Tests/Mom/`**, plus **7 tagged `Category=Benchmark`**
(the A-vs-B sweep, both Z_c tiers, the stub, the feed-length study and the cost run). `Ui.Tests` and
`Firewall.Tests` unchanged; nothing outside `src/Engine/Mom/` was touched.

### D1 — a port is an INCIDENCE MATRIX, and reciprocity is structural for the third time

L8c normalised the rooftop to **unit total current across the shared edge**, so the reaction of a
delta-gap of *v* volts with the basis spanning that edge is exactly *v* — no gap width, no aspect
ratio, no quadrature. With `B` the N×P matrix carrying ±1 on each port's row:

```
V = B·v,   i = Bᵀ·I   ⇒   Y = Bᵀ Z⁻¹ B      P back-substitutions, ONE factorisation
```

**Say the strength precisely, as L7b-b insists:** `Z` is symmetric bit for bit (L8c computes m ≤ n and
mirrors), so `Y` is symmetric because `BᵀZ⁻¹B` is — but it passes through an LU, so it is symmetric to
that routine's tolerance (measured 5.3e-18) and **not** bit for bit.

**The same `B` is used to excite and to read the current back**, because the pair (v, i) has to be
energy-conjugate for `Y` to be an admittance. A code that impresses +1 everywhere and reads back with a
side-dependent sign produces a hard π in S₂₁ — smooth, plausible, and invisible in a magnitude plot.

### D2/D3 — the port cut, and the one thing about it that is a genuine limitation

The reference plane is the shared edge of the two outermost cells, one cell in from the drawn metal;
the half-cell beyond it is part of the error box. Nothing is user-positionable, because all of it is
what the calibration removes. The ground reference is the slab's ground plane, always; a coplanar or
differential reference is refused by name pointing at L9.

**The consequence, stated because it is not obvious and it bounds the low-frequency end.** L8c's D2
forbids a basis with current at the metal's rim, so a port here is necessarily a *series* delta-gap
whose other terminal is the floating outer cell — i.e. the source sees that cell's capacitance to
ground in series. That is a legitimate and standard port model and de-embedding removes it exactly,
but it makes `a₂₁ ∝ ω` at low frequency, and the de-embedding divides by `a₂₁²`. Measured on the FR-4
hero: raw |S₂₁| is 0.045 at 2 GHz and 0.47 at 10 GHz, so the error amplification is ~22× at 2 GHz and
grows as f⁻² below it. **A true edge port — a half-rooftop at the boundary carrying the injected
current — would not have this floor, and it is the one thing in this slice that a later phase might
legitimately revisit.** It was not built here because L8c's D2 forbids the basis it needs.

### D4 — the standard is CONSTRUCTED from the DUT's own mesh, not re-meshed

The calibration is exact only insofar as the error box is the SAME OBJECT in the DUT and in the
standard. L8b's grid spacing is derived from the whole problem's narrowness per axis, so a bare
rectangle and the feed of a stepped line do **not** get the same cells — and the difference reads
exactly like a convergence problem. `PlanarCalibration.BuildLine` therefore takes the DUT's transverse
gridlines verbatim, the DUT's own longitudinal run for the first K cells inward (mirrored at the far
end), and the DUT's bulk cell to fill the middle. `SurfaceMesher` is untouched; R-msh-2's ordering is
honoured by construction; R-prt-5 asserts the result on coordinates as an equality.

**The trap is real and was fallen into.** The feed-invariance test first reused a calibration built
from a plain line on a *stepped* DUT and the "invariant" answer moved by 1.8e-1. Building the standard
from the DUT's own port resolution brought it to 2.5e-2.

### D5/D6 — γ and the error box, both in closed form, and THREE branch decisions

`M = T₂T₁⁻¹` has `det M = 1` for reciprocal standards, so **`cosh(γΔℓ) = ½·tr(M)` exactly** — no
discriminant, no eigensolver. The error box follows from the two standards in closed form
(`PlanarDeembed`'s header carries the algebra). Three branch decisions, and each was measured rather
than assumed:

1. **γ's 2π.** Anchored at the lowest frequency and continued upward by predicting βΔℓ from the last
   point scaled by frequency.
2. **The sign of γ itself.** The obvious rule — negate if `Re γ < 0`, since a passive line has α ≥ 0 —
   **is wrong, and it was a real bug.** Negating flips β too. On FR-4 at 20 GHz the principal value came
   back as (−0.061, +2.2005) against a true αΔℓ of +0.016: α is two orders of magnitude smaller than β,
   so its extracted *sign* is noise, and flipping on it turned a correct β = 804 into 1492. **β selects
   the branch; Re is only a tiebreak when there is no prediction.**
3. **`a₂₂ = ±√(a₂₂²)`,** decided by the REDUNDANT M₁₁ equation — the two standard lengths must give the
   same a₁₁, and flipping a₂₂ flips the correction. Measured: the chosen sign is consistent to 2e-16
   and the rejected one to 2.08, so it is decided by information rather than by noise, and both
   residuals are reported.

**`a₂₁ = ±√(a₂₁²)` is a different problem and it CANCELS for identical ports** — the peel divides by
`a₂₁(i)·a₂₁(j)`, which for an identical pair is `a₂₁²`. It does **not** cancel for unequal ports, where
it is a hard π in S₂₁; there it is carried across frequency by continuity. Both halves are pinned as
algebra (`T4_3`), not through a solve that could hide either.

### D7 — the de-embedded S is referenced to the LINE'S OWN Z_c, and the calibration cannot find it

This is a fact about the method. The algebra assumes the section between the planes is a *matched*
line, which is only true in its own Z_c. Consequences, all load-bearing:

- **The de-embedding's accuracy and Z_c's accuracy are SEPARABLE and are reported separately.** Tier 4
  lives entirely in the Z_c reference and is blind to Z_c's value; Tier 5 is about nothing else.
- **`Z_c = γ/(jωC_pul)`, with C_pul from DIFFERENCING the two standards' static capacitances**, so both
  end effects cancel exactly rather than being neglected. `C(ℓ)` is L8c's `ScalarPotentialMatrix` at
  ω → 0, promoted from the test project because D7 needs it in production.
- **Kernel A is the ORACLE for Z_c, never an input.** Feeding A's C_pul into B would make the phase
  table's own "A and B agree on a uniform line" gate a tautology.
- Renormalisation is `RFNetwork.SToS` and nothing else (R-mom-14, again).

### THE FINDING: what limits de-embedding accuracy is RADIATION, not the algebra

The de-embedded S of a uniform section is **exact at the two lengths the calibration was solved from**
— machine zero (8.5e-16), because four equations fixed four unknowns — and drifts away from them:

| ℓ (FR-4, 10 GHz) | 5.1 h (the standard) | 8.6 h | 10.3 h |
|---|---|---|---|
| \|S₁₁\| on a section that should be matched | **8.5e-16** | 6.0e-3 | 1.7e-2 |

Two measurements say what it is, and neither says "use a longer standard":

- **It is not monotone in the standard length.** β reads 402.2 / 400.0 / 398.2 / 395.9 / 394.2 / 399.0
  for standards of 5.1 / 6.8 / 8.6 / 12.0 / 15.4 / 23.9 substrate heights — a ±1% wander, not a
  convergence. An evanescent tail would decay monotonically.
- **It scales with FREQUENCY as roughly f².** On a third line the residual is 3.9e-4 at 2 GHz against
  6.0e-3 at 10 GHz — 15× for a 5× frequency ratio. An evanescent tail does not do that.

Both are the signature of **direct radiative and surface-wave coupling between the two ports**, which
decays only algebraically and beats against the guided mode because k₀ ≠ β — which is exactly why the
length dependence oscillates. It is the same physics L8a warned about when it said losslessness does
not survive into kernel B: a calibration that models the structure as "box + matched line + box" has no
term for the part of the field that goes *around* the line rather than along it. Real planar tools
suppress it with box walls or absorbing boundaries; this kernel has neither, by design.

**Practical consequence, stated as a number rather than a worry:** a de-embedded answer is good to a
few 1e-3 at 2 GHz and a few 1e-2 at 10 GHz on 1.6 mm FR-4, and the error accumulates with the electrical
length between the reference plane and the discontinuity. That is why `T4_5` gates |S₁₁| and |S₂₁| —
properties of the discontinuity — and reports ∠S₂₁ separately: moving a plane by Δ multiplies S by
e^{2γΔ}, so any residual in γ shows up there as βΔ and gating it would be gating γ under another name.

### R-prt-6 — how many standards a band needs, DERIVED and then MEASURED

One line separation covers a band ratio of 160/20 = **8:1**, straight off TRL's usable interval — which
is also where D6's denominator `(m₂x₂ − m₁x₁) ∝ (x₂²−x₁²)` sits away from its zero at βΔℓ = nπ. So:

- **Two standards do NOT suffice for a 2–20 GHz sweep.** That is 10:1 against an 8:1 interval. Measured
  with one separation: βΔℓ = 59.7° / 122.5° / **345.4°** at 2 / 6 / 20 GHz, with the top point 6.75e-2
  wrong in β while the other two are 2.5e-4 and 7.8e-4. `SuggestDeltas` therefore derives the count
  from the band.
- **The count is derived from 4:1, not 8:1, and the halving is measured margin.** Δℓ has to be chosen
  before any solve from ε_eff ≈ (εᵣ+1)/2, which runs 15–20% low, so a separation lands ~1.17× higher in
  βΔℓ than aimed. Designing to the full 8:1 put the top of a 5:1 band at 157°, one point from the edge.
- **`TargetElectricalDegrees` is 60°, not the interval's 90° centre**, for the same reason and in the
  same direction.
- **The separation is chosen per frequency on the PREDICTED βΔℓ, never on the extracted one.** An
  aliased separation reports a *wrapped* electrical length that can land inside the usable interval by
  accident and score better than the correct one.

**And `SuggestLengths` returns a SEPARATION, not a second absolute length.** Returning two lengths is
the obvious API and it silently breaks the calibration: `BuildLine` has a floor — a standard cannot be
shorter than its two end runs — so a short target is inflated while the long one is not, and Δℓ comes
out at half what was asked. Measured: a requested 7.2 mm realised as 3.58 mm, dropping βΔℓ from 31° to
15.6° and out of the interval.

### The oracle ladder, and what it established

| Tier | What | Result |
|---|---|---|
| 0 | the port operator: rows, signs, widths, refusals; Y symmetric; Y₁₁ = Y₂₂ on a symmetric line | exact / 5.3e-18 / 8.6e-17 |
| 1 | γ from the TRAVELLING WAVE of a single solve — no calibration in the path | recurrence residual 4.9e-3; ε_eff 3.673 (FR-4), 8.192 (GaAs) |
| 2 | the two-line γ against that oracle, across the band | **2.5e-4 / 4.9e-5 / 3.9e-3** at 2 / 6 / 10 GHz |
| 3 | **A and B agree on a uniform line**, then diverge by dispersion | −0.01% at 1 GHz; tracks Kirschning-Jansen to 0.9% out to 10 GHz |
| 4 | a THIRD line de-embeds to a matched section; the cascade identity; both a₂₁ branches | 3.9e-4 (2 GHz) / 6.0e-3 (10 GHz); 1.8e-2; exact |
| 5 | C_pul two ways; Z_c against kernel A | **−0.26%**; **+0.40%** at 1 GHz |
| 6 | reciprocity, passivity, the radiated fraction, the stub, the feed length | see below |
| 7 | the counter, determinism, frequency-order independence, cost | 1 core per mesh; bit-identical |

**Tier 1's oracle is D3's standing rule for the fifth time** (kernel A's meshed ground plate, L7b-b's
closed-form 2×2, L8a's Sommerfeld contour, L8c's cross-correlation): on a uniform line the current is a
two-wave sum, so `I_{k−1} + I_{k+1} = 2cosh(γΔz)·I_k` holds identically whatever the two amplitudes are
— no T-matrix, no error box, no standard, not even a second solve. **Taking it triple by triple is
badly conditioned and was replaced by a least squares over the same recurrence:** the delta-gap leaves a
strong standing wave, and every triple straddling a current NULL divides by a near-zero I_k (β reads
402 ± 2 over most of the line and −467 / −376 at the two nulls).

**Both γ routes are conditioned on ELECTRICAL length, so every fixture in this slice is scaled in
guided wavelengths rather than in millimetres.** A fixed physical length is well conditioned at the top
of a band and not at the bottom — on the 20 mm FR-4 line at 2 GHz the wave oracle read β = 36.6 against
a true ≈ 78. Scaling electrically costs nothing, because the mesh is frequency-scaled too: a 1.5 λ_g
line is about the same N at 1 GHz as at 20 GHz.

### Tier 3 — A and B on a uniform line, which is half of L8's own phase gate

`ε_eff` on §10.7's own hero cross-section, kernel B from the travelling wave against kernel A's
quasi-static answer and against Kirschning-Jansen's dispersion correction (which is an opt-in flag on
`QuasiStaticKernel` and is **not** an input to B anywhere):

| | 1 GHz | 2 GHz | 5 GHz | 10 GHz | 20 GHz |
|---|---|---|---|---|---|
| ε_eff (kernel B) | 3.3166 | 3.3454 | 3.4495 | 3.6425 | 4.0910 |
| vs kernel A's static 3.3169 | **−0.01%** | +0.86% | +4.00% | +9.82% | +23.34% |
| vs Kirschning-Jansen at that f | −0.45% | −0.29% | +0.19% | +0.89% | +5.32% |

**At the bottom of the band B reproduces A's quasi-static answer to 0.01%**, and the divergence above
it tracks a closed-form dispersion model B knows nothing about. That divergence is a RESULT — it is one
of the two things kernel B exists to compute — and reading it as an error would be the wrong lesson.

**One measured trap in the comparison itself: kernel A must not be run at an absurdly thin strip.** Its
edge reference is a fraction of the metal THICKNESS (R-mom-8), so t = 1 nm asks its mesher for a 3e-11 m
edge cell and the default mesh degenerates — 3.4652 against Hammerstad-Jensen's 3.3158, +4.5%, recovered
to 3.3188 only at `Refined(2)`. Measured across thickness: 3.3062 at 0.1 µm, **3.3169 at 1 µm**, 3.2875
at 35 µm (real copper, where A's own thickness effect is correctly present). t = 1 µm is what the
comparison uses.

### Tier 5 — Z_c, and the honest size of the quasi-static assumption

| | 1 GHz | 5 GHz | 20 GHz |
|---|---|---|---|
| Z_c = γ/(jωC_pul) | 51.854 − j0.475 Ω | 52.852 − j0.627 Ω | 54.924 + j1.690 Ω |
| vs kernel A's static 51.650 Ω | **+0.40%** | +2.33% | +6.34% |
| vs Kirschning-Jansen Z₀(f) | +0.44% | +1.40% | **−9.60%** |

C_pul itself agrees between two routes that share no code — **117.31 pF/m from differencing two
full-wave static solves against kernel A's 117.62 pF/m, −0.26%** — and moves by 0.17% across 1–20 GHz,
which is the differencing being frequency-independent by construction.

**Z_c RISES with frequency here, and that is the assumption showing rather than the physics.** Holding C
at its static value makes Z_c ∝ √ε_eff(f). K-J's own dispersive Z₀ rises too, and faster, so the two
part company by −9.6% at 20 GHz where fn = 32 GHz·mm and **both are outside their comfortable range** —
K-J is an empirical fit stretched past the TE₁ surface-wave onset (25.4 GHz on this slab, L8a's
R-lgf-3), and γ/(jωC_static) has no term for a dispersing C. Neither is authoritative there. **A
dispersive C needs a field integral this kernel does not have**; that is a real limitation of the
γ-and-C route and it is on the record rather than in a footnote.

### R-prt-10/11/12 — reciprocity and passivity are gates, losslessness is NOT

L8a wrote the warning for exactly this slice, and it is honoured: **there is no losslessness assertion
anywhere in L8d.** With tanδ = 0 and PEC metal on a 0.5 λ_g line, the missing power is

| | 2 GHz | 10 GHz |
|---|---|---|
| 1 − Σ\|S\|², de-embedded | +0.319% | +0.061% |
| the same on the RAW solve (port radiation included, no calibration in the path) | **+1.30%** | **+17.4%** |

The raw figure is the unambiguous one — nothing has been divided by a calibration — and it is what shows
the f² scaling. The de-embedded figure removes the port's own radiation into the error box, leaving the
straight line's, which is genuinely tiny; its non-monotonicity is inside the residual the section above
measures. **The DUT here is 0.5 λ_g rather than the 1.5 λ_g used elsewhere, deliberately**: on a longer
line at 10 GHz the de-embedding residual exceeds the radiated power and "missing" comes out negative,
which says nothing about the physics.

**R-prt-12 — conductor loss is NOT modelled and its size is reported.** Kernel B's sheet is PEC;
`PlanarConductorLayer.SigmaSm`/`ThicknessM` are carried and unused. From kernel A on the same line with
35 µm copper: α_c is **6.5% / 3.0% / 2.1%** of the total conducted loss at 2 / 10 / 20 GHz (0.41 of
6.29, 0.91 of 30.3, 1.29 of 60.1 dB/m). The two kernels are complementary — A cannot see radiation, B
cannot see conductor loss — and a surface-impedance term for B is named here rather than built.

### R-prt-13 — `Dcim.WithinValidatedRange` is a DECISION now, not an unwired function

It existed, was tested, and was called from nowhere. `PlanarSolve` now reports the mesh's widest ρ/λ
against it **as a note, never as a refusal**, and the note says why: the function is worded on L8a's
STRICT relative measure, which is right for a pointwise kernel query and wrong for a matrix fill. L8c's
Tier 2 measured the entries a real mesh produces and found the SCALED error — the one a fill experiences
— at ≤ 5.4e-3 on both starters. §10.7's own hero reaches ρ/λ = 2.83 at 20 GHz, so a per-entry refusal on
the strict measure would refuse the design the whole phase is scheduled against, for an error the fill
does not experience.

### Tier 6 — the quarter-wave open stub, which is half of L8's own phase gate

A λ_g/4 open stub on a through line, shipping mesh, sized from kernel B's own ε_eff at 6 GHz:

- ε_eff(6 GHz) = 3.4873 → λ_g = 26.756 mm → stub 6.689 mm; the 0.4 h open-end extension is 0.640 mm,
  **9.6% of the stub**, so it is not optional in the prediction.
- **Notch measured at 5.700 GHz.** A bare λ_g/4 predicts 6.000 GHz (−5.0%); with the open-end extension,
  5.476 GHz (**+4.1%**). The measured value sits between the two, nearer the corrected one.

L8e owns the formal gate. This is here so L8e is not the first time anyone looks, and so the open-end
correction is on the record before someone "fixes" the solver toward an uncorrected quarter wavelength.

### Tier 7 — the cost, and it is the finding L8d hands to L8e

Measured on §10.7's own hero (N = 552, FR-4, shipping mesh), a 3-point sweep with de-embedding on:

| | per frequency | for 101 points |
|---|---|---|
| kernel fit (`Dcim.Fit`, shared across all meshes) | 0.20 s | 20 s |
| the DUT (fill + factor + 2 back-substitutions) | 1.47 s | 149 s |
| **the calibration standards** | **5.98 s** | **604 s** |
| **total** | **7.66 s** | **~780 s** + 10 s of cores |

**Take this measurement ALONE or not at all.** Run in isolation it repeats to 1.5% across runs and its
DUT column reproduces L8c's own 1.48 s fill to 1%, which is what says the two are comparable. Run
alongside the other nine `Category=Benchmark` tests it read 16.79 s — more than twice as slow — and
that number went into the design note before it was checked. It is corrected there.

**De-embedding is 4.4× the bare fill L8c measured on the same hero (178 s), and the standards are 78%
of it.** Two things drive that, and both are reported by the driver rather than left to a stopwatch:

- **The standards are 2.58× the DUT's unknowns** — N = 297 / 382 / 331 / 416 against 552. A standard
  is not small: it has to reproduce the DUT's graded end cells over ~3 substrate heights at BOTH ends,
  and there are two of them per port.
- **The two ports of a plain microstrip did NOT share a calibration on the shipping mesh**, because
  L8b's edge grading is not exactly mirror-symmetric end to end, so their longitudinal runs differ by
  more than the 1e-12 `SameCrossSection` allows. On the coarse mesh they do share, halving the
  standards. **Making the mesher's end grading exactly symmetric is worth 2× here** and is the cheapest
  single saving identified in this slice.

For scale, `PlanarPortCalibrator` is a first-class object: a UI can build one per feed cross-section and
reuse it across every DUT that shares it, which is the other obvious saving and needs no engine change.
`SameCrossSection`'s own tolerance is 1e-12 rather than an equality **because the two ends of a uniform
line compute their run lengths by different subtractions** and differ in the last bit; demanding
equality silently doubled the standards for no reason.

**R-prt-4's answer is a NEGATIVE RESULT and is reported as one.** The brief asked for the minimum feed
length as a number in substrate heights. Asked of the geometry — vary the DUT's feed and compare — the
question is unanswerable, because comparing requires shifting the reference planes to a common point
and that multiplies S by e^{2γΔ}, so the γ residual accumulates faster than the near field decays
(measured: the answer moved 1.8e-2 / 2.3e-2 / 3.9e-2 across feeds of 1 / 2 / 3 / 5 h, GROWING). Asked
of the CALIBRATION instead — one fixed DUT, planes that never move, vary only how much of the feed the
standard reproduces — there is no knee either: 0.5 h and 1.0 h are bit-identical, and beyond that the
answer wanders 1.8e-3 / 3.1e-3 / 6.0e-3 per step for a **total spread of 9.9e-3 across 0.5–5 h**, which
tracks the calibration's own γ scatter rather than a decaying tail. **So the feed length is not the
binding constraint; the radiative floor is.** The practical rule is the one the default already
implements — enough feed to hold the standard's end run and keep the two error boxes apart, 3 substrate
heights — and the sensitivity to that choice is ~1e-2, the same order as the residual itself.

On the GaAs starter the question cannot even be posed at the shipping mesh: the wavelength-driven cell
is 418 µm at 10 GHz against a 100 µm slab, so "a few substrate heights" of feed is shorter than one
cell. On a thin substrate the requirement is satisfied by any feed the mesher can represent at all.

### R-prt-11's counter, generalised from R-fil-9

`PlanarSolveResult.CoreFillCount` counts the frequency-independent geometric cores built for a whole
run — **exactly one per mesh**, the DUT plus every standard — and is asserted independent of the
frequency list. The kernel is cached the other way round: `Dcim.Fit` depends on (slab, frequency) alone,
so **one fit per frequency serves the DUT and every standard**, which is worth 3–5× on a fixed cost that
is 12% of a hero frequency point.

`PlanarPortCalibrator` is **stateful and must be stepped in increasing frequency order**, because both
branch resolutions are continuations. The driver owns that invariant and sorts, so a caller handing over
a descending list gets the same answer rather than a wrong one (`T7_3`).

### Out of scope here, on purpose

- **`DataSet`, `.snp`, the kernel registry, any `IEmKernel`/`EmCapabilities` change, the current-density
  heat map, and the whole Ui half** — L8e. This slice returns matrices and diagnostics; inventing a
  result type here would be inventing the one that ships.
- **Conductor loss / surface impedance** — named and measured (R-prt-12), not built.
- **A true edge port (a half-rooftop at the boundary)** — it would remove the low-frequency
  conditioning floor D2 records, and it needs the basis L8c's own D2 forbids. A finding, not an edit.
- **Internal delta-gap ports, differential or multi-mode ports** — §10.6 lists the first as "later" and
  nothing in L8's gates needs any of them.
- **Any change to `SurfaceMesher`, `PlanarMesh` or the cell/basis ordering.** D4 exists so that none is
  needed. The one additive change made anywhere outside this slice's own files is **none** — even
  `PlanarProblem` was left alone, because ports are passed to the driver rather than carried on it.
- **ACA/MLFMM, adaptive frequency sampling** — still §10.7's, and Tier 7 above says adaptive sampling is
  now worth more than it was: the per-point cost went up 10×.

---

## L8e — the kernel registry, the diagnostics group, and the current-density reduction

**Brief:** `docs/sonnet-briefs/brief-L8e-results-registry-and-the-phase-gate.md`. The engine half of the
last L8 slice. The Ui half — auto-selection's call site, the port extractor, the heat-map overlay, the
provenance stamp — is in `src/Ui/Layout/Em/CLAUDE.md`; the phase-gate numbers are in `src/Ui/CLAUDE.md`
because the gate runs through the product path, not through the kernel.

### D1 — the registry is keyed on the ANALYSIS KIND, and unifies the OUTPUT, not the input

`EmKernelRegistry` is what §10.3.4 has been deferring since L6. What it does **not** do is give both
kernels a common `Solve` signature. Kernel A consumes an `EmProblem` and returns per-unit-length RLGC;
kernel B consumes a `PlanarProblem` — L8b's D1 forbids a shared base — and returns a de-embedded
S-matrix. A shared input interface would have to be `object` or a union, and both are worse than two
honest entry points.

So `IEmKernel` stays exactly kernel A's, `PlanarKernel` has its own, and what the registry unifies is
the **output contract**: `EmKernelOutcome` (kind, kernel name, `DataSet`, `EmSuitability`, notes). That
is the only thing a caller needs to be generic over, and it is the only thing that was actually shared.

`EmCapabilities.Planar` finally has a consumer — it was declared at L6 and read by nothing until now.
`RequiredCapability(kind)` maps the kind onto the flag and `Describe(kind)` resolves back by reading it,
so the enum and the flag cannot drift apart without a test failing.

### D2 — auto-selection takes VERDICTS, not geometry, and that is not a stylistic choice

`Choose(requested, crossSectionVerdict, planarVerdict)` never sees a layout, a shape, or a technology.
It cannot: both extractors live in `src/Ui/Layout/Em/`, behind the UI firewall, and the registry is in
the engine. The verdict pair is the whole interface.

The side effect is the good one — **D2's rule is testable in `Engine.Tests` with no layout document at
all**, which is why `EmKernelRegistryTests` is 16 tests that run in milliseconds instead of a fixture
that has to draw something.

The rule itself is conservative in one direction only:

| requested | A accepts | B accepts | outcome |
|---|---|---|---|
| `Auto` | yes | — | **A**, because it is about a thousand times cheaper and exact for this geometry |
| `Auto` | no | yes | **B**, naming what A refused |
| `Auto` | no | no | **refuse, quoting BOTH refusals** — never one of them |
| `CrossSection` | no | yes | **refuse.** Explicit stays explicit; it says B would work |
| `Planar` | yes | — | **B.** Explicit stays explicit in this direction too |

The "about a thousand times cheaper" ratio is worded in exactly one place,
`EmKernelRegistry.CheaperByRoughly`, because it appears in both the Auto note and the docs.

Every outcome carries a `Reason`, and the reason names the kernel. A user who cannot tell which solver
produced a number cannot tell whether the number is credible.

### D4 — the planar diagnostics group is `"planar"`, and it is NOT `"tline"`

`PlanarKernel.DiagnosticsGroup = "planar"`. No new result type: the same `DataSet` shape as kernel A —
`S`, per-port `Z0`, one diagnostics group — and it publishes γ, Z_c, ε_eff, α (dB/m), C_pul, the
calibration's electrical length, the de-embedding residual, the rejected-branch count, and a
per-frequency `CalibrationUsable` flag.

**Sharing `"tline"` was rejected on purpose.** A per-unit-length quantity from a 2-D quasi-static solve
and one back-solved from a de-embedded full-wave S-matrix are different claims about different objects.
They agree on a uniform line — that agreement is the phase gate — and they diverge with frequency,
which is dispersion and is a *result*. Put them in one group and a Data Display trace silently mixes the
two whenever a project contains both kinds of run.

`PlanarKernel.QuasiStaticNote` states the one caveat, once: **Z_c is γ/(jωC_pul) with C_pul held at its
quasi-static value**, which L8d measured as +0.4% at 1 GHz, +2.3% at 5 GHz, +6.3% at 20 GHz. The note
travels with the result rather than living in a doc nobody reads at plot time.

### D5 — the current-density reduction lives HERE, and it is stated once

`PlanarCurrentDensity` turns rooftop basis coefficients into a per-cell scalar. The reduction, in full:

```
I_x(cell) = ½ · Σ_{x-rooftops covering the cell} I_b        [A]
J_x(cell) = I_x(cell) / (the cell's TRANSVERSE extent)      [A/m]
|J|       = sqrt(|J_x|² + |J_y|²)
```

Two consequences are documented rather than smoothed over, because both look like bugs:

1. **An outermost cell carries HALF what its neighbour does, and that is correct.** A rooftop spans two
   cells; an edge cell is covered by one rooftop instead of two. The map is a *field* sample, not a
   conservation statement, and hiding the factor would make the edge look like a discretisation error.
2. **The exact identity is against the two adjacent EDGE currents — their mean — not against the port
   current.** Testing `Σ J·w == I_port` fails by exactly the edge factor above and would send someone
   hunting a bug in correct code.

The reduction is in the engine and not in the renderer because it is physics, and because the Ui must
not be the place where "what the colour means" is decided. `PlanarCurrentDensityMap` carries
`Normalised(cell)` **and** `ScaleCaption` — units, normalisation, the driven port, and the frequency —
so an unlabelled heat map cannot be drawn.

**One excitation, one frequency, no superposition and no sweep.** A heat map summed over a sweep or over
simultaneously-driven ports is a picture of nothing. The solve captures one solution column during the
sweep the panel already pays for (`PlanarSolveSettings.CurrentDensityPortNumber` /
`CurrentDensityFrequencyHz`, ~16·N bytes), so the map costs no second factorisation.

### The near-DC hole, found by the phase gate and left as a finding

Writing gate 1 produced a run that spent 50 s and ended in `Array dimensions exceeded supported range` —
a raw framework exception with no refusal attached. The cause was a **6 Hz** frequency point (a test
that passed a point *count* where a frequency list was expected), and at 6 Hz the per-frequency radial
remainder table is sized for a wavelength of 50,000 km.

The test was wrong and is fixed. **The product behaviour is still a hole**: R17's unknown ceiling is
guarded in three places, but nothing guards a frequency so low that the tables rather than the matrix
are what blows up. It is not on any path a user can reach from the EM panel today — the frequency spec
is authored in GHz and the mesher refuses long before this — so it is recorded here rather than fixed,
in this file, so the next person who sees that exception does not spend the 50 s twice.

### Out of scope here, on purpose

- **The extractors, the port labels, the overlay, the provenance stamp, `EmRunService`** — Ui half.
- **Any change to `SurfaceMesher`, `PlanarMesh`, the cell/basis ordering, the fill quadrature, or L8d's
  calibration settings.** None was needed and none was made. `PlanarSolve` gained three *optional*
  settings and three result fields for the captured column; the sweep's arithmetic is untouched.
- **A losslessness check.** L8a/L8d already established it does not carry over — an open planar
  structure radiates — and L8e adds none anywhere.
- **A second `.snp` naming convention, renormalisation, T-matrix cascade, or `DataSet` assembly.**
  All four already exist in `RfCore`; kernel B uses those.
- **ACA/MLFMM, adaptive frequency sampling, matrix compression** — §10.7's, still L9's.

## L9a — the general layered medium (N dielectrics, arbitrary source and observer heights)

**First of L9's FIVE slices, and L9 must not be attempted as one.** §11's L9 row reads *"DCIM, N
dielectrics, vias and z-directed current, adaptive frequency sampling, N-budget enforcement"* — strictly
more work than L8, which needed five. The split is L9a (this: the spectral kernel and its oracle ladder),
L9b (DCIM in two variables, the branch-point theorem re-derived, more than one pole), L9c (G_A^zz / G_A^zx,
via bases, junction continuity, the multi-level mesher), L9d (coplanar/differential/meshed-ground port
references), L9e (adaptive frequency sampling, ACA, the N budget, the refusal audit, and L9's phase gate).
Brief: `docs/sonnet-briefs/brief-L9a-general-layered-medium.md`.

```
LayeredMedium.cs      + Termination / MediumLayer / LayerStack, and LayeredStaticGreens (Tier 3's oracle).
                        GroundedSlab and StaticGreens are UNTOUCHED — D5 gates the new path against them.
SpectralGreens.cs     + LayeredSpectralGreens, alongside the one-layer kernel rather than replacing it
SurfaceWavePoles.cs   + the chain-matrix dispersion function and the pole search (D4/R-lyr-6)
SommerfeldIntegral.cs + EvaluateLayered / CanIntegrateLayered — Tier 4's rung
```

Gate: **378 routine tests in `tests/Engine.Tests/Mom/` (+28), and the whole routine `Engine.Tests` tier is
920 tests in 43 s** — inside the ~60 s ceiling. Plus **4 tagged `Category=Benchmark`** (~8 s: the oracle
convergence curve at three frequencies and the cost run). `tests/Firewall.Tests` unchanged. **Nothing
outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/` was touched**, and no user-facing string, refusal
or capability changed (D6): `EmCapabilities.LayeredWithVias` is still declared and read by nothing,
`PlanarKernel.CanSolve` still refuses more than one conductor level, `GroundedSlab.CanHost` still refuses
buried metal. A refusal is narrowed when the capability arrives, never in advance of it.

### R-lyr-1 — the conventions, stated once, in the block comment above `Termination`

**Layers are ordered BOTTOM-TO-TOP and z increases upward.** z = 0 is the bottom termination interface,
z = `TopZ` the top one. That matches `GroundedSlab` exactly (z = 0 ground plane, z = h top surface), so the
one-layer reduction D5 gates on is a *re-expression*, not a translation. Regions: 0 = bottom termination,
1..N = the finite layers, N+1 = top termination; interface i separates region i from region i+1. **A point
exactly ON an interface belongs to the region ABOVE it**, which is what puts a source at z = h in the air
above a grounded slab, matching L8's D2. Do not re-derive any of this at a call site.

`Termination` is PEC / PMC / half-space, and **D2 costs nothing here but is not free downstream**: an
open-below stack introduces the bottom half-space's own `k_b` as a SECOND branch point.
`LayerStacks.OpenBelow` exists so L9b has a concrete two-branch-cut case to measure DCIM against;
`PlanarExtractor`'s ungrounded-stack refusal stays until it answers.

### THE FINDING: the proper-sheet branch rule is NOT an analytic function, and R-lyr-4 needs one

R-lyr-4's generalisation of the `k_ρ² → 0` cancellation cannot be algebraic — there is no cascade analogue
of `Γ^e − Γ^h = 2jTk_ρ²(εᵣ−1)/[…]`. What replaces it is the technique this area already uses for pole
residues: `G(w) = Γ^e − Γ^h` is analytic at w = 0 with G(0) = 0, so its Taylor coefficients come from a
**trapezoidal average on a small circle in w = k_ρ²**, spectrally accurate and needing nothing re-derived
if the formulation changes.

**That only works if the integrand is analytic on the circle, and `SpectralGreens.ProperRoot` is not.** For
a real k² it negates its own result exactly when `Im(w) < 0`, so it flips sign on half of *any* circle
centred at the origin. This has nothing to do with a branch cut — k_z0's cut lies on the real axis at
w > k₀², far outside — and everything to do with the proper-sheet condition being a rule about physical
decay rather than an analytic function. `LayeredSpectralGreens.KzOfRegion` therefore takes the **PRINCIPAL**
root inside `|w| ≤ 4e-2·k₀²`, where `Re(k_i² − w) > 0` for every region makes it exactly the analytic
continuation from w = 0 — and where, on the real axis, the two functions are *identical*, which is the only
place production ever evaluates. The extraction contour sits at `1e-2·k₀²`, four times inside that
boundary, deliberately: a contour that grazes it picks the sign flip back up on the samples that round
outward.

**Getting this wrong is silent, and it was.** The reflection coefficients themselves stayed perfect —
Γ^e and Γ^h agreed with the shipped kernel to **2e-16** throughout the run that found it — and only Γ^q's
small-k_ρ limit came out **5% wrong**. Nothing but a direct comparison against L8a's exact cancellation
would have caught it.

### R-lyr-3 generalised, and the two conditioning facts that go with it

L8a's rule ("write `tan(k_z h)/k_z`, never `tan`") has an N-layer analogue: every layer enters as the
Möbius step `Γ ← (r + Γ′e^{−2jk_z d})/(1 + rΓ′e^{−2jk_z d})`, whose value is invariant under `k_z → −k_z`
(it reduces algebraically to the tan form, verified against L8a's own `TeFrom`/`TmFrom` by hand) and which
is finite at `k_z = 0` because **every interface coefficient is cross-multiplied** —
`(ε_a k_zb − ε_b k_za)/(ε_a k_zb + ε_b k_za)`, never `(Z_b − Z_a)/(Z_b + Z_a)`, which is 0/0 or ∞/∞ there.
Under the proper branch every propagation factor also satisfies `|e^{−2jk_z d}| ≤ 1`, so nothing overflows
however thick the stack. A zero denominator means the two media are *identical* — an invisible interface,
reflection exactly zero — and returning 0/0 there put a NaN in the middle of the εᵣ = 1 reduction at
k_ρ = k₀ precisely.

**G̃_q is assembled from Γ^q, not from V^e − V^h, whenever both points are in the open top half-space** —
which is every case production and every gate in this slice exercises. The two are mathematically
identical; the voltage route subtracts two ~Z-sized numbers to leave an O(w) remainder and so amplifies
rounding by `|Z|/|V^e − V^h|`, **measured at ~3e-12 on a thin GaAs slab at 1 GHz against ~1e-13 for the
reflection route**. The voltage route is kept for INTERIOR heights, where there is no half-space reflection
to refer to, and its looser conditioning is a stated property of that path rather than a surprise for L9c.

### R-lyr-5 — reciprocity, and why the cross-region case is deliberately NOT bit-identical

Same-region: **bit-identical**, structurally, at kernel A's own standard — the four-term form depends on
the heights only through `|z − z′|`, `z + z′` and the two interface distances, all symmetric.

Cross-region: the two orders take genuinely different computational paths (an upward generalised-
transmission chain versus a downward one), and they agree to **2.7e-13**. **Do not "fix" that by
canonicalising the order** — the agreement of two independent chains is worth more than a bit-identity
obtained by never taking one of them.

### The measured ladder

| Tier | What | Result |
|---|---|---|
| 0 | reciprocity, the k_ρ → 0 limit, Γ^q(0) ≠ Γ^e(0), the branch, every refusal | bit-identical / 2.7e-13 |
| 1 | **Γ^e, Γ^h, Γ^q vs the SHIPPED kernel**, both starters, 5 frequencies, whole k_ρ range | **6.2e-14 / 7.1e-14** |
| 1 | G̃_A, G̃_q at heights vs the shipped kernel | 4.6e-13 (FR-4), 1.7e-11 (GaAs) |
| 1 | εᵣ = 1 over ground, three invisible interfaces → exactly `−e^{−2jk_z0H}` | ≤ 1e-12 |
| 2 | **split-a-layer invariance** (halves, thirds, 0.3/0.7, middle layer, top layer) | **1.1e-13 … 3.3e-13** |
| 3 | the static solver vs L8a's own image series, scaled | 5.8e-14 / 2.7e-14 |
| 3 | ω → 0 convergence onto it, PCB 3-layer | 8.36e-4 → 9.28e-5 → 8.35e-6 → 9.28e-7 |
| 4 | εᵣ = 1 stack → free space + one image (a TRUE error curve) | rel 1.2e-10, scaled 7.3e-12 |
| 4 | the layered oracle vs L8a's own one-layer oracle | scaled 1.4e-15 / 3.7e-14 |
| 4 | the oracle's own movement under a 100× coarsening, ρ/λ ∈ [1e-4, 10] | **≤ 7e-10 everywhere** |

**Tier 2's "bit-identical" is not attainable and that is reported rather than tolerated.** Splitting a
layer of thickness d into d₁ + d₂ replaces `e^{−2jk_z d}` by `e^{−2jk_z d₁}·e^{−2jk_z d₂}`, and
`exp(a)exp(b) ≠ exp(a+b)` in floating point. The internal interface IS exactly transparent (its
cross-multiplied Fresnel coefficient evaluates to an exact zero for identical materials), so the residual
is purely that re-association: **7 of 16 samples are bit-identical and the worst vector-kernel deviation is
9.8e-15**. The scalar kernel is looser because its small-k_ρ Taylor coefficients are extracted
independently for each stack.

**Tier 1's ~1e-13 holds for k_ρ ≤ 40 k₀; past ~100 k₀ it is 1e-12.** There both kernels compute the SAME
exactly-Fresnel limit by two different underflowing routes — the shipped one saturates `tan` at |Im| > 30,
the cascade's `e^{−2jk_z d}` has already flushed to zero — on quantities that have decayed to ~1e-5 of
unity. **The KERNEL is looser than the reflections near k_ρ → 0 for a reason that belongs to the
quantity**: `G̃ = (1 + Γ)/(2jk_z0)`, and on a thin substrate `|1 + Γ^q| ≈ 3e-4`, so the last bit of Γ is
amplified ~3000×. Both kernels compute it that way and both lose the same digits.

### Tier 3's oracle is a genuinely electrostatic solver, and the COMPLEX-K finding reproduces

`LayeredStaticGreens` has no k_z, no TE/TM split of the medium, no wave: `e^{±k_ρ z}` (Laplace),
`K = (ε_a − ε_b)/(ε_a + ε_b)`, real `e^{−2k_ρ d}`, inverted by a J₀-zero-partitioned quadrature after the
two exact pieces are extracted. It is **checked against L8a's closed-form image series before being used**
as the multilayer reference — this area has now had four occasions where the oracle, not the method, was
at fault. L8a's own finding reproduces exactly: with tanδ dropped the static answer sits a
**frequency-INDEPENDENT 1.884e-2** away, which reads precisely like a convergence floor, while with a
complex K the convergence is exactly quadratic (ratios **9.0 / 11.1 / 9.0** against the 9.0 / 11.1 / 9.0
that (f₁/f₂)² predicts).

**A units slip in that quadrature cost 25 s per test and is worth remembering.** The panel-width cap was
written as `decay/8` where `decay` is a LENGTH; the exponential varies on the k-scale `1/decay`, so the
cap demanded ~10¹⁰ panels (capped to 64 per oscillation → 800 000), each driven to maximum adaptive
recursion by a per-panel tolerance of `relTol·|G|/N`. Correct units plus a per-panel *absolute* tolerance:
**25 s → 90 ms**, same answer. On a partition that already isolates every oscillation, a 12-point Gauss
rule is far inside tolerance on its first try; asking each panel for 1/N of the global budget only forces
every one of them to the depth limit.

### D4/R-lyr-6 — the pole finder, and the guard band that hid a mode

The dispersion function is a **chain-matrix determinant**, entire in k_ρ² except at the open terminations'
own branch points, because every matrix entry is `cos(k_z d)`, `sin(k_z d)/k_z` or `k_z sin(k_z d)` — all
even in k_z, all finite at k_z = 0. The obvious alternative (`1 − Γ_up Γ_dn`, or a reflection
denominator) carries the reflection coefficients' own poles, so a sign-change scan over it finds spurious
roots and can step over real ones. Ψ has none, so **every sign change is a real mode**. The scan runs on
the LOSSLESS stack, where Ψ is real-valued in the guided range, then a secant in w = k_ρ² moves the root
to the complex pole. Reported residuals are 1e-16 … 4e-13.

**A uniform grid alone silently returns "no modes" for the one case that can never legitimately return
it.** A thin grounded slab's TM₀ sits at `k_ρ/k₀ − 1 ≈ 1e-12` (h = 1 µm at 100 MHz), so the scan adds a
logarithmic refinement hugging both ends of the range down to 1e-14 of it — **and the endpoint guard band
had to come down from 1e-9 of the range to 1e-14**, because a "safe" pad excluded the root outright. That
was a real, silent, wrong answer, not a precaution.

**Counts, and the closest approach to the real axis** (which is what decides whether a pole matters to a
real-axis contour). Against the slab's own cutoff conditions the general finder matches exactly, and pole
locations agree to ≤1e-9:

| stack | 2 GHz | 10 GHz | 20 GHz | 40 GHz | closest Im/Re |
|---|---|---|---|---|---|
| FR-4 1.6 mm | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / **TE 1** | 1.6e-5 → 1.2e-2 |
| GaAs 100 µm | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | **2.5e-9** → 1.5e-6 |
| PCB 3-layer | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / **TE 1** | 8.4e-6 → 7.1e-3 |
| MMIC 2-level | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | TM 1 / TE 0 | 2.9e-9 → 1.6e-6 |
| Alumina, OPEN below | TM 1 / **TE 1** | TM 1 / TE 1 | TM 1 / TE 1 | TM 1 / TE 1 | 1.0e-8 → 1.3e-5 |

**The ungrounded stack carries a TE mode at every frequency measured** — a grounded slab does not until
25 GHz — so an open-below stack is not merely "one more branch point" for L9b; it is a second pole family
as well. **The GaAs and MMIC poles sit 2.5e-9 of their own real part off the axis at 2 GHz**, i.e. a
Lorentzian spike of relative half-width ~5e-9 essentially ON any real-axis contour. L8a already recorded
this shape for the one-layer GaAs slab; it does not improve with layers.

### M5 — the cost, measured (§8.2)

Per spectral sample, `LayeredSpectralGreens.KernelAtHeights` against `SpectralGreens.Kernel`, 40 000
samples, this machine at 10 GHz:

| | µs/sample | × the closed form |
|---|---|---|
| closed-form one-layer kernel (shipped) | 0.202 | 1.0 |
| one-layer cascade (FR-4 / GaAs / alumina) | 1.37 – 1.78 | **6.8 – 8.8** |
| two-layer cascade (MMIC) | 2.37 | 11.7 |
| three-layer cascade (PCB) | 3.42 | **17.0** |

Plus two one-off costs: the small-k_ρ Taylor extraction is **97.7 µs once per (height pair, frequency)**
and 1.13 µs/sample thereafter, and the surface-wave pole search is **5.8 ms once per (stack, frequency)**.

**What that projects to, and it changes the shape of L9b's problem.** L8a's DCIM fit costs ~0.2 s per
frequency at ~512 samples per candidate depth set; the cascade makes each of those samples 7–17× dearer,
so a fit at the same sample budget is **1.4–3.4 s per frequency per height pair** on a three-layer stack.
L9b then multiplies by the number of height pairs. At two metal levels that is three pairs (low-low,
low-high, high-high) — call it 4–10 s per frequency on top of L8d's already-measured 7.66 s per
de-embedded point. **The Taylor extraction is not the problem** (97.7 µs, amortised over a whole fit at a
fixed height pair), and neither is the pole search (5.8 ms per frequency, shared by every ρ and every
height pair — which is why it is cached on the kernel object rather than recomputed inside
`EvaluateLayered`). **The cascade itself is**, and the obvious lever is that the ladders are rebuilt from
scratch for TM and TE on every sample: caching the per-w ladder pair across the two polarisations is a
straightforward ~2× and is named here rather than left for L9b to discover.

### Out of scope here, on purpose

- **Any DCIM change.** Not a two-variable fit, not a re-derived branch-point theorem, not a new pole
  strategy. `Dcim.WithinValidatedRange`'s range is the ONE-LAYER kernel's and has not been silently
  widened by a kernel change underneath it — `Dcim.cs` is untouched. **L9b.**
- **`G_A^zz`, `G_A^zx`, via bases, junction continuity, any mesh change** — **L9c**. A z-directed current
  needs Green's-function components that do not exist in this repository; this is not "add a basis
  function".
- **Ports, references, finite ground pours, extractors, `.cem`, anything in `src/Ui`** — **L9d**.
- **Adaptive frequency sampling, ACA/MLFMM, N-budget changes** — **L9e**.
- **A losslessness check.** More true with more layers, not less: an open stratified structure radiates and
  launches surface waves, so `|S₁₁|² + |S₂₁|² < 1` legitimately. None was added anywhere.
- **A new starter technology** (D8): the multilayer stacks are hand-built in
  `tests/Engine.Tests/Mom/Support/LayerStacks.cs`, in SI units with every parameter written out.

### One refusal FLAGGED, deliberately not changed (§0.2 item 2)

`QuasiStaticKernel`'s sloped-boundary refusal ends *"A general dielectric stack arrives at L9"*. That is
true as written and **will be read as a promise it does not make**: "the general layered stack" means N
HORIZONTAL layers, and a vertical or sloped dielectric boundary is outside the 2.5D premise entirely — it
is not L9's, in any slice. **L9e's refusal audit sharpens the wording.** This slice changed no
user-facing string.

## L9b — DCIM for the general layered medium

**Second of L9's five slices.** L9a built the spectral kernel for an arbitrary stratified medium; this
makes DCIM work for it. Brief: `docs/sonnet-briefs/brief-L9b-dcim-for-the-general-medium.md`.

```
SpectralGreens.cs      + LayeredSpectralGreens.TopInterfaceReflectionAtKz0 (D1), the D7 cascade
                         cache, TopReflectedKernel, RegionWavenumberSquared/TopWavenumberSquared.
                         The one-layer members are untouched.
Dcim.cs                + Fit(LayeredSpectralGreens, …), CanFit, WithinValidatedRangeLayered,
                         DcimModel.Evaluate(ρ, z, z′) (D5), the generalised residue contour (D4)
```

Gate: **400 routine tests in `tests/Engine.Tests/Mom/` (+22), and the whole routine `Engine.Tests` tier
is 942 tests in 44 s** — inside the ~60 s ceiling. Plus **5 test methods tagged `Category=Benchmark`** (22 cases, counting
`R4_2`'s theory), adding **~11 min** to the opt-in tier — of which **6 m 40 s is the ORACLE self-check on
the two open-below stacks**, run before a single number below it was believed. L8's opt-in tier was
~8.5 min and L9a added 8 s; L9b roughly doubles it, and the oracle check is where it goes. `tests/Ui.Tests`
and `tests/Firewall.Tests` unchanged. **Nothing outside `src/Engine/Mom/` and
`tests/Engine.Tests/Mom/` was touched.** `Dcim.ValidatedRhoOverLambda` and its refusal string are
byte-identical (§0.2 item 1); the general medium got its own constant and its own wording.

### R-dcm-1 — the shipped one-layer fit is BIT-IDENTICAL, and that was checked the L7b-b way

`Dcim.Fit`'s body was refactored into a shared `FitCore` taking the reflection function, k₀, k_top² and
the reference height, so the one-layer and general paths differ in exactly three inputs and nothing else.
Before touching it, the fit was dumped at full precision for both starters × 2/10/20 GHz × both kernels;
`LayeredDcimTests.R1_1` asserts **exact equality** on the image count, the fit residual and three spatial
evaluations (ρ/λ = 1e-3, 0.1, 3), each of which folds every amplitude, every depth, every residue and the
quasi-static constant into one number. The Tier 2 oracles carry tolerances and structurally cannot catch a
one-ulp move, which is why this is an equality and not a bound.

### D1 — the cascade re-parameterised by k_z0, and the one rule that generalises

`w = k_ρ²` is **even in k_z0**, so a `w`-parameterised cascade literally cannot be asked what
`Dcim.BranchPointTaylor` asks — it evaluates by central differences *through* k_z0 = 0 and needs negative
k_z0, which a round trip through k_ρ cannot express and which costs a square root that can land on the
wrong branch. The generalisation is one rule: **every INTERIOR region's k_zi is even in k_z0 and comes
from `k_zi² = k_i² − k_top² + k_z0²`; the TOP region's vertical wavenumber IS the supplied k_z0, with its
literal sign.** The Möbius ladder is invariant under `k_zi → −k_zi` (verified by hand on the one-layer
case: both the interface coefficient and the round-trip factor go to their reciprocals, and the composition
is unchanged), so no interior branch decision can matter.

**Two things in that entry point are easy to get wrong and are handled explicitly.** Γ^q needs
`(Γ^e − Γ^h)/k_ρ²`, and small |w| means k_z0 has come back to ±k_top — where the **two signs are DIFFERENT
analytic functions of w**. `ReflectionTaylor` is therefore keyed by branch and the negative one is
extracted on its own contour; quietly using the positive series for a negative k_z0 is exactly the shape of
L9a's proper-sheet defect (reflections perfect, Γ^q wrong). And the difference does vanish as w on *both*
branches, because at k_ρ = 0 every interface coefficient reduces to `(k_a − k_b)/(k_a + k_b)` for **both**
polarisations, on either branch.

Tier 0: the two parameterisations agree to **1.3e-12** wherever both are defined; k_z0 < 0 is reachable,
carries its sign, and Γ is analytic through zero (the second central difference scales as d², ratio 4.00).

### D2 — the far-field sum rule SURVIVES generalisation, and it is still a theorem

At k_z0 = 0 the top interface's cross-multiplied Fresnel coefficient is `−1` (TE) and `+1` (TM)
**whatever is below**: k_z0 is the only thing that vanishes, and the Möbius ladder underneath enters only
through the other, finite, argument — and a Möbius step with r = ±1 returns ±1 regardless of it. So
Γ^h → −1, Γ^e → +1, Γ^q = 1 − 2 = −1, and `1 + Γ` vanishes identically for any number of layers provided
the top termination is an open half-space. **Measured: worst `|1 + Γ|` = 2.2e-16** over every stack, both
kernels, 2/10/20 GHz. `Σ A_i = −(1 + Γ(∞))` is therefore still exact and `BranchPointOrders = 1` still the
only order that is a theorem.

**The orders table re-run on the multilayer stacks (worst over ρ/λ ∈ [1e-4, 10] at 10 GHz), and order 1
survives:**

| stack | G_q rel / scaled, orders 0 / 1 / 2 / 3 |
|---|---|
| FR-4 1.6 mm | 2.7e-2 / **4.8e-3** / 8.3e-3 / 9.7e-3   —   sc 2.1e-2 / 3.7e-3 / **3.5e-3** / 4.1e-3 |
| GaAs 100 µm | **7.0e-2** / 5.1e-1 / 3.0 / 64   —   sc **6.0e-5** / 4.4e-4 / 2.6e-3 / 5.2e-2 |
| PCB 3-layer | 2.4e-2 / 2.5e-2 / **2.1e-2** / 2.9e-2   —   sc 1.1e-2 / 8.4e-3 / **5.2e-3** / 7.2e-3 |
| MMIC 2-level | 1.2e+2 / 6.7 / **3.4** / 62   —   sc 1.0e-1 / **2.0e-2** / 2.0e-2 / 5.2e-2 |
| Alumina, open | 2.2e-3 / **1.8e-3** / 2.3e-3 / 7.8e-2   —   sc 1.5e-2 / **8.0e-3** / 8.4e-3 / 1.6e-1 |

G_A behaves as L8a recorded: order 0 → 1 is a large improvement everywhere, 2 and 3 buy a little more.
**GaAs is the one stack where order 0 is better on G_q, and it was already at L8a** (8.2e-2 against 5.1e-1
there; 7.0e-2 against 5.1e-1 here — the same number, reproduced through a completely different kernel).
Order 1 is best or within ~1.6× of best on every other stack in both measures, and it is the only order
that is a theorem rather than a knob. **Nothing here argues for changing the default.**

### THE FINDING: the SECOND branch point is a structural obstruction, not an accuracy problem

An open-below stack makes Γ depend on `k_zb = √(k_b² − k_top² + k_z0²)`, which is neither even nor
single-valued in k_z0: flipping k_zb replaces the bottom Fresnel coefficient by its **reciprocal**, so the
bottom half-space's branch genuinely matters where every interior one does not. Its branch points sit at
`k_z0 = ±√(k_top² − k_b²)`, and with εᵣ ≥ 1 enforced and air on top, k_b ≥ k_top always — so that is
`±j·k₀√(εᵣµᵣ − 1)`, **on the imaginary axis, with the minus root in the half-plane the sampling path runs
into.** DCIM fits Γ as a sum of exponentials in k_z0, which is **entire**. It cannot carry a second cut.

**`LayerStacks.OpenBelow` is degenerate for this and must not be the fixture anyone concludes from** — it
is alumina in AIR, so k_b = k₀ exactly, the two branch points coincide at 0.0000, and `k_zb ≡ k_z0`
identically along the path. `LayerStacks.FilmOnSilicon` (4 µm oxide on semi-infinite silicon, εᵣ = 11.9)
is the honest shape, and it is decisive:

| | second branch point k_z0/k₀ | nearest approach: far path / near path | worst scaled error at ρ/λ = 10 |
|---|---|---|---|
| every grounded stack | none (Γ_bottom is exactly ±1) | — | ≤ 2.1e-2 |
| Alumina, open below | 0.0000 (coincident) | — | 2.9e-3 … 1.7e-1 |
| **Oxide on silicon** | **−0.0090 − 3.3015 j** | **0.178** / 1.022 | **G_q 59, G_A 2.3e+4** |

The far sampling path (`FarPathExtent = 4`) passes within **0.18 k₀** of it, on a path whose whole useful
extent is 4 k₀. The missing piece is the **lateral wave launched downward into the substrate**, which
falls off as 1/ρ² and is not an image — which is why the error is worst at large ρ and why more orders do
not touch it (the whole `BranchPointOrders` row is 4e-3 … 2.3e+4 with no trend).

**`Dcim.CanFit(LayerStack)` refuses it by name** and says what would carry it (a second exponential family
in k_zb, or an extracted lateral-wave term) **without building either** — that is L9c/L9e's, and the brief
said not to build the proposal here. `PlanarExtractor`'s ungrounded-stack refusal can therefore be narrowed
**only for the equal-density case**, and this is the measurement that says so.

**The oracle was checked there first, as the standing rule requires.** A 100× refinement moves
`EvaluateLayered` by ≤ 1.4e-10 relative / 1.2e-11 scaled on BOTH open-below stacks, at every ρ measured, with
the tail series converged. The 59× is DCIM's.

### D4 — N poles need no new algebra; two things do change

`SurfaceWaveTerm` was **widened** to `(Name, KRho, Residue)` rather than duplicated or unified with either
mode record — DCIM consumes exactly a pole location, a residue and a name, and the two finders' extra
fields are diagnostics of the SEARCH that belong on the search reports. Unifying `SurfaceWaveMode` and
`LayeredSurfaceWaveMode` would have forced the shipped one-layer finder to change for a consumer that
reads neither field.

**The residue contour radius is the part that fails silently.** The one-layer radius is written against
the slab (`0.05·min(|w_p − k₀²|, |K1² − w_p|)`) and neither term survives: the general radius is
`0.05·min` over **every region's `|k_i² − w_p|`** (which covers both branch points, including an open
bottom's k_b²) **and over every OTHER pole**, because with more than one mode the nearest singularity is
routinely the neighbouring pole. A contour that encloses a second singularity returns a smooth, plausible,
wrong far field.

### D5 — the height pair is an EXACT SHIFT, so "a fit per height pair" is simply wrong

In the top half-space, substituting DCIM's own decomposition of Γ into
`G̃ = [e^{−jk_z0Δ} + Γ e^{−jk_z0Σ}]/(2jk_z0)` gives, term by term: the direct term → `e^{−jk₀R}/4πR` with
`R = √(ρ²+Δ²)`; the quasi-static constant → an image at depth **Σ**; each fitted image → an image at depth
**b_i + Σ** with the **same amplitude**; each pole → the same `H₀⁽²⁾(k_pρ)` with its residue scaled by the
constant `e^{−jk_z0(k_p)Σ}` = the real decay `e^{−αΣ}`. **Every amplitude is unchanged, so the sum rule and
the far-field theorem survive the shift untouched.** `DcimModel.Evaluate(ρ, z, z′)` is that, and there is
no refit: **the two-variable fit §10.2 warns about is not needed for the case that covers every L8 geometry
and the top level of a two-level stack.**

The algebra is pinned separately from the accuracy: on the εᵣ = 1 control stack, where Γ is exactly
`−e^{−2jk_z0H}` and the fit has nothing to get wrong, the shifted answer is free space minus one PEC image
at depth `z + z′` to **8e-15 / 3.8e-14**.

**The ACCURACY is not uniform in Σ, and the reason is measurable rather than guessed** (Δ = 0 throughout,
scaled error, 10 GHz):

| stack | G_q Σ/λ = 0 → 0.30 | G_A Σ/λ = 0 → 0.30 | fitted Γ's own error over k_ρ ≤ k₀ (G_q / G_A) |
|---|---|---|---|
| FR-4 1.6 mm | 3.7e-3 → **8.4e-2** | 2.9e-6 → 2.7e-6 | 2.2e-3 / 2.3e-6 |
| GaAs 100 µm | 4.4e-4 → 3.4e-4 | 1.3e-6 → 8.5e-10 | 2.5e-4 / 1.6e-9 |
| PCB 3-layer | 8.4e-3 → **5.1e-2** | 1.0e-6 → 3.7e-7 | 7.7e-3 / 4.1e-7 |
| MMIC 2-level | 2.0e-2 → 5.2e-3 | 5.0e-7 → 9.3e-10 | 2.9e-3 / 1.8e-9 |
| Alumina, open | 8.0e-3 → **5.4e-1** | 1.4e-2 → **6.5e-1** | 7.5e-3 / 7.8e-3 |

`|e^{−jk_z0Σ}| = e^{−k_ρΣ}` out past the light line, so **a growing Σ kills the evanescent spectrum the
sampling path resolves best and leaves the answer standing on the propagating region the fit only ever
extrapolated into**, constrained by the branch-point sum rule alone. The last column is that region's own
fitted-Γ error, and it predicts the sign of the Σ dependence exactly: every case that degrades has a
~1e-3 Γ error there and every case that improves has ~1e-9. **G_A on a grounded stack improves
monotonically with height; G_q on a thick low-εᵣ substrate degrades by ~20×.**

### D6 — the interior case, handed to L9c as a SCOPED job rather than a discovery

Nothing is fitted for interior heights and nothing is claimed: `DcimModel.Evaluate(ρ,z,z′)`,
`SommerfeldIntegral.EvaluateLayered` and `LayeredStaticGreens` all refuse a source inside the stack by
name. **What was established is the shape of the problem**, because "you cannot measure a fit you have no
oracle for" cuts both ways — you also cannot hand a phase an unscoped one.

**The interior same-region kernel is still an exact shift — of FOUR exponential families rather than one.**
With both points in region m the voltage is
`(Z_m/2·denom)·[e^{−jk_zmΔ} + Γ_t e^{−jk_zm(2d−Σ_b)} + Γ_b e^{−jk_zmΣ_b} + Γ_tΓ_b e^{−jk_zm(2d−Δ)}]` with
`Σ_b = z + z′ − 2z_b`, and the four coefficients do not depend on the heights at all. **Measured, not
asserted:** solving for them from four height pairs predicts a FIFTH to **9.8e-15**, worst over every
stack, both polarisations, k_ρ/k₀ ∈ {0.3, 2, 15} (`R5_3`). So a fit per height pair is wrong for the
interior case too — but it is four fits in `k_zm`, not one in k_z0.

**What that costs L9c, precisely:** `SommerfeldIntegral.FreeSpace` takes a `double` wavenumber and must
widen to a complex one (the Sommerfeld identity is unchanged for complex k, but a lossy layer's k_m is
complex where the top half-space's k₀ is real); the oracle needs **three** closed-form extractions rather
than two, because a source sitting exactly on an interior interface makes the down-reflection term
non-decaying as well as the direct one; and the `k₀ sinθ / k₀ cosh u` substitutions are not needed at all,
because a lossy interior region's k_zm never vanishes on the real axis — but the top half-space's own
branch point at k_ρ = k₀ is still in the integrand as a square-root kink and needs breakpoints.
**The CROSS-REGION case (source in m, observer in n ≠ m — exactly two metal levels at two different
interfaces) mixes k_zm and k_zn and has no single reference wavenumber to be an exact shift IN.** It is a
different question and is reported as one.

### R-dcm-4 — the measured range, and TWO limits L8a did not have

Six stacks, both kernels, 2/10/20 GHz, ρ/λ ∈ [1e-4, 10], against `SommerfeldIntegral.EvaluateLayered`
(`R4_2`). **Inside everything `Dcim.WithinValidatedRangeLayered` admits, the error as a fraction of the
free-space kernel is ≤ 1.6e-2 — against L8a's ≤ 6e-3 on the two single-substrate starters. The general
medium is about 2.6× worse, and this says so rather than rounding it into the same sentence.** The four
largest values all sit at ρ/λ = 5.6e-4, the first grid point above the near-field floor below (MMIC and
GaAs at 2 GHz, 1.5e-2 and 1.0e-2; MMIC at 10 GHz, 5.9e-3); **every case whose worst point is elsewhere is
≤ 5.1e-4.** Strict
relative error on G_q is ≤ 1e-2 out to ρ/λ ≈ 1 exactly as at L8a, degrading to 0.26–0.65 at ρ/λ = 10 in the
same dipole cancellation zone. `ValidatedRhoOverLambdaLayered` is 1.0 **by measurement**, not by
inheritance — GaAs at 2 GHz breaks 1e-2 at ρ/λ = 1e-4 here, which is the number L8a's own table records for
the same substrate through a completely different kernel.

**1. A NEAR-FIELD floor, derived rather than fitted.** The sampling path reaches `k_ρ = PathExtent·k₀` and
no further, so nothing finer than `1/(PathExtent·k₀)` was ever sampled — which in ρ/λ is the
**stack-independent constant `1/(2π·PathExtent) = 5.3e-4`** at the default. L8a's own sweep ran below that
and got away with it only because both starters are single substrates whose thinnest feature is 100 µm.
Put a 3 µm spacer in and it bites: the MMIC stack's G_q is **1.8e-1 at ρ/λ = 1e-4 and 1.6e-2 above the
floor** at 2 GHz. Raising `PathExtent` lowers the floor proportionally and costs only samples.

**2. A weakly-bound surface wave.** When a pole sits within ~1e-4 of the branch point — an electrically
thin UNGROUNDED slab, where TM₀ is barely guided — the pole extraction and the branch-point far-field
constraint are describing the same feature and stop separating. 0.5 mm alumina in air at 2 GHz
(k₀H = 0.021): 1.5e-1 on G_q and 1.7e-1 on G_A at ρ/λ = 10, against ≤ 2.1e-2 on the **same stack** at
10 GHz (k₀H = 0.105) and 20 GHz. The floor (`MinUngroundedElectricalThickness = 0.05`) is bracketed by
those two measurements. **The mechanism was NOT isolated and the refusal does not claim one.** The obvious
candidate — a barely-guided TM₀ sitting on the branch point — was tested and ruled out: the GROUNDED FR-4
and GaAs slabs put their poles *closer* to it at the same frequency (1 + 7.8e-6 and 1 + 7.5e-6, against
1 + 4.4e-5 here) and are accurate to 2.0e-3 and 1.0e-2. A first attempt at this refusal was written on the
pole distance and the "a refusal must be EARNED" assertion in `R4_2` caught it refusing two stacks it had
no business refusing — which is what that assertion is for.

**3. And a LOW-FREQUENCY floor on the FIT that is far above the kernel's own.** Tier 3 found it:
`PathExtent = 300` is a statement in units of k₀, while the stack's image structure lives at `k_ρ ~ 1/H`,
which does not move with frequency — so `300·k₀H` is what decides whether the fit sees the stack at all,
and on the 1.4 mm PCB stack it falls through 1 between 300 MHz and 100 MHz. Below that the error **grows**
as the frequency falls (3.8e-3 at 300 MHz, 2.9e-2 at 100 MHz), which is not a floor and is not the oracle.
Holding `k_ρ,max·H = 100` instead gives ratios of **11.19 and 9.16 against the 11.1 and 9.0 that (f₁/f₂)²
predicts** — exactly quadratic onto `LayeredStaticGreens`. `GroundedSlab.MinElectricalThickness` (k₀H ≥
1e-6) is a limit on the KERNEL and is three orders of magnitude below this. **Recorded, not acted on:**
changing a shipped default belongs with L9e's audit, and a frequency-aware path extent is precisely the
shape of L9e's adaptive frequency sampling.

### Tier 1's "bit-identical" is not attainable for the FIT, and the reason is structural

The SAMPLES the two paths feed the fit agree to **2.7e-11** (the DCIM path runs out to |k_z0| ≈ 300 k₀,
three times further than L9a's Tier 1, into the region L9a already recorded at 1e-12 — the two kernels
computing the same exactly-Fresnel limit by two differently-underflowing routes). **The fit built from them
does not.** Prony picks its ORDER by a residual threshold, its roots by Durand-Kerner, and the final image
set by scoring three candidate depth sets against each other — every one a DISCRETE decision taken on
samples that differ in the last two digits. Measured, the image count differs (13 vs 19 on FR-4's G_A at
10 GHz, 6 vs 14 on GaAs's at 2 GHz) while both fits are equally good against the oracle. Demanding
bit-identity there is demanding that a discontinuous function of the samples be continuous. **The honest
gate is Tier 4's**: the general path's error *against the oracle* matches the shipped path's.

The same effect sets Tier 2's tolerance: splitting a layer moves the fitted answer by **1e-10 … 8.6e-6**
where the fit is well determined, and by the fit's own accuracy where it is not (1.4e-2 on the MMIC at
ρ/λ = 1e-4, i.e. below the near-field floor). L9a measured the KERNEL's split-invariance at 1.1e-13; the
model's is bounded by the fit, not by the kernel.

### D7 — the cascade cache, and L9a's cost projection was against the wrong quantity

Every k_zi and every `e^{−2jk_z d}` is polarisation-independent, and every consumer wants both
polarisations at the same w; before the cache, `FresnelDown`, `FresnelUp` and `RoundTrip` each re-derived
their own, so the three-layer stack took ~24 complex square roots and 12 exponentials per sample where 5
and 3 suffice. The cache is one entry deep (every caller walks w in a single pass) and lives inside one
instance, i.e. inside one frequency (R-dcm-7).

| | L9a measured | after D7 | gain |
|---|---|---|---|
| one-layer cascade (FR-4 / GaAs / alumina) | 1.37 – 1.78 µs/sample | **0.54 – 0.63** | 2.5 – 3.3× |
| two-layer cascade (MMIC) | 2.37 | **0.73** | 3.3× |
| three-layer cascade (PCB) | 3.42 | **0.93** | 3.7× |

**The fit costs 11–102 ms per frequency per kernel on every stack — the same as the shipped one-layer
closed-form fit (96 ms), not the 1.4–3.4 s L9a projected.** L9a's projection scaled L8a's fit time by the
per-sample ratio; that is wrong twice over. The fit samples the top-interface REFLECTION LADDER, not
`KernelAtHeights` (no `Voltage`, no four-term form, no per-height-pair Taylor extraction), and most of a
fit's wall clock is Prony's least squares, Durand-Kerner and the amplitude solve — none of which the stack
touches. **So D5's no-refit result and this together mean L9c/L9d are scheduled against ~0.2 s per
frequency for both kernels at any height pair, on top of L8d's measured 7.66 s per de-embedded point — a
101-point sweep of a two-level structure is minutes, not hours, and L9e's adaptive frequency sampling stays
optional on these grounds.** The pole search (5.8 ms per stack per frequency, cached on the kernel) and the
Taylor extraction (98 µs) remain non-issues.

### Out of scope here, on purpose

- **`G_A^zz`, `G_A^zx`, via bases, junction continuity, any mesh change** — **L9c**, and D6 above is what
  it is being handed.
- **Ports, references, finite ground pours, extractors, `.cem`, anything in `src/Ui`** — **L9d**. In
  particular `PlanarExtractor`'s ungrounded-stack refusal was NOT narrowed: this slice answers *whether* it
  can be (yes for an equal-density open bottom, no for a denser one) and narrowing it is L9d's.
- **Adaptive frequency sampling, ACA/MLFMM, N-budget changes, the refusal audit, L9's phase gate** —
  **L9e**. The low-frequency `PathExtent` finding above is L9e's input, not a change made here.
- **A gate on published multilayer reference data.** §11's L9 gate sentence is still unresolved and is
  still not this slice's to settle (§0.2 item 3).
- **Any widening of `Dcim.ValidatedRhoOverLambda` or of the existing refusal string.** Byte-identical.
- **A second exponential family in k_zb, or an extracted lateral-wave term.** D3 says what would carry the
  second cut and deliberately does not build it.
- **A losslessness check.** Still more true with more layers.

## L9c — z-directed current: the dyadic components and the interior inverse transform

**Third of L9's five slices.** Brief: `docs/sonnet-briefs/brief-L9c-z-directed-current-and-vias.md`.
**M1–M5 are DONE and gated**, including the multi-level fill. What is deliberately not built is
everything downstream of the matrix — see §"What is not built" before planning on top of this.

```
SpectralGreens.cs      + GreensKernel gains VerticalVectorPotential and MixedVectorPotential (D1);
                         LineResponse (the four TL Green's functions from one cascade traversal),
                         Current / SeriesCurrent / SeriesVoltage, VerticalKernel, MixedKernel,
                         AsymptoticAtHeights + InteriorAsymptote (D3), LineDifference.
                         Voltage now delegates to LineResponse and is BIT-IDENTICAL.
SommerfeldIntegral.cs  + FreeSpace(Complex k, …), CanIntegrateInterior, EvaluateInterior (M2)
Dcim.cs                + FitAtHeights + DcimModel's INTERIOR mode (M3), the k_m-referenced path
                         helpers, WithinValidatedRangeAtHeights. Fit/FitCore and the shipped
                         one-layer and top-half-space paths are untouched and bit-identical.
PlanarProblem.cs       + PlanarVia, per-level ZM, MediumStack, CanSolve, the generalised λ_g (D6)
PlanarBasisFunctions.cs+ the vertical basis: Weight/Divergence/VerticalWeight/SharedFootprintArea (D2)
PlanarMesh.cs          + PlanarBasisDirection.Z
PlanarMeshReport.cs    + ViaUnknownCount
SurfaceMesher.cs       + via footprints as HARD gridlines, and the vertical bases they imply (R-via-5)
PlanarKernelSet.cs     NEW — PlanarLevels (the midpoint rule + its refusal) and the per-pairing
                         kernel set with D7's fit counter (M5)
SingularExtraction.cs  + FromDcimAtHeights, PlanarKernelTerms.Derivative, BuildDerivative (M5)
PlanarFill.cs          + FillMultiLevel: the generalised scalar block, the ẑẑ block and the ẑx̂ one.
                         Fill/BuildCores and every L8c path are untouched.
Bessel.cs              + H₁⁽²⁾, needed only because d/dz H₀⁽²⁾ = −H₁⁽²⁾
```

Gate: **965 routine tests in `tests/Engine.Tests/Mom/` (+23 methods), and the whole routine
`Engine.Tests` tier is 965 tests in 51 s** — inside the ~60 s ceiling. Plus **5 methods tagged
`Category=Benchmark`, adding ~4 min** to the opt-in tier (L8's was ~8.5 min, L9a added 8 s, L9b
~11 min): the interior oracle's convergence sweep (216 oracle points), Tier 5's curve (48 fits × 5
oracle points) and the cost/N runs. **~110 oracle points sit in the routine tier**, which is the
budget §0 set. `tests/Firewall.Tests` unchanged (4/4) and **`tests/Ui.Tests` unchanged and green
(4,732)** — which is D6's decision gated rather than argued. **Nothing outside `src/Engine/Mom/` and
`tests/Engine.Tests/Mom/` was touched.**

### D1 — there are FOUR kernel components, the enum stayed FLAT, and the obvious answer is wrong twice

The derivation is written out in full in the block comment above `VerticalKernel`, from the
transmission-line source terms up. What it establishes:

| component | from which line | built from |
|---|---|---|
| `VectorPotential` G_A^xx | TE only | `V_i^h` — the shunt-source VOLTAGE (L8a's, unchanged) |
| `ScalarPotential` G_q | both | `V_i^e − V_i^h` (L8a's, unchanged) |
| **`VerticalVectorPotential` G_A^zz** | **both** | **`I_v` — the SERIES-source CURRENT** |
| **`MixedVectorPotential`** | **both** | **`I_i` — the shunt-source CURRENT** |

**"The horizontal component with the TE line swapped for the TM one" is wrong in two independent
ways, and each is worth stating because each is invisible if you get it wrong.**

1. **Wrong SOURCE TYPE.** A z-directed current enters the TM line as a **series voltage source**
   `v^e = k_ρĴ_z/(ωε)`, not as a shunt current source. So the vertical components are built from the
   *other two* transmission-line Green's functions — `I_v` and `V_v` — which did not exist in this
   repository before this slice. `LineResponse` produces all four from one cascade traversal by
   running the ladder on the DUAL line (Z↔Y, every Γ negated, the Möbius composition unchanged, the
   transmission coefficient's impedance ratio reciprocated). No second cascade.
2. **G_A^zz IS NOT TM-ONLY, even though a vertical dipole's FIELD is.** The field of a VED has
   `H_z ∝ V^h = 0` — no TE at all. But `G_A^zz` is not the field; it is what is left after
   subtracting `−∇φ`, and φ is built from a `G_q` that is a TM−TE difference. **The TE term in
   `G_A^zz` is exactly the TE content of ∇φ, put back.** Making `G_A^zz` TM-only is a different and
   equally valid formulation — it needs a SECOND scalar kernel for vertical sources, which would
   break L8c's D4 (one per-cell potential matrix `P` serving every pair) and with it the exact charge
   bookkeeping a via basis depends on. One scalar kernel is the choice; the TE term is its price.

**The dyadic has BOTH mixed components and they are not equal**: `G_A^uz(z,z′) = −G_A^zu(z′,z)`. A
formulation carrying only ẑx̂ would give a Galerkin matrix with an entry for (vertical *m*,
horizontal *n*) and none for its transpose — no reciprocity. The extra minus sign is compensated
exactly by the ẑx̂ component being **odd in x − x′** (it enters through a ∂/∂x), so `Z` stays
symmetric. That is structural, not observed.

**`GreensKernel` stayed a flat enum, deliberately.** All four members are scalar functions of
`(k_ρ, z, z′)` and nothing else; the direction structure lives entirely in how the FILL uses them
(the mixed one through a ∂/∂x, the others through an identity). A (source direction, observer
direction) pair would push a direction switch into `Dcim`, `SommerfeldIntegral` and
`SingularExtraction`, none of which has a use for one, and two of which switch on this enum in a hot
loop.

### Tier 1 — the εᵣ = 1 reduction, and the IMAGE SIGN FLIPS because the CURRENT reflection does

Over a bare PEC ground plane with no dielectric, every component collapses to free space plus **one**
image, exactly, and this is the check no plausible-but-wrong dyadic survives:

```
G_A^xx = G̃₀(Δ) − G̃₀(Σ)     G_q = G̃₀(Δ) − G̃₀(Σ)      ← NEGATIVE image
G_A^zz = G̃₀(Δ) + G̃₀(Σ)                                ← POSITIVE image
mixed  ≡ 0
```

**A PEC is a SHORT on both lines: the VOLTAGE reflection is −1 and the CURRENT reflection is +1** —
and the vertical component is built from a current. The whole image-sign flip is that one sentence,
arriving *through* the transmission-line analogy rather than being imposed on it. **Measured: worst
4.1e-15 (positive image), 9.6e-15 (negative), 2.1e-15 (mixed ≡ 0)**, over interior, cross-region and
half-space height pairs at 2 and 10 GHz. In genuine free space (air between two air half-spaces) all
three scalar kernels reduce to `1/(2jk_z)e^{−jk_z|Δ|}` to **5.2e-15** and the mixed one to 3.5e-16 —
which is what fixes `G_A^zz`'s overall normalisation, and nothing else in the ladder does.

### Tier 1b — the MPIE identity with NUMERICAL ∂_z G_q, and the rung is the DIFFERENCE

The one rung that checks the new components' sign and scale **without reusing the algebra that
produced them**. The derivation's entire content is the reduction of `∂_z G_q` and `∂_z∂_{z′}G_q`
into line currents; here those derivatives are taken by central differences from `ScalarKernel`
alone, and the "true" fields come from the transmission-line relations `E_z = k_ρI^e/(ωε_n)`,
`E_u = V^e`, which involve neither new component.

**The residual is the difference's own error and this is demonstrated rather than asserted.** On FR-4
at k_ρ/k₀ = 0.3, halving the step seven times gives
`1.62e-4 / 4.05e-5 / 1.01e-5 / 2.53e-6 / 6.32e-7 / 1.56e-7 / 4.41e-8` — a ratio of **4.00 every
time**. (The amplification is a 13× cancellation: the true E_z is 31.6 and it is the difference of a
G_A^zz term of 406 and a ∇φ term of 375.) A Richardson step removes the h² term, and the gate is then
the **derived** floor `amp·(3e-12/q^k + q⁴/30)` with `q = h/L` — not a round number, because a flat
tolerance either hides a wrong sign or fails on the 4 µm oxide where the geometry forces
`h = 0.15 µm` while the kernel varies on 1.4 mm. **Worst residual measured: 3.7e-9 (mixed), 3.7e-9
(its transpose), 2.0e-4 (G_A^zz, on that oxide) — never more than 0.3× the predicted floor.**

**Two things this cost, both worth remembering.** The step must be bounded by the distance to the
nearest INTERFACE and by the spectral scale `1/max(k_ρ, k_max)`, not by the thinnest layer in the
stack — the first attempt used `1e-3 ×` the thinnest layer and the roundoff floor came out at 1e-3,
which reads exactly like a kernel error. And the floor that survives is **L9a's own stated interior
conditioning**: for interior heights `G_q` has no half-space reflection to be referred to and takes
the VOLTAGE route, whose ~3e-12 relative error a second difference multiplies by (L/h)².

### THE BUG THIS SLICE'S OWN FIXTURE FOUND: the reflection route assumed an AIR top

`LayeredSpectralGreens.ScalarKernel`'s fast path for a top-half-space height pair is written in the
air-top normalisation — **no `(ε₀/ε_top)` prefactor, and Γ^q's `k₀²` where the general form needs the
top region's own `k²`**. Both are exactly right for every stack production or any earlier slice has
built, because they all have air on top, and both are silently wrong otherwise **by a factor of
ε_top**.

**Tier 3's εᵣ-uniform fixture is the first non-air-topped stack in this repository** — a lossy medium
over a PEC with the *same* medium as the top half-space, which is the reduction the brief asks for —
and `G_q` read **4.4× wrong** there. The fix is a guard, not a rewrite: a non-air top falls through to
the VOLTAGE route, which is general by construction and pays L9a's stated ~3e-12 conditioning instead
of ~1e-13. **Nothing air-topped changes path**, which is why every L9a and L9b number is untouched.

### M2 / D3 — the interior inverse transform: THREE extractions, and one has a different SHAPE

`SommerfeldIntegral.EvaluateInterior`. D3's four requirements were met and two of them changed the
design:

- **`FreeSpace` widened to a complex wavenumber**, as a NEW overload. The `double` one is kept and is
  what the shipped path still calls — promoting a real k₀ into a `Complex` multiply re-associates the
  arithmetic, and R-via-1 asks for that path to be bit-identical.
- **THREE closed-form extractions, and the third one's necessity is geometry rather than accuracy.**
  Everything decays like `e^{−k_ρ x}` with x the distance the term travels, so a term stops decaying
  exactly when x = 0. A source in the open half-space has one such term (the direct one, at
  Δ = |z−z′|) because its image is a whole substrate away. **A source sitting ON an interior
  interface — precisely where metal goes — has two**, because `Σ_b = z + z′ − 2z_b` is zero when both
  points are at the bottom of their own region. The coefficients are the k_ρ → ∞ limits of the
  cascade, in which every round trip has died, so the generalised bottom reflection collapses to the
  local Fresnel coefficient (`R_e` for TM, `R_h` for TE — zero at a non-magnetic dielectric
  interface, ∓1 at a wall):

  ```
  G_A^xx → (µ_m/µ₀)[E1 + R_h E3]/(2jk_zm)      G_q    → (ε₀/ε_m)[E1 + R_e E3]/(2jk_zm)
  G_A^zz → (µ_m/µ₀)[E1 + (R_h − 2R_e)E3]/(2jk_zm)
  mixed  → (µ_m/µ₀)(R_h − R_e) E3/(2j k_ρ²)     ← a DIFFERENT shape
  ```
  The `G_A^zz` row reproduces Tier 1's sign flip from the asymptotics as well: over a PEC,
  `R_h = R_e = −1` gives `R_h − 2R_e = +1`.
- **The mixed component's asymptote is not a Sommerfeld exponential at all.** Its direct terms cancel
  outright (`I_i`'s direct term is `sgn(z−z′)/2` on both lines), so what survives is `1/k_ρ²`. It is
  extracted with a regularised static surrogate whose inverse is a **logarithm**, derived here:
  `d/dp ∫e^{−pk}J₀(kρ)dk/k = −1/√(p²+ρ²)`, so the difference of two such integrals is
  `ln[(q+√(q²+ρ²))/(p+√(p²+ρ²))]`. The regulator is subtracted back exactly and costs nothing.
- **The k₀ sinθ / k₀ cosh u substitutions are GONE**, exactly as D3 said they would be: an interior
  source's extractions are referenced to its own complex `k_m`, so `1/k_zm` never blows up on the
  real axis. **But every region's own real `k_i` is still a square-root kink on the contour**, and it
  gets *geometrically clustered* breakpoints rather than one — that is worth three decades. With a
  single breakpoint the real-axis contour met the substituted one at **1.2e-5**; clustered, at
  **4.0e-7**.
- **CROSS-REGION NEEDS NO EXTRACTION AT ALL**, and that is the one cheerful thing in the milestone: a
  transmission chain carries `e^{−jk_z t}` for the full thickness of every region it crosses, so both
  coefficients are zero and the cost lands in the tail's LENGTH rather than in its convergence.

**One measured decision inside the integrator.** The hand-over `kSplit` is NOT pushed past `1/Σ` or
`1/t`. Doing so is the obvious move (hand over only once the surviving exponentials have died, so the
tail is monotone) and on a 4 µm layer that is `k_ρ ~ 1e6`; the head's uniform pre-partition then wants
twenty thousand adaptively-refined panels and the oracle takes **minutes per point** — measured, by
writing it that way first. The tail's J₀-zero partition plus repeated averaging is built for an
alternating algebraically-decaying series and an extra `e^{−k_ρΣ}` only makes it converge sooner.

### Tier 3 — the oracle checked against itself, in the order that is not negotiable

| rung | result |
|---|---|
| εᵣ-uniform over PEC (interior, cross-region and half-space pairs), VALUE | **2.2e-11** |
| …and its same-region REMAINDER, which must be exactly zero | **2.4e-15** |
| vs the shipped `EvaluateLayered` on a top-half-space pair, a different contour | **4.0e-7** |
| a 100× coarsening (216 oracle points, 6 stacks) | **9.6e-10**, 0 of 108 tails short |
| cross-region spatial reciprocity | **5.4e-15** |

**Which pairs the zero-remainder rung applies to is instructive rather than incidental.** The image
term extracted is the reflection off the SOURCE REGION'S OWN FLOOR, so it is the entire answer only
when that floor is the actual reflector — region 1, sitting on the PEC. A pair in the half-space
three invisible interfaces higher has its PEC image a whole stack thickness away, which decays and
therefore belongs in the integral; a cross-region pair has no extraction at all. All of them still
have to produce the right VALUE, which is where the quadrature gets tested.

### M3 / D4 — the height dependence spans exactly FOUR, and that is NOT the same as fittable

**Measured, generalising L9b's R5_3 in two directions at once.** At a fixed k_ρ and a fixed region
pair (m, n), every kernel component's dependence on (z, z′) lies in the span of the four products
`e^{∓jk_zm z′}·e^{∓jk_zn z}`, with coefficients that do not depend on the heights. Four pairs
determine them; **a fifth is predicted to 2.4e-13 (same-region) and 1.7e-13 (CROSS-region)**, over
six stacks, k_ρ/k₀ ∈ {0.3, 2, 15} and **all four kernel components**. The generalisation is free
because `k_zm` and `k_zn` are polarisation-independent, so the TM and TE lines share the same four
families and every component built from them lies in the same four-dimensional space.

**But a four-dimensional span is not a fittable one, and this is the answer to the brief's question
3.** DCIM fits a sum of exponentials in ONE vertical wavenumber, which is ENTIRE in it. The
cross-region product `e^{−jk_zm z′}e^{−jk_zn z}` is entire in neither:

| stack | εᵣ (lower level) | 2nd branch point, k_z0/k₀ | far path / near path | …in k_zm/k₀ |
|---|---|---|---|---|
| FR-4 1.6 mm | 4.4 | −0.0239 − 1.8441 j | 0.546 / 1.022 | 1.8441 − 0.0239 j |
| **GaAs 100 µm** | 12.9 | **−0.0037 − 3.4496 j** | **0.137** / 0.994 | 3.4496 − 0.0037 j |
| PCB 3-layer | 4.4 | −0.0239 − 1.8441 j | 0.546 / 1.022 | 1.8441 − 0.0239 j |
| **MMIC 2-level** | 12.9 | **−0.0037 − 3.4496 j** | **0.137** / 0.994 | 3.4496 − 0.0037 j |
| Alumina, open | 9.8 | −0.0017 − 2.9665 j | 0.252 / 0.993 | 2.9665 − 0.0017 j |
| Oxide on silicon | 4.1 | −0.0012 − 1.7607 j | 0.544 / 0.995 | 1.7607 − 0.0012 j |

`k_zm² = k_m² − k_top² + k_z0²`, so in k_z0 the branch point sits at `±j k₀√(εᵣ,m µᵣ,m − 1)` — **on
the imaginary axis, in the half-plane the sampling path runs into**, which is exactly the shape of
L9b's open-below obstruction. In the source region's own variable it sits on the **real** axis at
`±k₀√(εᵣ,m − 1)`, where a k_zm-parameterised path starts. **L9b measured 59× the free-space kernel
for a cut at 0.178 of the far path; GaAs and the MMIC stack put one at 0.137 — closer.**

**READ THAT TABLE THE WAY L9b's D3 TABLE IS READ AND NO FURTHER.** The locations are DERIVED and the
distances COMPUTED; **what a cut this close costs a fit is NOT measured, because the fit is not
built.** Which pairings it touches, stated precisely rather than optimistically:

- **HIGH–HIGH is clean and L9b measured it.** In k_z0 every interior region's k_zi is even (D1's one
  rule), so no interior branch can matter and the fit is entire.
- **LOW–LOW is NOT automatically clean, and saying so corrects the obvious reading of L9b's D6.** Its
  four families are exact shifts in k_zm — measured above at 2.4e-13 — but their COEFFICIENTS still
  contain the top half-space's k_z0, which is two-valued in k_zm (flipping it sends the top reflection
  to its reciprocal). *"The coefficients do not depend on the heights"* and *"the coefficients are
  entire in the fit variable"* are different statements and only the first is established.
- **LOW–HIGH has no single variable that works at all.** This is what §10.2's warning was actually
  about, and it is now located rather than suspected.

### The cost, measured for what exists (§8 item 5's first half)

Per spectral sample on the MMIC two-level stack at 10 GHz, 20 000 samples, this machine:

| height pairing | G_A^xx | G_q | **G_A^zz** | **mixed** |
|---|---|---|---|---|
| high–high | 1.13 | 0.86 | **2.17** | **1.81** |
| low–low | 0.92 | 1.63 | **1.78** | **1.54** |
| low–high | 0.87 | 1.66 | **2.13** | **1.69** |

µs/sample. **The two new components cost about 2× the vector kernel and about the same as the scalar
one** — they traverse the same cascade and differ only in which of the four line responses they
combine, so this is the expected shape rather than a finding. One **interior oracle point** costs
**40–50 ms** (~15 000 kernel evaluations), against L9b's ~0.13 s for a top-half-space point.

**NOT MEASURED, and it must not be inferred from the above: the cost of a FIT or of a two-level
FILL.** Neither is built. L9a's projection from a per-sample number was wrong by 15–35× and the same
mistake is available here.

### M3 — the fit, and THE FINDING is that the far-field sum rule is a theorem here TOO

`Dcim.FitAtHeights` re-references L8a's whole decomposition from the top half-space's k₀ to the
**source region's own k_m**: the two extracted asymptotes replace the direct term and the
quasi-static image, the poles are residues of *this height pair's* kernel, and the fitted images
invert to `A_i e^{−jk_m R_i}/4πR_i` — the Sommerfeld identity being unchanged for a complex
wavenumber, which is what `FreeSpace(Complex, Complex)` exists for.

**The finding, and it was found the expensive way.** The first version imposed no branch-point
constraint, on the stated grounds that L8a's sum rule is a theorem (`1 + Γ` vanishes at k_ρ = k₀,
removing a pole that would otherwise be there) and that no such identity is available for an interior
source. **The measurement said otherwise.** The scaled error tracked `|C_dir + C_img + ΣA_i|` across
every stack, component and pairing — MMIC's G_A^xx: total 1.9e-3, error 9.3e-5; FR-4's G_A^zz: total
77, error 4.1 — which is L8a's own M4 signature exactly. Measuring `M(k_zm) = 2j k_zm·K` then showed
it is **O(k_zm) on all 24 cases, dead linear over four decades**:

> **An interior source's kernel is simply FINITE at its own region's branch point** — the four-term
> bracket and the resonance denominator both vanish at k_zm = 0 and their ratio does not — so the
> numerator vanishes there by inspection and `C_dir + C_img + ΣA_i = M(0) = 0`. Same theorem, wholly
> different reason. **Asserting the ABSENCE of a theorem needs the same evidence as asserting one**,
> and this slice did not have it. `ViaBasisTests.M3_TheSumRuleIsATHEOREM_HereToo` is that measurement,
> kept as a rung.

**Tier 5, the reported curve** (10 GHz, both interior pairings, all four components, against
`EvaluateInterior`, whose own Tier 3 rungs passed first). As a fraction of the free-space kernel,
worst over ρ/λ ≤ 0.1 and separately over ρ/λ ≤ 1:

| stack | pairing | G_A^xx | G_q | **G_A^zz** | **mixed** |
|---|---|---|---|---|---|
| FR-4 1.6 mm | low–low | 7.2e-7 / 1.4e-3 | 2.0e-6 / 7.6e-3 | 6.1e-5 / 1.1e-1 | 3.2e-6 / 3.8e-3 |
| FR-4 1.6 mm | low–high | 6.7e-7 / 4.8e-3 | 3.0e-5 / 1.3e-2 | 2.3e-4 / 2.7e-2 | 1.9e-7 / 7.4e-5 |
| GaAs 100 µm | low–low | 1.0e-8 / 9.6e-4 | 8.6e-6 / 2.7e-3 | 1.3e-3 / **14** | 3.7e-8 / 1.3e-3 |
| GaAs 100 µm | low–high | 3.3e-7 / 2.4e-3 | 6.0e-6 / 6.2e-3 | 2.8e-3 / **3.7** | 3.8e-8 / 1.3e-3 |
| PCB 3-layer | low–low | 5.8e-7 / 2.6e-3 | 6.5e-6 / 1.9e-2 | 7.5e-6 / 2.8e-2 | 1.4e-3 / 1.4e-3 |
| PCB 3-layer | low–high | 1.3e-6 / 3.1e-3 | 1.7e-5 / 1.2e-2 | 4.2e-5 / 3.0e-2 | 2.2e-8 / 7.0e-5 |
| MMIC 2-level | low–low | 1.9e-7 / 1.2e-5 | 6.2e-6 / 9.5e-4 | 1.7e-3 / 4.6e-3 | 1.5e-3 / 2.1e-3 |
| MMIC 2-level | low–high | 1.4e-6 / 2.4e-4 | 3.2e-5 / 9.6e-4 | 2.8e-3 / 7.3e-1 | 7.8e-8 / 1.0e-6 |

**Three things to read off it, and the third is the answer to the brief's own question 3.**

1. **Inside `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` every component on every grounded stack is
   ≤ 2.8e-3** — inside L9b's 1.6e-2 envelope for the top-half-space pairing and inside L8a's 6e-3 for
   the one-layer starters. **The interior fit is not the weak link there.**
2. **Above it, G_A^zz is the outlier and the refusal is worded on it.** 14× on the GaAs slab at
   ρ/λ = 1, from 1.3e-3 at ρ/λ = 0.1. The mechanism is diagnosed, not guessed: the winning depth set
   carries **Σ|A_i| = 1.1e9 with two images of 5.6e8 at depths of 8.9 cm and 16.9 cm** — depths
   COMPARABLE TO ρ, whose cancellation is exact on the sampled path and degrades as ρ walks into
   them, so the error grows like **ρ⁴**. That is why the refusal is at ρ/λ ≤ 0.1 rather than L9b's 1.0.
3. **THE CROSS-REGION PAIRING IS NOT WORSE THAN THE SAME-REGION ONE.** Read the low–high rows against
   the low–low ones: they are the same size, and on FR-4, PCB and the MMIC stack low–high is *better*
   on G_A^zz. **This is the opposite of what `VerticalCurrentTests.M3_2`'s branch point suggested**,
   and the two together are the honest answer: the second cut is genuinely there, at −3.45 j k₀ and
   0.137 of the far path on GaAs — closer than the 0.178 that cost L9b 59× — **and it does not
   dominate. Locating a cut is not measuring its cost**, and this area has now been burned by
   concluding from a mechanism twice (L9b's first ungrounded refusal, and this).

**A negative result kept, because it cost real time.** An amplitude-conditioning cap — reject a
candidate depth set whose Σ|A_i| runs far past the data it fits — is the obvious remedy for (2) and
it **measured WORSE**: at a cap of 1e4 the GaAs low–low G_A^zz error went 14 → 39 (the rejected
candidate was the better one spatially in spite of its conditioning), and at 1e2 every candidate on
every stack was rejected. It is a correct diagnosis and a bad selector; the code carries the finding
and not the cap. What the outlier actually needs is a depth search that is not Prony-on-two-fixed-
paths, and the standard one — GPOF with an SVD rank truncation — is a general complex eigensolver,
which **D8 declines** and L7b-b and L8a both declined before it.

**Tier 4 (the static limit) was NOT run, and that is a scoping decision rather than an oversight.**
It needs `LayeredStaticGreens` extended to interior heights — a second, genuinely electrostatic
formulation — and its job is to catch an error the oracle SHARES with the fit. Tiers 0, 1, 1b and 3
already validate this kernel by exact reduction (the εᵣ = 1 image pair to 4e-15, free space to
5e-15, the εᵣ-uniform medium to 2e-11 with its remainder at 2e-15, and the MPIE identity against
numerical ∂_z G_q), so the kernel is not where an unmeasured error would be hiding. The fit's own
accuracy is bounded by Tier 5 above. **Naming it as not run is the point; it is L9e's if the
refusal at ρ/λ ≤ 0.1 ever needs to move.**

### M4 — the via IS a rooftop, one dimension over, and two mesher findings came with it

**D2(a), and it turned out not to be a new construction at all.** L8b's D8 put every conductor level
on one shared tensor grid explicitly so that a vertical basis would be an addition rather than a
re-mesh. Take that seriously and make a via's footprint exactly one cell of that grid, and then:

- a horizontal rooftop spans two cells adjacent in **x** (or y) and its unit current crosses their
  shared **edge**;
- a via basis spans two cells adjacent in **z** — the same (IX, IY) on consecutive levels — and its
  unit current crosses their shared **footprint**.

Same object. `∇·f` is the same ±1/Area pulse, so `∫∇·f dS = +1 − 1 = 0` **by construction**, R-via-3
is satisfied without a constraint row, and L8d's D1 (one factorisation, `Y = BᵀZ⁻¹B`, reciprocity
structural) is untouched. **Construction (b) was never reached for.** The one genuine difference is
that the ramp degenerates: the vertical current density is uniform at 1/Area over the footprint,
because the current crosses an area rather than an edge. Same normalisation, not a different one.

**D5's gate, as numbers rather than a comment** (`ViaBasisTests.M4_1`, `M4_2`): the two divergence
signs sum to zero as an **equality**; the round trip `(1/A)·A` is machine precision (1.1e-16, and the
residue belongs to the round trip — L8c's D4 assembles the scalar block from the signs directly and
never forms it); and the current at the two feet is **bit-identical**, being the same expression on
the same cell area.

**R-via-5 — every horizontal basis of every level comes before every vertical one.** The horizontal
block is a PREFIX of the unknown vector, so nothing downstream needs to know whether a mesh has vias
in order to index it. Within the vertical block the order is (via as given, IY, IX), integers
throughout. **What is deliberately NOT claimed is that adding a via leaves the horizontal unknowns
untouched** — it does not and must not, for the reason immediately below.

**FINDING 1 — a via footprint must contribute GRIDLINES, or the via silently vanishes.** R-msh-1's
rule is "a gridline comes from an axis-parallel boundary edge", and a via footprint is nothing else.
Leaving it out is not a loss of resolution, it is a via that is not there: measured on the two-level
MMIC fixture, a 40 µm footprint sat between bulk cell centres at 169.6 µm and 269.3 µm and produced
**zero vertical unknowns, with no error**. With the footprint in the hard set the grid tiles it
exactly and the basis count is a property of the geometry.

**FINDING 2 — but it must NOT get the EDGE GRADING a conductor rim gets.** The graded cells exist to
resolve the 1/√d edge current at a metal rim; a via footprint is an interior feature of continuous
metal and has no such singularity. Grading it anyway measured **2,448 unknowns against 424** for the
same fixture — a 5.8× cost for one 40 µm via, from four graded edges at 3% of the reference length.
Hard gridlines only.

**D6 — the problem type, and how far the change travels was decided rather than discovered.** The new
members (`PlanarConductorLayer.ZM`, `PlanarProblem.MediumStack`, `PlanarProblem.Vias`) are **optional
with a one-slab default**, so the Ui-side `PlanarExtractor` goes on producing the old shape and L9d
gets a design instead of a compile error — `tests/Ui.Tests` is unchanged and green, which is that
decision gated rather than argued. `GroundedSlab` is not deleted (L9a's D5 precedent). The one thing
that could have gone wrong silently is `GuidedWavelengthM`, whose rule changed from "the slab's εᵣ" to
R-msh-3's own "the maximum of εᵣµᵣ over every region": on a one-slab problem those are the same number
and `M4_5` asserts it as an **equality**, which is why §10.7's hero still measures N = 552 exactly.

**Three earned refusals** (`PlanarProblem.CanSolve`), each with its legitimate neighbour accepted in
the same test: a level that is not on any interface of the medium; levels not ordered bottom-to-top;
a via that skips a level.

### M5 — N and the cost, measured; the FILL, not built

§10.7's own FR-4 hero (2.9 × 20 mm) at 10 GHz, against R17's 5,000 ceiling:

| | N | cells | |
|---|---|---|---|
| ONE level | **552** | 297 | exactly L8b's number |
| TWO levels, no via | **1,104** | 594 | **2.00×**, to the unknown |
| TWO levels + one via | **1,140** | 612 | 2 vertical + 34 extra horizontal |

**Two levels plus a via is 2.07× one level and stays comfortably under R17.** The brief's worry —
that "two levels plus vias may cross R17's 5,000 ceiling on ordinary geometry" — does not materialise
on the hero; a library PCell at L8b's worst (2,055 on one level) would land at ~4,200, which is
inside the ceiling and inside the warning band. **A via costs more than its own unknowns** and that
is the shared-grid trade: its footprint refines every level.

**NOT MEASURED, and it must not be inferred: the seconds per frequency point.** L8c's fill is O(N²) in
its cores, so 2.07× N projects to ~4.3× the fill and ~7.7 s per frequency at the hero against L8c's
1.73 s — **but that is a projection and D7 asks for one that has been CHECKED, because L9a's was wrong
by 15–35×.** The fill is not built, so it has not been.

### M5 — the multi-level fill, and only ONE of its three new blocks needed new machinery

That is the payoff of D2's basis choice and L8b's shared grid, and it is worth reading as three
separate statements:

- **The SCALAR block is generalised for free.** `∇·f` is the same ±1/Area pulse whether the basis is
  horizontal or vertical, so L8c's D4 is unchanged and the block is still a signed sum of per-CELL
  entries. What changes is that a cell pair can straddle two levels, so the kernel is picked per
  (level, level) — and **the geometric cores are reused verbatim**, because they are in-plane
  integrals of 1/r, ln r and 1 and the height pair enters only through the coefficients.
- **The ẑẑ block is the scalar block's own cell-pair integral.** A via's in-plane weight is a PULSE
  over its footprint — the same pulse — so the vertical vector entry is that integral with `G_A^zz`
  in place of `G_q` and a factor `ℓ_mℓ_n` from the two z-integrals. **No new core, no new closed
  form, no new quadrature.**
- **The ẑx̂ / ẑŷ block is the one that is genuinely new**, and for a reason: its dyadic entry is
  `j ∂G/∂x`, not a value, so it is the only block that integrates a DERIVATIVE and the only one whose
  integrand is ODD in x − x′. It is done by direct graded quadrature rather than by extraction, which
  is affordable because the mixed kernel's own asymptote is a LOGARITHM rather than a 1/ρ — so
  `G′ ~ −C/ρ` and the integrand goes as `(u−u′)/ρ²`, which is integrable in two dimensions. A
  value-kernel dipole would have given 1/ρ³ and would have needed its own closed form.

**Which pieces are singular depends on the height pair, and that is the whole content of
`FromDcimAtHeights`.** A term whose depth is non-zero is bounded at ρ = 0 and belongs in the
constant, so a CROSS-LEVEL entry has no 1/ρ at all. **But the logarithm does not go away** — a surface
wave is a property of the stack, not of the height pair, and `H₀⁽²⁾(k_pρ)` carries its `ln ρ` whatever
Δ is. Reasoning "different levels, therefore smooth, therefore plain quadrature" pushes a logarithm
through Gauss-Legendre, which is R-fil-3's original failure one level up. The mixed component adds a
**second** logarithm, from the `1/k_ρ²` tail its asymptote inverts to, and it is a genuine `−ln ρ`
exactly when Σ_b = 0 — which is what a via foot produces.

**THE VIA IS TREATED AS ELECTRICALLY SHORT, and that is a decision with a measured condition rather
than a convenience.** *(SUPERSEDED: the z-integral is resolved — see the follow-up section at the end
of this file. The stated premise "a fit per z-quadrature node rather than one per pairing" is the
thing that turned out to be affordable, at ~1% of a de-embedded point.)* The z-integral over its length is `ℓ·G(z_mid) + O((kℓ)²)`; on the MMIC's 3 µm
spacer `kℓ = 2.3e-3` at 10 GHz, so the correction is 5e-6. `PlanarLevels.CanUseMidpointRule` refuses a
via long enough for it to matter **by name** (a 5 mm via measures kℓ = 3.76 and is refused), because
the alternative is a fit per z-quadrature node rather than one per pairing — and D7's cost projection
is written against one per pairing.

**The gates, and the first is the one that matters.**

| rung | result |
|---|---|
| on a one-level mesh with no vias, vs **L8c's own fill** | **6.8e-7** relative |
| `Z` symmetric on a two-level mesh with a via | **bit-identical**, with the mixed block at 1.7e3 Ω so it is not vacuous |
| D7's fit counter | **9 fits** for a two-level structure with a via; the projection was ~12 |
| the second fill on the same set | **0 further fits** |

The first is L9a's D5 precedent applied to the fill: the general path is checked against the shipped
one rather than replacing it. A one-level problem's only pairing is (h, h), so the two reach the same
matrix through **completely different fits** (`Dcim.Fit` vs `Dcim.FitAtHeights`, referenced to k₀ and
to k_m) and **completely different extractions** (`FromDcim` vs `FromDcimAtHeights`) — which is why
6.8e-7 is worth more than a bit-identity would have been.

**The symmetry rung is where the formulation could have failed silently.** `G_A^uz = −G_A^zu` with the
heights swapped, and only the ∂/∂x being ODD in x − x′ supplies the second sign that makes
`Z[m,n] = Z[n,m]`. A formulation carrying only ẑx̂ — which is how formulation C is usually written
down — gives a matrix with an entry on one side of the diagonal and not the other.

**One performance finding, because it decided a design.** The mixed block evaluates `G′(ρ)` at every
point of a 4-D integral, and `G′` is a loop over every fitted image plus a Hankel function per pole.
Directly, a 514-unknown two-level fill **does not finish in two minutes**. Tabulating `G′` radially —
L8c's own `RadialRemainderTable`, on a different function at the same spacing — makes the whole fill
seconds. `UseRadialTable = false` still selects the direct path for the reference comparison.

**The accuracy limit this fill carries, stated rather than discovered.** `G_A^zz` is validated to
ρ/λ ≤ 0.1 (see M3), and §10.7's hero is ~0.67 λ across at 10 GHz — so a via-coupled entry between
widely separated cells is outside it. `PlanarKernelSet.WithinValidatedRange` asks that question once
for a mesh rather than per entry. **It is a limit on the ẑẑ block only**: the scalar and horizontal
vector blocks at every pairing measured ≤ 1.9e-2 out to ρ/λ = 1.

### What is NOT built, and what a continuation starts from

- **EVERYTHING DOWNSTREAM OF THE MATRIX.** `PlanarSystem`, `PlanarSolve`, `PlanarExcitation`,
  `PlanarPort`, `PlanarDeembed`, `PlanarCurrentDensity` and `PlanarKernel` are **untouched**: there is
  a multi-level `Z`, and nothing excites it, factors it, or turns it into s-parameters. `PlanarPort`
  carries a `LayerIndex` that has still never been given a non-zero value, and a port ON a via has no
  meaning yet. **That is L9d's**, and it is what the L9d brief is written against.
- **Tier 4, the static limit.** Not run — see M3 above for why, and for what would have to exist.
- **The `G_A^zz` accuracy limit is carried, not closed.** The fill asks
  `PlanarKernelSet.WithinValidatedRange` and a two-level hero is outside ρ/λ ≤ 0.1. Closing it needs a
  depth search that is not Prony-on-two-fixed-paths, i.e. D8's declined eigensolver — an owner
  decision, not a slice's.
- **`EmCapabilities.LayeredWithVias` is still declared and read by nothing.** §0.2 item 4 asked this
  slice to declare what it means, and with no fill there is still no honest definition: the mesh,
  the basis and the kernel exist, and nothing can yet SOLVE a two-level structure. Declaring the
  capability now would be the advance-refusal mistake in reverse.
- **`DcimModel.Evaluate(ρ, z, z′)`'s refusal string is byte-identical** and still says buried metal
  arrives with L9c. That is now misleading — the interior fit exists, on `FitAtHeights`, and the
  refusal should point at it. **Deliberately not edited here**: L9e owns the refusal audit and this
  slice changed no user-facing string it did not add.

### Out of scope here, on purpose

- **Ports on more than one level, references, extractors, `.cem`, anything in `src/Ui`** — **L9d**.
- **Adaptive frequency sampling, ACA, the N budget, the refusal audit, L9's phase gate** — **L9e**.
  §11's L9 gate sentence is still unresolved and still not this slice's to settle.
- **A gate on published multilayer reference data.** None was built; every rung above is an exact
  reduction, a reciprocity, a self-convergence or a comparison against the repository's own
  independently-validated oracle.
- **Any widening of `Dcim.ValidatedRhoOverLambda`, `ValidatedRhoOverLambdaLayered`, `CanFit`, or any
  existing refusal string.** `Dcim.cs` was not opened. The two refusals ADDED are earned: the
  one-layer `SpectralGreens` refuses the two vertical components by name (and the test asserts the
  general kernel on the SAME slab does answer), and `AsymptoticTopReflection` refuses them because
  they are not built from a reflection coefficient at all.
- **A conformal or diagonal boundary cell; a general complex eigensolver or any new package; a
  losslessness check; a new starter technology.** None of these were touched. The uniform-medium and
  free-space fixtures Tier 1/3 need are hand-built in `VerticalCurrentTests` beside the tests that
  use them, not added to `LayerStacks`.

## L9d — ports on more than one level, references, de-embedding, and the cost

**Fourth of L9's five slices, and the first one that turns a two-level `Z` into an s-parameter.** L9c
left a multi-level matrix that nothing excited. Brief:
`docs/sonnet-briefs/brief-L9d-multilevel-ports-and-references.md`. **M1–M5 are DONE and gated**,
including the Ui-side extractor — which makes this the first L9 slice whose blast radius is not
`src/Engine/Mom/`.

```
PlanarKernelSet.cs     the FIT cache is now SHARED across every For() view (M1)
PlanarSystem.cs        + BuildMultiLevel (M1)
PlanarSolve.cs         + PlanarFrequencyKernel (the discriminated wrapper), PlanarSolveContext gains
                         Levels and a general SolveAt, PlanarPortCalibrator gains standardLevelZ,
                         PlanarSolve.Run gains a PlanarProblem overload, the G_A^zz REFUSAL and D3's
                         slab-top refusal (M1/M3/M5)
PlanarPort.cs          + LayerIndex is int? = infer, D2's ambiguity refusal, the VIA-PORT refusal,
                         PlanarPortResolution carries its LayerIndex (M2)
PlanarProblem.cs       + RequiresGeneralKernel, LevelIsOnSlabTop (M1/M3)
PlanarFill.cs          + PlanarFillCores.DirPos, and FillMultiLevel reuses D6's cached direction
                         cores instead of re-integrating them per entry (M1, and see the COST)
PlanarCurrentDensity.cs+ D4's vertical map — a CURRENT, not a third component of |J| (M5)
PlanarKernel.cs        + LayeredWithVias declared, CanSolve generalised, R-via-6 reaches a caller (M5)
EmKernelRegistry.cs    + the capability is read FROM the kernel rather than restated (M5)

src/Ui/Layout/Em/      PlanarExtractor produces N levels + a LayerStack + vias; EmSetup names them;
                       EmPortExtraction infers a port's LEVEL; EmSnpProvenance hashes all of it (M4)
```

Gate: **973 routine tests in `tests/Engine.Tests` in 51 s** — L9c's own baseline was 965 in 51 s, so
the +8 routine tests cost nothing measurable. Plus **9 methods tagged `Category=Benchmark`, adding
~6 min** (L8's was ~8.5 min, L9a 8 s, L9b ~11 min, L9c ~4 min): every de-embedded point and every
multi-level fill. `tests/Ui.Tests` **4,737 and green** (+5, and one pre-L9d test UPDATED rather than
loosened — see M4). `tests/Firewall.Tests` unchanged (4/4).

### M1 — the kernel is a DISCRIMINATED WRAPPER, not a widened pair, and the sharing was the real bug

The brief's own warning was that widening `PlanarKernelPair` in place is the wrong obvious answer.
It is, for the stated reason — R-mlp-1 requires the one-level path to stay bit-identical and the only
way to promise that is to leave it holding L8d's own objects — but **the failure that widening would
actually have caused was one level down, in the CACHE.**

`PlanarKernelSet.For(cores)` returned a fresh set whose cache was a **copy** of whatever had been
fitted so far. That was harmless at L9c, where nothing solved and only one mesh ever asked. A
de-embedded solve touches **three or more meshes at every frequency**, and each would have refitted
its whole pairing set — turning L9c's measured 9 fits per frequency into 9 per MESH, which is exactly
the "3–5× of a fixed cost" L8d's own caching decision exists to avoid, and which no answer anywhere
would have looked wrong. The fix separates the two halves that were conflated: **the `DcimModel` fit
is shared by every view (it is a property of the component, the height pairing and the frequency
alone); the `PlanarKernelTerms` are derived per view** (`FromDcimAtHeights` re-decomposes an
already-fitted model and is the cheap half). `PlanarKernelSet.FitCount` now counts across views,
because that is the quantity the decision is about. `MultiLevelPortTests.M1_4` asserts a standard
adds **zero** fits to the DUT's.

`PlanarFrequencyKernel` carries either L8's pair or L9's set, and
`PlanarProblem.RequiresGeneralKernel` is the single place the choice is made:
`MediumStack is not null || Layers.Count > 1 || ViaList.Count > 0`. **An explicit `MediumStack` counts
even at one level**, because `Slab` may not describe it and silently solving the slab instead is the
plausible-wrong-answer failure the whole phase is built to avoid.

**R-mlp-1 is pinned by RECONSTRUCTION, not by a tolerance** (`M1_1`): the test writes out the exact
calls L8d's own driver made — `PlanarSolveContext`, `PlanarKernelPair.Fit`, `PlanarPortCalibrator.At`,
`PlanarDeembed.Apply`/`Renormalise` — and compares every de-embedded and raw s-parameter for
**bit-identity**. The Tier oracles carry tolerances and structurally cannot catch a one-ulp move.

### THE COST FINDING: the general fill re-integrated the geometric cores, and it dominates everything

D7 asked for a cost that has been CHECKED rather than projected, and checking it found why.

`PlanarFill.FillMultiLevel`'s horizontal vector block called `PairCores` **fresh, four times per
matrix entry**, while L8c's own `Fill` looks the same numbers up in D6's cached
`VX0/VXLog/VXRad/VXArea`. That was invisible at L9c — a single fill on a small fixture — and is
crippling the moment a calibration standard exists, because **a standard is always single-level, so
every one of its entries takes that branch.** The cores are purely GEOMETRIC (in-plane integrals of
1/r, ln r and 1; the height pair enters only through the coefficients) — which is exactly what L9c's
own note already says of the SCALAR block — so they are now looked up through a new
`PlanarFillCores.DirPos` map. Two consequences worth stating:

- It also puts the expression on **L8c's own associativity** (one coefficient times the summed core,
  rather than the sum of four coefficient-times-core products), so L9c's `M5_1` one-level reduction
  against `PlanarFill.Fill` gets *tighter* rather than looser. It still passes unchanged.
- The remaining per-entry cost is the REMAINDER quadrature, which genuinely cannot be cached: it
  depends on the fitted terms, which depend on the height pairing.

**The measured cost, §8 item 5's answer, taken alone as L8d's own warning requires:**

| §10.7-class two-level MMIC, N = 514 (2 vertical), 4 single-level standards | |
|---|---|
| the DUT (fill + factor + 2 back-substitutions) | 15.7 s |
| **the calibration standards** | **27.5 s** |
| the frequency-independent cores, once, for all 5 meshes | 28.6 s |
| **one de-embedded point** | **71.9 s** |
| a 101-point sweep | **~73 minutes** |

**Against L8d's own 7.66 s per de-embedded point at N = 552, this is 9.4× at essentially the same N.**
The projection in L9c's note — "2.07× N is ~4.3× the fill" — is about the wrong quantity: N barely
moved here (514 against 552); what moved is the **per-entry cost of the general kernel**, which
carries a per-pairing remainder table and a mixed block that integrates a derivative. **L9a's cost
projection was wrong by 15–35× and this one would have been wrong the other way**; the number above is
measured, and it is what says L9e's adaptive frequency sampling is no longer optional. (The kernel-fit
column reads 0.00 s because the general path fits LAZILY inside the fill, so those seconds are inside
the DUT and standard columns rather than beside them.)

### M2 — D1's answer is "ONE INDEX", and the interesting work was deciding WHICH LEVEL

**What broke when L8d's port resolver met a two-level mesh: nothing.** `TryResolve` already filtered
cells by `port.LayerIndex`, and a port explicitly on level 1 resolves onto level 1's own rooftops with
the same basis count, the same width and disjoint indices (`M2_1`). D1's burden of proof was met and
the resolver was not rewritten.

What L9d had to ADD is D2: **a port's LEVEL is part of its identity.** `PlanarPort.LayerIndex` changed
from `int = 0` to `int? = null` meaning *infer* — exactly one candidate level resolves, or the port is
refused by name listing every level it could have meant. Every pre-L9d construction site passes
nothing and every one-level mesh has exactly one candidate, so inference reproduces the old behaviour
exactly. Picking the lowest silently would drive **a different conductor with the same footprint**,
which produces a complete and plausible answer for a structure nobody drew.

**A port ON A VIA is refused, and the refusal is EARNED by showing it is a different OBJECT rather
than an unimplemented case.** L8d's D1 makes a port a delta gap across the shared edge of the two
outermost cells of a conductor END, with the half-cell beyond it as the other terminal. A vertical
basis has no analogue of any of that: its unit current already crosses its shared footprint, its "cut"
is the via itself, and a via has no end in the layout plane. Driving the horizontal rooftops at the
same (x, y) is a perfectly good port — it is simply a **different** one, and `M2_3` measures the
difference structurally: every resolved port's rows lie inside R-via-5's horizontal PREFIX and no
vertical unknown is ever in a port's row. `PlanarPortReference.ViaBetweenLevels` exists so the refusal
can be worded against §0.2 item 2's own option (b) rather than against "not implemented"; a port truly
between two levels is an internal (co-simulation) port, which §10.6 lists as later work.

**L8d's coplanar/differential refusals used to point at "L9". L9 has arrived and neither is built** —
which is the argument for naming WHERE A CAPABILITY ARRIVES rather than a phase number. Both now name
§10.6, and `PlanarPortTests.T0_6` was **updated, not loosened**: it still asserts the destination is
named.

### M3 — D3's standards, and the refusal that is about Z_c's ELECTROSTATICS rather than about levels

A calibration standard is a single-level uniform line on the port's own level, built by
`PlanarCalibration.BuildLine` exactly as L8d built it and filled through the shared `PlanarKernelSet`
at the (z, z) pairing — so it shares the DUT's own same-level fit. `M3_3` asserts the property rather
than the promise: every standard's mesh names one layer, every cell is on layer 0, and no standard
carries a vertical basis. **A standard with a via in it is not a standard**, because the two-line
algebra models the section between the reference planes as a uniform matched line and a via is a
discontinuity in the middle of exactly that.

**The refusal that bounds this is not "multi-level" — it is that C_pul solves ONE electrostatic
problem.** `PlanarDeembed` differences the two standards' static capacitances, and the only static
Green's function in this repository is an image series over a **grounded slab**. That is the right
problem for a line on the slab's own top surface and the wrong one for a level buried inside the
stack, where the return path and every image depth change. The de-embedded S is **referenced** to the
Z_c that comes out, so a wrong C_pul is not a diagnostic inaccuracy — it renormalises every published
s-parameter. `PlanarSolve.Run` therefore refuses a port on a level that is not the slab's top, by
name, pointing at what it would need: a static Green's function at interior heights, which is L9c's
own **un-run Tier 4**. `M3_2` accepts the legitimate neighbour (ports on the level that sits on the
slab) in the same test.

**What C_pul still neglects on a general stack is stated on every run rather than discovered**: an
image series over the slab treats everything ABOVE the port's level as free space, and the note names
the layers (e.g. "3 µm of εᵣ = 2.7 above the port's level").

**Tier 3 re-run through the new code path** (`M3_1`) de-embeds the SAME uniform FR-4 line, with the
same mesh and the same ports, through both the shipped one-slab kernel and the general stack's
interior fit of the identical medium (`LayerStack.FromGroundedSlab`). Worst |ΔS| over 2 and 6 GHz is
well inside the gate — which is what catches a port-indexing error, because a mis-indexed port on a
one-level mesh is still a wrong answer.

**THE DRIFT, measured (`M3_4`, §8 item 3), and the honest reading is about SHAPE rather than
magnitude.** L8d's own drift measurement — the de-embedded S of a uniform section that should be
matched, away from the calibration lengths — read **3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz** on 1.6 mm
FR-4, and L8d diagnosed the mechanism as radiative and surface-wave coupling between the ports,
scaling as f². The two-level MMIC counterpart reads **worst |S₁₁| = 1.01e-1**, and it is **monotone in
frequency** — the same radiative signature, an order of magnitude larger.

**Do not read that 1.01e-1 as "two-level de-embedding is 26× worse than one-level".** The two numbers
are not comparable in magnitude, and saying why is the point: this structure is 400 µm on 100 µm GaAs
and L8d's was 20 mm on 1.6 mm FR-4 — a completely different electrical length, a different substrate
and a different port geometry, and the residual scales with the electrical distance between the
reference plane and the discontinuity. What IS comparable is that the residual is monotone in
frequency rather than oscillatory, which is the radiative signature and NOT the signature of an
algebraic error introduced by calibrating a two-level medium against single-level standards. **So the
answer to "is it legitimate" is yes on the evidence available, and the evidence is a shape argument,
not a tolerance.** A magnitude comparison would need the two fixtures made electrically equivalent,
which the un-run Tier 4 above (a static Green's function at interior heights) is a prerequisite for
anyway.

### M5 — G_A^zz's range is a REFUSAL, and D4's vertical map is a different QUANTITY

**§0.2 item 4.** The fill has been asking `PlanarKernelSet.WithinValidatedRange` since L9c and nothing
acted on the answer. It is now a refusal in `PlanarSolve.Run`, and the reason it is a refusal where
R-prt-13's is only a note is that the two are worded on different measures: R-prt-13's is L8a's STRICT
relative error, and L8c measured the SCALED error a fill actually experiences at ≤ 5.4e-3 out to
ρ/λ = 2.8. `ValidatedRhoOverLambdaAtHeights = 0.1` is an order of magnitude tighter, was measured on
the scaled error, and **G_A^zz reaches 14× the free-space kernel beyond it** — a complete, smooth,
plausible, wrong s-parameter set.

**It binds only where there IS vertical current**, and that scoping is earned rather than assumed
(`M5_2`): the same 400 µm structure at 100 GHz — ρ/λ ≈ 0.14, past the limit — is refused **with** a
via and solves **without** one, because a multi-level structure with no ẑẑ block is governed by the
horizontal components, which L9c measured at ≤ 1.9e-2 out to ρ/λ = 1 on every grounded stack. **Where
it binds a real answer:** §10.7's own FR-4 hero is ~0.67 λ across at 10 GHz, so a via-bearing DUT at
that size is refused outright — the structures this kernel can currently take a via through are
electrically small ones (an MMIC at a few hundred µm is ~0.01 λ and passes comfortably).

**D4 — the vertical map is a CURRENT in amperes, not a third component of |J|.** A via basis's
coefficient is the whole current crossing its shared FOOTPRINT — an area, not an edge — so the
per-cell quantity that is well defined is a current, not a sheet density. Dividing by the footprint
area to make it look like `Jx`/`Jy` would give A/m², and `sqrt(|Jx|²+|Jy|²+|Jz|²)` would then be adding
two dimensions and colouring the result: the map would mean one thing on a cell with a via under it
and another everywhere else. `PlanarCurrentDensityMap.Iz` is carried separately with its own
normalisation and its own caption, non-zero on exactly the two foot cells of each vertical basis, and
`M5_1` asserts that recomputing `Magnitude` from `Jx`/`Jy` alone reproduces it exactly.

**One real bug this found:** `PlanarCurrentDensity.Compute` branched `if (X) … else …`, so a vertical
basis was silently counted as a **Y** one the moment a mesh had a via — a wrong picture rather than a
missing one. The switch is explicit now.

**`EmCapabilities.LayeredWithVias` is finally declared**, and only now. L9c deliberately did not,
because with no solve there was nothing honest to declare; after L9d there is, and what it means is
stated on `PlanarKernel.Capabilities`: N conductor levels on an arbitrary stratified medium, with vias
carrying z-directed current between adjacent levels. The registry reads it **from the kernel** rather
than restating it. D2's rule is untouched — auto-selection still takes extractor VERDICTS, not
geometry.

**R-via-6 finally reaches a caller.** L9c refused a long via inside `PlanarLevels`; `PlanarKernel`
now asks at the sweep's actual top frequency (`CanSolve` can only guess from `MaxFrequencyHz`), so a
5 mm via is refused by name and the 3 µm MMIC spacer is accepted.

### What is NOT built, and what a continuation starts from

- **Adaptive frequency sampling, ACA, N-budget enforcement, the refusal audit, L9's phase gate** —
  **L9e**. The cost above is reported, not acted on.
- **A port on a buried level de-embeds nothing.** It needs a static Green's function at interior
  heights — L9c's un-run Tier 4 — and that is the single most valuable thing anyone could build next
  for this area.
- **`DcimModel.Evaluate(ρ, z, z′)`'s refusal string is still byte-identical** and still says buried
  metal arrives with L9c. L9e owns the refusal audit; this slice changed no user-facing string it did
  not add, except the two port references that pointed at "L9" and could no longer.
- **Losslessness is still not checked anywhere** (R-mlp-2): a two-level open structure with vias
  radiates MORE, not less. Reciprocity and passivity carry over and are gated.

## L9e — adaptive sampling, the N budget, the refusal audit, and the VIA finding

**Last of L9's five slices.** Brief:
`docs/sonnet-briefs/brief-L9e-adaptive-sampling-budget-and-the-phase-gate.md`. **M1-M4 are DONE and
gated; M5's ACA measurement is done.** *(This line used to add "and its PHASE GATE is not". It was
built in a later pass, in this same section — see "L9's PHASE GATE" below.)* See "What is NOT built",
which is §8's own named fault line rather than a gap discovered late.

```
PlanarAdaptiveSweep.cs  NEW — the criterion, both interpolants, the seeding (M1)
PlanarSolve.cs          + PlanarSolveSettings.Adaptive, the refinement loop, SolveRawAt/DeembedAt,
                          PlanarPortCalibrator's raw cache + RestartBranchContinuation, D8's guard
PlanarKernelSet.cs      + MaxLengthOverWidth — the midpoint rule's GEOMETRIC bound (M2)
PlanarKernel.cs         + NarrowestViaFootprint, so the geometric bound reaches a caller
Dcim.cs                 + CanFitAtFrequency (D8), and three refusals re-worded (M4)
LayeredMedium.cs / SpectralGreens.cs / QuasiStaticKernel.cs / ModalDecomposition.cs /
SommerfeldIntegral.cs / PlanarPort.cs / src/Ui/Layout/Em/CrossSectionExtractor.cs   the audit (M4)
```

Gate: **992 routine tests in `tests/Engine.Tests` in 65 s** (991 pass + 1 pre-existing skip) —
against L9d's own 973-in-51 s, so +19 routine tests for +14 s, which puts the tier just over its
~60 s ceiling; **the overrun is real and is stated rather than smoothed** (see "The routine tier"
below). Plus **6 methods tagged `Category=Benchmark`, ~7 min** — the ℓ/w sweep, the interpolant
measurement, the tolerance curve, the budget arithmetic, the working-set cross-check and ACA.
`tests/Ui.Tests` **4,737 and green**, with **two tests UPDATED rather than loosened**
(`EmRefusalWordingTests`, `ExtractionRefusalTests`). `tests/Firewall.Tests` unchanged (4/4).

### THE FINDING: the via is NOT physically right, and the cause is the midpoint rule

> **SUPERSEDED (follow-up brief, `brief-via-z-integral.md`) — the defect below is FIXED.** The
> z-integral is resolved, the ℓ/w curve measures flat to **0.124%** over ℓ/w ∈ [0.01, 5], and
> `MaxLengthOverWidth` is retired. **The measurements below are still the record of what was wrong and
> are what the fix is gated against** — read them, then read the follow-up section at the end of this
> file for what replaced them and why a plain quadrature in z was not the answer.

§0.2 item 1 asked for the check that had never been built, and §8 said that if the via turns out
wrong that is the finding and it outranks the rest of the brief. It is, and it does.

**The kernel is right.** At εᵣ = 1 over a PEC, `G_A^zz` is free space plus a **positive** image
(L9c's current-reflection finding, now measured against an absolute value rather than a symmetry
identity) to **≤ 3.0e-4** over ρ ∈ [1 µm, 1 mm]. Against a NEGATIVE image the same comparison reads
**21**, which is what earns the sign rather than asserting it.

**The fill is right.** The ẑẑ entry, converted to henries and separated from the charge term, matches
an independently-integrated closed form to **≤ 5.1e-5 across ℓ/w ∈ [0.075, 10]**.

**The MIDPOINT RULE is wrong, and it is wrong about a quantity nothing bounds.** L9c evaluates a via's
Green's function at the midpoint of its two feet and multiplies by ℓ, costing "O((kℓ)²) — 5e-6 on a
3 µm MMIC spacer". **That estimate is about the wave factor `e^{−jkR}` alone.** The same substitution
also freezes `1/R` over the via's length, and `1/R` is constant there only while ℓ is small against
the in-plane separations the integral runs over — i.e. against the **FOOTPRINT**. There is no
frequency in that condition at all.

| ℓ/w | 0.01 | 0.05 | 0.075 | 0.1 | 0.5 | 1.0 | 5.0 |
|---|---|---|---|---|---|---|---|
| via inductance high by | 0.67% | 3.3% | **4.9%** | 6.4% | 29% | 55% | 220% |

Linear at small ℓ/w with slope **0.673**, and essentially independent of w over a 16× range of w
(0.62-0.69% at ℓ/w = 0.01), which is what says it is a function of the aspect ratio and not of scale.
**§10.7's own MMIC spacer — 3 µm over a 40 µm footprint — is 4.9% high**, and R-via-6's electrical
bound (kℓ ≤ 0.05) admits **ℓ/w ≈ 12** on a 20 µm footprint at 10 GHz on GaAs, where the answer is
~5× too large.

**The oracle was checked before anything was concluded from it** (seven occasions in this area).
The polar quadrature reproduces its own closed form `4ln(1+√2) − (4/3)(√2−1)` to 6.7e-16; the exact
bar integral is converged at 1e-8 under node refinement; and independently, at w = 40 µm / ℓ = 400 µm
it returns **249.1 pH against 247.4 pH** from Grover's own bar formula plus the image's parallel-bar
mutual — 0.7%, which is that GMD approximation's own accuracy.

**The remedy is the refusal's own advice, and it converges.** Splitting the via across intermediate
levels — n stacked sub-vias each carrying their own midpoint rule over ℓ/n — walks back onto the exact
value: at ℓ/w = 1, **55.3% → 15.8% → 4.2% → 1.14% → 0.68%** for n = 1, 2, 4, 8, 16; at ℓ/w = 10,
**385% → 163% → 62% → 20% → 5.9%**.

**What shipped: a second, GEOMETRIC bound on the same refusal** (`PlanarLevels.MaxLengthOverWidth =
0.5`, ≈ 30% error), carrying the measured slope and naming the split as the fix.
`PlanarKernel.NarrowestViaFootprint` supplies the width from the DRAWN footprint's own bounding box —
before any meshing, so the answer cannot depend on where the footprint fell on the shared grid.
**The z-integral itself is deliberately not built** (§7 forbids it): it would need a fit per
z-quadrature node rather than one per pairing, and D7's whole cost projection is written against one
per pairing.

**What this does NOT say** is that every s-parameter carrying a via is wrong by this much. A via's own
inductance is one contribution among many and how much of it reaches the terminals depends on the
structure. What it does say is that the ẑẑ block's answer is high by a known, geometric amount that
nothing in the kernel bounded until now.

### M1 — the calibrator collision, and the answer is SEPARATE THE EXPENSIVE FROM THE ORDERED

§0.2 item 2's own warning is that M1 has a wrong obvious answer — that adaptive sampling is an
interpolation problem. It is not; the interpolation is the easy half.

`PlanarPortCalibrator` must be stepped in **increasing frequency order** (γ's 2π unwrap and a₂₁'s sign
are both continuations, L8d's D6). Every adaptive scheme inserts its next point in the **middle** of
the interval that disagreed most. The resolution is that these two facts are about different halves of
the same method: the **solve** (fill + factor + back-substitution on every standard mesh — 64% of a
de-embedded point) depends only on the frequency, while the **branch continuation** is a few lines of
algebra that depend on the order. So the raw standard matrices are cached per frequency
(`PlanarPortCalibrator.SolveCount` is the counter that proves it), and after every insertion the
driver calls `RestartBranchContinuation()` and replays the whole sorted set **at zero extra solves**.

**Measured: `SolveCount` stays at 3 across four full passes over three frequencies**, and a replayed
sweep reproduces γ, Z_c and the error box **bit for bit** against the straight-through sequential one.

**The alternative was rejected on a measurement, not on taste.** Making the branch resolution
non-incremental — predicting βΔℓ from the pre-solve ε_eff — needs no cache and no replay, but L8d
already measured that estimate running **15-20% low**, which is why its own standards are designed to
60° rather than 90°. A 20% error in the expected phase is a coin flip on the 2π branch the moment a
section passes half a wavelength.

### M1's other decisions

**D1 — adaptive is a SETTING and OFF is bit-identical.** `PlanarSolveSettings.Adaptive` defaults null;
`PlanarSolve.Run`'s loop body was lifted into two local functions (`SolveRawAt`, `DeembedAt`) that both
paths share, so bit-identity is a property of there being one implementation. `MultiLevelPortTests.M1_1`
— which reconstructs L8d's own call sequence by hand and compares at full precision — is what says the
refactor moved nothing, and it still passes unchanged.

**Tier 1's DENSE REDUCTION is the strongest single check and it passes exactly.** With `Tolerance = 0`
the refinement runs until it has nothing left to refine, and the adaptive path reproduces the
non-adaptive one **bit for bit** — the replayed calibration, the modelled grid and the point assembly
all collapse onto L8d's arithmetic when nothing is being approximated.

**D2 — the criterion is an ERROR, never a residual.** Solve the midpoint, compare the solved S against
what the interpolant PREDICTED there. A criterion on how well an interpolant fits its own nodes is
identically zero by construction, and this codebase has already found the residual-is-not-the-error
trap twice (L7b-b's `ModeCouplingResidual` is anti-correlated with the terminal error; L8a's
`FitResidual` picks one of the worst far-field configurations).

**R-adf-2/3 — the published grid is the user's, and two runs are identical.** A solved point carries
the solver's own matrix **byte for byte** (short-circuited in `PlanarAdaptiveSweep.Model`, not trusted
to exact arithmetic); refinement is bisection in ascending interval-index order, so there is no
magnitude tie to break; the seed set is a deterministic function of the grid alone. The per-port
calibration diagnostics of a modelled point are carried from the **nearest solved** frequency rather
than interpolated — interpolating γ across its own 2π branch is a second modelling claim this does not
need to make, and the note says so.

**Measured on the coarse FR-4 line:** 9 of 17 solved at tol 1e-2; realised worst |ΔS| against the
fully-solved answer of **8.5e-3 / 1.9e-3 / 2.5e-5** at tolerances 1e-1 / 1e-2 / 1e-3, i.e. the
realised error tracks the number asked for.

**D3 — which interpolant is a MEASUREMENT, it is `T4_2`, and it has now been RUN. The spline wins.**
Both are built (`RFNetwork.Interpolate`'s complex cubic spline, reproduced inline so a refinement loop
does not allocate an SNP per candidate; and a Floater-Hormann barycentric rational, d = 3, which needs
no pole extraction to EVALUATE and so does not require the eigensolver D9 declines). Measured on
L8e's own λ_g/4 open stub — the same shape L8's phase gate turns on, and the one structure in this
repository with a measured notch — 33 points over 3-9 GHz at tolerance 1e-2, against the fully-solved
answer, with a smooth 24 mm line beside it (N = 304 and 94):

| structure | interpolant | solved/33 | worst \|ΔS\| vs fully solved |
|---|---|---|---|
| **resonant** | **CubicSpline** | **27** | **1.9e-3** |
| resonant | Rational | 33 | 0 |
| smooth | CubicSpline | 33 | 0 |
| smooth | Rational | 33 | 0 |

**Read the "0" columns as "solved every point", not as "more accurate".** A run that models nothing is
exact by construction and saves nothing — so the only row where either scheme did any work is the
first, and there the spline modelled 6 points with a realised error **inside the 1e-2 asked for**.
The rational accepted no modelled point anywhere: strictly more cost, zero saving. `CubicSpline` stays
the default, now by measurement rather than by assumption; `Rational` stays reachable so the
measurement can be re-taken.

**Two honest limits on it.** The smooth row discriminates nothing at this band and grid — a 24 mm line
is over a guided wavelength long by 9 GHz, so 33 points is already near-minimal and neither scheme has
slack to find. And the observed conservatism of the rational scheme is reported, not explained: no
mechanism was isolated, and "Floater-Hormann oscillates at few nodes" is a plausible story rather than
a measurement.

### M3 — R17 polices a MESH; the user experiences a RUN

D5's own framing, now with numbers. `SurfaceMesher.UnknownCeiling` is checked in three places and
every one asks about one mesh's N. Measured exactly (16·N² for the matrix, `CoreBytes` for the cores —
no estimate anywhere), on §10.7's own hero cross-section stretched toward the ceiling:

| DUT N | standards | matrix | DUT cores | standard cores | **run total** |
|---|---|---|---|---|---|
| 552 | 6 meshes | 5 MB | 2 MB | 10 MB | **17 MB** |
| 1,504 | 6 | 35 MB | 18 MB | 10 MB | **62 MB** |
| 2,932 | 6 | 131 MB | 68 MB | 10 MB | **209 MB** |

Scaled quadratically to the ceiling that is **~607 MB against the 381 MB the refusal's own message
quotes** — and L8c had already corrected §10.7's 400 MB line to 559 MB for a *single* one-level mesh.
**The constant is defensible and its message is not**: 5,000 unknowns really is 381 MB of matrix, and
a de-embedded run — the normal case — holds several meshes' cores alongside it. Whether 5,000 should
move is the owner's call; §7 forbids changing it here.

### D8 — the near-DC hole is closed, and R-dcm-4's band is a refusal with the remedy named

`Dcim.CanFitAtFrequency(k0, stackHeightM, settings)`, asked of the sweep's lowest point in
`PlanarSolve.Run`. Two mechanisms, one check:

- **k₀H < 1e-6 is refused outright.** This closes L8e's own recorded hole — a 6 Hz point spending
  50 s and ending in a raw `Array dimensions exceeded supported range` with no refusal attached. L8e
  left it because nothing could reach it from the EM panel; **M1's adaptive scheme chooses its own
  frequencies, so it can**, which is why the refusal belongs in this slice and not in that one.
- **PathExtent·k₀H < 1 is refused with L9b's own measured numbers** (3.8e-3 at 300 MHz, 2.9e-2 at
  100 MHz on a 1.4 mm stack, *growing* as the frequency falls) and the extent the user would need.

**D8's decision, so neither option is left open: a frequency-aware path extent IS the right fix, and
it is not a one-line change.** The sample budget has to rise with the extent — a wider path at a fixed
`Samples` is a sparser one — and `DcimSettings.Samples` is what L8a's entire accuracy table is
calibrated against. Changing a shipped default on an unmeasured sample budget is the
plausible-wrong-answer failure this phase exists to avoid. So the **refusal** ships, and re-tuning
`(PathExtent, Samples)` together against L8a's own oracle sweep is named as its own job rather than
done blind.

### M4 — the audit, and the sweep is now on ANY phase letter

All five of §3's strings are re-worded, each a narrowing with its test **updated, not loosened**:
`Dcim.Evaluate` now points at `Dcim.FitAtHeights` and says what still cannot be done (de-embed a
buried port) instead of naming L9c; `GroundedSlab.CanHost`'s two point at `LayerStack`/
`LayeredSpectralGreens`; `SpectralGreens.KernelAtHeights` points at `LayeredSpectralGreens.
KernelAtHeights`; and `QuasiStaticKernel`'s **sloped-boundary** refusal — whose *both halves* were
false after L9 — now says plainly that a sloped boundary is outside the 2.5D premise **both** kernels
share and needs a genuinely 3-D formulation nothing here has.

**`EmRefusalWordingTests`' completeness sweep was widened from "L8" to any `arrive*(at|in|with) L…`,**
which is D7's own general rule, and **it immediately caught one more**: `ModalDecomposition.Decompose`
still promised "the general N-conductor case arrives at L7b-b" — L7b-b arrived and built it. Two more
were found by hand outside the sweep's phrasing (`SommerfeldIntegral`'s "is L9c's",
`PlanarPort`'s three "not part of L9"), which is the argument for the sweep being a floor rather than
the whole audit.

**`Dcim.CanFit`'s two refusals were left alone**, as §3 requires — except the closed-guide one, which
§3 explicitly permits touching if it names the decomposition rather than the phase, and now names a
discrete waveguide-mode expansion.

### Tier 5 — ACA is DEFERRED, with the number

D6 asked for a measurement before a feature. Partially-pivoted ACA (here with a **full** pivot, which
overstates what a practical scheme achieves — the asymmetry is the right way round for a deferral) on
far-field blocks of a real N = 790 fill at 10 GHz:

| block | size | separation/λ | rank @1e-3 | rank/min(m,n) |
|---|---|---|---|---|
| [0, 392] | 98×98 | 0.12 | 60 | **61%** |
| [0, 588] | 98×98 | 0.19 | 57 | **58%** |
| [98, 490] | 98×98 | 0.16 | 61 | **62%** |
| [0, 686] | 98×98 | 0.31 | 52 | **53%** |

**A rank-revealing scheme would still compute over half of each far block, plus the pivoting.** The
reason is structural rather than a tuning failure: the blocks reachable at N ≈ 1,000-5,000 are small,
and a MoM block only becomes strongly low-rank once the two clusters are **many wavelengths** apart —
which a structure fitting under R17's ceiling largely is not. And a compressed matrix needs a solver
that consumes it: an iterative one, whose convergence on a MoM system is not guaranteed and is its own
research item, replacing a direct solve L8c measured at 42.8 s against a 21.8 s fill. **Deferred with
the measurement as the reason** — the precedents are L7b-b's Route B and L9c's amplitude cap.

### The routine tier is OVER its ceiling, and that is reported rather than hidden

`tests/Engine.Tests` is **992 routine tests in 65 s** against L9d's 973-in-51 s and the ~60 s
ceiling. The overrun is this slice's own: the adaptive tests need a REAL sweep (a pure-function test
cannot exercise the calibrator replay, which is the whole design), and even on `PlanarLineFixtures.
Coarse` a de-embedded sweep is seconds. Everything measured at or above ~5 s is already tagged
(`AdaptiveSweepTests.T1_2` 16 s, `AdaptiveSweepTests.T4_1` and `T4_2` ~3-4 min each,
`ViaPhysicsTests.T3_1` 54 s, `PlanarBudgetTests.T4_3` 68 s / `T4_4` ~1 min / `T5_1` 6 s — 7 methods,
~10 min added to the opt-in tier, which is now roughly 40 min in total). **Getting back under 60 s means moving more of `AdaptiveSweepTests`
behind the tag, which trades the routine gate's own coverage of the collision fix for wall clock** —
a call worth making deliberately rather than by reflex, so it is left visible here.

### §11's L9 gate sentence — the PROPOSAL, for the owner to rule on

§11's L9 row reads *"Multi-layer structure with backside vias; agreement with published reference
structures."* §0.2 item 7 records that L9a found the second clause cannot survive this project's own
rules and "proposed a replacement" — and that the proposal was made conversationally and lost.
Reconstructing it and putting it in front of the owner is this slice's, so here it is.

**Strike the second clause.** §10.9's rule is that golden data becomes a gate only when the owner has
approved it, and a published multilayer S-parameter almost always arrives **without a verifiable
stackup** — no tanδ, no metal thickness, often no dielectric tolerance. A gate resting on one measures
the transcription, not the kernel, and when it disagrees there is no way to tell which. That is the
same reasoning that made L7b's Tier C3 a reported non-result rather than a loosened tolerance.

**Replace it with three self-consistency checks, all external-data-free**, and note that the first is
now **partly delivered by this slice** and its result is a finding rather than a pass:

1. **A backside via's inductance against a closed form and against its own convergence.** Tiers 2 and
   3 above, on the MMIC starter — where ρ/λ ≈ 0.01 is comfortably inside `G_A^zz`'s validated 0.1, and
   §10.7's FR-4 hero at ~0.67 λ is refused by construction (§0.2 item 5; **do not widen
   `ValidatedRhoOverLambdaAtHeights` because the gate is inconvenient** — L9c measured the 14× error
   that justifies the number). **This is built and it FAILS as a pass**: the kernel and fill are right
   and the midpoint rule is 4.9% high on the MMIC spacer. A gate here should be stated on the split
   via (which converges) or on the geometric bound, not on the raw rule.
2. **A two-level coupled structure against the one-level reduction it degenerates to** — increase the
   inter-level spacing and the answer must converge onto two independent single-level results computed
   by the shipped path. An exact-limit check with no external data, the analogue of L7b's own
   far-apart gate. **Not built.**
3. **A physically-predictable signature a wrong sign or a wrong image would destroy** — a via's series
   inductance showing as a rising |S₁₁| with frequency, and a broadside-coupled pair's coupling
   falling with spacing. L8e's own gate used a λ_g/4 stub notch and a bend's rising |S₁₁| for exactly
   this reason. **Not built.**

**Also worth the owner's ruling: §0.2 item 6 is confirmed and saves a slice.** The buried-level
de-embedding refusal does NOT block this gate — a backside via runs from the top metal down to the
ground plane, so **both ports are on the top level** and L9c's un-run Tier 4 is not a prerequisite.
Do not scope it into L9.

### L9's PHASE GATE — built, and it found two things before it passed anything

`tests/Ui.Tests/Em/L9PhaseGateTests.cs`, modelled on L8e's own gate and living in `Ui.Tests` for the
same reason: what a phase gate adds over the engine's own tiers is the **product path** — drawn
artwork → extractor → registry → kernel → `DataSet` → `.snp` — and three of those five live in
`src/Ui`. Every number the kernel itself is judged on is already in the tiers above.

**FINDING 1 — a BACKSIDE via is not representable through the product path at all, and that is
structural rather than a scoping convenience.** §11's own gate sentence asks for one. A backside via
joins a signal level to the **ground plane**, and the ground plane is the laterally infinite plane the
Green's function handles analytically — it is never a meshed level. L9c's via basis is a rooftop
spanning two ADJACENT MESHED levels, so a via to ground needs an **attachment (half) basis
terminating on the PEC boundary**, which does not exist. `PlanarExtractor.BuildVias` already drops it
with a note (its span names a ground-reference conductor, which is never an analysis level), so the
behaviour is correct and reported — it simply is not the thing §11 asks to gate. **What IS
representable on the MMIC starter is the Metal1↔Metal2 post**, the airbridge the stackup was built
for, and that is what the gate uses. Adding the attachment basis is now the second-most valuable
thing anyone could build for this area, after L9c's un-run Tier 4.

**FINDING 2 — the Ui-side via extraction was DEAD CODE, and the gate is what reached it.**
`PlanarExtractor.BuildStack` skips every `StackupKind.Via` entry (correctly — a via has no z band),
but the `ViaShape` branch looked its drawing layer up in the map built FROM those bands, so the lookup
could never match: **every drawn via was silently ignored and `BuildVias` was unreachable**, which is
exactly why it had no test. Fixed with a separate `BuildViaBinding` read straight off the stackup; see
`src/Ui/Layout/Em/CLAUDE.md`. The gate catches it structurally rather than by watching a number — see
gate 1 below.

**The three gates, and why gate 1 is a COMPARISON rather than an absolute.** L9d measured the
two-level de-embedding residual at worst |S₁₁| ≈ 1.0e-1 on a matched section, which is the same order
as a short airbridge's own reflection — so the obvious signature ("a series via inductance shows as
|S₁₁| rising with frequency") is reported and NOT gated, because gating it would be gating the
residual. What is unambiguous is that the fixture's Metal1 has a **gap**: with the posts, current
crosses it; without them, only a floating bridge couples across capacitively.

1. **Gate 1 — the vias carry the current.** |S₂₁| of the bridged structure against |S₂₁| of the same
   artwork with the posts removed, plus reciprocity and passivity (never losslessness — R-adf-4).
   Measured on a 300 × 100 µm airbridge on the MMIC starter at the shipping mesh (N = 1,023 with 8
   vertical unknowns, against N = 806 with the posts removed):

   | f | \|S₂₁\| bridged | \|S₂₁\| open | ratio | \|S₁₁\| bridged |
   |---|---|---|---|---|
   | 10 GHz | **0.9993** | 0.0502 | **19.9×** | 0.0364 |
   | 30 GHz | **0.9991** | 0.1468 | **6.8×** | 0.0352 |

   With the posts the bridge transmits essentially perfectly; without them all that is left is the
   capacitive gap, whose own |S₂₁| rises with frequency exactly as a series capacitance must. There is
   no tolerance to argue about here, which is the point of choosing this comparison. **|S₁₁| falls
   slightly rather than rising** — at 300 µm the line's own contribution dominates the two posts', and
   the change is inside the residual, which is why the signature is reported and not gated.
2. **Gate 2 — the two-level answer degenerates onto the shipped one-level one.** A Metal1 line with two
   ports, shadowed by a floating Metal2 strip carrying no port and no via; open the gap and the
   perturbation must fall. The one-level run takes L8's shipped one-slab path and the two-level runs
   take the general kernel, so this is the one place the two are compared through the whole product
   path. **The gate is the ORDERING**; the absolute closeness is bounded by that same ~1e-1 residual,
   so a tight tolerance there would be a tolerance on the residual under another name.

   **FINDING 3 — this gate's own first premise was wrong, and the measurement is what said so.**
   Written as "a strip 3 µm away perturbs the line more than one 50 µm away" it FAILED. Worst |ΔS|
   against the one-level answer, a 300 × 100 µm Metal1 line at 20 GHz (N = 314 one level, 594 two):

   | air gap | 3 µm | 50 µm | 400 µm |
   |---|---|---|---|
   | worst \|ΔS\| vs the shipped one-level answer | 8.87e-4 | **7.12e-3** | **5.27e-4** |

   The perturbation has a **maximum around 50 µm**, and that is not a kernel defect — it is what a
   **floating** conductor does. Its effect vanishes at BOTH ends: as the gap closes it is capacitively
   tied to the line and rides at the line's own potential (the pair behaves as one thicker conductor),
   and as the gap opens it decouples. A **driven or terminated** second line would fall monotonically.
   The gate is therefore stated past the maximum, where the limit is a theorem — **13.5× down from
   50 µm to 400 µm** — and the near point is kept and reported so the non-monotonicity stays visible
   rather than being quietly designed around.
3. **Gate 3 — the wiring**, and it is the only routine one: the extraction produces two levels and two
   vias with the equal-area square, a backside via is dropped WITH its note, and a via long against its
   own footprint is refused by name — the last of which arrives from `PlanarKernel.CanSolve`, before
   anything is meshed or filled.

**Cost, measured alone as L8d's own warning requires: 5 m 28 s + 6 m 29 s = 11.97 min** for the two
Benchmark gates, on top of L9e's ~10 min and the ~8.5 min L8's own gate costs. The three wiring tests
are routine at ~25 ms together, because none of them fills a matrix.

**Sizing, because two constraints bind here that did not bind L8.** `G_A^zz` is validated to
ρ/λ ≤ 0.1 (free-space λ), so the fixtures are 300 µm at 10-30 GHz — 0.03 λ. **Do not widen
`ValidatedRhoOverLambdaAtHeights` to fit a bigger fixture**; L9c measured the 14× error that justifies
it. And the mesh's narrowest conductor dimension per axis sets the pitch, so a via footprint landing a
few µm from a metal edge makes a sliver run and multiplies N for nothing — every edge in these
fixtures is ≥ 20 µm from every other.

### What is NOT built, and what a continuation starts from

- **The attachment basis a BACKSIDE via needs** — finding 1 above.
- **D3's resonant interpolant measurement** is now RUN and the spline won; see the D3 section above.
- **The via's z-integral.** §7 forbids it here; the geometric bound above is what stands in for it,
  and splitting the via is the measured remedy. **— BUILT, in the follow-up section at the end of this
  file. The geometric bound is retired and the curve is flat to 0.124%.**
- **ACA**, deferred with the numbers above.
- **A frequency-aware `PathExtent`**, deferred with its own reason (the sample budget).
- **A losslessness check** — still not added anywhere, and still more true with vias (R-adf-4).

## The via's z-integral — removing the midpoint rule (follow-up to L9)

**A FOLLOW-UP to L9, not a sixth slice of it.** Brief: `docs/sonnet-briefs/brief-via-z-integral.md`.
It fixes the one defect L9e found and deliberately bounded rather than repaired.

```
RectangleIntegrals.cs   + Corner0AtOffset / InverseAtOffset — the LIFTED rectangle integral
ViaZIntegral.cs         NEW — the split, the prism core, the z-averaged terms and mixed derivative
SingularExtraction.cs   + FromDcimAtHeightsMinusStaticAsymptotes, + PlanarKernelTerms.Combine,
                          + RadialRemainderTable.BuildFrom
PlanarKernelSet.cs      + GetMinusStaticAsymptotes / Model / Asymptote; MaxLengthOverWidth RETIRED,
                          CanUseMidpointRule → CanRepresentVias and re-worded
PlanarFill.cs           FillMultiLevel's ẑẑ and mixed branches; + ViaZNodes / ViaZStaticNodes
PlanarKernel.cs         NarrowestViaFootprint deleted with the bound it fed
```

Gate: **993 routine tests in `tests/Engine.Tests` in 70 s** (L9e's own baseline: 992 in 65 s — one
test added, and the tier is where L9e left it, still just over its ~60 s ceiling). Plus **9 methods
tagged `Category=Benchmark`, ~20 min** in this file's area. `tests/Ui.Tests` **4,740 and green**;
`tests/Firewall.Tests` unchanged (4/4).

### THE GATE: the ℓ/w curve is FLAT

L9e's table, re-measured against the FILL rather than against the closed form of what the fill used
to compute — three footprint widths spanning 16×, `ViaPhysicsTests.T3_1`:

| ℓ/w | 0.01 | 0.05 | 0.075 | 0.1 | 0.5 | 1.0 | 5.0 |
|---|---|---|---|---|---|---|---|
| **now** (w = 10/40/160 µm, worst) | 0.01% | 0.01% | 0.01% | 0.02% | 0.00% | 0.02% | **0.12%** |
| L9e's midpoint rule | 0.67% | 3.3% | 4.9% | 6.4% | 29% | 55% | 220% |

**Worst 0.124% over ℓ/w ∈ [0.01, 5] and a 16× range of w**, against a slope of 0.673 that is simply
gone. §10.7's own 3 µm-over-40 µm MMIC post was 4.9% high and now reads 0.00%.

**And n = 1 now equals n = 8 — to 0.00% at ℓ/w = 1 and 0.03% at ℓ/w = 10** — Tier 3, `T3_1b`, gated at
0.5%. L9e's own split-via chain
(55.3% → 15.8% → 4.2% → 1.14% at ℓ/w = 1; 385% → 163% → 62% → 20% at ℓ/w = 10) is reproduced by a
SINGLE via at every rung, so subdivision is now an INVARIANCE rather than a convergence. That is the
stronger statement and it is what the test asserts.

### WHY A PLAIN QUADRATURE IN z IS NOT THE ANSWER, and this is the whole design

The obvious fix — a Gauss rule in z, evaluating the kernel at n_z² height pairs — does not work, and
the reason is structural rather than a matter of order:

- At a height pair with Δ = |z − z′| > 0 the kernel is BOUNDED at ρ = 0 and its whole ρ-structure
  lives on the scale Δ. `FromDcimAtHeights` puts that in the CONSTANT and leaves the rest in the
  smooth remainder — which the fill integrates with 8 Gauss nodes across a cell and interpolates from
  a table spaced at 2% of one. **When Δ ≪ cell, neither resolves it**, and Δ ≤ ℓ is exactly the
  regime the whole defect lives in.
- The exact answer knows this. `∫∫dz dz′ /√(ρ²+(z−z′)²)` is `2ℓ·ln(2ℓ/ρ) − 2ℓ` for ρ ≪ ℓ and `ℓ²/ρ`
  for ρ ≫ ℓ. **A discrete z-rule reproduces neither limit**: its ρ → 0 behaviour is `Σ_a w_a²·C/ρ`
  from the diagonal nodes alone, i.e. it keeps a 1/ρ the true integral does not have.

So the integral is SPLIT, along the line the kernel's own decomposition already provides:

- **The SINGULAR half — the two extracted asymptotes — is closed form in z, and needs no fit at
  all.** Their coefficients are the k_ρ → ∞ limits of the cascade (the source region's own Fresnel
  coefficients), so they do not depend on the heights — **measured at exactly 0 drift over five
  height pairs**, `M1_1` — and their depths are exactly Δ and Σ_b. The static part `C/(4πR)` is then
  integrated over the two prisms exactly: the z-double-integral reduces to a ONE-dimensional integral
  in t (the integrand depends on z, z′ only through t, and its density is a trapezoid with four
  knots), the |t| kink at t = 0 becomes an ordinary PANEL BOUNDARY rather than a diagonal ridge inside
  a tensor rule, and the in-plane integral is the closed form `RectangleIntegrals.InverseAtOffset`.
  **Outer Gauss, inner closed form — L8c's own structure one dimension up**, and frequency-independent
  in D6's sense.
- **The BOUNDED half takes an ordinary Gauss rule in z, applied to the TERMS rather than to the
  ENTRY.** The entry is linear in the kernel, so `Σ_ab w_a w_b ⟨G_ab⟩ = ⟨Σ_ab w_a w_b G_ab⟩` and the
  fill's O(N²) cell-pair work happens exactly once, at today's cost. `PlanarKernelTerms.Combine` is
  that, and it is exact rather than approximate because every extraction coefficient and the
  remainder are all linear in the kernel.

### M1 — the cost premise L9c declined on is FALSE, measured

L9c wrote: *"it would need a fit per z-quadrature node rather than one per pairing, and D7's cost
projection is written against one per pairing."* On L9d's own two-level fixture (N = 514, one via,
one via span, two levels), `ViaPhysicsTests.M1_1`:

| | n_z = 2 | n_z = 4 | n_z = 8 |
|---|---|---|---|
| ẑẑ pairings (unordered node pairs) | 3 | 10 | 36 |
| mixed pairings (node × level) | 4 | 8 | 16 |
| **added over the midpoint rule** | **4** | **15** | **49** |
| …at 105 ms each | 0.42 s | **1.58 s** | 5.15 s |
| …as a fraction of a 149.9 s de-embedded point | 0.28% | **1.05%** | 3.43% |

One added height pair costs **`Dcim.FitAtHeights` 89.5 ms + `FromDcimAtHeights` 0.006 ms + a radial
remainder table 15.6 ms (10,636 samples, 166 kB)**. **The count is a property of the PAIRING SET, not
of N** — every via of one drawn layer spans the same two levels — so it does not grow with the
matrix at all. The vertical block is 2 unknowns of 514 here and 8 of 1,023 on L9's own phase-gate
fixture; a treatment 16× dearer there is invisible.

**Measured end to end: a de-embedded point at N = 514 reads 65.5 s, taken ALONE, against L9d's own
71.9 s for the same test.** The added fits are ~1.3 s and are not resolvable inside that spread. The
opt-in tier is unchanged in size.

### M1's negative result: FOUR fits do NOT predict a fifth, and the reason is derivable

L9c's M3 established that the interior height dependence spans exactly FOUR exponential families with
height-independent coefficients, and the brief asked whether that makes a fifth height pair a
constant-coefficient combination of four fitted ones — the way L9b's D5 shift makes a top-half-space
pair one fit. **It does not, and the reason comes before the measurement**: the four basis functions
`e^{∓jk_zm z}e^{∓jk_zn z′}` are themselves functions of k_ρ, so the 4×4 matrix that recovers the
coefficients is k_ρ-dependent and does not survive the inverse transform. That is NOT L9b's D5, where
the height pair shifts a DEPTH and every amplitude is unchanged.

**Measured anyway, and the number is worth reading carefully: 8.8e-4** of the free-space kernel over
ρ/λ ∈ [1e-3, 0.1], coefficients solved from four ρ points. That is *inside* the interior fit's own
2.8e-3 envelope, so the span is **not decisively refuted** — it is refuted by derivation and merely
not contradicted by measurement. It was not pursued because route (a) is exact, costs 1%, and needs
no new algebra. Recorded rather than dropped: if the fit ever becomes the cost driver, this is the
first thing to re-measure, on a fixture where 8.8e-4 would be decisive.

### R-viz-3 — the z-quadrature's ORDER, as a table

`ViaPhysicsTests.T3_1c`, at εᵣ = 1 over a PEC where the kernel is exact:

| n_z | ℓ/w = 0.075 | ℓ/w = 1 | ℓ/w = 5 |
|---|---|---|---|
| 1 | 0.0037% | −0.0043% | −0.0238% |
| 2 | 0.0003% | 0.0019% | −0.0148% |
| 4 | −0.0012% | −0.0031% | −0.0167% |
| 8 | −0.0010% | 0.0003% | −0.0186% |

…and the singular half's own t-rule, refined SEPARATELY so the two orders are not conflated (L8c's
Tier 6 shape): −0.0254% / −0.0031% / −0.0030% at 4 / 10 / 20 nodes per panel. **`ViaZStaticNodes = 10`.**

**The εᵣ = 1 table cannot measure what n_z is for**, and saying so before reading it off is the point:
there the only z-dependence outside the closed form is the wave factor. A grounded layered stack also
has surface-wave poles whose residues move with the heights and fitted images whose depths do, and
neither exists in the reduction — concluding from it alone would be exactly the mistake L9b's own
degenerate `OpenBelow` fixture is on the record for. **So the sweep is re-run on the MMIC two-level
stack**, where the via spans its whole region and the bounded half therefore has the most z-variation
it can have rather than the least. Against n_z = 8, the vertical blocks read:

| n_z | 1 | 2 | 4 |
|---|---|---|---|
| worst \|ΔZ\|/max\|Z\| over every entry touching a via | **5.6e-8** | 2.0e-9 | 1.3e-8 |

**`ViaZNodes = 2`, and the reason is that n_z = 1 would pass everything above.** This rule is simply
not where the accuracy comes from — the closed-form half carries it — so the default goes to the cheap
end, which is L8c's own precedent (its extraction order is 1 rather than 2 because "order 2 buys
nothing"). 2 is the smallest setting that is a genuine QUADRATURE; 1 is a midpoint rule, and reading
this setting as "midpoint" is precisely the mistake the whole change exists to undo. It costs **3 fits
per via span against 10 at n_z = 4 — 0.28% of a de-embedded point instead of 1.05%.**

### M5 — `MaxLengthOverWidth` is RETIRED; `MaxElectricalLength` stays and is now about the BASIS

`PlanarLevels.CanUseMidpointRule` is `CanRepresentVias`, and its geometric arm is gone with
`PlanarKernel.NarrowestViaFootprint`. Tier 2 is what earns that: there is nothing left for a geometric
bound to refuse.

**The electrical bound is kept, and its wording changed because what it bounds changed.** It is no
longer the quadrature — it is L9c's BASIS: one z-rooftop per inter-level gap, so the current a via
carries is UNIFORM along its whole length. That is exact for a short via and wrong for a resonant one
however well the Green's function is integrated, and no z rule removes it. The remedy the refusal
names is unchanged (split the via across intermediate levels), for a different reason: subdivision now
buys a current PROFILE rather than a better integral.

**RETIRING IT WIDENS NOTHING, and the report has to say so.**
`Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` on `G_A^zz` already refuses §10.7's own FR-4 hero outright
(~0.67 λ at 10 GHz), so a via-bearing full-wave run is restricted to electrically small structures
either way. This makes the answers that were already reachable correct; it does not unlock larger
geometry, and `Dcim.cs`'s constants and refusal strings are byte-identical.

### R-viz-1 / R-viz-6 — what did NOT move

- **Nothing without a vertical basis moves by one ulp.** `ViaBasisTests.M5_6` sweeps `ViaZNodes` over
  {1, 4, 9} — a range that changes a via answer by orders of magnitude — on a two-level problem with no
  via and on a one-level one, and asserts **exact equality** of every entry. That covers every
  calibration standard (always single-level), every L8 path, and the whole scalar and horizontal
  vector blocks in one statement. `PlanarFill.Fill` was not opened.
- **Reciprocity stays STRUCTURAL and was re-verified as such.** `ViaBasisTests.M5_2` — `Z[m,n]` and
  `Z[n,m]` bit-identical on a two-level mesh with a via, with the mixed block asserted non-zero so it
  cannot pass for the wrong reason — is unchanged and passes. The z rule preserves it by construction:
  the ẑẑ node sets on the two sides of the diagonal are the same set (the terms are canonicalised on
  the SPAN pair, and the entry is still computed on m ≤ n and mirrored).
- **D7's fit counter is UPDATED, not loosened.** 9 → **13** on the two-level fixture, and the bound is
  now written as arithmetic (`6 + n_z(n_z+1)/2 + n_z·levels`) rather than as a round number, so the
  next change to it has to be justified the same way. It is still one fit per PAIRING and still
  independent of N — which is the failure the counter exists to catch.

### The mixed block (R-viz-5) cost NOTHING per entry, and that is the point

Its integrand is `j ∂G/∂x` and the block is done by direct graded quadrature over four nested loops —
the expensive one. Adding a z rule there naively would be n_z times that. Instead the z-average is
folded into the radial DERIVATIVE the block already consumes, so **`MixedEntry` is called exactly as
often as it was**. The asymptote's own z-integral is closed form too: its near-depth piece is
`ρ/(r_p(p+r_p))` with `p = Σ_b` linear in z, and `∫dp ρ/(r_p(p+r_p)) = −ρ/(p+√(p²+ρ²))`, so the
singular part of the average is two endpoint evaluations. The cost is n_z fits and one table.

### Two ORACLES were wrong first — the eighth and ninth occasions in this area

- **The lifted rectangle integral's own check.** A uniformly panelled quadrature read **9.5e-5** from
  `InverseAtOffset` at c = 1e-3 while agreeing to 2e-15 at c ≥ 0.05. That is the signature of the
  CHECK failing, not the thing checked: at c = 1e-3 the integrand is a spike 1e-3 wide on a 2 × 2.5
  rectangle, 50× narrower than a uniform panel. Grading the panels toward the origin closes it to
  1e-13. **The closed form was never wrong**, and the run that says so is kept in `T2_0` beside the
  number the uniform rule gave.
- **`T2_2` used to compare the fill against `MidpointInductance` and now compares it against
  `ExactInductance`.** That is not a loosened test: reproducing the midpoint value is now the failure.
  Both columns are printed so the size of what moved stays visible.

### What is NOT built, and what a continuation starts from

- **The ATTACHMENT (half) basis a backside via needs.** Unchanged, and still the second-most valuable
  thing to build in this area.
- **A via with more than one degree of freedom in z.** `MaxElectricalLength` is exactly this gap, now
  that the integral is not one. Subdividing across intermediate levels is the workaround and it works;
  a genuine z-rooftop chain inside one gap would be the fix.
- **The four-fit spatial recombination**, measured at 8.8e-4 and not pursued — see M1's negative
  result for why, and for what would make it worth re-measuring.
- **`Dcim.ValidatedRhoOverLambdaAtHeights`**, untouched, and it is the binding limit on every
  via-bearing structure.

## The GROUND VIA — the attachment basis (follow-up to L9, gap A)

**A FOLLOW-UP to L9, like the via z-integral before it.** Brief:
`docs/sonnet-briefs/brief-ground-vias-and-interior-electrostatics.md`, **Part A only** — Part B (the
interior electrostatic Green's function) is not started; see the end of this section.

It closes the gap L9's own phase gate found and named as the most valuable remaining work in this
area: **a backside via was not representable by this kernel at all.** L9c's via basis is a rooftop
spanning two adjacent MESHED levels; a via to ground joins a signal level to the laterally infinite
PEC the Green's function handles analytically, which is never a meshed level. On a GaAs MMIC that is
how a source terminal reaches ground — the commonest via there is.

```
PlanarMesh.cs            + PlanarBasis.AttachesToGround (optional, defaulted)
PlanarBasisFunctions.cs  + the half basis's Halves/NetCharge, and the two invariants it breaks
PlanarKernelSet.cs       + PlanarLevels.GroundZ / AttachmentLengthOf; MaxElectricalLength 0.05 → 0.30
PlanarProblem.cs         + PlanarVia.GroundTerminal / ToGround; CanSolve's PEC-bottom refusal
SurfaceMesher.cs         the ground-via path — one meshed foot, gridlines but no edge grading
PlanarFill.cs            SpanOf takes the BASIS, so an attachment spans GroundZ → its own level
PlanarCurrentDensity.cs  an attachment names one cell twice and must not be counted twice
PlanarKernel.cs          the electrical verdict asks about the attachment's own span too
PlanarExtractor.cs (Ui)  BuildVias produces a ground attachment — for the RIGHT ground only
```

### THE DELIVERABLE: the ground via's own inductance, against a closed form

`ViaPhysicsTests.T4_1`. §2's observation is exact and no new oracle was written: a via to ground at
εᵣ = 1 over a PEC is a bar of length ℓ **plus its equal-direction image** (L9e's T2_1 earned that
sign — the CURRENT reflection at a PEC is +1), i.e. exactly the half of a 2ℓ bar `ExactInductance`
already integrates in closed form, evaluated at z₀ = 0.

| ℓ/w | 0.01 | 0.05 | 0.075 | 0.1 | 0.5 | 1.0 | 5.0 |
|---|---|---|---|---|---|---|---|
| w = 10 µm | 0.00% | 0.00% | 0.00% | 0.00% | 0.00% | 0.00% | −0.00% |
| w = 40 µm | 0.01% | 0.01% | 0.01% | 0.01% | 0.01% | 0.01% | −0.01% |
| w = 160 µm | −0.00% | 0.02% | 0.02% | 0.02% | 0.01% | 0.01% | −0.08% |

**Worst 0.081% over ℓ/w ∈ [0.01, 5] and a 16× range of w** — the same span and the same shape as
T3_1's own curve for an interior via, for the basis that did not exist until now.

### THE FINDING: M1 measured the chain's premise and it is FALSE, so the chain is NOT built

The brief's D2 makes the attachment basis "the bottom member of a chain whose other members are
ordinary z-rooftops", and §0.2 item 3 argues the chain is mandatory: `MaxElectricalLength` is k·ℓ ≤
0.05 and a 100 µm GaAs backside via at 30 GHz is **k·ℓ = 0.23**, 4.5× over, with no intermediate
levels to split across. **M1 (`ViaPhysicsTests.M1_2`, R-gv-1) measured it, and the premise does not
survive.**

**(3b) — an ATTACHED via, the only kind that exists in a real structure** (and the kind a backside
via is at *both* ends — its lower terminal is a perfect conductor, an even stronger termination than
a finite plate). Subdivide the same via into n segments and compare the reaction vᵀZ⁻¹v:

| k·ℓ | f | n=2 vs n=1 | n=4 vs n=1 | **n=8 vs n=1** | worst \|i_k/mean − 1\| at n=8 |
|---|---|---|---|---|---|
| 0.01 | 1.33 GHz | 0.020% | 0.050% | **0.062%** | 1.54% |
| 0.23 | 30.6 GHz | 0.031% | 0.065% | **0.077%** | 1.53% |
| 0.50 | 66.4 GHz | 0.100% | 0.147% | **0.172%** | 1.61% |
| 1.00 | 133 GHz | 0.089% | 0.126% | **0.141%** | 2.04% |

1.0 is as far as `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` permits on that fixture (ρ/λ = 0.095),
and the test asserts that rather than measuring outside the kernel's own validated range.

**(3a) — a FLOATING rod does move, by 10.2% at n = 8 with the current 28.5% non-uniform — and that
movement is 98% STATIC.** It is identical (10.181% → 10.345%) from k·ℓ = 0.01 to 0.23, a 23× span;
an electrical effect would scale like (kℓ)², i.e. 529×. It is the floating end condition — a
free-floating conductor's current genuinely vanishes at both ends and a uniform basis can never
represent that, however short the rod. **A via in a circuit is terminated at both ends and has no
such freedom.** Reading (3a) as "the chain is needed" is exactly L9e's own mistake one layer up:
blaming an electrical bound for a quantity that has no frequency in it.

**(1) — and the chain is not cheap.** Real fills on L9's own two-level fixture, counting
`PlanarKernelSet.FitCount`:

| n | N | vertical | fits/frequency | added | seconds @105 ms | % of a 149.9 s point |
|---|---|---|---|---|---|---|
| 1 | 514 | 2 | 13 | 0 | 0.00 | 0.00% |
| 2 | 516 | 4 | 27 | 14 | 1.47 | 0.98% |
| 4 | 520 | 8 | 70 | 57 | 5.98 | 3.99% |
| 8 | 528 | 16 | **216** | 203 | 21.31 | **14.22%** |

Growth of the added fits is **4.07× then 3.56× per doubling** — quadratic, not linear. Equal-length
segments share NO pairing (a fit is keyed on ABSOLUTE heights, and segment k's nodes sit at
z₀ + kℓ/n + offsets), and the ẑẑ arithmetic alone under-predicts it: the SCALAR block adds its own
(n+1)(n+2)/2 level pairings, which only a real fill reveals.

**So: 14% of a solve to move the answer 0.08%. The chain is not built, and `MaxElectricalLength` is
0.05 → 0.30 instead**, set past every measured point where the uniform-current basis is worth under
0.1%. `T3_3` and `L9PhaseGateTests.Gate3Wiring_TheAspectRatioRefusalIsGone_…` are **updated, not
loosened** — the refusal's own wording now names the measurement rather than prescribing a remedy
that is not worth it, and a 60 µm gap at 30 GHz (k·ℓ = 0.14) is now ACCEPTED where it used to refuse.
**Widening it unlocks nothing on its own** and the refusal keeps saying so:
`Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` already restricts every via-bearing run to electrically
small structures, and it is byte-identical.

### The two structural invariants it breaks, both re-gated rather than exempted (§0.2 item 4)

**(a) L9c's D5 equality does not survive.** An attachment has ONE divergence pulse, so
`∫∇·f dS = ∓1` — balanced by its IMAGE below the plane, not by a second pulse on the metal. Adding a
compensating pulse "to restore D5" would **double-count the image the Green's function already
carries**. `PlanarBasisFunctions.NetCharge` is the one place the quantity is stated; `T4_2` asserts
−1 exactly (not to a tolerance, for D5's own reason) and `M6_3` asserts 0 exactly for every other
basis. The ρ → ∞ decay is measured, not asserted: **ρ^−3.00** over a decade, against a monopole's
ρ^−1 — an absent or wrongly-signed image would read ρ^−1.

**(b) L8c's `s_A + s_B = 0` fails, so the extracted CONSTANT stops cancelling** — in exactly one row,
and nothing in the fill notices. **It does not bite, and that is measured rather than reasoned**
(`T4_3`): the split between the closed-form constant and the numerically-integrated remainder is
arbitrary, so the assembled entry must be invariant under `PlanarExtractionOrder`. Inverse vs
Constant is **1.09e-15**, Inverse vs Linear **3.28e-9** — on a row where `s_A + s_B = −1`, so the
agreement is a real statement rather than a vacuous one.

### The sign convention is NOT the brief's, and the reason is reciprocity

D4 calls the net charge "+1". This file instead keeps **every** vertical basis's current flowing +z
(A → B, upward), attachment included — so the ẑẑ block needs no per-basis direction factor and
reciprocity stays structural with an attachment and an interior via in the SAME mesh, which is
exactly the MMIC starter (a backside via and a Metal1↔Metal2 post). With that orientation the single
pulse sits at the upper (meshed) foot and carries −1. `M6_2` is the gate, and it asserts the
attachment↔interior-via ẑẑ cross block is **non-zero** (5.79e-1 Ω) so the symmetry cannot pass for
the wrong reason — that cross block is precisely what a direction slip would invert while leaving
`Z` symmetric and wrong.

**The mechanism that made this cheap:** `Halves` returns the ONE meshed cell **twice**, with the
grounded half carrying `Sign = 0`. The fill's own four-term signed sum then drops the ground terms
with no index guard and no special case anywhere downstream.

### R-gv-5 — the mesher's two silent failures do NOT transfer for free

They were fixed for the two-meshed-feet path at L9c and the new path does not inherit their tests.
`ViaBasisTests.M6_1` asserts both **for the ground-via path specifically**: the footprint's own four
edges are hard gridlines (without them the via vanishes with no error — L9c measured zero vertical
unknowns for a 40 µm footprint between cell centres), and adding it grows N by **1.08×**, not the
5.8× an edge-graded fan would cost. Both hold because `CollectBoundaryLines` walks `problem.ViaList`
without caring which terminal a via names — by construction, and now asserted.

### R-gv-6 — the extractor builds it only for the ground the kernel actually models

`PlanarExtractor.BuildVias` used to drop a via whose span named a non-analysis conductor
(`unknownLevels`). It now produces a ground attachment when the named conductor **is the ground
reference R-em-4 resolves**, and still drops anything else **by name**: a different ground pour is a
FINITE conductor this kernel does not mesh, and treating it as the infinite plane would solve a
structure nobody drew. `L9PhaseGateTests` gates both halves through the product path — a drawn
backside via on the MMIC starter now extracts, meshes and produces **4 ground-attachment vertical
unknowns** (N = 943), where before it was dropped with a note.

### M4's Tier 5 — BUILT, and it found a production bug before it passed anything

`L9PhaseGateTests.Gate4_AGroundViaAtAStubsEnd_TurnsItsQuarterWaveNOTCH_IntoTransmission`,
`Category=Benchmark`, **6.6 min**. A quarter-wave stub inverts whatever terminates it, so one drawn
`ViaShape` decides whether the through path notches or is transparent — a with/without COMPARISON,
for the same reason L9's own gate 1 is one (the two-level de-embedding residual is the same order as
a short structure's own reflection, so an absolute tolerance there would be a tolerance on the
residual). 144 µm line on a 50 µm GaAs slab, ε_eff(A) 9.3602, stub 408.3 µm = λ_g/4 at 60 GHz:

| f (GHz) | \|S₂₁\| OPEN stub | \|S₂₁\| SHORTED stub | ratio |
|---|---|---|---|
| 48.00 | 0.5137 | 0.9682 | 1.88 |
| 52.50 | 0.4019 | 0.9841 | 2.45 |
| **57.00** | **0.007319** | **0.9959** | **136** |
| 61.50 | 0.5145 | 0.9866 | 1.92 |
| 66.00 | 0.6863 | 0.9509 | 1.39 |

**136× at the open stub's own notch** (interpolated 56.72 GHz, −5.5% of the bare λ_g/4 prediction —
the open-end extension, in the direction and roughly the size L8's own gate 1 measured). N = 892
open / 1,006 shorted, of which **63 are the ground attachment's** vertical unknowns.

**THE BUG, and it is exactly what a phase gate is for.** `PlanarKernel.CanSolve`'s
`EveryViaLiesInOneMediumRegion` called `problem.LevelZ(via.LowerLayerIndex)` — and a ground
attachment's lower index is `PlanarVia.GroundTerminal = -1`, so it indexed `Layers[-1]` and threw a
raw `ArgumentOutOfRangeException` instead of returning a verdict. **Every engine-side test of the
attachment basis is on hand-built problems that never reach `CanSolve`**, which is why nothing caught
it; the product path reaches it on every run. Fixed by taking the stack's own bottom
(`InterfaceZ[0]`) for an attachment — the question the check asks is still worth asking of one, since
an attachment crossing a dielectric interface has the same two-sets-of-coefficients problem — and the
refusal now names "the ground plane" rather than "level -1".

**One fixture decision worth recording: the slab is 50 µm rather than the starter's 100.** A ground
attachment's length IS the slab height, and at the band's top a 100 µm one measures k·ℓ = 0.50
against `MaxElectricalLength`'s 0.30. **The refusal fired, by name, and the FIXTURE moved rather than
the constant** — which is the second thing this gate demonstrates.

### What is NOT built, and what a continuation starts from
- **R-gv-8's ω → 0 capacitance gate against `PlanarStaticLimitTests`.** `T4_3` covers the mechanism
  the trap actually runs through (the extracted constant) at full frequency; the static-limit rung is
  the independent cross-check and is not run.
- **The z-segment chain (M2).** Deliberately, on the measurement above. If a case ever appears where
  it matters — an attached via past k·ℓ ≈ 1 that `ValidatedRhoOverLambdaAtHeights` still admits —
  M1_2's own `ChainVias` helper (in `ViaPhysicsTests`) is the construction, and its cost is measured.
- **ALL OF PART B** — the interior electrostatic Green's function, `C_pul` at a buried level, and
  L9c's still-un-run Tier 4. Nothing here touches it, and `PlanarSolve`'s buried-level refusal is
  byte-identical. It is now the most valuable remaining work in this area, and this slice hit its
  edge directly: `T4_2`'s decay measurement had to be taken at a TOP-SURFACE height because
  `LayeredStaticGreens` refuses an interior source by name — the very refusal Part B exists to lift.


## G_A^zz's accuracy ceiling — M0: the refusal was asking the wrong question (follow-up to L9)

**A FOLLOW-UP to L9**, a sibling of the via z-integral and the ground-via briefs and independent of
both. Brief: `docs/sonnet-briefs/brief-gazz-accuracy-ceiling.md`. **M0 only** — M1 (the DCIM knob
sweep), M2 (direct integration instead of a fit) and M3 (a depth search) are measured deferrals, and
the brief's own §7 names M0 as the natural fault line and possibly the only milestone needed.

```
PlanarSolve.cs   + VerticalExtent / VerticalRangeVerdict (public — a pre-flight verdict, like
                   PlanarKernel.CanSolve); Run calls it rather than repeating it
Dcim.cs          + ValidatedRhoOverLambdaInteriorHorizontal = 1.0 (a MEASURED RANGE, not a refusal)
PlanarFill.cs    + PlanarFillDiagnostics — optional, defaulted null, Tier 1's own instrument
```

**`Dcim.ValidatedRhoOverLambdaAtHeights` did NOT move.** L9c measured the 14× that justifies it and
nothing here re-measured it. What moved is *which ρ it is asked about*.

### THE FINDING: the comment was right and the code was not

`PlanarSolve`'s own comment already said the limit *"binds ONLY the ẑẑ block"* — and two lines later
it asked `Diagonal(mesh)`. Those are different quantities, and on board-scale geometry they differ by
more than the limit itself. `G_A^zz` has exactly **two** consumers anywhere (`PlanarFill`'s
`zi && zj` arm and the `SingularPrismPart` it calls), both between two VERTICAL bases, so the widest
ρ it is ever evaluated at is the extent of the **via footprints**, not of the board.

**§10.7's own FR-4 hero (2.9 × 20 mm on 1.6 mm FR-4), two levels, 10 GHz, λ₀ = 30 mm:**

| layout | N | mesh diagonal | OLD ρ/λ | via extent | **NEW ρ/λ** | verdict |
|---|---|---|---|---|---|---|
| one via, mid-board | 1,105 | 20.21 mm | 0.674 | 0.707 mm | **0.024** | REFUSED → **PASS** |
| two vias, 1 mm apart | 1,174 | 20.21 mm | 0.674 | 1.581 mm | **0.053** | REFUSED → **PASS** |
| two vias, 18 mm apart | 1,174 | 20.21 mm | 0.674 | 18.507 mm | **0.617** | REFUSED → REFUSED |

**§10.7's FR-4 hero with a via runs at 10 GHz.** The last row is not a leftover — there the fit
genuinely IS asked about 0.617 λ, which is the regime L9c measured at 14×. **Narrowing the question
is not widening the answer**, and the refusal's wording carries that distinction because the two
cases need different instructions: it names the separation as being **between vias**, quotes the mesh
diagonal alongside it, and says outright that shrinking the surrounding metal does not act on it.

**Tier 1 is INSTRUMENTED, not read off the code**, because that difference is the entire soundness
argument. `PlanarFillDiagnostics` records the widest separation the ẑẑ arm actually reaches; on L9d's
own fixture it is **56.57 µm against the 56.57 µm the refusal checked — equal, not merely bounded**
(the arm computes every pair, so the two extreme via cells always are one), on a mesh 412 µm across.

### D2 — narrowing EXPOSED that three components were governed by nothing, and that is a finding

Scoping `G_A^zz` to the via footprints leaves the interior pairings of `G_A^xx`, `G_q` and the
**mixed** component checked by nothing at all — and the mixed block couples a via to *every*
horizontal basis, so its ρ genuinely does span the mesh. They do not need a refusal, and **the number
is what says so** rather than an assumption: L9c's Tier 5 measured all three at **≤ 1.9e-2** of the
free-space kernel out to ρ/λ = 1 on every grounded stack, which is L9b's own envelope for the
top-half-space pairing.

`Dcim.ValidatedRhoOverLambdaInteriorHorizontal = 1.0` records that, and `PlanarSolve` states it on
every general-kernel run — *inside* it below, and **"PAST … nothing above that separation has been
measured"** above. **A note, not a refusal, for exactly R-prt-13's reason**: reporting "unmeasured" is
honest, and refusing on it would be inventing a limit rather than reporting one. It would also have
refused structures accepted today, which D6 forbids.

### What did NOT move (D6 / R-zz-5)

**Nothing accepted today can become refused**, and it is a property of the construction rather than a
tolerance: the vertical extent is a subset of the mesh, so narrowing can only ever accept MORE. The
fill's arithmetic is untouched — `M0_4` proves the matrix is **bit-identical with and without the
Tier 1 instrument attached**, which is the claim worth making, since an instrument that perturbed
what it measures would be worse than none.

### M1–M3, deferred with the reason rather than attempted

The residual is the last row of the table: two vias genuinely far apart on a board still refuse, and
closing that needs the fit itself to improve. The brief's own ordering stands and none of it is
started:

- **M1 — the DCIM knob sweep.** `BranchSamples` is 0 and `BranchPointOrders` is 1 because of trades
  measured on the **one-layer** fit and settled **between components** by sharing one `DcimSettings`.
  The failing component is `G_A^zz` alone and `G_q` at heights is fine, so that trade is not the one
  this case faces. Per-component settings are a lookup on `GreensKernel` in `PlanarKernelSet`, and
  R-dcm-1 requires the one-layer path never to see it.
- **M2 — direct integration.** `SommerfeldIntegral.EvaluateInterior` is accurate everywhere out
  there and costs 40–50 ms a point; the ẑẑ block is a function of ρ alone at a fixed pairing and
  already consumes a radial table, and L9c measured a 100× coarsening at 9.6e-10. The measurement
  nobody has taken is the table's *required* sample count — by refining until the block stops moving,
  rather than inheriting the DCIM table's mesh-derived spacing.
- **M3 — a depth search.** Still declined, and still should be, until M1 and M2 are measured.

**Do NOT reach for an amplitude-conditioning cap.** L9c measured it worse (14 → 39 on GaAs low–low at
a cap of 1e4; every candidate rejected at 1e2). It is a correct diagnosis and a bad selector.

## G_A^zz's accuracy ceiling — M1 (a negative result), M2 (the direct path), M4

Continues the M0 section above. Brief: `docs/sonnet-briefs/brief-gazz-accuracy-ceiling.md`.
**M0 + M1 + M2 + M4 are done; M3 (a depth search) is still not started and is still not earned.**

### M1 — THE KNOBS DO NOT CLOSE IT, and three of the five cannot even reach it

§3's M1 names five `DcimSettings` knob groups. Swept on `G_A^zz` alone at interior heights, over every
grounded stack in `LayerStacks.All()` and both interior pairings, against
`SommerfeldIntegral.EvaluateInterior` (`ViaBasisTests.M1_1`, `Category=Benchmark`, ~3 min):

**`BranchPointOrders`, `BranchSamples`, `BranchExtent` and `FitTolerance` are INERT**, and this is
structural rather than a tuning plateau: `Dcim.FitAtHeights` re-references the whole decomposition to
the source region's own k_m and **never reads any of them** — L9c established that the far-field sum
rule is a theorem for an interior source *by inspection* (the kernel is finite at its own branch
point), so there is no branch-point Taylor sampling to configure. 30 configurations, bit-identical.
The test asserts that as an equality rather than arguing it.

**The reachable knobs give 10.4× at best and it is not a free win**, worst over every grounded case:

| | ≤ 0.1 λ | ≤ 1 λ |
|---|---|---|
| shipped defaults | 2.82e-3 | **1.40e+1** |
| best found (`FarOrder=10`, `PathExtent=1200`) | **6.46e-2** | **1.35** |
| the target the other three components meet | — | **1.9e-2** |

Still **71× outside** the envelope, and **23× WORSE inside ρ/λ ≤ 0.1**, which is where the kernel is
actually used today. `FarOrder` 6 → 8 is where most of the gain is (14 → 3.8); everything past 10 is
flat or worse. **There is no per-component setting that is simply better** — it is a trade and it goes
the wrong way, so R-zz-2's per-component plumbing was NOT built.

### M2 — the direct path, and the measurement that matters more than it does

`PlanarFillSettings.DirectVerticalKernel` (default **false**) makes the ẑẑ block take its kernel from
`SommerfeldIntegral.EvaluateInterior` instead of from the fit — reachable exactly like
`UseRadialTable = false`, via `PlanarKernelSet.GetDirectMinusStaticAsymptotes` and
`ViaZIntegral.AveragedTermsDirect`. Everything else is untouched: the singular half is closed form in
z and was never fitted, and the scalar, horizontal-vector and mixed blocks do not change.

**TABULATE THE REMAINDER, NOT THE KERNEL.** The first version tabulated `_full`, which still diverges
as 1/ρ once the static asymptotes are removed (the direct term's own 1/ρ and the poles' ln ρ are still
in it) — a linear table cannot carry either, and it would be worst exactly at the self and touching
cell pairs, where most of the block's value is. Subtracting `Extracted` first is what makes the
tabulated function bounded, and handing back `table + Extracted` as `_full` makes `Remainder()` return
the tabulated function exactly, so the fill sees the shape it always does. **Caught before any number
below was believed**, which is the standing rule here.

**The direct-table cache lives on `FitCache` and is shared across every `For()` view**, for exactly the
reason L9d shared the fits — a de-embedded solve builds one view per mesh — and it matters more here,
because a miss is seconds of Sommerfeld integration rather than ~90 ms of fit.

**The measurements** (`MultiLevelPortTests.ZzM2_1`, `Category=Benchmark`), on §10.7's own FR-4 hero with
two vias 18 mm apart — **the one layout M0 left refused**, N = 1,174, ρ/λ = 0.617 against the 0.1 limit:

| | |
|---|---|
| **(0) oracle check** — direct vs fitted at ρ/λ = 0.053, INSIDE the limit where L9c measured the fit at ≤ 2.8e-3 | **8.67e-5** |
| **(a) the block's own convergence** in the table's sample count (32/64/128/256 vs the next finer) | 2.18e-3 / 7.45e-4 / **8.34e-5** / 2.89e-6 |
| **(b) the FITTED block against the CONVERGED direct one** (which IS the oracle) | **4.53e-7** |
| **(c) cost**, per via span per frequency, at 32 / 128 / 512 samples | 21.5 / 28.4 / 64.7 s = **14% / 19% / 43%** of a 149.9 s de-embedded point |

**(b) must be taken against the CONVERGED direct block, and getting that wrong overstates the fit's
error by two decades.** Comparing it against a fresh 128-sample table reads 8.67e-5 — which is (a)'s
own 128-vs-256 table resolution, not anything the fit did. The test reuses (a)'s finest block for
exactly that reason.

**(b) IS THE FINDING, and it is not the one the brief expected.** L9c's Tier 5 measured the interior
fit **POINTWISE** — |ΔG| at one ρ. The refusal is asked of a **MESH**. Nobody had asked the second
question, and on this layout the answer is that essentially none of the pointwise error survives into
the assembled block: **4.53e-7, five decades inside the 1.9e-2 envelope.** So on §10.7's own board the
refusal is refusing a structure that would have solved correctly.

**Do NOT widen `ValidatedRhoOverLambdaAtHeights` on that.** It is ONE layout on ONE stack, and the
stack matters enormously — L9c's own table has FR-4 low–low at 1.1e-1 at ρ/λ = 1 against GaAs
low–low at **14**.

**The GaAs cross-check is NOT RUN, and that is now a MEASUREMENT rather than "it did not finish".**
The smallest fixture that reaches ρ/λ ≈ 0.6 at all — two 200 µm pads 18 mm apart on 100 µm GaAs,
each pad its own via footprint, on the `CoarseForZz` mesh — costs:

| | |
|---|---|
| N = 738, 324 cells, **162 vertical bases**, ρ/λ = 0.607 | |
| cores | 10.5 s |
| **FITTED fill** | **236.8 s** |
| **DIRECT fill, 32 samples** — the COARSEST rung of the ladder | **828.3 s** |

A comparison needs the fitted fill plus at least two direct rungs (an oracle not shown to have
stopped moving is not an oracle), so the honest cost is **45+ minutes** against this project's whole
~40-minute opt-in tier.

**What drives it is NOT what the first attempt assumed — not the unknown count and not the physical
size.** The same fixture at 100 GHz and 1.8 mm (a tenth of the extent, the same ρ/λ) meshes to the
IDENTICAL N = 738 with the identical 162 vertical bases, because the mesh is scale-free: the pitch
comes from the narrowest run and the via footprint is one. It is the **vertical basis count** — the
ẑẑ block is 162² entries and the mixed block integrates a derivative against every horizontal basis,
against L9d's own measured fixture which had TWO. A via footprint cannot be shrunk relative to its
pad without leaving a sliver run beside it, which drives the pitch down and the count straight back
up. **So the affordable version needs a mesher whose pitch is not tied to the via footprint, not a
smaller number here** — and the FR-4 conclusion stands as being about FR-4.

### M4 — the constant does NOT move; the way past it is a different KERNEL

`Dcim.ValidatedRhoOverLambdaAtHeights` is byte-identical. M1 is why: no setting improves the fit, so
widening the constant would be widening a claim nothing supports. Instead
`PlanarSolve.VerticalRangeVerdict` takes the fill settings, and with `DirectVerticalKernel` on it
**skips the refusal and says so in a note** — the limit is a property of the FIT and does not apply to
the integrator it was measured against. The refusal's own text now names the setting as the way past
it, alongside the two geometric instructions M0 added.

**Not an early return** — D2's horizontal-components note below it is unconditional, and dropping it
would re-open exactly the "narrowing left something ungoverned" hole M0 closed.

### The setting is REACHABLE, and two robustness fixes came with making it so

`PlanarFillSettings.DirectVerticalKernel` is wired through the `.cem` to the EM panel — see
`src/Ui/Layout/Em/CLAUDE.md`. **Wiring it found that `EmRunService` passed `null` for the whole of
`PlanarSolveSettings`**, so nothing in it — including L9e's adaptive frequency sampling — had ever
been reachable from the product. That is now fixed and adaptive sampling is the default.

Two fixes in this file's own code, both for hazards the direct path introduced:

- **A per-key build gate on the direct tables.** `GetDirectMinusStaticAsymptotes` is seconds of
  Sommerfeld integration, not the ~90 ms a fit costs, so two threads racing the same key would
  duplicate it outright. Double-checked locking on a per-key gate object, with the shared cache read
  under `_fits.Gate` on both sides of it.
- **`PlanarFillSettings.Validate()`, called from `BuildCores`.** The settings that matter are the
  ones whose bad case is a silently WRONG answer rather than an exception: `ViaZNodes = 0` produces
  an empty quadrature-node set, which makes the ẑẑ block **zero** — vias stop conducting and the
  s-parameters are complete, smooth and wrong. Every node/panel count is refused by name; the
  defaults are of course accepted. It is a construction-time refusal rather than a per-entry check,
  so it costs nothing in the fill.

### Gate, and one latent hazard checked rather than assumed

**`tests/Engine.Tests` 1,002 passed + 1 pre-existing skip — the routine tier is UNCHANGED in size**,
because both new methods are `Category=Benchmark`. `tests/Ui.Tests` **4,741**, `tests/Firewall.Tests`
**4/4**. Nothing outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/` was touched, and
`Dcim.ValidatedRhoOverLambdaAtHeights` and its refusal string are byte-identical.

`DirectVerticalKernel` / `VerticalTableSamples` were inserted into the MIDDLE of
`PlanarFillSettings`' positional parameter list, beside the other via settings where a reader looks for
them. That is safe here and was checked rather than assumed: there is **no `new PlanarFillSettings(`
anywhere in the repository** — every site is `PlanarFillSettings.Default` plus a name-based
`with { }` — and a future positional caller would bind an `int` to a `bool` and fail to compile rather
than silently shift.


## Edge mesh on CURVED geometry — a NEGATIVE RESULT (follow-up to L8b)

**A FOLLOW-UP to L8b**, and it touches no Green's function, no fill and no solve. Brief:
`docs/sonnet-briefs/brief-edge-mesh-on-curved-geometry.md`. **M0 and M1 are done; M2 was BUILT ONLY
AS A MEASUREMENT SEAM and is NOT the default, because M1 says it does not pay.**

```
SurfaceMesher.cs   + PlanarRimGrading (None / PerRun / PerRunSampled), the oblique-RUN walker,
                     Mesh's trailing `rimGrading` parameter, EdgeAttractors (D9's own quantity),
                     and §5's "no edge grading was actually applied" note
```

Gate: **+4 routine tests in `tests/Engine.Tests/Mom/PlanarRimGradingTests.cs` (~0.2 s)**, plus **2
tagged `Category=Benchmark`** (E3 3 m 8 s, E3a 1 m 44 s — the two convergence ladders). M0's own table
is in `tests/Ui.Tests/Em/PlanarMeshPCellTests.cs`, because the shipping PCells are in `src/Ui`.

### §0's finding reproduces exactly, and M0 says it is NARROWER than "smooth parts get nothing"

`CollectBoundaryLines` classifies every ring edge as vertical, horizontal or oblique. An oblique edge
contributes **neither** a hard gridline nor an edge attractor — D9's guarantee, by exclusion — so a
96-point disc's mesh is the plain λ_g marcher and nothing else: **N = 248 at `EdgeCells` 0, 3, 10 and
20 alike**, min cell flat at 223 µm, reproduced as `E1`.

**M0's table on the shipping parts is the part that needed care, because TOTAL N IS THE WRONG
STATISTIC AND SO IS THE MINIMUM CELL.** Every one of MBend / MTaper / MKlopf responds to `EdgeCells`
in N, on both starters, and every one of them shows a min cell collapsing from ~176 µm to ~21 µm —
which reads as "the rim responded" and is false. The fans come from the **axis-parallel end caps**,
whose attractors refine whole grid columns; a taper's rim passes within a bulk cell of its own caps,
so a minimum taken over the whole rim reports the cap's fan. The quantity that is actually about this
brief is the **transverse grid spacing at the rim point FARTHEST from any axis-parallel edge**:

| part (PCB 2-Layer, 10 GHz) | N at EdgeCells 0 → 20 | MID-RIM transverse spacing at 0 → 20 |
|---|---|---|
| MLIN straight (control) | 247 → 1,286 | — (no oblique edge) |
| MBend mitred | 196 → 1,736 | 580 → 694 → 245 → 213 µm |
| MTaper 2.9 → 1.0 mm | 657 → 851 | **263.64 µm, FLAT** |
| MKlopf on-axis | 605 → 1,115 | **176.36 µm, FLAT** |
| MKlopf Offset | 497 → 986 | **176.21 µm, FLAT** |

and the MMIC starter is the same shape (MTaper flat at 6.59 µm, MKlopf flat at 1.55 µm). **MBend is
the exception and not a counter-example**: its single oblique edge is the mitre, 1.2 mm from two long
axis-parallel edges whose fans reach it — and even there the EdgeCells = 3 value (694 µm) is *worse*
than EdgeCells = 0 (580 µm), which is the fan moving gridlines about rather than resolving anything.

### §0.1 item 2 — the non-monotone 45° bend was an UNREPRESENTATIVE FIXTURE, measured

A hand-built L-shape with a 45° chamfer measured N = 23,891 / 11,438 / 20,146 at EdgeCells 3 / 10 / 20
— non-monotone and 4.8× over R17's ceiling. Asked of the **real** `MBendPCell` on the same technology
(`E0b`), at 45°, 90° and 135°, N is monotone at every angle and inside the ceiling:

| MBend angle | EdgeCells 0 / 3 / 10 / 20 |
|---|---|
| 45° | 1,190 / 1,799 / 2,840 / 3,616 |
| 90° | 196 / 550 / 1,484 / 1,736 |
| 135° | 2,363 / 2,856 / 3,879 / 4,176 (Warn) |

**It is asserted rather than reported**, because if a shipping bend ever does go non-monotone that is
the growth-ratio knife edge L8b's own notes describe and it outranks this brief.

### THE FINDING: a graded fan on a STAIRCASED rim buys nothing, and the reason is measurable

§2 is the question that decides the work, and it has to be answered with a converged physical
quantity. The quantity is L8c's own Tier 5 harness — the static capacitance from
`PlanarFill.ScalarPotentialMatrix` at ω → 0, at εᵣ = 1 so the kernel is closed form and only the mesh
can be wrong — on a 96-point disc, r = 1.45 mm, over the FR-4 starter's ground plane.

**The CONTROL first, and it is not optional** (`E3a`). On a Manhattan square, where the graded fan
lands on a rim that is exactly where the metal is, the same harness and the same quantity:

| | N = 40/144 | 180/264 | 760/840 | 1,624 | 3,280 |
|---|---|---|---|---|---|
| uniform (edge mesh off) | **4.437%** | 2.224% | 0.970% | 0.556% | 0.279% |
| edge-graded (EdgeCells 3) | **0.431%** | 0.501% | 0.455% | 0.453% | 0.279% |

**At the shipping mesh grading is 10× better for 3.6× the unknowns, and the uniform ladder needs
~20× the unknowns to catch it.** So the harness sees edge grading perfectly well. (The graded
ladder's flatness at ~0.45% is R-fil-12's own already-recorded mechanism: the conductor-width edge
cell does not shrink with cells/λ, so its sequence is flat because it is at its own limit.)

**Now the disc** (`E3`), three ladders, each refined along cells/λ:

| ladder | N = ~250 | ~330 | ~670 | ~1,300 | best |
|---|---|---|---|---|---|
| shipped (no rim attractors) | 1.494% | 0.335% | 0.265% | 0.413% | **0.265%** |
| `PerRun` | 0.331% | 0.447% | 0.588% | 0.478% | 0.331% |
| `PerRunSampled` | 0.745% | 0.511% | 0.768% | 0.501% | 0.501% |

**Rim attractors are not better; `PerRunSampled` is measurably worse.** And the reason is in the
reference ladder itself: a uniformly refined disc does **not** converge monotonically — 137.48 /
138.31 / 138.51 / 137.59 / 137.94 fF at N = 324 … 3,972, a **non-monotone band of 0.669%** which is
*wider than every difference being compared*. The staircase's own area error, reported beside each
rung, wanders over exactly the same range (0.16% … 1.13%) and in step with it.

**That is the mechanism, and it is L8b's own already-recorded one seen on a smooth outline instead of
a mitre:** the quantisation error depends on how the grid happens to *align* with an oblique edge and
not only on how fine it is. Refining toward a *tread's* edge resolves the artifact, not the physics,
and the artifact is an order of magnitude larger than anything the fan changes. **§2's instruction is
"if it does not, stop and report that" — so `PlanarRimGrading.None` is the default, and the negative
result is ASSERTED in `E3`** (rim grading is required NOT to beat the shipped mesh by 2×) so that a
later change which made it genuinely pay turns the test red instead of quietly contradicting this
note. **What curved geometry needs is conformal / cut boundary cells (§4), which is its own phase with
its own brief** — a cut cell breaks the rooftop pairing L8c's fill and L9c's via basis both assume.

**Three already on the record for the same shape of answer:** L7b-b's Route B, L9c's amplitude cap,
L9e's ACA.

### M2 — built as a SEAM, and D9 is preserved NUMERICALLY rather than by exclusion

`PlanarRimGrading` is a measurement seam of exactly `PlanarEdgeReference`'s kind — **not a fourth user
control** (D3 permits three). An oblique **run** is a maximal chain of consecutive oblique edges, so a
96-point disc is ONE run and a taper is one per flank; attractors come from the run, at each end of its
own coordinate range in each axis, filtered by the SAME "long enough to crowd" test the axis-parallel
path uses (a fifth of the polygon's own extent), asked of the run. `PerRunSampled` adds three interior
samples spread along the run's **arc length**, each contributing an attractor transverse to the local
tangent — spreading by *coordinate* is wrong for a closed curve, since the midpoint of a disc's y-range
is the disc's centre and not on the rim at all.

**D9 is asserted on the ATTRACTOR COUNT and not on N** (`E1c`, via the new public
`SurfaceMesher.EdgeAttractors`), because N is a consequence of the marcher, the growth ratio and the
λ_g cap all at once. A 24-, 96- and 384-point disc each contribute **0 shipped, 4 `PerRun`, 7
`PerRunSampled`** — unchanged as the tessellation is quadrupled, which is the property D9 is about.

**A Manhattan mesh is BIT-IDENTICAL with the seam on** (`E2`): gridlines, cells and bases compared as
equalities, at EdgeCells 0/3/10 and both modes, and §10.7's FR-4 hero is still exactly **N = 552**. It
cannot move — a Manhattan polygon has no oblique edge and therefore no run — and that is asserted
rather than argued.

**One fixture trap, worth the sentence.** A disc built with its vertices offset by half a step —
the obvious way to keep vertices off the axes — makes the four edges that STRADDLE each axis exactly
axis-parallel to the mesher's own 1e-12 tolerance. The ring then splits into four runs and the
fixture hands itself the very gridlines it exists not to have: **16 attractors instead of 4**. Put the
vertices ON the axes; a vertex is geometry to be covered, never a gridline.

### §5 — a control that silently does nothing now says so

`SurfaceMesher`'s note read *"Edge mesh on: N graded cell(s) at every axis-parallel conductor edge…"*.
That is accurate and nobody reads the qualifier, so on an all-curved part it reported an edge mesh
that exists nowhere on the artwork. When an axis collects no attractor the report now adds *"…but NO
edge grading was actually applied…"*, naming the axis and saying that raising Edge cells will not
change the mesh. Same class as the `EffectiveEdgeCells` clamp note beside it. `E1b` asserts it fires
on the disc and does **not** fire on the Manhattan hero.

### Out of scope here, on purpose

- **Conformal / cut boundary cells, and RWG triangles.** They address *staircasing*, which the
  measurement above says is the binding term — and they are a much larger phase (a cut cell breaks
  L8c's rooftop pairing and L9c's via basis; RWG replaces the basis outright).
- **Any change to the fill, the kernel, ports, de-embedding or the solve.** None was made.
- **`EdgeFractionOfReference` (0.03), `EdgeGrowthRatio` (1.7) and `EdgeReferenceLength`'s
  conductor-width choice.** R-fil-12 closed that at 0.18% from the consensus limit; untouched.
- **A fourth user control.** `PlanarMeshSettings` is unchanged.
