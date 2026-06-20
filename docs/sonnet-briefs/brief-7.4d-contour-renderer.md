# Brief 7.4d — Contour renderer: iso-lines + filled contours (marching squares + Skia)

**Phase:** 7.4d (Data Display loadpull contours — the renderer; first UI-side sub-gate of 7.4).
**Design:** `docs/design/loadpull-contours.md` §2.1 (`ContourExtractor` in the stack), §2.5 (filled contours —
TopoMap + experimental HeatMap), §3 (7.4d), §0 "key gap" (iso-line tracing is ours to build).
**Goal:** turn a resampled metric grid (from 7.4b `LoadpullSurface.Resample` → `SurfaceGrid`) into **drawn
iso-lines** and **filled contours** on the Smith (Γ) / Z (Rect) substrate. Two parts: a **headless
marching-squares extractor** (RfCore) and the **Skia rendering** (UI), including TopoMap fill (preferred) and
an experimental HeatMap fill.
**Consumes (verified on disk):**
- `RfCore/src/Loadpull/LoadpullSurface.cs` (7.4b): `Resample(fit, box, resolution)` →
  `SurfaceGrid(double[] XSpace, double[] YSpace, double[] Values /* row-major, NaN outside Γ-disk */)`;
  `RecommendedBox(fit)`; `Fit(...)` → `LoadpullFit`; `ScatterReduction(Complex[] Coords, double[] Values, …)`
  (the measured scatter points — HeatMap needs these). `SurfacePlane { Gamma, Z }`.
- `src/Ui/DataDisplay/Renderers/PlotRenderer.cs`: the `Draw(canvas, canvasSize, plot, detail, theme, …)`
  entry; `BuildTransforms(plot, canvasSize)` → `TransformSet tf`; **`tf.ToCanvas(wx, wy, useSecondary)`** maps
  **world (Γ-plane or Z-plane) → canvas SKPoint** (this is the one mapping iso-polylines + the fill grid go
  through); `ViewportClipRect(tf.Viewport, canvasSize)`; the draw order is grid → clip-to-viewport → traces →
  markers. For Smith, world coords are the Γ-plane (unit disk); for Rect-Z, world coords are the Z-plane.
- `src/Ui/DataDisplay/Renderers/TraceRenderer_MarkerRenderer.cs`: `TraceRenderer.Draw(canvas, canvasSize,
  trace, tf, theme, stemMode)` — the per-trace draw a contour trace will branch from (or a sibling path).
- `RenderTheme` (colours), `SkiaFonts` (labels).

**Firewall:** the **extractor is framework-free RfCore** (no Skia/Avalonia) — emits polylines + band metadata
in world coords; **all colour + Skia drawing is `src/Ui`**. `LoadpullSurface`/`ContourExtractor` stay
colour-agnostic (design §2.5).

---

## 0. Scope split: extraction (RfCore) vs rendering (UI)

- **`ContourExtractor` (RfCore, headless, testable):** `SurfaceGrid` + level set → a list of **iso-polylines**
  (each a level value + a sequence of world (x,y) points) via marching squares. No colour, no Skia. This is
  the piece that has a math gate (reproduce a known contour set).
- **`ContourRenderer` (UI, Skia):** maps polylines world→canvas via `tf.ToCanvas`, strokes iso-lines, draws
  level labels, and fills (TopoMap / HeatMap). All colour lives here.

This split keeps the firewall and makes the hard part (marching squares correctness) unit-testable without a
canvas.

---

## 1. `ContourExtractor` — marching squares (RfCore, headless)

New file `RfCore/src/Loadpull/ContourExtractor.cs`, namespace `RfCore.Loadpull`.

```csharp
public readonly record struct IsoPolyline(double Level, IReadOnlyList<(double X, double Y)> Points, bool Closed);

public sealed record ContourLevelSet(double[] Levels);   // explicit levels

public static class ContourExtractor
{
    /// Marching squares over a row-major value grid. grid.Values may contain NaN (outside the Γ-disk);
    /// cells touching NaN are skipped (open contours at the disk boundary — correct for Smith).
    public static IReadOnlyList<IsoPolyline> Extract(SurfaceGrid grid, ContourLevelSet levels);

    /// Build a level set: N evenly-spaced levels between the grid's finite min and max (inclusive ends optional).
    public static ContourLevelSet LevelsBetween(SurfaceGrid grid, int n);

    /// Build a level set by explicit step from an anchor (e.g. every 1 dB).
    public static ContourLevelSet LevelsByStep(SurfaceGrid grid, double step, double anchor = 0.0);
}
```

