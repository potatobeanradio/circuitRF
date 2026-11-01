using System.Globalization;
using System.Text.Json.Serialization;

namespace CircuitRF.Core.Design;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The tuning and optimization setup a testbench carries (docs/design/tuning-optimization.md).
//
//  It lives HERE, beside Analysis and Measurement, and not in src/Design/Optimization where the
//  brief that introduced it first placed it: the `.cnl` reader and writer are in src/Core and a
//  TestBench carries the setup, and src/Core cannot reference src/Design. The orchestration —
//  the tunable catalog, the in-memory overrides and the `check` rules — is in src/Design.
//
//  Plain data with settable properties, so the `.csch` serializer binds it directly. Every value a
//  user writes (a bound, a preset value, a goal limit) is kept as the TEXT the schematic would hold
//  ("47 pF"), never as a resolved double: a pushed preset must be byte-identical to a typed value.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a tunable's slider and the optimizer's transform space its range.</summary>
public enum TuneScale
{
    /// <summary>Logarithmic when min &gt; 0 and max/min ≥ 10, linear otherwise.</summary>
    Auto,
    Lin,
    Log,
}

/// <summary>Which values a tunable may take.</summary>
public enum TuneDiscrete
{
    None,
    Integer,
    /// <summary>Standard component values (the Smith Chart tool's preferred-value series).</summary>
    Preferred,
    // An IEC 60063 series over the entry's range, chosen per entry rather than per quantity. Appended so
    // the members above keep their ordinals.
    E6,
    E12,
    E24,
    E48,
    E96,
}

/// <summary>How a goal's limit is met: <c>le</c> at or below, <c>ge</c> at or above, <c>eq</c> at,
/// <c>in</c> inside [a, b], <c>out</c> outside [a, b].</summary>
public enum GoalType { Le, Ge, Eq, In, Out }

/// <summary>What a goal serves (yield overview D4). <see cref="Both"/> is the default.</summary>
public enum GoalUse { Both, Opt, Yield }

/// <summary>How per-point violations combine into one cost.</summary>
public enum OptimizerCost
{
    /// <summary>Σ v² — the default.</summary>
    LeastSquares,
    /// <summary>max v.</summary>
    Minimax,
}

/// <summary>Which analyses an optimizer evaluation runs.</summary>
public enum OptimizerScope
{
    /// <summary>Only the analyses some enabled goal names.</summary>
    GoalAnalyses,
    All,
}

/// <summary>The distribution a statistical entry draws from (docs/design/yield.md, overview D2).</summary>
public enum StatDistribution
{
    None,
    /// <summary>Normal: <c>sd</c> is 1σ, or <c>tol</c> at <c>sigmas</c> σ.</summary>
    Gauss,
    /// <summary>Uniform on nominal ± <c>tol</c>, or on [<c>lo</c>, <c>hi</c>].</summary>
    Unif,
    /// <summary>The value's log is normal; <c>sd</c> is the value's relative 1σ.</summary>
    LogNorm,
    /// <summary>Equally likely values <c>lo</c>, <c>lo+by</c>, … ≤ <c>hi</c>.</summary>
    Discrete,
}

