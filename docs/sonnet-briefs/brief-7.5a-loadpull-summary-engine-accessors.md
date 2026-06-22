# Brief 7.5a — LoadpullSurface summary-table accessors (RfCore, headless)

**Phase:** 7.5 (loadpull summary table). **Layer:** RfCore only — NO Avalonia/UI. **Depends on:** nothing
(can land independently). **Design:** `circuitRF/docs/design/loadpull-summary-table.md` §3, §5.

Goal: add the headless engine primitives the Summary Table's per-cell computation needs. Everything is
**presence-tolerant** (missing cube → `null`, never throw). All new code goes in
`<workspace>/RfCore/src/Loadpull/LoadpullSurface.cs`.

The summary table evaluates, per frequency `j`, at the MXP or MXE optimum load (already computed by
`MaxPower`/`MaxEfficiency`), each metric column. This brief adds the three accessors that read those values.

---

## Context (already on disk — do NOT re-add)

`LoadpullSurface` already has:
- `MaxPower(freqIdx, constraint, plane, z0, kernel, smooth, epsilon) → MxxResult?` and `MaxEfficiency(...)`,
  returning `MxxResult { Complex Measured, Complex Interpolated }` (the optimum coordinate in the fit plane).
- `Fit(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon) → LoadpullFit?` with `.Rbf.Evaluate(re, im)`.
- `Reduce(...)`, `ConstraintSpec.AtCompression(dB)`, `SurfacePlane{Gamma,Z}`.
- `BuildFreqSlices` reads cubes Pout, Gt, Gp, DE, PAE, PavlDbm (the `metricNames` array), plus GammaLoad, ZLoad.
- `Rbf2D` exposes `NodeCount`, `NodesRe[]`, `NodesIm[]`, `NodeValues[]`, `Evaluate(re, im)`.
- Private `FreqSlice` holds `DriveUps` (Dictionary<string,double[][]> keyed by canonical metric → per-grid
  drive-up array), `Gammas[]`, `Zs[]`, `NGrid`, `PinAxis`, `Compressions[]`, `FreqHz`.

The new accessors reuse these. Do not duplicate the fit/MXX logic.

---

## Add 1 — `MetricAtCoord` (the single per-cell primitive)

Public method. Given a frequency, a metric name, a coordinate (the optimum, in the fit plane), the constraint,
and a mode flag, return the scalar metric value there. Interp = evaluate the metric's RBF surface at the
coordinate; Nearest = the metric's measured node value at the nearest measured node to the coordinate.

```csharp
/// <summary>
/// Scalar value of <paramref name="metricY"/> at <paramref name="coord"/> (in the fit plane: Γ if
/// plane==Gamma, Z if plane==Z), at the given constraint.
///   nearest == false (Interp): evaluate the metric's RBF surface at coord.
///   nearest == true  (Nearest): value of the nearest measured node to coord.
/// Returns NaN if the metric cube is absent or the fit cannot be built (presence-tolerant).
/// Used by the summary table to read each metric column at the MXP/MXE optimum.
/// </summary>
public double MetricAtCoord(
    int freqIdx, string metricY, Complex coord, ConstraintSpec constraint,
    SurfacePlane plane, double? z0 = null,
    bool nearest = false,
    RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
{
    var fit = Fit(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon);
    if (fit == null || fit.Rbf.NodeCount == 0) return double.NaN;
    var rbf = fit.Rbf;

    if (!nearest)
        return rbf.Evaluate(coord.Real, coord.Imaginary);

    // Nearest measured node to coord.
    int    best   = 0;
    double bestD2 = double.PositiveInfinity;
    for (int i = 0; i < rbf.NodeCount; i++)
    {
        double dx = rbf.NodesRe[i] - coord.Real;
        double dy = rbf.NodesIm[i] - coord.Imaginary;
        double d2 = dx * dx + dy * dy;
        if (d2 < bestD2) { bestD2 = d2; best = i; }
    }
    return rbf.NodeValues[best];
}
```

Notes:
- `Fit` already caches by `(freqIdx, metricY, constraint, plane, z0, kernel, smooth, epsilon)`, so repeated
  calls across columns at the same constraint are cheap.
- `Fit` returns null when the metric cube is absent from `DriveUps` (it reduces to zero scatter) or when
  `< MinFitNodes` — both yield NaN here, which the UI renders as an omitted/blank cell. Good.
- The caller passes the optimum coordinate it already obtained from `MaxPower`/`MaxEfficiency`
  (`.Interpolated` for Interp mode, `.Measured` for Nearest mode) — but `MetricAtCoord` independently honors
  `nearest` for the *metric* surface. The UI passes a consistent pair (see §3 of the design doc).

---

## Add 2 — `SourceZ` (per-frequency source impedance)

Public method returning the source termination impedance for a frequency, assumed constant across load
terminations. Today the importer does NOT produce a source-Z cube (see brief 7.5g), so this must be
presence-tolerant and return null when absent. Wire it by canonical name now so it lights up when 7.5g (or a
later importer change) adds the cube.

```csharp
/// <summary>
/// Per-frequency source termination impedance (Ω), assumed constant across load terminations.
/// Reads a source-Z cube if present (canonical name "ZSource"); null when absent (presence-tolerant).
/// Read at grid point 0 (source termination does not vary with the load grid).
/// </summary>
public Complex? SourceZ(int freqIdx) => _freqs[freqIdx].SourceZ;
```

