# Sonnet Brief — Phase L9b: DCIM for the general layered medium

**Design:** `docs/design/layout-view.md` **§10.2** (DCIM is the FIRST of the two "where a schedule goes
to die" markers, and this is the slice that walks into it a second time), §10.7 (the per-frequency cost
this multiplies), §11's phase table row **L9**.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` **§L9a** end to end — the
conventions, the branch-rule finding and the measured cost table are the ground this slice stands on —
then **§L8a**, above all its "M4 — the far-field defect had an exact cause, and the fix is a theorem"
section and its R-lgf-4 accuracy table. Then read `Dcim.cs`'s own header and `DcimSettings`'s doc
comments; they are the derivation and the measurement log you are extending, not a summary of one.

**Second of L9's five slices.** L9a built the spectral kernel for an arbitrary stratified medium and its
oracle ladder. This brief specifies **L9b only**: making DCIM work for that kernel. **No mesher, no basis
functions, no `G_A^zz`, no vias, no ports, no `IEmKernel`/`EmCapabilities` change, nothing in `src/Ui`.**

---

## Gate command — and it is NOT the full solution

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

L8a's and L9a's precedent stands: a slice that touches only `src/Engine/Mom/` has a blast radius of
`src/Engine/Mom/`, and the full-solution run belongs **once, at the end of L9, as part of the last slice's
gate**.

**The routine tier's headroom is now measured and small.** L9a left `Engine.Tests` at **920 routine tests
in 43–46 s** against the ~60 s ceiling. A single DCIM fit is projected at **1.4 s on a one-layer stack and
3.4 s on a three-layer one** (§0.1), so **the routine tier can afford roughly two or three fits and no
more**. Everything
that sweeps ρ/λ, frequency, stack or height pair is `Category=Benchmark`. Report both numbers — the routine
tier's total and the opt-in tier's added minutes — either way. L8's opt-in tier is already ~8.5 min and
L9a added 8 s to it; say what you add.

---

## 0. Read this before planning anything

### 0.1 What L9a actually established, in numbers

These are the numbers this slice is scheduled against. They are measured, not estimated, and they are in
`src/Engine/Mom/CLAUDE.md` §L9a with the tests that produce them.

- **The general kernel reproduces the shipped one-layer kernel exactly.** Γ^e, Γ^h, Γ^q agree to
  **6.2e-14 / 7.1e-14** on both starters across five frequencies and the whole k_ρ range. You are not
  re-litigating the kernel.
- **The oracle is trustworthy everywhere you will want to measure.**
  `SommerfeldIntegral.EvaluateLayered` moves by **≤ 7e-10** (scaled) under a 100× coarsening, over
  ρ/λ ∈ [1e-4, 10], three height pairs, 2/10/20 GHz, on both multilayer stacks. **But it only accepts
  source and observer in the TOP half-space** and refuses interior heights by name — see D6.
- **The cascade costs 6.8× the closed form at one layer and 17.0× at three** (0.202 → 1.37 → 3.42
  µs/sample). L8a's own `Dcim.Fit` measures at ~0.20 s per frequency, so at the same sample budget a fit
  becomes **~1.4 s on a one-layer stack and ~3.4 s on a three-layer one**, per frequency.
- **One-off costs are not the problem:** the small-k_ρ Taylor extraction is 98 µs per (height pair,
  frequency); the surface-wave pole search is 5.8 ms per (stack, frequency) and is already cached on the
  kernel object (`LayeredSpectralGreens.SurfaceWaves`).
- **Pole counts are no longer "one".** The measured table is in §L9a. Two facts matter here: an
  **ungrounded stack carries a TE mode at every frequency measured** (a grounded slab has none until
  25 GHz), and the **GaAs and MMIC poles sit 2.5e-9 of their own real part off the axis at 2 GHz** — a
  Lorentzian spike of relative half-width ~5e-9 essentially *on* any real-axis contour.

### 0.2 Three things that are true before you start

**1. `Dcim.WithinValidatedRange`'s range is the ONE-LAYER kernel's, and it must not be silently widened.**
It is a real decision (R-prt-13) worded on the strict relative measure, and `PlanarSolve` reports against
it. The general medium gets its **own** measured range and its **own** refusal wording; the existing
constant and the existing refusal string stay exactly as they are until L9e's audit. If the general
medium's range turns out to be worse, that is the finding, and it belongs in the refusal rather than in a
loosened constant.

