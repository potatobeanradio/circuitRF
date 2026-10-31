using CircuitRF.Core.Design;
using CircuitRF.Design.Statistics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// The edits the Tuning and Optimizer panels make to a schematic's variable entries (overview D4/D5),
/// as pure functions from one setup to the next. The caller wraps the result in an undoable command;
/// nothing here touches a document. Every function returns a NEW setup and leaves its argument alone,
/// so the old one can stand as the undo state.
/// </summary>
public static class TuningSetupEdits
{
    /// <summary>
    /// Sets or clears the <c>tune</c> flag of <paramref name="tunable"/>. Turning it on for a key with
    /// no entry creates one with the D4 default range; turning it off leaves the entry (its range is
    /// the user's) unless nothing else of it is set, in which case the entry goes.
    /// </summary>
    public static TuningSetup WithTune(TuningSetup? setup, Tunable tunable, bool on)
        => WithFlag(setup, tunable, on, (e, v) => e.Tune = v);

    /// <summary>The same for the <c>opt</c> flag — the Optimizer panel's half of one shared entry.</summary>
    public static TuningSetup WithOpt(TuningSetup? setup, Tunable tunable, bool on)
        => WithFlag(setup, tunable, on, (e, v) => e.Opt = v);

    private static TuningSetup WithFlag(TuningSetup? setup, Tunable tunable, bool on, Action<TunableEntry, bool> set)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == tunable.Key);
        if (entry is null)
        {
            if (!on) return next;
            entry = NewEntry(tunable);
            next.Variables.Add(entry);
        }
        // A tolerance-only entry carries no range (WithStat); the first tune or opt gives it the D4 default. A vendor
        // part's min/max bound its candidates' value, and it starts with none (brief vendorlib-4 R-vl4-5c).
        if (on && entry.Min is null && entry.Max is null && !tunable.IsVendorPart) { entry.Min = tunable.DefaultMin; entry.Max = tunable.DefaultMax; }
        set(entry, on);
        if (!entry.Tune && !entry.Opt && IsDefaultShaped(entry, tunable)) next.Variables.Remove(entry);
        return next;
    }

    /// <summary>A first activation's entry: the D4 default range, everything else default — except a
    /// phase, which is linear: an angle's range is not a ratio (overview D18).</summary>
    public static TunableEntry NewEntry(Tunable tunable) => tunable.IsVendorPart
        // A placed vendor part: the parts of the same kind, unbounded (brief vendorlib-4 R-vl4-5c, D1).
        ? new() { Key = tunable.Key, PartKind = "same" }
        : new()
    {
        Key      = tunable.Key,
        Min      = tunable.DefaultMin,
        Max      = tunable.DefaultMax,
        Scale    = DefaultScale(tunable),
        Discrete = tunable.IsInteger ? TuneDiscrete.Integer : TuneDiscrete.None,
    };

    private static TuneScale DefaultScale(Tunable t) => t.Part == ComplexPart.Phase ? TuneScale.Lin : TuneScale.Auto;

    /// <summary>Removes the entry for <paramref name="key"/> outright — the row's "Remove from tuning"
    /// when the optimizer does not use it either; otherwise only its tune flag clears.</summary>
    public static TuningSetup WithoutTuning(TuningSetup? setup, string key)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == key);
        if (entry is null) return next;
        if (entry.Opt) entry.Tune = false;
        else next.Variables.Remove(entry);
        return next;
    }

    /// <summary>Changes one entry in place (range, scale, step), creating it from
    /// <paramref name="tunable"/> when absent.</summary>
    public static TuningSetup WithEntry(TuningSetup? setup, string key, Tunable? tunable, Action<TunableEntry> change)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == key);
        if (entry is null)
        {
            if (tunable is null) return next;
            entry = NewEntry(tunable);
            next.Variables.Add(entry);
        }
        change(entry);
        return next;
    }

    // A tolerance is the user's too (yield overview D1): an entry carrying one is never dropped for being otherwise default.
    private static bool IsDefaultShaped(TunableEntry e, Tunable t)
        => t.IsVendorPart ? e.Min is null && e.Max is null && e.Parts is null && e.Extra is null
        : (e.Min is null || e.Min == t.DefaultMin) && (e.Max is null || e.Max == t.DefaultMax) && e.Scale == DefaultScale(t) && e.Step is null
           && e.Discrete == (t.IsInteger ? TuneDiscrete.Integer : TuneDiscrete.None) && e.Extra is null
           && !e.Stat && e.Distribution == StatDistribution.None && e.Spread is null;

    // ── The statistical part (brief-yield-10 R-ya10-3) ──────────────────────────────

    /// <summary>
    /// Sets or clears the <c>stat</c> flag of <paramref name="tunable"/> — the Yield panel's half of the shared entry
    /// (yield overview D1). Turning it on for a key with no entry creates one with <c>tune</c> and <c>opt</c> off and no
    /// range (a tolerance needs none; the first tune or opt gives it the default); an
    /// entry with no distribution yet gets <see cref="ToleranceText.DefaultDistribution"/> and its default spread.
    /// Turning it off keeps the distribution, as <c>stat=0</c> does (D2).
    /// </summary>
    public static TuningSetup WithStat(TuningSetup? setup, Tunable tunable, bool on)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == tunable.Key);
        if (entry is null)
        {
            if (!on) return next;
            entry = NewEntry(tunable);
            entry.Min = entry.Max = null;
            next.Variables.Add(entry);
        }
        if (on && entry.Distribution == StatDistribution.None)
        {
            entry.Distribution = ToleranceText.DefaultDistribution(tunable);
            entry.Spread       = tunable.IsPartValue
                // A vendor part's value draws with the part's own tolerance (brief vendorlib-5 D1): screened to its limit.
                ? new StatSpread { Tol = TunableKey.PartTolerance, Sigmas = ToleranceText.DefaultSigmas, Trunc = ToleranceText.DefaultSigmas }
                : ToleranceText.DefaultSpread(entry.Distribution);
        }
        entry.Stat = on;
        return next;
    }

    /// <summary>
    /// The tolerance removed from <paramref name="key"/>'s entry — flag, distribution and spread — with every
    /// correlation naming it. The entry itself goes when nothing else of it is set.
    /// </summary>
    public static TuningSetup WithoutTolerance(TuningSetup? setup, string key, Tunable? tunable)
    {
        var next  = setup?.Clone() ?? new TuningSetup();
        var entry = next.Variables.FirstOrDefault(v => v.Key == key);
        if (entry is null) return next;
        entry.Stat = false;
        entry.Distribution = StatDistribution.None;
        entry.Spread = null;
        next.Correlations.RemoveAll(c => c.First == key || c.Second == key);
        if (!entry.Tune && !entry.Opt && (tunable is null || IsDefaultShaped(entry, tunable))) next.Variables.Remove(entry);
        return next;
    }

    /// <summary>A new distribution on <paramref name="key"/>'s entry, its spread carried across where the new one reads
    /// the same keys (<see cref="ToleranceText.Convert"/>).</summary>
    public static TuningSetup WithDistribution(TuningSetup? setup, string key, Tunable? tunable, StatDistribution dist)
        => WithEntry(setup, key, tunable, e =>
        {
            e.Spread = ToleranceText.Convert(e.Spread, dist);
            e.Distribution = dist;
            if (dist == StatDistribution.None) e.Stat = false;
        });

    /// <summary>Sets a goal's <c>use=</c> (yield overview D4).</summary>
    public static TuningSetup WithGoalUse(TuningSetup? setup, string goal, GoalUse use)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        if (next.Goals.FirstOrDefault(g => g.Name == goal) is { } g) g.Use = use;
        return next;
    }

    /// <summary>The <c>statistics</c> line replaced — dropped when every setting is at its default, so a panel that
    /// only looked at the defaults writes nothing.</summary>
    public static TuningSetup WithStatistics(TuningSetup? setup, StatisticsSettings settings)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        next.Statistics = IsDefault(settings) ? null : settings.Clone();
        return next;
    }

    /// <summary>The <c>center</c> line replaced (brief-yield-12). Kept even at every default: the line's presence is the
    /// design saying it is centred, and the panel writes one only when a setting is edited.</summary>
    public static TuningSetup WithCentering(TuningSetup? setup, CenteringSettings settings)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        next.Centering = settings.Clone();
        return next;
    }

    /// <summary>The <c>doe</c> line replaced (brief-yield-14). Kept even at every default, as the center line is.</summary>
    public static TuningSetup WithDoe(TuningSetup? setup, DoeSettings settings)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        next.Doe = settings.Clone();
        return next;
    }

    /// <summary>True when <paramref name="s"/> says nothing a default does not.</summary>
    public static bool IsDefault(StatisticsSettings s)
        => s.Trials is null && s.Seed is null && s.Sampling == StatSampling.Random && s.Target is null && s.Confidence is null
           && !s.AutoStop && s.NonConverged == NonConvergedPolicy.Fail && s.Save is null && s.Process is null
           && s.Mismatch is null && s.SigmaScale is null && s.Parallelism is null && s.Scope == OptimizerScope.GoalAnalyses
           && s.Corners is null && s.Extra is null;

    /// <summary>The <c>correlate</c> lines replaced, in the order given; a ρ of zero is no line.</summary>
    public static TuningSetup WithCorrelations(TuningSetup? setup, IEnumerable<StatCorrelation> correlations)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        next.Correlations = [.. correlations.Where(c => c.Rho != 0).Select(c => c.Clone())];
        return next;
    }

    /// <summary>The <c>corner</c> lines replaced, in the order given.</summary>
    public static TuningSetup WithCorners(TuningSetup? setup, IEnumerable<CornerDefinition> corners)
    {
        var next = setup?.Clone() ?? new TuningSetup();
        next.Corners = [.. corners.Select(c => c.Clone())];
        return next;
    }
}
