using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>What one background analysis pass produced. Plain data — no view-model, no collection.</summary>
/// <param name="Options">One entry per response family, in <c>ResponseOptions</c> order.</param>
/// <param name="Solutions">The solution search's own result.</param>
internal sealed record MatchAnalysis(
    IReadOnlyList<MatchAnalysis.OptionVerdict> Options, MatchSolutionSet Solutions)
{
    /// <summary>Whether one response family can be synthesised here, and why not when it cannot.</summary>
    /// <param name="Shape">The family.</param>
    /// <param name="Ok">True when it synthesises.</param>
    /// <param name="Refusal">MN-1's refusal, with its numbers, or null.</param>
    internal sealed record OptionVerdict(ResponseShape Shape, bool Ok, MatchRefusal? Refusal);
}

/// <summary>
/// The two expensive answers — <b>which response families are feasible</b> and <b>what solutions
/// exist</b> — computed off the UI thread.
/// </summary>
/// <remarks>
/// <b>These are the only slow things in the Designer, and they were both on the UI thread.</b>
/// Measured on the design doc's order-4 interstage problem (owner-reported "slow when parameters
/// update", 2026-08-20): a specification edit cost <b>1,161 ms</b> before this phase's work, of which
/// <b>1,143 ms</b> was the four response probes — <c>MatchPrototypes.Search</c> runs a 121-value shape
/// sweep per family, and Butterworth and Bessel are the two families that go through it. With a
/// numerical-route response actually SELECTED it was worse again, because
/// <c>MatchSolutionSearch.FindQAdjust</c> bisects fifteen times and synthesises inside every step.
/// The rest of a refresh — the rebuild, the ladder, the grid, the status strip, the response plots —
/// totals about 1.5 ms and stays exactly where it was, on the UI thread, synchronous.
///
/// <para>The numeric work itself was cut first (see <c>MatchPoly.Roots</c>, <c>MatchPrototypes</c>'s
/// denominator memo and <c>MatchSynthesis</c>'s synthesis memo); what remains after that is ~100 ms
/// per keystroke on a Chebyshev design and ~950 ms on a Butterworth one, which is still a stutter and
/// still does not belong in front of the user's typing.</para>
///
/// <h3>Superseded, not queued</h3>
/// <para>Only the LAST request matters — a user dragging the order spinner produces one per step and
/// every intermediate answer is dead on arrival. Each request bumps a generation, cancels the one in
/// flight, and a result that comes back stale is dropped rather than applied. The cancellation is
/// checked between families and before the solution search, which bounds a doomed pass at about one
/// family's work.</para>
///
/// <h3>Nothing here reads the live design</h3>
/// <para>The worker gets <see cref="MatchDesign.Clone"/> of the design and copies of the two settings
/// that steer the search. The live <c>_design</c> is the UI thread's and is mutated by every setter
/// in this class; handing it to a worker would be a race, and a cheap clone removes the question.
/// The badges — "current", "previously applied" — are then decided when the result is APPLIED, from
/// the design as it stands then, so a solution list never claims the design is on something it has
/// since moved off.</para>
/// </remarks>
public sealed partial class MatchDesignerViewModel
{
    /// <summary>
    /// Where an analysis result is applied. The thread that constructed this view-model — the UI
    /// thread in the application, the test's own thread under xUnit, which has no dispatcher and
    /// whose scheduler therefore falls back to the pool.
    /// </summary>
    private readonly TaskScheduler _resultScheduler =
        SynchronizationContext.Current is null
            ? TaskScheduler.Default
            : TaskScheduler.FromCurrentSynchronizationContext();

    private CancellationTokenSource? _analysisCts;
    private int _analysisGeneration;

