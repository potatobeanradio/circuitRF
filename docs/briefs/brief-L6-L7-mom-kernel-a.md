# Sonnet Brief — Phases L6/L7 (engine half): the quasi-static MoM kernel A

**Design:** `docs/design/layout-view.md` §10 — §10.3 (kernel A, the formulation), §10.3.2 (charge →
s-parameters), §10.3.4 (`IEmKernel`), §10.4 (stackup), §10.5 (meshing + edge grading), §10.6 (ports),
§10.7 (solver), §10.8 (results), §10.9 (oracles). Phase table rows **L6** and **L7**.

**This brief is the engine half only.** It builds `src/Engine/Mom/` — the neutral geometry contract, the
boundary mesher, the charge solver, RLGC extraction, RLGC → s-parameters, and the `IEmKernel` seam —
validated entirely from `tests/Engine.Tests/Mom/` against closed-form oracles. **It touches no UI, reads
no `.clay`, writes no `.snp`, and adds no dialog.** A following brief wires the Ui side (cross-section
extraction from real layout geometry per §10.3.3, the mesh viewer, the EM setup panel, and the `.snp`
artifact of §10.8).

That split is deliberate and it is `Development_Plan.md` §0 decision 2 applied to this phase: *build the
engine first, validated numerically, before the GUI.* The Hammerstad-Jensen gate is reachable from a unit
test on day one because the oracle already exists in-tree.

Gate command is plain `dotnet test`.

---

## 1. The one correction to the design doc

§10.3.4 specifies `IEmKernel.Solve(LayoutFragment, Stackup, Port[], double[] freqs, …)` and §10.7 says the
kernel lives in `src/Engine/Mom/`. **Those two statements are not simultaneously satisfiable.**
`LayoutFragment`, `Stackup` and `Technology` live in `src/Ui/Layout/`, and the reference graph is
`Ui → Engine → Core → RfCore`. Engine cannot see them, and inverting the arrow would break the UI firewall
that `tests/Firewall.Tests` enforces.

**R-mom-1. The kernel consumes a neutral EM problem model defined in `src/Engine/Mom/`, in SI units, and
knows nothing about DBU, `.clay` shapes, layer tables or `LayerKey`.** The Ui-side cross-section extractor
(§10.3.3) produces that model; producing it *is* what extraction already had to do. Update
`layout-view.md` §10.3.4's code block to match once this lands.

This is the better boundary anyway — it is the standing invariant *"the numeric layer sees only
fully-resolved values"* applied to geometry, and it is what lets the whole kernel be tested without
constructing a layout document.

**R-mom-2. Everything in `src/Engine/Mom/` is in metres, siemens/metre, radians and hertz — doubles, not
integers.** DBU is a database concern and stops at the extractor. Do not carry `long` coordinates into the
physics.

---

## 2. The neutral problem model

`src/Engine/Mom/EmProblem.cs`. All records, all immutable.

```csharp
public readonly record struct EmPoint(double X, double Y);          // metres, cross-section plane

public sealed record EmMaterial(double EpsR, double TanD = 0, double MuR = 1);

/// A laterally infinite horizontal dielectric slab. Regions are ordered bottom-to-top and must tile
/// the y axis without gaps or overlap; the topmost and bottommost extend to ±infinity.
public sealed record EmDielectricRegion(double YBottom, double YTop, EmMaterial Material);

/// A closed, simple polygon in the cross-section plane, CCW, with finite thickness.
public sealed record EmConductor(string Name, IReadOnlyList<EmPoint> Outline, double SigmaSm);

/// Laterally infinite perfect/lossy plane at y = Y. Handled by an exact image (§3.4), not meshed.
public sealed record EmGroundPlane(double Y, double SigmaSm);

/// ReferenceConductor == null means "the ground plane". Kernel W will require this to be explicit;
/// carrying it from day one is what keeps that promise cheap.
public sealed record EmPort(int Number, string Conductor, string? ReferenceConductor, Complex Z0);

public sealed record EmProblem(
    IReadOnlyList<EmConductor>       Conductors,
    IReadOnlyList<EmDielectricRegion> Regions,
    EmGroundPlane?                   Ground,
    IReadOnlyList<EmPort>            Ports,
    double                           LengthMeters);
```

