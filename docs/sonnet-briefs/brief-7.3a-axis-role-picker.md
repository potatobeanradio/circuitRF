# Sonnet Brief — 7.3a: axis-role assignment picker (>2-D cubes, single curve)

**Context.** First of three 7.3 briefs (then **7.3b** family role, **7.3c** is folded into -b's cap). 7.2c gave
cube-bound traces a flat ≤2-D enumeration in `TraceRowViewModel.RebuildSignals` (rank-3+ skipped). 7.3a replaces
that for the selected cube with a real **axis-role editor**: each named axis gets a role — **X** (kept, exactly
one) or **pinned** (single index, value picker) — plus the existing transform. This unlocks rank ≥3 cubes (all
axes pinned except the X axis). **Family (one trace → N curves) is 7.3b** — not in this brief; here every trace
is a single curve.

## What exists (re-confirm before coding)
- `Trace` (`src/Ui/DataDisplay/Models/Trace.cs`): cube binding via `CubeName`, `Slice` (`AxisSlice[]`),
  `Transform` (`CubeTransform`), `IsCubeBound`; owner injects resolved arrays via `SetCubeData(...)`.
  `AxisSlice(string AxisName, AxisRole Role, int Index)`, `AxisRole { PinToIndex, KeepAsX }`.
- `DataCube` (`RfCore/src/Data/DataCube.cs`): `Axes` (each `Axis` has `Name`/`Values`/`Unit`/`Labels`/`Length`),
  `Rank`, `Axis(name)`, and `this[params object[]]` slicing — **int pins (collapses), `Range`/`..` keeps**,
  returns `SliceResult` (`.Cube`/`.ComplexValue`/`.RealValue`). End-exclusive ranges.
- `TraceRowViewModel.RebuildSignals()` builds `AvailableSignals` (flat list of `TraceDataItem`) and the owner
  resolves a selected item → `SetCubeData`. The cube branch currently enumerates rank≤2 only.
- The owner-side slice resolution (where a `TraceDataItem`'s `Slice` + `CubeName` become a sliced 1-D cube →
  `SetCubeData`) lives in `PlotInspectorViewModel`/`DataDisplayViewModel` (find the call site that builds the
  1-D arrays from `entry.Data[CubeName]` using `Slice`). 7.3a generalizes that resolver to N-D.

## Design — two-step picker (cube, then per-axis roles)
Replace the flat "one item per (cube × pinned-combo)" enumeration with a **cube selector** + an **axis-role
editor**, matching the §2.8 Analyses-Properties idiom (clean property rows, live redraw). Keep it inside the
trace card (the per-trace-kind body), not a modal.

1. **Cube selector** — a combo of the source's plottable cubes (skip `S`/`Z0` as today; rank ≥1). Selecting a
   cube (re)builds the axis-role rows for that cube. Persist as `Trace.CubeName`.
2. **Per-axis role rows** — one row per cube axis (in cube axis order), each with:
   - a **role toggle**: `X` or `Pinned` (segmented `.active` buttons per the idiom). Exactly one axis may be
     `X`; selecting `X` on another axis flips the previous X to `Pinned` automatically.
   - when **Pinned**: an **index/value picker** (combo) over that axis's values/labels
     (`Axis.Labels[k] ?? Axis.Values[k].ToString("G3")`), bound to the pin index.
   - The X axis row hides its value picker (it's kept).
   Build a small row VM, e.g. `AxisRoleRowViewModel { AxisName, Unit, IsX, PinIndex, PinOptions }`, exposed as
   an `ObservableCollection<AxisRoleRowViewModel> AxisRoles` on `TraceRowViewModel`, rebuilt when `CubeName`
   changes. Edits write back into `Trace.Slice` (rebuild the `AxisSlice[]` in axis order) and call
   `_parent.RebuildAndNotify()`.
3. **Transform** — unchanged (the existing `CubeTransform` combo).

**Validation / guards:**
- Exactly one `KeepAsX`. If a cube is rank-1, that axis is forced `X` (no toggle needed). If the user somehow
  clears all X (shouldn't be possible with the auto-flip), fall back to the first axis as X.
- Smith/Polar require a Complex cube (existing `isEnabled` gate) — keep it; disable those plot/transform combos
  for Real cubes as today.

## Owner-side slice resolution (generalize to N-D)
Where the owner turns `(CubeName, Slice)` into the 1-D arrays for `SetCubeData`, build the positional slice args
for `DataCube.this[]` from `Slice` (in **cube axis order**):
```csharp
var cube = entry.Data![trace.CubeName!];
var args = new object[cube.Rank];
int xDim = -1;
for (int d = 0; d < cube.Rank; d++)
{
    var s = trace.Slice!.First(a => a.AxisName == cube.Axes[d].Name);  // match by NAME, not position
    if (s.Role == AxisRole.KeepAsX) { args[d] = System.Range.All; xDim = d; }
    else                            { args[d] = s.Index; }             // int pins
}
var sliced = cube[args].Cube!;            // rank-1 cube (one surviving axis = X)
var xAxis  = sliced.Axes[0];
double[] xVals = xAxis.Values;            // freq-unit scaling handled as today when xAxis is frequency
Complex[]? cz = sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null;
double[]?  rz = sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null;
trace.SetCubeData(xVals, cz, rz, xAxis.Name, xAxis.Unit, plotType, freqUnit);
```
**Match axes by name** (not array position) so persisted `Slice` stays valid even if axis order ever shifts.
Guard rank-0/empty results (skip → no points).

## Persistence (`.cdd`)
`Slice` already round-trips (7.2c) as `{AxisName, Role, Index}` per axis. Confirm an N-D `Slice` (rank ≥3)
serializes/loads — it's the same list, just longer. No format bump (alpha; nullable/defaulted).

## Tests (`tests/Ui.Tests`, headless)
1. **Rank3_PinTwo_KeepOne:** a Complex `V{node,harmonic,Pin}` cube; assign node=pinned(k), harmonic=pinned(m),
   Pin=X → resolver yields a rank-1 cube over Pin; `Points.Count == Pin length`; values match
   `cube[k,m,..]`.
2. **SwitchX_Recomputes:** flip X from Pin to harmonic (Pin now pinned) → X axis + point count follow the
   harmonic axis.
3. **AxisMatchByName:** a `Slice` whose entries are in a different order than `cube.Axes` still resolves
   correctly (name-keyed).
4. **Rank3_Roundtrips_Cdd:** save+reload reproduces the rank-3 `Slice` and the curve.
5. **RealCube_SmithDisabled:** a Real rank-3 cube can't be assigned to a Smith plot (gate holds).

## Gate
Build 0W/0E; tests green. Manual: load an HB sweep `.npy` with a rank-3 cube (e.g. `V{node,harmonic,Pin}`),
add a plot, pick the cube, set node+harmonic pinned and Pin as X → a clean 1-D curve; change which axis is X →
the plot follows; save/reload keeps it.

## On completion
Note in `src/Ui/CLAUDE.md`: cube traces are authored via per-axis role assignment (X / pinned) over any-rank
cube; the owner resolves `(CubeName, Slice)` to a rank-1 slice by **name-matched** positional args
(`int` pins, `Range.All` keeps). Family role (one trace → N curves) lands in 7.3b.
