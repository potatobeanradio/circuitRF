---
name: project-brief-loadpull-ui-04b-tone-unit-var-wins
description: Loadpull UI 04b — loadpull/pursuit tone resolves to Hz via HB's var-unit-wins rule (ToneUnit); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 04b — tone-unit parity with HB (var-unit-wins) — COMPLETE 2026-06-23

Brought the **Loadpull** and **Loadpull-Pursuit** tone resolution to exact parity with HB's
var-unit-wins rule (`FreqUnit.ResolveHz`), so a VAR tone with or without a unit resolves correctly
(a unitless `RFfreq=2` + `ToneUnit=GHz` → 2e9 Hz, not 2 Hz). Prereq for Track B briefs 04–06.

**Directive-resolution layer only — numeric core (2-D sweep, HB solves, pursuit search) untouched.**

**Edits:**
- `src/Core/Design/Analysis.cs`: added `ToneUnit { get; init; } = "Hz"` to **both** `LoadpullAnalysis`
  and `LoadpullPursuitAnalysis` (next to `ToneExpr`; default "Hz" preserves back-compat).
- `src/Engine/Loadpull/LoadpullEngine.cs` `Resolve`: added `IReadOnlyCollection<string>?
  globalsWithUnit = null` param; replaced `double tone = Num(lpa.ToneExpr, 1e9)` with
  `FreqUnit.ResolveHz(lpa.ToneExpr, lpa.ToneUnit, globals, globalsWithUnit)` (try/catch→1e9). Other
  fields stay on `Num()` (only tone is frequency-unit-sensitive).
- `src/Engine/Loadpull/LoadpullPursuitEngine.cs` `Resolve`: same `globalsWithUnit` param + same tone
  change (its resolved `tone` feeds the `LoadpullAnalysisParams` it builds).
- `src/Ui/Schematic/SchematicRunService.cs`: both call sites now pass `nl.GlobalsWithExplicitUnit`
  (same source HB's caller uses). Default-null param keeps engine-test call sites compiling (they use
  Hz literals → unchanged).
- `CnlReader`: both loadpull + pursuit directive parsers read `ToneUnit = kv.GetValueOrDefault(
  "ToneUnit","Hz")` — mirrors HB (separate `Tone=`/`ToneUnit=` keys, quote-aware tokenizer).
- `CnlWriter`: both `FormatLoadpull*` emit `Tone="<expr>" ToneUnit=<unit>` (mirrors HB writer).

**Format note:** like HB, the unit is a **separate `ToneUnit=` key**, not a trailing token after
`Tone=` (e.g. `Tone="2" ToneUnit=GHz`). No CLI/engine-internal Resolve call sites exist beyond
SchematicRunService (pursuit builds `LoadpullAnalysisParams` directly, not via LoadpullEngine.Resolve).

**Gate:** 11 tests in `tests/Engine.Tests/Loadpull/LoadpullToneUnitTests.cs` — 4 per Resolve method
(unitless-VAR+GHz glitch case; unit'd-VAR var-wins no-double-scale; literal+GHz; Hz back-compat) + 3
reader/writer round-trips. LoadpullEngine.Resolve tests reuse the real `testdata/Hero3/hero3_load.gam`
(Resolve requires a Grid file); pursuit needs none. Build 0W/0E; Core 376 / Ui 1423 / Engine 440(+1
skip) / Firewall 4 — all green. **Downstream:** brief 04 DTO `LpToneUnit`; briefs 05/06 tone field =
coefficient+unit pair (mirror HbBodyViewModel).
