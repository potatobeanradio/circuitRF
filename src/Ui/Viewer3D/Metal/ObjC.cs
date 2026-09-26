using System.Runtime.InteropServices;

namespace CircuitRF.Ui.Viewer3D.Metal;

/// <summary>
/// brief-em3d-28 (lifted from tools/Viewer3dSpike, route A) — just enough of the Objective-C runtime to drive Metal, IOSurface and CoreFoundation from C#
/// with no binding package: objc_msgSend cast to the exact signature at each call site (arm64
/// has no variadic msgSend). Selectors are registered once; nothing here allocates per call.
/// </summary>
internal static unsafe class ObjC
{
    public static readonly nint Lib = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
    public static readonly nint MsgSend = NativeLibrary.GetExport(Lib, "objc_msgSend");
    static readonly delegate* unmanaged<byte*, nint> _getClass = (delegate* unmanaged<byte*, nint>)NativeLibrary.GetExport(Lib, "objc_getClass");
    static readonly delegate* unmanaged<byte*, nint> _sel = (delegate* unmanaged<byte*, nint>)NativeLibrary.GetExport(Lib, "sel_registerName");
    public static readonly delegate* unmanaged<nint> PoolPush = (delegate* unmanaged<nint>)NativeLibrary.GetExport(Lib, "objc_autoreleasePoolPush");
    public static readonly delegate* unmanaged<nint, void> PoolPop = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(Lib, "objc_autoreleasePoolPop");

    public static nint Class(string name) { var b = System.Text.Encoding.ASCII.GetBytes(name + "\0"); fixed (byte* p = b) return _getClass(p); }
    public static nint Sel(string name) { var b = System.Text.Encoding.ASCII.GetBytes(name + "\0"); fixed (byte* p = b) return _sel(p); }

    // the common shapes
    public static nint Send(nint o, nint s) => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(o, s);
    public static nint Send(nint o, nint s, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)MsgSend)(o, s, a);
    public static nint Send(nint o, nint s, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(o, s, a, b);
    public static nint Send(nint o, nint s, nint a, nint b, nint c) => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSend)(o, s, a, b, c);
    public static void SendV(nint o, nint s, nuint a) => ((delegate* unmanaged<nint, nint, nuint, void>)MsgSend)(o, s, a);
    public static void SendV(nint o, nint s, nint a) => ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(o, s, a);
    public static void SendB(nint o, nint s, bool a) => ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(o, s, a ? (byte)1 : (byte)0);
    public static void SendD(nint o, nint s, double a) => ((delegate* unmanaged<nint, nint, double, void>)MsgSend)(o, s, a);
    public static nuint SendU(nint o, nint s) => ((delegate* unmanaged<nint, nint, nuint>)MsgSend)(o, s);
    public static nint Idx(nint arr, nuint i) => ((delegate* unmanaged<nint, nint, nuint, nint>)MsgSend)(arr, S.objectAtIndexedSubscript, i);

    public static nint NSString(string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = b) return ((delegate* unmanaged<nint, nint, byte*, nint>)MsgSend)(Class("NSString"), Sel("stringWithUTF8String:"), p);
    }

    public static string Describe(nint nsObj)
    {
        if (nsObj == 0) return "(nil)";
        nint str = Send(nsObj, Sel("description"));
        return Marshal.PtrToStringUTF8(Send(str, Sel("UTF8String"))) ?? "";
    }

    /// <summary>Registered selectors, one field each, so the frame loop never registers a name.</summary>
    public static class S
    {
        public static readonly nint alloc = Sel("alloc"), init = Sel("init"), retain = Sel("retain"), release = Sel("release");
        public static readonly nint objectAtIndexedSubscript = Sel("objectAtIndexedSubscript:");
        public static readonly nint newCommandQueue = Sel("newCommandQueue"), commandBuffer = Sel("commandBuffer"), commit = Sel("commit"), status = Sel("status");
        public static readonly nint waitUntilCompleted = Sel("waitUntilCompleted"), name = Sel("name");
        public static readonly nint renderPassDescriptor = Sel("renderPassDescriptor"), colorAttachments = Sel("colorAttachments"), depthAttachment = Sel("depthAttachment");
        public static readonly nint setTexture = Sel("setTexture:"), setLoadAction = Sel("setLoadAction:"), setStoreAction = Sel("setStoreAction:");
        public static readonly nint setClearColor = Sel("setClearColor:"), setClearDepth = Sel("setClearDepth:");
        public static readonly nint renderCommandEncoderWithDescriptor = Sel("renderCommandEncoderWithDescriptor:"), blitCommandEncoder = Sel("blitCommandEncoder");
        public static readonly nint setRenderPipelineState = Sel("setRenderPipelineState:"), setDepthStencilState = Sel("setDepthStencilState:");
        public static readonly nint setVertexBuffer = Sel("setVertexBuffer:offset:atIndex:"), setVertexBytes = Sel("setVertexBytes:length:atIndex:");
        public static readonly nint setFragmentBytes = Sel("setFragmentBytes:length:atIndex:"), setFragmentBuffer = Sel("setFragmentBuffer:offset:atIndex:");
        public static readonly nint drawIndexed = Sel("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:");
        public static readonly nint endEncoding = Sel("endEncoding"), contents = Sel("contents");
        public static readonly nint copyFromTextureToBuffer = Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:");
        public static readonly nint copyFromTextureToTexture = Sel("copyFromTexture:toTexture:");
        public static readonly nint encodeSignalEvent = Sel("encodeSignalEvent:value:"), encodeWaitForEvent = Sel("encodeWaitForEvent:value:");
        public static readonly nint signaledValue = Sel("signaledValue"), width = Sel("width"), height = Sel("height");
    }
}

