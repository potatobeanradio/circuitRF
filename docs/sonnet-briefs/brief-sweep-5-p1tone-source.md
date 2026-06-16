# Sonnet Brief — Sweep Fix 5/5: P1Tone — available-power RF source with harmonic terminations

**Goal.** Add **P1Tone**: a single-tone RF power source specified by **available power** (`Pavl`, dBm) behind an
**internal reference impedance** (`Z`, default 50 Ω), with optional **per-harmonic-band terminations**
(`Z[1]`, `Z[2]`, …, like the Tuner). This is the canonical sweepable RF drive ("sweep `Pavl` −20→+10 dBm") and
lets users control the harmonic impedance the source presents. (A plain V-source + external impedance + voltage
sweep still works; P1Tone is the power-domain convenience.)

Mirrors the component pattern (model + `ComponentModelFactory` + `ComponentTypeRegistry` + symbol). The model is
closely modeled on **`TunerModel`** (SourceTuner role), minus the bias-tee/role machinery.

## Available-power formula (get this right)
Thévenin `Vs` behind series `Z` delivers available power `Pavl` into a conjugate match. Using `Re(Z)`:
```
Pavl_W = 10^((Pavl_dBm − 30)/10)
|Vs|   = sqrt(8 · Re(Z1_eff) · Pavl_W)
```
where **`Z1_eff` is the fundamental-band impedance actually presented at f0** (the §"harmonic" rule below → band
1), so available power and the presented source impedance stay consistent — identical to
`TunerModel.SetSourceDrive` (`|Vs| = sqrt(8·Pavl·Re(Z1_eff))`). Unit test: `Pavl=0 dBm, Z=50` → 1 mW into a
matched 50 Ω load; `Pavl=10 dBm` → 10 mW.

## Harmonic terminations — READ THE DESIGN DOC
Full rule + stamping contract + worked examples + design defense:
**`docs/design/p1tone-harmonic-terminations.md`** (read it before coding). Summary:

- User declares optional `Z[0]` (DC), `Z[1]` (fundamental, default = `Z`), `Z[2]`, `Z[3]`, … and `Zdefault`
  (catch-all, default = `Z`). `G[k]` spelling → `Z[k]=Z0(1+Γ)/(1−Γ)` at construction (copy the Tuner's
  conversion). Each is an **expression** (sweepable), resolved per point — reuse the Tuner's
  `ResolveTunerParameters`/`CreateTunerModel` `Z[k]`/`G[k]` parsing pattern in
  `ComponentModelFactory`/`Elaborator`.
- **Band-assignment rule (LOCKED):** every excited spectral line at signed freq `f` is presented `Z[n]` where
  `n = roundHalfUp(|f| / f_c)`, with `n==0 → Z[0]/Zdefault`, undeclared/over-top band → `Zdefault`. `f_c` is the
  band-center fundamental: **single-tone `f_c=f0`; two-tone `f_c=(f1+f2)/2`**. This makes IM3/IM5/IM7 sit in
  band 1 (→ Z[1]) and 2nd-harmonic-zone products in band 2 (→ Z[2]), with the crossover at exactly `1.5·f_c` —
  precisely the behavior the owner asked for. Single-tone reduces to `n=k` (Tuner-identical). Negative-ω reps:
  evaluate `Z(|f|)`; the engine's `ExtractMix` already conjugates.

## Implementation — dedicated `P1ToneModel` (approach B is REQUIRED here)
Harmonic terminations need a **per-ω** `GetZ(omega)` (a flat series resistor can't present different Z per
harmonic), so the "macro = ToneSource + one series R" approach does **not** work once `Z[k]` exist. Build a
dedicated `P1ToneModel : ComponentModel` patterned on `TunerModel`'s SourceTuner stamp:

