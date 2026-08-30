# Harmonic Balance engine — local conventions

Standing instructions for `src/Engine/HarmonicBalance`. Read with the root `CLAUDE.md`.
This is the hardest math in the project — design on paper (see `docs/design/harmonic-balance.md`)
before implementing, and keep the pieces below independently testable.

## Parametric-sweep warm-start + quiet diagnostics (harmonic-balance.md §11.1) — COMPLETE

- **Warm-start (continuation) for the generic HB sweep.** `HbEngine.Run(p, warmStart=null)` takes an
  optional interface-V seed `[N,K+1]`; when supplied it is the Newton guess and the per-point
  `NonlinearDcEngine.Run` DC seed is **skipped**. `HbRunResult` now carries `Converged` + `InterfaceV`
  (the converged interface spectrum). `ParametricSweepEngine.RunInner` returns the converged seed via an
  `out` param; `Run` chains it into the next point — **innermost sweep axis only** (nested/outer sweeps
  return a null seed → each outer step restarts cold), resets on non-convergence, falls back to cold on a
  dimension change. Gated by `AnalysisSettings.HbSweepWarmStart` (**default true**). Two-tone unchanged
  (cold). Benchmark: GaN-PA Pin sweep 22→12 Newton iters, 11→1 DC solves, bit-identical interface
  spectrum. Gate: `HbPinSweepWarmStartBenchTests` (iteration/DC counts + production warm-vs-cold
  equivalence through `ParametricSweepEngine.Run`). All 454 Engine tests green.
- **Quiet by default.** The per-solve stderr traces (`[HB]`/`[HB-DC]`/`[HB2D-DC]`/`[HB trace]` and the
  inductance-regularization notice) repeated once per sweep point. They are now gated behind
  `AnalysisSettings.HbConsoleDiagnostics` (**default false**). The regularization itself always runs
  (it converges to the exact answer as R→0); only its console notice is suppressed. Non-convergence
  warnings still flow through the `AddWarning` channel regardless.

## SDD control-current HB Jacobian `J_cc` (brief-sdd-control-current-hb-jacobian, 2026-06-19) — COMPLETE

Restores quadratic-quality convergence for SDD `_cn` references in HB by adding the
control-current Jacobian coupling, FD-oracle gated (`CompareJacobianNumerical` ≤ 1e-5).

- **Two-pass self-consistent `_c_ref(V)`** (`HbNewton.EvaluateNonlinear` → `RunDevicePass`): when an
  SDD has `C[n]` refs, pass 1 evaluates with `_c_ref` frozen at the entry seed (from `iNlPrev`) →
  `iNl(V)` for the current `V`; then `_c_ref(V)` is back-solved per harmonic from the **TOTAL**
  nonlinear injection `iNl + jωq + ΣH·WNl` (not just w=0); pass 2 re-evaluates with `_c_ref(V)`. The
  inner map is a **single linearization step** (NO inner iteration) so `J_cc` is the exact derivative
  of exactly that one-step residual — that is what the FD oracle differentiates. `cc==null` → byte-
  identical single-pass fast path.
- **`J_cc = B·R·A`** (`HbNewton.AddControlJacobian`, added inside `BuildJ`): `A=∂iNl_total/∂V` (the
  main conversion blocks G + jω·C + ΣH·Dw, no Y_NN/Maas), `R=∂_c_ref/∂iNl_total` (rRef rows, harmonic-
  diagonal), `B=∂F/∂_c_ref` (conversion of the per-w control kernels with H[w] weighting). DC-Im
  rows/cols are zeroed (Maas fictitious-DOF parity); `A`'s DC-Im output row is also zeroed (HbFft
  forces the DC bin real). Composition is a dense real matmul — fine since control refs are rare/small.
- **`HbLinearExtractor.ControlSensitivityRow(omega, branchIdx)`**: `rRef[j] = −(G⁻¹)_{branch,node_j}`
  via N forward solves on the cached LU (sign baked in: `∂_c_ref/∂iNl[j]`). Identity pinned by a test:
  `c0 + Σ rRef·iNl == SolveFullNetwork[branch]`.
- **Per-w control sensitivities** (`SddModel.Evaluate`): `NonlinearResult.DControlCharge` (∂Q/∂_c, w=1)
  and `WeightedTerm.JacCtrl` (∂I[p,w]/∂_c, w≥2) join the existing `DControl` (w=0); defaults keep non-
  control devices unaffected. FFT'd into `ControlJacData.Kernels` for `B`.
