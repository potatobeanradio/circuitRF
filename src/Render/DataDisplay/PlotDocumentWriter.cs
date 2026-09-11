// ================================================================
//  PlotDocumentWriter.cs  —  a drawn page, as PDF bytes or SVG text
//
//  RND-4. Both were `PlotExporter`'s and neither needed a window: an
//  SKDocument and an SKSvgCanvas over a memory stream, plus the SVG
//  repair every emitted SVG in this repo passes through
//  (SvgFontNormalizer, RND-1). They came down so `circuitrf render`
//  writes the SAME bytes rather than its own — which is §5.1's gate.
//
//  The PDF metadata is `Creator = "circuitRF"` and NOTHING ELSE, which
//  is deliberate and load-bearing: SKDocument writes a CreationDate only
//  when one is given, so two runs of the same composition are byte-
//  identical with no exclusion at all. RND-1 §5.2 allowed for excluding
//  a metadata date by name; this format does not need it.
// ================================================================

using System;
using System.IO;
using System.Text;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

public static class PlotDocumentWriter
{
    /// <summary>
    /// Writes a one-page PDF to <paramref name="path"/>, its metadata Title taken from the file
    /// name — which is what the Data Display's Export has always written, and the reason this is a
    /// path-taking method rather than bytes plus a File.WriteAllBytes: the title is part of the
    /// document, so a caller that did not know to set it would produce a different file.
    /// </summary>
    public static void WritePdf(string path, Action<SKCanvas> render, PagePlacement page)
        => File.WriteAllBytes(path, BuildPdfBytes(render, page, PdfTitleFor(path)));

    /// <summary>
    /// The metadata Title a PDF written to <paramref name="path"/> carries. Public because
    /// <c>circuitrf render</c> builds its bytes BEFORE opening the file — a cancelled run must leave
    /// no output — and would otherwise write a PDF that differs from the application's in one field.
    /// </summary>
    public static string PdfTitleFor(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// Writes an SVG to <paramref name="path"/>. Built in memory and then written, rather than
    /// straight to the file: the document has to be complete before
    /// <see cref="SvgFontNormalizer.RepairPositionLists"/> can run over it, and an exported plot is
    /// a few hundred kilobytes at most.
    /// </summary>
    public static void WriteSvg(string path, Action<SKCanvas> render, PagePlacement page)
        => File.WriteAllText(path, BuildSvgString(render, page), new UTF8Encoding(false));

    /// <summary>
    /// PDF bytes. <paramref name="title"/> null is the clipboard's form, which has no file name to
    /// take one from; a written file passes <see cref="PdfTitleFor"/>.
    /// </summary>
    public static byte[] BuildPdfBytes(Action<SKCanvas> render, PagePlacement page, string? title = null)
    {
        var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
        if (title is not null) metadata.Title = title;
        using var skStream = new SKDynamicMemoryWStream();
        using var doc      = SKDocument.CreatePdf(skStream, metadata);
        var canvas = doc.BeginPage(page.Width, page.Height);
        using (PlotDocumentScope.Enter()) render(canvas);
        doc.EndPage();
        doc.Close();
        return skStream.DetachAsData().ToArray();
    }

    /// <summary>
    /// A multi-PAGE PDF — one page per <paramref name="pages"/> entry. R-rnd4-5: multi-page is
    /// PDF's alone, because <c>SKDocument</c> is a multi-page format and SVG and PNG are not, and
    /// inventing <c>out-1.png</c>, <c>out-2.png</c> from one <c>-o</c> is a filename the tool made
    /// up (R-rnd0-6 forbids it).
    /// </summary>
    public static byte[] BuildPdfBytes(System.Collections.Generic.IReadOnlyList<Action<SKCanvas>> pages,
                                       PagePlacement page)
    {
        var metadata = new SKDocumentPdfMetadata { Creator = "circuitRF" };
        using var skStream = new SKDynamicMemoryWStream();
        using var doc      = SKDocument.CreatePdf(skStream, metadata);
        foreach (var render in pages)
        {
            var canvas = doc.BeginPage(page.Width, page.Height);
            using (PlotDocumentScope.Enter()) render(canvas);
            doc.EndPage();
        }
        doc.Close();
        return skStream.DetachAsData().ToArray();
    }

    public static string BuildSvgString(Action<SKCanvas> render, PagePlacement page)
    {
        using var skStream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, page.Width, page.Height), skStream))
        using (PlotDocumentScope.Enter())
            render(canvas);
        // Skia writes each text run's per-glyph x/y list with a trailing separator, which Firefox
        // reads as invalid and drops - putting every run a line above its baseline, where the
        // clip eats it. Correct in Illustrator, Inkscape, Chrome and Safari; unreadable in
        // Firefox. See SvgFontNormalizer.RepairPositionLists.
        return SvgFontNormalizer.RepairPositionLists(
                   Encoding.UTF8.GetString(skStream.DetachAsData().ToArray()));
    }

    /// <summary>A raster page at <paramref name="scale"/>× the page size, PNG-encoded.</summary>
    public static byte[]? BuildPngBytes(Action<SKCanvas> render, PagePlacement page, float scale)
    {
        int w = (int)(page.Width  * scale);
        int h = (int)(page.Height * scale);
        if (w <= 0 || h <= 0) return null;

        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale, scale);
        using (PlotDocumentScope.Enter()) render(canvas);

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
