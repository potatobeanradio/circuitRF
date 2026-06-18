# Sonnet Brief — Sweep revamp, Stage 1: persistence foundation (data + serialization only)

> First of three staged briefs (see `docs/design/parametric-sweep-ux.md`). **No UX change in this stage.**
> Goal: make the two persistence formats faithfully preserve (a) a sweep's compact Start/Stop/Step|Npts
> **Spec** and (b) each analysis's **Enabled** flag. This fixes "Start/Stop/Step gets converted to a list on
> save" and makes top-level Enabled actually take effect. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Background (already true — do not change)
- `ParametricSweepAnalysis` (src/Core/Design/Analysis.cs) already carries `SweepSpec? Spec`
  (`Start/Stop/StepOrCount/Mode{StepSize|PointCount}/Kind{Linear|Log}`), non-null when defined via
  Start/Stop/Step|Npts; null for an explicit values list. `SweepValues` is always the expanded array.
- The **`.cnl`** writer/reader already round-trip the Spec (compact `Var=/Start=/Stop=/Step=|Npts=/[log]/Inner=`
  vs list `Values=`). The only `.cnl` gap is `enabled`.
- The **`.csch`/`.canl`/clipboard** path already round-trips `Enabled` (CschAnalysis.Enabled) but **drops the
  Spec** — `CschAnalysis` stores only `PsaValues`. That's why a saved/templated/pasted sweep comes back as a
  list. This stage adds Spec to that DTO.

## Part A — `.cnl`: round-trip `enabled`

