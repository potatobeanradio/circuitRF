// ================================================================
//  TuneSession.cs  —  a stream of value changes in, a stream of results
//  out (brief-tuneopt-3, overview D7/D8/D9)
//
//  One prepared design, one evaluation in flight, at most one pending.
//  A newer request REPLACES the pending one and never cancels the
//  in-flight run — cancelling on every slider tick means a slow bench
//  never shows anything while the user drags. Stop, Revert, Push and
//  disposal do cancel it.
//
//  Headless: evaluations run on the thread pool and each result is
//  handed to the UI thread exactly once through the poster the caller
//  supplies. TO-4's Tuning panel is the face on this; nothing here draws.
// ================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Ui.Tuning;

/// <summary>Where a session's results go: the Data Display, and on Stop the results file.</summary>
public interface ITuneResultSink
{
    /// <summary>Shows a result in place of the results file (UI thread).</summary>
    void Publish(DataSet data);

    /// <summary>Writes the last displayed result to the results file, stating the tuned values.</summary>
    void Commit(DataSet data, IReadOnlyDictionary<string, string> values);

    /// <summary>Drops whatever was published and any snapshot; the display returns to the file.</summary>
    void Drop();

    /// <summary>Freezes what the display currently shows as a ghost (R-to3-9).</summary>
    void Snapshot();

    /// <summary>Removes the ghost.</summary>
    void ClearSnapshot();

    /// <summary>The session now evaluates at <paramref name="corner"/> (null: the nominal) — the display's chip says
    /// so (brief-yield-7 R-ya7-4).</summary>
    void AtCorner(string? corner) { }

    /// <summary>brief-sar-10 R-sar10-3 — a displayed result's fields analyses rewrote their recombined SAR and B1 beside the
    /// field set: a 3D view showing one draws it again (UI thread).</summary>
    void FieldsWritten(IReadOnlyList<FieldsArtifact> fields) { }

    /// <summary>brief-sdl-7 D7 — shows <paramref name="data"/> (an EM solve's result) as the faded traces under the live
    /// ones; null removes them.</summary>
    void ShowOverlay(DataSet? data) { }

    /// <summary>brief-sdl-7 D7 — every later <see cref="Publish(DataSet)"/> goes through <paramref name="correct"/> (the
    /// last EM solve's offset added), and the display's chip says <paramref name="label"/>; null for both returns to the
    /// circuit model as it is.</summary>
    void Correct(Func<DataSet, DataSet>? correct, string? label) { }
}

public sealed class TuneSession : IDisposable
{
    /// <summary>One evaluation of the design at <paramref name="values"/>, narrowed to
    /// <paramref name="analyses"/> (null = every enabled analysis), honouring <paramref name="control"/>'s
    /// cancellation.</summary>
    public delegate RunResult Evaluator(
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? analyses, RunControl control);

    /// <summary><see cref="Evaluator"/>, told whether the request is a slider's release (<paramref name="final"/>) — what a
    /// fields analysis needs to know to defer its averaged SAR while the knob moves (brief-sar-10 R-sar10-3).</summary>
    public delegate RunResult FinalAwareEvaluator(
        IReadOnlyDictionary<string, string> values, IReadOnlyList<string>? analyses, RunControl control, bool final);

    /// <summary>An evaluation slower than this suggests run-on-release (R-to3-3, D7).</summary>
    public static readonly TimeSpan SlowEvaluation = TimeSpan.FromSeconds(2);

    private readonly FinalAwareEvaluator _evaluate;
    private readonly ITuneResultSink? _sink;
    private readonly Action<Action>  _post;
    private readonly Func<DateTime>  _clock;
    private readonly object          _gate = new();

    private IReadOnlyDictionary<string, string>? _pending;
    private bool _pendingFinal;
    private CancellationTokenSource? _inFlightCts;
    private bool  _inFlight;
    private int   _generation;
    private bool  _disposed;
    private bool  _suggested;
    private long  _evaluations;
    private IReadOnlyDictionary<string, string>? _requested;

