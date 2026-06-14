// ================================================================
//  WindowsClipboard.cs — Windows-specific clipboard helpers
//
//  On Windows, Avalonia's clipboard.SetDataAsync() calls EmptyClipboard(),
//  which makes Avalonia's window the clipboard owner. That ownership persists
//  after CloseClipboard(). A second OpenClipboard / SetClipboardData call in a
//  separate session would fail because the caller is no longer the owner —
//  Windows requires EmptyClipboard (which transfers ownership) before
//  SetClipboardData can be called.
//
//  The fix: bypass Avalonia on Windows entirely. SetClipboard() performs one
//  complete P/Invoke session — OpenClipboard → EmptyClipboard → SetClipboardData
//  for every format (EMF, PNG, PDF, SVG, text) → CloseClipboard. This also lets
//  us put CF_ENHMETAFILE first in the enumeration order, which benefits apps that
//  iterate formats and take the first one they recognise (Word, PowerPoint).
//
//  Font handling: SkiaSharp's SVG canvas writes font-family names ("IBM Plex Sans")
//  but does not embed font data. Svg.NET resolves family names via GDI+'s
//  installed-font collection. AddFontMemResourceEx registers the embedded TTF bytes
//  for the current process before rendering, then RemoveFontMemResourceEx removes
//  them immediately after, guaranteeing correct text layout even when the font is
//  not installed system-wide.
//
//  All public members are marked [SupportedOSPlatform("windows")] and must only be
//  called inside an OperatingSystem.IsWindows() guard. The file compiles on all
//  platforms; none of this code executes on macOS or Linux.
// ================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Platform;
using Svg;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace CircuitRF.Ui.Clipboard;

internal static class WindowsClipboard
{
    // ---- Win32 P/Invoke ----------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    // On success the clipboard owns the handle — do NOT free it.
    // On failure (returns NULL) the caller still owns the handle.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteEnhMetaFile(IntPtr hemf);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(
        IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    [DllImport("gdi32.dll")]
    private static extern bool RemoveFontMemResourceEx(IntPtr fh);

    private const uint CF_UNICODETEXT = 13;
    private const uint CF_ENHMETAFILE = 14;
    private const uint GMEM_MOVEABLE  = 0x0002;

    // GDI+ and SkiaSharp/HarfBuzz use different text-metric engines, so EMF glyph spacing
    // will never be pixel-identical to the screen. These settings bring GDI+ closer:
    //   TextRenderingHint.AntiAlias  — no grid-fit → fractional glyph advances.
    //   PixelOffsetMode.HighQuality  — sub-pixel geometry offsetting.
    //   CompositingQuality.HighQuality — higher-quality blending.
    private const bool UseHighQualityTextMetrics = true;

    // circuitRF schematic text uses IBM Plex Sans (see Renderers/SkiaFonts.cs). Register every
    // weight the schematic renderer can emit so Svg.NET/GDI+ resolves each family name the SVG
    // references (labels=Regular, net labels=Italic, symbol text=Bold/Italic/Light/Regular).
    // This list must mirror the Plex weights in SkiaFonts.
    private static readonly string[] FontAssetUris =
    [
        "avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Regular.ttf",
        "avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Bold.ttf",
        "avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Italic.ttf",
        "avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Light.ttf",
        "avares://CircuitRF.Ui/Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-SemiBold.ttf",
    ];

