# Loadpull Analysis — UI/UX Implementation Plan (overview)

**Status:** Plan for review · **Date:** 2026-06-23 (rev 2 — Tuner symbols: single pin, hard-coded reference)
**Reads with:** `docs/design/loadpull.md` (the engine + `Tuner` this exposes), `docs/design/loadpull_pursuit.md`
(the pursuit search this exposes), `docs/design/analysis-authoring.md` (the Add/Edit Analysis surface this
extends — loadpull/pursuit authoring is the DEFERRED item in §0/§8), `docs/skills/adding-a-library-component.md`
and `docs/sonnet-briefs/palette-contributor-guide.md` (how to add the Tuner component).

## What this is

**The loadpull engine and the loadpull_pursuit search are already built and tested** (see
`src/Engine/Loadpull/CLAUDE.md` — Phase 4b-1 + 4b-2 complete, 259 tests green). What is missing is the
**authoring UI**: there is no way to draw a Tuner on a schematic, and the Add/Edit Analysis dialog shows
Loadpull / Loadpull-Pursuit as "coming soon" placeholders. This plan closes that gap — UI/UX only, no
engine changes.

Two independent workstreams, delivered as a series of small briefs:

1. **The `Tuner` component** (briefs 01–03) — a new built-in library component so a user can draw the
   programmable termination on a schematic. Three palette tiles (general / Source / Load) that all emit the
   **same** engine component; they differ only in glyph, instance-name prefix, and single-pin net ordering.
2. **Loadpull & Loadpull-Pursuit authoring** (briefs 04–07) — enable the two deferred analysis types in the
   Edit Analysis dialog, with progressive-disclosure forms and tuner-instance pickers, plus serialization
   and `.cnl` round-trip wiring.

## The architectural facts that drive Track A (Tuner)

A **`Tuner` is role-neutral hardware.** The engine's `TunerModel` declares `PortCount => 1` and connects
**two nets** (`Nodes[0]`, `Nodes[1]`), interpreted **by role** at run time (the role is assigned by the
`Loadpull`/`loadpull_pursuit` analysis via `LoadTuner=` / `SourceTuner=` + `SetRole()`, not by the symbol):
- **Load role:** `Nodes[0]` = DUT-facing, `Nodes[1]` = reference (ground).
- **Source role:** `Nodes[0]` = internal RF source node (the embedded `V_1Tone` drives it **against
  ground**, so it can never be ground), `Nodes[1]` = DUT-facing.

The engine reference string is `"Tuner"` — `ComponentModelFactory` already registers it
(`_parameterizedTypes` + `CreateTunerModel`). So all three GUI tiles emit `EngineReference("Tuner")` with
identical parameters.

**GUI decisions (owner-confirmed, rev 2):**
- **Single pin = the DUT-facing net.** No second pin. The reference/other net is bound at extraction:
  - **Tuner + LoadTuner (load-style):** pin → `Nodes[0]`; `Nodes[1]` hard-coded ground `"0"`. Pin on the
    **left**.
  - **SourceTuner (source-style):** pin → `Nodes[1]`; `Nodes[0]` = an **auto-generated unique internal
    source net** (NOT ground — it carries the internal drive). Pin on the **right**.
- **Compact general Tuner glyph: 300 × 200**, minimal interior mark (advanced users want a small footprint).
  Source/Load are **wider** (~400 × 200) and more illustrative (Source borrows the P1Tone source-drive
  motif; Load is passive).
- **Exposing the reference/source net as a pin is DEFERRED** — documented in code + `loadpull.md`; a second
  pin can be added later if users need a non-ground reference (e.g. differential terminations) or to wire a
  source's outer net.

**Equivalence (the §3 deliverable), restated precisely:** the three tiles are the same engine component
(same `"Tuner"` reference, same parameters, same `UserParamTemplate`). They differ in (a) glyph, (b)
instance prefix, and (c) single-pin net ordering (load-style vs source-style). Because the net ordering
encodes the intended role, **a SourceTuner symbol must be named `SourceTuner=` in the analysis, and a
Tuner/LoadTuner symbol named `LoadTuner=`.** The general Tuner is electrically identical to the LoadTuner.

