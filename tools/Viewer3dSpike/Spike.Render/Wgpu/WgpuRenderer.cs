using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Viewer3dSpike.Scene;

namespace Viewer3dSpike.Render.Wgpu;

/// <summary>
/// Route B's renderer: WebGPU through wgpu-native, one WGSL module for every OS. Same scene, same
/// draw structure as route C. Differences forced by WebGPU core:
/// - there are no push constants, so the per-frame camera/hover block is a <c>queue.writeBuffer</c>
///   of 96 bytes into a uniform buffer (counted as UNIFORM bytes, never as upload);
/// - pick readback is <c>copyTextureToBuffer</c> + <c>mapAsync</c>, polled each frame.
/// Renders into its own RGBA8/BGRA8 texture; a host presents it (Avalonia, via the Metal texture
/// behind it on macOS — see WgpuMetalBridge in Spike.App) or reads it back (the harness).
/// </summary>
public sealed unsafe class WgpuRenderer : IDisposable
{
    const int Ring = 3, UniformSize = 96 + 512;
    public readonly WgpuApi Api = new();
    readonly FrameCounters _counters;
    public nint Instance, Adapter, Device, Queue;
    nint _vb, _ib, _ubo, _pickUbo, _module;
    nint _pOpaque, _pTrans, _pPick, _bgOpaque, _bgTrans, _bgPick;
    nint _depthTex, _depthView, _pickTex, _pickView, _pickDepth, _pickDepthView;
    public nint ColorTexture { get; private set; }
    nint _colorView;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public readonly uint ColorFormat;
    SceneModel _scene = null!;
    TranslucentSorter _sorter = null!;
    readonly nint[] _readback = new nint[Ring];
    readonly long[] _rbFrame = new long[Ring];
    readonly int[] _rbState = new int[Ring];     // 0 free, 1 copy submitted (map pending), 2 mapped
    int _rbHead;
    GCHandle _self;
    public uint HoveredId { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public string Info { get; private set; } = "";
    public static string? LastError;

    public WgpuRenderer(FrameCounters counters, uint colorFormat = W.FmtRGBA8)
    {
        _counters = counters;
        ColorFormat = colorFormat;
        _self = GCHandle.Alloc(this);
    }

    public void Init(SceneModel scene, int width, int height)
    {
        _scene = scene;
        _sorter = new TranslucentSorter(scene.Translucent);
        var a = Api;
        Instance = a.CreateInstance(null);
        if (Instance == 0) throw new InvalidOperationException("wgpuCreateInstance failed");

        var opts = new RequestAdapterOptions { FeatureLevel = W.FeatureCore, PowerPreference = W.HighPerformance };
        nint adapter = 0;
        a.InstanceRequestAdapter(Instance, &opts, new CallbackInfo { Mode = W.CallbackAllowProcessEvents, Callback = (delegate* unmanaged<uint, nint, SV, void*, void*, void>)&OnAdapter, Ud1 = &adapter });
        for (int i = 0; i < 1000 && adapter == 0; i++) { a.InstanceProcessEvents(Instance); if (adapter == 0) Thread.Sleep(1); }
        if (adapter == 0) throw new InvalidOperationException("no WebGPU adapter: " + LastError);
        Adapter = adapter;
        AdapterInfo ai = default;
        a.AdapterGetInfo(Adapter, &ai);
        string[] backends = ["?", "Null", "WebGPU", "D3D11", "D3D12", "Metal", "Vulkan", "OpenGL", "OpenGLES"];
        Info = $"wgpu-native {a.GetVersion():X} | {WgpuApi.Str(ai.Device)} | backend {(ai.BackendType < backends.Length ? backends[ai.BackendType] : ai.BackendType.ToString())} | {Path.GetFileName(a.LibPath)}";

        var dd = new DeviceDescriptor
        {
            Label = SV.Null,
            DefaultQueue = new QueueDescriptor { Label = SV.Null },
            DeviceLost = new CallbackInfo { Mode = W.CallbackAllowSpontaneous },
            UncapturedError = new ErrorCallbackInfo { Callback = (delegate* unmanaged<nint*, uint, SV, void*, void*, void>)&OnError },
        };
        nint device = 0;
        a.AdapterRequestDevice(Adapter, &dd, new CallbackInfo { Mode = W.CallbackAllowProcessEvents, Callback = (delegate* unmanaged<uint, nint, SV, void*, void*, void>)&OnDevice, Ud1 = &device });
        for (int i = 0; i < 1000 && device == 0; i++) { a.InstanceProcessEvents(Instance); if (device == 0) Thread.Sleep(1); }
        if (device == 0) throw new InvalidOperationException("no WebGPU device: " + LastError);
        Device = device;
        Queue = a.DeviceGetQueue(Device);

        _vb = Buffer((ulong)scene.Vertices.Length, W.BufVertex | W.BufCopyDst);
        fixed (byte* p = scene.Vertices) a.QueueWriteBuffer(Queue, _vb, 0, p, (nuint)scene.Vertices.Length);
        _counters.CountUpload(scene.Vertices.Length);
        _ib = Buffer((ulong)scene.Indices.Length * 4, W.BufIndex | W.BufCopyDst);
        fixed (uint* p = scene.Indices) a.QueueWriteBuffer(Queue, _ib, 0, p, (nuint)scene.Indices.Length * 4);
        _counters.CountUpload(scene.Indices.Length * 4L);
        _ubo = Buffer(UniformSize, W.BufUniform | W.BufCopyDst);
        _pickUbo = Buffer(UniformSize, W.BufUniform | W.BufCopyDst);
        fixed (float* c = scene.Colors)
        {
            a.QueueWriteBuffer(Queue, _ubo, 96, c, (nuint)(scene.Colors.Length * 4));
            a.QueueWriteBuffer(Queue, _pickUbo, 96, c, (nuint)(scene.Colors.Length * 4));
        }
        _counters.CountUpload(scene.Colors.Length * 8L);
        for (int i = 0; i < Ring; i++) _readback[i] = Buffer(256, W.BufMapRead | W.BufCopyDst);

        _module = Module(Shader);
        _pOpaque = Pipeline(ColorFormat, "fs_color", blend: false, depthWrite: true);
        _pTrans = Pipeline(ColorFormat, "fs_color", blend: true, depthWrite: false);
        _pPick = Pipeline(W.FmtR32Uint, "fs_pick", blend: false, depthWrite: true);
        _bgOpaque = BindGroup(_pOpaque, _ubo);
        _bgTrans = BindGroup(_pTrans, _ubo);
        _bgPick = BindGroup(_pPick, _pickUbo);

        (_pickTex, _pickView) = Texture(1, 1, W.FmtR32Uint, W.TexRender | W.TexCopySrc);
        (_pickDepth, _pickDepthView) = Texture(1, 1, W.FmtDepth24Plus, W.TexRender);
        Resize(width, height);
    }

    public void Resize(int w, int h)
    {
        if (w == Width && h == Height) return;
        if (ColorTexture != 0) { Api.TextureViewRelease(_colorView); Api.TextureRelease(ColorTexture); Api.TextureViewRelease(_depthView); Api.TextureRelease(_depthTex); }
        Width = Math.Max(1, w); Height = Math.Max(1, h);
        (var ct, _colorView) = Texture(Width, Height, ColorFormat, W.TexRender | W.TexCopySrc | W.TexBinding);
        ColorTexture = ct;
        (_depthTex, _depthView) = Texture(Width, Height, W.FmtDepth24Plus, W.TexRender);
    }

    const string Shader = """
        struct U { vp: mat4x4f, eye: vec4f, hover: u32, sel: u32, per: u32, pad: u32, colors: array<vec4f, 32> };
        @group(0) @binding(0) var<uniform> u: U;
        struct VO { @builtin(position) pos: vec4f, @location(0) world: vec3f, @location(1) @interpolate(flat) id: u32 };
        @vertex fn vs(@location(0) p: vec3f, @location(1) id: u32) -> VO {
            var o: VO; o.pos = u.vp * vec4f(p, 1.0); o.world = p; o.id = id; return o;
        }
        @fragment fn fs_color(i: VO) -> @location(0) vec4f {
            let n = normalize(cross(dpdx(i.world), dpdy(i.world)));
            let d = abs(dot(n, normalize(u.eye.xyz - i.world)));
            let c = u.colors[(i.id - 1u) % u.per];
            var rgb = c.rgb * (0.25 + 0.75 * d);
            if (i.id == u.hover) { rgb = mix(rgb, vec3f(0.2, 0.9, 1.0), 0.6); }
            if (i.id == u.sel) { rgb = mix(rgb, vec3f(1.0, 0.3, 1.0), 0.6); }
            return vec4f(rgb, c.a);
        }
        @fragment fn fs_pick(i: VO) -> @location(0) u32 { return i.id; }
        """;

    /// <summary>Encode and submit one frame. The caller brackets it with FrameCounters.</summary>
    public void Render(in FrameInput input)
    {
        var a = Api;
        int draws = 0;
        a.DevicePoll(Device, 0, null);
        a.InstanceProcessEvents(Instance);
        CollectPicks();

        float* u = stackalloc float[24];
        input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true);
        var eye = input.Camera.Eye;
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        ((uint*)u)[20] = HoveredId; ((uint*)u)[21] = input.Selected; ((uint*)u)[22] = (uint)_scene.ObjectsPerReplica; ((uint*)u)[23] = 0;
        a.QueueWriteBuffer(Queue, _ubo, 0, u, 96);
        _counters.CountUniform(96);

        nint enc = a.DeviceCreateCommandEncoder(Device, null);
        int slot = -1;
        if (input.PickX >= 0 && input.PickY >= 0 && _rbState[_rbHead % Ring] == 0)
        {
            slot = _rbHead % Ring;
            input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true, input.PickX, input.PickY);
            a.QueueWriteBuffer(Queue, _pickUbo, 0, u, 96);
            _counters.CountUniform(96);
            var ca = new ColorAttachment { View = _pickView, DepthSlice = W.DepthSliceUndefined, LoadOp = W.LoadClear, StoreOp = W.StoreStore };
            var da = new DepthAttachment { View = _pickDepthView, DepthLoadOp = W.LoadClear, DepthStoreOp = W.StoreDiscard, DepthClear = 1f };
            var rp = new RenderPassDescriptor { Label = SV.Null, ColorCount = 1, Colors = &ca, Depth = &da };
            nint pass = a.CommandEncoderBeginRenderPass(enc, &rp);
            a.PassSetPipeline(pass, _pPick);
            a.PassSetBindGroup(pass, 0, _bgPick, 0, null);
            a.PassSetVertexBuffer(pass, 0, _vb, 0, ulong.MaxValue);
            a.PassSetIndexBuffer(pass, _ib, W.IndexUint32, 0, ulong.MaxValue);
            a.PassDrawIndexed(pass, (uint)_scene.OpaqueIndexCount, 1, 0, 0, 0); draws++;
            a.PassEnd(pass); a.PassRelease(pass);
            var src = new TexelCopyTextureInfo { Texture = _pickTex, Aspect = W.AspectAll };
            var dst = new TexelCopyBufferInfo { Layout = new TexelCopyBufferLayout { BytesPerRow = 256, RowsPerImage = 1 }, Buffer = _readback[slot] };
            var ext = new Extent3D { Width = 1, Height = 1, Depth = 1 };
            a.CommandEncoderCopyTextureToBuffer(enc, &src, &dst, &ext);
        }

