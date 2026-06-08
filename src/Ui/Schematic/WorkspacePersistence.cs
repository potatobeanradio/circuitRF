using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ── .cws workspace manifest — rev 1 ──────────────────────────────────────────
// A workspace is the collection of files that make up a project:
//   - member_files: relative paths to .csch / .cdd / .csym / .cnl etc.
//   - library_refs: paths to library manifests (.clib)
//   - dock_layout:  saved panel/tab arrangement (Dock library format)
//
// Rules (mirror .csch):
//   - format_version: reject on mismatch
//   - references, never embedded payloads
//   - relative paths preferred

public sealed class CwsFile
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>Relative or absolute paths to member files (.csch, .cdd, .csym, .cnl, …).</summary>
    public List<string> MemberFiles { get; set; } = [];

    /// <summary>Relative or absolute paths to library manifests (.clib) added to this workspace.</summary>
    public List<string> LibraryRefs { get; set; } = [];

    /// <summary>
    /// Dock layout serialized as a JSON string (Dock library format).
    /// Null when no layout has been captured yet.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DockLayout { get; set; }

    /// <summary>
    /// Name of the color scheme (.ccolor) to activate when this workspace is opened.
    /// Resolved via ThemeResolver (workspace dir → user dir → built-in assets).
    /// Null means "use the application-level preference".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorSchemeName { get; set; }
}

/// <summary>
/// Reads and writes .cws workspace manifest files.
/// Framework-free (no Avalonia) — same pattern as SchematicPersistence.
/// </summary>
public static class WorkspacePersistence
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(CwsFile ws)
        => JsonSerializer.Serialize(ws, JsonOpts);

    public static void SaveToFile(string path, CwsFile ws)
        => File.WriteAllText(path, Serialize(ws));

    public static CwsFile Deserialize(string json)
    {
        var ws = JsonSerializer.Deserialize<CwsFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .cws file.");
        if (ws.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $".cws format_version {ws.FormatVersion} does not match " +
                $"expected {CurrentFormatVersion}. Regenerate the file.");
        return ws;
    }

    public static CwsFile LoadFromFile(string path)
        => Deserialize(File.ReadAllText(path));
}
