using System.Text.Json.Nodes;

namespace CircuitRF.Design.Plugins;

/// <summary>
/// What the panel's update loop needs of a running plugin — <see cref="PluginSession"/> is the real one; a test hands
/// the controller a fake, so the loop is tested with no window and no process (brief-plg-2 §3).
/// </summary>
public interface IPluginPanelSession : IAsyncDisposable
{
    /// <summary>A <c>progress</c> frame, from any thread.</summary>
    event Action<PluginProgress>? Progress;
    /// <summary>The session ended by failing — the process exited, broke the protocol or ran past a deadline.</summary>
    event Action<PluginFailure>? Ended;
    /// <summary>The failure that ended the session, or null while it is alive.</summary>
    PluginFailure? Fatal { get; }

    Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default);
    /// <summary><c>update</c>. Cancelling <paramref name="ct"/> sends <c>cancel</c> and drops the reply on arrival.</summary>
    Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default);
    Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default);
    Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default);
    /// <summary>True once every cancelled request has been answered, false if <paramref name="within"/> passes first.</summary>
    Task<bool> SettledAsync(TimeSpan within);
    /// <summary><c>bye</c>, then the process is gone.</summary>
    Task ByeAsync();
}

/// <summary>What the panel says about the process itself, beside the plugin's own messages.</summary>
public enum PluginHostLineKind
{
    /// <summary>The process ended; the next change starts it again.</summary>
    Stopped,
    /// <summary>A request has had no answer within its deadline; Stop is offered.</summary>
    NoAnswer,
    /// <summary>Stop was pressed.</summary>
    StoppedByUser,
    /// <summary>The plugin could not be started, or refused a request in a way that leaves it running.</summary>
    Failed,
}

/// <summary>The host's line: what happened, and the details under it (the failure's sentence, the last stderr lines).</summary>
public sealed record PluginHostLine(PluginHostLineKind Kind, string Text, IReadOnlyList<string> Details)
{
    public const string StoppedText = "The plugin stopped. Change a value to restart it.";
    public bool OffersStop => Kind == PluginHostLineKind.NoAnswer;
}

/// <summary>
/// Everything the panel shows, as one value. The controller builds a new one off the UI thread and applies it
/// whole on it, so readouts, views, messages and <c>enabled</c> always come from the same reply.
/// </summary>
public sealed record PluginPanelState
{
    /// <summary>The last reply applied, or null before the first.</summary>
    public PluginReply? Reply { get; init; }
    /// <summary>Each readout parameter's value from the last reply, formatted for display.</summary>
    public IReadOnlyDictionary<string, PluginReadoutValue> Readouts { get; init; } = new Dictionary<string, PluginReadoutValue>();
    /// <summary>Every view the plugin has shown, by name: a view absent from a reply keeps what it showed, one set to
    /// null is gone (<c>plugins.md</c> §5).</summary>
    public JsonObject Views { get; init; } = new();
    /// <summary>The plugin's messages for the current parameters, then a placement's own.</summary>
    public IReadOnlyList<PluginMessage> Messages { get; init; } = [];
    /// <summary>Parameters and buttons the last reply named, and whether each is enabled. Absent means enabled.</summary>
    public IReadOnlyDictionary<string, bool> Enabled { get; init; } = new Dictionary<string, bool>();
    public PluginHostLine? Host { get; init; }
    /// <summary>A request is out (Stop applies).</summary>
    public bool InFlight { get; init; }
    /// <summary>A request has been out long enough to show the busy bar.</summary>
    public bool Busy { get; init; }
    /// <summary>Placement is running; buttons are disabled.</summary>
    public bool Committing { get; init; }
    public PluginProgress? Progress { get; init; }
    /// <summary>Parameters whose text cannot be sent, and why — marked on the control.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors { get; init; } = new Dictionary<string, string>();
    /// <summary>Values a reply <c>set</c>, by parameter, as control text; <see cref="SetSerial"/> counts them so a
    /// control is updated once, not on every later state.</summary>
    public IReadOnlyDictionary<string, string> Set { get; init; } = new Dictionary<string, string>();
    public long SetSerial { get; init; }

    public bool IsEnabled(string name) => !Enabled.TryGetValue(name, out bool on) || on;
}

