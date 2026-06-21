# Brief 7.4h-3 — Contour UX round 3: axis-label suppression, color-picker flyout fix, fade-opacity, sizes, tight autoscale, constant-metric combo

**Phase:** 7.4h (contour UX iteration, round 3). One consolidated brief — 8 items. Two are non-trivial (the
color-picker-closes-inspector bug, §3; the fade-line-opacity render, §7); the rest are small model/VM/card/render-link edits.
**Design:** `docs/design/loadpull-contours.md` §2.5; `data-display.md` (titles/axis-labels).
**Pairs with prior:** builds on 7.4h-1 (card/model) and 7.4h-2 (titles/labels/MXP/MXE/grid-points/colormaps), both landed.

**Verified on disk (anchors):**
- `Plot.Title` already composes contour titles; `Plot.XLabel`/`YLabel` already have a **Rect-contour** branch
  ("Real (Ω)"/"Imaginary (Ω)"). **Smith/Polar contour is NOT handled** — `XLabel` falls through to the
  network path and returns `freq (min to max …)` (item 1 root cause; §1).
- `TraceRowViewModel`: all contour `[ObservableProperty]` fields + handlers + the three `Pick*ColorAsync(Window? owner)`
  methods + `PickGridPointColorCommand`/`PickLabelBgColorCommand`/`PickLabelFgColorCommand`
  (`IAsyncRelayCommand<Window?>`). `RebuildContour()` computes Grid/Scatter/Levels + `MxpCoord`/`MxeCoord`.
- Card swatches bind `Command=Pick*Command CommandParameter="{Binding $parent[Window]}"`.
- `PlotControl.ShowPlotInspector()`: inspector is a **`Flyout` with `ShowMode=FlyoutShowMode.Standard`** (light-dismiss),
  `OverlayInputPassThroughElement=this`, reusing the container's single Inspector VM. `_inspectorFlyout` field; `Closed` handler present.
- `ColorPickerDialog : Window` — modal via `ShowDialog<Rgba?>(owner)`. Settings uses it with `this` (Settings IS a Window, not a flyout) — that's why Settings works and the inspector flyout doesn't.
- `PlotInspectorViewModel.AddContourTrace()` builds the contour trace + `ContourData { ShowFill = … }`, adds to `_plot.Traces` (→ `OnTracesChanged` → `Autoscale()`), creates the VM (whose ctor runs `RebuildContour`).
- `ContourData`: has `GridPointColor`, `LabelBackground`, `LabelForeground`, `LineColor`, `DisplayMxp/Mxe`, `MxpCoord/MxeCoord`, colormap, etc. **§5/§7 add new fields.**
- `ContourRenderer.DrawIsoLines(canvas, canvasSize, polylines, tf, lineColor, strokeWidth, drawLabels)` — where fade-opacity + line-color + level-font-size hook in. `IsoPolyline(double Level, IReadOnlyList<(double X,double Y)> Points, bool Closed)`.

---

## 1. Smith/Polar contour: remove freq X-label + Y-labels (RENDER too)
Two layers:
**(a) Plot model** — `Plot.XLabel`: add a Smith/Polar-contour guard BEFORE the network freq fallback. When the
plot is complex (`SupportsComplex`) and any trace `IsContourTrace`, return `""`. Same idea for `YLabel` (already
returns "" for non-Rect, but make the contour intent explicit). So:
```csharp
// in XLabel, before the "Network/SNP behavior" block:
if (PlotType.IsComplex() && Traces.Any(t => t.IsContourTrace)) return "";
```
**(b) Renderer** — returning "" may not be enough: confirm `AxesRenderer`/`PlotRenderer` does not draw the
freq label / Y tick-axis description strip for a Smith/Polar contour. Smith/Polar already omit rectangular
axis labels generally, so the leak is specifically the freq X-label text drawn from `Plot.XLabel`. With XLabel
now "" the title-block/freq-label draw should skip empty strings — **verify** the label-draw guards on
`string.IsNullOrEmpty`. If any axis-label/freq strip still draws for Smith/Polar, gate it off when
`PlotType.IsComplex()` (these plots never have rectangular freq/Y axis labels). Owner reported the freq label
*still* shows, so the render-side suppression is the actual fix — chase it in the renderer, not just the model.
**Gate:** a Smith and a Polar contour plot show NO freq label and NO Y-axis label (nothing drawn); Rect contour
still shows Real/Imag (Ω); a normal (non-contour) Smith S-param plot is unchanged.

