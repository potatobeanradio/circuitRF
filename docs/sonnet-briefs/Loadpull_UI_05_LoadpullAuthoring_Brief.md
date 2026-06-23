# Brief — Loadpull UI 05: enable Loadpull authoring in Edit Analysis

**Goal:** Make the **Loadpull** type a first-class, authorable analysis in the Add/Edit Analysis dialog,
producing a `LoadpullAnalysis` (which already exists and runs). Add an `LpBodyViewModel` + its view section,
wire it into `AnalysisEditorViewModel.BuildAnalyses`, enable the disabled "Loadpull" type-picker entry, and
give the list card an "LP" badge + summary.

**Depends on:** brief 04b (the tone resolves via the var-unit-wins rule, and the model carries `ToneUnit`)
and brief 04 (LP serialization). The Tuner component (briefs 01–02) should exist so the tuner pickers have
something to pick, but is not strictly required to compile.

**Reads with:** `docs/design/analysis-authoring.md` §4.2 / §4.3 (the progressive-disclosure form pattern),
`docs/design/analysis-cards-ui.md` (badges/summaries), `loadpull.md` §2.1 (the directive keys this authors).
The **template to copy** is the HB body: `src/Ui/ViewModels/HbBodyViewModel.cs` and its view (grep for the
HB section in the analysis editor AXAML, e.g. `src/Ui/Views/Analyses/AnalysisEditorView.axaml`).

## What's already scaffolded (don't re-create)

`AnalysisEditorViewModel` already has: `AnalysisKind.LP` in the enum, `IsLp` property,
`NextFreeName(... LP ...) => "LP1"`, and the `OnTypeChanged` rename logic. Missing:
- an `LpBodyViewModel` (there is `SpBody` and `HbBody`; no `LpBody`),
- the `BuildAnalyses` arm for `AnalysisKind.LP` (currently `_ => null`),
- the editor-view section for LP fields,
- the type-picker entry enabled,
- the `FromAnalysis` (edit) path loading an existing `LoadpullAnalysis`,
- the row badge/summary for LP.

## 1 — `LpBodyViewModel` (`src/Ui/ViewModels/LpBodyViewModel.cs`)

Model it on `HbBodyViewModel`: `[ObservableProperty]` fields per editable directive key, live "≈" previews
via `AnalysisPreviewHelper`, a `BuildAnalysis(name, enabled)` returning a `LoadpullAnalysis`, and a static
`FromAnalysis(LoadpullAnalysis lp, SchematicEditModel model)`.

Fields (Basic group, always visible):
- **LoadTuner** and **SourceTuner** — instance names of the two tuners (both **required**, `loadpull.md`
  §2.1). **Pickers**, not free text: expose `IReadOnlyList<string> TunerInstanceNames` from the model's
  components whose `Symbol` is `Tuner`/`SourceTuner`/`LoadTuner`
  (`_model.Components.Where(c => c.Symbol is SymbolKind.Tuner or SymbolKind.SourceTuner or
  SymbolKind.LoadTuner).Select(c => c.InstanceName)`), plus selected-name props `LoadTunerName` /
  `SourceTunerName`, bound to ComboBoxes. Empty schematic → inline hint "Place a Tuner component first";
  field stays invalid. (Per brief 02, a SourceTuner symbol should be the one chosen for `SourceTuner`, and a
  Tuner/LoadTuner symbol for `LoadTuner` — the net ordering matches the role. You may surface this as a
  gentle hint but need not enforce it.)
- **Tone (f₀)** — a **coefficient + unit pair**, exactly like HB. Mirror `HbBodyViewModel`'s `ToneCoeff` +
  `ToneUnit` (+ `FreqUnitHelper.Units` for the unit ComboBox, + `ComputeFreqPreview`, + the `OnToneUnitChanged`
  rescale nicety). Do **not** use a single combined expression string — that would break a unitless VAR used
  as the tone (brief 04b). The pair maps to `LoadpullAnalysis.ToneExpr` (the coefficient) + `ToneUnit`.
- **Grid (.gam file)** — `GridPath`. Text field + **Browse…** opening a `*.gam` picker (reuse the SnP `File`
  browse plumbing; store relative to the schematic dir per `SnpPathPolicy.cs`). Required.
- **Pin start / Pin max / Pin step** — `PinStartExpr`, `PinMaxExpr` (required cap), `PinStepExpr` (dBm/dB —
  NOT frequency; plain `ComputePreview`, no unit picker).
- **Compression (dB)** — `CompressionExpr`.

Advanced group (Expander, collapsed):
- **Sweep** — `SweepExpr`, `Load`/`Source` toggle (default `Load`).
- **TuneHarm** — `TuneHarmExpr` (default `1`).
- **GainType** — `GainTypeExpr`, `Gt`/`Gp` toggle (default `Gt`).
- **MaxHarm** — `MaxHarmonicExpr` (default `5`).
- **Tickle** — `TickleExpr` (default `-50`; literal `off` disables — accept a number or "off").
- **MaxIter** — `MaxIterExpr` (default `100`).
- **FFT oversample / Tol / DriveStepping / GuardHarmonic** — `FFTOverSampleExpr`, `TolExpr`,
  `DriveSteppingExpr` (reuse `HbBodyViewModel.DriveSteppingOptions`), `GuardHarmonicExpr`. Copy the HB
  advanced AXAML block.

