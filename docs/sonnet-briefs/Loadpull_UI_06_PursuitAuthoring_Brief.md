# Brief — Loadpull UI 06: enable Loadpull-Pursuit authoring in Edit Analysis

**Goal:** Make the **Loadpull-Pursuit** type authorable, producing a `LoadpullPursuitAnalysis` (already
exists and runs — Phase 4b-2). Add an `LppBodyViewModel` + view section, wire `AnalysisKind.LPP` into
`BuildAnalyses`, enable the disabled "Loadpull-Pursuit" type-picker entry, and add the "LPP" badge + summary.

**Depends on:** brief 04b (tone var-unit-wins + `ToneUnit` on the model), brief 04 (LPP serialization), and
brief 05 (`LppBodyViewModel` reuses the LP body's tuner-picker + shared-field machinery — including the
**Tone coefficient + unit** field — to avoid duplicating the ~16 shared fields).

**Reads with:** `docs/design/loadpull_pursuit.md` §3 (the pursuit directive keys), §0.1 / §6.5 (the optional
follow-on), `analysis-authoring.md` §4.2, and brief 05's LP body as the template.

## What's already scaffolded
`AnalysisEditorViewModel` has `AnalysisKind.LPP`, `IsLpp`, `NextFreeName(... LPP ...) => "LPP1"`. Missing:
`LppBodyViewModel`, the `BuildAnalyses` arm, the view section, the enabled picker entry, the edit `case`,
and the row badge/summary.

## 1 — `LppBodyViewModel` (`src/Ui/ViewModels/LppBodyViewModel.cs`)

`loadpull_pursuit.md` §3: **all `loadpull` keys EXCEPT `Grid`**, plus pursuit keys. **Reuse, don't
duplicate** the LP shared fields:
- **(preferred) Composition:** factor the LP body's shared fields (LoadTuner, SourceTuner, **Tone coeff +
  unit**, Pin start/max/step, Compression, Sweep, TuneHarm, GainType, MaxHarm, Tickle, MaxIter,
  FFT/Tol/DriveStepping/GuardHarmonic) into a small shared core during brief 05 and embed it in both LP and
  LPP. If too invasive, fall back to (b).
- **(b) Parallel fields:** copy the shared `[ObservableProperty]` fields and add the pursuit fields.

Either way, **the Tone is a coefficient + unit pair** (mirror `HbBodyViewModel`/the LP body — `ToneCoeff` +
`ToneUnit` + `FreqUnitHelper` + `ComputeFreqPreview`), NOT a single combined string (var-unit-wins,
brief 04b). The LPP body has **no Grid field**.

Pursuit fields (defaults from `LoadpullPursuitAnalysis`):
- **EffType** — `EffTypeExpr`, `DE`/`PAE` toggle (default `DE`). §2.
- **Zsource backoff (dB)** — `ZsourceOBOExpr` (default `5`). §6.
- **Search method** — `SearchMethodExpr`, ComboBox `["SteepestAscent","IteratedQuadratic"]` (default
  `SteepestAscent`). §1.1.2.

Output / follow-on group (Expander):
- **Create follow-on loadpull** — `CreateLoadpullResultExpr`, checkbox `true`/`false` (default `true`). §6.5.2.
- **Follow-on source match** — `LoadpullResultZsourceExpr`, ComboBox `["MXE","MXP","None"]` (default `MXE`);
  disable/grey when Create is off.
- **Output .gam grid (optional)** — `OutputGridPath` (nullable; blank = no file). Text + "Save as…" `*.gam`
  Browse; blank valid. §5 / §3.

Grid-builder group (Advanced Expander, §5):
- **VSWR1** `Vswr1Expr` (1.5); **VSWR1 resolution** `Vswr1ResolutionExpr` (4); **VSWR2** `Vswr2Expr` (3);
  **VSWR2 resolution** `Vswr2ResolutionExpr` (4); **Keep non-converging** `KeepNonconvergingExpr`
  checkbox (false); **Non-convergent exclusion VSWR** `NonconvergentVswrExpr` (1.05).

