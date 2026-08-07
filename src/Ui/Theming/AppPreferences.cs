using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Theming;

public enum LaunchAction { Welcome, NewSchematic, NewWorkspace, OpenWorkspace, NewDataDisplay, NewSymbol }
public enum LaunchPane   { ProjectTree, Palette }

public sealed class AppPreferences
{
    [JsonPropertyName("active_theme_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveThemeName { get; set; }

    // MRU list of workspace .cws paths, most-recent first, capped at 10.
    [JsonPropertyName("recent_workspaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RecentWorkspaces { get; set; }

    // MRU list of recently-placed component kinds (SymbolKind.ToString()), most-recent first, capped at 12.
    [JsonPropertyName("recently_placed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RecentlyPlaced { get; set; }

    // Clipboard render policy — null means use the default (FollowSystem / transparent).
    [JsonPropertyName("copy_color_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CopyColorMode? CopyColorMode { get; set; }

    [JsonPropertyName("copy_transparent_background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CopyTransparentBackground { get; set; }

    // Launch behavior — null means use defaults (NewSchematic / Palette).
    [JsonPropertyName("launch_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchAction? LaunchAction { get; set; }

    [JsonPropertyName("launch_pane")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchPane? LaunchPane { get; set; }

    // Which kits' PCell generator scripts this installation has agreed to run, keyed by the kit's
    // absolute directory. DELIBERATELY here and not in .cws: a decision recorded inside a shared
    // workspace could be written by whoever sent you the workspace, which would defeat the prompt
    // entirely. See PCellTrustStore for the full reasoning.
    [JsonPropertyName("pcell_trust")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? PCellTrust { get; set; }

    // L5b / R16d: "Export offers to run DRC first. Not mandatory, not silent." Null means the default,
    // which is ON — catching a spacing error before it reaches a fab is most of DRC's value, and a
    // check the user has to remember to run before every export is one they will forget before the
    // export that mattered. Per USER, not per workspace: it is a working habit, not a property of a
    // design, and a workspace arriving from someone else must not silently turn a user's check off.
    [JsonPropertyName("check_drc_on_export")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CheckDrcOnExport { get; set; }

    // R-h8-4: folders holding device kits, for harmonicaRF's Set DUT. A PREFERENCE and not a
    // workspace: DeviceWorkerProviderResolver's folder-list constructor needs nothing else (src/Cli's
    // --kits already ships that form), and standing up a WorkspaceViewModel to reach it would drag in
    // the project tree, the dock layout, technologies and PCell resolvers — none of which the device
    // path reads. Per user rather than per workspace, because harmonicaRF standalone has no workspace.
    [JsonPropertyName("harmonica_kit_folders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? HarmonicaKitFolders { get; set; }

    // Message timestamp display — null means default (Time). Serialized as a number, like the others.
    [JsonPropertyName("message_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageTimestampMode? MessageTimestamp { get; set; }
}

public static class AppPreferencesIo
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static string PrefsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "circuitRF");

    private static string PrefsPath => Path.Combine(PrefsDir, "preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
                return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PrefsPath), JsonOpts)
                    ?? new AppPreferences();
        }
        catch { /* corrupt prefs — start fresh */ }
        return new AppPreferences();
    }

    public static void Save(AppPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(PrefsDir);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, JsonOpts));
        }
        catch { /* non-critical — preference loss is recoverable */ }
    }

    /// <summary>Load → mutate → save in one step so partial writes never clobber other fields.</summary>
    public static void Update(Action<AppPreferences> mutate)
    {
        var prefs = Load();
        mutate(prefs);
        Save(prefs);
    }
}
