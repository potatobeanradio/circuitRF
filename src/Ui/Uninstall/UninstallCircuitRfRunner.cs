using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Uninstall;

/// <summary>
/// The window half of the Windows Apps-list uninstall (brief-em3d-25 R-em3d25-4b): the warning, then the
/// solvers, then circuitRF. The decisions — what is removed, how, and every sentence shown — are
/// <see cref="AppUninstall"/>'s and <see cref="SolverUninstaller"/>'s; this file only asks and reports.
/// There is no in-app menu command for it: an application is removed the way its platform removes
/// applications, and Settings ▸ 3D EM is where the solvers are removed on their own (2026-09-26).
/// </summary>
internal static class UninstallCircuitRfRunner
{
    private const string Title = "Uninstall circuitRF";

    /// <summary>
    /// <c>circuitRF.exe --uninstall</c>: no workspace window, so the question is a window of its own, and
    /// the process ends when it is answered.
    /// </summary>
    public static async Task RunStandaloneAsync(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { await AskAndRemoveAsync(); }
        finally { desktop.Shutdown(); }
    }

    private static async Task AskAndRemoveAsync()
    {
        Window? owner = null;   // the Apps-list start has no window to own the dialogs
        var (plan, removal) = await Task.Run(() => (new SolverUninstaller().PlanAll(), AppUninstall.Detect()));

        if (removal.Kind == AppRemovalKind.Unsupported)
        {
            await TextConfirmDialog.AskAsync(owner, Title, "circuitRF cannot uninstall this copy",
                removal.Describe + (plan.CanProceed
                    ? " The 3D solvers circuitRF installed can still be removed from Settings ▸ 3D EM ▸ Remove all 3D solvers."
                    : ""), confirmLabel: null);
            return;
        }

        if (!await TextConfirmDialog.AskAsync(owner, Title, "Uninstall circuitRF?", AppUninstall.Warning(plan, removal),
                                              plan.CanProceed ? $"_Uninstall circuitRF and its solvers ({SolverUninstaller.Size(plan.TotalBytes)})"
                                                              : "_Uninstall circuitRF"))
            return;

        // Step 2 — exactly Remove all 3D solvers; a refusal (one in use) stops here with circuitRF intact.
        if (plan.CanProceed)
        {
            var outcome = await Task.Run(() => new SolverUninstaller().Remove(plan));
            if (outcome.Status != RemovalStatus.Removed)
            {
                await TextConfirmDialog.AskAsync(owner, Title, "circuitRF was not uninstalled", outcome.Report, confirmLabel: null);
                return;
            }
        }

        // Step 3 — circuitRF itself.
        string? report = await Task.Run(() => AppUninstall.RemoveApp(removal));
        if (report is not null)
            await TextConfirmDialog.AskAsync(owner, Title, "Uninstall circuitRF", report, confirmLabel: null);
    }
}