**2. The far field is where DCIM already failed once, and the fix was a THEOREM, not a fit.** L8a measured
**187% error at ρ/λ = 10 on GaAs** from an unconstrained fit, and closed it by imposing
`Σ A_i = −(1 + Γ(∞))` exactly — because `1 + Γ` vanishes identically at the branch point k_ρ = k₀ and the
physical far field has no 1/ρ term. **That statement is about the grounded slab's Γ and does not survive
generalisation unexamined.** D2 is where you examine it.

**3. §11's L9 gate sentence is unresolved and is not yours to settle.** The L9a report found that *"agreement with
published reference structures"* cannot be a gate under this project's own rules (a published multilayer
S-parameter almost always arrives without a verifiable stackup, so the gate would measure the
transcription) and proposed a replacement. **The owner has not ruled.** Do not build a gate on published
multilayer data in this slice, and do not pre-empt the decision; L9e owns it.

---

## 1. Decisions taken

**D1 — The cascade must be re-parameterisable by k_z0, carrying its SIGN. This is the first thing to
build and the reason a naive port fails.**

`Dcim` does not sample in k_ρ. It samples along `k_z0(t) = k₀[(1 − t/T₀) − jt]` — linear in t, which is
what makes `e^{−jk_z0 b}` a geometric sequence in the sample index and therefore fittable by Prony at all
— and it evaluates the branch-point Taylor at **negative real k_z0**, which `Dcim.BranchPointTaylor`
does by fourth-order central differences through `remainder(±d)`.

`LayeredSpectralGreens` today is parameterised by **w = k_ρ²**, and `w = k₀² − k_z0²` is **EVEN in k_z0**
— so w alone cannot express the sign, and a `w`-parameterised cascade literally cannot be asked the
question `BranchPointTaylor` asks. This is exactly why `SpectralGreens.ReflectionAtKz0` and
`SpectralGreens.Kz1FromKz0` exist rather than the k_ρ entry point; read both before writing anything.

The general analogue is one method and one rule: **every INTERIOR region's k_zi is even in k_z0 and comes
from `k_zi² = k_i² − k₀² + k_z0²`; the TOP region's vertical wavenumber IS the supplied k_z0, with its
literal sign.** Nothing else changes. Add `LayeredSpectralGreens.TopInterfaceReflectionAtKz0(kernel, kz0)`
alongside the existing k_ρ entry point, and say in its doc comment that routing through k_ρ and back costs
a square root that can land on the wrong branch and cannot reach k_z0 < 0 at all — the same sentence
`Kz1FromKz0` already carries.

**D2 — The branch-point sum rule: state the argument, then MEASURE it. Do not assert it either way.**

Here is the argument, and it is short enough to check rather than trust. At k_z0 = 0 the top interface's
cross-multiplied Fresnel coefficient (`LayeredSpectralGreens.FresnelDown` at the last interface) reduces to

```
  TE:  (µ_b k_z0 − µ_t k_zb)/(µ_b k_z0 + µ_t k_zb)  →  −1
  TM:  (ε_t k_zb − ε_b k_z0)/(ε_t k_zb + ε_b k_z0)  →  +1
```

**regardless of what is below**, because k_z0 is the only thing that vanishes. The Möbius ladder below it
enters only through the *other* argument of the last composition, which stays finite. So Γ^h → −1,
Γ^e → +1, and Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h) → 1 − 2 = −1. **`1 + Γ` still vanishes identically at
k_ρ = k₀, for any number of layers, provided the top termination is an open half-space** — and L9a's
`T1_3` already exercises the k_z0 = 0 point on a three-layer stack.

That makes `Σ A_i = −(1 + Γ(∞))` still a theorem, and `BranchPointOrders = 1` still the only order that is
one. **Confirm it numerically on every stack in `LayerStacks` before relying on it**, and re-run L8a's own
`BranchPointOrders` 0/1/2/3 table on the multilayer stacks: order 1 was chosen by measurement on two
one-layer substrates and the choice is not automatically transferable.

**D3 — The SECOND branch point is the genuinely new physics, and it may be a structural obstruction
rather than an accuracy problem.**

