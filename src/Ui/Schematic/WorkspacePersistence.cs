using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ── .cws workspace manifest — rev 2 ──────────────────────────────────────────
// A workspace is the collection of files that make up a project:
//   - member_files: relative paths to .csch / .cdd / .csym / .cnl etc.
//   - library_refs: paths to library manifests (.clib)
//   - dock_layout:  saved panel/tab/floating-window arrangement (OUR schema — see CwsDockLayout)
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
    public bool TechFiles           { get; set; } = true;
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
    /// Saved dock arrangement — panels, tabbed groups, floating windows, document tab order.
    ///
    /// <para>This is <b>our</b> schema (<c>CircuitRF.Ui.Docking.CwsDockLayout</c>), never the docking
    /// library's serialized object graph (brief-dock-layout-persistence.md R-dock-3): <c>.cws</c> is a
    /// human-readable, long-lived file, and a third-party library's graph is neither — it is opaque to
    /// a reader, and a library upgrade can invalidate every saved workspace in the field.</para>
    ///
    /// <para>Typed as a raw <see cref="JsonNode"/> on purpose (R-dock-5): a structurally malformed
    /// block must not take the rest of the <c>.cws</c> — the tree state and the open-document list —
    /// down with it. <c>DockLayoutSerialization.TryRead</c> parses it separately behind its own
    /// try/catch, so a layout problem can never prevent a workspace from opening. It also means a
    /// block written by a newer build round-trips verbatim instead of being rewritten to a lossy
    /// subset. Null when no layout has been captured yet.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? DockLayout { get; set; }

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

    /// <summary>
    /// Relative path (from the workspace root) to the .ctech that TechnologyResolver falls back to
    /// when a .clay's own TechRef is null (docs/design/layout-view.md §2.4 "one default per
    /// workspace"). Null means "no default" — a valid state that resolves to the fallback palette.
    /// No FormatVersion bump: an absent field on an older .cws loads gracefully as null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultTechRef { get; set; }

    /// <summary>
    /// Relative path (from the workspace root) to the `.wasm` assembly rule file
    /// <c>WasmResolver</c> falls back to when a `.wBond`'s own <c>AssemblyRef</c> is null
    /// (docs/design/wbond.md §8, WB31).
    ///
    /// <para>Null means "no assembly rules", which is a valid state and NOT an error: a design with
    /// no house stated simply has its die-side rules checked and its wire geometry validated. It sits
    /// beside <see cref="DefaultTechRef"/> rather than inside the `.ctech` because the relation
    /// between assembly houses and process technologies is many-to-many and their lifecycles differ —
    /// that is the whole of WB31.</para>
    ///
    /// <para>No FormatVersion bump: an absent field on an older `.cws` loads gracefully as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultAssemblyRef { get; set; }

    /// <summary>
    /// The Python interpreter this workspace settled on for PCell generator scripts — the command,
    /// with any prefix arguments after it (e.g. <c>py.exe -3</c>).
    ///
    /// <para><b>An automatically-made decision, recorded so it is visible and one line to
    /// correct.</b> Discovery probes candidates by RUNNING each one, which costs a process launch
    /// apiece; the answer does not change between sessions, so it is replayed rather than
    /// re-derived — the same bargain a kit's own settings already strike, where the measured
    /// difference was 0.5 ms against 199.8 ms. A recorded interpreter that no longer works is
    /// re-derived and re-recorded rather than treated as fatal: an interpreter can be upgraded or
    /// removed between sessions, and the workspace should heal rather than need the user to know
    /// that is what happened.</para>
    ///
    /// <para>Null means "not settled yet". No <c>FormatVersion</c> bump — an absent field on an
    /// older <c>.cws</c> loads as null and discovery simply runs.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PythonInterpreter { get; set; }

    /// <summary>
    /// PDKs this workspace references. An import writes nothing into the workspace — a kit's
    /// translated symbols and parameter interfaces are the vendor's content and are rebuilt in
    /// memory on open (docs/design/pdk-import.md). Null or empty means no kits.
    ///
    /// <para>No FormatVersion bump: an absent field on an older .cws loads as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsPdkRef>? PdkRefs { get; set; }
}

/// <summary>
/// One referenced PDK: where it is, and what circuitRF settled about it.
///
/// <para><b>Decisions are recorded; translations are not.</b> An import both translates (symbols,
/// parameter interfaces, icons) and decides (which of a dozen library builds, which variant is the
/// default). The translations are the vendor's content and are rebuilt on open. The decisions are
/// tiny, carry no geometry, and are the difference between a workspace that opens the same way twice
/// and one that quietly re-decides — and re-deciding is also the only part with a cost worth caring
/// about (library discovery byte-scans candidate builds).</para>
/// </summary>
public sealed class CwsPdkRef
{
    /// <summary>
    /// The kit's folder — workspace-relative when inside the workspace, absolute otherwise, via
    /// <see cref="WorkspaceRefs.ToStoredRef"/>. A kit is normally outside, so absolute is the common
    /// case, which is why a broken one has to be repairable rather than merely reported.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The name a netlist asks for this kit by, and the name its parts' virtual references carry.
    /// Not cosmetic: change it and every placed part stops resolving.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// Which reader translated this kit's symbols. Pins snap to the P=100 connection grid, so a
    /// reader change moves pins — and wires attached to them silently disconnect. The frozen
    /// on-disk symbol used to prevent that; with the translation rebuilt on every open this is what
    /// replaces it. A mismatch is reported and refused, never applied silently.
    /// </summary>
    public int TranslationVersion { get; set; }

    /// <summary>
    /// What circuitRF worked out about how to simulate this kit — the same object a
    /// <c>device-provider.json</c> holds, kept here instead of written beside the kit. Null for a
    /// purely schematic kit with nothing compiled to serve.
    ///
    /// <para>A raw <see cref="JsonNode"/> for the same reason <see cref="CwsFile.DockLayout"/> is
    /// one: a malformed block must not take the rest of the <c>.cws</c> with it, and a block written
    /// by a newer build round-trips verbatim rather than through a lossy subset.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Settings { get; set; }

    /// <summary>
    /// True when this reference is a MODEL-LIBRARY PACKAGE rather than a part kit — a folder that
    /// supplies no placeable parts but does hold the compiled libraries other kits' devices need.
    ///
    /// <para>It exists because a delivery is several part kits beside one shared library package, and
    /// discovery finds that package by ADJACENCY. Reference a kit from anywhere else — a workspace,
    /// say — and the adjacency is gone with nothing on disk left to recover it from. This is the
    /// workspace saying where the models are.</para>
    ///
    /// <para>Absent in an existing <c>.cws</c> reads as false, which is the part-kit case.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLibraryOnly { get; set; }
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
        => AtomicFile.WriteAllText(path, Serialize(ws));

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
