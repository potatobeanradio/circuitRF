# Brief 7.4h-5 — Contour UX round 5: label leaks, zoom-independence, iso-line color, metric sort/filter

**Phase:** 7.4h round 5. 12 items. Two are correctness leaks (§1/§2 — "dB(S(1,1))" must never appear), several
are zoom-independence fixes (§3 labels, §8 glyphs), two reverse round-4 iso-line-color behaviour (§4/§5), and
two are list logic (§9 metric sort/alias, §10 non-varying-field filter). **Builds on 7.4h-1..-4 (landed).**

**Verified anchors (on disk):**
- `Plot.YLabel`: `CustomYLabelOn → return CustomYLabel` (so **on-but-empty returns ""**); else Rect-contour →
  "Imaginary (Ω)"; else "". `Plot.XLabel`: already skips Smith/Polar contour (returns "").
- `AxesRenderer.DrawTitleAndAxisLabels` (Rect only): when `plot.YLabel` is empty it **falls through to a
  per-trace fallback** — `LabelFor(t)` → `t.RectYLabel(...)` → "dB(S(1,1))" from the contour's placeholder SNP.
  This is the §1 leak (custom-Y-on-but-empty → empty `YLabel` → per-trace fallback fires for the contour trace).
- `PlotContainerViewModel.UpdateLabelStrips` (external Y-axis strips, used for **Smith/Polar**): builds one
  `LabelStripViewModel` per `leftTraces`/`rightTraces` trace; **contour traces are NOT excluded** → a strip is
  built for the contour trace and `AxisLabelControl` renders its placeholder-SNP description "dB(S(1,1))". This
  is the §2 Smith leak. Also its `hasCustomY = CustomYLabelOn && !IsNullOrEmpty(CustomYLabel)` → on-but-empty
  falls to per-trace strips (same pattern as §1).
- `ContourRenderer.DrawIsoLines`: `spacingPx = (float)(labelSpacing * 100.0)` + arc-length walk in **canvas px**
  → labels reposition on zoom (§3). Per-line `baseLineColor` computed from `ContourColormaps.Sample(colorMap,
  tPos)` lerped **per level** when `!lineColorOverridden` (§5 — must be removed; one color, fade only). Fade
  branch already correct.
- `TraceRowViewModel.OnContourColorMapChanged`: sets `cd.ColorMap` + `Notify()`, but **does NOT reset
  `LineColorOverridden`** → picking a colormap after a manual line color doesn't retint (§4).
- `TraceRowViewModel.OnContourLineColorChanged`: sets `cd.LineColor` + `cd.LineColorOverridden = true`.
- `ContourRenderer.DrawOptimumMarker`: `const float r = 7f`, font `9f` — drawn at canvas px on the
  **pre-zoom-scaled canvas**, so glyphs scale with Data Display zoom (§8). `DrawOptimaMarkers(canvas, cd, tf)`
  has no zoom param.
- `PlotRenderer.Draw(..., float zoomLevel = 1f)`: receives zoom but **does NOT forward it** into the
  `ContourRenderer.DrawIsoLines` / `DrawOptimaMarkers` calls (§3/§8 plumbing point).
- `ContourData`: `LevelMode = ContourLevelMode.Range` default (§6 → change to Count). Has `ColorMap`,
  `LineColor`, `LineColorOverridden`, `LabelSpacing`.
- Card Options: Fill-style row currently holds Labels/Lines/Fill; Colormap is a separate row lower down (§7).
- `TraceRowViewModel.RebuildMetricList`: iterates `ds.CubesIn(group)`, skips `GammaLoad`/`__`-prefixed, requires
  a `gridPoint` axis, adds keys in iteration order (no sort, no alias, no variation check) — the method to
  modify for §9/§10.
- `ContourData.LevelMode` default + `AddContourTrace` ContourData initializer (in PlotInspectorViewModel) are
  where §6 default flips.

---

## §1 — "dB(S(1,1))" leaks when custom Y-label is enabled but empty [BUG — must never happen]
Root cause: empty `Plot.YLabel` makes `DrawTitleAndAxisLabels` fall into the per-trace fallback, which renders
the contour trace's placeholder-SNP label. Fix on two fronts:
1. **Honor `CustomYLabelOn` even when empty.** In `DrawTitleAndAxisLabels`, gate the per-trace fallback: only
   draw per-trace Y labels when the user has NOT enabled a custom Y label. I.e. pass/read
   `plot.CustomYLabelOn`; when true, draw `plot.YLabel` (which may be "") and do NOT fall through to the
   per-trace loop. (Same for Y2/`CustomY2LabelOn`.)
