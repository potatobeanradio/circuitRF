// Layout pictures for the IM-3 gates, drawn in memory from a known layout — brief-img-3-trace-layout-image.md §4.
// Never a committed file.

using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class TracePictures
{
    public const string TechId = "pcb-2layer_RO4350B_30mil_1oz";

    public static Technology Tech() => ShippedTechnologies.Load(TechId);

    public static ImageSource Source(RasterImage raster, string name = "board.png") =>
        ImageSource.FromBytes(Pictures.Png(raster), name).Source!;

    // ── the two-layer board (pixels) ─────────────────────────────────────────────────────────────────

    public static readonly SKColor Top = new(0xd0, 0x20, 0x20, 0x80);     // drawn translucent over everything
    public static readonly SKColor Bottom = new(0x30, 0x50, 0xc0);

    /// <summary>Rect A (top), x0, y0, x1, y1.</summary>
    public static readonly (double X0, double Y0, double X1, double Y1) RectA = (40.3, 30.7, 150.6, 80.2);

    /// <summary>Rect B (bottom), overlapping A in a 30 × 30 px square.</summary>
    public static readonly (double X0, double Y0, double X1, double Y1) RectB = (120.4, 50.2, 230.8, 120.6);

    /// <summary>A 16 px trace with a 45° jog (top).</summary>
    public static readonly (double X, double Y)[] Jog =
        [(60, 150), (143.3137, 150), (183.3137, 190), (260, 190), (260, 206), (176.6863, 206), (136.6863, 166), (60, 166)];

    /// <summary>A round pad (top).</summary>
    public static readonly (double X, double Y, double R) Disc = (320, 70, 22.5);

    /// <summary>A via: a round pad (top) with a black drill hole in it.</summary>
    public static readonly (double X, double Y, double R, double Drill) Via = (320, 200, 20, 8);

    public static RasterImage TwoLayerBoard() => Pictures.Draw(400, 300, c =>
    {
        using var bottom = Pictures.Fill(Bottom);
        using var top = Pictures.Fill(Top);
        using var drill = Pictures.Fill(SKColors.Black);
        c.DrawRect(SKRect.Create((float)RectB.X0, (float)RectB.Y0, (float)(RectB.X1 - RectB.X0), (float)(RectB.Y1 - RectB.Y0)), bottom);

        // The top layer is drawn as ONE path, so its own shapes do not darken where they meet.
        using var path = new SKPath();
        path.AddRect(SKRect.Create((float)RectA.X0, (float)RectA.Y0, (float)(RectA.X1 - RectA.X0), (float)(RectA.Y1 - RectA.Y0)));
        path.MoveTo((float)Jog[0].X, (float)Jog[0].Y);
        foreach (var (x, y) in Jog.Skip(1)) path.LineTo((float)x, (float)y);
        path.Close();
        path.AddCircle((float)Disc.X, (float)Disc.Y, (float)Disc.R);
        path.AddCircle((float)Via.X, (float)Via.Y, (float)Via.R);
        c.DrawPath(path, top);
        c.DrawCircle((float)Via.X, (float)Via.Y, (float)Via.Drill, drill);
    });

    // ── land patterns ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Chip pairs drawn from the generator's own references at <paramref name="umPerPx"/>: each pair lies along x,
    /// at its grid position.</summary>
    public static RasterImage Pairs(double umPerPx, params (string Code, double X, double Y)[] pairs) => Pictures.Draw(600, 300, c =>
    {
        using var copper = Pictures.Fill(new SKColor(0xc8, 0x75, 0x33));
        foreach (var (code, x, y) in pairs)
        {
            var r = LandPatternMatch.References.All.First(r => r.Case.Code == code && r.Density == DensityLevel.Nominal);
            float along = (float)(r.AlongUm / umPerPx), across = (float)(r.AcrossUm / umPerPx), gap = (float)(r.GapUm / umPerPx);
            c.DrawRect(SKRect.Create((float)x, (float)y, along, across), copper);
            c.DrawRect(SKRect.Create((float)x + along + gap, (float)y, along, across), copper);
        }
    });
}
