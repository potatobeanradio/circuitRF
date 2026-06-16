# Sonnet Brief — Table plot: support cube-bound traces (X-axis rows, NaN fix, cube-shorthand header)

**Problem (confirmed).** A Table plot of a cube-bound HB-sweep trace (e.g. V at Vout, fundamental, swept over
Pin) renders **all cells NaN**, and column 0 is labelled **"Freq"**. Root cause: `TableRenderer` is hardwired to
frequency — the row axis is `GetSortedFrequencies(plot)` (union of `trace.Data.Frequencies`), column 0 is
`"Freq (GHz)"`, and `FormatTraceCell` looks up a value via `Array.FindIndex(trace.Data.Frequencies, f => f ==
freq)`. A cube trace's X-axis is the **pinned/kept axis** (Pin), not frequency, so `Data.Frequencies` doesn't
match → every cell is NaN. This is a vestige of "Tables only show S-params over frequency."

**Goal.** When a Table's traces are cube-bound, the table's first column is the trace's **X-axis** (the kept axis,
e.g. `Pin`), labelled with that axis's name/unit; cells read the cube values at each X point; and the trace
column header uses the **DataCube shorthand** (e.g. `V["Vout", 1, :]`).

## Cube data already lives on the Trace — just expose it
`Trace` (cube-bound) holds (private): `_cubeXValues` (double[]), `_cubeComplexValues` (Complex[]?),
`_cubeRealValues` (double[]?), `_cubeXAxisName` (string), `_cubeXUnit` (string?), plus public `CubeName`,
`Slice` (`AxisSlice[]`), `Transform`, `IsCubeBound`. `BuildCubePath` already maps these to `Points` with the
transform. Add **read accessors** (no recompute — the owner already filled them via `SetCubeData`):
```csharp
// On Trace (cube-bound reads for the Table renderer)
public IReadOnlyList<double>?   CubeXValues   => _cubeXValues;
public IReadOnlyList<Complex>?  CubeComplex   => _cubeComplexValues;
public IReadOnlyList<double>?   CubeReal      => _cubeRealValues;
public string                   CubeXAxisName => _cubeXAxisName;
public string?                  CubeXUnit     => _cubeXUnit;
```
Also add a **cube cell formatter** parallel to the existing `FormatTraceCell`, returning the post-transform value
at an X index. The transform/format logic already exists in `BuildCubePath`; factor the per-element value map
into a helper both can call, or replicate the small switch:
```csharp
/// <summary>Formats the cube value at X index i for the Table (post-Transform). "" if out of range.</summary>
public string FormatCubeCell(int i, PrecisionFormat fmt, int fracDigits)
{
    if (!IsCubeBound || _cubeXValues is null || i < 0 || i >= _cubeXValues.Length) return "NaN";
    string f = $"{fmt}{fracDigits}";
    if (_cubeComplexValues is not null)
    {
        var z = _cubeComplexValues[i];
        // For Complex transform (None) show MA like the matrix path; else show the scalar transform.
        return Transform switch
        {
            CubeTransform.None  => FormatComplexMA(z, f),          // mag∠deg
            CubeTransform.Conj  => FormatComplexMA(Complex.Conjugate(z), f),
            CubeTransform.dB20  => (20*Math.Log10(Math.Max(z.Magnitude,1e-300))).ToString(f),
            CubeTransform.dB10 or CubeTransform.dB => (10*Math.Log10(Math.Max(z.Magnitude,1e-300))).ToString(f),
            CubeTransform.Mag   => z.Magnitude.ToString(f),
            CubeTransform.Phase => (z.Phase*180/Math.PI).ToString(f),
            CubeTransform.Real  => z.Real.ToString(f),
            CubeTransform.Imag  => z.Imaginary.ToString(f),
            _                   => z.Magnitude.ToString(f),
        };
    }
    if (_cubeRealValues is not null) { /* real transform switch, same as BuildCubePath */ }
    return "NaN";
}
```
(Keep the exact dB20/dB10 distinction from `BuildCubePath`. Reuse the existing `FormatMA`/`FormatRI`/`FormatDB`
helpers in `TableRenderer` for the complex case if you prefer they stay in the renderer — then pass the raw
Complex out instead and let `TableRenderer.FormatTraceCell` format it. Either factoring is fine; don't duplicate
the dB-base footgun.)

## Cube-shorthand header
Add a `Trace` member that renders the DataCube-shorthand description for a cube-bound trace, used as the Table
column header (and reusable elsewhere):
```csharp
/// <summary>DataCube-shorthand label, e.g. V["Vout", 1, :] — pinned axes show their pinned label/index,
/// the kept (X) axis shows ':'. Falls back to ShortDescription for non-cube traces.</summary>
public string CubeShorthand
{
    get
    {
        if (!IsCubeBound || Slice is null) return ShortDescription;
        var parts = Slice.Select(s => s.Role == AxisRole.KeepAsX
            ? ":"                                   // kept axis
            : FormatPinToken(s));                   // pinned: label if available else index
        var inner = string.Join(", ", parts);
        var t = Transform == CubeTransform.None ? "" : $"{Transform.ToString().ToLowerInvariant()} ";
        return $"{t}{CubeName}[{inner}]";
    }
}
```
`FormatPinToken(AxisSlice)`: the owner knows axis labels; if the Trace doesn't carry them, just use the pin
**index** (`s.Index`) or the X value if available. Keep it simple — string axis labels are nice-to-have; the
index form `V["Vout", 1, :]`-style is acceptable. (If a pinned axis is a named node like `Vout`, show it quoted;
otherwise show the integer index. Use what's available without plumbing new data — the cube labels may not be on
the Trace, in which case index form is the documented fallback.) Document which form you used.