    /// <summary>
    /// The analysis in flight, or a completed task.
    /// </summary>
    /// <remarks>
    /// <b>Public so a caller can be sure it has the answer</b> — a test asserting on
    /// <see cref="ResponseOptions"/> or <see cref="Solutions"/> right after an edit is asserting on
    /// whatever the previous pass left there unless it waits. Nothing in the application awaits it;
    /// the window binds to the collections and to <see cref="IsAnalysing"/>.
    /// </remarks>
    public Task AnalysisTask { get; private set; } = Task.CompletedTask;

    /// <summary>Blocks until the analysis in flight has been applied. For tests and for exports.</summary>
    public void WaitForAnalysis()
    {
        // Re-read each time: applying one result cannot start another, but a caller may have queued
        // a second edit between the two, and the field is only ever written from this thread.
        for (int i = 0; i < 8; i++)
        {
            var task = AnalysisTask;
            if (task.IsCompleted) return;
            try { task.Wait(); }
            catch (AggregateException) { return; }   // a cancelled pass is not a failure to report
        }
    }

    /// <summary>True while the response feasibility and the solutions are being recomputed.</summary>
    [ObservableProperty] private bool _isAnalysing;

    /// <summary>
    /// Starts one analysis pass, superseding any in flight.
    /// </summary>
    private void QueueAnalysis()
    {
        // The SELECTED marks are free and are what the radio group renders from, so they move now
        // rather than a beat later. Enablement and the tooltips are what costs, and they keep their
        // previous values until the pass lands — stale for a moment, never blank.
        foreach (var option in ResponseOptions) option.IsSelected = option.Shape == _design.Response;

        int generation = ++_analysisGeneration;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        var snapshot = _design.Clone();
        bool offerQAdjust = Settings.OfferQAdjustedSolutions;
        double qMin = Settings.QMin;
        var token = cts.Token;

        IsAnalysing = true;
        var work = Task.Run(() => Analyse(snapshot, offerQAdjust, qMin, token), token);
        AnalysisTask = work.ContinueWith(
            t =>
            {
                // A superseded pass has already been replaced by a newer IsAnalysing = true; only
                // the newest is allowed to clear the flag or write anything.
                if (generation != _analysisGeneration) return;
                IsAnalysing = false;
                if (t.Status == TaskStatus.RanToCompletion) ApplyAnalysis(t.Result);
            },
            CancellationToken.None, TaskContinuationOptions.None, _resultScheduler);

        // Deliberately NOT part of AnalysisTask: nothing on screen waits for it and nothing reads
        // its result.
        _ = work.ContinueWith(
            _ => WarmTheOrdersThePickerOffers(snapshot, token),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>
    /// Synthesises the current response at every OTHER order the picker offers, and throws the
    /// results away.
    /// </summary>
    /// <remarks>
    /// <b>The two edits the owner named as slow were "network order" and "filter response type"
    /// (2026-08-20), and they are slow for the same reason and are fixed by the same mechanism.</b>
    /// Both land in <see cref="MatchRebuild.Rebuild"/> on the UI thread, which must synthesise the
    /// new design before there is a ladder, a grid or a plot to show — and on the numerical route
    /// that is a whole lowpass-prototype search, up to ~120 ms at order 6. It cannot be deferred: it
    /// is the thing being looked at.
    ///
    /// <para>So it is done EARLY instead. A response change is already covered for free, because the
    /// feasibility pass above probes all four families at the current order — changing the response
    /// asks for exactly one of the designs it just synthesised, and <c>MatchSynthesis</c>'s memo
    /// answers in microseconds. This method covers the other axis. <c>MatchOrders.ValidOrders</c> is
    /// the SHORT list the picker actually offers (a like or mixed termination pair fixes the parity,
    /// so it is two or three entries, never a range), which is what makes speculating here bounded
    /// and worth doing rather than a guess. Measured on the design doc's order-4 interstage problem
    /// with Butterworth selected: an order change costs 121 ms of UI thread cold and ~5 ms warmed.</para>
    ///
    /// <para>It runs last, on the pool, under the same token, and its result is discarded — the memo
    /// is the entire product. A superseded pass stops at the next order rather than finishing.</para>
    /// </remarks>
    private static void WarmTheOrdersThePickerOffers(MatchDesign design, CancellationToken token)
    {
        try
        {
            foreach (int order in MatchOrders.ValidOrders(design.Term1, design.Term2))
            {
                if (order == design.Order) continue;
                token.ThrowIfCancellationRequested();
                var probe = design.Clone();
                probe.Order = order;
                MatchSynthesis.Synthesize(probe);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded. There is nothing to report: this pass had no result to lose.
        }
    }

    private static MatchAnalysis Analyse(
        MatchDesign design, bool offerQAdjust, double qMin, CancellationToken token)
    {
        var verdicts = new List<MatchAnalysis.OptionVerdict>(4);
        foreach (var shape in (ResponseShape[])
                 [ResponseShape.ChebyshevFano, ResponseShape.ChebyshevTwoEnded,
                  ResponseShape.Butterworth, ResponseShape.Bessel])
        {
            token.ThrowIfCancellationRequested();
            var probe = design.Clone();
            probe.Response = shape;
            var result = MatchSynthesis.Synthesize(probe);
            verdicts.Add(new MatchAnalysis.OptionVerdict(shape, result.Ok, result.Refusal));
        }

        token.ThrowIfCancellationRequested();
        return new MatchAnalysis(verdicts, MatchSolutionSearch.Search(design, offerQAdjust, qMin));
    }

    private void ApplyAnalysis(MatchAnalysis analysis)
    {
        foreach (var option in ResponseOptions)
        {
            var verdict = analysis.Options.FirstOrDefault(v => v.Shape == option.Shape);
            if (verdict is null) continue;
            option.IsEnabled = verdict.Ok;
            option.Refusal = verdict.Refusal;
            // The refusal already carries its numbers (MN-1 makes that a rule); rendering them is all
            // that is left to do, and recomputing them here would be a second opinion nobody asked for.
            option.Tooltip = verdict.Ok ? option.Description : verdict.Refusal!.Message;
        }
        OnPropertyChanged(nameof(ResponseOptions));

        ApplySolutions(analysis.Solutions);
    }

    private void ApplySolutions(MatchSolutionSet set)
    {
        Solutions.Clear();

        // The badges are decided HERE, against the design as it stands now — the search ran against a
        // snapshot, and between the two the user may have applied something.
        string current = MatchSolutionSearch.SolutionFingerprint(_design, _design.Transforms);
        foreach (var s in set.Solutions)
        {
            var badge =
                string.Equals(s.Fingerprint, current, StringComparison.Ordinal) ? MatchSolutionBadge.Current
                : _design.AppliedSolutions.Contains(s.Fingerprint, StringComparer.Ordinal)
                    ? MatchSolutionBadge.PreviouslyApplied
                    : MatchSolutionBadge.NeverApplied;
            Solutions.Add(new MatchSolutionRowViewModel(this, s, badge, _design.Response));
        }

        SolutionsRefusal = set.Refusal is { } r
            ? $"No solutions available for order {_design.Order}. {r.Message}"
            : "";

        var applied = Solutions.FirstOrDefault(s => s.Badge == MatchSolutionBadge.Current);
        string appliedText = applied is null
            ? _design.Transforms.Count == 0 ? "applied: none" : "applied: a hand-set transform set"
            : $"applied: {applied.CountText}, {ResponseShortName(applied.Response)}";

        SolutionsSummary = Solutions.Count == 0
            ? "no solutions"
            : $"{Solutions.Count} solution{(Solutions.Count == 1 ? "" : "s")} · {appliedText}";
    }

    private static string ResponseShortName(ResponseShape shape) => shape switch
    {
        ResponseShape.ChebyshevFano      => "Fano",
        ResponseShape.ChebyshevTwoEnded  => "two-ended",
        ResponseShape.Butterworth        => "Butterworth",
        _                                => "Bessel",
    };

    private void CancelAnalysis()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
    }
}
