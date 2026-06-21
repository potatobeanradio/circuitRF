# Brief 7.4h-2 — Contour UX round 1: plot titles, axis labels, impedance grid, autoscale, colormap, MXP/MXE/grid-points

**Phase:** 7.4h (contour UX iteration, round 1 — part 2 of 2). Pairs with **7.4h-1** (card / VM / model / File
menu). This brief = the **plot + renderer** changes. Depends on 7.4h-1 §1 (the new `ContourData` fields:
`DisplayMxp`, `DisplayMxe`, `DisplayGridPoints`, `GridPointColor`, `LabelForeground`, `InterpKernel`,
`Smoothing`, `Epsilon`). Land 7.4h-1 §1 first.

**Design:** `docs/design/loadpull-contours.md` §2.5, `data-display.md` (titles/axis-labels/grids).
**Goal:** make contour plots read correctly: default composed titles, suppressed/relabeled axes per substrate,
an always-on impedance grid on Rect contour, autoscale-to-grid on Rect, MXP/MXE markers + original grid-point
dots, and real matplotlib colormaps for the fills.

**Consumes (verified on disk):**
- `Plot` (`src/Ui/DataDisplay/Models/Plot.cs`): `Title => CustomTitleOn ? CustomTitle : ""` (no default
  today); `XLabel`/`YLabel`/`Y2Label` (custom-or-fallback); `Autoscale()`/`AutoscaleCore()` (uses each trace's
  `PathBoundingRect()`; **contour traces have empty `Points` ⇒ no bounding box ⇒ Rect contour does NOT
  autoscale to the grid — the user-reported gap**); `CustomXLabelOn` etc. override flags (user override already
  works via `AxesLabelsViewModel`).
- `Trace.ContourData` / `IsContourTrace`; `ContourData.Grid` (`SurfaceGrid` w/ `XSpace`/`YSpace`/`Values`),
  `Scatter` (`ScatterReduction.Coords` = measured grid points), `Levels`, `MetricName`, constraint fields,
  new display fields from 7.4h-1.
- `LoadpullSurface.MaxPower(freqIdx, constraint, plane, z0)` / `MaxEfficiency(...)` → `MxxResult?`
  (`Measured` + `Interpolated` Γ/Z) — for MXP/MXE markers. `RecommendedBox(fit)` → `ViewBox` (the auto extent
  for Rect autoscale).
- Renderers: `PlotRenderer.Draw` (grid → clip → contour-fill pre-pass → traces → iso-lines → markers →
  title/labels); `ContourRenderer` (`DrawTopoMapFill`/`DrawHeatMapFill`/`DrawIsoLines`); `AxesRenderer`
  (`DrawRect`/`DrawSmithGrid`/`DrawPolarGrid`); the title/label draw in `PlotRenderer` reads `plot.Title`/
  `plot.XLabel`/`plot.YLabel`.
- `tf.ToCanvas(wx, wy, useSecondary)` world→canvas (Smith world = Γ; Rect world = Z).

**Firewall:** renderer + Plot-model (UI) changes; colormap ramps live in `src/Ui` (renderer colour, not
RfCore). `LoadpullSurface` stays colour-agnostic.

---

## 1. Default contour plot title (composed from traces)
Today `Plot.Title` is empty unless the user sets a custom title. Add a **computed default** for contour plots
when `!CustomTitleOn`.

**Per-trace title string** (build a helper, e.g. `ContourData.TitleString` or a renderer/Plot helper):
- Compression constraint: `"P-{c}dB {Metric} ({unit})"` — e.g. compression 3 dB, metric Pout →
  **"P-3dB Pout (dBm)"**; metric Gain → **"P-3dB Gain (dB)"**.
- Constant-metric constraint: `"{Metric} ({unit}) at Constant {OtherMetric}={value} {otherUnit}"` — e.g.
  **"Efficiency (%) at Constant Pout=30 dBm"**.
