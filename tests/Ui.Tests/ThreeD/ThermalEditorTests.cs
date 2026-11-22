// ================================================================
//  ThermalEditorTests.cs — brief-em3d-75's editor gates, read off the view model and the document (pixels were not seen):
//    1  each tool's commit writes exactly brief 73's record, and undo restores the document byte for byte
//    2  Plot Temperature is enabled only with a current thermal result for the active setup: none, current, stale
//    3  Fix range across sweep: the range is the union of the two steps' own ranges, and a step keeps it
//    7  a sweep step re-reads the temperature alone: no geometry is cut or gathered again
// ================================================================

using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Thermal;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class ThermalEditorTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-th75e-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public ThermalEditorTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. round trips ───────────────────────────────────────────────────────────────────────────

    /// <summary>The toolbar's Probe clicked on a bond wire makes a WIRE probe — the record the context menu's Add Probe ▸ Wire
    /// writes — rather than a point on the wire's surface.</summary>
    [Fact]
    public void ProbeTool_ClickOnAWire_MakesAWireProbe()
    {
        var vm = Open(new C3dWire
        {
            Name = "bond", Material = "Gold", DiameterUm = 2,
            Points = [new C3dPoint3(5 * Um, 10 * Um, 20 * Um), new C3dPoint3(10 * Um, 10 * Um, 32 * Um), new C3dPoint3(15 * Um, 10 * Um, 20 * Um)],
        });
        var wire = vm.Viewer.Scene.Objects.Single(o => o.Kind == Scene3DKind.Wire);
        vm.Arm(C3dToolKind.ProbePoint);
        ClickAt(vm, 10, 10, 32);
        Settle(vm);
        var probe = Assert.Single(vm.Document.Probes);
        Assert.Equal(wire.Name, probe.Wire);
        Assert.Null(probe.Point);
        Assert.Equal(C3dProbeStat.Max, probe.Stat);
    }


    /// <summary>A heat source, a line probe, a point probe and a mesh region, each drawn with its tool: each is ONE entry
    /// writing exactly the record brief 73 defines (compared with the record built by hand, through the file's own spelling),
    /// it survives Save and reopen, and undoing all four puts the document back byte for byte.</summary>
    [Fact]
    public void Gate1_EachToolWritesItsRecord_AndUndoRestoresTheDocument()
    {
        var vm = Open();
        var asked = new List<string>();
        vm.TextRequested += (title, _, _, _) => asked.Add(title);
        string original = C3dPersistence.Serialize(vm.Document);
        int entries = vm.UndoEntries;

        vm.Arm(C3dToolKind.HeatSource);
        ClickAt(vm, 30, 0, 0);
        ClickAt(vm, 70, 30, 0);
        Settle(vm);
        vm.Arm(C3dToolKind.ProbeLine);
        ClickAt(vm, 30, 40, 0);
        ClickAt(vm, 70, 50, 0);
        Settle(vm);
        vm.Arm(C3dToolKind.ProbePoint);
        var corner = NearestTopCorner(vm.Viewer, "block");
        HoverAt(vm.Viewer, corner);
        Assert.Equal(Snap3DKind.Vertex, vm.Viewer.Snap.Kind);
        vm.Viewer.Click(false);
        Settle(vm);
        vm.Arm(C3dToolKind.MeshRegion);
        ClickAt(vm, 30, 0, 0);
        ClickAt(vm, 70, 30, 0);
        HoverAt(vm.Viewer, corner);
        vm.Viewer.Click(false);
        Settle(vm);

        C3dPoint3 D(Point3 p) => new((long)Math.Round(p.X / UmM * Um), (long)Math.Round(p.Y / UmM * Um), (long)Math.Round(p.Z / UmM * Um));
        var expected = new C3dDocument
        {
            HeatSources =
            [
                new C3dHeatSource
                {
                    Name = "source1",
                    Sheet = new C3dHeatSheet { Plane = C3dPlane.XY, Offset = 0, Rect = new C3dRect { Min = new C3dPoint2(30 * Um, 0), Size = new C3dPoint2(40 * Um, 30 * Um) } },
                },
            ],
            Probes =
            [
                new C3dProbe { Name = "probe1", Line = new C3dProbeLine { From = new C3dPoint3(30 * Um, 40 * Um, 0), To = new C3dPoint3(70 * Um, 50 * Um, 0) } },
                new C3dProbe { Name = "probe2", Point = D(corner) },
            ],
            MeshRegions =
            [
                new C3dMeshRegion { Name = "region1", Min = new C3dPoint3(30 * Um, 0, 0), Size = new C3dPoint3(40 * Um, 30 * Um, 20 * Um), SizeUm = 2 },
            ],
        };
        Assert.Equal(C3dPersistence.SerializeThermalPlaces(expected), C3dPersistence.SerializeThermalPlaces(vm.Document));
        Assert.Equal(4, vm.ToolCommits);
        Assert.Equal(entries + 4, vm.UndoEntries);
        Assert.Equal(["Heat source source1", "Mesh region region1"], asked);        // a source's name and power; a region's size

        Assert.Null(vm.Save());
        Assert.Equal(C3dPersistence.SerializeThermalPlaces(vm.Document),
                     C3dPersistence.SerializeThermalPlaces(C3dPersistence.LoadFromFile(vm.FilePath)));
        for (int k = 0; k < 4; k++) vm.UndoRedo.Undo();
        Assert.Equal(original, C3dPersistence.Serialize(vm.Document));
    }

    // ── 2. the menu's state ──────────────────────────────────────────────────────────────────────

    /// <summary>Plot Temperature (the context menu, 3D ▸ View ▸ Temperature, the toolbar) asks one predicate: no thermal result
    /// greys it, a current one enables it, and an edit after the run greys it again, each with its reason.</summary>
    [Fact]
    public void Gate2_PlotTemperature_IsEnabledOnlyWithACurrentThermalResult()
    {
        var vm = Open();
        Assert.Contains("no thermal result", vm.PlotTemperatureRefusal());
        WriteRun(vm, (x, _, _) => 30 + x);
        Assert.Null(vm.PlotTemperatureRefusal());
        vm.AddThermalPlace(new C3dProbe { Name = "late", Solid = "block" });
        Assert.Contains("stale", vm.PlotTemperatureRefusal());
    }

    // ── 3 and 7. the range across the sweep; a step rebuilds nothing ─────────────────────────────

    /// <summary>
    /// Two sweep points on one mesh, painted on every face: stepping re-reads the temperature ALONE — the triangles are the
    /// ones already drawn (no cut or gather, the geometry version unchanged) — and the range follows the step (its own true
    /// minimum and maximum). With Fix Range Across Sweep the range is the union of both steps', and a step keeps it.
    /// </summary>
    [Fact]
    public void Gate3And7_ASweepStepRevaluesOnly_AndAFixedRangeIsTheUnion()
    {
        var vm = Open();
        WriteRun(vm, (x, _, _) => 30 + x, (_, y, _) => 20 + 1.25 * y);        // step 0: 30 … 50 °C; step 1: 20 … 45 °C
        var v = vm.Viewer;
        Assert.Null(vm.SetTemperatureAllFaces(true));
        Until(() => v.FieldGeometryBuilds == 1 && v.FieldScale is not null, "the temperature was never drawn");
        Assert.Equal((30.0, 50.0), (v.FieldScale!.Lo, v.FieldScale.Hi));
        Assert.Equal(100, v.FieldScale.Percentile);
        long geometry = v.FieldGeometry.GeometryVersion, version = v.FieldGeometry.Version;

        v.TemperatureStep = 1;
        Until(() => v.FieldRevalues == 1, "the step was never shown");
        Assert.Equal(1, v.FieldGeometryBuilds);                                  // gate 7: nothing cut or gathered again
        Assert.Equal(geometry, v.FieldGeometry.GeometryVersion);
        Assert.NotEqual(version, v.FieldGeometry.Version);
        Assert.Equal((20.0, 45.0), (v.FieldScale!.Lo, v.FieldScale.Hi));         // its own range

        v.FixRangeAcrossSweep = true;
        Until(() => v.FieldGeometryBuilds == 2, "the fixed range was never taken");
        Assert.Equal((20.0, 50.0), (v.FieldScale!.Lo, v.FieldScale.Hi));         // the union of both steps
        v.TemperatureStep = 0;
        Until(() => v.FieldRevalues == 2, "the step was never shown");
        Assert.Equal((20.0, 50.0), (v.FieldScale!.Lo, v.FieldScale.Hi));         // kept across the step
        Assert.Equal(2, v.FieldGeometryBuilds);
    }

    // ── brief-em3d-86 R-em3d86-3: the sweep slider ─────────────────────────────────────────────────

    /// <summary>
    /// A three-point run swept over Pdiss (W), drawn on every face: the slider shows one axis labelled with the variable, value
    /// and unit. A drag to the third point previews (nothing written) and its release is ONE undo entry that moves the drawn
    /// plot's Solution there; the drawn values, the legend, the probe table and the hot spot all read that point. Undo puts the
    /// plot and every reading back; ▶ is one entry per press. (Read off the view model: pixels were not seen.)
    /// </summary>
    [Fact]
    public void TheSweepSlider_MovesTheDrawnPlot_EverythingReadsThePoint_AndAGestureIsOneUndoEntry()
    {
        var vm = Open();
        var sweep = new RfCore.Data.Axis("Pdiss", [5, 7, 9], "W");
        var ds = new RfCore.Data.DataSet();
        ds.AddToGroup(ThermalRunService.Group, "Energy:in", new RfCore.Data.DataCube([sweep], [5.0, 7, 9]) { Unit = "W" });
        ds.AddToGroup(ThermalRunService.Group, "T:block:max", new RfCore.Data.DataCube([sweep], [50.0, 60, 70]) { Unit = "°C" });
        Directory.CreateDirectory(Results);
        RfCore.Export.DataSetExporter.Export(ds, ThermalRunService.NpyPath(Results, vm.ActiveRunSetup!), RfCore.Export.ExportFormat.Npy);
        WriteRun(vm, (x, _, _) => 30 + x, (x, _, _) => 40 + x, (x, _, _) => 50 + x);    // each step's hottest: 50, 60, 70 °C
        var v = vm.Viewer;
        Assert.Null(vm.SetTemperatureAllFaces(true));
        Until(() => v.FieldScale is not null && vm.SweepControlVisible, "the sweep was never drawn");
        var axis = Assert.Single(vm.SweepAxes);
        Assert.Equal(("Pdiss = 5 W", 2), (axis.Label, axis.Max));
        var plot = vm.VisibleFieldPlot!;
        var first = plot.Solution;
        long stamp = vm.UndoRedo.TopUndoStamp;

        // the drag: two positions, previewed, then its release
        axis.Index = 1;
        axis.Index = 2;
        Assert.Equal(stamp, vm.UndoRedo.TopUndoStamp);                           // a preview writes nothing
        Assert.Null(vm.CommitSweepStep());
        long committed = vm.UndoRedo.TopUndoStamp;
        Assert.NotEqual(stamp, committed);
        Assert.Equal(3, vm.VisibleFieldPlot!.Solution!.Point);                  // the picker's own edit: 1-based
        Until(() => v.TemperatureStep == 2 && v.FieldScale?.Hi == 70, "the third point was never drawn");
        Assert.Equal("Pdiss = 9 W", axis.Label);
        Assert.Contains(v.FieldLegendLines(), l => l.Contains("Pdiss = 9 W"));
        Assert.StartsWith("70", v.HotSpotLabel);
        vm.RefreshProbeTable();
        Assert.Equal("Pdiss = 9 W", vm.ProbeTableHeading);
        Assert.Equal("70 °C", vm.ProbeTable.Single(r => r.Name == "T:block:max").Value);

        vm.UndoRedo.Undo();                                                      // one entry: back to where the drag began
        Assert.Equal(stamp, vm.UndoRedo.TopUndoStamp);
        Assert.True(first is null ? vm.VisibleFieldPlot!.Solution is null : first.SameAs(vm.VisibleFieldPlot!.Solution));
        Until(() => v.TemperatureStep == 0 && v.FieldScale?.Hi == 50, "undo never drew the first point");
        Assert.Equal("Pdiss = 5 W", axis.Label);

        axis.NextCommand.Execute(null);                                          // ▶: one press, one entry
        Assert.NotEqual(stamp, vm.UndoRedo.TopUndoStamp);
        Assert.Equal(2, vm.VisibleFieldPlot!.Solution!.Point);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    private string Results => Path.Combine(_root, "results");

    private C3dEditorViewModel Open(params C3dObject[] more)
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
        var setup = new EmSetup
        {
            Name = "Hot", Solver3D = Em3dSolverChoice.None, Problem3D = Em3dProblemType.Thermal,
            Thermal = new CemThermal { Boundaries = [new CemThermalBoundary { Face = "block/zmin", Kind = ThermalBoundaryKind.FixedT, TempC = "25" }] },
        };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "block", Material = "Gold", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 20 * Um) }, .. more],
            Setups = [EmSetupPersistence.ToEmbedded(setup)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue)
        {
            ResultsRootProvider = () => Results,
        };
        _open.Add(vm);
        vm.RestoreActiveSetup(null);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.Equal("Hot", vm.ActiveSetupName);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-10 * UmM, -10 * UmM, -10 * UmM), s.ToLocal(80 * UmM, 60 * UmM, 40 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    /// <summary>A thermal run of the active setup, one step per field (°C of x, y, z in µm), on a 2 × 2 × 2 block of the
    /// box's size: the field files (brief 74's writer), the group table naming the solid, and the solved document — then the
    /// editor told the run finished, as the shell tells it.</summary>
    private void WriteRun(C3dEditorViewModel vm, params Func<double, double, double, double>[] steps)
    {
        var run = vm.ActiveRunSetup!;
        string dir = ThermalRunService.RunDirectory(Results, run);
        var (mesh, _) = SyntheticThermal.Block(2, 10, 1, (_, _, _) => 0);
        var fields = steps.Select(f => Enumerable.Range(0, mesh.NodeCount)
                                                 .Select(n => f(mesh.Nodes[3 * n] * 1e6, mesh.Nodes[3 * n + 1] * 1e6, mesh.Nodes[3 * n + 2] * 1e6)).ToArray()).ToList();
        ThermalFieldFiles.Write(dir, mesh, fields, [1, 1]);
        File.WriteAllText(Path.Combine(dir, CircuitRF.Design.Em3d.GmshGeoWriter.GroupsFile),
            JsonSerializer.Serialize(new { Groups = new[] { new { Name = "block", Attribute = 1, Dimension = 3, Kind = "Conductor" } } }));
        C3dRunInputs.Take(vm.Document, vm.TopFilePath, []).KeepIn(dir);   // what the run service keeps (brief-em3d-87)
        vm.RunFinished();
        Until(() => vm.Viewer.FieldsAvailable && vm.Viewer.IsThermalRun && vm.Viewer.FieldSolutions.Count == steps.Length, "the run's fields were never read");
    }

    /// <summary>Pumps what the view model posted (the UI thread's work, on this thread) until <paramref name="done"/>.</summary>
    private void Until(Func<bool> done, string what)
        => Assert.True(SpinWait.SpinUntil(() => { while (_posted.TryDequeue(out var a)) a(); return done(); }, TimeSpan.FromSeconds(30)), what);

    private void Settle(C3dEditorViewModel vm) => Until(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, "the scene never settled");

    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    private static void HoverAt(Viewer3DViewModel v, Point3 p)
    {
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), W, H);
        Assert.True(front);
        HoverVm(v, x + 1.5f, y + 0.5f);
    }

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
        v.Click(false);
    }

    private static Point3 NearestTopCorner(Viewer3DViewModel v, string name)
    {
        var o = v.Scene.Objects.Single(x => x.Name == name);
        var f = v.Scene.FeaturesOf(o.Id);
        var corners = Enumerable.Range(0, f.Table!.Vertices.Length).Select(f.Vertex).ToList();
        double top = corners.Max(p => p.Z);
        return corners.Where(p => Math.Abs(p.Z - top) < 1e-12).MinBy(p => v.View.Camera.ViewDepth(v.Scene.ToLocal(p.X, p.Y, p.Z)));
    }
}
