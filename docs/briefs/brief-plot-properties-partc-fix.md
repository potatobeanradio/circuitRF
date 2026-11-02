# Sonnet Brief — Part C follow-up: data-source switch must reset/adapt the trace expression

Two bugs in `TraceRowViewModel.OnSelectedSignalChanged` (`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`).
Both stem from the same omission: when the data source changes, the trace's `Expression` is never
re-derived, and `Trace.IsCubeBound` is `CubeName is not null || Expression is not null`. Build 0W/0E.

## Bug 1 — HB → S-param still shows HB fields
`IsCubeBound => CubeName is not null || Expression is not null`. The network branch sets
`_trace.CubeName = null` but leaves the old HB `Expression` (e.g. `V[:, "Vout2", 2]`) intact, so
`IsCubeBound` stays `true` and the card keeps showing the cube editor. **Fix:** in the network branch,
clear all cube/expression state.

## Bug 2 — HB.V → HB.I keeps the V expression
On a cube→cube switch the code sets `CubeName` + a fresh default `Slice`, but never rebuilds
`Expression`. Value resolution and `CubeShorthand` honor `Expression` first, so the stale `V[...]`
persists. **Fix:** on a cube→cube switch, rebuild `Expression` from the new cube, **carrying over the
old slice parameters by axis name** (keep the harmonic index, keep which axis is X, etc.); for axes that
don't exist in the new cube or whose saved index is out of range, fall back to a valid index (0). Example:
`V[:, "Vout2", 2]` (axes freq,node,harmonic) → switching to the `I:X1:g` cube (axes freq,harmonic) keeps
the harmonic index and X-on-freq, yielding `I:X1:g[:, 2]`.

---

## The edit — `OnSelectedSignalChanged`

Current cube branch and network branch:
```csharp
if (value.IsCubeBound)
{
    _trace.CubeName = value.CubeName;
    _trace.Slice    = value.Slice?.ToArray();  // default slice; user edits via AxisRoles
    _trace.SourceZ0PerPort   = null;
    _trace.SourceZ0IsUnusual = false;
    RebuildAxisRoles();
}
else
{
    _trace.CubeName = null;
    _trace.Slice    = null;
    AxisRoles.Clear();
    _trace.Data     = value.Entry.Snp!;
    ApplySourceZ0(value.Entry);
    if (value.Derived != DerivedParameters.None) { _trace.Derived = value.Derived; }
    else { _trace.Derived = DerivedParameters.None; _trace.Row = value.Row; _trace.Col = value.Col; }
}
```

Replace with:
```csharp
if (value.IsCubeBound)
{
    // Carry over as much of the prior slice as the new cube allows (match by axis name),
    // then re-derive the Expression so the spec adapts to the new data source.
    var oldSlice = _trace.Slice;          // may be from a different cube (e.g. V → I)
    _trace.CubeName = value.CubeName;
    _trace.Slice    = BuildCarriedSlice(value, oldSlice);   // default slice + carried-over indices
    _trace.Transform       = CubeTransform.None == _trace.Transform ? _trace.Transform : _trace.Transform; // keep transform
    _trace.InvalidSpecText = null;
    _trace.ExpressionError = null;
    _trace.Expression      = _trace.BuildPickerExpression();  // adapt expression to the new cube/slice

    // Cube-bound traces have no per-port Z0 from the S matrix.
    _trace.SourceZ0PerPort   = null;
    _trace.SourceZ0IsUnusual = false;
    RebuildAxisRoles();
}
else
{
    // Switching to network-bound: clear ALL cube identity, INCLUDING the expression —
    // otherwise IsCubeBound (CubeName is not null || Expression is not null) stays true and
    // the card keeps showing HB fields.
    _trace.CubeName        = null;
    _trace.Slice           = null;
    _trace.Expression      = null;   // ← Bug 1 fix
    _trace.InvalidSpecText = null;
    _trace.ExpressionError = null;
    _trace.Transform       = CubeTransform.None;
    AxisRoles.Clear();
    _trace.Data     = value.Entry.Snp!;
    ApplySourceZ0(value.Entry);
    if (value.Derived != DerivedParameters.None) { _trace.Derived = value.Derived; }
    else { _trace.Derived = DerivedParameters.None; _trace.Row = value.Row; _trace.Col = value.Col; }
}
```
> Keep the existing tail of the method unchanged (`_parent.RebuildAndNotify();` then the
> `OnPropertyChanged(nameof(IsCubeBoundTrace)); OnPropertyChanged(nameof(ShowAllNodesToggleVisible)); RefreshDescription();`
> added in the prior brief). With `Expression` now correctly nulled/rebuilt, `IsCubeBoundTrace` flips
> correctly and the spec field (`SpecShorthand` → `CubeShorthand` → `Expression`) shows the adapted text.
> (The `_trace.Transform` self-assignment line above is just a no-op placeholder to show transform is
> intentionally preserved on cube→cube; you can drop it — do NOT reset Transform on the cube branch.)

