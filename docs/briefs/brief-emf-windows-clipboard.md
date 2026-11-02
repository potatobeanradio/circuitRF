# Brief: enable EMF (CF_ENHMETAFILE) clipboard export — Windows only

Port splotRF's proven Windows clipboard path into circuitRF so Copy puts a **vector EMF** on the Windows
clipboard for Word/PowerPoint, in addition to PDF/PNG/SVG/text. macOS and Linux are untouched (they keep
the existing Avalonia `DataTransfer` path). Reuse splotRF's implementation as-is, including the **clipboard
format ordering** (EMF → PNG → application/pdf → image/svg+xml → text).

Two circuitRF-specific adaptations (everything else is verbatim from splotRF):
1. **Fonts.** splotRF embeds DejaVu Sans; circuitRF's schematic renderer draws with **IBM Plex Sans**
   (labels = `PlexRegular`, net labels = `PlexItalic`, symbol text = `PlexBold/PlexItalic/PlexLight/PlexRegular`).
   The EMF font registration must point at circuitRF's Plex TTFs, or GDI+/Svg.NET substitutes the wrong
   font in the metafile and text renders incorrectly.
2. **Page size.** splotRF uses a fixed letter page (792×612 pt). circuitRF sizes the SVG per-selection, so
   the EMF frame is derived from the SVG's own dimensions (scaled so the longest side is a sane on-paste
   size; vector stays crisp).

Size: **M**. Files: `CircuitRF.Ui.csproj`, new `src/Ui/Clipboard/WindowsClipboard.cs`,
`src/Ui/Clipboard/SchematicClipboard.cs`, `src/Ui/Views/Content/SchematicView.axaml.cs`.

---

## 1. `CircuitRF.Ui.csproj` — add the Svg package

In the SkiaSharp `ItemGroup`, add:
```xml
    <!-- SVG→EMF conversion for the Windows clipboard (CF_ENHMETAFILE for Word/PowerPoint).
         Unconditional so it compiles on all platforms; every execution path is guarded by
         OperatingSystem.IsWindows() at runtime. Transitively brings System.Drawing.Common
         (used by WindowsClipboard.SvgToEmfHandle), so System.Drawing.Imaging compiles
         cross-platform. -->
    <PackageReference Include="Svg" Version="3.4.7" />
```

**`TreatWarningsAsErrors=true` caveat:** our own GDI+ usage is kept warning-clean by the
`[SupportedOSPlatform("windows")]` attributes in `WindowsClipboard.cs` (satisfies CA1416). If the Svg /
System.Drawing.Common transitive reference surfaces a *NuGet* warning under the strict setting, add a
**targeted** `<NoWarn>` for that one code — do **not** weaken the global `TreatWarningsAsErrors`. (splotRF
builds clean with this exact package version on net10.0.)

---

## 2. New file `src/Ui/Clipboard/WindowsClipboard.cs`

