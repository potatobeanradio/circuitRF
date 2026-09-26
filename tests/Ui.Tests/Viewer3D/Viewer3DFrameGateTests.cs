// brief-em3d-28 §6 — the frame-loop gates, with the GPU layer behind a recording fake (and, on macOS,
// the real Metal backend rendering offscreen). Counters, not timings.
//
//   1b every generated shader carries the hash of the WGSL it came from, and it matches
//   3  orbit uploads nothing: 100 camera changes upload 0 bytes
//   5  hover does no geometry work: 1,000 hover moves, 0 tessellations, 0 problem generations
// plus: the Metal backend draws the scene and its ID pass agrees with the CPU pick (macOS only).

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Rendering.Composition;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.Viewer3D;

/// <summary>Records what a backend is asked to do, and answers a pick with a fixed object.</summary>
internal sealed class RecordingBackend : Viewer3DBackend
{
    public int SceneUploads, OverlayUploads, Frames, DrawCalls;
    public uint Answer;

    public override string Description => "recording fake";

    public override void UploadScene(Scene3DModel scene)
    {
        SceneUploads++;
        Counters.CountUpload(scene.VertexBytes + scene.IndexBytes + scene.LineBytes);
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        OverlayUploads++;
        Counters.CountUpload((long)lines.Length * Scene3DVertex.Stride);
    }

    public override string? CheckInterop(ICompositionGpuInterop interop) => null;
    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count) { }
    public override void ReleaseImages() { }
    public override bool WaitReusable(int image, int timeoutMs) => true;
    public override void Present(CompositionDrawingSurface surface, int image, ulong frame) { }

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        Frames++;
        Counters.CountUniform(Scene3DFramePlan.UniformBytes * (plan.Pick ? 2 : 1));
        DrawCalls += plan.DrawCount + plan.PickDrawCount;
        if (plan.Pick) { PickedId = Answer; PickedSomething = Answer != 0; }
    }

    public override void Dispose() { }
}