/// <summary>Places what a plugin staged — the workspace's half of <b>Place in workspace</b>, which asks questions in
/// windows and so is supplied by the caller. Returns the lines to show.</summary>
public delegate Task<IReadOnlyList<PluginMessage>> PluginCommitter(IPluginPanelSession session, JsonObject parameters, CancellationToken cancel);

/// <summary>How a <see cref="PluginPanelController"/> is wired. Every time is an argument so a test can shorten it.</summary>
public sealed record PluginPanelOptions
{
    /// <summary>Where parameters resolve.</summary>
    public PluginParamContext Context { get; init; } = new(null);
    /// <summary>The text each parameter starts with (the last used); a missing one takes its default.</summary>
    public IReadOnlyDictionary<string, string>? Initial { get; init; }
    /// <summary>Runs an action on the UI thread, in order. Null runs it at once, on the calling thread.</summary>
    public Action<Action>? Post { get; init; }
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(150);
    /// <summary>How long an <c>update</c> or <c>action</c> may go unanswered before the panel says so; null takes the
    /// manifest's <c>timeouts</c>, else 30 s and 10 min.</summary>
    public TimeSpan? UpdateDeadline { get; init; }
    public TimeSpan? ActionDeadline { get; init; }
    /// <summary>How long Stop waits for the plugin to answer its <c>cancel</c> before restarting it.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How long a request is out before the busy bar shows, so a quick answer does not flicker it.</summary>
    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Hears the parameter text each time an <c>update</c> is sent — what is remembered as the last used.</summary>
    public Action<IReadOnlyDictionary<string, string>>? Sent { get; init; }
    /// <summary><b>Place in workspace</b>; null refuses it with a message.</summary>
    public PluginCommitter? Commit { get; init; }
}

