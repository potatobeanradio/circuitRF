// ================================================================
//  C3dEditorGateTests.cs — the gate for brief-em3d-43: the 3D editor's modes, picking, B, undo, locality
//  and display unit, headless. Counters and view-model state only — no pixel is looked at (overview §1n);
//  the GPU is a recording fake. Gate 2 (object IDs unchanged) is brief 28's gate 9 and brief 29's picking
//  tests, run untouched, plus the ID-pass identity asserted in gate 1; gate 9 is in Firewall.Tests.
// ================================================================

using System.Numerics;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

/// <summary>A recording backend that also records partial uploads (brief 43's PatchScene).</summary>
internal sealed class PatchRecordingBackend : Viewer3DBackend
{
    public int SceneUploads, Patches, ElementTransforms;
    public long PatchBytes;
    public uint AnswerId;
    public uint AnswerFace = Scene3DVertex.NoFace;

    public override string Description => "recording fake";

    public override void UploadScene(Scene3DModel scene)
    {
        SceneUploads++;
        Counters.CountUpload(scene.VertexBytes + scene.IndexBytes + scene.LineBytes);
    }

    public override void PatchScene(Scene3DModel scene, Scene3DPatch patch)
    {
        Patches++;
        PatchBytes += patch.Bytes;
        ElementTransforms += patch.ElementTransforms;
        Counters.CountUpload(patch.Bytes);
    }

    /// <summary>brief-em3d-104 — the shade stream's whole uploads, patched bytes and releases.</summary>
    public int ShadeUploads, ShadeReleases;
    public long ShadePatchBytes;

    public override void UploadShade(Scene3DModel scene)
    {
        ShadeUploads++;
        Counters.CountUpload(scene.ShadeBytes);
    }

    public override void PatchShade(Scene3DModel scene, Scene3DPatch patch)
    {
        ShadePatchBytes += patch.ShadeBytes;
        Counters.CountUpload(patch.ShadeBytes);
    }

    public override void ReleaseShade() => ShadeReleases++;

