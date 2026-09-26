// brief-em3d-28 R-em3d28-1b — route A on Windows: native Direct3D 11 through Vortice's MANAGED bindings
// (MIT; d3d11.dll, dxgi.dll and d3dcompiler_47.dll are the operating system's). No native package
// (R-em3d28-1c).
//
// Lifted from tools/Viewer3dSpike's D3D11 half (brief 28 step 0, findings §8), whose behaviour was
// read from Avalonia 12.0.3's own importer rather than assumed:
//   * Avalonia's default Windows compositor is ANGLE over D3D11. It imports a D3D11 texture by DXGI
//     shared handle and synchronises it with the texture's KEYED MUTEX ONLY (it offers no semaphores),
//     and in R8G8B8A8 ONLY (it wraps the texture as an EGL pbuffer). So the images are R8G8B8A8 with
//     D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX, and the device is created on the compositor's adapter
//     (ICompositionGpuInterop.DeviceLuid) — a shared handle does not open across adapters.
//   * Protocol: the render thread takes key 0, draws, and gives key 1; UpdateWithKeyedMutexAsync(image,
//     1, 0) makes the compositor take 1 and give 0 back.
//   * IDXGIKeyedMutex::AcquireSync reports a timeout as WAIT_TIMEOUT (0x102) — a SUCCESS HRESULT that
//     the managed wrapper does not throw on. It is called through the vtable here so a timeout is seen:
//     WaitReusable returns false, and Render refuses (Viewer3DPresentFault) to draw into an image it
//     does not own rather than loop.
//
// The shader is the generated scene.hlsl (Shaders/), compiled once at start-up by d3dcompiler_47 for
// vs_5_0 / ps_5_0. naga names every user varying LOC<n>, which is what the input layout binds. The
// 400-byte uniform block (brief 29 added the field block) goes in by UpdateSubresource on a DEFAULT constant buffer per pass — counted
// as uniform bytes, never geometry.
//
// Built and compiled on macOS; NOT YET RUN on Windows (the owner's check, findings §7).

using System.Numerics;
using System.Runtime.Versioning;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DxFormat = Vortice.DXGI.Format;
using DxMapFlags = Vortice.Direct3D11.MapFlags;

namespace CircuitRF.Ui.Viewer3D.D3D11;

[SupportedOSPlatform("windows")]
internal sealed unsafe class D3D11Viewer3DBackend : Viewer3DBackend
{
    private const int Ring = 3;
    private const DxFormat ColorFormat = DxFormat.R8G8B8A8_UNorm;
    private const ulong KeyRenderer = 0, KeyCompositor = 1;
    private const int WaitTimeout = 0x102, WaitAbandoned = 0x80;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private byte[]? _adapterLuid;
    private string _description = "Direct3D 11 (no device yet)";
    private ID3D11InputLayout _layout = null!, _layoutField = null!;
    private ID3D11VertexShader _vs = null!, _vsField = null!;
    private ID3D11PixelShader _psColor = null!, _psLine = null!, _psPick = null!, _psField = null!;
    private ID3D11Buffer? _field;
    private int _fieldCount;
    private ID3D11BlendState _blendOff = null!, _blendOn = null!;
    private ID3D11DepthStencilState _dsWrite = null!, _dsNoWrite = null!;
    private ID3D11RasterizerState _raster = null!;
    private ID3D11Buffer _cb = null!;
    private ID3D11Texture2D _pickId = null!, _pickPos = null!, _pickDepth = null!;
    private ID3D11RenderTargetView _pickIdRtv = null!, _pickPosRtv = null!;
    private ID3D11DepthStencilView _pickDsv = null!;
    private readonly ID3D11Texture2D[] _stagingId = new ID3D11Texture2D[Ring], _stagingPos = new ID3D11Texture2D[Ring];
    private readonly ID3D11Query[] _query = new ID3D11Query[Ring];
    private readonly bool[] _inFlight = new bool[Ring];
    private readonly long[] _rbFrame = new long[Ring];
    private int _rbHead;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _dsv;
    private int _depthW, _depthH;
    private ID3D11Buffer? _vb, _ib, _lines;
    private readonly ID3D11Buffer?[] _overlays = new ID3D11Buffer?[3];
    private readonly ID3D11RenderTargetView[] _pickTargets = new ID3D11RenderTargetView[2];

