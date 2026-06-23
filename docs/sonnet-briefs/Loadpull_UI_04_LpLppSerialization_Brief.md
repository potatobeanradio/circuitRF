# Brief — Loadpull UI 04: serialize Loadpull & Loadpull-Pursuit analyses (`.csch` / clipboard / `.canl`)

**Goal:** Teach the single shared analysis encoder to round-trip the **already-existing**
`LoadpullAnalysis` and `LoadpullPursuitAnalysis` model types (including the `ToneUnit` field added in brief
04b). Headless, framework-free, fully unit-testable, **no UI** — the foundation the authoring forms
(briefs 05–06) build on.

**Depends on:** **brief 04b** (which adds `ToneUnit` to both loadpull models — required so the DTO can carry
it). Do 04b first.

**Reads with:** `docs/design/analysis-authoring.md` §3 + §5.4 (the ONE serialization principle),
`src/Ui/Schematic/AnalysisSerialization.cs` (the encoder you extend) and `src/Core/Design/Analysis.cs`
(the `LoadpullAnalysis` / `LoadpullPursuitAnalysis` fields — already defined; brief 04b adds `ToneUnit`).

## Context

`Analysis.cs` defines `LoadpullAnalysis` and `LoadpullPursuitAnalysis` with every field as a raw expression
string (`ToneExpr`, `LoadTunerName`, `SourceTunerName`, `GridPath`, `PinStartExpr`, `PinMaxExpr`,
`SweepExpr`, `TuneHarmExpr`, `CompressionExpr`, `GainTypeExpr`, `PinStepExpr`, `TickleExpr`, `MaxIterExpr`,
…; pursuit adds `EffTypeExpr`, `ZsourceOBOExpr`, `SearchMethodExpr`, `OutputGridPath`, `Vswr1Expr`,
`Vswr1ResolutionExpr`, `Vswr2Expr`, `Vswr2ResolutionExpr`, `KeepNonconvergingExpr`,
`NonconvergentVswrExpr`, `CreateLoadpullResultExpr`, `LoadpullResultZsourceExpr`). **Brief 04b adds a
`ToneUnit` field (default `"Hz"`) to both** — this brief serializes it. Read both classes and copy field
names exactly.

`AnalysisSerialization` only handles `"dc"`/`"sp"`/`"hb"`/`"sweep"` today; add `"lp"` and `"lpp"`.

## Tone is `ToneExpr` + `ToneUnit` (NOT a single combined string) — corrected

The tone must serialize as a **coefficient expression + a frequency unit**, exactly like HB
(`hb.ToneExpr` + `hb.ToneUnit`). This is what makes a VAR with *or without* a unit resolve correctly at run
time via the var-unit-wins rule (brief 04b). Do **not** collapse the tone into one combined expression — a
unitless VAR would then glitch to Hz. Mirror HB's serialization, which omits `ToneUnit` when it is the
default `"Hz"`.

## Changes (all in `src/Ui/Schematic/AnalysisSerialization.cs`)

### 1 — DTO fields on `CschAnalysis`
Add the LP + pursuit fields as nullable strings, grouped/commented like the existing `// ── HB ──` block.
Use dedicated `Lp*`/`Lpp*` properties so LP never couples to HB's fields:
```csharp
// ── LP / LPP (loadpull + loadpull_pursuit) ─────────────────────────────────
public string?  LpLoadTunerName    { get; set; }
public string?  LpSourceTunerName  { get; set; }
public string?  LpGridPath         { get; set; }   // LP only (LPP generates its grid)
public string?  LpToneExpr         { get; set; }
public string?  LpToneUnit         { get; set; }   // null/absent → "Hz" (var-unit-wins; brief 04b)
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
`SourceDirectory` on the model (resolves relative `Grid`/`OutputGrid` paths) is set by the reader at run
time — **not serialized**.

### 2 — `ToDto` cases (before the `_ => …` fallback)
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
    LpToneUnit        = lp.ToneUnit != "Hz" ? lp.ToneUnit : null,   // omit default (mirror HB)
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
    LpToneUnit        = lpp.ToneUnit != "Hz" ? lpp.ToneUnit : null,
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

### 3 — `FromDto` cases (before `_ => null`)
Fall back to model defaults when a DTO field is null (old/short files still load). **`ToneUnit` defaults
`"Hz"`** when absent:
```csharp
"lp" => new LoadpullAnalysis(dto.Name)
{
    Enabled         = dto.Enabled,
    LoadTunerName   = dto.LpLoadTunerName   ?? "",
    SourceTunerName = dto.LpSourceTunerName ?? "",
    GridPath        = dto.LpGridPath        ?? "",
    ToneExpr        = dto.LpToneExpr        ?? "0",
    ToneUnit        = dto.LpToneUnit        ?? "Hz",
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
…and the analogous `"lpp"` arm (all LP fields incl. `ToneUnit = dto.LpToneUnit ?? "Hz"` + the `Lpp*`
keys; `OutputGridPath = dto.LppOutputGridPath` stays nullable = "no file"). Use the exact defaults from
`Analysis.cs`.

### 4 — Header comment
Update the file header: discriminator now includes `"lp"/"lpp"`; authoring supported (briefs 05–06); the
tone serializes as `LpToneExpr` + `LpToneUnit` (var-unit-wins, brief 04b).

## Tests (`tests/Ui.Tests/AnalysisSerializationTests.cs`)
- LP round-trip with non-default values for every field — **including a non-`"Hz"` `ToneUnit`** (e.g.
  `ToneExpr="RFfreq"`, `ToneUnit="GHz"`) — `ToDto`→`FromDto`, assert all fields + `Enabled` equal.
- LP with default `ToneUnit="Hz"`: assert `LpToneUnit` is omitted (null) in the DTO and re-reads as `"Hz"`.
- LPP round-trip incl. `ToneUnit`, `OutputGridPath = null` and a non-null case.
- Clipboard `Serialize`/`Deserialize` and `.canl` `SerializeCanl`/`DeserializeCanl` with a list mixing DC,
  SP, HB, LP, LPP.
- Forward-compat: `Type="lp"` with fields absent → loads with model defaults (`ToneUnit="Hz"`), no throw.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green (existing + new).
2. No UI touched; firewall unaffected.
3. `.csch` round-trip is automatic via `SchematicPersistence.ToFileModel/FromFileModel` →
   `AnalysisSerialization.ToDto/FromDto` (confirm; no extra `.csch` wiring needed).