**Marching squares specifics:**
- Grid is `resolution × resolution`, row-major; cell `(i,j)` has corners
  `(XSpace[i], YSpace[j])`… (match `SurfaceGrid`'s row-major convention exactly — `Resample` fills
  `idx = yi*res + xi`, so **row = y (im), col = x (re)**; verify against 7.4b `Resample` and index
  consistently). Get this indexing right — a transpose bug rotates every contour.
- Standard 16-case marching squares per cell, per level. Linear interpolation along each edge for the
  crossing point (`t = (level − v0)/(v1 − v0)`). Emit segments.
- **NaN handling:** if any of a cell's 4 corners is NaN, skip the cell (the Γ-disk clip in `Resample` NaNs
  out-of-disk cells, so contours naturally open at the disk boundary — this is correct, not a bug).
- **Saddle disambiguation:** use the standard center-average rule for the ambiguous cases (5, 10) so lines
  don't cross. Document the choice.
- **Segment stitching:** join the per-cell segments at a level into **polylines** (chain segments sharing an
  endpoint within a small epsilon). Closed loops → `Closed = true`. Open chains (hit the disk boundary or grid
  edge) → `Closed = false`. A simple endpoint hash-join is fine at resolution 50 (≤2500 cells).
- Output polylines are in **world coords** (the Γ-plane or Z-plane values from `XSpace`/`YSpace`) — the
  renderer maps to canvas. Never bake canvas coords into the extractor.

**Gate (7.4d-1, headless):** on a synthetic analytic field (e.g. `f(x,y) = x² + y²` over a known grid), the
level=`r²` contour is a circle of radius `r` — assert extracted polyline points satisfy `√(x²+y²) ≈ r` within
grid tolerance, and the loop is `Closed`. On a saddle field (`x² − y²`) assert no crossing lines. On a
real loadpull `SurfaceGrid` (from 7.4b on `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl`), assert sane polyline counts and
that NaN-disk cells produce open contours at the boundary. (Owner can eyeball against the Python/matplotlib
`contour` reference — flag a `// GOLDEN:` slot if a reference polyline CSV is supplied.)

---

## 2. `ContourRenderer` — iso-lines + labels (UI, Skia)

New file `src/Ui/DataDisplay/Renderers/ContourRenderer.cs`, namespace `CircuitRF.Ui.DataDisplay`.

```csharp
public static class ContourRenderer
{
    public static void DrawIsoLines(
        SKCanvas canvas, (double W, double H) canvasSize,
        IReadOnlyList<IsoPolyline> polylines, TransformSet tf, RenderTheme theme,
        SKColor lineColor, float strokeWidth, bool drawLabels);
    // ... fill methods in §3/§4
}
```
- Map each polyline point with `tf.ToCanvas(p.X, p.Y, useSecondary: false)` → `SKPath`; stroke with an
  antialiased `SKPaint`. (Smith world = Γ; Rect world = Z. Both go through the same `ToCanvas` — the substrate
  difference is already in the transform.)
- **Labels:** place the level value near the polyline (a midpoint or the rightmost vertex; keep it simple —
  one label per polyline, `SkiaFonts` small). Skip labels on very short polylines.
- Draw **inside the viewport clip** the same way traces do (`PlotRenderer.Draw` already clips before the trace
  loop — the contour draw must run inside that clip; see §5 integration).

**Gate (7.4d-2):** a contour trace renders iso-lines on a Smith plot that clip to the unit circle and visually
match the extractor output; labels appear; no lines escape the disk.

---

## 3. TopoMap filled contours (PREFERRED, Skia, UI) — design §2.5

Discrete elevation-style colour **bands**, one per interval between adjacent iso-levels, hard-edged.
- **The iso-levels ARE the band boundaries** — reuse the same `ContourLevelSet` the iso-lines use. For N
  levels you get N+1 bands (below the lowest, between each pair, above the highest).
