---
name: project-brief-sdd-weighting-editor
description: SDD weighting editor (brief #4 — UI): inline CommitName validation + NameWatermark placeholder; 26 gate tests; completed 2026-06-19
metadata:
  type: project
---

SDD weighting editor (brief #4, Option A — minimal) — COMPLETE 2026-06-19.

Depends on brief #3 (parser) which made `I[p,w≥2]` + `H[w]=expr` netlist-real.

**Changes (UI only — no Core/Engine):**
- `ParameterRowViewModel.cs`: `TryValidateSddName(string, out string error)` — `internal static` pure validator; 4 static Regex fields duplicating ComponentModelFactory's private patterns (with comment pointing back). Validation runs in `CommitName` guarded by `_ownerSymbol is SymbolKind.Sdd or SymbolKind.FetSdd`. `NameWatermark` property emits `"I[p,w] · Q[p] · H[w]"` for SDD/FetSdd, `""` otherwise.
- `ParameterEditorView.axaml`: `PlaceholderText="{Binding NameWatermark}"` on the name TextBox.

**New test file:** `tests/Ui.Tests/SddEquationNameValidationTests.cs` — 26 tests (valid names, H[0]/H[1] built-in, malformed H, p=0 rejection, unknown heads, regression).

**Key design decisions:**
- Option A (minimal): no structured editor, no new dialog. Leans on fact that SDD name cells are already free-form editable (`NameEditable=true`).
- Watermark uses Avalonia `PlaceholderText` (not deprecated `Watermark`).
- `TryValidateSddName` is `internal` (not `private`) so tests can call it directly without Avalonia runtime.

**Why:** Malformed SDD names (`H[1]`, `I[1,`) committed silently and only failed at elaboration/run time. Option A surfaces errors at CommitName time.
**How to apply:** If Option B (dedicated SDD equation editor dialog) is ever built, `TryValidateSddName` is the existing validation surface — no duplication needed.

Related: [[project-brief-sdd-weighting-parser]] [[project-brief-sdd-weighting-engine]]
