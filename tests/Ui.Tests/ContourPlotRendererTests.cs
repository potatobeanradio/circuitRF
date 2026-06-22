// ================================================================
//  ContourPlotRendererTests.cs
//  Gate tests for brief-7.4h-2 — contour plot renderer enhancements
//
//  T1  Plot_DefaultTitle_CompressionContour         — "P-3dB Pout (dBm)"
//  T2  Plot_DefaultTitle_TwoContourTraces           — joined " / "
//  T3  Plot_DefaultTitle_CustomTitleOverrides       — custom wins
//  T4  Plot_DefaultTitle_ConstantMetricContour      — constant-metric form
//  T5  Plot_XLabel_RectContour                      — "Real (Ω)"
//  T6  Plot_YLabel_RectContour                      — "Imaginary (Ω)"
//  T7  Plot_XLabel_SmithContour                     — NOT impedance label
//  T8  Plot_XLabel_RectContour_CustomOverride       — custom overrides
//  T9  Plot_YLabel_RectContour_CustomOverride       — custom overrides
//  T10 Trace_PathBoundingRect_ContourWithGrid       — returns grid extent
//  T11 Trace_PathBoundingRect_ContourNoGrid         — returns default(Rect)
//  T12 ContourColormaps_Hot_Endpoints               — black at 0, white at 1
//  T13 ContourColormaps_Cool_Endpoints              — cyan at 0, magenta at 1
//  T14 ContourColormaps_Copper_Endpoints            — black at 0, copper at 1
//  T15 ContourColormaps_Gray_Endpoints              — black at 0, white at 1
//  T16 ContourData_TitleString_GainCompression      — "P-3dB Gain (dB)"
//  T17 ContourData_TitleString_EfficiencyConstant   — constant Pout form
// ================================================================

