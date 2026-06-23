---
name: project-brief-loadpull-ui-04-lp-lpp-serialization
description: Loadpull UI 04 — AnalysisSerialization round-trips LoadpullAnalysis + LoadpullPursuitAnalysis (lp/lpp DTO); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 04 — LP/LPP analysis serialization — COMPLETE 2026-06-23

Extended the single shared analysis encoder (`src/Ui/Schematic/AnalysisSerialization.cs`) to round-trip
`LoadpullAnalysis` (`"lp"`) and `LoadpullPursuitAnalysis` (`"lpp"`) across `.csch` / clipboard / `.canl`.
Headless, framework-free, no UI. Depends on [[project-brief-loadpull-ui-04b-tone-unit-var-wins]]
(`ToneUnit` field).

**Edits (all in `AnalysisSerialization.cs`):**
- **`CschAnalysis` DTO**: added 19 `Lp*` fields (tuner names, GridPath, ToneExpr/ToneUnit, Pin*, MaxHarm,
  Sweep, TuneHarm, Compression, GainType, Tickle, MaxIter, FftOverSample, Tol, DriveStepping,
  GuardHarmonic) + 12 `Lpp*` pursuit-only fields (EffType, ZsourceOBO, SearchMethod, OutputGridPath,
  Vswr1/2 + resolutions, KeepNonconverging, NonconvergentVswr, CreateLoadpullResult,
  LoadpullResultZsource). All nullable, `WhenWritingNull`.
- **`ToDto`**: `LoadpullAnalysis`→`"lp"`, `LoadpullPursuitAnalysis`→`"lpp"` cases before the `_=>`
  fallback. `LpToneUnit = lp.ToneUnit != "Hz" ? lp.ToneUnit : null` (omit default, mirrors HB).
- **`FromDto`**: `"lp"`/`"lpp"` arms before `_=>null`; every field `?? <Analysis.cs default>` so
  old/short files load (ToneUnit `?? "Hz"`; OutputGridPath stays nullable = no file).
- Header + Type-discriminator doc updated to include `"lp"/"lpp"`.

**Key facts:** Tone is a **coefficient+unit pair** (`LpToneExpr` + `LpToneUnit`), NOT a combined string —
this is what makes a unitless VAR tone resolve correctly via var-unit-wins (04b). `SourceDirectory`
(resolves relative Grid/OutputGrid paths) is set by the reader at run time — **not serialized**. LP
couples to dedicated `Lp*` keys, never HB's fields. `.csch` round-trip is automatic via
`SchematicPersistence.ToFileModel/FromFileModel` → these `ToDto`/`FromDto` (no extra `.csch` wiring).

**Gate:** 6 tests in `tests/Ui.Tests/AnalysisSerializationTests.cs` (LP all-fields incl. non-Hz ToneUnit;
LP default-ToneUnit omitted→re-reads Hz; LPP all-fields incl. null OutputGridPath; LPP non-null
OutputGridPath; mixed DC/SP/HB/LP/LPP via `.canl`; LP forward-compat absent-fields→model defaults).
Build 0W/0E; Core 376 / Ui 1429(+6) / Engine 440(+1 skip) / Firewall 4 — all green.
