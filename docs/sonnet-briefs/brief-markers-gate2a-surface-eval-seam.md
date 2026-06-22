# Brief — Markers Gate 2a: Contour surface-evaluation seam (plumbing only)

**Status:** Ready to implement
**Scope:** Give the marker/render path a way to evaluate a contour trace's load-pull surface at an arbitrary Γ/Z (interpolated) and to snap a Γ/Z to the nearest measured grid node — **without** `Trace` or `ContourData` holding a `DataSet` or `LoadpullSurface` type in a way that breaks the firewall. **No marker behavior changes in this brief.** This is pure plumbing that Gate 2b will consume.
**Design ref:** `/docs/design/trace-markers-design.md` §5.5, §12 "Gate 2", and resolved Q1. Read those first.
**Depends on:** Gates 0 + 1 (landed).

---

## Why this exists (read once)

A contour marker must, at drag/read time, evaluate the load-pull surface:
- **Mode 1 (free/interpolated):** metric value at an arbitrary Γ/Z → `LoadpullSurface.MetricAtCoord(..., nearest: false)`.
- **Mode 2 (snapped/grid):** exact value at the nearest measured node, and the node's coordinate to snap the glyph to → `MetricAtCoord(..., nearest: true)` plus a nearest-node coordinate.

The problem: `LoadpullSurface` is owned **privately** by `TraceRowViewModel` (built in `EnsureLoadpullSurface()`, used in `RebuildContour()`), and that VM only exists while the inspector is open. Markers must work without it. `Trace` deliberately holds **no** `DataSet` (firewall). So the marker path (in `src/Ui`, which *may* call RfCore) needs a surface entry point that survives independently of the VM.

## Recommended architecture (do this)

Cache two **self-contained delegates** on `ContourData`, populated in `TraceRowViewModel.RebuildContour()` where the surface and all query params are already in scope. They close over the VM-owned `LoadpullSurface`; `ContourData` only sees `Func<...>`, never the `LoadpullSurface` type. They are **non-serialized derived state**, exactly like `ContourData.Grid` (which is also rebuilt by `RebuildContour`). This keeps the model/persistence layer free of any surface dependency and needs no firewall change.

Add to `ContourData` (in `src/Ui/DataDisplay/Models/ContourData.cs`):

```csharp
// ---- Marker surface-evaluation hooks (non-serialized; set by RebuildContour) ----
// Both are null until the contour has been fitted. Markers must null-check and
// fall back to NaN / identity when null (e.g. before first fit, or fit failed).

/// <summary>Evaluates the contour metric at a coordinate in the fit plane (Γ if Smith/Polar, Z if Rect).
/// snapped=false → RBF-interpolated surface value (Mode 1); snapped=true → nearest measured node value
/// (Mode 2). Returns NaN when the surface can't be evaluated. Set by RebuildContour.</summary>
public Func<Complex, bool, double>? EvaluateMetric { get; set; }

/// <summary>Maps an arbitrary coordinate (fit plane) to the nearest measured grid-node coordinate,
/// for Mode-2 glyph snapping. Returns the input unchanged when unavailable. Set by RebuildContour.</summary>
public Func<Complex, Complex>? NearestNode { get; set; }
```

`Complex` is `System.Numerics.Complex` — `ContourData.cs` already `using System.Numerics;`.

