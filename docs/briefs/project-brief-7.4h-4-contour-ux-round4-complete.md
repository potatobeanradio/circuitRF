# Phase 7.4h-4 — Contour UX Round 4 — COMPLETE
Date: 2026-06-20  
Tests before: 2086 | Tests after: 2092 (+6)

## Slices implemented

### Slice 4a — bug fixes (§A, §N, §O)
- **§A** `SkColorToAvaloniaColorConverter`: now returns `SolidColorBrush` instead of bare `Color`; `ConvertBack` handles both `SolidColorBrush` and `Color` inputs.
- **§N** `AxesRenderer.DrawComplexXLabels`: filters `nonContourTraces` before iterating; skips rendering entirely when only contour traces are present.
- **§O** `PlotInspectorViewModel.OnLibraryChanged`: contour traces now guarded with `if (t.IsContourTrace) return false` — they are never flagged stale.

### Slice 4b — card layout (§C, §G/§H, §I, §F)
- **§I** (prior session): range NUD column widths 48→36; N-levels NUD `Width="40"`.
- **§C** + **§G/§H**: Options `StackPanel` restructured into 8 rows:
  1. MXP/MXE marker buttons (unchanged)
  2. Fill ISB (None/TopoMap/HeatMap) — standalone row
  3. **Line row**: Lines toggle + color swatch + Fade toggle + stroke-width `Slider` (0.5–5, Width=60)
  4. **Grid row**: Show toggle + color swatch + size NUD (Width=34) — merged, no separate "Pt size" row
  5. **Label row**: Labels toggle + Bg swatch + Text swatch + font-size NUD (Width=36) + spacing NUD (Width=36) — all merged, no separate rows
  6. Kernel combo
  7. Smooth + ε
  8. Colormap combo — "deferred render" tooltip text removed
- **§F**: `ContourStrokeWidth` `[ObservableProperty]` added to `TraceRowViewModel`; `OnContourStrokeWidthChanged` sets `cd.StrokeWidth` + `_parent.Notify()`; constructor initializes from `cd.StrokeWidth`.

### Slice 4c — iso-line labels (§B)
- `DrawIsoLines` signature extended: `lineColorOverridden`, `strokeWidth`, `labelBg`, `labelFg`, `labelSpacing`, `colorMap`, `fadeLineOpacity`.
- Label bg/fg now respects `ContourData.LabelBackground` / `LabelForeground` (not hard-coded black).
- **Stagger**: `startFrac = 0.5 + 0.18 * ((ringIndex % 3) - 1)` clamped [0.15, 0.85].
- **Spacing walk**: accumulates arc length; places a label every `spacingPx = labelSpacing * 100` px (rather than one per polyline at midpoint).
- `strokeWidth` passed directly into `linePaint.StrokeWidth`.

### Slice 4d — markers + colormap coupling (§D, §E)
- **§D** `DrawOptimaMarkers` / `DrawOptimumMarker`: filled circle (r=7) + black ring (1.5 stroke) + luminance-based letter color ('P'/'E'). `MxpAccent` = `EnsureBright(Sample(map, 0.15))`, `MxeAccent` = `EnsureBright(Sample(map, 0.85))`.
- **§E** `LineColorOverridden` flag on `ContourData`; set `true` in `OnContourLineColorChanged`. When false, `DrawIsoLines` computes per-line colormap-contrast colors (`tPos` → `Sample` → luminance → hi-contrast mix). Fade alpha applies on top.
- `DataDisplayConfig.ContourTraceConfig`: `StrokeWidth`, `LineColorOverridden`, `LabelBackground` fields persisted; wired in `BuildTraceConfig` and config loading.

## New models/fields
| Location | Change |
|---|---|
| `ContourData` | + `LineColorOverridden` (bool) |
| `ContourTraceConfig` | + `StrokeWidth`, `LineColorOverridden`, `LabelBackground` |
| `TraceRowViewModel` | + `ContourStrokeWidth` observable + handler |
| `DataSourceLibraryViewModel` | + `internal FireLibraryChangedForTest()` |

## Gate tests (T25–T30 in ContourTraceCardTests.cs)
| # | Description |
|---|---|
| T25 | Converter returns `SolidColorBrush` with correct RGBA |
| T26 | Converter null input returns transparent `SolidColorBrush` |
| T27 | `OnLibraryChanged` with empty library preserves contour trace |
| T28 | `OnLibraryChanged` with empty library removes stale standard trace |
| T29 | `ContourLineColor` set → `LineColorOverridden = true` |
| T30 | `ContourStrokeWidth` propagates to `ContourData.StrokeWidth` |
