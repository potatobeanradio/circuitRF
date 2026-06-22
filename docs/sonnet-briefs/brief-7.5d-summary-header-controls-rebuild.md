# Brief 7.5d — Summary-table header controls, card trimming, and live-data RebuildSummary (src/Ui)

**Phase:** 7.5 (loadpull summary table). **Layer:** `src/Ui` ViewModels (+ minimal AXAML). **Depends on:**
7.5a (engine accessors — landed), 7.5b (model/persistence — landed), 7.5c (renderer — landed), 7.5g (importer
cubes — landed). **Design:** `circuitRF/docs/design/loadpull-summary-table.md` §2.1–2.4, §2.7, §3, §4.

Goal: make a summary Table come alive — add the table-wide header controls (MXP/MXE, Interp/Nearest,
compression), relabel "+ Contour" → "+ Summary" and add summary traces, trim the trace card for summary
columns, and (the keystone) implement **`RebuildSummary`** in `PlotInspectorViewModel` that builds one
`LoadpullSurface`, computes `Plot.SummaryFreqs`, and fills every summary column's `CellsReal`/`CellsComplex`
via the 7.5a accessors. This is the live-data wiring the 7.5c renderer draws from.

Files:
- `<repo>/src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs` (controls,
  AddSummaryTrace, RebuildSummary).
- `<repo>/src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs` (card-trimming
  visibility flags + compression-disable).
- `<repo>/src/Ui/Views/DataDisplay/PlotInspectorView.axaml` (header controls +
  relabel binding). **Confirm the exact AXAML path** (search `**/PlotInspectorView.axaml`); wire bindings to the
  new VM members. AXAML specifics are light here — match the existing FreqUnit combo + AddContourTrace button.

**TreatWarningsAsErrors is ON.** No unused privates; nullable property reads into locals; no `<`/`>` in `///`.

---

## Part 1 — Header controls (PlotInspectorViewModel)

The header already exposes `PlotType` set-commands and a `FreqUnit` combo (`[ObservableProperty] _freqUnit`
with `OnFreqUnitChanged` mutating `_plot.FreqUnits`). Add three table-wide controls, visible only when
`IsTablePlot` AND the table is a summary table. They sit LEFT of the FreqUnit control (design §2.1–2.2b).

### 1a. Visibility flag
```csharp
/// <summary>True when this Table contains summary columns — gates the summary header controls.</summary>
public bool IsSummaryTable => _plot.PlotType == PlotType.Table
    && _plot.Traces.Any(t => t.IsSummaryColumn);
```
Raise `OnPropertyChanged(nameof(IsSummaryTable))` wherever summary traces are added/removed (AddSummaryTrace,
RemoveTrace, RebuildTraces, OnLibraryChanged) and on plot-type change (it's already in `OnPlotTypeChanged`'s
notify list — add it there).

### 1b. Optimum (MXP/MXE) selector
```csharp
[ObservableProperty] private TableOptimum _tableOptimum;

partial void OnTableOptimumChanged(TableOptimum value)
{
    _plot.TableOptimum = value;
    RebuildSummary();
}
```
Initialize `_tableOptimum = plot.TableOptimum;` in the ctor (next to `_freqUnit = plot.FreqUnits;`).
Static items source for the combo:
```csharp
public static IReadOnlyList<TableOptimum> AllTableOptima { get; } = Enum.GetValues<TableOptimum>().ToList();
```

### 1c. Read mode (Interp/Nearest) selector
```csharp
[ObservableProperty] private TableReadMode _tableReadMode;

partial void OnTableReadModeChanged(TableReadMode value)
{
    _plot.TableReadMode = value;
    RebuildSummary();
}
```
Init `_tableReadMode = plot.TableReadMode;`. Items:
```csharp
public static IReadOnlyList<TableReadMode> AllTableReadModes { get; } = Enum.GetValues<TableReadMode>().ToList();
```

### 1d. Table-wide compression
```csharp
public double TableCompression
{
    get => _plot.TableCompression;
    set
    {
        if (Math.Abs(_plot.TableCompression - value) < 1e-9) return;
        _plot.TableCompression = value;
        OnPropertyChanged();
        RebuildSummary();   // recompute every column AND the title's {x}
    }
}
```
(A plain property, not `[ObservableProperty]`, because it wraps `_plot` directly — same style as `FontSize`.)
Per design §2.2b: changing this updates every column at once; the per-trace compression field is
disabled/greyed (Part 3). A numeric entry (NumericUpDown) or small combo of common values (0.1, 0.5, 1, 2, 3…)
is fine — match whatever the contour `ConstraintValue` box uses for consistency.

