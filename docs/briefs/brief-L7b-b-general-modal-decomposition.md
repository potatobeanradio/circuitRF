# Sonnet Brief — Phase L7b-b: the general modal decomposition

**Design:** `docs/design/layout-view.md` §10 — §10.3 (the kernel), §10.6 (ports), §10.8 (results).
Phase table row **L7b-b**, whose own words are: *"General modal decomposition of `[Z][Y]`; the
non-Hermitian complex eigensolver it requires"*, gated on *"the error that introduces must be
MEASURED, not assumed — that measurement is the first deliverable."*

**Read `src/Engine/Mom/CLAUDE.md` — especially its L7b section — and `src/Ui/Layout/Em/CLAUDE.md`
first.** L7b is complete and validated. **This brief is an addition to L7b, not a rewrite**: the
per-conductor `[R]`, the diagonal `[R_dc]`, the symmetriser, the D3 port map, the 2N-port extractor
and the `.cem`'s per-port Z₀ are all already there and already general in N. What is missing is one
thing: a modal decomposition that does not depend on the pair being symmetric.

Gate command is plain `dotnet test`.

---

## 0. Read this before planning anything: the one thing that IS available, and its sharp edge

L7b's §0 established that NumFlat cannot decompose a general complex `[Z][Y]`. That has not changed.
**What it CAN do — verified against NumFlat 1.3.0's own XML at the time of writing, re-verify against
whatever version is current — is the real symmetric-definite generalized problem:**

> `GeneralizedEigenValueDecompositionDouble(a, b)` — *"The matrix to be decomposed must be symmetric
> positive definite. Note that this implementation does not verify whether the input matrix is
> symmetric. Specifically, only the upper triangular part of the input matrix is used, and the rest
> is ignored."*

That is exactly the shape the **lossless** multiconductor problem takes. For a lossless line
`[Z] = jω[L]` and `[Y] = jω[C]`, so `[Z][Y] = −ω²[L][C]`, and the modal problem
`[L][C]·Tv = Tv·diag(1/v_p²)` is the generalized eigenproblem

```
[C]·v = λ·[L]⁻¹·v        λ = 1/v_p²        both [C] and [L]⁻¹ symmetric positive definite
```

**The sharp edge is in that same quoted sentence, and it is the single most likely way to get a
smooth, plausible, wrong answer out of this phase.** NumFlat reads only the upper triangle and does
not check. Point collocation does **not** produce a symmetric `[C]` — that is R-cpl-7's whole
subject, measured at 0.554% off-diagonal residual at default mesh settings. Handing the raw `[C]`
to the GEVD silently decomposes a matrix that is not the one you have.

**R-gen-1. `ModalDecomposition.Symmetrise` is a PRECONDITION of the eigensolve, not a tidy-up.**
Symmetrise `[C]`, `[C₀]` and `[L]` before anything is decomposed, and keep reporting the residual
(`RlgcModel.AsymmetryResidual`) as the discretisation-error indicator it already is.

---

## 1. Decisions taken

**D1 (owner). The general path SUBSUMES the symmetric pair; it does not sit beside it.** Once the
general decomposition exists, a symmetric pair goes through it like everything else, and L7b's fixed
`[1 1; 1 −1]` construction survives **as a test oracle, not as a production branch**.

Two code paths that must agree are two code paths that will eventually disagree — and the one that
drifts would be the rarely-exercised one. Keeping L7b's closed form as an *oracle* gets the benefit
(an exact, eigensolver-free answer to check against, valid with loss) without the drift risk.
**If the eigensolver turns out to be fragile at N = 2 — degenerate eigenvalues are guaranteed there,
see R-gen-6 — stop and report rather than quietly reinstating a production branch;** that would be a
real finding about the eigensolver, not a detail.

**D2 (owner). Route A first, and the measurement decides whether Route B is ever built.** Implement
the real symmetric GEVD with loss carried perturbatively, measure its error against an exact
reference, and **report the number before writing a line of Route B.** A hand-written complex QR
eigensolver (Hessenberg reduction + shifted QR) is a genuine numerical-methods commitment in a solo
project; it must be earned by a measurement, not assumed by a plan.

**D3. The measurement has an exact reference already sitting in the repository, and this is why the
first milestone is cheap.** L7b's symmetric-pair decomposition is **exact with loss** — the modal
matrix `[1 1; 1 −1]/√2` diagonalises `[a b; b a]` whatever `a` and `b` are, including complex ones.
So a symmetric pair is a case where the perturbative answer and the exact answer can be compared
directly, with no external data and no second solver. **Do not invent a new oracle for M1; L7b is
it.**

