using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// The rules a tuning setup must satisfy — what <c>circuitrf check</c> reports, and what the tuning
/// and optimizer windows refuse on. One implementation, so a setup <c>check</c> calls clean is one
/// the application accepts. Diagnostics are RETURNED (<see cref="TuningDiagnostics"/>); the caller
/// decides where they go.
/// </summary>
public static class TuningValidator
{
    /// <param name="netlist">The elaborated design, for the resolved globals a sweep's grid is
    /// computed with. Null when elaboration failed — the grid rule is then skipped, not guessed.</param>
    public static IReadOnlyList<Diagnostic> Validate(
        TestBench tb, TunableCatalog catalog, ElaboratedNetlist? netlist = null)
    {
        var f = new List<Diagnostic>();
        if (tb.Tuning is not { } setup) return f;

        foreach (var e in setup.Variables)
        {
            string who = $"tune {e.Key}";
            KeyRule(e.Key, who, f, catalog, inPreset: false);

            // A placed vendor part's candidates (brief vendorlib-4 R-vl4-1c/6a); kind= and parts= mean nothing elsewhere.
            if (catalog.Find(e.Key) is { VendorPart: { } part }) PartRules(e, part, who, f);
            else if (e.PartKind is not null || e.Parts is not null) f.Add(TuningDiagnostics.PartSelectionNotAPart(who));

            // A part is continuous, and preferred needs a ladder (brief-tuneopt-8 R-to8-2/7).
            if (e.Discrete != TuneDiscrete.None && catalog.Find(e.Key) is { VendorPart: null } t
                && !TunableValue.DiscreteChoices(t).Contains(e.Discrete))
                f.Add(t.Part is not null
                    ? OptimizationDiagnostics.DiscreteOnPart(e.Key, TunableValue.DiscreteToken(e.Discrete))
                    : OptimizationDiagnostics.PreferredNoLadder(e.Key, t.Unit));

            double? min = Number(e.Min, who, "min", f), max = Number(e.Max, who, "max", f);
            if (min is { } lo && max is { } hi)
            {
                if (lo >= hi)
                    f.Add(TuningDiagnostics.RangeInverted(who, e.Min!, e.Max!));
                else if (e.Scale == TuneScale.Log && lo <= 0)
                    f.Add(TuningDiagnostics.LogNeedsPositiveMin(who, e.Min!));
                else if (TunableKey.TryParse(e.Key, out var k) && k.Part == ComplexPart.Phase
                         && TunableValue.InUnit(e.Max, "deg") - TunableValue.InUnit(e.Min, "deg") > 360)
                    f.Add(TuningDiagnostics.PhaseSpanTooWide(who, e.Min!, e.Max!));
            }
        }

        // The ranges of one complex value's parts must leave it somewhere to be (overview D18).
        var wholes = setup.Variables
            .Select(e => TunableKey.TryParse(e.Key, out var k) && k.Part is not null ? k.Whole.ToString() : null)
            .OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var whole in wholes)
        {
            string unit = catalog.PartsOf(whole).FirstOrDefault()?.WholeUnit ?? "";
            if (ComplexRegion.Of(setup, whole, unit).IsEmpty)
                f.Add(TuningDiagnostics.ComplexRangesDisjoint(whole, string.Join(", ", setup.Variables
                    .Where(e => TunableKey.TryParse(e.Key, out var k) && k.Part is not null && k.Whole.ToString() == whole)
                    .Select(e => $"{e.Key} {e.Min} .. {e.Max}"))));
        }

        foreach (var p in setup.Presets)
        {
            if (p.Name.Contains('"')) f.Add(TuningDiagnostics.PresetNameQuote(p.Name));
            foreach (var key in p.Values.Keys)
                KeyRule(key, $"preset \"{p.Name}\": {key}", f, catalog, inPreset: true);
        }