2. **Contour traces never emit a per-trace Y label.** In the per-trace fallback loop, skip contour traces:
   when building `leftTraces`/`rightTraces` label draws, filter `!t.IsContourTrace`. Defense-in-depth: make
   `Trace.RectYLabel(...)` return "" when `IsContourTrace` so no path can leak it.
**Gate:** enable custom Y label, leave text empty → NOTHING renders for Y (no "dB(S(1,1))", no residual). Enter
text → it shows. A Rect contour with no custom label shows "Imaginary (Ω)" only (never the SNP label).

## §2 — Smith chart still shows y-axis label "dB(S(1,1))" [BUG]
Root cause: `PlotContainerViewModel.UpdateLabelStrips` builds external per-trace Y-axis strips for Smith/Polar
and does NOT exclude contour traces, so a strip renders the contour's placeholder-SNP description. Fix:
1. **Exclude contour traces from the strips.** Build `leftTraces`/`rightTraces` for strip purposes as
   `plot.LeftAxisTraces.Where(t => !t.IsContourTrace)` (and right). A contour-only Smith plot → zero strips.
2. **Honor `CustomYLabelOn` even when empty** (mirror §1): treat "custom Y enabled" as suppressing per-trace
   strips regardless of whether the text is empty. Current `hasCustomY` requires non-empty text; change so an
   enabled-but-empty custom label yields a single empty strip (or no strip) rather than falling through to
   per-trace strips.
**Gate:** a Smith contour shows NO y-axis label text; enabling custom-Y-empty shows nothing; a normal Smith
S-param plot is unchanged (still shows its per-trace strips).

> §1 and §2 are the same class of bug in two render paths (Skia margin labels for Rect, external strips for
> Smith/Polar). Fix both; add the `RectYLabel`-returns-""-for-contour defense so neither path can ever leak.