**D4. No new result type, for the third phase running.** The `S` cube, the per-port `Z0` cube and the
`tline` group are the same plumbing. L7b's `tline` group carries per-mode PAIRS
(`ZcEven`/`ZcOdd`, …); N modes get a **mode axis** instead — see R-gen-8.

---

## 2. What already exists — read this before designing anything

L7b left this phase much smaller than the phase table implies. Six things are already general in N:

1. **`[C]`, `[C₀]`, `[L]` are full N×N**, and `RlgcExtractor.Invert` is a general Gauss-Jordan.
2. **`[R]` is a full N×N matrix built from per-conductor recessions** (R-cpl-2) and `[R_dc]` is a
   proper diagonal (R-cpl-3). Wheeler is already right for any conductor count.
3. **`ModalDecomposition.Symmetrise` and `AsymmetryResidual`** exist and are already used.
4. **`CrossSectionExtractor` builds 2N ports** in D3 order for any N — the loop is already generic.
   It needs **no change**.
5. **`.cem` per-port Z₀ and the panel's port list are already N-general** — `EmSetup.PortZ0s` is an
   arbitrary-length list and `EmSetupEditorViewModel.PortRows` is built from the extracted port
   count. They need **no change**.
6. **`RFNetwork.ZToS` is general in N** and already takes a per-port `Complex[]`.

**So the Ui half of this phase is close to free.** If you find yourself editing the extractor, the
`.cem` schema or the panel, stop and check whether you are re-doing L7b's work.

---

## 3. The general transform, stated once — and why it is not a new construction

**R-gen-2. L7b's 4-port block construction is the N = 2 special case of the general one. Generalise
it; do not write a second one.**

With `Tv` the voltage modal matrix (`I_conductor = Ti · I_mode`, `V_conductor = Tv · V_mode`) and a
per-mode scalar `x_m` — the entry of that mode's own 2-port line matrix — every block of the 2N-port
Z is

```
Zblock(x) = Tv · diag(x_m) · Ti⁻¹
```

Near-end/far-end blocks use `x_m = Zc_m·coth(γ_m ℓ)` and `x_m = Zc_m/sinh(γ_m ℓ)` respectively,
exactly as the single line does. Substituting `Tv = Ti = [[1, 1], [1, −1]]` reproduces L7b's
`Zs = ½(Z_e2 + Z_o2)`, `Zm = ½(Z_e2 − Z_o2)` identically — **check that by hand before writing the
code, and pin it with a test**; if it does not reduce, one of the two is wrong.

The port map is unchanged: **D3, port `2k−1` = conductor *k*'s near end, `2k` its far end.** Blocks
are indexed by conductor, entries within a block by near/far.

**R-gen-3. The terminal answer is INVARIANT to eigenvector scaling; per-mode reported quantities are
not.** An eigensolver returns eigenvectors of arbitrary scale and sign, and a symmetric-definite GEVD
conventionally returns them **B-orthonormal** (`Tvᵀ[L]⁻¹Tv = I`) — a normalisation you did not choose.
**Verify that is what NumFlat actually returns rather than assuming it**; the rest of this section
holds either way, but what you have to correct for depends on it.

Scaling column *m* of `Tv` by *c* scales `Ti`'s column *m* by *c* too (because `Ti` derives from
`Tv`), so `Ti⁻¹`'s row *m* scales by 1/*c* and `Tv·diag(x)·Ti⁻¹` is unchanged. **The 2N-port
S-parameters therefore cannot depend on normalisation — if a test says they do, the `Ti` derivation
is wrong, not the eigensolver.** Assert it directly (Tier G2) with a deliberately vicious scaling.

**R-gen-3a. The per-mode `Zc` you REPORT is not the classical `Z_e`/`Z_o` unless you make it so, and
the correction is PER MODE — no single constant fixes it.** Worked on a representative symmetric pair
(L₁₁ = 390 nH/m, L₁₂ = 110 nH/m, C₁₁ = 93 pF/m, C₁₂ = −32 pF/m), with `Ti = (Tvᵀ)⁻¹` and a
B-orthonormal `Tv`:

| | mode 1 | mode 2 |
|---|---|---|
| `Zc_m = √(Zm,mm / Ym,mm)` as computed | **1.81 × 10⁸** | **1.69 × 10⁸** |
| classical `Z_e`, `Z_o` | 90.54 Ω | 47.33 Ω |

