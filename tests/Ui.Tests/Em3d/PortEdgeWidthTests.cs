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

// brief-port-width-planar-and-c3d — a port's "Use edge width" box and Width, read by every run, and a .c3d keeping up
// with its layout's ports. The shipped two-layer RO4350B board: Top Copper over 508 µm to Bottom Copper. H = 508 µm.
// A 6 × 3 mm pour with a 0.3 mm line off its right edge; port 1 on the pour's 3 mm left edge, 0.5 mm below its centre,
// port 2 at the line's far end.
public sealed class PortEdgeWidthTests : IDisposable
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Bottom = new(2, 0);
    private const double H = 508e-6;
    private readonly string _root = Directory.CreateTempSubdirectory("pw1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static Technology Tech() => ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");

    private static LabelShape Port(string text, double xUm, double yUm, LayoutRotation direction, double? widthUm = null,
                                   bool? useEdge = null) => new()
    {
        Layer = Top, PortLayer = Top, X = Um(xUm), Y = Um(yUm), Text = text, Height = Um(200), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = direction, PortWidth = widthUm is { } w ? Um(w) : null,
        PortUseEdgeWidth = useEdge,
    };

    private static List<LayoutShape> Pour(double? statedUm = null, bool? useEdge = null, bool plane = false, double portYUm = 1000)
    {
        var shapes = new List<LayoutShape>
        {
            new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Um(6000), Y2 = Um(3000) },
            new RectShape { Layer = Top, X1 = Um(6000), Y1 = Um(1350), X2 = Um(9000), Y2 = Um(1650) },
            Port("1", 0, portYUm, LayoutRotation.R0, statedUm, useEdge),
            Port("2", 9000, 1500, LayoutRotation.R180),
        };
        if (plane) shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(9000), Y2 = Um(3000) });
        return shapes;
    }

    // ── R-pw1-0 — the checkbox's reading ───────────────────────────────────────────────────────

    /// <summary>Null reads as "no width stated": a port with no width uses its edge, one that states a width does not;
    /// setting the value null already reads as stores null, so an untouched .clay round-trips byte for byte.</summary>
    [Fact]
    public void TheCheckbox_NullReadsAsNoWidthStated_AndTheDefaultWritesNothing()
    {
        Assert.True(EdgePortSheet.UsesEdgeWidth(Port("1", 0, 0, LayoutRotation.R0)));
        Assert.False(EdgePortSheet.UsesEdgeWidth(Port("1", 0, 0, LayoutRotation.R0, widthUm: 300)));
        Assert.Null(EdgePortSheet.StoredUseEdgeWidth(true, null));
        Assert.Null(EdgePortSheet.StoredUseEdgeWidth(false, Um(300)));
        Assert.False(EdgePortSheet.StoredUseEdgeWidth(false, null));
        Assert.True(EdgePortSheet.StoredUseEdgeWidth(true, Um(300)));

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(Pour(statedUm: 300));
        string text = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("PortUseEdgeWidth", text, StringComparison.Ordinal);
        Assert.Equal(text, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(text)));

        view.Shapes.Clear();
        view.Shapes.AddRange(Pour(useEdge: false));
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        Assert.False(EdgePortSheet.UsesEdgeWidth(back.Shapes.OfType<LabelShape>().Single(l => l.Text == "1")));
    }

    // ── R-pw1-1 / R-pw1-3 — the planar run's port ──────────────────────────────────────────────

    private static EmPortExtractionResult Planar(List<LayoutShape> shapes)
    {
        PlanarPolygon R(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("Top Copper (1 oz)", [R(0, 0, 6e-3, 3e-3), R(6e-3, 1.35e-3, 9e-3, 1.65e-3)], 5.8e7, 35e-6)],
            new GroundedSlab(H, new EmMaterial(3.66, 0.0037)), 3e9);
        return EmPortExtraction.Extract(shapes, problem, Dbu, technology: Tech());
    }

    /// <summary>Checked (the default here), the planar port is the whole edge: no span, today's port exactly.</summary>
    [Fact]
    public void Planar_ACheckedPort_DrivesTheWholeEdge()
    {
        var r = Planar(Pour());
        Assert.True(r.Ok, r.Refusal);
        Assert.Null(r.Ports.Single(p => p.Number == 1).Span);
    }

    /// <summary>Unchecked and blank: 2 × H centred on the port. A stated width: that width, and the run's note says so in
    /// the words the 3D run uses.</summary>
    [Fact]
    public void Planar_AnUncheckedPort_Is2HOrItsStatedWidth_AndSaysSo()
    {
        var blank = Planar(Pour(useEdge: false)).Ports.Single(p => p.Number == 1).Span!.Value;
        Assert.Equal(1000e-6 - H, blank.LoM, 9);
        Assert.Equal(1000e-6 + H, blank.HiM, 9);

        var r = Planar(Pour(statedUm: 300));
        var stated = r.Ports.Single(p => p.Number == 1).Span!.Value;
        Assert.Equal(300e-6, stated.WidthM, 9);
        Assert.Equal(1000e-6, 0.5 * (stated.LoM + stated.HiM), 9);
        Assert.Contains(r.Notes, n => n == "Port 1's cut is 300 µm wide, the Width the port states.");
    }

    // ── R-pw1-0b — the 3D refusal names the checkbox ───────────────────────────────────────────

    /// <summary>A whole-edge sheet that reaches return metal covering only part of the edge is refused, and for a port
    /// using its edge width the refusal names the checkbox as the fix — and unticking it does clear the refusal, the plane
    /// covering the 2 × H the port then is.</summary>
    [Fact]
    public void ThreeD_ASheetAcrossTheWholeEdgeThatIsRefused_NamesTheCheckbox()
    {
        Assert.Contains("Use edge width", Generate(useEdge: null).Refusal, StringComparison.Ordinal);
        var unchecked_ = Generate(useEdge: false);
        Assert.True(unchecked_.Ok, unchecked_.Refusal);
    }

    private static Em3dGenerationResult Generate(bool? useEdge)
    {
        // Port 1 at y = 700 µm. The plane under its 3 mm edge covers only the lower 1.3 mm: not the whole edge, but the
        // 2 × H (192 … 1208 µm) the port is when unticked; under the line, all of it.
        var shapes = Pour(useEdge: useEdge, portYUm: 700);
        shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(6000), Y2 = Um(1300) });
        shapes.Add(new RectShape { Layer = Bottom, X1 = Um(6000), Y1 = 0, X2 = Um(9000), Y2 = Um(3000) });
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var setup = new EmSetup
        {
            Name = "pw", LayoutRef = "pw.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        };
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/pw.clay", view, Tech(), Dbu), Tech());
    }

    // ── R-pw1-4/5/6 — a .c3d keeps up with its layout's ports ──────────────────────────────────

    private (string Cell, string C3d, string Cws) Board(List<LayoutShape> shapes)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), Tech());
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = CellFolder.CreateCellFolder(ws, "Board");
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        CellCreate.WriteLayoutView(dir, "Board", view);
        return (dir, Path.Combine(CellFolder.SubFolderPath(dir, ViewType.ThreeD), "Board" + C3dPersistence.Extension), cws);
    }

    /// <summary>R-pw1-6 — a layout no 3D setup names still brings in its ports, and the result says it used a default
    /// setup.</summary>
    [Fact]
    public void NewFromLayout_WithNo3DSetup_BringsInThePortLabels_AndSaysSo()
    {
        var (cell, c3d, cws) = Board(Pour(plane: true));
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);
        Assert.Null(made.Refusal);
        Assert.Empty(made.SetupsCopied);
        Assert.Equal([1, 2], made.Document!.Ports.Select(p => p.Number));
        Assert.Contains("default driven setup", made.PortsNote, StringComparison.Ordinal);
    }

    /// <summary>A board-edge launch's inner ground pulled back from the line's end, 300 µm on a 300 µm line, beyond the
    /// tenth of a width a solve forgives: the port still comes into the .c3d, at x = 8.7 mm where the plane begins, and
    /// says so. A solve of the layout itself still refuses it.</summary>
    [Fact]
    public void NewFromLayout_APlanePulledBackFromTheLineEnd_PutsThePortWhereThePlaneBegins()
    {
        var shapes = Pour();
        shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(8700), Y2 = Um(3000) });
        var (cell, c3d, cws) = Board(shapes);
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);
        Assert.Empty(made.PortsLeftOut);
        Assert.Equal(Um(8700), made.Document!.Ports.Single(p => p.Number == 2).Offset);
        Assert.Contains("Port 2's sheet is 300 µm inside its line's end", Assert.Single(made.PortsMoved), StringComparison.Ordinal);
        Assert.Contains("short of the port's end", Solve(shapes).Refusal, StringComparison.Ordinal);
    }

    /// <summary>Designer feedback round 34 — port 1 on the 3 mm pad's far edge, the plane beginning 6.5 mm in, out on the
    /// 300 µm line: within three of the sheet's widths, but the sheet would land across a line a tenth its width. It is
    /// left out rather than slid onto the line, and port 2 still comes in.</summary>
    [Fact]
    public void NewFromLayout_APlaneBeginningOnANarrowerLine_DoesNotSlideThePortOntoIt()
    {
        var shapes = Pour();
        shapes.Add(new RectShape { Layer = Bottom, X1 = Um(6500), Y1 = 0, X2 = Um(9000), Y2 = Um(3000) });
        var (cell, c3d, cws) = Board(shapes);
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);
        Assert.Empty(made.PortsMoved);
        Assert.Contains(made.PortsLeftOut, l => l.Contains("port 1", StringComparison.Ordinal));
        Assert.Equal([2], made.Document!.Ports.Select(p => p.Number));
    }

    /// <summary>One port that cannot be built (its plane stops 3 mm, ten widths, short of the line's end) is left out with
    /// its reason, and the other port still comes in.</summary>
    [Fact]
    public void NewFromLayout_OnePortRefusing_LeavesOutThatPortAlone()
    {
        var shapes = Pour();
        shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(6000), Y2 = Um(3000) });
        var (cell, c3d, cws) = Board(shapes);
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);
        Assert.Equal([1], made.Document!.Ports.Select(p => p.Number));
        Assert.StartsWith("ports: port 2 (Port 2 ", Assert.Single(made.PortsLeftOut), StringComparison.Ordinal);
    }

    /// <summary>A label snapped to the corner of the line's end — on its long edge, y = 1650 µm — is the line's end, not
    /// the pour's right edge 3 mm back along the same ray.</summary>
    [Fact]
    public void ALabelOnTheLinesLongEdge_FindsTheLinesEnd()
    {
        var shapes = Pour(plane: true);
        shapes[3] = Port("2", 9000, 1650, LayoutRotation.R180);
        var (cell, c3d, cws) = Board(shapes);
        var port2 = C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!.Ports.Single(p => p.Number == 2);
        Assert.Equal(Um(9000), port2.Offset);
        Assert.Equal(Um(300), port2.Rect.Size.U);
    }

    private static Em3dGenerationResult Solve(List<LayoutShape> shapes)
    {
        var setup = new EmSetup
        {
            Name = "pw", LayoutRef = "pw.clay", Solver3D = Em3dSolverId.Palace,
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        };
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/pw.clay", Layout(shapes), Tech(), Dbu), Tech());
    }

    /// <summary>R-pw1-4 — a Width edit on the layout after the .c3d was made is reported against the port, with the old and
    /// new widths; R-pw1-5 — Update Ports from Layout replaces exactly the ports the layout states.</summary>
    [Fact]
    public void AStalePort_IsReportedWithBothWidths_AndUpdateReplacesOnlyTheStatedPorts()
    {
        var (cell, c3d, cws) = Board(Pour(plane: true));
        var doc = C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!;
        Assert.Empty(C3dHierarchy.StalePorts(doc, C3dHierarchy.PortsFromLayouts(doc, c3d, cws)));

        // Port 1 stops using its edge: 2 × H, 1016 µm, where the .c3d has the 3 mm edge.
        CellCreate.WriteLayoutView(cell, "Board", Layout(Pour(useEdge: false, plane: true)));
        var now = C3dHierarchy.PortsFromLayouts(doc, c3d, cws);
        var stale = Assert.Single(C3dHierarchy.StalePorts(doc, now));
        Assert.Equal(1, stale.Number);
        Assert.Equal(Um(3000), stale.OldWidthDbu);
        Assert.Equal(Um(1016), stale.NewWidthDbu);

        var mine = new C3dPort { Number = 9, Name = "mine", Plane = C3dPlane.XY, Rect = new C3dRect { Size = new C3dPoint2(1, 1) } };
        doc.Ports.Add(mine);
        var port2 = doc.Ports.Single(p => p.Number == 2).Rect.Size;
        var update = C3dHierarchy.UpdatePorts(doc, now);
        Assert.Equal([1], update.Updated);
        Assert.Empty(update.Added);
        Assert.Equal(Um(1016), doc.Ports.Single(p => p.Number == 1).Rect.Size.U);
        Assert.Equal(port2, doc.Ports.Single(p => p.Number == 2).Rect.Size);
        Assert.Same(mine, doc.Ports.Single(p => p.Number == 9));
        Assert.Empty(C3dHierarchy.StalePorts(doc, now));
    }

    /// <summary>Two placed instances of one layout both number their ports 1 and 2. Update brings the second instance's in
    /// at the next free numbers and says so; each record is linked to its instance's label, so a renumbered record is
    /// still found, and a Width edit is then reported against all four. The link is written to the file and is not there
    /// on a port drawn by hand.</summary>
    [Fact]
    public void TwoInstancesNumberingAPort1_TheSecondComesInAtFreeNumbers_AndTheLinkSurvivesARenumber()
    {
        var (cell, c3d, cws) = Board(Pour(plane: true));
        var doc = C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!;
        Assert.Equal("U1", doc.Ports.Single(p => p.Number == 1).FromLayout!.Instance);
        doc.Instances.Add(new C3dInstance
        {
            Name = "U2", CellRef = doc.Instances[0].CellRef, View = C3dInstanceView.Layout,
            Placement = new C3dPlacement { Origin = new C3dPoint3(0, Um(5000), 0) },
        });

        var update = C3dHierarchy.UpdatePorts(doc, C3dHierarchy.PortsFromLayouts(doc, c3d, cws));
        Assert.Empty(update.Updated);
        Assert.Equal([(1, 3), (2, 4)], update.Added.Select(a => (a.From.LayoutNumber, a.Number)));
        Assert.All(update.Added, a => Assert.Equal("U2", a.From.Instance));
        Assert.Contains("U2's port 1 as port 3 (port 1 is taken)", update.Describe(), StringComparison.Ordinal);
        var p3 = doc.Ports.Single(p => p.Number == 3);
        Assert.Equal(doc.Ports.Single(p => p.Number == 1).Offset, p3.Offset);
        Assert.Equal(Um(5000), p3.Rect.Min.U - doc.Ports.Single(p => p.Number == 1).Rect.Min.U);

        p3.Number = 7;
        CellCreate.WriteLayoutView(cell, "Board", Layout(Pour(useEdge: false, plane: true)));
        var stale = C3dHierarchy.StalePorts(doc, C3dHierarchy.PortsFromLayouts(doc, c3d, cws));
        Assert.Equal([1, 7], stale.Select(x => x.Number).Order());
        Assert.All(stale, x => Assert.Equal(Um(1016), x.NewWidthDbu));

        string text = C3dPersistence.Serialize(doc);
        Assert.Equal("U2", C3dPersistence.Deserialize(text).Ports.Single(p => p.Number == 7).FromLayout!.Instance);
        Assert.DoesNotContain("FromLayout", C3dPersistence.SerializePorts([new C3dPort { Number = 1, Name = "P1" }]), StringComparison.Ordinal);
    }

    /// <summary>A port's number is edited from its menu: a taken number is refused, a free one is taken, and a default
    /// name follows it while a given one is kept.</summary>
    [Fact]
    public void APortsNumber_IsEdited_RefusingATakenOne()
    {
        var (cell, c3d, cws) = Board(Pour(plane: true));
        CellCreate.WriteThreeDView(cell, "Board", C3dHierarchy.NewFromLayout(cell, c3d, cws).Document!);
        using var vm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d),
            () => new CircuitRF.Ui.Tests.ThreeD.PatchRecordingBackend(), () => cws, _ => { });
        var p1 = vm.Document.Ports.Single(p => p.Number == 1);
        Assert.Contains("already taken", vm.SetPortNumber(p1, "2"), StringComparison.Ordinal);
        Assert.Null(vm.SetPortNumber(p1, "5"));
        Assert.Equal("P5", vm.Document.Ports.Single(p => p.Number == 5).Name);
        var p2 = vm.Document.Ports.Single(p => p.Number == 2);
        Assert.Null(vm.SetPortName(p2, "RF out"));
        Assert.Null(vm.SetPortNumber(vm.Document.Ports.Single(p => p.Number == 2), "1"));
        Assert.Equal("RF out", vm.Document.Ports.Single(p => p.Number == 1).Name);
    }

    private static LayoutView Layout(List<LayoutShape> shapes)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        return view;
    }
}
