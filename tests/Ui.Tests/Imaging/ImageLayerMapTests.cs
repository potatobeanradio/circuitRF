// brief-img-3-trace-layout-image.md §4 — which colour is which layer (R-im3-4, R-im3-8, D7).

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageLayerMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-imgmap-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly SKColor Copper = new(0xc8, 0x75, 0x33), Green = new(0x2e, 0x8b, 0x57);

    /// <summary>Copper covering most of the picture, but white all round it.</summary>
    private static RasterImage MostlyCopper() => Pictures.Draw(300, 200, c =>
    {
        using var copper = Pictures.Fill(Copper);
        using var green = Pictures.Fill(Green);
        c.DrawRect(12, 12, 276, 176, copper);
        c.DrawRect(40, 40, 60, 30, green);
    });

    private static ImageTraceResult Trace(RasterImage raster, ImageLayerMap? map = null) => ImageTrace.Trace(new ImageTraceInput
    {
        Source = TracePictures.Source(raster), Technology = TracePictures.Tech(), LayerMap = map,
    });

    [Fact]
    public void TheBackground_IsTheColourTouchingTheBorder_NotTheLargest()
    {
        var r = Trace(MostlyCopper());
        var clusters = r.Clusters!.Clusters;
        Assert.True(clusters.Max(c => c.Share) > 0.6);
        var bg = Assert.Single(r.LayerMap!.Rows, x => x.Role == ImageLayerRole.Background);
        Assert.Equal(0xffffff, bg.Rgb);
        Assert.Equal(["Top Copper"], r.LayerMap.Rows.Single(x => x.Share > 0.6).Layers);
    }

    [Fact]
    public void AUserEditedMap_SurvivesARerun()
    {
        var first = Trace(MostlyCopper());
        var copper = first.LayerMap!.Rows.Single(x => x.Share > 0.6);
        var edited = first.LayerMap.With(copper.Cluster, ImageLayerRole.Layer, "Bottom Copper");

        var again = Trace(MostlyCopper(), edited);

        Assert.True(again.LayerMap!.Edited);
        Assert.Equal(["Bottom Copper"], again.LayerMap.Rows.Single(x => x.Share > 0.6).Layers);
        Assert.Contains(again.Layers, l => l.Name == "Bottom Copper" && l.PixelRegions.Count > 0);
    }

    [Fact]
    public void APreset_IsOfferedForAPictureOfTheSameColours()
    {
        var first = Trace(MostlyCopper());
        ImageTracePresets.Save(ImageTracePresets.From("bench board", first.LayerMap!, new ImageTraceOptions()), _dir);

        // The same two colours drawn differently: offered. A different copper colour: not.
        var same = ColourClusters.Find(Pictures.Draw(300, 200, c =>
        {
            using var copper = Pictures.Fill(Copper);
            using var green = Pictures.Fill(Green);
            c.DrawRect(50, 20, 120, 150, copper);
            c.DrawRect(200, 100, 70, 70, green);
        }));
        var other = ColourClusters.Find(Pictures.Draw(300, 200, c =>
        {
            using var gold = Pictures.Fill(new SKColor(0xe0, 0xc0, 0x20));
            using var green = Pictures.Fill(Green);
            c.DrawRect(50, 20, 120, 150, gold);
            c.DrawRect(200, 100, 70, 70, green);
        }));

        var offered = Assert.Single(ImageTracePresets.Matching(same, _dir));
        Assert.Equal("bench board", offered.Name);
        Assert.Empty(ImageTracePresets.Matching(other, _dir));
        Assert.True(ImageTracePresets.Apply(offered, same).Edited);
    }
}
