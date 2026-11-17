using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-21 — half models: the <c>.c3d</c> Split, the air box's symmetry planes, the radiation pattern mirrored into the whole
/// structure's (the same function for EMerge, Palace and openEMS), and Hertzon's Split and Symmetry translated and written back. The
/// fixture (<c>half-dipole.hzn</c>) was written for these tests by a script: a dipole's arms cut by Split at x = 0, a Perfect H face
/// there and radiation on the other five. The runs against the full dipole on each solver are recorded in src/Design/RESOLVED.md (HZN-21).
/// </summary>
public sealed class HalfModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn21_" + Guid.NewGuid().ToString("N")[..8]);

    public HalfModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "half-dipole.hzn");

    private string Edited(Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        edit(json["designs"]![0]!.AsObject());
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".hzn");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static C3dTranslation Translate(string path) => HznToC3d.Translate(HznFile.Read(path).File!.Project, "Half");

    private static string Why(C3dTranslation t) => string.Join("; ", t.Refusals.Select(r => r.What + ": " + r.Why));

    private static JsonObject ArmTopSplit(JsonObject design) =>
        design["objects"]!.AsArray().Single(o => (string?)o!["name"] == "ArmTop")!["history"]![1]!["props"]!.AsObject();

    // ── translation (R-hzn21-4) ──────────────────────────────────────────────────────────────────

    /// <summary>A Split step is a .c3d Split of the cylinder at the YZ plane, keeping +x; the Symmetry face is the air box's xmin, a PMC
    /// wall flagged as a mirror plane; the other faces absorb.</summary>
    [Fact]
    public void SplitAndSymmetry_Translate()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, Why(t));
        var arm = Assert.IsType<C3dSplit>(t.Document!.Objects.Single(o => o.Name == "ArmTop"));
        Assert.Equal((C3dSplitPlane.YZ, C3dSplitKeep.Positive, 0L), (arm.Plane, arm.Keep, arm.At));
        Assert.IsType<C3dCylinder>(arm.Target);
        Assert.Equal("split:xmin", arm.CutFace);
        var box = C3dSetups.Read(t.Document).Single().Setup!.AirBox!;
        Assert.Equal((Em3dBoundaryKind.Pmc, true), (box.XMin!.Boundary, box.XMin.Symmetry));
        Assert.All(new[] { box.XMax, box.YMin, box.YMax, box.ZMin, box.ZMax }, f => Assert.Equal((Em3dBoundaryKind.Absorbing, false), (f!.Boundary, f.Symmetry)));
    }

    /// <summary>A Split in a Relative system whose axes are the global ones cuts through that system's origin: At is the origin's x, as the
    /// file spells it. A Custom plane, Keep Both and a turned system are each refused by name.</summary>
    [Fact]
    public void SplitInATranslatedSystem_TakesItsOrigin_AndTheRestAreRefused()
    {
        var shifted = Translate(Edited(d =>
        {
            d["coord_systems"] = new JsonArray(new JsonObject
            {
                ["name"] = "CS_Off", ["kind"] = "Relative", ["reference"] = "Global",
                ["props"] = new JsonObject { ["Mode"] = "Axis/Position", ["Origin"] = "r/2,0,0", ["XAxis"] = "1,0,0", ["YPoint"] = "0,1,0" },
            });
            ArmTopSplit(d)["CoordinateSystem"] = "CS_Off";
        }));
        Assert.True(shifted.Ok, Why(shifted));
        var arm = (C3dSplit)shifted.Document!.Objects.Single(o => o.Name == "ArmTop");
        Assert.Equal("r/2", C3dBindings.GetExpr(arm, nameof(C3dSplit.At), 0)!.Expr);

        var flat = Translate(Edited(d => { ArmTopSplit(d)["Plane"] = "Custom"; ArmTopSplit(d)["Normal"] = "0,0,0"; }));
        Assert.Contains(flat.Refusals, r => r.Why.Contains("is not three numbers, not all zero"));
        var both = Translate(Edited(d => ArmTopSplit(d)["Keep"] = "Both"));
        Assert.Contains(both.Refusals, r => r.What == "Split keeping 'Both'");
        var turned = Translate(Edited(d =>
        {
            d["coord_systems"] = new JsonArray(new JsonObject
            {
                ["name"] = "CS_Turn", ["kind"] = "Relative", ["reference"] = "Global",
                ["props"] = new JsonObject { ["Mode"] = "Axis/Position", ["Origin"] = "0,0,0", ["XAxis"] = "0,1,0", ["YPoint"] = "-1,0,0" },
            });
            ArmTopSplit(d)["CoordinateSystem"] = "CS_Turn";
        }));
        Assert.Contains(turned.Refusals, r => r.Why.Contains("turned from the global axes"));
    }

    /// <summary>A Custom plane is a .c3d Custom Split: Base as the file spells it (moved by a translated system's origin) and the Normal's
    /// three numbers; export writes it back as Hertzon's Custom Split, and the file reads back to the same plane.</summary>
    [Fact]
    public void ACustomPlane_Translates_AndRoundTrips()
    {
        var t = Translate(Edited(d =>
        {
            d["coord_systems"] = new JsonArray(new JsonObject
            {
                ["name"] = "CS_Off", ["kind"] = "Relative", ["reference"] = "Global",
                ["props"] = new JsonObject { ["Mode"] = "Axis/Position", ["Origin"] = "0,0,g", ["XAxis"] = "1,0,0", ["YPoint"] = "0,1,0" },
            });
            var split = ArmTopSplit(d);
            (split["Plane"], split["Base"], split["Normal"], split["CoordinateSystem"]) = ("Custom", "r/3,0,0", "2,0,0", "CS_Off");
        }));
        Assert.True(t.Ok, Why(t));
        var arm = (C3dSplit)t.Document!.Objects.Single(o => o.Name == "ArmTop");
        Assert.Equal(C3dSplitPlane.Custom, arm.Plane);
        Assert.Equal([2.0, 0.0, 0.0], arm.Normal!);
        Assert.Equal(("r/3", "g"), (C3dBindings.GetExpr(arm, nameof(C3dSplit.Base), 0)!.Expr, C3dBindings.GetExpr(arm, nameof(C3dSplit.Base), 2)!.Expr));

        var written = C3dToHzn.Write(t.Document, new HznExportOptions { DesignName = "Half", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Half", written.Refusals).Render());
        string path = Path.Combine(_root, "custom.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var step = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json).Objects.Single(o => o.Name == "ArmTop").History.Single(s => s.Kind == "Split");
        Assert.Equal(("Custom", "2,0,0", "Global"), (step.Props["Plane"], step.Props["Normal"], step.Props["CoordinateSystem"]));
        var back = (C3dSplit)Translate(path).Document!.Objects.Single(o => o.Name == "ArmTop");
        Assert.Equal((C3dSplitPlane.Custom, arm.Base), (back.Plane, back.Base));
    }

    /// <summary>A named plane off the origin is exported in a Relative coordinate system of its own, its origin on the plane as an
    /// expression, and reads back to the same At.</summary>
    [Fact]
    public void ANamedPlaneOffTheOrigin_IsExportedInItsOwnCoordinateSystem()
    {
        var t = Translate(Fixture);
        var arm = (C3dSplit)t.Document!.Objects.Single(o => o.Name == "ArmTop");
        C3dBindings.SetExpr(arm, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.At))!, 0, new C3dExpr("r/2", "mm"));
        arm.At = 750L * t.Document!.DbuPerMicron;
        var written = C3dToHzn.Write(t.Document, new HznExportOptions { DesignName = "Half", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Half", written.Refusals).Render());
        string path = Path.Combine(_root, "shifted.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var design = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json);
        var cs = Assert.Single(design.CoordSystems);
        Assert.Equal(("Relative", "Global", "r/2,0,0"), (cs.Kind, cs.Reference, cs.Props["Origin"]));
        Assert.Equal(cs.Name, design.Objects.Single(o => o.Name == "ArmTop").History.Single(s => s.Kind == "Split").Props["CoordinateSystem"]);
        var back = (C3dSplit)Translate(path).Document!.Objects.Single(o => o.Name == "ArmTop");
        Assert.Equal("r/2", C3dBindings.GetExpr(back, nameof(C3dSplit.At), 0)!.Expr);
    }

    /// <summary>Saving an edit of a Split's At back into the .hzn adds the coordinate system its step now names, and nothing else.</summary>
    [Fact]
    public void SavingAMovedSplit_AddsItsCoordinateSystem()
    {
        var file = HznFile.Read(Fixture).File!;
        var t = HznToC3d.Translate(file.Project, "Half");
        Assert.True(t.Ok, Why(t));
        var baseline = new HznSaveBaseline(t.Document!, t.Provenance!, t.Technology!);
        var edited = HznToC3d.Translate(file.Project, "Half").Document!;      // a second, independent reading: the document as edited
        var arm = (C3dSplit)edited!.Objects.Single(o => o.Name == "ArmTop");
        C3dBindings.SetExpr(arm, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.At))!, 0, new C3dExpr("r/2", "mm"));
        arm.At = 750L * edited.DbuPerMicron;
        var project = HznFile.Read(Fixture).File!.Project;
        var outcome = HznPatcher.Apply(project, "Half", baseline, edited);
        Assert.True(outcome.Ok, string.Join("; ", outcome.Refusals));
        var design = new HznDesign(project.Designs.Single().Json);
        var cs = Assert.Single(design.CoordSystems);
        Assert.Equal(cs.Name, design.Objects.Single(o => o.Name == "ArmTop").History.Single(s => s.Kind == "Split").Props["CoordinateSystem"]);
        Assert.Contains(outcome.Notes, n => n.Contains($"Coordinate system '{cs.Name}' was added"));
        Assert.Equal("Global", design.Objects.Single(o => o.Name == "ArmBottom").History.Single(s => s.Kind == "Split").Props["CoordinateSystem"]);
    }

    /// <summary>An Infinite Sphere turns the translated setups' radiation pattern on, with a note; export writes a whole-sphere Infinite
    /// Sphere for a setup that asks for the pattern, and that reads back as the pattern on.</summary>
    [Fact]
    public void AnInfiniteSphere_IsTheRadiationPattern_BothWays()
    {
        Assert.Null(C3dSetups.Read(Translate(Fixture).Document!).Single().Setup!.RadiationPattern ? "on" : null);
        var t = Translate(Edited(d => d["spheres"] = new JsonArray(new JsonObject
        {
            ["name"] = "FF", ["props"] = new JsonObject { ["CoordinateSystem"] = "Global", ["ThetaStart"] = "0deg", ["ThetaStop"] = "180deg",
                                                          ["ThetaStep"] = "5deg", ["PhiStart"] = "0deg", ["PhiStop"] = "360deg", ["PhiStep"] = "5deg" },
        })));
        Assert.True(t.Ok, Why(t));
        Assert.True(C3dSetups.Read(t.Document!).Single().Setup!.RadiationPattern);
        Assert.Contains(t.Notes, n => n.Contains("compute the radiation pattern"));

        var written = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = "Half", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Half", written.Refusals).Render());
        string path = Path.Combine(_root, "pattern.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var sphere = Assert.Single(HznFile.Read(path).File!.Project.Designs.Single().Json.GetArray("spheres")!.Items.OfType<HznJsonObject>());
        Assert.Equal("2deg", new HznStringMap(sphere, "props")["ThetaStep"]);
        Assert.True(C3dSetups.Read(Translate(path).Document!).Single().Setup!.RadiationPattern);
    }

    /// <summary>An impedance multiplier other than 1 is listed as unused, with a note: Hertzon's own solver does not apply it.</summary>
    [Fact]
    public void AnImpedanceMultiplier_IsUnused_AsInHertzon()
    {
        var t = Translate(Edited(d => d["boundaries"]![0]!["props"]!["ImpedanceMultiplier"] = "2"));
        Assert.True(t.Ok, Why(t));
        Assert.Contains(t.Provenance!.Unused, u => u.Key == "ImpedanceMultiplier" && u.Value == "2");
        Assert.Contains(t.Notes, n => n.Contains("Hertzon's own solver does not apply it"));
    }

    /// <summary>Export writes the Split step and the Perfect H Symmetry boundary, and the file reads back to the same Split and flag.</summary>
    [Fact]
    public void Export_WritesSplitAndSymmetry_AndReadsBack()
    {
        var t = Translate(Fixture);
        var written = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = "Half", Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Half", written.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var design = new HznDesign(HznFile.Read(path).File!.Project.Designs.Single().Json);
        var step = design.Objects.Single(o => o.Name == "ArmTop").History.Single(s => s.Kind == "Split");
        Assert.Equal(("YZ", "Positive"), (step.Props["Plane"], step.Props["Keep"]));
        Assert.Contains(design.Boundaries, b => b.Kind == "Symmetry" && b.Props["Type"] == "Perfect H");
        var back = Translate(path);
        Assert.True(back.Ok, Why(back));
        Assert.IsType<C3dSplit>(back.Document!.Objects.Single(o => o.Name == "ArmTop"));
        Assert.True(C3dSetups.Read(back.Document).Single().Setup!.AirBox!.XMin!.Symmetry);
    }

    /// <summary>R-hzn21-3 — through the geometry kernel: each arm is the half of its cylinder at x ≥ 0 (radius 1.5 mm), its cut face
    /// is named split:xmin, and the elaborated problem's air box has its xmin face, at x = 0, as a magnetic symmetry plane.</summary>
    [KernelFact]
    public void ThroughTheKernel_TheArmsAreHalves_AndTheBoxHasItsSymmetryPlane()
    {
        using var kernel = KernelForTests.New();
        var t = Translate(Fixture);
        Assert.True(t.Ok, Why(t));
        var e = new C3dElaborator(null, kernel) { AttachedTechnology = t.Technology }.Elaborate(t.Document!, Path.Combine(_root, "half.c3d"), null);
        Assert.Empty(e.Refusals);
        var arm = e.Solids.Single(s => s.Name == "ArmTop");
        var b = Em3dProblem.Bounds(arm.Primitive);
        Assert.Equal((0.0, 1.5e-3, -1.5e-3, 1.5e-3), (b.X0, b.X1, b.Y0, b.Y1), new Tolerance(1e-9));
        Assert.Contains("split:xmin", arm.Primitive is Em3dShapeSolid k ? k.Faces.Select(f => f.Name) : []);

        var setup = C3dSetups.Read(t.Document!).Single().Setup!;
        var made = C3dProblemAssembly.Assemble(setup, t.Document!, Path.Combine(_root, "half.c3d"), null, new C3dElaborator(null, kernel) { AttachedTechnology = t.Technology });
        Assert.True(made.Ok, made.Refusal);
        var plane = Assert.Single(made.Problem!.Boundary.SymmetryPlanes);
        Assert.Equal((0, Em3dBoundaryKind.Pmc), (plane.Axis, plane.Wall));
        Assert.Equal(0.0, plane.AtM, 12);
    }

    /// <summary>A Custom plane through a cylinder's axis with normal (1, 1, 0) keeps the half the normal points to: the cylinder (radius
    /// 1.5 mm) cut by x + y = 0 reaches x = −r/√2 on the kept side and x = +r on the far one; Negative keeps the mirror of that.</summary>
    [KernelFact]
    public void ThroughTheKernel_ACustomPlaneKeepsTheSideItsNormalPointsTo()
    {
        using var kernel = KernelForTests.New();
        var t = Translate(Fixture);
        int dbu = t.Document!.DbuPerMicron;
        long r = 1500L * dbu;
        C3dSplit Cut(string name, C3dSplitKeep keep) => C3dSplits.Make(
            new C3dCylinder { Name = name, Material = "pec", Axis = C3dAxis.Z, Base = new C3dPoint3(0, 0, 0), Length = 10_000L * dbu, Radius = r },
            C3dSplitPlane.Custom, 0, keep) is var s ? Custom(s) : null!;
        static C3dSplit Custom(C3dSplit s) { s.Plane = C3dSplitPlane.Custom; s.Normal = [1, 1, 0]; return s; }
        var doc = new C3dDocument { DbuPerMicron = dbu, Objects = [Cut("pos", C3dSplitKeep.Positive), Cut("neg", C3dSplitKeep.Negative)] };
        var e = new C3dElaborator(null, kernel) { AttachedTechnology = t.Technology }.Elaborate(doc, Path.Combine(_root, "custom.c3d"), null);
        Assert.Empty(e.Refusals);
        double m = 1.5e-3, d = m / Math.Sqrt(2);
        var pos = Em3dProblem.Bounds(e.Solids.Single(s => s.Name == "pos").Primitive);
        var neg = Em3dProblem.Bounds(e.Solids.Single(s => s.Name == "neg").Primitive);
        Assert.Equal((-d, m, -d, m), (pos.X0, pos.X1, pos.Y0, pos.Y1), new Tolerance(2e-5));
        Assert.Equal((-m, d, -m, d), (neg.X0, neg.X1, neg.Y0, neg.Y1), new Tolerance(2e-5));
    }

    private sealed class Tolerance(double eps) : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) a, (double, double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < eps && Math.Abs(a.Item2 - b.Item2) < eps && Math.Abs(a.Item3 - b.Item3) < eps && Math.Abs(a.Item4 - b.Item4) < eps;
        public int GetHashCode((double, double, double, double) obj) => 0;
    }

    // ── the format and check (R-hzn21-1, R-hzn21-3) ──────────────────────────────────────────────

    /// <summary>A Split round-trips through the file with an expression At, and its box spans only the kept side.</summary>
    [Fact]
    public void ASplit_RoundTrips_AndItsBoundsAreTheKeptSide()
    {
        var cyl = new C3dCylinder { Name = "", Axis = C3dAxis.Z, Base = new C3dPoint3(0, 0, 0), Length = 10_000, Radius = 2_000 };
        var split = C3dSplits.Make(new C3dCylinder { Name = "rod", Axis = C3dAxis.Z, Length = 10_000, Radius = 2_000 }, C3dSplitPlane.YZ, 500, C3dSplitKeep.Negative);
        C3dBindings.SetExpr(split, C3dBindings.SpecOf(typeof(C3dSplit), nameof(C3dSplit.At))!, 0, new C3dExpr("x0", "Um"));
        var back = Assert.IsType<C3dSplit>(C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(split)));
        Assert.Equal(("rod", C3dSplitPlane.YZ, C3dSplitKeep.Negative, 500L, "x0"),
                     (back.Name, back.Plane, back.Keep, back.At, C3dBindings.GetExpr(back, nameof(C3dSplit.At), 0)!.Expr));
        Assert.Equal("", back.Target!.Name);
        Assert.Equal("split:xmax", back.CutFace);
        // the cutting box: past the target everywhere but the plane, which bounds the kept (negative) side
        var tree = GeometryKernelTree.From(back, 1000).Json;
        Assert.Contains("\"op\":\"intersect\"", tree);
        Assert.Contains("\"name\":\"split\"", tree);
        Assert.NotNull(GeometryKernelTree.BoundsUm(cyl, 1000));
    }

    /// <summary>R-hzn21-1 — Symmetry on an absorbing face, and on both faces of one axis, are refused by name; a flagged PMC face is a
    /// symmetry plane at the box's face.</summary>
    [Fact]
    public void SymmetryFaces_AreRefusedWhereTheyCannotBe_AndPlacedWhereTheyCan()
    {
        Em3dAirBox? Box(EmAirBox box, out string? refusal) =>
            Em3dGenerator.PaddedAirBox(new EmSetup { Solver3D = Em3dSolvers.Default, AirBox = box }, (0, -0.01, 0.01, 0.01, -0.02, 0.02), 1e9, null, [], [], out refusal);

        Assert.Null(Box(new EmAirBox(XMin: new EmAirBoxFace(0, Em3dBoundaryKind.Absorbing, null, true)), out string? absorbing));
        Assert.Contains("marked Symmetry, but it is Absorbing", absorbing);
        Assert.Null(Box(new EmAirBox(XMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pmc, null, true), XMax: new EmAirBoxFace(null, Em3dBoundaryKind.Pec, null, true)), out string? twice));
        Assert.Contains("are both symmetry planes", twice);

        var ok = Box(new EmAirBox(XMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pmc, null, true)), out string? none);
        Assert.Null(none);
        var plane = Assert.Single(ok!.SymmetryPlanes);
        Assert.Equal((0, 0.0, Em3dBoundaryKind.Pmc, "xmin"), (plane.Axis, plane.AtM, plane.Wall, plane.Face));
    }

    // ── the mirrored pattern (R-hzn21-2) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Electric current elements and their images: the half's elements radiate F_half, the whole structure (the half plus the image of
    /// every element in the plane) radiates F_whole, both summed exactly in circuitRF's convention. Mirroring F_half must give F_whole —
    /// for a magnetic wall and an electric one, with the plane on the origin and 0.3 λ off it, and with two planes (four copies).
    /// </summary>
    [Theory]
    [InlineData(Em3dBoundaryKind.Pmc, 0.0, false)]
    [InlineData(Em3dBoundaryKind.Pmc, 0.3, false)]
    [InlineData(Em3dBoundaryKind.Pec, 0.3, false)]
    [InlineData(Em3dBoundaryKind.Pmc, 0.3, true)]
    public void TheMirroredPattern_IsTheWholeStructures(Em3dBoundaryKind wall, double offsetLambda, bool twoPlanes)
    {
        const double f = 1e9;
        double lambda = 299_792_458.0 / f, x0 = offsetLambda * lambda, y0 = -0.1 * lambda;
        // the half: on x ≥ x0 (and y ≥ y0 with two planes)
        var half = new List<(Point3 R, Complex Jx, Complex Jy, Complex Jz)>
        {
            (new Point3(x0 + 0.05 * lambda, y0 + 0.07 * lambda, 0.02 * lambda), new Complex(0.3, 0.1), new Complex(-0.2, 0.4), new Complex(1, 0)),
            (new Point3(x0 + 0.21 * lambda, y0 + 0.11 * lambda, -0.13 * lambda), new Complex(0.0, -0.5), new Complex(0.6, 0.2), new Complex(0.4, -0.3)),
        };
        double s = wall == Em3dBoundaryKind.Pmc ? 1 : -1;
        var whole = new List<(Point3 R, Complex Jx, Complex Jy, Complex Jz)>(half);
        // the image of an electric current element in a wall: J'' = s M J at R r (the file header of Em3dSymmetry)
        (Point3, Complex, Complex, Complex) ImageX((Point3 R, Complex Jx, Complex Jy, Complex Jz) e) =>
            (new Point3(2 * x0 - e.R.X, e.R.Y, e.R.Z), s * -e.Jx, s * e.Jy, s * e.Jz);
        (Point3, Complex, Complex, Complex) ImageY((Point3 R, Complex Jx, Complex Jy, Complex Jz) e) =>
            (new Point3(e.R.X, 2 * y0 - e.R.Y, e.R.Z), s * e.Jx, s * -e.Jy, s * e.Jz);
        whole.AddRange(half.Select(ImageX));
        if (twoPlanes) whole.AddRange(whole.ToList().Select(ImageY));

        var grid = FarFieldStage.Sphere();
        var box = new Em3dAirBox(new Point3(x0, twoPlanes ? y0 : -1, -1), new Point3(1, 1, 1),
                                 new Em3dFaces(wall, Em3dBoundaryKind.Absorbing, twoPlanes ? wall : Em3dBoundaryKind.Absorbing,
                                               Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing))
        {
            SymmetryPlanes = twoPlanes
                ? [new Em3dSymmetryPlane(0, x0, wall) { Face = "xmin" }, new Em3dSymmetryPlane(1, y0, wall) { Face = "ymin" }]
                : [new Em3dSymmetryPlane(0, x0, wall) { Face = "xmin" }],
        };
        var halfPoint = new Em3dPatternPoint(Radiate(half, f, grid), 0.25, new Complex(0.1, 0.05));
        var mirrored = Em3dSymmetry.Mirror([halfPoint], box, +1, out string? note, out string? refusal);
        Assert.Null(refusal);
        Assert.Contains(twoPlanes ? "a quarter of the structure" : "half of the structure", note);
        var got = Assert.Single(mirrored!);
        var want = Radiate(whole, f, grid);
        double peak = want.ETheta.Concat(want.EPhi).Max(c => c.Magnitude);
        for (int g = 0; g < grid.DirectionCount; g++)
        {
            Assert.True((got.Pattern.ETheta[g] - want.ETheta[g]).Magnitude < 1e-9 * peak, $"Eθ at {g}");
            Assert.True((got.Pattern.EPhi[g] - want.EPhi[g]).Magnitude < 1e-9 * peak, $"Eφ at {g}");
        }
        Assert.Equal(0.25 * (twoPlanes ? 4 : 2), got.AcceptedW, 12);
        Assert.Equal(halfPoint.Reflection, got.Reflection);
    }

    /// <summary>The far field of electric current elements, F = −(jkη/4π) N⊥, N = Σ J e^{+jk r̂·r} — NearToFarField's convention.</summary>
    private static PlanarFarFieldPattern Radiate(IReadOnlyList<(Point3 R, Complex Jx, Complex Jy, Complex Jz)> elements, double f, PlanarFarFieldGrid grid)
    {
        double k = 2 * Math.PI * f / 299_792_458.0;
        var c = new Complex(0, k * FarFieldStage.Eta0 / (4 * Math.PI));
        var eth = new Complex[grid.DirectionCount];
        var eph = new Complex[grid.DirectionCount];
        for (int it = 0; it < grid.ThetaDeg.Count; it++)
            for (int ip = 0; ip < grid.PhiDeg.Count; ip++)
            {
                var (st, ct) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180);
                var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180);
                Complex nx = 0, ny = 0, nz = 0;
                foreach (var e in elements)
                {
                    var ph = Complex.FromPolarCoordinates(1, k * (st * cp * e.R.X + st * sp * e.R.Y + ct * e.R.Z));
                    nx += e.Jx * ph; ny += e.Jy * ph; nz += e.Jz * ph;
                }
                int g = grid.IndexOf(it, ip);
                eth[g] = -c * (nx * ct * cp + ny * ct * sp - nz * st);
                eph[g] = -c * (-nx * sp + ny * cp);
            }
        return FarFieldStage.Pattern(grid, eth, eph, 1, f);
    }

    /// <summary>EMerge and Palace no longer refuse a pattern because a face is a flagged wall; an unflagged one is still refused.</summary>
    [Fact]
    public void TheFarFieldRefusals_AllowASymmetryFace()
    {
        Em3dProblem Problem(bool flagged)
        {
            var faces = new Em3dFaces(Em3dBoundaryKind.Pmc, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing,
                                      Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing, Em3dBoundaryKind.Absorbing);
            var box = new Em3dAirBox(new Point3(0, -1, -1), new Point3(1, 1, 1), faces)
            {
                SymmetryPlanes = flagged ? [new Em3dSymmetryPlane(0, 0, Em3dBoundaryKind.Pmc) { Face = "xmin" }] : [],
            };
            return new Em3dProblem([], [], [], [], box, new Em3dFrequency(1e9, 1e9, 1, Em3dSweepKind.Linear), 25);
        }
        Assert.Null(EmergeFields.FarFieldRefusal(Problem(true)));
        Assert.Null(PalaceConfigWriter.FarFieldRefusal(Problem(true)));
        Assert.Contains("xmin face is Pmc", EmergeFields.FarFieldRefusal(Problem(false)));
        Assert.Contains("xmin face is Pmc", PalaceConfigWriter.FarFieldRefusal(Problem(false)));
    }
}
