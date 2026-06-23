# Brief — Loadpull UI 04: serialize Loadpull & Loadpull-Pursuit analyses (`.csch` / clipboard / `.canl`)

**Goal:** Teach the single shared analysis encoder to round-trip the **already-existing**
`LoadpullAnalysis` and `LoadpullPursuitAnalysis` model types. This is headless, framework-free, fully
unit-testable, and has **no UI** — it is the foundation the authoring forms (briefs 05–06) build on. Do this
first so the editor work can immediately persist what it produces.

**Depends on:** nothing (independent of the Tuner track).

**Reads with:** `docs/design/analysis-authoring.md` §3 + §5.4 (the ONE serialization principle), and the
existing code: `src/Ui/Schematic/AnalysisSerialization.cs` (the encoder you extend) and
`src/Core/Design/Analysis.cs` (the `LoadpullAnalysis` / `LoadpullPursuitAnalysis` fields — already defined,
do not change them).

## Context: the model already exists; only serialization is missing

`Analysis.cs` already defines `LoadpullAnalysis` and `LoadpullPursuitAnalysis` with every field as a raw
expression string (e.g. `ToneExpr`, `LoadTunerName`, `SourceTunerName`, `GridPath`, `PinStartExpr`,
`PinMaxExpr`, `SweepExpr`, `TuneHarmExpr`, `CompressionExpr`, `GainTypeExpr`, `PinStepExpr`, `TickleExpr`,
`MaxIterExpr`, …; and for pursuit: `EffTypeExpr`, `ZsourceOBOExpr`, `SearchMethodExpr`, `OutputGridPath`,
`Vswr1Expr`, `Vswr1ResolutionExpr`, `Vswr2Expr`, `Vswr2ResolutionExpr`, `KeepNonconvergingExpr`,
`NonconvergentVswrExpr`, `CreateLoadpullResultExpr`, `LoadpullResultZsourceExpr`). Read both classes and copy
the field names **exactly**.

But `AnalysisSerialization` only handles `"dc"`/`"sp"`/`"hb"`/`"sweep"`. Its header comment even says
loadpull/pursuit are intentionally omitted and unknown tags are skipped. We now add `"lp"` and `"lpp"`.

## Changes (all in `src/Ui/Schematic/AnalysisSerialization.cs`)

