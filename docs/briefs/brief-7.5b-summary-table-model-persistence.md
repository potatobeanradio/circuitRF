# Brief 7.5b — Summary-table model + persistence (src/Ui)

**Phase:** 7.5 (loadpull summary table). **Layer:** `src/Ui` (model + config only — no rendering, no controls).
**Depends on:** 7.5a (engine accessors — landed), 7.5g (importer cubes — landed). **Design:**
`circuitRF/docs/design/loadpull-summary-table.md` §2.1–2.2b, §6, §7.

Goal: add the data structures and persistence that the renderer (7.5c), header controls (7.5d), and auto-fill
(7.5e) build on. No UI controls and no rendering in this slice — just model fields, a per-column authoring
record, and `.cdd` round-trip. After this slice, a summary table can be constructed in code and saved/loaded;
it won't render or have controls yet (those are 7.5c/7.5d).

**TreatWarningsAsErrors is ON in src/Ui** — no unused privates, nullable-property reads into locals, no `<`/`>`
in XML doc comments.

---

## Add 1 — Table-wide enums + Plot state

### 1a. Enums
Add two enums. Put them in `Plot.cs` (alongside `PlotType`) or a small new file
`src/Ui/DataDisplay/Models/SummaryTableTypes.cs` (preferred — keeps Plot.cs focused):

```csharp
namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>Which optimum load termination a summary Table evaluates every column at.</summary>
    public enum TableOptimum { Mxp, Mxe }

    /// <summary>How a summary Table reads each surface metric at the optimum coordinate.</summary>
    public enum TableReadMode { Interp, Nearest }
}
```

### 1b. Plot fields (table-wide state)
In `<repo>/src/Ui/DataDisplay/Models/Plot.cs`, in the "Table view" region
(near `TableViewAscendingSortOrder`, `TableViewScrollIndex`, `FontSize`), add:

```csharp
// ---- Summary-table state (Phase 7.5) ----------------------------
// Table-wide controls: which optimum, how metrics are read, and the single shared compression.
// Only meaningful when PlotType == Table with summary traces; ignored otherwise.
public TableOptimum  TableOptimum     { get; set; } = TableOptimum.Mxp;
public TableReadMode TableReadMode    { get; set; } = TableReadMode.Interp;
public double        TableCompression { get; set; } = 3.0;
```

These are plain auto-properties (Plot is a plain model, not an ObservableObject). The header controls (7.5d)
will mutate them and push redraws.

---

## Add 2 — `SummaryColumnData` (per-trace authoring record)

A summary Table trace carries a `SummaryColumnData` (analogous to `ContourData` but far lighter). Mirror the
`ContourData` model/clone pattern. New file
`<repo>/src/Ui/DataDisplay/Models/SummaryColumnData.cs`:

