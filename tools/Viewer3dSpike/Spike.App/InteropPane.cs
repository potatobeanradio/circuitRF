using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Viewer3dSpike.Render.Metal;
using Viewer3dSpike.Render.Wgpu;
using Viewer3dSpike.Scene;
using static Viewer3dSpike.Render.Metal.ObjC;

namespace Viewer3dSpike.App;

/// <summary>
/// Routes A and B: composition GPU interop (em-3d.md §8.3, the chosen direction). The pane owns a
/// swapchain of three IOSurface-backed textures, imported into Avalonia's compositor once. A
/// dedicated RENDER THREAD draws each frame into a free image; the UI thread's share of a frame
/// (<see cref="OnTick"/>, once per composition frame) is handing the finished image to the
/// compositor and asking for the next — it never touches geometry and never waits on the GPU.
///
/// Synchronisation follows what the compositor offers: timeline semaphores over two
/// MTLSharedEvents (ready: render thread → compositor; released: compositor → render thread) where
/// supported, else "automatic" (the render thread waits for its own GPU work to finish before the
/// image is handed over, and does not reuse an image until the compositor's update task ends).
///
/// macOS only in this spike. On Windows (D3D11 shared handle + keyed mutex under ANGLE) and Linux
/// (Vulkan opaque FD) the same class shape applies with a different image source — not attempted.
/// </summary>
sealed class InteropPane : Control, IPane
{
    sealed class Image
    {
        public nint Surface, Texture;
        public ICompositionImportedGpuImage? Imported;
        public ulong ReleaseNeeded;
        public Task? Pending;
    }

    readonly Session _s;
    readonly Action _tick;
    Compositor? _compositor;
    ICompositionGpuInterop? _interop;
    CompositionDrawingSurface? _surface;
    CompositionSurfaceVisual? _visual;
    ICompositionImportedGpuSemaphore? _readySem, _releasedSem;
    nint _readyEvt, _releasedEvt, _device;
    bool _timeline;
    Image[] _images = [];
    int _imgW, _imgH;

    Thread? _thread;
    readonly AutoResetEvent _go = new(false);
    volatile bool _stop, _busy, _done;
    FrameInput _next;
    int _nextImage;
    long _frame;           // frames requested
    int _doneImage;
    ulong _doneValue;
    bool _tickQueued;
    public string? Error { get; private set; }

