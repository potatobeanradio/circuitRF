using System;
using System.Diagnostics;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui;

/// <summary>
/// Where this copy of circuitRF is installed, as the About box shows it (brief-install-location
/// R-inst-2). A first-time Windows user could not tell where the installer had put it.
///
/// <para><b>The updater's answer, not a second one.</b> <see cref="UpdateInstallSite.Detect"/> already
/// works the layout out structurally: the <c>.app</c> bundle on macOS, the folder holding
/// <c>current</c> for a per-user Windows install (the folder the user picked, not the
/// <c>app-&lt;version&gt;</c> inside it, which changes with every update), and the executable's own
/// folder otherwise. Its result is cached per process, so asking here costs nothing more.</para>
/// </summary>
public static class InstallLocation
{
    /// <summary>The install folder (or, on macOS, the <c>.app</c> bundle).</summary>
    public static string Folder => UpdateInstallSite.Detect().Root;

    /// <summary>
    /// Shows <see cref="Folder"/> in the platform's file manager.
    ///
    /// <para><b>A bundle is SELECTED, never opened.</b> <see cref="FileReveal.Reveal"/> opens a
    /// directory, and <c>open</c> on a <c>.app</c> directory launches it — a second circuitRF instead
    /// of a Finder window.</para>
    /// </summary>
    public static void Reveal()
    {
        var site = UpdateInstallSite.Detect();
        if (site.Shape != InstallShape.MacOsBundle)
        {
            FileReveal.Reveal(site.Root);
            return;
        }

        try
        {
            var psi = FileReveal.BuildCommand(site.Root, select: true, FileReveal.Platform.MacOS);
            if (psi is not null) Process.Start(psi);
        }
        catch (Exception) { /* no file manager; the path is on screen to copy either way */ }
    }
}
