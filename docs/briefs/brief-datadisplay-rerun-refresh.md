# Sonnet Brief — Data display: auto re-render when a re-run changes a trace's cube

Symptom (the owner): plot a cube-bound trace (e.g. `I:Ids`), then re-run with different sweep dimensions or a
different nested order. The plot does not re-render to match the new data without extra user input.

## How the reactivity is supposed to work (confirmed by reading the code)
1. A run writes one `.npy` per result to a **stable** path: `results/<schematicKey>/<analysisName>.npy`
   (`RunResultsWriter.WriteResults` clears stale `.npy` and rewrites; returns the written absolute paths).
   For a sweep chain the file is named after the **base** analysis (e.g. `DC1.npy`) and contains the cubes
   (`I:Ids`, `V`, …) with their new axes.
2. The open data display must reload those paths in place:
   `DataSourceLibraryViewModel.ReloadChangedAsync(writtenPaths)` → `entry.RefreshNpy(...)` →
   fires `LibraryChanged`.
3. Each plot's `PlotInspectorViewModel.OnLibraryChanged` then rebuilds every cube-bound trace via
   `TrySetCubeData` and refreshes the axis-role rows, and fires `PlotNeedsRedraw` / `PlotStructureChanged`.

So a same-structure re-run *should* already redraw. Two things can break it; confirm which (Step 0) before fixing.

## Step 0 — instrument to find the actual break (do this first)
Add temporary `Console.Error.WriteLine` (or `IMessageSink.Info`) probes and run a sweep, plot `I:Ids`, then
re-run with (a) a changed point count and (b) an added sweep axis:
- In the run command (WorkspaceViewModel — the path that calls `RunResultsWriter.WriteResults`): log the
  returned written paths and whether `ReloadChangedAsync` is called with them.
- In `DataSourceLibraryViewModel.ReloadChangedAsync`: log each path it matches/reloads.
- In `PlotInspectorViewModel.OnLibraryChanged`: log entry count + that it ran.

This tells you whether the break is **(A) the reload never fires** (no path match / call missing) or
**(B) the reload fires but the trace keeps its stale slice** so the new dimensions don't show. Fix only what
Step 0 implicates; remove the probes after.

## Fix A — ensure a run reloads the displayed results
If Step 0 shows `LibraryChanged` does NOT fire after a run for the displayed `.npy`:
- In the run command, after `RunResultsWriter.WriteResults(...)` returns `written`, the open data display's
  library must be told: `await library.ReloadChangedAsync(written);` (await, so the in-place `RefreshNpy` +
  `LibraryChanged` happen before the command returns). Wire it for the data display(s) bound to the schematic
  that ran. If the call exists but passes the wrong paths (e.g. relative vs `Path.GetFullPath`), normalize to
  match what `WriteResults` returned (it returns `Path.GetFullPath`-ed paths; `ReloadChangedAsync` also
  `GetFullPath`-normalizes, so a plain pass-through of `written` is correct).
- Path identity is the linchpin: the trace's `SourcePath` must equal the rewritten file path. Because the file
  name is the analysis name and is stable across runs, this holds — but verify the displayed trace's
  `SourcePath` matches a path in `written` during Step 0.

