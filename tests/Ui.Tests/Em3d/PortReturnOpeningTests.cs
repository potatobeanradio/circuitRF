using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr34-1 — a 3D port's return through an opening (R-4), through solid metal the technology does not mark (R-5),
//  and across the gap to same-layer ground (R-6, owner decision D1: one sheet, to the nearer side).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PortReturnOpeningTests : IDisposable
{
    private const int Dbu = 1000;
    private readonly string _root = Directory.CreateTempSubdirectory("dfr34-1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Mm(double v) => (long)Math.Round(v * 1000 * Dbu);
    private static RectShape Rect(LayerKey l, double x1, double y1, double x2, double y2)
        => new() { Layer = l, X1 = Mm(x1), Y1 = Mm(y1), X2 = Mm(x2), Y2 = Mm(y2) };

    private static LabelShape Port(string text, double xMm, double yMm, LayerKey layer, LayoutRotation direction) => new()
    {
        Layer = layer, PortLayer = layer, X = Mm(xMm), Y = Mm(yMm), Text = text, Height = Mm(0.2), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = direction,
    };

    private static Em3dGenerationResult Generate(Technology tech, IEnumerable<LayoutShape> shapes, Action<EmSetup>? edit = null)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var setup = new EmSetup
        {
            Name = "rp", LayoutRef = "rp/layout/rp.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        };
        edit?.Invoke(setup);
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/rp.clay", view, tech, Dbu), tech);
    }

    private static Em3dPort PortOf(Em3dGenerationResult g, int n) => g.Problem!.Ports.Single(p => p.Number == n);

    // ── R-4 — the field case: a pad's flat edge over a clearance in the first two inner planes ──────────────────

    // The shipped six-layer board, drawing layers Top 1, Inner 1 10, Inner 2 11, Inner 3 12, Inner 4 13, Bottom 2; Inner 1
    // to 3 marked as ground references, as on the board this came from.
    private static readonly LayerKey Top = new(1, 0), In1 = new(10, 0), In2 = new(11, 0), In3 = new(12, 0);

    private static Technology SixLayer()
    {
        var t = ShippedTechnologies.Load("pcb-6layer_FR-4_1.6mm_1oz");
        foreach (var l in t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
            l.IsGroundReference = l.Name is "Inner 1" or "Inner 2" or "Inner 3";
        return t;
    }

    /// <summary>A plane over the board with a 2.5 mm square clearance centred on the pad.</summary>
    private static PolygonShape Clearance(LayerKey l) => new()
    {
        Layer = l,
        Xy = [Mm(-2), Mm(-5), Mm(12), Mm(-5), Mm(12), Mm(5), Mm(-2), Mm(5)],
        Holes = [[Mm(7.5), Mm(-1.25), Mm(10), Mm(-1.25), Mm(10), Mm(1.25), Mm(7.5), Mm(1.25)]],
    };

    /// <summary>A 0.3 mm feed from x = 0 into a 1.5 mm pad whose flat right edge, at x = 9.5 mm, is port 2's end.</summary>
    private static List<LayoutShape> PadBoard() =>
    [
        Rect(Top, 0, -0.15, 8, 0.15),
        Rect(Top, 8, -0.75, 9.5, 0.75),
        Port("P1", 0, 0, Top, LayoutRotation.R0),
        Port("P2", 9.5, 0, Top, LayoutRotation.R180),
        Clearance(In1), Clearance(In2),
        Rect(In3, -2, -5, 12, 5),
    ];

    /// <summary>The opening in Inner 1 and Inner 2 is walked through to Inner 3, and a note names each opening and its size;
    /// port 1, over solid Inner 1, still returns there.</summary>
    [Fact]
    public void APadOverAClearance_ReturnsThroughThePlaneBeyondIt_NamingTheOpening()
    {
        var g = Generate(SixLayer(), PadBoard());

        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith("Inner 1/", PortOf(g, 1).NegativeObject, StringComparison.Ordinal);
        Assert.StartsWith("Inner 3/", PortOf(g, 2).NegativeObject, StringComparison.Ordinal);
        foreach (string layer in new[] { "Inner 1", "Inner 2" })
            Assert.Contains(g.Notes, n => n.StartsWith($"Port 2's return passes through an opening in '{layer}'", StringComparison.Ordinal) &&
                                          n.Contains("2500 µm", StringComparison.Ordinal));
    }

    /// <summary>Gate 1 — New 3D View from Layout brings port 2 in, its sheet reaching down to Inner 3, and posts the note.</summary>
    [Fact]
    public void NewFromLayout_BringsInThePadsPort_ReturningThroughTheThirdPlane()
    {
        var (cell, c3d, cws) = Board(SixLayer(), PadBoard());
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);

        Assert.Empty(made.PortsLeftOut);
        var p2 = made.Document!.Ports.Single(p => p.Number == 2);
        Assert.Equal(C3dPlane.YZ, p2.Plane);
        Assert.Contains(made.PortsMoved, m => m.Contains("opening in 'Inner 2'", StringComparison.Ordinal));

        var e = new C3dElaborator().Elaborate(made.Document, c3d, cws);
        var r = C3dPorts.Resolve(p2, made.Document.DbuPerMicron, C3dProblemAssembly.PortContext(null, made.Document, e, null));
        Assert.True(r.Resolved is not null, r.Refusal);
        Assert.Contains("/Inner 3/", r.Resolved!.NegativeObject, StringComparison.Ordinal);
    }

    // ── R-5 — unmarked solid metal before any marked plane ──────────────────────────────────────────

    private static readonly LayerKey L1 = new(1, 0), L2 = new(2, 0), L3 = new(3, 0), L4 = new(4, 0);

    private static Technology FourLayer(params string[] flagged)
    {
        var t = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        foreach (var l in t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
            l.IsGroundReference = flagged.Contains(l.Name);
        return t;
    }

    private static List<LayoutShape> Line(LayerKey layer, params LayoutShape[] planes) =>
    [
        Rect(layer, 0, -0.15, 10, 0.15),
        Port("P1", 0, 0, layer, LayoutRotation.R0),
        Port("P2", 10, 0, layer, LayoutRotation.R180),
        .. planes,
    ];

    /// <summary>Only Bottom is marked; Inner 2's pour covers the whole sheet first, so it is the return, with a note. A pour
    /// covering half the line's end is still in the sheet's way.</summary>
    [Fact]
    public void UnmarkedMetalCoveringTheWholeSheet_IsTheReturn_PartOfItStillRefuses()
    {
        var g = Generate(FourLayer("Bottom Copper (1 oz)"), Line(L1, Rect(L3, -2, -5, 12, 5), Rect(L4, -2, -5, 12, 5)));
        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith("Inner 2/", PortOf(g, 1).NegativeObject, StringComparison.Ordinal);
        Assert.Contains(g.Notes, n => n.Contains("'Inner 2' is not marked as a ground reference", StringComparison.Ordinal));

        var partial = Generate(FourLayer("Bottom Copper (1 oz)"), Line(L1, Rect(L3, -2, 0, 12, 5), Rect(L4, -2, -5, 12, 5)));
        Assert.Contains("would cross metal on 'Inner 2'", partial.Refusal, StringComparison.Ordinal);
    }

    // ── R-6 — a coplanar return ─────────────────────────────────────────────────────────────────────

    /// <summary>A grounded coplanar line, its gaps 200 µm (+y) and 250 µm (−y): Return = Same layer is one horizontal sheet
    /// at the copper's mid-height across the nearer gap, the line's width long, from the ground to the line.</summary>
    [Fact]
    public void ASameLayerReturn_IsOneSheetAcrossTheNearerGap()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var top = new LayerKey(1, 0);
        List<LayoutShape> shapes =
        [
            Rect(top, 0, -0.3, 10, 0.3),
            Rect(top, 0, 0.5, 10, 3), Rect(top, 0, -3, 10, -0.55),
            Port("P1", 0, 0, top, LayoutRotation.R0),
            Port("P2", 10, 0, top, LayoutRotation.R180),
            Rect(new LayerKey(2, 0), 0, -3, 10, 3),
        ];
        var g = Generate(tech, shapes, s => s.Ports3D = [new EmPort3D(1, Return: EmPort3D.SameLayerReturn)]);

        Assert.True(g.Ok, g.Refusal);
        var p1 = PortOf(g, 1);
        Assert.Equal(p1.Min.Z, p1.Max.Z, 12);
        Assert.Equal(0, p1.Min.X, 12);
        Assert.Equal(0.6e-3, p1.Max.X, 12);
        Assert.Equal(0.3e-3, p1.Min.Y, 9);
        Assert.Equal(0.5e-3, p1.Max.Y, 9);
        Assert.Equal(new Point3(0, -1, 0), p1.Direction);
        Assert.Equal("Top Copper (1 oz)", g.PortReturns[1].Layer);
        Assert.Contains(g.Notes, n => n.StartsWith("Port 1 at its line's end is a horizontal lumped sheet", StringComparison.Ordinal) &&
                                      n.Contains("+y side, the nearer of the two", StringComparison.Ordinal));
        // Port 2 still walks down to the plane.
        Assert.StartsWith("Bottom Copper (1 oz)/", PortOf(g, 2).NegativeObject, StringComparison.Ordinal);

        var none = Generate(tech, [.. shapes.Take(1), .. shapes.Skip(3)], s => s.Ports3D = [new EmPort3D(1, Return: EmPort3D.SameLayerReturn)]);
        Assert.Contains("a coplanar return", none.Refusal, StringComparison.Ordinal);
    }

    /// <summary>Designer round 35 — a return stated on the layout label is the planar kernel's, and its refusals are about that
    /// kernel's calibration standards. In 3D, coplanar on the label builds the coplanar sheet; with no straight gap there the
    /// port walks to its plane; a second conductor on the label leaves the walk alone.</summary>
    [Fact]
    public void AReturnOnTheLabel_IsNoEdgePortRefusalIn3D()
    {
        var tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var top = new LayerKey(1, 0);
        var coplanar = Port("P1", 0, 0, top, LayoutRotation.R0);
        coplanar.PortReference = LayoutPortReference.CoplanarGround;
        List<LayoutShape> shapes =
        [
            Rect(top, 0, -0.3, 10, 0.3),
            Rect(top, 0, 0.5, 10, 3), Rect(top, 0, -3, 10, -0.55),
            coplanar,
            Port("P2", 10, 0, top, LayoutRotation.R180),
            Rect(new LayerKey(2, 0), 0, -3, 10, 3),
        ];
        var g = Generate(tech, shapes);
        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(PortOf(g, 1).Min.Z, PortOf(g, 1).Max.Z, 12);
        Assert.Contains(g.Notes, n => n.Contains("says it returns through a coplanar ground conductor", StringComparison.Ordinal));

        // No ground beside it: the walk answers, down to the plane.
        var alone = Generate(tech, [shapes[0], coplanar, shapes[4], shapes[5]]);
        Assert.True(alone.Ok, alone.Refusal);
        Assert.StartsWith("Bottom Copper (1 oz)/", PortOf(alone, 1).NegativeObject, StringComparison.Ordinal);

        var second = Port("P1", 0, 0, top, LayoutRotation.R0);
        second.PortReference = LayoutPortReference.SecondConductor;
        var walked = Generate(tech, [shapes[0], second, shapes[4], shapes[5]]);
        Assert.True(walked.Ok, walked.Refusal);
    }

    private (string Cell, string C3d, string Cws) Board(Technology tech, List<LayoutShape> shapes)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), tech);
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = CellFolder.CreateCellFolder(ws, "Board");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        CellCreate.WriteLayoutView(dir, "Board", view);
        return (dir, Path.Combine(CellFolder.SubFolderPath(dir, ViewType.ThreeD), "Board" + C3dPersistence.Extension), cws);
    }
}
