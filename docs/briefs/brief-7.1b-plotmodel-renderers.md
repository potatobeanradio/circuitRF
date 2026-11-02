# Sonnet Brief — Phase 7.1b: plot model + Skia renderers, one Rect trace rendering

**Design:** `docs/design/data-display.md` §3 → "7.1b", §2.6 (firewall), §4 (fonts). Read those. **Scope:
port splotRF's data-model + Skia renderer layer into circuitRF and render ONE Rect plot.** This is the
heaviest step — but it is a *faithful copy-port* (rename namespace, retarget the font + color seams, drop a
file), **not** a rewrite. **No interaction, no inspector, no multi-plot canvas, no tabs, no persistence, no
real data loading** — those are 7.1c/7.1d/7.1e/7.2.

## Goal
A single `PlotControl` hosted in `DataDisplayView` renders a **Rect** plot (axes + grid + an S21-in-dB
trace + labels) using **IBM Plex** fonts and circuitRF theming. Verified from a synthetic in-code Plot/Trace
(no file I/O yet).

## Verified substrate (consume)
- circuitRF's `SchematicCanvas` (`src/Ui/Controls/SchematicCanvas.cs`) is the **in-repo precedent** for the
  exact Avalonia-Skia bridge splotRF's `PlotControl` uses: `Control` + `DirectProperty<…>` model + a
  `Render(DrawingContext)` override that pushes an `ICustomDrawOperation`, leases the Skia canvas via
  `ISkiaSharpApiLease` (`Avalonia.Skia`), and calls a renderer. **Model the render-only `PlotControl` on
  `SchematicCanvas`'s draw-op boilerplate.** SkiaSharp + `Avalonia.Skia` are already dependencies.
- circuitRF's `SkiaFonts` (`src/Ui/Renderers/SkiaFonts.cs`) already exposes **IBM Plex** typefaces:
  `SkiaFonts.PlexRegular`, `SkiaFonts.PlexBold`, `PlexSemiBold`, `PlexItalic`, `PlexLight` (namespace
  `CircuitRF.Ui.Renderers`). This is the font-retarget target.
- `RfCore` (`SNP`, `TouchstoneIO`, `MatrixType`, etc.) is referenced; splotRF's models bind to `SNP`.
- `DataDisplayDocument` / `DataDisplayViewModel` / `DataDisplayView` exist from 7.1a (in
  `src/Ui/DataDisplay/` and `src/Ui/Views/DataDisplay/`).

## The port

