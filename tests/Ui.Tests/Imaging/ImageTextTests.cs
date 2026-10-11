// brief-img-9-text-and-values.md §4 — words drawn in DejaVu Sans (a font circuitRF ships) at a 14 px cap height,
// read by AS-10's glyph matcher under the grammar.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Cells;
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

    [Fact]
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

    /// <summary>Bold at a 14 px cap reads some words WRONG (IN as the value 1H); it is refused, word by word, and said so.</summary>
    [Fact]
    public void BoldTypeAtThisSize_IsNotRead()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(300, 140, c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(10, 20, 280, 20, pen);
            using var font = new SKFont(CircuitRF.Render.SkiaFonts.PlexBold, 14f / 0.698f);
            using var ink = Pictures.Fill(SKColors.Black);
            c.DrawText("IN", 40, 70, SKTextAlign.Left, font, ink);
            c.DrawText("DNP", 40, 110, SKTextAlign.Left, font, ink);
        }));
        var words = ImageText.Read(r);
        Assert.Equal(2, words.Count);
        Assert.All(words, w => Assert.True(w.TooHeavy && w.Class == WordClass.Unread, $"'{w.Text}' {w.Class}"));
        Assert.Equal(2, LabelAssociation.Associate([], words).Report.TooHeavy);
    }
}
