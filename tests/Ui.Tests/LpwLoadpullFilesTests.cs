using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Loadpull;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Brief LPW, gates 3 and 4 (the display half): every place that opens a loadpull file goes through
/// <see cref="LoadpullFiles.Read"/>, so a <c>.cst</c> opens in the Data Display, in <c>render</c> and as a Model Composer
/// set alike; and a contour or summary written before the compression reference existed reads back as it drew.
/// </summary>
public sealed class LpwLoadpullFilesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Cst => Path.Combine(RepoRoot(), "testdata", "lpw", "synthetic_2f0.cst");

    [Fact]
    public void NoFileButTheDispatcher_CallsALoadpullReader()
    {
        var call = new Regex(@"\b(SplReader\.ReadSpl|LpcwaveReader\.ReadLpcwave|CstReader\.Read|SatwaveReader\.Read)\s*\(");
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "LoadpullFiles.cs")
            .Where(f => call.IsMatch(Regex.Replace(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "")))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();
        Assert.True(offenders.Count == 0, "read a loadpull file through LoadpullFiles.Read: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task ACstFile_OpensInTheDataDisplay_InRender_AndAsAComposerSet_ThroughTheSameCall()
    {
        var expected = LoadpullFiles.Read(Cst);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(Cst);
        var entry = Assert.Single(lib.Entries);
        Assert.False(entry.IsBroken);
        Assert.Equal(SourceKind.Lpcwave, entry.Kind);

        var (rendered, _, error) = PlotSourceFile.Load(Cst);
        Assert.Null(error);

        foreach (var ds in new[] { entry.Data!, rendered! })
            foreach (var cube in new[] { "Pout_dBm", "PinDel_dBm", "Gt_dB", "Pout_2f0_dBm" })
                Assert.Equal(expected[cube].RealValues, ds[cube].RealValues);

        Assert.Equal(ImportSettings.Loadpull, ModelCommands.FormatOf(Cst));
    }

    [Fact]
    public void AContour_IsNeverHeldAtAConstantLevelOfItsOwnQuantity()
    {
        // The card greys a constraint in the plotted metric's group: Pout in dBm and in W are one quantity.
        Assert.Equal(ContourData.MetricAliasGroup("Pout_dBm"), ContourData.MetricAliasGroup("Pout_W"));
        Assert.NotEqual(ContourData.MetricAliasGroup("Pout_dBm"), ContourData.MetricAliasGroup("PinDel_dBm"));

        // ...and a pair that reaches the resolver anyway (a hand-written .cdd) draws nothing and says why.
        var trace = new CircuitRF.Render.DataDisplay.Trace(new RfCore.SNP(new double[] { 1e9 }, 1), RfCore.MatrixType.S, 0, 0,
                                                           CircuitRF.Render.DataDisplay.DependentVarFormat.Db, false)
        {
            ContourData = new ContourData { MetricName = "Pout_dBm", ContourConstraintKind = ConstraintKind.ConstantMetric,
                                            ConstraintMetricName = "Pout_W", ConstraintValue = 0.1 },
        };
        Assert.False(ContourResolve.Rebuild(trace, new LoadpullSurface(LoadpullFiles.Read(Cst)), PlotType.Smith));
        Assert.Null(trace.ContourData.Grid);
        Assert.StartsWith("No contour: Pout_dBm cannot be held at a constant Pout_W", trace.ContourData.Status);
    }

    [Fact]
    public async Task SwitchingANewContourToConst_PicksAConstraintThatIsNotItsOwnQuantity()
    {
        var lib = new DataSourceLibraryViewModel();
        await lib.SelectDataSourceAsync(Cst);
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var inspector = new PlotInspectorViewModel(plot, () => { }, library: lib);
        inspector.AddContourTraceCommand.Execute(null);
        var row = Assert.Single(inspector.Traces);
        string metric = plot.Traces[0].ContourData!.MetricName;

        row.SetConstraintConstantCommand.Execute(null);

        var cd = plot.Traces[0].ContourData!;
        Assert.Equal(ConstraintKind.ConstantMetric, cd.ContourConstraintKind);
        Assert.NotEqual(ContourData.MetricAliasGroup(metric), ContourData.MetricAliasGroup(cd.ConstraintMetricName));
        Assert.Equal(cd.ConstraintMetricName, row.ContourConstraintMetric);
        Assert.All(row.ConstraintMetricOptions.Where(o => ContourData.MetricAliasGroup(o.Name) == ContourData.MetricAliasGroup(metric)),
                   o => Assert.False(o.IsEnabled, o.Name));
    }

    [Fact]
    public void AContoursMaximumAndMinimumMarkers_LandOnTheMeasuredLoadsWithTheHighestAndLowestValue()
    {
        // The contour's scatter — its loads at its constraint, what ContourResolve stores (the 3-load fixture is too few
        // for a fit, which the marker does not need).
        var ds = LoadpullFiles.Read(Cst);
        var scatter = new LoadpullSurface(ds).Reduce(0, "Pout_dBm", ConstraintSpec.AtCompression(1), SurfacePlane.Gamma);
        Assert.Equal(3, scatter.Values.Length);
        var cd = new ContourData { MetricName = "Pout_dBm", ConstraintValue = 1, Scatter = scatter };
        int iMax = Array.IndexOf(scatter.Values, scatter.Values.Max()), iMin = Array.IndexOf(scatter.Values, scatter.Values.Min());
        Assert.NotEqual(iMax, iMin);

        var max = Assert.NotNull(cd.ExtremeNode(maximum: true));
        var min = Assert.NotNull(cd.ExtremeNode(maximum: false));
        Assert.Equal(scatter.Coords[iMax], max);
        Assert.Equal(scatter.Coords[iMin], min);
        // Each is one of the file's own loads, not a point on the fitted surface.
        Assert.Contains(ds["GammaLoad"].ComplexValues, g => (g - max).Magnitude < 1e-12);
        Assert.Contains(ds["GammaLoad"].ComplexValues, g => (g - min).Magnitude < 1e-12);
    }

    [Fact]
    public void AContourOrSummaryWrittenBeforeTheCompressionReference_ReadsBackAsItDrew()
    {
        // An older .cdd's contour: no reference keys at all.
        var ct = JsonSerializer.Deserialize<ContourTraceConfig>("""{"MetricName":"Pout_dBm","ConstraintKind":"Compression","ConstraintValue":3}""")!;
        Assert.Null(ct.CompressionReference);
        Assert.Equal(CompressionType.Gmax, ct.CompressionType);
        var cd = new ContourData { MetricName = ct.MetricName, ConstraintValue = ct.ConstraintValue,
                                   CompressionReference = ct.CompressionReference, CompressionType = ct.CompressionType };
        Assert.Equal("P-3dB Pout (dBm)", cd.TitleString());
        Assert.Equal(ConstraintSpec.AtCompression(3).Reference, ConstraintSpec.AtCompression(3, ct.CompressionReference, ct.CompressionType).Reference);

        // ...and re-saves without them, so the file is unchanged.
        string json = JsonSerializer.Serialize(ct);
        Assert.DoesNotContain("CompressionReference", json);
        Assert.DoesNotContain("CompressionType", json);
        string table = JsonSerializer.Serialize(new PlotContainerConfig());
        Assert.DoesNotContain("TableCompressionReference", table);
        Assert.DoesNotContain("TableCompressionType", table);

        // A non-default one says so in its title.
        cd.CompressionReference = CompressionGain.Gp; cd.CompressionType = CompressionType.Gss;
        Assert.Equal("P-3dB (Gp from Gss) Pout (dBm)", cd.TitleString());
    }
}
