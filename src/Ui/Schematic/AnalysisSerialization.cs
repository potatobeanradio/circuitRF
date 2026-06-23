using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  Analysis serialization — the ONE encoder (§5.4).
//  This is the single shared serializer for Analysis + Measurement lists.
//  Three destinations reuse it: .csch (Layer 2), clipboard (step 5), .canl (step 5).
//  Never write a second encoder; always use AnalysisSerialization.
//
//  Conventions mirror SchematicPersistence:
//    - System.Text.Json, enum-as-string (JsonStringEnumConverter in options)
//    - WhenWritingNull on all nullable/variant fields
//    - Id never persisted
//    - format_version reject-on-mismatch (enforced by the CschFile wrapper)
//
//  Type discriminator: CschAnalysis.Type is "dc" / "sp" / "hb" / "sweep" / "lp" / "lpp".
//  Loadpull ("lp") and Loadpull-Pursuit ("lpp") round-trip here (authoring forms: briefs 05-06).
//  Their tone serializes as LpToneExpr + LpToneUnit — the same coefficient+unit pair HB uses, so a
//  VAR tone with or without a unit resolves correctly via the var-unit-wins rule (brief 04b).
//  Unknown Type tags are silently skipped on load — graceful forward-compat.
// ──────────────────────────────────────────────────────────────────────────────

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// One frequency-sweep segment.  Mirrors <see cref="FrequencySpec"/>.
/// Mode/Kind stored as enum-as-string (JsonStringEnumConverter in options).
/// </summary>
public sealed class CschFrequencySpec
{
    public string      StartExpr  { get; set; } = "";
    public string      StopExpr   { get; set; } = "";
    /// <summary>Step-size expression.  Null / absent in PointCount mode.</summary>
    public string?     StepExpr   { get; set; }
    /// <summary>Point count.  Null / absent in StepSize mode.</summary>
    public int?        NumPoints  { get; set; }
    public FreqSpecMode Mode      { get; set; }
    public SweepKind   Kind       { get; set; }
    /// <summary>Unit for StartExpr.  Null / absent → "Hz" (back-compat).</summary>
    public string?     StartUnit  { get; set; }
    /// <summary>Unit for StopExpr.  Null / absent → "Hz" (back-compat).</summary>
    public string?     StopUnit   { get; set; }
    /// <summary>Unit for StepExpr.  Null / absent → "Hz" (back-compat).</summary>
    public string?     StepUnit   { get; set; }
}

/// <summary>
/// One analysis — polymorphic flat DTO discriminated by <see cref="Type"/>.
/// Variant-specific fields are nullable and omitted (WhenWritingNull) when not applicable.
/// Mirrors <see cref="CschCanvasObject"/>: one flat class, discriminator field, all variants inline.
/// </summary>
public sealed class CschAnalysis
{
    /// <summary>Type tag: "dc" | "sp" | "hb" | "sweep" | "lp" | "lpp".  Unknown tags are skipped on load.</summary>
    public string Type    { get; set; } = "";
    public string Name    { get; set; } = "";
    /// <summary>False = skip at run time (VendorC "enabled" pattern). Defaults true; absent on old files → true.</summary>
    public bool   Enabled { get; set; } = true;

    // ── SP ────────────────────────────────────────────────────────────────────
    public List<CschFrequencySpec>? Sweeps { get; set; }

    // ── HB ────────────────────────────────────────────────────────────────────
    public string?   ToneExpr          { get; set; }
    /// <summary>Unit for ToneExpr.  Null / absent → "Hz" (back-compat).</summary>
    public string?   ToneUnit          { get; set; }
    public string?   NumFreqsExpr      { get; set; }
    public string[]? ToneExprs         { get; set; }
    /// <summary>Units for ToneExprs (parallel).  Null / absent → all "Hz" (back-compat).</summary>
    public string[]? ToneUnits         { get; set; }
    public string?   MaxMixOrderExpr   { get; set; }
    public string?   MaxHarmonicExpr   { get; set; }
    public string?   FFTOverSampleExpr { get; set; }
    public string?   TolExpr           { get; set; }
    public string?   DriveSteppingExpr { get; set; }
    public string?   GuardHarmonicExpr { get; set; }
    public string?   LambdaExpr        { get; set; }
    public string?   MaxIterExpr       { get; set; }
    public string?   SweepVarName      { get; set; }
    public string?   SweepStartExpr    { get; set; }
    public string?   SweepStopExpr     { get; set; }
    public string?   SweepStepExpr     { get; set; }

