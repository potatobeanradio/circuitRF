# Sonnet Brief — Trace data-model conformance + table multi-x-axis

The trace system must obey the data-model spec (`docs/design/data-model.md` §7 and the authoritative
`src/Core/Data/CLAUDE.md`). Today it does not, in four ways. The spec is already correct — these are
conformance fixes, plus one genuinely new feature (the multi-x-axis table). Do the parts in order; they
are mostly independent. Build 0W/0E after each part; the whole brief is large, so commit per part.

The data model already SUPPORTS everything needed: `DataCube`'s indexer takes `Range.All` and sub-ranges
(`PlotInspectorViewModel.TrySetCubeData` already passes `Range.All` for kept axes and pins ints). The bug
is purely in the two TEXT parsers that reject the `All` keyword and `a..b` ranges, and in the table
renderer that assumes a single shared frequency x-axis.

Files touched:
- `src/Ui/DataDisplay/CubeTraceSpecParser.cs` — accept `All` + ranges (Part 1).
- `src/Ui/DataDisplay/TraceExpression.cs` — accept `All` + ranges (Part 1).
- `src/Engine/HarmonicBalance/HbEngine.cs` — exclude node-indexed current from the picker (Part 2).
- `src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs` — picker cube filter (Part 2, belt-and-suspenders).
- `src/Ui/DataDisplay/Renderers/TableRenderer.cs` — per-trace x-axis columns + dedup + sort (Part 3).
- `src/Ui/DataDisplay/Controls/PlotControl.cs` — Copy Table Data + hit-test + sort-click (Part 3).
- `docs/design/data-model.md`, `docs/design/data-display.md`, `src/Core/Data/CLAUDE.md` — doc updates (Part 4).

---

## PART 1 — Accept `All` and `a..b` ranges in the trace spec (fixes `V[All,4,1]` and `V[2..3,4,1]`)

Both parsers currently accept only `:`, a `"quoted label"`, or an integer per axis token. Add two token
forms, shared by both parsers via one helper so they can't drift:

- **`All`** (case-insensitive) — an alias for `:` (the kept X axis). Spec: *"`All` is an alias for the
  `..` range."*
- **`a..b`** — an end-exclusive sub-range that KEEPS the axis (narrowed). `2..3` = index 2 only; `2..4` =
  indices 2,3. Also accept open ends `..b`, `a..`, and `..` (≡ `:` / whole axis).

Add a shared token classifier (new file `src/Ui/DataDisplay/SliceTokenParser.cs`):
```csharp
using System;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay;

/// <summary>Classifies one bracket token of a cube slice spec. Shared by CubeTraceSpecParser
/// (single-slice picker text) and TraceExpression (multi-cube expressions) so the accepted
/// grammar can never drift between them. Conforms to src/Core/Data/CLAUDE.md slice semantics:
/// every slot is an axis INDEX; `int` pins/removes an axis; `:`/`All`/`a..b` keep it; ranges
/// are END-EXCLUSIVE (NumPy/C#, not MATLAB).</summary>
public static class SliceTokenParser
{
    public enum Kind { KeepWhole, KeepRange, PinIndex, PinLabel, Invalid }

    public readonly record struct Token(
        Kind Kind, int Index = 0, int RangeStart = 0, int RangeEndExclusive = 0, string Label = "");

    /// <summary>Parses one trimmed token against an axis of the given length.
    /// Resolves quoted labels against axisLabels (may be null). On failure returns Invalid and sets error.</summary>
    public static Token Parse(string tk, int axisLength, string[]? axisLabels, string axisName, out string error)
    {
        error = "";
        tk = tk.Trim();

        // Whole-axis: ":", "All" (case-insensitive), or ".."
        if (tk == ":" || tk == ".." || string.Equals(tk, "All", StringComparison.OrdinalIgnoreCase))
            return new Token(Kind.KeepWhole);

        // Range "a..b" (end-exclusive), with open ends "..b", "a..", "..".
        int dots = tk.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            string loStr = tk[..dots].Trim();
            string hiStr = tk[(dots + 2)..].Trim();
            int lo = 0, hiEx = axisLength;
            if (loStr.Length > 0 && !int.TryParse(loStr, out lo))
            { error = $"Bad range start '{loStr}' for axis '{axisName}'."; return new Token(Kind.Invalid); }
            if (hiStr.Length > 0 && !int.TryParse(hiStr, out hiEx))
            { error = $"Bad range end '{hiStr}' for axis '{axisName}'."; return new Token(Kind.Invalid); }
            lo   = Math.Clamp(lo,   0, axisLength);
            hiEx = Math.Clamp(hiEx, 0, axisLength);
            if (hiEx <= lo)
            { error = $"Empty range '{tk}' for axis '{axisName}' (end-exclusive)."; return new Token(Kind.Invalid); }
            return new Token(Kind.KeepRange, RangeStart: lo, RangeEndExclusive: hiEx);
        }

        // Quoted label "Vout".
        if (tk.Length >= 2 && tk[0] == '"' && tk[^1] == '"')
        {
            string label = tk[1..^1];
            if (axisLabels is null)
            { error = $"Axis '{axisName}' has no labels; use a numeric index."; return new Token(Kind.Invalid); }
            int idx = Array.IndexOf(axisLabels, label);
            if (idx < 0)
            { error = $"No label '{label}' in axis '{axisName}'."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: idx, Label: label);
        }

        // Integer index (pins/removes the axis).
        if (int.TryParse(tk, out int index))
        {
            if (index < 0 || index >= axisLength)
            { error = $"Index {index} out of range for axis '{axisName}' (0..{axisLength - 1})."; return new Token(Kind.Invalid); }
            return new Token(Kind.PinIndex, Index: index);
        }

        error = $"Cannot parse token '{tk}' for axis '{axisName}'.";
        return new Token(Kind.Invalid);
    }
}
```

