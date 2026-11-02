# Brief 7.4h-7 — Contour UX round 7: Smith fill edge, uniform zoom scaling, line-color darkness, input validation, title, drag-clip artifact

**Phase:** 7.4h round 7. 7 items. The big ones: uniform zoom-scaling of in-plot contour elements (§2),
the drag-clip text artifact (§7), and the Smith-fill edge (§1, evaluating the user's marching idea).
**Builds on 7.4h-1..-6 (landed).**

**Verified anchors (on disk):**
- **Canvas is ALREADY zoom-scaled.** `PlotControl.Render` builds the draw op with `new Rect(Bounds.Size)`;
  `Bounds` = the control size, which the container sets to `Width*ZoomLevel × Height*ZoomLevel`. So
  `canvasSize` passed to `PlotRenderer.Draw` already grows with zoom. Grid lines scale because
  `AxesRenderer.LineWidth(canvasSize) = min(W,H)/200` (proportional to canvas), and Smith circles scale because
  they're drawn via `tf.ToCanvas` (viewport ∝ canvas). **`zoomLevel` passed into `Draw` is REDUNDANT for
  sizing** — the canvas already encodes zoom.
- `ContourRenderer.DrawOptimumMarker`: `r=7f, sw=1.5f, fs=9f` — **constant canvas-px**, so they stay a fixed
  device size while the zoomed canvas grows around them → glyph looks small zoomed-in, big zoomed-out (§2 root
  cause). Same pattern: `DrawIsoLines` label font (`levelFontSize`), iso-line `strokeWidth`, and
  `DrawGridPoints` `pointRadius` are all constant-px → all under-scale on zoom.
- Round-6 §10 "removed the divide-by-zoom" — but constant-px is still wrong because the canvas itself is the
  zoom mechanism. The correct lever is **canvas-size-proportional** sizing (like `LineWidth`), NOT `zoomLevel`.
- `DrawTopoMapFill` (round-6): already clips to the Γ-disk via `ClipPath(circle, antialias:true)` on
  `plane==Gamma`. The fill polygons themselves are still built only from **non-NaN grid cells**, and the grid
  (`Resample`, res=50) spans the MXP/MXE view box, which is **smaller than the Smith unit disk** — so on Smith
  the fill's data coverage stops inside the chart and the clip just rounds whatever reaches the edge (§1).
- `ContourColormaps.Sample`: Gray mid=(128,128,128); Bone mid≈bluish-grey; Winter mid=(0,128,191);
  GistHeat mid≈(255,160,0); Copper mid≈(204,128,80). `DrawIsoLines` derives the line color from
  `Sample(map,0.5)` then lerps **50%** toward luminance-contrast — not enough to darken these five (§3).
- `ContourData.TitleString()`: composes `"{metric} ({unit}) at Constant {constraintMetric}={val} {unit}"` — if
  `ConstraintMetricName == MetricName` (or aliases), the title reads "Efficiency at Constant Efficiency" (§6).
  `Plot.Title` is a getter recomputed each render, so the stale value means `cd.ConstraintMetricName` actually
  holds the same metric at render time → the round-6 §7/§8 invariant (constant-metric ≠ metric) isn't fully
  enforced for the title path.
- `PlotControl.PlotDrawOperation.Render`: only `canvas.Clear(Transparent)` when `_plot is null`; in the normal
  path it does NOT clear and does NOT clip to bounds. splotRF's is **identical** — so the drag-clip fix is NOT a
  draw-op clear difference; it's edge-bleed of text drawn near the control bounds during container moves (§7).
- Text edit boxes bind `ConstraintValue` (double) etc. — Avalonia `NumericUpDown`/`TextBox` two-way bindings to
  non-nullable doubles can throw/!commit on partial input ("-", "", "1.") (§4/§5).

---

## §1 — Smith filled-contour edge: extend grid to chart extent, then clip (evaluate user's idea) [CHANGE]
**User's proposal:** evaluate the contour grid over the full Smith x/y limits (not the smaller MXP/MXE view
box), run marching squares/triangles over everything, then clip the polylines/fill at the Smith boundary →
smooth edge. **Assessment: yes, that's the right direction, with a caveat.** The blocky look comes from the
fill's data grid being *smaller than the disk* (it spans the recommended view box), so the fill stops short of
the edge and the round-6 circular clip only rounds the corner of a too-small square. Fix:
1. **Resample the surface over a box that covers the whole Γ-disk** for Smith/Polar fill (i.e. `[-1,1]×[-1,1]`
   in Γ, or the chart's current axis window if zoomed), not just `RecommendedBox`. The RBF can be evaluated
   anywhere; outside the measured-node radius it returns NaN (already masked), so cells beyond the data are
   dropped and only the disk-covering region fills. Pass an explicit full-disk `ViewBox` to
   `Resample` for the fill grid (keep the tighter box for the autoscale/recommended view, but fill over the
   disk).
2. **Keep the antialiased circular clip** (round-6) as the hard edge — now the fill actually reaches it, so the
   edge is smooth instead of a staircase ending short.
3. Optionally bump fill `resolution` (50→~80) for the disk-covering grid so bands are smooth; the clip handles
   the outer edge regardless.
   - **Note:** this is fill-only and Smith/Polar-only (Rect Z-plane is unaffected, as the user says). The
     iso-LINES already trace correctly; this is about the band fill coverage.
**Concretely:** in `PlotRenderer.Draw`'s TopoMap pre-pass for `plane==Gamma`, build the fill grid by
`surface.Resample(fit, fullDiskBox, res)` where `fullDiskBox` covers the current Smith window (or unit disk),
instead of reusing `cd.Grid` (which is the recommended-box grid). Either store a second "fill grid" on
`ContourData` or compute it in the pre-pass. Keep `cd.Grid` (recommended box) for iso-line extraction +
autoscale. **Gate:** Smith filled contour fills smoothly to the chart's circular edge with no staircase; iso-
lines unchanged; Rect unchanged; PDF export still smooth (vector clip).

> If storing a separate fill grid is too heavy, the minimal version: change the fill `Resample` call to use a
> disk-covering box and higher res, computed on the fly in the pre-pass. Owner-verify the visual.

## §2 — Uniform zoom scaling: ALL in-plot contour elements scale with the grid [BUG]
**Principle (user):** everything inside the plot must scale by the same factor so zoom looks like a true
camera zoom. The grid scales because its sizes derive from `LineWidth(canvasSize)=min(W,H)/200`. The contour
glyphs/labels/dots/line-width use **constant canvas-px**, so they DON'T scale with the (already-zoomed) canvas.
**Fix — size every in-plot contour element proportionally to the canvas, exactly like the grid:**
- Compute a scale reference inside `ContourRenderer` from `canvasSize`: `float lw =
  AxesRenderer.LineWidth(canvasSize);` (= min(W,H)/200), or pass `lw` in. Define a base reference (the
  canvas size at zoom=1 is already baked into `canvasSize`), so sizing `∝ lw` makes elements scale identically
  to grid lines.
- **MXP/MXE glyph** (`DrawOptimumMarker`): replace constants with `lw`-proportional sizes, e.g. `r = 7f *
  (lw / BaseLw)`, ring `1.5f*(lw/BaseLw)`, font `9f*(lw/BaseLw)`, where `BaseLw` is the `lw` at the design
  (Actual-zoom) canvas size — OR simpler, express directly as multiples of `lw` tuned so Actual zoom matches
  today (e.g. `r ≈ 3.5*lw`, font ≈ `4.5*lw`). Tune so zoom=1 reproduces the current good size.
- **Iso-line labels** (`DrawIsoLines`): make `levelFontSize` effective size `∝ lw` (the card's `LevelFontSize`
  becomes a multiplier on `lw`, or scale the passed px by `lw/BaseLw`).
- **Iso-line width** (`strokeWidth`): scale by `lw/BaseLw` so line thickness tracks zoom like grid lines.
- **Grid-point dots** (`DrawGridPoints` `pointRadius`): scale by `lw/BaseLw`.
- **Drop the redundant `zoomLevel` param** from these calls — the canvas size IS the zoom; using `zoomLevel`
  would double-count. (Leave `zoomLevel` plumbing only if needed elsewhere; for sizing, use `canvasSize`/`lw`.)
**Pick BaseLw** = `min(W,H)/200` at the design canvas size where the current constants (7f/9f/1.5f/2.5f) look
right; bake that ratio so zoom=1 is unchanged and zoom in/out scales proportionally.
**Gate:** zoom in → MXP/MXE glyphs, iso-line labels, iso-line width, and grid-point dots all grow in the SAME
proportion as the Smith grid lines; zoom out → all shrink together; at Actual zoom everything matches today.
Nothing in the plot stays a fixed device size.

> This is the unifying fix for the user's "check Grid Pts and iso-linewidth too" — all four element classes get
> the same `lw`-proportional treatment.

## §3 — Iso-line color too light for Gray, Bone, Winter, GistHeat, Copper [BUG]
Root cause: the single line color = `Sample(map,0.5)` lerped only 50% toward contrast — for these five the
midpoint is a light/medium tone and 50% isn't enough to darken it; the working maps happen to yield saturated
midpoints that already contrast. **Fix (preserve the working maps, darken the failing ones):**
- After computing the candidate line color, enforce a **luminance ceiling**: if the line color's luminance is
  above a threshold (e.g. > 0.45), darken it (scale RGB down, or increase the contrast-lerp factor) until it's
  below the ceiling. Maps that already produce a dark/saturated contrast color pass through unchanged; the five
  light ones get pushed darker.
- Equivalent alternative: increase the contrast-lerp from 0.5 → a value derived from the midpoint luminance
  (lighter midpoint → lerp further toward black). E.g. `lerpAmt = clamp(0.5 + (lum-0.4)*1.2, 0.5, 0.95)`.
- Keep the line readable on the fill (don't force pure black if the fill is also dark — but for these five the
  fills are light enough that a darker line reads well). Tune against Gray/Bone/Winter/GistHeat/Copper.
**Gate:** Gray, Bone, Winter, GistHeat, Copper iso-lines are clearly dark/visible; Hot, Spring, Cool, Autumn,
Summer, Pink, Wistia, Afmhot (the already-good ones) are visually unchanged.

## §4/§5 — Graceful input validation on all text/value edit boxes [BUG]
The `ConstraintValue` box (and any other numeric `TextBox`/`NumericUpDown` two-way-bound to a non-nullable
double) can throw or fail to commit on partial/invalid input ("", "-", "1.", "abc"), which surfaces as Avalonia
binding exceptions or stuck values. **Fix — make every contour-card text edit box validate gracefully:**
- For numeric entry, prefer `NumericUpDown` (which parses internally) over raw `TextBox`+converter; ensure
  `Minimum`/`Maximum` set and `ParsingNumberStyle` appropriate, and that an empty/partial value doesn't throw
  (NumericUpDown tolerates this and reverts on blur).
- For any `TextBox` bound to a number via converter, the converter's `ConvertBack` must catch parse failure and
  return `BindingOperations.DoNothing` (or `Avalonia.Data.BindingNotification` with the old value) instead of
  throwing — so a half-typed value never crashes the binding; commit on Enter/blur only.
- Audit ALL contour-card edit boxes (constraint value, level start/step/stop, count, label spacing, grid size,
  level font, smoothing, epsilon, line width): each must (a) not throw on partial input, (b) revert to the last
  valid value on blur if unparseable, (c) clamp to allowed range.
**Gate:** typing partial/garbage into any contour edit box never throws or logs a binding error; on blur it
reverts to the last valid value or clamps; valid input commits normally.

## §6 — Plot title can show "X at Constant X" (stale/degenerate) [BUG]
Even with round-6 §7/§8, a sequence of selections can leave `cd.ConstraintMetricName == cd.MetricName` (or an
alias), so `TitleString()` renders "Efficiency (%) at Constant Efficiency=…". Fix at the **invariant** level so
the title can never be degenerate:
- Enforce in the VM that `ConstraintMetricName` is never equal (by alias-normalization) to `MetricName` when in
  ConstantMetric mode: whenever `MetricName` changes, if the constant metric would collide, auto-pick the next
  valid metric (round-6 §8 logic) — and make sure this fires for EVERY path that changes either field
  (including the disable-same-metric path in §7 of round-6). The title is a symptom; fixing the invariant fixes
  the title.
- Defense-in-depth in `TitleString()`: if `ConstraintMetricName` normalizes to the same concept as
  `MetricName`, fall back to a non-degenerate title (e.g. drop the "at Constant …" clause, or show the
  compression form) rather than printing the contradiction. This guarantees the title is never wrong even if a
  transient state slips through.
**Gate:** no sequence of metric / constant-metric selections produces a "X at Constant X" title; the constant
metric is always a different quantity than the plotted metric.

## §7 — Drag-move leaves a clipped text artifact at the Plot Control edge [BUG]
When axis labels reach the edge of the PlotControl, dragging a plot leaves a residual text fragment smeared on
the DataDisplay canvas. The draw op currently never clears its bounds in the normal path and never clips to
bounds, so antialiased text at/over the edge can leave residue, and the moved container's vacated region isn't
fully repainted. **Fix (the splotRF-proven approach — apply here):**
1. **Clear the bounds at the start of every draw**, not just when `_plot is null`. In
   `PlotDrawOperation.Render`, before `PlotRenderer.Draw`, do
   `canvas.Clear(SKColors.Transparent)` (or save/clear the `_bounds` rect) so no stale pixels from a previous
   frame survive within the op's own bounds.
2. **Clip all rendering to the control bounds** so text never bleeds past the edge: wrap the `PlotRenderer.Draw`
   call in `canvas.Save(); canvas.ClipRect(new SKRect(0,0,(float)W,(float)H)); … canvas.Restore();` — labels
   drawn near the edge are hard-clipped, eliminating the 1px antialiased bleed that smears on move.
3. **Ensure the parent invalidates the vacated region on move.** When a container moves, the DataDisplay canvas
   must invalidate both the old and new bounds. If circuitRF's move path only invalidates the new position,
   add invalidation of the old rect (or invalidate the whole DataDisplay viewport during an active drag). Check
   how splotRF's DataDisplay/container move handler invalidates — mirror it. (The container/`DataDisplayView`
   move handler, not PlotControl, is where the vacated-region invalidation lives.)
