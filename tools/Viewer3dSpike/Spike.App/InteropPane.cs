using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Viewer3dSpike.Render.Direct3D;
using Viewer3dSpike.Render.Metal;
using Viewer3dSpike.Render.Vk;
using Viewer3dSpike.Render.Wgpu;
using Viewer3dSpike.Scene;
using Vortice.Vulkan;
using static Viewer3dSpike.Render.Metal.ObjC;

namespace Viewer3dSpike.App;

/// <summary>
/// Routes A and B: composition GPU interop (em-3d.md §8.3, the chosen direction). The pane owns a
/// swapchain of three shared images, imported into Avalonia's compositor once. A dedicated RENDER
/// THREAD draws each frame into a free image; the UI thread's share of a frame (<see cref="OnTick"/>,
/// once per composition frame) is handing the finished image to the compositor and asking for the
/// next — it never touches geometry and never waits on the GPU.
///
/// The IMAGE SOURCE is per platform (brief em3d-28 step 0 added the last two):
/// <list type="bullet">
/// <item><b>macOS</b> — IOSurface + MTLSharedEvent timeline semaphores (else "automatic"): Metal (route A)
///   or wgpu's Metal texture (route B).</item>
/// <item><b>Windows</b> — a D3D11 texture with a KEYED MUTEX, imported by its DXGI global shared handle
///   into ANGLE (Avalonia's default there), on the compositor's own adapter (LUID). The render thread
///   takes key 0, draws, gives key 1; the compositor takes 1 and gives 0 back
///   (<c>UpdateWithKeyedMutexAsync(image, 1, 0)</c>).</item>
/// <item><b>Linux</b> — a Vulkan image exported as an opaque POSIX fd with a pair of exported binary
///   semaphores per image (ready: us → compositor; released: compositor → us), on the compositor's own
///   device (UUID), handed over in TRANSFER_SRC_OPTIMAL. Avalonia's GLX compositor imports these only
///   when the GL driver has GL_EXT_memory_object_fd and GL_EXT_semaphore_fd; <c>--compositor vulkan</c>
///   puts the window on Avalonia's Vulkan compositor instead.</item>
/// </list>
/// A synchronisation step that cannot complete is a FAULT — reported in the pane and the rendering
/// stopped — never a timeout loop (findings §5.6: a missing signal once froze the whole window).
/// </summary>
sealed class InteropPane : Control, IPane
{
    readonly Session _s;
    readonly Action _tick;
    Compositor? _compositor;
    ICompositionGpuInterop? _interop;
    CompositionDrawingSurface? _surface;
    CompositionSurfaceVisual? _visual;
    IImageSource? _src;
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
            var visual = ElementComposition.GetElementVisual(this);
            if (visual == null) { Fail("no composition visual"); return; }
            _compositor = visual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (_interop == null) { Fail("Compositor.TryGetCompositionGpuInterop() returned null for this backend (see --compositor)"); return; }
            string offered = Offered(_interop);
            _s.HostInfo = "compositor offered: " + offered;
            Console.WriteLine("[pane] compositor offered: " + offered);

