// Designators TYPED on the silkscreen — brief-artsch-10-silkscreen-ocr.md R-as10-2, R-as10-4, R-as10-6; R-as4-1 (4), R-as4-9.
//
// A drawing exported as DXF carries its designators as TEXT or MTEXT, which the import keeps as LabelShapes with the exact
// characters. SilkscreenText reads stroked shapes only, so these were skipped and a board drawn with no lands came out with no
// named parts and no word about why. Here a label on a silkscreen layer whose text is a designator (StrokeGlyphs' own rule) is
// a SilkTextLine with no glyphs — there is nothing to be unsure of — and a claim through IPartEvidenceSource, the seam
// RasterSilkscreenEvidence plugs a picture's legend into. RefdesAssociation then gives it to an unnamed part as it does a
// stroked one; a label only NAMES a part another rung found. An empty glyph list is what SilkscreenText.Lesson already
// answers with "nothing to learn".

using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>The designators typed as text labels on the silkscreen layers, as part claims.</summary>
public sealed class TypedSilkscreenEvidence : IPartEvidenceSource
{
    /// <summary>A label's characters are this share of its height wide, for its box: the association needs where the
    /// text is, not how a font sets it.</summary>
    public const double CharWidth = 0.6;

    // MTEXT's inline codes — \A1; \fArial|b0; \H2.5; and the braces round a run — say how the text is set, not what it is.
    private static readonly Regex MtextCodes = new(@"\\[A-Za-z][^;\\{}]*;|[{}]", RegexOptions.CultureInvariant);

    public TypedSilkscreenEvidence(IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        var lines = new List<SilkTextLine>();
        int other = 0;
        foreach (var (layer, bottom) in SilkscreenText.Layers(tech))
            foreach (var shape in shapes)
            {
                if (shape is not LabelShape { IsPort: false } label || label.Layer != layer) continue;
                string text = MtextCodes.Replace(label.Text, "").Trim().ToUpperInvariant();
                if (!StrokeGlyphs.IsDesignator(text)) { if (text.Length > 0) other++; continue; }
                lines.Add(new SilkTextLine(text, text, [], BoxOf(label, text.Length), label.Height,
                                           new TextOrientation((int)Math.Round(label.RotationDegrees), bottom), [], layer));
            }
        Lines = lines;
        OtherLabels = other;
    }

    public PartEvidenceSource Source => PartEvidenceSource.Silkscreen;

    /// <summary>The designators read, each a line of known text.</summary>
    public IReadOnlyList<SilkTextLine> Lines { get; }

    /// <summary>Labels on the silkscreen that are not designators — a value, a title, a pin name.</summary>
    public int OtherLabels { get; }

    public IEnumerable<PartClaim> Claims(RecognitionInput input, BoardGraph board) =>
        Lines.Select(l => new PartClaim(Source, l.X, l.Y, l.Refdes!, 0) { Line = l });

    /// <summary>The box a label's text covers: its anchor (baseline left unless it says otherwise), its height, and a
    /// width of <see cref="CharWidth"/> heights a character, turned with the label.</summary>
    internal static Bbox BoxOf(LabelShape label, int characters)
    {
        double h = label.Height, w = CharWidth * h * Math.Max(1, characters);
        double x0 = label.HAlign switch { LabelHAlign.Center => -w / 2, LabelHAlign.Right => -w, _ => 0 };
        double y0 = label.VAlign switch { LabelVAlign.Top => -h, LabelVAlign.Middle => -h / 2, _ => 0 };
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
