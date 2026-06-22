# Brief 7.5c — TableRenderer summary path (src/Ui)

**Phase:** 7.5 (loadpull summary table). **Layer:** `src/Ui` rendering only. **Depends on:** 7.5b (model +
persistence — landed). **Design:** `circuitRF/docs/design/loadpull-summary-table.md` §2.5, §2.6, §3.

Goal: render a **summary Table** — one row per loadpull frequency, one column per summary trace, plus a leading
Freq anchor column, plus a top-right right-aligned title. This slice draws from **pre-computed cell values** held
on `SummaryColumnData`; it does NOT compute them from the engine (the VM does that in 7.5d, mirroring how the VM
populates `ContourData.Grid` for contours and the renderer just draws it). After this slice, a summary table
with hand-populated cells renders correctly; live data wiring is 7.5d.

File: `<repo>/src/Ui/DataDisplay/Renderers/TableRenderer.cs` (+ a small addition to
`SummaryColumnData`).

**TreatWarningsAsErrors is ON.** No unused privates; nullable property reads into locals; no `<`/`>` in `///`.

---

## Architecture (read before coding)

The existing `TableRenderer` builds its column plan in `BuildColumns(plot)` by reading each trace's X axis
(frequency or cube sweep) and value samples from `trace.Points`/`CubeXValues`. Rows = the X-axis values. This is
the **standard** table path and must remain unchanged for non-summary tables.

A **summary** table is structurally different:
- Rows = the loadpull **frequencies** (one row per freq), shared across all columns.
- Columns = a leading **Freq** anchor column + one **summary** column per trace (`IsSummaryColumn`).
- Each summary cell value is read at the per-frequency MXP/MXE optimum — computed by the VM (7.5d), stored on
  `SummaryColumnData`, and just *drawn* here.

The renderer cannot compute summary values itself: `LoadpullSurface` needs the source `DataSet`, which the
renderer doesn't hold (Trace holds no DataSet — the VM owns it). So the contract is: **VM populates derived
cell arrays on `SummaryColumnData`; the renderer reads them** — exactly the contour pattern
(`ContourData.Grid`/`Levels` populated by `RebuildContour`, drawn by `ContourRenderer`).

**Strategy:** detect summary mode at the top of the public entry points and branch to a parallel summary
column-plan + draw path, reusing the existing layout machinery (row heights, backgrounds, borders, clipping,
scroll) as much as possible.

---

## Add 1 — derived cell storage on `SummaryColumnData`

In `<repo>/src/Ui/DataDisplay/Models/SummaryColumnData.cs`, add derived (non-
persisted) fields the VM fills and the renderer reads. Mirror how `ContourData` holds derived `Grid` not in the
config:

```csharp
// ---- Derived cell values (set by the VM's RebuildSummary; NOT persisted) ----
// One entry per frequency row (same length/order as the table's frequency list).
// Real columns use CellsReal; complex columns (Zload/Zsource/Zin) use CellsComplex.
// Null/empty until the VM populates them. NaN entry → rendered as blank/"NaN".

/// <summary>Per-frequency real cell values (Metric / OperatingPoint columns). Null until populated.</summary>
public double[]? CellsReal { get; set; }

/// <summary>Per-frequency complex cell values (Zload / Zsource / Zin columns). Null until populated.</summary>
public System.Numerics.Complex[]? CellsComplex { get; set; }
```

These are derived state — do NOT add them to `SummaryColumnConfig` or `Clone()` (Clone already copies only
authoring fields; leave derived arrays null on the clone so the pasted trace recomputes — same as
`ContourData.Clone()` leaving `Grid` null).

> The VM (7.5d) sets `CellsReal` for `Kind ∈ {Metric, OperatingPoint}` and `CellsComplex` for
> `Kind ∈ {Zload, Zsource, Zin}`. `SummaryColumns.IsComplexColumn(Kind)` (from 7.5b) tells the renderer which to
> read.

---

## Add 2 — frequency row axis for summary tables

The summary table's rows are the dataset frequencies. The VM owns the frequency list (from
`LoadpullSurface.Frequencies`), but the renderer needs it to lay out rows. Store it where the renderer can read
it without the engine. Simplest: a per-plot derived array the VM sets alongside the cells.

