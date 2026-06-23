# Brief: Polyline (cube-X) marker glitchy during drag on Smith/Polar

## Symptom (reported by User)
Dragging a `MarkerKind.Polyline` marker on a **Smith chart** with an S-parameter
**cube** trace (e.g. `SP1.S[:, 1, 1]`) is glitchy — the marker jumps to the wrong
point during the drag. Suspected "nearest point calculation" bug. Confirmed root cause below.

## Root cause (verified in source)
`SP1.S[:, 1, 1]` is a **cube-bound** trace (a cube slice expression), NOT a network
SNP trace. On a Smith/Polar plot its `Trace.Points` are a 2-D locus of `(Re, Im)` of Γ.
Its markers are `IsCubeXMarker` (`IsCubeBound && !IsContourTrace && !IsHarmonicStem`).

The cube-marker position logic in `src/Ui/DataDisplay/Models/Trace.cs` resolves the
marker by **X-only** distance, which is correct for a Rect Pin-sweep (X = swept var,
unique & monotonic) but WRONG on Smith/Polar where the trace loops in 2-D and many
points share a similar real part:

- `SnapToCubeMarker(worldPt)` — non-family branch uses `Math.Abs(Points[i].X - worldPt.X)`
  (X-only). (The family branch already uses 2-D `Dist` — correct.)
- `CubeMarkerIndex(Marker m)` — matches `Math.Abs(pts[i].X - m.PositionStatic.X)` (X-only),
  used at DRAW time via `GetMarkerDataLocation → CubeMarkerPointFor`.

Marker storage: `PositionStatic = (CubeX, CurveIndex)` where `.X` = the snapped cube
X-value and `.Y` = bound family-curve index (0 for single-curve). So for single-curve
traces `.Y` is unused.

So on Smith: during drag, `SnapToCubeMarker` picks the wrong sample (X-only), and even
when it picks right, `CubeMarkerIndex` re-resolves by X-only at draw time and can land
on a different point that shares the real part → glitch.

## Required behavior
On Smith/Polar (a complex/2-D plane), cube-X marker snapping AND draw-time resolution
must use **2-D Euclidean** nearest. On Rect, keep the existing X-only behavior (so the
marker tracks the swept variable; 2-D would also usually be fine but do not risk the
heavily-used HB Pin-sweep Rect path).

## Recommended approach (lowest-risk)
The trace does not know the plot type at the relevant call sites, so thread a
`bool complexPlane` (or pass `PlotType`) through:

1. **Snapping (PlotControl already knows plot type).** In
   `src/Ui/DataDisplay/Controls/PlotControl.cs`:
   - `TryAddCubeMarker` and `MoveMarkerToCanvasPoint` (the `IsCubeXMarker` branch) call
     `trace.SnapToCubeMarker(world)`. Add an overload
     `SnapToCubeMarker(Vector2 worldPt, bool complexPlane)` where, for the **non-family**
     branch, when `complexPlane` is true it searches by 2-D `Dist` instead of X-only.
     Pass `complexPlane: !_plot.PlotType.IsRect()` (i.e. Smith/Polar → true).
   - The family branch is already 2-D; leave it.

2. **Draw-time resolution.** `CubeMarkerIndex` / `CubeMarkerPointFor` are reached from
   `GetMarkerDataLocation(Marker)` which has no plot type. Two options:
   - (Preferred) For **single-curve** cube traces, change marker storage so the snapped
     2-D world point is recoverable. Since `.Y` is unused for single-curve, you cannot
     store both CubeX and a world-Y there without ambiguity vs families. Instead, resolve
     by 2-D nearest to a stored cube-X is insufficient. Simplest robust fix: have
     `GetMarkerDataLocation` for a single-curve cube trace, when the trace's `Points` are
     a 2-D locus, find the Points index whose X is nearest CubeX AND, among ties / near-ties
     in X, the one nearest in 2-D to the *previously drawn* location. This is fragile.
   - (Cleaner) Give `Trace` an injected `Func<PlotType>` or a `PlotType LastPlotType`
     field set in `BuildPath`/`SetCubeData` (the trace is always rebuilt for a specific
     plot type). Then `CubeMarkerIndex` can branch: complex plane → 2-D nearest to
     `PositionStatic` interpreted as a world point. BUT `PositionStatic.X` currently
     stores CubeX (display-X), not Re. So for the complex-plane case, store the full
     snapped `(Re, Im)` in `PositionStatic` for single-curve cube markers and resolve by
     2-D nearest; keep `(CubeX, CurveIndex)` only for Rect and families.

   Recommendation: add `private PlotType _lastPlotType;` set in `BuildCubePath`
   (and the family/scalar setters) to the `plotType` arg. Then:
     - In `SnapToCubeMarker` non-family: if `_lastPlotType` is Smith/Polar, snap 2-D and
       return `CubeX = Points[best].X` BUT ALSO store the full point — see next.
     - For single-curve cube markers on a complex plane, store
       `PositionStatic = (Re, Im)` (the snapped world point) and have `CubeMarkerIndex`
       (when `_lastPlotType` is complex) pick the Points index nearest in 2-D to
       `PositionStatic`. On Rect keep `(CubeX, 0)` + X-only.
   This keeps families (which use `.Y` for curve index) on Rect-only — families on Smith
   are already 2-D-snapped for ADD/DRAG but have the same draw-time X-only issue; if
   families on Smith are in scope, they need the curve index elsewhere. Confirm with
   the owner whether cube FAMILIES on Smith are a real case; the reported bug is single-curve.

## Acceptance
- Drag a marker on `SP1.S[:,1,1]` on a Smith chart: marker follows the cursor smoothly
  and stays on the nearest 2-D locus point; no jumping.
- Rect HB Pin-sweep cube markers (single-curve and family) unchanged.
- Re-resolution after redraw (zoom/pan) keeps the marker on the same logical point.

## Files
- `src/Ui/DataDisplay/Models/Trace.cs` — `SnapToCubeMarker`, `CubeMarkerIndex`,
  `CubeMarkerPointFor`, `CubeMarkerPoints`, `BuildCubePath`/setters (add `_lastPlotType`).
- `src/Ui/DataDisplay/Controls/PlotControl.cs` — `TryAddCubeMarker`,
  `MoveMarkerToCanvasPoint` (IsCubeXMarker branch): pass `complexPlane`.

## Notes / constraints
- Architectural firewall: Trace holds no Avalonia; `PlotType` is a Core/UI enum already
  used in Trace (`BuildPath(PlotType, ...)`), so storing `_lastPlotType` is fine.
- TreatWarningsAsErrors=true on UI/Core. Watch unused-field warnings.
- Verify on disk; instrument (Console.WriteLine of snap index + 2-D vs X-only choice)
  if the draw-time path is unclear before finalizing.
