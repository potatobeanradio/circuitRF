// The round trips of brief-artsch-9-acceptance-docs-example.md (R-as9-1, R-as9-2): the synthetic designs, and every
// step between a design and its recognition, each through the function the application's own command calls —
//   Update Layout from Schematic   SchematicToLayoutGenerator.Run, as WorkspaceViewModel.RunLayoutUpdate calls it
//                                  (with DiskCellResolver, as HierarchyExampleAuthoring does);
//   File ▸ Export ▸ Gerber         GerberExport.Analyze + Write;
//   the placement and BOM writers  BoardCompanions.Project + Write;
//   File ▸ Import ▸ Board          GerberImportEntry.RunFolder, into a fresh workspace with its own technology.
// The designer's half of Update Layout — arranging the placed parts — is done as HierarchyExampleAuthoring does it, by
// moving each instance so one of its points lands on another's.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

internal static class ArtworkRoundTripBoards
{
    public static readonly LayerKey Top = new(1, 0), Bottom = new(2, 0), Plane = new(3, 0), Inner = new(4, 0),
                                    Mask = new(5, 0), Drill = new(9, 0);

    public const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    public static long Um(double um) => (long)Math.Round(um * Dbu);

    /// <summary>Line width, µm: about 50 Ω on the 254 µm laminate, and narrower than an 0402's pad.</summary>
    public const double W = 450;

    // Colours are the shipped board technologies' (pcb-4layer_FR-4_62mil_1oz.ctech): a Gerber import takes them from the
    // workspace technology, so a layer given none here arrives transparent.
    private static LayerDef Layer(LayerKey key, string name, string purpose, string suffix, string function, Rgba color) =>
        new() { Key = key, Name = name, Purpose = purpose, Color = color, Interchange = new InterchangeMapping(null, null, null, suffix, function) };

    private static readonly Rgba TopColor = new(200, 122, 62), PlaneColor = new(155, 138, 70), InnerColor = new(122, 147, 85),
                                 BottomColor = new(138, 80, 40), MaskColor = new(30, 107, 60), DrillColor = new(32, 32, 32);

