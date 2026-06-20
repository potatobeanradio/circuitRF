# Loadpull Contours & Surface Modeling — Phase 7.4 Design

**Status:** Design (materials in hand) · **Date:** 2026-06-20
**Reads with:** `data-display.md` (§2 spine, §2.8 per-trace-kind inspector cards, sub-phase 7.4), `loadpull.md`
(the loadpull engine + result shape — FOM cubes over `{gridPoint, pinStep}`, `GammaLoad`/`ZLoad` over
`{gridPoint}`), `results-dataset-layout.md` (grouped `run.npy`), `src/Core/Data/CLAUDE.md` (DataSet/DataCube
contract, lockstep with splotRF), `data-export.md` (the `.npy` carrier).
**Reference material (outside the repo, by owner's choice):** `loadpull-contours-refs/` —
*"Improving loadpull measurement time by intelligent measurement interpolation and surface modeling
techniques"* (Hart, ARFTG 2006) + `SPLData.py` (the working Python reference) + `.spl`/`.lpcwave` test data.

This note specifies **Phase 7.4** of the Data Display: first-class loadpull **contour** plotting, the
**surface-model engine** beneath it (smooth 2-D interpolation of the scattered loadpull field + off-grid
power-sweep synthesis), and the **measured-data ingest** (`.spl`/`.lpcwave`) that lets the Data Display
treat measured and simulated loadpull identically. It is **sub-gated** (7.4a–7.4f); each sub-gate is
independently shippable with a concrete acceptance check.

---

## 0. What the method is (from the paper + `SPLData.py`)

Loadpull characterizes an active device by sweeping the termination Γ presented to it over a grid, and at
**each grid point driving the device up in power** to (and slightly past) a target gain compression. The raw
field is therefore inherently **two-axis**: `{gridPoint, pinStep}` — one power drive-up per swept Γ. Every
figure of merit (Pout, PAE, gain, …) is a value over that 2-D field.

The paper's technique — as implemented in `SPLData.py` — is **three layers** stacked on that raw field:

1. **Per-grid-point compression preprocessing.** For each Γ grid point, find the gain-compression reference
   (`Gmax` = peak gain over the drive-up — the small-signal anchor) and build a per-point `Compression`
   curve = `peakGain − gain(p)`. A 1-D linear interpolation over each drive-up then evaluates *any* metric
   "at constant X dB compression." Loadpull and compression are inseparable — this coupling is intrinsic.

2. **The 2-D surface (the core).** To make a contour, reduce every grid point to a single scalar (e.g. Pout
   at const 3 dB compression) via the per-point 1-D interp, then fit **one smooth 2-D surface** over the
   scattered Γ points. `SPLData.py` uses a radial basis function (RBF): complex Γ split into `(Re, Im)`
   coordinates, `multiquadric` basis, `smooth=1e-3`, euclidean norm, NaN points dropped. Iso-levels (the
   contour lines) are then pulled from this surface resampled onto a grid.

3. **Off-grid power-sweep synthesis (the clever part).** To produce a drive-up at a Γ **not** in the grid:
   build a *stack* of 2-D RBF surfaces, one per "dB-below-compression" back-off slice (the reference uses
   `NumInterpStacks = 32` surfaces spanning 16 dB). Evaluating the whole stack at a query Γ, then
   re-interpolating, reconstructs a synthetic power sweep at that arbitrary load. This is the
   "power sweeps at loadpoints off the data grid" capability — the surface model is a *data-synthesis
   engine*, not merely an iso-line tracer.

Supporting math (all in `SPLData.py` via `NetworkUtilities`): `g2z`/`z2g` (Γ↔Z), `vswr_circle` (build a
VSWR circle around a point), and an **auto-view-box** algorithm (`get_recommended_grid` / `get_MXX`) that
finds the max-power (MXP) and max-efficiency (MXE) locations and frames the plot around them.

> **Key gap to fill ourselves:** the contour **iso-line tracing** is *not* in `SPLData.py`. The Python
> produces a `(Γ-grid, value-grid)` and handed it to matplotlib's `contour` to draw the lines. circuitRF
> has no such library on the Skia substrate — **we build the iso-line renderer** (marching squares over the
> resampled grid, clipped to the Γ-disk or Z-plane substrate). This is its own sub-gate (7.4d).

---

## 1. Locked decisions (7.4 kickoff)

1. **Surface storage = derived, cube stays honest (Option A).** The `run.npy` / ingested `.spl` holds **only
   the measured/simulated field** (FOM cubes over `{gridPoint, pinStep}` + `GammaLoad{gridPoint}`). The RBF
   surfaces, compression preprocessing, and off-grid stacks are **derived** by a first-class headless
   `LoadpullSurface` model and held in an **in-memory cache** (the `DataInterp`/`DataInterpStack` dictionary
   pattern from `SPLData.py`). The fit is a *view*, not data: change basis / smoothing / compression level /
   Z0 / grid resolution and nothing on disk is stale. No surface serialization format to version (honors the
   alpha "break format freely" rule by having no new format). **Cache key includes every fit parameter**
   (`freqIndex, metric, constantType, constantValue, Z0, basis, smoothing, gridRes`) so a parameter change is
   always a fresh entry, never a stale read.
   - **Cross-session caching = documented future option, not built now.** A disposable sidecar cache
     (`run.npy.lpcache`, regenerated on miss, never the source of truth) is recorded here as a pure
     optimization to revisit only if cold-start proves painful. 7.4 ships in-memory caching only.
2. **Ingest first (7.4f → 7.4a).** Build the `.spl`/`.lpcwave` readers **before** the contour engine, so the
   surface/contour work is validated against real measured data from day one and the data path is de-risked
   early. The reader's job: `.spl`/`.lpcwave` → the **same** loadpull DataSet shape the engine emits, so the
   Data Display **cannot tell measured from simulated** without being told (owner's explicit goal).
3. **Custom, allocation-free dense solver (LDLᵀ/Cholesky).** The RBF kernel is **dense** (every center
   interacts with every other), so a sparse solver (CSparse) is the wrong tool. A purpose-built symmetric
   dense factorization is the **fastest at both ends** — microseconds with zero ceremony at N≈10–20, sub-ms
   and cache-resident at N≈200 — because it carries no library call/setup tax and no sparse bookkeeping. The
   hot path allocates nothing. **Performance is a gated, benchmarked property** (7.4a), not a hope.
4. **Substrates: Γ-plane (Smith) and Z-plane (Rect).** Contours render on both, per `data-display.md` §2.4.
   Overlay: a contour plot may overlay 1-port Touchstone (`.s1p`) reflection; it does **not** mix with
   power-sweep line traces (those are a different plot).
5. **Contour trace = another inspector card kind.** Per `data-display.md` §2.8, a contour trace is a new
   per-trace-kind card body ("metric @ constant other-metric = value", level set, Γ/Z substrate). The
   inspector was built so trace-kind → card-body is the extension point; 7.4 is additive, not a rewrite.
6. **Firewall.** All surface/contour **math** is framework-free in **RfCore** (the `LoadpullSurface` model,
   the RBF solver, the marching-squares core) — headless, testable, potentially shared with splotRF. Only
   the Skia **rendering** of iso-lines and the inspector **card** live in `src/Ui`.

---

## 2. Architecture

### 2.1 Headless surface-model stack (RfCore)
Three framework-free layers, bottom-up:

```
RfBfRbf2D          ── dense RBF interpolant: fit (LDLᵀ solve) + evaluate.  ~150-200 LOC.
Interp1DLinear     ── linear 1-D interp, bounds_error=False → NaN.          ~40 LOC.
        │
LoadpullSurface    ── the SPLData.py engine, ported:
                       • compression preprocessing (Gmax anchor, per-point Compression curve)
                       • generate_interpolator (metric @ const compression/other-metric → 2-D surface)
                       • interpolate_2D (resampled grid for contouring)
                       • get_recommended_grid / MXP·MXE auto-view-box
                       • DataInterpStack (off-grid power-sweep synthesis)
                       • lazy in-memory cache keyed by all fit params
        │
ContourExtractor   ── marching squares over the resampled grid → iso-polylines,
                       clipped to the Γ-disk (Smith) or the box (Z-plane).  Headless.
```

The UI consumes `LoadpullSurface` + `ContourExtractor`; it owns only the Skia draw and the inspector card.

### 2.2 The data contract (one shape, two producers)
The loadpull engine already emits FOM cubes over `{gridPoint, pinStep}` plus `GammaLoad`/`ZLoad` over
`{gridPoint}` (loadpull.md §5; `LoadpullEngine.BuildLoadpullDataSet`). The **ingest readers normalize
`.spl`/`.lpcwave` into the same cube shape and the same FOM names**, so `LoadpullSurface` takes a DataSet and
**never knows the origin**. This is the seam that makes "view measured and simulated identically" fall out of
one mechanism. (FOM-name normalization mirrors `SPLData.py`'s header-key guessing — `Pout_dBm`/`PoutWaves[dBm]`
→ a canonical `Pout`, etc.)

### 2.3 Where the RBF math comes from (scope of the scipy.Rbf port)
We re-implement only the subset `SPLData.py` actually exercises:

**In scope (must match scipy numerically — the 7.4a gate):**
- 2-D scattered interpolation: nodes `(Re Γ, Im Γ)`, scalar values `dᵢ`.
- **Multiquadric** basis `φ(r) = √((r/ε)² + 1)` (the only one used; `thin_plate` path is commented out).
  Implement multiquadric + thin-plate + gaussian (three lines each) for flexibility/comparison.
- scipy's **epsilon default** = `(prod(coordinate ranges) / N)^(1/dim)` — must replicate exactly for
  numerical parity with the reference.
- **smoothing**: subtract `smooth · I` from the kernel diagonal before solve (scipy's exact convention).
- Solve dense symmetric `A w = d` (`A[i,j] = φ(‖xᵢ−xⱼ‖)`), evaluate `Σ wᵢ φ(‖q−xᵢ‖)` at arbitrary points.
- Euclidean norm; NaN-drop on input values.
- `Interp1DLinear` with `bounds_error=False` → NaN outside (per-point compression reduction + final sweep
  resample).

**Out of scope (deliberately not ported):** other norms, custom-callable basis, N-D (we are strictly 2-D for
surfaces / 1-D for sweeps), vector outputs, epsilon overrides beyond the default.

**Verdict:** the RBF core is small (~150–200 LOC) and self-contained. The only correctness risk is matching
scipy's epsilon/smooth conventions, which the 7.4a numerical gate pins against the Python.

### 2.4 The solver (performance design)
RBF has two cost phases with different curves; the two N regimes stress different things:
- **Fit:** dense symmetric `N×N` solve. **N≈20:** trivial — the cost is *fixed overhead*, so the win is
  zero ceremony (stack/`Span<double>` buffers, no heap churn, no library call). **N≈200:** `O(N³)`≈8M flops,
  sub-ms with a good factorization; the kernel build (`O(N²)`) also matters.
- **Evaluate:** `M²·N` basis evals per surface (e.g. 50²·200 = 500k), ×32 stack surfaces — **this is the real
  hotspot**, embarrassingly parallel and vectorizable.

Design: **LDLᵀ / Cholesky-family** (symmetric ⇒ ~2× faster than LU, cleaner numerically); allocation-free hot
path (pre-sized contiguous row-major buffers); evaluation as a tight contiguous loop (centers + weights in
flat arrays) with `System.Numerics.Vector<double>` SIMD added only if profiling demands. Custom beats CSparse
(sparse overhead on a dense matrix) and NumFlat (call overhead hurts N≈20). **Gate includes micro-benchmarks**
at N≈20 and N≈200 plus a full contour render (fit + 50×50 eval) so "fast" is regression-guarded.

### 2.5 Filled contours (7.4d renderer; Skia, UI)
The renderer supports both **iso-lines** and **filled contours**. Two fill types:

**TopoMap (PREFERRED).** Topographic-style discrete colour bands, one band per interval between adjacent
iso-levels. Rendered as **pure vector** (no bitmap): for each level threshold Lk, a filled `SKPath` covering the
region where the surface value is ≥ Lk is built by **"marching-squares fill"** — per grid cell, the polygon of
the ≥Lk sub-region (cell corners ≥Lk plus linearly-interpolated edge crossings), accumulated into one `SKPath`
per band. Bands are painted back-to-front (lowest threshold first) in **opaque** colours, composited through a
single `SaveLayer` alpha so the Smith grid shows through uniformly and every pixel ends up exactly one band
colour. Because the per-cell crossings are the same ones marching-squares uses for the iso-lines, **band edges
coincide with the iso-lines by construction**. NaN cells (outside the Γ-disk) are skipped, so the fill clips to
the disk for free. Being `DrawPath`-based, it exports to PDF/SVG as true vector via `SKDocument.CreatePdf` /
`SKSvgCanvas` (a `DrawBitmap` LUT raster was the original sketch but is rejected — it pixelates on vector
export). The `SkGradientShader` duplicate-stop banding technique
(`https://api.skia.org/classSkGradientShader.html`) remains available if a continuous-gradient TopoMap variant
is ever wanted, but the band-path approach is what ships.

**HeatMap (EXPERIMENTAL).** A true density heat map of the scattered measured points, rendered as **pure
vector**: each measured point is drawn as a warm radial-gradient `SKShader` (semi-opaque hot core → transparent
edge) with `SKBlendMode.Plus`, so overlapping points accumulate additively toward the hot end (dense clusters
read hotter). No bitmap — each point is a `DrawCircle` with a radial shader, which `SKDocument.CreatePdf` /
`SKSvgCanvas` emit as native radial shadings, so it scales cleanly in vector export. (The earlier two-pass
bitmap sketch — `SKBlendMode.Plus` accumulation into a monochrome bitmap then `SKColorFilter.CreateTable` LUT —
is rejected for the same rasterization reason.) Offered behind a fill-type selector — NOT the default — because
it visualizes raw point density rather than the fitted FOM surface. Owner's framing: TopoMap is the preferred
filled contour; HeatMap is exploratory for now.

> **Future option (if HeatMap graduates from experimental):** the vector additive-bloom above reads as density
> but cannot reproduce a true per-pixel multi-hue density LUT (blue→cyan→green→yellow→red) exactly — a precise
> LUT is inherently raster. The one faithful route that is *also* vector is to **treat the density field as a
> surface and TopoMap-fill it**: rasterize point density onto a grid (or evaluate a kernel-density surface),
> then run the same marching-squares band-fill used for TopoMap. That yields the exact palette look as clean
> vector bands. Deferred until HeatMap proves its keep — noted here so the path is on record.

Both fills are **renderer-only** (Skia, `src/Ui`) and **all vector** (no `DrawBitmap`) so PDF/SVG export stays
crisp at any scale — the headless `LoadpullSurface`/`ContourExtractor` stay colour-agnostic (they emit value
grids + iso-polylines; colour mapping is a UI concern). This keeps the firewall intact and lets the fill
palette be a pure UI/style choice.

---

## 3. Sub-phases

### 7.4f — `.spl` / `.lpcwave` ingest (FIRST — de-risk data + supply real test data)
**Goal:** read measured loadpull (`.spl`, `.lpcwave`) into the **same** loadpull DataSet shape the engine
emits, so the Data Display treats measured and simulated identically.
**Deliverables:** framework-free readers (RfCore) for `.spl` and `.lpcwave` → DataSet with FOM cubes over
`{gridPoint, pinStep}` + `GammaLoad`/`ZLoad` over `{gridPoint}`; canonical FOM-name normalization (header-key
guessing à la `SPLData.py`); harmonic-termination metadata where present (2f0/3f0 load/source Γ). Wire into the
data-source library (`data-display.md` 7.2) as new source kinds beside Touchstone/`.npy`.
**Gate:** load `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` and a `.lpcwave` file; the resulting DataSet has the
expected grid/pin shape and FOM cubes; plotting a per-grid-point drive-up (Pout vs Pin) works through the
existing trace machinery; the viewer shows no origin-specific affordance.
**Note:** independent of contours — could even ship standalone. Doing it first means 7.4a–c validate against
real data.

### 7.4a — RBF + interp1d math core (headless RfCore, fully testable)
**Goal:** the smooth-interpolation primitive, numerically matching scipy and fast at both N regimes.
**Deliverables:** `RfBfRbf2D` (multiquadric/thin-plate/gaussian; scipy epsilon default; smoothing; NaN-drop;
allocation-free LDLᵀ solve; arbitrary-point evaluate); `Interp1DLinear`.
**Gate:** (correctness) reproduce scipy `Rbf` outputs on scattered points from the test `.spl` within
tolerance; (performance) benchmark assertions at N≈20 and N≈200 and a full fit+50×50-eval, within targets.

### 7.4b — `LoadpullSurface` model (the SPLData engine, headless)
**Goal:** metric-at-constant-other-metric surfaces over Γ/Z, with the auto-view-box.
**Deliverables:** compression preprocessing (Gmax anchor + per-point Compression curve); `generate_interpolator`
(metric @ const compression / @ const other-metric → 2-D surface); `interpolate_2D` (resampled grid);
`get_recommended_grid` + MXP/MXE finder; Z0 renormalization as a surface option; lazy in-memory cache keyed by
all fit params.
**Gate:** the metric grid for a known FOM/level (e.g. Pout @ 3 dB compression) matches the Python reference on
the test `.spl` within tolerance; MXP/MXE locations match.

### 7.4c — off-grid power-sweep synthesis (the `DataInterpStack`)
**Goal:** reconstruct a power drive-up at an arbitrary (off-grid) Γ.
**Deliverables:** the per-back-off-slice surface stack + `get_power_sweep` (stack-evaluate at a query Γ →
resample to a smooth sweep), with the reference's support-count guards (drop slices with too few points).
**Gate:** a synthesized drive-up at an off-grid Γ matches the Python reference within tolerance (the
data-display.md headline gate). Verify against a held-out grid point (fit without it, synthesize at its Γ,
compare to its measured drive-up).

### 7.4d — contour renderer: iso-lines + filled contours (UI / Skia)
**Goal:** turn a resampled metric grid into drawn **iso-lines** AND **filled contours** on the Smith/Z
substrate.
**Deliverables:**
- `ContourExtractor` (marching squares → iso-polylines, headless RfCore) clipped to the Γ-disk (Smith) or the
  auto-box (Z-plane); level-set specification (explicit levels, or N levels between min/max, or step).
- Skia rendering of **iso-lines** with level labels.
- Skia rendering of **filled contours**, two fill types (see §2.5):
  - **TopoMap (PREFERRED)** — discrete elevation-style colour bands between the iso-levels, hard-edged via
    duplicate gradient colour stops (`SkGradientShader` banding). The marching-squares iso-levels ARE the band
    boundaries, so fill reuses the extractor output — no separate computation.
  - **HeatMap (EXPERIMENTAL)** — true density heat map via a two-pass Skia approach (Pass 1: accumulate
    radial-gradient intensity with `SKBlendMode.Plus` onto a monochrome bitmap; Pass 2: remap intensity →
    multi-stop colour via `SKColorFilter.CreateTable` LUT). Marked experimental; ships behind a fill-type
    selector, not the default.
- Integrate with the existing Smith/Polar/Rect substrates and the auto-view-box.
**Gate:** reproduce a known contour set from a loadpull run (owner-verifiable against the Python/matplotlib
reference); iso-lines clip correctly to the Smith boundary; TopoMap fill bands align exactly with the iso-lines
(shared boundaries); HeatMap renders a plausible density map behind a selector.

### 7.4e — contour trace card (inspector extension) + `.s1p` overlay
**Goal:** author a contour trace end-to-end from the Data Display.
**Deliverables:** a new per-trace-kind card body (`data-display.md` §2.8) — metric picker, "@ constant
<other-metric> = value" (incl. the special "@ constant compression = x dB"), level-set controls, Γ/Z substrate
toggle, frequency pin; bind to a loadpull DataSet (measured or simulated); `.s1p` reflection overlay.
**Gate:** author a contour from a loadpull `run.npy` and from an ingested `.spl` in the same way; an `.s1p`
overlays; live edits re-extract + redraw.

---

## 4. Cross-cutting

- **Performance.** Surface fits cached in-memory keyed by all fit params; recomputed only on input change.
  Contour grid resolution is a knob (reference default 50; 200 noted as "much too slow" in Python — our custom
  solver should move that ceiling, validated by the 7.4a benchmarks). Off-grid stacks are the heaviest;
  build lazily, cache aggressively.
- **Honesty.** The cube stores only the measured/simulated field (decision §1.1). Derived surfaces never
  persist (no sidecar in 7.4). Indicator/label policy from `data-display.md` applies to contour traces too.
- **Firewall.** RBF, `LoadpullSurface`, marching-squares = RfCore (headless, tested). Skia iso-line draw +
  inspector card = `src/Ui`.
- **Lockstep.** No new DataSet-API surface beyond what 7.2 added, *unless* ingest needs a canonical loadpull
  FOM-naming convention recorded in `src/Core/Data/CLAUDE.md` (flag if so).
- **Alpha file-format freedom.** No new on-disk format in 7.4 (surfaces are derived). Ingest is read-only of
  external `.spl`/`.lpcwave`.

---

## 5. Open questions (by sub-gate)
- **7.4f:** exact `.spl`/`.lpcwave` grammar coverage (the reference reader handles many vendor header
  variants — scope which we support first vs defer); canonical FOM-name set + where it's recorded.
- **7.4a:** confirm the scipy epsilon/smooth conventions reproduce on our test data to the chosen tolerance;
  performance targets (concrete ms numbers) for the benchmark gate.
- **7.4b:** which FOMs are first-class (Pout/PAE/DE/Gt/Gp/AMPM …); compression type default (`Gmax` vs `Gss`);
  the auto-view-box VSWR inclusion factors.
- **7.4c:** stack count / back-off span defaults (reference: 32 slices / 16 dB) and the min-support guard.
- **7.4d:** level-set UX (explicit / N-between / step); label placement; Γ-disk clip robustness at the edge;
  TopoMap band palette + colour-stop banding details; whether HeatMap intensity uses measured-point density or
  the resampled surface; fill-type selector UX (lines / TopoMap / HeatMap, and lines-over-fill combos).
- **7.4e:** how "@ constant other-metric = value" reads in the card; `.s1p` overlay styling.

---

## 6. Status
- Materials in hand (paper + `SPLData.py` + `.spl`/`.lpcwave` test data, in `loadpull-contours-refs/`).
- Decisions §1 locked: surface storage = derived/in-memory (Option A); ingest first; custom LDLᵀ solver.
- Sub-gates 7.4a–7.4f defined (§3). Ingest (7.4f) runs first.
- Next: 7.4f detailed brief(s) — `.spl`/`.lpcwave` reader → loadpull DataSet shape; request 1–2 test files
  copied into `circuitRF/testdata/` for the reader's regression tests.
