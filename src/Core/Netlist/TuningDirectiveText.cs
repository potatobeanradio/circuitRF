using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// Reads and writes the <c>tune</c>, <c>preset</c>, <c>goal</c> and <c>optimize</c> directives, and the
/// statistical <c>correlate</c>, <c>statistics</c>, <c>center</c>, <c>doe</c> and <c>corner</c> (docs/design/yield.md) — the
/// <c>.cnl</c> spelling of a <see cref="TuningSetup"/>. The grammar is
/// <see cref="AnalysisDirectiveSchema.TuningDirectives"/>; the writer's output reads back to the same
/// setup and writes the same bytes again.
///
/// <para>A malformed line is REFUSED (a <see cref="TuningDirectiveException"/> carrying its
/// <see cref="TuningDirectiveDiagnostics"/> entry, which the reader turns into a
/// <see cref="CnlReadException"/> naming the line). A key the grammar does not know is a WARNING
/// naming the key, and the key is kept, so a file written by a later version round-trips.</para>
/// </summary>
public static class TuningDirectiveText
{
    /// <summary>The nine keywords.</summary>
    public static bool IsKeyword(string word)
        => word is "tune" or "preset" or "goal" or "optimize" or "correlate" or "statistics" or "center" or "doe" or "corner";

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>Reads one directive's remainder (the text after the keyword) into
    /// <paramref name="tb"/>'s setup. Unknown keys are added to <paramref name="warnings"/>.</summary>
    public static void Read(string keyword, string rest, TestBench tb, List<string> warnings, int lineNumber)
    {
        var setup = tb.Tuning ??= new TuningSetup();
        var spec  = AnalysisDirectiveSchema.FindTuningDirective(keyword)!;
        void Unknown(string key) => warnings.Add(
            TuningDirectiveDiagnostics.UnknownKey(lineNumber, keyword, key, string.Join(", ", spec.Keys.Select(k => k.Name))).Render());

        switch (keyword)
        {
            case "tune":     setup.Variables.Add(ReadTune(rest, spec, Unknown)); break;
            case "preset":   setup.Presets.Add(ReadPreset(rest)); break;
            case "goal":     setup.Goals.Add(ReadGoal(rest, spec, Unknown)); break;
            case "optimize":
                if (setup.Optimizer is not null)
                    throw Refuse(TuningDirectiveDiagnostics.SecondOptimize());
                setup.Optimizer = ReadOptimize(rest, Unknown);
                break;
            case "correlate": setup.Correlations.Add(ReadCorrelate(rest, Unknown)); break;
            case "statistics":
                if (setup.Statistics is not null)
                    throw Refuse(TuningDirectiveDiagnostics.SecondStatistics());
                setup.Statistics = ReadStatistics(rest, Unknown);
                break;
            case "center":
                if (setup.Centering is not null)
                    throw Refuse(TuningDirectiveDiagnostics.SecondCenter());
                setup.Centering = ReadCenter(rest, Unknown);
                break;
            case "doe":
                if (setup.Doe is not null)
                    throw Refuse(TuningDirectiveDiagnostics.SecondDoe());
                setup.Doe = ReadDoe(rest, Unknown);
                break;
            case "corner": setup.Corners.Add(ReadCorner(rest)); break;
        }
    }

    private static TunableEntry ReadTune(string rest, TuningDirectiveSpec spec, Action<string> unknown)
    {
        var t = Tokens(rest);
        if (t.Count == 0 || t[0].Text.Contains('='))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("tune", "a key comes first — tune R1.R min=… max=…"));
        if (!TunableKey.TryParse(t[0].Text, out _))
            throw Refuse(TuningDirectiveDiagnostics.KeyMalformed(t[0].Text));