- **Banding via duplicate gradient stops:** build an `SKShader.CreateLinearGradient` (or radial, but linear
  mapped through the value range is simplest) whose colour stops are **duplicated at each band boundary
  fraction** so the gradient steps rather than blends (the `SkGradientShader` hard-edge technique;
  `https://api.skia.org/classSkGradientShader.html`). Equivalent and often simpler: precompute a **256-entry
  LUT** that is piecewise-constant per band, and apply it (see HeatMap §4 for the LUT mechanics) — pick
  whichever is cleaner; the requirement is **hard band edges aligned to the iso-levels**.
- **How to paint the bands:** the robust approach that aligns perfectly with the lines is to **fill between
  consecutive iso-polylines**. Two viable methods (pick one, document):
  1. **Value-threshold fill per cell (recommended, simplest correct):** for each band `[Lk, Lk+1)`, build an
     `SKPath` of the grid cells whose interpolated region falls in that band — i.e. render the band as the
     area between the level-`Lk` and level-`Lk+1` iso-polylines. In practice: draw the filled level-set
     back-to-front (highest band first or lowest first) using the closed iso-polylines as fill regions with
     even-odd/winding fill, so each band's colour shows in its annulus.
  2. **Per-pixel LUT bitmap:** rasterize the `SurfaceGrid` to a small bitmap (value→band colour via the
     piecewise-constant LUT), then `DrawBitmap` scaled into the viewport with the Γ-disk clip. Hard edges come
     from the piecewise-constant LUT. This is the easiest to get pixel-correct and naturally respects NaN
     (transparent outside the disk). **Recommend method 2 for TopoMap** — it's a direct value→colour map of
     the same grid the lines came from, so bands and lines align by construction; method 1 is fiddlier.
- **Clip** to the Γ-disk (Smith) / box (Rect) — the NaN-transparent LUT bitmap already handles the disk if the
  grid NaNs are mapped to transparent.
- **Draw order:** fill first (under), then iso-lines over, then markers (handled by `PlotRenderer`).

**Gate (7.4d-3):** TopoMap fill renders bands whose edges coincide with the iso-lines (overlay the lines and
confirm they sit on band boundaries); out-of-disk is transparent; palette is a clean discrete ramp.

---

## 4. HeatMap filled contours (EXPERIMENTAL, behind a selector) — design §2.5

A true **density** heat map of the **measured scatter points** (`ScatterReduction.Coords`), NOT the RBF
surface — a different visualization, offered experimentally. Two-pass Skia (the owner's suggested approach;
use it unless a cleaner one is obvious):
- **Pass 1 — accumulate intensity:** a separate `SKBitmap`/`SKCanvas`; for each measured point draw a
  monochrome **radial gradient** (opaque-at-centre → transparent) with **`SKBlendMode.Plus`** so overlapping
  points sum their alpha. Point radius is a tunable (in canvas px). Map each point's world Γ/Z → canvas via
  `tf.ToCanvas`.
- **Pass 2 — colourize:** bake a **256-entry LUT** from a multi-stop gradient (e.g. blue→cyan→green→yellow→red)
  by sampling an `SKShader.CreateLinearGradient` (or compute stops directly), populate `byte[256]` R/G/B/A
  tables, and apply `SKColorFilter.CreateTable(aTable, rTable, gTable, bTable)` when drawing the density
  bitmap onto the plot canvas. (Owner supplied a code sketch in the 7.4d request — follow its shape.)
- Clip to viewport/disk. Ships **behind a fill-type selector** (`None / Lines / TopoMap / HeatMap`), default
  NOT HeatMap.

**Gate (7.4d-4):** HeatMap renders a plausible density map from the measured points behind the selector;
overlapping clusters read hotter; does not crash on sparse/empty scatter.

---

## 5. Integration into the plot draw path

Minimal, additive. A contour trace is a `Trace` (its full inspector card + DataSet binding is **7.4e** — for
7.4d, a thin contour-trace representation is enough to drive the renderer; coordinate with how `Trace` carries
kind-specific data, but DO NOT build the inspector card here).
- In `PlotRenderer.Draw`, **inside the existing viewport clip** (before/around the trace loop): if a trace is a
  contour kind, route to `ContourRenderer` — **fills first** (under all traces), then iso-lines. Keep ordinary
  traces drawing as today. The cleanest seam: a branch in the `foreach (var trace in plot.Traces)` block (or a
  pre-pass that draws contour fills, then the normal loop draws contour lines + other traces).