    private sealed class Image
    {
        public ID3D11Texture2D Texture = null!;
        public ID3D11RenderTargetView View = null!;
        public IDXGIKeyedMutex Mutex = null!;
        public ICompositionImportedGpuImage Imported = null!;
        public bool Owned;
    }
    private Image[] _images = [];
    private string _handleType = "";

    public override string Description => _description;

    // The device is made in CheckInterop, once the compositor's adapter is known; UploadScene before
    // that (a session that uploads first) makes it on the default adapter.
    private ID3D11Device Device => _device ?? CreateDevice(null);
    private ID3D11DeviceContext Ctx => _ctx ?? throw new Viewer3DPresentFault("The 3D view's D3D11 device has no context.");

    private ID3D11Device CreateDevice(byte[]? luid)
    {
        IDXGIAdapter1? chosen = null;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (luid is { Length: 8 })
        {
            uint lo = BitConverter.ToUInt32(luid, 0);
            int hi = BitConverter.ToInt32(luid, 4);
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
            {
                var l = a.Description1.Luid;
                if (l.LowPart == lo && l.HighPart == hi) { chosen = a; break; }
                a.Dispose();
            }
            if (chosen is null)
                throw new Viewer3DPresentFault($"No DXGI adapter has the compositor's LUID {Convert.ToHexString(luid)}.");
        }
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var r = Vortice.Direct3D11.D3D11.D3D11CreateDevice(chosen, chosen is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (r.Failure || device is null || context is null)
        {
            chosen?.Dispose();
            throw new Viewer3DPresentFault($"D3D11CreateDevice failed ({r}).");
        }
        _device = device;
        _ctx = context;
        _adapterLuid = luid;
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            _description = $"Direct3D 11 ({device.FeatureLevel}) — {adapter.Description.Description}";
        chosen?.Dispose();
        BuildPipelines();
        return device;
    }

    private void BuildPipelines()
    {
        var dev = _device!;
        string hlsl = Viewer3DShaders.Hlsl;
        ReadOnlyMemory<byte> Compile(string entry, string profile)
        {
            try { return Compiler.Compile(hlsl, entry, "scene.hlsl", profile); }
            catch (Exception ex) { throw new Viewer3DPresentFault($"The 3D view's HLSL entry point '{entry}' did not compile: {ex.Message}"); }
        }
        var vs = Compile("vs", "vs_5_0");
        _vs = dev.CreateVertexShader(vs.Span);
        _psColor = dev.CreatePixelShader(Compile("fs_color", "ps_5_0").Span);
        _psLine = dev.CreatePixelShader(Compile("fs_line", "ps_5_0").Span);
        _psPick = dev.CreatePixelShader(Compile("fs_pick", "ps_5_0").Span);
        // brief-em3d-29 — the field pass: FieldVertex (position, real part, imaginary part), LOC0..2.
        var vsf = Compile("vs_field", "vs_5_0");
        _vsField = dev.CreateVertexShader(vsf.Span);
        _psField = dev.CreatePixelShader(Compile("fs_field", "ps_5_0").Span);
        _layoutField = dev.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, DxFormat.R32G32B32_Float, 12, 0),
            new InputElementDescription("LOC", 2, DxFormat.R32G32B32_Float, 24, 0),
        ], vsf.Span);
        _layout = dev.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, DxFormat.R32_UInt, 12, 0),
            new InputElementDescription("LOC", 2, DxFormat.R8G8B8A8_UNorm, 16, 0),
        ], vs.Span);

        _blendOff = dev.CreateBlendState(BlendDescription.Opaque);
        var on = BlendDescription.Opaque;
        ref var rt = ref on.RenderTarget[0];
        rt.BlendEnable = true;
        rt.SourceBlend = Blend.SourceAlpha; rt.DestinationBlend = Blend.InverseSourceAlpha; rt.BlendOperation = BlendOperation.Add;
        // the shared image's alpha stays 1 (R-em3d28-1d): ONE, ONE_MINUS_SRC_ALPHA on alpha
        rt.SourceBlendAlpha = Blend.One; rt.DestinationBlendAlpha = Blend.InverseSourceAlpha; rt.BlendOperationAlpha = BlendOperation.Add;
        rt.RenderTargetWriteMask = ColorWriteEnable.All;
        _blendOn = dev.CreateBlendState(on);
        _dsWrite = dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        _dsNoWrite = dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        // Cull none; front faces counter-clockwise, as the tessellation winds them seen from outside —
        // the shader's SV_IsFrontFace draws the clip plane's caps from the back faces.
        var rs = RasterizerDescription.CullNone;
        rs.FrontCounterClockwise = true;
        _raster = dev.CreateRasterizerState(rs);
        _cb = dev.CreateBuffer(new BufferDescription(Scene3DFramePlan.UniformBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));

        _pickId = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32_UInt, 1, 1, 1, 1, BindFlags.RenderTarget));
        _pickPos = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32B32A32_Float, 1, 1, 1, 1, BindFlags.RenderTarget));
        _pickDepth = dev.CreateTexture2D(new Texture2DDescription(DxFormat.D32_Float, 1, 1, 1, 1, BindFlags.DepthStencil));
        _pickIdRtv = dev.CreateRenderTargetView(_pickId);
        _pickPosRtv = dev.CreateRenderTargetView(_pickPos);
        _pickDsv = dev.CreateDepthStencilView(_pickDepth);
        _pickTargets[0] = _pickIdRtv; _pickTargets[1] = _pickPosRtv;
        for (int i = 0; i < Ring; i++)
        {
            _stagingId[i] = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32_UInt, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _stagingPos[i] = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32B32A32_Float, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _query[i] = dev.CreateQuery(QueryType.Event);
        }
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        _vb?.Dispose(); _ib?.Dispose(); _lines?.Dispose();
        fixed (Scene3DVertex* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length * Scene3DVertex.Stride, BindFlags.VertexBuffer);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4, BindFlags.IndexBuffer);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = NewBuffer(p, scene.LineVertices.Length * Scene3DVertex.Stride, BindFlags.VertexBuffer);
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        int i = slot - Scene3DBuffer.Overlay0;
        _overlays[i]?.Dispose();
        fixed (Scene3DVertex* p = lines) _overlays[i] = NewBuffer(p, lines.Length * Scene3DVertex.Stride, BindFlags.VertexBuffer);
    }

    public override void UploadField(FieldVertex[] vertices)
    {
        _field?.Dispose();
        fixed (FieldVertex* p = vertices) _field = NewBuffer(p, vertices.Length * FieldVertex.Stride, BindFlags.VertexBuffer);
        _fieldCount = vertices.Length;
    }

    private ID3D11Buffer? NewBuffer(void* data, int length, BindFlags bind)
    {
        if (length == 0) return null;
        Counters.CountUpload(length);
        return Device.CreateBuffer(new BufferDescription((uint)length, bind, ResourceUsage.Immutable), (nint)data)
               ?? throw new Viewer3DPresentFault($"D3D11 could not allocate a {length:N0}-byte buffer.");
    }

    // ── presentation ────────────────────────────────────────────────────────────────────────

    public override string? CheckInterop(ICompositionGpuInterop interop)
    {
        string images = string.Join(", ", interop.SupportedImageHandleTypes), sems = string.Join(", ", interop.SupportedSemaphoreTypes);
        _handleType = interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle)
            ? KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle : "";
        if (_handleType == "")
            return $"the compositor cannot import a D3D11 texture by shared handle (it offered images [{images}], semaphores [{sems}])";
        var caps = interop.GetSynchronizationCapabilities(_handleType);
        if (!caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
            return $"the compositor's D3D11 synchronisation [{caps}] offers no keyed mutex";
        var luid = interop.DeviceLuid;
        if (_device is null) CreateDevice(luid);
        else if (luid is { Length: 8 } && (_adapterLuid is null || !luid.AsSpan().SequenceEqual(_adapterLuid)))
            return $"the compositor moved to adapter {Convert.ToHexString(luid)} after the 3D view's device was made on another";
        return null;
    }

    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count)
    {
        ReleaseImages();
        var dev = Device;
        _images = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var im = new Image
            {
                Texture = dev.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)width, (uint)height, 1, 1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
                    ResourceOptionFlags.SharedKeyedMutex)),
            };
            im.View = dev.CreateRenderTargetView(im.Texture);
            im.Mutex = im.Texture.QueryInterface<IDXGIKeyedMutex>();
            nint handle;
            using (var res = im.Texture.QueryInterface<IDXGIResource>()) handle = res.SharedHandle;
            if (handle == 0) throw new Viewer3DPresentFault("IDXGIResource::GetSharedHandle returned no handle for the 3D view's image.");
            im.Imported = interop.ImportImage(new PlatformHandle(handle, _handleType),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = width, Height = height, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm, TopLeftOrigin = true,
                });
            _images[i] = im;
        }
    }

    public override void ReleaseImages()
    {
        foreach (var im in _images)
        {
            if (im.Owned) { im.Mutex.ReleaseSync(KeyRenderer); im.Owned = false; }
            Dispose(im.Imported);
            im.Mutex.Dispose(); im.View.Dispose(); im.Texture.Dispose();
        }
        _images = [];
    }

    private static void Dispose(object? o)
    {
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
        else if (o is IDisposable d) d.Dispose();
    }

    public override bool WaitReusable(int image, int timeoutMs)
    {
        var im = _images[image];
        if (im.Owned) return true;
        int hr = ((delegate* unmanaged[Stdcall]<nint, ulong, int, int>)(*(nint**)im.Mutex.NativePointer)[8])(
            im.Mutex.NativePointer, KeyRenderer, timeoutMs);
        if (hr == WaitTimeout) return false;
        if (hr == WaitAbandoned || hr < 0)
            throw new Viewer3DPresentFault($"IDXGIKeyedMutex::AcquireSync on the 3D view's image {image} failed (0x{hr:X8}).");
        im.Owned = true;
        return true;
    }

    public override void Present(CompositionDrawingSurface surface, int image, ulong frame)
        => _ = surface.UpdateWithKeyedMutexAsync(_images[image].Imported, (uint)KeyCompositor, (uint)KeyRenderer);

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        var im = _images[image];
        if (!im.Owned)
            throw new Viewer3DPresentFault($"The compositor did not hand the 3D view's image {image} back (keyed mutex key {KeyRenderer} not acquired).");
        RenderInto(im.View, plan);
        // hand the image to the compositor's key; Present tells the compositor to take it
        im.Mutex.ReleaseSync(KeyCompositor);
        im.Owned = false;
    }

    /// <summary>brief-em3d-29 R-em3d29-5 — the plan drawn into a texture of its own and copied back
    /// through a staging texture: the same shaders and buffers as the view, the swapchain untouched.</summary>
    public override byte[] RenderPixels(Scene3DFramePlan plan)
    {
        var dev = Device;
        uint w = (uint)plan.Width, h = (uint)plan.Height;
        using var tex = dev.CreateTexture2D(new Texture2DDescription(ColorFormat, w, h, 1, 1, BindFlags.RenderTarget));
        using var view = dev.CreateRenderTargetView(tex);
        using var staging = dev.CreateTexture2D(new Texture2DDescription(ColorFormat, w, h, 1, 1, BindFlags.None,
            ResourceUsage.Staging, CpuAccessFlags.Read));
        RenderInto(view, plan);
        var ctx = Ctx;
        ctx.CopyResource(staging, tex);
        var m = ctx.Map(staging, 0, MapMode.Read, DxMapFlags.None);
        try
        {
            var px = new byte[w * h * 4];
            for (uint y = 0; y < h; y++)
                new ReadOnlySpan<byte>((byte*)m.DataPointer + y * m.RowPitch, (int)w * 4).CopyTo(px.AsSpan((int)(y * w * 4)));
            return px;
        }
        finally { ctx.Unmap(staging, 0); }
    }

    private void RenderInto(ID3D11RenderTargetView target, Scene3DFramePlan plan)
    {
        var ctx = Ctx;
        int draws = 0;
        CollectPicks(ctx);
        EnsureDepth(plan.Width, plan.Height);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.RSSetState(_raster);

        int slot = _rbHead % Ring;
        if (plan.Pick && plan.PickDrawCount > 0 && _vb is not null && _ib is not null && !_inFlight[slot])
        {
            ctx.UpdateSubresource(plan.PickUniforms.AsSpan(), _cb);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            ctx.OMSetRenderTargets(_pickTargets, _pickDsv);
            ctx.RSSetViewport(0, 0, 1, 1);
            ctx.ClearRenderTargetView(_pickIdRtv, new Color4(0, 0, 0, 0));
            ctx.ClearRenderTargetView(_pickPosRtv, new Color4(0, 0, 0, 0));
            ctx.ClearDepthStencilView(_pickDsv, DepthStencilClearFlags.Depth, 1f, 0);
            ctx.PSSetShader(_psPick);
            ctx.OMSetBlendState(_blendOff);
            ctx.OMSetDepthStencilState(_dsWrite);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetVertexBuffer(0, _vb, Scene3DVertex.Stride);
            ctx.IASetIndexBuffer(_ib, DxFormat.R32_UInt, 0);
            for (int i = 0; i < plan.PickDrawCount; i++)
            {
                ref var d = ref plan.PickDraws[i];
                ctx.DrawIndexed((uint)d.Count, (uint)d.First, 0); draws++;
            }
            ctx.CopyResource(_stagingId[slot], _pickId);
            ctx.CopyResource(_stagingPos[slot], _pickPos);
            ctx.End(_query[slot]);
            _inFlight[slot] = true;
            _rbFrame[slot] = Counters.FrameIndex;
            _rbHead++;
        }

        ctx.UpdateSubresource(plan.Uniforms.AsSpan(), _cb);
        Counters.CountUniform(Scene3DFramePlan.UniformBytes);
        ctx.OMSetRenderTargets(target, _dsv);
        ctx.RSSetViewport(0, 0, plan.Width, plan.Height);
        var (r, g, b) = plan.Clear;
        ctx.ClearRenderTargetView(target, new Color4(r, g, b, 1f));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);
        Scene3DBuffer bound = (Scene3DBuffer)(-1);
        Scene3DPipeline state = (Scene3DPipeline)(-1);
        for (int i = 0; i < plan.DrawCount; i++)
        {
            ref var d = ref plan.Draws[i];
            var buf = d.Buffer switch
            {
                Scene3DBuffer.Scene => _vb, Scene3DBuffer.SceneLines => _lines, Scene3DBuffer.Field => _field,
                Scene3DBuffer.Overlay0 => _overlays[0], Scene3DBuffer.Overlay1 => _overlays[1], _ => _overlays[2],
            };
            bool lines = d.Pipeline == Scene3DPipeline.Lines, field = d.Pipeline == Scene3DPipeline.Field;
            if (buf is null || (!lines && !field && _ib is null)) continue;
            if (field && d.First + d.Count > _fieldCount) continue;
            if (d.Pipeline != state)
            {
                bool wasField = state == Scene3DPipeline.Field;
                state = d.Pipeline;
                if (field != wasField)
                {
                    ctx.IASetInputLayout(field ? _layoutField : _layout);
                    ctx.VSSetShader(field ? _vsField : _vs);
                    bound = (Scene3DBuffer)(-1);
                }
                ctx.PSSetShader(field ? _psField : lines ? _psLine : _psColor);
                ctx.OMSetBlendState(state == Scene3DPipeline.Translucent ? _blendOn : _blendOff);
                ctx.OMSetDepthStencilState(state == Scene3DPipeline.Translucent ? _dsNoWrite : _dsWrite);
                ctx.IASetPrimitiveTopology(lines ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList);
            }
            if (d.Buffer != bound)
            {
                bound = d.Buffer;
                ctx.IASetVertexBuffer(0, buf, field ? (uint)FieldVertex.Stride : Scene3DVertex.Stride);
                if (!lines && !field) ctx.IASetIndexBuffer(_ib!, DxFormat.R32_UInt, 0);
            }
            if (lines || field) ctx.Draw((uint)d.Count, (uint)d.First);
            else ctx.DrawIndexed((uint)d.Count, (uint)d.First, 0);
            draws++;
        }
        ctx.Flush();
        DrawCallsLastFrame = draws;
    }

    private void EnsureDepth(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _depthW && h == _depthH && _dsv is not null) return;
        _dsv?.Dispose(); _depth?.Dispose();
        _depth = Device.CreateTexture2D(new Texture2DDescription(DxFormat.D32_Float, (uint)w, (uint)h, 1, 1, BindFlags.DepthStencil));
        _dsv = Device.CreateDepthStencilView(_depth);
        _depthW = w; _depthH = h;
    }

    /// <summary>Non-blocking: a slot's texels are read only once its event query reports done.</summary>
    private void CollectPicks(ID3D11DeviceContext ctx)
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = (_rbHead + k) % Ring;
            if (!_inFlight[slot]) continue;
            if (!ctx.GetData(_query[slot], AsyncGetDataFlags.DoNotFlush, out int done) || done == 0) continue;
            var m = ctx.Map(_stagingId[slot], 0, MapMode.Read, DxMapFlags.None);
            PickedId = *(uint*)m.DataPointer;
            ctx.Unmap(_stagingId[slot], 0);
            m = ctx.Map(_stagingPos[slot], 0, MapMode.Read, DxMapFlags.None);
            float* w = (float*)m.DataPointer;
            PickedPoint = new Vector3(w[0], w[1], w[2]);
            PickedSomething = w[3] > 0.5f;
            ctx.Unmap(_stagingPos[slot], 0);
            _inFlight[slot] = false;
            Counters.PickResolved(_rbFrame[slot]);
        }
    }

    public override void Dispose()
    {
        ReleaseImages();
        _vb?.Dispose(); _ib?.Dispose(); _lines?.Dispose(); _field?.Dispose();
        foreach (var o in _overlays) o?.Dispose();
        if (_device is null) return;
        foreach (var s in _stagingId) s?.Dispose();
        foreach (var s in _stagingPos) s?.Dispose();
        foreach (var q in _query) q?.Dispose();
        _dsv?.Dispose(); _depth?.Dispose();
        _pickIdRtv?.Dispose(); _pickPosRtv?.Dispose(); _pickDsv?.Dispose();
        _pickId?.Dispose(); _pickPos?.Dispose(); _pickDepth?.Dispose();
        _cb?.Dispose(); _layout?.Dispose(); _vs?.Dispose(); _layoutField?.Dispose(); _vsField?.Dispose();
        _psColor?.Dispose(); _psLine?.Dispose(); _psPick?.Dispose(); _psField?.Dispose();
        _blendOff?.Dispose(); _blendOn?.Dispose(); _dsWrite?.Dispose(); _dsNoWrite?.Dispose(); _raster?.Dispose();
        _ctx?.Dispose(); _device.Dispose();
    }
}