Plus the shared advanced LP fields (MaxHarm, Tickle, MaxIter, FFT, Tol, DriveStepping, GuardHarmonic, Sweep,
TuneHarm, GainType).

`BuildAnalysis(name, enabled)` → `LoadpullPursuitAnalysis` with `ToneExpr = ToneCoeff`, `ToneUnit =
ToneUnit`, all shared LP fields, and the pursuit keys; `OutputGridPath = string.IsNullOrWhiteSpace(...) ?
null : ...Trim()`. Add `FromAnalysis(LoadpullPursuitAnalysis lpp, ...)` mirroring the LP body (incl. the
`ToneCoeff`/`ToneUnit` split). **Validation:** LoadTuner, SourceTuner, Tone, PinMax required (Grid NOT
required for LPP).

## 2 — Wire into `AnalysisEditorViewModel`
- `public LppBodyViewModel LppBody { get; }`, constructed in both constructors (Edit: new `case
  LoadpullPursuitAnalysis lpp:` → `LppBodyViewModel.FromAnalysis(lpp, model)`, `_type = LPP`).
- `BuildAnalyses` arm: `AnalysisKind.LPP => LppBody.BuildAnalysis(name, Enabled),`. **No sweep chains** on
  LPP for v1 — return `[inner]`.

## 3 — Editor view (AXAML)
Add an LPP section bound to `IsLpp`: the shared LP fields (tuner pickers, **Tone coeff + unit**, Pin fields,
Compression, shared Advanced expander) **minus the Grid row**, plus the pursuit group (EffType, Zsource
backoff, SearchMethod), the follow-on group (Create checkbox, source-match combo, optional OutputGrid
browse), and the grid-builder advanced group. Add `≈` previews on numeric fields. **Enable the
"Loadpull-Pursuit"** type-picker entry; remove any remaining "coming soon" affordance (per
`analysis-authoring.md` §8 the deferred item is now done — update that doc's status line + TODO note).

## 4 — Row badge + summary (`AnalysisRowViewModel.cs`)
- `TypeLabel`: `LoadpullPursuitAnalysis => "LPP",`.
- `ComputeSummary`: `LoadpullPursuitAnalysis lpp => FormatLppSummary(lpp),` → one plain line, e.g.
  `"Pursuit · SteepestAscent · DE, +loadpull"`.

## 5 — Docs
- Update `docs/design/analysis-authoring.md`: §0 decision, §8 open-items, and the §4.2 "coming soon" note
  now read **done**; add a line that LP and LPP authoring shipped (briefs 05/06), with the v1 limitation
  that LP/LPP can't be wrapped in a parametric sweep yet, and that the tone resolves with the HB
  var-unit-wins rule (brief 04b).

## 6 — Tests
- Editor unit test: build an LPP via the editor VM; assert the `LoadpullPursuitAnalysis` has the set fields
  incl. pursuit keys and `ToneExpr`+`ToneUnit`; `OutputGridPath` blank → null.
- Tone-unit: Tone coeff `"RFfreq"` + unit `"GHz"` → `ToneExpr="RFfreq"`, `ToneUnit="GHz"`.
- Edit round-trip from an existing `LoadpullPursuitAnalysis` (incl. non-`"Hz"` tone unit).
- Follow-on combo gating: source-match combo disabled when Create off.
- Persistence smoke (brief 04): add LPP, save+reload `.csch`, survives.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green.
2. Launch: Add Analysis → **Loadpull-Pursuit** enabled; form shows shared LP fields (Tone coeff + unit, no
   Grid) + the pursuit/follow-on/grid-builder groups. OK adds an "LPP" card; double-click reopens populated;
   save/reload keeps it. No "coming soon" placeholders remain.
3. Firewall passes.
