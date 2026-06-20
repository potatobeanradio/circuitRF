# Brief #3 (SDD control currents): HB Jacobian coupling `J_cc` — FD-oracle-gated

Design ref: `docs/design/sdd-control-current.md` §4 (linear functional), §6 (Jacobian). Depends on briefs #1
(contract/resolver/DC) and #2 (HB residual `_c_ref` recompute) — both landed. This brief adds the
**control-current Jacobian coupling** so HB recovers **quadratic** convergence, and it is gated by the FD
oracle (`CompareJacobianNumerical`) the same way the weighting buckets were. **This is the hardest brief of the
arc.** Read §6 of the design and this whole brief before writing any code.

Engine only (`src/Engine`). Build **0W/0E**. Every existing HB test stays green.

---

## 0. The consistency trap — read this first (it determines whether the whole thing works)

The FD oracle is `CompareJacobianNumerical`: it perturbs each `V` DOF and re-runs `EvaluateNonlinear` +
`BuildF`, central-differencing the residual to get the "true" Jacobian. **The analytic Jacobian is only
correct if it is the exact derivative of the residual the oracle differentiates.** So the analytic `J_cc` and
the residual's `_c_ref` computation must be derivatives of *the same function of `V`*.

Brief #2's residual computes `_c_ref` from **`iNlPrev`** (the previous Newton iterate's interface currents),
which is an *argument* to `EvaluateNonlinear` — frozen when the oracle perturbs `V`. **If left that way, the
oracle would see `∂_c_ref/∂V = 0`, and the consistent analytic Jacobian would have NO `J_cc`.** Adding `J_cc`
to `BuildJ` while the residual freezes `_c_ref` against the perturbation → guaranteed FD mismatch that is *not*
a bug in `J_cc` but a residual/Jacobian inconsistency.

**Therefore brief #3's first required change:** in the FD-oracle path (and to make `J_cc` meaningful at the
solution), `_c_ref` must be computed **self-consistently from the `V` being evaluated** — i.e. from `iNl(V)`
of the *current* `V`, not a frozen `iNlPrev`. Two ways to satisfy this; pick **(A)**:

- **(A) Two-pass `EvaluateNonlinear` for control circuits (recommended).** Pass 1: evaluate the SDD with the
  control currents held at their entry value (seed `_c_ref` from `iNlPrev` *or* zero) to get `iNl(V)` for the
  current `V`. Then compute `_c_ref` from *that* `iNl(V)` via `SolveFullNetwork` per harmonic. Pass 2:
  re-evaluate the SDD with the self-consistent `_c_ref(V)` → the `iNl`/`G`/`C`/`Dw` actually returned. The
  residual the oracle sees now has `_c_ref` moving with `V`. **This is what makes `∂_c_ref/∂V` real and
  FD-checkable.** (Cost: one extra nonlinear pass + one back-solve set per iterate when control refs exist —
  acceptable; off the common path entirely.)
  - Note the inner self-consistency is itself a small fixed point (`_c_ref` depends on `iNl`, which depends on
    `_c_ref`). One pass-1→solve→pass-2 is a **single** linearization step, NOT iterated to convergence — and
    that's *correct*, because the analytic `J_cc` (§2 below) is the derivative of *exactly that one-step map*.
    Do **not** iterate the inner loop, or the residual stops matching the analytic Jacobian. The outer Newton
    converges the fixed point; the inner map is single-step by construction.
- (B) make `CompareJacobianNumerical` and the Newton loop drive `_c_ref` from the current `V` via a shared
  closure. More invasive to the oracle; (A) keeps the oracle untouched (it just calls `EvaluateNonlinear`,
  which now does the two-pass internally when `cc` is present).

**`CompareJacobianNumerical` must be given the `cc` context** so its `EvaluateNonlinear` calls exercise the
control path (today it calls `EvaluateNonlinear` with no `cc` → control currents inert in the oracle). Thread
`cc` through `CompareJacobianNumerical` and the `HbEngine.RunJacobianDiagnostic` entry. Without this, the
oracle can't see `J_cc` at all and the gate is vacuous.

## 1. The sensitivity row `rᵀ_ref` (the new linear-algebra object)

From design §4: `_c_ref(V) = c0_ref − rᵀ_ref·iNl(V)`, where `rᵀ_ref = e_refᵀ G(ω_k)⁻¹ P` and `P` injects the
nonlinear interface currents at the interface nodes. `SolveFullNetwork` already computes the **forward** action
(`x = G⁻¹(bSrc − P·iNl)`); `rᵀ_ref` is the **row** of `G⁻¹P` selecting the referenced branch.

