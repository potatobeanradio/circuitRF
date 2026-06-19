# Brief — Grouped DataSet, stage 3 (display addressing)

Design: `docs/design/results-dataset-layout.md`. **Stage 3 of 4.** Stages 1–2 landed: a run writes one
grouped `run.npy` (group per analysis + a `measurements` group). This stage makes the Data Display
address cubes as `Analysis.Cube` so grouped results are pickable and plottable. UI layer only.

**Why it's small:** the `Trace` model carries `CubeName` as an opaque string, and all resolution goes
through `ds.Contains(name)` / `ds[name]`, which the stage-1 grouped indexer already resolves for qualified
`Group.Cube` specs. So `Trace`, `TrySetCubeData`, the family/slice resolver, the axis-role editor,
`BuildCarriedSliceFromCube`, `ReseedSliceIfCubeShapeChanged`, and typed-spec `CommitSpec` all work
unchanged once the **enumeration** points emit qualified names. The per-trace signal picker is a flat
ComboBox (not a tree), so qualified labels in the combo are the whole UI change.

**Decision (from the user): no SNP bridge for results (option a).** S-parameter analysis results are
addressed as cubes (`SP1.S[:, i, j]`); the SNP matrix UI (Smith / stability circles / Z0 renorm) stays
Touchstone-only. Do **not** try to expose run-result S as an `SNP`. (Future: stability circles for
simulated S come from a measurement expression over `SP1.S`, with the trace referencing that measurement —
out of scope here.)

Scope: `src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`,
`src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs`, `src/Ui/DataDisplay/TraceExpression.cs`,
`src/Ui/DataDisplay/CubeTraceSpecParser.cs`, plus tests. `DataSet.DefaultGroup` is the public const `""`.

## 1. TraceRowViewModel — qualified signal enumeration + group-relative `__LabeledNodes`

**`RebuildSignals`, cube-bound section** (the `foreach (var (cubeName, cube) in ds.Cubes)` loop): iterate
groups instead of the default-group `Cubes`. Apply the existing skip rules to the **bare** cube name, but
emit the **qualified** name as the item's `CubeName` and in its label:
```csharp
foreach (var group in ds.Groups)
{
    foreach (var (bareName, cube) in ds.CubesIn(group))
    {
        if (bareName is "S" or "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
        if (bareName.EndsWith("Converged", StringComparison.Ordinal) ||
            bareName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
        bool isNodeIndexedCurrent =
            (bareName == "I" || bareName == "INl") && cube.Axes.Any(a => a.Name == "node");
        if (isNodeIndexedCurrent) continue;
        int rank = cube.Rank;
        if (rank <= 0) continue;

        bool isEnabled = !isComplexPlot || cube.DataKind == DataKind.Complex;
        string qualified = group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}";

        var defaultSlice = new AxisSlice[rank];
        defaultSlice[0] = new AxisSlice(cube.Axes[0].Name, AxisRole.KeepAsX, 0);
        for (int d = 1; d < rank; d++)
        {
            var ax  = cube.Axes[d];
            string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
            defaultSlice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
        }
        AvailableSignals.Add(new TraceDataItem(entry, qualified, defaultSlice,
                                               $"{filePrefix}{qualified}", isEnabled));
    }
}
```
For a flat (Touchstone) source `ds.Groups` is `[""]` and `qualified == bareName`, so its signal list is
byte-for-byte what it is today — no behavior change for `.sNp` sources. `TraceDataItem.CubeName` now holds
the qualified name; `OnSelectedSignalChanged` already sets `_trace.CubeName = value.CubeName`, and
`BuildPickerExpression` emits `HB1.V[...]` automatically.

**`RebuildAxisRolesCore`, `__LabeledNodes` lookup** — it currently does `ds.Contains("__LabeledNodes")`,
which (bare) fails on a multi-group set. The sidecar lives in the trace cube's own group. Add a helper and
use it:
```csharp
private static string SiblingCubeName(string cubeName, string sibling)
{
    int dot = cubeName.IndexOf('.');
    return dot > 0 ? string.Concat(cubeName.AsSpan(0, dot), ".", sibling) : sibling;
}
```
Replace the two `__LabeledNodes` references with `SiblingCubeName(_trace.CubeName, "__LabeledNodes")` (the
`Contains` guard and the `ds[…]` fetch). Bare CubeName (flat source) → `"__LabeledNodes"` as before;
qualified `HB1.V` → `"HB1.__LabeledNodes"`.