An open-below stack introduces the bottom half-space's own `k_b`. DCIM approximates Γ as
`Σ A_i e^{−j k_z0 b_i}` — **a function of k_z0 alone**. But with an open bottom, Γ genuinely depends on
`k_zb = sqrt(k_b² − k₀² + k_z0²)`, which is not single-valued in k_z0 near `k_z0² = k₀² − k_b²`. For a
denser bottom (k_b > k₀) that point sits on the **negative-imaginary k_z0 axis**, which is the half-plane
the sampling path runs into.

**So the question is not "how well does it fit" but "can this basis represent it at all".** Locate the
second branch point in the k_z0 plane for each open-below stack, say where it sits relative to the
sampling path and to `BranchPointTaylor`'s evaluation points, and measure. If a sum of exponentials in
k_z0 cannot carry a second cut, say so plainly and propose what would (a second exponential family in
k_zb, an extracted lateral-wave term, or a refusal) — **and do not build the proposal in this slice.**

**`LayerStacks.OpenBelow` is DEGENERATE for this purpose and must not be the fixture you conclude from.**
It is alumina in air, so k_b = k₀ exactly and the two branch points coincide. Add an open-below stack with
a **denser** bottom half-space — a thin film on a semi-infinite silicon or GaAs substrate is the honest
shape — in `tests/Engine.Tests/Mom/Support/LayerStacks.cs`, SI units, parameters written out, per D8 of
L9a's brief.

**D4 — More than one pole is mostly free, and the two places it is not are named.**

`Dcim.Fit` already loops `g.SurfaceWaveModes` and `PoleSum` already sums them, so N poles need no new
algebra. Two things do change:

- **`Dcim.Residue`'s contour radius is written against the slab**: `0.05·min(|w_p − k₀²|, |K1² − w_p|)`.
  A general stack has no single `K1`, and with two poles the nearest singularity may be **the other
  pole**. Generalise to the minimum over every region's `|k_i² − w_p|`, every other pole's
  `|w_p − w_q|`, and the branch points. Getting this wrong does not fail loudly — it returns a residue
  contaminated by a neighbouring singularity, which is a smooth, plausible, wrong far field.
