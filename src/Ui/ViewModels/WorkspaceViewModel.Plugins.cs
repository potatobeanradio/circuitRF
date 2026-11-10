using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

// brief-plg-1 — Tools ▸ Plugins: ask once, open the parameter dialog, run the plugin, place what it staged. Every step
// that is not drawing — the session, the parameters, the placement — is src/Design/Plugins, which the command line
// calls too.
public partial class WorkspaceViewModel
{
    /// <summary>Opens <paramref name="entry"/> from the menu: refuses what cannot run here, asks permission the first
    /// time, then opens a <c>panel</c> plugin's window (brief-plg-2) or an <c>action</c> plugin's parameter dialog.</summary>
    internal async Task OpenPluginAsync(PluginEntry entry)
    {
        if (ResolveOwner(null) is not { } window) return;
        // Re-read, so a plugin edited in a linked folder starts as it now stands.
        entry = PluginCatalog.Read(entry.Directory, entry.Source);
        if (PluginHost.UnavailableReason(entry) is { } unavailable) { Messages.Warning($"{entry.DisplayName} {unavailable}."); return; }

        var policy = PluginHost.Policy;
        if (!policy.Allowed) { Messages.Warning(policy.Reason); return; }

        var trust = PluginHost.Trust();
        if (trust.Decide(entry.Directory) == PluginTrustDecision.Unknown)
        {
            if (await PluginTrustDialog.AskAsync(window, entry) is not { } answer) return;
            trust.Record(entry.Directory, answer);
        }
        if (trust.Decide(entry.Directory) != PluginTrustDecision.Allowed)
        {
            Messages.Warning($"{entry.DisplayName} is not allowed to run on this machine. Settings ▸ Plugins changes that.");
            return;
        }

        string? workspaceDir = CurrentWorkspacePath is { } cws ? Path.GetDirectoryName(cws) : null;
        if (entry.Manifest!.Kind == PluginKind.Panel) { OpenPluginPanel(window, entry, trust, workspaceDir); return; }
        await PluginActionDialog.ShowAsync(window, entry, workspaceDir,
            (values, keep, progress, cancel) => RunPluginAsync(window, entry, trust, workspaceDir, values, keep, progress, cancel));
    }

    private async Task<PluginRunAnswer> RunPluginAsync(
        Window window, PluginEntry entry, PluginTrustStore trust, string? workspaceDir,
        JsonObject values, bool keepStaging, Action<PluginProgress> progress, CancellationToken cancel)
    {
        var manifest = entry.Manifest!;
        using var log = PluginLog.Open(manifest.Id);
        var started = PluginSession.Start(entry, new PluginStartOptions
        {
            Trust = trust, Policy = PluginHost.Policy, WorkspaceDir = workspaceDir, KeepStaging = keepStaging,
            Log = log.Write,
        });
        if (started.Failure is { } refused) return Failed(refused);

        await using var session = started.Value!;
        session.Progress += progress;
        if (keepStaging) Messages.Info($"{manifest.Name} stages its files in {session.StagingDirectory}; they are kept.", session.StagingDirectory);

        var hello = await session.HelloAsync(cancel);
        if (hello.Failure is { } hf) return Failed(hf);

        var reply = await session.UpdateAsync(values, cancel);
        if (reply.Failure is { } uf) return Failed(uf);
        if (reply.Value!.HasError) return new PluginRunAnswer(false, reply.Value.Messages);

        return await PlacePluginAsync(window, entry, workspaceDir, session, values, reply.Value.Messages, cancel);

        PluginRunAnswer Failed(PluginFailure f) => PluginFailedAnswer(manifest, f);
    }

    /// <summary>A failure as the dialog or panel shows it, and posted to Messages.</summary>
    private PluginRunAnswer PluginFailedAnswer(PluginManifest manifest, PluginFailure f)
    {
        if (f.Kind == PluginFailureKind.Cancelled) return new PluginRunAnswer(false, [new PluginMessage("info", "Stopped.")]);
        var lines = new List<PluginMessage> { new("error", f.Sentence) };
        if (f.Kind is PluginFailureKind.Exited or PluginFailureKind.Protocol or PluginFailureKind.Timeout)
            lines.AddRange(f.StderrTail.TakeLast(5).Select(l => new PluginMessage("info", l)));
        Messages.Error($"{manifest.Name}: {f.Sentence}");
        return new PluginRunAnswer(false, lines);
    }

