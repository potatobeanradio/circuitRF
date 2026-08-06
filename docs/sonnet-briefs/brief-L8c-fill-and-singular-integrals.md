# Sonnet Brief — Phase L8c: basis functions, the matrix fill, and the singular integrals

**Design:** `docs/design/layout-view.md` **§10.2 (the honest cost — read the "item 4" paragraph
first)**, §10.5 (the rooftop basis), §10.7 (the size budget and R17), §10.3.4 (the kernel seam).
Phase table row **L8 — Full-wave, single dielectric (B)**, third of five slices.

**Read `src/Engine/Mom/CLAUDE.md` §L8a AND §L8b end to end before planning anything.** L8a built the
layered Green's function and measured where it is accurate; L8b built the mesher, fixed the cell
ordering contract, and produced the unknown counts this slice is scheduled against. Both record
findings that will cost you a day each if you rediscover them.

**§10.2 names two places a schedule goes to die. L8a survived the first. THIS SLICE IS THE SECOND.**
The design note's own words: *"Item 4 (singular self- and near-term integrals) is the second such
place."* Everything below is arranged around that sentence.

---

## Gate command — and it is NOT the full solution

**Run `dotnet test tests/Engine.Tests` and `dotnet test tests/Ui.Tests`, as two invocations** (this
SDK's `dotnet test` rejects two explicit project paths in one call), plus `dotnet test
tests/Firewall.Tests` whenever an assembly reference could have moved. **Do not run the
full-solution `dotnet test` at the repo root as a routine gate for this slice.**

**This applies to every L8 slice — L8a through L8d.** The full-solution run is required **once, at
the end of L8**, as part of L8e's gate. The reasoning is unchanged from L8a/L8b and the second
reason is measured rather than assumed:

1. **The slices touch two directories.** L8a–L8d live in `src/Engine/Mom/` and `src/Ui/Layout/Em`
   (plus L8b's renderer file). The seams that could affect anything else — `IEmKernel`,
   `EmCapabilities`, the kernel registry, the narrowed refusals — are deliberately *not opened* until
   L8e. Running 6,800 tests after each slice is cost with no signal.
2. **The full-solution run is where an unrelated test's timing budget bites.** `Hero1BTests` gates on
   a 10 s import-plus-solve wall clock. Measured at L8a under full-solution load **with L8's tests
   excluded**: 4.2 / 9.6 / 8.6 s — it already reaches its own gate on that machine, independently of
   this phase. **Do not "fix" `Hero1BTests` by widening its budget.**
3. **L8e is where the full run earns itself**, because L8e is where the interface actually changes.

If a slice does something that could plausibly reach outside those directories — it should not, but
if it does — say so in the report rather than quietly running the full suite and moving on.

**Tagging.** L8b added 22 tests that run in ~0.3 s and tagged none. **This slice will not be that
cheap**: Tier 2's brute-force oracle and Tier 8's cost sweeps are genuinely expensive. Follow the
precedent L8a set — tag the sweeps `Category=Benchmark`, keep **one representative case per starter
technology** in the routine gate, and say in the report which cases were moved out and why. The
mechanism is `--settings circuitrf.benchmark.runsettings`; the root `CLAUDE.md` explains why a
command-line `--filter` cannot be used for this.

---

## 0. Read this before planning anything

**There are THREE singular pieces in this kernel, not one, and the second is the one that gets
missed.** `DcimModel.Evaluate(ρ)` (read it — it is thirty lines) returns

```
G(ρ) = (1 + QuasiStatic)·e^{−jk₀ρ}/(4πρ)          ← 1/ρ   singular
     + Σ_poles  Residue·(−j/4)·H₀⁽²⁾(k_p ρ)        ← ln ρ  singular
     + Σ_images A_i·e^{−jk₀R_i}/(4πR_i),  R_i = √(ρ² + b_i²)   ← smooth, b_i ≠ 0
```

- The **first** term is the obvious one and every MoM text tells you to extract it.
- The **second** is not obvious and is not optional. `H₀⁽²⁾ = J₀ − jY₀` and `Y₀(z) ~ (2/π)(ln(z/2)+γ)`
  as z → 0, so **every surface-wave term carries a real logarithmic singularity**. A grounded slab
  always has at least one surface wave, however thin — L8a's R-lgf-3 verified that down to h = 1 µm.
  An implementation that extracts only 1/ρ will push a log through Gauss-Legendre and get an answer
  that converges slowly toward something plausible and wrong.
- The **third** is smooth *provided* no fitted image depth is small compared to a cell. Check it
  (R-fil-8); do not assume it.

**The second thing to internalise: rectangles make the hard integral easy, and that is the whole
payoff of D8.** The inner integral of `1/R` over a rectangle, for an observation point **in the same
plane**, is closed form:

```
∫₀^a ∫₀^b  du dv / √(u² + v²)  =  a·asinh(b/a) + b·asinh(a/b)
```

corner-summed with signs for a general rectangle and a general in-plane observation point. (Check:
the centre of a unit square gives 4·asinh(1) ≈ 3.5255.) `∫∫ ln r dS'` over a rectangle likewise has
a closed form, and so does the **first moment** `∫∫ u'/R dS'` that a rooftop's linear weight needs.
**Derive these three; do not transcribe them** — the same D4 rule L8a followed, and they are short
enough to derive and to check against adaptive quadrature to 1e-12.

With the inner integral analytic, **the classic near-singular problem largely disappears**: the
"nearly touching cells" difficulty in a triangle code comes from doing both integrals numerically.
Here the inner one is exact and the outer one sees a continuous function with a kink. That is a
quadrature-order question, not a special case. Say so, and measure it.

**The third thing: this slice produces a matrix nobody excites, and that is fine.** Ports are L8d.
The matrix is still fully gateable — against the Sommerfeld oracle entry by entry, against an exact
εᵣ = 1 reduction, against structural symmetry, and against a real physical capacitance in the ω → 0
limit. Those four are the ladder. If you find yourself inventing a port to get a number out, stop:
you have crossed into L8d.

---

## 1. Decisions taken

**D1. Galerkin, not point matching, and reciprocity is STRUCTURAL.** Test with the same rooftop you
expand with. `Z` is complex symmetric because the Green's function is reciprocal — but do not *rely*
on that to come out numerically: **compute only `m ≤ n` and mirror**, so `Z[m,n]` and `Z[n,m]` are
bit-identical by construction. This is exactly the shape L7b/L7b-b already established for the modal
blocks ("reciprocity is structural, at exactly L7b's strength"), and it halves the fill for free.

**D2. Rooftop over an adjacent cell pair, and nothing else.** `PlanarBasis` already defines them —
`(LayerIndex, CellA, CellB, Direction)`, in a fixed order, N of them. Do not invent a second basis
family, a charge-only basis, or a "half rooftop" at the boundary: L8b's mesher emits a rooftop only
where two cells genuinely share an edge, so the basis set is already exactly the set with no charge
accumulating on the outer boundary. **Do not add boundary bases to "improve" the edge current** —
that would put charge on the metal's rim, which is physically wrong and would silently change every
answer.

**D3. THE SINGULARITY IS EXTRACTED, AND THE NUMBER OF EXTRACTED TERMS IS A MEASUREMENT, NOT A
GUESS.** The mandatory extractions are the `1/ρ` and the `ln ρ` above. Whether to also extract the
*next* order (the `−jk₀/4π` constant and the `k₀²ρ/8π` linear term, which make the remainder smooth
rather than merely continuous) is a real trade — one more closed form each, against a
quadrature-order saving. **Measure both and report the order and the cost.** L8a's own precedent
applies exactly: higher-order exact statements are not automatically better, and it recorded a table
rather than a preference.

**D4. The scalar block is assembled from a per-CELL potential matrix, not from per-basis integrals.**
A rooftop's divergence is a **pulse** on each of its two cells (±1/Δ). So

```
Z^φ[m,n] = (1/(jωε₀)) · Σ_{a ∈ m} Σ_{b ∈ n}  s_a s_b  P[a,b],
P[a,b] = ∫_a ∫_b G_q(|r−r'|) dS' dS  /(area normalisation)
```

Build `P` once over **cells** (≈ N/2 of them, and each entry is a pulse–pulse integral with no
linear weight), then assemble `Z^φ` by signed differences. That is roughly a **4× reduction in
integrals** for the scalar half and it is exact, not an approximation. It also makes the ω → 0
capacitance gate (Tier 5) reachable, because `P` *is* the electrostatic potential-coefficient matrix.

**D5. The vector block is BLOCK-DIAGONAL BY DIRECTION, and that is a formulation fact worth
testing.** In Michalski-Zheng Formulation C — which is what L8a derived and what
`SpectralGreens` implements — the vector kernel is a single scalar `G_A` with no `xy` component
(the L8a note: *"the vector kernel is purely TE, the scalar kernel is a genuine scalar with no
k_x/k_y left in it"*). So `f_m · f_n = 0` whenever an X-rooftop meets a Y-rooftop, and **an
X-rooftop couples to a Y-rooftop through the SCALAR term alone**. Halves the vector fill, and gives
Tier 4 a test that catches a formulation error immediately.

**D6. The FREQUENCY-INDEPENDENT core is computed once per sweep, and it is enforced by a COUNTER.**
The Green's function is per-frequency (L8a's R-lgf-5) and the fill is therefore per-frequency — but
**the extracted singular cores are the static `1/R` and `ln r` integrals, which are not.** Compute
those once per mesh and reuse them across every frequency. This is R-mom-11's rule applied to kernel
B, and R-mom-11's own lesson applies too: *"it is easy to lose in a refactor — so it is enforced by
`RlgcModel.MatrixFillCount`, asserted at exactly 4 … **not by a comment**."* Do the same here.

**D7. Dense storage, NumFlat LU, no compression.** §10.7 is explicit: N² × 16 bytes, LU per
frequency, and ACA/MLFMM is out of scope. `ChargeSolver` already establishes the idiom
(`var lu = a.Lu(); lu.Solve(rhs)`) — reuse it rather than inventing a second dense-solve path.
**R17's ceiling is enforced HERE as well as in the mesh report**: refuse before allocating, with the
message `SurfaceMesher` already words.

**D8. No ports, no excitation vector, no s-parameters, no de-embedding.** L8d. The one excitation
this slice may construct is the **static capacitance harness** of Tier 5 (all cells at 1 V), and it
is a TEST fixture, not a product surface — it must not appear on `IEmKernel`, on the `.cem`, or in
the panel.

**D9. Nothing about staircasing is revisited.** L8b measured it: the mitre survives (2.8% cut-area
error at the auto cell size), the smooth tapers do not (17–24% worst local width error against a
0.5% area error). **Do not attempt conformal or diagonal boundary cells here.** If Tier 6's
convergence study says the taper's error is what dominates, that is a finding for a later brief, not
a licence to change the mesher inside this one.

---

## 2. What already exists, and what genuinely does not

**Exists and is reused unchanged — none of this should be re-implemented:**

- **`PlanarMesh`** — `Cells` (ordered `(LayerIndex, IY, IX)`, an integer comparison with no
  floating-point tie), `Bases` (the rooftops, in a fixed order), `GridX`/`GridY`. **The ordering is a
  permanent contract**: L8c's matrix rows and columns index by `Bases`, L8d's excitation by the same,
  L8e's heat map by `Cells`. Do not re-sort anything.
- **`PlanarMeshReport.UnknownCount`** — N, the matrix dimension, already exact and already gated
  (Tier 2 of L8b recounts it independently). **N = 552 for §10.7's own hero**; every non-Manhattan
  shipping PCell measures 536–2,055. That is what this slice is scheduled against.
- **`SurfaceMesher.UnknownCeiling`** (5,000) and its refusal wording.
- **`Dcim.Fit` → `DcimModel.Evaluate(ρ)`** — the production Green's function, closed form.
  **`Dcim.WithinValidatedRange`** is the R-mom-17 refusal, worded on the STRICT relative measure.
- **`SommerfeldIntegral.Evaluate`** — the second, independent formulation, and **the oracle**. It
  requires a LOSSY slab (a lossless grounded slab puts its TM₀ pole on the contour); both starter
  substrates are lossy, so this costs nothing.
- **`StaticGreens`** — the exact ω → 0 branch, with a COMPLEX K. L8a records at length why the real-K
  version reads exactly like a convergence floor and is not one.
- **`Bessel`** — J₀/J₁/Y₀/Y₁/H₀⁽²⁾ for complex argument, measured to 2.9e-13 / 5.6e-11.
- **`EmCapabilities.Planar`** already exists and does not need widening.

**L8a's measured accuracy, which is the budget this slice inherits:** error as a fraction of the
free-space kernel — *"what a matrix fill experiences (an entry perturbed by ε·(1/4πρ) perturbs the
linear system by ε)"* — is **≤ 6e-3 across ρ/λ ∈ [1e-4, 10] on both starters**, and L8a says in as
many words that this *"is the number L8c should be scheduled against"*. **Do not chase a quadrature
error below the kernel's own.** Measure both and report them side by side; a fill accurate to 1e-9
against a kernel accurate to 6e-3 is wasted work, and saying so is part of the deliverable.