    /// <summary>
    /// Writes all clipboard formats (CF_ENHMETAFILE, PNG, PDF, SVG, plain text) in a single
    /// P/Invoke session, bypassing Avalonia. Formats are set in descending richness so apps that
    /// iterate EnumClipboardFormats and take the first recognised format get the best one.
    /// <paramref name="pdf"/> and <paramref name="svg"/> are best-effort; null entries are skipped
    /// (no EMF is built when svg is null). Must only be called on Windows.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void SetClipboard(
        IntPtr          hwnd,
        byte[]?         pdf,
        string?         svg,
        string          json,
        AvaloniaBitmap? bitmap,
        float           pageW,
        float           pageH)
    {
        // Build EMF before opening the clipboard — font registration and SVG parsing take
        // non-trivial time and must not hold the clipboard lock.
        IntPtr hEmf = IntPtr.Zero;
        if (!string.IsNullOrEmpty(svg))
        {
            IntPtr[] fontHandles = RegisterFonts();
            try   { hEmf = SvgToEmfHandle(svg!, pageW, pageH); }
            finally { UnregisterFonts(fontHandles); }
        }

        if (!OpenClipboard(hwnd))
        {
            if (hEmf != IntPtr.Zero) DeleteEnhMetaFile(hEmf);
            return;
        }

        try
        {
            EmptyClipboard();   // transfers clipboard ownership to hwnd

            // ---- Set formats in descending richness ----------------
            // CF_ENHMETAFILE first: highest quality for Word / PowerPoint.
            if (hEmf != IntPtr.Zero)
            {
                IntPtr r = SetClipboardData(CF_ENHMETAFILE, hEmf);
                if (r == IntPtr.Zero) DeleteEnhMetaFile(hEmf);  // refused — we still own it
                // On success the clipboard owns the handle — do not delete it.
            }

            // PNG raster — widely accepted by Office, image editors, browsers.
            if (bitmap is not null)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    bitmap.Save(ms);
                    SetBytesOnClipboard(RegisterClipboardFormat("PNG"), ms.ToArray());
                }
                catch { }
            }

            // PDF — recognised by Acrobat, some Office versions, PDF viewers.
            if (pdf is not null)
                SetBytesOnClipboard(RegisterClipboardFormat("application/pdf"), pdf);

            // SVG — recognised by Inkscape, some modern browsers.
            if (!string.IsNullOrEmpty(svg))
                SetBytesOnClipboard(RegisterClipboardFormat("image/svg+xml"),
                    Encoding.UTF8.GetBytes(svg!));

            // Plain-text JSON last — Paste reads this; visible to any text-only receiver.
            SetUnicodeTextOnClipboard(json);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetBytesOnClipboard(uint format, byte[] data)
    {
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return;

        IntPtr ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero) { GlobalFree(hMem); return; }

        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(hMem);

        IntPtr result = SetClipboardData(format, hMem);
        if (result == IntPtr.Zero) GlobalFree(hMem);   // refused — we still own the block
    }

    [SupportedOSPlatform("windows")]
    private static void SetUnicodeTextOnClipboard(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');   // CF_UNICODETEXT: null-terminated UTF-16
        SetBytesOnClipboard(CF_UNICODETEXT, bytes);
    }

    /// <summary>
    /// Loads each TTF from the embedded Avalonia assets and registers it with GDI+ via
    /// AddFontMemResourceEx so Svg.NET's font resolver finds the family even when not installed.
    /// Returns handles to pass to <see cref="UnregisterFonts"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr[] RegisterFonts()
    {
        var handles = new IntPtr[FontAssetUris.Length];
        for (int i = 0; i < FontAssetUris.Length; i++)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri(FontAssetUris[i]));
                var bytes = new byte[stream.Length];
                _ = stream.Read(bytes, 0, bytes.Length);

                var gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);  // pin for the P/Invoke
                uint numFonts = 0;
                IntPtr h = AddFontMemResourceEx(
                    gcHandle.AddrOfPinnedObject(), (uint)bytes.Length, IntPtr.Zero, ref numFonts);
                gcHandle.Free();   // safe to unpin once AddFontMemResourceEx returns
                handles[i] = h;
            }
            catch { }
        }
        return handles;
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterFonts(IntPtr[] handles)
    {
        foreach (var h in handles)
            if (h != IntPtr.Zero) RemoveFontMemResourceEx(h);
    }

    /// <summary>
    /// Renders <paramref name="svg"/> into an in-memory GDI+ Enhanced Metafile and returns the raw
    /// EMF handle, or IntPtr.Zero on failure. The caller must either pass the handle to
    /// SetClipboardData (which takes ownership) or call DeleteEnhMetaFile to free it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr SvgToEmfHandle(string svg, float pageW, float pageH)
    {
        IntPtr refDc = GetDC(IntPtr.Zero);   // screen DC initialises the Metafile recording context
        if (refDc == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            SvgDocument svgDoc;
            try { svgDoc = SvgDocument.FromSvg<SvgDocument>(svg); }
            catch { return IntPtr.Zero; }

            // Frame in points (1 unit = 1/72 inch) so receiving apps get sane physical dimensions.
            var frame    = new RectangleF(0f, 0f, pageW, pageH);
            var metafile = new Metafile(refDc, frame, MetafileFrameUnit.Point, EmfType.EmfPlusDual);

            using (var g = Graphics.FromImage(metafile))
            {
                g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                if (UseHighQualityTextMetrics)
                {
                    g.PixelOffsetMode    = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                }

                // Scale SVG natural dimensions → page frame so the content fills it exactly.
                float svgW = svgDoc.Width.Value  > 0f ? svgDoc.Width.Value  : pageW;
                float svgH = svgDoc.Height.Value > 0f ? svgDoc.Height.Value : pageH;
                g.ScaleTransform(pageW / svgW, pageH / svgH);

                svgDoc.Draw(g);
            }

            IntPtr hEmf = metafile.GetHenhmetafile();   // transfers GDI handle ownership to caller
            metafile.Dispose();
            return hEmf;
        }
        catch { return IntPtr.Zero; }
        finally { ReleaseDC(IntPtr.Zero, refDc); }
    }
}
