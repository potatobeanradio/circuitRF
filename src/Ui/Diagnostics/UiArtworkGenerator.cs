using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Skia.Helpers;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// Renders LIVE circuitRF user interface — real Avalonia controls, real dialogs, real toolbars and
/// our own Skia canvases — to vector SVG, in light and dark, for the User Documentation.
///
/// <para>Nothing here is a mock-up. <c>Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync</c>
/// draws a laid-out visual tree into any <see cref="SKCanvas"/>, and <see cref="SKSvgCanvas"/> IS an
/// <c>SKCanvas</c> — so the theme chrome, the text, the borders and the clips all come out as
/// vectors. Our own <c>ICustomDrawOperation</c> canvases (schematic, layout, Smith chart, wBond
/// profile) come out as vectors too, because <c>ISkiaSharpApiLease</c> hands the draw op the real
/// canvas, which here is the SVG recorder. A captured window containing a Smith chart is vector end
/// to end.</para>
///
/// <para><b>This type never references Avalonia.Headless.</b> It renders whatever visual it is
/// given under whatever platform is already initialised; <c>tools/DocGen</c> owns the headless
/// bootstrap so the shipping UI assembly does not carry that dependency. The headless platform MUST
/// be configured with <c>UseHeadlessDrawing = false</c> or drawing is stubbed out and every figure
/// comes back empty with no error — see <c>tools/DocGen/HeadlessHost.cs</c>.</para>
///
/// <para>Regenerate every figure with:</para>
/// <code>dotnet run --project tools/DocGen -- --out docs/user</code>
/// </summary>
public static class UiArtworkGenerator
{
    /// <summary>Emitted file stem suffix per variant, matching the docs CSS's .sym-light/.sym-dark pair.</summary>
    public static string FileStem(string id, ColorVariant variant)
        => variant == ColorVariant.Dark ? id + "-dark" : id;

    // ── The render call ───────────────────────────────────────────────────────

    /// <summary>
    /// Host <paramref name="content"/> in a window, lay it out at <paramref name="w"/> x
    /// <paramref name="h"/>, and write it to <paramref name="path"/> as vector SVG.
    /// </summary>
    /// <remarks>
    /// <para><b>Both theme variants must be set, not one.</b> Avalonia chrome follows
    /// <c>Application.RequestedThemeVariant</c>; our own Skia canvases read circuitRF's
    /// <see cref="ColorVariant"/> (see docs/design/color-themes.md). Setting one and not the other
    /// gives a dark dialog containing a light Smith chart.</para>
    /// <para><b>There is no natural-size fallback.</b> A low-level child captured without its parent
    /// has no size of its own, so every catalog row states an explicit capture size and that size is
    /// what the content is measured and arranged at.</para>
    /// </remarks>
    /// <returns>The path written.</returns>
    public static string RenderVisual(Control content, int w, int h, ColorVariant variant, string path,
                                      WindowFrame? chrome = null,
                                      Func<Control, IReadOnlyList<PopupCapture>>? popups = null)
        => RenderScene(new FigureScene(content) { Popups = popups }, w, h, variant, path, chrome);

    /// <summary>The full form: a <see cref="FigureScene"/> (content plus any popups it opens).</summary>
    public static string RenderScene(FigureScene scene, int w, int h, ColorVariant variant, string path,
                                     WindowFrame? chrome = null, bool mustContainPopup = false)
    {
        if (w <= 0 || h <= 0)
            throw new ArgumentOutOfRangeException(nameof(w), $"Capture size must be positive; got {w}x{h}.");

        ApplyVariant(variant);

        var body = chrome is null ? scene.Content : chrome.Wrap(scene.Content, w, h, variant);
        int totalW = w;
        int totalH = chrome is null ? h : h + WindowFrame.TitleBarHeight;

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width  = totalW,
            Height = totalH,
            Content = body,
            // NOT transparent, and not black. Skia's SVG device writes a zero-alpha fill as an
            // OPAQUE one, so ANY transparent window background serialises as a full-canvas slab —
            // and pure black loses its paint attributes entirely (DocsPaintRemap), which renders as
            // an opaque black one. Painting the docs stylesheet's own surface colour makes that
            // unavoidable slab the colour the figure's frame already is, in both variants.
            Background = new SolidColorBrush(WindowFrame.DocsSurface(variant)),
        };

        SuppressContextualAlternates(window);