**R-mom-3. Dielectric interfaces are horizontal, laterally infinite, and implied by the region list —
never authored directly.** This is the 2.5D premise stated as a data structure: an interface exists at each
`YTop`/`YBottom` boundary where the two adjacent materials differ. A structure that needs a vertical or
sloped dielectric boundary is out of scope for kernel A and must be refused by `CanSolve`, not
approximated.

**R-mom-4. Conductors are finite-thickness closed polygons, never zero-thickness sheets.** Wheeler's
incremental-inductance rule (§6) recedes a conductor surface into the metal; a sheet has no interior to
recede into, so a zero-thickness conductor makes conductor loss undefined rather than merely approximate.
Reject a degenerate outline in `CanSolve` with that reason stated.

---

## 3. The formulation, in full

The equations below are the whole of kernel A. They are written out rather than cited because the sign
conventions are where this kind of solver silently goes wrong, and every one of them is checkable by a
Tier-0/1 test in §8.

### 3.1 Unknowns

One uniform surface charge density σ_j (C/m², per unit length in z) per boundary segment:

- **conductor perimeter segments** carry **free** charge,
- **dielectric interface segments** carry **bound (polarization)** charge.

Because bound charge is carried explicitly, every charge radiates into **free space** — the Green's
function is the plain 2D logarithmic potential. **There is no Sommerfeld integral, no DCIM, and no special
function anywhere in this kernel**, and an arbitrary number of dielectrics costs nothing but more segments.
This is the single property that makes L7 bounded engineering rather than research (§10.2).

### 3.2 The two closed-form integrals

For a straight segment **a**→**b** of length `L`, tangent `û = (b−a)/L`, normal `n̂ = (−û_y, û_x)`, and an
observation point **p**, define local coordinates

```
x  = (p−a)·û            y  = (p−a)·n̂
r₁ = hypot(x, y)        r₂ = hypot(x−L, y)
```

**Potential coefficient** — potential at **p** per unit σ on the segment:

```
F(u) = u·ln(u² + y²) − 2u + 2y·atan(u/y)
Φ    = ½·[ F(L−x) − F(−x) ]
P    = −Φ / (2πε₀)
```

**Self term** (collocation at the segment's own midpoint, `x = L/2`, `y = 0`; the `atan` term vanishes in
the limit):

```
P_self = L·(1 − ln(L/2)) / (2πε₀)
```

**Field coefficients** — `E = σ·∇Φ/(2πε₀)`, in the *source* segment's frame:

```
∂Φ/∂y = atan((L−x)/y) − atan(−x/y)        (the angle the segment subtends at p)
∂Φ/∂x = ln(r₁ / r₂)
E·n̂   = (σ/2πε₀) · ∂Φ/∂y
E·û   = (σ/2πε₀) · ∂Φ/∂x
```

Guard `y == 0` explicitly: off the segment the subtended angle is 0; on it, it is π — which is the
`σ/(2ε₀)` self-field that §3.3 has already accounted for analytically and must therefore be **excluded**.

**R-mom-5. `F_ii = 0` for the principal-value field of a segment on itself.** Getting this wrong
double-counts the self-field and the solver converges smoothly to the wrong answer — which is why Tier 0
tests the field kernel against a finite difference of the potential kernel rather than trusting it.

To project a source segment's field onto an observation segment's normal `m̂`:
`E·m̂ = (E·û)(û·m̂) + (E·n̂)(n̂·m̂)`.

### 3.3 The dielectric-interface equation

With `n̂` pointing **upward** (region 1 below, region 2 above), the field just either side of an interface
carrying bound charge σ_b is `E_n^(2) = E_n^avg + σ_b/(2ε₀)` and `E_n^(1) = E_n^avg − σ_b/(2ε₀)`, where
`E_n^avg` is the principal-value field from every *other* charge. Normal-D continuity `ε₁E_n^(1) = ε₂E_n^(2)`
then gives