Add to `Plot.cs` (Table region, derived/non-persisted — plain field):
```csharp
/// <summary>Per-frequency row axis (Hz) for a summary Table, set by the VM's RebuildSummary.
/// Null/empty for non-summary tables. Not persisted (re-derived on load).</summary>
public double[]? SummaryFreqs { get; set; }
```

> Rationale: every summary column shares the same frequency rows, so the axis lives on the plot, not per-column.
> The renderer reads `plot.SummaryFreqs` for row count + the Freq column values. The frequencies are stored in
> Hz; the renderer scales by `plot.FreqUnits.Scale()` for display (same as the standard freq column).

---

## Add 3 — summary detection + branch

At the top of `TableRenderer`, add:
```csharp
/// <summary>True when this Table should render as a loadpull summary (one row per freq, columns
/// read at the MXP/MXE optimum). Triggered by any trace carrying SummaryColumnData.</summary>
public static bool IsSummaryTable(Plot plot)
{
    var traces = plot.Traces;
    for (int i = 0; i < traces.Count; i++)
        if (traces[i].IsSummaryColumn) return true;
    return false;
}
```

Branch in the public entry points. In `Draw(...)`, `HitTest(...)`, `BuildColumns(...)`, `BuildLayout(...)`,
`TotalColumnWidth(...)`, `CalcFitWidth(...)`, and `BuildCopyGrid(...)`: when `IsSummaryTable(plot)`, use the
summary plan/format helpers below instead of the standard ones. Keep the standard path byte-for-byte for
non-summary tables.

Cleanest factoring: make `BuildColumns` and `FormatColumnCell` summary-aware so the existing `BuildLayout`/draw
loop "just works" with a summary column plan. Specifically:

### 3a. `BuildColumns` → summary plan
Add a `BuildSummaryColumns(plot)` and call it from `BuildColumns` when `IsSummaryTable`:
```csharp
public static List<TableColumn> BuildColumns(Plot plot)
{
    if (IsSummaryTable(plot)) return BuildSummaryColumns(plot);
    // ... existing standard implementation unchanged ...
}

private static List<TableColumn> BuildSummaryColumns(Plot plot)
{
    var result = new List<TableColumn>(plot.Traces.Count + 1);
    double[] freqs = plot.SummaryFreqs ?? Array.Empty<double>();

    // Leading Freq anchor column. XValues = freqs (Hz); IsFreqUnit so FormatColumnCell scales them.
    // FirstTraceIndex = the first summary trace (used only for the anchor's column-width lookup).
    int firstSummary = 0;
    for (int i = 0; i < plot.Traces.Count; i++)
        if (plot.Traces[i].IsSummaryColumn) { firstSummary = i; break; }

    result.Add(new TableColumn
    {
        Kind            = TableColKind.XAxis,
        FirstTraceIndex = firstSummary,
        Header          = SummaryColumns.FreqHeader(plot.FreqUnits),
        Unit            = "Hz",
        XValues         = freqs,
        IsFreqUnit      = true,
    });

    // One value column per summary trace, in trace order.
    for (int ti = 0; ti < plot.Traces.Count; ti++)
    {
        var trace = plot.Traces[ti];
        if (!trace.IsSummaryColumn) continue;
        var sc = trace.SummaryColumn!;
        string header = string.IsNullOrEmpty(sc.Header) ? SummaryColumns.AutoHeader(sc) : sc.Header;
        result.Add(new TableColumn
        {
            Kind            = TableColKind.TraceValue,
            FirstTraceIndex = ti,
            Header          = header,
            XValues         = freqs,   // shares the freq rows
        });
    }
    return result;
}
```

> The standard `BuildLayout` computes `RowCount` from the longest `XAxis` column's `XValues.Length` — which is
> `freqs.Length` here. Row heights, scroll, borders, backgrounds all work unchanged. Column widths come from the
> same `plot.ColumnWidth` / `trace.ColumnWidth` path (a summary trace's `SummaryColumn.ColumnWidth` can feed
> `trace.ColumnWidth` when the VM creates it; OR extend the width lookup — see "Column widths" note).