    /// <summary>
    /// <b>Place in workspace</b> — <c>commit</c>, then each staged cell through <see cref="PluginPlacement"/>, asking
    /// Replace or New name when a cell exists. The action dialog's OK and the panel's commit button both come here.
    /// </summary>
    /// <param name="layoutView">A panel's layout view by name, read from the reply it last drew — what a placed cell's
    /// <c>layout</c> names (brief-plg-3 §3). Null for an action plugin, which has no views.</param>
    private async Task<PluginRunAnswer> PlacePluginAsync(
        Window window, PluginEntry entry, string? workspaceDir, IPluginPanelSession session, JsonObject values,
        IReadOnlyList<PluginMessage> replyMessages, CancellationToken cancel, Func<string, PluginLayoutView?>? layoutView = null)
    {
        var manifest = entry.Manifest!;
        if (workspaceDir is null)
            return new PluginRunAnswer(false, [.. replyMessages, new PluginMessage("error", "Open a workspace to place what this plugin makes.")]);

        var placed = await session.CommitAsync(values, cancel);
        if (placed.Failure is { } cf) return Failed(cf);
        if (placed.Value!.HasError) return new PluginRunAnswer(false, placed.Value.Messages);
        if (placed.Value.Cells.Count == 0) return new PluginRunAnswer(false, [.. placed.Value.Messages, new PluginMessage("warning", $"{manifest.Name} placed nothing.")]);

        var names = new List<string>();
        string? last = null;
        foreach (var cell in placed.Value.Cells)
        {
            var request = new PluginPlacementRequest(workspaceDir, manifest, cell, placed.Value.Messages,
                Layout: cell.LayoutView is { } lv ? layoutView?.Invoke(lv) : null);
            var result = await Task.Run(() => PluginPlacement.Place(request));
            while (result.Outcome == PluginPlacementOutcome.NameTaken)
            {
                var (choice, chosen) = await CellExistsDialog.AskAsync(window, workspaceDir, result.CellName, "Place in Workspace",
                    $"Replace writes {manifest.Name}'s files over that cell's views of the same kinds; its other files stay as they are. " +
                    "A restore point is taken first. Or place it under another name:");
                if (choice == CellExistsChoice.Cancel) return new PluginRunAnswer(false, [new PluginMessage("info", "Nothing was placed.")]);
                if (choice == CellExistsChoice.Replace && OpenDocumentIn(Path.Combine(workspaceDir, result.CellName)) is { } open)
                    return new PluginRunAnswer(false, [new PluginMessage("error", $"{Path.GetFileName(open)} is open, so cell {result.CellName} was not replaced. Close it and try again.")]);
                request = request with { Replace = choice == CellExistsChoice.Replace, CellName = choice == CellExistsChoice.NewName ? chosen : result.CellName };
                result = await Task.Run(() => PluginPlacement.Place(request));
            }
            if (result.Outcome == PluginPlacementOutcome.Refused)
                return new PluginRunAnswer(false, [.. result.Refusals.Select(r => new PluginMessage("error", r))]);
            names.Add(result.CellName);
            last = result.CellDir;
        }

        _factory.ProjectTreeTool?.Refresh();
        if (last is not null) _factory.ProjectTreeTool?.Reveal(last);
        foreach (var m in placed.Value.Messages.Where(m => !m.IsError))
            Messages.Post(m.Severity == "warning" ? CircuitRF.Ui.Messages.MessageLevel.Warning : CircuitRF.Ui.Messages.MessageLevel.Info, $"{manifest.Name}: {m.Text}");
        string done = $"{manifest.Name} {manifest.Version} placed {(names.Count == 1 ? "cell" : "cells")} {string.Join(", ", names)}.";
        Messages.Info(done, last);
        return new PluginRunAnswer(true, [new PluginMessage("info", done)]);

        PluginRunAnswer Failed(PluginFailure f) => PluginFailedAnswer(manifest, f);
    }

