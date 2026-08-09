using System.Text.RegularExpressions;

namespace CircuitRF.Core.Pdk;

/// <summary>
/// Classifies one file found in a kit. Returns null when it has no opinion.
///
/// <para>The set of recognisers is open on purpose. circuitRF ships generic ones — formats that are
/// industry standards or plainly self-describing — and a host or a device provider registers more
/// for whatever else it can read. That is what keeps circuitRF free of knowledge about any
/// particular supplier while still importing their kits.</para>
/// </summary>
public interface IPdkFormatRecognizer
{
    /// <summary>Ordering hint; higher runs first, so a specific recogniser can pre-empt a general one.</summary>
    int Priority => 0;

    /// <summary>
    /// Classify <paramref name="relativePath"/>. <paramref name="peekText"/> returns the first few
    /// KB of the file as text (empty for binary or unreadable files) so a recogniser can look
    /// inside rather than trusting an extension.
    /// </summary>
    PdkAsset? Recognize(string relativePath, Func<string> peekText);
}

/// <summary>
/// Where format recognisers live. Ships a generic built-in set; hosts add to it.
/// </summary>
public static class PdkFormatRegistry
{
    private static readonly List<IPdkFormatRecognizer> _extra = [];

    public static void Register(IPdkFormatRecognizer r)
    {
        lock (_extra) _extra.Add(r);
    }

    public static void Clear()
    {
        lock (_extra) _extra.Clear();
    }

    public static IReadOnlyList<IPdkFormatRecognizer> All
    {
        get
        {
            lock (_extra)
                return [.. _extra.Concat(BuiltIn).OrderByDescending(r => r.Priority)];
        }
    }

    /// <summary>
    /// The generic recognisers. Every one keys off an industry-standard format or a self-describing
    /// file, never off a supplier — see <see cref="PdkAsset.FormatName"/>.
    /// </summary>
    public static readonly IPdkFormatRecognizer[] BuiltIn =
    [
        new ExtensionRecognizer(),
        new CellDatabaseRecognizer(),
        new ComponentCatalogRecognizer(),
        new SymbolRecordFileRecognizer(),
        new SpiceNetlistRecognizer(),
        new LayerTechnologyRecognizer(),
    ];
}

/// <summary>Classifies by file extension — the common, boring cases.</summary>
internal sealed class ExtensionRecognizer : IPdkFormatRecognizer
{
    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        string ext  = name.Contains('.') ? name[name.LastIndexOf('.')..].ToLowerInvariant() : "";

        // Touchstone: .s1p … .s99p
        if (ext.Length >= 4 && ext[1] == 's' && ext[^1] == 'p' &&
            int.TryParse(ext[2..^1], out int np) && np > 0)
            return new(path, PdkAssetKind.NetworkData, PdkAssetSupport.Supported,
                       "Touchstone network data", $"{np}-port");

        return ext switch
        {
            ".net" or ".inc" or ".ckt" or ".cir" =>
                new(path, PdkAssetKind.Netlist, PdkAssetSupport.Supported, "subcircuit netlist"),

            ".bmp" or ".png" or ".gif" or ".ico" =>
                new(path, PdkAssetKind.PaletteIcon, PdkAssetSupport.Supported, "palette icon"),

            ".gds" or ".gds2" or ".gdsii" =>
                new(path, PdkAssetKind.LayoutArtwork, PdkAssetSupport.RecognizedNotSupported,
                    "GDSII stream", "Layout geometry in a standard interchange format."),

            ".oas" =>
                new(path, PdkAssetKind.LayoutArtwork, PdkAssetSupport.RecognizedNotSupported,
                    "OASIS stream", "Layout geometry in a standard interchange format."),

            // A binary symbol LIBRARY — several named symbols in one file, referenced by name.
            // Keyed by extension because the payload is binary and the peek returns nothing for it.
            ".syf" =>
                new(path, PdkAssetKind.SymbolLibrary, PdkAssetSupport.Supported,
                    "symbol library (binary)",
                    "Holds several named symbols that parts reference by name. circuitRF reads each " +
                    "symbol's terminals and their positions."),

            ".dsn" =>
                new(path, PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                    "symbol description (.dsn)",
                    "A text, record-based drawing description. circuitRF reads its geometry and pins " +
                    "and installs it as a placeable symbol."),

            ".mdl" or ".mds" or ".dat" =>
                new(path, PdkAssetKind.ModelData, PdkAssetSupport.RecognizedNotSupported,
                    "device model data",
                    "Read by a device provider, not by circuitRF. Register a provider that " +
                    "declares this kit's device types to use it."),

            // A compiled model library. circuitRF never loads one into its own process — a device
            // provider runs it out of process — but recognising it is what turns "the parts do not
            // simulate" into a message at IMPORT time rather than a failure at Run.
            ".dll" or ".so" or ".dylib" =>
                new(path, PdkAssetKind.ModelData, PdkAssetSupport.RecognizedNotSupported,
                    "compiled model library",
                    "The parts' behaviour is compiled into this library. It is evaluated by a " +
                    "device provider in a separate process, never loaded by circuitRF itself."),

            // The SAME thing under another extension: an .osdi is the loader's own shared-object
            // format, holding one or more compiled Verilog-A modules. circuitRF already finds these
            // (OsdiModelDiscovery) and routes a .model card to whichever one implements it, so
            // reporting them as "unknown (.osdi)" told the user their kit's models were unrecognised
            // at the very moment those models were what made the kit simulate.
            ".osdi" =>
                new(path, PdkAssetKind.ModelData, PdkAssetSupport.Supported,
                    "compiled Verilog-A model",
                    "Built from the kit's own Verilog-A sources. circuitRF matches the modules it " +
                    "declares against the model cards this kit's devices name, and evaluates them in " +
                    "a separate process — it is never loaded into circuitRF itself."),

            ".pdf" or ".txt" or ".md" or ".html" or ".htm" =>
                new(path, PdkAssetKind.Documentation, PdkAssetSupport.Supported, "documentation"),

            _ when name.Equals(".spiceinit", StringComparison.OrdinalIgnoreCase) =>
                new(path, PdkAssetKind.Other, PdkAssetSupport.RecognizedNotSupported,
                    "simulator start-up file",
                    "Set-up for the simulator this kit was written for — search paths, and which " +
                    "compiled models to load. circuitRF works those out for itself from the kit's own " +
                    "netlists and artefacts, so nothing here needs running."),

            _ => null,
        };
    }
}

