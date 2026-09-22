// The four CLOSED primitives — rectangle, rounded rectangle, oval, circle — drawn so that a paint's
// PATH EFFECT survives vector export.
//
// Skia's SVG device writes drawRect/drawRRect/drawOval/drawCircle out as <rect>/<ellipse>/<circle>
// elements and then calls addPaint to attach the stroke. A path effect cannot be expressed on a
// primitive element, so addPaint prints "Unsupported path effect in addPaint." to stderr and emits
// the element WITHOUT the effect: a dashed selection box exports as a SOLID one. The only warning is
// that line of native console noise, which nothing reads on a build machine.
//
// drawPath has no such branch — the SVG device flattens the effect into the path's own geometry, so
// the dash lands in the emitted <path d="..."> as real segments. These helpers take the second route
// whenever a path effect is present, and fall through to the primitive when it is not, so nothing
// that was already correct changes shape or cost.
//
// Raster and PDF are unaffected either way (both devices convert the primitive to a path internally
// before applying the effect), so this is a vector-export repair that the on-screen path pays nothing
// for. See src/Render/RESOLVED.md.

using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// <see cref="SKCanvas"/> closed-primitive draws that keep a paint's <see cref="SKPathEffect"/> when
/// the canvas is an SVG one.
/// </summary>
/// <remarks>
/// <b>Reach for these at any call site whose paint CAN carry a path effect</b>, including one where
/// it is conditional (<c>PathEffect = crossing ? dash : null</c>) or arrives from a caller as an
/// override paint. A site whose paint is unconditionally solid should keep the plain primitive — the
/// name is then a claim about the paint that is not true, and the reader has to check.
///
/// <para>The path is built per call rather than cached. A closed primitive costs one
/// <see cref="SKPath"/> allocation, which is far below the cost of the dash geometry Skia is about to
/// build from it, and a cached instance would have to be thread-static: layout tiles render in
/// parallel.</para>
/// </remarks>
public static class DashSafeShapes
{
    /// <summary>A rectangle, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawRectDashSafe(this SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(paint);

        if (paint.PathEffect is null) { canvas.DrawRect(rect, paint); return; }

        using var path = new SKPath();
        path.AddRect(rect.Standardized);
        canvas.DrawPath(path, paint);
    }

    /// <summary>A rounded rectangle, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawRoundRectDashSafe(this SKCanvas canvas, SKRect rect, float rx, float ry, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(paint);

        if (paint.PathEffect is null) { canvas.DrawRoundRect(rect, rx, ry, paint); return; }

        using var path = new SKPath();
        path.AddRoundRect(rect.Standardized, rx, ry);
        canvas.DrawPath(path, paint);
    }

    /// <summary>A rounded rectangle, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawRoundRectDashSafe(this SKCanvas canvas, SKRoundRect rrect, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(rrect);
        ArgumentNullException.ThrowIfNull(paint);

        if (paint.PathEffect is null) { canvas.DrawRoundRect(rrect, paint); return; }

        using var path = new SKPath();
        path.AddRoundRect(rrect);
        canvas.DrawPath(path, paint);
    }

    /// <summary>An oval, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawOvalDashSafe(this SKCanvas canvas, SKRect rect, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(paint);

        if (paint.PathEffect is null) { canvas.DrawOval(rect, paint); return; }

        using var path = new SKPath();
        path.AddOval(rect.Standardized);
        canvas.DrawPath(path, paint);
    }

    /// <summary>An oval about a centre, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawOvalDashSafe(this SKCanvas canvas, float cx, float cy, float rx, float ry, SKPaint paint)
        => canvas.DrawOvalDashSafe(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), paint);

    /// <summary>A circle, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawCircleDashSafe(this SKCanvas canvas, float cx, float cy, float r, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(paint);

        if (paint.PathEffect is null) { canvas.DrawCircle(cx, cy, r, paint); return; }

        using var path = new SKPath();
        path.AddCircle(cx, cy, r);
        canvas.DrawPath(path, paint);
    }

    /// <summary>A circle, keeping <paramref name="paint"/>'s path effect under SVG export.</summary>
    public static void DrawCircleDashSafe(this SKCanvas canvas, SKPoint c, float r, SKPaint paint)
        => canvas.DrawCircleDashSafe(c.X, c.Y, r, paint);
}
