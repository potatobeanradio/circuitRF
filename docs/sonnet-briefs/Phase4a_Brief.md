# Phase 4a — Implementation Brief: Single-Tone Harmonic Balance → Hero 2 (Claude Code / Sonnet)

**Goal:** the **single-tone harmonic-balance engine** — the core HB solver — validated on **Hero 2**
(grounded-source GaN HEMT PA). This is the make-or-break gate of Phase 4: prove the single-tone engine
before the sweep/transform layers (4b loadpull, 4c multi-tone, 4d multi-device) build on it.

> Read first, in order: root `CLAUDE.md`, `src/Engine/CLAUDE.md`, `src/Engine/HarmonicBalance/CLAUDE.md`,
> then `docs/design/harmonic-balance.md` (rev 2 — the whole note, authoritative), and for the pieces it
> CONSUMES: `docs/design/nonlinear-dc.md` (the Phase-3 Evaluate/AD/SDD/nonlinear-DC solver — already
> built), `docs/design/linear-engine.md` (§2.1 three MNA uses, §4.4 Z_Port/tone sources, §10 reuse by
> HB). Where this brief and a design note disagree, the design note wins — flag, don't guess.

## Prerequisite (done)
Phases 1–3 complete and passing: expression engine + elaboration + `.cnl` reader; linear engine
(MNA, DC, S-params, RfCore, Hero 1 + Hero 1B); nonlinear DC + the `Evaluate (i,q,dg,dc)` contract +
forward-mode AD + the SDD device (Hero-3-FET converges). **Phase 4a CONSUMES all of this — it does not
re-implement the Evaluate contract, the AD engine, the SDD, or the nonlinear-DC solver.** It adds the
frequency-domain machinery on top.

## Working style (important — read this)
**Focus on diagnostics over extensive problem-solving for the HB engine and convergence.** HB
convergence debugging can rabbit-hole; the owner has limited appetite for deep convergence work in
this gate. So: build the engine, build good **diagnostics** (the convergence trace from Phase 3,
extended to HB — residual per Newton iteration, per continuation step), and if Hero 2 doesn't
converge or doesn't match, **report the diagnostic and the symptom rather than launching a large
solo investigation.** Small fixes are fine (a sign, an index, an FFT-scale error). Large convergence
re-architecture is NOT — flag it for the owner / a design pass. Do not silently spend enormous effort
or context grinding on convergence.

## Scope — build the single-tone HB engine (in this order)

### STEP 1 — New `.cnl` vocabulary the netlist needs
Before the engine, the reader must parse Hero 2's new components/directive (all designed in the docs):
- **`Z_Port`** (linear-engine §4.4): general N-port, Group 2, `Z[i,j]=<freq-expression>`. `freq` is the
  reserved injected stamping-frequency keyword (expressions.md §3). Hero 2 uses the 1-port (2-net) case.
  Stamp by the §4.1 `Z(ω)` branch expansion. Reuses the expression engine with `freq` in scope.
- **`V_1Tone` / `V_nTone`** (linear-engine §4.4): one internal model, two netlist spellings. Params
  `Freq` (capital F — the tone), `V` (complex phasor amplitude), `Phase` (deg, optional, adds), `Vdc`
  (optional, stamped only at freq=0). Stamps Vdc at k=0, the phasor at the harmonic where the stamped
  `freq == Freq`, zero elsewhere.
- **The complex-helper functions** (expressions.md §7): `real`/`imag`/`abs`/`mag`/`phase`/`phase_rad`/
  `polar`. Hero 2 uses `real()`. (Some may already exist from Phase-3 follow-up — reconcile.)
- **The HB analysis directive** (harmonic-balance §3.2): `analysis Name type=hb Tone=… MaxHarm=…
  Sweep="var: a .. b step s" …`, all values resolving through the expression engine (so config can be
  parameters). This is the FIRST real analysis-directive grammar (replaces the opaque `RawDirective`
  for `type=hb` only; other analysis types stay `RawDirective`).

### STEP 2 — The HB engine core (single-tone)
Per harmonic-balance.md, building on the Phase-2 linear engine and Phase-3 Evaluate/AD:
- **Partition** the elaborated netlist into the linear subnetwork and the nonlinear device(s), at the
  nonlinear-facing nodes (the partition seed is precomputed: `NonlinearComponents`/`NonlinearNodes`,
  data-model §3).
- **Interface extraction** (linear-engine §10, §2.1 third use): build the linear-partition MNA, factor
  per harmonic `k` at frequency **exactly `k·f0`** (the exact-harmonic guarantee, linear-engine §4.4 —
  compute `k*f0` as the same double the Z_Port band edges use), multi-RHS extract the interface
  Y-matrix at the nonlinear-facing nodes, AND solve the source-driven response for the interface
  excitation vector (bias + drive transformed to the interface).