**Investigate first:** confirm whether the bleed is (a) text drawn past `Bounds` (fixed by clip §7.2), or (b)
stale parent-canvas pixels from the move (fixed by §7.1 + §7.3). Likely both; apply all three. Compare against
splotRF's container-move/invalidation path since the user says it was solved there.
**Gate:** dragging a plot whose axis labels reach the edge leaves NO residual text fragment anywhere on the
DataDisplay canvas; labels are crisp and clipped at the control edge during and after the move.

---

## Slice plan (compile-and-test-gated)
- **7a — zoom scaling (§2):** the headline. `lw`-proportional sizing for glyphs/labels/line-width/grid-dots;
  drop redundant `zoomLevel` for sizing. Owner-verify across zoom.
- **7b — drag-clip artifact (§7):** clear bounds + clip-to-bounds in the draw op + parent vacated-region
  invalidation. Owner-verify drag.
- **7c — Smith fill edge (§1):** disk-covering fill grid + keep circular clip. Owner-verify.
- **7d — line darkness (§3) + title invariant (§6):** luminance ceiling on line color; constant-metric ≠ metric
  invariant + TitleString defense.
- **7e — input validation (§4/§5):** audit all contour edit boxes for graceful parse/commit.

## Constraints / gotchas
- §2: the canvas already encodes zoom — size by `canvasSize`/`lw`, NOT by `zoomLevel` (avoid double-counting).
  Tune the `lw` multipliers so Actual zoom reproduces today's sizes exactly.
- §1: fill over a disk-covering box but keep iso-line extraction + autoscale on the recommended-box grid; NaN
  masking already clips beyond measured data; circular clip stays.
- §3: enforce a luminance ceiling so only the light maps change; don't alter the already-good maps.
- §7: clip-to-bounds must not clip legitimate in-canvas labels (they're inside bounds); it only kills edge
  bleed. Mirror splotRF's parent invalidation for the vacated region.
- §6: fix the invariant at every field-change path; TitleString fallback is defense-in-depth, not the primary
  fix.
- TreatWarningsAsErrors: nullable props → locals; no unused privates; no `<`/`>` in XML doc comments.

## Tests
- Owner-verified: uniform zoom scaling (glyphs/labels/line-width/dots scale with grid), smooth Smith fill edge,
  no drag-clip artifact.
- `ContourColormaps`-derived line color: luminance ≤ ceiling for Gray/Bone/Winter/GistHeat/Copper; unchanged
  for the others (unit-test the line-color helper if extracted).
- Title invariant: constant-metric never equals metric across metric-change paths; `TitleString()` never emits
  "X at Constant X" (unit test).
- Input validation: partial/garbage input into each contour edit box doesn't throw; reverts/clamps on blur.
