using System.Runtime.InteropServices;
using Viewer3dSpike.Scene;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Viewer3dSpike.Render.Direct3D;

/// <summary>
/// Route A on Windows (brief em3d-28 step 0): native Direct3D 11. The shader is the ONE WGSL source,
/// cross-compiled offline by tools/ShaderGen to <c>shaders/scene.hlsl</c> and compiled here at start-up
/// by the OS's own d3dcompiler_47 (vs_5_0 / ps_5_0). Bindings are Vortice's managed wrappers (MIT); the
/// DLLs they call — d3d11, dxgi, d3dcompiler_47 — are the operating system's.
///
/// Same scene and draw structure as the other routes: one draw for every opaque object, one per
/// translucent object sorted back to front per object, and a 1×1 R32_UINT ID pass copied to one of three
/// STAGING textures and read back when its event query says the GPU is done — never waited on.
/// The 608-byte uniform block (camera, hover, colour table) goes in by UpdateSubresource on a DEFAULT
/// constant buffer: counted as uniform bytes, never geometry.
///
/// Renders into an R8G8B8A8 texture the host hands it: a keyed-mutex shared texture in the Avalonia pane
/// (ANGLE imports that format only), a plain one in the harness.
/// </summary>
public sealed unsafe class D3D11Renderer : IDisposable
{
    const int Ring = 3;
    public const int UniformBytes = 96 + 512;
    public const Format ColorFormat = Format.R8G8B8A8_UNorm;

    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    readonly FrameCounters _counters;
    public string Info { get; }
    ID3D11Buffer _vb = null!, _ib = null!, _cb = null!;
    ID3D11InputLayout _layout = null!;
    ID3D11VertexShader _vs = null!;
    ID3D11PixelShader _psColor = null!, _psPick = null!;
    ID3D11BlendState _blendOff = null!, _blendOn = null!;
    ID3D11DepthStencilState _dsWrite = null!, _dsNoWrite = null!;
    ID3D11RasterizerState _raster = null!;
    ID3D11Texture2D _pickTex = null!, _pickDepth = null!;
    ID3D11RenderTargetView _pickRtv = null!;
    ID3D11DepthStencilView _pickDsv = null!;
    ID3D11Texture2D? _depth;
    ID3D11DepthStencilView? _dsv;
    int _depthW, _depthH;
    readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[Ring];
    readonly ID3D11Query[] _query = new ID3D11Query[Ring];
    readonly bool[] _inFlight = new bool[Ring];
    readonly long[] _rbFrame = new long[Ring];
    int _rbHead;
    SceneModel _scene = null!;
    TranslucentSorter _sorter = null!;
    readonly float[] _ublock = GC.AllocateArray<float>(UniformBytes / 4, pinned: true);
    public uint HoveredId { get; private set; }
    public int DrawCallsLastFrame { get; private set; }

    /// <param name="adapterLuid">The compositor's adapter (ICompositionGpuInterop.DeviceLuid, 8 bytes) —
    /// a shared texture only opens on the device that made it if both are on ONE adapter. Null: the
    /// default adapter.</param>
    public D3D11Renderer(FrameCounters counters, byte[]? adapterLuid = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("route A's D3D11 half is Windows only");
        _counters = counters;
        IDXGIAdapter1? chosen = null;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (adapterLuid is { Length: 8 })
        {
            uint lo = BitConverter.ToUInt32(adapterLuid, 0);
            int hi = BitConverter.ToInt32(adapterLuid, 4);
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
            {
                var l = a.Description1.Luid;
                if (l.LowPart == lo && l.HighPart == hi) { chosen = a; break; }
                a.Dispose();
            }
            if (chosen == null) throw new InvalidOperationException($"no DXGI adapter has the compositor's LUID {Convert.ToHexString(adapterLuid)}");
        }
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var r = Vortice.Direct3D11.D3D11.D3D11CreateDevice(chosen, chosen == null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext context);
        if (r.Failure || device == null || context == null) throw new InvalidOperationException($"D3D11CreateDevice failed: {r}");
        Device = device!;
        Context = context!;
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        Info = $"D3D11 ({Device.FeatureLevel}): {adapter.Description.Description}" + (chosen != null ? " — the compositor's adapter (LUID match)" : "");
        chosen?.Dispose();
    }

