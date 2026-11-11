using System;
using System.Collections.Generic;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// The per-USER half of the plugin runtime (brief-plg-1): everything <c>src/Design/Plugins</c> takes as an argument,
/// read from this installation's preferences. Nothing else in <c>src/Ui</c> reads the plugin preference keys.
/// </summary>
public static class PluginHost
{
    /// <summary>Whether plugins may run here, recomputed on every read so a Settings change applies at the next start.</summary>
    public static PluginPolicyState Policy
        => PluginPolicy.Resolve(Updates.UpdateInstallSite.Detect().Root, AppPreferencesIo.Load().PluginsEnabled);

    /// <summary>The preferences file the plugin keys live in — this installation's.</summary>
    public static string PreferencesPath => CircuitRF.Design.UserStateDirectory.PreferencesPath;

    /// <summary>
    /// The trust store the application uses. <b>Read from the file each time it is consulted, and each decision merged
    /// into the file by its one key</b> (brief-plg-6 §4): <c>circuitRF plugin deny</c> writes the same table while the
    /// window is open, and a store seeded from the window's in-memory copy, or one that wrote its whole table back, would
    /// quietly allow again a plugin the person had just revoked.
    /// </summary>
    public static PluginTrustStore Trust()
        => new(PluginTrustStore.ReadRecorded(PreferencesPath), persistOne: (key, allowed) => AppPreferencesIo.UpdatePluginTrust(key, allowed));

    /// <summary>Records <paramref name="pluginDirectory"/>'s permission — allowed, denied, or (null) Ask again. The
    /// window's only writer of the table, and the only place in circuitRF that can allow (§9.4).</summary>
    public static void RecordPermission(string pluginDirectory, bool? allowed)
        => AppPreferencesIo.UpdatePluginTrust(PluginTrustStore.Key(pluginDirectory), allowed);

    /// <summary>Where the Tools ▸ Plugins menu looks: the per-user plugins folder, linked folders, CRF_PLUGIN_PATH. The
    /// linked folders are read from the file, so a folder linked from the command line appears without a restart.</summary>
    public static PluginCatalogSources Sources()
    {
        var recorded = PluginPreferences.Read(PreferencesPath);
        return new PluginCatalogSources(
            PluginCatalogSources.DefaultRoot,
            recorded.LinkedFolders,
            Environment.GetEnvironmentVariable(PluginCatalogSources.EnvironmentVariable),
            recorded.ActiveVersions);
    }

    /// <summary>An install made from the window: Settings is its origin, and a Python plugin's environment is made as part
    /// of it — this is where the person asked for it.</summary>
    public static PluginInstallContext InstallContext(IPluginHttp? http, Action<string>? log)
        => new(PluginCatalogSources.DefaultRoot, PreferencesPath, PluginInstallOrigin.Settings, http, log, CreatePythonEnvironment: true);

    /// <summary>The vetted list in use: Refresh's copy when its signature verifies under the release key, else the one
    /// built in. Reads files only.</summary>
    public static PluginIndex Index()
        => PluginIndex.Current(PluginInstaller.IndexCache(PluginCatalogSources.DefaultRoot), Updates.ReleaseKeys.PublicKeySpkiBase64);

    /// <summary>The catalogue as the menu shows it. Reads manifests only.</summary>
    public static IReadOnlyList<PluginEntry> Discover() => PluginCatalog.Discover(Sources());

    /// <summary>Why a listed plugin cannot be started from the menu, or null when it can.</summary>
    public static string? UnavailableReason(PluginEntry entry) => entry.DimReason;

    /// <summary>The width <paramref name="pluginId"/>'s parameter dialog was last left at, or null for the default.</summary>
    public static double? DialogWidth(string pluginId)
        => AppPreferencesIo.Load().PluginDialogWidths?.GetValueOrDefault(pluginId) is { } w && w > 0 ? w : null;

    /// <summary>Remembers the dialog's width for <paramref name="pluginId"/>, on this machine.</summary>
    public static void RecordDialogWidth(string pluginId, double width)
        => AppPreferencesIo.Update(p =>
        {
            p.PluginDialogWidths ??= new Dictionary<string, double>(StringComparer.Ordinal);
            p.PluginDialogWidths[pluginId] = Math.Round(width);
        });

    /// <summary><paramref name="pluginId"/>'s panel window as last left, or null.</summary>
    public static PluginWindowMemory? WindowMemory(string pluginId)
        => AppPreferencesIo.Load().PluginWindows?.GetValueOrDefault(pluginId);

    /// <summary>Remembers <paramref name="pluginId"/>'s panel window, on this machine.</summary>
    public static void RecordWindowMemory(string pluginId, PluginWindowMemory memory)
        => AppPreferencesIo.Update(p =>
        {
            p.PluginWindows ??= new Dictionary<string, PluginWindowMemory>(StringComparer.Ordinal);
            p.PluginWindows[pluginId] = memory;
        });

    private static string ParamsKey(string pluginId, string? workspaceDir) => $"{pluginId}|{workspaceDir ?? ""}";

    /// <summary>The parameter text last sent from <paramref name="pluginId"/>'s panel in this workspace, or null.</summary>
    public static IReadOnlyDictionary<string, string>? LastParams(string pluginId, string? workspaceDir)
        => AppPreferencesIo.Load().PluginLastParams?.GetValueOrDefault(ParamsKey(pluginId, workspaceDir));

    /// <summary>Remembers the parameter text last sent, per plugin per workspace, in this user's preferences.</summary>
    public static void RecordLastParams(string pluginId, string? workspaceDir, IReadOnlyDictionary<string, string> values)
        => AppPreferencesIo.Update(p =>
        {
            p.PluginLastParams ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            p.PluginLastParams[ParamsKey(pluginId, workspaceDir)] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        });

    /// <summary>Where the plugin came from, for the permission prompt.</summary>
    public static string SourceText(PluginEntry entry)
        => entry.Source == PluginSource.Linked ? $"the folder {entry.Directory}" : $"installed in {entry.Directory}";

    /// <summary>What the permission prompt says of a linked folder, which runs in place (brief-plg-6 §2).</summary>
    public const string LinkedNote = "Linked folder — changes take effect on the next start";

    /// <summary>What will run, for the permission prompt: the executable, or the interpreter and the entry.</summary>
    public static string RunsText(PluginEntry entry)
    {
        var run = entry.Manifest?.ResolveRun();
        return run?.Entry switch
        {
            { IsPython: true } p => $"Python, {p.PythonEntry}" + (p.RequiresPython is { } v ? $" (Python {v})" : ""),
            { Exe: { } exe }     => exe,
            _                    => run?.Refusal ?? "nothing on this machine",
        };
    }
}