Ported verbatim from splotRF except: file-scoped namespace `CircuitRF.Ui.Clipboard`; `FontAssetUris`
points at the IBM Plex TTFs; `pdf`/`svg` are nullable-tolerant (circuitRF's rich formats are best-effort).
Keep the comments — they explain *why* the single-session bypass and the font registration are required.

```csharp
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
```

---

## 3. `src/Ui/Clipboard/SchematicClipboard.cs`

### 3a. Update the class doc comment
Replace the "On Windows, CF_ENHMETAFILE (EMF) is omitted here…" sentence with:
```csharp
/// On Windows, all formats — including CF_ENHMETAFILE (vector EMF for Word/PowerPoint) — are written
/// in one P/Invoke session via WindowsClipboard, bypassing Avalonia. macOS/Linux use Avalonia's
/// DataTransfer (PDF/SVG native UTIs + PNG + text).
```

### 3b. `TryRenderToSvg` — also return the SVG's pixel dimensions
Change the return type and the final return; everything else stays:
```csharp
    private static (string Svg, float W, float H)? TryRenderToSvg(
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        SchematicRenderTheme                theme,
        bool                                useTransparentBackground,
        bool                                excludeGrid,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null)
    {
        try
        {
            // … unchanged body through the using(canvas) block …
            return (Encoding.UTF8.GetString(stream.DetachAsData().ToArray()), pxW, pxH);
        }
        catch { return null; }
    }
```

### 3c. `CopyAsync` — add the owner hwnd, branch Windows vs. the rest
Add `IntPtr ownerHwnd = default` as the last parameter, and replace the body from the `var item = …`
section onward:
```csharp
    public static async Task CopyAsync(
        IClipboard clipboard,
        IReadOnlyList<EditableComponent>    components,
        IReadOnlyList<EditableWire>         wires,
        IReadOnlyList<EditableCanvasObject> canvasObjects,
        double gridSize = 100.0,
        IReadOnlyList<EditableNetLabel>?    netLabels = null,
        string?                             schematicDirectory = null,
        IntPtr                              ownerHwnd = default)
    {
        if (components.Count == 0 && wires.Count == 0 && canvasObjects.Count == 0) return;

        string json = SchematicPersistence.SerializeSelection(components, wires, canvasObjects, gridSize);

        var (variant, transparent) = ClipboardRenderPolicy.Resolve();
        var renderTheme = SchematicRenderTheme.FromTheme(ThemeService.Active, variant);
        const bool excludeGrid = true;

        // Rich formats are best-effort; JSON text is always present as the fallback.
        byte[]?                          pdf = null;
        (string Svg, float W, float H)?  svg = null;
        Bitmap?                          bmp = null;
        try
        {
            pdf = TryRenderToPdf(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            svg = TryRenderToSvg(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
            bmp = TryRenderToAvaloniaImage(components, wires, canvasObjects, renderTheme, transparent, excludeGrid, netLabels, schematicDirectory);
        }
        catch { /* best-effort */ }

        // ── Windows: bypass Avalonia, write all formats (incl. CF_ENHMETAFILE) in ONE P/Invoke
        //    session. Avalonia's SetDataAsync calls EmptyClipboard and keeps clipboard ownership,
        //    so a second session to add EMF would fail. See WindowsClipboard.cs for the full why. ──
        if (OperatingSystem.IsWindows())
        {
            // circuitRF sizes the SVG per-selection (no fixed page). The EMF frame matches the SVG's
            // own dimensions, scaled so the longest side is a sane on-paste size — vector stays crisp.
            float pageW = 0f, pageH = 0f;
            if (svg is { } s)
            {
                const float maxSide = 720f;   // ≈10in at 72pt/in — Word/PowerPoint-friendly default
                float scale = MathF.Min(1f, maxSide / MathF.Max(s.W, s.H));
                pageW = s.W * scale;
                pageH = s.H * scale;
            }
            WindowsClipboard.SetClipboard(ownerHwnd, pdf, svg?.Svg, json, bmp, pageW, pageH);
            return;
        }

        // ── macOS / Linux: Avalonia cross-platform clipboard (native PDF/SVG UTIs + PNG + text). ──
        var item = new DataTransferItem();
        if (pdf is not null)
            item.Set(ClipboardFormats.PdfNativeMacFormat, pdf);
        if (svg is { } sv)
            item.Set(ClipboardFormats.SvgNativeFormat, Encoding.UTF8.GetBytes(sv.Svg));
        if (bmp is not null)
            item.Set(DataFormat.Bitmap, bmp);
        item.Set(DataFormat.Text, json);

        var transfer = new DataTransfer();
        transfer.Add(item);
        try { await clipboard.SetDataAsync(transfer); }
        catch { await clipboard.SetTextAsync(json); }
    }
```
`ClipboardFormats.PdfNativeWinFormat` is no longer used here (Windows now registers `"application/pdf"`
inside `WindowsClipboard`); leave the field defined (it's `internal static readonly` — no unused-warning,
and `SymbolClipboard` may reference it).

---

## 4. `src/Ui/Views/Content/SchematicView.axaml.cs` — pass the real window handle

In `CopySelectionToClipboardAsync`, supply the owner hwnd (the view is a `UserControl`, so `this` resolves
the `TopLevel`). Replace the existing call:
```csharp
        var netLabels = model.NetLabels
            .Where(n => n.IsAnchored && wholeWireIds.Contains(n.OwnerWireId))
            .ToList();
        IntPtr ownerHwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await SchematicClipboard.CopyAsync(clipboard, comps, wires, objs, model.GridSize,
                                           netLabels, model.SchematicDirectory, ownerHwnd);
        if (cut) vm.DeleteSelection();
```
(`System` is already imported for `IntPtr`; `Avalonia.Controls` for `TopLevel`.)

**Other call sites:** `ownerHwnd` defaults to `IntPtr.Zero`, and `OpenClipboard(IntPtr.Zero)` is valid
(associates the clipboard with the current task), so any other caller of `CopyAsync` compiles unchanged
and still gets EMF — passing the real handle is just an ownership nicety. No need to hunt them down.

---

## Verification

**On Windows (the target):**
1. Select a schematic with components, wires, cell instances, and net labels → Copy → paste into
   **Word** and **PowerPoint**: a crisp **vector** image appears (zoom in — no pixelation), with correct
   **IBM Plex** text, correct cell-instance symbols, and net labels (i.e. the EMF inherits the
   net-label/cell-symbol export fixes).
2. Paste into an image editor → PNG raster appears. Paste into a PDF viewer/Acrobat → PDF. Paste into a
   text field → the JSON. Paste back into circuitRF → round-trips via JSON as before.
3. Copy twice in a row (two separate copies) → the second still works (single-session bypass; no
   "clipboard owned by previous session" failure).

**On macOS / Linux (must be unchanged):**
4. Copy → paste into Keynote/Pages/Preview (PDF UTI), Illustrator/Inkscape (SVG UTI), and Word (PNG):
   identical to today. No EMF, no `System.Drawing` calls executed.
5. `dotnet build` on macOS succeeds with `TreatWarningsAsErrors` (the `[SupportedOSPlatform("windows")]`
   attributes keep CA1416 quiet; the Svg package restores cleanly).

## Acceptance

- Windows Copy places CF_ENHMETAFILE on the clipboard in the splotRF order
  (EMF → PNG → application/pdf → image/svg+xml → text), in one P/Invoke session.
- EMF text uses the schematic's IBM Plex fonts; cell symbols and net labels render correctly (same render
  path as PDF/SVG).
- macOS/Linux clipboard behaviour and the build are unchanged; all Windows-only code is guarded and
  attributed.

## Notes

- The EMF frame is derived from the per-selection SVG size (scaled to ≤720 pt longest side). This is the
  only intentional deviation from splotRF's fixed-page `SvgToEmfHandle` call — required because circuitRF
  has no fixed page. The `WindowsClipboard.cs` core (P/Invoke, format ordering, font registration, SVG→EMF
  conversion) is otherwise a verbatim port.
- `FontAssetUris` must stay in sync with the Plex weights in `Renderers/SkiaFonts.cs`. If the schematic
  renderer starts using another weight/family, add its TTF here or that text will font-substitute in EMF.
