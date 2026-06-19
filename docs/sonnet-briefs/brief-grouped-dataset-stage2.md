# Brief — Grouped DataSet, stage 2 (run pipeline → one grouped file)

Design: `docs/design/results-dataset-layout.md`. **Stage 2 of 4.** Stage 1 (grouped `DataSet` + NPY
round-trip) is landed and green. This stage makes a run produce **one** grouped DataSet (a group per
analysis + a `measurements` group) and write **one** `.npy` per run, and **reverts** the per-analysis
measurement-attachment machinery (no longer needed — the whole run is one dataset). UI layer only;
RfCore is untouched. Alpha: no back-compat — rewrite the affected tests to the new shape.

Scope: `src/Ui/Schematic/SchematicRunService.cs`, `src/Ui/Schematic/RunResultsWriter.cs`,
`src/Ui/ViewModels/WorkspaceViewModel.cs` (one call site), `src/Engine/MeasurementEvaluator.cs` +
`src/Core/Expressions/MeasurementContext.cs` (revert), plus tests. No display/addressing changes — those
are stage 3 (see the inter-stage note at the end).

## 1. Revert the measurement per-analysis attachment

These were added in anticipation of per-analysis files; with one dataset per run they're unnecessary.

**`MeasurementContext.cs`** — remove `_accessed`, `AccessedAnalyses`, `ResetAccessLog`. Restore the
simple accessor:
```csharp
public DataSet GetAnalysis(string name)
    => _results.TryGetValue(name, out var ds) ? ds
       : throw new KeyNotFoundException(
           $"No analysis named '{name}' in measurement context. Available: [{string.Join(", ", _results.Keys)}]");
```

**`MeasurementEvaluator.cs`** — delete `EvaluateIntoReferencedAnalyses`. Keep the private core + `ToCube`,
but drop the access-log: change the core to `private void Evaluate(Action<Measurement, Value> emit)`
(remove the `ctx.ResetAccessLog()` call and the `IReadOnlyCollection<string>` arg), and make `EvaluateInto`
the only public method:
```csharp
public void EvaluateInto(DataSet ds)
    => Evaluate((m, result) => ds.Add(m.Name, ToCube(m, result)));
```
`Hero2MeasurementTests` (calls `EvaluateInto(ds)`) stays green unchanged.

## 2. SchematicRunService — assemble one grouped DataSet

`RunResult` keeps `Results` (per-analysis, named — drives the dispatch/naming tests) and `DataSets`.
**Add** a grouped output:
- New property `public DataSet? GroupedResults { get; }` and a matching optional ctor param
  (defaulted `null`), set from `RunNetlist`.

Replace the current "4b. Measurements" block (which calls `EvaluateIntoReferencedAnalyses`) with grouped
assembly, placed after both dispatch loops have populated `results`:
```csharp
// ── 4b. Assemble the one grouped run DataSet (group per analysis + measurements) ──
DataSet? grouped = null;
if (results.Count > 0)
{
    grouped = new DataSet();
    foreach (var r in results)                       // r.Data is a flat (default-group) engine result
        foreach (var kv in r.Data.Cubes)
            grouped.AddToGroup(r.Name, kv.Key, kv.Value);

    if (tb.Measurements.Count > 0)
    {
        try
        {
            var analysisResults = new Dictionary<string, DataSet>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in results) analysisResults[r.Name] = r.Data;   // measurements read the flat per-analysis sets

            var measDs = new DataSet();
            new MeasurementEvaluator(tb, nl, analysisResults).EvaluateInto(measDs);
            foreach (var kv in measDs.Cubes)
                grouped.AddToGroup("measurements", kv.Key, kv.Value);
        }
        catch (Exception ex) { errors.Add($"measurements: {ex.Message}"); }
    }
}
```
Notes: `r.Data.Cubes` returns the default group (engine results are single-group) — correct source for
the per-analysis cubes. Measurements still resolve `HB1.V(...)` etc. against the flat per-analysis
`analysisResults` map (unchanged contract), so a measurement referencing an unknown analysis still throws
→ caught → run note. Pass `grouped` into the returned `RunResult` on the Success path (and only there).

