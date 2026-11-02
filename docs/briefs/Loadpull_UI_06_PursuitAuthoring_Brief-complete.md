---
name: project-brief-loadpull-ui-06-pursuit-authoring
description: Loadpull UI 06 — Loadpull-Pursuit authorable in Add/Edit Analysis (LppBodyViewModel + view + wiring); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 06 — Loadpull-Pursuit authoring — COMPLETE 2026-06-23

Made **Loadpull-Pursuit** authorable in the Add/Edit Analysis dialog, producing a
`LoadpullPursuitAnalysis`. Builds on [[project-brief-loadpull-ui-05-loadpull-authoring]] (LP body),
[[project-brief-loadpull-ui-04b-tone-unit-var-wins]], [[project-brief-loadpull-ui-04-lp-lpp-serialization]].

**Approach:** brief's sanctioned fallback (b) — **parallel fields**, not a shared-core refactor (refactoring
the shipped `LpBodyViewModel` would risk its 9 gate tests).

**New files:**
- `src/Ui/ViewModels/LppBodyViewModel.cs` — all LP shared fields **minus Grid**; Tone = ToneCoeff+ToneUnit
  pair (var-unit-wins); pursuit fields (EffType DE/PAE toggle, ZsourceOBO, SearchMethod combo); follow-on
  group (`CreateLoadpullResult` bool + `FollowOnEnabled` gate, `LoadpullResultZsource` MXE/MXP/None combo,
  `OutputGridPath` nullable + Save-as); grid-builder group (Vswr1/2 + resolutions, `KeepNonconverging` bool,
  NonconvergentVswr). `IsValid` = LoadTuner+SourceTuner+Tone+PinMax (**Grid NOT required**). `BuildAnalysis`
  maps bools→"true"/"false", `OutputGridPath` blank→null. `FromAnalysis` parses bools + Split tone nicety.
- `src/Ui/Views/Analyses/LppBodyView.axaml(.cs)` — shared LP layout minus Grid + pursuit/follow-on/grid-
  builder Expanders. Save-as handler → `SaveFilePickerAsync` → `vm.ApplyPickedOutputGridPath`.

**Edits:**
- `AnalysisEditorViewModel`: `public LppBodyViewModel LppBody` in both ctors + all edit arms; new edit
  `case LoadpullPursuitAnalysis lpp` (before the `LoadpullAnalysis` case — distinct types, order safe);
  `BuildAnalyses` arm `AnalysisKind.LPP => LppBody.IsValid ? ... : null`. LP/LPP already return `[inner]`
  (no sweep chains) + `ShowSweeps` hides the sweep UI.
- `AnalysisRowViewModel`: `TypeLabel` += `LoadpullPursuitAnalysis => "LPP"` (before LP); `ComputeSummary`
  += `FormatLppSummary` (`"Pursuit · <method> · <eff>[, +loadpull]"`).
- `AnalysisEditorDialog.axaml(.cs)`: enabled **LP Pursuit** radio (removed "coming soon"); `LppBodyPanel`
  + `LppBodyViewControl`; wired DataContext/radio/`OnTypeRadioChanged`/`UpdateBodyPanels`.
- `docs/design/analysis-authoring.md`: §0 v1-types line, §4.2 picker line, §8 open-items → LP/LPP authoring
  DONE (briefs 05/06); recorded the v1 limitation (LP/LPP not yet wrappable in a parametric sweep) + tone
  var-unit-wins.

**Gate:** 9 tests in `tests/Ui.Tests/LppAuthoringTests.cs` (build-from-form incl. pursuit keys; blank/non-
blank OutputGridPath→null/value; VAR tone coeff+unit; follow-on combo gating when Create off; edit round-
trip incl. non-Hz tone + PAE/MXP/KeepNonconverging; blank LoadTuner→null; valid-without-Grid; `.csch`
round-trip). Build 0W/0E; Core 376 / Ui 1447(+9) / Engine 440(+1 skip) / Firewall 4 — all green.
**Loadpull UI series (briefs 01–06 + 04b) COMPLETE.**

## Follow-up bug fix — .gam picker path base (2026-06-23)

**Symptom:** authoring an LPP OutputGrid via the Save-as picker stored `./../results/lpp_test.gam`; at
run time the engine reported "Could not find a part of the path '…/claude/results/lpp_test.gam'" (one
directory level lost). **Root cause:** the LP `ApplyPickedGridPath` and LPP `ApplyPickedOutputGridPath`
stored the relative path against `_model.SchematicDirectory`, but the engine resolves the extracted
`netlist.cnl`'s relative paths against the **workspace root** (where `netlist.cnl` is written + read
back by `CnlReader.ReadFile` → `_sourceDirectory`). Base mismatch → wrong absolute path. The SnP `File`
picker (`ParameterEditorViewModel`) already uses `SnpPathPolicy.ToStored(path, SchematicViewModel.WorkspaceRoot)`.
**Fix:** threaded `string? workspaceRoot` into `LpBodyViewModel`/`LppBodyViewModel` ctors + their
`FromAnalysis`, and through `AnalysisEditorViewModel`'s two ctors (`_workspaceRoot`); `AnalysesListViewModel`
passes `_schematicVm.WorkspaceRoot` at both call sites. Both pickers now `ToStored(abs, _workspaceRoot)`.
Affected LP Grid too (same bug class) — fixed together. 3 regression tests in
`tests/Ui.Tests/LpGridPathPolicyTests.cs` (LPP + LP store relative to ws root and round-trip; null ws
root keeps absolute). Build 0W/0E; Ui 1450 / Engine 440(+1 skip) / Core 376 / Firewall 4 — all green.
