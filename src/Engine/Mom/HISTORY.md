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

## Conformal (cut) boundary cells — the phase the negative result above scheduled

**A FOLLOW-UP to L8b/L8c. It is NOT a slice of L9 and it touches no Green's function.** Brief:
`docs/sonnet-briefs/brief-conformal-boundary-cells.md`. The section immediately above ends with
"conformal / cut boundary cells" listed as out of scope and names staircasing as the binding term;
this is that phase. **It ships OFF (`PlanarBoundaryCells.Staircase`), see "The default" below.**

```
PlanarCellRegion.cs   NEW — the cut cell as geometry: a list of CONVEX pieces, not a half-plane
PolygonIntegrals.cs   NEW — the six closed forms over a convex piece (L8c's, generalised off the rectangle)
RooftopSupport.cs     NEW — the ramp measured from the METAL's own boundary, as weighted strips
SurfaceMesher.cs      + BuildConformalCells, the sliver merge, the three per-cell refusals, the notes
PlanarFill.cs         + PairCoresConformal / InnerCores / WeightMoment — the cut-cell fill path
PlanarMeshSettings.cs + PlanarBoundaryCells, the fourth user control (D3 reopened — see §5 below)
PlanarPort.cs         + the refusal for a port that lands on a cut cell
PlanarCurrentDensity  the transverse extent is Area/Width, not Height, once a cell is cut
```

Tests added closing out the phase: `ConformalDiscConvergenceTests` (gate 6, Benchmark, 2 m 35 s),
`ConformalSliverTests` (gate 5 — one Benchmark `[Theory]` at two densities, one Benchmark density
scan, one routine area check), `ConformalDownstreamTests` (§4, 4 routine tests, 20 ms), and Ui-side
`ConformalBoundaryCellsUiTests` (§5's eight UI gates plus gate 1's shipping-PCell half and its two
follow-ups, 11 routine tests, 0.8 s). **Every new ladder is `Category=Benchmark`** — §7 gate 8 asks
that the routine `Engine.Tests` tier not grow a sweep, and it is already over its ~60 s ceiling.

**Gate: `tests/Engine.Tests` 1,119 routine passed + 1 pre-existing skip, in 4 m 46 s taken ALONE;
`tests/Ui.Tests` 6,145 in 23 s; `tests/Firewall.Tests` **6/6** — not the 4/4 every older entry above
quotes, because that project has grown since; `tests/Core.Tests` 1,238.** Plus this
phase's own opt-in tier: `ConformalFillOracleTests` **17 m 53 s** for its three Benchmark methods
(T0's reference-rule convergence, T1's vector block, T2's scalar block), `ConformalDiscConvergence`
2 m 35 s and `ConformalSliverTests`' two sweeps — so **~25 min added** to a repo tier that was
already ~40 min, which is where the phase's cost actually landed.

**§7 gate 8, answered with the measurement rather than by construction: this phase's whole routine
contribution is 37 tests in 2 s** (`--filter FullyQualifiedName~Conformal`). The tier's 4 m 46 s is
**not** this phase's — it stands at 1,119 tests against the 1,002 the G_A^zz entry above recorded,
and the growth is spread across every phase since, not concentrated here. L9e already flagged the
~60 s ceiling as breached and the reason it is left visible; nothing here improves or worsens it.

### R-cut-1 IS EXACT, and the disc ladder is what says so

Gate 6 — §7's own "single most important gate in the phase" — re-runs the edge-mesh brief's disc
ladder with `PlanarBoundaryCells` as the ONLY variable
(`tests/Engine.Tests/Mom/ConformalDiscConvergenceTests.cs`, `Category=Benchmark`, 2 m 35 s):

| 96-point disc, static C at εᵣ=1, N = 316 … 3,972 | staircase | conformal |
|---|---|---|
| tiling error **against the DRAWN artwork** | 2.1e-3 … 8.3e-3, **wandering** | **7.7e-16 … 6.5e-15 at every rung** |
| band of C over the last three rungs | 0.669% | **0.279%** |
| monotone over the last three rungs | **NO** | **YES** |

**The tiling row is the one that matters and it is exact, not merely improved.** R-msh-1's own
argument is that a mesh which does not tile its input solves a slightly different structure and
reports a smooth, plausible, wrong answer; L8b made that exact for Manhattan artwork only. The
conformal mesh tiles a 96-gon to round-off at every rung, so refining no longer changes WHICH
structure is being solved — which is precisely §0's "there is no converged value to aim at",
removed. The staircase's own tiling error wanders with grid alignment and shows no trend.

**AND A TRAP THIS GATE FELL INTO FIRST, worth keeping because it looks exactly like a mesher bug.**
Measured against a TRUE DISC the conformal error is **7.138e-4 at every rung, flat to four
figures** — which is exactly `(2π/96)²/6`, the inscribed 96-gon's own area deficit against the
circle it approximates. The first version of this gate asserted `< 1e-12` against `πr²` and failed:
it was measuring the FIXTURE's discretisation, not the mesher's. R-cut-1's claim is about the drawn
artwork, so the drawn artwork is what it is measured against; the circle deficit is reported
separately, and asserted to equal `(2π/n)²/6`, so it can never again be mistaken for an error.

**The band fell by 2.4×, NOT by the order of magnitude an earlier draft of the gate demanded. That
threshold was not met and is not asserted.** Said plainly rather than quietly relaxed: what the gate
asserts instead is the pair of properties §0 actually claims — the sequence is monotone, and the
tiling is exact at every rung — which say it far more directly than a band ratio does. Both
non-vacuity guards are in place: the staircase must still be non-monotone AND its own area error
must still wander, or the fixture is re-taken rather than read as a pass.

### The representation is a LIST OF CONVEX PIECES — a deliberate deviation from the brief

§2 suggests a half-plane `(nx, ny, d)` — "one straight cut" — and §8 flags that as a guess. Two
things it cannot carry, both of which the brief itself asks for: **R-cut-3's sliver MERGE** produces
an L-shaped or trapezoidal cell, which is not a rectangle minus a half-plane (a half-plane would
have forced the sliver remedy to be a SNAP, trading R-cut-3 against R-cut-1 — the two gates the
phase exists for); and **a cell the artwork crosses twice** is silently mis-described by a
half-plane, where a piece list either describes it exactly or refuses. Every quantity the fill wants
is additive over the pieces, so the list costs one loop and nothing else.

**A whole rectangle is a NULL region, not a four-vertex piece.** That is what makes R-cut-2's
bit-identity structural: `PairCores` dispatches to L8c's own expressions in L8c's own order unless a
half genuinely carries strips, so every pre-conformal number in this repository is reproduced bit
for bit rather than to a tolerance, and a MIXED pair pays the conformal path for one side only.

**The fill keeps L8c's own shape: the inner integral is CLOSED FORM and only the outer one is a
Gauss rule.** `PolygonIntegrals.CoresXY` generalises L8c's six rectangle forms onto a convex piece;
that is the whole reason a cut cell is affordable, and it is what an RWG basis would have given back
(L8c's own note: "the classic near-singular difficulty comes from doing BOTH integrals numerically,
and here only one of them is").

### Three per-cell configurations are REFUSED, and a refusal is a refinement instruction

All three fall back to L8b's staircase decision **for that cell alone**, counted in the notes: more
than one polygon of the layer touches the cell; a hole ring touches it; or the clipped region is not
convex (a reflex vertex of the artwork inside one cell — "a cell straddling a sharp corner"). On
Manhattan artwork none can fire, because every axis-parallel edge is already a hard gridline.

**"Refine and watch the count go to zero" is TRUE OF THE FIRST TWO AND FALSE OF THE THIRD, and that
is the phase's biggest limitation** — see "THE LIMITATION THAT MATTERS MOST" below. A reflex vertex
of the ARTWORK does not go away when cells shrink; past a certain density each one simply owns a cell,
and the count saturates. Measured on MKlopf: 126 reflex vertices, 126 fallback cells, permanently.

### M4 — everything else keyed to a rectangular cell, and the one that was already right

`tests/Engine.Tests/Mom/ConformalDownstreamTests.cs` (3 routine tests, 20 ms):

- **A calibration standard carries NO cut cell**, asserted on a DUT that is full of them.
  `PlanarCalibration.BuildLine` assembles from the DUT's own gridlines with the 6-arg `PlanarCell`
  ctor and never runs the conformal pass — a standard that acquired cut cells would be calibrating
  out something the DUT does not have.
- **A via footprint is uncut and its hard gridlines still tile it exactly** (0.000E+000 area error
  on the lower level). L9c measured that a via VANISHES silently without those gridlines; this is
  the same failure returning with cut cells as the excuse.
- **The reported cell extents are the GRID's own, never the cut region's**, and `MinCellEdgeM` /
  `MaxCellEdgeM` match the staircase's EXACTLY on the same artwork. `MaxCellEdgeM` is what λ_g/N
  caps and what λ_g/N caps is the grid pitch; `MinCellEdgeM` would otherwise report a sliver's
  extent — which R-cut-3 already reports separately as a merge count — and make every conformal mesh
  look far finer than it is, which is the wrong thing to tell a user deciding whether to refine.
  **`CellsAcrossNarrowestConductor` IS allowed to differ** (5 → 6 on an earlier fixture, 6 → 6 on
  the shipped one), because it counts cells covering metal and a conformal mesh genuinely covers
  metal a staircase drops; it can only rise.

**A finding rather than a fix: the §4 PORT refusal already existed and fired correctly**, on the
first run, against a fixture whose chamfer reached the MaxX end. A port on a cut cell is a different
port — its reference plane is the shared edge of two cells whose transverse extent is no longer the
grid's — and `PlanarPorts` refuses it by name, naming the boundary-cell control as the way back. The
fixture moved the obliquity to the middle of the part so both claims are testable at once.

### §5 — THE FOURTH CONTROL, and D3 is reopened

**D3 says `PlanarMeshSettings` carries "exactly three user controls, and no more"; this adds a
fourth on the owner's explicit instruction.** Recorded rather than slipped in — D3's reasoning
stands for everything else and is not generally relaxed. Why this one earns it: cells/λ and Edge
cells change how FINELY the same structure is discretised; **Boundary cells changes WHICH STRUCTURE
is discretised at all.** A staircased disc and a conformal disc are different geometry, not two
resolutions of one, and that is a modelling decision. It also needs an off switch on evidence rather
than taste: **every L8/L9 measurement in this repository was taken on the staircase**, and anyone
reproducing one must be able to.

Wired end to end, gated by `tests/Ui.Tests/Em/ConformalBoundaryCellsUiTests.cs` (8 tests, 0.3 s):

- **The `.cem` follows the omit-at-default rule**, so a file written before this phase gains no byte
  and re-serialises byte-identically — an asserted property of this format, not a nicety.
- **`EmSnpProvenance.MeshHash` includes it**, and this is the load-bearing one: an `.snp` produced
  under one boundary model is NOT current for the other, and the hash is the only thing that can say
  so. Leaving it out would have been exactly the staleness failure R-em-20 exists to prevent — one
  line, and easy to forget. The gate also asserts every other control still moves the hash, so the
  new term did not displace one.
- **It is ONE undo entry and it calls `InvalidateMesh()`** — the panel must not go on showing an N
  produced under the other model. Deliberately NOT routed through `CommitMeshField`, which is for
  staged TEXT fields; this commits on selection, like the edge-mesh checkbox.
- **`Auto` does NOT throw it away.** The other three controls change how finely Auto's own sizing is
  applied, so setting one by hand means "stop deciding this for me"; the boundary model is
  orthogonal — Auto has no opinion about whether a cell follows the metal — so `Resolved` carries it
  through. Clearing `Auto` here would silently pin the cell size the moment a user changed the
  boundary model.
- **The choice list is sourced from the enum**, so a third boundary model cannot silently fail to
  appear in the panel.
- **The notes name the model and the cut/merged counts**, and `StaircasedPolygons`' own note stops
  claiming a staircase once the cells are conformal.

### §3's own question, answered: the fill on a cut mesh, against an independent quadrature

§3 sets the bar and the comparison in one sentence: *"L8c reached 5.0e-6 there; this phase must say
what it reaches and whether the fill is still three decades more accurate than the kernel it fills
from."* Both halves, measured 2026-08-11 on a chamfered fixture (46 cells, 75 bases, 5 cut):

| | worst relative error vs the oracle |
|---|---|
| scalar block **P**, every NON-self entry (touching, cut↔cut, far) | **1.5e-12 … 1.1e-11** |
| scalar block **P**, self entries | 7.1e-7, **1.35e-6** |
| vector block **Z_A**, self rooftop pairs | 3.0e-8 … **2.34e-6** |

**So the answers are 1.35e-6 and 2.34e-6, both BETTER than L8c's own 5.0e-6 on a rectangular mesh** —
the conformal fill is not worse than the fill it generalises. And the kernel it fills from carries a
scaled error of **≤ 6e-3 across the entire span** (R-lgf-4), so 2.34e-6 is a factor of ~2,560 below
it: **~3.4 decades, so yes, the second half of §3's question is a yes too.**

**Every non-self entry is exact to 1e-11 or better, and that is the structurally informative part.**
The closed form and a direct 4-D quadrature that shares no line with it agree to round-off wherever
the supports are disjoint. All of the disagreement is in SELF terms — which is where a cut cell's
piecewise ramp and its own singular integral are actually being exercised, and exactly where an error
would be expected to live.

**PlanarPairOracle could NOT be extended to a cut support, and that is a fact about the oracle.** Its
whole construction is the cross-correlation identity, which needs the weight to be SEPARABLE; a cut
cell's ramp is measured from the metal's own oblique boundary, so it is affine in BOTH coordinates at
once and the domain is not a product of intervals. The replacement is a direct 4-D quadrature with
the sinh regularisation `(x − x₀) = |d|·sinh w` — the same substitution that fixed
`PolygonIntegralTests`' own oracle — evaluating no antiderivative, no corner sum and no closed form.

### The oracle was the harder half, and separating its two knobs overturned the obvious reading

**T0 exists because this area has recorded ten occasions where the REFERENCE was the broken part.**
It refines the quadrature and reports what it moves by, and nothing below it is believed until that
number is smaller than the differences being measured. Getting it there took an isolation the first
reading could not give:

```
  GRADING LEVELS, at 8 nodes:     L=2 → 3 → 4 → 5   moves 5.3e-8, 5.0e-9, 3.1e-10
  OUTER nodes, at inner 8:        8 → 12 → 16 → 20  moves 8.0e-7, 1.3e-6, 9.7e-7      (misleading)
  OUTER nodes, at inner 16:       8 → 16            moves 8.9e-8
  INNER nodes, at outer 8:        8 → 12 → 16 → 24  moves 9.3e-6, 7.8e-7, 3.3e-7
```

The outer and inner rules shared one `nodes` parameter, so the first reading — 9.6e-6 between (2, 8)
and (4, 14) — could not say which was open, and the obvious hypothesis (more grading levels) was
wrong: **levels are converged by L=4 at ~1e-9.** Split, the **outer rule turns out to be converged at
8 nodes** and only LOOKED like it was drifting: read at a badly-under-resolved inner it converges
toward the wrong integrand, and that ~1e-6-per-step "drift" is the inner error being resolved. At a
converged inner it moves 8.9e-8 and stops.

**The whole residual is the INNER rule on the PULSE self term** — the one entry whose observation
point lies inside its own domain — and it converges slowly, at ~n⁴ cost. The shipped reference rule
is **(levels 3, outer 8, inner 24)**, whose own residuals are:

| pulse self | outer knob | touching pair | ramp self |
|---|---|---|---|
| 3.3e-7 | 8.9e-8 | **2.3e-16** | **2.2e-11** |

**T0's gate is therefore 1e-6, not the 1e-7 an earlier draft asserted.** That value was not met, is
not claimed, and was in any case below what the reference can decide: asking the oracle to certify
itself to 1e-7 when its own self-term residual is 3.3e-7 is asking it for a decision it cannot make.
What T0 actually has to establish is that the reference's uncertainty is smaller than the differences
being measured — and 3.3e-7 against a 5e-6 gate is a factor of 15.

**The honest consequence for the self-term numbers above: the oracle is now the limiting instrument
there.** 1.35e-6 and 2.34e-6 are 4× and 7× the reference's own 3.3e-7, so part of each is the
reference rather than the fill, and the fill may well be better than measured. Every non-self entry
is limited by neither.

### R-cut-3's sliver threshold — MEASURED, and the measurement contradicts its own rationale

`tests/Engine.Tests/Mom/ConformalSliverTests.cs`. §7 gate 5 asks for "conditioning and answer either
side of it", and **that sweep did not exist**: `DefaultSliverAreaFraction`'s doc comment asserted the
value "is a MEASUREMENT, not a taste — see `ConformalSliverTests`", naming a file that was never
written. A constant claimed to be measured with no measurement behind it is worse than an admitted
guess, so the sweep is here and the comment now says what it actually establishes.

**Picking the density is the whole trick, and the first attempt got it wrong.** At cells/λ = 70 the
disc's thinnest cut cell is 1.5e-2 of its grid rectangle — there is no sliver to absorb, so the table
read "the threshold barely matters" for entirely the wrong reason. `G5c` then scanned cells/λ = 40 …
400 with merging OFF and found the mesher genuinely produces slivers down to **4.4e-4 of a grid
rectangle at cells/λ = 250** — a 1/Area factor of ~2,270. The sweep is run at densities that make one.

Two densities, chosen so the sweep is run where a sliver actually exists:

```
  cells/λ = 130   (thinnest cut cell 4.1e-3 of its rectangle — a 1/Area factor of ~245)
   frac    cells   cut  merged   min area/rect        κ(P)          C (fF)
   0.000     613   104       0   4.087E-003   5.4406E+001   137.4644
   0.001     613   104       0   4.087E-003   5.4406E+001   137.4644
   0.005     609   100       4   6.614E-002   5.3754E+001   137.4585
   0.020     609   100       4   6.614E-002   5.3754E+001   137.4585
   0.050     609   100       4   6.614E-002   5.3754E+001   137.4585
   0.100     593    84      20   2.760E-001   5.3027E+001   137.3518

  cells/λ = 250   (4.4e-4 — the THINNEST any density in G5c's 40 … 400 scan produced, ~2,264x)
   frac    cells   cut  merged   min area/rect        κ(P)          C (fF)
   0.000    2133   200       0   4.418E-004   1.2755E+002   137.9251
   0.001    2125   192       8   1.106E-002   1.0066E+002   137.9237
   0.005    2125   192       8   1.106E-002   1.0066E+002   137.9237
   0.020    2117   184      16   3.163E-002   1.0045E+002   137.9179
   0.050    2101   168      32   5.930E-002   1.0010E+002   137.8923
   0.100    2093   160      40   1.366E-001   9.9925E+001   137.8796
```

**R-cut-3 says a sliver "puts an enormous row in the matrix and destroys the conditioning". It does
not.** The effect is real but modest, and it scales with how thin the sliver is: **1.01× at a 245×
area ratio, 1.27× at a 2,264× one** (κ 127.55 → 100.10). κ never approaches anything dangerous in
either case. The reason is structural rather than a property of this fixture: P is normalised
1/(A_i·A_j) on BOTH sides, so it is a symmetric diagonal scaling D·P₀·D — but **a patch's
self-potential grows as the inverse of its LINEAR size, not its area**, so a 2,264× area reduction
buys a ~48× diagonal entry, not a 2,264× one. Most of the 1/Area blow-up the rule is written against
is cancelled by the kernel's own scaling.

**Almost all of the available improvement comes from absorbing the ONE thinnest cell.** At cells/λ =
250 a threshold of 0.001 — merging 8 cells — already takes κ from 127.55 to 100.66; going all the way
to 0.05 (32 cells) buys a further 0.6%. So the threshold's value is in catching the extreme tail, not
in how much it absorbs.

**So the honest claim for 0.05 is CONSERVATIVE, not optimal.** It sits on a plateau (0.005 … 0.05
produce the identical mesh at cells/λ = 130) and comfortably above the thinnest sliver observed
anywhere. Nothing here located a conditioning cliff, and the shipped doc comment no longer claims
one — it previously said the value "is a MEASUREMENT, not a taste", which was true of neither.

The answer moves 0.004% (cells/λ = 130) to 0.024% (250), and `G5b` asserts directly that merging
never loses area at any threshold — the merged cell carries BOTH pieces, so R-cut-1 survives it — so
that residual is a discretisation difference and not a tiling failure.

### THE LIMITATION THAT MATTERS MOST, and it is on the part with the most to gain

**Gate 1's own table, on the REAL library PCells** (`tests/Ui.Tests/Em/ConformalBoundaryCellsUiTests.cs`
— they live in `src/Ui` and the reference graph is Ui → Engine, so an Engine test cannot reach them),
cells/λ = 20, edge mesh on:

| part | staircase area error | conformal | fallback cells |
|---|---|---|---|
| MBend mitred, PCB / MMIC | 0.096% / 0.112% | **1.3e-15 / 9.4e-16** | 0 |
| MTaper 2.9→1.0 mm, PCB / MMIC | 0.465% / 0.465% | **3.5e-15 / 4.6e-15** | 0 |
| **MKlopf on-axis, PCB** | 0.593% | **0.766% — WORSE** | 52 |
| MKlopf Offset, PCB | 0.540% | 0.750% — worse | 50 |
| MKlopf on-axis, MMIC | 0.278% | 0.189% | 90 |
| MKlopf Offset, MMIC | 0.334% | 0.084% | 114 |

**Bends and tapers are exact on both starters. MKlopf is not, and on the PCB starter it is worse than
the staircase it replaced** — the opposite of the phase's headline claim, on the one part whose entire
value is a controlled 0.05 equiripple. Reported rather than left as a footnote.

**The cause is §2's case (c) — a non-convex clip — and it is a REFUSAL, not a representational
limit.** `PlanarCellRegion` already holds a LIST of convex pieces, so a non-convex clipped region
*could* be convex-decomposed; the mesher chooses to fall that cell back to a staircase instead. §1
scopes this phase to "one straight cut per cell, and no more", so decomposing is out of scope — but
it is the concrete fix, not a wall.

**Refining does NOT clear it, and the reason is exact rather than approximate.** `G1b` walks
cells/λ = 20 … 320 and the fallback count *saturates*:

```
  cells/λ      N    cut  fallback   conformal err   staircase err
       20    745     56        52   7.663E-003       5.927E-003
       40   1100     58        78   7.269E-004       5.436E-004
       80   2098    110       126   3.397E-003       4.657E-004
      160   8517    334       126   2.416E-004       2.544E-003
      320  34292    780       126   7.745E-005       1.217E-004
```

A count that stops falling under refinement is a count of FEATURES OF THE ARTWORK, not of cells —
and `G1c` closes it: **MKlopf's own outline has 194 vertices, 126 of them REFLEX, and the plateau is
126.** One permanent fallback per concave vertex, once the mesh is fine enough that each sits in its
own cell. `SmoothSteps` blends each end of the taper, which is what puts the inflections there.

**So the honest scope of this phase's guarantee is: exact on artwork whose rim is convex where it is
cut** — every bend, every straight taper, the disc — **and best-effort on artwork with a concave
rim.** MKlopf's conformal answer does still beat the staircase at fine meshes (7.7e-5 vs 1.2e-4 at
cells/λ = 320) and does so on MMIC at every density; it is the coarse PCB case that regresses.

### The default, and what would flip it

**Ships OFF.** §5 says to flip to `Conformal` only when §3's own gate passes — the fill's accuracy
against the oracle, the tiling gate, the sliver gate, and a measured N against R17. **All four now
pass**: the fill reaches 1.35e-6 / 2.34e-6 against L8c's own 5.0e-6, tiling is exact at every rung of
the disc ladder, the sliver threshold is measured and conservative, and N is essentially unchanged
(316 vs 324 and 3,964 vs 3,972 on the disc; 547 vs 550, 704 vs 714, 745 vs 743, 579 vs 590 on the
shipping PCells — a conformal mesh is not more expensive).

**It still ships off, for two reasons, and the second is the substantive one.**

1. **Every accuracy figure recorded in this repository was taken on the staircase**, and anyone
   reproducing one has to be able to. That alone makes flipping a separate, deliberate act.
2. **MKlopf regresses at coarse PCB settings** — 0.77% against the staircase's 0.59%, because of the
   reflex-vertex fallback above. Shipping a default that makes one shipping library part *worse* is
   not defensible, however good it is on the other three. **The convex-decomposition follow-up is
   what removes this objection**, and after it the case for flipping is straightforward.

Flipping the default is a separate, deliberate act with its own line here, because it moves every
number a user has previously recorded.

## Convex decomposition — M0 said the PREDICATE was wrong, so nothing was decomposed

**A FOLLOW-UP to the conformal phase above, and the M7 its §6 named.** Brief:
`docs/sonnet-briefs/brief-convex-decomposition.md`. **M0 and M1 are done; M2 (Route B, an actual
decomposition) was NOT BUILT, because M0 measured it unnecessary.** It touches no Green's function,
no DCIM, no kernel set, no de-embedding algebra and no solve — and, as §1 suspected, no closed-form
integral either.

```
RooftopSupport.cs      + FlowSimple on the support, IsFlowSimple(region, alongX, tol), and the
                         private Intervals() the two share with Extent's own crossing walk
PlanarCellRegion.cs    + a per-piece convexity flag, so Contains keeps the half-plane test where it
                         is valid and takes a ray cast only where M1 made one necessary
ConformalDiagnostics.cs NEW — M0's instrument, optional and defaulted null
SurfaceMesher.cs       the predicate swap, R-cvx-2's second count, and the notes
PlanarMeshReport.cs    + OneDirectionCells
```

Gate: **`tests/Engine.Tests` 1,135 routine passed + 1 pre-existing skip, in 4 m 46 s** — the same
wall clock the conformal phase recorded, so this phase's +17 routine tests cost nothing measurable.
`tests/Ui.Tests` **6,173 and green**, `tests/Firewall.Tests` 6/6. **This phase's whole opt-in
contribution is ONE method** (`ConformalFillOracleTests.T4`, 2 min) against the conformal phase's own
~25 min, which is §7 gate 9's own request.

### THE FINDING: `IsConvex` was a SUFFICIENT test being used as a NECESSARY one

§1's guess, and the measurement confirms it. What `RooftopSupport.Build` actually needs is not
convexity but

> **FLOW-SIMPLICITY. At every transverse coordinate, the region's intersection with that line is a
> single interval** — because `Extent` returns the outer HULL of the crossing set and `Build` spans
> ONE trapezoid across it, so a region met twice has source integrated over a gap in the metal.

Convexity implies it **in both directions**; flow-simplicity is much weaker and is **per direction**.
The standing counter-example was already shipping: R-cut-3's merged cell is L-shaped, has never been
convex, and has always worked — because two pieces sharing a face meet any line in one interval.

**M0's table** (`ConvexDecompositionM0Tests`, three shipping PCells × two starters × cells/λ 20/80/
320, with the 96-point disc as the zero-fallback control):

| | refused cells | flow-simple in BOTH | in exactly ONE | in NEITHER |
|---|---|---|---|---|
| whole table | **1,158** | **1,152** | 6 | **0** |

MBend and MTaper refuse nothing at any density; every refused cell is MKlopf's, 52/126/126 on the PCB
starter and 90/90/114 on MMIC. The 6 one-direction cells are two distinct cells (one per starter) on
MKlopf Offset, y-simple only. **The disc control refuses nothing at any density**, as §2 requires.

**§2's outcome 1, so Route A alone**: the refusal moves from the CELL to the BASIS, and no
decomposition is built. §4's Route B, R-cvx-6, R-cvx-8 and R-cvx-9 are all **measured deferrals** —
the residue they exist for is empty. The precedents for that shape of answer are L7b-b's Route B,
L9c's amplitude cap, L9e's ACA, the edge-mesh brief and the ground-via chain.

### The prize, and it is the whole reason the phase existed

Measured against the **DRAWN artwork** (the conformal phase's own recorded trap — measuring a 96-gon
against a true disc reports the fixture's deficit, not the mesher's), cells/λ = 20:

| part | staircase | conformal BEFORE M1 | conformal AFTER M1 |
|---|---|---|---|
| **MKlopf on-axis, PCB** | 0.593% | **0.766% — WORSE** | **5.4e-15** |
| MKlopf Offset, PCB | 0.540% | 0.750% — worse | 1.5e-15 |
| MKlopf on-axis, MMIC | 0.278% | 0.189% | 9.1e-15 |
| MKlopf Offset, MMIC | 0.334% | 0.084% | 8.1e-15 |
| MBend / MTaper, both starters | 0.10–0.47% | 1e-15 | 1e-15 (unmoved) |

**And the saturation ladder is gone.** `G1b` re-runs cells/λ = 20 … 320 and the fallback count is
**0 at every rung**, against the 52 / 78 / 126 / 126 / 126 plateau that was the signature this phase
existed to remove. `G1c` is **updated rather than loosened**: the outline still has 194 vertices and
**126 of them are still reflex** — the artwork did not change — and they now cost nothing. That is a
stronger statement than the old count-matching one.

### R-cvx-7's gate does not survive Route A, and saying so is part of the deliverable

§7 gate 5 asks for **N as an EQUALITY** against the pre-M1 conformal counts (547 / 704 / 745 / 579).
**It does not hold and asserting it would be asserting something Route A does not claim.** The gate
is written for Route B, where a cell gains PIECES and must not gain CELLS. Route A never splits a
cell; it changes which cells EXIST, because a cell the old predicate refused was rounded to
whole-or-absent and is now cut — so a grid position that held no cell can hold one, and the adjacency
count moves with it. **Measured, PCB starter: 547 / 704 / 745 → 761 / 579 → 577.** MBend and MTaper
are unmoved; MKlopf on-axis gains 16 unknowns and MKlopf Offset loses 2.

What IS asserted instead (`ConvexDecompositionTests`) is the invariant the gate is actually for:
**at most one cell per (layer, IX, IY)**, and every basis joining two distinct cells exactly once —
so nothing was subdivided and L8c's fill and L9c's via basis still index what they think they do.

### What the swap cost elsewhere, enumerated rather than left to discovery (§5)

- **`PlanarCellRegion.Contains` DID assume convexity**, exactly as §5 suspected. The half-plane test
  it used is what produced every conformal number in this repository, so it is KEPT for a convex
  piece — decided once at construction — and a genuinely non-convex piece takes a ray cast. Bit
  identity where it matters, by construction rather than by care.
- **`PolygonIntegrals` needed nothing.** Its edge reduction "needs neither convexity nor the
  observation point being inside", and that claim holds: `T4`'s newly-admitted cells reproduce an
  independent 4-D quadrature to **1.46e-6**, against T1's own 1.35e-6 on convex cut cells and L8c's
  5.0e-6 benchmark for the whole fill. **§7 gate 7 is gated at 5e-6 and not tighter**, because the
  reference's own residual on a pulse self term is 3.3e-7.
- **`RooftopSupport.Tiles` had to choose a DIRECTION**, and that is the one place the swap could have
  gone silently wrong. The tiles carry a unit weight so the direction looks free — but it is the
  DIVERGENCE PULSE's own domain, and strips taken across the axis a one-direction cell is crossed
  twice along would span the gap. It builds X and falls back to Y when X is not flow-simple.
