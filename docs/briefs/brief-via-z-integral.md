# Sonnet Brief — the via's z-integral: removing the midpoint rule

**Design:** `docs/design/layout-view.md` §10.7 (the cost model and R17's ceiling), §11's phase-table
row **L9**. **This is a FOLLOW-UP to L9, not a sixth slice of it** — L9's five slices are complete and
its phase gate is built and green. It fixes one defect that L9e found and deliberately bounded rather
than repaired. Renumber it into the phase table if you prefer; nothing here depends on the name.

**Read, in this order, before planning anything:**

1. `src/Engine/Mom/CLAUDE.md` **§L9e's "THE FINDING: the via is NOT physically right"** — the whole
   section, including the ℓ/w table and the split-via convergence. That is the defect and its measured
   size, and this brief exists to remove it.
2. **§L9c's M4 and M5** — the via basis ("a rooftop, one dimension over") and the multi-level fill's
   three blocks. Note especially the sentence that states the midpoint rule and its stated cost
   premise: *"the alternative is a fit per z-quadrature node rather than one per pairing — and D7's
   cost projection is written against one per pairing."* **That premise is what M1 below tests.**
3. **§L9b's D5** — a height pair in the top half-space is an EXACT SHIFT of one fit: every amplitude
   unchanged, image depths shifted by Σ, poles scaled by a real decay. And **§L9c's M3/D4** — the
   interior height dependence spans exactly FOUR exponential families, a fifth pair predicted from
   four to 2.4e-13 (same region) and 1.7e-13 (cross region), **and** the second branch point that
   makes a cross-region fit a different problem.
4. **§L8c's Tier 8** — the fill cost table and what dominates it.
5. Then the code, not summaries of it: `PlanarFill.FillMultiLevel`'s three branches (the `zi && zj`
   and mixed arms are what you are changing), `PlanarKernelSet` (`PlanarLevels.MidOf` / `LengthOf` /
   `CanUseMidpointRule`), `Dcim.FitAtHeights`, `SingularExtraction.FromDcimAtHeights`,
   `SommerfeldIntegral.EvaluateInterior`, and **`tests/Engine.Tests/Mom/ViaPhysicsTests.cs` end to
   end** — every oracle this brief needs already exists there.

---

## Gate command

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

