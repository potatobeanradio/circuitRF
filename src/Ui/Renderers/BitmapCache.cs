// docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md §2 (R-bmp-1): the ONE decode cache for
// every bitmap-carrying host in this app — symbol primitives (BitmapPrimitive), schematic canvas
// objects (SchematicBitmap), and now layout shapes (BitmapShape). Was private-static inside
// SchematicRenderer; extracted so LayoutRenderer doesn't reach into a sibling renderer for it and
// doesn't start a second cache that decodes the same file twice and misses invalidations.
//
// SchematicRenderer.InvalidateBitmapCache / TryGetBitmapPixelSize remain as thin forwarders to this
// class — no existing caller (SchematicViewModel, SymbolEditorViewModel) needed to change.

using System.Collections.Concurrent;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

public static class BitmapCache
{
    // null value = path tried and failed (broken ref); avoids repeated I/O each frame.
    private static readonly ConcurrentDictionary<string, SKBitmap?> _cache = new(StringComparer.Ordinal);

    public static SKBitmap? Load(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return _cache.GetOrAdd(path, static p =>
        {
            try   { return SKBitmap.Decode(p); }
            catch { return null; }
        });
    }

    public static void Invalidate(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _cache.TryRemove(path, out _);
    }

    /// <summary>Native pixel dimensions of the image at <paramref name="path"/> via the shared decode
    /// cache, or null if the path is empty/unreadable. Used to size a freshly-dropped/inserted bitmap
    /// to its true aspect ratio.</summary>
    public static (int Width, int Height)? TryGetPixelSize(string path)
    {
        var bmp = Load(path);
        return bmp is null ? null : (bmp.Width, bmp.Height);
    }

    /// <summary>The one broken-file visual (R-bmp-1: "one broken-file visual") — a dashed rectangle
    /// with an X through it, axis-aligned in DEVICE-pixel space. Shared by the schematic canvas-object
    /// bitmap path and the new layout bitmap path, both of which are axis-aligned (no per-shape
    /// rotation). The symbol PRIMITIVE bitmap path is deliberately NOT routed through this — a
    /// <c>BitmapPrimitive</c> renders under the component's own rotate/mirror transform, so its broken
    /// placeholder is a sheared quad (3 explicit corner points), not this simple axis-aligned rect; it
    /// stays its own small inline draw in <see cref="SchematicRenderer"/>, same visual style
    /// (dashed box + X), different geometry.</summary>
    public static void DrawBrokenPlaceholder(SKCanvas canvas, float x, float y, float w, float h, SKColor warningColor)
    {
        using var dashEffect = SKPathEffect.CreateDash([6f, 4f], 0);
        using var strokePaint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f, Color = warningColor.WithAlpha(180),
            PathEffect = dashEffect,
        };
        canvas.DrawRect(SKRect.Create(x, y, w, h), strokePaint);
        strokePaint.PathEffect = null;
        strokePaint.StrokeWidth = 1f;
        canvas.DrawLine(x,     y,     x + w, y + h, strokePaint);
        canvas.DrawLine(x + w, y,     x,     y + h, strokePaint);
    }
}
