---
name: project-brief-7.4g-loadpull-source-entry-point
description: Phase 7.4g: KnownLoadpullProvider + loadpull block in RefreshAvailableDataSources + GetKnownLoadpullFiles in WorkspaceViewModel; simulated path already wired; 6 gate tests; 2057 total — completed 2026-06-20
metadata:
  type: project
---

Phase 7.4g — Loadpull data-source entry point. Gap was only in discovery/listing: loaders for `.spl`/`.lpcwave` already existed in `DataSourceLibraryViewModel`; they just weren't *offered* in `AvailableDataSources`.

**Changes:**
- `DataSourceLibraryViewModel.KnownLoadpullProvider` added (sibling to `KnownTouchstoneProvider`)
- Loadpull block added in `RefreshAvailableDataSources` after Touchstone block (uses `IsSplExtension`/`IsLpcwaveExtension`, assigns `SourceKind.Spl`/`SourceKind.Lpcwave`, LogicalId = abs path)
- `WorkspaceViewModel.GetKnownLoadpullFiles()` added (filters `cws.KnownFiles` to `.spl`/`.lpcwave`)
- `WireDataDisplayLibraryEvents` wires `lib.KnownLoadpullProvider = GetKnownLoadpullFiles`
- File picker filter (§1c) was already complete prior to this brief

**Simulated path (§2):** confirmed already wired — LP analysis → `LoadpullEngine.Run()` → `grouped` DataSet → `RunResultsWriter.WriteRun()` → `run.npy` → scanned by `RefreshAvailableDataSources` sim-run block. No code changes needed.

**Tests:** `LoadpullSourceEntryTests.cs` — 6 gate tests covering: `.spl` in provider → `AvailableDataSources` with `SourceKind.Spl`; `.lpcwave` → `SourceKind.Lpcwave`; null provider no-throw; `SelectDataSourceAsync` on `.spl`/`.lpcwave` LogicalId loads entry; both providers coexist.

**Why:** [[project-brief-7.4e-contour-trace-card]] needed a data source to bind — without this, the whole 7.4 contour stack had no UI entry point.

2057 total tests. Phase 7.4 COMPLETE.
