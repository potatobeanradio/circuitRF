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
//   * the shared image's alpha stays 1: blend alpha ONE, ONE_MINUS_SRC_ALPHA;
//   * every native handle is checked before it is encoded with — a message to nil is a silent no-op,
//     and a compositor waiting on a signal that was never encoded freezes the whole window. A present
//     step that cannot signal THROWS (Viewer3DPresentFault), and the pane shows the fault; it never
//     loops on a timeout.

using System.Numerics;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;

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

    /// <summary>Replaces one overlay slot's line buffer (<see cref="Scene3DBuffer.Overlay0"/>…2).</summary>
    public abstract void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines);

    /// <summary>brief-em3d-29 — replaces the field's vertex buffer (a triangle list, not indexed). Counts
    /// the bytes. Called when the field GEOMETRY changes — never for a phase step.</summary>
    public abstract void UploadField(FieldVertex[] vertices);

    // ── export (brief-em3d-29 R-em3d29-5) ─────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="plan"/> (planned at the export's size, no pick) into an offscreen image of
    /// its own and reads the pixels back: RGBA8, rows top to bottom. The live swapchain is not touched, so
    /// the file is the same picture the view shows, at the size asked for.
    /// </summary>
    public abstract byte[] RenderPixels(Scene3DFramePlan plan);

    // ── presentation ────────────────────────────────────────────────────────────────────────

    /// <summary>Null when this backend can present through <paramref name="interop"/>, else why not —
    /// naming what the compositor offered.</summary>
    public abstract string? CheckInterop(ICompositionGpuInterop interop);

    /// <summary>Creates <paramref name="count"/> shareable images of the size and imports each.</summary>
    public abstract void CreateImages(ICompositionGpuInterop interop, int width, int height, int count);

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

    // ── picking ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The object the last completed ID pass found under the cursor (0 for none) and the
    /// world point it hit, scene-local.</summary>
    public uint PickedId { get; protected set; }
    public Vector3 PickedPoint { get; protected set; }
    public bool PickedSomething { get; protected set; }

    /// <summary>Draw calls issued by the last frame (both passes) — the status line reports it.</summary>
    public int DrawCallsLastFrame { get; protected set; }

    public abstract void Dispose();
}
