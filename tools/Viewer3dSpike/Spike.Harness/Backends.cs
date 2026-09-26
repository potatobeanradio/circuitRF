using Viewer3dSpike.Render.Gl;
using Viewer3dSpike.Render.Metal;
using Viewer3dSpike.Render.Wgpu;
using Viewer3dSpike.Scene;

namespace Viewer3dSpike.Render;

/// <summary>One route, offscreen. <see cref="Frame"/> is the pane's own code (counted);
/// <see cref="EndOfFrame"/> stands in for what the host does after it (a flush / present).</summary>
interface IHarnessBackend : IDisposable
{
    string Info { get; }
    void Init(SceneModel scene, FrameCounters counters, int w, int h);
    void Frame(in FrameInput input);
    void EndOfFrame();
    uint Hovered { get; }
    bool PickPending { get; }
    int DrawCalls { get; }
    byte[] Screenshot();
}

sealed class GlBackend : IHarnessBackend
{
    CglOffscreen _ctx = null!;
    GlRenderer _r = null!;
    int _w, _h;
    public string Info => "OpenGL (CGL offscreen): " + _r.Info;

    public void Init(SceneModel scene, FrameCounters counters, int w, int h)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("the headless GL harness uses CGL (macOS); on Windows/Linux read the counters from the app's status line");
        _w = w; _h = h;
        _ctx = new CglOffscreen();
        _r = new GlRenderer(_ctx.GetProc, isEs: false, counters);
        _ctx.CreateTarget(_r.GL, w, h);
        _r.Init(scene);
    }

    public void Frame(in FrameInput input) => _r.Render(_ctx.Fbo, input);
    public unsafe void EndOfFrame() => _r.GL.Flush();
    public uint Hovered => _r.HoveredId;
    public bool PickPending => _r.PickPending;
    public int DrawCalls => _r.DrawCallsLastFrame;
    public byte[] Screenshot() => _ctx.ReadRgba(_r.GL, _w, _h);
    public void Dispose() { _r?.Dispose(); _ctx?.Dispose(); }
}

sealed class WgpuBackend : IHarnessBackend
{
    WgpuRenderer _r = null!;
    public string Info => "WebGPU (offscreen texture): " + _r.Info;
    public void Init(SceneModel scene, FrameCounters counters, int w, int h) { _r = new WgpuRenderer(counters); _r.Init(scene, w, h); }
    public void Frame(in FrameInput input) => _r.Render(input);
    public void EndOfFrame() { }
    public uint Hovered => _r.HoveredId;
    public bool PickPending => _r.PickPending;
    public int DrawCalls => _r.DrawCallsLastFrame;
    public byte[] Screenshot() => _r.ReadColor();
    public void Dispose() => _r?.Dispose();
}

sealed class MetalBackend : IHarnessBackend
{
    MetalRenderer _r = null!;
    nint _target;
    int _w, _h;
    public string Info => _r.Info + " (offscreen BGRA8 texture)" + (Environment.GetEnvironmentVariable("SPIKE_MSL") != null ? " — MSL cross-compiled from the WGSL by naga" : "");
    public void Init(SceneModel scene, FrameCounters counters, int w, int h)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("route A's Metal half is macOS; the D3D11 and Vulkan halves were not attempted");
        _w = w; _h = h;
        var msl = Environment.GetEnvironmentVariable("SPIKE_MSL");
        _r = new MetalRenderer(counters, 0, msl != null ? File.ReadAllText(msl) : null);
        _r.Init(scene);
        _target = _r.NewTexture(w, h, MetalRenderer.FmtBGRA8, 4 | 1, 0);
    }
    public void Frame(in FrameInput input) => _r.Render(_target, input);
    public void EndOfFrame() { }
    public uint Hovered => _r.HoveredId;
    public bool PickPending => _r.PickPending;
    public int DrawCalls => _r.DrawCallsLastFrame;
    public byte[] Screenshot() => _r.ReadBgra(_target, _w, _h);
    public void Dispose() => _r?.Dispose();
}

/// <summary>Route B's macOS PRESENT path, headless: wgpu renders, the bridge copies the frame into an
/// IOSurface-backed texture on wgpu's own queue and signals a shared event — exactly what the pane
/// does — and the harness then waits for that signal (as the compositor would) and reads the IOSurface
/// texture back. A signal that never arrives is the pane's "release timeouts" failure.</summary>
sealed unsafe class WgpuBridgeBackend : IHarnessBackend
{
    WgpuRenderer _r = null!;
    MetalRenderer _reader = null!;
    nint _surface, _target, _event;
    ulong _value;
    int _w, _h;
    public int SignalTimeouts;
    public string Info => "WebGPU -> IOSurface via wgpu's Metal queue: " + _r.Info;
    public void Init(SceneModel scene, FrameCounters counters, int w, int h)
    {
        _w = w; _h = h;
        _r = new WgpuRenderer(counters, W.FmtBGRA8);
        _r.Init(scene, w, h);
        nint device = (nint)_r.Api.DeviceGetNativeMetalDevice(_r.Device);
        var (q, src) = WgpuMetalBridge.Handles(_r);
        Console.WriteLine($"native  MTLDevice {device:X}, MTLCommandQueue {q:X}, colour MTLTexture {src:X}");
        _reader = new MetalRenderer(new FrameCounters("reader"), device);
        _surface = IOSurf.CreateBgra(w, h);
        _target = MetalRenderer.IOSurfaceTexture(device, _surface, w, h);
        _event = ObjC.Send(device, ObjC.Sel("newSharedEvent"));
    }
    public void Frame(in FrameInput input)
    {
        _r.Render(input);
        WgpuMetalBridge.Present(_r, _target, _event, ++_value, waitCompleted: false);
    }
    public void EndOfFrame()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (WgpuMetalBridge.SignaledValue(_event) < _value)
            if (sw.ElapsedMilliseconds > 250) { SignalTimeouts++; Console.WriteLine($"  ready signal {_value} not seen after 250 ms (event at {WgpuMetalBridge.SignaledValue(_event)})"); break; }
    }
    public uint Hovered => _r.HoveredId;
    public bool PickPending => _r.PickPending;
    public int DrawCalls => _r.DrawCallsLastFrame;
    public byte[] Screenshot() => _reader.ReadBgra(_target, _w, _h);
    public void Dispose() => _r?.Dispose();
}
