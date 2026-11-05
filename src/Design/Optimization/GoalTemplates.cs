using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Circuit;

namespace CircuitRF.Design.Optimization;

/// <summary>The sections the goal editor lists templates under (brief-tuneopt-9 R-to9-3).</summary>
public enum GoalTemplateGroup { SParameters, WsProbe, Fields, Measurements, Custom }

/// <summary>One field a template asks for — a port, a probe, a scale — with its choices and default.</summary>
/// <param name="Key">The name <see cref="GoalTemplate.Make"/> reads it by.</param>
/// <param name="Label">What the editor shows beside it.</param>
/// <param name="Choices">The values offered; never empty.</param>
/// <param name="Default">The value used when none is given; one of <paramref name="Choices"/>.</param>
public sealed record GoalTemplateField(string Key, string Label, IReadOnlyList<string> Choices, string Default);

/// <summary>
/// A starting point for a goal: a few fields in, an <see cref="OptimizationGoal"/> out — its name, the
/// expression in the one expression engine, the analysis it reads, the analysis's swept range and a
/// suggested type. The expression is ordinary text the user can read and edit; nothing about a goal
/// made from a template differs from one typed by hand.
/// </summary>
public sealed class GoalTemplate
{
    private readonly Func<Func<string, string>, OptimizationGoal> _make;

    internal GoalTemplate(string id, string label, GoalTemplateGroup group, string? analysis,
                          IReadOnlyList<GoalTemplateField> fields, Func<Func<string, string>, OptimizationGoal> make)
    {
        Id = id; Label = label; Group = group; Analysis = analysis; Fields = fields; _make = make;
    }

    /// <summary>Stable within one catalog: <c>SP1.sdb</c>, <c>SP1.mu</c>, <c>SP1.wsp.SM</c>, <c>meas.Eff</c>, <c>custom</c>.</summary>
    public string Id { get; }

    /// <summary>What the list shows.</summary>
    public string Label { get; }

    public GoalTemplateGroup Group { get; }

    /// <summary>The analysis the template reads; null for a custom expression.</summary>
    public string? Analysis { get; }

    public IReadOnlyList<GoalTemplateField> Fields { get; }

    /// <summary>The goal for these field values; a missing or unknown value takes the field's default.</summary>
    public OptimizationGoal Make(IReadOnlyDictionary<string, string>? values = null)
        => _make(key =>
        {
            var field = Fields.First(f => f.Key == key);
            return values is not null && values.TryGetValue(key, out var v) && field.Choices.Contains(v) ? v : field.Default;
        });
}

/// <summary>
/// The goal-template catalog (brief-tuneopt-9 R-to9-3, overview D10) — headless, so the Optimizer
/// window, the CLI and a test all list the same templates.
///
/// <para><b>What it offers, for the analyses a bench declares:</b> for each S-parameter analysis,
/// |Sij| in dB or linear, the phase of Sij, its group delay, a port's VSWR, μ, μ′, Rollett K and the
/// maximum gain; for each S-parameter analysis of a circuit carrying WSProbes, every single-probe
/// derived metric of <c>stability-wsprobe.md</c> §5.2; every <c>measure</c> row by name; and a
/// custom expression. An HB power, efficiency or PAE goal is a <c>measure</c> row the user writes —
/// the catalog deliberately has no HB family of its own.</para>
///
/// <para>Every expression a template writes uses the functions a <c>measure</c> line uses
/// (<c>dB</c>, <c>phase</c>, <c>mu</c>, <c>wsp_SM_Y0</c> …), never a second spelling.</para>
/// </summary>
public static class GoalTemplates
{
    /// <summary>The templates for the analyses of a prepared circuit; empty when it could not be read.</summary>
    public static IReadOnlyList<GoalTemplate> For(PreparedCircuit circuit)
        => circuit.Tb is { } tb ? For(tb) : [Custom()];