**Does NOT exist:**

- **Any basis function anywhere in this repository.** Kernel A has none — it collocates charge on
  boundary segments.
- **Any surface integral.** `Kernel2D` integrates over a *line* in 2-D; its `Φ = ∫ ln r ds` is the
  wrong dimension and the wrong kernel. It is a useful thing to have read, not a thing to call.
- **Any singularity-extraction machinery, any Duffy transform, any adaptive 4-D quadrature.**
- **Any dense complex matrix at this scale.** `ChargeSolver`'s is a few hundred square and real-ish;
  5,000² complex is 400 MB and needs the ceiling honoured before allocation.
- **Any Gauss-Legendre rule reachable from outside `SommerfeldIntegral`** — its `LegendreNodes` is
  private. Either widen it (one line, and it is the same nodes computed the same way) or restate it;
  **do not add a package** — the root `CLAUDE.md` reserves dependency additions to the owner, and
  L8a already wrote its own nodes by Newton on the Legendre recurrence for exactly this reason.

---

## 3. Requirements

**R-fil-1. The basis is defined once, and its divergence is exact.** `∫ ∇·f dS = 0` to machine
precision over the rooftop's own pair — a basis that does not conserve charge puts a monopole on
every cell and the answer is wrong in a way that looks like a bad mesh. `f` is continuous across the
shared edge and zero on the pair's outer edges.

