// brief-em3d-31 R-em3d31-3 — the radiation pattern from Palace. The spike (src/Design/RESOLVED.md §brief-em3d-31)
// found the installed 0.18.1 carries Boundaries.Postprocessing.FarField; these check the WRITTEN request and
// its refusals without a solver, and the short-dipole oracle (1.5, 1.76 dBi) through a real Palace run in the
// Benchmark tier.

using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class PalaceRadiationTests(ITestOutputHelper output) : IDisposable
{
    private const double C0 = 299_792_458.0;
    private readonly string _root = Directory.CreateTempSubdirectory("crf-em3d31p-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly PalaceSettings Coarse =
        PalaceSettings.Default with { AdaptiveMaxIterations = 0, MaxElementWavelengths = 0.15, EdgeRefinement = 1 };

    [Fact]
    public void TheFarFieldRequest_NamesTheAbsorbingFaces_AndTheOneDegreeSphere_AndIsAbsentWhenNotAsked()
    {
        var p = OpenEmsRadiationTests.Dipole(0.01, 1e-3, 1e-3, 0.025, 2.5e9, 3.5e9, 3);
        var low = GmshGeoWriter.Write(p, Coarse);
        Assert.True(low.Ok, low.Refusal);
        var off = PalaceConfigWriter.Write(p, low.Groups, Coarse);
        Assert.DoesNotContain("FarField", off.Json!, StringComparison.Ordinal);

        var on = PalaceConfigWriter.Write(p, low.Groups, Coarse, farField: true);
        var b = JsonDocument.Parse(on.Json!).RootElement.GetProperty("Boundaries");
        var ff = b.GetProperty("Postprocessing").GetProperty("FarField");
        Assert.Equal(b.GetProperty("Absorbing").GetProperty("Attributes").EnumerateArray().Select(a => a.GetInt32()),
                     ff.GetProperty("Attributes").EnumerateArray().Select(a => a.GetInt32()));
        Assert.Equal(179 * 360 + 2, ff.GetProperty("ThetaPhis").GetArrayLength());
        Assert.Null(PalaceConfigWriter.FarFieldRefusal(p));
    }

    [Fact]
    public void AConductingFloor_LeavesPalaceNoClosedSurface_AndIsSaidRatherThanRefusingTheRun()
    {
        var p = OpenEmsRadiationTests.Dipole(0.01, 1e-3, 1e-3, 0.025, 2.5e9, 3.5e9, 3, floor: Em3dBoundaryKind.Pec);
        string? why = PalaceConfigWriter.FarFieldRefusal(p);
        output.WriteLine(why);
        Assert.NotNull(why);
        Assert.Contains("zmin face is Pec", why, StringComparison.Ordinal);
        Assert.Contains("FDTD 3D (openEMS)", why, StringComparison.Ordinal);
        var low = GmshGeoWriter.Write(p, Coarse);
        var cfg = PalaceConfigWriter.Write(p, low.Groups, Coarse, farField: true);
        Assert.True(cfg.Ok, cfg.Refusal);
        Assert.DoesNotContain("FarField", cfg.Json!, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A short dipole's directivity is 1.5 (1.76 dBi)</b>, through Palace's own far-field integral and the same
    /// stage. The box is a quarter wavelength from the dipole and Palace's absorbing faces are first order, so the
    /// tolerance is wider than openEMS's (whose PML is not): 0.1 dB, and the measurement is printed.
    ///
    /// <para><b>Directivity, not efficiency.</b> The short dipole is mismatched to 1 − |S₁₁|² of a few thousandths,
    /// and Palace's accepted power is the difference of an incident and a reflected power that agree to that — so
    /// its efficiency is a statement about S₁₁'s fourth digit, not about the far field. openEMS's accepted power
    /// is ½·Re(V·I*) of its probes and does not difference two waves, which is why its test also holds η.</para>
    /// </summary>
    [PalaceFact]
    [Trait("Category", "Benchmark")]
    public void AShortDipole_Directivity_Is1p76Dbi()
    {
        var p = OpenEmsRadiationTests.Dipole(0.1 * C0 / 3e9, 1e-3, 1e-3, C0 / 3e9 / 4, 2.5e9, 3.5e9, 3);
        var low = GmshGeoWriter.Write(p, Coarse);
        Assert.True(low.Ok, low.Refusal);
        var cfg = PalaceConfigWriter.Write(p, low.Groups, Coarse, farField: true);
        Assert.True(cfg.Ok, cfg.Refusal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var mesh = PalaceRun.Mesh(_root, low, SolverDiscovery.Gmsh.Find(out _)!.Path, null, default);
        Assert.True(mesh.Ok, mesh.Message);
        string palace = SolverDiscovery.ReadinessFor(Em3dSolver.Palace).Single(r => r.Tool == SolverTool.Palace).Installation!.Path;
        var solved = PalaceRun.Solve(_root, cfg.Json!, palace, Math.Min(8, Environment.ProcessorCount), null, default, out _,
                                     new PalaceStageTracker(0, Coarse.SweepAdaptiveTol, null));
        Assert.True(solved.Ok, solved.Message);
        output.WriteLine($"Palace: {mesh.Tetrahedra} tetrahedra, {watch.Elapsed.TotalSeconds:F1} s");

        string post = Path.Combine(_root, PalaceConfigWriter.OutputDirectory);
        var s = PalaceRun.ReadPortS(Path.Combine(post, PalaceRun.PortSFile), [1], out string? err);
        Assert.Null(err);
        var data = new DataSet();
        var notes = new List<string>();
        Assert.Null(Em3dRunService.PalacePattern(post, s, p.Ports, new CircuitRF.Design.Layout.Em.EmSetup(), data, notes));
        foreach (string n in notes) output.WriteLine(n);
        var d = data.CubesIn(PlanarFarField.Group)["DirectivityDbi"].RealValues;
        var acc = data.CubesIn(PlanarFarField.Group)["PowerAccepted"].RealValues;
        var rad = data.CubesIn(PlanarFarField.Group)["PowerRadiated"].RealValues;
        for (int i = 0; i < d.Length; i++)
            output.WriteLine($"{s.FrequenciesHz[i] / 1e9:F2} GHz: D = {d[i]:F3} dBi, P_rad/P_acc = {rad[i] / acc[i]:P2}, " +
                             $"1 − |S11|² = {1 - s.S[i][0, 0].Magnitude * s.S[i][0, 0].Magnitude:E3}");
        foreach (double x in d) Assert.True(Math.Abs(x - 10 * Math.Log10(1.5)) <= 0.1, $"D = {x:F3} dBi");
    }
}
