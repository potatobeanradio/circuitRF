using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-13 R-hzn13-3 — Hertzon polylines built from named dimensions, and unswept circles and ellipses: translated with their
/// expressions kept, saved back one property at a time, and written as Hertzon writes them. The fixture (<c>outline-shapes.hzn</c>) was
/// written for these tests: a ground plane minus a round anti-pad, a trace whose outline is named dimensions, and an elliptical pad.
/// </summary>
public sealed class HznOutlineAndEllipseTests : IDisposable
{
    private const long Mm = 1_000_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn13_" + Guid.NewGuid().ToString("N")[..8]);

    public HznOutlineAndEllipseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "outline-shapes.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Shapes");
    }

    [Fact]
    public void APolylineOfNamedDimensions_KeepsThemInItsOutline_AndItsPlaneCoordinateAsTheOffset()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var trace = Assert.IsType<C3dSheet>(t.Document!.Objects.Single(o => o.Name == "Trace"));
        var spec = C3dBindings.SpecOf(typeof(C3dSheet), nameof(C3dSheet.Outline))!;
        Assert.Equal(5, trace.Outline.Count);
        Assert.Contains("L", C3dBindings.GetExpr(trace, spec.ElementAt(0), 0)!.Expr);
        Assert.Contains("w", C3dBindings.GetExpr(trace, spec.ElementAt(0), 1)!.Expr);
        Assert.Equal("h", C3dBindings.GetExpr(trace, nameof(C3dSheet.Offset), 0)!.Expr);
        Assert.Equal(new C3dPoint2(-5 * Mm, 1_500_000), trace.Outline[0]);   // -L/2+c, w/2 resolved
    }

    [Fact]
    public void AnUnsweptCircleAndEllipse_AreEllipseSheets_WithTheirExpressions()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var ground = Assert.IsType<C3dBoolean>(t.Document!.Objects.Single(o => o.Name == "Ground"));
        var antipad = Assert.IsType<C3dSheet>(ground.Tools.Single());
        var radii = C3dBindings.SpecOf(typeof(C3dEllipse), nameof(C3dEllipse.Radii))!;
        Assert.Equal(["ra", "ra"], [C3dBindings.GetExpr(antipad.Ellipse!, radii, 0)!.Expr, C3dBindings.GetExpr(antipad.Ellipse!, radii, 1)!.Expr]);
        var oval = Assert.IsType<C3dSheet>(t.Document.Objects.Single(o => o.Name == "Oval"));
        Assert.Equal(new C3dPoint2(2 * Mm, Mm), oval.Ellipse!.Radii);   // major along x, minor R × Ratio
        Assert.Single(C3dSheetBooleans.Result(ground).Pieces[0].Holes);
    }

    [Fact]
    public void AnEllipseOffAxisZ_IsRefusedByName()
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        var oval = json["designs"]![0]!["objects"]!.AsArray().Select(o => o!.AsObject()).First(o => (string?)o["name"] == "Oval");
        oval["history"]![0]!["props"]!["Axis"] = "X";
        string path = Path.Combine(_root, "x.hzn");
        File.WriteAllText(path, json.ToJsonString());
        var t = Translate(path);
        Assert.False(t.Ok);
        Assert.Contains(t.Refusals, r => r.What == "CreateEllipse off Axis Z");
    }

    [Fact]
    public void AnEditedOutlinePoint_RewritesOnlyThePolylinesPoints()
    {
        string path = Path.Combine(_root, "edit.hzn");
        File.Copy(Fixture, path);
        string[] before = File.ReadAllText(path).Split('\n');
        var source = new HznDesignSource(path, "Shapes", new TechnologyCache());
        var doc = source.Load();
        var trace = (C3dSheet)doc.Objects.Single(o => o.Name == "Trace");
        var spec = C3dBindings.SpecOf(typeof(C3dSheet), nameof(C3dSheet.Outline))!;
        C3dBindings.SetExpr(trace, spec.ElementAt(1), 0, new C3dExpr("L/2+c", "mm"));
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);

        Assert.Null(source.Save(doc));

        string[] after = File.ReadAllText(path).Split('\n');
        var changed = Assert.Single(before.Zip(after), p => p.First != p.Second);
        Assert.Contains("\"Points\":", changed.Second);
        Assert.Contains("L/2+c,w/2,h;", changed.Second);
    }

    [Fact]
    public void NativeEllipses_AreWrittenAsCreateCircleAndCreateEllipse_AndTheRestRefused()
    {
        C3dSheet E(string name, C3dPlane plane, long ru, long rv, int? seg = null) => new()
        {
            Name = name, Material = "Copper", Plane = plane, Offset = 0,
            Ellipse = new C3dEllipse { Centre = new C3dPoint2(0, 0), Radii = new C3dPoint2(ru, rv), Segments = seg },
        };
        var tech = new Technology { Name = "t", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }] };
        C3dToHznResult Write(params C3dSheet[] sheets)
            => C3dToHzn.Write(new C3dDocument { Objects = [.. sheets] }, new HznExportOptions { DesignName = "N", Technology = tech });

        var ok = Write(E("disc", C3dPlane.YZ, Mm, Mm), E("oval", C3dPlane.XY, 2 * Mm, Mm));
        Assert.True(ok.Ok, HznExportDiagnostics.Refused("N", ok.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        HznExport.ToNewFile(ok, path);
        var design = HznFile.Read(path).File!.Project.Designs[0];
        var disc = design.Objects.First(o => o.Name == "disc").History.Single();
        Assert.Equal(("CreateCircle", "X"), (disc.Kind, disc.Props["Axis"]));
        var oval = design.Objects.First(o => o.Name == "oval").History.Single();
        Assert.Equal(("CreateEllipse", "0.5"), (oval.Kind, oval.Props["Ratio"]));

        Assert.Contains(Write(E("tall", C3dPlane.XY, Mm, 2 * Mm)).Refusals, r => r.What == "ellipse");
        Assert.Contains(Write(E("faceted", C3dPlane.XY, Mm, Mm, seg: 16)).Refusals, r => r.What == "ellipse with a segment count");
    }
}
