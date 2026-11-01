// What every entry point to Import Schematic shares — brief-schimp-5 R-si5-1…6, overview D1.
//
// The menu's dialog, the drop on an open schematic, the drop on the project tree, `circuitrf import schematic` and the
// MCP `import` tool each need the same four answers, and none of them may hold an answer of its own (the Authoring.cs
// rule; a source scan holds it):
//   Read           — a file, classified by CONTENT, through the one reader for its format;
//   FileExtensions — what the file picker and a drag-over offer, by extension only (overview D7);
//   PortCandidates — what the dialog's ports list and `--list` show (overview D9);
//   Preview        — the very build the import writes, and its connectivity check, with nothing written beside the
//                    target: the cells it needs go into a scratch folder laid out as the target is, so every relative
//                    cell reference in the drawing is the one the import writes.

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>A file read for import: the design, the format it was classified as, or why it cannot be read.</summary>
public sealed record SchematicImportRead(ForeignSchematic? Schematic, SchematicFileKind Kind, string? Refusal)
{
    public bool Ok => Schematic is not null;
}

/// <summary>One row of the dialog's ports list (overview D9): a connector part by its reference, or a net by its name.
/// <paramref name="Key"/> is what <see cref="SchematicImportOptions.Ports"/> and <c>--port</c> take.</summary>
public sealed record ImportPortCandidate(string Key, string Description, bool IsPart);

/// <summary>
/// What an import would do, before it does it (R-si5-2): the drawing, what each part becomes, the connectivity check and
/// the report — or the refusal the import would give. Disposing it deletes the scratch folder its cells were written to.
/// </summary>
public sealed class SchematicImportPreview : IDisposable
{
    private readonly string? _scratch;

    internal SchematicImportPreview(string? scratch, SchematicEditModel? drawing, IReadOnlyList<ImportPartDecision> mapping,
                                    ImportConnectivityResult? connectivity, IReadOnlyList<string> messages, string? refusal)
    {
        _scratch = scratch;
        Drawing = drawing;
        Mapping = mapping;
        Connectivity = connectivity;
        Messages = messages;
        Refusal = refusal;
    }

    /// <summary>The top schematic as the import writes it; its cells resolve from the scratch folder while this lives.
    /// Null when there is nothing to draw.</summary>
    public SchematicEditModel? Drawing { get; }

    public IReadOnlyList<ImportPartDecision> Mapping { get; }

    public ImportConnectivityResult? Connectivity { get; }

    /// <summary>The report the import would give, one sentence a line.</summary>
    public IReadOnlyList<string> Messages { get; }

    /// <summary>Why Import would write nothing, or null.</summary>
    public string? Refusal { get; }

    public bool Ok => Refusal is null;

    public void Dispose()
    {
        if (_scratch is null || !Directory.Exists(_scratch)) return;
        foreach (string cell in Directory.EnumerateDirectories(_scratch, "*", SearchOption.AllDirectories))
            CellSymbolResolver.Invalidate(cell);
        try { Directory.Delete(_scratch, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder; the OS reclaims it */ }
    }
}

public static partial class SchematicImport
{
    /// <summary>The extensions the file picker lists and a drag-over accepts (R-si5-1, R-si5-3). Extensions only — a drop
    /// is then classified by its content, so a <c>.dsn</c> that is not a schematic design is refused, not misread.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".dsn", ".edf", ".edn", ".edif", ".kicad_sch"];

    /// <summary>The file picker's filter name (overview D7 — formats by extension, never by tool).</summary>
    public const string FileFilterName = "Schematic designs";

    /// <summary>The ANSI code page an EDIF or binary design's text is read in when the caller says none.</summary>
    public const int DefaultCodePage = EdifText.DefaultCodePage;

    public static bool HasSchematicExtension(string path) =>
        FileExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a format's text is decoded through a code page — the dialog shows the field only for these.</summary>
    public static bool TakesCodePage(SchematicFileKind kind) => kind is SchematicFileKind.Edif200 or SchematicFileKind.BinaryDsn;

    /// <summary>A format's name as the dialog and the CLI say it — by extension and container (overview D7).</summary>
    public static string FormatName(SchematicFileKind kind) => kind switch
    {
        SchematicFileKind.KicadSch  => ".kicad_sch",
        SchematicFileKind.Edif200   => "EDIF 2.0.0",
        SchematicFileKind.BinaryDsn => "binary .DSN design (CFBF)",
        _                           => "not a schematic design",
    };

    /// <summary>
    /// <paramref name="path"/> classified by its content and read by its format's reader (R-si5-3, R-si5-5).
    /// <paramref name="codePage"/> applies to the formats <see cref="TakesCodePage"/> names.
    /// </summary>
    public static SchematicImportRead Read(string path, int? codePage = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) return new SchematicImportRead(null, SchematicFileKind.Unknown, $"No such file: {path}");
        var cls = SchematicFileClassifier.Classify(path);
        string name = Path.GetFileName(path);
        ForeignSchematicRead read = cls.Kind switch
        {
            SchematicFileKind.KicadSch => SchematicSexprReader.Read(path),
            SchematicFileKind.Edif200  => EdifReader.Read(path, new EdifReadOptions(codePage ?? DefaultCodePage)),
            SchematicFileKind.BinaryDsn => BinaryDsn.BinaryDsnReader.Read(path, new BinaryDsn.BinaryDsnReadOptions(codePage ?? DefaultCodePage)),
            _ => new ForeignSchematicRead(null, $"{name} cannot be imported as a schematic: {cls.Reason ?? "it is not a schematic design."}"),
        };
        return new SchematicImportRead(read.Schematic, cls.Kind, read.Refusal);
    }

