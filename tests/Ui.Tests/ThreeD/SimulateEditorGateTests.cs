// ================================================================
//  SimulateEditorGateTests.cs — brief-em3d-49's editor gates: the Setups panel's edits are document edits that
//  round-trip the .c3d byte for byte and undo (5); an air-box edit writes only the active setup (6); the stale-fields
//  banner is a view-model state (8); and a port drawn in the editor is resolved by contact, flipped and undone.
//  Pixels were not seen: these read the view model and the document.
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SimulateEditorGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sim49-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public SimulateEditorGateTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
        // A new setup's solver comes from Settings ▸ Solvers (EmSolverPreference); a test reads the default, never this machine's file.
        CircuitRF.Ui.Layout.Em.EmSolverPreference.TestOverrideActive = true;
        (CircuitRF.Ui.Layout.Em.EmSolverPreference.TestLayoutStore, CircuitRF.Ui.Layout.Em.EmSolverPreference.TestThreeDStore) = (null, null);
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 5. the panel's edits are the document's ─────────────────────────────────────────────

    [Fact]
    public void Gate5_ASetupEditFromThePanel_RoundTripsTheC3dByteForByte_AndUndoRestoresIt()
    {
        var vm = Open(Microstrip(Setup("S1")));
        string original = C3dPersistence.Serialize(vm.Document);
        var panel = Assert.IsType<EmSetupDocument>(vm.SetupEditor).ViewModel;
        Assert.True(panel.IsEmbedded);
        Assert.False(panel.ShowGeometryReference);
        Assert.DoesNotContain(panel.Solver3DChoiceList, c => c.Value == Em3dSolverChoice.None && !c.IsThermal);   // no planar kernel

        panel.Problem3DChoice = EmSetupEditorViewModel.Problem3DChoices.Single(c => c.Value == Em3dProblemType.Eigenmode);
        Settle(vm);
        Assert.False(panel.UndoRedo.CanUndo);                       // the panel's own stack stays empty
        Assert.True(vm.IsDirty);
        Assert.Equal(Em3dProblemType.Eigenmode, EmSetupPersistence.FromEmbedded(vm.Document.Setups[0]).Problem3D);
        string edited = C3dPersistence.Serialize(vm.Document);
        Assert.Null(vm.Save());
        Assert.Equal(edited, C3dPersistence.Serialize(C3dPersistence.LoadFromFile(vm.FilePath)));
        Assert.Equal(edited, File.ReadAllText(vm.FilePath));

        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Equal(original, C3dPersistence.Serialize(vm.Document));
        Assert.Equal(Em3dProblemType.Driven, vm.SetupEditor!.ViewModel.Working.Problem3D);
        vm.UndoRedo.Redo();
        Assert.Equal(edited, C3dPersistence.Serialize(vm.Document));
    }

    /// <summary>A setup panel opened before the 3D view has been built lists the placed parts once it is, with no edit: it read
    /// "not built yet", showed only the parts its setup names, and stayed so until something refreshed it.</summary>
    [Fact]
    public async Task TheComponentPortsList_FillsWhenTheViewIsBuilt_WithNoEdit()
    {
        string ws = Path.Combine(_root, "filter");
        string src = Path.Combine(Em3dTestRoot(), "examples", "PCB Component Ports");
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(ws, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        string path = Path.Combine(ws, "Filter Board", "3d", "Filter Board.c3d");
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new PatchRecordingBackend(),
                                        () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.SelectedSetupItem = vm.SetupItems.First(i => !i.IsExternal);
        var panel = vm.SetupEditor!.ViewModel;
        await panel.ComponentPartsListing;
        Assert.DoesNotContain(panel.ComponentPortRows, r => r.Refdes == "TL1");   // not built yet: only the parts the setup names

        vm.Viewer.Resized(W, H);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        await panel.ComponentPartsListing;
        Assert.Contains(panel.ComponentPortRows, r => r.Refdes == "TL1");          // placed, not named: listed from the view
    }

    private static string Em3dTestRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    /// <summary>Add starts a setup on the Preferred 3D solver (Settings ▸ Solvers).</summary>
    [Fact]
    public void Add_StartsOnThePreferred3DSolver()
    {
        try
        {
            EmSolverPreference.ThreeD = Em3dSolverId.OpenEms;
            var vm = Open(Microstrip(Setup("S1")));
            vm.AddSetup();
            var added = EmSetupPersistence.FromEmbedded(vm.Document.Setups[^1]);
            Assert.Equal(("S2", Em3dSolverChoice.One(Em3dSolverId.OpenEms)), (added.Name, added.Solver3D));
        }
        finally { EmSolverPreference.TestThreeDStore = null; }
    }

    /// <summary>With Thermal preferred, Add makes a thermal setup, as Add Thermal does.</summary>
    [Fact]
    public void Add_WithThermalPreferred_MakesAThermalSetup()
    {
        try
        {
            EmSolverPreference.ThreeDIsThermal = true;
            var vm = Open(Microstrip(Setup("S1")));
            vm.AddSetup();
            var added = EmSetupPersistence.FromEmbedded(vm.Document.Setups[^1]);
            Assert.Equal(("T1", true, true), (added.Name, added.IsThermal, added.Solver3D.IsNone));
        }
        finally { EmSolverPreference.TestThreeDStore = null; }
    }

    [Fact]
    public void TheSetupsPanel_AddsDuplicatesRenamesAndRemoves_EachOneUndoableEntry_AndKeepsTheActiveOne()
    {
        var vm = Open(Microstrip(Setup("S1")));
        int entries = vm.UndoEntries;
        vm.AddSetup();
        Assert.Equal(["S1", "S2"], vm.SetupItems.Select(i => i.Name));
        Assert.Equal("S1", vm.ActiveSetupName);
        vm.SelectedSetupItem = vm.SetupItems[0];
        vm.DuplicateSetup();
        Assert.Equal(["S1", "S1 copy", "S2"], vm.SetupItems.Select(i => i.Name));
        vm.SelectedSetupItem = vm.SetupItems[0];
        Assert.Null(vm.RenameSetup("Main"));
        Assert.Equal("Main", vm.ActiveSetupName);
        Assert.Contains("already has a setup named 'S2'", vm.RenameSetup("S2"));
        vm.SelectedSetupItem = vm.SetupItems.Single(i => i.Name == "S2");
        vm.RemoveSetup();
        Assert.Equal(["Main", "S1 copy"], vm.SetupItems.Select(i => i.Name));
        Assert.Equal(entries + 4, vm.UndoEntries);
        for (int k = 0; k < 4; k++) vm.UndoRedo.Undo();
        Assert.Equal(["S1"], vm.SetupItems.Select(i => i.Name));
    }

    /// <summary>3D editor round 1 — Simulate ▸ Setup Analyses… replaced 3D ▸ Setups…: the toolbar's tune button asks
    /// the shell for that one dialog, and each setup is an analysis card (badge + summary, or why it cannot run).</summary>
    [Fact]
    public void SetupAnalyses_TheTuneButtonAsksTheShell_AndEachSetupIsACard()
    {
        var planar = new EmSetup { Name = "Flat" };
        var eig = Setup("Modes");
        eig.Problem3D = Em3dProblemType.Eigenmode;
        var vm = Open(Microstrip(Setup("S1"), eig, planar));
        C3dEditorViewModel? asked = null;
        vm.SetupAnalysesRequested = c => asked = c;
        vm.OpenSetupAnalysesCommand.Execute(null);
        Assert.Same(vm, asked);

        Assert.Equal(["SP", "EIG", "?"], vm.SetupItems.Select(i => i.TypeLabel));
        Assert.Equal("Palace · 1–20 GHz, 101 pts", vm.SetupItems[0].Summary);
        Assert.StartsWith("Palace · eigenmodes", vm.SetupItems[1].Summary);
        Assert.Contains(C3dSetups.PlanarRefusal, vm.SetupItems[2].Summary);
        Assert.Equal("Run S1", vm.SetupItems[0].RunLabel);
    }

    // ── 6. an air-box edit writes only the active setup ─────────────────────────────────────

    [Fact]
    public void Gate6_AnAirBoxEdit_WritesOnlyTheActiveSetup()
    {
        var vm = Open(Microstrip(Setup("S1"), Setup("S2")));
        vm.SetActiveSetup("S2");
        Settle(vm);
        string s1 = vm.Document.Setups[0].GetRawText();
        Assert.Null(vm.SetAirBoxBoundary("zmin", Em3dBoundaryKind.Pec));
        Assert.Null(vm.SetAirBoxPadding("zmin", "0"));
        Settle(vm);
        Assert.Equal(s1, vm.Document.Setups[0].GetRawText());
        var s2 = EmSetupPersistence.FromEmbedded(vm.Document.Setups[1]);
        Assert.Equal(new EmAirBoxFace(0, Em3dBoundaryKind.Pec), s2.AirBox!.ZMin);
        Assert.Contains("Writes setup 'S2'", vm.StatusMessage);
        // The drawn box is the active setup's: its floor is PEC and at the geometry's bottom.
        Assert.Equal(Em3dBoundaryKind.Pec, vm.ShownAirBox!.Faces.ZMin);
        Assert.Equal(0, vm.ShownAirBox.Min.Z, 12);
        Assert.True(vm.Viewer.ShowBoundaryFaces || vm.Viewer.Scene.Objects.Any(o => o.Name == "airbox/zmin" && o.PickLast));
    }

    // ── 8. the stale-fields banner ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_AfterAnEdit_TheFieldsFromTheRunSayTheModelHasChangedSince()
    {
        var vm = Open(Microstrip(Setup("S1")));
        string results = Path.Combine(_root, "results");
        vm.ResultsRootProvider = () => results;
        var run = vm.ActiveRunSetup!;
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(Em3dRunService.RunDirectory(results, run, Em3dSolverId.Palace));
        vm.RunFinished();
        Assert.Null(vm.FieldsStaleText);

        vm.ChangeObjects("Rename", [1], o => o.Name = "line");
        Settle(vm);
        Assert.StartsWith("Fields are from the run at", vm.FieldsStaleText);
        vm.UndoRedo.Undo();
        Assert.Null(vm.FieldsStaleText);
    }

    // ── ports in the editor ─────────────────────────────────────────────────────────────────

    [Fact]
    public void APortDrawnInTheEditor_IsResolvedByContact_DrawnWithItsNumber_Flipped_AndUndone()
    {
        var vm = Open(Microstrip(Setup("S1")));
        vm.SetPlane(new DrawingPlane(C3dPlane.YZ, 0));
        var tool = new PortTool(vm, () => vm.NewPortTemplate());
        Assert.True(tool.Click(At(0, 450, 0)).Advanced);
        Assert.NotNull(tool.Preview(At(0, 550, 100)));
        var step = tool.Click(At(0, 550, 100));
        Assert.True(step.Finished);
        vm.AddPort(tool.Made!);
        Settle(vm);
        var r = Assert.Single(vm.PortResults);
        Assert.Equal(("gnd", "trace"), (r.Resolved!.NegativeObject, r.Resolved.PositiveObject));
        Assert.NotNull(vm.SceneObject("port/1"));
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.Contains(overlay.Labels, l => l.Text == "1");

        vm.FlipPorts([vm.Document.Ports[0]]);
        Settle(vm);
        Assert.Equal("trace", vm.PortResults[0].Resolved!.NegativeObject);
        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Settle(vm);
        Assert.Empty(vm.Document.Ports);
        Assert.Null(vm.SceneObject("port/1"));
    }

    /// <summary>The realistic view with its Look's Ports off draws no port number, as it draws no port sheet; with Ports on, and
    /// in the plain view, the number is there.</summary>
    [Fact]
    public void TheRealisticViewWithPortsOff_DrawsNoPortNumber()
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        var doc = Microstrip(Setup("S1"));
        doc.Ports = [new C3dPort { Number = 1, Plane = C3dPlane.YZ, Offset = 0, Rect = R(450, 0, 100, 100) }];
        var vm = Open(doc);
        Assert.NotNull(vm.PortResults[0].Resolved);
        bool Numbered()
        {
            var overlay = new Viewer3DDrawOverlay();
            vm.FillDrawOverlay(overlay);
            return overlay.Labels.Any(l => l.Text == "1");
        }
        Assert.True(Numbered());
        vm.Viewer.View.Realistic = true;
        vm.Viewer.View.Environment = CircuitRF.Render.Scene3D.Look.EnvironmentPrefilter.For(C3dLook.DefaultStudio, null);
        vm.Viewer.View.Look = CircuitRF.Render.Scene3D.Look.RealisticLook.From(new C3dLook { ShowPorts = false });
        Assert.False(Numbered());
        vm.Viewer.View.Look = CircuitRF.Render.Scene3D.Look.RealisticLook.From(new C3dLook { ShowPorts = true });
        Assert.True(Numbered());
    }

    /// <summary>A port unticked shows nothing of itself — its sheet, its number, and for a refused port its "refused" text and red
    /// outline — and stays that way across a rebuild; ticked again, all of it is back.</summary>
    [Fact]
    public void APortUnticked_DrawsNothingOfItself_ResolvedOrRefused()
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        var doc = Microstrip(Setup("S1"));
        doc.Ports =
        [
            new C3dPort { Number = 1, Plane = C3dPlane.YZ, Offset = 0, Rect = R(450, 0, 100, 100) },     // trace to ground
            new C3dPort { Number = 2, Plane = C3dPlane.YZ, Offset = 0, Rect = R(450, 300, 100, 100) },   // above everything: refused
        ];
        var vm = Open(doc);
        Assert.NotNull(vm.PortResults[0].Resolved);
        Assert.Null(vm.PortResults[1].Resolved);
        C3dTreeItem Row(string name) => vm.Tree.SelectMany(g => g.Items).Single(r => r.Kind == "Port" && r.Name == name);
        Viewer3DDrawOverlay Overlay() { var o = new Viewer3DDrawOverlay(); vm.FillDrawOverlay(o); return o; }
        var shown = Overlay();
        Assert.Contains(shown.Labels, l => l.Text == "1");
        Assert.Contains(shown.Labels, l => l.Text.EndsWith(": refused"));
        Assert.NotEmpty(shown.Crossing);

        Row("port/1").IsVisible = false;
        Row("port/2").IsVisible = false;
        vm.Viewer.Regenerate();
        Settle(vm);
        var hidden = Overlay();
        Assert.Empty(hidden.Labels);
        Assert.Empty(hidden.Crossing);
        Assert.False(vm.Viewer.View.IsVisible(vm.SceneObject("port/1")!.Id));
        Assert.False(Row("port/1").IsVisible);
        Assert.False(Row("port/2").IsVisible);

        Row("port/1").IsVisible = true;
        Row("port/2").IsVisible = true;
        var back = Overlay();
        Assert.Contains(back.Labels, l => l.Text == "1");
        Assert.Contains(back.Labels, l => l.Text.EndsWith(": refused"));
        Assert.True(vm.Viewer.View.IsVisible(vm.SceneObject("port/1")!.Id));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private static C3dDrawInput At(long x, long y, long z) => new(new C3dPoint3(x * Um, y * Um, z * Um), true, true, null, null);

    /// <summary>brief-emi-6 — Run is off for an active comparison with fewer than two solvers that can run here (openEMS has no
    /// eigensolver), its tooltip the panel's own reason, and on again when the setup can run.</summary>
    [Fact]
    public void ACompareThatCannotRun_TurnsRunOff_WithThePanelsReason()
    {
        var setup = Setup("S1");
        setup.Solver3D = Em3dSolvers.Both;
        setup.Problem3D = Em3dProblemType.Eigenmode;
        var vm = Open(Microstrip(setup));
        Assert.False(vm.SimulateActiveCommand.CanExecute(null));
        Assert.StartsWith("A comparison runs two or more solvers, and only 1 of the checked ones can run here: openEMS:", vm.SimulateBlockedReason);
        Assert.Equal(vm.SimulateBlockedReason, vm.RunSetupFor(null).Refusal);

        var panel = Assert.IsType<EmSetupDocument>(vm.SetupEditor).ViewModel;
        panel.Problem3DChoice = EmSetupEditorViewModel.Problem3DChoices.Single(c => c.Value == Em3dProblemType.Driven);
        Settle(vm);
        Assert.Null(vm.SimulateBlockedReason);
        Assert.True(vm.SimulateActiveCommand.CanExecute(null));
    }

    private static EmSetup Setup(string name) => new() { Name = name, Solver3D = Em3dSolverId.Palace };

    private static C3dDocument Microstrip(params EmSetup[] setups)
    {
        C3dRect R(long u, long v, long du, long dv) => new() { Min = new C3dPoint2(u * Um, v * Um), Size = new C3dPoint2(du * Um, dv * Um) };
        return new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "Fill", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1000 * Um, 1000 * Um, 100 * Um) },
                new C3dSheet { Name = "trace", Material = "Copper", Plane = C3dPlane.XY, Offset = 100 * Um, Rect = R(0, 450, 1000, 100) },
                new C3dSheet { Name = "gnd", Material = "Copper", Plane = C3dPlane.XY, Offset = 0, Rect = R(0, 0, 1000, 1000) },
            ],
            Setups = [.. setups.Select(EmSetupPersistence.ToEmbedded)],
        };
    }

    private C3dEditorViewModel Open(C3dDocument doc)
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "Fill", Epsr = 2 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "Cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Cell.c3d");
        C3dPersistence.SaveToFile(path, doc);
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(W, H);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");
}
