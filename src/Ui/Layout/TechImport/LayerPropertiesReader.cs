// Reads a LAYER-PROPERTIES file — a process's own layer table: which (layer, datatype) pair each
// drawn purpose lives on, what it is called, and how it is displayed.
//
// Recognised STRUCTURALLY (a layer-properties root carrying entries that name a stream source),
// never by extension, and parsed by LOCAL element name so a file carrying a namespace reads exactly
// like one that does not — the namespace names the tool that wrote the file, and circuitRF must hold
// no knowledge of any particular one.

using System.Globalization;
using System.Xml.Linq;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>
/// One row of a process layer table, exactly as the file states it.
/// </summary>
/// <param name="Layer">Stream layer number.</param>
/// <param name="Datatype">Stream datatype number.</param>
/// <param name="BaseName">The layer's own name, with any trailing purpose removed.</param>
/// <param name="Purpose">The purpose suffix (drawing, pin, label, …), or null when the name carries none.</param>
/// <param name="Color">Display colour, or null when the file states none usable.</param>
/// <param name="Visible">Whether the process shows this row by default.</param>
/// <param name="Order">Position in the file. The file's order is the display order, so it is kept.</param>
/// <param name="FillRef">The fill pattern this row names, exactly as the file spells it — a
/// letter-and-number reference into either the file's own pattern list or the reader's built-in set.
/// Null when the row states none. Resolved by the builder, not here: this reader states what the file
/// says, and which stipple that becomes is a question about the whole file.</param>
public sealed record ProcessLayerEntry(
    int     Layer,
    int     Datatype,
    string  BaseName,
    string? Purpose,
    Rgba?   Color,
    bool    Visible,
    int     Order,
    string? FillRef = null)
{
    /// <summary>The full name as the file spelled it — base plus purpose.</summary>
    public string FullName => Purpose is { Length: > 0 } p ? $"{BaseName}.{p}" : BaseName;
}

/// <summary>One fill pattern the file defines for itself, at the position it defines it.</summary>
/// <param name="Index">Its own stated position, which is what a layer's reference names.</param>
/// <param name="Name">What the file calls it, or null when it says nothing.</param>
/// <param name="Rows">The mask, one string per row.</param>
public sealed record ProcessFillPattern(int Index, string? Name, IReadOnlyList<string> Rows);

/// <summary>A whole layer-properties file, read.</summary>
public sealed record ProcessLayerTable(
    IReadOnlyList<ProcessLayerEntry> Entries,
    IReadOnlyList<string>            Notes,
    IReadOnlyList<ProcessFillPattern>? FillPatterns = null);

/// <summary>Reads the layer-properties XML format. Framework-free (no Avalonia / Skia).</summary>
public static class LayerPropertiesReader
{
    private const string RootLocalName  = "layer-properties";
    private const string EntryLocalName = "properties";

    /// <summary>
    /// True when the text is a layer-properties file. Requires the root element AND at least one
    /// entry naming a stream source: the root alone appears on files that carry only display
    /// defaults, and a table with no sources maps to no layers.
    /// </summary>
    public static bool LooksLikeLayerPropertiesFile(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.Contains(RootLocalName, StringComparison.Ordinal)) return false;

