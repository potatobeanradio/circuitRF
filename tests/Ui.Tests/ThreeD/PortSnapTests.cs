using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>
/// A port snaps as a sheet does: a measurement from a box's corner to a lumped port's corner found nothing on the port —
/// no marker, no snap — because a port had no snap features and its ID-pass pixels carry no face.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class PortSnapTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-portsnap-" + Guid.NewGuid().ToString("N")[..12]);
    private C3dEditorViewModel? _vm;

    public PortSnapTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        _vm?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ALumpedPortsCorner_SnapsAsAVertex_AtItsExactDocumentPoint()
    {
        var vm = Open(new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "a", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 20 * Um) },
                new C3dBox { Name = "b", Material = "Gold", Min = new C3dPoint3(150 * Um, 0, 0), Size = new C3dPoint3(100 * Um, 100 * Um, 20 * Um) },
            ],
            Ports =
            [
                new C3dPort { Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XY, Offset = 20 * Um,
                              Rect = new C3dRect { Min = new C3dPoint2(100 * Um, 30 * Um), Size = new C3dPoint2(50 * Um, 40 * Um) },
                              Positive = "a", Negative = "b" },
            ],
        });
        var v = vm.Viewer;
        var port = v.Scene.Objects.Single(o => o.Kind == Scene3DKind.Port);
        var corners = v.Scene.FeaturesOf(port.Id).Table!.Vertices;
        Assert.Equal(4, corners.Length);                                    // the rectangle's, not the checkerboard's cells

        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H);
        v.View.Camera.Yaw = -0.9f; v.View.Camera.Pitch = 0.5f;
        var corner = new Point3(150e-6, 70e-6, 20e-6);                       // a corner clear of both boxes' own corners
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal(corner.X, corner.Y, corner.Z), W, H);
        Assert.True(front);
        v.Hover(x + 1.5f, y + 0.5f);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x + 1.5f, y + 0.5f, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);

        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        Assert.Equal(port.Id, v.Snap.Object);
        var p = vm.ToDocumentPoint(v.Snap);
        Assert.True(p.Exact);
        Assert.Equal(new C3dPoint3(150 * Um, 70 * Um, 20 * Um), p.Dbu);
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "c", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "c.c3d");
        C3dPersistence.SaveToFile(path, doc);

        var fake = new PatchRecordingBackend();
        _vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _vm.Viewer.Resized(W, H);
        _vm.Start();
        Assert.True(SpinWait.SpinUntil(() => _vm.AdoptedGeneration == _vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                    "the scene never settled");
        Assert.True(_vm.Viewer.SnapEnabled);
        return _vm;
    }
}