### A1. CnlWriter (`src/Core/Netlist/CnlWriter.cs`)
Emit ` enabled=false` on each analysis line when disabled (omit when enabled, to keep files clean). Do it
centrally in `Write` so every analysis type is covered, handling the multi-line S-param case:
```csharp
        // Typed analyses
        foreach (var analysis in tb.Analyses)
        {
            var text = FormatAnalysis(analysis);
            if (!analysis.Enabled)
                // Append to every \n-separated sub-line (S-param emits one line per segment).
                text = string.Join("\n", text.Split('\n').Select(l => l + " enabled=false"));
            sb.AppendLine(text);
        }
```
(Add `using System.Linq;` if not already present — it is used elsewhere in Core, confirm the file's usings.)

### A2. CnlReader (`src/Core/Netlist/CnlReader.cs`)
Each typed analysis is built in a `TryParse*Directive` from a token list or a `kv` dict. Add a shared helper
and set `.Enabled` from it in every typed parser:
```csharp
    // enabled=false → disabled; absent/anything else → enabled (default true).
    private static bool ParseEnabledToken(IReadOnlyList<string> tokens)
    {
        foreach (var t in tokens)
        {
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (t[..eq].Equals("enabled", StringComparison.OrdinalIgnoreCase))
                return !t[(eq + 1)..].Trim('"').Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }
```
Wire it in each parser (all already tokenize via `TokeniseLine`):
- **DC** (`TryParseDcDirective`): `result = new DcAnalysis(analysisName) { Enabled = ParseEnabledToken(tokens) };`
- **Parametric sweep** (`TryParseParametricSweepDirective`): after building `result`, set
  `result.Enabled = ParseEnabledToken(tokens);` (both the list and spec branches — set once after).
- **HB** (`TryParseHbDirective`): `result = new HarmonicBalanceAnalysis(...) { … };` → set
  `result.Enabled = ParseEnabledToken(tokens);` after construction.
- **S-param** (`TryParseSParamDirective`): set `result.Enabled = ParseEnabledToken(tokens);` before returning.
  (The multi-segment merge in `TryParseLine` already keeps `existing.Enabled` — the first segment's line wins,
  which is what A1 emits, so no change needed there.)
- **Loadpull / LoadpullPursuit**: set `.Enabled = ParseEnabledToken(tokens);` on the result too (consistency).

`enabled` is a reserved key — confirm no analysis type already uses a param literally named `enabled` (none do).
The S-param/sweep parsers collect unknown `key=value` into `kv`/`bare` and ignore extras, so a stray
`enabled=false` token won't break them; only our explicit handler reads it.

## Part B — `.csch`/`.canl`/clipboard: round-trip the sweep `Spec`
File: `src/Ui/Schematic/AnalysisSerialization.cs`.

### B1. Extend the DTO (`CschAnalysis`, the ParametricSweep region)
```csharp
    // ── ParametricSweep ───────────────────────────────────────────────────────
    public string?   PsaVarName        { get; set; }
    public double[]? PsaValues         { get; set; }   // list form (Spec == null)
    public string?   PsaInnerName      { get; set; }
    // Compact spec form — preserves Start/Stop/Step|Npts so it never flattens to a list.
    public SweepAxisMode? PsaMode        { get; set; }   // StepSize | PointCount (enum-as-string)
    public double?        PsaStart       { get; set; }
    public double?        PsaStop        { get; set; }
    public double?        PsaStepOrCount { get; set; }
    public SweepKind?     PsaKind        { get; set; }   // Linear | Log (enum-as-string)
```
(`SweepAxisMode`/`SweepKind` are in `CircuitRF.Core.Design`, already imported at the top of this file.)

### B2. `ToDto(Analysis)` — the `ParametricSweepAnalysis psa` arm
Prefer the Spec; only emit Values for true list sweeps:
```csharp
        ParametricSweepAnalysis psa => new CschAnalysis
        {
            Type         = "sweep",
            Name         = psa.Name,
            Enabled      = psa.Enabled,
            PsaVarName   = psa.SweepVarName,
            PsaInnerName = psa.InnerAnalysisName,
            // Spec form when present (Start/Stop/Step|Npts) — do NOT also write the expanded list.
            PsaMode        = psa.Spec?.Mode,
            PsaStart       = psa.Spec?.Start,
            PsaStop        = psa.Spec?.Stop,
            PsaStepOrCount = psa.Spec?.StepOrCount,
            PsaKind        = psa.Spec?.Kind,
            // List form only when there is no Spec.
            PsaValues    = psa.Spec is null && psa.SweepValues.Length > 0 ? psa.SweepValues : null,
        },
```

### B3. `FromDto(CschAnalysis)` — the `"sweep"` arm
Reconstruct a Spec'd sweep when the Spec fields are present; else fall back to the list. Relax the old guard
that required `PsaValues`:
```csharp
        "sweep" when dto.PsaVarName is not null && dto.PsaInnerName is not null
                     && dto.PsaMode is { } mode && dto.PsaStart is { } st
                     && dto.PsaStop is { } sp && dto.PsaStepOrCount is { } soc =>
            new ParametricSweepAnalysis(dto.Name, dto.PsaVarName,
                new SweepSpec(st, sp, soc, mode, dto.PsaKind ?? SweepKind.Linear),
                dto.PsaInnerName) { Enabled = dto.Enabled },

        "sweep" when dto.PsaVarName is not null && dto.PsaInnerName is not null
                     && dto.PsaValues is { Length: > 0 } =>
            new ParametricSweepAnalysis(dto.Name, dto.PsaVarName, dto.PsaValues, dto.PsaInnerName)
            { Enabled = dto.Enabled },
```
(Keep both arms; the Spec arm must come first. `SweepSpec` is in `CircuitRF.Core.Design`.)

## Tests
- **Core.Tests** (`Netlist/`): write a TestBench with a Spec'd `ParametricSweepAnalysis`
  (Start/Stop/Step and a separate Start/Stop/Npts) and a disabled DC + disabled sweep → `CnlWriter.Write` →
  `CnlReader.Read` → assert: the sweep's `Spec` is non-null with the same Mode/Start/Stop/StepOrCount/Kind
  (NOT flattened to a list), and `Enabled == false` survives on both the DC and the sweep. Add an
  enabled-default test (line without `enabled=` → `Enabled == true`).
- **Ui.Tests** (AnalysisSerialization): round-trip a Spec'd sweep through `Serialize`/`Deserialize`,
  through `SerializeCanl`/`DeserializeCanl`, and through `ToDto`/`FromDto` → assert `Spec` preserved
  (Mode/Start/Stop/StepOrCount/Kind) and `Enabled` preserved. Add one explicit-list sweep → still round-trips
  as a list (Spec null, Values intact).

## Gate (manual, after build)
Create a sweep as Start/Stop/Step, run, save the schematic, close & reopen the workspace → the sweep still
shows Start/Stop/Step (not a list). Copy the analysis and paste into another schematic → still Start/Stop/Step.
Save it as a template (.canl), insert it → still Start/Stop/Step. Disable the base DC in the editor, run via
the .cnl path → it is skipped (top-level disable now reaches the dispatcher; full inner/collapse semantics land
in Stage 2).

## On completion
Note in `src/Core/Netlist/CLAUDE.md` (or the nearest CLAUDE.md): analyses round-trip `enabled=false` in `.cnl`
(default true when absent); a parametric sweep's compact Spec (Start/Stop/Step|Npts) now round-trips through
`.cnl` AND `.csch`/`.canl`/clipboard — it only serializes as a Values list when the user authored an explicit
list. Stage 2 (dispatcher Enabled semantics) and Stage 3 (unified editor) follow.
