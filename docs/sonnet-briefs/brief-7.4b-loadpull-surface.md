# Brief 7.4b — `LoadpullSurface` model (compression preprocessing + metric surfaces + MXP/MXE view-box)

**Phase:** 7.4b (Data Display loadpull contours — the surface-model engine).
**Design:** `docs/design/loadpull-contours.md` §0 (the 3-layer method), §1.1 (cube stays honest; derived;
in-memory cache keyed by all fit params), §2.1 (the stack), §3 (7.4b).
**Goal:** the headless engine that turns a **loadpull DataSet** (`{gridPoint, pinStep}` FOM cubes) into
**smooth 2-D metric surfaces over Γ (or Z)**, with per-grid-point **compression preprocessing** and an
**MXP/MXE auto-view-box**. This is the port of the core of `SPLData.py` onto the 7.4a `Rbf2D`. **It does NOT
extract iso-lines** (that's 7.4d) and does NOT synthesize off-grid power sweeps (that's 7.4c) — it produces
the fitted surface + a resampled value grid that 7.4d will contour.
**Reference (read-only):** `<workspace>/loadpull-contours-refs/SPLData.py` —
`generate_interpolator`, `interpolate_2D`, `get_recommended_grid`, `get_MXX`, `calcMXPMXE`, and the
compression preprocessing in `__init__` (the `Gmax`/`compression_index`/`Compression` block). Port the
**algorithms**, not the dict-of-tuples structure.
**Consumes (verified on disk):**
- `RfCore/src/Loadpull/Rbf2D.cs` — `new Rbf2D(ReadOnlySpan<Complex> nodes, ReadOnlySpan<double> values,
  RbfKernel.Multiquadric, smooth: 1e-3)`; `.Evaluate(re,im)`, `.Evaluate(qRe,qIm,result)`,
  `.UsedIndices`, `.NodesRe/.NodesIm/.NodeValues`, `.NodeCount`.
- `RfCore/src/Loadpull/Interp1DLinear.cs` — `new Interp1DLinear(x, y)`; `.Eval(x)` → NaN out of range.
- `RfCore/src/Loadpull/SplReader.cs` / `LpcwaveReader.cs` output shape (the input contract):
  cubes `Pout`(W), `Gt`(dB), `Gp`(dB), `DE`(linear), `PAE`(linear), `PavlDbm`(dBm) over axes
  **`{gridPoint, pinStep}`** (or `{freq, gridPoint, pinStep}` when multi-freq); `GammaLoad`(Complex) and
  `ZLoad`(Complex) over `{gridPoint}` (or `{freq, gridPoint}`). `pinStep` axis values **= PavlDbm**.
  Invalid points are **NaN** (Rbf2D NaN-drops them).
- `RfCore/src/Data/DataCube.cs` / `DataSet.cs` — `ds[name]`, `ds.Contains(name)`, `cube.Axis("gridPoint")
  .Values`, `cube.RealValues`, `cube.ComplexValues`, `cube[args].Cube`/`.RealValue` slicing
  (int pins, `Range`/`DataCube.All` keeps).

**Firewall:** RfCore, zero Avalonia. Output = arrays + small structs. No Skia, no DataSet mutation of the
input (cube stays honest — surfaces are derived & cached separately).

---

## 0. The method, concretely (what 7.4b computes)

A loadpull metric surface answers: **"metric Y at constant <constraint> over the Γ (or Z) plane."** Two
constraint types (from `SPLData.py`):
- **`Compression`** (the headline): Y at constant **X dB gain compression**. Requires the per-grid-point
  compression preprocessing (§1) to know, for each Γ point, the drive level that is X dB compressed, then
  reads Y there.
- **A constant value of another metric** (e.g. Y=PAE at constant Pout=45 dBm): same shape, the per-point 1-D
  interp is keyed on that other metric instead of `Compression`.

Pipeline for one surface `(freqIdx, keyY, constantType, constantVal, Z0, kernel)`:
1. **Per grid point i**, build a 1-D interp of `Y` vs the **constraint variable** over that point's drive-up
   (the `pinStep` slice). Evaluate at `constantVal` → one scalar `Yᵢ` (NaN if out of range). For
   `Compression`, the constraint variable is the per-point `Compression` curve (§1); the interp domain starts
   at `compression_index` (the `Gmax` location), matching `SPLData.generate_interpolator`.
