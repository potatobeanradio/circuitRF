using System.Threading.Tasks;
using Avalonia.Controls;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-em3d-24 R-em3d24-2a — the consent step before the install assistant fetches anything. The text is
/// <c>SolverInstaller.Consent</c>'s, the same the CLI prints without <c>--yes</c>: the program and version,
/// every upstream URL, where it goes, what it cost when measured and on what machine, the ParMETIS sentence
/// for Palace, and that it runs in the background and can be cancelled. <b>Nothing is downloaded before
/// Install is pressed</b>; Cancel (or closing the window) creates nothing.
///
/// <para>Selectable, because the URLs are the part a cautious user — or their IT department — will want to
/// copy.</para>
/// </summary>
public static class SolverInstallConsentDialog
{
    public static Task<bool> AskAsync(Window owner, string program, string version, string consentText)
        => TextConfirmDialog.AskAsync(owner, $"Install {program}", $"Install {program} {version}?", consentText,
                                      $"_Install {program} {version}");
}
