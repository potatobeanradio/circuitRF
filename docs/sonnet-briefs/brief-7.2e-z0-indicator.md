# Sonnet Brief — 7.2e: non-uniform/complex Z0 indicator + one-time Messages warning

**Context.** Phase 7.2a shipped the `Z0{port}` carrier and the headless classifier; 7.2b/c shipped the
data-source library and rename. 7.2e is the **UI consumer**: warn the user when a scattering (S) data source is
referenced to a **non-uniform-across-ports OR complex** reference impedance — because `S(i,j)` is Z0-dependent,
so a forgotten/odd reference silently mis-reads every S result (the VendorA footgun). **No RF math, no
DataSet/DataCube API change** — pure consume + display.

## API already in place (verify names, don't re-implement)
- `RfCore.Data.DataSetBuilder.ClassifyZ0(DataCube) → Z0Kind` where `Z0Kind { UniformReal, UniformComplex,
  NonUniform }`. (`UniformReal` is the only "ordinary" case; both others trigger the indicator.)
- A loaded S DataSet carries a `"Z0"` cube: `ds.Contains("Z0")`, `ds["Z0"]`, `.ComplexValues` (per-port, index
  `k` = port `k+1`), `.Axes[0]` = `port` (1-based values). Touchstone-derived sets always have a **uniform**
  `Z0` (so they classify `UniformReal`/`UniformComplex`, never `NonUniform`).
- `DataSourceEntryViewModel` (`…/ViewModels/DataSourceEntryViewModel.cs`) holds `DataSet? Data`, `SNP? Snp`,
  `Kind`, `FilePath`; refresh paths are `RefreshTouchstone`/`RefreshNpy`. Trace S-binding is the
  network/SNP/matrix path in `Trace.cs` (`MatrixType.S`, `Row`/`Col`, `Derived`); cube-S-traces also exist
  (7.2c). A "scattering trace" = the S-cube/matrix kind (not Y/Z view, not a non-S cube trace).

## 1. Entry-level classification (compute once, on load/refresh)
In `DataSourceEntryViewModel` add:
```csharp
/// <summary>Reference-impedance kind of this source's S data, or null when there is no Z0 cube
/// (no S data / cube-only non-S source). Computed on load and refresh.</summary>
public Z0Kind? Z0Kind { get; private set; }

/// <summary>True when S results from this source are referenced to a non-uniform or complex Z0
/// (the value the user must be reminded about). False for plain uniform-real 50 Ω-style sources.</summary>
public bool HasUnusualZ0 => Z0Kind is RfCore.Data.Z0Kind.NonUniform or RfCore.Data.Z0Kind.UniformComplex;

/// <summary>Per-port reference impedances (index k = port k+1); empty when no Z0 cube.</summary>
public IReadOnlyList<Complex> Z0PerPort { get; private set; } = Array.Empty<Complex>();
```
Compute in a private `ClassifyZ0FromData()` called at the end of both constructors and both `Refresh*`
methods: if `_data?.Contains("Z0") == true`, set `Z0Kind = DataSetBuilder.ClassifyZ0(_data["Z0"])` and
`Z0PerPort = _data["Z0"].ComplexValues`; else null/empty. (Cheap; the cube is length = nPorts.)

## 2. Per-trace badge (always-on indicator)
A **subtle** badge appears on any **scattering trace** whose source `HasUnusualZ0` is true. Surface it on the
trace's inspector card (`TraceRowViewModel`) and on the Y-axis label strip:
- `TraceRowViewModel`: add `public bool ShowZ0Badge` = (trace is S-kind) AND (the resolved library entry for
  this trace has `HasUnusualZ0`). Resolve the entry the same way the row already finds its source (it already
  matches `entry.Snp`/`entry.Data` for the picker). Add `public string Z0BadgeTooltip` listing the per-port
  values, e.g. `"Reference Z0: port1=50Ω, port2=75−j10Ω (non-uniform)"` / `"… (complex)"`. Recompute when the
  selected signal or library changes (hook the existing `RebuildSignals`/`RefreshDataSources` path).
- View (`TraceRowView`/inspector card XAML): a small warning glyph (Material `AlertCircleOutline` or
  `Information`, `CrfWarningBrush`) bound to `IsVisible={Binding ShowZ0Badge}` with `ToolTip.Tip={Binding
  Z0BadgeTooltip}`. Keep it subtle — a single icon, not a banner. (Use `CrfWarningBrush`, **not** a
  `System*Color` key — those resolve to Color and silently fail on `IBrush`/`MaterialIcon.Foreground`.)
- Marker impedance: the existing `GetMarkerImpedanceString` already uses the trace `Z0`; **no change required**
  for the uniform case (SNP path stays uniform). Full per-port marker impedance is the 7.2f follow-on — out of
  scope here; do not wire it.

## 3. One-time Messages warning (per source)
When a source with `HasUnusualZ0` is **loaded or first plotted**, emit one `Message` (not per trace, not per
redraw): e.g. `Warning: "<file> uses a non-uniform/complex reference impedance — S-parameter results depend on
it. Per-port Z0: …"`. Fire **once per source path** (track a `HashSet<string>` of already-warned paths on the
library/display so reload or adding a 2nd trace doesn't re-warn; clearing/removing the source clears its entry).

**Wiring the sink:** the data-source library/entry has no `IMessageSink`. Read
`src/Ui/DataDisplay/ViewModels/DataSourceLibraryViewModel.cs` and `DisplayWindowViewModel.cs` to find the
existing display→workspace signal seam (the workspace already drains display events — e.g. the auto-refresh /
`RefreshOpenDataDisplaysAsync` path in `WorkspaceViewModel`, and `Messages` lives on `WorkspaceViewModel`).
Prefer **raising an event** (e.g. `DataSourceLibraryViewModel.UnusualZ0Detected(string path, Z0Kind kind,
IReadOnlyList<Complex> z0)`) that the workspace subscribes to and posts to `Messages` — mirror how other
library changes already reach the workspace. **Do not** give the library a direct `IMessageSink` if the
existing pattern is event-based; match what's there. If no clean seam exists, flag it rather than inventing a
new dependency.

## Tests (`tests/Ui.Tests`, headless)
1. **Entry_ClassifiesUnusualZ0:** build a `.npy`/DataSet with a non-uniform `Z0` cube → entry `HasUnusualZ0`
   true, `Z0PerPort` matches; a uniform-real source → false.
2. **Badge_OnlyOnScatteringTrace:** an S-trace from an unusual-Z0 source → `ShowZ0Badge` true; the same source
   plotted as a non-S cube trace (or a uniform source) → false.
3. **Warning_FiresOncePerSource:** loading + adding two traces from one unusual-Z0 source raises the
   warn-event exactly once; a second distinct source warns again.

## Gate
Build 0W/0E; tests green. Manual: build an S-param run with per-port Term `Z` (non-uniform or complex) → its
`.npy` source shows the subtle badge on S-traces with per-port Z0 in the tooltip, and a single Messages warning
on load; a normal 50 Ω Touchstone shows no badge and no warning.

## On completion
Note in `src/Ui/CLAUDE.md`: Data Display surfaces an always-on per-trace badge (tooltip = per-port Z0) on
scattering traces whose source Z0 is non-uniform or complex (`DataSetBuilder.ClassifyZ0`), plus a one-time
per-source Messages warning routed through the library→workspace event seam. Full per-port Z0-dependent compute
(S→Y/Z, marker impedance, stability on non-uniform sources) remains the **7.2f** follow-on.