Clear both in `ClearContourGrid` (so a failed/empty fit doesn't leave stale closures):

```csharp
cd.EvaluateMetric = null;
cd.NearestNode    = null;
```

(`Clone()` already deliberately leaves derived/cached state null — do **not** copy these two in `Clone()`; the pasted trace re-fits and repopulates them.)

## Populate the delegates in `RebuildContour` (TraceRowViewModel.cs)

`RebuildContour()` already computes `surface`, `freqIdx`, `constraint`, and `plane`, then calls `surface.Fit(...)`. After the existing successful-fit work (where it sets `cd.Grid`, `cd.Scatter`, `cd.Levels`, `cd.MxpCoord`, `cd.MxeCoord`), add the two closures. Capture **locals**, not `cd`-mutating state, so the closures stay correct:

```csharp
// Marker surface-evaluation hooks — capture locals so the closures are stable.
var      evalSurface = surface;
int      evalFreq    = freqIdx;
string   evalMetric  = cd.MetricName;
var      evalConstr  = constraint;
var      evalPlane   = plane;
RbfKernel evalKernel = cd.InterpKernel;
double   evalSmooth  = cd.Smoothing;
double?  evalEps     = cd.Epsilon;

cd.EvaluateMetric = (coord, snapped) =>
    evalSurface.MetricAtCoord(evalFreq, evalMetric, coord, evalConstr, evalPlane,
        nearest: snapped, kernel: evalKernel, smooth: evalSmooth, epsilon: evalEps);

// Nearest measured node: reuse the scatter reduction's measured coordinates for this fit.
var nodeCoords = scatter.Coords;   // Complex[] of measured nodes in the fit plane
cd.NearestNode = coord =>
{
    if (nodeCoords is null || nodeCoords.Length == 0) return coord;
    int best = 0; double bestD2 = double.PositiveInfinity;
    for (int i = 0; i < nodeCoords.Length; i++)
    {
        double dx = nodeCoords[i].Real - coord.Real;
        double dy = nodeCoords[i].Imaginary - coord.Imaginary;
        double d2 = dx * dx + dy * dy;
        if (d2 < bestD2) { bestD2 = d2; best = i; }
    }
    return nodeCoords[best];
};
```

Notes:
- `scatter` is the existing local `var scatter = surface.Reduce(freqIdx, cd.MetricName, constraint, plane);` already computed in `RebuildContour` — reuse it; don't recompute.
- `MetricAtCoord` already does nearest-node *value* selection internally for `nearest:true`. `NearestNode` is only for snapping the glyph's **coordinate**; the *value* still comes from `EvaluateMetric(coord, snapped:true)`. Using the same `scatter.Coords` for both keeps them consistent.
- In the failure paths (`fit is null`, surface absent) `RebuildContour` calls `ClearContourGrid(cd)` — that now nulls the delegates. Good.

## Context (already verified — do not re-investigate)

- `LoadpullSurface.MetricAtCoord(int freqIdx, string metricY, Complex coord, ConstraintSpec constraint, SurfacePlane plane, double? z0 = null, bool nearest = false, RbfKernel kernel = ..., double smooth = 1e-3, double? epsilon = null)` exists and returns NaN when the fit can't be built. (RfCore/src/Loadpull/LoadpullSurface.cs)
- `RebuildContour()` already derives `plane = (PlotType is Smith or Polar) ? Gamma : Z` and builds `constraint` from `cd.ContourConstraintKind`/`ConstraintValue`. Reuse those exact locals.
- `ContourData.Grid`, `Scatter`, `Levels`, `MxpCoord`, `MxeCoord` are all non-serialized and rebuilt each `RebuildContour` — the two new delegates follow the identical lifecycle.
- `z0` is left at its default (null) here, matching how `RebuildContour` already calls `Fit`/`Resample` (no explicit z0).

## UI/Core build gate

UI builds with `TreatWarningsAsErrors=true`. The new `ContourData` properties are public auto-properties (no unused-field warning). The closures capture locals (no warning). Don't add unused usings.

## Out of scope (do NOT do in 2a)

- No `Marker`/`MarkerRenderer` changes.
- No hit-test / add / drag changes in `PlotControl`.
- No `GetMarkerDataLocation` / `BuildMarkerBoxLines` changes.
- No context-menu / inspector changes.
- Do **not** wire anything to actually call `EvaluateMetric`/`NearestNode` yet — Gate 2b does that. After 2a, the delegates are populated but unused.

## Acceptance / verification

1. **Build green** (UI + Core, warnings-as-errors).
2. Open a load-pull contour plot; confirm it still renders identically (no behavior change).
3. **Temporary smoke test (then revert):** in `RebuildContour`, right after setting the delegates, add:
   ```csharp
   if (cd.EvaluateMetric is not null && cd.MxpCoord is { } mxp)
       Console.WriteLine($"[CTReval] mxp interp={cd.EvaluateMetric(mxp, false):F3} " +
                         $"node={cd.EvaluateMetric(mxp, true):F3} " +
                         $"snap={cd.NearestNode!(mxp)}");
   ```
   Run, open/refresh a contour, confirm the printed interpolated value is finite and close to the metric's peak (e.g. for Pout it should read near the max dBm), and `node`/`snap` are sensible. **Then remove the Console.WriteLine.**

## Report back

- Confirm build is green and the contour still renders unchanged.
- Paste the one `[CTReval]` line from the smoke test, then confirm you removed it.
- Confirm `ClearContourGrid` nulls both delegates and `Clone()` does not copy them.
