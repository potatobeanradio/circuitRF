// brief-dfr34-1 — a 3D port in the editor: Move… on it (R-1), ranked first in the pick and drawn over the geometry (R-2).
// Headless: document bytes, the hover's answer and the overlay's lists.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class PortMoveAndPickTests : IDisposable
{
    private const float W = 400, H = 300;
    private const int Dbu = 1000;
    private const string In1 = "Inner 1 (Ground Plane)";
    private readonly string _root = Directory.CreateTempSubdirectory("dfr34-1-ui-").FullName;
    private readonly List<C3dEditorViewModel> _open = [];

    public PortMoveAndPickTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>Move P1…, in the view's menu and the tree's, moves the port's rectangle by the typed distance as one undo entry.</summary>
    [Fact]
    public void MoveOnAPort_TranslatesItsRectangle_AsOneUndoEntry()
    {
        var vm = Open();
        var port = vm.Document.Ports.Single(p => p.Number == 1);
        SelectPort(vm, 1);
        Assert.Contains(Flatten(vm.Viewer.ContextMenuItems()), m => m.Header == "Move P1…" && m.Enabled);
        var row = vm.Tree.SelectMany(g => g.Items).Single(i => i.Kind == "Port" && i.Name == "port/1");
        Assert.Contains(vm.TreeMenuItems(row), m => m.Header == "Move P1…");
        Assert.True(vm.CanRunModify("Move"));

        long offset = port.Offset, v0 = port.Rect.Min.V;
        int entries = vm.UndoEntries;
        vm.StartMove(C3dMoveLock.AxisX);
        var move = Assert.IsType<MoveTool>(vm.Tool);
        Assert.Equal([port], move.Ports);
        move.SetBase(new C3dPoint3(0, 0, 0), exact: true);
        vm.OpenField(null);
        vm.FieldText = "250";
        vm.FieldEnter();
        Settle(vm);

        Assert.True(LayoutUnits.TryParse("250", vm.Document.DisplayUnit, vm.Document.DbuPerMicron, out long d));
        Assert.Equal(C3dPlane.YZ, port.Plane);
        Assert.Equal(offset + d, port.Offset);
        Assert.Equal(v0, port.Rect.Min.V);
        Assert.Equal(entries + 1, vm.UndoEntries);
        vm.UndoRedo.Undo();
        Assert.Equal(offset, vm.Document.Ports.Single(p => p.Number == 1).Offset);
    }

    /// <summary>A port inside the board is what the hover offers over the dielectric in front of it, a second press at the same
    /// point steps behind it, and while it is selected the overlay draws its sheet.</summary>
    [Fact]
    public void APortRanksFirstInThePick_ASecondPressStepsBehindIt_AndItIsDrawnOnTop()
    {
        var vm = Open();
        var v = vm.Viewer;
        v.SelectMode = Scene3DSelectMode.Object;
        var sheet = vm.SceneObject("port/1")!;
        var port = vm.Document.Ports.Single(p => p.Number == 1);
        double M(long x) => x * 1e-6 / Dbu;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(M(port.Offset), M(port.Rect.Min.U + port.Rect.Size.U / 2),
                                                                   M(port.Rect.Min.V + port.Rect.Size.V / 2)), W, H);
        Assert.True(front);
        Hover(v, sx, sy);
        Assert.NotEqual(sheet.Id, v.LastPick.Object);                      // the ID pass sees what is in front of it
        Assert.Equal(sheet.Id, v.HoveredItem!.Value.Object);

        v.Click(false);
        Assert.Equal([Scene3DItem.OfObject(sheet.Id)], v.Selection);
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.Single(overlay.Ports, p => p.Selected);

        v.Click(false);
        Assert.NotEqual(sheet.Id, Assert.Single(v.Selection).Object);
    }

    // ── fixture: a stripline on Inner 2 of the four-layer board, its planes Inner 1 and Bottom ──────────────────

    private C3dEditorViewModel Open()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        foreach (var l in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
            l.IsGroundReference = l.Name is In1 or "Bottom Copper (1 oz)";
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), tech);
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string cell = CellFolder.CreateCellFolder(ws, "Board");
        LayerKey L2 = new(2, 0), L3 = new(3, 0), L4 = new(4, 0);
        long Mm(double x) => (long)Math.Round(x * 1000 * Dbu);
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(
        [
            new RectShape { Layer = L3, X1 = 0, Y1 = Mm(-0.15), X2 = Mm(10), Y2 = Mm(0.15) },
            new RectShape { Layer = L2, X1 = Mm(-2), Y1 = Mm(-5), X2 = Mm(12), Y2 = Mm(5) },
            new RectShape { Layer = L4, X1 = Mm(-2), Y1 = Mm(-5), X2 = Mm(12), Y2 = Mm(5) },
            Label("P1", 0, LayoutRotation.R0), Label("P2", Mm(10), LayoutRotation.R180),
        ]);
        CellCreate.WriteLayoutView(cell, "Board", view);
        string c3d = Path.Combine(CellFolder.SubFolderPath(cell, ViewType.ThreeD), "Board" + C3dPersistence.Extension);
        var made = C3dHierarchy.NewFromLayout(cell, c3d, cws);
        Assert.Equal([1, 2], made.Document!.Ports.Select(p => p.Number));
        Directory.CreateDirectory(Path.GetDirectoryName(c3d)!);
        C3dPersistence.SaveToFile(c3d, made.Document);

        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => cws, a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.BoundsMin, s.BoundsMax, W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;

        LabelShape Label(string text, long x, LayoutRotation dir) => new()
        {
            Layer = L3, PortLayer = L3, X = x, Y = 0, Text = text, Height = Mm(0.2), IsPort = true,
            PortKind = PlanarPortKind.Edge, PortDirection = dir,
        };
    }

    private static void SelectPort(C3dEditorViewModel vm, int n)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject($"port/{n}")!.Id)]);
    }

    private static IEnumerable<Viewer3DMenuItem> Flatten(IEnumerable<Viewer3DMenuItem> items)
        => items.SelectMany(i => (i.Children ?? []).Prepend(i));

    private static void Hover(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");
}
