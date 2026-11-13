// ================================================================
//  CoaxialPortEditorTests.cs — brief-cxp-2: the coaxial port in the 3D editor. Make Port ▸ Coaxial on the 3D Wave Ports
//  Launch's bore end face (its bore runs through the housing's back) makes a ring bound to pin_d/2 and bore_d/2, the pin
//  positive, and a VAR edit moves it; the Coaxial Port tool draws CoaxialPortTests' ring in each plane; the ring's Inspector
//  fields write its radii. Nothing here saves the shipped example.
// ================================================================

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class CoaxialPortEditorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-cxp2-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly Queue<Action> _posted = new();
    private CircuitRF.Design.ThreeD.Occ.GeometryKernel? _kernel;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        _kernel?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private const long Um = 1000;                 // DBU per µm

    private static string ExampleRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "examples", "3D Wave Ports"));

    // ── gate 1 — Make Port ▸ Coaxial ────────────────────────────────────────────────────────────

    [KernelFact]
    public void Gate1_MakePortCoaxial_OnTheBoresEndFace_BindsTheRingToTheVars_AndAVarEditMovesIt()
    {
        string path = Path.Combine(ExampleRoot(), "Launch", "3d", "Launch.c3d");
        var vm = Open(path, kernel: true);
        vm.DeletePorts([.. vm.Document.Ports.Where(p => p.Number == 1)]);
        Settle(vm);
        var bore = vm.SceneObject("bore")!;
        int face = Face(bore, "bottom");                       // the bore's base, at x = −5 mm: the PTFE disc round the pin
        var pin = vm.SceneObject("pin")!;

        // the face's menu offers Coaxial beside Lumped and Wave, and so does the pin's own end face in the same plane; its tip,
        // out in the air over the line with nothing round it, does not
        var makePort = MakePortMenu(vm, bore, face);
        output.WriteLine(string.Join(" | ", makePort.Select(i => $"{i.Header}{(i.Enabled ? "" : " (disabled)")}: {i.Tip}")));
        Assert.Equal(["Lumped", "Wave", "Coaxial"], makePort.Select(i => i.Header));
        Assert.True(makePort[2].Enabled, makePort[2].Tip);
        Assert.True(MakePortMenu(vm, pin, Face(pin, "bottom")).Single(i => i.Header == "Coaxial").Enabled);
        Assert.DoesNotContain(MakePortMenu(vm, pin, Face(pin, "top")), i => i.Header == "Coaxial");
        vm.Viewer.SetSelection([]);

        Assert.Null(vm.MakeCoaxialPortFromFace(bore.Id, face));
        Settle(vm);
        output.WriteLine(vm.StatusMessage);
        var made = vm.Document.Ports.Single(p => p.Annulus is not null);
        var ring = made.Annulus!;
        Assert.Equal((C3dPlane.YZ, -5000 * Um), (made.Plane, made.Offset));
        Assert.Equal((200 * Um, 670 * Um), (ring.Inner, ring.Outer));
        Assert.Equal(new C3dExpr("pin_d/2", "Mm"), C3dBindings.GetExpr(ring, nameof(C3dPortAnnulus.Inner), 0));
        Assert.Equal(new C3dExpr("bore_d/2", "Mm"), C3dBindings.GetExpr(ring, nameof(C3dPortAnnulus.Outer), 0));
        Assert.Equal(new C3dExpr("axis_z", "Mm"), C3dBindings.GetExpr(ring, nameof(C3dPortAnnulus.Centre), 1));
        var resolved = vm.PortResults.Single(r => r.Port.Number == made.Number).Resolved!;
        Assert.Equal(("pin", "housing"), (resolved.PositiveObject, resolved.NegativeObject));

        // and Make Port ▸ Coaxial on the tip says why
        Assert.Contains("has nothing round and coaxial with it", vm.MakeCoaxialPortFromFace(pin.Id, Face(pin, "top")));

        // bore_d 1.34 → 1.5 mm: the bore and the ring's outer circle move together
        Assert.Null(vm.EditNames("bore_d = 1.5", (doc, _) => { doc.Variables.Single(v => v.Name == "bore_d").Expression = "1.5"; return null; }));
        Settle(vm);
        Assert.Equal(750 * Um, vm.Document.Ports.Single(p => p.Annulus is not null).Annulus!.Outer);
        Assert.Null(vm.PortResults.Single(r => r.Port.Annulus is not null).Refusal);
    }

    /// <summary>The 3D Connector's launch, its housing hidden, P1 deleted: the pin's back end face, 0.3 mm short of the bore's
    /// floor and inside the bore, makes the ring between the pin and the housing — the face as its inner edge, the bore as its
    /// outer — bound to pin_d/2, bore_d/2 and axis_z, as the bore's own end face does where the bore runs through.</summary>
    [KernelFact]
    public void Gate1_MakePortCoaxial_OnThePinsEndFace_InsideItsBore_MakesTheRingRoundIt()
    {
        string path = Path.GetFullPath(Path.Combine(ExampleRoot(), "..", "3D Connector", "Launch", "3d", "Launch.c3d"));
        var vm = Open(path, kernel: true);
        vm.DeletePorts([.. vm.Document.Ports.Where(p => p.Number == 1)]);
        Settle(vm);
        var pin = vm.SceneObject("pin")!;
        int face = Face(pin, "bottom");                        // at x = −4.2 mm, inside the bore
        var coaxial = MakePortMenu(vm, pin, face).Single(i => i.Header == "Coaxial");
        Assert.True(coaxial.Enabled, coaxial.Tip);
        vm.Viewer.SetSelection([]);
        Assert.Null(vm.MakeCoaxialPortFromFace(pin.Id, face));
        Settle(vm);
        var made = vm.Document.Ports.Single(p => p.Annulus is not null);
        Assert.Equal((C3dPlane.YZ, -4200 * Um, 200 * Um, 670 * Um), (made.Plane, made.Offset, made.Annulus!.Inner, made.Annulus.Outer));
        Assert.Equal(new C3dExpr("pin_d/2", "Mm"), C3dBindings.GetExpr(made.Annulus, nameof(C3dPortAnnulus.Inner), 0));
        Assert.Equal(new C3dExpr("bore_d/2", "Mm"), C3dBindings.GetExpr(made.Annulus, nameof(C3dPortAnnulus.Outer), 0));
        Assert.Equal(new C3dExpr("axis_z", "Mm"), C3dBindings.GetExpr(made.Annulus, nameof(C3dPortAnnulus.Centre), 1));
        var resolved = vm.PortResults.Single(r => r.Port.Number == made.Number).Resolved!;
        Assert.Equal(("pin", "housing"), (resolved.PositiveObject, resolved.NegativeObject));

        // The bore's faces: its back (x = −4.5 mm) is the bore's metal floor, which the pin stops 0.3 mm short of, so no ring;
        // its front (x = 0, the flange's face at the board's edge), which the pin runs through, offers one.
        var bore = vm.SceneObject("bore")!;
        foreach (string name in new[] { "bottom", "top" })
        {
            var item = MakePortMenu(vm, bore, Face(bore, name)).SingleOrDefault(i => i.Header == "Coaxial");
            output.WriteLine($"bore {name}: {(item is null ? "not offered" : $"{(item.Enabled ? "enabled" : "disabled")} — {item.Tip}")}");
            if (name == "bottom") Assert.Null(item);
            else Assert.True(item is { Enabled: true }, item?.Tip);
        }
    }

    // ── gate 2 — the Coaxial Port tool ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(C3dPlane.YZ)]
    [InlineData(C3dPlane.XZ)]
    [InlineData(C3dPlane.XY)]
    public void Gate2_TheTool_DrawsTheFixturesRing_InEachPlane(C3dPlane plane)
    {
        var fixture = CoaxialPortTests.Coax(plane);
        string path = CoaxialPortTests.Save(_root, fixture);
        var vm = Open(path, kernel: false);
        var expected = vm.Document.Ports[0];
        vm.DeletePorts([.. vm.Document.Ports]);
        var drawing = new DrawingPlane(plane, 0);
        vm.SetPlane(drawing);
        vm.Arm(C3dToolKind.CoaxialPort);
        var tool = Assert.IsType<CoaxialPortTool>(vm.Tool);
        C3dDrawInput At(long u, long v) => new(drawing.FromUv(new C3dPoint2(u * Um, v * Um)), true, true, null, null);
        Assert.Equal(C3dToolStep.Next, tool.Click(At(0, 0)));                                  // the centre
        Assert.Equal(C3dToolStep.Next, tool.Click(At(0, CoaxialPortTests.PinUm)));             // on the pin's surface
        Assert.True(tool.Preview(At(CoaxialPortTests.BoreUm, 0)) is { Annulus.Outer: CoaxialPortTests.BoreUm * Um });
        Assert.Equal(C3dToolStep.Finish, tool.Click(At(CoaxialPortTests.BoreUm, 0)));          // on the shell's inner surface
        vm.AddPort(tool.Made!);
        Settle(vm);
        var drawn = vm.Document.Ports.Single();
        Assert.Equal((expected.Plane, expected.Offset), (drawn.Plane, drawn.Offset));
        Assert.Equal((expected.Annulus!.Centre, expected.Annulus.Inner, expected.Annulus.Outer),
                     (drawn.Annulus!.Centre, drawn.Annulus.Inner, drawn.Annulus.Outer));
        var r = vm.PortResults.Single();
        Assert.Null(r.Refusal);
        Assert.Equal(("pin", "shell"), (r.Resolved!.PositiveObject, r.Resolved.NegativeObject));

        // an outer radius inside the inner one is refused, and the gesture stays where it was
        vm.Arm(C3dToolKind.CoaxialPort);
        var again = (CoaxialPortTool)vm.Tool!;
        again.Click(At(0, 0));
        again.Click(At(CoaxialPortTests.PinUm, 0));
        var refused = again.Click(At(100, 0));
        Assert.Contains("outer radius is the shell's, beyond the inner one", refused.Refusal);
        Assert.Equal(2, again.Step);
    }

    // ── R-cxp2-3 — the ring's Inspector fields ──────────────────────────────────────────────────

    [Fact]
    public void TheRingsInspectorFields_WriteItsRadii_AsANumberOrAnExpression_AndRefuseAZeroOne()
    {
        string path = CoaxialPortTests.Save(_root, CoaxialPortTests.Coax(C3dPlane.YZ));
        var vm = Open(path, kernel: false);
        var port = vm.Document.Ports[0];
        Assert.Null(vm.SetPortFieldText(port, "Annulus.Outer", "0.7 mm"));
        Assert.Equal(700 * Um, vm.Document.Ports[0].Annulus!.Outer);
        Assert.Null(C3dBindings.GetExpr(vm.Document.Ports[0].Annulus!, nameof(C3dPortAnnulus.Outer), 0));
        Assert.Null(vm.SetPortFieldText(vm.Document.Ports[0], "Annulus.Inner", "pin_d/2"));
        // typed in a field, an expression takes the display unit as its site unit, as every dimension's does
        Assert.Equal(new C3dExpr("pin_d/2", "Um"), C3dBindings.GetExpr(vm.Document.Ports[0].Annulus!, nameof(C3dPortAnnulus.Inner), 0));
        Assert.Equal(200 * Um, vm.Document.Ports[0].Annulus!.Inner);
        Assert.Equal("A coaxial port's radius is a positive length.", vm.SetPortFieldText(vm.Document.Ports[0], "Annulus.Inner", "0"));
        var report = vm.PortReport(vm.Document.Ports[1]);
        Assert.NotNull(report?.RingImpedance);
        Assert.Equal(50.02, report!.Value.RingImpedance!.Value, 2);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The canvas menu's Make Port submenu for one face selected in Face mode.</summary>
    private static List<Viewer3DMenuItem> MakePortMenu(C3dEditorViewModel vm, Scene3DObject o, int face)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Face;
        vm.Viewer.SetSelection([Scene3DItem.OfFace(o.Id, face)]);
        return [.. vm.DrawMenuItems().Single(i => i.Header == "Make Port").Children!];
    }

    private C3dEditorViewModel Open(string path, bool kernel)
    {
        var fake = new PatchRecordingBackend();
        if (kernel && _kernel is null)
        {
            _kernel = KernelForTests.New();
            _kernel.Probe();
        }
        string cws = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, ".cws");
        var vm = kernel
            ? new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, _posted.Enqueue, kernel: _kernel)
            : new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        vm.RestoreActiveSetup(null);
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(120)), "the scene never settled");

    private int Face(Scene3DObject o, string name)
    {
        if (!o.FaceNames.Contains(name)) output.WriteLine($"{o.Name}'s faces: {string.Join(", ", o.FaceNames)}");
        return Enumerable.Range(0, o.FaceNames.Count).Single(i => o.FaceNames[i] == name);
    }
}