To populate it, extend `FreqSlice` and `BuildFreqSlices`:
- Add field `public Complex? SourceZ;` to the private `FreqSlice` class.
- In `BuildFreqSlices`, after building each freq slice, attempt to read a per-freq source-Z cube. The cube is
  optional. Two acceptable shapes (handle whichever exists; null if neither):
  - `ZSource` indexed `{freq, gridPoint}` (complex) → read `[fi, 0]`.
  - `ZSource` indexed `{gridPoint}` (single-freq) → read `[0]`.
  Use the existing `hasCube`/`GetCube` local helpers and the `DataCube` complex accessors already used for
  `GammaLoad`/`ZLoad` (`((DataCube)cube[...]).ComplexValues` or scalar indexer). Example:
```csharp
// inside BuildFreqSlices, per-freq, after slice is otherwise built:
Complex? srcZ = null;
if (hasCube("ZSource"))
{
    var zc = GetCube("ZSource");
    bool zHasFreq = zc.Axes.Any(a => a.Name == "freq");
    var sr = zHasFreq ? zc[fi, 0] : zc[0];
    if (sr.IsComplex) srcZ = sr.ComplexValue;
}
slices[fi].SourceZ = srcZ;   // or set via the FreqSlice initializer
```
> Confirm the exact `DataCube` scalar-accessor API against `RfCore/src/Data/DataCube.cs` (the codebase uses
> `cube[i, j]` returning a result with `IsComplex`/`ComplexValue`/`IsCube`/`Cube` — match the pattern already
> used in `BuildFreqSlices` for GammaLoad/ZLoad and in `GetMxx`).

---

## Add 3 — `OperatingPoint` (per-frequency bias scalar: VDD, Idq)

Public method returning a per-frequency operating-point scalar by canonical cube name (e.g. `BiasVLoad` for
VDD, `BiasILoad` for Idq). Per design decision: take the **first sample in the sweep** (grid point 0, pinStep
0) — these are constant over the Pin sweep. Presence-tolerant.

```csharp
/// <summary>
/// Per-frequency operating-point scalar from a bias cube (e.g. "BiasVLoad"=VDD, "BiasILoad"=Idq).
/// Returns the value at grid point 0, pinStep 0 (constant over the sweep). null if the cube is absent.
/// Stored unit is the cube's native unit (e.g. BiasILoad in Amps — caller scales to mA for display).
/// </summary>
public double? OperatingPoint(int freqIdx, string cubeName)
{
    if (!_freqs[freqIdx].DriveUps.TryGetValue(cubeName, out var driveUps)) return null;
    if (driveUps.Length == 0 || driveUps[0] is not { Length: > 0 } du0) return null;
    double v = du0[0];                      // grid 0, pinStep 0
    return double.IsNaN(v) ? (double?)null : v;
}
```

Prerequisite: `BuildFreqSlices` must include the bias cubes in its drive-up extraction. Today `metricNames` is
`{ "Pout", "Gt", "Gp", "DE", "PAE", "PavlDbm" }` — it does NOT include `BiasVLoad`/`BiasILoad`/`BiasVSrc`/
`BiasISrc`. Extend `metricNames` to include them so their drive-ups are captured:

```csharp
var metricNames = new[] { "Pout", "Gt", "Gp", "DE", "PAE", "PavlDbm",
                          "BiasVLoad", "BiasILoad", "BiasVSrc", "BiasISrc" };
```
This is safe: `metricCubes` only adds entries for cubes that pass `hasCube`, and the drive-up extraction loop
already iterates `metricCubes.Keys`. Absent bias cubes are simply skipped. (Also lets `MetricAtCoord` fit
surfaces for any future bias-vs-load analysis, though the summary reads bias via `OperatingPoint`, not a fit.)

> If you'd rather not widen `metricNames` (it makes those cubes available as fittable surfaces everywhere),
> the alternative is a separate optional-cube read in `BuildFreqSlices` that stashes a per-freq scalar for
> each bias cube. Widening `metricNames` is simpler and lower-risk; prefer it unless it causes a test to
> treat bias as a contour metric.

---

## Constraints / gotchas
- RfCore firewall: no Avalonia, no UI types. `Complex` is `System.Numerics.Complex`.
- RfCore has **no** TreatWarningsAsErrors, but keep it clean anyway (no unused locals).
- `Nullable enable` is on in RfCore — `Complex?`/`double?` returns are fine; guard NaN explicitly.
- Do not change existing public signatures of `MaxPower`/`MaxEfficiency`/`Fit`/`Reduce`.
- `_freqs` is the private `FreqSlice[]`; index callers pass `freqIdx` directly (no bounds re-check beyond what
  exists — match existing methods like `GridPointCount`).

## Tests (RfCore.Tests — add to or alongside `LoadpullSurfaceTests.cs`)
Use the existing test loadpull DataSet fixture (the tests already construct a `LoadpullSurface`; reuse that
setup). Add:
1. **MetricAtCoord Interp ≈ surface eval.** For a known freq + constraint, get `MaxPower(...).Interpolated`,
   call `MetricAtCoord(freq, "Pout", optimum, AtCompression(c), plane, nearest:false)`; assert it equals
   `Fit(freq,"Pout",...).Rbf.Evaluate(optimum.Real, optimum.Imaginary)` (same value, since that's the impl) and
   is finite.
2. **MetricAtCoord Nearest returns a node value.** Call with `nearest:true`; assert the result equals one of
   the metric's `Rbf.NodeValues` (it must be an actual measured node value).
3. **MetricAtCoord absent metric → NaN.** Call with a bogus metric name (`"NopeMetric"`); assert `double.IsNaN`.
4. **OperatingPoint present/absent.** If the fixture has a bias cube, assert `OperatingPoint(freq,"BiasVLoad")`
   is non-null and finite; assert `OperatingPoint(freq,"NopeCube")` is null. (If the fixture lacks bias cubes,
   assert the absent case only and note the present case is covered once 7.5g adds bias data.)
5. **SourceZ absent → null.** With a fixture lacking `ZSource`, assert `SourceZ(freq)` is null. (Present case
   deferred to 7.5g.)
