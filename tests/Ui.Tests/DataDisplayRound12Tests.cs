using System;
using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Data Display, owner round of 2026-08-18: the contour RBF defaults, the marker glyph's size against
/// MXP/MXE, and Escape-to-deselect.
/// </summary>
public sealed class DataDisplayRound12Tests
{
    // ── The RBF defaults ────────────────────────────────────────────────────

    /// <summary>
    /// <b>Smoothing 0.1 and epsilon 0.5, from ONE definition.</b>
    ///
    /// <para>They had three independent copies — the persisted config, the runtime contour, and the
    /// trace card — which is three chances for a default to move in two of them. This asserts the
    /// values AND that all three read the same constant.</para>
    /// </summary>
    [Fact]
    public void TheContourRbfDefaultsAreSmooth0Point1AndEpsilon0Point5()
    {
        Assert.Equal(RbfKernel.Multiquadric, ContourDefaults.Kernel);
        Assert.Equal(0.1, ContourDefaults.Smoothing);
        Assert.Equal(0.5, ContourDefaults.Epsilon);

        var contour = new ContourData();
        Assert.Equal(ContourDefaults.Kernel, contour.InterpKernel);
        Assert.Equal(ContourDefaults.Smoothing, contour.Smoothing);
        Assert.Equal(ContourDefaults.Epsilon, contour.Epsilon);

        var config = new ContourTraceConfig();
        Assert.Equal(ContourDefaults.Kernel, config.InterpKernel);
        Assert.Equal(ContourDefaults.Smoothing, config.Smoothing);
        Assert.Equal(ContourDefaults.Epsilon, config.Epsilon);
    }

    /// <summary>
    /// The shipped epsilon is <b>stated</b>, not left on the auto formula — auto is a function of the
    /// node bounding box and the node count, so it moves when the tuner grid changes and two sweeps
    /// of one device at different densities got different surfaces.
    /// </summary>
    [Fact]
    public void TheShippedEpsilonIsStatedRatherThanAuto()
    {
        Assert.NotNull(new ContourData().Epsilon);
        Assert.NotNull(new ContourTraceConfig().Epsilon);
    }

    // ── The marker glyph against MXP/MXE ────────────────────────────────────

    /// <summary>
    /// <b>The contour marker glyph is the MXP/MXE glyph's size at EVERY canvas size</b> (owner,
    /// 2026-08-18: <i>"the marker render size changes relative size to MXP/MXE glyphs depending on
    /// data display zoom level"</i>).
    ///
    /// <para>They used to come from different formulas that landed within 14 % of each other at one
    /// size, and the marker's carried a <c>max(6f, …)</c> floor — so below roughly a 300 px plot it
    /// stopped shrinking while MXP/MXE kept going, reaching 1.71 × at 200 px. A floor is exactly what
    /// breaks proportionality, so the ratio is asserted across the whole range rather than at one
    /// convenient size.</para>
    /// </summary>
    [Theory]
    [InlineData(150.0, 150.0)]
    [InlineData(200.0, 200.0)]
    [InlineData(300.0, 260.0)]
    [InlineData(400.0, 400.0)]
    [InlineData(1200.0, 900.0)]
    public void TheMarkerGlyphMatchesTheOptimumGlyphAtEveryCanvasSize(double w, double h)
    {
        float marker = MarkerRenderer.ContourMarkerRadiusForTests((w, h));
        float optimum = ContourRenderer.OptimumMarkerRadius((w, h));

        Assert.Equal(optimum, marker, 5);
        Assert.True(marker > 0.0f, "A degenerate canvas must still produce a positive radius.");
    }

    /// <summary>
    /// And the size is strictly PROPORTIONAL to the canvas — doubling the canvas doubles the glyph,
    /// with no floor anywhere in the range. This is the property the old <c>max(6f, …)</c> broke, and
    /// the one that makes "at any zoom level" true rather than "at the sizes I happened to try".
    /// </summary>
    [Fact]
    public void TheGlyphSizeIsProportionalToTheCanvasWithNoFloor()
    {
        float small = ContourRenderer.OptimumMarkerRadius((100.0, 100.0));
        float large = ContourRenderer.OptimumMarkerRadius((800.0, 800.0));

        Assert.Equal(8.0f, large / small, 4);

        float markerSmall = MarkerRenderer.ContourMarkerRadiusForTests((100.0, 100.0));
        float markerLarge = MarkerRenderer.ContourMarkerRadiusForTests((800.0, 800.0));
        Assert.Equal(8.0f, markerLarge / markerSmall, 4);
    }

    /// <summary>The letter and the ring follow the same one formula, so the whole family scales together.</summary>
    [Theory]
    [InlineData(200.0)]
    [InlineData(600.0)]
    public void TheFontAndRingScaleWithTheGlyph(double size)
    {
        float lw = AxesRenderer.LineWidth((size, size));

        Assert.Equal(3.5f * lw, ContourRenderer.OptimumMarkerRadius((size, size)), 5);
        Assert.Equal(4.5f * lw, ContourRenderer.OptimumMarkerFontSize((size, size)), 5);
        Assert.Equal(0.75f * lw, ContourRenderer.OptimumMarkerRingWidth((size, size)), 5);
    }

    // ── Escape deselects ────────────────────────────────────────────────────

    /// <summary>
    /// <b>Escape drops every selection</b> — plots and marker info boxes alike, which between them are
    /// everything a Data Display can select.
    /// </summary>
    [Fact]
    public void DeselectAll_ClearsEverySelection()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot();
        display.AddPlot();

        display.SelectAll();
        Assert.True(display.HasAnySelection, "pre-condition: something is selected");

        display.DeselectAll();

        Assert.False(display.HasAnySelection);
        Assert.All(display.Plots, p => Assert.False(p.IsSelected));
        Assert.All(display.MarkerInfoBoxes, m => Assert.False(m.IsSelected));
    }

    /// <summary>Deselecting when nothing is selected is a no-op, not a fault.</summary>
    [Fact]
    public void DeselectAll_OnAnEmptySelection_IsHarmless()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot();

        display.DeselectAll();
        display.DeselectAll();

        Assert.False(display.HasAnySelection);
    }

    /// <summary>
    /// <see cref="DataDisplayViewModel.DeselectAll"/> is the exact inverse of
    /// <see cref="DataDisplayViewModel.SelectAll"/> — round-tripping through both leaves nothing
    /// selected, which is what catches a collection added to one and not the other.
    /// </summary>
    [Fact]
    public void DeselectAll_IsTheInverseOfSelectAll()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot();
        display.AddPlot();
        display.AddPlot();

        for (int i = 0; i < 3; i++)
        {
            display.SelectAll();
            Assert.True(display.HasAnySelection);
            display.DeselectAll();
            Assert.False(display.HasAnySelection);
        }
    }
}
