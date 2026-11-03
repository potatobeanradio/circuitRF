// ================================================================
//  OutlinePointRowsTests.cs — the Inspector's point list for a polygon drawn in the 3D view (a sheet), a prism and a
//  polyline: one row per point in world x, y, z through the placement, the plane's normal coordinate held, each typed
//  point one undo entry; an outline made to cross itself is refused; a polyline point typed off its plane is stored in 3D.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class OutlinePointRowsTests : IDisposable
{
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-outline-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public OutlinePointRowsTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void APolygonSheet_ListsItsPointsInWorld_HoldsZ_AndATypedPointIsOneEntry_ACrossingRefused()
    {
        var sheet = new C3dSheet
        {
            Name = "poly", Material = "Gold", Plane = C3dPlane.XY, Offset = 20 * Um,
            Outline = [new(0, 0), new(100 * Um, 0), new(100 * Um, 50 * Um), new(0, 50 * Um)],
            Placement = new C3dPlacement { Origin = new C3dPoint3(10 * Um, 0, 0) },
        };
        var vm = Open(sheet);
        Select(vm, "poly");
        var p = vm.Properties;
        Assert.True(p.HasOutlinePoints);
        var rows = p.OutlinePoints.OfType<C3dOutlinePointRow>().ToList();
        Assert.Equal([("10", "0", "20"), ("110", "0", "20"), ("110", "50", "20"), ("10", "50", "20")], rows.Select(r => (r.X, r.Y, r.Z)));
        Assert.All(rows, r => Assert.True(r.ZReadOnly && !r.XReadOnly && !r.YReadOnly));

        int entries = vm.UndoEntries;
        rows[2].X = "150";
        p.CommitOutlinePoint(rows[2]);
        Settle(vm);
        Assert.Equal("", p.Error);
        Assert.Equal(new C3dPoint2(140 * Um, 50 * Um), ((C3dSheet)vm.Document.Objects[^1]).Outline[2]);
        Assert.Equal(entries + 1, vm.UndoEntries);

        var again = p.OutlinePoints.OfType<C3dOutlinePointRow>().ToList();
        again[1].X = "-50";                                                 // edges 1→2 and 3→0 would cross
        p.CommitOutlinePoint(again[1]);
        Assert.Contains("cross itself", p.Error, StringComparison.Ordinal);
        Assert.Equal(entries + 1, vm.UndoEntries);
    }

    [Fact]
    public void APolyline_ListsItsPoints_AndAPointTypedOffItsPlane_IsStoredIn3D()
    {
        var line = new C3dPolyline
        {
            Name = "guide", Plane = C3dPlane.XY, Offset = 20 * Um,
            Points = [new(0, 0), new(100 * Um, 0), new(100 * Um, 50 * Um)],
        };
        var vm = Open(line);
        Select(vm, "guide");
        var p = vm.Properties;
        var rows = p.OutlinePoints.OfType<C3dOutlinePointRow>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.XReadOnly || r.YReadOnly || r.ZReadOnly));

        rows[2].Z = "0";
        p.CommitOutlinePoint(rows[2]);
        Settle(vm);
        Assert.Equal("", p.Error);
        var stored = (C3dPolyline)vm.Document.Objects[^1];
        Assert.Equal([new(0, 0, 20 * Um), new(100 * Um, 0, 20 * Um), new(100 * Um, 50 * Um, 0)], stored.Points3);
        Assert.Empty(stored.Points);
    }

    [Fact]
    public void APrism_ListsItsBottom_AndItsTopFollowsATypedPoint()
    {
        var prism = new C3dPrism
        {
            Name = "slab", Material = "Gold", Plane = C3dPlane.XY, Offset = 0, Height = 20 * Um,
            Outline = [new(0, 0), new(100 * Um, 0), new(50 * Um, 80 * Um)],
        };
        var vm = Open(prism);
        Select(vm, "slab");
        var p = vm.Properties;
        var rows = p.OutlinePoints.OfType<C3dOutlinePointRow>().ToList();
        Assert.Equal(["0", "0", "0"], rows.Select(r => r.Z));
        rows[2].Y = "90";
        p.CommitOutlinePoint(rows[2]);
        Settle(vm);
        Assert.Equal("", p.Error);
        var stored = Assert.IsType<C3dPrism>(vm.Document.Objects[^1]);
        Assert.Equal(new C3dPoint2(50 * Um, 90 * Um), stored.Outline[2]);
        Assert.Equal(20 * Um, stored.Height);
    }

    private static void Select(C3dEditorViewModel vm, string name)
    {
        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == name);
        vm.Properties.Reload();
        Assert.True(vm.Properties.IsEditable);
    }

    private C3dEditorViewModel Open(C3dObject extra)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        var objects = new List<C3dObject>
        {
            new C3dBox { Name = "die", Material = "Gold", Min = new(0, 0, 0), Size = new(100 * Um, 60 * Um, 20 * Um) },
            extra,
        };
        C3dPersistence.SaveToFile(path, new C3dDocument { DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Um, Objects = objects });
        var fake = new CircuitRF.Ui.Tests.ThreeD.PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");
}
