// brief-em3d-29 R-em3d29-5 — Export picture… and Copy: the GPU's own pixels (read back from an offscreen image the
// view's backend drew with its own buffers), with — when asked — the legend and the caption painted over
// them, encoded as PNG. The picture is the one on screen at the chosen size; nothing here re-renders the
// scene. No video: every route to one is a native dependency (the brief's scope).

using SkiaSharp;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>
/// A picture of the 3D view read back from the GPU, with what is to be painted over it — taken on the UI
/// thread (the read-back holds the render lock), composed and encoded wherever the caller likes: at 4× a
/// window the composing and the encoding are the slow part, not the GPU.
/// </summary>
public sealed record FieldPictureShot(byte[] Rgba, int Width, int Height, float Scale, IReadOnlyList<string> Legend,
                                      ColorMap3D? Map, FieldColorScale? Range, string? Caption, bool Dark)
{
    /// <summary>The pixels with the legend and caption painted on (the caller disposes it).</summary>
    public SKBitmap Compose() => FieldPicture.Compose(Rgba, Width, Height, Scale, Legend, Map, Range, Caption, Dark);

    public byte[] Png() => FieldPicture.Png(Rgba, Width, Height, Scale, Legend, Map, Range, Caption, Dark);
}

public static class FieldPicture
{
    /// <summary>The largest side a picture may have: every backend's largest 2D texture (Metal and D3D11
    /// both 16,384), which the offscreen image has to be.</summary>
    public const int MaxSide = 16384;
    /// <summary>
    /// <paramref name="rgba"/> (<paramref name="width"/> × <paramref name="height"/>, rows top first) as a
    /// PNG, with <paramref name="legend"/>'s lines and a colour bar in <paramref name="map"/> at the top
    /// right and <paramref name="caption"/> at the bottom left, scaled by <paramref name="scale"/> (the
    /// export's multiple of the window, so the text is the size it is on screen).
    /// </summary>
    public static byte[] Png(byte[] rgba, int width, int height, float scale, IReadOnlyList<string> legend, ColorMap3D? map,
                             FieldColorScale? range, string? caption, bool dark)
    {
        using var bmp = Compose(rgba, width, height, scale, legend, map, range, caption, dark);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>As <see cref="Png"/>, unencoded: the composed pixels, RGBA8 premultiplied (opaque).</summary>
    public static SKBitmap Compose(byte[] rgba, int width, int height, float scale, IReadOnlyList<string> legend, ColorMap3D? map,
                                   FieldColorScale? range, string? caption, bool dark)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, width * height * 4));
        using (var canvas = new SKCanvas(bmp))
        {
            var ink = dark ? new SKColor(235, 235, 240) : new SKColor(30, 32, 38);
            var box = dark ? new SKColor(28, 30, 34, 215) : new SKColor(250, 250, 252, 225);
            using var font = new SKFont(SKTypeface.Default, 12 * scale);
            using var text = new SKPaint { Color = ink, IsAntialias = true };
            using var fill = new SKPaint { Color = box, IsAntialias = true };
            float pad = 8 * scale, line = 16 * scale;

            if (legend.Count > 0 && map is not null)
            {
                float barW = 220 * scale, barH = 12 * scale;
                float w = Math.Max(barW, legend.Max(l => font.MeasureText(l))) + 2 * pad;
                float h = pad * 2 + barH + line * (legend.Count + 1);
                float x0 = width - w - pad, y0 = pad;
                canvas.DrawRoundRect(new SKRect(x0, y0, x0 + w, y0 + h), 4 * scale, 4 * scale, fill);
                float y = y0 + pad + line * 0.8f;
                canvas.DrawText(legend[0], x0 + pad, y, font, text);
                y += line * 0.4f;
                var stops = map.Stops;
                using var bar = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(x0 + pad, 0), new SKPoint(x0 + pad + barW, 0),
                        [.. stops.Select(s => new SKColor(s.R, s.G, s.B))], [.. stops.Select(s => s.T)], SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(new SKRect(x0 + pad, y, x0 + pad + barW, y + barH), bar);
                y += barH + line * 0.9f;
                if (range is not null)
                {
                    string lo = FieldColorScale.G(range.Lo), hi = FieldColorScale.G(range.Hi);
                    canvas.DrawText(lo, x0 + pad, y, font, text);
                    canvas.DrawText(hi, x0 + pad + barW - font.MeasureText(hi), y, font, text);
                    y += line;
                }
                for (int i = 1; i < legend.Count; i++, y += line) canvas.DrawText(legend[i], x0 + pad, y, font, text);
            }
            if (caption is { Length: > 0 })
            {
                float w = font.MeasureText(caption) + 2 * pad;
                float y0 = height - pad - line - pad;
                canvas.DrawRoundRect(new SKRect(pad, y0, pad + w, y0 + line + pad), 4 * scale, 4 * scale, fill);
                canvas.DrawText(caption, 2 * pad, y0 + line * 0.85f, font, text);
            }
        }
        return bmp;
    }
}
