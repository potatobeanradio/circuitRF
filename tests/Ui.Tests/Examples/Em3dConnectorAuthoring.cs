// ================================================================
//  Em3dConnectorAuthoring.cs — brief-em3d-70 R-em3d70-1d: the 3D Connector example's two 3D cells, DRAWN with the 3D
//  editor's own operations rather than written as JSON. Skipped unless CRF_AUTHOR_3D_CONNECTOR=1, because it WRITES
//  examples/3D Connector/Flange/ and Launch/ (both cell folders are replaced), and it needs the geometry kernel. It is
//  kept, not deleted, so the files can be re-drawn from their gestures after a format change, and so the gestures the
//  README and the user page describe are the ones used.
//
//  Every step is one the editor offers a user: the Variables panel, the toolbar's material and drawing plane, the Box
//  and Cylinder tools with their sizes TYPED, the Properties Inspector's name and dimension fields, Boolean ▸ Subtract…
//  and Unite… with the dialog's Blank and Keep tools, Edge mode and Fillet…, Export STEP… and Import STEP… with its
//  material table, Place Cell, the Port tool on a drawing plane, and Setup Analyses with the air box's face menu. The
//  GPU is the recording fake the editor gates use; the picks are the CPU patch those gates use.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Step;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class Em3dConnectorAuthoring
{
    private const float W = 1600, H = 1200;
    private const double UmM = 1e-6;
    private const long Mm = 1_000_000;                      // DBU per mm at 1000 DBU/µm
    private const long AxisZ = 725_500;                     // axis_z's value, DBU: 0.508 + 0.035/2 + pin_d/2 mm
    private const string Alloy = "Connector alloy";
    private readonly Queue<Action> _posted = new();
    private CircuitRF.Design.ThreeD.Occ.GeometryKernel _kernel = null!;

    public Em3dConnectorAuthoring()
    {
        // A new setup's solver comes from Settings ▸ Solvers (EmSolverPreference); a test reads the default, never this machine's file.
        CircuitRF.Ui.Layout.Em.EmSolverPreference.TestOverrideActive = true;
        (CircuitRF.Ui.Layout.Em.EmSolverPreference.TestLayoutStore, CircuitRF.Ui.Layout.Em.EmSolverPreference.TestThreeDStore) = (null, null);
    }

    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_3D_CONNECTOR") != "1")
                Skip = "re-draws examples/3D Connector/Flange and Launch only with CRF_AUTHOR_3D_CONNECTOR=1";
            else if (KernelForTests.SkipReason is { } reason)
                Skip = reason;
        }
    }

    [AuthorFact]
    public async Task DrawTheConnector()
    {
        Snap3DPreference.TestOverrideActive = true;
        string ws = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D Connector");
        foreach (string cell in new[] { "Flange", "Launch" })
            if (Directory.Exists(Path.Combine(ws, cell))) Directory.Delete(Path.Combine(ws, cell), true);
        var tech = TechPersistence.LoadFromFile(Path.Combine(ws, "tech", "board-and-connector.ctech"));
        string cws = Path.Combine(ws, ".cws");

        // New Cell ▸ 3D view, twice — the function the GUI's New Cell calls; the units (mm) come from the technology.
        string flange = CellCreate.Create(ws, "Flange", CellViews.ThreeD, tech: tech).ThreeDPath!;
        string launch = CellCreate.Create(ws, "Launch", CellViews.ThreeD, tech: tech).ThreeDPath!;
        string step = Path.Combine(Path.GetDirectoryName(launch)!, "flange.step");
        using var kernel = KernelForTests.New();
        kernel.Probe();
        _kernel = kernel;

        await Edit(flange, cws, async vm =>
        {
            DrawFlange(vm);
            // File ▸ Export STEP… from the Flange's editor: the dialog as it opens (flattened, precedence applied, AP214), OK.
            var dialog = new StepExportDialogViewModel(step, new StepExportOptions { Document = vm.Document, WorkspaceCws = cws },
                options => StepExport.Plan(flange, options, vm.Kernel),
                (plan, control) => StepExport.Write(plan, step, vm.Kernel, control));
            await dialog.PlanAsync();
            Assert.True(dialog.CanExport, dialog.Error);
            await dialog.ExportCommand.ExecuteAsync(null);
            Assert.True(dialog.Result is not null, dialog.Error);
        });
        await Edit(launch, cws, vm => { DrawLaunch(vm, ws, step); return Task.CompletedTask; });
    }

    private async Task Edit(string c3d, string cws, Func<C3dEditorViewModel, Task> draw)
    {
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => cws, _posted.Enqueue, kernel: _kernel);
        try
        {
            vm.RestoreActiveSetup(null);
            vm.Viewer.Resized(W, H);
            vm.Viewer.Session.EnsureBackend();
            vm.Start();
            Settle(vm);
            Assert.Equal(LayoutUnit.Mm, vm.Document.DisplayUnit);
            await draw(vm);
            Assert.Null(vm.Save());
        }
        finally { vm.Dispose(); }
    }

    // ── the flange: a plate with a hole the bore passes through — the part a connector's maker would supply ─────────

    private void DrawFlange(C3dEditorViewModel vm)
    {
        Frame(vm, new Vector3(-2, -4, -2), new Vector3(1, 4, 3.5f));
        Box(vm, "flange", Alloy, ["-1", "-3.5", "-1.5"], ["1", "7", "4.5"]);
        Cylinder(vm, "hole", Alloy, 0, ["-1", "0", "0.7255"], "0.67", "1");
        Boolean(vm, C3dBooleanOp.Subtract, keepTools: false, "hole", "flange");
    }

    // ── the launch ───────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawLaunch(C3dEditorViewModel vm, string ws, string step)
    {
        // The names: the coax's two diameters, the pin's tip radius, the laminate's and the copper's thickness, the housing's
        // height, and the height of the coax's axis over the ground (the pin sinks half the copper into the line). A literal beside a name with its own unit is in METRES (`axis_z - 1.5` is 1.5 m below the
        // axis, and check says so), which is why the housing's height is a name too.
        vm.ShowVariables = true;
        var v = vm.Variables!;
        // axis_z has NO unit of its own: it takes its names' units. Given one (Mm), the whole expression is scaled by it,
        // names included, and pin_d/2 would add 0.2 µm, not 0.2 mm — the pin would sink 200 µm into the laminate.
        foreach (var (name, value, unit) in new[] { ("pin_d", "0.4", "Mm"), ("bore_d", "1.34", "Mm"), ("tip_r", "0.1", "Mm"), ("sub_h", "0.508", "Mm"),
                                                    ("cu_t", "0.035", "Mm"), ("body_h", "3", "Mm"), ("axis_z", "sub_h + cu_t/2 + pin_d/2", "") })
        {
            v.NewName = name; v.NewExpression = value; v.NewUnit = unit;
            v.Add();
            Assert.Equal("", v.Error);
        }
        vm.ShowVariables = false;
        Settle(vm);

        // The board: the Board cell's LAYOUT, its ground plane's top on z = 0 and its edge on x = 0.
        Assert.Null(vm.BeginInstancePlacement("../../Board", Path.Combine(ws, "Board"), C3dInstanceView.Layout));
        vm.PlaceArmedAt(new C3dPoint3(0, 0, -35_000));
        Settle(vm);
        Assert.Null(vm.RenameInstance(0, "B1"));
        Settle(vm);

        Frame(vm, new Vector3(-6, -4, -2), new Vector3(6, 4, 3.5f));

        // The housing, and its bore subtracted from it and KEPT as the PTFE fill.
        Box(vm, "housing", Alloy, ["-5", "-1.5", "axis_z - body_h/2"], ["4", "3", "body_h"]);
        Cylinder(vm, "bore", "PTFE", 0, ["-4.5", "0", "axis_z"], "bore_d/2", "4.5");
        Boolean(vm, C3dBooleanOp.Subtract, keepTools: true, "bore", "housing");

        // Import STEP… the flange, its one part mapped to the connector alloy by its colour, then united with the housing.
        var plan = vm.ReadStep(step, null);
        var part = Assert.Single(plan.Parts);
        Assert.Equal((Alloy, StepMatch.ByColour), (part.Material, part.Match));
        Assert.Null(vm.AcceptStepImport(plan));
        Settle(vm);
        Boolean(vm, C3dBooleanOp.Unite, keepTools: false, "flange", "housing");

        // The centre pin, from just short of the bore's floor out over the line, and its tip rounded.
        Cylinder(vm, "pin", Alloy, 0, ["-4.2", "0", "axis_z"], "pin_d/2", "5.7");
        vm.Viewer.SelectMode = Scene3DSelectMode.Edge;
        vm.Viewer.SetSelection([vm.Viewer.EdgeItem("pin", "side|top") ?? throw new InvalidOperationException("no tip edge")]);
        vm.OpenFillet(C3dEdgeOp.Fillet);
        Assert.True(vm.FilletOpen, vm.StatusMessage);
        vm.FilletSizeText = "tip_r";
        Assert.True(SpinWait.SpinUntil(() => { Drain(); return !vm.FilletBusy && (vm.FilletPreviewsDrawn > 0 || vm.FilletError is not null); },
                                       TimeSpan.FromSeconds(60)), "the fillet preview never arrived");
        Settle(vm);
        Assert.True(vm.CanAcceptFillet, vm.FilletError);
        vm.AcceptFillet();
        Settle(vm);

        // The ports. P1 bridges the gap between the bore's floor and the pin's end, on the XZ plane through the axis;
        // P2 stands at the board's far edge, from the ground up to the line, on the YZ plane there.
        Port(vm, new DrawingPlane(C3dPlane.XZ, 0), Snap(-4_500_000, 0, AxisZ - 100_000), Snap(-4_200_000, 0, AxisZ + 100_000));
        Port(vm, new DrawingPlane(C3dPlane.YZ, 5_080_000), Snap(5_080_000, -787_400, 0), Snap(5_080_000, 787_400, 508_000));

        // The setups: Palace (active) and openEMS, one sweep, the same air box.
        Setup(vm, "Palace", Em3dSolverId.Palace);
        Setup(vm, "openEMS", Em3dSolverId.OpenEms);
        vm.SetActiveSetup("Palace");
        Settle(vm);
    }

    // ── the setups, chosen by measurement (README ▸ the settings traded away) ──────────────────────────────────────

    private void Setup(C3dEditorViewModel vm, string name, Em3dSolverId solver)
    {
        vm.AddSetup();
        Settle(vm);
        vm.SelectedSetupItem = vm.SetupItems.Single(i => i.Name.StartsWith('S') && i.Name.Length <= 3);
        Assert.Null(vm.RenameSetup(name));
        vm.SetActiveSetup(name);
        Settle(vm);
        var panel = vm.SetupEditor!.ViewModel;
        panel.Solver3DChoice = EmSetupEditorViewModel.Solver3DChoices.Single(c => c.Value == solver);
        Settle(vm);
        panel = vm.SetupEditor!.ViewModel;
        panel.Frequency.StartCoeff = "2";
        panel.Frequency.StopCoeff = "18";
        panel.Frequency.NumPointsExpr = "5";
        Settle(vm);
        if (solver == Em3dSolverId.Palace)
        {
            panel = vm.SetupEditor!.ViewModel;
            panel.PalaceQualityChoice = EmSetupEditorViewModel.PalaceQualityChoices.Single(c => c.Value == PalaceQuality.Draft);
            Settle(vm);
            panel = vm.SetupEditor!.ViewModel;
            panel.PalaceLinearSolverChoice = EmSetupEditorViewModel.PalaceLinearSolverChoices.Single(c => c.Value == PalaceLinearSolver.Direct);
            Settle(vm);
            Assert.Null(vm.SetupEditor!.ViewModel.PalaceFieldError);
        }
        // The air box's face menu: the housing's back is metal, so xmin lies on it as PEC; every other face absorbs, 2 mm out.
        Assert.Null(vm.SetAirBoxBoundary("xmin", Em3dBoundaryKind.Pec));
        Assert.Null(vm.SetAirBoxPadding("xmin", "0"));
        foreach (string face in new[] { "xmax", "ymin", "ymax", "zmin", "zmax" })
        {
            Assert.Null(vm.SetAirBoxBoundary(face, Em3dBoundaryKind.Absorbing));
            Assert.Null(vm.SetAirBoxPadding(face, "2"));
        }
        Settle(vm);
    }

    // ── gestures ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Box: the toolbar's material, a click on the XY plane, the width and depth typed (Tab between), the height
    /// typed; then the name and the corner typed in Properties.</summary>
    private void Box(C3dEditorViewModel vm, string name, string material, string[] min, string[] size)
    {
        vm.CurrentMaterial = material;
        vm.SetPlane(new DrawingPlane(C3dPlane.XY, 0));
        vm.Arm(C3dToolKind.Box);
        ClickAt(vm, 0, 0, 0);
        Assert.Equal(1, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = size[0];
        vm.FieldTab();
        vm.FieldText = size[1];
        vm.FieldEnter();
        Assert.False(vm.DefineOpen);
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = size[2];
        vm.FieldEnter();
        Settle(vm);
        vm.Disarm();
        int index = vm.Document.Objects.Count - 1;
        Assert.IsType<C3dBox>(vm.Document.Objects[index]);
        Name(vm, index, name);
        for (int k = 0; k < 3; k++) Field(vm, name, $"Min[{k}]", min[k]);
    }

    /// <summary>Cylinder along x: the YZ drawing plane, a click for the centre, the radius and the length typed; the base
    /// typed in Properties.</summary>
    private void Cylinder(C3dEditorViewModel vm, string name, string material, long planeX, string[] baseAt, string radius, string length)
    {
        vm.CurrentMaterial = material;
        vm.SetPlane(new DrawingPlane(C3dPlane.YZ, planeX));
        vm.Arm(C3dToolKind.Cylinder);
        ClickAt(vm, planeX / 1000.0, 0, AxisZ / 1000.0);
        Assert.Equal(1, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = radius;
        vm.FieldEnter();
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField(null);
        vm.FieldText = length;
        vm.FieldEnter();
        Settle(vm);
        vm.Disarm();
        int index = vm.Document.Objects.Count - 1;
        var c = Assert.IsType<C3dCylinder>(vm.Document.Objects[index]);
        Assert.Equal(C3dAxis.X, c.Axis);
        Name(vm, index, name);
        for (int k = 0; k < 3; k++) Field(vm, name, $"Base[{k}]", baseAt[k]);
    }

    /// <summary>Boolean ▸ Subtract… / Unite…: the Tool selected first, then the Blank (D4 — the last-selected is the Blank),
    /// the dialog's Keep tools, the preview, OK.</summary>
    private void Boolean(C3dEditorViewModel vm, C3dBooleanOp op, bool keepTools, string tool, string blank)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject(tool)!.Id), Scene3DItem.OfObject(vm.SceneObject(blank)!.Id)]);
        vm.OpenBoolean(op);
        Assert.True(vm.BooleanOpen, vm.StatusMessage);
        Assert.Equal([tool, blank], vm.BooleanRows.Select(r => r.Name));
        Assert.Equal(blank, vm.BooleanRows.Single(r => r.IsBlank).Name);
        vm.BooleanKeepTools = keepTools;
        Assert.True(SpinWait.SpinUntil(() => { Drain(); return !vm.BooleanBusy && (vm.BooleanPreviewsDrawn > 0 || vm.BooleanError is not null); },
                                       TimeSpan.FromSeconds(60)), "the boolean preview never arrived");
        Settle(vm);
        Assert.True(vm.CanAcceptBoolean, vm.BooleanError);
        vm.AcceptBoolean();
        Settle(vm);
        var b = Assert.IsType<C3dBoolean>(vm.Document.Objects.Single(o => o.Name == blank));
        Assert.Equal((op, keepTools), (b.Op, b.KeepTools));
    }

    /// <summary>Port: the drawing plane, and the rectangle's two corners, snapped (the Port tool's clicks, with the
    /// editor's own AddPort commit).</summary>
    private void Port(C3dEditorViewModel vm, DrawingPlane plane, C3dDrawInput a, C3dDrawInput b)
    {
        vm.SetPlane(plane);
        var tool = new PortTool(vm, () => vm.NewPortTemplate());
        Assert.True(tool.Click(a).Advanced);
        var step = tool.Click(b);
        Assert.True(step.Finished, step.Refusal);
        vm.AddPort(tool.Made!);
        Settle(vm);
        var r = vm.PortResults.Single(p => p.Port.Number == tool.Made!.Number);
        Assert.True(r.Resolved is not null, r.Refusal);
    }

    private void Name(C3dEditorViewModel vm, int index, string name)
    {
        Select(vm, vm.Document.Objects[index].Name);
        vm.Properties.NameText = name;
        vm.Properties.CommitName();
        Assert.Equal("", vm.Properties.Error);
        Settle(vm);
    }

    private void Field(C3dEditorViewModel vm, string obj, string path, string text)
    {
        Select(vm, obj);
        var f = vm.Properties.Fields.Single(x => x.Path == path);
        f.Text = text;
        vm.Properties.CommitField(f);
        Assert.Equal("", vm.Properties.Error);
        Settle(vm);
    }

    private static void Select(C3dEditorViewModel vm, string name)
    {
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject(name)!.Id)]);
        vm.Properties.Reload();
    }

    // ── the view ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The camera on a region, from the iso view; corners in mm.</summary>
    private static void Frame(C3dEditorViewModel vm, Vector3 min, Vector3 max)
    {
        var s = vm.Viewer.Scene;
        const double k = 1000;
        Vector3 L(Vector3 p) => s.ToLocal(p.X * k * UmM, p.Y * k * UmM, p.Z * k * UmM);
        vm.Viewer.View.Camera = Camera3D.Fit(L(min), L(max), W / H);
    }

    /// <summary>A click at a world point, µm.</summary>
    private void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(false);
        Settle(vm);
    }

    private static C3dDrawInput Snap(long x, long y, long z) => new(new C3dPoint3(x, y, z), true, true, null, null);

    private void Drain()
    {
        while (_posted.TryDequeue(out var a)) a();
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            Drain();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