        var popupRoots = new List<PopupCapture>();
        string raw;
        try
        {
            window.Show();
            Pump();
            window.Measure(new Size(totalW, totalH));
            window.Arrange(new Rect(0, 0, totalW, totalH));
            Pump();

            // Anything that needs the view to have a SIZE before it can be asked for. Zoom-to-fit is
            // the whole reason this exists: a canvas fits its content to its viewport, and before the
            // first arrange there is no viewport, so a fixture that asks for it at construction time
            // is asking a control with zero bounds and gets silently ignored.
            if (scene.AfterLayout is { } settle)
            {
                settle(scene.Content);
                Pump();
                window.Measure(new Size(totalW, totalH));
                window.Arrange(new Rect(0, 0, totalW, totalH));
                Pump();
            }

            if (scene.Popups is { } open)
            {
                popupRoots.AddRange(open(scene.Content) ?? []);
                Pump();
                foreach (var p in popupRoots)
                {
                    if (p.SeparateRoot is Layoutable l)
                    {
                        l.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        l.Arrange(new Rect(l.DesiredSize));
                    }
                }
                Pump();
            }

            // "The menu silently did not render" and "the menu is closed" produce the SAME figure,
            // so a figure that declares a popup proves the popup drew something before it ships.
            if (mustContainPopup)
            {
                if (popupRoots.Count == 0)
                    throw new InvalidOperationException(
                        $"Figure '{Path.GetFileName(path)}' declares a popup, but the fixture opened none. " +
                        "A popup lives in its own top level; if it did not open, the figure is silently " +
                        "just the window.");

                foreach (var p in popupRoots)
                    if (!DrawsAnything(p.Content))
                        throw new InvalidOperationException(
                            $"Figure '{Path.GetFileName(path)}' declares a popup whose visual root drew " +
                            "nothing. The figure would be indistinguishable from one with the popup closed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new SKDynamicMemoryWStream())
            {
            using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, totalW, totalH), stream))
            {
                DrawingContextHelper.RenderAsync(canvas, window);

                // Popups live in their own top-level, so they are NOT in the window's visual tree
                // and a naive capture silently omits them. Compositing them onto the SAME canvas at
                // their own offset keeps the whole figure a single vector document.
                foreach (var p in popupRoots)
                {
                    if (p.SeparateRoot is not { } popupRoot) continue;   // overlay-hosted: already drawn
                    using var picture = Record(popupRoot);
                    canvas.DrawPicture(picture,
                        SKMatrix.CreateTranslation((float)p.X,
                                                   (float)(p.Y + (chrome is null ? 0 : WindowFrame.TitleBarHeight))));
                }
            }

            using var data = stream.DetachAsData();
            raw = System.Text.Encoding.UTF8.GetString(data.ToArray());
            }
        }
        finally
        {
            scene.Dispose();
            // Detach before closing: a control left parented to a closed window cannot be hosted
            // again, and a toolbar is captured four times (plain and indexed, light and dark).
            window.Content = null;
            window.Close();
            Pump();
        }

        if (!SvgLint.HasDrawingElements(raw))
            throw new InvalidOperationException(
                $"Figure '{Path.GetFileName(path)}' rendered to an SVG with no drawing elements. " +
                "An empty capture is a bug, not an empty figure — the usual cause is a headless " +
                "platform configured WITHOUT UseHeadlessDrawing = false, or a control that never " +
                "got a size (every catalog row must state an explicit capture size).");

        string svg = SvgPostPass.Run(raw, Path.GetFileNameWithoutExtension(path), out var report);
        LastReport = report;

        var findings = SvgLint.DroppedPaint(svg);
        if (findings.Count > 0 && !LintDiagnosticMode)
            throw new InvalidOperationException(SvgLint.Explain(Path.GetFileName(path), findings));

        File.WriteAllText(path, Banner(path) + svg + "\n");
        return path;
    }

    /// <summary>
    /// Turn OFF the font's contextual alternates for everything in this capture.
    ///
    /// <para><b>Inter substitutes a case-height hyphen — and case-height parentheses — beside an
    /// UPPERCASE letter, through the <c>calt</c> feature, which is on by default. Those alternate
    /// glyphs have no cmap entry, so Skia's SVG device cannot map them back to a character and
    /// SILENTLY OMITS THEM</b>, leaving the gap they occupied. Measured with a probe string:
    /// <c>"PROBE A-B c-d E - F g - h"</c> came out as <c>"PROBE AB c-d E F g - h"</c> — the two
    /// hyphens next to a capital gone, the two next to a lower-case letter intact.</para>
    ///
    /// <para>It is not a corner case: it hit every framed figure's title bar
    /// (<c>circuitRF - Layout editor</c> rendered as <c>circuitRF Layout editor</c>), the C-V
    /// editor's whole negative-voltage column (<c>-4</c> as <c>4</c>, <c>6.2E-13</c> as
    /// <c>6.2E13</c>) and the EM panel's port coordinates (<c>('1') at (0, …)</c> losing its
    /// parentheses) — a figure that states the wrong number, with nothing to say so.</para>
    ///
    /// <para>The property is inherited, so setting it on the window reaches every control in the
    /// capture, templated ones included. It affects the DOCS RENDER ONLY; the application is
    /// untouched and keeps Inter's typography as designed.</para>
    /// </summary>
    private static void SuppressContextualAlternates(Control root)
        => Avalonia.Controls.Documents.TextElement.SetFontFeatures(
               root, FontFeatureCollection.Parse("-calt"));

    /// <summary>
    /// Set BOTH theme systems for <paramref name="variant"/>. Public because a fixture that renders
    /// through its own path (the symbol generator) needs the same pairing.
    /// </summary>
    public static void ApplyVariant(ColorVariant variant)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = variant == ColorVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeService.CurrentVariant = variant;
    }

    /// <summary>
    /// Development escape hatch: write the figure even when the dropped-paint lint fires, so the
    /// offending file can be opened and the element found. NEVER set during a real generation — the
    /// lint is blocking precisely because a wrong figure does not announce itself.
    /// </summary>
    public static bool LintDiagnosticMode { get; set; }

    /// <summary>The size report from the most recent <see cref="RenderScene"/>, for the run total.</summary>
    public static SvgPostPass.Report LastReport { get; private set; }

    /// <summary>
    /// The banner every generated file carries, naming the one command that regenerates it. An XML
    /// comment cannot contain a double hyphen, so the flag is written in words.
    /// </summary>
    public const string RegenerateCommand = "dotnet run --project tools/DocGen -- --out docs/user";

    internal static string Banner(string path) =>
        "<!-- GENERATED FILE - do not edit. Regenerate every figure and page with:\n"
      + "     " + RegenerateCommand.Replace("--", "––") + "\n"
      + "     (the flags above are ordinary double hyphens; XML comments cannot contain them.)\n"
      + "     Source: src/Ui/Diagnostics/UiArtworkGenerator.cs + tools/DocGen. -->\n";

    /// <summary>
    /// Record <paramref name="visual"/> into a picture so it can be drawn under a transform.
    ///
    /// <para><b><c>DrawingContextHelper.RenderAsync</c> does not honour the canvas transform in
    /// force when it is called</b> — it installs the visual's own. Measured twice: a composited
    /// popup landed at the origin instead of its offset, and a slide figure drew at full size over
    /// the whole page instead of scaled into its box. Recording to an <see cref="SKPicture"/> and
    /// drawing THAT with a matrix is what makes placing and scaling a captured visual possible.</para>
    /// </summary>
    public static SKPicture Record(Visual visual)
    {
        var b = visual.Bounds;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(
            new SKRect(0, 0, (float)Math.Max(1, b.Width), (float)Math.Max(1, b.Height)));
        DrawingContextHelper.RenderAsync(canvas, visual);
        return recorder.EndRecording();
    }

    /// <summary>
    /// True when <paramref name="visual"/> puts at least one drawing element on a canvas. Rendered
    /// to a throwaway SVG rather than inspected structurally, because "does this produce ink" is
    /// exactly the question the serializer answers and a visual-tree walk only guesses at.
    /// </summary>
    private static bool DrawsAnything(Visual visual)
    {
        var bounds = visual.Bounds;
        float w = (float)Math.Max(1, bounds.Width), h = (float)Math.Max(1, bounds.Height);
        using var stream = new SKDynamicMemoryWStream();
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream))
            DrawingContextHelper.RenderAsync(canvas, visual);
        using var data = stream.DetachAsData();
        return SvgLint.HasDrawingElements(System.Text.Encoding.UTF8.GetString(data.ToArray()));
    }

    /// <summary>Run queued dispatcher jobs so templates apply and layout settles.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
    }
}

