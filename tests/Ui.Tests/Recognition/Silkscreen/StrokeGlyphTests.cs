// brief-artsch-10-silkscreen-ocr.md §3 — stroked silkscreen text read back. The text is drawn by the built-in stroke
// font itself, laid on the board at each orientation a CAD tool writes it at, with every coordinate moved by up to
// 0.5 % of the cap height, and the strokes are read exactly as the silkscreen reader reads an imported legend.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition.Silkscreen;

public sealed class StrokeGlyphTests
{
    private const double Cap = 1_000_000;   // 1 mm at 1000 DBU/µm
    private const double Pen = 0.15 * Cap;

    private static readonly string[] Labels = ["C12", "R3", "FB7", "L45", "U1", "TP10", "J2", "C8"];

    /// <summary>Text drawn by the font, turned to <paramref name="rotation"/> (and mirrored), at (x0, y0), noised.</summary>
    internal static List<SilkStroke> Text(string text, double x0, double y0, int rotation, bool mirrored, Random noise)
    {
        double c = Math.Cos(rotation * Math.PI / 180), s = Math.Sin(rotation * Math.PI / 180);
        return [.. GlyphTemplates.BuiltIn.Draw(text, Cap).Select(xy =>
        {
            var o = new double[xy.Length];
            for (int i = 0; i + 1 < xy.Length; i += 2)
            {
                double x = mirrored ? -xy[i] : xy[i], y = xy[i + 1];
                o[i] = x0 + c * x - s * y + (noise.NextDouble() * 2 - 1) * 0.005 * Cap;
                o[i + 1] = y0 + s * x + c * y + (noise.NextDouble() * 2 - 1) * 0.005 * Cap;
            }
            return new SilkStroke(o, Pen);
        })];
    }

    [Fact]
    public void TextAtEveryOrientationAndMirroredReadsBackExactly()
    {
        var noise = new Random(10);
        foreach (var (rotation, mirrored) in new[] { (0, false), (90, false), (180, false), (270, false), (0, true), (90, true) })
        {
            var strokes = new List<SilkStroke>();
            for (int i = 0; i < Labels.Length; i++)
                strokes.AddRange(Text(Labels[i], (i % 4) * 6 * Cap, (i / 4) * 6 * Cap, rotation, mirrored, noise));

            var (lines, unread) = StrokeGlyphs.Read(strokes, GlyphTemplates.BuiltIn, bottomSide: mirrored);

            string at = $"{rotation}°{(mirrored ? " mirrored" : "")}";
            Assert.True(Labels.Order().SequenceEqual(lines.Select(l => l.Refdes ?? $"'{l.Text}'").Order()),
                        $"{at}: read {string.Join(", ", lines.Select(l => $"{l.Refdes ?? "-"} '{l.Text}' {l.Orientation}"))}");
            Assert.All(lines, l => Assert.Equal(new TextOrientation(rotation, mirrored), l.Orientation));
            Assert.Equal(0, unread);
        }
    }

    [Fact]
    public void ALogoOfClosedShapesIsExcludedAndCounted()
    {
        var noise = new Random(11);
        var strokes = Text("C6", 0, 0, 0, false, noise);
        // A logo beside it: squares and rings several glyph heights across.
        var logo = new List<SilkStroke>();
        for (int k = 0; k < 3; k++)
        {
            double x = 4 * Cap + k * 5 * Cap, size = (3 + k) * Cap;
            logo.Add(new SilkStroke([x, 0, x + size, 0, x + size, size, x, size, x, 0], Pen));
            logo.Add(new SilkStroke([.. Enumerable.Range(0, 33).SelectMany(i =>
                new[] { x + size / 2 + size / 3 * Math.Cos(i * Math.PI / 16), size / 2 + size / 3 * Math.Sin(i * Math.PI / 16) })], Pen));
        }
        strokes.AddRange(logo);

        var (lines, unread) = StrokeGlyphs.Read(strokes, GlyphTemplates.BuiltIn);

        Assert.Equal(["C6"], lines.Select(l => l.Refdes));
        Assert.Equal(logo.Count, unread);
    }

    /// <summary>brief-img-9 R-im9-2: the value glyphs are a class of their own. The silkscreen reader's set holds
    /// the designator characters and AS-10's three variants and nothing else, and a value glyph taught from a picture
    /// comes back in the value class, which <see cref="GlyphTemplates.ForUser"/> leaves out.</summary>
    [Fact]
    public void TheSilkscreenSet_IsUnchangedByTheValueGlyphs()
    {
        var silk = GlyphTemplates.BuiltIn.All;
        Assert.All(silk, t => Assert.Equal(GlyphClass.Designator, t.Class));
        Assert.Equal(GlyphTemplates.DesignatorCharacters.Length + 3, silk.Count);
        Assert.Equal(GlyphTemplates.DesignatorCharacters.Order(), silk.Select(t => t.Char).Distinct().Order());

        var text = GlyphTemplates.Text.All;
        Assert.Equal(silk, text.Take(silk.Count));
        Assert.All(text.Skip(silk.Count), t => Assert.Equal(GlyphClass.Value, t.Class));
        Assert.Contains(text, t => t.Char == 'θ');

        string dir = Path.Combine(Path.GetTempPath(), "crf-glyphs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var glyph = text.First(t => t.Char == 'p').Glyph;
            Assert.Equal(1, GlyphTemplates.Learn(dir, [('p', glyph, GlyphClass.Value)]));
            Assert.Equal(GlyphClass.Value, Assert.Single(GlyphTemplates.ReadTaught(dir)).Class);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
