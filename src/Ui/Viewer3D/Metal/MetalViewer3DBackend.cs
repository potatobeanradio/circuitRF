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
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using static CircuitRF.Ui.Viewer3D.Metal.ObjC;

namespace CircuitRF.Ui.Viewer3D.Metal;

internal sealed unsafe class MetalViewer3DBackend : Viewer3DBackend
{
    [StructLayout(LayoutKind.Sequential)] private struct ClearColor { public double R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] private struct MtlOrigin { public nuint X, Y, Z; }
    [StructLayout(LayoutKind.Sequential)] private struct MtlSize { public nuint W, H, D; }
    [StructLayout(LayoutKind.Sequential)] private struct MtlRegion { public MtlOrigin Origin; public MtlSize Size; }

    private const int Ring = 3;
    private const nuint FmtBGRA8 = 80, FmtRG32Uint = 103, FmtRGBA32Float = 125, FmtDepth32F = 252;
    /// <summary>brief-em3d-101 — an image's texture: RGBA8 UNORM, never _sRGB, so a texel reaches the framebuffer with the value a
    /// vertex colour of that value would.</summary>
    private const nuint FmtRGBA8Unorm = 70;
    /// <summary>brief-em3d-106 R-em3d106-4c — the environment's two textures: half-float RGBA, which every Mac Metal device samples
    /// with linear filtering, so no RGBE fallback was needed.</summary>
    private const nuint FmtRGBA16Float = 115;
    /// <summary>brief-em3d-107 — the occlusion's targets: the prepass's depths (R32Float) and the occlusion itself, raw and blurred (R8).</summary>
    private const nuint FmtR32Float = 55, FmtR8Unorm = 10;
    private const nuint VtxFloat3 = 30, VtxUInt = 36, VtxUChar4Normalized = 9, VtxFloat2 = 29;
    private const nuint PrimLine = 1, PrimTriangle = 3, IndexUInt32 = 1;
    private const nuint WindingCounterClockwise = 1;

    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")] private static extern nint MTLCreateSystemDefaultDevice();

    private readonly nint _device, _queue;
    private nint _pOpaque, _pTrans, _pLines, _pPick, _pField, _pEdges, _pTop, _pGrid, _dsWrite, _dsNoWrite, _dsAlways, _depth, _pickId, _pickPos, _pickDepth;
    /// <summary>brief-em3d-101 — the image pipelines (opaque and translucent), the image vertex stream, its one sampler, and the
    /// textures, held by identity (Scene3DTextureResidency): a scene rebuilt with the same files uploads no pixels.</summary>
    private nint _pImage, _pImageTrans, _imageVb, _imageSampler;
    private readonly Scene3DTextureResidency<nint> _textures = new();

    /// <summary>brief-em3d-101 gate 6 — textures this backend has uploaded.</summary>
    internal long TextureUploads => _textures.Uploads;
    /// <summary>brief-em3d-43 — a partial upload waiting for the next frame's command buffer: staging buffer,
    /// destination, offset, length. Copied by a blit on the queue, so it lands after every frame already
    /// committed has finished reading the old bytes.</summary>
    private readonly List<(nint Staging, nint Target, nuint Offset, nuint Length)> _patches = [];
    private nint _field;
    private int _fieldCount;
    private int _depthW, _depthH;
    private nint _vb, _ib, _lines;
    /// <summary>brief-em3d-104 — the shade stream (Scene3DShadeVertex), held only while the realistic view is on; 0 otherwise.
    /// Bound at vertex buffer index 3 by the realistic pipelines (brief 106; scene.wgsl's header has the mapping).</summary>
    private nint _shade;
    /// <summary>brief-em3d-106 — the realistic pipelines (opaque, translucent premultiplied, the backdrop), the appearance table
    /// (fragment buffer 4) and the environment's two textures (fragment textures 1 and 2, sampled through the image sampler at index
    /// 1); every one but the pipelines held only while the realistic view is on.</summary>
    private nint _pPbr, _pPbrTrans, _pBackdrop, _appearances, _envMap, _envBrdf;
    // brief-em3d-109 — the blended field (Exact/Glow below 100 %), the Lit field, and the Lit field's normal stream
    private nint _pFieldBlend, _pFieldLit, _fieldNormals;
    private int _fieldNormalCount;
    /// <summary>brief-em3d-107 — the shadow pass's pipeline (depth only), the occlusion's three (the prepass of the materials and of the
    /// ground, the horizon pass, its blur), the ground's; the shadow map (kept between frames: re-rendered only when the session says),
    /// the occlusion's three targets (the view's size), the comparison sampler, and 1 × 1 stand-ins bound while there is no map or no
    /// occlusion (the uniforms then say neither is read).
    /// The textures are held only while the realistic view is on.</summary>
    private nint _pShadow, _pPrepass, _pGroundPrepass, _pAo, _pAoBlur, _pGround, _shadowSampler;
    private nint _shadowMap, _noShadowMap, _noOcclusion, _aoDepth, _aoRaw, _aoBlur;
    private int _shadowMapSize, _aoW, _aoH;
    private readonly nint[] _overlays = new nint[3];
    private readonly nint[] _rb = new nint[Ring], _rbCmd = new nint[Ring];
    private readonly long[] _rbFrame = new long[Ring];
    private int _rbHead;
    /// <summary>brief-em3d-44 — what each read-back slot's pick was planned with: the patch it becomes.</summary>
    private readonly (int Size, float X, float Y, Camera3D Camera, float W, float H, long Generation, float PerDip)[] _rbMeta = new (int, float, float, Camera3D, float, float, long, float)[Ring];
    private int _pickN = 1;
    /// <summary>Where the positions start in a read-back slot: after the largest patch's (object, face) texels.</summary>
    private const int PosOffset = Scene3DIdPatch.MaxSize * Scene3DIdPatch.MaxSize * 8;
    private const int SlotBytes = PosOffset + Scene3DIdPatch.MaxSize * Scene3DIdPatch.MaxSize * 16;

    public override int MaxPickSize => Scene3DIdPatch.MaxSize;

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

    /// <summary>brief-em3d-96 — the most bytes <c>setVertexBytes</c> / <c>setFragmentBytes</c> may carry: the uniform block goes
    /// in inline (<see cref="Common"/>), so it must stay under this.</summary>
    public const int InlineBytesLimit = 4096;

    /// <summary>The uniform block fits the inline limit — checked where a device is made, so a block grown past it fails the
    /// first 3D view on a Mac, not a frame at random. Also held by Viewer3DFrameGateTests.</summary>
    internal static void AssertUniformsFitInline()
    {
        if (Scene3DFramePlan.UniformBytes > InlineBytesLimit)
            throw new InvalidOperationException($"The 3D view's uniform block is {Scene3DFramePlan.UniformBytes:N0} bytes; Metal takes at most " +
                                                $"{InlineBytesLimit:N0} inline (setVertexBytes).");
    }

