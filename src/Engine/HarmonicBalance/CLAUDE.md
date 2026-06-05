# Harmonic Balance engine — local conventions

Standing instructions for `src/Engine/HarmonicBalance`. Read with the root `CLAUDE.md`.
This is the hardest math in the project — design on paper (see `docs/design/harmonic-balance.md`)
before implementing, and keep the pieces below independently testable.

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
  solves k = 0 self-consistently with the RF, capturing drive-dependent self-biasing. The k = 0
  slice must survive into the result cubes, not be discarded after convergence.

## Validation
Hero 2 (single-FET PA power sweep), Hero 4 (2-stage PA), Hero 5 (two-tone IM). References use the
**identical SDD FET** transcribed into other simulators with matched harmonic count and solver
tolerances — so a tolerance miss points at our HB math, not the transistor model.