Add to `HbLinearExtractor` a method that returns, for a given `omega` and referenced branch index, the
sensitivity of that branch current to a unit nonlinear injection at each interface node:
```csharp
// rRef[j] = ∂(x[branchIdx]) / ∂(iNl_at_interface[j])  for j = 0..N-1
// = − e_branchᵀ G⁻¹ e_{node_j}      (the minus matches SolveFullNetwork's b -= iNl injection)
public Complex[] ControlSensitivityRow(double omega, int branchIdx)
```
Compute it by **transpose-solve against the cached LU**: solve `Gᵀ y = e_branch` once (`e_branch` = unit at
`branchIdx`), then `rRef[j] = − y[node_j-1]` for each interface node `j` (the entries `P` would pick). One
transpose-solve per (referenced branch, harmonic) per sweep point — cache it alongside the `_luCache` entry.
**Verify the LU exposes a transpose solve**; if CSparse's `SparseLU` only solves `G x = b`, get `rRef` instead
by the identity `rRef[j] = −(G⁻¹)_{branch, node_j}` via solving `G z_j = e_{node_j}` for each interface node
`j` and reading `z_j[branchIdx]` (N solves — N is small; this avoids needing a transpose solve and reuses the
exact forward factorization `SolveFullNetwork` uses). **Prefer the N-forward-solve form** unless a transpose
solve is cleanly available — it's guaranteed consistent with the residual's `SolveFullNetwork`.

Sanity identity to assert in a test: `_c_ref(V) = c0_ref + Σ_j rRef[j]·iNl[j]` must equal
`SolveFullNetwork(...)[branchIdx]` for arbitrary `iNl` (the row reproduces the forward solve). This pins the
sign and the indexing before any Jacobian work.

## 2. The `J_cc` block — derivative of the one-step residual

`I[p,w]` now depends on `V` through `_c_ref`. The chain (design §6):
```
∂F-contribution / ∂V  +=  Σ_w H[w](kω₀) · FT{ ∂I[p,w]/∂_c_ref · ∂_c_ref(t)/∂V }
∂_c_ref/∂V            =  − rᵀ_ref · ∂iNl/∂V        (one-step: iNl from pass 1)
```
Assemble it from pieces the engine already has after the two-pass evaluation:
- **`∂I[p,w]/∂_c_ref`** — from the SDD dual-AD (`DControl`, and the per-w control sensitivities). FFT the
  time-domain `∂I[p,w]/∂_c_ref` to the conversion grid, exactly like `Dg`/`Dc`/`Dw`. Brief #1 exposed
  `DControl` for `w=0`; **this brief must also expose the charge (`w=1`) and bucket (`w≥2`) control
  sensitivities** (`∂Q/∂_c_ref`, `∂I[p,w]/∂_c_ref`), since at AC the charge/bucket paths carry the `H[w]`
  weighting. Extend `NonlinearResult`/the SDD to return per-w control sensitivities (a
  `DControlByW[w][p, ctrl]` or fold into the existing `Terms` + a charge analogue). Keep defaults so non-control
  devices are unaffected.
- **`∂_c_ref/∂V = − rᵀ_ref · (conversion matrix of iNl)`** — `rᵀ_ref` from §1 (constant per harmonic);
  `∂iNl/∂V` is the existing `G`/`C`/`Dw` conversion structure. Because `_c_ref` at harmonic `m` depends only on
  `iNl` at harmonic `m` (the linear network is diagonal-per-harmonic), `rᵀ_ref` multiplies the conversion
  matrix harmonic-diagonally on the **`_c_ref`-source side**; the **time-domain SDD mixing** of `_c_ref(t)`
  then produces the off-diagonal (k≠i) structure via the FFT of `∂I[p,w]/∂_c_ref` — i.e. `J_cc` enters
  `BuildJ` as **another conversion block**, built from the FFT of `∂I[p,w]/∂_c_ref`, then contracted with
  `rᵀ_ref` against the column harmonic's `iNl`-sensitivity.

