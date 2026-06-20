// ================================================================
//  ContourTraceCardTests.cs
//  Gate tests for brief-7.4e — contour trace inspector card
//
//  T1  ContourDefaults_LevelRange_Pout       — Pout gets -30:0.5:60
//  T2  ContourDefaults_LevelRange_DE         — DE gets 0:5:100
//  T3  ContourDefaults_LevelRange_Unknown    — unknown metric gets 0:1:10
//  T4  ContourData_FillType_ShowFillFalse    — ShowFill=false → FillType=None
//  T5  ContourData_FillType_TopoMap          — ShowFill=true, TopoMap kind → TopoMap
//  T6  ContourData_FillType_HeatMap          — ShowFill=true, HeatMap kind → HeatMap
//  T7  ContourDefaults_ShowFillDefault_Gamma — Gamma plane → ShowFill false
//  T8  ContourDefaults_ShowFillDefault_Z     — Z plane → ShowFill true
//  T9  ContourTraceConfig_RoundTrip          — defaulted ContourTraceConfig survives JSON round-trip
//  T10 BuildTraceConfig_ContourTrace_Persists — BuildTraceConfig emits ContourTraceConfig
//  T11 VM_AddContourTrace_PopulatesTraces    — AddContourTrace adds a trace row with IsContourTrace=true
//  T12 TraceRowVm_IsContourTrace_Properties  — IsStandardTrace/IsContourTrace are complementary
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ContourTraceCardTests
{
    // ── T1 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourDefaults_LevelRange_Pout_Returns_Neg30_Half_60()
    {
        var (start, step, stop) = ContourDefaults.LevelRange("Pout");
        Assert.Equal(-30.0, start);
        Assert.Equal(  0.5, step);
        Assert.Equal( 60.0, stop);
    }

    // ── T2 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourDefaults_LevelRange_DE_Returns_0_5_100()
    {
        var (start, step, stop) = ContourDefaults.LevelRange("DE");
        Assert.Equal(  0.0, start);
        Assert.Equal(  5.0, step);
        Assert.Equal(100.0, stop);
    }

    // ── T3 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourDefaults_LevelRange_UnknownMetric_Returns_0_1_10()
    {
        var (start, step, stop) = ContourDefaults.LevelRange("XYZ_metric_nobody_knows");
        Assert.Equal( 0.0, start);
        Assert.Equal( 1.0, step);
        Assert.Equal(10.0, stop);
    }

    // ── T4 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourData_FillType_WhenShowFillFalse_IsNone()
    {
        var cd = new ContourData { ShowFill = false, SelectedFillKind = ContourFillKind.TopoMap };
        Assert.Equal(ContourFillType.None, cd.FillType);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourData_FillType_WhenShowFillTrue_TopoMap_IsTopoMap()
    {
        var cd = new ContourData { ShowFill = true, SelectedFillKind = ContourFillKind.TopoMap };
        Assert.Equal(ContourFillType.TopoMap, cd.FillType);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourData_FillType_WhenShowFillTrue_HeatMap_IsHeatMap()
    {
        var cd = new ContourData { ShowFill = true, SelectedFillKind = ContourFillKind.HeatMap };
        Assert.Equal(ContourFillType.HeatMap, cd.FillType);
    }

    // ── T7 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourDefaults_ShowFillDefault_GammaPlane_IsFalse()
    {
        Assert.False(ContourDefaults.ShowFillDefault(SurfacePlane.Gamma));
    }

    // ── T8 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourDefaults_ShowFillDefault_ZPlane_IsTrue()
    {
        Assert.True(ContourDefaults.ShowFillDefault(SurfacePlane.Z));
    }

    // ── T9 ────────────────────────────────────────────────────────────────────
    [Fact]
    public void ContourTraceConfig_JsonRoundTrip_DefaultsPreserved()
    {
        var cfg = new ContourTraceConfig
        {
            MetricName   = "Gain",
            LevelStart   = -10.0,
            LevelStep    =   0.5,
            LevelStop    =  50.0,
            LevelCount   = 20,
            ShowIsoLines = true,
            ShowFill     = false,
            DrawLabels   = true,
            SelectedFillKind = ContourFillKind.HeatMap,
            ColorMap     = ContourColorMap.Cool,
            LabelSpacing = 2.5,
        };

        var opts = new JsonSerializerOptions { WriteIndented = false };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var json   = JsonSerializer.Serialize(cfg, opts);
        var result = JsonSerializer.Deserialize<ContourTraceConfig>(json, opts)!;

        Assert.Equal("Gain",               result.MetricName);
        Assert.Equal(-10.0,                result.LevelStart);
        Assert.Equal(0.5,                  result.LevelStep);
        Assert.Equal(50.0,                 result.LevelStop);
        Assert.Equal(20,                   result.LevelCount);
        Assert.True(                       result.ShowIsoLines);
        Assert.False(                      result.ShowFill);
        Assert.True(                       result.DrawLabels);
        Assert.Equal(ContourFillKind.HeatMap, result.SelectedFillKind);
        Assert.Equal(ContourColorMap.Cool, result.ColorMap);
        Assert.Equal(2.5,                  result.LabelSpacing);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void BuildTraceConfig_ContourTrace_EmitsContourTraceConfig()
    {
        var snp = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.ContourData = new ContourData
        {
            MetricName    = "PAE",
            ShowFill      = true,
            SelectedFillKind = ContourFillKind.TopoMap,
            LevelStart    = 0.0,
            LevelStep     = 5.0,
            LevelStop     = 100.0,
        };

        var tc = DataDisplayViewModel.BuildTraceConfig(trace, "/tmp");

        Assert.NotNull(tc.ContourTrace);
        Assert.Equal("PAE",               tc.ContourTrace!.MetricName);
        Assert.True(                      tc.ContourTrace!.ShowFill);
        Assert.Equal(ContourFillKind.TopoMap, tc.ContourTrace!.SelectedFillKind);
        Assert.Equal(5.0,                 tc.ContourTrace!.LevelStep);
    }

    // ── T11 ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AddContourTraceCommand_WhenLibraryHasLoadpullEntry_AddsContourTrace()
    {
        var path = FindSplFile();
        if (path is null)
        {
            // Skip if test data isn't present in this environment.
            return;
        }

        var lib = new DataSourceLibraryViewModel();
        await lib.SelectDataSourceAsync(path);

        Assert.Single(lib.Entries);

        var plot     = new Plot(PlotType.Smith, FreqUnit.GHz);
        var inspector = new PlotInspectorViewModel(plot, () => {}, library: lib);

        Assert.True(inspector.CanAddContourTrace);

        inspector.AddContourTraceCommand.Execute(null);

        Assert.Single(plot.Traces);
        Assert.True(plot.Traces[0].IsContourTrace);
    }

    // ── T12 ───────────────────────────────────────────────────────────────────
    [Fact]
    public void TraceRowVm_IsContourTrace_And_IsStandardTrace_AreComplementary()
    {
        var snp = new SNP(new[] { 1e9, 2e9 }, 2, MatrixType.S, MatrixFormat.MA);
        var stdTrace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);

        var contourSnp = new SNP(new[] { 1e9 }, 1);
        var contourTrace = new Trace(contourSnp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        contourTrace.ContourData = new ContourData();

        var plot      = new Plot(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(stdTrace);
        plot.Traces.Add(contourTrace);

        var inspector = new PlotInspectorViewModel(plot, () => {}, library: null);

        var stdRow     = inspector.Traces[0];
        var contourRow = inspector.Traces[1];

        Assert.False(stdRow.IsContourTrace);
        Assert.True (stdRow.IsStandardTrace);
        Assert.True (contourRow.IsContourTrace);
        Assert.False(contourRow.IsStandardTrace);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string? FindSplFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "spl_test_data",
                                    "Ideal_GaN_FET_1p6_mm_1p8_GHz.spl");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