## §3 — Iso-line labels reposition on zoom [BUG]
Root cause: label spacing + arc-length placement are in **canvas pixels**, which change with zoom. Make spacing
a function of **display/world units** so positions are zoom-invariant for both Smith and Rect.
**Fix:** in `DrawIsoLines`, compute the label spacing and the arc-length walk in **world coordinates** (the
plot's data units), not canvas px. Concretely:
- Walk each polyline in **world space** (use `pl.Points` directly — they're already world coords — accumulating
  world arc length), and place a label every `LabelSpacing` **world units** (scaled by a sensible factor so the
  default `LabelSpacing≈1.0` gives a reasonable count). Convert each chosen world point to canvas via
  `tf.ToCanvas` only for drawing.
- The stagger offset (`startFrac`) stays as a fraction of the world spacing.
- The `minLabelLen` skip should also be world-based (skip polylines shorter than a world-length threshold), or
  keep a canvas-px guard purely for "too small to read" — but the label POSITIONS must be world-derived so they
  don't move on zoom.
This makes labels lock to data positions: zoom in/out moves the camera, not the labels relative to the contour.
**Gate:** create a contour, note label positions, zoom Data Display in/out → labels stay on the same contour
locations (Smith and Rect).

## §4 — Picking a new Colormap must always retint the iso-lines (override is overwritten) [BUG]
Root cause: `OnContourColorMapChanged` doesn't clear `LineColorOverridden`, so a prior manual line color sticks
and the colormap change doesn't retint. Fix: in `OnContourColorMapChanged`, set `cd.LineColorOverridden =
false` (and sync the VM's `ContourLineColor` if you surface it) before `Notify()`. Now the colormap always wins;
the user can re-pick a custom line color afterward (which re-sets `LineColorOverridden = true`).
**Gate:** pick a custom iso-line color, then change the colormap → lines retint to the new colormap (custom
choice overwritten). Pick a custom color again → it sticks until the next colormap change.

## §5 — Iso-lines must be ONE color (per-level variation is wrong; only fading allowed) [BUG]
Root cause: round-4 added per-level colormap-contrast coloring in `DrawIsoLines` (the `else` branch lerping
`Sample(colorMap, tPos)` per level). Remove it. All iso-lines share **one** color derived from the colormap (or
the override); only the fade-opacity alpha may vary per line.
**Fix:** in `DrawIsoLines`, when `!lineColorOverridden`, derive a **single** line color from the colormap for
the whole set (e.g. a high-contrast color vs. the colormap mid, or a fixed pick like `Sample(colorMap, 0.5)`
pushed to high contrast) — computed ONCE before the loop, not per polyline. When `lineColorOverridden`, use
`cd.LineColor`. Then apply only the fade alpha per line (existing fade logic). Delete the per-level
`baseLineColor` lerp.
**Gate:** all iso-lines are the same hue; with fade on, they fade with distance from the peak but never change
hue level-to-level.

> §4 + §5 together: colormap → one consistent line color, always re-derived on colormap change; override pins a
> flat color; fade is the only per-line variation.

## §6 — Default to Level Count (Range disabled by default), all plot types [CHANGE]
Change `ContourData.LevelMode` default from `Range` to `Count`. Also set `LevelMode = ContourLevelMode.Count`
in the `AddContourTrace` ContourData initializer (PlotInspectorViewModel) so every new contour (Smith/Polar and
Rect) starts in Count mode. The VM ctor copies `cd.LevelMode` into `_contourLevelMode`, so the card's Count
toggle shows active. **Gate:** adding any contour starts with Count enabled, Range disabled.

## §7 — Move Colormap onto the Fill-style row, first column, narrow [CHANGE]
In the card Options, move the Colormap `ComboBox` up to the **same row** as the Fill-style selector, as the
**first** column. Narrow it to ~6–7 chars (e.g. `Width≈"60"`). Widen the Fill-style `IconSelectButton` to
support 7 chars (e.g. enough for "Topography"/"Heatmap"). Remove the now-empty separate Colormap row.
**Gate:** Colormap (narrow) and Fill style (wider) share one row, Colormap first.

## §8 — MXP/MXE glyphs scale with Data Display zoom [BUG]
Root cause: `DrawOptimumMarker` uses constant canvas-px sizes (`r = 7f`, font `9f`), but the canvas is
pre-scaled by the Data Display zoom, so the glyphs grow/shrink with zoom. The size at **Actual (zoom = 1)** is
correct, so divide the glyph dimensions by `zoomLevel`.
**Fix:** plumb `zoomLevel` from `PlotRenderer.Draw` into `ContourRenderer.DrawOptimaMarkers` →
`DrawOptimumMarker`, and divide the circle radius, ring stroke, and font size by `zoomLevel` (so at zoom = 2 the
glyph is drawn at half the canvas-px size, keeping its on-screen size constant). Clamp to a sane minimum.
**Gate:** zoom the Data Display in/out → the MXP/MXE glyphs stay the same on-screen size; at Actual zoom they
look exactly as now.

> §3 and §8 both need `zoomLevel` forwarded from `PlotRenderer.Draw` into the `ContourRenderer` calls. Add a
> `float zoomLevel` parameter to `DrawIsoLines` and `DrawOptimaMarkers` and pass `zoomLevel` at the call sites.
> (§3 uses it only if you keep any px-based guard; the positions come from world units regardless.)

## §9 — Metric combobox: priority sort + cross-vendor alias inference [CHANGE]
Sort `AvailableMetrics` (in `RebuildMetricList`) by user-interest priority, inferring aliases so different
vendors' header names map to the same concept. Priority order:
1. **Pout** (output power)
2. **Efficiency** (Drain Efficiency) — aliases: `DE`, `DEff`, `Eff`, `DrainEff`, `Efficiency`
3. **Gain (Gt)** — aliases: `Gt`, `Gt_dB`, `Gain`
4. **AM/PM** — aliases: `AMPM`, `trans_phase`, `transmission phase`, `transPhase`
5. **Curvilinear Angle** (deferred — leave a slot/comment; treat as low priority for now)
6. **PAE**
7. **Gp** (Power Gain) — aliases: `Gp`, `Gp_dB`
8. **Zin_real**
9. **Zin_imag**
10. everything else → **alphabetical**

**Approach:** add an alias-normalization + priority table (UI-side helper in `TraceRowViewModel`, or a small
shared static if cleaner). For each metric key, compute a `(priorityRank, originalLabel)` — `priorityRank` via
a case-insensitive alias lookup (normalize: strip `_dB` suffix where it disambiguates Gt/Gp, lowercase, trim).
Sort by `priorityRank` then alphabetical for the "everything else" bucket. **Respect the vendor's actual label**
in the combo text (don't rename to "Drain Efficiency" — show `DE`/`Eff` as-is), only use the alias knowledge to
order them. Document the alias table inline.
**Gotcha:** Gt vs Gp disambiguation — `Gt`/`Gt_dB` = Gain (rank 3); `Gp`/`Gp_dB` = Power Gain (rank 7). Match
the exact stem, not a substring (so `Gp_dB` doesn't match `Gt`).
**Gate:** with a multi-metric loadpull source, the Metric combo lists Pout, Efficiency-alias, Gain-alias,
AM/PM-alias, PAE, Gp-alias, Zin_real, Zin_imag first (in that order, using the library's own names), then the
rest alphabetically.

## §10 — Filter out non-varying / non-plottable fields [CHANGE + design]
Some loadpull fields don't vary across the sweep (e.g. `Ideal_GaN_FET_1p6mm_1p8GHz.spl` has `BiasBSrc`/`Zload`
constant) so contouring them is meaningless. Filter them from the Metric list.
**Recommended design (keep the math in RfCore — firewall; it's data analysis, not UI):**
- Add a headless RfCore helper, e.g. `LoadpullSurface.MetricVaries(string metricName, int freqIdx)` **or** a
  static `bool CubeVaries(DataCube cube, double epsilon = 1e-9)` in RfCore that returns false when the cube's
  values are (near-)constant across the `gridPoint` (and `pinStep`) axes — i.e. `max - min <= epsilon *
  max(1, |mean|)` (relative tolerance so large constants aren't misjudged). NaN-only or empty → also filtered.
- `RebuildMetricList` calls this per candidate cube and **skips non-varying** cubes (in addition to the existing
  `GammaLoad`/`__`/`gridPoint`-axis filters). Keep the check cheap (single pass; early-out once two distinct
  values seen).
- Edge cases to honor: a metric that varies at one frequency but not another — check at the **currently
  selected freq** (or "varies at ANY freq" → keep; simpler and safer is per-selected-freq, but that means the
  list can change with freq — prefer "varies at any freq" so the list is stable). Recommend **"varies at any
  freq" = keep**, so the list doesn't churn when the user changes the freq pin.
- This belongs in RfCore because it's pure numerical analysis over the DataSet; the UI just consumes the
  boolean. Don't put the variation math in the VM.
**Why RfCore, briefly:** the architectural firewall keeps contour/surface MATH headless; a "does this field
carry information" check is the same kind of data analysis as the RBF fit, and may be reused by CLI/tests. The
VM stays a thin consumer.
**Gate:** loading `Ideal_GaN_FET_1p6mm_1p8GHz.spl` no longer lists `BiasBSrc`/`Zload` (or any constant field)
in the Metric combo; varying metrics still appear.

---

## Slice plan (compile-and-test-gated)
- **5a — label leaks (§1, §2):** highest-value correctness. Honor `CustomYLabelOn`-when-empty in BOTH
  `DrawTitleAndAxisLabels` and `UpdateLabelStrips`; exclude contour traces from per-trace Y labels in both;
  `RectYLabel` returns "" for contour. Land + test first.
- **5b — iso-line color (§4, §5) + Level Count default (§6):** remove per-level color, single colormap color,
  colormap-always-wins, Count default. Owner-verified.
- **5c — zoom-independence (§3, §8):** plumb `zoomLevel` into `ContourRenderer`; world-unit label spacing;
  divide glyph dims by zoom. Owner-verified across zoom.
- **5d — card layout (§7):** Colormap onto Fill row, widths.
- **5e — metric list (§9, §10):** priority+alias sort in `RebuildMetricList`; RfCore variation helper + filter.
  Includes an RfCore test for `CubeVaries`/`MetricVaries`.

## Constraints / gotchas
- §1/§2: the two leak paths are independent (Skia margin vs external strips) — fix both, plus the `RectYLabel`
  defense, or it'll resurface.
- §3/§8 share the `zoomLevel` plumbing — do them together in 5c.
- §5: derive the single line color ONCE before the polyline loop; don't leave the per-level lerp in any branch.
- §10: keep the variation math in RfCore (firewall); relative epsilon so large constants aren't misread as
  varying; "varies at any freq" so the list is stable across freq changes.
- §9: respect vendor labels in the displayed text; alias table only orders. Disambiguate Gt vs Gp by stem.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests
- `Plot`/renderer: custom-Y-on-but-empty → no Y text on Rect AND Smith; contour never emits per-trace Y label
  (unit-test `RectYLabel` returns "" for a contour trace; owner-verify the render).
- `UpdateLabelStrips`: contour traces excluded from strips (assert strip count 0 for a contour-only Smith plot).
- `DrawIsoLines`: single line color across levels (owner-verified); labels world-anchored (owner-verified zoom).
- Colormap change resets `LineColorOverridden` (VM unit test).
- `ContourData.LevelMode` default == Count; `AddContourTrace` sets Count.
- MXP/MXE glyph size constant across zoom (owner-verified).
- `RebuildMetricList`: priority+alias order on a synthetic metric set; non-varying cube filtered.
- RfCore `CubeVaries`/`MetricVaries`: constant cube → false; varying cube → true; relative-epsilon large-constant
  case; NaN-only → false.