- **`PlanarCellRegion.Area`, `CentroidX/Y`** — signed-area formulas, exact for any simple polygon,
  untouched. **`LayoutRenderer.PlanarMesh`** draws the region as an `SKPath`, which a non-convex ring
  is fine as. **`PlanarCurrentDensity`** reads `Area / Width`, not `Height`, already.
- **R-cvx-5, the strongest available regression check, passes**: every merged cell on the disc at
  cells/λ = 130 and 250 is flow-simple in BOTH directions. (The counts are not equal and should not
  be — `MergedSliverCount` counts ABSORPTIONS and one host takes up to four: 32 merges into 28 hosts.)
- **Manhattan is BIT-IDENTICAL**, unchanged: `C1` compares gridlines, cells and bases as equalities
  and §10.7's hero is still exactly **N = 552**. It cannot move — a Manhattan polygon has no oblique
  edge, so no cell is cut and no predicate is asked.

### R-cvx-2 — the refusal moved from the CELL to the BASIS, and the two counts are reported apart

`StaircaseFallbackCells`' non-convex component NARROWED to "flow-simple in neither direction", which
M0 measured at **zero** everywhere. `PlanarMeshReport.OneDirectionCells` is the new, different event:
the cell is CUT and tiles the metal exactly, and one of its two bases is declined. Both have their
own note. **A mesher that silently drops a basis is as bad as one that silently re-shapes a cell.**

### THE LIMIT THIS DID NOT REMOVE, and it is now the binding one at a PORT

§0's counter-measurement asked whether the area error is a proxy for the LOCAL WIDTH error a
Klopfenstein actually cares about. It is not, and the honest answer arrived from the port side rather
than from a width sweep: **R-cut-4's `Anchored` test is all-or-nothing over a support's strips.** A
shallow oblique rim leaves a sliver strip at the top of the outermost cell whose metal does not reach
the shared face, and the WHOLE rooftop is declined for it. On MKlopf's 50 Ω feed that is one cell at
each end of the port's transverse run — **17.3% of the feed's width at cells/λ = 20, 11.4% at 40,
5.0% at 80** (see `src/Ui/CLAUDE.md`'s own entry for the port half).

**And the thing that does NOT go wrong, measured rather than assumed:** a cut cell that DOES survive
R-cut-4 has a full shared face, so a conformal port's width is the grid extent to the last bit —
short faces and un-swept halves are the same geometric condition, so the pair carrying one is already
refused. `ConformalPortTests` shows it on a slanted-end fixture with 7 cut cells in the port's run.

**The concrete way to close the undriven metal, deliberately not taken:** accept a nearly-swept
support instead of refusing it. The unswept strip is **~0.56% of that cell's area**, so it would carry 0.994 A
instead of 1.000 A — against losing 12.5% of a port's width by dropping it. The trade looks
overwhelming and it is still a separate act, because it retires L8c's **exact** `∫f·û dℓ = 1 A`
(gated at machine precision by `ConformalBasisTests.B3`) for a bounded one, and that needs its own
measurement of what the deficit does to an s-parameter.

### The default: the SECOND reason to keep it off is gone

The conformal phase's own note gives two reasons for shipping `Staircase`, and says the second is the
real one — MKlopf regressing. **That reason no longer exists**: MKlopf tiles to round-off on both
starters at every density. What remains is the first, which is bookkeeping: every accuracy figure in
this file was taken on the staircase and anyone reproducing one has to be able to. **Flipping the
default is still its own deliberate act with its own line here**, and the undriven-metal limit above
is what a reader should weigh before taking it.

## "Long enough to crowd" — the edge attractor a two-scale part was denied (2026-08-11)

**A follow-up to the port fix above, and the user found it by looking at the mesh overlay:** on the
reported MKlopf the metal near port 2 was finely meshed and the metal near port 1 was not. Not
boundary cells — the EDGE MESH.

`CollectBoundaryLines` gives an axis-parallel boundary edge a graded fan when it is **at least a
fifth of the polygon's own extent across it**. The purpose is to stop a drawn staircase demanding a
fan per tread, and for that it works. **The yardstick is wrong for a part whose features differ in
size**, measured on that taper:

```
  extent Y = 21.989 mm  ->  threshold 0.2 x extent = 4.398 mm
    end cap x = 0.0  mm : 2.998 mm  <  4.398  ->  gridline only, NO fan
    end cap x = 50.8 mm : 20.292 mm >= 4.398  ->  ATTRACTOR
  x-cell widths from x = 0   : 356.9, 356.9, 356.9, 356.9 um
  x-cell widths up to x=50.8 : 356.9, 237.1, 141.7,  94.3 um
```

**The 50 Ω end — where the crowding is strongest and where the port sits — was graded by nothing,
because the OTHER end set the threshold.** L8b's own control measured what grading is worth on this
quantity: 4.437% → 0.431% on a Manhattan square, ~10× for 3.6× the unknowns.

### The second clause, and it is the statement the first one was reaching for

**An edge that TERMINATES the conductor has both its corners convex; an edge that is part of a longer
boundary chain does not.** A staircase alternates convex and reflex, so every tread and every riser
has one of each and is still excluded — the property the first clause exists to protect, now held for
a reason rather than by a proxy. O(1) per edge, purely local, and it is an **OR**: nothing that
graded before can stop.

**The floor is DERIVED.** R17 caps this kernel at ~5,000 unknowns ≈ 2,500 cells ≈ a 50 × 50 grid, so
one cell is ~2% of the extent per axis at the finest mesh it can afford; an edge shorter than that is
sub-cell however the mesh is refined, and grading it spends gridlines across the whole TENSOR grid on
a feature the mesh cannot represent. `CapMinFractionOfExtent = 0.02`. It also bounds the one
pathological case — artwork with hundreds of small Manhattan features would otherwise ask for four
fans each.

**Measured after: both caps grade, and the narrow end's fan MIRRORS the wide one's** —
94.2 / 146.9 / 228.9 / 356.6 µm inward from x = 0 against 356.6 / 191.3 / 114.3 / 94.2 µm inward from
x = 50.8, with the outermost cell equal to 12 figures.

### What it is gated on, and what did NOT move

`tests/Engine.Tests/Mom/EdgeAttractorCapTests.cs` (6 routine tests, **15 ms** — all meshing):

- a narrow cap on a part whose other end is wide earns its fan, and the GRID responds;
- **the floor brackets**: 1% of the extent does not qualify, 3% does — so the refusal above is about
  the floor and not about the cap clause having quietly stopped working;
- **a drawn staircase still contributes no fan per tread**, asserted the way D9's own guarantee is:
  on the COUNT, held invariant at 10 / 20 / 40 steps (1 x-attractor and 2 y-attractors at every one);
- **§10.7's hero is still exactly N = 552** and still has exactly 2 + 2 attractors — all four of its
  edges already passed the first clause, so the second can only re-qualify them, never add a fifth.

Blast radius run: `Engine.Tests/Mom` **563 passed in 4 m 44 s**, `Ui.Tests/Em` **293 in 8 s**,
`Firewall` 6/6.

## M0 — the mesh frequency, and the measurement that decides whether it is safe

The first milestone of `docs/sonnet-briefs/brief-em-sweep-performance.md`, and the only one in that
brief that can change an answer — which is why its own gate is an accuracy measurement rather than a
pass/fail. It adds no physics: `PlanarMeshSettings.MeshFrequencyHz` (`double?`, **null = the sweep's
own top, which is today's behaviour exactly**) is the frequency the λ_g/N cell-size cap is taken at,
and `SurfaceMesher.Mesh` derives λ_g from it through the SAME `problem with { MaxFrequencyHz = f }`
pattern `PlanarKernel.cs:197` already uses. There is one way to ask a problem for λ_g at some other
frequency and this reuses it.

### THE MEASUREMENT, and it is the deliverable

`tests/Engine.Tests/Mom/MeshFrequencyAccuracyTests.cs`, `Category=Benchmark`, **taken ALONE** (L8d's
own standing warning). The reference is today's behaviour — mesh sized at the sweep's top — so what
is reported is the error the CONTROL introduces, read against the residuals this directory already
measured: **L8d's 6.0e-3 at 10 GHz on 1.6 mm FR-4, and L9d's ~1e-2 on a two-level structure.**

**§10.7's own FR-4 hero** (2.9 × 20 mm on 1.6 mm FR-4), 1–20 GHz, 9 de-embedded points, cells/λ = 20:

| mesh sized at | N | wall clock | worst \|ΔS\| 1–10 GHz | worst \|ΔS\| 10–20 GHz |
|---|---|---|---|---|
| **20 GHz** (the sweep top — reference) | 1,345 | 352.4 s | — | — |
| **10 GHz** (half) | **552** | **127.8 s** (2.76×) | 2.97e-3 | **1.50e-2** |
| **5 GHz** (quarter) | **348** | **103.6 s** (3.40×) | 1.40e-3 | **1.58e-1** |

**The answer, stated plainly: HALVING is defensible and QUARTERING is not.** At half the mesh
frequency the error below the mesh frequency is 3.0e-3 — under L8d's own 6.0e-3 residual, i.e. below
what the method already cannot resolve — and 1.5e-2 at the top of the band, which is *at* L9d's ~1e-2
rather than under it. At a quarter it is **1.6e-1 at the top of the band**, an order of magnitude past
any residual this directory has ever measured: that is not a knob setting, it is a different answer.
**A measured "this knob is not safe below X" outranks the saving**, and X is about half the sweep top.

**Three things in that table are worth keeping for their own sake.**

1. **The 10 GHz row's N is exactly 552** — §10.7's own hero number, which this directory has quoted
   since L8b and which was always measured *at 10 GHz*. The mesh-frequency mechanism reproduces it
   to the unknown, which is a stronger cross-check on the plumbing than any assertion in the routine
   tier.
2. **The saving is AXIAL ONLY and the numbers say so.** N falls 2.44× for a 2× mesh-frequency drop
   and only 3.86× for 4× — nothing like quadratic, because `MinCellsAcrossConductor` sets the
   transverse pitch on this geometry and does not respond to λ at all. **Do not describe M0 as a
   quadratic saving.** Wall clock tracks N rather than N², for the same reason.
3. **The error below the mesh frequency is NOT monotone in the control** — the quarter-mesh run is
   *better* than the half-mesh run over 1–10 GHz (1.4e-3 against 3.0e-3). Reported rather than
   smoothed: at that level both are inside the method's own residual, so the ordering there is noise,
   and the only reading the data supports is the one that matters — **the error is concentrated
   ABOVE the mesh frequency**, where the cells are genuinely under-resolved.

`M0Accuracy_GaAsHero_ReportedPerDecade` is the same measurement on the MMIC stack. **Its band starts
at 5 GHz, not 1 GHz, and that is a PRE-EXISTING refusal rather than anything M0 introduced**: a 100 µm
substrate at 1 GHz has `PathExtent·k₀H = 0.63`, so DCIM's sampling path stops before it reaches the
k_ρ ~ 1/H scale the stack's image structure lives at, and `Dcim.CanFitAtFrequency` refuses by name
(L9e's D8). Both bands keep the same 20 GHz top so the ratios stay comparable.

### THE OTHER FINDING, and it reframes the whole milestone: M0 is `CellsPerWavelength` RE-PARAMETERISED

The cap is `cellSize = λ_g(f_mesh)/N = c / (f_mesh · √ε · N)`, so it depends on the **product**
`MeshFrequencyHz × CellsPerWavelength` and on nothing else. **M0 produces no mesh
`CellsPerWavelength` could not already produce.** Asserted as bit-identical GRIDS rather than equal N
(two different meshes can share an unknown count) by
`MeshFrequencyTests.ItIsCellsPerWavelength_REPARAMETERISED_OnlyTheProductMatters`: cells/λ = 5 at a
20 GHz sweep top and cells/λ = 20 with the mesh at 5 GHz give the same `MaxCellSizeM` to the last
bit, the same `GridX` and the same `GridY`.

**So what M0 actually buys is the parameterisation and the REPORT, not a new capability.** Cells/λ
alone cannot say *where in the band* the resolution was spent, because λ itself moves across the
band; "sized at 5 GHz, so λ_g/5 at the 20 GHz top" can, and that is the number the accuracy table
above is indexed by. Read the FR-4 rows as **effective cells/λ at the sweep top**: λ_g/20 is the
reference, **λ_g/10 costs 1.50e-2** and **λ_g/5 costs 1.58e-1**.

### What that says about §0's own design, which is NOT what the brief predicted

§0 estimates that sizing the §0 MKlopf taper's mesh at 10 GHz "roughly halves the axial cell count".
**Measured on that exact fixture (`ConformalPortTests`' reported setup — Z1 = 50, Z2 = 12,
Γmax = 0.05, L = 50.8 mm, PCB starter, conformal, EdgeCells = 3):**

| cells/λ | mesh at 20 GHz | at 10 GHz | at 5 GHz |
|---|---|---|---|
| **5** (§0's own setting) | N = 1,038 | 1,078 | 1,078 |
| **20** (the shipped default) | N = 8,418 | 2,098 | 1,038 |

**At §0's own settings M0 saves nothing — N does not fall, it rises slightly and then stops moving.**
The reason is the equivalence above: **that user had already taken the entire saving by hand**, by
running cells/λ = 5 instead of 20. `cells/λ = 5 at the sweep top` and `cells/λ = 20 at 5 GHz` are the
same 1,038-unknown mesh, which is why the two table corners agree exactly.

**And the useful half of that: the §0 setup is running at effective λ_g/5 at the top of its band,
which the FR-4 measurement prices at ~1.6e-1 in |ΔS| — an order of magnitude past any residual this
directory has measured.** That is a real warning about a real user's current settings, found by this
work rather than by the run failing. The remedy M0 makes statable is the middle cell: **cells/λ = 20
with the mesh at 10 GHz is N = 2,098 — inside R17's 5,000 ceiling, where cells/λ = 20 at the sweep
top (N = 8,418) is refused outright — and buys λ_g/10 (~1.5e-2) for twice the unknowns.**

**Not run, and stated rather than implied:** a full de-embedded accuracy sweep ON the §0 taper. The
equivalence above makes it a re-measurement of a quantity the FR-4 table already indexes (effective
cells/λ), on a design whose own N table is in hand; §0 measured one de-embedded point there at
238.3 s, so the three-row sweep is roughly an hour. If a taper-specific accuracy number is wanted, it
is `MeshFrequencyAccuracyTests`' own shape pointed at `ConformalPortTests.ReportedSetup`.

### THE FINDING: on a narrow conductor, lowering the mesh frequency can RAISE N

The saving is not merely sub-quadratic — **on some geometry it is negative**, and the first version of
M0's own benchmark asserted that N fell monotonically and was wrong.

**Measured, 2 mm × 72 µm on 100 µm GaAs, sweep top 20 GHz: N = 773 / 705 / 2,014** at mesh
frequencies of 20 / 10 / 5 GHz. The mechanism is R-msh-5 meeting R-msh-3: the outermost edge cell is
`EdgeFractionOfReference` × the conductor WIDTH, while the bulk cell is λ_g/N. Coarsening the λ cap
widens the gap the graded fan has to bridge between the two, and past some point the fan costs more
cells than the bulk saves. On this fixture the axial pitch also stops responding entirely well before
that — `MinCellsAcrossConductor` caps it at a quarter of the conductor's own run — so past 10 GHz the
fan's growth is the only thing left moving.

**Consequences, all of them acted on rather than noted:**

- M0's accuracy benchmark **reports N per row** and asserts nothing about its direction.
- The panel shows the unknown count, so a user can see it. **Do not add a UI hint that a lower mesh
  frequency is cheaper** — on a narrow-conductor MMIC it is not.
- It is why the FR-4 table above is not evidence on its own. The trade is a property of the STACK and
  the ARTWORK together, not of the control.

Gated cheaply and permanently by `MeshFrequencyTests.OnANarrowConductor_LoweringIt_CanRAISETheUnknownCount`
(milliseconds — it only meshes), so the behaviour cannot quietly change without someone noticing.

### R-emp-3 — `MaxFrequencyHz` still means THE SWEEP'S TOP, and that separation is load-bearing

`PlanarProblem.MaxFrequencyHz` used to answer two unrelated questions; M0 leaves it answering exactly
one — *how high does this sweep go* — and three consumers still read it unchanged:
`PlanarKernel.CanSolve`'s electrical via bound (R-via-6), the ρ/λ validated-range note at
`PlanarKernel.cs:197`, and `EmSnpProvenance`'s GEOMETRY hash. **Pointing any of them at the mesh
frequency would let a user silently widen a PHYSICS refusal by turning down a PERFORMANCE knob.** The
guarantee is structural rather than numeric and is gated as such: `CanSolve` is handed a
`PlanarProblem` and nothing else, and `MeshFrequencyHz` lives on `PlanarMeshSettings`, which it never
sees. The one place a mesh setting *does* reach a verdict — R17's unknown budget — is a budget, and
lowering the mesh frequency may only ever relax it.

### R-emp-2 — the report, because a report that names the wrong frequency is the failure mode

`PlanarMeshReport.FrequencyHz` is the MESH frequency now, and the λ_g note quotes it. With the control
unset the note still ends *"the highest frequency of the sweep"*, unchanged; with it set below the top
that clause is replaced and a **second** note quantifies the trade in the unit the user actually set:

> The mesh was sized at 10GHz, not at the sweep's 20GHz top. At 20GHz the cells are λ_g/10 rather
> than the λ_g/20 you asked for. Raise Mesh frequency, or raise Cells per wavelength, if the top of
> the band matters.

Effective cells/λ at the sweep top is `CellsPerWavelength × MeshFrequencyHz / sweepTop` — a physical
quantity, never hertz. The note fires strictly BELOW the sweep's top; at or above it there is nothing
under-resolved to report, and `TheUnderResolutionNote_FiresBelowTheSweepTop_AndNotAtOrAboveIt` pins
all three of unset / exactly-at / above.

**No refusal was added.** The brief forbids one without a measurement first, and the measurement above
is what a floor would have to be derived from — see "the answer" for what it says. Stating it as a
note the user can act on, rather than a refusal, is the same choice `Dcim.ValidatedRhoOverLambdaInteriorHorizontal`
already made for an unmeasured range.

### `Resolved` carries it through Auto, like `BoundaryCells` and for the same reason

Auto decides cells/λ and edge cells — a RESOLUTION. Which frequency that resolution is applied AT is
a different question and Auto has no opinion about it. A fixture that sets this and leaves Auto on
must not silently mesh at the sweep's top instead; that is the shape that already cost this area once.

Gate: `tests/Engine.Tests/Mom/MeshFrequencyTests.cs`, **8 routine tests in ~20 ms** (unset is
bit-identical to before; halving coarsens but not quadratically; the re-parameterisation equivalence
as bit-identical grids; the narrow-conductor N RISE; Auto survival; both notes; and the structural
no-refusal-sees-the-mesh-frequency check), plus **2 `Category=Benchmark`** measurement methods — the
FR-4 one is **9.7 min**, which is why it is opt-in. `tests/Ui.Tests/Em/MeshFrequencyUiTests.cs` adds
**9 routine**.

Full routine gate after M0: `Core` 1,238 · `RfCore` 281 · `WBond` 237 · `Firewall` 6 · `Ui` **6,202**
· `Engine` **1,149 + 1 pre-existing skip**, all green. The one failure in a full run is
`Harmonica.Tests.ContextAndPersistenceTests.R11_AMissingReferencedModelIsNamedRatherThanSubstituted`,
which `src/Ui/CLAUDE.md` already records as failing deterministically at HEAD; nothing in this work
touches `src/Harmonica`.

---

## Sections 8–12 archived 2026-08-13 (M1/M2 parallelism, calibration-standard fill, M5 AIM accelerator decision gate + build)

These were CLAUDE.md's own §8 through §12 (lines 684–1143 as they stood just before this
archival pass) — the regrowth after the 2026-08-11 split above. Moved here verbatim;
CLAUDE.md now carries only the condensed shipped-defaults/negative-result summary.

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

> **Superseded at P4–P6 (2026-08-29) — see §P6's own table below for the numbers.** Every figure in
> the table above was measured when the near field's singular cores were integrated per PAIR and per
> FREQUENCY. P5 keys them by translation class (the 1,074 ms near fill at N = 3,731 in this table's
> 25.18 s build was 8.3 s after P4 and 1.2 s after P5), and P6 builds the accelerator's
> frequency-independent state — stencils, near set, and every singular core — ONCE per mesh. Measured
> directly rather than scaled, one dense point (P5 fill + LU) against one accelerated point is
> 0.24 / 0.54 / 2.03 s against 0.37 / 0.71 / 0.96 s at N = 552 / 994 / 1,912, so **the time crossover
> is now N ≈ 1,100**, and at N = 3,731 the accelerated point (1.4 s + 0.1 s solve) is an order of
> magnitude under the dense one (1.25 s fill + ≈ 18 s LU). The memory column is unchanged in kind and
> within ±2 MB in value (§P6).

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

---

## 13. brief-em-aim-ceiling — the ceiling MOVES, to 12,000, single-level only (2026-08-14)

The debt §12 closed with: measurement above N = 3,731, on a via-bearing or cut mesh, plus a decision on
whether R17's ceiling moves. Triggered by a real refusal (`docs/sonnet-briefs/brief-em-aim-ceiling.md`
§0): a user's 6.92 Ω → 100 Ω Klopfenstein taper meshed at N = 7,749 — 13% over R17's 5,000 — with no
combination of panel settings under it.

### §3 first — the near-field radius discrepancy, resolved before the ladder ran

CLAUDE.md's AIM paragraph stated the near-field radius twice, in two different units, as if they were
one number: §11's decision gate (pre-AIM, testing GMRES viability on the DENSE matrix with a naive
near-field preconditioner) measured in MESH CELLS — 3 degrades with N, 8 is flat. R-emp-17 (§12, on the
accelerator actually built) measured `PlanarAimSettings.NearRadiusFactor` in units of the LARGEST BASIS
SUPPORT and landed the shipped default at 6, not 8 — order 3 there put `|ΔI|` at 8.7e-7, inside L8c's
5.0e-6 fill accuracy, and radius 8 supports bought a further 3.6× for cost the table itself said was not
worth it. **The code (`NearRadiusFactor = 6.0`) was always right; the prose conflated two different
knobs.** Corrected in CLAUDE.md rather than in code.

### A1 — the N ladder, two constructions, and they told two different stories

Built past M5's own N = 3,731 top rung, to and past N = 12,000, on the FR-4 hero cross-section at
6 GHz. **Dense is a HARD refusal above N = 5,000 through the shipped API** (`PlanarFill.GuardCeiling` /
`PlanarSystem.GuardCeiling` throw unconditionally) — not merely slow — so "the top of the ladder where
a dense reference exists" is exactly R17's ceiling itself, not the ~8,000 the brief guessed at before
measuring. `AimCeilingTests.cs`, `Category=Benchmark`.

**A1a — grow the LENGTH at the shipping mesh (cells/λ = 20, edge grading on).** The construction that
matches how a real board actually gets big — a wide-to-narrow taper included, since MinCellsAcrossConductor
pays for the narrow end everywhere and length simply adds more of the same cell:

| L | N | near/row | near % | build s | iters | resid | solve s | dense s | \|Δcurrent\| | MB | dense MB |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 256 mm | 3,731 | 392 | 10.5% | 24.8 | 6 | 1.2e-9 | 0.11 | 28.9 | 1.12e-5 | 53.3 | 212.4 |
| 320 mm | 4,649 | 394 | 8.5% | 30.1 | 6 | 3.8e-9 | 0.14 | 49.7 | 1.78e-5 | 67.7 | 329.8 |
| 384 mm | 5,584 | 396 | 7.1% | 37.9 | 6 | 6.1e-9 | 0.16 | — | — | 81.2 | — |
| 448 mm | 6,485 | 396 | 6.1% | 42.9 | 6 | 9.6e-9 | 0.18 | — | — | 94.0 | — |
| 512 mm | 7,386 | 397 | 5.4% | 50.4 | 7 | 7.8e-10 | 0.25 | — | — | 106.8 | — |
| 640 mm | 9,239 | 398 | 4.3% | 70.2 | 7 | 2.3e-9 | 0.31 | — | — | 135.9 | — |
| 768 mm | 11,041 | 398 | 3.6% | 78.6 | 7 | 4.8e-9 | 0.65 | — | — | 161.6 | — |
| 896 mm | 12,894 | 399 | 3.1% | 90.1 | 7 | 9.0e-9 | 0.41 | — | — | 188.2 | — |

(Dense reference through N = 4,649 — the last rung under R17's own ceiling; past it dense cannot be
asked to run at all, per the hard-refusal note above.) **Flat, all the way to N = 12,894**: near/row
392 → 399 (1.8% drift over 3.5× N), iterations 6 → 7, `|Δcurrent|` stays at 1-2e-5 where a dense
reference exists, accelerator memory scales linearly (53 → 188 MB, i.e. 3.53× for 3.46× N). This is
AIM's premise holding — a "stable line" in the brief's own words.

**A1b — refine the RESOLUTION at a FIXED 64 mm footprint instead (cells/λ swept).** The brief's own
trap check: does the ladder's story change when the mesh's CHARACTER changes (finer cells throughout,
including inside the edge-grading fan) rather than only its extent?

| cells/λ | N | near/row | near % | build s | iters | resid | solve s | dense s | \|Δcurrent\| | MB | dense MB |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 20 | 994 | 365 | 36.7% | 7.8 | 5 | 1.2e-10 | 0.03 | 3.1 | 5.52e-6 | 13.3 | 15.1 |
| 40 | 1,895 | 379 | 20.0% | 13.5 | 5 | 5.7e-9 | 0.05 | 6.5 | 1.33e-4 | 26.3 | 54.8 |
| 60 | 3,454 | 465 | 13.5% | 24.0 | 8 | 5.4e-9 | 0.16 | 23.0 | 1.98e-4 | 59.4 | 182.0 |
| 80 | 5,437 | 539 | 9.9% | 41.4 | 21 | 1.0e-8 | 0.64 | — | — | 105.9 | — |
| 100 | 7,873 | 597 | 7.6% | 57.8 | 143 | 9.8e-9 | 8.26 | — | — | 170.5 | — |
| 120 | 10,708 | 630 | 5.9% | 80.8 | 372 | 9.9e-9 | 36.05 | — | — | 242.1 | — |
| 140 | 13,967 | 656 | 4.7% | 108.3 | 400 | **9.1e-5** | 52.00 | — | — | 326.7 | — |

**This one BREAKS.** Iterations hold at 5-8 through N = 3,454 (matching A1a), then climb 21 → 143 → 372
as cells/λ goes 80 → 100 → 120 — still converging inside the 1e-8 tolerance, just much slower (solve
time 0.64 s → 36.05 s) — and at cells/λ = 140 (N = 13,967) GMRES hits its 400-iteration cap at a
residual of 9.1e-5, **not converged**. In production this throws (`PlanarAimOperator.Solve`'s own
non-convergence exception) rather than returning the half-converged current shown here; the table
catches the exception and reports what it measured for the record.

**Near/row is NOT flat here either** — 365 → 656 (1.8×) over a 14× N range, against A1a's 392 → 399
(1.02×) over 3.5×. Refining resolution at fixed extent shrinks the largest basis support that
`GridSpacingFactor`/`NearRadiusFactor` are sized from, so the stencil and the near set both grow
relative to the geometry in a way that growing the geometry at fixed resolution does not. **A single
accelerated N is therefore not a perfectly stable line in general** — but the failure mode this ladder
finds is a mesh refined 7× past the shipping default (cells/λ = 20) on artwork already adequately
resolved, which is not the shape of the real complaint that opened this brief (§0's own table: N = 7,749
identically at cells/λ 5, 10 and 20 — the owner's large N came from the taper's WIDTH RATIO, i.e. from
A1a's construction, not from cranking resolution).

### A2 — accuracy at ceiling-adjacent scale

`AimCeilingTests.A2_DeEmbeddedS_AtTheTopOfTheReachableLadder`: 30 mm FR-4 line, cells/λ = 60, N = 3,726
(near the DENSE ceiling — the largest a full de-embedded dense-vs-accelerated comparison can still be
run at), 6 calibration standards, 3 frequencies:

| f | worst \|ΔS\| de-embedded | worst \|ΔS\| raw |
|---|---|---|
| 2 GHz | 2.13e-5 | 8.75e-8 |
| 5 GHz | 9.38e-6 | 2.03e-6 |
| 10 GHz | 1.42e-5 | 7.72e-6 |

Worst over the band 2.13e-5, against L8d's own measured de-embedding residual of 6.0e-3 — three orders
of margin, consistent with R-emp-16 gate 2's own N = 94 result (8.07e-4 there, a different fixture and
frequency). The accelerator is not the error budget at this scale either.

### A3 — the CONFORMAL (cut) single-level mesh, which M5 never measured

`AimCeilingTests.A3_ConformalCutMesh_AimAgainstDense`: a straight-flanked taper (1.0 → 6.71 mm),
`BoundaryCells.Conformal`, so every cell the flank crosses is genuinely cut rather than staircased:

| L | N | cut cells | near/row | near % | build s | iters | resid | solve s | dense s | \|Δcurrent\| |
|---|---|---|---|---|---|---|---|---|---|---|
| 16 mm | 1,538 | 134 | 935 | 60.8% | 15.0 | 4 | 2.8e-9 | 0.06 | 5.8 | 5.50e-5 |
| 32 mm | 1,577 | 136 | 579 | 36.7% | 12.7 | 5 | 2.0e-9 | 0.06 | 5.7 | 1.55e-5 |
| 64 mm | 1,690 | 144 | 644 | 38.1% | 20.1 | 5 | 3.5e-10 | 0.06 | 8.7 | 1.65e-6 |
| 96 mm | 2,232 | 186 | 696 | 31.2% | 33.3 | 5 | 6.7e-10 | 0.09 | 13.9 | 3.18e-6 |

**No penalty from cut cells** — iterations and `|Δcurrent|` are in the same range as the staircased A1
tables at comparable N. `PlanarFill.WeightNodes` is what the projection reads for a basis's moments
(cut cells, strips and all — see `PlanarAim.cs`'s own file header), so this is confirming the projection
is structurally right, not discovering it. The accelerated ceiling therefore does not depend on
`PlanarBoundaryCells`.

### The decision: (b), a second ceiling, 12,000, single-level only

§1's three candidates, decided on A1/A2/A3's evidence: **(b)**, not (a) (the reported file's own class
of geometry — width-ratio-driven N growth — is exactly A1a's stable-line construction, and refusing it
unconditionally would be leaving a real, safe capability unused) and not (c) (A1a's own construction IS
a stable line — flat near/row, linear memory, flat iterations — so the honest memory-budget formulation
(c) asks for is not needed to make THIS decision safely; A1b's breakage is handled by GMRES's own
non-convergence throw rather than by a per-run byte budget, which is the cheaper and already-built
backstop).

**`SurfaceMesher.AcceleratedUnknownCeiling = 12_000`** — the top of A1a's healthy rung, with margin
under A1b's failing one (13,967), covering the owner's own N = 7,749 with 1.55× headroom.

**All three call sites move together, as A4 required:**
- `SurfaceMesher.Mesh`'s own verdict — gained an `accelerated` parameter (default `false`, so every
  existing caller is unaffected); ignored whenever `problem.RequiresGeneralKernel`, because the
  accelerator refuses a multi-level/via mesh by name regardless.
- `PlanarSolveContext`'s constructor — was an unconditional `PlanarSystem.GuardCeiling` (the DENSE
  ceiling, wrong question for a solver that holds no N×N anything); now `SurfaceMesher.GuardCeiling(n,
  accelerated)`, accelerated exactly when `Settings.Aim is not null && Levels is null`.