/// <summary>
/// The panel's update loop (brief-plg-2 §3, <c>plugins.md</c> §6.2). UI-free: the window calls
/// <see cref="SetValue"/>, <see cref="RunAction"/>, <see cref="Commit"/> and <see cref="Stop"/>, each of which returns
/// at once, and draws <see cref="State"/> whenever <see cref="StateChanged"/> says it changed.
///
/// <list type="bullet">
/// <item>A change waits 150 ms for the next; only then is <c>update</c> sent, with every parameter's current text.</item>
/// <item>A newer request cancels the one in flight, and only the reply to the newest request is ever applied.</item>
/// <item>Text that does not resolve is marked on its control and not sent; the last good reply stays shown.</item>
/// <item>A crashed process keeps the last reply shown, says so, and the next change starts it again.</item>
/// <item>A request past its deadline says so and offers Stop; Stop cancels, and restarts a plugin that does not
/// answer the cancel within 2 s.</item>
/// </list>
/// </summary>
public sealed class PluginPanelController : IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly Func<PluginOutcome<IPluginPanelSession>> _start;
    private readonly PluginPanelOptions _options;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private PluginPanelState _state = new();
    private IPluginPanelSession? _session;
    private Task<IPluginPanelSession?>? _starting;
    private long _seq;
    private (long Seq, CancellationTokenSource Cts)? _inflight;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _commit;
    private bool _closed;

    public PluginPanelController(PluginManifest manifest, Func<PluginOutcome<IPluginPanelSession>> start, PluginPanelOptions? options = null)
    {
        _manifest = manifest;
        _start = start;
        _options = options ?? new PluginPanelOptions();
        _post = _options.Post ?? (a => a());
        foreach (var p in manifest.Params)
            if (p.Kind != PluginParamKind.Readout)
                _values[p.Name] = _options.Initial?.TryGetValue(p.Name, out var given) == true ? given : PluginParams.DefaultText(p);
    }

    public PluginManifest Manifest => _manifest;

    /// <summary>The state last applied. Read it on the UI thread.</summary>
    public PluginPanelState State { get { lock (_gate) return _state; } }

    /// <summary>Raised on the UI thread (through <see cref="PluginPanelOptions.Post"/>) after each change of state.</summary>
    public event Action<PluginPanelState>? StateChanged;

    /// <summary>A parameter's current text.</summary>
    public string Value(string name) { lock (_gate) return _values.TryGetValue(name, out var v) ? v : ""; }

    /// <summary>Every parameter's current text.</summary>
    public IReadOnlyDictionary<string, string> Values { get { lock (_gate) return new Dictionary<string, string>(_values); } }

    /// <summary>How many sessions have been started — what a restart test counts.</summary>
    public int Starts { get; private set; }

    // ── What the window calls ───────────────────────────────────────────────────

    /// <summary>Opens the panel: <c>hello</c>, then <c>update</c> with the starting values.</summary>
    public void Open() => Send(null);

    /// <summary>The user changed <paramref name="name"/>. Debounced; text equal to the current value is not a change,
    /// which is how a value a reply <c>set</c> reaches its control without sending another <c>update</c>.</summary>
    public void SetValue(string name, string text)
    {
        CancellationTokenSource debounce;
        lock (_gate)
        {
            if (_closed || !_values.ContainsKey(name) || _values[name] == text) return;
            _values[name] = text;
            _debounce?.Cancel();
            _debounce = debounce = new CancellationTokenSource();
        }
        _ = Task.Delay(_options.Debounce, debounce.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Send(null);
        }, TaskScheduler.Default);
    }

    /// <summary>A button: <c>commit</c> runs placement, any other name sends <c>action</c>.</summary>
    public void RunAction(string name)
    {
        if (name == PluginButtonLeaf.CommitAction) { Commit(); return; }
        Send(name);
    }

    /// <summary>Stop: cancels what is in flight; a plugin that has not answered its <c>cancel</c> within the grace is
    /// restarted (<c>hello</c> only — the next change asks again).</summary>
    public void Stop()
    {
        (long Seq, CancellationTokenSource Cts)? inflight;
        IPluginPanelSession? session;
        lock (_gate)
        {
            inflight = _inflight;
            if (inflight is null && _commit is null) return;
            _inflight = null;
            _seq++;                  // anything still on its way is now stale
            session = _session;
            _commit?.Cancel();
        }
        inflight?.Cts.Cancel();
        Change(s => s with { InFlight = false, Busy = false, Progress = null,
                             Host = new PluginHostLine(PluginHostLineKind.StoppedByUser, "Stopped.", []) });

        if (session is null) return;
        _ = Task.Run(async () =>
        {
            if (await session.SettledAsync(_options.StopGrace)) return;
            lock (_gate)
            {
                if (_closed || !ReferenceEquals(_session, session)) return;
                _session = null;
            }
            await session.ByeAsync();
            var fresh = await EnsureSessionAsync();
            if (fresh is not null)
                Change(s => s with { Host = new PluginHostLine(PluginHostLineKind.StoppedByUser,
                    "Stopped, and the plugin was restarted. Change a value to ask again.", []) });
        });
    }

    /// <summary><b>Place in workspace</b>. Buttons are disabled while it runs.</summary>
    public void Commit()
    {
        var (values, errors) = Resolve();
        if (values is null) { Change(s => s with { FieldErrors = errors }); return; }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_closed || _commit is not null) return;
            _commit = cts = new CancellationTokenSource();
        }
        Change(s => s with { Committing = true, FieldErrors = errors });

        _ = Task.Run(async () =>
        {
            IReadOnlyList<PluginMessage> lines;
            try
            {
                var session = await EnsureSessionAsync();
                if (session is null) lines = [];
                else if (_options.Commit is null) lines = [new PluginMessage("error", "This panel cannot place anything here.")];
                else lines = await _options.Commit(session, values, cts.Token);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                lines = [new PluginMessage("error", $"Placing failed: {e.Message}")];
            }
            lock (_gate) _commit = null;
            cts.Dispose();
            Change(s => s with
            {
                Committing = false,
                Messages = [.. (s.Reply?.Messages ?? []), .. lines],
            });
        });
    }

    /// <summary>Closes the panel: <c>bye</c>, and nothing more is sent.</summary>
    public async Task CloseAsync()
    {
        IPluginPanelSession? session;
        Task<IPluginPanelSession?>? starting;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _debounce?.Cancel();
            _inflight?.Cts.Cancel();
            _inflight = null;
            _commit?.Cancel();
            session = _session;
            starting = _starting;
            _session = null;
        }
        if (session is null && starting is not null) session = await starting;
        if (session is not null) await session.ByeAsync();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    // ── The loop ────────────────────────────────────────────────────────────────

    /// <summary>Sends <c>update</c> (or <c>action</c> when <paramref name="action"/> is named) with the current text,
    /// superseding whatever is in flight — or marks the fields that cannot be sent and sends nothing.</summary>
    private void Send(string? action)
    {
        var (values, errors) = Resolve();
        Change(s => s with { FieldErrors = errors });
        if (values is null) return;

        long seq;
        CancellationTokenSource cts;
        (long Seq, CancellationTokenSource Cts)? prior;
        IReadOnlyDictionary<string, string> sentText;
        lock (_gate)
        {
            if (_closed) return;
            prior = _inflight;
            seq = ++_seq;
            cts = new CancellationTokenSource();
            _inflight = (seq, cts);
            sentText = new Dictionary<string, string>(_values);
        }
        prior?.Cts.Cancel();      // the session sends cancel for it, and drops its reply when it comes
        if (action is null) try { _options.Sent?.Invoke(sentText); } catch { }

        Change(s => s with { InFlight = true, Progress = null });
        _ = RunAsync(seq, cts, action, values);
    }

    private async Task RunAsync(long seq, CancellationTokenSource cts, string? action, JsonObject values)
    {
        var deadline = action is null
            ? _options.UpdateDeadline ?? Secs(_manifest.Timeouts.Update) ?? TimeSpan.FromSeconds(30)
            : _options.ActionDeadline ?? Secs(_manifest.Timeouts.Action) ?? TimeSpan.FromMinutes(10);

        // Neither watcher holds the request: each only posts a line if the request is still the newest when it fires.
        _ = Task.Delay(_options.BusyDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Change(s => s with { Busy = true }, () => IsCurrent(seq));
        }, TaskScheduler.Default);
        _ = Task.Delay(deadline, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Change(s => s with { Host = new PluginHostLine(PluginHostLineKind.NoAnswer, $"No answer in {Describe(deadline)}.", []) },
                       () => IsCurrent(seq));
        }, TaskScheduler.Default);

        var session = await EnsureSessionAsync();
        if (session is null) { Finish(seq); return; }
        if (cts.IsCancellationRequested) return;

        PluginOutcome<PluginReply> outcome;
        try
        {
            outcome = action is null ? await session.UpdateAsync(values, cts.Token) : await session.ActionAsync(action, values, cts.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = PluginOutcome<PluginReply>.Fail(new PluginFailure(PluginFailureKind.Protocol, e.Message, []));
        }

        if (!IsCurrent(seq)) return;   // superseded or stopped: never applied

        if (outcome.Failure is { } f)
        {
            if (f.Kind == PluginFailureKind.Cancelled) return;
            Finish(seq, session.Fatal is not null || IsFatal(f.Kind)
                ? Stopped(f)
                : new PluginHostLine(PluginHostLineKind.Failed, f.Sentence, []));
            return;
        }

        // The model is built here, off the UI thread; the post only swaps it in.
        var reply = outcome.Value!;
        var readouts = new Dictionary<string, PluginReadoutValue>(StringComparer.Ordinal);
        foreach (var p in _manifest.Params.Where(p => p.Kind == PluginParamKind.Readout))
            readouts[p.Name] = reply.Readouts.TryGetPropertyValue(p.Name, out var rv) ? PluginValueFormat.Readout(rv, p.Unit) : PluginReadoutValue.Empty;
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, node) in reply.Enabled)
            if (node is JsonValue b && b.TryGetValue<bool>(out bool on)) enabled[name] = on;
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reply.Set is { } setObj)
            foreach (var (name, node) in setObj)
                if (_manifest.Params.FirstOrDefault(p => p.Name == name && p.Kind != PluginParamKind.Readout) is { } p)
                    set[name] = PluginValueFormat.ControlText(p, node);

        Change(s =>
        {
            var views = (JsonObject)s.Views.DeepClone();
            foreach (var (name, body) in reply.Views)
            {
                if (body is null) views.Remove(name);
                else views[name] = body.DeepClone();
            }
            lock (_gate)
            {
                foreach (var (name, text) in set) _values[name] = text;
                if (_inflight?.Seq == seq) _inflight = null;
            }
            return s with
            {
                Reply = reply, Readouts = readouts, Views = views, Messages = reply.Messages, Enabled = enabled,
                // A plugin may answer and then exit: the answer is shown, and so is the exit.
                Host = session.Fatal is { } gone ? Stopped(gone) : null,
                InFlight = false, Busy = false, Progress = null,
                Set = set.Count > 0 ? set : s.Set, SetSerial = set.Count > 0 ? s.SetSerial + 1 : s.SetSerial,
            };
        }, () => IsCurrent(seq));
    }

    /// <summary>The running session, starting one (and saying <c>hello</c>) when there is none or it has ended.
    /// Concurrent callers share one start.</summary>
    private Task<IPluginPanelSession?> EnsureSessionAsync()
    {
        lock (_gate)
        {
            if (_closed) return Task.FromResult<IPluginPanelSession?>(null);
            if (_session is { Fatal: null } live) return Task.FromResult<IPluginPanelSession?>(live);
            if (_starting is { } pending) return pending;
            var old = _session;
            _session = null;
            if (old is not null) _ = old.ByeAsync();
            return _starting = Task.Run(StartAsync);
        }

        async Task<IPluginPanelSession?> StartAsync()
        {
            try
            {
                var started = _start();
                Starts++;
                if (started.Failure is { } refused)
                {
                    Change(s => s with { Host = new PluginHostLine(PluginHostLineKind.Failed, refused.Sentence, []) });
                    return null;
                }
                var session = started.Value!;
                session.Progress += OnProgress;
                session.Ended += f => OnEnded(session, f);
                var hello = await session.HelloAsync();
                if (hello.Failure is { } hf)
                {
                    await session.ByeAsync();
                    Change(s => s with { Host = Stopped(hf), InFlight = false, Busy = false });
                    return null;
                }
                lock (_gate)
                {
                    if (_closed) { _ = session.ByeAsync(); return null; }
                    _session = session;
                }
                return session;
            }
            finally
            {
                lock (_gate) _starting = null;
            }
        }
    }

    private void OnProgress(PluginProgress p)
    {
        lock (_gate) if (_inflight is null) return;
        Change(s => s with { Progress = p, Busy = true });
    }

    /// <summary>The process ended. A request still out is answered by the session with this failure (or had its
    /// reply already), so it finishes itself; this only says so — and the last reply stays shown.</summary>
    private void OnEnded(IPluginPanelSession session, PluginFailure failure)
    {
        lock (_gate) if (_closed || !ReferenceEquals(_session, session)) return;
        Change(s => s with { Host = Stopped(failure) });
    }

    private static PluginHostLine Stopped(PluginFailure f)
        => new(PluginHostLineKind.Stopped, PluginHostLine.StoppedText, [f.Sentence, .. f.StderrTail.TakeLast(5)]);

    private static bool IsFatal(PluginFailureKind k)
        => k is PluginFailureKind.Exited or PluginFailureKind.Timeout or PluginFailureKind.Protocol or PluginFailureKind.StartFailed;

    private bool IsCurrent(long seq) { lock (_gate) return !_closed && _inflight?.Seq == seq && seq == _seq; }

    private void Finish(long seq, PluginHostLine? host = null)
    {
        lock (_gate) if (_inflight?.Seq == seq) _inflight = null; else return;
        Change(s => s with { InFlight = false, Busy = false, Progress = null, Host = host ?? s.Host });
    }

    private (JsonObject? Values, IReadOnlyDictionary<string, string> Errors) Resolve()
    {
        Dictionary<string, string> texts;
        lock (_gate) texts = new Dictionary<string, string>(_values);
        var values = new JsonObject();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in _manifest.Params)
        {
            if (p.Kind == PluginParamKind.Readout) continue;
            var (node, error) = PluginParams.ResolveValue(p, texts.GetValueOrDefault(p.Name, ""), _options.Context);
            if (error is not null) errors[p.Name] = error;
            else values[p.Name] = node;
        }
        return (errors.Count == 0 ? values : null, errors);
    }

    /// <summary>Builds the next state from the current one and applies it on the UI thread — unless
    /// <paramref name="still"/>, asked there, says the change no longer applies.</summary>
    private void Change(Func<PluginPanelState, PluginPanelState> next, Func<bool>? still = null)
    {
        _post(() =>
        {
            PluginPanelState s;
            lock (_gate)
            {
                if (_closed) return;
                // _gate is re-entrant, so the predicate and the builder may take it too.
                if (still is not null && !still()) return;
                s = _state = next(_state);
            }
            StateChanged?.Invoke(s);
        });
    }

    private static TimeSpan? Secs(double? s) => s is { } v ? TimeSpan.FromSeconds(v) : null;

    private static string Describe(TimeSpan t)
        => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.#} s" : $"{t.TotalMilliseconds:0} ms";
}
