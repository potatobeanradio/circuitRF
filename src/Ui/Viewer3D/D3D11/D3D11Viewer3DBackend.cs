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
using CircuitRF.Render.Scene3D.Look;
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
    private ID3D11InputLayout _layout = null!, _layoutField = null!, _layoutImage = null!;
    private ID3D11VertexShader _vs = null!, _vsField = null!, _vsGrid = null!, _vsImage = null!;
    private ID3D11PixelShader _psColor = null!, _psLine = null!, _psPick = null!, _psField = null!, _psEdge = null!, _psTop = null!, _psGrid = null!, _psImage = null!;
    /// <summary>brief-em3d-101 — the image vertex stream, its one sampler (s0), and the textures' views (t0), held by identity
    /// (Scene3DTextureResidency): a scene rebuilt with the same files uploads no pixels.</summary>
    private ID3D11Buffer? _imageVb;
    private ID3D11SamplerState _imageSampler = null!;
    private readonly Scene3DTextureResidency<ID3D11ShaderResourceView?> _textures = new();
    private ID3D11Buffer? _field;
    private int _fieldCount;
    // brief-em3d-109 — the Lit field: its vertex stage and input layout (LOC0..2 from slot 0, LOC3 the normal from slot 1), its pixel
    // shader, and its normal stream
    private ID3D11VertexShader _vsFieldLit = null!;
    private ID3D11PixelShader _psFieldLit = null!;
    private ID3D11InputLayout _layoutFieldLit = null!;
    private ID3D11Buffer? _fieldNormals;
    private int _fieldNormalCount;
    private ID3D11BlendState _blendOff = null!, _blendOn = null!;
    private ID3D11DepthStencilState _dsWrite = null!, _dsNoWrite = null!, _dsOff = null!;
    /// <summary>3D editor round 3 / bugs round 9 — one rasterizer state per depth tie (Scene3DDepthTie − TieMin): cull none, and the
    /// tie's polygon offset (Scene3DFramePlan.DepthBias).</summary>
    private readonly ID3D11RasterizerState[] _raster = new ID3D11RasterizerState[Scene3DFramePlan.TieMax - Scene3DFramePlan.TieMin + 1];

    private ID3D11RasterizerState Raster(Scene3DDepthTie tie) => _raster[tie - Scene3DFramePlan.TieMin];
    private ID3D11Buffer _cb = null!;
    /// <summary>brief-em3d-46 — the per-draw transform (register b1), 80 bytes (brief-em3d-48 added the id offset), rewritten only when a draw's slot changes.</summary>
    private ID3D11Buffer _cbTransform = null!;
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
    /// <summary>brief-em3d-104 — the shade stream (Scene3DShadeVertex), held only while the realistic view is on. Input slot 1 of
    /// the realistic pipelines' layout (brief 106; scene.wgsl's header has the mapping).</summary>
    private ID3D11Buffer? _shade;
    /// <summary>brief-em3d-106 — the realistic pipelines' shaders and layout (the scene's vertex at slot 0, the shade stream at slot 1),
    /// the premultiplied blend PbrTranslucent takes, the appearance table (b2), and the environment's two RGBA16F textures (t1, t2,
    /// sampled through the image sampler at s1). The table and the textures are held only while the realistic view is on.</summary>
    private ID3D11VertexShader _vsPbr = null!;
    private ID3D11PixelShader _psPbr = null!, _psBackdrop = null!;
    private ID3D11InputLayout _layoutPbr = null!;
    private ID3D11BlendState _blendPremultiplied = null!;
    private ID3D11Buffer? _cbAppearances;
    private ID3D11ShaderResourceView? _envMap, _envBrdf;
    /// <summary>brief-em3d-107 — the shadow pass (vs_shadow / fs_depth, the shadow bias's rasterizer state), the occlusion's three passes
    /// (the prepass of the materials through vs and of the ground through vs_grid; the horizon pass and its blur), the ground, the glass
    /// pixel shader (no occlusion), the comparison sampler (s2); the shadow map (t3: R32 typeless — a D32 depth view to draw it, an R32
    /// float view to read it), the occlusion's targets (t4 the blurred, t5 the prepass's depths, t6 the raw) and 1 × 1 stand-ins. The
    /// textures are held only while the realistic view is on.</summary>
    private ID3D11VertexShader _vsShadow = null!;
    private ID3D11PixelShader _psDepth = null!, _psPrepass = null!, _psGroundPrepass = null!, _psAo = null!, _psAoBlur = null!, _psGround = null!;
    private ID3D11PixelShader _psPbrGlass = null!;
    private ID3D11RasterizerState _rasterShadow = null!;
    private ID3D11SamplerState _shadowSampler = null!;
    private DepthTarget? _shadowMap, _noShadowMap;
    private ColourTarget? _aoDepth, _aoRaw, _aoBlur, _noOcclusion;

    /// <summary>A depth texture both drawn into and read: R32 typeless, D32 to draw, R32 float to read.</summary>
    private sealed class DepthTarget(ID3D11Texture2D texture, ID3D11DepthStencilView dsv, ID3D11ShaderResourceView srv, int size) : IDisposable
    {
        public readonly ID3D11Texture2D Texture = texture;
        public readonly ID3D11DepthStencilView Dsv = dsv;
        public readonly ID3D11ShaderResourceView Srv = srv;
        public readonly int Size = size;
        public void Dispose() { Srv.Dispose(); Dsv.Dispose(); Texture.Dispose(); }
    }

    /// <summary>A colour texture both drawn into and read.</summary>
    private sealed class ColourTarget(ID3D11Texture2D texture, ID3D11RenderTargetView rtv, ID3D11ShaderResourceView srv, int w, int h) : IDisposable
    {
        public readonly ID3D11Texture2D Texture = texture;
        public readonly ID3D11RenderTargetView Rtv = rtv;
        public readonly ID3D11ShaderResourceView Srv = srv;
        public readonly int W = w, H = h;
        public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Texture.Dispose(); }
    }
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
        _psEdge = dev.CreatePixelShader(Compile("fs_edge", "ps_5_0").Span);
        _psTop = dev.CreatePixelShader(Compile("fs_top", "ps_5_0").Span);
        // brief-em3d-29 — the field pass: FieldVertex (position, real part, imaginary part), LOC0..2.
        var vsf = Compile("vs_field", "vs_5_0");
        _vsField = dev.CreateVertexShader(vsf.Span);
        _psField = dev.CreatePixelShader(Compile("fs_field", "ps_5_0").Span);
        var vsfl = Compile("vs_field_lit", "vs_5_0");
        _vsFieldLit = dev.CreateVertexShader(vsfl.Span);
        _psFieldLit = dev.CreatePixelShader(Compile("fs_field_lit", "ps_5_0").Span);
        _layoutFieldLit = dev.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, DxFormat.R32G32B32_Float, 12, 0),
            new InputElementDescription("LOC", 2, DxFormat.R32G32B32_Float, 24, 0),
            new InputElementDescription("LOC", 3, DxFormat.R32G32B32_Float, 0, ShadeSlot),
        ], vsfl.Span);
        // brief-em3d-45 — the drawing grid: SV_VertexID only, so no input layout at all.
        _vsGrid = dev.CreateVertexShader(Compile("vs_grid", "vs_5_0").Span);
        _psGrid = dev.CreatePixelShader(Compile("fs_grid", "ps_5_0").Span);
        // brief-em3d-101 — an image: Scene3DImageVertex (position, texture coordinate, id, face, colour), LOC0..4.
        var vsi = Compile("vs_image", "vs_5_0");
        _vsImage = dev.CreateVertexShader(vsi.Span);
        _psImage = dev.CreatePixelShader(Compile("fs_image", "ps_5_0").Span);
        _layoutImage = dev.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, DxFormat.R32G32_Float, 12, 0),
            new InputElementDescription("LOC", 2, DxFormat.R32_UInt, 20, 0),
            new InputElementDescription("LOC", 3, DxFormat.R32_UInt, 24, 0),
            new InputElementDescription("LOC", 4, DxFormat.R8G8B8A8_UNorm, 28, 0),
        ], vsi.Span);
        _imageSampler = dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp,
                                                                      TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        // brief-em3d-106 — the realistic view: LOC0..3 from the scene's vertex (slot 0), LOC4 the normal and LOC5 the slot from the
        // shade stream (slot 1); the backdrop is vs_grid's (no layout) with its own pixel shader.
        var vsp = Compile("vs_pbr", "vs_5_0");
        _vsPbr = dev.CreateVertexShader(vsp.Span);
        _psPbr = dev.CreatePixelShader(Compile("fs_pbr", "ps_5_0").Span);
        _psBackdrop = dev.CreatePixelShader(Compile("fs_backdrop", "ps_5_0").Span);
        // brief-em3d-107 — vs_shadow takes vs's input (the same LOC0..3, so _layout serves it); the full-screen passes are vs_grid's.
        _vsShadow = dev.CreateVertexShader(Compile("vs_shadow", "vs_5_0").Span);
        _psDepth = dev.CreatePixelShader(Compile("fs_depth", "ps_5_0").Span);
        _psPrepass = dev.CreatePixelShader(Compile("fs_prepass", "ps_5_0").Span);
        _psGroundPrepass = dev.CreatePixelShader(Compile("fs_ground_prepass", "ps_5_0").Span);
        _psAo = dev.CreatePixelShader(Compile("fs_ao", "ps_5_0").Span);
        _psAoBlur = dev.CreatePixelShader(Compile("fs_ao_blur", "ps_5_0").Span);
        _psGround = dev.CreatePixelShader(Compile("fs_ground", "ps_5_0").Span);
        _psPbrGlass = dev.CreatePixelShader(Compile("fs_pbr_glass", "ps_5_0").Span);
        _shadowSampler = dev.CreateSamplerState(new SamplerDescription(Filter.ComparisonMinMagLinearMipPoint, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.LessEqual, 0, float.MaxValue));
        _layoutPbr = dev.CreateInputLayout(
        [
            new InputElementDescription("LOC", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("LOC", 1, DxFormat.R32_UInt, 12, 0),
            new InputElementDescription("LOC", 2, DxFormat.R8G8B8A8_UNorm, 16, 0),
            new InputElementDescription("LOC", 3, DxFormat.R32_UInt, 20, 0),
            new InputElementDescription("LOC", 4, DxFormat.R32G32B32_Float, 0, ShadeSlot),
            new InputElementDescription("LOC", 5, DxFormat.R32_UInt, 12, ShadeSlot),
        ], vsp.Span);
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
            new InputElementDescription("LOC", 3, DxFormat.R32_UInt, 20, 0),
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
        // PbrTranslucent's colour is premultiplied: ONE on RGB, so a reflection on glass adds on top of what shows through.
        on.RenderTarget[0].SourceBlend = Blend.One;
        _blendPremultiplied = dev.CreateBlendState(on);
        _dsWrite = dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        _dsNoWrite = dev.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        // brief-em3d-43 — the selection's edges and its face on top: no depth test at all.
        _dsOff = dev.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));
        // Cull none; front faces counter-clockwise, as the tessellation winds them seen from outside —
        // the shader's SV_IsFrontFace draws the clip plane's caps from the back faces.
        var rs = RasterizerDescription.CullNone;
        rs.FrontCounterClockwise = true;
        // 3D editor round 3 / bugs round 9 — each tie's polygon offset, so at a coincident face a metal wins over a dielectric,
        // a via over a metal and a port over a via.
        for (int t = 0; t < _raster.Length; t++)
        {
            var (constant, slope, clamp) = Scene3DFramePlan.DepthBias((Scene3DDepthTie)(t + (int)Scene3DFramePlan.TieMin));
            rs.DepthBias = (int)constant;
            rs.SlopeScaledDepthBias = slope;
            rs.DepthBiasClamp = clamp;
            _raster[t] = dev.CreateRasterizerState(rs);
        }
        {
            var (constant, slope, clamp) = Scene3DFramePlan.ShadowBias;
            rs.DepthBias = (int)constant;
            rs.SlopeScaledDepthBias = slope;
            rs.DepthBiasClamp = clamp;
            _rasterShadow = dev.CreateRasterizerState(rs);
        }
        // the stand-ins hold what "none" means — a map with nothing in it (depth 1: lit) and no occlusion (1) — rather than whatever
        // memory they were given, in case one is ever read with its uniform switched on
        _noShadowMap = NewDepthTarget(1);
        _noOcclusion = NewColourTarget(1, 1, DxFormat.R8_UNorm);
        Ctx.ClearDepthStencilView(_noShadowMap.Dsv, DepthStencilClearFlags.Depth, 1f, 0);
        Ctx.ClearRenderTargetView(_noOcclusion.Rtv, new Color4(1f, 1f, 1f, 1f));
        _cb = dev.CreateBuffer(new BufferDescription(Scene3DFramePlan.UniformBytes, BindFlags.ConstantBuffer, ResourceUsage.Default));
        _cbTransform = dev.CreateBuffer(new BufferDescription(Scene3DFramePlan.TransformBytesPerDraw, BindFlags.ConstantBuffer, ResourceUsage.Default));

        _pickId = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32_UInt, 1, 1, 1, 1, BindFlags.RenderTarget));
        _pickPos = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32B32A32_Float, 1, 1, 1, 1, BindFlags.RenderTarget));
        _pickDepth = dev.CreateTexture2D(new Texture2DDescription(DxFormat.D32_Float, 1, 1, 1, 1, BindFlags.DepthStencil));
        _pickIdRtv = dev.CreateRenderTargetView(_pickId);
        _pickPosRtv = dev.CreateRenderTargetView(_pickPos);
        _pickDsv = dev.CreateDepthStencilView(_pickDepth);
        _pickTargets[0] = _pickIdRtv; _pickTargets[1] = _pickPosRtv;
        for (int i = 0; i < Ring; i++)
        {
            _stagingId[i] = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32_UInt, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _stagingPos[i] = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32G32B32A32_Float, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _query[i] = dev.CreateQuery(QueryType.Event);
        }
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        _vb?.Dispose(); _ib?.Dispose(); _lines?.Dispose();
        // DEFAULT, not immutable: brief 43's PatchScene rewrites ranges of these three in place.
        fixed (Scene3DVertex* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length * Scene3DVertex.Stride, BindFlags.VertexBuffer, ResourceUsage.Default);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4, BindFlags.IndexBuffer, ResourceUsage.Default);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = NewBuffer(p, scene.LineVertices.Length * Scene3DVertex.Stride, BindFlags.VertexBuffer, ResourceUsage.Default);
        _imageVb?.Dispose();
        fixed (Scene3DImageVertex* p = scene.ImageVertices) _imageVb = NewBuffer(p, scene.ImageVertices.Length * Scene3DImageVertex.Stride, BindFlags.VertexBuffer);
        _textures.Sync(scene, UploadTexture, v => v?.Dispose());
    }

    /// <summary>brief-em3d-101 R-em3d101-4d — an immutable RGBA8 UNORM texture (never _SRGB) created with every CPU-built mip
    /// level as its initial data, and the view the pixel shader reads it through. Once per <see cref="Scene3DTexture"/>.</summary>
    private ID3D11ShaderResourceView? UploadTexture(Scene3DTexture t)
        => NewSampledTexture(DxFormat.R8G8B8A8_UNorm, 4, [.. t.Levels().Select(l => (l.Width, l.Height, l.Rgba))]);

    /// <summary>brief-em3d-106 R-em3d106-4c — brief 101's upload with the format an argument: RGBA8 for an image, R16G16B16A16_FLOAT for
    /// the environment (sampleable with linear filtering at feature level 10 and up, so no RGBE fallback).</summary>
    private ID3D11ShaderResourceView? NewSampledTexture(DxFormat format, int texelBytes, IReadOnlyList<(int Width, int Height, byte[] Bytes)> levels)
    {
        var pins = new System.Runtime.InteropServices.GCHandle[levels.Count];
        var data = new SubresourceData[levels.Count];
        try
        {
            for (int k = 0; k < levels.Count; k++)
            {
                pins[k] = System.Runtime.InteropServices.GCHandle.Alloc(levels[k].Bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
                data[k] = new SubresourceData(pins[k].AddrOfPinnedObject(), (uint)(levels[k].Width * texelBytes), (uint)levels[k].Bytes.Length);
                Counters.CountUpload(levels[k].Bytes.Length);
            }
            using var tex = Device.CreateTexture2D(new Texture2DDescription(format, (uint)levels[0].Width, (uint)levels[0].Height,
                1, (uint)levels.Count, BindFlags.ShaderResource, ResourceUsage.Immutable), data);
            return Device.CreateShaderResourceView(tex);
        }
        finally
        {
            foreach (var h in pins) if (h.IsAllocated) h.Free();
        }
    }

    // ── the realistic view's lighting (brief-em3d-106) ─────────────────────────────────────────────────────────────────

    /// <summary>The shade stream's input slot (scene.wgsl's header).</summary>
    private const uint ShadeSlot = 1;

    public override void UploadAppearances(float[] table)
    {
        _cbAppearances ??= Device.CreateBuffer(new BufferDescription((uint)(table.Length * 4), BindFlags.ConstantBuffer, ResourceUsage.Default));
        Ctx.UpdateSubresource(table.AsSpan(), _cbAppearances);
        Counters.CountUpload(table.Length * 4L);
    }

    public override void UploadEnvironment(PrefilteredEnvironment environment)
    {
        _envMap?.Dispose(); _envBrdf?.Dispose();
        _envMap = NewSampledTexture(DxFormat.R16G16B16A16_Float, 8, [.. environment.Levels.Select(l => (l.Width, l.Height, l.Bytes.ToArray()))]);
        _envBrdf = NewSampledTexture(DxFormat.R16G16B16A16_Float, 8,
            [(Pbr.BrdfSize, Pbr.BrdfSize,
              System.Runtime.InteropServices.MemoryMarshal.AsBytes(environment.BrdfTable.AsSpan()).ToArray())]);
    }

    public override void ReleaseEnvironment()
    {
        _envMap?.Dispose(); _envBrdf?.Dispose(); _cbAppearances?.Dispose();
        (_envMap, _envBrdf, _cbAppearances) = (null, null, null);
        // brief-em3d-107 — the shadow map and the occlusion's targets go with it
        _shadowMap?.Dispose(); _aoDepth?.Dispose(); _aoRaw?.Dispose(); _aoBlur?.Dispose();
        (_shadowMap, _aoDepth, _aoRaw, _aoBlur) = (null, null, null, null);
    }

    // ── brief-em3d-107: the shadow map, the occlusion, the ground ──────────────────────────────────────────────────────────

    private DepthTarget NewDepthTarget(int size)
    {
        var dev = Device;
        var tex = dev.CreateTexture2D(new Texture2DDescription(DxFormat.R32_Typeless, (uint)size, (uint)size, 1, 1,
            BindFlags.DepthStencil | BindFlags.ShaderResource));
        var dsv = dev.CreateDepthStencilView(tex, new DepthStencilViewDescription(tex, DepthStencilViewDimension.Texture2D, DxFormat.D32_Float));
        var srv = dev.CreateShaderResourceView(tex, new ShaderResourceViewDescription(tex, Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
            DxFormat.R32_Float, 0, 1));
        return new DepthTarget(tex, dsv, srv, size);
    }

    private ColourTarget NewColourTarget(int w, int h, DxFormat format)
    {
        var dev = Device;
        var tex = dev.CreateTexture2D(new Texture2DDescription(format, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        return new ColourTarget(tex, dev.CreateRenderTargetView(tex), dev.CreateShaderResourceView(tex), w, h);
    }

    /// <summary>t3..t6 left empty: a texture about to be drawn into is never still bound to be read (D3D11 would unbind it itself, with
    /// a warning, and the pass reading it next would read nothing).</summary>
    private static void UnbindLighting(ID3D11DeviceContext ctx) => ctx.PSUnsetShaderResources(3, 4);

    /// <summary>R-em3d107-1 — the casters into the map from the key light, with the shadow bias; depth only.</summary>
    private int ShadowPass(ID3D11DeviceContext ctx, Scene3DFramePlan plan)
    {
        if (_shadowMap?.Size != plan.ShadowSize)
        {
            // cleared before the new one is made, so a failed allocation leaves no disposed map behind to be bound
            _shadowMap?.Dispose();
            _shadowMap = null;
            _shadowMap = NewDepthTarget(plan.ShadowSize);
        }
        UnbindLighting(ctx);
        ctx.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), _shadowMap.Dsv);
        ctx.RSSetViewport(0, 0, plan.ShadowSize, plan.ShadowSize);
        ctx.ClearDepthStencilView(_shadowMap.Dsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.RSSetState(_rasterShadow);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vsShadow);
        ctx.PSSetShader(_psDepth);
        ctx.OMSetBlendState(_blendOff);
        ctx.OMSetDepthStencilState(_dsWrite);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _vb!, Scene3DVertex.Stride);
        ctx.IASetIndexBuffer(_ib!, DxFormat.R32_UInt, 0);
        int transform = -1, draws = 0;
        for (int i = 0; i < plan.ShadowDrawCount; i++)
        {
            ref var d = ref plan.ShadowDraws[i];
            if (d.Transform != transform && d.Transform < plan.TransformCount) SetTransform(ctx, plan, transform = d.Transform);
            ctx.DrawIndexed((uint)d.Count, (uint)d.First, 0);
            draws++;
        }
        return draws;
    }

    /// <summary>R-em3d107-2 — the occlusion: the opaque materials' (and the ground's) depths along the view's rays, the horizon pass, the
    /// blur. The main depth buffer serves the prepass; the main pass clears it again.</summary>
    private int OcclusionPasses(ID3D11DeviceContext ctx, Scene3DFramePlan plan)
    {
        int w = plan.Width, h = plan.Height, draws = 0;
        if (_aoDepth is null || _aoDepth.W != w || _aoDepth.H != h)
        {
            _aoDepth?.Dispose(); _aoRaw?.Dispose(); _aoBlur?.Dispose();
            (_aoDepth, _aoRaw, _aoBlur) = (null, null, null);
            _aoDepth = NewColourTarget(w, h, DxFormat.R32_Float);
            _aoRaw = NewColourTarget(w, h, DxFormat.R8_UNorm);
            _aoBlur = NewColourTarget(w, h, DxFormat.R8_UNorm);
        }
        UnbindLighting(ctx);
        ctx.OMSetRenderTargets(_aoDepth.Rtv, _dsv);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.ClearRenderTargetView(_aoDepth.Rtv, new Color4(Occlusion.Empty, 0, 0, 0));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.RSSetState(Raster(Scene3DDepthTie.None));
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_psPrepass);
        ctx.OMSetBlendState(_blendOff);
        ctx.OMSetDepthStencilState(_dsWrite);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _vb!, Scene3DVertex.Stride);
        ctx.IASetIndexBuffer(_ib!, DxFormat.R32_UInt, 0);
        int transform = -1;
        for (int i = 0; i < plan.DrawCount; i++)
        {
            ref var d = ref plan.Draws[i];
            if (d.Pipeline != Scene3DPipeline.Pbr) continue;
            if (d.Transform != transform && d.Transform < plan.TransformCount) SetTransform(ctx, plan, transform = d.Transform);
            ctx.DrawIndexed((uint)d.Count, (uint)d.First, 0);
            draws++;
        }
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(_vsGrid);
        if (plan.GroundDrawn)
        {
            ctx.PSSetShader(_psGroundPrepass);
            ctx.Draw(6, 0);
            draws++;
        }
        ctx.OMSetDepthStencilState(_dsOff);
        foreach (var (ps, target) in new[] { (_psAo, _aoRaw!), (_psAoBlur, _aoBlur!) })
        {
            UnbindLighting(ctx);
            ctx.OMSetRenderTargets(target.Rtv, null);
            ctx.PSSetShaderResource(5, _aoDepth.Srv);
            if (ps == _psAoBlur) ctx.PSSetShaderResource(6, _aoRaw!.Srv);
            ctx.PSSetShader(ps);
            ctx.Draw(6, 0);
            draws++;
        }
        return draws;
    }

    /// <summary>The shadow map (or its stand-in), its comparison sampler and the blurred occlusion for a lit fragment: t3, s2, t4.</summary>
    private void BindLighting(ID3D11DeviceContext ctx)
    {
        ctx.PSSetShaderResource(3, (_shadowMap ?? _noShadowMap!).Srv);
        ctx.PSSetSampler(2, _shadowSampler);
        ctx.PSSetShaderResource(4, (_aoBlur ?? _noOcclusion!).Srv);
    }

    /// <summary>brief-em3d-43 gate 6 — the changed ranges only, through the immediate context, which orders
    /// the write after every draw already issued that reads the old bytes.</summary>
    public override void PatchScene(Scene3DModel scene, Scene3DPatch patch)
    {
        var ctx = Ctx;
        foreach (var r in patch.Ranges)
        {
            var target = r.Buffer switch { Scene3DPatchBuffer.Vertices => _vb, Scene3DPatchBuffer.Indices => _ib, _ => _lines };
            if (target is null) { UploadScene(scene); return; }
            ctx.UpdateSubresource(Scene3DPatch.Source(scene, r), target, 0, 0, 0,
                                  new Vortice.Mathematics.Box(r.ByteOffset, 0, 0, r.ByteOffset + r.ByteLength, 1, 1));
            Counters.CountUpload(r.ByteLength);
        }
    }

    /// <summary>brief-em3d-104 R-em3d104-3a — the whole shade stream; DEFAULT usage, so PatchShade rewrites ranges in place.</summary>
    public override void UploadShade(Scene3DModel scene)
    {
        ReleaseShade();
        fixed (Scene3DShadeVertex* p = scene.ShadeVertices)
            _shade = NewBuffer(p, scene.ShadeVertices.Length * Scene3DShadeVertex.Stride, BindFlags.VertexBuffer, ResourceUsage.Default);
    }

    public override void PatchShade(Scene3DModel scene, Scene3DPatch patch)
    {
        if (patch.ShadeRanges.Count > 0 && _shade is null) { UploadShade(scene); return; }
        foreach (var r in patch.ShadeRanges)
        {
            Ctx.UpdateSubresource(Scene3DPatch.Source(scene, r), _shade!, 0, 0, 0,
                                  new Vortice.Mathematics.Box(r.ByteOffset, 0, 0, r.ByteOffset + r.ByteLength, 1, 1));
            Counters.CountUpload(r.ByteLength);
        }
    }

    public override void ReleaseShade() { _shade?.Dispose(); _shade = null; }

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

    public override void UploadFieldNormals(float[] normals)
    {
        _fieldNormals?.Dispose();
        fixed (float* p = normals) _fieldNormals = NewBuffer(p, normals.Length * 4, BindFlags.VertexBuffer);
        _fieldNormalCount = normals.Length / FieldShading.Floats;
    }

    private ID3D11Buffer? NewBuffer(void* data, int length, BindFlags bind, ResourceUsage usage = ResourceUsage.Immutable)
    {
        if (length == 0) return null;
        Counters.CountUpload(length);
        return Device.CreateBuffer(new BufferDescription((uint)length, bind, usage), (nint)data)
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
            ObservePresent(im.Imported.ImportCompleted);   // designer feedback round 34: a lost context fails HERE first
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
        if (o is IAsyncDisposable ad) Viewer3DBackend.ObserveDisposal(ad.DisposeAsync());
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
        => ObservePresent(surface.UpdateWithKeyedMutexAsync(_images[image].Imported, (uint)KeyCompositor, (uint)KeyRenderer));

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
        ID3D11Texture2D? tex = null, staging = null;
        ID3D11RenderTargetView? view = null;
        try
        {
            tex = dev.CreateTexture2D(new Texture2DDescription(ColorFormat, w, h, 1, 1, BindFlags.RenderTarget));
            view = dev.CreateRenderTargetView(tex);
            staging = dev.CreateTexture2D(new Texture2DDescription(ColorFormat, w, h, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));
            RenderInto(view, plan);
            return ReadBack(tex, staging, w, h);
        }
        catch (SharpGen.Runtime.SharpGenException e)
        {
            // brief-em3d-107 — a picture is the largest thing this backend draws (up to FieldPicture.MaxSide a side, supersampled, with the
            // occlusion's targets beside it): an allocation the device refuses is reported as the picture failing, never thrown past the
            // caller's handler as a COM error.
            throw new Viewer3DPresentFault($"The 3D view could not draw a {w} × {h} picture: {e.Message}");
        }
        finally { staging?.Dispose(); view?.Dispose(); tex?.Dispose(); }
    }

    private byte[] ReadBack(ID3D11Texture2D tex, ID3D11Texture2D staging, uint w, uint h)
    {
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
        // brief-em3d-107 — the shadow map when its key moved (the session decides) and the occlusion every realistic frame, before the
        // pick and the colour pass; both read the frame's uniforms.
        if (plan.Realistic && _vb is not null && _ib is not null && (plan.ShadowPass && plan.ShadowDrawCount > 0 || plan.Occlusion))
        {
            ctx.UpdateSubresource(plan.Uniforms.AsSpan(), _cb);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            ctx.VSSetConstantBuffer(0, _cb);
            ctx.PSSetConstantBuffer(0, _cb);
            ctx.VSSetConstantBuffer(1, _cbTransform);
            if (plan.ShadowPass && plan.ShadowDrawCount > 0) draws += ShadowPass(ctx, plan);
            if (plan.Occlusion) draws += OcclusionPasses(ctx, plan);
            UnbindLighting(ctx);
        }
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.VSSetConstantBuffer(1, _cbTransform);
        SetTransform(ctx, plan, 0);
        int transform = 0;
        var tie = Scene3DDepthTie.None;
        ctx.RSSetState(Raster(tie));

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
                if (d.Tie != tie) ctx.RSSetState(Raster(tie = d.Tie));
                // brief-em3d-48 — an array element's pick draw: its translation and id offset.
                if (d.Transform != transform && d.Transform < plan.TransformCount)
                {
                    transform = d.Transform;
                    SetTransform(ctx, plan, transform);
                }
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
        ctx.ClearRenderTargetView(target, new Color4(r, g, b, plan.Transparent ? 0f : 1f));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);
        Scene3DBuffer bound = (Scene3DBuffer)(-1);
        Scene3DPipeline state = (Scene3DPipeline)(-1);
        for (int i = 0; i < plan.DrawCount; i++)
        {
            ref var d = ref plan.Draws[i];
            if (d.Tie != tie) ctx.RSSetState(Raster(tie = d.Tie));
            var buf = d.Buffer switch
            {
                Scene3DBuffer.Scene => _vb, Scene3DBuffer.SceneLines => _lines, Scene3DBuffer.Field => _field, Scene3DBuffer.Image => _imageVb,
                Scene3DBuffer.Overlay0 => _overlays[0], Scene3DBuffer.Overlay1 => _overlays[1], _ => _overlays[2],
            };
            bool lines = d.Pipeline is Scene3DPipeline.Lines or Scene3DPipeline.Edges;
            bool field = d.Pipeline is Scene3DPipeline.Field or Scene3DPipeline.FieldBlend or Scene3DPipeline.FieldLit;
            bool fieldLit = d.Pipeline == Scene3DPipeline.FieldLit;
            // brief-em3d-106 — the backdrop draws like the grid (six vertices, no buffer); a realistic draw needs the look bound.
            bool grid = d.Pipeline is Scene3DPipeline.Grid or Scene3DPipeline.Backdrop or Scene3DPipeline.Ground;
            bool pbr = d.Pipeline is Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent;
            if ((pbr || d.Pipeline == Scene3DPipeline.Backdrop) && (_envMap is null || _envBrdf is null || _cbAppearances is null)) continue;
            if (pbr && _shade is null) continue;
            // brief-em3d-109 — a Lit field needs the environment for its sheen and its normals beside its vertices
            if (fieldLit && (_envMap is null || _envBrdf is null || _cbAppearances is null || _fieldNormals is null
                             || d.First + d.Count > _fieldNormalCount)) continue;
            // brief-em3d-101 — an image draw: not indexed, its own vertex stage, its texture at t0.
            bool image = d.Pipeline is Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent;
            if (image && (d.Texture < 0 || d.Texture >= _textures.Bound.Length || _textures.Bound[d.Texture] is null)) continue;
            if (!grid && (buf is null || (!lines && !field && !image && _ib is null))) continue;
            if (field && d.First + d.Count > _fieldCount) continue;
            if (d.Pipeline != state)
            {
                // The vertex stage: the scene's vertex (0), the field's (1), none at all — the grid (2) — or an image's (3).
                static int StageOf(Scene3DPipeline p) => p switch
                {
                    Scene3DPipeline.Field or Scene3DPipeline.FieldBlend => 1,
                    Scene3DPipeline.Grid or Scene3DPipeline.Backdrop or Scene3DPipeline.Ground => 2,
                    Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent => 3,
                    Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent => 4, Scene3DPipeline.FieldLit => 5, _ => 0,
                };
                int wasStage = (int)state < 0 ? -1 : StageOf(state);
                int stage = StageOf(d.Pipeline);
                state = d.Pipeline;
                if (stage != wasStage)
                {
                    ctx.IASetInputLayout(stage switch { 1 => _layoutField, 2 => null, 3 => _layoutImage, 4 => _layoutPbr, 5 => _layoutFieldLit, _ => _layout });
                    ctx.VSSetShader(stage switch { 1 => _vsField, 2 => _vsGrid, 3 => _vsImage, 4 => _vsPbr, 5 => _vsFieldLit, _ => _vs });
                    bound = (Scene3DBuffer)(-1);
                    if (stage == 4) ctx.IASetVertexBuffer(ShadeSlot, _shade!, (uint)Scene3DShadeVertex.Stride);
                    if (stage == 5) ctx.IASetVertexBuffer(ShadeSlot, _fieldNormals!, (uint)FieldShading.Stride);
                }
                if (pbr || state is Scene3DPipeline.Backdrop or Scene3DPipeline.FieldLit)
                {
                    // brief-em3d-106 — the look: the appearance table at b2, the environment at t1 and t2, its sampler at s1.
                    ctx.PSSetConstantBuffer(2, _cbAppearances!);
                    ctx.PSSetShaderResource(1, _envMap!);
                    ctx.PSSetShaderResource(2, _envBrdf!);
                    ctx.PSSetSampler(1, _imageSampler);
                }
                // brief-em3d-107 — a lit fragment and the ground read the shadow map and the occlusion
                if (pbr || state == Scene3DPipeline.Ground) BindLighting(ctx);
                ctx.PSSetShader(state switch
                {
                    Scene3DPipeline.Field or Scene3DPipeline.FieldBlend => _psField, Scene3DPipeline.FieldLit => _psFieldLit,
                    Scene3DPipeline.Lines => _psLine, Scene3DPipeline.Edges => _psEdge,
                    Scene3DPipeline.OnTop => _psTop, Scene3DPipeline.Grid => _psGrid,
                    Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent => _psImage,
                    Scene3DPipeline.Pbr => _psPbr, Scene3DPipeline.PbrTranslucent => _psPbrGlass, Scene3DPipeline.Backdrop => _psBackdrop,
                    Scene3DPipeline.Ground => _psGround, _ => _psColor,
                });
                if (image) ctx.PSSetSampler(0, _imageSampler);
                bool selection = state is Scene3DPipeline.Edges or Scene3DPipeline.OnTop;
                ctx.OMSetBlendState(state == Scene3DPipeline.PbrTranslucent ? _blendPremultiplied
                                    : state is Scene3DPipeline.Translucent or Scene3DPipeline.Grid or Scene3DPipeline.Image
                                    or Scene3DPipeline.ImageTranslucent or Scene3DPipeline.Ground or Scene3DPipeline.FieldBlend
                                    or Scene3DPipeline.FieldLit || selection ? _blendOn : _blendOff);
                ctx.OMSetDepthStencilState(selection || state == Scene3DPipeline.Backdrop ? _dsOff
                    : state is Scene3DPipeline.Translucent or Scene3DPipeline.Grid or Scene3DPipeline.ImageTranslucent
                      or Scene3DPipeline.PbrTranslucent or Scene3DPipeline.Ground ? _dsNoWrite : _dsWrite);
                ctx.IASetPrimitiveTopology(lines ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList);
            }
            if (grid)
            {
                ctx.Draw(6, 0);
                draws++;
                continue;
            }
            if (d.Transform != transform && d.Transform < plan.TransformCount)
            {
                transform = d.Transform;
                SetTransform(ctx, plan, transform);
            }
            if (d.Buffer != bound)
            {
                bound = d.Buffer;
                ctx.IASetVertexBuffer(0, buf!, field ? (uint)FieldVertex.Stride : image ? (uint)Scene3DImageVertex.Stride : Scene3DVertex.Stride);
                if (!lines && !field && !image) ctx.IASetIndexBuffer(_ib!, DxFormat.R32_UInt, 0);
            }
            if (image) ctx.PSSetShaderResource(0, _textures.Bound[d.Texture]!);
            if (lines || field || image) ctx.Draw((uint)d.Count, (uint)d.First);
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
        (_dsv, _depth) = (null, null);
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
            PickedFace = ((uint*)m.DataPointer)[1];
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
        _vb?.Dispose(); _ib?.Dispose(); _lines?.Dispose(); _field?.Dispose(); _imageVb?.Dispose(); _shade?.Dispose(); _fieldNormals?.Dispose();
        ReleaseEnvironment();
        _textures.Clear(v => v?.Dispose());
        foreach (var o in _overlays) o?.Dispose();
        if (_device is null) return;
        foreach (var s in _stagingId) s?.Dispose();
        foreach (var s in _stagingPos) s?.Dispose();
        foreach (var q in _query) q?.Dispose();
        _dsv?.Dispose(); _depth?.Dispose();
        _pickIdRtv?.Dispose(); _pickPosRtv?.Dispose(); _pickDsv?.Dispose();
        _pickId?.Dispose(); _pickPos?.Dispose(); _pickDepth?.Dispose();
        _cb?.Dispose(); _cbTransform?.Dispose(); _layout?.Dispose(); _vs?.Dispose(); _layoutField?.Dispose(); _vsField?.Dispose();
        _psColor?.Dispose(); _psLine?.Dispose(); _psPick?.Dispose(); _psField?.Dispose(); _psEdge?.Dispose(); _psTop?.Dispose(); _vsGrid?.Dispose(); _psGrid?.Dispose();
        _layoutImage?.Dispose(); _vsImage?.Dispose(); _psImage?.Dispose(); _imageSampler?.Dispose();
        _layoutPbr?.Dispose(); _vsPbr?.Dispose(); _layoutFieldLit?.Dispose(); _vsFieldLit?.Dispose(); _psFieldLit?.Dispose(); _psPbr?.Dispose(); _psBackdrop?.Dispose(); _blendPremultiplied?.Dispose();
        _vsShadow?.Dispose(); _psDepth?.Dispose(); _psPrepass?.Dispose(); _psGroundPrepass?.Dispose(); _psAo?.Dispose(); _psAoBlur?.Dispose();
        _psGround?.Dispose(); _psPbrGlass?.Dispose(); _rasterShadow?.Dispose(); _shadowSampler?.Dispose();
        _noShadowMap?.Dispose(); _noOcclusion?.Dispose();
        _blendOff?.Dispose(); _blendOn?.Dispose(); _dsWrite?.Dispose(); _dsNoWrite?.Dispose(); _dsOff?.Dispose(); foreach (var r in _raster) r?.Dispose();
        _ctx?.Dispose(); _device.Dispose();
    }

    /// <summary>brief-em3d-46 — slot <paramref name="slot"/> of the plan's per-draw transforms into register b1.</summary>
    private void SetTransform(ID3D11DeviceContext ctx, Scene3DFramePlan plan, int slot)
    {
        ctx.UpdateSubresource(plan.Transforms.AsSpan(Scene3DFramePlan.TransformFloats * slot, Scene3DFramePlan.TransformFloats), _cbTransform);
        Counters.CountUniform(Scene3DFramePlan.TransformBytesPerDraw);
    }
}
