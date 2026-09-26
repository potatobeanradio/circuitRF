using System.Numerics;
using Viewer3dSpike.Render.Direct3D;
using Viewer3dSpike.Render.Metal;
using Viewer3dSpike.Render.Vk;
using Viewer3dSpike.Render.Wgpu;
using Viewer3dSpike.Scene;

namespace Viewer3dSpike.App;

/// <summary>
/// Everything the pane needs that must SURVIVE the pane being re-hosted (a Dock float or re-dock
/// re-templates the view): the scene, the input state, the counters and — for routes A and B, which
/// own their GPU device — the renderer and its uploaded buffers. Route C cannot keep its buffers:
/// Avalonia gives each OpenGlControlBase a fresh context, so it re-uploads on every re-host, and the
/// status line counts it.
/// </summary>
public sealed class Session
{
    public readonly string Route;
    public readonly SceneModel Scene;
    public readonly string SceneSource;
    public readonly ViewportInput Input;
    public readonly FrameCounters Ui = new("ui-thread");
    public readonly FrameCounters Render = new("render-thread");
    public MetalRenderer? Metal;
    public WgpuRenderer? Wgpu;
    public D3D11Renderer? D3D11;          // route A, Windows (brief em3d-28 step 0)
    public VulkanRenderer? Vulkan;        // route A, Linux
    public string DeviceInfo = "(not initialised)";
    public string HostInfo = "";
    public int Inits, GlRenderCalls;
    public int ReleaseTimeouts;
    public volatile uint Hovered;
    public int RenderThreadId;
    public readonly int UiThreadId = Environment.CurrentManagedThreadId;
    public bool Continuous = true;
    /// <summary>A Dock re-host can attach the new pane before the old one detaches; both would
    /// drive the one renderer from two render threads. Uncontended, this costs no allocation.</summary>
    public readonly Lock RenderLock = new();
    int _printed;
    public bool FirstPrint() => Interlocked.Exchange(ref _printed, 1) == 0;

    public Session(string route, string? msh, int tris)
    {
        Route = route;
        string? path = msh ?? FindDefaultMsh();
        if (path != null && File.Exists(path))
        {
            var mesh = MshReader.Read(path);
            SceneSource = $"{Path.GetFileName(path)} ({mesh.Triangles.Count:N0} boundary triangles)";
            Scene = SceneModel.FromMsh(mesh, tris);
        }
        else
        {
            SceneSource = "SYNTHETIC stand-in — no .msh found (see README: generate data/case.msh with Gmsh)";
            Scene = SceneModel.Synthetic(tris);
        }
        Input = new ViewportInput(Camera.Frame(Scene.BoundsMin, Scene.BoundsMax));
    }

    static string? FindDefaultMsh()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "data", "case.msh");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public string ObjectName(uint id) =>
        id == 0 ? "—" : $"{Scene.Objects[(id - 1) % Scene.ObjectsPerReplica].Name} (replica {(id - 1) / Scene.ObjectsPerReplica}, id {id})";
}

/// <summary>
/// The input loop's side of the pane (em-3d.md §8.4): pointer events become small edits to a
/// camera value; the frame loop takes a snapshot. Guarded by a lock because routes A/B snapshot it
/// from their render thread.
/// </summary>
public sealed class ViewportInput(Camera camera)
{
    readonly Lock _gate = new();
    Camera _cam = camera;
    float _px = -1, _py = -1;
    bool _moved;
    uint _selected;
    public volatile bool AutoOrbit;

    public void Orbit(float dx, float dy) { lock (_gate) { _cam.Orbit(dx, dy); _moved = true; } }
    public void Pan(float dx, float dy, float h) { lock (_gate) { _cam.Pan(dx, dy, h); _moved = true; } }
    public void Zoom(float wheel) { lock (_gate) { _cam.Zoom(wheel); _moved = true; } }
    public void Hover(float x, float y) { lock (_gate) { _px = x; _py = y; } }
    public void Leave() { lock (_gate) { _px = -1; _py = -1; } }
    public void Select(uint id) { lock (_gate) _selected = id; }

    public FrameInput Snapshot(int w, int h)
    {
        lock (_gate)
        {
            if (AutoOrbit) { _cam.Orbit(2.5f, 0); _moved = true; }
            var f = new FrameInput { Camera = _cam, Width = Math.Max(1, w), Height = Math.Max(1, h), PickX = _px, PickY = _py, Orbiting = _moved, Selected = _selected };
            _moved = false;
            return f;
        }
    }
}
