using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// The additions that let Hertzon's own PCB and antenna examples open: Auto Open (both kinds), the library materials a design
/// may name without defining, a two-sided Finite Conductivity on a sheet, a wavelength written <c>c0/f0</c>, and a second-order
/// radiation boundary. Each case is the hand-written <c>microstrip-line.hzn</c> fixture changed in code (brief-hzn-0 D2), and
/// the meanings relied on are in <c>src/Design/RESOLVED.md</c> §HZN.
/// </summary>
public sealed class HznAutoOpenAndLibraryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hzn-autoopen-" + Guid.NewGuid().ToString("N"));

    public HznAutoOpenAndLibraryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "microstrip-line.hzn")))!.AsObject();

    private static JsonObject Design(JsonObject p) => p["designs"]![0]!.AsObject();

    /// <summary>The fixture without its drawn region and the radiation boundary on it, with these design settings.</summary>
    private static JsonObject AutoOpen(params (string Key, string Value)[] settings)
    {
        var p = Fixture();
        var d = Design(p);
        var objects = d["objects"]!.AsArray();
        objects.Remove(objects.First(o => o!["name"]!.GetValue<string>() == "Region"));
        var boundaries = d["boundaries"]!.AsArray();
        boundaries.Remove(boundaries.First(b => b!["name"]!.GetValue<string>() == "Rad1"));
        foreach (var (k, v) in settings) d["settings"]![k] = v;
        return p;
    }

    private C3dTranslation Translate(JsonObject p, string? file = null)
    {
        string path = Path.Combine(_tmp, file ?? Guid.NewGuid().ToString("N") + ".hzn");
        File.WriteAllText(path, p.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Line");
    }

    private static JsonElement AirBox(C3dTranslation t) => Assert.Single(t.Document!.Setups).GetProperty("AirBox");

    private static void AllFaces(C3dTranslation t, double paddingUm)
    {
        var air = AirBox(t);
        foreach (string face in new[] { "XMin", "XMax", "YMin", "YMax", "ZMin", "ZMax" })
        {
            Assert.Equal("Absorbing", air.GetProperty(face).GetProperty("Boundary").GetString());
            Assert.Equal(paddingUm, air.GetProperty(face).GetProperty("PaddingUm").GetDouble(), 6);
        }
    }

    [Fact]
    public void AutoOpenFilter_IsAnAirBoxAtTheFilterMargin_AbsorbingOnEveryFace()
    {
        var t = Translate(AutoOpen(("AutoOpen", "Auto Open - Filter"), ("FilterAirMargin", "8mm")));
        Assert.True(t.Ok, t.Refusal?.Render());
        AllFaces(t, 8000);
    }

    [Fact]
    public void AutoOpen_IsAThirdOfTheWavelengthAtTheLowestFrequencySolved_OrTheStatedPadding()
    {
        // The fixture's sweep starts at 4 GHz.
        var t = Translate(AutoOpen(("AutoOpen", "Auto Open"), ("AutoOpenBoundary", "Radiation"), ("AutoOpenPadding", "")));
        Assert.True(t.Ok, t.Refusal?.Render());
        AllFaces(t, 299792458.0 / 4e9 / 3 * 1e6);

        var stated = Translate(AutoOpen(("AutoOpen", "Auto Open"), ("AutoOpenBoundary", "PML"), ("AutoOpenPadding", "12mm")));
        Assert.True(stated.Ok, stated.Refusal?.Render());
        AllFaces(stated, 12000);
        Assert.Contains(stated.Notes, n => n.Contains("PML", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoOpen_BesideADrawnRegion_IsRefused()
    {
        var p = Fixture();
        Design(p)["settings"]!["AutoOpen"] = "Auto Open - Filter";
        Design(p)["settings"]!["FilterAirMargin"] = "8mm";
        var t = Translate(p);
        Assert.False(t.Ok);
        Assert.Contains(t.Refusals, i => i.What.Contains("with a drawn region", StringComparison.Ordinal));
    }

    [Fact]
    public void ALibraryMaterialTheProjectDoesNotDefine_ComesFromHertzonsLibrary()
    {
        var p = Fixture();
        Design(p)["objects"]![0]!["material"] = "FR4_epoxy";
        var t = Translate(p);
        Assert.True(t.Ok, t.Refusal?.Render());
        var m = t.Technology!.FindMaterial("FR4_epoxy")!;
        Assert.Equal(4.4, m.Epsr);
        Assert.Equal(0.02, m.TanD);
    }

    private static JsonObject WithFiniteConductivityOnTrace(bool twoSided)
    {
        var p = Fixture();
        var boundaries = Design(p)["boundaries"]!.AsArray();
        boundaries.Remove(boundaries.First(b => b!["name"]!.GetValue<string>() == "PerfE_Trace"));
        boundaries.Add(JsonNode.Parse($$"""
            {"name": "Cu", "kind": "Finite Conductivity",
             "props": {"UseMaterial": "True", "Material": "copper", "Conductivity": "58000000", "Permeability": "1", "Roughness": "0um",
                       "UseThickness": "True", "Thickness": "35um", "TwoSided": "{{(twoSided ? "True" : "False")}}"},
             "faces": [], "objects": ["Trace"], "edges": [], "enabled": true}
            """));
        return p;
    }

    [Fact]
    public void TwoSidedFiniteConductivityOnASheet_IsALossySheet_AndOneSidedIsRefused()
    {
        var t = Translate(WithFiniteConductivityOnTrace(twoSided: true));
        Assert.True(t.Ok, t.Refusal?.Render());
        var trace = Assert.IsType<C3dSheet>(t.Document!.Objects.Single(o => o.Name == "Trace"));
        Assert.Equal(35, trace.ThicknessUm);
        Assert.Equal(5.8e7, t.Technology!.FindMaterial(trace.Material!)!.Sigma20);

        var one = Translate(WithFiniteConductivityOnTrace(twoSided: false));
        Assert.False(one.Ok);
        Assert.Contains(one.Refusals, i => i.What == "Finite Conductivity on one side of a sheet");
    }

    [Fact]
    public void AnExportedLossySheet_IsWrittenTwoSided_AndReadsBack()
    {
        var t = Translate(WithFiniteConductivityOnTrace(twoSided: true));
        var written = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = "Line", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Line", written.Refusals).Render());
        string path = Path.Combine(_tmp, "exported.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        Assert.Contains("\"TwoSided\": \"True\"", File.ReadAllText(path), StringComparison.Ordinal);
        var back = HznToC3d.Translate(HznFile.Read(path).File!.Project, "Line");
        Assert.True(back.Ok, back.Refusal?.Render());
    }

    [Fact]
    public void AWavelengthWrittenC0OverF0_IsALength_SoItAddsToOne()
    {
        var p = Fixture();
        var vars = Design(p)["variables"]!.AsObject();
        vars["f0"] = "2.5GHz";
        vars["Wsub"] = "0.05*c0/f0";
        vars["pad"] = "c0/f0/40+0.5mm";
        var t = Translate(p);
        Assert.True(t.Ok, t.Refusal?.Render());
        var res = C3dResolver.Resolve(t.Document!, C3dCell.None);
        double lambda = 299792458.0 / 2.5e9;
        Assert.Equal(lambda / 40 + 0.5e-3, res.Evaluate("pad", null).AsReal(), 12);
        Assert.Equal(0.05 * lambda, res.Evaluate("Wsub", null).AsReal(), 12);
    }

    [Fact]
    public void ASecondOrderRadiationBoundary_IsTheAbsorbingFace_AndFirstOrderSaysSo()
    {
        var p = Fixture();
        Design(p)["boundaries"]![2]!["props"]!["Order"] = "Second order";
        var t = Translate(p);
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.Equal("Absorbing", AirBox(t).GetProperty("XMin").GetProperty("Boundary").GetString());

        var first = Translate(Fixture());
        Assert.Contains(first.Notes, n => n.Contains("first-order radiation boundary", StringComparison.Ordinal));
    }
}