    /// <summary>The templates for <paramref name="tb"/>'s analyses, grouped in the editor's order.</summary>
    public static IReadOnlyList<GoalTemplate> For(TestBench tb)
    {
        var list  = new List<GoalTemplate>();
        var ports = PortNumbers(tb);
        var probes = tb.Instances.Where(i => i.Reference == "WSProbe").Select(i => i.InstanceName).ToList();

        foreach (var sp in tb.Analyses.OfType<SParameterAnalysis>())
            list.AddRange(SParameterTemplates(sp, ports));
        if (probes.Count > 0)
            foreach (var sp in tb.Analyses.OfType<SParameterAnalysis>())
                list.AddRange(WsProbeTemplates(sp, probes));
        foreach (var fa in tb.Analyses.OfType<FieldsAnalysis>())
            list.AddRange(FieldsTemplates(fa));
        foreach (var m in tb.Measurements)
        {
            string name = m.Name, analysis = AnalysisReferencedBy(m.Expression, tb) ?? "";
            list.Add(new GoalTemplate($"meas.{name}", name, GoalTemplateGroup.Measurements,
                analysis.Length == 0 ? null : analysis, [],
                _ => new OptimizationGoal
                {
                    Name = name, Expression = name, Analysis = analysis.Length == 0 ? null : analysis,
                    Range = RangeOf(tb, analysis), Type = GoalType.Le,
                }));
        }
        list.Add(Custom());
        return list;
    }

    /// <summary>Why <paramref name="expression"/> cannot be a goal's expression — the engine's own
    /// parse message — or null when it parses. Names are resolved only when the goal runs.</summary>
    public static string? Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "A goal needs an expression.";
        try { Parser.Parse(expression); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// The analysis <paramref name="expression"/> reads — the first <c>Name.</c> qualifier naming one
    /// of the bench's analyses, following a <c>measure</c> name into that measurement's own
    /// expression — or null when it reads none (a goal over variables alone).
    /// </summary>
    public static string? AnalysisReferencedBy(string expression, TestBench tb)
        => AnalysisReferencedBy(expression, tb.Analyses, tb.Measurements);

    /// <summary>As <see cref="AnalysisReferencedBy(string, TestBench)"/>, over a schematic's own
    /// analysis and measurement lists.</summary>
    public static string? AnalysisReferencedBy(string expression, IEnumerable<Analysis> analysisList,
                                               IEnumerable<Measurement> measurementList)
    {
        var analyses = analysisList.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var measures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in measurementList) measures.TryAdd(m.Name, m.Expression);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Find(expression);

        string? Find(string text)
        {
            string? previous = null;
            foreach (var (word, dotted) in Words(text))
            {
                if (dotted && analyses.Contains(word)) return word;
                // brief-sar-10 — a field measurement names its fields analysis as its first argument: psSAR(F1, 10)
                if (!dotted && previous is { } fn && FieldMetricFunctions.IsFieldMetric(fn) && analyses.Contains(word)) return word;
                previous = word;
                if (!dotted && measures.TryGetValue(word, out var inner) && seen.Add(word) && Find(inner) is { } a)
                    return a;
            }
            return null;
        }
    }

    // ── S-parameters ─────────────────────────────────────────────────────────