```
σ_b = 2ε₀ · K · E_n^avg          K = (ε₁ − ε₂) / (ε₁ + ε₂)     ε₁ = below, ε₂ = above
```

Sign check, which must be in a comment at the implementation site: a positive line charge above a
dielectric half-space gives `K > 0` and a downward `E_n^avg`, hence **negative** bound charge — the
dielectric is attracted. That matches the textbook image `q′ = −q(ε_r−1)/(ε_r+1)`.

### 3.4 Loss enters as a complex permittivity, and it costs one solve

**R-mom-6. The system is complex-valued, with `ε* = ε_r(1 − j·tanδ)` throughout.** `K` is then complex and
`[C]` comes out complex, `C = C′ − jC″`. The per-unit-length shunt admittance is exactly

```
Y = jω·C_complex = ω·C″ + j·ω·C′   ⇒   G = ω·C″ = −ω·Im(C_complex),   C = Re(C_complex)
```

This is §10.3.2 item 4 done exactly rather than by lumping, it handles any number of independently lossy
dielectrics, `G ∝ ω` (constant tanδ) falls out rather than being asserted, and it costs one complex solve
on a matrix of a few hundred. Do **not** implement a separate partial-capacitance accumulation; this
supersedes that sketch. When every `tanδ` is zero the imaginary part must be zero to round-off — assert it
(§8, Tier 2).

### 3.5 The ground plane is an exact image

**R-mom-7. A ground plane is never meshed. Every source segment contributes its mirror about `y = Y_g`
with negated charge**, for both `P` and the field, by mirroring the segment's endpoints and building the
frame from the mirrored pair. Because *all* charge — free and bound — is explicit and radiating into free
space, the image makes φ = 0 on the plane **exactly**, dielectrics included. This is not an approximation
and should be commented as such.

A dielectric interface coincident with the ground plane is not an interface; drop it.

### 3.6 Assembly

Unknowns `σ_j`, `j = 1…N`. Two row types:

| Row | Equation | RHS |
|---|---|---|
| conductor segment `i` on conductor `c` | `Σ_j P_ij σ_j` | `V_c` |
| dielectric segment `i` | `σ_i − 2ε₀·K_i·Σ_j F_ij σ_j` | `0` |

Excite conductor `m` with `V_m = 1`, every other conductor `V = 0`; solve; integrate
`Q_k = Σ_{i∈k} σ_i·Δ_i`. That column is column `m` of the **Maxwell (short-circuit) capacitance matrix**
— positive diagonal, negative off-diagonal — which is the `[C]` that multiconductor transmission-line
theory wants. The ground plane is the reference and contributes no row and no `Q`.

Dense complex LU via NumFlat, **factored once and reused for all `M` excitations** (they differ only in the
RHS). `N` is a few hundred; §10.7's size budget does not bind here and no ceiling needs policing yet.

---

## 4. Meshing (L6's engine half)

`src/Engine/Mom/BoundaryMesher.cs`. One dimension, not two — segments along conductor perimeters and along
dielectric interfaces (§10.5).

```csharp
public sealed record EmMeshSettings(
    int    MinCellsAcrossWidth   = 6,      // §10.5: "at least 3–5"; 6 buys margin cheaply
    int    EdgeCells             = 3,      // 2–4
    double EdgeFractionOfWidth   = 0.03,   // outermost cell, 2–5% of the conductor width
    double EdgeGrowthRatio       = 1.7,    // 1.5–2, geometric inward
    double TruncationHeights     = 20.0,   // interface half-extent, in substrate heights
    int    TruncationTailCells   = 12);    // graded cells in each truncated tail
```

