# Loadpull Analysis — UI/UX Implementation Plan (overview)

**Status:** rev 4 — Track A (Tuner, briefs 01–03) **landed**; Track B (authoring, 04b/04/05/06/07) defined;
Track C (Data Display recognition of simulated LP results, 08–09) added 2026-06-23.
**Reads with:** `docs/design/loadpull.md`, `docs/design/loadpull_pursuit.md`,
`docs/design/analysis-authoring.md`, `docs/design/loadpull-contours.md`,
`docs/skills/adding-a-library-component.md`, `docs/sonnet-briefs/palette-contributor-guide.md`.

## What this is

The loadpull engine, the loadpull_pursuit search, AND the Data Display contour stack (Phase 7.4) are all
**already built and tested**. Missing is the **UI glue**: drawing a Tuner, authoring Loadpull /
Loadpull-Pursuit, and recognizing a **simulated** LP result as a loadpull source for contour viewing. Three
tracks:

1. **The `Tuner` component** (briefs 01–03) — **DONE.** Three tiles (general / Source / Load), one engine
   component, single pin, hard-coded reference (ground for Tuner/Load; auto internal net for Source).
2. **Loadpull & Loadpull-Pursuit authoring** (briefs 04b, 04, 05, 06, 07) — enable the two deferred analysis
   types, with progressive-disclosure forms + tuner pickers, serialization, **tone-unit parity with HB**,
   and `.cnl` round-trip / run wiring.
3. **Data Display recognition of simulated LP results** (briefs 08–09) — make a simulated LP `run.npy` build
   a `LoadpullSurface` for contour viewing, identical to an ingested `.spl`/`.lpcwave`.

## Track A facts (Tuner) — complete

Role-neutral hardware; `TunerModel` is `PortCount => 1` over two role-interpreted nets. GUI: single pin =
DUT-facing; reference hard-coded (`"0"` for Tuner/Load; unique internal net for Source); general glyph
300×200, Source/Load wider; second pin deferred. EngineReference `"Tuner"` for all three; SourceTuner named
`SourceTuner=`, Tuner/LoadTuner named `LoadTuner=`.

## Track B fact — tone units (var-unit-wins)

HB resolves its tone with `FreqUnit.ResolveHz(ToneExpr, ToneUnit, globals, globalsWithUnit)` so a VAR with
*or without* a unit works. The loadpull/pursuit engines did the tone with a plain evaluator (no field unit) —
a unitless VAR glitched to Hz. **Brief 04b** brings Loadpull to HB parity (model `ToneUnit` +
`FreqUnit.ResolveHz` in both `Resolve` methods + reader/writer); it is a prerequisite for 04/05/06. (As of
the latest engine read, `LoadpullEngine.Resolve` already routes the tone through `FreqUnit.ResolveHz` and
`SchematicRunService` passes `nl.GlobalsWithExplicitUnit` — so 04b may be partly/fully landed; verify the
model `ToneUnit` field + reader/writer unit-token before treating 04b as done.)

## Track C fact — simulated LP results need shape-based recognition

The Data Display's loadpull/contour eligibility is gated on `SourceKind.Spl`/`.Lpcwave`. A simulated LP
result loads as `SourceKind.Npy` (a grouped `run.npy`, LP cubes nested under the analysis-name group e.g.
`LP1`), so it is selectable but never treated as loadpull — no surface, no contour. The **data is already
correct**: `LoadpullEngine.BuildLoadpullDataSet` emits the canonical loadpull cubes/units, and the
`.spl`/`.lpcwave` readers were built to match it. The fix is **shape-based, group-aware recognition** (08)
plus **group-aware surface binding** (09). No engine/model/format change.

## Brief sequence

| # | File | Scope | Depends on | Status |
|---|------|-------|------------|--------|
| 01 | `Loadpull_UI_01_TunerComponent_Brief.md` | General `Tuner`: single left pin, 300×200 glyph, params, extraction `[pinNet,"0"]`. | — | **done** |
| 02 | `Loadpull_UI_02_SourceLoadTuner_Brief.md` | `SourceTuner`/`LoadTuner`; same engine component; Source emits `[uniqueNet, pinNet]`. | 01 | **done** |
| 03 | `Loadpull_UI_03_TunerPolish_Brief.md` | `ShowBias` toggle, label clearance, deferred-reference-pin docs, tests. | 01, 02 | **done** |
| 04b | `Loadpull_UI_04b_ToneUnitVarWinsParity_Brief.md` | Tone-unit parity with HB: `ToneUnit` on both LP models; `FreqUnit.ResolveHz` in resolves; reader/writer unit token. | — | verify |
| 04 | `Loadpull_UI_04_LpLppSerialization_Brief.md` | `"lp"`/`"lpp"` serialization (incl. `LpToneUnit`) for `.csch`/clipboard/`.canl`. | 04b | todo |
| 05 | `Loadpull_UI_05_LoadpullAuthoring_Brief.md` | `LpBodyViewModel` + Loadpull form (tuner pickers, Tone coeff+unit, `.gam` browse, …); "LP" badge. | 04b, 04 | todo |
| 06 | `Loadpull_UI_06_PursuitAuthoring_Brief.md` | `LppBodyViewModel` + Pursuit form (LP keys except Grid + pursuit keys); "LPP" badge. | 04b, 04, 05 | todo |
| 07 | `Loadpull_UI_07_ExtractionAndRun_Brief.md` | Carry LP/LPP into the `TestBench`; `CnlWriter`/`CnlReader` round-trip; run on Hero 3. | 04b, 05, 06 | partly landed (LP run works) |
| 08 | `Loadpull_UI_08_DataDisplayRecognition_Brief.md` | **Shape-based, group-aware loadpull recognition** (`LoadpullRecognition.FindLoadpullViews`) replacing the `SourceKind.Spl/.Lpcwave` gate; headless + testable. | — | todo |
| 09 | `Loadpull_UI_09_ContourBinding_Brief.md` | Group-aware `LoadpullSurface` construction + contour-card binding for a recognized LP `run.npy`; end-to-end render gate. | 08 | todo |

Tracks are independent. Track A is done. Track B order: 04b → 04 → 05/06 → 07. Track C order: 08 → 09. Track C
needs only that an LP run writes its `run.npy` (it does), so it can proceed in parallel with Track B.

## Guardrails (apply to every brief)

- **UI firewall:** `src/Core`, `src/Engine`, `src/Cli`, `RfCore` reference no Avalonia. Brief 04b touches
  Core/Engine only via `FreqUnit` (already a Core dependency). Track C's recognizer (`LoadpullRecognition`)
  and the surface/contour math stay framework-free; only binding VMs change.
- **Numeric core + result shape are off-limits.** The 2-D sweep, HB solves, pursuit search, and
  `BuildLoadpullDataSet` are done and tested. Track C is Data Display wiring only — do not "fix" the LP
  result shape; it already matches the `.spl` contract.
- **`TreatWarningsAsErrors=true`** everywhere; zero new warnings.
- **Grep an existing analog:** authoring forms → `HbBodyViewModel` + its view (the Tone coeff+unit pattern,
  `FreqUnitHelper`, `ComputeFreqPreview`). Track C recognition → grep `SourceKind.Spl`/`.Lpcwave` in
  `src/Ui/DataDisplay`; surface binding → grep `LoadpullSurface` / `RebuildContour` in `TraceRowViewModel`.
- **Build + test after each brief:** `dotnet build` (zero warnings), `dotnet test` (all green), firewall
  check passes.
