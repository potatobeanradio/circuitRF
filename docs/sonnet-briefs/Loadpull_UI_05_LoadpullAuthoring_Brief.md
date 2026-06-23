# Brief — Loadpull UI 05: enable Loadpull authoring in Edit Analysis

**Goal:** Make the **Loadpull** type a first-class, authorable analysis in the Add/Edit Analysis dialog,
producing a `LoadpullAnalysis` (which already exists and runs). Add an `LpBodyViewModel` + its view section,
wire it into `AnalysisEditorViewModel.BuildAnalyses`, enable the disabled "Loadpull" type-picker entry, and
give the list card an "LP" badge + summary.

**Depends on:** brief 04 (LP serialization) so the authored analysis persists. The Tuner component (briefs
01–02) should exist so the tuner pickers have something to pick, but is not strictly required to compile.

**Reads with:** `docs/design/analysis-authoring.md` §4.2 / §4.3 (the progressive-disclosure form pattern),
`docs/design/analysis-cards-ui.md` (badges/summaries), `loadpull.md` §2.1 (the directive keys this authors).
The **template to copy** is the HB body: `src/Ui/ViewModels/HbBodyViewModel.cs` and its view
`src/Ui/Views/.../` (grep for the HB section in the analysis editor AXAML — likely
`src/Ui/Views/Analyses/AnalysisEditorView.axaml` or similar; find it by searching for `HbBody`).

## What's already scaffolded (don't re-create)