**R-mom-8. Edge grading is geometric, from both ends of every conductor face, and it applies to dielectric
interface segments near a conductor edge too.** The 1/√d current singularity at a conductor edge has a
bound-charge counterpart in the interface directly beside it; grading only the metal leaves the larger
error un-addressed. This is the logic §10.5 says B and C will reuse verbatim, so write it against segment
geometry, not against "the microstrip case".

**R-mom-9. Dielectric interface segments are excluded wherever the interface lies inside a conductor.**
The microstrip strip sits *on* the substrate: its bottom face carries free charge and the interface beneath
it does not exist. Getting this wrong puts two unknowns on the same physical surface and the matrix goes
singular — which is a good failure, but the specific one to test for.

**R-mom-10. The interface truncation distance is an explicit setting with a convergence test, never a
hidden constant.** §10.3.1 names this as the one place kernel A can be quietly wrong. Extend
`TruncationHeights × h` beyond the outermost conductor on each side with a geometrically graded tail, and
gate on §8 Tier 3: doubling it must not move Z₀ by more than the oracle tolerance.

`Mesh(...)` returns an `EmMeshReport` carrying the segment list, the unknown count, per-conductor and
per-interface counts, and the smallest/largest cell — everything the mesh viewer will later draw and
everything §10.5's "report the unknown count *before* solving" needs. **Report it from the engine so the
UI has nothing to recompute.**

---

## 5. Capacitance → RLGC

`src/Engine/Mom/RlgcExtractor.cs`.

1. **`[C]`** — the §3 solve with the real stackup and complex ε*.
2. **`[C₀]`** — the same solve with every material replaced by air. Every `K` is then zero, so the
   dielectric rows drop out entirely; solve the conductor block only. Cheap.
3. **`ε_eff = C/C₀`** (per-mode when multiconductor arrives).
4. **`[L] = µ₀ε₀[C₀]⁻¹`** — the TEM identity. No second formulation.
5. **`G`** — already in `[C]`'s imaginary part (R-mom-6). Nothing further to compute.
6. **`[R]`** — Wheeler's incremental inductance rule, §6 below.

**R-mom-11. `[C]`, `[C₀]` and `∂L/∂n` are frequency-independent and must be computed exactly once for a
whole sweep.** This is the property that makes v1 "dramatically snappier than the thing that replaces it"
(§10.3.2), and it is easy to lose in a later refactor. Enforce it with a fill counter asserted in a test
(§8, Tier 4) — not with a comment.

---

## 6. Conductor loss — Wheeler, without destroying R-mom-11

Wheeler's rule, in the form to implement:

```
R(ω) = (R_s(ω)/µ₀) · ∂L/∂n          R_s = √(ωµ₀/2σ) = 1/(σδ)
```

The naive reading — "recede every surface by δ/2, re-solve, difference" — makes the recession
frequency-dependent and forces a refill per frequency, destroying R-mom-11 for no accuracy gain.

**R-mom-12. `∂L/∂n` is a purely geometric derivative, evaluated once by a single finite-difference
recession, and the frequency dependence enters only through `R_s`.** Use a recession `Δ` that is small
against both the metal thickness and the smallest mesh cell (start at `Δ = min(t, W)/50`, and assert the
result is insensitive to halving it).

**Sum over every lossy surface, each with its own `R_s`:**

- **signal conductors** — shrink the outline inward by `Δ`, recompute `[C₀]` and `[L]`;
- **the ground plane** — move it *down* by `Δ` (equivalently `h → h+Δ`), recompute.

Both perturbations increase `L`, so both `∂L/∂n` are positive; a negative one is a bug, and asserting the
sign is a cheap guard. Omitting the ground-plane term is the common error and it under-reports microstrip
loss noticeably.

**R-mom-13. Report the frequency below which Wheeler is invalid, and floor R with the DC value.**
The rule assumes δ ≪ t. Compute `R_dc = 1/(σ·A)` per conductor and blend `R = √(R_dc² + R_wheeler²)` —
labelled in the code as the standard smooth interpolation it is, not as physics — and surface the crossover
frequency `f(δ = t/2)` in the mesh/solve report so a user sweeping down into the invalid region is told
rather than quietly misled.

