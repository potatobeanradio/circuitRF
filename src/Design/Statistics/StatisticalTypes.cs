using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Optimization;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Statistics;
using RfCore.Data;

namespace CircuitRF.Design.Statistics;

/// <summary>What a statistical run is for (docs/design/yield.md §8).</summary>
public enum StatisticalMode
{
    /// <summary>The spread alone: needs no goal, and scores every enabled goal it finds.</summary>
    MonteCarlo,
    /// <summary>Pass/fail against the yield specs — the enabled goals with <c>use=yield|both</c>; needs one.</summary>
    Yield,
}

/// <summary>What a caller may change about a run beyond what the design's setup says.</summary>
public sealed record StatisticalOptions
{
    public StatisticalMode Mode { get; init; } = StatisticalMode.Yield;

    /// <summary>The setup to run; null runs the one the circuit's netlist carries.</summary>
    public TuningSetup? Setup { get; init; }

    /// <summary><c>--set name=expr</c> overrides, applied before the trial's values as a run verb applies them.</summary>
    public IReadOnlyList<(string Name, string Expression)> Sets { get; init; } = [];

    /// <summary>brief-sdl-7 D8 — false runs every trial by circuit models (<c>--em off</c>); null as the design says, which
    /// for an analysis simulated by EM is a refusal: a trial count of EM solves is not a run anyone waits for.</summary>
    public bool? Em { get; init; }

    /// <summary>Told after every batch.</summary>
    public IProgress<StatisticalProgress>? Progress { get; init; }

    /// <summary>Cancelling abandons the run and writes nothing (exit 130); Stop keeps every finished trial.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Trials per batch; null is the evaluator's parallelism.</summary>
    public int? BatchSize { get; init; }

    /// <summary>Where the result is written when the run finishes or is stopped
    /// (<see cref="StatisticalRun.ResultPathFor"/>); null writes nothing.</summary>
    public string? ResultPath { get; init; }

    /// <summary>The run's result so far, for the in-memory Data Display source (yield overview D14) — at most once
    /// per <see cref="PublishInterval"/>, the newest state, and once more at the end.</summary>
    public Action<DataSet>? Publish { get; init; }

    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A corner's bindings (brief-yield-6 R-ya6-3): key → value text, applied to the nominal and every trial,
    /// and the nominal each spread is drawn around. Null for none.</summary>
    public IReadOnlyDictionary<string, string>? Bindings { get; init; }
}

/// <summary>
/// The z-vector a run recorded for one trial (yield overview D5) — what a statistical corner replays when the run that
/// made it is at hand (brief-yield-6 R-ya6-4).
/// </summary>
/// <param name="EntryZ">Statistical entry key → its standard normal after correlation.</param>
/// <param name="KitZ">Distribution-call stream → its standard normal.</param>
public sealed record RecordedTrial(IReadOnlyDictionary<string, double> EntryZ, IReadOnlyDictionary<string, double> KitZ)
{
    /// <summary>A trial of a run held in memory.</summary>
    public static RecordedTrial Of(TrialRecord record)
        => new(record.Z, record.Kit.ToDictionary(kv => kv.Key, kv => kv.Value.Z, StringComparer.Ordinal));

    /// <summary>
    /// Trial <paramref name="trial"/> of a run's result file (<c>&lt;design&gt;.yield.npy</c>) — when that file is the
    /// run the corner names: the same seed and sampling, and the trial in it. Null otherwise; a later run with other
    /// settings overwrites the file, and its trial n is a different trial.
    /// </summary>
    public static RecordedTrial? FromDataSet(DataSet data, CornerDefinition corner)
    {
        if (corner.Trial is not { } t || !data.ContainsGroup(StatisticalDataSet.YieldGroup) || !data.ContainsGroup(StatisticalDataSet.TrialsGroup))
            return null;
        var yield = data.CubesIn(StatisticalDataSet.YieldGroup);
        if (!yield.TryGetValue("seed", out var seed) || seed.RealValues[0] != (corner.Seed ?? StatisticsSettings.DefaultSeed)) return null;
        string sampling = (corner.Sampling ?? StatSampling.Random).ToString().ToLowerInvariant();
        if (!yield.TryGetValue("sampling", out var sc) || sc.Axes[0].Labels is not { Length: > 0 } labels || labels[0] != sampling) return null;

        var entries = new Dictionary<string, double>(StringComparer.Ordinal);
        var kit     = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, cube) in data.CubesIn(StatisticalDataSet.TrialsGroup))
        {
            if (!name.StartsWith("z:", StringComparison.Ordinal) || cube.Rank != 1 || t > cube.Axes[0].Length) continue;
            double z = cube.RealValues[t - 1];
            if (double.IsNaN(z)) continue;
            string stream = name[2..];
            if (stream.StartsWith("process:", StringComparison.Ordinal)) kit[stream["process:".Length..]] = z;
            else if (stream.StartsWith("mismatch:", StringComparison.Ordinal)) kit[stream["mismatch:".Length..]] = z;
            else entries[stream] = z;
        }
        return entries.Count + kit.Count == 0 ? null : new RecordedTrial(entries, kit);
    }
}

