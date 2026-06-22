# Brief — Markers Gate 2b: Contour marker behavior — COMPLETE

**Status:** Complete (2026-06-21)
**Tests:** 0W/0E build; 1379 Ui.Tests + 334 Core.Tests + 412 Engine.Tests + 4 Firewall.Tests pass

## What was done

### Task 1 — `Trace.GetMarkerDataLocation`
Added `IsContourTrace` branch before `IsCubeBound` guard so contour markers return `m.PositionStatic` 
(the world Γ/Z coordinate) rather than `Vector2.Zero`. HitTestMarker and glyph rendering now work.

### Task 2 — `Trace.ResolveContourMarkerPosition`
New public method on `Trace.cs`. Mode 1 (free, ContourSnapped=false) returns worldPt unchanged.
Mode 2 (snapped, ContourSnapped=true) invokes `ContourData.NearestNode` and returns the snapped grid coord.

### Task 3a — `PlotControl.TryAddContourMarker` + add paths
- New private `TryAddContourMarker(trace, canvasPt)` helper: creates a `Marker` with `MarkerKind=Contour`,
  calls `ResolveContourMarkerPosition`, fires `MarkerAdded`.
- `AddMarkerAtCanvasPoint`: early-return via `TryAddContourMarker` before `FindNearestTraceData`.
- `TryAddMarkerNearPoint`: contour fallback at end — if polyline miss, picks first contour trace and adds.

### Task 3b — `MoveMarkerToCanvasPoint`
Added contour branch first: converts canvas → world, clips to viewport, calls `ResolveContourMarkerPosition`
(respects Mode 1/2), returns. Drag follows mode automatically.

### Task 4 — `Trace.BuildMarkerBoxLines` contour branch
Added early-return contour branch at top. Shows:
- marker name (bold)
- `{metric}={value} (interp)` for Mode 1, `{metric}={value}` for Mode 2
- `Γ={coord}` or `Z={coord}` depending on `ContourData.GammaPlane`

`ContourData.GammaPlane` (bool, non-serialized) added to `ContourData`; set in `TraceRowViewModel.RebuildContour`
from `plane == SurfacePlane.Gamma`; cleared in `ClearContourGrid`.

### Task 5 — `PopulateMarkerMenu` "Snap to Point" toggle
Added optional `onContourModeToggled: Action?` parameter (existing callers unchanged).
For `MarkerKind.Contour` markers, adds a separator + "Snap to Point" menu item with CheckboxOutline/CheckboxBlankOutline
icon reflecting current `ContourSnapped` state. On click: toggles `ContourSnapped`, calls `ResolveContourMarkerPosition`
to re-snap position, fires callback.

Wired at both call sites:
- `MarkerInfoBoxView.RebuildContextMenu` → `() => Vm.RequestRedraw()`
- `PlotControl.ShowMarkerContextMenu` → `() => { InvalidateVisual(); PlotChanged; MarkerMoved; }`