Surface roughness is **out of scope** for this brief (§10.4 lists it as optional). Leave the field off the
model rather than accepting and ignoring it.

---

## 7. RLGC → s-parameters → `DataSet`

`src/Engine/Mom/RlgcToSparams.cs`. **L7 ships the single-conductor 2-port**; `[C]` is a matrix throughout
so multiconductor modal decomposition is an addition at L7b, not a rewrite.

```
Z  = R + jωL
Y  = jω·C_complex                     (R-mom-6: this is exactly G + jωC)
γ  = √(ZY)          branch with Re(γ) ≥ 0
Zc = √(Z/Y)
```

**R-mom-14. Form the Z-matrix and convert with `RFNetwork.ZToS`; do not write a second ABCD→S.**

```
Z11 = Z22 = Zc·coth(γℓ)
Z12 = Z21 = Zc / sinh(γℓ)
S   = RFNetwork.ZToS(Z, z0PerPort)
```

`RFNetwork.ZToS(Mat<Complex>, Complex[])` already exists in RfCore, already handles per-port and complex
reference impedances, and is already the path every other s-parameter in this project goes through.
Reciprocity is then structural rather than hoped for.

**R-mom-15. De-embedding is a no-op for kernel A, and that is a finding, not a shortcut.** §10.6 requires
de-embedding because a *meshed* port excitation carries a port discontinuity. Kernel A computes γ and Z_c
analytically and forms the ABCD/Z of a uniform line of length ℓ — the reference planes are exactly at the
line ends by construction, and there is nothing to remove. Say so in the code and in `layout-view.md`; the
two-line calibration becomes real work at L8, and building it now would be building a calibration for an
error that does not exist.

**Results follow the house convention exactly** — the same three lines `SParameterEngine` ends with:

```csharp
var snp = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0);
var ds  = DataSetBuilder.FromSnp(snp);
ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort));
```

Then add, in a `"tline"` group, one cube per frequency axis: `Zc` (Complex), `Gamma` (Complex), `Eeff`
(Real), `AttenDbPerM` (Real), and the per-unit-length `Rpul`, `Lpul`, `Gpul`, `Cpul` (Real). These are the
quantities a transmission-line solver is uniquely able to report and they cost nothing; they are also what
makes a wrong answer diagnosable.

**No new result type** (standing invariant). No `.snp` file is written by this brief — that is §10.8's
artifact and belongs to the Ui-side run path.

**Optional, only after every §8 gate is green:** the Kirschning–Jansen dispersion correction of §10.3.2.
`src/Core/Devices/Microstrip/KirschningJansen.cs` already implements it. It applies to the single-microstrip
case only; wire it as an opt-in setting, off by default, and never let it run before the static result has
been validated on its own.

---

## 8. Validation — the gate ladder

**R-mom-16. Validate the charge solver against exact closed forms *before* comparing anything to
Hammerstad-Jensen.** H-J is itself an empirical fit with ~0.2–1% error; a ±2% agreement against it can
hide a real defect, and a disagreement gives you no information about which of five stages is wrong. Each
tier below must pass before the next is written.

`tests/Engine.Tests/Mom/`. Tag anything measured at or above ~5 s with `[Trait("Category","Benchmark")]`
per `CLAUDE.md`; everything here should sit far below that.

**Tier 0 — the integrals, no solver.**
- `P` for random segment/observation pairs vs high-order numerical quadrature, 1e-10 relative.
- `∂Φ/∂x`, `∂Φ/∂y` vs a central finite difference of `Φ`, 1e-7 relative. *(This is the R-mom-5 guard.)*
- Far-field limit: `E → λ/(2πε₀d)` for `d ≫ L`.
- `P_self` vs quadrature with the log singularity handled analytically.

**Tier 1 — conductors only, exact oracles.** All meshed as polygons, all converging under refinement.
- **Coaxial line:** `C = 2πε₀ / ln(b/a)`.
- **Round wire over a ground plane:** `C = 2πε₀ / acosh(h/a)` — this is what tests R-mom-7's image.
- **Two parallel wires:** `C = πε₀ / acosh(d/2a)`.

