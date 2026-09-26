using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Engine;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// The GUI's <i>Install …</i> (brief-em3d-24 §0): offered from a 3D run's refusal and from each Settings ▸
/// 3D EM row. It asks for consent, then runs <see cref="SolverInstaller.Install"/> — the one function
/// <c>circuitrf solver install</c> also calls — off the UI thread, as a live Messages row with a Cancel
/// button, on the <see cref="RunControl"/> an EM run uses (R-em3d24-2c). It owns no install logic: this
/// file is a dialog, a progress row and a report.
///
/// <para>A day-long build must not hold a window hostage, so nothing here is modal after the consent
/// dialog closes. One install per tool at a time in this process; the installer's own lock refuses a
/// second process as well.</para>
/// </summary>
internal static class SolverInstallRunner
{
    private static readonly HashSet<SolverTool> Running = [];

    /// <summary>Raised on the UI thread when an install attempt ends, however it ended — the Settings rows
    /// re-ask discovery on it.</summary>
    public static event Action<SolverTool>? Finished;

    public static bool IsRunning(SolverTool tool) => Running.Contains(tool);

    /// <param name="messages">Where the progress row goes; null uses the last active workspace window's
    /// Messages panel (the Settings dialog has none of its own).</param>
    public static async Task InstallAsync(SolverTool tool, Window? owner, IMessageSink? messages)
    {
        messages ??= (App.LastActiveWorkspace?.DataContext as ViewModels.WorkspaceViewModel)?.Messages;
        string name = SolverDiscovery.For(tool).Name;

        if (SolverRecipes.For(tool) is not { } recipe)
        {
            messages?.Error($"circuitRF has no install recipe for {name} on this computer. Recipes exist for: " +
                            $"{SolverRecipes.PlatformsFor(tool)}.");
            return;
        }
        if (!Running.Add(tool))
        {
            messages?.Info($"{name} is already being installed — its progress is in the Messages panel.");
            return;
        }

        try
        {
            var installer = new SolverInstaller();
            owner ??= App.DialogOwner();
            if (owner is null || !await Views.Dialogs.SolverInstallConsentDialog.AskAsync(owner, name, recipe.Version, installer.Consent(recipe)))
                return;

            string title = $"Install {name} {recipe.Version}";
            var live = messages?.BeginProgress(title);
            live?.Update(title, "starting", indeterminate: true);

            using var cts = new CancellationTokenSource();
            var cancellation = new RunCancellation($"the install of {name}", () =>
            {
                messages?.Info($"Cancelling the install of {name}. The running step is stopped, and nothing is published.");
                cts.Cancel();
            });
            live?.BindCancellation(cancellation);

            var control = new RunControl
            {
                Token    = cts.Token,
                Total    = SolverInstaller.WorkUnits(recipe),
                Progress = new Inline(p => Dispatcher.UIThread.Post(() => live?.Update(
                    $"{title}: {p.Stage}",
                    Counter(p),
                    p.StageTotal > 0 ? 100.0 * p.StageCompleted / p.StageTotal
                                     : p.Total > 0 ? 100.0 * p.Completed / p.Total : null,
                    indeterminate: p.StageTotal == 0 && p.Total == 0))),
            };

            InstallOutcome outcome;
            try
            {
                outcome = await Task.Run(() => installer.Install(recipe, control));
            }
            finally
            {
                cancellation.Finish();
            }

            var level = outcome.Status switch
            {
                InstallStatus.Installed        => MessageLevel.Success,
                InstallStatus.AlreadyInstalled => MessageLevel.Info,
                InstallStatus.Cancelled        => MessageLevel.Info,
                _                              => MessageLevel.Error,
            };
            if (live is not null) live.Complete(level, outcome.Report);
            else messages?.Post(level, outcome.Report);
        }
        finally
        {
            Running.Remove(tool);
            Finished?.Invoke(tool);
        }
    }

    /// <summary>The changing tail of the row: "step k of N", then the stage's own figure.</summary>
    private static string Counter(RunProgress p)
    {
        string step = p.Total > 0 ? $"step {Math.Min(p.Completed + 1, p.Total)} of {p.Total}" : "";
        string stage = p.StageTotal > 0 ? $"{p.StageCompleted} / {p.StageTotal} {p.StageUnit}".TrimEnd() : "";
        return string.Join(" · ", new[] { step, stage, p.StageDetail }.Where(s => s.Length > 0));
    }

    private sealed class Inline(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