**Implementation shape:** the cleanest assembly that stays consistent with the existing `BuildJ` 2×2-block
machinery is to treat the control-current contribution as **an additional set of conversion blocks** added into
`a00..a11`, structured as:
```
J_cc[row (n,k)][col (m,i)]  =  Σ_ref Σ_w  Hw(k)·FFT{∂I[p,w]/∂_c_ref}[k − ·] (conversion)  ·  (− rRef_ref) ⊗ (iNl-sensitivity of node m at harmonic i)
```
Given the subtlety, **do not hand-derive the final index algebra and hope** — build it incrementally against
the oracle (§3). Land §1 (the sensitivity row, with its forward-solve identity test) first; then the residual
two-pass (§0); then add `J_cc` term-by-term, watching `CompareJacobianNumerical` drop. The FD oracle localizes
errors to specific (row-harmonic, col-harmonic) blocks exactly as it did for the weighting work.

## 3. Build order (do NOT skip — mirrors how the weighting Jacobian was cracked)

1. **`ControlSensitivityRow`** (§1) + its forward-solve identity test (`_c_ref = c0 + Σ rRef·iNl` matches
   `SolveFullNetwork`). Pin sign/indexing before any Jacobian math.
2. **Two-pass self-consistent `_c_ref`** in `EvaluateNonlinear` (§0A) + thread `cc` into
   `CompareJacobianNumerical`. At this point the residual is self-consistent and the oracle *sees* the control
   dependence — run the oracle with **no `J_cc` yet** and confirm it now reports a large, structured error
   localized to the SDD rows/cols (that error is exactly `J_cc`; seeing it proves the oracle is wired).
3. **Add `J_cc`** to `BuildJ` (§2), built from the FFT'd `∂I[p,w]/∂_c_ref` contracted with `rRef`. Iterate
   against the oracle until `MaxRelError` ≤ gate at a converged low-drive point.
4. **Expose per-w control sensitivities** (charge + buckets) so AC weighting paths are covered (§2).

## 4. Tests (`tests/Engine.Tests/HarmonicBalance`)

- **Sensitivity-row identity (§3.1):** `c0 + Σ rRef·iNl == SolveFullNetwork[branchIdx]` for random `iNl`.
- **FD Jacobian — single control ref (the decisive gate):** an SDD with `I[1,0]=g*_v1 + beta*_c1`, `C[1]=L1`,
  at a converged low-drive operating point → `CompareJacobianNumerical(..., cc)` `MaxRelError` ≤ **1e-5**
  (codebase FD-oracle gate; use a modest operating point per the weighting-arc lesson — gentle harmonics so
  central differences are trustworthy).
- **FD Jacobian — control current in a charge/bucket path:** `I[1,1]=beta*_c1` (control current weighted by jω)
  → oracle passes, proving the per-w control sensitivities (§2) are right.
- **Cross-device coupling (the sharp edge):** two SDDs sharing a linear network, SDD-B sensing a branch SDD-A
  drives → `CompareJacobianNumerical` passes, proving the off-diagonal `J_cc` (one device's `V` moving another
  device's residual through `_c_ref`) is present. Also assert quadratic convergence (few iterations) vs the
  brief-#2 residual-only path (more iterations) on the same circuit.
- **Convergence improvement:** a circuit that converged slowly under brief #2 (quasi-Newton) now converges in
  noticeably fewer iterations — the practical payoff of `J_cc`.
- **All five kinds (FD):** one FD-oracle pass per referenceable kind (Vdc, IProbe, L, SnP port, ZnP port) with
  a control term active.
- **Regression:** every existing HB/weighting/equivalence test green; no `C[n]` → no two-pass, no `J_cc`,
  byte-identical.

## Gate
Build 0W/0E; all green; `CompareJacobianNumerical` ≤ 1e-5 with control currents active across all five kinds and
the cross-device case; quadratic convergence restored. **This completes the control-current arc** (DC +
S-param-deferred + HB residual + HB Jacobian). Remaining follow-ons (separate, optional): `StampLinearized`
control column for S-parameter analysis (design §5), and the docs-sync pass (`sdd.md` + the control-current
design doc's "current state"). Flag both; don't fold them in here.

## Risk note (for the human)
This is the brief most likely to need a debugging round, exactly like the weighting-function Jacobian. The
failure mode will look like a structured FD mismatch on the SDD rows at specific harmonics. If it resists, the
fastest path is the same one that worked before: **trust the FD oracle, print the analytic-vs-FD values for the
top discrepancy block, and derive `J_cc` for that one block by literally differentiating the two-pass residual
term — not by analogy.** The sensitivity-row identity test (§3.1) and the "oracle sees the error before `J_cc`"
checkpoint (§3.2) are the two tripwires that localize whether a failure is in `rRef` (linear algebra) or in the
`J_cc` index assembly (conversion blocks). Keep them.