## 2. PlotInspectorViewModel — qualified `FirstPlottableCubeName`

`FirstPlottableCubeName` iterates `ds.Cubes`; change it to iterate groups and return the qualified name
(same skip rules on the bare name):
```csharp
private static string? FirstPlottableCubeName(DataSourceEntryViewModel e)
{
    if (e.Data is not { } ds) return null;
    foreach (var group in ds.Groups)
        foreach (var (bareName, cube) in ds.CubesIn(group))
        {
            if (bareName is "S" or "Z0" || bareName.StartsWith("__", StringComparison.Ordinal)) continue;
            if (bareName.EndsWith("Converged", StringComparison.Ordinal) ||
                bareName.EndsWith("Residual",  StringComparison.Ordinal)) continue;
            if ((bareName == "I" || bareName == "INl") && cube.Axes.Any(a => a.Name == "node")) continue;
            if (cube.Rank <= 0) continue;
            return group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}";
        }
    return null;
}
```
`BuildSeedCubeTrace` needs no change beyond this: `entry.Data![cubeName]` with a qualified `cubeName`
resolves via the grouped indexer, and `trace.CubeName = cubeName` stores the qualified name. (`HasPlottableData`
calls `FirstPlottableCubeName`, so it follows automatically.)

## 3. TraceExpression — qualified candidate names

In `TryEvaluate`, the candidate-name scan reads `ds.Cubes.Keys`. Enumerate qualified names across groups:
```csharp
var cubeNames = ds.Groups
    .SelectMany(g => ds.CubesIn(g).Keys
        .Select(c => g == DataSet.DefaultGroup ? c : $"{g}.{c}"))
    .OrderByDescending(n => n.Length)
    .ToList();
```
The matcher (literal prefix + required `[`) and resolution (`ds.Contains(info.CubeName)` / `ds[info.CubeName]`)
work unchanged — qualified names match literally and resolve through the grouped indexer. Longest-first
ordering keeps `HB1.V` matching before any shorter candidate. Flat sources are unaffected (qualified == bare).

## 4. CubeTraceSpecParser — qualified "Available:" list (cosmetic)

The single-cube parser already resolves qualified names (it passes the whole `cubeName` to `ds.Contains`/`ds[…]`).
Only the error message lists `ds.Cubes.Keys` (default group). Change that one `names` expression to enumerate
qualified names so the spec-editor hint is accurate on grouped sets:
```csharp
var names = string.Join(", ", ds.Groups
    .SelectMany(g => ds.CubesIn(g).Keys.Select(c => g == DataSet.DefaultGroup ? c : $"{g}.{c}")));
```

## Tests

Flat-source tests (`CubeTraceTests`, `TraceExpressionTests`, `TableCubeTraceTests`, `DataDisplayVmSmokeTest`)
must stay green unchanged — for a default-group DataSet every qualified name equals the bare name. Add a
focused grouped case (headless; the parser/expression statics need no Avalonia):
- **CubeTraceSpecParser**: build a 2-group DataSet (`HB1` with `V` [freq,node] complex, `SP1` with `S`
  [freq,i,j]); `TryParse("HB1.V[:, 0]", ds, …)` → `cubeName == "HB1.V"`, one X axis; `TryParse("V[:, 0]", ds, …)`
  → fails clean (ambiguous/again qualify). 
- **TraceExpression**: `TryEvaluate("mag(HB1.V[:, 0])", ds, PlotType.Rect, …)` → real output, correct length;
  a cross-group expression with shape-matched slices resolves.
- **TraceRowViewModel** (mirror existing VM test setup with a library): a grouped source yields
  `AvailableSignals` whose cube items carry qualified `CubeName`s (e.g. contains `HB1.V`, `SP1.S`,
  `measurements.Gain`), and selecting one sets `Trace.CubeName` to the qualified value and resolves data.

## Gate
`dotnet test` (Ui.Tests data-display suites) green; solution builds clean under `TreatWarningsAsErrors`.
End-to-end check (manual, by the owner): run a multi-analysis schematic with a `measure`, open a Data Display,
import the run's `run.npy`, confirm the signal combo lists `HB1.*` / `SP1.*` / `measurements.*` and traces
plot. This completes the grouped-dataset feature (stage 4 = doc updates).