### 3b. `FormatColumnCell` → summary cells
Make cell formatting summary-aware. The simplest non-invasive approach: add a summary branch at the top of
`FormatColumnCell` keyed on the trace being a summary column:
```csharp
public static string FormatColumnCell(TableColumn col, int rowIndex, Plot plot)
{
    if (col.Kind == TableColKind.XAxis)
    {
        // Freq anchor (summary) uses the same freq-scaling branch as standard.
        if (rowIndex >= col.XValues.Length) return "";
        if (col.IsFreqUnit)
        {
            double xVal = col.XValues[rowIndex];
            string fmtF = $"{plot.FormatString}{plot.MaximumFractionDigits}";
            return (xVal * plot.FreqUnits.Scale()).ToString(fmtF);
        }
        // ... existing XAxis handling (scalar/node/non-freq) unchanged ...
    }

    var trace = plot.Traces[col.FirstTraceIndex];
    if (trace.IsSummaryColumn)
        return FormatSummaryCell(trace.SummaryColumn!, rowIndex);

    // ... existing TraceValue handling (family/cube/network) unchanged ...
}

private static string FormatSummaryCell(SummaryColumnData sc, int rowIndex)
{
    if (SummaryColumns.IsComplexColumn(sc.Kind))
    {
        var cells = sc.CellsComplex;
        if (cells is null || rowIndex < 0 || rowIndex >= cells.Length) return "";
        var z = cells[rowIndex];
        if (double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)) return "NaN";
        return FormatComplexOhms(z, 2);     // "R+jX Ω", 2 decimals (design §3)
    }
    else
    {
        var cells = sc.CellsReal;
        if (cells is null || rowIndex < 0 || rowIndex >= cells.Length) return "";
        double v = cells[rowIndex];
        if (double.IsNaN(v)) return "NaN";
        string fmt = $"F{sc.FractionDigits}";
        return v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Formats a complex impedance as "R+jX Ω" with the given decimals (design §3,
/// reference complex2str prec=[2,2]).</summary>
private static string FormatComplexOhms(System.Numerics.Complex z, int decimals)
{
    string f    = $"F{decimals}";
    var    ci   = System.Globalization.CultureInfo.InvariantCulture;
    string sign = z.Imaginary >= 0 ? "+" : "-";
    return $"{z.Real.ToString(f, ci)}{sign}j{Math.Abs(z.Imaginary).ToString(f, ci)} Ω";
}
```

> Cell text uses the `∠`/`Ω` glyphs → the existing `DrawDataRows` already passes the DejaVu fallback font for
> TraceValue columns, so `Ω` (U+03A9) renders via fallback. Good — no change needed there. (XAxis cells pass
> null fallback, but the Freq column has no special glyphs.)

---

## Add 4 — title (top-right, right-aligned)

Summary tables show a default title at the **top-right**, text **right-aligned** (design §2.6). The standard
Table path draws no title (titles are a Rect/complex concern in `AxesRenderer`). Add a summary title draw at the
end of `Draw(...)` when `IsSummaryTable(plot)`:

```csharp
// after DrawResizeHandles(...), still inside the canvas.Save()/Restore() block:
if (IsSummaryTable(plot))
    DrawSummaryTitle(canvas, canvasSize, plot, theme, fs);
```

```csharp
private static void DrawSummaryTitle(
    SKCanvas canvas, (double W, double H) canvasSize, Plot plot, RenderTheme theme, float fs)
{
    string title = SummaryTitle(plot);
    if (string.IsNullOrEmpty(title)) return;

    float titleSize = fs * 1.1f;
    using var font  = new SKFont(SkiaFonts.PlexBold, titleSize);
    using var paint = new SKPaint { Color = theme.TextColor, IsAntialias = true };

    float pad   = fs * 0.5f;
    float rightX = (float)canvasSize.W - pad;        // right edge
    float baseY  = titleSize + pad * 0.5f;            // near the top
    // Right-aligned text.
    canvas.DrawText(title, rightX, baseY, SKTextAlign.Right, font, paint);
}

/// <summary>Default summary-table title: "Max P-{x}dB Power Load" (MXP) / "...Efficiency Load" (MXE),
/// where {x} is the table-wide compression. CustomTitle overrides.</summary>
public static string SummaryTitle(Plot plot)
{
    if (plot.CustomTitleOn && !string.IsNullOrEmpty(plot.CustomTitle)) return plot.CustomTitle;
    string x    = FormatCompressionToken(plot.TableCompression);
    string kind = plot.TableOptimum == TableOptimum.Mxp ? "Power" : "Efficiency";
    return $"Max P-{x}dB {kind} Load";
}

private static string FormatCompressionToken(double value)
{
    string s = value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
    if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
    return s;
}
```

