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

## Interface to the linear engine
The linear engine (`docs/design/linear-engine.md` §2.1, §10) provides the HB engine **two** things
per harmonic at the nonlinear-facing nodes, not one:
1. the linear subnetwork as a frequency-domain **N-port** (Y- or Z-matrix), wrapped as an RfCore
   `Network`; and
2. the **source-excitation vector** at that interface — the bias + RF drive transformed to the
   nonlinear-facing nodes (a Norton/Thévenin equivalent), per harmonic.
Do not re-derive either inside the HB engine; request both from the linear layer.

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