## 3. RunResultsWriter — write one file per run

Replace `WriteResults(IReadOnlyList<AnalysisResult> …)` with a single-DataSet writer (keep
`SchematicKey`/`OwnerIdentity`/`Sanitize` unchanged):
```csharp
public static IReadOnlyList<string> WriteRun(
    string baseDir, string schematicKey, string ownerIdentity,
    DataSet? grouped, IMessageSink? messages)
```
- Return `[]` when `grouped` is null or `grouped.Groups.Count == 0`.
- Same dir + `.source` collision mechanism as today: `dir = results/<schematicKey>/`; if `.source` exists
  with a different owner → warn, return `[]`; else create dir, write `.source`, **delete stale `*.npy`**,
  then write exactly one file `Path.Combine(dir, "run.npy")` via `DataSetExporter.Export(grouped, …, Npy)`.
- Success message e.g. `$"Results written: {schematicKey} ({grouped.Groups.Count} group(s))"`. Return
  `[ Path.GetFullPath(runNpy) ]`. Keep the catch-all → warning, return `[]`.

(Open-question #1 resolved: keep the per-key directory + `.source`; one file `run.npy` inside it. Stale-
clear naturally removes old per-analysis `*.npy` from before this change — no migration.)

## 4. WorkspaceViewModel — one call-site change

In `RunSchematicDocAsync`, Success branch (currently ~line 1079), replace the `WriteResults(... result.Results ...)`
call with:
```csharp
var written = RunResultsWriter.WriteRun(
    baseDir,
    RunResultsWriter.SchematicKey(activeDoc.FilePath, activeDoc.Id),
    RunResultsWriter.OwnerIdentity(activeDoc.FilePath, activeDoc.Id),
    result.GroupedResults,
    Messages);
await RefreshOpenDataDisplaysAsync(written);
```
`written` is now a single path; `RefreshOpenDataDisplaysAsync` → `ReloadChangedAsync` works unchanged.
Leave `_lastRunDataSets = result.DataSets;` as is.

## 5. Tests

- **`RunResultsWriterTests`** — rewrite to the one-file model. Build a grouped DataSet via `AddToGroup`
  (two analysis groups, e.g. `SP1`/`HB1`, plus a `measurements` group). Assert: `results/<key>/run.npy`
  exists; `.source` == owner; one success message; a pre-seeded stale `*.npy` in the dir is deleted on
  write; different-owner → warning + nothing written; same-owner proceeds; empty/zero-group → `[]` and no
  `results/` dir; returns the single `run.npy` path. Keep the `SchematicKey_*` tests unchanged.
- **`SchematicRunServiceTests`** — S1–S4 and the rest assert on `result.Results` and stay green
  unchanged. **Add** `MeasurementsAndAnalyses_AssembleIntoGroupedResults`: an inline 2-port netlist with
  an S-param analysis and a `measure Gain = dB(SP1.S(2,1))` directive; run; assert `result.GroupedResults`
  is non-null, `Groups` contains `"SP1"` and `"measurements"`, `grouped["SP1.S"]` resolves, and
  `grouped["measurements.Gain"]` resolves to a Real cube.
- **`Hero2MeasurementTests`** — unchanged (uses `EvaluateInto(ds)`).

## Gate
`dotnet test` green (Engine.Tests + Ui.Tests, especially Measurements/Export/RunResultsWriter/
SchematicRunService); solution builds clean under `TreatWarningsAsErrors`.

## Inter-stage note (sequencing)
After stage 2, a run writes a grouped `run.npy`, but the Data Display still addresses cubes by bare name /
default group, so a loaded `run.npy` shows **no cubes** until **stage 3** (source tree file→analysis→cube
and `Analysis.Cube` resolution in `CubeTraceSpecParser`/`TraceExpression`). Loading is crash-safe (the SNP
bridge's `Contains("S")` simply returns false on a grouped set). Do stage 3 next before relying on the
display for grouped results.
