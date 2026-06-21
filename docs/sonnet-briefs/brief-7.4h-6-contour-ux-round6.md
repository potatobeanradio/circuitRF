# Brief 7.4h-6 — Contour UX round 6: Smith fill edge, MXP/MXE-on-kernel, copy/paste, defaults, card polish

**Phase:** 7.4h round 6. 16 items. Three are substantive (Smith fill ragged edge §1, copy/paste loses contour
§3, constant-metric units §16); the rest are defaults/clamps/card layout. **Builds on 7.4h-1..-5 (landed).**

**Verified anchors (on disk):**
- `LoadpullSurface.MaxPower`/`MaxEfficiency` → `GetMxx(...)` → `Fit(freqIdx, metricY, mxxConstraint, plane, z0)`
  **with NO kernel/smooth/epsilon** → always uses default `Multiquadric`. So MXP/MXE ignore the user's RBF
  kernel (§2 root cause). `Fit(... kernel, smooth, epsilon)` overload exists.
- `Trace(Trace src, ...)` copy ctor copies cube/Z0/family fields but **does NOT copy `ContourData`** → a pasted
  contour trace has `ContourData == null` → `IsContourTrace` false → renders nothing, and `PathBoundingRect`
  returns `default` so Rect autoscale falls to the default window (§3 root cause). Smith looks unaffected only
  because complex autoscale forces the unity circle regardless.
- `ContourData`: `ColorMap = Hot` default (§4 → Bone). `LabelForeground = White` (§6 → black),
  `LabelBackground = (0,0,0,140)` (§6 → white). `LabelSpacing = 1.0` default, VM clamps; the >10 cap is a card
  `NumericUpDown` Maximum, not the model (§9). `DrawLabels = true` default (§13 → plane-dependent). No
  `Clone()` method (needed for §3). No label-box-stroke field (§5 — render-only, no field needed).
- `ContourRenderer.DrawTopoMapFill`: marching-squares band fill; per-cell polygons are **skipped entirely when
  any corner is NaN**, so the Γ-disk boundary is approximated at cell resolution → blocky/ragged outer edge
  (§1 root cause). `DrawIsoLines` label box: `bgPaint` fill only, no stroke (§5).
- `ContourRenderer.DrawOptimumMarker` + `DrawIsoLines` labels: round-5 divided sizes BY `zoomLevel` to keep
  on-screen size constant. **User now wants the opposite** — glyphs/labels should scale WITH zoom (bigger when
  zoomed in). So: remove the divide-by-zoom (draw at constant canvas-px on the pre-scaled canvas) (§10).
- `PlotInspectorViewModel.AddContourTrace`: builds `ContourData { ShowFill, DisplayMxp=true, DisplayMxe=true,
  FadeLineOpacity=(plane==Gamma) }`; sets `LevelMode` (round-5). The place to set per-add defaults (§4 colormap
  inherit, §6 label colors, §13 DrawLabels) and line-color-matches-colormap (§4).
- `TraceRowViewModel.RebuildContour`: calls `surface.MaxPower(freqIdx, constraint, plane)` /
  `MaxEfficiency(...)` (no kernel) — update once §2's RfCore signature changes.
- Card constant-metric row: constant-metric `ComboBox` (round-4) + `ConstraintValue` box. Metric combo +
  Colormap/Fill row exist. "Grid Pts" text + Show button are separate (§12). Colormap/Fill widths clip (§11).

---

## §1 — Smith filled-contour ragged/blocky outer edge [CHANGE — algorithm]
**Why it happens:** `DrawTopoMapFill` builds the fill from per-grid-cell marching-squares polygons and **drops
any cell touching a NaN** (NaN = outside the Γ-disk). So the fill's outer boundary is a staircase at the 50×50
grid-cell resolution, not the smooth Γ circle. Known fixes (pick the robust one):
1. **Clip the fill to the true Γ-disk path** (recommended): before drawing the bands, `canvas.Save()` +
   `canvas.ClipPath(circlePath, antialias: true)` where `circlePath` is the unit Γ-circle (radius =
   `MaxNodeRadius*1.02` in world → canvas, same radius `Resample` uses for NaN masking), then draw the bands,
   then `Restore()`. The antialiased circular clip gives a smooth edge regardless of grid resolution, and the
   bands inside are unaffected. This is the cleanest, cheapest fix and stays vector (SKPath clip exports to
   PDF/SVG). **Do this one.**
