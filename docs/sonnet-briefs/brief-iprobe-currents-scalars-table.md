# Sonnet Brief — scalars on Table + `<invalid>` guard (Data Display)

Goal (decision (a)): a **scalar** (rank-0) cube — a DC operating-point value, an IProbe DC current, a
scalar measurement like `PDC` — is **plottable only on a Table**, where it renders as a single value
cell. On Rect/Smith/Polar a scalar produces no geometry and shows a soft **`<invalid>`** suffix on its
label. Rank-1 (and higher) cubes are unchanged — they already plot everywhere via the existing path
(this is decision (b): a rank-1 measurement plots on Rect/Smith with no new work).

Scope: Data Display only (no engine). Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.
Grouped-combo presentation is a **separate** brief — do not touch picker labels/structure here.

Read first (verified seams): `Models/Trace.cs` (`SetCubeData`, `BuildCubePath`, `CubeShorthand`,
`RectYLabel`, `BuildPickerExpression`, `FormatCubeCell`), `ViewModels/PlotInspectorViewModel.cs`
(`TrySetCubeData`, `FirstPlottableCubeName`, `HasPlottableData`, `BuildSeedCubeTrace`),
`ViewModels/TraceRowViewModel.cs` (`RebuildSignals` + the signal-selection handler),
`Renderers/TableRenderer.cs` (`BuildColumns`, `FormatColumnCell`).

## Why scalars are invisible today (root cause)
The picker (`RebuildSignals`), `FirstPlottableCubeName`, and `HasPlottableData` all skip
`cube.Rank <= 0`. And `TrySetCubeData`'s single-slice path builds `args` for `cube.Rank` axes; for a
rank-0 cube `args` is empty, `cube[args]` returns a bare element, `!result.IsCube` ⇒ `Points.Clear()` ⇒
nothing. So a no-sweep DC run (all scalars except `V`) shows only `V`.

## A core principle that recurs below
**A rank-0 cube has no axes.** Any code that builds a "default slice" must NOT index `cube.Axes[0]` for
rank 0 — it must use an **empty slice** (`Array.Empty<AxisSlice>()`) and set `Expression` to the bare
(qualified) cube name. The scalar snippet is reused at three sites (Part C).

---

## Part A — Trace model (`Models/Trace.cs`)

### A1. Scalar state + invalid flag
Near the other cube fields (`_cubeXValues` etc.) add:
```csharp
private bool _cubeIsScalar;
public  bool CubeIsScalar => _cubeIsScalar;

/// <summary>True when a scalar (rank-0) cube is bound while the plot type is not Table. Scalars render
/// only on a Table; elsewhere the trace draws nothing and its label shows a soft "&lt;invalid&gt;".</summary>
public bool ScalarOnNonTableInvalid { get; private set; }
```
In the copy constructor add `_cubeIsScalar = src._cubeIsScalar;` (alongside the other `_cube*` copies).

### A2. Clear the flag on the normal paths
At the top of `SetCubeData(...)` add `_cubeIsScalar = false;`. In `SetFamilyData(...)` add
`_cubeIsScalar = false;` next to its existing `RectValueInvalid = false;`.

### A3. Scalar binding entry point
```csharp
/// <summary>Binds a scalar (rank-0) cube value. Renders as one Table cell; on any non-Table plot type
/// the trace produces no geometry and flags ScalarOnNonTableInvalid for a soft label.</summary>
public void SetScalarCubeData(Complex? complexValue, double? realValue,
                              PlotType plotType, FreqUnit freqUnit)
{
    _cubeIsScalar      = true;
    _cubeXValues       = new[] { 0.0 };                                  // synthetic 1-row anchor
    _cubeComplexValues = complexValue is Complex c ? new[] { c } : null;
    _cubeRealValues    = realValue   is double  r ? new[] { r } : null;
    _cubeXAxisName     = "";
    _cubeXUnit         = null;
    BuildCubePath(plotType, freqUnit);
}
```

### A4. `BuildCubePath` — short-circuit scalars
Immediately after the existing `RectValueInvalid = false;` line (before the `_cubeXValues is null`
guard) insert:
```csharp
ScalarOnNonTableInvalid = false;
if (_cubeIsScalar)
{
    // Scalars render only on a Table (which reads CubeXValues/FormatCubeCell, not Points).
    // Rect/Smith/Polar have nothing meaningful to draw → no points + soft <invalid> label.
    ScalarOnNonTableInvalid = plotType != PlotType.Table;
    return;   // Points already cleared at top of BuildCubePath.
}
```