Those are not ohms — under B-orthonormalisation `Zc_m` comes out as the mode's **phase velocity**
(1.81 × 10⁸ = 1/√λ₁ exactly). And the ratios differ (1.07 versus 1.91), so this cannot be repaired
with one scale factor applied to both modes. The terminal S-parameters are perfectly correct
throughout — this affects only what the `tline` group publishes, which is precisely the quantity a
user reads off a plot and believes.

**The gate is therefore concrete: for a symmetric pair, the reported per-mode `Zc` must reproduce
L7b's own `Z_e` and `Z_o`.** Pick the normalisation that achieves it (L7b's own "each conductor
carries the mode's own current", i.e. `Tv = Ti`, is one such choice) and state which.

---

## 4. Route A, concretely, and the error indicator that comes free

**R-gen-4. Perturbative loss means: real eigenvectors from the lossless problem, then DISCARD the
mode coupling that loss introduces — and the discarded part is measurable, so measure it.**

```
1. Symmetrise [C], [C₀], [L].                                    (R-gen-1 — not optional)
2. Solve Gevd(Re[C], [L]⁻¹)  →  real Tv, λ_m = 1/v_p,m²          (lossless, exact)
3. Ti = (Tvᵀ)⁻¹                                                  (the biorthogonal partner)
4. Per frequency, form the FULL modal matrices with loss in them:
       Zm(ω) = Tv⁻¹ · ([R](ω) + jω[L]) · Ti
       Ym(ω) = Ti⁻¹ · ( jω[C_complex] ) · Tv                     (R-mom-6: G rides in Im[C])
5. Take only their DIAGONALS. γ_m = √(Zm,mm · Ym,mm), Zc_m = √(Zm,mm / Ym,mm), Re γ ≥ 0.
6. Build the 2N-port via R-gen-2 and hand it to RFNetwork.ZToS.
```

**Step 5 is the entire approximation**, and step 4 hands you its size for nothing:

**R-gen-5. Report `ModeCouplingResidual` — `max_{i≠j}|Zm_ij| / min_i|Zm_ii|`, and the same for
`Ym` — as a named per-frequency number, in the notes and in the `tline` group.** It is the exact
analogue of R-cpl-7's asymmetry residual: the thing being thrown away, surfaced rather than assumed
small, so a user can see when the approximation is under strain. A pair for which it is 1e-9 is
being decomposed essentially exactly; one for which it is 0.2 is not.

**Where Route A is most stressed, so this is where to measure it:** loss matters relative to
reactance as `R/(ωL)` and `G/(ωC)`, both of which grow as ω falls. **Sweep DOWN in frequency toward
and below the Wheeler crossover (`RlgcModel.WheelerValidAboveHz`, ~14 MHz for 35 µm copper) and UP in
tanδ.** Measuring only at 1–20 GHz on a low-loss board would find nothing and prove nothing.

