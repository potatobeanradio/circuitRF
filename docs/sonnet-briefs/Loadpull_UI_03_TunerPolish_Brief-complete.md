---
name: project-brief-loadpull-ui-03-tuner-polish
description: Loadpull UI 03 — Tuner polish: ShowBias display-only glyph variant, label clearance, deferral docs; completed 2026-06-23
metadata:
  type: project
---

# Loadpull UI 03 — Tuner polish (ShowBias glyph, labels, deferral docs) — COMPLETE 2026-06-23

Finished the Tuner family ([[project-brief-loadpull-ui-01-tuner-component]],
[[project-brief-loadpull-ui-02-source-load-tuner]]). No engine work.

**ShowBias display-only bias-tee glyph:**
- `ComponentTypeRegistry.DefaultParameters` (shared Tuner/Source/Load case): added hidden
  `ShowBias="false"` param. Also added a Γ-vs-Z entry comment (rename row to `G[k]` + set `Z0`).
- `BuiltInSymbols.cs`: `PrimitivesForTuner(SymbolKind kind, bool showBias)` — cached per `(kind,
  showBias)`; returns the base per-kind glyph, and when `showBias` appends a **shared** `BiasTeeAddOn()`
  (RF choke 2 coils + Vdc two-bar supply, drawn beneath the box from bottom-center down to y≈+236).
  Bias-tee hardware is identical across the three kinds.
- **Renamed the per-instance glyph carrier `SchematicComponent.SnpSymbol` → `InstanceSymbol`** (now used
  by both SnP and the Tuner family; runtime-only field, 4 sites: model field, renderer ×2, ToRenderComponent
  assignment). Renderer branch `c.InstanceSymbol is not null`.
- `EditableSchematic.cs`: `ToRenderComponent` + `ComputeGlyphBb` set/use `instanceSymbol` for SnP
  (existing) **and** tuner kinds via `PrimitivesForTuner(Symbol, GetBoolParam("ShowBias"))`. Renamed
  private `GetSnpBool` → `GetBoolParam` (generic bool-param reader, reused for ShowBias).
- `NetExtractor.EmitInstance`: `overrides2` filter now drops `not "CvData" and not "ShowBias"` — ShowBias
  never reaches the engine, so the extracted Instance is identical regardless of ShowBias.

**Label clearance:** `SchematicComponent.LabelBaseYFor` gained a Tuner/Source/Load branch
(`Math.Max(LabelBaseY, glyphHalfH + LabelWorldStep)`, like SDD/ZPort/SnP) so type/name labels sit below
the actual glyph extent — including the taller ShowBias variant. The renderer/FullBb already feed
`GlyphBbMaxY - Y` as `glyphHalfH`.

**Deferred-reference-pin docs:** strengthened comments at the `SymbolPortDefs.For` Tuner/Load/Source cases
and (from briefs 01/02) the `EmitInstance` branch + `loadpull.md` §1 — single pin = DUT-facing; reference
(ground) / internal source net bound at extraction, not a pin; a 2nd pin is a deferred enhancement.

**Gate:** 4 tests in `tests/Ui.Tests/TunerPolishTests.cs` (ShowBias not emitted; extraction identical
with/without ShowBias; ShowBias extends glyph downward; labels clear the bias glyph). Build 0W/0E; Ui 1423
/ Core 376 / Engine 429(+1 skip) / Firewall 4 — all green. Tuner UI series (briefs 01–03) COMPLETE.