`BuildAnalysis(name, enabled)`:
```csharp
public LoadpullAnalysis BuildAnalysis(string name, bool enabled) => new(name)
{
    Enabled         = enabled,
    LoadTunerName   = LoadTunerName?.Trim()   ?? "",
    SourceTunerName = SourceTunerName?.Trim() ?? "",
    GridPath        = GridPath?.Trim()        ?? "",
    ToneExpr        = ToneCoeff,        // coefficient expression
    ToneUnit        = ToneUnit,         // frequency unit (var-unit-wins resolves at run time; brief 04b)
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
    // SourceDirectory set by the extractor/reader at run time, not here.
};
```
`FromAnalysis(...)`: copy every field back, splitting `ToneExpr`/`ToneUnit` into `ToneCoeff`/`ToneUnit` with
the same `FreqUnitHelper.Split` nicety HB uses when `ToneUnit == "Hz"` and `ToneExpr` is numeric (mirror
`HbBodyViewModel.FromAnalysis`).

**Validation:** gate OK so LoadTuner, SourceTuner, Grid, the tone, and PinMax are non-blank (match the
rigor of the SP/HB bodies; at minimum, never produce an LP with empty `LoadTunerName`/`SourceTunerName`/
`GridPath`).

## 2 — Wire into `AnalysisEditorViewModel`
- Add `public LpBodyViewModel LpBody { get; }`, constructed in BOTH constructors (Add: `new LpBodyViewModel(model)`;
  Edit: a new `case LoadpullAnalysis lp:` arm → `LpBodyViewModel.FromAnalysis(lp, model)`, `_type = LP`).
  Construct a fresh `LpBody` in the other arms so it's always non-null (like `SpBody`/`HbBody`).
- `BuildAnalyses` arm: `AnalysisKind.LP => LpBody.BuildAnalysis(name, Enabled),`.
- **No sweep chains on LP** for v1: if `SweepAxes.Count > 0` while `Type == LP`, ignore them (or hide the
  sweep-axis UI for LP/LPP). Return `[inner]` only. Document the limitation.
- `ResolveChain` handles non-sweep bases generically — only the new edit `case` is needed.

## 3 — Editor view (AXAML)
Find the editor view (grep `IsHb`/`HbBody`). It swaps the body via `IsDc`/`IsSp`/`IsHb` visibility. Add an LP
section bound to `IsLp`: Basic group + Advanced `Expander`, laid out like HB. Bind LoadTuner/SourceTuner
ComboBoxes to `LpBody.TunerInstanceNames`; the **Tone** is a coefficient TextBox + a unit ComboBox bound to
`FreqUnitHelper.Units` with the `≈` preview (copy the HB Tone row exactly). Add the Grid text-box + Browse
button. Mirror the HB `≈ preview` TextBlocks under each expression field. **Enable the Loadpull** type-picker
entry (remove its disabled/"coming soon" state; leave LPP disabled until brief 06 unless you do both views
together).

## 4 — List badge + summary (`AnalysisRowViewModel.cs`)
- `TypeLabel`: add `LoadpullAnalysis => "LP",`.
- `ComputeSummary`: add `LoadpullAnalysis lp => FormatLpSummary(lp),` → one plain-text line, e.g.
  `"Loadpull · LoadTuner1/SourceTuner1 · 3 dB, grid hero3.gam"` (use `FormatFreq` for the tone if shown).
- The badge is a TextBlock bound to `TypeLabel` — "LP" needs no new view code (the badge surface already
  plans for LP/LPP per `analysis-cards-ui.md`).

## 5 — Tests
- Editor unit test: `AnalysisEditorViewModel(model, AnalysisKind.LP)`, set LoadTuner/SourceTuner/Grid/
  Tone(coeff+unit)/PinMax, `BuildAnalyses()` → one `LoadpullAnalysis` with those values, correct
  `ToneExpr`+`ToneUnit`, and `Enabled`.
- **Tone-unit:** set Tone coeff `"RFfreq"` + unit `"GHz"` → the built `LoadpullAnalysis` has
  `ToneExpr="RFfreq"`, `ToneUnit="GHz"` (the run-time var-unit-wins resolution is covered by brief 04b's
  engine tests).
- Edit round-trip from an existing `LoadpullAnalysis` (incl. a non-`"Hz"` tone unit).
- Required-field gating: blank LoadTuner/Grid → `BuildAnalyses` null / OK disabled.
- Persistence smoke (brief 04): add LP, save+reload `.csch`, survives incl. tone unit.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green.
2. Launch: Add Analysis → **Loadpull** enabled; the form shows tuner pickers, a **Tone coeff + unit** row
   (like HB), Grid browse, Pin fields, Compression, Advanced expander. A unitless VAR + GHz unit authors
   correctly (and, with brief 04b, runs at the right frequency).
3. OK adds an "LP" card with a sensible summary; double-click reopens it; Save/reload keeps it.
4. Firewall passes.