    // Evaluate at (brief-yield-7 R-ya7-4): how a corner name becomes a point to evaluate at, and the one chosen.
    private Func<string, (CornerPoint? Point, string? Refusal)>? _cornerFor;
    private volatile CornerPoint? _corner;

    public TuneSession(Evaluator evaluate, ITuneResultSink? sink, Action<Action> postToUi,
                       IReadOnlyList<string>? analyses = null, long sweepPoints = 0,
                       Func<DateTime>? clock = null)
        : this((values, scope, control, _) => evaluate(values, scope, control), sink, postToUi, analyses, sweepPoints, clock) { }

    public TuneSession(FinalAwareEvaluator evaluate, ITuneResultSink? sink, Action<Action> postToUi,
                       IReadOnlyList<string>? analyses = null, long sweepPoints = 0,
                       Func<DateTime>? clock = null)
    {
        _evaluate   = evaluate;
        _sink       = sink;
        _post       = postToUi;
        _clock      = clock ?? (() => DateTime.UtcNow);
        Analyses    = analyses;
        SweepPoints = sweepPoints;
    }

    /// <summary>
    /// A session over a prepared design: every evaluation is one <see cref="CircuitEvaluation.Evaluate"/>
    /// of <paramref name="circuit"/> with the requested values as tunables, so nothing is re-read.
    /// </summary>
    /// <param name="recorded">The run statistical corners came from, when at hand — read only when one is chosen.</param>
    public static TuneSession ForCircuit(PreparedCircuit circuit, ITuneResultSink? sink, Action<Action> postToUi,
                                         IReadOnlyList<string>? analyses = null, Func<RfCore.Data.DataSet?>? recorded = null)
    {
        var plan = CircuitEvaluation.Plan(circuit, new CircuitEvaluationRequest { Analyses = analyses });
        plan.Nl?.Dispose();
        TuneSession? session = null;
        session = new TuneSession(
            (values, scope, control, final) =>
            {
                // At a corner the sliders' values are the point the corner is evaluated at: its bindings over them,
                // or its trial replayed around them (brief-yield-7 R-ya7-4).
                var at = session!._corner is { } corner ? corner.At(values) : new CornerPointValues(values, null, [], null);
                if (at.Refusal is { } why) return new RunResult(RunStatus.EngineError, why.Render());
                return CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
                {
                    Tunables = at.Values, Statistics = at.Draws, Analyses = scope, Control = control,
                    // brief-sdl-7 D7: the sliders drive the circuit model — an EM solve per slider move is a run nobody
                    // waits for. Solve EM here is the one EM evaluation a session makes, on request.
                    Em = false,
                    // brief-sar-10 R-sar10-3 — a fields analysis draws local SAR and B1 while the knob moves; its averaged
                    // SAR waits for the release
                    Fields = final ? FieldsOutput.Written : FieldsOutput.Local,
                });
            },
            sink, postToUi, analyses, plan.SweepPoints);
        session._circuit = circuit;
        session.HasEm = plan.HasEm;
        session._cornerFor = name =>
        {
            var (point, _, refusal) = CornerPoint.For(circuit, name, recorded?.Invoke());
            return (point, refusal?.Render());
        };
        return session;
    }

    // ---- What the session reports (R-to3-2, R-to3-4) -------------------------

    /// <summary>The analyses evaluated, by name; null = every enabled analysis (R-to3-4).</summary>
    public IReadOnlyList<string>? Analyses { get; }

    /// <summary>Points across the parametric sweeps in scope — each request costs all of them.</summary>
    public long SweepPoints { get; }

    /// <summary>The values most recently requested.</summary>
    public IReadOnlyDictionary<string, string>? RequestedValues { get { lock (_gate) return _requested; } }

    /// <summary>The values the displayed result was computed at.</summary>
    public IReadOnlyDictionary<string, string>? DisplayedValues { get; private set; }

    /// <summary>The displayed result.</summary>
    public RunResult? DisplayedResult { get; private set; }

    /// <summary>True while an evaluation is running.</summary>
    public bool IsEvaluating { get { lock (_gate) return _inFlight; } }

    /// <summary>How long the last completed evaluation took.</summary>
    public TimeSpan? LastDuration { get; private set; }

    private DateTime? _displayedAt;

    /// <summary>How old the displayed result is.</summary>
    public TimeSpan? DisplayedAge => _displayedAt is { } at ? _clock() - at : null;

    /// <summary>The sliders are ahead of the display: the requested values are not the displayed ones.</summary>
    public bool IsLagging => !SameValues(RequestedValues, DisplayedValues);

    /// <summary>The corner the session evaluates at (brief-yield-7 R-ya7-4); null at the nominal.</summary>
    public string? Corner => _corner?.Name;

    /// <summary>
    /// Evaluates at <paramref name="corner"/> from now on — null or <see cref="CornerRun.NominalName"/> for the
    /// nominal: the in-flight evaluation is cancelled and the requested values evaluated again there. Push still
    /// writes the sliders' values alone. The refusal, when the corner cannot be evaluated here; the session then
    /// stays where it was.
    /// </summary>
    public string? EvaluateAt(string? corner)
    {
        CornerPoint? point = null;
        if (corner is not null && !corner.Equals(CornerRun.NominalName, StringComparison.OrdinalIgnoreCase))
        {
            if (_cornerFor is null) return "this session cannot evaluate at a corner";
            var (found, refusal) = _cornerFor(corner);
            if (found is null) return refusal ?? $"there is no corner '{corner}'";
            point = found;
        }
        lock (_gate)
        {
            if (_disposed) return null;
            _corner = point;
            CancelLocked();
            // The cancelled run starts the pending request when it returns; with nothing in flight, start now.
            if (_requested is { } values)
            {
                if (_inFlight) { _pending = values; _pendingFinal = true; }
                else StartLocked(values);
            }
        }
        _sink?.AtCorner(point?.Name);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Evaluations started — the counter the coalescing claim is tested on.</summary>
    public long Evaluations => Interlocked.Read(ref _evaluations);

    /// <summary>When set, only a request marked final (the slider's release) is evaluated (R-to3-3).</summary>
    public bool RunOnRelease { get; set; }

    /// <summary>Raised on the UI thread whenever a result is displayed or the session's state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once, on the UI thread, when an evaluation took longer than
    /// <see cref="SlowEvaluation"/> while run-on-release was off. The session never turns it on itself.</summary>
    public event EventHandler? RunOnReleaseSuggested;

    // ---- Requests (R-to3-1, R-to3-3) -----------------------------------------

    /// <summary>
    /// Asks for an evaluation at <paramref name="values"/>. With nothing running it starts at once;
    /// otherwise it becomes THE pending request, replacing any earlier one. <paramref name="final"/>
    /// marks a slider's release, the only request evaluated while <see cref="RunOnRelease"/> is set.
    /// </summary>
    public void Request(IReadOnlyDictionary<string, string> values, bool final = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _requested = values;
            if (RunOnRelease && !final) return;
            if (_inFlight) { _pending = values; _pendingFinal = final; return; }
            StartLocked(values, final);
        }
    }

    private void StartLocked(IReadOnlyDictionary<string, string> values, bool final = true)
    {
        _inFlight = true;
        var cts        = new CancellationTokenSource();
        _inFlightCts   = cts;
        int generation = _generation;
        Interlocked.Increment(ref _evaluations);
        Task.Run(() => Run(values, cts, generation, final));
    }

    private void Run(IReadOnlyDictionary<string, string> values, CancellationTokenSource cts, int generation, bool final)
    {
        var sw = Stopwatch.StartNew();
        RunResult? result = null;
        try
        {
            result = _evaluate(values, Analyses, new RunControl { Token = cts.Token }, final);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            result = new RunResult(RunStatus.EngineError, ex.Message);
        }
        sw.Stop();

        lock (_gate)
        {
            bool current = generation == _generation && !cts.IsCancellationRequested;
            if (current && result is not null)
            {
                var r = result; var d = sw.Elapsed;
                _post(() => Display(values, r, d, generation));
            }
            cts.Dispose();
            if (ReferenceEquals(_inFlightCts, cts)) _inFlightCts = null;
            _inFlight = false;
            if (_pending is { } next && !_disposed)
            {
                _pending = null;
                StartLocked(next, _pendingFinal);
            }
        }
    }

    private void Display(IReadOnlyDictionary<string, string> values, RunResult result, TimeSpan duration, int generation)
    {
        lock (_gate) if (generation != _generation || _disposed) return;

        LastDuration = duration;
        if (result.Status == RunStatus.Success && result.GroupedResults is { } data)
        {
            DisplayedValues = values;
            DisplayedResult = result;
            _displayedAt    = _clock();
            _sink?.Publish(data);
            if (result.Fields.Count > 0) _sink?.FieldsWritten(result.Fields);
        }
        Changed?.Invoke(this, EventArgs.Empty);

        if (duration > SlowEvaluation && !RunOnRelease && !_suggested)
        {
            _suggested = true;
            RunOnReleaseSuggested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Ending and interrupting (R-to3-1, R-to3-7) ---------------------------

    /// <summary>Cancels the in-flight evaluation and drops the pending request; any result still on
    /// its way to the UI thread is discarded.</summary>
    private void CancelLocked()
    {
        _generation++;
        _pending = null;
        _inFlightCts?.Cancel();
    }

    /// <summary>
    /// Ends the session: cancels, writes the last displayed result to the results file with
    /// provenance naming the tuned values, then drops the published result.
    /// </summary>
    public void Stop()
    {
        lock (_gate) { CancelLocked(); _disposed = true; }
        if (DisplayedResult?.GroupedResults is { } data && DisplayedValues is { } values)
            _sink?.Commit(data, values);
        _sink?.Drop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels and drops the published result; the display returns to the file as it was.
    /// The session continues.</summary>
    public void Revert()
    {
        lock (_gate) { CancelLocked(); _requested = null; }
        DisplayedValues = null;
        DisplayedResult = null;
        _displayedAt    = null;
        _sink?.Drop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels the in-flight evaluation before the values are written into the design (TO-4
    /// does the writing). The session continues.</summary>
    public void Push()
    {
        lock (_gate) CancelLocked();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Freezes the displayed result as ghosts (R-to3-9).</summary>
    public void Snapshot() => _sink?.Snapshot();

    // ---- EM here (brief-sdl-7 D7) ---------------------------------------------

    private PreparedCircuit? _circuit;

    /// <summary>Whether an analysis in scope is simulated by EM (an analysis with model=em, or a part set to EM) — what
    /// Solve EM here needs. The sliders still drive the circuit model.</summary>
    public bool HasEm { get; private set; }

    /// <summary>
    /// One EM evaluation at <paramref name="values"/> (the analyses in scope as the design says, so by EM where it says
    /// so) and the circuit model at the same values — the pair the correction's offset is made of. Off the UI thread;
    /// a geometry already in the EM cache costs no solve.
    /// </summary>
    public Task<(RunResult Em, RunResult Circuit)> SolveEmAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var circuit = _circuit ?? throw new InvalidOperationException("this session has no prepared design to solve");
        var analyses = Analyses;
        return Task.Run(() =>
        {
            var control = new RunControl { Token = ct };
            var em = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Tunables = values, Analyses = analyses, Control = control });
            var model = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
                { Tunables = values, Analyses = analyses, Control = control, Em = false });
            return (em, model);
        }, ct);
    }

    /// <summary>The EM result as faded traces under the live ones (null: none).</summary>
    public void ShowOverlay(DataSet? data) => _sink?.ShowOverlay(data);

    /// <summary>Publishes the live circuit model corrected by <paramref name="correct"/> from now on (null: as it is),
    /// re-publishing what is shown at once.</summary>
    public void Correct(Func<DataSet, DataSet>? correct, string? label)
    {
        _sink?.Correct(correct, label);
        if (DisplayedResult?.GroupedResults is { } data) _sink?.Publish(data);
    }

    /// <summary>Removes the ghosts.</summary>
    public void ClearSnapshot() => _sink?.ClearSnapshot();

    /// <summary>Cancels and drops the published result without writing anything.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelLocked();
            _disposed = true;
        }
        _sink?.Drop();
    }

    private static bool SameValues(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        return a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
    }
}
