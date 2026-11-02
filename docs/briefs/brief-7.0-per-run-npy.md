# Sonnet Brief — Phase 7.0: per-run `.npy` results writer (data-path spine)

**Design:** `docs/design/data-display.md` §1.3, §2.2, §3 (7.0). Read that §3 "7.0" block first — this brief
implements exactly it. **Scope is 7.0 only: the data path that writes simulation results to disk in the
canonical, addressable form. NO Data Display UI, NO plotting, NO trace model.** Those are 7.1+.

## Goal
On a successful simulation run, write the run's DataSet(s) to disk as `.npy` at a stable, collision-safe,
workspace-external path, so a later Data Display can plot them by file path. Confirm the
`export → import` round-trip holds for every analysis type.

## Background (already built — consume, do not modify)
- Every engine path returns a `RfCore.Data.DataSet` (S-param, HB, parametric sweep, loadpull). The run
  entry point `SchematicRunService.RunNetlist(netlistPath)` (`src/Ui/Schematic/SchematicRunService.cs`)
  already collects them into `RunResult.DataSets` and is called from `WorkspaceViewModel.RunAnalysis`
  after it writes `netlist.cnl`.
- `RfCore.Export.DataSetExporter.Export(ds, path, ExportFormat.Npy)` writes a `.npy`;
  `RfCore.Export.DataSetImporter.Import(path)` returns `(DataSet, ImportedLinearNetwork?)`. **Use these
  as-is** — default `ExportOptions` (no linear-network payload; Level-2 is a later concern). Do **not**
  pass a linear payload or set `IncludeLinearNetwork` in 7.0.
- `netlist.cnl` is already written to the **workspace root** for a saved workspace, and to the
  **recovery session dir** for scratch (see `project-file-formats.md` "netlist.cnl on simulate" and
  `scratch-and-save-lifecycle.md` §1.3). The results writer roots `results/` at that **same base
  directory** — do not invent a new base-dir resolution.

## The naming rule (LOCKED — `data-display.md` §3 / 7.0)
Write **one `.npy` per analysis** at:

```
<baseDir>/results/<schematicKey>/<analysisName>.npy
```

- `<baseDir>` = the same base dir the run uses for `netlist.cnl` (workspace root when a workspace is open;
  the `RecoveryManager.SessionDir` for scratch / no-workspace). `results/` is therefore **external to the
  cell folder structure** — never inside a cell.
- `<schematicKey>` is a pure function of the active schematic's own identity (stable as siblings change):
  - **Scratch** (`SchematicDocument.FilePath` is null): `Sanitize(doc.Id)` — the tab title, e.g.
    `Untitled-Schematic-1`.
  - **Cell-homed** (`FilePath` matches `…/<Cell>/schematic/<View>.csch`, i.e. the parent dir is named
    `schematic`): cell = the directory **above** `schematic/` (its leaf name); view = file stem. Key =
    `cell` when `view == cell`, else `$"{cell}.{view}"`.
  - **Loose** (`FilePath` set but not under a `…/<Cell>/schematic/` layout): key = file stem.
- `<analysisName>` = the analysis's own name (`Analysis.Name`, or the parsed name of a raw `sparam`
  directive). Each file is a **clean single-analysis DataSet with canonical cube names** (no
  analysis-prefixing — `ds.S(2,1)` / `ds.V(...)` must still resolve). `Sanitize` analysis names for the
  filename.
- **Collision = detect-and-warn (Option A).** A `results/<schematicKey>/` directory carries a `.source`
  marker file containing the owning schematic's identity. On write, if the dir already exists with a
  `.source` naming a **different** owner, do **not** rename/suffix/clobber — post an `IMessageSink.Warning`
  ("`results/<key>/` belongs to a different cell — rename one cell to avoid a results collision") and
  return without writing. Same owner (or new dir) → proceed.
  - **Owner identity:** cell-homed → `Path.GetFullPath(<cell folder>)` (the dir above `schematic/`);
    loose → `Path.GetFullPath(FilePath)`; scratch → `"scratch:" + doc.Id`. Compare with
    `OrdinalIgnoreCase`.

