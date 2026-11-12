// HZN-8 R-hzn8-2 — Run in Hertzon and Open in Hertzon for a design of a Hertzon project.
//
// Finding hertzon-cli is HertzonDiscovery, the process and its exit-code sentences are HertzonRun, and the result is
// HertzonResults; this file is the window around them: the question, a live row with a Cancel, and the Data Display. Hertzon
// solves the FILE: a design open with unsaved edits is not run, and circuitRF writes nothing into <project>.hznresults.

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    // ── the tree's two items ─────────────────────────────────────────────────────────────────

    /// <summary>Why Run in Hertzon is off, from the last answer (no process is started to build a menu); null when it is on or unasked.</summary>
    public string? RunInHertzonUnavailable => WarmHertzon()?.Refusal;

    /// <summary>Why Open in Hertzon is off: Hertzon is not found, or its application is not beside its command line.</summary>
    public string? OpenInHertzonUnavailable
    {
        get
        {
            var ready = WarmHertzon();
            if (ready is null) return null;
            if (ready.Installation is not { } cli) return ready.Refusal;
            return HertzonDiscovery.ApplicationFor(cli) is null ? "Hertzon's application was not found beside hertzon-cli." : null;
        }
    }

    /// <summary>The last readiness, asking in the background the first time so a menu never waits on a process.</summary>
    private static HertzonReadiness? WarmHertzon()
    {
        if (HertzonDiscovery.Shared.Last is { } last) return last;
        _ = System.Threading.Tasks.Task.Run(() => HertzonDiscovery.Shared.Check());
        return null;
    }

    public void RunInHertzon(ProjectTreeNodeViewModel design)
        => _ = RunInHertzonAsync(design.AbsolutePath, design.Name);

    public void OpenInHertzon(ProjectTreeNodeViewModel project)
        => _ = OpenInHertzonAsync(project.AbsolutePath);

    // ── the editor's Simulate menu ───────────────────────────────────────────────────────────

    private HznDesignSource? ActiveHeldHznDesign
        => _factory.DocumentDock?.ActiveDockable is C3dEditorDocument { ViewModel.Source: HznDesignSource held } ? held : null;

    /// <summary>Simulate ▸ Run in Hertzon: the opened Hertzon design, solved by Hertzon from its file.</summary>
    [RelayCommand(CanExecute = nameof(CanRunInHertzonFromEditor))]
    private Task RunInHertzonFromEditor()
        => ActiveHeldHznDesign is { } held ? RunInHertzonAsync(held.FilePath, held.Design) : System.Threading.Tasks.Task.CompletedTask;

    private bool CanRunInHertzonFromEditor() => ActiveHeldHznDesign is not null;

    // ── open ─────────────────────────────────────────────────────────────────────────────────

    internal async System.Threading.Tasks.Task OpenInHertzonAsync(string hznPath)
    {
        var ready = await System.Threading.Tasks.Task.Run(() => HertzonDiscovery.Shared.Check());
        if (ready.Installation is not { } cli) { Messages.Error(ready.Refusal ?? "Hertzon was not found."); return; }
        string? failed = HertzonDiscovery.Open(cli, hznPath);
        if (failed is not null) Messages.Error(failed);
        else Messages.Info($"Opened '{Path.GetFileName(hznPath)}' in {cli.Label}.", hznPath);
    }

    // ── run ──────────────────────────────────────────────────────────────────────────────────

    private readonly HashSet<string> _hertzonRuns = [];

    /// <summary>Asks the setup, sweep and variables, then runs <paramref name="design"/> of <paramref name="hznPath"/> in Hertzon.</summary>
    /// <param name="answer">A test or a script supplies the answer instead of the dialog.</param>
    internal async System.Threading.Tasks.Task<bool> RunInHertzonAsync(string hznPath, string design, Window? owner = null,
                                                                       HertzonRunAnswer? answer = null)
    {
        string project = Path.GetFullPath(hznPath);
        string runKey = project + "#" + design;
        if (_hertzonRuns.Contains(runKey)) { Messages.Warning($"'{design}' is already running in Hertzon."); return false; }

        var ready = await System.Threading.Tasks.Task.Run(() => HertzonDiscovery.Shared.Check());
        if (!ready.Ready) { Messages.Error(ready.Refusal!); return false; }
        var cli = ready.Installation!;

        if (!await SettleUnsavedHznDesignAsync(project, design, "Run Anyway")) return false;
        if (!File.Exists(project)) { Messages.Error($"'{project}' does not exist."); return false; }

        var described = await System.Threading.Tasks.Task.Run(() => HertzonRun.Describe(cli, project));
        if (!described.Ok) { Messages.Error(described.Error!); return false; }
        if (described.Designs.FirstOrDefault(d => d.Name == design) is not { } chosen)
        {
            Messages.Error($"Hertzon finds no design '{design}' in '{Path.GetFileName(project)}'.");
            return false;
        }
        if (!chosen.IsThreeD) { Messages.Info(HznMenuText.CircuitNotOpened); return false; }

        if (answer is null)
        {
            if (ResolveOwner(owner) is not { } window) return false;
            answer = await HertzonRunDialog.AskAsync(window, project, chosen, cli.Version);
            if (answer is null) return false;
        }

        string resultsFolder = EmResultsRoot();
        string touchstone = HertzonResults.TouchstonePath(resultsFolder, design, chosen.Rows);
        var request = new HertzonRunRequest(project, design, answer.Setup, answer.Sweep, answer.Sets, touchstone);
        return await RunHertzonAsync(cli, request, chosen, runKey);
    }

    private async System.Threading.Tasks.Task<bool> RunHertzonAsync(HertzonInstallation cli, HertzonRunRequest request, HertzonDesign design, string runKey)
    {
        string name = request.Design;
        string baseDir = Path.GetDirectoryName(Path.GetDirectoryName(request.Output)!)!;
        string? owningWorkspace = CurrentWorkspacePath;
        string setupText = (request.Setup ?? "first enabled setup") + (request.Sweep is null ? "" : " / " + request.Sweep);
        Messages.Info($"{cli.Label}: solving '{name}' ({setupText}) from {Path.GetFileName(request.Project)}." +
                      (request.Sets.Count > 0 ? " With " + string.Join(", ", request.Sets.Select(s => $"{s.Key} = {s.Value}")) + "." : ""),
                      request.Project);

        var live = Messages.BeginProgress($"Hertzon '{name}'");
        var descriptor = $"the Hertzon run '{name}'";
        using var cts = new System.Threading.CancellationTokenSource();
        bool cancelPressed = false;
        var cancellation = new RunCancellation(descriptor, () =>
        {
            Messages.Info("Cancelling the Hertzon run. Hertzon is interrupted and nothing is read.");
            cancelPressed = true;
            cts.Cancel();
        });
        live.BindCancellation(cancellation);

        string lastText = "starting";
        string counter = "";
        void Show(double? percent) => Dispatcher.UIThread.Post(() =>
        {
            if (!cancelPressed) live.Update($"Hertzon '{name}' — {lastText}", counter.Length == 0 ? null : counter, percent);
        });
        void OnEvent(HertzonEvent e)
        {
            switch (e.Kind)
            {
                case HertzonEventKind.Progress:
                    lastText = e.Message;
                    Show(e.Percent);
                    break;
                case HertzonEventKind.Pass:
                    counter = $"pass {e.Pass} · {e.Tetrahedra?.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra"
                              + (e.DeltaS is { } d ? $" · ΔS {d.ToString("0.####", CultureInfo.InvariantCulture)}" : "");
                    Show(null);
                    break;
                case HertzonEventKind.Log when e.Level == "warning":
                    Dispatcher.UIThread.Post(() => Messages.Warning(e.Message));
                    break;
                case HertzonEventKind.Quiet:
                    lastText = "no news from Hertzon";
                    Dispatcher.UIThread.Post(() => { Messages.Warning(e.Message); Show(null); });
                    break;
            }
        }

        HertzonRunResult result;
        _hertzonRuns.Add(runKey);
        _emWorkInFlight.Add(descriptor);
        try
        {
            result = await System.Threading.Tasks.Task.Run(() => HertzonRun.Solve(cli, request, OnEvent, cts.Token));
        }
        finally
        {
            cancellation.Finish();
            _emWorkInFlight.Remove(descriptor);
            _hertzonRuns.Remove(runKey);
        }

        if (result.Outcome == HertzonOutcome.Cancelled)
        {
            live.Finish(MessageLevel.Warning, "Hertzon run stopped — no result was read", keepBar: false);
            return false;
        }
        if (!result.HasResult)
        {
            live.Complete(MessageLevel.Info, $"Hertzon '{name}' — see the error below");
            Messages.Error(result.Sentence ?? "Hertzon did not produce a result.");
            return false;
        }

        string summary = $"{cli.Label}: {result.Passes?.ToString(CultureInfo.InvariantCulture) ?? "?"} adaptive pass(es), " +
                         $"{result.Tetrahedra?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} tetrahedra, " +
                         $"{result.Seconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"} s" +
                         (result.Cached ? " (from Hertzon's kept solution)" : "");
        live.Finish(result.Outcome == HertzonOutcome.NotConverged ? MessageLevel.Warning : MessageLevel.Success, summary, keepBar: false);
        if (result.Sentence is { } warning) Messages.Warning(warning);
        Messages.Success($"Wrote s-parameters ({cli.Label})", result.TouchstonePath);

        RfCore.Data.DataSet data;
        string? npy;
        string? npyError;
        try
        {
            (data, npy, npyError) = await System.Threading.Tasks.Task.Run(() =>
            {
                var ds = HertzonResults.Build(result.TouchstonePath!, result);
                string? written = HertzonResults.WriteNpy(baseDir, name, ds, out string? error);
                return (ds, written, error);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            Messages.Error($"Hertzon wrote '{result.TouchstonePath}', which circuitRF could not read: {ex.Message}");
            return false;
        }
        if (npy is null) { Messages.Warning("The results file could not be written: " + npyError); return true; }
        Messages.Success($"Wrote results ({cli.Label})", npy);

        if (!string.Equals(CurrentWorkspacePath, owningWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            Messages.Info($"This window moved to another workspace while '{name}' was solving, so no Data Display was opened. The results are in {Path.Combine(baseDir, "results")}.");
            return true;
        }
        await RefreshOpenDataDisplaysAsync([npy]);
        await AutoOpenOrCreateDataDisplayAsync(baseDir, Path.GetFileNameWithoutExtension(npy), npy);
        return true;
    }
}