        {
            var ca = new ColorAttachment { View = _colorView, DepthSlice = W.DepthSliceUndefined, LoadOp = W.LoadClear, StoreOp = W.StoreStore, Clear = new Color { R = 0.11, G = 0.12, B = 0.14, A = 1 } };
            var da = new DepthAttachment { View = _depthView, DepthLoadOp = W.LoadClear, DepthStoreOp = W.StoreDiscard, DepthClear = 1f };
            var rp = new RenderPassDescriptor { Label = SV.Null, ColorCount = 1, Colors = &ca, Depth = &da };
            nint pass = a.CommandEncoderBeginRenderPass(enc, &rp);
            a.PassSetVertexBuffer(pass, 0, _vb, 0, ulong.MaxValue);
            a.PassSetIndexBuffer(pass, _ib, W.IndexUint32, 0, ulong.MaxValue);
            a.PassSetPipeline(pass, _pOpaque);
            a.PassSetBindGroup(pass, 0, _bgOpaque, 0, null);
            a.PassDrawIndexed(pass, (uint)_scene.OpaqueIndexCount, 1, 0, 0, 0); draws++;
            _sorter.Sort(input.Camera);
            a.PassSetPipeline(pass, _pTrans);
            a.PassSetBindGroup(pass, 0, _bgTrans, 0, null);
            var tr = _scene.Translucent;
            foreach (int i in _sorter.Order) { a.PassDrawIndexed(pass, (uint)tr[i].IndexCount, 1, (uint)tr[i].FirstIndex, 0, 0); draws++; }
            a.PassEnd(pass); a.PassRelease(pass);
        }
        nint cmd = a.CommandEncoderFinish(enc, null);
        a.QueueSubmit(Queue, 1, &cmd);
        a.CommandBufferRelease(cmd); a.EncoderRelease(enc);