2. **Collect** the scattered set `{ Γᵢ (or Zᵢ), Yᵢ }` over all grid points; optionally renormalize Γ to a
   chosen `Z0` (`SPLData`: `X = z2g(50*g2z(X)/Z0)`).
3. **Fit one `Rbf2D`** over `(Re, Im)` of the coordinate with values `Yᵢ` (NaN-dropped automatically).
4. **Resample**: evaluate the fit on a grid (auto-box from §3, or a caller box) → a value grid 7.4d contours.

The cache (design §1.1): a `Dictionary` keyed by **all** of `(freqIdx, keyY, constantType, constantVal, Z0,
kernel, smooth)` so any parameter change is a fresh entry. Surfaces are expensive; build lazily, cache hard.

---

## 1. Compression preprocessing (port from `SPLData.py __init__`)

Loadpull and compression are coupled — this is computed once per (freqIdx) and reused. For each grid point i,
over its drive-up (the `pinStep` slice, valid points only):
- **`Gmax` anchor (default `CompressionType=Gmax`):** `compression_index = argmax(gain over the drive-up)`,
  where `gain` is `Gt` (dB) — the `CompressionVarString` (`SPLData`: `Gt_dB`/`GainWavesTrd[dB]`; here the
  canonical cube is **`Gt`**).
- **`Compression` curve** (from `compression_index` onward):
  `Compression[p] = gain[compression_index] − gain[p]` for `p ≥ compression_index`. This is a monotone-ish
  increasing dB-below-peak curve used as the 1-D interp domain.
- Store, per grid point: `CompressionIndex` and the `Compression` array (aligned to the drive-up tail).
- Also compute per-freq `MedianCompression`, `MaxCompression`, `MinPout`/`MaxPout` (used by the recommended
  compression setting + view-box). Port `RecommendedCompressionSetting` (the nearest of
  `{0.1,0.5,1,2,…,19}` ≤ median).

> **Honesty:** this preprocessing lives in `LoadpullSurface` (derived), NOT written back into the input
> DataSet. The cube stays the raw measured field (design §1.1).

`CompressionType=Gss` (small-signal: `compression_index=0`) is a documented alternative — implement the enum,
default `Gmax`.

---

## 2. API (what 7.4d / the trace card will call)

New file `RfCore/src/Loadpull/LoadpullSurface.cs`, namespace `RfCore.Loadpull`. Sketch:

```csharp
public enum CompressionType { Gmax, Gss }
public enum SurfacePlane    { Gamma, Z }   // contour substrate: Γ-disk (Smith) or Z-plane

/// A derived surface-model engine over a loadpull DataSet. Holds the raw cubes by reference,
/// computes compression preprocessing once, and lazily fits/caches RBF metric surfaces.
public sealed class LoadpullSurface
{
    public LoadpullSurface(DataSet data, string group = "");  // group: loadpull analysis group, "" if bare

    public IReadOnlyList<double> Frequencies { get; }          // Hz; single-element if no freq axis
    public int GridPointCount(int freqIdx);

    // Per-freq compression facts (preprocessing results).
    public double MedianCompression(int freqIdx);
    public double RecommendedCompression(int freqIdx);

    /// The scattered reduced values {coord, Y} at constant <constraint>, NaN-dropped.
    /// coord is Γ (Z0-renormalized if z0 != null) or Z per `plane`.
    public ScatterReduction Reduce(
        int freqIdx, string metricY, ConstraintSpec constraint,
        SurfacePlane plane, double? z0 = null);

    /// Fit (or fetch cached) the RBF surface for this query.
    public LoadpullFit Fit(
        int freqIdx, string metricY, ConstraintSpec constraint,
        SurfacePlane plane, double? z0 = null,
        RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3);

    /// Resample a fitted surface onto a grid. If box is null, uses the MXP/MXE auto-view-box.
    /// Returns gridded values (NaN outside the Γ-disk when plane==Gamma).
    public SurfaceGrid Resample(LoadpullFit fit, ViewBox? box = null, int resolution = 50);

    /// The auto-view-box around MXP/MXE (get_recommended_grid).
    public ViewBox RecommendedBox(LoadpullFit fit);

    /// Max-power / max-efficiency locations (measured node + interpolated peak).
    public MxxResult MaxPower(int freqIdx, ConstraintSpec constraint, SurfacePlane plane, double? z0 = null);
    public MxxResult MaxEfficiency(int freqIdx, ConstraintSpec constraint, SurfacePlane plane, double? z0 = null);
}

public readonly record struct ConstraintSpec(ConstraintKind Kind, string MetricName, double Value);
public enum ConstraintKind { Compression, ConstantMetric }
public readonly record struct ViewBox(double MinRe, double MaxRe, double MinIm, double MaxIm);
public sealed record ScatterReduction(Complex[] Coords, double[] Values, int[] UsedGridIndices);
public sealed record LoadpullFit(Rbf2D Rbf, SurfacePlane Plane, double? Z0, /* echo of query for cache id */ ...);
public sealed record SurfaceGrid(double[] XSpace, double[] YSpace, double[] Values /* row-major, NaN outside disk */);
public sealed record MxxResult(Complex Measured, Complex Interpolated);
```
(Adjust names to match house style; keep the **records immutable** and the cache **private** inside
`LoadpullSurface`.)

