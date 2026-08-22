using System;
using System.Linq;
using Avalonia.Media;
using CircuitRF.Ui.Diagnostics;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The mechanism behind the docs factory's blocking lint, pinned so it cannot quietly stop working.
///
/// <para>The defect it guards is specific and was measured, not inferred: Skia's SVG device omits
/// <c>fill</c> when the paint colour is pure black and drops <c>fill-opacity</c> with it, so a
/// 20 %-opaque black brush — which is exactly Fluent's light-theme <c>ButtonBackground</c> —
/// serialises as a bare shape and renders as an OPAQUE BLACK slab.</para>
/// </summary>
public class SvgPaintAndPostPassTests
{
    // ── The lint ──────────────────────────────────────────────────────────────

    [Fact]
    public void AShapeWithNeitherFillNorStrokeIsFlagged()
    {
        const string svg = """<svg><path d="M0 0L10 10Z"/></svg>""";
        var findings = SvgLint.DroppedPaint(svg);
        Assert.Single(findings);
        Assert.Equal("path", findings[0].Element);
    }

    [Fact]
    public void AShapeThatCarriesAPaintIsNotFlagged()
    {
        const string svg = """<svg><path fill="#010101" fill-opacity="0.2" d="M0 0L10 10Z"/></svg>""";
        Assert.Empty(SvgLint.DroppedPaint(svg));
    }

    [Fact]
    public void AStrokeOnlyShapeIsNotFlagged()
    {
        // Measured: Skia DOES emit stroke="black" stroke-opacity="0.6" for a #99000000 pen. Strokes
        // are not affected by the defect, and flagging them would be a false positive on every border.
        const string svg = """<svg><path fill="none" stroke="black" stroke-opacity="0.6" d="M0 0L1 1"/></svg>""";
        Assert.Empty(SvgLint.DroppedPaint(svg));
    }

    [Fact]
    public void GeometryInsideAClipPathIsNotInkAndIsNotFlagged()
    {
        // Avalonia emits one clip per control, so without this exclusion the lint would be nothing
        // but false positives — a 320x200 four-control panel produced thirteen clip rectangles.
        const string svg = """
            <svg><clipPath id="c"><rect width="10" height="10"/></clipPath>
            <g clip-path="url(#c)"><rect fill="red" width="4" height="4"/></g></svg>
            """;
        Assert.Empty(SvgLint.DroppedPaint(svg));
    }

    [Fact]
    public void AShapeInheritingAPaintFromItsGroupIsNotFlagged()
    {
        const string svg = """<svg><g fill="#123456"><rect width="4" height="4"/></g></svg>""";
        Assert.Empty(SvgLint.DroppedPaint(svg));
    }

    [Fact]
    public void ClosingAGroupPopsItsInheritedPaint()
    {
        const string svg = """<svg><g fill="#123456"><rect width="4" height="4"/></g><rect width="4" height="4"/></svg>""";
        Assert.Single(SvgLint.DroppedPaint(svg));
    }

    [Fact]
    public void AnSvgWithNoDrawingElementsIsRecognisedAsEmpty()
    {
        Assert.False(SvgLint.HasDrawingElements("""<svg xmlns="http://www.w3.org/2000/svg"></svg>"""));
        Assert.True(SvgLint.HasDrawingElements("""<svg><rect width="1" height="1"/></svg>"""));
    }

    [Fact]
    public void TheFailureMessageNamesTheFileAndEveryElement()
    {
        var findings = SvgLint.DroppedPaint("""<svg><path d="M0 0L1 1Z"/><rect width="2" height="2"/></svg>""");
        var message = SvgLint.Explain("em-setup-editor.svg", findings);
        Assert.Contains("em-setup-editor.svg", message, StringComparison.Ordinal);
        Assert.Contains("<path", message, StringComparison.Ordinal);
        Assert.Contains("<rect", message, StringComparison.Ordinal);
    }

    // ── The remap ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0x33, 0, 0, 0, true)]      // Fluent's light-theme ButtonBackground: the reported case
    [InlineData(0x00, 0, 0, 0, true)]      // Brushes.Transparent is ALSO pure black
    [InlineData(0xFF, 0, 0, 0, true)]
    [InlineData(0x33, 1, 1, 1, false)]     // already one bit off black
    [InlineData(0xFF, 0xFF, 0xFF, 0xFF, false)]
    public void PureBlackIsRecognisedWhateverItsAlpha(byte a, byte r, byte g, byte b, bool expected)
        => Assert.Equal(expected, DocsPaintRemap.IsPureBlack(Color.FromArgb(a, r, g, b)));

