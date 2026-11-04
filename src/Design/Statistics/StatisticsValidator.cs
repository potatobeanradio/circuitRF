using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// The rules a setup's statistical part must satisfy (docs/design/yield.md §4) — the tolerances on its
/// entries, its correlations, its statistics settings, its corners and its yield goals. Called by
/// <see cref="TuningValidator"/>, so <c>check</c> reports them, and by every run that draws, so a setup
/// <c>check</c> passes never refuses later for a reason <c>check</c> could have seen. Diagnostics are
/// RETURNED; the caller decides where they go.
/// </summary>
public static class StatisticsValidator
{
    /// <summary>A draw at or below zero more likely than this is a warning (yield overview D2).</summary>
    public const double NonPhysicalThreshold = 1e-9;

    /// <param name="netlist">The design elaborated, when the caller has it: its distribution calls are what
    /// varies besides the tolerances. Without it the testbench's own text is read for one.</param>
    /// <param name="lib">The design's cells, when the caller has them: a corner's <c>part:</c> selection is checked
    /// against the parts placed in it. Without it that rule is skipped, not guessed.</param>
    public static IReadOnlyList<Diagnostic> Validate(TestBench tb, TunableCatalog catalog,
                                                     ElaboratedNetlist? netlist = null, Library? lib = null)
    {
        var f = new List<Diagnostic>();
        if (tb.Tuning is not { } setup) return f;

        f.AddRange(ValidateSetup(setup, catalog));
        Corners(setup, tb, catalog, lib, f);

        if (!setup.Variables.Any(e => e.IsStatistical) && !HoldsDistribution(tb, netlist))
            foreach (var g in setup.Goals.Where(g => g.Enabled && g.Use == GoalUse.Yield))
                f.Add(StatisticsDiagnostics.NothingVaries(g.Name));
        return f;
    }

    /// <summary>
    /// The findings a tuning block holds on its own — every entry's tolerance, the complex parts, the correlations and
    /// the statistics line — without the testbench its corners bind against. What a panel edit is checked with, so the
    /// Yield panel refuses an edit in <c>check</c>'s own words.
    /// </summary>
    public static IReadOnlyList<Diagnostic> ValidateSetup(TuningSetup setup, TunableCatalog catalog)
    {
        var f = new List<Diagnostic>();
        // A non-physical draw is asked of the spread a trial actually draws from, sigmascale applied (R-ya15-7); an
        // out-of-range scale is the settings' own refusal, so it is not applied here.
        double sigmaScale = setup.Statistics?.SigmaScale is { } k && k > 0 ? k : 1;
        foreach (var e in setup.Variables) Entry(e, catalog, sigmaScale, f);
        ComplexParts(setup, f);
        Correlations(setup, f);
        Settings(setup.Statistics, f);
        SnpExclude(setup, catalog, f);
        return f;
    }