**The routine tier has NO headroom.** `Engine.Tests` is at **992 tests in 65-68 s** against the ~60 s
ceiling — already over, and L9e's own note says so plainly rather than smoothing it. **Do not add a
routine test that fills a matrix.** Everything that solves goes behind `Category=Benchmark`; the opt-in
tier is currently ~52 minutes (L8 ~8.5, L9a 8 s, L9b ~11, L9c ~4, L9d ~6, L9e ~10, L9's gate ~12) and
you must say what you add to it.

**One measurement discipline.** L8d's own finding: a benchmark sharing a run with nine others reads
more than twice as slow, and L9d's 71.9 s was first mis-measured at 16.79 s that way. **Take every
timing measurement alone, and say in the report that you did.**

---

## 0. Read this before planning anything

### 0.1 The defect, exactly

`PlanarFill.FillMultiLevel` evaluates the Green's function ONCE, at the midpoint of a via's two feet,
and multiplies by the length:

```csharp
// the ẑẑ branch
double za = levels.MidOf(bi.LayerIndex), zb = levels.MidOf(bj.LayerIndex);
…
z[i, j] += vectorScale * levels.LengthOf(bi.LayerIndex)
                       * levels.LengthOf(bj.LayerIndex) * core;

// the mixed ẑx̂ branch
double zv = levels.MidOf(vertical.LayerIndex);
…
z[i, j] += vectorScale * levels.LengthOf(vertical.LayerIndex) * MixedEntry(…);
```

i.e. `∫∫ dz dz′ G(ρ,z,z′) → ℓ_i ℓ_j G(ρ, mid_i, mid_j)` for ẑẑ, and `∫ dz G(ρ,z,z_h) → ℓ_v G(ρ, mid_v,
z_h)` for the mixed block. **L9c bounded this with an ELECTRICAL condition only** (`kℓ ≤ 0.05`,
`MaxElectricalLength`), on the grounds that the error is O((kℓ)²) — which is true of the wave factor
`e^{−jkR}` and false of the whole substitution, because the same step freezes `1/R` over the via's
length. That is a **geometric** condition with no frequency in it.

**Measured (L9e, `ViaPhysicsTests.T3_1`): the via's terminal series inductance is HIGH by
≈ 0.673·(ℓ/w)**, linear at small ℓ/w and independent of w over a 16× range:

| ℓ/w | 0.01 | 0.05 | 0.075 | 0.1 | 0.5 | 1.0 | 5.0 |
|---|---|---|---|---|---|---|---|
| inductance high by | 0.67% | 3.3% | **4.9%** | 6.4% | 29% | 55% | 220% |

**§10.7's own 3 µm-over-40 µm MMIC post is 4.9% high**, and the electrical bound alone admitted
ℓ/w ≈ 12 on a 20 µm footprint at 10 GHz on GaAs — about 5× too large, silently.

### 0.2 Six things that are true before you start

1. **The KERNEL is right; do not go looking there.** At εᵣ = 1 over a PEC, `G_A^zz` is free space plus
   a **positive** image to **≤ 3.0e-4** over ρ ∈ [1 µm, 1 mm]; against a negative image the same
   comparison reads **21**. The current reflection at a PEC is +1 — L9c's own finding, measured
   against an absolute value rather than a symmetry identity.
2. **The FILL is right.** The ẑẑ entry, converted to henries and separated from the charge term,
   matches an independently-integrated closed form to **≤ 5.1e-5 across ℓ/w ∈ [0.075, 10]**. The
   defect is localised to the quadrature in z and nowhere else.
3. **The remedy is known and converges.** Splitting the via across n intermediate levels — n stacked
   sub-vias each carrying their own midpoint rule over ℓ/n — walks back onto the exact value: at
   ℓ/w = 1, **55.3% → 15.8% → 4.2% → 1.14% → 0.68%** for n = 1, 2, 4, 8, 16; at ℓ/w = 10,
   **385% → 163% → 62% → 20% → 5.9%**. Those numbers are the reference your integral must reproduce
   **at n = 1**.
4. **What ships today is a BOUND, not a fix.** `PlanarLevels.MaxLengthOverWidth = 0.5` (≈ 30% error),
   carrying the measured slope and naming the split as the remedy;
   `PlanarKernel.NarrowestViaFootprint` supplies w from the drawn footprint's own bounding box. It
   makes the failure loud. It does not make the answer right.
5. **This is NOT the binding accuracy limit for via-bearing structures, and the report must say so.**
   `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` on `G_A^zz` already refuses §10.7's own FR-4 hero
   outright (~0.67 λ at 10 GHz), so a via-bearing full-wave run is restricted to electrically small
   structures. **Fixing the midpoint rule widens nothing.** It makes the answers you can already get
   correct; do not let anyone read it as unlocking larger geometry.
6. **L9c declined this on a COST premise, and that premise may be false.** It reasoned that a
   z-quadrature needs "a fit per z-quadrature node rather than one per pairing". Two independent
   reasons to doubt it, and **§3 is where you settle them**:
   - **The vertical block is a vanishing fraction of the matrix.** L9's own phase-gate fixture measures
     **8 vertical unknowns out of N = 1,023** — the ẑẑ block is 64 entries and the mixed block ~1.6%
     of the matrix. A treatment that is 16× dearer *there* is invisible in a 150 s point.
   - **A fit may not be needed per node at all.** L9b's D5 makes a top-half-space height pair an exact
     shift of ONE fit; L9c's M3 measured the interior height dependence spanning exactly FOUR
     exponential families with height-independent coefficients. If that carries into the spatial
     domain, every (z, z′) node is an algebraic recombination of four fits per pairing, not a refit.

---

## 1. Decisions taken

**D1 — the fix is the INTEGRAL, not a correction factor.** The 0.673 slope was measured for a square
bar over a PEC at εᵣ = 1. Applying it as a multiplier would be fitting a number rather than fixing an
integral, and it would be silently wrong the moment the geometry, the medium or the neighbour distance
differs. **Do not ship a factor.** If the integral turns out unaffordable, ship nothing and say so —
the bound already in place is the honest fallback.

**D2 — M1 is a STOPPING POINT, and measuring comes before building.** This area has three precedents
for exactly this: L7b-b weighed Route B against a measurement and declined; L8a's branch-point-order
table chose order 1 on numbers rather than on theory; L9e measured ACA at 53-62% rank and deferred it
with the number. **If M1 says the cost is genuinely prohibitive, stop and report the measurement.**
That is a complete and useful outcome, not a failure.

**D3 — the ELECTRICAL bound stays; only the GEOMETRIC one may retire.** `MaxElectricalLength`
(`kℓ ≤ 0.05`) is about the wave factor and is a real, separate physical condition that a z-quadrature
over a smooth kernel does not remove by itself — the kernel still has to be resolvable along the via.
`MaxLengthOverWidth` is what the integral is for. **Retire it only when Tier 2 shows the ℓ/w error
curve is flat, and widen nothing else.**

**D4 — one-level and no-via answers must be BIT-IDENTICAL (R-viz-1).** Nothing without a vertical
basis may move by one ulp. The horizontal vector block, the scalar block, `PlanarFill.Fill`, every
calibration standard (always single-level) and every L8 path are untouched. This is L9a's D5 precedent
and L9d's R-mlp-1 precedent: pin it by RECONSTRUCTION at full precision, not by a tolerance.

**D5 — no new dependency and no eigensolver.** Gauss-Legendre nodes are computed by Newton on the
Legendre recurrence, as L8a's and L8c's already are and for the recorded reason. A GPOF-with-SVD depth
search is still declined (L7b-b, L8a, L9c all declined it).

**D6 — the scope is the VECTOR blocks only, and you must confirm that rather than assume it.** The
scalar block is built from the ±1/Area divergence pulses at the two FEET (L8c's D4 generalised by
L9c), which sit at the real level heights and involve no z-integral. Read `FillMultiLevel`'s scalar
half and confirm. If it turns out to carry a midpoint anywhere, that is a finding and it belongs in
the report.

---

## 2. What already exists, and what genuinely does not

**Exists, and must be reused rather than rewritten:**

- `Dcim.FitAtHeights` + `DcimModel`'s interior mode + `Dcim.WithinValidatedRangeAtHeights`.
- `SingularExtraction.FromDcimAtHeights`, `PlanarKernelTerms`, `PlanarKernelSet.Get` (with its
  **shared** fit cache across every `For()` view — L9d's own bug fix; do not re-break it).
- `SommerfeldIntegral.EvaluateInterior` / `CanIntegrateInterior` — **the validated oracle for an
  interior height pair**, itself checked to 2.2e-11 against an εᵣ-uniform reduction, 2.4e-15 on its
  own zero-remainder rung, and 9.6e-10 under a 100× coarsening.
- **`ViaPhysicsTests.cs`, the whole file.** `Stack(w, z0, ell, n)`, `AirOverPec`, `SeriesInductance`
  (with the exact algebraic ω-separation that L9e needed — read its doc comment before you re-derive
  it), `MeanOverSquare`, `MeanInverseExact`, `MidpointInductance`, `ExactInductance` /
  `ExactInductanceQ`, and `T2_0`'s oracle self-check. **The ladder you need is already built.**
- `PlanarKernelSet.FitCount` and `PlanarFillCores.CoreFillCount` — the counters M1 reports on.

**Does NOT exist:**

- Any quadrature in z anywhere.
- Any sharing of a fit across height pairs in the SPATIAL domain (L9b's D5 shift is implemented for
  the top half-space; nothing analogous exists for interior pairs).
- Any treatment of the ẑẑ **self** term's z-coincidence — with the midpoint rule the case never arose.

---

## 3. M1 — the measurement that decides the design

**This milestone produces numbers and a recommendation. It changes no production code.**

**R-viz-2 — measure the three costs, separately, on L9's own phase-gate fixture** (the 300 × 100 µm
airbridge, MMIC starter, shipping mesh, N = 1,023 with 8 vertical unknowns — `L9PhaseGateTests`'
`Airbridge()` is the geometry, and 149.9 s per de-embedded point is the baseline to compare against):

1. **How many height pairs a z-quadrature actually asks for.** With n_z Gauss nodes per via: the ẑẑ
   block wants n_z² pairs per level pairing, the mixed block n_z. Report the count for n_z ∈ {2, 4, 8}
   and for the fixture's own pairing count.
2. **What one extra height pair costs.** A `Dcim.FitAtHeights` (L9b measured 11-102 ms per frequency
   per kernel), a `FromDcimAtHeights` decomposition, and — this is the one to watch — a radial
   remainder TABLE. L8c's tabulation is what made the fill seconds instead of minutes; n_z² tables per
   pairing may or may not be affordable in build time and memory. **Measure both.**
3. **Whether the fits are needed at all.** L9c's M3 established that at a fixed k_ρ the interior height
   dependence spans four exponential families with height-independent coefficients. Ask the SPATIAL
   question: **can a DCIM model at four reference height pairs predict a fifth pair's spatial kernel**,
   the way L9b's D5 shift does for the top half-space? Check against `EvaluateInterior` at the same
   ρ range Tier 5 of L9c used, and report the error as a fraction of the free-space kernel (L8a's
   scaled measure — the strict relative measure says more about G_q's cancellation zones than about
   the method).

**The pass condition is a RECOMMENDATION, not a threshold**: report what a z-quadrature adds to a
149.9 s de-embedded point, as a percentage, and say which of the three routes you propose —
(a) refit per node, (b) four-fit algebraic recombination, or (c) stop.

**If the answer is (c), the brief ends here and that is a complete outcome.** Write the numbers into
`src/Engine/Mom/CLAUDE.md` beside L9e's own finding and leave the geometric bound in place.

---

## 4. The formulation, stated as requirements

**R-viz-3 — the z-quadrature is Gauss-Legendre and its order is a MEASUREMENT.** Away from coincidence
the integrand varies on the scale of `min(footprint, distance to the nearest interface)`, so a modest
rule should converge fast — but say so with a convergence table (n_z ∈ {2, 4, 8, 16} against the exact
bar integral) rather than by assertion, exactly as L8c did for its own rules. **Report the order you
chose and why**, and make it a setting on `PlanarFillSettings` so the measurement can be re-taken.

**R-viz-4 — the SELF term is the hard part, and it is a genuinely different integral.** For a via
basis against itself the in-plane separation goes to zero AND the z separation goes to zero, so the
combined integrand carries a `1/R` with `R = √(ρ² + (z−z′)²)`. L8c's singular extraction is written
for the in-plane problem at a FIXED height pair and does not cover this. Two viable routes, and the
choice is yours to measure:

- extract the full 3-D static `1/R` piece over the prism pair and integrate it in closed form, leaving
  a smooth remainder for quadrature — the standard MoM shape, and `ViaPhysicsTests.ExactInductance` is
  already exactly that integral for the εᵣ = 1 case, so the oracle exists;
- or keep the in-plane extraction and refine only the z rule near coincidence, if the measurement says
  the z-direction singularity is weak enough at realistic ℓ/w.

**Whichever you choose, `T2_0`'s discipline applies: check the oracle before concluding from it.** This
area has been burned seven times by an oracle being the thing that was wrong.

**R-viz-5 — the MIXED block integrates over ONE z only.** Its integrand is `j ∂G/∂x`, whose own
asymptote is a logarithm rather than a 1/ρ (L9c's M5), so it is the block that is currently done by
direct graded quadrature. Adding a z rule there is n_z evaluations, not n_z², and its singular
structure is milder. Do it, and say what it cost.

**R-viz-6 — reciprocity stays STRUCTURAL and must be re-verified as such.** L9c's `Z` symmetry rests on
`G_A^uz = −G_A^zu` with the heights swapped, compensated by the ẑx̂ component being odd in x − x′. A
z-quadrature must preserve that pairing exactly — the node sets on the two sides of the diagonal have
to be the same set, not merely the same rule. **Assert bit-identity of `Z[m,n]` and `Z[n,m]` on a
two-level mesh with a via, with the mixed block asserted non-zero so the test cannot pass for the
wrong reason** (L9c's own gate does exactly this; extend it rather than replacing it).

---

## 5. The oracle ladder

| Tier | What | Where it comes from |
|---|---|---|
| 0 | **The oracles check out before anything is concluded from them** | `ViaPhysicsTests.T2_0`, extended to whatever new closed form R-viz-4 needs |
| 1 | **The spatial four-family question** (M1 item 3) vs `EvaluateInterior` | new, in `ViaPhysicsTests` |
| 2 | **THE GATE: the ℓ/w error curve is FLAT** — re-run `T3_1`'s sweep and show the 0.673 slope is gone, ≤ 1% over ℓ/w ∈ [0.01, 5] | `T3_1`, rewritten around the new integral |
| 3 | **Subdivision invariance**: n = 1 now equals n = 8 and n = 16 to the fill's own quadrature | `T3_1`'s split table, reused as the reference |
| 4 | **R-viz-1 bit-identity**: a one-level problem, a two-level problem with no via, and every calibration standard are unchanged to the last ulp | reconstruction at full precision, L9d's `M1_1` is the model |
| 5 | **Reciprocity structural** (R-viz-6) and passivity | L9c's own symmetry gate, extended |
| 6 | **Cost**: a de-embedded point against 149.9 s, measured ALONE | L9's phase-gate fixture |
| 7 | **L9's phase gate re-run** — both Benchmark gates must still pass, and gate 1's \|S₂₁\| table is expected to move only slightly (the posts are ℓ/w = 0.075, i.e. 4.9%) | `L9PhaseGateTests` |

**Tier 2 is the whole point of the brief.** If it does not go flat, nothing else matters.

---

## 6. What must NOT be built here

- **A correction factor** (D1). Not a fitted slope, not a table, not an ℓ/w-dependent multiplier.
- **Any widening of `Dcim.ValidatedRhoOverLambda`, `ValidatedRhoOverLambdaLayered`,
  `ValidatedRhoOverLambdaAtHeights`, or `Dcim.CanFit`.** §0.2 item 5 is why. L9c measured the 14× error
  that justifies the 0.1.
- **The ATTACHMENT (half) basis a backside via needs.** L9's phase gate established that a via to the
  ground plane is not representable at all — that is a bigger, separate piece of work and it is named
  in `src/Engine/Mom/CLAUDE.md` as the second-most valuable thing to build in this area. **Not here.**
- **A losslessness check.** Still not added anywhere, and still more true with vias.
- **Any change to `SurfaceMesher`, `PlanarMesh`, the cell/basis ordering, `SurfaceMesher.UnknownCeiling`,
  the port model, the calibration, or adaptive sampling.**
- **A general complex eigensolver, GPOF-with-SVD, vector fitting, ACA, or a new package** (D5).
- **A new starter technology.** Hand-build fixtures beside the tests that need them, as
  `ViaPhysicsTests` already does.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M1** | The cost measurement (§3) and the route recommendation | Numbers reported; **a legitimate stopping point** |
| **M2** | The z-quadrature for the ẑẑ block, non-self entries | Tier 2's curve flat for well-separated vias |
| **M3** | The self and near terms (R-viz-4) | Tiers 0, 2, 3 |
| **M4** | The mixed block (R-viz-5) | Tier 5's reciprocity bit-identity |
| **M5** | Retire `MaxLengthOverWidth`, keep `MaxElectricalLength` (D3); update every refusal string that quotes the 0.67 slope | `EmRefusalWordingTests`' sweep still green; the refusal names a real remaining limit or is gone |
| **M6** | Cost + L9's phase gate re-run | Tiers 6, 7 |

**The natural fault line is after M1.** If M1 recommends (c), stop. If M2 lands and M3 turns out to be
a week of singular-integral work, **stop after M2 and report** — a via with ℓ/w ≲ 0.1 whose self term
is still the midpoint value is strictly better than today and the bound can be narrowed rather than
retired.

---

## 8. File map (indicative)

```
src/Engine/Mom/PlanarFill.cs          FillMultiLevel's ẑẑ and mixed branches; the new z rule, and
                                      PlanarFillSettings (same file, line ~130) gains the z-node
                                      count (R-viz-3) so the measurement stays re-takeable
src/Engine/Mom/SingularExtraction.cs  the 3-D static extraction, if R-viz-4 takes that route
src/Engine/Mom/PlanarKernelSet.cs     MaxLengthOverWidth's retirement (M5); the fit cache, untouched
src/Engine/Mom/Dcim.cs                ONLY if M1 recommends the four-fit recombination
tests/Engine.Tests/Mom/ViaPhysicsTests.cs   the ladder, extended — do not start a new file
```

---

## 9. What to report back on, whatever else happens

1. **M1's three numbers**, and which route you took. If you stopped, say what the cost was.
2. **The ℓ/w curve after the fix** — the same table §0.1 carries, re-measured. This is the deliverable.
3. **Whether n = 1 now equals n = 8**, and to what.
4. **What the self term needed**, and whether the oracle was checked before it was believed.
5. **The cost of a de-embedded point** against 149.9 s, measured alone, and what it adds to the opt-in
   tier.
6. **What `MaxLengthOverWidth` became** — retired, narrowed, or kept, with the reason.
7. **Any place the answer moved that should not have** (R-viz-1). If a one-level answer moved by an
   ulp, say so and say why rather than adjusting a tolerance.
