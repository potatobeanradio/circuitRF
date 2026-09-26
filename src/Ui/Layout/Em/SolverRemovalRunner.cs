using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>
/// The GUI's removal of circuitRF-installed solvers (brief-em3d-25): a Settings ▸ 3D EM row's
/// <i>Uninstall …</i> and <i>Remove all 3D solvers…</i>. It plans
/// with <see cref="SolverUninstaller"/> (sizes measured now), asks with the plan's own confirmation, and
/// removes with <see cref="SolverUninstaller.Remove"/> — the functions <c>circuitrf solver remove</c>
/// calls. It owns no removal logic: this file is a dialog and a report.
/// </summary>
internal static class SolverRemovalRunner
{
    /// <summary>Raised on the UI thread when a removal ends, however it ended.</summary>
    public static event Action? Finished;

    /// <summary>
    /// Plans (off the UI thread — measuring a Spack tree is thousands of files), confirms, and removes.
    /// Returns the outcome, or null when the user cancelled; a refusal before the question is returned as a
    /// Refused outcome without asking anything.
    /// </summary>
    /// <param name="plan">The plan to make: one tool's version, or all of them.</param>
    public static async Task<RemovalOutcome?> RemoveAsync(Func<SolverUninstaller, SolverRemovalPlan> plan, Window? owner,
                                                          IMessageSink? messages, string title)
    {
        messages ??= (App.LastActiveWorkspace?.DataContext as ViewModels.WorkspaceViewModel)?.Messages;
        var uninstaller = new SolverUninstaller();
        try
        {
            var planned = await Task.Run(() => plan(uninstaller));
            if (planned.Refusal is { } why)
            {
                messages?.Info(why);
                return new RemovalOutcome(RemovalStatus.Refused, why, [], []);
            }

            owner ??= App.DialogOwner();
            if (owner is null || !await Views.Dialogs.TextConfirmDialog.AskAsync(
                    owner, title, $"{title}?", planned.Confirmation, $"_Remove ({SolverUninstaller.Size(planned.TotalBytes)})"))
                return null;

            var outcome = await Task.Run(() => uninstaller.Remove(planned));
            messages?.Post(outcome.Status switch
            {
                RemovalStatus.Removed => MessageLevel.Success,
                RemovalStatus.Refused => MessageLevel.Warning,
                _                     => MessageLevel.Error,
            }, outcome.Report);
            return outcome;
        }
        finally
        {
            Finished?.Invoke();
        }
    }
}
