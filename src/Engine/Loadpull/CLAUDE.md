# Loadpull Engine — local conventions

Standing instructions for `src/Engine/Loadpull`. Read with root `CLAUDE.md` and
`src/Engine/CLAUDE.md`. Design note: `docs/design/loadpull.md`.

## Zin uses the source-delivered current, not INl[gate] (brief-loadpull-zin-passives, 2026-06-24)

**Bug fixed:** Zin / Zsource / Pin_delivered divided by `INl[src]` (the SDD's nonlinear gate
current). That equals the source-delivered current ONLY when the gate node carries nothing but the
source tuner + FET. With passives wired at the gate (input match, parasitics, a shunt) the source
also feeds them, so `INl[gate]` is just the FET's *intrinsic* gate impedance — e.g. a `I[1,0]=_v1/5000`
SDD reported Zin ≈ 5000 Ω no matter what else was on the node.

**Fix:** `LoadpullEngine.ComputeSourceInputCurrent` recovers the true current INTO the DUT input node
per harmonic: `ISrcIn[k] = I_srcZport[k] − I_choke[k]` — two **branch** currents of the SourceTuner
(its series Z_Port and its bias-tee choke), read from `HbEngine.RunSinglePoint`'s `HbLinearBackSolver`
(`x[branchIdx]`). By KCL at the gate this equals `INl[gate] + Σ I_passive`, and **reduces to
`INl[gate]` in the canonical case → Hero 3/3B goldens unchanged**.

Plumbing:
- `TunerModel.SourceZPortBranchIndex` (new) — captured in `StampSource`; `ChokeBranchIndex` already
  existed. Both are `-1` for the Load role / S-param mode → fallback to `INl[src]`.
- `HbEngine.SinglePointResult.BackSolver` (new, lazy; null on singular DC extraction).
- `PinStepResult.ISrcIn[k]` carries the spectrum; `ComputeFoms` uses `ISrcIn[1]` for Pin_delivered;
  `LoadpullPursuitEngine.ComputeZsource` uses it for Zsource.
- New **`Iin`** cube `{gridPoint, pinStep, harmonic}` in the result DataSet. The RfCore
  `LoadpullPostProcessor` prefers `Iin` over `INl[src]` for the summary-table Zin (`= V[src]/Iin`).
  Falls back to `INl[src]` when `Iin` is absent (e.g. imported `.spl`/`.lpcwave` data).

## IRL referenced to the source impedance, not 50 Ω (brief-loadpull-irl-source-ref, 2026-06-24)

Input return loss must be referenced to the impedance the **source tuner presents at the fundamental**
(declared `Z[1]`, or the pursuit `Zsource` the follow-on sets via `LoadpullResultZsource`), NOT a fixed
50 Ω. The old `Γin = (Zin−50)/(Zin+50)` mis-reported the match whenever the source ≠ 50 Ω — e.g. an
MXE-matched follow-on read a poor IRL even though the input was conjugate-matched.

- `TunerModel.FundamentalZ(toneFreqHz)` (new) = `GetZ(2π·f0)` (respects the harmonic-1 override).
- `GridPointResult.SourceZFund` (new) captured in `RunOneTermination` after the grid override applies.
- Engine emits **`__SrcZ`** `{gridPoint}` (Complex) — `__`-prefixed and hidden, deliberately NOT named
  `ZSource` (the `.spl`/`.lpcwave` importers already use rank-1 `{freq}` "ZSource"; `LoadpullSurface`
  assumes that shape — a same-name `{gridPoint}` cube crashes its slicer).
- `LoadpullPostProcessor` computes the **power-wave (Kurokawa) reflection** `Γs = (Zin − Zs*)/(Zin + Zs)`
  → `IRL_dB = 20·log10|Γs|`, passed to `LoadpullDerivedFields.Derive` as `reflDb`. `Γs → 0` at conjugate
  match (`Zin = Zs*`) → IRL → −∞, exactly what a matched source-pull expects. Absent `__SrcZ` → `reflDb`
  null → Derive's legacy 50 Ω `Γin` (so `Zs = 50` reduces to the old value; back-compatible).
