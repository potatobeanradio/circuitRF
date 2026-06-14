---
name: project-brief-7.1b-plotmodel-renderers
description: Brief 7.1b: splotRF plot model + Skia renderers ported, render-only PlotControl, demo Rect harness — completed 2026-06-14
metadata:
  type: project
---

Phase 7.1b — plot model + Skia renderer layer ported from splotRF into circuitRF.

**What was done:**
- `src/Ui/DataDisplay/Models/`: Misc.cs, Axes.cs, Marker.cs, Plot.cs, Trace.cs (namespace `CircuitRF.Ui.DataDisplay`, `SNP`-backed)
- `src/Ui/DataDisplay/Renderers/`: RenderTheme.cs, PlotRenderer.cs, AxesRenderer.cs, TraceRenderer_MarkerRenderer.cs, TableRenderer.cs
- `src/Ui/DataDisplay/Controls/PlotControl.cs`: render-only `Control` subclass with `DirectProperty<PlotControl, Plot?>` and `DirectProperty<PlotControl, RenderTheme>`, `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` pattern
- `DataDisplayViewModel`: `CurrentPlot`, `HasPlots`, `InsertDemoPlotCommand` (TEMP 7.1b)
- `DataDisplayView.axaml`: `PlotControl` + demo button (TEMP 7.1b)

**Key seam retargets:**
- Font: `SkiaFonts.Regular` → `SkiaFonts.PlexRegular`, `SkiaFonts.Bold` → `SkiaFonts.PlexBold` (IBM Plex)
- Color: `RenderTheme.Light`/`Dark` selected from `ActualThemeVariant` (TODO 7.x: wire to ColorTheme/.ccolor)
- Watermark: `"splotRF"` → `"circuitRF"`
- `AppSettings.GoldenAspectRatio` inlined as `1.618033988749895` in Plot.cs
- `ComplexStringHelper.Format` inlined as `FormatRI` in Marker.cs

**Gate:** Firewall 4/4 · Core 254/254 · Ui 721/721 · Engine 225/225 — all green.

**Why:** To port splotRF's battle-tested plot rendering layer into circuitRF without taking splotRF as a dependency.

**How to apply:** 7.1c will add pan/zoom + multi-plot containers; 7.2 will retarget Trace.Data from SNP to DataCube.