/// <summary>
/// An opened popup — a menu, a flyout, a drop-down — described so the generator can both PROVE it
/// drew and, when it needs to, composite it.
///
/// <para><b>Two hosting modes, and the difference is invisible in the figure.</b> Avalonia either
/// gives a popup its own top level (a <c>PopupRoot</c> window) or hosts it in the parent's overlay
/// layer, depending on the platform. Overlay-hosted, the popup is ALREADY inside the window's visual
/// tree and compositing it again draws the entire window twice — measured: the context-menu figure
/// came out at 178 KB against the same window's 93 KB, one window stacked on the other. So
/// <see cref="SeparateRoot"/> is non-null only for the own-top-level case.</para>
/// </summary>
/// <param name="Content">
/// The popup's own control. This is what "did the popup actually render?" is asked of — asking it of
/// the root would be trivially true in the overlay case and would prove nothing.
/// </param>
/// <param name="SeparateRoot">The popup's own top level, or null when it is already in the window's tree.</param>
public readonly record struct PopupCapture(Visual Content, Visual? SeparateRoot, double X, double Y);

/// <summary>
/// What a catalog row builds: the control to capture, optionally with popups it opens once the
/// window is laid out, and anything that must be disposed afterwards.
/// </summary>
public sealed class FigureScene(Control content) : IDisposable
{
    public Control Content { get; } = content;

    /// <summary>
    /// Called after the window is shown and arranged. Open the menu/flyout here and return its root
    /// with the offset it should be composited at. Returning nothing is normal.
    /// </summary>
    public Func<Control, IReadOnlyList<PopupCapture>>? Popups { get; init; }

    /// <summary>
    /// Called once the view has been laid out, before any popup is opened, for anything that needs
    /// a real size to answer — <b>Zoom to Fit above all</b>, which is a viewport operation and does
    /// nothing at all when asked of a control that has not been arranged yet. The window is
    /// re-measured and re-arranged afterwards, so the request is reflected in the capture.
    /// </summary>
    public Action<Control>? AfterLayout { get; init; }

    /// <summary>Cleanup for anything the fixture opened (documents, temp directories).</summary>
    public Action? Cleanup { get; init; }

    public void Dispose() => Cleanup?.Invoke();
}