    /// <summary>
    /// What may be made a circuitRF Port (overview D9), in source order: every part that will be a symbol cell and looks
    /// like a connector — by its reference prefix or its symbol's name — then every global or hierarchical net name the
    /// top level draws. None is chosen by default; the user decides what an RF port is.
    /// </summary>
    public static IReadOnlyList<ImportPortCandidate> PortCandidates(ForeignSchematic source, IReadOnlyList<ImportPartDecision>? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        mapping ??= SchematicImportMap.Decide(source);
        var rows = new List<ImportPortCandidate>();
        var connectors = mapping.Where(d => d.Becomes == ImportPartKind.SymbolCell && LooksLikeConnector(d)).ToList();

        // Only a connector the build would make a Port is offered — one signal pin and at most one other net off ground.
        // A ten-pin header ticked here used to be refused after the user had asked for it. The build writes nothing.
        if (connectors.Count > 0)
        {
            string nowhere = Path.Combine(Path.GetTempPath(), "circuitrf-port-candidates");
            var trial = SchematicImportBuild.Build(source, "candidates", nowhere, nowhere,
                                                   new SchematicImportOptions { Ports = [.. connectors.Select(d => d.Reference)] });
            if (trial.Ok) connectors = [.. connectors.Where(d => trial.PortParts.Contains(d.Reference))];
        }
        foreach (var d in connectors)
            rows.Add(new ImportPortCandidate(d.Reference, $"{d.Reference} ({d.SymbolName})", IsPart: true));

        var sheets = source.Hierarchical ? source.Pages.Take(1) : source.Pages;
        var nets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in sheets.SelectMany(s => s.Labels))
            if (label.Kind is ForeignLabelKind.Global or ForeignLabelKind.Hierarchical
                && label.Text.Trim() is { Length: > 0 } text && !SchematicImportMap.IsGroundName(text) && nets.Add(text))
                rows.Add(new ImportPortCandidate(text, $"net {text}", IsPart: false));
        return rows;
    }

    private static readonly string[] ConnectorPrefixes = ["J", "P", "CN", "CON", "X"];

    private static bool LooksLikeConnector(ImportPartDecision d)
    {
        string prefix = new([.. d.Reference.TakeWhile(char.IsLetter)]);
        return ConnectorPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(d.SymbolName, @"conn|(?<![a-z])(?:sma|bnc)(?![a-z])|coax|port",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>Why <see cref="Import"/> would refuse <paramref name="cellName"/> under <paramref name="parentDir"/>
    /// before building anything, or null — the dialog validates its name field live with this.</summary>
    public static string? CellNameRefusal(string parentDir, string? cellName)
    {
        cellName = (cellName ?? "").Trim();
        if (NameValidator.Validate(cellName) is { } bad) return $"'{cellName}' cannot be a cell name: {bad}";
        string parent = Path.GetFullPath(parentDir);
        string cellDir = Path.Combine(parent, cellName);
        return Directory.Exists(cellDir) || File.Exists(cellDir)
            ? $"A cell named '{cellName}' already exists in {parent}; choose another name."
            : null;
    }

    /// <summary>
    /// What <see cref="Import"/> would do with the same arguments, writing nothing beside <paramref name="parentDir"/>
    /// (R-si5-2): the same build, the same plan against the cells already there, and the connectivity check run against
    /// a scratch copy of the cells it would write. A cell name the import would refuse is reported, and the drawing is
    /// still made under a stand-in name so there is something to look at while it is corrected.
    /// </summary>
    public static SchematicImportPreview Preview(ForeignSchematic source, string parentDir, string cellName,
                                                 SchematicImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new SchematicImportOptions();
        string? nameRefusal = CellNameRefusal(parentDir, cellName);
        string name = NameValidator.Validate((cellName ?? "").Trim()) is null ? cellName!.Trim() : "Imported";

        string scratch = Path.Combine(Path.GetTempPath(), "crf-schimport-" + Guid.NewGuid().ToString("N")[..12]);
        string scratchParts = Path.Combine(scratch, PartsFolderName(source));
        string scratchSchematic = CellFolder.SubFolderPath(Path.Combine(scratch, name), ViewType.Schematic);
        var built = SchematicImportBuild.Build(source, name, scratchSchematic, scratchParts, options);
        if (!built.Ok)
            return new SchematicImportPreview(null, null, built.Mapping, null, built.Notes, string.Join(" ", built.Refusals));

        // The plan against the REAL folder: which cells would be written and which reused, and whether a cell that is
        // there but differs would refuse the import.
        var plan = Plan(built, Path.Combine(Path.GetFullPath(parentDir), PartsFolderName(source)));
        try
        {
            Directory.CreateDirectory(scratch);
            WriteCells(built, scratchParts, Plan(built, scratchParts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SchematicImportPreview(scratch, built.Top!.Model, built.Mapping, null, built.Notes,
                                              $"The preview could not be built: {ex.Message}");
        }
        var top = built.Top!.Model;
        top.SchematicDirectory = scratchSchematic;
        var connectivity = ImportConnectivity.Compare(source, built);
        var messages = Report(built, plan, connectivity);
        return new SchematicImportPreview(scratch, top, built.Mapping, connectivity, messages, nameRefusal ?? plan.Refusal);
    }
}