---

## 3. The MXP/MXE auto-view-box (port `get_recommended_grid` / `get_MXX`)

`get_MXX(metric)`: (a) **measured peak** = the grid point with max metric value (`argmax` over `Rbf.di`,
i.e. `NodeValues`); (b) **interpolated peak** = evaluate the fit on a high-res grid inside a VSWR=1.2 circle
around the measured peak, take the argmax. `calcMXPMXE` does this for power (MXP) and efficiency (MXE).

`get_recommended_grid`: build a box that includes the measured data extent AND VSWR circles around MXP & MXE
(VSWR inclusion factors from `SPLData`: 1.3 for Z-plane; "include as much as possible" / 99 for Γ-plane, then
clip to the measured Γ extent). For Z-plane, round the box to nice grid lines (`RoundFactor = span/5`). Port
the clip-to-measured-box logic exactly — it prevents contour artifacts at the grid edge.

**Needs `vswr_circle` + `g2z`/`z2g`.** `RFNetwork` already has `g2z`/`z2g` analogues (per the codebase notes)
and `VSWR`. Check `RfCore/src/RFNetwork.cs` for a usable `vswr_circle` (a circle of constant VSWR in the Z or
Γ plane); if absent, add a tiny private helper in `LoadpullSurface` (a VSWR-S circle is standard: center/radius
in the Γ-plane from the VSWR value). Reuse `RFNetwork` conversions; do not duplicate Γ↔Z math.

---

## 4. Slice plan (compile-and-test-gated)

### 7.4b-1 — DataSet adapter + compression preprocessing
Parse the input DataSet into per-freq, per-grid-point drive-up arrays (read `Pout`/`Gt`/`Gp`/`DE`/`PAE`/
`PavlDbm` cubes by name; locate axes **by name** `gridPoint`/`pinStep`/optional `freq`; slice per grid point;
drop NaN/invalid). Compute the compression preprocessing (§1) + per-freq stats. Expose `Frequencies`,
`GridPointCount`, `MedianCompression`, `RecommendedCompression`, and an internal per-point
`(CompressionIndex, Compression[], driveUpYByMetric)`.
**Gate:** on `testdata/spl_test_data/Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` (via `SplReader`), construct a
`LoadpullSurface`; assert 145 grid points, a sane `MedianCompression` (>0, < device max), and that a chosen
grid point's `Compression` curve starts at 0 and increases. Hand-cross-check one point's `compression_index`
against the raw drive-up's peak-`Gt` row.

### 7.4b-2 — scatter reduction + RBF fit + cache
`Reduce(...)` (the per-point 1-D interp at constant constraint → scattered `{coord, Y}`) and `Fit(...)`
(build `Rbf2D`, cache by full key). Γ↔Z + Z0 renorm via `RFNetwork`. Use `Interp1DLinear` for the per-point
reduction (matches `scipy.interp1d(..., bounds_error=False)` → NaN out of range, then Rbf2D NaN-drops).
**Gate:** `Reduce(freq0, "Pout", {Compression, 3dB}, Gamma)` returns ≈145 coords with mostly-finite values;
`Fit(...)` builds an `Rbf2D` with `NodeCount` close to grid count (minus NaN drops); a second identical `Fit`
call returns the **cached** instance (reference-equality or a hit-counter assert); changing `smooth` or
`constantVal` produces a new fit.