    public MetalViewer3DBackend()
    {
        AssertUniformsFitInline();
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
        nint vs = Fn("vs"), fsc = Fn("fs_color"), fsl = Fn("fs_line"), fsp = Fn("fs_pick"), fse = Fn("fs_edge"), fst = Fn("fs_top");
        nint vsf = Fn("vs_field"), fsf = Fn("fs_field"), vsfl = Fn("vs_field_lit"), fsfl = Fn("fs_field_lit");
        nint vsg = Fn("vs_grid"), fsg = Fn("fs_grid");
        nint vsi = Fn("vs_image"), fsi = Fn("fs_image");
        nint vsp = Fn("vs_pbr"), fsp2 = Fn("fs_pbr"), fsb = Fn("fs_backdrop"), fspg = Fn("fs_pbr_glass");
        nint vss = Fn("vs_shadow"), fsd = Fn("fs_depth"), fspre = Fn("fs_prepass"), fsgp = Fn("fs_ground_prepass"), fsao = Fn("fs_ao");
        nint fsaob = Fn("fs_ao_blur"), fsgr = Fn("fs_ground");

        nint vd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint attrs = Send(vd, Sel("attributes"));
        void Attr(nuint i, nuint fmt, nuint off)
        {
            nint a = Idx(attrs, i);
            SendV(a, Sel("setFormat:"), fmt); SendV(a, Sel("setOffset:"), off); SendV(a, Sel("setBufferIndex:"), (nuint)0);
        }
        Attr(0, VtxFloat3, 0); Attr(1, VtxUInt, 12); Attr(2, VtxUChar4Normalized, 16); Attr(3, VtxUInt, 20);
        SendV(Idx(Send(vd, Sel("layouts")), 0), Sel("setStride:"), (nuint)Scene3DVertex.Stride);

        // brief-em3d-29 — the field vertex: position, the value's real part, its imaginary part.
        nint fvd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint fattrs = Send(fvd, Sel("attributes"));
        for (nuint k = 0; k < 3; k++)
        {
            nint a = Idx(fattrs, k);
            SendV(a, Sel("setFormat:"), VtxFloat3); SendV(a, Sel("setOffset:"), 12 * k); SendV(a, Sel("setBufferIndex:"), (nuint)0);
        }
        SendV(Idx(Send(fvd, Sel("layouts")), 0), Sel("setStride:"), (nuint)FieldVertex.Stride);

        // brief-em3d-109 — the Lit field: the field vertex at buffer 0 (attributes 0–2) and its normal at the shade stream's buffer 3
        // (attribute 3).
        nint flvd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint flattrs = Send(flvd, Sel("attributes"));
        for (nuint k = 0; k < 4; k++)
        {
            nint a = Idx(flattrs, k);
            SendV(a, Sel("setFormat:"), VtxFloat3); SendV(a, Sel("setOffset:"), k < 3 ? 12 * k : 0);
            SendV(a, Sel("setBufferIndex:"), k < 3 ? 0 : ShadeBufferIndex);
        }
        SendV(Idx(Send(flvd, Sel("layouts")), 0), Sel("setStride:"), (nuint)FieldVertex.Stride);
        SendV(Idx(Send(flvd, Sel("layouts")), ShadeBufferIndex), Sel("setStride:"), (nuint)FieldShading.Stride);

        // brief-em3d-101 — the image vertex: position, texture coordinate, object id, face, colour (its alpha the object's).
        nint ivd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint iattrs = Send(ivd, Sel("attributes"));
        void IAttr(nuint i, nuint fmt, nuint off)
        {
            nint a = Idx(iattrs, i);
            SendV(a, Sel("setFormat:"), fmt); SendV(a, Sel("setOffset:"), off); SendV(a, Sel("setBufferIndex:"), (nuint)0);
        }
        IAttr(0, VtxFloat3, 0); IAttr(1, VtxFloat2, 12); IAttr(2, VtxUInt, 20); IAttr(3, VtxUInt, 24); IAttr(4, VtxUChar4Normalized, 28);
        SendV(Idx(Send(ivd, Sel("layouts")), 0), Sel("setStride:"), (nuint)Scene3DImageVertex.Stride);

        // brief-em3d-106 — the realistic triangles: the scene's vertex at buffer 0 (attributes 0–3, as vd) and the shade stream at
        // buffer 3 (attribute 4 the normal, 5 the slot) — scene.wgsl's header and tools/ShaderGen's README state the index.
        nint pvd = Send(Class("MTLVertexDescriptor"), Sel("vertexDescriptor"));
        nint pattrs = Send(pvd, Sel("attributes"));
        void PAttr(nuint i, nuint fmt, nuint off, nuint buffer)
        {
            nint a = Idx(pattrs, i);
            SendV(a, Sel("setFormat:"), fmt); SendV(a, Sel("setOffset:"), off); SendV(a, Sel("setBufferIndex:"), buffer);
        }
        PAttr(0, VtxFloat3, 0, 0); PAttr(1, VtxUInt, 12, 0); PAttr(2, VtxUChar4Normalized, 16, 0); PAttr(3, VtxUInt, 20, 0);
        PAttr(4, VtxFloat3, 0, ShadeBufferIndex); PAttr(5, VtxUInt, 12, ShadeBufferIndex);
        SendV(Idx(Send(pvd, Sel("layouts")), 0), Sel("setStride:"), (nuint)Scene3DVertex.Stride);
        SendV(Idx(Send(pvd, Sel("layouts")), ShadeBufferIndex), Sel("setStride:"), (nuint)Scene3DShadeVertex.Stride);

        // brief-em3d-107 — color: the colour target's format (0: none, the shadow pass); depth: false for the occlusion's full-screen passes.
        nint Pipe(nint fs, bool blend, bool pick, nuint topologyClass, nint vfn = 0, nint vdesc = 0, bool premultiplied = false,
                  nuint color = FmtBGRA8, bool depth = true)
        {
            nint d = Send(Send(Class("MTLRenderPipelineDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setVertexFunction:"), vfn != 0 ? vfn : vs);
            SendV(d, Sel("setFragmentFunction:"), fs);
            // brief-em3d-45: the grid's vertex shader reads no vertex buffer — its pipeline has no descriptor (−1).
            SendV(d, Sel("setVertexDescriptor:"), vdesc == -1 ? 0 : vdesc != 0 ? vdesc : vd);
            if (depth) SendV(d, Sel("setDepthAttachmentPixelFormat:"), FmtDepth32F);
            SendV(d, Sel("setInputPrimitiveTopology:"), topologyClass);
            nint cas = Send(d, Sel("colorAttachments"));
            if (pick)
            {
                SendV(Idx(cas, 0), Sel("setPixelFormat:"), FmtRG32Uint);
                SendV(Idx(cas, 1), Sel("setPixelFormat:"), FmtRGBA32Float);
            }
            else if (color != 0)
            {
                nint ca = Idx(cas, 0);
                SendV(ca, Sel("setPixelFormat:"), color);
                if (blend)
                {
                    SendB(ca, Sel("setBlendingEnabled:"), true);
                    // brief-em3d-106 — PbrTranslucent's colour is premultiplied: One, so a reflection on glass adds on top
                    SendV(ca, Sel("setSourceRGBBlendFactor:"), premultiplied ? 1 : (nuint)4);   // One : SourceAlpha
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
        _pField = Pipe(fsf, false, false, TopoTriangle, vsf, fvd);
        _pFieldBlend = Pipe(fsf, true, false, TopoTriangle, vsf, fvd);
        _pFieldLit = Pipe(fsfl, true, false, TopoTriangle, vsfl, flvd);
        _pEdges = Pipe(fse, true, false, TopoLine);
        _pTop = Pipe(fst, true, false, TopoTriangle);
        _pGrid = Pipe(fsg, true, false, TopoTriangle, vsg, -1);
        _pImage = Pipe(fsi, true, false, TopoTriangle, vsi, ivd);
        _pImageTrans = _pImage;
        _pPbr = Pipe(fsp2, false, false, TopoTriangle, vsp, pvd);
        _pPbrTrans = Pipe(fspg, true, false, TopoTriangle, vsp, pvd, premultiplied: true);
        _pBackdrop = Pipe(fsb, false, false, TopoTriangle, vsg, -1);
        // brief-em3d-107 — the shadow map's casters (depth only), the occlusion's prepass (the materials through vs, the ground through
        // vs_grid; depths into R32Float), its horizon pass and blur (R8, no depth), and the ground (blended over the view, its depth tested).
        _pShadow = Pipe(fsd, false, false, TopoTriangle, vss, vd, color: 0);
        _pPrepass = Pipe(fspre, false, false, TopoTriangle, vs, vd, color: FmtR32Float);
        _pGroundPrepass = Pipe(fsgp, false, false, TopoTriangle, vsg, -1, color: FmtR32Float);
        _pAo = Pipe(fsao, false, false, TopoTriangle, vsg, -1, color: FmtR8Unorm, depth: false);
        _pAoBlur = Pipe(fsaob, false, false, TopoTriangle, vsg, -1, color: FmtR8Unorm, depth: false);
        _pGround = Pipe(fsgr, true, false, TopoTriangle, vsg, -1);

        // brief-em3d-101 — one sampler for every image: linear between texels and between mip levels, clamped to the edge (the
        // shader draws nothing outside [0, 1] itself).
        nint sd = Send(Send(Class("MTLSamplerDescriptor"), S.alloc), S.init);
        SendV(sd, Sel("setMinFilter:"), (nuint)1);       // Linear
        SendV(sd, Sel("setMagFilter:"), (nuint)1);
        SendV(sd, Sel("setMipFilter:"), (nuint)2);       // Linear
        SendV(sd, Sel("setSAddressMode:"), (nuint)0);    // ClampToEdge
        SendV(sd, Sel("setTAddressMode:"), (nuint)0);
        _imageSampler = Send(_device, Sel("newSamplerStateWithDescriptor:"), sd);
        Send(sd, S.release);
        if (_imageSampler == 0) throw new Viewer3DPresentFault("Metal returned no sampler for the 3D view's images.");
        // brief-em3d-107 — the shadow map's comparison sampler: lit where the reference is at or before the stored depth, each tap a
        // bilinear blend of four comparisons.
        nint cd = Send(Send(Class("MTLSamplerDescriptor"), S.alloc), S.init);
        SendV(cd, Sel("setMinFilter:"), (nuint)1);
        SendV(cd, Sel("setMagFilter:"), (nuint)1);
        SendV(cd, Sel("setSAddressMode:"), (nuint)0);
        SendV(cd, Sel("setTAddressMode:"), (nuint)0);
        SendV(cd, Sel("setCompareFunction:"), (nuint)3);   // LessEqual
        _shadowSampler = Send(_device, Sel("newSamplerStateWithDescriptor:"), cd);
        Send(cd, S.release);
        if (_shadowSampler == 0) throw new Viewer3DPresentFault("Metal returned no comparison sampler for the 3D view's shadows.");

        nint Depth(bool write, nuint compare = 3)
        {
            nint d = Send(Send(Class("MTLDepthStencilDescriptor"), S.alloc), S.init);
            SendV(d, Sel("setDepthCompareFunction:"), compare);   // 3 LessEqual, 7 Always
            SendB(d, Sel("setDepthWriteEnabled:"), write);
            nint s = Send(_device, Sel("newDepthStencilStateWithDescriptor:"), d);
            Send(d, S.release);
            return s;
        }
        _dsWrite = Depth(true);
        _dsNoWrite = Depth(false);
        _dsAlways = Depth(false, 7);
        for (int i = 0; i < Ring; i++)
            _rb[i] = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(_device, Sel("newBufferWithLength:options:"), (nuint)SlotBytes, 0);
        _pickId = NewTexture(1, 1, FmtRG32Uint, 4, 0);
        _pickPos = NewTexture(1, 1, FmtRGBA32Float, 4, 0);
        _pickDepth = NewTexture(1, 1, FmtDepth32F, 4, 2);

        // The pipelines hold what they need; the library and its functions were ours (new…), the
        // vertex descriptor was not (a class factory's autoreleased object).
        _noShadowMap = NewTexture(1, 1, FmtDepth32F, 4 | 1, 2);
        _noOcclusion = NewTexture(1, 1, FmtR8Unorm, 4 | 1, 2);
        // the stand-ins hold what "none" means — a map with nothing in it (depth 1: lit) and no occlusion (1) — rather than whatever a
        // private texture starts with, in case one is ever read with its uniform switched on
        {
            nint pool = PoolPush();
            nint cb = Send(_queue, S.commandBuffer);
            nint rp = Send(Class_RPD, S.renderPassDescriptor);
            DepthOnly(rp, _noShadowMap);
            Send(Send(cb, S.renderCommandEncoderWithDescriptor, rp), S.endEncoding);
            Send(Send(cb, S.renderCommandEncoderWithDescriptor, Pass(_noOcclusion, 0, 1, 1, 1, 0)), S.endEncoding);
            Send(cb, S.commit);
            Send(cb, S.waitUntilCompleted);
            PoolPop(pool);
        }
        foreach (nint f in new[] { vs, fsc, fsl, fsp, fse, fst, vsf, fsf, vsi, fsi, vsp, fsp2, fsb, fspg, vss, fsd, fspre, fsgp, fsao, fsaob, fsgr })
            Send(f, S.release);
        Send(lib, S.release);
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        foreach (var p in _patches) Send(p.Staging, S.release);
        _patches.Clear();
        Release(ref _vb); Release(ref _ib); Release(ref _lines);
        fixed (Scene3DVertex* p = scene.Vertices) _vb = NewBuffer(p, scene.Vertices.Length * Scene3DVertex.Stride);
        fixed (uint* p = scene.Indices) _ib = NewBuffer(p, scene.Indices.Length * 4);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = NewBuffer(p, scene.LineVertices.Length * Scene3DVertex.Stride);
        Release(ref _imageVb);
        fixed (Scene3DImageVertex* p = scene.ImageVertices) _imageVb = NewBuffer(p, scene.ImageVertices.Length * Scene3DImageVertex.Stride);
        _textures.Sync(scene, UploadTexture, ReleaseTexture);
    }

    /// <summary>brief-em3d-101 R-em3d101-4d — a texture with every one of its CPU-built mip levels, uploaded once per
    /// <see cref="Scene3DTexture"/> (Scene3DTextureResidency decides when). Counted with the buffers.</summary>
    private nint UploadTexture(Scene3DTexture t)
        => NewSampledTexture(FmtRGBA8Unorm, 4, [.. t.Levels().Select(l => (l.Width, l.Height, l.Rgba))]);

    /// <summary>brief-em3d-106 R-em3d106-4c — a sampled 2D texture of <paramref name="format"/> (<paramref name="texelBytes"/> a texel)
    /// with every level given: the upload path brief 101 made, with the format an argument (RGBA8 for an image, RGBA16F for the
    /// environment). Counted with the buffers.</summary>
    private nint NewSampledTexture(nuint format, int texelBytes, IReadOnlyList<(int Width, int Height, byte[] Bytes)> levels)
    {
        nint d = ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, byte, nint>)MsgSend)(Class("MTLTextureDescriptor"),
            Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), format, (nuint)levels[0].Width, (nuint)levels[0].Height, 0);
        SendV(d, Sel("setMipmapLevelCount:"), (nuint)levels.Count);
        SendV(d, Sel("setUsage:"), (nuint)1);                        // ShaderRead
        nint tex = Send(_device, Sel("newTextureWithDescriptor:"), d);
        if (tex == 0) throw new Viewer3DPresentFault($"Metal returned no texture for a {levels[0].Width} × {levels[0].Height} image.");
        for (int k = 0; k < levels.Count; k++)
        {
            var l = levels[k];
            var region = new MtlRegion { Size = new MtlSize { W = (nuint)l.Width, H = (nuint)l.Height, D = 1 } };
            fixed (byte* px = l.Bytes)
                ((delegate* unmanaged<nint, nint, MtlRegion, nuint, void*, nuint, void>)MsgSend)(tex, Sel_replaceRegion, region, (nuint)k, px, (nuint)(l.Width * texelBytes));
            Counters.CountUpload(l.Bytes.Length);
        }
        return tex;
    }

    // ── the realistic view's lighting (brief-em3d-106) ─────────────────────────────────────────────────────────────────

    /// <summary>The shade stream's vertex buffer index: 0 the geometry, 1 the uniforms, 2 the per-draw transform (one argument table).</summary>
    private const nuint ShadeBufferIndex = 3;
    /// <summary>The appearance table's fragment buffer index (tools/ShaderGen: [[buffer(4)]]).</summary>
    private const nuint AppearanceBufferIndex = 4;

    public override void UploadAppearances(float[] table)
    {
        Release(ref _appearances);
        fixed (float* p = table) _appearances = NewBuffer(p, table.Length * 4);
    }

    public override void UploadEnvironment(PrefilteredEnvironment environment)
    {
        Release(ref _envMap); Release(ref _envBrdf);
        _envMap = NewSampledTexture(FmtRGBA16Float, 8, [.. environment.Levels.Select(l => (l.Width, l.Height, l.Bytes.ToArray()))]);
        _envBrdf = NewSampledTexture(FmtRGBA16Float, 8,
            [(Pbr.BrdfSize, Pbr.BrdfSize,
              System.Runtime.InteropServices.MemoryMarshal.AsBytes(environment.BrdfTable.AsSpan()).ToArray())]);
    }

    /// <summary>A command buffer already encoded retains what it reads, so releasing here is safe mid-flight. brief-em3d-107 — the shadow
    /// map and the occlusion's targets go too.</summary>
    public override void ReleaseEnvironment()
    {
        Release(ref _envMap); Release(ref _envBrdf); Release(ref _appearances);
        Release(ref _shadowMap); Release(ref _aoDepth); Release(ref _aoRaw); Release(ref _aoBlur);
        (_shadowMapSize, _aoW, _aoH) = (0, 0, 0);
    }

    // ── brief-em3d-107: the shadow map, the occlusion, the ground ──────────────────────────────────────────────────────────

    /// <summary>R-em3d107-1a — the key light's map at <paramref name="size"/>² (made again only when the size changes).</summary>
    private void EnsureShadowMap(int size)
    {
        if (size == _shadowMapSize && _shadowMap != 0) return;
        Release(ref _shadowMap);
        _shadowMap = NewTexture(size, size, FmtDepth32F, 4 | 1, 2);
        _shadowMapSize = size;
    }

    /// <summary>R-em3d107-2a — the occlusion's three targets at the view's size.</summary>
    private void EnsureOcclusion(int w, int h)
    {
        if (w == _aoW && h == _aoH && _aoDepth != 0) return;
        Release(ref _aoDepth); Release(ref _aoRaw); Release(ref _aoBlur);
        _aoDepth = NewTexture(w, h, FmtR32Float, 4 | 1, 2);
        _aoRaw = NewTexture(w, h, FmtR8Unorm, 4 | 1, 2);
        _aoBlur = NewTexture(w, h, FmtR8Unorm, 4 | 1, 2);
        (_aoW, _aoH) = (w, h);
    }

    /// <summary>The shadow map (or the 1 × 1 stand-in), its sampler and the blurred occlusion for a lit fragment: textures 3 and 4, sampler
    /// 2 (scene.wgsl's group 3). The uniforms say whether either is read.</summary>
    private void BindLighting(nint e)
    {
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentTexture, _shadowMap != 0 ? _shadowMap : _noShadowMap, 3);
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentSampler, _shadowSampler, 2);
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentTexture, _aoBlur != 0 ? _aoBlur : _noOcclusion, 4);
    }

    /// <summary>R-em3d107-1 — the casters into the map, from the key light (the plan's lvp), with the shadow bias.</summary>
    private void EncodeShadowPass(nint cb, Scene3DFramePlan plan, float* u, float* xf, ref int draws)
    {
        EnsureShadowMap(plan.ShadowSize);
        nint rp = Send(Class_RPD, S.renderPassDescriptor);
        DepthOnly(rp, _shadowMap);
        nint e = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
        Common(e, u, xf);
        SendV(e, S.setRenderPipelineState, _pShadow);
        SendV(e, S.setDepthStencilState, _dsWrite);
        var (constant, slope, clamp) = Scene3DFramePlan.ShadowBias;
        ((delegate* unmanaged<nint, nint, float, float, float, void>)MsgSend)(e, Sel_setDepthBias, constant, slope, clamp);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _vb, 0, 0);
        int transform = 0;
        for (int i = 0; i < plan.ShadowDrawCount; i++)
        {
            ref var d = ref plan.ShadowDraws[i];
            if (d.Transform != transform && d.Transform < plan.TransformCount) SetTransform(e, xf, transform = d.Transform);
            DrawIndexed(e, d);
            draws++;
        }
        Send(e, S.endEncoding);
    }

    /// <summary>R-em3d107-2 — the occlusion: the prepass of the opaque materials (and the ground) into the depths target, the horizon pass,
    /// the blur. The main pass's depth texture serves the prepass; the main pass clears it again.</summary>
    private void EncodeOcclusion(nint cb, Scene3DFramePlan plan, float* u, float* xf, ref int draws)
    {
        EnsureOcclusion(plan.Width, plan.Height);
        nint rp = Pass(_aoDepth, _depth, Occlusion.Empty, 0, 0, 0);
        nint e = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
        Common(e, u, xf);
        SendV(e, S.setRenderPipelineState, _pPrepass);
        SendV(e, S.setDepthStencilState, _dsWrite);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _vb, 0, 0);
        int transform = 0;
        for (int i = 0; i < plan.DrawCount; i++)
        {
            ref var d = ref plan.Draws[i];
            if (d.Pipeline != Scene3DPipeline.Pbr) continue;
            if (d.Transform != transform && d.Transform < plan.TransformCount) SetTransform(e, xf, transform = d.Transform);
            DrawIndexed(e, d);
            draws++;
        }
        if (plan.GroundDrawn)
        {
            SendV(e, S.setRenderPipelineState, _pGroundPrepass);
            ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, 0, 6);
            draws++;
        }
        Send(e, S.endEncoding);
        foreach (var (pipe, target) in new[] { (_pAo, _aoRaw), (_pAoBlur, _aoBlur) })
        {
            nint pass = Send(Class_RPD, S.renderPassDescriptor);
            nint ca = Idx(Send(pass, S.colorAttachments), 0);
            SendV(ca, S.setTexture, target);
            SendV(ca, S.setLoadAction, (nuint)0);     // DontCare: every pixel is written
            SendV(ca, S.setStoreAction, (nuint)1);
            nint f = Send(cb, S.renderCommandEncoderWithDescriptor, pass);
            Common(f, u, xf);
            SendV(f, S.setRenderPipelineState, pipe);
            ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(f, Sel_setFragmentTexture, _aoDepth, 5);
            if (pipe == _pAoBlur) ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(f, Sel_setFragmentTexture, _aoRaw, 6);
            ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(f, Sel_drawPrimitives, PrimTriangle, 0, 6);
            draws++;
            Send(f, S.endEncoding);
        }
    }

    private static void DepthOnly(nint rp, nint depth)
    {
        nint da = Send(rp, S.depthAttachment);
        SendV(da, S.setTexture, depth);
        SendV(da, S.setLoadAction, (nuint)2);
        SendV(da, S.setStoreAction, (nuint)1);
        SendD(da, S.setClearDepth, 1.0);
    }

    private static readonly nint Sel_setFragmentBuffer = Sel("setFragmentBuffer:offset:atIndex:");

    /// <summary>The environment, its sampler and the appearance table for the fragment stage: false when the view has not got them
    /// yet (the draw is skipped, never drawn unlit).</summary>
    private bool BindLook(nint e)
    {
        if (_envMap == 0 || _envBrdf == 0 || _appearances == 0) return false;
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentTexture, _envMap, 1);
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentTexture, _envBrdf, 2);
        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentSampler, _imageSampler, 1);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, Sel_setFragmentBuffer, _appearances, 0, AppearanceBufferIndex);
        return true;
    }

    private static void ReleaseTexture(nint tex) { if (tex != 0) Send(tex, S.release); }

    private static readonly nint Sel_replaceRegion = Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");
    private static readonly nint Sel_setFragmentTexture = Sel("setFragmentTexture:atIndex:");
    private static readonly nint Sel_setFragmentSampler = Sel("setFragmentSamplerState:atIndex:");

    /// <summary>brief-em3d-43 gate 6 — only the changed ranges, staged now and copied on the queue by the next
    /// frame (so never under a frame still reading them). Every staged byte is counted.</summary>
    public override void PatchScene(Scene3DModel scene, Scene3DPatch patch)
    {
        foreach (var r in patch.Ranges)
        {
            nint target = r.Buffer switch { Scene3DPatchBuffer.Vertices => _vb, Scene3DPatchBuffer.Indices => _ib, _ => _lines };
            if (target == 0) { UploadScene(scene); return; }
            nint staging;
            fixed (byte* p = Scene3DPatch.Source(scene, r)) staging = NewBuffer(p, r.ByteLength);
            _patches.Add((staging, target, (nuint)r.ByteOffset, (nuint)r.ByteLength));
        }
    }

    /// <summary>brief-em3d-104 R-em3d104-3a — the whole shade stream, as UploadScene uploads the vertices.</summary>
    public override void UploadShade(Scene3DModel scene)
    {
        ReleaseShade();
        fixed (Scene3DShadeVertex* p = scene.ShadeVertices) _shade = NewBuffer(p, scene.ShadeVertices.Length * Scene3DShadeVertex.Stride);
    }

    /// <summary>The shade stream's changed ranges, staged and copied on the queue by the next frame, as PatchScene's are.</summary>
    public override void PatchShade(Scene3DModel scene, Scene3DPatch patch)
    {
        if (patch.ShadeRanges.Count > 0 && _shade == 0) { UploadShade(scene); return; }
        foreach (var r in patch.ShadeRanges)
        {
            nint staging;
            fixed (byte* p = Scene3DPatch.Source(scene, r)) staging = NewBuffer(p, r.ByteLength);
            _patches.Add((staging, _shade, (nuint)r.ByteOffset, (nuint)r.ByteLength));
        }
    }

    /// <summary>A command buffer already encoded retains the buffer it reads; a patch still staged for it is dropped with it.</summary>
    public override void ReleaseShade()
    {
        if (_shade == 0) return;
        for (int k = _patches.Count - 1; k >= 0; k--)
            if (_patches[k].Target == _shade) { Send(_patches[k].Staging, S.release); _patches.RemoveAt(k); }
        Release(ref _shade);
    }

    private static readonly nint Sel_copyBuffer = Sel("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:");

    /// <summary>Encodes the staged patches at the head of <paramref name="cb"/> and lets the staging buffers go
    /// (the command buffer retains what it references).</summary>
    private void EncodePatches(nint cb)
    {
        if (_patches.Count == 0) return;
        nint blit = Send(cb, S.blitCommandEncoder);
        foreach (var (staging, target, offset, length) in _patches)
            ((delegate* unmanaged<nint, nint, nint, nuint, nint, nuint, nuint, void>)MsgSend)(blit, Sel_copyBuffer, staging, 0, target, offset, length);
        Send(blit, S.endEncoding);
        foreach (var p in _patches) Send(p.Staging, S.release);
        _patches.Clear();
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        int i = slot - Scene3DBuffer.Overlay0;
        Release(ref _overlays[i]);
        fixed (Scene3DVertex* p = lines) _overlays[i] = NewBuffer(p, lines.Length * Scene3DVertex.Stride);
    }

    public override void UploadField(FieldVertex[] vertices)
    {
        Release(ref _field);
        fixed (FieldVertex* p = vertices) _field = NewBuffer(p, vertices.Length * FieldVertex.Stride);
        _fieldCount = vertices.Length;
    }

    public override void UploadFieldNormals(float[] normals)
    {
        Release(ref _fieldNormals);
        fixed (float* p = normals) _fieldNormals = NewBuffer(p, normals.Length * 4);
        _fieldNormalCount = normals.Length / FieldShading.Floats;
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
            ObservePresent(_readySem.ImportCompleted);
            ObservePresent(_releasedSem.ImportCompleted);
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
            ObservePresent(im.Imported.ImportCompleted);   // designer feedback round 34: a lost context fails HERE first
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
    internal byte[] ReadImage(int image) => Read(_images[image].Texture, _offW, _offH);

    /// <summary>A BGRA8 texture's pixels as RGBA8, rows top to bottom.</summary>
    private byte[] Read(nint texture, int w, int h)
    {
        nint buf = ((delegate* unmanaged<nint, nint, nuint, nuint, nint>)MsgSend)(_device, Sel("newBufferWithLength:options:"), (nuint)(w * h * 4), 0);
        nint pool = PoolPush();
        nint cb = Send(_queue, S.commandBuffer);
        nint blit = Send(cb, S.blitCommandEncoder);
        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
            blit, S.copyFromTextureToBuffer, texture, 0, 0, default, new MtlSize { W = (nuint)w, H = (nuint)h, D = 1 }, buf, 0, (nuint)(w * 4), (nuint)(w * h * 4));
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
        if (o is IAsyncDisposable ad) Viewer3DBackend.ObserveDisposal(ad.DisposeAsync());
        else if (o is IDisposable d) d.Dispose();
    }

    public override bool WaitReusable(int image, int timeoutMs)
    {
        var im = _images[image];
        if (_timeline)
        {
            // brief-idle-power: a blocking wait (MTLSharedEvent, macOS 12+), not a SpinWait loop. The common case returns at
            // once (the image was released frames ago); the rare one — a compositor holding the image, as a minimised window's
            // may — used to burn a core for the whole timeout.
            if (((delegate* unmanaged<nint, nint, ulong>)MsgSend)(_releasedEvt, S.signaledValue) >= im.ReleaseNeeded) return true;
            return ((delegate* unmanaged<nint, nint, ulong, ulong, byte>)MsgSend)(
                _releasedEvt, S.waitUntilSignaledValue, im.ReleaseNeeded, (ulong)Math.Max(0, timeoutMs)) != 0;
        }
        return im.Pending is not { IsCompleted: false } t || t.Wait(timeoutMs);
    }

    public override void Present(CompositionDrawingSurface surface, int image, ulong frame)
    {
        var im = _images[image];
        if (_timeline)
        {
            ObservePresent(surface.UpdateWithTimelineSemaphoresAsync(im.Imported!, _readySem!, frame, _releasedSem!, frame));
            im.ReleaseNeeded = frame;
        }
        else im.Pending = ObservePresent(surface.UpdateAsync(im.Imported!));
    }

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        var target = _images[image].Texture;
        if (target == 0) throw new Viewer3DPresentFault("The 3D view's image has no texture.");
        RenderInto(target, plan, frame, signal: true, wait: false);
    }

    /// <summary>brief-em3d-29 R-em3d29-5 — the plan drawn into a texture of its own, read back. The same
    /// pipelines and buffers as the view; the swapchain is untouched.</summary>
    public override byte[] RenderPixels(Scene3DFramePlan plan)
    {
        int w = plan.Width, h = plan.Height;
        nint tex = NewTexture(w, h, FmtBGRA8, 4 | 1, 0);
        try
        {
            RenderInto(tex, plan, 0, signal: false, wait: true);
            return Read(tex, w, h);
        }
        finally { Send(tex, S.release); }
    }

    private void RenderInto(nint target, Scene3DFramePlan plan, ulong frame, bool signal, bool wait)
    {
        nint pool = PoolPush();
        try
        {
            int draws = 0;
            CollectPicks();
            EnsureDepth(plan.Width, plan.Height);
            nint cb = Send(_queue, S.commandBuffer);
            if (cb == 0) throw new Viewer3DPresentFault("Metal returned no command buffer.");
            EncodePatches(cb);

            fixed (float* pu = plan.PickUniforms)
            fixed (float* u = plan.Uniforms)
            fixed (float* xf = plan.Transforms)
            {
                int slot = -1;
                if (plan.Pick && plan.PickDrawCount > 0 && _vb != 0 && _rbCmd[_rbHead % Ring] == 0)
                {
                    slot = _rbHead % Ring;
                    int n = Math.Clamp(plan.PickSize | 1, 1, Scene3DIdPatch.MaxSize);
                    EnsurePick(n);
                    _rbMeta[slot] = (n, plan.PickCursorX, plan.PickCursorY, plan.PickCamera, plan.Width, plan.Height,
                                     plan.SceneGeneration, plan.PickPixelsPerDip);
                    nint rp = Pass(_pickId, _pickDepth, 0, 0, 0, _pickPos);
                    nint enc = Send(cb, S.renderCommandEncoderWithDescriptor, rp);
                    Common(enc, pu, xf);
                    SendV(enc, S.setRenderPipelineState, _pPick);
                    SendV(enc, S.setDepthStencilState, _dsWrite);
                    ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(enc, S.setVertexBuffer, _vb, 0, 0);
                    int pickTransform = 0;
                    var pickTie = Scene3DDepthTie.None;
                    for (int i = 0; i < plan.PickDrawCount; i++)
                    {
                        // brief-em3d-48 — an array element's pick draw: its translation and id offset.
                        ref var pd = ref plan.PickDraws[i];
                        if (pd.Tie != pickTie) SetDepthBias(enc, pickTie = pd.Tie);
                        if (pd.Transform != pickTransform && pd.Transform < plan.TransformCount)
                        {
                            pickTransform = pd.Transform;
                            SetTransform(enc, xf, pickTransform);
                        }
                        DrawIndexed(enc, pd);
                        draws++;
                    }
                    Send(enc, S.endEncoding);
                    nint blit = Send(cb, S.blitCommandEncoder);
                    CopyRegion(blit, _pickId, _rb[slot], 0, 8, n);
                    CopyRegion(blit, _pickPos, _rb[slot], PosOffset, 16, n);
                    Send(blit, S.endEncoding);
                }

                // brief-em3d-107 — the shadow map when its key moved (the session decides), and the occlusion every realistic frame
                if (plan.Realistic && _vb != 0 && _ib != 0)
                {
                    if (plan.ShadowPass && plan.ShadowDrawCount > 0) EncodeShadowPass(cb, plan, u, xf, ref draws);
                    if (plan.Occlusion) EncodeOcclusion(cb, plan, u, xf, ref draws);
                }
                var (r, g, b) = plan.Clear;
                nint main = Pass(target, _depth, r, g, b, 0, plan.Transparent ? 0 : 1);
                nint e = Send(cb, S.renderCommandEncoderWithDescriptor, main);
                Common(e, u, xf);
                int transform = 0;
                var tie = Scene3DDepthTie.None;
                for (int i = 0; i < plan.DrawCount; i++)
                {
                    ref var d = ref plan.Draws[i];
                    if (d.Tie != tie) SetDepthBias(e, tie = d.Tie);
                    if (d.Transform != transform && d.Transform < plan.TransformCount)
                    {
                        // brief-em3d-46 — a drag's preview (brief 48: an array element): this draw's slot, set only when it changes.
                        transform = d.Transform;
                        SetTransform(e, xf, transform);
                    }
                    nint buf = d.Buffer switch
                    {
                        Scene3DBuffer.Scene => _vb, Scene3DBuffer.SceneLines => _lines, Scene3DBuffer.Field => _field,
                        Scene3DBuffer.Overlay0 => _overlays[0], Scene3DBuffer.Overlay1 => _overlays[1], _ => _overlays[2],
                    };
                    if (d.Pipeline == Scene3DPipeline.Grid)
                    {
                        // brief-em3d-45 — six vertices from the uniform block; no buffer is bound.
                        SendV(e, S.setRenderPipelineState, _pGrid);
                        SendV(e, S.setDepthStencilState, _dsNoWrite);
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, 0, 6);
                        draws++;
                        continue;
                    }
                    if (d.Pipeline == Scene3DPipeline.Ground)
                    {
                        // brief-em3d-107 — the ground: vs_grid's six vertices casting rays at its plane, darkening only.
                        BindLighting(e);
                        SendV(e, S.setRenderPipelineState, _pGround);
                        SendV(e, S.setDepthStencilState, _dsNoWrite);
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, 0, 6);
                        draws++;
                        continue;
                    }
                    if (d.Pipeline == Scene3DPipeline.Backdrop)
                    {
                        // brief-em3d-106 — the backdrop: vs_grid's six vertices, no buffer, no depth.
                        if (!BindLook(e)) continue;
                        SendV(e, S.setRenderPipelineState, _pBackdrop);
                        SendV(e, S.setDepthStencilState, _dsAlways);
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, 0, 6);
                        draws++;
                        continue;
                    }
                    if (d.Pipeline is Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent)
                    {
                        // brief-em3d-106 — the scene's triangles with the shade stream beside them, lit.
                        if (_vb == 0 || _ib == 0 || _shade == 0 || !BindLook(e)) continue;
                        BindLighting(e);
                        SendV(e, S.setRenderPipelineState, d.Pipeline == Scene3DPipeline.Pbr ? _pPbr : _pPbrTrans);
                        SendV(e, S.setDepthStencilState, d.Pipeline == Scene3DPipeline.Pbr ? _dsWrite : _dsNoWrite);
                        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _vb, 0, 0);
                        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _shade, 0, ShadeBufferIndex);
                        DrawIndexed(e, d);
                        draws++;
                        continue;
                    }
                    if (d.Pipeline == Scene3DPipeline.FieldLit)
                    {
                        // brief-em3d-109 — the Lit field: its vertices with their normals beside them, the environment bound for the sheen.
                        if (_field == 0 || _fieldNormals == 0 || d.First + d.Count > Math.Min(_fieldCount, _fieldNormalCount) || !BindLook(e)) continue;
                        SendV(e, S.setRenderPipelineState, _pFieldLit);
                        SendV(e, S.setDepthStencilState, _dsWrite);
                        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _field, 0, 0);
                        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _fieldNormals, 0, ShadeBufferIndex);
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, (nuint)d.First, (nuint)d.Count);
                        draws++;
                        continue;
                    }
                    if (d.Pipeline is Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent)
                    {
                        // brief-em3d-101 — an image: its texture and the one sampler, then its triangles (not indexed).
                        var bound = _textures.Bound;
                        if (_imageVb == 0 || d.Texture < 0 || d.Texture >= bound.Length || bound[d.Texture] == 0) continue;
                        SendV(e, S.setRenderPipelineState, d.Pipeline == Scene3DPipeline.Image ? _pImage : _pImageTrans);
                        SendV(e, S.setDepthStencilState, d.Pipeline == Scene3DPipeline.Image ? _dsWrite : _dsNoWrite);
                        ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, _imageVb, 0, 0);
                        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentTexture, bound[d.Texture], 0);
                        ((delegate* unmanaged<nint, nint, nint, nuint, void>)MsgSend)(e, Sel_setFragmentSampler, _imageSampler, 0);
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives, PrimTriangle, (nuint)d.First, (nuint)d.Count);
                        draws++;
                        continue;
                    }
                    if (buf == 0 || (d.Pipeline is Scene3DPipeline.Opaque or Scene3DPipeline.Translucent or Scene3DPipeline.OnTop && _ib == 0)) continue;
                    if (d.Pipeline is Scene3DPipeline.Field or Scene3DPipeline.FieldBlend && d.First + d.Count > _fieldCount) continue;
                    SendV(e, S.setRenderPipelineState, d.Pipeline switch
                    {
                        Scene3DPipeline.Translucent => _pTrans, Scene3DPipeline.Lines => _pLines,
                        Scene3DPipeline.Field => _pField, Scene3DPipeline.FieldBlend => _pFieldBlend, Scene3DPipeline.Edges => _pEdges,
                        Scene3DPipeline.OnTop => _pTop, _ => _pOpaque,
                    });
                    SendV(e, S.setDepthStencilState, d.Pipeline switch
                    {
                        Scene3DPipeline.Translucent => _dsNoWrite,
                        Scene3DPipeline.Edges or Scene3DPipeline.OnTop => _dsAlways,
                        _ => _dsWrite,
                    });
                    ((delegate* unmanaged<nint, nint, nint, nuint, nuint, void>)MsgSend)(e, S.setVertexBuffer, buf, 0, 0);
                    if (d.Pipeline is Scene3DPipeline.Lines or Scene3DPipeline.Field or Scene3DPipeline.FieldBlend or Scene3DPipeline.Edges)
                        ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, void>)MsgSend)(e, Sel_drawPrimitives,
                            d.Pipeline is Scene3DPipeline.Field or Scene3DPipeline.FieldBlend ? PrimTriangle : PrimLine, (nuint)d.First, (nuint)d.Count);
                    else DrawIndexed(e, d);
                    draws++;
                }
                Send(e, S.endEncoding);

                if (_timeline && signal)
                    ((delegate* unmanaged<nint, nint, nint, ulong, void>)MsgSend)(cb, S.encodeSignalEvent, _readyEvt, frame);
                Send(cb, S.commit);
                if (slot >= 0)
                {
                    _rbCmd[slot] = Send(cb, S.retain);
                    _rbFrame[slot] = Counters.FrameIndex;
                    _rbHead++;
                }
                if (!_timeline || wait) Send(cb, S.waitUntilCompleted);
            }
            DrawCallsLastFrame = draws;
        }
        finally { PoolPop(pool); }
    }

    private static readonly nint Sel_drawPrimitives = Sel("drawPrimitives:vertexStart:vertexCount:");
    private static readonly nint Sel_setDepthBias = Sel("setDepthBias:slopeScale:clamp:");

    /// <summary>3D editor round 3 / bugs round 9 — a draw's polygon offset for its tie (Scene3DFramePlan.DepthBias): encoder
    /// state, so it is set only when it changes between draws.</summary>
    private static void SetDepthBias(nint enc, Scene3DDepthTie tie)
    {
        var (constant, slope, clamp) = Scene3DFramePlan.DepthBias(tie);
        ((delegate* unmanaged<nint, nint, float, float, float, void>)MsgSend)(enc, Sel_setDepthBias, constant, slope, clamp);
    }
    private static readonly nint Sel_setFrontFacing = Sel("setFrontFacingWinding:");
    private static readonly nint Class_RPD = Class("MTLRenderPassDescriptor");

    private void Common(nint enc, float* u, float* xf)
    {
        SendV(enc, Sel_setFrontFacing, WindingCounterClockwise);
        ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setVertexBytes, u, (nuint)Scene3DFramePlan.UniformBytes, 1);
        ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setFragmentBytes, u, (nuint)Scene3DFramePlan.UniformBytes, 1);
        Counters.CountUniform(2 * Scene3DFramePlan.UniformBytes);
        SetTransform(enc, xf, 0);
    }

    /// <summary>brief-em3d-46 — the vertex stage's per-draw transform (buffer 2): slot <paramref name="slot"/> of the plan's.</summary>
    private void SetTransform(nint enc, float* xf, int slot)
    {
        ((delegate* unmanaged<nint, nint, void*, nuint, nuint, void>)MsgSend)(enc, S.setVertexBytes, xf + Scene3DFramePlan.TransformFloats * slot,
            (nuint)Scene3DFramePlan.TransformBytesPerDraw, 2);
        Counters.CountUniform(Scene3DFramePlan.TransformBytesPerDraw);
    }

    private void DrawIndexed(nint enc, in Scene3DDraw d)
        => ((delegate* unmanaged<nint, nint, nuint, nuint, nuint, nint, nuint, void>)MsgSend)(
               enc, S.drawIndexed, PrimTriangle, (nuint)d.Count, IndexUInt32, _ib, (nuint)(d.First * 4L));

    /// <summary>An n × n texture into a buffer at <paramref name="offset"/>, rows of n texels.</summary>
    private static void CopyRegion(nint blit, nint tex, nint buf, nuint offset, int texelBytes, int n)
        => ((delegate* unmanaged<nint, nint, nint, nuint, nuint, MtlOrigin, MtlSize, nint, nuint, nuint, nuint, void>)MsgSend)(
               blit, S.copyFromTextureToBuffer, tex, 0, 0, default, new MtlSize { W = (nuint)n, H = (nuint)n, D = 1 }, buf, offset,
               (nuint)(n * texelBytes), (nuint)(n * n * texelBytes));

    /// <summary>brief-em3d-44 R-em3d44-2a — the ID pass's targets at the patch's size (made again only when it changes).</summary>
    private void EnsurePick(int n)
    {
        if (n == _pickN && _pickId != 0) return;
        Release(ref _pickId); Release(ref _pickPos); Release(ref _pickDepth);
        _pickId = NewTexture(n, n, FmtRG32Uint, 4, 0);
        _pickPos = NewTexture(n, n, FmtRGBA32Float, 4, 0);
        _pickDepth = NewTexture(n, n, FmtDepth32F, 4, 2);
        _pickN = n;
    }

    private static nint Pass(nint color, nint depth, double r, double g, double b, nint color1, double a = 1)
    {
        nint rp = Send(Class_RPD, S.renderPassDescriptor);
        nint cas = Send(rp, S.colorAttachments);
        nint ca = Idx(cas, 0);
        SendV(ca, S.setTexture, color);
        SendV(ca, S.setLoadAction, (nuint)2);
        SendV(ca, S.setStoreAction, (nuint)1);
        ((delegate* unmanaged<nint, nint, ClearColor, void>)MsgSend)(ca, S.setClearColor, new ClearColor { R = r, G = g, B = b, A = a });
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
            var (n, cx, cy, cam, pw, ph, gen, perDip) = _rbMeta[slot];
            // The centre texel is the cursor's pixel: what the 1 × 1 pass read before the patch.
            int centre = (n / 2) * n + n / 2;
            uint* ids = (uint*)p;
            float* pos = (float*)(p + PosOffset);
            PickedId = ids[2 * centre];
            PickedFace = ids[2 * centre + 1];
            PickedPoint = new Vector3(pos[4 * centre], pos[4 * centre + 1], pos[4 * centre + 2]);
            PickedSomething = pos[4 * centre + 3] > 0.5f;
            var patch = PickPatch ??= new Scene3DIdPatch();
            patch.Begin(n, cx, cy, cam, pw, ph, gen, perDip);
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int t = j * n + i;
                    patch.Set(i, j, ids[2 * t], ids[2 * t + 1], new Vector3(pos[4 * t], pos[4 * t + 1], pos[4 * t + 2]), pos[4 * t + 3] > 0.5f);
                }
            Send(_rbCmd[slot], S.release);
            _rbCmd[slot] = 0;
            Counters.PickResolved(_rbFrame[slot]);
        }
    }

    /// <summary>Everything this backend made or retained — a closed 3D tab otherwise kept the device, its
    /// pipelines and a full-size depth texture alive for the rest of the session.</summary>
    public override void Dispose()
    {
        for (int i = 0; i < Ring; i++)
        {
            if (_rbCmd[i] == 0) continue;
            Send(_rbCmd[i], S.waitUntilCompleted);      // its blit writes a readback buffer freed below
            Release(ref _rbCmd[i]);
        }
        ReleaseImages();
        Release(ref _vb); Release(ref _ib); Release(ref _lines); Release(ref _field); Release(ref _imageVb); Release(ref _shade);
        Release(ref _fieldNormals);
        ReleaseEnvironment();
        _textures.Clear(ReleaseTexture);
        for (int i = 0; i < 3; i++) Release(ref _overlays[i]);
        for (int i = 0; i < Ring; i++) Release(ref _rb[i]);
        Release(ref _depth); Release(ref _pickId); Release(ref _pickPos); Release(ref _pickDepth);
        foreach (var p in _patches) Send(p.Staging, S.release);
        _patches.Clear();
        Release(ref _pOpaque); Release(ref _pTrans); Release(ref _pLines); Release(ref _pPick); Release(ref _pField);
        Release(ref _pEdges); Release(ref _pTop); Release(ref _pGrid); Release(ref _pImage); Release(ref _imageSampler);
        Release(ref _pPbr); Release(ref _pPbrTrans); Release(ref _pBackdrop); Release(ref _pFieldBlend); Release(ref _pFieldLit);
        Release(ref _pShadow); Release(ref _pPrepass); Release(ref _pGroundPrepass); Release(ref _pAo); Release(ref _pAoBlur);
        Release(ref _pGround); Release(ref _shadowSampler); Release(ref _noShadowMap); Release(ref _noOcclusion);
        Release(ref _dsWrite); Release(ref _dsNoWrite); Release(ref _dsAlways);
        if (_queue != 0) Send(_queue, S.release);
        if (_device != 0) Send(_device, S.release);
    }
}