    public InteropPane(Session s)
    {
        _s = s;
        _tick = OnTick;
        ClipToBounds = true;
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        try
        {
            if (!OperatingSystem.IsMacOS()) { Fail("composition interop hosting is implemented for macOS only in this spike (Windows: D3D11 shared handle; Linux: Vulkan FD — not attempted)"); return; }
            var visual = ElementComposition.GetElementVisual(this);
            if (visual == null) { Fail("no composition visual"); return; }
            _compositor = visual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (_interop == null) { Fail("Compositor.TryGetCompositionGpuInterop() returned null for this backend — try --compositor metal"); return; }
            string images = string.Join(",", _interop.SupportedImageHandleTypes), sems = string.Join(",", _interop.SupportedSemaphoreTypes);
            if (!_interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef))
            { Fail($"compositor cannot import IOSurfaceRef (images: [{images}], semaphores: [{sems}]) — try --compositor metal"); return; }
            var caps = _interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
            _timeline = caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores)
                        && _interop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent);
            if (!_timeline && !caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Automatic))
            { Fail($"IOSurfaceRef sync capabilities [{caps}] offer neither timeline semaphores nor automatic"); return; }

            EnsureRenderer();
            if (_timeline)
            {
                _readyEvt = Send(_device, Sel("newSharedEvent"));
                _releasedEvt = Send(_device, Sel("newSharedEvent"));
                _readySem = _interop.ImportSemaphore(new PlatformHandle(_readyEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
                _releasedSem = _interop.ImportSemaphore(new PlatformHandle(_releasedEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
            }
            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
            _s.HostInfo = $"composition interop: images [{images}], semaphores [{sems}], IOSurface sync [{caps}] -> using {(_timeline ? "timeline semaphores (MTLSharedEvent)" : "automatic + GPU wait on the render thread")}";

            _stop = false;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "viewer3d-render" };
            _thread.Start();
            RequestFrame();
        }
        catch (Exception ex) { Fail(ex.GetType().Name + ": " + ex.Message); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _stop = true;
        _go.Set();
        _thread?.Join(2000);
        _thread = null;
        ElementComposition.SetElementChildVisual(this, null);
        foreach (var im in _images) DisposeImage(im);
        _images = [];
        _imgW = _imgH = 0;
        _surface?.Dispose(); _surface = null;
        Release(_readySem); Release(_releasedSem); _readySem = _releasedSem = null;
        if (_readyEvt != 0) { Send(_readyEvt, S.release); Send(_releasedEvt, S.release); _readyEvt = _releasedEvt = 0; }
        _busy = _done = false;
        _tickQueued = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _visual != null)
        {
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            RequestFrame();
        }
    }

    static void Release(object? o)
    {
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
        else if (o is IDisposable d) d.Dispose();
    }

    void Fail(string msg) { Error = msg; _s.HostInfo = "NOT HOSTED: " + msg; }

    unsafe void EnsureRenderer()
    {
        _s.RenderThreadId = -1;
        if (_s.Route == "metal")
        {
            if (_s.Metal == null)
            {
                _s.Metal = new MetalRenderer(_s.Render);
                _s.Metal.Init(_s.Scene);
                _s.Inits++;
                _s.DeviceInfo = _s.Metal.Info + " (own device, own queue)";
            }
            _device = _s.Metal.Device;
        }
        else
        {
            if (_s.Wgpu == null)
            {
                _s.Wgpu = new WgpuRenderer(_s.Render, W.FmtBGRA8);
                _s.Wgpu.Init(_s.Scene, 16, 16);
                _s.Inits++;
                _s.DeviceInfo = _s.Wgpu.Info + " — presented through its native Metal texture (wgpuTextureGetNativeMetalTexture)";
            }
            _device = (nint)_s.Wgpu.Api.DeviceGetNativeMetalDevice(_s.Wgpu.Device);
            if (_device == 0) throw new InvalidOperationException("wgpu returned no native Metal device (backend is not Metal?)");
        }
    }

    public void RequestFrame()
    {
        if (_compositor == null || _tickQueued || Error != null) return;
        _tickQueued = true;
        _compositor.RequestCompositionUpdate(_tick);
    }

    /// <summary>The UI thread's whole per-frame cost for routes A/B: present what is finished,
    /// ask for the next. Counted on the UI lane.</summary>
    void OnTick()
    {
        _tickQueued = false;
        if (_surface == null || _stop) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var input = _s.Input.Snapshot(w, h);
        _s.Ui.BeginFrame(input.Orbiting);

        if (_done)
        {
            var im = _images[_doneImage];
            if (_timeline)
            {
                _surface.UpdateWithTimelineSemaphoresAsync(im.Imported!, _readySem!, _doneValue, _releasedSem!, _doneValue);
                im.ReleaseNeeded = _doneValue;
            }
            else im.Pending = _surface.UpdateAsync(im.Imported!);
            _done = false;
            _busy = false;
        }

        bool more = _s.Continuous || input.Orbiting || _s.Input.AutoOrbit;
        if (!_busy)
        {
            if (w != _imgW || h != _imgH) Recreate(w, h);
            _next = input;
            _nextImage = (int)(_frame % _images.Length);
            _frame++;
            _busy = true;
            _go.Set();
        }
        _s.Ui.EndFrame();
        if (more || _busy) RequestFrame();
    }

    void Recreate(int w, int h)
    {
        foreach (var im in _images) DisposeImage(im);
        _images = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            var im = new Image { Surface = IOSurf.CreateBgra(w, h) };
            im.Texture = MetalRenderer.IOSurfaceTexture(_device, im.Surface, w, h);
            im.Imported = _interop!.ImportImage(new PlatformHandle(im.Surface, KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef),
                new PlatformGraphicsExternalImageProperties { Width = w, Height = h, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm, TopLeftOrigin = true });
            _images[i] = im;
        }
        _imgW = w; _imgH = h;
        _s.Wgpu?.Resize(w, h);   // safe: the render thread is idle (_busy is false)
    }

    static unsafe void DisposeImage(Image im)
    {
        Release(im.Imported);
        if (im.Texture != 0) Send(im.Texture, S.release);
        if (im.Surface != 0) IOSurf.CFRelease(im.Surface);
    }

    unsafe void RenderLoop()
    {
        _s.RenderThreadId = Environment.CurrentManagedThreadId;
        while (true)
        {
            _go.WaitOne();
            if (_stop) return;
            var input = _next;
            var im = _images[_nextImage];
            ulong value = (ulong)_frame;
            // don't draw into an image the compositor may still be reading
            if (_timeline)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (SignaledValue(_releasedEvt) < im.ReleaseNeeded)
                {
                    if (sw.ElapsedMilliseconds > 250) { _s.ReleaseTimeouts++; break; }
                    Thread.SpinWait(100);
                }
            }
            else if (im.Pending is { IsCompleted: false } t && !t.Wait(250)) _s.ReleaseTimeouts++;

            lock (_s.RenderLock)
            {
                _s.Render.BeginFrame(input.Orbiting);
                if (_s.Metal != null)
                    _s.Metal.Render(im.Texture, input, _timeline ? _readyEvt : 0, 0, _timeline ? value : 0, waitCompleted: !_timeline);
                else if (_s.Wgpu is { } wg)
                {
                    wg.Render(input);
                    WgpuMetalBridge.Present(wg, im.Texture, _timeline ? _readyEvt : 0, value, !_timeline);
                }
                _s.Render.EndFrame();
            }
            _s.Hovered = _s.Metal?.HoveredId ?? _s.Wgpu?.HoveredId ?? 0;
            _doneImage = _nextImage;
            _doneValue = value;
            _done = true;
        }
    }

    static unsafe ulong SignaledValue(nint evt) => ((delegate* unmanaged<nint, nint, ulong>)MsgSend)(evt, S.signaledValue);
}
