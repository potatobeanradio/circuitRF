# Loadpull Engine — local conventions

Standing instructions for `src/Engine/Loadpull`. Read with root `CLAUDE.md` and
`src/Engine/CLAUDE.md`. Design note: `docs/design/loadpull.md`.

## What lives here
- **`GamReader`** — `.gam` grid file parser (Γ or Z points, mag_ang / re_im / re+j*imag).
- **`LoadpullEngine`** — 2-D sweep orchestrator: outer Γ/Z grid × inner adaptive Pin drive-up.
- **`LoadpullResult`** / `GridPointResult` / `PinStepResult` — capture-everything result types.
- **`LoadpullAnalysisParams`** — resolved directive parameters (from `LoadpullAnalysis`).

## Architecture

The loadpull engine orchestrates HB single-point solves via `HbEngine.RunSinglePoint`.
It does NOT modify the HB inner Newton solve.

### The `Tuner` component
`TunerModel` (in `src/Core/Devices/`) is a composite linear component with two declared
nets + two internally-allocated nodes:

```
Nodes: [0]=declared_net_0  [1]=declared_net_1
       [2]=__tuner_<inst>_block  (DC-block ↔ Z_Port junction)
       [3]=__tuner_<inst>_bias   (choke ↔ bias supply junction)
```

Internal nodes are minted by the Elaborator at elaboration time (via `NodeMap.GetOrAssign`
with collision-proof `__tuner_<inst>_*` names). The `__` prefix is reserved; user nets must
never use it.

**LoadTuner topology** (Nodes[0]=n_dut, Nodes[1]=n_ref):
```
n_dut --[C=1F]-- n_block --[Z_Port per-harmonic]-- n_ref
n_dut --[L=1H]-- n_bias --[V=Vbias@DC]------------ n_ref
```

**SourceTuner topology** (Nodes[0]=n_outer, Nodes[1]=n_dut):
```
n_outer --[V_1Tone drive]-- gnd
n_outer --[Z_Port per-harmonic]-- n_block --[C=1F]-- n_dut
n_dut   --[L=1H]-- n_bias --[V=Vbias@DC]------------ gnd
```

C = 1 F (ideal: open at DC, short at RF), L = 1 H (ideal: short at DC, open at RF).
Matches the Hero-2 explicit bias-tee topology exactly.

### Role assignment
Roles (Load / Source) are assigned by the `LoadpullEngine` before HB runs, via `SetRole()`.
In S-param mode (no tone set), both roles present Z[1] flat over all frequencies.

### InductanceRegularization=Always
Every loadpull HB solve uses `InductanceRegularization=Always` because the Tuner's ideal
choke always creates the voltage-pinned DC interface (linear-engine §4.3.1). This skips the
speculative fail-then-retry of `IfNecessary` on each of hundreds of inner solves — a real
per-point speedup (loadpull.md §2.1).

`HbLinearExtractor.ApplyInductanceReg()` was updated to regularize both `InductorModel`
branches AND `TunerModel.ChokeBranchIndex` (the TunerModel's internal choke, which is NOT
an `InductorModel`). Both get `R_reg = InductanceRegR` added to the DC branch diagonal.

`HbLinearExtractor.IsVoltageOrToneSource()` was updated to include `TunerModel`, so that
in the zeroDrive=true path (Y_NN extraction), the TunerModel is stamped via `ZeroDriveMna`
— zeroing the V_1Tone drive and bias supply values while leaving the impedance topology intact.

### Power measurement sign convention (verified against Hero-2 golden data)
```
Pout          = −½·Re(V[load_dut_idx, k=1] · conj(I_nl[load_dut_idx, k=1]))
Pin_delivered = +½·Re(V[src_dut_idx,  k=1] · conj(I_nl[src_dut_idx,  k=1]))
```
The sign asymmetry arises from the SDD's I_nl accumulation convention: I_nl[drain] is
positive (SDD injects into n_drain) — the outgoing power formula thus needs a sign flip.
I_nl[gate] is also positive but represents absorbed power (sign matches Pin_delivered > 0).

### Γ-grid warm-start
Between grid points: the engine picks the nearest already-converged neighbor using the
bilinear (Möbius) VSWR distance: VSWR(Γ_a, Γ_b) = (1+|Δ|)/(1−|Δ|) where
Δ = (Γ_a−Γ_b)/(1−Γ_a·Γ_b*). VSWR → 1 as Γ_a → Γ_b.

## .cnl parsing notes
The Tuner line parser (`ParseTunerLine` in CnlReader) scans for Z[k]= / G[k]= boundaries
using bracket-depth-zero scanning (same pattern as ZPort, SDD). Simple params (Zdefault,
BiasTee, Vbias, Z0) may appear ANYWHERE in the line — before OR after harmonic entries.
The parser collects them from:
1. The net-section prefix (before the first harmonic header)
2. The trailing text after each harmonic value (after whitespace truncation)

**Important**: the Tuner line `Tuner:Load n_drain 0  Z[1]=80+j*10  Z[2]=1  Zdefault=1e-6
BiasTee=on  Vbias=Vdd` puts BiasTee/Vbias after Z[2]'s value. This is parsed correctly
by collecting trailing tokens from the Z[2] expression region.

## Phase 4b-1 deliverable — COMPLETE (2026-06-03)

- **`TunerModel`** — LoadTuner + SourceTuner stamping, role assignment, harmonic override,
  swept-harmonic override, ChokeBranchIndex and BiasSupplyBranchIndex for regularization
  and Pdc readback.
- **`GamReader`** — forgiving `.gam` parser (mag_ang, re_im, re+j*imag; gamma/impedance;
  optional header; comment/blank skipping).
- **`LoadpullEngine`** + **`LoadpullResult`** — 2-D sweep, adaptive Pin drive-up, compression
  stop (P-xdB + overshoot), VSWR-nearest warm-start, InductanceRegularization=Always.
- **`HbEngine.RunSinglePoint`** — single-point HB solve entry point for the Loadpull engine.
- **`HarmonicBalanceAnalysis.MaxIterExpr`** and **`LoadpullAnalysis.MaxIterExpr`** — explicit
  user-settable MaxIter (default 100) on both directive types.
- **`AnalysisSettings.HbMaxIter`** default changed to 100 (was 50).
- **Hero 3 golden** — self-generated regression (`testdata/Hero3/hero3_self_FOMs.csv`, `_V.csv`,
  `_INl.csv`). SELF-GENERATED, NOT INDEPENDENTLY VALIDATED. Owner to verify before freezing.
- All 225 tests pass (Phases 1–4a + Phase 4b-1).

### Known limits (for 4b-2)
- Pout_at_compression (exact P-xdB interpolation) is deferred to post-processor.
- Efficiency/PAE (Pdc = Vdc · Idc) requires bias-supply branch current readback, deferred to 4b-2.
  The bias V/I approximate values (from interface V and I_nl at DC) are captured in
  `PinStepResult.BiasVoltageLoadV` / `BiasCurrentLoadA` etc.
- MXP/MXE/auto-Zsource/frequency loop search layer — Phase 4b-2.