### 1 — DTO fields on `CschAnalysis`
`CschAnalysis` is a flat polymorphic DTO (all variants' fields inline, nullable, `WhenWritingNull`). Add the
loadpull + pursuit fields as nullable strings, grouped and commented like the existing `// ── HB ──` block.
Reuse `ToneExpr`/`MaxHarmonicExpr`/`GuardHarmonicExpr`/`TolExpr`/`FFTOverSampleExpr`/`DriveSteppingExpr`/
`MaxIterExpr` **only if** their semantics match (they do — same raw-expression strings); to avoid coupling LP
to HB's fields, prefer **dedicated** LP-prefixed properties so the two never drift. Recommended explicit set:

```csharp
// ── LP / LPP (loadpull + loadpull_pursuit) ─────────────────────────────────
public string?  LpLoadTunerName    { get; set; }
public string?  LpSourceTunerName  { get; set; }
public string?  LpGridPath         { get; set; }   // LP only (LPP generates its grid)
public string?  LpToneExpr         { get; set; }
public string?  LpToneUnit         { get; set; }   // null/absent → "Hz" (see note below)
public string?  LpPinStartExpr     { get; set; }
public string?  LpPinMaxExpr       { get; set; }
public string?  LpPinStepExpr      { get; set; }
public string?  LpMaxHarmonicExpr  { get; set; }
public string?  LpSweepExpr        { get; set; }   // "Load" | "Source"
public string?  LpTuneHarmExpr     { get; set; }
public string?  LpCompressionExpr  { get; set; }
public string?  LpGainTypeExpr     { get; set; }   // "Gt" | "Gp"
public string?  LpTickleExpr       { get; set; }   // dBm or "off"
public string?  LpMaxIterExpr      { get; set; }
public string?  LpFftOverSampleExpr{ get; set; }
public string?  LpTolExpr          { get; set; }
public string?  LpDriveSteppingExpr{ get; set; }
public string?  LpGuardHarmonicExpr{ get; set; }

// ── LPP-only pursuit keys ──────────────────────────────────────────────────
public string?  LppEffType              { get; set; }   // "DE" | "PAE"
public string?  LppZsourceOBO           { get; set; }
public string?  LppSearchMethod         { get; set; }   // "SteepestAscent" | "IteratedQuadratic"
public string?  LppOutputGridPath       { get; set; }   // null = no file
public string?  LppVswr1                { get; set; }
public string?  LppVswr1Resolution      { get; set; }
public string?  LppVswr2                { get; set; }
public string?  LppVswr2Resolution      { get; set; }
public string?  LppKeepNonconverging    { get; set; }
public string?  LppNonconvergentVswr    { get; set; }
public string?  LppCreateLoadpullResult { get; set; }
public string?  LppLoadpullResultZsource{ get; set; }   // "MXE" | "MXP" | "None"
```
**Note on `ToneUnit`:** `LoadpullAnalysis` stores `ToneExpr` only — there is **no `ToneUnit` field** on the
model (unlike HB). The model's `ToneExpr` is resolved by the engine with its own unit handling. So you have
two choices: (a) store the LP tone as a single combined expression string (no separate unit) to match the
model exactly — simplest, recommended; or (b) keep a UI-side `LpToneUnit` for editor convenience and bake it
into `ToneExpr` when building the model in brief 05. **Recommend (a):** `LpToneExpr` is the whole tone
expression (e.g. `"2e9"` or `"2*f0"`), no `LpToneUnit`. Drop `LpToneUnit` from the list above unless brief 05
finds it genuinely needed; if kept, it is editor-only and never on the model. Keep this brief consistent with
whatever brief 05 expects — they are co-designed.

`SourceDirectory` on the model (used to resolve relative `Grid`/`OutputGrid` paths) is set by the reader at
extraction time, **not** serialized (it is environment-specific). Do not add a DTO field for it.

### 2 — `ToDto` cases
Add two `switch` arms in `ToDto(Analysis a)`:
```csharp
LoadpullAnalysis lp => new CschAnalysis
{
    Type              = "lp",
    Name              = lp.Name,
    Enabled           = lp.Enabled,
    LpLoadTunerName   = lp.LoadTunerName,
    LpSourceTunerName = lp.SourceTunerName,
    LpGridPath        = lp.GridPath,
    LpToneExpr        = lp.ToneExpr,
    LpPinStartExpr    = lp.PinStartExpr,
    LpPinMaxExpr      = lp.PinMaxExpr,
    LpPinStepExpr     = lp.PinStepExpr,
    LpMaxHarmonicExpr = lp.MaxHarmonicExpr,
    LpSweepExpr       = lp.SweepExpr,
    LpTuneHarmExpr    = lp.TuneHarmExpr,
    LpCompressionExpr = lp.CompressionExpr,
    LpGainTypeExpr    = lp.GainTypeExpr,
    LpTickleExpr      = lp.TickleExpr,
    LpMaxIterExpr     = lp.MaxIterExpr,
    LpFftOverSampleExpr = lp.FFTOverSampleExpr,
    LpTolExpr         = lp.TolExpr,
    LpDriveSteppingExpr = lp.DriveSteppingExpr,
    LpGuardHarmonicExpr = lp.GuardHarmonicExpr,
},

LoadpullPursuitAnalysis lpp => new CschAnalysis
{
    Type              = "lpp",
    Name              = lpp.Name,
    Enabled           = lpp.Enabled,
    LpLoadTunerName   = lpp.LoadTunerName,
    LpSourceTunerName = lpp.SourceTunerName,
    LpToneExpr        = lpp.ToneExpr,
    LpPinStartExpr    = lpp.PinStartExpr,
    LpPinMaxExpr      = lpp.PinMaxExpr,
    LpPinStepExpr     = lpp.PinStepExpr,
    LpMaxHarmonicExpr = lpp.MaxHarmonicExpr,
    LpSweepExpr       = lpp.SweepExpr,
    LpTuneHarmExpr    = lpp.TuneHarmExpr,
    LpCompressionExpr = lpp.CompressionExpr,
    LpGainTypeExpr    = lpp.GainTypeExpr,
    LpTickleExpr      = lpp.TickleExpr,
    LpMaxIterExpr     = lpp.MaxIterExpr,
    LpFftOverSampleExpr = lpp.FFTOverSampleExpr,
    LpTolExpr         = lpp.TolExpr,
    LpDriveSteppingExpr = lpp.DriveSteppingExpr,
    LpGuardHarmonicExpr = lpp.GuardHarmonicExpr,
    LppEffType              = lpp.EffTypeExpr,
    LppZsourceOBO           = lpp.ZsourceOBOExpr,
    LppSearchMethod         = lpp.SearchMethodExpr,
    LppOutputGridPath       = lpp.OutputGridPath,
    LppVswr1                = lpp.Vswr1Expr,
    LppVswr1Resolution      = lpp.Vswr1ResolutionExpr,
    LppVswr2                = lpp.Vswr2Expr,
    LppVswr2Resolution      = lpp.Vswr2ResolutionExpr,
    LppKeepNonconverging    = lpp.KeepNonconvergingExpr,
    LppNonconvergentVswr    = lpp.NonconvergentVswrExpr,
    LppCreateLoadpullResult = lpp.CreateLoadpullResultExpr,
    LppLoadpullResultZsource= lpp.LoadpullResultZsourceExpr,
},
```
Place these arms **before** the `_ => …` fallback. (The fallback that emits `Type="?"` for unknown types
stays.)

### 3 — `FromDto` cases
Add two arms in `FromDto(CschAnalysis dto)`, before the `_ => null` fallback. Use the model's init-only
properties; fall back to the model's documented defaults when a DTO field is null (so an old/short file still
loads). Example for LP:
```csharp
"lp" => new LoadpullAnalysis(dto.Name)
{
    Enabled         = dto.Enabled,
    LoadTunerName   = dto.LpLoadTunerName   ?? "",
    SourceTunerName = dto.LpSourceTunerName ?? "",
    GridPath        = dto.LpGridPath        ?? "",
    ToneExpr        = dto.LpToneExpr        ?? "0",
    PinStartExpr    = dto.LpPinStartExpr    ?? "-20",
    PinMaxExpr      = dto.LpPinMaxExpr      ?? "10",
    PinStepExpr     = dto.LpPinStepExpr     ?? "1",
    MaxHarmonicExpr = dto.LpMaxHarmonicExpr ?? "5",
    SweepExpr       = dto.LpSweepExpr       ?? "Load",
    TuneHarmExpr    = dto.LpTuneHarmExpr    ?? "1",
    CompressionExpr = dto.LpCompressionExpr ?? "3",
    GainTypeExpr    = dto.LpGainTypeExpr    ?? "Gt",
    TickleExpr      = dto.LpTickleExpr      ?? "-50",
    MaxIterExpr     = dto.LpMaxIterExpr     ?? "100",
    FFTOverSampleExpr = dto.LpFftOverSampleExpr ?? "1",
    TolExpr         = dto.LpTolExpr         ?? "1e-6",
    DriveSteppingExpr = dto.LpDriveSteppingExpr ?? "IfNecessary",
    GuardHarmonicExpr = dto.LpGuardHarmonicExpr ?? "0",
},
```
…and the analogous `"lpp"` arm filling `LoadpullPursuitAnalysis` (all LP fields + the `Lpp*` pursuit keys;
`OutputGridPath = dto.LppOutputGridPath` stays nullable — null means "no file"). Use the exact defaults from
`Analysis.cs`.

