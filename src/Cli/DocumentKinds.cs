using CircuitRF.Design.Cells;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
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
    /// <summary>A cell's 3D view, a <c>.c3d</c> (brief-em3d-41).</summary>
    ThreeD,
    Technology,
    /// <summary>A <c>.cmat</c> material library (brief-em3d-53 §1a).</summary>
    MaterialLibrary,
    EmSetup,
    /// <summary>A finished EM run's result record, a <c>.cemr</c> (brief-cosim-pattern-1 R-cp1-1).</summary>
    EmResult,
    Rail,
    Smith,
    /// <summary>A composed FET model, a <c>.cmodel</c> (brief-modelcomposer-1 R-mc1-2).</summary>
    ComposedModel,
    /// <summary>A Model Composer document, a <c>.cmc</c> (brief-modelcomposer-3 R-mc3-1).</summary>
    ComposerDocument,
    Netlist,
    AssemblyRules,
    DataDisplay,
    Touchstone,
    Interchange,
    /// <summary>A bitmapped picture — PNG, JPEG, BMP, GIF or WebP (brief-img-2 R-im2-6). Named by its kind of drawing,
    /// never called unreadable.</summary>
    Picture,
    /// <summary>A model's <c>part.cpart</c> (brief vendorlib-6 R-vl6-2a). The model FOLDER classifies as
    /// <see cref="Cell"/> — it is one — and <see cref="ModelFolder.Is"/> says it is a model as well.</summary>
    ModelPart,
    /// <summary>A model library's <c>library.clib</c> (brief vendorlib-6 R-vl6-2a).</summary>
    ModelLibraryManifest,
    /// <summary>A file with one of circuitRF's extensions whose CONTENT is another program's — the
    /// motion-capture format that also uses <c>.c3d</c> (overview §1c). Named, never reported as a
    /// broken circuitRF document.</summary>
    Foreign,
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
        DocumentKind.ThreeD        => "3d",
        DocumentKind.Technology    => "technology",
        DocumentKind.MaterialLibrary => "material-library",
        DocumentKind.EmSetup       => "em-setup",
        DocumentKind.EmResult      => "em-result",
        DocumentKind.Rail          => "rail",
        DocumentKind.Smith         => "smith",
        DocumentKind.ComposedModel => "composed-model",
        DocumentKind.ComposerDocument => "composer-document",
        DocumentKind.Netlist       => "netlist",
        DocumentKind.AssemblyRules => "assembly-rules",
        DocumentKind.DataDisplay   => "data-display",
        DocumentKind.Touchstone    => "touchstone",
        DocumentKind.Interchange   => "interchange",
        DocumentKind.Picture       => "picture",
        DocumentKind.ModelPart     => "model-part",
        DocumentKind.ModelLibraryManifest => "model-library",
        DocumentKind.Foreign       => "foreign",
        _                          => "unknown",
    };

    /// <summary>The workspace file's own name. A workspace is a DIRECTORY holding one of these
    /// (<c>WorkspaceCreate.Create</c>), which is why a bare extension test cannot find it.</summary>
    public const string CwsFileName = ".cws";

    /// <summary>
    /// Classifies <paramref name="path"/>. A directory is a workspace when it holds a
    /// <c>.cws</c>, a cell when it holds a <c>.ccell</c> or one of the three view sub-folders, and a
    /// plain folder otherwise. A model folder (one holding a <c>part.cpart</c>) is a cell folder and
    /// classifies as one even with no <c>.ccell</c>, so every verb that reads a cell reads a model; the
    /// verbs to which the difference matters ask <see cref="ModelFolder.Is"/>. A file goes by extension, and an extension circuitRF does not own is
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
            if (LooksLikeCellFolder(path) || ModelFolder.Is(path)) return DocumentKind.Cell;
            return DocumentKind.Folder;
        }

        string name = Path.GetFileName(path);
        if (string.Equals(name, CwsFileName, StringComparison.OrdinalIgnoreCase))
            return DocumentKind.Workspace;
        // By NAME: both manifests have a fixed name, and their extensions belong to nothing else.
        if (string.Equals(name, ModelFolder.PartFileName, StringComparison.OrdinalIgnoreCase))
            return DocumentKind.ModelPart;
        if (string.Equals(name, ModelFolder.LibraryFileName, StringComparison.OrdinalIgnoreCase))
            return DocumentKind.ModelLibraryManifest;

        var byExtension = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csch"  => DocumentKind.Schematic,
            ".csym"  => DocumentKind.Symbol,
            ".clay"  => DocumentKind.Layout,
            // BY CONTENT, whatever contentSniff says: the extension is shared with a motion-capture
            // format, and a file from that program must be named foreign rather than reported as a
            // broken 3D view. The sniff reads a JSON prefix and stops — a binary file on its first byte.
            ".c3d"   => C3dPersistence.LooksLikeC3d(path) ? DocumentKind.ThreeD : DocumentKind.Foreign,
            ".ctech" => DocumentKind.Technology,
            ".cmat"  => DocumentKind.MaterialLibrary,
            ".cem"   => DocumentKind.EmSetup,
            // How a finished run was made and where its files are (brief-cosim-pattern-1 R-cp1-1).
            ".cemr"  => DocumentKind.EmResult,
            // A railRF document (brief-railrf-1-document.md R-rail1-11). `check`, `explain`, `find`
            // and `render` classify it BY KIND rather than calling it unreadable; the `rail` verb
            // that runs one is brief 10's.
            ".crail" => DocumentKind.Rail,
            // A Smith Chart design (brief-smith-1-document.md R-smith1-8). Classified here
            // so `check`, `explain` and `find` name it rather than calling it unreadable; the
            // `smith` verb that EVALUATES one is brief 10's.
            ".csmith" => DocumentKind.Smith,
            // A composed FET model (brief-modelcomposer-1 R-mc1-2). A ZIP container, so classified by
            // name; `check` reads it through the model's own validator.
            ".cmodel" => DocumentKind.ComposedModel,
            // A Model Composer document (brief-modelcomposer-3 R-mc3-1); `check` reads it through
            // CmcFile, the validator the window opens it with.
            ".cmc"   => DocumentKind.ComposerDocument,
            ".cnl"   => DocumentKind.Netlist,
            ".wasm"  => DocumentKind.AssemblyRules,
            // A data display. `render` draws one (RND-4); `check`/`explain` do not read it yet, and
            // classify it rather than calling it unknown so the refusal names what it IS.
            ".cdd"   => DocumentKind.DataDisplay,
            _        => DocumentKind.Unknown,
        };

        if (byExtension != DocumentKind.Unknown) return byExtension;

        // A picture by its extension (brief-img-2 R-im2-6) — whatever the bytes turn out to be: a `.png` that does not
        // decode is a broken picture, which is check's finding, not an unknown file.
        if (RasterImage.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return DocumentKind.Picture;

        // Touchstone is `.sNp` for any N, so it cannot be a row in the table above. The port count
        // in the extension IS the classification — a file named `.s2p` claims to be a 2-port and is
        // checked against that claim, which is one of the findings.
        if (TouchstoneIO.ParsePortsFromExtension(path) is > 0) return DocumentKind.Touchstone;

        // Not one of ours by name. Ask the verb that owns the foreign formats, rather than repeating
        // its table here — including its content sniff, which is the only thing that can name a
        // Gerber or an Excellon file (a toolchain names those however it likes).
        if (!contentSniff) return DocumentKind.Unknown;
        // A picture whose extension is missing or wrong is named by its signature — before convert's classifier, which
        // reads text formats and has no business guessing at a PNG's bytes.
        if (RasterImage.DetectFormat(path) is not null) return DocumentKind.Picture;
        return LayoutConvert.DetectSource(path) is not null ? DocumentKind.Interchange : DocumentKind.Unknown;
    }

    /// <summary>The interchange format a path holds, as <c>convert</c> names it, or null.</summary>
    public static string? InterchangeFormat(string path) =>
        LayoutConvert.DetectSource(path) is { } f ? LayoutConvert.Name(f) : null;

    /// <inheritdoc cref="CellLookup.LooksLikeCellFolder"/>
    public static bool LooksLikeCellFolder(string dir) => CellLookup.LooksLikeCellFolder(dir);

    public static readonly ViewType[] AllViewTypes = Enum.GetValues<ViewType>();

    /// <summary>
    /// The view types <c>render</c> and <c>explain --extents</c> can draw or measure. A 3D view is
    /// not one of them yet: its picture is the sections brief 42 draws from its elaboration, and until
    /// then a cell whose only view is 3D has nothing to draw — said, not guessed.
    /// </summary>
    public static readonly ViewType[] DrawableViewTypes = [ViewType.Schematic, ViewType.Symbol, ViewType.Layout];

    /// <summary>The nearest ancestor <c>.cws</c>, or null. The same walk every document-relative
    /// reference in circuitRF resolves through (<c>cli.md</c> §8.1).</summary>
    public static string? AncestorCws(string documentPath) =>
        WorkspaceRootFinder.FindAncestorCws(
            Directory.Exists(documentPath)
                ? Path.GetFullPath(documentPath)
                : Path.GetDirectoryName(Path.GetFullPath(documentPath)));
}
