using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>--result summary</c> for the reports that carry no DataSet (EMB-5 R-emb5-8): optimize, the yield
/// family and import library. A DataSet's summary is its shape without values; these reports have no shape,
/// so their summary is the answer without the per-trial, per-corner and per-iteration records — the outcome,
/// the values as text to write back, each goal's margin and where it binds.
///
/// <para><b>A projection only.</b> Every field kept is the full report's own, unrenamed and unrecomputed; a
/// record list left out is emptied (or nulled where the record allows it) and named in
/// <see cref="ResultPayload.Summarized"/>, so an empty list cannot be read as "there were none".</para>
/// </summary>
internal static class ReportSummaries
{
    private const string AskFull = "; result=full (--result full) returns them";

    public static (OptimizeReportJson, string) Optimize(OptimizeReportJson r)
        // The default is already the final result only; per iteration comes back only when showIterations
        // asked for it, and then it stays. What a summary drops is each goal's per-corner table — the binding
        // corner stays on the goal.
        => (r with { Goals = [.. r.Goals.Select(g => g with { PerCorner = null })] },
            "each goal's per-corner margins" + AskFull);

    public static (YieldReportJson, string) Yield(YieldReportJson r)
        => (YieldOnly(r), "the spread of every margin and measurement (statistics), the worst trials' values (worst), "
                        + "the contribution fits and the trial numbers behind each reason" + AskFull);

    private static YieldReportJson YieldOnly(YieldReportJson r) => r with
    {
        Statistics    = [],
        Worst         = [],
        Contributions = null,
        Reasons       = [.. r.Reasons.Select(x => x with { Trials = [] })],
    };

    public static (CornerReportJson, string) Corners(CornerReportJson r)
        => (r with
            {
                Corners = [],
                Yields  = r.Yields is null ? null : [.. r.Yields.Select(y => y with { Run = YieldOnly(y.Run) })],
            },
            "the corner x goal table (corners) and each corner run's statistics and worst trials; worst names each "
          + "goal's binding corner" + AskFull);

    public static (CenterReportJson, string) Center(CenterReportJson r)
        => (r with { History = [] }, "the yield vs iteration history" + AskFull);

    public static (DoeReportJson, string) Doe(DoeReportJson r)
        => (r with { Responses = [.. r.Responses.Select(x => x with { Effects = [.. x.Effects.Where(e => e.Active)] })] },
            "every effect that is not active" + AskFull);
}