- **2 terminals** (`Nodes[0]` = external/DUT-facing, `Nodes[1]` = reference, typically ground) + **1 minted
  internal node** `__p1tone_<inst>_drv` between the drive source and the series Z (like the Tuner mints
  `__tuner_..._block`). Add the minted node in the elaborator exactly as the Tuner case does
  (`netlist.Nodes.GetOrAssign($"__p1tone_{childPath}_drv")`, appended to `resolvedNodes`).
- **Stamp(mna, c, omega)** (copy `TunerModel.StampSource` shape, drop bias-tee/choke):
  1. **Drive branch** (Group-2 V-source) between the internal drive node and `Nodes[1]`, active only at the
     fundamental (`|ω − 2π·Freq| < OmegaTol`), value `Complex(_vsMagnitude, 0)`, else 0 — copy the SourceTuner
     `V_1Tone` drive stamping (`AddBranch`/`AddBranchCurrent`/`AddConstraint`/`AddSourceValue`).
  2. **Series Z** (Group-2 `StampZPort`, copy verbatim from `TunerModel`) between `Nodes[0]` and the internal
     drive node, with `Z = GetZ(omega)`.
- **`GetZ(omega)`** = the §band rule: `f=omega/2π`, `n=roundHalfUp(|f|/_fc)`, return `Z[n]` (override-aware hook
  optional/none for now) else `Zdefault`, `n==0 → Z[0]/Zdefault`. Use `Math.Floor(x+0.5)` for round-half-up
  (documented tie-break). Copy `TunerModel.GetDeclaredZ`.
