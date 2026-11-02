# Brief — Markers Gate 2a: Contour surface-evaluation seam — COMPLETE 2026-06-21

## What was done

Added two non-serialized delegate properties to `ContourData` that give the marker/render path a
firewall-safe way to query the load-pull surface without holding a `LoadpullSurface` reference.

**`ContourData.EvaluateMetric : Func<Complex, bool, double>?`**  
Evaluates the contour metric at any Γ/Z coordinate. `snapped=false` → RBF interpolated value;
`snapped=true` → nearest measured-node value. Returns NaN when unavailable.

**`ContourData.NearestNode : Func<Complex, Complex>?`**  
Maps an arbitrary coordinate to the nearest measured grid-node coordinate (for Mode-2 glyph snapping).
Returns the input unchanged when unavailable.

## Files changed

- `src/Ui/DataDisplay/Models/ContourData.cs` — added two delegate properties after `MxeCoord`.
- `src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`:
  - `ClearContourGrid` nulls both delegates.
  - `RebuildContour` populates both closures after successful fit, capturing locals (`evalSurface`,
    `evalFreq`, `evalMetric`, `evalConstr`, `evalPlane`, `evalKernel`, `evalSmooth`, `evalEps`,
    `nodeCoords = scatter.Coords`).

## Lifecycle

Same as `Grid`/`Scatter`/`MxpCoord`: rebuilt each `RebuildContour`, nulled by `ClearContourGrid`.
`Clone()` deliberately omits them — the pasted trace re-fits and repopulates.

## Test result

Build 0W/0E; 2128 total tests pass (334 Core + 1379 Ui + 412 Engine + 4 Firewall).
