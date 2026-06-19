---
name: project-brief-schematic-housecleaning
description: Schematic housecleaning (6 items): paste Num dedup, P1Tone S-param lint, Save As title, toolbar glyphs + Pin, ohm/ohms units, SNP label fix — completed 2026-06-19
metadata:
  type: project
---

6 independent items, completed 2026-06-19. Build 0W/0E; 1220 Ui.Tests, 318 Core.Tests pass.

**Item 1 (paste Num dedup):** `SchematicPasteCommand.ResolveNums` deduplicates `Num` parameter for Term/P1Tone on paste. 5 gate tests in `SchematicHousecleaningTests.cs`.

**Item 2 (P1Tone S-param lint):** `Elaborator.LintTopLevelTerms` extended to include `P1ToneModel` — P1Tone:Num=1 + Term:Num=2 no longer warns "port 1 missing". 4 gate tests in `P1ToneLintTests.cs`.

**Item 3 (Save As title):** `SchematicDocument.OnSavedAs(filePath, cellName)` (new); `SaveLooseSchematic` now targets active materialized doc when no dirty scratch exists. 2 gate tests.

**Item 4 (toolbar glyphs + Pin):** Wire → `<Path>` line glyph; Ground/Term → `<PaletteGlyphControl>`; new Pin button with `OnPlacePin` handler. 1 gate test.

**Item 5 (ohm/ohms units):** `Units._scales` gains `{ "ohm", 1.0 }` and `{ "ohms", 1.0 }`. 5 gate tests in `OhmLowercaseTests.cs`.

**Item 6 (SNP label position):** `LabelBaseYFor`/`LabelRowGeometry` gain optional `double? glyphHalfH` parameter; SNP branch uses real glyph extent when provided. 5 callsites updated. 5 gate tests.

**Pre-existing failure:** `Engine.Tests.Export.DataSetExportTests.Mat_IncludeLinearNetwork_WritesLinearNetworkGroup` was failing before these changes.

**How to apply:** If SNP label/hitbox is wrong for a specific SNP component, check that `ComputeGlyphBb()` is called and its `MaxY - Y` is passed to `LabelBaseYFor`.