    // ── ParametricSweep ───────────────────────────────────────────────────────
    /// <summary>The variable name swept (parametric sweep type only).</summary>
    public string?          PsaVarName        { get; set; }
    /// <summary>Explicit double array of sweep values. Null when the sweep is defined by Spec fields.</summary>
    public double[]?        PsaValues         { get; set; }
    /// <summary>Name of the inner analysis this sweep wraps (parametric sweep type only).</summary>
    public string?          PsaInnerName      { get; set; }
    // Spec form — all four required to reconstruct a SweepSpec (PsaKind defaults to Linear if absent).
    public SweepAxisMode?   PsaMode           { get; set; }
    public double?          PsaStart          { get; set; }
    public double?          PsaStop           { get; set; }
    public double?          PsaStepOrCount    { get; set; }
    public SweepKind?       PsaKind           { get; set; }
    /// <summary>Unit for sweep range.  Null / absent → base units (back-compat).</summary>
    public string?          PsaUnit           { get; set; }

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
}

/// <summary>
/// One measurement expression.  Mirrors <see cref="Measurement"/>.
/// </summary>
public sealed class CschMeasurement
{
    public string  Name       { get; set; } = "";
    public string  Expression { get; set; } = "";
    public string? Unit       { get; set; }
}

// ── Wrapper used by the standalone (clipboard / .canl) serializer ─────────────

file sealed class CschAnalysisBlock
{
    public List<CschAnalysis>    Analyses     { get; set; } = [];
    public List<CschMeasurement> Measurements { get; set; } = [];
}

/// <summary>
/// The <c>.canl</c> file format: a named multi-analysis bundle.
/// Contains the same <see cref="CschAnalysis"/> / <see cref="CschMeasurement"/> DTOs
/// as the clipboard payload (§5.4 — one serialization, shared across all three destinations).
/// </summary>
public sealed class CanlFile
{
    public string  Name        { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    public List<CschAnalysis>    Analyses     { get; set; } = [];
    public List<CschMeasurement> Measurements { get; set; } = [];
}

// ── The ONE encoder ───────────────────────────────────────────────────────────

/// <summary>
/// The single shared encoder for <see cref="Analysis"/> + <see cref="Measurement"/> lists.
/// <para>
/// Three use-sites (§5.4) — all reuse these same static methods:
/// <list type="bullet">
///   <item><see cref="ToDto"/> / <see cref="FromDto"/> — called by <c>SchematicPersistence</c> to
///     populate / read <c>CschFile.Analyses</c> + <c>CschFile.Measurements</c>.</item>
///   <item><see cref="Serialize"/> / <see cref="Deserialize"/> — produces the standalone JSON payload
///     for clipboard (step 5) and <c>.canl</c> templates (step 5).</item>
/// </list>
/// </para>
/// Framework-free (no Avalonia).  Headless-testable.
/// </summary>
public static class AnalysisSerialization
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Standalone JSON (clipboard / .canl) ───────────────────────────────────