**R-fil-2. `Z` is complex symmetric, bit-identically.** Computed on `m ≤ n` and mirrored (D1).
Assert `BitConverter.DoubleToInt64Bits` equality on both parts, not a tolerance — a tolerance here
tests the Green's function's reciprocity, which is a *different* question and gets its own test.

**R-fil-3. Both singularities are extracted.** `1/ρ` and `ln ρ`. Their contributions are integrated
with the **analytic inner integral**; only the smooth remainder goes through quadrature. State in the
code which term each extraction corresponds to in `DcimModel.Evaluate`, so a future change to the
DCIM's own decomposition cannot silently leave one unextracted.

**R-fil-4. The three closed forms are DERIVED and each is checked against adaptive quadrature to
1e-12**: the zeroth moment `∫∫ dS'/R`, the first moment `∫∫ u' dS'/R`, and `∫∫ ln r dS'`, each over a
rectangle for an in-plane observation point — inside it, on an edge, at a corner, and far outside.
The corner and edge cases are where a naive corner-summed antiderivative divides by zero.

**R-fil-5. The quadrature rule is REPORTED, not hidden.** How many Gauss points, in which direction,
chosen by what rule (a separation-to-size ratio), and the measured error at the worst case. A fill
whose accuracy depends on a magic number nobody wrote down is a fill nobody can debug.