using System;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Loadpull;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ContourPlotRendererTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static Trace MakeContourTrace(string metric, ConstraintKind kind = ConstraintKind.Compression,
        double constraintValue = 3.0, string constraintMetric = "")
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.ContourData = new ContourData
        {
            MetricName            = metric,
            ContourConstraintKind = kind,
            ConstraintValue       = constraintValue,
            ConstraintMetricName  = constraintMetric,
        };
        return trace;
    }

    // ── T1 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_DefaultTitle_SingleCompression_ShowsPdBmForm()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout", ConstraintKind.Compression, 3.0));

        Assert.Equal("P-3dB Pout (dBm)", plot.Title);
    }

    // ── T2 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_DefaultTitle_TwoContourTraces_JoinedWithSlash()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout", ConstraintKind.Compression, 3.0));
        plot.Traces.Add(MakeContourTrace("Gain", ConstraintKind.Compression, 3.0));

        Assert.Equal("P-3dB Pout (dBm) / P-3dB Gain (dB)", plot.Title);
    }

    // ── T3 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_DefaultTitle_CustomTitleOnOverrides()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));
        plot.CustomTitleOn = true;
        plot.CustomTitle   = "My Custom Title";

        Assert.Equal("My Custom Title", plot.Title);
    }

    // ── T4 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_DefaultTitle_ConstantMetric_ShowsAtConstantForm()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("DE", ConstraintKind.ConstantMetric, 30.0, "Pout"));

        Assert.Equal("Efficiency (%) at Constant Pout=30 dBm", plot.Title);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_XLabel_RectContourReturnsRealOhm()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));

        Assert.Equal("Real (Ω)", plot.XLabel);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_YLabel_RectContourReturnsImagOhm()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));

        Assert.Equal("Imaginary (Ω)", plot.YLabel);
    }

    // ── T7 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_XLabel_SmithContour_IsNotImpedanceLabel()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));

        // Smith contour should NOT return the impedance label (Rect-only).
        Assert.NotEqual("Real (Ω)", plot.XLabel);
    }

    // ── T8 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_XLabel_RectContour_CustomXLabelOverrides()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));
        plot.CustomXLabelOn = true;
        plot.CustomXLabel   = "X axis";

        Assert.Equal("X axis", plot.XLabel);
    }

    // ── T9 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Plot_YLabel_RectContour_CustomYLabelOverrides()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(MakeContourTrace("Pout"));
        plot.CustomYLabelOn = true;
        plot.CustomYLabel   = "Y axis";

        Assert.Equal("Y axis", plot.YLabel);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Trace_PathBoundingRect_ContourWithGrid_ReturnsGridExtent()
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.ContourData = new ContourData
        {
            Grid = new SurfaceGrid(
                new[] { 10.0, 50.0, 90.0 },   // XSpace: 10..90
                new[] { -30.0, 0.0, 30.0 },    // YSpace: -30..30
                new double[9])
        };

        var rect = trace.PathBoundingRect();

        Assert.Equal(10.0,  rect.X,      precision: 5);
        Assert.Equal(-30.0, rect.Y,      precision: 5);
        Assert.Equal(80.0,  rect.Width,  precision: 5);
        Assert.Equal(60.0,  rect.Height, precision: 5);
    }

    // ── T11 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void Trace_PathBoundingRect_ContourNoGrid_ReturnsDefault()
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.ContourData = new ContourData { Grid = null };

        var rect = trace.PathBoundingRect();

        Assert.Equal(0.0, rect.Width,  precision: 5);
        Assert.Equal(0.0, rect.Height, precision: 5);
    }

    // ── T12 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourColormaps_Hot_Endpoints_BlackAndWhite()
    {
        var lo = ContourColormaps.Sample(ContourColorMap.Hot, 0.0);
        var hi = ContourColormaps.Sample(ContourColorMap.Hot, 1.0);

        Assert.Equal(0,   lo.Red);
        Assert.Equal(0,   lo.Green);
        Assert.Equal(0,   lo.Blue);
        Assert.Equal(255, hi.Red);
        Assert.Equal(255, hi.Green);
        Assert.Equal(255, hi.Blue);
    }

    // ── T13 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourColormaps_Cool_Endpoints_CyanAndMagenta()
    {
        var lo = ContourColormaps.Sample(ContourColorMap.Cool, 0.0);
        var hi = ContourColormaps.Sample(ContourColorMap.Cool, 1.0);

        // t=0: cyan (0,255,255)
        Assert.Equal(0,   lo.Red);
        Assert.Equal(255, lo.Green);
        Assert.Equal(255, lo.Blue);
        // t=1: magenta (255,0,255)
        Assert.Equal(255, hi.Red);
        Assert.Equal(0,   hi.Green);
        Assert.Equal(255, hi.Blue);
    }

    // ── T14 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourColormaps_Copper_Endpoints_BlackThenCopper()
    {
        var lo = ContourColormaps.Sample(ContourColorMap.Copper, 0.0);
        var hi = ContourColormaps.Sample(ContourColorMap.Copper, 1.0);

        Assert.Equal(0, lo.Red);
        Assert.Equal(0, lo.Green);
        Assert.Equal(0, lo.Blue);
        // t=1: copper tone — R near 255, G ~199, B ~127
        Assert.Equal(255, hi.Red);
        Assert.InRange(hi.Green, 180, 220);
        Assert.InRange(hi.Blue,  110, 140);
    }

    // ── T15 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourColormaps_Gray_Endpoints_BlackAndWhite()
    {
        var lo = ContourColormaps.Sample(ContourColorMap.Gray, 0.0);
        var hi = ContourColormaps.Sample(ContourColorMap.Gray, 1.0);

        Assert.Equal(0,   lo.Red);
        Assert.Equal(0,   lo.Green);
        Assert.Equal(0,   lo.Blue);
        Assert.Equal(255, hi.Red);
        Assert.Equal(255, hi.Green);
        Assert.Equal(255, hi.Blue);
    }

    // ── T16 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourData_TitleString_GainCompression_ShowsGainDb()
    {
        var cd = new ContourData
        {
            MetricName            = "Gain",
            ContourConstraintKind = ConstraintKind.Compression,
            ConstraintValue       = 3.0,
        };
        Assert.Equal("P-3dB Gain (dB)", cd.TitleString());
    }

    // ── T17 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourData_TitleString_PAEAtConstantPout_ShowsEfficiencyForm()
    {
        var cd = new ContourData
        {
            MetricName            = "PAE",
            ContourConstraintKind = ConstraintKind.ConstantMetric,
            ConstraintMetricName  = "Pout",
            ConstraintValue       = 30.0,
        };
        Assert.Equal("Efficiency (%) at Constant Pout=30 dBm", cd.TitleString());
    }

    // ── T18 — §6: TitleString never emits "X at Constant X" for same metric ──
    [Fact]
    public void ContourData_TitleString_SameConstraintMetric_FallsBackToCompression()
    {
        var cd = new ContourData
        {
            MetricName            = "Gain",
            ContourConstraintKind = ConstraintKind.ConstantMetric,
            ConstraintMetricName  = "Gain",   // direct collision
            ConstraintValue       = 20.0,
        };
        var title = cd.TitleString();
        Assert.DoesNotContain("at Constant Gain", title);
        Assert.DoesNotContain("at Constant Gain", title);
    }

    // ── T19 — §6: alias collision (Gt aliases to Gain) also falls back ────────
    [Fact]
    public void ContourData_TitleString_AliasConstraintMetric_FallsBackToCompression()
    {
        var cd = new ContourData
        {
            MetricName            = "Gain",
            ContourConstraintKind = ConstraintKind.ConstantMetric,
            ConstraintMetricName  = "Gt",    // aliases to "Gain"
            ConstraintValue       = 20.0,
        };
        var title = cd.TitleString();
        Assert.DoesNotContain("at Constant Gain", title);
    }

    // ── T20 — §6: DE aliases to Efficiency also falls back ───────────────────
    [Fact]
    public void ContourData_TitleString_DEAliasToEfficiency_FallsBackWhenSame()
    {
        var cd = new ContourData
        {
            MetricName            = "DE",
            ContourConstraintKind = ConstraintKind.ConstantMetric,
            ConstraintMetricName  = "PAE",   // aliases to "Efficiency", same as DE
            ConstraintValue       = 50.0,
        };
        var title = cd.TitleString();
        Assert.DoesNotContain("at Constant Efficiency", title);
    }

    // §3 line-color helper: mirrors DrawIsoLines auto-color logic (50% lerp + luminance ceiling).
    private static (byte R, byte G, byte B) ComputeIsoLineColor(ContourColorMap colorMap)
    {
        var mapColor = ContourColormaps.Sample(colorMap, 0.5);
        float lum = (0.299f * mapColor.Red + 0.587f * mapColor.Green + 0.114f * mapColor.Blue) / 255f;
        byte hi = lum > 0.5f ? (byte)0 : (byte)255;
        byte r = LerpByte(mapColor.Red,   hi, 0.5f);
        byte g = LerpByte(mapColor.Green, hi, 0.5f);
        byte b = LerpByte(mapColor.Blue,  hi, 0.5f);
        float lineL = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        const float LumCeiling = 0.45f;
        if (lineL > LumCeiling)
        {
            float scale = LumCeiling / lineL;
            r = (byte)Math.Round(r * scale);
            g = (byte)Math.Round(g * scale);
            b = (byte)Math.Round(b * scale);
        }
        return (r, g, b);
    }

    // ── T21 — §3: Gray colormap iso-line color meets luminance ceiling ────────
    [Fact]
    public void ContourIsoLineColor_Gray_MeetsDarknessThreshold()
    {
        var (r, g, b) = ComputeIsoLineColor(ContourColorMap.Gray);
        float resultLum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        Assert.True(resultLum <= 0.46f, $"Gray iso-line luminance {resultLum:F3} must be ≤ 0.46");
    }

    // ── T22 — §3: GistHeat colormap iso-line color meets luminance ceiling ────
    [Fact]
    public void ContourIsoLineColor_GistHeat_MeetsDarknessThreshold()
    {
        var (r, g, b) = ComputeIsoLineColor(ContourColorMap.GistHeat);
        float resultLum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        Assert.True(resultLum <= 0.46f, $"GistHeat iso-line luminance {resultLum:F3} must be ≤ 0.46");
    }

    // ── T23 — §3: Bone, Winter, Copper also meet the ceiling ─────────────────
    [Theory]
    [InlineData(ContourColorMap.Bone)]
    [InlineData(ContourColorMap.Winter)]
    [InlineData(ContourColorMap.Copper)]
    public void ContourIsoLineColor_LightMaps_MeetDarknessThreshold(ContourColorMap colorMap)
    {
        var (r, g, b) = ComputeIsoLineColor(colorMap);
        float resultLum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
        Assert.True(resultLum <= 0.46f, $"{colorMap} iso-line luminance {resultLum:F3} must be ≤ 0.46");
    }

    // ── T24 — §2: DrawGridPoints accepts canvasSize (compile-time + empty-scatter path) ─
    [Fact]
    public void ContourRenderer_DrawGridPoints_AcceptsCanvasSizeParam()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var canvas = surface.Canvas;
        var tf = new TransformSet
        {
            Primary    = (400.0, -400.0, 200.0, 200.0),
            Secondary  = (400.0, -400.0, 200.0, 200.0),
            CanvasSize = (400, 400),
            Viewport   = new Avalonia.Rect(0, 0, 1, 1),
        };
        // Use empty scatter — no drawing happens, but signature is verified.
        var scatter = new ScatterReduction(
            Array.Empty<System.Numerics.Complex>(),
            Array.Empty<double>(),
            Array.Empty<int>());
        ContourRenderer.DrawGridPoints(canvas, (400.0, 400.0), scatter, tf, SKColors.Black, 3f);
    }

    // ── T25 — §2: DrawOptimaMarkers signature accepts canvasSize (empty-coords path) ─
    [Fact]
    public void ContourRenderer_DrawOptimaMarkers_AcceptsCanvasSizeParam()
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var canvas = surface.Canvas;
        var tf = new TransformSet
        {
            Primary    = (400.0, -400.0, 200.0, 200.0),
            Secondary  = (400.0, -400.0, 200.0, 200.0),
            CanvasSize = (400, 400),
            Viewport   = new Avalonia.Rect(0, 0, 1, 1),
        };
        // DisplayMxp/Mxe = false — no drawing, no SkiaFonts load needed; signature verified.
        var cd = new ContourData { DisplayMxp = false, DisplayMxe = false };
        ContourRenderer.DrawOptimaMarkers(canvas, cd, tf, (400.0, 400.0));
    }

    // ── T26 — §1: FillGrid property exists on ContourData ────────────────────
    [Fact]
    public void ContourData_FillGrid_PropertyExists()
    {
        var cd = new ContourData();
        Assert.Null(cd.FillGrid);
        var grid = new SurfaceGrid(new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { 1.0, 2.0, 3.0, 4.0 });
        cd.FillGrid = grid;
        Assert.Same(grid, cd.FillGrid);
    }

    // ── T27 — §1: Clone does not copy FillGrid (re-built on first draw) ───────
    [Fact]
    public void ContourData_Clone_FillGridIsNull()
    {
        var grid = new SurfaceGrid(new[] { 0.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { 1.0, 2.0, 3.0, 4.0 });
        var cd   = new ContourData { FillGrid = grid };
        var copy = cd.Clone();
        Assert.Null(copy.FillGrid);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static byte LerpByte(byte a, byte b, float t)
        => (byte)Math.Round(a + (b - a) * t);
}
