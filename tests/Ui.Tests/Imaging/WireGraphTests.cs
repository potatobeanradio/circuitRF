// brief-img-7-schematic-wires-and-regions.md R-im7-4, R-im7-5 — the wires of a schematic picture as a graph.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class WireGraphTests
{
    private static SchematicImageRead Read(System.Action<SKCanvas> draw, CrossingRule rule = CrossingRule.WithDot) =>
        SchematicImageReading.Read(Pictures.Draw(200, 200, draw), new SchematicImageOptions { Crossings = rule });

    [Fact]
    public void AnLNetwork_GivesFiveSegmentsAndThreeSymbols()
    {
        var r = SchematicImageReading.Read(SchematicPictures.LNetwork());

        Assert.True(r.Ok, r.Refusal);
        Assert.InRange(r.StrokeWidth, 1.5, 2.5);
        Assert.Equal(5, r.Wires.Segments.Count);
        Assert.Equal(3, r.Wires.NetCount);
        Assert.Equal(3, r.Symbols.Count);
        Assert.Equal(new[] { 1, 2, 2 }, r.Symbols.Select(s => s.Attachments.Count).OrderBy(n => n));
        Assert.Single(r.Wires.Nodes, n => n.Degree == 3);
    }

    [Fact]
    public void ATWithoutADot_Connects()
    {
        var r = Read(c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(20, 100, 180, 100, pen);
            c.DrawLine(100, 100, 100, 180, pen);
        });

        Assert.Equal(3, r.Wires.Segments.Count);
        Assert.Equal(1, r.Wires.NetCount);
        Assert.Empty(r.Wires.Crossings);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void ACrossing_ConnectsOnlyWithADot(bool dot, int nets)
    {
        var r = Read(c =>
        {
            using var pen = SchematicPictures.Ink();
            c.DrawLine(20, 100, 180, 100, pen);
            c.DrawLine(100, 20, 100, 180, pen);
            if (dot) { using var fill = Pictures.Fill(SKColors.Black); c.DrawCircle(100, 100, 4, fill); }
        });

        var x = Assert.Single(r.Wires.Crossings);
        Assert.Equal(dot, x.Dot);
        Assert.Equal(4, r.Wires.Segments.Count);
        Assert.Equal(nets, r.Wires.NetCount);
    }

    [Theory]
    [InlineData(CrossingRule.WithDot)]
    [InlineData(CrossingRule.Always)]
    public void AHop_NeverConnects(CrossingRule rule)
    {
        var r = Read(c =>
        {
            using var pen = SchematicPictures.Ink();
            using var hop = new SKPath();
            hop.MoveTo(20, 100);
            hop.LineTo(95, 100);
            hop.ArcTo(new SKRect(95, 95, 105, 105), 180, 180, false);
            hop.LineTo(180, 100);
            c.DrawPath(hop, pen);
            c.DrawLine(100, 20, 100, 180, pen);
        }, rule);

        var x = Assert.Single(r.Wires.Crossings);
        Assert.Equal(CrossingReading.Hop, x.Reading);
        Assert.Equal(2, r.Wires.Segments.Count);
        Assert.Equal(2, r.Wires.NetCount);
        Assert.Empty(r.Symbols);
    }

    [Fact]
    public void ASkewedCopy_ReadsTheSameGraph()
    {
        var straight = SchematicImageReading.Read(SchematicPictures.LNetwork());
        var skewed = SchematicImageReading.Read(SchematicPictures.LNetwork(1.2f));

        Assert.InRange(skewed.DeskewDeg, -1.4, -1.0);
        Assert.Equal(straight.Wires.Segments.Count, skewed.Wires.Segments.Count);
        Assert.Equal(straight.Wires.Segments.Select(s => s.Orientation).Order(), skewed.Wires.Segments.Select(s => s.Orientation).Order());
        Assert.Equal(straight.Wires.Nodes.Select(n => n.Degree).Order(), skewed.Wires.Nodes.Select(n => n.Degree).Order());
        Assert.Equal(straight.Wires.NetCount, skewed.Wires.NetCount);
        Assert.Equal(straight.Symbols.Select(s => s.Attachments.Count), skewed.Symbols.Select(s => s.Attachments.Count));
    }
}
