# Phase 4a Convergence — Diagnosis-First: Verify the HB Jacobian, then Improve Convergence (Claude Code / Sonnet)

**Context:** The loadpull_pursuit MXP↔MXE result is stuck at ~1.05 VSWR (wrong; should be ~2–2.5 per Pedro
for this stable FET). Root cause is upstream: the **HB engine fails to converge** at terminations/drives it
should handle easily. Concretely, a ZL=85 Ω loadpull point did NOT converge, yet the owner knows a priori
(and from a validated MATLAB HB implementation) that this SDD converges from ZL=50–200 Ω real to P-3dB, and
also at ZL_f=80 Ω with ZL_2=500 Ω simultaneously (inverse-Class-F). **A search cannot be debugged on top of
an unreliable solver — so we pause 4b-2 and harden the HB engine first.** Suspicion order: HB engine ≫ SDD
model (the SDD is independently validated in MATLAB).

**This is DIAGNOSIS-FIRST. Do PASS A (verify the Jacobian) and STOP and report BEFORE any fix.** The whole
problem this phase has been premature "passes"; we settle whether the Jacobian is correct *before* choosing
a fix direction.

> Read first: `docs/design/harmonic-balance.md` (§4, §7 the Jacobian), `src/Engine/HarmonicBalance/CLAUDE.md`
> (frozen FFT/sign conventions), and the Maas Jacobian excerpt the owner provided (eqns 3.37–3.62 — the
> reference for the 2×2 conversion-matrix blocks). Files: `src/Engine/HarmonicBalance/HbNewton.cs` (the
> Jacobian + Newton loop), `HbEngine.cs` (warm-start, sweep), `HbFft.cs` (grid). Test harness:
> `tests/Engine.Tests/HarmonicBalance/Hero2Tests.cs` (the owner's `SimpleSweep()` loads
> `testdata/Hero2/hero2_convergence.cnl`).

## Convergence targets (owner-supplied, a priori known to converge)
Using `hero2_convergence.cnl`:
- ZLoad_f = 50 … 200 Ω (purely real), input-power sweep to **P-3dB compression**.
- ZLoad_f = 80 Ω with ZLoad_2 = 500 Ω simultaneously (inverse-Class-F), to **P-3dB**.
Current status: `SimpleSweep()` converges to Pstop=18 but **fails at Pstop=19**. These are the make-or-break
cases — the engine must handle them, since the device demonstrably does in other HB simulators.

## PASS A — Verify the Jacobian numerically (the linchpin diagnostic). STOP and report after this.

### A1 — Numerical (finite-difference) Jacobian vs analytic `BuildJ`, as a PERMANENT test
Add a permanent test in `Hero2Tests.cs` that, at a chosen operating point, compares the analytic Jacobian
`HbNewton.BuildJ` against a finite-difference Jacobian of `HbNewton.BuildF`:
- Pick an operating point: take a converged V (e.g. low-drive Pstop), and ALSO test near the failing point
  (the last converged V before Pstop=19).
- FD Jacobian: for each real DOF j of V (the 2·N·(K+1) real-split unknowns), perturb V[j] by ±ε, recompute
  `BuildF` (which requires re-running `EvaluateNonlinear` to get fresh iNl/qNl at the perturbed V), and form
  the central difference (F(V+ε)−F(V−ε))/(2ε) as column j. Choose ε per-DOF (e.g. ε = 1e-6·max(|V[j]|,1)).
- Compare element-by-element to `BuildJ(yNN,G,C,…)` at the same V. Report the **max absolute and max relative
  element error**, and the **(row,col) location and block-meaning** of the largest discrepancies (which
  node/harmonic pair, G-term vs C-term vs Y-term, sum vs difference, DC special-case cell).
- The test asserts agreement to a tight tolerance (start ~1e-6 relative on the dominant elements; the owner
  used finite-difference as ground truth in his MATLAB sim, so this is the trusted oracle).

### A2 — Report
Report the comparison: does the analytic Jacobian match the numerical one? If YES (within tol), the Jacobian
is exonerated and the bug is elsewhere (warm-start / step damping / guard) — proceed mentally to PASS B but
REPORT FIRST. If NO, report exactly which blocks disagree (this localizes the bug — e.g. the C-term rotation
sign in `BuildJ` lines ~`a00 += -kw*cb10 …`, the G sum/difference assembly, or the DC special-case cells
`if (i==0)… if (k==0)…`). **Do not fix yet — report the localization.**