> AXAML: place these three controls in the header row, left of the FreqUnit combo, wrapped in a panel bound to
> `IsSummaryTable` for visibility. Labels: "Optimum" (MXP/MXE), "Read" (Interp/Nearest), "Compression" (dB).

---

## Part 2 — "+ Summary" trace add (relabel + AddSummaryTrace)

### 2a. Relabel the add button
On a Table loadpull source, the "+ Contour" affordance becomes "+ Summary". Add:
```csharp
/// <summary>Label for the loadpull add-trace button: "+ Summary" on a Table, "+ Contour" otherwise.</summary>
public string AddLoadpullTraceLabel => _plot.PlotType == PlotType.Table ? "+ Summary" : "+ Contour";

/// <summary>True when the loadpull add button should add a summary column (Table) vs a contour (Smith/Polar/Rect).</summary>
public bool IsSummaryAddMode => _plot.PlotType == PlotType.Table;
```
Raise both in `OnPlotTypeChanged`'s notify list. Bind the button's content to `AddLoadpullTraceLabel`.

### 2b. Route the command
Keep one command but branch on plot type, OR add a second command. Simplest: branch inside a wrapper so the
existing `AddContourTraceCommand` binding can stay, and add a new `AddSummaryTraceCommand`:
```csharp
public IRelayCommand AddSummaryTraceCommand { get; }
// in ctor:
AddSummaryTraceCommand = new RelayCommand(AddSummaryTrace, () => CanAddSummaryTrace);
```
`CanAddSummaryTrace` mirrors `CanAddContourTrace` but requires a Table:
```csharp
public bool CanAddSummaryTrace =>
    _plot.PlotType == PlotType.Table && _library?.SelectedEntry is { } e && IsLoadpullSource(e);
```
Refresh it alongside the others in `RefreshAddCommand()`:
```csharp
OnPropertyChanged(nameof(CanAddSummaryTrace));
((RelayCommand)AddSummaryTraceCommand).NotifyCanExecuteChanged();
OnPropertyChanged(nameof(AddLoadpullTraceLabel));
OnPropertyChanged(nameof(IsSummaryTable));
```
> AXAML: bind the loadpull-add button to `AddSummaryTraceCommand` when `IsSummaryAddMode`, else
> `AddContourTraceCommand`. If the view uses one button, a `MultiBinding`/converter is overkill — simplest is
> two buttons, each gated by `IsSummaryAddMode` / `!IsSummaryAddMode` visibility. Or keep one button bound to a
> dispatcher command that calls the right method. Pick the lightest option that compiles.

### 2c. AddSummaryTrace
Mirror `AddContourTrace` exactly (placeholder SNP, SourceRef/SourcePath, attach data object, add to plot +
Traces, notify). Default metric Pout (design §2.4).
```csharp
private void AddSummaryTrace()
{
    var entry = _library?.SelectedEntry;
    if (entry?.Data is null) return;

    var placeholder = new SNP(new double[] { 1e9 }, 1);
    var trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db);
    trace.SourceRef  = DataSourceRef.Selected;
    trace.SourcePath = _library!.SelectedDataSourceAbs;

    trace.SummaryColumn = new SummaryColumnData
    {
        Kind           = SummaryColumnKind.Metric,
        MetricName     = "Pout",
        FractionDigits = 1,
    };
    // 7.5c note: mirror column width into trace.ColumnWidth so the renderer's layout (which reads
    // trace.ColumnWidth) sizes the column without special-casing. 0 → falls back to plot.ColumnWidth.
    trace.ColumnWidth = trace.SummaryColumn.ColumnWidth > 0
        ? trace.SummaryColumn.ColumnWidth
        : _plot.ColumnWidth;

    _plot.Traces.Add(trace);
    Traces.Add(new TraceRowViewModel(trace, this));
    RebuildSummary();          // compute freqs + this column's cells (and any existing columns)
    RefreshAddCommand();
    OnPropertyChanged(nameof(IsSummaryTable));
    PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    PlotStructureChanged?.Invoke(this, EventArgs.Empty);
}
```

---

## Part 3 — Card trimming for summary columns (TraceRowViewModel)