/// <summary>
/// A statistical corner replayed (brief-yield-6 R-ya6-4): the value map and expression draws to evaluate, what the replay
/// noticed (streams the design no longer has), and why it cannot be evaluated, if it cannot.
/// </summary>
public sealed record StatisticalReplay(
    int                                 Trial,
    IReadOnlyDictionary<string, string> Values,
    ExpressionDraws?                    Draws,
    IReadOnlyList<Diagnostic>           Notes,
    Diagnostic?                         Refusal);

/// <summary>One goal's yield.</summary>
public sealed record GoalYield(string Goal, YieldEstimate Estimate);

/// <summary>What a run reports after every batch (R-ya4-1).</summary>
/// <param name="Trials">Trials done.</param>
/// <param name="DidNotEvaluate">Of those, how many did not evaluate.</param>
/// <param name="Yield">Overall: passes, counted trials, yield and interval; <see cref="YieldEstimate.None"/> with no goal.</param>
public sealed record StatisticalProgress(
    int                       Trials,
    int                       DidNotEvaluate,
    YieldEstimate             Yield,
    IReadOnlyList<GoalYield>  Goals,
    TimeSpan                  Elapsed);

/// <summary>How a run ended (yield overview D13 gives each its exit code).</summary>
public enum StatisticalOutcome
{
    /// <summary>Finished or stopped, and the yield is at or above the target, or there is no target — exit 0.</summary>
    Finished,
    /// <summary>Finished or stopped with the yield below the target — exit 3.</summary>
    BelowTarget,
    /// <summary>Refused before the first trial — exit 1.</summary>
    Refused,
    /// <summary>No trial evaluated — exit 2.</summary>
    NoneEvaluated,
    /// <summary>Cancelled — exit 130, and nothing is written.</summary>
    Cancelled,
}

/// <summary>
/// One trial (R-ya4-2): what was drawn, what it evaluated to.
/// </summary>
/// <param name="Trial">Its number, from 1, in the order trials are drawn.</param>
/// <param name="Values">The value map evaluated (key → text): every statistical entry's drawn value.</param>
/// <param name="Draws">Entry key → the drawn number in base SI.</param>
/// <param name="Z">Entry key → its standard normal after correlation (yield overview D5).</param>
/// <param name="Kit">Kit stream → its kind and standard normal, for every distribution call the trial drew.</param>
/// <param name="Goals">Each scored goal's score; empty when the trial did not evaluate.</param>
/// <param name="Scalars">Every real scalar measurement the trial produced, by name.</param>
/// <param name="Data">The trial's analysis results, when the save policy keeps them.</param>
public sealed record TrialRecord(
    int                                                         Trial,
    PointStatus                                                 Status,
    Diagnostic?                                                 Reason,
    IReadOnlyDictionary<string, string>                         Values,
    IReadOnlyDictionary<string, double>                         Draws,
    IReadOnlyDictionary<string, double>                         Z,
    IReadOnlyDictionary<string, (StatisticalKind Kind, double Z)> Kit,
    IReadOnlyList<GoalScore>                                    Goals,
    IReadOnlyDictionary<string, double>                         Scalars,
    DataSet?                                                    Data)
{
    public bool Evaluated => Status == PointStatus.Evaluated;

    /// <summary>It evaluated and every scored goal is met.</summary>
    public bool Pass => Evaluated && Goals.All(g => g.Met);
}

/// <summary>What the save policy kept (R-ya4-5).</summary>
/// <param name="Requested">The <c>save=</c> text as written, <c>auto</c> when absent.</param>
/// <param name="KeptTrials">How many leading trials keep their analysis results.</param>
/// <param name="BytesPerTrial">The estimate one trial's analysis results take, from the nominal's.</param>
public sealed record SaveReport(string Requested, int KeptTrials, long BytesPerTrial, string Sentence);