**R-fil-6. Every entry is validated against `SommerfeldIntegral`, never against `Dcim`.** DCIM is the
production path; validating it against itself proves nothing. The oracle entry is built by direct
4-D quadrature over the same two cells with the spectral integral evaluated per point — expensive,
which is why it is one entry and not a matrix, and why it is `Category=Benchmark`.

**R-fil-7. The εᵣ = 1 reduction is exact and is the strongest single check** — the direct analogue of
L8a's own Tier 1 (`3.8e-10`) and of kernel A's `T0_7`/`T1_2` image gate. With no slab the Green's
function is free space plus **one** image, in closed form, for both kernels. The whole fill can then
be reproduced by a completely independent path with no DCIM anywhere in it. **Write this test early**
— it is the one that will find a sign, a factor of 4π, or a transposed index on the first run.

**R-fil-8. Report the smallest fitted image depth relative to the smallest cell.** D3's "the images
are smooth" claim is conditional. If `min|b_i|` is comparable to a cell dimension, an image is
*nearly* singular and the quadrature must know. Measure it on both starters across the band, and say
whether it ever happens.

**R-fil-9. The frequency-independent core is reused, enforced by a counter** (D6), asserted at the
same value for a 3-point and a 101-point sweep — R-mom-11's own test shape, which exists because a
comment does not survive a refactor.