**Tier 2 — bound charge.** These are the tests that earn §3.3.
- **Two-layer coax:** `C = 2πε₀ / [ ln(r_m/a)/ε₁ + ln(b/r_m)/ε₂ ]`. Exact, and the only cheap closed form
  that genuinely exercises a dielectric interface.
- **Fully-filled coax:** `C = 2πε₀ε_r / ln(b/a)`.
- **Air check:** every `ε_r = 1` ⇒ `C == C₀` to 1e-12, and `Im(C) == 0` to round-off.
- **Lossy fill:** fully-filled coax with `tanδ` ⇒ `C_complex = C·(1 − j·tanδ)` in closed form. This
  validates the complex-`K` path of R-mom-6 exactly rather than by plausibility.

**Tier 3 — microstrip. This is the L7 phase gate.**
- Z₀ and ε_eff within **±2%** of `HammerstadJensen.Compute(w, h, t, epsR, …)` across `0.1 ≤ W/h ≤ 10`, on
  **both** starter stackups: 1.6 mm FR-4 / ε_r 4.4 and 100 µm GaAs / ε_r 12.9 (§2.4).
- **Truncation convergence** (R-mom-10): doubling `TruncationHeights` moves Z₀ by < 0.5%.
- **Mesh convergence**: refining monotonically approaches a limit — it must not wander. Assert monotone,
  not merely bounded, because wandering is the signature of an assembly sign error that a tolerance test
  passes by luck.

**Tier 4 — network level.**
- Reciprocity `S₁₂ = S₂₁` to solver tolerance.
- Passivity: eigenvalues of `I − SᴴS ≥ 0`.
- Losslessness: with `σ = ∞` and `tanδ = 0`, `|S₁₁|² + |S₂₁|² = 1`.
- **Cascade identity:** a line of length 2ℓ equals two length-ℓ lines cascaded.
- **R-mom-11 guard:** a 1001-point sweep increments the matrix-fill counter exactly once.

**Tier 5 — loss.**
- Dielectric attenuation against the closed form
  `α_d = (π/λ_g)·ε_r(ε_eff−1)·tanδ / (√ε_eff·(ε_r−1))`, a few percent.
- Conductor attenuation against `src/Core/Devices/Microstrip/MicrostripLoss.cs` — **agreement within 20%
  is the gate, but a disagreement in the √f *slope* is a bug regardless of magnitude.** Two independent
  loss models agreeing on level to 20% and on slope exactly is a strong result; agreeing on level while
  differing in slope means one of them is not modelling skin effect.
- `∂L/∂n > 0` for both the conductor and the ground-plane perturbation (R-mom-12).

---

## 9. The `IEmKernel` seam

`src/Engine/Mom/IEmKernel.cs`.

```csharp
[Flags]
public enum EmCapabilities {
    None = 0, UniformCrossSection = 1, Planar = 2, LayeredWithVias = 4, Wires = 8, Surfaces = 16
}

public sealed record EmSuitability(bool Ok, string? Reason);

public interface IEmKernel {
    string         Name         { get; }
    EmCapabilities Capabilities { get; }
    EmSuitability  CanSolve(EmProblem problem);
    EmMeshReport   Mesh(EmProblem problem, EmMeshSettings settings);
    DataSet        Solve(EmProblem problem, EmMeshSettings settings, double[] freqsHz, CancellationToken ct);
}
```

`QuasiStaticKernel` reports `UniformCrossSection`.