### A5. Bare-name shorthand for rank-0 (no `[]`)
In `BuildPickerExpression()`, right after the `if (CubeName is null || Slice is null) return ShortDescription;`
guard, add:
```csharp
if (Slice.Length == 0)   // scalar (rank-0) cube — no axes to slice
    return Transform == CubeTransform.None
        ? CubeName
        : $"{TransformFunctionName(Transform)}({CubeName})";
```
(So a scalar's `Expression`/label is `measurements.PDC`, not `measurements.PDC[]`.)

### A6. Surface `<invalid>` on labels
Rework `CubeShorthand` to a base-label + suffix form:
```csharp
public string CubeShorthand
{
    get
    {
        string baseLabel;
        if (InvalidSpecText is not null)        baseLabel = $"{InvalidSpecText} <invalid>";
        else if (Expression is not null)        baseLabel = Expression;
        else if (!IsCubeBound || Slice is null) baseLabel = ShortDescription;
        else                                    baseLabel = BuildPickerExpression();
        if (ScalarOnNonTableInvalid && !baseLabel.Contains("<invalid")) baseLabel += " <invalid>";
        return baseLabel;
    }
}
```
In `DescriptionFor(bool includePrefix)`, the cube-bound branch becomes:
```csharp
if (IsCubeBound)
{
    var lbl = $"{prefix}{Expression ?? CubeName ?? ""}";
    if (ScalarOnNonTableInvalid) lbl += " <invalid>";
    return lbl;
}
```
(`RectYLabel` already appends `<invalid>` via `RectValueInvalid` and guards on `Contains("<invalid")`,
so it won't double-add — it uses `CubeShorthand` as its base, which now carries the scalar suffix.)

---

## Part B — `TrySetCubeData` rank-0 branch (`PlotInspectorViewModel.cs`)
In the **single-slice path**, immediately after:
```csharp
var cube  = ds[t.CubeName];
var slice = t.Slice;
if (slice is null) { t.Points.Clear(); return; }
```
insert:
```csharp
// Scalar cube (rank 0): operating-point value — valid only on a Table (Part A).
if (cube.Rank == 0)
{
    var sr = cube[Array.Empty<object>()];
    t.InvalidSpecText = null;
    t.ExpressionError = null;
    t.SetScalarCubeData(
        sr.IsComplex ? sr.ComplexValue : (Complex?)null,
        sr.IsReal    ? sr.RealValue    : (double?)null,
        plotType, freqUnit);
    return;
}
```
(Confirm `SliceResult` member names against `DataCube.cs`: `IsComplex`/`ComplexValue` (Complex?),
`IsReal`/`RealValue` (double?). Adjust if they differ.)

---

## Part C — surface scalars on Table only (picker + seeding)

### C1. `FirstPlottableCubeName` — Table-aware
Add a parameter and relax the rank gate:
```csharp
private static string? FirstPlottableCubeName(DataSourceEntryViewModel e, bool allowScalars = false)
{
    ...
    if (bareName == "I" or "INl" && cube.Axes.Any(a => a.Name == "node")) continue;  // unchanged
    if (cube.Rank == 0 && !allowScalars) continue;   // was: if (cube.Rank <= 0) continue;
    return group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}";
    ...
}
```

### C2. `HasPlottableData` / `CanAddTrace`
Thread the flag from the plot type:
```csharp
private static bool HasPlottableData(DataSourceEntryViewModel e, bool allowScalars) =>
    (e.Snp is not null && !e.Snp.IsEmpty) || FirstPlottableCubeName(e, allowScalars) is not null;
```
In `CanAddTrace`:
```csharp
(_library?.Entries.Any(e => HasPlottableData(e, _plot.PlotType == PlotType.Table)) ?? false);
```
In `AddTrace`, the cube-only branch:
```csharp
else if (_library?.Entries.FirstOrDefault(e =>
             FirstPlottableCubeName(e, _plot.PlotType == PlotType.Table) is not null) is { } firstCube)
```

### C3. `BuildSeedCubeTrace` — rank-0 seed
Pass the flag through to find the name, and handle rank 0 before the axis-0 default slice:
```csharp
string cubeName = FirstPlottableCubeName(entry, _plot.PlotType == PlotType.Table)!;
var    cube     = entry.Data![cubeName];
int    rank     = cube.Rank;

if (rank == 0)   // scalar: empty slice, bare-name Expression
{
    var st = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
    st.SourcePath = entry.FilePath;
    st.CubeName   = cubeName;
    st.Slice      = Array.Empty<AxisSlice>();
    st.Expression = st.BuildPickerExpression();      // → bare CubeName (Part A5)
    return st;
}
```

### C4. `RebuildSignals` (`TraceRowViewModel.cs`) — two edits
The row holds the parent inspector (the reference Brief 2 used for the "Show all branches" predicate);
it exposes `IsTablePlot`.
- **Skip rule:** replace `if (cube.Rank <= 0) continue;` with
  ```csharp
  if (cube.Rank == 0 && !_parent.IsTablePlot) continue;   // scalars are Table-only
  ```
  (keep all other skip rules — S/Z0/`__`/Converged/Residual/node-indexed-current — and the Brief-2
  probe filter, exactly as-is.)
- **Default-slice construction:** where a rank-1+ cube gets its default slice (`KeepAsX` on axis 0,
  the rest pinned — the code that reads `cube.Axes[...]`), add a rank-0 guard FIRST so it never indexes
  `Axes[0]`:
  ```csharp
  AxisSlice[] defaultSlice = cube.Rank == 0
      ? Array.Empty<AxisSlice>()
      : /* existing default-slice builder */;
  ```
  Emit the `TraceDataItem` with the qualified `CubeName` (`group == DefaultGroup ? bareName :
  $"{group}.{bareName}"`) and this slice, as today.

### C5. Signal-selection handler (`TraceRowViewModel`)
Wherever selecting a signal sets up the trace's slice (the handler that sets `_trace.CubeName =
value.CubeName` and builds a default slice — `OnSelectedSignalChanged` or equivalent): apply the same
rank-0 guard — for a rank-0 cube set `_trace.Slice = Array.Empty<AxisSlice>()` and
`_trace.Expression = _trace.BuildPickerExpression()` (bare name); do not index `Axes[0]`. Then the
existing `TrySetCubeData` call binds it via Part B.

---

## Part D — Table rendering of a scalar (`Renderers/TableRenderer.cs`)
A scalar trace has `CubeXValues = [0]`, so `BuildColumns` already yields one XAxis column (header "",
since `CubeXAxisName` is "") + the trace's value column, and `FormatColumnCell` → `FormatCubeCellAt`
→ `FormatCubeCell(0)` renders the value. Two refinements so the synthetic X reads cleanly:

Add a flag to the column plan:
```csharp
public sealed class TableColumn { /* ... */ public bool IsScalar; }
```
In `BuildColumns`, when determining a trace's X identity, detect a scalar trace and mark its XAxis
column. In the `trace.IsCubeBound` X-identity branch:
```csharp
if (trace.IsCubeBound && trace.CubeIsScalar)
{
    axisName = ""; unit = null; raw = new[] { 0.0 };   // single-row anchor, blank header
}
else if (trace.IsCubeBound && trace.CubeXValues is { } xs) { /* existing */ }
```
and when the `!dedup` block creates the XAxis `TableColumn`, set `IsScalar = trace.CubeIsScalar`.
(Adjacent scalars share one column via the existing dedup — same name "", unit null, values `[0]`.)

In `FormatColumnCell`, blank the scalar X cell:
```csharp
if (col.Kind == TableColKind.XAxis)
{
    if (col.IsScalar) return "";        // scalar anchor column: no X value
    /* existing XAxis formatting */
}
```
Result: a scalar shows as a value cell under its quantity-name header (e.g. `PDC`, `DC1.I:Ids`), with a
narrow blank anchor column; an all-scalar DC table reads as a one-row operating-point strip. Mixing a
rank-1 trace (its own X axis) with scalars works — two X columns, longest drives the row count.

---

## Tests
`tests/Ui.Tests` (headless):
1. **Scalar_OnTable_RendersValueCell:** a DC-style source (`PDC` rank-0 real, `I:Ids` rank-0) on a
   `PlotType.Table` → `BuildColumns` produces value columns; `FormatColumnCell` returns the formatted
   numbers (not "" / "NaN"); the scalar XAxis column is `IsScalar` and formats to "".
2. **Scalar_PickerVisibleOnlyOnTable:** `RebuildSignals` includes a rank-0 cube when the parent is a
   Table, excludes it on Rect/Smith/Polar.
3. **Scalar_OnRect_IsInvalid:** bind a scalar then build on `PlotType.Rect` → `Points` empty,
   `ScalarOnNonTableInvalid` true, `CubeShorthand` and `Description` end with `<invalid>`.
4. **Scalar_AddTrace_OnTableOnly:** with a scalar-only source, `CanAddTrace` is true on a Table and
   `AddTrace` seeds a scalar trace (empty slice, `Expression` == bare name); on a Rect, the scalar-only
   source does not enable Add.
5. **Rank1_Unchanged:** a rank-1 cube still plots on Rect/Smith and tables as before (regression).
6. **NoAxisIndex_OnRank0:** binding/seeding a rank-0 cube never throws (no `Axes[0]` access).

## Gate (manual)
No-sweep DC run with `PDC = DC1.I("Ids")*DC1.V("Vds")` and an IProbe → Data Display, **Table**: `PDC`
and `I:Ids` appear as value cells alongside `V` columns. Switch the plot to **Rect**: the scalar
trace’s label shows a soft `<invalid>` and it draws nothing; `V` still plots. Switch back to Table:
values return.

## On completion
Note in `src/Ui/DataDisplay/CLAUDE.md`: scalars (rank-0 cubes) are surfaced in the picker and rendered
as value cells **only** on a Table; on other plot types they draw nothing and show a soft `<invalid>`
label (`Trace.ScalarOnNonTableInvalid`). Rank-1 cubes are unchanged (decision (b) needs no display work).