/// <summary>
/// A plain-text RECORD symbol file: one symbol per file, each line a single-letter record with its
/// fields and a braced attribute block.
///
/// <para><b>Recognised by its STRUCTURE, never by its extension or by the tool named in its first
/// line.</b> The extension is shared with several unrelated formats, and keying off the writing
/// tool's name would put a particular editor's identity into circuitRF — and would stop recognising
/// the same format the moment a kit generated it with something else. The grammar is the thing that
/// is actually stable.</para>
///
/// <para>Runs at a higher priority than the extension recogniser so a symbol whose extension is also
/// claimed elsewhere is classified by what it contains.</para>
/// </summary>
internal sealed class SymbolRecordFileRecognizer : IPdkFormatRecognizer
{
    /// <summary>
    /// This recogniser's own classification. Named rather than repeated as a literal, because part
    /// discovery has to be able to tell a symbol IT can read from one classified as artwork some
    /// other reader handles — the two fail for different reasons and need different messages.
    /// </summary>
    public const string Format = "symbol record file (text)";

    public int Priority => 20;

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string text = peek();
        if (text.Length == 0 || !KitSymbolFileReader.LooksLikeSymbolFile(text)) return null;

        return new PdkAsset(path, PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported, Format,
                            "One symbol per file. circuitRF reads its terminals, their names, and " +
                            "the part's parameter interface with the kit's own defaults, and " +
                            "installs it as a placeable symbol.");
    }
}

/// <summary>
/// A netlist or model library in the SPICE dialect, recognised by the DIRECTIVES it contains rather
/// than by its extension.
///
/// <para><b>An extension list would not have worked, and that is the reason this exists.</b> Kits of
/// this shape spell the same content <c>.lib</c>, <c>.sp</c>, <c>.spice</c>, <c>.mod</c>, <c>.cir</c>
/// and <c>.net</c> — sometimes several within one kit — and <c>.lib</c> in particular is claimed by
/// unrelated formats elsewhere. A file that declares a subcircuit or a model card IS one, whatever it
/// is called, and a file that does not is not one however it is spelled.</para>
///
/// <para>Directives are matched at the START of a line only. A mention of <c>.model</c> inside prose
/// or a path is not a declaration, and reading documentation as a netlist would put phantom parts in
/// a kit's palette.</para>
/// </summary>
internal sealed class SpiceNetlistRecognizer : IPdkFormatRecognizer
{
    public int Priority => 15;

    /// <summary>
    /// The directives that mark a file as this dialect's, at line start.
    ///
    /// <para><b><c>.lib</c> is here for a reason worth stating.</b> A file that declares CORNERS
    /// contains no subcircuit and no model card at all — it is nothing but <c>.lib</c> sections, each
    /// binding a few parameters and including the shared model file. Keyed on the first two markers
    /// alone such a file classifies as unrecognised, so the corners it declares are invisible to the
    /// import and there is no way to know a kit offers any. Measured: several of its
    /// corner files were landing in the import as unrecognised for exactly this reason.</para>
    ///
    /// <para>Deliberately not widened further. <c>.param</c> and <c>.include</c> would also match a
    /// corner file, and both are far likelier to appear at line start in something that is not a
    /// netlist at all — <c>.lib</c> is the one that means "this file participates in this dialect's
    /// own section mechanism", which is the thing being recognised.</para>
    /// </summary>
    private static readonly Regex Directive =
        new(@"^\s*\.(subckt|model|lib)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string text = peek();
        if (text.Length == 0 || !Directive.IsMatch(text)) return null;

        return new PdkAsset(path, PdkAssetKind.Netlist, PdkAssetSupport.Supported,
                            "subcircuit netlist (SPICE dialect)",
                            "circuitRF reads its subcircuits as ordinary cells, its model cards as " +
                            "the parameter sets the devices in them refer to, and its .lib sections " +
                            "as the alternatives — corners — the kit offers.");
    }
}

