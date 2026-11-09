using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Design;

namespace CircuitRF.Design.Schematic;

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
//  Type discriminator: CschAnalysis.Type is "dc" / "sp" / "hb" / "sweep" / "lp" / "lpp" / "fields" / "trl".
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
    /// <summary>Type tag: "dc" | "sp" | "hb" | "sweep" | "lp" | "lpp" | "fields" | "trl".  Unknown tags are skipped on load.</summary>
    public string Type    { get; set; } = "";
    public string Name    { get; set; } = "";
    /// <summary>False = skip at run time (VendorC "enabled" pattern). Defaults true; absent on old files → true.</summary>
    public bool   Enabled { get; set; } = true;
    /// <summary>brief-sdl-6 R-sdl6-1 — <c>"em"</c> when the analysis runs on the EM result of the schematic's own layout;
    /// absent (the circuit models) on every file written before it existed.</summary>
    public string? Model   { get; set; }
    /// <summary>The <c>.cem</c> a <c>model=em</c> analysis names when the layout has several; absent otherwise.</summary>
    public string? EmSetup { get; set; }

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
    /// <summary>The WSProbe stability-margin report threshold in dB, or "none" (WSP-9). Null /
    /// absent → the default, so a file written before this key existed reads unchanged.</summary>
    public string?   MarginThresholdExpr { get; set; }
    /// <summary>The NDF knob and its two passivation lists (brief-wsprobe-6). All null / absent on a
    /// file written before they existed, which reads back as the run that file always had.</summary>
    public string?   NdfExpr           { get; set; }
    public string?   PassiveVarsExpr   { get; set; }
    public string?   PassiveParamsExpr { get; set; }
    /// <summary>The S-parameter noise knob. Null / absent → off, as on a file written before it existed.</summary>
    public string?   NoiseExpr         { get; set; }
    /// <summary>The per-part noise share knob. Null / absent → off.</summary>
    public string?   NoiseContribExpr  { get; set; }
    // The small-signal (probe-tickle) sweep — WSP-5 R-wsp5-1. All null on a file written before it,
    // which reads back as "no small-signal sweep" and therefore as the run that file always had.
    public string?   SsStartExpr       { get; set; }
    public string?   SsStopExpr        { get; set; }
    public string?   SsStepExpr        { get; set; }
    public string?   SsNptsExpr        { get; set; }
    public string?   SsUnit            { get; set; }
    public string?   SsLogExpr         { get; set; }
    public string?   SsMaxHarmExpr     { get; set; }
    // Brief NZ-5 — HB noise keys; null is the default, so a document without them reads back unchanged.
    public string?   HbNoiseExpr         { get; set; }
    public string?   HbNoiseInExpr       { get; set; }
    public string?   HbNoiseOutExpr      { get; set; }
    public string?   HbNoiseSidebandExpr { get; set; }
    public string?   HbNoiseContribExpr  { get; set; }
    // brief-sar-9 — a fields analysis ("fields"): the EM block, the frequency, the drive port, the normalisation, averaging.
    public string?   FieldsBlock       { get; set; }
    public string?   FieldsFreqExpr    { get; set; }
    public string?   FieldsFreqUnit    { get; set; }
    public string?   FieldsDriveExpr   { get; set; }
    public string?   FieldsWeightsExpr { get; set; }
    public string?   FieldsNorm        { get; set; }
    public string?   FieldsAverageExpr { get; set; }
    // brief-de-4 — a TRL calibration ("trl"): the three standards' SnP instances, the reflect's side, and the two fixture files.
    public string?   TrlThru           { get; set; }
    public string?   TrlReflect        { get; set; }
    public string?   TrlLine           { get; set; }
    public string?   TrlReflectType    { get; set; }
    public string?   TrlFixtureA       { get; set; }
    public string?   TrlFixtureB       { get; set; }
    // Envelope (type "env") — the remaining env fields reuse HB ones (MaxHarmonicExpr, MaxMixOrderExpr, TolExpr).
    public string?   EnvStop           { get; set; }
    public string?   EnvStep           { get; set; }
    public string?   EnvAccuracy       { get; set; }
    /// <summary>Retired (brief ENV-MEAS-3): read so an old file's nodes join the recorded voltages, never written.</summary>
    public string?   EnvProbes         { get; set; }
    /// <summary>Retired (brief ENV-MEAS-1): read so an old file's value reaches the run's warning, never written.</summary>
    public string?   EnvZmeas          { get; set; }
    public string?   EnvSegments       { get; set; }
    public string?   EnvWarmup         { get; set; }
    public string?   EnvStart          { get; set; }
    public string?   EnvSaveWaveforms  { get; set; }
    /// <summary>Envelope mode (single / lattice); null = auto.</summary>
    public string?   EnvMode           { get; set; }
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
    // Brief NZ-6 — the loadpull noise keys, LP and LPP alike; null is the default, so a document without them reads back unchanged.
    public string?  LpNoiseExpr         { get; set; }
    public string?  LpNoiseFreqExpr     { get; set; }
    public string?  LpNoiseFreqUnit     { get; set; }
    public string?  LpNoiseInExpr       { get; set; }
    public string?  LpNoiseOutExpr      { get; set; }
    public string?  LpNoiseSidebandExpr { get; set; }

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

    /// <summary>An omitted key rather than an empty string, so a document that declared nothing
    /// writes nothing and reads back identical.</summary>
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The analysis as the <c>.csch</c> stores it, its <c>model=</c> choice included (brief-sdl-6 R-sdl6-1).</summary>
    public static CschAnalysis ToDto(Analysis a)
    {
        var dto = ToDtoCore(a);
        dto.Model   = a.Model == AnalysisModel.Em ? "em" : null;
        dto.EmSetup = NullIfBlank(a.EmSetup);
        return dto;
    }

    private static CschAnalysis ToDtoCore(Analysis a) => a switch
    {
        DcAnalysis => new CschAnalysis { Type = "dc", Name = a.Name, Enabled = a.Enabled },
        FieldsAnalysis f => new CschAnalysis
        {
            Type = "fields", Name = a.Name, Enabled = a.Enabled, FieldsBlock = f.Block, FieldsFreqExpr = f.FreqExpr,
            FieldsFreqUnit = f.FreqUnit, FieldsDriveExpr = f.DriveExpr,
            FieldsWeightsExpr = f.WeightsExpr.Length > 0 ? f.WeightsExpr : null, FieldsNorm = f.Norm, FieldsAverageExpr = f.AverageExpr,
        },

        TrlAnalysis t => new CschAnalysis
        {
            Type = "trl", Name = a.Name, Enabled = a.Enabled, TrlThru = t.Thru, TrlReflect = t.Reflect, TrlLine = t.Line,
            TrlReflectType = t.ReflectType,
            TrlFixtureA = t.FixtureA.Length > 0 ? t.FixtureA : null, TrlFixtureB = t.FixtureB.Length > 0 ? t.FixtureB : null,
        },

        SParameterAnalysis sp => new CschAnalysis
        {
            Type    = "sp",
            Name    = sp.Name,
            Enabled = sp.Enabled,
            Sweeps  = sp.Sweeps.Select(ToDto).ToList(),
            MarginThresholdExpr = sp.MarginThresholdExpr != Analysis.MarginThresholdDefault
                ? sp.MarginThresholdExpr : null,
            NdfExpr = sp.NdfExpr != Analysis.NdfDefault ? sp.NdfExpr : null,
            PassiveVarsExpr   = sp.PassiveVarsExpr.Length   > 0 ? sp.PassiveVarsExpr   : null,
            PassiveParamsExpr = sp.PassiveParamsExpr.Length > 0 ? sp.PassiveParamsExpr : null,
            NoiseExpr = sp.NoiseExpr != Analysis.NoiseDefault ? sp.NoiseExpr : null,
            NoiseContribExpr = sp.NoiseContribExpr != Analysis.NoiseContribDefault ? sp.NoiseContribExpr : null,
        },

        EnvelopeAnalysis env => new CschAnalysis
        {
            Type = "env", Name = env.Name, Enabled = env.Enabled,
            EnvStop = env.StopExpr, EnvStep = env.StepExpr, EnvAccuracy = env.Accuracy,
            EnvSegments = env.SegmentsExpr, EnvWarmup = env.WarmupExpr, EnvStart = env.StartExpr,
            EnvSaveWaveforms = env.SaveWaveforms, MaxHarmonicExpr = env.MaxHarmExpr, MaxMixOrderExpr = env.MaxMixOrderExpr,
            EnvMode = env.Envelope.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : env.Envelope,
            TolExpr = env.TolExpr,
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
            MarginThresholdExpr = hb.MarginThresholdExpr != Analysis.MarginThresholdDefault
                ? hb.MarginThresholdExpr : null,
            SsStartExpr       = NullIfBlank(hb.SsStartExpr),
            SsStopExpr        = NullIfBlank(hb.SsStopExpr),
            SsStepExpr        = NullIfBlank(hb.SsStepExpr),
            SsNptsExpr        = NullIfBlank(hb.SsNptsExpr),
            SsUnit            = hb.SsUnit != "Hz" ? hb.SsUnit : null,
            SsLogExpr         = NullIfBlank(hb.SsLogExpr),
            SsMaxHarmExpr     = NullIfBlank(hb.SsMaxHarmExpr),
            HbNoiseExpr         = hb.NoiseExpr != Analysis.NoiseDefault ? hb.NoiseExpr : null,
            HbNoiseInExpr       = hb.NoiseInExpr != HarmonicBalanceAnalysis.NoiseInDefault ? hb.NoiseInExpr : null,
            HbNoiseOutExpr      = hb.NoiseOutExpr != HarmonicBalanceAnalysis.NoiseOutDefault ? hb.NoiseOutExpr : null,
            HbNoiseSidebandExpr = hb.NoiseSidebandExpr != HarmonicBalanceAnalysis.NoiseSidebandDefault ? hb.NoiseSidebandExpr : null,
            HbNoiseContribExpr  = hb.NoiseContribExpr != Analysis.NoiseContribDefault ? hb.NoiseContribExpr : null,
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
            LpNoiseExpr         = lp.NoiseExpr != Analysis.NoiseDefault ? lp.NoiseExpr : null,
            LpNoiseFreqExpr     = NullIfBlank(lp.NoiseFreqExpr),
            LpNoiseFreqUnit     = lp.NoiseFreqUnit != "Hz" ? lp.NoiseFreqUnit : null,
            LpNoiseInExpr       = NullIfBlank(lp.NoiseInExpr),
            LpNoiseOutExpr      = NullIfBlank(lp.NoiseOutExpr),
            LpNoiseSidebandExpr = lp.NoiseSidebandExpr != HarmonicBalanceAnalysis.NoiseSidebandDefault ? lp.NoiseSidebandExpr : null,
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
            LpNoiseExpr         = lpp.NoiseExpr != Analysis.NoiseDefault ? lpp.NoiseExpr : null,
            LpNoiseFreqExpr     = NullIfBlank(lpp.NoiseFreqExpr),
            LpNoiseFreqUnit     = lpp.NoiseFreqUnit != "Hz" ? lpp.NoiseFreqUnit : null,
            LpNoiseInExpr       = NullIfBlank(lpp.NoiseInExpr),
            LpNoiseOutExpr      = NullIfBlank(lpp.NoiseOutExpr),
            LpNoiseSidebandExpr = lpp.NoiseSidebandExpr != HarmonicBalanceAnalysis.NoiseSidebandDefault ? lpp.NoiseSidebandExpr : null,
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
    public static Analysis? FromDto(CschAnalysis dto)
    {
        var a = FromDtoCore(dto);
        if (a is null) return null;
        a.Model   = Analysis.ParseModel(dto.Model);
        a.EmSetup = NullIfBlank(dto.EmSetup);
        return a;
    }

    private static Analysis? FromDtoCore(CschAnalysis dto) => dto.Type switch
    {
        "dc" => new DcAnalysis(dto.Name) { Enabled = dto.Enabled },
        "fields" => new FieldsAnalysis(dto.Name)
        {
            Enabled = dto.Enabled, Block = dto.FieldsBlock ?? "", FreqExpr = dto.FieldsFreqExpr ?? "", FreqUnit = dto.FieldsFreqUnit ?? "Hz",
            DriveExpr = dto.FieldsDriveExpr ?? "1", WeightsExpr = dto.FieldsWeightsExpr ?? "", Norm = dto.FieldsNorm ?? FieldsAnalysis.NormAccepted, AverageExpr = dto.FieldsAverageExpr ?? "yes",
        },

        "trl" => new TrlAnalysis(dto.Name)
        {
            Enabled = dto.Enabled, Thru = dto.TrlThru ?? "", Reflect = dto.TrlReflect ?? "", Line = dto.TrlLine ?? "",
            ReflectType = dto.TrlReflectType ?? TrlAnalysis.ReflectShort, FixtureA = dto.TrlFixtureA ?? "", FixtureB = dto.TrlFixtureB ?? "",
        },

        "sp" when dto.Sweeps is { Count: > 0 } =>
            new SParameterAnalysis(dto.Name, dto.Sweeps.Select(FromDto).ToList())
            {
                Enabled = dto.Enabled,
                MarginThresholdExpr = dto.MarginThresholdExpr ?? Analysis.MarginThresholdDefault,
                NdfExpr           = dto.NdfExpr           ?? Analysis.NdfDefault,
                PassiveVarsExpr   = dto.PassiveVarsExpr   ?? "",
                PassiveParamsExpr = dto.PassiveParamsExpr ?? "",
                NoiseExpr         = dto.NoiseExpr         ?? Analysis.NoiseDefault,
                NoiseContribExpr  = dto.NoiseContribExpr  ?? Analysis.NoiseContribDefault,
            },

        "env" => new EnvelopeAnalysis(dto.Name)
        {
            Enabled = dto.Enabled,
            StopExpr = dto.EnvStop ?? "100e-6", StepExpr = dto.EnvStep ?? "auto", Accuracy = dto.EnvAccuracy ?? "standard",
            Probes = dto.EnvProbes ?? "", ZmeasExpr = dto.EnvZmeas ?? "50", SegmentsExpr = dto.EnvSegments ?? "auto",
            WarmupExpr = dto.EnvWarmup ?? "auto", StartExpr = dto.EnvStart ?? "0", SaveWaveforms = dto.EnvSaveWaveforms ?? "yes",
            MaxHarmExpr = dto.MaxHarmonicExpr ?? "5", MaxMixOrderExpr = dto.MaxMixOrderExpr ?? "7", TolExpr = dto.TolExpr ?? "1e-6",
            Envelope = dto.EnvMode ?? "auto",
        },

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
            MarginThresholdExpr = dto.MarginThresholdExpr ?? Analysis.MarginThresholdDefault,
            SsStartExpr       = dto.SsStartExpr       ?? "",
            SsStopExpr        = dto.SsStopExpr        ?? "",
            SsStepExpr        = dto.SsStepExpr        ?? "",
            SsNptsExpr        = dto.SsNptsExpr        ?? "",
            SsUnit            = dto.SsUnit            ?? "Hz",
            SsLogExpr         = dto.SsLogExpr         ?? "",
            SsMaxHarmExpr     = dto.SsMaxHarmExpr     ?? "",
            NoiseExpr         = dto.HbNoiseExpr         ?? Analysis.NoiseDefault,
            NoiseInExpr       = dto.HbNoiseInExpr       ?? HarmonicBalanceAnalysis.NoiseInDefault,
            NoiseOutExpr      = dto.HbNoiseOutExpr      ?? HarmonicBalanceAnalysis.NoiseOutDefault,
            NoiseSidebandExpr = dto.HbNoiseSidebandExpr ?? HarmonicBalanceAnalysis.NoiseSidebandDefault,
            NoiseContribExpr  = dto.HbNoiseContribExpr  ?? Analysis.NoiseContribDefault,
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
            NoiseExpr         = dto.LpNoiseExpr         ?? Analysis.NoiseDefault,
            NoiseFreqExpr     = dto.LpNoiseFreqExpr     ?? "",
            NoiseFreqUnit     = dto.LpNoiseFreqUnit     ?? "Hz",
            NoiseInExpr       = dto.LpNoiseInExpr       ?? "",
            NoiseOutExpr      = dto.LpNoiseOutExpr      ?? "",
            NoiseSidebandExpr = dto.LpNoiseSidebandExpr ?? HarmonicBalanceAnalysis.NoiseSidebandDefault,
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
            NoiseExpr         = dto.LpNoiseExpr         ?? Analysis.NoiseDefault,
            NoiseFreqExpr     = dto.LpNoiseFreqExpr     ?? "",
            NoiseFreqUnit     = dto.LpNoiseFreqUnit     ?? "Hz",
            NoiseInExpr       = dto.LpNoiseInExpr       ?? "",
            NoiseOutExpr      = dto.LpNoiseOutExpr      ?? "",
            NoiseSidebandExpr = dto.LpNoiseSidebandExpr ?? HarmonicBalanceAnalysis.NoiseSidebandDefault,
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