> The title overlaps the top-right header cells visually only if the table is very wide; that's acceptable per
> the design (title sits above/over the top-right). If you prefer, reserve a few px of top margin — but the
> design says top-right over the table, so drawing on top is fine. Keep it simple.

> **Title overlap with header row:** the title is drawn at `baseY ≈ titleSize` (top), which sits in the header
> band. If this looks cramped in practice, the owner can request a dedicated title strip later; for now, top-right
> over the header is the spec. Do not restructure layout for it.

---

## Add 5 — width/copy/hit-test pass-through

- `TotalColumnWidth` and `CalcFitWidth` call `BuildColumns(plot)`, which now returns the summary plan in summary
  mode — they work unchanged (they iterate the returned columns). Verify `CalcFitWidth`'s `FormatColumnCell`
  calls produce summary cells (they will, via the branch above).
- `BuildCopyGrid` uses `BuildLayout`→`FormatColumnCell`, so "Copy Table Data" exports the summary grid for free.
- `HitTest` uses `BuildLayout`/`Columns`; summary columns hit-test as FreqHeader/TraceHeader/FreqCell/DataCell
  normally. Summary traces have no `Markers` (they're not freq-swept network traces), so the MarkerGlyph upgrade
  path simply never fires. No change needed.

### Column widths note
A summary column's width should come from its `SummaryColumnData.ColumnWidth` when set. The simplest wiring: when
the VM (7.5d) creates a summary trace, mirror `SummaryColumn.ColumnWidth` into `trace.ColumnWidth` (the existing
layout reads `trace.ColumnWidth`). Then `BuildLayout`/`TotalColumnWidth` need NO change. **Prefer this** — note
it in the brief for 7.5d. If instead you want the renderer to read `SummaryColumn.ColumnWidth` directly, extend
the width lookups in `BuildLayout`/`TotalColumnWidth`/`CalcFitWidth` to check
`trace.SummaryColumn?.ColumnWidth`. Pick the trace.ColumnWidth mirroring approach to keep this slice small.

---

## Constraints / gotchas
- Non-summary tables MUST be unaffected: the standard `BuildColumns` body stays exactly as-is; only a guarded
  early branch is added.
- A summary table with `plot.SummaryFreqs == null` (cells not yet computed) must render gracefully: empty
  freqs → `RowCount == 0` → header row only, no data rows, no crash. (The VM populates freqs+cells in 7.5d;
  before that, an empty summary table is fine.)
- Glyphs: use `Ω` (U+03A9), `∠` only via the existing DejaVu fallback in TraceValue cells. Keep all Unicode in
  string literals, never in `///` comments.
- `Plot.SummaryFreqs`, `SummaryColumnData.CellsReal/CellsComplex` are derived — never serialized, never cloned.
- TreatWarningsAsErrors: invariant-culture `ToString` on all numeric formatting (avoid CA-style warnings and
  locale drift); no unused locals.

## Tests / verification (owner-run)
No UI unit harness; verify by constructing a summary table in code (or via a tiny scratch path) and rendering:
1. **Renders one row per freq.** Set `plot.PlotType = Table`, `plot.SummaryFreqs = {1.8e9, 1.9e9, 2.0e9}`, add
   two summary traces (e.g. Kind=Metric "Pout" with `CellsReal={36.2,36.0,35.7}`, Kind=Zload with
   `CellsComplex={...}`). Assert the table shows a Freq column (1.8/1.9/2.0 in GHz) + Power column + Zload column
   ("R+jX Ω"), exactly 3 rows.
2. **Title.** With `TableOptimum=Mxp`, `TableCompression=3` → title reads "Max P-3dB Power Load", top-right,
   right-aligned. Switch to `Mxe` → "...Efficiency Load". `TableCompression=1.5` → "Max P-1.5dB ...".
3. **Complex format.** A Zload cell `12.3 - j4.5` renders as "12.30-j4.50 Ω" (2 decimals, Ω suffix).
4. **NaN/empty.** A cell with NaN renders "NaN"; rows beyond cell-array length render blank without crashing.
5. **Non-summary regression.** A normal cube/network Table renders identically to before (no summary traces →
   `IsSummaryTable` false → standard path).
6. **Copy Table Data** on a summary table exports headers + the freq/value grid.
