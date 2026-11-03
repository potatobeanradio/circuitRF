using System.Text.Json.Serialization;

namespace CircuitRF.Core.Design;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The statistical half of a testbench's tuning setup (docs/design/yield.md): the Monte Carlo /
//  yield settings, correlations between statistical entries, and named corners. It lives in the ONE
//  tuning block (yield overview D1) — a tolerance is part of the variable entry, a yield spec is a
//  goal — so there is no second "statistics" block beside it.
//
//  As for the rest of the setup, every value a user writes is kept as TEXT or as the typed value it
//  is, and a default is held as null so neither serialization ever writes one.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a run's trials are placed (yield overview D5).</summary>
public enum StatSampling
{
    /// <summary>Independent pseudo-random draws — the default.</summary>
    Random,
    /// <summary>Latin hypercube: needs the trial count up front, so it is refused with auto-stop.</summary>
    Lhs,
    /// <summary>Scrambled low-discrepancy sequence; extensible, so it works with auto-stop.</summary>
    Sobol,
}

/// <summary>What a trial that does not evaluate counts as (yield overview D7).</summary>
public enum NonConvergedPolicy
{
    /// <summary>Counted as a fail and reported separately — the default.</summary>
    Fail,
    /// <summary>Excluded from the yield's denominator and reported as a warning.</summary>
    Warn,
}

/// <summary>
/// The Monte Carlo / yield settings — the <c>statistics</c> line. Every property is null (or the
/// enum's first member) at its default, and the defaults are the constants below.
/// </summary>
public sealed class StatisticsSettings
{
    public const int    DefaultTrials     = 100;
    public const int    DefaultSeed       = 1;
    public const double DefaultConfidence = 95;

    /// <summary>The number of trials — with auto-stop, the cap.</summary>
    public int? Trials { get; set; }

    public int? Seed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatSampling Sampling { get; set; }

    /// <summary>The yield target in percent; null for none.</summary>
    public double? Target { get; set; }

    /// <summary>The confidence of the yield interval in percent.</summary>
    public double? Confidence { get; set; }

    /// <summary>Stop when the yield interval clears <see cref="Target"/> either way (yield overview D8).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoStop { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public NonConvergedPolicy NonConverged { get; set; }

    /// <summary>What a run keeps: <c>scalars</c>, <c>all</c> or a trial count; null for <c>auto</c>.</summary>
    public string? Save { get; set; }

    /// <summary>Kit process draws; null is on.</summary>
    public bool? Process { get; set; }

    /// <summary>Kit mismatch draws; null is on.</summary>
    public bool? Mismatch { get; set; }

    /// <summary>Scales every kit σ; null is 1.</summary>
    public double? SigmaScale { get; set; }

    /// <summary>How many trials may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerScope Scope { get; set; }

    /// <summary>The corners a run covers: <c>all</c>, or names separated by commas; null for none.</summary>
    public string? Corners { get; set; }

    /// <summary>Library parts whose own distributions are held at nominal (brief vendorlib-13 R-vl13-1c): part numbers
    /// separated by commas, as written on the <c>nominalparts=</c> key; null holds none.</summary>
    public string? NominalParts { get; set; }

    /// <summary>The part numbers <see cref="NominalParts"/> lists; empty for none.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> NominalPartList => NominalParts is null ? []
        : NominalParts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    [JsonIgnore] public int    EffectiveTrials     => Trials ?? DefaultTrials;
    [JsonIgnore] public int    EffectiveSeed       => Seed ?? DefaultSeed;
    [JsonIgnore] public double EffectiveConfidence => Confidence ?? DefaultConfidence;