## 2. MXP / MXE on by default
In `PlotInspectorViewModel.AddContourTrace()`, set both in the `ContourData` initializer:
```csharp
trace.ContourData = new ContourData
{
    ShowFill   = ContourDefaults.ShowFillDefault(plane),
    DisplayMxp = true,
    DisplayMxe = true,
};
```
(The VM ctor copies these into `ContourDisplayMxp/Mxe`, so the card toggles show active.) Also set the
`ContourData` field defaults to `true` for `DisplayMxp`/`DisplayMxe` so persisted/loaded traces and any other
creation path default on. **Gate:** adding a contour shows MXP + MXE dots immediately; the Options toggles read active.

## 3. Color-picker closes the Plot Inspector (items 3 + 4 — shared root cause)
**Diagnosis (confirmed on disk):** the inspector is a light-dismiss `Flyout` (`ShowMode.Standard`). Opening the
modal `ColorPickerDialog.ShowDialog(owner)` creates a new window that takes focus → the flyout light-dismisses →
the inspector closes. The grid-point color VALUE still updates (the `await` completes and writes the property),
which is exactly what the owner observed; the label-bg/text cases look like "no update" because the inspector is
gone before the user sees the change. Settings works only because Settings IS a Window, not a flyout. Also,
`CommandParameter="{Binding $parent[Window]}"` from inside a flyout resolves to the flyout's **PopupRoot**, not
the main window — wrong owner.

**Fix (two parts):**
**(a) Correct owner.** Don't pass `$parent[Window]`. In each `Pick*ColorAsync`, resolve the **main application
window** as the dialog owner — e.g. via `TopLevel.GetTopLevel(...)` on a real control, or have `PlotControl`
inject an owner-provider into the inspector VM when it builds the flyout (PlotControl knows its `TopLevel`). The
dialog must be owned by the main window, not the popup root.
**(b) Keep the flyout open during the dialog.** Even with the right owner, the modal dialog steals focus and the
Standard flyout light-dismisses. Suppress that while a pick is in progress. Two acceptable approaches — pick the
smaller that works:
  - **Guard + cancel Closing:** give `PlotControl` an `internal bool _suppressInspectorDismiss` and subscribe to
    the inspector flyout's `Closing` event (`FlyoutBase.Closing` is cancelable); while a color pick is active,
    cancel the close. The VM signals pick-start/end to PlotControl (e.g. an event on the VM the control
    subscribes to, or route the pick through the control). Restore normal dismissal after.
  - **In-flyout Popup picker (preferred if clean):** replace the modal `ColorPickerDialog` for these swatches
    with an in-flyout `Popup`-hosted color picker (same `ColorView` control the dialog wraps), consistent with
    the card's other `IconSelectButton` popups that never dismiss the flyout. This sidesteps the modal-window
    focus problem entirely and matches the rest of the card's interaction model. The owner asked for "the same
    color picker as Settings"; a Popup hosting the same `ColorView` preserves the look while fixing the bug —
    confirm this is acceptable, else use the guard approach.

Apply the fix to **all** swatches: grid-point, label-bg, label-text, AND the new line-color swatch (§7). After
the fix: opening any swatch's picker keeps the inspector open and updates the color live.
**Gate:** open each of the 4 color pickers from the contour Options; the inspector stays open; the colour
updates on OK; Cancel leaves it unchanged.

> This touches `PlotControl` flyout lifecycle + the VM pick commands. Do it carefully with a build between (a)
> and (b). Don't regress the Axes-Labels / Axes-Limits flyouts (same Standard pattern) — only the inspector
> needs the dialog-suppression, and only while a child dialog is open.

## 4. (covered by §3 — grid-point picker is the same bug)

