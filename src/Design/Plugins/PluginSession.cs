using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Design.Frames;

namespace CircuitRF.Design.Plugins;

/// <summary>Why a plugin request did not produce an answer. Each is a result the caller shows, never an exception.</summary>
public enum PluginFailureKind
{
    /// <summary>The user has not allowed this plugin, or has refused it.</summary>
    NotPermitted,
    /// <summary>A policy file, the environment or the preference turns plugins off.</summary>
    DisabledByPolicy,
    /// <summary>Written for a newer plugin contract than this build speaks.</summary>
    NeedsNewerCircuitRF,
    /// <summary>No entry for this platform and no Python entry.</summary>
    NoBuild,
    /// <summary>The program would not start — missing, not executable, or no Python interpreter.</summary>
    StartFailed,
    /// <summary>The program exited before it answered.</summary>
    Exited,
    /// <summary>A deadline passed; the program was killed.</summary>
    Timeout,
    /// <summary>The program broke the protocol: not a frame, not JSON, a wrong id or type.</summary>
    Protocol,
    /// <summary>The program named a file outside its staging folder.</summary>
    OutsideStaging,
    /// <summary>The request was cancelled — Stop, or a newer request.</summary>
    Cancelled,
    /// <summary>A <c>window</c> plugin that is already running was started again (brief-plg-4 §3).</summary>
    AlreadyRunning,
}

/// <summary>A failure, the sentence that says what happened, and the plugin's last stderr lines.</summary>
public sealed record PluginFailure(PluginFailureKind Kind, string Sentence, IReadOnlyList<string> StderrTail)
{
    public override string ToString() => Sentence;
}

/// <summary>An answer or a failure — never both, never neither.</summary>
public sealed record PluginOutcome<T>(T? Value, PluginFailure? Failure) where T : class
{
    public bool Ok => Failure is null;
    public static PluginOutcome<T> Of(T value) => new(value, null);
    public static PluginOutcome<T> Fail(PluginFailure failure) => new(null, failure);
}

/// <summary>An <c>error</c>, <c>warning</c> or <c>info</c> line from the plugin (<c>plugins.md</c> §4.2 messages).</summary>
public sealed record PluginMessage(string Severity, string Text)
{
    public bool IsError => Severity == "error";
}

/// <summary>What <c>hello</c> agreed.</summary>
public sealed record PluginHello(int ApiVersion);

/// <summary>A plugin's <c>reply</c> to <c>update</c> or <c>action</c> (<c>plugins.md</c> §6.2).</summary>
public sealed record PluginReply(
    JsonObject Readouts, JsonObject Views, IReadOnlyList<PluginMessage> Messages, JsonObject Enabled, JsonObject? Set)
{
    /// <summary>The reply frame's int64 payload — a <c>layout</c> view's geometry, which its shapes' spans index
    /// (<c>pcell-wire-schema.md</c> §4.3). Empty when the frame carried none.</summary>
    public ReadOnlyMemory<long> Payload { get; init; }

    public bool HasError => Messages.Any(m => m.IsError);
}

/// <summary>One cell a plugin staged: its suggested name (null takes the id's tail) and its files, each already
/// resolved to a full path inside the staging folder.</summary>
public sealed record PluginPlacedCell(string? Name, IReadOnlyList<string> Files)
{
    /// <summary>A <c>layout</c> view whose shapes and ports become the cell's <c>.clay</c> (brief-plg-3 §3): circuitRF
    /// writes it, so what is placed is what the preview drew. Null when the plugin names none.</summary>
    public string? LayoutView { get; init; }
}

/// <summary>A plugin's answer to <c>commit</c>.</summary>
public sealed record PluginPlaced(IReadOnlyList<PluginPlacedCell> Cells, IReadOnlyList<PluginMessage> Messages)
{
    public bool HasError => Messages.Any(m => m.IsError);
}

/// <summary>A <c>progress</c> frame, for the busy bar.</summary>
public sealed record PluginProgress(long Id, double? Fraction, string? Text);

/// <summary>Deadline overrides. The manifest's <c>timeouts</c> come first; these override both for a caller that
/// must not wait the default (a test of a plugin that never answers).</summary>
public sealed record PluginDeadlines(TimeSpan? Hello = null, TimeSpan? Update = null, TimeSpan? Action = null, TimeSpan? Commit = null)
{
    /// <summary>How long a host call's <c>simulate</c> may run inside a request. Null is nine tenths of the request's own
    /// manifest deadline (an <c>update</c>'s 30 s, an <c>action</c>'s 10 min), which leaves the plugin the last tenth
    /// to report the cut-off — and is deliberately NOT <see cref="Update"/>, which a panel sets to infinite because
    /// its own controller keeps the deadline (brief-plg-4 §2).</summary>
    public TimeSpan? Simulate { get; init; }
}

/// <summary>What circuitRF did with a <c>window</c> plugin's <c>placed</c>: the cells it placed, or why not.</summary>
public sealed record PluginWindowAnswer(IReadOnlyList<string> Cells, string? Error);