        try
        {
            var root = XDocument.Parse(text).Root;
            if (root is null || root.Name.LocalName != RootLocalName) return false;

            foreach (var e in Entries(root))
                if (TryReadSource(Child(e, "source"), out _, out _))
                    return true;

            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The same question asked of a PARTIAL read, for a caller that has only the first few kilobytes
    /// of a file — kit classification works that way, and a truncated XML document does not parse at
    /// all, so <see cref="LooksLikeLayerPropertiesFile"/> would reject every real table.
    ///
    /// <para>Deliberately weaker: it looks for the root element and an entry naming a stream source,
    /// both of which are within the first entry of any real table. Use the strict predicate whenever
    /// the whole file is in hand.</para>
    /// </summary>
    public static bool HeadLooksLikeLayerProperties(string head)
        => head.Contains("<" + RootLocalName, StringComparison.Ordinal)
        && head.Contains("<source>", StringComparison.Ordinal);

    public static ProcessLayerTable Read(string text)
    {
        var notes   = new List<string>();
        var entries = new List<ProcessLayerEntry>();

        XElement? root;
        try
        {
            root = XDocument.Parse(text ?? "").Root;
        }
        catch (System.Xml.XmlException ex)
        {
            return new ProcessLayerTable([], [$"The layer table could not be parsed: {ex.Message}"]);
        }

        if (root is null || root.Name.LocalName != RootLocalName)
            return new ProcessLayerTable([], ["The file is not a layer-properties document."]);

        var seen        = new Dictionary<(int, int), string>();
        int order       = 0;
        int noSource    = 0;
        int duplicates  = 0;

        foreach (var e in Entries(root))
        {
            if (!TryReadSource(Child(e, "source"), out int layer, out int datatype))
            {
                // A grouping row, or a row keyed on something other than a stream number. It draws
                // nothing of its own, so it contributes no layer — counted rather than named, since
                // a real table has many and listing each would bury the notes that matter.
                noSource++;
                continue;
            }

            if (seen.TryGetValue((layer, datatype), out string? already))
            {
                duplicates++;
                notes.Add($"Layer ({layer},{datatype}) is declared twice — as \"{already}\" and again " +
                          "later; the first declaration is kept.");
                continue;
            }

            SplitName(Child(e, "name") ?? "", out string baseName, out string? purpose);
            if (baseName.Length == 0) baseName = $"L{layer}/{datatype}";

            seen[(layer, datatype)] = purpose is { Length: > 0 } p ? $"{baseName}.{p}" : baseName;

            entries.Add(new ProcessLayerEntry(
                layer, datatype, baseName, purpose,
                Color:   ReadColor(Child(e, "fill-color")) ?? ReadColor(Child(e, "frame-color")),
                Visible: ReadBool(Child(e, "visible"), fallback: true),
                Order:   order++,
                FillRef: Child(e, "dither-pattern")));
        }

        if (entries.Count == 0)
            notes.Add("The layer table declares no layers with a stream number.");
        if (noSource > 0)
            notes.Add($"{noSource} table row(s) name no stream layer (grouping or display-only rows) " +
                      "and became no layer.");
        if (duplicates > 3)
            notes.Add($"{duplicates} duplicate stream numbers in total.");

        var patterns = ReadFillPatterns(root, notes);

        return new ProcessLayerTable(entries, notes, patterns);
    }

    // ── fill patterns ─────────────────────────────────────────────────────────

    private const string PatternLocalName = "custom-dither-pattern";

    /// <summary>
    /// The stipples the file defines for itself, in file order.
    ///
    /// <para>Each states its own position, and that position — not the position in this list — is
    /// what a layer's reference names, so it is carried rather than recomputed. A file that numbers
    /// them sparsely, or out of order, still resolves correctly.</para>
    /// </summary>
    private static List<ProcessFillPattern>? ReadFillPatterns(XElement root, List<string> notes)
    {
        List<ProcessFillPattern>? patterns = null;
        int malformed = 0;
        int fallbackIndex = 0;

        foreach (var e in root.Descendants().Where(x => x.Name.LocalName == PatternLocalName))
        {
            var rows = e.Elements().FirstOrDefault(c => c.Name.LocalName == "pattern")
                       ?.Elements().Where(c => c.Name.LocalName == "line")
                        .Select(c => c.Value.Trim()).ToList();

            int index = int.TryParse(Child(e, "order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o)
                ? o : fallbackIndex;
            fallbackIndex = index + 1;

            // Square and within the size cap, or it is not a mask this can paint through. Counted
            // rather than named: a file with one bad row has many, and the layers that referred to it
            // fall back to a solid fill, which is visible.
            if (rows is not { Count: > 0 } || rows.Count > Layout.FillPattern.MaxSize
                || rows.Exists(r => r.Length != rows.Count))
            {
                malformed++;
                continue;
            }

            (patterns ??= []).Add(new ProcessFillPattern(index, Child(e, "name"), rows));
        }

        if (malformed > 0)
            notes.Add($"{malformed} fill pattern(s) in the layer table were not a square mask and were " +
                      "skipped; layers naming them fill solid.");

        return patterns;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<XElement> Entries(XElement root)
        => root.Descendants().Where(e => e.Name.LocalName == EntryLocalName);

    private static string? Child(XElement e, string localName)
        => e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value.Trim();

    /// <summary>
    /// A source reads "layer/datatype", optionally followed by qualifiers this reader does not use.
    /// A wildcard or a name-keyed source yields no layer rather than a guessed number.
    /// </summary>
    private static bool TryReadSource(string? raw, out int layer, out int datatype)
    {
        layer = datatype = 0;
        if (raw is not { Length: > 0 }) return false;

        int slash = raw.IndexOf('/');
        if (slash <= 0) return false;

        string lhs = raw[..slash].Trim();
        string rhs = raw[(slash + 1)..].Trim();

        // Drop any trailing qualifier on the datatype ("@1", "*", a transformation, …).
        int cut = rhs.IndexOfAny(['@', ' ', '\t', '*', '(']);
        if (cut >= 0) rhs = rhs[..cut];

        return int.TryParse(lhs, NumberStyles.Integer, CultureInfo.InvariantCulture, out layer)
            && int.TryParse(rhs, NumberStyles.Integer, CultureInfo.InvariantCulture, out datatype);
    }

    /// <summary>
    /// Splits "Metal1.drawing" into its layer name and its purpose. The purpose is the part after the
    /// LAST separator, so a name that itself contains one keeps it.
    /// </summary>
    private static void SplitName(string raw, out string baseName, out string? purpose)
    {
        raw = raw.Trim();
        int dot = raw.LastIndexOf('.');
        if (dot <= 0 || dot == raw.Length - 1)
        {
            baseName = raw;
            purpose  = null;
            return;
        }
        baseName = raw[..dot];
        purpose  = raw[(dot + 1)..];
    }

    private static Rgba? ReadColor(string? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        string s = raw.TrimStart('#');
        if (s.Length == 8) s = s[2..];              // #aarrggbb → drop the alpha, circuitRF sets its own
        if (s.Length != 6) return null;

        return byte.TryParse(s[..2],   NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
               byte.TryParse(s[2..4],  NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
               byte.TryParse(s[4..6],  NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)
            ? new Rgba(r, g, b)
            : null;
    }

    private static bool ReadBool(string? raw, bool fallback)
        => raw is { Length: > 0 } && bool.TryParse(raw, out bool v) ? v : fallback;
}