## 5. New Options: Grid point size + Level font size (linked to render)
**Model** (`ContourData`): add
```csharp
public double GridPointSize  { get; set; } = 3.0;   // px radius for scatter dots
public double LevelFontSize  { get; set; } = 9.0;   // px for iso-line level labels
```
**VM**: `[ObservableProperty] double _contourGridPointSize = 3.0;` and `_contourLevelFontSize = 9.0;` with
`On…Changed` → set `cd.*`, `_parent.Notify()` (redraw-only). Init from `cd` in ctor.
**Card** (Options): a "Grid pt size" `NumericUpDown` (e.g. min 1, max 12, step 0.5) near the grid-points row; a
"Level font" `NumericUpDown` (min 5, max 24, step 1) near the labels/colormap controls. Short labels.
**Render**: pass `GridPointSize` into the grid-point draw (the `DrawGridPoints` radius from 7.4h-2 §6) and
`LevelFontSize` into `DrawIsoLines` label text size (replace the hard-coded label font size). **Gate:** changing
either value visibly changes dot radius / label text size.

## 6. Rect contour: tight autoscale to the contour edge
Owner wants the Rect plot, when a contour is added, to autoscale **tight** to the contour grid perimeter (no
slack). Two parts:
**(a) Bounding rect** — `Trace.PathBoundingRect()` contour branch (from 7.4h-2 §4) must return the grid extent
(`min/max XSpace`, `min/max YSpace`). Confirm it exists and returns the *grid* extent (the resampled
`SurfaceGrid`), which is the contour perimeter.
**(b) Tight, and actually applied at add-time.** Two issues:
  - **Padding:** Rect autoscale applies `paddingRect = 0.10`. "Tight to the edge" means **no padding** for a
    contour. In `AutoscaleCore`, when the plot has a contour trace (Rect), use padding ≈ 0 for that plot (or
    special-case: if any trace `IsContourTrace`, set `padX=padY=0`). Keep normal padding for non-contour Rect.
  - **Timing:** `AddContourTrace` adds the trace (→ `Autoscale`) BEFORE the new `TraceRowViewModel` ctor runs
    `RebuildContour()` — so at first autoscale `ContourData.Grid` is still null and `PathBoundingRect` returns
    empty → the plot frames the default `(0,0,2,2)`. After `RebuildContour` populates the grid, **re-autoscale**.
    Fix: after creating the contour VM in `AddContourTrace` (which fits), call `_plot.Autoscale(force:true)` (or
    have `RebuildContour`'s completion notify the plot to re-autoscale). Simplest: in `AddContourTrace`, after
    `Traces.Add(new TraceRowViewModel(...))`, call `_plot.Autoscale(force:true)` then `PlotNeedsRedraw`.
**Gate:** adding a contour to a Rect plot frames the surface exactly to its Real/Imag extent with no slack;
the surface fills the plot area.

## 7. Line color + Fade Line Opacity (new render feature)
**Model** (`ContourData`): `LineColor` already exists (SKColor). Add:
```csharp
public bool FadeLineOpacity { get; set; }   // default set per-plane at creation (§ below)
```
**Creation default** (`AddContourTrace`): `FadeLineOpacity = (plane == SurfacePlane.Gamma)` — i.e. **true for
Smith/Polar, false for Rect**. (Plane is already computed there.)
**VM**: `[ObservableProperty] SKColor _contourLineColor;` (init from `cd.LineColor`) with `On…Changed` → set
`cd.LineColor`, Notify. `[ObservableProperty] bool _contourFadeLineOpacity;` → set `cd.FadeLineOpacity`, Notify.
Add a `PickLineColorCommand` (`IAsyncRelayCommand<Window?>`) + `PickLineColorAsync` mirroring the grid-point one
(and subject to the §3 fix).
**Card** (Options, BELOW the grid-point row): a "Line" label + the small square color swatch button (same style
as grid-point: `Border` bg via `SKC` converter, opens the color picker) bound to `ContourLineColor` /
`PickLineColorCommand`. **Next to it**, a "Fade" seg-btn toggle (`Classes.active="{Binding ContourFadeLineOpacity}"`,
`Command=ToggleFadeLineOpacityCommand`).
**Render** (`ContourRenderer.DrawIsoLines`): when `FadeLineOpacity`:
  - For each iso-polyline, fade its stroke alpha so the line is fully opaque near the **contour center**
    (the peak/trough — the extremum of the surface) and fades to **fully transparent** away from it.
  - Practical implementation: the contour's "center" is the location of the surface max (peak) or min (trough)
    — use `MxpCoord`/the grid's max cell, or more generally the grid extremum. For each iso-level, lines at
    levels closer to the extremum value are more opaque; lines at the outer levels fade to transparent. Simplest
    correct reading: **alpha as a function of the level's normalized position** — innermost ring (nearest the
    peak/trough level) = full alpha, outermost = 0. Linear or exponential falloff (owner allows either; start
    linear: `alpha = lineColor.Alpha * (1 - t)` where `t∈[0,1]` is the level's normalized distance from the
    extremum level, or per-vertex distance from the extremum point for a smoother gradient).
  - Per-vertex gradient (nicer): for each polyline vertex, compute distance from the extremum point (in world
    coords), normalize by the grid's max radius, and set per-segment alpha so far-from-center vertices are
    transparent. Use an `SKShader` linear/radial-gradient along the stroke, or draw the polyline in short
    segments with interpolated alpha. Radial-alpha gradient centred on the extremum is the cleanest vector
    approach.
  - When `FadeLineOpacity` is false, draw lines at full `LineColor` alpha (current behavior).
  - Line color comes from `ContourData.LineColor` (wire it — `DrawIsoLines` currently takes a `lineColor` param;
    pass `cd.LineColor`).
