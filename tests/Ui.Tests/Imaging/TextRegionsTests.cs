// brief-img-7-schematic-wires-and-regions.md R-im7-3 — words are found and taken out before the wires are traced.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class TextRegionsTests
{
    [Fact]
    public void ADesignatorAndAValue_AreTwoWords_RemovedBeforeTracing()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(320, 200, c =>
        {
            SchematicPictures.SeriesCapacitor(c);
            SchematicPictures.Text(c, "C1", 170, 88);
            SchematicPictures.Text(c, "10p", 170, 106);
        }));

        Assert.Equal(2, r.Words.Count);
        Assert.Equal(2, r.Words[0].Glyphs.Count);
        Assert.Equal(3, r.Words[1].Glyphs.Count);
        Assert.All(r.Words, t => Assert.NotEmpty(t.Strokes));
        // No stub: the leads are the only wires, the capacitor the only symbol, and nothing was left of the words.
        Assert.Equal(2, r.Wires.Segments.Count);
        Assert.Single(r.Symbols);
        Assert.Empty(r.Decorations);
    }

    [Fact]
    public void AWordTouchingAWire_IsKeptAndReported()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(200, 220, c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(60, 20, 60, 200, pen);
            SchematicPictures.Text(c, "E1", 60, 110);
        }));

        Assert.Empty(r.Words);
        var kept = Assert.Single(r.KeptText);
        Assert.True(kept.Contact > 3, $"contact {kept.Contact} w");
        Assert.Equal(1, r.Report.WordsKeptAsSymbolDetail);
    }
}
