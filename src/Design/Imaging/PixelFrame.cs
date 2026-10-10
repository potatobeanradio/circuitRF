// The one conversion out of pixel space — brief-img-1-raster-core.md R-im1-10.
//
// Pixel space is x right, y DOWN, origin at the top-left pixel's corner, pixel centres at ½. A layout is y up in DBU, a
// schematic has its own grid. Every later phase converts through a PixelFrame so the flip is written once: a phase that
// negated y itself would be a second place for the sign to be wrong.

using Clipper2Lib;

namespace CircuitRF.Design.Imaging;

/// <summary>Pixel space → a target space: <c>x' = OffsetX + ScaleX·x</c>, and <c>y' = OffsetY ± ScaleY·y</c> — minus
/// when <see cref="FlipY"/>.</summary>
/// <param name="ScaleX">Target units per pixel along x.</param>
/// <param name="ScaleY">Target units per pixel along y (positive; the sign is <see cref="FlipY"/>).</param>
/// <param name="OffsetX">Where pixel x = 0 lands.</param>
/// <param name="OffsetY">Where pixel y = 0 (the top edge) lands.</param>
/// <param name="FlipY">True for a y-up target.</param>
public readonly record struct PixelFrame(double ScaleX, double ScaleY, double OffsetX, double OffsetY, bool FlipY)
{
    /// <summary>Pixel space itself.</summary>
    public static PixelFrame Identity => new(1, 1, 0, 0, false);

    /// <summary>A y-up target at <paramref name="unitsPerPixel"/> with the picture's BOTTOM-left corner at
    /// (<paramref name="originX"/>, <paramref name="originY"/>) — how a picture lies on a layout.</summary>
    public static PixelFrame YUp(double unitsPerPixel, int heightPx, double originX = 0, double originY = 0) =>
        new(unitsPerPixel, unitsPerPixel, originX, originY + unitsPerPixel * heightPx, true);

    /// <summary>A y-down target at <paramref name="unitsPerPixel"/> with the picture's top-left corner at the origin
    /// given — how a picture lies on a schematic.</summary>
    public static PixelFrame YDown(double unitsPerPixel, double originX = 0, double originY = 0) =>
        new(unitsPerPixel, unitsPerPixel, originX, originY, false);

    public (double X, double Y) ToTarget(double x, double y) =>
        (OffsetX + ScaleX * x, FlipY ? OffsetY - ScaleY * y : OffsetY + ScaleY * y);

    public (double X, double Y) ToPixel(double x, double y) =>
        ((x - OffsetX) / ScaleX, FlipY ? (OffsetY - y) / ScaleY : (y - OffsetY) / ScaleY);

    /// <summary>A path carried into the target. A flip reverses winding, so the path is reversed with it: an outer
    /// boundary stays an outer boundary under Clipper2's orientation rule.</summary>
    public PathD ToTarget(PathD path)
    {
        var r = new PathD(path.Count);
        foreach (var p in path)
        {
            var (x, y) = ToTarget(p.x, p.y);
            r.Add(new PointD(x, y));
        }
        if (FlipY) r.Reverse();
        return r;
    }

    public PathsD ToTarget(PathsD paths)
    {
        var r = new PathsD(paths.Count);
        foreach (var p in paths) r.Add(ToTarget(p));
        return r;
    }
}
