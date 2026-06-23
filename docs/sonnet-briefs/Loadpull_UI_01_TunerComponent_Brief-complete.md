---
name: project-brief-loadpull-ui-01-tuner-component
description: Loadpull UI 01 — general Tuner library component (palette/glyph/extraction); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 01 — general `Tuner` component (UI/palette/extraction) — COMPLETE 2026-06-23

Added `SymbolKind.Tuner` end-to-end through the UI layer; the engine `TunerModel` + factory
(`"Tuner"`) already existed. **Why:** lets a user draw the programmable RF termination
(`loadpull.md` §1) on a schematic. Source/Load variants are brief 02.

**Edits (all `src/Ui/Schematic` unless noted):**
- `SchematicModel.cs`: `Tuner` appended to `SymbolKind`.
- `ComponentTypeRegistry.cs`: registry entry (DisplayName/prefix `"Tuner"`, `Terminals` +
  `ExtraCategories:[Sources]`, `IsCommon`); `EngineReference(Tuner)="Tuner"`; `DefaultParameters` →
  `Z[1]=50Ω(shown)`, `Zdefault=1e-6Ω`, `Z0=50Ω`, `BiasTee=off`, `Vbias=0V` (last four hidden);
  `TryParseCode("TUNER")`; `UserParamTemplate` → `Z[{0}]` `FirstAddIndex:2` (Z[1] IS the first index,
  no skip).
- `EditableSchematic.cs` `SymbolPortDefs.For`: `Tuner` → single pin `("1",-300,0)` (LEFT).
- `BuiltInSymbols.cs`: `_tuner` + `BuildTuner()` (compact 300×200 RRect + small circle + slug) +
  dispatch case.
- `NetExtractor.cs` `EmitInstance`: `Tuner` branch BEFORE generic loop — emits 2 nets
  `[pinNet, "0"]` (LOAD-STYLE: pin→Nodes0 DUT-facing, ground hard-coded→Nodes1). TODO marker for
  brief 02 (LoadTuner/SourceTuner; SourceTuner uses source-style ordering). `LoadTuner` enum does NOT
  exist yet — branch is `is SymbolKind.Tuner` only.
- `docs/design/loadpull.md` §1.2: documented single-pin + implicit-ground reference; 2nd pin deferred.

**Net ordering gotcha:** general Tuner = LOAD-STYLE, so it is electrically a LoadTuner and must be
named `LoadTuner=` in a Loadpull analysis (or run in S-param where role defaults to Load).

**BiasTee string-param flow:** the elaborator has NO `ResolveTunerParameters`; `BiasTee=off` would
crash generic `Eval`. It works because `CnlReader.ParseTunerSimpleParams` **quotes** BiasTee
(`"off"`) on read → `Value.String`. So extraction emits `BiasTee=off` verbatim; the real run path
(extract → CnlWriter → CnlReader → Elaborate) re-quotes it. Test 4 round-trips through CNL.

**Gate:** 4 tests in `tests/Ui.Tests/TunerExtractionTests.cs` (reference+2-net load order;
default params unit-normalized Ω→Ohm; user Z[2] round-trip; CNL round-trip + elaborate). Build 0W/0E;
Ui 1416 / Core 376 / Engine 429(+1 skip) / Firewall 4 — all green.