- **`_fc` injection:** the model needs the band-center. Add `SetToneContext(double fc, double driveFreqHz)`
  called at HB setup. The HB engine knows the tones: single-tone `fc=f0`; two-tone `fc=(f1+f2)/2`. Wire it where
  the engine prepares source models for the run (mirror how `TunerModel.SetTone/SetSourceDrive` are called by
  the LoadpullEngine — find the equivalent setup hook in `HbEngine.Run`/`RunTwoTone`; if none exists for tone
  sources, add a small "configure sources with tone context" pass before the sweep/extract). For S-param mode
  (no tone), present `Z[1]` flat (like the Tuner's `_toneFreqHz<=0` branch).
- **`|Vs|` / Pavl re-derivation under sweep:** `P1ToneModel` recomputes `_vsMagnitude =
  sqrt(8·Re(GetZ(2π·Freq))·Pavl_W)` whenever globals change. Implement `ReevaluateFromGlobals(globals)` (re-eval
  the `Pavl`/`Z[k]` expressions against the new globals, then recompute `_vsMagnitude`) **and** call it during
  the parametric-sweep re-elaboration path. NOTE: with the Brief-3 consolidation, `ParametricSweepEngine`
  **re-elaborates** per point, so `Pavl` is re-resolved fresh each point automatically — confirm the freshly
  elaborated `P1ToneModel` recomputes `_vsMagnitude` in its constructor/`SetToneContext`. (Keep
  `ReevaluateFromGlobals` for any in-engine re-eval path that still exists.)

## Parameters (`ComponentTypeRegistry.DefaultParameters`)
`P1Tone`: `Pavl` (dBm, default `0`, shown), `Z` (Ω, default `50`, shown), `Freq` (GHz, default `1`, shown),
`Phase` (deg, default `0`). Harmonic terminations `Z[1]`/`Z[2]`/… and `Zdefault`/`Z[0]` are **optional** — not in
the default template (the user adds them via the parameter editor, exactly like the Tuner's `Z[k]`). `|Vs|` is
derived, never a user param.

## Registration
- `SymbolKind.P1Tone` (add to enum). `ComponentTypeRegistry`: DisplayName `"P1Tone"`, InstancePrefix `"P"`,
  Category `Sources`, SearchTerms `["P1Tone","power","Pavl","available power","RF source","drive","harmonic"]`,
  `IsCommon: true`; `EngineReference(P1Tone)=>"P1Tone"`; `TryParseCode "P1TONE"`; `SymbolPortDefs.For(P1Tone)` = 2
  terminals.
- `ComponentModelFactory`: add `P1Tone` to `_parameterizedTypes`; `CreateP1ToneModel(parameters)` parsing
  `Pavl`/`Z`/`Freq`/`Phase` + `Z[k]`/`G[k]`/`Zdefault` (reuse the Tuner's `RxTunerZ`/`RxTunerG` regex + Γ→Z
  conversion). `Elaborator`: `ResolveP1ToneParameters` (store `Z[k]`/`Zdefault` exprs as strings + inject
  referenced scope vars, mirroring `ResolveZPortParameters`/the Tuner path) and mint the internal drive node.
- Symbol: source-style glyph (circle + sine/arrow, small "Z"); follow the ToneSource/Term symbol drawing.

## Tests (`tests/Engine.Tests` + factory/elaboration)
1. **P1Tone_AvailablePower:** `Pavl=0,Z=50,Freq=1GHz` into matched 50 Ω → 1 mW; `Pavl=10` → 10 mW.
2. **P1Tone_SweepPavl:** `ParametricSweepAnalysis` over `Pavl` (−20→10) wrapping HB → delivered power tracks
   `10^((Pavl−30)/10)` (validates re-derivation under the consolidated sweep path).
3. **P1Tone_InternalZ:** S11 into a P1Tone (drive nulled) ≈ Γ→0 for `Z=50`.
4. **P1Tone_HarmonicBands_SingleTone:** declare `Z[1]=50, Z[2]=10`; single-tone HB at f0 → the line at 2f0 is
   stamped `Z[2]` (probe via the presented impedance / a current ratio), f0 sees `Z[1]`, 3f0 (undeclared) sees
   `Zdefault`.
5. **P1Tone_HarmonicBands_TwoTone (the headline rule):** f1=1.00, f2=1.02 GHz, `Z[1]=50, Z[2]=10`. Assert the
   band assignment from the design doc's table: `(2,−1)`/`(3,−2)` (IM3/IM5, ≈0.98/0.96 GHz) → **Z[1]**;
   `(1,1)`/`(2,0)`/`(3,−1)` (≈2.02/2.00/1.98 GHz) → **Z[2]**; `(1,−1)`=Δ → `Z[0]/Zdefault`. Test `GetZ`/the
   band-map function directly (pull `n = roundHalfUp(|f|/fc)` into a tiny pure static so it's unit-testable
   without a full solve).
6. **P1Tone_Crossover:** a high-order 2nd-harmonic-family product whose frequency dips below `1.5·f_c` flips from
   Z[2] to Z[1] (assert the `n` flip at the boundary).
7. **P1Tone_Elaborates:** placing one P1Tone yields one `P1ToneModel` + one `__`-prefixed internal node; no stray
   user-namespace nodes.

## Gate
Build 0W/0E; tests green. Manual: place a P1Tone, set `Pavl` + a couple of `Z[k]`, drive a DUT, run HB wrapped in
a `Pavl` parametric sweep → power-vs-Pavl plots sensibly; declaring `Z[2]` changes the 2nd-harmonic loading; a
two-tone run terminates IM products per the documented band rule. V-source + external R path still works.

## On completion
Note in `src/Engine/CLAUDE.md` + `src/Ui/CLAUDE.md`: P1Tone is an available-power RF source (`Pavl` dBm behind
internal `Z`) with optional per-harmonic-band terminations `Z[k]`; band assignment is by nearest-band frequency
rule `n=round(|f|/f_c)` (`f_c=f0` single-tone, `(f1+f2)/2` two-tone) per
`docs/design/p1tone-harmonic-terminations.md`; realized as a dedicated `P1ToneModel` (Tuner-patterned: drive +
series `GetZ(ω)` Z-element + minted internal node); `|Vs|=sqrt(8·Re(Z1_eff)·Pavl_W)` keeps Pavl/impedance
consistent and re-derives under parametric `Pavl` sweeps. Completes the parameter-sweep upgrade series.
