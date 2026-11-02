---
name: project-brief-loadpull-ui-05-loadpull-authoring
description: Loadpull UI 05 — Loadpull authorable in Add/Edit Analysis (LpBodyViewModel + view + editor wiring); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 05 — Loadpull authoring in Edit Analysis — COMPLETE 2026-06-23

Made **Loadpull** a first-class authorable analysis type in the Add/Edit Analysis dialog, producing a
`LoadpullAnalysis`. Modeled on the HB body. Depends on
[[project-brief-loadpull-ui-04b-tone-unit-var-wins]] (ToneUnit) + [[project-brief-loadpull-ui-04-lp-lpp-serialization]].

**New files:**
- `src/Ui/ViewModels/LpBodyViewModel.cs` — `[ObservableProperty]` fields per directive key; tuner
  pickers (`TunerInstanceNames` from components whose `Symbol is Tuner/SourceTuner/LoadTuner`;
  `HasNoTuners` hint); **Tone = ToneCoeff + ToneUnit** pair (mirrors HB: `FreqUnits`, `ComputeFreqPreview`,
  `OnToneUnitChanged` rescale); `≈` previews; Sweep(Load/Source) + GainType(Gt/Gp) toggle commands;
  `ApplyPickedGridPath` (SnpPathPolicy relative-store); `IsValid` (LoadTuner+SourceTuner+Grid+tone+PinMax
  non-blank); `BuildAnalysis(name,enabled)` → `LoadpullAnalysis`; `FromAnalysis` (Split tone nicety when
  unit=="Hz").
- `src/Ui/Views/Analyses/LpBodyView.axaml(.cs)` — Basic group (tuner combos, tone coeff+unit, Grid +
  **Browse…** `.gam` picker, Pin start/max/step, Compression) + Advanced Expander (Sweep, GainType,
  TuneHarm, MaxHarm, Tickle, MaxIter, FFT, Tol, DriveStepping, GuardHarmonic). Browse handler opens
  `StorageProvider.OpenFilePickerAsync` → `vm.ApplyPickedGridPath`.

**Edits:**
- `AnalysisEditorViewModel`: `public LpBodyViewModel LpBody` constructed in BOTH ctors (Add + all Edit
  arms); new edit `case LoadpullAnalysis lp` → `FromAnalysis`, `_type=LP`. `BuildAnalyses` LP arm =
  `LpBody.IsValid ? LpBody.BuildAnalysis(...) : null`. **LP/LPP ignore sweep chains in v1** — `return
  [inner]` regardless of SweepAxes. New `ShowSweeps => !IsLp && !IsLpp` (hides the sweep Expander).
- `AnalysisRowViewModel`: `TypeLabel` += `LoadpullAnalysis => "LP"`; `ComputeSummary` += `FormatLpSummary`
  (`"Loadpull · Load/Source · N dB, grid <file>"`).
- `AnalysisEditorDialog.axaml(.cs)`: enabled the **Load Pull** radio (removed disabled/"coming soon");
  added `LpBodyPanel` + `LpBodyViewControl`; wired DataContext, radio seed, `OnTypeRadioChanged` LP case,
  `UpdateBodyPanels` LP visibility; sweep Expander `IsVisible="{Binding ShowSweeps}"`.

**Tone is a coeff+unit pair, never combined** — keeps a unitless VAR tone resolving correctly (04b).
LPP authoring stays disabled (brief 06).

**Gate:** 9 tests in `tests/Ui.Tests/LpAuthoringTests.cs` (tuner picker populate/empty; build-from-form;
VAR tone coeff+unit pair; edit round-trip incl. non-Hz tone + Gp/Source toggles; blank LoadTuner/Grid →
null; LP ignores sweep axes; authored LP survives `.csch` round-trip). Build 0W/0E; Core 376 / Ui
1438(+9) / Engine 440(+1 skip) / Firewall 4 — all green.
