// brief-em3d-31 R-em3d31-1 — the far-field METRICS STAGE was factored away from the MoM transform so a 3D
// solver can drive the same "farfield" cubes. The planar result had to come out byte for byte as it did
// before the split; the golden below was dumped from the pre-split code, BEFORE a line of the split was
// written, and this test holds every cube of it to exact equality.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class FarFieldStageTests(ITestOutputHelper output)
{
    private const double FHz = 5e9;

    private static string GoldenPath => Path.Combine(TestDataRoot(), "em3d", "farfield", "planar-line-golden.npy");

    private static string TestDataRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A two-port FR-4 line, two frequencies, de-embedded, with the whole far-field block on a coarse
    /// grid: every cube the stage emits (pattern, metrics, the derived beamwidth cut, polarization) on two
    /// ports and two frequencies, in about a second.</summary>
    private static DataSet PlanarRun()
    {
        var problem = PlanarLineFixtures.Fr4Line(4e-3, FHz);
        var far = new PlanarFarFieldSettings(PlanarFarFieldGrid.Hemisphere(5, 15), FrequenciesHz: [4.5e9, FHz]);
        var result = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [4.5e9, FHz],
            new PlanarSolveSettings(FarField: far));
        var ds = new DataSet();
        foreach (var (name, cube) in result.Data.CubesIn(PlanarFarField.Group))
            ds.AddToGroup(PlanarFarField.Group, name, cube);
        ds.Add("S", result.Data["S"]);
        return ds;
    }

    [Fact]
    public void PlanarFarFieldGroup_IsByteIdenticalToThePreSplitGolden()
    {
        var ds = PlanarRun();
        if (Environment.GetEnvironmentVariable("CRF_WRITE_FARFIELD_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            DataSetExporter.Export(ds, GoldenPath, ExportFormat.Npy);
            output.WriteLine($"wrote {GoldenPath}");
        }

        var (golden, _) = DataSetImporter.Import(GoldenPath);
        var want = golden.CubesIn(PlanarFarField.Group);
        var have = ds.CubesIn(PlanarFarField.Group);
        Assert.Equal(want.Keys.OrderBy(k => k, StringComparer.Ordinal), have.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (name, g) in want)
        {
            var h = have[name];
            Assert.Equal(g.Unit, h.Unit);
            Assert.Equal(g.Axes.Select(a => a.Name), h.Axes.Select(a => a.Name));
            for (int a = 0; a < g.Axes.Count; a++) Assert.Equal(g.Axes[a].Values, h.Axes[a].Values);
            Assert.Equal(g.DataKind, h.DataKind);
            if (g.DataKind == DataKind.Complex) Assert.Equal(g.ComplexValues.ToArray(), h.ComplexValues.ToArray());
            else Assert.Equal(g.RealValues.ToArray(), h.RealValues.ToArray());
        }
        output.WriteLine($"{want.Count} farfield cubes identical: {string.Join(", ", want.Keys)}");
    }
}