    /// <summary>The open panel windows, by plugin id — choosing an open plugin again raises its window.</summary>
    private readonly Dictionary<string, PluginWindow> _pluginWindows = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a <c>panel</c> plugin's window (brief-plg-2): one process per open window, started now and restarted after
    /// a crash by the controller. Its deadlines are the panel's, not the process host's — past one the panel says so
    /// and offers Stop rather than killing the plugin — so the session waits indefinitely for an update or action.
    /// </summary>
    private void OpenPluginPanel(Window owner, PluginEntry entry, PluginTrustStore trust, string? workspaceDir)
    {
        var manifest = entry.Manifest!;
        if (_pluginWindows.TryGetValue(manifest.Id, out var open)) { open.Activate(); return; }

        var (layout, diagnostics) = PluginLayoutReader.Read(manifest);
        if (layout is null)
        {
            Messages.Error($"{manifest.Name}'s panel cannot be drawn: {string.Join("; ", diagnostics.Where(d => d.IsError))}");
            return;
        }

        var log = PluginLog.Open(manifest.Id);
        PluginWindow? window = null;
        IReadOnlyDictionary<string, string>? lastSent = null;   // remembered on close: the last values used, not unsent text
        PluginPanelController controller = null!;   // read by the commit below, which runs only after it is assigned
        controller = new PluginPanelController(manifest, () =>
        {
            var started = PluginSession.Start(entry, new PluginStartOptions
            {
                Trust = trust, Policy = PluginHost.Policy, WorkspaceDir = workspaceDir, Log = log.Write,
                Deadlines = new PluginDeadlines(Update: Timeout.InfiniteTimeSpan, Action: Timeout.InfiniteTimeSpan),
            });
            return started.Ok ? PluginOutcome<IPluginPanelSession>.Of(started.Value!) : PluginOutcome<IPluginPanelSession>.Fail(started.Failure!);
        }, new PluginPanelOptions
        {
            Context = new PluginParamContext(workspaceDir),
            Initial = PluginHost.LastParams(manifest.Id, workspaceDir),
            Post = a => Dispatcher.UIThread.Post(a),
            Sent = values => lastSent = values,
            Commit = (session, values, cancel) => Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (window is null) return (IReadOnlyList<PluginMessage>)[];
                var answer = await PlacePluginAsync(window, entry, workspaceDir, session, values, [], cancel, name =>
                    controller.State.ViewBodies.GetValueOrDefault(name) is { } body
                        ? PluginViews.Read(body, "layout", new PluginViewContext(manifest, workspaceDir)) as PluginLayoutView
                          ?? new PluginLayoutView(name, new LayoutView(), null, [], [], body.Params) { Problem = $"The {name} view is not a layout." }
                        : null);
                return answer.Messages;
            }),
        });

        window = new PluginWindow(layout, controller, workspaceDir, PluginHost.WindowMemory(manifest.Id), OpenDocumentByPath)
        {
            Remember = (memory, _) =>
            {
                PluginHost.RecordWindowMemory(manifest.Id, memory);
                if (lastSent is not null) PluginHost.RecordLastParams(manifest.Id, workspaceDir, lastSent);
            },
        };
        _pluginWindows[manifest.Id] = window;
        window.Closed += (_, _) =>
        {
            _pluginWindows.Remove(manifest.Id);
            owner.Closed -= CloseWithOwner;
            // The log outlives the window by the plugin's 2 s to say goodbye.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => log.Dispose(), TaskScheduler.Default);
        };
        owner.Closed += CloseWithOwner;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        controller.Open();

        void CloseWithOwner(object? _, EventArgs __) => window.Close();
    }

    /// <summary>A document open from inside <paramref name="cellDir"/>, or null — Replace would change it under its editor.</summary>
    private string? OpenDocumentIn(string cellDir)
    {
        string prefix = Path.GetFullPath(cellDir) + Path.DirectorySeparatorChar;
        return _openDocsByPath.Keys.FirstOrDefault(p => Path.GetFullPath(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A plugin session's stderr, as its run log: <c>logs/plugin-&lt;id&gt;.log</c>, one session per file, capped
    /// so a plugin that floods stderr cannot fill the disk.</summary>
    private sealed class PluginLog : IDisposable
    {
        private const long Cap = 1024 * 1024;
        private readonly StreamWriter? _writer;
        private long _written;
        private readonly object _gate = new();

        private PluginLog(StreamWriter? writer) => _writer = writer;

        public static PluginLog Open(string id)
        {
            try
            {
                string dir = AppDataRoot.SubDir(CircuitRF.Ui.Messages.MessagesLog.DirName);
                Directory.CreateDirectory(dir);
                return new PluginLog(new StreamWriter(Path.Combine(dir, $"plugin-{id}.log"), append: false) { AutoFlush = true });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new PluginLog(null); }
        }

        public void Write(string line)
        {
            lock (_gate)
            {
                if (_writer is null || _written > Cap) return;
                _written += line.Length + 1;
                try { _writer.WriteLine(_written > Cap ? "… the rest of this session's output was not kept." : line); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
        }

        public void Dispose() { lock (_gate) _writer?.Dispose(); }
    }
}
