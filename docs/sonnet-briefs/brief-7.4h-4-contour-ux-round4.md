# Brief 7.4h-4 — Contour UX round 4: swatch rendering, label chain, MXP/MXE glyphs, colormap-coupled lines, card consolidation, two serious bugs

**Phase:** 7.4h round 4. 15 items. Several are small once the root cause is known; three are substantive
(label render chain §B, MXP/MXE glyph redesign §D, the trace-wipe-on-source-switch bug §O). Group by area below.
**Design:** `docs/design/loadpull-contours.md` §2.5.
**Builds on:** 7.4h-1/-2/-3 (all landed).

**Verified anchors (on disk):**
- `SkColorToAvaloniaColorConverter` (key `SKC`): returns an **Avalonia `Color`**, not a brush.
- Card swatches bind `Background="{Binding ContourGridPointColor, Converter={StaticResource SKC}}"` on a
  `Border` — **`Border.Background` wants `IBrush`; a `Color` does not implicitly convert → swatch renders nothing**
  (§A root cause).
- `ContourRenderer.DrawIsoLines(...)`: hard-codes `bgPaint = SKColor(0,0,0,140)` and `labelPaint.Color =
  lineColor`; never reads `cd.LabelBackground`/`cd.LabelForeground`/`cd.LabelSpacing`. Fade branch fades
  `linePaint`+`labelPaint` but NOT `bgPaint`. One label per polyline at `pts.Count/2` (midpoint) → labels align
  (§B).
- `ContourRenderer.DrawOptimaMarkers`/`DrawOptimumMarker`: cross + text-beside-it, hard white/green (§D).
- `PlotRenderer.Draw` contour block calls `DrawIsoLines(canvas, canvasSize, polylines, tf, cd.LineColor,
  cd.StrokeWidth, cd.DrawLabels, (float)cd.LevelFontSize, cd.FadeLineOpacity)` — does NOT pass label colors,
  spacing, or colormap (§B/§E wiring point).
- `AxesRenderer.DrawComplexXLabels`: builds the `freq (min to max …)` X-label **per trace, for every trace incl.
  contour** — ignores `IsContourTrace` and ignores `Plot.XLabel`. This is why Smith/Polar shows freq labels when
  a contour's metric is invalid (the grid is empty so the label isn't hidden under fill) (§N root cause).
- `PlotInspectorViewModel.OnLibraryChanged`: removes "stale" trace VMs. For a **contour trace**, `IsCubeBound`
  is false and `t.Data` is the placeholder SNP (never in `librarySnps`) → the `else` branch flags it stale →
  **any `LibraryChanged` (incl. loading a file on source-switch) deletes every contour trace** (§O root cause).
- `ContourData`: has `LineColor`, `LabelBackground`, `LabelForeground`, `LabelSpacing`, `ColorMap`,
  `GridPointColor`, `GridPointSize`, `LevelFontSize`, `MxpCoord`/`MxeCoord`, `DisplayMxp/Mxe`. `ContourColormaps.Sample(map, t)` exists.
- Card Options layout (current order): MXP/MXE row, Labels/Lines/Fill row, Grid-pts row (+ size), Label Bg/Text
  row, Kernel, Smooth+Eps, Label spacing, Colormap. Line color swatch + Fade live in a line row.

---

## §A — Swatch buttons render no color (Iso-line, Grid point, Label bg, Label text) [BUG]
Root cause: the `SKC` converter returns `Color`, but the swatch `Border.Background` needs an `IBrush`.
**Fix (one line, fixes all four):** change `SkColorToAvaloniaColorConverter.Convert` to return a
`SolidColorBrush` (an `IBrush`):
```csharp
public object? Convert(...)
    => value is SKColor c ? new SolidColorBrush(new Color(c.Alpha, c.Red, c.Green, c.Blue))
                          : (object?)new SolidColorBrush(Colors.Transparent);
```
(Keep `ConvertBack` returning `SKColor` from a `Color`, but it now must accept a `SolidColorBrush` too if used —
ConvertBack isn't used for these one-way swatch bindings, so leaving it is fine; if the analyzer complains,
handle `SolidColorBrush`.) **Gate:** all four swatches show their actual colours and update live.

## §B — Label render chain: bg/text color, box fade, spacing, stagger [BUGS]
All in `ContourRenderer.DrawIsoLines` + its `PlotRenderer` call site. Pass the missing fields and use them.
**Wiring:** extend `DrawIsoLines` signature to also take `SKColor labelBg`, `SKColor labelFg`, `double
labelSpacing`; pass `cd.LabelBackground`, `cd.LabelForeground`, `cd.LabelSpacing` from `PlotRenderer.Draw`.

