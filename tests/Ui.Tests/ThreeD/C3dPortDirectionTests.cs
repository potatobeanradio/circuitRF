// ================================================================
//  C3dPortDirectionTests.cs — brief-emb-3's gates: a lumped port with both ends stated takes the axis its
//  ends face, on a plane and on a cylinder, even stopped short of its metal; one whose ends face no pair of
//  its edges is refused; check warns when the direction is not the one its conductors face and errors when
//  stated ends and inference choose different pairs; and the 3d-view reference names the result a run writes.
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Diagnostics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dPortDirectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-emb3-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Um = 1000;

    /// <summary>R-emb3-1 — an XY sheet stopped 1 µm short of two pads, both ends stated, runs along the axis the pads face:
    /// y for pads above and below it, x for pads either side. Before F5 both came out ±x.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStatedSheetStoppedShort_RunsAlongTheAxisItsPadsFace(bool alongY)
    {
        var doc = alongY
            ? new C3dDocument { Objects = [Sheet("a", 0, -100, 100, 100), Sheet("b", 0, 50, 100, 100)] }
            : new C3dDocument { Objects = [Sheet("a", -100, 0, 100, 100), Sheet("b", 50, 0, 100, 100)] };
        doc.Ports.Add(Port(alongY ? Rect(0, 1, 100, 48) : Rect(1, 0, 48, 100), positive: "b", negative: "a"));
        var r = Resolve(doc);
        Assert.Null(r.Refusal);
        Assert.Equal(alongY ? new Point3(0, 1, 0) : new Point3(1, 0, 0), r.Resolved!.Direction);
        Assert.Contains($"P1 runs along {(alongY ? "+y" : "+x")} across a 48 µm gap, 100 µm wide.", Findings(doc).Select(C3dPortChecks.Text));
    }

    /// <summary>R-emb3-1 — the cylinder branch: two copper blocks 1 µm off a gap on a cylinder, below and above it (axial) or either
    /// side of it (round the axis). Measured with the contact cut-off both kept the azimuthal default.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStatedGapOnACylinder_RunsTheWayItsEndsFace(bool axial)
    {
        // r = 20 mm about z; the gap spans -5° to +5° and z 0 to 2 mm. Its azimuth edges are at y = ±1743.1 µm.
        var doc = axial
            ? new C3dDocument { Objects = [Box("a", 19500, -2000, -1000, 1000, 4000, 999), Box("b", 19500, -2000, 2001, 1000, 4000, 999)] }
            : new C3dDocument { Objects = [Box("a", 19500, -4000, 0, 1000, 2255, 2000), Box("b", 19500, 1745, 0, 1000, 2255, 2000)] };
        var port = Port(Rect(0, 0, 1, 1), positive: "b", negative: "a");
        port.Cylinder = new C3dCylinderRect { Axis = C3dAxis.Z, Base = new C3dPoint3(0, 0, 0), Radius = 20_000 * Um, Length = 2000 * Um, StartDeg = -5, SweepDeg = 10 };
        doc.Ports.Add(port);
        var r = Resolve(doc);
        Assert.Null(r.Refusal);
        Assert.Equal(axial ? Em3dCylinderGapAxis.Axial : Em3dCylinderGapAxis.Azimuthal, r.Resolved!.Cylinder!.Along);
        Assert.Equal(1, r.Resolved.Cylinder.Sign);
    }

    /// <summary>brief-cxp-5 — a loop fed across its two open ends: a U of one conductor whose arms touch the port's left and right
    /// edges, the sheet between them open. It runs between the loop's two ends; the same sheet drawn ON a strip, whose centre lies
    /// on the metal, stays refused (its two ends are shorted).</summary>
    [Fact]
    public void ALoopFedAcrossItsOpenEnds_IsAPort_AndASheetOnAStripIsNot()
    {
        var loop = new C3dDocument
        {
            Objects = [new C3dSheet { Name = "loop", Material = "Copper", Plane = C3dPlane.XY, Offset = 0,
                                      Outline = [P(0, 0), P(100, 0), P(100, 300), P(400, 300), P(400, 0), P(500, 0), P(500, 400), P(0, 400)] }],
        };
        loop.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(100, 0, 300, 100) });
        var r = Resolve(loop);
        Assert.Null(r.Refusal);
        Assert.Equal(("loop", "loop"), (r.Resolved!.PositiveObject, r.Resolved.NegativeObject));
        Assert.Contains("fed across an open gap in it", r.Reason);

        var strip = new C3dDocument { Objects = [Sheet("strip", 0, 0, 500, 100)] };
        strip.Ports.Add(new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(100, 0, 300, 100) });
        Assert.Contains("both of its ends are one conductor", Resolve(strip).Refusal);

        static C3dPoint2 P(long u, long v) => new(u * Um, v * Um);
    }

    /// <summary>R-emb3-1 — two stated conductors over the middle of the sheet face no pair of its edges: refused, naming the port,
    /// both ends and that its direction is undecided, never ±u.</summary>
    [Fact]
    public void StatedEndsFacingNoEdge_AreRefused()
    {
        var doc = new C3dDocument { Objects = [Box("a", 45, 20, 100, 10, 10, 10), Box("b", 45, 20, 200, 10, 10, 10)] };
        doc.Ports.Add(Port(Rect(0, 0, 100, 50), positive: "b", negative: "a"));
        var r = Resolve(doc);
        Assert.Null(r.Resolved);
        Assert.StartsWith("P1 states 'b' and 'a' as its ends, and they do not face two opposite edges of it", r.Refusal);
        Assert.Contains("so its direction is undecided", r.Refusal);
    }

    /// <summary>R-emb3-2 — a port drawn across a y-gap (pads 1 µm off its bottom and top) but stated between the x pair beside it:
    /// it runs along x, and check says its conductors face each other along y.</summary>
    [Fact]
    public void AStatedPortDrawnAcrossTheOtherGap_IsWarnedNamingBothAxes()
    {
        var doc = FourPads(touching: false);
        var d = Assert.Single(Findings(doc), f => f.Id == C3dPortChecks.DirectionId);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.StartsWith("P1 runs along +x, but its conductors face each other along y: its bottom and top edges are 1 µm and 1 µm " +
                          "from 'south' and 'north'", C3dPortChecks.Text(d));
        Assert.DoesNotContain(Findings(doc), f => f.Id == C3dPortChecks.DisagreesId);
    }

    /// <summary>R-emb3-3 — the same port touching its y pads: inference would choose y, the stated ends x, and check refuses it
    /// with an error (through the CLI, so the wiring is the gate).</summary>
    [Fact]
    public void StatedEndsThatDisagreeWithInference_AreACheckError()
    {
        var doc = FourPads(touching: true);
        string path = WriteC3d(Workspace(), "Cell", doc);
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var (exit, stdout, stderr) = CliProcess.Run(repo, [], "check", path);
        Assert.Equal(1, exit);
        // (which of the two equal pads inference calls negative is its own business: the pair is what is checked)
        Assert.Contains("error: ", stderr);
        Assert.Contains("P1 states 'west' and 'east' as its ends, which puts it along x, but its edges touch ", stderr);
        Assert.Contains("which put it along y: the stated ends and what the port touches disagree.", stderr);
    }

    /// <summary>R-emb3-4 — the 3d-view reference names the files a run writes, as Em3dRunService names them: driven and static.</summary>
    [Fact]
    public void TheReference_NamesTheResultTheRunWrites()
    {
        string page = CircuitRF.Cli.DocumentSchema.Render(CircuitRF.Cli.DocumentSchema.Find("3d-view")!);
        string root = Path.Combine(_root, "results");
        string Spell(string pattern) => pattern.Replace("<file stem>", "board").Replace("<setup name>", "Driven")
                                               .Replace("<solver>", "palace").Replace("<N>", "15");
        var driven = C3dSetups.ForRun(new EmSetup { Name = "Driven", Solver3D = Em3dSolverId.Palace }, Path.Combine(_root, "board.c3d"));
        var es = C3dSetups.ForRun(new EmSetup { Name = "Driven", Solver3D = Em3dSolverId.Palace, Problem3D = Em3dProblemType.Electrostatic },
                                  Path.Combine(_root, "board.c3d"));
        var palace = PalaceSolver.Id;
        foreach (var (pattern, actual) in new[]
                 {
                     ("<file stem> <setup name>.<solver>.s<N>p", Path.GetFileName(Em3dRunService.SnpBasePath(root, driven, palace)) + ".s15p"),
                     ("<file stem> <setup name>.<solver>_em.npy", Em3dRunService.NpyKey(driven, palace) + ".npy"),
                     ("<file stem> <setup name>.<solver>/", Path.GetFileName(Em3dRunService.RunDirectory(root, driven, palace)) + "/"),
                     ("<file stem> <setup name>.<solver>_es.npy", Em3dRunService.NpyKey(es, palace) + ".npy"),
                     ("<file stem> <setup name>.<solver>_es/", Path.GetFileName(Em3dRunService.RunDirectory(root, es, palace)) + "/"),
                 })
        {
            Assert.Contains($"\"{pattern}\"", page);
            Assert.Equal(Spell(pattern), actual);
        }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 100 × 60 µm XY port with pads south and north of it (touching, or 1 µm off) and west and east of it 5 µm off,
    /// stated between west and east.</summary>
    private static C3dDocument FourPads(bool touching)
    {
        long off = touching ? 0 : 1;
        var doc = new C3dDocument
        {
            Objects = [Sheet("south", 0, -100 - off, 100, 100), Sheet("north", 0, 60 + off, 100, 100),
                       Sheet("west", -100, 0, 95, 60), Sheet("east", 105, 0, 95, 60)],
        };
        doc.Ports.Add(Port(Rect(0, 0, 100, 60), positive: "east", negative: "west"));
        return doc;
    }

    private static C3dPort Port(C3dRect rect, string positive, string negative)
        => new() { Number = 1, Name = "P1", Plane = C3dPlane.XY, Offset = 0, Rect = rect, Positive = positive, Negative = negative };

    private static C3dRect Rect(long u, long v, long du, long dv)
        => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };

    private static C3dSheet Sheet(string name, long u, long v, long du, long dv)
        => new() { Name = name, Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = Rect(u, v, du, dv) };

    private static C3dBox Box(string name, long x, long y, long z, long dx, long dy, long dz)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(dx * Um, dy * Um, dz * Um) };

    private C3dPortResult Resolve(C3dDocument doc)
    {
        var e = Elaborate(doc);
        return C3dPorts.Resolve(doc, C3dProblemAssembly.PortContext(null, doc, e, null)).Single();
    }

    private IReadOnlyList<Diagnostic> Findings(C3dDocument doc) => C3dPortChecks.For(doc, Elaborate(doc));

    private C3dElaboration Elaborate(C3dDocument doc)
    {
        var e = C3dElaborator.ElaborateOnce(doc, WriteC3d(Workspace(), "Cell", doc), null);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        return e;
    }

    /// <summary>A workspace whose default technology holds Copper.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Air", Epsr = 1 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }
}
