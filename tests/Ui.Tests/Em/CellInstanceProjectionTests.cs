// brief-icp-1 — which cell instances a component port can name: the hierarchy walk, paths, pins, the selection rule and the .cem
// entries. No solve: every gate here reads files and compares what the projection and the selection say.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CellInstanceProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-icp1-" + Guid.NewGuid().ToString("N")[..12]);

    public CellInstanceProjectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static readonly LayerKey Metal = new(1, 0);
    private static readonly LayerKey Marker = new(50, 0);
    private const double PerDbu = 1e-6 / Dbu;

    // ── Gate 1: a nested instance's pins land where the flatten puts the pin's own point, facing the transformed way ──────

    [Fact]
    public void NestedPins_LandWhereTheFlattenPutsThem_FacingTheTransformedDirection()
    {
        string top = TwoLevel(out _);
        var set = Project(top);

        var q3 = set.At("X1/Q3");
        Assert.NotNull(q3);
        Assert.Null(q3!.Why);

        // Each pin has a marker drawn in the device cell at its point and one 10 µm out along its face's outward direction; the flatten
        // of X1 carries both through the same placements, so where they land is the answer the projection must give.
        var view = LayoutPersistence.LoadFromFile(top);
        var flat = LayoutFlatten.FlattenAllLevels(view.Instances[0], Path.GetDirectoryName(top)!, null).Shapes
                                .Where(s => s.Layer == Marker).Select(s => LayoutGeometry.BboxOf(s)).ToList();
        foreach (var pin in q3.Pins)
        {
            var at = flat.Select(b => ((b.MinX + b.MaxX) / 2.0 * PerDbu, (b.MinY + b.MaxY) / 2.0 * PerDbu))
                         .OrderBy(c => Math.Abs(c.Item1 - pin.X) + Math.Abs(c.Item2 - pin.Y)).First();
            Assert.Equal(at.Item1, pin.X, 9);
            Assert.Equal(at.Item2, pin.Y, 9);
            var rad = pin.OutwardDeg * Math.PI / 180;
            double ox = pin.X + 10e-6 * Math.Cos(rad), oy = pin.Y + 10e-6 * Math.Sin(rad);
            Assert.Contains(flat, b => Math.Abs((b.MinX + b.MaxX) / 2.0 * PerDbu - ox) < 1e-9 && Math.Abs((b.MinY + b.MaxY) / 2.0 * PerDbu - oy) < 1e-9);
        }
        Assert.Equal(["G", "D"], q3.Pins.Select(p => p.Name));
    }

    // ── The box: the instance's own shapes, carried through its placement ───────────────────────────────────────────

    [Fact]
    public void Box_IsTheNestedInstancesOwnShapes_ThroughItsRotatedMirroredPlacement()
    {
        string top = TwoLevel(out string topCell);
        var q3 = Project(top).At("X1/Q3")!;

        // Q3 composed into the top's frame by hand and flattened: the box the projection must report.
        var topView = LayoutPersistence.LoadFromFile(top);
        string midClay = Path.Combine(CellFolder.SubFolderPath(Path.Combine(_root, "Mid"), ViewType.Layout), "Mid.clay");
        var composed = LayoutInstanceTransform.ComposeInstances(topView.Instances[0], 0, 0, LayoutPersistence.LoadFromFile(midClay).Instances[0]);
        string topLayout = Path.GetDirectoryName(top)!;
        composed.CellRef = Path.GetRelativePath(topLayout, Path.Combine(_root, "Dev"));
        var shapes = LayoutFlatten.FlattenAllLevels(composed, topLayout, null).Shapes.Select(s => LayoutGeometry.BboxOf(s)).ToList();

        var box = q3.Box!.Value;
        Assert.Equal(shapes.Min(b => b.MinX) * PerDbu, box.X0, 12);
        Assert.Equal(shapes.Min(b => b.MinY) * PerDbu, box.Y0, 12);
        Assert.Equal(shapes.Max(b => b.MaxX) * PerDbu, box.X1, 12);
        Assert.Equal(shapes.Max(b => b.MaxY) * PerDbu, box.Y1, 12);
    }

    // ── Gate 2: the paths are LVS's ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Paths_AreTheOnesLvsReports_JoinedBySlashBelowTheTop()
    {
        string top = TwoLevel(out string topCell);
        var set = Project(top);

        var view = LayoutPersistence.LoadFromFile(top);
        var tagged = LayoutDesignFlatten.FlattenTagged(view, topCell, null, null, null).Shapes
                                        .Select(t => t.InstancePath).Where(p => p.Length > 0).Distinct().OrderBy(p => p).ToList();
        Assert.Equal(tagged, set.Instances.Where(i => i.Depth == 0).Select(i => i.Path).OrderBy(p => p).ToList());
        Assert.Equal(["X1", "X1/Q3", "X1/@1", "@1", "@1/Q3", "@1/@1"], set.Instances.Select(i => i.Path));
    }

    // ── Gate 3: "*" on a board takes exactly the footprints ──────────────────────────────────────────────────────────

    [Fact]
    public void Star_OnABoardOfFootprintsAndALinePCell_TakesExactlyTheFootprints()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string layoutDir = LayoutDir(cellDir);
        string land = Footprint("Land0402");
        string line = Cell("MLIN_ab12", v => { Pin(v, "1", 0, 0, 180); Pin(v, "2", 400, 0, 0); v.Shapes.Add(Rect(Metal, 0, -50, 400, 50)); });
        var view = new LayoutView();
        view.Instances.Add(Place(land, layoutDir, 0, 0, refdes: "C1"));
        view.Instances.Add(Place(land, layoutDir, 2000, 0, refdes: "C2"));
        view.Instances.Add(Place(line, layoutDir, 600, 0, schematicId: "TL1"));
        view.PCellSnapshots["MLIN_ab12"] = new PCellSnapshot("MLIN", new Dictionary<string, PCellValue>(), null, null, null);
        string clay = Save(view, layoutDir, "Board");

        var setup = new EmSetup { ComponentPorts = [new EmComponentPort(EmSetup.AllComponents)] };
        var (parts, set) = PartsAndInstances(clay);
        var selection = EmPartPads.Select(setup, parts, "this layout", set);

        Assert.Null(selection.Refusal);
        Assert.Equal(["C1", "C2"], selection.Wanted.Select(w => w.Refdes));
        Assert.Empty(selection.Instances);
        Assert.Equal(EmInstanceKind.Copper, set.At("TL1")!.Kind);
    }

    // ── Gate 4: "*" on two copper PCells takes nothing and says why ──────────────────────────────────────────────────

    [Fact]
    public void Star_OnTwoUnnamedPCells_SelectsNothing_AndNamesThemAndTheirCells()
    {
        // The shape of the shipped SpiralResonator: two generated cells, no designators, no schematic.
        string cellDir = CellFolder.CreateCellFolder(_root, "SpiralResonator");
        string layoutDir = LayoutDir(cellDir);
        string spiral = Cell("KIT_SPIRAL_ca17", v => { Pin(v, "1", 0, 0, 180); Pin(v, "2", 300, 0, 0); v.Shapes.Add(Rect(Metal, 0, -10, 300, 10)); });
        string mim = Cell("KIT_MIMCAP_84c6", v => { Pin(v, "1", 0, 0, 180); Pin(v, "2", 60, 0, 0); v.Shapes.Add(Rect(Metal, 0, -30, 60, 30)); });
        var view = new LayoutView();
        view.Instances.Add(Place(spiral, layoutDir, 0, 0));
        view.Instances.Add(Place(mim, layoutDir, -255, -46));
        view.PCellSnapshots["KIT_SPIRAL_ca17"] = new PCellSnapshot("KIT_SPIRAL", new Dictionary<string, PCellValue>(), null, null, null);
        view.PCellSnapshots["KIT_MIMCAP_84c6"] = new PCellSnapshot("KIT_MIMCAP", new Dictionary<string, PCellValue>(), null, null, null);
        string clay = Save(view, layoutDir, "SpiralResonator");

        var (parts, set) = PartsAndInstances(clay);
        var selection = EmPartPads.Select(new EmSetup { ComponentPorts = [new EmComponentPort(EmSetup.AllComponents)] }, parts, "this layout", set);

        Assert.Equal("ComponentPorts asks for every placed part that is not copper, and no part placed on this layout is a two-pad part or " +
                     "a device; @0 (KIT_SPIRAL), @1 (KIT_MIMCAP) are copper the solver can solve: name them, or select their cell, to cut " +
                     "them out.", selection.Refusal);

        // Named, or by cell, each is taken.
        var byName = EmPartPads.Select(new EmSetup { ComponentPorts = [new EmComponentPort("@1")] }, parts, "this layout", set);
        Assert.Equal(["@1"], byName.Instances.Select(i => i.Instance.Path));
        var byCell = EmPartPads.Select(new EmSetup { ComponentPorts = [new EmComponentPort("") { Cell = "KIT_SPIRAL" }] }, parts, "this layout", set);
        Assert.Equal(["@0"], byCell.Instances.Select(i => i.Instance.Path));
    }

    // ── Gate 5: a cell entry reaches a nested instance; an instance and one inside it is refused ─────────────────────────

    [Fact]
    public void CellEntry_ReachesANestedInstance_AndAnInstanceWithOneInsideItIsRefused()
    {
        string top = TwoLevel(out _);
        var (parts, set) = PartsAndInstances(top);

        var byCell = EmPartPads.Select(new EmSetup { ComponentPorts = [new EmComponentPort("") { Cell = "Dev", Z0 = "25" }] }, parts,
                                       "this layout", set);
        Assert.Null(byCell.Refusal);
        Assert.Equal(["X1/Q3", "X1/@1", "@1/Q3", "@1/@1"], byCell.Instances.Select(i => i.Instance.Path));
        Assert.All(byCell.Instances, i => Assert.Equal("25", i.Entry.Z0));

        var nested = EmPartPads.Select(new EmSetup { ComponentPorts = [new EmComponentPort("X1"), new EmComponentPort("X1/Q3")] }, parts,
                                       "this layout", set);
        Assert.Equal("ComponentPorts selects X1 and X1/Q3, which is inside it; an instance inside a cut one is already left out of the " +
                     "problem.", nested.Refusal);
    }

    // ── Gate 6: the .cem entries round-trip; an old file's spelling is unchanged ────────────────────────────────────────

    [Fact]
    public void CemEntries_RoundTrip_AndAnOldListIsWrittenAsItWas()
    {
        var old = new EmSetup { ComponentPorts = [new EmComponentPort(EmSetup.AllComponents), new EmComponentPort("C1", Z0: "25", WidthUm: 300)] };
        string oldJson = EmSetupPersistence.Serialize(old);
        Assert.Equal(oldJson, EmSetupPersistence.Serialize(EmSetupPersistence.Deserialize(oldJson)));
        foreach (var key in new[] { "\"Cell\"", "\"PortAt\"", "\"KeepOutUm\"", "\"Construction\"" }) Assert.DoesNotContain(key, oldJson);

        var full = new EmSetup
        {
            ComponentPorts =
            [
                new EmComponentPort("X1/Q3", Reference: "S") { PortAt = EmComponentPortAt.Cell, KeepOutUm = new EmKeepOut(-5, -5, 45, 30) },
                new EmComponentPort("") { Cell = "KIT_FET", Construction = EmComponentConstruction.Cutout, Z0 = "50" },
                new EmComponentPort("X2/*"),
            ],
        };
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(full));
        Assert.Equal(full.ComponentPorts, back.ComponentPorts);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top places X1 (cell Mid) rotated 90° at (1000, 500) µm and an unnamed Mid copy; Mid places Q3 (cell Dev, a device in Mid's
    /// schematic) rotated 90° and mirrored at (200, 100) µm, and an unnamed Dev. Dev has pins G (facing −x) and D (facing +x).
    /// </summary>
    private string TwoLevel(out string topCellDir)
    {
        string dev = Cell("Dev", v =>
        {
            Pin(v, "G", 0, 0, 180);
            Pin(v, "D", 40, 0, 0);
            v.Shapes.Add(Rect(Metal, 0, -10, 40, 10));
            foreach (var (x, y) in new[] { (0.0, 0.0), (-10.0, 0.0), (40.0, 0.0), (50.0, 0.0) })
                v.Shapes.Add(Rect(Marker, Um(x) - 1, Um(y) - 1, Um(x) + 1, Um(y) + 1, raw: true));
        });
        string mid = CellFolder.CreateCellFolder(_root, "Mid");
        string midLayout = LayoutDir(mid);
        var midView = new LayoutView();
        midView.Instances.Add(Place(dev, midLayout, 200, 100, deg: 90, mirror: true, schematicId: "Q3"));
        midView.Instances.Add(Place(dev, midLayout, 600, 100));
        Save(midView, midLayout, "Mid");
        var midSch = new SchematicEditModel();
        midSch.Components.Add(new EditableComponent { InstanceName = "Q3", Symbol = SymbolKind.Sdd, CellRef = "pdk://kit/fet" });
        WriteSchematic(mid, "Mid", midSch);

        topCellDir = CellFolder.CreateCellFolder(_root, "Top");
        string topLayout = LayoutDir(topCellDir);
        var topView = new LayoutView();
        topView.Instances.Add(Place(mid, topLayout, 1000, 500, deg: 90, schematicId: "X1"));
        topView.Instances.Add(Place(mid, topLayout, 3000, 500));
        return Save(topView, topLayout, "Top");
    }

    private string Footprint(string name) => Cell(name, v =>
    {
        Pin(v, "1", -300, 0, 180);
        Pin(v, "2", 300, 0, 0);
        v.Shapes.Add(Rect(Metal, -400, -150, -200, 150));
        v.Shapes.Add(Rect(Metal, 200, -150, 400, 150));
    });

    private string Cell(string name, Action<LayoutView> draw)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        var v = new LayoutView();
        draw(v);
        Save(v, LayoutDir(cellDir), name);
        return cellDir;
    }

    private static string LayoutDir(string cellDir)
    {
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Save(LayoutView view, string layoutDir, string name)
    {
        string path = Path.Combine(layoutDir, name + ".clay");
        LayoutPersistence.SaveToFile(path, view);
        return path;
    }

    private static void WriteSchematic(string cellDir, string name, SchematicEditModel model)
    {
        string dir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csch"), SchematicPersistence.Serialize(model));
    }

    private static void Pin(LayoutView v, string name, double xUm, double yUm, double outwardDeg)
        => v.Pins.Add(new LayoutPin { Name = name, X = Um(xUm), Y = Um(yUm), WidthDbu = Um(20), OutwardDeg = outwardDeg, Layer = Metal });

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2, bool raw = false)
    {
        long a = raw ? (long)x1 : Um(x1), b = raw ? (long)y1 : Um(y1), c = raw ? (long)x2 : Um(x2), d = raw ? (long)y2 : Um(y2);
        return new RectShape { Layer = layer, X1 = Math.Min(a, c), Y1 = Math.Min(b, d), X2 = Math.Max(a, c), Y2 = Math.Max(b, d) };
    }

    private static LayoutInstance Place(string cellDir, string layoutDir, double xUm, double yUm, double deg = 0, bool mirror = false,
                                        string? refdes = null, string? schematicId = null)
        => new()
        {
            CellRef = Path.GetRelativePath(layoutDir, cellDir), X = Um(xUm), Y = Um(yUm), Mag = 1.0,
            RotationDegrees = deg, MirrorX = mirror, RefDes = refdes, SchematicId = schematicId,
        };

    private static EmCellInstanceSet Project(string clay)
        => EmCellInstances.Project(LayoutPersistence.LoadFromFile(clay), clay, null, C3dTransform.Identity);

    private static (Dictionary<string, PlacedPart> Parts, EmCellInstanceSet Set) PartsAndInstances(string clay)
    {
        var view = LayoutPersistence.LoadFromFile(clay);
        var parts = EmPartPads.Project(view, clay, null, C3dTransform.Identity).Parts
                              .GroupBy(p => p.Refdes, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return (parts, EmCellInstances.Project(view, clay, null, C3dTransform.Identity));
    }
}
