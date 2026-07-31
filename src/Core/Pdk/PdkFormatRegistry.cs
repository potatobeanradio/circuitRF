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

            ".pdf" or ".txt" or ".md" or ".html" or ".htm" =>
                new(path, PdkAssetKind.Documentation, PdkAssetSupport.Supported, "documentation"),

            _ => null,
        };
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