    [Fact]
    public void TheNudgePreservesAlphaAndMovesOnlyOneBit()
    {
        var nudged = DocsPaintRemap.Nudge(Color.FromArgb(0x33, 0, 0, 0));
        Assert.Equal(0x33, nudged.A);
        Assert.Equal(DocsPaintRemap.OffBlack, nudged.R);
        Assert.Equal(DocsPaintRemap.OffBlack, nudged.G);
        Assert.Equal(DocsPaintRemap.OffBlack, nudged.B);
        Assert.Equal(1, nudged.R);   // "visually identical" has to mean one, not a compromise value
    }

    // ── Font attributes (the caption-renders-Regular bug) ─────────────────────

    [Theory]
    // The measured case: a SemiBold face written by Skia as a full name plus a weight that is not
    // the face's. Left alone, the browser skips the undeclared first name and CSS weight matching
    // for 500 descends to 400 — so a SemiBold caption ships looking Regular.
    [InlineData("IBM Plex Sans SemiBold, IBM Plex Sans", "IBM Plex Sans", 600, null)]
    [InlineData("Inter SemiBold, Inter",                 "Inter",         600, null)]
    [InlineData("Inter Light, Inter",                    "Inter",         300, null)]
    [InlineData("IBM Plex Sans Bold, IBM Plex Sans",     "IBM Plex Sans", 700, null)]
    // Italic carries no weight word, so there is no weight to restate — Skia's own stays.
    [InlineData("IBM Plex Sans Italic, IBM Plex Sans",   "IBM Plex Sans", null, "italic")]
    [InlineData("IBM Plex Sans SemiBold Italic, IBM Plex Sans", "IBM Plex Sans", 600, "italic")]
    // Skia already got these right; nothing to restate.
    [InlineData("Inter",                                 "Inter",         null, null)]
    [InlineData("IBM Plex Sans",                         "IBM Plex Sans", null, null)]
    public void SkiaFontFamilyListsResolveToAShippedFamilyAndTheRealWeight(
        string emitted, string family, int? weight, string? style)
    {
        var (f, w, st, sub) = SvgFontNormalizer.Resolve(emitted);
        Assert.Equal(family, f);
        Assert.Equal(weight, w);
        Assert.Equal(style, st);
        Assert.Null(sub);
    }

    [Fact]
    public void APlatformFontSkiaSubstitutedIsRedirectedToAShippedOneAndReported()
    {
        // Measured: the Layout and wBond status bars draw U+25BE, which neither UI font covers, so
        // Skia baked in macOS's Lucida Grande — unreproducible on another OS and not shipped.
        var (family, _, _, substituted) = SvgFontNormalizer.Resolve("Lucida Grande");
        Assert.Equal(SvgFontNormalizer.GlyphFallbackFamily, family);
        Assert.Equal("Lucida Grande", substituted);
    }