    public void Init(SceneModel scene, string hlslSource)
    {
        _scene = scene;
        _sorter = new TranslucentSorter(scene.Translucent);
        Array.Copy(scene.Colors, 0, _ublock, 24, Math.Min(scene.Colors.Length, 128));

        var vsCode = Compiler.Compile(hlslSource, "vs", "scene.hlsl", "vs_5_0");
        var psColorCode = Compiler.Compile(hlslSource, "fs_color", "scene.hlsl", "ps_5_0");
        var psPickCode = Compiler.Compile(hlslSource, "fs_pick", "scene.hlsl", "ps_5_0");
        _vs = Device.CreateVertexShader(vsCode.Span);
        _psColor = Device.CreatePixelShader(psColorCode.Span);
        _psPick = Device.CreatePixelShader(psPickCode.Span);
        // naga names every user varying LOC<n>
        _layout = Device.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, Format.R32_UInt, 12, 0),
        ], vsCode.Span);

        var off = BlendDescription.Opaque;
        _blendOff = Device.CreateBlendState(off);
        var on = BlendDescription.Opaque;
        ref var rt = ref on.RenderTarget[0];
        rt.BlendEnable = true;
        rt.SourceBlend = Blend.SourceAlpha; rt.DestinationBlend = Blend.InverseSourceAlpha; rt.BlendOperation = BlendOperation.Add;
        // the shared image's alpha stays 1 (findings §5.1): ONE, ONE_MINUS_SRC_ALPHA on alpha
        rt.SourceBlendAlpha = Blend.One; rt.DestinationBlendAlpha = Blend.InverseSourceAlpha; rt.BlendOperationAlpha = BlendOperation.Add;
        rt.RenderTargetWriteMask = ColorWriteEnable.All;
        _blendOn = Device.CreateBlendState(on);
        _dsWrite = Device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        _dsNoWrite = Device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        _raster = Device.CreateRasterizerState(RasterizerDescription.CullNone);

        fixed (byte* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length, BindFlags.VertexBuffer);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4, BindFlags.IndexBuffer);
        _cb = Device.CreateBuffer(new BufferDescription(UniformBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));

        _pickTex = Device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, 1, 1, 1, 1, BindFlags.RenderTarget));
        _pickRtv = Device.CreateRenderTargetView(_pickTex);
        _pickDepth = Device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, 1, 1, 1, 1, BindFlags.DepthStencil));
        _pickDsv = Device.CreateDepthStencilView(_pickDepth);
        for (int i = 0; i < Ring; i++)
        {
            _staging[i] = Device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _query[i] = Device.CreateQuery(QueryType.Event);
        }
    }

    ID3D11Buffer NewBuffer(void* data, int length, BindFlags bind)
    {
        _counters.CountUpload(length);
        return Device.CreateBuffer(new BufferDescription((uint)length, bind, ResourceUsage.Immutable), (nint)data);
    }

    /// <summary>A render-target view of a texture the host owns (the harness's, or a shared one).</summary>
    public ID3D11RenderTargetView TargetView(ID3D11Texture2D tex) => Device.CreateRenderTargetView(tex);

    public ID3D11Texture2D NewTarget(int w, int h, bool shared) =>
        Device.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)w, (uint)h, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
            shared ? ResourceOptionFlags.SharedKeyedMutex : ResourceOptionFlags.None));

    /// <summary>Encode one frame into <paramref name="target"/> (<c>input.Width</c> × <c>input.Height</c>).
    /// The caller owns the keyed mutex around it; this only draws.</summary>
    public void Render(ID3D11RenderTargetView target, in FrameInput input)
    {
        int draws = 0;
        CollectPicks();
        EnsureDepth(input.Width, input.Height);
        var ctx = Context;
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vb, SceneModel.VertexStride);
        ctx.IASetIndexBuffer(_ib, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.RSSetState(_raster);

        float* u = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _ublock[0]);
        int slot = _rbHead % Ring;
        if (input.PickX >= 0 && input.PickY >= 0 && !_inFlight[slot])
        {
            FillUniforms(u, input, pick: true);
            ctx.UpdateSubresource(new Span<float>(u, UniformBytes / 4), _cb);
            _counters.CountUniform(UniformBytes);
            ctx.OMSetRenderTargets(_pickRtv, _pickDsv);
            ctx.RSSetViewport(0, 0, 1, 1);
            ctx.ClearRenderTargetView(_pickRtv, new Color4(0, 0, 0, 0));
            ctx.ClearDepthStencilView(_pickDsv, DepthStencilClearFlags.Depth, 1f, 0);
            ctx.PSSetShader(_psPick);
            ctx.OMSetBlendState(_blendOff);
            ctx.OMSetDepthStencilState(_dsWrite);
            ctx.DrawIndexed((uint)_scene.OpaqueIndexCount, 0, 0); draws++;
            ctx.CopyResource(_staging[slot], _pickTex);
            ctx.End(_query[slot]);
            _inFlight[slot] = true;
            _rbFrame[slot] = _counters.FrameIndex;
            _rbHead++;
        }

        FillUniforms(u, input, pick: false);
        ctx.UpdateSubresource(new Span<float>(u, UniformBytes / 4), _cb);
        _counters.CountUniform(UniformBytes);
        ctx.OMSetRenderTargets(target, _dsv);
        ctx.RSSetViewport(0, 0, input.Width, input.Height);
        ctx.ClearRenderTargetView(target, new Color4(0.11f, 0.12f, 0.14f, 1f));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.PSSetShader(_psColor);
        ctx.OMSetBlendState(_blendOff);
        ctx.OMSetDepthStencilState(_dsWrite);
        ctx.DrawIndexed((uint)_scene.OpaqueIndexCount, 0, 0); draws++;
        _sorter.Sort(input.Camera);
        ctx.OMSetBlendState(_blendOn);
        ctx.OMSetDepthStencilState(_dsNoWrite);
        var tr = _scene.Translucent;
        foreach (int i in _sorter.Order) { ctx.DrawIndexed((uint)tr[i].IndexCount, (uint)tr[i].FirstIndex, 0); draws++; }
        ctx.Flush();
        DrawCallsLastFrame = draws;
    }

    void FillUniforms(float* u, in FrameInput input, bool pick)
    {
        if (pick) input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true, input.PickX, input.PickY);
        else input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true);
        var eye = input.Camera.Eye;
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        ((uint*)u)[20] = HoveredId; ((uint*)u)[21] = input.Selected; ((uint*)u)[22] = (uint)_scene.ObjectsPerReplica; ((uint*)u)[23] = 0;
    }

    void EnsureDepth(int w, int h)
    {
        if (w == _depthW && h == _depthH && _dsv != null) return;
        _dsv?.Dispose(); _depth?.Dispose();
        _depth = Device.CreateTexture2D(new Texture2DDescription(Format.D32_Float, (uint)w, (uint)h, 1, 1, BindFlags.DepthStencil));
        _dsv = Device.CreateDepthStencilView(_depth);
        _depthW = w; _depthH = h;
    }

    /// <summary>Non-blocking: a slot's pixel is read only once its event query reports done.</summary>
    void CollectPicks()
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = (_rbHead + k) % Ring;
            if (!_inFlight[slot]) continue;
            if (!Context.GetData(_query[slot], AsyncGetDataFlags.DoNotFlush, out int done) || done == 0) continue;
            var m = Context.Map(_staging[slot], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            HoveredId = *(uint*)m.DataPointer;
            Context.Unmap(_staging[slot], 0);
            _inFlight[slot] = false;
            _counters.PickResolved(_rbFrame[slot]);
        }
    }

    public bool PickPending { get { foreach (var b in _inFlight) if (b) return true; return false; } }

    /// <summary>Harness only: blocking readback of an R8G8B8A8 texture as RGBA rows.</summary>
    public byte[] ReadRgba(ID3D11Texture2D tex, int w, int h)
    {
        using var st = Device.CreateTexture2D(new Texture2DDescription(ColorFormat, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        Context.CopyResource(st, tex);
        var m = Context.Map(st, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Marshal.Copy(m.DataPointer + (nint)(y * (long)m.RowPitch), px, y * w * 4, w * 4);
        Context.Unmap(st, 0);
        return px;
    }

    /// <summary>IDXGIKeyedMutex::AcquireSync through its vtable, so a WAIT_TIMEOUT (0x102, a SUCCESS
    /// code the wrapper does not throw on) is seen and reported instead of drawing unowned.</summary>
    public static int AcquireSync(IDXGIKeyedMutex m, ulong key, int ms) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, int, int>)(*(nint**)m.NativePointer)[8])(m.NativePointer, key, ms);

    public void Dispose()
    {
        foreach (var s in _staging) s?.Dispose();
        foreach (var q in _query) q?.Dispose();
        _dsv?.Dispose(); _depth?.Dispose();
        _pickRtv?.Dispose(); _pickDsv?.Dispose(); _pickTex?.Dispose(); _pickDepth?.Dispose();
        _vb?.Dispose(); _ib?.Dispose(); _cb?.Dispose(); _layout?.Dispose();
        _vs?.Dispose(); _psColor?.Dispose(); _psPick?.Dispose();
        _blendOff?.Dispose(); _blendOn?.Dispose(); _dsWrite?.Dispose(); _dsNoWrite?.Dispose(); _raster?.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}