            _src = _s.Route switch
            {
                "metal" or "wgpu" when OperatingSystem.IsMacOS() => new MetalSource(_s),
                "d3d11" when OperatingSystem.IsWindows() => new D3D11Source(_s),
                "vulkan" when !OperatingSystem.IsMacOS() => new VulkanSource(_s),
                _ => null,
            };
            if (_src == null)
            {
                Fail($"route {_s.Route} has no composition-interop image source on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                     "(metal/wgpu: macOS; d3d11: Windows; vulkan: Linux)");
                return;
            }
            string? why = _src.Setup(_interop);
            if (why != null) { Fail(why + " — compositor offered " + offered); return; }

            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Surface = _surface;
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            ElementComposition.SetElementChildVisual(this, _visual);
            _s.HostInfo = $"composition interop: {offered} -> {_src.Describe}";

            _stop = false;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "viewer3d-render" };
            _thread.Start();
            RequestFrame();
        }
        catch (Exception ex) { Fail(ex.GetType().Name + ": " + ex.Message); }
    }

    static string Offered(ICompositionGpuInterop i)
    {
        string images = string.Join(",", i.SupportedImageHandleTypes), sems = string.Join(",", i.SupportedSemaphoreTypes);
        var sync = string.Join(" ", i.SupportedImageHandleTypes.Select(t => $"{t}:[{i.GetSynchronizationCapabilities(t)}]"));
        string luid = i.DeviceLuid is { } l ? Convert.ToHexString(l) : "-", uuid = i.DeviceUuid is { } u ? Convert.ToHexString(u) : "-";
        return $"images [{images}], semaphores [{sems}], sync {sync}, LUID {luid}, UUID {uuid}";
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _stop = true;
        _go.Set();
        _thread?.Join(2000);
        _thread = null;
        ElementComposition.SetElementChildVisual(this, null);
        _src?.Dispose();
        _src = null;
        _imgW = _imgH = 0;
        _surface?.Dispose(); _surface = null;
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

    internal static void Release(object? o)
    {
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
        else if (o is IDisposable d) d.Dispose();
    }

    void Fail(string msg)
    {
        Error = msg;
        _s.HostInfo = "NOT HOSTED: " + msg;
        Console.WriteLine("[pane] NOT HOSTED: " + msg);
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
        if (_surface == null || _stop || _src == null) return;
        if (_src.Fault is { } fault) { Fail(fault); return; }
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var input = _s.Input.Snapshot(w, h);
        _s.Ui.BeginFrame(input.Orbiting);

        if (_done)
        {
            _src.Present(_surface, _doneImage, _doneValue);
            _done = false;
            _busy = false;
        }

        bool more = _s.Continuous || input.Orbiting || _s.Input.AutoOrbit;
        if (!_busy)
        {
            if (w != _imgW || h != _imgH) { _src.Recreate(_interop!, w, h); _imgW = w; _imgH = h; }   // the render thread is idle
            _next = input;
            _nextImage = (int)(_frame % 3);
            _frame++;
            _busy = true;
            _go.Set();
        }
        _s.Ui.EndFrame();
        if (more || _busy) RequestFrame();
    }

    void RenderLoop()
    {
        _s.RenderThreadId = Environment.CurrentManagedThreadId;
        while (true)
        {
            _go.WaitOne();
            if (_stop) return;
            var input = _next;
            ulong value = (ulong)_frame;
            bool ok;
            lock (_s.RenderLock)
            {
                _s.Render.BeginFrame(input.Orbiting);
                ok = _src!.Render(_nextImage, input, value);
                _s.Render.EndFrame();
            }
            _s.Hovered = _src.Hovered;
            if (!ok) return;            // a fault: OnTick reports it and stops asking for frames
            _doneImage = _nextImage;
            _doneValue = value;
            _done = true;
        }
    }
}

/// <summary>One platform's shared images and the synchronisation that goes with them. Setup and
/// Recreate/Present run on the UI thread; Render on the pane's render thread, never at the same time
/// as Recreate.</summary>
interface IImageSource : IDisposable
{
    /// <summary>Null when this source can present through what the compositor offers; else why not.</summary>
    string? Setup(ICompositionGpuInterop interop);
    string Describe { get; }
    void Recreate(ICompositionGpuInterop interop, int w, int h);
    /// <summary>Draw into image <paramref name="i"/>. False after recording <see cref="Fault"/>.</summary>
    bool Render(int i, in FrameInput input, ulong value);
    void Present(CompositionDrawingSurface surface, int i, ulong value);
    uint Hovered { get; }
    string? Fault { get; }
}

/// <summary>macOS: IOSurface + MTLSharedEvent (brief em3d-27), unchanged in behaviour.</summary>
sealed unsafe class MetalSource(Session s) : IImageSource
{
    sealed class Image
    {
        public nint Surface, Texture;
        public ICompositionImportedGpuImage? Imported;
        public ulong ReleaseNeeded;
        public Task? Pending;
    }
    ICompositionImportedGpuSemaphore? _readySem, _releasedSem;
    nint _readyEvt, _releasedEvt, _device;
    bool _timeline;
    Image[] _images = [];
    public string Describe { get; private set; } = "";
    public string? Fault => null;
    public uint Hovered => s.Metal?.HoveredId ?? s.Wgpu?.HoveredId ?? 0;

