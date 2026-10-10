// brief-img-1-raster-core.md R-im1-9 (D17) — the same bytes from two runs and from two thread counts.

using System.IO;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class RasterDeterminismTests
{
    [Fact]
    public void TheCore_GivesTheSameBytes_FromTwoRunsAndTwoThreadCounts()
    {
        // Taller than several fixed bands, so the parallel loops really do split.
        var img = Pictures.Draw(300, 260, c =>
        {
            c.DrawRect(new SKRect(20.4f, 15.2f, 140.7f, 60.9f), Pictures.Fill(new SKColor(200, 117, 51)));
            c.DrawCircle(210.3f, 90.6f, 30.2f, Pictures.Fill(new SKColor(46, 139, 87)));
            using var ink = Pictures.Stroke(SKColors.Black, 3);
            c.DrawLine(20, 150.5f, 280, 150.5f, ink);
            c.DrawLine(120.5f, 150.5f, 120.5f, 240, ink);
            c.DrawLine(180, 200, 270, 245, ink);
        });

        byte[] one = Run(img, 1), four = Run(img, 4), again = Run(img, 4);
        Assert.Equal(one, four);
        Assert.Equal(four, again);
    }

    private static byte[] Run(RasterImage img, int threads)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var set = ColourClusters.Find(img, options: new ColourClusterOptions { MaxThreads = threads });
        foreach (var k in set.Clusters)
        {
            w.Write(k.Rgb);
            w.Write(k.Share);
            foreach (var v in set.Coverage(k.Index).Values) w.Write(v);
            if (k.Index == set.BackgroundIndex()) continue;
            foreach (var region in Contours.Trace(set.Coverage(k.Index)))
                foreach (var path in region.Paths)
                    foreach (var p in path) { w.Write(p.x); w.Write(p.y); }
        }
        var mask = LineArt.Binarise(img, new LineArtOptions { MaxThreads = threads });
        w.Write(mask.Pixels);
        foreach (var d in DistanceTransform.Compute(mask, threads)) w.Write(d);
        var g = SkeletonGraph.Build(mask, new SkeletonGraphOptions { MaxThreads = threads });
        foreach (var n in g.Nodes) { w.Write(n.X); w.Write(n.Y); w.Write(n.Degree); }
        foreach (var e in g.Edges)
        {
            w.Write(e.From); w.Write(e.To); w.Write(e.MeanWidth);
            foreach (var p in e.Polyline) { w.Write(p.x); w.Write(p.y); }
        }
        foreach (var s in Segments.FromSkeleton(g)) { w.Write(s.A.x); w.Write(s.A.y); w.Write(s.B.x); w.Write(s.B.y); }
        w.Flush();
        return ms.ToArray();
    }
}