- **`SurfaceWaveTerm` holds a `SurfaceWaveMode`** (the slab's record) while the general finder returns
  `LayeredSurfaceWaveMode`. Decide once whether to widen `SurfaceWaveTerm`, unify the two records, or
  carry both — and write down why. Two near-identical records for one concept is the kind of thing this
  area has been bitten by; so is a premature unification that forces the one-layer path to change.

**D5 — For source and observer both in the TOP half-space, the height pair is an EXACT SHIFT and needs no
refit. Prove it, measure it, and let it delete the work it deletes.**

In the top half-space the kernel is exactly

```
  G̃(k_ρ; z, z′) = [ e^{−j k_z0 Δ} + Γ(k_ρ) e^{−j k_z0 Σ} ] / (2j k_z0),   Δ = |z − z′|,  Σ = z + z′ − 2H
```

so substituting DCIM's own decomposition of Γ gives, term by term:

- the direct term → `e^{−jk₀R}/4πR` with `R = √(ρ² + Δ²)` — exact, no fit;
- the quasi-static constant → an image at depth **Σ**;
- each fitted image `A_i e^{−jk_z0 b_i}` → an image at depth **b_i + Σ**, same amplitude;
- each pole term → the same `H₀⁽²⁾(k_p ρ)` with its residue scaled by `e^{−j k_z0(k_p) Σ}`, a constant.

**Every one of those is closed form.** The sum rule is untouched (the 1/ρ coefficient still cancels), so
the far-field theorem survives the shift. **If this holds — and it should — then "a fit per source/observer
height pair" is simply wrong for the case that covers every L8 geometry and the top level of a two-level
stack, and `DcimModel` should carry `Evaluate(rho, z, zp)` rather than being refitted.** Measure the
resulting error against `EvaluateLayered` over a grid of height pairs; if it degrades with Σ, say where
and why.

**D6 — INTERIOR heights are where the second variable is real, and they need an oracle that does not
exist yet.**

L9c needs metal at an interior interface. There the source sits in region m and the exponentials are in
`k_zm`, not `k_z0`. The machinery still applies — the Sommerfeld identity holds for any region,
`∫ e^{−jk_z b}/(2jk_z) J₀ k_ρ dk_ρ /2π = e^{−jkR}/4πR` whenever `k_z² = k² − k_ρ²` — but it must be
**re-referenced to the source region's own k_m**, which is complex for a lossy layer.
`SommerfeldIntegral.FreeSpace(double k0, Complex r)` takes a real k and would need widening.

**And `SommerfeldIntegral.EvaluateLayered` refuses interior heights by name**, because the substitutions
that keep k_z0 out of every denominator are referenced to the top half-space. **You cannot measure a fit
you have no oracle for.** Extending the oracle to interior heights is therefore part of this slice
(M4), not an afterthought — or, if it proves larger than it looks, the honest outcome is to restrict L9b's
measured claim to top-half-space heights, say so, and hand the interior case to L9c with the obstruction
written down. **Either outcome is acceptable; silently fitting something unmeasured is not.**

The cross-region case (source in region m, observer in region n ≠ m — which is exactly two metal levels at
two different interfaces) mixes `k_zm` and `k_zn` and has no single reference. Treat it as its own
question and report on it separately from the same-region interior case.

**D7 — Take the ~2× the cost measurement already identified, and then re-measure.**

L9a named the lever: `Ladders` is rebuilt from scratch for TM and TE on every sample, and the scalar
kernel needs both. Cache the per-w ladder pair. Then **re-measure the fit cost** the way L9a measured the
sample cost — do not assume the 2× lands. §8.2 asks for the number that L9c/L9d will be scheduled against,
and it is a fit cost, not a sample cost.

**D8 — No dependency, and no eigensolver.** L8a wrote its own Bessel functions and reached GPOF's poles
through classic Prony plus Durand-Kerner rather than committing to a general complex eigensolver; L7b-b
weighed the same commitment and declined. Nothing here changes that calculus. If you find yourself wanting
a linear-algebra package, the formulation has gone wrong.

---

## 2. What already exists, and what genuinely does not

**Exists and is load-bearing — read it before writing anything:**

- `Dcim.cs` — `Fit`, the two-level path scheme with its three scored candidate depth sets, `FitAmplitudes`
  with its row-scaled branch-point constraint block, `BranchPointTaylor`, `Residue`, `PoleSum`,
  `DcimModel.Evaluate`, `Prony`, `LinearAlgebra`. **Every measurement that chose a default is in a doc
  comment.** Read them; several of them are the reason a "better" scheme is not applied.
- `LayeredSpectralGreens` (in `SpectralGreens.cs`) — the general kernel: `KzOfRegion(region, w)`,
  `TopInterfaceFresnel`, `TopInterfaceReflection`, `AsymptoticTopReflection`, `KernelAtHeights`,
  `Voltage`, `SurfaceWaves`, `ScalarKernelNaive`.
- `SurfaceWavePoles.cs` — the chain-matrix dispersion function, the pole search, and the report that
  states its own domain.
- `SommerfeldIntegral.EvaluateLayered` — Tier 4's oracle, trustworthy to ≤7e-10 scaled, **top half-space
  only**.
- `LayeredStaticGreens` — the ω → 0 branch, already cross-checked against L8a's own image series.

**Does not exist:**

- `LayeredSpectralGreens.TopInterfaceReflectionAtKz0` — **D1**.
- Any DCIM overload taking a `LayeredSpectralGreens`. `DcimModel.Greens` is typed `SpectralGreens`.
- An interior-height Sommerfeld oracle — **D6**.
- Any `G_A^zz` / `G_A^zx` component. **L9c.**

---

## 3. The formulation, stated as requirements

**R-dcm-1 — The one-layer path stays bit-identical.** `Dcim.Fit(SpectralGreens, …)` and every number in
L8a's own tables must be unchanged. If you refactor shared internals, reconstruct the pre-change fit and
compare at full precision — the L7b-b precedent, which found two one-ulp re-associations that way and
reverted them. The Tier 2 oracles carry tolerances and structurally cannot catch a one-ulp move.

**R-dcm-2 — Sample in k_z0, with the sign, through one entry point.** Per D1. The sampling path, the
branch-point Taylor and the residue contour all go through it; there is no second parameterisation of the
cascade.

**R-dcm-3 — The far-field constraint is imposed exactly or not at all.** L8a measured that a *weighted*
constraint leaves a residual 1/ρ, and that eliminating one amplitude is what actually works. Keep
`SumRuleResidual` reported (it is 1e-16 in every one-layer case); a general-medium value that is not
~1e-16 is a finding, not a tolerance to widen.

**R-dcm-4 — The validated range is MEASURED per stack and worded as its own refusal.** Two measures, as
L8a and L9a both report: the **scaled** error `|ΔG|·4πρ` (what a matrix fill experiences) and the **strict
relative** error (what a user reads off a plot). Report both, over ρ/λ ∈ [1e-4, 10] on every stack in
`LayerStacks`, both kernels, at 2/10/20 GHz. The gap between the two is real physics — G_q's dipole
cancellation zone — not slack.

**R-dcm-5 — Determinism, bit for bit.** L8a asserts `Dcim.Fit` is deterministic across repeated calls and
across a serialize/reload. A general-medium fit that scores three candidate depth sets by residual must
score them in a fixed order with no dictionary or set iteration on the path that produces them.

**R-dcm-6 — Every refusal names the specific feature and where the capability arrives**, per R-mom-17. A
stack whose spectrum this fit cannot represent (D3's second cut, if that is the outcome) is refused by
name, with `SommerfeldIntegral.EvaluateLayered` named as the thing that is accurate there and far too slow
to fill a matrix with.

**R-dcm-7 — Nothing is cached across frequencies.** `LayeredSpectralGreens` is per-frequency by
construction and D7's ladder cache must stay inside one instance. A layered medium is *more*
frequency-dependent, not less.

---

## 4. The oracle ladder

**Tier 0 — structural, free.** The sum rule holds at k_z0 = 0 on every stack (D2). `1 + Γ` vanishes there
for both kernels. The k_z0-parameterised entry point agrees with the k_ρ one wherever both are defined,
**and reaches k_z0 < 0 where the k_ρ one cannot** — assert the second half, or the entry point is
decoration.

**Tier 1 — the one-layer reduction, bit-identical (R-dcm-1).** `Dcim.Fit` driven through
`LayerStack.FromGroundedSlab` must reproduce the shipped one-layer fit's images, poles, residual and sum
rule. This is the analogue of L9a's D5 and it is the strongest single check: the two are the same
formula.

**Tier 2 — split-a-layer invariance of the FIT.** L9a's Tier 2 for the kernel; here for the model.
Splitting a layer must not move the fitted images by more than the kernel itself moves (~1e-13). It will
not be bit-identical, for L9a's own recorded reason.

**Tier 3 — the static limit.** As ω → 0 the fitted model must converge onto `LayeredStaticGreens`
quadratically with no floor. L8a's warning applies: a floor here means the ORACLE is wrong, and this area
has had four occasions where it was.

**Tier 4 — DCIM against direct integration, and this rung is the REPORTED MEASUREMENT.** The R-dcm-4
curve. This is the L8a analogue of R-lgf-4's table and it is what L9c/L9d get scheduled against.

**Tier 5 — the height-pair rung (D5/D6).** The shift theorem measured over a grid of height pairs in the
top half-space; and, if M4 lands, the interior-height fit against the extended oracle.

**A warning that has now cost this area four milestones: check the oracle before concluding the method is
wrong.** L8a records two occasions, L7b-b a third, the L8e phase gate a fourth, and L9a a fifth (a
25-second quadrature that was a units slip, producing the right answer the whole time). When a rung
disagrees, **the first hypothesis is the rung.**

---

## 5. What must NOT be built here

- **`G_A^zz`, `G_A^zx`, via bases, junction continuity, any mesh change** — **L9c**.
- **Ports, references, finite ground pours, extractors, `.cem`, anything in `src/Ui`** — **L9d**.
- **Adaptive frequency sampling, ACA/MLFMM, N-budget changes, the refusal audit, L9's phase gate** —
  **L9e**.
- **A gate on published multilayer reference data** — §0.2 item 3.
- **Any change to `GroundedSlab`, `StaticGreens`, `SpectralGreens`'s one-layer members, `SurfaceMesher`,
  `PlanarMesh`, `PlanarFill`, or L8d's calibration.** None is needed.
- **Any widening of `Dcim.ValidatedRhoOverLambda` or of the existing refusal string** — §0.2 item 1.
- **A losslessness check.** Still more true with more layers: an open stratified structure radiates.
- **A new starter technology** — hand-built stacks in `tests/Engine.Tests/Mom/Support/LayerStacks.cs`.

---

## 6. Milestones, each with its own gate

| | content | gate |
|---|---|---|
| **M1** | The k_z0-parameterised cascade (D1) | **Tier 0** green, including k_z0 < 0 |
| **M2** | `Dcim.Fit` for `LayeredSpectralGreens`; the generalised residue contour and N poles (D4) | **Tier 1** bit-identical on both starters, and **Tier 2** |
| **M3** | The branch-point behaviour re-derived and re-measured (D2), and the second-cut question answered (D3) | The `BranchPointOrders` table re-run on the multilayer stacks; the second branch point LOCATED and its consequence stated |
| **M4** | Height pairs: the shift theorem (D5), and the interior-height oracle + fit or a stated obstruction (D6) | **Tier 5** |
| **M5** | The R-dcm-4 curve, and the cost after D7 | **Tier 4** written down before anything is claimed about it |

**M3 is the one with a wrong obvious answer** (the sum rule looks like it must generalise, and the second
cut looks like an accuracy problem). **M5 is the one that takes the time.**

---

## 7. File map (indicative)

```
src/Engine/Mom/
  SpectralGreens.cs      + TopInterfaceReflectionAtKz0 on LayeredSpectralGreens (D1), and the
                           D7 ladder cache.  The one-layer members are untouched.
  Dcim.cs                + the LayeredSpectralGreens path.  DcimModel's own typing decision (D4)
                           lives here; Dcim.Fit's one-layer behaviour is bit-identical (R-dcm-1)
  SommerfeldIntegral.cs  + interior-height support for EvaluateLayered, or a sharper refusal (D6)

tests/Engine.Tests/Mom/
  Support/LayerStacks.cs        + an open-below stack with a DENSER bottom half-space (D3)
  LayeredGreensFunctionTests.cs   L8a's ladder — EXTEND, loosen nothing
  GeneralLayeredMediumTests.cs    L9a's ladder — EXTEND, loosen nothing
  LayeredDcimTests.cs           + Tiers 0-5 for the general-medium fit
```

Nothing outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/` should change. If something does, that is
a finding worth reporting, not a step to take quietly.

---

## 8. Five things to report back on, whatever else happens

1. **The R-dcm-4 curve as numbers** — scaled and strict relative, over ρ/λ ∈ [1e-4, 10], every stack, both
   kernels, three frequencies, **and where it stops being trustworthy in each variable**. This is the
   deliverable, and it is what L9c's matrix fill will be scheduled against. Compare it to L8a's own
   ≤ 6e-3 scaled / ≤ 1e-2 strict-to-ρ/λ≈1 and say whether the general medium is worse, and by how much.

2. **Whether the far-field sum rule survives generalisation, with the measurement that says so** — and
   the re-run `BranchPointOrders` table on the multilayer stacks. If order 1 is no longer the right
   default, say which is and what the table says.

3. **What the SECOND branch point does to a fit in k_z0 alone.** Where it sits, whether the basis can
   represent it, and — if it cannot — what should, without building it. This is the question that decides
   whether `PlanarExtractor`'s ungrounded-stack refusal can ever be narrowed.

4. **Whether a two-variable fit is needed at all, and where.** D5 predicts that the top-half-space case is
   an exact shift needing no refit. If that holds, say so plainly and report the measured error across
   height pairs; if it does not, report where it breaks. Then state separately what the interior-height
   and cross-region cases cost, or what obstruction stops them.

5. **The fit cost after D7, per frequency and per height pair**, against L9a's projection (~1.4 s at one
   layer, ~3.4 s at three) and
   against L8d's measured 7.66 s per de-embedded point. L9c/L9d are scheduled against this number; if a
   101-point sweep of a two-level structure is projected in hours rather than minutes, that is the finding
   and L9e's adaptive frequency sampling stops being optional.