    public string? Setup(ICompositionGpuInterop interop)
    {
        if (!interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef))
            return "compositor cannot import IOSurfaceRef — try --compositor metal";
        var caps = interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
        _timeline = caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores)
                    && interop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent);
        if (!_timeline && !caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Automatic))
            return $"IOSurfaceRef sync capabilities [{caps}] offer neither timeline semaphores nor automatic";
        EnsureRenderer();
        if (_timeline)
        {
            _readyEvt = Send(_device, Sel("newSharedEvent"));
            _releasedEvt = Send(_device, Sel("newSharedEvent"));
            _readySem = interop.ImportSemaphore(new PlatformHandle(_readyEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
            _releasedSem = interop.ImportSemaphore(new PlatformHandle(_releasedEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
        }
        Describe = $"IOSurface, {(_timeline ? "timeline semaphores (MTLSharedEvent)" : "automatic + GPU wait on the render thread")}";
        return null;
    }

    void EnsureRenderer()
    {
        s.RenderThreadId = -1;
        if (s.Route == "metal")
        {
            if (s.Metal == null)
            {
                s.Metal = new MetalRenderer(s.Render);
                s.Metal.Init(s.Scene);
                s.Inits++;
                s.DeviceInfo = s.Metal.Info + " (own device, own queue)";
            }
            _device = s.Metal.Device;
        }
        else
        {
            if (s.Wgpu == null)
            {
                s.Wgpu = new WgpuRenderer(s.Render, W.FmtBGRA8);
                s.Wgpu.Init(s.Scene, 16, 16);
                s.Inits++;
                s.DeviceInfo = s.Wgpu.Info + " — presented through its native Metal texture (wgpuTextureGetNativeMetalTexture)";
            }
            _device = (nint)s.Wgpu.Api.DeviceGetNativeMetalDevice(s.Wgpu.Device);
            if (_device == 0) throw new InvalidOperationException("wgpu returned no native Metal device (backend is not Metal?)");
        }
    }

    public void Recreate(ICompositionGpuInterop interop, int w, int h)
    {
        foreach (var im in _images) DisposeImage(im);
        _images = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            var im = new Image { Surface = IOSurf.CreateBgra(w, h) };
            im.Texture = MetalRenderer.IOSurfaceTexture(_device, im.Surface, w, h);
            im.Imported = interop.ImportImage(new PlatformHandle(im.Surface, KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef),
                new PlatformGraphicsExternalImageProperties { Width = w, Height = h, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm, TopLeftOrigin = true });
            _images[i] = im;
        }
        s.Wgpu?.Resize(w, h);
    }

    public bool Render(int i, in FrameInput input, ulong value)
    {
        var im = _images[i];
        // don't draw into an image the compositor may still be reading
        if (_timeline)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (SignaledValue(_releasedEvt) < im.ReleaseNeeded)
            {
                if (sw.ElapsedMilliseconds > 250) { s.ReleaseTimeouts++; break; }
                Thread.SpinWait(100);
            }
        }
        else if (im.Pending is { IsCompleted: false } t && !t.Wait(250)) s.ReleaseTimeouts++;

        if (s.Metal != null)
            s.Metal.Render(im.Texture, input, _timeline ? _readyEvt : 0, 0, _timeline ? value : 0, waitCompleted: !_timeline);
        else if (s.Wgpu is { } wg)
        {
            wg.Render(input);
            WgpuMetalBridge.Present(wg, im.Texture, _timeline ? _readyEvt : 0, value, !_timeline);
        }
        return true;
    }

    public void Present(CompositionDrawingSurface surface, int i, ulong value)
    {
        var im = _images[i];
        if (_timeline)
        {
            surface.UpdateWithTimelineSemaphoresAsync(im.Imported!, _readySem!, value, _releasedSem!, value);
            im.ReleaseNeeded = value;
        }
        else im.Pending = surface.UpdateAsync(im.Imported!);
    }

    static void DisposeImage(Image im)
    {
        InteropPane.Release(im.Imported);
        if (im.Texture != 0) Send(im.Texture, S.release);
        if (im.Surface != 0) IOSurf.CFRelease(im.Surface);
    }

    public void Dispose()
    {
        foreach (var im in _images) DisposeImage(im);
        _images = [];
        InteropPane.Release(_readySem); InteropPane.Release(_releasedSem); _readySem = _releasedSem = null;
        if (_readyEvt != 0) { Send(_readyEvt, S.release); Send(_releasedEvt, S.release); _readyEvt = _releasedEvt = 0; }
    }

    static ulong SignaledValue(nint evt) => ((delegate* unmanaged<nint, nint, ulong>)MsgSend)(evt, S.signaledValue);
}