**R-fil-10. R17 is enforced before allocation.** `N > UnknownCeiling` refuses with
`SurfaceMesher`'s own wording, before any `Mat<Complex>` of that size is constructed. A "lightweight"
simulator that OOMs instead of refusing is not lightweight.

**R-fil-11. Determinism.** Same mesh, same frequency, same settings ⇒ bit-identical `Z`, entry by
entry. No parallel accumulation whose order varies, no dictionary iteration on the fill path. If the
fill is parallelised — and at N = 5,000 it should be — parallelise over *rows*, each written once,
never over a shared accumulator.

**R-fil-12. R-msh-5's DEFERRED HALF IS CLOSED HERE.** L8b measured N under both candidate edge
reference lengths and recorded that *"the CONVERGENCE half of R-mom-8's measurement needs a solver
and belongs to L8c"*. It now exists. **Measure a converged physical quantity** (Tier 5's capacitance
is the obvious one, and it is the same quantity kernel A's own R-mom-8 measurement used in spirit)
**against both references, and record the number the way R-mom-8 records its own.** If the
conductor-width default turns out to be wrong, say so and change it.

**R-fil-13. Nothing here decides a port, a reference impedance, a mode or a normalisation.** Those
are L8d's, and a "temporary" choice made here will be the one that ships.

---

## 4. The oracle ladder

Same rule as every phase in this area: **each tier passes before the next is written.**

**Tier 0 — the basis.** `∫∇·f dS = 0` exactly; continuity across the shared edge; zero on the outer
edges; the divergence is the expected ±1/Δ pulse; an X-rooftop and a Y-rooftop are orthogonal
pointwise.

**Tier 1 — the three closed forms** (R-fil-4). Against adaptive quadrature, at an interior point, an
edge point, a corner, and far away; both moments and the log form. Include the hand-checkable value:
the zeroth moment at the centre of a unit square is `4·asinh(1) = 3.5255…`.

**Tier 2 — ONE matrix entry against the Sommerfeld oracle.** Self-term, nearest neighbour,
next-nearest, and one far pair, for both kernels, on both starter substrates, at 2 / 10 / 20 GHz.
This is the tier that decides whether the fill is right. Expensive → `Category=Benchmark`, with one
representative case per starter left in the routine gate.

**Tier 3 — the εᵣ = 1 reduction** (R-fil-7). The complete fill, against a direct free-space + single
image quadrature. No DCIM anywhere in the comparison path.

**Tier 4 — structure.**
- `Z` complex symmetric, bit-identical.
- The vector block is zero for every X↔Y pair (D5) — and the scalar block for that same pair is
  *not*, or the test passes for the wrong reason.
- The scalar block assembled from `P` (D4) equals a directly-integrated per-basis scalar block to
  round-off.
- A symmetric plate meshed symmetrically produces a `Z` with the matching permutation symmetry —
  this is what catches a transposed index, which no magnitude check will.

**Tier 5 — the static limit, and a real capacitance.** ω → 0, `P` alone, all cells at 1 V.
- **A plate over a ground plane at small h/W converges to `ε₀A/h`**, with the fringing error falling
  as h/W does. Use εᵣ = 1 so the slab contributes exactly one image and the oracle stays closed-form.
  Report the ratio at three h/W values; the trend is the test, not any single number.
- **An isolated plate** (h → ∞) converges under mesh refinement to a definite value; report it with
  its Richardson extrapolation rather than comparing against a transcribed constant.

**Tier 6 — convergence.**
- Individual entries converge under mesh refinement.
- The capacitance converges at a stated rate.
- The answer converges under **quadrature order** independently of mesh refinement — the two must be
  separable, or you cannot tell a quadrature error from a discretisation error.
- **R-fil-12's edge-reference comparison**, measured on a converged quantity.

**Tier 7 — determinism and the counter.** Bit-identical `Z` across two runs in one process; the
frequency-independent core built exactly once for a 101-point sweep (R-fil-9).

