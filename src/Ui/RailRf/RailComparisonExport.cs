// The A/B report as a page — through brief 9's ONE render function and no other
// (docs/sonnet-briefs/brief-railrf-16-ab-comparison.md R-rail16-6; railrf.md §2.5, §11.7).
//
// ══ THIS FILE COMPOSES NO PAGE ════════════════════════════════════════════════════════════════
//
// §11.7's last line: "there is one route from an overlay to a page and not two." That route is
// `RailReportPage` in CircuitRF.Render, below the firewall, which `circuitrf rail -o report.svg`
// already draws its page with. The A/B report is a different SET OF SECTIONS, not a different page:
// same title, same provenance banner, same picture of the board, same two-column text block, same
// seven level-of-detail tiers turned off.
//
// So what is here is a MAPPING and a size, and deliberately nothing else. Every heading and every
// line comes out of `RailComparisonReport.Sections` in src/Design — which is where the wording
// belongs, because a window panel showing the comparison has to read the same list the page does —
// and every pixel comes out of `RailReportPage.Draw`. A second composition that drew a title and some text beside a board
// would agree with the first one today and drift the first time either was touched, with the drift
// invisible because both produce a plausible page. That is the failure §11.7 exists to forbid.
//
// ── WHY THE MAPPING EXISTS AT ALL ─────────────────────────────────────────────────────────────
//
// `RailComparisonSection` and `RailReportSection` are the same shape and are two types because
// src/Design sits BELOW CircuitRF.Render and cannot name the second one. The alternative — putting
// the comparison's wording in CircuitRF.Render so it could build `RailReportSection` directly —
// would move a page's worth of domain sentences into the drawing project, which is the boundary
// `src/Render`'s own `.csproj` states: it draws, it does not decide what a finding says.

using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Turns a <see cref="RailComparisonReport"/> into the page
/// <see cref="RailReportPage"/> draws. Renders nothing itself.
/// </summary>
internal static class RailComparisonExport
{
    /// <summary>The page size, in points — <c>circuitrf rail</c>'s own, so the A/B report and the
    /// single-design report are the same page.</summary>
    internal const int PageW = 1600;

    /// <inheritdoc cref="PageW"/>
    internal const int PageH = 1200;

    /// <summary>
    /// The page request for one comparison.
    /// </summary>
    /// <remarks>
    /// <b>The picture is the JUDGED design's board</b>, not the reference's and not both. §2.5's
    /// question is "the reference passes; does yours?", so the board a reader is looking at while
    /// they read the findings is the one they are about to change. The reference is named in the
    /// provenance banner, which is where R-rail10-5 puts everything a reader has to know before
    /// reading the numbers.
    /// </remarks>
    /// <param name="report">What <see cref="RailComparisonReport.Build"/> produced.</param>
    /// <param name="board">The judged design's artwork, or null where there is none.</param>
    /// <param name="tech">Its stackup.</param>
    /// <param name="map">The scene the board panel is showing, or null for the bare artwork.</param>
    /// <param name="provenance">R-rail10-5's lines, from the run that produced the results.</param>
    /// <param name="notFitted">The JUDGED design's unmounted parts, marked on its board. It is the
    /// judged side for the same reason <paramref name="board"/> is, and it belongs on a comparison
    /// page more than on any other: a depopulated part is very often the difference the two sides
    /// are being compared over.</param>
    internal static RailReportPageRequest PageOf(
        RailComparisonReport report,
        LayoutView? board,
        Technology? tech,
        RailMapScene? map,
        IReadOnlyList<string>? provenance = null,
        IReadOnlyList<RailPartHighlight>? notFitted = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        var variant = ClipboardRenderPolicy.Resolve().Variant;

        return new RailReportPageRequest
        {
            Title      = $"{report.Match.TargetName} against {report.Match.ReferenceName}",
            Provenance = Banner(report, provenance),
            Sections   = [.. report.Sections.Select(s => new RailReportSection(s.Heading, s.Lines))],
            Board      = board,
            Technology = tech,
            Map        = map,
            NotFitted  = notFitted ?? [],
            Theme      = ThemeService.Active,
            Variant    = variant,
            BaseDir    = "",
        };
    }

    /// <summary>The page as SVG.</summary>
    internal static string BuildSvg(RailReportPageRequest request) =>
        PlotDocumentWriter.BuildSvgString(
            canvas => RailReportPage.Draw(canvas, request, PageW, PageH),
            new PagePlacement(PageW, PageH, 0f));

    /// <summary>The page as PDF bytes.</summary>
    internal static byte[] BuildPdf(RailReportPageRequest request) =>
        PlotDocumentWriter.BuildPdfBytes(
            canvas => RailReportPage.Draw(canvas, request, PageW, PageH),
            new PagePlacement(PageW, PageH, 0f));

    /// <summary>
    /// The banner under the title.
    /// </summary>
    /// <remarks>
    /// <b>Which design is which is the first thing on it</b>, because every signed number on the
    /// page — every Δ|Z|, every millivolt, every "worse" — is signed relative to that choice, and a
    /// reader six months later has no status strip to ask. R-rail16-8's refusal never reaches here,
    /// but the extent both sides ran at does, for the same reason.
    /// </remarks>
    private static IReadOnlyList<string> Banner(
        RailComparisonReport report, IReadOnlyList<string>? provenance)
    {
        var lines = new List<string>
        {
            $"Reference: {report.Match.ReferenceName} · judged: {report.Match.TargetName} · " +
            $"rail '{report.Match.Reference?.Name ?? "(none)"}'. A positive Δ means the judged " +
            "design is the higher impedance.",
        };

        if (report.Match.Unmatched.Count > 0)
            lines.Add($"{report.Match.Unmatched.Count} item(s) could not be matched and are listed " +
                      "at the foot of this report — nothing on this page accounts for them.");

        if (provenance is { Count: > 0 }) lines.AddRange(provenance);

        return lines;
    }
}
