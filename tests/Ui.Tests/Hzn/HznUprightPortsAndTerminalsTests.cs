using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-14 R-hzn14-2/-3 — Hertzon port sheets drawn as polylines (standing in a turned upright plane, or in an axis plane), an
/// upright polyline that is no port, and the Terminal solution. The fixtures were written for these tests: <c>upright-ports.hzn</c>
/// is a 30° microstrip with an upright port sheet at its end, a straight one whose port sheet is a polyline, and an upright fin;
/// <c>terminal-pair.hzn</c> is a coupled pair in a closed box whose alphabetically first trace is on the right.
/// </summary>
public sealed class HznUprightPortsAndTerminalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn14_" + Guid.NewGuid().ToString("N")[..8]);

    public HznUprightPortsAndTerminalsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    private static C3dDocument Translate(string path, string design)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        var t = HznToC3d.Translate(read.File!.Project, design);
        Assert.True(t.Ok, t.Refusal?.Render());
        return t.Document!;
    }

    [Fact]
    public void ASlantedPolylinePortSheet_IsALumpedPortTurnedAboutTheVertical()
    {
        var doc = Translate(Fixture("upright-ports.hzn"), "Upright");
        var port = doc.Ports.Single(p => p.Number == 1);
        Assert.Equal((C3dPlane.XZ, 30.0), (port.Plane, Math.Round(port.Angle, 2)));     // to the fixture's four decimals
        Assert.InRange(port.Rect.Size.U, 1_199_900, 1_200_100);               // 1.2 mm, to the fixture's four decimals
        Assert.Equal(508_000, port.Rect.Size.V);
        Assert.DoesNotContain(doc.Objects, o => o.Name == "P_Slant");          // the port took its sheet
    }

    [Fact]
    public void AnAxisPlaneRectanglePolyline_IsAPort()
    {
        var doc = Translate(Fixture("upright-ports.hzn"), "Upright");
        var port = doc.Ports.Single(p => p.Number == 2);
        Assert.Equal((C3dPlane.XZ, 0.0, -15_000_000L), (port.Plane, port.Angle, port.Offset));
        Assert.Equal(new C3dPoint2(8_000_000, 0), port.Rect.Min);
        Assert.Equal("h", C3dBindings.GetExpr(port.Rect, nameof(C3dRect.Size), 1)!.Expr);
    }

    [Fact]
    public void AnUprightPolylineThatIsNoPort_IsASheetTurnedByItsPlacement()
    {
        var doc = Translate(Fixture("upright-ports.hzn"), "Upright");
        var fin = Assert.IsType<C3dSheet>(doc.Objects.Single(o => o.Name == "Fin"));
        Assert.Equal(C3dPlane.XZ, fin.Plane);
        var turn = Assert.Single(fin.Placement.Rotate);
        Assert.Equal((C3dAxis.Z, 30.0), (turn.Axis, Math.Round(turn.Deg, 2)));
        Assert.Equal(new C3dPoint3(-10_000_000, -10_000_000, 0), fin.Placement.Origin);
        Assert.Equal(new C3dPoint2(2_309_401, 3_000_000), fin.Outline[2]);   // 2.3094 mm along the plane, 3 mm up
    }

    [Fact]
    public void ATerminalDesign_GivesTerminalsNumberedByPosition_NamedAsHertzon_AtTheRenormImpedance()
    {
        var doc = Translate(Fixture("terminal-pair.hzn"), "Pair");
        Assert.All(doc.Ports, p => Assert.Equal(Em3dPortKind.Wave, p.Kind));
        var terminals = doc.Ports.OrderBy(p => p.Name).SelectMany(p => p.Terminals!).ToList();
        Assert.Equal([(1, "P1_TraceB", "TraceB"), (2, "P1_TraceA", "TraceA"), (3, "P2_TraceB", "TraceB"), (4, "P2_TraceA", "TraceA")],
                     terminals.Select(t => (t.Number, t.Name, t.Conductor)));
        Assert.All(terminals, t => Assert.Equal("42", t.Z0));
        Assert.All(doc.Ports, p => Assert.Null(p.Reference));                  // the closed box's PEC faces, inferred by the run
    }

    [Fact]
    public void Export_WritesATurnedPortAsAPolyline_AndTerminalsAsOneWavePortOfATerminalDesign_AndEachReadsBack()
    {
        foreach (var (fixture, design) in new[] { ("upright-ports.hzn", "Upright"), ("terminal-pair.hzn", "Pair") })
        {
            var t = HznToC3d.Translate(HznFile.Read(Fixture(fixture)).File!.Project, design);
            var (doc, tech) = (t.Document!, t.Technology);
            var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = design, Technology = tech });
            Assert.True(written.Ok, HznExportDiagnostics.Refused(design, written.Refusals).Render());
            string path = Path.Combine(_root, fixture);
            HznExport.ToNewFile(written, path);
            var hzn = HznFile.Read(path).File!.Project.Designs[0];
            var back = Translate(path, design);
            if (design == "Upright")
            {
                Assert.Equal("CreatePolyline", hzn.Objects.First(o => o.Name.StartsWith("1", StringComparison.Ordinal)).History[0].Kind);
                Assert.Equal(30.0, Math.Round(back.Ports.Single(p => p.Number == 1).Angle, 2));
            }
            else
            {
                Assert.Equal("Terminal", hzn.Json.GetObject("settings")!.GetString("SolutionType"));
                Assert.Equal(2, hzn.Excitations.Count(x => x.Kind == "Wave Port"));
                Assert.Equal(4, back.Ports.Sum(p => p.Terminals!.Count));
            }
        }
    }
}