### 4 — Update the header comment
The file header says the discriminator is `"dc"/"sp"/"hb"` and LP/LPP are omitted. Update it to include
`"lp"/"lpp"` and note that authoring is now supported (briefs 05–06).

## Tests
Add round-trip tests in `tests/Ui.Tests/AnalysisSerializationTests.cs` (mirror the existing 19 there):
- LP: build a `LoadpullAnalysis` with non-default values for every field, `ToDto` → `FromDto`, assert all
  fields equal (and `Enabled` preserved).
- LPP: same for `LoadpullPursuitAnalysis`, including `OutputGridPath = null` (no file) and a non-null case.
- Clipboard payload round-trip via `Serialize`/`Deserialize` with a list mixing DC, SP, HB, LP, LPP.
- `.canl` round-trip via `SerializeCanl`/`DeserializeCanl` containing an LP and an LPP.
- Forward-compat: a JSON blob with `Type="lp"` but several fields absent → loads with model defaults, no
  throw.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green (existing + new).
2. No UI touched; firewall unaffected. This brief is pure serialization plumbing.
3. Sanity: a `.csch` saved with an LP/LPP analysis (once briefs 05–06 author one) reloads identically — the
   `.csch` path uses `ToDto`/`FromDto` automatically via `SchematicPersistence`, so no further `.csch` wiring
   is needed here (confirm `SchematicPersistence.ToFileModel/FromFileModel` already routes through
   `AnalysisSerialization.ToDto/FromDto` — it does, per analysis-authoring.md §7 step 2).
