# Brief 7.5e — Auto-fill standard column set + alias display-name registration (src/Ui)

**Phase:** 7.5 (loadpull summary table). **Layer:** `src/Ui` ViewModel (+ a tiny AXAML button). **Depends on:**
7.5b (model), 7.5c (renderer), 7.5d (header controls, AddSummaryTrace, RebuildSummary) — all landed. **Design:**
`circuitRF/docs/design/loadpull-summary-table.md` §2.4, §4, §8.

Goal: one-click **auto-fill** of the standard performance column set (design §4), presence-gated (absent backing
cube → column silently skipped), sharing one compression + one optimum (the table's current selectors). Plus
confirm the alias display-name headers resolve (most already do via `SummaryColumns.AutoHeader`/`MetricHeader`
from 7.5b — this slice verifies coverage and fills any gaps).

File: `<repo>/src/Ui/DataDisplay/ViewModels/PlotInspectorViewModel.cs` (auto-fill
command + helpers) and a button in the Table Properties header AXAML.

**TreatWarningsAsErrors is ON.** No unused privates; nullable property reads into locals; no `<`/`>` in `///`.

---

## Part 1 — the standard column set (design §4, authoritative order)

Auto-fill adds these columns IN THIS ORDER, skipping any whose backing cube is absent:

| # | Kind / Metric                         | Backing cube(s)            | Presence |
|---|---------------------------------------|----------------------------|----------|
| 1 | (Freq anchor — emitted by renderer)   | dataset freq axis          | implicit, not a trace |
| 2 | OperatingPoint "BiasVLoad" (VDD)      | `BiasVLoad`                | gated |
| 3 | OperatingPoint "BiasILoad" (Idq)      | `BiasILoad`                | gated |
| 4 | Zsource                               | `ZSource`                  | gated |
| 5 | Zin                                   | `Zin_real` AND `Zin_imag`  | gated (both) |
| 6 | Zload                                 | `ZLoad` (core)             | always |
| 7 | Metric "Pout" (Power dBm)             | `Pout` (core)              | always |
| 8 | Metric "DE" (Efficiency %)            | `DE` (core)                | always |
| 9 | Metric "Gt" (Gain dB)                 | `Gt` (core)                | always |
| 10| Metric "AMPM" (AM/PM °)               | `AMPM`                     | gated |
| 11| Metric "IRL" (Input Return Loss dB)   | `IRL`                      | gated |

> The Freq column is the renderer's implicit anchor (7.5c `BuildSummaryColumns` always prepends it) — auto-fill
> does NOT add a trace for it. Columns 6–9 derive from core loadpull cubes and are effectively always present for
> a valid loadpull dataset; still, gate them too (a malformed dataset missing `Pout` should skip, not crash).

FractionDigits per design §4: real metrics `F1` (1 digit); impedance columns use the renderer's 2-dp R+jX
(FractionDigits irrelevant for complex). VDD `F1`, Idq `F1` (the design notes "int if >10" but `F1` is fine and
simpler; keep `F1` for consistency unless the owner asks otherwise).

---

## Part 2 — presence detection

Presence is a cube-existence check against the selected entry's DataSet, using the same group-aware lookup the
rest of the VM uses. Add a helper:

```csharp
/// <summary>True when a cube of the given canonical name exists in any group of the dataset.</summary>
private static bool HasCube(DataSet ds, string name) =>
    ds.Groups.Any(g => ds.CubesIn(g).ContainsKey(name));
```
> `ds.CubesIn(group)` returns bare names (the codebase uses this pattern in `IsLoadpullSource`,
> `FirstPlottableCubeName`, and `RebuildMetricList`). The loadpull importer writes `Pout`/`DE`/`Gt`/`ZLoad`/
> `BiasVLoad`/`BiasILoad`/`ZSource`/`Zin_real`/`Zin_imag`/`AMPM`/`IRL` as bare cube names in the default group,
> so a bare-name check across groups is correct. If a future grouped layout appears, this still finds them.

---

## Part 3 — the auto-fill command

Mirror `AddSummaryTrace` for each column, but build the full set in one pass. Per design §2.4 "auto-fills the
standard performance columns in one action" — interpret as: **replace** any existing summary columns with the
standard set (one-click standard table). If the owner prefers append, that's a one-line change; replace is the
cleaner default for a "fill standard set" button.

```csharp
public IRelayCommand AutoFillSummaryCommand { get; }
// in ctor:
AutoFillSummaryCommand = new RelayCommand(AutoFillSummary, () => CanAddSummaryTrace);
// in RefreshAddCommand():
OnPropertyChanged(nameof(CanAutoFillSummary));
((RelayCommand)AutoFillSummaryCommand).NotifyCanExecuteChanged();
```
`CanAutoFillSummary` = `CanAddSummaryTrace` (Table + loadpull source). Expose it as a property for the button's
enable state:
```csharp
public bool CanAutoFillSummary => CanAddSummaryTrace;
```

```csharp
/// <summary>
/// Replaces the table's summary columns with the standard performance set (design §4), in order,
/// presence-gated against the dataset. One shared compression + one optimum (the table's current
/// selectors). Columns whose backing cube is absent are silently skipped. (Phase 7.5e.)
/// </summary>
private void AutoFillSummary()
{
    var entry = _library?.SelectedEntry;
    if (entry?.Data is not { } ds) return;

    // Remove existing summary traces first (replace semantics).
    var existing = Traces.Where(vm => vm.Trace.IsSummaryColumn).ToList();
    foreach (var vm in existing)
    {
        vm.UnsubscribeFromLibrary();
        _plot.Traces.Remove(vm.Trace);
        Traces.Remove(vm);
    }

    // Build the standard set in order, presence-gated.
    void AddCol(SummaryColumnKind kind, string metricName, bool present)
    {
        if (!present) return;
        var placeholder = new SNP(new double[] { 1e9 }, 1);
        var trace = new Trace(placeholder, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourceRef  = DataSourceRef.Selected;
        trace.SourcePath = _library!.SelectedDataSourceAbs;
        trace.SummaryColumn = new SummaryColumnData
        {
            Kind           = kind,
            MetricName     = metricName,
            FractionDigits = 1,
        };
        trace.ColumnWidth = _plot.ColumnWidth;
        _plot.Traces.Add(trace);
        Traces.Add(new TraceRowViewModel(trace, this));
    }

    // 2 VDD, 3 Idq, 4 Zsource, 5 Zin, 6 Zload, 7 Power, 8 Efficiency, 9 Gain, 10 AM/PM, 11 IRL.
    AddCol(SummaryColumnKind.OperatingPoint, "BiasVLoad", HasCube(ds, "BiasVLoad"));
    AddCol(SummaryColumnKind.OperatingPoint, "BiasILoad", HasCube(ds, "BiasILoad"));
    AddCol(SummaryColumnKind.Zsource,        "",          HasCube(ds, "ZSource"));
    AddCol(SummaryColumnKind.Zin,            "",          HasCube(ds, "Zin_real") && HasCube(ds, "Zin_imag"));
    AddCol(SummaryColumnKind.Zload,          "",          HasCube(ds, "ZLoad"));
    AddCol(SummaryColumnKind.Metric,         "Pout",      HasCube(ds, "Pout"));
    AddCol(SummaryColumnKind.Metric,         "DE",        HasCube(ds, "DE"));
    AddCol(SummaryColumnKind.Metric,         "Gt",        HasCube(ds, "Gt"));
    AddCol(SummaryColumnKind.Metric,         "AMPM",      HasCube(ds, "AMPM"));
    AddCol(SummaryColumnKind.Metric,         "IRL",       HasCube(ds, "IRL"));

    RebuildSummary();           // compute Plot.SummaryFreqs + every column's cells
    RefreshAddCommand();
    OnPropertyChanged(nameof(IsSummaryTable));
    PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
    PlotStructureChanged?.Invoke(this, EventArgs.Empty);
}
```

> Notes:
> - `RebuildSummary` (7.5d) does all per-cell work; auto-fill only constructs the column traces in order.
> - Each `TraceRowViewModel` ctor (7.5d) runs `RebuildSummaryMetricOptions()` for summary traces, so each new
>   card's metric dropdown is correctly pre-selected.
> - Replace-then-RebuildSummary means a single redraw at the end. The intermediate trace adds don't each
>   recompute (we call RebuildSummary once).
> - `DataSet` is `RfCore.Data.DataSet` — already imported in this file.

---

## Part 4 — alias display-name registration (verify + fill gaps)

The column headers come from `SummaryColumns.AutoHeader`/`MetricHeader` (7.5b). Per design §4/§10.6 the standard
headers are: VDD (V), Idq (mA), Zsource (Ω), Zin (Ω), Zload (Ω), Power (dBm), Efficiency (%), Gain (dB),
AM/PM (°), Input Return Loss (dB). Verify `SummaryColumns` (from 7.5b) produces exactly these:

- `MetricHeader("Pout")` → "Power (dBm)" ✓
- `MetricHeader("DE")` → "Efficiency (%)" ✓
- `MetricHeader("Gt")` → "Gain (dB)" ✓
- `MetricHeader("AMPM")` → "AM/PM (°)" ✓
- `MetricHeader("IRL")` → "Input Return Loss (dB)" ✓ (design §4 header is "Input Return Loss"; the "(dB)" suffix
  is fine and consistent — keep it, or drop to match the table exactly. Match the design: **"Input Return Loss
  (dB)"** is acceptable; if the owner wants the bare "Input Return Loss" header, trim the unit in `MetricHeader`.)
- `AutoHeader` for OperatingPoint "BiasVLoad" → "VDD (V)" ✓, "BiasILoad" → "Idq (mA)" ✓
- `AutoHeader` for Zload/Zsource/Zin → "Zload (Ω)" / "Zsource (Ω)" / "Zin (Ω)" ✓

**Action:** open `<repo>/src/Ui/DataDisplay/Models/SummaryColumns.cs` and confirm
each mapping above exists. If any is missing or differs, fix it there (single source of truth, used by both the
renderer and auto-fill). No separate alias registry is needed for the summary headers — `SummaryColumns` IS the
registry. (The Phase-7.4h metric alias system governs the contour metric *list*; summary headers are owned by
`SummaryColumns`.)

> If `MetricHeader` lacks any of these (e.g. an older 7.5b landed a shorter map), add the missing arms. Keep the
> Ω/° glyphs in string literals only (never in `///`).

---

## Part 5 — AXAML button

Add an "Auto-fill" button to the Table Properties header (near the "+ Summary" button), bound to
`AutoFillSummaryCommand`, visible/enabled when `IsSummaryAddMode` / `CanAutoFillSummary`. Mechanical — match the
existing "+ Summary" / "+ Contour" button styling. Label e.g. "Auto-fill" or "Standard columns".

> Confirm the view file (search `**/PlotInspectorView.axaml`). The button is a one-liner mirroring the existing
> add buttons; no new converter needed.

---

## Constraints / gotchas
- Presence-gating is silent: absent cube → no trace added (NOT an empty column). A dataset with only core cubes
  yields Zload/Power/Efficiency/Gain (+ VDD/Idq if bias present); Zsource/Zin/AMPM/IRL appear only when 7.5g's
  derived cubes are present.
- Replace semantics: auto-fill clears existing summary columns first. Unsubscribe each removed row's library
  hooks (mirrors `RemoveTrace`) to avoid leaks.
- One `RebuildSummary` at the end (not per column) — single recompute + redraw.
- TreatWarningsAsErrors: local-capture nullable props; no unused locals; invariant formatting stays in the
  renderer (7.5c), headers in `SummaryColumns`.
- Don't touch the standard (non-summary) table or contour paths.

## Tests / verification (owner-run)
1. **Full dataset.** On a loadpull source with bias + derived cubes (post-7.5g import of a dataset carrying
   Γin/trans_phase/Refl + bias), auto-fill yields columns in order: Freq, VDD, Idq, Zsource, Zin, Zload, Power,
   Efficiency, Gain, AM/PM, IRL — one row per freq, correct headers/units.
2. **Core-only dataset.** On a loadpull source with only core cubes (no Zsource/Zin/AMPM/IRL/bias), auto-fill
   yields Freq, Zload, Power, Efficiency, Gain only — gated columns silently omitted, no blank columns, no crash.
3. **Replace semantics.** With some summary columns already present, auto-fill replaces them with the standard
   set (no duplicates).
4. **Shared optimum/compression.** All auto-filled columns evaluate at the table's current MXP/MXE +
   compression; toggling the optimum recomputes all of them; the title reflects the optimum/compression.
5. **Persistence.** Save + reload an auto-filled table; the column set + order round-trips (7.5b) and cells
   repopulate via RebuildSummary on load.
6. **Headers.** Each header matches design §4 exactly (Power (dBm), Efficiency (%), Gain (dB), AM/PM (°), etc.).
7. **Regression.** Non-summary tables and contour plots unaffected.
