# Sonnet Brief — Phase L8d: ports, excitation, the per-frequency solve, and de-embedding

**Design:** `docs/design/layout-view.md` **§10.6 (ports and de-embedding — read the "de-embedding is
mandatory" bullet AND the L7 amendment under it, which says in as many words that this is where it
becomes real work)**, §10.7 (the solve and the measured cost blockquote L8c added), §10.9 (the
oracles, including the paragraph that says **losslessness does not survive into kernel B**), §10.3.2
(kernel A's own RLGC → s-parameter path, which is the thing B must agree with on a uniform line).
Phase table row **L8 — Full-wave, single dielectric (B)**, **fourth of five slices**.

**Read `src/Engine/Mom/CLAUDE.md` §L8a, §L8b AND §L8c end to end before planning anything**, plus
**R-mom-14/R-mom-15** in the kernel-A part of the same file — R-mom-15 is the paragraph that explains
why de-embedding was a no-op for A and is not for B, and it is the whole premise of this slice.

**L8c's own closing measurement is your schedule.** A 101-point sweep of §10.7's hero is ~3 minutes
of fill *before* anything is excited, and this slice multiplies the number of meshes by three. Plan
the test suite around that from the first line of code, not after the gate turns amber.

---

## Gate command — and it is still NOT the full solution

**Run `dotnet test tests/Engine.Tests` and `dotnet test tests/Ui.Tests`, as two invocations** (this
SDK's `dotnet test` rejects two explicit project paths in one call), plus `dotnet test
tests/Firewall.Tests`. **Do not run the full-solution `dotnet test` at the repo root as a routine
gate for this slice.** The reasoning is unchanged from L8a/L8b/L8c and is recorded in all three: the
slices touch two directories, `Hero1BTests`' 10 s budget is already marginal on this machine
independently of this phase, and **L8e is where the full run earns itself** because L8e is where the
interface actually changes.

**Tagging.** L8c added 81 routine tests (~11 s) and 17 `Category=Benchmark` (~6 min). **This slice is
more expensive per test than any before it** — every de-embedded answer costs a DUT solve plus two
calibration solves, at every frequency. Budget accordingly:

- **The routine gate stays under ~20 s of new tests.** Achieve that by testing the *algebra* on
  deliberately coarse meshes (a line three cells across and twenty long is N ≈ 100 and fills in
  milliseconds) — the T-matrix algebra, the port operator, the branch resolution and the
  self-consistency identities are all exact regardless of mesh quality, and a coarse mesh tests them
  just as hard.
- **Everything that needs a physically converged answer** — the A-vs-B comparison, the feed-length
  convergence study, the stub, the cost sweep — is `Category=Benchmark`, opt-in via
  `--settings circuitrf.benchmark.runsettings`. Keep one representative case per starter technology
  in the routine gate, as L8a and L8c both did, and **say in the report which cases were moved out.**

---

## 0. Read this before planning anything

**There are four separable problems here and only the third one is hard.** Naming them apart is what
keeps this slice from becoming a fog:

1. **Excitation** — turning a port into a right-hand side and a solution into a Y-matrix. This is
   twenty lines and it is exact. D1 below makes it structural.
2. **The calibration standards** — synthesising uniform reference lines whose port neighbourhood is
   *identical* to the DUT's. D4 makes this exact by construction rather than approximate by
   coincidence, and if you get it wrong every number downstream is smoothly, plausibly wrong.
3. **The de-embedding algebra** — γ, the error box, the branch ambiguities. This is where the care
   goes. It is classical (it is TRL's line-line step) and it is fully derivable; §1's D5/D6 write
   down the equations so nobody has to guess a convention.
4. **The reference impedance** — and this is the one that is genuinely *not* determined by the
   calibration. See D7. **Read D7 before you write any code, because the natural assumption — that
   the de-embedded S comes out at 50 Ω — is false, and discovering it late means rewriting the
   result path.**

**The second thing to internalise: the calibration is exact only insofar as the error box is the SAME
object in the DUT and in the standard.** That is not a tolerance, it is a construction. Everything in
D4 exists to make the port's cell neighbourhood cell-for-cell identical between the two — same
transverse gridlines, same graded end run, same outer stub. A standard that is "the same line,
re-meshed" is *not* the same error box, and the difference between the two shows up as a de-embedding
residual that looks exactly like a convergence problem.

**The third thing: kernel B is NOT lossless and must never be made to be.** L8a wrote the warning
into `src/Engine/Mom/CLAUDE.md` in advance, and this is the slice it was written for:

> Kernel A's losslessness oracle does NOT carry over to kernel B. With σ = ∞ and tanδ = 0 a closed 2D
> cross-section is exactly lossless, but an *open* planar structure radiates and launches surface
> waves — both of which carry real power away — so `|S₁₁|² + |S₂₁|² < 1` **legitimately**. Reciprocity
> and passivity carry over; losslessness does not. Whoever writes L8d/L8e must not copy
> `TC2_TheFourPortIsLosslessWhenEveryLossIsIdeal` across and then "fix" the kernel until it passes:
> that would mean suppressing radiation, which is one of the two things L8 exists to model.

Do not write that test. **Measure the missing power instead and report it** (R-prt-11) — it is one of
the two capabilities this whole kernel exists to buy.

---

## 1. Decisions taken

**D1. A PORT IS AN INCIDENCE MATRIX, AND RECIPROCITY IS STRUCTURAL AGAIN.** A port resolves to a
*row* of rooftops crossing one gridline of the feed — not to a single basis, because a feed is
several cells wide. Collect them into an N × P matrix `B` whose column *i* holds ±1 on port *i*'s
bases and zero elsewhere. Then, with `Z I = V` the L8c system:

```
V = B·v   (a delta-gap of v volts on every basis in the row)
i = Bᵀ·I  (the port current is the signed sum of those bases' currents)
⇒  Y = Bᵀ Z⁻¹ B                     P columns, ONE LU factorisation
```

`RHS = 1` per basis is exact and needs no gap width: L8c normalised the rooftop to **unit total
current across the shared edge**, so `⟨f_m, E^imp⟩` for a delta-gap of v volts across that edge is
exactly v. **Y is symmetric because Z is** — same structural-reciprocity shape as L7b's block
construction and L8c's `m ≤ n` mirror. Say its strength precisely, as L7b-b's own note does: `Z` is
bit-identically symmetric, `Y` is symmetric to the LU's tolerance because it passes through a solve.

**D2. The port cut is the OUTERMOST rooftop row of the feed, and nothing is user-positionable.** The
reference plane is therefore the shared edge of the two outermost cells — one cell in from the drawn
metal end — and the half-cell beyond it is part of the error box. There is no port-offset setting, no
"reference plane" coordinate, and no de-embedding distance to choose: **all of that is what the
calibration removes**, and offering a knob for it would offer a way to get a different answer for the
same structure. §10.6's "show the de-embedding reference plane in the layout" is then a UI statement
about a location the engine already knows, and it is L8e's.

**D3. Excitation is a delta-gap, the ground reference is the slab's ground plane, and there is no
other option in v1.** D2 of L8a puts one metal layer on top of one grounded slab, so the return path
is the ground plane by construction and there is nothing to declare. **A port that names any other
reference — a coplanar ground, a second conductor, a differential pair — is refused by name pointing
at L9**, in `R-mom-17`'s wording style. Internal (mid-structure) delta-gap ports are likewise not
built: §10.6 lists them as "later" and nothing in L8's own gates needs one.

**D4. THE CALIBRATION STANDARD IS CONSTRUCTED FROM THE DUT'S OWN MESH, NOT RE-MESHED.** Take the
DUT's transverse gridlines across the port, and the DUT's own longitudinal cell run for the first
*K* cells inward from the port; mirror that run at the far end; fill the middle with bulk cells to
reach the requested length. Build the `PlanarMesh` directly, honouring **R-msh-2's `(LayerIndex, IY,
IX)` ordering contract**. Consequences, all of them the point:

- The port's cell neighbourhood is **identical**, so the error box is the same object rather than a
  similar one. This is what makes calibration exact rather than approximate.
- **`SurfaceMesher` is not touched** — L8c's own out-of-scope list keeps it closed and there is no
  reason to open it.
- It is directly testable: assert the standard's first *K* cell coordinates equal the DUT's, exactly.

*The limitation this leaves, stated rather than discovered:* the DUT's feed may have other metal
near it that the standard does not. That is inherent to any two-line calibration (it is true of real
TRL too) and it is why R-prt-3 asks the feed to be isolated, and why R-prt-4's feed-length study is
the measurement that says how isolated.

**D5. γ COMES FROM A 2×2 EIGENVALUE, IN CLOSED FORM, AND THE BRANCH IS RESOLVED BY CONTINUITY.** With
`T₁`, `T₂` the wave-cascade matrices of the two standards' raw S,

```
M = T₂ T₁⁻¹ = T_A · diag(e^{+γΔℓ}, e^{−γΔℓ}) · T_A⁻¹
```

so `e^{±γΔℓ}` are M's eigenvalues — **the quadratic formula, no eigensolver**, exactly as L7b-b's
`ExactTwoConductorOracle` does it and for the same recorded reason. Pick the root with `Re γ ≥ 0`.
**`β` is then known only modulo `2π/Δℓ`**: start at the lowest frequency (where `βΔℓ ≪ π` and the
principal value is right) and unwrap upward. **Check the condition rather than assuming it** — see
R-prt-6.

**D6. THE ERROR BOX IS SOLVED IN CLOSED FORM, AND THE TWO SIGN AMBIGUITIES ARE HANDLED DIFFERENTLY
BECAUSE THEY ARE DIFFERENT.** With both boxes mirror images of one another (D4 guarantees it), a
standard of length ℓ measures

```
M₁₁(ℓ) = a₁₁ + a₂₁²·a₂₂·x²/(1 − a₂₂²x²)          x = e^{−γℓ}
M₂₁(ℓ) = a₂₁²·x/(1 − a₂₂²x²)
```

(a₁₁ = the box's external reflection, a₂₂ its internal reflection, a₂₁ = a₁₂ by reciprocity). With γ
from D5 both x's are known, so with `m_i = M₂₁(ℓ_i)`:

```
a₂₂² = (m₂/x₂ − m₁/x₁) / (m₂x₂ − m₁x₁)      then  a₂₁² = m_i(1 − a₂₂²x_i²)/x_i,  a₁₁ = M₁₁ − …
```

Two square roots, and **they are not the same kind of problem**:

- **`a₂₁ = ±√(a₂₁²)` cancels exactly** and needs no resolution *when the two ports are identical*:
  `T_A → −T_A` under the flip, and `T_DUT = T_A⁻¹ T_meas T_B⁻¹` picks up `(−1)(−1)`. **It does NOT
  cancel when the two ports have different widths** — there it is a hard π in `S₂₁`. Resolve by
  anchoring at the lowest frequency, where the box is a near-transparent impedance transformer and
  `a₂₁` is positive real, then continuing. Both halves of this belong in a test.
- **`a₂₂ = ±√(a₂₂²)` does not cancel**, and it is resolved by the **redundant** `M₁₁` equation: the
  two lengths must give the same `a₁₁`, and flipping `a₂₂` flips the correction term. Take the sign
  that agrees and **report the residual of the other as a de-embedding-quality diagnostic** — this
  area's standing habit (`AsymmetryResidual`, `ModeCouplingResidual`, `SumRuleResidual`,
  `FitResidual`), and the same caveat applies: report it, do not claim it predicts accuracy until it
  has been measured to.

**D7. THE DE-EMBEDDED S IS REFERENCED TO THE LINE'S OWN Z_c, NOT TO 50 Ω — AND THE CALIBRATION CANNOT
DETERMINE Z_c.** This is a fact about the method, not a gap in the implementation: the algebra above
assumed the section between the reference planes is a *matched* line, `[[0,x],[x,0]]`, which is only
true in the line's own `Z_c`. Consequences:

- **The de-embedding's accuracy and `Z_c`'s accuracy are separable, and must be reported separately.**
  Tier 4's third-line gate lives entirely in the `Z_c` reference and is blind to `Z_c`'s value; Tier 5
  is where `Z_c` alone is tested. Conflating them would let a bad `Z_c` hide behind a good
  calibration.
- **`Z_c = γ/(jωC_pul)`, with `C_pul` from DIFFERENCING two static solves** of the same two standards
  — `C_pul = (C(ℓ₂) − C(ℓ₁))/(ℓ₂ − ℓ₁)`, so the end effects cancel *exactly* rather than being
  neglected. `C(ℓ)` is L8c's `PlanarFill.ScalarPotentialMatrix` at ω → 0, which is a product surface
  and already gated (L8c Tier 5). This is the standard γ-and-C route and it is honest about what it
  assumes: **`C_pul` is quasi-static**, so `Z_c` inherits that. R-prt-8 requires the size of the
  assumption to be measured, not hand-waved.
- **Kernel A is the ORACLE for `Z_c` and `ε_eff`, never an input.** Do not read `Z_c` or `C_pul` off
  `QuasiStaticKernel` and feed it into B — that would make the phase table's own gate ("A and B agree
  on a uniform line") a tautology and would import A's ≤1.3 % `ε_eff` discretisation error into B's
  answer.
- The final renormalisation to the user's port impedances is **`RFNetwork.SToS`**, which already
  handles per-port complex Z₀. **Do not write a second renormalisation** — R-mom-14's rule, applied
  again.

**D8. Multi-RHS, one factorisation, and the L8c core-reuse counter generalises rather than being
abandoned.** `PlanarSystem.Lu` is computed once per frequency per mesh and back-substituted P times.
Each of the three meshes (DUT + two standards) keeps its own frequency-independent core, so
**R-fil-9's counter now reads exactly 3 for a sweep of any length**, and it is asserted at 3 for both
a 3-point and a 101-point sweep. Standards are **shared between ports of identical width and end
run** — measure what that saves.

**D9. No `DataSet`, no `.snp`, no kernel registry, no `IEmKernel` change, no heat map, no UI.** L8d
produces the de-embedded, renormalised S-matrix per frequency as a plain complex matrix, plus γ, Z_c,
ε_eff and the diagnostics. **L8e** wraps it in the house `DataSet` convention, writes the `.snp`,
registers the kernel, narrows the refusals and owns the phase gate. If you find yourself editing
`IEmKernel`, stop — you have crossed into L8e.

---

## 2. What already exists, and what genuinely does not

**Exists and is reused unchanged:**

- **`PlanarMesh`** — `Cells` and `Bases` in L8b's permanent order, `GridX`/`GridY`. **Port resolution
  and the standards' construction both index by that order and neither may re-sort it.**
- **`PlanarFill.BuildCores` / `Fill` / `ScalarPotentialMatrix`**, `PlanarFillSettings.Default` — all
  measured at L8c and not to be re-tuned here. `ScalarPotentialMatrix` is the static-limit path D7
  needs.
- **`PlanarSystem`** — the dense matrix, `GuardCeiling` (R17 before allocation), `Lu`, `Solve`. Its
  own header says in as many words that its `Solve` is *"NOT a port excitation — D8 keeps those in
  L8d"*. This is L8d; that is the method to build on.
- **`PlanarSweep.Run`** — the fill-and-factor driver and its `CoreFillCount`. Extending it, or
  writing a sibling that drives three meshes, are both fine; **keeping one counter honest is not
  optional** (D8).
- **`RFNetwork`** — `YToS`, `ZToS`, `SToS` (renormalisation, per-port complex Z₀), `SToT2Port`,
  `TToS2Port`, `Passivity`. **The 2-port T-matrix helpers already exist; do not write a third pair.**
- **`QuasiStaticKernel` + `EmProblemBuilders.Microstrip(...)`** in the test project — kernel A, its
  validated `ε_eff`/`Z₀`/γ, and the fixtures that build a cross-section. **The oracle for Tier 3.**
- **`SurfaceMesher.UnknownCeiling`** (5,000) and its refusal wording.

**Does NOT exist:**

- **Any port anywhere in kernel B.** `EmPort` is kernel A's cross-section port (a conductor name and
  an end); it does not describe a location on a sheet and must not be reused for one. A new neutral
  type belongs beside `PlanarProblem`, and adding a defaulted `Ports` list to `PlanarProblem` is an
  **additive** change that leaves every L8b construction compiling.
- **Any right-hand side, any multi-RHS solve, any Y or S in kernel B.** L8c built and factored a
  matrix nobody excited, on purpose.
- **Any de-embedding of any kind.** `RFNetwork.DeEmbed2Port` de-embeds against *known* fixture
  networks; it does not extract one, which is the entire problem here.
- **Any calibration standard, any γ extraction, any T-matrix cascade over a synthesised mesh.**
- **Conductor loss in kernel B.** L8c's sheet is PEC. `PlanarConductorLayer.SigmaSm` and
  `ThicknessM` are carried and unused. **Do not add a surface impedance here** (D9's spirit): it is a
  real omission, it is named in R-prt-12, and its size is to be *reported* so whoever schedules it
  knows what it is worth.
- **A wired-up `Dcim.WithinValidatedRange`.** It exists, it is tested, and **nothing in production
  calls it.** R-prt-13 makes that a decision instead of an oversight.

---

## 3. Requirements

**R-prt-1. The port operator is `B`, and `Y = Bᵀ Z⁻¹ B` with ONE factorisation and P right-hand
sides.** Reciprocity is inherited from `Z`'s structural symmetry; state its strength precisely (`Z`
bit-identical, `Y` to the LU's tolerance) rather than overclaiming, exactly as L7b-b's own note does.

**R-prt-2. Port resolution is REPORTED, and a port that cannot be resolved is refused by name.** How
many bases the row contains, the resolved width, which gridline the reference plane sits on, and its
coordinates. A port that lands on no metal, on a single-cell conductor (no rooftop row exists), or
on a conductor whose local direction is ambiguous, is refused with a specific message in R-mom-17's
style — never silently snapped to something nearby.

**R-prt-3. The feed must be isolated and uniform for a stated distance, and that is CHECKED.** The
calibration replaces the DUT's port neighbourhood with a standard's; if other metal is inside that
neighbourhood the substitution is invalid. Check the feed's own width is constant over the calibration
run and that no other metal is within the distance R-prt-4 measures; **warn** (not refuse) with the
measured clearance, because a user may knowingly accept it.

**R-prt-4. THE MINIMUM FEED LENGTH IS MEASURED, NOT ASSUMED, AND IS REPORTED IN SUBSTRATE HEIGHTS.**
De-embed the *same* discontinuity behind feeds of increasing length and report where the answer stops
moving, to a stated tolerance, on both starter substrates. This is R-mom-10's truncation-convergence
requirement transplanted, and it is the number a user will need in order to draw a structure that can
be simulated at all. A rule of thumb transcribed from anywhere else is not acceptable.

**R-prt-5. The standard's port neighbourhood is IDENTICAL to the DUT's, asserted on coordinates.**
D4. The test compares cell rectangles for exact equality over the first *K* cells and across the full
transverse partition — not a tolerance, an equality.

**R-prt-6. The γ branch is resolved, and the ambiguity condition is CHECKED across the band.** Report
`βΔℓ` at both band edges. Flag every frequency outside `[20°, 160°]` — the standard TRL usable band,
and the same interval where D6's `a₂₂²` denominator `(m₂x₂ − m₁x₁)` degenerates (it vanishes exactly
at `βΔℓ = nπ`). **Measure how badly the answer actually degrades at the edges** and use that to decide
whether two standards suffice for a 2–20 GHz sweep or whether a third length is needed. Decide by
measurement, as L8a decided its branch-point order and L8c its extraction order.

**R-prt-7. A THIRD line length, not used in the calibration, is the de-embedding's own gate.** In the
`Z_c` reference it must de-embed to `[[0, e^{−γℓ₃}], [e^{−γℓ₃}, 0]]`: `|S₁₁|` below a *measured*
bound and `∠S₂₁ = −βℓ₃`. This gate is blind to `Z_c` by construction (D7) and therefore isolates the
calibration from the reference impedance.

**R-prt-8. `Z_c` is `γ/(jωC_pul)` with `C_pul` from differencing two static solves, and the
quasi-static assumption's size is measured.** Report `Z_c(f)` across the band against kernel A's own
static value, and say where and by how much dispersion separates them — that separation is a *result*
of this kernel, not an error, and confusing the two is the trap.

**R-prt-9. Renormalisation is `RFNetwork.SToS`; no second implementation.** R-mom-14, again.

**R-prt-10. Reciprocity and passivity are gates; the cascade identity is a gate.** `S₁₂ = S₂₁` to
solver tolerance; `RFNetwork.Passivity` ≥ 0; a de-embedded uniform line of length 2L equals two
cascaded lines of length L (§10.9's own list).

**R-prt-11. LOSSLESSNESS IS NOT A GATE — the missing power is MEASURED and REPORTED.** With tanδ = 0
and PEC metal, `1 − |S₁₁|² − |S₂₁|²` is the power radiated and launched into surface waves. Report it
for §10.7's hero at 2 / 10 / 20 GHz on both starters. **This is one of the two things kernel B exists
to compute**, and kernel A structurally cannot see it.

**R-prt-12. Conductor loss is NOT modelled, and the size of the omission is reported.** Kernel B's
sheet is PEC. Take kernel A's own `α_c` and `α_d` on the same line at 2 / 10 / 20 GHz and state what
fraction of the total loss B is missing, so the A-vs-B comparison is read correctly and so whoever
schedules the surface-impedance term knows what it buys.

**R-prt-13. `Dcim.WithinValidatedRange` becomes a decision.** It exists, it is worded on L8a's
*strict* relative measure, and nothing calls it. A 20 mm line at 20 GHz reaches `ρ/λ_g ≈ 2.4` between
its far ends, so wiring it naively would refuse §10.7's own hero. **Measure the largest `ρ/λ` in the
hero's mesh, state whether the refusal would fire, and take the decision explicitly** — L8c's Tier 2
already measured that the *scaled* error (the one a fill experiences) is ≤ 5.4e-3 on real meshes, so
the likely right answer is that a per-entry refusal on the strict measure is the wrong instrument for
a fill. Whatever the answer, it must be written down rather than left as an unwired function for a
third phase to trip over.

**R-prt-14. Determinism.** Same problem, same settings ⇒ bit-identical S, entry by entry, across two
runs in one process. Same rule as R-fil-11 and for the same reason.

**R-prt-15. Nothing here decides a `DataSet`, an `.snp`, a registry entry, an `IEmKernel` signature,
a heat map, or anything in `src/Ui`.** D9. A "temporary" result type invented here will be the one
that ships.

---

## 4. The oracle ladder

Same rule as every phase in this area: **each tier passes before the next is written.**

**Tier 0 — the port operator.** `B`'s shape, signs and count on a hand-built mesh; the resolved width
equals the conductor width; a port off the metal is refused by name; `Y = BᵀZ⁻¹B` is symmetric to LU
tolerance; and — the test that catches a transposed index — driving a **symmetric** two-port
structure gives `Y₁₁ = Y₂₂` to solver tolerance.

**Tier 1 — the raw solve, before any calibration exists.**
- A uniform line's raw S is reciprocal and passive at every frequency.
- **The current on the line is a travelling/standing wave, and `γ` falls out of it in closed form.**
  With uniform bulk cells of pitch Δz along the line, three consecutive centre-line rooftop currents
  satisfy `cosh(γΔz) = (I_{k−1} + I_{k+1}) / (2 I_k)` exactly. Extract γ from many triples in the
  line's middle and report the scatter. **This is an independent oracle for D5 that shares no algebra
  with it** — no T-matrix, no error box, no standards — and it is this area's standing D3 pattern for
  the fifth time (kernel A's meshed ground plate, L7b-b's closed-form 2×2, L8a's Sommerfeld contour,
  L8c's cross-correlation).

**Tier 2 — γ two ways.** D5's two-line eigenvalue against Tier 1's current fit, across the band, on
both starters. They must agree to a *measured* bound. Disagreement localises immediately: the current
fit is wrong if it is noisy across triples, the two-line step is wrong if it is smooth but offset.

**Tier 3 — A and B agree on a uniform line** (the phase table's own words). `ε_eff` and `Z_c` from
kernel B against `QuasiStaticKernel` on the same geometry, from low frequency upward.
- **Expect agreement at low frequency and divergence at high** — that divergence is microstrip
  dispersion, which B computes and A does not. Report where it starts and how large it gets, and
  compare it against the **Kirschning-Jansen** correction that already exists as an opt-in flag on
  `QuasiStaticKernel`: if B's dispersion tracks K-J, that is a strong corroboration of both.
- **Do not gate on α.** R-prt-12 — B has no conductor loss.

**Tier 4 — de-embedding self-consistency, all of it blind to `Z_c`.**
- R-prt-7's third line: `|S₁₁| ≤` a measured bound, `∠S₂₁ = −βℓ₃`.
- **Feed-length invariance:** the same DUT behind two different feed lengths de-embeds to the same S.
- The 2L = L + L cascade identity.
- **The `a₂₁` sign ambiguity cancels for identical ports and does NOT for unequal ones** — both
  halves tested, the second on a step-in-width where the two feeds differ (D6).

**Tier 5 — `Z_c`, alone.** `γ/(jωC_pul)` against kernel A at low frequency; `C_pul` from the
differenced static solves against kernel A's own per-unit-length `C` — two routes that share no code.
Report the frequency dependence separately from the level.

**Tier 6 — physics, on structures that are not lines.** `Category=Benchmark`.
- **A quarter-wave open stub resonates at the right frequency.** This is half of L8's own phase gate;
  L8e owns the formal gate, but **measure it here so L8e is not the first time anybody looks.**
  Compare against `λ_g/4` with the open-end extension named (Hammerstad's `ΔL/h` form) rather than
  against a bare quarter wavelength, and report the discrepancy either way.
- R-prt-4's feed-length convergence study.
- R-prt-11's radiated fraction.

**Tier 7 — determinism, the counter, and cost.** Bit-identical S across two runs; `CoreFillCount = 3`
for a 3-point and a 101-point sweep (D8); and the measured cost of a de-embedded sweep against L8c's
own bare-fill numbers.

---

## 5. What must NOT be built here

- **`DataSet`, `.snp`, the kernel registry, any `IEmKernel` or `EmCapabilities` change** — L8e.
- **The current-density heat map** — L8e. L8b's provision on `LayoutRenderer.DrawPlanarMeshOverlay`
  stays wired to nothing.
- **Anything in `src/Ui`.** No port tool, no reference-plane rendering, no panel field. The Ui half of
  L8 is L8e's, and §10.10's budget table already lost its port-placement row (the L6/L7 EM-UI brief's
  D5) — putting it back is a design change, not an implementation detail.
- **Any change to `SurfaceMesher`, `PlanarMesh` or the cell/basis ordering.** D4 exists so none is
  needed. Adding a defaulted `Ports` list to `PlanarProblem` is the one permitted additive change.
- **Any re-tuning of L8c's quadrature, extraction order or fill settings.** Those were measured. If a
  de-embedded answer looks wrong, it is not the fill — L8c's Tier 3 pins the fill at 5.0e-6 against
  an exact reduction.
- **A surface-impedance / conductor-loss term** (R-prt-12 reports it instead), **internal delta-gap
  ports** (D3), **differential or multi-mode ports**, **ACA/MLFMM**, **adaptive frequency sampling**.
- **A general eigensolver.** D5's is 2×2 and closed form, as L7b-b's was.
- **A new dependency of any kind.**
- Nothing in `src/Core`, `RfCore`, or `src/Engine` outside `Mom/`.

---

## 6. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | The port type, its resolution, `B`, the multi-RHS solve, raw `Y` → raw `S`. | **Tier 0 and 1 green**, including the closed-form current-fit γ. |
| **M2** | The synthesised standards + γ by the two-line eigenvalue. | **Tier 2 green**, and the standard's port neighbourhood asserted identical (R-prt-5). |
| **M3** | The error box, the two branch resolutions, de-embedding, `Z_c`, renormalisation. | **Tier 4 and 5 green**, with R-prt-7's bound measured rather than assumed. |
| **M4** | The sweep driver, the refusals, the physics measurements, determinism and cost. | **Tier 3, 6 and 7 green**, with R-prt-4's feed length and R-prt-11's radiated fraction reported. |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass. **In particular: if Tier 2's two γ's disagree, do not average them and do not widen
the tolerance.** They come from disjoint algebra; a disagreement means one of them is wrong and the
scatter across triples in Tier 1 says which. And if Tier 3 shows B disagreeing with A at *low*
frequency, the fault is in this slice — A's low-frequency answer is validated to ≤1.3 % on `ε_eff`
against exact closed forms, not against an empirical fit.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  PlanarPort.cs           the neutral port type, its resolution to a basis row, the refusals (new)
  PlanarExcitation.cs     B, the multi-RHS solve, Y and the raw S                            (new)
  PlanarCalibration.cs    the synthesised standards, γ, the error box, C_pul, Z_c            (new)
  PlanarDeembed.cs        the T-matrix cascade, the branch resolutions, renormalisation      (new)
  PlanarSolve.cs          the per-frequency driver: DUT + standards → de-embedded S          (new)
  PlanarProblem.cs        + a defaulted Ports list — additive, nothing else changes        (edit)

tests/Engine.Tests/Mom/
  PlanarPortTests.cs         Tier 0
  PlanarExcitationTests.cs   Tier 1
  PlanarGammaTests.cs        Tiers 2, 3   (the A-vs-B sweep Category=Benchmark)
  PlanarDeembedTests.cs      Tiers 4, 5
  PlanarSolveTests.cs        Tiers 6, 7   (mostly Category=Benchmark)
  Support/CurrentWaveOracle.cs   Tier 1's closed-form γ from three consecutive currents
```

---

## 8. Four things to report back on, whatever else happens

1. **The minimum feed length, measured** (R-prt-4) — in substrate heights and in guided wavelengths,
   on both starters, with the convergence sequence that produced it. This is the number that decides
   whether a user's drawn structure can be simulated at all, and it is the first thing L8e's UI will
   have to tell them.
2. **The de-embedding's own accuracy and its usable band** (R-prt-6, R-prt-7) — the third-line
   residual across the band, `βΔℓ` at both edges, how badly the extraction degrades where the interval
   is violated, and therefore **whether two calibration standards suffice for a 2–20 GHz sweep or
   whether a third length is needed.** Decide by measurement.
3. **A vs B on a uniform line** (Tier 3), which is half of the phase table's own gate: `ε_eff` and
   `Z_c` across the band, where dispersion separates them and by how much, checked against the
   existing Kirschning-Jansen option — **plus the radiated + surface-wave fraction** (R-prt-11) and
   the size of the conductor-loss omission (R-prt-12), so the comparison is read correctly rather than
   as B being wrong about loss.
4. **The cost of a de-embedded sweep, against L8c's own bare-fill numbers** — the hero's 1.73 s per
   frequency and 178 s for 101 points is the baseline; this slice adds two more meshes and P
   back-substitutions. Report the total, what fraction is the standards, what sharing standards
   between identical ports saves, and **whether L8c's two named remedies (per-cell-pair moment
   caching, adaptive frequency sampling) are now the thing to build next or whether something in this
   slice overtook them.**
