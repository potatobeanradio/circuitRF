using System.Runtime.InteropServices;
using Viewer3dSpike.Scene;
using static Viewer3dSpike.Render.Metal.ObjC;

namespace Viewer3dSpike.Render.Metal;

/// <summary>
/// Route A on macOS: native Metal. MSL is compiled from source at start-up
/// (newLibraryWithSource), so there is no shader tool and no native package — only the OS's own
/// Metal framework. Same scene and draw structure as routes B and C. The camera/hover block goes
/// in with setVertexBytes/setFragmentBytes (inline command data, no buffer), so an orbit frame
/// writes into no buffer at all.
/// Renders into any BGRA8 MTLTexture the host hands it — an IOSurface-backed one in the Avalonia
/// pane, a plain one in the harness.
/// </summary>
public sealed unsafe class MetalRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct ClearColor { public double R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] struct MtlOrigin { public nuint X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)] struct MtlSize { public nuint W, H, D; }

    const int Ring = 3;
    public const nuint FmtBGRA8 = 80, FmtR32Uint = 53, FmtDepth32F = 252;
    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")] static extern nint MTLCreateSystemDefaultDevice();

    public readonly nint Device, Queue;
    readonly FrameCounters _counters;
    nint _vb, _ib, _colors, _pOpaque, _pTrans, _pPick, _dsWrite, _dsNoWrite, _pickTex, _pickDepth, _depth;
    int _depthW, _depthH;
    SceneModel _scene = null!;
    TranslucentSorter _sorter = null!;
    readonly nint[] _rb = new nint[Ring], _rbCmd = new nint[Ring];
    readonly long[] _rbFrame = new long[Ring];
    int _rbHead;
    public uint HoveredId { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public string Info { get; }
    readonly string _msl;
    readonly int _uBytes;
    readonly float[] _ublock = GC.AllocateArray<float>(152, pinned: true);

    /// <param name="device">An existing MTLDevice (e.g. wgpu's) or 0 for the system default.</param>
    /// <param name="wgslCrossCompiledMsl">MSL produced from route B's WGSL by naga (the single-source
    /// shader experiment, findings §4): its uniform block carries the colour table, so the whole
    /// 608-byte block goes in as inline bytes and there is no separate colours buffer.</param>
    public MetalRenderer(FrameCounters counters, nint device = 0, string? wgslCrossCompiledMsl = null)
    {
        _msl = wgslCrossCompiledMsl ?? Msl;
        _uBytes = wgslCrossCompiledMsl != null ? 96 + 512 : 96;
        _counters = counters;
        Device = device != 0 ? device : MTLCreateSystemDefaultDevice();
        if (Device == 0) throw new InvalidOperationException("no Metal device");
        Queue = Send(Device, S.newCommandQueue);
        Info = "Metal: " + Marshal.PtrToStringUTF8(Send(Send(Device, S.name), Sel("UTF8String")));
    }

    const string Msl = """
        #include <metal_stdlib>
        using namespace metal;
        struct VIn { float3 pos [[attribute(0)]]; uint id [[attribute(1)]]; };
        struct U { float4x4 vp; float4 eye; uint hover; uint sel; uint per; uint pad; };
        struct VOut { float4 pos [[position]]; float3 world; uint id [[flat]]; };
        vertex VOut vs(VIn v [[stage_in]], constant U& u [[buffer(1)]]) {
            VOut o; o.pos = u.vp * float4(v.pos, 1.0); o.world = v.pos; o.id = v.id; return o;
        }
        fragment float4 fs_color(VOut i [[stage_in]], constant U& u [[buffer(1)]], constant float4* colors [[buffer(2)]]) {
            float3 n = normalize(cross(dfdx(i.world), dfdy(i.world)));
            float d = abs(dot(n, normalize(u.eye.xyz - i.world)));
            float4 c = colors[(i.id - 1u) % u.per];
            float3 rgb = c.rgb * (0.25 + 0.75 * d);
            if (i.id == u.hover) rgb = mix(rgb, float3(0.2, 0.9, 1.0), 0.6);
            if (i.id == u.sel) rgb = mix(rgb, float3(1.0, 0.3, 1.0), 0.6);
            return float4(rgb, c.a);
        }
        fragment uint fs_pick(VOut i [[stage_in]]) { return i.id; }
        """;

    public void Init(SceneModel scene)
    {
        _scene = scene;
        _sorter = new TranslucentSorter(scene.Translucent);
        Array.Copy(scene.Colors, 0, _ublock, 24, scene.Colors.Length);
        nint err = 0;
        nint lib = ((delegate* unmanaged<nint, nint, nint, nint, nint*, nint>)MsgSend)(Device, Sel("newLibraryWithSource:options:error:"), NSString(_msl), 0, &err);
        if (lib == 0) throw new InvalidOperationException("MSL: " + Describe(err));
        nint vs = Send(lib, Sel("newFunctionWithName:"), NSString("vs"));
        nint fsc = Send(lib, Sel("newFunctionWithName:"), NSString("fs_color"));
        nint fsp = Send(lib, Sel("newFunctionWithName:"), NSString("fs_pick"));

        nint vd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint attrs = Send(vd, Sel("attributes"));
        nint a0 = Idx(attrs, 0), a1 = Idx(attrs, 1);
        SendV(a0, Sel("setFormat:"), (nuint)30); SendV(a0, Sel("setOffset:"), (nuint)0); SendV(a0, Sel("setBufferIndex:"), (nuint)0);
        SendV(a1, Sel("setFormat:"), (nuint)36); SendV(a1, Sel("setOffset:"), (nuint)12); SendV(a1, Sel("setBufferIndex:"), (nuint)0);
        SendV(Idx(Send(vd, Sel("layouts")), 0), Sel("setStride:"), (nuint)SceneModel.VertexStride);

        nint Pipe(nint fs, nuint fmt, bool blend)
        {
            nint d = Send(Send(Class("MTLRenderPipelineDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setVertexFunction:"), vs);
            SendV(d, Sel("setFragmentFunction:"), fs);
            SendV(d, Sel("setVertexDescriptor:"), vd);
            SendV(d, Sel("setDepthAttachmentPixelFormat:"), FmtDepth32F);
            nint ca = Idx(Send(d, Sel("colorAttachments")), 0);
            SendV(ca, Sel("setPixelFormat:"), fmt);
            if (blend)
            {
                SendB(ca, Sel("setBlendingEnabled:"), true);
                SendV(ca, Sel("setSourceRGBBlendFactor:"), (nuint)4);          // SourceAlpha
                SendV(ca, Sel("setDestinationRGBBlendFactor:"), (nuint)5);     // OneMinusSourceAlpha
                SendV(ca, Sel("setSourceAlphaBlendFactor:"), (nuint)1);        // One
                SendV(ca, Sel("setDestinationAlphaBlendFactor:"), (nuint)5);
            }
            nint e = 0;
            nint p = ((delegate* unmanaged<nint, nint, nint, nint*, nint>)MsgSend)(Device, Sel("newRenderPipelineStateWithDescriptor:error:"), d, &e);
            Send(d, S.release);
            if (p == 0) throw new InvalidOperationException("pipeline: " + Describe(e));
            return p;
        }
        _pOpaque = Pipe(fsc, FmtBGRA8, false);
        _pTrans = Pipe(fsc, FmtBGRA8, true);
        _pPick = Pipe(fsp, FmtR32Uint, false);

        nint DepthState(bool write)
        {
            nint d = Send(Send(Class("MTLDepthStencilDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setDepthCompareFunction:"), (nuint)3);   // LessEqual
            SendB(d, Sel("setDepthWriteEnabled:"), write);
            nint s = Send(Device, Sel("newDepthStencilStateWithDescriptor:"), d);
            Send(d, S.release);
            return s;
        }
        _dsWrite = DepthState(true);
        _dsNoWrite = DepthState(false);

        fixed (byte* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4);
        fixed (float* p = scene.Colors) _colors = NewBuffer(p, scene.Colors.Length * 4);
        for (int i = 0; i < Ring; i++)
            _rb[i] = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(Device, Sel("newBufferWithLength:options:"), 256, 0);
        _pickTex = NewTexture(1, 1, FmtR32Uint, 4, 0);
        _pickDepth = NewTexture(1, 1, FmtDepth32F, 4, 2);
    }

    nint NewBuffer(void* data, int length)
    {
        _counters.CountUpload(length);
        return ((delegate* unmanaged<nint, nint, void*, nuint, nuint, nint>)MsgSend)(Device, Sel("newBufferWithBytes:length:options:"), data, (nuint)length, 0);
    }

    public nint NewTexture(int w, int h, nuint fmt, nuint usage, nuint storage)
    {
        nint d = ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(Class("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), fmt, (nuint)w, (nuint)h, 0);
        SendV(d, Sel("setUsage:"), usage);
        SendV(d, Sel("setStorageMode:"), storage);
        return Send(Device, Sel("newTextureWithDescriptor:"), d);
    }

    /// <summary>A BGRA8 render target on <paramref name="device"/> backed by an IOSurface, shareable
    /// with the compositor. Static so route B can make one on wgpu's own MTLDevice.</summary>
    public static nint IOSurfaceTexture(nint device, nint ioSurface, int w, int h)
    {
        nint d = ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(Class("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), FmtBGRA8, (nuint)w, (nuint)h, 0);
        SendV(d, Sel("setUsage:"), (nuint)(4 | 1));
        SendV(d, Sel("setStorageMode:"), (nuint)0);
        return ((delegate* unmanaged<nint, nint, nint, nint, nuint, nint>)MsgSend)(device, Sel("newTextureWithDescriptor:iosurface:plane:"), d, ioSurface, 0);
    }

    /// <summary>
    /// Encode and commit one frame into <paramref name="target"/> (BGRA8, <c>input.Width</c> x
    /// <c>input.Height</c>). With a shared event, the command buffer first waits for
    /// <paramref name="waitValue"/> (the compositor finished reading this image) and finally
    /// signals <paramref name="signalValue"/> (the image is ready). <paramref name="waitCompleted"/>
    /// blocks this thread until the GPU finished (for a compositor that syncs "automatically").
    /// Runs in its own autorelease pool (Metal returns autoreleased per-frame objects).
    /// </summary>
    public void Render(nint target, in FrameInput input, nint sharedEvent = 0, ulong waitValue = 0, ulong signalValue = 0, nint copyFrom = 0, bool waitCompleted = false)
    {
        nint pool = PoolPush();
        int draws = 0;
        CollectPicks();
        EnsureDepth(input.Width, input.Height);

        nint cb = Send(Queue, S.commandBuffer);
        if (sharedEvent != 0 && waitValue != 0)
            ((delegate* unmanaged<nint, nint, nint, ulong, void>)MsgSend)(cb, S.encodeWaitForEvent, sharedEvent, waitValue);

        float* u = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _ublock[0]);
        nuint ub = (nuint)_uBytes;
        int slot = -1;
        if (copyFrom == 0 && input.PickX >= 0 && input.PickY >= 0 && _rbCmd[_rbHead % Ring] == 0)
        {
            slot = _rbHead % Ring;
            FillUniforms(u, input, pick: true);
            nint rp = Pass(_pickTex, _pickDepth, 0, 0, 0);
            nint enc = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
            SendV(enc, S.setRenderPipelineState, _pPick);
            SendV(enc, S.setDepthStencilState, _dsWrite);
            ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(enc, S.setVertexBuffer, _vb, 0, 0);
            ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setVertexBytes, u, ub, 1);
            _counters.CountUniform(_uBytes);
            DrawIndexed(enc, _scene.OpaqueIndexCount, 0); draws++;
            Send(enc, S.endEncoding);
            nint blit = Send(cb, S.blitCommandEncoder);
            ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
                blit, S.copyFromTextureToBuffer, _pickTex, 0, 0, default, new MtlSize { W = 1, H = 1, D = 1 }, _rb[slot], 0, 256, 256);
            Send(blit, S.endEncoding);
        }

        if (copyFrom != 0)
        {
            // route B hosted: wgpu drew the frame; this only copies it into the shared image
            nint blit = Send(cb, S.blitCommandEncoder);
            Send(blit, S.copyFromTextureToTexture, copyFrom, target);
            Send(blit, S.endEncoding);
        }
        else
        {
            FillUniforms(u, input, pick: false);
            nint rp = Pass(target, _depth, 0.11, 0.12, 0.14);
            nint enc = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
            ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(enc, S.setVertexBuffer, _vb, 0, 0);
            ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setVertexBytes, u, ub, 1);
            ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setFragmentBytes, u, ub, 1);
            ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(enc, S.setFragmentBuffer, _colors, 0, 2);
            _counters.CountUniform(2 * _uBytes);
            SendV(enc, S.setRenderPipelineState, _pOpaque);
            SendV(enc, S.setDepthStencilState, _dsWrite);
            DrawIndexed(enc, _scene.OpaqueIndexCount, 0); draws++;
            _sorter.Sort(input.Camera);
            SendV(enc, S.setRenderPipelineState, _pTrans);
            SendV(enc, S.setDepthStencilState, _dsNoWrite);
            var tr = _scene.Translucent;
            foreach (int i in _sorter.Order) { DrawIndexed(enc, tr[i].IndexCount, tr[i].FirstIndex); draws++; }
            Send(enc, S.endEncoding);
        }

        if (sharedEvent != 0 && signalValue != 0)
            ((delegate* unmanaged<nint, nint, nint, ulong, void>)MsgSend)(cb, S.encodeSignalEvent, sharedEvent, signalValue);
        Send(cb, S.commit);
        if (slot >= 0)
        {
            _rbCmd[slot] = Send(cb, S.retain);
            _rbFrame[slot] = _counters.FrameIndex;
            _rbHead++;
        }
        DrawCallsLastFrame = draws;
        if (waitCompleted) Send(cb, S.waitUntilCompleted);
        PoolPop(pool);
    }

    void FillUniforms(float* u, in FrameInput input, bool pick)
    {
        if (pick) input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true, input.PickX, input.PickY);
        else input.Camera.ViewProjection(new Span<float>(u, 16), input.Width, input.Height, true);
        var eye = input.Camera.Eye;
        u[16] = eye.X; u[17] = eye.Y; u[18] = eye.Z; u[19] = 1;
        ((uint*)u)[20] = HoveredId; ((uint*)u)[21] = input.Selected; ((uint*)u)[22] = (uint)_scene.ObjectsPerReplica; ((uint*)u)[23] = 0;
    }

    static nint Pass(nint color, nint depth, double r, double g, double b)
    {
        nint rp = Send(Class_RPD, S.renderPassDescriptor);
        nint ca = Idx(Send(rp, S.colorAttachments), 0);
        SendV(ca, S.setTexture, color);
        SendV(ca, S.setLoadAction, (nuint)2);
        SendV(ca, S.setStoreAction, (nuint)1);
        ((delegate* unmanaged<nint, nint, ClearColor, void>)MsgSend)(ca, S.setClearColor, new ClearColor { R = r, G = g, B = b, A = 1 });
        nint da = Send(rp, S.depthAttachment);
        SendV(da, S.setTexture, depth);
        SendV(da, S.setLoadAction, (nuint)2);
        SendV(da, S.setStoreAction, (nuint)0);
        SendD(da, S.setClearDepth, 1.0);
        return rp;
    }
    static readonly nint Class_RPD = Class("MTLRenderPassDescriptor");

    void DrawIndexed(nint enc, int count, int firstIndex) =>
        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, nint, nuint, void>)MsgSend)(enc, S.drawIndexed, 3, (nuint)count, 1, _ib, (nuint)(firstIndex * 4L));

    void EnsureDepth(int w, int h)
    {
        if (w == _depthW && h == _depthH && _depth != 0) return;
        if (_depth != 0) Send(_depth, S.release);
        _depth = NewTexture(w, h, FmtDepth32F, 4, 2);
        _depthW = w; _depthH = h;
    }

    void CollectPicks()
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = (_rbHead + k) % Ring;
            if (_rbCmd[slot] == 0 || SendU(_rbCmd[slot], S.status) < 4) continue;   // 4 = Completed
            HoveredId = *(uint*)Send(_rb[slot], S.contents);
            Send(_rbCmd[slot], S.release);
            _rbCmd[slot] = 0;
            _counters.PickResolved(_rbFrame[slot]);
        }
    }

    public bool PickPending { get { foreach (var c in _rbCmd) if (c != 0) return true; return false; } }

    /// <summary>Harness only: blocking readback of a shared-storage BGRA texture as RGBA rows.</summary>
    public byte[] ReadBgra(nint tex, int w, int h)
    {
        nint buf = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(Device, Sel("newBufferWithLength:options:"), (nuint)(w * h * 4), 0);
        nint pool = PoolPush();
        nint cb = Send(Queue, S.commandBuffer);
        nint blit = Send(cb, S.blitCommandEncoder);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
            blit, S.copyFromTextureToBuffer, tex, 0, 0, default, new MtlSize { W = (nuint)w, H = (nuint)h, D = 1 }, buf, 0, (nuint)(w * 4), (nuint)(w * h * 4));
        Send(blit, S.endEncoding);
        Send(cb, S.commit);
        Send(cb, S.waitUntilCompleted);
        PoolPop(pool);
        var px = new byte[w * h * 4];
        Marshal.Copy(Send(buf, S.contents), px, 0, px.Length);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        Send(buf, S.release);
        return px;
    }

    public void Dispose() { }
}
