using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Uninstall;

/// <summary>
/// The window half of <i>Uninstall circuitRF…</i> (brief-em3d-25 R-em3d25-4a): the warning, then the
/// solvers, then circuitRF. The decisions — what is removed, how, and every sentence shown — are
/// <see cref="AppUninstall"/>'s and <see cref="SolverUninstaller"/>'s; this file only asks and reports.
/// </summary>
internal static class UninstallCircuitRfRunner
{
    private const string Title = "Uninstall circuitRF";

    /// <summary>Help ▸ <i>Uninstall circuitRF…</i>. Quits through <see cref="App.Quit"/> — which asks about
    /// unsaved work — once circuitRF is on its way out.</summary>
    public static async Task RunAsync(Window? owner)
    {
        if (await AskAndRemoveAsync(owner) is { } quit && quit)
            (Avalonia.Application.Current as App)?.Quit();
    }

    /// <summary>
    /// The Windows Apps-list route, <c>circuitRF.exe --uninstall</c>: no workspace window, so the question
    /// is a window of its own, and the process ends when it is answered.
    /// </summary>
    public static async Task RunStandaloneAsync(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { await AskAndRemoveAsync(owner: null); }
        finally { desktop.Shutdown(); }
    }

    /// <summary>True when circuitRF should now close; false when it stays (cancelled, refused, or a
    /// <c>.deb</c> whose removal is the package manager's); null when there was nothing to do.</summary>
    private static async Task<bool?> AskAndRemoveAsync(Window? owner)
    {
        var (plan, removal) = await Task.Run(() => (new SolverUninstaller().PlanAll(), AppUninstall.Detect()));

        if (removal.Kind == AppRemovalKind.Unsupported)
        {
            await TextConfirmDialog.AskAsync(owner, Title, "circuitRF cannot uninstall this copy",
                removal.Describe + (plan.CanProceed
                    ? " The 3D solvers circuitRF installed can still be removed from Settings ▸ 3D EM ▸ Remove all 3D solvers."
                    : ""), confirmLabel: null);
            return null;
        }

        if (!await TextConfirmDialog.AskAsync(owner, Title, "Uninstall circuitRF?", AppUninstall.Warning(plan, removal),
                                              plan.CanProceed ? $"_Uninstall circuitRF and its solvers ({SolverUninstaller.Size(plan.TotalBytes)})"
                                                              : "_Uninstall circuitRF"))
            return false;

        // Step 2 — exactly Remove all 3D solvers; a refusal (one in use) stops here with circuitRF intact.
        if (plan.CanProceed)
        {
            var outcome = await Task.Run(() => new SolverUninstaller().Remove(plan));
            if (outcome.Status != RemovalStatus.Removed)
            {
                await TextConfirmDialog.AskAsync(owner, Title, "circuitRF was not uninstalled", outcome.Report, confirmLabel: null);
                return false;
            }
        }

        // Step 3 — circuitRF itself.
        string? report = await Task.Run(() => AppUninstall.RemoveApp(removal));
        if (report is null) return true;
        await TextConfirmDialog.AskAsync(owner, Title, "Uninstall circuitRF", report, confirmLabel: null);
        return false;
    }
}
