---
name: project-brief-loadpull-ui-02-source-load-tuner
description: Loadpull UI 02 — SourceTuner/LoadTuner variant tiles (same engine component, role-dependent net ordering); completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 02 — SourceTuner & LoadTuner variants — COMPLETE 2026-06-23

Added `SymbolKind.SourceTuner` + `SymbolKind.LoadTuner`. **Same engine component as the general
[[project-brief-loadpull-ui-01-tuner-component]]** (`EngineReference="Tuner"`, identical default
params + `UserParamTemplate`); differ only by glyph, instance prefix, and single-pin **net ordering**.

**Edits (all `src/Ui/Schematic` unless noted):**
- `SchematicModel.cs`: `SourceTuner`, `LoadTuner` appended to `SymbolKind` with XML-doc stating the
  role/ordering contract.
- `ComponentTypeRegistry.cs`: two registry entries (SourceTuner → Sources + extra Terminals;
  LoadTuner → Terminals; both `IsCommon`); `EngineReference` both → `"Tuner"`; `DefaultParameters`
  delegates `SourceTuner`/`LoadTuner` to the `Tuner` case (shared list); `UserParamTemplate` merged to
  `Tuner or SourceTuner or LoadTuner`; `TryParseCode` `SOURCETUNER`/`SRCTUNER`, `LOADTUNER`/`LDTUNER`.
- `EditableSchematic.cs` `SymbolPortDefs.For`: `LoadTuner` → pin `("1",-300,0)` (LEFT);
  `SourceTuner` → pin `("1",300,0)` (RIGHT).
- `BuiltInSymbols.cs`: `_sourceTuner`/`_loadTuner` + `BuildSourceTuner()` (400×200, P1Tone-style drive
  circle + sine + Γ mark + slug; right lead) + `BuildLoadTuner()` (400×200, passive: Γ mark + slug +
  termination zigzag; left lead) + dispatch cases.
- `NetExtractor.cs` `EmitInstance`: LOAD-STYLE branch now `Tuner or LoadTuner` → `[pinNet,"0"]`.
  New SOURCE-STYLE branch `SourceTuner` → `[sourceNet, pinNet]` where `sourceNet =
  UniqueInternalNetName("nsrc_<inst>", netNames)` (non-ground, non-"__", per-instance unique; new
  private helper appends `_2/_3…` if seed collides with a real net).
- `docs/design/loadpull.md` §1: documented the three-tile equivalence + role-match requirement.

**Key role/ordering nuance (document everywhere):** net ordering encodes the role, so a SourceTuner
symbol MUST be named `SourceTuner=` and a Tuner/LoadTuner symbol `LoadTuner=` in the Loadpull analysis.
Source-style: `Nodes[0]`=internal source net (where embedded `V_1Tone` drives vs ground, so NOT
ground), `Nodes[1]`=pin (DUT-facing). Load-style: `Nodes[0]`=pin, `Nodes[1]`="0".

**Gate:** 3 tests in `tests/Ui.Tests/SourceLoadTunerExtractionTests.cs` (LoadTuner ≡ general Tuner:
same Reference/nets/params; SourceTuner source-style ordering with unique non-ground Nodes[0];
two SourceTuners → distinct source nets). Build 0W/0E; Ui 1419 / Core 376 / Engine 429(+1 skip) /
Firewall 4 — all green.
