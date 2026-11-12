using CircuitRF.Core.Expressions;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-2 — a Hertzon 3D design as a <c>C3dDocument</c>: one test per mapping row, the refusal, the references found by
/// geometry, and the expression spellings. The fixtures are small hand-written <c>.hzn</c> files
/// (<c>tests/Ui.Tests/Hzn/Fixtures</c>); the expected values of the expression theory were measured with
/// <c>hertzon-cli describe</c> and are recorded in <c>src/Design/RESOLVED.md</c> §HZN.
/// </summary>
public sealed class HznToC3dTests
{
    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    private static C3dTranslation Translate(string file, string design, GeometryKernel? kernel = null)
    {
        var read = HznFile.Read(Fixture(file));
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, design, kernel);
    }

    private static C3dTranslation Mapping()
    {
        var t = Translate("mapping.hzn", "Mapping");
        Assert.True(t.Ok, t.Refusal?.Render());
        return t;
    }

    private static T Obj<T>(C3dTranslation t, string name) where T : C3dObject =>
        Assert.IsType<T>(t.Document!.Objects.Single(o => o.Name == name));

    private static string Expr(IC3dBindable o, string property, int k) =>
        C3dBindings.GetExpr(o, property, k)?.Expr ?? "(number)";

    // ── histories to the tree ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Box_NegativeSizeIsNormalised_AndExpressionsStayExpressions()
    {
        var t = Mapping();
        var boolean = Obj<C3dBoolean>(t, "Sub");
        var box = Assert.IsType<C3dBox>(boolean.Blank);
        // ZSize was -h at z = 0: the box is Min = -h, Size = h, and nothing was turned into a number.
        Assert.Equal("-h", Expr(box, "Min", 2));
        Assert.Equal("h", Expr(box, "Size", 2));
        Assert.Equal("2*w", Expr(box, "Size", 0));
        Assert.Equal("L", Expr(box, "Size", 1));
        Assert.Equal(1500000, box.Size.Z);
    }

    [Fact]
    public void Subtract_BecomesABooleanWhoseToolKeepsItsName_AndIsNotATopLevelObject()
    {
        var t = Mapping();
        var boolean = Obj<C3dBoolean>(t, "Sub");
        Assert.Equal(C3dBooleanOp.Subtract, boolean.Op);
        Assert.Equal("lam", boolean.Blank!.Material);
        Assert.Equal("", boolean.Blank.Name);
        var pin = Assert.IsType<C3dCylinder>(Assert.Single(boolean.Tools));
        Assert.Equal("Pin", pin.Name);
        Assert.Equal("pec", pin.Material);
        Assert.DoesNotContain(t.Document!.Objects, o => o.Name == "Pin");
        Assert.Contains("Pin", t.Provenance.Objects["Sub"].Consumed);
        // Height -h from z = 0: the cylinder starts at -h and is h long.
        Assert.Equal("h", Expr(pin, "Length", 0));
    }

    [Fact]
    public void LiteralPolylineSweptAlongItsNormal_IsAPrism()
    {
        var wedge = Obj<C3dPrism>(Mapping(), "Wedge");
        Assert.Equal(C3dPlane.XY, wedge.Plane);
        Assert.Equal(3, wedge.Outline.Count);
        Assert.Equal(new C3dPoint2(4000000, 0), wedge.Outline[1]);
        Assert.Equal(1000000, wedge.Height);
    }

    [Fact]
    public void Sphere_AndTrueCylinder_AreTheirPrimitives()
    {
        var t = Mapping();
        var ball = Obj<C3dSphere>(t, "Ball");
        Assert.Equal(2000000, ball.Radius);
        Assert.Equal(10000000, ball.Centre.Z);
        Assert.IsType<C3dCylinder>(Assert.Single(Obj<C3dBoolean>(t, "Sub").Tools));
    }

    [Fact]
    public void RectangleWithPerfectE_IsAConductorSheet_OnItsAxisPlane()
    {
        var pad = Obj<C3dSheet>(Mapping(), "Pad");
        Assert.Equal(C3dPlane.XY, pad.Plane);
        Assert.Equal("h", Expr(pad, "Offset", 0));
        Assert.Equal("-w/2", Expr(pad.Rect!, "Min", 0));
        Assert.Equal("pec", pad.Material);
        Assert.Equal(Em3dRole.Conductor, pad.Role);
    }

    [Fact]
    public void RotateThenMove_IsAPlacement_AndASuppressedStepIsRecordedNotApplied()
    {
        var t = Mapping();
        var slab = Obj<C3dBox>(t, "Slab");
        var rot = Assert.Single(slab.Placement.Rotate);
        Assert.Equal((C3dAxis.Z, 90.0), (rot.Axis, rot.Deg));
        Assert.Equal(1000000, slab.Placement.Origin.X);

        var blocked = Obj<C3dBox>(t, "Blocked");
        Assert.True(blocked.Placement.IsDefault);
        var steps = t.Provenance.Objects["Blocked"].Steps;
        Assert.Equal(2, steps.Count);
        Assert.True(steps[1].Suppressed);
        Assert.False(steps[1].Applied);
    }

    // ── variables, materials, ports, setups ─────────────────────────────────────────────────────

    [Fact]
    public void Variables_DesignAndProject_HaveReversibleNames()
    {
        var t = Mapping();
        var vars = t.Document!.Variables;
        Assert.Equal("1.5mm", vars.Single(v => v.Name == "h").Expression);
        var project = vars.Single(v => v.Name == "hznp_f0");
        Assert.Equal("2.4GHz", project.Expression);
        Assert.Equal("hznp_f0/2", vars.Single(v => v.Name == "fl").Expression);
        var origin = t.Provenance.Variables.Single(v => v.C3dName == "hznp_f0");
        Assert.True(origin.Project);
        Assert.Equal("f0", origin.HznName);
        Assert.Equal("f0", HznNames.HznProjectName("hznp_f0"));
    }

    [Fact]
    public void ProjectMaterials_BecomeTheDocumentsOwnTechnology()
    {
        var t = Mapping();
        Assert.Null(t.Document!.TechRef);
        var tech = t.Technology!;
        var lam = tech.Materials.Single(m => m.Name == "lam");
        Assert.Equal(3.55, lam.Epsr);
        Assert.Equal(0.002, lam.TanD);
        // Named but not defined: from the table written here.
        Assert.Equal(5.8e7, tech.Materials.Single(m => m.Name == "copper").Sigma20);
        Assert.DoesNotContain(tech.Materials, m => m.Name == "vacuum");   // nothing in the document uses it
    }

    [Fact]
    public void LumpedPortOnASheet_IsAPortWithTheSheetsRectangle_AndTheSheetIsGone()
    {
        var t = Mapping();
        var port = Assert.Single(t.Document!.Ports);
        Assert.Equal((1, "P1", Em3dPortKind.Lumped, C3dPlane.XZ, "50"), (port.Number, port.Name, port.Kind, port.Plane, port.Z0));
        Assert.Equal("w", Expr(port.Rect, "Size", 0));
        Assert.Equal("h", Expr(port.Rect, "Size", 1));
        Assert.DoesNotContain(t.Document.Objects, o => o.Name == "Port1");
        Assert.Equal(new HznPortOrigin(1, "P1", "Port1"), Assert.Single(t.Provenance.Ports));
        // What circuitRF's run does not use is kept and reported, not refused.
        Assert.Contains(t.Provenance.Unused, u => u.Key == "IntegrationLine");
    }

    [Fact]
    public void SetupAndSweep_BecomeAnEmergeSetupThatTheSetupReaderAccepts()
    {
        var t = Mapping();
        var element = Assert.Single(t.Document!.Setups);
        var setup = EmSetupPersistence.FromEmbedded(element);
        Assert.Equal("Setup1", setup.Name);
        var freq = setup.Frequency;
        Assert.Equal(("1", "3", 21), (freq.StartExpr, freq.StopExpr, freq.NumPoints ?? 0));
        Assert.Equal(2, element.GetProperty("Emerge").GetProperty("ElementOrder").GetInt32());
        Assert.Equal(6, element.GetProperty("Emerge").GetProperty("AdaptiveMesh").GetInt32());
        Assert.True(element.GetProperty("Emerge").GetProperty("AdaptiveSweep").GetBoolean());
        Assert.Contains(t.Provenance.Unused, u => u.Owner == "setup Setup1" && u.Key == "MaximumDeltaS");
        Assert.Equal("Setup1/Sweep", t.Provenance.Setups["Setup1"]);
    }

    [Fact]
    public void ADesignThatTranslates_PassesTheDocumentsOwnValidators()
    {
        // The translation runs the 3D editor's validators itself; a document that came back holds no error.
        var t = Mapping();
        var findings = C3dValidation.Validate(t.Document!, isKnownMaterial: m => t.Technology!.FindMaterial(m) is not null);
        Assert.DoesNotContain(findings, f => f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error);
        Assert.True(C3dResolver.Resolve(t.Document!, C3dCell.None).Ok);
    }

    [Fact]
    public void TheFileModelsOwnFixture_BoxAndPort_Translates()
    {
        var t = Translate("box-and-lumped-port.hzn", "Block");
        Assert.True(t.Ok, t.Refusal?.Render());
        Assert.Single(t.Document!.Objects);
        Assert.Single(t.Document.Ports);
    }

    // ── the refusal ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnsupportedDesign_IsRefusedWithEveryItem_GroupedByKind_AndNothingOpens()
    {
        var t = Translate("unsupported.hzn", "Bad");
        Assert.False(t.Ok);
        Assert.Null(t.Document);
        var items = t.Refusals;
        bool Has(HznRefusalKind kind, string what, string user) =>
            items.Any(i => i.Kind == kind && i.What.Contains(what) && i.Users.Any(u => u.StartsWith(user)));

        Assert.True(Has(HznRefusalKind.CreationStep, "CreateTorus", "Torus1"));
        Assert.True(Has(HznRefusalKind.CreationStep, "CreateCylinder", "Facet"));
        Assert.True(Has(HznRefusalKind.Operation, "Fillet", "Rounded"));
        Assert.True(Has(HznRefusalKind.Operation, "Subtract on a sheet", "Plane"));
        Assert.True(Has(HznRefusalKind.Operation, "Rotate after Move", "Late"));
        Assert.True(Has(HznRefusalKind.Material, "mystery_ceramic", "Odd"));
        Assert.True(Has(HznRefusalKind.Expression, "round", "Odd"));
        Assert.True(Has(HznRefusalKind.Expression, "bare number", "Mixed"));
        Assert.True(Has(HznRefusalKind.Boundary, "Impedance", "Imp"));
        // Rad2's second-order radiation is circuitRF's own absorbing face on the EMerge run (EMerge's default), so it is not an item.
        Assert.False(Has(HznRefusalKind.Boundary, "second order", "Rad2"));
        Assert.True(Has(HznRefusalKind.Excitation, "Incident Plane Wave", "Wave"));

        string text = t.Refusal!.Render();
        Assert.True(text.IndexOf("Creation steps:", StringComparison.Ordinal) < text.IndexOf("Operations:", StringComparison.Ordinal));
        Assert.Equal("hzn.translate.refused", t.Refusal.Id);
    }

    [Fact]
    public void UnknownAndCircuitDesigns_AreRefusedByName()
    {
        var read = HznFile.Read(Fixture("circuit-and-3d.hzn")).File!;
        Assert.Equal("hzn.translate.no-design", HznToC3d.Translate(read.Project, "nope").Refusal!.Id);
        string circuit = read.Project.Designs.First(d => d.Kind == HznDesignKind.Circuit).Name!;
        Assert.Equal("hzn.translate.not-3d", HznToC3d.Translate(read.Project, circuit).Refusal!.Id);
    }

    // ── references by index, resolved by geometry ───────────────────────────────────────────────

    [Fact]
    public void ABoxFaceNamedByIndex_IsFoundByItsSignature_NotByItsIndex()
    {
        var t = Translate("faces.hzn", "Faces");
        Assert.True(t.Ok, t.Refusal?.Render());
        // RadA says face 1 (xmax in the conventional order) but its signature is the -x face; RadB says face 0 with +x.
        var air = Assert.Single(t.Document!.Setups).GetProperty("AirBox");
        Assert.Equal("Absorbing", air.GetProperty("XMin").GetProperty("Boundary").GetString());
        Assert.Equal("Absorbing", air.GetProperty("XMax").GetProperty("Boundary").GetString());
        Assert.Equal("Pec", air.GetProperty("YMin").GetProperty("Boundary").GetString());
        Assert.Contains(("RadA", "xmin"), t.Provenance.AirBoxFaces);
        Assert.Contains(("RadB", "xmax"), t.Provenance.AirBoxFaces);
    }

    private static readonly IReadOnlyList<HznFaceFrame> Cube =
    [
        new("xmin", [-1, 0, 0], [-1, 0, 0], 4), new("xmax", [1, 0, 0], [1, 0, 0], 4), new("ymax", [0, 1, 0], [0, 1, 0], 4),
    ];

    [Fact]
    public void ASignatureThatFitsNoFace_OrTwo_IsRefused()
    {
        Assert.Equal("xmax", HznFaceMatch.Match(Cube, [1, 0, 0, 1, 0, 0, 4, 1]).Name);
        Assert.Null(HznFaceMatch.Match(Cube, [0, 0, 5, 0, 0, 1, 4, 1]).Name);
        // Two faces both fit: refused, never the first.
        var twins = new List<HznFaceFrame>(Cube) { new("xmax#2", [1, 0, 0], [1, 0, 0], 4) };
        var ambiguous = HznFaceMatch.Match(twins, [1, 0, 0, 1, 0, 0, 4, 1]);
        Assert.Null(ambiguous.Name);
        Assert.Contains("more than one", ambiguous.Problem);
    }

    [Fact]
    public void AFaceOfABuiltShape_NeedsTheWorker_AndTheRefusalNamesIt()
    {
        using var absent = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => new GeometryKernelLocation(null, null, "", GeometryKernelAbsence.Missing, "The geometry kernel was not found.", "Reinstall."),
        });
        var t = Translate("faces.hzn", "Kernel", absent);
        Assert.False(t.Ok);
        var item = Assert.Single(t.Refusals, i => i.Kind == HznRefusalKind.FaceReference);
        Assert.Contains("geometry kernel", item.Why);
    }

    [Fact]
    public void AFaceOfABuiltShape_IsFoundByTheWorkerWhenItIsThere()
    {
        var kernel = GeometryKernel.Shared;
        if (!kernel.Capability.Available) return;   // skipped without the worker: the refusal above is the other half
        var t = Translate("faces.hzn", "Kernel", kernel);
        Assert.True(t.Ok, t.Refusal?.Render());
        var fb = Assert.Single(t.Document!.FaceBoundaries);
        Assert.Equal(("Plate", "zmax", Em3dFaceBoundaryKind.Pec), (fb.Object, fb.Face, fb.Kind));
    }

    // ── expressions, token by token ─────────────────────────────────────────────────────────────

    private static (HznExpr? Result, IReadOnlyList<HznRefusalItem> Items) Expression(string source, params string[] projectNames)
    {
        var names = new HznNames
        {
            Design = new Dictionary<string, string> { ["w"] = "3mm", ["M"] = "0.65" },
            Project = projectNames.ToDictionary(n => n, _ => "1GHz"),
        };
        var refusals = new HznRefusalBuilder();
        var result = new HznExpressionTranslator(names, refusals).Translate(source, "test");
        return (result, refusals.Items());
    }

    // Value (base SI) as `hertzon-cli describe` 0.3.19 reports it, and the same expression through circuitRF's engine.
    [Theory]
    [InlineData("1m", 1.0)]
    [InlineData("1mil", 2.54e-5)]
    [InlineData("1in", 0.0254)]
    [InlineData("5 mm", 0.005)]
    [InlineData("1.5e3mm", 1.5)]
    [InlineData("90deg", Math.PI / 2)]
    [InlineData("sqrt(2)*3mm", 0.004242640687119286)]
    [InlineData("log(100)", 2.0)]
    [InlineData("ln(10)", 2.302585092994046)]
    [InlineData("2**3", 8.0)]
    [InlineData("-2^2", -4.0)]
    [InlineData("2^-1", 0.5)]
    [InlineData("max(1mm,5um,3mm)", 0.003)]
    [InlineData("3mm/1mm", 3.0)]
    [InlineData("c0*1mm", 299792.458)]
    [InlineData("e0*1", 8.8541878128e-12)]
    [InlineData("sin(90deg)", 1.0)]
    public void HertzonSpellings_EvaluateToWhatHertzonReports(string source, double expected)
    {
        var (result, items) = Expression(source);
        Assert.NotNull(result);
        Assert.Empty(items);
        var value = new Evaluator().Eval(result!.Text, new Scope("t")).AsReal();
        Assert.Equal(expected, value, 12);
    }

    [Theory]
    [InlineData("1m", "1metre")]
    [InlineData("log(x)", "log10(x)")]
    [InlineData("2**w", "2^w")]
    [InlineData("a and b", "a && b")]
    [InlineData("w+2.5mm", "w+2.5mm")]
    public void OnlyTheTokensThatDifferChange(string source, string expected)
    {
        var names = new HznNames { Design = new Dictionary<string, string> { ["x"] = "1", ["w"] = "3mm", ["a"] = "1", ["b"] = "1" } };
        Assert.Equal(expected, new HznExpressionTranslator(names, new HznRefusalBuilder()).Translate(source, "t")!.Text);
    }

    [Theory]
    [InlineData("round(2.5)", "function round")]
    [InlineData("7 % 2", "operator %")]
    [InlineData("!(1)", "operator !")]
    [InlineData("1km", "unit km")]
    [InlineData("mystery(1)", "function mystery")]
    [InlineData("nothere*2", "identifier nothere")]
    [InlineData("w+1", "bare number with a unit")]
    public void WhatCircuitRFDoesNotHave_IsAnItemNamingIt(string source, string what)
    {
        var (result, items) = Expression(source);
        Assert.Null(result);
        Assert.Contains(items, i => i.Kind == HznRefusalKind.Expression && i.What == what);
    }

    [Fact]
    public void ProjectVariableNames_AreMappedToTheReservedPrefix_AndAZeroMayBesideAQuantity()
    {
        var (result, _) = Expression("f0/2 + 0", "f0");
        Assert.Equal("hznp_f0/2 + 0", result!.Text);
        Assert.Equal(["f0"], result.Refs);
        Assert.Equal(["a", "if(x,1,2)", "c"], HznExpressionTranslator.SplitTopLevel("a, if(x,1,2) ,c", ','));
    }
}