**Tier 8 — cost, measured and reported.** Fill time and peak memory at **N = 552** (§10.7's hero),
at N ≈ 2,000, and at N = 5,000 (the ceiling), per frequency and for a realistic sweep. Report against
§10.10's 30-second target and against §10.7's own table (5,000 → 400 MB). **If the hero does not fill
in a time that makes a 101-point sweep tolerable, that is a finding and it belongs in the report, not
in a later slice's lap.** Say what dominates: the smooth quadrature, the singular cores, or the LU.

---

## 5. What must NOT be built here

- **Ports, excitation, de-embedding, s-parameters, any `DataSet`** — L8d.
- **The current-density heat map** — L8e. L8b already left the provision (one per-cell scalar on
  `LayoutRenderer.DrawPlanarMeshOverlay`); do not wire it to anything.
- **The kernel registry, any `IEmKernel` change, any `EmCapabilities` widening** — L8e.
- **Any change to `SurfaceMesher`, `PlanarMesh` or the cell/basis ordering.** If the fill wants a
  different mesh, that is a finding to report, not an edit to make inside this brief.
- **Conformal or diagonal boundary cells, triangles, a triangulator** — D9.
- **ACA, MLFMM, any matrix compression; adaptive frequency sampling** — §10.7 defers both explicitly.
- **A new dependency of any kind.** Write the quadrature, as L8a wrote its Bessel functions and its
  Legendre nodes, and for the same recorded reason.
- Nothing in `src/Core`, `RfCore`, or `src/Engine` outside `Mom/`. Nothing in `src/Ui` at all.

---

## 6. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | The rooftop basis + the three analytic inner integrals. | **Tier 0 and 1 green**, the unit-square value reproduced. |
| **M2** | The fill: singularity extraction, quadrature, `P`, the assembled `Z`. | **Tier 3 and 4 green**, and **Tier 2 green for at least the self-term and nearest neighbour**. |
| **M3** | The dense factorisation + the static harness. | **Tier 5 and 6 green**, and R-fil-12's edge-reference measurement reported. |
| **M4** | Determinism, the reuse counter, and the cost measurement. | **Tier 7 and 8 green**, with the N = 552 / 2,000 / 5,000 numbers reported. |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass. **In particular: if Tier 2 disagrees with the oracle, check the ORACLE first.**
L8a's own record is that this area has twice found the closed-form "oracle" to be the wrong one, and
kernel A's does the same twice more. Refine the oracle's own integrator and see whether the
discrepancy moves; if it does not move, it is not convergence.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  PlanarBasisFunctions.cs   the rooftop: evaluation, divergence, support (new)
  RectangleIntegrals.cs     the three closed forms + their derivations in the header (new)
  SingularExtraction.cs     which terms are extracted, and the smooth remainder (new)
  PlanarFill.cs             P over cells, Z over bases, the quadrature rules (new)
  PlanarSystem.cs           dense storage, the NumFlat LU, the R17 pre-allocation refusal (new)

tests/Engine.Tests/Mom/
  RectangleIntegralTests.cs   Tier 1
  PlanarFillTests.cs          Tiers 0, 3, 4, 7
  PlanarFillOracleTests.cs    Tier 2  (mostly Category=Benchmark)
  PlanarStaticLimitTests.cs   Tiers 5, 6
  PlanarFillCostTests.cs      Tier 8  (Category=Benchmark)
```

---

## 8. Four things to report back on, whatever else happens

1. **The measured fill cost and memory at N = 552, ≈ 2,000 and 5,000**, per frequency and for a
   realistic sweep, against §10.10's 30-second target — with a breakdown of what dominates. This is
   what L8d and L8e are scheduled against, and it is the number that decides whether §10.7's ceiling
   is generous or optimistic.
2. **The measured accuracy of the fill against the Sommerfeld oracle, beside L8a's own 6e-3
   kernel-accuracy figure** — and the quadrature order and singularity-extraction order that were
   chosen, with the measurement that chose them. If the fill is more accurate than the kernel it is
   filling from, say so plainly; that is useful information about where the next effort should go.
3. **Whether the frequency-independent core is genuinely reused** — the counter's value for a
   101-point sweep — **and what fraction of the total fill it represents.** If it is a small
   fraction, D6 bought little and that is worth knowing before L8d builds a sweep on top of it.
4. **R-msh-5's convergence measurement, closed** (R-fil-12): the edge reference length measured
   against a converged physical quantity, recorded the way R-mom-8 records kernel A's — and an
   explicit statement of whether the conductor-width default L8b chose survives contact with a
   solver.