## The carryover helper
Add to `TraceRowViewModel`. It builds the new cube's slice: start from the new cube's **default** slice
(`value.Slice`), then for every axis whose **name** also appears in the old slice, copy the old
role + index (clamped to the new axis length); fill the quoted `Label` for label-bearing axes (node) so
the shorthand emits the net name. Guarantee exactly one `KeepAsX` axis.

```csharp
/// <summary>
/// Builds the slice for a newly-selected cube, preserving as many parameters from the previous
/// slice as possible (matched by axis NAME): same role (X vs pinned) and the same pin index when it
/// is in range, else clamped to 0. Axes absent from the old slice use the new cube's default
/// (axis 0 = X, rest pinned at 0). Exactly one axis ends up as X. Label is set from the new cube's
/// axis labels so node slots render as quoted net names (e.g. "Vout2").
/// </summary>
private AxisSlice[] BuildCarriedSlice(TraceDataItem value, AxisSlice[]? oldSlice)
{
    // Resolve the new cube to read axis names / lengths / labels.
    var ds   = value.Entry.Data;
    var cube = (ds is not null && value.CubeName is not null && ds.Contains(value.CubeName))
        ? ds[value.CubeName] : null;

    // Fallback: no cube metadata available → just use the item's default slice.
    if (cube is null) return value.Slice?.ToArray() ?? Array.Empty<AxisSlice>();

    int rank = cube.Rank;
    var result = new AxisSlice[rank];

    // Old slice lookup by axis name.
    var old = new Dictionary<string, AxisSlice>(StringComparer.Ordinal);
    if (oldSlice is not null)
        foreach (var s in oldSlice) old[s.AxisName] = s;

    bool anyX = false;
    for (int d = 0; d < rank; d++)
    {
        var ax  = cube.Axes[d];
        int len = ax.Length;

        if (old.TryGetValue(ax.Name, out var prev))
        {
            // Carry over role + index (clamped). Index meaningless for X but harmless.
            int idx  = Math.Clamp(prev.Index, 0, Math.Max(0, len - 1));
            var role = prev.Role;
            string lbl = (role == AxisRole.PinToIndex && ax.Labels is { Length: > 0 } && idx < ax.Labels.Length)
                ? ax.Labels[idx] : "";
            result[d] = new AxisSlice(ax.Name, role, idx, Label: lbl);
            if (role == AxisRole.KeepAsX) anyX = true;
        }
        else
        {
            // New axis: default to pinned at 0 (X assigned below if none carried over).
            string lbl = (ax.Labels is { Length: > 0 }) ? ax.Labels[0] : "";
            result[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, 0, Label: lbl);
        }
    }

    // Guarantee exactly one X axis: if the carried-over X didn't survive, promote axis 0;
    // if MULTIPLE somehow ended X (shouldn't, old slice has one), demote all but the first.
    if (!anyX && rank > 0)
        result[0] = result[0] with { Role = AxisRole.KeepAsX, Label = "" };
    else
    {
        bool seenX = false;
        for (int d = 0; d < rank; d++)
            if (result[d].Role == AxisRole.KeepAsX)
            {
                if (seenX) result[d] = result[d] with { Role = AxisRole.PinToIndex };
                seenX = true;
            }
    }

    return result;
}
```

Notes:
- `value.Entry.Data` is the `DataSet` (same accessor `RebuildAxisRolesCore`/`TrySetCubeData` use);
  `ds[cubeName]` → cube with `.Rank`, `.Axes[d].Name/Length/Labels`. Use the exact member names the
  rest of the file uses (e.g. `cube.Axes`, `ax.Length`, `ax.Labels`).
- A pinned label-bearing axis (node) gets `Label` set, so `BuildPickerExpression` emits the quoted
  net name (`"Vout2"`); the X axis and non-label axes emit `:` / index as before.
- The example holds: V's slice `{freq:X, node:pin@k, harmonic:pin@2}` → switching to `I:X1:g`
  (axes freq, harmonic) keeps `freq:X` and `harmonic:pin@2`, drops node (absent) → `I:X1:g[:, 2]`.

## Gate
Build 0W/0E. Manual checks:
- HB cube trace → S-param source: card immediately shows S-param fields (matrix button + Z0 row), hides
  the cube spec editor; the spec text is gone (`IsCubeBound` false). Reverse (S-param → HB) shows the
  cube editor.
- HB.V (`V[:, "Vout2", 2]`) → HB.I (`I:X1:g`): spec field changes to `I:X1:g[:, 2]` — harmonic index 2
  preserved, X stays on freq, node dropped; the plot updates to current data.
- Switching between two cubes that share all axes preserves every pinned index and the X assignment.
- Switching to a cube with a shorter axis clamps the carried index into range (no crash, picks a valid
  index).

Add/extend a unit test on `BuildCarriedSlice` (or the public switch path) asserting: shared-axis index
carryover, X-role carryover, out-of-range clamp, and absent-axis drop. Report the exact `DataSet`/cube
axis member names you used if they differ from this brief.
