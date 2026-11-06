// brief-em3d-28 R-em3d28-1b — ONE backend contract, three backends: Metal (macOS), D3D11 (Windows) and
// Vulkan (Linux), chosen at run time by platform (Viewer3DBackends.Create). A route-B (WebGPU) backend
// would slot in beside them without touching the scene model: everything a backend is told comes from
// Scene3DFramePlan, which lives below the firewall.
//
// A backend owns: the device, the uploaded buffers, the pipelines compiled from the generated shaders,
// the three shareable images the compositor imports, and the synchronisation that goes with them. It
// is OWNED BY A Viewer3DSession, which outlives the view — so a Dock float or re-dock detaches the
// pane, re-imports three images, and uploads no geometry (findings §5.7).
//
// RULES FROM BRIEF 27 (R-em3d28-1d), which every implementation keeps:
//   * the shared image's alpha stays 1: blend alpha ONE, ONE_MINUS_SRC_ALPHA (brief-em3d-107: only a transparent PICTURE, drawn
//     offscreen by RenderPixels, is cleared to alpha 0 — never the live image);
//   * every native handle is checked before it is encoded with — a message to nil is a silent no-op,
//     and a compositor waiting on a signal that was never encoded freezes the whole window. A present
//     step that cannot signal THROWS (Viewer3DPresentFault), and the pane shows the fault; it never
//     loops on a timeout.

using System.Numerics;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using Look = CircuitRF.Render.Scene3D.Look;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>A present step that could not signal the compositor, or a native call that returned no
/// object. Reported in the pane; never retried in a loop.</summary>
public sealed class Viewer3DPresentFault(string message) : Exception(message);

public abstract class Viewer3DBackend : IDisposable
{
    /// <summary>The device and API, for the status line.</summary>
    public abstract string Description { get; }

    /// <summary>The API's framebuffer y runs down (Vulkan): the plan flips clip y.</summary>
    public virtual bool FlipY => false;

    /// <summary>Counters on the render thread's lane: every buffer upload is counted here.</summary>
    public FrameCounters Counters { get; } = new("render");

    // ── geometry: once per generation, never per frame ──────────────────────────────────────

    /// <summary>Replaces the scene's vertex, index and line buffers. Counts the bytes.</summary>
    public abstract void UploadScene(Scene3DModel scene);

    /// <summary>brief-em3d-43 R-em3d43-1b — rewrites only <paramref name="patch"/>'s ranges of the buffers the
    /// last <see cref="UploadScene"/> (or patch) left, to make them <paramref name="scene"/>'s. Counts the
    /// bytes. A backend that cannot patch uploads the whole scene instead, which is always correct.</summary>
    public virtual void PatchScene(Scene3DModel scene, Scene3DPatch patch) => UploadScene(scene);

    // ── the shade stream (brief-em3d-104 R-em3d104-3): held only while the realistic view is on ────────────────────────

    /// <summary>Replaces the shade stream's buffer with <paramref name="scene"/>'s <see cref="Scene3DModel.ShadeVertices"/> (parallel
    /// to its vertices). Counts the bytes. Called only while the realistic view is on (<see cref="Viewer3DSession.ShadeStream"/>);
    /// with it off this backend holds no shade buffer at all.</summary>
    public abstract void UploadShade(Scene3DModel scene);

    /// <summary>Rewrites only <paramref name="patch"/>'s <see cref="Scene3DPatch.ShadeRanges"/> of the shade buffer the last
    /// <see cref="UploadShade"/> (or patch) left. Counts the bytes. A backend that cannot patch uploads the whole stream, which is
    /// always correct.</summary>
    public virtual void PatchShade(Scene3DModel scene, Scene3DPatch patch) => UploadShade(scene);

    /// <summary>Releases the shade buffer: the realistic view was turned off.</summary>
    public abstract void ReleaseShade();

    // ── the realistic view's lighting (brief-em3d-106 R-em3d106-1e): held only while it is on ──────────────────────────────

    /// <summary>Replaces the appearance table (binding 2: <see cref="Look.Pbr.TableBytes"/>, 256 rows of three vec4f). Counts the
    /// bytes. Called when the realistic view turns on and when a scene's appearances change, never per frame.</summary>
    public abstract void UploadAppearances(float[] table);

    /// <summary>Replaces the environment's two textures (group 2: the prefiltered octahedral map with its five levels as mips, and
    /// the split-sum table), both RGBA16F. Counts the bytes. Once per turn-on and per environment change — a rotation is a uniform.</summary>
    public abstract void UploadEnvironment(Look.PrefilteredEnvironment environment);

    /// <summary>Releases the environment's textures and the appearance table — and brief 107's shadow map and occlusion targets: the
    /// realistic view was turned off.</summary>
    public abstract void ReleaseEnvironment();