/// <summary>
/// Windows: route A's D3D11 half. Avalonia's default Windows compositor is ANGLE over D3D11, which
/// imports a D3D11 texture by DXGI shared handle and synchronises it with the texture's KEYED MUTEX
/// only (it offers no semaphores), in R8G8B8A8 only. The device is created on the compositor's adapter
/// (LUID) so the shared handle opens there.
/// </summary>
sealed class D3D11Source(Session s) : IImageSource
{
    sealed class Image
    {
        public Vortice.Direct3D11.ID3D11Texture2D Texture = null!;
        public Vortice.Direct3D11.ID3D11RenderTargetView View = null!;
        public Vortice.DXGI.IDXGIKeyedMutex Mutex = null!;
        public ICompositionImportedGpuImage Imported = null!;
    }
    const int KeyRenderer = 0, KeyCompositor = 1, AcquireTimeoutMs = 250;
    const int WAIT_TIMEOUT = 0x102, WAIT_ABANDONED = 0x80;
    Image[] _images = [];
    string _handleType = "";
    public string Describe { get; private set; } = "";
    public string? Fault { get; private set; }
    public uint Hovered => s.D3D11?.HoveredId ?? 0;

    public string? Setup(ICompositionGpuInterop interop)
    {
        var types = interop.SupportedImageHandleTypes;
        _handleType = types.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
            ? KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle : "";
        if (_handleType == "") return "compositor cannot import a D3D11 texture by global shared handle";
        if (!interop.GetSynchronizationCapabilities(_handleType).HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
            return $"{_handleType} offers no keyed-mutex synchronisation";
        if (s.D3D11 == null)
        {
            s.D3D11 = new D3D11Renderer(s.Render, interop.DeviceLuid);
            s.D3D11.Init(s.Scene, File.ReadAllText(Viewer3dSpike.Render.ShaderFiles.Find("scene.hlsl")));
            s.Inits++;
            s.DeviceInfo = s.D3D11.Info + " (own device, own immediate context)";
        }
        s.RenderThreadId = -1;
        Describe = $"{_handleType}, keyed mutex (renderer key {KeyRenderer}, compositor key {KeyCompositor})";
        return null;
    }

    public void Recreate(ICompositionGpuInterop interop, int w, int h)
    {
        DisposeImages();
        var r = s.D3D11!;
        _images = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            var im = new Image { Texture = r.NewTarget(w, h, shared: true) };
            im.View = r.TargetView(im.Texture);
            im.Mutex = im.Texture.QueryInterface<Vortice.DXGI.IDXGIKeyedMutex>();
            using var res = im.Texture.QueryInterface<Vortice.DXGI.IDXGIResource>();
            nint handle = res.SharedHandle;
            if (handle == 0) throw new InvalidOperationException("IDXGIResource::GetSharedHandle returned null");
            im.Imported = interop.ImportImage(new PlatformHandle(handle, _handleType),
                new PlatformGraphicsExternalImageProperties { Width = w, Height = h, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm, TopLeftOrigin = true });
            _images[i] = im;
        }
    }

    public bool Render(int i, in FrameInput input, ulong value)
    {
        var im = _images[i];
        int hr = D3D11Renderer.AcquireSync(im.Mutex, KeyRenderer, AcquireTimeoutMs);
        if (hr == WAIT_TIMEOUT || hr == WAIT_ABANDONED || hr < 0)
        {
            s.ReleaseTimeouts++;
            Fault = $"the compositor did not hand image {i} back (IDXGIKeyedMutex::AcquireSync({KeyRenderer}) = 0x{hr:X8} after {AcquireTimeoutMs} ms) — a fault, not retried";
            return false;
        }
        s.D3D11!.Render(im.View, input);
        im.Mutex.ReleaseSync(KeyCompositor);
        return true;
    }

    public void Present(CompositionDrawingSurface surface, int i, ulong value) =>
        _ = surface.UpdateWithKeyedMutexAsync(_images[i].Imported, KeyCompositor, KeyRenderer);

    void DisposeImages()
    {
        foreach (var im in _images)
        {
            InteropPane.Release(im.Imported);
            im.Mutex?.Dispose(); im.View?.Dispose(); im.Texture?.Dispose();
        }
        _images = [];
    }

    public void Dispose() => DisposeImages();
}

/// <summary>
/// Linux: route A's Vulkan half. Images and binary semaphores exported as opaque POSIX fds (each fd's
/// ownership passes to the importer, so a fresh one is exported per import). Per image: "ready" — we
/// signal it when the frame is complete and in TRANSFER_SRC_OPTIMAL; "released" — the compositor signals
/// it when it has read the image, and the next frame into that image waits for it on the GPU. A binary
/// semaphore may only be waited after its signal was SUBMITTED, so the render thread first sees the
/// compositor's update task finish; one that never finishes is a fault.
/// </summary>
sealed class VulkanSource(Session s) : IImageSource
{
    sealed class Image
    {
        public VulkanTarget Target = null!;
        public VkSemaphore Ready, Released;
        public ICompositionImportedGpuImage Imported = null!;
        public ICompositionImportedGpuSemaphore ReadyImported = null!, ReleasedImported = null!;
        public Task? Pending;
        public bool ReleaseOwed;
    }
    const int ReleaseTimeoutMs = 250;
    Image[] _images = [];
    public string Describe { get; private set; } = "";
    public string? Fault { get; private set; }
    public uint Hovered => s.Vulkan?.HoveredId ?? 0;