```csharp
using System.Numerics;

namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>What a summary-table column reports. Drives how the cell value is computed.</summary>
    public enum SummaryColumnKind
    {
        Metric,         // a surface metric read at the optimum (Pout, DE, Gt, AMPM, IRL, …)
        Zload,          // optimum load impedance ZL (complex, derived from MXP/MXE)
        Zsource,        // per-freq source impedance (complex, read directly)
        Zin,            // input impedance: Zin_real + j·Zin_imag at the optimum (complex)
        OperatingPoint, // per-freq bias scalar (VDD from BiasVLoad, Idq from BiasILoad)
    }

    /// <summary>
    /// Per-trace authoring state for one summary-table column (Phase 7.5). A summary trace
    /// carries this instead of (or alongside) network/cube binding. The frequency anchor column
    /// is implicit (the renderer always emits the freq column); SummaryColumnData describes the
    /// metric/impedance/bias column the user added.
    ///
    /// Compression is NOT stored here — it is a single table-wide value (Plot.TableCompression).
    /// MXP/MXE and Interp/Nearest are also table-wide (Plot.TableOptimum / Plot.TableReadMode).
    /// Persisted in .cdd via SummaryColumnConfig.
    /// </summary>
    public sealed class SummaryColumnData
    {
        /// <summary>What kind of value this column reports.</summary>
        public SummaryColumnKind Kind { get; set; } = SummaryColumnKind.Metric;

        /// <summary>
        /// Canonical metric/cube name for Kind==Metric (e.g. "Pout","DE","Gt","AMPM","IRL")
        /// or the bias cube for Kind==OperatingPoint ("BiasVLoad","BiasILoad").
        /// Ignored for Zload/Zsource/Zin (their source is fixed by Kind).
        /// </summary>
        public string MetricName { get; set; } = "Pout";

        /// <summary>Column header text. Empty ⇒ auto-generate from Kind/MetricName (see SummaryColumns).</summary>
        public string Header { get; set; } = "";

        /// <summary>Display precision for the cell value (real columns). Complex columns use 2-dp R+jX.</summary>
        public int    FractionDigits { get; set; } = 1;

        /// <summary>Per-column width override (0 ⇒ fall back to plot.ColumnWidth).</summary>
        public double ColumnWidth { get; set; } = 0;

        /// <summary>Deep copy for paste (no derived/cached state to drop — all fields are authoring).</summary>
        public SummaryColumnData Clone() => new SummaryColumnData
        {
            Kind           = Kind,
            MetricName     = MetricName,
            Header         = Header,
            FractionDigits = FractionDigits,
            ColumnWidth    = ColumnWidth,
        };
    }
}
```

### 2b. Hang it on Trace
In `<repo>/src/Ui/DataDisplay/Models/Trace.cs`, next to `ContourData`:

```csharp
/// <summary>When non-null, this trace is a summary-table column (Phase 7.5). Mutually exclusive
/// with ContourData; only meaningful on a Table plot.</summary>
public SummaryColumnData? SummaryColumn { get; set; }

public bool IsSummaryColumn => SummaryColumn != null;
```

And in the `Trace(Trace src, ...)` copy constructor, mirror the `ContourData` clone line:
```csharp
SummaryColumn = src.SummaryColumn?.Clone();
```

