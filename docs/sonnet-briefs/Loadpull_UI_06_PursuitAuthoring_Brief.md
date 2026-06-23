# Brief — Loadpull UI 06: enable Loadpull-Pursuit authoring in Edit Analysis

**Goal:** Make the **Loadpull-Pursuit** type authorable, producing a `LoadpullPursuitAnalysis` (already
exists and runs — Phase 4b-2). Add an `LppBodyViewModel` + view section, wire `AnalysisKind.LPP` into
`BuildAnalyses`, enable the disabled "Loadpull-Pursuit" type-picker entry, and add the "LPP" badge + summary.

**Depends on:** brief 04 (LPP serialization) and brief 05 (LP authoring — `LppBodyViewModel` reuses the LP
body's tuner-picker + shared-field machinery; build LPP on top of that to avoid duplicating the ~16 shared
fields).

**Reads with:** `docs/design/loadpull_pursuit.md` §3 (the pursuit directive keys — the authoritative field
list) and §0.1 / §6.5 (what the optional follow-on does, so the UI labels make sense), plus
`analysis-authoring.md` §4.2 and the LP body from brief 05 as the template.

## What's already scaffolded
`AnalysisEditorViewModel` already has `AnalysisKind.LPP`, `IsLpp`, and `NextFreeName(... LPP ...) => "LPP1"`.
Missing: `LppBodyViewModel`, the `BuildAnalyses` arm, the view section, the enabled picker entry, the edit
`case`, and the row badge/summary.

## 1 — `LppBodyViewModel` (`src/Ui/ViewModels/LppBodyViewModel.cs`)

`loadpull_pursuit.md` §3: **all `loadpull` keys EXCEPT `Grid`**, plus pursuit-specific keys. So the LPP body
shares the LP body's fields minus Grid. **Reuse, don't duplicate.** Two acceptable structures — pick one:

- **(preferred) Composition:** `LppBodyViewModel` *contains* an `LpBodyViewModel`-like shared core for the
  common fields (LoadTuner, SourceTuner, Tone, Pin start/max/step, Compression, Sweep, TuneHarm, GainType,
  MaxHarm, Tickle, MaxIter, FFT/Tol/DriveStepping/GuardHarmonic), and adds its own pursuit fields. If the LP
  body's shared fields are easy to factor into a small `LpSharedFieldsViewModel` during brief 05, do that and
  have both LP and LPP embed it. If that refactor is too invasive, fall back to (b).
- **(b) Parallel fields:** copy the shared `[ObservableProperty]` fields into `LppBodyViewModel` (it's
  mechanical) and add the pursuit fields. Simpler, slightly more code. Acceptable.

Whichever you choose, the LPP body has **no Grid field** (pursuit generates its grid) and adds these pursuit
fields (defaults from `LoadpullPursuitAnalysis` in `Analysis.cs`):

Basic-ish pursuit group:
- **EffType** — `EffTypeExpr`, `DE`/`PAE` toggle (default `DE`). MXE criterion (§2).
- **Zsource backoff (dB)** — `ZsourceOBOExpr` (default `5`). §6 auto-Zsource.
- **Search method** — `SearchMethodExpr`, a ComboBox of `["SteepestAscent","IteratedQuadratic"]` (default
  `SteepestAscent`). §1.1.2.

Output / follow-on group (Expander):
- **Create follow-on loadpull** — `CreateLoadpullResultExpr`, a checkbox bound to `true`/`false` (default
  `true`). §6.5.2: runs the focused loadpull around the optima.
- **Follow-on source match** — `LoadpullResultZsourceExpr`, ComboBox `["MXE","MXP","None"]` (default `MXE`).
  Only meaningful when Create is on — disable/grey it when the checkbox is off.
- **Output .gam grid (optional)** — `OutputGridPath` (nullable; blank = no file). A text field + Browse
  ("Save as…" `*.gam`) — but it's optional, so blank is valid. §5 / §3 `OutputGrid`.

Grid-builder group (Advanced Expander — these shape the recommended-terminations grid, §5):
- **VSWR1 (focused radius)** — `Vswr1Expr` (default `1.5`).
- **VSWR1 resolution (N×N)** — `Vswr1ResolutionExpr` (default `4`).
- **VSWR2 (broad radius)** — `Vswr2Expr` (default `3`).
- **VSWR2 resolution (N×N)** — `Vswr2ResolutionExpr` (default `4`).
- **Keep non-converging** — `KeepNonconvergingExpr`, checkbox `false`/`true` (default `false`). §5.
- **Non-convergent exclusion VSWR** — `NonconvergentVswrExpr` (default `1.05`).

Plus the **shared** advanced LP fields (MaxHarm, Tickle, MaxIter, FFT, Tol, DriveStepping, GuardHarmonic,
Sweep, TuneHarm, GainType) from the LP body.