- `Zin_real/imag` are reference-free (`V/Iin`) and unchanged — only the IRL reference moved.

Gate tests: `LoadpullPostProcessorTests.Enrich_IRL_ReferencedToSourceImpedance_NotFixed50` (conjugate
match → IRL < −100 dB; `Zs=50` → −12.74 dB) + `LoadpullZinPassivesTests` asserts `GridPointResult.SourceZFund`.
- The **DC bias readback is unchanged** (`-INl[node,0]`): at DC the gate passives carry no current
  (caps block; a cap-fed R path is DC-open), so the bias-supply current still equals `INl[gate,0]`.

Gate test: `tests/Engine.Tests/Loadpull/LoadpullZinPassivesTests.cs` — with a 200 Ω gate shunt,
`ISrcIn = INl[gate] + V/Rg` to ~1e-12 and Zin = 192.3 Ω (= 5000∥200), vs the old 5000 Ω; canonical
(no shunt) gives `ISrcIn == INl[gate]`.

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
Nodes: [0]=n_dut (DUT-facing)  [1]=n_ref (reference; ground "0" by default)
       [2]=__tuner_<inst>_block  (DC-block ↔ Z_Port junction)
       [3]=__tuner_<inst>_bias   (choke ↔ bias supply junction)
       [4]=__tuner_<inst>_outer  (SourceTuner RF-drive node; unused by LoadTuner)
