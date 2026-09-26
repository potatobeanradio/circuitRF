using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Uninstall;

/// <summary>How this copy of circuitRF is removed, found from how it is laid out on disk.</summary>
internal enum AppRemovalKind
{
    /// <summary>A Windows <c>.msi</c> install: <c>msiexec /x</c> for the product code the installer recorded.</summary>
    WindowsMsi,

    /// <summary>Not an installed copy (a development build, or any platform but Windows). circuitRF
    /// removes nothing of itself and says how.</summary>
    Unsupported,
}

/// <param name="Target">The product code — what <see cref="AppUninstall.RemoveApp"/> acts on.</param>
/// <param name="Describe">One sentence: how circuitRF itself will be removed, or why it will not be.</param>
internal sealed record AppRemoval(AppRemovalKind Kind, string Target, string Describe);

/// <summary>
/// The Windows Apps-list uninstall (brief-em3d-25 R-em3d25-4b, em-3d.md §7.2 points 2 and 5): the
/// warning, then the solvers exactly as <i>Remove all 3D solvers</i> removes them (refused while one is
/// in use), then the MSI uninstall. The Apps list's Uninstall runs <c>circuitRF.exe --uninstall</c>
/// (<see cref="Argument"/>, <c>packaging/windows/circuitRF.wxs</c>).
///
/// <para><b>Windows only, and not a menu command.</b> An in-app <i>Uninstall circuitRF…</i> was removed
/// (2026-09-26): an application is removed the way its platform removes applications. The Linux
/// tarball's <c>install.sh --uninstall</c> does its own equivalent through <c>solver remove --all</c>;
/// the macOS Trash and a <c>.deb</c> removal run no circuitRF code, so there the solvers stay, are
/// reused by a reinstall, and are removed from Settings ▸ 3D EM (em-3d.md §7.2 point 4). There is
/// deliberately no CLI verb (R-em3d25-4c): a build machine runs <c>circuitrf solver remove --all</c> and
/// then the platform's own uninstall.</para>
///
/// <para><b>Nothing here is reachable from an upgrade</b> (R-em3d25-5a): the updater and the bundle
/// exchange call neither this type nor <see cref="SolverUninstaller"/>, which a source scan holds.</para>
/// </summary>
internal static class AppUninstall
{
    /// <summary>The argument the Windows Apps-list entry starts circuitRF with.</summary>
    public const string Argument = "--uninstall";

    /// <summary>Where the Windows installer records its product code (<c>circuitRF.wxs</c>, AppsEntryComp).</summary>
    public const string ProductCodeKey = @"Software\circuitRF\circuitRF";

    /// <summary>How the running copy is removed.</summary>
    public static AppRemoval Detect() => Detect(UpdateInstallSite.Detect(), ReadProductCode);

    /// <summary>The rule, with the platform's facts handed in so a test can ask it about any layout.</summary>
    internal static AppRemoval Detect(InstallSite site, Func<bool, string?> productCode)
    {
        if (!OperatingSystem.IsWindows())
            return new(AppRemovalKind.Unsupported, "",
                       "circuitRF removes itself only from the Windows Apps list. Elsewhere, remove it the way it was installed.");

        // perUser is the versioned layout behind the stub; perMachine is the flat Program Files one.
        bool perUser = site.Shape == InstallShape.VersionedPointer;
        return productCode(perUser) is { Length: > 0 } code
            ? new(AppRemovalKind.WindowsMsi, code, "Windows Installer then removes circuitRF; it asks you to confirm once more.")
            : new(AppRemovalKind.Unsupported, "",
                  "This copy of circuitRF was not installed by its .msi, so it cannot remove itself: delete its folder, " +
                  $"{site.Root}, yourself.");
    }

    /// <summary>
    /// The warning (R-em3d25-4a step 1): the solvers go too, each with its size and what reinstalling it
    /// costs; solvers other accounts installed are theirs and stay; documents are not touched; and how
    /// circuitRF itself is removed.
    /// </summary>
    public static string Warning(SolverRemovalPlan solvers, AppRemoval removal)
    {
        var sb = new StringBuilder();
        if (solvers.CanProceed)
        {
            sb.AppendLine("Uninstalling circuitRF also removes the 3D solvers it installed for you. Reinstalling circuitRF later means reinstalling them too.");
            sb.AppendLine();
            sb.AppendLine(solvers.Confirmation);
        }
        else
        {
            sb.AppendLine("circuitRF has installed no 3D solvers for this account, so none are removed.");
            sb.AppendLine("Your documents are not touched: workspaces, setups and results all stay.");
        }
        sb.AppendLine();
        sb.AppendLine("This reaches only your account. Solvers another account installed on this computer are theirs and stay; " +
                      "each account's own Settings ▸ 3D EM ▸ Remove all 3D solvers is how they go.");
        sb.AppendLine();
        sb.Append(removal.Describe);
        return sb.ToString();
    }

    /// <summary>
    /// Step 3: removes circuitRF itself. Returns the sentence to show — or null when the installer has
    /// taken over and there is nothing more to say.
    /// </summary>
    public static string? RemoveApp(AppRemoval removal)
    {
        switch (removal.Kind)
        {
            case AppRemovalKind.WindowsMsi:
                // Its own interface, not /passive: its one confirmation gives this process the time to quit,
                // and a file still held is shown by the installer's files-in-use dialog rather than guessed at.
                var msi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
                msi.ArgumentList.Add("/x");
                msi.ArgumentList.Add(removal.Target);
                Process.Start(msi)?.Dispose();
                return null;

            default:
                return removal.Describe;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadProductCodeWindows(bool perUser)
    {
        // A 32-bit package writes HKLM through WOW6432Node, so the 64-bit view alone would miss it.
        var hive = perUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine;
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var key  = root.OpenSubKey(ProductCodeKey);
                if (key?.GetValue("ProductCode") is string code && code.StartsWith('{') && code.EndsWith('}')) return code;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return null;
    }

    private static string? ReadProductCode(bool perUser) => OperatingSystem.IsWindows() ? ReadProductCodeWindows(perUser) : null;
}