/// <summary>Everything <see cref="PluginSession.Start"/> needs. Preferences are arguments, never read here.</summary>
public sealed record PluginStartOptions
{
    public required PluginTrustStore Trust { get; init; }
    public required PluginPolicyState Policy { get; init; }
    /// <summary>Where staging folders go; null is <see cref="ScratchFolder.Root"/>.</summary>
    public string? ScratchRoot { get; init; }
    /// <summary>The workspace folder the plugin is opened in, or null.</summary>
    public string? WorkspaceDir { get; init; }
    /// <summary>Where it was opened from (<c>plugins.md</c> §6.2 <c>context</c>), or null.</summary>
    public JsonObject? Context { get; init; }
    /// <summary><c>interactive</c> for a window, <c>headless</c> for <c>plugin run</c>.</summary>
    public string Mode { get; init; } = "interactive";
    /// <summary>Keep the staging folder after the session — Keep staging, for plugin authors.</summary>
    public bool KeepStaging { get; init; }
    /// <summary>The Python interpreter to run a <c>python</c> plugin with; null discovers one.</summary>
    public string? PythonCommand { get; init; }
    public IReadOnlyList<string> PythonArguments { get; init; } = [];
    /// <summary>Arguments after the program's own — the broken example's mode, a plugin author's debug switch.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public PluginDeadlines Deadlines { get; init; } = new();
    /// <summary>Each stderr line, for the run log.</summary>
    public Action<string>? Log { get; init; }
    /// <summary>circuitRF's own verbs, which <c>simulate</c>, a <c>reference</c> and an expression in a document are
    /// answered with (<see cref="IPluginVerbs"/>); null refuses those calls.</summary>
    public IPluginVerbs? Verbs { get; init; }
    /// <summary>A <c>window</c> plugin's <c>placed</c> (brief-plg-4 §3): places the cells, as §6.3 places a commit's.
    /// Null refuses it.</summary>
    public Func<PluginPlaced, Task<PluginWindowAnswer>>? WindowPlaced { get; init; }
    /// <summary>A <c>window</c> plugin's <c>open</c>: opens a staged or workspace document, already checked to be one;
    /// returns null, or why it could not. Null refuses it.</summary>
    public Func<string, Task<string?>>? WindowOpen { get; init; }
}

/// <summary>
/// One running plugin process and its conversation (brief-plg-1 §3, <c>plugins.md</c> §6).
///
/// <para><b>A plugin cannot take circuitRF down.</b> Frames are read on a thread of their own and every request is
/// awaited against a deadline, so no caller ever blocks on a pipe. Whatever goes wrong — the process exits, never
/// answers, writes text where frames belong, sends a frame that does not decode, answers an id nobody asked, or names
/// a file outside its staging folder — ends as a <see cref="PluginFailure"/> RESULT with a sentence, and for anything
/// that leaves the stream out of step, the process tree is killed.</para>
///
/// <para><b>Nothing starts unless it may.</b> <see cref="Start"/> consults the policy and the trust store before a
/// process exists; the catalogue never comes here at all.</para>
/// </summary>
public sealed class PluginSession : IPluginPanelSession
{
    public const string Peer = "The plugin";
    private const int StderrLines = 200;
    private const int StderrLineChars = 2000;
    private const int QuoteBytes = 200;

    private static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> StartProbe = new();

    /// <summary>
    /// The counting probe series gate 2 reads: from this call on, every plugin process started in the CALLER'S async
    /// flow adds one to the box. Per flow rather than per process, so a test counting starts is not disturbed by
    /// another test starting a plugin beside it.
    /// </summary>
    public static System.Runtime.CompilerServices.StrongBox<int> CountStartsInThisFlow()
    {
        var box = new System.Runtime.CompilerServices.StrongBox<int>();
        StartProbe.Value = box;
        return box;
    }

