// ================================================================
//  CoaxialPortTests.cs — brief-cxp-1, routine tier (no solver): the .c3d's Annulus key, its resolution to a coaxial lumped
//  port in each of the three planes, the refusals, and the Palace input it lowers to. The fixture is a straight coax — the
//  3D Connector's 0.4 mm pin in a 1.34 mm PTFE bore — 20 mm long, a ring at each end on the air box's PEC faces. The same
//  fixture, solved, is CoaxialPortPalaceTests' (the Benchmark tier).
// ================================================================

using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class CoaxialPortTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cxp1-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const long Um = 1000;                 // DBU per µm
    internal const long PinUm = 200, BoreUm = 670, LengthUm = 20000, ShellUm = 1000;

    /// <summary>
    /// The coax along the normal of <paramref name="plane"/>: a square shell with the bore through it, the PTFE fill, the pin,
    /// and a ring at each end. The radii are bound to VARs, as the editor's Make Port writes them. One embedded Palace setup,
    /// every face PEC with no padding, so each ring lies on a face of the box.
    /// </summary>
    /// <param name="order">The Palace setup's element order; null leaves Draft's own (1).</param>
    /// <param name="solver">The embedded setup's solver and name: <c>Palace</c>, <c>Emerge</c> (EMerge at its Accurate quality), or
    /// <c>OpenEms</c> (a grading ratio of 1.15 near the coax, the 3D Wave Ports Launch's).</param>
    /// <param name="lengthUm">The coax's length.</param>
    /// <param name="endPaddingUm">Air beyond the coax's two ends before the box's PEC faces: 0 puts each ring ON a face of the box;
    /// more puts it inside, an open coax end with the air behind it.</param>
    /// <param name="boolean">The shell as the editor makes one — a box with the bore subtracted and kept as the PTFE — which needs the
    /// geometry kernel; else a square prism whose hole is a 64-sided polygon, which needs none. The polygon's flats stand 0.8 µm
    /// inside the round PTFE, which is no matter to the port's inference but leaves slivers Gmsh cannot mesh: a solve takes the
    /// boolean.</param>
    internal static C3dDocument Coax(C3dPlane plane = C3dPlane.YZ, double startGHz = 1, double stopGHz = 10, int points = 3, bool boolean = false,
                                     int? order = null, string solver = "Palace", long endPaddingUm = 0,
                                     int? openEmsCellsPerWavelength = null, long lengthUm = LengthUm)
    {
        var axis = plane switch { C3dPlane.YZ => C3dAxis.X, C3dPlane.XZ => C3dAxis.Y, _ => C3dAxis.Z };
        C3dPoint2 P(long u, long v) => new(u * Um, v * Um);
        C3dPort Ring(int n, long offsetUm) => new()
        {
            Number = n, Name = $"P{n}", Kind = Em3dPortKind.Lumped, Plane = plane, Offset = offsetUm * Um,
            Annulus = Bound(new C3dPortAnnulus { Centre = new C3dPoint2(0, 0), Inner = PinUm * Um, Outer = BoreUm * Um }),
        };
        var doc = new C3dDocument
        {
            Variables =
            [
                new C3dVariable { Name = "pin_d", Expression = "0.4", Unit = "Mm" },
                new C3dVariable { Name = "bore_d", Expression = "1.34", Unit = "Mm" },
            ],
            Objects = boolean
            ?
            [
                new C3dBoolean
                {
                    Name = "shell", Op = C3dBooleanOp.Subtract, KeepTools = true,
                    Blank = new C3dBox { Material = "Copper", Min = At(-ShellUm, -ShellUm, 0), Size = Extent(2 * ShellUm, 2 * ShellUm, lengthUm) },
                    Tools = [new C3dCylinder { Name = "ptfe", Material = "PTFE", Base = new C3dPoint3(0, 0, 0), Axis = axis, Length = lengthUm * Um, Radius = BoreUm * Um }],
                },
                new C3dCylinder { Name = "pin", Material = "Copper", Base = new C3dPoint3(0, 0, 0), Axis = axis, Length = lengthUm * Um, Radius = PinUm * Um },
            ]
            :
            [
                new C3dPrism
                {
                    Name = "shell", Material = "Copper", Plane = plane, Offset = 0, Height = lengthUm * Um,
                    Outline = [P(-ShellUm, -ShellUm), P(ShellUm, -ShellUm), P(ShellUm, ShellUm), P(-ShellUm, ShellUm)],
                    Holes = [[.. Enumerable.Range(0, 64).Select(k => new C3dPoint2((long)Math.Round(BoreUm * Um * Math.Cos(2 * Math.PI * k / 64)),
                                                                                  (long)Math.Round(BoreUm * Um * Math.Sin(2 * Math.PI * k / 64))))]],
                },
                new C3dCylinder { Name = "ptfe", Material = "PTFE", Base = new C3dPoint3(0, 0, 0), Axis = axis, Length = lengthUm * Um, Radius = BoreUm * Um },
                new C3dCylinder { Name = "pin", Material = "Copper", Base = new C3dPoint3(0, 0, 0), Axis = axis, Length = lengthUm * Um, Radius = PinUm * Um },
            ],
            Ports = [Ring(1, 0), Ring(2, lengthUm)],
        };
        doc.Setups.Add(JsonDocument.Parse(PalaceSetupJson(startGHz, stopGHz, points, plane, order, solver, endPaddingUm, openEmsCellsPerWavelength)).RootElement.Clone());
        return doc;

        // (u, v) in the plane and w along its normal, as a world point and as a world size
        C3dPoint3 At(long u, long v, long w) => plane switch
        {
            C3dPlane.YZ => new(w * Um, u * Um, v * Um), C3dPlane.XZ => new(u * Um, w * Um, v * Um), _ => new(u * Um, v * Um, w * Um),
        };
        C3dPoint3 Extent(long u, long v, long w) => At(u, v, w);

        static C3dPortAnnulus Bound(C3dPortAnnulus a)
        {
            C3dBindings.SetExpr(a, C3dBindings.Fields.Single(f => f.Owner == typeof(C3dPortAnnulus) && f.Property == nameof(C3dPortAnnulus.Inner)), 0,
                                new C3dExpr("pin_d/2", "Mm"));
            C3dBindings.SetExpr(a, C3dBindings.Fields.Single(f => f.Owner == typeof(C3dPortAnnulus) && f.Property == nameof(C3dPortAnnulus.Outer)), 0,
                                new C3dExpr("bore_d/2", "Mm"));
            return a;
        }
    }

    /// <summary>Every face PEC: the coax's two ends on the box with no padding, so each ring lies on a face; 0.2 mm of air round
    /// the shell's sides, so each face has something besides metal to select.</summary>
    private static string PalaceSetupJson(double startGHz, double stopGHz, int points, C3dPlane plane, int? order, string solver, long endPaddingUm,
                                           int? openEmsCellsPerWavelength)
    {
        string Pad(string axis) => (plane, axis) is (C3dPlane.YZ, "X") or (C3dPlane.XZ, "Y") or (C3dPlane.XY, "Z")
            ? endPaddingUm.ToString(System.Globalization.CultureInfo.InvariantCulture) : "200";
        string block = solver == "Emerge" ? "\"EMerge\": { \"Quality\": \"Accurate\" }"
            : solver == "OpenEms" ? "\"OpenEms\": { \"GradingRatio\": 1.15" + (openEmsCellsPerWavelength is { } cpw ? $", \"CellsPerWavelength\": {cpw}" : "") + " }"
            : "\"Palace\": { \"Quality\": \"Draft\", \"LinearSolver\": \"Direct\"" + (order is { } o ? $", \"ElementOrder\": {o}" : "") + " }";
        return $$"""
        {
          "FormatVersion": 1, "Name": "{{solver}}",
          "Frequency": { "StartExpr": "{{startGHz}}", "StopExpr": "{{stopGHz}}", "StepExpr": "", "NumPoints": {{points}}, "Mode": "PointCount",
                         "Kind": "Linear", "StartUnit": "GHz", "StopUnit": "GHz", "StepUnit": "Hz" },
          "Solver3D": "{{solver}}",
          "AirBox": { "XMin": { "PaddingUm": {{Pad("X")}}, "Boundary": "Pec" }, "XMax": { "PaddingUm": {{Pad("X")}}, "Boundary": "Pec" },
                      "YMin": { "PaddingUm": {{Pad("Y")}}, "Boundary": "Pec" }, "YMax": { "PaddingUm": {{Pad("Y")}}, "Boundary": "Pec" },
                      "ZMin": { "PaddingUm": {{Pad("Z")}}, "Boundary": "Pec" }, "ZMax": { "PaddingUm": {{Pad("Z")}}, "Boundary": "Pec" } },
          {{block}}
        }
        """;
    }

    /// <summary>Writes the coax into a workspace with copper and PTFE, and returns its path.</summary>
    internal static string Save(string root, C3dDocument doc, string cell = "Coax")
    {
        string ws = Path.Combine(root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "PTFE", Epsr = 2.1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private IReadOnlyList<C3dPortResult> Resolve(C3dDocument doc)
    {
        string path = Save(_root, doc);
        var read = C3dPersistence.LoadFromFile(path);
        var e = C3dElaborator.ElaborateOnce(read, path, null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var setup = C3dSetups.Read(read).Single().Setup!;
        var ctx = C3dProblemAssembly.PortContext(setup, read, e, C3dProblemAssembly.AirBox(setup, e, out _));
        return C3dPorts.Resolve(read, ctx);
    }

    // ── gate 1 — the format ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheRing_RoundTrips_WithItsExpressions_AndAPortWithoutOneWritesNoKey()
    {
        foreach (var plane in new[] { C3dPlane.XY, C3dPlane.YZ, C3dPlane.XZ })
        {
            string path = Save(_root, Coax(plane));
            string first = File.ReadAllText(path);
            Assert.Contains("\"Annulus\"", first);
            Assert.Contains("\"Expr\": \"pin_d/2\"", first);
            var again = C3dPersistence.LoadFromFile(path);
            C3dPersistence.SaveToFile(path, again);
            Assert.Equal(first, File.ReadAllText(path));
            Assert.Equal(new C3dExpr("bore_d/2", "Mm"), C3dBindings.GetExpr(again.Ports[0].Annulus!, nameof(C3dPortAnnulus.Outer), 0));
        }

        var plain = Coax();
        plain.Ports[0].Annulus = null;
        plain.Ports[0].Rect = new C3dRect { Min = new C3dPoint2(0, 0), Size = new C3dPoint2(Um, Um) };
        string text = File.ReadAllText(Save(_root, plain));
        Assert.Equal(1, text.Split("\"Annulus\"").Length - 1);       // only port 2's
    }

    // ── gate 2 — inference, polarity and the refusals ───────────────────────────────────────────

    [Theory]
    [InlineData(C3dPlane.YZ, 0)]
    [InlineData(C3dPlane.XZ, 1)]
    [InlineData(C3dPlane.XY, 2)]
    public void Gate2_EachPlane_PinIsPositive_ShellNegative_TheSquareBoundsTheRing_AndFlipReverses(C3dPlane plane, int normal)
    {
        var doc = Coax(plane);
        var rs = Resolve(doc);
        foreach (var r in rs) Assert.Null(r.Refusal);
        var p1 = rs[0].Resolved!;
        output.WriteLine(C3dPortReports.Describe(rs[0]));
        Assert.Equal(("pin", "shell"), (p1.PositiveObject, p1.NegativeObject));
        Assert.Equal(PinUm * 1e-6, p1.Annulus!.InnerRadiusM, 12);
        Assert.Equal(BoreUm * 1e-6, p1.Annulus.OuterRadiusM, 12);
        Assert.False(p1.Annulus.Outward);
        Assert.Equal(normal, Em3dAnnulus.NormalAxis(p1.Min, p1.Max));
        var (u, v) = Em3dAnnulus.PlaneAxes(normal);
        foreach (int ax in new[] { u, v })
        {
            Assert.Equal(-BoreUm * 1e-6, Em3dAnnulus.Get(p1.Min, ax), 12);
            Assert.Equal(BoreUm * 1e-6, Em3dAnnulus.Get(p1.Max, ax), 12);
        }
        // each ring's reference plane faces into the coax: +normal at the near end, −normal at the far end
        Assert.Equal(1.0, Em3dAnnulus.Get(p1.ReferencePlane.Normal, normal));
        Assert.Equal(-1.0, Em3dAnnulus.Get(rs[1].Resolved!.ReferencePlane.Normal, normal));
        Assert.Contains("its inner circle lies on 'pin' and its outer circle on 'shell'", rs[0].Reason);

        doc.Ports[0].Flip = true;
        var flipped = Resolve(doc)[0].Resolved!;
        Assert.Equal(("shell", "pin", true), (flipped.PositiveObject, flipped.NegativeObject, flipped.Annulus!.Outward));
    }

    [Fact]
    public void Gate2_TheRefusals_SayWhatTheCirclesFound()
    {
        string Refusal(Action<C3dPort> change)
        {
            var doc = Coax();
            change(doc.Ports[0]);
            return Resolve(doc)[0].Refusal ?? "";
        }
        static void Radii(C3dPort p, long inner, long outer)
            => p.Annulus = new C3dPortAnnulus { Centre = new C3dPoint2(0, 0), Inner = inner * Um, Outer = outer * Um };

        string tooSmall = Refusal(p => Radii(p, 150, BoreUm));
        output.WriteLine(tooSmall);
        Assert.Contains("P1's ring crosses 'pin' at r = 0.176 mm", tooSmall);
        string tooLarge = Refusal(p => Radii(p, 250, BoreUm));
        Assert.Contains("P1's inner circle (r = 0.25 mm) lies on no conductor: the nearest, 'pin', is up to 0.05 mm from it.", tooLarge);

        Assert.Contains("P1's two circles both lie on 'shell'", Refusal(p => Radii(p, 680, 690)));
        Assert.Contains("needs an inner radius above zero and an outer radius above it", Refusal(p => Radii(p, BoreUm, PinUm)));
        Assert.Contains("P1 states both Annulus and Kind Wave", Refusal(p => p.Kind = Em3dPortKind.Wave));
        Assert.Contains("P1 states its Positive end only", Refusal(p => p.Positive = "pin"));
        Assert.Contains("neither lies on its inner circle", Refusal(p => (p.Positive, p.Negative) = ("pin", "pin")));

        // stated the other way round is the coax with its polarity reversed, not a refusal
        var doc = Coax();
        (doc.Ports[0].Positive, doc.Ports[0].Negative) = ("shell", "pin");
        var stated = Resolve(doc)[0];
        Assert.Null(stated.Refusal);
        Assert.True(stated.Resolved!.Annulus!.Outward);
    }

    [Fact]
    public void Gate2_ExplainNamesTheRing_AndItsTemImpedance_AsANote()
    {
        string path = Save(_root, Coax());
        var doc = C3dPersistence.LoadFromFile(path);
        var e = C3dElaborator.ElaborateOnce(doc, path, null);
        var report = C3dPortReports.For(doc, e)[0].Result;
        string said = C3dPortReports.Describe(report);
        output.WriteLine(said);
        Assert.Contains("P1 (coaxial, a = 0.2 mm, b = 0.67 mm, ln(b/a) = 1.209) runs from 'shell' to 'pin' radially", said);
        // η₀/(2π√2.1)·ln(3.35) = 50.02 Ω
        Assert.Contains("As a coax filled with 'PTFE' (εr 2.1), its ring is 50.02 Ω; its Z0 is 50 Ω.", said);
    }

    // ── R-cxp1-3/-4 — lowered for Palace in any plane ───────────────────────────────────────────

    [Theory]
    [InlineData(C3dPlane.YZ)]
    [InlineData(C3dPlane.XZ)]
    [InlineData(C3dPlane.XY)]
    public void TheRing_LowersToARingSurface_AndAPalaceCoaxialLumpedPort(C3dPlane plane)
    {
        string path = Save(_root, Coax(plane));
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, path);
        var assembled = C3dProblemAssembly.Assemble(setup, doc, path, null);
        Assert.True(assembled.Ok, assembled.Refusal);
        var problem = assembled.Problem!;
        Assert.Empty(problem.Validate());
        var settings = PalaceSettings.Default with { AdaptiveMaxIterations = 0 };
        var low = GmshGeoWriter.Write(problem, settings);
        Assert.True(low.Ok, low.Refusal);
        int rings = low.Geo!.Split("BooleanDifference{ Surface{d1}; Delete; }{ Surface{d2}; Delete; }").Length - 1;
        Assert.Equal(2, rings);
        if (plane == C3dPlane.XY) Assert.DoesNotContain("Rotate {{", low.Geo);
        else Assert.Contains(plane == C3dPlane.YZ ? "Rotate {{0, 1, 0}, {0, 0, 0}, Pi/2}" : "Rotate {{1, 0, 0}, {0, 0, 0}, -Pi/2}", low.Geo);

        var config = PalaceConfigWriter.Write(problem, low.Groups, settings);
        Assert.True(config.Ok, config.Refusal);
        var ports = JsonDocument.Parse(config.Json!).RootElement.GetProperty("Boundaries").GetProperty("LumpedPort");
        Assert.Equal(2, ports.GetArrayLength());
        foreach (var port in ports.EnumerateArray())
            Assert.Equal("-R", port.GetProperty("Direction").GetString());      // shell to pin: inward
    }

    /// <summary>brief-cxp-3 — inside the box the pin's end cap is a surface of the meshed space in the ring's plane: the ring's
    /// claim subtracts its inner circle's box, so the cap is never part of the port (on the box's wall there is nothing to
    /// subtract, and the same query takes nothing).</summary>
    [Fact]
    public void ARingInsideTheModel_ClaimsItsRingAndNotThePinsEndCap()
    {
        string path = Save(_root, Coax(C3dPlane.YZ, endPaddingUm: 500));
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, path);
        var problem = C3dProblemAssembly.Assemble(setup, doc, path, null).Problem!;
        var low = GmshGeoWriter.Write(problem, PalaceSettings.Default with { AdaptiveMaxIterations = 0 });
        Assert.True(low.Ok, low.Refusal);
        foreach (string x in new[] { "0", "20000" })
            Assert.Contains($"x[] = Surface In BoundingBox{{{x} - e, -200 - e, -200 - e, {x} + e, 200 + e, 200 + e}};", low.Geo);
    }

    /// <summary>brief-cxp-3 R-cxp3-1 — EMerge is handed the ring (centre, the axis it faces, radii, which circle is negative) and
    /// no rectangle; its run table names the radial lumped port.</summary>
    [Fact]
    public void EMerge_IsHandedTheRing_AndItsTableNamesTheRadialPort()
    {
        string path = Save(_root, Coax(C3dPlane.XZ, solver: "Emerge"));
        var doc = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(doc).Single().Setup!, path);
        var problem = C3dProblemAssembly.Assemble(setup, doc, path, null).Problem!;
        var low = EmergeScriptWriter.Write(problem, EmergeSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        var ports = System.Text.Json.JsonDocument.Parse(low.ProblemJson!).RootElement.GetProperty("ports");
        var ring = ports[0].GetProperty("annulus");
        Assert.Equal([0.0, 1.0, 0.0], ring.GetProperty("normal").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal(200e-6, ring.GetProperty("inner").GetDouble(), 12);
        Assert.Equal(670e-6, ring.GetProperty("outer").GetDouble(), 12);
        Assert.False(ring.GetProperty("outward").GetBoolean());
        Assert.False(ports[0].TryGetProperty("origin", out _));
        Assert.Contains(EmergeLoweringRules.Mappings(problem, EmergeSettings.Default, 1e-4), r => r.Mapping.StartsWith("a coaxial lumped port, number 1", StringComparison.Ordinal));
    }

    /// <summary>brief-cxp-4 R-cxp4-4 — on a face of the box openEMS's wall would short the ring: refused, saying to move it in.</summary>
    [Fact]
    public void openEMS_RefusesARingOnAFaceOfTheBox()
    {
        var written = OpenEms(Coax(solver: "OpenEms"));
        Assert.Contains("P1 is a coaxial port on a face of the air box.", written.Refusal);
    }

    /// <summary>brief-cxp-4 R-cxp4-1…3 — inside the box: the conducting slab with κ = ln(b/a)/(2π·Z0·t), the radial source, and the
    /// two probes, all on the ring's grid plane.</summary>
    [Fact]
    public void openEMS_WritesARingInsideTheBox_AsSourceSlabAndProbes()
    {
        var written = OpenEms(Coax(solver: "OpenEms", endPaddingUm: 500));
        Assert.True(written.Ok, written.Refusal);
        string xml = written.Model!;
        Assert.Contains("Name=\"port1_resist\"", xml);
        Assert.Contains("<CylindricalShell Priority=", xml);
        Assert.Contains("Name=\"port1_u\"", xml);
        Assert.Contains("Name=\"port1_i\"", xml);
        // κ = ln(0.67/0.2)/(2π · 50 Ω · t): the slab's conductance is the ring's 50 Ω
        double kappa = double.Parse(System.Text.RegularExpressions.Regex.Match(xml[xml.IndexOf("port1_resist", StringComparison.Ordinal)..], "Kappa=\"([^,\"]+)").Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture);
        output.WriteLine($"κ = {kappa} S/m");
        Assert.InRange(kappa, 1.0, 1e5);
        Assert.Contains("(sqrt((y-0)^2+(z-0)^2)&gt;=0.0002)*(sqrt((y-0)^2+(z-0)^2)&lt;=0.00067)", written.PortFiles[0]);   // the radial source, XML-escaped
        output.WriteLine(written.Notes.Single(n => n.StartsWith("Port 1 is coaxial", StringComparison.Ordinal)));
    }

    private CsxcadLowering OpenEms(C3dDocument doc)
    {
        string path = Save(_root, doc);
        var read = C3dPersistence.LoadFromFile(path);
        var setup = C3dSetups.ForRun(C3dSetups.Read(read).Single().Setup!, path);
        var assembled = C3dProblemAssembly.Assemble(setup, read, path, null);
        Assert.True(assembled.Ok, assembled.Refusal);
        var gridSettings = CemOpenEms.ResolveGrid(setup.OpenEms);
        var grid = FdtdGrid.Build(assembled.Problem!, gridSettings, long.MaxValue);
        return CsxcadWriter.Write(assembled.Problem!, grid, gridSettings, CemOpenEms.ResolveRun(setup.OpenEms));
    }
}
