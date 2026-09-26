// brief-em3d-28 R-em3d28-1b / R-em3d28-1d — what OUTLIVES the pane: the backend (device, pipelines,
// uploaded buffers) and which scene generation and overlay versions those buffers hold.
//
// A Dock float or re-dock detaches the pane from the visual tree and attaches a new one; the session
// stays with the document, so the new pane re-imports three images and uploads NO geometry. The
// frame's upload step compares numbers — generation, overlay versions — and uploads only what moved,
// which is what makes gate 3 ("100 camera changes upload 0 bytes") a property of the design rather
// than of care in the caller.

using CircuitRF.Render.Scene3D;

namespace CircuitRF.Ui.Viewer3D;

public sealed class Viewer3DSession(Func<Viewer3DBackend> create) : IDisposable
{
    private long _uploadedGeneration = -1;
    private readonly long[] _overlayVersions = [-1, -1, -1];
    private Scene3DModel? _uploadedScene;
    private bool _disposed;

    /// <summary>Serialises the render thread against a backend teardown.</summary>
    public object RenderLock { get; } = new();

    /// <summary>The backend, created on first use and kept until the document closes.</summary>
    public Viewer3DBackend? Backend { get; private set; }

    /// <summary>How many times a backend has been created — a re-dock must not add one.</summary>
    public int BackendsCreated { get; private set; }

    /// <summary>Counters on the UI lane: the pane's per-frame share on the UI thread.</summary>
    public FrameCounters Ui { get; } = new("ui");

    public Viewer3DBackend EnsureBackend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Backend is null)
        {
            Backend = create();
            BackendsCreated++;
        }
        return Backend;
    }

    /// <summary>
    /// Render thread: uploads whatever the frame needs that the backend does not already hold, then
    /// draws <paramref name="plan"/> into <paramref name="image"/>. Counted on the backend's lane —
    /// a frame whose camera alone changed adds 0 upload bytes. A session disposed under the render
    /// thread (the tab closed mid-frame) draws nothing and creates nothing: false.
    /// </summary>
    public bool Frame(int image, Scene3DFramePlan plan, ulong frame, Scene3DModel scene,
                      Scene3DOverlay mesh, Scene3DOverlay section, Scene3DOverlay grid, bool orbiting)
    {
        lock (RenderLock)
        {
            if (Backend is not { } b) return false;
            b.Counters.BeginFrame(orbiting);
            if (!ReferenceEquals(scene, _uploadedScene) || scene.Generation != _uploadedGeneration)
            {
                b.UploadScene(scene);
                _uploadedScene = scene;
                _uploadedGeneration = scene.Generation;
            }
            Sync(b, Scene3DBuffer.Overlay0, 0, mesh);
            Sync(b, Scene3DBuffer.Overlay1, 1, section);
            Sync(b, Scene3DBuffer.Overlay2, 2, grid);
            b.Render(image, plan, frame);
            b.Counters.EndFrame();
            return true;
        }
    }

    private void Sync(Viewer3DBackend b, Scene3DBuffer slot, int i, Scene3DOverlay o)
    {
        if (_overlayVersions[i] == o.Version) return;
        b.UploadOverlay(slot, o.Lines);
        _overlayVersions[i] = o.Version;
    }

    public void Dispose()
    {
        lock (RenderLock)
        {
            _disposed = true;
            Backend?.Dispose();
            Backend = null;
        }
    }
}