### 1a. CubeTraceSpecParser — single-slice picker text (`V[All,4,1]`, `V[2..3,4,1]`)
`AxisSlice` (in `Trace.cs`) currently only models `PinToIndex`/`KeepAsX` with a single `Index`. A kept
*range* needs a start+end. Extend `AxisSlice` minimally — add two fields with defaults so existing
`new AxisSlice(name, role, idx)` call sites keep compiling:
```csharp
public readonly record struct AxisSlice(
    string AxisName, AxisRole Role, int Index,
    int RangeStart = 0, int RangeEndExclusive = -1)   // RangeEndExclusive < 0 ⇒ whole axis (":"/All)
{
    public bool IsNarrowedRange => Role == AxisRole.KeepAsX && RangeEndExclusive >= 0;
}
```
In `TryParse`, replace the colon-count check + per-token loop. The "exactly one X axis" rule must count
BOTH `KeepWhole` and `KeepRange` tokens as X candidates (a narrowed range on the sweep IS the X axis —
that is the user's `V[2..3,4,1]` case where the sweep becomes the table's first column):
```csharp
slice = new AxisSlice[tokens.Length];
int xCount = 0;
for (int i = 0; i < tokens.Length; i++)
{
    var axis = cube.Axes[i];
    var t = SliceTokenParser.Parse(tokens[i], axis.Length, axis.Labels, axis.Name, out error);
    switch (t.Kind)
    {
        case SliceTokenParser.Kind.KeepWhole:
            slice[i] = new AxisSlice(axis.Name, AxisRole.KeepAsX, 0); xCount++; break;
        case SliceTokenParser.Kind.KeepRange:
            slice[i] = new AxisSlice(axis.Name, AxisRole.KeepAsX, t.RangeStart,
                                     t.RangeStart, t.RangeEndExclusive); xCount++; break;
        case SliceTokenParser.Kind.PinIndex:
            slice[i] = new AxisSlice(axis.Name, AxisRole.PinToIndex, t.Index); break;
        default:
            return false;   // error already set
    }
}
if (xCount != 1)
{
    error = xCount == 0 ? "Need exactly one X axis (':' , 'All', or a range)."
                        : "Too many X axes — only one ':'/range is allowed.";
    return false;
}
```
Delete the now-dead inline token parsing and the old `colonCount` block.

