// PWL-4 — the wall port in the 3D editor, driven as a user drives it: Make Port ▸ Wall on a face of the air box, a face switched
// between wave and wall ports, the Analysis Setup's face-port table and its Calibrate wall ports switch. Each asserts what the
// .c3d then holds. testdata/pwl's edge-coupled pair, run to both x faces; no solver runs.

using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class WallPortEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pwl4e-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();
    private const long Len = 20000000;

    public WallPortEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private C3dEditorViewModel Open()
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "coupled_line_L20000um.c3d")))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if (((string?)o!["Name"])!.StartsWith("strip", StringComparison.Ordinal)) { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = Len; }
        doc["Ports"] = new JsonArray();
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, $"pair{_open.Count}.c3d");
        File.WriteAllText(path, doc.ToJsonString());
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(), () => null, _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup("Palace");          // as the shell does, before the first scene
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        vm.SetActiveSetup("Palace");
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
                       {
                           while (_posted.TryDequeue(out var a)) a();
                           return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
                       }, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    private static string PortsJson(C3dEditorViewModel vm) => JsonNode.Parse(C3dPersistence.Serialize(vm.Document))!["Ports"]!.ToJsonString();

    private (C3dEditorViewModel Vm, double XMin, double XMax) WithWallPorts()
    {
        var vm = Open();
        double xmin = vm.ShownAirBox!.Min.X, xmax = vm.ShownAirBox.Max.X;
        Assert.Null(vm.MakeWallPorts(0, xmin));
        Settle(vm);
        Assert.Null(vm.MakeWallPorts(0, xmax));
        Settle(vm);
        return (vm, xmin, xmax);
    }

    /// <summary>R-pwl4-1 — one wall port per line, numbered left to right, in one undo entry; a second time skips both lines.</summary>
    [Fact]
    public void MakePortWall_WritesAWallPortPerLine_InOneUndoEntry()
    {
        var vm = Open();
        int entries = vm.UndoEntries;
        Assert.Null(vm.MakeWallPorts(0, vm.ShownAirBox!.Min.X));
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Contains("Made wall ports P1, P2 on the xmin face", vm.StatusMessage);
        Assert.Equal(
            """[{"Number":1,"Name":"P1","Kind":"Wall","Plane":"YZ","Offset":0,"Rect":{"Min":[-1350000,0],"Size":[1200000,1016000]},"Z0":"50"},""" +
            """{"Number":2,"Name":"P2","Kind":"Wall","Plane":"YZ","Offset":0,"Rect":{"Min":[150000,0],"Size":[1200000,1016000]},"Z0":"50"}]""",
            PortsJson(vm));
        Assert.Contains("has a port there already", vm.MakeWallPorts(0, vm.ShownAirBox!.Min.X));
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.Ports);
    }

    /// <summary>R-pwl4-3 — the Kind switch takes the whole face, in one undo entry, and back again gives the same bytes.</summary>
    [Fact]
    public void AFaceSwitchedToWaveAndBack_InTheEditor_IsTheFileItStartedFrom()
    {
        var (vm, _, _) = WithWallPorts();
        string walls = PortsJson(vm);
        int entries = vm.UndoEntries;
        Assert.Null(vm.SetFaceKind(vm.Document.Ports[0], Em3dPortKind.Wave));
        Settle(vm);
        Assert.Equal(entries + 1, vm.UndoEntries);
        Assert.Contains("ports are wave ports now", vm.StatusMessage);
        var wave = vm.Document.Ports.Single(p => p.Kind == Em3dPortKind.Wave);
        Assert.Equal([1, 2], wave.Terminals!.Select(t => t.Number));
        Assert.Null(vm.SetFaceKind(wave, Em3dPortKind.Wall));
        Settle(vm);
        Assert.Equal(walls, PortsJson(vm));
    }

    /// <summary>R-pwl4-6b — a 3D view's setup runs a face as wave ports in its own Ports3D; the drawing is not touched, and
    /// "As drawn" takes the rows away again.</summary>
    [Fact]
    public void TheSetupsFacePortTable_WritesPorts3D_AndLeavesThePortsAlone()
    {
        var (vm, _, _) = WithWallPorts();
        string ports = PortsJson(vm);
        vm.SelectedSetupItem = vm.SetupItems.Single(s => s.Name == "Palace");
        var panel = vm.SetupEditor!.ViewModel;
        Assert.True(panel.IsFacePortSetup);
        Assert.Equal(["1", "2", "3", "4"], panel.FacePortRows.Select(r => r.Number));

        panel.FacePortRows[0].Choice = EmFacePortRow.WaveChoice;
        Settle(vm);
        var setup = C3dSetups.Read(vm.Document).Single(s => s.Name == "Palace").Setup!;
        Assert.Equal([(1, Em3dPortKind.Wave), (2, Em3dPortKind.Wave)], setup.Ports3D.Select(r => (r.Port, r.Kind)));
        Assert.Equal(ports, PortsJson(vm));
        // R-pwl4-6c — the view draws the face as the active setup runs it: one wave port over the face, a terminal per line
        var waveFace = vm.PortResults.Where(r => r.Resolved is { Kind: Em3dPortKind.Wave }).ToList();
        Assert.Equal([1, 2], waveFace.SelectMany(r => r.Terminals ?? []).Select(t => t.Number).Order());
        Assert.Equal(2, vm.PortResults.Count(r => r.Resolved?.Wall is not null));

        vm.SetupEditor!.ViewModel.FacePortRows[1].Choice = EmFacePortRow.AsDrawn(Em3dPortKind.Wall);
        Settle(vm);
        Assert.Empty(C3dSetups.Read(vm.Document).Single(s => s.Name == "Palace").Setup!.Ports3D);
        Assert.Equal(ports, PortsJson(vm));
    }

    /// <summary>R-pwl4-7 — the switch is greyed with no wall port and, off, writes CalibrateWallPorts: false into the setup.</summary>
    [Fact]
    public void CalibrateWallPorts_IsGreyedWithoutWallPorts_AndOffIsWritten()
    {
        var bare = Open();
        bare.SelectedSetupItem = bare.SetupItems.Single(s => s.Name == "Palace");
        Assert.False(bare.SetupEditor!.ViewModel.HasWallPorts);
        Assert.Equal("No wall ports in this setup.", bare.SetupEditor.ViewModel.CalibrateWallPortsTip);

        var (vm, _, _) = WithWallPorts();
        vm.SelectedSetupItem = vm.SetupItems.Single(s => s.Name == "Palace");
        var panel = vm.SetupEditor!.ViewModel;
        Assert.True(panel.HasWallPorts);
        panel.CalibrateWallPorts = false;
        Settle(vm);
        Assert.Contains("\"CalibrateWallPorts\": false", C3dPersistence.Serialize(vm.Document));
    }
}