- Units per metric: Pout→dBm, Gain/Gt/Gp→dB, DE/PAE/Efficiency→%, AMPM→deg (centralize a metric→unit map;
  reuse/extend the one 7.4h-1 may add for level defaults). Use a friendly metric display name (DE/PAE →
  "Efficiency" if that's the owner's preferred wording; Gt/Gp → "Gain" — confirm the exact label map; the
  examples imply "Pout", "Gain", "Efficiency").
- Compression value formatting: trim trailing zeros (3 not 3.0); the "P-3dB" form uses the compression dB.

**Plot title** = join all contour traces' title strings with **" / "** between them (e.g. two traces →
`"P-3dB Pout (dBm) / P-3dB Gain (dB)"`).

**Where:** give `Plot` a `DefaultTitle` notion: change `Title` to
`CustomTitleOn ? CustomTitle : ComputedDefaultTitle` where `ComputedDefaultTitle` is non-empty only when the
plot has contour traces (join their title strings); otherwise "" (unchanged for non-contour plots). User
override still wins (the existing `CustomTitleOn`/`AxesLabelsViewModel` path is untouched). The renderer already
draws `plot.Title`; no renderer change needed for the title itself.

**Gate:** a Smith plot with one Pout@3dB contour shows title "P-3dB Pout (dBm)"; add a Gain@3dB contour → title
becomes "P-3dB Pout (dBm) / P-3dB Gain (dB)"; a constant-Pout efficiency contour shows
"Efficiency (%) at Constant Pout=30 dBm"; setting a custom title still overrides.

---

## 2. Axis labels per substrate (contour plots)
**Smith / Polar contour (Γ-plane):** no X-label and no Y-label, and they must **not be rendered** (not just
empty strings — suppress the label draw so no blank gap/baseline shows). Today Smith/Polar may already skip
axis labels; confirm and ensure contour Smith/Polar draws none.

**Rect contour (Z-plane):** X-label = **"Real (Ω)"**, Y-label = **"Imaginary (Ω)"** (the impedance plane).
Add this as the default (`!CustomXLabelOn`/`!CustomYLabelOn`) when the Rect plot has contour traces:
- In `Plot.XLabel`/`YLabel`, add a contour branch: if `!CustomXLabelOn` and the plot is Rect with a contour
  trace, return "Real (Ω)" (X) / "Imaginary (Ω)" (Y). Keep the existing freq/cube logic for non-contour.

**User override:** unchanged — `CustomXLabelOn`/`CustomYLabelOn` still take precedence (the existing
per-plot axis-label override UI works as-is). Just make sure the contour defaults slot in at the
`!CustomXLabelOn` fallback, so overriding still works.

**Gate:** Smith contour shows no axis labels (nothing rendered); Rect contour shows "Real (Ω)" / "Imaginary
(Ω)"; overriding either via the axis-label editor replaces it.

---

## 3. Rect contour: always-on impedance grid
A Rect contour plot is in the Z-plane and should **always show an impedance grid** (the rectangular grid is
fine — X=Real Ω, Y=Imaginary Ω — i.e. the standard Rect grid with the impedance labels from §2). Confirm the
Rect grid (`AxesRenderer.DrawRect`) renders for contour Rect plots (it should, since contour is just another
trace on a Rect plot). The "impedance grid" requirement = the normal Rect grid + the Real/Imag(Ω) labels (§2) +
autoscale to the data (§4). If the owner intends a *Smith-like impedance arc grid* on the Rect plane, that's a
larger feature — flag it and confirm; for round 1, the rectangular Real/Imag grid is the reading. **Confirm
intent with the owner if ambiguous**, but default to the rectangular impedance grid.

---

## 4. Rect contour: autoscale to grid X/Y min/max (the reported gap)
`AutoscaleCore` unions trace `PathBoundingRect()`; a contour trace has no `Points`, so its box is empty and the
Rect plot doesn't frame the contour. Fix:
- Give `Trace.PathBoundingRect()` a contour branch: when `IsContourTrace` and `ContourData.Grid` is set, return
  the grid extent — `Rect(min XSpace, min YSpace, span XSpace, span YSpace)` (the resampled grid's X/Y range,
  which is the `RecommendedBox` extent the surface was sampled on). For Smith/Polar the autoscale forces the
  unit disk anyway (so this mainly matters for Rect-Z, exactly as asked).