    private static IEnumerable<GoalTemplate> SParameterTemplates(SParameterAnalysis sp, IReadOnlyList<string> ports)
    {
        string a = sp.Name, s = $"{a}.S";
        var range = RangeOf(sp);
        static string In(Func<string, string> v) => v("in");
        static string Out(Func<string, string> v) => v("out");
        var i = new GoalTemplateField("i", "Response port i", ports, ports.Count > 1 ? ports[1] : ports[0]);
        var j = new GoalTemplateField("j", "Drive port j",    ports, ports[0]);
        var scale = new GoalTemplateField("scale", "Scale", ["dB", "linear"], "dB");

        OptimizationGoal Goal(string name, string expr, GoalType type, string limit = "") => new()
        {
            Name = name, Expression = expr, Analysis = a, Range = range?.Clone(), Type = type, Limit = limit,
        };

        yield return new GoalTemplate($"{a}.sij", "|Sij|", GoalTemplateGroup.SParameters, a, [i, j, scale], v =>
        {
            bool db = v("scale") == "dB", reflection = v("i") == v("j");
            string sij = $"{s}({v("i")}, {v("j")})";
            return Goal($"{(db ? "dB" : "mag")}_S{v("i")}{v("j")}", db ? $"dB({sij})" : $"mag({sij})",
                        reflection ? GoalType.Le : GoalType.Ge);
        });
        yield return new GoalTemplate($"{a}.phase", "Phase of Sij (degrees)", GoalTemplateGroup.SParameters, a, [i, j],
            v => Goal($"phase_S{v("i")}{v("j")}", $"phase({s}({v("i")}, {v("j")}))", GoalType.Eq));

        if (ports.Count < 2) yield break;

        var pin  = new GoalTemplateField("in",  "Input port",  ports, ports[0]);
        var pout = new GoalTemplateField("out", "Output port", ports, ports[1]);
        // A two-port's pair is implied; an N-port names it, since mu(SP1.S) is refused there.
        string Pair(Func<string, string> v) => ports.Count == 2 && In(v) == ports[0] && Out(v) == ports[1]
            ? "" : $", {In(v)}, {Out(v)}";

        yield return new GoalTemplate($"{a}.group_delay", "Group delay", GoalTemplateGroup.SParameters, a, [pin, pout],
            v => Goal($"delay_S{Out(v)}{In(v)}", $"group_delay({s}, {In(v)}, {Out(v)})", GoalType.Le));
        yield return new GoalTemplate($"{a}.vswr", "VSWR at a port", GoalTemplateGroup.SParameters, a,
            [new GoalTemplateField("port", "Port", ports, ports[0])],
            v => Goal($"VSWR{v("port")}", $"vswr({s}({v("port")}, {v("port")}))", GoalType.Le));
        yield return new GoalTemplate($"{a}.mu", "μ — load stability", GoalTemplateGroup.SParameters, a, [pin, pout],
            v => Goal("mu", $"mu({s}{Pair(v)})", GoalType.Ge, "1"));
        yield return new GoalTemplate($"{a}.mu_prime", "μ′ — source stability", GoalTemplateGroup.SParameters, a, [pin, pout],
            v => Goal("mu_prime", $"mu_prime({s}{Pair(v)})", GoalType.Ge, "1"));
        yield return new GoalTemplate($"{a}.K", "Rollett K", GoalTemplateGroup.SParameters, a, [pin, pout],
            v => Goal("K", $"K({s}{Pair(v)})", GoalType.Ge, "1"));
        yield return new GoalTemplate($"{a}.max_gain", "Maximum gain (MAG/MSG)", GoalTemplateGroup.SParameters, a,
            [pin, pout, scale],
            v => v("scale") == "dB"
                ? Goal("max_gain", $"max_gain({s}{Pair(v)})", GoalType.Ge)
                : Goal("max_gain_lin", $"max_gain_lin({s}{Pair(v)})", GoalType.Ge));
    }

    // ── WSProbe ──────────────────────────────────────────────────────────────