**Gate:** Smith/Polar contour lines fade to transparent toward the outer rings by default; Rect default is
no-fade; toggling Fade flips it; the line-color swatch changes the iso-line colour.

## 8. Constant-metric → ComboBox (not TextBox)
In the card, the constant-metric name (shown when constraint kind = Const) is a `TextBox`
(`ContourConstraintMetric`). Replace with a `ComboBox` bound to `AvailableMetrics` (the same list the Metric
combo uses), `SelectedItem="{Binding ContourConstraintMetric, Mode=TwoWay}"`. `AvailableMetrics` is already
populated in the VM. **Gate:** the constant-metric field is a dropdown of valid metrics; picking one re-fits.

---

## Slice plan (compile-and-test-gated)
- **7.4h-3a — model + defaults + small VM/card** (§1a, §2, §5 model+VM+card, §7 model+VM+card, §8): all the
  non-render, non-flyout edits. Compile.
- **7.4h-3b — renderer** (§1b suppression, §5 render-link sizes, §6 tight autoscale, §7 fade-opacity + line-color
  render). Owner-verified visually.
- **7.4h-3c — color-picker flyout fix** (§3/§4): the careful one. Build between owner-fix and dismissal-fix.
  Apply to all 4 swatches.

## Constraints / gotchas
- §3 is the subtle one: don't regress the Axes-Labels/Limits flyouts; only suppress dismissal while a child
  dialog is open; restore after. Prefer the in-flyout Popup picker if it's clean, else guard+cancel `Closing`.
- §6 timing: autoscale must run AFTER `RebuildContour` populates the grid — re-autoscale in `AddContourTrace`.
- §7 fade: keep vector (no bitmap) so PDF/SVG export stays crisp — radial alpha gradient or segment-wise alpha.
- Engine params unchanged here; sizes/colors/fade are all redraw-only (`Notify`), not re-fit.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.
- HIG: short labels, tooltips for full names, swatches consistent with grid-point swatch.

## Tests
- `Plot.XLabel`/`YLabel`: Smith/Polar contour → ""; Rect contour → Real/Imag (Ω); non-contour unchanged.
- `AddContourTrace`: `DisplayMxp/Mxe` true; `FadeLineOpacity` true on Smith/Polar, false on Rect.
- `Trace.PathBoundingRect` contour branch returns grid extent; Rect autoscale tight (no padding) frames it.
- VM: GridPointSize/LevelFontSize/LineColor/FadeLineOpacity changes set `cd.*` + Notify (no re-fit).
- Color-picker flyout fix is owner-verified (inspector stays open; colour updates). Fade-opacity render owner-verified.