`AnalysisEditorViewModel` already has: `AnalysisKind.LP` in the enum, `IsLp` property,
`NextFreeName(... LP ...) => "LP1"`, and the `OnTypeChanged` rename logic. What's missing:
- an `LpBodyViewModel` (there is `SpBody` and `HbBody`; there is no `LpBody`),
- the `BuildAnalyses` arm for `AnalysisKind.LP` (currently `_ => null`, so OK silently fails for LP),
- the editor-view section for LP fields,
- the type-picker entry enabled (it's a disabled "coming soon" entry today),
- the `FromAnalysis` (edit) path loading an existing `LoadpullAnalysis` into the body,
- the row badge/summary for LP.

## 1 — `LpBodyViewModel` (`src/Ui/ViewModels/LpBodyViewModel.cs`)

Model it on `HbBodyViewModel`: `[ObservableProperty]` fields for each editable directive key, live "≈"
previews via `AnalysisPreviewHelper`, a `BuildAnalysis(name, enabled)` that returns a `LoadpullAnalysis`, and
a static `FromAnalysis(LoadpullAnalysis lp, SchematicEditModel model)` for the edit path.

Fields (Basic group, always visible — the few a user must set):
- **LoadTuner** and **SourceTuner** — the instance names of the two tuners (both **required**, `loadpull.md`
  §2.1). These are **pickers**, not free text: expose `IReadOnlyList<string> TunerInstanceNames` computed
  from the model's components whose `Symbol` is one of `Tuner`/`SourceTuner`/`LoadTuner`
  (`_model.Components.Where(c => c.Symbol is SymbolKind.Tuner or SymbolKind.SourceTuner or
  SymbolKind.LoadTuner).Select(c => c.InstanceName)`), plus the two selected-name properties
  `LoadTunerName` / `SourceTunerName`. A ComboBox bound to the list. If the schematic has no tuners, the
  list is empty — show an inline hint ("Place a Tuner component first") and let the field stay invalid.
- **Tone (f₀)** — `LpToneExpr` (single combined expression string per brief 04's decision; e.g. `2e9` or
  `2*f0`). Preview via `AnalysisPreviewHelper.ComputePreview`.
- **Grid (.gam file)** — `GridPath`. A text field + a **Browse…** button that opens a file picker filtered to
  `*.gam` (find the existing file-picker pattern — e.g. how the SnP `File` param browses for Touchstone; grep
  for the SnP browse dialog used by the parameter editor / `brief-snp-browse-show-relative`). Store a path
  relative to the schematic directory when possible (mirror SnP's relative-path policy, `SnpPathPolicy.cs`).
  Required.
- **Pin start / Pin max / Pin step** — `PinStartExpr`, `PinMaxExpr` (required safety cap), `PinStepExpr`.
- **Compression (dB)** — `CompressionExpr`.

Advanced group (Expander, collapsed by default — `loadpull.md` §2.1 has many keys):
- **Sweep** — `SweepExpr`, a `Load`/`Source` toggle (segmented control or ComboBox; default `Load`).
- **TuneHarm** — `TuneHarmExpr` (default `1`).
- **GainType** — `GainTypeExpr`, `Gt`/`Gp` toggle (default `Gt`).
- **MaxHarm** — `MaxHarmonicExpr` (default `5`).
- **Tickle** — `TickleExpr` (default `-50`; the literal `off` disables — accept either a number or "off").
- **MaxIter** — `MaxIterExpr` (default `100`).
- **FFT oversample / Tol / DriveStepping / GuardHarmonic** — `FFTOverSampleExpr`, `TolExpr`,
  `DriveSteppingExpr` (reuse `HbBodyViewModel.DriveSteppingOptions = ["Always","IfNecessary","Never"]`),
  `GuardHarmonicExpr`. These mirror the HB advanced fields one-for-one — copy that AXAML block.

`BuildAnalysis(name, enabled)`:
```csharp
public LoadpullAnalysis BuildAnalysis(string name, bool enabled) => new(name)
{
    Enabled         = enabled,
    LoadTunerName   = LoadTunerName?.Trim()   ?? "",
    SourceTunerName = SourceTunerName?.Trim() ?? "",
    GridPath        = GridPath?.Trim()        ?? "",
    ToneExpr        = ToneExpr,
    PinStartExpr    = PinStartExpr,
    PinMaxExpr      = PinMaxExpr,
    PinStepExpr     = PinStepExpr,
    MaxHarmonicExpr = MaxHarmonicExpr,
    SweepExpr       = SweepExpr,
    TuneHarmExpr    = TuneHarmExpr,
    CompressionExpr = CompressionExpr,
    GainTypeExpr    = GainTypeExpr,
    TickleExpr      = TickleExpr,
    MaxIterExpr     = MaxIterExpr,
    FFTOverSampleExpr = FftOverSampleExpr,
    TolExpr         = TolExpr,
    DriveSteppingExpr = DriveSteppingExpr,
    GuardHarmonicExpr = GuardHarmonicExpr,
    // SourceDirectory is set by the extractor/reader at run time, not here.
};
```
`FromAnalysis(...)`: construct an `LpBodyViewModel(model)` and copy every field back out of the
`LoadpullAnalysis`. Mirror `HbBodyViewModel.FromAnalysis`.

**Validation:** add a method the editor can consult (or expose bool props) so OK is gated when LoadTuner,
SourceTuner, Grid, Tone, or PinMax are blank. The simplest path: have `AnalysisEditorViewModel.BuildAnalyses`
return null when `LpBody.BuildAnalysis` would produce empty required fields — but better UX is an inline hint
like the name validator. Match whatever rigor the SP/HB bodies use; at minimum, do not let OK produce an LP
with empty `LoadTunerName`/`SourceTunerName`/`GridPath`.

## 2 — Wire into `AnalysisEditorViewModel`

- Add `public LpBodyViewModel LpBody { get; }`, constructed in BOTH constructors (Add: `new LpBodyViewModel(model)`;
  Edit: `LpBodyViewModel.FromAnalysis(lp, model)` in a new `case LoadpullAnalysis lp:` arm of the edit
  `switch`, setting `_type = AnalysisKind.LP`). In the Add constructor and the other Edit arms, construct a
  fresh `new LpBodyViewModel(model)` so the field is always non-null (same as how `SpBody`/`HbBody` are
  always constructed).
- In `BuildAnalyses`, replace the `_ => null` (or add an arm) for `AnalysisKind.LP`:
  ```csharp
  AnalysisKind.LP => LpBody.BuildAnalysis(name, Enabled),
  ```
  **Sweep chains for LP:** parametric sweeps wrap HB today; a swept loadpull is unusual. Keep it simple —
  **disallow sweep axes on LP** for v1: if `SweepAxes.Count > 0` while `Type == LP`, ignore them (or, better,
  hide the sweep-axis UI when LP/LPP is selected). Document this as a v1 limitation. So for LP, return
  `[inner]` only.
- `ResolveChain` already handles non-sweep base analyses generically; an LP that isn't wrapped resolves to
  itself. No change needed there beyond the new edit `case`.

## 3 — Editor view (AXAML)

Find the analysis editor view (grep for `IsHb` / `HbBody` binding). It uses `IsDc`/`IsSp`/`IsHb` visibility
bindings to swap the body. Add an LP section bound to `IsLp` visibility, containing the Basic group + an
Advanced `Expander`, laid out like the HB section. Bind ComboBoxes for LoadTuner/SourceTuner to
`LpBody.TunerInstanceNames` with `SelectedItem` → `LpBody.LoadTunerName`/`SourceTunerName`. Add the Grid
text-box + Browse button (wire the button to a command on the body VM or the editor VM that opens the
`*.gam` picker; reuse the SnP browse plumbing). Mirror the HB `≈ preview` TextBlocks under each expression
field.

**Type picker:** find where the picker disables Loadpull/Pursuit with a "coming soon" tooltip
(`analysis-authoring.md` §4.2 says they're shown disabled). Enable the **Loadpull** entry (remove the
`IsEnabled=false`/tooltip for LP; leave LPP disabled until brief 06 unless you do both views together).

## 4 — List badge + summary (`AnalysisRowViewModel.cs`)

- `TypeLabel`: add `LoadpullAnalysis => "LP",`.
- `ComputeSummary`: add `LoadpullAnalysis lp => FormatLpSummary(lp),` and write `FormatLpSummary` →
  something like `$"{lp.SweepExpr}pull · {tuners} · {lp.CompressionExpr} dB"` where `tuners` is e.g.
  `"{LoadTunerName}/{SourceTunerName}"`. Keep it one line, plain text (reuse `FormatFreq` for the tone if you
  show it). Example: `"Loadpull · LoadTuner1/SourceTuner1 · 3 dB, grid hero3.gam"`.
- The badge color/area in the list view already renders `TypeLabel`; "LP" needs no new view code if the
  badge is a TextBlock bound to `TypeLabel` (confirm — `analysis-cards-ui.md` lists badges DC/SP/HB/LP/LPP/SW
  as already-planned, so the badge surface likely already accommodates the string).

## 5 — Tests
- Editor unit test: construct `AnalysisEditorViewModel(model, AnalysisKind.LP)`, set LoadTuner/SourceTuner/
  Grid/Tone/PinMax, call `BuildAnalyses()`, assert it returns one `LoadpullAnalysis` with those values and
  `Enabled`.
- Edit round-trip: `new AnalysisEditorViewModel(model, existingLp)` loads the body fields; `BuildAnalyses`
  reproduces an equal `LoadpullAnalysis`.
- Required-field gating: blank LoadTuner (or Grid) → `BuildAnalyses` returns null / OK disabled.
- Persistence smoke (relies on brief 04): add the LP to the model, save+reload the `.csch`, assert the LP
  survives.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green.
2. Launch: Add Analysis → pick **Loadpull** (now enabled). The form shows tuner pickers, a Grid browse, Pin
   fields, Compression, and an Advanced expander. Picking tuners from a schematic that has them works; an
   empty schematic shows the "place a Tuner first" hint.
3. OK adds an "LP" card with a sensible summary; double-clicking it reopens the populated form; Save/reload
   keeps it.
4. Firewall passes.