        var e = new TunableEntry { Key = t[0].Text };
        bool? stat = null;
        for (int i = 1; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            switch (key.ToLowerInvariant())
            {
                case "min":      e.Min = value; break;
                case "max":      e.Max = value; break;
                case "step":     e.Step = value; break;
                case "scale":    e.Scale = Enum<TuneScale>(key, value, AnalysisDirectiveSchema.ScaleTokens); break;
                case "discrete": e.Discrete = Enum<TuneDiscrete>(key, value, AnalysisDirectiveSchema.DiscreteTokens); break;
                // A vendor part's candidates (brief vendorlib-4 R-vl4-1d): one of the two ways, never both.
                case "kind":
                    if (!value.Equals("same", StringComparison.OrdinalIgnoreCase))
                        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "same"));
                    e.PartKind = value;
                    break;
                case "parts":
                    if (string.IsNullOrWhiteSpace(value))
                        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a part search, quoted (parts=\"0603 C0G 50V\")"));
                    e.Parts = value;
                    break;
                case "tune":     e.Tune = Bool(key, value); break;
                case "opt":      e.Opt = Bool(key, value); break;
                // The statistical part (yield overview D2).
                case "stat":     stat = Bool(key, value); break;
                case "dist":     e.Distribution = StatEnum<StatDistribution>(key, value, AnalysisDirectiveSchema.DistTokens); break;
                case "sd":       (e.Spread ??= new()).Sd     = SpreadValue(key, value); break;
                case "tol":
                    // tol=part: a vendor part's own tolerance (brief vendorlib-5 R-vl5-3b), only on an instance's C or L.
                    if (value.Equals(TunableKey.PartTolerance, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TunableKey.TryParse(e.Key, out var pk) || !pk.IsPartValueShape)
                            throw Refuse(TuningDirectiveDiagnostics.TolPartNotAPartValue(e.Key));
                        (e.Spread ??= new()).Tol = TunableKey.PartTolerance;
                        break;
                    }
                    (e.Spread ??= new()).Tol = SpreadValue(key, value);
                    break;
                case "lo":       (e.Spread ??= new()).Lo     = SpreadValue(key, value); break;
                case "hi":       (e.Spread ??= new()).Hi     = SpreadValue(key, value); break;
                case "by":       (e.Spread ??= new()).By     = SpreadValue(key, value); break;
                case "sigmas":   (e.Spread ??= new()).Sigmas = PlainNumber(key, value); break;
                case "trunc":    (e.Spread ??= new()).Trunc  = PlainNumber(key, value); break;
                default:
                    (e.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        // stat defaults to 1 when a distribution is stated (D2), wherever on the line stat= sits.
        e.Stat = stat ?? e.Distribution != StatDistribution.None;
        if (e.PartKind is not null && e.Parts is not null)
            throw Refuse(TuningDirectiveDiagnostics.Malformed("tune",
                "kind= and parts= are two ways to choose a part's candidates — write one of them"));
        return e;
    }

    private static StatisticsSettings ReadStatistics(string rest, Action<string> unknown)
    {
        var t = Tokens(rest);
        var s = new StatisticsSettings();
        for (int i = 0; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: false);
            // A default is never held, so neither serialization ever writes one.
            switch (key.ToLowerInvariant())
            {
                case "trials":       s.Trials = Default(StatInt(key, value), StatisticsSettings.DefaultTrials); break;
                case "seed":         s.Seed = Default(StatInt(key, value), StatisticsSettings.DefaultSeed); break;
                case "sampling":     s.Sampling = StatEnum<StatSampling>(key, value, AnalysisDirectiveSchema.SamplingTokens); break;
                case "target":       s.Target = Percent(key, value); break;
                case "confidence":   s.Confidence = Default(Percent(key, value), StatisticsSettings.DefaultConfidence); break;
                case "autostop":     s.AutoStop = Bool(key, value); break;
                case "nonconverged": s.NonConverged = StatEnum<NonConvergedPolicy>(key, value, AnalysisDirectiveSchema.NonConvergedTokens); break;
                case "save":         s.Save = SaveValue(key, value); break;
                case "process":      s.Process = Bool(key, value) ? null : false; break;
                case "mismatch":     s.Mismatch = Bool(key, value) ? null : false; break;
                case "sigmascale":   s.SigmaScale = Default(StatDouble(key, value), 1.0); break;
                case "parallel":     s.Parallelism = StatInt(key, value); break;
                case "analyses":     s.Scope = StatEnum<OptimizerScope>(key, value, AnalysisDirectiveSchema.ScopeTokens); break;
                case "corners":      s.Corners = CornersValue(key, value); break;
                default:
                    (s.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        return s;
    }

    private static CenteringSettings ReadCenter(string rest, Action<string> unknown)
    {
        var t = Tokens(rest);
        var c = new CenteringSettings();
        for (int i = 0; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            // A default is never held, so neither serialization ever writes one.
            switch (key.ToLowerInvariant())
            {
                case "algorithm": c.Algorithm = value == CenteringSettings.DefaultAlgorithm ? null : value; break;
                case "trials":    c.Trials = Default(Positive(key, value), CenteringSettings.DefaultTrials); break;
                case "verify":    c.Verify = Default(Positive(key, value), CenteringSettings.DefaultVerify); break;
                case "maxiter":   c.MaxIterations = Positive(key, value); break;
                case "maxevals":  c.MaxEvaluations = Positive(key, value); break;
                case "timelimit": c.TimeLimit = value; break;
                case "width":
                    double w = StatDouble(key, value);
                    if (!(w > 0)) throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a number above zero"));
                    c.Width = Default(w, CenteringSettings.DefaultWidth);
                    break;
                case "parallel":  c.Parallelism = Positive(key, value); break;
                case "seed":      c.Seed = Default(StatInt(key, value), CenteringSettings.DefaultSeed); break;
                case "surrogate":
                    var sg = StatEnum<CenteringSurrogate>(key, value, AnalysisDirectiveSchema.SurrogateTokens);
                    c.Surrogate = sg == CenteringSurrogate.None ? null : sg;
                    break;
                default:
                    (c.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        return c;
    }

    /// <summary>The <c>doe</c> line (brief-yield-14 R-ya14-3). A malformed value is refused with <c>cnl.doe.*</c>; a
    /// default is never held, so neither serialization writes one.</summary>
    private static DoeSettings ReadDoe(string rest, Action<string> unknown)
    {
        var t = Tokens(rest);
        var d = new DoeSettings();
        T DoeEnum<T>(string key, string value, IReadOnlyList<string> tokens) where T : struct, Enum
            => TryEnum<T>(value, tokens) ?? throw Refuse(TuningDirectiveDiagnostics.DoeValueInvalid(key, value, "one of " + string.Join(", ", tokens)));
        int Whole(string key, string value, int min)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= min ? n
               : throw Refuse(TuningDirectiveDiagnostics.DoeValueInvalid(key, value, min == 0 ? "a whole number, 0 or more" : "a whole number, 1 or more"));
        for (int i = 0; i < t.Count; i++)
        {
            if (!t[i].Text.Contains('=')) throw Refuse(TuningDirectiveDiagnostics.DoeMalformed($"'{t[i].Text}' is not key=value — doe design=frac resolution=5"));
            var (key, value) = KeyValue(t, ref i, unitsAllowed: false);
            switch (key.ToLowerInvariant())
            {
                case "design":
                    var design = DoeEnum<DoeDesignKind>(key, value, AnalysisDirectiveSchema.DoeDesignTokens);
                    d.Design = design == DoeDesignKind.Full2 ? null : design;
                    break;
                case "resolution":
                    d.Resolution = value is "4" or "5" ? Default(int.Parse(value, CultureInfo.InvariantCulture), DoeSettings.DefaultResolution)
                                 : throw Refuse(TuningDirectiveDiagnostics.DoeValueInvalid(key, value, "4 or 5"));
                    break;
                case "factors":
                    var factors = DoeEnum<DoeFactorSource>(key, value, AnalysisDirectiveSchema.DoeFactorTokens);
                    d.Factors = factors == DoeFactorSource.Opt ? null : factors;
                    break;
                case "levels":
                    if (value.Equals("range", StringComparison.OrdinalIgnoreCase)) d.Levels = "range";
                    else if (DoeSettings.SigmaOf(value) is { } k) d.Levels = "sigma:" + k.ToString("R", CultureInfo.InvariantCulture);
                    else throw Refuse(TuningDirectiveDiagnostics.DoeValueInvalid(key, value, "range, or sigma:<k> with k above zero"));
                    break;
                case "centre":
                    d.Centre = Default(Whole(key, value, 0), DoeSettings.DefaultCentre);
                    break;
                case "responses":
                    var responses = DoeEnum<DoeResponseSet>(key, value, AnalysisDirectiveSchema.DoeResponseTokens);
                    d.Responses = responses == DoeResponseSet.Goals ? null : responses;
                    break;
                case "parallel":
                    d.Parallelism = Whole(key, value, 1);
                    break;
                default:
                    (d.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        // The levels the factors take anyway are the default, wherever on the line factors= sits.
        if (d.Levels == DoeSettings.DefaultLevels(d.EffectiveFactors)) d.Levels = null;
        return d;
    }

    private static int Positive(string key, string value)
        => StatInt(key, value) is var n && n >= 1 ? n
           : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a whole number, 1 or more"));

    private static StatCorrelation ReadCorrelate(string rest, Action<string> unknown)
    {
        const string shape = "two tunable keys, then rho= — correlate R1.R R2.R rho=0.9";
        var t = Tokens(rest);
        if (t.Count < 2 || t[0].Text.Contains('=') || t[1].Text.Contains('='))
            throw Refuse(TuningDirectiveDiagnostics.StatisticsMalformed("correlate", shape));
        foreach (var k in t.Take(2))
            if (!TunableKey.TryParse(k.Text, out _)) throw Refuse(TuningDirectiveDiagnostics.KeyMalformed(k.Text));

        var c = new StatCorrelation { First = t[0].Text, Second = t[1].Text };
        bool rho = false;
        for (int i = 2; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: false);
            if (key.Equals("rho", StringComparison.OrdinalIgnoreCase))
            {
                c.Rho = StatDouble(key, value);
                rho = true;
                continue;
            }
            (c.Extra ??= new(StringComparer.Ordinal))[key] = value;
            unknown(key);
        }
        if (!rho) throw Refuse(TuningDirectiveDiagnostics.StatisticsMalformed("correlate", shape));
        return c;
    }

    /// <summary>A corner line owns enabled, trial, seed, sampling, trials and temp wherever they sit;
    /// every other key is a value it binds. A global variable with one of those names cannot be bound by
    /// a corner — <c>temp</c> is that global already.</summary>
    private static CornerDefinition ReadCorner(string rest)
    {
        var t = Tokens(rest);
        if (t.Count == 0 || t[0].Text.Contains('='))
            throw Refuse(TuningDirectiveDiagnostics.CornerMalformed("?", "a name comes first — corner SS_hot temp=85 Vdd=3.0 V"));
        string name = t[0].Text;
        if (!IsIdentifier(name)) throw Refuse(TuningDirectiveDiagnostics.CornerMalformed(name, "a corner's name is one word."));

        var c = new CornerDefinition { Name = name };
        for (int i = 1; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            switch (key.ToLowerInvariant())
            {
                case "enabled":  c.Enabled = CornerBool(name, key, value); break;
                case "trial":    c.Trial = CornerInt(name, key, value); break;
                case "seed":     c.Seed = CornerInt(name, key, value); break;
                case "trials":   c.Trials = CornerInt(name, key, value); break;
                case "temp":     c.Temp = value; break;
                case "sampling":
                    c.Sampling = TryEnum<StatSampling>(value, AnalysisDirectiveSchema.SamplingTokens)
                        ?? throw Refuse(TuningDirectiveDiagnostics.CornerValueInvalid(name, key, value,
                               "one of " + string.Join(", ", AnalysisDirectiveSchema.SamplingTokens)));
                    break;
                default:
                    if (!IsIdentifier(key) && !TunableKey.TryParse(key, out _))
                        throw Refuse(TuningDirectiveDiagnostics.CornerMalformed(name,
                            $"'{key}' is neither a variable's name nor a tunable key."));
                    if (!c.Values.TryAdd(key, value))
                        throw Refuse(TuningDirectiveDiagnostics.CornerMalformed(name, $"it binds {key} twice."));
                    break;
            }
        }
        return c;
    }

    private static TuningPreset ReadPreset(string rest)
    {
        var t = Tokens(rest);
        if (t.Count == 0 || t[0].Text.Contains('=') && !t[0].Text.StartsWith('"'))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("preset", "a name comes first — preset \"wide band\" R1.R=47 Ohm …"));

        var p = new TuningPreset { Name = Unquote(t[0].Text) };
        if (p.Name.Length == 0)
            throw Refuse(TuningDirectiveDiagnostics.Malformed("preset", "its name cannot be empty."));

        bool inValues = false;
        for (int i = 1; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            if (!inValues && key.Equals("created", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                    throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a date written yyyy-MM-ddTHH:mm:ssZ"));
                p.Created = DateTime.SpecifyKind(when, DateTimeKind.Utc);
                continue;
            }
            if (!inValues && key.Equals("lasttuned", StringComparison.OrdinalIgnoreCase))
            {
                p.IsLastTuned = Bool(key, value);
                continue;
            }
            if (!inValues && key.Equals("cost", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost))
                    throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a number"));
                p.Cost = cost;
                continue;
            }
            inValues = true;
            if (!TunableKey.TryParse(key, out _))
                throw Refuse(TuningDirectiveDiagnostics.KeyMalformed(key));
            p.Values[key] = value;
        }
        return p;
    }

    private static OptimizationGoal ReadGoal(string rest, TuningDirectiveSpec spec, Action<string> unknown)
    {
        int eq = rest.IndexOf('=');
        if (eq <= 0)
            throw Refuse(TuningDirectiveDiagnostics.Malformed("goal", "a name, '=' and an expression come first — goal G1 = dB(SP1.S(2,1)) … ge -0.5"));
        string name = rest[..eq].Trim();
        if (!IsIdentifier(name)) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "a goal's name is one word."));

        string after = rest[(eq + 1)..];
        var t = Tokens(after);
        if (t.Count == 0) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it has no expression."));

        var g = new OptimizationGoal { Name = name };

        // The expression: one quoted token, or every token up to the first that this grammar owns.
        int i;
        if (t[0].Text.StartsWith('"'))
        {
            g.Expression = Unquote(t[0].Text);
            i = 1;
        }
        else
        {
            int end = 1;
            while (end < t.Count && !IsGoalStop(t[end].Text)) end++;
            g.Expression = after[t[0].Start..(t[end - 1].Start + t[end - 1].Text.Length)];
            i = end;
        }
        if (g.Expression.Trim().Length == 0) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it has no expression."));

        bool typed = false;
        string? over = null, lo = null, hi = null;
        for (; i < t.Count; i++)
        {
            string tok = t[i].Text;
            if (TryGoalType(tok, out var type))
            {
                if (typed) throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "it states its type twice."));
                typed  = true;
                g.Type = type;
                g.Limit = Limit(t, ref i, name, tok);
                if (type is GoalType.In or GoalType.Out)
                    g.UpperLimit = Limit(t, ref i, name, tok);
                else if (i + 1 < t.Count && t[i + 1].Text == "to")
                {
                    i++;
                    g.LimitAtHi = Limit(t, ref i, name, "to");
                }
                continue;
            }

            var (key, value) = KeyValue(t, ref i, unitsAllowed: true, goalLine: true);
            switch (key.ToLowerInvariant())
            {
                case "analysis": g.Analysis = value; break;
                case "over":     over = value; break;
                case "lo":       lo = value; break;
                case "hi":       hi = value; break;
                case "weight":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
                        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a number"));
                    g.Weight = w;
                    break;
                case "scale":    g.Scale = value; break;
                case "enabled":  g.Enabled = Bool(key, value); break;
                case "use":      g.Use = StatEnum<GoalUse>(key, value, AnalysisDirectiveSchema.UseTokens); break;
                default:
                    (g.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }

        if (!typed)
            throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name,
                $"it states no type; end it with one of {string.Join(", ", AnalysisDirectiveSchema.GoalTypeTokens)} and its limit."));
        if (lo is not null || hi is not null || over is not null)
        {
            if (lo is null || hi is null)
                throw Refuse(TuningDirectiveDiagnostics.GoalProblem(name, "its range needs both lo= and hi=."));
            g.Range = new GoalRange { Axis = over ?? "freq", Lo = lo, Hi = hi };
        }
        return g;
    }

    private static OptimizerSettings ReadOptimize(string rest, Action<string> unknown)
    {
        var t = Tokens(rest);
        var o = new OptimizerSettings();
        for (int i = 0; i < t.Count; i++)
        {
            var (key, value) = KeyValue(t, ref i, unitsAllowed: true);
            string lower = key.ToLowerInvariant();
            if (lower.StartsWith(AnalysisDirectiveSchema.AlgorithmOptionPrefix, StringComparison.Ordinal)
                && key.Length > AnalysisDirectiveSchema.AlgorithmOptionPrefix.Length)
            {
                (o.Options ??= new(StringComparer.Ordinal))[key[AnalysisDirectiveSchema.AlgorithmOptionPrefix.Length..]] = value;
                continue;
            }
            switch (lower)
            {
                case "algorithm": o.Algorithm = value; break;
                case "maxiter":   o.MaxIterations = Int(key, value); break;
                case "maxevals":  o.MaxEvaluations = Int(key, value); break;
                case "timelimit": o.TimeLimit = value; break;
                case "seed":      o.Seed = Int(key, value); break;
                case "parallel":  o.Parallelism = Int(key, value); break;
                case "corners":   o.Corners = CornersValue(key, value); break;
                case "nominal":   o.Nominal = Bool(key, value) ? null : false; break;
                case "cost":
                    o.Cost = value.ToLowerInvariant() switch
                    {
                        "lsq"     => OptimizerCost.LeastSquares,
                        "minimax" => OptimizerCost.Minimax,
                        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", AnalysisDirectiveSchema.CostTokens))),
                    };
                    break;
                case "analyses":
                    o.Scope = value.ToLowerInvariant() switch
                    {
                        "goals" => OptimizerScope.GoalAnalyses,
                        "all"   => OptimizerScope.All,
                        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", AnalysisDirectiveSchema.ScopeTokens))),
                    };
                    break;
                default:
                    (o.Extra ??= new(StringComparer.Ordinal))[key] = value;
                    unknown(key);
                    break;
            }
        }
        return o;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>The directive lines for <paramref name="setup"/>, in the order tune, preset, goal,
    /// optimize, correlate, statistics, center, doe, corner. Empty for an empty setup.</summary>
    public static IEnumerable<string> Write(TuningSetup setup)
    {
        foreach (var e in setup.Variables)    yield return WriteTune(e);
        foreach (var p in setup.Presets)      yield return WritePresetLine(p);
        foreach (var g in setup.Goals)        yield return WriteGoal(g);
        if (setup.Optimizer is { } o)         yield return WriteOptimize(o);
        foreach (var c in setup.Correlations) yield return WriteCorrelate(c);
        if (setup.Statistics is { } st)       yield return WriteStatistics(st);
        if (setup.Centering is { } center)    yield return WriteCenter(center);
        if (setup.Doe is { } doe)             yield return WriteDoe(doe);
        foreach (var c in setup.Corners)      yield return WriteCorner(c);
    }

    private static string WriteCorrelate(StatCorrelation c)
    {
        var sb = new StringBuilder("correlate ").Append(c.First).Append(' ').Append(c.Second)
            .Append(" rho=").Append(c.Rho.ToString("R", CultureInfo.InvariantCulture));
        Extra(sb, c.Extra, "");
        return sb.ToString();
    }

    private static string WriteStatistics(StatisticsSettings s)
    {
        var sb = new StringBuilder("statistics");
        if (s.Trials is { } n)        sb.Append(" trials=").Append(n.ToString(CultureInfo.InvariantCulture));
        if (s.Seed is { } seed)       sb.Append(" seed=").Append(seed.ToString(CultureInfo.InvariantCulture));
        if (s.Sampling != StatSampling.Random) sb.Append(" sampling=").Append(AnalysisDirectiveSchema.SamplingTokens[(int)s.Sampling]);
        if (s.Target is { } target)   sb.Append(" target=").Append(PercentText(target));
        if (s.Confidence is { } conf) sb.Append(" confidence=").Append(PercentText(conf));
        if (s.AutoStop)               sb.Append(" autostop=1");
        if (s.NonConverged != NonConvergedPolicy.Fail) sb.Append(" nonconverged=").Append(AnalysisDirectiveSchema.NonConvergedTokens[(int)s.NonConverged]);
        Opt(sb, "save", s.Save);
        if (s.Process == false)       sb.Append(" process=0");
        if (s.Mismatch == false)      sb.Append(" mismatch=0");
        if (s.SigmaScale is { } k)    sb.Append(" sigmascale=").Append(k.ToString("R", CultureInfo.InvariantCulture));
        if (s.Parallelism is { } p)   sb.Append(" parallel=").Append(p.ToString(CultureInfo.InvariantCulture));
        if (s.Scope != OptimizerScope.GoalAnalyses) sb.Append(" analyses=").Append(AnalysisDirectiveSchema.ScopeTokens[(int)s.Scope]);
        Opt(sb, "corners", s.Corners);
        Extra(sb, s.Extra, "");
        return sb.ToString();
    }

    /// <summary>The <c>center</c> line, keys in the order the grammar lists them, defaults never written.</summary>
    private static string WriteCenter(CenteringSettings c)
    {
        var sb = new StringBuilder("center");
        Opt(sb, "algorithm", c.Algorithm);
        if (c.Trials is { } m)         sb.Append(" trials=").Append(m.ToString(CultureInfo.InvariantCulture));
        if (c.Verify is { } v)         sb.Append(" verify=").Append(v.ToString(CultureInfo.InvariantCulture));
        if (c.MaxIterations is { } mi) sb.Append(" maxiter=").Append(mi.ToString(CultureInfo.InvariantCulture));
        if (c.MaxEvaluations is { } me) sb.Append(" maxevals=").Append(me.ToString(CultureInfo.InvariantCulture));
        Opt(sb, "timelimit", c.TimeLimit);
        if (c.Width is { } w)          sb.Append(" width=").Append(w.ToString("R", CultureInfo.InvariantCulture));
        if (c.Parallelism is { } p)    sb.Append(" parallel=").Append(p.ToString(CultureInfo.InvariantCulture));
        if (c.Seed is { } seed)        sb.Append(" seed=").Append(seed.ToString(CultureInfo.InvariantCulture));
        if (c.Surrogate is { } sg && sg != CenteringSurrogate.None)
            sb.Append(" surrogate=").Append(AnalysisDirectiveSchema.SurrogateTokens[(int)sg]);
        Extra(sb, c.Extra, "");
        return sb.ToString();
    }

    /// <summary>The <c>doe</c> line, keys in the order the grammar lists them, defaults never written.</summary>
    private static string WriteDoe(DoeSettings d)
    {
        var sb = new StringBuilder("doe");
        if (d.Design is { } design && design != DoeDesignKind.Full2)
            sb.Append(" design=").Append(AnalysisDirectiveSchema.DoeDesignTokens[(int)design]);
        if (d.Resolution is { } r)     sb.Append(" resolution=").Append(r.ToString(CultureInfo.InvariantCulture));
        if (d.Factors is { } f && f != DoeFactorSource.Opt)
            sb.Append(" factors=").Append(AnalysisDirectiveSchema.DoeFactorTokens[(int)f]);
        Opt(sb, "levels", d.Levels);
        if (d.Centre is { } c)         sb.Append(" centre=").Append(c.ToString(CultureInfo.InvariantCulture));
        if (d.Responses is { } rs && rs != DoeResponseSet.Goals)
            sb.Append(" responses=").Append(AnalysisDirectiveSchema.DoeResponseTokens[(int)rs]);
        if (d.Parallelism is { } p)    sb.Append(" parallel=").Append(p.ToString(CultureInfo.InvariantCulture));
        Extra(sb, d.Extra, "");
        return sb.ToString();
    }

    /// <summary>One corner as its <c>.cnl</c> line — what <c>yield corners --generate</c> prints (brief-yield-6).</summary>
    public static string CornerLine(CornerDefinition c) => WriteCorner(c);

    /// <summary>One corner as its <c>.cnl</c> line. Kit axis selections are not written: extraction has
    /// already resolved them into values (yield overview D10).</summary>
    private static string WriteCorner(CornerDefinition c)
    {
        var sb = new StringBuilder("corner ").Append(c.Name);
        if (!c.Enabled)            sb.Append(" enabled=0");
        if (c.Trial is { } trial)  sb.Append(" trial=").Append(trial.ToString(CultureInfo.InvariantCulture));
        if (c.Seed is { } seed)    sb.Append(" seed=").Append(seed.ToString(CultureInfo.InvariantCulture));
        if (c.Sampling is { } smp) sb.Append(" sampling=").Append(AnalysisDirectiveSchema.SamplingTokens[(int)smp]);
        if (c.Trials is { } n)     sb.Append(" trials=").Append(n.ToString(CultureInfo.InvariantCulture));
        Opt(sb, "temp", c.Temp);
        foreach (var (k, v) in c.Values) Opt(sb, k, v);
        return sb.ToString();
    }

    private static string PercentText(double percent) => percent.ToString("R", CultureInfo.InvariantCulture) + "%";

    private static string WriteTune(TunableEntry e)
    {
        var sb = new StringBuilder("tune ").Append(e.Key);
        Opt(sb, "kind", e.PartKind);
        Opt(sb, "parts", e.Parts);
        Opt(sb, "min", e.Min);
        Opt(sb, "max", e.Max);
        if (e.Scale != TuneScale.Auto) sb.Append(" scale=").Append(AnalysisDirectiveSchema.ScaleTokens[(int)e.Scale]);
        Opt(sb, "step", e.Step);
        if (e.Discrete != TuneDiscrete.None) sb.Append(" discrete=").Append(AnalysisDirectiveSchema.DiscreteTokens[(int)e.Discrete]);
        if (e.Tune) sb.Append(" tune=1");
        if (e.Opt)  sb.Append(" opt=1");
        if (e.Distribution != StatDistribution.None)
            sb.Append(" dist=").Append(AnalysisDirectiveSchema.DistTokens[(int)e.Distribution]);
        if (e.Spread is { } sp)
        {
            Opt(sb, "sd", sp.Sd);
            Opt(sb, "tol", sp.Tol);
            Opt(sb, "sigmas", sp.Sigmas);
            Opt(sb, "lo", sp.Lo);
            Opt(sb, "hi", sp.Hi);
            Opt(sb, "by", sp.By);
            Opt(sb, "trunc", sp.Trunc);
        }
        // stat is implied by dist (D2): written only where it says something else.
        if (e.Stat != (e.Distribution != StatDistribution.None)) sb.Append(e.Stat ? " stat=1" : " stat=0");
        Extra(sb, e.Extra, "");
        return sb.ToString();
    }

    /// <summary>One preset as its <c>.cnl</c> line — what "Copy as .cnl" puts on the clipboard.</summary>
    public static string WritePreset(TuningPreset p) => WritePresetLine(p);

    /// <summary>Reads one <c>preset …</c> line back — the inverse of <see cref="WritePreset"/>. A
    /// malformed line throws <see cref="TuningDirectiveException"/>.</summary>
    public static TuningPreset ReadPresetLine(string line)
    {
        string s = line.Trim();
        if (!s.StartsWith("preset", StringComparison.Ordinal) || s.Length < 7 || !char.IsWhiteSpace(s[6]))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("preset", "the line does not start with 'preset'."));
        return ReadPreset(s[7..]);
    }

    /// <summary>One goal as its <c>.cnl</c> line — what a Monte Carlo result records beside each goal's per-trial
    /// cubes so a display can draw the goal's limits from the result alone (brief-yield-8 R-ya8-3).</summary>
    public static string GoalLine(OptimizationGoal g) => WriteGoal(g);

    /// <summary>Reads one <c>goal …</c> line back — the inverse of <see cref="GoalLine"/>. A malformed line throws
    /// <see cref="TuningDirectiveException"/>; an unknown key is ignored.</summary>
    public static OptimizationGoal ReadGoalLine(string line)
    {
        string s = line.Trim();
        if (!s.StartsWith("goal", StringComparison.Ordinal) || s.Length < 5 || !char.IsWhiteSpace(s[4]))
            throw Refuse(TuningDirectiveDiagnostics.Malformed("goal", "the line does not start with 'goal'."));
        return ReadGoal(s[5..], AnalysisDirectiveSchema.FindTuningDirective("goal")!, _ => { });
    }

    private static string WritePresetLine(TuningPreset p)
    {
        var sb = new StringBuilder("preset \"").Append(p.Name).Append('"');
        if (p.Created is { } c) sb.Append(" created=").Append(TuningPreset.FormatCreated(c));
        if (p.IsLastTuned) sb.Append(" lasttuned=1");
        if (p.Cost is { } cost) sb.Append(" cost=").Append(cost.ToString("R", CultureInfo.InvariantCulture));
        foreach (var (k, v) in p.Values) Opt(sb, k, v);
        return sb.ToString();
    }

    private static string WriteGoal(OptimizationGoal g)
    {
        var sb = new StringBuilder("goal ").Append(g.Name).Append(" = ");
        sb.Append(ExpressionNeedsQuotes(g.Expression) ? $"\"{g.Expression}\"" : g.Expression);
        Opt(sb, "analysis", g.Analysis);
        if (g.Range is { } r)
        {
            Opt(sb, "over", r.Axis);
            Opt(sb, "lo", r.Lo, goalLine: true);
            Opt(sb, "hi", r.Hi, goalLine: true);
        }
        sb.Append(' ').Append(AnalysisDirectiveSchema.GoalTypeTokens[(int)g.Type]).Append(' ').Append(Value(g.Limit, goalLine: true));
        if (g.Type is GoalType.In or GoalType.Out)
            sb.Append(' ').Append(Value(g.UpperLimit ?? "", goalLine: true));
        else if (g.LimitAtHi is { } atHi)
            sb.Append(" to ").Append(Value(atHi, goalLine: true));
        if (g.Weight != 1.0) sb.Append(" weight=").Append(g.Weight.ToString("R", CultureInfo.InvariantCulture));
        Opt(sb, "scale", g.Scale, goalLine: true);
        if (!g.Enabled) sb.Append(" enabled=false");
        if (g.Use != GoalUse.Both) sb.Append(" use=").Append(AnalysisDirectiveSchema.UseTokens[(int)g.Use]);
        Extra(sb, g.Extra, "");
        return sb.ToString();
    }

    private static string WriteOptimize(OptimizerSettings o)
    {
        var sb = new StringBuilder("optimize algorithm=").Append(o.Algorithm);
        if (o.MaxIterations  is { } it) sb.Append(" maxiter=").Append(it.ToString(CultureInfo.InvariantCulture));
        if (o.MaxEvaluations is { } ev) sb.Append(" maxevals=").Append(ev.ToString(CultureInfo.InvariantCulture));
        if (o.TimeLimit is { } limit) sb.Append(" timelimit=").Append(Value(limit, duration: true));
        if (o.Cost  != OptimizerCost.LeastSquares) sb.Append(" cost=").Append(AnalysisDirectiveSchema.CostTokens[(int)o.Cost]);
        if (o.Scope != OptimizerScope.GoalAnalyses) sb.Append(" analyses=").Append(AnalysisDirectiveSchema.ScopeTokens[(int)o.Scope]);
        if (o.Seed        is { } s) sb.Append(" seed=").Append(s.ToString(CultureInfo.InvariantCulture));
        if (o.Parallelism is { } n) sb.Append(" parallel=").Append(n.ToString(CultureInfo.InvariantCulture));
        Opt(sb, "corners", o.Corners);
        if (o.Nominal == false) sb.Append(" nominal=0");
        Extra(sb, o.Options, AnalysisDirectiveSchema.AlgorithmOptionPrefix);
        Extra(sb, o.Extra, "");
        return sb.ToString();
    }

    private static void Opt(StringBuilder sb, string key, string? value, bool goalLine = false)
    {
        if (value is null) return;
        sb.Append(' ').Append(key).Append('=').Append(Value(value, goalLine));
    }

    private static void Extra(StringBuilder sb, OrderedDictionary<string, string>? map, string prefix)
    {
        if (map is null) return;
        foreach (var (k, v) in map) Opt(sb, prefix + k, v);
    }

    /// <summary>
    /// A value as it must be written to read back as itself: as-is when it is one token or one token
    /// and a unit (the way <c>measure</c> and an instance line write a unit), quoted otherwise.
    /// </summary>
    private static string Value(string text, bool goalLine = false, bool duration = false)
    {
        if (text.Length == 0) return "\"\"";
        var t = Tokens(text);
        bool plain = text == text.Trim() && !text.Contains('"')
                  && (t.Count == 1
                      || t.Count == 2 && text == t[0].Text + " " + t[1].Text
                                      && (duration ? IsTimeUnit(t[1].Text) : IsUnit(t[1].Text, goalLine)));
        return plain ? text : $"\"{text}\"";
    }

    /// <summary>True when the reader would end the expression early: some token after the first is
    /// one this grammar owns.</summary>
    private static bool ExpressionNeedsQuotes(string expression)
    {
        var t = Tokens(expression);
        return t.Count == 0 || t[0].Text.StartsWith('"') || t.Skip(1).Any(x => IsGoalStop(x.Text));
    }

    // ── Shared token handling ────────────────────────────────────────────────

    private readonly record struct Token(string Text, int Start);

    /// <summary>Splits on whitespace outside quotes and outside ( ) [ ] { } — so an expression's own
    /// spacing (<c>max_over(x, freq)</c>) stays inside one token.</summary>
    private static List<Token> Tokens(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            int start = i, depth = 0;
            bool quoted = false;
            while (i < s.Length && (quoted || depth > 0 || !char.IsWhiteSpace(s[i])))
            {
                char c = s[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && c is '(' or '[' or '{') depth++;
                else if (!quoted && c is ')' or ']' or '}' && depth > 0) depth--;
                i++;
            }
            tokens.Add(new Token(s[start..i], start));
        }
        return tokens;
    }

    /// <summary>Reads <c>key=value</c> at <paramref name="i"/>, then a unit token after it when one
    /// follows. Advances <paramref name="i"/> past whatever it consumed.</summary>
    private static (string Key, string Value) KeyValue(List<Token> t, ref int i, bool unitsAllowed, bool goalLine = false)
    {
        string tok = t[i].Text;
        int eq = tok.IndexOf('=');
        if (eq <= 0) throw Refuse(TuningDirectiveDiagnostics.NotKeyValue(tok));
        string key = tok[..eq];
        string value = tok[(eq + 1)..];
        if (value.StartsWith('"')) return (key, Unquote(value));
        bool duration = key.Equals("timelimit", StringComparison.OrdinalIgnoreCase);
        if (unitsAllowed && value.Length > 0 && i + 1 < t.Count
            && (duration ? IsTimeUnit(t[i + 1].Text) : IsUnit(t[i + 1].Text, goalLine)))
        {
            value += " " + t[i + 1].Text;
            i++;
        }
        return (key, value);
    }

    private static string Limit(List<Token> t, ref int i, string goal, string after)
    {
        if (i + 1 >= t.Count || IsGoalStop(t[i + 1].Text))
            throw Refuse(TuningDirectiveDiagnostics.GoalProblem(goal, $"'{after}' needs a limit after it."));
        i++;
        string v = t[i].Text;
        if (v.StartsWith('"')) return Unquote(v);
        if (i + 1 < t.Count && IsUnit(t[i + 1].Text, goalLine: true))
        {
            v += " " + t[i + 1].Text;
            i++;
        }
        return v;
    }

    /// <summary>A unit token after a value. On a goal line, the grammar's own words are never units —
    /// <c>in</c> is a goal type there and not inches.</summary>
    private static bool IsUnit(string token, bool goalLine)
        => !(goalLine && (token == "to" || AnalysisDirectiveSchema.GoalTypeTokens.Contains(token)))
        && !token.Contains('=') && !token.StartsWith('"')
        && Units.IsRecognizedUnit(UnitNormalizer.ToEngineUnit(token));

    /// <summary>The units a <c>timelimit</c> may carry. Local to this grammar: the shared unit table
    /// has no time units, and adding a bare <c>s</c> there would change what a bare word after an
    /// instance line's value means.</summary>
    public static bool IsTimeUnit(string token) => token is "s" or "ms" or "min" or "h";

    private static bool IsGoalStop(string token)
        => AnalysisDirectiveSchema.GoalTypeTokens.Contains(token) || IsKeyToken(token);

    /// <summary><c>name=…</c> where name is a word — and not <c>a==b</c>.</summary>
    private static bool IsKeyToken(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0 || eq + 1 < token.Length && token[eq + 1] == '=') return false;
        var key = token[..eq];
        return (char.IsLetter(key[0]) || key[0] == '_')
            && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '[' or ']');
    }

    private static bool TryGoalType(string token, out GoalType type)
    {
        int idx = -1;
        for (int k = 0; k < AnalysisDirectiveSchema.GoalTypeTokens.Count; k++)
            if (AnalysisDirectiveSchema.GoalTypeTokens[k] == token) idx = k;
        type = (GoalType)Math.Max(idx, 0);
        return idx >= 0;
    }

    private static TEnum Enum<TEnum>(string key, string value, IReadOnlyList<string> tokens) where TEnum : struct, Enum
    {
        for (int k = 0; k < tokens.Count; k++)
            if (tokens[k].Equals(value, StringComparison.OrdinalIgnoreCase)) return (TEnum)(object)k;
        throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "one of " + string.Join(", ", tokens)));
    }

    private static bool Bool(string key, string value) => value.ToLowerInvariant() switch
    {
        "1" or "true" or "yes"  => true,
        "0" or "false" or "no"  => false,
        _ => throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "1 or 0")),
    };

    private static int Int(string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : throw Refuse(TuningDirectiveDiagnostics.ValueInvalid(key, value, "a whole number"));

    // ── The statistical values (refused with cnl.statistics.* / cnl.corner.*) ──

    private static TEnum? TryEnum<TEnum>(string value, IReadOnlyList<string> tokens) where TEnum : struct, Enum
    {
        for (int k = 0; k < tokens.Count; k++)
            if (tokens[k].Equals(value, StringComparison.OrdinalIgnoreCase)) return (TEnum)(object)k;
        return null;
    }

    private static TEnum StatEnum<TEnum>(string key, string value, IReadOnlyList<string> tokens) where TEnum : struct, Enum
        => TryEnum<TEnum>(value, tokens)
           ?? throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "one of " + string.Join(", ", tokens)));

    private static int StatInt(string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a whole number"));

    private static double StatDouble(string key, string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a number"));

    private static double Percent(string key, string value)
        => StatSpread.Percent(value) is { } p && double.IsFinite(p)
            ? p : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a percent, written 95%"));

    private static T? Default<T>(T value, T @default) where T : struct, IEquatable<T>
        => value.Equals(@default) ? null : value;

    /// <summary>A spread value: a percent of the nominal (<c>2%</c>) or a number with an optional unit
    /// after a space (<c>0.1 pF</c>). Kept as written — the text is its form.</summary>
    private static string SpreadValue(string key, string value)
    {
        if (StatSpread.IsPercent(value))
            return StatSpread.Percent(value) is not null ? value
                : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a percent (2%) or a value with its unit"));
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool ok = parts.Length is 1 or 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
               && (parts.Length == 1 || Units.IsRecognizedUnit(UnitNormalizer.ToEngineUnit(parts[1])));
        return ok ? value
            : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "a percent (2%) or a value with its unit"));
    }

    private static string PlainNumber(string key, string value)
    {
        StatDouble(key, value);
        return value;
    }

    private static string? SaveValue(string key, string value)
    {
        string lower = value.ToLowerInvariant();
        if (lower == "auto") return null;
        if (lower is "scalars" or "all") return lower;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? value
            : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "auto, scalars, all or a trial count"));
    }

    private static string? CornersValue(string key, string value)
    {
        string lower = value.ToLowerInvariant();
        if (lower == "none") return null;
        if (lower == "all") return lower;
        var names = value.Split(',', StringSplitOptions.TrimEntries);
        return names.All(IsIdentifier) ? string.Join(',', names)
            : throw Refuse(TuningDirectiveDiagnostics.StatisticsValueInvalid(key, value, "none, all or corner names separated by commas"));
    }

    private static bool CornerBool(string corner, string key, string value) => value.ToLowerInvariant() switch
    {
        "1" or "true" or "yes"  => true,
        "0" or "false" or "no"  => false,
        _ => throw Refuse(TuningDirectiveDiagnostics.CornerValueInvalid(corner, key, value, "1 or 0")),
    };

    private static int CornerInt(string corner, string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : throw Refuse(TuningDirectiveDiagnostics.CornerValueInvalid(corner, key, value, "a whole number"));

    private static TuningDirectiveException Refuse(CircuitRF.Diagnostics.Diagnostic d) => new(d);

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
}