### 1. Models → `src/Ui/DataDisplay/Models/` (namespace `CircuitRF.Ui.DataDisplay`)
Copy splotRF `src/Models/`: **`Plot.cs`, `Trace.cs`, `Axes.cs`, `Marker.cs`, `Misc.cs`** (the enums:
`PlotType`, `FreqUnit`, `DependentVarFormat`, `PlotDetail`, `LineType`, `MarkerType`, `PrecisionFormat`,
`DerivedParameters`, `TraceProperties` + `ColorLUT`, etc.). Rename `namespace splotRF` →
`namespace CircuitRF.Ui.DataDisplay`. Keep `Trace.Data` as `SNP` for now (DataSet/DataCube retarget is 7.2).
**Do NOT** port `AppSettings.cs`, `DataDisplayConfig.cs` (that's 7.1e), or `UndoRedo.cs` (later) in this step.
Models may reference `Avalonia.Rect`, `SkiaSharp`, and `RfCore` (all UI-layer-legal — `data-display.md` §2.6).
If any model references a splotRF *ViewModel/View/Control* type, that reference belongs to a later step —
flag it; it should not appear in the model layer.

### 2. Renderers → `src/Ui/DataDisplay/Renderers/` (namespace `CircuitRF.Ui.DataDisplay`)
Copy splotRF `src/Renderers/`: **`PlotRenderer.cs`, `AxesRenderer.cs`, `TraceRenderer_MarkerRenderer.cs`,
`RenderTheme.cs`, `TableRenderer.cs`**. Rename the namespace. Two retargets:
- **Font seam — drop splotRF's `SkiaFonts.cs`; use circuitRF's.** Replace every `SkiaFonts.Regular` →
  `CircuitRF.Ui.Renderers.SkiaFonts.PlexRegular` and `SkiaFonts.Bold` →
  `CircuitRF.Ui.Renderers.SkiaFonts.PlexBold` (add `using CircuitRF.Ui.Renderers;`). Do not copy splotRF's
  `SkiaFonts.cs`. (This is the DejaVu → IBM Plex switch for plot text.)
- **Color seam — keep `RenderTheme` Light/Dark; pick by circuitRF's active theme.** Port `RenderTheme.cs`
  as-is (its `GetTransparentAccent` already reads `SystemAccentColor`, which works in circuitRF). Full
  mapping of `RenderTheme` onto circuitRF's `ColorTheme`/`.ccolor` system is **deferred** — for 7.1b just
  select `RenderTheme.Light` vs `RenderTheme.Dark` from `Application.Current.ActualThemeVariant`. Leave a
  `// TODO 7.x: wire RenderTheme to circuitRF ColorTheme/.ccolor` marker.
- Port `TableRenderer.cs` faithfully so `PlotRenderer.Draw`'s `Table` branch compiles; it is **not exercised**
  in 7.1b (Table comes alive in 7.1c). Do not modify `PlotRenderer.Draw`.
- Replace the `"splotRF"` watermark string with `"circuitRF"` (in `PlotRenderer.DrawWatermark`).

### 3. Render-only `PlotControl` → `src/Ui/DataDisplay/Controls/PlotControl.cs` (namespace `CircuitRF.Ui.DataDisplay.Controls`)
**Do NOT port splotRF's 77 KB `PlotControl` wholesale** — it carries pan/zoom/markers/context-menus/the
inspector flyout/table interaction, all of which are 7.1c/7.1d. Write a **minimal render-only** control:
- `Control` subclass with a `DirectProperty<PlotControl, Plot?> Plot` (raise + `InvalidateVisual()` on set)
  and a `DirectProperty<PlotControl, RenderTheme> PlotTheme` (default chosen from the active theme variant).
- `Render(DrawingContext context)` pushes an `ICustomDrawOperation` whose `Render(ImmediateDrawingContext)`
  leases the Skia canvas (`ISkiaSharpApiLease`) and calls
  `PlotRenderer.Draw(canvas, (Bounds.Width, Bounds.Height), Plot, PlotDetail.Full, PlotTheme)`.
  **Copy this boilerplate from `SchematicCanvas`** (same pattern, same usings).
- No pointer handlers, no context menu, no flyouts in 7.1b.
- Re-render on theme change (subscribe to circuitRF's theme-changed signal or `ActualThemeVariantChanged`)
  by swapping `PlotTheme` and invalidating — minimal is fine.

(Defer `AxisLabelControl.cs` and `DragSelectOverlay.cs` to 7.1c.)

### 4. Host one PlotControl + a synthetic Rect plot (the render harness)
- In `DataDisplayViewModel`, add a method `InsertDemoPlot()` that builds a synthetic Rect plot exactly like
  splotRF's `PlotInspectorViewModel.DesignInstance` does: a small 2-port `SNP` (a few freq points), a
  `new Plot(PlotType.Rect, FreqUnit.GHz)`, a `Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Db)` (S21 in
  dB), `trace.BuildPath(PlotType.Rect, FreqUnit.GHz)`, `plot.Traces.Add(trace)`, `plot.Autoscale()`. Expose
  the resulting `Plot` (e.g. `CurrentPlot`) and flip `HasPlots`.
- In `DataDisplayView`, when a plot exists, host a single `PlotControl` filling the canvas:
  `<ctl:PlotControl Plot="{Binding CurrentPlot}" .../>` (replace/overlay the 7.1a empty-state). Keep the
  empty state when `HasPlots` is false, with a **temporary dev affordance** (a button bound to a
  `InsertDemoPlotCommand`) to seed the demo plot. **Mark this harness `// TEMP 7.1b — replaced by real plot
  creation in 7.1c`.**
- This keeps the default display empty (per the locked "starts empty" decision); the demo plot is an
  explicit dev action purely to verify rendering.

## Scope guardrails (do NOT do in 7.1b)
- No pan/zoom, no pointer interaction, no context menus, no marker editing, no selection.
- No Plot Inspector / `TraceRowViewModel` / inspector flyout (7.1d).
- No multi-plot containers, no canvas drag/resize, no tabs (7.1c).
- No real data sources / file open / `SnpLibrary` / DataSet picker (7.2).
- No `.cdd` persistence / `DataDisplayConfig` / `AppSettings` / `UndoRedo` (7.1e+).
- Don't port `AxisLabelControl`/`DragSelectOverlay`/`PlotContainerView`/`DataDisplayView`(splotRF's)/
  `DisplayWindow`/`PlotInspectorView`/any VM beyond what §4 needs.
- Keep everything in `src/Ui` (firewall: Data Display is UI; Skia/Avalonia allowed here).

## Gate (acceptance)
1. Builds green (`TreatWarningsAsErrors=true`); no reference to `splotRF` namespace remains in ported files.
2. Seeding the demo plot renders a **Rect** plot: framed axes, gridlines, tick labels, axis titles, and an
   S21-in-dB polyline — visually matching splotRF's Rect output **except** text is now **IBM Plex**.
3. Renders correctly in both **light and dark** themes (RenderTheme switches with the app theme variant).
4. Plot text uses IBM Plex (not DejaVu); the watermark (if shown) reads "circuitRF".
5. No regression to the schematic/symbol canvases (shared `SkiaFonts`/Skia usage unaffected).

## On completion
Add a one-line "Phase 7.1b — COMPLETE" note to `src/Ui/CLAUDE.md` (models + renderers ported, font/color
seams retargeted, render-only PlotControl, Rect verified). Report back with a screenshot/description before
7.1c (multi-plot canvas, containers, tabs, pan/zoom, Smith/Polar/Table) is briefed.
