# Brief — Markers Gate 3a: VSWR locus API + overlay rendering — COMPLETE

**Status:** Complete — 2026-06-21
**Tests:** 2129 total (4 firewall + 334 core + 1379 Ui + 412 engine), 0 failures, build 0W/0E.

## What was done

### Part A — RfCore: `LoadpullSurface.VswrLocus` (public static)
- Added `public static Complex[] VswrLocus(Complex center, double vswr, SurfacePlane plane, Complex z0ref, int nPoints = VswrNPoints)` to `LoadpullSurface`.
- For `SurfacePlane.Gamma`: maps Γ center → Z using full complex `z0ref`, builds Z-plane circle via existing `VswrCircleZ`, maps each point back to Γ normalized to `z0ref`. Imaginary part of z0ref preserved end-to-end.
- For `SurfacePlane.Z`: delegates directly to `VswrCircleZ`.
- `VswrBoundingBox` refactored to one-liner `=> BoundingBox(VswrLocus(center, vswr, plane, new Complex(z0ref ?? 50.0, 0.0)))` — behavior-preserving (real z0ref widened to `re + 0j`).
- `VswrCircleZ`, `BoundingBox`, `VswrCirclePoints`, `VswrNPoints` left unchanged.

### Part B1 — Ui: `MarkerRenderer.DrawVswrLocus`
- Added `using System.Numerics;` to `TraceRenderer_MarkerRenderer.cs`.
- Added `public static void DrawVswrLocus(SKCanvas canvas, (double W, double H) canvasSize, Marker marker, Trace trace, TransformSet tf, RfCore.Loadpull.SurfacePlane plane, Complex z0Ref)`.
- No-ops when `!marker.VswrEnabled`. Builds locus via `LoadpullSurface.VswrLocus`, draws red closed polyline via `tf.ToCanvas`. Stroke width proportional to `Min(W,H)/200`.

### Part B2 — Ui: `PlotRenderer.Draw` call site + `VswrAvailableFor`
- Added `using System.Numerics;` to `PlotRenderer.cs`.
- In the `detail == PlotDetail.Full` marker-symbol block: for each marker with `VswrEnabled && VswrAvailableFor(plot, trace, marker)`, resolves `vplane` (Gamma for Smith/Polar, Z for Rect) and `z0Ref` (full `trace.Z0`; fallback to `50+0j` only when Z0 is zero), calls `DrawVswrLocus` BEFORE `DrawSymbol` so glyph sits on top.
- Added `private static bool VswrAvailableFor(Plot plot, Trace trace, Marker marker)`: returns true for Smith/Polar (any marker); for Rect, returns `trace.IsContourTrace`; false for Table.

## Key facts for Gate 3b
- `VswrNPoints` = 100 (default used by `VswrLocus`). 3b may pass a higher count for smooth drag.
- No interaction wired — locus is static from `marker.VswrValue`. `VswrEnabled` toggle UI is Gate 5.
- The `VswrAvailableFor` gate exactly mirrors §6.1 without over-reaching.
