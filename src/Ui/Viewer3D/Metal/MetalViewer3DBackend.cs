// brief-em3d-28 R-em3d28-1b — route A on macOS: native Metal through the OS's own frameworks, reached
// through the Objective-C runtime (ObjC.cs). No native package (R-em3d28-1c).
//
// Lifted from tools/Viewer3dSpike's MetalRenderer + InteropPane, which passed every counter on the
// owner's Mac (findings §7): IOSurface-backed BGRA8 images imported by the compositor, synchronised
// by MTLSharedEvent TIMELINE SEMAPHORES — Avalonia 12's default macOS compositor is Metal and offers
// exactly those (R-em3d28-1d) — or, where a compositor offers only "automatic", by waiting for our
// own command buffer before handing the image over.
//
// The shader is the generated scene.metal (Shaders/), compiled once at start-up with
// newLibraryWithSource. Per frame the uniform block goes in as INLINE bytes (setVertexBytes /
// setFragmentBytes), so an orbit writes into no buffer at all.

using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using static CircuitRF.Ui.Viewer3D.Metal.ObjC;

namespace CircuitRF.Ui.Viewer3D.Metal;

internal sealed unsafe class MetalViewer3DBackend : Viewer3DBackend
{
    [StructLayout(LayoutKind.Sequential)] private struct ClearColor { public double R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] private struct MtlOrigin { public nuint X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)] private struct MtlSize { public nuint W, H, D; }

    private const int Ring = 3;
    private const nuint FmtBGRA8 = 80, FmtR32Uint = 53, FmtRGBA32Float = 125, FmtDepth32F = 252;
    private const nuint VtxFloat3 = 30, VtxUInt = 36, VtxUChar4Normalized = 9;
    private const nuint PrimLine = 1, PrimTriangle = 3, IndexUInt32 = 1;
    private const nuint WindingCounterClockwise = 1;

    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")] private static extern nint MTLCreateSystemDefaultDevice();

    private readonly nint _device, _queue;
    private nint _pOpaque, _pTrans, _pLines, _pPick, _dsWrite, _dsNoWrite, _depth, _pickId, _pickPos, _pickDepth;
    private int _depthW, _depthH;
    private nint _vb, _ib, _lines;
    private readonly nint[] _overlays = new nint[3];
    private readonly nint[] _rb = new nint[Ring], _rbCmd = new nint[Ring];
    private readonly long[] _rbFrame = new long[Ring];
    private int _rbHead;

    private sealed class Image
    {
        public nint Surface, Texture;
        public ICompositionImportedGpuImage? Imported;
        public ulong ReleaseNeeded;
        public Task? Pending;
    }
    private Image[] _images = [];
    private bool _timeline;
    private nint _readyEvt, _releasedEvt;
    private ICompositionImportedGpuSemaphore? _readySem, _releasedSem;

    public override string Description { get; }

    public MetalViewer3DBackend()
    {
        _device = MTLCreateSystemDefaultDevice();
        if (_device == 0) throw new Viewer3DPresentFault("This Mac has no Metal device.");
        _queue = Send(_device, S.newCommandQueue);
        if (_queue == 0) throw new Viewer3DPresentFault("Metal returned no command queue.");
        Description = "Metal — " + Marshal.PtrToStringUTF8(Send(Send(_device, S.name), Sel("UTF8String")));
        BuildPipelines();
    }

    private void BuildPipelines()
    {
        nint err = 0;
        nint lib = ((delegate* unmanaged<nint, nint, nint, nint, nint*, nint>)MsgSend)(
            _device, Sel("newLibraryWithSource:options:error:"), NSString(Viewer3DShaders.Metal), 0, &err);
        if (lib == 0) throw new Viewer3DPresentFault("The 3D view's Metal shader did not compile: " + Describe(err));
        nint Fn(string name)
        {
            nint f = Send(lib, Sel("newFunctionWithName:"), NSString(name));
            return f != 0 ? f : throw new Viewer3DPresentFault($"The 3D view's Metal shader has no function '{name}'.");
        }
        nint vs = Fn("vs"), fsc = Fn("fs_color"), fsl = Fn("fs_line"), fsp = Fn("fs_pick");

        nint vd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint attrs = Send(vd, Sel("attributes"));
        void Attr(nuint i, nuint fmt, nuint off)
        {
            nint a = Idx(attrs, i);
            SendV(a, Sel("setFormat:"), fmt); SendV(a, Sel("setOffset:"), off); SendV(a, Sel("setBufferIndex:"), (nuint)0);
        }
        Attr(0, VtxFloat3, 0); Attr(1, VtxUInt, 12); Attr(2, VtxUChar4Normalized, 16);
        SendV(Idx(Send(vd, Sel("layouts")), 0), Sel("setStride:"), (nuint)Scene3DVertex.Stride);

        nint Pipe(nint fs, bool blend, bool pick, nuint topologyClass)
        {
            nint d = Send(Send(Class("MTLRenderPipelineDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setVertexFunction:"), vs);
            SendV(d, Sel("setFragmentFunction:"), fs);
            SendV(d, Sel("setVertexDescriptor:"), vd);
            SendV(d, Sel("setDepthAttachmentPixelFormat:"), FmtDepth32F);
            SendV(d, Sel("setInputPrimitiveTopology:"), topologyClass);
            nint cas = Send(d, Sel("colorAttachments"));
            if (pick)
            {
                SendV(Idx(cas, 0), Sel("setPixelFormat:"), FmtR32Uint);
                SendV(Idx(cas, 1), Sel("setPixelFormat:"), FmtRGBA32Float);
            }
            else
            {
                nint ca = Idx(cas, 0);
                SendV(ca, Sel("setPixelFormat:"), FmtBGRA8);
                if (blend)
                {
                    SendB(ca, Sel("setBlendingEnabled:"), true);
                    SendV(ca, Sel("setSourceRGBBlendFactor:"), (nuint)4);         // SourceAlpha
                    SendV(ca, Sel("setDestinationRGBBlendFactor:"), (nuint)5);    // OneMinusSourceAlpha
                    SendV(ca, Sel("setSourceAlphaBlendFactor:"), (nuint)1);       // One: the image's alpha stays 1
                    SendV(ca, Sel("setDestinationAlphaBlendFactor:"), (nuint)5);
                }
            }
            nint e = 0;
            nint p = ((delegate* unmanaged<nint, nint, nint, nint*, nint>)MsgSend)(_device, Sel("newRenderPipelineStateWithDescriptor:error:"), d, &e);
            Send(d, S.release);
            return p != 0 ? p : throw new Viewer3DPresentFault("The 3D view's Metal pipeline did not build: " + Describe(e));
        }
        const nuint TopoLine = 2, TopoTriangle = 3;
        _pOpaque = Pipe(fsc, false, false, TopoTriangle);
        _pTrans = Pipe(fsc, true, false, TopoTriangle);
        _pLines = Pipe(fsl, false, false, TopoLine);
        _pPick = Pipe(fsp, false, true, TopoTriangle);

        nint Depth(bool write)
        {
            nint d = Send(Send(Class("MTLDepthStencilDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setDepthCompareFunction:"), (nuint)3);   // LessEqual
            SendB(d, Sel("setDepthWriteEnabled:"), write);
            nint s = Send(_device, Sel("newDepthStencilStateWithDescriptor:"), d);
            Send(d, S.release);
            return s;
        }
        _dsWrite = Depth(true);
        _dsNoWrite = Depth(false);
        for (int i = 0; i < Ring; i++)
            _rb[i] = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(_device, Sel("newBufferWithLength:options:"), 256, 0);
        _pickId = NewTexture(1, 1, FmtR32Uint, 4, 0);
        _pickPos = NewTexture(1, 1, FmtRGBA32Float, 4, 0);
        _pickDepth = NewTexture(1, 1, FmtDepth32F, 4, 2);
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        Release(ref _vb); Release(ref _ib); Release(ref _lines);
        fixed (Scene3DVertex* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length * Scene3DVertex.Stride);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = NewBuffer(p, scene.LineVertices.Length * Scene3DVertex.Stride);
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        int i = slot - Scene3DBuffer.Overlay0;
        Release(ref _overlays[i]);
        fixed (Scene3DVertex* p = lines) _overlays[i] = NewBuffer(p, lines.Length * Scene3DVertex.Stride);
    }

    private nint NewBuffer(void* data, int length)
    {
        if (length == 0) return 0;
        Counters.CountUpload(length);
        nint b = ((delegate* unmanaged<nint, nint, void*, nuint, nuint, nint>)MsgSend)(_device, Sel("newBufferWithBytes:length:options:"), data, (nuint)length, 0);
        return b != 0 ? b : throw new Viewer3DPresentFault($"Metal could not allocate a {length:N0}-byte buffer.");
    }

    private static void Release(ref nint o) { if (o != 0) { Send(o, S.release); o = 0; } }

    private nint NewTexture(int w, int h, nuint fmt, nuint usage, nuint storage)
    {
        nint d = ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(Class("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), fmt, (nuint)w, (nuint)h, 0);
        SendV(d, Sel("setUsage:"), usage);
        SendV(d, Sel("setStorageMode:"), storage);
        nint t = Send(_device, Sel("newTextureWithDescriptor:"), d);
        return t != 0 ? t : throw new Viewer3DPresentFault("Metal returned no texture.");
    }

    // ── presentation ────────────────────────────────────────────────────────────────────────

    public override string? CheckInterop(ICompositionGpuInterop interop)
    {
        string images = string.Join(", ", interop.SupportedImageHandleTypes), sems = string.Join(", ", interop.SupportedSemaphoreTypes);
        if (!interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef))
            return $"the compositor cannot import an IOSurface (it offered images [{images}], semaphores [{sems}])";
        var caps = interop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
        _timeline = caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores)
                    && interop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent);
        if (!_timeline && !caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Automatic))
            return $"the compositor's IOSurface synchronisation [{caps}] offers neither timeline semaphores nor automatic";
        return null;
    }

    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count)
    {
        ReleaseImages();
        if (_timeline)
        {
            _readyEvt = Send(_device, Sel("newSharedEvent"));
            _releasedEvt = Send(_device, Sel("newSharedEvent"));
            if (_readyEvt == 0 || _releasedEvt == 0) throw new Viewer3DPresentFault("Metal returned no shared event for the compositor's semaphores.");
            _readySem = interop.ImportSemaphore(new PlatformHandle(_readyEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
            _releasedSem = interop.ImportSemaphore(new PlatformHandle(_releasedEvt, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent));
        }
        _images = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var im = new Image { Surface = IOSurf.CreateBgra(width, height) };
            nint d = ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(Class("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), FmtBGRA8, (nuint)width, (nuint)height, 0);
            SendV(d, Sel("setUsage:"), (nuint)(4 | 1));
            SendV(d, Sel("setStorageMode:"), (nuint)0);
            im.Texture = ((delegate* unmanaged<nint, nint, nint, nint, nuint, nint>)MsgSend)(_device, Sel("newTextureWithDescriptor:iosurface:plane:"), d, im.Surface, 0);
            if (im.Texture == 0) throw new Viewer3DPresentFault("Metal could not make a texture over the shared IOSurface.");
            im.Imported = interop.ImportImage(new PlatformHandle(im.Surface, KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef),
                new PlatformGraphicsExternalImageProperties { Width = width, Height = height, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm, TopLeftOrigin = true });
            _images[i] = im;
        }
    }

    /// <summary>
    /// Tests and headless use: <paramref name="count"/> plain (non-shared) BGRA8 images the backend
    /// renders into exactly as it renders into the compositor's — the same pipelines, buffers and
    /// pick pass — with "automatic" synchronisation (Render waits for its own GPU work). No window is
    /// involved, which is what lets a test drive the real Metal path on a machine with no display.
    /// </summary>
    internal void CreateOffscreenImages(int width, int height, int count)
    {
        ReleaseImages();
        _timeline = false;
        _images = new Image[count];
        for (int i = 0; i < count; i++) _images[i] = new Image { Texture = NewTexture(width, height, FmtBGRA8, 4 | 1, 0) };
        _offW = width; _offH = height;
    }

    private int _offW, _offH;

    /// <summary>Tests: an offscreen image's pixels, RGBA8 rows top to bottom.</summary>
    internal byte[] ReadImage(int image)
    {
        int w = _offW, h = _offH;
        nint buf = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(_device, Sel("newBufferWithLength:options:"), (nuint)(w * h * 4), 0);
        nint pool = PoolPush();
        nint cb = Send(_queue, S.commandBuffer);
        nint blit = Send(cb, S.blitCommandEncoder);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
            blit, S.copyFromTextureToBuffer, _images[image].Texture, 0, 0, default, new MtlSize { W = (nuint)w, H = (nuint)h, D = 1 }, buf, 0, (nuint)(w * 4), (nuint)(w * h * 4));
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

    public override void ReleaseImages()
    {
        foreach (var im in _images)
        {
            Dispose(im.Imported);
            if (im.Texture != 0) Send(im.Texture, S.release);
            if (im.Surface != 0) IOSurf.CFRelease(im.Surface);
        }
        _images = [];
        Dispose(_readySem); Dispose(_releasedSem);
        _readySem = _releasedSem = null;
        Release(ref _readyEvt); Release(ref _releasedEvt);
    }

    private static void Dispose(object? o)
    {
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
        else if (o is IDisposable d) d.Dispose();
    }

    public override bool WaitReusable(int image, int timeoutMs)
    {
        var im = _images[image];
        if (_timeline)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (((delegate* unmanaged<nint, nint, ulong>)MsgSend)(_releasedEvt, S.signaledValue) < im.ReleaseNeeded)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Thread.SpinWait(100);
            }
            return true;
        }
        return im.Pending is not { IsCompleted: false } t || t.Wait(timeoutMs);
    }

    public override void Present(CompositionDrawingSurface surface, int image, ulong frame)
    {
        var im = _images[image];
        if (_timeline)
        {
            surface.UpdateWithTimelineSemaphoresAsync(im.Imported!, _readySem!, frame, _releasedSem!, frame);
            im.ReleaseNeeded = frame;
        }
        else im.Pending = surface.UpdateAsync(im.Imported!);
    }

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        var target = _images[image].Texture;
        if (target == 0) throw new Viewer3DPresentFault("The 3D view's image has no texture.");
        nint pool = PoolPush();
        try
        {
            int draws = 0;
            CollectPicks();
            EnsureDepth(plan.Width, plan.Height);
            nint cb = Send(_queue, S.commandBuffer);
            if (cb == 0) throw new Viewer3DPresentFault("Metal returned no command buffer.");

            fixed (float* pu = plan.PickUniforms)
            fixed (float* u = plan.Uniforms)
            {
                int slot = -1;
                if (plan.Pick && plan.PickDrawCount > 0 && _vb != 0 && _rbCmd[_rbHead % Ring] == 0)
                {
                    slot = _rbHead % Ring;
                    nint rp = Pass(_pickId, _pickDepth, 0, 0, 0, _pickPos);
                    nint enc = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
                    Common(enc, pu);
                    SendV(enc, S.setRenderPipelineState, _pPick);
                    SendV(enc, S.setDepthStencilState, _dsWrite);
                    ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(enc, S.setVertexBuffer, _vb, 0, 0);
                    for (int i = 0; i < plan.PickDrawCount; i++) { DrawIndexed(enc, plan.PickDraws[i]); draws++; }
                    Send(enc, S.endEncoding);
                    nint blit = Send(cb, S.blitCommandEncoder);
                    CopyTexel(blit, _pickId, _rb[slot], 0, 4);
                    CopyTexel(blit, _pickPos, _rb[slot], 16, 16);
                    Send(blit, S.endEncoding);
                }

                var (r, g, b) = plan.Clear;
                nint main = Pass(target, _depth, r, g, b, 0);
                nint e = Send(cb, S.renderCommandEncoderWithDescriptor, main);
                Common(e, u);
                for (int i = 0; i < plan.DrawCount; i++)
                {
                    ref var d = ref plan.Draws[i];
                    nint buf = d.Buffer switch
                    {
                        Scene3DBuffer.Scene => _vb, Scene3DBuffer.SceneLines => _lines,
                        Scene3DBuffer.Overlay0 => _overlays[0], Scene3DBuffer.Overlay1 => _overlays[1], _ => _overlays[2],
                    };
                    if (buf == 0 || (d.Pipeline is Scene3DPipeline.Opaque or Scene3DPipeline.Translucent && _ib == 0)) continue;
                    SendV(e, S.setRenderPipelineState, d.Pipeline switch
                    {
                        Scene3DPipeline.Translucent => _pTrans, Scene3DPipeline.Lines => _pLines, _ => _pOpaque,
                    });
                    SendV(e, S.setDepthStencilState, d.Pipeline == Scene3DPipeline.Translucent ? _dsNoWrite : _dsWrite);
                    ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, buf, 0, 0);
                    if (d.Pipeline == Scene3DPipeline.Lines)
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimLine, (nuint)d.First, (nuint)d.Count);
                    else DrawIndexed(e, d);
                    draws++;
                }
                Send(e, S.endEncoding);

                if (_timeline)
                    ((delegate* unmanaged<nint, nint, nint, ulong, void>)MsgSend)(cb, S.encodeSignalEvent, _readyEvt, frame);
                Send(cb, S.commit);
                if (slot >= 0)
                {
                    _rbCmd[slot] = Send(cb, S.retain);
                    _rbFrame[slot] = Counters.FrameIndex;
                    _rbHead++;
                }
                if (!_timeline) Send(cb, S.waitUntilCompleted);
            }
            DrawCallsLastFrame = draws;
        }
        finally { PoolPop(pool); }
    }

    private static readonly nint Sel_drawPrimitives = Sel("drawPrimitives:vertexStart:vertexCount:");
    private static readonly nint Sel_setFrontFacing = Sel("setFrontFacingWinding:");
    private static readonly nint Class_RPD = Class("MTLRenderPassDescriptor");

    private void Common(nint enc, float* u)
    {
        SendV(enc, Sel_setFrontFacing, WindingCounterClockwise);
        ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setVertexBytes, u, (nuint)Scene3DFramePlan.UniformBytes, 1);
        ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setFragmentBytes, u, (nuint)Scene3DFramePlan.UniformBytes, 1);
        Counters.CountUniform(2 * Scene3DFramePlan.UniformBytes);
    }

    private void DrawIndexed(nint enc, in Scene3DDraw d)
        => ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, nint, nuint, void>)MsgSend)(
               enc, S.drawIndexed, PrimTriangle, (nuint)d.Count, IndexUInt32, _ib, (nuint)(d.First * 4L));

    private static void CopyTexel(nint blit, nint tex, nint buf, nuint offset, nuint bytes)
        => ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
               blit, S.copyFromTextureToBuffer, tex, 0, 0, default, new MtlSize { W = 1, H = 1, D = 1 }, buf, offset, bytes, bytes);

    private static nint Pass(nint color, nint depth, double r, double g, double b, nint color1)
    {
        nint rp = Send(Class_RPD, S.renderPassDescriptor);
        nint cas = Send(rp, S.colorAttachments);
        nint ca = Idx(cas, 0);
        SendV(ca, S.setTexture, color);
        SendV(ca, S.setLoadAction, (nuint)2);
        SendV(ca, S.setStoreAction, (nuint)1);
        ((delegate* unmanaged<nint, nint, ClearColor, void>)MsgSend)(ca, S.setClearColor, new ClearColor { R = r, G = g, B = b, A = 1 });
        if (color1 != 0)
        {
            nint c1 = Idx(cas, 1);
            SendV(c1, S.setTexture, color1);
            SendV(c1, S.setLoadAction, (nuint)2);
            SendV(c1, S.setStoreAction, (nuint)1);
            ((delegate* unmanaged<nint, nint, ClearColor, void>)MsgSend)(c1, S.setClearColor, default);
        }
        nint da = Send(rp, S.depthAttachment);
        SendV(da, S.setTexture, depth);
        SendV(da, S.setLoadAction, (nuint)2);
        SendV(da, S.setStoreAction, (nuint)0);
        SendD(da, S.setClearDepth, 1.0);
        return rp;
    }

    private void EnsureDepth(int w, int h)
    {
        if (w == _depthW && h == _depthH && _depth != 0) return;
        Release(ref _depth);
        _depth = NewTexture(Math.Max(1, w), Math.Max(1, h), FmtDepth32F, 4, 2);
        _depthW = w; _depthH = h;
    }

    private void CollectPicks()
    {
        for (int k = 0; k < Ring; k++)
        {
            int slot = (_rbHead + k) % Ring;
            if (_rbCmd[slot] == 0 || SendU(_rbCmd[slot], S.status) < 4) continue;   // 4 = Completed
            byte* p = (byte*)Send(_rb[slot], S.contents);
            PickedId = *(uint*)p;
            float* w = (float*)(p + 16);
            PickedPoint = new Vector3(w[0], w[1], w[2]);
            PickedSomething = w[3] > 0.5f;
            Send(_rbCmd[slot], S.release);
            _rbCmd[slot] = 0;
            Counters.PickResolved(_rbFrame[slot]);
        }
    }

    public override void Dispose()
    {
        ReleaseImages();
        Release(ref _vb); Release(ref _ib); Release(ref _lines);
        for (int i = 0; i < 3; i++) Release(ref _overlays[i]);
    }
}
