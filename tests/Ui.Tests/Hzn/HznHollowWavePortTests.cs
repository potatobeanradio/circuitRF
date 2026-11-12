using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-11 R-hzn11-5 — Hertzon's Wave Port without renormalisation: translated to a <c>.c3d</c> wave port with <c>Z0: "mode"</c>,
/// saved back as <c>Renormalize</c> alone, and written as Hertzon writes one. The fixture (<c>guide-post.hzn</c>) is a hollow
/// rectangular guide with an off-centre post, written for these tests: the guide is a vacuum box with no region, so its outer faces
/// are perfect conductors, and a one-mode wave port covers each end.
/// </summary>
public sealed class HznHollowWavePortTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn11_" + Guid.NewGuid().ToString("N")[..8]);

    public HznHollowWavePortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixtures => FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures");

    private string Copy(string fixture = "guide-post.hzn")
    {
        string path = Path.Combine(_root, fixture);
        File.Copy(Path.Combine(Fixtures, fixture), path, overwrite: true);
        return path;
    }

    private static C3dTranslation Translate(string path, string design = "Guide")
        => HznToC3d.Translate(HznFile.Read(path).File!.Project, design);

    /// <summary>R-hzn11-5 (HZN-2) — Renormalize False with one mode is Z0 "mode"; the RenormImpedance it does not read is listed as
    /// unused, and nothing is refused.</summary>
    [Fact]
    public void AWavePortWithoutRenormalisation_TranslatesToAPortNormalisedToItsMode()
    {
        var t = Translate(Path.Combine(Fixtures, "guide-post.hzn"));
        Assert.True(t.Ok, string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.All(t.Document!.Ports, p => Assert.Equal((Em3dPortKind.Wave, C3dPorts.ModeZ0), (p.Kind, p.Z0)));
        Assert.Contains(t.Provenance!.Unused, u => u.Owner == "port 1" && u.Key == "RenormImpedance" && u.Value == "50ohm");
    }

    /// <summary>A hole cut from the guide that bounds a closed design is outside Hertzon's solved space, whose walls are perfect
    /// conductors: the post is kept as a perfect-conductor solid, and a Finite Conductivity boundary on it makes it that metal (the
    /// copper-boundary WR90 post design).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHoleInTheGuideOfAClosedDesign_IsAConductorSolid_DressedByItsBoundary(bool finite)
    {
        string path = Copy();
        if (finite)
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"boundaries\": []",
                "\"boundaries\": [{\"name\": \"FC\", \"kind\": \"Finite Conductivity\", \"props\": {\"UseMaterial\": \"False\", " +
                "\"Conductivity\": \"58000000\", \"TwoSided\": \"False\"}, \"faces\": [], \"objects\": [\"Rod\"], \"edges\": [], \"enabled\": true}]"));
        var t = Translate(path);
        Assert.True(t.Ok, string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why)));
        var tube = Assert.IsType<C3dBoolean>(t.Document!.Objects.Single(o => o.Name == "Tube"));
        Assert.True(tube.KeepTools);
        var rod = tube.Tools.Single();
        var material = t.Technology!.ResolvedMaterials.Single(m => m.Name == rod.Material);
        if (finite) Assert.Equal(5.8e7, material.Sigma20);
        else Assert.Equal(Em3dRole.Conductor, rod.Role);
    }

    /// <summary>R-hzn11-5 — the existing refusals stay: a mode type other than Auto is an item naming it.</summary>
    [Fact]
    public void AModeTypeOtherThanAuto_StaysAnItem()
    {
        string path = Copy();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"ModeType\": \"Auto\"", "\"ModeType\": \"TE\""));
        var t = Translate(path);
        Assert.Contains(t.Refusals, r => r.What == "Wave Port with mode type TE");
    }

    /// <summary>R-hzn11-5 (HZN-5) — a port changed from "mode" to 50 Ω saves as Renormalize True and nothing else; changed back, the
    /// file is the bytes it was.</summary>
    [Fact]
    public void ChangingAPortBetweenModeAndAnImpedance_SavesAsRenormalize_AndNothingElse()
    {
        string path = Copy();
        byte[] original = File.ReadAllBytes(path);
        var source = new HznDesignSource(path, "Guide", new TechnologyCache());
        var doc = source.Load();
        doc.Ports.Single(p => p.Number == 1).Z0 = "50";
        Assert.Null(source.Save(doc));
        var changed = Assert.Single(System.Text.Encoding.UTF8.GetString(original).Split('\n').Zip(File.ReadAllText(path).Split('\n')),
                                    p => p.First != p.Second);
        Assert.Equal(("\"Renormalize\": \"False\",", "\"Renormalize\": \"True\","), (changed.First.Trim(), changed.Second.Trim()));

        doc.Ports.Single(p => p.Number == 1).Z0 = C3dPorts.ModeZ0;
        Assert.Null(source.Save(doc));
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
    }

    /// <summary>R-hzn11-5 (HZN-4) — a native port normalised to its mode is written as Hertzon writes one: Renormalize False,
    /// RenormImpedance kept at Hertzon's 50 ohm, ImpedanceDefinition Zpv; and it reads back as Z0 "mode".</summary>
    [Fact]
    public void ANativeModePort_IsWrittenAsHertzonWritesOne_AndReadsBack()
    {
        var (_, path) = WriteNative();
        var props = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json).Excitations.First().Props;
        Assert.Equal(("False", "50ohm", "Zpv"), (props["Renormalize"], props["RenormImpedance"], props["ImpedanceDefinition"]));
        var back = Translate(path, "Native");
        Assert.True(back.Ok, string.Join("; ", back.Refusals.Select(r => r.What + ": " + r.Why)));
        Assert.All(back.Document!.Ports, p => Assert.Equal(C3dPorts.ModeZ0, p.Z0));
    }

    /// <summary>Gate 2 — Hertzon's own WaveguidePost with its post moved, saved, and the native hollow guide exported, both read by
    /// <c>hertzon-cli describe</c>. Skipped without an install.</summary>
    [HertzonHomeFact]
    public void Hertzon_Describes_AMovedPost_AndAnExportedHollowGuide()
    {
        string path = Path.Combine(_root, "WaveguidePost.hzn");
        File.Copy(Path.Combine(HertzonHomeFactAttribute.Home!, "examples", "WaveguidePost.hzn"), path);
        var source = new HznDesignSource(path, "WR90_Post", new TechnologyCache());
        var doc = source.Load();
        var post = (C3dCylinder)((C3dBoolean)doc.Objects.First(o => o.Name == "Waveguide")).Tools.Single();
        var spec = C3dBindings.SpecOf(typeof(C3dCylinder), nameof(C3dCylinder.Base))!;
        C3dBindings.SetExpr(post, spec, 0, new C3dExpr("a/3", "mm"));
        C3dBindings.SetNumber(post, spec, 0, 7_620_000);
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Null(source.Save(doc));
        Assert.Contains("\"CenterPosition\": \"a/3,L/2,0", File.ReadAllText(path));
        Assert.Equal(2, HznSheetBooleanTests.Describe(path).GetProperty("ports").GetArrayLength());

        var (_, native) = WriteNative();
        Assert.Equal(2, HznSheetBooleanTests.Describe(native).GetProperty("ports").GetArrayLength());
    }

    /// <summary>The WR90 section of <c>wr90-empty.c3d</c>, written as a new Hertzon project.</summary>
    private (C3dToHznResult Written, string Path) WriteNative()
    {
        var doc = C3dPersistence.LoadFromFile(Path.Combine(FixturePaths.Require("tests/Ui.Tests/ThreeD/Fixtures"), "wr90-empty.c3d"));
        var tech = new Technology { Name = "t", Materials = [new TechMaterial { Name = "Air", Epsr = 1 }] };
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Native", Technology = tech });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Native", written.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        var outcome = HznExport.ToNewFile(written, path);
        Assert.True(outcome.Ok, outcome.Error?.Render());
        return (written, path);
    }
}
