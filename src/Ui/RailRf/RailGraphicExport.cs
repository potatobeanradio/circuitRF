// Copy out of railRF (docs/sonnet-briefs/brief-railrf-9-copy.md; railrf.md §11.7).
//
// ══ RAILRF WRITES NO CLIPBOARD CODE ═══════════════════════════════════════════════════════════
//
// R-rail9-1, and it is the whole brief stated as a rule. The owner instruction behind it:
//
//     a copy out of railRF must land as a picture in a document or a slide, it must carry railRF's
//     own rendering and not just the copper, and IT MUST REUSE THE EXPORTER THAT ALREADY EXISTS —
//     this path has cost real debugging time across three platforms and none of it should be spent
//     again.
//
// So there is no P/Invoke here, no format negotiation, no platform branch and no second renderer.
// What this file contains is the four decisions that are railRF's own — which scene, framed how, in
// which colours, with which TEXT beside the picture — and every one of those is a handful of lines.
// StackupGraphicExport and HarmonicaClipboard are the two worked examples of the same shape.
//
// ══ THE FIVE PIECES, IN THE ORDER THEY ARE CALLED ═════════════════════════════════════════════
//
// Each already exists, and each carries a finding that put it there — every one of them a defect
// somebody already shipped once.
//
//  1. FRAME THE PAGE FROM WHAT IS PAINTED — never from the current pan and zoom, never from raw
//     geometry bounds. LayoutClipboard.ComputeSelectionBounds does it, and it is where R-rail9-2
//     lands: railRF's overlay goes into that same pass, so a legend outside the copper's bbox is
//     inside the page. Two older defects live in that method's own comments (a label's stored bbox
//     is a POINT; a hidden layer must not size the page) and this copy inherits both fixes.
//
//  2. RESOLVE COLOUR AND BACKGROUND THROUGH ClipboardRenderPolicy.Resolve() — one app-wide setting,
//     never a per-call parameter. A user who has set "always copy in light mode" has set it for this
//     picture too, which is why the map theme below is built from the RESOLVED variant and not from
//     the overlay's current one (that one follows the window). Brief 8's R-rail8-10 already made the
//     map opaque paint, so nothing here depends on the page's background — which is what keeps this
//     step a one-liner rather than the stackup copy's hole-cutting exercise.
//
//  3. RENDER PDF, SVG AND A 2x PNG AND WRITE THEM ALL AT ONCE — PlotExporter.SetClipboardDataAsync.
//     ONE call and not four, because a second Avalonia clipboard session fails on Windows (see
//     WindowsClipboard's own header). On Windows that call bypasses Avalonia entirely and performs
//     one P/Invoke session covering every format with the enhanced metafile FIRST, which is what a
//     slide or a word-processor document takes when it iterates the formats on offer; on macOS and
//     Linux it is one transfer carrying the native PDF and SVG types, the bitmap and the text, with
//     a text-only fallback if the platform refuses the multi-format write.
//
//  4. EXPORTS OPT OUT OF THE LEVEL-OF-DETAIL TIERS — LayoutClipboard.ExportOptions. A PDF or SVG
//     page has no device pixels to budget against and a pasted bitmap may be rescaled away from the
//     size it was rendered at, so what is STORED is what is drawn, exactly as
//     `circuitrf render --detail full` does. On a real board that is a visible difference, not a
//     theoretical one — and it was not true until this brief's own gate measured it: only
//     DetailPixelThreshold was set there, which leaves five of the seven tiers running at their
//     interactive defaults. 240 stored rects drew 2 elements. src/Ui/RESOLVED.md has the detail.
//
//  5. THE FONT REPAIR IS NOT OPTIONAL — every emitted SVG passes through SvgFontNormalizer inside
//     LayoutClipboard.TryRenderToSvg, and on Windows the embedded faces are registered for the
//     process for the duration of the render. A copy must never be able to fail because of a font.
//
// ══ AND ONE ROUTE FROM AN OVERLAY TO A PAGE, NOT TWO ══════════════════════════════════════════
//
// §11.7's last line: the same render path produces the picture in the `Report ▸` menu and in the
// headless report. That is why Compose/BuildSvg/BuildPdf below are separate from
// CopyToClipboardAsync — brief 10 calls the first three and writes them to a file; nothing about the
// picture is decided in the clipboard method.

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Turns the railRF board panel into a page, and puts that page on the system clipboard beside the
/// document's own state. Renders nothing itself.
/// </summary>
internal static class RailGraphicExport
{
    /// <summary>
    /// Everything the picture is a function of.
    /// </summary>
    /// <remarks>
    /// <b>The VIEW, never a selection</b> (§11.7, and R-rail9-5 is the reason): a copy of a selection
    /// can be a copy of nothing, and a copy of nothing writes nothing, which leaves the previous copy
    /// on the clipboard for the next paste to find. railRF has no selection of its own in any case —
    /// the overlay declines every press.
    /// </remarks>
    /// <param name="Board">The board canvas's own <c>LayoutView</c> — the imported artwork, with
    /// nothing railRF added (R-rail8-2). Null before an import.</param>
    /// <param name="Tech">The stackup, for layer colours and visibility.</param>
    /// <param name="Map">The scene the showing tab draws, or null for the bare artwork.</param>
    /// <param name="Document">What the TEXT flavour carries.</param>
    internal sealed record Request(
        LayoutView? Board,
        Technology? Tech,
        RailMapScene? Map,
        RailDocument Document);

