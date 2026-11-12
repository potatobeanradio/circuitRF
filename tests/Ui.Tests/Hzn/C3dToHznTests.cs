using System.Text;
using System.Text.Json;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-4 — a <c>.c3d</c> written as a Hertzon design and read back. The mapping rows are proved the way the brief asks: the fixture's
/// design is translated by HZN-2, written by HZN-4, read by HZN-2 again, and the two documents compared as the solver sees them.
/// </summary>
public sealed class C3dToHznTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-hzn4-" + Guid.NewGuid().ToString("N")[..10]);

    public C3dToHznTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    private static C3dTranslation Translate(string file, string design)
    {
        var read = HznFile.Read(Fixture(file));
        Assert.True(read.Ok, read.Refusal?.Render());
        var t = HznToC3d.Translate(read.File!.Project, design);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t;
    }

    /// <summary>Writes <paramref name="t"/>'s document as a new project and returns what a reader makes of it.</summary>
    private (C3dToHznResult Written, C3dTranslation Back, string Path) RoundTrip(C3dTranslation t, string name)
    {
        var written = C3dToHzn.Write(t.Document!, new HznExportOptions { DesignName = name, Technology = t.Technology });
        Assert.True(written.Ok, HznExportDiagnostics.Refused(name, written.Refusals).Render());
        string path = Path.Combine(_tmp, name + ".hzn");
        var outcome = HznExport.ToNewFile(written, path);
        Assert.True(outcome.Ok, outcome.Error?.Render());
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        var back = HznToC3d.Translate(read.File!.Project, name);
        Assert.True(back.Ok, back.Refusal?.Render());
        return (written, back, path);
    }

    // ── what "equal" means ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The document as the solver sees it: every number resolved, every bound component named (an expression's text is spelled with
    /// the unit it was typed in on one side and the design's on the other, so its VALUE and the fact that it is bound are compared), the
    /// variables by value, the setups by what they run.
    /// </summary>
    private static string Canon(C3dDocument doc, Technology? tech)
    {
        var c = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        // Absent means air, and a written region says air: the same box.
        c.AirBoxMaterial ??= "air";
        var res = C3dResolver.Resolve(c, C3dCell.None);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        var sb = new StringBuilder();
        foreach (var b in C3dBindings.Bound(c).OrderBy(b => b.Item + b.Path, StringComparer.Ordinal))
            sb.AppendLine($"bound {b.Item}.{b.Path}");
        foreach (var n in res.Names.Values.OrderBy(n => (HznNames.HznProjectName(n.Name) ?? n.Name), StringComparer.Ordinal))
            sb.AppendLine($"var {HznNames.HznProjectName(n.Name) ?? n.Name} = {n.Value:R}");
        foreach (var item in C3dBindings.ItemsOf(c))
            foreach (var (_, owner) in C3dBindings.OwnersOf(item.Item)) owner.Exprs = null;
        foreach (var rot in c.Objects.SelectMany(C3dOperands.SelfAndDescendants).SelectMany(o => o.Placement.Rotate)) rot.Deg = Math.Round(rot.Deg, 9);
        foreach (var setup in C3dSetups.Read(c))
        {
            var s = setup.Setup!;
            sb.AppendLine($"setup {s.Name} f=[{string.Join(",", s.ExpandFrequencies().Select(f => f.ToString("R")))}] " +
                          $"adaptive={s.Emerge?.AdaptiveSweep} order={s.Emerge?.ElementOrder} passes={s.Emerge?.AdaptiveMesh}");
            if (s.AirBox is { } a)
                foreach (var (face, f) in new[] { ("xmin", a.XMin), ("xmax", a.XMax), ("ymin", a.YMin), ("ymax", a.YMax), ("zmin", a.ZMin), ("zmax", a.ZMax) })
                    sb.AppendLine($"  airbox {face} {f?.PaddingUm:R} {f?.Boundary}");
        }
        // A material is what it is made of, not what it is called: a sheet Hertzon makes metal by a boundary is named for its conductivity.
        string Values(string? name)
        {
            if (name is null) return "(none)";
            var m = tech?.FindMaterial(name);
            return m is null ? "(missing " + name + ")" : $"eps={m.Epsr ?? 1} tan={m.TanD ?? 0} mur={m.Mur ?? 1} sigma={m.Sigma20 ?? 0}";
        }
        foreach (var o in c.Objects.SelectMany(C3dOperands.SelfAndDescendants)) o.Material = o.Material is null ? null : Values(o.Material);
        // The integration line is not read back (which conductor is positive is not established), so the stated pair is not compared.
        foreach (var port in c.Ports) (port.Positive, port.Negative) = (null, null);
        foreach (var el in c.LumpedElements)
        {
            (el.Positive, el.Negative) = (null, null);
            // A value is spelled with the unit it was typed in on one side and in the expression on the other: compare the value.
            string? Value(string? expr, string? unit, string baseUnit) => expr is null ? null : res.Evaluate(expr, unit ?? baseUnit).AsReal().ToString("R");
            (el.R, el.RUnit) = (Value(el.R, el.RUnit, "Ohm"), null);
            (el.L, el.LUnit) = (Value(el.L, el.LUnit, "H"), null);
            (el.C, el.CUnit) = (Value(el.C, el.CUnit, "F"), null);
        }
        // A sheet is metal whatever it says; the reader says so.
        foreach (var sh in c.Objects.OfType<C3dSheet>()) sh.Role = null;
        foreach (var fb in c.FaceBoundaries) fb.Material = fb.Material is null ? null : Values(fb.Material);
        c.AirBoxMaterial = c.AirBoxMaterial == "air" ? null : c.AirBoxMaterial is null ? null : Values(c.AirBoxMaterial);
        c.ActiveSetup = null;
        c.FaceBoundaries = [.. c.FaceBoundaries.OrderBy(f => f.Object + "/" + f.Face, StringComparer.Ordinal)];
        c.Variables = [];
        c.Setups = [];
        sb.AppendLine(C3dPersistence.Serialize(c));
        return sb.ToString();
    }

    // ── the mapping rows, written and read back ─────────────────────────────────────────────────

    [Fact]
    public void EveryMappingRow_WrittenAndReadBack_GivesAnEqualDocument()
    {
        var t = Translate("mapping.hzn", "Mapping");
        var (_, back, _) = RoundTrip(t, "Mapping");
        Assert.Equal(Canon(t.Document!, t.Technology), Canon(back.Document!, back.Technology));
    }

    // ── a document drawn in circuitRF: what no Hertzon fixture produces ──────────────────────────

    private static void Bind(IC3dBindable owner, string property, int k, string expr, string unit = "Mm") =>
        C3dBindings.SetExpr(owner, C3dBindings.SpecOf(owner.GetType(), property)!, k, new C3dExpr(expr, unit));

    private static JsonElement Setup(string name, double startGhz, double stopGhz, int points, bool adaptive, string? airBoxJson)
    {
        string box = airBoxJson is null ? "" : ", \"AirBox\": " + airBoxJson;
        string json = """
            {"FormatVersion": 1, "Name": "@NAME", "Solver3D": "Emerge", "Problem3D": "Driven",
             "Frequency": {"StartExpr": "@START", "StopExpr": "@STOP", "StepExpr": "", "Mode": "PointCount", "Kind": "Linear",
                           "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "GHz", "NumPoints": @POINTS},
             "Emerge": {"AdaptiveSweep": @ADAPTIVE, "ElementOrder": 2, "AdaptiveMesh": 6}@BOX}
            """;
        json = json.Replace("@NAME", name).Replace("@START", startGhz.ToString("R")).Replace("@STOP", stopGhz.ToString("R"))
                   .Replace("@POINTS", points.ToString()).Replace("@ADAPTIVE", adaptive ? "true" : "false").Replace("@BOX", box);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string AirBox(double padUm, string x, string y, string z)
    {
        string Face(string k) => "{\"PaddingUm\": " + padUm.ToString("R") + ", \"Boundary\": \"" + k + "\"}";
        return "{\"XMin\": " + Face(x) + ", \"XMax\": " + Face(x) + ", \"YMin\": " + Face(y) + ", \"YMax\": " + Face(y)
             + ", \"ZMin\": " + Face(z) + ", \"ZMax\": " + Face(z) + "}";
    }

    private static Technology DrawnTechnology()
    {
        var tech = new Technology { Name = "drawn" };
        tech.Materials.Add(new TechMaterial { Name = "FR4", Epsr = 4.4, TanD = 0.02, Mur = 1 });
        tech.Materials.Add(new TechMaterial { Name = "Cu", Sigma20 = 5.8e7, Mur = 1 });
        tech.Materials.Add(new TechMaterial { Name = "pec", Epsr = 1, TanD = 0, Mur = 1 });
        return tech;
    }

    private static C3dDocument DrawnDocument()
    {
        var sub = new C3dBox { Name = "sub", Material = "FR4", Min = new(-3000000, 0, -1500000), Size = new(6000000, 20000000, 1500000) };
        Bind(sub, nameof(C3dBox.Min), 0, "-w");
        Bind(sub, nameof(C3dBox.Size), 0, "2*w");
        Bind(sub, nameof(C3dBox.Size), 2, "h");
        Bind(sub, nameof(C3dBox.Min), 2, "-h");
        var wedge = new C3dPrism
        {
            Name = "wedge", Material = "Cu", Plane = C3dPlane.XZ, Offset = 2000000, Height = 1500000,
            Outline = [new(0, 0), new(4000000, 0), new(0, 3000000)],
        };
        Bind(wedge, nameof(C3dPrism.Height), 0, "h");
        var post = new C3dCylinder { Name = "post", Material = "Cu", Axis = C3dAxis.X, Base = new(0, 5000000, 0), Length = 3000000, Radius = 500000 };
        post.Placement.MirrorX = true;
        post.Placement.Rotate.Add(new C3dRotation { Axis = C3dAxis.Z, Deg = 30 });
        post.Placement.Origin = new(1000000, 2000000, 0);
        Bind(post.Placement, nameof(C3dPlacement.Origin), 1, "w");
        var lid = new C3dBoolean
        {
            Name = "lid", Op = C3dBooleanOp.Subtract,
            Blank = new C3dBox { Material = "FR4", Min = new(5000000, 0, 0), Size = new(4000000, 4000000, 1000000) },
            Tools = [new C3dCylinder { Name = "bore", Material = "pec", Role = Em3dRole.Conductor, Base = new(7000000, 2000000, -1000000), Length = 3000000, Radius = 800000 }],
        };
        lid.Placement.Rotate.Add(new C3dRotation { Axis = C3dAxis.Z, Deg = 90 });
        var gnd = new C3dSheet { Name = "gnd", Material = "pec", Role = Em3dRole.Conductor, Plane = C3dPlane.XY, Offset = 0, Rect = new C3dRect { Min = new(-6000000, 0), Size = new(12000000, 20000000) } };
        var trace = new C3dSheet { Name = "trace", Material = "Cu", Plane = C3dPlane.XY, Offset = 1500000, ThicknessUm = 35, Rect = new C3dRect { Min = new(-1500000, 0), Size = new(3000000, 20000000) } };
        Bind(trace, nameof(C3dSheet.Offset), 0, "h");
        var slab = new C3dBox { Name = "slab", Material = "FR4", Min = new(-9000000, 0, 0), Size = new(2000000, 2000000, 2000000) };
        var ball = new C3dSphere { Name = "ball", Material = "Cu", Centre = new(-8000000, 10000000, 0), Radius = 1000000 };
        var uni = new C3dBoolean
        {
            Name = "uni", Op = C3dBooleanOp.Unite,
            Blank = new C3dBox { Material = "FR4", Min = new(10000000, 0, 0), Size = new(2000000, 2000000, 2000000) },
            Tools = [new C3dBox { Name = "uni_tool", Material = "FR4", Min = new(11000000, 1000000, 0), Size = new(2000000, 2000000, 2000000) }],
        };
        var kept = new C3dBoolean
        {
            Name = "kept", Op = C3dBooleanOp.Subtract, KeepTools = true,
            Blank = new C3dBox { Material = "FR4", Min = new(14000000, 0, 0), Size = new(3000000, 3000000, 2000000) },
            Tools = [new C3dCylinder { Name = "kept_tool", Material = "FR4", Base = new(15500000, 1500000, 0), Length = 2000000, Radius = 600000 }],
        };
        var outline = new C3dSheet
        {
            Name = "outline", Material = "Cu", Plane = C3dPlane.YZ, Offset = -9000000, ThicknessUm = 35,
            Outline = [new(0, 0), new(3000000, 0), new(3000000, 2000000), new(0, 1000000)],
        };
        var doc = new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mm,
            Variables =
            [
                new C3dVariable { Name = "w", Expression = "3", Unit = "Mm" },
                new C3dVariable { Name = "h", Expression = "1.5", Unit = "Mm" },
                new C3dVariable { Name = "n", Expression = "2" },
            ],
            Objects = [sub, wedge, post, lid, gnd, trace, slab, ball, uni, kept, outline],
            FaceBoundaries =
            [
                .. C3dBox.FaceNameList.Select(f => new C3dFaceBoundary { Object = "sub", Face = f, Kind = Em3dFaceBoundaryKind.Pec }),
                new C3dFaceBoundary { Object = "slab", Face = "zmax", Kind = Em3dFaceBoundaryKind.Conductive, Material = "Cu" },
                // A face of a built shape (a rotated plate with a bore): found by the worker's signature, not by a box's arithmetic.
                new C3dFaceBoundary { Object = "lid", Face = "zmax", Kind = Em3dFaceBoundaryKind.Pec },
            ],
            Ports =
            [
                new C3dPort
                {
                    Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = 0, Z0 = "50",
                    Rect = new C3dRect { Min = new(-1500000, 0), Size = new(3000000, 1500000) }, Positive = "trace", Negative = "gnd",
                },
                new C3dPort
                {
                    Number = 2, Name = "P2", Kind = Em3dPortKind.Wave, Plane = C3dPlane.YZ, Offset = -3000000, Z0 = "75",
                    Rect = new C3dRect { Min = new(0, -1500000), Size = new(20000000, 3000000) },
                },
            ],
            LumpedElements =
            [
                new C3dLumpedElement
                {
                    Name = "R1", Plane = C3dPlane.XZ, Offset = 10000000, Topology = Em3dRlcTopology.Series, R = "0.05", RUnit = "Ohm",
                    Rect = new C3dRect { Min = new(-1500000, 0), Size = new(3000000, 1500000) }, Positive = "trace", Negative = "gnd",
                },
            ],
            Setups = [Setup("S1", 1, 3, 21, true, AirBox(5000, "Pec", "Pec", "Pec"))],
            ActiveSetup = "S1",
        };
        Bind(doc.Ports[0].Rect, nameof(C3dRect.Size), 0, "w");
        return doc;
    }

    [Fact]
    public void ADocumentDrawnHere_WithPlacementsAPrismABooleanPortsAndFaces_ReadsBackEqual()
    {
        var doc = DrawnDocument();
        var tech = DrawnTechnology();
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Drawn", Technology = tech });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Drawn", written.Refusals).Render());
        string path = Path.Combine(_tmp, "drawn.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        var back = HznToC3d.Translate(read.File!.Project, "Drawn");
        Assert.True(back.Ok, back.Refusal?.Render());
        Assert.Equal(Canon(doc, tech), Canon(back.Document!, back.Technology));
    }

    [Fact]
    public void AirBoxWalls_AbsorbingAndMagnetic_AreRegionBoundariesThatReadBackEqual()
    {
        var doc = new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mm,
            Objects = [new C3dBox { Name = "slab", Material = "FR4", Min = new(0, 0, 0), Size = new(10000000, 10000000, 2000000) }],
            Setups = [Setup("S1", 2, 2, 1, false, AirBox(7000, "Absorbing", "Pmc", "Pec"))],
        };
        var tech = DrawnTechnology();
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Walls", Technology = tech });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Walls", written.Refusals).Render());
        string path = Path.Combine(_tmp, "walls.hzn");
        Assert.True(HznExport.ToNewFile(written, path).Ok);
        var back = HznToC3d.Translate(HznFile.Read(path).File!.Project, "Walls");
        Assert.True(back.Ok, back.Refusal?.Render());
        Assert.Equal(Canon(doc, tech), Canon(back.Document!, back.Technology));
    }

    // ── what Hertzon cannot say ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRefusal_ListsEveryUnsupportedItemAtOnce_AndWritesNothing()
    {
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dEllipsoid { Name = "egg", Material = "FR4", SemiAxes = new(1000000, 2000000, 3000000) },
                C3dPhantom.New(C3dPhantomShape.Sphere1, "head", default, 1000),
                new C3dVoxelBody { Name = "anatomy", File = "x.nii", Labels = [new C3dVoxelLabel { Label = 1, Name = "t", Material = "FR4" }] },
                new C3dMesh { Name = "tissue", Material = "FR4", File = "x.stl" },
                new C3dFillet { Name = "round", Radius = 100000, Edges = ["xmax|zmax"], Target = new C3dBox { Material = "FR4", Size = new(1000000, 1000000, 1000000) } },
                new C3dWire { Name = "bond", Points = [new(0, 0, 0), new(1000000, 0, 0)] },
            ],
            HeatSources = [new C3dHeatSource()],
            Probes = [new C3dProbe()],
            ContactResistances = [new C3dContactResistance()],
            EffectiveBlocks = [new C3dEffectiveBlock()],
            Ports = [new C3dPort { Number = 1, Name = "Pcyl", Cylinder = new C3dCylinderRect { Radius = 1000000, Length = 1000000, SweepDeg = 10 } }],
            Setups =
            [
                JsonDocument.Parse("""{"FormatVersion": 1, "Name": "Heat", "Problem3D": "Thermal"}""").RootElement.Clone(),
                JsonDocument.Parse("""{"FormatVersion": 1, "Name": "Cap", "Solver3D": "Palace", "Problem3D": "Electrostatic"}""").RootElement.Clone(),
            ],
        };
        var result = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Bad", Technology = DrawnTechnology() });
        Assert.False(result.Ok);
        Assert.Null(result.Json);
        string all = string.Join("\n", result.Refusals.Select(i => $"{i.Kind} {i.What}"));
        foreach (string expected in new[]
                 {
                     "ellipsoid", "phantom", "voxel body", "mesh body", "Fillet", "bond wire", "heat source", "probe",
                     "contact resistance", "effective block", "cylindrical port", "thermal setup", "electrostatic or magnetostatic setup",
                 })
            Assert.Contains(expected, all);
        string path = Path.Combine(_tmp, "never.hzn");
        Assert.False(HznExport.ToNewFile(result, path).Ok);
        Assert.False(File.Exists(path));
    }

    // ── into an existing project ─────────────────────────────────────────────────────────────────

    private static C3dToHznResult OneBox(string design, double epsr = 4.4)
    {
        var tech = DrawnTechnology();
        tech.Materials.Single(m => m.Name == "FR4").Epsr = epsr;
        var doc = new C3dDocument { DisplayUnit = LayoutUnit.Mm, Objects = [new C3dBox { Name = "b", Material = "pec", Role = Em3dRole.Conductor, Size = new(1000000, 1000000, 1000000) }] };
        return C3dToHzn.Write(doc, new HznExportOptions { DesignName = design, Technology = tech });
    }

    private static C3dToHznResult OneDielectric(string design, double epsr)
    {
        var tech = DrawnTechnology();
        tech.Materials.Single(m => m.Name == "FR4").Epsr = epsr;
        var doc = new C3dDocument { DisplayUnit = LayoutUnit.Mm, Objects = [new C3dBox { Name = "b", Material = "FR4", Size = new(1000000, 1000000, 1000000) }] };
        return C3dToHzn.Write(doc, new HznExportOptions { DesignName = design, Technology = tech });
    }

    [Fact]
    public void IntoAnExistingProject_EveryByteOutsideTheAppendedDesignIsUnchanged()
    {
        string path = Path.Combine(_tmp, "existing.hzn");
        File.Copy(Fixture("two-designs.hzn"), path);
        byte[] before = File.ReadAllBytes(path);

        var outcome = HznExport.IntoExisting(OneBox("Added"), path);
        Assert.True(outcome.Ok, outcome.Error?.Render());
        byte[] after = File.ReadAllBytes(path);

        // Removing one contiguous block from the new bytes gives the old ones back: the comma and the design are all that was added.
        int prefix = 0;
        while (prefix < before.Length && before[prefix] == after[prefix]) prefix++;
        int suffix = 0;
        while (suffix < before.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        Assert.Equal(before.Length, prefix + suffix);
        Assert.True(after.Length > before.Length);

        var project = HznFile.Read(path).File!.Project;
        Assert.Equal("Added", project.Designs[^1].Name);
        Assert.Equal(3, project.Designs.Count);
        Assert.Empty(project.Variables.Names.Where(n => n == "w"));   // our variables are the design's, never the project's
    }

    [Fact]
    public void IntoAnExistingProject_ANameTakenIsRefused_AConflictingMaterialIsRefusedWithItsProperties_AnIdenticalOneIsReused()
    {
        string path = Path.Combine(_tmp, "project.hzn");
        Assert.True(HznExport.ToNewFile(OneDielectric("First", 4.4), path).Ok);
        byte[] before = File.ReadAllBytes(path);

        var taken = HznExport.IntoExisting(OneDielectric("First", 4.4), path);
        Assert.False(taken.Ok);
        Assert.True(taken.NameTaken);
        Assert.Contains("--design-name", taken.Error!.Render());

        var conflict = HznExport.IntoExisting(OneDielectric("Second", 3.5), path);
        Assert.False(conflict.Ok);
        string why = conflict.Error!.Render();
        Assert.Contains("material 'FR4'", why);
        Assert.Contains("permittivity 3.5 here, 4.4 in the project", why);
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "a refusal must write nothing");

        var reused = HznExport.IntoExisting(OneDielectric("Third", 4.4), path);
        Assert.True(reused.Ok, reused.Error?.Render());
        var project = HznFile.Read(path).File!.Project;
        Assert.Equal(["First", "Third"], project.Designs.Select(d => d.Name));
        Assert.Single(project.Materials);
    }

    // ── flattening (D8) ──────────────────────────────────────────────────────────────────────────

    private string Workspace(out string childDir)
    {
        string ws = Path.Combine(_tmp, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), DrawnTechnology());
        CircuitRF.Design.Workspace.WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CircuitRF.Design.Workspace.CwsFile { DefaultTechRef = "tech.ctech" });
        childDir = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Child");
        CircuitRF.Design.Cells.CellCreate.WriteThreeDView(childDir, "Child", new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mm,
            Objects =
            [
                new C3dBox { Name = "cbox", Material = "FR4", Size = new(2000000, 2000000, 1000000) },
                new C3dCylinder { Name = "cpin", Material = "pec", Role = Em3dRole.Conductor, Base = new(1000000, 1000000, 1000000), Length = 1000000, Radius = 300000 },
            ],
        });
        return ws;
    }

    private string ParentWithInstance(string ws)
    {
        string dir = CircuitRF.Design.Cells.CellFolder.CreateCellFolder(ws, "Parent");
        var doc = new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mm,
            Objects = [new C3dBox { Name = "base", Material = "FR4", Size = new(5000000, 5000000, 500000) }],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Child", Placement = { Origin = new(1000000, 1000000, 500000) } }],
        };
        return CircuitRF.Design.Cells.CellCreate.WriteThreeDView(dir, "Parent", doc);
    }

    [Fact]
    public void AnInstance_IsFlattenedIntoObjectsNamedForIt_AndTheNoteStatesTheCounts()
    {
        string ws = Workspace(out _);
        string parent = ParentWithInstance(ws);
        var result = HznExport.FromFile(parent, null, Path.Combine(ws, ".cws"));
        Assert.True(result.Ok, HznExportDiagnostics.Refused("Parent", result.Refusals).Render());
        Assert.Equal("Parent", result.Design);
        Assert.Contains("flattened: 1 instance, 2 objects", result.Notes);
        var names = result.Json!.GetArray("objects")!.Items.OfType<HznJsonObject>().Select(o => o.GetString("name")).ToList();
        Assert.Equal(["base", "U1/cbox", "U1/cpin"], names);
    }
}