    /// <summary>Replaces one overlay slot's line buffer (<see cref="Scene3DBuffer.Overlay0"/>…2).</summary>
    public abstract void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines);

    /// <summary>brief-em3d-29 — replaces the field's vertex buffer (a triangle list, not indexed). Counts
    /// the bytes. Called when the field GEOMETRY changes — never for a phase step.</summary>
    public abstract void UploadField(FieldVertex[] vertices);

    /// <summary>brief-em3d-109 R-em3d109-2b — replaces the Lit field's normal stream (<see cref="FieldShading"/>: three floats a field
    /// vertex, parallel to the field buffer) — bound beside it as the shade stream is beside the scene's: Metal vertex buffer 3, D3D11
    /// input slot 1, Vulkan vertex binding 1. Empty releases it. Counts the bytes. Only a frame with a Lit field asks for it.</summary>
    public abstract void UploadFieldNormals(float[] normals);

    // ── export (brief-em3d-29 R-em3d29-5) ─────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="plan"/> (planned at the export's size, no pick) into an offscreen image of
    /// its own and reads the pixels back: RGBA8, rows top to bottom. The live swapchain is not touched, so
    /// the file is the same picture the view shows, at the size asked for. brief-em3d-107 — a <see cref="Scene3DFramePlan.Transparent"/>
    /// plan clears the image to (0, 0, 0, 0), so the read-back is PREMULTIPLIED (the blend makes it so); the caller straightens it.
    /// </summary>
    public abstract byte[] RenderPixels(Scene3DFramePlan plan);

    // ── presentation ────────────────────────────────────────────────────────────────────────

    /// <summary>Null when this backend can present through <paramref name="interop"/>, else why not —
    /// naming what the compositor offered.</summary>
    public abstract string? CheckInterop(ICompositionGpuInterop interop);

    /// <summary>True when the compositor is on another GPU than this backend's device, so a new device would present
    /// through it. Read only after <see cref="CheckInterop"/> has refused.</summary>
    public virtual bool CompositorMovedGpu(ICompositionGpuInterop interop) => false;

    /// <summary>Creates <paramref name="count"/> shareable images of the size and imports each.</summary>
    public abstract void CreateImages(ICompositionGpuInterop interop, int width, int height, int count);

    /// <summary>True when the backend can present without GPU interop: <see cref="CreateImages"/> then takes a null
    /// interop and makes images no compositor imports, and <see cref="ReadFramePixels"/> reads each frame back.</summary>
    public virtual bool CanPresentInSoftware => false;

    /// <summary>Software present (no GPU interop): the pixels of image <paramref name="image"/> after its
    /// frame, RGBA8 premultiplied, rows top to bottom; null where the backend has no such path.</summary>
    public virtual byte[]? ReadFramePixels(int image) => null;

    /// <summary>Releases the imported images and their semaphores; keeps the device and buffers.</summary>
    public abstract void ReleaseImages();

    /// <summary>Render thread: waits until the compositor has finished reading image
    /// <paramref name="image"/>. False after <paramref name="timeoutMs"/> (counted, not retried).</summary>
    public abstract bool WaitReusable(int image, int timeoutMs);

    /// <summary>Render thread: draws <paramref name="plan"/> into image <paramref name="image"/> and
    /// arranges for the compositor to know when it is done (<paramref name="frame"/> numbers it).</summary>
    public abstract void Render(int image, Scene3DFramePlan plan, ulong frame);

    /// <summary>UI thread: hands the finished image to the compositor.</summary>
    public abstract void Present(CompositionDrawingSurface surface, int image, ulong frame);

    private Exception? _presentFault;

    /// <summary>
    /// brief-dfr31-5 R-dfr31-5-3 — the compositor failed to take an image (a lost GPU context: "OpenSharedResource failed",
    /// <c>PlatformGraphicsContextLostException</c>). Raised once, on the thread the compositor completes on, until
    /// <see cref="ClearPresentFault"/>; every later failure of the same burst is observed and dropped, so a crash trail holds
    /// one line where it held forty unobserved task exceptions.
    /// </summary>
    public event Action<Exception>? PresentFaulted;

    /// <summary>Observes a compositor update's task — a fault reaches <see cref="PresentFaulted"/> — and returns it.</summary>
    protected Task ObservePresent(Task task)
    {
        task.ContinueWith(static (t, o) => ((Viewer3DBackend)o!).OnPresentFaulted(t.Exception!.GetBaseException()), this,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    /// <summary>Designer feedback round 34 — releasing an imported object on a lost context faults too; that fault is
    /// observed and dropped (the object is gone either way) rather than left to the crash trail as unobserved.</summary>
    internal static void ObserveDisposal(ValueTask disposal)
    {
        if (disposal.IsCompletedSuccessfully) return;
        disposal.AsTask().ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnPresentFaulted(Exception e)
    {
        if (Interlocked.CompareExchange(ref _presentFault, e, null) is null) PresentFaulted?.Invoke(e);
    }

    /// <summary>The surface was rebuilt: the next failure is a new event.</summary>
    public void ClearPresentFault() => Interlocked.Exchange(ref _presentFault, null);

    // ── picking ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The object the last completed ID pass found under the cursor (0 for none) and the
    /// world point it hit, scene-local.</summary>
    public uint PickedId { get; protected set; }
    /// <summary>brief-em3d-43 R-em3d43-3a — the face of <see cref="PickedId"/> the ID pass found
    /// (<see cref="Scene3DVertex.NoFace"/> for none): the second half of the RG32Uint target.</summary>
    public uint PickedFace { get; protected set; } = Scene3DVertex.NoFace;
    public Vector3 PickedPoint { get; protected set; }
    public bool PickedSomething { get; protected set; }

    /// <summary>brief-em3d-44 R-em3d44-2a — the largest pick patch this backend reads back (odd); 1 reads the
    /// cursor's texel only, and the snap then renders its patch on the CPU (R-em3d44-2e).</summary>
    public virtual int MaxPickSize => 1;

    /// <summary>The last completed ID pass's patch, or null for a backend that reads one texel. Written on the
    /// render thread and read on the UI thread only between frames, as <see cref="PickedId"/> is.</summary>
    public CircuitRF.Render.Scene3D.Edit.Scene3DIdPatch? PickPatch { get; protected set; }

    /// <summary>Draw calls issued by the last frame (both passes) — the status line reports it.</summary>
    public int DrawCallsLastFrame { get; protected set; }

    public abstract void Dispose();
}