    public string? Setup(ICompositionGpuInterop interop)
    {
        const string fd = KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor;
        if (!interop.SupportedImageHandleTypes.Contains(fd))
            return "compositor cannot import a VulkanOpaquePosixFileDescriptor image (under GLX that needs GL_EXT_memory_object_fd + GL_EXT_semaphore_fd; try --compositor vulkan)";
        if (!interop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor))
            return "compositor cannot import a VulkanOpaquePosixFileDescriptor semaphore";
        if (!interop.GetSynchronizationCapabilities(fd).HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Semaphores))
            return "VulkanOpaquePosixFileDescriptor images offer no semaphore synchronisation";
        if (s.Vulkan == null)
        {
            s.Vulkan = new VulkanRenderer(s.Render, interop.DeviceUuid);
            if (!s.Vulkan.CanExport)
                return $"{s.Vulkan.Info}: the Vulkan device cannot export memory and semaphores as fds";
            s.Vulkan.Init(s.Scene, File.ReadAllBytes(Viewer3dSpike.Render.ShaderFiles.Find("scene.spv")));
            s.Inits++;
            s.DeviceInfo = s.Vulkan.Info + " (own device, own queue)";
        }
        s.RenderThreadId = -1;
        Describe = "VulkanOpaquePosixFileDescriptor image + binary semaphore pair per image, TRANSFER_SRC_OPTIMAL hand-over";
        return null;
    }

    public void Recreate(ICompositionGpuInterop interop, int w, int h)
    {
        DisposeImages();
        var r = s.Vulkan!;
        const string fd = KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor;
        _images = new Image[3];
        for (int i = 0; i < 3; i++)
        {
            var im = new Image { Target = r.NewTarget(w, h, exportable: true), Ready = r.NewExportableSemaphore(), Released = r.NewExportableSemaphore() };
            im.Imported = interop.ImportImage(new PlatformHandle(r.ExportMemoryFd(im.Target), fd),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = w, Height = h, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                    MemorySize = im.Target.MemorySize, MemoryOffset = 0, TopLeftOrigin = true,
                });
            im.ReadyImported = interop.ImportSemaphore(new PlatformHandle(r.ExportSemaphoreFd(im.Ready), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor));
            im.ReleasedImported = interop.ImportSemaphore(new PlatformHandle(r.ExportSemaphoreFd(im.Released), KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor));
            _images[i] = im;
        }
    }

    public bool Render(int i, in FrameInput input, ulong value)
    {
        var im = _images[i];
        VkSemaphore wait = default;
        if (im.ReleaseOwed)
        {
            // the compositor's "released" signal must have been submitted before we may wait on it
            if (im.Pending is { } t && !t.Wait(ReleaseTimeoutMs))
            {
                s.ReleaseTimeouts++;
                Fault = $"the compositor's update of image {i} did not complete in {ReleaseTimeoutMs} ms, so its release semaphore cannot be waited — a fault, not retried";
                return false;
            }
            if (im.Pending is { IsFaulted: true } f)
            {
                Fault = $"the compositor's update of image {i} failed: {f.Exception?.GetBaseException().Message}";
                return false;
            }
            wait = im.Released;
            im.ReleaseOwed = false;
        }
        s.Vulkan!.Render(im.Target, input, wait, im.Ready);
        return true;
    }

    public void Present(CompositionDrawingSurface surface, int i, ulong value)
    {
        var im = _images[i];
        im.Pending = surface.UpdateWithSemaphoresAsync(im.Imported, im.ReadyImported, im.ReleasedImported);
        im.ReleaseOwed = true;
    }

    void DisposeImages()
    {
        var r = s.Vulkan;
        foreach (var im in _images)
        {
            im.Pending?.Wait(ReleaseTimeoutMs);
            InteropPane.Release(im.Imported); InteropPane.Release(im.ReadyImported); InteropPane.Release(im.ReleasedImported);
            if (r != null)
            {
                r.DestroyTarget(im.Target);
                unsafe { r.Api.vkDestroySemaphore(im.Ready, null); r.Api.vkDestroySemaphore(im.Released, null); }
            }
        }
        _images = [];
    }

    public void Dispose() => DisposeImages();
}
