using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Statistics;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The statistical half of <c>explain</c> (docs/design/yield.md): each tunable's tolerance for
/// <c>--tunables</c>, and for <c>--analysis</c> the effective statistics settings, the goals by what
/// they serve, the corners, the correlation matrix a run would use and what the trial count can
/// resolve. It owns no rule — the numbers are <see cref="StatisticsSummary"/>'s and
/// <see cref="StatisticsValidator"/>'s.
/// </summary>
internal static class ExplainStatistics
{
    /// <summary>The <c>stat</c> object of one tunable row; null when its entry has no distribution.</summary>
    public static ExplainStatJson? Stat(TunableEntry? e, Tunable t)
    {
        if (e is null || e.Distribution == StatDistribution.None || StatisticsSummary.Describe(e, t) is not { } text)
            return null;
        var r = ResolvedSpread.Of(e, t);
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.Spread is { } sp)
            foreach (var (k, v) in new[] { ("sd", sp.Sd), ("tol", sp.Tol), ("sigmas", sp.Sigmas), ("lo", sp.Lo),
                                           ("hi", sp.Hi), ("by", sp.By), ("trunc", sp.Trunc) })
                if (v is not null) written[k] = v;
        return new ExplainStatJson(
            AnalysisDirectiveSchema.DistTokens[(int)e.Distribution], e.Stat, text,
            Core.Expressions.Units.BaseUnit(t.Unit), r?.Sigma, r?.Lo, r?.Hi, r?.Step, r?.Trunc, written);
    }

    /// <summary>
    /// The distribution calls the design holds, grouped process/mismatch (docs/design/yield.md §7); null when
    /// it holds none. Elaborated only when the text names a distribution somewhere, so a design without one
    /// pays nothing for the question.
    /// </summary>
    public static ExplainDistributionsJson? Distributions(Library lib, TestBench tb)
    {
        if (!MentionsDistribution(lib, tb)) return null;
        IReadOnlyList<StatisticalCall> calls;
        try
        {
            using var nl = new Elaborator(lib).Elaborate(tb);
            calls = nl.StatisticalCalls;
        }
        catch (Exception) { return null; }   // elaboration failures are check's to report
        if (calls.Count == 0) return null;

        static string KindOf(StatisticalCall c) => c.Kind == StatisticalKind.Process ? "process" : "mismatch";
        return new ExplainDistributionsJson(
            calls.Count(c => c.Kind == StatisticalKind.Process),
            calls.Count(c => c.Kind == StatisticalKind.Mismatch),
            [.. calls.OrderBy(c => c.Kind).Select(c => new ExplainDistributionJson(c.Function, KindOf(c), c.Stream))]);
    }

    private static bool MentionsDistribution(Library lib, TestBench tb)
    {
        static bool Any(IEnumerable<string?> texts) => texts.Any(t => t is not null && Evaluator.ContainsStatisticalCall(t));
        return Any(tb.GlobalVariables.Select(v => v.Expression))
            || Any(tb.Instances.SelectMany(i => i.Overrides).Select(o => o.Expression))
            || lib.Cells.Any(c => Any(c.Variables.Select(v => v.Expression))
                               || Any(c.Parameters.Select(p => p.DefaultExpression))
                               || Any(c.Instances.SelectMany(i => i.Overrides).Select(o => o.Expression)));
    }

    public static void PrintDistributions(ExplainDistributionsJson d)
    {
        Console.WriteLine();
        Console.WriteLine($"Distributions: {d.Process} process, {d.Mismatch} mismatch — nominal in every run but a Monte Carlo trial");
        foreach (var s in d.Streams) Console.WriteLine($"  {s.Kind,-8} {s.Function,-6} {s.Stream}");
    }

    /// <summary>The statistical report of a testbench; null when its setup has no statistical content.</summary>
    /// <param name="path">The document explained; a <c>.csch</c>'s corners report each kit axis they set or inherit.</param>
    /// <param name="parts">The schematic's placed vendor parts (<see cref="CircuitSource.Read(string, DocumentKind, out IReadOnlyList{CircuitRF.Design.VendorLibraries.VendorPartTunable}, Action{string}?)"/>):
    /// without them a part's value (<c>C1.C</c>) is no tunable, and the run constructors asked here refuse it.</param>
    public static ExplainStatisticsJson? Collect(Library lib, TestBench tb, string? path = null,
                                                 IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts = null)
    {
        if (tb.Tuning is not { } setup || !HasStatistics(setup)) return null;
        var s = setup.Statistics ?? new StatisticsSettings();
        var (halfWidth, trialsFor) = StatisticsSummary.ExpectedInterval(s.EffectiveTrials, s.EffectiveConfidence);
        var kit = path is not null && DocumentKinds.Classify(path) == DocumentKind.Schematic ? KitCorners.Of(path) : null;

        ExplainCorrelationJson? correlation = null;
        if (StatisticsValidator.CorrelationOf(setup) is { } m)
        {
            int n = m.Keys.Count;
            var rows = Enumerable.Range(0, n)
                .Select(i => (IReadOnlyList<double>)[.. Enumerable.Range(0, n).Select(j => m.Matrix[i, j])]).ToList();
            correlation = new ExplainCorrelationJson(m.Keys, rows, m.Repaired, m.LargestChange);
        }

        return new ExplainStatisticsJson(
            s.EffectiveTrials, s.EffectiveSeed,
            AnalysisDirectiveSchema.SamplingTokens[(int)s.Sampling], s.Target, s.EffectiveConfidence, s.AutoStop,
            AnalysisDirectiveSchema.NonConvergedTokens[(int)s.NonConverged], s.Save ?? "auto",
            s.Process ?? true, s.Mismatch ?? true, s.SigmaScale ?? 1, s.Parallelism,
            AnalysisDirectiveSchema.ScopeTokens[(int)s.Scope], s.Corners ?? "none",
            [.. setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key)],
            [.. setup.Goals.Select(g => new ExplainGoalUseJson(g.Name, AnalysisDirectiveSchema.UseTokens[(int)g.Use], g.Enabled))],
            [.. setup.Corners.Select(c => new ExplainCornerJson(
                c.Name, c.Enabled, c.IsStatistical ? "statistical" : "value", c.Temp,
                new Dictionary<string, string>(c.Values, StringComparer.Ordinal), c.Trial,
                Bindings(lib, tb, c, parts), kit?.Axes(c.Name)))],
            correlation, StatisticsSummary.QuotedYield, halfWidth, trialsFor, YieldRun(lib, tb, setup, s, parts),
            Center(lib, tb, setup, parts));
    }

    /// <summary>R-ya11-6: a center line's estimated total, asked of <see cref="CenteringRun"/>, which evaluates nothing.</summary>
    private static ExplainCenterJson? Center(Library lib, TestBench tb, TuningSetup setup,
                                             IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts)
    {
        if (setup.Centering is not { } c) return null;
        var run = CenteringRun.Create(PreparedCircuit.FromBench(lib, tb, null, parts));
        var e = run.Estimate();
        return new ExplainCenterJson(c.EffectiveAlgorithm, c.EffectiveTrials, c.EffectiveVerify, c.EffectiveWidth, c.EffectiveSeed,
            e?.Variables, e?.PointsPerIteration, e?.EvaluationsPerIteration, e?.Total, e?.Estimate, run.Refusal?.Render(),
            c.EffectiveSurrogate == CenteringSurrogate.None ? null : AnalysisDirectiveSchema.SurrogateTokens[(int)c.EffectiveSurrogate]);
    }

    /// <summary>
    /// R-ya5-8: which chains a yield run executes under each <c>analyses=</c> scope, through the run's own rule
    /// (<see cref="OptimizationRun.AnalysesUnder"/> over the yield specs), and its cost in nominal evaluations at the
    /// parallelism the run would use — asked of <see cref="StatisticalRun.Create"/>, which evaluates nothing.
    /// </summary>
    private static ExplainYieldRunJson YieldRun(Library lib, TestBench tb, TuningSetup setup, StatisticsSettings s,
                                                IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts)
    {
        var goals = OptimizationRun.AnalysesUnder(tb, setup, OptimizerScope.GoalAnalyses, GoalUse.Yield);
        var all   = OptimizationRun.AnalysesUnder(tb, setup, OptimizerScope.All, GoalUse.Yield);
        var run   = StatisticalRun.Create(PreparedCircuit.FromBench(lib, tb, null, parts), new StatisticalOptions { Mode = StatisticalMode.Yield });
        int trials = s.EffectiveTrials, parallel = run.Parallelism;
        int batches = (trials + parallel - 1) / parallel;
        string estimate = $"about {batches + 1} times one nominal evaluation: the nominal, then {batches} batch(es) of up to " +
                          $"{parallel} trials (an estimate)";
        return new ExplainYieldRunJson(goals, all, s.Scope == OptimizerScope.All ? "all" : "goals",
                                       trials + 1, parallel, batches, estimate, run.Refusal?.Render());
    }

    /// <summary>
    /// R-ya6-7: every binding a corner makes — its temp (°C) and each value — in base SI with its base unit. A bare
    /// number takes the unit of what it binds (the tunable's, or the variable's).
    /// </summary>
    private static IReadOnlyList<ExplainCornerBindingJson> Bindings(Library lib, TestBench tb, CornerDefinition c,
                                                                    IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts)
    {
        var list = new List<ExplainCornerBindingJson>();
        if (c.Temp is { } temp)
            list.Add(new ExplainCornerBindingJson(Core.Devices.Temperature.AmbientGlobalName, temp,
                double.TryParse(temp.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) ? t : null, "°C"));
        if (c.Values.Count == 0) return list;
        TunableCatalog? catalog = null;
        try { catalog = TunableCatalog.FromNetlist(tb, lib, parts: parts); } catch (Exception) { /* units then come from the text alone */ }
        foreach (var (key, text) in c.Values)
        {
            string known = catalog?.FindValue(key)?.Unit ?? tb.GlobalVariables.FirstOrDefault(v => v.Name == key)?.Unit ?? "";
            if (!TunableValue.TryParse(text, out double n, out string unit, out double si))
            {
                list.Add(new ExplainCornerBindingJson(key, text, null, known.Length == 0 ? "" : Units.BaseUnit(known)));
                continue;
            }
            if (unit.Length == 0 && known.Length > 0) { unit = known; si = n * (Units.Scale(known) ?? 1); }
            list.Add(new ExplainCornerBindingJson(key, text, si, unit.Length == 0 ? "" : Units.BaseUnit(unit)));
        }
        return list;
    }

    /// <summary>A schematic's corners against its workspace's kit axes: per corner, each axis's section and whether the
    /// corner sets it or inherits the schematic's selection (R-ya6-7).</summary>
    private sealed class KitCorners
    {
        private readonly IReadOnlyList<CircuitRF.Design.Workspace.WorkspaceCornerAxis> _axes;
        private readonly SchematicEditModel _model;

        private KitCorners(IReadOnlyList<CircuitRF.Design.Workspace.WorkspaceCornerAxis> axes, SchematicEditModel model)
        { _axes = axes; _model = model; }

        public static KitCorners? Of(string cschPath)
        {
            var axes = CircuitRF.Design.Workspace.WorkspaceCorners.ForDocument(cschPath);
            if (axes.Count == 0) return null;
            try { return new KitCorners(axes, CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(cschPath).model); }
            catch (Exception) { return null; }
        }

        public IReadOnlyList<ExplainCornerAxisJson> Axes(string corner)
        {
            var own = _model.Tuning?.Corners.FirstOrDefault(c => c.Name == corner)?.AxisSelections;
            return [.. _axes.Select(a =>
            {
                if (own is not null && own.TryGetValue(a.Key, out var set) && !string.IsNullOrWhiteSpace(set))
                    return new ExplainCornerAxisJson(a.Label, set, true);
                string inherited = _model.CornerSelections.TryGetValue(a.Key, out var chosen) && !string.IsNullOrWhiteSpace(chosen)
                    ? chosen : a.Options[0];
                return new ExplainCornerAxisJson(a.Label, inherited, false);
            })];
        }
    }

    private static bool HasStatistics(TuningSetup setup)
        => setup.Statistics is not null || setup.Correlations.Count > 0 || setup.Corners.Count > 0
        || setup.Variables.Any(e => e.Distribution != StatDistribution.None)
        || setup.Goals.Any(g => g.Use != GoalUse.Both) || setup.Centering is not null;

    public static void Print(ExplainStatisticsJson r)
    {
        static string N(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        Console.WriteLine();
        Console.WriteLine("Statistics:");
        Console.WriteLine($"  trials {r.Trials} · seed {r.Seed} · sampling {r.Sampling} · confidence {N(r.Confidence)} %" +
                          (r.Target is { } t ? $" · target {N(t)} %" : "") + (r.AutoStop ? " · autostop" : "") +
                          $" · non-converged {r.NonConverged} · save {r.Save}");
        Console.WriteLine($"  kit process {(r.Process ? "on" : "off")} · mismatch {(r.Mismatch ? "on" : "off")} · sigma scale {N(r.SigmaScale)}" +
                          $" · analyses {r.Analyses} · corners {r.Corners}");
        Console.WriteLine($"  statistical entries: {(r.StatisticalEntries.Count == 0 ? "none" : string.Join(", ", r.StatisticalEntries))}");
        foreach (var use in new[] { "yield", "both", "opt" })
        {
            var names = r.Goals.Where(g => g.Use == use).Select(g => g.Enabled ? g.Name : g.Name + " (disabled)").ToList();
            if (names.Count > 0) Console.WriteLine($"  goals use={use}: {string.Join(", ", names)}");
        }
        foreach (var c in r.CornerList)
        {
            string what = c.Kind == "statistical" ? $"trial {c.Trial}"
                        : string.Join(" ", (c.Temp is { } temp ? new[] { $"temp={temp}" } : []).Concat(c.Values.Select(kv => $"{kv.Key}={kv.Value}")));
            Console.WriteLine($"  corner {c.Name}{(c.Enabled ? "" : " (disabled)")}: {what}");
            foreach (var b in c.Bindings ?? [])
                Console.WriteLine($"    {b.Name} = {(b.Si is { } si ? N(si) + (b.Unit.Length > 0 ? " " + b.Unit : "") : b.Written)}");
            foreach (var a in c.KitAxes ?? [])
                Console.WriteLine($"    {a.Axis}: {a.Section} ({(a.Sets ? "set by the corner" : "the schematic's")})");
        }
        if (r.Correlation is { } m)
        {
            Console.WriteLine($"  correlation over {string.Join(", ", m.Keys)}" +
                              (m.Repaired ? $" — repaired to the nearest valid matrix, largest change {N(m.LargestChange)}" : ""));
            foreach (var row in m.Matrix) Console.WriteLine("    " + string.Join("  ", row.Select(v => v.ToString("F4", CultureInfo.InvariantCulture).PadLeft(7))));
        }
        if (!double.IsNaN(r.ExpectedHalfWidth))
            Console.WriteLine($"  at a yield of {N(r.AtYield)} %, {r.Trials} trials resolve it to ±{N(r.ExpectedHalfWidth)} %" +
                              (r.TrialsForTwoPercent is { } n ? $"; {n} trials resolve it to under ±{N(StatisticsSummary.QuotedHalfWidth)} %" : ""));
        if (r.Run is { } run)
        {
            Console.WriteLine($"  a yield run evaluates: goals → {(run.UnderGoals.Count == 0 ? "none" : string.Join(", ", run.UnderGoals))}" +
                              $" · all → {(run.UnderAll.Count == 0 ? "none" : string.Join(", ", run.UnderAll))} (setup={run.Selected})");
            Console.WriteLine($"  cost: {run.Evaluations} evaluations, {run.Parallel} at once — {run.Estimate}");
            if (run.Refusal is { } refused) Console.WriteLine($"  a yield run would be refused: {refused}");
        }
        if (r.Center is { } ctr)
        {
            Console.WriteLine($"  centering: {ctr.Algorithm} · {ctr.Trials} common trials · verify {ctr.Verify} · width {N(ctr.Width)} · seed {ctr.Seed}" +
                              (ctr.Surrogate is { } sg ? $" · surrogate {sg}" : ""));
            if (ctr.Estimate is { } estimate)
                Console.WriteLine($"  centering cost: {ctr.EvaluationsPerIteration} simulations per iteration ({ctr.PointsPerIteration} candidate(s) × " +
                                  $"{(ctr.Surrogate is null ? ctr.Trials : ctr.EvaluationsPerIteration / Math.Max(1, ctr.PointsPerIteration ?? 1))}) — {estimate}");
            if (ctr.Refusal is { } refused) Console.WriteLine($"  centering would be refused: {refused}");
        }
    }
}