    /// <summary>The single-probe metrics of <c>stability-wsprobe.md</c> §5.2: the function, what the
    /// list calls it, and whether its value is complex (and so needs a part to be a goal).</summary>
    private static readonly (string Fn, string Name, string Label, bool Complex, GoalType Type)[] WspMetrics =
    [
        ("wsp_stability_margin", "SM",    "Stability margin (smaller of the two)", false, GoalType.Ge),
        ("wsp_SM_Y0",            "SM_Y0", "Stability margin on 1/Y0",              false, GoalType.Ge),
        ("wsp_SM_H0",            "SM_H0", "Stability margin on 1/H0",              false, GoalType.Ge),
        ("wsp_rY",               "rY",    "Real-part proxy of ZG, ZL",             false, GoalType.Ge),
        ("wsp_iY",               "iY",    "Imaginary-part proxy of ZG, ZL",        false, GoalType.Ge),
        ("wsp_rH",               "rH",    "Real-part proxy of YG, YL",             false, GoalType.Ge),
        ("wsp_iH",               "iH",    "Imaginary-part proxy of YG, YL",        false, GoalType.Ge),
        ("wsp_H0",               "H0",    "Driving-point impedance H0",            true,  GoalType.Ge),
        ("wsp_Y0",               "Y0",    "Driving-point admittance Y0",           true,  GoalType.Ge),
        ("wsp_ZG",               "ZG",    "Impedance looking out of G",            true,  GoalType.Ge),
        ("wsp_ZL",               "ZL",    "Impedance looking out of L",            true,  GoalType.Ge),
        ("wsp_YG",               "YG",    "Admittance looking out of G",           true,  GoalType.Ge),
        ("wsp_YL",               "YL",    "Admittance looking out of L",           true,  GoalType.Ge),
        ("wsp_zop",              "Zop",   "Open-port impedance",                   true,  GoalType.Ge),
        ("wsp_yop",              "Yop",   "Open-port admittance",                  true,  GoalType.Ge),
        ("wsp_nodal_gamma",      "Gamma", "Nodal conjugate reflection coefficient", true, GoalType.Le),
        ("wsp_loopgain",         "LG",    "Bilateral (Tian) loop gain",            true,  GoalType.Le),
    ];

    private static IEnumerable<GoalTemplate> WsProbeTemplates(SParameterAnalysis sp, IReadOnlyList<string> probes)
    {
        string a = sp.Name;
        var range = RangeOf(sp);
        var probe = new GoalTemplateField("probe", "Probe", probes, probes[0]);
        foreach (var (fn, name, label, complex, type) in WspMetrics)
        {
            // A complex value has no order, so it is a goal only through one of its parts.
            var part = new GoalTemplateField("part", "Part", ["real", "imag", "mag", "dB", "phase"],
                                             name is "LG" or "Gamma" ? "mag" : "real");
            yield return new GoalTemplate($"{a}.wsp.{name}", label, GoalTemplateGroup.WsProbe, a,
                complex ? [probe, part] : [probe], v =>
                {
                    string call = fn == "wsp_loopgain"
                        ? $"wsp_loopgain(wsp_yparam({a}.wsp, {a}.idx(\"{v("probe")}\")), \"BI\")"
                        : $"{fn}({a}.wsp, {a}.idx(\"{v("probe")}\"))";
                    string expr = complex ? $"{v("part")}({call})" : call;
                    return new OptimizationGoal
                    {
                        Name = Identifier($"{name}_{v("probe")}{(complex ? "_" + v("part") : "")}"),
                        Expression = expr, Analysis = a, Range = range?.Clone(), Type = type,
                    };
                });
        }
    }

    // ── fields (brief-sar-10 R-sar10-1) ──────────────────────────────────────