## Brief sequence

| # | File | Scope | Depends on |
|---|------|-------|------------|
| 01 | `Loadpull_UI_01_TunerComponent_Brief.md` | General `Tuner`: `SymbolKind.Tuner`, registry entry, **single left pin**, **compact 300×200** glyph, default params (`Z[1]`/`Zdefault`/`BiasTee`/`Vbias`/`Z0`), `Z[k]` "+" template, code-parse. Extraction emits `[pinNet, "0"]` (reference hard-coded ground; second pin deferred). Extraction test. | — |
| 02 | `Loadpull_UI_02_SourceLoadTuner_Brief.md` | `SymbolKind.SourceTuner` (pin **right**, drive motif) + `SymbolKind.LoadTuner` (pin **left**, passive), ~400-wide glyphs. Same engine component. Load emits `[pinNet,"0"]`; Source emits `[uniqueInternalNet, pinNet]`. Equivalence + role-agreement documented. | 01 |
| 03 | `Loadpull_UI_03_TunerPolish_Brief.md` | Polish: the "render the bias supply" display toggle (`ShowBias`, display-only, filtered from extraction), label clearance, **deferred-reference-pin docs**, Γ-vs-Z entry note, palette + full test checklist. | 01, 02 |
| 04 | `Loadpull_UI_04_LpLppSerialization_Brief.md` | Extend `AnalysisSerialization` with `"lp"`/`"lpp"` discriminators so the existing `LoadpullAnalysis`/`LoadpullPursuitAnalysis` round-trip through `.csch` / clipboard / `.canl`. Headless, testable, no UI. | — |
| 05 | `Loadpull_UI_05_LoadpullAuthoring_Brief.md` | `LpBodyViewModel` + the Loadpull editor form (tuner pickers, `.gam` grid browse, Sweep/TuneHarm/compression/Pin fields), wired into `AnalysisEditorViewModel.BuildAnalyses`; list badge "LP" + summary. | 04 |
| 06 | `Loadpull_UI_06_PursuitAuthoring_Brief.md` | `LppBodyViewModel` + the Loadpull-Pursuit editor form (all LP keys except Grid, plus the §3 pursuit keys), badge "LPP" + summary. | 04, 05 |
| 07 | `Loadpull_UI_07_ExtractionAndRun_Brief.md` | Carry LP/LPP analyses into the extracted `TestBench`; confirm `CnlWriter`/`CnlReader` round-trip the directives; Run executes an authored loadpull end-to-end on Hero 3. | 05, 06 |

Briefs 01–03 (Tuner) and 04–07 (authoring) are independent and may be done in parallel by two Sonnet
sessions. Within each track the order matters.

## Guardrails (apply to every brief)

- **UI firewall:** `src/Core`, `src/Engine`, `src/Cli`, `RfCore` reference no Avalonia. All view code stays
  in `src/Ui`. The model types (`LoadpullAnalysis`, `TunerModel`) are already framework-free — do not move
  UI concerns into them.
- **Do not touch the engine.** `TunerModel`, `LoadpullEngine`, `PursuitEngine`, `ComponentModelFactory`,
  `GamReader`/`GamWriter`, and the analysis model classes are done and tested. This work is purely the UI
  surface that produces those model objects and that draws the Tuner. The one exception is serialization
  DTOs (brief 04), which live in `src/Ui/Schematic/AnalysisSerialization.cs` — still UI-side.
- **`TreatWarningsAsErrors=true`** everywhere. Zero new warnings. Capture nullable properties into locals
  before passing to non-null parameters.
- **Grep an existing analog** rather than reasoning about which files a `SymbolKind` touches. For the Tuner,
  grep `SymbolKind.Term` (a box-framed terminal), `SymbolKind.Tline` (a single-case horizontal device with
  an implicit/ground reference), and `SymbolKind.P1Tone` (the `Z[k]`-bearing source whose drive motif you
  borrow). For the analysis forms, the HB body (`HbBodyViewModel` + its view) is the template.
- **Build + test after each brief:** `dotnet build` (zero warnings), `dotnet test` (all green), and the
  firewall assembly-reference check must pass.
