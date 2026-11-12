// ================================================================
//  HierarchyGateTests.cs — the gate for brief-em3d-48: hierarchy in the 3D editor, headless. Counters, document
//  bytes and view-model text only — no pixel is looked at (overview §1n); the GPU is the recording fake.
// ================================================================

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Theming;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hierarchy;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class HierarchyGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;                       // DBU per µm at the default 1000 DBU/µm
    private const string Tech = "pcb-2layer_RO4350B_20mil_1oz";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hier48-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];
    // The UI thread, played by the test's own: what the view model posts runs here, in Settle, never on the builder's
    // thread — so an adoption can never race an edit the test is making.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted = new();

    public HierarchyGateTests()
    {
        Snap3DPreference.TestOverrideActive = true;
        Lod3DPreference.TestOverrideActive = true;
    }

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. tessellate once; a column is 20 transforms ────────────────────────────────────────

    [Fact]
    public void Gate1_A20x20ArrayOfALayoutChild_IsElaboratedAndTessellatedOnce_AndAColumnIsTwentyTransforms()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var fake = new PatchRecordingBackend();
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument { Instances = [Array20("Die", C3dInstanceView.Layout)] }), fake);
        var v = vm.Viewer;
        var plan = new Scene3DFramePlan();
        Frame(v, plan);

        var scene = v.Scene;
        int k = scene.Objects.Count(o => o.Element < 0 && o.Name.StartsWith("U1[0,0,0]/", StringComparison.Ordinal));
        Assert.True(k >= 1, string.Join(" ", scene.Notes));
        Assert.Equal(1, vm.ChildrenElaborated);
        // brief-em3d-138 — a slab whose wall a copper reaches costs two tessellations, still once per prototype: its own surface, then carved.
        int carved = scene.Batches.Count(b => b.Free && b.Element < 0);
        Assert.True(k + carved == vm.TessellationMisses, $"{vm.TessellationMisses} tessellations for {k} + {carved} carved: " +
            string.Join(", ", scene.Objects.Where(o => o.Element < 0).Select(o => o.Name)));   // the prototype's objects, once
        Assert.Equal(399, scene.Elements.Length);
        Assert.Equal(400 * k, scene.Objects.Count(o => o.Name.StartsWith("U1[", StringComparison.Ordinal)));
        Assert.All(scene.Objects.Where(o => o.Element >= 0), o => Assert.Equal(scene.Object(o.Prototype)!.FirstVertex, o.FirstVertex));
        // One draw per element for its opaque triangles, not one per object.
        int elementDraws = plan.Draws.Take(plan.DrawCount).Count(d => d.Pipeline == Scene3DPipeline.Opaque && d.Transform >= 1 && d.Transform <= 399);
        Assert.Equal(399, elementDraws);

        // A column more: the child is not read again, nothing is tessellated, no geometry byte of it moves — 20 transforms.
        long uploads = fake.Counters.UploadBytesTotal;
        var before = scene;
        vm.ChangeInstance("Add a column", 0, i => i.Array!.Counts = [21, 20, 1]);
        Settle(vm);
        Frame(v, plan);
        Assert.Equal(1, vm.ChildrenElaborated);
        Assert.Equal(k + carved, vm.TessellationMisses);
        Assert.Equal(419, v.Scene.Elements.Length);
        var patch = Scene3DPatch.Between(before, v.Scene);
        Assert.NotNull(patch);
        // 3D editor round 3 — the one geometry that moves is the air box (drawn with no setup too), which grows with the
        // array: its six faces' 24 vertices and its twelve edges' 24 line vertices (every one of them, since the default
        // padding is the structure's extent and grows on every side), and nothing of the array itself.
        Assert.Equal(2 * 24 * Scene3DVertex.Stride, patch!.Bytes);
        Assert.Equal(20, patch.ElementTransforms);
        Assert.Equal(uploads + patch.Bytes, fake.Counters.UploadBytesTotal);
        Assert.Equal(20, fake.ElementTransforms);
    }

    // ── 2. a pick names the element ──────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_APickInElement7_3_ReturnsThatElementAndItsFace_AndTheCpuAndSoftwareIdPathsAgree()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument { Instances = [Array20("Die", C3dInstanceView.Layout)] }));
        var v = vm.Viewer;
        var scene = v.Scene;
        // The element's top copper: the highest conductor, and of those the widest rectangle.
        var element = scene.Objects.Where(o => o.Name.StartsWith("U1[7,3,0]/", StringComparison.Ordinal) && o.Kind == Scene3DKind.Conductor).ToList();
        float topZ = element.Max(o => o.Max.Z);
        var trace = element.Where(o => o.Max.Z == topZ).MaxBy(o => o.Max.X - o.Min.X)!;
        Assert.True(trace.Element >= 0, "element [7,3] should be drawn from the prototype");
        // Straight down onto the element's copper, from above.
        var centre = (trace.Min + trace.Max) * 0.5f;
        var cam = Camera3D.Fit(trace.Min - new Vector3(20e-6f), trace.Max + new Vector3(20e-6f), W / H);
        cam.Yaw = 0; cam.Pitch = MathF.PI / 2 - 1e-3f;
        cam.Target = centre;
        var (px, py, front) = cam.Project(new Vector3(centre.X, centre.Y, trace.Max.Z), W, H);
        Assert.True(front);

        var (id, face) = Scene3DPicking.PairAtPixel(scene, cam, px, py, W, H, v.View.Visible);
        var (rayId, _) = Scene3DPicking.Pick(scene, cam, px, py, W, H, v.View.Visible);
        Assert.Equal(trace.Id, id);
        Assert.Equal(id, rayId);
        Assert.Equal("top", scene.Object(id)!.FaceName((int)face));
        Assert.Equal("U1[7,3,0]", vm.InstanceOf(scene.Object(id)!));
    }

    /// <summary>Gate 2 on the real GPU (macOS): the Metal ID pass, drawing an element from its prototype's triangles under
    /// the element's transform and id offset, writes the ELEMENT's object — what the software ID pass computes.</summary>
    [Fact]
    public void Gate2_OnMetal_TheIdPassWritesTheElementsObject()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument { Instances = [Array20("Die", C3dInstanceView.Layout)] }));
        var scene = vm.Viewer.Scene;
        using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        const int w = 320, h = 200;
        metal.CreateOffscreenImages(w, h, 1);
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Camera.Yaw = -0.7f; view.Camera.Pitch = 0.9f;
        view.Adopt(scene, null);
        var session = new Viewer3DSession(() => metal);
        session.EnsureBackend();

        (int X, int Y, uint Id)? hit = null;
        for (int y = 0; y < h && hit is null; y += 3)
            for (int x = 0; x < w; x += 3)
                if (Scene3DPicking.IdAtPixel(scene, view.Camera, x, y, w, h, view.Visible) is var id && scene.Object(id) is { Element: > 5 })
                { hit = (x, y, id); break; }
        Assert.NotNull(hit);
        view.CursorX = hit.Value.X; view.CursorY = hit.Value.Y;
        var plan = new Scene3DFramePlan();
        for (ulong f = 1; f <= 3; f++)
        {
            plan.Plan(scene, view, w, h, metal.FlipY, pick: true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        Assert.True(metal.PickedSomething);
        Assert.Equal(hit.Value.Id, metal.PickedId);
    }

    // ── 3. the encoding's limit ──────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AChildBeyondTheObjectLimit_IsRefusedAtPlacementWithTheNumbers_AndNothingIsWritten()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        string pkg = C3dCell(ws, "Pkg", new C3dDocument());
        var vm = Open(pkg, settleOnObjects: false);
        string cellDir = Path.Combine(ws, "Die");
        Assert.Null(vm.BeginInstancePlacement("../../Die", cellDir, C3dInstanceView.Layout));
        Assert.IsType<PlaceInstanceTool>(vm.Tool);
        int k = CountChild(vm, cellDir);
        vm.Disarm();

        vm.MaxChildObjects = k - 1;
        string? why = vm.BeginInstancePlacement("../../Die", cellDir, C3dInstanceView.Layout);
        Assert.NotNull(why);
        Assert.Contains($"{k:N0} objects", why, StringComparison.Ordinal);
        Assert.Contains($"at most {k - 1:N0}", why, StringComparison.Ordinal);
        Assert.Null(vm.Tool);
        Assert.Empty(vm.Document.Instances);
        Assert.False(vm.IsDirty);
    }

    // ── 4. swap view ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_ASwapStrandsAPortNamingU1Trace_ReportsIt_KeepsTheSwap_AndUndoRestoresTheView()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        C3dCell(ws, "Die", new C3dDocument { Objects = [Box("trace", 0, 0, 0, 100, 20, 5)] });
        var doc = new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die" }] };
        doc.Ports.Add(new C3dPort { Number = 1, Name = "P1", Positive = "U1/trace" });
        var vm = Open(C3dCell(ws, "Pkg", doc));
        Assert.NotNull(vm.SceneObject("U1/trace"));

        string said = vm.SwapView(0);
        Assert.Equal(C3dInstanceView.Layout, vm.Document.Instances[0].View);
        Assert.Equal(["U1/trace"], vm.LastSwapMissing);
        Assert.Contains("U1/trace", said, StringComparison.Ordinal);
        Assert.Contains("swap is kept", said, StringComparison.Ordinal);

        vm.UndoRedo.Undo();
        Assert.Equal(C3dInstanceView.ThreeD, vm.Document.Instances[0].View);
        Assert.Equal("U1/trace", vm.Document.Ports[0].Positive);
    }

    // ── 5. cycles at edit time ───────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_PlacingAInB_WhereBHoldsA_IsRefusedWithThePath_AndACellsOwnLayoutInItsOwn3DViewIsAllowed()
    {
        string ws = Workspace();
        LayoutCell(ws, "A");
        string a = C3dCell(ws, "A", new C3dDocument { Objects = [Box("pad", 0, 0, 0, 10, 10, 2)] });
        C3dCell(ws, "B", new C3dDocument { Instances = [new C3dInstance { Name = "U1", CellRef = "../../A" }] });
        var vm = Open(a);
        string bytes = File.ReadAllText(a);

        string? why = vm.BeginInstancePlacement("../../B", Path.Combine(ws, "B"), C3dInstanceView.ThreeD);
        Assert.NotNull(why);
        Assert.Contains("A → B → A", why, StringComparison.Ordinal);
        Assert.Null(vm.Tool);
        Assert.Empty(vm.Document.Instances);
        Assert.Equal(bytes, File.ReadAllText(a));

        // A's 3D view placing A's 3D view is the direct cycle; A's LAYOUT is not a cycle — the views differ.
        Assert.NotNull(vm.BeginInstancePlacement("..", Path.Combine(ws, "A"), C3dInstanceView.ThreeD));
        Assert.Null(vm.BeginInstancePlacement("..", Path.Combine(ws, "A"), C3dInstanceView.Layout));
        vm.PlaceArmedAt(new C3dPoint3(0, 0, 10 * Um));
        Assert.Equal(C3dInstanceView.Layout, Assert.Single(vm.Document.Instances).View);
        Settle(vm);
        Assert.True(vm.Elaboration!.Ok, string.Join(" ", vm.Elaboration.Refusals));
    }

    // ── 6. flatten, then group, round-trips the problem ──────────────────────────────────────

    [Fact]
    public void Gate6_FlattenThenGroupIntoCell_RoundTripsTheElaboratedProblem()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", View = C3dInstanceView.Layout,
                                           Placement = new C3dPlacement { Origin = new C3dPoint3(300 * Um, -40 * Um, 0) } }],
        }));
        var original = Solids(vm.Elaboration!);

        var (result, question) = vm.PlanFlatten(0);
        Assert.True(result is not null, vm.StatusMessage);
        Assert.Contains($"{result!.Objects.Count}", question, StringComparison.Ordinal);    // the count, stated first
        vm.ApplyFlatten(0, result);
        Settle(vm);
        Assert.Empty(vm.Document.Instances);
        Assert.True(vm.Elaboration!.Ok, string.Join(" ", vm.Elaboration.Refusals));
        Assert.Equal(original, Solids(vm.Elaboration));

        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection(vm.Viewer.Scene.Objects.Select(o => Scene3DItem.OfObject(o.Id)));
        Assert.Null(vm.GroupIntoCell("Grp"));
        Settle(vm);
        Assert.Empty(vm.Document.Objects);
        Assert.Equal(C3dInstanceView.ThreeD, Assert.Single(vm.Document.Instances).View);
        Assert.True(File.Exists(Path.Combine(ws, "Grp", "3d", "Grp.c3d")));
        Assert.Equal(original, Solids(vm.Elaboration!));

        // Undo: the objects are back, the new cell stays (R-L3c-6).
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.Instances);
        Assert.Equal(result.Objects.Count, vm.Document.Objects.Count);
        Assert.True(Directory.Exists(Path.Combine(ws, "Grp")));
    }

    /// <summary>Undoing a Flatten with its objects selected: the menu asks what the selection is before the restored scene is
    /// adopted, and the selection's cached indices named objects the undo had just removed — an out-of-range crash.</summary>
    [Fact]
    public void UndoFlatten_WithTheFlattenedObjectsSelected_AsksTheMenuWithoutIndexingPastTheObjects()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", View = C3dInstanceView.Layout }],
        }));
        var (result, _) = vm.PlanFlatten(0);
        vm.ApplyFlatten(0, result!);
        Settle(vm);
        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        vm.Viewer.SetSelection(vm.Viewer.Scene.Objects.Select(o => Scene3DItem.OfObject(o.Id)));
        Assert.NotEmpty(vm.Targets());

        vm.UndoRedo.Undo();                                      // no Settle: the flattened scene is still the adopted one
        Assert.Empty(vm.Document.Objects);
        Assert.False(vm.CanRunModify("ReseatWires"));
        Assert.All(vm.Targets(), t => Assert.True(t.Instance ? t.Index < vm.Document.Instances.Count : t.Index < vm.Document.Objects.Count));
    }

    /// <summary>A plated via's core is the built-in Air, which the shipped technology does not define: flatten keeps it as an
    /// object of Air and the problem is unchanged. A refusal is handed to the shell's dialog, not only the status line.</summary>
    [Fact]
    public async Task Flatten_APlatedViasAirCoreNeedsNoAirInTheTechnology_AndARefusalReachesTheDialog()
    {
        // A via row that names no material and states σ 0 (every shipped PCB technology's until 2026-09-30) — nothing in the
        // technology has those values: refused.
        string ws = Workspace();
        var tech = TechPersistence.LoadFromFile(Path.Combine(ws, "tech.ctech"));
        var via = tech.Stackup!.Layers.Single(l => l.Kind == StackupKind.Via);
        (via.Material, via.SigmaSm) = (null, 0);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), tech);
        ViaCell(ws, "Board");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Board", View = C3dInstanceView.Layout }],
        }));
        var told = new List<string>();
        vm.Inform = s => { told.Add(s); return Task.CompletedTask; };
        await vm.FlattenAsync(0);
        Assert.Single(vm.Document.Instances);
        string why = Assert.Single(told);
        Assert.Contains("'Plated Through-Hole'", why, StringComparison.Ordinal);
        Assert.DoesNotContain("'Air'", why, StringComparison.Ordinal);
        await vm.FlattenAllAsync();
        Assert.Equal(2, told.Count);

        // The shipped technology, whose via row is Copper: nothing is left unmatched, and the core lands as an object of the
        // built-in Air.
        ws = Workspace();
        Assert.Null(TechPersistence.LoadFromFile(Path.Combine(ws, "tech.ctech")).FindMaterial("Air"));
        ViaCell(ws, "Board");
        vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Board", View = C3dInstanceView.Layout }],
        }));
        var original = Solids(vm.Elaboration!);
        var colours = Colours(vm);
        told.Clear();
        await vm.FlattenAsync(0);
        Settle(vm);
        Assert.Empty(told);
        Assert.Empty(vm.Document.Instances);
        Assert.Contains(vm.Document.Objects, o => o.Material == "Air");
        Assert.True(vm.Elaboration!.Ok, string.Join(" ", vm.Elaboration.Refusals));
        Assert.Equal(original, Solids(vm.Elaboration));

        // A flattened conductor remembers its drawing layer and draws in that layer's colour, as the placed layout drew it —
        // the pad's layer and the barrel's differ — never the stackup edge ink.
        Assert.Equal(colours, Colours(vm));
        Assert.True(vm.Viewer.Scene.Objects.Where(o => o.Material == "Copper").Select(o => o.Rgba).Distinct().Count() > 1);
    }

    /// <summary>A stackup whose dielectric rows state their values and name no material (a Gerber import's): flatten offers
    /// the materials, ONE PER ROW even where the rows agree (each slab keeps saying which row it is), adds them when confirmed,
    /// and keeps the problem; a second instance of the same cell, flattened by a later command, lands on those materials by name
    /// and adds nothing.</summary>
    [Fact]
    public async Task Flatten_DielectricRowsNamingNoMaterial_AddsOneMaterialPerRow_AndASecondInstanceAddsNone()
    {
        string ws = Workspace();
        string techPath = Path.Combine(ws, "tech.ctech");
        var tech = TechPersistence.LoadFromFile(techPath);
        var layers = tech.Stackup!.Layers;
        int at = layers.FindIndex(l => l.Kind == StackupKind.Dielectric);
        var core = layers[at];
        (core.Material, core.Epsr, core.TanD, core.Name) = (null, 3.17, 0.0041, "Dielectric 1");
        core.ThicknessDbu /= 2;
        var second = TechPersistence.Clone(tech).Stackup!.Layers[at];
        second.Name = "Dielectric 2";
        layers.Insert(at + 1, second);
        TechPersistence.SaveToFile(techPath, tech);
        LayoutCell(ws, "Die");
        string c3d = C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", View = C3dInstanceView.Layout },
                         new C3dInstance { Name = "U2", CellRef = "../../Die", View = C3dInstanceView.Layout,
                                           Placement = new C3dPlacement { Origin = new C3dPoint3(1000 * Um, 0, 0) } }],
        });
        var vm = Open(c3d);
        var original = Solids(vm.Elaboration!);
        string? asked = null;
        var added = new List<IReadOnlyList<TechMaterial>>();
        vm.Confirm = q => { asked = q; return Task.FromResult(true); };
        vm.AddMaterials = m =>
        {
            added.Add(m);
            var t = TechPersistence.LoadFromFile(techPath);
            t.Materials.AddRange(m);
            TechPersistence.SaveToFile(techPath, t);
            return Task.FromResult<string?>(null);
        };

        await vm.FlattenAsync(0);
        Assert.Equal(["Dielectric 1", "Dielectric 2"], Assert.Single(added).Select(m => m.Name));
        Assert.Contains("'Dielectric 1' (εr 3.17, tanδ 0.0041) and 'Dielectric 2' (εr 3.17, tanδ 0.0041)", asked, StringComparison.Ordinal);
        Assert.Equal("U2", Assert.Single(vm.Document.Instances).Name);
        Assert.Contains(vm.Document.Objects, o => o.Material == "Dielectric 2");

        // The next command, on the technology as it now stands: nothing to add, the same materials, the same problem.
        C3dPersistence.SaveToFile(c3d, vm.Document);
        vm = Open(c3d);
        var materialsBefore = vm.Document.Objects.Select(o => o.Material).ToHashSet();
        added.Clear();
        vm.Confirm = q => { asked = q; return Task.FromResult(true); };
        vm.AddMaterials = m => { added.Add(m); return Task.FromResult<string?>(null); };
        await vm.FlattenAsync(0);
        Settle(vm);
        Assert.Empty(added);
        Assert.DoesNotContain("no material", asked, StringComparison.Ordinal);
        Assert.Empty(vm.Document.Instances);
        Assert.Equal(materialsBefore, vm.Document.Objects.Select(o => o.Material).ToHashSet());
        Assert.True(vm.Elaboration!.Ok, string.Join(" ", vm.Elaboration.Refusals));
        Assert.Equal(original, Solids(vm.Elaboration));
    }

    /// <summary>A flatten that brings up more than one part gathers them in a group named after the instance, says so first,
    /// and leaves that group selected; Ungroup gives the loose parts back.</summary>
    [Fact]
    public void Flatten_GathersItsPartsInAGroupNamedAfterTheInstance_AndSelectsIt()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", View = C3dInstanceView.Layout }],
        }));
        var (result, question) = vm.PlanFlatten(0);
        Assert.True(result is not null, vm.StatusMessage);
        Assert.True(result!.Objects.Count > 1);
        Assert.Equal("U1", result.Group);
        Assert.Contains("in group 'U1'", question, StringComparison.Ordinal);

        var colours = Colours(vm);
        vm.ApplyFlatten(0, result);
        Settle(vm);
        Assert.All(vm.Document.Objects, o => Assert.Equal("U1", o.Group));
        // Drawn as it was: each copper solid remembers its drawing layer and takes that layer's colour, not its material's.
        Assert.Contains(vm.Document.Objects, o => o.DrawingLayer == new LayerKey(1, 0));
        Assert.Equal(colours, Colours(vm));
        Assert.DoesNotContain("DrawingLayer", C3dPersistence.SerializeForRun(vm.Document), StringComparison.Ordinal);
        Assert.True(vm.Viewer.Selection.Count > 1);
        Assert.Equal(vm.SceneObjectsOfGroup("U1").Count, vm.Viewer.Selection.Count);
        Assert.Equal("U1", Assert.Single(vm.SelectedTreeItems).GroupPath);

        vm.Ungroup("U1");
        Assert.All(vm.Document.Objects, o => Assert.Null(o.Group));
    }

    /// <summary>A 3D child's groups come up beneath the new group under names free in the parent, inside the instance's own
    /// group; a name already taken gives <c>U1_2</c>, not <c>U2</c>. A child of one object makes no group of one.</summary>
    [Fact]
    public void FlattenOne_NestsTheChildsGroupsUnderTheInstancesGroup_AndASinglePartMakesNone()
    {
        string ws = Workspace();
        C3dCell(ws, "Pair", new C3dDocument
        {
            Objects = [new C3dBox { Name = "a", Size = new(10 * Um, 10 * Um, 10 * Um), Group = "match" },
                       new C3dBox { Name = "b", Min = new(20 * Um, 0, 0), Size = new(10 * Um, 10 * Um, 10 * Um) }],
        });
        C3dCell(ws, "One", new C3dDocument { Objects = [new C3dBox { Name = "c", Size = new(10 * Um, 10 * Um, 10 * Um), Group = "match" }] });
        var parent = new C3dDocument
        {
            Objects = [new C3dBox { Name = "lid", Size = new(Um, Um, Um), Group = "U1/match" }],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Pair", Group = "pa" },
                         new C3dInstance { Name = "U3", CellRef = "../../One", Group = "pa" }],
        };
        string path = C3dCell(ws, "Pkg", parent);
        string cws = Path.Combine(ws, ".cws");
        var e = C3dElaborator.ElaborateOnce(parent, path, cws);

        var r = C3dHierarchy.FlattenOne(parent, path, 0, e, e.Technology, null, cws);
        Assert.Null(r.Refusal);
        Assert.Equal("pa/U1_2", r.Group);
        Assert.Equal(["pa/U1_2/match_2", "pa/U1_2"], r.Objects.Select(o => o.Group));

        var one = C3dHierarchy.FlattenOne(parent, path, 1, e, e.Technology, null, cws);
        Assert.Null(one.Group);
        Assert.Equal("pa", Assert.Single(one.Objects).Group);
    }

    // ── 7. New 3D View from Layout ───────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_New3DViewFromLayout_OnThePackageExample_AssemblesBrief42sEquivalenceOracle()
    {
        string ws = CopyExample();
        string cws = Path.Combine(ws, ".cws");
        string cell = Path.Combine(ws, "Package");
        string path = Path.Combine(cell, "3d", "Package.c3d");
        var made = C3dHierarchy.NewFromLayout(cell, path, cws);
        Assert.Null(made.Refusal);
        Assert.Contains("Package C", made.SetupsCopied);
        var doc = made.Document!;
        Assert.Equal("U1", Assert.Single(doc.Instances).Name);
        Assert.Equal(C3dInstanceView.Layout, doc.Instances[0].View);
        CellCreate.WriteThreeDView(cell, "Package", doc);

        // Brief 42's oracle: one layout instance at the origin with an embedded copy of Package C.
        var cemSetup = EmSetupPersistence.LoadFromFile(Path.Combine(cell, "em", "Package C.cem"));
        string oraclePath = Path.Combine(ws, "Assembly", "3d", "Assembly.c3d");
        Directory.CreateDirectory(Path.GetDirectoryName(oraclePath)!);
        C3dPersistence.SaveToFile(oraclePath, new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Package", View = C3dInstanceView.Layout }],
            Setups = [EmSetupPersistence.ToEmbedded(cemSetup)],
        });
        Em3dProblem Assembled(string c3d)
        {
            var d = C3dPersistence.LoadFromFile(c3d);
            var (s, why) = C3dSetups.Select(d, "Package C");
            Assert.Null(why);
            var g = C3dProblemAssembly.Assemble(C3dSetups.ForRun(s!, c3d), d, c3d, cws);
            Assert.True(g.Ok, g.Refusal);
            return g.Problem!;
        }
        var (p, q) = (Assembled(oraclePath), Assembled(path));
        Assert.Equal(p.Solids.Select(Describe).Order(), q.Solids.Select(Describe).Order());
        Assert.Equal(p.Sheets.Select(s => s.Name).Order(), q.Sheets.Select(s => s.Name).Order());
        Assert.Equal(p.Materials.OrderBy(m => m.Name).Select(m => m with { EpsrTensor = null }),
                     q.Materials.OrderBy(m => m.Name).Select(m => m with { EpsrTensor = null }));
        Assert.Equal(p.Terminals.Select(t => (t.Name, string.Join(",", t.Objects))), q.Terminals.Select(t => (t.Name, string.Join(",", t.Objects))));
        Assert.Equal((p.Boundary.Min, p.Boundary.Max, p.Boundary.Faces), (q.Boundary.Min, q.Boundary.Max, q.Boundary.Faces));

        static string Describe(Em3dSolid s) => $"{s.Name} {s.Material} {s.Role} {Em3dGeneratorDumpTests.Prim(s.Primitive)}";
    }

    // ── 8. rename and remove with placed instances of both views ─────────────────────────────

    [Fact]
    public void Gate8_RenameAndRemoveCell_FollowPlacedInstancesOfBothViews()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        C3dCell(ws, "Die", new C3dDocument { Objects = [Box("trace", 0, 0, 0, 100, 20, 5)] });
        string pkg = C3dCell(ws, "Pkg", new C3dDocument());
        var vm = Open(pkg, settleOnObjects: false);
        string die = Path.Combine(ws, "Die");
        Assert.Null(vm.BeginInstancePlacement("../../Die", die, C3dInstanceView.ThreeD));
        vm.PlaceArmedAt(new C3dPoint3(0, 0, 0));
        Assert.Null(vm.BeginInstancePlacement("../../Die", die, C3dInstanceView.Layout));
        vm.PlaceArmedAt(new C3dPoint3(500 * Um, 0, 0), bottomCentre: true);
        Assert.Equal(["U1", "U2"], vm.Document.Instances.Select(i => i.Name));
        Assert.Null(vm.Save());

        Assert.Equal(1, CellUsageScanner.CountReferencingCells(ws, die).Count);
        Directory.Move(die, Path.Combine(ws, "Chip"));
        var rewritten = CellUsageScanner.RewriteCellReferences(ws, die, "Chip", out var failed);
        Assert.Empty(failed);
        Assert.Equal([pkg], rewritten);
        var back = C3dPersistence.LoadFromFile(pkg);
        Assert.Equal(["../../Chip", "../../Chip"], back.Instances.Select(i => i.CellRef));
        Assert.Equal([C3dInstanceView.ThreeD, C3dInstanceView.Layout], back.Instances.Select(i => i.View));
        Assert.Equal(1, CellUsageScanner.CountReferencingCells(ws, Path.Combine(ws, "Chip")).Count);
    }

    // ── 9. LOD is said ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate9_OverTheTriangleBudget_TheStatusTextNamesTheElementsDrawnAsBoxes()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument { Instances = [Array20("Die", C3dInstanceView.Layout)] }));
        var v = vm.Viewer;
        var plan = new Scene3DFramePlan();
        Frame(v, plan);
        v.FramePlanned(plan);
        Assert.Equal("", v.LodText);

        var scene = v.Scene;
        long owned = scene.Batches.Take(scene.OwnedBatches).Sum(b => (long)b.IndexCount / 3);
        long perElement = scene.Groups.Sum(g => (long)g.Triangles);
        plan.TriangleBudget = owned + 10 * perElement;
        Frame(v, plan);
        v.FramePlanned(plan);
        Assert.Equal(389, plan.LodBoxedElements);
        Assert.Equal(Viewer3DViewModel.LodMessage(389, 399, plan.TriangleBudget), v.LodText);
        Assert.Contains("389 of 399", v.LodText, StringComparison.Ordinal);
        Assert.Equal(389, plan.Draws.Take(plan.DrawCount).Count(d => d.Pipeline == Scene3DPipeline.Lines && d.First == scene.UnitBox.FirstVertex
                                                                    && d.Count == scene.UnitBox.VertexCount));
    }

    // ── §4: push in, edit in the child's frame, pop out saving or discarding ──────────────────

    [Fact]
    public void PushIn_EditsTheChildAloneInItsOwnFrame_AndPopOutSavesOrDiscards_NeverLoses()
    {
        string ws = Workspace();
        string die = C3dCell(ws, "Die", new C3dDocument { Objects = [Box("pad", 0, 0, 0, 10, 10, 2)] });
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Objects = [Box("lead", -50, 0, 0, 40, 10, 2)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", Placement = new C3dPlacement { Origin = new C3dPoint3(1000 * Um, 0, 0) } }],
        }));
        string pkg = vm.FilePath;
        string dieBytes = File.ReadAllText(die);
        string pkgBytes = File.ReadAllText(pkg);

        vm.Viewer.SelectMode = Scene3DSelectMode.Object;
        // The toolbar's pair: Push Into Cell enables on one selected instance, Pop Out once pushed in.
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.CanPushIn);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("lead")!.Id)]);
        Assert.False(vm.CanPushIn);
        vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("U1/pad")!.Id)]);
        Assert.True(vm.CanPushIn);
        Assert.Contains(nameof(vm.CanPushIn), raised);
        Assert.False(vm.CanPopOut);
        Assert.Null(vm.PushIntoSelected());
        Settle(vm);
        Assert.True(vm.CanPopOut);
        Assert.Equal(die, vm.FilePath);
        Assert.Equal(pkg, vm.TopFilePath);
        // The child in its own frame, and ONLY the child: the parent is not drawn at all, as the layout editor draws only
        // the cell pushed into (owner, 2026-10-04 — brief-em3d-48's dimmed parent read as part of the child).
        var pad = vm.SceneObject("pad")!;
        Assert.Equal(0, vm.Viewer.Scene.ToWorld(pad.Min).X, 1e-12);
        Assert.DoesNotContain(vm.Viewer.Scene.Objects, o => o.Name.Contains("lead", StringComparison.Ordinal) || o.Context);

        vm.ChangeObjects("Grow", [0], o => ((C3dBox)o).Size = new C3dPoint3(30 * Um, 10 * Um, 2 * Um));
        Assert.True(vm.IsDirty);
        Assert.True(vm.PopOut(C3dPopOutChoice.Discard));
        Assert.Equal(pkg, vm.FilePath);
        Assert.False(vm.IsDirty);
        Assert.Equal(dieBytes, File.ReadAllText(die));

        Assert.Null(vm.PushInto(0));
        Settle(vm);
        vm.ChangeObjects("Grow", [0], o => ((C3dBox)o).Size = new C3dPoint3(30 * Um, 10 * Um, 2 * Um));
        Assert.False(vm.PopOut(C3dPopOutChoice.Cancel));
        Assert.Equal(die, vm.FilePath);
        Assert.True(vm.PopOut(C3dPopOutChoice.Save));
        Settle(vm);
        Assert.Equal(30 * Um, ((C3dBox)C3dPersistence.LoadFromFile(die).Objects[0]).Size.X);
        // The child is saved to ITS file: the parent's is untouched (a save through the top document's source wrote the
        // child's content over the parent's .c3d).
        Assert.Equal(pkgBytes, File.ReadAllText(pkg));
        var grown = (Em3dBox)vm.Elaboration!.Solids.Single(s => s.Name == "U1/pad").Primitive;
        Assert.Equal(30e-6, grown.Max.X - grown.Min.X, 1e-12);
    }

    /// <summary>Designer round 32 — a placed layout saved while the file watcher said nothing (an overflow, a synced
    /// folder): re-checking the files the scene was built from, as activating the tab does, elaborates it again.</summary>
    [Fact]
    public void RecheckInputs_APlacedLayoutChangedUnseen_IsElaboratedAgain()
    {
        string ws = Workspace();
        LayoutCell(ws, "Die");
        var vm = Open(C3dCell(ws, "Pkg", new C3dDocument
        {
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../Die", View = C3dInstanceView.Layout }],
        }));
        int before = vm.Elaboration!.Solids.Count;

        string clay = Path.Combine(ws, "Die", "layout", "Die.clay");
        var view = LayoutPersistence.LoadFromFile(clay);
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 300 * Um, Y1 = 0, X2 = 400 * Um, Y2 = 50 * Um });
        LayoutPersistence.SaveToFile(clay, view);
        File.SetLastWriteTimeUtc(clay, DateTime.UtcNow.AddMinutes(1));

        long requested = vm.Viewer.Source.Requested;
        vm.RecheckInputs();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.Viewer.Source.Requested > requested;
        }, TimeSpan.FromSeconds(10)), "no rebuild was asked for");
        Settle(vm);
        Assert.Equal(2, vm.ChildrenElaborated);
        Assert.True(vm.Elaboration!.Solids.Count > before);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A 20 × 20 array of <paramref name="cell"/>'s view, 400 µm apart.</summary>
    private static C3dInstance Array20(string cell, C3dInstanceView view) => new()
    {
        Name = "U1", CellRef = "../../" + cell, View = view,
        Array = new C3dArray { Counts = [20, 20, 1], Pitch = new C3dPoint3(400 * Um, 400 * Um, 0) },
    };

    private static int CountChild(C3dEditorViewModel vm, string cellDir)
    {
        var probe = new C3dDocument { Instances = [new C3dInstance { Name = "X", CellRef = "../../" + Path.GetFileName(cellDir), View = C3dInstanceView.Layout }] };
        var e = new C3dElaborator().Elaborate(probe, vm.FilePath, null);
        return e.Solids.Count + e.Sheets.Count;
    }

    /// <summary>The problem's solids up to names and order: the material's VALUES (a layout's stackup entry and the
    /// technology material it became are one material under two names), the role, and the geometry to a picometre.</summary>
    /// <summary>Every drawn solid's and sheet's colour, as a sorted list: what a flatten must not change.</summary>
    private static List<uint> Colours(C3dEditorViewModel vm)
        => [.. vm.Viewer.Scene.Objects.Where(o => o.Material is not null).Select(o => o.Rgba).Order()];

    private static List<string> Solids(C3dElaboration e)
    {
        string V(string name) => e.Materials.Single(m => m.Name == name) is var m
            ? string.Create(CultureInfo.InvariantCulture, $"[εr {m.Epsr:R} tanδ {m.TanD:R} μr {m.Mur:R} σ {m.SigmaSm:R}]") : name;
        return [.. e.Solids.Select(s => $"{V(s.Material)} {s.Role} {Geometry(s.Primitive)}")
                   .Concat(e.Sheets.Select(s => $"{V(s.Material)} sheet {R(s.Z)} [{string.Join(" ", s.Outline.Select(q => $"{R(q.X)},{R(q.Y)}"))}]"))
                   .Order(StringComparer.Ordinal)];
    }

    private static string Geometry(Em3dPrimitive p) => p switch
    {
        Em3dBox b => $"box {P(b.Min)} {P(b.Max)}",
        Em3dExtrudedPolygon x => $"extrude {R(x.ZBottom)} {R(x.ZTop)} [{string.Join(" ", x.Outline.Select(q => $"{R(q.X)},{R(q.Y)}"))}]",
        Em3dCylinder c => $"cylinder {P(c.AxisStart)} {P(c.AxisEnd)} {R(c.Radius)}",
        _ => p.GetType().Name,
    };

    private static string R(double v) => Math.Round(v * 1e12).ToString("R", CultureInfo.InvariantCulture);
    private static string P(Point3 q) => $"({R(q.X)},{R(q.Y)},{R(q.Z)})";

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Copper", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    /// <summary>A workspace whose default technology is a shipped two-layer board.</summary>
    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), CircuitRF.Design.Layout.ShippedTechnologies.Load(Tech));
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    /// <summary>A cell with a small layout: two copper rectangles on the top layer.</summary>
    private static void LayoutCell(string ws, string cell)
    {
        string dir = Directory.Exists(Path.Combine(ws, cell)) ? Path.Combine(ws, cell) : CellFolder.CreateCellFolder(ws, cell);
        var view = LayoutPersistence.Deserialize("""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 0, "X2": 200000, "Y2": 50000 },
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": 0, "Y1": 100000, "X2": 60000, "Y2": 160000 }
              ],
              "Instances": []
            }
            """);
        CellCreate.WriteLayoutView(dir, cell, view);
    }

    /// <summary>A cell whose layout is one plated through-hole (the shipped technology's Drill layer) under a top pad.</summary>
    private static void ViaCell(string ws, string cell)
    {
        string dir = CellFolder.CreateCellFolder(ws, cell);
        var view = LayoutPersistence.Deserialize("""
            {
              "FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 1000,
              "Shapes": [
                { "$type": "Rect", "Layer": { "Layer": 1, "Datatype": 0 }, "X1": -400000, "Y1": -400000, "X2": 400000, "Y2": 400000 },
                { "$type": "Via", "X": 0, "Y": 0, "PadSize": 609600, "DrillSize": 304800, "Layer": { "Layer": 7, "Datatype": 0 } }
              ],
              "Instances": []
            }
            """);
        CellCreate.WriteLayoutView(dir, cell, view);
    }

    private static string C3dCell(string ws, string cell, C3dDocument doc)
    {
        string dir = Directory.Exists(Path.Combine(ws, cell)) ? Path.Combine(ws, cell) : CellFolder.CreateCellFolder(ws, cell);
        doc.SnapDbu = Um;
        return CellCreate.WriteThreeDView(dir, cell, doc);
    }

    private C3dEditorViewModel Open(string c3d, PatchRecordingBackend? backend = null, bool settleOnObjects = true)
    {
        string ws = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = backend ?? new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), _posted.Enqueue);
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        Settle(vm);
        if (settleOnObjects) Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            while (_posted.TryDequeue(out var a)) a();
            return vm.AdoptedGeneration == vm.Viewer.Source.Requested;
        }, TimeSpan.FromSeconds(60)), "the scene never settled");

    private static void Frame(Viewer3DViewModel v, Scene3DFramePlan plan)
    {
        plan.Plan(v.Scene, v.View, (int)W, (int)H, false, false, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
        v.Session.Frame(0, plan, 1, v.Scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);
    }

    /// <summary>The shipped 3D EM example, copied under the test's root.</summary>
    private string CopyExample()
    {
        string src = Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "3D EM");
        string dst = Path.Combine(_root, "3D EM");
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dst;
    }
}
