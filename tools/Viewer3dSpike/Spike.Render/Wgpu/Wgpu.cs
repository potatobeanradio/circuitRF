using System.Runtime.InteropServices;

namespace Viewer3dSpike.Render.Wgpu;

// Hand-written, minimal bindings to wgpu-native v29.0.1.1 (webgpu.h + wgpu.h from that release,
// 2026-06-23). Only what the spike calls. Every layout below was transcribed field by field from
// that header; there is no generator, which is itself a finding (the webgpu.h ABI moved under
// every earlier .NET binding — see the findings document, route B).

[StructLayout(LayoutKind.Sequential)] public unsafe struct SV { public byte* Data; public nuint Length; public static SV Null => new() { Data = null, Length = nuint.MaxValue }; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct Chained { public void* Next; public uint SType; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct CallbackInfo { public void* Next; public uint Mode; public void* Callback; public void* Ud1; public void* Ud2; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct ErrorCallbackInfo { public void* Next; public void* Callback; public void* Ud1; public void* Ud2; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct QueueDescriptor { public void* Next; public SV Label; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct DeviceDescriptor { public void* Next; public SV Label; public nuint RequiredFeatureCount; public void* RequiredFeatures; public void* RequiredLimits; public QueueDescriptor DefaultQueue; public CallbackInfo DeviceLost; public ErrorCallbackInfo UncapturedError; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct RequestAdapterOptions { public void* Next; public uint FeatureLevel; public uint PowerPreference; public uint ForceFallbackAdapter; public uint BackendType; public void* CompatibleSurface; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct BufferDescriptor { public void* Next; public SV Label; public ulong Usage; public ulong Size; public uint MappedAtCreation; }
[StructLayout(LayoutKind.Sequential)] public struct Extent3D { public uint Width, Height, Depth; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct TextureDescriptor { public void* Next; public SV Label; public ulong Usage; public uint Dimension; public Extent3D Size; public uint Format; public uint MipLevelCount; public uint SampleCount; public nuint ViewFormatCount; public void* ViewFormats; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct ShaderSourceWgsl { public Chained Chain; public SV Code; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct ShaderModuleDescriptor { public void* Next; public SV Label; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct VertexAttribute { public void* Next; public uint Format; public ulong Offset; public uint ShaderLocation; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct VertexBufferLayout { public void* Next; public uint StepMode; public ulong ArrayStride; public nuint AttributeCount; public VertexAttribute* Attributes; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct VertexState { public void* Next; public nint Module; public SV EntryPoint; public nuint ConstantCount; public void* Constants; public nuint BufferCount; public VertexBufferLayout* Buffers; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct PrimitiveState { public void* Next; public uint Topology; public uint StripIndexFormat; public uint FrontFace; public uint CullMode; public uint UnclippedDepth; }
[StructLayout(LayoutKind.Sequential)] public struct StencilFaceState { public uint Compare, FailOp, DepthFailOp, PassOp; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct DepthStencilState { public void* Next; public uint Format; public uint DepthWriteEnabled; public uint DepthCompare; public StencilFaceState Front, Back; public uint ReadMask, WriteMask; public int DepthBias; public float SlopeScale, Clamp; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct MultisampleState { public void* Next; public uint Count; public uint Mask; public uint AlphaToCoverage; }
[StructLayout(LayoutKind.Sequential)] public struct BlendComponent { public uint Operation, Src, Dst; }
[StructLayout(LayoutKind.Sequential)] public struct BlendState { public BlendComponent Color, Alpha; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct ColorTargetState { public void* Next; public uint Format; public BlendState* Blend; public ulong WriteMask; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct FragmentState { public void* Next; public nint Module; public SV EntryPoint; public nuint ConstantCount; public void* Constants; public nuint TargetCount; public ColorTargetState* Targets; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct RenderPipelineDescriptor { public void* Next; public SV Label; public nint Layout; public VertexState Vertex; public PrimitiveState Primitive; public DepthStencilState* DepthStencil; public MultisampleState Multisample; public FragmentState* Fragment; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct BindGroupEntry { public void* Next; public uint Binding; public nint Buffer; public ulong Offset; public ulong Size; public nint Sampler; public nint TextureView; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct BindGroupDescriptor { public void* Next; public SV Label; public nint Layout; public nuint EntryCount; public BindGroupEntry* Entries; }
[StructLayout(LayoutKind.Sequential)] public struct Color { public double R, G, B, A; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct ColorAttachment { public void* Next; public nint View; public uint DepthSlice; public nint ResolveTarget; public uint LoadOp; public uint StoreOp; public Color Clear; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct DepthAttachment { public void* Next; public nint View; public uint DepthLoadOp; public uint DepthStoreOp; public float DepthClear; public uint DepthReadOnly; public uint StencilLoadOp; public uint StencilStoreOp; public uint StencilClear; public uint StencilReadOnly; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct RenderPassDescriptor { public void* Next; public SV Label; public nuint ColorCount; public ColorAttachment* Colors; public DepthAttachment* Depth; public nint Occlusion; public void* Timestamps; }
[StructLayout(LayoutKind.Sequential)] public struct Origin3D { public uint X, Y, Z; }
[StructLayout(LayoutKind.Sequential)] public struct TexelCopyTextureInfo { public nint Texture; public uint Mip; public Origin3D Origin; public uint Aspect; }
[StructLayout(LayoutKind.Sequential)] public struct TexelCopyBufferLayout { public ulong Offset; public uint BytesPerRow; public uint RowsPerImage; }
[StructLayout(LayoutKind.Sequential)] public struct TexelCopyBufferInfo { public TexelCopyBufferLayout Layout; public nint Buffer; }
[StructLayout(LayoutKind.Sequential)] public unsafe struct AdapterInfo { public void* Next; public SV Vendor, Architecture, Device, Description; public uint BackendType, AdapterType, VendorId, DeviceId, SubgroupMin, SubgroupMax; }

public static class W
{
    public const uint CallbackAllowProcessEvents = 2, CallbackAllowSpontaneous = 3, STypeShaderSourceWgsl = 2;
    public const uint FmtBGRA8 = 0x1B, FmtRGBA8 = 0x16, FmtR32Uint = 0x0F, FmtDepth24Plus = 0x2E, FmtDepth32Float = 0x30;
    public const uint VFloat32x3 = 0x1E, VUint32 = 0x20, TriangleList = 4, IndexUint32 = 2, StepVertex = 1;
    public const uint CmpLess = 2, CmpLessEqual = 4, CmpAlways = 8, OptTrue = 1, OptFalse = 0;
    public const uint LoadClear = 2, LoadLoad = 1, StoreStore = 1, StoreDiscard = 2;
    public const uint BlendAdd = 1, FactorZero = 1, FactorOne = 2, FactorSrcAlpha = 5, FactorOneMinusSrcAlpha = 6;
    public const uint Tex2D = 2, FrontCCW = 1, CullNone = 1, AspectAll = 1, StencilKeep = 1, FeatureCore = 2, HighPerformance = 2;
    public const uint StatusSuccess = 1;
    public const ulong BufMapRead = 1, BufCopySrc = 4, BufCopyDst = 8, BufIndex = 0x10, BufVertex = 0x20, BufUniform = 0x40;
    public const ulong TexCopySrc = 1, TexCopyDst = 2, TexBinding = 4, TexRender = 0x10;
    public const ulong MapRead = 1, ColorWriteAll = 0xF;
    public const uint DepthSliceUndefined = uint.MaxValue;
}

/// <summary>Function pointers into libwgpu_native, resolved by name. No DllImport, so no
/// marshalling stubs and nothing allocated per call.</summary>
public sealed unsafe class WgpuApi
{
    public readonly nint Lib;
    public readonly string LibPath;
    public delegate* unmanaged<void*, nint> CreateInstance;
    public delegate* unmanaged<nint, RequestAdapterOptions*, CallbackInfo, ulong> InstanceRequestAdapter;
    public delegate* unmanaged<nint, void> InstanceProcessEvents;
    public delegate* unmanaged<nint, DeviceDescriptor*, CallbackInfo, ulong> AdapterRequestDevice;
    public delegate* unmanaged<nint, AdapterInfo*, uint> AdapterGetInfo;
    public delegate* unmanaged<nint, nint> DeviceGetQueue;
    public delegate* unmanaged<nint, BufferDescriptor*, nint> DeviceCreateBuffer;
    public delegate* unmanaged<nint, TextureDescriptor*, nint> DeviceCreateTexture;
    public delegate* unmanaged<nint, void*, nint> TextureCreateView;
    public delegate* unmanaged<nint, ShaderModuleDescriptor*, nint> DeviceCreateShaderModule;
    public delegate* unmanaged<nint, RenderPipelineDescriptor*, nint> DeviceCreateRenderPipeline;
    public delegate* unmanaged<nint, uint, nint> RenderPipelineGetBindGroupLayout;
    public delegate* unmanaged<nint, BindGroupDescriptor*, nint> DeviceCreateBindGroup;
    public delegate* unmanaged<nint, void*, nint> DeviceCreateCommandEncoder;
    public delegate* unmanaged<nint, RenderPassDescriptor*, nint> CommandEncoderBeginRenderPass;
    public delegate* unmanaged<nint, TexelCopyTextureInfo*, TexelCopyBufferInfo*, Extent3D*, void> CommandEncoderCopyTextureToBuffer;
    public delegate* unmanaged<nint, void*, nint> CommandEncoderFinish;
    public delegate* unmanaged<nint, nuint, nint*, void> QueueSubmit;
    public delegate* unmanaged<nint, nint, ulong, void*, nuint, void> QueueWriteBuffer;
    public delegate* unmanaged<nint, ulong, nuint, nuint, CallbackInfo, ulong> BufferMapAsync;
    public delegate* unmanaged<nint, nuint, nuint, void*> BufferGetConstMappedRange;
    public delegate* unmanaged<nint, void> BufferUnmap;
    public delegate* unmanaged<nint, nint, void> PassSetPipeline;
    public delegate* unmanaged<nint, uint, nint, nuint, uint*, void> PassSetBindGroup;
    public delegate* unmanaged<nint, uint, nint, ulong, ulong, void> PassSetVertexBuffer;
    public delegate* unmanaged<nint, nint, uint, ulong, ulong, void> PassSetIndexBuffer;
    public delegate* unmanaged<nint, uint, uint, uint, int, uint, void> PassDrawIndexed;
    public delegate* unmanaged<nint, void> PassEnd, PassRelease, EncoderRelease, CommandBufferRelease, BufferRelease, TextureRelease, TextureViewRelease;
    public delegate* unmanaged<nint, uint, void*, uint> DevicePoll;
    public delegate* unmanaged<uint> GetVersion;
    public delegate* unmanaged<nint, void*> DeviceGetNativeMetalDevice, QueueGetNativeMetalCommandQueue, TextureGetNativeMetalTexture;

    public WgpuApi()
    {
        LibPath = Locate();
        Lib = NativeLibrary.Load(LibPath);
        nint L(string n) => NativeLibrary.GetExport(Lib, n);
        CreateInstance = (delegate* unmanaged<void*, nint>)L("wgpuCreateInstance");
        InstanceRequestAdapter = (delegate* unmanaged<nint, RequestAdapterOptions*, CallbackInfo, ulong>)L("wgpuInstanceRequestAdapter");
        InstanceProcessEvents = (delegate* unmanaged<nint, void>)L("wgpuInstanceProcessEvents");
        AdapterRequestDevice = (delegate* unmanaged<nint, DeviceDescriptor*, CallbackInfo, ulong>)L("wgpuAdapterRequestDevice");
        AdapterGetInfo = (delegate* unmanaged<nint, AdapterInfo*, uint>)L("wgpuAdapterGetInfo");
        DeviceGetQueue = (delegate* unmanaged<nint, nint>)L("wgpuDeviceGetQueue");
        DeviceCreateBuffer = (delegate* unmanaged<nint, BufferDescriptor*, nint>)L("wgpuDeviceCreateBuffer");
        DeviceCreateTexture = (delegate* unmanaged<nint, TextureDescriptor*, nint>)L("wgpuDeviceCreateTexture");
        TextureCreateView = (delegate* unmanaged<nint, void*, nint>)L("wgpuTextureCreateView");
        DeviceCreateShaderModule = (delegate* unmanaged<nint, ShaderModuleDescriptor*, nint>)L("wgpuDeviceCreateShaderModule");
        DeviceCreateRenderPipeline = (delegate* unmanaged<nint, RenderPipelineDescriptor*, nint>)L("wgpuDeviceCreateRenderPipeline");
        RenderPipelineGetBindGroupLayout = (delegate* unmanaged<nint, uint, nint>)L("wgpuRenderPipelineGetBindGroupLayout");
        DeviceCreateBindGroup = (delegate* unmanaged<nint, BindGroupDescriptor*, nint>)L("wgpuDeviceCreateBindGroup");
        DeviceCreateCommandEncoder = (delegate* unmanaged<nint, void*, nint>)L("wgpuDeviceCreateCommandEncoder");
        CommandEncoderBeginRenderPass = (delegate* unmanaged<nint, RenderPassDescriptor*, nint>)L("wgpuCommandEncoderBeginRenderPass");
        CommandEncoderCopyTextureToBuffer = (delegate* unmanaged<nint, TexelCopyTextureInfo*, TexelCopyBufferInfo*, Extent3D*, void>)L("wgpuCommandEncoderCopyTextureToBuffer");
        CommandEncoderFinish = (delegate* unmanaged<nint, void*, nint>)L("wgpuCommandEncoderFinish");
        QueueSubmit = (delegate* unmanaged<nint, nuint, nint*, void>)L("wgpuQueueSubmit");
        QueueWriteBuffer = (delegate* unmanaged<nint, nint, ulong, void*, nuint, void>)L("wgpuQueueWriteBuffer");
        BufferMapAsync = (delegate* unmanaged<nint, ulong, nuint, nuint, CallbackInfo, ulong>)L("wgpuBufferMapAsync");
        BufferGetConstMappedRange = (delegate* unmanaged<nint, nuint, nuint, void*>)L("wgpuBufferGetConstMappedRange");
        BufferUnmap = (delegate* unmanaged<nint, void>)L("wgpuBufferUnmap");
        PassSetPipeline = (delegate* unmanaged<nint, nint, void>)L("wgpuRenderPassEncoderSetPipeline");
        PassSetBindGroup = (delegate* unmanaged<nint, uint, nint, nuint, uint*, void>)L("wgpuRenderPassEncoderSetBindGroup");
        PassSetVertexBuffer = (delegate* unmanaged<nint, uint, nint, ulong, ulong, void>)L("wgpuRenderPassEncoderSetVertexBuffer");
        PassSetIndexBuffer = (delegate* unmanaged<nint, nint, uint, ulong, ulong, void>)L("wgpuRenderPassEncoderSetIndexBuffer");
        PassDrawIndexed = (delegate* unmanaged<nint, uint, uint, uint, int, uint, void>)L("wgpuRenderPassEncoderDrawIndexed");
        PassEnd = (delegate* unmanaged<nint, void>)L("wgpuRenderPassEncoderEnd");
        PassRelease = (delegate* unmanaged<nint, void>)L("wgpuRenderPassEncoderRelease");
        EncoderRelease = (delegate* unmanaged<nint, void>)L("wgpuCommandEncoderRelease");
        CommandBufferRelease = (delegate* unmanaged<nint, void>)L("wgpuCommandBufferRelease");
        BufferRelease = (delegate* unmanaged<nint, void>)L("wgpuBufferRelease");
        TextureRelease = (delegate* unmanaged<nint, void>)L("wgpuTextureRelease");
        TextureViewRelease = (delegate* unmanaged<nint, void>)L("wgpuTextureViewRelease");
        DevicePoll = (delegate* unmanaged<nint, uint, void*, uint>)L("wgpuDevicePoll");
        GetVersion = (delegate* unmanaged<uint>)L("wgpuGetVersion");
        if (OperatingSystem.IsMacOS())
        {
            DeviceGetNativeMetalDevice = (delegate* unmanaged<nint, void*>)L("wgpuDeviceGetNativeMetalDevice");
            QueueGetNativeMetalCommandQueue = (delegate* unmanaged<nint, void*>)L("wgpuQueueGetNativeMetalCommandQueue");
            TextureGetNativeMetalTexture = (delegate* unmanaged<nint, void*>)L("wgpuTextureGetNativeMetalTexture");
        }
    }

    /// <summary>VIEWER3D_WGPU, else tools/Viewer3dSpike/native/&lt;rid&gt;/lib found by walking up
    /// from the executable (where fetch-wgpu.sh / .ps1 put it).</summary>
    static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("VIEWER3D_WGPU");
        if (!string.IsNullOrEmpty(env)) return env;
        string file = OperatingSystem.IsWindows() ? "wgpu_native.dll" : OperatingSystem.IsMacOS() ? "libwgpu_native.dylib" : "libwgpu_native.so";
        string rid = RuntimeInformation.RuntimeIdentifier;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "native", rid, "lib", file);
            if (File.Exists(p)) return p;
        }
        throw new DllNotFoundException($"{file} for {rid} not found under native/{rid}/lib — run fetch-wgpu.sh (or .ps1), or set VIEWER3D_WGPU");
    }

    public static string Str(SV s) => s.Data == null ? "" : System.Text.Encoding.UTF8.GetString(s.Data, (int)(s.Length == nuint.MaxValue ? (nuint)Strlen(s.Data) : s.Length));
    static int Strlen(byte* p) { int n = 0; while (p[n] != 0) n++; return n; }
}
