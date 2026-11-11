using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// Allowing a plugin — the first-run prompt's Allow and Settings ▸ Plugins' picker, and nowhere else (<c>plugins.md</c>
/// §9.4). An installed Python plugin whose environment was not made at install — it was installed outside the window,
/// where nothing may run — gets it now, with the install dialog's progress and log (brief-plg-6 §5); if that fails the
/// plugin stays installed and not allowed, and the reason is returned and recorded.
/// </summary>
public static class PluginAllow
{
    /// <summary>Allows <paramref name="entry"/>, or returns why it could not be allowed.</summary>
    public static async Task<string?> AllowAsync(Window owner, PluginEntry entry)
    {
        if (entry.Source == PluginSource.Installed && PluginInstaller.RecordFor(entry.Directory) is { } record
            && (record.NeedsEnvironment || !PluginPythonEnvironment.IsPresent(entry.Directory, record.Environment)))
        {
            bool network = PluginPythonEnvironment.NeedsNetwork(entry.Directory);
            var made = await PluginInstallDialog.RunAsync(owner, $"Preparing {entry.DisplayName}'s Python environment",
                async (log, ct) =>
                {
                    string? why = await PluginInstaller.PrepareEnvironmentAsync(PluginHost.InstallContext(null, log), entry.Directory, ct);
                    return why is null ? new PluginInstallResult(true, entry.Manifest?.Id, entry.Manifest?.Version, entry.Directory)
                                       : PluginInstallResult.Refused(why);
                },
                network ? "Its requirements are installed with pip, which reaches the network." : "It has no requirements, so nothing reaches the network.");
            if (!made.Ok) return made.Refusal ?? "Its Python environment could not be made.";
        }
        PluginHost.RecordPermission(entry.Directory, true);
        return null;
    }
}