`BuildAnalysis(name, enabled)` returns a `LoadpullPursuitAnalysis`:
```csharp
public LoadpullPursuitAnalysis BuildAnalysis(string name, bool enabled) => new(name)
{
    Enabled         = enabled,
    LoadTunerName   = LoadTunerName?.Trim()   ?? "",
    SourceTunerName = SourceTunerName?.Trim() ?? "",
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
    EffTypeExpr               = EffTypeExpr,
    ZsourceOBOExpr            = ZsourceOBOExpr,
    SearchMethodExpr          = SearchMethodExpr,
    OutputGridPath            = string.IsNullOrWhiteSpace(OutputGridPath) ? null : OutputGridPath.Trim(),
    Vswr1Expr                 = Vswr1Expr,
    Vswr1ResolutionExpr       = Vswr1ResolutionExpr,
    Vswr2Expr                 = Vswr2Expr,
    Vswr2ResolutionExpr       = Vswr2ResolutionExpr,
    KeepNonconvergingExpr     = KeepNonconvergingExpr,
    NonconvergentVswrExpr     = NonconvergentVswrExpr,
    CreateLoadpullResultExpr  = CreateLoadpullResultExpr,
    LoadpullResultZsourceExpr = LoadpullResultZsourceExpr,
};
```
Add `FromAnalysis(LoadpullPursuitAnalysis lpp, SchematicEditModel model)` mirroring the LP body.
**Validation:** LoadTuner, SourceTuner, Tone, PinMax required (Grid is NOT required for LPP). Gate OK the
same way brief 05 does.

## 2 — Wire into `AnalysisEditorViewModel`
- Add `public LppBodyViewModel LppBody { get; }`, constructed in both constructors (Add: fresh; Edit: a new
  `case LoadpullPursuitAnalysis lpp:` arm → `LppBodyViewModel.FromAnalysis(lpp, model)`, `_type = LPP`).
- `BuildAnalyses` arm: `AnalysisKind.LPP => LppBody.BuildAnalysis(name, Enabled),`. Like LP, **no sweep
  chains** on LPP for v1 — return `[inner]`.

## 3 — Editor view (AXAML)
Add an LPP section bound to `IsLpp`. It mirrors the LP section's shared fields (tuner pickers, Tone, Pin
fields, Compression, the shared Advanced expander) **minus the Grid row**, plus the pursuit group (EffType,
Zsource backoff, SearchMethod), the follow-on group (Create checkbox, source-match combo, optional OutputGrid
browse), and the grid-builder advanced group (VSWR1/2 + resolutions, keep-nonconverging, exclusion VSWR).
Add `≈` previews on the numeric expression fields. **Enable the "Loadpull-Pursuit" type-picker entry**
(remove its disabled/"coming soon" state).

If brief 05 already enabled only LP in the picker, finish the picker here so both LP and LPP are live and any
"coming soon" affordance is gone (per `analysis-authoring.md` §8 the deferred item is now done — update that
doc's status line and the TODO note).

## 4 — Row badge + summary (`AnalysisRowViewModel.cs`)
- `TypeLabel`: `LoadpullPursuitAnalysis => "LPP",`.
- `ComputeSummary`: `LoadpullPursuitAnalysis lpp => FormatLppSummary(lpp),` → e.g.
  `$"Pursuit · {lpp.SearchMethodExpr} · MXE={lpp.EffTypeExpr}{(create?\", +loadpull\":\"\")}"`. One line,
  plain text. Example: `"Pursuit · SteepestAscent · DE, +loadpull"`.

## 5 — Docs
- Update `docs/design/analysis-authoring.md`: §0 decision line ("Loadpull / loadpull-pursuit authoring is
  DEFERRED") and §8 open-items + the §4.2 "coming soon" note now read **done**; add a §7-style line that LP
  and LPP authoring shipped (brief 05/06), with the v1 limitation that LP/LPP cannot be wrapped in a
  parametric sweep yet.

## 6 — Tests
- Editor unit test: build an LPP via the editor VM, assert the resulting `LoadpullPursuitAnalysis` has the
  set fields incl. pursuit keys; `OutputGridPath` blank → null.
- Edit round-trip from an existing `LoadpullPursuitAnalysis`.
- Follow-on combo gating: source-match combo disabled when Create is off (VM-level: a bool prop the view
  binds `IsEnabled` to).
- Persistence smoke: add LPP, save+reload `.csch`, survives (brief 04).

## Verify
1. `dotnet build` zero warnings; `dotnet test` green.
2. Launch: Add Analysis → **Loadpull-Pursuit** is enabled; the form shows shared LP fields (no Grid) + the
   pursuit/follow-on/grid-builder groups. OK adds an "LPP" card; double-click reopens it populated; save/
   reload keeps it.
3. No "coming soon" placeholders remain in the type picker.
4. Firewall passes.
