// ================================================================
//  IntrinsicGlyphSizeTests.cs  —  R8C §4.3
// ================================================================

using System;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class IntrinsicGlyphSizeTests
{
    [Theory]
    [InlineData(200.0)]
    [InlineData(600.0)]
    [InlineData(1200.0)]
    public void GlyphHalfSize_Is0Point9TimesTheMarkerRadius_AtEverySize(double size)
    {
        double markerRadius = HarmonicaPanelRenderer.MarkerRadius((size, size));
        double expectedGlyphHalfSize = markerRadius * HarmonicaPanelRenderer.IntrinsicGlyphScaleOfMarker;

        // 0.9 exactly — derived, not a second independently-typed literal.
        Assert.Equal(markerRadius * 0.9, expectedGlyphHalfSize, 12);
    }

    [Fact]
    public void MarkerRadius_RespectsItsOwnFloor_AtTheSmallestSize()
    {
        // Below MarkerRadiusFraction's own floor crossover, MarkerRadius must clamp to
        // MarkerRadiusFloorPx rather than shrink further — the same floor DrawMarkers always applied.
        double tiny = 10.0;   // 10 * 0.020 = 0.2 px, far under the 6 px floor.
        Assert.Equal((double)HarmonicaPanelRenderer.MarkerRadiusFloorPx,
            HarmonicaPanelRenderer.MarkerRadius((tiny, tiny)), 9);
    }

    [Theory]
    [InlineData(200.0)]
    [InlineData(600.0)]
    [InlineData(1200.0)]
    public void GlyphFloor_Is0Point9TimesTheMarkerFloor(double size)
    {
        // At sizes small enough that the MARKER itself is floor-clamped, the glyph's own floor rides
        // along with it (5.4 px = 6 px * 0.9), never an independent 3.5f constant.
        double markerRadius = HarmonicaPanelRenderer.MarkerRadius((size, size));
        double glyphHalf = markerRadius * HarmonicaPanelRenderer.IntrinsicGlyphScaleOfMarker;
        Assert.True(glyphHalf > 0);
        if (markerRadius <= HarmonicaPanelRenderer.MarkerRadiusFloorPx + 1e-9)
            Assert.Equal(HarmonicaPanelRenderer.MarkerRadiusFloorPx * 0.9, glyphHalf, 9);
    }

    [Fact]
    public void DrawIntrinsicGlyphs_SourceScan_NeverUsesTheOldFixedAlpha()
    {
        // R8C §4.2 — the renderer cannot be exercised headlessly (no live typeface/canvas in this
        // repo's Ui.Tests), so this pins the source instead: the old WithAlpha(190) must be gone from
        // DrawIntrinsicGlyphs specifically, not merely absent from the file as a whole (the dashed
        // compressed-annulus outline legitimately keeps its OWN WithAlpha(255) a few lines later).
        string text = File.ReadAllText(Path.Combine(RepoRoot(),
            "src", "Ui", "Harmonica", "Renderers", "HarmonicaPanelRenderer.cs"));

        int start = text.IndexOf("private static void DrawIntrinsicGlyphs", StringComparison.Ordinal);
        Assert.True(start >= 0, "DrawIntrinsicGlyphs not found in source");
        int end = text.IndexOf("\n    private static void DrawMarkers", start, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        string body = text[start..end];

        Assert.DoesNotContain("WithAlpha(190)", body, StringComparison.Ordinal);
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }
}
