using System.Text.Json;
using CircuitRF.Design.Layout.PCells.Wire;

namespace CircuitRF.Design.Plugins;

/// <summary>What this installation has decided about one plugin.</summary>
public enum PluginTrustDecision
{
    /// <summary>Nobody has been asked. Nothing runs.</summary>
    Unknown,
    Allowed,
    Denied,
}

/// <summary>
/// Which plugins this installation has agreed to run (<c>plugins.md</c> P-6, §9.4).
///
/// <para><b>The <see cref="PCellTrustStore"/> shape and its reasoning, in a store of its own.</b> Recorded per user
/// in preferences and never in a <c>.cws</c> — a decision that travels with a workspace can be written by whoever
/// sends the workspace. Keyed by the plugin's absolute installed directory, so editing a plugin under development
/// does not re-ask, and a plugin moved somewhere else is asked about again. Separate from the kit store so that
/// revoking a kit never touches a plugin and the reverse.</para>
/// </summary>
public sealed class PluginTrustStore
{
    /// <summary>The <c>preferences.json</c> key decisions are recorded under.</summary>
    public const string PreferenceKey = "plugin_trust";

    private readonly Dictionary<string, bool> _decisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<IReadOnlyDictionary<string, bool>>? _persist;
    private readonly Action<string, bool>? _persistOne;

    /// <param name="seed">Decisions already recorded.</param>
    /// <param name="persist">Where a new decision goes; null keeps it in memory (a headless caller, a test).</param>
    /// <param name="persistOne">Where ONE new decision goes, by its key — what the window uses, so a decision is merged
    /// into the file rather than this store's whole table written over it (brief-plg-6 §4). Wins over
    /// <paramref name="persist"/>.</param>
    public PluginTrustStore(IEnumerable<KeyValuePair<string, bool>>? seed = null, Action<IReadOnlyDictionary<string, bool>>? persist = null,
        Action<string, bool>? persistOne = null)
    {
        _persist = persist;
        _persistOne = persistOne;
        if (seed is null) return;
        foreach (var (path, allowed) in seed)
            if (Key(path) is { Length: > 0 } key) _decisions[key] = allowed;
    }

    /// <summary>
    /// The decisions recorded in <paramref name="preferencesPath"/> (this user's by default), read-only — what the
    /// command line honours. A missing or malformed file is no decisions, never a throw.
    /// </summary>
    public static PluginTrustStore FromRecordedPreferences(string? preferencesPath = null)
        => new(ReadRecorded(preferencesPath));

    /// <summary>The <see cref="PreferenceKey"/> table as the file holds it now, or empty.</summary>
    public static IReadOnlyDictionary<string, bool> ReadRecorded(string? preferencesPath = null)
    {
        var seed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = preferencesPath ?? UserStateDirectory.PreferencesPath;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(PreferenceKey, out var table)
                    && table.ValueKind == JsonValueKind.Object)
                    foreach (var row in table.EnumerateObject())
                        if (row.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            seed[row.Name] = row.Value.GetBoolean();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return seed;
    }

    public PluginTrustDecision Decide(string pluginDirectory)
        => _decisions.TryGetValue(Key(pluginDirectory), out bool allowed)
            ? allowed ? PluginTrustDecision.Allowed : PluginTrustDecision.Denied
            : PluginTrustDecision.Unknown;

    /// <summary>Records and writes a decision. A refusal is recorded too, so the same plugin is not asked about on
    /// every start; Settings ▸ Plugins (brief 6) is where it changes.</summary>
    public void Record(string pluginDirectory, bool allowed)
    {
        string key = Key(pluginDirectory);
        if (key.Length == 0) return;
        _decisions[key] = allowed;
        try
        {
            if (_persistOne is not null) _persistOne(key, allowed);
            else _persist?.Invoke(_decisions);
        }
        catch { /* preferences are best-effort; the session's answer stands */ }
    }

    public IReadOnlyDictionary<string, bool> Decisions => _decisions;

    /// <summary>The key a directory is recorded under: absolute, links resolved, no trailing separator.</summary>
    public static string Key(string directory) => PCellTrustStore.ResolvedDirectory(directory);
}

/// <summary>Which override, if any, is forcing plugins off.</summary>
public enum PluginPolicyOverride { None, PolicyFile, Environment }

/// <summary>The effective answer to "may plugins run here", and why.</summary>
/// <param name="Reason">The sentence a refusal or a disabled setting shows; empty when nothing overrides and the
/// preference allows.</param>
public sealed record PluginPolicyState(bool Allowed, PluginPolicyOverride Override, string Reason)
{
    public bool IsOverridden => Override != PluginPolicyOverride.None;
}

/// <summary>
/// Whether plugins may run at all (<c>plugins.md</c> §9.4): a <c>no-plugins</c> file beside the install, then
/// <c>CRF_NO_PLUGINS=1</c>, then the preference (default on). The precedence and record shape of
/// <c>ExternalWorkerPolicy</c>, below the firewall so the command line refuses on the same answer; the install root
/// and the preference are arguments.
/// </summary>
public static class PluginPolicy
{
    public const string PolicyFileName      = "no-plugins";
    public const string EnvironmentVariable = "CRF_NO_PLUGINS";
    /// <summary>The <c>preferences.json</c> key of the on/off preference.</summary>
    public const string PreferenceKey       = "plugins_enabled";

    /// <param name="installRoot">The installation's root, beside which an administrator drops the marker.</param>
    /// <param name="preference">The user's setting; null is the default, on.</param>
    /// <param name="environment">Reads an environment variable; null reads this process's.</param>
    public static PluginPolicyState Resolve(string? installRoot, bool? preference, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (InstallPolicyFile.PresentBeside(installRoot, PolicyFileName))
            return new PluginPolicyState(false, PluginPolicyOverride.PolicyFile,
                $"Plugins are disabled for this installation by an administrator (a '{PolicyFileName}' file beside the application).");

        if (environment(EnvironmentVariable) == "1")
            return new PluginPolicyState(false, PluginPolicyOverride.Environment,
                $"Plugins are disabled by {EnvironmentVariable}=1 in this environment.");

        bool wanted = preference ?? true;
        return new PluginPolicyState(wanted, PluginPolicyOverride.None,
            wanted ? "" : "Plugins are turned off in Settings.");
    }
}