## Code changes

### 1. `SchematicRunService` — carry analysis names with DataSets
`src/Ui/Schematic/SchematicRunService.cs`. The DataSets currently travel without their analysis name;
the writer needs the name to build the filename.
- Add a record: `public sealed record AnalysisResult(string Name, DataSet Data);`
- Change `RunResult` to hold the authoritative named list, keeping a `DataSets` convenience so existing
  readers/tests still compile:
  ```csharp
  public sealed class RunResult(
      RunStatus status, string statusMessage, IReadOnlyList<AnalysisResult>? results = null)
  {
      public RunStatus Status { get; } = status;
      public string StatusMessage { get; } = statusMessage;
      public IReadOnlyList<AnalysisResult> Results { get; } = results ?? [];
      public IReadOnlyList<DataSet> DataSets => Results.Select(r => r.Data).ToList(); // convenience
  }
  ```
- In `RunNetlist`, build `List<AnalysisResult>`: typed analyses → `new AnalysisResult(analysis.Name, ds)`;
  the raw-sparam path → `new AnalysisResult(name, ds)` (the parsed name). **Within-run duplicate-name
  guard:** if a name is already used in this run, suffix `_2`, `_3`, … so two analyses never write the
  same file. (Cross-cell collisions are handled by the writer's `.source` check, not here.)
- Update the success-path construction and any direct `new RunResult(...)` sites (and tests in
  `Ui.Tests/SchematicRunServiceTests.cs`) to the new `results` parameter. `Status` / `StatusMessage` /
  `DataSets` reads are unchanged.

### 2. New `RunResultsWriter` — framework-free helper
`src/Ui/Schematic/RunResultsWriter.cs`. **No Avalonia/Skia** (it must pass `Firewall.Tests` and be unit-
testable like `CellFolder` / `SavePlanExecutor`). Static methods:
- `public static string SchematicKey(string? filePath, string scratchId)` — the rule above.
- `public static string OwnerIdentity(string? filePath, string scratchId)` — the rule above.
- `public static void WriteResults(string baseDir, string schematicKey, string ownerIdentity,
      IReadOnlyList<AnalysisResult> results, IMessageSink? messages)`:
  1. If `results.Count == 0`, return.
  2. `dir = Path.Combine(baseDir, "results", schematicKey)`. `source = Path.Combine(dir, ".source")`.
  3. **Collision check:** if `Directory.Exists(dir)` and `File.Exists(source)` and the trimmed contents of
     `source` differ from `ownerIdentity` (OrdinalIgnoreCase) → `messages?.Warning(…, dir)` and **return**.
  4. `Directory.CreateDirectory(dir)`; write `ownerIdentity` to `.source` (atomic-ish: it's tiny, plain
     `File.WriteAllText` is fine).
  5. **Clear stale outputs:** delete existing `*.npy` in `dir` (leave `.source`) so a removed analysis
     does not orphan a file.
  6. For each `r` in `results`: `npy = Path.Combine(dir, Sanitize(r.Name) + ".npy")`;
     `DataSetExporter.Export(r.Data, npy, ExportFormat.Npy);`.
  7. Report once via `messages?.Success($"Results written: {schematicKey} ({results.Count} analysis file(s))", dir)`
     (the `filePath`/dir arg makes it a clickable reveal link in Messages).
  8. Wrap the write body so an I/O failure posts `messages?.Warning(...)` and does not throw — results
     writing must never break the run.
- Private `Sanitize(string)` — replace `Path.GetInvalidFileNameChars()` with `_` (mirror
  `RecoveryManager.SafeFileName`, without the `.csch` suffix). 
- `DataSet` / `AnalysisResult` are from `RfCore.Data` / `CircuitRF.Ui.Schematic`; `IMessageSink` from
  `CircuitRF.Ui.Messages`.

### 3. Hook into the run path
`src/Ui/ViewModels/WorkspaceViewModel.cs`. Locate `RunAnalysis` (the method that writes `netlist.cnl`
then calls `SchematicRunService.RunNetlist`). **After** a `RunStatus.Success` result, and reusing the
**same base directory** that `netlist.cnl` was written to (workspace root for a saved workspace; the
recovery session dir for scratch — reuse the existing variable/branch, do not duplicate the logic):
```csharp
RunResultsWriter.WriteResults(
    baseDir,
    RunResultsWriter.SchematicKey(activeDoc.FilePath, activeDoc.Id),
    RunResultsWriter.OwnerIdentity(activeDoc.FilePath, activeDoc.Id),
    result.Results,
    _messages /* the existing IMessageSink */);
```
where `activeDoc` is the `SchematicDocument` being run. Use the message sink already used by `RunAnalysis`.
Do not change any other behavior of `RunAnalysis`.

## Tests
### A. `Ui.Tests/RunResultsWriterTests.cs` (new) — pure logic, temp dirs, fake sink
- `SchematicKey`: cell-homed sole view (`…/Amp/schematic/Amp.csch` → `"Amp"`); multi-view
  (`…/Amp/schematic/tb2.csch` → `"Amp.tb2"`); loose (`…/foo/bar.csch` → `"bar"`); scratch
  (`null, "Untitled-Schematic-1"` → `"Untitled-Schematic-1"`).
- `WriteResults` happy path: 1–2 tiny synthetic `DataSet`s (a couple of small `DataCube`s) → asserts
  `results/<key>/<name>.npy` exist, `.source` contains the owner, `Success` posted.
- Re-run by the same owner: removing one analysis from the set clears its stale `.npy`; remaining file
  rewritten.
- **Collision:** pre-create `results/<key>/.source` with a *different* owner → `WriteResults` posts a
  `Warning` (capture via a fake `IMessageSink`), writes nothing, leaves the existing dir untouched.
- Use a fake `IMessageSink` that records `(level, text, path)`.

### B. `Engine.Tests/Export/` — round-trip gate for every analysis type
First **check `NpyRoundTripTests.cs` / `DataSetExportTests.cs`** for existing coverage; **add only the
missing cases** (don't duplicate). The gate: for each of S-param, HB, parametric sweep, and loadpull,
produce the DataSet from the engine (reuse existing hero fixtures — Hero1 S-param, Hero2 HB + sweep,
Hero3 loadpull), `DataSetExporter.Export(ds, tmp, ExportFormat.Npy)`, `DataSetImporter.Import(tmp)`, and
assert equivalence: same cube names, same axis names/lengths/values, same `DataKind`, values within a
tight tolerance. This proves the on-disk artifact the writer produces rehydrates correctly per analysis.

## Scope guardrails (do NOT do these in 7.0)
- No Data Display UI, plot/trace model, or in-memory plotting handoff (7.1+).
- Do not modify the engines, `RfCore` export/import, or the `DataSet`/`DataCube` API.
- Do not thread `ILinearNetworkPayload` / `IncludeLinearNetwork` (Level-2, later).
- No format versioning/migration shims (alpha "break freely"; the exporter already writes
  `format_version`).
- Never write results inside a cell folder — `results/` is workspace-external.
- Keep `RunResultsWriter` free of Avalonia so `Firewall.Tests` stays green.
- Keep the `RunResult` change minimal (add `Results`, keep `DataSets` convenience).

## Gate (acceptance)
1. Solution builds green with `TreatWarningsAsErrors=true`.
2. Running a saved testbench produces `<workspaceRoot>/results/<schematicKey>/<analysisName>.npy` per
   analysis; the Messages "Results written" entry reveals the folder.
3. `RunResultsWriter` unit tests pass (key derivation, write, stale-clear, collision-warn).
4. Round-trip tests pass for S-param, HB, parametric sweep, and loadpull DataSets.
5. `Firewall.Tests` and the existing `SchematicRunServiceTests` pass (update the latter for the
   `RunResult.Results` shape if needed).

## On completion
Add a short "Phase 7.0 deliverable — COMPLETE" note to `src/Ui/CLAUDE.md` (follow the existing
deliverable-log convention in the other `CLAUDE.md` files): the per-run `.npy` writer, the naming rule,
and the test counts. Report results back for verification before 7.1 is briefed.
