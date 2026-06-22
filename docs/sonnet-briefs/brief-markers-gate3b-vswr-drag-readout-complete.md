---
name: project-brief-markers-gate3b-vswr-drag-readout
description: Gate 3b: VSWR locus drag-to-resize + live readout; HitTestVswrLocus+ResolveVswrPlaneAndZ0+DistPointToSegment in PlotControl; VswrReadout struct; vswrReadout param on Draw(); VswrAvailableFor made internal — completed 2026-06-21
metadata:
  type: project
---

Gate 3b: Interactive VSWR circle drag-to-resize with live readout.

**Why:** Locus drawn in 3a needed interactivity — users drag the red ring to set marker VswrValue live.

**What landed:**
- `HitTestVswrLocus(Point)` in PlotControl: iterates traces/markers reverse, calls `LoadpullSurface.VswrLocus` for the same points the renderer draws, checks per-segment `DistPointToSegment` within `grabPx` threshold
- `ResolveVswrPlaneAndZ0(Trace)`: Smith/Polar → SurfacePlane.Gamma; else → Z; z0Ref fallback 50+0j
- `DistPointToSegment` static helper (clamped-t standard formula)
- 4 drag state fields: `_draggingVswrMarker`, `_draggingVswrTrace`, `_vswrReadoutPt`, `_vswrReadoutActive`
- `OnPointerPressed`: locus hit checked after glyph hit (glyph wins priority)
- `OnPointerMoved`: updates `VswrValue` only (marker does not move); Γ plane → `VswrFromGamma`; Z plane → normalize by `trace.Z0` then `VswrFromZ`; fires `MarkerMoved` for InfoBox refresh
- `OnPointerReleased`: clears drag state, hides readout, fires `PlotChanged` to persist
- `VswrReadout readonly record struct` at top of PlotRenderer.cs namespace
- `VswrAvailableFor` changed from private → internal static so PlotControl can call it
- `vswrReadout` optional param on `PlotRenderer.Draw()`; readout text drawn at pointer+10,-10 offset in red PlexBold (fixed offset — outward-vector noted as acceptable for 3b)

**How to apply:** Gate 5 (property-editor VSWR field + enable/disable UI) is the next markers milestone. Do not add a VSWR field to the property panel until Gate 5 brief.