- **FD oracle wiring**: `CompareJacobianNumerical` / `HbEngine.RunJacobianDiagnostic` take `cc` and a
  **frozen `iNlPrev` seed** (same seed for analytic + every FD eval). Both also take
  `useControlJacobian` (also on `HbNewton.Solve`) — quasi-Newton fallback + the §3.2 tripwire (oracle
  reports ~0.57 without `J_cc`, ≤1e-8 with it).
- **FD-floor lesson**: tiny ad-hoc control circuits hit the FD relative-error floor on near-zero
  off-diagonal entries (`MaxAbsError ~1e-12` but `MaxRelError ~5e-5`) when the SDD is **linear** (no
  harmonic generation) and the network is **purely resistive** (no phase). Gate tests use a gentle
  quadratic SDD term + a reactive element so all entries are FD-resolvable → margins 1e-10..7e-8.
- **Convergence note**: the one-step `_c_ref` seed lags by an iterate, so for *strong* coupling
  (beta≈0.8) the outer Newton is superlinear, not strictly quadratic (J_cc 28 iters vs quasi-Newton
  31 — J_cc still wins). This is inherent to the brief's single-step design, not a bug.
- 10 gate tests: `SddControlCurrentHbJacobianTests.cs`. **Owed follow-ons — now landed** (brief #4 /
  `SddControlCurrentSParamTests.cs`): the `StampLinearized` control column for S-parameter analysis (design
  §5, see Engine CLAUDE.md), plus the docs-sync of `sdd.md` §6/§8.5 and the control-current design doc.

## Formulation
- **Partition** the circuit into a linear subnetwork (characterized in the frequency domain) and
  nonlinear devices (evaluated in the time domain, transformed with the FFT). The HB unknowns are
  the harmonic voltage phasors at the nonlinear-facing nodes.
- **Residual = frequency-domain KCL** at every node and harmonic:
  linear current + nonlinear current spectrum + source current = 0.
  Nonlinear current spectrum = FFT of `i(v(t))`, where `v(t)` is the inverse-FFT of the current
  guess; charge terms contribute `jωQ`.
- **Jacobian = conversion matrix.** Conductive part `Γ · diag(dg/dv) · Γ⁻¹`; charge part
  `jΩ · Γ · diag(dq/dv) · Γ⁻¹` (Γ = DFT operator, Ω = diagonal of angular frequencies).
  Assembling this correctly is the core of the engine.
- **Amplitude-convention scaling (Phase 4a convergence fix, 2026-06-04).** `HbFft` uses
  full-amplitude phasors: `v(t) = V_DC + Σ Re{Vₖ e^{jkωt}}`, giving `∂v/∂Re(Vᵢ) = cos(iωt)`.
  Maas derives his conversion-matrix blocks for half-amplitude (`∂v/∂Re(Vᵢ) = 2·cos`), so the
  G/C formula `G[k-i] + G[k+i]` is 2× too large. The correct per-term weights (`ConversionWeight`
  in `HbNewton`) are:
  - `G[j=0]` in a `k≥1` row: weight **1** (DC bin normalized ÷N, AC row uses ÷(N/2))
  - `G[j≠0]` in a `k≥1` row: weight **0.5** (half-amplitude correction)
  - Any `G[j]` in a `k=0` row: ×**0.5** additionally (DC row itself uses ÷N)
  The same weights apply to the C (charge) blocks. Y_NN is **not scaled** (frequency-domain,
  no convention mismatch). See `HbNewton.ConversionWeight` for the formula.