## Fix B — adopt the new cube shape when structure changes
If Step 0 shows the reload fires but the plot doesn't reflect new dimensions: on reload, the trace's stored
`Slice` is read as-is by `TrySetCubeData` and is stale for the new shape (a new sweep axis isn't in the slice
so it's silently pinned at index 0; the user never sees the new dimension). Re-seed the slice when the cube's
axis-name set changes.

In `PlotInspectorViewModel.OnLibraryChanged`, in the loop that rebuilds remaining traces, replace the
cube-bound branch:
```csharp
        foreach (var t in _plot.Traces)
        {
            if (t.IsCubeBound)
                TrySetCubeData(t, _library, _plot.PlotType, _plot.FreqUnits);
            else
                t.BuildPath(_plot.PlotType, _plot.FreqUnits);
        }
```
with a structure-aware re-seed for single-cube traces (leave multi-cube `Expression` traces alone):
```csharp
        foreach (var t in _plot.Traces)
        {
            if (t.IsCubeBound)
            {
                ReseedSliceIfCubeShapeChanged(t, _library);   // new helper below
                TrySetCubeData(t, _library, _plot.PlotType, _plot.FreqUnits);
            }
            else
                t.BuildPath(_plot.PlotType, _plot.FreqUnits);
        }
```
Add the helper to `PlotInspectorViewModel` (reuses the existing name-matching slice builder, which preserves
roles for axes that still exist, defaults new axes to PinToIndex/0, drops vanished axes, and guarantees one X):
```csharp
    /// <summary>
    /// When a re-run changes a single-cube trace's bound cube to a different axis-name set
    /// (added/removed sweep axis), rebuild Trace.Slice for the new shape so the plot adopts the
    /// new dimensions. Axes that still exist keep their role + pin (clamped); new axes default to
    /// pinned-at-0; exactly one axis remains X. Same-shape re-runs are left untouched so the user's
    /// role choices and pins survive a value/point-count change.
    /// </summary>
    private static void ReseedSliceIfCubeShapeChanged(Trace t, DataSourceLibraryViewModel? library)
    {
        if (t.Expression is not null && (t.CubeName is null || t.Slice is null)) return; // multi-cube expr
        if (library is null || t.SourcePath is null || t.CubeName is null || t.Slice is null) return;

        var entry = library.Entries.FirstOrDefault(e =>
            string.Equals(e.FilePath, t.SourcePath, StringComparison.OrdinalIgnoreCase));
        var ds = entry?.Data;
        if (ds is null || !ds.Contains(t.CubeName)) return;

        var cube = ds[t.CubeName];
        var cubeAxes  = cube.Axes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var sliceAxes = t.Slice.Select(s => s.AxisName).ToHashSet(StringComparer.Ordinal);
        if (cubeAxes.SetEquals(sliceAxes)) return;   // same structure → preserve user's slice exactly

        t.Slice = TraceRowViewModel.BuildCarriedSliceFromCube(cube, t.Slice);
        t.Expression = t.BuildPickerExpression();    // keep the spec text in sync with the reshaped slice
    }
```
`OnLibraryChanged` already calls `vm.RefreshDataSources()` afterward, which rebuilds the axis-role rows from the
new cube + the (now refreshed) slice, so the trace card's pin/X/Fam editor shows the new axes too. New axes
arrive pinned (same as a fresh plot's default); the user promotes one to X/Family if desired. Note for the owner:
re-render is automatic; assigning a *brand-new* axis to X/Family is still a deliberate click, matching how a
fresh plot of the same cube would seed.

## Tests
- **PlotInspectorViewModel reseed (headless):** build a library entry whose cube is `[Vgs(41), node]`, a
  cube-bound trace with slice `{Vgs:X, node:pin0}`; replace the entry's DataSet in place with `[Vgs(81), node]`
  (same axes, more points) and fire `LibraryChanged` → slice unchanged (still `{Vgs:X, node:pin}`), trace
  rebuilt (point count 81). Then replace with `[Vgs, Vds, node]` (new axis) → slice now contains a `Vds` entry
  (pinned), still exactly one X, and `TrySetCubeData` yields a 1-D result without clearing Points.
- **ReloadChangedAsync (if Fix A applies):** after a simulated re-write of the backing `.npy`, the entry's Data
  is the new cube and `LibraryChanged` fired once.

## Gate (manual)
Plot `I:Ids` from a single-Vgs-sweep DC. Re-run after (1) changing Vgs point count → the curve redraws with the
new sampling immediately; (2) adding a Vds sweep → the plot redraws and a `Vds` row appears in the trace card's
axis-role editor (pinned); promote it to Fam/X and the family/axis appears. No manual reload needed.

## On completion
Note in the nearest CLAUDE.md which fix applied: data-display traces re-render automatically after a re-run
(run → `ReloadChangedAsync` → `LibraryChanged` → inspector rebuild), and a cube-bound trace re-seeds its slice
when the bound cube's axis set changes (new axes default to pinned; same-shape re-runs preserve the user's
roles/pins).
