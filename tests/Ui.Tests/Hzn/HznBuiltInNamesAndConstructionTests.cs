using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-15 Part 0 — three things that kept a scattering design shut: a design variable named after one of circuitRF's built-in
/// functions (<c>frequency</c>), a field-plot plane that is not in the model reaching past the box that carries the radiation boundary,
/// and an open polyline. The fixture (<c>plane-wave-box.hzn</c>) was written for these tests by a script: a perfectly conducting ball in a
/// vacuum box sized from <c>frequency</c>, a non-model plot plane 0.5 um wider than the box, and a non-model plot line.
/// </summary>
public sealed class HznBuiltInNamesAndConstructionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn15p0_" + Guid.NewGuid().ToString("N")[..8]);

    public HznBuiltInNamesAndConstructionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "plane-wave-box.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Scatter");
    }

    [Fact]
    public void AVariableNamedAfterABuiltIn_OpensRenamed_AndEveryReferenceFollows()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var doc = t.Document!;
        Assert.Contains(doc.Variables, v => v.Name == "frequency_hzn" && v.Expression == "3GHz");
        Assert.Equal("299792458/frequency_hzn", doc.Variables.Single(v => v.Name == "lamb0").Expression);
        Assert.DoesNotContain(doc.Variables, v => v.Name == "frequency");
        Assert.Contains(t.Notes, n => n.Contains("'frequency' is 'frequency_hzn'"));
    }

    [Fact]
    public void ARenamedVariable_SavesBackUnderItsOwnName()
    {
        string path = Path.Combine(_root, "save.hzn");
        File.Copy(Fixture, path);
        var source = new HznDesignSource(path, "Scatter", new TechnologyCache());
        var doc = source.Load();
        // The same value spelt differently, so the box (whose faces carry the boundary) keeps its size; and a new variable naming it.
        doc.Variables.Single(v => v.Name == "frequency_hzn").Expression = "1.5GHz*2";
        doc.Variables.Add(new C3dVariable { Name = "f2", Expression = "2*frequency_hzn" });
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);

        string? refused = source.Save(doc);
        Assert.True(refused is null, refused);

        var vars = HznFile.Read(path).File!.Project.Designs[0].Variables;
        Assert.Equal("1.5GHz*2", vars["frequency"]);
        Assert.Equal("2*frequency", vars["f2"]);
        Assert.False(vars.Contains("frequency_hzn"));
        Assert.Equal("c0/frequency", vars["lamb0"]);
    }

    [Fact]
    public void ANonModelPlanePastTheBox_DoesNotMoveTheOuterExtent()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.Empty(t.Refusals);
        Assert.False(t.Document!.Objects.Single(o => o.Name == "PlotPlane").Model);
    }

    [Fact]
    public void AnOpenPolyline_IsAConstructionPolyline_KeepingItsExpressions()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var line = Assert.IsType<C3dPolyline>(t.Document!.Objects.Single(o => o.Name == "PlotLine"));
        Assert.False(line.Closed);
        Assert.Equal(2, line.VertexCount);
        Assert.Null(line.Material);
        var spec = C3dBindings.SpecOf(typeof(C3dPolyline), nameof(C3dPolyline.Points3))!;
        Assert.Equal("lamb0", C3dBindings.GetExpr(line, spec.ElementAt(1), 2)!.Expr);
    }
}