### 7.4b-3 — resample grid + MXP/MXE + auto-view-box
`MaxPower`/`MaxEfficiency` (`get_MXX`), `RecommendedBox` (`get_recommended_grid`), `Resample` (meshgrid eval,
NaN outside the Γ-disk when `plane==Gamma`, per `interpolate_2D`'s `|X| < MaxGamma*1.02` clip).
**Gate:** `MaxPower` returns a measured Γ inside the unit disk and an interpolated Γ near it; `RecommendedBox`
is finite and contains the MXP point; `Resample` on a 50×50 grid returns a value grid whose max is ≈ the MXP
metric value (within interp tolerance) and whose out-of-disk cells are NaN. Spot-check a couple of grid cell
values are physically plausible (between the metric's measured min/max).

---

## 5. Constraints / gotchas
- **Firewall:** RfCore only; no Avalonia. Output is arrays/records.
- **Cube stays honest:** never write derived data back into the input DataSet. The surface + preprocessing
  live in `LoadpullSurface`'s own fields/cache.
- **Axis-by-name, never by position:** the input may be `{gridPoint, pinStep}` or `{freq, gridPoint, pinStep}`
  (multi-freq `.spl`). Resolve `freq`/`gridPoint`/`pinStep` by name via `cube.Axis(name)`; pin `freq` with the
  cube indexer. Don't assume rank.
- **NaN discipline:** invalid drive-up points and out-of-range interp results are NaN; rely on `Rbf2D`'s
  NaN-drop. Never zero-fill (zeros would corrupt the surface).
- **Min-support guard:** `SPLData` ignores fits with too few points (`len(IndexUsed) > 12` for stacks). For
  2-D surfaces, if `Rbf2D.NodeCount` is below a small threshold (e.g. < 6), return a "could not fit" result
  (empty `SurfaceGrid`) rather than a garbage surface — warn-once.
- **`RFNetwork` reuse:** Γ↔Z, VSWR via `RFNetwork` (`g2z`/`z2g`/`VSWR`); add `vswr_circle` only if not present.
  Don't reinvent.
- **NumFlat available but unused here** (the heavy linear algebra is inside `Rbf2D`; this layer is bookkeeping
  + 1-D interp).
- **Determinism:** no parallelism (keep simple/deterministic; the perf budget was gated in 7.4a).
- **TreatWarningsAsErrors** not on RfCore, but keep clean (no unused privates). RfCore.Tests has
  **ImplicitUsings disabled** — add usings in tests.

## 6. Tests
- `RfCore/tests/RfCore.Tests/LoadpullSurfaceTests.cs` — drive the real `testdata` files via `SplReader`/
  `LpcwaveReader`. Cover: compression preprocessing (7.4b-1), reduction+fit+cache (7.4b-2), MXP/MXE+box+
  resample (7.4b-3), multi-freq `.spl` (axis-by-name correctness on `GaN_FET_1p6_mm_3_Freq.spl`), and a
  `.lpcwave` source (origin-blind: the same API works on both).
- Keep assertions tolerance-based and physical (counts, finiteness, monotonicity, peak-near-MXP) rather than
  exact float matches — exact RBF parity was already gated in 7.4a. If the owner supplied a scipy golden for
  a full `interpolate_2D` grid, add a parity test against it (flag a `// GOLDEN:` slot).

## 7. Out of scope (next sub-gates)
- **Off-grid power-sweep synthesis** (the `DataInterpStack` — stack of per-back-off-slice surfaces +
  `get_power_sweep`) → **7.4c**. (7.4b builds single surfaces; 7.4c builds the stack on top of this same
  reduce/fit machinery.)
- **Contour iso-line extraction** (marching squares over the `SurfaceGrid`) → **7.4d**.
- **Curvilinear angle** metric (`curvilinear_angle` in `SPLData`) — niche; defer unless asked.
- Any UI, trace card, `.s1p` overlay → **7.4e**.
