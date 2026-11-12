using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-12 R-hzn12-5 — Hertzon's Incident Plane Waves: each translated to a plane wave of the 3D view (its name, its enabled flag,
/// its Cartesian vectors), its Infinite Sphere to the cross-section grid's steps; the enabled one drives the setup; saved back as the
/// keys that changed alone; and written as Hertzon writes one. The fixture (<c>plane-wave-sphere.hzn</c>) is a perfectly conducting sphere in Auto Open with one plane wave, written for
/// these tests.
/// </summary>
public sealed class HznPlaneWaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn12_" + Guid.NewGuid().ToString("N")[..8]);

    public HznPlaneWaveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Fixture = "plane-wave-sphere.hzn";

    private string Copy(Func<string, string>? edit = null)
    {
        string path = Path.Combine(_root, Fixture);
        string text = File.ReadAllText(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), Fixture));
        File.WriteAllText(path, edit is null ? text : edit(text));
        return path;
    }

    private static C3dTranslation Translate(string path, string design = "Ball")
        => HznToC3d.Translate(HznFile.Read(path).File!.Project, design);

    private static EmSetup OnlySetup(C3dDocument doc) => C3dSetups.Read(doc).Single().Setup!;

    /// <summary>R-hzn12-5 (HZN-2) — the wave becomes a plane wave of the view under its own name, which drives the setup; the sphere's 5°
    /// steps are its grid; the objects list is a note.</summary>
    [Fact]
    public void AnIncidentPlaneWave_TranslatesToAWaveOfTheView_ThatDrivesTheSetup()
    {
        var t = Translate(Copy());
        Assert.True(t.Ok, string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.Equal(("IncPWave1", true), (t.Document!.PlaneWaves.Single().Name, t.Document.PlaneWaves.Single().Enabled));
        var w = OnlySetup(t.Document!).PlaneWave!;
        Assert.Equal((new CircuitRF.Engine.Em3d.Point3(0, 0, 1), new CircuitRF.Engine.Em3d.Point3(1, 0, 0)), (w.Direction, w.EField));
        Assert.Equal((5.0, 5.0), (w.ThetaStepDeg, w.PhiStepDeg));
        Assert.Contains(t.Notes, n => n.Contains("lists 'Ball' as its objects"));
    }

    /// <summary>R-hzn12-5 — what is not translated is refused as its own item: the spherical form, a wave beside a port, a sphere that
    /// does not cover the whole sphere, and a vector built from a variable.</summary>
    [Theory]
    [InlineData("\"VectorInput\":\"Cartesian\"", "\"VectorInput\":\"Spherical\"", "Incident Plane Wave with VectorInput 'Spherical'")]
    [InlineData("\"ThetaStop\":\"180deg\"", "\"ThetaStop\":\"90deg\"", "Infinite Sphere that does not cover the full sphere")]
    [InlineData("\"KVector\":\"0,0,1\"", "\"KVector\":\"0,0,R\"", "Incident Plane Wave KVector built from named variables")]
    public void WhatIsNotTranslated_IsRefusedAsItsOwnItem(string from, string to, string item)
    {
        var t = Translate(Copy(text => text.Replace(from.Replace(":", ": "), to.Replace(":", ": "))));
        Assert.False(t.Ok);
        Assert.Contains(t.Refusals, r => r.What == item);
    }

    /// <summary>R-hzn12-5 — a plane wave beside a port is refused (circuitRF runs one with no port).</summary>
    [Fact]
    public void APlaneWaveBesideAPort_IsRefused()
    {
        string path = Copy(text =>
        {
            var d = JsonNode.Parse(text)!;
            var design = d["designs"]![0]!;
            design["objects"]!.AsArray().Add(JsonNode.Parse(
                "{\"name\":\"Feed\",\"history\":[{\"kind\":\"CreateRectangle\",\"props\":{\"Position\":\"40,0,0\",\"Axis\":\"Z\",\"XSize\":\"1\",\"YSize\":\"1\"},\"suppressed\":false}]," +
                "\"material\":\"vacuum\",\"solve_inside\":true,\"model\":true,\"color\":\"#808080\",\"transparency\":0.0,\"wireframe\":false,\"visible\":true,\"consumed_by\":null,\"group\":\"\",\"orientation\":\"Global\"}"));
            design["excitations"]!.AsArray().Add(JsonNode.Parse(
                "{\"name\":\"1\",\"kind\":\"Lumped Port\",\"props\":{\"PortNumber\":\"1\",\"Impedance\":\"50ohm\"},\"faces\":[],\"objects\":[\"Feed\"],\"edges\":[],\"enabled\":true}"));
            return d.ToJsonString();
        });
        Assert.Contains(Translate(path).Refusals, r => r.What == "Incident Plane Wave beside a port");
    }

    /// <summary>A disabled wave stays a disabled wave of the view, and drives nothing; two enabled are refused as their own item.</summary>
    [Fact]
    public void SeveralWaves_TranslateEach_WithOnlyOneEnabled()
    {
        string Waves(bool firstEnabled, bool secondEnabled) => Copy(text =>
        {
            var d = JsonNode.Parse(text)!;
            var x = d["designs"]![0]!["excitations"]!.AsArray();
            var second = JsonNode.Parse(x[0]!.ToJsonString())!;
            second["name"] = "Side";
            second["props"]!["KVector"] = "1,0,0";
            second["props"]!["E0Vector"] = "0,0,1";
            second["enabled"] = secondEnabled;
            x[0]!["enabled"] = firstEnabled;
            x.Add(second);
            return d.ToJsonString();
        });
        var t = Translate(Waves(false, true));
        Assert.True(t.Ok, string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.Equal([("IncPWave1", false), ("Side", true)], t.Document!.PlaneWaves.Select(w => (w.Name, w.Enabled)));
        Assert.Equal("Side", OnlySetup(t.Document).PlaneWave!.Name);

        Assert.Contains(Translate(Waves(true, true)).Refusals, r => r.What == "several enabled Incident Plane Waves");
    }

    /// <summary>R-hzn12-5 (HZN-5) — editing the wave's direction and polarisation saves as KVector and E0Vector, and nothing else of the
    /// file changes.</summary>
    [Fact]
    public void AnEditedPlaneWave_SavesAsItsKeysAlone()
    {
        string path = Copy();
        var original = File.ReadAllLines(path);
        var source = new HznDesignSource(path, "Ball", new TechnologyCache());
        var doc = source.Load();
        doc.PlaneWaves[0].Direction = [1, 0, 0];
        doc.PlaneWaves[0].EField = [0, 0, 1];
        Assert.Null(source.Save(doc));

        var now = File.ReadAllLines(path);
        Assert.Equal(original.Length, now.Length);
        var changed = original.Zip(now).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();
        Assert.Equal(["\"E0Vector\": \"0,0,1\",", "\"KVector\": \"1,0,0\","], changed.Order().ToList());
    }

    /// <summary>Renaming a wave and disabling it saves as its name and enabled flag alone; a wave added in circuitRF is appended as a
    /// Hertzon plane wave of its own and reads back.</summary>
    [Fact]
    public void ARenamedDisabledWave_SavesAsItsNameAndFlag_AndAnAddedOneIsAppended()
    {
        string path = Copy();
        var original = File.ReadAllLines(path);
        var source = new HznDesignSource(path, "Ball", new TechnologyCache());
        var doc = source.Load();
        doc.PlaneWaves[0].Name = "Nose on";
        doc.PlaneWaves[0].Enabled = false;
        Assert.Null(source.Save(doc));
        var now = File.ReadAllLines(path);
        Assert.Equal(original.Length, now.Length);
        var changed = original.Zip(now).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();
        Assert.Equal(["\"enabled\": false", "\"name\": \"Nose on\","], changed.Order().ToList());

        doc = new HznDesignSource(path, "Ball", new TechnologyCache()).Load();
        doc.PlaneWaves.Add(new C3dPlaneWave { Name = "Side", Direction = [1, 0, 0], EField = [0, 0, 1] });
        Assert.Null(new HznDesignSource(path, "Ball", new TechnologyCache()) is var again && again.Load() is not null ? again.Save(doc) : "");
        var back = Translate(path);
        Assert.True(back.Ok, string.Join("; ", back.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.Equal([("Nose on", false), ("Side", true)], back.Document!.PlaneWaves.Select(w => (w.Name, w.Enabled)));
    }

    /// <summary>R-hzn12-5 (HZN-4) — a native scattering setup is written as Hertzon writes one (the Cartesian form, an Infinite Sphere at
    /// its steps), and reads back as the same wave.</summary>
    [Fact]
    public void ANativePlaneWave_IsWrittenAsHertzonWritesOne_AndReadsBack()
    {
        var (_, path) = WriteNative();
        var design = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json);
        var x = design.Excitations.Single();
        Assert.Equal(("Incident Plane Wave", "Cartesian", "0,0,1", "1,0,0"), (x.Kind, x.Props["VectorInput"], x.Props["KVector"], x.Props["E0Vector"]));
        var back = Translate(path, "Native");
        Assert.True(back.Ok, string.Join("; ", back.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.Equal((5.0, 5.0), (OnlySetup(back.Document!).PlaneWave!.ThetaStepDeg, OnlySetup(back.Document!).PlaneWave!.PhiStepDeg));
    }

    /// <summary>Gate 2 — Hertzon's own SphereRCS with its radius changed, saved, and a native scattering design exported, both read by
    /// <c>hertzon-cli describe</c>. Skipped without an install.</summary>
    [HertzonHomeFact]
    public void Hertzon_Describes_AnEditedSphereRCS_AndAnExportedScatteringDesign()
    {
        string path = Path.Combine(_root, "SphereRCS.hzn");
        File.Copy(Path.Combine(HertzonHomeFactAttribute.Home!, "examples", "SphereRCS.hzn"), path);
        var source = new HznDesignSource(path, "PECSphere", new TechnologyCache());
        var doc = source.Load();
        var r = doc.Variables.Single(v => v.Name == "R");
        r.Expression = "60mm";
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Null(source.Save(doc));
        Assert.Contains("\"R\": \"60mm\"", File.ReadAllText(path));
        Assert.Equal("PECSphere", HznSheetBooleanTests.Describe(path).GetProperty("name").GetString());

        var (_, native) = WriteNative();
        Assert.Equal("Native", HznSheetBooleanTests.Describe(native).GetProperty("name").GetString());
    }

    /// <summary>brief-hzn-12 Part 2 — a faceted sphere is the hull of its golden-spiral lattice: closed, planar, 2N − 4 triangles, and
    /// the volume Hertzon's own geometry listing reports for its 25 cm, 800-point sphere (64,977,000 mm³, five figures).</summary>
    [Fact]
    public void AFacetedSphere_IsTheHullOfItsLattice_WithHertzonsVolume()
    {
        var poly = C3dFacetedSphere.Metres(default, 0.25, 800);
        Assert.Empty(poly.Problems("ball"));
        Assert.Equal(2 * 800 - 4, poly.Faces.Count);
        Assert.Equal(64_977_000, poly.SignedVolume() * 1e9, 64_977_000 * 1e-4);
    }

    /// <summary>R-hzn12 Part 2 (HZN-2/4) — NumberOfSegments above 0 is a faceted sphere on NumberOfVertices points; it is written back as
    /// one and reads back the same; a point count out of range is refused.</summary>
    [Fact]
    public void AFacetedSphere_Translates_IsWrittenBack_AndAnOutOfRangeCountIsRefused()
    {
        string Faceted(string vertices) => Copy(text => text.Replace("\"NumberOfSegments\": \"0\"",
                                                                     $"\"NumberOfSegments\": \"100\", \"NumberOfVertices\": \"{vertices}\""));
        var t = Translate(Faceted("800"));
        Assert.True(t.Ok, string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.Equal(800, ((C3dSphere)t.Document!.Objects.Single(o => o.Name == "Ball")).FacetVertices);

        var source = new HznDesignSource(Faceted("800"), "Ball", new TechnologyCache());
        var doc = source.Load();
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Native", Technology = source.ProjectTechnology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Native", written.Refusals).Render());
        string path = Path.Combine(_root, "faceted.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        Assert.Equal(800, ((C3dSphere)Translate(path, "Native").Document!.Objects.Single(o => o.Name == "Ball")).FacetVertices);

        Assert.Contains(Translate(Faceted("3")).Refusals, r => r.What == "CreateSphere faceted");
    }

    /// <summary>The fixture's sphere, written as a new Hertzon project.</summary>
    private (C3dToHznResult Written, string Path) WriteNative()
    {
        var source = new HznDesignSource(Copy(), "Ball", new TechnologyCache());
        var doc = source.Load();
        var tech = source.ProjectTechnology;
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Native", Technology = tech });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Native", written.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        var outcome = HznExport.ToNewFile(written, path);
        Assert.True(outcome.Ok, outcome.Error?.Render());
        return (written, path);
    }
}