2. (Alternative/secondary) raise `Resample` resolution (50→~100) — reduces but doesn't eliminate the staircase,
   and costs RBF evaluations. Not a substitute for the clip; optionally combine.
3. (Alternative) marching-squares with sub-cell NaN-edge interpolation against the disk boundary — much more
   code; the clip in (1) achieves the same visual result far more simply.
**Implementation note:** the disk only applies when `plane == Gamma` (Smith/Polar). For Rect (Z-plane) there's
no disk — skip the circular clip there (or clip to the grid rect, which is already the case). Pass the plane (or
a "clip radius / null" ) into `DrawTopoMapFill`. The Γ-circle is centered at origin in world coords; map to
canvas via `tf.ToCanvas`.
**Gate:** Smith filled contour has a smooth round outer edge (no staircase); Rect fill unchanged; PDF export
still smooth (vector clip).

## §2 — MXP/MXE must update when RBF interpolation mode changes [BUG]
Root cause: `GetMxx` calls `Fit(...)` without the kernel/smooth/epsilon, so MXP/MXE always use default
Multiquadric. **Fix (RfCore — firewall-clean):** thread the engine params through.
- `MaxPower`/`MaxEfficiency`: add params `RbfKernel kernel = Multiquadric, double smooth = 1e-3, double?
  epsilon = null`; forward to `GetMxx`.
- `GetMxx`: add the same params; pass them into the internal `Fit(freqIdx, metricY, mxxConstraint, plane, z0,
  kernel, smooth, epsilon)` call.
- `RecommendedBox` also calls `GetMxx` (for the view box) — forward `fit.Kernel`/`fit.Smooth`/`fit.Epsilon`
  (the `LoadpullFit` already carries them) so the box matches the chosen kernel too.
- **VM:** `TraceRowViewModel.RebuildContour` — pass `cd.InterpKernel`, `cd.Smoothing`, `cd.Epsilon` into the
  `surface.MaxPower(...)`/`MaxEfficiency(...)` calls.
**Gate:** change the RBF kernel (Multiquadric → ThinPlate → Gaussian) → the MXP/MXE glyphs move to the new
optima; an RfCore test asserts `MaxPower` differs across kernels on a non-trivial surface.

## §3 — Copy/paste of a contour plot drops the contour trace [SERIOUS BUG]
Root cause: the `Trace` copy ctor never copies `ContourData`, so the pasted trace isn't a contour trace (renders
nothing; Rect autoscale wrong; Smith masks it via forced unity circle). **Fix:**
1. **Add `ContourData.Clone()`** (deep copy) returning a new `ContourData` with all authoring/style/display
   fields copied. Do NOT copy the derived/cached fields (`Grid`, `Scatter`, `Levels`, `MxpCoord`, `MxeCoord`,
   polyline cache) — leave them null/empty; the pasted trace's VM re-fits them from `SourcePath` in its ctor
   (`EnsureLoadpullSurface` + `RebuildContour`). Copying the authoring params (MetricName, constraint, levels,
   colors, colormap, kernel, etc.) is what matters.
2. **Copy it in the `Trace(Trace src, ...)` ctor:** `ContourData = src.ContourData?.Clone();`
3. **Ensure the pasted VM re-fits.** When the copied plot's traces get their `TraceRowViewModel`s built, the
   contour VM ctor already calls `EnsureLoadpullSurface()` + `RebuildContour()` when `ContourData != null` and
   `SourcePath` resolves (the copy ctor carries `SourcePath`). Verify the paste path goes through
   `RebuildTraces`/VM construction so the surface rebuilds. After the fit, `_plot.Autoscale(force:true)` frames
   it (mirror `AddContourTrace`). If the paste path doesn't autoscale post-fit, add it.
