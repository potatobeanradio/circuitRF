// ================================================================
//  DrawToolGateTests.cs — the gate for brief-em3d-45: the drawing plane, the grid and the tools, driven through
//  the editor's view models with synthetic clicks (a hover at a world point's projection, the CPU snap, a click).
//  Counters and document state only — no pixel is looked at; the GPU is a recording fake.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using CircuitRF.Design.Layout;
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
public sealed class DrawToolGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;                       // DBU per µm at the default 1000 DBU/µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-draw45-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public DrawToolGateTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. box in three clicks ───────────────────────────────────────────────────────────────

    [Fact]
    public void Draw1_ABoxInThreeClicks_IsTheSnappedPointsExactly_AndItsHeightSnapsToANeighboursTop()
    {
        var vm = Open();
        var v = vm.Viewer;
        vm.Arm(C3dToolKind.Box);
        ClickAt(vm, 0, 0, 0);
        ClickAt(vm, 40, 30, 0);
        Assert.Equal(2, vm.Tool!.Step);

        // The height: near the pad's top corner, the vertex snap wins and the box rises to exactly its z.
        HoverAt(v, NearestTopCorner(v, "pad"));
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        v.Click(false);
        Settle(vm);

        var box = Assert.IsType<C3dBox>(vm.Document.Objects[^1]);
        Assert.Equal("box1", box.Name);
        Assert.Equal("Gold", box.Material);
        Assert.Equal(new C3dPoint3(0, 0, 0), box.Min);
        Assert.Equal(new C3dPoint3(40 * Um, 30 * Um, 20 * Um), box.Min + box.Size);
        Assert.Equal(1, vm.ToolCommits);
    }

    /// <summary>A first corner snapped on a top corner starts the box there, not at its shadow on the plane below.</summary>
    [Fact]
    public void Draw1b_AFirstCornerSnappedAboveThePlane_MovesThePlaneThroughIt()
    {
        var vm = Open();
        var v = vm.Viewer;
        vm.Arm(C3dToolKind.Box);
        var c = NearestTopCorner(v, "pad");
        HoverAt(v, c);
        Assert.Equal(Snap3DKind.Vertex, v.Snap.Kind);
        v.Click(false);
        Assert.Equal(20 * Um, vm.Plane.OffsetDbu);

        double cx = Math.Round(c.X / UmM), cy = Math.Round(c.Y / UmM);
        double bx = cx + (cx <= 100 ? -20 : 20), by = cy + (cy <= 0 ? -20 : 20);   // off the pad, on the moved plane
        ClickAt(vm, bx, by, 20);
        Assert.Equal(2, vm.Tool!.Step);
        vm.OpenField("1");
        vm.FieldText = "15um";                                                 // the height, typed: no snap to argue with
        vm.FieldEnter();
        Settle(vm);

        var box = Assert.IsType<C3dBox>(vm.Document.Objects[^1]);
        Assert.Equal(new C3dPoint3((long)Math.Min(cx, bx) * Um, (long)Math.Min(cy, by) * Um, 20 * Um), box.Min);
        Assert.Equal(new C3dPoint3(20 * Um, 20 * Um, 15 * Um), box.Size);
    }

    // ── 2. every plane ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw2_TheSameThreeClicks_OnXyYzAndXz_GiveBoxesThatArePermutationsOfEachOther()
    {
        var sizes = new Dictionary<C3dPlane, C3dPoint3>();
        foreach (var plane in new[] { C3dPlane.XY, C3dPlane.YZ, C3dPlane.XZ })
        {
            var vm = Open();
            var p = new DrawingPlane(plane, 0);
            vm.SetPlane(p);
            Assert.Equal(plane, vm.Viewer.SnapGrid.Plane);             // the snap grid follows the plane
            vm.Arm(C3dToolKind.Box);
            ClickAt(vm, p.FromUvw(0, 0, 0));
            ClickAt(vm, p.FromUvw(40 * Um, 60 * Um, 0));
            ClickAt(vm, p.FromUvw(40 * Um, 60 * Um, 30 * Um));        // the height, from the cursor's ray
            var box = Assert.IsType<C3dBox>(vm.Document.Objects[^1]);
            Assert.Equal(new C3dPoint3(0, 0, 0), box.Min);
            sizes[plane] = box.Size;
        }
        // (u, v, normal) = (width 40, depth 60, height 30) on each plane's own axes.
        Assert.Equal(new C3dPoint3(40 * Um, 60 * Um, 30 * Um), sizes[C3dPlane.XY]);
        Assert.Equal(new C3dPoint3(30 * Um, 40 * Um, 60 * Um), sizes[C3dPlane.YZ]);
        Assert.Equal(new C3dPoint3(40 * Um, 30 * Um, 60 * Um), sizes[C3dPlane.XZ]);
    }

    // ── 3. typed values ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw3_TypedValues_AreExactDbu_AndABadEntryChangesNothing()
    {
        Assert.Equal(254_000, C3dDimension.Parse("10mil", LayoutUnit.Um, 1000).Dbu);                // exact, decimal
        Assert.Equal(C3dDimensionKind.Expression, C3dDimension.Parse("2*w", LayoutUnit.Um, 1000).Kind);
        Assert.Equal(C3dDimensionKind.Invalid, C3dDimension.Parse("12 furlongs", LayoutUnit.Um, 1000).Kind);

        var vm = Open();
        vm.Arm(C3dToolKind.Box);
        ClickAt(vm, 0, 0, 0);
        Assert.True(vm.Viewer.HandleKey(Key.D1, KeyModifiers.None, false));   // a digit opens the field …
        Assert.True(vm.FieldOpen);
        Assert.Equal("1", vm.FieldText);                                       // … with that digit typed
        vm.FieldText = "10mil";
        vm.FieldTab();                                                         // width → depth, prefilled
        Assert.Equal(1, vm.FieldIndex);
        vm.FieldText = "25um";
        vm.FieldEnter();
        Assert.False(vm.FieldOpen);
        Assert.Equal(2, vm.Tool!.Step);

        int objects = vm.Document.Objects.Count, entries = vm.UndoEntries;
        vm.OpenField("x");
        vm.FieldText = "12 furlongs";
        vm.FieldEnter();
        Assert.True(vm.FieldOpen);                                             // stays, red
        Assert.NotNull(vm.FieldError);
        vm.FieldText = "2*w";                                                  // brief-em3d-51: an unknown name asks to be defined
        vm.FieldEnter();
        Assert.True(vm.DefineOpen);
        Assert.Equal("w", Assert.Single(vm.DefineRows).Name);
        vm.DefineEscape();                                                     // … and Esc returns to the field, nothing written
        Assert.Equal(objects, vm.Document.Objects.Count);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.Equal(2, vm.Tool.Step);

        vm.FieldText = "7.5";                                                  // a bare number is the display unit
        vm.FieldEnter();
        var box = Assert.IsType<C3dBox>(vm.Document.Objects[^1]);
        Assert.Equal(new C3dPoint3(254_000, 25 * Um, 7_500), box.Size);
    }

    // ── 4. edge-on ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw4_APlaneSeenEdgeOn_IsRefused_WithTheReason()
    {
        var vm = Open();
        var v = vm.Viewer;
        v.View.Camera.SetStandardView(StandardView3D.Front);                   // looking along +y: XY is edge-on
        v.View.Camera.Projection = Projection3D.Orthographic;
        vm.Arm(C3dToolKind.Box);
        HoverVm(v, W / 2, H / 2);
        v.Click(false);
        Assert.Contains("the XY plane is edge-on; orbit or choose another plane", vm.StatusMessage);
        Assert.Equal(0, vm.Tool!.Step);

        vm.SetPlane(new DrawingPlane(C3dPlane.XZ, 0));                          // the plane facing the camera draws
        HoverVm(v, W / 2, H / 2);
        v.Click(false);
        Assert.Equal(1, vm.Tool.Step);
    }

    // ── 5. polygon self-intersection ─────────────────────────────────────────────────────────

    [Fact]
    public void Draw5_ASelfIntersectingPolygon_IsRefusedAtClose_AndTheDocumentIsUnchanged()
    {
        var vm = Open();
        string before = C3dPersistence.Serialize(vm.Document);
        vm.Arm(C3dToolKind.Polygon);
        foreach (var (x, y) in new[] { (0, 0), (40, 40), (40, 0), (0, 40) }) ClickAt(vm, x, y, 0);   // a bow tie
        vm.Viewer.HandleKey(Key.Enter, KeyModifiers.None, false);
        Assert.Contains("crosses itself", vm.StatusMessage);
        Assert.Equal(before, C3dPersistence.Serialize(vm.Document));
        Assert.Equal(0, vm.UndoEntries);
        var overlay = new Viewer3DDrawOverlay();
        vm.FillDrawOverlay(overlay);
        Assert.Equal(2, overlay.Crossing.Count);                                // the two crossing edges, highlighted

        // Take three vertices back, draw a square instead, and close it on the first vertex.
        for (int k = 0; k < 3; k++) vm.Viewer.HandleKey(Key.Back, KeyModifiers.None, false);
        ClickAt(vm, 40, 0, 0);
        ClickAt(vm, 40, 40, 0);
        ClickAt(vm, 0, 40, 0);
        ClickAt(vm, 0, 0, 0);
        var sheet = Assert.IsType<C3dSheet>(vm.Document.Objects[^1]);
        Assert.Equal([new(0, 0), new(40 * Um, 0), new(40 * Um, 40 * Um), new(0, 40 * Um)], sheet.Outline);
    }

    // ── 6. extrude ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw6_AClosedPolylineExtrudes_ToAPrismOfAreaTimesDistance_AndIsConsumed()
    {
        var vm = Open();
        vm.Arm(C3dToolKind.Polyline);
        foreach (var (x, y) in new[] { (0, 0), (60, 0), (60, 30), (10, 50), (0, 0) }) ClickAt(vm, x, y, 0);
        var line = Assert.IsType<C3dPolyline>(vm.Document.Objects[^1]);
        Assert.True(line.Closed);
        Assert.Null(line.Points3);                                               // planar: the 2D form
        vm.Disarm();
        Settle(vm);
        int count = vm.Document.Objects.Count;

        vm.SelectedTreeItem = vm.Tree.SelectMany(g => g.Items).Single(i => i.Name == line.Name);
        vm.Extrude();
        Assert.Equal(C3dToolKind.Extrude, vm.ArmedTool);
        vm.OpenField(null);
        vm.FieldText = "25";
        vm.FieldEnter();
        Settle(vm);

        Assert.Equal(count, vm.Document.Objects.Count);                          // consumed: replaced in its slot
        Assert.DoesNotContain(vm.Document.Objects, o => o is C3dPolyline);
        var prism = Assert.IsType<C3dPrism>(vm.Document.Objects[^1]);
        Assert.Equal(25 * Um, prism.Height);
        var solid = vm.Elaboration!.Solids.Single(s => s.Name == prism.Name);
        double mPerDbu = UmM / Um;
        double want = (double)Int128.Abs(DrawGeometry.TwiceArea(line.Points)) / 2 * mPerDbu * mPerDbu * 25 * UmM;
        Assert.True(Math.Abs(Em3dSizeEstimate.Volume(solid.Primitive) - want) <= 1e-12 * want,
                    $"volume {Em3dSizeEstimate.Volume(solid.Primitive)} vs {want}");
        Assert.Equal(2, vm.UndoEntries);                                         // the polyline, then ONE extrude entry
    }

    // ── 7. the grid ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw7_AHundredOrbitsWithTheGridOn_UploadNothing_AndTheMinorSpacingStaysInItsBand()
    {
        var vm = Open();
        var v = vm.Viewer;
        var fake = new PatchRecordingBackend();
        using var session = new Viewer3DSession(() => fake);
        session.EnsureBackend();
        var plan = new Scene3DFramePlan();
        plan.Plan(v.Scene, v.View, 800, 500, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        session.Frame(0, plan, 1, v.Scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        long first = fake.Counters.UploadBytesTotal;
        for (int i = 0; i < 100; i++)
        {
            v.View.Camera.Orbit(4, 1);
            plan.Plan(v.Scene, v.View, 800, 500, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(i % 3, plan, (ulong)i + 2, v.Scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, orbiting: true);
            Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Grid && d.Buffer == Scene3DBuffer.None);
        }
        Assert.Equal(first, fake.Counters.UploadBytesTotal);
        Assert.Equal(101, plan.GridFrames);

        // A zoom sweep over six decades, in perspective and orthographic, in µm and in mil: the minor spacing
        // changes in the unit's own steps and its size on screen stays in the band.
        var grid = v.View.DrawingGrid!;
        foreach (var unit in new[] { LayoutUnit.Um, LayoutUnit.Mil })
            foreach (var projection in new[] { Projection3D.Perspective, Projection3D.Orthographic })
            {
                grid.Unit = unit;
                v.View.Camera.Projection = projection;
                var spacings = new HashSet<long>();
                float d = v.View.Camera.Distance;
                for (int k = -60; k <= 60; k++)
                {
                    v.View.Camera.Distance = d * MathF.Pow(10, k / 20f);
                    var s = PlaneGrid.SpacingFor(v.Scene, v.View.Camera, grid, H);
                    Assert.InRange(s.MinorPixels, PlaneGrid.MinPixels, PlaneGrid.MaxPixels);
                    Assert.Equal(unit == LayoutUnit.Mil ? 5 : 10, s.MajorEvery);
                    spacings.Add(s.MinorDbu);
                }
                v.View.Camera.Distance = d;
                Assert.True(spacings.Count >= 12, $"{spacings.Count} spacings over six decades ({unit}, {projection})");
            }
    }

    // ── 8. undo ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Draw8_EachToolsResultIsOneUndoEntry_AndACancelledGestureAddsNone()
    {
        var vm = Open();
        vm.Arm(C3dToolKind.Box);
        ClickAt(vm, 0, 0, 0);
        ClickAt(vm, 20, 20, 0);
        vm.Viewer.HandleKey(Key.Escape, KeyModifiers.None, false);               // cancelled mid-gesture, one stage
        vm.Viewer.HandleKey(Key.Escape, KeyModifiers.None, false);               // at a time (3D round 1)
        Assert.Equal(0, vm.Tool!.Step);
        Assert.Equal(0, vm.UndoEntries);

        void Draw(C3dToolKind kind, params (double X, double Y, double Z)[] clicks)
        {
            vm.Arm(kind);
            foreach (var c in clicks) ClickAt(vm, c.X, c.Y, c.Z);
            if (kind == C3dToolKind.Polyline) vm.Viewer.HandleKey(Key.Enter, KeyModifiers.None, false);
        }
        Draw(C3dToolKind.Box, (0, 0, 0), (20, 20, 0), (20, 20, 10));
        Draw(C3dToolKind.Sheet, (0, 40, 0), (20, 60, 0));
        Draw(C3dToolKind.Polygon, (40, 40, 0), (60, 40, 0), (50, 60, 0), (40, 40, 0));
        Draw(C3dToolKind.Polyline, (0, 70, 0), (30, 70, 0));
        Draw(C3dToolKind.Cylinder, (70, 10, 0), (80, 10, 0), (70, 10, 15));      // the height above the centre
        Assert.Equal(5, vm.UndoEntries);
        Assert.Equal(["box1", "sheet1", "polygon1", "polyline1", "cylinder1"], vm.Document.Objects.Skip(1).Select(o => o.Name));
        var cyl = Assert.IsType<C3dCylinder>(vm.Document.Objects[^1]);
        Assert.Equal((C3dAxis.Z, 10 * Um, 15 * Um), (cyl.Axis, cyl.Radius, cyl.Length));

        for (int k = 0; k < 5; k++) vm.UndoRedo.Undo();
        Assert.Single(vm.Document.Objects);                                      // only the pad
    }

    // ── 3D round 1: Esc steps back one stage; the drag is the tool's; the layout's snap keys ───

    private static void Esc(Viewer3DViewModel v) => Assert.True(v.HandleKey(Key.Escape, KeyModifiers.None, false));

    [Fact]
    public void Esc1_ABoxStepsBackOneStageAtATime_ThenTheToolIsPutAway()
    {
        var vm = Open();
        var v = vm.Viewer;
        vm.Arm(C3dToolKind.Box);
        Assert.True(v.OrbitNeedsCommand);                     // armed: a plain drag is the tool's click, Ctrl/Cmd + drag orbits
        ClickAt(vm, 0, 0, 0);
        ClickAt(vm, 40, 30, 0);
        Assert.Equal(2, vm.Tool!.Step);                        // at the height
        Esc(v);
        Assert.Equal(1, vm.Tool!.Step);                        // back at the sheet's second corner, which can be placed again
        ClickAt(vm, 20, 10, 0);
        Assert.Equal(2, vm.Tool!.Step);
        Esc(v);
        Esc(v);
        Assert.IsType<BoxTool>(vm.Tool);                       // back at the first point, still armed
        Assert.Equal(0, vm.Tool!.Step);
        Esc(v);
        Assert.Null(vm.Tool);
        Assert.False(v.OrbitNeedsCommand);
        Assert.Equal(0, vm.UndoEntries);
    }

    [Fact]
    public void Esc2_AMoveGoesBackToItsBasePoint_ThenEnds_ThenTheSelectionClears()
    {
        var vm = Open();
        var v = vm.Viewer;
        var pad = v.Scene.Objects.Single(o => o.Name == "pad");
        v.SetSelection([Scene3DItem.OfObject(pad.Id)]);
        HoverVm(v, 2, 2);                                      // off the pad: the move waits for its base point
        vm.StartMove();
        var move = Assert.IsType<CircuitRF.Ui.ThreeD.Operations.MoveTool>(vm.Tool);
        Assert.Equal(0, move.Step);
        ClickAt(vm, 0, 0, 0);
        Assert.Equal(1, move.Step);
        HoverVm(v, W / 2, H / 2);
        Assert.NotNull(v.View.Preview);
        Esc(v);
        Assert.Same(move, vm.Tool);                            // choosing the base point again; nothing follows the cursor
        Assert.Equal(0, move.Step);
        Assert.Null(v.View.Preview);
        Esc(v);
        Assert.Null(vm.Tool);
        Assert.Single(v.View.Selection);                       // the tool went first ...
        Esc(v);
        Assert.Empty(v.View.Selection);                        // ... then the selection
        Assert.Equal(0, vm.UndoEntries);
    }

    [Fact]
    public void Esc3_AMeasurementGoesBeforeMeasureItself()
    {
        var vm = Open();
        var v = vm.Viewer;
        v.StartMeasure();
        Assert.True(v.OrbitNeedsCommand);
        ClickAt(vm, 0, 0, 0);
        ClickAt(vm, 40, 0, 0);
        Assert.NotNull(v.MeasureReadout);
        Esc(v);
        Assert.True(v.MeasureActive);
        Assert.Null(v.MeasureP1);
        Assert.Null(v.MeasureReadout);
        Esc(v);
        Assert.False(v.MeasureActive);
    }

    [Fact]
    public void Snap1_SOrF3TogglesGeometrySnap_AndF9TheGrid_AsInALayout()
    {
        var vm = Open();
        var v = vm.Viewer;
        v.SnapEnabled = true;
        v.SnapKinds = Snap3DKinds.Vertex | Snap3DKinds.Edge | Snap3DKinds.Grid;
        Assert.True(v.HandleKey(Key.S, KeyModifiers.None, false));
        Assert.Equal(Snap3DKinds.Grid, v.SnapKinds);           // geometry off; the grid still applies
        Assert.True(v.HandleKey(Key.F3, KeyModifiers.None, false));
        Assert.Equal(Snap3DKinds.Vertex | Snap3DKinds.Edge | Snap3DKinds.Grid, v.SnapKinds);   // back as it was
        Assert.True(v.HandleKey(Key.F9, KeyModifiers.None, false));
        Assert.Equal(Snap3DKinds.Vertex | Snap3DKinds.Edge, v.SnapKinds);
        Assert.False(v.HandleKey(Key.S, KeyModifiers.Meta, false));                       // Cmd+S stays Save's
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private C3dEditorViewModel Open()
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
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "pad", Material = "Gold", Min = new C3dPoint3(100 * Um, 0, 0), Size = new C3dPoint3(40 * Um, 40 * Um, 20 * Um) }],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        // An iso look at the drawing area and the pad beside it, about 0.7 µm a pixel.
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 80 * UmM, 80 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)),
                       "the scene never settled");

    /// <summary>A hover at (<paramref name="x"/>, <paramref name="y"/>) and the ID pass's answer for it (the CPU patch).</summary>
    private static void HoverVm(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    /// <summary>A hover 1.5 px from where world point <paramref name="p"/> lands — near enough for a vertex snap.</summary>
    private static void HoverAt(Viewer3DViewModel v, Point3 p)
    {
        var (x, y, front) = v.View.Camera.Project(v.Scene.ToLocal(p.X, p.Y, p.Z), W, H);
        Assert.True(front);
        HoverVm(v, x + 1.5f, y + 0.5f);
    }

    /// <summary>A click exactly where world point (µm) lands: the cursor's ray passes through it.</summary>
    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        HoverVm(v, sx, sy);
        v.Click(false);
    }

    private static void ClickAt(C3dEditorViewModel vm, C3dPoint3 dbu)
        => ClickAt(vm, dbu.X / (double)Um, dbu.Y / (double)Um, dbu.Z / (double)Um);

    /// <summary>The top corner of <paramref name="name"/> nearest the camera — certainly visible.</summary>
    private static Point3 NearestTopCorner(Viewer3DViewModel v, string name)
    {
        var o = v.Scene.Objects.Single(x => x.Name == name);
        var f = v.Scene.FeaturesOf(o.Id);
        var corners = Enumerable.Range(0, f.Table!.Vertices.Length).Select(f.Vertex).ToList();
        double top = corners.Max(p => p.Z);
        return corners.Where(p => Math.Abs(p.Z - top) < 1e-12).MinBy(p => v.View.Camera.ViewDepth(v.Scene.ToLocal(p.X, p.Y, p.Z)));
    }
}