/// <summary>
/// A statistical entry's spread — whichever of the keys were written, each kept as the TEXT written,
/// which is also its form: a percent of the nominal (<c>2%</c>) or an absolute value in the parameter's
/// unit (<c>0.1 pF</c>). <see cref="Sigmas"/> and <see cref="Trunc"/> are plain numbers.
/// </summary>
public sealed class StatSpread
{
    public string? Sd     { get; set; }
    public string? Tol    { get; set; }
    public string? Sigmas { get; set; }
    public string? Lo     { get; set; }
    public string? Hi     { get; set; }
    public string? By     { get; set; }
    public string? Trunc  { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Sd is null && Tol is null && Sigmas is null && Lo is null && Hi is null && By is null
                           && Trunc is null;

    public StatSpread Clone() => new()
    {
        Sd = Sd, Tol = Tol, Sigmas = Sigmas, Lo = Lo, Hi = Hi, By = By, Trunc = Trunc,
    };

    /// <summary>True when value text is a percent of the nominal (<c>2%</c>).</summary>
    public static bool IsPercent(string? text) => text is { Length: > 1 } t && t.TrimEnd().EndsWith('%');

    /// <summary>True when <see cref="Tol"/> is <c>part</c> — the placed vendor part's own tolerance, resolved at check
    /// time from the library index (brief vendorlib-5 R-vl5-3b).</summary>
    [JsonIgnore]
    public bool TolIsPart => string.Equals(Tol, TunableKey.PartTolerance, StringComparison.Ordinal);

    /// <summary>The number of a percent (<c>2%</c> → 2), or null when the text is not one.</summary>
    public static double? Percent(string? text)
        => IsPercent(text) && double.TryParse(text!.Trim()[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;
}

/// <summary>
/// One tunable's entry — shared by tuning and optimizing (overview D4). The CURRENT tuned value is
/// session state and is not here.
/// </summary>
public sealed class TunableEntry
{
    /// <summary>The identity key (overview D3): <c>R1.R</c>, <c>Wline</c>, <c>DUT:R3.R</c>,
    /// <c>DUT:Wline</c>, <c>X1.Rbias</c>, <c>DUT:X5.Wf</c>.</summary>
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tune { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Opt { get; set; }

    /// <summary>Lower bound as value text, in the parameter's own unit (<c>10 Ohm</c>).</summary>
    public string? Min { get; set; }

    /// <summary>Upper bound as value text.</summary>
    public string? Max { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TuneScale Scale { get; set; }

    /// <summary>Slider / grid step as value text; null for continuous.</summary>
    public string? Step { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TuneDiscrete Discrete { get; set; }

    /// <summary>
    /// A placed vendor part's <c>Part</c> entry (brief vendorlib-4 R-vl4-1b): <c>same</c> takes the parts of
    /// the same kind as the placed one — same file, same Description fields but the value. Null when the line
    /// does not say; <see cref="Parts"/> null as well then means <c>same</c>. Kept as written, so a line that
    /// states it reads back stating it.
    /// </summary>
    public string? PartKind { get; set; }

    /// <summary>A placed vendor part's <c>Part</c> entry: the part-search query (<c>0603 C0G 50V</c>) whose
    /// matches in the part's own library are its candidates. Null when the line names none.</summary>
    public string? Parts { get; set; }

    /// <summary>The statistical flag (yield overview D1/D2): the entry's distribution is drawn in a
    /// Monte Carlo trial. Reading <c>dist=</c> sets it unless the line says <c>stat=0</c>; an entry with
    /// no distribution is not statistical whatever this says.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stat { get; set; }

    /// <summary>The distribution a trial draws the value from; <see cref="StatDistribution.None"/> for
    /// an entry with no tolerance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatDistribution Distribution { get; set; }

    /// <summary>The distribution's spread, as written; null when the entry has none.</summary>
    public StatSpread? Spread { get; set; }

    /// <summary>The entry carries a distribution that a trial draws (<see cref="Stat"/> and a
    /// distribution).</summary>
    [JsonIgnore]
    public bool IsStatistical => Stat && Distribution != StatDistribution.None;

    /// <summary>Keys this build does not know, kept verbatim so a file written by a later version
    /// survives a round trip through this one.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public TunableEntry Clone() => new()
    {
        Key = Key, Tune = Tune, Opt = Opt, Min = Min, Max = Max, Scale = Scale, Step = Step,
        Discrete = Discrete, PartKind = PartKind, Parts = Parts, Stat = Stat, Distribution = Distribution, Spread = Spread?.Clone(),
        Extra = CloneMap(Extra),
    };

    internal static OrderedDictionary<string, string>? CloneMap(OrderedDictionary<string, string>? m)
    {
        if (m is null) return null;
        var copy = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in m) copy.Add(k, v);
        return copy;
    }
}

/// <summary>A named set of tuned values (overview D6 for the automatic "Last tuned" one).</summary>
public sealed class TuningPreset
{
    public string Name { get; set; } = "";

    private DateTime? _created;

    /// <summary>When it was locked in, UTC, to the whole second — the precision the <c>.cnl</c>
    /// spelling carries, so the two serializations cannot disagree after a round trip.</summary>
    public DateTime? Created
    {
        get => _created;
        set => _created = value is { } v ? TruncateToSecondUtc(v) : null;
    }

    /// <summary>The automatic preset written when a document is saved or closed with unpushed tuned
    /// values. There is at most one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLastTuned { get; set; }

    /// <summary>The optimizer's cost at these values, when the Optimizer locked them in; null for a
    /// preset a person locked in.</summary>
    public double? Cost { get; set; }

    /// <summary>Key → value text, in the order the preset lists them.</summary>
    public OrderedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public TuningPreset Clone() => new()
    {
        Name = Name, Created = Created, IsLastTuned = IsLastTuned, Cost = Cost,
        Values = TunableEntry.CloneMap(Values)!,
    };

    private static DateTime TruncateToSecondUtc(DateTime v)
    {
        var utc = v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>The name of the automatic preset (overview D6).</summary>
    public const string LastTunedName = "Last tuned";

    /// <summary>The spelling both serializations use for <see cref="Created"/>.</summary>
    public static string FormatCreated(DateTime utc)
        => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The one swept axis a goal is evaluated over, and the part of it that counts.</summary>
public sealed class GoalRange
{
    /// <summary>The axis name: <c>freq</c>, a sweep variable, a power or harmonic axis.</summary>
    public string Axis { get; set; } = "freq";

    /// <summary>Lower end as value text (<c>1 GHz</c>).</summary>
    public string Lo { get; set; } = "";

    /// <summary>Upper end as value text.</summary>
    public string Hi { get; set; } = "";

    public GoalRange Clone() => new() { Axis = Axis, Lo = Lo, Hi = Hi };
}

/// <summary>One optimization goal (overview D10).</summary>
public sealed class OptimizationGoal
{
    public string Name { get; set; } = "";

    /// <summary>An expression in the one expression engine — the language <c>measure</c> uses.</summary>
    public string Expression { get; set; } = "";

    /// <summary>The analysis the goal reads. Null for a goal over variables only, which costs no
    /// simulation.</summary>
    public string? Analysis { get; set; }

    public GoalRange? Range { get; set; }

    public GoalType Type { get; set; }

    /// <summary><c>le</c>/<c>ge</c>/<c>eq</c>: the limit, or its value at the range's low end when
    /// <see cref="LimitAtHi"/> slopes it. <c>in</c>/<c>out</c>: the lower edge of the band.</summary>
    public string Limit { get; set; } = "";

    /// <summary><c>in</c>/<c>out</c> only: the upper edge of the band.</summary>
    public string? UpperLimit { get; set; }

    /// <summary><c>le</c>/<c>ge</c>/<c>eq</c> only: the limit at the range's high end — the limit is
    /// interpolated linearly between <see cref="Limit"/> and this across the range.</summary>
    public string? LimitAtHi { get; set; }

    public double Weight { get; set; } = 1.0;

    /// <summary>What one unit of violation is worth, as value text; null for the default — the band's
    /// width for <c>in</c>/<c>out</c>, otherwise the larger of |limit| and 1 in the limit's own unit
    /// (docs/design/tuning-optimization.md §10).</summary>
    public string? Scale { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>What the goal serves (yield overview D4): the optimizer, the yield analysis, or both.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GoalUse Use { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    public OptimizationGoal Clone() => new()
    {
        Name = Name, Expression = Expression, Analysis = Analysis, Range = Range?.Clone(), Type = Type,
        Limit = Limit, UpperLimit = UpperLimit, LimitAtHi = LimitAtHi, Weight = Weight, Scale = Scale, Enabled = Enabled,
        Use = Use, Extra = TunableEntry.CloneMap(Extra),
    };

    /// <summary>The optimizer aims for it.</summary>
    [JsonIgnore]
    public bool ForOptimizer => Use != GoalUse.Yield;

    /// <summary>A yield trial passes only when it is met.</summary>
    [JsonIgnore]
    public bool ForYield => Use != GoalUse.Opt;
}

/// <summary>The optimizer's own settings — the algorithm and what stops it.</summary>
public sealed class OptimizerSettings
{
    /// <summary>A stable id from <see cref="OptimizerAlgorithms.Ids"/>.</summary>
    public string Algorithm { get; set; } = OptimizerAlgorithms.Auto;

    /// <summary>The algorithm's own options (population size, restarts …), name → value text. Written
    /// <c>alg.&lt;name&gt;=</c> in a <c>.cnl</c>; each algorithm phase documents its own.</summary>
    public OrderedDictionary<string, string>? Options { get; set; }

    public int? MaxIterations { get; set; }
    public int? MaxEvaluations { get; set; }

    /// <summary>Wall-clock limit as value text (<c>60 s</c>).</summary>
    public string? TimeLimit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerCost Cost { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OptimizerScope Scope { get; set; }

    public int? Seed { get; set; }

    /// <summary>How many evaluations may run at once; null lets the evaluator decide.</summary>
    public int? Parallelism { get; set; }

    /// <summary>The corners every goal must be met at (brief-yield-7 R-ya7-1): <c>all</c>, or names separated by
    /// commas; null for none — the nominal alone.</summary>
    public string? Corners { get; set; }

    /// <summary>With <see cref="Corners"/>, whether the nominal is evaluated too (<c>nominal=0</c> omits it); null
    /// is yes.</summary>
    public bool? Nominal { get; set; }

    /// <summary>Keys this build does not know, kept verbatim.</summary>
    public OrderedDictionary<string, string>? Extra { get; set; }

    /// <summary>The corner names a run is evaluated at; empty for none, null for every enabled corner.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? CornerNames => Corners is null ? []
        : Corners.Equals("all", StringComparison.OrdinalIgnoreCase) ? null
        : Corners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public OptimizerSettings Clone() => new()
    {
        Algorithm = Algorithm, Options = TunableEntry.CloneMap(Options), MaxIterations = MaxIterations,
        MaxEvaluations = MaxEvaluations, TimeLimit = TimeLimit, Cost = Cost, Scope = Scope, Seed = Seed,
        Parallelism = Parallelism, Corners = Corners, Nominal = Nominal, Extra = TunableEntry.CloneMap(Extra),
    };
}

/// <summary>
/// Everything a schematic says about tuning and optimizing it: the variable entries, the presets,
/// the goals and the optimizer settings (overview D5). Null on a schematic that never had any.
/// </summary>
public sealed class TuningSetup
{
    public List<TunableEntry>     Variables { get; set; } = [];
    public List<TuningPreset>     Presets   { get; set; } = [];
    public List<OptimizationGoal> Goals     { get; set; } = [];
    public OptimizerSettings?     Optimizer { get; set; }

    /// <summary>Correlations between statistical entries (yield overview D3).</summary>
    public List<StatCorrelation>  Correlations { get; set; } = [];

    /// <summary>The Monte Carlo / yield settings; null when the design states none.</summary>
    public StatisticsSettings?    Statistics { get; set; }

    /// <summary>Named corners (yield overview D10).</summary>
    public List<CornerDefinition> Corners { get; set; } = [];

    /// <summary>The design-centering settings (brief-yield-11); null when the design states none.</summary>
    public CenteringSettings?     Centering { get; set; }

    /// <summary>The design-of-experiments settings (brief-yield-14); null when the design states none.</summary>
    public DoeSettings?           Doe { get; set; }

    /// <summary>The sensitivity settings (brief-sens-1); null when the design states none.</summary>
    public SensitivitySettings?   Sensitivity { get; set; }

    /// <summary>How many significant digits the Tuning and Optimizer panels spell a tuned or optimized
    /// value with — what they show, simulate and push. Null is the panels' default; display only, so
    /// it is not part of the <c>.cnl</c>.</summary>
    public int? Digits { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Variables.Count == 0 && Presets.Count == 0 && Goals.Count == 0 && Optimizer is null
                           && Digits is null && Correlations.Count == 0 && Statistics is null && Corners.Count == 0
                           && Centering is null && Doe is null && Sensitivity is null;

    public TuningSetup Clone() => new()
    {
        Variables    = [.. Variables.Select(v => v.Clone())],
        Presets      = [.. Presets.Select(p => p.Clone())],
        Goals        = [.. Goals.Select(g => g.Clone())],
        Optimizer    = Optimizer?.Clone(),
        Digits       = Digits,
        Correlations = [.. Correlations.Select(c => c.Clone())],
        Statistics   = Statistics?.Clone(),
        Corners      = [.. Corners.Select(c => c.Clone())],
        Centering    = Centering?.Clone(),
        Doe          = Doe?.Clone(),
        Sensitivity  = Sensitivity?.Clone(),
    };
}

/// <summary>
/// One part of a complex value (overview D18). A complex value is never tuned whole: each part is a
/// tunable of its own, keyed <c>real(K)</c>, <c>imag(K)</c>, <c>mag(K)</c> or <c>phase(K)</c> where
/// <c>K</c> is the value's own key. The spellings are the expression engine's own functions, so a key
/// reads as what it measures; <see cref="Phase"/> is in degrees, as <c>phase()</c> is.
/// </summary>
public enum ComplexPart { Real, Imag, Mag, Phase }

/// <summary>
/// A tunable key split into its parts (overview D3): <c>[Cell:]Instance.Parameter</c> or
/// <c>[Cell:]Variable</c>, optionally wrapped in a complex part — <c>mag(DUT:ZL)</c>.
/// <see cref="Cell"/> is the cell as the <c>.cnl</c> spells its instance type; null at the top level.
/// </summary>
public readonly record struct TunableKey(string? Cell, string? Instance, string Name, ComplexPart? Part = null)
{
    /// <summary>The parameter name a placed vendor part's one tunable is keyed with: <c>C1.Part</c> (brief
    /// vendorlib-4 R-vl4-1a). Its value is the part, not a number.</summary>
    public const string PartParameter = "Part";

    public bool IsVariable => Instance is null;

    /// <summary>The key names a placed vendor part's choice of part (<c>C1.Part</c>, <c>DUT:C1.Part</c>).</summary>
    public bool IsVendorPart => Instance is not null && Part is null && Name == PartParameter;

    /// <summary>Whether <paramref name="key"/> parses as a vendor part's key.</summary>
    public static bool IsVendorPartKey(string? key) => key is not null && TryParse(key, out var k) && k.IsVendorPart;

    /// <summary>The spread value meaning "this vendor part's own tolerance" (brief vendorlib-5 R-vl5-3b): <c>tol=part</c>.</summary>
    public const string PartTolerance = "part";

    /// <summary>A key a vendor part's value can be drawn under — <c>C1.C</c>, <c>DUT:L2.L</c>: an instance's <c>C</c> or
    /// <c>L</c>. Whether the instance IS a vendor part is not in the key; the catalog knows that.</summary>
    public bool IsPartValueShape => Instance is not null && Part is null && Name is "C" or "L";

    /// <summary>The key of the whole value a part key names; the key itself when it names no part.</summary>
    public TunableKey Whole => this with { Part = null };

    public override string ToString()
    {
        string whole = (Cell is null ? "" : Cell + ":") + (Instance is null ? Name : Instance + "." + Name);
        return Part is { } p ? $"{PartWord(p)}({whole})" : whole;
    }

    /// <summary>The word a part is spelled with: <c>real</c>, <c>imag</c>, <c>mag</c>, <c>phase</c>.</summary>
    public static string PartWord(ComplexPart part) => part switch
    {
        ComplexPart.Real => "real",
        ComplexPart.Imag => "imag",
        ComplexPart.Mag  => "mag",
        _                => "phase",
    };

    /// <summary>The part of the whole value <paramref name="wholeKey"/>, spelled as a key.</summary>
    public static string PartKey(string wholeKey, ComplexPart part) => $"{PartWord(part)}({wholeKey})";

    /// <summary>Splits a key. False for an empty part (<c>:R1.R</c>, <c>R1.</c>, <c>mag()</c>).</summary>
    public static bool TryParse(string text, out TunableKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        ComplexPart? part = null;
        int open = s.IndexOf('(');
        if (open > 0 && s[^1] == ')')
        {
            part = s[..open] switch
            {
                "real"  => ComplexPart.Real,
                "imag"  => ComplexPart.Imag,
                "mag"   => ComplexPart.Mag,
                "phase" => ComplexPart.Phase,
                _       => null,
            };
            if (part is null) return false;
            s = s[(open + 1)..^1].Trim();
            if (s.Contains('(') || s.Contains(')')) return false;
        }
        else if (s.Contains('(') || s.Contains(')'))
        {
            return false;
        }

        string? cell = null;
        int colon = s.IndexOf(':');
        if (colon >= 0)
        {
            cell = s[..colon];
            s    = s[(colon + 1)..];
            if (cell.Length == 0) return false;
        }

        int dot = s.IndexOf('.');
        if (dot < 0)
        {
            if (s.Length == 0) return false;
            key = new TunableKey(cell, null, s, part);
            return true;
        }

        string inst = s[..dot], name = s[(dot + 1)..];
        if (inst.Length == 0 || name.Length == 0) return false;
        key = new TunableKey(cell, inst, name, part);
        return true;
    }
}