    /// <summary>
    /// Produces a <c>.canl</c> template file JSON: name + optional description +
    /// the same analyses/measurements payload as the clipboard format (§5.4).
    /// </summary>
    public static string SerializeCanl(
        string name, string? description,
        IReadOnlyList<Analysis>    analyses,
        IReadOnlyList<Measurement> measurements)
    {
        var file = new CanlFile
        {
            Name         = name,
            Description  = string.IsNullOrWhiteSpace(description) ? null : description,
            Analyses     = analyses.Select(ToDto).ToList(),
            Measurements = measurements.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(file, _jsonOpts);
    }

    /// <summary>
    /// Parses a <c>.canl</c> template file.  Unknown type tags are silently skipped.
    /// </summary>
    public static (string Name, string? Description, List<Analysis> Analyses, List<Measurement> Measurements)
        DeserializeCanl(string json)
    {
        var file = JsonSerializer.Deserialize<CanlFile>(json, _jsonOpts) ?? new CanlFile();
        return (
            file.Name,
            file.Description,
            file.Analyses
                .Select(FromDto)
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList(),
            file.Measurements.Select(FromDto).ToList()
        );
    }

    /// <summary>
    /// Produces the standalone JSON payload used by the clipboard and <c>.canl</c> template files.
    /// The payload is a <c>{ analyses: [...], measurements: [...] }</c> object.
    /// </summary>
    public static string Serialize(
        IReadOnlyList<Analysis>    analyses,
        IReadOnlyList<Measurement> measurements)
    {
        var block = new CschAnalysisBlock
        {
            Analyses     = analyses.Select(ToDto).ToList(),
            Measurements = measurements.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(block, _jsonOpts);
    }

    /// <summary>
    /// Parses the standalone JSON payload (clipboard / <c>.canl</c>).
    /// Unknown type tags are silently skipped; null / malformed input → empty lists.
    /// </summary>
    public static (List<Analysis> Analyses, List<Measurement> Measurements) Deserialize(string json)
    {
        var block = JsonSerializer.Deserialize<CschAnalysisBlock>(json, _jsonOpts)
                    ?? new CschAnalysisBlock();
        return (
            block.Analyses
                 .Select(FromDto)
                 .Where(a => a is not null)
                 .Select(a => a!)
                 .ToList(),
            block.Measurements.Select(FromDto).ToList()
        );
    }

    // ── Domain → DTO ─────────────────────────────────────────────────────────

    public static CschAnalysis ToDto(Analysis a) => a switch
    {
        DcAnalysis => new CschAnalysis { Type = "dc", Name = a.Name, Enabled = a.Enabled },

        SParameterAnalysis sp => new CschAnalysis
        {
            Type    = "sp",
            Name    = sp.Name,
            Enabled = sp.Enabled,
            Sweeps  = sp.Sweeps.Select(ToDto).ToList(),
        },

        HarmonicBalanceAnalysis hb => new CschAnalysis
        {
            Type              = "hb",
            Name              = hb.Name,
            Enabled           = hb.Enabled,
            ToneExpr          = hb.ToneExpr,
            ToneUnit          = hb.ToneUnit != "Hz" ? hb.ToneUnit : null,
            NumFreqsExpr      = hb.NumFreqsExpr,
            ToneExprs         = hb.ToneExprs.Length > 0 ? hb.ToneExprs : null,
            ToneUnits         = hb.ToneUnits.Length > 0 ? hb.ToneUnits : null,
            MaxMixOrderExpr   = hb.MaxMixOrderExpr,
            MaxHarmonicExpr   = hb.MaxHarmonicExpr,
            FFTOverSampleExpr = hb.FFTOverSampleExpr,
            TolExpr           = hb.TolExpr,
            DriveSteppingExpr = hb.DriveSteppingExpr,
            GuardHarmonicExpr = hb.GuardHarmonicExpr,
            LambdaExpr        = hb.LambdaExpr,
            MaxIterExpr       = hb.MaxIterExpr,
#pragma warning disable CS0618
            SweepVarName      = hb.SweepVarName,
            SweepStartExpr    = hb.SweepStartExpr,
            SweepStopExpr     = hb.SweepStopExpr,
            SweepStepExpr     = hb.SweepStepExpr,
#pragma warning restore CS0618
        },

        ParametricSweepAnalysis psa => new CschAnalysis
        {
            Type           = "sweep",
            Name           = psa.Name,
            Enabled        = psa.Enabled,
            PsaVarName     = psa.SweepVarName,
            PsaInnerName   = psa.InnerAnalysisName,
            PsaMode        = psa.Spec?.Mode,
            PsaStart       = psa.Spec?.Start,
            PsaStop        = psa.Spec?.Stop,
            PsaStepOrCount = psa.Spec?.StepOrCount,
            PsaKind        = psa.Spec?.Kind,
            PsaUnit        = !string.IsNullOrEmpty(psa.Spec?.Unit) ? psa.Spec.Unit : null,
            PsaValues      = psa.Spec is null && psa.SweepValues.Length > 0 ? psa.SweepValues : null,
        },

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

        // Unknown / v2 types: preserve Type tag + Name so a future version can round-trip them.
        _ => new CschAnalysis { Type = "?", Name = a.Name, Enabled = a.Enabled },
    };

    public static CschFrequencySpec ToDto(FrequencySpec fs) => new()
    {
        StartExpr = fs.StartExpr,
        StopExpr  = fs.StopExpr,
        StepExpr  = fs.Mode == FreqSpecMode.StepSize ? fs.StepExpr : null,
        NumPoints = fs.Mode == FreqSpecMode.PointCount ? fs.NumPoints : null,
        Mode      = fs.Mode,
        Kind      = fs.Kind,
        StartUnit = fs.StartUnit != "Hz" ? fs.StartUnit : null,
        StopUnit  = fs.StopUnit  != "Hz" ? fs.StopUnit  : null,
        StepUnit  = fs.StepUnit  != "Hz" ? fs.StepUnit  : null,
    };

    public static CschMeasurement ToDto(Measurement m) => new()
    {
        Name       = m.Name,
        Expression = m.Expression,
        Unit       = m.Unit,
    };

    // ── DTO → Domain ─────────────────────────────────────────────────────────

    /// <summary>Returns null for unknown type tags (silently skipped by caller).</summary>
    public static Analysis? FromDto(CschAnalysis dto) => dto.Type switch
    {
        "dc" => new DcAnalysis(dto.Name) { Enabled = dto.Enabled },

        "sp" when dto.Sweeps is { Count: > 0 } =>
            new SParameterAnalysis(dto.Name, dto.Sweeps.Select(FromDto).ToList())
            { Enabled = dto.Enabled },

        "hb" => new HarmonicBalanceAnalysis(dto.Name)
        {
            Enabled           = dto.Enabled,
            ToneExpr          = dto.ToneExpr          ?? "0",
            ToneUnit          = dto.ToneUnit           ?? "Hz",
            NumFreqsExpr      = dto.NumFreqsExpr      ?? "1",
            ToneExprs         = dto.ToneExprs          ?? [],
            ToneUnits         = dto.ToneUnits          ?? [],
            MaxMixOrderExpr   = dto.MaxMixOrderExpr   ?? "5",
            MaxHarmonicExpr   = dto.MaxHarmonicExpr   ?? "7",
            FFTOverSampleExpr = dto.FFTOverSampleExpr ?? "1",
            TolExpr           = dto.TolExpr           ?? "1e-6",
            DriveSteppingExpr = dto.DriveSteppingExpr ?? "IfNecessary",
            GuardHarmonicExpr = dto.GuardHarmonicExpr ?? "0",
            LambdaExpr        = dto.LambdaExpr        ?? "1",
            MaxIterExpr       = dto.MaxIterExpr        ?? "100",
#pragma warning disable CS0618
            SweepVarName      = dto.SweepVarName,
            SweepStartExpr    = dto.SweepStartExpr,
            SweepStopExpr     = dto.SweepStopExpr,
            SweepStepExpr     = dto.SweepStepExpr,
#pragma warning restore CS0618
        },

        "sweep" when dto.PsaVarName is not null && dto.PsaInnerName is not null
                     && dto.PsaMode is { } mode && dto.PsaStart is { } st
                     && dto.PsaStop is { } sp && dto.PsaStepOrCount is { } soc =>
            new ParametricSweepAnalysis(dto.Name, dto.PsaVarName,
                new SweepSpec(st, sp, soc, mode, dto.PsaKind ?? SweepKind.Linear, dto.PsaUnit ?? ""),
                dto.PsaInnerName) { Enabled = dto.Enabled },

        "sweep" when dto.PsaVarName is not null && dto.PsaInnerName is not null
                     && dto.PsaValues is { Length: > 0 } =>
            new ParametricSweepAnalysis(dto.Name, dto.PsaVarName, dto.PsaValues, dto.PsaInnerName)
            { Enabled = dto.Enabled },

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

        "lpp" => new LoadpullPursuitAnalysis(dto.Name)
        {
            Enabled         = dto.Enabled,
            LoadTunerName   = dto.LpLoadTunerName   ?? "",
            SourceTunerName = dto.LpSourceTunerName ?? "",
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
            EffTypeExpr               = dto.LppEffType              ?? "DE",
            ZsourceOBOExpr            = dto.LppZsourceOBO           ?? "5",
            SearchMethodExpr          = dto.LppSearchMethod         ?? "SteepestAscent",
            OutputGridPath            = dto.LppOutputGridPath,   // null = no file
            Vswr1Expr                 = dto.LppVswr1               ?? "1.5",
            Vswr1ResolutionExpr       = dto.LppVswr1Resolution     ?? "4",
            Vswr2Expr                 = dto.LppVswr2               ?? "3",
            Vswr2ResolutionExpr       = dto.LppVswr2Resolution     ?? "4",
            KeepNonconvergingExpr     = dto.LppKeepNonconverging   ?? "false",
            NonconvergentVswrExpr     = dto.LppNonconvergentVswr   ?? "1.05",
            CreateLoadpullResultExpr  = dto.LppCreateLoadpullResult ?? "true",
            LoadpullResultZsourceExpr = dto.LppLoadpullResultZsource ?? "MXE",
        },

        // Unknown type tag (e.g. "loadpull") or empty/malformed: skip gracefully.
        _ => null,
    };

    public static FrequencySpec FromDto(CschFrequencySpec dto)
    {
        string su = dto.StartUnit ?? "Hz";
        string eu = dto.StopUnit  ?? "Hz";
        return dto.Mode == FreqSpecMode.PointCount && dto.NumPoints is int n
            ? new FrequencySpec(dto.StartExpr, dto.StopExpr, n,                dto.Kind, su, eu)
            : new FrequencySpec(dto.StartExpr, dto.StopExpr, dto.StepExpr ?? "0", dto.Kind, su, eu,
                                dto.StepUnit ?? "Hz");
    }

    public static Measurement FromDto(CschMeasurement dto) =>
        new(dto.Name, dto.Expression, dto.Unit);
}