The resolver `PlotInspectorViewModel.TrySetCubeData` (single-slice path) builds `args[d]` from the slice.
Make a kept *range* produce a C# `Range` instead of `Range.All`:
```csharp
if (found?.Role == AxisRole.KeepAsX)
{
    args[d] = found.Value.IsNarrowedRange
        ? new Range(found.Value.RangeStart, found.Value.RangeEndExclusive)   // end-exclusive
        : Range.All;
    xDim = d;
}
```
(The `DataCube` indexer already accepts `Range`; a narrowed range keeps the axis, yielding a rank-1 slice
over the sub-range — exactly what the table's x-axis column then shows.)

### 1b. TraceExpression — multi-cube expressions (`V[All,1,0]*2`, `V[2..3,1,0]`)
Same grammar via the shared helper. In the per-token loop, accept `KeepWhole`/`KeepRange` as the X axis
(still exactly one), pinning ints/labels otherwise:
```csharp
int xDim = -1;
var args = new object[cube.Rank];
for (int d = 0; d < tokens.Length; d++)
{
    var axis = cube.Axes[d];
    var t = SliceTokenParser.Parse(tokens[d], axis.Length, axis.Labels, axis.Name, out error);
    switch (t.Kind)
    {
        case SliceTokenParser.Kind.KeepWhole:
            args[d] = Range.All; if (xDim >= 0) { error = $"'{info.RefStr}': more than one X axis."; return false; } xDim = d; break;
        case SliceTokenParser.Kind.KeepRange:
            args[d] = new Range(t.RangeStart, t.RangeEndExclusive);
            if (xDim >= 0) { error = $"'{info.RefStr}': more than one X axis."; return false; } xDim = d; break;
        case SliceTokenParser.Kind.PinIndex:
            args[d] = t.Index; break;
        default:
            error = $"'{info.RefStr}': {error}"; return false;
    }
}
if (xDim < 0) { error = $"'{info.RefStr}': no X axis — use ':' , 'All', or a range."; return false; }
```
The downstream rank-1 check, equal-length validation, and substitution are unchanged (a narrowed range
still yields a rank-1 slice; equal-length validation across refs still applies — good, since mixing
`V[2..3,...]` with `V[:,...]` SHOULD error as a length mismatch).

**Part 1 tests** (`tests/Ui.Tests/CubeTraceTests.cs` + `TraceExpressionTests.cs`):
1. `V[All,4,1]` parses identically to `V[:,4,1]` (same Slice, same resolved X values).
2. `V[2..3,4,1]` → kept range on axis 0, rank-1 slice of length 1; resolves with the sweep axis as X.
3. `V[2..4,4,1]` → length-2 trace (end-exclusive).
4. `all` / `ALL` (case-insensitive) accepted.
5. `V[2..2,...]` → "empty range" error (end-exclusive, lo==hi).
6. `V[1..0,...]` → empty-range error.
7. TraceExpression: `V[All,1,0]` evaluates equal to `V[:,1,0]`.
8. Two X axes (`V[:, :, 0]`) → "more than one X axis" error (both parsers).

---

## PART 2 — INL must not be accessible by net node (current is a branch property)

Per `src/Core/Data/CLAUDE.md`: *"Node/net-indexed current (`ds["INl"][nodeIdx,…]`) is an internal
diagnostic, not a measurement accessor… Many currents sum at nodes; current flows through branches."*
The only public current path is the named-branch `I:instancePath:terminal` cube. The trace picker must
NOT offer node-indexed current.

**Find the offending cube in `HbEngine.cs`.** Search for where node-indexed current is written to the
DataSet — the cube name is likely `"INl"` or `"I"` carrying a `node` axis (grep `HbEngine.cs` for
`INl`, `"I"`, `new Axis("node"` near current emission, and `ds.Add`). Branch-current cubes are named
`I:<path>:<terminal>` (e.g. `I:X1.M1:d`) and have NO node axis. **Do not change the branch cubes.**

**Fix (engine, authoritative):** if a node-indexed current cube is emitted for diagnostics, rename it to
carry the internal `__` prefix (e.g. `__INl`) so it round-trips for diagnostics but is filtered
everywhere `__`-prefixed cubes already are (`DataSet.StackSweepAxis`, the trace picker, splotRF). If it
is NOT needed at all post-debug, stop emitting it. STOP and report which cube name you find and which
choice you made before editing, if it's ambiguous.

**Belt-and-suspenders (UI):** `TraceRowViewModel.RebuildSignals` enumerates `ds.Cubes` skipping
`S`/`Z0`/`__`-prefixed. Also skip any cube whose axis set includes a `node` axis AND whose name denotes
current (starts with `I` but is not the `I:`-branch form). Concretely, skip a cube when:
```csharp
bool isNodeIndexedCurrent =
    (cubeName == "I" || cubeName == "INl")
    && cube.Axes.Any(a => a.Name == "node");
if (cubeName is "S" or "Z0"
    || cubeName.StartsWith("__", StringComparison.Ordinal)
    || isNodeIndexedCurrent) continue;
```
This keeps the picker correct even against an older DataSet that still carries the un-prefixed name.
The branch-current `I:...` cubes (no `node` axis) remain offered.

**Part 2 tests:**
- Engine: run an HB analysis; assert the DataSet exposes `I:<path>:<term>` branch cubes and that any
  node-indexed current cube is `__`-prefixed (or absent). (Extend `HbLabeledNodesCubeTests` style.)
- UI (`CubeTraceTests`): build a DataSet containing a node-indexed current cube + a branch cube; assert
  `RebuildSignals` offers the branch cube and NOT the node-indexed one.

---

## PART 3 — Table with per-trace X axes (the substantial new feature)

**Current behavior (the bug):** `TableRenderer` builds ONE shared row axis (`GetSortedRowAxis` unions all
traces' X values), uses column 0 as the only x-axis column, and looks up every trace cell by that shared
value. Two traces with different x-axes (S-param vs freq, V vs Pin) collapse into a nonsense union.

**New design (user-specified):**
- Respect the user's trace/column order.
- Emit an X-axis column for EACH trace, immediately to the LEFT of that trace's value column.
- **Adjacent-dedup:** when two (or more) adjacent traces share EXACTLY the same x-axis data — same values
  AND same point count — collapse to a single shared X column spanning those adjacent traces. "Adjacent"
  means consecutive in column order; a non-matching trace between two matching ones breaks the run.
- Rows are per-X-group (each X column has its own row values); the table's row count = max points across
  groups. A trace shorter than the tallest group shows blank cells past its end.
- **Copy Table Data** must reproduce this exact column layout (WYSIWYG tab-delimited paste into Excel).
- Clicking the ascend/descend triangle on ANY x-axis column flips ascend/descend for the ENTIRE table.

### 3a. Column model — introduce an explicit ordered column list
Replace the implicit "col 0 = freq, col c = trace c-1" model with a built column plan. Add to
`TableRenderer`:
```csharp
public enum TableColKind { XAxis, TraceValue }

public sealed class TableColumn
{
    public TableColKind Kind;
    public int          FirstTraceIndex;     // XAxis: first trace this X column serves; TraceValue: the trace
    public string       Header = "";
    public string?      Unit;                 // XAxis only (freq-unit aware)
    public double[]     XValues = Array.Empty<double>();   // XAxis: the shared sorted X values for its group
    public bool         IsFreqUnit;           // XAxis: scale by plot.FreqUnits when true
}

/// <summary>Builds the ordered column plan: for each trace, an X column (unless the immediately
/// preceding trace shares identical X data — same values and count — in which case the value column
/// reuses the prior X column). Network (SNP) traces use Data.Frequencies as their X; cube traces use
/// CubeXValues/CubeXAxisName/CubeXUnit. Sort order (asc/desc) is applied to every X column's values.</summary>
public static List<TableColumn> BuildColumns(Plot plot) { … }
```
Build rules:
- For trace `t`, its X identity is: cube-bound → `(t.CubeXAxisName, t.CubeXUnit, t.CubeXValues)`;
  network → `("Freq", plot.FreqUnits-as-Hz, t.Data.Frequencies)`.
- Compare X identity to the PREVIOUS trace's X identity with an exact equality test: same length AND
  element-wise equal values (use a tolerance-free `==` on the stored doubles; they come from the same
  axis arrays so exact match is correct, matching the existing `FormatCubeCellAt` lookup which uses `==`).
  Same axis NAME+UNIT is required too (so "freq" and "Pin" never dedup even if values coincidentally match).
- If equal to the previous trace's X → reuse the previous X column (no new X column; the value column's
  `FirstTraceIndex` group extends). Else → emit a new X column before this trace's value column.
- Each X column stores its OWN sorted values (respect `plot.TableViewAscendingSortOrder`). Value lookups
  in that column's rows are by index into the (sorted) X values, then mapped to the trace's sample.

> Per-trace value lookup changes from "find row freq in this trace" to "row r of this X group → the
> trace's sample at the matching X". Keep a small map from sorted-row-index → trace-sample-index per
> column group. For a single-trace group this is just the sorted permutation of that trace's X.

### 3b. Layout, draw, hit-test, fit-width, sort
- `TableLayout` becomes column-driven: `ColCount = columns.Count`; `ColW[c]` from a per-column width.
  Width storage: keep `plot.ColumnWidth` for X columns and `trace.ColumnWidth` for value columns — for a
  shared X column use `plot.ColumnWidth`. (No model migration needed; alpha, no back-compat.)
- Row count = max `XValues.Length` across X columns. A cell whose column's group has no sample at row `r`
  renders blank ("").
- `DrawHeaderRow`: draw each column's header. **Draw the sort triangle on EVERY XAxis column header**
  (not just col 0). All show the same asc/desc state (`plot.TableViewAscendingSortOrder`).
- `HitTest`: return `FreqHeader` for ANY XAxis column header (carry `ColIndex`), `TraceHeader` for a
  TraceValue header (set `HitTrace`), `DataCell`/`MarkerGlyph` for value columns, `ResizeHandle` per
  column edge. Marker rows: a marker sits at its trace's X value; find the row within that trace's X
  group (not the global union).
- `CalcFitWidth(plot, colIndex, …)`: measure against the column plan (header + that column's cells).
- **Sort click:** in `PlotControl.HandleDoubleTapAt`, the Table `FreqHeader` branch already flips
  `plot.TableViewAscendingSortOrder`. Keep that, but it now fires for ANY X column (since HitTest returns
  `FreqHeader` for all of them) and the flip re-sorts every X column — satisfying "flips the entire
  table." No per-column sort state.

### 3c. Copy Table Data — WYSIWYG
Rewrite `PlotControl.CopyTableDataToClipboardAsync` to walk the SAME column plan
(`TableRenderer.BuildColumns`) and the visible row window, emitting tab-delimited columns in the exact
on-screen order (X col, value col(s), next X col, …). Header row = each column's header text; data rows =
each column's formatted cell for that visible row (blank when the group has no sample). Provide a
`TableRenderer` helper so the renderer and the copy path can't drift:
```csharp
public static string FormatColumnCell(TableColumn col, int rowIndex, Plot plot);   // X or value, "" if absent
public static (string[] headers, string[][] rows) BuildCopyGrid(
    Plot plot, (double W, double H) canvasSize, float zoomLevel);   // visible rows only, WYSIWYG
```
`CopyTableDataToClipboardAsync` then just joins `headers`/`rows` with `\t` and `\n`.

### 3d. Marker drag freq mapping
`PlotControl` table marker drag currently reads `TableRenderer.GetSortedFrequencies(plot)[rowIndex]`.
With per-trace X, map the dragged row to the X value of the marker's OWN trace group, not the global
union. Use the hit column's group X values.

**Part 3 tests** (`tests/Ui.Tests/TableCubeTraceTests.cs` + `TableCubeTraceTests`):
1. Two traces, different x-axes (freq vs Pin) → `BuildColumns` yields 4 columns: X(freq),V0,X(Pin),V1.
2. Two adjacent traces, identical X (same values+count+name) → 3 columns: X(shared),V0,V1.
3. Three traces A,B,C where A&C share X but B differs → no dedup (A and C not adjacent): 6 columns.
4. Two traces same values but different axis NAME ("freq" vs "Pin") → NOT deduped.
5. Two traces same axis but different point counts → NOT deduped.
6. `BuildCopyGrid` headers/rows match the column plan exactly (WYSIWYG), blanks where a group is shorter.
7. Sort flip: toggling `TableViewAscendingSortOrder` reverses every X column's values.
8. Single-trace and all-same-X tables still render as today (regression: 2 columns / 1 shared X).

---

## PART 4 — Documentation

The slice/accessor semantics in `docs/design/data-model.md` §7 and `src/Core/Data/CLAUDE.md` are already
correct (they already document `All`, end-exclusive ranges, index-only slots, and branch-only current).
Updates needed:
1. **`src/Core/Data/CLAUDE.md`** — under "HB branch currents", add one sentence: node-indexed current is
   stored (if at all) as the `__`-prefixed internal cube and is filtered from the trace picker; restate
   that the trace UI offers only `I:<path>:<term>` branch cubes.
2. **`docs/design/data-display.md`** — add a subsection "Table with multiple X axes" documenting the
   per-trace X column rule, adjacent-dedup (same values AND count AND axis name/unit), entire-table sort
   on any X header, and WYSIWYG Copy Table Data. This is the new behavior, so it must be written down.
3. **`docs/design/data-model.md`** §7 — add a short note that the Data-Display *trace spec text box*
   accepts the same slice grammar as the accessors: `:` / `All` / `a..b` (end-exclusive) for the kept X
   axis, integer/`"label"` to pin. (One paragraph; the semantics already match the doc.)

On completion, note in `src/Ui/DataDisplay/CLAUDE.md` (create if absent) that the trace spec grammar lives
in `SliceTokenParser` (shared by `CubeTraceSpecParser` + `TraceExpression`) and the table column layout in
`TableRenderer.BuildColumns` (shared by draw + Copy).

## Gate (per part)
Build 0W/0E (TreatWarningsAsErrors). All new + existing trace/table tests green. Verify on disk before
declaring done: `V[All,4,1]` and `V[2..3,4,1]` resolve; node-indexed current absent from the picker; a
two-different-x-axis table renders distinct X columns and Copy Table Data pastes WYSIWYG into a spreadsheet.