- `PlanarFill.cs`'s own `GuardCeiling` (dense-only, `BuildCores`/`Fill`/`FillMultiLevel`) — **unchanged
  on purpose**. It is never on the accelerated path (`BuildGeometryOnlyCores` calls no guard at all,
  by design — M5's whole cost claim rests on that path staying O(N)), so the dense ceiling it enforces
  is correctly the dense one.
- `PlanarSystem.GuardCeiling` — **unchanged on purpose**, for the same reason: `PlanarSystem` IS the
  dense path, so 5,000 is always the right question to ask it.

**The refusal text is now conditional on the solver, and on whether turning it on would help:**
`SurfaceMesher.BuildRefusal` used to end unconditionally with "the accelerated solve reduces the
memory one costs but does not move this ceiling" — true when M5 shipped, false the moment this landed.
Three cases now: (1) dense-only and turning the accelerator on WOULD let this mesh run — "turn on the
accelerated solve" is named FIRST, ahead of every mesh-setting remedy, because it is the only one that
touches nothing about the drawn geometry; (2) dense-only and the accelerator would NOT help either
(N past 12,000 too) — the old sentence's shape survives, restated against both ceilings so it stays
true; (3) the accelerator is ALREADY what was asked for and this mesh is STILL past 12,000 — states
that ceiling by name, notes the accelerator's own working set stays under 200 MB even there (so this
is not a memory limit either), and points at GMRES's own non-convergence throw as what actually catches
an over-refined mesh past this point.

### The limitation this surfaced: de-embedding's calibration step is NOT accelerated

Found running gate 5 (the owner's own taper, actually solved, not merely meshed) end to end through
`PlanarKernel.Solve`: it threw from `PlanarDeembed.CapacitancePerMetre` → `StaticCapacitance` →
`PlanarFill.BuildCores`, twenty real minutes into a dense fill nobody asked for. **This is a genuinely
separate, always-dense computation** — an m×m system over CELLS (not the N×N basis system AIM
accelerates) that recovers a calibration standard's static (ω → 0) capacitance for D7's reference
impedance — and `PlanarFill.BuildCores`'s own `GuardCeiling` refuses it exactly like any other dense
fill. A calibration standard reproduces the DUT's own transverse gridlines VERBATIM (D4, §10's own
finding), so a WIDE-port DUT's standard can be as large as the DUT itself or larger; on the owner's own
taper it meshed at N = 6,466, past R17's dense ceiling on its own.