- The contour trace needs, at render time: the `SurfaceGrid` (from `LoadpullSurface.Resample`), the
  `ContourLevelSet`, the fill type (`None/TopoMap/HeatMap`), line colour/width, and (HeatMap) the
  `ScatterReduction`. For 7.4d, it's acceptable to compute the `SurfaceGrid` on demand in the trace (cached in
  `LoadpullSurface`) given a `LoadpullFit`; wiring the *authoring* of that fit is 7.4e.
- **Performance:** extraction at resolution 50 is cheap; the LUT bitmap is `res×res` then scaled — fine. Cache
  the extracted polylines + the band bitmap on the trace keyed by (fit params, level set, fill type, viewport
  size) so pan/zoom redraws don't re-extract unless inputs change. Re-extract only when the surface/levels/box
  change.

**Gate (7.4d-5, integration):** a contour trace on a Smith plot draws grid → TopoMap fill → iso-lines →
(optional) markers, in order; pan/zoom redraws use the cache; switching fill type (Lines/TopoMap/HeatMap)
updates live.

---

## 6. Slice plan (compile-and-test-gated)
- **7.4d-1 — `ContourExtractor` (RfCore, headless)** + marching-squares tests (analytic circle, saddle, real
  grid). No Skia.
- **7.4d-2 — `ContourRenderer.DrawIsoLines` + labels (UI)**; iso-lines on Smith, clipped.
- **7.4d-3 — TopoMap fill (UI)**; bands aligned to iso-levels (recommend the value→LUT bitmap approach).
- **7.4d-4 — HeatMap fill (UI, experimental)**; two-pass density, behind selector.
- **7.4d-5 — integration into `PlotRenderer.Draw`** + render cache + fill-type selector plumbing (minimal;
  full inspector card is 7.4e).

## 7. Constraints / gotchas
- **Firewall:** `ContourExtractor` = RfCore, zero Skia/Avalonia/colour. All colour + Skia = `src/Ui`.
- **Row-major / transpose:** match `SurfaceGrid`'s `idx = yi*res + xi` exactly (row=y/im, col=x/re). A
  transpose bug silently rotates contours — assert against the analytic circle test.
- **World coords only out of the extractor:** map to canvas exclusively via `tf.ToCanvas` in the renderer, so
  Smith vs Rect-Z is handled by the existing transform (don't special-case the substrate in the extractor).
- **NaN = outside disk:** skip NaN cells (open contours at boundary); map NaN→transparent in fill bitmaps.
- **Draw order + clip:** fills under lines under markers, all inside `PlotRenderer`'s viewport clip.
- **TreatWarningsAsErrors** (Core/UI): capture nullable props into locals; no unused privates; no `<`/`>` in
  XML doc comments. RfCore.Tests has ImplicitUsings disabled (add usings).
- **Cache:** key the extracted polylines + band bitmap by (fit params, level set, fill type, viewport size) so
  pan/zoom doesn't re-extract.
- **Determinism:** no parallelism in the extractor (resolution 50 is tiny); keep it simple/testable.

## 8. Tests
- `RfCore/tests/RfCore.Tests/ContourExtractorTests.cs` — analytic circle (closed loop, radius), saddle (no
  crossings), real `SurfaceGrid` (counts, NaN-boundary openness), level-set builders. Headless, fast.
- UI rendering is owner-verified (visual) per the gates; add a smoke test that `ContourRenderer` runs on a
  synthetic grid + transform without throwing if a UI test harness exists (mirror existing renderer smoke
  tests).

## 9. Out of scope (next sub-gate)
- The **contour trace inspector card** (metric picker, "@ constant other-metric = value", level-set controls,
  Γ/Z toggle, fill-type selector UI, frequency pin) + DataSet binding → **7.4e**.
- `.s1p` reflection overlay → **7.4e**.
- TopoMap/HeatMap palette *authoring* UI → 7.4e (7.4d ships sensible default palettes + the selector plumbing).