    private static StackupLayer Cu(string name, LayerKey key, bool reference = false) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Um(35), SigmaSm = 5.8e7, DrawingLayers = [key],
        IsGroundReference = reference,
    };

    private static StackupLayer Laminate(string name, double um) =>
        new() { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Um(um), Epsr = 3.66, TanD = 0.004 };

    // The via row is LAST: a land-pattern generator refuses a front copper whose next stackup row is not a laminate.
    private static StackupLayer Pth() => new()
    {
        Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [Drill], SpanFromLayer = "Top", SpanToLayer = "Bottom",
        Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
    };

    /// <summary>
    /// Top and Bottom copper on a 254 µm laminate (εr 3.66), Bottom the ground reference, a plated through via, and
    /// a top solder mask — each layer carrying the Gerber name and file function it is written and read back as.
    /// </summary>
    public static Technology TwoLayer()
    {
        var tech = new Technology
        {
            Name = "round-trip",
            Layers =
            [
                Layer(Top, "Top", "conductor", "GTL", "Copper,L1,Top,Signal", TopColor),
                Layer(Bottom, "Bottom", "conductor", "GBL", "Copper,L2,Bot,Plane", BottomColor),
                Layer(Mask, "Soldermask Top", "soldermask", "GTS", "Soldermask,Top", MaskColor),
                Layer(Drill, "Drill", "via", "DRL", "Plated,1,2,PTH", DrillColor),
            ],
        };
        tech.Stackup.Layers = [Cu("Top", Top), Laminate("Core", 254), Cu("Bottom", Bottom, reference: true), Pth()];
        return tech;
    }

    /// <summary>Top, a ground plane, an inner signal layer and a Bottom plane, both planes ground references (a
    /// stripline needs one on each side): the inner layer lies 800 µm below the plane and 200 µm above Bottom.</summary>
    public static Technology FourLayer()
    {
        var tech = new Technology
        {
            Name = "round-trip-4",
            Layers =
            [
                Layer(Top, "Top", "conductor", "GTL", "Copper,L1,Top,Signal", TopColor),
                Layer(Plane, "Plane", "conductor", "G2", "Copper,L2,Inr,Plane", PlaneColor),
                Layer(Inner, "Inner", "conductor", "G3", "Copper,L3,Inr,Signal", InnerColor),
                Layer(Bottom, "Bottom", "conductor", "GBL", "Copper,L4,Bot,Plane", BottomColor),
                Layer(Drill, "Drill", "via", "DRL", "Plated,1,4,PTH", DrillColor),
            ],
        };
        tech.Stackup.Layers =
        [
            Cu("Top", Top), Laminate("PP1", 200), Cu("Plane", Plane, reference: true), Laminate("Core", 800),
            Cu("Inner", Inner), Laminate("PP2", 200), Cu("Bottom", Bottom, reference: true), Pth(),
        ];
        return tech;
    }

    /// <summary>
    /// The round trip's design, in mm: port 1 — TL1 — a 90° bend — TL2 — a tee whose branch TL3 is an open stub —
    /// TL4 — C1 (0402, series) — TL5 — L1 (0402, shunt, to a VIAGND pad) standing on the line — TL6 — R1 (0603,
    /// series) — TL7 — port 2. Every line is a different length, so a recognised line pairs with one of these by it.
    /// </summary>
    public const string OriginalCnl = """
        Port:P1  p1  0  Num=1  Z=50 Ohm
        MLIN:TL1  p1  n1  W=0.45 mm  L=7.6 mm  SignalLayer=Top
        MBEND:B1  n1  n2  W=0.45 mm  Angle=90 deg  Miter=0  SignalLayer=Top
        MLIN:TL2  n2  n3  W=0.45 mm  L=4 mm  SignalLayer=Top
        MTEE:T1  n3  n4  n5  W1=0.45 mm  W2=0.45 mm  W3=0.45 mm  SignalLayer=Top
        MLIN:TL3  n5  n6  W=0.45 mm  L=3.5 mm  SignalLayer=Top
        MLIN:TL4  n4  n7  W=0.45 mm  L=3 mm  SignalLayer=Top
        C:C1  n7  n8  C=2.2 pF
        MLIN:TL5  n8  n9  W=0.45 mm  L=2 mm  SignalLayer=Top
        L:L1  n9  n10  L=8.2 nH
        VIAGND:VG1  n10  0  Drill=0.3 mm  Pad=0.6 mm  FromLayer=Top
        MLIN:TL6  n9  n11  W=0.45 mm  L=2.5 mm  SignalLayer=Top
        R:R1  n11  n12  R=10 Ohm
        MLIN:TL7  n12  p2  W=0.45 mm  L=4.5 mm  SignalLayer=Top
        Port:P2  p2  0  Num=2  Z=50 Ohm

        analysis SP1 type=sparam start=0.1 stop=3 npts=59 Unit=GHz
        """;

    private static readonly (string Part, string Footprint)[] Footprints = [("C1", "smt:0402"), ("L1", "smt:0402"), ("R1", "smt:0603")];

    /// <summary>A workspace at <paramref name="root"/> whose default technology is <paramref name="tech"/>; returns
    /// the technology's path.</summary>
    public static string Workspace(string root, Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(root, "tech", "board.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), new CwsFile { DefaultTechRef = "tech/board.ctech" });
        return Path.Combine(root, "tech", "board.ctech");
    }

    /// <summary>The design built and laid out in a workspace at <paramref name="root"/>: the schematic drawn from
    /// <see cref="OriginalCnl"/> with footprints on the three parts, Update Layout from Schematic, the parts arranged,
    /// a Bottom plane drawn, and Update Layout again (VG1 placed beside L1 where L1 now is).</summary>
    public static (string CellDir, string SchematicPath, string ClayPath, Technology Tech) Original(string root)
    {
        string techPath = Workspace(root, TwoLayer());
        var tech = TechPersistence.LoadFromFile(techPath);

        string cellDir = Path.Combine(root, "Board");
        string schematicDir = Path.Combine(cellDir, "schematic"), layoutDir = Path.Combine(cellDir, "layout");
        var (lib, tb) = new CnlReader().Read(OriginalCnl);
        var drawn = NetlistSchematic.Build(lib, tb, schematicDir);
        Assert.True(drawn.Schematic is not null, string.Join("; ", drawn.Refusals));
        var model = drawn.Schematic!;
        foreach (var (part, footprint) in Footprints)
            model.Components.Single(c => c.InstanceName == part).Parameters.Add(
                new EditableParameter { Name = ArtworkParameters.FootprintName, Expression = footprint, ShowOnSchematic = false });
        var made = CellCreate.Create(root, "Board", CellViews.Schematic | CellViews.Layout, model, tech);
        model.SchematicDirectory = schematicDir;

        var view = LayoutPersistence.LoadFromFile(made.LayoutPath!);
        void UpdateLayout(bool restoreRemoved = false)
        {
            var r = SchematicToLayoutGenerator.Run(model, view, schematicDir, root, layoutDir, tech, techPath, DiskCellResolver.Instance,
                                                   restoreRemoved: restoreRemoved);
            Assert.All(r.NoLayoutWarnings, w => Assert.StartsWith("P", w));   // the two ports have no artwork; everything else does
            r.Command?.Execute();
        }
        UpdateLayout();

        Arrange(view, layoutDir, tech);
        // The designer's own plane under the board, its edges where the two lines leave it (the generated pour reaches
        // past them), and VG1 deleted with the copper tying it to L1 where L1 first landed — the next update, asked to
        // restore removed parts (a part deleted by hand otherwise stays deleted, brief-sdl-4), places it beside L1 again.
        view.Shapes.RemoveAll(s => s.Generated == GroundArtwork.PourTag);
        view.Shapes.RemoveAll(s => s is PathShape && s.Layer == Top);
        view.Instances.Remove(Inst(view, "VG1"));
        view.Shapes.Add(new RectShape { Layer = Bottom, X1 = 0, Y1 = 0, X2 = Um(14_000), Y2 = PinAt(view, layoutDir, tech, "TL7", "2").Y });
        UpdateLayout(restoreRemoved: true);
        Assert.Contains(view.Instances, i => i.SchematicId == "VG1");
        LayoutPersistence.SaveToFile(made.LayoutPath!, view);
        return (cellDir, made.SchematicPath!, made.LayoutPath!, tech);
    }

    /// <summary>
    /// The board written out — Gerber + Excellon into <c>&lt;root&gt;/fab/board</c>, and with
    /// <paramref name="companions"/> the placement file and bill of materials beside it — and imported into a fresh
    /// workspace at <c>&lt;root&gt;/imported</c> (<see cref="ImportBoard"/>).
    /// </summary>
    public static (string Clay, string? Placement, string? Bom) ExportAndImport(
        string root, string cellDir, string clay, Technology tech, Technology stated, bool companions)
    {
        string gerbers = Path.Combine(root, "fab", "board");
        string? pos = companions ? Path.Combine(root, "fab", "board.pos") : null;
        string? bom = companions ? Path.Combine(root, "fab", "board-bom.csv") : null;
        WriteFab(cellDir, clay, tech, gerbers, pos, bom);
        return (ImportBoard(gerbers, Path.Combine(root, "imported"), stated), pos, bom);
    }

    /// <summary>File ▸ Export ▸ Gerber into <paramref name="gerbers"/> (its folder name is what an import calls the
    /// cell), and the placement file and bill of materials where they are named.</summary>
    /// <param name="unionCopper">Merges each copper layer's rectangles and polygons into non-overlapping regions before
    /// writing — for the shipped example only, whose bends' and tees' cells draw arms over the lines beside them and
    /// left overlapping copper in the imported board (designer report, round 15). The electrical lengths are the
    /// schematic's either way: the arms lie on copper the lines already draw.</param>
    public static void WriteFab(string cellDir, string clay, Technology tech, string gerbers, string? placement, string? bom,
                                bool unionCopper = false)
    {
        var view = LayoutPersistence.LoadFromFile(clay);
        var plan = GerberExport.Analyze(cellDir, tech, Dbu, view, null);
        Assert.True(plan.CanWrite, string.Join("; ", plan.Diagnostics));
        if (unionCopper) plan = plan with { Shapes = UnionCopper(plan.Shapes, tech) };
        GerberExport.Write(gerbers, Path.GetFileName(gerbers), plan);
        if (placement is null && bom is null) return;

        var projection = BoardCompanions.Project(view, clay, tech);
        Assert.True(projection.Refusal is null, projection.Refusal);
        Assert.True(projection.HasSchematic, string.Join("; ", projection.Notes));
        BoardCompanions.Write(projection, null, placement, bom);
    }

    private static List<LayoutShape> UnionCopper(IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        var copper = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
        bool Merged(LayoutShape s) => copper.Contains(s.Layer) && s is RectShape or PolygonShape;
        var result = shapes.Where(s => !Merged(s)).ToList();
        foreach (var layer in shapes.Where(Merged).GroupBy(s => s.Layer))
        {
            var clipper = new Clipper2Lib.Clipper64();
            foreach (var s in layer) clipper.AddSubject(LayoutClipper.ToClipperPaths(s, 1));
            var tree = new Clipper2Lib.PolyTree64();
            clipper.Execute(Clipper2Lib.ClipType.Union, LayoutClipper.Rule, tree);
            result.AddRange(LayoutClipper.FromClipperTree(tree, layer.Key, null));
        }
        return result;
    }

    /// <summary>
    /// The Gerber set in <paramref name="gerbers"/> imported into a workspace at <paramref name="workspace"/> whose
    /// technology is <paramref name="stated"/>; returns the imported <c>.clay</c>. The import writes a technology of
    /// its own with an FR-4 stackup it says it guessed; the designer types in the fabricator's
    /// (<paramref name="stated"/>'s) on the Stackup tab, as the import asks.
    /// </summary>
    public static string ImportBoard(string gerbers, string workspace, Technology stated)
    {
        var imported = GerberImportEntry.RunFolder(gerbers, workspace, TechPersistence.LoadFromFile(Workspace(workspace, stated)), Dbu);
        Assert.False(imported.Cancelled, string.Join("\n", imported.Messages));

        var itech = TechPersistence.LoadFromFile(imported.TechPath!);
        var dielectrics = stated.Stackup.Layers.Where(l => l.Kind == StackupKind.Dielectric).ToList();
        int d = 0;
        foreach (var l in itech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via))
        {
            var s = l.Kind == StackupKind.Dielectric ? dielectrics[d++] : stated.Stackup.Layers.Single(x => x.Name == l.Name);
            (l.ThicknessDbu, l.Epsr, l.TanD, l.SigmaSm, l.IsGroundReference) = (s.ThicknessDbu, s.Epsr, s.TanD, s.SigmaSm, s.IsGroundReference);
        }
        Assert.Equal(dielectrics.Count, d);
        TechPersistence.SaveToFile(imported.TechPath!, itech);

        return Directory.GetFiles(Path.Combine(imported.CellDir!, "layout"), "*.clay").Single();
    }

    /// <summary>A layout drawn by hand — the CPWG and SLIN boards, which no generator draws (D18) — saved as the cell
    /// <c>Board</c> of a workspace at <paramref name="root"/>.</summary>
    public static (string CellDir, string ClayPath, Technology Tech) Drawn(string root, Technology tech, IEnumerable<LayoutShape> shapes)
    {
        var saved = TechPersistence.LoadFromFile(Workspace(root, tech));
        var made = CellCreate.Create(root, "Board", CellViews.Layout, tech: saved);
        var view = LayoutPersistence.LoadFromFile(made.LayoutPath!);
        view.Shapes.AddRange(shapes);
        LayoutPersistence.SaveToFile(made.LayoutPath!, view);
        return (made.CellDir, made.LayoutPath!, saved);
    }

    public static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    public static ViaShape ViaAt(double x, double y) =>
        new() { Layer = Drill, X = Um(x), Y = Um(y), DrillSize = Um(300), PadSize = Um(600) };

    // ── the designer's half ──────────────────────────────────────────────────────────────────────

    private static void Arrange(LayoutView view, string dir, Technology tech)
    {
        // TL1 in from the left edge at y = 3 mm; the bend turns it up; the tee's branch runs right, open; the parts
        // climb to the top edge.
        Orient(view, dir, tech, "TL1", "1", (0, Um(3_000)), ("1", 180), ("2", 0));
        // A line meets a bend or a tee at the model's reference plane — half the crossing width from the corner or the
        // centre — and runs over the generator's arm, which is 2.5 W of artwork the lumped models do not carry.
        OrientByReference(view, dir, tech, "B1", "1", PinAt(view, dir, tech, "TL1", "2"), ("1", 180), ("2", 90));
        Orient(view, dir, tech, "TL2", "1", Reference(view, dir, tech, "B1", "2"), ("1", 270), ("2", 90));
        OrientByReference(view, dir, tech, "T1", "1", PinAt(view, dir, tech, "TL2", "2"), ("1", 270), ("2", 90), ("3", 0));
        Orient(view, dir, tech, "TL3", "1", Reference(view, dir, tech, "T1", "3"), ("1", 180), ("2", 0));
        Orient(view, dir, tech, "TL4", "1", Reference(view, dir, tech, "T1", "2"), ("1", 270), ("2", 90));
        // A line meets a series part at its pad's outer edge, which is where it is drawn to.
        OrientByPadEdge(view, dir, tech, "C1", "1", PinAt(view, dir, tech, "TL4", "2"), ("1", 270), ("2", 90));
        Orient(view, dir, tech, "TL5", "1", PadEdge(view, dir, tech, "C1", "2"), ("1", 270), ("2", 90));
        // L1 stands on the line with its first pad centred on it, away from the stub; TL6 carries on from that point.
        var tap = PinAt(view, dir, tech, "TL5", "2");
        Orient(view, dir, tech, "L1", "1", tap, ("1", 0), ("2", 180));
        Orient(view, dir, tech, "TL6", "1", tap, ("1", 270), ("2", 90));
        OrientByPadEdge(view, dir, tech, "R1", "1", PinAt(view, dir, tech, "TL6", "2"), ("1", 270), ("2", 90));
        Orient(view, dir, tech, "TL7", "1", PadEdge(view, dir, tech, "R1", "2"), ("1", 270), ("2", 90));
    }

    private static LayoutInstance Inst(LayoutView view, string id) => view.Instances.Single(i => i.SchematicId == id);

    private static LayoutPin LocalPin(LayoutView view, string dir, Technology tech, string id, string pin, out LayoutView cell)
    {
        var resolved = CellLayoutResolver.Resolve(Inst(view, id).CellRef, dir);
        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        cell = resolved.View!;
        return CellPins.Resolve(cell, tech).Single(p => p.Name == pin);
    }

    private static (long X, long Y) PinAt(LayoutView view, string dir, Technology tech, string id, string pin)
    {
        var p = LocalPin(view, dir, tech, id, pin, out _);
        return LayoutInstanceTransform.TransformPoint(p.X, p.Y, Inst(view, id), 0, 0);
    }

    /// <summary>The direction a placed pin faces, degrees.</summary>
    private static double Facing(LayoutView view, string dir, Technology tech, string id, string pin)
    {
        var p = LocalPin(view, dir, tech, id, pin, out _);
        var inst = Inst(view, id);
        double r = p.OutwardDeg * Math.PI / 180;
        var a = LayoutInstanceTransform.TransformPoint(p.X, p.Y, inst, 0, 0);
        var b = LayoutInstanceTransform.TransformPoint(p.X + (long)Math.Round(1e6 * Math.Cos(r)), p.Y + (long)Math.Round(1e6 * Math.Sin(r)), inst, 0, 0);
        return (Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Turns the instance so each named pin faces the stated way, then moves it so <paramref name="pin"/> is
    /// on <paramref name="at"/> — dragging a part by its pin onto a point. Never mirrored: a mirrored instance is a
    /// bottom-side placement.</summary>
    private static void Orient(LayoutView view, string dir, Technology tech, string id, string pin, (long X, long Y) at,
                               params (string Pin, double Deg)[] facing)
    {
        var inst = Inst(view, id);
        // Update Layout from Schematic places a bend in its symbol's hand, which can be mirrored; this
        // board is drawn top-side, so the instance is un-mirrored before it is turned.
        inst.MirrorX = false;
        foreach (var rot in Enum.GetValues<LayoutRotation>())
        {
            (inst.Rot, inst.X, inst.Y) = (rot, 0, 0);
            if (!facing.All(f => Math.Abs(((Facing(view, dir, tech, id, f.Pin) - f.Deg) % 360 + 540) % 360 - 180) < 1)) continue;
            var p = PinAt(view, dir, tech, id, pin);
            (inst.X, inst.Y) = (at.X - p.X, at.Y - p.Y);
            return;
        }
        throw new InvalidOperationException($"{id}: no rotation faces its pins as asked");
    }

    /// <summary>The middle of a land's outer edge — the edge its pin faces.</summary>
    private static (long X, long Y) PadEdge(LayoutView view, string dir, Technology tech, string id, string pin)
    {
        var p = LocalPin(view, dir, tech, id, pin, out var cell);
        var pad = cell.Shapes.OfType<RectShape>().Single(r => r.Layer == p.Layer && r.X1 <= p.X && p.X <= r.X2 && r.Y1 <= p.Y && p.Y <= r.Y2);
        long ex = p.OutwardDeg switch { 0 => pad.X2, 180 => pad.X1, _ => p.X };
        long ey = p.OutwardDeg switch { 90 => pad.Y2, 270 => pad.Y1, _ => p.Y };
        return LayoutInstanceTransform.TransformPoint(ex, ey, Inst(view, id), 0, 0);
    }

    /// <summary>Where a line meets a bend's or a tee's pin by the model's reference planes: the corner or the centre
    /// (where the pin's axis crosses the other arm's), back out along the pin by half the line width.</summary>
    private static (long X, long Y) Reference(LayoutView view, string dir, Technology tech, string id, string pin)
    {
        string other = pin switch { "1" when id.StartsWith('B') => "2", "2" when id.StartsWith('B') => "1", "3" => "1", _ => "3" };
        var (p, q) = (PinAt(view, dir, tech, id, pin), PinAt(view, dir, tech, id, other));
        double a = Facing(view, dir, tech, id, pin) * Math.PI / 180, b = Facing(view, dir, tech, id, other) * Math.PI / 180;
        (double X, double Y) u = (Math.Cos(a), Math.Sin(a)), v = (Math.Cos(b), Math.Sin(b));
        // p − s·u = q − t·v, for s: the arm from the pin back to the corner.
        double s = ((p.X - q.X) * v.Y - (p.Y - q.Y) * v.X) / (u.X * v.Y - u.Y * v.X);
        double back = s - Um(W) / 2.0;
        return ((long)Math.Round(p.X - back * u.X), (long)Math.Round(p.Y - back * u.Y));
    }

    private static void OrientByReference(LayoutView view, string dir, Technology tech, string id, string pin, (long X, long Y) at,
                                          params (string Pin, double Deg)[] facing)
    {
        Orient(view, dir, tech, id, pin, at, facing);
        Shift(Inst(view, id), at, Reference(view, dir, tech, id, pin));
    }

    private static void OrientByPadEdge(LayoutView view, string dir, Technology tech, string id, string pin, (long X, long Y) at,
                                        params (string Pin, double Deg)[] facing)
    {
        Orient(view, dir, tech, id, pin, at, facing);
        Shift(Inst(view, id), at, PadEdge(view, dir, tech, id, pin));
    }

    private static void Shift(LayoutInstance inst, (long X, long Y) to, (long X, long Y) from) =>
        (inst.X, inst.Y) = (inst.X + to.X - from.X, inst.Y + to.Y - from.Y);
}