    /// <summary>The field measurements over one fields analysis, each over the setup's own region (B1's, or SAR's); the
    /// region and mass are text the user edits in the goal (sar.md §12).</summary>
    private static IEnumerable<GoalTemplate> FieldsTemplates(FieldsAnalysis fa)
    {
        string a = fa.Name;
        var mass = new GoalTemplateField("mass", "Averaging mass (g)", ["10", "1"], "10");
        GoalTemplate One(string id, string label, string name, string expr, GoalType type, GoalTemplateField[]? fields = null)
            => new($"{a}.{id}", label, GoalTemplateGroup.Fields, a, fields ?? [],
                   v => new OptimizationGoal { Name = name, Expression = expr.Replace("<mass>", fields is null ? "" : v("mass")), Analysis = a, Type = type });
        yield return One("b1mean", "Mean |B1+| over a region (T/√W)", "B1mean", $"B1pMean({a})", GoalType.Ge);
        yield return One("b1min", "Smallest |B1+| over a region (T/√W)", "B1min", $"B1pMin({a})", GoalType.Ge);
        yield return One("b1cov", "|B1+| non-uniformity, std/mean", "B1cov", $"B1pCoV({a})", GoalType.Le);
        yield return One("pssar", "Peak spatial-average SAR (W/kg per W)", "psSAR", $"psSAR({a}, <mass>)", GoalType.Le, [mass]);
        yield return One("sarwb", "Whole-region SAR (W/kg per W)", "SARwb", $"SARwb({a})", GoalType.Le);
        yield return One("sareff", "SAR efficiency, B1pMean/√psSAR", "SarEff", $"SarEff({a}, \"all\", <mass>)", GoalType.Ge, [mass]);
        yield return One("pabs", "Absorbed power fraction (W per W)", "Pabs", $"Pabs({a})", GoalType.Le);
    }

    private static GoalTemplate Custom() => new("custom", "Custom expression", GoalTemplateGroup.Custom, null, [],
        _ => new OptimizationGoal { Name = "G1", Expression = "", Type = GoalType.Le });

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The bench's port numbers, ascending, from its <c>Port</c> instances' <c>Num</c>; two
    /// ports when it declares none it can read (a hierarchical bench, say).</summary>
    private static IReadOnlyList<string> PortNumbers(TestBench tb)
    {
        var nums = new SortedSet<int>();
        foreach (var inst in tb.Instances.Where(i => i.Reference == "Port"))
            if (inst.Overrides.FirstOrDefault(o => o.Name == "Num") is { } num
                && int.TryParse(num.Expression.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                nums.Add(n);
        return nums.Count > 0 ? [.. nums.Select(n => n.ToString(CultureInfo.InvariantCulture))] : ["1", "2"];
    }

    /// <summary>The whole swept frequency range of an S-parameter analysis, in the units it was written
    /// in; null when an end is not a plain number (a variable, say), which the editor shows as "whole range".</summary>
    private static GoalRange? RangeOf(SParameterAnalysis sp)
    {
        // The lowest and highest of every segment: segments come in any order, and a single point added after a
        // sweep usually lies inside it.
        (string Text, double Si)? lo = null, hi = null;
        foreach (var s in sp.Sweeps)
            foreach (var end in new[] { $"{s.StartExpr.Trim()} {s.StartUnit}".Trim(), $"{s.StopExpr.Trim()} {s.StopUnit}".Trim() })
            {
                if (!TunableValue.TryParse(end, out _, out _, out double si)) return null;
                if (lo is null || si < lo.Value.Si) lo = (end, si);
                if (hi is null || si > hi.Value.Si) hi = (end, si);
            }
        return new GoalRange { Axis = "freq", Lo = lo!.Value.Text, Hi = hi!.Value.Text };
    }

    private static GoalRange? RangeOf(TestBench tb, string analysis)
        => tb.Analyses.FirstOrDefault(a => a.Name == analysis) is SParameterAnalysis sp ? RangeOf(sp) : null;

    /// <summary>Identifier-shaped words of <paramref name="text"/>, outside string literals, each with
    /// whether a '.' follows it — the qualifier of an accessor (<c>SP1.S</c>).</summary>
    private static IEnumerable<(string Word, bool Dotted)> Words(string text)
    {
        int k = 0;
        while (k < text.Length)
        {
            char c = text[k];
            if (c == '"')
            {
                int end = text.IndexOf('"', k + 1);
                k = end < 0 ? text.Length : end + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = k;
                while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                yield return (text[start..k], k < text.Length && text[k] == '.');
                continue;
            }
            k++;
        }
    }

    /// <summary>A goal name is one word: everything else becomes '_'.</summary>
    internal static string Identifier(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string s = sb.ToString().Trim('_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length == 0) return "G1";
        return char.IsDigit(s[0]) ? "G" + s : s;
    }
}