    /// <summary>brief-em3d-106 — the realistic view's uploads, counted.</summary>
    public int EnvironmentUploads, AppearanceUploads, EnvironmentReleases;
    public override void UploadAppearances(float[] table) { AppearanceUploads++; Counters.CountUpload(table.Length * 4L); }
    public override void UploadEnvironment(CircuitRF.Render.Scene3D.Look.PrefilteredEnvironment environment)
    {
        EnvironmentUploads++;
        Counters.CountUpload(environment.Bytes);
    }
    public override void ReleaseEnvironment() => EnvironmentReleases++;

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines) => Counters.CountUpload((long)lines.Length * Scene3DVertex.Stride);
    public override void UploadField(CircuitRF.Render.Scene3D.Fields.FieldVertex[] vertices) { }
    /// <summary>brief-em3d-109 — the Lit field's normal stream uploads (an empty one is a release).</summary>
    public int FieldNormalUploads;
    public override void UploadFieldNormals(float[] normals) { if (normals.Length > 0) FieldNormalUploads++; }
    /// <summary>brief-em3d-107 — the size the last picture was drawn at (a supersampled one is drawn larger than it is saved).</summary>
    public (int W, int H) LastPixels;
    /// <summary>The last picture's occlusion cap scale (ao.w): its pixels per window pixel.</summary>
    public float LastOcclusionScale;
    public override byte[] RenderPixels(Scene3DFramePlan plan)
    {
        LastPixels = (plan.Width, plan.Height);
        LastOcclusionScale = plan.Uniforms[Scene3DFramePlan.LookAt + Scene3DFramePlan.LightingAt + 35];
        return new byte[plan.Width * plan.Height * 4];
    }
    public override string? CheckInterop(ICompositionGpuInterop interop) => null;
    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count) { }
    public override void ReleaseImages() { }
    public override bool WaitReusable(int image, int timeoutMs) => true;
    public override void Present(CompositionDrawingSurface surface, int image, ulong frame) { }

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        if (!plan.Pick) return;
        PickedId = AnswerId;
        PickedFace = AnswerFace;
        PickedSomething = AnswerId != 0;
    }

    public override void Dispose() { }
}

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class C3dEditorGateTests : IDisposable
{
    private const float W = 400, H = 300;
    private const long Um = 1000;                      // DBU per µm at the default 1000 DBU/µm

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-c3d43-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── 1. face pick = CPU face pick; 2. the object half of the pair is the ID it always was ─────

    [Fact]
    public void Gate1_TheIdPassesFacePair_IsWhatTheCpuRaysNearestFaceIs()
    {
        var vm = Open(Stack(extra: true));
        var scene = vm.Viewer.Scene;
        var view = vm.Viewer.View;
        var cam = Camera3D.Fit(scene.ContentMin, scene.ContentMax, W / H);
        cam.Yaw = -0.7f; cam.Pitch = 0.5f;
        const int w = 160, h = 100;
        int hits = 0, exact = 0;
        var faces = new HashSet<(uint, uint)>();
        for (int py = 1; py < h; py += 2)
            for (int px = 1; px < w; px += 2)
            {
                var (id, face) = Scene3DPicking.PairAtPixel(scene, cam, px, py, w, h, view.Visible);
                Assert.Equal(Scene3DPicking.IdAtPixel(scene, cam, px, py, w, h, view.Visible), id);
                var q = new Scene3DRayQuery(cam, px, py, w, h);
                var near = RayHits.Nearest(scene, q, Scene3DSelectMode.Face, view.Visible);
                Assert.Equal(Scene3DPicking.Pick(scene, cam, px, py, w, h, view.Visible).Id, near?.Item.Object ?? 0);
                if (id == 0) { Assert.Null(near); continue; }
                hits++;
                Assert.Equal(id, near!.Value.Item.Object);
                if ((int)face == near.Value.Item.Face) { exact++; faces.Add((id, face)); continue; }
                // A pixel on the edge two faces share: both are under it, at one depth, and either answer is right.
                var all = RayHits.Collect(scene, q, Scene3DSelectMode.Face, view.Visible);
                Assert.Contains(all, x => x.Item.Object == id && x.Item.Face == (int)face && MathF.Abs(x.Depth - near.Value.Depth) <= 1e-4f * near.Value.Depth);
            }
        Assert.True(hits > 200, $"only {hits} pixels hit anything");
        Assert.True(exact >= hits * 0.95, $"{exact} of {hits} pixels agreed exactly");
        Assert.True(faces.Count >= 6, $"only {faces.Count} distinct faces were sampled");
    }

    // ── 3. B through a stack; 4e. the context menu acts on the cycled item ─────────────────────

    [Fact]
    public void Gate3_BFromTheTopFace_VisitsEveryFaceBehindInDepthOrder_ThenWraps_AndShiftBReverses()
    {
        var vm = Open(Stack());
        var v = vm.Viewer;
        LookDownOnTheStack(v);
        v.SelectMode = Scene3DSelectMode.Face;
        ClickCentre(v);
        Assert.Equal("Face zmax · Box \"b1\"", v.Name(v.Selection.Single()));

        string[] expected =
        [
            "Face zmin · Box \"b1\"", "Face zmax · Box \"b2\"", "Face zmin · Box \"b2\"",
            "Face zmax · Box \"b3\"", "Face zmin · Box \"b3\"", "Face zmax · Box \"b1\"",
        ];
        foreach (string want in expected)
        {
            Assert.True(v.HandleKey(Key.B, KeyModifiers.None, gestureInProgress: false));
            Assert.Equal(want, v.Name(v.Selection.Single()));
        }
        Assert.EndsWith("1 of 6", v.CycleText);
        Assert.True(v.HandleKey(Key.B, KeyModifiers.Shift, gestureInProgress: false));
        Assert.Equal("Face zmin · Box \"b3\"", v.Name(v.Selection.Single()));
        Assert.Equal("Face zmin · Box \"b3\" · 6 of 6", v.CycleText);

        // R-em3d43-4e: the cursor is over b1's top face, but the menu acts on — and names — the cycled face.
        var menu = v.OpenContextMenu();
        Assert.Equal("Face zmin · Box \"b3\"", menu[0].Header);
        Assert.Equal("Face zmin · Box \"b3\"", v.Name(v.Selection.Single()));
    }

    /// <summary>A selected face is drawn over everything, so a right-click on its highlight may hover a face in front of it. It stays
    /// selected and the menu acts on it. A selected face off the cursor's ray gives way to what is hovered, as ever.</summary>
    [Fact]
    public void ARightClickOnASelectedFace_KeepsIt_ThoughAFaceStandsInFrontOfIt()
    {
        var vm = Open(Stack());
        var v = vm.Viewer;
        LookDownOnTheStack(v);
        v.SelectMode = Scene3DSelectMode.Face;
        Hover(v, W / 2, H / 2);
        var b3 = v.Scene.Objects.Single(o => o.Name == "b3");
        Scene3DItem Face(string name) => Scene3DItem.OfFace(b3.Id, Enumerable.Range(0, b3.FaceNames.Count).Single(i => b3.FaceNames[i] == name));

        v.SetSelection([Face("zmin")]);
        Assert.Equal("Face zmin · Box \"b3\"", v.OpenContextMenu()[0].Header);
        Assert.Equal(Face("zmin"), v.Selection.Single());

        v.SetSelection([Face("xmax")]);
        Assert.Equal("Face zmax · Box \"b1\"", v.OpenContextMenu()[0].Header);
    }

    /// <summary>The same for a solid, whose outline is drawn through what stands in front of it.</summary>
    [Fact]
    public void ARightClickOnASelectedSolid_KeepsIt_ThoughASolidStandsInFrontOfIt()
    {
        var vm = Open(Stack());
        var v = vm.Viewer;
        LookDownOnTheStack(v);
        v.SelectMode = Scene3DSelectMode.Object;
        Hover(v, W / 2, H / 2);
        var b3 = Scene3DItem.OfObject(v.Scene.Objects.Single(o => o.Name == "b3").Id);
        Assert.NotEqual(b3, v.HoveredItem);

        v.SetSelection([b3]);
        v.OpenContextMenu();
        Assert.Equal(b3, v.Selection.Single());
    }

    // ── 4. the list resets ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_TheListIsDiscarded_OnACursorMove_AModeChange_AndANewScene()
    {
        var vm = Open(Stack());
        var v = vm.Viewer;
        var cycle = v.HitCycle;
        LookDownOnTheStack(v);
        v.SelectMode = Scene3DSelectMode.Face;
        Hover(v, W / 2, H / 2);

        v.Cycle(+1); v.Cycle(+1);
        Assert.Equal(1, cycle.ListsBuilt);
        Assert.Equal(0, cycle.Resets);

        Hover(v, W / 2 + 2, H / 2);                            // within the threshold: kept
        Assert.Equal(0, cycle.Resets);
        Hover(v, W / 2 + 2 + Scene3DHitCycle.ResetPixels + 1, H / 2);
        Assert.Equal(1, cycle.Resets);

        v.Cycle(+1);
        Assert.Equal(2, cycle.ListsBuilt);
        v.SelectMode = Scene3DSelectMode.Object;               // a mode change
        Assert.Equal(2, cycle.Resets);

        v.Cycle(+1);
        Assert.Equal(3, cycle.ListsBuilt);
        vm.Rename(0, "top");                                   // an edit: a new scene generation
        Settle(vm);
        Assert.Equal(3, cycle.Resets);
    }

    // ── 5. hover does no geometry work ──────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AThousandHoverMoves_InEveryMode_ElaborateTessellateAndUploadNothing()
    {
        var fake = new PatchRecordingBackend();
        var vm = Open(Stack(extra: true), fake);
        var v = vm.Viewer;
        LookDownOnTheStack(v);
        var scene = v.Scene;
        var plan = new Scene3DFramePlan();
        v.Session.EnsureBackend();
        plan.Plan(scene, v.View, (int)W, (int)H, false, false, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
        v.Session.Frame(0, plan, 1, scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);

        long elaborated = vm.ObjectsElaborated, tessellated = vm.TessellationMisses, builds = v.Source.Builds;
        long uploaded = fake.Counters.UploadBytesTotal;
        var hovered = new HashSet<Scene3DSelectMode>();
        foreach (var mode in new[] { Scene3DSelectMode.Object, Scene3DSelectMode.Face, Scene3DSelectMode.Vertex })
        {
            v.SelectMode = mode;
            for (int i = 0; i < 1000; i++)
            {
                float x = 40 + i % 320, y = 30 + i * 7 % 240;
                v.Hover(x, y);
                var (id, face) = Scene3DPicking.PairAtPixel(scene, v.View.Camera, x, y, W, H, v.View.Visible);
                fake.AnswerId = id; fake.AnswerFace = face;
                plan.Plan(scene, v.View, (int)W, (int)H, false, pick: true, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
                v.Session.Frame(i % 3, plan, (ulong)i + 2, scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);
                v.OnPicked(fake.PickedId, fake.PickedFace, Vector3.Zero, fake.PickedSomething);
                if (v.HoveredItem is not null) hovered.Add(mode);
            }
        }
        Assert.Equal(elaborated, vm.ObjectsElaborated);
        Assert.Equal(tessellated, vm.TessellationMisses);
        Assert.Equal(builds, v.Source.Builds);
        Assert.Equal(uploaded, fake.Counters.UploadBytesTotal);
        Assert.Equal(0, v.HitCycle.ListsBuilt);                // B's ray is cast on B, never on a hover
        Assert.Equal(3, hovered.Count);                        // every mode found something to highlight
    }

    // ── 6. edit locality ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_EditingOneObjectOfAThousand_ElaboratesOne_TessellatesOne_AndUploadsOnlyItsBytes()
    {
        var objects = new List<C3dObject>();
        for (int k = 0; k < 1000; k++)
            objects.Add(new C3dBox { Name = $"b{k}", Material = "Gold", Min = new C3dPoint3(k % 40 * 30 * Um, k / 40 * 30 * Um, 0), Size = new C3dPoint3(20 * Um, 20 * Um, 5 * Um) });
        var fake = new PatchRecordingBackend();
        var vm = Open(Write(objects), fake);
        var v = vm.Viewer;
        v.Session.EnsureBackend();
        Frame(v);
        Assert.Equal(1, fake.SceneUploads);

        // A rename: one object re-elaborated and re-tessellated; its bytes are the same, so nothing uploads.
        long e0 = vm.ObjectsElaborated, t0 = vm.TessellationMisses;
        Assert.Null(vm.Rename(500, "renamed"));
        Settle(vm);
        Frame(v);
        Assert.Equal(1, vm.ObjectsElaborated - e0);
        Assert.InRange(vm.TessellationMisses - t0, 0, 1);      // an unchanged box may be a value-equal cache hit
        Assert.Equal(1, fake.SceneUploads);
        Assert.Equal(1, fake.Patches);
        var renamed = vm.SceneObject("renamed")!;
        long batchBytes = (long)renamed.VertexCount * Scene3DVertex.Stride + (long)Scene3DFaces.TrianglesOf(v.Scene, renamed.Id).Count * 4;
        Assert.InRange(fake.PatchBytes, 0, batchBytes);

        // A move: its vertices and its edges change, and exactly those bytes upload.
        e0 = vm.ObjectsElaborated; t0 = vm.TessellationMisses;
        long p0 = fake.PatchBytes;
        vm.ChangeObjects("Move", [500], o => o.Placement.Origin = new C3dPoint3(3 * Um, 0, 0));
        Settle(vm);
        Frame(v);
        Assert.Equal(1, vm.ObjectsElaborated - e0);
        Assert.Equal(1, vm.TessellationMisses - t0);
        Assert.Equal(1, fake.SceneUploads);
        var moved = vm.SceneObject("renamed")!;
        var edges = v.Scene.EdgeBatches.Single(b => b.ObjectId == moved.Id);
        Assert.Equal((long)(moved.VertexCount + edges.VertexCount) * Scene3DVertex.Stride, fake.PatchBytes - p0);
    }

    // ── 7. undo ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_EditsUndoAndRedo_ToByteIdenticalSavedDocuments_AndOneDragIsOneEntry()
    {
        var vm = Open(Stack());
        string path = vm.FilePath;
        var saved = new List<byte[]> { SaveAndRead(vm, path) };
        void Step(Action edit) { edit(); Settle(vm); saved.Add(SaveAndRead(vm, path)); }

        Step(() => Assert.Null(vm.Rename(0, "lid")));
        Step(() => vm.ChangeObjects("Material", [1], o => o.Material = "Copper"));
        Step(() => vm.ChangeObjects("Hide", [2], o => o.Hidden = true));
        Step(() => vm.ChangeObjects("Rotate", [1], o => o.Placement.Rotate = [new C3dRotation { Axis = C3dAxis.Z, Deg = 30 }]));
        Step(() =>
        {
            vm.Viewer.SetSelection([Scene3DItem.OfObject(vm.SceneObject("b2")!.Id)]);
            Assert.True(vm.DeleteSelection());
        });
        Step(() =>
        {
            // One drag: forty moves, one entry.
            int before = vm.UndoEntries;
            var drag = vm.BeginGesture("Drag lid", [0]);
            for (int i = 1; i <= 40; i++) drag.Update(o => o.Placement.Origin = new C3dPoint3(i * Um, 0, 0));
            drag.Commit();
            Assert.Equal(before + 1, vm.UndoEntries);
        });
        Assert.Equal(6, vm.UndoEntries);

        for (int k = saved.Count - 2; k >= 0; k--)
        {
            vm.UndoRedo.Undo();
            Settle(vm);
            Assert.Equal(saved[k], SaveAndRead(vm, path));
        }
        Assert.False(vm.UndoRedo.CanUndo);
        for (int k = 1; k < saved.Count; k++)
        {
            vm.UndoRedo.Redo();
            Settle(vm);
            Assert.Equal(saved[k], SaveAndRead(vm, path));
        }
    }

    // ── 8. keys, in both panes ──────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_InTheEditorAndTheReadOnlyViewer_FArmsFaceMode_HomeFits_AndPTogglesTheProjection()
    {
        var editor = Open(Stack()).Viewer;
        using var viewer = new Viewer3DViewModel(Path.Combine(_root, "setup.cem"),
            () => new Viewer3DInputs(new CircuitRF.Design.Layout.Em.EmSetup(), null, "no layout", ColorTheme.BuiltIn, ColorVariant.Light),
            () => new PatchRecordingBackend(), () => null, a => a());
        foreach (var pane in new[] { editor, viewer })
        {
            Assert.Equal(Scene3DSelectMode.Object, pane.SelectMode);
            Assert.True(pane.HandleKey(Key.F, KeyModifiers.None, gestureInProgress: false));
            Assert.Equal(Scene3DSelectMode.Face, pane.SelectMode);
            Assert.True(pane.IsFaceMode);
            Assert.Equal(0, pane.Fits);
            Assert.True(pane.HandleKey(Key.Home, KeyModifiers.None, gestureInProgress: false));
            Assert.Equal(1, pane.Fits);
            bool perspective = pane.IsPerspective;
            Assert.True(pane.HandleKey(Key.P, KeyModifiers.None, gestureInProgress: false));
            Assert.NotEqual(perspective, pane.IsPerspective);
            Assert.True(pane.HandleKey(Key.V, KeyModifiers.None, gestureInProgress: false));
            Assert.Equal(Scene3DSelectMode.Vertex, pane.SelectMode);
            // Mode keys wait for a gesture, and a modifier is someone else's shortcut (Cmd+V is paste).
            Assert.False(pane.HandleKey(Key.O, KeyModifiers.None, gestureInProgress: true));
            Assert.False(pane.HandleKey(Key.O, KeyModifiers.Meta, gestureInProgress: false));
            Assert.Equal(Scene3DSelectMode.Vertex, pane.SelectMode);
            Assert.True(pane.HandleKey(Key.O, KeyModifiers.None, gestureInProgress: false));
            Assert.Equal(Scene3DSelectMode.Object, pane.SelectMode);
        }
        Assert.False(viewer.HandleKey(Key.Delete, KeyModifiers.None, false));   // the viewer edits nothing
    }

    // ── 10. the display unit is free ────────────────────────────────────────────────────────

    [Fact]
    public void Gate10_ChangingTheUnit_DirtiesTheDocument_AddsNoEntry_ElaboratesNothing_AndChangesEveryLength()
    {
        var vm = Open(Stack());
        var v = vm.Viewer;
        LookDownOnTheStack(v);
        v.SelectMode = Scene3DSelectMode.Object;
        ClickCentre(v);
        v.OnPicked(v.Selection[0].Object, 5, new Vector3(1e-5f, 2e-5f, 3e-5f), hit: true);
        Assert.Equal(LayoutUnit.Um, vm.DisplayUnit);
        string cursor = v.CursorText, rows = PropertiesText(vm);
        Assert.Contains("µm", cursor);

        long elaborated = vm.ObjectsElaborated, builds = v.Source.Builds, tessellated = vm.TessellationMisses;
        int entries = vm.UndoEntries;
        Assert.False(vm.IsDirty);
        vm.DisplayUnit = LayoutUnit.Mil;

        Assert.True(vm.IsDirty);
        Assert.Equal(entries, vm.UndoEntries);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(elaborated, vm.ObjectsElaborated);
        Assert.Equal(builds, v.Source.Builds);
        Assert.Equal(tessellated, vm.TessellationMisses);
        Assert.NotEqual(cursor, v.CursorText);
        Assert.Contains("mil", v.CursorText);
        Assert.NotEqual(rows, PropertiesText(vm));
        Assert.Contains("mil", PropertiesText(vm));

        Assert.Null(vm.Save());
        Assert.False(vm.IsDirty);
        Assert.Equal(LayoutUnit.Mil, C3dPersistence.LoadFromFile(vm.FilePath).DisplayUnit);
    }

    /// <summary>Every length the Inspector shows: its read-only rows and its fields' lines, each with its unit.</summary>
    private static string PropertiesText(C3dEditorViewModel vm)
        => string.Join("|", vm.Properties.Rows) + "|" +
           string.Join("|", vm.Properties.FieldRows.Select(r => $"{r.Label} {string.Join(",", r.Fields.Select(f => f.Text))} {r.Unit}"));

    // ── the real Metal backend: the face half of the pair, the selection's passes, and a patch ──

    /// <summary>
    /// macOS only. Brief 28's Metal test proves the ID pass's OBJECT on the new 24-byte vertex and RG32Uint
    /// target; this proves its FACE reads back too, that a Face-mode selection's edge and on-top draws run on
    /// the device, and that an edit's partial upload (a blit on the queue) leaves the pick right.
    /// </summary>
    [Fact]
    public void Metal_TheIdPassReadsBackTheFace_TheSelectionDraws_AndAPatchedSceneStillPicks()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var vm = Open(Stack(extra: true));
        var v = vm.Viewer;
        var scene = v.Scene;
        const int w = 320, h = 200;
        var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        metal.CreateOffscreenImages(w, h, 1);
        using var session = new Viewer3DSession(() => metal);     // disposes the backend: not disposed twice
        session.EnsureBackend();
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Camera.Yaw = -0.7f; view.Camera.Pitch = 0.5f;
        view.Adopt(scene, null);
        view.Mode = Scene3DSelectMode.Face;

        // A pixel on a SIDE face of the top box — not face 0, so a face lost in the read-back shows.
        (int X, int Y)? at = null;
        uint b1 = vm.SceneObject("b1")!.Id;
        for (int y = 0; y < h && at is null; y += 2)
            for (int x = 0; x < w; x += 2)
                if (Scene3DPicking.PairAtPixel(scene, view.Camera, x, y, w, h, view.Visible) is { Id: var id, Face: var f } && id == b1 && f is >= 1 and < 4)
                { at = (x, y); break; }
        Assert.NotNull(at);
        view.CursorX = at.Value.X; view.CursorY = at.Value.Y;
        var want = Scene3DPicking.PairAtPixel(scene, view.Camera, at.Value.X, at.Value.Y, w, h, view.Visible);

        var plan = new Scene3DFramePlan();
        void Frames(Scene3DModel s)
        {
            for (ulong f = 1; f <= 3; f++)
            {
                plan.Plan(s, view, w, h, metal.FlipY, pick: true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
                session.Frame(0, plan, f, s, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
            }
        }
        Frames(scene);
        Assert.Equal(want.Id, metal.PickedId);
        Assert.Equal(want.Face, metal.PickedFace);
        int plain = metal.DrawCallsLastFrame;

        view.Selection = [Scene3DItem.OfFace(want.Id, (int)want.Face)];
        Frames(scene);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.Edges);
        Assert.Contains(plan.Draws.Take(plan.DrawCount), d => d.Pipeline == Scene3DPipeline.OnTop);
        // 3D editor bugs round 3 — the edges drawn EdgePasses times (a two-pixel outline), the face once on top.
        Assert.Equal(plain + Scene3DFramePlan.EdgePasses + 1, metal.DrawCallsLastFrame);

        // 3D editor bugs round 3 — Object mode: the selected object moves from the opaque draws to the translucent ones
        // (faded), its outline is four draws under offset slots, and the ID pass still finds it where it is faded.
        view.Mode = Scene3DSelectMode.Object;
        view.Selection = [Scene3DItem.OfObject(want.Id)];
        Frames(scene);
        var batch = scene.Batches.Single(b => b.ObjectId == want.Id);
        var drawn = plan.Draws.Take(plan.DrawCount).ToList();
        Assert.DoesNotContain(drawn, d => d.Pipeline == Scene3DPipeline.Opaque && d.First == batch.FirstIndex);
        Assert.Contains(drawn, d => d.Pipeline == Scene3DPipeline.Translucent && d.First == batch.FirstIndex);
        var edges = drawn.Where(d => d.Pipeline == Scene3DPipeline.Edges).ToList();
        Assert.Equal(Scene3DFramePlan.EdgePasses, edges.Count);
        Assert.Equal([0u, 1u, 2u, 3u], edges.Select(d => BitConverter.SingleToUInt32Bits(plan.Transforms[Scene3DFramePlan.TransformFloats * d.Transform + 17])));
        Assert.Equal(want.Id, metal.PickedId);
        view.Mode = Scene3DSelectMode.Face;

        // An edit that keeps the layout goes up as a patch, and the device draws and picks from it.
        vm.ChangeObjects("Move", [2], o => o.Placement.Origin = new C3dPoint3(0, 0, -2 * Um));
        Settle(vm);
        view.Adopt(v.Scene, [.. scene.Objects.Select(o => o.Name)]);
        Frames(v.Scene);
        Assert.Equal(1, session.PatchesApplied);
        Assert.Equal(want.Id, metal.PickedId);
        Assert.Equal(want.Face, metal.PickedFace);
    }

    // ── R-em3d43-3c: an instance's faces select; the menu offers nothing that edits the child ──

    [Fact]
    public void AnInstancesFace_SelectsAndReads_AndItsMenuOffersNoEditOfTheChild()
    {
        string ws = Workspace();
        WriteC3d(ws, "die", new C3dDocument { Objects = [Box("pad", 0, 0, 0, 50, 50, 5)] });
        var vm = Open(WriteC3d(ws, "pkg", new C3dDocument
        {
            Objects = [Box("base", -100, -100, -20, 300, 300, 10)],
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../die" }],
        }), ws: ws);
        var v = vm.Viewer;
        var pad = vm.SceneObject("U1/pad")!;
        Assert.Equal("U1", vm.InstanceOf(pad));
        v.SelectMode = Scene3DSelectMode.Face;
        v.SetSelection([Scene3DItem.OfFace(pad.Id, 5)]);
        var headers = v.ContextMenuItems().Select(i => i.Header).ToList();
        Assert.Contains("Select Owning Instance", headers);
        Assert.Contains(headers, h => h.StartsWith("Measure", StringComparison.Ordinal));   // live since brief 46
        Assert.DoesNotContain("Rename…", headers);
        Assert.DoesNotContain("Delete", headers);
        Assert.Contains("area", v.SelectionText);
        v.HandleKey(Key.Delete, KeyModifiers.None, false);
        Assert.Equal(0, vm.UndoEntries);
    }

    // ── R-em3d43-1d: the open document changing on disk ─────────────────────────────────────

    [Fact]
    public void TheFileChangingOnDisk_ReloadsAClean_AsksForADirty_AndIgnoresOurOwnSave()
    {
        var vm = Open(Stack());
        int asked = 0;
        vm.ExternalChangeWhileDirty += () => asked++;

        Assert.Null(vm.Save());                                // our own write: nothing happens
        vm.OnFileChangedOnDisk();
        Assert.Equal(0, asked);

        var other = C3dPersistence.LoadFromFile(vm.FilePath);  // someone else's write, while clean: reloaded
        other.Objects[0].Name = "theirs";
        File.SetLastWriteTimeUtc(vm.FilePath, DateTime.UtcNow.AddSeconds(-5));
        C3dPersistence.SaveToFile(vm.FilePath, other);
        vm.OnFileChangedOnDisk();
        Settle(vm);
        Assert.Equal("theirs", vm.Document.Objects[0].Name);
        Assert.NotNull(vm.SceneObject("theirs"));

        Assert.Null(vm.Rename(1, "mine"));                     // dirty now: the change is asked about, not applied
        other.Objects[0].Name = "theirs again";
        C3dPersistence.SaveToFile(vm.FilePath, other);
        vm.OnFileChangedOnDisk();
        Assert.Equal(1, asked);
        Assert.Equal("theirs", vm.Document.Objects[0].Name);
        Assert.Equal("mine", vm.Document.Objects[1].Name);
    }

    [Fact]
    public void OneWriteOnDisk_IsAskedAboutOnce_HoweverManyWatcherEventsReportIt()
    {
        var vm = Open(Stack());
        int asked = 0;
        vm.ExternalChangeWhileDirty += () => asked++;
        Assert.Null(vm.Rename(1, "mine"));

        var other = C3dPersistence.LoadFromFile(vm.FilePath);
        other.Objects[0].Name = "theirs";
        C3dPersistence.SaveToFile(vm.FilePath, other);
        vm.OnFileChangedOnDisk();
        vm.OnFileChangedOnDisk();
        vm.OnFileChangedOnDisk();
        Assert.Equal(1, asked);

        other.Objects[0].Name = "theirs again";                // a different write is a new question
        C3dPersistence.SaveToFile(vm.FilePath, other);
        vm.OnFileChangedOnDisk();
        Assert.Equal(2, asked);
    }

    [Fact]
    public void AnUnreadableFile_UnderACleanDocument_IsReportedOnce_AndTheDocumentIsKept()
    {
        var vm = Open(Stack());
        var posted = new List<string>();
        vm.PostMessage = (text, _) => posted.Add(text);

        File.WriteAllText(vm.FilePath, """{ "FormatVersion": 1 }""");
        vm.OnFileChangedOnDisk();
        vm.OnFileChangedOnDisk();

        Assert.Contains("Could not reload", Assert.Single(posted), StringComparison.Ordinal);
        Assert.Equal(3, vm.Document.Objects.Count);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    private C3dEditorViewModel Open(string c3d, PatchRecordingBackend? backend = null, string? ws = null)
    {
        ws ??= Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(c3d)))!;
        var fake = backend ?? new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(c3d, C3dPersistence.LoadFromFile(c3d), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Settle(vm);
        Assert.True(vm.Viewer.Scene.Objects.Length > 0, string.Join(" ", vm.Viewer.Scene.Notes));
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(30)),
                       "the scene never settled");

    private static void Frame(Viewer3DViewModel v)
    {
        var plan = new Scene3DFramePlan();
        plan.Plan(v.Scene, v.View, (int)W, (int)H, false, false, v.MeshOverlay, v.SectionOverlay, v.GridOverlay);
        v.Session.Frame(0, plan, 1, v.Scene, v.MeshOverlay, v.SectionOverlay, v.GridOverlay, false);
    }

    private static byte[] SaveAndRead(C3dEditorViewModel vm, string path)
    {
        Assert.Null(vm.Save());
        return File.ReadAllBytes(path);
    }

    /// <summary>Three 100 × 100 × 10 µm boxes stacked along z with 10 µm gaps — b1 on top — and, with
    /// <paramref name="extra"/>, a cylinder and a sheet beside them.</summary>
    private string Stack(bool extra = false)
    {
        var objects = new List<C3dObject> { Box("b1", 0, 0, 40, 100, 100, 10), Box("b2", 0, 0, 20, 100, 100, 10), Box("b3", 0, 0, 0, 100, 100, 10) };
        if (extra)
        {
            objects.Add(new C3dCylinder { Name = "via", Material = "Gold", Base = new C3dPoint3(160 * Um, 50 * Um, 0), Length = 50 * Um, Radius = 20 * Um });
            objects.Add(new C3dSheet
            {
                Name = "trace", Material = "Gold", Plane = C3dPlane.XY, Offset = 0,
                Rect = new C3dRect { Min = new C3dPoint2(-120 * Um, 0), Size = new C3dPoint2(80 * Um, 40 * Um) },
            });
        }
        return Write(objects);
    }

    private static C3dBox Box(string name, long x, long y, long z, long sx, long sy, long sz)
        => new() { Name = name, Material = "Gold", Min = new C3dPoint3(x * Um, y * Um, z * Um), Size = new C3dPoint3(sx * Um, sy * Um, sz * Um) };

    private string Write(List<C3dObject> objects) => WriteC3d(Workspace(), "cell", new C3dDocument { Objects = objects });

    private string Workspace()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }, new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        return ws;
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    /// <summary>Straight down on the stack, orthographic, so the ray through the centre crosses all six faces.</summary>
    private static void LookDownOnTheStack(Viewer3DViewModel v)
    {
        v.View.Camera = Camera3D.Fit(v.Scene.ContentMin, v.Scene.ContentMax, W / H, Projection3D.Orthographic);
        v.View.Camera.SetStandardView(StandardView3D.Top);
        v.View.Camera.Target = (v.Scene.ContentMin + v.Scene.ContentMax) * 0.5f;
    }

    /// <summary>A hover at (<paramref name="x"/>, <paramref name="y"/>) and the ID pass's answer for it.</summary>
    private static void Hover(Viewer3DViewModel v, float x, float y)
    {
        v.Hover(x, y);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
    }

    private static void ClickCentre(Viewer3DViewModel v)
    {
        Hover(v, W / 2, H / 2);
        v.Click(shift: false);
        Assert.Single(v.Selection);
    }
}
