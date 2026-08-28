using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Core.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Matching;

/// <summary>What the response-feasibility pass produced. Plain data — no view-model, no collection.</summary>
/// <param name="Options">One entry per response family, in <c>ResponseOptions</c> order.</param>
internal sealed record MatchAnalysis(IReadOnlyList<MatchAnalysis.OptionVerdict> Options)
{
    /// <summary>Whether one response family can be synthesised here, and why not when it cannot.</summary>
    /// <param name="Shape">The family.</param>
    /// <param name="Ok">True when it synthesises.</param>
    /// <param name="Refusal">MN-1's refusal, with its numbers, or null.</param>
    internal sealed record OptionVerdict(ResponseShape Shape, bool Ok, MatchRefusal? Refusal);
}

/// <summary>One cell of the order x response search, as it comes back off the worker.</summary>
/// <param name="Order">The order it was searched at.</param>
/// <param name="Response">The family it was searched under.</param>
/// <param name="Set">MN-1's own result for that pair.</param>
/// <param name="IsCurrent">True for the combination the design is on — the one searched FIRST.</param>
internal sealed record MatchSolutionBatch(
    int Order, ResponseShape Response, MatchSolutionSet Set, bool IsCurrent);

/// <summary>
/// Everything about a design that changes WHAT SOLUTIONS EXIST — and nothing that only changes which
/// of them is applied.
/// </summary>
/// <remarks>
/// <b>Order, response, Q-adjust, the negative-component flag and the transforms are all deliberately
/// absent.</b> The search enumerates every order and every family itself, always with Q-adjusted
/// candidates and always with negative components permitted, so none of those five is an input to it
/// any more — which is the whole reason applying a solution is instant: it moves the design onto one
/// of the answers already in hand and cannot invalidate the list it came from.
/// </remarks>
internal sealed record MatchSpecKey(
    double F1, double F2, double RippleDb, AnalysisEndChoice AnalysisEnd,
    Termination Term1, Termination Term2, double QMin)
{
    /// <summary>The key of one design under one Qmin.</summary>
    internal static MatchSpecKey From(MatchDesign design, double qMin) =>
        new(design.F1, design.F2, design.RippleDb, design.AnalysisEnd,
            design.Term1, design.Term2, qMin);
}

