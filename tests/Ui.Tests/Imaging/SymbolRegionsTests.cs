// brief-img-7-schematic-wires-and-regions.md R-im7-6 — symbol regions, merged across their own gaps, with the wire
// ends that reach them; a border frame removed first.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolRegionsTests
{
    [Fact]
    public void ACapacitorsTwoPlates_AreOneRegionWithTwoAttachments()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(320, 200, SchematicPictures.SeriesCapacitor));

        var cap = Assert.Single(r.Symbols);
        Assert.Equal(2, cap.Attachments.Count);
        Assert.Equal(2, r.Wires.Segments.Count);
    }

    [Fact]
    public void AGroundsBars_AreOneRegionWithOneAttachment()
    {
        var r = SchematicImageReading.Read(Pictures.Draw(200, 200, c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(100, 20, 100, 120, pen);
            SchematicPictures.Ground(c, 100, 120);
        }));

        var ground = Assert.Single(r.Symbols);
        Assert.Single(ground.Attachments);
        Assert.Empty(r.SupplyMarks);
        Assert.Single(r.Wires.Segments);
    }

    [Fact]
    public void ATitleBlockFrame_IsRemovedAsABorder()
    {
        var plain = SchematicImageReading.Read(SchematicPictures.LNetwork());
        var framed = SchematicImageReading.Read(Pictures.Draw(360, 260, c =>
        {
            SchematicPictures.LNetwork(c);
            using var pen = SchematicPictures.Ink();
            c.DrawRect(4, 4, 352, 252, pen);
            c.DrawRect(240, 216, 116, 40, pen);
            c.DrawLine(240, 236, 356, 236, pen);
            SchematicPictures.Text(c, "TITLE", 250, 231, 12);
        }));

        Assert.NotNull(framed.Border);
        Assert.True(framed.Report.BorderRemoved);
        Assert.Equal(plain.Wires.Segments.Count, framed.Wires.Segments.Count);
        Assert.Equal(plain.Symbols.Count, framed.Symbols.Count);
        Assert.Empty(framed.Decorations);
    }
}
