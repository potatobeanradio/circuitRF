using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ── .cws workspace manifest — rev 2 ──────────────────────────────────────────
// A workspace is the collection of files that make up a project:
//   - member_files: relative paths to .csch / .cdd / .csym / .cnl etc.
//   - library_refs: paths to library manifests (.clib)
//   - dock_layout:  saved panel/tab arrangement (Dock library format)
//
// Rules (mirror .csch):
//   - format_version: reject on mismatch
//   - references, never embedded payloads
//   - relative paths preferred

/// <summary>
/// One open document entry persisted in .cws — path, kind, and tab order.
/// Restored when the workspace is next opened.
/// </summary>
public sealed class CwsOpenDocument
{
    /// <summary>Relative (preferred) or absolute path to the document file or cell folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>"schematic" (.csch), "symbol" (.csym), or "cell" (cell folder).</summary>
    public string Kind { get; set; } = "schematic";

    /// <summary>Zero-based tab order used to restore the original tab sequence.</summary>
    public int TabOrder { get; set; }
}

/// <summary>
/// Tree view-state persisted in .cws — filter category flags + ordering preference.
/// Ordering is alphabetical-only in v1; the field is reserved for a future ordering UI (§3.1).
/// </summary>
public sealed class CwsTreeViewState
{
    public bool Cells               { get; set; } = true;
    public bool Libraries           { get; set; } = true;
    public bool TestBenches         { get; set; } = true;
    public bool DataDisplays        { get; set; } = true;
    public bool ColorThemes         { get; set; } = true;
    public bool KnownFiles          { get; set; } = true;
    public bool WorkspaceFileSystem { get; set; } = true;

    /// <summary>Ordering mode; "alphabetical" is the only valid value in v1.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Ordering { get; set; }
}

public sealed class CwsFile
{
    public int FormatVersion { get; set; } = 2;

    /// <summary>
    /// Relative or absolute paths to external library folders (or legacy .clib manifest files).
    /// Resolved by the scanner relative to the workspace root when relative.
    /// </summary>
    public List<string> LibraryRefs { get; set; } = [];

    /// <summary>
    /// Relative or absolute paths to files bookmarked for convenient access (§5).
    /// Unresolvable paths produce a KnownFile node with a WarningReason.
    /// </summary>
    public List<string> KnownFiles { get; set; } = [];

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

    /// <summary>
    /// Project Tree filter category flags + ordering, restored on open.
    /// Null means "use defaults" (all categories on, alphabetical ordering).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CwsTreeViewState? TreeViewState { get; set; }

    /// <summary>
    /// Documents open in the main DocumentDock when the workspace was last saved.
    /// Null or empty means no documents to restore (welcome stub is shown).
    /// Scratch documents are never persisted here.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsOpenDocument>? OpenDocuments { get; set; }

    /// <summary>
    /// Relative (preferred) or absolute path of the active document when the workspace
    /// was last saved.  Null when no named document was active.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveDocumentPath { get; set; }
}

/// <summary>
/// Reads and writes .cws workspace manifest files.
/// Framework-free (no Avalonia) — same pattern as SchematicPersistence.
/// </summary>
public static class WorkspacePersistence
{
    public const int CurrentFormatVersion = 2;

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

    /// <summary>
    /// Atomic write: serializes to a temp file then renames over the target.
    /// A crash mid-write leaves the old file intact (never a half-written .cws).
    /// </summary>
    public static void SaveToFileAtomic(string path, CwsFile ws)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(ws));
        File.Move(tmp, path, overwrite: true);
    }

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
