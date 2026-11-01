// The port pass asked for each port's conductor every frame, and each answer is a polygon union of the copper
// overlapping it — 98 % of a frame on a 14-port evaluation board, at any zoom. LayoutConductorCache keeps the
// answers across frames until the model changes. Counters, not timing.

using CircuitRF.Render;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public sealed class LayoutConductorCacheTests
{
    private static readonly LayerKey Copper = new(1, 0);

    private static Technology Tech() => new()
    {
        Layers = { new LayerDef { Key = Copper, Name = "Top Copper", Color = new Rgba(200, 120, 40) } },
    };

    /// <summary>Two traces crossing a pour on the same layer, a port on each trace's end: the case that unions.</summary>
    private static LayoutView Board()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        view.Shapes.Add(new RectShape { Layer = Copper, X1 = -20_000, Y1 = -20_000, X2 = 20_000, Y2 = 20_000 });
        for (int k = 0; k < 2; k++)
        {
            long y = k * 10_000;
            view.Shapes.Add(new RectShape { Layer = Copper, X1 = -40_000, Y1 = y - 1_000, X2 = 0, Y2 = y + 1_000 });
            view.Shapes.Add(new LabelShape
            {
                Layer = Copper, PortLayer = Copper, X = -40_000, Y = y, Text = $"P{k + 1}", Height = 1_000, IsPort = true,
                PortDirection = LayoutRotation.R0,
            });
        }
        return view;
    }

    private static byte[] Draw(LayoutView view, Technology tech, LayoutViewport vp, LayoutConductorCache? cache)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp,
                            new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ConductorCache = cache });
        using var image = surface.Snapshot();
        return image.PeekPixels().GetPixelSpan().ToArray();
    }

    [Fact]
    public void APanAsksForNoConductorAgain_AnEditDoes_AndThePictureIsUnchanged()
    {
        var view = Board();
        var tech = Tech();   // one instance, as the canvas hands its DrawTechnology: a new one starts the cache again
        var vp = LayoutViewport.ZoomToFit(new Bbox(-45_000, -25_000, 25_000, 25_000), 500, 360, 0.05);
        var cache = new LayoutConductorCache();

        var first = Draw(view, tech, vp, cache);
        int misses = cache.Misses;
        Assert.True(misses > 0);
        Assert.Equal(Draw(view, tech, vp, null), first);

        Draw(view, tech, vp with { PanX = vp.PanX + 30 / vp.Zoom }, cache);
        Assert.Equal(misses, cache.Misses);

        cache.Clear();   // what the canvas does on Model.Changed
        Draw(view, tech, vp, cache);
        Assert.True(cache.Misses > misses);
    }
}