    /// <summary>
    /// The board as a <c>LayoutFragment.Payload</c> — the shape
    /// <see cref="LayoutClipboard"/>'s three renderers take.
    /// </summary>
    /// <remarks>
    /// <b>Rulers travel</b> (R-rail9-4): §9B.9's rule is that a ruler is document content rather than
    /// overlay state, so it comes out in a slide — the same rule <c>circuitrf render</c> follows. A
    /// railRF board carries none today; carrying them anyway is what keeps that true when it does.
    ///
    /// <para>Instances are not copied across because the imported artwork is flat shapes (see
    /// <c>RailRfViewModel.RebuildBoardLayout</c>) — and the payload is never SERIALIZED on this path,
    /// so it is only ever the renderers' input.</para>
    /// </remarks>
    internal static LayoutFragment.Payload PayloadOf(LayoutView? board)
    {
        var payload = new LayoutFragment.Payload
        {
            DbuPerMicron = board?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = board?.DisplayUnit ?? LayoutUnit.Um,
        };
        if (board is null) return payload;

        payload.Shapes.AddRange(board.Shapes);
        payload.Instances.AddRange(board.Instances);
        payload.Rulers.AddRange(board.Rulers);
        return payload;
    }

    /// <summary>
    /// The export context for one request, in the colours
    /// <see cref="ClipboardRenderPolicy"/> resolves.
    /// </summary>
    /// <remarks>
    /// <b>Both themes come from the one variant</b> — the artwork's and the map's. Resolving them
    /// separately is how a copy ends up with a light board under a dark legend, and the whole point of
    /// the policy being app-wide is that a copy looks like one picture.
    /// </remarks>
    internal static LayoutClipboard.ExportContext ContextFor(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (variant, transparent) = ClipboardRenderPolicy.Resolve();

        return LayoutClipboard.MakeExportContext(
            PayloadOf(request.Board),
            request.Tech,
            LayoutRenderTheme.FromTheme(ThemeService.Active, variant),
            transparent,
            baseDir: "",
            railMap: request.Map,
            railTheme: RailMapTheme.FromTheme(ThemeService.Active, variant));
    }

    /// <summary>
    /// The page as SVG. <b>Never null</b> — see <see cref="BlankSvg"/>.
    /// </summary>
    internal static string BuildSvg(LayoutClipboard.ExportContext ctx)
        => LayoutClipboard.TryRenderToSvg(ctx)?.Svg ?? BlankSvg();

    /// <summary>The page as PDF bytes. <b>Never null</b>, for <see cref="BuildSvg"/>'s reason.</summary>
    internal static byte[] BuildPdf(LayoutClipboard.ExportContext ctx)
        => LayoutClipboard.TryRenderToPdf(ctx) ?? BlankPdf();

    /// <summary>
    /// R-rail9-4 — renders the board as drawn and writes PDF, SVG, a bitmap and the document's own
    /// marker-guarded JSON to the system clipboard together, in one call.
    /// </summary>
    /// <remarks>
    /// <b>R-rail9-5: there is no early return.</b> Not on an empty board, not on an unsolved
    /// document, not on a rail with no result:
    ///
    /// <para><i>A copy that writes nothing to the system clipboard leaves the PREVIOUS copy sitting
    /// there, so the next paste produces something unrelated and nothing reports a failure — that is
    /// precisely what a ruler-only layout copy did.</i></para>
    ///
    /// <para>Which is why the two vector builders above substitute a blank page rather than returning
    /// null, and why this method has no guard clause at the top. The JSON is always the document,
    /// whatever state it is in.</para>
    /// </remarks>
    internal static async Task CopyToClipboardAsync(Control anchor, Request request)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(request);

        var ctx = ContextFor(request);

        byte[] pdf  = BuildPdf(ctx);
        var    svg  = LayoutClipboard.TryRenderToSvg(ctx);
        var    bmp  = BuildBitmap(ctx);
        string json = RailClipboard.Serialize(request.Document);

        // The page dimensions travel with the bytes, because the Windows bypass is the only place a
        // receiving application is told how big the picture is. LayoutClipboard owns that arithmetic
        // so the two copies cannot size their pages differently.
        var (pageW, pageH) = svg is { } s
            ? LayoutClipboard.ClipboardPageSize(s.W, s.H)
            : (PlotExporter.PageW, PlotExporter.PageH);

        await PlotExporter.SetClipboardDataAsync(
            anchor, pdf, svg?.Svg ?? BlankSvg(), json, bmp, pageW, pageH);
    }

    /// <summary>
    /// A 2x raster for applications that take a bitmap and nothing richer.
    /// </summary>
    /// <remarks>
    /// Null on failure, and the <c>catch</c> is not sloppiness — it is the rule every graphic copy in
    /// this application follows: <b>a raster failure must never cost the vector formats</b>, which are
    /// the richer two of the three.
    /// </remarks>
    private static Bitmap? BuildBitmap(LayoutClipboard.ExportContext ctx)
    {
        try { return LayoutClipboard.TryRenderToAvaloniaImage(ctx); }
        catch { return null; }
    }

    /// <summary>
    /// An empty page at the exporter's own size — what a board-less window copies.
    /// </summary>
    /// <remarks>
    /// This is the <b>whole</b> mechanism behind "a copy always writes": the three renderers decline a
    /// page they cannot frame (nothing painted, nothing to measure), and a decline has to become a
    /// blank page here rather than a write that does not happen.
    /// </remarks>
    private static string BlankSvg() => PlotExporter.BuildSvgString(static _ => { });

    private static byte[] BlankPdf() => PlotExporter.BuildPdfBytes(static _ => { });
}
