using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>A macOS <c>.app</c>: moved to the Trash.</summary>
    MacTrash,

    /// <summary>The Linux tarball: its own <c>install.sh --uninstall</c>, installed beside it.</summary>
    LinuxScript,

    /// <summary>A Linux <c>.deb</c>: the package manager removes it, so circuitRF prints the command and stops.</summary>
    LinuxPackage,

    /// <summary>Not an installed copy (a development build), or an install whose own uninstaller cannot
    /// be found. circuitRF removes nothing of itself and says how.</summary>
    Unsupported,
}

/// <param name="Target">The product code, the <c>.app</c>, or the script — what <see cref="AppUninstall.RemoveApp"/> acts on.</param>
/// <param name="Describe">One sentence: how circuitRF itself will be removed, or why it will not be.</param>
internal sealed record AppRemoval(AppRemovalKind Kind, string Target, string Describe);

/// <summary>
/// <i>Uninstall circuitRF…</i> (brief-em3d-25 R-em3d25-4, em-3d.md §7.2 points 1, 2 and 5): the warning,
/// then the solvers exactly as <i>Remove all 3D solvers</i> removes them (refused while one is in use),
/// then circuitRF itself — the MSI uninstall on Windows, the Trash on macOS, the tarball's own
/// <c>install.sh --uninstall</c> on Linux, and for a <c>.deb</c> the one command that removes it.
///
/// <para><b>Reached two ways</b>: Help ▸ <i>Uninstall circuitRF…</i> on every platform, and on Windows
/// the Apps list, whose Uninstall runs <c>circuitRF.exe --uninstall</c> (<see cref="Argument"/>,
/// <c>packaging/windows/circuitRF.wxs</c>). There is deliberately no CLI verb (R-em3d25-4c): a build
/// machine runs <c>circuitrf solver remove --all</c> and then the platform's own uninstall.</para>
///
/// <para><b>Nothing here is reachable from an upgrade</b> (R-em3d25-5a): the updater and the bundle
/// exchange call neither this type nor <see cref="SolverUninstaller"/>, which a source scan holds.</para>
/// </summary>
internal static class AppUninstall
{
    /// <summary>The argument the Windows Apps-list entry starts circuitRF with.</summary>
    public const string Argument = "--uninstall";

    /// <summary>The <c>.deb</c>'s package name (<c>packaging/linux/build-linux.sh</c>, <c>fpm -n</c>).</summary>
    public const string DebPackage = "circuitrf";

    /// <summary>The <c>.deb</c>'s install directory (<c>build-linux.sh</c>).</summary>
    public const string DebRoot = "/opt/circuitrf";

    /// <summary>Where the Windows installer records its product code (<c>circuitRF.wxs</c>, AppsEntryComp).</summary>
    public const string ProductCodeKey = @"Software\circuitRF\circuitRF";

    /// <summary>How the running copy is removed.</summary>
    public static AppRemoval Detect() => Detect(UpdateInstallSite.Detect(), ReadProductCode);

    /// <summary>The rule, with the platform's facts handed in so a test can ask it about any layout.</summary>
    internal static AppRemoval Detect(InstallSite site, Func<bool, string?> productCode)
    {
        if (OperatingSystem.IsWindows())
        {
            // perUser is the versioned layout behind the stub; perMachine is the flat Program Files one.
            bool perUser = site.Shape == InstallShape.VersionedPointer;
            return productCode(perUser) is { Length: > 0 } code
                ? new(AppRemovalKind.WindowsMsi, code, "Windows Installer then removes circuitRF; it asks you to confirm once more.")
                : new(AppRemovalKind.Unsupported, "",
                      "This copy of circuitRF was not installed by its .msi, so it cannot remove itself: delete its folder, " +
                      $"{site.Root}, yourself.");
        }
        if (OperatingSystem.IsMacOS())
            return site.Shape == InstallShape.MacOsBundle
                ? new(AppRemovalKind.MacTrash, site.Root, $"circuitRF ({site.Root}) is then moved to the Trash.")
                : new(AppRemovalKind.Unsupported, "", $"This copy of circuitRF is not an installed application ({site.Root}), so it cannot remove itself.");

        if (site.Shape == InstallShape.VersionedPointer)
        {
            // The running version's own copy first: the updater installs only app-<ver>/, so that is the
            // one an updated install is sure to have. The root copy is what install.sh left at install time.
            string? script = new[] { Path.Combine(site.Root, UpdateInstallSite.CurrentPointerName, "install.sh"), Path.Combine(site.Root, "install.sh") }
                .FirstOrDefault(File.Exists);
            return script is not null
                ? new(AppRemovalKind.LinuxScript, script, $"circuitRF is then removed from {site.Root} by its own install.sh --uninstall.")
                : new(AppRemovalKind.Unsupported, "",
                      $"This copy of circuitRF is older than its built-in uninstaller, so there is no install.sh in {site.Root}. " +
                      "Run install.sh --uninstall from the archive you installed it from, or update circuitRF first.");
        }
        if (IsUnder(site.Root, DebRoot))
            return new(AppRemovalKind.LinuxPackage, $"sudo apt remove {DebPackage}",
                       $"circuitRF was installed by your package manager, which is what removes it: afterwards, run  sudo apt remove {DebPackage}");
        return new(AppRemovalKind.Unsupported, "", $"This copy of circuitRF is not an installed application ({site.Root}), so it cannot remove itself.");
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
                      "each account's own Remove all 3D solvers is how they go.");
        sb.AppendLine();
        sb.Append(removal.Describe);
        if (removal.Kind is AppRemovalKind.WindowsMsi or AppRemovalKind.MacTrash or AppRemovalKind.LinuxScript)
            sb.Append(" circuitRF closes when it is done, asking about any unsaved work first.");
        return sb.ToString();
    }

    /// <summary>
    /// Step 3: removes circuitRF itself. Returns the sentence to show — or null when the caller should
    /// now quit because circuitRF is on its way out.
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

            case AppRemovalKind.MacTrash:
                return MacTrash.MoveToTrash(removal.Target, out string? why)
                    ? null
                    : $"The 3D solvers were removed, but circuitRF could not be moved to the Trash ({why}). Drag {removal.Target} to the Trash yourself.";

            case AppRemovalKind.LinuxScript:
                var sh = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                sh.ArgumentList.Add(removal.Target);
                sh.ArgumentList.Add("--uninstall");
                sh.ArgumentList.Add("--yes");
                using (var p = Process.Start(sh))
                {
                    if (p is null) return $"The 3D solvers were removed, but {removal.Target} could not be started. Run it with --uninstall yourself.";
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0
                        ? null
                        : $"The 3D solvers were removed, but {removal.Target} --uninstall failed (exit {p.ExitCode}): {(output + err.Result).Trim()}";
                }

            case AppRemovalKind.LinuxPackage:
                return $"The 3D solvers were removed. circuitRF was installed by your package manager; remove it with:  {removal.Target}";

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

    private static bool IsUnder(string path, string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full == root || full.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