- Then `AutoscaleCore` naturally frames the Rect contour to the grid min/max X and Y. Keep the existing Rect
  padding.
- Alternative if `Grid` isn't always present at autoscale time: have the contour trace expose its
  `RecommendedBox`/grid extent via `ContourData` and read it in `PathBoundingRect`. Prefer reading `Grid`
  (`XSpace`/`YSpace`) since it's set after `RebuildContour`.

**Gate:** a Rect contour autoscales so the surface fills the plot (min/max Real on X, min/max Imag on Y), not
the default `(0,0,2,2)` window.

---

## 5. MXP / MXE markers
When `ContourData.DisplayMxp` / `DisplayMxe`, draw a marker at the MXP / MXE location on the substrate:
- VM (or renderer) gets the location from `LoadpullSurface.MaxPower(freqIdx, constraint, plane, z0)` /
  `MaxEfficiency(...)` → `MxxResult.Measured` (or `.Interpolated` — pick `Measured` for "the measured optimum";
  confirm with owner, default `Measured`). Map Γ/Z → canvas via `tf.ToCanvas`.
- Draw a distinct marker (e.g. a small ✛ or ◆ with a tiny "MXP"/"MXE" label). Keep it vector. The cleanest
  place is a small addition to `ContourRenderer` (e.g. `DrawOptimaMarkers`) called from the `PlotRenderer`
  contour pass, after iso-lines. The VM must stash the MXP/MXE coords on `ContourData` during `RebuildContour`
  (compute once, since they need the surface), OR the renderer asks the VM — prefer **VM computes and stores**
  `MxpCoord`/`MxeCoord` (nullable `Complex`) on `ContourData` during rebuild so the renderer stays
  surface-free. Add those two cached fields to `ContourData` (renderer-read, VM-write).
- If MXP/MXE is null (no fit), draw nothing.

**Gate:** toggling Max Power / Max Eff places a labelled marker at the optimum on both Smith and Rect.

---

## 6. Grid-point dots (original loadpull datapoints)
When `ContourData.DisplayGridPoints`, draw a small dot at each measured grid point in `ContourData.Scatter.Coords`
(the reduced scatter = the Γ/Z points behind the surface), in `GridPointColor` (default black):
- `ContourRenderer.DrawGridPoints(canvas, scatter, tf, color)` — small filled circles (vector) at each
  `tf.ToCanvas(coord.Real, coord.Imaginary)`. ~2–3px radius. Call from the contour pass (over fill, under/with
  iso-lines).
- `Scatter` is already produced by `RebuildContour` (`surface.Reduce(...)`). No new data needed.

**Gate:** toggling Grid Pts shows a dot at each original loadpull termination; colour follows the swatch.

---

## 7. Matplotlib colormaps (real ramps)
Implement the matplotlib colormaps named by `ContourColorMap` (gray, bone, pink, spring, summer, autumn,
winter, cool, Wistia, hot, afmhot, gist_heat, copper) so TopoMap (and HeatMap) render in the actual colormap
colours instead of the current fixed blue→red:
- Add a `ContourColormaps` helper in `src/Ui` mapping each enum to a ramp: a function `SKColor Sample(double t)`
  for `t∈[0,1]`. Implement via the standard matplotlib control-point tables (piecewise-linear RGB anchors —
  the segmentdata for each map). These are well-known small tables; encode the anchor points per map and
  lerp. (gray/bone/copper/hot/afmhot/gist_heat are simple few-anchor ramps; spring/summer/autumn/winter/cool
  are 2-anchor linear; pink/Wistia have a few anchors.)