A summary trace's card is heavily trimmed (design §2.7): keep the metric selector + units/format; the
compression field is shown DISABLED; REMOVE Levels, Show Max Power/Efficiency, Fill, Lines row, Grids row,
Labels row. Drive this with visibility flags mirroring `IsContourTrace`/`IsStandardTrace`.

### 3a. Discriminator flags
In `TraceRowViewModel`, near `IsContourTrace`/`IsStandardTrace`:
```csharp
/// <summary>True when this row is a summary-table column (Phase 7.5).</summary>
public bool IsSummaryColumn => _trace.IsSummaryColumn;

/// <summary>A "standard" trace is neither contour nor summary (full line/marker/table card body).</summary>
public bool IsStandardTrace => !IsContourTrace && !IsSummaryColumn;   // UPDATE existing definition
```
> The existing `IsStandardTrace => !IsContourTrace` must be widened to also exclude summary columns, so the
> standard line/marker body hides for summary rows. Add `OnPropertyChanged(nameof(IsSummaryColumn))` to
> `RefreshDescription()`'s notify list (next to `IsContourTrace`/`IsStandardTrace`).

### 3b. Summary metric selector
Summary columns need a metric dropdown (Pout, DE, Gt, AMPM, IRL, Zload, Zsource, Zin, VDD, Idq…). Reuse the
metric-list machinery: a summary column's metric maps to either a `SummaryColumnKind` (for Z*/bias) or a
`Metric` name. Expose:
```csharp
// Summary column authoring (Phase 7.5d).
public ObservableCollection<string> SummaryMetricOptions { get; } = new();

[ObservableProperty] private string? _summaryMetricSelection;

partial void OnSummaryMetricSelectionChanged(string? value)
{
    if (_suppressSummaryCallback || _trace.SummaryColumn is not { } sc || value is null) return;
    ApplySummaryMetric(sc, value);
    _parent.RebuildSummary();   // re-derive this column's cells
}
```
`ApplySummaryMetric` maps the display string to `Kind`+`MetricName`:
```csharp
private void ApplySummaryMetric(SummaryColumnData sc, string selection)
{
    switch (selection)
    {
        case "Zload":   sc.Kind = SummaryColumnKind.Zload;   sc.MetricName = ""; break;
        case "Zsource": sc.Kind = SummaryColumnKind.Zsource; sc.MetricName = ""; break;
        case "Zin":     sc.Kind = SummaryColumnKind.Zin;     sc.MetricName = ""; break;
        case "VDD":     sc.Kind = SummaryColumnKind.OperatingPoint; sc.MetricName = "BiasVLoad"; break;
        case "Idq":     sc.Kind = SummaryColumnKind.OperatingPoint; sc.MetricName = "BiasILoad"; break;
        default:        sc.Kind = SummaryColumnKind.Metric;  sc.MetricName = selection; break;
    }
    sc.Header = "";   // re-auto-generate header from the new kind/metric
}
```
Populate `SummaryMetricOptions` when the row is a summary column. The available surface metrics come from the
same source as the contour metric list — build the surface and read its cube names. Simplest: reuse
`EnsureLoadpullSurface()` (already builds/caches the surface for this row) + `RebuildMetricList()` (already
fills `AvailableMetrics`), then compose the summary options = standard set (Pout, DE, Gt, AMPM, IRL) ∩ available
+ the always-available Zload + presence-gated Zsource/Zin/VDD/Idq. Keep it pragmatic:
```csharp
private void RebuildSummaryMetricOptions()
{
    SummaryMetricOptions.Clear();
    // Surface metrics actually present (AvailableMetrics is filled by RebuildMetricList()).
    foreach (var m in new[] { "Pout", "DE", "Gt", "AMPM", "IRL" })
        if (AvailableMetrics.Contains(m)) SummaryMetricOptions.Add(m);
    SummaryMetricOptions.Add("Zload");                       // always (derived from optimum)
    if (AvailableMetrics.Contains("Zin_real")) SummaryMetricOptions.Add("Zin");
    // Zsource / VDD / Idq presence is engine-checked at rebuild; offer them and let cells be NaN if absent.
    SummaryMetricOptions.Add("Zsource");
    SummaryMetricOptions.Add("VDD");
    SummaryMetricOptions.Add("Idq");
    // Sync selection to the column's current kind/metric.
    _suppressSummaryCallback = true;
    SummaryMetricSelection = SummaryMetricForColumn(_trace.SummaryColumn);
    _suppressSummaryCallback = false;
}

private static string? SummaryMetricForColumn(SummaryColumnData? sc) => sc?.Kind switch
{
    null                              => null,
    SummaryColumnKind.Zload           => "Zload",
    SummaryColumnKind.Zsource         => "Zsource",
    SummaryColumnKind.Zin             => "Zin",
    SummaryColumnKind.OperatingPoint  => sc.MetricName == "BiasILoad" ? "Idq" : "VDD",
    _                                 => sc.MetricName,
};
```
Add a `private bool _suppressSummaryCallback;` field. Call `RebuildSummaryMetricOptions()` from the ctor when
`_trace.IsSummaryColumn` (mirroring how the contour branch calls its setup), and after
`EnsureLoadpullSurface()` succeeds.