Known checkpoints to verify `BuildJ` against Maas while analyzing (for the report):
- G block (Maas 3.57): `[G^R_{k−i}+G^R_{k+i}, −G^I_{k−i}+G^I_{k+i}; G^I_{k−i}+G^I_{k+i}, G^R_{k−i}−G^R_{k+i}]`.
  **Both sum (k+i) and difference (k−i)** terms — the sum terms (Maas 3.53) are what "noticeably improve
  convergence in strongly nonlinear circuits," i.e. exactly at P-3dB. Confirm they're present and correct.
- C block (Maas 3.60): rotation `[0,−kω; kω,0]` × the C-component 2×2. Verify the sign of the `kω` rotation.
- DC singularity (Maas 3.58–3.59): (2,2) cell at k=i=0 set to G0; Im of DC consistently zeroed.
- `SafeGet` conjugation for negative index (k−i<0 → conj) — verify this matches the negative-frequency
  component definition.

## PASS B — Convergence improvements (ONLY after the PASS A report is reviewed)

Apply in priority order; re-run the targets after each to measure effect:

### B1 — If A2 found a Jacobian discrepancy: fix that block. (Highest priority if it exists.)
A correct Jacobian is necessary for Newton to converge quadratically; a wrong block degrades or breaks it.

### B2 — Add Newton step damping / line search (likely needed regardless)
`ApplyUpdate` uses λ=1 always (full step). Maas Fig 3.7(b) shows undamped Newton overshooting/trapping. A
strongly-driven PA step at λ=1 can overshoot into a region where the next `Evaluate` is garbage, killing
convergence at high drive (the Pstop=19 symptom). Add a simple damped update: if ‖F(V+λΔV)‖ ≥ ‖F(V)‖, halve
λ (a few backtracking steps), accept the λ that reduces the residual. This is a standard HB convergence aid.

### B3 — Implement GuardHarmonic in the Jacobian (currently INERT — confirmed not applied anywhere)
The `GuardHarmonic` parameter is plumbed through but **never used in `BuildJ`**. Implement it per its design:
above the guard harmonic index, **attenuate the high-harmonic G and C conversion components (both)** so the
lower-frequency components dominate the Newton update. Make the attenuation shape configurable and test
empirically on the targets (try: hard step-down to zero above the index; vs exponential decay; vs linear
taper). Add a test that sweeps the guard index/shape and reports convergence reach (max Pstop) for each, so
the owner can choose. NOTE: the guard is a convergence *aid*, not a correctness fix — if the engine NEEDS it
to converge a mildly-nonlinear PA to P-3dB, that points back to B1/B2.

### B4 — Verify warm-start (owner's check)
Confirm each Pin step's initial V guess is the **converged V of the previous (lower) Pin step**, not a cold
DC restart. Trace it in `HbEngine` (the sweep loop's `prevV`) and `RunSinglePoint` (the loadpull path's
`warmStart`). Report whether the failing Pstop=19 actually receives the Pstop=18 converged V as its seed.

### B5 — (Low priority — likely transitively covered by A1) SDD derivative spot-check
If A1 verifies the *full* Jacobian numerically, the SDD `Dg`/`Dc` are verified transitively (they feed it).
Only if A1 is inconclusive: add a focused finite-difference check of the SDD's `Evaluate` Dg/Dc vs numerical
dI/dV, dQ/dV at a few bias points, tight tolerance.

## Acceptance
1. PASS A: permanent numerical-vs-analytic Jacobian test exists in `Hero2Tests.cs`; the comparison is
   reported (match or localized discrepancy) BEFORE any fix.
2. After fixes: `hero2_convergence.cnl` converges to P-3dB for ZLoad_f = 50…200 Ω real, and for
   ZLoad_f=80/ZLoad_2=500 Ω inverse-Class-F. `SimpleSweep()` passes Pstop=19 and beyond (to P-3dB).
3. Newton step damping in place; GuardHarmonic actually applied (with a test characterizing its effect).
4. Warm-start verified (reported).
5. `dotnet build`/`dotnet test` green; Phases 1–4 still pass. (The loadpull_pursuit ~1.05 issue is NOT
   addressed here — it is expected to improve once convergence is fixed; re-evaluate it afterward.)

## Guardrails
- **STOP and report after PASS A.** Do not choose a fix direction before the numerical-Jacobian result is in.
- The numerical Jacobian is the trusted oracle (owner's MATLAB practice) — believe it over the analytic one.
- Diagnostics over grinding: if convergence resists after B1–B4, report the per-iteration ‖F‖ trajectory and
  which harmonics/nodes carry the residual at the failing step — do NOT grind or burn context.
- Do not touch the frozen FFT/sign conventions (HbFft / CLAUDE.md) without flagging.
- The SDD model is low-suspicion (validated in MATLAB) — do not rewrite it; verify, don't modify.
- Update `src/Engine/HarmonicBalance/CLAUDE.md` with the Jacobian-verification test and any fix.