```

**Both roles declare the same two nets `[DUT, ref]`** — symmetric net ordering across the
Tuner/LoadTuner/SourceTuner tiles. The internal nodes (block, bias, **and** the SourceTuner's
RF-drive `outer` node) are all minted by the Elaborator (via `NodeMap.GetOrAssign` with
collision-proof `__tuner_<inst>_*` names). The `__` prefix is reserved; user nets must never use
it. `outer` is minted for every Tuner (the role isn't known at elaboration time); the LoadTuner
role simply ignores it.

**LoadTuner topology** (n_dut=Nodes[0], n_ref=Nodes[1]):
```
n_dut --[C=1F]-- n_block --[Z_Port per-harmonic]-- n_ref
n_dut --[L=1H]-- n_bias --[V=Vbias@DC]------------ n_ref
```

**SourceTuner topology** (n_dut=Nodes[0], n_ref=Nodes[1], n_outer=Nodes[4] minted):
```
n_outer --[V_1Tone drive]-- n_ref
n_outer --[Z_Port per-harmonic]-- n_block --[C=1F]-- n_dut
n_dut   --[L=1H]-- n_bias --[V=Vbias@DC]------------ n_ref
```

The `LoadpullEngine` reads the DUT-facing node as `Nodes[0]` for **both** roles (the source's
drive node is the minted `outer`, never a declared net).

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

### B2 — Compression stop simplified (supersedes earlier +0.1 dB overshoot design)
Old (phase 4b-2 original): ran one extra solve at `lastDbm + 0.1 dB` off the regular grid;
     ExtractCriterion set xdB = comprAbove (overshoot compression level), making t ≈ 1.0 so
     it always returned the overshoot step's Pout — effectively Pout at ABOVE p.Compression.
New (final): drive stays on the regular Pin grid; stop when `compression >= p.Compression + 0.1`.
     The two bracketing steps are adjacent regular-grid steps straddling p.Compression.
     No off-grid solve; cleaner code, eliminates the non-convergence risk of the overshoot solve.

### B3 — MXP criterion in dBm; ExtractCriterion correctly interpolates to p.Compression
Old: returned PoutW (Watts) — gradient surface order-of-magnitude inconsistent across terminations.
     Bracket search found first-gain-drop (wrong), fraction formula missing xdB (wrong).
     After B2 original: xdB = comprAbove → t ≈ 1.0 → returned overshoot step Pout (still wrong).
New: signature `ExtractCriterion(gpr, usePae, mxe, xdB)` — xdB = lpp.Compression from caller.
     Bracket: below = last step with compr < xdB; above = first step with compr >= xdB.
     t = (xdB − comprBelow) / (comprAbove − comprBelow) — real fraction in [0,1].
     Interpolated Pout lies STRICTLY BETWEEN the two grid steps (regression-verified).
     ComputeZsource: replaced 0.5 dB proxy with same bracketing at lpp.Compression.

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

### B8 — Goldens regenerated (twice: after B3 original, again after B2+B3 final fix)
Hero 3 and Hero 3B goldens regenerated with corrected code. Key numbers (owner to verify):
  Hero 3B pursuit (B2+B3 final):
    MXP Pout = 37.64 dBm at Z≈65Ω — correctly interpolated to exactly P-3dB (not to overshoot step).
    MXE DE = 60.4% at Z≈85Ω.
    Pedro VSWR (MXP↔MXE) = 1.31 — MXP and MXE separated more than before; still inherent to this SDD model.
    Unscorable = 0 (was 2 with B2 original — those 2 had the off-grid overshoot solve fail to converge).
  Zsource: Re(Zin) > 0 confirmed by diagnostic (was −50Ω before B1 fix, now +50Ω).
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
SteepestAscent (default): MXP ≈ 80.5 Ω real, Pout ≈ 40.6 dBm at PinMax=30 dBm, 3 dB compression.
(Note: earlier CLAUDE.md entries showed MXP at 65 Ω; those reflected a pre-debug state.
 The golden CSV was regenerated after the B-series fixes; the current SA result is 80.5 Ω.)
MXE ≈ 140 Ω real, DE ≈ 69.6%.
Pedro VSWR (MXP↔MXE) ≈ 1.75 for this SDD model.
Non-compression exit verified: PinMax=-18 aborts cleanly with an unscorable-start message.

## Phase 4b-2 enhancement — IteratedQuadratic search method (2026-06-05)

### New: `SearchMethod` enum and `PursuitParams.SearchMethod` field
- `public enum SearchMethod { SteepestAscent, IteratedQuadratic }` — extensible, open to future methods.
- `LoadpullPursuitAnalysis.SearchMethodExpr` (default `"SteepestAscent"`) — parsed by `CnlReader`.
- `PursuitParams.SearchMethod` (default `SteepestAscent`) — threaded into both MXP and MXE engine instances.
- `PursuitEngine.Method` (init property, default `SteepestAscent`) — dispatches `Run` to either
  `RunSteepestAscent` (existing path, unchanged) or `RunIteratedQuadratic` (new).

### `RunIteratedQuadratic` — trust-region iterated quadratic
At each iterate: places 4 axis-aligned cardinal neighbours at R VSWR (exact `FindStepLength`),
fits a decoupled local quadratic per axis, and jumps toward the analytic optimum if the Hessian
is negative-definite and the optimum is within the trust region. Otherwise: gradient step.
Shrinks/grows R (VSWR) by the VSWR-excess rule; converges when R < ConvergenceThreshold.
Tracks and returns the best-scored point seen across all iterations (including cardinals).

**Implementation note — decoupled fit (`FitAxis1D`):**
The full 5-parameter `FitQuadraticSurface` cannot be used with axis-aligned cardinals: the ΔxΔy
cross-term column in AtA is identically zero, making `Solve5x5` return all-zeros (flat apparent
gradient). Solution: `FitAxis1D` fits each axis independently — (m1, m11) from Re-axis cardinals,
(m2, m22) from Im-axis cardinals, m12=0 (unobservable from axis-aligned probes). `SolveQuadraticOptimum`
receives m12=0, giving the correct decoupled optimum delta = (−m1/m11, −m2/m22).

### Cache: automatic via criterion delegate
IQ obtains every score through the `criterion` delegate (never calls `LoadpullEngine` directly),
so the VSWR-dedup cache in `LoadpullPursuitEngine` applies automatically to all cardinal queries.

### Cleanup: debug `Console.WriteLine` removed from `ExtractCriterion`
The two leftover `poutAboveDbm=…` and `effAbove=…` debug prints are removed. The intentional
`Console.Error.WriteLine` diagnostic logging is preserved.

### Hero 3B results (IteratedQuadratic, 2026-06-05)
- MXP: Z ≈ 77.6 Ω real, Pout ≈ 40.64 dBm.  VSWR from brute-force MXP (80 Ω) = 1.031. ✓
- MXE: Z ≈ 123 Ω real, DE ≈ 69.7%.
- Query count: IQ=39 vs SA=21 → ratio 1.86× (target ≤ 2×). ✓
- Brute-force-vs-pursuit VSWR = 1.031 < 1.20. ✓

### New tests (total now 257: 158 Core + 99 Engine)
- `PursuitEngineTests.IteratedQuadratic_FindsKnownOptimum_QuadraticCriterion` — IQ unit test.
- `Hero3BPursuitTests.Hero3BPursuit_BruteForceAgreement_IteratedQuadratic` — IQ brute-force gate.
- `Hero3BPursuit_IteratedQuadratic_ReachesOptimum` — IQ walk + query count vs SA report.

## Phase 4b-2 enhancement — `LoadpullPursuitResult` + optional follow-on `LoadpullResult` (2026-06-05)

Completes the "search → recommend → focused loadpull, unattended" headline workflow (loadpull_pursuit.md §0.1).

### `LoadpullPursuitResult` (replaces `PursuitRunResult`)
`PursuitRunResult` renamed → **`LoadpullPursuitResult`** and extended with three new fields:
- **`Params`** — the resolved `PursuitParams` (inputs: all directive settings; self-documenting).
- **`RecommendedTerminations`** — `GamWriter.GamBuilderResult` (always built in memory, §6.5.1).
  Previously only built when `OutputGrid` was set; now always computed and stored in the result.
  `OutputGrid` still controls only the `.gam` file write (`GamWriter.WriteFile`).
- **`LoadpullData`** — the follow-on `LoadpullResult` (§6.5.2), or **null** if
  `CreateLoadpullResult=false` or either optimum did not converge.

### New directive keys (`LoadpullPursuitAnalysis` + `CnlReader` + `PursuitParams`)
- **`CreateLoadpullResult`** (bool, default **on**) — whether to run the follow-on loadpull.
- **`LoadpullResultZsource`** (`MXE` default / `MXP` / `None`) — which Zsource the follow-on uses
  for the Source Tuner's fundamental impedance.

### `PursuitParams` extended
Gam-builder fields added (previously implicit via directive, now resolved into params):
`Vswr1`, `Vswr1Resolution`, `Vswr2`, `Vswr2Resolution`, `KeepNonconverging`, `NonconvergentVswr`,
`OutputGridPath`, `CreateLoadpullResult`, `LoadpullResultZsource`.

### Follow-on loadpull mechanics (`RunFollowOnLoadpull`)
1. Builds `GamReader.GamGrid` from `RecommendedTerminations.Points` (Z values; gamma via `Z2G`).
2. Optionally sets `Source Tuner.SetHarmonicOverride(1, zsource)` for MXE/MXP modes before
   calling `_lp.Run(followOnLpParams)`. Cleared in a `finally` block.
3. **`LoadpullResultZsource=None`** — no override; Source Tuner keeps its declared `Z[1]`.
4. **Drive-voltage and Pavl always in agreement:** `SetSourceDrive` calls `GetZ(omega0)` (not
   `GetDeclaredZ`) for the Pavl calibration, so `|Vs| = sqrt(8·Pavl·Re(Z1_eff))` where `Z1_eff`
   is the effective impedance — the override if set, else the declared value. `TunerModel.cs` fix
   applied 2026-06-05.

### Three orthogonal outputs (verified)
- **Recommended terminations** (in-memory `GamBuilderResult`): always built.
- **`.gam` file**: written **iff** `OutputGridPath` is non-null — controlled by `OutputGrid` directive.
- **Follow-on `LoadpullResult`**: run **iff** `CreateLoadpullResult=true` AND both MXP+MXE converged.

### New tests (total now 259: 158 Core + 101 Engine)
- `Hero3BPursuit_FollowOnLoadpullResult_WhenCreateOn_DataPresent` — `CreateLoadpullResult=true`:
  `LoadpullData` present, grid-point count == recommended-terminations count, role-agnostic type.
- `Hero3BPursuit_FollowOnLoadpullResult_WhenCreateOff_DataNull` — `CreateLoadpullResult=false`:
  `RecommendedTerminations` built, `LoadpullData=null`.