**Deliberately not fixed here** — `PlanarDeembed.cs` is the file this brief's own §4 names as
out-of-scope ("no de-embedding algebra"), and accelerating a second, structurally different m×m system
is exactly the kind of widening §4 exists to prevent. **So a wide-port DE-EMBEDDED run can still refuse
past R17's dense ceiling even when the DUT's own accelerated Z-matrix would solve fine** — recorded
here and in §4's own table so it is not rediscovered as a new bug. `EmCeilingRefusalTests
.TheOwnersReportedTaper_TheDutsOwnZMatrix_ACTUALLYSOLVES_WithTheAcceleratedSolveOn` gates the DUT's own
raw (non-de-embedded) solve instead, which is what this brief actually moved, and is the fast (real
solve, seconds) member of the pair; the full de-embedded attempt is not kept as a test at 20 real
minutes for a result already known.

### Gate 5 — the owner's own file, re-run

`EmCeilingRefusalTests.TheOwnersReportedTaper_NowFitsUnderTheAcceleratedCeiling` (routine) and its
`…ACTUALLYSOLVES…` pair (`Category=Benchmark`) reconstruct §0's own reported taper (Z1 = 6.92 Ω,
Z2 = 100 Ω, L = 28.575 mm, conformal, cells/λ = 5, mesh frequency 500 MHz) from the PCell's own
defaults — landing at N = 6,581 rather than the owner's exact 7,749 (the PCell's `GammaMax` and similar
were not stated in the report; the RATIO, not the exact count, was always the point, and 6,581 is past
the dense ceiling and under the accelerated one either way). Densely refused, unchanged. With the
accelerated solve on: meshes, and the DUT's own Z-matrix actually solves — `PlanarSolveContext
.RawScatteringAt` returns a real 2×2 raw S with a positive GMRES iteration count. **The sentence this
user gets now**: turn on Accelerated solve (Solver options); it is named first in the refusal, and it
is the one thing that actually works for this class of part.


---

## P1 — honest memory accounting (brief-em-p1-honest-memory-accounting.md, 2026-08-29)

**This phase measured and re-worded. It changed no arithmetic in any kernel** — no fill, no
factorisation, no Green's function, no s-parameter moved by a bit. What changed is what the code
SAYS about its own size, in the three ceiling refusals and in `PlanarAimReport`.

The three `GuardCeiling` bodies (`SurfaceMesher`, `PlanarSystem`, `PlanarFill`) all quoted `16·N²`
— "381 MB at the ceiling". That number is exactly right about the dense matrix and silent about
everything live beside it. §7 already carried "381 MB quoted against ~607 MB real — owner's call";
**607 was itself an underestimate, and this phase replaces both numbers with a measured one.**

### Table 1 — one DENSE frequency point, COUNTED

FR-4 hero cross-section at 10 GHz, shipping mesh (cells/λ = 20, edge grading on, staircase), lines
of 20 / 80 / 200 mm. Every term is an array the code allocates, at its element size — nothing here
is a profile or a fit. `PlanarMemoryAccountingTests.P1_1` (`Category=Benchmark`, 2 m 3 s).

| N | cells | cores | matrix | L + U | P (transient) | **quoted (16·N²)** | **RESIDENT PEAK** | ratio |
|---|---|---|---|---|---|---|---|---|
| 552 | 297 | 2.4 MB | 4.6 MB | 9.3 MB | 1.3 MB | 4.6 MB | **16.4 MB** | **3.52×** |
| 1,980 | 1,053 | 30.9 MB | 59.8 MB | 119.6 MB | 16.9 MB | 59.8 MB | **210.4 MB** | **3.52×** |
| 4,836 | 2,565 | 184.1 MB | 356.9 MB | 713.7 MB | 100.4 MB | 356.9 MB | **1,254.7 MB** | **3.52×** |

At the ceiling itself (N = 5,000) the same arithmetic gives **1,338 MB**, which is the number every
refusal now quotes in place of 381.

**The ratio is 3.52× and it is FLAT**, which is what says it is structural rather than a size
effect: every term is O(N²) with a fixed coefficient. The split is

- **the matrix, 16·N²** — kept alive by `PlanarSystem.Matrix` for the whole life of the point;
- **the factors, 32·N²** — `NumFlat.LuDecompositionComplex` holds `L` and `U` as two SEPARATE full
  `Mat<Complex>` of stride n (verified by reflection AND by measurement). **This is not a packed
  in-place LU**, and it is the single largest term in the table;
- **the cached geometric cores, ≈ 0.52 × 16·N²** — L8c's own "+51% on top of the matrix", which
  `PlanarSystem.CoreBytes(n, cellCount)` now reconstructs from N and the cell count to within 1%
  (measured 0.988-1.000× against `PlanarFillCores.CoreBytes` on real meshes, gated routinely by
  `P1_5`).

**The transient m×m `P` is real and is NOT the peak.** `PlanarFill.Fill` builds the scalar-potential
matrix over CELLS, uses it, and drops it before anything is factored; at m ≈ N/1.95 it is a quarter
of a matrix against the factorisation's two, so the fill's own high-water mark is below the
factorisation's. Measured at N = 552: counted 1.3 MB, measured 1.4 MB.

### Table 2 — the same three points, MEASURED

Cumulative live heap from one baseline (`GC.GetTotalMemory(true)` with LOH compaction requested),
with `GC.GetTotalAllocatedBytes` beside it.

| N | live after cores | live after fill | live after LU | allocated by fill | allocated by LU | counted peak | live / counted |
|---|---|---|---|---|---|---|---|
| 552 | 3.1 MB | 7.6 MB | 25.0 MB | 8.3 MB | 17.3 MB | 16.4 MB | 1.52 |
| 1,980 | 31.0 MB | 83.8 MB | 250.1 MB | 83.0 MB | 183.7 MB | 210.4 MB | 1.19 |
| 4,836 | 120.6 MB | 386.6 MB | 1,492.8 MB | 463.8 MB | 1,225.9 MB | 1,254.7 MB | 1.19 |

Process working set at the end of the run: **1,433.6 MB**. (`Process.PeakWorkingSet64` reads 0 on
macOS — the platform does not track it — so `WorkingSet64` is what this row can say.)

**So the machine sees about 1.19× the counted peak, i.e. ≈ 4.2× the 16·N² that was quoted.** The
extra 19% is the factorisation's own released scratch, still committed on the large object heap: the
LU ALLOCATES ~3.64 × 16·N² and RETAINS 2 × 16·N². Only the retained part belongs in a refusal — a
refusal that quoted the transient would be quoting the GC's collection policy — but a user watching
a memory meter sees both, which is why both are recorded here.

**A measurement trap this phase paid for, and the reason Table 2 is cumulative rather than
per-phase.** The LOH does not compact by default, so a released n×n matrix leaves committed space
that the next allocation lands in. A per-phase live DELTA therefore reads the matrix LOW (it lands
in the transient `P`'s grave) and the factorisation HIGH, and a ladder measuring several N in one
process reads the second rung's matrix at half its size. The first version of this table showed
"cores −4.1 MB" at N = 2,932. **Cumulative live from one baseline, plus the allocation counter, are
both exact; a per-phase delta of live bytes is not.** The same trap explains the brief's own opening
scratch measurement ("a 3,000×3,000 matrix is 137 MB and `.Lu()` added 530 MB", i.e. 3.87×): 530 MB
is the live figure with the scratch still committed. Retained is 2×; the reading was not wrong, it
was measuring a different thing than it named.

### The AIM report — what it had omitted

`PlanarAimReport.ApproximateBytes` counted the accelerator's own arrays and stopped there. Three
things were missing, and one of them was large:

1. **The sparse LU's own fill-in.** The preconditioner is a `CSparse.SparseLU` of the near matrix,
   and its factors are several times the matrix they come from.
2. **The name `PreconditionerNonZeros` was never the fill-in.** It is `csc.NonZerosCount` — the near
   matrix's own non-zero count, which (every near pair being stored exactly once) is the near ENTRY
   count again, under a name that reads like the factor's. The brief expected this field to be the
   fill-in "reported but never added"; it is not, and the honest number needed a new field.
   `FactorNonZeros` is `SparseLU.NonZerosCount` — L and U together.
3. **`_nearExact`**, held for the operator's whole life to serve one diagnostic.

### Table 3 — one ACCELERATED frequency point

FR-4 hero cross-section at 6 GHz, shipping mesh, at M5's own top rung and at the accelerated
ceiling. `PlanarMemoryAccountingTests.P1_2` (`Category=Benchmark`).

| line | N | near/row | stencils | grid + FFT | near CSR | sparse LU | **REPORTED** | measured | ratio | dense resident at the same N |
|---|---|---|---|---|---|---|---|---|---|---|
| 256 mm | 3,731 | 392 | 1.8 MB | 1.3 MB | 27.9 MB | 29.0 MB | **60.1 MB** | 63.6 MB | 0.94 | 746.9 MB |
| 832 mm | 11,959 | 399 | 5.8 MB | 5.3 MB | 90.9 MB | 94.1 MB | **196.2 MB** | 205.0 MB | 0.96 | 7,671.7 MB |

**The sparse LU is very nearly half of it**, and it had never been counted. The near field itself is
as flat in N as M5 said it was (392 → 399 entries per row over a 3.2× N range); what grows with it
is the factorisation of that near field.

| N | near nnz (`PreconditionerNonZeros`) | L+U nnz (`FactorNonZeros`) | old `ApproximateBytes` | honest, `_nearExact` KEPT | **honest, released — what ships** |
|---|---|---|---|---|---|
| 3,731 | 1,461,553 | 1,518,133 | 53.3 MB | 82.4 MB | **60.1 MB** |
| 11,959 | 4,765,871 | 4,927,957 | 174.7 MB | 268.9 MB | **196.2 MB** |

**Two things fall out of the last two columns.**

*First*, `CLAUDE.md` §8's "the accelerator's own working set stays under 200 MB even at that
ceiling", and the identical sentence in `SurfaceMesher`'s accelerated refusal, **was false as
shipped**: honestly counted with the exact near entries still held, the operator at N = 11,959 holds
268.9 MB. Releasing them after the factorisation brings it to 196.2 MB and the sentence is true
again — *because of* this brief rather than in spite of it, and now tight rather than comfortable.

*Second*, the old number was low by coincidence rather than by construction: it over-counted the
near set (36 B/entry, the exact and the correction and the index) by almost exactly what it
under-counted the factorisation. Two errors of opposite sign is not an accounting.

**AIM still wins the comparison it was built to win, and against the honest dense number it wins by
more** — the dense side grew 3.52× at P1 and the accelerated side grew 13%.

*A measurement note, because the first run of this table was wrong.* The two rungs must be measured
with EVERY earlier operator still reachable. Collect the 256 mm operator before building the 832 mm
one and the large object heap hands its 60 MB straight to the next build, which then measures ~40%
below what it holds (141.7 MB against a real 205.0 MB). Same trap as Table 2, one level up.

### The gate had to change tier, and the reason is the instrument

The brief asks for "the reported bytes within 20% of the measured resident delta" as a routine test.
**`GC.GetTotalMemory` is process-wide and xUnit runs test classes concurrently in one process**, so
another class's collection lands inside the measurement window: the first version of that test read
**0.925 measured alone and −0.245 under full-suite load.** That is not a flake to re-run — it is the
wrong instrument for a parallel suite.

So the resident comparison is asserted inside the `Category=Benchmark` measurement above, which
carries a "measure this one alone" note (the same precedent §10 already sets for the L8d cost
table), and the ROUTINE gate counts the same quantity a different way: it **walks the operator's own
object graph and adds up every array it holds**. That is load-immune, and it is independent of the
report's arithmetic in the way that matters — it counts the arrays that EXIST rather than
re-deriving them from the report's own fields, which is exactly the check that would have caught the
omission P1 fixed. **It agrees to 0.3%**: 9.7 MB reported against 9.7 MB walked at N = 552, where
the same count with the sparse LU removed is 4.1 MB, i.e. 0.425 of what is there.

### What was changed in code

- **`PlanarSystem.CoreBytes` / `FactorBytes` / `ResidentBytes` / `ResidentPhrase`** — new, and the
  ONE place the three refusals now get their megabytes from, so they cannot drift apart again.
  `ResidentBytes(n, cellCount)` takes the cell count where a caller has one and derives it from
  N/1.95 where it does not (`PlanarSystem.Wrap` holds a bare matrix). The cores term states two
  assumptions and both make it a FLOOR: the shipped extraction order (`Constant` — `Linear` adds a
  fourth radial array per family) and an even x̂/ŷ split (which minimises the vector term).
- **The three refusals** — `SurfaceMesher.BuildRefusal`, `SurfaceMesher.GuardCeiling`,
  `PlanarSystem.GuardCeiling`, `PlanarFill.GuardCeiling`, plus the WARN note — now say
  "*N MB resident at the peak of one frequency point (matrix + factors + cached cores), against
  1,338 MB at the ceiling*". The megabytes stay where the 2026-08-14 owner report put them (after
  the unknown count, never leading), and they now say what they ARE, because a bare figure beside a
  ceiling is what read as a RAM limit the first time.
- **`PlanarAimReport.ApproximateBytes` → `ResidentBytes`**, renamed because it no longer
  approximates: every term is an allocation at its element size. It gained the sparse LU's factors,
  the CSR row pointer, and the AMD permutation; `PeakBuildBytes` adds the transient CSC copy the
  factorisation is built from. `FactorNonZeros` and `NearExactRetained` are new report fields.
- **`_nearExact` is released once `FactorNear` has run**, unless
  `PlanarAimSettings.KeepNearExact` (new, default false) asks for it. The accelerated PRODUCT reads
  the CORRECTION (exact − AIM); nothing in a solve reads the exact entries. `NearExactAt` THROWS
  when they were not kept rather than returning zero — a silent zero there is indistinguishable
  from "not near", which is the question the caller is asking.

  *The brief suggested reading them back out of CSparse's CSC instead. That was measured against and
  not taken: the CSC's row index costs 4 B per entry MORE than the array it would replace, for the
  same numbers, so keeping the CSC to serve a diagnostic is strictly worse than keeping the array.*
- **`tests/Engine.Tests/Mom/PlanarMemoryAccountingTests.cs`** — new. Two `Category=Benchmark`
  measurements (the three tables above, plus the brief's own resident-delta assertion) and four
  routine COUNTER gates: the report against every array the operator holds and not vacuously so; the
  exact entries released by default, with the solved current vector bit-identical either way;
  `CoreBytes` reproducing `PlanarFillCores.CoreBytes`; and all three refusals quoting one sentence.
- `EmCeilingRefusalTests.TheRefusal_SaysTheCeilingIsTheKERNELS_NotTheMACHINES` — its expected
  sentence updated and still asserted, now pinned against `PlanarSystem.ResidentPhrase` itself so the
  gate cannot drift away from the code. `docs/design/mom-engine.md` §10.7, `CLAUDE.md` §7/§8 and
  `docs/user/src/reference/mom-engine.md`'s own budget table corrected in place.
- One small refactor that removed a throw rather than allow-listing it:
  `FactorNear` now takes the exact entries as an argument instead of reading the (now nullable)
  field, so "this runs once, while they are still live" is a signature rather than a sentence.
  `tests/Firewall.Tests`' user-facing-text allow-list gains exactly one line, for `NearExactAt`.

### What P1 deliberately did NOT do

`UnknownCeiling` and `AcceleratedUnknownCeiling` are untouched — that decision is P7's (dense) and
P8's (accelerated), once the memory is what it will be. `PlanarSystem`'s factorisation is untouched
— P7. **The 3.52× is a fact about the code as it stands today, and P2 and P7 exist to move it.**


---

## P2 — four mechanical memory wins on the dense path (brief-em-p2-cheap-memory-wins.md, 2026-08-29)

**P1 measured; P2 removes.** Four allocations came out. None of them changes what any entry of any
matrix *is*: three are bit-identical, measured rather than argued, and the fourth (M2) replaces one
rounding with a different one and is pinned at ≤ 1 ulp.

| | what came out | where it shows |
|---|---|---|
| **M1** | the `VXArea`/`VYArea` packed triangles — two O(N²) arrays of the outer product of an O(N) vector | **24% off the cached cores at every N**, i.e. 3.5% off a whole dense frequency point |
| **M2** | `StaticCapacitance`'s copy of `P`, built only to divide every entry by ε₀ | one m×m off the calibration standards' static solve |
| **M3** | `StaticCapacitance` re-coring a mesh whose `PlanarSolveContext` already held its cores | 7 → 5 `BuildCores` calls on a 4-standard de-embedded sweep |
| **M4** | `PlanarPortCalibrator` coring every standard of its band in its constructor | 2 of 4 standards cored on a wide band stepped narrowly; **0 of 4** at construction |

### M1 — an O(N²) array of an outer product

`BuildDirectionCores` stored `cArea[p] = mMom * nMom` for every same-direction basis pair: the
extracted CONSTANT term's vector core is `∫w_m dS · ∫w_n dS`, which factors. The other two cores in
that family — `∫∫ w w /R` and `∫∫ w w ln r` — genuinely do not, which is why they are cached and
this one need never have been. The per-basis moments are now one `double[N]` on `PlanarFillCores`
(`VMoment`) and the product is formed at the point of use, one multiply per entry.

**Bit-identity is not an argument here, it is a hash.** A git worktree at the commit before P2 ran
the same fixture and printed the SHA-256 of the whole assembled `Z`:

```
N = 552, cells = 297, FR-4 hero at 10 GHz, shipping mesh
before  04F078DD4765A83EC4D068F20691BCD2A7CCF86943FCC680D40E620090B84D99
after   04F078DD4765A83EC4D068F20691BCD2A7CCF86943FCC680D40E620090B84D99
```

The digest is a literal in `PlanarP2MemoryWinsTests.P2_1`, which is the only form a bit-identity
claim can take — no single tree holds both builds, and a test that recomputed its expected value
from the code under test would assert nothing.

**What it saves.** Counted from the arrays (`P2_7`), on P1's own three rungs:

| N | cells | cores before | cores after | resident before | resident after | off a whole point |
|---|---|---|---|---|---|---|
| 552 | 297 | 2.43 MB | **1.85 MB** | 16.38 MB | **15.80 MB** | 3.5% |
| 1,980 | 1,053 | 30.92 MB | **23.45 MB** | 210.39 MB | **202.92 MB** | 3.6% |
| 4,836 | 2,565 | 184.09 MB | **139.50 MB** | 1,254.68 MB | **1,210.09 MB** | 3.6% |

The fraction is fixed at ~24% of the cores because the term removed was one of three O(N²) vector
triangles; the O(N) vector that replaced it is 15.5 kB at N = 1,980 against the 7.51 MB it replaced.
**P1's flat 3.52× resident-to-matrix ratio is now 3.39×**, and the three ceiling refusals quote
**1,290 MB** at N = 5,000 rather than 1,338.

*The geometry-only core the AIM accelerator builds is no longer literally empty — it carries the
same `VMoment` vector, 8·N bytes — and `PlanarEntryFill` now reads that instead of deriving its own
copy of the identical array. `AimAcceleratorTests.T1b`'s "`CoreBytes == 0`" became "`== 8·N`", which
is the same claim stated exactly: nothing QUADRATIC.*

### M2 — the copy of P that existed to divide by ε₀

`StaticCapacitance` solved `(P/ε₀) q = 1` by building a second m×m complex matrix and dividing every
entry into it. It now solves `P q = ε₀·1`. Same system; one fewer m×m, and one fewer rounding —
`ε₀·1` is exact, so the matrix that gets factored is now the fill's own `P` bit for bit.

**This is the one milestone that is not bit-identical end to end, and the size of the move is
measured rather than bounded.** On `P2_3`'s own 32-cell standard the two forms agree *exactly*
(`1.4951676979500528E-12` F both ways). On the swept fixture the published S moves in its last bit
or two — isolated by running the pre-P2 worktree twice, once with M2's six lines alone:

```
5-point de-embedded sweep, N = 94, S21 at 20 GHz
pre-P2                 0.6208172551602424 − 1.14559600691411 j
pre-P2 + M2 only       0.6208172551602426 − 1.1455960069141098 j
P2 (all four)          0.6208172551602426 − 1.1455960069141098 j     ← identical to M2-only
```

**So M1, M3 and M4 together move not one bit of a published s-parameter**, and M2 is the only
arithmetic P2 changed. Both digests are literals in `P2_6`.

*Consequence for the refusal text.* `GuardCapacitanceCeiling` said "the two m×m complex matrices this
solve holds at once (the potential-coefficient matrix and its own copy)". The copy is gone, and the
sentence was in any case an undercount — P1 established that NumFlat's LU holds `L` and `U` as two
further full matrices. It now says **three** (P, L, U) and quotes 3·16·m² rather than 2·16·m².

### M3 — a mesh cored twice

`PlanarDeembed.CapacitancePerMetre` called `PlanarFill.BuildCores` on the two extreme standards'
meshes — meshes whose `PlanarSolveContext` was already holding cores built from the same mesh and
the same `PlanarFillSettings`. Both now take the context's own cores; a caller with none, or with a
geometry-only core (the accelerator's contexts carry those), falls back to building them exactly as
before, and the mesh identity is checked rather than assumed.

**R-prt-11's counter was blind to this, and that is the interesting part.**
`PlanarSolveResult.CoreFillCount` is derived from the number of standard MESHES (`1 + standards`),
not from the number of builds — so it read 5 both before and after, while the code was doing 7
builds. `PlanarCoreBuildCounter` is new and counts the builds themselves: an INSTANCE threaded
through `PlanarFillSettings` beside `Budget`, never a static, for the reason `PlanarSweepResult`'s
own note already gives (a static counter is flaky the moment two fill tests run concurrently, which
xUnit does by default).

```
4 standards + DUT, 5-point de-embedded sweep, 1-20 GHz
CoreFillCount   5  →  5     (unchanged, as the brief requires)
BuildCores      7  →  5     (max 1 per mesh, over 5 distinct meshes)
```

### M4 — a standard the sweep never selects is never cored

`PlanarPortCalibrator`'s constructor built a `PlanarSolveContext` per standard, and each of those
built its cores. `NeededAt` fills exactly two per frequency: the short line and the ONE long line
the β prediction selects. `PlanarSolveContext.Cores` is now a thread-safe `Lazy`; the R17 ceiling
check stays eager, because a refusal has to happen at setup and is a decision about N.

**And here is the honest finding, which is smaller than the brief assumed.** Through
`PlanarSolve.Run` the calibration band IS the sweep's own frequency range, so over a full sweep
every separation gets selected at some point and M4 saves *nothing*:

| band, stepping | standards | cored, before | cored, after |
|---|---|---|---|
| 1–20 GHz, 5 pts | 4 | 4 | **4** |
| 1–20 GHz, 21 pts | 4 | 4 | **4** |
| 9–11 GHz, 5 pts | 2 | 2 | **2** |

The saving is real only when the band is wider than the frequencies actually stepped — the shape a
single-frequency check, an interrupted or cancelled run, and a direct `PlanarPortCalibrator` caller
all have:

```
calibrator built for 1-20 GHz, stepped at 9 / 10 / 11 GHz
standards N = [52, 143, 87, 66]     cores 19.6 / 137.9 / 52.5 / 30.9 kB
cored: 2 of 4 — 50.5 kB of 240.8 kB, i.e. 79% of the standards' cores never built
```

At the shipping mesh those four standards are N = 297 / 739 / 467 / 365 and their cores are
0.5 / 3.3 / 1.3 / 0.8 MB, so the same case leaves 4.6 of 6.0 MB unbuilt. **At construction the
number cored is now 0 of 4 rather than 4 of 4**, which is the part that is unconditional.

**Interaction with M3, and it is a real cost.** `CapacitancePerMetre` uses `Standards[0]` and
`Standards[^1]`, so handing it the contexts' cores BUILDS the longest standard's whether or not any
frequency selected it. That is correct — D7 differences the two EXTREME lengths, not the one this
frequency solved — and it is why the table above shows 2 cored rather than 1. P11 changes that
solve; until then the longest standard is cored on every de-embedded run.

### What was changed in code

- **`PlanarFillCores.VXArea`/`VYArea` → `VMoment`** (one `double[N]`, per BASIS rather than per
  direction-position, because both readers already have a basis index). `BuildDirectionCores` returns
  three arrays instead of four; the moment derivation moved to a shared `PlanarFill.BasisMoments`
  that both core builders call. `CoreBytes` counts the new vector — it is O(N) and P1's whole point
  is that this number is what the object HOLDS.
- **`PlanarSystem.CoreBytes`** — `8·(2·scalarPairs + 3·vectorPairs)` → `8·(2·scalarPairs +
  2·vectorPairs + n)`. Its own doc comment's two stated assumptions are unchanged and still make it
  a floor.
- **`PlanarDeembed.StaticCapacitance`** — solves `P q = ε₀·1`; takes an optional `PlanarFillCores`.
  **`CapacitancePerMetre`** takes two. `GuardCapacitanceCeiling` says three m×m, not two.
- **`PlanarCoreBuildCounter`** — new; `PlanarFillSettings.CoreBuilds` carries one, outside the
  positional list beside `Budget` and `Aim`.
- **`PlanarSolveContext.Cores` is a `Lazy`**, plus `CoresBuilt` and `CoreBuildMs`;
  `PlanarPortCalibrator` gained `CoredMeshCount` and `CoreBuildMs`. `PlanarSolve.Run` now SUMS the
  run's core time from the contexts that actually built one, since it can no longer be measured
  around a constructor.
- **`tests/Engine.Tests/Mom/PlanarP2MemoryWinsTests.cs`** — new, 7 routine tests: the two pinned
  digests, the core-byte composition, the capacitance value and its allocation, the two counters, and
  the resident table above.
- `AimAcceleratorTests.T1b` and `EmDeembedCeilingTests.C2` updated for the two claims that genuinely
  changed (8·N bytes on a geometry-only core; three m×m in the capacitance refusal).
- `docs/design/mom-engine.md` §10.7, `src/Engine/Mom/CLAUDE.md` §7 and `PlanarSystem.ResidentBytes`'
  own comment carry the new 3.39× / 1,290 MB in place.

### What P2 deliberately did NOT do

The brief's fifth candidate — **storing `P` packed** (it halves a transient 4N² bytes) — was not
taken. It touches every `p[a, b]` reader in three fills and is a milestone with its own bit-identity
gate, not a few lines. `PlanarAim.cs`, the LU and every quadrature are untouched, as instructed.

## P3 — the multi-level fill's scaling, measured, and what the hoist actually bought (brief-em-p3-multilevel-fill-scalability.md, 2026-08-29)

**The brief's premise was that `FillMultiLevel`'s shape — a kernel-set lookup under a lock and a
fresh `PlanarKernelTerms` per pair, three more locked caches per entry, an array per horizontal
entry — would scale "far worse" than the single-level fill's 5.4× on ten cores. Milestone 1 measured
it before anything was changed, and the premise is false:** the multi-level fill already scaled
5.5–5.9× at cap 10, i.e. exactly like `Fill`. The hoist was done anyway (it is the right shape, it
is bit-identical, and it removes ~8× the allocation per fill), and it bought something the brief did
not name: **19–24% off every via-bearing fill at every cap**, from an arm that is not O(N²) at all.

All timings are one fill (the second on a warm kernel set — fits and tables already built), alone on
the machine, on a 10-core box with **4 performance + 6 efficiency cores** (which is the whole story
of the cap-10 column; see below). Caps 1/2/4/10 as the brief asked.

### Table 1 — the multi-level fill BEFORE P3 (pre-P3 worktree: P1 and P2 applied, P3 not)

| fixture | N (vertical) | cap 1 | cap 2 | cap 4 | cap 10 |
|---|---|---|---|---|---|
| two-level MMIC line + via, 10 GHz | 514 (2) | 63.35 s | 32.02 s · 1.98× · 99% | 17.74 s · 3.57× · 89% | 10.86 s · 5.83× · 58% |
| two-level MMIC line, no via | 424 (0) | 22.14 s | 11.56 s · 1.92× · 96% | 5.94 s · 3.73× · 93% | 3.77 s · 5.88× · 59% |
| FR-4 hero on two levels, 3 vias | 1,175 (3) | 64.36 s | 32.82 s · 1.96× · 98% | 16.87 s · 3.82× · 95% | 11.61 s · 5.54× · 55% |

### Table 2 — the same three fills AFTER P3

| fixture | N (vertical) | cap 1 | cap 2 | cap 4 | cap 10 |
|---|---|---|---|---|---|
| two-level MMIC line + via, 10 GHz | 514 (2) | **51.16 s** | 26.49 s · 1.93× · 97% | 14.24 s · 3.59× · 90% | **9.12 s** · 5.61× · 56% |
| two-level MMIC line, no via | 424 (0) | 22.07 s | 11.57 s · 1.91× · 95% | 5.92 s · 3.73× · 93% | 4.22 s · 5.23× · 52% |
| FR-4 hero on two levels, 3 vias | 1,175 (3) | **54.31 s** | 27.64 s · 1.96× · 98% | 14.23 s · 3.82× · 95% | **9.03 s** · 6.01× · 60% |

Reading the two together:

- **The no-via fill did not move** (22.14 → 22.07 s serial). Every lock the brief listed was a HIT
  on a small dictionary — ~100 ns uncontended, and rarely contended, because between two lookups a
  thread spends ~200 µs in the pair's remainder quadrature. Removing them is correct and is
  invisible. `.With()`'s allocation per pair was likewise ~150 bytes against 200 µs of arithmetic.
- **The via-bearing fills got 19% (N = 514) and 16% (N = 1,175) faster serially, and 16–22% at
  cap 10.** Neither number comes from an O(N²) arm. It is the MIXED block (`MixedEntry`, one entry per
  via basis × horizontal basis): per via quadrature node it enumerated the horizontal cell's nodes
  through an iterator, so every one of ~1,500 outer nodes on a near or intermediate pair allocated an
  enumerator — 236 MB of them per fill on the N = 514 fixture, and ~30 s of its 58 s vector phase.
  The nodes are now enumerated inline (same nodes, same weights, same nesting order; bit-identical).
  The block is O(N·N_z), but a via cell is large enough that most horizontal cells count as near or
  intermediate to it and take the full graded rule, so on a fixture with two vertical unknowns it
  cost as much as the whole horizontal block.
- **The scaling shape is the same before and after, on every fixture, and it is the single-level
  fill's shape** — 95–99% at 2, 89–95% at 4, 52–60% at 10.

### Table 3 — the SINGLE-level fill's scaling on the 256 mm line, before and after milestone 4

`Fill` at N = 3,731 (FR-4, 6 GHz, HISTORY §12's top rung) — the fixture where `CLAUDE.md` §6's
98 / 81 / 74 / 53% was recorded:

| | cap 1 | cap 2 | cap 4 | cap 10 |
|---|---|---|---|---|
| before (upper triangle written, row-strided) | 70.90 s | 36.51 s · 1.94× · 97% | 18.78 s · 3.77× · 94% | 12.09 s · 5.86× · 59% |
| after (lower triangle written, contiguous; column-wise mirror) | 71.32 s | 36.53 s · 1.95× · 98% | 18.82 s · 3.79× · 95% | 11.10 s · 6.43× · 64% |

**The fall-off did not move.** The serial time is identical (71 s, so a strided write is nowhere in
the cost), and the cap-10 figure's 59 → 64% is one run each on a machine whose efficiency cores get
scheduled differently run to run — not a result. **This retires the "memory bandwidth" half of
§6's sentence.** The arithmetic: 25 M cache-missing writes at ~100 ns is ~2.5 s of a 71 s serial
fill spread over ten threads — even if every one of them stalled, it could not produce a 40% loss.

**What DOES produce it is written on the box.** An M4 has 4 performance and 6 efficiency cores.
Efficiency is 89–95% at cap 4 (four performance cores) on every fixture, before and after; the drop
appears only when the cap admits efficiency cores. If an efficiency core runs this fill at a
fraction f of a performance core's rate, the ideal speedup at cap 10 is 4 + 6f, and every cap-10
measurement here (5.2–6.4×) solves to **f ≈ 0.2–0.4** — which is what an efficiency core is. That
is also why §6's Amdahl fit gave three different serial fractions: there is no serial fraction; the
"lost" cores are slow ones. §6's "hardware, not scheduling" stands; the "memory bandwidth" candidate
is struck. On a box with homogeneous cores this fill should scale near-linearly, which is what §6
already listed as the thing that would change M3's answer.

### Table 4 — allocation per fill, serial (`GC.GetAllocatedBytesForCurrentThread`, exact)

| fixture | N | before | after | beyond the two matrices, after |
|---|---|---|---|---|
| two-level MMIC line + via, 400 µm | 514 | 269.9 MB (1,540 B/pair) | 32.6 MB | 27.1 MB — 12.6 MB of radial tables (built per fill, 9 pairings), 14.4 MB from the THREE ẑẑ entries' prism integrals |
| two-level MMIC line, no via, 100 µm | 288 | 13.1 MB (207 B/pair) | 3.2 MB | 1.45 MB — the six radial tables |

Attributed per arm on the N = 514 fixture with a temporary per-arm counter: after P3 the horizontal
arm allocates **0 bytes** and the mixed arm **0 bytes** across the whole fill. What remains beyond the
matrices is the per-pairing radial tables (which `Fill` also builds per call) and
`ViaZIntegral.PrismCore`'s own node arrays — **~4.8 MB per ẑẑ entry**, O(N_z²) entries, the via
z-integral's quadrature and out of P3's scope. It is the reason the routine allocation gate
(`P3_4`) runs on the no-via fixture: its allowance is computed from the settings — one
`MaxTableSamples`-capped table per (kernel, level, level) plus O(N) — and the pre-P3 tree fails it by
2.7× (11.35 MB against 4.22 MB allowed) while the P3 tree sits at 1.45 MB.

### Bit-identity — six digests, all literals

SHA-256 over every entry of the assembled `Z`, printed by `PlanarP3MultiLevelFillTests` in the
pre-P3 worktree and re-printed by the same test on the P3 tree:

```
two-level MMIC line + via, 100 µm (N = 324)      8E97C4ADC350AA1E33208FC054FBE3D9CFBBDA40C9B40E64193D8BFCDDDAABDE   routine
two-level MMIC line, no via, 100 µm (N = 288)    BA5557F07B4E64CB2581D6C8F0500C75D72A813F4D2EE2CDDF348C44A6CEFD3A   routine
two-level MMIC line + via, 400 µm (N = 514)      BEEBDDBEAEC69CA62EAEEA54D39F5FA1E8BE9842370286E041D51F72D906DC0E   Benchmark
two-level MMIC line, no via, 400 µm (N = 424)    F9F755E1D114703BE4DFD6C63D7731F5E71A2989B39FB80C7F5CEDCF1F34A3A0   Benchmark
FR-4 hero on two levels, 3 vias (N = 1,175)      4A1D16DDE421E310655A73237BA46929670D1CED86979148FBA7D503692A8210   Benchmark
two-level MMIC line + via, Order = Linear         A7B5D8C8415ECE92AE4CD4F5E7FCF07B811E0FED6685D885DDCE985A4CB2B832   Benchmark
FR-4 hero, single-level Fill (N = 552, P2_1's)   04F078DD4765A83EC4D068F20691BCD2A7CCF86943FCC680D40E620090B84D99   routine
```

Identical in every case, at caps 1, 2 and unbounded (`P3_3`). The single-level digest is the one P2
pinned, re-asserted after milestone 4 swapped the written triangle. `PlanarKernelSet.FitCount` is
unchanged on every fixture (13 / 6 / 13 / 13), which is the other thing the hoist had to preserve:
exactly the pairings the loops visit are resolved, and no others.

### What was changed in code

- `src/Engine/Mom/PlanarFill.cs` — `MultiLevelPairings` (private): resolves, serially and before
  `ForRows`, the scalar and vector terms + remainder per (layer, layer), the ẑẑ z-averaged terms +
  remainder + asymptote per ordered span pair, the mixed derivative per (span, horizontal layer),
  `RampHalves` per horizontal basis and `Pulse` per cell. `FillMultiLevel`'s three row loops read
  those tables and nothing else; the four dictionaries, their locks and `RemFor`/`ZzTermsFor`/
  `MixedDerivativeFor` are gone. `HorizontalVectorEntry` takes cached halves and adds its four
  remainders sequentially (the array is gone; the association is the one the digests were taken on).
  `MixedEntry` → `MixedHalf` enumerates a whole-rectangle half's nodes inline; a cut half still uses
  `OuterNodes`. `SingularPrismPart` takes the pair's asymptote rather than asking the set per entry.
  Milestone 4: `ScalarPotentialMatrix`, `Fill`, `AddDirectionBlock` and both multi-level loops write
  the LOWER triangle (contiguous in NumFlat's column-major `Mat<T>`) and `MirrorLowerToUpper` copies
  it column-wise; R-fil-2 and R-fil-11 are intact.
- `tests/Engine.Tests/Mom/PlanarP3MultiLevelFillTests.cs` — new. Routine: `P3_1` (two digests),
  `P3_2` (the hero's single-level digest + exact symmetry), `P3_3` (bit-identity at every cap),
  `P3_4` (the allocation allowance). Benchmark: `P3_1b` (four larger digests, ~80 s), `P3_5` and
  `P3_6` (the scaling tables above).

### What P3 deliberately did NOT do

- **No quadrature rule, table spacing or kernel evaluation changed**, and no second `Parallel.For`
  shape was added — `ForRows` is still the one loop, as the brief's Must-Not list requires.
- **`ViaZIntegral.PrismCore`'s per-entry allocation** (~4.8 MB per ẑẑ entry) was left alone: it is the
  via z-integral's own rule, O(N_z²), and outside the O(N²) loops this brief is about.
- **The mixed block's COST** — a full graded 4-D quadrature at near/mid order for most horizontal
  cells, because the via cell is large — is a quadrature-selection question and therefore out of
  scope by instruction. It remains the dominant cost of a via-bearing fill at small N (half the
  vector phase on N = 514) and is worth its own brief.
- **The `CrossFrequencyParallelismTests` numbers in §6 were not re-run** — Table 3 is the same
  measurement on the same fixture family and supersedes their INTERPRETATION, not their values.


---

## P4 — per-cell-pair moment caching in the vector block (brief-em-p4-vector-block-moment-cache.md, 2026-08-29)

**The quadrature count fell 6–7× and the wall clock 3–4×; the cached cores did not grow by a byte.**
Every measurement below is one run alone on the machine (10-core M4, 4 performance + 6 efficiency
cores), the second of two so the kernel fit and the radial tables are warm, at the shipping mesh
unless stated. "Before" is the P3 tree; "after" is this one. The pass counts are exact
(`PlanarFillCores.QuadraturePasses`, `PlanarFillCounters.RemainderPasses`), and are what the
routine gate asserts; the seconds are what a user waits for.

### Table 1 — the once-per-mesh core build

| fixture | N | cells m | passes before | passes after | ratio | before | after | speed-up |
|---|---|---|---|---|---|---|---|---|
| FR-4 hero, 20 mm, 10 GHz | 552 | 297 | 3.975 m² | **0.601 m²** | 6.6× | 2.81 s | **0.78 s** | **3.6×** |
| 60 mm 2.9 → 0.5 mm taper, 10 GHz | 1,891 | 1,000 | 4.085 m² | **0.563 m²** | 7.3× | 14.76 s | **3.70 s** | **4.0×** |
| 256 mm FR-4 line, 6 GHz (§12's top rung) | 3,731 | 1,980 | 4.064 m² | **0.599 m²** | 6.8× | 26.57 s | **6.75 s** | **3.9×** |
| 16 mm 1.0 → 6.71 mm taper, CONFORMAL, 6 GHz | 1,538 | 871 | 3.629 m² | 1.129 m² | 3.2× | 9.82 s | 4.81 s | 2.0× |

"Passes" is outer quadratures: before P4, one per unordered cell pair for the scalar block
(0.5 m²) plus four per same-direction basis pair for the vector block (≈ 3.5 m², two directions of
≈ m bases each); after, one per ORDERED cell pair in the band `c ≥ a − n_x` serving the scalar block
and both directions at once, plus four per basis pair that touches a cut half. The conformal
fixture is the last row's whole story: 1,538 unknowns of which a third sit on cut cells, and every
pair touching one takes the four-call path unchanged (milestone 5), so its count sits between.

**Why 6.6× in passes is 3.6× in seconds.** A seven-primitive pass evaluates six inner closed forms
per node (`Inverse`, `Log`, and the U and V first moments of each) where a ramp×ramp call evaluated
four; and the closed forms are the cost. The pass is therefore ~1.5× a call, and 6.6 / 1.5 ≈ 4.4
is the ceiling the seconds approach on the two long lines.

### Table 2 — the per-frequency fill (`PlanarFill.Fill`, one point)

| fixture | N | remainder passes before | after | ratio | fill before | fill after | speed-up |
|---|---|---|---|---|---|---|---|
| FR-4 hero | 552 | 3.473 m² | **0.601 m²** | 5.8× | 1.52 s | **0.51 s** | **3.0×** |
| 60 mm taper | 1,891 | 3.585 m² | **0.563 m²** | 6.4× | 6.93 s | **2.48 s** | **2.8×** |
| 256 mm line | 3,731 | 3.564 m² | **0.599 m²** | 5.9× | 11.08 s | **4.12 s** | **2.7×** |
| 16 mm conformal taper | 1,538 | 3.129 m² | 0.987 m² | 3.2× | 5.11 s | 3.20 s | 1.6× |

"Remainder passes" counts the VECTOR block's outer×inner quadratures of `rem(ρ)` — the scalar
block's own remainder (0.5 m² of the OTHER kernel, `G_q`) is unchanged by P4 and is why the fill's
factor sits below the vector count's: at N = 3,731 the vector remainder went from ~9.5 s to ~1.6 s
and the scalar block, the radial tables and the row passes are the ~2.5 s that remain. A
seven-sum pass costs about one call (the `rem` evaluation dominates; the three inner complex sums
are cheap beside it), which is why the fill's speed-up tracks the count more closely than the core
build's does.

**Where a dense frequency point's time now goes.** At N = 3,731 the fill is 4.1 s against an LU of
≈ 18 s (scaled by N³ from P1's 42.8 s at N = 4,933; the LU was not re-measured here). The
per-frequency crossover `CLAUDE.md` §8 put at "about N = 3,000" is nearer **N = 2,000** — the 60 mm
taper's 2.5 s fill against a scaled ≈ 2.4 s LU — and the sentence has been corrected in place.

### Table 3 — AIM's near field (`PlanarEntryFill`, milestone 4)

| fixture | N | near fill before | after | speed-up |
|---|---|---|---|---|
| 256 mm FR-4 line, 6 GHz | 3,731 | 23.68 s | **9.34 s** | **2.5×** |

`PlanarEntryFill.At` used to run four `PairCores` and four `PairRemainder` per same-direction pair
with no reuse across the ~400 pairs of a near-field row; it now memoises the seven primitives (cores
and remainder together) per ORDERED cell pair and assembles with the dense build's own `Combine`.
The factor is smaller than the dense fill's because a near-field row visits each cell pair from
fewer neighbouring rooftops than a full triangle does, and because the scalar `P` memo it always
had is unchanged. `AimAcceleratorTests.T1`'s bit-identity against `Fill` holds, and the routine
`P4_3` holds the same assertion at N = 24.

### Table 4 — the assembled matrix against the four-call reference

The pre-P4 arithmetic is kept as `PlanarFill.BuildCoresByHalves` / `FillByHalves` (a reference,
not a production path), so the agreement is a runnable gate rather than a digest printed once.
Compared entry by entry, `|Δ|/|reference|`, over the whole matrix:

| fixture | N | max relative difference | entries touching a cut basis | S0 / SLog |
|---|---|---|---|---|
| FR-4 hero, shipping | 552 | **1.03e-13** | none | bit-identical |
| 60 mm taper, shipping | 1,891 | **7.98e-13** | none | bit-identical |
| 16 mm conformal taper | 1,538 | **6.57e-13** | bit-identical, every one | bit-identical |

Also checked against a binary dump of the P3 tree's own `Fill` on the same three meshes (the
same three numbers to the digit) and: zero cross-direction entries moved — the scalar block is
untouched — and the largest difference relative to the matrix's largest entry is 1e-16.

### Memory — what the primitives cost, and why they are not stored

The brief's storage arithmetic ("7 × 2 doubles per cell pair against today's 2 + 3 + 3 — roughly
the same bytes") does not hold: the vector cores are per BASIS pair and there are ≈ 2 basis pairs
per unordered cell pair, so holding the primitives would be 14 doubles per cell pair against the
≈ 6 the P2 layout holds — **2.3× the cached cores, ≈ +250 MB at the ceiling**, in a series whose
P1 and P2 exist to take memory off. So the primitives are transient: each outer cell's pass
scatters them straight into P2's per-basis-pair triangles, the B-half contributions through a second
triangle of the same size (98 MB at N = 4,933, alive only during the build) that the row pass adds
in and drops. Resident cores are byte-for-byte the P2 layout — `P1_5`'s `CoreBytes` reconstruction
and `P2_2`'s triangle count pass unchanged. The per-frequency fill likewise scatters its seven
remainder sums into four transient `Complex` triangles (≈ 195 MB at N = 4,933) that die with the
fill; P1's Table 2 puts the fill phase's high-water mark well under the factorisation's (which
retains 2 × 16·N² = 778 MB there), so the point's resident peak is unmoved.

### Bit-identity — what moved, what did not, and the re-pinned digests

Four combinations of seven primitives are not the same floating-point operations as four
quadratures summed, so the vector block's last bits move (the brief said so; Table 4 bounds it).
What does NOT move: `S0`/`SLog` (the pulse×pulse primitive is accumulated with the pulse path's
own expressions — asserted exactly), every entry touching a cut basis (never left the four-call
path — asserted exactly), and `PlanarEntryFill.At` against `Fill` (both assemble with the same
function in the same order — asserted exactly). Digest literals from P2 and P3 therefore had to be
re-printed on this tree, and were, with a note at each: `P2_1`/`P3_2`'s hero
`04F078DD…B84D99` → `C30C787B…F90263`; `P2_6`'s sweep `8DC00C54…96DD97` → `4634B313…9839CD`;
`P3_1`'s `8E97C4AD…AABDE` → `C2A6E966…7EC7B4` and `BA5557F0…CEFD3A` → `2D4D1583…B1CC02`; the four
`P3_1b` benchmark digests likewise (values in the test). The 1e-12 gate is the bridge between the
old literals and the new.

### Tiering, on the owner's instruction (2026-08-29)

`P3_1` (9 s), `P3_3` (48 s) and `P3_4` (24 s) measured under full-suite load and were moved to
`Category=Benchmark` — the latter two were that slow before P4 (the via fixture's mixed block); P4's
own routine gates run 0.6–3.6 s (the four-call reference is the slow half of each), and its series
timings are `P4_4`, Benchmark.

### What was changed in code

- `src/Engine/Mom/PlanarFill.cs` — the P4 header (derivation, (P4.1)/(P4.2), the orientation and
  one-writer arguments). `RampTopology` (internal, O(N), on both core builders): per-basis halves and
  cut flags, per-direction CSR of (basis position, half) per cell, `MinInner` (the band's lower
  edge) and `HasWholeRamp`. `CellPairCores` / `CellPairRemainder`: the seven-primitive passes,
  L8c's own rules and node order. `Combine` (double and `Complex`): the (P4.2) map. `Scatter`:
  one ordered pair into every basis pair it serves, A half to the entry / B half to the transient.
  `BuildCores`: one cell-parallel pass (scalar + both directions) then `FinishDirectionCores` per
  direction (B triangle added; cut pairs by four calls). `Fill`: `ScalarBlock` (so `P` dies early)
  then `VectorBlock` (one remainder pass per ordered pair, both directions) and `AddDirectionBlock`
  reading the slots (cut pairs by four calls). `BuildCoresByHalves` / `FillByHalves`: the pre-P4
  arithmetic, kept as the reference. `PlanarFillCores.QuadraturePasses`, `.IsCutBasis`;
  `PlanarFillSettings.Counters` / `PlanarFillCounters.RemainderPasses`. `PlanarEntryFill`: `_vCache`
  of ordered-pair primitives, `VectorPairCount`, `At` assembling in the dense order; the four-call
  path retained for cut pairs.
- `tests/Engine.Tests/Mom/PlanarP4MomentCacheTests.cs` — new. Routine: `P4_1` (hero coarse: 1e-12,
  S0 bit-identity, the pass-count ratios under 30%), `P4_1b` (Order = Linear reaches the radius
  primitives), `P4_2` (a cut mesh: cut pairs bit-identical, whole pairs 1e-12), `P4_3` (per-entry
  fill bit-identical to the dense one, cache smaller than the four-call count). Benchmark: `P4_4`
  (the three series fixtures with timings).
- `PlanarP2MemoryWinsTests`, `PlanarP3MultiLevelFillTests` — digests re-pinned with notes; three
  P3 tests re-tiered.
- `docs/design/mom-engine.md` §10.7 — `> Built at P4`; `CLAUDE.md` §8's "cost is the fill" bullet
  corrected in place.

### What P4 deliberately did NOT do

- **No quadrature rule, panel clustering or closed form changed**; the ẑ blocks were not touched
  (they are pulse×pulse and share `S0`, which is bit-identical).
- **The multi-level fill's horizontal remainder** (`HorizontalVectorEntry`) still runs four
  `PairRemainder` per pair per frequency. It reads P4's cores (so its core build got the full
  factor) but its per-(layer, layer) remainder tables would need the cell pass to pick a `rem` per
  pair; the brief names `AddDirectionBlock` and `PlanarEntryFill` only, and this is recorded for the
  follow-up rather than widened into.
- **AIM's per-frequency rebuild of the primitives** — `_vCache` lives one frequency, like the
  operator that owns it. P6 is the frequency-independent AIM state.
- **The 256 mm line's LU was not re-timed**; the crossover statement above is scaled from P1's
  measured 42.8 s at N = 4,933 and says so.

## P5 — translation classes: one integral per class of cell pairs (brief-em-p5-translation-class-memo.md, 2026-08-29)

**The brief's class table reproduced to the pair; the core build fell 3.7–41× and the per-frequency
fill 2.8–5.8× beyond P4; the cores shrank up to 5× where reuse is high and grew slightly where it is
not.** Every measurement below is one run alone on the machine (10-core M4), the P4 arithmetic
(`BuildCoresByPairs`/`FillByPairs`, retained as the reference) and the P5 arithmetic timed in the
same test (`PlanarP5TranslationClassTests.P5_6`), fills the second of two so the tables are warm.
Counts are exact and are what the routine gate asserts.

### Table 1 — the classes, counted two ways

The brief's count (unordered pairs, canonical orientation, quantisation 1e-6 · smallest edge) is
reproduced exactly on all seven fixtures — including the coupled pair, which the brief did not
specify beyond its name and which a gap of one line width reproduces to the cell (1,098 / 2,056 /
106,608). The classifier's count is over P4's ORDERED band (≈ 20% more pairs than the unordered
triangle), with the 180° rotation folded and the τ band in the key (see PlanarPairClasses.cs).

| structure | N | cells | brief: pairs / classes | reuse | classifier: band pairs / classes | reuse |
|---|---|---|---|---|---|---|
| FR-4 hero 2.9 × 20 mm, 10 GHz | 552 | 297 | 44,253 / 18,036 | 2.5× | 52,997 / 21,056 | 2.5× |
| FR-4 line 80 mm, 10 GHz | 1,980 | 1,053 | 554,931 / 70,494 | 7.9× | 664,559 / 82,264 | 8.1× |
| FR-4 line 256 mm, 6 GHz | 3,731 | 1,980 | 1,961,190 / 118,188 | 16.6× | 2,348,609 / 137,688 | 17.1× |
| FR-4 taper 2.9 → 0.5 mm, 20 mm | 1,371 | 728 | 265,356 / 10,430 | 25.4× | 298,576 / 11,298 | 26.4× |
| FR-4 taper 60 mm | 1,891 | 1,000 | 500,500 / 16,888 | 29.6× | 563,266 / 18,252 | 30.9× |
| GaAs line 72 µm × 2 mm, 20 GHz | 773 | 414 | 85,905 / 40,656 | 2.1× | 102,878 / 47,632 | 2.2× |
| two coupled 40 mm lines | 2,056 | 1,098 | 603,351 / 106,608 | 5.7× | 663,007 / 115,287 | 5.8× |

**Spacing classes, and why they are quantised.** Exact `==` on the gridline differences does not
hold on any of the seven: the hero's x axis has 15 exactly-distinct spacings for 6 classes at 1e-12
relative (the 80 mm line 20 for 7, the 60 mm taper 18 for 6), and the in-class spread reaches
4.0e-13 (GaAs x) and 3.7e-13 (60 mm taper x). The mesher writes a bulk line as `a + len·i/n` and the
graded runs through the marcher's rescale, so the classifier quantises at 1e-12 relative and
represents a class by its smallest member. The y axes of every FR-4 line have 6 classes in 9 cells
(three graded cells each side of a bulk of three); the tapers' y axis is ONE class of 5 (19 cells at
one pitch, since the taper's flanks are oblique and contribute no gridline).

### Table 2 — the once-per-mesh core build

| structure | passes P4 → P5 | P4 | P5 | speed-up | cores P4 → P5 |
|---|---|---|---|---|---|
| FR-4 hero | 52,997 → 21,056 | 0.91 s | **0.24 s** | 3.8× | 1.9 → 2.6 MB |
| FR-4 line 80 mm | 664,559 → 82,264 | 3.50 s | **0.32 s** | 10.8× | 23.6 → 12.1 MB |
| FR-4 line 256 mm | 2,348,609 → 137,688 | 7.51 s | **0.46 s** | **16.2×** | 83.5 → **25.0 MB** |
| taper 20 mm | 298,576 → 11,298 | 2.15 s | **0.05 s** | 39.4× | 11.3 → 2.5 MB |
| taper 60 mm | 563,266 → 18,252 | 3.72 s | **0.09 s** | **40.8×** | 21.4 → **4.3 MB** |
| GaAs line 2 mm | 102,878 → 47,632 | 3.26 s | 0.88 s | 3.7× | 3.6 → 5.9 MB |
| coupled 40 mm lines | 663,007 → 115,287 | 3.35 s | **0.43 s** | 7.8× | 25.4 → 15.8 MB |

"Passes" is seven-primitive outer quadratures: P4 ran one per ordered band pair, P5 runs one per
class — the counts are the reuse factors of Table 1. The seconds lag the counts on the short
structures (hero 2.5× in count, 3.8× in seconds only because P4's own pass count includes the band's
extra 20%) and lead them nowhere: classification of the band (a key per pair, a sort of the distinct
keys, a binary search per pair) is 0.1–0.2 s at N = 3,731 and is most of the P5 build there.

**Memory.** The class layout holds 4 bytes per ordered band pair plus 112 bytes per class (seven
primitives × two kernels), where P4's triangles held ≈ 24 m² bytes; the two meet at a reuse of ≈ 3×.
The long line and the tapers are 3.3–5× smaller; the hero (2.5×) and the GaAs line (2.2×) are
1.4–1.6× LARGER, both under 6 MB. A mesh with no translation reuse at all would hold up to 2.9× P4's
bytes — the brief's premise that reuse grows with the structures that get big is what the layout
rests on, and it is stated as a break-even rather than as a win. The a-priori figure the ceiling
refusals quote (`PlanarSystem.CoreBytes`) is P4's formula, kept as a conservative quote.

### Table 3 — the per-frequency fill (`PlanarFill.Fill`, one point)

| structure | N | vector remainder passes P4 → P5 | P4 | P5 | speed-up |
|---|---|---|---|---|---|
| FR-4 hero | 552 | 52,997 → 21,056 | 0.59 s | **0.21 s** | 2.8× |
| FR-4 line 80 mm | 1,980 | 664,559 → 82,264 | 1.95 s | **0.64 s** | 3.1× |
| FR-4 line 256 mm | 3,731 | 2,348,609 → 137,688 | 4.66 s | **1.25 s** | 3.7× |
| taper 20 mm | 1,371 | 298,576 → 11,298 | 1.43 s | **0.29 s** | 4.9× |
| taper 60 mm | 1,891 | 563,266 → 18,252 | 2.36 s | **0.41 s** | **5.8×** |
| GaAs line 2 mm | 773 | 102,878 → 47,632 | 2.03 s | 0.72 s | 2.8× |
| coupled 40 mm lines | 2,056 | 663,007 → 115,287 | 2.14 s | **0.56 s** | 3.9× |

The remainder is now evaluated once per class — the vector block's seven sums and the scalar
block's pulse sum, the latter a saving P4 never had — and every entry is assembled from its cell
pairs' class values by one function (`WholeVectorEntry`, shared with `PlanarEntryFill.At`). The
transient per-basis-pair `Complex` triangles P4 scattered into (≈ 195 MB at N = 4,933) are gone; the
per-class remainder tables are 112 bytes per class. What remains of the fill at N = 3,731 is the
O(N²) assembly itself (the scalar block's m×m `P`, the basis-pair loops, the mirror) and the two
radial tables — the fill's floor without a change of formulation.

**Where a dense point's time goes now.** At N = 3,731 the fill is 1.25 s against an LU of ≈ 18 s
(scaled by N³ from P1's 42.8 s at N = 4,933, not re-timed); at N = 1,891 it is 0.41 s against ≈ 2.4 s.
The per-point crossover P4 put at "roughly N = 2,000" is now nearer **N = 1,000**, and the sweep-level
statement "cost is the fill, not the LU" no longer holds anywhere the core build is amortised over
more than a handful of points. P7 (the factorisation) is where the time is.

### Table 4 — AIM's near field (`PlanarEntryFill`, milestone 5)

| structure | N | near fill P4 | P5 | speed-up | memo entries P4 → P5 |
|---|---|---|---|---|---|
| FR-4 line 256 mm, 6 GHz | 3,731 | 8.34 s | **1.22 s** | **6.8×** | 219,879 cell pairs → 7,434 scalar classes |

P4's figure was measured on the P4 tree on the same box the same morning (`PlanarAimReport.NearFillMs`).
The per-entry fill classifies each near pair on demand and integrates a class once, on the same
synthetic representative the dense build uses, so `At` stays bit-identical to `Fill`
(`AimAcceleratorTests.T1`, `P4_3`); the 1,461,553 near entries of this operator draw on 7,434 classes.

### Table 5 — the assembled matrix against P4's arithmetic

Compared entry by entry against `BuildCoresByPairs`/`FillByPairs` in the tree (and, before the code
changed, against binary dumps of the P4 tree's own `Fill` on the same seven meshes — the same
numbers).

| structure | max |Δ| / √(|Z_ii||Z_jj|) | max |Δ| / |Z_max| | relative, entries ≥ 1e-6·|Z_max| | relative, all entries |
|---|---|---|---|---|
| FR-4 hero | 6.1e-14 | 3.1e-14 | 2.1e-8 | 1.0e-6 |
| FR-4 line 80 mm | 2.2e-13 | 1.1e-13 | 6.7e-8 | 3.5e-5 |
| FR-4 line 256 mm | **4.7e-13** | 1.8e-13 | 1.2e-8 | 2.0e-3 |
| taper 20 mm | 8.5e-14 | 3.8e-14 | 1.8e-8 | 1.3e-5 |
| taper 60 mm | 2.6e-13 | 1.5e-13 | 3.0e-8 | 1.4e-4 |
| GaAs line 2 mm | 3.0e-13 | 3.0e-13 | 7.2e-9 | **1.2e-1** |
| coupled 40 mm lines | 4.6e-13 | 1.6e-13 | 1.2e-7 | 6.8e-6 |

**The brief's "1e-12 relative per entry" is the last column, and it is not attainable — by the
reference, not by P5.** The closed forms evaluated at absolute coordinates lose ~1e-16·(x/w) of their
own value: on the P4 tree, before any P5 code, the self core of a 0.5 mm cell moved 2e-13 relative
between x = 0 and x = 1 m, and the far core of two cells 0.25 m apart 5e-13. A class value is computed
with the outer cell at the origin — the more stable of the two — and D4's signed second differences
turn a fixed ~1e-13·|Z_ii| disagreement into O(1) relative on an aligned x̂/ŷ far pair whose true
entry is cancellation residue (the GaAs worst entry is 1.2e-14 of the largest). The gate is the first
column, per entry, at 1e-12 — the scale on which the LU reads the entry — and the solved currents of
`Z x = e_k` on the coarse hero agree through the factorisation to ~1e-12 relative (`P5_2b`). The
third column is the same disagreement seen on entries that are not residue.

### The mesher finding the brief asked for (milestone 6)

The GaAs line's x grid, printed: −x end 2.16, 6.47, 19.4, 58.3, 175 µm into a 208 µm bulk of 8 cells;
+x end **32 cells of 2.16 µm** — no graded fan at all, 69 µm of a 2 mm line at the edge-cell pitch.
The hero's x axis is asymmetric in a milder form (86.6, 174.8, 352.7 → 711.6 µm left; 538.9, 173.2,
86.6, 86.6 right), which is what §3.5's "end grading is not exactly mirror-symmetric" already records.
So the GaAs line's 2.1× is not a short-line effect alone: its two ends are different meshes. Recorded
for a mesher brief; the brief forbids changing the mesher here.

### Bit-identity — what moved, and the re-pinned digests

Every core and remainder is now the integral on its class's representative, so the last bits of
every entry move, the scalar block included (P4 had left it untouched). What stays bit-identical:
`PlanarEntryFill.At` against `Fill` (`T1`, `P4_3`), and the scalar core of every pair with a cut cell
(its own row, the same conformal call in the same orientation). An entry that touches a cut BASIS is
no longer bit-identical to P4's, because its scalar block sums whole-cell pairs; P4's own bit-identity
claims are therefore held on P4's own retained arithmetic (`PlanarP4MomentCacheTests` runs on
`BuildCoresByPairs`/`FillByPairs`). Re-pinned with a note at each literal: `P2_1`/`P3_2`'s hero
`C30C787B…F90263` → `BF177C91…7EC34B`; `P2_6`'s sweep `4634B313…9839CD` → `713EFE3B…D4FE0B`; all six
multi-level digests of `P3_1`/`P3_1b`/`P3_3`. `P1_5` and `P2_2` re-pin the cores' COMPOSITION
(band + class table + row starts + moments + classifier tables) and treat `PlanarSystem.CoreBytes` as
an a-priori figure rather than a reconstruction; `T1b` re-pins the geometry-only core as 8·N plus the
classifier's grid-bounded tables.

### What was changed in code

- `src/Engine/Mom/PlanarPairClasses.cs` — new. `PairClassifier`: per axis, spacing classes at 1e-12
  relative and hash-consed spacing LISTS (`ListId`, `Count × Count`, forward and backward reads sharing
  ids); the 63-bit key (τ band, sign pattern, x list id, y list id); `Representative(key)` — the
  synthetic pair; `CoreRule`/`RemainderNodes` from the band. `CutRows`: per-index rows for what no
  class serves.
- `src/Engine/Mom/PlanarFill.cs` — `PlanarCoreLayout` (Classes / Triangles). `PlanarFillCores`: the
  band (`BandStart`, `BandClass` = `(class << 1) | rotated`), `ClassKey`, `ClassCores` (stride 7 ×
  kernels), `CutScalar`/`CutX`/`CutY`, `Memoised`, accessors `ScalarCoreOf`/`VectorCoreOf`/`ClassMoments`,
  `ClassCount`/`BandPairs`/`SpacingClasses`/`ExactlyDistinctSpacings`/`ClassifierBytes`; the
  triangle arrays nullable, for the references. `BuildCores`: classify (parallel keys, sorted distinct
  keys, ranks), one `CellPairCores` per class on its representative, cut rows. `Fill`: per-class
  remainders (`ClassRemainders`, `ClassPulseRemainders`), `AddDirectionBlockFromClasses` pulling
  through `IPairSource`/`BandSource`/`WholeVectorEntry`. `ScalarPotentialMatrix` layout-aware.
  `CellPairCores`/`CellPairRemainder` overloads on two cells with an explicit rule;
  `CellPairPulseRemainder`. `BuildCoresByPairs`/`FillByPairs`: P4's build and fill, verbatim, the
  reference; `RequireLayout`. `FillMultiLevel`'s readers go through the accessors.
  `PlanarEntryFill`: three class-keyed caches (`_cores`, `_remA7`, `_remQ1`) plus a per-pair one for
  pairs with a cut cell; `ClassSource`; `At` shares `WholeVectorEntry`.
- `PlanarSystem.CoreBytes` doc (a-priori figure); `PlanarAimReport.NearCellPairs` doc.
- `tests/Engine.Tests/Mom/PlanarP5TranslationClassTests.cs` — new. Routine: `P5_1` (the brief's counts
  and the classifier's, literals, hero + 60 mm taper), `P5_2` (coarse hero, the diagonal-scale gate,
  one pass per class), `P5_2b` (solved currents), `P5_3` (Order = Linear), `P5_4` (a cut mesh: rows,
  scalar cut cores bit-identical), `P5_5` (a uniform square grid — the τ = 4 family). Benchmark:
  `P5_6` (all seven: counts, gate, timings), `P5_7` (AIM near fill, 256 mm).
- `PlanarP4MomentCacheTests` re-pointed at `BuildCoresByPairs`/`FillByPairs`; digests re-pinned in
  `PlanarP2MemoryWinsTests`, `PlanarP3MultiLevelFillTests`; `P1_5`, `P2_2`, `T1b` re-pinned.
- `docs/design/mom-engine.md` §10.7 — `> Built at P5`.

### What P5 deliberately did NOT do

- **No hybrid layout** for low-reuse meshes (scatter the class table into P4's triangles when the
  count is unfavourable): a second production layout in every reader, for a case none of the seven
  fixtures approaches. Recorded with the break-even.
- **The multi-level fill's horizontal remainder** still runs four `PairRemainder` per pair per
  frequency (its cores come from the class table); the brief names `Fill` and `PlanarEntryFill`.
- **The mesher's end asymmetry** is recorded, not fixed.
- **The 256 mm line's LU was not re-timed.**

## P6 — AIM's frequency-independent state is built once per mesh (brief-em-p6-aim-frequency-independent-state.md, 2026-08-29)

Files: `PlanarAim.cs` (`PlanarAimGeometry`, new; `PlanarAimOperator` now per frequency over it),
`PlanarFill.cs` (`PlanarEntryCores`, new — the frequency-independent half of `PlanarEntryFill`;
`PlanarCoreBuildCounter.AimGeometryTotal`), `PlanarSolve.cs` (`PlanarSolveContext.AimGeometry`, lazy
beside the cores). Gates: `PlanarP6AimGeometryTests` (**3 routine, ~1 s**: bit-identity across the
seam, the once-per-sweep counter, the report's accounting) and `P6_4` (`Category=Benchmark`, ~1 min:
the tables below). `AimAcceleratorTests` unchanged and green; `AimAccuracyTests` was not re-run —
see "Bit-identity" for why its numbers cannot have moved.

### The premise, corrected before anything was built

The brief opens from §12's table: "AIM's per-frequency build at N = 3,731 is 25 s". That was true
when §12 was written and false on the tree P6 started from — P4 and P5 had already taken the near
fill from 23.7 s to 1.07 s. Measured on the committed tree (`b373b2f`) the same morning, one
`PlanarAimOperator.Build` at the shipping mesh:

| N | projection | grid kernel | remainder tables (+ near set) | near fill | sparse LU | **whole build** | resident |
|---|---|---|---|---|---|---|---|
| 552 | 22–25 ms | 3–6 ms | 54 ms | 407–434 ms | 94–101 ms | **579–623 ms** | 9.7 MB |
| 3,731 | 143–145 ms | 11–12 ms | 141–162 ms | 1,074–1,082 ms | 371–399 ms | **1,751–1,791 ms** | 60.1 MB |
| 11,959 | 461–465 ms | 31–34 ms | 144–148 ms | 2,955–3,161 ms | 1,112–1,114 ms | **4,707–4,918 ms** | 196.2 MB |

(FR-4 line, 20 mm at 10 GHz / 256 mm at 6 GHz / 832 mm at 6 GHz — the fixtures of §P1's tables;
two builds each, both shown. "Near fill" here is cores + remainders + AIM correction + the lower
copy, undivided, because the pre-P6 report timed them as one.)

So what P6 could remove per frequency was the projection, the near set, and whatever fraction of the
near fill was the singular cores — not the twenty-odd seconds the brief's premise implied. The
structural claim stands regardless: the dense path runs its singular quadrature once per mesh
(D6, `CoreFillCount == 1`) and the accelerator ran it once per frequency.

### Table 1 — the same three builds after P6, split as the brief asks

| N | **geometry, ONCE** (projection / near set / cores) | grid kernel | remainder tables | near remainders | **AIM correction** | lower copy | sparse LU | **per frequency** | resident (geometry / per-freq) |
|---|---|---|---|---|---|---|---|---|---|
| 552 | **243 ms** (25 / 3 / 215) | 3–6 ms | 43–52 ms | 150–214 ms | 75–115 ms | 1 ms | 95 ms | **349–424 ms** | 11.5 MB (2.8 / 8.7) |
| 3,731 | **470–510 ms** (145 / 15–29 / 295–337) | 12–13 ms | 132–136 ms | 345–347 ms | 539–576 ms | 5 ms | 342–359 ms | **1,248–1,299 ms** | 61.0 MB (8.4 / 52.6) |
| 11,959 | **1,100–1,120 ms** (445–453 / 25–39 / 630–635) | 31–34 ms | 112–115 ms | 880–966 ms | 1,768–1,861 ms | 17 ms | 1,121–1,139 ms | **3,820–3,992 ms** | 195.8 MB (23.7 / 172.2) |

"Per frequency" is grid kernel + near remainders + correction + copy + LU, the remainder tables
excluded as every AIM table here excludes them (the dense path builds the same two). Against the
whole pre-P6 build, a second and later point now costs **1.4× / 1.25× / 1.2× less** at the three N
(579–623 → 392–476 ms; 1,751–1,791 → 1,380–1,435 ms; 4,707–4,918 → 3,932–4,107 ms, tables included),
and the first point pays the geometry once on top: +0.24 / +0.5 / +1.1 s. Through the shipped
driver, a 5-point sweep at N = 3,731 is **8.3 s**: 2.05 s for the first point (0.46 s of it the
geometry) and 1.56 s for each of the other four, the GMRES solve and excitation included.

**What the split shows that the undivided number hid: the AIM correction is now the largest
per-frequency term** — 43% of the build at N = 3,731 and 46% at 11,959, ahead of the LU (28%) and
the remainders (23–27%). `AimEntry` runs `(M+1)⁴ = 256` complex kernel lookups and products per
near pair for each of the two blocks; the products of the two stencils' coefficients are
frequency-independent, the kernel values are not, and there are at most `(2M+1)² = 49` distinct
grid offsets between two stencils — so the same sum can be written as 256 real products into 49
offset weights and 49 complex multiplies, roughly a fifth of the arithmetic. Recorded here for
P7/P8; not built, because it is neither a milestone of this brief nor a memory question.

### Table 2 — the time crossover, measured rather than scaled

One dense point is the P5 fill plus NumFlat's LU and one back-substitution; one accelerated point
is the per-frequency build over an already-built geometry plus GMRES. Shipping mesh, one core build
of each kind untimed:

| N | dense fill | dense LU + solve | **dense point** | AIM per-frequency build | AIM solve (iters) | **AIM point** |
|---|---|---|---|---|---|---|
| 552 | 0.20 s | 0.04 s | **0.24 s** | 0.35 s | 0.02 s (5) | **0.37 s** |
| 994 | 0.36 s | 0.18 s | **0.54 s** | 0.69 s | 0.03 s (5) | **0.71 s** |
| 1,912 | 0.52 s | 1.51 s | **2.03 s** | 0.91 s | 0.05 s (5) | **0.96 s** |

**The crossover is at N ≈ 1,100** (between the 64 mm and 128 mm rungs; §12 put it at 3,700, and
§8's P5 correction had already moved the dense LU's own dominance to about 1,000). Above it the
dense point grows as N³ in the LU while the accelerated point grows close to linearly, so at
N = 1,912 the accelerated point is 2.1× faster and at N = 3,731 roughly 13× (1.4 s + 0.1 s against
1.25 s + ≈ 18 s, the LU scaled from P1's 42.8 s at N = 4,933 as §8 does). Below it the accelerator is
still 1.3–1.5× slower and the dense path is the right default, which is what ships.

### Memory — what the geometry adds, and what it does not (milestone 5)

| N | stencils (16 B/node, was 32) | CSR column index | near cores | **geometry** | per-frequency | **resident** | **pre-P6** |
|---|---|---|---|---|---|---|---|
| 552 | 0.14 MB | 0.74 MB | 2.0 MB (9,706 classes) | 2.8 MB | 8.7 MB | **11.5 MB** | 9.7 MB |
| 3,731 | 0.96 MB | 5.6 MB | 1.9 MB (8,606 classes) | 8.4 MB | 52.6 MB | **61.0 MB** | 60.1 MB |
| 11,959 | 3.06 MB | 18.2 MB | 2.5 MB (9,966 classes) | 23.7 MB | 172.2 MB | **195.8 MB** | 196.2 MB |

- **The near cores cost 2–2.5 MB at every N, not the 112 B × ~200/row × m the brief budgeted.** That
  arithmetic is per near CELL PAIR (P4's primitives); P5 keys them by translation class, and the
  number of classes a near field draws on is ~9–10 thousand at N = 552 and at N = 11,959 alike. Held
  as a flat array of 168-byte structs (seven `CoreTriple`s) plus a key-to-slot index, they are
  **~208 B per class** — at the ceiling 2.5 MB against the ~130 MB the per-pair form would have been.
- **The stencils halve** (they were `Complex` holding real numbers; now `double`), which is what
  makes the ceiling figure come out at 195.8 MB rather than up: the halving (−3.1 MB at 11,959)
  covers the cores (+2.5 MB).
- **The mirror index is NOT held, though the brief listed it.** Measured first with it held: 4 B per
  near entry is 0.7 / 5.6 / **18.2 MB**, and the resident set at N = 11,959 read **214 MB** — over the
  "under 200 MB at the ceiling" line §8 states — to save a rebuild that is one binary search per
  lower-triangle entry per frequency: **1 / 5 / 17 ms** (the "lower copy" column of Table 1).
  `PlanarAimGeometry.TransposePosition` does the search inline.
- The geometry-only cores (`PlanarFillCores.CoreBytes`: 8·N plus the classifier's grid tables) are
  reported beside these, as before, not inside `ResidentBytes`; `P1_3`'s walk stops at them.

### Bit-identity — what was compared, and why the accuracy tier was not re-run

Before a line changed, a throwaway test on the committed tree dumped, for the coarse 16 mm fixture at
6, 10 and 3 GHz: the whole accelerated matrix (every column by a unit probe through `Multiply`), every
near exact entry, the solved current and the GMRES iteration count. The same test on the P6 tree
produced the same 196,576 bytes, `cmp`-identical — the `double` stencils, the geometry split, the
flat class store and the inline transpose search change no arithmetic. `P6_1` holds the same claim
permanently on the operator API (the four-argument `Build`, which still constructs geometry and
operator from scratch, against an operator over a shared geometry, at five frequencies), and `P6_2`
through `PlanarSolveContext`. `AimAccuracyTests` reads only `PlanarAimOperator.Build(cores, …)`, which
is bit-identical by that measurement, so its tables cannot have moved; it was not spent five minutes
on, per the owner's instruction to avoid the Benchmark tier where a cheaper measurement answers.

### The counter (milestone 3)

`PlanarCoreBuildCounter.AimGeometryTotal` (and `AimGeometryBuildsFor(mesh)`) counts geometry builds
the way `PairCoreTotal` counts dense core builds; `PlanarEntryCores.CorePasses` counts singular-core
passes (a class 1, a cut scalar pair 1, a cut vector pair 4). `P6_2` runs a 5-point sweep through
`PlanarSolveContext` and asserts `AimGeometryTotal == 1`, `Total == 1` (one geometry-only core build,
no pair-core build), and `CorePasses` unchanged from the first point to the fifth — and, the stronger
form, unchanged from the geometry build itself, because the geometry warms every near pair's cores at
build (`PlanarEntryCores.Prepare` over the upper triangle, row-parallel).

### What was changed in code

- `PlanarAimGeometry` (new, `PlanarAim.cs`): grid, stencils, near set CSR, and a `PlanarEntryCores`
  warmed over the near set. `Build(cores, settings)` validates and refuses the ẑ basis by name (the
  refusal moved here from the operator). Reports `ProjectionMs` / `NearSetMs` / `NearCoreMs` and `Bytes`.
- `PlanarAimOperator`: constructed over a geometry; `Build(geometry, termsA, termsQ, ω)` is the sweep's
  overload and `Build(cores, …)` builds both. `AimStencil.Current`/`Charge` are `double[]`.
  `PlanarAimReport` gains `GeometryMs`, `NearSetMs`, `NearCoreMs`, `NearRemainderMs`, `CorrectionMs`,
  `LowerCopyMs`, `GeometryBytes`, `NearCoreClasses`, `PerFrequencyMs`, `PerFrequencyBytes`;
  `ResidentBytes` carries the geometry as one term.
- `PlanarEntryCores` (new, `PlanarFill.cs`): the frequency-independent half of `PlanarEntryFill` —
  halves, moments, classifier, and the class / cut-pair core stores with `CorePasses`.
  `PlanarEntryFill` takes one (its four-argument constructor builds a private one) and keeps only
  the ω-dependent remainder memos.
- `PlanarSolveContext.AimGeometry`, lazy beside `Cores`; `SolveAt` builds the per-frequency operator
  over it. `PlanarCoreBuildCounter.AimGeometryTotal`.
- `PlanarMemoryAccountingTests`: the walk sizes a struct element with `Unsafe.SizeOf` (it sized one
  at 0, which would have let the class store go uncounted) and stops at the geometry-only cores.

### What P6 deliberately did NOT do

- **The mirror index** — measured and left out (above).
- **The AIM correction's restructuring** — recorded as the largest per-frequency term, not built.
- **Projection order, pitch, near radius, tolerance, preconditioner** — untouched (P8 owns the
  radius). **The multi-level refusal** — untouched (P12); it moved from the operator to the geometry
  with its text unchanged.
- **`AimAccuracyTests`** — not re-run, for the reason given under bit-identity.

---

## P7 — an in-place, blocked, parallel complex-symmetric factorisation (brief-em-p7-symmetric-inplace-factorisation.md, 2026-08-29)

The dense path's factorisation was `NumFlat.LuDecompositionComplex`: a general LU that knows nothing
about Z's symmetry, does 2N³/3 complex multiply-adds, runs on **one core** (P1 measured CPU/wall =
1.00 on ten), and stores `L` and `U` as two further full N×N matrices beside the one `PlanarSystem`
already holds. P4 and P5 had cut the fill 8–17×, so by the time this brief started a dense frequency
point near R17's ceiling was mostly a single-threaded factorisation sitting next to a fill that
parallelises 3.7× and 32·N² of factor storage that nothing but the back-substitution ever reads.

**Z is complex-symmetric BIT FOR BIT** — `PlanarFill.Fill` and `FillMultiLevel` both compute the
lower triangle and `MirrorLowerToUpper` copies it, so `Z[i,j]` and `Z[j,i]` are the same bits
(R-fil-2). `SymmetricFactorization` is an unpivoted `A = L·D·Lᵀ` written into that same lower
triangle: half the arithmetic, no second matrix, and a right-looking blocked form (64 columns) whose
trailing update is one independent write per destination column, so it goes through
`PlanarFill.ForRowsOf` and spends the run's ONE parallel cap exactly as the fill does.

### Table 1 — factorisation time, the SHIPPED (Release) configuration

FR-4 hero cross-section at 10 GHz, shipping mesh, lines of 20 / 80 / 200 mm — the same three rungs
P1's memory table used. Each factorisation on its own copy of one filled Z (the shipped one consumes
its input), copies outside the timer.

| N | cells | cap 1 | cap 2 | cap 4 | cap 10 | NumFlat LU | **LU ÷ cap 10** | cap 1 ÷ cap 10 |
|---|---|---|---|---|---|---|---|---|
| 552 | 297 | 0.04 s | 0.02 s | 0.02 s | **0.01 s** | 0.04 s | **2.7×** | 3.1× |
| 1,980 | 1,053 | 0.91 s | 0.49 s | 0.41 s | **0.26 s** | 1.92 s | **7.3×** | 3.5× |
| 4,836 | 2,565 | 13.14 s | 6.88 s | 5.83 s | **3.59 s** | 41.21 s | **11.5×** | 3.7× |

Two independent factors, and it is worth keeping them apart. **Single-threaded, the new
factorisation is already 3.1× the LU at the top rung** (13.14 s against 41.21 s) — that is the
halved flop count plus the blocking, not the cores. **The cores then add 3.7×**, and 3.7 is the
number P3 predicted for this box: ten cores of which four are performance and six efficiency, so a
cap-10 speed-up solving as 4 + 6f gives f ≈ 0.2–0.4 exactly as every fill measurement here does.
The LU's 41.21 s reproduces P1's 42.8 s at N = 4,933 to within the size difference, which is what
says the two measurements are comparable.

### The measurement trap this phase paid for, and it is not a small one

**`dotnet test` builds DEBUG.** Every other timing in this area compares managed code against
managed code, so the build configuration has always cancelled and nobody has had to think about it.
**This comparison does not cancel**: NumFlat's LU is native and barely moves between configurations,
while `SymmetricFactorization` is ordinary C# and runs about ten times slower unoptimised. The first
run of the benchmark, in Debug, produced

| N | cap 1 | cap 2 | cap 4 | cap 10 | NumFlat LU |
|---|---|---|---|---|---|
| 552 | 0.22 s | 0.14 s | 0.10 s | 0.08 s | 0.04 s |
| 1,980 | 10.10 s | 5.38 s | 3.09 s | 2.25 s | 1.99 s |
| 4,836 | 143.81 s | 74.13 s | 42.62 s | 28.31 s | 40.72 s |

i.e. **"the new factorisation is 3.5× slower single-threaded than the one it replaces"** — and it is
the same code that is 3.1× faster in Release. The Debug/Release ratio is **11×** for the managed
factorisation and **0.99×** for the LU. `PlanarP7FactorCostTests` now prints its own build configuration
above its table and asserts only what is true in both (cap 10 beats cap 1; the allocation counts);
"faster than the LU" is a Release statement and lives here.

**A second, smaller trap, recorded because it wasted a run.** A synthetic complex-symmetric matrix
with a `1/(1+|i−j|)` decay is NOT a stand-in for Z when the thing being timed is NumFlat's LU: the
same N = 4,836 reads **97.0 s** on the synthetic (twice, reproducibly) against **41.2 s** on a real
filled Z, while `SymmetricFactorization` reads 13.1 s on both. The synthetic is fine for the
correctness tests, which is where it is used; it is not fine for the cost table.

### Table 2 — memory, counted

`PlanarSystem`'s own arithmetic. `LuFactorBytes` is the pre-P7 term, kept and named as such;
`SymmetricFactorBytes` is `16·N` — the diagonal D, and nothing else.

| N | matrix | L + U (pre-P7) | D (P7) | cached cores | **peak pre-P7** | **peak P7** | ratio |
|---|---|---|---|---|---|---|---|
| 500 | 3.8 MB | 7.6 MB | 7.8 kB | 1.5 MB | 12.9 MB | **5.3 MB** | 2.44× |
| 2,000 | 61.0 MB | 122.1 MB | 31.3 kB | 23.3 MB | 206.4 MB | **84.4 MB** | 2.45× |
| 5,000 | 381.5 MB | 763.0 MB | 78.1 kB | 145.6 MB | 1,290.0 MB | **527.2 MB** | 2.45× |
| 10,000 | 1.49 GB | 2.98 GB | 156.3 kB | 582.3 MB | 5.04 GB | **2.06 GB** | 2.45× |

**2.45×, flat**, for the reason P1's 3.39× was flat: every term is O(N²) with a fixed coefficient.
All three ceiling refusals go through `PlanarSystem.ResidentPhrase` as before and now quote **527 MB**
at the ceiling in place of 1,290. P1's own multiplier over the bare 16·N² the refusals printed before
that phase falls from 3.39× to **1.39×**.

### Table 3 — memory, measured

Around the FACTORISATION alone, on identical copies of one filled Z, with the baseline taken
immediately before the copy. **Scoping matters and this table is where P1's own trap was paid
again**: a first version measured a whole frequency point (cores → fill → factor) cumulatively and
read a **negative 188 MB** at the largest rung, because the large object heap hands a released
matrix's committed space to the next allocation instead of giving it back. Scoped to one
factorisation, it is exact to the last megabyte.

| N | live, LDLᵀ | live, general LU | ratio | 16·N² | allocated, LDLᵀ | allocated, LU |
|---|---|---|---|---|---|---|
| 552 | 5.3 MB | 14.0 MB | 2.6× | 4.6 MB | 0.9 MB | 9.3 MB |
| 1,980 | 59.9 MB | 179.5 MB | 3.0× | 59.8 MB | 0.2 MB | 119.7 MB |
| 4,836 | **357.0 MB** | **1,070.6 MB** | **3.0×** | 356.9 MB | 0.6 MB | 713.8 MB |

**The two large rungs land on 1× and exactly 3× the matrix** — the in-place factorisation leaves the
matrix it consumed and nothing else; the LU leaves the matrix plus two further full ones. The
smallest rung's 2.6× is the same statement with a megabyte of test-host noise on it.

The ALLOCATION counter is reported beside the live one and is the more striking of the two — the LU
allocates 2 × 16·N² where the in-place form allocates a length-N diagonal — but it is
**process-wide and counts every thread**, so the LDLᵀ column picks up the trailing update's own
`Parallel.For` task objects and whatever else the host allocated in that window: 0.2 to 0.9 MB,
**independent of N**, which is what says it is noise rather than the factorisation. It is therefore
reported and not gated on.

### Accuracy — milestone 2, and where the gate had to change shape

`x` against NumFlat's LU solution on the same Z, and `‖Zx − b‖/‖b‖` for both. The coarse mesh
throughout: the factorisation is a property of the MATRIX and cannot tell a coarse mesh's Z from a
shipping mesh's, so a finer mesh is a bigger matrix rather than a harder question (`PlanarLineFixtures`'
own two-tier note makes exactly this argument for the port algebra).

| fixture | N | \|x − x_LU\|/\|x_LU\| | residual LDLᵀ | residual LU | growth max\|L\| | min\|D\|/max\|Z\| |
|---|---|---|---|---|---|---|
| FR-4 hero 2.9 × 20 mm, 10 GHz | 94 | 1.07e-14 | 1.14e-14 | 7.52e-15 | 4.45 | 1.72e-1 |
| FR-4 line 80 mm, 10 GHz | 388 | 1.64e-14 | 2.23e-14 | 1.30e-14 | 4.45 | 1.72e-1 |
| FR-4 taper 2.9 → 0.5 mm, 20 mm | 1,278 | 5.42e-13 | 6.56e-13 | 3.34e-13 | 1.49 | 8.96e-3 |
| **FR-4 hero at 20 GHz** (remainder-stressed) | 247 | 1.01e-14 | 1.46e-14 | 7.10e-15 | 1.69 | 3.89e-1 |
| **FR-4 hero at 120 MHz** (`Dcim.CanFitAtFrequency`'s floor + 20%) | 24 | 1.49e-12 | 5.47e-12 | 3.81e-12 | 1.00 | 1.63e-4 |

**The brief's 1e-8 stop is not approached anywhere — the worst residual on any fixture is 5.5e-12,
three and a half decades inside it — so no pivoted alternative is needed and none was built.**

**But the brief's 1e-12 residual gate cannot be applied to the two stress fixtures, and the reason is
the fixture rather than the factorisation.** At 120 MHz the GENERAL LU's own residual on the same
matrix is 3.81e-12. That is the MPIE's low-frequency breakdown — the vector term vanishing like ω
against a scalar term growing like 1/ω — and it is a property of Z that a pivoted factorisation
misses by the same margin. Holding an unpivoted LDLᵀ to a bound the pivoted reference also misses
would be measuring the fixture, which is the oracle trap this area has now paid for ten times. So
the gate is in two parts: the three SERIES fixtures keep the brief's 1e-10 relative / 1e-12 residual
verbatim, and every fixture including the two stressed ones is held to **1e-8 absolute AND to within
10× of the general LU's own residual on the same matrix** — which is the question the brief is
actually asking. The measured ratio is **1.4–2.0× on all five**.

`GrowthFactor` (max |L| over the strict lower triangle) is the matrix-free half of the answer and it
is the reassuring one: **1.0 to 4.5 across every fixture**, where element growth is exactly what
pivoting exists to bound. It costs one O(N²) pass and it survives the matrix, so it is available on
runs where the residual is not.

### Milestone 4 — the published s-parameters

De-embedded and renormalised S — the answer that ships, through two calibration standards per port
cross-section, the T-matrix cascade, both branch resolutions and the renormalisation — against the
same run with `UseSymmetricFactorization = false`, 5 points, worst |ΔS| over every entry:

| fixture | N | worst \|ΔS\| |
|---|---|---|
| FR-4 hero 20 mm | 94 | 1.04e-14 |
| FR-4 line 80 mm | 388 | 3.56e-14 |
| FR-4 taper 2.9 → 1.5 mm, 20 mm | 392 | 7.36e-13 |

Against the brief's **1e-9 absolute**, with three decades to spare on the worst of them. The taper is
2.9 → 1.5 mm rather than the series' own 2.9 → 0.5: the narrow end sets the transverse pitch, so the
shipped taper meshes to N = 1,278 even coarse and a five-point de-embedded sweep of it costs 19 s
twice over. What this gate needs from a taper is its oblique flanks, and a 2:1 taper has them.

### The whole-sweep digest, and why it was not simply re-pinned

`PlanarP2MemoryWinsTests.P2_6` hashes the published S of a 5-point de-embedded sweep against a
literal, and P4 and P5 each re-pinned it when they moved the last bits. P7 moves them too, by
construction. **A bare re-pin would prove nothing** — it accepts any change, including a real one —
so the test now runs the SAME sweep twice, once through each factorisation, and asserts:

- through NumFlat's general LU: `713EFE3B…D0D4FE0B`, **P5's own literal, bit for bit**;
- through the shipped LDLᵀ: `D788322C…6A25BA27`.

The first is the load-bearing one. It says P7 moved the factorisation and **nothing else** — not the
fill, not the cores, not the calibration, not the peel, not the renormalisation — which is a stronger
statement than any tolerance on the second.

### Milestone 5 — R17 re-asked, and NOT acted on

**1 GB of resident peak now buys N = 6,968, against 4,454 with the general LU — 1.56× the unknowns.**
The sentence that would change, written out and deliberately not applied:

> `SurfaceMesher.UnknownCeiling` | 5,000 → 6,968 | R17's per-mesh N ceiling for the DENSE path, at
> the same 1 GB the 5,000 was sized against.

**What has NOT been measured, and would have to be first:** the FILL's own wall clock at that N (the
factorisation is 3.59 s at 4,836 and scales as N³, so ~10 s at 6,968 — but the fill and the once-per-
mesh core build are the other half and neither was re-timed there), and whether a mesh that large is
accurate enough to be worth running. Memory stopped being what binds; time has not been re-asked.
`PlanarP7SymmetricFactorTests.P7_9` prints all of this and asserts the constant is still 5,000, so a
well-meaning follow-up cannot move it by accident.

### What was changed in code

- **`SymmetricFactorization.cs`** (new). In-place blocked LDLᵀ for a complex-symmetric matrix;
  `Factor(Mat<Complex>, PlanarFillSettings?, blockSize)`, `Solve` for one and for P right-hand sides,
  `Residual(z, x, b)` as a static taking the original matrix, and the two matrix-free instruments
  `GrowthFactor` / `SmallestPivotRatio`. NumFlat's `Mat<T>` is column-major with element (i,j) at
  `memory[i + j·stride]` — verified directly, and it is why every inner loop runs over `i`.
- **`PlanarFillSettings.UseSymmetricFactorization`** (default true) and
  **`TrackFactorizationResidual`** (default false), both init-only outside the positional list for
  the reason `Aim` is: they select a solver and a diagnostic, not a quantity.
- **`PlanarSystem`** now holds its matrix privately and **throws from `Matrix` once factored** — the
  factorisation is in place and the upper triangle survives it untouched, so a stale read would
  return plausible numbers from the wrong frequency. `Factor()` replaces `_ = system.Lu` as the way
  to force and time it; `Lu` stays and refuses by name on the symmetric path. `Wrap` takes optional
  settings and does NOT copy.
- **`IPlanarOperator` gained a default `Solve(IReadOnlyList<Vec<Complex>>)`** — the per-vector loop
  every operator did — which `PlanarSystem` overrides with a substitution that reads each column of
  L once for all P vectors. `PlanarExcitation.Solve` makes one call instead of P.
- `MatrixBytes` / `CoreBytes` unchanged; `FactorBytes` now means the shipped path and
  `LuFactorBytes` is the old body under its own name.

### What P7 deliberately did NOT do

- **No native BLAS.** Root `CLAUDE.md` requires asking first, and the brief forbids it outright.
- **No pivoting.** Nothing measured came within three decades of the brief's stop, and Bunch–Kaufman
  stays the named follow-up rather than a silent fallback — `FactorPanel` refuses by name on an
  exactly zero pivot and points at the setting.
- **No packed storage.** Packing the lower triangle would halve the steady state to 8·N², but the
  matrix arrives as a full N×N array from the fill and packing it needs a transient 24·N² — a HIGHER
  peak than the 16·N² in-place form, and the peak is what a refusal is about. Building the fill
  packed is a fill change, which the brief forbids.
- **No vectorisation of the inner loop.** The trailing update is scalar managed C# and NumFlat's
  native LU still beats it per flop; a `Vector128`/`Vector256` complex multiply-add and destination-
  column register blocking are the obvious next 2–4×. Recorded for a later phase — the brief's own
  target was already met by 11.5×.
- **`PlanarDeembed.StaticCapacitance` and `ChargeSolver` are untouched**, though both factor a
  symmetric matrix over CELLS and would take the same treatment. Out of the brief's scope, and worth
  a line in a later one: the de-embedding path's own dense solve is now the only general LU left on a
  planar run.
- **The mirror is still written.** `MirrorLowerToUpper` copies the lower triangle to the upper on
  every fill, and the factorisation reads only the lower one. Nothing else on the dense path reads
  the upper triangle either, so that O(N²) write is now arguably dead — but `PlanarEntryFill`, the
  AIM path and several tests do read `Z[i,j]` for arbitrary (i,j), and establishing that none of
  them needs it is its own piece of work.

# P8 — a physical floor on AIM's near radius (brief-em-p8-aim-near-radius-floor.md, 2026-08-29)

**The whole change is one `Math.Max`.** `PlanarAimGeometry.NearRadiusM` was
`NearRadiusFactor · maxSpan`; it is now `max(NearRadiusFactor · maxSpan, NearRadiusMinM)`, and
`NearRadiusMinM` defaults to **2·h**, the depth of the ground plane's image.

## Why a radius measured in supports had to break, and where

`brief-em-aim-ceiling.md`'s A1b ladder — refine the RESOLUTION at a fixed 64 mm footprint — climbed
GMRES 21 → 143 → 372 iterations over cells/λ 80 → 120 and then did not converge at all at 140. A1a,
the LENGTH ladder at the shipping resolution, was flat over the same N range. Two ladders, two
stories, and the difference is a unit:

`NearRadiusFactor` is **6 largest basis supports**. Refining the mesh at a fixed footprint shrinks the
largest support, so the exact near field shrinks IN METRES while the geometry it sits on does not.
Measured on the FR-4 hero cross-section (2.9 mm on 1.6 mm FR-4, 6 GHz), and **independent of the
board's length to four figures** — 8.92 h at 64 mm and 8.93 h at 16, 24 and 32 mm:

| cells/λ | largest support | near radius | in units of h |
|---|---|---|---|
| 20 | 2.380 mm | 14.28 mm | **8.93** |
| 40 | 1.190 mm | 7.14 mm | 4.46 |
| 60 | 0.793 mm | 4.76 mm | 2.98 |
| 80 | 0.595 mm | 3.57 mm | 2.23 |
| 100 | 0.476 mm | 2.85 mm | 1.78 |
| 120 | 0.397 mm | 2.38 mm | 1.49 |
| 140 | 0.340 mm | 2.04 mm | **1.28** |

The scalar (charge) kernel over a grounded slab is `1/ρ − 1/√(ρ² + 4h²)` plus smooth terms: the charge
and its image very nearly cancel, and past ρ ≈ 2h what is left falls like `2h²/ρ³` rather than like
`1/ρ`. **2h is where the coupling stops being long-ranged.** A near-field preconditioner narrower than
that is missing the dominant coupling — and the table above is a ladder walking straight through it.
The iteration climb starts at cells/λ = 80, which is the first rung under 2.5 h.

## M1 — the failing ladder, reproduced exactly

A1b's construction on the P8 tree with the floor OFF (`NearRadiusMinM: 0`), Release, one port's RHS.
`nearNNZ` is the near matrix, `factorNNZ` the sparse LU's own fill-in (P1's field), MB
`PlanarAimReport.ResidentBytes`:

| cells/λ | N | near/row | nearNNZ | factorNNZ | R/h | build s | iters | resid | solve s | MB |
|---|---|---|---|---|---|---|---|---|---|---|
| 20 | 994 | 365 | 363,102 | 673,206 | 8.92 | 1.0 | 5 | 1.2e-10 | 0.02 | 22 |
| 40 | 1,895 | 379 | 718,159 | 754,343 | 4.46 | 0.3 | 5 | 5.7e-9 | 0.01 | 31 |
| 60 | 3,454 | 465 | 1,605,470 | 1,720,650 | 2.98 | 0.6 | 8 | 5.4e-9 | 0.04 | 69 |
| 80 | 5,437 | 539 | 2,929,671 | 3,241,905 | 2.23 | 1.2 | 21 | 1.0e-8 | 0.18 | 124 |
| 100 | 7,873 | 597 | 4,701,995 | 5,465,368 | 1.78 | 2.2 | **143** | 9.8e-9 | 1.89 | 205 |
| 120 | 10,708 | 630 | 6,746,950 | 8,519,323 | 1.49 | 3.6 | **372** | 9.9e-9 | 7.42 | 303 |
| 140 | 13,967 | 656 | 9,163,153 | 12,537,093 | 1.28 | 5.7 | **400** | **9.1e-5** | 10.88 | 426 |

Iteration counts and near/row reproduce `brief-em-aim-ceiling.md`'s own A1b table to the digit
(5, 5, 8, 21, 143, 372, 400 and 365 … 656), so this is the same ladder and not a lookalike. The build
and solve seconds are much lower than that table's because P6 moved the geometry and the near cores
out of the per-frequency build, and because this is Release rather than a test host.

**`factorNNZ` is non-zero on every rung, including the one that fails.** That rules out the obvious
alternative explanation — a preconditioner that silently failed to factor, leaving GMRES unpreconditioned.
The factorisation succeeds throughout; it is simply a factorisation of the wrong operator's near field.

## M2 — the floor, and the same ladder with it

`NearRadiusMinM = 2h` (the shipped default). Rungs 20 through 80 carry the identical near set,
factorisation and answer — the floor is 3.2 mm and their radius is 3.57 mm and up, so it does not bind
there at all; only the seconds jitter:

| cells/λ | N | near/row | nearNNZ | factorNNZ | R/h | build s | iters | resid | solve s | MB |
|---|---|---|---|---|---|---|---|---|---|---|
| 20 | 994 | 365 | 363,102 | 673,206 | 8.92 | 1.0 | 5 | 1.2e-10 | 0.02 | 22 |
| 40 | 1,895 | 379 | 718,159 | 754,343 | 4.46 | 0.3 | 5 | 5.7e-9 | 0.01 | 31 |
| 60 | 3,454 | 465 | 1,605,470 | 1,720,650 | 2.98 | 0.7 | 8 | 5.4e-9 | 0.04 | 69 |
| 80 | 5,437 | 539 | 2,929,671 | 3,241,905 | 2.23 | 1.2 | 21 | 1.0e-8 | 0.18 | 124 |
| 100 | 7,873 | 690 | 5,435,173 | 5,939,938 | **2.00** | 2.4 | **28** | 6.8e-9 | 0.40 | 228 |
| 120 | 10,708 | 951 | 10,180,244 | 11,458,849 | **2.00** | 6.2 | **36** | 8.0e-9 | 0.89 | 426 |
| 140 | 13,967 | 1,246 | 17,399,109 | — | **2.00** | **did not finish** | — | — | — | — |

**The blow-up is gone where the rung can be built at all**: 143 → 28 and 372 → 36. It is not perfectly
flat, and the residual climb is attributable: the peak of the floored ladder is the cells/λ = 80 rung
at 21 iterations, where the radius is 2.23 h and **the floor does not bind at all**. Everything the
floor reaches sits below that peak.

**The top rung swapped one wall for another, and this is the P8 finding that matters most.** At
cells/λ = 140 the floored near set is **8.9% dense at N = 13,967** (near/row 656 → 1,246, 9.2M → 17.4M
entries) and **CSparse's sparse LU over it had not returned after 8 minutes of CPU and 1.43 GB of
resident memory still growing**, where the unfloored one factors in 5.0 s. It was stopped there, so 8
minutes is a lower bound and not a measurement. It is not a convergence failure any more; it is a
preconditioner that cannot be built. The geometry itself is unbothered (0.6 s), so this is `FactorNear`
alone — `SparseLU.Create(csc, AMD(…), 1.0)` is an EXACT sparse LU with full partial pivoting, and its
fill-in is superlinear in the near set's density. The same density at N = 10,708 factors in 5.3 s with
1.13× fill-in, so the wall is between those two rungs and it is sharp.

This is the one rung of the ladder that is past `AcceleratedUnknownCeiling = 12,000` and therefore
unreachable through the shipped API — which is why it is a note here rather than a defect. It is also
why `AimCeilingTests.A1_LadderByResolution` is now **pinned to the pre-P8 radius**: that test exists to
record the "before", and left on the shipped default its top rung would never finish.

### The 16 mm ladder, which is where this was actually developed — and it IS flat

The pathology is set by the cell size against h, not by the footprint (see the R/h table above: it is
the same at every length). So the same ladder at 16 mm reproduces it faithfully at a fifth of the cost,
and its top rung stays under the DENSE ceiling, which is what makes M4's `|ΔI|` measurable at all.
This is the fixture `PlanarP8NearRadiusFloorTests.P8_5` runs:

| cells/λ | N | R/h off → on | near/row off → on | **iters off → on** | resid off | resid on |
|---|---|---|---|---|---|---|
| 20 | 314 | 8.93 → 8.93 | 290 → 290 | 2 → 2 | 1.4e-9 | 1.4e-9 |
| 40 | 518 | 4.46 → 4.46 | 327 → 327 | 4 → 4 | 1.7e-10 | 1.7e-10 |
| 60 | 913 | 2.97 → 2.97 | 421 → 421 | 6 → 6 | 5.3e-9 | 5.3e-9 |
| 80 | 1,412 | 2.23 → 2.23 | 501 → 501 | 12 → 12 | 4.1e-9 | 4.1e-9 |
| 100 | 2,015 | 1.78 → **2.00** | 563 → 646 | **46 → 14** | 7.3e-9 | 5.1e-9 |
| 120 | 2,722 | 1.49 → **2.00** | 600 → 888 | **144 → 10** | 9.1e-9 | 9.2e-9 |
| 140 | 3,533 | 1.28 → **2.00** | 629 → 1,160 | **273 → 10** | 9.8e-9 | 9.8e-9 |

**2, 4, 6, 12, 14, 10, 10** — flat, in the sense A1a's length ladder is flat. `4h` was therefore never
tried: the brief's own stopping rule is "flat at 2h, stop", and 2h is where the physics puts it.

## M3 — what the floor costs, and where it costs nothing

**On the LENGTH ladder it changes NOTHING, and that is asserted rather than argued.** At the shipping
cells/λ = 20 the radius is 8.9 h at every length, so the floor is unreachable. Measured at 6 GHz,
cells/λ = 20, floor off and on:

| L | N | near/row | nearNNZ | factorNNZ | R/h | build s | iters | resid | MB |
|---|---|---|---|---|---|---|---|---|---|
| 64 mm | 994 | 365 | 363,102 | 673,206 | 8.92 | 1.0 | 5 | 1.2e-10 | 22 |
| 128 mm | 1,912 | 383 | 732,414 | 790,873 | 8.93 | 0.4 | 5 | 1.6e-9 | 32 |
| 256 mm | 3,731 | 392 | 1,461,553 | 1,518,133 | 8.93 | 0.5 | 6 | 1.2e-9 | 61 |
| 384 mm | 5,584 | 396 | 2,208,760 | 2,288,258 | 8.93 | 0.8 | 6 | 6.1e-9 | 92 |

Every column above is identical with the floor and without — one table, not two.
`PlanarP8NearRadiusFloorTests.P8_2` asserts it pair by pair (`IsNear(i,j)` over the whole N×N, not
merely equal entry counts) at 16, 32 and 64 mm, and then asserts the solved current is bit-identical.

**And it binds on NO mesh either starter technology produces at a shipped resolution** — which is the
question that decides whether P8 changes an answer a user already has. It does not:

| technology | h | conductor | fixture | cells/λ | N | radius from supports | floor | R/h | floored? |
|---|---|---|---|---|---|---|---|---|---|
| FR-4 | 1.6 mm | 2.9 mm | 20 mm at 10 GHz | 20 | 552 | 8,538.7 µm | 3,200 µm | 5.34 | no |
| FR-4 | 1.6 mm | 2.9 mm | 20 mm at 10 GHz | 40 | 1,345 | 4,286.1 µm | 3,200 µm | 2.68 | no |
| FR-4 | 1.6 mm | 2.9 mm | 64 mm at 6 GHz | 20 | 994 | 14,278.5 µm | 3,200 µm | 8.92 | no |
| FR-4 | 1.6 mm | 2.9 mm | 64 mm at 6 GHz | 40 | 1,895 | 7,137.1 µm | 3,200 µm | 4.46 | no |
| GaAs | 100 µm | 72 µm | 2 mm at 10 GHz | 20 | 705 | 5,003.1 µm | 200 µm | **50.03** | no |
| GaAs | 100 µm | 72 µm | 2 mm at 40 GHz | 20 | 654 | 1,251.3 µm | 200 µm | 12.51 | no |
| GaAs | 100 µm | 72 µm | 2 mm at 40 GHz | 40 | 756 | 626.0 µm | 200 µm | 6.26 | no |
| GaAs | 100 µm | 72 µm | 1 mm at 60 GHz | 40 | 569 | 416.6 µm | 200 µm | 4.17 | no |

**The MMIC case is not close and could not be**: a 72 µm conductor on a 100 µm slab has cells that are
large against h by construction, so its near radius is tens of image depths even at 60 GHz. It is the
PCB case, where the conductor is nearly twice the slab height, that can be walked into the floor — and
only by asking for a resolution several times past the default.

**Where it does bind, it is a NET WIN at scale and a net loss on a stubby board**, and the difference
is the sparse LU's fill-in rather than the near set:

| fixture | build+solve off | build+solve on | nearNNZ → | factorNNZ → | fill-in ratio |
|---|---|---|---|---|---|
| 64 mm, cells/λ = 120 (N = 10,708) | 3.6 + 7.4 = **11.0 s** | 6.2 + 0.9 = **7.1 s** | 6.7M → 10.2M | 8.5M → 11.5M | 1.13× |
| 16 mm, cells/λ = 140 (N = 3,533) | 9.1 + 3.0 = **12.1 s** | 18.8 + 0.2 = **19.0 s** | 2.2M → 4.1M | 8.4M → 12.0M | **2.93×** |

On the 16 mm board the 3.2 mm floor spans the whole 2.9 mm width and a fifth of the length, so the
"sparse" near matrix is a wide band on a small N and CSparse's LU fills in nearly 3×. That is a
short-board artefact, not the general shape: at 64 mm the same radius is a small part of the extent and
the floor pays for itself several times over. The memory price is real either way — 303 → 426 MB at
N = 10,708.

## M4 — accuracy, against the dense solve

`|ΔI|`, the relative 2-norm of the solved current against the DENSE LU on the identical mesh (16 mm, so
the top rung is N = 3,533 and a dense reference still exists):

| cells/λ | N | R/h off → on | **\|ΔI\| off** | **\|ΔI\| on** |
|---|---|---|---|---|
| 20 | 314 | 8.93 → 8.93 | 4.90e-7 | 4.90e-7 |
| 100 | 2,015 | 1.78 → 2.00 | 1.58e-4 | 4.64e-5 |
| 140 | 3,533 | 1.28 → 2.00 | **5.93e-4** | **3.58e-5** |

**The brief's premise was half right and the measurement corrects it.** It said "nothing about the AIM
projection's accuracy is at stake — the preconditioner is what degraded". The preconditioner is indeed
what makes GMRES climb, but the near radius is also the boundary between the entries computed EXACTLY
and the entries the projection approximates, so shrinking it degrades the OPERATOR too: 4.9e-7 at the
shipping mesh becomes 5.9e-4 seven times finer, a 1,200× loss that the floor recovers 17× of. Both
numbers converge at the same GMRES residual (9.8e-9), so this is the operator and not the stopping
rule. It remains true that the accelerator is not the error budget at the shipping resolution.

## M5 — re-asking the ceiling: the number does not move here, and the QUESTION has changed

`SurfaceMesher.AcceleratedUnknownCeiling = 12,000` was set at "the top of A1a's healthy rung with
margin under A1b's failing one (13,967)". After P8:

- **A1a, the healthy construction, is untouched** — the floor never binds there, so its own basis for
  12,000 (flat near/row, flat iterations, linear memory to N = 12,894 at 188 MB) stands exactly as
  measured.
- **A1b no longer fails to converge below 12,000**, so the margin argument that set 12,000 has lost
  its subject. Every rung inside the ceiling now converges in 36 iterations or fewer.
- **But N stopped predicting the working set on a refined mesh.** At N = 10,708 the accelerator holds
  426 MB with the floor against 303 MB without, and its build peaks at 620 MB — against the 188 MB
  §8 quotes at N = 12,894 on the length ladder. Same N, 2.3× the memory, because near/row is 951
  instead of 399.
- **And the first rung past the ceiling is where the preconditioner stops being buildable at all**
  (above): N = 13,967, 8.9% near density, no factorisation after 8 minutes of CPU. The old failure was
  a GMRES that did not converge and threw; the new one would be a build that does not return.

**The recommendation is therefore to LEAVE 12,000 where it is, and the reason has changed** — it is no
longer "with margin under a ladder that fails to converge", it is "with margin under the density at
which the near field's exact LU stops factoring". The sentence that would move it, written out and not
applied:

> `SurfaceMesher.AcceleratedUnknownCeiling` could move above 12,000 only together with a check on the
> NEAR SET rather than on N — near entries per row, or the near matrix's density — because with a
> radius floor those are what bound the build, and a fine mesh at 14,000 unknowns has a near matrix
> the exact sparse LU does not factor in useful time where a coarse one at the same N factors in
> seconds.

Left alone here on purpose. Moving it is a second decision, and the thing that would have to carry it
is a density/working-set guard on the accelerated path, not a bigger integer.

## Beside the brief: where dense and accelerated actually cross now (asked by the owner, 2026-08-29)

The owner asked whether the accelerator should be ON by default. That turns on one number the tree
still states from before P6 and P7 — `EmSetupModel.AcceleratedSolve`'s own doc comment says "the win is
memory, not time, and the time crossover is much later, around N ≈ 3,700". **P6 moved the geometry and
the near cores out of the per-frequency build, and P7 made the DENSE factorisation 11.5× faster; both
sides moved, so it was re-measured.** Everything a user waits for at ONE frequency and one port —
cores, fill, factor, solve — on the shipping mesh at 6 GHz, Release:

| L | N | dense cores | dense fill+solve | **dense total** | dense MB | AIM geom | AIM build | AIM solve | **AIM total** | AIM MB | AIM/dense |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 8 mm | 195 | 0.13 | 0.17 | **0.30 s** | 1 | 0.15 | 0.22 | 0.01 | **0.37 s** | 3 | 1.24× |
| 20 mm | 365 | 0.12 | 0.16 | **0.28 s** | 3 | 0.28 | 0.25 | 0.01 | **0.53 s** | 7 | 1.90× |
| 64 mm | 994 | 0.10 | 0.14 | **0.24 s** | 21 | 0.11 | 0.40 | 0.01 | **0.53 s** | 22 | 2.19× |
| 128 mm | 1,912 | 0.13 | 0.35 | **0.48 s** | 78 | 0.12 | 0.30 | 0.01 | **0.43 s** | 32 | 0.91× |
| 200 mm | 2,932 | 0.13 | 0.95 | **1.08 s** | 183 | 0.11 | 0.42 | 0.02 | **0.55 s** | 48 | 0.51× |
| 256 mm | 3,731 | 0.16 | 1.77 | **1.93 s** | 296 | 0.13 | 0.52 | 0.03 | **0.67 s** | 61 | 0.35× |

**The crossover is now N ≈ 1,900, not 3,700**, and past it the accelerator wins on BOTH axes (2.9× the
speed and 4.9× less memory at N = 3,731). Below it the accelerator costs up to **2.19×** the time for a
memory saving that does not exist yet — 22 MB against 21 MB at N = 994 — because the near set is most
of the matrix at small N and the FFT grid is charged in full regardless. The doc comment's number is
stale and its CONCLUSION ("off by default") is unaffected by the correction; both are recorded here
rather than edited, because changing a shipped default is the owner's call and P8 was not briefed to
make it.

## What changed in the code

- `PlanarAimSettings` gained **`double? NearRadiusMinM = null`** — null derives `2·h`
  (`DerivedNearRadiusImageDepths`, named rather than a literal because it is the image depth), a
  positive value overrides, **0 disables the floor** and is the pre-P8 behaviour every "off" column
  above was measured with. `NearRadiusFloorFor(h)` is the one place the derivation lives.
- `PlanarAimGeometry.Build` **requires the slab height**, and refuses a non-positive one by name. It is
  not defaulted, because the bad case is silent: a geometry built with no h takes the pre-P8 radius and
  returns a complete, plausible answer that merely takes 20× as long to reach. The compiler found all
  six call sites. `PlanarSolveContext` takes it as a trailing optional (the dense path has no near
  radius to floor) and throws when `Fill.Aim` is set without one.
- The geometry publishes `NearRadiusM`, `NearRadiusFromSupportM`, `NearRadiusFloorM`,
  `NearRadiusIsFloored` and `SlabHeightM`; `PlanarAimReport` carries the first three, so a ladder table
  is printable from the report.
- `PlanarAimOperator.Build(cores, …)`'s one-frequency overload gained `slabHeightM` before `settings`,
  which is what turned every stale call site into a compile error rather than a silent behaviour change.
- **Nothing else moved**: not the projection order, not the pitch rule (`GridSpacingFactor` is still
  sized from the largest support — the stencil's job is to resolve the kernel across its own width and
  has nothing to do with the slab), not the preconditioner's factorisation, not
  `AcceleratedUnknownCeiling`.

`AimAcceleratorTests.T2` is the one existing test that had to opt OUT (`NearRadiusMinM: 0`): it sets
`NearRadiusFactor: 0` deliberately so the radius criterion cannot mask stencil overlap, and a floor in
metres would have re-widened it and made the test vacuous.

---

# P9 — should adaptive frequency sampling be on by default? (brief-em-p9-adaptive-sweep-default.md, 2026-08-29)

**A decision brief. Nothing was flipped.** Every table below was taken with a standalone Release
harness, one fixture at a time, never alongside another benchmark — HISTORY's own 2× distortion
warning. The mesh is deliberately **coarse (cells/λ = 10, edge mesh off)** throughout: the quantity
under study is the SHAPE of S(f), which is what decides where refinement goes and how well the
interpolant covers the gaps. Mesh density sets the COST of a point; it does not change that shape's
spectral content, so it does not change the decision. Every sweep is de-embedded, as a real run is.

**The brief's premise was two days stale, and it matters.** It says the feature "ships
`PlanarSolveSettings.Adaptive = null` — off". That is still true of the ENGINE default, but the
shipping USER default was flipped on 2026-08-26 (`An EM setup now runs from the command line`):
`EmSetupModel.AdaptiveSampling = true`, and `EmRunService` turns that into
`PlanarAdaptiveSettings.Default`. So the question this brief asks was already answered in the
affirmative for every run a user starts; what follows measures whether that was right, and what it
actually buys.

## M1 — what the default DOES, on the EM panel's own default grid

1–20 GHz, **101 points**, de-embedded — the panel's shipped frequency spec.
`adj |ΔS|` is the largest entry-wise difference between **adjacent points of the requested grid** on
the fully-solved curve; it is the explanatory number, and the reason it is in the same table as the
result is that it predicts the result.

| fixture | N | adj \|ΔS\| median | full solve | tol | solved/101 | speed-up | worst stopped | max \|ΔS\| vs full |
|---|---|---|---|---|---|---|---|---|
| FR-4 hero line, 20 mm | 94 | 1.29e-1 | 4.25 s | 1e-2 | 39 | **2.94×** | 9.70e-3 | 2.56e-3 |
| | | | | **1e-3** | **68** | **1.73×** | 3.65e-3 | 1.42e-3 |
| | | | | 1e-4 | 85 | 1.38× | 3.65e-3 | 1.80e-4 |
| FR-4 line, 256 mm | 752 | 6.34e-1 | 7.85 s | 1e-2 | 101 | **0.99×** | 1.01e+0 | 0 |
| | | | | **1e-3** | **101** | **0.99×** | 1.01e+0 | 0 |
| | | | | 1e-4 | 101 | 0.99× | 1.01e+0 | 0 |
| taper 60 mm, 2.9→0.5 mm | 1,278 | 3.36e-1 | 15.63 s | 1e-2 | 101 | 0.99× | 8.69e-2 | 0 |
| | | | | **1e-3** | **101** | **0.99×** | 8.69e-2 | 0 |
| | | | | 1e-4 | 101 | 0.99× | 8.69e-2 | 0 |
| open stub (λ_g/4 notch) | 304 | 1.73e-1 | 4.48 s | 1e-2 | 70 | 1.45× | 7.13e-3 | 1.03e-3 |
| | | | | **1e-3** | **87** | **1.16×** | 5.78e-3 | 3.99e-4 |
| | | | | 1e-4 | 101 | 1.00× | 5.78e-3 | 0 |
| coupled λ_g/2 resonator | 160 | 2.95e-1 | 4.22 s | 1e-2 | 89 | 1.14× | 4.18e-1 | 1.06e-3 |
| | | | | **1e-3** | **101** | **1.00×** | 4.18e-1 | 0 |
| | | | | 1e-4 | 101 | 1.00× | 4.18e-1 | 0 |

**Read the `0` in the last column as "solved every point", not as "more accurate"** — L9e's own
warning, and three of the five fixtures land there at the default tolerance.

**On the shipped default grid, at the shipped default tolerance, the saving is between nothing and
1.73×.** §10.7's "typically cuts solve count by 5–10×" is not what this grid gets on any fixture
measured. The adjacent-|ΔS| column says why in one number: consecutive points of the requested grid
already differ by **0.13 to 0.63**, i.e. **130× to 630× the tolerance**. Adaptive sampling can skip a
point only when the interpolant predicts it to within the tolerance; a grid whose neighbours are that
far apart is not oversampled at all with respect to a 1e-3 criterion, and there is nothing to skip.

**Where the disagreement is huge, the run says so.** `WorstAdaptiveDisagreement` reads **1.01** on the
256 mm line — near the largest an s-parameter difference can be. That is refinement reporting "I ran
out of grid", not "I converged", and it is already published on `PlanarSolveResult` and in the run's
own note.

## M1b — the saving is a function of how OVERSAMPLED the requested grid is

Same fixtures, same band, more points. This is the measurement that reconciles §10.7 with M1.

| fixture / grid | step | adj \|ΔS\| median | tol | solved | speed-up | max \|ΔS\| vs full |
|---|---|---|---|---|---|---|
| hero, 1–20 GHz, 101 pts | 190 MHz | 1.29e-1 | 1e-3 | 68/101 | 1.73× | 1.42e-3 |
| hero, 1–20 GHz, **201 pts** | 95 MHz | 6.48e-2 | 1e-2 | 39/201 | 5.33× | 2.36e-3 |
| | | | **1e-3** | **77/201** | **2.80×** | 7.75e-4 |
| | | | 1e-4 | 134/201 | 1.61× | 4.49e-4 |
| hero, 1–20 GHz, **401 pts** | 48 MHz | 3.24e-2 | 1e-2 | 39/401 | 10.16× | 2.88e-3 |
| | | | **1e-3** | **81/401** | **4.94×** | 1.25e-3 |
| | | | 1e-4 | 170/401 | 2.33× | 7.09e-4 |
| hero, **1–5 GHz**, 101 pts | 40 MHz | 2.62e-2 | 1e-3 | 29/101 | 3.48× | 2.37e-4 |
| | | | 1e-4 | 49/101 | 2.06× | 9.78e-5 |
| taper 60 mm, 1–20 GHz, 201 pts | 95 MHz | 1.68e-1 | 1e-3 | 200/201 | 1.00× | 9.49e-4 |

**The consequence is counter-intuitive and it is measured, not argued.** On the hero, a **401-point**
adaptive sweep costs **3.01 s** and solves 81 points; a **101-point** non-adaptive sweep of the same
band costs **4.25 s** and solves 101. Four times the frequency resolution, for less time. With
adaptive sampling on, **asking for more points is close to free and asking for fewer buys almost
nothing** — the solved count is set by the structure, not by the grid. The taper row is the limit
case: at 95 MHz its neighbours still differ by 0.17, so even 201 points is not oversampled and it
solves 200 of them.

## M2 — the narrow-resonance failure mode, and what `InitialPoints` buys

The fixture is a side-coupled open-ended λ_g/2 resonator beside a 40 mm through line (resonator
13.8 mm, gap 0.6 mm, εr 4.4, tanδ 1e-5, coarse mesh, N = 160). Measured on a 5 MHz scan: the notch is
at **5.63 GHz, −7.2 dB, 80 MHz wide at half depth (Q ≈ 70)**. Sweeping tanδ over 1e-5…2e-2 and the gap
over 0.15…2.4 mm moves the depth but **not the width** — 80 MHz throughout — so that is the narrowest
feature this structure class produces at this mesh.

**First, the honest boundary: on the shipping default grid the notch is invisible to the REQUESTED
grid, adaptive or not.** 101 points over 1–20 GHz is a 190 MHz step against an 80 MHz feature.
R-adf-2 publishes exactly the grid the user asked for and never inserts a point, so **adaptive
sampling cannot rescue a resonance that falls between two requested frequencies** — that is a
property of the requested grid alone. (M1's last row shows the symptom: its deepest published |S21|
is −6.5 dB at 11.26 GHz, a different feature entirely.)

On grids that DO resolve it:

| grid | step | notch width | tol | init | solved | speed-up | max \|ΔS\| | notch error |
|---|---|---|---|---|---|---|---|---|
| 4–7 GHz, 501 pts | 6 MHz | ~13 pts | 1e-2 | 5 | 61/501 | 8.12× | 9.75e-4 | **0.0 dB** |
| | | | 1e-2 | 17 | 63/501 | 8.07× | 9.67e-4 | 0.0 dB |
| | | | 1e-2 | 65 | 137/501 | 3.71× | 1.13e-4 | 0.0 dB |
| | | | **1e-3** | **5** | **95/501** | **5.34×** | 2.53e-4 | **0.0 dB** |
| | | | 1e-3 | 17 | 95/501 | 5.32× | 2.53e-4 | 0.0 dB |
| | | | 1e-3 | 65 | 149/501 | 3.41× | 8.25e-5 | 0.0 dB |
| | | | 1e-4 | 5 | 136/501 | 3.75× | 3.53e-5 | 0.0 dB |
| 1–20 GHz, 1001 pts | 19 MHz | ~4 pts | 1e-2 | 5 | 162/1001 | 6.19× | 5.55e-3 | **0.0 dB** |
| | | | 1e-2 | 129 | 285/1001 | 3.51× | 4.77e-3 | 0.0 dB |
| | | | **1e-3** | **5** | **272/1001** | **3.67×** | **1.01e-2** | **0.0 dB** |
| | | | 1e-3 | 17 | 277/1001 | 3.59× | 5.85e-3 | 0.0 dB |
| | | | 1e-3 | 129 | 343/1001 | 2.92× | 5.85e-3 | 0.0 dB |
| | | | 1e-4 | 5 | 582/1001 | 1.72× | 1.58e-3 | 0.0 dB |

**The notch depth and frequency are recovered EXACTLY in all 33 configurations run** — every
tolerance, every seed count, both grids. No configuration was found in which the interpolant loses
the resonance.

**`InitialPoints` buys nothing and should not be raised.** From 5 to 17 seeds the realised error is
unchanged to two figures on both grids; from 33 upward the solved count climbs and the error does
not fall in proportion. Its only visible effect is cost.

**The one real accuracy caveat, and it is a magnitude caveat rather than a missed-feature one: the
tolerance is a LOCAL stopping test, not a global error bound.** On the 1001-point grid at tol 1e-3
the realised worst |ΔS| against the fully-solved answer is **1.01e-2 — ten times the number asked
for**. On the default 101-point grid the overshoot is milder (1.42e-3 against 1e-3, 1.4×), because
there refinement runs to the grid floor almost everywhere. L9e's `T1_2` already gates this at
`worst < 10 × tol`; these numbers sit right at that gate rather than comfortably inside it.

## M3 — what it costs when it saves nothing

**Time: about 1%.** The 256 mm line and the 60 mm taper both solve all 101 points under adaptive
sampling and both read **0.99×** — the overhead is the interpolant evaluations at each probe plus the
calibration replays, and the replays are cache hits by construction (R-adf-3).

**Memory: real in principle, unmeasurable at this size.** The adaptive branch keeps every solved
point's kernel, raw matrix and per-port basis-current vectors alive simultaneously, because the
calibration is replayed from a fresh branch state after every insertion; the plain loop keeps one
point's worth. The dominant term is the currents, **16·N·P bytes per solved point** — 4.1 MB at
N = 1,278 with 101 points and 2 ports, and ~16 MB at N = 5,000. Against the run's own peak managed
heap this does not show: **217 MB adaptive against 227 MB off** on the taper (polled at 25 ms, peak
`GC.GetGCMemoryInfo().HeapSizeBytes`), the difference being GC timing rather than retention. The
transient fill and factorisation dominate by two orders of magnitude.

## Why the saving is small and the resonance is never lost — the same fact, stated once

For a distributed structure the transmission phase rotates at `dφ/df = 2π√ε_eff·L/c`, so one grid
step of `Δf` already moves S by roughly that much regardless of what else is in the band. For a
resonance of quality factor Q at `f₀` to be under-resolved — narrower than about two grid steps —
the step must satisfy `Δf ≳ f₀/2Q`. At that step the background's own adjacent-point difference is

    |ΔS|_background ≈ (2π√ε_eff·L/c)·(f₀/2Q) = π·(L/λ_g)/Q

which is below a tolerance τ only when `L/λ_g < τ·Q/π`. For τ = 1e-3 and the measured Q ≈ 70 that is
`L/λ_g < 0.022` — a structure shorter than λ/45, which cannot host a distributed resonance in the
first place. **So whenever a resonance is sharp enough to fall between two solved points, the
background is already forcing refinement down to the grid floor, and the resonance gets bracketed on
the way down.** The measurements are consistent with this at every point tested; it is an argument
supported by them, not a proof.

The same sentence read the other way is M1's result: refinement is driven to the grid floor by the
background, which is exactly why so few points can be skipped.

## Two sentences elsewhere in the tree that these numbers make false

Both corrected in place with a dated note, per the series convention:

- **`docs/design/mom-engine.md` §10.7** — "rational-interpolate" (L9e's `T4_2` measured the spline
  as the winner and `PlanarAdaptiveSettings.Interpolant` defaults to `CubicSpline`) and "typically
  cuts solve count by 5–10×" (1.0×–1.7× on the shipped default grid; 5–10× needs 2–4× the points).
- **`docs/user/src/reference/mom-engine.md` §Adaptive frequency sampling** — the same "rational
  interpolant" error; a "problem it solves" framing that implies adaptive sampling rescues a notch
  falling between two samples (it cannot — it never adds a published point); and tuning advice to
  **reduce** the requested point count, which is backwards for the shipped scheme.

# P10 — is M2's fan-out starving the thread pool? (brief-em-p10-fanout-starvation.md, 2026-08-29)

**A hypothesis, tested and refuted at milestone 1. Nothing in the solver's parallelism was
changed.** Milestones 2 and 3 are conditional on the instrument showing a stall (`"If it stalls"`),
and it does not, so the shared row queue was not built.

The hypothesis: `PlanarFill.ForRows` takes a semaphore permit in `Parallel.For`'s `localInit`, so
when `PlanarFanOut` runs K solves concurrently each inner loop asks for up to `cap` workers, K·cap
pool threads are requested, and (K−1)·cap of them sit blocked in `Enter()` while the pool injects
replacements at ~1–2 per second — stalling the run for seconds at every frequency.

**Every measurement below is a standalone Release harness**, one fixture at a time, on the 10-core
box (4 performance + 6 efficiency) every other number in this file was taken on. `dotnet test` builds
Debug and would have changed the arithmetic's cost relative to the scheduling under study.

## The instrument

`PlanarParallelBudget` gained four counters, kept: `WaitingThreads` (threads parked inside `Enter()`
right now), `HeldPermits` (permits handed out right now — the fill's own progress, since a permit is
held only while a worker is doing fill arithmetic), `EnterCount` and `TotalWaitSeconds`. They are
**process-wide statics on purpose**: a run creates exactly one budget, so "any budget" and "the
budget" are the same set, and a static needs no handle on an object `PlanarSolve` owns privately.
Two interlocked adds and a `Stopwatch.GetTimestamp` pair per worker **per loop** — not per row; a
whole de-embedded point of the 80 mm line makes ~700–800 `Enter()` calls.

## Table 1 — the brief's own measurement, sampled every 100 ms

FR-4 80 mm line, 10 GHz, shipping mesh. **N = 1,980** (1,053 cells), de-embedded, standards fanned
out: **5 independent solves** — the DUT plus 4 calibration standards of N = 297 / 382 / 331 / 416.
`cap = 10 = ProcessorCount`; the pool's minimum worker count is therefore also 10. One point,
**1.31 s**.

| t (s) | pool threads | pending work items | blocked in `Enter()` | permits held (of 10) |
|---|---|---|---|---|
| 0.00 | 11 | 0 | 0 | 0 |
| 0.10 | 11 | 0 | 0 | 0 |
| 0.20 | 12 | 4 | 3 | **10** |
| 0.31 | 12 | 3 | 1 | **10** |
| 0.41 | 12 | 5 | 3 | **10** |
| 0.51 | 12 | 5 | 3 | **10** |
| 0.61 | 12 | 0 | 0 | 0 |
| 0.71 | 12 | 3 | 0 | **10** |
| 0.82 | 12 | 0 | 0 | **10** |
| 0.92 | 12 | 0 | 0 | **10** |
| 1.02 | 12 | 0 | 0 | **10** |
| 1.12 | 12 | 0 | 0 | 5 |
| 1.22 | 12 | 0 | 0 | 0 |

**Peak blocked in `Enter()`: 3.** The hypothesis predicts (K−1)·cap = **40**. Pool thread count
11 → 12 across the whole point; the pool never had to grow to let the fill reach its cap, because the
cap equals the pool's own minimum. Parked time over the point is **1.49 thread-seconds = 11.4% of the
13.1 core-seconds the budget could have spent**, and none of it is on the critical path (Table 3).

The two zero rows are not stalls: t = 0.00–0.10 is the Green's-function fit, which is sequential by
design and takes no permits (`KernelFitMs` = 0.11 s, exactly the two rows), and the later ones are
`SymmetricFactorization`'s serial panel steps between its parallel trailing updates.

## Table 2 — the same point at 20 ms, which is where the ramp is visible

61 samples. The fill reaches the cap **within one sample of starting**: permits held are 0 through
t = 0.09 (the kernel fit), **8** at 0.11, **10** at 0.13, and stay at 10 through t = 0.88 apart from
four dips to 8–9. Blocked in `Enter()` never exceeds **2**. Pending work items sit at 3–5 for the
whole fill phase.

That last number is the mechanism. **`Parallel.For` does not eagerly spawn `MaxDegreeOfParallelism`
workers.** It is a replicating task: a worker queues one replica when it starts and work remains, and
a replica becomes a thread only when the pool has one to give. So the K·cap request materialises as
**queued work items**, not as blocked threads — the brief's multiplication never happens on a pool
whose thread count is near the cap. Mean permits held over the point is 7.87/10 (**78.7%
utilisation**); the shortfall is the sequential kernel fit and the factorisation's serial panels, not
the permits.

## Table 3 — the control that settles it: give the pool the threads first

If thread injection were the stall, starting with a large pool would remove it. `SetMinThreads(64)`
before the same run, three repetitions of each:

| configuration | pool threads | peak blocked in `Enter()` | thread-seconds parked | **wall clock, one point** |
|---|---|---|---|---|
| default pool (min = 10) | 11 → 12 | 2–3 | 0.76 / 1.27 / 1.34 | **1.37 / 1.23 / 1.33 s** |
| `SetMinThreads(64)` | 30 → 50 | 34 | 12.93 / 19.98 / 20.34 | **1.25 / 1.47 / 1.32 s** |

**15× the parked thread-seconds, and the same wall clock.** With the big pool the brief's predicted
picture appears in full — 34 threads parked in `Enter()` at once, 20 thread-seconds of parking, 151%
of the budget's own core-seconds — and utilisation goes *up*, to 84.6% from 78.7%, because the cap
stays saturated either way. Parked threads burn no CPU and hold no permit; what they cost is a pool
thread each, not time.

## Table 4 — caps below the machine, where the surplus threads are most numerous

Same point, same fixture, one run each:

| cap | wall clock | pool threads | peak blocked | thread-seconds parked | parked / (cap × wall clock) |
|---|---|---|---|---|---|
| 2 | 1.98 s | 4 | 2 | 3.14 | 79% |
| 4 | 1.42 s | 12 | 8 | 6.34 | 111% |
| 10 | 1.31 s | 12 | 3 | 1.10 | 8% |

A cap *below* `ProcessorCount` is the configuration that parks the most threads, because the pool's
minimum is 10 whatever the cap is — at cap 4, eight surplus threads exist and block. The wall clock
is still monotone in the cap and matches §6's own cap-4-to-cap-10 story on this box.

## Table 5 — a five-point sweep, because the pool grows over one and a single point cannot show it

Same fixture, 5 frequencies, 20 ms sampling, **3.01 s** total:

| t (s) | pool threads | blocked (max in bucket) | permits held (mean) |
|---|---|---|---|
| 0.0–0.6 | 11 | 2 | 6.3 |
| 0.8–1.6 | 12 | 2 | 7.6 |
| 1.8–2.4 | **24** | **13** | 8.0 |
| 2.6–3.0 | 24 | 8 | 7.3 |

The pool does grow across a sweep — 11 → 12 → **24** in one jump near t = 1.8 s — and the blocked
count spikes with it, exactly as it does when the pool is inflated by hand. It changes nothing:
**3.01 s against 3.05 s** for the identical sweep started at `SetMinThreads(64)` (pool 30 → 51, peak
blocked **40**, 19.3 thread-seconds parked against 1.86). Parked over the whole sweep is 6.2% of the
budget's core-seconds; mean utilisation 75%.

## The finding

**Refuted, and precisely: the mechanism is real, its consequence is not.** Threads do park in
`Enter()`, and on a pool that is already larger than the cap they park in exactly the numbers the
brief predicts. But the fill reaches its full cap within ~30 ms of starting in every configuration
measured, from a pool of 11 threads or one of 50, and the wall clock is invariant to how many threads
are parked. There is no stall to remove, so there is no overlap for a shared row queue to recover —
`cap` long-lived workers draining one queue would arrive at the same 10 permits' worth of arithmetic
that the present scheme already keeps busy.

Two facts make it work, and neither is obvious from reading `ForRows`:

1. **The cap is bounded by `ProcessorCount`, and so is the pool's minimum thread count.** The fill
   never needs an injected thread to reach its cap; it needs at most `cap` of the threads the pool
   already has, plus a handful more for the workers that will park.
2. **`Parallel.For`'s unmet demand queues rather than blocks.** A replica that has no thread is a
   pending work item, not a parked thread, so K concurrent loops do not produce K·cap parked threads
   unless something else has already grown the pool past the cap.

**What would change the answer:** a machine or host where the pool's minimum worker count is *below*
the cap the user asks for (then the fill genuinely waits on injection to reach it), or a cap that is
allowed to exceed `ProcessorCount`. Neither is reachable through the shipped settings —
`PlanarSolve` materialises a null cap as `Environment.ProcessorCount`.

## What was changed in the code

```
src/Engine/Mom/PlanarParallel.cs           + WaitingThreads / HeldPermits / EnterCount /
                                             TotalWaitSeconds / ResetCounters — the instrument, kept
tests/Engine.Tests/Mom/ParallelBudgetTests.cs
                                           + P10_TheBudgetsCounters_SeeTheFillTheyExistToMeasure
                                           + P10_APermitAlwaysComesBack_SoOneBudgetSurvivesASecondFill
```

No change to `ForRows`, to `PlanarFanOut`, to the budget's semantics or to any answer. §6's M3
paragraph is untouched, because nothing in it became false — the fall-off it attributes to hardware
is not the permit scheme, which is what this phase set out to check.

---

# P11 — the calibration standards' static capacitance, accelerated (brief-em-p11-accelerated-static-capacitance.md, 2026-08-29)

`PlanarDeembed.StaticCapacitance` solved `P q = ε₀·1` on each calibration standard with a dense m×m
complex LU, whatever the run's settings said. `P` is exactly the scalar block M5 already projects, so
`PlanarStaticAim` now solves it accelerated — same projection, same near-set rule, same grid FFT,
same sparse-LU-preconditioned GMRES, over CELLS with one grid kernel and no ω. The narrative,
the negative results and the wiring are in `RESOLVED.md` §P11; the tables are here.

## Table 1 — the knob ladder, against the dense solve on the same meshes

Relative error in `C_pul = (C₂ − C₁)/Δℓ`. FR-4 hero's own 30.84 mm / 90.89 mm standards
(m = 279 / 738, differencing amplification `C₂/(C₂ − C₁)` = 1.54) and the GaAs hero's 1.14 mm /
3.02 mm (m = 117 / 198, amplification 1.64), both at the shipping mesh. One knob moved at a time from
the shipped defaults. Release build, standalone harness.

| knob | FR-4 | GaAs | near entries/row (FR-4) |
|---|---|---|---|
| `GridSpacingFactor` 0.125 cell spans | 1.19e-7 | 2.00e-9 | 196 |
| 0.25 | 1.11e-7 | 2.00e-9 | 196 |
| **0.5 (shipped)** | **1.04e-7** | **2.02e-9** | 196 |
| 0.75 | 1.60e-7 | 2.11e-9 | 196 |
| 1.0 | 3.32e-7 | 2.56e-9 | 196 |
| `NearRadiusFactor` 3 basis supports | 2.69e-7 | 8.88e-7 | 99 |
| 4 | 3.69e-7 | 1.61e-7 | 132 |
| 5 | 1.98e-7 | 2.89e-8 | 164 |
| **6 (shipped)** | **1.04e-7** | **2.02e-9** | 196 |
| 8 | 3.39e-8 | 7.07e-15 | 256 |
| `ProjectionOrder` 2 | 1.05e-7 | 2.15e-9 | 196 |
| **3 (shipped)** | **1.04e-7** | **2.02e-9** | 196 |
| 4 | 1.08e-7 | 2.00e-9 | 196 |
| 5 | 1.08e-7 | 2.00e-9 | 196 |

**The radius is the only knob that acts.** The order is inert across the whole reachable range, and
the pitch is flat below 0.5 and degrades above it. The radius is read in BASIS supports — M5's own
unit and M5's own number — not in cell spans; reading it in cell spans halves it (a basis spans two
cells) and leaves GaAs at 8.88e-7, inside the 1e-6 gate by 12%.

**The pitch and the radius are not independent when the radius is too small.** At a radius of 3
supports the pitch ladder has the opposite sign: `0.5 → 2.69e-7`, `0.25 → 1.31e-6`,
`0.125 → 2.45e-6`. Refining the grid cannot compensate for a near field too narrow to hold the
coupling.

## Table 2 — the GMRES tolerance, on the DIFFERENCED answer

FR-4 standards as above.

| `StaticTolerance` | relative error in `C_pul` | iterations |
|---|---|---|
| 1e-6 | 9.88e-8 | 2 / 2 |
| 1e-8 | 1.04e-7 | 3 / 3 |
| **1e-10 (shipped)** | **1.04e-7** | 3 / 3 |
| 1e-12 | 1.04e-7 | 4 / 4 |
| 1e-14 | 1.04e-7 | 5 / 5 |

Identical in every printed digit from 1e-8 down: **the projection's error is what is being measured,
not the solve's.** 1e-10 is two decades under where it stopped moving, so a standard pair whose
lengths are close (a larger amplification than the 1.5-1.9 here) still has the residual's own
contribution well under the projection's — and not tighter than that, because a tolerance GMRES
cannot reach becomes a refusal.

## Table 3 — the owner's taper, re-run (M4)

Reconstructed at the reported geometry (13.1 mm into 299 µm over 28.575 mm, 20 mil RO4350B,
cells/λ 5, edge 3, mesh frequency 500 MHz), straight-flanked rather than Klopfenstein, landing at
N = 4,239 against the reported 7,749 — the width RATIO is what reproduces the failure, and it does:
the WIDE port's standards mesh at **N = 1,498 / 7,603 / 4,273**.

Each standard's static capacitance, accelerated, Release:

| standard | m (cells) | n (bases) | time | resident | dense working set | near/row | fill |
|---|---|---|---|---|---|---|---|
| 3.67 mm | 784 | 1,498 | 0.34 s | 14 MB | 28 MB | 435 | 55.5% |
| 13.66 mm | 2,184 | 4,273 | 2.08 s | 82 MB | 218 MB | 575 | 26.3% |
| **25.66 mm** | **3,864** | **7,603** | **9.69 s** | **222 MB** | **683 MB** | 607 | 15.7% |

The dense route does not allocate that 683 MB — it is refused. Of the 9.69 s, **9.24 s is the sparse
LU** of the near matrix and 72 ms is the GMRES solve (4 iterations); the near fill is 219 ms and the
AIM correction 93 ms.

The whole run, 3 points at 1 / 3 / 5 GHz, de-embedded:

| | before P11 | after |
|---|---|---|
| dense | refused at setup | refused at setup; the sentence now names the accelerator and quotes its ceiling |
| **accelerated** | **refused at setup** | **107 s**, 7 concurrent solves per point across 10 cores |

| f | \|S₁₁\| | \|S₂₁\| | \|S₁₁\|²+\|S₂₁\|² | port 1 Z_c | port 2 Z_c |
|---|---|---|---|---|---|
| 1 GHz | −1.24 dB | −6.10 dB | 0.998 | 6.59 Ω | 82.2 Ω |
| 3 GHz | −2.13 dB | −4.22 dB | 0.991 | 6.93 Ω | 83.7 Ω |
| 5 GHz | −1.23 dB | −6.28 dB | 0.990 | 7.00 Ω | 84.1 Ω |

Port 1's `Z_c` against the 6.92 Ω the taper was drawn for is the interesting column. Port 2's 82-84 Ω
against 100 Ω is not a P11 result: that port resolves to **2 basis functions** across 468 µm and the
run raises its own feed-clearance note about it.

## The refusal that could not fire

`brief-em-deembed-ceiling-closeout.md`'s C1 put the de-embedding-specific ceiling check in
`PlanarSolve.Run` after the calibrators were constructed. `PlanarPortCalibrator`'s constructor builds
one `PlanarSolveContext` per standard, and that constructor's eager `SurfaceMesher.GuardCeiling`
throws first — so on the dense path the informative sentence was **unreachable**, and the message a
user actually got was "This mesh has 7,603 unknowns…", which names neither de-embedding, nor which
port, nor a remedy. Verified directly on the taper before the fix. The check now runs before the
calibrator is built (which is handed the standard set rather than rebuilding it) and two tests assert
its text.

## What was changed in the code

```
src/Engine/Mom/PlanarStaticAim.cs   NEW — PlanarStaticAim, PlanarStaticAimReport
src/Engine/Mom/PlanarAim.cs         + AimProjection, AimGridFft (MOVED out of the two AIM classes,
                                      not copied) + PlanarAimSettings.StaticTolerance
src/Engine/Mom/PlanarFill.cs        + PlanarPulsePotential (carved out of PlanarEntryFill.P whole)
src/Engine/Mom/PlanarDeembed.cs     the accelerated route, its ceiling, the slabHeightM parameter
src/Engine/Mom/PlanarSolve.cs       the standards' ceiling refusal moved before the calibrator
tests/Engine.Tests/Mom/PlanarP11StaticAimTests.cs      NEW — 12 routine tests, 0.93 s
tests/Engine.Tests/Mom/EmDeembedCeilingTests.cs        + the two setup-refusal gates
tests/Ui.Tests/Em/EmCeilingRefusalTests.cs             Gate1 inverted: no longer refuses
```

`Aim = null` is bit-identical and asserted as exact equality.

# P12 — multi-level and vias under AIM, as a bordered system (brief-em-p12-aim-bordered-vias.md, 2026-08-29)

`PlanarAimGeometry.Build` refused any mesh with a ẑ basis and `PlanarSolveContext.SolveAt` refused
the general kernel under `Aim`, so every board with a ground via was capped at the dense 5,000
however much of it was ordinary horizontal metal. The refusal's reason — "a projection with a
derivative in it" — is true of PROJECTING the ẑ bases and was never a reason to project them.
`PlanarBorderedAimOperator` projects the HORIZONTAL PREFIX (R-via-5 already makes it a prefix) with
one grid kernel table per (level, level) pairing over the SAME auxiliary grid, and carries the ẑ
unknowns as a dense border filled by `PlanarFill`'s own via arms. The narrative, the negative results
and the ceiling recommendation are in `RESOLVED.md` §P12; the tables are here.

Every rung is `PlanarP12BorderedAimTests`, Release, ten cores.

## Table 1 — M1: the multi-level block, against the dense multi-level solve

Two conductor levels, no vias, so there is no border at all and what is measured is the per-pairing
grid kernel and the per-level scatter/gather. `|ΔZ|` is entry-wise against
`PlanarFill.FillMultiLevel`'s matrix, scaled by that matrix's largest entry; `|ΔI|` is the solved
current vector, which is what an s-parameter is read from.

| fixture | N | near/row | near % | worst \|ΔZ\| | iters | \|ΔI\| | dense | aim |
|---|---|---|---|---|---|---|---|---|
| MMIC 400 µm, shipping mesh | 424 | 424 | 100.0% | **1.88e-15** | 1 | **7.42e-11** | 0.78 s | 0.22 s |
| MMIC 2 mm, cells/λ 80 | 272 | 231 | 84.9% | 5.60e-9 | 2 | 3.73e-6 | 0.26 s | 0.04 s |

Both carry **3 level pairings on one grid**. The first row has no far field — every pair is near —
so it is the EXACTNESS statement rather than an accuracy one: the accelerated product reproduces the
dense multi-level matrix to round-off, with no tolerance in the claim.

## Table 2 — M2: the border, against the dense via solve

Two fixtures because the mesher makes two kinds of vertical basis: an INTERIOR via between two meshed
levels (`ViaPhysicsTests`' MMIC fixture) and a GROUND ATTACHMENT spanning the plane to the metal
(`InternalPortTests`' backside via).

| fixture | N | N_z | near/row | near % | worst \|ΔZ\| | iters | \|ΔI\| |
|---|---|---|---|---|---|---|---|
| MMIC + via, shipping mesh (all near) | 514 | 2 | 512 | 100.0% | **1.09e-15** | 1 | **1.38e-10** |
| MMIC + via, 2 mm cells/λ 80 | 452 | 2 | 386 | 85.8% | 2.39e-9 | 2 | 3.30e-6 |
| …the same, near radius 12 supports | 452 | 2 | 450 | 100.0% | **3.48e-15** | 1 | **3.97e-11** |
| FR-4 hero + ground via, shipping mesh | 374 | 4 | 346 | 93.4% | 1.09e-9 | 2 | **3.97e-7** |

De-embedded S through `PlanarSolve.Run` itself, N = 100, two calibration standards, 5 and 10 GHz:
worst `|ΔS|` **1.62e-14** de-embedded and 3.64e-15 raw.

## Table 3 — the control that makes Table 2 readable

The SHIPPED single-level accelerator against the SHIPPED single-level dense fill, on the FR-4 hero at
6 GHz, 16 mm, at the two mesh characters Table 2 uses. **No P12 code runs in this table.**

| mesh | N | near/row | near % | worst \|ΔZ\| | iters | \|ΔI\| |
|---|---|---|---|---|---|---|
| cells/λ 20, edge mesh ON (shipping) | 314 | 290 | 92.2% | 1.27e-9 | 2 | **4.90e-7** |
| cells/λ 40, edge mesh OFF | 238 | 166 | 69.6% | 2.14e-7 | 4 | **2.71e-5** |

**55× of spread from the MESH alone**, on a path P12 does not touch — which is what says Table 2's
3.3e-6 rung is the projection's number for that mesh and not the border's. The brief's own 8.7e-7 is
`AimAccuracyTests`' figure for the 32 mm FR-4 hero AT THE SHIPPING MESH; quoting it at a different
fixture would be grading P12 on somebody else's mesh.

## Table 4 — M4: the ceiling ladder, growing by LENGTH

`brief-em-aim-ceiling.md`'s own healthy construction — grow the part at a fixed resolution, which is
how a real board gets big. Two-level GaAs line at 10 GHz, cells/λ = 80, **one 40 µm via**, so `N_z`
is fixed at 2. Seconds are per FREQUENCY unless the column says geometry (which is per MESH).

| L | N | N_z | near/row | near % | geom | grid | near | border | precond | solve | iters | resid | MB | dense MB |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2 mm | 452 | 2 | 386 | 85.8% | 0.02 s | 0.00 | 0.02 | 0.43 | 0.05 | 0.00 | 2 | 3.9e-9 | 8.4 | 4 |
| 8 mm | 1,728 | 2 | 488 | 28.3% | 0.04 s | 0.00 | 0.07 | 0.45 | 1.19 | 0.03 | 4 | 6.4e-11 | 59.5 | 64 |
| 24 mm | 5,072 | 2 | 510 | 10.1% | 0.09 s | 0.01 | 0.18 | 0.54 | 0.64 | 0.04 | 4 | 5.2e-9 | 107.0 | 549 |
| 48 mm | 10,132 | 2 | 515 | 5.1% | 0.18 s | 0.02 | 0.39 | 0.56 | 1.32 | 0.11 | 5 | 5.4e-9 | 215.3 | 2,192 |
| 72 mm | **15,192** | 2 | **517** | 3.4% | 0.26 s | 0.04 | 0.51 | 0.46 | 2.01 | 0.21 | **6** | 3.6e-9 | **327.1** | **4,927** |

"dense MB" is `PlanarSystem.ResidentBytes` — what a dense point of the same N actually holds, not
16N². §11's two findings both survive the border: **near entries per row are flat** (488 → 517 over
8.8× N) and **GMRES does not walk** (4 → 6). The whole point at N = 15,192 is ~2.5 s and 327 MB.

## Table 5 — the same ladder with the via footprint growing WITH the part

The construction nobody builds, run because it is the one that decides what "cheap while N_z ≪ N_h"
is a statement about. Identical in every other respect; the via is 10% of the line's length.

| L | N | N_z | near fill | **border** | precond | resident MB | border MB |
|---|---|---|---|---|---|---|---|
| 2 mm | 432 | 4 | 0.17 s | 1.51 s | 0.06 s | 7.1 | 0.0 |
| 8 mm | 1,720 | 16 | 0.23 s | 3.85 s | 1.21 s | 59.6 | 0.4 |
| 24 mm | 5,140 | 48 | 0.26 s | 10.25 s | 0.75 s | 121.8 | 3.8 |
| 48 mm | 10,268 | 94 | 0.36 s | 19.27 s | 1.94 s | 273.0 | 14.9 |
| 72 mm | 15,352 | 140 | 0.62 s | **28.56 s** | 3.31 s | 454.7 | 33.1 |

**The border's MEMORY is `16·N_h·N_z` and stays negligible either way; its TIME is `N_h × N_z` graded
4-D quadratures and is not negligible at all.** At `N_z` = 140 it is 28.6 s of a 34 s point, against
0.5 s at `N_z` = 2 and the same N. The dense path pays the same `N_h × N_z` mixed entries, so this is
not a regression against it — but it is the term that decides whether the ceiling can be widened for
a mesh with a via FIELD rather than a via.

# MIM-3 — thin dielectric layers: what was measured, and what it costs (brief-em-mim-3-thin-layer-gate.md, 2026-08-30)

A MIM capacitor puts two meshed conductor levels 0.05–0.5 µm apart, `LayerStack.CanRepresent`
accepts any positive thickness, and nothing had ever measured the kernel or the quadrature there.
The brief was measurement-first and the answer separates cleanly into two halves: **the kernel is
fine at plate spacing and the QUADRATURE is not.** The narrative and the verdict are in
`RESOLVED.md` §MIM-3; the tables are here.

Run against a post-MIM-6 build, so the plate gap is the 0.2 µm the shipped MIM technology states
and not the 3.2 µm a pre-MIM-6 tree would have measured. Everything below is a scratch harness,
Release, run alone.

## The fixture, and why d is the only thing that moves

`GaAs 103 µm | capacitor dielectric d (εᵣ 6.8, tanδ 0.001) | air to 106 µm`, on a ground plane —
the shipped `mmic-GaAs_2LM_100um_MIM` stack with its thickness parameterised. The top stays at
106 µm at every rung, so the mode content and the ρ/λ span do not move with d; **and in the fill
and physics tiers d is a STACKUP number, so the artwork, the mesh, the unknown count and the ports
are bit-identical across the whole ladder.** Each ladder therefore varies one thing.

The ladder carries **2 µm and 3 µm control rungs** on the same stack shape. 3 µm is L9c's own
`MmicTwoLevel` spacing, i.e. the separation the existing record was taken at, so every table below
can be read against a regime that was already measured.

## Table 0 — the ORACLE, before anything is concluded from it

`SommerfeldIntegral.EvaluateInterior`'s own header warns that a cross-region pair separated by a
3 µm spacer exhausts `MaxTailPanels` on the default settings. This ladder asks for 0.05 µm, so the
first thing measured was the oracle. Worst over d = 0.05 µm, both pairings, all four components,
ρ/λ ∈ [1e-3, 1] (G_A^zz to 0.1, its own validated range):

| knob, one at a time | worst rel. move vs default | worst cost |
|---|---|---|
| `RelativeTolerance` 1e-11 → 1e-12 | **3.3e-8** | 3.17 s (G_A^zz) |
| `PanelNodes` 16 → 24 | 1.8e-10 | 0.29 s |
| `OscillationDensity` 8 → 16 | 8.8e-11 | 0.34 s |
| `MaxSubdivisions` 14 → 18 | 5.0e-16 | 2.17 s |
| `MaxTailPanels` 500 → 5000 | 0.0 | 0.34 s |

`TailConverged` is true at every point at the DEFAULT settings, with tail residuals ≤ 2.5e-8. **The
oracle is converged at its defaults to ~3e-8 relative, five decades below anything reported below,
so the defaults are what Table 1 uses.**

**A setting that never returns is not a tighter oracle, it is no oracle.** The first attempt paired
`RelativeTolerance = 1e-13` with `MaxSubdivisions = 18` and took ~15 minutes for a single
(d, pairing, component) row at ρ/λ ≥ 0.5 — the whole 120-row table would have been ~30 hours. The
knob table above exists because that had to be diagnosed rather than waited out.

## Table 1 — milestone 1, the KERNEL tier

`Dcim.FitAtHeights` → `DcimModel.EvaluateAtHeights` (what `PlanarKernelSet` hands the fill) against
`EvaluateInterior`, at 10 GHz. The measure is L8a's and L9c's: the SCALED error `|ΔG|·4πR`, i.e.
the error as a fraction of the free-space kernel at the same R. **Worst over both pairings**
(low–low at 103 µm, low–high across the dielectric) and over the validated ρ span — ρ/λ ≤ 1 for
G_A^xx / G_q / mixed (`ValidatedRhoOverLambdaInteriorHorizontal`) and ρ/λ ≤ 0.1 for G_A^zz
(`ValidatedRhoOverLambdaAtHeights`).

| d (µm) | G_A^xx | G_q | G_A^zz | mixed | worst STRICT rel, ρ/λ ≤ 0.1 (excl. mixed low–low) |
|---|---|---|---|---|---|
| 0.05 | 7.5e-4 | 4.2e-3 | 1.1e-3 | 7.5e-4 | 1.2e-2 |
| 0.10 | 7.4e-4 | 4.1e-3 | 5.6e-3 | 7.5e-4 | 1.2e-2 |
| 0.20 | 7.9e-4 | 4.0e-3 | 6.1e-3 | 7.5e-4 | 1.4e-2 |
| 0.50 | 7.5e-4 | 5.6e-3 | 6.1e-3 | 7.5e-4 | 2.1e-2 |
| 1.00 | 7.4e-4 | 6.3e-3 | 2.7e-3 | 7.5e-4 | 1.6e-2 |
| **2.00** (control) | 7.8e-4 | 5.7e-3 | 5.7e-3 | 7.5e-4 | 2.0e-2 |
| **3.00** (control, = L9c's `MmicTwoLevel`) | 7.1e-4 | 6.4e-3 | 2.8e-3 | 7.5e-4 | 1.6e-2 |

**The ladder is FLAT.** 0.05 µm is not worse than 3 µm on any component — it is marginally better
on two of them — and every number sits inside L9b's own ≤1.6e-2 envelope for the top-half-space
pairing and L9c's ≤1.9e-2 for the interior one. **There is no thin-layer kernel failure to find**,
and the ρ/λ constants stay exactly where L8a/L9b/L9c measured them.

The εᵣ = 1 control (the same geometry with no permittivity contrast in the slot) is flat in d too,
so the flatness is not the contrast cancelling itself out.

### One observation that is NOT a MIM-3 finding, recorded so nobody re-finds it

The **mixed component at a same-level (low–low) pairing** fits with 0 images and an infinite
residual, and its strict relative error is 2.07 at every rung. It is **d-independent to four
figures** — the last three ρ columns are the same numbers at 0.05 µm and at 3 µm — and it is
*worse* on the εᵣ = 1 control (1.58e+1). So it is a property of that pairing, not of thin layers.
Its scaled error is ≤ 7.5e-4 throughout, and the fill only asks the mixed kernel about a via
against a horizontal basis, so nothing here depends on it. Left as an observation.

## Table 2 — milestone 2, the FILL tier. This is where it goes

Two coincident square plates, four cells to a side, straddling the dielectric; `FillMultiLevel` at
the SHIPPED quadrature against the same matrix at a forced-high one, entry-wise, scaled by **that
block's own largest entry**. The kernel is identical in both, so what is isolated is the
quadrature. N = 48 and cells = 32 at every rung by construction — only the cell/separation ratio
moves.

Forced-high is `SelfPanels 8 / TouchPanels 6 / NearNodes 20 / MidNodes 16 / FarNodes 12 /
NearRatio 8 / FarRatio 32 / remainder 16-12-8 / UseRadialTable off`. The last two columns step it
again (12/10, 28/24/20, remainder 24-20-16) on the d = 0.2 µm rungs, so "the reference is
converged" is measured rather than asserted.

| d (µm) | cell/d | cell (µm) | plate (µm) | same-level \|ΔZ\| | **cross-level \|ΔZ\|** | ref. same | ref. cross |
|---|---|---|---|---|---|---|---|
| 0.05 | 1 | 0.05 | 0.2 | 3.37e-6 | **2.33e-7** | – | – |
| 0.05 | 2 | 0.1 | 0.4 | 3.54e-6 | **9.59e-6** | – | – |
| 0.05 | 5 | 0.25 | 1 | 2.83e-6 | **4.11e-3** | – | – |
| 0.05 | 10 | 0.5 | 2 | 3.95e-4 | **3.93e-2** | – | – |
| 0.05 | 20 | 1 | 4 | 9.59e-3 | **1.52e-1** | – | – |
| 0.05 | 50 | 2.5 | 10 | 9.41e-2 | **4.85e-1** | – | – |
| 0.10 | 1 | 0.1 | 0.4 | 3.37e-6 | **2.29e-7** | – | – |
| 0.10 | 2 | 0.2 | 0.8 | 3.53e-6 | **8.46e-6** | – | – |
| 0.10 | 5 | 0.5 | 2 | 3.15e-6 | **3.89e-3** | – | – |
| 0.10 | 10 | 1 | 4 | 3.33e-4 | **3.81e-2** | – | – |
| 0.10 | 20 | 2 | 8 | 8.73e-3 | **1.49e-1** | – | – |
| 0.10 | 50 | 5 | 20 | 8.94e-2 | **4.78e-1** | – | – |
| **0.20** | 1 | 0.2 | 0.8 | 3.37e-6 | **2.24e-7** | 1.53e-8 | **7.65e-15** |
| **0.20** | 2 | 0.4 | 1.6 | 3.54e-6 | **7.35e-6** | 1.60e-8 | **1.31e-10** |
| **0.20** | 5 | 1 | 4 | 2.89e-6 | **3.66e-3** | 1.83e-8 | **2.42e-5** |
| **0.20** | 10 | 2 | 8 | 3.84e-4 | **3.68e-2** | 7.86e-7 | **1.69e-3** |
| **0.20** | 20 | 4 | 16 | 9.45e-3 | **1.46e-1** | 2.51e-4 | **1.61e-2** |
| **0.20** | 50 | 10 | 40 | 9.34e-2 | **4.72e-1** | 9.75e-3 | **8.29e-2** |
| 0.50 | 1 | 0.5 | 2 | 3.37e-6 | **1.86e-7** | – | – |
| 0.50 | 2 | 1 | 4 | 3.54e-6 | **2.35e-6** | – | – |
| 0.50 | 5 | 2.5 | 10 | 2.89e-6 | **2.26e-3** | – | – |
| 0.50 | 10 | 5 | 20 | 3.84e-4 | **2.81e-2** | – | – |
| 0.50 | 20 | 10 | 40 | 9.45e-3 | **1.23e-1** | – | – |
| 0.50 | 50 | 25 | 100 | 9.35e-2 | **4.24e-1** | – | – |
| 1.00 | 1 | 1 | 4 | 3.37e-6 | **1.58e-7** | – | – |
| 1.00 | 2 | 2 | 8 | 3.54e-6 | **7.56e-7** | – | – |
| 1.00 | 5 | 5 | 20 | 2.94e-6 | **1.50e-3** | – | – |
| 1.00 | 10 | 10 | 40 | 3.75e-4 | **2.25e-2** | – | – |
| 1.00 | 20 | 20 | 80 | 9.32e-3 | **1.08e-1** | – | – |
| 1.00 | 50 | 50 | 200 | 9.33e-2 | **3.94e-1** | – | – |
| **2.00** (control) | 1 | 2 | 8 | 3.37e-6 | **7.60e-8** | – | – |
| **2.00** | 2 | 4 | 16 | 3.54e-6 | **3.00e-7** | – | – |
| **2.00** | 5 | 10 | 40 | 3.12e-6 | **2.05e-4** | – | – |
| **2.00** | 10 | 20 | 80 | 3.39e-4 | **8.91e-3** | – | – |
| **2.00** | 20 | 40 | 160 | 8.86e-3 | **7.04e-2** | – | – |
| **2.00** | 50 | 100 | 400 | 9.34e-2 | **3.30e-1** | – | – |

**The ladder is a function of cell/d and essentially not of d.** Read down any cell/d value and the
cross-level column moves by at most 3× across a 40× change in the separation itself, while reading
across cell/d moves it by six decades. That is what makes the ratio — rather than the thickness —
the quantity the verdict is stated in, and it is why `ValidatedCellOverSeparation` is a mesh
condition rather than a stackup one.

**Three things, and the third is an honesty note.**

1. **The cross-level block loses four decades between cell/separation 1 and 20** — 2.3e-7,
   9.6e-6, 4.1e-3, 3.9e-2, 1.5e-1, 4.9e-1 — steepest at the bottom and flattening as it saturates.
   The SAME-level block does not follow it — it is still 3e-6 where
   the cross-level block is at 4e-3, and only turns up (9e-2) at cell/d = 50, where the cells have
   become large in absolute terms as well. So this is the cross-level quadrature, not a coarse
   mesh.
2. **The reference is converged where the verdict is taken.** At cell/d = 1 the default-vs-high
   difference is 2.24e-7 while high-vs-higher is 7.65e-15 — seven decades apart. At cell/d = 5,
   where the range is drawn, it is 3.66e-3 against 2.42e-5, still 150×. The error being reported is
   the default's, not the reference's.
3. **AND THE HONESTY NOTE: at the COARSEST rungs the reference is not converged either.** At
   cell/d = 20 the two references disagree by 1.61e-2 against a default-vs-high of 1.46e-1 — only
   9× — and at 50 it is 8.29e-2 against 4.72e-1, 5.7×. The same peak that defeats the shipped rule
   is starting to defeat the expensive one, which is itself evidence for the mechanism. Those two
   rungs therefore support "the error is large and grows" and NOT the third significant figure of
   any number in them. **Nothing in the verdict rests on them**: the range is drawn at 5, where the
   reference has 150× of headroom.

**The mechanism is the one already on the record** (§3.5): *"cross-level entries have no 1/ρ but
still carry logarithms"*, and *"different levels ⇒ smooth ⇒ plain quadrature"* is a recorded trap.
At d ≪ cell the cross-level kernel has a peak of width d sitting inside a cell of width h; a rule
that treats the pair as smooth because Δz > 0 integrates straight over it. **This is §L8c's
converged-looking-but-wrong mode reproduced exactly** — see Table 4: reciprocity holds to 1e-19 and
passivity to 1e-5 the whole way up.

## Table 3 — milestone 3, the PHYSICS tier: what it costs in an answer

### 3a — the instrument, and the two that were discarded first

**Raw S cannot carry a capacitance in this engine, and that is measured rather than assumed.** The
port is necessarily a series delta gap whose `a₂₁ ∝ ω` (§5's low-frequency floor). The control:

| structure | expected | raw reading |
|---|---|---|
| 70 µm GaAs microstrip, 800 µm, 10 GHz (≈ 50 Ω, matched) | \|S₂₁\| ≈ 1 | **\|S₂₁\| = 0.0706**, \|S₁₁\| = 0.997 |

So the first two instruments were discarded on the strength of that one line, not on their answers:
a one-port shunt cap read `Im(Y₁₁)/ω` **negative** at d ≤ 0.1 µm, and a series two-port read
`−Im(Y₂₁)/ω` at 0.50 → 0.18 fF over a 40× change in d where ε₀εᵣA/d spans 120 → 3 fF. Both are the
port, not the structure. **This is MIM-2's finding (b) reproduced, and it confirms MIM-2's
RETRACTION of it rather than the finding.**

### 3b — the de-embedded reading

De-embedding a port off the slab top is MIM-4's, so the stack is truncated at the upper plate:
`GaAs 103 µm | capacitor dielectric d`, top = the upper plate's own level. A 10 × 10 µm shunt MIM
capacitor — lower plate on the interior interface, grounded by a backside via; upper plate on the
slab top with a 40 µm feed to one de-embedded port. 60 GHz, so a 30 fF plate pair is a readable
53 Ω rather than a short. The mesh pitch is 2.5 µm at every rung, so cell/d is the ladder's axis
exactly as in Table 2.

`C_baseline` is the SAME feed and the SAME port with the lower plate and via removed: **4.93 fF,
frequency-stable to 8% from 20 to 140 GHz.** Everything that is not the capacitor is in it.

| d (µm) | cell/d | C_deemb (fF) | C − baseline (fF) | ε₀εᵣA/d (fF) | Palmer (fF) | **(C−base)/(A/d)** |
|---|---|---|---|---|---|---|
| 2.00 | 1.25 | 7.61 | 2.68 | 3.010 | 4.715 | **0.89** |
| 1.00 | 2.5 | 10.86 | 5.93 | 6.021 | 7.991 | **0.99** |
| 0.50 | 5 | 18.15 | 13.22 | 12.042 | 14.278 | **1.10** |
| 0.20 | 12.5 | 48.92 | 44.00 | 30.104 | 32.691 | **1.46** |
| 0.10 | 25 | −23.76 | −28.68 | 60.208 | 63.061 | **−0.48** |
| 0.05 | 50 | −6.56 | −11.48 | 120.417 | 123.536 | **−0.10** |

**Table 2 predicts Table 3b, rung for rung.** Within 10% of the closed form while cell/d ≤ 5 (where
the cross-level fill is ≤ 4.1e-3), 1.46× at 12.5, and the WRONG SIGN at 25 and 50 (where the fill is
at 1.5e-1 and 4.9e-1).

### 3c — frequency consistency, which is what says it is not a capacitance up there

A capacitance does not depend on ω. Same structure, `C − 0` (raw de-embedded, no baseline
subtracted) at six frequencies:

| d (µm) | 20 GHz | 40 | 60 | 80 | 100 | 140 |
|---|---|---|---|---|---|---|
| 2.00 | 7.30 | 7.42 | 7.61 | 7.90 | 8.32 | 10.03 |
| 1.00 | 10.09 | 10.36 | 10.86 | 11.70 | 13.13 | 21.28 |
| 0.50 | 15.55 | 16.34 | 18.15 | 21.24 | 28.17 | **−330.6** |
| 0.20 | 38.95 | 41.25 | 48.93 | 110.4 | **−134.8** | **−16.9** |
| 0.10 | −64.5 | −71.7 | −23.8 | −9.6 | −11.8 | −8.3 |
| **the baseline** | 4.833 | 4.880 | 4.927 | 4.979 | 5.041 | 5.234 |

The baseline is stable to 8% across the whole band; the plate pair is not, and it destabilises in
the same order the fill ladder degrades. (The 140 GHz column also carries de-embedding's own f²
error growth, §5 — it is not all MIM-3's.)

### 3d — kernel A on the equivalent cross-section, which settles the closed form

Two 10 µm strips, 0.05 µm thick, gap d of εᵣ 6.8, over 103 µm of GaAs on ground. `C₁₂` from the
Maxwell matrix against ε₀εᵣW/d per metre. **Kernel A shares no code with kernel B** — no DCIM, no
rooftop basis, no layered spectral Green's function, no shared tensor grid.

| d (µm) | C₁₂ (pF/m) | ε₀εᵣW/d (pF/m) | ratio | ε_eff |
|---|---|---|---|---|
| 0.05 | 12126.3 | 12041.7 | **1.0070** | 6.783 |
| 0.10 | 6098.0 | 6020.9 | **1.0128** | 6.781 |
| 0.20 | 3080.4 | 3010.4 | **1.0232** | 6.788 |
| 0.50 | 1265.2 | 1204.2 | **1.0506** | 6.834 |
| 1.00 | 656.5 | 602.1 | **1.0904** | 6.935 |
| 2.00 | 349.0 | 301.0 | **1.1594** | 7.139 |

Mesh-converged: refining 1.0 → 3.0 moves `C₁₂` at d = 0.2 µm from 3080.37 to 3080.15 pF/m, i.e. the
fifth digit. The excess over the bare form grows monotonically with d, which is what a fringing
correction does. **The closed form is not in doubt, and a 0.05 µm gap of εᵣ 6.8 is not intrinsically
hard — kernel A does it to 0.7%.**

## Table 4 — the self-consistency set, and why it catches nothing

Same fixture as Table 3's discarded series two-port (both plates meshed, two ports, raw S):

| d (µm) | \|S₁₂ − S₂₁\| | max eig(S Sᴴ) |
|---|---|---|
| 0.05 | 8.7e-19 | 0.999995 |
| 0.10 | 4.3e-19 | 1.000008 |
| 0.20 | 2.2e-19 | 0.999999 |
| 0.50 | 2.2e-19 | 0.999985 |
| 1.00 | 4.3e-19 | 0.999986 |
| 2.00 | 2.2e-19 | 0.999987 |

**Reciprocity to 1e-19 and passivity to 1e-5 at every rung, including the ones whose capacitance
has the wrong sign.** §10.9's oracle-free set is blind to this by construction — the matrix is
still complex-symmetric and the structure still does not create energy; what is wrong is a
magnitude inside it. That is the whole reason MIM-3 was a measurement brief rather than a test.

## What changed in the code

| file | change |
|---|---|
| `PlanarKernelSet.cs` | `PlanarLevels.ValidatedCellOverSeparation = 5.0`, carrying Table 2's and Table 3b's ladders in its own doc comment |
| `PlanarSolve.cs` | `LevelSeparationNotes` — a NOTE, never a refusal; asked per ADJACENT LEVEL PAIR over the cells on those two levels only (R-zz-1's discipline), and naming the binding quantity rather than the inert frequency knobs (§3.5's trap). Called from `VerticalRangeVerdict`, so every multi-level sweep carries it |
| `tests/Engine.Tests/Mom/MimThinLayerTests.cs` | 6 routine tests (~1 s: the fixed-input cross-level literals, and the note's firing, wording, scoping and wiring) + `T7`, `Category=Benchmark`, which is the accuracy statement above (1 m 5 s in Debug) |

**Nothing in the one-level or interconnect-scale arithmetic was touched.** No existing gate was
loosened; `Dcim.ValidatedRhoOverLambdaAtHeights`, `…Layered` and `…InteriorHorizontal` are exactly
where they were, because Table 1 says they should be.

---

# MIM-4 — the interior-height static Green's function: the tables (brief-em-mim-4-interior-static-greens.md, 2026-08-30)

Narrative, findings and the "not done" list are in `RESOLVED.md` §MIM-4. What follows is the measured
data behind them.

## The formulation, once

At ω = 0 the layered problem is a Sturm-Liouville problem in z rather than a reflection referenced to
one region — there is no `k_z`, and the propagation constant is `k_ρ` in every region alike:

```
    d/dz( P(z) dG̃/dz ) − k²P(z) G̃ = −2k·δ(z − z′),     P = ε*  (scalar) or 1/µ  (vector)
    G̃(k; z, z′) = −2k · ψ↓(z_<) ψ↑(z_>) / W,           W = P(ψ↓ψ↑′ − ψ↓′ψ↑) = const
    G(ρ)        = (1/4π) ∫₀^∞ G̃(k) J₀(kρ) dk           (free space = 1/4πρ, as everywhere here)
```

with ψ↓ satisfying the bottom termination and ψ↑ the top. Reduction checks, all exact by hand before
any code: a homogeneous stack over a PEC floor gives `(e^{−k|z−z′|} − e^{−k(z+z′)})/ε` at every
height pair; both points on a one-slab top surface give `1 + Γ_e` with
`Γ_e = (K − x)/(1 − Kx)`, `x = e^{−2kh}` — the spectral form `StaticGreens`' shipped image series
inverts.

## Table 1 — the spectral kernel (milestone 1), worst deviation over the sampled grids

Deviation is measured against the kernel's own O(1) scale, not the sample's magnitude: as k → 0 the
kernel is a difference of two nearly-equal exponentials **in the closed-form oracle exactly as much as
in the production path** — at k·H = 1e-9 the oracle itself is only good to 2e-7 relative — and what
the spatial integral consumes is the absolute error.

| Check | Stack | Worst |
|---|---|---|
| Homogeneous over PEC = free space + one image, every (z, z′) | εᵣ = 1 / 4.4 / 12.9, two layers | < 1e-14 |
| Vector kernel does not see a non-magnetic stratification | 3-layer board | < 1e-14 |
| Split a layer into 3 sub-layers of its own material | 3-layer board → 6 layers, scalar and vector | < 1e-14 |
| Cross-region pair at k·H = 1e-9 (the 0/0 trap) | 2 layers over PEC | 1e-15 absolute |
| `1 + Γ_e` on the slab top | FR-4 1.6 mm, GaAs 100 µm | < 1e-14 |
| vs `LayeredStaticGreens`' spectral form in the top half-space | 3-layer board, scalar and vector | < 1e-14 |
| Flux `P·∂G̃/∂z` continuous across every interface | 3-layer board, k = 500 / 5000 | < 1e-4 (probe-offset limited) |

## Table 2 — the independent finite-volume solve (milestone 1, tier 3)

A finite-volume discretisation of the same ODE on a uniform grid with every interface on a node,
solved by a complex tridiagonal elimination. It shares no reflection coefficient, no cascade, no
exponential basis and no Wronskian with the thing under test. Gated on Richardson behaviour as well as
on error — halving h must QUARTER it — because a second-order scheme agreeing to 1e-4 proves nothing
on its own.

| (z, z′), mm | 350 cells | 700 cells | ratio |
|---|---|---|---|
| (0.40, 0.40) same layer, coincident | — | < 3e-4 | > 3.2 |
| (1.00, 1.00) exactly on an interface | — | < 3e-4 | > 3.2 |
| (0.30, 1.30) two layers apart | — | < 3e-4 | > 3.2 |
| (1.60, 1.65) inside the thin top layer | — | < 3e-4 | > 3.2 |

(k = 800 and 4 000, 3-layer board 1.00 / 0.50 / 0.25 mm at εᵣ 4.4 / 9.8 / 2.2.)

## Table 3 — the spatial function against the SHIPPED one-slab image series

`InteriorStaticImages` fitted at z = z′ = h in `LayerStack.FromGroundedSlab(slab)`, against
`StaticGreens.ScalarPotential`.

| ρ/h | FR-4 (1.6 mm, εᵣ 4.4, tanδ 0.02) | GaAs (100 µm, εᵣ 12.9, tanδ 0.002) |
|---|---|---|
| 1e-4 | 3.6e-15 | 6.7e-15 |
| 1e-2 | 3.5e-13 | 6.5e-13 |
| 1 | 7.8e-11 | 1.5e-10 |
| 10 | 1.1e-7 | 7.0e-7 |
| 100 | 7.4e-6 → **8.6e-7** | 4.9e-5 → **8.9e-6** |
| 1000 | 8.5e-6 → **9.3e-7** | 5.7e-5 → **8.9e-6** |

Arrows are before → after the low-k sample block (finding 3). The far-field figures are relative to a
value that has itself fallen by 1.6e9 from ρ = h; in absolute terms the worst is **4e-14 of the
near-field scale**.

## Table 4 — interior heights, against direct Hankel integration

`InteriorStaticGreens.PotentialByQuadrature`, which shares no approximation with the image model.
Every one of these heights is refused by name by `LayeredStaticGreens`.

| Stack | z | images | spectral residual | worst rel over ρ |
|---|---|---|---|---|
| board 1.00/0.50/0.25 mm | 1.00 mm (on an interface) | 25 | 1.7e-10 | 6.5e-6 at ρ = 66 mm |
| board | 1.50 mm | 27 | 4.2e-11 | 8.1e-7 |
| board | 1.75 mm (top) | 25 | 3.4e-11 | 4.9e-7 |
| MIM 100/0.2/2.8 µm | 100.0 µm (lower plate) | 22 | 2.1e-10 | 8.2e-9 |
| MIM | 100.2 µm (upper plate) | 23 | 1.4e-10 | 1.4e-8 |
| MIM | 103.0 µm (interconnect) | 14 | 1.6e-11 | 1.6e-8 |

ρ on the MIM stack is swept 0.05 → 250 µm — five decades, down to a quarter of the capacitor
dielectric's thickness. `c_∞` comes out exact at every level: 0.1015 = (1 + r)/ε_nitride on the lower
plate, 0.2564 on the upper, 1 on the interconnect.

## Table 5 — cost, which is the fit-versus-quadrature decision

| | per ρ | once |
|---|---|---|
| `InteriorStaticModel.Evaluate` (9–27 images) | **0.34 µs** | fit 16–78 ms |
| `StaticGreens.ScalarPotential` (the shipped series, 130 images on GaAs) | 2.5 µs | — |
| `InteriorStaticGreens.PotentialByQuadrature` | **1–270 ms**, rising with ρ | — |

The fill's radial table wants 10⁴–10⁶ samples of the kernel, so the reference integrator is four to
six orders of magnitude away from being usable and is kept only as the oracle. **The image model is
7× faster than the series it replaces on the case where both apply.**

## Table 6 — C_pul (milestone 3)

| Check | Result |
|---|---|
| Interior route vs shipped, FR-4 slab top | 1.1643962437e-10 F/m both; **3.5e-12** relative |
| Interior route vs shipped, GaAs slab top | 3.5162217008e-9 vs …012e-9; **1.2e-10** relative |
| Slab split into 3 sub-layers of its own material | **8.3e-12** |
| Fit residual, worst over 8 stacks (one-slab, board ×3, MIM ×3) | **2.1e-10**, against a refusal ceiling of 1e-6 |

### The series-capacitance limit (§10.9's tier 2, in the planar geometry)

Two stacked dielectrics (60 µm εᵣ 9.8 + 40 µm εᵣ 2.2) against the single slab whose εᵣ is their series
equivalent (4.26931). A wide line IS a parallel-plate capacitor, so the two must converge; a narrow
one must NOT, because fringing samples the layers differently and no single εᵣ can represent that.

| W/h | N | equivalent slab | stacked | difference |
|---|---|---|---|---|
| 0.5 | 87 | 5.827e-11 F/m | 4.584e-11 F/m | 21.33% |
| 2 | 157 | 1.181e-10 | 1.060e-10 | 10.26% |
| 8 | 1 045 | 3.500e-10 | 3.389e-10 | 3.16% |
| 24 | 8 877 | 9.657e-10 | 9.549e-10 | 1.12% |

## Table 7 — the de-embedded gates (milestone 4)

A 60 µm-wide uniform line on the UPPER level of the MMIC 2-level stack (100 µm GaAs + 3 µm εᵣ 2.7),
at 10 GHz — the configuration `PlanarSolve` threw on before this brief. Renormalised back to the Z_c
the run itself reports, so the number under test is the one the interior electrostatics produced.

| Quantity | Value |
|---|---|
| C_pul | 1.4788e-10 F/m |
| Z_c | 61.227 − j0.729 Ω |
| interior fit residual | 1.1e-9 |
| \|S₁₁\| of a section that should be matched | **9.05e-4** (gate 3e-2) |
| ∠S₂₁ vs −βℓ | **1.19e-3 rad** (gate 3e-2) |
| \|S₂₁\| vs e^{−αℓ} | **4.67e-6** (gate 3e-2) |
| cascade identity, 2ℓ vs two ℓ (ℓ = 881 µm) | **1.8e-4** (gate 3e-2) |
| reciprocity | < 1e-9 |

For scale: the same |S₁₁| gate reads 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz on the SLAB TOP over
1.6 mm FR-4 (§L8d's own T4_6), where the limit is direct radiative and surface-wave coupling between
the ports rather than the algebra. The buried level is inside that band.
