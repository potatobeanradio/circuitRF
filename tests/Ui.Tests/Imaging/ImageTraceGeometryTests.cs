// brief-img-3-trace-layout-image.md §4 — a layout picture traced at a stated scale lands on the layout it was drawn from
// (R-im3-4, R-im3-5).

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageTraceGeometryTests
{
    /// <summary>10 µm a pixel: 10,000 DBU, so half a pixel is 5,000 DBU. The picture's top-left is the origin, y up.</summary>
    private const double Dbu = 10_000, Half = 5_000;

    private static readonly Lazy<ImageTraceResult> Traced = new(() => ImageTrace.Trace(new ImageTraceInput
    {
        Source = TracePictures.Source(TracePictures.TwoLayerBoard()),
        Technology = TracePictures.Tech(),
        Scale = ImageScaleStatement.Stated(10e-6),
    }));

    private static (double X, double Y) At(double px, double py) => (px * Dbu, -py * Dbu);

    private static LayoutShape[] On(string layer) =>
        [.. Traced.Value.Layers.Single(l => l.Name == layer).Shapes];

    [Fact]
    public void EveryEdge_LandsWithinHalfAPixel_AndThe45DegreeJogIsExact()
    {
        var r = Traced.Value;
        Assert.True(r.Ok, r.Refusal);

        var (ax0, ay1) = At(TracePictures.RectA.X0, TracePictures.RectA.Y0);
        var (ax1, ay0) = At(TracePictures.RectA.X1, TracePictures.RectA.Y1);
        var a = Assert.Single(On("Top Copper").OfType<RectShape>());
        Assert.InRange(a.X1, ax0 - Half, ax0 + Half);
        Assert.InRange(a.X2, ax1 - Half, ax1 + Half);
        Assert.InRange(a.Y1, ay0 - Half, ay0 + Half);
        Assert.InRange(a.Y2, ay1 - Half, ay1 + Half);

        var (bx0, by1) = At(TracePictures.RectB.X0, TracePictures.RectB.Y0);
        var (bx1, by0) = At(TracePictures.RectB.X1, TracePictures.RectB.Y1);
        var b = Assert.Single(On("Bottom Copper").OfType<RectShape>());
        Assert.InRange(b.X1, bx0 - Half, bx0 + Half);
        Assert.InRange(b.X2, bx1 - Half, bx1 + Half);
        Assert.InRange(b.Y1, by0 - Half, by0 + Half);
        Assert.InRange(b.Y2, by1 - Half, by1 + Half);

        var jog = Assert.Single(On("Top Copper").OfType<PolygonShape>());
        Assert.Equal(TracePictures.Jog.Length, jog.Xy.Length / 2);
        foreach (var (px, py) in TracePictures.Jog)
        {
            var (x, y) = At(px, py);
            double nearest = Enumerable.Range(0, jog.Xy.Length / 2).Min(i => Math.Max(Math.Abs(jog.Xy[2 * i] - x), Math.Abs(jog.Xy[2 * i + 1] - y)));
            Assert.InRange(nearest, 0, Half);
        }
        int diagonals = 0;
        for (int i = 0; i < jog.Xy.Length / 2; i++)
        {
            int j = (i + 1) % (jog.Xy.Length / 2);
            long dx = jog.Xy[2 * j] - jog.Xy[2 * i], dy = jog.Xy[2 * j + 1] - jog.Xy[2 * i + 1];
            if (dx != 0 && dy != 0)
            {
                Assert.Equal(Math.Abs(dx), Math.Abs(dy));   // exactly 45°, not a DBU off it
                diagonals++;
            }
        }
        Assert.Equal(2, diagonals);

        var disc = Assert.Single(On("Top Copper").OfType<CircleShape>());
        var (cx, cy) = At(TracePictures.Disc.X, TracePictures.Disc.Y);
        Assert.InRange(disc.Cx, cx - Half, cx + Half);
        Assert.InRange(disc.Cy, cy - Half, cy + Half);
        Assert.InRange(disc.R, TracePictures.Disc.R * Dbu - Half, TracePictures.Disc.R * Dbu + Half);
    }

    [Fact]
    public void TheOverlapColour_LandsOnBothLayers()
    {
        var map = Traced.Value.LayerMap!;
        var overlap = Assert.Single(map.Rows, r => r.Overlap);
        Assert.Equal(["Bottom Copper", "Top Copper"], overlap.Layers.Order());
    }

    [Fact]
    public void ADrillCircle_BecomesAVia()
    {
        var via = Assert.Single(Traced.Value.Shapes.OfType<ViaShape>());
        var tech = TracePictures.Tech();
        Assert.Equal("Drill", tech.Layers.Single(l => l.Key == via.Layer).Name);
        Assert.Equal("Top Copper", tech.Layers.Single(l => l.Key == via.LandingLayer).Name);
        var (x, y) = At(TracePictures.Via.X, TracePictures.Via.Y);
        Assert.InRange(via.X, x - Half, x + Half);
        Assert.InRange(via.Y, y - Half, y + Half);
        Assert.InRange(via.DrillSize, 2 * TracePictures.Via.Drill * Dbu - 2 * Half, 2 * TracePictures.Via.Drill * Dbu + 2 * Half);
        Assert.InRange(via.PadSize, 2 * TracePictures.Via.R * Dbu - 2 * Half, 2 * TracePictures.Via.R * Dbu + 2 * Half);
        // The pad became the via's: it is not also a copper disc.
        Assert.Single(On("Top Copper").OfType<CircleShape>());
    }

    /// <summary>Designer round 29: a board drawn with its holes in the background's colour has no drill row. A small round
    /// hole in the pour is a via with the copper whole under it; a hole wider than a millimetre (a mounting hole's
    /// clearance) stays an opening.</summary>
    [Fact]
    public void ARoundHoleInThePour_BecomesAVia_AndAMountingHoleStaysAnOpening()
    {
        var picture = Pictures.Draw(400, 300, c =>
        {
            using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
            using var hole = Pictures.Fill(SKColors.White);
            c.DrawRect(SKRect.Create(20, 20, 360, 260), copper);
            c.DrawCircle(100, 150, 15, hole);   // 0.3 mm across at 10 µm/px
            c.DrawCircle(260, 150, 60, hole);   // 1.2 mm across
        });
        var r = ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(picture), Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(10e-6),
        });
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Shapes.OfType<ViaShape>());
        var (x, y) = At(100, 150);
        Assert.InRange(via.X, x - Half, x + Half);
        Assert.InRange(via.Y, y - Half, y + Half);
        Assert.InRange(via.DrillSize, 30 * Dbu - 2 * Half, 30 * Dbu + 2 * Half);
        // Stated as a designer would: a whole mil on this inch-unit technology (0.3 mm reads 12 mil, not 11.811).
        Assert.Equal(12 * 25_400, via.DrillSize);

        var pour = Assert.Single(r.Layers.Single(l => l.Name == "Top Copper").Shapes.OfType<PolygonShape>());
        var opening = Assert.Single(pour.Holes!);
        Assert.InRange(opening.Where((_, i) => i % 2 == 0).Max() - opening.Where((_, i) => i % 2 == 0).Min(), 120 * Dbu - 2 * Half, 120 * Dbu + 2 * Half);
    }

    /// <summary>A datasheet drawing's board: a green pour with ten holes each of three drills, every hole white inside a dark
    /// rim drawn on the hole's circle — <paramref name="mmPerPx"/> a pixel, the rim <paramref name="rimPx"/> wide.</summary>
    internal static RasterImage RimmedHoles(double[] drillsMm, double mmPerPx, float rimPx = 2) => Pictures.Draw(620, 120 * drillsMm.Length + 40, c =>
    {
        using var copper = Pictures.Fill(new SKColor(0xb8, 0xe0, 0x1e));
        using var hole = Pictures.Fill(SKColors.White);
        using var rim = Pictures.Stroke(new SKColor(0x10, 0x10, 0x10), rimPx);
        c.DrawRect(SKRect.Create(20, 20, 580, 120 * drillsMm.Length), copper);
        for (int row = 0; row < drillsMm.Length; row++)
            for (int k = 0; k < 10; k++)
            {
                float x = 60 + 55 * k, y = 80 + 120 * row, r = (float)(drillsMm[row] / mmPerPx / 2);
                c.DrawCircle(x, y, r, hole);
                c.DrawCircle(x, y, r, rim);
            }
    });

    /// <summary>brief-dfr30-1 R-dfr30-1-1 / R-dfr30-1-8 — a datasheet draws a hole white inside a dark rim, and the rim's
    /// colour is read as a second copper layer. Each hole is one via drilled on the rim's centre line (what was drawn), and
    /// no rim is written as copper: 1.5.7 made each ring whole, a solid disc on the rim's layer, and drilled the hole at
    /// the rim's outer edge — a 1.0 mm hole was then an opening AND a via.</summary>
    [Fact]
    public void ADarkRimmedHole_IsOneVia_DrilledOnTheRimsCentreLine_AndTheRimIsNoCopper()
    {
        var r = ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(RimmedHoles([0.3, 0.6, 1.0], 0.02)), Technology = TracePictures.Tech(),
            Scale = ImageScaleStatement.Stated(20e-6),
        });
        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.LayerMap!.Rows, row => row.Layers.Contains("Bottom Copper"));   // the rims are read as copper

        // 0.3, 0.6 and 1.0 mm on this inch-unit technology: 12, 24 and 39 mil, ten of each.
        var drills = r.Shapes.OfType<ViaShape>().GroupBy(v => v.DrillSize).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()));
        Assert.Equal([(12 * 25_400L, 10), (24 * 25_400L, 10), (39 * 25_400L, 10)], drills);
        Assert.DoesNotContain(r.Holes, h => h.Kind != ImageHoleKind.Via);
        Assert.Empty(r.Layers.Single(l => l.Name == "Bottom Copper").Shapes);
    }

    /// <summary>brief-dfr30-3 gate 2 — a board drawn as a closed outline, traced at a scale where it is 2.4 and 6 mm across
    /// (8 and 20 µm a pixel): its inside is no hole, at either scale, and the overlay draws no opening. Before, the near-square
    /// inside passed as "round", and at the small scale was kept as an opening the overlay drew as a board-sized circle.</summary>
    [Theory]
    [InlineData(8e-6)]
    [InlineData(20e-6)]
    public void ABoardDrawnAsAnOutline_IsNoHole_AtAnyScale(double metresPerPixel)
    {
        var r = ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(Pictures.Draw(400, 400, c =>
            {
                using var line = Pictures.Stroke(SKColors.Black, 2);
                c.DrawRect(SKRect.Create(50, 50, 300, 300), line);
            })),
            Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(metresPerPixel),
        });
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Holes);
        Assert.Empty(r.Shapes.OfType<ViaShape>());
        Assert.DoesNotContain(ImageOverlay.Build(r, null, TracePictures.Tech()).Items, i => i.Id.StartsWith(ImageOverlay.HolePrefix, StringComparison.Ordinal));
    }

    /// <summary>brief-dfr30-3 gate 3 — a hole must be round: in one pour a 1 mm circle is a via and a 1 mm square with its
    /// corners rounded by a fifth of its side (a pad's shape) is no hole at all. The 0.3, 0.6 and 1.0 mm circles of
    /// <see cref="ADarkRimmedHole_IsOneVia_DrilledOnTheRimsCentreLine_AndTheRimIsNoCopper"/> stay vias.</summary>
    [Fact]
    public void ARoundedSquareTheSizeOfAPad_IsNoHole_WhereACircleIsAVia()
    {
        var r = ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(Pictures.Draw(400, 200, c =>
            {
                using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
                using var hole = Pictures.Fill(SKColors.White);
                c.DrawRect(SKRect.Create(20, 20, 360, 160), copper);
                c.DrawCircle(100, 100, 25, hole);
                c.DrawRoundRect(SKRect.Create(250, 75, 50, 50), 10, 10, hole);
            })),
            Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(20e-6),
        });
        Assert.True(r.Ok, r.Refusal);
        var via = Assert.Single(r.Shapes.OfType<ViaShape>());
        Assert.InRange(via.X, 95 * 20_000, 105 * 20_000);
        Assert.All(r.Holes, h => Assert.True(h.Circle.Cx < 200));
    }

    /// <summary>A datasheet's hand-drawn hole traces as a pixel staircase, whose perimeter is long for its area: a 24 px
    /// disc's staircase has a circularity near 0.62, below a square's 0.79. It is still a round hole, so the test is box
    /// fill, not circularity — every via of a line-drawn board was dropped while it was circularity ≥ 0.85.</summary>
    [Fact]
    public void AStaircaseCircle_IsARoundHole()
    {
        const int r = 12;
        var right = new List<Clipper2Lib.PointD>();
        var left = new List<Clipper2Lib.PointD>();
        for (int y = -r; y < r; y++)
        {
            double x = Math.Floor(Math.Sqrt(r * r - (y + 0.5) * (y + 0.5)));
            right.Add(new(x, y)); right.Add(new(x, y + 1));
            left.Add(new(-x, y)); left.Add(new(-x, y + 1));
        }
        left.Reverse();
        var staircase = new Clipper2Lib.PathD(right.Concat(left));
        var c = Assert.NotNull(ImageTrace.RoundHole(staircase));
        Assert.InRange(c.R, 10.5, 12.5);
    }

    /// <summary>brief-dfr30-1 R-dfr30-1-7 / gate 6 — a board drawn as outlines alone is said to be a line drawing, and the
    /// same board filled is not.</summary>
    [Fact]
    public void ALineDrawing_IsSaidToBeOne_AndAFilledBoardIsNot()
    {
        static ImageTraceResult Read(bool filled) => ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(Pictures.Draw(400, 300, c =>
            {
                using var paint = filled ? Pictures.Fill(new SKColor(0x20, 0x20, 0x20)) : Pictures.Stroke(new SKColor(0x20, 0x20, 0x20), 2);
                c.DrawRect(SKRect.Create(20, 20, 160, 110), paint);
                c.DrawRect(SKRect.Create(220, 20, 160, 110), paint);
                c.DrawRect(SKRect.Create(20, 170, 360, 16), paint);
                c.DrawRect(SKRect.Create(20, 210, 360, 70), paint);
            })),
            Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(20e-6),
        });
        var outlines = Read(filled: false);
        Assert.True(outlines.LineArt);
        Assert.Equal(ImageTrace.LineArtNote, outlines.Report.Of(CircuitRF.Design.Layout.Recognition.RecognitionFindingClass.ImageLineArt)?.Sentence);
        Assert.False(Read(filled: true).LineArt);
    }

    /// <summary>A line drawing's board: a pour drawn as a 2 px outline round (20,20)–(380,280), with two vias drawn as 2 px
    /// circles 20 px across (0.4 mm at 20 µm a pixel) inside it. A line drawing reads no copper from its lines.</summary>
    private static ImageTraceInput OutlinedPour(params ImageCorrection[] fills) => new()
    {
        Source = TracePictures.Source(Pictures.Draw(400, 300, c =>
        {
            using var line = Pictures.Stroke(SKColors.Black, 2);
            c.DrawRect(SKRect.Create(20, 20, 360, 260), line);
            c.DrawCircle(100, 150, 10, line);
            c.DrawCircle(300, 150, 10, line);
        })),
        Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(20e-6),
        Corrections = fills,
    };

    /// <summary>Silkscreen is off by default (owner, 2026-10-28): a colour drawn as rows of lettering is kept out of the
    /// copper and not read at all — the user sets its row to Silkscreen to have it.</summary>
    [Fact]
    public void AColourThatLooksLikeSilkscreen_IsIgnoredByDefault()
    {
        var legend = new SKColor(0x30, 0x30, 0xc0);
        var r = ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(Pictures.Draw(600, 400, c =>
            {
                using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
                c.DrawRect(SKRect.Create(20, 20, 200, 360), copper);
                using var ink = Pictures.Fill(legend);
                using var font = new SKFont(SKTypeface.Default, 16);
                for (int row = 0; row < 8; row++) c.DrawText("PCB SIZE=17.0mm x 17.0mm", 260, 60 + 40 * row, font, ink);
            })),
            Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(20e-6),
        });
        Assert.True(r.Ok, r.Refusal);
        int k = Pictures.ClusterOf(r.Clusters!, (legend.Red << 16) | (legend.Green << 8) | legend.Blue);
        Assert.Equal(ImageLayerRole.Ignore, r.LayerMap!.Rows.Single(row => row.Cluster == k).Role);
        Assert.DoesNotContain(r.LayerMap.Rows, row => row.Role == ImageLayerRole.Silkscreen);
    }

    /// <summary>A line drawing's lines are not copper (owner, 2026-10-28): with nothing painted it reads no copper and no
    /// vias, so its outlines, lettering and any table beside the board are never copper to remove first.</summary>
    [Fact]
    public void ALineDrawing_ReadsNoCopper_UntilPainted()
    {
        var r = ImageTrace.Trace(OutlinedPour());
        Assert.True(r.Ok, r.Refusal);
        Assert.True(r.LineArt);
        Assert.DoesNotContain(r.Shapes, s => s is not BitmapShape);
    }

    /// <summary>Only the copper inside the board outline drawn is read (owner, 2026-10-28), and the scale says how big that
    /// board is — else the crop, else the whole picture — so a scale taken against the wrong stackup is seen at once.</summary>
    [Fact]
    public void CopperOutsideTheDrawnOutline_IsNotRead_AndTheBoardsSizeIsSaid()
    {
        static ImageTraceResult Read(PixelRect? crop, params ImageCorrection[] fixes) => ImageTrace.Trace(new ImageTraceInput
        {
            Source = TracePictures.Source(Pictures.Draw(400, 200, c =>
            {
                using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
                c.DrawRect(SKRect.Create(20, 20, 160, 160), copper);    // the board's copper
                c.DrawRect(SKRect.Create(250, 40, 120, 30), copper);    // a table beside it
            })),
            Technology = TracePictures.Tech(), Scale = ImageScaleStatement.Stated(100e-6), Scope = crop,
            Corrections = fixes,
        });
        var board = Read(null, ImageCorrection.Outline(ImageCorrection.Rectangle(10, 10, 190, 190)));
        Assert.Single(board.Layers.Single(l => l.Name == "Top Copper").Shapes);
        Assert.Equal("Board 18 mm × 18 mm", board.SizeLine);
        Assert.Equal("Crop 30 mm × 15 mm", Read(new PixelRect(0, 0, 300, 150)).SizeLine);
        Assert.Equal("Picture 40 mm × 20 mm", Read(null).SizeLine);
    }

    /// <summary>The Paint Fill tool: a click inside a drawn outline paints what it encloses as copper, its edge on the line's
    /// centre line, and the round holes drawn inside it are vias drilled on their circles (16 mil for 0.4 mm).</summary>
    [Fact]
    public void APaintFill_PaintsTheOutlinedArea_OnTheLinesCentre_AndItsDrawnHolesAreVias()
    {
        var r = ImageTrace.Trace(OutlinedPour(ImageCorrection.PaintFill(60, 60, "Top Copper")));
        Assert.True(r.Ok, r.Refusal);
        Assert.All(r.Corrections, o => Assert.True(o.Applied, o.Refusal));

        // The vias' holes made whole, the pour is the rectangle the outline was drawn on.
        long[] xy = Assert.Single(r.Layers.Single(l => l.Name == "Top Copper").Shapes) switch
        {
            RectShape rect => [rect.X1, rect.Y1, rect.X2, rect.Y2],
            PolygonShape poly => poly.Xy,
            var other => throw new InvalidOperationException(other.GetType().Name),
        };
        const double px = 20_000;   // DBU a pixel at 20 µm
        double X(Func<IEnumerable<long>, long> f) => f(xy.Where((_, i) => i % 2 == 0)) / px;
        double Y(Func<IEnumerable<long>, long> f) => -f(xy.Where((_, i) => i % 2 == 1)) / px;
        Assert.Equal((20, 20, 380, 280), ((int)Math.Round(X(Enumerable.Min)), (int)Math.Round(Y(Enumerable.Max)), (int)Math.Round(X(Enumerable.Max)), (int)Math.Round(Y(Enumerable.Min))));
        var vias = r.Shapes.OfType<ViaShape>().ToList();
        Assert.Equal(2, vias.Count);
        Assert.All(vias, v => Assert.Equal(16 * 25_400, v.DrillSize));
    }

    /// <summary>A via whose picture draws no pad takes the technology's pad only up to twice its drill: read at a scale too
    /// small, a 0.6 mm pad on a 0.12 mm drill drew as a dark disc six holes wide (owner, 2026-10-28).</summary>
    [Fact]
    public void AViaWithNoPadDrawn_HasAPadNoWiderThanTwiceItsDrill()
    {
        var r = ImageTrace.Trace(OutlinedPour(ImageCorrection.PaintFill(60, 60, "Top Copper")) with { Scale = ImageScaleStatement.Stated(6e-6) });
        var vias = r.Shapes.OfType<ViaShape>().ToList();
        Assert.Equal(2, vias.Count);
        Assert.All(vias, v => Assert.InRange(v.PadSize, v.DrillSize + 1, 2 * v.DrillSize));
    }

    /// <summary>A Paint Fill on the picture's background — the flood reaching all four edges — is refused with its sentence.</summary>
    [Fact]
    public void APaintFillOnTheBackground_IsRefused()
    {
        var r = ImageTrace.Trace(OutlinedPour(ImageCorrection.PaintFill(5, 5, "Top Copper")));
        Assert.Equal(ImageTrace.FillBackgroundRefusal, r.Corrections[^1].Refusal);
    }
}