## TableRenderer — cube mode
The table is cube-mode when **all** traces in the plot are cube-bound (mixed cube+SNP tables are out of scope —
if mixed, fall back to today's frequency behavior for the SNP traces and show NaN for cube traces, OR keep
frequency mode; pick frequency mode and note it). Determine once:
```csharp
bool cubeMode = plot.Traces.Count > 0 && plot.Traces.All(t => t.IsCubeBound);
```

### Row axis (replaces `GetSortedFrequencies` when cubeMode)
Add a cube row-axis builder: the **union of all traces' `CubeXValues`** (sorted), analogous to the frequency
union. In practice all cube traces in one table share the same X axis (same sweep), but union keeps it robust:
```csharp
public static double[] GetSortedRowAxis(Plot plot)
{
    if (!plot.Traces.All(t => t.IsCubeBound))
        return GetSortedFrequencies(plot);            // legacy
    var set = new SortedSet<double>();
    foreach (var t in plot.Traces)
        if (t.CubeXValues is { } xs) foreach (var x in xs) set.Add(x);
    var arr = set.ToArray();
    if (!plot.TableViewAscendingSortOrder) Array.Reverse(arr);
    return arr;
}
```
Use `GetSortedRowAxis` everywhere `GetSortedFrequencies` feeds the layout/draw/hit-test/copy (BuildLayout's
`Frequencies`, `GetVisibleFrequencies`, marker rows). Keep the field name `layout.Frequencies` or rename to
`layout.RowValues` — your call, but if you rename, update all uses.

### Column 0 header + cells (cubeMode)
- Header: instead of `$"Freq ({plot.FreqUnits.Description()})"`, use the trace X-axis name+unit. All cube traces
  share it; take the first: `var x0 = plot.Traces[0]; string col0 = string.IsNullOrEmpty(x0.CubeXUnit) ?
  x0.CubeXAxisName : $"{x0.CubeXAxisName} ({x0.CubeXUnit})";` (e.g. `"Pin"` or `"Pin (dBm)"`). **Do not** apply
  `FreqUnits.Scale()` in cube mode — the X values are already in the axis's own unit.
- Cell: in cube mode, column-0 cell text = the raw X value formatted with the plot's format
  (`(rowValue).ToString(plotFmt)`), no frequency scaling.
- Keep the sort-arrow triangle behavior; it now sorts the X axis.

### Trace data cells (cubeMode)
For trace column `ti` at row value `x`: find the trace's X index `i = Array.FindIndex(t.CubeXValues, v => v ==
x)` (or nearest within tolerance — but exact match is fine since the union is built from these same values), then
`t.FormatCubeCell(i, t.FormatString, t.MaximumFractionDigits)`. If `i < 0` (a trace lacks this X point), "NaN" —
same convention as the frequency path. **Route through a cube-aware branch in `FormatTraceCell`** (or a sibling
the draw/measure loops call when `plot.Traces[ti].IsCubeBound`).

### Header label (cubeMode)
Trace column header uses `trace.CubeShorthand` instead of `ShortDescription`/`Description`. Keep the
`showFilePrefix` behavior optional (cube shorthand can prepend the source stem when `showFilePrefix`).

### CalcFitWidth (cubeMode)
Update the auto-fit measurement to use `GetSortedRowAxis`, the cube column-0 header, `CubeShorthand` headers, and
`FormatCubeCell` for data cells, so column widths fit cube content. Mirror the existing structure; just swap the
text sources under `cubeMode`.

## Markers (cube traces)
Markers on cube traces are already guarded off elsewhere (`IsCubeBound` returns NaN/zero from the marker
methods). In the Table, **skip marker glyphs/highlights for cube traces** (the `trace.Markers` loop will be empty
for cube traces in practice). Don't add marker support here — out of scope.

## Tests (`tests/Ui.Tests`, headless — exercise the model/format, not Skia draw)
1. **CubeShorthand_Format:** a cube trace with `CubeName="V"`, slice `["Vout" pinned, harmonic=1 pinned, Pin
   kept]` → `CubeShorthand` == `V["Vout", 1, :]` (or the documented index-form fallback).
2. **FormatCubeCell_Transform:** a complex cube with `Transform=dB20` → cell at index i equals
   `20*log10(|z_i|)` formatted; `Transform=None` → mag∠deg.
3. **GetSortedRowAxis_UsesCubeX:** a plot of two cube traces sharing a Pin axis → row axis == the Pin values
   (sorted), not frequencies; a legacy SNP plot still returns frequencies.
4. **Cube_NoNaNForValidIndex:** every row value present in `CubeXValues` formats to a finite/`mag∠` string, not
   "NaN" (the regression that proves the bug is fixed).

## Gate
Build 0W/0E; tests green. Manual: load `HB1.npy`, add a Table plot, add a trace V at Vout / fundamental / Pin
kept → column 0 header reads `Pin` (or `Pin (dBm)`), rows are the Pin sweep values, the data column header reads
`V["Vout", 1, :]`, and cells show real values (no NaN). A legacy S-param Touchstone Table is unchanged
(frequency rows, `Freq (GHz)` header).

## On completion
Note in `src/Ui/CLAUDE.md`: the Table plot supports cube-bound traces — column 0 becomes the trace's kept
(X) axis (name+unit, no freq scaling), cells read cube values via `Trace.FormatCubeCell`, and trace headers use
`Trace.CubeShorthand` (DataCube `Name[pinned, …, :]` form). Mixed cube+SNP tables fall back to frequency mode.
Markers on cube traces remain unsupported.