- **TopoMap:** replace the current `BuildTopoPalette` (HSV blue→red) with sampling the selected colormap at
  each band's normalized level (`t = (bandIndex)/(nBands-1)`), keeping the opaque-bands + SaveLayer-alpha
  vector approach from the existing renderer. So band colours come from the chosen colormap.
- **HeatMap:** colourize the additive density using the selected colormap ramp (the warm core → transparent
  becomes colormap(t) → transparent). Keep it vector (radial gradients).
- `ContourData.ColorMap` is no longer "deferred" — wire it through. Remove the DEFERRED comments on ColorMap in
  `ContourData`/VM once rendered. Default colormap: pick one that reads well for loadpull (e.g. keep a sensible
  default like `Hot` or a perceptual ramp; owner can change per-trace).

**Gate:** switching the Colormap combo visibly changes the TopoMap band colours to the matplotlib ramp; a few
spot colours match matplotlib references (e.g. `viridis`-style endpoints if added later; for now verify
`hot` = black→red→yellow→white, `cool` = cyan→magenta, `copper` = black→copper).

> Note: the named set is matplotlib's *non-perceptual* classics. If the owner later wants viridis/plasma, add
> them to the enum + table; out of scope here (only the listed names).

---

## 8. Slice plan (compile-and-test-gated)
- **7.4h-2a — titles + axis labels** (§1, §2): composed default title; Smith/Polar no-axis; Rect Real/Imag(Ω);
  override still wins.
- **7.4h-2b — Rect impedance grid + autoscale-to-grid** (§3, §4): `PathBoundingRect` contour branch; confirm
  Rect grid + labels.
- **7.4h-2c — MXP/MXE + grid-point dots** (§5, §6): cached MXP/MXE coords on `ContourData`; renderer draws
  markers + dots.
- **7.4h-2d — matplotlib colormaps** (§7): `ContourColormaps` ramps; TopoMap/HeatMap use them; un-defer
  ColorMap.

## 9. Constraints / gotchas
- **User override precedence:** all default title/label logic slots into the `!Custom…On` fallback. Never
  override a user-set title/label. The existing `AxesLabelsViewModel` path is untouched.
- **Autoscale:** only Rect needs grid-extent framing (Smith/Polar force the unit disk). Keep existing padding;
  don't let an empty grid produce a degenerate window (guard `Grid == null`).
- **MXP/MXE compute on the surface** → VM writes coords to `ContourData`; renderer stays surface-free. Recompute
  in `RebuildContour` (they depend on metric/constraint/freq/fit params).
- **Colormaps are UI-side** (renderer colour). RfCore stays colour-agnostic. Keep fills **vector** (the round
  that made TopoMap/HeatMap vector must not regress — sample colormap into band colours / gradient stops, no
  `DrawBitmap`).
- **Impedance-grid intent (§3):** if "impedance grid" means a Smith-like arc grid on Rect, confirm with owner;
  default to the rectangular Real/Imag grid for round 1.
- TreatWarningsAsErrors-clean; no `<`/`>` in XML doc comments.

## 10. Tests
- `Plot` title composition: per-trace strings + " / " join; compression vs constant-metric forms; custom
  override wins. (Pure model test.)
- `Plot.XLabel`/`YLabel` contour branch: Rect → "Real (Ω)"/"Imaginary (Ω)"; Smith → none; override wins.
- `Trace.PathBoundingRect` contour branch returns the grid extent; Rect autoscale frames it.
- `ContourColormaps.Sample` endpoints match references for a few maps (hot/cool/copper/gray).
- Renderer (MXP/MXE/grid-points/colormap fills) owner-verified visually per gates.

## 11. Out of scope (future rounds)
- Smith-like impedance-arc grid on the Rect plane (if that's what's wanted — confirm; larger feature).
- Perceptual colormaps (viridis/plasma/etc.) beyond the listed matplotlib classics.
- Richer iso-line label boxes (label bg/fg colours now exist as fields; the actual label-box *rendering* with
  bg/fg + spacing may be its own round if the current single-label draw isn't sufficient).
- `.s1p` overlay styling polish.