1. **Label text color** — set `labelPaint.Color = labelFg` (NOT `lineColor`). (Item: "label text rendered using
   line color".)
2. **Label box color** — set `bgPaint.Color = labelBg` (NOT hard-coded `0,0,0,140`).
3. **Label box fades with the line** — in the fade branch, also fade `bgPaint`: `bgPaint.Color =
   labelBg.WithAlpha((byte)(labelBg.Alpha * (1-t)))`, same `t` as the line/text. (Item: "label box is not fading".)
   Apply per-label (the bg rect is drawn per label, so set bgPaint alpha right before `DrawRect`).
4. **Stagger label positions** — labels currently sit at each polyline's midpoint, so adjacent rings line up.
   Stagger by offsetting the label index per ring: instead of always `mid = pts.Count/2`, vary the fractional
   position per polyline, e.g. `frac = 0.5 + 0.18*((ringIndex % 3) - 1)` clamped to `[0.15,0.85]`, `idx =
   (int)(pts.Count*frac)`. Track a per-call ring counter (increment per labelled polyline). Goal: the labels of
   successive iso-lines do not form a straight radial line.
5. **Label spacing (Matlab semantics)** — `LabelSpacing` = spacing in points between labels **along** a contour
   line; smaller → more labels. Currently exactly one label per polyline. Implement: walk each polyline
   accumulating canvas-space arc length; place a label every `LabelSpacing` points (convert points→canvas px:
   1 pt = 1/72 inch; use a sensible px-per-point of the render, or treat `LabelSpacing` directly as px for now
   and note the approximation). So a long ring gets multiple labels spaced ~`LabelSpacing` apart; a short ring
   gets one (or none if shorter than `minLabelLen`). Combine with stagger by offsetting the first label's
   arc-length start per ring. **Gate:** smaller LabelSpacing → more labels per line; labels of adjacent rings
   are staggered, not collinear; label bg/text colours apply and the box fades with the line.

> Keep `minLabelLen` skip. Keep vector. Reading `LabelSpacing` removes its "does nothing" status — remove any
> `// DEFERRED` note on it.

## §C — Card: move grid-point dot radius up to the grid row; shrink the box [CHANGE]
Move the "Grid pt size" `NumericUpDown` onto the **same row** as the grid-point Show toggle + color swatch
(the grid row). Make the numeric box small — wide enough for **2 digits** only (e.g. `Width="34"`, max 12).
**Gate:** grid options (Show, color, size) sit on one row; size box is compact.

## §D — MXP / MXE glyph redesign [CHANGE]
Replace `DrawOptimumMarker`'s cross+text with a **filled circle + thin black ring + centered letter**:
- **MXP:** a filled circle in a colour that **accents** the chosen colormap (stands out from it), a thin black
  outline ring, and the letter **"P"** centered (horizontally centered, vertically middle) exactly at the MXP
  impedance point.
- **MXE:** identical but a **different** accent colour (also coordinated with the colormap) and the letter
  **"E"**.
- **Accent colour from colormap:** derive two distinct accent colours from `cd.ColorMap`. Simple approach:
  sample the colormap at two positions far apart and/or take a high-contrast complement. E.g. MXP =
  `ContourColormaps.Sample(map, 0.15)` pushed toward saturation, MXE = `Sample(map, 0.85)` — but they must
  **stand out** from the fill, so consider using a fixed high-contrast pair tinted by the map, or the map's
  endpoints brightened. Pick something legible on the fill; a reasonable default: MXP = bright variant of the
  low-end colour, MXE = bright variant of the high-end colour, each with a black ring for separation.
- **Geometry:** circle radius ~7px; black ring stroke ~1.5px; letter in `SkiaFonts.PlexBold` sized to fit
  (~9px), drawn with `SKTextAlign.Center` at the circle center, vertically centered via font metrics (center
  on `pt.Y + (textHeight/2 - descent)` so the glyph's optical middle is at the point). The circle CENTER (and
  thus the letter) sits exactly at the impedance point.
**Signature:** `DrawOptimaMarkers(canvas, cd, tf)` already has `cd` → pass `cd.ColorMap` into the per-marker
draw; replace `DrawOptimumMarker(canvas, coord, label, color, tf)` with one that takes the letter + accent.
**Gate:** MXP shows a colormap-accented filled circle with black ring and centered "P" exactly at the optimum;
MXE same with its own accent colour and "E".

## §E — Iso-line color follows the colormap (contrast near peak/trough) [CHANGE]
When the user changes the colormap, the iso-line colour should **track** the colormap such that lines
**contrast** near the contour peak/trough (so they're visible at the points of interest) and may blend with the
colormap toward the contour edges (less important there). The user can still override via the Iso-Line color
picker.
**Approach (per-line color, not a single `lineColor`):** in `DrawIsoLines`, when the line colour is in
"auto/colormap" mode, compute each polyline's stroke colour from its level's normalized position `t∈[0,1]`
(0 = outer/edge, 1 = inner/peak). Near the peak (`t→1`) use a **high-contrast** colour vs. the colormap at that
level (e.g. the colormap's complement, or black/white chosen by luminance of `Sample(map, t)`); near the edge
(`t→0`) let it approach `Sample(map, t)` (blends in). Interpolate between "contrasting" and "matching" by `t`.
**Override seam:** keep an explicit "line color overridden" flag on `ContourData` (e.g. add `bool
LineColorOverridden`). When the user picks a colour via the Iso-Line swatch, set `LineColorOverridden = true`
and use `cd.LineColor` flat (current behavior). When the colormap changes, if NOT overridden, leave
`LineColor` as the auto sentinel and let `DrawIsoLines` derive per-line colours. So:
- `OnContourColorMapChanged`: if `!LineColorOverridden`, just `Notify()` (renderer derives line colours).
- `OnContourLineColorChanged` (the swatch): set `LineColorOverridden = true`.
- `DrawIsoLines`: if `cd.LineColorOverridden` → flat `cd.LineColor`; else per-line colormap-contrast colour.
**Gate:** changing the colormap visibly retints the iso-lines with high contrast near the peak and blending at
the edges; picking a line colour pins it (colormap changes no longer move it).

> This composes with fade-opacity (§7.4h-3): apply the fade alpha on top of the per-line colour.

## §F — Add a line-width slider to the Line row [CHANGE]
`ContourData.StrokeWidth` exists (1.5f default). Add a VM `[ObservableProperty] double _contourStrokeWidth`
(init from `cd.StrokeWidth`, On…Changed → `cd.StrokeWidth = (float)value; Notify()`). Add a **narrow** `Slider`
(e.g. min 0.5, max 5, width ~60) to the Line row. **Gate:** dragging changes iso-line width live.

## §N — Smith/Polar X/Y labels appear when Constant-Metric is invalid [BUG]
Root cause: `AxesRenderer.DrawComplexXLabels` emits the per-trace `freq (...)` label for **every** trace,
including contour traces, regardless of `Plot.XLabel`. Fix: in `DrawComplexXLabels`, **skip contour traces** in
the per-trace loop, and if there are no non-contour traces (all contour), draw nothing (unless `hasCustomX`).
Concretely: build the trace list as `traces.Where(t => !t.IsContourTrace)`; if empty and not custom → return
after the title. (Title still draws.) The contour title comes from `Plot.Title`; contour plots never show a
freq X-label or per-trace Y-label unless the user sets a custom one. **Gate:** a Smith/Polar contour with an
INVALID constant-metric (empty grid) shows NO freq/X/Y axis labels; a custom axis label still shows; non-contour
Smith plots unchanged.

## §G/§H — Card consolidation: one Label row, one Line row; reorder [CHANGE]
**Label row (single line):** move the **Labels** toggle down to join the label-styling controls. Remove the
"Label" text label. The row becomes: **[Labels toggle] [Bg swatch] [Text swatch] [Size NUD (2-digit width)]
[Spacing NUD]**. (Size = `LevelFontSize`, shrink to ~2-digit width; Spacing = `LabelSpacing`.) All label options
on one row.
**Line row (single line):** replace the "Line" text with the line enable/disable toggle. Row: **[Line on/off
toggle] [line color swatch] [Fade toggle] [width slider (§F)]**. Must fit on one line (compact widths). The
line on/off toggle drives `ShowIsoLines` (rename intent: it's the iso-line enable).
**Reorder Options:** 1) MXP/MXE row, 2) **Fill Style selector row** immediately under it, 3) **Line row**
immediately under that, then Grid row, Label row, Kernel/Smooth/Eps, Colormap. (Owner: "Move the Fill Style
selector row immediately under MXP/MXE, and the Line row immediately under that.")
**Gate:** label options on one row (no "Label" text); line options on one row (no "Line" text); order is
MXP/MXE → Fill → Line → (Grid, Label, …).

## §I — Number-of-Isolines + Range boxes too wide [CHANGE]
- **N levels** (`ContourLevelCount`): user enters 1–99 → 2-digit width. Set the count `NumericUpDown
  Width≈"40"` (2 digits + padding).
- **Range Start/Step/Stop**: only 2–3 chars ever (e.g. "-20") → make each ~3-char width. The range grid is
  currently `ColumnDefinitions="48,Auto,48,Auto,48"`; reduce the numeric columns to ~`34`–`38` and/or set each
  NUD `Width`. Keep the `:` separators aligned.
**Gate:** N-levels and the three range boxes are compact (2–3 char), still aligned.

## §O — Switching the Data Source deletes all traces (unrecoverable) [SERIOUS BUG]
Root cause: `PlotInspectorViewModel.OnLibraryChanged` removes any trace VM it deems "stale". For a **contour
trace**, `IsCubeBound` is false and `t.Data` is a 1-point placeholder SNP that is never in `librarySnps`, so the
`else` branch (`t.Data is not null && !librarySnps.Contains(t.Data)`) marks it stale → it's removed. This fires
on **any** `LibraryChanged`, including the file-load that happens when the user picks a different source. Standard
traces bound to the previous source can be removed too when the new selection's entry set changes.
**Fixes:**
1. **Never treat a contour trace as stale here.** In the `staleVms` predicate, exclude contour traces:
   `if (rv.Trace.IsContourTrace) return false;` first. A contour trace's data lives in `ContourData` keyed by
   `SourcePath`, not by `t.Data`/`librarySnps`.
2. **Don't delete traces merely because the SELECTED source changed.** `OnLibraryChanged` should only remove a
   trace whose source file is genuinely **gone from the library** (`Entries`), not one whose source is simply
   not the currently-selected entry. Switching selection does not remove the prior entry from `Entries` (the
   library caches loaded files), so the existing path/SNP membership test *should* keep them — verify that
   switching source doesn't also evict the old entry. If switching source replaces rather than caches (check
   `SelectDataSourceAsync` / how the selected combo drives the library), make it **additive** (keep prior
   entries) so traces bound to other sources survive, OR make trace staleness depend on the file being
   physically absent, not on selection.
3. **Contour trace re-resolves against its own `SourcePath`.** A contour trace stores `SourcePath` (the
   loadpull file). `OnLibraryChanged` should, for contour traces, leave them intact and let
   `TraceRowViewModel.RebuildContour`/`RefreshDataSources` re-fit from the still-cached entry. Ensure
   `RefreshDataSources`/the contour VM re-fits when its source is reselected.
**Gate (the headline):** with traces (incl. a contour) on a plot, switch the Data Display source to a different
file and back — **all traces and their full config survive**; nothing is deleted; the contour re-renders.

> This is the most user-visible bug in the round. Add a regression test: a `PlotInspectorViewModel` with a
> contour trace + a standard trace; fire `LibraryChanged` after adding another entry; assert both traces remain.
> Also test selecting a different source then back preserves `Traces.Count` and each trace's config.

---

## Slice plan (compile-and-test-gated)
- **4a — quick bugs:** §A (converter), §N (complex X-label skip contour), §O (trace-wipe). These are the
  highest-value, smallest-diff fixes. Land + test first.
- **4b — card layout:** §C, §G/§H, §I, §F (VM+card). Compile; owner-verified.
- **4c — label render chain:** §B (colors, box fade, stagger, spacing). Owner-verified.
- **4d — glyphs + colormap lines:** §D (MXP/MXE redesign), §E (colormap-coupled iso-line colour + override).
  Owner-verified.

## Constraints / gotchas
- Keep all contour rendering **vector** (no bitmap) — colours/alpha via paint, gradients via SKShader.
- §E and §3 (fade) compose: per-line colour first, then fade alpha.
- §O: don't over-correct into never removing genuinely-missing sources — only stop removing on *selection*
  change and stop removing contour traces.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.
- HIG: compact, 2–3-char numeric boxes where specified; one-line Label and Line rows; tooltips for full names.

## Tests
- Converter returns a brush (swatch bindings render).
- `DrawComplexXLabels` emits nothing for an all-contour Smith plot (unit-test via a thin seam or owner-verify).
- `OnLibraryChanged` preserves contour + standard traces across a `LibraryChanged`/source-switch (regression).
- `DrawIsoLines` uses label bg/fg + spacing; box fades with line (owner-verified visually).
- MXP/MXE glyph + colormap-coupled line colour owner-verified.