    /// <summary>
    /// brief-sens-2 R-sens2-5a: a name in <c>snpexclude</c> that is no SnP instance is a WARNING, ignored by the run —
    /// deleting an instance from the schematic must not make the bench unrunnable.
    /// </summary>
    private static void SnpExclude(TuningSetup setup, TunableCatalog catalog, List<Diagnostic> f)
    {
        if (setup.Sensitivity?.SnpExclude is not { } list) return;
        var snp = catalog.SnpInstances.Select(s => s.Instance).ToList();
        foreach (var name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal))
            if (!snp.Contains(name, StringComparer.Ordinal))
                f.Add(StatisticsDiagnostics.SensSnpExcludeUnknown(name, snp.Count == 0 ? "it has none" : string.Join(", ", snp)));
    }

    /// <summary>
    /// brief-sens-2 R-sens2-3: an SnP stated <c>Reciprocal=tied</c> whose file is not reciprocal — allowed, and warned once
    /// per instance with the worst difference in dB, because one offset will move two entries that differ.
    /// </summary>
    public static IReadOnlyList<Diagnostic> SnpTieWarnings(TunableCatalog catalog)
    {
        var f = new List<Diagnostic>();
        foreach (var s in catalog.SnpInstances)
        {
            if (s.Mode != RfCore.SnpReciprocal.Tied || s.Reciprocity is not { } r || r.Worst <= RfCore.SnpPerturbation.ReciprocalTolerance) continue;
            string pair = s.Ports > 9 ? $"S({r.I},{r.J}) and S({r.J},{r.I})" : $"S{r.I}{r.J} and S{r.J}{r.I}";
            string at = $"{(r.Hz / 1e9).ToString("G4", System.Globalization.CultureInfo.InvariantCulture)} GHz";
            f.Add(StatisticsDiagnostics.SnpTiedNotReciprocal(s.Instance, pair, r.WorstDb, at));
        }
        return f;
    }

    /// <summary>
    /// The first error <paramref name="after"/> holds that <paramref name="before"/> did not: the refusal an edit from
    /// <paramref name="before"/> to <paramref name="after"/> states (brief-yield-10 R-ya10-3). Null when the edit adds no
    /// error — a problem the setup already had is not this edit's to refuse.
    /// </summary>
    public static Diagnostic? EditRefusal(TuningSetup? before, TuningSetup after, TunableCatalog catalog)
    {
        var had = before is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : ValidateSetup(before, catalog).Where(d => d.Severity == DiagnosticSeverity.Error)
                                            .Select(d => d.Render()).ToHashSet(StringComparer.Ordinal);
        return ValidateSetup(after, catalog)
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error && !had.Contains(d.Render()));
    }

    // A kit's or a VAR's distribution call varies too (docs/design/yield.md §7).
    private static bool HoldsDistribution(TestBench tb, ElaboratedNetlist? netlist)
        => netlist is not null
            ? netlist.StatisticalCalls.Count > 0
            : tb.GlobalVariables.Any(v => Evaluator.ContainsStatisticalCall(v.Expression))
              || tb.Instances.Any(i => i.Overrides.Any(o => Evaluator.ContainsStatisticalCall(o.Expression)));

    // ── One entry ────────────────────────────────────────────────────────────

    private static void Entry(TunableEntry e, TunableCatalog catalog, double sigmaScale, List<Diagnostic> f)
    {
        string who = $"tune {e.Key}";
        var sp = e.Spread ?? new StatSpread();
        string dist = Word(e.Distribution);

        if (e.Distribution == StatDistribution.None)
        {
            foreach (var (key, _) in Written(sp)) f.Add(StatisticsDiagnostics.SpreadExtra(who, dist, key));
            return;
        }

        if (catalog.Find(e.Key) is not { } t)
        {
            f.Add(StatisticsDiagnostics.NotTunable(who));
            return;
        }
        if (!e.Stat) f.Add(StatisticsDiagnostics.DistributionOff(who));

        // Exactly the keys the distribution reads (D2).
        var allowed = e.Distribution switch
        {
            StatDistribution.Gauss or StatDistribution.LogNorm
                => sp.Sd is not null ? ["sd", "trunc"] : new[] { "tol", "sigmas", "trunc" },
            StatDistribution.Unif
                => sp.Tol is not null ? ["tol"] : new[] { "lo", "hi" },
            _   => ["lo", "hi", "by"],
        };
        bool shapeOk = true;
        foreach (var (key, _) in Written(sp))
            if (!allowed.Contains(key)) { f.Add(StatisticsDiagnostics.SpreadExtra(who, dist, key)); shapeOk = false; }
        string? missing = e.Distribution switch
        {
            StatDistribution.Gauss or StatDistribution.LogNorm when sp.Sd is null && sp.Tol is null => "sd=, or tol= with sigmas=",
            StatDistribution.Gauss or StatDistribution.LogNorm when sp.Sd is null && sp.Sigmas is null => "sigmas= with tol=",
            StatDistribution.Unif when sp.Tol is null && (sp.Lo is null || sp.Hi is null) => "tol=, or lo= and hi=",
            StatDistribution.Discrete when sp.Lo is null || sp.Hi is null || sp.By is null => "lo=, hi= and by=",
            _ => null,
        };
        if (missing is not null) { f.Add(StatisticsDiagnostics.SpreadMissing(who, dist, missing)); shapeOk = false; }

        if (t.IsInteger && e.Distribution is StatDistribution.Gauss or StatDistribution.LogNorm)
            f.Add(StatisticsDiagnostics.ContinuousOnInteger(who, dist));

        double scale = t.Unit.Length == 0 ? 1 : Core.Expressions.Units.Scale(t.Unit) ?? 1;
        double nominal = t.Value * scale;
        if (e.Distribution == StatDistribution.LogNorm && nominal <= 0)
            f.Add(StatisticsDiagnostics.LogNormNotPositive(who, t.ValueText));

        // Each written value must be one, and the widths positive.
        foreach (var (key, written) in Written(sp))
        {
            string text = written;
            // tol=part: the vendor part's own tolerance, resolved from the index (brief vendorlib-5 R-vl5-3b).
            if (key == "tol" && sp.TolIsPart)
            {
                if (!t.IsPartValue) { f.Add(StatisticsDiagnostics.TolPartNotAPart(who)); shapeOk = false; continue; }
                if (t.PartTolerance is not { } own)
                {
                    f.Add(StatisticsDiagnostics.TolPartUnknown(who, t.PartEntry?.PartNumber ?? t.Owner,
                                                                t.PartToleranceNote ?? VendorLibraries.VendorPartToleranceDecoder.NotKnown));
                    shapeOk = false;
                    continue;
                }
                text = own.SpreadText;
            }
            // A unit of another quantity converts to a number, and a meaningless one: 3 pF on a resistance is 3e-12 Ω.
            if (key is not ("sigmas" or "trunc") && ResolvedSpread.ForeignUnit(text, t.Unit))
            { f.Add(StatisticsDiagnostics.SpreadWrongUnit(who, key, text, t.Unit)); shapeOk = false; continue; }
            double? v = key is "sigmas" or "trunc" ? ResolvedSpread.Number(text)
                      : key is "lo" or "hi" ? ResolvedSpread.Point(text, nominal, t.Unit)
                      : ResolvedSpread.Width(text, nominal, t.Unit);
            if (v is null) { f.Add(StatisticsDiagnostics.SpreadNotANumber(who, key, text)); shapeOk = false; }
            else if (key is "sd" or "tol" or "sigmas" or "trunc" or "by" && v <= 0)
            { f.Add(StatisticsDiagnostics.SpreadNotPositive(who, key, text)); shapeOk = false; }
        }
        if (!shapeOk || ResolvedSpread.Of(e, t) is not { } r) return;

        if (r.Lo is { } lo && r.Hi is { } hi)
        {
            if (lo >= hi) { f.Add(StatisticsDiagnostics.SpreadInverted(who, sp.Lo ?? "", sp.Hi ?? "")); return; }
            if (r.Step is { } by)
            {
                double steps = (hi - lo) / by;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-6 * Math.Max(1, steps))
                    f.Add(StatisticsDiagnostics.StepNotDividing(who, sp.By!, sp.Lo!, sp.Hi!));
            }
        }

        if (e.Stat && MustBePositive(t, nominal) && r.NonPhysicalProbability(sigmaScale) is var p && p > NonPhysicalThreshold)
            f.Add(StatisticsDiagnostics.NonPhysical(who, p));
    }

    /// <summary>A resistance, capacitance, inductance, conductance or length with a positive nominal —
    /// or a magnitude — is non-physical at or below zero.</summary>
    internal static bool MustBePositive(Tunable t, double nominal)
    {
        if (nominal <= 0) return false;
        if (t.Part == ComplexPart.Mag) return true;
        if (t.Part is not null) return false;
        return Core.Expressions.Units.BaseUnit(t.Unit) is "Ohm" or "F" or "H" or "S" or "metre";
    }

    // ── Complex parts (D2) ───────────────────────────────────────────────────

    private static void ComplexParts(TuningSetup setup, List<Diagnostic> f)
    {
        var byWhole = setup.Variables
            .Where(e => e.Distribution != StatDistribution.None)
            .Select(e => TunableKey.TryParse(e.Key, out var k) && k.Part is { } part ? (Whole: k.Whole.ToString(), Part: part, e.Key) : default)
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Whole, StringComparer.Ordinal);
        foreach (var g in byWhole)
        {
            var parts = g.Select(x => x.Part).Distinct().ToList();
            string list = string.Join(", ", g.Select(x => x.Key));
            if (parts.Count > 2) f.Add(StatisticsDiagnostics.TooManyParts(g.Key, list));
            else if (parts.Count == 2 && !(parts.All(p => p is ComplexPart.Real or ComplexPart.Imag)
                                           || parts.All(p => p is ComplexPart.Mag or ComplexPart.Phase)))
                f.Add(StatisticsDiagnostics.MixedParts(g.Key, list));
        }
    }

    // ── Correlations (D3) ────────────────────────────────────────────────────

    private static void Correlations(TuningSetup setup, List<Diagnostic> f)
    {
        var stat = setup.Variables.Where(e => e.IsStatistical).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        bool valid = true;
        foreach (var c in setup.Correlations)
        {
            string who = $"correlate {c.First} {c.Second}";
            if (c.First == c.Second) { f.Add(StatisticsDiagnostics.CorrelateSameKey(who)); valid = false; }
            foreach (var key in new[] { c.First, c.Second }.Distinct())
                if (!stat.Contains(key)) { f.Add(StatisticsDiagnostics.CorrelateNotStatistical(who, key)); valid = false; }
            if (!(c.Rho > -1 && c.Rho < 1)) { f.Add(StatisticsDiagnostics.RhoOutOfRange(who, c.Rho)); valid = false; }
        }
        if (valid && CorrelationOf(setup) is { Repaired: true } m)
            f.Add(StatisticsDiagnostics.CorrelationRepaired(m.LargestChange));
    }

    /// <summary>The correlation matrix the setup's <c>correlate</c> lines describe, over the statistical
    /// entries they name in setup order, and — when it is not positive definite — the nearest one that is
    /// (D3). Null when there are no correlations. A pair stated twice takes its last value.</summary>
    public static CorrelationMatrix? CorrelationOf(TuningSetup setup)
    {
        if (setup.Correlations.Count == 0) return null;
        var named = setup.Correlations.SelectMany(c => new[] { c.First, c.Second }).ToHashSet(StringComparer.Ordinal);
        var keys = setup.Variables.Select(e => e.Key).Where(named.Contains).Distinct().ToList();
        foreach (var k in named.Where(k => !keys.Contains(k))) keys.Add(k);

        int n = keys.Count;
        var a = new double[n, n];
        for (int i = 0; i < n; i++) a[i, i] = 1;
        foreach (var c in setup.Correlations)
        {
            int i = keys.IndexOf(c.First), j = keys.IndexOf(c.Second);
            if (i == j) continue;
            a[i, j] = a[j, i] = c.Rho;
        }
        if (NearestCorrelation.IsPositiveDefinite(a)) return new CorrelationMatrix(keys, a, false, 0);
        var (fixedMatrix, change) = NearestCorrelation.Repair(a);
        return new CorrelationMatrix(keys, fixedMatrix, true, change);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private static void Settings(StatisticsSettings? s, List<Diagnostic> f)
    {
        // A Monte Carlo at each corner draws mismatch only (brief-yield-6 R-ya6-3); process=1 asked for both.
        if (s is { Process: true } && s.CornerNames is not { Count: 0 }) f.Add(StatisticsDiagnostics.CornerProcessDoubleCounted());
        if (s is null) return;

        // Out of range is refused here, before anything draws or counts (brief-yield-15 R-ya15-1): a 100 % interval
        // has no Clopper–Pearson bound, and zero trials no yield.
        static string N(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        if (s.Confidence is { } c && !(c > 0 && c < 100))
            f.Add(StatisticsDiagnostics.SettingOutOfRange("confidence", N(c) + "%", "above 0% and below 100%"));
        if (s.Target is { } t && !(t > 0 && t <= 100))
            f.Add(StatisticsDiagnostics.SettingOutOfRange("target", N(t) + "%", "above 0% and at most 100%"));
        if (s.Trials is { } n && n < 1)
            f.Add(StatisticsDiagnostics.SettingOutOfRange("trials", N(n), "1 or more"));
        if (s.SigmaScale is { } k && !(k > 0))
            f.Add(StatisticsDiagnostics.SettingOutOfRange("sigmascale", N(k), "above zero"));
        if (s.Parallelism is { } p && p < 1)
            f.Add(StatisticsDiagnostics.SettingOutOfRange("parallel", N(p), "1 or more"));

        if (!s.AutoStop) return;
        if (s.Sampling == StatSampling.Lhs) f.Add(StatisticsDiagnostics.LhsWithAutoStop());
        if (s.Target is null) f.Add(StatisticsDiagnostics.AutoStopNeedsTarget());
    }

    // ── Corners (D10) ────────────────────────────────────────────────────────

    private static void Corners(TuningSetup setup, TestBench tb, TunableCatalog catalog, Library? lib, List<Diagnostic> f)
    {
        var globals = tb.GlobalVariables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in setup.Corners)
        {
            string who = $"corner {c.Name}";
            if (c.Temp is { } temp && !TunableValue.TryParse(temp, out _, out _, out _))
                f.Add(StatisticsDiagnostics.CornerTempNotANumber(who, temp));

            if (c.Trial is not null)
            {
                var missing = new List<string>();
                if (c.Seed is null)     missing.Add("seed=");
                if (c.Sampling is null) missing.Add("sampling=");
                if (c.Trials is null)   missing.Add("trials=");
                if (missing.Count > 0) f.Add(StatisticsDiagnostics.CornerTrialIncomplete(who, string.Join(", ", missing)));
            }

            foreach (var key in c.Values.Keys)
            {
                bool known = TunableKey.TryParse(key, out var k) && k.IsVariable && k.Cell is null && k.Part is null
                    ? globals.Contains(key) || catalog.FindValue(key) is not null
                    : catalog.FindValue(key) is not null;
                if (!known) f.Add(StatisticsDiagnostics.CornerUnknownKey(who, key));
            }

            // A library part's corner (brief vendorlib-13 R-vl13-2e) names a part NUMBER the design places and one of
            // that part's corners. Applying it skips anything else with only a note, so the corner would run as the
            // nominal and report the nominal's margins under its own name.
            if (lib is null) continue;
            foreach (var (part, corner) in c.PartCorners ?? [])
            {
                var cell = lib.Cells.FirstOrDefault(x => string.Equals(x.Part, part, StringComparison.Ordinal));
                if (cell is null)
                {
                    var placed = lib.Cells.Select(x => x.Part).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                    f.Add(StatisticsDiagnostics.CornerUnknownPart(who, part, placed.Count == 0 ? "none" : string.Join(", ", placed)));
                }
                else if (!corner.Trim().Equals(CellCorner.Nominal, StringComparison.OrdinalIgnoreCase)
                         && !cell.Corners.Any(x => x.Name.Equals(corner.Trim(), StringComparison.OrdinalIgnoreCase)))
                    f.Add(StatisticsDiagnostics.CornerUnknownPartCorner(who, part, corner.Trim(),
                        string.Join(", ", new[] { CellCorner.Nominal }.Concat(cell.Corners.Select(x => x.Name)))));
            }
        }
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>The spread keys written on an entry, in the writer's order.</summary>
    internal static IEnumerable<(string Key, string Text)> Written(StatSpread sp)
    {
        if (sp.Sd     is { } sd) yield return ("sd", sd);
        if (sp.Tol    is { } tol) yield return ("tol", tol);
        if (sp.Sigmas is { } k) yield return ("sigmas", k);
        if (sp.Lo     is { } lo) yield return ("lo", lo);
        if (sp.Hi     is { } hi) yield return ("hi", hi);
        if (sp.By     is { } by) yield return ("by", by);
        if (sp.Trunc  is { } tr) yield return ("trunc", tr);
    }

    internal static string Word(StatDistribution d)
        => Core.Netlist.AnalysisDirectiveSchema.DistTokens[(int)d];
}

/// <summary>A correlation matrix over <see cref="Keys"/>, repaired to the nearest valid one when
/// <see cref="Repaired"/>, with the largest change that made.</summary>
public sealed record CorrelationMatrix(IReadOnlyList<string> Keys, double[,] Matrix, bool Repaired, double LargestChange);
