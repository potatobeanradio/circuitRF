# Brief — Markers Gate 0: Complete

**Status:** Complete  
**Date:** 2026-06-21

## What was done

Three files edited, no behavior change, build 0W/0E (warnings are pre-existing in RfCore).

### Task 1 — `Marker.cs`
- Added `public enum MarkerKind { Polyline, Spectrum, StabilityCircle, Table, Contour }` beside `MarkerStyle`
- Added 5 properties to `Marker`: `MarkerKind`, `ShowInfoBox` (default true), `ContourSnapped`, `VswrEnabled`, `VswrValue` (default 2.0)
- Extended copy constructor with all 5 fields

### Task 2 — `DataDisplayConfig.cs`
- Added 5 matching defaulted properties to `MarkerConfig` with `[JsonConverter(typeof(JsonStringEnumConverter))]` on `MarkerKind`

### Task 3 — `DataDisplayViewModel.cs`
- **config → marker** (load): object-initializer around line 1071 — `LoadPlotContainerConfigAsync` path
- **marker → config** (save): `BuildTraceConfig` method around line 1297 — the `foreach (var m in t.Markers)` block

## Map site method names
- **config → marker**: inline object-initializer block inside `LoadPlotContainerConfigAsync` (private async method)
- **marker → config**: inline object-initializer block inside `BuildTraceConfig` (private method, called by `BuildPlotContainerConfig`)
