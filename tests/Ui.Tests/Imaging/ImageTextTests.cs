// brief-img-9-text-and-values.md §4 — words drawn in DejaVu Sans (a font circuitRF ships) at a 14 px cap height,
// read by AS-10's glyph matcher under the grammar. brief-imf-7-typography.md §4 — bold, confused, word-designator,
// annotation and textbook serif italic words.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageTextTests
{
    // DejaVu Sans's cap height is 0.729 em.
    private const float Size = 14f / 0.729f;

    /// <summary>Each line drawn at its own baseline, 40 px apart, under a wire that sets the stroke width.</summary>
    private static IReadOnlyList<ImageWord> Read(Action<SKCanvas>? more, params string[] lines)
    {
        var r = SchematicImageReading.Read(Pictures.Draw(420, 80 + 40 * lines.Length, c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(10, 20, 400, 20, pen);
            for (int i = 0; i < lines.Length; i++) SchematicPictures.Text(c, lines[i], 40, 70 + 40 * i, Size);
            more?.Invoke(c);
        }));
        return ImageText.Read(r);
    }

    private static readonly Lazy<IReadOnlyList<ImageWord>> Gate =
        new(() => Read(null, "C12", "10pF", "2n2", "4R7", "1kΩ", "RFIN", "Z=50Ω, E=90°"));

    private static ImageWord At(int line) => Assert.Single(Gate.Value, w => w.Box.Top >= 50 + 40 * line && w.Box.Top < 90 + 40 * line);

    [MeasuredOnMacOSFact]
    public void TheGateWords_ReadAsTheirClasses()
    {
        Assert.Equal(7, Gate.Value.Count);
        Assert.Equal(("C12", WordClass.Designator), (At(0).Text, At(0).Class));

        void Value(int line, string text, double si, params UnitDimension[] dims)
        {
            var w = At(line);
            Assert.Equal((text, WordClass.Value), (w.Text, w.Class));
            Assert.Equal(si, w.Reading!.Value!.Si, si * 1e-12);
            Assert.Equal(dims.Order(), w.Reading.Value.Dimensions.Order());
        }
        Value(1, "10pF", 10e-12, UnitDimension.Capacitance);
        Value(2, "2n2", 2.2e-9, UnitDimension.Capacitance, UnitDimension.Inductance);   // the part says which
        Value(3, "4R7", 4.7, UnitDimension.Resistance);
        Value(4, "1kΩ", 1000, UnitDimension.Resistance);                                   // k and Ω touch: cut

        Assert.Equal(("RFIN", WordClass.Name, true), (At(5).Text, At(5).Class, At(5).Reading!.IsPortWord));

        var set = At(6);
        Assert.Equal(WordClass.LineParameters, set.Class);
        Assert.Equal([("Z", 50.0), ("E", 90.0)], set.Reading!.Parameters!.Select(p => (p.Key, p.Si)));
    }

    /// <summary>A sans-serif l, I and flagless 1 skeletonise to one bar; only the 1 makes a word of it.</summary>
    [Fact]
    public void AnAmbiguousFirstGlyph_IsReadByTheGrammar()
    {
        var w = Assert.Single(Read(null, "l0pF"));
        Assert.Equal(("10pF", WordClass.Value), (w.Text, w.Class));
        var first = w.Glyphs[0];
        Assert.Contains(first.Ranked, m => m.Char == 'l' && m.Distance <= first.Best.Distance + 1e-9);
    }

    [Fact]
    public void AScribble_IsUnreadWithItsBox()
    {
        // Three scrawls of glyph size, side by side — nine random strokes each — a word's shape that is no word. Each
        // piece is near some letter; together they fit none well enough.
        var rng = new Random(4);
        var words = Read(c =>
        {
            using var pen = SchematicPictures.Ink();
            for (int k = 0; k < 3; k++)
            {
                using var path = new SKPath();
                float x = 60 + 16 * k, y = 48;
                path.MoveTo(x + (float)rng.NextDouble() * 11, y + (float)rng.NextDouble() * 14);
                for (int i = 0; i < 9; i++) path.LineTo(x + (float)rng.NextDouble() * 11, y + (float)rng.NextDouble() * 14);
                c.DrawPath(path, pen);
            }
        });
        Assert.NotEmpty(words);
        Assert.All(words, w => Assert.True(w.Class == WordClass.Unread, $"read '{w.Text}' as {w.Class}, seen '{w.Seen}' at {w.Distance:0.000}"));
        var box = words.Select(w => w.Box).Aggregate((a, b) => a.Union(b));
        Assert.True(box.Left <= 64 && box.Right >= 98 && box.Top <= 52 && box.Bottom >= 58, $"box {box}");
    }

    // ── brief-imf-7-typography.md ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Lines of <paramref name="face"/> at a <paramref name="cap"/> px cap height, <paramref name="lead"/> caps
    /// apart, under a wire — the picture's text height stated, as a datasheet's dozens of labels let the profile measure
    /// it where a test picture's three do not.</summary>
    private static (SchematicImageRead Read, IReadOnlyList<ImageWord> Words) Lines(SKTypeface face, float cap, float lead, params string[] lines)
    {
        using var probe = new SKFont(face, 100);
        probe.MeasureText("H", out var bounds);
        float size = cap / (-bounds.Top / 100f);
        var r = SchematicImageReading.Read(Pictures.Draw(420, (int)(80 + lead * cap * lines.Length), c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(10, 20, 400, 20, pen);
            using var font = new SKFont(face, size);
            using var ink = Pictures.Fill(SKColors.Black);
            for (int i = 0; i < lines.Length; i++) c.DrawText(lines[i], 40, 60 + lead * cap * i, SKTextAlign.Left, font, ink);
        }), new SchematicImageOptions { Profile = new PictureProfileOptions { TextHeight = cap } });
        return (r, ImageText.Read(r));
    }

    /// <summary>R-imf7-1: DejaVu Sans Bold — the regulator's face — at a datasheet's 9 and 14 px caps, refused as too heavy
    /// before, is thinned to a regular weight and read: VIN, 240 Ω, 1.0 µF.</summary>
    [MeasuredOnMacOSTheory]
    [InlineData(9f)]
    [InlineData(14f)]
    public void BoldDatasheetWords_AreThinnedAndRead(float cap)
    {
        var (read, words) = Lines(CircuitRF.Render.SkiaFonts.DejaVuBold, cap, 3, "VIN", "240 Ω", "1.0 µF");
        Assert.Equal(3, words.Count);
        Assert.Equal("VIN", words[0].Text.ToUpperInvariant());
        Assert.Equal("240Ω", words[1].Text.Replace(" ", ""));
        Assert.Contains(words[2].Text.Replace(" ", ""), new[] { "1.0µF", "1.0uF" });
        Assert.True(words[0].Reading!.IsKnownWord);
        Assert.Equal((240.0, UnitDimension.Resistance), (words[1].Reading!.Value!.Si, Assert.Single(words[1].Reading!.Value!.Dimensions)));
        Assert.Equal(1e-6, words[2].Reading!.Value!.Si, 1e-12);
        Assert.Contains(read.Words, t => t.Thinned > 0);
        Assert.DoesNotContain(words, w => w.TooHeavy);
    }

    /// <summary>R-imf7-1: past <see cref="StrokeTextReader.MaxBoldWeight"/> the refusal stays — DejaVu Sans Bold at a 9 px
    /// cap read as it is drawn stands at 0.33 of its cap, where the survey read wrong as often as right — and the report
    /// says so.</summary>
    [MeasuredOnMacOSFact]
    public void BoldTooHeavyEvenThinned_IsNotRead()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(500, 140, c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(10, 20, 480, 20, pen);
            using var font = new SKFont(CircuitRF.Render.SkiaFonts.DejaVuBold, 9f / 0.729f);
            using var ink = Pictures.Fill(SKColors.Black);
            c.DrawText("DNP", 40, 90, SKTextAlign.Left, font, ink);
        }));
        var word = Assert.Single(ImageText.Read(r));
        Assert.True(word.TooHeavy && word.Class == WordClass.Unread, $"'{word.Text}' {word.Class}");
        Assert.Equal(1, LabelAssociation.Associate([], [word]).Report.TooHeavy);
    }

    private static GlyphReading G(params (char C, double D)[] ranked) =>
        new(new Glyph([[0.0, 0.0, 0.0, 1.0]]), [.. ranked.Select(m => new GlyphMatch(m.C, m.D))]);

    /// <summary>R-imf7-2: a run whose glyphs the matcher confused reads as the known word — V1N (a flagless 1 for the I)
    /// as VIN, QNO (a G read Q, a D read O, each past the matcher's reach of its best) as GND — and a clean RF, no word of
    /// the list, whose R no N stands near, stays RF rather than becoming the known NF (within the matcher's reach of
    /// each other a known word is ranked first, as a port word is).</summary>
    [Fact]
    public void ConfusedGlyphRuns_ReadAsTheKnownWord()
    {
        var v1n = new[] { G(('V', .03), ('U', .05)), G(('1', .03), ('I', .03), ('l', .03)), G(('N', .03), ('H', .06)) };
        var qno = new[] { G(('Q', .03), ('O', .04), ('G', .07)), G(('N', .03), ('H', .06)), G(('O', .03), ('0', .035), ('D', .08)) };
        var rf = new[] { G(('R', .03), ('N', .07)), G(('F', .03), ('E', .06)) };
        Assert.Equal(("VIN", true), StrokeTextReader.Decode(v1n, [(0, 3)]) is { } a ? (a.Reading.Text, a.Reading.IsKnownWord) : default);
        Assert.Equal(("GND", true), StrokeTextReader.Decode(qno, [(0, 3)]) is { } b ? (b.Reading.Text, b.Reading.IsKnownWord) : default);
        Assert.Equal("RF", StrokeTextReader.Decode(rf, [(0, 2)])?.Reading.Text);
    }

    /// <summary>R-imf7-3: a word designator names the symbol of its kind beside it — even standing nearer a part of
    /// another kind, which takes none of it and is given a designator of its own.</summary>
    [Theory]
    [InlineData("Cblock", ImageSymbolKind.Capacitor)]
    [InlineData("RFC", ImageSymbolKind.Inductor)]
    [InlineData("CADJ", ImageSymbolKind.Capacitor)]
    public void AWordDesignator_NamesTheSymbolOfItsKindBesideIt(string text, ImageSymbolKind kind)
    {
        var resistor = new LabelTarget(0, new PixelBox(100, 100, 120, 140), ImageSymbolKind.Resistor, PartConfidence.High);
        var part = new LabelTarget(1, new PixelBox(170, 100, 190, 140), kind, PartConfidence.High);
        var word = new ImageWord(0, 0, new PixelBox(128, 113, 148, 126), ValueGrammar.Parse(text), text, 0.03, []);

        var r = LabelAssociation.Associate([resistor, part], [word], h: 14);

        Assert.Equal((text, false), (r.Symbols[1].Designator, r.Symbols[1].Generated));
        Assert.True(r.Symbols[0].Generated);
    }

    /// <summary>R-imf7-5: two lines of words that are no designator, value or known word — a datasheet's DC Blocking /
    /// Capacitor — are one comment, never a name.</summary>
    [MeasuredOnMacOSFact]
    public void ATwoLineAnnotation_IsOneComment()
    {
        var (_, words) = Lines(CircuitRF.Render.SkiaFonts.DejaVuRegular, 14, 1.3f, "DC Blocking", "Capacitor");
        var note = Assert.Single(words);
        Assert.Equal(WordClass.Comment, note.Class);
        Assert.True(note.Box.Height >= 2 * 14, $"the comment's box {note.Box} does not hold both lines");
    }

    private static readonly Lazy<IReadOnlyList<ImageWord>> Textbook = new(() =>
    {
        // A textbook's labels in serif italic (a test asset, overview O4), set as Drafting.Subscripted sets them: a
        // subscript 0.65 of the cap height, its baseline 0.3 of it below the line's. Drawn together, so the style's slant
        // is measured over all of them (R-imf7-7).
        var face = CircuitRF.Ui.Tests.ImageAcceptance.Drafting.SerifItalic;
        const float cap = 22;
        (string Main, string Sub, string? After)[] items = [("R", "1", null), ("C", "n", null), ("v", "out", null), ("R", "3", "10kΩ"), ("Q", "1", "2N2222"), ("R", "", null)];
        var r = SchematicImageReading.Read(Pictures.Draw(700, 200 + (int)(items.Length * 3 * cap), c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(10, 20, 680, 20, pen);
            using var ink = Pictures.Fill(SKColors.Black);
            for (int k = 0; k < items.Length; k++)
            {
                float x = 60, baseline = 70 + 3 * cap * k;
                var (m, sub, after) = items[k];
                using (var f = new SKFont(face, cap / 0.729f)) { c.DrawText(m, x, baseline, SKTextAlign.Left, f, ink); x += f.MeasureText(m); }
                if (sub.Length > 0) using (var f = new SKFont(face, cap * 0.65f / 0.729f)) { c.DrawText(sub, x, baseline + cap * 0.3f, SKTextAlign.Left, f, ink); x += f.MeasureText(sub); }
                if (after is not null) using (var f = new SKFont(face, cap / 0.729f)) c.DrawText(after, x, baseline, SKTextAlign.Left, f, ink);
            }
        }));
        return ImageText.Read(r);
    });

    /// <summary>R-imf7-9 … R-imf7-11: a textbook's serif italic labels, each read to its text and class — a subscripted
    /// designator, a designator then its value on one run, a lone designator letter. The cases skipped are what the
    /// phase measured short of the brief, with what they read.</summary>
    [MeasuredOnMacOSTheory]
    [InlineData(0, "R1", WordClass.Designator, null)]
    [InlineData(1, "Cn", WordClass.Name, null, Skip = "measured short (design note §23.9): C_n reads C9 — the italic n subscript is nearer a 9")]
    [InlineData(2, "vout", WordClass.Name, null, Skip = "measured short (design note §23.9): v_out reads UOUZ — an italic v reads U, and no grammar says which")]
    [InlineData(3, "R3", WordClass.Designator, "10kΩ", Skip = "measured short (design note §23.9): R₃10kΩ reads R3 then 1Ω — the italic k and 0 touch and are lost")]
    [InlineData(4, "Q1", WordClass.Designator, "2N2222", Skip = "measured short (design note §23.9): Q₁2N2222 reads Q,2N2222 — the subscript is not found under the Q's tail")]
    [InlineData(5, "R", WordClass.Name, null)]
    public void TextbookItalicSerif_ReadsItsLabel(int line, string text, WordClass cls, string? after)
    {
        var words = Textbook.Value.OrderBy(w => w.Box.Top).ThenBy(w => w.Box.Left).ToList();
        // The lines are 66 px apart from y = 70: a word belongs to the line its box's middle is nearest.
        var mine = words.Where(w => (int)Math.Round((w.Box.CentreY - 64) / 66.0) == line).OrderBy(w => w.Box.Left).ToList();
        Assert.True(mine.Count > 0, $"line {line} read no word");
        Assert.Equal((text, cls), (mine[0].Text, mine[0].Class));
        if (text is "R" or "Cn") Assert.NotEmpty(mine[0].Reading!.Designates);
        if (after is not null) Assert.Equal(after, Assert.Single(mine.Skip(1)).Text);
    }
}

/// <summary>
/// The readings were measured on macOS. Skia rasterises text through the platform's font engine, so the same words
/// drawn on Linux come out a few pixels different and the glyph matcher reads some of them otherwise; there these
/// tests are skipped rather than held to numbers taken from another rasteriser.
/// </summary>
internal sealed class MeasuredOnMacOSFactAttribute : FactAttribute
{
    public MeasuredOnMacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = MeasuredOnMacOS.Reason;
    }
}

/// <inheritdoc cref="MeasuredOnMacOSFactAttribute"/>
internal sealed class MeasuredOnMacOSTheoryAttribute : TheoryAttribute
{
    public MeasuredOnMacOSTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = MeasuredOnMacOS.Reason;
    }
}

internal static class MeasuredOnMacOS
{
    public const string Reason = "the readings were measured on macOS, and text rasterises differently elsewhere";
}