/// <summary>
/// The two expensive answers — <b>which response families are feasible</b> and <b>what solutions
/// exist</b> — computed off the UI thread, as two independent jobs.
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
/// <h3>The search is now the whole cross-product, and it STREAMS</h3>
/// <para><b>Owner, 2026-08-28:</b> <i>"I want the Solutions panel to list all the solutions for every
/// filter response and order"</i>, and <i>"UI needs to feel responsive so if the solution list is
/// built (and added to the UI) in the background that would be nice."</i> Those two together decide
/// the shape of this file. A search that was one <c>MatchSolutionSearch.Search</c> is now up to
/// <b>five orders x four families</b> of them, which is seconds rather than a fraction of one — so no
/// part of it is allowed to be a wait. Each (order, family) cell is published the moment it is done
/// and its rows are inserted into the list in their canonical position, so the panel fills in front
/// of the user instead of appearing at the end.</para>
///
/// <para><b>The design's OWN combination is searched first</b>, which is what makes the applied
/// solution appear immediately and is what the termination auto-solve waits on. The rest follow in
/// order, and because rows are INSERTED by sort key rather than appended, the list reads the same
/// whichever cell happens to finish when.</para>
///
/// <h3>Two jobs, restarted on different events — this is what makes Apply instant</h3>
/// <list type="bullet">
/// <item><b>The search</b> is keyed on <see cref="MatchSpecKey"/> — the terminations, the band, the
///   ripple, the analysis end and Qmin. Applying a solution, changing the order and changing the
///   response family all leave that key alone, so the list is not recomputed and not even re-sorted;
///   only the badges move. A search is restarted ONLY when the key changes.</item>
/// <item><b>The response verdicts</b> are order-dependent, so they are re-run on every specification
///   refresh. They are four <c>MatchSynthesis.Synthesize</c> calls against a memo the search has
///   already filled, which is microseconds once a search has run.</item>
/// </list>
///
/// <h3>Superseded, not queued</h3>
/// <para>Only the LAST request matters — a user dragging a slider produces one per step and every
/// intermediate answer is dead on arrival. Each job bumps a generation, cancels the one in flight,
/// and a result that comes back stale is dropped rather than applied. The search's cancellation is
/// checked between cells, which bounds a doomed pass at about one cell's work.</para>
///
/// <h3>Nothing here reads the live design</h3>
/// <para>The worker gets <see cref="MatchDesign.Clone"/> of the design and a copy of the one setting
/// that steers the search. The live <c>_design</c> is the UI thread's and is mutated by every setter
/// in this class; handing it to a worker would be a race, and a cheap clone removes the question.
/// The badges — "current", "previously applied" — are then decided when a batch LANDS, from the
/// design as it stands then, so a solution list never claims the design is on something it has
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

    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private MatchSpecKey? _searchKey;

    /// <summary>
    /// The response-feasibility pass in flight, or a completed task.
    /// </summary>
    /// <remarks>
    /// <b>Public so a caller can be sure it has the answer</b> — a test asserting on
    /// <see cref="ResponseOptions"/> right after an edit is asserting on whatever the previous pass
    /// left there unless it waits. Nothing in the application awaits it; the window binds to the
    /// collections and to <see cref="IsAnalysing"/>.
    /// </remarks>
    public Task AnalysisTask { get; private set; } = Task.CompletedTask;

    /// <summary>The cross-product solution search in flight, or a completed task.</summary>
    /// <remarks>Separate from <see cref="AnalysisTask"/> because it is restarted on a different
    /// event — see this file's own remarks.</remarks>
    public Task SolutionSearchTask { get; private set; } = Task.CompletedTask;

    /// <summary>Blocks until both jobs in flight have been applied. For tests and for exports.</summary>
    /// <remarks>
    /// <b>Applying one result CAN start another.</b> <see cref="AutoApplyAReachingSolution"/> runs
    /// inside a batch landing and applies a solution, which refreshes and therefore queues a fresh
    /// verdict pass — so a completed <see cref="AnalysisTask"/> is no longer on its own proof that
    /// the panel has settled. <see cref="IsAnalysing"/> is set SYNCHRONOUSLY by the two queue methods
    /// and is what distinguishes "finished" from "handed on", so both are read; the loop is bounded
    /// either way, and a cancelled pass still clears its flag on its way out.
    /// </remarks>
    public void WaitForAnalysis()
    {
        // Re-read each time: a caller may have queued a second edit between the two, and the auto-
        // applied solution queues one of its own from inside a landing.
        //
        // The "handed on" branch YIELDS rather than spinning. Both flags are set synchronously by the
        // queue methods and both task fields are assigned a moment later, so there is a window in
        // which the tasks in hand are complete and IsAnalysing is already true for a pass that has no
        // task yet — and a tight loop burns its whole budget inside that window and returns as though
        // the panel had settled. Observed as a load-dependent failure in MatchRound6Tests, not
        // theorised.
        for (int i = 0; i < 200; i++)
        {
            var verdicts = AnalysisTask;
            var search = SolutionSearchTask;
            if (verdicts.IsCompleted && search.IsCompleted && !IsAnalysing) return;
            if (verdicts.IsCompleted && search.IsCompleted) { Thread.Sleep(1); continue; }
            try { Task.WaitAll(verdicts, search); }
            catch (AggregateException) { return; }   // a cancelled pass is not a failure to report
        }
    }

    /// <summary>True while either background job is running.</summary>
    public bool IsAnalysing => IsProbingResponses || IsSearchingSolutions;

    /// <summary>True while the four response families are being re-probed.</summary>
    [ObservableProperty] private bool _isProbingResponses;

    /// <summary>True while the order x family cross-product is being searched.</summary>
    /// <remarks>Bound separately from <see cref="IsAnalysing"/> because the solutions panel shows its
    /// own "searching…" line while the list is still filling, and that line is about this job only.</remarks>
    [ObservableProperty] private bool _isSearchingSolutions;

    partial void OnIsProbingResponsesChanged(bool value) => OnPropertyChanged(nameof(IsAnalysing));

    partial void OnIsSearchingSolutionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAnalysing));
        OnPropertyChanged(nameof(SolutionsProgressNote));
    }

    /// <summary>What the solutions panel says while the search is still running, or empty.</summary>
    public string SolutionsProgressNote =>
        IsSearchingSolutions ? "Searching every order and response family…" : "";

    // ── Queueing ──────────────────────────────────────────────────────────────

    /// <summary>Starts whichever of the two jobs the current design needs, superseding as required.</summary>
    private void QueueAnalysis()
    {
        // The SELECTED marks are free and are what the filter renders from, so they move now rather
        // than a beat later. Enablement and the tooltips are what costs, and they keep their previous
        // values until the pass lands — stale for a moment, never blank.
        foreach (var option in ResponseOptions) option.IsSelected = option.Shape == _design.Response;
        Filter.SetOrders(OrderOptions);

        // A termination edit asks for an auto-solve, and the answer is a solution for the design's
        // OWN combination — which is the first cell the search runs. Recorded as a pending request
        // rather than acted on here: nothing has been searched yet at this point.
        _pendingAutoSolve = _autoSolveEnd == 0 ? null : (_autoSolveEnd, _designEpoch);

        QueueResponseVerdicts();
        QueueSolutionSearch();

        // Order, response and Q-adjust are all in the fingerprint, so an edit to any of them moves
        // which row is "current" without moving the list. Done synchronously: it is a hash per row.
        RebadgeSolutions();
    }

    private void QueueResponseVerdicts()
    {
        int generation = ++_analysisGeneration;
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;

        var snapshot = _design.Clone();
        var token = cts.Token;

        IsProbingResponses = true;
        var work = Task.Run(() => ProbeResponses(snapshot, token), token);
        AnalysisTask = work.ContinueWith(
            t =>
            {
                if (generation != _analysisGeneration) return;
                // Under the refresh gate because a landing can start a refresh of its own — see
                // MatchDesignerViewModel.RefreshGate for the crash that establishes.
                lock (RefreshGate)
                {
                    IsProbingResponses = false;
                    if (t.Status == TaskStatus.RanToCompletion) ApplyVerdicts(t.Result);
                }
            },
            CancellationToken.None, TaskContinuationOptions.None, _resultScheduler);
    }

    /// <summary>
    /// Starts the cross-product search, or leaves the one that answers this specification alone.
    /// </summary>
    /// <remarks>
    /// <b>The early return is the feature.</b> An order change, a response change and an Apply all
    /// arrive here, and none of them changes what solutions exist — so the seconds of searching
    /// behind the list are spent once per specification rather than once per click.
    /// </remarks>
    private void QueueSolutionSearch()
    {
        var key = MatchSpecKey.From(_design, Settings.QMin);
        if (_searchKey is { } existing && existing == key
            && (IsSearchingSolutions || SolutionsComplete))
            return;

        int generation = ++_searchGeneration;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searchKey = key;

        _allSolutions.Clear();
        Solutions.Clear();
        SolutionsComplete = false;
        SolutionsRefusal = "";
        RefreshSolutionsSummary();

        var snapshot = _design.Clone();
        double qMin = Settings.QMin;
        var token = cts.Token;
        var publisher = new BatchPublisher(_resultScheduler);

        void Publish(MatchSolutionBatch batch) => publisher.Post(() =>
        {
            if (generation != _searchGeneration) return;
            lock (RefreshGate) LandBatch(batch);
        });

        IsSearchingSolutions = true;
        var work = Task.Run(() => SearchEveryCombination(snapshot, qMin, Publish, token), token);

        // The landing has to come after every batch, and in a host with no dispatcher the batches are
        // pool tasks rather than queued messages — so it is chained onto the publisher's own tail
        // rather than merely scheduled after the worker.
        SolutionSearchTask = work.ContinueWith(
                t => publisher.Chain.ContinueWith(
                    _ =>
                    {
                        if (generation != _searchGeneration) return;
                        lock (RefreshGate)
                        {
                            IsSearchingSolutions = false;
                            if (t.Status != TaskStatus.RanToCompletion) return;
                            SolutionsComplete = true;
                            LandSearchComplete(t.Result);
                        }
                    },
                    CancellationToken.None, TaskContinuationOptions.None, _resultScheduler),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// Serializes the batch landings, in the order they were published, on whichever scheduler the
    /// host gave us.
    /// </summary>
    /// <remarks>
    /// <b>A chain rather than N independent posts, and that matters only off the dispatcher.</b> In
    /// the application <c>_resultScheduler</c> is the UI thread's and posts run FIFO on their own; in
    /// a test it falls back to <c>TaskScheduler.Default</c>, where N posts are N pool tasks that can
    /// run at once and in any order — two of them inserting into the same
    /// <see cref="ObservableCollection{T}"/> at once. Chaining makes the two hosts behave the same,
    /// and gives the completion landing something to wait on.
    /// </remarks>
    private sealed class BatchPublisher(TaskScheduler scheduler)
    {
        /// <summary>The tail of the chain. Written only by the worker thread that posts.</summary>
        public Task Chain { get; private set; } = Task.CompletedTask;

        public void Post(Action action) =>
            Chain = Chain.ContinueWith(
                _ => action(), CancellationToken.None, TaskContinuationOptions.None, scheduler);
    }

    // ── The two workers ───────────────────────────────────────────────────────

    private static readonly ResponseShape[] AllShapes =
    [
        ResponseShape.ChebyshevFano, ResponseShape.ChebyshevTwoEnded,
        ResponseShape.Butterworth, ResponseShape.Bessel,
    ];

    private static MatchAnalysis ProbeResponses(MatchDesign design, CancellationToken token)
    {
        var verdicts = new List<MatchAnalysis.OptionVerdict>(AllShapes.Length);
        foreach (var shape in AllShapes)
        {
            token.ThrowIfCancellationRequested();
            var probe = design.Clone();
            probe.Response = shape;
            var result = MatchSynthesis.Synthesize(probe);
            verdicts.Add(new MatchAnalysis.OptionVerdict(shape, result.Ok, result.Refusal));
        }
        return new MatchAnalysis(verdicts);
    }

    /// <summary>
    /// Searches every permitted order against every response family, publishing each cell as it lands.
    /// </summary>
    /// <returns>MN-1's refusal for the design's OWN combination, or null — see
    /// <see cref="LandSearchComplete"/> for why that one and not another.</returns>
    /// <remarks>
    /// <b>Every cell is searched with Q-adjusted candidates offered and negative components
    /// permitted</b>, because both are now FILTERS over the answer rather than inputs to it. Allowing
    /// negatives is a strict superset — the flag only ever widens a transform's positivity range — so
    /// one pass finds what two would, and the rows that used it are identified afterwards by looking
    /// at the network (see <c>MatchSolutionRowViewModel.HasNegativeComponents</c>). A stored
    /// <c>QAdjust</c> is cleared on the probe for the same reason: <c>MatchSolutionSearch</c> offers
    /// the Q-adjusted variant only to a design that has none, so a design already carrying one would
    /// otherwise hide the un-adjusted half of its own list.
    /// </remarks>
    private static MatchRefusal? SearchEveryCombination(
        MatchDesign design, double qMin, Action<MatchSolutionBatch> publish, CancellationToken token)
    {
        MatchRefusal? here = null;

        foreach (var (order, shape) in Combinations(design))
        {
            token.ThrowIfCancellationRequested();

            var probe = design.Clone();
            probe.Order = order;
            probe.Response = shape;
            probe.QAdjust = 0.0;
            probe.AllowNegativeComponents = true;

            var set = MatchSolutionSearch.Search(probe, includeQAdjust: true, qMin);
            bool isCurrent = order == design.Order && shape == design.Response;
            if (isCurrent) here = set.Refusal;

            publish(new MatchSolutionBatch(order, shape, set, isCurrent));
        }

        return here;
    }

    /// <summary>
    /// Every (order, family) pair to search, <b>the design's own first</b> and the rest in the order
    /// the list will show them.
    /// </summary>
    private static IEnumerable<(int Order, ResponseShape Shape)> Combinations(MatchDesign design)
    {
        var orders = MatchOrders.ValidOrders(design.Term1, design.Term2);
        bool ownIsValid = orders.Contains(design.Order);
        if (ownIsValid) yield return (design.Order, design.Response);

        foreach (int order in orders)
            foreach (var shape in AllShapes)
                if (!ownIsValid || order != design.Order || shape != design.Response)
                    yield return (order, shape);
    }

    // ── Landings ──────────────────────────────────────────────────────────────

    private void ApplyVerdicts(MatchAnalysis analysis)
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
        // The closed combo's tooltip IS the selected option's, and this pass is what writes it.
        OnPropertyChanged(nameof(ResponseTooltip));
    }

    /// <summary>
    /// Appends one searched cell's solutions to the end of the list.
    /// </summary>
    /// <remarks>
    /// <b>APPENDED, never inserted — and that is a bug fix, not a simplification</b> (owner-reported,
    /// 2026-08-28: <i>"sometimes hitting Apply changes the scroll view to a different position and it
    /// seems like my solution selection was not picked (I did not see a green outline box)"</i>, and
    /// <i>"hitting Apply on the Solution card does not update the Response plots"</i>).
    ///
    /// <para><b>Both reports are the same defect, and neither is about Apply.</b> The rows were being
    /// inserted at their canonical (order, family, rank) position, because the cells are not searched
    /// in that order — the design's own runs first. So for the four seconds the cross-product takes,
    /// every cell that landed slid the rows below it DOWN, under the user's pointer. Clicking Apply
    /// in that window applies whichever card has arrived beneath the cursor, not the one that was
    /// there when the click began: the list jumps, the green outline appears on a row that has
    /// scrolled out of view, and the response plots change to a network the user did not choose —
    /// which reads exactly as "they did not change" when the two are similar.</para>
    ///
    /// <para>Appending cannot do that. Rows only ever arrive BELOW everything already on screen, so
    /// nothing under the pointer ever moves. What it costs is that the list is grouped in the order
    /// the cells were searched rather than by order and family — and that order is deliberate and
    /// better: the combination the design is on comes first (see <see cref="Combinations"/>), then
    /// every other one in ascending order, so the rows for the network in front of the user are the
    /// ones at the top. Every card names its own family and order, so nothing is lost by not sorting
    /// on them.</para>
    /// </remarks>
    private void LandBatch(MatchSolutionBatch batch)
    {
        string current = MatchSolutionSearch.SolutionFingerprint(_design, _design.Transforms);

        foreach (var solution in batch.Set.Solutions)
            _allSolutions.Add(new MatchSolutionRowViewModel(
                this, solution, BadgeFor(solution, current), batch.Order, batch.Response));

        ReapplyFilter();
        RefreshSolutionsSummary();

        // ── EVERY cell, not only the design's own ──
        //
        // The design's own combination is searched FIRST precisely so an auto-solve can fire here
        // rather than at the end of a cross-product that takes seconds, and for almost every edit it
        // does. But that cell can come back EMPTY — a family that refuses at this order produces a
        // refusal and no rows — and the request was being consumed by that first landing and never
        // retried, so the one design most in need of a re-solve got none (owner-reported,
        // 2026-08-28). It is offered every cell now, and it keeps the request until something
        // answers it; the call costs a null check when nothing is pending.
        TryPendingAutoSolve();
    }

    private MatchSolutionBadge BadgeFor(MatchSolution solution, string currentFingerprint) =>
        string.Equals(solution.Fingerprint, currentFingerprint, StringComparison.Ordinal)
            ? MatchSolutionBadge.Current
            : _design.AppliedSolutions.Contains(solution.Fingerprint, StringComparer.Ordinal)
                ? MatchSolutionBadge.PreviouslyApplied
                : MatchSolutionBadge.NeverApplied;

    /// <summary>
    /// What the panel says when the whole cross-product came back empty.
    /// </summary>
    /// <remarks>
    /// <b>The remedies the old refusal named are gone, because they have all already been tried.</b>
    /// It used to say "order 4 and 5 do reach it with this response" — a sentence that existed
    /// (<c>FindWaysOut</c>, 2026-08-20) because the panel showed one order in one family and the user
    /// had to be told which OTHER setting to go and pick. There is no other setting now: an order or
    /// a family that reaches the target has its solutions in this list, so a list that is empty is the
    /// statement that nothing does. The current combination's own refusal is still shown, because it
    /// is the one carrying MN-1's numbers for the design in front of the user.
    /// </remarks>
    private void LandSearchComplete(MatchRefusal? refusalHere)
    {
        SolutionsRefusal = _allSolutions.Count > 0 || refusalHere is null
            ? ""
            : $"No solutions at order {_design.Order.ToString(CultureInfo.InvariantCulture)}. "
              + refusalHere.Message
              + " No other order or response family this pair permits reaches it either — the two "
              + "terminations are too far apart for a network this Designer can synthesise.";

        RefreshSolutionsSummary();

        // The last cell has landed, so a request still outstanding is one nothing in the whole
        // cross-product could answer. Dropped here rather than left to be picked up by an unrelated
        // edit's landing.
        TryPendingAutoSolve(lastChance: true);
    }

    // ── The list, and the filter over it ──────────────────────────────────────

    /// <summary>Every solution found, across every order and family, in canonical order.</summary>
    private readonly List<MatchSolutionRowViewModel> _allSolutions = [];

    /// <summary>Every solution found — the unfiltered list <see cref="Solutions"/> is a view of.</summary>
    public IReadOnlyList<MatchSolutionRowViewModel> AllSolutions => _allSolutions;

    /// <summary>True once the cross-product search has finished without being superseded.</summary>
    [ObservableProperty] private bool _solutionsComplete;

    /// <summary>Which orders and families the list shows. See <see cref="MatchSolutionFilterViewModel"/>.</summary>
    public MatchSolutionFilterViewModel Filter { get; private set; } = null!;

    private void BuildFilter()
    {
        Filter = new MatchSolutionFilterViewModel(ResponseOptions);
        Filter.Changed += (_, _) =>
        {
            lock (RefreshGate)
            {
                ReapplyFilter();
                RefreshSolutionsSummary();
            }
        };
    }

    /// <summary>
    /// Brings <see cref="Solutions"/> into step with the filter, by insert and remove rather than by
    /// rebuilding.
    /// </summary>
    /// <remarks>
    /// <c>Solutions</c> is always a SUBSEQUENCE of <see cref="_allSolutions"/> in the same order,
    /// which is what makes one pass enough. Clearing and refilling would be shorter and would scroll
    /// the panel back to the top every time a batch lands — during a search that is up to twenty
    /// times, which is unusable while the user is reading the list that is filling.
    /// </remarks>
    private void ReapplyFilter()
    {
        int i = 0;
        foreach (var row in _allSolutions)
        {
            bool showing = i < Solutions.Count && ReferenceEquals(Solutions[i], row);
            // The applied solution is listed whatever the filter says. A panel whose whole job since
            // 2026-08-28 is to make "which one am I looking at?" obvious cannot answer it by hiding
            // the answer — and a filter is a way to find a solution, not a claim about the design.
            if (Filter.Accepts(row) || row.IsCurrent)
            {
                if (!showing) Solutions.Insert(i, row);
                i++;
            }
            else if (showing)
            {
                Solutions.RemoveAt(i);
            }
        }
        while (Solutions.Count > i) Solutions.RemoveAt(Solutions.Count - 1);
    }

    /// <summary>Re-decides every row's badge against the design as it now stands.</summary>
    private void RebadgeSolutions()
    {
        if (_allSolutions.Count == 0) return;

        string current = MatchSolutionSearch.SolutionFingerprint(_design, _design.Transforms);
        foreach (var row in _allSolutions) row.Badge = BadgeFor(row.Solution, current);

        ReapplyFilter();
        RefreshSolutionsSummary();
    }

    private void RefreshSolutionsSummary()
    {
        var applied = _allSolutions.FirstOrDefault(s => s.Badge == MatchSolutionBadge.Current);
        string appliedText = applied is null
            ? _design.Transforms.Count == 0 ? "applied: none" : "applied: a hand-set transform set"
            : $"applied: {applied.CountText}, {applied.ResponseName} order {applied.Order}";

        int shown = Solutions.Count;
        int total = _allSolutions.Count;
        string count = total == 0
            ? "no solutions"
            : shown == total
                ? $"{total} solution{(total == 1 ? "" : "s")}"
                : $"{shown} of {total} solutions shown";

        SolutionsSummary = $"{count} · {appliedText}";

        // The header's "scroll to the applied solution" button is enabled by this, and every path
        // that can change which row is current — a batch landing, a re-badge, a filter toggle — comes
        // through here.
        OnPropertyChanged(nameof(HasAppliedSolution));
    }

    // ── The termination auto-solve ────────────────────────────────────────────

    /// <summary>
    /// Which end the auto-solve in progress is about — 1, 2, or 0 for "no termination edit". Set for
    /// the duration of one <c>SetTermination</c>. See <c>SetTermination</c> for why it is not read live.
    /// </summary>
    private int _autoSolveEnd;

    /// <summary>The auto-solve this edit asked for, with the epoch it was asked in, or null.</summary>
    private (int End, int Epoch)? _pendingAutoSolve;

    /// <summary>
    /// The undo stamp of the entry the termination edit already pushed, so the auto-solve's own commit
    /// AMENDS it instead of stacking a second entry — one gesture, one Ctrl+Z.
    /// </summary>
    /// <remarks>
    /// Written by <c>CommitCore</c>, on any commit made while <see cref="_pendingAutoSolve"/> is
    /// outstanding — which is exactly "this termination edit is still in progress". A stamp rather
    /// than a flag because the amend has to be REFUSED when the entry is no longer ours: this window
    /// is not modal, and a schematic edit made in the seconds between the two commits must not be the
    /// thing that gets undone.
    /// </remarks>
    private long _autoSolveCommitStamp;

    /// <summary>
    /// Offers the outstanding auto-solve request whatever solutions have landed so far, and
    /// <b>keeps it</b> until something answers it.
    /// </summary>
    /// <remarks>
    /// <b>It used to be consumed by the first landing that saw it</b>, which is right for the common
    /// case and wrong for the one that matters: the design's own combination is the FIRST cell
    /// searched, and when that cell refuses it lands with no rows at all — so the request was spent
    /// on an empty list and the design was left unmatched with a red termination, while the cells
    /// that followed filled the panel with solutions that would have reached (owner-reported,
    /// 2026-08-28).
    /// </remarks>
    /// <param name="lastChance">
    /// True at the end of the search, where a request that still cannot be answered is dropped rather
    /// than left to fire under some later edit's landing.
    /// </param>
    private void TryPendingAutoSolve(bool lastChance = false)
    {
        if (_pendingAutoSolve is not { } request) return;
        if (AutoApplyAReachingSolution(request.End, request.Epoch) || lastChance)
            _pendingAutoSolve = null;
    }

    /// <summary>
    /// After a termination edit, moves the design onto a solution that <b>reaches the target</b>
    /// whenever the one it is on does not.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported:</b> a Designer showing a network that is not matched to the value the user
    /// just declared is confusing, and the window should not present one when a solution exists.
    ///
    /// <para>Re-declaring an end moves <c>MatchSynthesisResult.RequiredTransformRatio</c> while the
    /// stored N's stay where they were, so Π N² lands off target and the far end goes on presenting
    /// the old resistance. <c>RelinkAfterSpecChange</c> already absorbs that when Link is on and one
    /// unlocked transform can carry it; the cases it cannot — Link off, every transform locked, the
    /// new target outside the rack's range, a topology change that rebuilt the ladder underneath the
    /// stored records, or no transforms at all — are what this covers, and they are exactly the cases
    /// the user was previously left to fix by hand from the solutions panel.</para>
    ///
    /// <h3>Only the design's own order and family are candidates</h3>
    /// <para>The list now spans every order and every family, and an auto-solve that reached into it
    /// freely would answer "termination 2 is 75 Ω now" by silently changing the network's ORDER —
    /// which is a design decision, and one the user made. It is a re-solve of the rack in place, so
    /// the candidates are the solutions of the combination the design is already on. Moving to
    /// another order stays what it always was: a click on a card.</para>
    ///
    /// <h3>Which solution</h3>
    /// <para><b>The one nearest what is already applied</b>, not simply the best-ranked: a rack with
    /// the same transforms in the same places, re-driven to the new N's, is the answer to "the target
    /// moved" and leaves the user's own choice of network standing. Only when the ladder no longer has
    /// that rack — a topology change renames the elements — does it fall back to MN-1's own order
    /// (fewest transforms, then position, then Q-adjust), preferring a solution whose element values
    /// are buildable over one flagged <c>ImplausibleValues</c>.</para>
    ///
    /// <para><b>It says nothing</b> (owner, 2026-08-28). Re-declaring an end is a change to the
    /// problem, and a user who has just made one is already expecting the network to be different —
    /// they can see the new schematic. A line explaining it would spend some of the specification
    /// pane's small column on something nobody needed told, which is why <c>OrderNote</c> and
    /// <c>QAdjustNote</c> beside it are not the precedent here: those two change a control the user
    /// did NOT touch, and this changes the one thing they were looking at.</para>
    ///
    /// <para>It fires at most once per edit: the request is cleared when it is taken, and the apply's
    /// own refresh queues the passes that render the result. A design that is already on target, one
    /// whose search refused, and every non-termination edit all cost nothing.</para>
    /// </remarks>
    /// <returns>
    /// True when the request is SETTLED — applied, superseded, or not needed. False only when there
    /// is nothing to move onto <em>yet</em>, which is the one case worth waiting for another cell of
    /// the search to land. See <see cref="TryPendingAutoSolve"/>.
    /// </returns>
    private bool AutoApplyAReachingSolution(int end, int epoch)
    {
        if (end == 0 || IsOrphaned) return true;

        // The design has been rebuilt since this request was made — the user went on editing while
        // the search ran, and the solutions in hand are answers to a question that has moved. Round
        // 6's fixture is exactly this: two terminations set, then three transforms added, with
        // nothing waiting in between.
        if (epoch != _designEpoch) return true;

        // ── Already matched — and "matched" is not the same as OnTarget ──
        //
        // Owner-reported, 2026-08-28: adding reactance left the termination target unmet and the
        // design on its old rack, even though a solution existed. **`OnTarget` was true.** It compares
        // Π N² against the ratio the rack has to reach, and a design whose synthesis REFUSED has no
        // ladder and no transforms at all — so both sides are 1, the comparison passes, and the one
        // state that most needs re-solving was the one this returned early on. Measured on the
        // Bessel fixture: after the edit, `Achieved = Required = 1`, `OnTarget = true`, and
        // termination 2 flagged red on screen.
        //
        // So the question asked here is the one the window is actually showing: is there a network,
        // was it built without a refusal, and does it reach? Anything else is a Designer presenting
        // something that is not a match, which is what the auto-solve exists to stop.
        if (_rebuild is null) return true;
        if (_rebuild.Refusal is null && _rebuild.Network is not null && _rebuild.OnTarget) return true;

        // NOT settled: the cells searched so far have nothing this design can move onto, and the
        // ones still to come may. Answered at the end of the search either way.
        var chosen = NearestReachingSolution();
        if (chosen is null) return false;

        // AMENDING the commit the edit already made, so the whole gesture is one undo entry (owner,
        // 2026-08-28). The stamp is cleared either way: it names one specific entry, and carrying it
        // forward would let a later auto-solve amend an edit it has nothing to do with.
        long amend = _autoSolveCommitStamp;
        _autoSolveCommitStamp = 0;
        ApplySolution(chosen, amend);                // refreshes and commits
        return true;
    }

    /// <summary>
    /// The reaching solution closest to the rack in place — <b>widening past the design's own order
    /// and family rather than leaving the window off target</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-28:</b> adding reactance left the termination target unmet and the
    /// design on its old rack, <i>even though a solution existed</i>; changing a termination, or
    /// probing a new one, must move onto a solution that meets the target whenever one exists.
    ///
    /// <para><b>This overrules the narrower rule that shipped earlier the same day</b>, which
    /// restricted the candidates to the design's own order AND family on the grounds that answering
    /// "termination 2 is 75 Ω now" by silently changing the network's ORDER would be changing a design
    /// decision the user did not make. That reasoning is sound and it is still what the search ORDER
    /// below expresses — it simply lost to the thing it was trading against. A Designer showing an
    /// unmatched network, with a red termination, while its own list holds a row that matches, is the
    /// worse of the two: the user has to notice the mismatch, work out that the fix is a different
    /// family, and find it. The order and the family are still on the card that gets applied, and
    /// that card is the bold green-bordered one in the list, so the move is not hidden — it is simply
    /// not narrated.</para>
    ///
    /// <h3>Nearest first, and "nearest" is defined by what the user chose</h3>
    /// <list type="number">
    /// <item>The design's own order AND family — the same transforms in the same places if the search
    /// still offers them, otherwise the best row there. Nothing the user picked moves, and this is
    /// the case that answers almost every edit.</item>
    /// <item>The design's own ORDER, any family. The order is the more structural of the two — it is
    /// how many elements the network has — so a family swap is the smaller concession.</item>
    /// <item>Anything, nearest order first. <c>OrderBy</c> is stable, so within one order the rows
    /// stay in MN-1's own ranking.</item>
    /// </list>
    ///
    /// <h3>The candidates are the FILTERED list — what the user can actually see</h3>
    /// <para><b>Owner, 2026-08-28:</b> the candidates must come only from the filtered list of
    /// solutions; a filter set so tight that nothing is left is a fine outcome. So this reads
    /// <see cref="Solutions"/> and not <c>AllSolutions</c>. Auto-applying a row the panel is not
    /// showing would move the design onto a network the user has said they do not want to consider,
    /// and leave them looking at a list that does not contain the thing they are now on.</para>
    ///
    /// <para><b>That also retires the separate negative-element guard, which is the same rule said
    /// twice.</b> This used to skip rows with a non-positive element unless
    /// <c>MatchDesign.AllowNegativeComponents</c> was set — but the filter's own "Allow negative
    /// components" toggle is exactly that question, it is OFF by default, and it is the one the user
    /// can see and change. The design flag is no longer an input at all: nothing in the window sets
    /// it (the Options card went on 2026-08-28) and <c>ApplySolution</c> WRITES it from whatever row
    /// was applied. Gating candidates on it meant gating on this method's own output.</para>
    ///
    /// <para>The applied row is listed whatever the filter says (see <c>ReapplyFilter</c>), so it
    /// stays a candidate — which is what makes the "re-drive the rack already in place" tier keep
    /// working when the filter excludes the design's own family. A buildable row is preferred to one
    /// flagged <c>ImplausibleValues</c> at every tier.</para>
    /// </remarks>
    private MatchSolutionRowViewModel? NearestReachingSolution()
    {
        var candidates = Solutions.ToList();
        if (candidates.Count == 0) return null;

        var here = candidates
            .Where(r => r.Order == _design.Order && r.Response == _design.Response)
            .ToList();

        var same = here.FirstOrDefault(r => IsSameRack(r.Solution, _design));
        if (same is not null) return same;
        if (Best(here) is { } best) return best;

        if (Best(candidates.Where(r => r.Order == _design.Order)) is { } sameOrder) return sameOrder;

        return Best(candidates
            .OrderBy(r => Math.Abs(r.Order - _design.Order))
            .ThenBy(r => r.Order));

        static MatchSolutionRowViewModel? Best(IEnumerable<MatchSolutionRowViewModel> rows)
        {
            var list = rows.ToList();
            return list.FirstOrDefault(r => !r.Solution.ImplausibleValues) ?? list.FirstOrDefault();
        }
    }

    /// <summary>
    /// Whether a solution is the rack the design already carries, differing only in the N's — the same
    /// element pairs, in the same order, with the same form and the same analysis-end Q-adjust.
    /// </summary>
    private static bool IsSameRack(MatchSolution solution, MatchDesign design)
    {
        if (solution.Transforms.Count != design.Transforms.Count) return false;
        if (Math.Abs(solution.QAdjust - design.QAdjust) > 1e-12) return false;

        for (int i = 0; i < solution.Transforms.Count; i++)
        {
            var a = solution.Transforms[i];
            var b = design.Transforms[i];
            if (a.Form != b.Form
                || !string.Equals(a.ElementA, b.ElementA, StringComparison.Ordinal)
                || !string.Equals(a.ElementB, b.ElementB, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void CancelAnalysis()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }
}