/// <summary>CoreFoundation + IOSurface, for creating a shareable texture's backing store.</summary>
internal static unsafe class IOSurf
{
    static readonly nint CF = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
    static readonly nint IOS = NativeLibrary.Load("/System/Library/Frameworks/IOSurface.framework/IOSurface");
    static readonly delegate* unmanaged<nint, nint, nint, nint, nint> _dictCreate = (delegate* unmanaged<nint, nint, nint, nint, nint>)NativeLibrary.GetExport(CF, "CFDictionaryCreateMutable");
    static readonly delegate* unmanaged<nint, nint, nint, void> _dictSet = (delegate* unmanaged<nint, nint, nint, void>)NativeLibrary.GetExport(CF, "CFDictionarySetValue");
    static readonly delegate* unmanaged<nint, nint, void*, nint> _num = (delegate* unmanaged<nint, nint, void*, nint>)NativeLibrary.GetExport(CF, "CFNumberCreate");
    public static readonly delegate* unmanaged<nint, void> CFRelease = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(CF, "CFRelease");
    static readonly delegate* unmanaged<nint, nint> _create = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(IOS, "IOSurfaceCreate");

    static nint Key(nint lib, string sym) => *(nint*)NativeLibrary.GetExport(lib, sym);

    /// <summary>A BGRA8 IOSurface. The caller owns it (CFRelease).</summary>
    public static nint CreateBgra(int w, int h)
    {
        nint d = _dictCreate(0, 0, NativeLibrary.GetExport(CF, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(CF, "kCFTypeDictionaryValueCallBacks"));
        void Set(string key, int v)
        {
            nint n = _num(0, 9 /* kCFNumberIntType */, &v);
            _dictSet(d, Key(IOS, key), n);
            CFRelease(n);
        }
        Set("kIOSurfaceWidth", w);
        Set("kIOSurfaceHeight", h);
        Set("kIOSurfaceBytesPerElement", 4);
        Set("kIOSurfacePixelFormat", 0x42475241 /* 'BGRA' */);
        nint s = _create(d);
        CFRelease(d);
        if (s == 0) throw new InvalidOperationException("IOSurfaceCreate failed");
        return s;
    }
}
