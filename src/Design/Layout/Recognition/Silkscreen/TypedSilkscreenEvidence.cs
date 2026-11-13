// Designators TYPED on the silkscreen — brief-artsch-10-silkscreen-ocr.md R-as10-2, R-as10-4, R-as10-6; R-as4-1 (4), R-as4-9.
//
// A drawing exported as DXF carries its designators as TEXT or MTEXT, which the import keeps as LabelShapes with the exact
// characters. SilkscreenText reads stroked shapes only, so these were skipped and a board drawn with no lands came out with no
// named parts and no word about why. Here a label on a silkscreen layer whose text is a designator (StrokeGlyphs' own rule) is
// a SilkTextLine with no glyphs — there is nothing to be unsure of — and a PartClaim, the kind of claim RasterSilkscreenEvidence
// makes for a picture's legend through IPartEvidenceSource. It is not plugged in through that seam: PartReading reads it
// itself, first and whatever else is found, as it reads the stroked silkscreen. RefdesAssociation then gives it to an unnamed
// part as it does a stroked one; a label only NAMES a part another rung found. An empty glyph list is what
// SilkscreenText.Lesson already answers with "nothing to learn".

using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>The designators typed as text labels on the silkscreen layers, as part claims.</summary>
public sealed class TypedSilkscreenEvidence
{
    /// <summary>A label's characters are this share of its height wide, for its box: the association needs where the
    /// text is, not how a font sets it.</summary>
    public const double CharWidth = 0.6;

    // How the text is set, not what it is: MTEXT's codes with a value up to a semicolon (\A1; \C1; \c16711680; \fArial|b0;
    // \H2.5x; \pxqc; \S1/2;), its toggles (\L \l \O \o \K \k \~), the braces round a run, and TEXT's toggles (%%u %%o %%k).
    // A \U+ escape is left: it is a character, and no designator has one.
    private static readonly Regex Codes = new(@"\\[ACcFfHpQTWS][^;]*;|\\[LlOoKk~]|[{}]|%%[uUoOkK]", RegexOptions.CultureInvariant);

    // MTEXT's breaks — a paragraph (\P), a column (\N), the line under a dimension's (\X) — and a line break: each line is a
    // text of its own, a designator over its value two, where stripped as codes they ran together into one ("C210").
    private static readonly Regex Breaks = new(@"\\[PNX]|\r?\n", RegexOptions.CultureInvariant);

    /// <summary>A line of an MTEXT is this many of its heights below the one above it, at its default spacing.</summary>
    public const double LineSpacing = 5.0 / 3;

    public TypedSilkscreenEvidence(IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        var lines = new List<SilkTextLine>();
        var other = new List<(long X, long Y)>();
        foreach (var (layer, bottom) in SilkscreenText.Layers(tech))
            foreach (var shape in shapes)
            {
                if (shape is not LabelShape { IsPort: false } label || label.Layer != layer) continue;
                var parts = Breaks.Split(label.Text);
                for (int line = 0; line < parts.Length; line++)
                {
                    string text = Codes.Replace(parts[line], "").Trim().ToUpperInvariant();
                    if (!StrokeGlyphs.IsDesignator(text)) { if (text.Length > 0) other.Add((label.X, label.Y)); continue; }
                    lines.Add(new SilkTextLine(text, text, [], BoxOf(label, text.Length, line), label.Height,
                                               new TextOrientation((int)Math.Round(label.RotationDegrees), bottom), [], layer));
                }
            }
        Lines = lines;
        OtherLabels = other;
    }

    /// <summary>The designators read, each a line of known text.</summary>
    public IReadOnlyList<SilkTextLine> Lines { get; }

    /// <summary>Where the labels on the silkscreen that are not designators are — a value, a title, a pin name.</summary>
    public IReadOnlyList<(long X, long Y)> OtherLabels { get; }

    /// <summary>Each designator as a claim of the silkscreen's.</summary>
    public IEnumerable<PartClaim> Claims() =>
        Lines.Select(l => new PartClaim(PartEvidenceSource.Silkscreen, l.X, l.Y, l.Refdes!, 0) { Line = l });

    /// <summary>The box a label's text covers: its anchor (baseline left unless it says otherwise), its height, and a
    /// width of <see cref="CharWidth"/> heights a character, turned with the label; its <paramref name="line"/>th line
    /// <see cref="LineSpacing"/> heights a line below the first.</summary>
    internal static Bbox BoxOf(LabelShape label, int characters, int line = 0)
    {
        double h = label.Height, w = CharWidth * h * Math.Max(1, characters);
        double x0 = label.HAlign switch { LabelHAlign.Center => -w / 2, LabelHAlign.Right => -w, _ => 0 };
        double y0 = label.VAlign switch { LabelVAlign.Top => -h, LabelVAlign.Middle => -h / 2, _ => 0 } - line * LineSpacing * h;
        double a = label.RotationDegrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (u, v) in new[] { (x0, y0), (x0 + w, y0), (x0 + w, y0 + h), (x0, y0 + h) })
        {
            double x = label.X + u * c - v * s, y = label.Y + u * s + v * c;
            (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        }
        return new Bbox((long)Math.Floor(minX), (long)Math.Floor(minY), (long)Math.Ceiling(maxX), (long)Math.Ceiling(maxY));
    }
}