[Collection(Viewer3DCollection.Name)]
public sealed class Viewer3DFrameGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-viewer3d-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ── 1b. shaders current ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1b_EveryGeneratedShader_CarriesTheHashOfTheWgslItWasGeneratedFrom()
    {
        string dir = Path.Combine(PalaceBackendTests.RepoRoot(), "src", "Ui", "Viewer3D", "Shaders");
        string want = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "scene.wgsl"))));

        foreach (string text in new[] { "scene.metal", "scene.hlsl" })
        {
            string first = File.ReadLines(Path.Combine(dir, text)).First();
            Assert.EndsWith("wgsl-sha256: " + want, first);
        }
        Assert.Equal(want, SpirvHash(File.ReadAllBytes(Path.Combine(dir, "scene.spv"))));

        // What ships is what is committed: the embedded copies are the files.
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "scene.metal")), Viewer3DShaders.Metal);
        Assert.Equal(File.ReadAllText(Path.Combine(dir, "scene.hlsl")), Viewer3DShaders.Hlsl);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "scene.spv")), Viewer3DShaders.Spirv);
    }

    /// <summary>The <c>wgsl-sha256:</c> OpSourceExtension (opcode 4) literal in a SPIR-V module.</summary>
    private static string? SpirvHash(byte[] spv)
    {
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spv);
        Assert.Equal(0x07230203u, words[0]);
        for (int i = 5; i < words.Length;)
        {
            int count = (int)(words[i] >> 16), op = (int)(words[i] & 0xFFFF);
            if (count == 0) break;
            if (op == 4)
            {
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.Slice(i + 1, count - 1)).ToArray();
                string lit = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                if (lit.StartsWith("wgsl-sha256:", StringComparison.Ordinal)) return lit["wgsl-sha256:".Length..];
            }
            i += count;
        }
        return null;
    }

    // ── 3. orbit uploads nothing ────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AHundredCameraChanges_UploadZeroBytes_AndAReHostUploadsNoGeometry()
    {
        var g = Scene3DGateTests.CaseA(_root);
        var scene = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        var fake = new RecordingBackend();
        using var session = new Viewer3DSession(() => fake);
        session.EnsureBackend();                        // the pane does this on attach
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, 1.6f) };
        view.Adopt(scene, null);
        var plan = new Scene3DFramePlan();

        plan.Plan(scene, view, 800, 500, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
        session.Frame(0, plan, 1, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        long first = fake.Counters.UploadBytesTotal;
        Assert.Equal(scene.VertexBytes + scene.IndexBytes + scene.LineBytes, first);

        for (int i = 0; i < 100; i++)
        {
            switch (i % 3)
            {
                case 0: view.Camera.Orbit(4, 1); break;
                case 1: view.Camera.Pan(3, -2, 500); break;
                default: view.Camera.ZoomAt(0.5f, 400, 250, 800, 500); break;
            }
            if (i == 50) view.Camera.Projection = Projection3D.Orthographic;
            plan.Plan(scene, view, 800, 500, false, false, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(i % 3, plan, (ulong)i + 2, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, orbiting: true);
        }
        // A Dock float and re-dock: the pane releases and re-imports its images; the session keeps
        // the backend, so the next frame uploads nothing either.
        fake.ReleaseImages();
        session.Frame(0, plan, 200, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, true);

        Assert.Equal(first, fake.Counters.UploadBytesTotal);
        Assert.Equal(1, fake.SceneUploads);
        Assert.Equal(1, session.BackendsCreated);
        var summary = fake.Counters.Summarize();
        Assert.Equal(101, summary.OrbitFrames);
        Assert.Equal(0, summary.OrbitUploadMax);
    }

    // ── 5. hover does no geometry work ──────────────────────────────────────────────────────

    [Fact]
    public void Gate5_AThousandHoverMoves_CauseNoTessellationAndNoProblemGeneration()
    {
        var (setup, source) = PalaceProgressTests.CaseA(_root);
        var fake = new RecordingBackend();
        using var ready = new ManualResetEventSlim(false);
        using var vm = new Viewer3DViewModel(Path.Combine(_root, "caseA.cem"),
            () => new Viewer3DInputs(setup.Clone(), source, null, ColorTheme.BuiltIn, ColorVariant.Light),
            () => fake, () => null, a => a());
        vm.Source.SceneReady += _ => ready.Set();
        vm.Regenerate();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        SpinWait.SpinUntil(() => vm.Scene.Generation == 1, TimeSpan.FromSeconds(5));
        var scene = vm.Scene;
        uint wire = scene.Objects.First(o => o.Kind == Scene3DKind.Wire).Id;
        fake.Answer = wire;
        vm.Session.EnsureBackend();

        long tessellations = Scene3DBuilder.Tessellations, builds = vm.Source.Builds;
        var plan = new Scene3DFramePlan();
        long uploadsAfterFirst = -1;
        for (int i = 0; i < 1000; i++)
        {
            vm.Hover(100 + i % 300, 80 + i % 200);
            plan.Plan(vm.Scene, vm.View, 800, 500, false, pick: true, vm.MeshOverlay, vm.SectionOverlay, vm.GridOverlay);
            vm.Session.Frame(i % 3, plan, (ulong)i + 1, vm.Scene, vm.MeshOverlay, vm.SectionOverlay, vm.GridOverlay, false);
            vm.OnPicked(fake.PickedId, Vector3.Zero, fake.PickedSomething);
            if (i == 0) uploadsAfterFirst = fake.Counters.UploadBytesTotal;
        }

        Assert.Equal(tessellations, Scene3DBuilder.Tessellations);
        Assert.Equal(builds, vm.Source.Builds);
        Assert.Equal(uploadsAfterFirst, fake.Counters.UploadBytesTotal);
        Assert.Equal(wire, vm.View.Hovered);
        Assert.StartsWith(scene.Object(wire)!.Name, vm.HoverText);
        Assert.Contains("σ", vm.HoverText);
    }

    // ── a stored camera from a view that never had a scene ──────────────────────────────────

    /// <summary>Owner report, 2026-09-25: a view closed before it ever framed a scene stored a
    /// zero-distance camera; reopened, the wheel could not zoom (zoom multiplies the distance).</summary>
    [Fact]
    public void AZeroDistanceStoredCamera_IsIgnored_AndTheWheelZoomsOut()
    {
        var (setup, source) = PalaceProgressTests.CaseA(_root);
        using var ready = new ManualResetEventSlim(false);
        using var vm = new Viewer3DViewModel(Path.Combine(_root, "caseA.cem"),
            () => new Viewer3DInputs(setup.Clone(), source, null, ColorTheme.BuiltIn, ColorVariant.Light),
            () => new RecordingBackend(), () => null, a => a());
        Assert.Null(vm.CameraToPersist());                     // nothing framed yet: nothing to keep
        vm.RestoreCamera(new Design.Workspace.CwsCamera3D());  // what the broken session wrote
        vm.Source.SceneReady += _ => ready.Set();
        vm.Regenerate();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        SpinWait.SpinUntil(() => vm.Scene.Generation == 1, TimeSpan.FromSeconds(5));

        float before = vm.View.Camera.Distance;
        Assert.True(before > 0);
        vm.Zoom(-3, 400, 250, 800, 500);
        Assert.True(vm.View.Camera.Distance > before * 1.3f);
    }

    // ── a planar setup, shown in 3D ─────────────────────────────────────────────────────────

    /// <summary>Show 3D is offered for a planar setup too: its layout through the stackup, with one
    /// note saying what it is — and no mesh or grid, which only a 3D solver makes.</summary>
    [Fact]
    public void APlanarSetup_IsShownInThreeD_WithOneNote()
    {
        var (setup, source) = Em3dGeneratorTests.CaseB(plated: true);
        setup.Solver3D = Em3dSolver.None;
        using var ready = new ManualResetEventSlim(false);
        using var vm = new Viewer3DViewModel(Path.Combine(_root, "via.cem"),
            () => new Viewer3DInputs(setup.Clone(), source, null, ColorTheme.BuiltIn, ColorVariant.Light),
            () => new RecordingBackend(), () => _root, a => a());
        vm.Source.SceneReady += _ => ready.Set();
        vm.Regenerate();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        SpinWait.SpinUntil(() => vm.Scene.Generation == 1, TimeSpan.FromSeconds(5));

        Assert.Contains(vm.Scene.Objects, o => o.Kind == Scene3DKind.Conductor);
        Assert.Contains(vm.Scene.Objects, o => o.Kind == Scene3DKind.Port);
        Assert.Equal(["Planar setup, shown in 3D."], vm.Scene.Notes);
        Assert.False(vm.MeshAvailable);
        Assert.False(vm.GridAvailable);
    }

    // ── the real Metal backend, offscreen ───────────────────────────────────────────────────

    [Fact]
    public void Metal_DrawsTheScene_AndItsIdPassNamesWhatTheCpuPickNames()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var g = Scene3DGateTests.CaseA(_root);
        var scene = Scene3DBuilder.Build(g.Problem!, 1, g.Origins);
        using var metal = new CircuitRF.Ui.Viewer3D.Metal.MetalViewer3DBackend();
        const int w = 320, h = 200;
        metal.CreateOffscreenImages(w, h, 1);
        var view = new Viewer3DViewState { Camera = Camera3D.Fit(scene.ContentMin, scene.ContentMax, w / (float)h) };
        view.Adopt(scene, null);
        var session = new Viewer3DSession(() => metal);
        session.EnsureBackend();

        // A pixel on the wire, found by the CPU.
        (int X, int Y)? onWire = null;
        for (int y = 0; y < h && onWire is null; y += 2)
            for (int x = 0; x < w; x += 2)
                if (scene.Object(Scene3DPicking.IdAtPixel(scene, view.Camera, x, y, w, h, view.Visible))?.Kind == Scene3DKind.Wire)
                { onWire = (x, y); break; }
        Assert.NotNull(onWire);
        view.CursorX = onWire.Value.X; view.CursorY = onWire.Value.Y;

        var plan = new Scene3DFramePlan();
        for (ulong f = 1; f <= 3; f++)      // the pick is read back on a later frame, as in the pane
        {
            plan.Plan(scene, view, w, h, metal.FlipY, pick: true, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None);
            session.Frame(0, plan, f, scene, Scene3DOverlay.None, Scene3DOverlay.None, Scene3DOverlay.None, false);
        }
        uint cpu = Scene3DPicking.IdAtPixel(scene, view.Camera, onWire.Value.X, onWire.Value.Y, w, h, view.Visible);
        Assert.Equal(cpu, metal.PickedId);
        Assert.True(metal.PickedSomething);

        var px = metal.ReadImage(0);
        var (br, bg, bb) = plan.Clear;
        int drawn = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            Assert.Equal(255, px[i + 3]);    // the shared image's alpha stays 1 (R-em3d28-1d)
            if (Math.Abs(px[i] - br * 255) > 3 || Math.Abs(px[i + 1] - bg * 255) > 3 || Math.Abs(px[i + 2] - bb * 255) > 3) drawn++;
        }
        if (Environment.GetEnvironmentVariable("CRF_VIEWER3D_PNG") is { Length: > 0 } png)
        {
            using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            using var f = File.Create(png);
            bmp.Encode(f, SkiaSharp.SKEncodedImageFormat.Png, 100);
        }
        Assert.True(drawn > w * h / 200, $"only {drawn} pixels differ from the background");
    }
}

/// <summary>The Viewer3D tests read process-wide counters (Scene3DBuilder.Tessellations), so they run
/// one class at a time.</summary>
[CollectionDefinition(Name)]
public sealed class Viewer3DCollection { public const string Name = "Viewer3D"; }
