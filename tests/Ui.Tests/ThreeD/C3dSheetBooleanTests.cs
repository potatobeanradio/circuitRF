// ================================================================
//  C3dSheetBooleanTests.cs — brief-hzn-10's gate: a Boolean of coplanar sheets. One test per claim: the polygon arithmetic
//  (union, notch, hole), several pieces as one object, the refusals (empty, mixed, out of one plane), no geometry worker,
//  an expression moving the result, and a dry run on EMerge and Palace that solves nothing.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class C3dSheetBooleanTests : IDisposable
{
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn10-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── the polygon arithmetic ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Unite_OfTwoOverlappingRectangles_IsTheirUnion()
    {
        var r = C3dSheetBooleans.Result(Op(C3dBooleanOp.Unite, Rect("", 0, 0, 100, 100), Rect("b", 50, 50, 100, 100)));
        var piece = Assert.Single(r.Pieces);
        Assert.Empty(piece.Holes);
        Assert.Equal(8, piece.Outline.Count);
        Assert.Equal(100.0 * 100 * 2 - 50 * 50, Area(piece.Outline));
    }

    [Fact]
    public void Subtract_OfANotch_IsAPolygonWithTheNotch()
    {
        var r = C3dSheetBooleans.Result(Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 100), Rect("notch", 40, 0, 20, 30)));
        var piece = Assert.Single(r.Pieces);
        Assert.Empty(piece.Holes);
        Assert.Equal(8, piece.Outline.Count);
        Assert.Equal(100.0 * 100 - 20 * 30, Area(piece.Outline));
    }

    [Fact]
    public void Subtract_OfAnInteriorRectangle_IsAPolygonWithAHole()
    {
        var r = C3dSheetBooleans.Result(Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 100), Rect("cut", 30, 30, 40, 20)));
        var piece = Assert.Single(r.Pieces);
        Assert.Equal(4, piece.Outline.Count);
        var hole = Assert.Single(piece.Holes);
        Assert.Equal(40.0 * 20, Math.Abs(Area(hole)));
    }

    // ── into the problem ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADisjointResult_IsTwoSheets_WithOneProvenance()
    {
        var e = Elaborate(Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 20), Rect("gap", 40, -10, 20, 40), name: "strip"));
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Equal(["strip", "strip#2"], e.Sheets.Select(s => s.Name));
        Assert.All(e.Sheets, s => Assert.Equal("strip", e.Provenance[s.Name].ObjectName));
    }

    [Fact]
    public void AnEmptyResult_IsRefused()
    {
        var b = Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 20), Rect("all", -10, -10, 200, 200), name: "strip");
        var e = Elaborate(b);
        Assert.Contains(C3dBooleans.EmptyResult(b, "strip"), e.Refusals);
        Assert.Empty(e.Sheets);
    }

    [Fact]
    public void SheetsAndSolids_AreRefused()
    {
        var b = Op(C3dBooleanOp.Unite, Rect("", 0, 0, 100, 20), new C3dBox { Name = "post", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(10 * Um, 10 * Um, 10 * Um) },
                   name: "strip");
        var found = C3dValidation.Validate(Doc(b));
        Assert.Contains(found, d => d.Id == "c3d.operation.sheet-mixed" && d.Render().Contains("'post' is a Box"));
        Assert.False(C3dSheetBooleans.IsSheetBoolean(b));
        Assert.Equal(C3dBooleans.MixedRefused("strip", "post"), C3dBooleans.SelectionRefusal([Rect("strip", 0, 0, 1, 1), b.Tools[0]]));
    }

    /// <summary>Another offset, another plane, and a placement off the plane: each refused by validation, naming the operand.</summary>
    [Theory]
    [InlineData("offset", "lies at another offset along z than the Blank")]
    [InlineData("plane", "lies in the XZ plane and the Blank in XY")]
    [InlineData("placed", "is moved along z, out of its plane")]
    public void DifferentPlanesOrOffsets_AreRefused(string how, string said)
    {
        var tool = Rect("t", 50, 0, 100, 20);
        switch (how)
        {
            case "offset": tool.Offset = 5 * Um; break;
            case "plane": tool.Plane = C3dPlane.XZ; break;
            default: tool.Placement = new C3dPlacement { Origin = new C3dPoint3(0, 0, 5 * Um) }; break;
        }
        var b = Op(C3dBooleanOp.Unite, Rect("", 0, 0, 100, 20), tool, name: "strip");
        var d = Assert.Single(C3dValidation.Validate(Doc(b)), d => d.Id == "c3d.operation.sheet-plane");
        Assert.StartsWith($"'t' in 'strip' {said}.", d.Render());
        Assert.Contains(Elaborate(b).Refusals, r => r.StartsWith($"'t' in 'strip' {said}.", StringComparison.Ordinal));
    }

    [Fact]
    public void ItElaborates_WithNoGeometryWorker()
    {
        using var absent = new GeometryKernel(new GeometryKernelOptions
        {
            Locate = () => new GeometryKernelLocation(null, null, "not found", GeometryKernelAbsence.NotBuilt, "Not built here.", "Build it."),
            DiskCache = false,
        });
        var b = Op(C3dBooleanOp.Unite, Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 100), Rect("notch", 40, 0, 20, 30)), Rect("feed", 45, -50, 10, 90),
                   name: "patch");
        Assert.Empty(C3dKernelUse.Of(Doc(b)));
        var e = Elaborate(b, absent);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        Assert.Single(e.Sheets);
        Assert.Equal(0, absent.RequestsSent);
    }

    /// <summary>D5 — the notch's width is a variable: changing it moves the result, with nothing in the file frozen.</summary>
    [Fact]
    public void ChangingAVariable_MovesTheResult()
    {
        double AreaAt(string n)
        {
            var notch = Rect("notch", 40, 0, 20, 30);
            notch.Rect!.Exprs = new() { ["Size"] = [new C3dExpr("n", "Um"), null] };
            var doc = Doc(Op(C3dBooleanOp.Subtract, Rect("", 0, 0, 100, 100), notch, name: "patch"));
            doc.Variables = [new C3dVariable { Name = "n", Expression = n, Unit = "Um" }];
            var e = Elaborate(doc);
            Assert.True(e.Ok, string.Join(" ", e.Refusals));
            return Area(Assert.Single(e.Sheets).Outline) / 1e-12;   // µm²
        }
        Assert.Equal(100.0 * 100 - 20 * 30, AreaAt("20"), 6);
        Assert.Equal(100.0 * 100 - 50 * 30, AreaAt("50"), 6);
    }

    // ── the solvers need nothing new ─────────────────────────────────────────────────────────────

    /// <summary>R-hzn10-2 — a notched patch with an inset feed, driven by a lumped port, dry-run (nothing solved) on EMerge and on
    /// Palace through the CLI: the result is an ordinary sheet to both.</summary>
    [Theory]
    [InlineData(Em3dSolverId.Emerge)]
    [InlineData(Em3dSolverId.Palace)]
    public void ADryRun_OnEachSolver_TakesTheResultAsASheet(Em3dSolverId solver)
    {
        var patch = Op(C3dBooleanOp.Unite, Op(C3dBooleanOp.Subtract, Rect("", 500, 800, 2000, 1400, z: 200, mat: "Copper"), Rect("notch", 1300, 800, 400, 300, z: 200)),
                       Rect("feed", 1400, 0, 200, 1200, z: 200), name: "patch");
        var doc = new C3dDocument
        {
            SnapDbu = Um,
            Objects =
            [
                new C3dBox { Name = "sub", Material = "FR4", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(3000 * Um, 3000 * Um, 200 * Um) },
                Rect("ground", 0, 0, 3000, 3000, z: 0, mat: "Copper"),
                patch,
            ],
            Ports = [new C3dPort { Number = 1, Name = "P1", Kind = CircuitRF.Engine.Em3d.Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = 0, Z0 = "50",
                                   Rect = new C3dRect { Min = new C3dPoint2(1400 * Um, 0), Size = new C3dPoint2(200 * Um, 200 * Um) } }],
            Setups = [EmSetupPersistence.ToEmbedded(new EmSetup
            {
                Name = "Driven", Solver3D = solver, Frequency = new FrequencySpec("2", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            })],
        };
        string path = Write(doc);
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "circuitRF.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var (exit, stdout, stderr) = CliProcess.Run(repo, [], "em", path, "--dry-run");
        Assert.True(exit == 0, stderr + stdout);
        Assert.DoesNotContain("error: ", stderr);
        Assert.Contains("Estimate:", stdout);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static C3dSheet Rect(string name, long u, long v, long su, long sv, long z = 0, string? mat = null)
        => new() { Name = name, Material = mat, Plane = C3dPlane.XY, Offset = z * Um,
                   Rect = new C3dRect { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(su * Um, sv * Um) } };

    private static C3dBoolean Op(C3dBooleanOp op, C3dObject blank, C3dObject tool, string name = "")
    {
        if (blank is C3dSheet { Material: null } s) s.Material = "Copper";
        return new C3dBoolean { Name = name, Op = op, Blank = blank, Tools = [tool] };
    }

    /// <summary>Shoelace area of a ring, in the ring's own units squared (signed).</summary>
    private static double Area(IReadOnlyList<C3dPoint2> ring)
    {
        double a = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            a += (double)p.U / Um * q.V / Um - (double)q.U / Um * p.V / Um;
        }
        return a / 2;
    }

    private static double Area(IReadOnlyList<CircuitRF.Engine.Em3d.Point2> ring)
    {
        double a = 0;
        for (int i = 0; i < ring.Count; i++) a += ring[i].X * ring[(i + 1) % ring.Count].Y - ring[(i + 1) % ring.Count].X * ring[i].Y;
        return Math.Abs(a / 2);
    }

    private static C3dDocument Doc(C3dObject o) => new() { SnapDbu = Um, Objects = [o] };

    private C3dElaboration Elaborate(C3dObject o, GeometryKernel? kernel = null) => Elaborate(Doc(o), kernel);

    private C3dElaboration Elaborate(C3dDocument doc, GeometryKernel? kernel = null)
    {
        string path = Write(doc);
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!, ".cws");
        return C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(path), path, cws, kernel: kernel);
    }

    private string Write(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "FR4", Epsr = 4.4 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }
}