- **Jacobian FD verification (Phase 4a, `JacobianFd_MatchesAnalytic_LowDriveAndNearFailing`).**
  `HbNewton.CompareJacobianNumerical` compares the analytic Jacobian (BuildJ) against a central-
  difference FD oracle. The permanent test in `Hero2Tests.cs` asserts 1e-5 relative on all
  non-DC-dummy elements at Pin=0 (low drive) and Pin=18 (near-failing). Actual residual after
  B1 fix: ~3 ppm (FD oracle limit from large J''' in the GaN SDD; Richardson extrapolation
  would be needed to verify below 3 ppm). All per-block-class systematic errors eliminated.
- **B2 — Newton step lambda (2026-06-04).** `HbAnalysisParams.Lambda` (default 1.0) scales the
  Newton update `V += λ·ΔV` in `HbNewton.ApplyUpdate`. Set via `Lambda=` in the cnl analysis
  directive or via `p with { Lambda = x }` in code. Owner tests non-unity λ externally.
- **B3 — Guard harmonic (2026-06-04).** `HbAnalysisParams.GuardHarmonic` (default 0 = off) sets
  the guard index H. In `HbNewton.BuildJ`, when H > 0, the G/C conversion-matrix terms for any
  (k > H OR i > H) block are zeroed before Y_NN is added. Applied to J only, never to F.
  Hard cutoff (default); tapered profile selectable in future. Y_NN is never guarded.
  Set via `GuardHarmonic=` in the cnl directive.

## Models are evaluated only at ω ≥ 0 — engine contract (do not break)
**Every component model is stamped/evaluated at a NON-NEGATIVE frequency.** A two-tone retained
mixing product (k₁,k₂) may have a *negative* physical frequency (e.g. (1,−1) = f₁−f₂ = −10 MHz),
but the engine never passes that negative ω to a model. `HbEngine.RunTwoTone.ExtractMix` extracts
the linear interface at **+|ω|** and applies the conjugate (`Y(−ω) = conj(Y(ω))`, the physical
requirement for a real network; the Norton source at a negative-ω rep is zero since sources sit on
positive carriers). Consequences:
- A `.cnl` `Z(freq)` / `Y(freq)` expression sees `freq` as the **positive magnitude**
  `|k₁f₁+k₂f₂|`. Band on `freq` directly — **`abs(freq)` is unnecessary** (and so is any attempt to
  specify different behavior at −f, which conjugate symmetry forbids). This matches VendorA: users
  think in positive frequency.
- Frequency-band boundaries must be **magnitude windows** (e.g. fundamental = `freq < 2.5e9`), NOT
  single-tone `freq > RFfreq` tests — the latter mis-bands the upper carrier/IM products in
  two-tone (the upper carrier 2.005 GHz is > RFfreq but is still the fundamental band).
- Any future caller of `HbLinearExtractor.Extract(ω)` with ω < 0 must conjugate, or route through
  an `ExtractMix`-style helper, to preserve this contract.

## FFT / sign conventions — fix once, document, never silently change
- Pick and record the time↔frequency sign convention and the harmonic ordering (DC, +k, −k).
- All FFT round-trips must use the same convention; a sign/order mismatch is the most common bug.
- **Pseudocode indexing caveat.** The reference HB pseudocode (`docs/design/harmonic-balance.md`,
  derived from the conference slides) is written in **MATLAB, which is 1-based**. C# is 0-based.
  Every harmonic-index, node-index, and conversion-matrix expression transcribed from the
  pseudocode must be **rebased to 0** — this is a prime source of off-by-one spectrum corruption
  (a misplaced DC bin or a harmonic shifted by one). When porting a formula, convert the index
  math deliberately and add a unit test that pins the DC bin and the first few harmonics to known
  values before trusting the solver.

## Single-tone vs two-tone
- **Single-tone:** uniform-sample FFT. Build and validate this fully first.
- **Two-tone is qualitatively harder.** Spectrum is `{k₁f₁ + k₂f₂}`. Required:
  - **Diamond truncation** (not box) — far cheaper for the same accuracy.
  - **Mixing order ≥ 5** (Hero 5 checks IM2–IM5; IM5 = 3f₁−2f₂ needs |k₁|+|k₂| ≥ 5).
  - Retain **baseband and harmonic-zone products** (e.g. f₂−f₁ = 10 MHz) — needed for IM2/IM4 and
    for the source/load baseband-termination effects this tool targets.
  - **The frequency-index map is a separate, unit-tested component** — do not entangle it with the
    solver. Use an almost-periodic Fourier transform (APFT) with carefully chosen sample times,
    or a frequency-mapping/multidimensional-FFT approach.

## Multiple nonlinear devices (Hero 4)
With ≥2 nonlinear devices, place **all** of them in the nonlinear partition; the surrounding linear
network (input/interstage/output/bias) is one multiport linear block interfacing all
nonlinear-facing nodes. The Jacobian is block-structured across devices. Verify inter-stage
transfer through the linear interstage network at every harmonic.

## Convergence
- Plan **continuation from day one**: power/source stepping (ramp drive, reuse previous solution
  as the next initial guess) is the workhorse for compression.
- Seed from a converged DC operating point (gmin / source stepping lives in the DC analysis).
- Hero-sized problems: sparse direct complex LU per Newton step is fine. Keep a matrix-free
  Newton–GMRES + block preconditioner in reserve for large problems — do not build it prematurely.

## INl current-direction convention — one statement, applied everywhere

**INl[n,k] = current flowing FROM interface node n INTO the nonlinear device.**
Positive = current entering the device (passive sign convention on the device ports).

Source: `EvaluateNonlinear` (HbNewton) accumulates `res.I[p]` — the SDD port current with
passive-sign convention — into `iTime[nodeIdx]`, then FFTs.  The HB residual
`F = Y_NN·(V−V_oc) + INl = 0` confirms this: at DC, `INl[drain,0] = +Idd` (drain current
leaving n_drain into FET) is balanced by `Y_NN·(V_oc−V[drain]) = +Idd` from the supply.

**Consequences for all consumers (derived from KCL at the interface node; NOT ad-hoc sign patches):**

At RF (choke L=1H is open-circuit at f0):
- n_drain has only load tuner + FET.  KCL: `I_into_load = −INl[drain,k]`
- n_gate has only source tuner + FET.  KCL: `I_from_source = INl[gate,k]`

Therefore:
```
Pout          = ½·Re(V[drain,k]·conj(I_into_load))    = −½·Re(V[drain,k]·conj(INl[drain,k]))
Pin_delivered = ½·Re(V[gate,k]·conj(I_from_source))   = +½·Re(V[gate,k]·conj(INl[gate,k]))
Zin           = V[gate,k] / I_from_source              = V[gate,k] / INl[gate,k]  (no negation)
Zsource       = conj(Zin)
Pdc           = Σ V[n,0]·INl[n,0]  over Tuner bias nodes  (supply current = INl at DC node)
```

The sign asymmetry between Pout (−½) and Pin (+½) is physically required, not a patch.
The absence of negation in Zin is required; negating would make Re(Zin) negative (non-physical).

Do NOT introduce per-site sign flips to "make a number look positive." Derive from this convention.

**Caveat — the `I_from_source = INl[gate,k]` identity assumes the gate node carries ONLY the source
tuner + FET.** If the user wires passives at the gate (an input-matching network, package
parasitics, a gate shunt), the source also feeds those, so `I_from_source = INl[gate,k] + Σ I_passive`
≠ `INl[gate,k]`. Using `INl[gate]` then reports the FET's *intrinsic* gate impedance (e.g. exactly
the `I[1,0]=_v1/Rg` value) instead of what the source actually sees. The **loadpull engine** therefore
does NOT use `INl[gate]` for Zin/Zsource/Pin_delivered — it recovers the true source-delivered current
`ISrcIn = I_srcZport − I_choke` (two source-tuner branch currents, via `HbLinearBackSolver`), which by
KCL equals `INl[gate] + Σ I_passive` and reduces to `INl[gate]` in the canonical case. See
`src/Engine/Loadpull/CLAUDE.md` (ISrcIn / `Iin` cube) and `LoadpullEngine.ComputeSourceInputCurrent`.

## Interface to the linear engine
The linear engine (`docs/design/linear-engine.md` §2.1, §10) provides the HB engine **two** things
per harmonic at the nonlinear-facing nodes, not one:
1. the linear subnetwork as a frequency-domain **N-port** (Y- or Z-matrix), wrapped as an RfCore
   `Network`; and
2. the **source-excitation vector** at that interface — the bias + RF drive transformed to the
   nonlinear-facing nodes (a Norton/Thévenin equivalent), per harmonic.
Do not re-derive either inside the HB engine; request both from the linear layer.

### DC interface extraction — real Y(0), auto-regularized (Maas 3.10–3.14; linear-engine §4.3.1)
The k = 0 (DC) harmonic uses the **real DC admittance** `Y_{N×N}(0)` and Norton source
`I_src(0)`, extracted identically to `Extract(ω)` but evaluated at ω = 0:
1. Zero sources → Z-column extraction → `Y_{N×N}(0)` (via `BuildMna(0, zeroDrive: true)`).
2. Active sources → single solve → `V_oc(0)` → `I_src(0) = −Y_{N×N}(0)·V_oc(0)`.

**No virtual-admittance clamp.** The old `Y_DC_VIRT = 1e6 S` prevented the DC interface
voltage from shifting with drive (defeating self-biasing). The real Y(0) lets the Newton
solve balance the DC component self-consistently.

**Voltage-pinned singularity handling** (`InductanceRegularization`, default `IfNecessary`):
An ideal-choke (no `R=`) through an ideal voltage source → `Z(0) = 0` at the interface node.
`ExtractDC` detects this (`|Z_NN[i,i]| < 1e-15`) and handles it per the regularization mode
(identical tri-state behaviour to `ConductanceRegularization`/gmin):
- **`IfNecessary`** (default): retry with series `R = InductanceRegR` (1 µΩ default) added to
  all inductor branches. Warns to stderr naming the pinned nodes. Clean circuits pay nothing.
- **`Always`**: apply from the start (skip the speculative first attempt).
- **`Never`**: throw `SingularMatrixException` with full diagnostic (V_oc, node names, fix hint).

This is regularization, not a circuit edit — the inductive dual of gmin. `Y_{N×N}(0) ≈ 1/R_reg
≈ 1e6 S`, giving good Jacobian conditioning (same order as the old clamp). Converges to the
exact answer as R_reg → 0. See linear-engine §4.3.1 for the principled Option-2 upgrade (deferred).

## Output
Every HB run writes a **`DataSet`** — primary cubes `V` and `I` (axes typically
`{…sweep, node, harmonic}`), each a single-kind `DataCube` (→ `src/Core/Data/CLAUDE.md`). FOM
extraction (Pout, gain, drain efficiency, PAE, IMn) is done by **measurements**, which are added to
the same DataSet as named cubes; the HB engine does not invent its own result type. See
`docs/design/measurements.md`.
- **Retain the DC (k = 0) component of V and I, including DC-source branch currents.** `Pdc`
  (hence `PAE`/`DE`) is read from the HB DC component — *not* a separate DC analysis — because HB

## IProbe branch currents — single-tone (brief-iprobe-currents-hb, 2026-06-18)

**Single-tone `HbEngine.Run`** now emits **`I:<probe>`** cubes (axis `[harmonic]`, `Complex`) for
each `IProbeModel` in the circuit, recovering the full current spectrum from the linear back-solver's
branch-current rows (`x[LastBranchIndex]`, where `LastBranchIndex` is the absolute MNA row returned
by `AddBranch()`).

- These coexist with the existing device-port `I:<instance>:<terminal>` cubes (different key format).
- **`__ProbeBranches`** side-cube (axis `probe` with probe-name Labels, Real, all-zeros) marks the
  IProbe set for Data Display filtering — analogous to `__LabeledNodes`. `StackSweepAxis` passes it
  through sweep-invariantly.
- `DcResultPacker` now also emits `__ProbeBranches` for DC runs, so the display filter treats DC and
  HB identically.
- **Two-tone CLOSED (2026-06-25):** `RunTwoTone` now back-solves the linear network per mixing product
  (`SolveMixFull`) to recover linear-only node voltages AND IProbe currents over the mix lattice — V cube spans
  all user nodes, I cube carries IProbe branches (`__ProbeBranches`). Negative-ω reps solve at `|ω|` with
  conjugated injection/source + conjugated result (reusing `ExtractMix`'s cached LU). See
  [[twotone-result-completeness-and-spectrum]].
- 5 gate tests: `HbIProbeCurrentTests` (T1–T4 HB; T5 DC) in
  `tests/Engine.Tests/HarmonicBalance/HbIProbeCurrentTests.cs`.
  solves k = 0 self-consistently with the RF, capturing drive-dependent self-biasing. The k = 0
  slice must survive into the result cubes, not be discarded after convergence.

## Validation
Hero 2 (single-FET PA power sweep), Hero 4 (2-stage PA), Hero 5 (two-tone IM). References are
externally generated using the **identical SDD FET definition on both sides**, with matched harmonic
count and solver tolerances — so a tolerance miss points at our HB math, not the transistor model.

## Phase 4c deliverable — multi-tone (two-tone) HB — COMPLETE (2026-06-05)
GENERALIZES the single-tone engine from the scalar harmonic axis k to the 2-D mixing lattice
(k₁,k₂); it does NOT rewrite it. **Single-tone is the `NumFreqs=1` path and stays on the original
`HbNewton`/`HbEngine.Run` code (golden byte-identical).** The two-tone path is parallel, sharing the
real-split block math (the FD oracle guards it).

### Components
- **`MixingGrid`** — diamond-truncated half-plane lattice, locked mixIndex order (§16 item 1):
  ascending |k₁|+|k₂|, then upper-half-plane (k₁>0, or k₁=0∧k₂≥0), k₁ then k₂ descending. (0,0)=DC
  is index 0. M = 1 + order·(order+1). Raising the order only appends — never renumber.
- **`HbFft2D`** — separable 2-D FFT composing the 1-D primitives. **Amplitude convention is GLOBAL,
  not per-axis:** divisor N₁N₂ at the global DC bin (0,0), N₁N₂/2 at every other bin (a per-axis
  ½·½ wrongly halves cross bins twice). Same for `ConversionWeight2D`. Forward2D folds out the
  redundant k₂=0 negative-k₁ half (real-signal symmetry). The Jacobian's G/C spectra use a
  NON-folded forward so `SpecGet` reconstructs every (k₁∓i₁,k₂∓i₂) lookup.
- **`HbNewton2D`** — `EvaluateNonlinear2D` / `BuildF2D` / `BuildJ2D` / `Solve`: the single-tone
  blocks with k→mixIndex, scalar k∓i → vector (k₁∓i₁,k₂∓i₂), charge rotation by ω=2π(k₁f₁+k₂f₂).
  Reduces EXACTLY to single-tone when k₂≡0. Guarded by the two-tone FD-Jacobian oracle
  (`HbJacobian2DTests`, gate 1e-4 — the active-device FD floor is ~2e-5; structural bugs show at
  O(0.1–1), as the per-axis ConversionWeight2D bug did at exactly 0.5).
- **`HbEngine.RunTwoTone`** — **no longer the default two-tone route (2026-08-30).** `Run` now sends
  two tones to `RunMultiTone` (the T-tone lattice) unless `AnalysisSettings.HbTwoToneOnLattice` is
  cleared, which is what reaches this method; it is a measured 3.5× on `hero5.cnl`, and §6.1–§6.3 of
  `harmonic-balance.md` stay in the tree as the independent second implementation the lattice is
  gated against (`HbNewtonNdVs2DTests`). **The committed Hero-5 goldens were produced on THIS path
  and are deliberately left that way**, so `Hero5GateTests` is now a cross-path check — do not
  regenerate them (`src/Engine/RESOLVED.md` §HB-P1). Either route extracts the linear interface per
  mixing product; `HbResult` carries the `MixingGrid` + tone freqs, with V/INl on the mixIndex axis,
  plus per-device branch-current cubes `I:instance:terminal` (axes `[mixIndex, Pin]`). See the ω≥0
  contract above for negative-frequency handling.
- **`HbNewton2D.ComputeDevicePortCurrents2D`** — post-convergence per-port current extraction for the
  two-tone path. Mirrors `HbNewton.ComputeDevicePortCurrents` (IFFT V → time domain, device eval, FFT
  per port) but uses `HbFft2D.Inverse2D` / `HbFft2D.SpecGet` over the mixing lattice. Returns
  `Dictionary<string, Complex[]>` keyed "instancePath:terminal" → `Complex[M]`. Values are numerically
  identical to INl at the device's interface nodes (same passive-sign convention). Called per sweep point
  in `RunTwoTone`; results are stored as `I:instancePath:terminal` cubes in the DataSet.
- **`TwoToneMeasurements`** — IMD selectors: `Tone(k₁,k₂)` (inverts mixIndex, conjugate fallback for
  non-retained reps), per-product Pout/Pout(dBm), `ImDbc`.

### Hero 5 gate — PASSED (self-generated golden; physics anchor independent)
`testdata/Hero5/hero5.cnl` (two tones 1.995/2.005 GHz, MaxMixOrder=5, MaxHarm=4, baseband
ZLoad₀=10+j10). Self-generated golden over the mixIndex axis (V/I at n_gate, n_drain) with the
<1e-5-is-noise rule — SELF-CONSISTENCY, not independently validated. **Independent physics anchor:
the IM3 3:1 slope is exact — carrier slope 1.00, IM3 slope 3.00** over −18..−12 dBm; the
unequal-amplitude (V[2]=0.5·V[1]) guard gives output carrier ratio 0.5001. The Newton residual
floors at ~1e-9 (huge Y dynamic range from 1µΩ near-shorts → Y~1e6 caps conditioning), so IMD
slope checks must sit in a drive window where the product current is well above ~1e-9 yet below
compression.

### Owed
Cross-check Hero 5 against an external reference with the identical SDD FET definition (currently
self-consistent only). Phase 4d (multi-device → Hero 4) remains to complete Phase 4.

## Phase 5 corrective brief — COMPLETE (2026-06-06)
Three coordinated corrections to the Phase 5-3/5-5 retrofit:

### C1 — Linear back-solve (lazy cached reconstruction)
`HbLinearBackSolver` (in `HarmonicBalance/`) lazily reconstructs linear-interior node voltages and
branch currents from the converged interface solution. The per-harmonic LU factorization performed
during HB extraction is cached in `HbLinearExtractor._luCache` and **reused** in
`SolveFullNetwork` — one cheap back-solve per harmonic per sweep point, no refactorization.

`ILinearBackSolver` (in `src/Core/Expressions/`) exposes `TryGetNodeNumber`, `GetNodeVoltage`,
`SweepCount`, `NonGroundCount`. The full solution vector `x[0..NonGroundCount-1]` = node voltages;
`x[NonGroundCount..]` = branch currents. `HbRunResult.BackSolver` returns the solver after Run().

**LU reuse proof:** tightening HB Tol from 1e-6 to 1e-10 improves cube↔back-solve agreement by
11 billion× at intermediate sweep points — proving the residual is Newton convergence quality, not
a back-solve bug. Test: `BackSolver_TighterHbTol_ImprovesCubeAgreement` in `LinearBackSolveTests`.

### C2 — Branch-current addressing (brief-unify-i-cube-engine, 2026-06-18)
All branch currents live in a **single `I` cube** with a labeled `branch` axis — mirroring `V`'s
`node` axis. There are no per-branch `I:instance:terminal` cubes.

- **Single-tone:** `BuildSingleToneDataSet` emits `I [branch, harmonic]` Complex. IProbe branches
  come first (labeled), device-port branches follow (unlabeled in `__ProbeBranches`).
- **Two-tone:** `BuildTwoToneDataSet` emits `I [branch, mixIndex]` Complex over device-port
  branches only. Two-tone IProbe back-solver is deferred — no `__ProbeBranches` in two-tone DataSets.
- **`__ProbeBranches`** (axis `"probe"`, labels = IProbe names) marks the labeled subset, mirroring
  `__LabeledNodes`. Absent in two-tone DataSets (no IProbe two-tone support yet).
- **Measurement accessor:** `HB1.I("Ids")` (branch name) / `HB1.I("Ids", 1)` (pin harmonic) /
  `HB1.I` (whole cube) — all fold through the `V`/`INl` node-accessor path with `axisName="branch"`.
- `INl` persists as an internal diagnostic cube; it is **not** the measurement/test-facing current path.

### C3 — Optional sweep axis
`BuildSingleToneDataSet` now takes `isSweep: bool` (computed from `HbAnalysisParams.HasSweep`):
- **Sweep present:** V/INl have axes `[sweep, node, harmonic]`; Converged/Residual have axis `[sweep]`;
  I cube has axes `[sweep, branch, harmonic]`.
- **No sweep:** V/INl have axes `[node, harmonic]` (2 axes); Converged/Residual are
  rank-0 scalars; I cube has axes `[branch, harmonic]`.
No dummy sweep axis is fabricated. Gate: `NoSweepHbTests.NoSweep_VCube_Is2D_NodeHarmonic`.

## Phase 5-6 — Composable nested parametric sweep — COMPLETE (2026-06-06)

`ParametricSweepEngine` (in `src/Engine/ParametricSweepEngine.cs`) wraps any inner analysis
(HarmonicBalanceAnalysis or another ParametricSweepAnalysis) and **prepends one named axis** to
every cube in the resulting DataSet per nesting level. N nested sweeps → N prepended axes.

### Components
- **`DataCube.PrependAxis(Axis, IReadOnlyList<DataCube>)`** (RfCore) — stacks N same-shaped cubes
  along a new prepended axis. Accesses `_complexData`/`_realData` directly (internal access for
  zero-copy). Validates DataKind + rank + axis lengths.
- **`DataSet.StackSweepAxis(Axis, IReadOnlyList<DataSet>)`** (RfCore) — calls PrependAxis for each
  key present in all datasets. Result is a new DataSet with every cube one rank higher.
- **`ParametricSweepAnalysis`** (Core/Design/Analysis.cs) — new Analysis subtype with
  `SweepVarName`, `SweepValues` (double[]), `InnerAnalysisName`. Slots into `TestBench.Analyses`.
- **CNL directive:** `analysis SW1 type=parametric_sweep Var=Vgg Values=-3.0,-3.2 Inner=HB1`
  Parsed by `CnlReader.TryParseParametricSweepDirective` (inserted in the analysis dispatch chain).
  Values= is comma-separated, unquoted or quoted.
- **`ParametricSweepEngine.Run(sweep, lib, tb, settings?)`** — for each value: temporarily
  overrides `tb.GlobalVariables` (restored in finally), re-elaborates, dispatches inner analysis
  via `RunInner`. Recursive for nested sweeps. Returns `DataSet.StackSweepAxis(...)` result.

### Override mechanism
The swept variable is injected by temporarily mutating `TestBench.GlobalVariables` — the existing
variable entry is replaced with `Variable(name, value.ToString("G17"))`, then restored in a
`finally` block. Re-elaboration from `Elaborator(lib).Elaborate(tb)` sees the overridden value
and propagates it to all component resolved params (VoltageSourceModel.V, etc.). This is the
correct path for sweeping circuit globals — `UpdateSweepPoint`/`ReEvaluateGlobals` (inner HB sweep)
only updates ToneSourceModels and is NOT sufficient for bias/topology parameters.

### Cube shape after stacking
- 1-level (Vgg outer × HB1 inner with Pin sweep): V axes = `[Vgg, node, harmonic, Pin]`
- 2-level (Vgg outer × Vdd middle × HB1 inner): V axes = `[Vgg, Vdd, node, harmonic, Pin]`
- The unified I cube gains the same prepended axes: `I` axes = `[Vgg, branch, harmonic]` (1-level sweep).

### Gate: `Hero2ParametricSweepTests` (5 tests)
- `CnlRoundTrip_ParametricSweepDirective_Parses` — CNL parse
- `SingleLevel_VggSweep_PrependsSweepAxis` — 1-level axis structure
- `TwoLevel_VggVdd_PrependsTwoAxes` — 2-level axis structure (recursive composition)
- `SingleLevel_PositionalSlice_WorksAtEachVggPoint` — axis-count-agnostic positional slicing
- `SingleLevel_DcDrainCurrent_ShiftsWithVgg` — physics: Idc(Vgg=-3.0) > Idc(Vgg=-3.2)

## Phase 5-7 — DataSet export (.mat / .npy) — COMPLETE (2026-06-06)

Exports a `DataSet` to MATLAB v7.3 / HDF5 (`.mat`) or NumPy packed structured array (`.npy`).
When `IncludeLinearNetwork = true`, the per-harmonic linear MNA system is also serialised —
enough for a consumer to reconstruct any linear-interior node voltage or branch current without
rerunning the HB sweep.

### Engine changes (this directory)
- **`HbLinearExtractor._luCache`** extended to cache the pre-factorization sparse G matrix
  (`CompressedColumnStorage<Complex>`) alongside the LU. `BuildCsc()` is called before
  `Factorize()` and stored in the tuple.  `HbLinearExtractor.GetSparseG(omega)` exposes it.
- **`MnaSystem.BuildCsc()`** — new public method that wraps `BuildCscMatrix()`, snapshotting
  the sparse matrix before it is consumed by `Factorize()`.
- **`HbLinearNetworkPayload`** — new class implementing both `ILinearNetworkPayload` (RfCore)
  and `IBackSolverProvider` (RfCore).  Wraps `HbLinearBackSolver` to expose all engine data
  the exporter needs without a circular dependency.
- **`HbRunResult.LinearPayload`** — new property, always non-null when `BackSolver` is non-null
  (§8.6: expose always; cost is zero — data was already retained).

### RfCore changes (sibling project)
- **`ILinearNetworkPayload`** — bridge interface for the linear-network data.
- **`IBackSolverProvider`** — optional interface enabling eager evaluation of V_linear/I_linear.
- **`LinearEvalMode`** — enum: `EvaluateNone` / `EvaluateAll` / `EvaluateSpecified`.
- **`ExportFormat`** — enum: `Mat` / `Npy`.
- **`ExportOptions`** — record: `IncludeLinearNetwork`, `LinearEvalMode`, `EvalNodeNames`,
  `EvalBranchRefs`, `SizeWarningThresholdMiB` (default 100).
- **`NpyWriter`** — NumPy v1.0/v2.0 structured-array writer; header padded to 64-byte boundary.
- **`MatWriter`** — PureHDF 2.1.2 HDF5 v7.3 writer; complex data as compound `{real, imag}`.
- **`DataSetExporter`** — entry point: size estimate → optional warning → EvalMode evaluation
  → format dispatch.

### Key design facts
- **k=0 vs k≥1 sparsity**: the design note claimed topology-invariance across ALL harmonics, but
  in practice the DC MNA (ω=0) can have a different sparsity pattern than AC harmonics because
  zero-admittance entries from capacitors at DC are stored in the CSC (but some components may
  not stamp them consistently). AC harmonics k≥1 ARE topology-invariant. The NpyWriter and
  MatWriter call `GetSparseG(k)` per harmonic, so both patterns are handled correctly.
- **Eager back-solve** (`EvaluateAll`/`EvaluateSpecified`): implemented via `IBackSolverProvider`;
  falls back to zero if the payload is not `HbLinearNetworkPayload`.

### Tests
- **`LinearNetworkPayloadTests`** (7 tests, HarmonicBalance/): round-trip oracle including k=0 DC
  and k=1 fundamental; all harmonics; drain-node cross-check; omegas validation.
- **`DataSetExportTests`** (14 tests, Export/): .npy magic/header/alignment/size; .mat HDF5
  read-back; size-warning fires/not-fires; EvaluateNone/All; IncludeLinearNetwork in both formats.

**Total tests: 367 pass, 0 fail (171 Core + 196 Engine).**