**R-gen-6. Degenerate eigenvalues are GUARANTEED here, not a corner case.** Two identical conductors
far apart have two modes with the same velocity, so λ is repeated and the eigenvectors are not
unique — any linear combination spans the same subspace. That configuration is not hypothetical: it
is L7b's own Tier C2 far-apart gate, which this phase must keep passing. The terminal answer stays
correct (R-gen-3's invariance covers a repeated-eigenvalue subspace too), but **per-mode reported
quantities become arbitrary within it, and mode ORDER becomes arbitrary between frequency points.**
A `Zc[mode]` trace that swaps modes mid-sweep is a plot nobody can read.

**R-gen-7. Order the modes deterministically, and say by what.** Sort by λ (ascending — slowest mode
first, so the ordering is a physical property rather than whatever LAPACK returned), tie-broken by
the eigenvector's own largest-magnitude conductor index so a degenerate pair still orders stably.
`Tv` is frequency-independent under Route A (it comes from the lossless problem, which has no ω in
it), so mode identity is fixed for the whole sweep by construction — **that is a real advantage of
Route A over Route B and is worth stating in the completion note.**

---

## 5. Results, refusals, and the UI

**R-gen-8. The `tline` group gets a MODE AXIS, not N named scalars.** L7b emits `ZcEven`/`ZcOdd`
because two modes have names; N do not. Emit `Zc`, `Gamma`, `Eeff`, `AttenDbPerM`, `Rpul`, `Lpul`,
`Gpul`, `Cpul` as rank-2 cubes over `[freq, mode]`, plus `ModeCouplingResidual` over `[freq]`.

**Decide and state what happens to L7b's `ZcEven`/`ZcOdd` names.** Two defensible answers: keep them
as an additional alias for N = 2 (a coupled-line designer thinks in even/odd and every existing
Data Display trace pointing at `tline.ZcEven` keeps working), or drop them and let the mode axis
carry it. **Recommendation: keep them for N = 2 only, sourced from the same arrays** — a second name
for one number cannot drift, and silently breaking saved `.cdd` traces is the kind of change that
costs a user their working plots. Whichever you pick, say why.

**R-gen-9. Narrow `QuasiStaticKernel.CanSolve` again — narrow, do not delete.** L7b refuses
`signalCount > 2` and refuses an asymmetric pair via `CheckGeometricSymmetry`. Both go, but what
replaces them is not "nothing":

- A **conductor-count ceiling** with a stated reason. The dense solve is O(N_seg³) in the boundary
  unknowns and the modal step O(N³) in conductors; neither is the binding constraint at small N, but
  an unbounded N with no message is how a user discovers a limit by waiting. Pick a number, say it,
  and say what it is bounded by.
- The **geometric symmetry check does not disappear — it stops being a REFUSAL and becomes the
  route selector's input.** `CheckGeometricSymmetry` still tells you a pair is symmetric, which is
  what makes L7b's exact oracle applicable in tests (D1). Keep the method; change its callers.
- Every L8/L9/LW refusal elsewhere in that file is untouched.

**R-gen-10. `EmProblem` still needs no new field, and the extractor still needs no change.** If N
conductors require a new input, something has gone wrong — the cross-section already carries them.

---

## 6. Validation — the gate ladder

`tests/Engine.Tests/Mom/` for the kernel, `tests/Ui.Tests/Em/` for anything that reaches the panel.
Tag anything at or above ~5 s `[Trait("Category","Benchmark")]`; nothing here should come close.

**Tier G1 — the decision measurement (M1's whole content).**
- **Route A vs L7b's exact answer, on a symmetric pair, swept into the regime where loss dominates.**
  Compare S-parameters entry-by-entry across frequency from below the Wheeler crossover up to
  20 GHz, at tanδ = 0, 0.02 and 0.2, with real and with perfect metal. Report the worst relative
  error and the `ModeCouplingResidual` alongside it, so the two can be seen to track.
- **The residual must PREDICT the error.** If `ModeCouplingResidual` is small where the error is
  large, it is not the indicator this brief claims and R-gen-5 needs rethinking — that is a finding,
  not a tolerance to loosen.

**Tier G2 — exact and self-consistency oracles, which need no external data.**
- **A symmetric pair through the GENERAL path reproduces L7b's fixed-matrix answer.** This is the
  continuity gate and the analogue of L7b's own C1 byte-identity gate. It is the single most
  important test in this phase: it is the only one where an exact answer exists for a genuinely
  coupled, genuinely lossy structure.
- **N far-apart conductors reproduce N independent single lines** — including with DIFFERENT widths,
  which is the asymmetric case's exact oracle and the reason it is worth building. Assert against
  kernel A's own single-line result for each width.
- **The merged-strip limit generalises.** L7b's Tier C3 substitute (as the gap closes, the pair's
  even-mode total capacitance approaches a single strip of the combined width) works for three
  conductors too, against a strip of width 3W + 2S.
- **Reciprocity, passivity, losslessness of the 2N-port**, extended from L7b's 4-port versions.
  **Reciprocity deserves particular attention**: L7b's block construction made `S = Sᵀ` structural,
  and a general `Tv·diag·Ti⁻¹` does not obviously preserve it. If it holds only to solver tolerance
  rather than exactly, say so and say why — that is a real change in the strength of the guarantee.
- **Normalisation invariance (R-gen-3):** scale `Tv`'s columns by arbitrary non-zero factors —
  including a pathological spread like `diag(1e3, 1e−3)` — and assert the S-matrix is unchanged to
  round-off. This is the test that catches a wrong `Ti`.
- **Reported `Zc` is in OHMS (R-gen-3a):** a symmetric pair's two modal impedances must equal L7b's
  `Z_e` and `Z_o`. Without this test the naive implementation publishes phase velocities and every
  number on the plot is wrong by a different per-mode factor while the S-parameters look perfect.

**Tier G3 — the degenerate and near-degenerate cases (R-gen-6).**
- Two identical conductors far apart (exactly degenerate) solves and gives the right terminal answer.
- Two identical conductors at a gap that makes the modes nearly degenerate — the numerically nastiest
  case, where eigenvectors are ill-conditioned even though the answer is well-conditioned.
- Mode order is stable across a frequency sweep (R-gen-7): assert `Zc[mode]` traces do not swap.

**Tier G4 — refusals stay specific.** Over the conductor ceiling refuses by name with the number and
what bounds it. Every L7b refusal that is NOT superseded still fires with its own wording.

**Tier G5 — co-simulation, end to end, extended.** L7b's `EmCoSimulationTests` gate with **three**
conductors: extract, Simulate, back-annotate a 6-port `.snp`, run an HB analysis against it.
`EmBackAnnotation` should need no change — if it does, that is a defect in L7b's idempotency key
(it keys on the setup name precisely so a changing port count repoints the same component), and it
should be fixed there rather than worked around here.

---

## 7. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **M1** | Route A: symmetrise → `Gevd(Re[C], [L]⁻¹)` → `Ti` → per-mode diagonal extraction → `ModeCouplingResidual`. **No s-parameters, no refusal changes, no UI.** | **Tier G1 green AND THE NUMBER REPORTED.** Stop here and report before starting M2 — D2 says the measurement decides whether Route B is ever built |
| **M2** | *Conditional on M1.* A dense complex QR eigensolver (Hessenberg + shifted QR), only if M1 shows Route A's error is unacceptable in a regime that matters | Route B reproduces Route A wherever Route A is accurate, and closes the gap where it is not |
| **M3** | The general `Tv·diag·Ti⁻¹` transform → 2N-port Z → `RFNetwork.ZToS`; the mode-axis `tline` group | Tier G2 green — **especially the symmetric-pair continuity gate** |
| **M4** | `CanSolve` narrowed again; the conductor ceiling; the route-selector change to `CheckGeometricSymmetry`'s callers | Tier G3 + G4 green |
| **M5** | Three-conductor co-simulation end to end | **Tier G5 green — the L7b-b phase gate** |

**M1's gate is the one to take seriously, and it is a REPORTING gate as much as a testing one.** The
whole staging decision of L7b rested on the claim that the general case needs an eigensolver; this
phase's staging rests on whether a *real* one plus a perturbation is enough. Neither is worth
guessing at.

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass.

---

## 8. Explicitly out of scope

- **A general non-symmetric complex eigensolver, unless M1 earns it** — D2.
- **Frequency-dependent modal matrices.** Route A's `Tv` is fixed by the lossless problem and is the
  same at every frequency (R-gen-7). If Route B is built, it will produce a per-frequency `Tv`, and
  mode TRACKING across the sweep then becomes a real problem rather than a free one — scope that
  with Route B, not before.
- **Inhomogeneous-medium modal theory beyond quasi-TEM.** Kernel A is quasi-static by construction;
  a mode here is a quasi-TEM mode. Full-wave modes are L8.
- **Stripline, full-wave (L8/L9), wirebonds (LW1/LW2), the manual cut-line tool, the current-density
  heat map** — all still their own briefs.
- **Adaptive frequency sampling.** Route A's decomposition is frequency-independent, so there is
  still nothing to adapt.

---

## 9. File map (indicative)

```
src/Engine/Mom/
  ModalDecomposition.cs   — the general decomposition beside the symmetric one (existing file)
  RlgcToSparams.cs        — the general 2N-port; BuildCoupledPair becomes its N=2 case (existing)
  QuasiStaticKernel.cs    — R-gen-9's narrowed refusals (existing file)
  ComplexEigen.cs         — M2 ONLY, and only if M1 earns it (new)

tests/Engine.Tests/Mom/
  GeneralModalTests.cs        — Tiers G1/G2/G3/G4
  CoupledLineTests.cs         — L7b's own tests; they must keep passing UNCHANGED (existing file)
tests/Ui.Tests/Em/
  EmCoSimulationTests.cs      — Tier G5's three-conductor case (existing file)
```

---

## 10. Three things to report back on, whatever else happens

1. **Route A's measured error, and where it stops being acceptable.** The number, the regime, and
   whether `ModeCouplingResidual` predicted it. This is the deliverable M1 exists for; a phase that
   ships Route A without stating its error has not done the thing the phase table asks for.
2. **Whether reciprocity survived as a STRUCTURAL property or degraded to a numerical one.** L7b
   could say `S = Sᵀ` falls out of the construction. If that is no longer true, the completion note
   must say so plainly rather than leaving a reader to assume the stronger claim still holds.
3. **Whether L7b's symmetric closed form is still worth keeping as an oracle** — or whether the
   general path proved accurate enough at N = 2 that maintaining a second exact construction in the
   tests earns nothing. Either answer is fine; leaving it undecided is what is not.