- **The error function & FFT layer** (harmonic-balance §4, §5): per Newton iteration, IFFT the current
  spectral V-guess to time samples → call the **Phase-3 `Evaluate`** at each sample → FFT the returned
  i (and q) back to harmonics → form `F(V) = Y_s·V_s + Y_{N×N}·V + I_nl + jω·Q_nl`. FFT conventions are
  FROZEN in `src/Engine/HarmonicBalance/CLAUDE.md` — use them exactly (DC + positive harmonics, the
  documented scale, conjugate reconstruction). `FFTOverSample` sets the time-grid density without
  growing the Newton unknowns.
- **The conversion-matrix Jacobian** (harmonic-balance §4): the real-valued split-form Jacobian, each
  (n,k)-(m,i) block a 2×2 real from the AD-supplied `G_{k±i}`/`C_{k±i}` (the Phase-3 `dg`/`dc`, FFT'd) —
  BOTH sum and difference terms. Rebase MATLAB 1-based indices to C# 0-based (flagged in HB CLAUDE.md).
- **Dense Newton** (harmonic-balance §8): the HB Newton solve is DENSE (NumFlat) — the conversion
  matrix is dense; the SPARSE solve is only the linear-partition MNA in extraction.
- **Initial guess** (harmonic-balance §10): seed from the **Phase-3 nonlinear-DC solver** (already
  built — CALL it, don't rebuild) for k=0, plus a small `1e-3` harmonic seed.
- **Continuation** (harmonic-balance §11): `DriveStepping {IfNecessary, Always, Never}`, default
  IfNecessary — direct solve first (warm-start from previous sweep point across the power sweep), fall
  back to power-ramping only on failure.
- **Writeback** (data-model §7): the converged spectra into the `V` and `I` cubes `{node, harmonic, Pin}`.
- **Commensurability check** (harmonic-balance §3.1): at setup, validate every source `Freq` lands on
  the declared tone grid; error naming the offending source if not.

### STEP 3 — Diagnostics (build alongside, not after)
- Extend the Phase-3 **convergence trace** to HB: residual ‖F‖ per Newton iteration, per
  continuation/power-sweep step, plus which step converged/failed. This is the primary tool if Hero 2
  misbehaves — REPORT it rather than grinding.
- Reuse the singular-node diagnostic for the extraction MNA.

## Acceptance gate — Hero 2 (sanity check, not a numerical benchmark)
The owner has generated external golden node voltages (NOT a tight numerical benchmark — a sanity
gate). Files in `testdata/Hero2/`:
- `hero2_golden_reference_n_drain.csv`, `hero2_golden_reference_n_gate.csv`
- Format: semicolon-delimited, columns `hbfrequency; Pave; r <node>.Vb; i <node>.Vb`. Rows are
  (harmonic frequency 0/2e9/4e9/6e9/8e9, power Pave in dBm) → real & imag of the node-voltage phasor.
- **`MaxHarm = 4`** (DC + 4 harmonics → 5 frequency rows per power point).
- **Power sweep −20 to −10 dBm, 1 dB steps (11 points).** (The owner will set the hero2.cnl to match —
  MaxHarm=4, this Pin range — himself; do not regenerate the netlist.)

**Pass criteria (owner's rule — this is a sanity gate):**
- Compare circuitRF's solved `V` at `n_drain` and `n_gate` against the golden CSVs, per harmonic, per
  power point (real and imag).
- **Voltage components with magnitude < 1e-5 (real or imag) are numerical noise — treat as
  pass-by-default**, do not fail the gate on them (e.g. the gate harmonics above the fundamental sit at
  ~1e-23 to 1e-29 — pure floor). Compare only the bins with real signal.
- **Currents are NOT used for validation** (the owner could not extract them from the external tool in
  reasonable time). Validate node voltages only. (Still BUILD the `I` cube writeback — it's needed for
  Phase-4b+ measurements — just don't gate on it here.)
- A reasonable agreement on the signal-bearing voltage bins (the owner will judge; this is a sanity
  check that the HB engine produces the right operating point and harmonic content, not a 1e-6 match).
- Sanity anchors visible in the golden data: DC `n_gate = −3.05 V`, DC `n_drain = +48 V` exactly
  (bias-tee); drain fundamental ≈ −28 V real at −20 dBm; gate harmonics above fundamental ≈ 0.

## Guardrails
- CONSUME Phase 3 (Evaluate, AD, SDD, nonlinear-DC solver) and Phase 2 (linear MNA, extraction) — do
  NOT re-implement them. The HB engine is the frequency-domain layer on top.
- **Diagnostics over deep convergence problem-solving** (see Working style). Small fixes OK; large
  convergence re-architecture → flag, don't grind. Don't burn large context on a convergence rabbit-hole.
- FFT/sign conventions are frozen in HB CLAUDE.md — use exactly; a sign flip is the classic silent HB bug.
- Rebase the HB note's MATLAB 1-based pseudocode to 0-based C#.
- Don't read large data files wholesale into context; the golden CSVs are small but compare
  programmatically, not by eyeballing every row.
- After the gate, update `src/Engine/HarmonicBalance/CLAUDE.md` with what was built.
- Flag design questions to Opus/Chat.

*Phase 4a exit (Hero 2 converges + matches the voltage sanity gate) unblocks 4b (loadpull → Hero 3),
4c (multi-tone → Hero 5), 4d (multi-device → Hero 4).*