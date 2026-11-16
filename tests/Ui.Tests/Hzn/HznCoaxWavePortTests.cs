using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-19 — what lies outside a closed design, and a wave port on a coaxial line's end cap. The fixture (<c>coax-line.hzn</c>,
/// written for these tests by a script) is a closed design (Auto Open off, no region): a Teflon cylinder (radius rb = 1.675 mm,
/// length len = 20 mm) that subtracts a perfect-conductor pin (ra = 0.5 mm) keeping it, and a disc wave port of radius rb on each
/// end, normalised to its mode.
/// </summary>
public sealed class HznCoaxWavePortTests : IDisposable
{
    private const long Mm = 1_000_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn19_" + Guid.NewGuid().ToString("N")[..8]);

    public HznCoaxWavePortTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "coax-line.hzn");

    private static C3dTranslation Translate(string path, string design = "Line")
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, design);
    }

    private string Edited(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        edit(json["designs"]![0]!.AsObject());
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".hzn");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    [Fact]
    public void AClosedDesignWhoseSolidsDoNotFillTheirBox_HasThePerfectConductorOutsideThemAsAHiddenSolid()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var outside = Assert.IsType<C3dBoolean>(t.Document!.Objects.Single(o => o.Name == "hzn_outside"));
        Assert.True(outside.Hidden);
        Assert.Equal(C3dBooleanOp.Subtract, outside.Op);
        var box = Assert.IsType<C3dBox>(outside.Blank);
        Assert.Equal((new C3dPoint3(-1_675_000, -1_675_000, 0), new C3dPoint3(3_350_000, 3_350_000, 20 * Mm)), (box.Min, box.Size));
        Assert.Equal(Em3dRole.Conductor, box.Role);
        Assert.Null(t.Technology!.FindMaterial(box.Material!)!.Sigma20);      // a perfect conductor states no conductivity
        // the cutting copy of the line draws its pin once: a Subtract keeping its tools cuts as their union
        Assert.DoesNotContain(C3dOperands.SelfAndDescendants(outside).OfType<C3dBoolean>(), b => b.KeepTools);
    }

    [Fact]
    public void SolidsThatTileTheirBox_AddNothing()
    {
        var t = Translate(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "guide-post.hzn"), "Guide");
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.DoesNotContain(t.Document!.Objects, o => o.Name == "hzn_outside");
    }

    [Fact]
    public void ADiscWavePortOnTheLinesEndCap_IsItsSquare_AndResolvesAsTheLineBetweenPinAndOutside()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.All(t.Document!.Ports, p => Assert.Equal((Em3dPortKind.Wave, -1_675_000L, 3_350_000L, C3dPorts.ModeZ0),
                                                       (p.Kind, p.Rect.Min.U, p.Rect.Size.U, p.Z0)));
        Assert.Equal([0L, 20 * Mm], t.Document.Ports.Select(p => p.Offset).Order().ToList());

        var (plan, refusal) = HznEmergeRun.Plan(Fixture, "Line", null);
        Assert.True(plan is not null, refusal?.Render());
        var made = C3dProblemAssembly.Assemble(plan.Setup, plan.Document, plan.DocumentPath, null, HznEmergeRun.Elaborator(plan));
        Assert.True(made.Ok, made.Refusal);
        Assert.All(made.Problem!.Ports, p =>
        {
            Assert.False(p.Hollow);
            Assert.Equal(("Pin", "hzn_outside"), (p.PositiveObject, p.NegativeObject));    // the pin against the conductor round it
        });
        Assert.Empty(made.Problem.Validate());
    }

    [Fact]
    public void ADiscThatIsNoEndCap_AndALumpedPortOnADisc_AreRefused()
    {
        string shifted = Edited(d => d["objects"]!.AsArray().Single(o => (string?)o!["name"] == "PortA")!["history"]![0]!["props"]!["Radius"] = "ra");
        Assert.Contains(Translate(shifted).Refusals, r => r.What == "Wave Port on 'PortA'" && r.Why.Contains("end cap of a cylinder"));

        string lumped = Edited(d =>
        {
            var e = d["excitations"]!.AsArray()[0]!.AsObject();
            e["kind"] = "Lumped Port";
            e["props"] = new JsonObject { ["PortNumber"] = "1", ["Impedance"] = "50ohm" };
        });
        Assert.Contains(Translate(lumped).Refusals, r => r.What == "Lumped Port on 'PortA'" && r.Why.Contains("circle"));
    }

    /// <summary>Hertzon's own Coax example, under <c>CIRCUITRF_HERTZON_HOME</c>.</summary>
    [HertzonHomeFact]
    public void HertzonsCoaxExample_Translates()
    {
        string? path = Directory.EnumerateFiles(HertzonHomeFactAttribute.Home!, "Coax.hzn", SearchOption.AllDirectories).FirstOrDefault();
        if (path is null) return;
        var t = Translate(path, "Coax50");
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.Contains(t.Document!.Objects, o => o.Name == "hzn_outside");
    }
}
