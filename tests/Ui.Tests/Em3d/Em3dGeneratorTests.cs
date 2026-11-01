using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-3 — the solver-neutral 3D problem, generated from a layout and a technology. §7's
//  gates 1-7 and 9 (gate 8, Validate's count, is tests/Engine.Tests/Em3d/Em3dProblemTests.cs).
//  No solver is involved anywhere.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class Em3dGeneratorTests
{
    private const double Um = 1e-6;

    // ── 1. The reference page's microstrip ──────────────────────────────────────────────────────

    /// <summary>Designer round 33: with nothing stated, each face pads by multiples of H (20 H on the sides and top, 10 H
    /// below), rounded to 5 % of its own axis — on case B (H = 200 µm) the sides are 4 mm, and every face is a round share.</summary>
    [Fact]
    public void TheDefaultAirBox_PadsByMultiplesOfH_RoundedToFivePercentOfItsOwnAxis()
    {
        var (setup, source) = CaseB(plated: false);
        var p = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        Assert.Equal(200e-6, p.PaddingHeightM!.Value, 1e-12);
        var (lo, hi) = Em3dGenerator.ContentBounds(p);
        var b = p.Boundary;
        Assert.Equal(4e-3, lo.Y - b.Min.Y, 1e-9);
        foreach (var (pad, span) in new[] { (lo.X - b.Min.X, hi.X - lo.X), (b.Max.Y - hi.Y, hi.Y - lo.Y),
                                            (lo.Z - b.Min.Z, hi.Z - lo.Z), (b.Max.Z - hi.Z, hi.Z - lo.Z) })
            Assert.Equal(0, Math.Round(pad / span * 100, 6) % 5);
        Assert.True(b.Max.Z - hi.Z > lo.Z - b.Min.Z, "the top pads by 20 H, the bottom by 10 H");
    }

    /// <summary>Designer round 33: a drawn bottom ground plane with no hole floors the box on its underside, as a PEC face;
    /// a radiator keeps its absorbing floor, and so does a setup that states ZMin.</summary>
    [Fact]
    public void ADrawnUnbrokenBottomPlane_IsTheFloor_ExceptForARadiatorOrAStatedZMin()
    {
        (EmSetup, EmLayoutSource) Board()
        {
            var (setup, source) = Microstrip();
            var ground = source.Technology!.Stackup.Layers.Last(l => l.Kind == StackupKind.Conductor && l.IsGroundReference);
            source.View.Shapes.Add(new RectShape { Layer = ground.DrawingLayers[0], X1 = 0, Y1 = -3_000_000, X2 = 10_000_000, Y2 = 3_000_000 });
            return (setup, source);
        }
        var (s1, src1) = Board();
        var floored = Em3dGenerator.Generate(s1, src1, src1.Technology!);
        Assert.Equal(Em3dBoundaryKind.Pec, floored.Problem!.Boundary.Faces.ZMin);
        Assert.Equal(0, floored.Problem.Boundary.Min.Z, 1e-12);
        Assert.Contains(floored.Notes, n => n.Contains("floor is the underside of", StringComparison.Ordinal));

        var (s2, src2) = Board();
        s2.RadiationPattern = true;
        Assert.Equal(Em3dBoundaryKind.Absorbing, Em3dGenerator.Generate(s2, src2, src2.Technology!).Problem!.Boundary.Faces.ZMin);

        var (s3, src3) = Board();
        s3.AirBox = new EmAirBox(ZMin: new EmAirBoxFace(null, null, 50));
        Assert.Equal(Em3dBoundaryKind.Absorbing, Em3dGenerator.Generate(s3, src3, src3.Technology!).Problem!.Boundary.Faces.ZMin);
    }

    /// <summary>Designer round 33: a board edge drawn only as a closed zero-width path on a copper layer leaves every
    /// dielectric reaching the air box, and the notes say so and name the layer; on the outline layer it bounds them.</summary>
    [Fact]
    public void ABoardEdgeDrawnAsACentrelineOnCopper_IsNamed_AndOnTheOutlineLayerItBoundsTheDielectric()
    {
        var (setup, source) = CaseB(plated: false);
        var tech = source.Technology!;
        var copper = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).DrawingLayers[0];
        var edge = new LayerKey(90, 0);
        tech.Layers.Add(new LayerDef { Key = edge, Name = "Board outline", Interchange = new InterchangeMapping(null, null, null, null, null, "Edge.Cuts") });
        long um = source.View.DbuPerMicron;
        var square = new PathShape { Layer = copper, Width = 0, Xy = [-5000 * um, -2500 * um, 5000 * um, -2500 * um, 5000 * um, 2500 * um, -5000 * um, 2500 * um, -5000 * um, -2500 * um] };
        source.View.Shapes.Add(square);

        var onCopper = Em3dGenerator.Generate(setup, source, tech);
        Assert.Contains(onCopper.Notes, n => n.StartsWith("No board outline is drawn", StringComparison.Ordinal) && n.Contains("'Board outline'"));

        square.Layer = edge;
        var onOutline = Em3dGenerator.Generate(setup, source, tech);
        Assert.DoesNotContain(onOutline.Notes, n => n.StartsWith("No board outline is drawn", StringComparison.Ordinal));
        Assert.All(onOutline.Problem!.Solids.Where(s => s.Role == Em3dRole.Dielectric), s => Assert.IsNotType<Em3dBox>(s.Primitive));
    }

    [Fact]
    public void Gate1_TheReferenceMicrostrip_OneSubstrateOneAirOneConductorTwoPorts_OnAPecFloor()
    {
        var (setup, source) = Microstrip();
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        var p = result.Problem!;
        Assert.Empty(p.Validate());

        // One substrate, one air, one conductor — and 35 µm of copper is 50 skin depths at 10 GHz,
        // so a solid, not a sheet (R-em3d3-5f).
        Assert.Equal(["RO4350", "air", "Top Copper (1 oz)/1"], p.Solids.Select(s => s.Name));
        Assert.Equal([Em3dRole.Dielectric, Em3dRole.Air, Em3dRole.Conductor], p.Solids.Select(s => s.Role));
        Assert.Equal([1, 2, 3], p.Solids.Select(s => s.Order));
        Assert.Empty(p.Sheets);

        // z to the DBU-to-metre conversion: ground copper 0-35, RO4350 35-543, top copper 543-578 µm.
        var sub = Assert.IsType<Em3dBox>(p.Solids[0].Primitive);
        Assert.Equal(35 * Um, sub.Min.Z, 15);
        Assert.Equal(543 * Um, sub.Max.Z, 15);
        var cu = Assert.IsType<Em3dExtrudedPolygon>(p.Solids[2].Primitive);
        Assert.Equal(543 * Um, cu.ZBottom, 15);
        Assert.Equal(578 * Um, cu.ZTop, 15);
        var air = Assert.IsType<Em3dBox>(p.Solids[1].Primitive);
        Assert.Equal(543 * Um, air.Min.Z, 15);

        // The floor is the undrawn ground plane's top, and it is PEC; every other face absorbs.
        Assert.Equal(Em3dBoundaryKind.Pec, p.Boundary.Faces.ZMin);
        Assert.Equal(Em3dBoundaryKind.Absorbing, p.Boundary.Faces.ZMax);
        Assert.Equal(35 * Um, p.Boundary.Min.Z, 15);

        // Two ports, numbered as the planar setup numbers the same layout (R-em3d3-2d): the planar
        // extractor's own ports, compared by number and position.
        var planar = PlanarExtractor.Extract(source.View.Shapes, source.Technology!, source.DbuPerMicron, 10e9,
                                             setup.ToExtractionSettings());
        var planarPorts = EmPortExtraction.Extract(source.View.Shapes, planar.Problem!, source.DbuPerMicron,
                                                   setup.ResolvePortZ0).Ports;
        Assert.Equal(planarPorts.Select(q => (q.Number, q.Location.X)), p.Ports.Select(q => (q.Number, q.Min.X)));

        var p1 = p.Ports[0];
        Assert.Equal("port/1", p1.Name);
        Assert.Equal("Top Copper (1 oz)/1", p1.PositiveObject);
        Assert.Equal("airbox/zmin", p1.NegativeObject);
        Near(new Point3(0, -550 * Um, 35 * Um), p1.Min);
        Near(new Point3(0, 550 * Um, 543 * Um), p1.Max);
        Assert.Equal(new Point3(0, 0, 1), p1.Direction);
        Assert.Equal(0.0, p1.ReferencePlane.ShiftM);
        Assert.Equal(new Point3(1, 0, 0), p1.ReferencePlane.Normal);
        Assert.Equal(new Point3(-1, 0, 0), p.Ports[1].ReferencePlane.Normal);

        Assert.Equal(20.0, p.OperatingTempC);
        Assert.Equal(new Em3dFrequency(1e9, 10e9, 4, Em3dSweepKind.Linear), p.Frequency);
    }

    /// <summary>A ZMin that restates the default floor (Pec, or a padding alone) keeps the PEC floor on
    /// the undrawn plane. Turning the floor off for it left that plane with no representation and
    /// refused both ports; only a DIFFERENT stated boundary moves the floor below the geometry.</summary>
    [Theory]
    [InlineData(Em3dBoundaryKind.Pec, null)]
    [InlineData(null, 1500.0)]
    public void Gate1b_AZMinRestatingTheDefault_KeepsThePecFloor(Em3dBoundaryKind? kind, double? padding)
    {
        var (setup, source) = Microstrip();
        setup.AirBox = new EmAirBox(ZMin: new EmAirBoxFace(padding, kind));
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(Em3dBoundaryKind.Pec, result.Problem!.Boundary.Faces.ZMin);
        Assert.Equal(35 * Um, result.Problem.Boundary.Min.Z, 15);
    }

    /// <summary>brief-dfr32-1 — the content a .cem's box is padded round is the generator's, not its solids' bounds: the
    /// substrate slab is built out TO the box, so measuring from it read every stated lateral face as "setup: 0 mm" in
    /// explain, and made the box's own size the structure's extent.</summary>
    [Fact]
    public void ContentBounds_OfACemProblem_IsWhatTheBoxWasPaddedRound_NotTheSlabThatReachesIt()
    {
        var (setup, source) = Microstrip();
        setup.AirBox = new EmAirBox(XMin: new EmAirBoxFace(1500, null), YMax: new EmAirBoxFace(700, null));
        var p = Em3dGenerator.Generate(setup, source, source.Technology!).Problem!;
        var line = Em3dProblem.Bounds(p.Solids.Single(s => s.Role == Em3dRole.Conductor).Primitive);
        Assert.Equal(p.Boundary.Min.X, Em3dProblem.Bounds(p.Solids.Single(s => s.Role == Em3dRole.Dielectric).Primitive).X0, 12);

        var (lo, hi) = Em3dGenerator.ContentBounds(p);
        Assert.Equal(1500 * Um, lo.X - p.Boundary.Min.X, 9);
        Assert.Equal(700 * Um, p.Boundary.Max.Y - hi.Y, 9);
        Assert.Equal(line.X0, lo.X, 12);
    }

    /// <summary>brief-dfr28-3 R-dfr28-3-3 — the thru's port 1 is a sheet on its line's end at x = 0, the content's edge: a
    /// zero-padded absorbing xmin face would hold it, so that face is padded out by the sheet's longer side and a Note says
    /// so; a PMC face there is refused by name; the default padding keeps the port clear and says nothing.</summary>
    [Fact]
    public void ALumpedPortInAnAbsorbingFace_IsPaddedClear_AndInAPmcFaceIsRefused()
    {
        var (setup, source) = Microstrip();
        setup.AirBox = new EmAirBox(XMin: new EmAirBoxFace(0, null));
        var padded = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(padded.Ok, padded.Refusal);
        var port = padded.Problem!.Ports.Single(q => q.Number == 1);
        double side = Math.Max(port.Max.Y - port.Min.Y, port.Max.Z - port.Min.Z);
        Assert.Equal(-side, padded.Problem.Boundary.Min.X, 15);
        Assert.Contains(padded.Notes, n => n.StartsWith("Port 1's sheet lay in the air box's xmin face, an absorbing face", StringComparison.Ordinal));

        setup.AirBox = new EmAirBox(XMin: new EmAirBoxFace(0, Em3dBoundaryKind.Pmc));
        var pmc = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.False(pmc.Ok);
        Assert.StartsWith("Port 1's sheet lies in the air box's xmin face, which is Pmc", pmc.Refusal);

        setup.AirBox = null;
        Assert.DoesNotContain(Em3dGenerator.Generate(setup, source, source.Technology!).Notes, n => n.Contains("sheet lay in"));
    }

    // ── 2. Determinism ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_GeneratingTwiceGivesTheSameProblem_ByteForByteInCanonicalText()
    {
        var (setup, source) = CaseB(plated: true);
        string a = Canonical(Em3dGenerator.Generate(setup, source, source.Technology!).Problem!);
        var (setup2, source2) = CaseB(plated: true);
        string b = Canonical(Em3dGenerator.Generate(setup2, source2, source2.Technology!).Problem!);
        Assert.Equal(a, b);
    }

    // ── 3. F0's case B: a plated via is a tube, and the antipad is a hole ──────────────────────

    [Fact]
    public void Gate3_CaseB_APlatedViaIsTubePlusFill_AndTheGroundPlaneKeepsItsAntipad()
    {
        var (setup, source) = CaseB(plated: true);
        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        var p = result.Problem!;
        Assert.Empty(p.Validate());

        var plating = p.Solids.Single(s => s.Name == "via/1");
        var fill    = p.Solids.Single(s => s.Name == "via/1/fill");
        var barrel  = Assert.IsType<Em3dCylinder>(plating.Primitive);
        var bore    = Assert.IsType<Em3dCylinder>(fill.Primitive);
        Assert.Equal(Em3dRole.Conductor, plating.Role);
        Assert.Equal(Em3dRole.Air, fill.Role);
        Assert.Equal(150 * Um, barrel.Radius, 15);
        Assert.Equal(125 * Um, bore.Radius, 15);               // 25 µm wall
        Assert.Equal(0.0, barrel.AxisStart.Z, 15);              // bottom copper's bottom …
        Assert.Equal(505 * Um, barrel.AxisEnd.Z, 15);           // … to top copper's top
        Assert.Equal(plating.Order + 1, fill.Order);            // the fill wins: §1d subtracts
        Assert.True(plating.Order > p.Solids.Where(s => !s.Name.StartsWith("via/", StringComparison.Ordinal))
                                             .Max(s => s.Order));

        var plane = p.Solids.Single(s => s.Name == "Ground Plane/1");
        var slab  = Assert.IsType<Em3dExtrudedPolygon>(plane.Primitive);
        var hole  = Assert.Single(slab.Holes);
        Assert.Equal(450 * Um, hole.Max(q => q.X), 9);          // the 900 µm antipad
        Assert.Equal(235 * Um, slab.ZBottom, 15);
        Assert.Equal(270 * Um, slab.ZTop, 15);

        // The antipad is filled by the dielectric the inner plane's band is given to (the one above).
        var diel1 = Assert.IsType<Em3dBox>(p.Solids.Single(s => s.Name == "Dielectric 1").Primitive);
        Assert.Equal(235 * Um, diel1.Min.Z, 15);
        Assert.True(p.Solids.Single(s => s.Name == "Dielectric 1").Order < plane.Order);

        // A drawn plane is geometry, not a floor; the ports end on it, one from each side.
        Assert.Equal(Em3dBoundaryKind.Absorbing, p.Boundary.Faces.ZMin);
        Assert.Equal(["Ground Plane/1", "Ground Plane/1"], p.Ports.Select(q => q.NegativeObject));
        Assert.Equal([new Point3(0, 0, 1), new Point3(0, 0, -1)], p.Ports.Select(q => q.Direction));
        Assert.Equal(270 * Um, p.Ports[0].Min.Z, 15);
        Assert.Equal(470 * Um, p.Ports[0].Max.Z, 15);
        Assert.Equal(35 * Um, p.Ports[1].Min.Z, 15);
        Assert.Equal(235 * Um, p.Ports[1].Max.Z, 15);
    }

    // ── 4. An overmold ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_AnOvermoldCoversTheLateralExtent_AboveDielectricsBelowConductors()
    {
        var (setup, source) = Microstrip();
        var tech = TechPersistence.Clone(source.Technology!);
        tech.Materials.Add(new TechMaterial { Name = "Mould compound", Epsr = 3.9, TanD = 0.005 });
        tech.Bodies.Add(new TechBody
        {
            Name = "Overmold", Material = "Mould compound", SitsOn = "RO4350", ThicknessDbu = 500_000,
        });

        var p = Em3dGenerator.Generate(setup, source with { Technology = tech }, tech).Problem!;
        Assert.Empty(p.Validate());

        var mould = p.Solids.Single(s => s.Name == "Overmold");
        var bodyBox = Assert.IsType<Em3dBox>(mould.Primitive);
        Assert.Equal(p.Boundary.Min.X, bodyBox.Min.X);
        Assert.Equal(p.Boundary.Max.Y, bodyBox.Max.Y);
        Assert.Equal(543 * Um, bodyBox.Min.Z, 15);
        Assert.Equal(1043 * Um, bodyBox.Max.Z, 15);
        Assert.Equal(Em3dRole.Dielectric, mould.Role);

        int dielectric = p.Solids.Single(s => s.Name == "RO4350").Order;
        int conductor  = p.Solids.Single(s => s.Role == Em3dRole.Conductor).Order;
        Assert.InRange(mould.Order, dielectric + 1, conductor - 1);
        Assert.Contains(p.Materials, m => m.Name == "Mould compound" && m.Epsr == 3.9);
    }

    // ── 5. Auto never resolves to 3D ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AutoNeverResolvesTo3D_OverEveryCemInTheRepo()
    {
        var cems = RepoCems();
        Assert.True(cems.Count >= 10, $"only {cems.Count} .cem files found");
        foreach (string cem in cems)
        {
            var setup = EmSetupPersistence.LoadFromFile(cem);
            // A 3D setup is one its FILE states (the shipped 3D EM examples); Auto never resolves to one.
            if (setup.Is3D) { Assert.Contains("\"Solver3D\"", File.ReadAllText(cem), StringComparison.Ordinal); continue; }
            var choice = EmKernelRegistry.Choose(setup.AnalysisKind, EmExtractorVerdict.Yes, EmExtractorVerdict.Yes);
            Assert.Contains(choice.Kind, new[] { EmAnalysisKind.CrossSection, EmAnalysisKind.Planar });
        }
        // By construction: no registered kernel, and no analysis kind, is a 3D solver.
        Assert.Equal([EmAnalysisKind.CrossSection, EmAnalysisKind.Planar], EmKernelRegistry.Kernels.Select(k => k.Kind));
        Assert.Equal(["CrossSection", "Planar", "Auto"], Enum.GetNames<EmAnalysisKind>());
    }

    // ── 6. Round trip ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_EveryCemInTheRepo_RoundTripsWithNoNewKey_AndA3DSetupRoundTrips()
    {
        // Writer to writer, as brief 2's gate 1 does for .ctech: not every .cem in the repo is in this
        // writer's exact spelling (ten state "ResonanceSearch": false, which the writer omits), so the
        // bytes on disk were never the fixed point. What the 3D fields could change is what the writer
        // EMITS for a planar file, and the only way is a key the file did not state.
        foreach (string cem in RepoCems())
        {
            string text = File.ReadAllText(cem);
            string written = EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(text));
            Assert.Equal(written, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(written)));
            // A key the file did not state — a 3D EM example states these, a planar file must not gain them.
            foreach (string key in new[] { "\"Solver3D\"", "\"OperatingTempC\"", "\"AirBox\"", "\"Palace\"", "\"OpenEms\"" })
                if (!text.Contains(key, StringComparison.Ordinal)) Assert.DoesNotContain(key, written, StringComparison.Ordinal);
        }

        var (setup, _) = Microstrip();
        setup.Solver3D       = Em3dSolverId.Palace;
        setup.OperatingTempC = 85;
        setup.AirBox         = new EmAirBox(ZMax: new EmAirBoxFace(2000, Em3dBoundaryKind.Pec));
        setup.Palace         = new CemPalace();
        string once = EmSetupPersistence.Serialize(setup);
        Assert.Equal(once, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(once)));
        var back = EmSetupPersistence.Deserialize(once);
        Assert.Equal(Em3dSolverId.Palace, back.Solver3D);
        Assert.Equal(85, back.OperatingTempC);
        Assert.Equal(new EmAirBoxFace(2000, Em3dBoundaryKind.Pec), back.AirBox!.ZMax);
        Assert.NotNull(back.Palace);
        Assert.Null(back.OpenEms);
    }

    // ── 7. A refused port ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_APortThePlanarExtractorRefuses_IsRefusedWithThePlanarSentence()
    {
        var (setup, source) = Microstrip();
        var port2 = source.View.Shapes.OfType<LabelShape>().Single(l => l.Text == "2");
        port2.X = 12_000_000;                           // 2 mm past the end of the line

        var planar = PlanarExtractor.Extract(source.View.Shapes, source.Technology!, source.DbuPerMicron, 10e9,
                                             setup.ToExtractionSettings());
        string planarRefusal = EmPortExtraction.Extract(source.View.Shapes, planar.Problem!, source.DbuPerMicron,
                                                        setup.ResolvePortZ0, source.View.DisplayUnit).Refusal!;

        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.False(result.Ok);
        Assert.Equal(planarRefusal, result.Refusal);
    }

    // ── A via's pad on the ground plane ────────────────────────────────────────────────────────

    /// <summary>A VIAGND on a microstrip board draws a pad on the plane it lands on. That pad is the plane's, not a
    /// drawing of it: the plane stays the PEC floor, the ports return through it, and the barrel ends on it.</summary>
    [Fact]
    public void AViaPadOnTheUndrawnPlane_KeepsTheFloor_AndTheBarrelIsBuilt()
    {
        var (setup, source) = Microstrip();
        var shapes = source.View.Shapes;
        shapes.Add(new ViaShape { X = 5_000_000, Y = 0, PadSize = 609_600, DrillSize = 304_800, Layer = new LayerKey(7, 0) });
        shapes.Add(new CircleShape { Cx = 5_000_000, Cy = 0, R = 304_800, Layer = new LayerKey(1, 0) });
        shapes.Add(new CircleShape { Cx = 5_000_000, Cy = 0, R = 304_800, Layer = new LayerKey(2, 0) });

        var result = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(result.Ok, result.Refusal);
        Assert.Contains(result.Problem!.Solids, sd => sd.Name == "via/1" && sd.Primitive is Em3dCylinder);
        Assert.DoesNotContain(result.Problem.Solids, sd => sd.Material.StartsWith("Bottom Copper", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("floor is 'Bottom Copper", StringComparison.Ordinal));
        Assert.Equal(2, result.Problem.Ports.Count);
    }

    /// <summary>A non-plated hole is drilled as air through its span — after every metal, so it cuts the copper it crosses
    /// as well as the substrate — and is never a conductor. The shipped plated barrel is copper.</summary>
    [Fact]
    public void ANonPlatedHole_IsAirThroughItsSpan_AndAPlatedBarrelIsCopper()
    {
        var (setup, source) = Microstrip();
        source.View.Shapes.Add(new ViaShape { X = 5_000_000, Y = 0, PadSize = 609_600, DrillSize = 304_800, Layer = new LayerKey(7, 0) });

        var plated = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(plated.Ok, plated.Refusal);
        Assert.Equal("Copper", plated.Problem!.Solids.Single(sd => sd.Name == "via/1").Material);

        source.Technology!.Stackup.Layers.Single(l => l.Kind == StackupKind.Via).Plated = false;
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;
        Assert.DoesNotContain(p.Solids, sd => sd.Name.StartsWith("via/", StringComparison.Ordinal));
        var hole = p.Solids.Single(sd => sd.Name == "hole/1");
        Assert.Equal(Em3dRole.Air, hole.Role);
        var c = Assert.IsType<Em3dCylinder>(hole.Primitive);
        Assert.Equal(152.4e-6, c.Radius, 1e-12);
        var precedence = Em3dPrecedence.Of(p);
        Assert.All(p.Solids.Where(sd => sd != hole), sd => Assert.True(precedence.Of(hole) > precedence.Of(sd), sd.Name));
        Assert.Contains(r.Notes, n => n.Contains("NON-PLATED", StringComparison.Ordinal));
        // …and the 3D view (and the 3D editor, which draws through the same builder) shows the substrate drilled.
        var bores = CircuitRF.Render.Scene3D.Scene3DBores.Of(p)!;
        Assert.All(p.Solids.Where(sd => sd.Role == Em3dRole.Dielectric), sd => Assert.NotNull(bores.Carved(sd)));
    }

    /// <summary>A Gerber or DXF drill file imports as CIRCLES on the drill layer, not vias. A plated one is a barrel with an
    /// air core exactly as a via is, so the 3D view carves the substrate around it; as an extruded polygon it was a solid
    /// copper rod whose caps z-fought the pads it ends in (owner report, 2026-10-12).</summary>
    [Fact]
    public void APlatedDrillCircle_IsABarrelWithAnAirCore_AsAViaIs()
    {
        var (setup, source) = Microstrip();
        source.View.Shapes.Add(new CircleShape { Cx = 5_000_000, Cy = 0, R = 152_400, Layer = new LayerKey(7, 0) });

        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;
        Assert.Equal(152.4e-6, Assert.IsType<Em3dCylinder>(p.Solids.Single(sd => sd.Name == "via/1").Primitive).Radius, 1e-12);
        Assert.Equal(Em3dRole.Air, p.Solids.Single(sd => sd.Name == "via/1/fill").Role);
        var bores = CircuitRF.Render.Scene3D.Scene3DBores.Of(p)!;
        Assert.All(p.Solids.Where(sd => sd.Role == Em3dRole.Dielectric), sd => Assert.NotNull(bores.Carved(sd)));
    }

    /// <summary>A Gerber outline is strokes, merged into a thin ring: the board is the ring's hole, not the ring. A ring
    /// inside that hole is a cutout, and a corner mark's tiny enclosed gap is no board. A filled shape stays as drawn.</summary>
    [Fact]
    public void AnOutlineDrawnAsALine_BoundsTheBoardItEncloses()
    {
        static List<EmPoint> Sq(double c, double h) => [new(c - h, c - h), new(c + h, c - h), new(c + h, c + h), new(c - h, c + h)];
        const double Mm = 1e-3;
        var gap = new List<EmPoint> { new(-25.8 * Mm, -25.8 * Mm), new(-25.3 * Mm, -25.8 * Mm), new(-25.8 * Mm, -25.3 * Mm) };
        var line = new PlanarPolygon(Sq(0, 26 * Mm), [Sq(0, 25 * Mm), gap]);
        var cutout = new PlanarPolygon(Sq(0, 5.1 * Mm), [Sq(0, 5 * Mm)]);
        var filled = new PlanarPolygon(Sq(40 * Mm, 1 * Mm));

        var board = Em3dLayoutSolids.Builder.BoardRegions([line, cutout, filled], out int lines);

        Assert.Equal(2, lines);
        Assert.Equal(2, board.Count);
        Assert.Same(filled, board[0]);
        Assert.Equal((50 * 50 - 10.2 * 10.2) * Mm * Mm, board[1].Area(), 1e-12);
        Assert.True(board[1].Contains(20 * Mm, 20 * Mm));
        Assert.False(board[1].Contains(0, 0));
    }

    /// <summary>Crop to Rectangle redraws a stroked outline as ONE closed path along the crop line. The 3D builder dropped
    /// every path on the outline layer as "strokes, which bound no region", so a cropped board's substrate filled the whole
    /// air box (designer report, round 26). A closed stroke is read along its centre line, which is the crop line.</summary>
    [Fact]
    public void AClosedOutlineStroke_BoundsTheBoardAlongItsCentreLine()
    {
        var (setup, source) = Microstrip();
        source.View.Shapes.Add(new PathShape
        {
            Layer = new LayerKey(8, 0), Width = 200_000,
            Xy = [-1_000_000, -2_000_000, 11_000_000, -2_000_000, 11_000_000, 2_000_000, -1_000_000, 2_000_000, -1_000_000, -2_000_000],
        });

        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        Assert.DoesNotContain(r.Notes, n => n.Contains("bound no region", StringComparison.Ordinal));
        var substrate = Assert.IsType<Em3dExtrudedPolygon>(
            r.Problem!.Solids.First(sd => sd.Role == Em3dRole.Dielectric).Primitive);
        Assert.Equal(-1e-3, substrate.Outline.Min(q => q.X), 9);
        Assert.Equal(11e-3, substrate.Outline.Max(q => q.X), 9);
        Assert.Equal(2e-3, substrate.Outline.Max(q => q.Y), 9);
    }

    /// <summary>A fabricator's target: a disc on the outline layer with a drill of its own size through it. It is no board — as
    /// substrate it was a solid disc over the barrel whose facets striped through the copper wall (owner report, 2026-10-12).</summary>
    [Fact]
    public void AnOutlineDiscNoLargerThanItsDrill_IsDrilledAway()
    {
        var (setup, source) = Microstrip();
        source.View.Shapes.Add(new CircleShape { Cx = 20_000_000, Cy = 0, R = 1_054_100, Layer = new LayerKey(8, 0) });
        source.View.Shapes.Add(new CircleShape { Cx = 20_000_000, Cy = 0, R = 1_054_100, Layer = new LayerKey(7, 0) });
        var r = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n => n.Contains("no larger than the hole drilled through", StringComparison.Ordinal));

        const double Mm = 1e-3;
        var disc = new PlanarPolygon([.. Enumerable.Range(0, 24).Select(k => new EmPoint(Math.Cos(k * Math.PI / 12) * Mm, Math.Sin(k * Math.PI / 12) * Mm))]);
        Assert.True(Em3dLayoutSolids.Builder.WithinDrill(disc, (0, 0, 1 * Mm)));
        Assert.False(Em3dLayoutSolids.Builder.WithinDrill(disc, (0, 0, 0.9 * Mm)));
    }

    // ── A return plane that stops short of the line's end ───────────────────────────────────────

    /// <summary>A plane drawn to a line's end by hand can stop a few µm short of it. Within a tenth of
    /// the line's width (40 µm here) the sheet moves in to where the plane begins and says so; beyond
    /// it the refusal states the gap, which the old "no metal under the port" never did.</summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(100, false)]
    public void AReturnPlaneShortOfTheLineEnd_WithinATenthOfTheWidthMovesTheSheet_BeyondItSaysHowFar(int gapUm, bool ok)
    {
        long planeEnd = 10_000_000 - gapUm * 1000L;
        string clay = $$"""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X1": 0, "Y1": -200000, "X2": 10000000, "Y2": 200000 },
                { "$type": "Rect", "Layer": { "Layer": 2, "Datatype": 0 },
                  "X1": -500000, "Y1": -2000000, "X2": {{planeEnd}}, "Y2": 2000000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var setup = new EmSetup
        {
            // brief-dfr28-4 — no named plane: the walk finds Inner 1 under the line, and stops at it when it stops short.
            Name = "thru", LayoutRef = "thru/layout/thru.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        var r = Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/thru.clay", view, tech, view.DbuPerMicron), tech);

        if (ok)
        {
            Assert.True(r.Ok, r.Refusal);
            var p2 = r.Problem!.Ports.Single(q => q.Number == 2);
            Assert.Equal(planeEnd * 1e-9, p2.Min.X, 12);
            Assert.StartsWith("Inner 1 (Ground Plane)", p2.NegativeObject, StringComparison.Ordinal);
            Assert.Contains(r.Notes, n => n.Contains("Port 2's sheet is 30 µm (0.03 mm) inside its line's end", StringComparison.Ordinal));
        }
        else
            Assert.Contains("stops 100 µm (0.1 mm) short of the port's end of the line", r.Refusal, StringComparison.Ordinal);
    }

    // ── 9. The reference page ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_TheEmSetupReferencePage_DescribesTheNewFields()
    {
        string page = CircuitRF.Cli.DocumentSchema.Render(CircuitRF.Cli.DocumentSchema.Find("em-setup")!);
        foreach (string word in new[] { "Solver3D", "AirBox", "OperatingTempC", "CemAirBoxFace", "PaddingUm",
                                        "Em3dSolver", "OpenEms", "Em3dBoundaryKind", "Absorbing" })
            Assert.Contains(word, page, StringComparison.Ordinal);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>`circuitrf reference layout`'s own example, on the technology it names, with the
    /// em-setup page's own frequency plan and Solver3D set.</summary>
    internal static (EmSetup, EmLayoutSource) Microstrip()
    {
        const string clay = """
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Mm", "SnapDbu": 10000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X1": 0, "Y1": -550000, "X2": 10000000, "Y2": 550000 },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 0, "Y": 0, "Text": "1", "Height": 400000, "IsPort": true, "PortDirection": "R0" },
                { "$type": "Label", "Layer": { "Layer": 1, "Datatype": 0 },
                  "X": 10000000, "Y": 0, "Text": "2", "Height": 400000, "IsPort": true, "PortDirection": "R180" }
              ],
              "Instances": []
            }
            """;
        var view = LayoutPersistence.Deserialize(clay);
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var setup = new EmSetup
        {
            Name = "thru", LayoutRef = "thru/layout/thru.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        return (setup, new EmLayoutSource("/nowhere/thru.clay", view, tech, view.DbuPerMicron));
    }

    /// <summary>F0's case B (testdata/em3d/f0/B-via/planar/ws) — its own .clay and .ctech, with the
    /// signal via made a plated barrel with a 25 µm wall.</summary>
    internal static (EmSetup, EmLayoutSource) CaseB(bool plated)
    {
        string ws = Path.Combine(RepoRoot(), "testdata", "em3d", "f0", "B-via", "planar", "ws");
        string clay = Path.Combine(ws, "via", "layout", "via.clay");
        var view = LayoutPersistence.LoadFromFile(clay);
        var tech = TechPersistence.Deserialize(File.ReadAllText(Path.Combine(ws, "tech", "f0-case-b.ctech")));
        if (plated)
        {
            var via = tech.Stackup.Layers.Single(l => l.Kind == StackupKind.Via);
            via.Fill = ViaFillKind.Plated;
            via.WallThicknessDbu = 25_000;
        }
        var setup = new EmSetup
        {
            Name = "via", LayoutRef = "via/layout/via.clay",
            Frequency = new FrequencySpec("0.1", "20", 200, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        return (setup, new EmLayoutSource(clay, view, tech, view.DbuPerMicron));
    }

    /// <summary>The problem as text, every double in round-trip form — what gate 2 compares.</summary>
    private static string Canonical(Em3dProblem p)
    {
        var sb = new StringBuilder();
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string P3(Point3 q) => $"({R(q.X)},{R(q.Y)},{R(q.Z)})";
        string Ring(IReadOnlyList<Point2> r) => string.Join(" ", r.Select(q => $"{R(q.X)},{R(q.Y)}"));

        foreach (var s in p.Solids)
        {
            sb.Append($"solid {s.Name} {s.Material} {s.Role} {s.Order} ");
            sb.AppendLine(s.Primitive switch
            {
                Em3dExtrudedPolygon e => $"extrude {R(e.ZBottom)} {R(e.ZTop)} [{Ring(e.Outline)}] " +
                                         string.Join("", e.Holes.Select(h => $"[{Ring(h)}]")),
                Em3dBox b => $"box {P3(b.Min)} {P3(b.Max)}",
                Em3dCylinder c => $"cylinder {P3(c.AxisStart)} {P3(c.AxisEnd)} {R(c.Radius)}",
                var other => other.ToString(),
            });
        }
        foreach (var s in p.Sheets)
            sb.AppendLine($"sheet {s.Name} {s.Material} {s.Order} {R(s.Z)} {R(s.ThicknessM)} [{Ring(s.Outline)}]");
        foreach (var m in p.Materials)
            sb.AppendLine($"material {m.Name} {R(m.Epsr)} {R(m.TanD)} {R(m.Mur)} {R(m.SigmaSm)}");
        foreach (var q in p.Ports)
            sb.AppendLine($"port {q.Number} {q.Name} {q.PositiveObject} {q.NegativeObject} {P3(q.Min)} {P3(q.Max)} " +
                          $"{P3(q.Direction)} {R(q.Z0.Real)} {R(q.Z0.Imaginary)} {P3(q.ReferencePlane.Origin)}");
        sb.AppendLine($"box {P3(p.Boundary.Min)} {P3(p.Boundary.Max)} {p.Boundary.Faces}");
        sb.AppendLine($"freq {p.Frequency} {R(p.OperatingTempC)}");
        return sb.ToString();
    }

    private static void Near(Point3 expected, Point3 actual)
    {
        Assert.Equal(expected.X, actual.X, 15);
        Assert.Equal(expected.Y, actual.Y, 15);
        Assert.Equal(expected.Z, actual.Z, 15);
    }

    private static List<string> RepoCems()
        => [.. new[] { "examples", "testdata" }
               .SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*.cem", SearchOption.AllDirectories))
               .Order(StringComparer.Ordinal)];

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }
}
