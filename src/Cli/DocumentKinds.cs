using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using RfCore;

namespace CircuitRF.Cli;

/// <summary>What a path was taken to be. The wire spelling is the enum's name, lower-kebab.</summary>
internal enum DocumentKind
{
    Workspace,
    Cell,
    Folder,
    Schematic,
    Symbol,
    Layout,
    Technology,
    EmSetup,
    Rail,
    Netlist,
    AssemblyRules,
    DataDisplay,
    Touchstone,
    Interchange,
    Unknown,
}

/// <summary>
/// One rule for "what IS this path?", shared by <c>check</c> and <c>explain</c>
/// (brief-automation-4-check-and-explain.md R-aut4-11).
///
/// <para><b>Why one function and not one verb per type.</b> R-aut-9: the caller has a path and wants
/// it looked at, and making it say which KIND of document it holds is asking it to know something it
/// came here to be told. The inference is by extension, and for a directory by what it contains —
/// exactly as <c>convert</c> already infers a format, and for the interchange formats literally
/// THROUGH <c>convert</c>'s own classifier rather than a second copy of it. A GDSII file that
/// <c>convert</c> can read must not be reported here as a file circuitRF does not handle.</para>
/// </summary>
internal static class DocumentKinds
{
    /// <summary>The wire spelling — what lands in a JSON document's <c>kind</c> field.</summary>
    public static string Name(DocumentKind k) => k switch
    {
        DocumentKind.Workspace     => "workspace",
        DocumentKind.Cell          => "cell",
        DocumentKind.Folder        => "folder",
        DocumentKind.Schematic     => "schematic",
        DocumentKind.Symbol        => "symbol",
        DocumentKind.Layout        => "layout",
        DocumentKind.Technology    => "technology",
        DocumentKind.EmSetup       => "em-setup",
        DocumentKind.Rail          => "rail",
        DocumentKind.Netlist       => "netlist",
        DocumentKind.AssemblyRules => "assembly-rules",
        DocumentKind.DataDisplay   => "data-display",
        DocumentKind.Touchstone    => "touchstone",
        DocumentKind.Interchange   => "interchange",
        _                          => "unknown",
    };

    /// <summary>The workspace file's own name. A workspace is a DIRECTORY holding one of these
    /// (<c>WorkspaceCreate.Create</c>), which is why a bare extension test cannot find it.</summary>
    public const string CwsFileName = ".cws";

    /// <summary>
    /// Classifies <paramref name="path"/>. A directory is a workspace when it holds a
    /// <c>.cws</c>, a cell when it holds a <c>.ccell</c> or one of the three view sub-folders, and a
    /// plain folder otherwise. A file goes by extension, and an extension circuitRF does not own is
    /// offered to <c>convert</c>'s classifier — which reads CONTENT — before being called unknown.
    /// </summary>
    /// <param name="contentSniff">
    /// Whether to ask <c>convert</c>'s classifier to READ an unrecognised file. True when the caller
    /// NAMED the path — a Gerber or Excellon file is named however its toolchain felt like, so the
    /// content is the only thing that can identify one. False when walking a folder, where the
    /// unrecognised files are somebody's `.md`, `.s2p` and `.DS_Store`: opening each of them to
    /// discover it is not Gerber is a read per file for an answer nothing acts on.
    /// </param>
    public static DocumentKind Classify(string path, bool contentSniff = true)
    {
        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, CwsFileName))) return DocumentKind.Workspace;
            if (LooksLikeCellFolder(path))                    return DocumentKind.Cell;
            return DocumentKind.Folder;
        }

        string name = Path.GetFileName(path);
        if (string.Equals(name, CwsFileName, StringComparison.OrdinalIgnoreCase))
            return DocumentKind.Workspace;

        var byExtension = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csch"  => DocumentKind.Schematic,
            ".csym"  => DocumentKind.Symbol,
            ".clay"  => DocumentKind.Layout,
            ".ctech" => DocumentKind.Technology,
            ".cem"   => DocumentKind.EmSetup,
            // A railRF document (brief-railrf-1-document.md R-rail1-11). `check`, `explain`, `find`
            // and `render` classify it BY KIND rather than calling it unreadable; the `rail` verb
            // that runs one is brief 10's.
            ".crail" => DocumentKind.Rail,
            ".cnl"   => DocumentKind.Netlist,
            ".wasm"  => DocumentKind.AssemblyRules,
            // A data display. `render` draws one (RND-4); `check`/`explain` do not read it yet, and
            // classify it rather than calling it unknown so the refusal names what it IS.
            ".cdd"   => DocumentKind.DataDisplay,
            _        => DocumentKind.Unknown,
        };

        if (byExtension != DocumentKind.Unknown) return byExtension;

        // Touchstone is `.sNp` for any N, so it cannot be a row in the table above. The port count
        // in the extension IS the classification — a file named `.s2p` claims to be a 2-port and is
        // checked against that claim, which is one of the findings.
        if (TouchstoneIO.ParsePortsFromExtension(path) is > 0) return DocumentKind.Touchstone;

        // Not one of ours by name. Ask the verb that owns the foreign formats, rather than repeating
        // its table here — including its content sniff, which is the only thing that can name a
        // Gerber or an Excellon file (a toolchain names those however it likes).
        if (!contentSniff) return DocumentKind.Unknown;
        return LayoutConvert.DetectSource(path) is not null ? DocumentKind.Interchange : DocumentKind.Unknown;
    }

    /// <summary>The interchange format a path holds, as <c>convert</c> names it, or null.</summary>
    public static string? InterchangeFormat(string path) =>
        LayoutConvert.DetectSource(path) is { } f ? LayoutConvert.Name(f) : null;

    /// <summary>
    /// True when a directory has the SHAPE of a cell folder. Deliberately structural rather than a
    /// `.ccell` test alone: a cell folder is legal with no `.ccell` at all (primacy is implicit when
    /// a sub-folder holds exactly one file — <see cref="PrimaryState.SoleFile"/>), so requiring the
    /// manifest would classify a perfectly ordinary cell as a plain folder and check nothing in it.
    /// </summary>
    public static bool LooksLikeCellFolder(string dir)
    {
        if (File.Exists(Path.Combine(dir, CellFolder.CcellFileName))) return true;

        foreach (var t in AllViewTypes)
            if (Directory.Exists(Path.Combine(dir, CellFolder.SubFolderName(t))))
                return true;

        return false;
    }

    public static readonly ViewType[] AllViewTypes = [ViewType.Schematic, ViewType.Symbol, ViewType.Layout];

    /// <summary>The nearest ancestor <c>.cws</c>, or null. The same walk every document-relative
    /// reference in circuitRF resolves through (<c>cli.md</c> §8.1).</summary>
    public static string? AncestorCws(string documentPath) =>
        WorkspaceRootFinder.FindAncestorCws(
            Directory.Exists(documentPath)
                ? Path.GetFullPath(documentPath)
                : Path.GetDirectoryName(Path.GetFullPath(documentPath)));
}