### 3c. Disabled compression field
The card shows a compression value box for summary columns but DISABLED (driven by the table-wide control).
Expose a read-only display + an `IsEnabled=false` binding:
```csharp
/// <summary>The table-wide compression shown (disabled) on a summary column card.</summary>
public double SummaryCompressionDisplay => _parent.TableCompression;
/// <summary>Always false — the per-column compression box is greyed; compression is table-wide.</summary>
public bool SummaryCompressionEditable => false;
```
Raise `OnPropertyChanged(nameof(SummaryCompressionDisplay))` when the table compression changes — easiest is to
have `PlotInspectorViewModel.TableCompression`'s setter call a small `NotifySummaryColumnsCompression()` that
loops `Traces` raising it. (Or have RebuildSummary raise it.)

> AXAML (trace card): wrap the existing standard/contour body and add a summary body shown when
> `IsSummaryColumn`. Summary body = metric combo (`SummaryMetricOptions`/`SummaryMetricSelection`) + format
> digits + a disabled compression box (`SummaryCompressionDisplay`, `IsEnabled="{Binding
> SummaryCompressionEditable}"`). Hide the Lines/Grids/Labels/Levels/Fill rows (they're already gated on
> contour/standard; ensure they're also hidden for `IsSummaryColumn`). Match the existing card structure; this
> is mechanical AXAML.

---

## Part 4 — RebuildSummary (the keystone, PlotInspectorViewModel)

`RebuildSummary` builds one `LoadpullSurface` from the selected entry, computes the per-freq optimum once, and
fills every summary column's derived cells. This is the live-data source the renderer draws. It mirrors
`TraceRowViewModel.RebuildContour` but at plot scope and writing into `SummaryColumnData`.

```csharp
/// <summary>
/// Recomputes the summary table's derived state: Plot.SummaryFreqs and each summary column's
/// CellsReal/CellsComplex, read at the per-frequency MXP/MXE optimum using the table-wide
/// compression and read mode. No-op when the plot is not a summary table. (Phase 7.5d.)
/// </summary>
public void RebuildSummary()
{
    var summaryTraces = _plot.Traces.Where(t => t.IsSummaryColumn).ToList();
    if (summaryTraces.Count == 0)
    {
        _plot.SummaryFreqs = null;
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        return;
    }

    // Build the surface from the selected entry (summary columns share one source).
    var entry = _library?.SelectedEntry;
    if (entry?.Data is not { } ds)
    {
        _plot.SummaryFreqs = null;
        ClearSummaryCells(summaryTraces);
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        return;
    }

    LoadpullSurface surface;
    try { surface = new LoadpullSurface(ds); }
    catch
    {
        _plot.SummaryFreqs = null;
        ClearSummaryCells(summaryTraces);
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        return;
    }

    int nFreq = surface.Frequencies.Count;
    var freqs = new double[nFreq];
    for (int i = 0; i < nFreq; i++) freqs[i] = surface.Frequencies[i];
    _plot.SummaryFreqs = freqs;

    // Table-wide constraint + plane. Summary tables are Z-plane (impedance) by convention; the
    // optimum coordinate is read in the same plane the metrics are fit in. Use Z-plane (design §3).
    var constraint = ConstraintSpec.AtCompression(_plot.TableCompression);
    var plane      = SurfacePlane.Z;
    bool nearest   = _plot.TableReadMode == TableReadMode.Nearest;

    // Per-freq optimum coordinate (MXP or MXE), computed once and shared by all metric columns.
    var optima = new System.Numerics.Complex?[nFreq];
    for (int fi = 0; fi < nFreq; fi++)
    {
        var mxx = _plot.TableOptimum == TableOptimum.Mxp
            ? surface.MaxPower(fi, constraint, plane)
            : surface.MaxEfficiency(fi, constraint, plane);
        // Interp mode → interpolated optimum; Nearest → measured optimum.
        optima[fi] = mxx is null ? (System.Numerics.Complex?)null
                   : (nearest ? mxx.Measured : mxx.Interpolated);
    }

    // Fill each column's cells.
    foreach (var t in summaryTraces)
    {
        var sc = t.SummaryColumn!;
        if (SummaryColumns.IsComplexColumn(sc.Kind))
            sc.CellsComplex = ComputeComplexColumn(surface, sc, optima, freqs, constraint, plane, nearest);
        else
            sc.CellsReal = ComputeRealColumn(surface, sc, optima, constraint, plane, nearest);
        // Keep the renderer's width path fed (7.5c note).
        t.ColumnWidth = sc.ColumnWidth > 0 ? sc.ColumnWidth : _plot.ColumnWidth;
    }

    NotifySummaryColumnsCompression();
    PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
}

private static void ClearSummaryCells(IEnumerable<Trace> summaryTraces)
{
    foreach (var t in summaryTraces)
    {
        if (t.SummaryColumn is not { } sc) continue;
        sc.CellsReal = null;
        sc.CellsComplex = null;
    }
}

private void NotifySummaryColumnsCompression()
{
    foreach (var vm in Traces)
        vm.RaiseSummaryCompressionChanged();   // small helper raising SummaryCompressionDisplay
}
```

### 4a. Per-column compute helpers
```csharp
private static double[] ComputeRealColumn(
    LoadpullSurface surface, SummaryColumnData sc,
    System.Numerics.Complex?[] optima, ConstraintSpec constraint, SurfacePlane plane, bool nearest)
{
    int n = optima.Length;
    var cells = new double[n];
    for (int fi = 0; fi < n; fi++)
    {
        if (sc.Kind == SummaryColumnKind.OperatingPoint)
        {
            // Bias scalar read directly per freq (ignores Interp/Nearest; design §2.4/decision 5).
            double? v = surface.OperatingPoint(fi, sc.MetricName);
            // Idq (BiasILoad) is stored in Amps → display in mA.
            cells[fi] = v is null ? double.NaN
                      : (sc.MetricName == "BiasILoad" ? v.Value * 1000.0 : v.Value);
        }
        else // Metric surface read at the optimum
        {
            if (optima[fi] is not { } coord) { cells[fi] = double.NaN; continue; }
            cells[fi] = surface.MetricAtCoord(fi, sc.MetricName, coord, constraint, plane, nearest: nearest);
        }
    }
    return cells;
}

private static System.Numerics.Complex[] ComputeComplexColumn(
    LoadpullSurface surface, SummaryColumnData sc,
    System.Numerics.Complex?[] optima, double[] freqs, ConstraintSpec constraint, SurfacePlane plane, bool nearest)
{
    int n = optima.Length;
    var cells = new System.Numerics.Complex[n];
    var nan   = new System.Numerics.Complex(double.NaN, double.NaN);
    for (int fi = 0; fi < n; fi++)
    {
        switch (sc.Kind)
        {
            case SummaryColumnKind.Zsource:
                cells[fi] = surface.SourceZ(fi) ?? nan;     // per-freq, ignores optimum/read mode
                break;

            case SummaryColumnKind.Zload:
                // The optimum load impedance. In Z-plane the optimum coord IS the load impedance.
                cells[fi] = optima[fi] ?? nan;
                break;

            case SummaryColumnKind.Zin:
                // Interpolate Zin_real and Zin_imag SEPARATELY at the optimum, then combine (design §3).
                if (optima[fi] is { } c)
                {
                    double re = surface.MetricAtCoord(fi, "Zin_real", c, constraint, plane, nearest: nearest);
                    double im = surface.MetricAtCoord(fi, "Zin_imag", c, constraint, plane, nearest: nearest);
                    cells[fi] = (double.IsNaN(re) || double.IsNaN(im)) ? nan
                              : new System.Numerics.Complex(re, im);
                }
                else cells[fi] = nan;
                break;

            default:
                cells[fi] = nan;
                break;
        }
    }
    return cells;
}
```

> **Zload semantics:** in `SurfacePlane.Z` the optimum coordinate returned by MaxPower/MaxEfficiency is already
> the load impedance (Ω). If the design later wants Γ-plane optima converted via G2Z·z0, that's a Z-vs-Γ plane
> decision — for this slice we fit in Z-plane (design §3 reads impedance columns directly), so the coord is ZL.
> If `RecommendedBox`/fit semantics turn out to need Γ-plane, revisit; the engine accessors support both.

### 4b. Trigger RebuildSummary on the right events
- AddSummaryTrace (Part 2c) — done.
- Header control changes (Part 1b/1c/1d) — done.
- FreqUnit change: the freq COLUMN re-labels and re-scales, but cells don't change. `OnFreqUnitChanged`
  already calls `RebuildAndNotify`; add `RebuildSummary()` (cheap; also covers nothing-changed). Actually freqs
  are stored in Hz and scaled at draw, so a freq-unit change needs only a redraw — but calling RebuildSummary
  is harmless and keeps it simple.
- Library change (`OnLibraryChanged`) and `RebuildAndNotify`: call `RebuildSummary()` at the end so the table
  refreshes when the source reloads (mirrors how contour rebuilds via the row VM there). Add a guarded call:
  `if (IsSummaryTable) RebuildSummary();`
- Plot-type change to Table with existing summary traces (rare): `OnPlotTypeChanged` → if now a summary table,
  `RebuildSummary()`.

### 4c. Initial build on load
When a `.cdd` with summary traces loads, `DataDisplayViewModel` builds the traces (7.5b load branch) but the
inspector's `RebuildSummary` must run once after the library data is present so cells populate. The inspector
ctor runs `RebuildTraces()`; add at the end of the ctor: `if (IsSummaryTable) RebuildSummary();`. Also ensure
`OnLibraryChanged` (which fires when data finishes resolving) calls it — covered by 4b.

---

## Part 5 — small helper on TraceRowViewModel
```csharp
/// <summary>Raises the disabled-compression display so a summary card reflects the table-wide value.</summary>
internal void RaiseSummaryCompressionChanged()
{
    if (_trace.IsSummaryColumn)
        OnPropertyChanged(nameof(SummaryCompressionDisplay));
}
```

---

## Constraints / gotchas
- `_plot.SummaryFreqs`, `SummaryColumnData.CellsReal/CellsComplex` are derived — never serialized/cloned
  (already enforced in 7.5b/7.5c). RebuildSummary fully repopulates them each call.
- Presence-gating: absent cube → engine accessor returns NaN/null → cell renders "NaN"/blank. Never throw.
- One surface per RebuildSummary call is acceptable (matches contour cost); the surface fit-cache makes the
  per-column `MetricAtCoord` calls cheap (same constraint/plane reused).
- TreatWarningsAsErrors: read nullable props into locals; `System.Numerics.Complex` fully-qualified or `using`;
  invariant-culture formatting is the renderer's job (already done in 7.5c), not here.
- Do NOT recompute summary cells inside the renderer — that's this VM's job. The renderer only reads.
- Keep AXAML changes minimal and mechanical; the logic lives in the VMs. Confirm the view filename first.

## Tests / verification (owner-run)
1. **Add summary column.** On a Table loadpull source, "+ Summary" adds a Pout column; the table shows one row
   per freq with Power (dBm) values, plus the Freq column. Title "Max P-3dB Power Load" top-right.
2. **Optimum toggle.** MXP→MXE recomputes every column (values change to the efficiency-optimum load); title
   switches to "...Efficiency Load".
3. **Read mode.** Interp→Nearest changes metric values to nearest measured-node values; impedance/bias columns
   (Zsource/VDD/Idq) are unchanged (they ignore read mode).
4. **Compression.** Changing the table-wide compression recomputes all columns and updates the title's {x};
   per-column compression boxes are greyed and track the table value.
5. **Column kinds.** Add Zload, Zsource, Zin, VDD, Idq columns; Zload/Zsource/Zin render "R+jX Ω", VDD in V,
   Idq in mA. Absent backing cube → "NaN" cells, no crash.
6. **Persistence.** Save + reload a summary table; columns, optimum, read mode, and compression round-trip
   (7.5b) and cells repopulate via RebuildSummary on load.
7. **Card trimming.** A summary column's card shows only the metric selector + format + a greyed compression
   box; no Lines/Grids/Labels/Levels/Fill rows.
8. **Regression.** Contour traces on Smith/Polar/Rect and normal cube/network Tables are unaffected.