        var declared = tb.Analyses.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var g in setup.Goals)
        {
            string who = $"goal {g.Name}";

            try { Parser.Parse(g.Expression); }
            catch (ExpressionException ex) { f.Add(TuningDiagnostics.ExpressionUnparsed(who, ex.Message)); }

            if (g.Analysis is { } a && !declared.Contains(a))
                f.Add(TuningDiagnostics.AnalysisUndeclared(who, a,
                    declared.Count == 0 ? "it declares none" : string.Join(", ", declared)));

            double? limit = Number(g.Limit, who, "limit", f);
            if (g.Type is GoalType.In or GoalType.Out)
            {
                if (g.UpperLimit is null)
                    f.Add(TuningDiagnostics.BandNeedsTwoLimits(who, g.Type.ToString().ToLowerInvariant()));
                // Edges given high-first are simply read the other way round (GoalResiduals.Score).
                else if (limit is { } l && Number(g.UpperLimit, who, "upper limit", f) is { } u && l == u)
                    f.Add(TuningDiagnostics.BandEmpty(who, g.Limit));
            }
            else if (g.LimitAtHi is { } atHi)
            {
                Number(atHi, who, "limit after 'to'", f);
                if (g.Range is null) f.Add(TuningDiagnostics.SlopeNeedsRange(who));
            }

            if (g.Scale is { } sc && Number(sc, who, "scale", f) is <= 0)
                f.Add(TuningDiagnostics.GoalScaleNotPositive(who, sc));

            if (g.Range is { } r) RangeRule(g, r, tb, netlist, f);
        }

        if (setup.Optimizer is { } o)
        {
            if (OptimizerAlgorithms.Find(o.Algorithm) is not { } info)
                f.Add(TuningDiagnostics.UnknownAlgorithm(o.Algorithm, string.Join(", ", OptimizerAlgorithms.Ids)));
            else
            {
                if (o.Cost == OptimizerCost.Minimax && !info.Accepts(OptimizerCost.Minimax))
                    f.Add(OptimizationDiagnostics.LeastSquaresOnly(info.Label));
                // Auto's options are those of the methods it runs, so only a named method is checked.
                if (info.Id != OptimizerAlgorithms.Auto)
                {
                    var known = info.Options.Concat(OptimizerAlgorithms.CommonOptions).Select(x => x.Name).ToList();
                    foreach (var name in o.Options?.Keys ?? Enumerable.Empty<string>())
                        if (!known.Contains(name))
                            f.Add(TuningDiagnostics.UnknownAlgorithmOption(name, info.Label, string.Join(", ", known)));
                        else if (info.Option(name)?.Choices is { } choices && !choices.Contains(o.Options![name]))
                            f.Add(TuningDiagnostics.AlgorithmOptionChoice(name, o.Options![name], string.Join(" or ", choices)));
                }
            }
            if (o.TimeLimit is { } limit && TimeLimitSeconds(limit) is null)
                f.Add(TuningDiagnostics.TimeLimitInvalid(limit));
        }

        // The statistical part — tolerances, correlations, statistics settings, corners (docs/design/yield.md).
        f.AddRange(Statistics.StatisticsValidator.Validate(tb, catalog, netlist));
        return f;
    }

    /// <summary>
    /// A vendor part entry (R-vl4-1c, R-vl4-3d, R-vl4-6a): it needs a candidate, an unreadable candidate is said,
    /// and its levels are its candidates — never a series, a step or a tolerance. There is no upper limit (issue #32).
    /// </summary>
    private static void PartRules(TunableEntry e, CircuitRF.Design.VendorLibraries.VendorPartTunable part, string who,
                                  List<Diagnostic> f)
    {
        if (part.Candidates.Count == 0)
            f.Add(TuningDiagnostics.PartNoCandidates(who, part.Selection,
                part.RemovedForPorts > 0 ? $"; {part.RemovedForPorts} found have another port count than {part.PlacedPart}" : ""));
        if (part.Unreadable.Count > 0)
            f.Add(TuningDiagnostics.PartUnreadable(who, string.Join(", ", part.Unreadable)));
        if (e.Distribution != StatDistribution.None)
            f.Add(TuningDiagnostics.PartNotStatistical(who));
        if (e.Discrete != TuneDiscrete.None || e.Step is not null || e.Scale != TuneScale.Auto)
            f.Add(TuningDiagnostics.PartLevelsAreCandidates(who));
    }

    /// <summary>A <c>timelimit</c> in seconds — a number with an optional s, ms, min or h — or null.</summary>
    public static double? TimeLimitSeconds(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            || n <= 0)
            return null;
        return (parts.Length == 1 ? "s" : parts[1]) switch
        {
            "s" => n, "ms" => n / 1000, "min" => n * 60, "h" => n * 3600, _ => (double?)null,
        };
    }

    /// <summary>A key resolving to nothing is a warning (overview D3); one naming a value that is not
    /// offered — an expression, a port number — is an error, since it can never be tuned.</summary>
    private static void KeyRule(string key, string who, List<Diagnostic> f, TunableCatalog catalog, bool inPreset)
    {
        // A preset holds a complex value whole, under its own key; an entry names one part of it.
        if ((inPreset ? catalog.FindValue(key) : catalog.Find(key)) is not null) return;
        f.Add(catalog.WhyNotOffered(key) is { } why
            ? TuningDiagnostics.KeyNotOffered(who, why)
            : TuningDiagnostics.KeyUnresolved(who, inPreset));
    }

    /// <summary>Value text in base SI, or null — reporting it when it is present and not a number.</summary>
    private static double? Number(string? text, string who, string what, List<Diagnostic> f)
    {
        if (text is null) return null;
        if (TunableValue.TryParse(text, out _, out _, out double si)) return si;
        f.Add(TuningDiagnostics.NotANumber(who, what, text));
        return null;
    }

    /// <summary>
    /// The range must be a range, and on an axis whose grid is known it must hold at least one grid
    /// point — the rule <c>max_over</c> applies at run time, said before anything runs.
    /// </summary>
    private static void RangeRule(OptimizationGoal g, GoalRange r, TestBench tb, ElaboratedNetlist? netlist, List<Diagnostic> f)
    {
        string who = $"goal {g.Name}";
        double? lo = Number(r.Lo, who, "range's lo", f), hi = Number(r.Hi, who, "range's hi", f);
        if (lo is not { } a || hi is not { } b) return;
        if (a > b) (a, b) = (b, a);   // ends given high-first are read the other way round

        if (netlist is null || g.Analysis is null) return;
        var grid = GridOf(tb, g.Analysis, r.Axis, netlist);
        if (grid is not { Length: > 0 }) return;   // an axis whose grid is not known before a run

        double tol = 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
        if (!grid.Any(x => x >= a - tol && x <= b + tol))
            f.Add(TuningDiagnostics.GoalRangeEmpty(who, r.Lo, r.Hi, r.Axis, g.Analysis, grid.Min(), grid.Max()));
    }

    /// <summary>The points of one axis of one analysis, in base SI — <c>freq</c> of an S-parameter
    /// sweep (through a wrapping parametric sweep too), or a parametric sweep's own variable. Null for
    /// any other axis.</summary>
    private static double[]? GridOf(TestBench tb, string analysis, string axis, ElaboratedNetlist nl)
    {
        var a = tb.Analyses.FirstOrDefault(x => x.Name == analysis);
        for (int depth = 0; a is not null && depth < 8; depth++)
        {
            switch (a)
            {
                case ParametricSweepAnalysis ps when ps.SweepVarName == axis:
                    return ps.SweepValues;
                case ParametricSweepAnalysis ps:
                    a = tb.Analyses.FirstOrDefault(x => x.Name == ps.InnerAnalysisName);
                    continue;
                case SParameterAnalysis sp when axis == "freq":
                    try
                    {
                        return [.. sp.Sweeps.SelectMany(s => s.Expand(nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit))];
                    }
                    catch (Exception) { return null; }
                default:
                    return null;
            }
        }
        return null;
    }
}
