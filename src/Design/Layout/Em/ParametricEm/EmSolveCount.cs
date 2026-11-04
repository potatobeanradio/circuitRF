// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 D5 — solves are counted before they start. Every EM evaluation a run will make is resolved at every point
// it will run — the sweep's points walked exactly as ParametricSweepEngine walks them, each elaborated with the provider
// in counting mode — so the run can say how many solves it needs, how many the cache already holds, and what the rest
// cost, before any of them is paid for.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>What a run's EM evaluations come to.</summary>
/// <param name="Evaluations">Every EM evaluation, at every point.</param>
/// <param name="Geometries">Distinct geometries among them.</param>
/// <param name="Cached">Of those, how many the cache holds.</param>
/// <param name="ToSolve">How many need a solve.</param>
/// <param name="Seconds">The factor time those solves are estimated at (the fill is extra); null when unknown.</param>
/// <param name="PeakMemoryBytes">The largest of their memory estimates; null when unknown.</param>
/// <param name="Refusals">Why an evaluation cannot be made — the run would refuse with the same sentence.</param>
/// <param name="Unreached">D4: variables that do not reach the copper they drive.</param>
/// <param name="Records">Every evaluation, for <c>explain</c>.</param>
public sealed record EmSolveCountResult(
    int Evaluations, int Geometries, int Cached, int ToSolve, double? Seconds, long? PeakMemoryBytes,
    IReadOnlyList<string> Refusals, IReadOnlyList<string> Unreached, IReadOnlyList<EmEvaluationRecord> Records)
{
    public static readonly EmSolveCountResult None = new(0, 0, 0, 0, null, null, [], [], []);

    public bool Any => Evaluations > 0 || Refusals.Count > 0;

    /// <summary>brief-sdl-8 D2/D7 — what a 3D EM view says of its evaluations (each flattened group, what the mesh holds),
    /// once each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>One sentence: "3 EM solves needed (2 geometries already cached), about 4 min, 1.2 GB".</summary>
    public string Sentence
    {
        get
        {
            string s = $"{ToSolve} EM solve{(ToSolve == 1 ? "" : "s")} needed for {Evaluations} evaluation{(Evaluations == 1 ? "" : "s")} " +
                       $"({Geometries} distinct geometr{(Geometries == 1 ? "y" : "ies")}, {Cached} already in the EM cache)";
            if (ToSolve > 0 && Seconds is { } t) s += $", about {Duration(t)} of factoring";
            if (ToSolve > 0 && PeakMemoryBytes is { } m) s += $", up to {PlanarNearFieldEstimate.Size(m)} a frequency point";
            return s + ".";
        }
    }

    internal static string Duration(double seconds) => seconds switch
    {
        < 1 => "under a second",
        < 90 => $"{seconds.ToString("0", CultureInfo.InvariantCulture)} s",
        < 5400 => $"{(seconds / 60).ToString("0", CultureInfo.InvariantCulture)} min",
        _ => $"{(seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture)} h",
    };
}

public static class EmSolveCount
{
    /// <summary>
    /// Counts the EM evaluations the chain topped by <paramref name="top"/> makes on <paramref name="tb"/>, solving
    /// nothing. The bench is restored after each swept point exactly as the sweep engine restores it.
    /// </summary>
    public static EmSolveCountResult Count(TestBench tb, Library lib, Analysis top, string? baseDirectory, RunControl? control = null)
        => Summarise(CountRecords(tb, lib, top, baseDirectory, control));

    /// <summary>The records of one chain's count, for a caller summing several chains.</summary>
    public static IReadOnlyList<EmEvaluationRecord> CountRecords(TestBench tb, Library lib, Analysis top, string? baseDirectory,
                                                                 RunControl? control = null, bool estimate = true)
    {
        ParametricEmProvider.Register();
        using var scope = EmEvaluationScope.Begin(countOnly: true, control: control, estimate: estimate);
        try { Walk(top); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!scope.Records.Any(r => r.Refusal is not null))
                return [.. scope.Records, new EmEvaluationRecord(top.Name, "", null, false, e.Message, null, [])];
        }
        return scope.Records;

        void Walk(Analysis a)
        {
            control?.ThrowIfCancellationRequested();
            if (a is ParametricSweepAnalysis sweep
                && AnalysisChain.ResolveEffectiveInner(sweep.InnerAnalysisName, tb) is { } inner)
            {
                double[] values = ParametricSweepEngine.PointValues(sweep, tb, out _, out string baseUnit);
                int idx = tb.GlobalVariables.FindIndex(v => v.Name == sweep.SweepVarName);
                var orig = idx >= 0 ? tb.GlobalVariables[idx] : null;
                try
                {
                    foreach (double v in values)
                    {
                        var point = ParametricSweepEngine.PointVariable(sweep, v, baseUnit);
                        if (idx >= 0) tb.GlobalVariables[idx] = point;
                        else { tb.GlobalVariables.Add(point); idx = tb.GlobalVariables.Count - 1; }
                        Walk(inner);
                    }
                }
                finally
                {
                    if (orig is not null) tb.GlobalVariables[idx] = orig;
                    else if (idx >= 0) tb.GlobalVariables.RemoveAt(idx);
                }
                return;
            }
            using ElaboratedNetlist nl = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
        }
    }

    /// <summary>The records summed: distinct geometries by key, the cached ones, and the estimates of the rest.</summary>
    public static EmSolveCountResult Summarise(IReadOnlyList<EmEvaluationRecord> records)
    {
        if (records.Count == 0) return EmSolveCountResult.None;
        var byKey = records.Where(r => r.Key is not null).GroupBy(r => r.Key!).Select(g => g.First()).ToList();
        var toSolve = byKey.Where(r => !r.Cached).ToList();
        double? seconds = toSolve.Count == 0 ? 0 : toSolve.All(r => r.Estimate?.Seconds is not null)
            ? toSolve.Sum(r => r.Estimate!.Seconds!.Value) : null;
        long? memory = toSolve.Count == 0 ? null : toSolve.Where(r => r.Estimate is not null).Select(r => (long?)r.Estimate!.MemoryBytes).Max();
        return new EmSolveCountResult(
            records.Count(r => r.Key is not null), byKey.Count, byKey.Count(r => r.Cached), toSolve.Count, seconds, memory,
            records.Select(r => r.Refusal).Where(r => r is not null).Cast<string>().Distinct().ToList(),
            records.SelectMany(r => r.Unreached).Distinct().ToList(),
            records)
        {
            Notes = records.SelectMany(r => r.ThreeDNotes).Distinct().ToList(),
        };
    }
}
