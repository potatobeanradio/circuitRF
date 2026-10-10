// brief-img-1-raster-core.md R-im1-2 — a three-colour drawing gives three clusters, the same with the same seed twice;
// two colours ΔE 6 apart merge.

using System.Linq;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ColourClustersTests
{
    [Fact]
    public void AThreeColourDrawing_GivesThreeClusters_TheSameTwice()
    {
        var img = Pictures.Draw(160, 100, c =>
        {
            c.DrawRect(new SKRect(12.3f, 10.6f, 80.2f, 40.4f), Pictures.Fill(new SKColor(200, 30, 30)));
            c.DrawCircle(115.4f, 62.7f, 24.1f, Pictures.Fill(new SKColor(30, 60, 200)));
        });

        var a = ColourClusters.Find(img);
        var b = ColourClusters.Find(img);
        Assert.Equal(3, a.Clusters.Count);
        Assert.Equal(a.Clusters.Select(k => k.Rgb), b.Clusters.Select(k => k.Rgb));
        for (int k = 0; k < 3; k++) Assert.Equal(a.Coverage(k).Values, b.Coverage(k).Values);

        Assert.Equal(0xFFFFFF, a.Clusters[a.BackgroundIndex()].Rgb);
        int red = Pictures.ClusterOf(a, 0xC81E1E), blue = Pictures.ClusterOf(a, 0x1E3CC8);
        Assert.Equal(0xC81E1E, a.Clusters[red].Rgb);          // anti-aliasing does not pull a colour toward the ground
        Assert.Equal(0x1E3CC8, a.Clusters[blue].Rgb);
    }

    [Fact]
    public void TwoColoursDeltaESixApart_Merge()
    {
        var c1 = new SKColor(106, 106, 106);
        var c2 = new SKColor(121, 121, 121);
        Assert.InRange(CieLab.FromRgb(106, 106, 106).DeltaE(CieLab.FromRgb(121, 121, 121)), 5.5, 6.5);
        var img = Pictures.Draw(80, 60, c => c.DrawRect(new SKRect(40, 0, 80, 60), Pictures.Fill(c2)), ground: c1);

        var set = ColourClusters.Find(img);
        Assert.Single(set.Clusters);
    }
}