/// <summary>One of a goal's worst trials (R-ya4-10).</summary>
public sealed record WorstTrial(int Trial, double Margin, double Worst, IReadOnlyDictionary<string, string> Values);

/// <summary>One contributor to a scalar's spread (R-ya4-9): a statistical entry, a process draw, or an
/// instance's mismatch draws together.</summary>
/// <param name="Coefficient">The standardized regression coefficient; for an instance's group, the root sum of
/// its streams' squares (unsigned).</param>
/// <param name="Share">Its share of the explained variance, β·r (the shares sum to 1). With correlated entries one
/// share can pass 1 and another fall below 0; this is the honest number, and it is what JSON and the result file carry.
/// A display shows <see cref="ShownShare"/> (owner decision D-a, brief-yield-16).</param>
/// <param name="Spearman">The rank correlation of the scalar with the contributor (for a group, with its fitted
/// linear part).</param>
public sealed record Contributor(string Name, string Kind, double Coefficient, double Share, double Spearman, int Streams)
{
    /// <summary>The share as every display shows it: clamped to 0–1 (D-a).</summary>
    public double ShownShare => Shown(Share);

    /// <summary><paramref name="share"/> clamped to 0–1; a share that is not a number shows as 0.</summary>
    public static double Shown(double share) => double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;

    /// <summary>A Pareto's running total over <paramref name="shares"/> in ranked order: of the shown shares,
    /// renormalized so it ends at 1 and never decreases (D-a). All zero when nothing shows.</summary>
    public static double[] ShownCumulative(IReadOnlyList<double> shares)
    {
        var shown = shares.Select(Shown).ToArray();
        double total = shown.Sum(), sum = 0;
        return [.. shown.Select(x => total > 0 ? (sum += x) / total : 0)];
    }
}

/// <summary>A contribution ranking, largest share first (R-ya4-9).</summary>
public sealed record ContributionReport(
    string                      Of,
    IReadOnlyList<Contributor>  Contributors,
    double                      RSquared,
    bool                        Underdetermined,
    int                         Trials,
    Diagnostic?                 Refusal = null);

/// <summary>What a run produced.</summary>
public sealed class StatisticalResult
{
    public required StatisticalOutcome Outcome { get; init; }

    /// <summary>Yield overview D13: 0, 3, 1, 2 or 130.</summary>
    public int ExitCode => Outcome switch
    {
        StatisticalOutcome.Finished      => 0,
        StatisticalOutcome.BelowTarget   => 3,
        StatisticalOutcome.Refused       => 1,
        StatisticalOutcome.NoneEvaluated => 2,
        _                                => 130,
    };

    public StatisticalMode Mode { get; init; }

    /// <summary>Why it stopped, as a sentence.</summary>
    public string FinishReason { get; init; } = "";

    public Diagnostic? Refusal { get; init; }

    public IReadOnlyList<Diagnostic> Notes { get; init; } = [];

    /// <summary>Trials done (after auto-stop, the count it stopped at).</summary>
    public int Trials { get; init; }

    public int DidNotEvaluate { get; init; }

    /// <summary>Overall; <see cref="YieldEstimate.None"/> with no goal.</summary>
    public YieldEstimate Yield { get; init; } = YieldEstimate.None;

    public IReadOnlyList<GoalYield> Goals { get; init; } = [];

    /// <summary>What auto-stop decided; null when it did not stop the run.</summary>
    public AutoStopVerdict? AutoStop { get; init; }

    public SaveReport? Save { get; init; }

    /// <summary>The nominal's evaluation.</summary>
    public PointEvaluation? Nominal { get; init; }

    /// <summary>Every trial, in trial order.</summary>
    public IReadOnlyList<TrialRecord> Records { get; init; } = [];

    /// <summary>The run's <c>DataSet</c> (docs/design/results-dataset-layout.md §"Monte Carlo and yield"); null
    /// when refused or cancelled.</summary>
    public DataSet? Data { get; init; }

    /// <summary>Where <see cref="Data"/> was written; null when it was not.</summary>
    public string? WrittenPath { get; init; }

    /// <summary>The statistics settings the run applied — the seed, sampling and trial count that identify its trials
    /// (a statistical corner names them). Null when refused or cancelled.</summary>
    public StatisticsSettings? Settings { get; init; }
}