    private static readonly HashSet<PluginSession> Live = [];
    static PluginSession() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        PluginSession[] all;
        lock (Live) all = [.. Live];
        foreach (var s in all) s.KillTree();
    };

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly RecordingStream _stdout;
    private readonly PluginManifest _manifest;
    private readonly PluginStartOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly HashSet<long> _cancelled = [];
    private readonly LinkedList<string> _stderr = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _hostLock = new(1, 1);
    private readonly Dictionary<long, CancellationTokenSource> _hostCalls = [];
    private readonly CancellationTokenSource _life = new();
    private readonly List<Task> _windowWork = [];
    private readonly PluginHostServices _host;
    private long _nextId;
    private PluginFailure? _fatal;
    private int _closed;

    /// <summary>The folder this session's plugin writes into.</summary>
    public string StagingDirectory { get; }
    public string PluginDirectory { get; }
    public PluginManifest Manifest => _manifest;

    /// <summary>Raised from the reader thread for each <c>progress</c> frame.</summary>
    public event Action<PluginProgress>? Progress;

    /// <summary>Raised once, from whichever thread noticed, when the session ends by failing — the process exited,
    /// broke the protocol or ran past a deadline. Not raised by <see cref="ByeAsync"/>: closing is not a failure.</summary>
    public event Action<PluginFailure>? Ended;

    /// <summary>The plugin's last stderr lines, oldest first (at most 200).</summary>
    public IReadOnlyList<string> StderrTail { get { lock (_stderr) return [.. _stderr]; } }

    /// <summary>The failure that ended the session, or null while it is alive.</summary>
    public PluginFailure? Fatal { get { lock (_gate) return _fatal; } }

    private sealed record Answer(JsonObject? Message, PluginFailure? Failure, ReadOnlyMemory<long> Payload = default);
    private sealed record Pending(string Type, string Expect, TaskCompletionSource<Answer> Done, DateTime Sent);

    private PluginSession(Process process, PluginManifest manifest, string pluginDir, string staging, PluginStartOptions options)
    {
        _process = process;
        _manifest = manifest;
        _options = options;
        PluginDirectory = pluginDir;
        StagingDirectory = staging;
        _stdin = process.StandardInput.BaseStream;
        _stdout = new RecordingStream(process.StandardOutput.BaseStream, QuoteBytes);
        _host = new PluginHostServices(staging, options.WorkspaceDir, options.Verbs);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            string line = e.Data.Length > StderrLineChars ? e.Data[..StderrLineChars] + "…" : e.Data;
            lock (_stderr)
            {
                _stderr.AddLast(line);
                while (_stderr.Count > StderrLines) _stderr.RemoveFirst();
            }
            try { options.Log?.Invoke(line); } catch { /* a log sink must not stop the drain */ }
        };
        process.BeginErrorReadLine();

        lock (Live) Live.Add(this);
        new Thread(ReadLoop) { IsBackground = true, Name = $"plugin {manifest.Id} reader" }.Start();
    }

    // ── Start ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why <see cref="Start"/> would refuse <paramref name="entry"/> before starting anything — policy first, then the
    /// manifest, permission, the contract version and a build for this machine — or null when it would start it. A
    /// headless run asks this before it judges the parameters, so a plugin that may not run is never argued with.
    /// </summary>
    public static PluginFailure? Refusal(PluginEntry entry, PluginStartOptions options)
    {
        if (!options.Policy.Allowed)
            return Refuse(PluginFailureKind.DisabledByPolicy, options.Policy.Reason.Length > 0 ? options.Policy.Reason : "Plugins are turned off.");

        if (entry.Manifest is not { } manifest)
            return Refuse(PluginFailureKind.StartFailed, $"{entry.DisplayName} cannot run: {entry.DimReason}.");

        var decision = options.Trust.Decide(entry.Directory);
        if (decision != PluginTrustDecision.Allowed)
            return Refuse(PluginFailureKind.NotPermitted, decision == PluginTrustDecision.Denied
                ? $"{manifest.Name} is not allowed to run on this machine. Settings ▸ Plugins changes that."
                : $"{manifest.Name} has not been allowed to run on this machine yet.");

        if (manifest.NeedsNewerCircuitRF is { } newer)
            return Refuse(PluginFailureKind.NeedsNewerCircuitRF, manifest.ApiVersion > PluginApi.Max
                ? $"{manifest.Name} {newer} (it is written for plugin contract {manifest.ApiVersion})."
                : $"{manifest.Name} {newer}; this is circuitRF {PluginVersions.CircuitRF}.");

        var run = manifest.ResolveRun();
        if (run.Entry is null)
            return Refuse(PluginFailureKind.NoBuild, $"{manifest.Name} has {run.Refusal}.");

        // An installed Python plugin runs in its own environment (brief-plg-6 §3); one whose environment is not made yet,
        // or could not be, does not fall back to another interpreter.
        if (entry.Source == PluginSource.Installed && run.Entry.IsPython && PluginInstaller.RecordFor(entry.Directory) is { } record)
        {
            if (record.NeedsEnvironment)
                return Refuse(PluginFailureKind.StartFailed, record.Environment == PluginActiveRecord.Pending
                    ? $"{manifest.Name}'s Python environment has not been made yet. Allowing it in Settings ▸ Plugins makes it."
                    : $"{manifest.Name}'s Python environment could not be made: {record.Environment}");
            if (!PluginPythonEnvironment.IsPresent(entry.Directory, record.Environment))
                return Refuse(PluginFailureKind.StartFailed,
                    $"The Python environment {manifest.Name} runs in is missing ({PluginPythonEnvironment.InterpreterFor(entry.Directory, record.Environment)}). Set it to Ask and then Allowed in Settings ▸ Plugins to make it again.");
        }
        return null;

        static PluginFailure Refuse(PluginFailureKind kind, string sentence) => new(kind, sentence, []);
    }

    /// <summary>
    /// Starts <paramref name="entry"/>'s program, or refuses — policy first, then permission, then the contract
    /// version, then a build for this machine — with no process started for any refusal. The staging folder is
    /// created before the process and named in <c>hello</c>.
    /// </summary>
    public static PluginOutcome<PluginSession> Start(PluginEntry entry, PluginStartOptions options)
    {
        if (Refusal(entry, options) is { } refused) return PluginOutcome<PluginSession>.Fail(refused);
        var manifest = entry.Manifest!;
        var how = manifest.ResolveRun().Entry!;

        ProcessStartInfo info;
        if (how.IsPython)
        {
            string? command = options.PythonCommand;
            IReadOnlyList<string> prefix = options.PythonArguments;
            // The environment its requirements were installed into (brief-plg-6 §3): the shared one or its own. With none,
            // the shared Python itself, found below as PDK kits find it.
            if (command is null && entry.Source == PluginSource.Installed
                && PluginPythonEnvironment.InterpreterFor(entry.Directory, PluginInstaller.RecordFor(entry.Directory)?.Environment) is { } env && File.Exists(env))
                (command, prefix) = (env, []);
            else if (command is null && File.Exists(PluginPythonEnvironment.Interpreter(entry.Directory)))
                (command, prefix) = (PluginPythonEnvironment.Interpreter(entry.Directory), []);
            if (command is null)
            {
                var found = PythonInterpreterDiscovery.Find(null, null, out var rejected);
                if (found is null)
                    return Refuse(PluginFailureKind.StartFailed, $"{manifest.Name} needs Python, and none was found: {PythonInterpreterDiscovery.DescribeFailure(rejected)}");
                (command, prefix) = (found.Command, found.Arguments);
            }
            info = new ProcessStartInfo(command);
            foreach (string a in prefix) info.ArgumentList.Add(a);
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(Path.GetFullPath(Path.Combine(entry.Directory, how.PythonEntry!)));
            info.Environment["PYTHONIOENCODING"] = "utf-8";
        }
        else
        {
            string exe = Path.GetFullPath(Path.Combine(entry.Directory, how.Exe!));
            if (!File.Exists(exe))
                return Refuse(PluginFailureKind.StartFailed, $"{manifest.Name}'s program {how.Exe} is not in its folder {entry.Directory}.");
            info = new ProcessStartInfo(exe);
        }
        foreach (string a in options.ExtraArguments) info.ArgumentList.Add(a);
        info.WorkingDirectory       = entry.Directory;
        info.RedirectStandardInput  = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError  = true;
        info.UseShellExecute        = false;
        info.CreateNoWindow         = true;

        string staging;
        try { staging = PluginStaging.Create(options.ScratchRoot ?? ScratchFolder.Root, manifest.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse(PluginFailureKind.StartFailed, $"The staging folder for {manifest.Name} could not be made: {e.Message}");
        }

        Process? process;
        try
        {
            if (StartProbe.Value is { } probe) Interlocked.Increment(ref probe.Value);
            process = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            PluginStaging.Delete(staging);
            return Refuse(PluginFailureKind.StartFailed, $"{manifest.Name} could not be started ({e.Message}).");
        }
        if (process is null)
        {
            PluginStaging.Delete(staging);
            return Refuse(PluginFailureKind.StartFailed, $"{manifest.Name} could not be started.");
        }

        return PluginOutcome<PluginSession>.Of(new PluginSession(process, manifest, entry.Directory, staging, options));

        static PluginOutcome<PluginSession> Refuse(PluginFailureKind kind, string sentence)
            => PluginOutcome<PluginSession>.Fail(new PluginFailure(kind, sentence, []));
    }

    // ── Requests ────────────────────────────────────────────────────────────────

    /// <summary><c>hello</c>: the workspace, the context, the staging folder; the plugin answers with the contract
    /// version it will speak, which must be one this build supports and no newer than its manifest says.</summary>
    public async Task<PluginOutcome<PluginHello>> HelloAsync(CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["apiVersion"] = Math.Min(_manifest.ApiVersion, PluginApi.Max),
            ["circuitRF"]  = CircuitRfVersion,
            ["platform"]   = PluginPlatform.CurrentRid,
            ["staging"]    = StagingDirectory,
            ["workspace"]  = _options.WorkspaceDir,
            ["context"]    = _options.Context?.DeepClone(),
            ["mode"]       = _options.Mode,
        };
        var answer = await RequestAsync("hello", body, "hello", _options.Deadlines.Hello ?? TimeSpan.FromSeconds(30), ct);
        if (answer.Failure is { } f) return PluginOutcome<PluginHello>.Fail(f);

        int? api = answer.Message!["apiVersion"] is JsonValue v && v.TryGetValue<int>(out int a) ? a : null;
        if (api is null || api < PluginApi.Min || api > Math.Min(_manifest.ApiVersion, PluginApi.Max))
            return PluginOutcome<PluginHello>.Fail(await Task.Run(() => Fail(PluginFailureKind.Protocol,
                $"{Peer} answered hello with contract version {(api?.ToString() ?? "none")}; this circuitRF speaks {PluginApi.Min}–{PluginApi.Max} and the manifest says {_manifest.ApiVersion}.")));
        // It started: an update's previous version is no longer needed for a roll back (brief-plg-6 §1).
        PluginInstaller.ConfirmStarted(PluginDirectory);
        return PluginOutcome<PluginHello>.Of(new PluginHello(api.Value));
    }

    /// <summary><c>update</c> with the current parameters.</summary>
    public Task<PluginOutcome<PluginReply>> UpdateAsync(JsonObject parameters, CancellationToken ct = default)
        => ReplyAsync("update", new JsonObject { ["params"] = parameters.DeepClone() },
                      _options.Deadlines.Update ?? Seconds(_manifest.Timeouts.Update) ?? TimeSpan.FromSeconds(30), ct);

    /// <summary><c>event</c> (<c>plugins.md</c> §6.2): a bound marker or a handle was dragged, an image clicked.
    /// <paramref name="fields"/> are the event's own (<c>marker</c>, <c>x</c>, <c>param</c>, <c>value</c>, …), sent beside
    /// <c>view</c>, <c>kind</c> and the parameters. Its deadline is an <c>update</c>'s: it answers a gesture.</summary>
    public Task<PluginOutcome<PluginReply>> EventAsync(string view, string kind, JsonObject fields, JsonObject parameters, CancellationToken ct = default)
    {
        var body = (JsonObject)fields.DeepClone();
        body["view"] = view;
        body["kind"] = kind;
        body["params"] = parameters.DeepClone();
        return ReplyAsync("event", body, _options.Deadlines.Update ?? Seconds(_manifest.Timeouts.Update) ?? TimeSpan.FromSeconds(30), ct);
    }

    /// <summary><c>action</c>: a named button.</summary>
    public Task<PluginOutcome<PluginReply>> ActionAsync(string name, JsonObject parameters, CancellationToken ct = default)
        => ReplyAsync("action", new JsonObject { ["name"] = name, ["params"] = parameters.DeepClone() },
                      _options.Deadlines.Action ?? Seconds(_manifest.Timeouts.Action) ?? TimeSpan.FromMinutes(10), ct);

    /// <summary><c>commit</c>: asks for the documents. Every file named is resolved inside the staging folder, and
    /// one outside it refuses the answer by name.</summary>
    public async Task<PluginOutcome<PluginPlaced>> CommitAsync(JsonObject parameters, CancellationToken ct = default)
    {
        var answer = await RequestAsync("commit", new JsonObject { ["params"] = parameters.DeepClone() }, "placed",
                                        _options.Deadlines.Commit ?? Seconds(_manifest.Timeouts.Commit) ?? TimeSpan.FromMinutes(10), ct);
        if (answer.Failure is { } f) return PluginOutcome<PluginPlaced>.Fail(f);
        return ParsePlaced(answer.Message!);
    }

    /// <summary>A <c>placed</c> message's cells, every file resolved inside the staging folder — a commit's answer or a
    /// <c>window</c> plugin's own.</summary>
    private PluginOutcome<PluginPlaced> ParsePlaced(JsonObject msg)
    {
        var cells = new List<PluginPlacedCell>();
        if (msg["cells"] is JsonArray array)
            foreach (var c in array)
            {
                if (c is not JsonObject cell) return PluginOutcome<PluginPlaced>.Fail(Soft(PluginFailureKind.Protocol, $"{Peer}'s placed lists a cell that is not an object."));
                string? name = cell["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) && n.Trim().Length > 0 ? n.Trim() : null;
                var files = new List<string>();
                if (cell["files"] is JsonArray fa)
                    foreach (var fnode in fa)
                    {
                        if (fnode is not JsonValue fv || !fv.TryGetValue<string>(out var named))
                            return PluginOutcome<PluginPlaced>.Fail(Soft(PluginFailureKind.Protocol, $"{Peer}'s placed lists a file that is not a path."));
                        var (full, refusal) = PluginStaging.Resolve(StagingDirectory, named);
                        if (refusal is not null) return PluginOutcome<PluginPlaced>.Fail(Soft(PluginFailureKind.OutsideStaging, Capital(refusal)));
                        if (!File.Exists(full)) return PluginOutcome<PluginPlaced>.Fail(Soft(PluginFailureKind.Protocol, $"{Peer} named {named}, which is not in its staging folder."));
                        files.Add(full!);
                    }
                string? layoutView = cell["layout"] is JsonValue lv && lv.TryGetValue<string>(out var ln) && ln.Length > 0 ? ln : null;
                cells.Add(new PluginPlacedCell(name, files) { LayoutView = layoutView });
            }
        return PluginOutcome<PluginPlaced>.Of(new PluginPlaced(cells, Messages(msg)));
    }

    private async Task<PluginOutcome<PluginReply>> ReplyAsync(string type, JsonObject body, TimeSpan deadline, CancellationToken ct)
    {
        var answer = await RequestAsync(type, body, "reply", deadline, ct);
        if (answer.Failure is { } f) return PluginOutcome<PluginReply>.Fail(f);
        var msg = answer.Message!;
        var views = msg["views"] as JsonObject ?? new JsonObject();

        // A view a later brief draws from a file names it as "path"/"file": checked now, so no brief can forget to.
        foreach (string named in NamedPaths(views))
            if (PluginStaging.Resolve(StagingDirectory, named).Refusal is { } refusal)
                return PluginOutcome<PluginReply>.Fail(Soft(PluginFailureKind.OutsideStaging, Capital(refusal)));

        return PluginOutcome<PluginReply>.Of(new PluginReply(
            (msg["readouts"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject(),
            views.DeepClone().AsObject(),
            Messages(msg),
            (msg["enabled"] as JsonObject)?.DeepClone().AsObject() ?? new JsonObject(),
            (msg["set"] as JsonObject)?.DeepClone().AsObject())
        {
            Payload = answer.Payload,
        });
    }

    private async Task<Answer> RequestAsync(string type, JsonObject body, string expect, TimeSpan deadline, CancellationToken ct)
    {
        long id;
        var pending = new Pending(type, expect, new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously), DateTime.UtcNow);
        lock (_gate)
        {
            if (_fatal is { } dead) return new Answer(null, dead);
            id = ++_nextId;
            _pending[id] = pending;
        }
        body["type"] = type;
        body["id"] = id;

        var work = Task.Run(async () =>
        {
            if (await WriteAsync(body) is { } failed) return new Answer(null, failed);
            return await pending.Done.Task;
        });

        var timer = Task.Delay(deadline, ct).ContinueWith(_ => { }, TaskScheduler.Default);
        var winner = await Task.WhenAny(work, timer);
        if (winner == work) return await work;

        if (ct.IsCancellationRequested)
        {
            CancellationTokenSource? hostCall;
            bool waiting;
            lock (_gate)
            {
                // Only a request still waiting is marked: one whose answer arrived a moment ago has nothing left to drop,
                // and marking it would leave SettledAsync waiting for an answer that never comes (a needless restart).
                waiting = _pending.Remove(id);
                if (waiting) _cancelled.Add(id);
                _hostCalls.TryGetValue(id, out hostCall);
            }
            try { hostCall?.Cancel(); } catch (ObjectDisposedException) { }   // a host call cancelled with its request
            if (waiting) _ = WriteAsync(new JsonObject { ["type"] = "cancel", ["id"] = id });
            return new Answer(null, Soft(PluginFailureKind.Cancelled, "Stopped."));
        }

        // Killing waits for the tree to go; never on the caller's thread.
        var timeout = await Task.Run(() => Fail(PluginFailureKind.Timeout,
            $"{Peer} did not answer {type} within {Describe(deadline)}, so it was stopped."));
        return new Answer(null, timeout);
    }

    /// <summary>Writes one frame on a pool thread — a write to a pipe the plugin is not reading can block, and the
    /// caller may be the UI thread.</summary>
    private Task<PluginFailure?> WriteAsync(JsonObject message) => Task.Run(() => WriteCoreAsync(message));

    private async Task<PluginFailure?> WriteCoreAsync(JsonObject message)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (Fatal is { } dead) return dead;
            WireFrames.Write(_stdin, new WireFrame(message.ToJsonString()));
            return null;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A closed pipe is the process having gone. Give the reader its moment to report how it ended.
            try { await _process.WaitForExitAsync(new CancellationTokenSource(1000).Token); } catch (OperationCanceledException) { }
            return Fatal ?? Fail(PluginFailureKind.Exited, ExitedSentence());
        }
        finally { _writeLock.Release(); }
    }

    // ── The reader ──────────────────────────────────────────────────────────────

    private void ReadLoop()
    {
        while (true)
        {
            WireFrame frame;
            _stdout.Mark();
            try { frame = WireFrames.Read(_stdout, Peer); }
            catch (WireFrameException e) when (e.EndOfStream)
            {
                // The parameterless wait is the one that lets stderr's reader reach its end, so the last words are in
                // the ring before the sentence quotes them — bounded, because a grandchild can hold the pipe open.
                try { if (_process.WaitForExit(2000)) Task.Run(() => _process.WaitForExit()).Wait(2000); } catch { }
                bool waiting;
                lock (_gate) waiting = _pending.Count > 0;
                // A window plugin that closes with nothing asked of it has simply been closed by its user.
                Fail(PluginFailureKind.Exited, _manifest.Kind == PluginKind.Window && !waiting
                    ? $"{_manifest.Name} closed{(ExitCode is { } c && c != 0 ? $" (exit code {c})" : "")}."
                    : ExitedSentence());
                return;
            }
            catch (WireFrameException)
            {
                Fail(PluginFailureKind.Protocol,
                    $"{Peer} wrote something on its standard output that is not a message: \"{QuoteMore()}\". " +
                    "A plugin writes only frames there; its own log goes to standard error.");
                return;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                Fail(PluginFailureKind.Exited, ExitedSentence());
                return;
            }

            JsonObject? msg;
            try { msg = JsonNode.Parse(frame.Json) as JsonObject; }
            catch (JsonException) { msg = null; }
            if (msg is null)
            {
                Fail(PluginFailureKind.Protocol, $"{Peer} sent a frame that is not a JSON object: \"{Quote(frame.Json)}\".");
                return;
            }
            if (!Dispatch(msg, frame.Payload)) return;
        }
    }

    /// <summary>Routes one message. False when it broke the protocol, which ends the session.</summary>
    private bool Dispatch(JsonObject msg, ReadOnlyMemory<long> payload)
    {
        string? type = msg["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
        long? id = IdOf(msg["id"]);

        switch (type)
        {
            case "progress":
                if (id is { } pid)
                {
                    double? fraction = msg["fraction"] is JsonValue fv && fv.TryGetValue<double>(out var fr) ? fr : null;
                    string? text = msg["text"] is JsonValue xv && xv.TryGetValue<string>(out var tx) ? tx : null;
                    try { Progress?.Invoke(new PluginProgress(pid, fraction, text)); } catch { }
                }
                return true;

            case "host":
                // Answered off the reader thread, one at a time (brief-plg-4 §1), so a simulate never holds up a frame.
                Track(() => AnswerHostAsync(msg, id));
                return true;

            case "open" when _manifest.Kind == PluginKind.Window:
                Track(() => AnswerOpenAsync(msg));
                return true;

            case "placed" when _manifest.Kind == PluginKind.Window && !IsAwaited(id):
                // A window plugin places when it chooses, under an id of its own (brief-plg-4 §3).
                Track(() => AnswerPlacedAsync(msg));
                return true;

            case "hello" or "reply" or "placed":
                Pending? pending = null;
                lock (_gate)
                {
                    if (type == "hello")
                    {
                        var match = _pending.FirstOrDefault(p => p.Value.Expect == "hello");
                        if (match.Value is not null) { pending = match.Value; _pending.Remove(match.Key); }
                    }
                    else if (id is { } rid)
                    {
                        if (_pending.Remove(rid, out var p)) pending = p;
                        else if (_cancelled.Remove(rid)) return true;   // a superseded answer: dropped, as §6.2 says
                    }
                }
                if (pending is null)
                {
                    Fail(PluginFailureKind.Protocol, id is null
                        ? $"{Peer} sent a {type} with no request id: \"{Quote(msg.ToJsonString())}\"."
                        : $"{Peer} answered request {id}, which it was never sent (or has already answered).");
                    return false;
                }
                if (pending.Expect != type)
                {
                    var f = Fail(PluginFailureKind.Protocol, $"{Peer} answered with a {type} where a {pending.Expect} was expected.");
                    pending.Done.TrySetResult(new Answer(null, f));
                    return false;
                }
                pending.Done.TrySetResult(new Answer(msg, null, payload));
                return true;

            default:
                Fail(PluginFailureKind.Protocol, type is null
                    ? $"{Peer} sent a message with no type: \"{Quote(msg.ToJsonString())}\"."
                    : $"{Peer} sent a message of type '{type}', which this circuitRF does not know.");
                return false;
        }
    }

    // ── Host calls and window messages (brief-plg-4) ────────────────────────────

    private void Track(Func<Task> work)
    {
        var task = Task.Run(work);
        lock (_windowWork) { _windowWork.RemoveAll(t => t.IsCompleted); _windowWork.Add(task); }
    }

    /// <summary>Completes once every host call, placement and open already received has been answered — what a
    /// window plugin's staging folder waits for before it is deleted.</summary>
    public Task IdleAsync()
    {
        lock (_windowWork) return Task.WhenAll(_windowWork.ToArray());
    }

    private bool IsAwaited(long? id)
    {
        lock (_gate) return id is { } r && (_pending.ContainsKey(r) || _cancelled.Contains(r));
    }

    /// <summary>
    /// One host call: answered by <see cref="PluginHostServices"/>, one at a time, and cancelled with the request it
    /// arrived inside. A panel's busy bar says circuitRF is simulating while a <c>simulate</c> runs. Every refusal is a
    /// <c>hostReply</c> error with a sentence; the session lives on.
    /// </summary>
    private async Task AnswerHostAsync(JsonObject msg, long? id)
    {
        await _hostLock.WaitAsync();
        try
        {
            if (Fatal is not null) return;
            Pending? request = null;
            bool cancelled;
            lock (_gate)
            {
                if (id is { } r) _pending.TryGetValue(r, out request);
                cancelled = id is { } c && _cancelled.Contains(c);
            }

            JsonObject body;
            if (cancelled)
                body = new JsonObject { ["error"] = $"Request {id} was cancelled, so its host call was not made." };
            else if (request is null && _manifest.Kind != PluginKind.Window)
                body = new JsonObject { ["error"] = id is null
                    ? "A host call carries the id of the request it is made for."
                    : $"A host call is made before the reply to a request, and request {id} is not waiting for one." };
            else
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                if (id is { } key) lock (_gate) _hostCalls[key] = cts;
                bool simulating = msg["call"] is JsonValue cv && cv.TryGetValue<string>(out var call) && call == "simulate";
                if (simulating && id is { } pid) RaiseProgress(new PluginProgress(pid, null, PluginHostServices.SimulatingText));
                try
                {
                    body = await Task.Run(() => _host.Answer(msg, request is null ? null : Budget(request), cts.Token));
                }
                finally
                {
                    if (id is { } done) lock (_gate) if (_hostCalls.TryGetValue(done, out var mine) && ReferenceEquals(mine, cts)) _hostCalls.Remove(done);
                    if (simulating && id is { } pid2) RaiseProgress(new PluginProgress(pid2, null, null));
                }
            }
            body["type"] = "hostReply";
            body["id"] = msg["id"]?.DeepClone();
            await WriteAsync(body);
        }
        finally { _hostLock.Release(); }
    }

    /// <summary>What remains of nine tenths of <paramref name="request"/>'s deadline — or the override.</summary>
    private TimeSpan Budget(Pending request)
    {
        if (_options.Deadlines.Simulate is { } fixedBudget) return fixedBudget;
        TimeSpan deadline = request.Type switch
        {
            "action" => Seconds(_manifest.Timeouts.Action) ?? TimeSpan.FromMinutes(10),
            "commit" => Seconds(_manifest.Timeouts.Commit) ?? TimeSpan.FromMinutes(10),
            _        => Seconds(_manifest.Timeouts.Update) ?? TimeSpan.FromSeconds(30),
        };
        var left = deadline * 0.9 - (DateTime.UtcNow - request.Sent);
        return left > TimeSpan.FromMilliseconds(1) ? left : TimeSpan.FromMilliseconds(1);
    }

    private void RaiseProgress(PluginProgress p)
    {
        try { Progress?.Invoke(p); } catch { /* a listener must not stop a host call */ }
    }

    /// <summary>A window plugin's <c>placed</c>: its files checked as a commit's are, then placed by the caller.</summary>
    private async Task AnswerPlacedAsync(JsonObject msg)
    {
        var reply = new JsonObject { ["type"] = "placedReply", ["id"] = msg["id"]?.DeepClone() };
        var parsed = ParsePlaced(msg);
        if (parsed.Failure is { } f) reply["error"] = f.Sentence;
        else if (_options.WindowPlaced is not { } place) reply["error"] = "This circuitRF places nothing from a plugin's own window here.";
        else
        {
            PluginWindowAnswer answer;
            try { answer = await place(parsed.Value!); }
            catch (Exception e) when (e is not OutOfMemoryException) { answer = new PluginWindowAnswer([], e.Message); }
            if (answer.Error is { } why) reply["error"] = why;
            else reply["cells"] = new JsonArray([.. answer.Cells.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        }
        await WriteAsync(reply);
    }

    /// <summary>A window plugin's <c>open {path}</c>: a document in its staging folder or the workspace, opened by the
    /// caller.</summary>
    private async Task AnswerOpenAsync(JsonObject msg)
    {
        var reply = new JsonObject { ["type"] = "openReply", ["id"] = msg["id"]?.DeepClone() };
        string? named = msg["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : null;
        string? path = named is null ? null : _host.Document(named);
        if (path is null) reply["error"] = named is null ? "open names its document as path." : $"{named} is not a document in the workspace or the staging folder.";
        else if (_options.WindowOpen is not { } open) reply["error"] = "This circuitRF opens nothing for a plugin here.";
        else
        {
            string? refusal;
            try { refusal = await open(path); }
            catch (Exception e) when (e is not OutOfMemoryException) { refusal = e.Message; }
            if (refusal is not null) reply["error"] = refusal;
        }
        await WriteAsync(reply);
    }

    /// <summary>The process's exit code once it has exited, else null.</summary>
    public int? ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : null; } catch (InvalidOperationException) { return null; } }
    }

    // ── Ending ──────────────────────────────────────────────────────────────────

    /// <summary>Records <paramref name="kind"/> as the end of the session — the first one wins — answers every
    /// waiting request with it, and kills the process tree.</summary>
    private PluginFailure Fail(PluginFailureKind kind, string sentence)
    {
        Pending[] waiting;
        PluginFailure failure;
        lock (_gate)
        {
            if (_fatal is { } already) return already;
            _fatal = failure = new PluginFailure(kind, sentence, StderrTail);
            waiting = [.. _pending.Values];
            _pending.Clear();
        }
        // Killed BEFORE anyone is answered: a caller told "it failed" may rightly assume nothing is left running.
        KillTree();
        try { _life.Cancel(); } catch (ObjectDisposedException) { }   // and no host call it asked for runs on
        foreach (var p in waiting) p.Done.TrySetResult(new Answer(null, failure));
        if (Volatile.Read(ref _closed) == 0)
            try { Ended?.Invoke(failure); } catch { /* a listener must not stop the session ending */ }
        return failure;
    }

    /// <summary>A failure of one request that leaves the session usable.</summary>
    private PluginFailure Soft(PluginFailureKind kind, string sentence) => new(kind, sentence, StderrTail);

    /// <summary>
    /// True once every request cancelled so far has been answered (and its answer dropped), or the session has ended;
    /// false if <paramref name="within"/> passes first — a plugin still busy with work it was told to stop, which is
    /// what the panel's Stop restarts (brief-plg-2 §2).
    /// </summary>
    public async Task<bool> SettledAsync(TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (true)
        {
            lock (_gate) if (_fatal is not null || _cancelled.Count == 0) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// <c>bye</c>: the process gets 2 s to exit, then is killed with everything it started. The staging folder goes
    /// unless the session was asked to keep it. Safe to call twice and after a failure.
    /// </summary>
    public async Task ByeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try
        {
            if (Fatal is null && !_process.HasExited)
            {
                await WriteAsync(new JsonObject { ["type"] = "bye" });
                try { await _process.WaitForExitAsync(new CancellationTokenSource(2000).Token); } catch (OperationCanceledException) { }
            }
        }
        catch (InvalidOperationException) { }
        lock (_gate) _fatal ??= new PluginFailure(PluginFailureKind.Exited, $"{Peer} was closed.", []);
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        await Task.Run(KillTree);
        lock (Live) Live.Remove(this);
        if (!_options.KeepStaging) PluginStaging.Delete(StagingDirectory);
        _process.Dispose();
    }

    public ValueTask DisposeAsync() => new(ByeAsync());

    /// <summary>True once the process is gone — what a test asserts after every failure.</summary>
    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    private void KillTree()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { _process.WaitForExit(2000); } catch { }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private string ExitedSentence()
    {
        string code = "";
        try { if (_process.HasExited) code = $" (exit code {_process.ExitCode})"; } catch { }
        string? last;
        lock (_stderr) last = _stderr.Last?.Value;
        return $"{Peer} exited{code} before it answered." + (string.IsNullOrWhiteSpace(last) ? "" : $" Its last line of output: {last}");
    }

    /// <summary>What was read since the frame began, plus whatever follows within a moment — the bytes a protocol
    /// error quotes, at most 200 of them.</summary>
    private string QuoteMore()
    {
        var more = Task.Run(() =>
        {
            var buf = new byte[QuoteBytes];
            try { int n = _stdout.Inner.Read(buf, 0, Math.Max(0, QuoteBytes - _stdout.Recorded.Length)); return buf[..Math.Max(0, n)]; }
            catch { return []; }
        });
        byte[] extra = more.Wait(300) ? more.Result : [];
        byte[] all = [.. _stdout.Recorded, .. extra];
        return Quote(Encoding.UTF8.GetString(all[..Math.Min(all.Length, QuoteBytes)]));
    }

    private static string Quote(string s)
    {
        var b = new StringBuilder();
        foreach (char c in s.Length > QuoteBytes ? s[..QuoteBytes] : s)
            b.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when char.IsControl(c) => "�", _ => c.ToString() });
        return s.Length > QuoteBytes ? b + "…" : b.ToString();
    }

    private static long? IdOf(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out long l)) return l;
        if (v.TryGetValue<int>(out int i)) return i;
        if (v.TryGetValue<double>(out double d) && d == Math.Floor(d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out long p)) return p;
        return null;
    }

    private static IReadOnlyList<PluginMessage> Messages(JsonObject msg)
    {
        var list = new List<PluginMessage>();
        if (msg["messages"] is JsonArray a)
            foreach (var m in a)
                if (m is JsonObject o && o["text"] is JsonValue tv && tv.TryGetValue<string>(out var text))
                {
                    string sev = o["severity"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.ToLowerInvariant() : "info";
                    list.Add(new PluginMessage(sev is "error" or "warning" ? sev : "info", text));
                }
        return list;
    }

    private static IEnumerable<string> NamedPaths(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                {
                    if (key is "path" or "file" or "cnl" && value is JsonValue v && v.TryGetValue<string>(out var s)) yield return s;
                    foreach (var inner in NamedPaths(value)) yield return inner;
                }
                break;
            case JsonArray a:
                foreach (var item in a)
                    foreach (var inner in NamedPaths(item)) yield return inner;
                break;
        }
    }

    private static TimeSpan? Seconds(double? s) => s is { } v ? TimeSpan.FromSeconds(v) : null;

    private static string Describe(TimeSpan t)
        => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min" : t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.#} s" : $"{t.TotalMilliseconds:0} ms";

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string CircuitRfVersion
    {
        get
        {
            string v = typeof(PluginSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }

    /// <summary>A read-through stream that keeps the bytes read since <see cref="Mark"/>, up to a limit — so a
    /// protocol error can quote what the plugin actually wrote.</summary>
    private sealed class RecordingStream(Stream inner, int limit) : Stream
    {
        private readonly MemoryStream _seen = new();
        public Stream Inner => inner;
        public byte[] Recorded => _seen.ToArray();
        public void Mark() => _seen.SetLength(0);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            if (n > 0 && _seen.Length < limit) _seen.Write(buffer[..(int)Math.Min(n, limit - _seen.Length)]);
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
