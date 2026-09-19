using System.Text;
using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.Cli;

/// <summary>
/// One drawing, three encoders — the surface a picture verb writes its bytes through.
/// </summary>
/// <remarks>
/// <b>It encodes; it draws nothing</b>, which is what keeps it on the right side of the rule
/// <c>src/Cli/Authoring.cs</c> states. The caller's delegate is the only thing that touches the
/// canvas, and every pixel it puts there comes out of <c>CircuitRF.Render</c>.
///
/// <para><b>Shared rather than copied, and that is the point of the file existing.</b> It was
/// <c>Render.Emit</c>'s body; <c>rail</c> needed the same three lines for its report page
/// (brief-railrf-10-cli-verb.md R-rail10-8 — "no second export path"), and two copies of an encoder
/// diverge in exactly one visible way: the SVG font repair below gets applied by one of them. A
/// caller comparing two of this program's SVGs would then see a difference nobody chose.</para>
///
/// <para><b>The bytes are complete before anything reaches the filesystem.</b> Every caller writes
/// the returned array, so "a cancelled run writes nothing" is true by construction rather than by a
/// guard each verb has to remember.</para>
/// </remarks>
internal static class VectorPage
{
    /// <summary>A PDF page. <paramref name="w"/> and <paramref name="h"/> are points.</summary>
    internal static byte[] Pdf(int w, int h, Action<SKCanvas> draw)
    {
        var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = document.BeginPage(w, h);
            draw(canvas);
            document.EndPage();
            document.Close();
        }
        return stream.DetachAsData().ToArray();
    }

    /// <summary>
    /// An SVG page, through <see cref="SvgFontNormalizer"/>.
    /// </summary>
    /// <remarks>
    /// <b>The repair is not optional.</b> Skia writes each text run's per-glyph x/y list with a
    /// trailing separator, which Firefox reads as invalid and drops — putting every run a line above
    /// its baseline, where the clip eats it. The same function every clipboard export applies, which
    /// is why it had to come below the firewall with RND-2.
    /// </remarks>
    internal static byte[] Svg(int w, int h, Action<SKCanvas> draw)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream))
            draw(canvas);

        return Encoding.UTF8.GetBytes(
            SvgFontNormalizer.RepairPositionLists(
                Encoding.UTF8.GetString(stream.DetachAsData().ToArray())));
    }

    /// <summary>A raster page, in device pixels.</summary>
    /// <remarks>The bitmap arrives already zero-initialised because <c>LayoutRenderer</c> never
    /// <c>Clear</c>s (see its header) — which is what makes a transparent background mean
    /// anything.</remarks>
    internal static byte[] Png(int w, int h, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap)) draw(canvas);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? [];
    }
}