    /// <summary>The corner names a run covers; empty for none, null for all.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? CornerNames => Corners is null ? []
        : Corners.Equals("all", StringComparison.OrdinalIgnoreCase) ? null
        : Corners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public StatisticsSettings Clone() => new()
    {
        Trials = Trials, Seed = Seed, Sampling = Sampling, Target = Target, Confidence = Confidence,
        AutoStop = AutoStop, NonConverged = NonConverged, Save = Save, Process = Process, Mismatch = Mismatch,
        SigmaScale = SigmaScale, Parallelism = Parallelism, Scope = Scope, Corners = Corners, NominalParts = NominalParts,
        Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>
/// The design-centering settings — the <c>center</c> line (brief-yield-11 R-ya11-6). Centering moves the nominals of
/// the <c>opt=1</c> entries to maximize yield against the <c>use=yield|both</c> goals, every candidate scored on ONE
/// fixed set of <see cref="Trials"/> trials (common random numbers), then verifies the start and the best point on
/// <see cref="Verify"/> fresh trials. Every property is null at its default, and the defaults are the constants below.
/// </summary>
public sealed class CenteringSettings
{
    public const int    DefaultTrials    = 200;
    public const int    DefaultVerify    = 1000;
    public const double DefaultWidth     = 0.05;
    public const int    DefaultSeed      = 1;
    public const string DefaultAlgorithm = "cmaes";

    /// <summary>A registry id whose algorithm suits a noisy objective; null is CMA-ES.</summary>
    public string? Algorithm { get; set; }

    /// <summary>M — the trials every candidate is scored on.</summary>
    public int? Trials { get; set; }

    /// <summary>The trials of the closing verification, each of the start and the best point.</summary>
    public int? Verify { get; set; }

    public int? MaxIterations { get; set; }

    /// <summary>The simulation limit — one candidate costs M of them.</summary>
    public int? MaxEvaluations { get; set; }

    /// <summary>Wall-clock limit as value text (<c>10 min</c>).</summary>
    public string? TimeLimit { get; set; }

    /// <summary>The smooth pass indicator's logistic width, as a fraction of each goal's scale.</summary>
    public double? Width { get; set; }

    /// <summary>How many simulations may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    /// <summary>The seed of the common trial set; the verification draws from the next seed.</summary>
    public int? Seed { get; set; }

    /// <summary>The search accelerator (brief-yield-12 R-ya12-2); null is <see cref="CenteringSurrogate.None"/> —
    /// every candidate simulated on the common trials.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CenteringSurrogate? Surrogate { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    [JsonIgnore] public CenteringSurrogate EffectiveSurrogate => Surrogate ?? CenteringSurrogate.None;
    [JsonIgnore] public string EffectiveAlgorithm => Algorithm ?? DefaultAlgorithm;
    [JsonIgnore] public int    EffectiveTrials    => Trials ?? DefaultTrials;
    [JsonIgnore] public int    EffectiveVerify    => Verify ?? DefaultVerify;
    [JsonIgnore] public double EffectiveWidth     => Width ?? DefaultWidth;
    [JsonIgnore] public int    EffectiveSeed      => Seed ?? DefaultSeed;

    public CenteringSettings Clone() => new()
    {
        Algorithm = Algorithm, Trials = Trials, Verify = Verify, MaxIterations = MaxIterations,
        MaxEvaluations = MaxEvaluations, TimeLimit = TimeLimit, Width = Width, Parallelism = Parallelism, Seed = Seed,
        Surrogate = Surrogate, Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>How design centering scores a candidate (brief-yield-12 R-ya12-2).</summary>
public enum CenteringSurrogate
{
    /// <summary>Every candidate simulated on the M common trials — the default.</summary>
    None,
    /// <summary>Each yield goal's margin fitted as a quadratic in z from a small design of simulations, and the yield
    /// estimated on virtual trials of the fit. A search accelerator only: the result's yield is always simulated.</summary>
    Quadratic,
}

/// <summary>The structured designs of design of experiments (brief-yield-14 R-ya14-2).</summary>
public enum DoeDesignKind
{
    /// <summary>The two-level full factorial, 2^k runs.</summary>
    Full2,
    /// <summary>A two-level fractional factorial 2^(k−p) at a stated resolution.</summary>
    Frac,
    /// <summary>A Plackett–Burman screening design of 12, 20 or 24 runs.</summary>
    Pb,
    /// <summary>The face-centred central composite: a two-level cube, the axial points on its faces, the centre.</summary>
    Ccf,
}

/// <summary>Which entries a design of experiments varies (brief-yield-14 R-ya14-1).</summary>
public enum DoeFactorSource
{
    /// <summary>The <c>opt=1</c> entries, at the ends of their ranges.</summary>
    Opt,
    /// <summary>The <c>stat=1</c> entries, at nominal ± k σ.</summary>
    Stat,
}

/// <summary>What a design of experiments analyses (brief-yield-14 R-ya14-4).</summary>
public enum DoeResponseSet
{
    /// <summary>The goals the factors are for — <c>use=opt|both</c> with opt factors, <c>use=yield|both</c> with stat
    /// factors — and every scalar measure.</summary>
    Goals,
    /// <summary>Every enabled goal regardless of <c>use</c>, and every scalar measure.</summary>
    All,
}

/// <summary>
/// The design-of-experiments settings — the <c>doe</c> line (brief-yield-14 R-ya14-3). Every property is null at its
/// default, so neither serialization ever writes one.
/// </summary>
public sealed class DoeSettings
{
    public const int DefaultResolution = 4;
    public const int DefaultCentre     = 1;
    public const double DefaultSigma   = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DoeDesignKind? Design { get; set; }

    /// <summary>A fractional factorial's resolution, 4 or 5; null is 4.</summary>
    public int? Resolution { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DoeFactorSource? Factors { get; set; }

    /// <summary>The levels as written: <c>range</c> or <c>sigma:&lt;k&gt;</c>; null is the factors' own default —
    /// <c>range</c> for opt factors, <c>sigma:1</c> for stat factors.</summary>
    public string? Levels { get; set; }

    /// <summary>Centre points added to the design; null is 1.</summary>
    public int? Centre { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DoeResponseSet? Responses { get; set; }

    /// <summary>How many simulations may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    [JsonIgnore] public DoeDesignKind   EffectiveDesign     => Design ?? DoeDesignKind.Full2;
    [JsonIgnore] public int             EffectiveResolution => Resolution ?? DefaultResolution;
    [JsonIgnore] public DoeFactorSource EffectiveFactors    => Factors ?? DoeFactorSource.Opt;
    [JsonIgnore] public int             EffectiveCentre     => Centre ?? DefaultCentre;
    [JsonIgnore] public DoeResponseSet  EffectiveResponses  => Responses ?? DoeResponseSet.Goals;

    /// <summary>The levels in force: <c>range</c>, or <c>sigma:&lt;k&gt;</c>.</summary>
    [JsonIgnore] public string EffectiveLevels => Levels ?? DefaultLevels(EffectiveFactors);

    /// <summary>k of <c>sigma:&lt;k&gt;</c>; null for <c>range</c> (or a spelling the reader would refuse).</summary>
    [JsonIgnore] public double? SigmaK => SigmaOf(EffectiveLevels);

    public static string DefaultLevels(DoeFactorSource factors) => factors == DoeFactorSource.Stat ? "sigma:1" : "range";

    /// <summary>k of a <c>sigma:&lt;k&gt;</c> spelling, when it is one with k above zero.</summary>
    public static double? SigmaOf(string levels)
        => levels.StartsWith("sigma:", StringComparison.OrdinalIgnoreCase)
           && double.TryParse(levels[6..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double k)
           && k > 0 && double.IsFinite(k) ? k : null;

    public DoeSettings Clone() => new()
    {
        Design = Design, Resolution = Resolution, Factors = Factors, Levels = Levels, Centre = Centre,
        Responses = Responses, Parallelism = Parallelism, Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>Which values a sensitivity run steps (brief-sens-1 R-sens1-1).</summary>
public enum SensitivityFactorSource
{
    /// <summary>Every tunable the catalog offers: at its tolerance (±kσ) when it has one, at ±step otherwise.</summary>
    All,
    /// <summary>The <c>stat=1</c> entries, at ±kσ through their own distributions.</summary>
    Stat,
    /// <summary>The <c>opt=1</c> entries, at ±step of their value.</summary>
    Opt,
}

/// <summary>
/// The sensitivity settings — the <c>sensitivity</c> line (brief-sens-1 R-sens1-5). Every property is null at its
/// default, so neither serialization ever writes one. <see cref="Snp"/>, <see cref="SnpExclude"/>, <see cref="SnpDb"/>
/// and <see cref="SnpDeg"/> are read and validated now and used from SENS-2.
/// </summary>
public sealed class SensitivitySettings
{
    public const double DefaultStep   = 1;
    public const double DefaultSigma  = 1;
    public const double DefaultSnpDb  = 0.1;
    public const double DefaultSnpDeg = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SensitivityFactorSource? Factors { get; set; }

    /// <summary>The ± step of a value with no tolerance, in percent of the value; null is 1 %.</summary>
    public double? Step { get; set; }

    /// <summary>k of the ±kσ step of a toleranced value; null is 1.</summary>
    public double? Sigma { get; set; }

    /// <summary>True with <c>ports=on</c>: <c>factors=all</c> then steps every value of a Port instance too (its
    /// reference impedance). Null is off — a port is the measurement's reference, not a value of the design.</summary>
    public bool? Ports { get; set; }

    /// <summary>False with <c>snp=off</c>; null is on.</summary>
    public bool? Snp { get; set; }

    /// <summary>SnP instances left out, comma-separated as written; null leaves none out.</summary>
    public string? SnpExclude { get; set; }

    /// <summary>The ± step of an SnP entry's magnitude, in dB; null is 0.1.</summary>
    public double? SnpDb { get; set; }

    /// <summary>The ± step of an SnP entry's phase, in degrees; null is 1.</summary>
    public double? SnpDeg { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DoeResponseSet? Responses { get; set; }

    /// <summary>How many simulations may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    [JsonIgnore] public SensitivityFactorSource EffectiveFactors   => Factors ?? SensitivityFactorSource.All;
    [JsonIgnore] public double                  EffectiveStep      => Step ?? DefaultStep;
    [JsonIgnore] public double                  EffectiveSigma     => Sigma ?? DefaultSigma;
    [JsonIgnore] public bool                    EffectivePorts     => Ports ?? false;
    [JsonIgnore] public bool                    EffectiveSnp       => Snp ?? true;
    [JsonIgnore] public double                  EffectiveSnpDb     => SnpDb ?? DefaultSnpDb;
    [JsonIgnore] public double                  EffectiveSnpDeg    => SnpDeg ?? DefaultSnpDeg;
    [JsonIgnore] public DoeResponseSet          EffectiveResponses => Responses ?? DoeResponseSet.Goals;

    public SensitivitySettings Clone() => new()
    {
        Factors = Factors, Step = Step, Sigma = Sigma, Ports = Ports, Snp = Snp, SnpExclude = SnpExclude, SnpDb = SnpDb, SnpDeg = SnpDeg,
        Responses = Responses, Parallelism = Parallelism, Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>A correlation between two statistical entries (yield overview D3) — the <c>correlate</c> line.</summary>
public sealed class StatCorrelation
{
    public string First  { get; set; } = "";
    public string Second { get; set; } = "";
    public double Rho    { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public StatCorrelation Clone() => new() { First = First, Second = Second, Rho = Rho, Extra = TunableEntry.CloneMap(Extra) };
}

/// <summary>
/// A named corner (yield overview D10) — the <c>corner</c> line. A set of bindings: kit corner-axis
/// selections (a <c>.csch</c> only — extraction resolves them into <see cref="Values"/>, so a
/// <c>.cnl</c> corner never names a kit file), the ambient <see cref="Temp"/>, and values for global
/// variables and tunables. A STATISTICAL corner names a trial of a run instead (<see cref="Trial"/>
/// with the seed, sampling and trial count that identify it) and replays that trial's draws.
/// </summary>
public sealed class CornerDefinition
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Kit corner-axis selections, keyed as the schematic's own corner selections are
    /// (<c>&lt;kit&gt;|&lt;kit-relative corner file&gt;</c>) → section. <c>.csch</c> only.</summary>
    public OrderedDictionary<string, string>? AxisSelections { get; set; }

    /// <summary>The ambient temperature as value text, in °C.</summary>
    public string? Temp { get; set; }

    /// <summary>Library parts' corners as axes (brief vendorlib-13 R-vl13-2e): part number → corner name, written
    /// <c>part:&lt;part number&gt;=&lt;corner&gt;</c>. Applies to every instance of the part in the design — the
    /// one-lot convention — overriding each instance's own <c>Corner</c>, and holds that part's own draws at
    /// nominal in a Monte Carlo at this corner (R-vl13-2g).</summary>
    public OrderedDictionary<string, string>? PartCorners { get; set; }

    /// <summary>Global-variable names and tunable keys → value text, in the order written.</summary>
    public OrderedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A statistical corner's trial number (1-based); null for a value corner.</summary>
    public int? Trial { get; set; }

    /// <summary>The seed of the run the trial came from.</summary>
    public int? Seed { get; set; }

    /// <summary>The sampling of the run the trial came from.</summary>
    public StatSampling? Sampling { get; set; }

    /// <summary>The trial count of the run the trial came from (LHS places trials by it).</summary>
    public int? Trials { get; set; }

    [JsonIgnore]
    public bool IsStatistical => Trial is not null;

    public CornerDefinition Clone() => new()
    {
        Name = Name, Enabled = Enabled, AxisSelections = TunableEntry.CloneMap(AxisSelections), Temp = Temp,
        PartCorners = TunableEntry.CloneMap(PartCorners),
        Values = TunableEntry.CloneMap(Values)!, Trial = Trial, Seed = Seed, Sampling = Sampling, Trials = Trials,
    };
}
