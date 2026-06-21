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
}
