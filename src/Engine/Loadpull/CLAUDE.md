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

## Phase 4b code review — COMPLETE (2026-06-03)

### B1 — Zin sign fix (LoadpullPursuitEngine.ComputeZsource)
Old: `Complex iSrc = -iNlSrc;`  → Re(Zin) < 0 (non-physical).
New: `Complex iSrc = iNlSrc;`   → Re(Zin) > 0 (derived from INl convention + KCL).

### B2 — Compression overshoot: exact +0.1 dB
Old: continued to next full PinStep (1 dB) after compression detected.
New: exits inner loop at compression and runs exactly one extra solve at `lastDbm + 0.1 dB`.
Tightly brackets P-xdB for the downstream interpolator.

### B3 — MXP criterion in dBm; clean ExtractCriterion interpolation
Old: returned PoutW (Watts) — gradient surface order-of-magnitude inconsistent across terminations.
     Bracket search found first-gain-drop (wrong), fraction formula missing xdB (wrong).
New: returns Pout at P-xdB in dBm. Bracket = last-2 steps (below) and last step (above, +0.1 dB
     overshoot from B2). Interpolation fraction = (xdB - gainBelow)/(gainAbove - gainBelow).

### B4 — WToDb renamed to RatioToDb
Applies only to dimensionless power ratios (gain). Never to absolute power (those use WattsToDbm).

### B5 — Metric mismatch fixed: search now works in Γ-plane
Old: FitLinearPlane used Euclidean Z offsets (Ω); StepAlongDirection used VSWR — inconsistent.
New: PursuitEngine works entirely in Γ (normalised to Z0=50Ω). Gradient and step both use the
     same Euclidean-Γ metric. Convert Z↔Γ at boundaries via RfHelpers.Z2G/G2Z.
     VswrToDeltaGamma: dG=(vswr-1)/(vswr+1) — exact at Γ=0, ≤5% error for |Γ|<0.5.
     Polynomial refinement uses ONLY the 4 cardinal neighbours (not history), preventing
     large-offset history points from corrupting the local quadratic fit.

### B6 — Mirror-neighbour fix (PursuitEngine)
Old: `n1Z = TangentNeighbours(startZ, Dn).Item1 * new Complex(-1, 0)` — negated Z (negative-R probe).
New: `n1Z = 2 * startZ - n1Z` — proper mirror through startZ in Γ-plane.

### B7 — Dead gamma code removed; redundant gamma parameter removed
Old: LoadpullPursuitEngine.Query had two dead lines computing gamma (identity expression, then
     correct but hardcoded-50 formula) and passed gamma to RunOneTermination redundantly.
New: RunOneTermination computes gamma internally from Z and the grid's Z0 (not hardcoded).
     The gamma parameter is removed from RunOneTermination's signature.

### B8 — Goldens regenerated
Hero 3 and Hero 3B goldens regenerated with corrected code. Key numbers (owner to verify):
  Hero 3B pursuit: MXP Pout = 40.39 dBm at Z≈65Ω, MXE DE = 59.6% at Z≈68Ω.
  Zsource: Re(Zin) > 0 confirmed by diagnostic (was −50Ω before B1 fix, now +50Ω).
  Pedro VSWR (MXP↔MXE) ≈ 1.05 — inherent property of this SDD model (not a search artifact).
  NOT INDEPENDENTLY VALIDATED — owner to hand-check before freezing.

## Phase 4b-2 deliverable — COMPLETE (2026-06-03)

### New files
- **`PursuitEngine`** — Baylis steepest-ascent search (loadpull_pursuit.md §1).
  Distance = `RfHelpers.VswrFromZ` throughout. Internal rep = Z (Ω). Tangent-plane fit →
  ascent with Ds-shrink-to-1/3 → 2nd-order polynomial refinement. Returns unscorable list.
- **`LoadpullPursuitEngine`** — MXP-then-MXE orchestration (§4):
  - `Query(Z)` = one `RunOneTermination` call, VSWR-dedup'd cache.
  - Pedro seed: MXE starts at highest-efficiency cached point from MXP's search.
  - Auto-Zsource (§6): `Zin = V[src,k=1]/(-INl[src,k=1])` at OBO level (linear interp
    between bracketing Pin steps); `Zsource = conj(Zin)`.
  - `Resolve(LoadpullPursuitAnalysis, globals)` → `PursuitParams`.
- **`GamWriter`** — focused+broad .gam builder (§5): VSWR-circle box extents from
  `z_center ± z_radius` (Z domain, §5.1); non-convergent exclusion with warning.
  `WriteFile` emits `# impedance Z0=... re+j*imag` header readable by `GamReader`.
- **`LoadpullEngine.PrepareContext`** / **`RunOneTermination`** — extracted from `Run()` so
  the pursuit can issue single-termination queries without full-grid overhead.

### Design layer changes
- **`LoadpullPursuitAnalysis`** (`src/Core/Design/Analysis.cs`) — new third analysis type.
- **`CnlReader`** — `TryParseLoadpullPursuitDirective` (dispatched before `loadpull`).

### Efficiency (added to 4b-1 LP engine)
- **`PinStepResult.PdcW / De / Pae`** — computed at construction from bias V/I fields.
  Formula: `Pdc = BiasVoltageLoadV·(-BiasCurrentLoadA) + BiasVoltageSrcV·(-BiasCurrentSrcA)`.
  KCL-exact for ideal choke/cap (V(n_dut)=Vbias, I_supply = INl[node,0]).

### Hero 3B golden
`testdata/Hero3B/hero3B_at_compression.cnl` — `type=loadpull_pursuit`, PinMax=30 dBm.
`testdata/Hero3B/hero3B_self_pursuit.csv` — SELF-GENERATED, NOT INDEPENDENTLY VALIDATED.
`testdata/Hero3B/loadpull_pursuit_output.gam` — recommended-terminations grid.

### Test counts
245 tests pass (158 Core + 87 Engine), including 3 Hero3B pursuit tests, 9 GamWriter tests,
7 PursuitEngine unit tests, 1 efficiency sanity test.

### Observed behavior on Hero 3B (synthetic SDD FET)
MXP ≈ 65 Ω real, Pout ≈ 37 dBm at PinMax=30 dBm, 3 dB compression.
MXE ≈ 68 Ω real, DE ≈ 44%.
Pedro VSWR (MXP↔MXE) ≈ 1.05 — this FET's MXP and MXE are unusually close.
The Pedro 2–2.5 VSWR coupling is empirical from real GaN PAs; synthetic SDDs may differ.
Non-compression exit verified: PinMax=-18 aborts cleanly with an unscorable-start message.
