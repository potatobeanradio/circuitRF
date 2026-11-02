---
name: project-brief-7.4h-2-contour-plot-renderer
description: Phase 7.4h-2: composed Plot titles + axis labels + Trace.PathBoundingRect contour branch + MXP/MXE markers + grid-point dots + matplotlib colormaps; 17 gate tests; 2080 total — completed 2026-06-20
metadata:
  type: project
---

Phase 7.4h-2 contour plot renderer enhancements. Paired with 7.4h-1 (card/VM/model).

**Changes made:**
- `ContourData.cs`: added `MxpCoord`/`MxeCoord` (nullable Complex); added `TitleString()` helper + metric→unit/display-name maps; removed DEFERRED comment on ColorMap; added `using System.Numerics`
- `Plot.cs`: `Title` now composes from contour trace `TitleString()` (joined " / ") when `!CustomTitleOn`; `YLabel` returns "Imaginary (Ω)" for Rect contour when `!CustomYLabelOn`; `XLabel` returns "Real (Ω)" for Rect contour when `!CustomXLabelOn`
- `Trace.cs`: `PathBoundingRect()` gains contour branch — returns `SurfaceGrid.XSpace`/`YSpace` min/max extent when `IsContourTrace && Grid != null`
- `TraceRowViewModel.cs`: `RebuildContour()` computes `cd.MxpCoord`/`cd.MxeCoord` via `surface.MaxPower`/`MaxEfficiency`; `ClearContourGrid` nulls them
- New `ContourColormaps.cs`: piecewise-linear ramps for all 13 `ContourColorMap` values; `Sample(map, t)` returns opaque SKColor
- `ContourRenderer.cs`: `DrawTopoMapFill`/`DrawHeatMapFill` accept `ContourColorMap colorMap`; `BuildTopoPalette`+`HsvToRgb` replaced by `BuildPalette`; new `DrawGridPoints` and `DrawOptimaMarkers`/`DrawOptimumMarker` methods
- `PlotRenderer.cs`: passes `cd.ColorMap` to fill methods; calls `DrawGridPoints` and `DrawOptimaMarkers` in trace pass

**Why:** make contour plots self-describing (automatic titles, impedance axis labels), correctly autoscaled on Rect/Z-plane, and enriched with MXP/MXE markers + grid-point dots + real matplotlib colormaps.

**How to apply:** 17 new gate tests in `ContourPlotRendererTests.cs`; visual gates require app testing.