**R-mom-17. `CanSolve` returns a *specific* reason, and it is the only place a refusal is worded.**
§10.3.3's requirement — *"This geometry has a bend at (x, y); the quasi-static solver handles uniform
cross-sections only. Full-wave analysis of discontinuities arrives in L8"* — is the difference between v1
reading as bounded and reading as broken. Kernel A's own refusals are the ones it can see from an
`EmProblem`: a non-horizontal dielectric boundary (R-mom-3), a degenerate/zero-thickness conductor
(R-mom-4), a self-intersecting outline, a port naming a conductor that does not exist, a port with no
resolvable reference, and `Ground == null` combined with a stackup that has no reference conductor. The
*geometric* refusals — bends, tapers, non-parallel conductors — are detected by the Ui-side extractor
before an `EmProblem` is ever built, and its message must follow the same shape.

**Do not build a kernel registry yet.** One kernel, constructed directly. The registry earns its place when
kernel W or B exists; adding it now is speculative plumbing with no second implementation to constrain it.

---

## 10. File map

```
src/Engine/Mom/
  EmProblem.cs          — EmPoint, EmMaterial, EmDielectricRegion, EmConductor, EmGroundPlane, EmPort, EmProblem
  IEmKernel.cs          — IEmKernel, EmCapabilities, EmSuitability
  EmMeshSettings.cs     — settings record (§4)
  EmMeshReport.cs       — segments, unknown count, per-conductor/interface counts, min/max cell
  BoundaryMesher.cs     — perimeters, interfaces, edge grading, truncation, R-mom-9 exclusion
  Kernel2D.cs           — the §3.2 closed forms, plus the R-mom-7 image
  ChargeSolver.cs       — assembly (§3.6), NumFlat LU, one factorisation for M excitations
  RlgcExtractor.cs      — [C], [C₀], [L], [G], Wheeler [R]
  RlgcToSparams.cs      — γ, Z_c, Z-matrix, RFNetwork.ZToS, DataSet assembly
  QuasiStaticKernel.cs  — IEmKernel implementation; CanSolve
  CLAUDE.md             — conventions + the sign conventions of §3, written as this lands

tests/Engine.Tests/Mom/
  Kernel2DIntegralTests.cs          — Tier 0
  ClosedFormCapacitanceTests.cs     — Tier 1 + Tier 2
  MicrostripOracleTests.cs          — Tier 3 (the L7 gate)
  NetworkPropertyTests.cs           — Tier 4
  LossTests.cs                      — Tier 5
  MeshingTests.cs                   — grading, exclusion, truncation, report contents
  Support/EmProblemBuilders.cs      — coax, wire-over-ground, two-wire, microstrip builders
```

## 11. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | `EmProblem`, `Kernel2D`, a trivial uniform mesher, `ChargeSolver` conductors-only | Tier 0 + Tier 1 green |
| **M2** | Bound charge, complex ε*, the image ground | Tier 2 green |
| **M3** | `BoundaryMesher` — edge grading, interface exclusion, truncation, `EmMeshReport` | Tier 3 green — **this is the L7 acceptance gate** |
| **M4** | `RlgcExtractor` incl. Wheeler, `RlgcToSparams`, `DataSet` | Tier 4 + Tier 5 green |
| **M5** | `IEmKernel`, `CanSolve` refusals, `src/Engine/Mom/CLAUDE.md` | Every refusal in R-mom-17 has a test asserting its wording is specific |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened to make
it pass. A widened tolerance in Tier 1 or Tier 2 is not a judgement call — those are exact closed forms.

---

## 12. Explicitly out of scope

- **All UI.** No mesh viewer, no EM setup panel, no stackup-editor change, no cross-section extraction from
  `.clay` geometry, no `.snp` written to disk. Next brief.
- **Multiconductor s-parameters** — `[C]` is a matrix, but modal decomposition and coupled-line S is L7b.
- **Full-wave** (L8/L9) and **wirebonds** (LW1/LW2).
- **Surface roughness**, **adaptive frequency sampling**, **the N-ceiling of R17** — none binds a solver
  whose matrix is a few hundred square.
- **Stripline** (ground above *and* below). It needs an infinite image series rather than one image; it is
  a small, well-defined extension and should be added *after* Tier 3 is green, with its own convergence
  test on the series truncation — not folded into M2.
- **DRC (L5b)**, which is still unbuilt and independent of this arc.