    [Fact]
    public void AFaceNameWordWeCannotWeighIsAGenerationErrorRatherThanAGuess()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SvgFontNormalizer.Resolve("Inter Wobbly, Inter"));
        Assert.Contains("Wobbly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisingRewritesTheAttributesAndLeavesTheTextAlone()
    {
        const string svg = """
            <svg><text fill="#6A8EF6" font-size="15" font-weight="500" font-family="IBM Plex Sans SemiBold, IBM Plex Sans" x="0, " y="12, ">R</text></svg>
            """;

        var result = SvgFontNormalizer.Normalize(svg, out var subs);

        Assert.Empty(subs);
        Assert.Contains("font-family=\"IBM Plex Sans\"", result, StringComparison.Ordinal);
        Assert.Contains("font-weight=\"600\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("SemiBold", result, StringComparison.Ordinal);
        Assert.Contains(">R</text>", result, StringComparison.Ordinal);

        // The per-glyph positions are KEPT, but without Skia's trailing separator — that comma is
        // what made every figure's text unreadable in Firefox (see TrailingSeparator... below).
        Assert.Contains("x=\"0\"", result, StringComparison.Ordinal);
        Assert.Contains("y=\"12\"", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Owner-reported, 2026-08-21: figure text "missing or else really small" in Firefox on Ubuntu,
    /// while macOS and Windows were perfect.</b> Skia writes a per-glyph position list with a
    /// separator after the last entry, and an SVG <c>list-of-coordinates</c> may not end in one.
    /// Gecko applies SVG's strict error handling and drops the whole attribute, so <c>x</c> and
    /// <c>y</c> both fall back to 0 and every run is drawn at the element origin — one line above its
    /// baseline — where the control's own clip removes all but a sliver of each glyph.
    ///
    /// <para>Proven by A/B render in a Linux Firefox 140 ESR container: the same figure on the same
    /// page renders correctly the moment the trailing commas are stripped from the DOM, and
    /// <c>getBBox().y</c> on the first run of <c>analyses-setup.svg</c> moves from -12.00 to +0.11.
    /// Blink and WebKit accept the trailing comma, which is the whole reason this read as a Linux
    /// problem: Edge and Safari are the defaults there, Firefox is the default on Ubuntu.</para>
    /// </summary>
    [Theory]
    [InlineData("x=\"0, 8.11, 15.52, \"", "x=\"0, 8.11, 15.52\"")]
    [InlineData("y=\"12.11, \"",          "y=\"12.11\"")]
    [InlineData("x=\"0,8.11,\"",          "x=\"0,8.11\"")]
    [InlineData("y=\"9.69,   \"",         "y=\"9.69\"")]
    public void TrailingSeparatorsAreStrippedFromPerGlyphPositionLists(string given, string expected)
    {
        string svg = $"""<svg><text font-family="Inter" {given}>Setup</text></svg>""";

        var result = SvgFontNormalizer.Normalize(svg, out _);

        Assert.Contains(expected, result, StringComparison.Ordinal);
        Assert.DoesNotContain(", \"", result, StringComparison.Ordinal);
        Assert.Contains(">Setup</text>", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// An EMPTY list is as invalid as a trailing separator and means exactly what having no attribute
    /// means, so it is removed rather than written back empty. Skia emits these for a run with no
    /// glyphs; <c>analyses-setup.svg</c> as shipped has one.
    /// </summary>
    [Fact]
    public void AnEmptyPositionListIsRemovedRatherThanLeftInvalid()
    {
        const string svg = """<svg><text font-family="Inter" x="" y="">x</text></svg>""";

        var result = SvgFontNormalizer.Normalize(svg, out _);

        Assert.DoesNotContain("x=\"\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("y=\"\"", result, StringComparison.Ordinal);
        Assert.Contains(">x</text>", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The position lists are fixed on EVERY run, including one with no <c>font-family</c> for the
    /// normaliser to rewrite — that early return used to skip such a run entirely, and the trailing
    /// comma is Skia's, not the font's.
    /// </summary>
    [Fact]
    public void ARunWithNoFontFamilyStillGetsItsPositionListsFixed()
    {
        const string svg = """<svg><text x="0, 7.1, " y="11.6, ">Hi</text></svg>""";

        var result = SvgFontNormalizer.Normalize(svg, out _);

        Assert.Contains("x=\"0, 7.1\"", result, StringComparison.Ordinal);
        Assert.Contains("y=\"11.6\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AWeightSkiaAlreadyGotRightIsLeftAlone()
    {
        // An earlier version of the normaliser REMOVED the weight whenever it had none to restate,
        // silently un-bolding 238 correctly-weighted runs. Removal is never right here.
        const string svg = """<svg><text font-weight="600" font-family="Inter">Hi</text></svg>""";
        var result = SvgFontNormalizer.Normalize(svg, out _);
        Assert.Contains("font-weight=\"600\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePostPassAppliesTheFontNormalisation()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <text font-weight="500" font-family="IBM Plex Sans SemiBold, IBM Plex Sans">R</text></svg>
            """;
        var result = SvgPostPass.Run(svg, "fixture", out var report);
        Assert.Contains("font-weight=\"600\"", result, StringComparison.Ordinal);
        Assert.Empty(report.FontSubstitutions);
    }

    /// <summary>
    /// The trailing-separator repair must survive the WHOLE post-pass, not just the normaliser:
    /// <c>RoundNumbers</c> rewrites <c>x</c>/<c>y</c> afterwards, so a repair it undid would be
    /// invisible to the unit tests above and shipped anyway.
    /// </summary>
    [Fact]
    public void ThePostPassShipsNoTrailingSeparatorInAPositionList()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <text font-family="Inter" font-size="12" x="0, 8.114, 15.523, " y="12.113, ">Set</text></svg>
            """;

        var result = SvgPostPass.Run(svg, "fixture", out _);

        Assert.DoesNotContain(", \"", result, StringComparison.Ordinal);
        Assert.Contains("y=\"12.11\"", result, StringComparison.Ordinal);
    }

    // ── The post-pass ─────────────────────────────────────────────────────────

    [Fact]
    public void CoordinatesAreRoundedToTwoDecimals()
        => Assert.Equal("M0.5 8.3L1.23 4", SvgPostPass.RoundAll("M0.5 8.30361L1.23456 4"));

    [Fact]
    public void RoundingLeavesIntegersAlone()
        => Assert.Equal("M0 0L320 234Z", SvgPostPass.RoundAll("M0 0L320 234Z"));

    [Fact]
    public void AClipThatCoversTheWholeCanvasIsDroppedAndItsEmptyGroupUnwrapped()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <clipPath id="c"><rect width="100" height="50"/></clipPath>
            <g clip-path="url(#c)"><rect fill="red" width="4" height="4"/></g></svg>
            """;

        var result = SvgPostPass.Run(svg, "fixture", out var report);

        Assert.Equal(1, report.ClipsDropped);
        Assert.DoesNotContain("clipPath", result, StringComparison.Ordinal);
        Assert.DoesNotContain("clip-path", result, StringComparison.Ordinal);
        Assert.Contains("fill=\"red\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void AClipSmallerThanTheCanvasIsKept()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <clipPath id="c"><rect width="10" height="10"/></clipPath>
            <g clip-path="url(#c)"><rect fill="red" width="4" height="4"/></g></svg>
            """;

        var result = SvgPostPass.Run(svg, "fixture", out var report);

        Assert.Equal(0, report.ClipsDropped);
        Assert.Contains("clip-path", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedPathDataIsHoistedIntoDefsAndReferenced()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <path fill="#C3CDD6" d="M0 0L9 9Z"/><path fill="#C3CDD6" d="M0 0L9 9Z"/>
            <path fill="#C3CDD6" d="M0 0L9 9Z"/></svg>
            """;

        var result = SvgPostPass.Run(svg, "fixture", out var report);

        Assert.Equal(3, report.PathsDeduped);
        Assert.Contains("<defs", result, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result, "<use").Count);
    }

    [Fact]
    public void ThePostPassDoesNotChangeWhatIsDrawn()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="50">
            <rect fill="#010101" fill-opacity="0.2" width="10.123456" height="5"/>
            <text fill="#5C6B7A" x="1.5" y="2.5">Hi</text></svg>
            """;

        var result = SvgPostPass.Run(svg, "fixture", out _);

        Assert.Contains("fill-opacity=\"0.2\"", result, StringComparison.Ordinal);
        Assert.Contains("10.12", result, StringComparison.Ordinal);
        Assert.Contains(">Hi<", result, StringComparison.Ordinal);
        Assert.True(SvgLint.HasDrawingElements(result));
    }

    [Fact]
    public void ThePostPassReportsARealSizeReduction()
    {
        // The measured whole-run figure is ~2.1x; a single synthetic document only has to shrink.
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\">"
                   + string.Concat(Enumerable.Repeat(
                       "<clipPath id=\"c" + Guid.Empty.ToString("N") + "\"><rect width=\"100\" height=\"50\"/></clipPath>", 1))
                   + "<g clip-path=\"url(#c" + Guid.Empty.ToString("N") + "\"><path fill=\"red\" d=\"M0.123456 0.987654L9 9Z\"/></g></svg>";

        SvgPostPass.Run(svg, "fixture", out var report);

        Assert.True(report.BytesAfter < report.BytesBefore,
            $"post-pass produced {report.BytesAfter} bytes from {report.BytesBefore}");
    }
}
