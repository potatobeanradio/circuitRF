// The silkscreen's text, read — brief-artsch-10-silkscreen-ocr.md R-as10-2, R-as10-6.
//
// Which layers: the technology's silkscreen by its board-format alias (F.SilkS / B.SilkS) or purpose, else by the
// name a Gerber import gave it ("Silk Top", "Legend", …). Each layer is read on its own — top and bottom text never
// share a line — and a bottom layer prefers a mirrored reading. Only stroked shapes (paths) are text; a filled shape
// on a silkscreen layer is a logo, a pin-1 dot, or text drawn as filled outlines, which this reader does not read and
// says so. A label is typed text, which needs no reading: TypedSilkscreenEvidence takes it as it is.

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>Everything read off the silkscreen layers.</summary>
/// <param name="Lines">The text lines read.</param>
/// <param name="Strokes">How many stroked shapes the silkscreen layers hold.</param>
/// <param name="ExcludedStrokes">Strokes no text line took — outlines, logos, marks.</param>
/// <param name="FilledShapes">Filled shapes on the silkscreen layers, which are not read.</param>
/// <param name="Layers">The layers read, top first.</param>
public sealed record SilkscreenReading(
    IReadOnlyList<SilkTextLine> Lines, int Strokes, int ExcludedStrokes, int FilledShapes, IReadOnlyList<LayerKey> Layers)
{
    public static SilkscreenReading Empty { get; } = new([], 0, 0, 0, []);

    /// <summary>The lines that read as designators.</summary>
    public IEnumerable<SilkTextLine> Designators => Lines.Where(l => l.Refdes is not null);

    /// <summary>Glyphs not clear of their runner-up, over every line.</summary>
    public int LowMarginGlyphs => Lines.Sum(StrokeGlyphs.LowMarginGlyphs);
}

/// <summary>Reads the silkscreen.</summary>
public static class SilkscreenText
{
    /// <summary>Reads every silkscreen layer of <paramref name="tech"/> among <paramref name="shapes"/>.</summary>
    public static SilkscreenReading Read(IReadOnlyList<LayoutShape> shapes, Technology tech, GlyphTemplates templates,
                                         CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        var layers = Layers(tech);
        if (layers.Count == 0) return SilkscreenReading.Empty;

        var lines = new List<SilkTextLine>();
        int strokes = 0, excluded = 0, filled = 0;
        foreach (var (layer, bottom) in layers)
        {
            var onLayer = new List<SilkStroke>();
            foreach (var shape in shapes)
            {
                if (shape.Layer != layer || shape is LabelShape or BitmapShape) continue;
                if (shape is not PathShape path) { filled++; continue; }
                var xy = LayoutFlattener.FlattenOpenEdgeList(path.Xy, path.Edges, LayoutFlattener.ResolveTolDbu(path, tech));
                if (xy.Length < 2) continue;
                onLayer.Add(new SilkStroke([.. xy.Select(v => (double)v)], path.Width));
            }
            if (onLayer.Count == 0) continue;
            var (read, unread) = StrokeGlyphs.Read(onLayer, templates, bottom, token);
            lines.AddRange(read.Select(l => l with { Layer = layer }));
            strokes += onLayer.Count;
            excluded += unread;
        }
        return new SilkscreenReading(lines, strokes, excluded, filled, [.. layers.Select(l => l.Layer)]);
    }

    /// <summary>
    /// What a corrected row teaches (R-as10-5): each glyph of the silkscreen line the part's designator was read from
    /// — or that stood beside it, uncertain — that the user's designator says is another character. Null when the row
    /// was not renamed in the parts table, has no silkscreen line, or names a different number of characters than the
    /// line has glyphs (then which glyph is which cannot be told).
    /// </summary>
    public static IReadOnlyList<(char Char, Glyph Glyph)>? Lesson(PartRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Silk is not { } line || row.RenamedFrom is null) return null;
        string said = row.Refdes.Trim().ToUpperInvariant(), read = line.Refdes ?? line.Text;
        if (said.Length != line.Glyphs.Count || said.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))) return null;
        var lesson = new List<(char, Glyph)>();
        for (int i = 0; i < said.Length; i++)
            if (said[i] != read[i] && !StrokeGlyphs.Alike(said[i], read[i])) lesson.Add((said[i], line.Glyphs[i].Glyph));
        return lesson.Count == 0 ? null : lesson;
    }

    /// <summary>
    /// The silkscreen layers, top side first: a layer whose board-format alias is a silkscreen's, whose purpose is
    /// silkscreen or legend, or whose name says silk, legend or overlay. Its side is the alias's (<c>F.</c>/<c>B.</c>),
    /// else the name's (bottom, bot, back).
    /// </summary>
    public static IReadOnlyList<(LayerKey Layer, bool Bottom)> Layers(Technology tech)
    {
        var found = new List<(LayerKey, bool)>();
        foreach (var layer in tech.Layers)
        {
            string alias = layer.Interchange?.PcbLayerName ?? "";
            string name = (layer.Name ?? "").ToLowerInvariant();
            string purpose = (layer.Purpose ?? "").Trim().ToLowerInvariant();
            bool silk = alias.EndsWith(".SilkS", StringComparison.OrdinalIgnoreCase)
                        || purpose is "silkscreen" or "silk" or "legend"
                        || name.Contains("silk") || name.Contains("legend") || name.Contains("overlay");
            if (!silk) continue;
            bool bottom = alias.Length > 0 && alias.Contains('.')
                ? alias.StartsWith("B.", StringComparison.OrdinalIgnoreCase)
                : name.StartsWith("b.") || name.Contains("bottom") || name.Contains("bot") || name.Contains("back");
            found.Add((layer.Key, bottom));
        }
        return [.. found.OrderBy(l => l.Item2)];
    }
}