/// <summary>
/// Binary cell-database views — a per-cell directory holding one file per view
/// (<c>symbol/symbol.oa</c>, <c>layout/layout.oa</c>). Classified by the VIEW directory rather than
/// the extension, so the artwork's role is known even though the payload cannot be read.
/// </summary>
internal sealed class CellDatabaseRecognizer : IPdkFormatRecognizer
{
    public int Priority => 10;

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        var parts = path.Split('/');
        if (parts.Length < 2) return null;
        string view = parts[^2].ToLowerInvariant();
        string file = parts[^1].ToLowerInvariant();

        if (!file.EndsWith(".oa", StringComparison.Ordinal)) return null;

        const string detail =
            "A binary cell-database view. The format is not publicly documented and reading it " +
            "needs a licensed native library, so circuitRF cannot open it. If you need this " +
            "artwork, export it from the tool that wrote it — GDSII for layout.";

        return view switch
        {
            "layout" => new(path, PdkAssetKind.LayoutArtwork, PdkAssetSupport.RecognizedNotSupported,
                            "binary cell view (layout)", detail),
            "symbol" => new(path, PdkAssetKind.SymbolArtwork, PdkAssetSupport.RecognizedNotSupported,
                            "binary cell view (symbol)", detail),
            _        => new(path, PdkAssetKind.Other, PdkAssetSupport.RecognizedNotSupported,
                            $"binary cell view ({view})", detail),
        };
    }
}

/// <summary>
/// An XML catalog declaring the parts a kit offers: repeated <c>&lt;COMPONENT&gt;</c> entries, each
/// naming a part and pointing at its symbol and icon.
///
/// <para>Why this is worth recognising: a kit need not have a netlist OR a cell-database tree. One
/// can declare its parts here and supply their behaviour from a compiled model library instead —
/// and such a kit is otherwise invisible to part discovery, which produces an empty palette with no
/// explanation of why.</para>
///
/// <para>Recognised STRUCTURALLY, by the repeated component element, never by a schema URI or a
/// namespace. A namespace names the tool that wrote the file, and keying off one would put supplier
/// knowledge into circuitRF and would fail on the next kit that used the same shape.</para>
/// </summary>
internal sealed class ComponentCatalogRecognizer : IPdkFormatRecognizer
{
    public int Priority => 15;

    /// <summary>
    /// A component element that actually NAMES something. Requiring the name attribute — rather
    /// than counting bare mentions of the element — is what separates a catalog from a document
    /// that merely talks about one, and it does so without ruling out a kit that offers a single
    /// part. Counting mentions was tried first and rejected exactly one such catalog.
    /// </summary>
    internal static readonly Regex RxComponent = new(
        @"<\s*(?:\w+:)?COMPONENT\b[^>]*\bName\s*=\s*""[^""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        string head = peek();
        if (head.Length == 0 || !RxComponent.IsMatch(head)) return null;

        return new(path, PdkAssetKind.ComponentCatalog, PdkAssetSupport.Supported,
                   "component catalog (XML)",
                   "The kit's own list of the parts it offers. Read to populate the palette.");
    }
}

/// <summary>
/// Layer definitions: a stream-number map, or an XML technology file listing layer/purpose pairs.
/// These are worth singling out — they are plain text, they map almost directly onto circuitRF's own
/// technology model, and they are useful even when a kit ships no geometry at all.
/// </summary>
internal sealed class LayerTechnologyRecognizer : IPdkFormatRecognizer
{
    public int Priority => 20;

    public PdkAsset? Recognize(string path, Func<string> peek)
    {
        string name = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();

        if (name.EndsWith(".map", StringComparison.Ordinal))
        {
            string head = peek();
            // A stream map is a table of: layerName purpose streamNumber dataType
            bool looksLikeMap = head.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith(';'))
                .Take(8)
                .Count(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } f
                            && int.TryParse(f[2], out _) && int.TryParse(f[3], out _)) >= 2;

            if (looksLikeMap)
                return new(path, PdkAssetKind.LayerTechnology, PdkAssetSupport.Supported,
                           "layer stream map",
                           "Layer names, purposes and stream numbers — importable as a technology.");
        }

        if (name.EndsWith(".tech", StringComparison.Ordinal))
        {
            string head = peek();
            if (head.Contains("<LPP", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("<Display", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("<Technology", StringComparison.OrdinalIgnoreCase))
                return new(path, PdkAssetKind.LayerTechnology, PdkAssetSupport.Supported,
                           "layer display technology (XML)",
                           "Layer colours, fill styles, visibility and display order.");
        }

        return null;
    }
}
