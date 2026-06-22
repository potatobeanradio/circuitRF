---
name: project-brief-7.4h-7-contour-ux-round7
description: Phase 7.4h-7 complete: zoom-scaling for all contour elements, drag-clip artifact, Smith fill edge, line-color luminance ceiling, title invariant, input validation
metadata:
  type: project
---

Phase 7.4h round 7 — 5 slices, 7 items, completed 2026-06-21.

**Slice 7a (§2) — Uniform zoom scaling:**
- Added `BaseLw = 2.0f` constant in `ContourRenderer`
- `DrawOptimumMarker`: replaced `r=7f, sw=1.5f, fs=9f` with `r=3.5f*lw, sw=0.75f*lw, fs=4.5f*lw` where `lw = AxesRenderer.LineWidth(canvasSize)`
- `DrawOptimaMarkers`: replaced `zoomLevel` param with `canvasSize`
- `DrawIsoLines`: added `effectiveStroke = strokeWidth*(lw/BaseLw)` and `effectiveFontSize = levelFontSize*(lw/BaseLw)`; removed `zoomLevel` param
- `DrawGridPoints`: added `canvasSize` param; applies `pointRadius*(lw/BaseLw)`
- PlotRenderer call sites updated to pass `canvasSize` and drop `zoomLevel`

**Slice 7b (§7) — Drag-clip artifact:**
- `PlotDrawOperation.Render`: always `canvas.Clear(SKColors.Transparent)`, then wrap `PlotRenderer.Draw` in `Save/ClipRect/Restore`

**Slice 7c (§1) — Smith fill edge:**
- `ContourData`: added `FillGrid` property (disk-covering grid for Gamma plane)
- `TraceRowViewModel.RebuildContour`: computes `fillGrid = surface.Resample(fit, new ViewBox(-1.0, 1.0, -1.0, 1.0), 80)` for Gamma plane, null for Z plane
- `PlotRenderer.Draw` TopoMap pre-pass: uses `cd.FillGrid ?? cd.Grid`
- `ClearContourGrid`: also clears `FillGrid`

**Slice 7d (§3 + §6) — Line color + title invariant:**
- `DrawIsoLines`: replaced `lerpAmt` formula with luminance ceiling: if `lineL > 0.45f`, scale RGB down by `0.45f/lineL` (fixes Gray/Bone/Winter/GistHeat/Copper)
- `ContourData.TitleString()`: defense — if `MetricDisplayName(ConstraintMetricName) == displayName`, returns fallback instead of "X at Constant X"
- `TraceRowViewModel.RebuildConstraintMetricOptions`: uses `MetricAliasGroup` comparison (Gain/Gt/Gp, DE/PAE/Efficiency treated as same); always syncs `cd.ConstraintMetricName` when constraint collides with primary metric

**Slice 7e (§4/§5) — Input validation:**
- All contour edit boxes already use NumericUpDown; added `Minimum/Maximum` to `ContourConstraintValue`, `LevelStart`, `LevelStop`, `LevelStep`

**Tests:** 10 new gate tests (T18–T27); total 1375 Ui.Tests, 2125 total. 0 failures.

**Why:** Canvas already encodes zoom (Bounds = Width×Zoom), so constant canvas-px sizes looked wrong zoomed. The disk fill was smaller than the Smith disk. Light colormaps produced near-white iso-lines. The constant-metric could alias to the primary metric creating "X at Constant X" titles.

**How to apply:** Owner-verify zoom scaling (glyphs/labels/dots grow with grid lines), Smith fill edge (smooth to circular clip), drag no longer leaves residual text.