        if (slot >= 0)
        {
            _rbState[slot] = 1;
            _rbFrame[slot] = _counters.FrameIndex;
            _rbHead++;
            a.BufferMapAsync(_readback[slot], W.MapRead, 0, 256, new CallbackInfo
            {
                Mode = W.CallbackAllowProcessEvents, Callback = (delegate* unmanaged<uint, SV, void*, void*, void>)&OnMapped,
                Ud1 = (void*)GCHandle.ToIntPtr(_self), Ud2 = (void*)slot,
            });
        }
        DrawCallsLastFrame = draws;
    }

    void CollectPicks()
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = (_rbHead + k) % Ring;
            if (_rbState[slot] != 2) continue;
            var p = (uint*)Api.BufferGetConstMappedRange(_readback[slot], 0, 256);
            if (p != null) HoveredId = p[0];
            Api.BufferUnmap(_readback[slot]);
            _rbState[slot] = 0;
            _counters.PickResolved(_rbFrame[slot]);
        }
    }

    public bool PickPending { get { foreach (var s in _rbState) if (s != 0) return true; return false; } }

    [UnmanagedCallersOnly]
    static void OnMapped(uint status, SV msg, void* ud1, void* ud2)
    {
        var self = (WgpuRenderer)GCHandle.FromIntPtr((nint)ud1).Target!;
        int slot = (int)(nint)ud2;
        if (status == W.StatusSuccess) self._rbState[slot] = 2;
        else { self._rbState[slot] = 0; LastError = "mapAsync: " + WgpuApi.Str(msg); }
    }

    [UnmanagedCallersOnly] static void OnAdapter(uint status, nint adapter, SV msg, void* ud1, void* ud2) { if (status == W.StatusSuccess) *(nint*)ud1 = adapter; else LastError = WgpuApi.Str(msg); }
    [UnmanagedCallersOnly] static void OnDevice(uint status, nint device, SV msg, void* ud1, void* ud2) { if (status == W.StatusSuccess) *(nint*)ud1 = device; else LastError = WgpuApi.Str(msg); }
    [UnmanagedCallersOnly] static void OnError(nint* device, uint type, SV msg, void* ud1, void* ud2) { LastError = $"wgpu error {type}: {WgpuApi.Str(msg)}"; Console.Error.WriteLine(LastError); }

    /// <summary>Harness only: read the colour texture back as top-down RGBA (blocks).</summary>
    public byte[] ReadColor()
    {
        var a = Api;
        uint row = (uint)((Width * 4 + 255) & ~255);
        nint buf = Buffer(row * (ulong)Height, W.BufMapRead | W.BufCopyDst);
        nint enc = a.DeviceCreateCommandEncoder(Device, null);
        var src = new TexelCopyTextureInfo { Texture = ColorTexture, Aspect = W.AspectAll };
        var dst = new TexelCopyBufferInfo { Layout = new TexelCopyBufferLayout { BytesPerRow = row, RowsPerImage = (uint)Height }, Buffer = buf };
        var ext = new Extent3D { Width = (uint)Width, Height = (uint)Height, Depth = 1 };
        a.CommandEncoderCopyTextureToBuffer(enc, &src, &dst, &ext);
        nint cmd = a.CommandEncoderFinish(enc, null);
        a.QueueSubmit(Queue, 1, &cmd);
        int done = 0;
        a.BufferMapAsync(buf, W.MapRead, 0, (nuint)(row * Height), new CallbackInfo { Mode = W.CallbackAllowProcessEvents, Callback = (delegate* unmanaged<uint, SV, void*, void*, void>)&OnMappedFlag, Ud1 = &done });
        while (done == 0) { a.DevicePoll(Device, 1, null); a.InstanceProcessEvents(Instance); }
        var p = (byte*)a.BufferGetConstMappedRange(buf, 0, (nuint)(row * Height));
        var outp = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++) Marshal.Copy((nint)(p + y * row), outp, y * Width * 4, Width * 4);
        if (ColorFormat == W.FmtBGRA8) for (int i = 0; i < outp.Length; i += 4) (outp[i], outp[i + 2]) = (outp[i + 2], outp[i]);
        a.BufferUnmap(buf); a.BufferRelease(buf);
        return outp;
    }
    [UnmanagedCallersOnly] static void OnMappedFlag(uint status, SV msg, void* ud1, void* ud2) => *(int*)ud1 = 1;

    nint Buffer(ulong size, ulong usage)
    {
        var d = new BufferDescriptor { Label = SV.Null, Usage = usage, Size = size };
        return Api.DeviceCreateBuffer(Device, &d);
    }

    (nint Tex, nint View) Texture(int w, int h, uint fmt, ulong usage)
    {
        var d = new TextureDescriptor { Label = SV.Null, Usage = usage, Dimension = W.Tex2D, Size = new Extent3D { Width = (uint)w, Height = (uint)h, Depth = 1 }, Format = fmt, MipLevelCount = 1, SampleCount = 1 };
        nint t = Api.DeviceCreateTexture(Device, &d);
        return (t, Api.TextureCreateView(t, null));
    }

    nint Module(string wgsl)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(wgsl);
        fixed (byte* p = b)
        {
            var src = new ShaderSourceWgsl { Chain = new Chained { SType = W.STypeShaderSourceWgsl }, Code = new SV { Data = p, Length = (nuint)b.Length } };
            var d = new ShaderModuleDescriptor { Next = &src, Label = SV.Null };
            return Api.DeviceCreateShaderModule(Device, &d);
        }
    }

    nint Pipeline(uint format, string fs, bool blend, bool depthWrite)
    {
        var attrs = stackalloc VertexAttribute[2];
        attrs[0] = new VertexAttribute { Format = W.VFloat32x3, Offset = 0, ShaderLocation = 0 };
        attrs[1] = new VertexAttribute { Format = W.VUint32, Offset = 12, ShaderLocation = 1 };
        var vbl = new VertexBufferLayout { StepMode = W.StepVertex, ArrayStride = SceneModel.VertexStride, AttributeCount = 2, Attributes = attrs };
        var vsName = "vs"u8; var fsName = System.Text.Encoding.ASCII.GetBytes(fs);
        fixed (byte* pv = vsName) fixed (byte* pf = fsName)
        {
            var bs = new BlendState
            {
                Color = new BlendComponent { Operation = W.BlendAdd, Src = W.FactorSrcAlpha, Dst = W.FactorOneMinusSrcAlpha },
                Alpha = new BlendComponent { Operation = W.BlendAdd, Src = W.FactorOne, Dst = W.FactorOneMinusSrcAlpha },
            };
            var target = new ColorTargetState { Format = format, Blend = blend ? &bs : null, WriteMask = W.ColorWriteAll };
            var frag = new FragmentState { Module = _module, EntryPoint = new SV { Data = pf, Length = (nuint)fsName.Length }, TargetCount = 1, Targets = &target };
            var keep = new StencilFaceState { Compare = W.CmpAlways, FailOp = W.StencilKeep, DepthFailOp = W.StencilKeep, PassOp = W.StencilKeep };
            var ds = new DepthStencilState { Format = W.FmtDepth24Plus, DepthWriteEnabled = depthWrite ? W.OptTrue : W.OptFalse, DepthCompare = W.CmpLessEqual, Front = keep, Back = keep };
            var d = new RenderPipelineDescriptor
            {
                Label = SV.Null,
                Vertex = new VertexState { Module = _module, EntryPoint = new SV { Data = pv, Length = (nuint)vsName.Length }, BufferCount = 1, Buffers = &vbl },
                Primitive = new PrimitiveState { Topology = W.TriangleList, FrontFace = W.FrontCCW, CullMode = W.CullNone },
                DepthStencil = &ds,
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &frag,
            };
            nint p = Api.DeviceCreateRenderPipeline(Device, &d);
            if (p == 0) throw new InvalidOperationException("pipeline: " + LastError);
            return p;
        }
    }

    nint BindGroup(nint pipeline, nint ubo)
    {
        var e = new BindGroupEntry { Binding = 0, Buffer = ubo, Offset = 0, Size = UniformSize };
        var d = new BindGroupDescriptor { Label = SV.Null, Layout = Api.RenderPipelineGetBindGroupLayout(pipeline, 0), EntryCount = 1, Entries = &e };
        return Api.DeviceCreateBindGroup(Device, &d);
    }

    public void Dispose()
    {
        if (_self.IsAllocated) _self.Free();
        // The process exits after the spike; wgpu objects are reclaimed with the device.
    }
}