**Gate (headline):** copy a Rect contour plot, paste → the pasted plot shows the SAME contour with the SAME axis
scaling; same for Smith. Add a test: clone a contour `Trace`, assert `IsContourTrace` and that authoring params
match the source.

> This is the highest-priority bug in the round. Land + test first.

## §4 — Default colormap 'Bone'; inherit colormap from previous trace on same plot; line color matches [CHANGE]
- Change `ContourData.ColorMap` default to `ContourColorMap.Bone`.
- In `AddContourTrace`: if the plot already has a contour trace, **inherit** the most-recent contour trace's
  `ColorMap` (and arguably its style) for the new trace; else default to `Bone`. Set
  `newCd.ColorMap = existingContour?.ContourData.ColorMap ?? ContourColorMap.Bone`.
- **Line color matches the colormap** at add-time: set the new trace's `LineColor` to a colormap-derived color
  (and leave `LineColorOverridden = false` so §5/round-5's colormap-coupled line color applies). Since round-5
  derives the line color from the colormap when `!LineColorOverridden`, the main requirement is: at creation,
  ensure `LineColorOverridden = false` and `ColorMap = Bone` so the rendered line matches Bone. If you also want
  the stored `LineColor` seeded, set it from `ContourColormaps.Sample(Bone, <contrast position>)`.
**Gate:** first contour added to a plot uses Bone with a Bone-matching line color; a second contour on the same
plot uses the first's colormap.

## §5 — Label box: add a thin subtle black stroke outline [CHANGE]
In `DrawIsoLines`, after filling the label background rect (`bgPaint`), also stroke its outline with a thin
subtle black: a second `SKPaint { Style = Stroke, Color = SKColor(0,0,0,~120), StrokeWidth = ~0.75f }`, drawn on
the same `bg` rect. Apply the same fade factor to the stroke alpha as the box fill (so it fades with the line
when fade is on). No new model field needed. **Gate:** label boxes have a faint black border; it fades with the
line when fade is on.

## §6 — Default label text = black; default label background = white [CHANGE]
- `ContourData.LabelForeground` default → `SKColors.Black`.
- `ContourData.LabelBackground` default → `SKColors.White` (opaque, or white at high alpha e.g. 230 if you want
  slight translucency — prefer opaque white per the request). Update the VM field initializers
  (`_contourLabelForeground`, `_contourLabelBackground`) to match so the swatches show the new defaults. (These
  are read from `cd` in the VM ctor, so changing the model defaults flows through for NEW traces.)
**Gate:** a new contour's labels render black-on-white by default; swatches show black text / white bg.

## §7 — Disallow Metric-at-constant-Same-Metric (grey out the current metric) [CHANGE]
In the constant-metric `ComboBox`, the item equal to the trace's currently-selected **Metric** must be disabled
(greyed out) so the user can't constrain a metric by itself (degenerate). Implement by projecting
`AvailableMetrics` into an item type with an `Enabled` flag (disabled when the name matches `ContourMetricName`,
using the alias-normalization from round-5 §9 so e.g. Pout-vs-Pout aliases are also caught), and bind the combo
to that. When `ContourMetricName` changes, refresh the disabled flags; if the currently-selected constant metric
becomes the same as the metric, auto-pick the next valid one (ties into §8). **Gate:** with Metric = Pout, the
constant-metric dropdown greys out Pout (and its aliases); the user can't select it.

## §8 — Constant-metric mode must always show a constant metric (never blank-render) [CHANGE]
When the user switches the constraint to **Constant Metric**, a valid `ConstraintMetricName` must always be
populated (never empty → which fits nothing → blank plot). On switching to ConstantMetric (and on metric change
per §7), if `ConstraintMetricName` is empty or equals the current Metric, **auto-select** the first valid
metric (first `AvailableMetrics` entry that isn't the current Metric, by alias). Generally: avoid any state that
renders a blank plot. **Gate:** selecting Constant-Metric immediately shows a populated, valid constant metric
and a non-blank contour (assuming data fits).

## §9 — Label spacing: default ~30, allow any value > 0 (currently capped at 10) [BUG]
The >10 cap is the card `NumericUpDown` Maximum. Fix:
- Card: set the Label-spacing `NumericUpDown` `Minimum` to a small positive (e.g. 0.1) and `Maximum` to a large
  value (e.g. 1000) so "anything > 0" is allowed; keep a sensible increment.
- Default: set `ContourData.LabelSpacing` default to `30.0` (and the VM field `_contourLabelSpacing = 30.0`).
  Note round-5 made label spacing world-unit-based; re-tune the px-per-unit factor so 30 gives a reasonable
  label count (with the world-unit spacing, 30 should be a usable default — confirm visually).
**Gate:** label spacing accepts values >10 (e.g. 50); default new-trace spacing ≈30; smaller → more labels.

## §10 — MXP/MXE glyphs AND iso-line labels scale WITH zoom (bigger zoomed in) [BUG — reverses round-5]
Round-5 divided glyph/label sizes by `zoomLevel` to keep on-screen size constant. The user wants them to scale
**with** zoom: at Actual (zoom=1) the current size is perfect; zoom in → larger; zoom out → smaller. Since the
canvas is already pre-scaled by `zoomLevel`, drawing at **constant canvas-px** sizes makes them scale with zoom
automatically. **Fix:** in `DrawOptimumMarker` (and the iso-line label font in `DrawIsoLines`), REMOVE the
divide-by-`zoomLevel` added in round-5 — draw at the constant base sizes (`r=7f`, label font from
`cd.LevelFontSize`, etc.). The `zoomLevel` param can be dropped from those calls (or left unused). Net: glyphs
and labels track the zoom like the rest of the plot. **Gate:** zoom in → MXP/MXE glyphs and iso-line labels get
proportionally larger; zoom out → smaller; at Actual zoom they match today's size.

> §10 literally undoes round-5 §3/§8's divide-by-zoom for these elements. Keep label spacing world-unit-based
> (round-5 §3) for POSITION; only the glyph/label SIZE reverts to canvas-px (scale-with-zoom).

## §11 — Colormap & Fill-style selectors are clipped — widen to fit full text [CHANGE]
The Colormap `ComboBox` and Fill-style selector are too narrow (text clipped L/R). Widen both so the longest
entry fits: Colormap needs to fit names like "GistHeat"/"Afmhot" (~8 chars); Fill style needs
"Topography"/"Heatmap"/"None". Set explicit `Width` (or `MinWidth`) large enough, and ensure the row layout
(ColumnDefinitions) gives them the space. **Gate:** no clipping on either selector for any entry.

> Note this is in tension with round-5 §7 ("narrow colormap to 6–7 chars on the Fill row"). Round-6 supersedes:
> make them wide enough to read fully even if that means the Fill row is wider. Prefer readability.

## §12 — Merge "Grid Pts" text into its Show button → one button labeled "Grid" [CHANGE]
Replace the separate "Grid Pts" label + Show toggle with a single toggle button labeled **"Grid"** (drives
`ContourDisplayGridPoints`). Keep the grid-point color swatch and size box on the same row. **Gate:** one "Grid"
button toggles grid points; no separate label.

## §13 — New trace ShowLabels: false on Smith/Polar, true on Rect [CHANGE]
In `AddContourTrace`, set `DrawLabels = (plane == SurfacePlane.Z)` — i.e. true on Rect, false on Smith/Polar.
(Mirror the `FadeLineOpacity = (plane == Gamma)` pattern.) The VM ctor copies `cd.DrawLabels` into
`_contourShowLabels`. **Gate:** a new Smith/Polar contour starts with labels off; a new Rect contour starts with
labels on.

## §16 — Constant-metric row: narrow value box + units suffix text [CHANGE]
For the Metric-at-constant-metric (and compression) constraint row:
- **Narrow the `ConstraintValue` edit box** to ~4–5 digits, leaving room for a units label to its right.
- **Add a units text label** to the right of the value box, derived from the **constraint metric** (or
  "Compression" → "dB"):
  - **dB** — Compression, Gain, Gt, Gp, Gp_dB, Gt_dB (gain-like, dB).
  - **%** — Eff, DE, DEff, PAE, Efficiency, Drain Efficiency (efficiency-like).
  - **dBm** — Pout, power-like.
  - **"" (empty)** — anything else / unknown.
  Use the round-5 alias-normalization so vendor variants map correctly. Add a VM read-only property, e.g.
  `ConstraintUnits`, computed from the active constraint (Compression → "dB"; else from
  `ConstraintMetricName` via the alias→unit table), and bind a `TextBlock` to it. Recompute when the constraint
  kind / constant-metric changes.
**Gate:** the constraint value box is compact with a units label that reads "dB"/"%"/"dBm" appropriately for
the selected constraint metric (across vendor aliases), empty when unknown.

---

## Slice plan (compile-and-test-gated)
- **6a — serious bugs:** §3 (copy/paste `ContourData.Clone` + ctor copy + re-fit), §2 (MXP/MXE kernel thread).
  Land + test first (both have unit tests).
- **6b — defaults:** §4 (Bone + inherit + line match), §6 (black/white label), §9 (spacing default+range),
  §13 (DrawLabels per plane). Model + VM + AddContourTrace.
- **6c — render:** §1 (Γ-disk clip for smooth Smith fill), §5 (label-box stroke), §10 (glyph/label scale-with-
  zoom — remove round-5 divide). Owner-verified.
- **6d — card:** §7 (disable same-metric), §8 (always-populated constant metric), §11 (widen colormap/fill),
  §12 (merge Grid button), §16 (narrow value + units suffix). Owner-verified.

## Constraints / gotchas
- §3: `Clone()` must NOT carry derived `Grid`/`Levels`/cache — re-fit from `SourcePath`, else two plots share
  surface state. Verify the paste path constructs VMs (which re-fit) and autoscales after.
- §1: circular clip only for `plane==Gamma`; keep vector (SKPath clip) for PDF/SVG.
- §2: forward kernel/smooth/epsilon through `MaxPower`/`MaxEfficiency`/`GetMxx`/`RecommendedBox`; VM passes
  `cd.InterpKernel/Smoothing/Epsilon`.
- §10 reverses round-5's divide-by-zoom for glyph/label SIZE only; POSITION (label spacing) stays world-unit.
- §11 supersedes round-5 §7's "narrow colormap" — readability wins.
- §7/§8/§16 share the round-5 alias-normalization table — reuse it (don't duplicate); consider a shared static
  metric-alias helper if it isn't already one.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests
- RfCore `MaxPower`/`MaxEfficiency`: result varies with kernel (Multiquadric vs ThinPlate) on a synthetic
  surface; epsilon/smooth forwarded.
- `ContourData.Clone()`: deep-copies authoring/style fields, leaves Grid/Levels/caches null.
- `Trace` copy ctor: `IsContourTrace` preserved; cloned `ContourData` not reference-equal to source.
- Defaults: `ColorMap == Bone`, `LabelForeground == Black`, `LabelBackground == White`, `LabelSpacing == 30`.
- `AddContourTrace`: second contour inherits first's colormap; Smith → DrawLabels false, Rect → true.
- VM: constant-metric mode never leaves `ConstraintMetricName` empty; same-metric is disabled.
- `ConstraintUnits` returns dB/%/dBm/"" across aliases.
- Owner-verified: smooth Smith fill edge, label-box stroke, glyph/label scale-with-zoom, card widths.
