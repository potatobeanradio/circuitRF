// Background work (brief-modelcomposer-14 R-mc14-3; overview D13).
//
// One task at a time per document. Every task runs a src/Design/Modeling function on a worker with a RunControl; the UI
// thread does only marshalling and drawing. Progress is COALESCED: a report stores the latest progress and posts one UI
// work item only when none is already queued, so a fit reporting thousands of times costs the UI thread one item per
// frame it can actually show — UiWorkItems ≤ ProgressReports, held by a counter rather than a timing.
//
// Two kinds of task. A REFRESH (opening the data, de-embedding after an edit) reads the in-memory document and writes
// nothing; a run the user asks for pre-empts it. A FILE RUN (import, fit) writes the document first, runs the file-based
// command, then reads the result back and replays onto it any edit made while it ran. Cancelled or refused, a file run's
// command leaves the file as it was, so the previous result stands untouched.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>Runs an action on the UI thread. Inline by default, which is what lets a test drive the view model.</summary>
    public Action<Action> PostToUi { get; set; } = a => a();

    /// <summary>Runs a task's work off the UI thread.</summary>
    public Func<Func<object?>, CancellationToken, Task<object?>> RunOffThread { get; set; } = (w, t) => Task.Run(w, t);

    /// <summary>What the last open or run loaded: the data, the intrinsic data, the flags and the stale steps.</summary>
    public ModelData Loaded { get; private set; } = ModelData.Empty;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _taskStage = "";
    /// <summary>The progress bar's fraction, 0…1.</summary>
    [ObservableProperty] private double _progress;
    /// <summary>False while a stage reports no total — the bar is indeterminate.</summary>
    [ObservableProperty] private bool _progressKnown;

    /// <summary>True while a file run (an import, a fit) is writing the document.</summary>
    public bool IsFileRunning { get; private set; }

    /// <summary>The task in flight, or null — what a test awaits.</summary>
    internal Task? CurrentTask { get; private set; }

    private long _progressReports, _uiWorkItems;

    /// <summary>Progress reports the tasks made.</summary>
    public long ProgressReports => Interlocked.Read(ref _progressReports);

    /// <summary>UI-thread work items the progress posted — never more than the reports, and far fewer under load.</summary>
    public long UiWorkItems => Interlocked.Read(ref _uiWorkItems);

    private CancellationTokenSource? _cts;
    private bool _isRefresh;
    private bool _refreshPending;
    private readonly List<CmcEdit> _editsDuringRun = [];

    /// <summary>True from Cancel until the run stops: the run ends at its next check, which can be a while — a report stops
    /// between PA classes — so Cancel is disabled and says so meanwhile (owner, review 4: it seemed to do nothing).</summary>
    [ObservableProperty] private bool _isStopping;

    /// <summary>What the Cancel buttons say.</summary>
    public string CancelText => IsStopping ? "Stopping…" : "Cancel";

    partial void OnIsRunningChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    partial void OnIsStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(CancelText));
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel()) return;
        IsStopping = true;
        CancelRunningTask();
    }

    private bool CanCancel() => IsRunning && !IsStopping;

    private void CancelRunningTask()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Runs <paramref name="work"/> off the UI thread and hands its result to <paramref name="done"/> on it. A second task
    /// while one runs is a one-line refusal — unless the running one is a refresh, which gives way. Returns false when
    /// refused, cancelled or failed.
    /// </summary>
    private Task<bool> StartTask(string stage, bool refresh, bool writesFile, Func<RunControl, object?> work, Action<object?> done)
    {
        // CurrentTask is the WHOLE task — its result handed over and its state cleared — not only the worker's part: a run
        // that waited on the worker alone could start while the previous task was still clearing up behind it.
        var whole = RunTask(stage, refresh, writesFile, work, done);
        if (!whole.IsCompleted) CurrentTask = whole;
        return whole;
    }

    private async Task<bool> RunTask(string stage, bool refresh, bool writesFile, Func<RunControl, object?> work, Action<object?> done)
    {
        if (IsRunning)
        {
            if (!_isRefresh || refresh)
            {
                if (refresh) { _refreshPending = true; return false; }
                Warn($"{TaskStage} is running; cancel it or wait for it to finish.");
                return false;
            }
            CancelRunningTask();
            try { if (CurrentTask is { } t) await t; } catch { }
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _isRefresh = refresh;
        IsFileRunning = writesFile;
        _editsDuringRun.Clear();
        IsRunning = true;
        TaskStage = stage;
        Progress = 0;
        ProgressKnown = false;
        RunCommand.NotifyCanExecuteChanged();

        var rc = new RunControl { Token = cts.Token, Progress = new Coalescer(this) };
        var task = RunOffThread(() => work(rc), cts.Token);
        bool ok = false;
        try
        {
            var result = await task;
            done(result);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            if (!refresh) Info($"{stage}: cancelled; the previous result is kept.");
        }
        catch (Exception ex)
        {
            Error(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsStopping = false;
            IsRunning = false;
            IsFileRunning = false;
            _isRefresh = false;
            TaskStage = "";
            Progress = 0;
            RunCommand.NotifyCanExecuteChanged();
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            BeginLoad();
        }
        return ok;
    }

    /// <summary>The worker's progress, latest only, one UI work item at a time.</summary>
    private sealed class Coalescer(ModelComposerViewModel vm) : IProgress<RunProgress>
    {
        private RunProgress? _latest;
        private int _posted;

        public void Report(RunProgress p)
        {
            Interlocked.Increment(ref vm._progressReports);
            Volatile.Write(ref _latest, p);
            if (Interlocked.Exchange(ref _posted, 1) != 0) return;
            Interlocked.Increment(ref vm._uiWorkItems);
            vm.PostToUi(Drain);
        }

        private void Drain()
        {
            Volatile.Write(ref _posted, 0);
            if (Volatile.Read(ref _latest) is not { } p || !vm.IsRunning) return;
            vm.TaskStage = p.Stage;
            if (p.StageTotal > 0) { vm.ProgressKnown = true; vm.Progress = Math.Clamp((double)p.StageCompleted / p.StageTotal, 0, 1); }
            else if (p.Total > 0) { vm.ProgressKnown = true; vm.Progress = Math.Clamp((double)p.Completed / p.Total, 0, 1); }
            else vm.ProgressKnown = false;
        }
    }

    // ── refresh: the data, opened through the cache ──────────────────────────

    /// <summary>
    /// Opens the document's data and de-embeds it, off the UI thread, from the document in memory — nothing is written.
    /// A document with no file yet has no data to open.
    /// </summary>
    public void BeginLoad()
    {
        if (DocumentPath is not { } path || IsChoosingKind) return;
        var doc = Document;
        _loadedKey = DataKey(doc);
        _ = StartTask("Opening data", refresh: true, writesFile: false,
            rc => ModelCommands.Load(path, doc, rc),
            r =>
            {
                Loaded = (ModelData)r!;
                SayLoaded();
                RefreshDocumentViews();
            });
    }

    /// <summary>The open's own line: a re-import or a warning, else nothing to say. The first open of a document says what
    /// it holds — its data, the steps already done, the exported model — so a finished .cmc reads as finished.</summary>
    private void SayLoaded()
    {
        string? opened = _saidOpened ? null : OpenedSummary();
        _saidOpened = true;
        // Opening, the summary leads and the data's first remark follows it as a note: it describes the data, which the
        // opened document already accounts for, and a warning-coloured line would read as the open having failed.
        if (opened is not null) Info(Loaded.Warnings.Count > 0 ? $"{opened} Note: {Loaded.Warnings[0]}" : opened);
        else if (Loaded.Warnings.Count > 0) Warn(Loaded.Warnings[0]);
        else if (Loaded.Status.Count > 0) Info(Loaded.Status[^1]);
    }

    private bool _saidOpened;

    /// <summary>"9 data sets open · done: Network, Current, … · gan_4x125.cmodel is exported; Run re-runs a step." — the
    /// rail's own marks, as one line.</summary>
    private string OpenedSummary()
    {
        var done = Steps.Where(s => s.IsDone && s.Name != "Data").Select(s => s.Name).ToList();
        var stale = Steps.Where(s => s.IsStale).Select(s => s.Name).ToList();
        string data = $"{Loaded.Data.Count} data set{(Loaded.Data.Count == 1 ? "" : "s")} open";
        if (Loaded.Data.Count == 0) return Document.DataSets.Count == 0 ? "No data yet: Import… on the Data step brings in the first set." : $"{data}.";
        string line = done.Count > 0 ? $"{data} · done: {string.Join(", ", done)}" : $"{data}; no step is fitted yet";
        if (stale.Count > 0) line += $" · stale: {string.Join(", ", stale)}";
        return line + ".";
    }

    /// <summary>After an edit: a change the data or its de-embedding depends on re-opens it.</summary>
    private void AfterEdit()
    {
        if (DocumentPath is null) return;
        string key = DataKey(Document);
        if (key == _loadedKey) return;
        _loadedKey = key;
        BeginLoad();
    }

    private string? _loadedKey;

    private static string DataKey(CmcDocument d) =>
        string.Join("\u0001", d.DataSets.Select(s => $"{s.Name}|{s.Sources.Count}|{s.Exclusions.Count}|{string.Join(",", s.Exclusions.Select(x => x.Point))}|{s.Conditions}"))
        + "\u0002" + (d.Pads?.ToJsonString() ?? "") + "\u0002" + NetworkKey(d);

    private static string NetworkKey(CmcDocument d)
    {
        try
        {
            var n = CmcExtrinsic.Network(d);
            return n.Schematic + "|" + string.Join(",", n.Values.Select(kv => $"{kv.Key}={kv.Value:R}")) + "|" + n.FlatnessBand;
        }
        catch (Exception) { return d.Network?.ToJsonString() ?? ""; }
    }

    // ── file runs ────────────────────────────────────────────────────────────

    /// <summary>A document with no file is saved as first; a dirty one is written. False when that was refused or cancelled.</summary>
    private async Task<bool> EnsureWritten()
    {
        if (DocumentPath is null || IsDirty) return await SaveAsync(saveAs: false);
        return true;
    }

    /// <summary>
    /// Runs a file-based command (an import, a fit): writes the document, runs <paramref name="work"/> on the file, reads the
    /// result back with the data re-opened, and replays the edits made meanwhile. The command's lines become the status.
    /// </summary>
    private async Task<bool> RunOnFile(string stage, Func<string, RunControl, IReadOnlyList<ModelLine>> work)
    {
        if (IsRunning && !_isRefresh)
        {
            Warn($"{TaskStage} is running; cancel it or wait for it to finish.");
            return false;
        }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        return await StartTask(stage, refresh: false, writesFile: true, rc =>
        {
            var lines = work(path, rc);
            var after = CmcFile.Read(path);
            // The result is on disk now: the data is re-opened without the token, so a late Cancel cannot leave the window
            // holding the old document over a file that has the new one.
            var data = ModelCommands.Load(path, after, new RunControl { Progress = rc.Progress });
            return (lines, after, data);
        }, r =>
        {
            var (lines, after, data) = ((IReadOnlyList<ModelLine>, CmcDocument, ModelData))r!;
            AdoptRunResult(after, data);
            var shown = lines.Where(l => l.Kind != "note").ToList();
            if (data.Warnings.Count > 0) Warn(data.Warnings[0]);
            else if (shown.Count > 0) Info(shown[^1].Text);
            else Info($"{stage}: done.");
        });
    }

    /// <summary>The run's document, with the window's view state and every edit made while it ran put back on top.</summary>
    private void AdoptRunResult(CmcDocument after, ModelData data)
    {
        var doc = after with { Panels = Document.Panels, View = Document.View };
        foreach (var e in _editsDuringRun)
        {
            try { doc = e.Redo(doc); }
            catch (Exception ex) when (ex is CmcFormatException or ModelingException or ArgumentException) { }
        }
        bool replayed = _editsDuringRun.Count > 0;
        _editsDuringRun.Clear();
        Loaded = data;
        Document = doc;
        _loadedKey = DataKey(doc);
        IsDirty = replayed;
        RefreshDocumentViews();
        if (replayed) AfterEdit();
    }

    // ── Run ▾ (R-mc14-2): the selected step's runs ───────────────────────────

    /// <summary>One entry of the Run menu: its label and what it runs.</summary>
    public sealed record RunItem(string Label, string Key);

    /// <summary>The selected step's runs, the first of which the Run button itself runs.</summary>
    public IReadOnlyList<RunItem> RunItems => StepName switch
    {
        // A document that holds data opens it again; one that holds none has nothing to run but an import.
        "Data" => Document.DataSets.Count > 0 ? [new("Reload the data", "reload"), new("Import…", "import")] : [new("Import…", "import"), new("Reload", "reload")],
        "Pads" => [new("De-embed", "deembed")],
        "Network" => [new("Fit", "fit"), new("De-embed", "deembed")],
        "Traps" => TrapRunItems(),
        "Current" or "Charge" or "Fit" => [new("Fit", "fit")],
        "Thermal" => [new("Extract", "fit")],
        "Noise" => [new("Fit", "fit")],
        "Check" => [new("Check charge conservation", "check")],
        "Validate" => [new("Validate all", "validate"), new("Show testbench", "testbench")],
        "Export" => [new("Export", "export"), new("Run stale", "stale")],
        "Topology" => [new("Propose", "propose"), new("Analyse", "analyse")],
        _ => [],
    };

    /// <summary>
    /// The Traps step's run. With no trap model there is nothing to fit — unless the currents were trained with one, which
    /// they would go on carrying into the export, so the run re-trains them without.
    /// </summary>
    private IReadOnlyList<RunItem> TrapRunItems()
    {
        try
        {
            if (TrapFit.Settings(Document).Modelled) return [new("Fit", "fit")];
            return CurrentFit.Record(Document)?.Traps is not null ? [new("Re-train the currents without traps", "fit")] : [];
        }
        catch (CmcFormatException) { return [new("Fit", "fit")]; }
    }

    /// <summary>The Run button's tooltip: what it runs, or that the step has nothing to run.</summary>
    public string RunTip => RunItems.FirstOrDefault() is { } first ? $"Run: {first.Label}" : "Nothing to run in this step";

    /// <summary>The Run button's runs and tooltip follow the step and the document.</summary>
    private void NotifyRunItems()
    {
        OnPropertyChanged(nameof(RunItems));
        OnPropertyChanged(nameof(RunTip));
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task Run(string? key) => RunKey(key ?? RunItems.FirstOrDefault()?.Key);

    private bool CanRun(string? key) => !IsChoosingKind && RunItems.Count > 0;

    /// <summary>Runs <paramref name="key"/> for the selected step. Returns when it finished, failed or was refused.</summary>
    public Task<bool> RunKey(string? key) => key switch
    {
        "import" => ImportAsync(),
        "reload" => Reload(),
        "deembed" => Reload(),
        "fit" => FitAsync(FitStepOf(StepName)),
        "propose" => ProposeAsync(),
        "check" => CheckChargeAsync(),
        "validate" => ValidateAsync(null),
        "testbench" => Run(ShowTestbench),
        "export" => ExportAsync(),
        "stale" => RunStaleAsync(),
        "analyse" => AnalyseAsync(),
        _ => Task.FromResult(false),
    };

    private static Task<bool> Run(Action action)
    {
        action();
        return Task.FromResult(true);
    }

    private static string FitStepOf(string step) => step switch
    {
        "Fit" => "passive",
        _ => step.ToLowerInvariant(),
    };

    /// <summary>Re-opens the data and de-embeds it now, as a run — what Reload and De-embed do.</summary>
    private async Task<bool> Reload()
    {
        if (DocumentPath is not { } path)
        {
            Warn("Save the document first; its data is found relative to it.");
            return false;
        }
        var doc = Document;
        return await StartTask("De-embedding", refresh: false, writesFile: false, rc => ModelCommands.Load(path, doc, rc), r =>
        {
            Loaded = (ModelData)r!;
            _loadedKey = DataKey(doc);
            RefreshDocumentViews();
            if (Loaded.Warnings.Count > 0) Warn(Loaded.Warnings[0]);
            else Info(Loaded.Intrinsic.Count > 0
                ? string.Join("; ", Loaded.Intrinsic.Select(kv => $"{kv.Key}: {ModelCommands.IntrinsicSummary(kv.Value)}"))
                : $"{Loaded.Data.Count} data set(s) open.");
        });
    }

    /// <summary>Fits <paramref name="step"/> through <see cref="ModelCommands.Fit"/>, the function <c>model fit</c> calls.</summary>
    public async Task<bool> FitAsync(string step)
    {
        bool ok = await RunOnFile($"Fitting {step}", (path, rc) => ModelCommands.Fit(path, step, null, rc));
        // R-mc15-1: a current or charge fit ends on its one-line summary, unless the data has a warning to show.
        if (!ok || Loaded.Warnings.Count > 0) return ok;
        try
        {
            if ((step == "current" ? CurrentFit.Summary(Document) : step == "charge" ? ChargeFit.Summary(Document) : null) is { } summary)
                Info(summary);
        }
        catch (CmcFormatException) { }
        return ok;
    }
}