> Do NOT add summary logic to `BuildPath`/`PathBoundingRect`/autoscale — summary traces produce no geometry
> (like contour traces). The renderer (7.5c) reads `SummaryColumn` + the engine directly. `IsSummaryColumn`
> traces should be treated like `IsContourTrace` wherever geometry is skipped (e.g. they have no Points). For
> THIS slice, just ensure they don't crash autoscale: `PathBoundingRect()` already returns `default` for a
> trace with no points, which is correct (a Table plot's `Autoscale()` returns early anyway).

---

## Add 3 — auto-header helper (shared by renderer + auto-fill)

Add a small static helper so the renderer (7.5c) and auto-fill (7.5e) agree on headers/units. New file
`<repo>/src/Ui/DataDisplay/Models/SummaryColumns.cs`:

```csharp
namespace CircuitRF.Ui.DataDisplay
{
    /// <summary>Header/format helpers for summary-table columns (single source of truth, shared by
    /// the renderer and the auto-fill command). Mirrors the reference generator's column set.</summary>
    public static class SummaryColumns
    {
        /// <summary>Auto header for a column when SummaryColumnData.Header is empty.
        /// freqUnit is needed for the (implicit) Freq column header.</summary>
        public static string AutoHeader(SummaryColumnData col) => col.Kind switch
        {
            SummaryColumnKind.Zload          => "Zload (Ω)",
            SummaryColumnKind.Zsource        => "Zsource (Ω)",
            SummaryColumnKind.Zin            => "Zin (Ω)",
            SummaryColumnKind.OperatingPoint => col.MetricName switch
            {
                "BiasVLoad" => "VDD (V)",
                "BiasILoad" => "Idq (mA)",
                _            => col.MetricName,
            },
            _ /* Metric */   => MetricHeader(col.MetricName),
        };

        public static string MetricHeader(string metric) => metric switch
        {
            "Pout"                          => "Power (dBm)",
            "DE" or "Eff" or "Efficiency"   => "Efficiency (%)",
            "Gt" or "Gain"                  => "Gain (dB)",
            "Gp"                            => "Power Gain (dB)",
            "PAE"                           => "PAE (%)",
            "AMPM"                          => "AM/PM (°)",
            "IRL"                           => "Input Return Loss (dB)",
            _                                => metric,
        };

        /// <summary>True when the column renders a complex R+jX Ω value (vs a real scalar).</summary>
        public static bool IsComplexColumn(SummaryColumnKind kind) =>
            kind is SummaryColumnKind.Zload or SummaryColumnKind.Zsource or SummaryColumnKind.Zin;

        /// <summary>The freq anchor-column header for a given unit, e.g. "Freq (GHz)".</summary>
        public static string FreqHeader(FreqUnit unit) => $"Freq ({unit.Description()})";
    }
}
```

> Use the canonical Ω/° glyphs exactly (U+03A9 Ω, U+00B0 °). Keep them out of XML doc comments (they're in
> string literals here, which is fine). Match the existing `ContourData.MetricUnit`/`MetricDisplayName` style.

---

## Add 4 — persistence (.cdd round-trip)

### 4a. Config records
In `<repo>/src/Ui/DataDisplay/Models/DataDisplayConfig.cs`:

Plot-level — add to `PlotContainerConfig` (near the Table-view settings), with defaults matching `Plot`:
```csharp
// Summary-table state (Phase 7.5). Defaults match Plot so a non-summary Table round-trips unchanged.
[JsonConverter(typeof(JsonStringEnumConverter))]
public TableOptimum  TableOptimum     { get; set; } = TableOptimum.Mxp;
[JsonConverter(typeof(JsonStringEnumConverter))]
public TableReadMode TableReadMode    { get; set; } = TableReadMode.Interp;
public double        TableCompression { get; set; } = 3.0;
```

Per-trace — add a `SummaryColumnConfig` record (mirror `ContourTraceConfig`'s shape/placement) and a nullable
field on `TraceConfig`:
```csharp
/// <summary>Persisted authoring state for one summary-table column (Phase 7.5).
/// When present, the trace is a summary column; standard network/cube fields are ignored.</summary>
public sealed class SummaryColumnConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SummaryColumnKind Kind { get; set; } = SummaryColumnKind.Metric;
    public string MetricName     { get; set; } = "Pout";
    public string Header         { get; set; } = "";
    public int    FractionDigits { get; set; } = 1;
    public double ColumnWidth    { get; set; } = 0;
}
```
And on `TraceConfig`, next to `ContourTrace`:
```csharp
/// <summary>Non-null when this trace is a summary-table column (7.5). Mutually exclusive with ContourTrace.</summary>
public SummaryColumnConfig? SummaryColumn { get; set; }
```

### 4b. Build side (save + copy)
In `<repo>/src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs`:

`BuildPlotContainerConfig` — add the three plot-level fields to the `PlotContainerConfig` initializer:
```csharp
TableOptimum     = plot.TableOptimum,
TableReadMode    = plot.TableReadMode,
TableCompression = plot.TableCompression,
```

`BuildTraceConfig` — after the `if (t.ContourData is { } cd) { ... }` block, add the summary block:
```csharp
if (t.SummaryColumn is { } sc)
{
    tc.SummaryColumn = new SummaryColumnConfig
    {
        Kind           = sc.Kind,
        MetricName     = sc.MetricName,
        Header         = sc.Header,
        FractionDigits = sc.FractionDigits,
        ColumnWidth    = sc.ColumnWidth,
    };
}
```

### 4c. Load side
In `LoadPlotContainerConfigAsync`:

Plot-level — where the other `plot.*` fields are restored from `pc` (near `plot.TableViewScrollIndex = ...`):
```csharp
plot.TableOptimum     = pc.TableOptimum;
plot.TableReadMode    = pc.TableReadMode;
plot.TableCompression = pc.TableCompression > 0 ? pc.TableCompression : 3.0;
```

Per-trace — in the trace-build loop, handle the summary case. It mirrors the contour branch: a summary trace
needs a library entry (its source DataSet) but no SNP/cube binding. Add a branch BEFORE the
`isCubeBound`/network branches (parallel to `isContourTrace`):
```csharp
bool isSummaryTrace = traceConfig.SummaryColumn is not null;
// ... in the gating section, allow summary like contour:
if (isSummaryTrace && libEntry is null) continue;   // needs a source entry
// ... construction:
if (isSummaryTrace)
{
    var sc = traceConfig.SummaryColumn!;
    var placeholder = new SNP(new double[] { 1e9 }, 1);
    trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db, false);
    trace.SummaryColumn = new SummaryColumnData
    {
        Kind           = sc.Kind,
        MetricName     = sc.MetricName,
        Header         = sc.Header,
        FractionDigits = sc.FractionDigits,
        ColumnWidth    = sc.ColumnWidth,
    };
    ApplyProperties(traceConfig.Properties, trace.Properties);
    trace.SourceRef  = sref;
    trace.SourcePath = resolvedPath;
    plot.Traces.Add(trace);
    continue;
}
```
> Match the EXACT structure of the existing `isContourTrace` branch in this method (the placeholder SNP,
> `ApplyProperties`, `SourceRef`/`SourcePath`, `plot.Traces.Add(trace); continue;`). Wire `isSummaryTrace` into
> the same gating block that currently sets `isCubeBound`/`isContourTrace` so a summary trace isn't rejected by
> the network/cube `continue` guards.

### 4d. format_version
No bump needed: per alpha no-back-compat, adding nullable/defaulted fields is safe; loaders still reject only
on `format_version` mismatch. `SummaryColumn` defaults to null (old files = no summary columns); the three
plot-level fields default to their `Plot` defaults. A pre-7.5 `.cdd` loads as a normal Table unchanged.

---

## Constraints / gotchas
- Mutual exclusivity: a trace has at most one of `ContourData` / `SummaryColumn` / cube / network binding.
  Don't set both. `IsSummaryColumn` and `IsContourTrace` must never both be true.
- `Plot` is a plain model (no INotify); header controls in 7.5d own change notification.
- TreatWarningsAsErrors: read nullable properties into locals before use; no unused fields; keep `Ω`/`°`/`Γ`
  out of `///` XML doc comments (string literals are fine).
- Don't touch the renderer or trace card in this slice — those are 7.5c / 7.5d. This slice must compile and
  round-trip but produces no visible change yet (a summary trace added in code will persist and reload).
- `FreqUnit.Description()` and `ContourData`'s metric-name patterns already exist — reuse, don't duplicate.

## Tests / verification (owner-run)
No unit-test harness for UI models typically; verify by round-trip:
1. **Compiles** with TreatWarningsAsErrors on.
2. **Round-trip (manual or scripted):** construct a Table `Plot`, add a `Trace` with a `SummaryColumnData`
   (Kind=Metric, MetricName="Pout"), set `plot.TableOptimum=Mxe`, `plot.TableCompression=1.0`; build a
   `PlotContainerConfig` via `BuildPlotContainerConfig`; serialize+deserialize; load via
   `LoadPlotContainerConfigAsync`; assert the reloaded plot has the same `TableOptimum`/`TableReadMode`/
   `TableCompression` and a trace whose `SummaryColumn` matches (Kind, MetricName, Header, FractionDigits).
3. **Back-compat:** a pre-7.5 `.cdd` (no summary fields) loads as a normal Table — TableOptimum=Mxp,
   TableReadMode=Interp, TableCompression=3.0, no summary traces, no exceptions.
4. **Copy/paste:** a summary trace's `SummaryColumn` deep-copies (Clone) on paste (the copy ctor line).
